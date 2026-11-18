using System.Net;
using System.Text.Json;
using DeskMonitor;
using DeskMonitor.Core;

internal static class OpenAiUsageChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var json in new[]
        {
            "{}",
            """{"data":[{"results":[{"amount":{"currency":"usd"}}]}],"has_more":false}""",
            """{"data":[{"results":[{"amount":{"currency":"eur","value":1}}]}],"has_more":false}""",
            """{"data":[{"results":[{"amount":{"currency":"usd","value":null}}]}],"has_more":false}""",
            """{"data":[],"has_more":true,"next_page":" "}""",
            """{"data":[],"has_more":"false"}"""
        })
        {
            using var doc = JsonDocument.Parse(json);
            var rejected = false;
            try { ProviderUsageParser.OpenAiCostsPage(doc.RootElement); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "OpenAI incomplete costs, wrong currency and invalid pagination are rejected");
        }
        using (var empty = JsonDocument.Parse("""{"data":[],"has_more":false,"next_page":null}"""))
            check(ProviderUsageParser.OpenAiCostsPage(empty.RootElement) == (0m, null, 0), "OpenAI empty periods retain the absence of cost records");

        var defaults = ProviderUsageSettings.Defaults();
        var openAi = defaults.Single(x => x.Provider == UsageProvider.OpenAI) with { Enabled = true };
        var preferences = Preferences.Normalize(new() { Markets = [], ProviderUsages = defaults.Select(x => x.Provider == UsageProvider.OpenAI ? openAi : x).ToArray() });
        var restored = Preferences.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(preferences))!);
        check(restored.ProviderUsages.Length == 7 && restored.CardOrder.Contains(openAi.Key)
            && openAi.Kind == UsageAccountKind.Api && openAi.CredentialEnvironment == "OPENAI_ADMIN_KEY",
            "OpenAI API-only card coexists with the six existing settings and persists");

        var env = "DESKMONITOR_OPENAI_TEST_" + Guid.NewGuid().ToString("N");
        var settings = openAi with { CredentialEnvironment = env };
        var missingCalls = 0;
        using (var missing = new ProviderUsageClient(new Handler((_, _) =>
        {
            missingCalls++;
            throw new Exception("Missing credentials must not send a request.");
        })))
        {
            var rejected = false;
            try { await missing.ReadAsync(settings, CancellationToken.None); }
            catch (InvalidDataException ex) { rejected = ex.Message.Contains("Admin API Key"); }
            check(rejected && missingCalls == 0, "OpenAI missing credentials explain Admin API Key without a request");
        }
        Environment.SetEnvironmentVariable(env, "test-only-openai-key");
        try
        {
            var calls = 0;
            string? firstQuery = null;
            using var client = new ProviderUsageClient(new Handler((request, _) =>
            {
                calls++;
                var uri = request.RequestUri!;
                var query = uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2))
                    .ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
                var from = DateTimeOffset.FromUnixTimeSeconds(long.Parse(query["start_time"]));
                var to = DateTimeOffset.FromUnixTimeSeconds(long.Parse(query["end_time"]));
                check(request.Method == HttpMethod.Get && uri.Scheme == "https" && uri.Host == "api.openai.com"
                    && uri.AbsolutePath == "/v1/organization/costs" && request.Headers.Authorization?.Scheme == "Bearer"
                    && request.Headers.Authorization.Parameter == "test-only-openai-key", "OpenAI costs use official HTTPS GET and bearer authentication");
                check(from.Day == 1 && from.TimeOfDay == TimeSpan.Zero && from.Month == to.Month && from.Year == to.Year
                    && Math.Abs((DateTimeOffset.UtcNow - to).TotalSeconds) < 10 && query["limit"] == "31" && query["bucket_width"] == "1d",
                    "OpenAI costs cover the current UTC calendar month with seconds and daily buckets");
                if (calls == 1) firstQuery = uri.Query;
                else check(query["page"] == "cursor+/=" && uri.Query.StartsWith(firstQuery! + "&page="), "OpenAI paginated requests preserve the range and encode cursors");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(calls == 1
                    ? """{"data":[{"results":[{"amount":{"value":12.3456,"currency":"usd"}},{"amount":{"value":-0.25,"currency":"usd"}}]}],"has_more":true,"next_page":"cursor+/="}"""
                    : """{"data":[{"results":[{"amount":{"value":0.5,"currency":"usd"}}]}],"has_more":false,"next_page":null}""") });
            }));
            var result = await client.ReadAsync(settings, CancellationToken.None);
            check(calls == 2 && result.Metrics.Single().Value == "12.5956 USD" && result.Metrics[0].Label == "本月累计费用"
                && result.Scope.Contains("非剩余余额"), "OpenAI sums all USD pages including adjustments and labels costs separately from balance");

            foreach (var (body, expected) in new[]
            {
                ("""{"data":[{"results":[]}],"has_more":false}""", "暂无费用记录"),
                ("""{"data":[{"results":[{"amount":{"value":0,"currency":"usd"}}]}],"has_more":false}""", "0.00 USD"),
                ("""{"data":[{"results":[{"amount":{"value":0.0001,"currency":"usd"}}]}],"has_more":false}""", "0.0001 USD")
            })
            {
                using var amounts = new ProviderUsageClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(body) })));
                var snapshot = await amounts.ReadAsync(settings, CancellationToken.None);
                check(snapshot.Metrics.Single().Value == expected, "OpenAI distinguishes missing records, explicit zero and small nonzero costs: " + expected);
                if (expected == "暂无费用记录") check(snapshot.Scope.Contains("不代表没有使用"), "OpenAI absent cost records do not claim there was no usage");
            }

            var loopCalls = 0;
            using var loop = new ProviderUsageClient(new Handler((_, _) =>
            {
                loopCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[],"has_more":true,"next_page":"same"}""") });
            }));
            var loopRejected = false;
            try { await loop.ReadAsync(settings, CancellationToken.None); }
            catch (InvalidDataException) { loopRejected = true; }
            check(loopRejected && loopCalls == 2, "OpenAI repeated cursors do not return a partial total or loop");

            using var denied = new ProviderUsageClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                { Content = new StringContent("private-server-body") })));
            var deniedSafely = false;
            try { await denied.ReadAsync(settings, CancellationToken.None); }
            catch (HttpRequestException ex) { deniedSafely = !ex.Message.Contains("private-server-body") && !ex.Message.Contains("test-only-openai-key"); }
            check(deniedSafely, "OpenAI permission errors neither become zero costs nor expose response bodies or credentials");

            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            using var blocking = new ProviderUsageClient(new Handler(async (_, token) => { await Task.Delay(10000, token); return new(HttpStatusCode.OK); }));
            var cancelled = false;
            try { await blocking.ReadAsync(settings, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "OpenAI cancellation stops the cost request");
        }
        finally { Environment.SetEnvironmentVariable(env, null); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
