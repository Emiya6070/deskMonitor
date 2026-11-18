using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;

namespace DeskMonitor.NotesService;

// Private, single-owner authorization server with one pre-registered ChatGPT client.
// Codes are short-lived and single-use; only hashed access/refresh tokens persist.
public sealed class NotesOAuth
{
    public const string ClientId = "deskmonitor-chatgpt";
    public const string RedirectUri = "https://chatgpt.com/connector_platform_oauth_redirect";
    private const string Scope = "notes offline_access";
    private readonly string _clientSecret;
    private readonly string _ownerKey;
    private readonly string _file;
    private readonly object _gate = new();
    private readonly Dictionary<string, CodeGrant> _codes = new();
    private TokenGrant[] _tokens;
    public string Issuer { get; }
    public string Resource => Issuer + "/mcp";
    public string Metadata => Issuer + "/.well-known/oauth-protected-resource";

    private sealed record Authorization(string ClientId, string RedirectUri, string Resource, string Challenge, string State, string Scope);
    private sealed record CodeGrant(Authorization Request, DateTimeOffset Expires);
    private sealed record TokenGrant(string Hash, string Kind, string ClientId, string Resource, string Scope, DateTimeOffset Expires);

    public NotesOAuth(string issuer, string clientSecret, string ownerKey, string stateFolder)
    {
        var uri = DeskMonitor.Core.NoteProtocol.ValidateUrl(issuer);
        if ((uri.Scheme != "https" && !uri.IsLoopback) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("NOTES_PUBLIC_URL 必须为 HTTPS 基础地址（本机测试允许 HTTP）。");
        if (!Regex.IsMatch(clientSecret, "^[A-Za-z0-9_-]{32,512}$")) throw new InvalidOperationException("NOTES_OAUTH_CLIENT_SECRET 必须为至少 32 字符的随机令牌。");
        Issuer = issuer.TrimEnd('/'); _clientSecret = clientSecret; _ownerKey = ownerKey;
        _file = Path.Combine(stateFolder, "oauth-tokens.json");
        _tokens = File.Exists(_file) ? JsonSerializer.Deserialize<TokenGrant[]>(File.ReadAllText(_file))
            ?? throw new InvalidDataException("OAuth 存储为空。") : [];
        if (_tokens.Any(t => t is null || t.Kind is not ("access" or "refresh") || !Regex.IsMatch(t.Hash, "^[A-F0-9]{64}$")
            || t.ClientId != ClientId || t.Resource != Resource || !ValidScope(t.Scope) || t.Expires == default))
            throw new InvalidDataException("OAuth 存储内容无效。");
    }
    public static bool EqualsSecret(string a, string b) => CryptographicOperations.FixedTimeEquals(HashBytes(a), HashBytes(b));
    private static byte[] HashBytes(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));
    private static string Hash(string text) => Convert.ToHexString(HashBytes(text));
    private static string RandomToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private static bool ValidScope(string scope)
    {
        var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return scopes.Contains("notes") && scopes.All(s => s is "notes" or "offline_access");
    }
    public bool AcceptsAccess(string header)
    {
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var hash = Hash(header[7..]);
        lock (_gate) return _tokens.Any(t => t.Kind == "access" && t.Hash == hash && t.Expires > DateTimeOffset.UtcNow && t.Resource == Resource && t.ClientId == ClientId && ValidScope(t.Scope));
    }
    public static bool IsPublicPath(PathString path) => path.Value is "/.well-known/oauth-protected-resource"
        or "/.well-known/oauth-authorization-server" or "/oauth/authorize" or "/oauth/token";

    public void Map(WebApplication app)
    {
        app.MapGet("/.well-known/oauth-protected-resource", () => Results.Json(new
        {
            resource = Resource, resource_name = "DeskMonitor 桌面便签", authorization_servers = new[] { Issuer },
            scopes_supported = new[] { "notes", "offline_access" }, bearer_methods_supported = new[] { "header" }
        }));
        app.MapGet("/.well-known/oauth-authorization-server", () => Results.Json(new
        {
            issuer = Issuer, authorization_endpoint = Issuer + "/oauth/authorize", token_endpoint = Issuer + "/oauth/token",
            authorization_response_iss_parameter_supported = true,
            response_types_supported = new[] { "code" }, grant_types_supported = new[] { "authorization_code", "refresh_token" },
            code_challenge_methods_supported = new[] { "S256" }, scopes_supported = new[] { "notes", "offline_access" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic" }
        }));
        app.MapGet("/oauth/authorize", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var request = Parse(context.Request.Query.ToDictionary(p => p.Key, p => p.Value.ToString()));
            var csrf = antiforgery.GetAndStoreTokens(context);
            context.Response.Headers.ContentSecurityPolicy = $"default-src 'none'; style-src 'unsafe-inline'; form-action 'self' {RedirectUri}; frame-ancestors 'none'; base-uri 'none'";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var values = new Dictionary<string, string>
            {
                [csrf.FormFieldName] = csrf.RequestToken!, ["client_id"] = request.ClientId, ["redirect_uri"] = request.RedirectUri,
                ["resource"] = request.Resource, ["code_challenge"] = request.Challenge, ["code_challenge_method"] = "S256",
                ["response_type"] = "code", ["state"] = request.State, ["scope"] = request.Scope
            };
            var hidden = string.Join("", values.Select(p => $"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(p.Key)}\" value=\"{WebUtility.HtmlEncode(p.Value)}\">"));
            return Results.Content($$"""
                <!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
                <title>连接桌面便签</title><style>body{font:16px/1.7 system-ui;max-width:560px;margin:64px auto;padding:24px;color:#eaf1eb;background:#142019}input,button{box-sizing:border-box;width:100%;padding:12px;margin-top:12px;font:inherit}button{background:#bee68e;border:0;border-radius:8px}small{color:#b0bfb2}</style>
                <h1>连接桌面便签</h1><p>授权 ChatGPT 读取和保存你的便签，包括重点与全文。此连接不提供删除工具。</p>
                <form method="post" action="{{WebUtility.HtmlEncode(Issuer)}}/oauth/authorize">{{hidden}}
                <label>便签访问令牌<input name="owner_key" type="password" autocomplete="off" required maxlength="512"></label>
                <small>请输入部署此便签服务时设置的 NOTES_API_TOKEN。令牌仅在本站验证。</small>
                <button type="submit">授权连接 ChatGPT</button></form><p>不想授权时，直接关闭此页。</p></html>
                """, "text/html; charset=utf-8");
        }).RequireRateLimiting("notes-oauth");
        app.MapPost("/oauth/authorize", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "invalid_request", error_description = "授权页已失效，请从 ChatGPT 重新连接。" }); }
            var form = await context.Request.ReadFormAsync();
            var request = Parse(form.ToDictionary(p => p.Key, p => p.Value.ToString()));
            if (!EqualsSecret(form["owner_key"].ToString(), _ownerKey)) return Results.Json(new { error = "access_denied", error_description = "访问令牌不正确，请返回授权页重新输入。" }, statusCode: 401);
            var code = RandomToken();
            lock (_gate)
            {
                foreach (var key in _codes.Where(p => p.Value.Expires <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) _codes.Remove(key);
                if (_codes.Count >= 100) return Results.StatusCode(429);
                _codes.Add(Hash(code), new(request, DateTimeOffset.UtcNow.AddMinutes(5)));
            }
            return Results.Redirect(QueryHelpers.AddQueryString(request.RedirectUri, new Dictionary<string, string?> { ["code"] = code, ["state"] = request.State, ["iss"] = Issuer }));
        }).RequireRateLimiting("notes-oauth");
        app.MapPost("/oauth/token", (Func<HttpContext, Task<IResult>>)Exchange).RequireRateLimiting("notes-oauth");
    }
    private Authorization Parse(Dictionary<string, string> values)
    {
        string Get(string name) => values.GetValueOrDefault(name) ?? "";
        var scope = Get("scope"); if (scope.Length == 0) scope = Scope;
        if (Get("client_id") != ClientId || Get("redirect_uri") != RedirectUri || Get("resource") != Resource
            || Get("response_type") != "code" || Get("code_challenge_method") != "S256"
            || !Regex.IsMatch(Get("code_challenge"), "^[A-Za-z0-9_-]{43}$") || Get("state").Length is < 1 or > 1024 || !ValidScope(scope))
            throw new InvalidDataException("OAuth 请求无效：请使用已登记客户端、正确资源地址和 S256 PKCE。");
        return new(ClientId, RedirectUri, Resource, Get("code_challenge"), Get("state"), scope);
    }
    private async Task<IResult> Exchange(HttpContext context)
    {
        if (!context.Request.HasFormContentType) return OAuthError("invalid_request");
        var form = await context.Request.ReadFormAsync();
        var clientId = form["client_id"].ToString(); var secret = form["client_secret"].ToString();
        var auth = context.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            if (secret.Length > 0) return OAuthError("invalid_request");
            try
            {
                var parts = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..])).Split(':', 2);
                if (parts.Length != 2) return OAuthError("invalid_client", 401);
                var basicId = WebUtility.UrlDecode(parts[0]);
                if (clientId.Length > 0 && clientId != basicId) return OAuthError("invalid_client", 401);
                clientId = basicId; secret = WebUtility.UrlDecode(parts[1]);
            }
            catch (FormatException) { return OAuthError("invalid_client", 401); }
        }
        if (clientId != ClientId || !EqualsSecret(secret, _clientSecret)) return OAuthError("invalid_client", 401);
        if (form["resource"] != Resource) return OAuthError("invalid_target");
        lock (_gate)
        {
            var type = form["grant_type"].ToString(); string scope; TokenGrant? previous = null;
            if (type == "authorization_code")
            {
                if (!_codes.Remove(Hash(form["code"].ToString()), out var code) || code.Expires <= DateTimeOffset.UtcNow) return OAuthError("invalid_grant");
                var verifier = form["code_verifier"].ToString();
                if (form["redirect_uri"] != code.Request.RedirectUri || !Regex.IsMatch(verifier, "^[A-Za-z0-9._~-]{43,128}$")
                    || !EqualsSecret(WebEncoders.Base64UrlEncode(HashBytes(verifier)), code.Request.Challenge)) return OAuthError("invalid_grant");
                scope = code.Request.Scope;
            }
            else if (type == "refresh_token")
            {
                previous = _tokens.SingleOrDefault(t => t.Hash == Hash(form["refresh_token"].ToString()) && t.Kind == "refresh" && t.Expires > DateTimeOffset.UtcNow && t.ClientId == clientId && t.Resource == Resource);
                if (previous is null) return OAuthError("invalid_grant");
                scope = previous.Scope;
                if (form["scope"].Count > 0 && form["scope"] != scope) return OAuthError("invalid_scope");
            }
            else return OAuthError("unsupported_grant_type");
            var access = RandomToken(); var refresh = RandomToken();
            var now = DateTimeOffset.UtcNow;
            var tokens = _tokens.Where(t => t.Expires > now && t != previous).Concat(new[]
            {
                new TokenGrant(Hash(access), "access", clientId, Resource, scope, now.AddHours(1)),
                new TokenGrant(Hash(refresh), "refresh", clientId, Resource, scope, now.AddDays(30))
            }).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(tokens)); File.Move(_file + ".tmp", _file, true); _tokens = tokens;
            return Results.Json(new { access_token = access, token_type = "Bearer", expires_in = 3600, refresh_token = refresh, scope });
        }
    }
    private static IResult OAuthError(string error, int status = 400) => Results.Json(new { error }, statusCode: status);
}
