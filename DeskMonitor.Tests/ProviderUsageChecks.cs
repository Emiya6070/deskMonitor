using System.Net;
using System.Text.Json;
using DeskMonitor.Core;

internal static class ProviderUsageChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var sample = JsonSerializer.Serialize(new { provider = "Claude", observedAt = now.AddHours(-1), windows = new[] { new { label = "5 小时", usedPercent = 25, resetsAt = now.AddHours(1) } } });
        var snapshot = ProviderUsageParser.Subscription(sample, UsageProvider.Claude, now);
        check(snapshot.Metrics[0].RemainingPercent == 75 && snapshot.ObservedAt < now.AddMinutes(-15), "subscription snapshots preserve observation age and convert used to remaining");
        void Reject(Action action, string label)
        {
            try { action(); } catch (InvalidDataException) { check(true, label); return; }
            check(false, label);
        }
        Reject(() => ProviderUsageParser.Subscription(sample, UsageProvider.Grok, now), "cross-provider subscription snapshots are rejected");
        Reject(() => ProviderUsageParser.Subscription(sample.Replace("25", "125"), UsageProvider.Claude, now), "invalid quota percentages are not silently clamped");
        Reject(() => ProviderUsageParser.Subscription("{}", UsageProvider.Claude, now), "missing quota data never becomes zero usage");
        using var truncated = JsonDocument.Parse("""{"limitReached":true,"timeSeries":[]}""");
        Reject(() => ProviderUsageParser.GrokCost(truncated.RootElement), "truncated xAI reports are rejected");
        using var cost = JsonDocument.Parse("""{"data":[{"results":[{"amount":"12345.67","currency":"USD"}]}],"has_more":false}""");
        check(ProviderUsageParser.ClaudePage(cost.RootElement).Total == 123.4567m, "Claude minor currency units retain decimal precision");
        using var grok = JsonDocument.Parse("""{"limitReached":false,"timeSeries":[{"dataPoints":[{"values":[0.75]},{"values":[1.25]}]}]}""");
        check(ProviderUsageParser.GrokCost(grok.RootElement) == 2, "xAI costs are already USD, not cents");
        var envName = "DESKMONITOR_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, "test-only-key");
        try
        {
            var calls = 0;
            using var client = new ProviderUsageClient(new Handler(async (request, token) =>
            {
                calls++;
                var body = await request.Content!.ReadAsStringAsync(token);
                check(request.RequestUri!.Host == "api.cursor.com" && request.Headers.Authorization?.Scheme == "Basic" && body.Contains($"\"page\":{calls}"), "Cursor authenticates and requests the next spending page");
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"teamMemberSpend":[{"overallSpendCents":1000,"spendCents":250}],"totalPages":2}""") };
            }));
            var result = await client.ReadAsync(new() { Provider = UsageProvider.Cursor, Kind = UsageAccountKind.Api, CredentialEnvironment = envName }, CancellationToken.None);
            check(calls == 2 && result.Metrics[0].Value == "20.00 USD" && result.Metrics[1].Value == "5.00 USD", "Cursor sums all pages without confusing included and on-demand spend");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            using var blocking = new ProviderUsageClient(new Handler(async (_, token) => { await Task.Delay(10000, token); return new(HttpStatusCode.OK); }));
            var stopped = false;
            try { await blocking.ReadAsync(new() { Provider = UsageProvider.Claude, Kind = UsageAccountKind.Api, CredentialEnvironment = envName }, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            check(stopped, "provider request cancellation propagates");
        }
        finally { Environment.SetEnvironmentVariable(envName, null); }
        var leap = DateProgress.Days(new(2024, 2, 15), DateProgressPeriod.Month);
        check(leap.Length == 29 && leap.Count(d => d.IsPast) == 14 && leap.Count(d => d.IsToday) == 1 && !leap[14].IsPast, "leap-month progress treats today as not yet passed");
        var week = DateProgress.Days(new(2025, 1, 1), DateProgressPeriod.Week);
        check(week[0].Date == new DateOnly(2024, 12, 30) && week[^1].Date == new DateOnly(2025, 1, 5), "week progress crosses years and starts on Monday");
        check(DateProgress.Days(new(2026, 9, 20), DateProgressPeriod.Week).Count(d => d.IsPast) == 6, "Sunday has six elapsed days");
        Reject(() => new DateProgressSettings { PastColor = "red" }.Validate(), "date progress rejects invalid configurable colors");
        var oldDate = JsonSerializer.Deserialize<DateProgressSettings>("""{"Enabled":true,"Period":1}""")!;
        check(oldDate.Placement == DateProgressPlacement.Left && oldDate.HorizontalLayout == DateProgressHorizontalLayout.Left && oldDate.ShowHeading,
            "old date settings retain left placement, left alignment and heading");
        var bottomDate = oldDate with { Placement = DateProgressPlacement.Bottom, HorizontalLayout = DateProgressHorizontalLayout.Stretch, ShowHeading = false };
        var restoredDate = JsonSerializer.Deserialize<DateProgressSettings>(JsonSerializer.Serialize(bottomDate))!;
        check(restoredDate.Placement == DateProgressPlacement.Bottom && restoredDate.HorizontalLayout == DateProgressHorizontalLayout.Stretch && !restoredDate.ShowHeading,
            "horizontal bottom date layout and heading visibility persist");
        Reject(() => new DateProgressSettings { Placement = (DateProgressPlacement)2 }.Validate(), "date progress rejects invalid placement");
        Reject(() => new DateProgressSettings { HorizontalLayout = (DateProgressHorizontalLayout)3 }.Validate(), "date progress rejects invalid horizontal layout");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
