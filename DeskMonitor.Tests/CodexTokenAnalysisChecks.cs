using System.Text.Json;
using DeskMonitor.Core;

internal static class CodexTokenAnalysisChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "deskmonitor-token-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            static string Context(string model) => JsonSerializer.Serialize(new { type = "turn_context", payload = new { model } });
            static string Usage(string timestamp, object last, object total) => JsonSerializer.Serialize(new
            {
                timestamp,
                type = "event_msg",
                payload = new { type = "token_count", info = new { last_token_usage = last, total_token_usage = total } }
            });
            static object T(long input, long cached, long output, long reasoning = 0, long cacheWrite = 0) => new
            {
                input_tokens = input, cached_input_tokens = cached, cache_write_input_tokens = cacheWrite,
                output_tokens = output, reasoning_output_tokens = reasoning, total_tokens = input + output
            };
            var first = T(100, 40, 10, 4);
            var second = T(50, 20, 5, 2);
            var cumulative = T(150, 60, 15, 6);
            var luna = T(1000, 500, 100, 20);
            var unknown = T(10, 2, 3);
            var lines = new[]
            {
                Context("gpt-5.6-sol"),
                Usage("2026-09-20T01:00:00Z", first, first),
                Usage("2026-09-20T01:01:00Z", second, cumulative),
                Context("gpt-5.6-luna"),
                Usage("2026-09-20T02:00:00Z", luna, luna),
                Context("private-model"),
                Usage("2026-09-20T03:00:00Z", unknown, unknown),
                Usage("2026-09-19T15:59:59Z", unknown, unknown)
            };
            File.WriteAllLines(Path.Combine(directory, "rollout-a.jsonl"), lines);
            File.WriteAllLines(Path.Combine(directory, "rollout-copy.jsonl"), lines);
            var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.FromHours(8));
            var report = await CodexTokenAnalyzer.ReadAsync(CodexTokenRangeKind.Today, null, null,
                CancellationToken.None, directory, now);
            check(report.Tokens == new CodexTokenTotals(598, 562, 0, 118, 26), "Codex analyzer splits inclusive cached input and aggregates output once");
            check(report.Tokens.TotalTokens == 1278 && report.Models.Count == 3, "Codex analyzer attributes usage by turn model and deduplicates copied events");
            check(report.UnpricedTokens == 13 && Math.Abs(report.EstimatedCostUsd - 0.000914m) < 0.000000001m,
                "Codex analyzer estimates known models and identifies unknown pricing");
            check(report.Models.Single(x => x.Model == "gpt-5.6-sol").Tokens.TotalTokens == 165,
                "Codex analyzer uses last usage deltas rather than cumulative totals");
            var excluded = CodexTokenAnalyzer.ApplyCostExclusions(report, ["GPT-5.6-SOL", "private-model"]);
            check(excluded.Tokens == report.Tokens && excluded.Models.Single(x => x.Model == "gpt-5.6-sol").ExcludedFromCost
                && excluded.Models.Single(x => x.Model == "gpt-5.6-sol").EstimatedCostUsd == 0m
                && excluded.Models.Single(x => x.Model == "gpt-5.6-luna").EstimatedCostUsd == report.Models.Single(x => x.Model == "gpt-5.6-luna").EstimatedCostUsd
                && excluded.UnpricedTokens == 0 && excluded.EstimatedCostUsd < report.EstimatedCostUsd,
                "excluded models retain tokens but do not contribute to estimated cost or unpriced totals");
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            var sol = T(1000, 200, 100, cacheWrite: 100);
            var luna6 = T(1100, 210, 110, cacheWrite: 110);
            var terraLong = T(300_000, 100_000, 20_000, cacheWrite: 10_000);
            File.WriteAllLines(Path.Combine(directory, "pricing.jsonl"),
            [
                Context("gpt-6-sol"),
                Usage("2026-09-20T04:00:00Z", sol, sol),
                Context("gpt-6-luna"),
                Usage("2026-09-20T02:00:00Z", luna6, luna6),
                Context("gpt-5.6-terra"),
                Usage("2026-09-20T03:00:00Z", terraLong, terraLong)
            ]);
            var pricing = await CodexTokenAnalyzer.ReadAsync(CodexTokenRangeKind.Today, null, null,
                CancellationToken.None, directory, now);
            check(pricing.UnpricedTokens == 0 && pricing.Tokens.TotalTokens == 322_310
                && Math.Abs(pricing.Models.Single(x => x.Model == "gpt-6-sol").EstimatedCostUsd!.Value - .00269m) < .000000001m
                && Math.Abs(pricing.Models.Single(x => x.Model == "gpt-6-luna").EstimatedCostUsd!.Value - .00014885m) < .000000001m,
                "Codex analyzer prices GPT-6 Sol and Luna input, cache reads, cache writes and output");
            check(Math.Abs(pricing.Models.Single(x => x.Model == "gpt-5.6-terra").EstimatedCostUsd!.Value - 1.21m) < .000000001m,
                "Codex analyzer applies long-context pricing above 272K input tokens");
            var excludedOnRead = await CodexTokenAnalyzer.ReadAsync(CodexTokenRangeKind.Today, null, null,
                CancellationToken.None, directory, now, ["gpt-6-sol"]);
            check(excludedOnRead.Tokens == pricing.Tokens
                && excludedOnRead.Models.Single(x => x.Model == "gpt-6-sol").ExcludedFromCost
                && excludedOnRead.EstimatedCostUsd == pricing.EstimatedCostUsd - .00269m,
                "saved model exclusions are applied when a token report is read");
            var quota = new QuotaWindow(25, 300, now.AddHours(2));
            var bounds = CodexTokenAnalyzer.ResolveRange(CodexTokenRangeKind.ShortReset, quota, null, now);
            check(bounds.From == now.AddHours(-3) && bounds.Label.Contains("短周期"), "reset range starts at reset timestamp minus window duration");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
