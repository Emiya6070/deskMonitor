using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskMonitor.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class NotesMcpChecks
{
    internal sealed record Tokens(string Access, string Refresh);
    private const string Callback = "https://chatgpt.com/connector_platform_oauth_redirect";
    private static string UrlEncode(string value) => Uri.EscapeDataString(value);
    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static async Task<Tokens> RunAsync(string address, string owner, string secret, Action<bool, string> check)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(20) };
        using (var denied = await http.PostAsJsonAsync("mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" }))
            check(denied.StatusCode == HttpStatusCode.Unauthorized && denied.Headers.WwwAuthenticate.ToString().Contains("resource_metadata"), "MCP: unauthenticated requests advertise OAuth discovery");
        var metadata = await http.GetFromJsonAsync<JsonElement>(".well-known/oauth-protected-resource");
        check(metadata.GetProperty("resource").GetString() == address + "/mcp", "OAuth: resource metadata binds the canonical MCP endpoint");
        var server = await http.GetFromJsonAsync<JsonElement>(".well-known/oauth-authorization-server");
        check(server.GetProperty("issuer").GetString() == address && server.GetProperty("code_challenge_methods_supported")[0].GetString() == "S256", "OAuth: issuer and S256 metadata available without desktop token");
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        var authParams = new Dictionary<string, string>
        {
            ["client_id"] = "deskmonitor-chatgpt", ["redirect_uri"] = Callback, ["resource"] = address + "/mcp", ["response_type"] = "code",
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["state"] = "test-state", ["scope"] = "notes offline_access"
        };
        string Query(Dictionary<string, string> values) => string.Join("&", values.Select(p => UrlEncode(p.Key) + "=" + UrlEncode(p.Value)));
        using (var badRedirect = await http.GetAsync("oauth/authorize?" + Query(new(authParams) { ["redirect_uri"] = "https://example.com/steal" })))
            check(badRedirect.StatusCode == HttpStatusCode.BadRequest && badRedirect.Headers.Location is null, "OAuth: unregistered redirects rejected without redirecting credentials");
        var page = await http.GetStringAsync("oauth/authorize?" + Query(authParams));
        var fields = Regex.Matches(page, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">")
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups[1].Value), m => WebUtility.HtmlDecode(m.Groups[2].Value));
        check(fields.ContainsKey("__RequestVerificationToken") && !page.Contains(owner), "OAuth: authorization page includes CSRF protection and no secret");
        using (var noCsrf = await http.PostAsync("oauth/authorize", new FormUrlEncodedContent(new Dictionary<string, string>(authParams) { ["owner_key"] = owner })))
            check(noCsrf.StatusCode == HttpStatusCode.BadRequest, "OAuth: authorization without CSRF proof is rejected");
        fields["owner_key"] = "incorrect";
        using (var wrongOwner = await http.PostAsync("oauth/authorize", new FormUrlEncodedContent(fields)))
            check(wrongOwner.StatusCode == HttpStatusCode.Unauthorized, "OAuth: knowing the MCP URL does not grant access");
        fields["owner_key"] = owner;
        using var approval = await http.PostAsync("oauth/authorize", new FormUrlEncodedContent(fields));
        check(approval.StatusCode == HttpStatusCode.Redirect, "OAuth: explicit owner approval produces an authorization code");
        var query = ParseQuery(approval.Headers.Location!.Query);
        check(query["state"] == "test-state" && query["iss"] == address, "OAuth: callback preserves state and includes exact issuer");
        var exchange = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = "deskmonitor-chatgpt", ["client_secret"] = secret,
            ["redirect_uri"] = Callback, ["resource"] = address + "/mcp", ["code"] = query["code"], ["code_verifier"] = verifier
        };
        using (var wrongClient = await http.PostAsync("oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>(exchange) { ["client_secret"] = "wrong" })))
            check(wrongClient.StatusCode == HttpStatusCode.Unauthorized, "OAuth: token exchange requires the registered client secret");
        using (var wrongResource = await http.PostAsync("oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>(exchange) { ["resource"] = address + "/other" })))
            check(wrongResource.StatusCode == HttpStatusCode.BadRequest, "OAuth: tokens cannot be minted for another resource");
        using var issued = await http.PostAsync("oauth/token", new FormUrlEncodedContent(exchange));
        issued.EnsureSuccessStatusCode();
        var tokenJson = await issued.Content.ReadFromJsonAsync<JsonElement>();
        var tokens = new Tokens(tokenJson.GetProperty("access_token").GetString()!, tokenJson.GetProperty("refresh_token").GetString()!);
        check(tokens.Access != owner && tokens.Refresh != tokens.Access, "OAuth: scoped access/refresh tokens differ from desktop credential");
        using (var replay = await http.PostAsync("oauth/token", new FormUrlEncodedContent(exchange)))
            check(replay.StatusCode == HttpStatusCode.BadRequest, "OAuth: authorization code replay rejected");
        // A fresh code with a wrong PKCE verifier must not mint a token.
        using var nextApproval = await http.PostAsync("oauth/authorize", new FormUrlEncodedContent(fields));
        exchange["code"] = ParseQuery(nextApproval.Headers.Location!.Query)["code"];
        exchange["code_verifier"] = new string('x', 43);
        using (var wrongPkce = await http.PostAsync("oauth/token", new FormUrlEncodedContent(exchange)))
            check(wrongPkce.StatusCode == HttpStatusCode.BadRequest, "OAuth: mismatched PKCE proof rejected");

        using var origin = new HttpRequestMessage(HttpMethod.Post, "mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }) };
        origin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Access);
        origin.Headers.Add("Origin", "https://example.com");
        using (var deniedOrigin = await http.SendAsync(origin)) check(deniedOrigin.StatusCode == HttpStatusCode.Forbidden, "MCP: untrusted browser origins rejected");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Access);
        using (var rest = await http.GetAsync("notes")) check(rest.StatusCode == HttpStatusCode.Unauthorized, "OAuth: MCP token cannot use desktop REST delete/read API");

        var id = "mcp-check-" + Guid.NewGuid().ToString("N");
        using var desktop = new NoteServiceClient(address, owner);
        await using (var client = await Connect(address, tokens.Access))
        {
            var tools = await client.ListToolsAsync();
            check(tools.Select(t => t.Name).Order().SequenceEqual(new[] { "get_note", "list_notes", "save_note" }), "MCP: official client discovers exactly the three note tools");
            var write = await client.CallToolAsync("save_note", new Dictionary<string, object?> { ["id"] = id, ["title"] = "MCP 测试", ["summary"] = "独立重点", ["content"] = "完整分析内容", ["expectedRevision"] = 0 });
            check(write.IsError != true && (await desktop.ListAsync()).Any(n => n.Id == id && n.Summary == "独立重点" && n.Content == "完整分析内容"), "MCP: tool write is immediately readable by desktop REST client");
            var read = await client.CallToolAsync("get_note", new Dictionary<string, object?> { ["id"] = id });
            check(read.Content.OfType<TextContentBlock>().Any(t => t.Text.Contains("content")), "MCP: get_note returns full continuation context");
            var listing = await client.CallToolAsync("list_notes");
            var summaries = JsonDocument.Parse(listing.Content.OfType<TextContentBlock>().Single().Text);
            check(!summaries.RootElement.EnumerateArray().Single(n => n.GetProperty("id").GetString() == id).TryGetProperty("content", out _), "MCP: list_notes returns summaries without unrelated full text");
            var conflict = await client.CallToolAsync("save_note", new Dictionary<string, object?> { ["id"] = id, ["title"] = "旧写入", ["summary"] = "不应覆盖", ["content"] = "不应覆盖", ["expectedRevision"] = 0 });
            check(conflict.IsError == true && (await desktop.ListAsync()).Single(n => n.Id == id).Revision == 1, "MCP: version conflict is a tool error and preserves current note");
            var update = await client.CallToolAsync("save_note", new Dictionary<string, object?> { ["id"] = id, ["title"] = "MCP 测试", ["summary"] = "继续分析后的重点", ["content"] = "继续后的完整分析", ["expectedRevision"] = 1 });
            check(update.IsError != true && (await desktop.ListAsync()).Single(n => n.Id == id).Revision == 2, "MCP: continuation updates the same desktop note");
        }
        await using (var legacy = await Connect(address, tokens.Access, "2025-11-25"))
            check((await legacy.ListToolsAsync()).Count == 3, "MCP: previous protocol initialize clients also connect");
        await desktop.DeleteAsync((await desktop.ListAsync()).Single(n => n.Id == id));
        return tokens;
    }
    public static async Task VerifyRefreshAsync(string address, string secret, Tokens tokens, Action<bool, string> check)
    {
        using var http = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/") };
        var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = "deskmonitor-chatgpt", ["client_secret"] = secret, ["resource"] = address + "/mcp", ["refresh_token"] = tokens.Refresh };
        using var response = await http.PostAsync("oauth/token", new FormUrlEncodedContent(form)); response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        await using var client = await Connect(address, json.GetProperty("access_token").GetString()!);
        check((await client.ListToolsAsync()).Count == 3, "OAuth: stored refresh token issues usable access");
        using var replay = await http.PostAsync("oauth/token", new FormUrlEncodedContent(form));
        check(replay.StatusCode == HttpStatusCode.BadRequest, "OAuth: rotated refresh token cannot be replayed");
    }
    private static Task<McpClient> Connect(string address, string access, string? version = null) => McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = new Uri(address + "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
        AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + access }
    }), version is null ? null : new McpClientOptions { ProtocolVersion = version });

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&')
        .Select(s => s.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
}
