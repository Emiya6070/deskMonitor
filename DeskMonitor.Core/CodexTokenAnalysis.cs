using System.Globalization;
using System.Text.Json;

namespace DeskMonitor.Core;

public enum CodexTokenRangeKind
{
    ShortReset,
    LongReset,
    Today,
    Last7Days,
    Last30Days
}

public sealed record CodexTokenTotals(
    long InputTokens = 0,
    long CachedInputTokens = 0,
    long CacheWriteTokens = 0,
    long OutputTokens = 0,
    long ReasoningTokens = 0)
{
    public long TotalTokens => InputTokens + CachedInputTokens + CacheWriteTokens + OutputTokens;
}

public sealed record CodexModelUsage(
    string Model,
    CodexTokenTotals Tokens,
    decimal? EstimatedCostUsd,
    long UnpricedTokens,
    bool ExcludedFromCost = false);

public sealed record CodexTokenReport(
    string RangeLabel,
    DateTimeOffset From,
    DateTimeOffset To,
    CodexTokenTotals Tokens,
    decimal EstimatedCostUsd,
    long UnpricedTokens,
    IReadOnlyList<CodexModelUsage> Models,
    int FilesRead,
    DateTimeOffset FetchedAt);

public static class CodexTokenAnalyzer
{
    private const long LongContextThreshold = 272_000;
    private sealed record Rates(decimal Input, decimal CachedInput, decimal CacheWrite, decimal Output, bool LongContext = false);
    private sealed record RawUsage(long Input, long CachedInput, long CacheWrite, long Output, long Reasoning, long Total);
    private sealed class DeltaState
    {
        public RawUsage? LastTotal;
        public List<RawUsage> Baselines { get; } = [];
    }
    private sealed class MutableUsage
    {
        public long Input, CachedInput, CacheWrite, Output, Reasoning, Unpriced;
        public decimal Cost;
        public void Add(CodexTokenTotals value)
        {
            Input += value.InputTokens; CachedInput += value.CachedInputTokens;
            CacheWrite += value.CacheWriteTokens; Output += value.OutputTokens; Reasoning += value.ReasoningTokens;
        }
        public CodexTokenTotals Freeze() => new(Input, CachedInput, CacheWrite, Output, Reasoning);
    }

    public static async Task<CodexTokenReport> ReadAsync(
        CodexTokenRangeKind range,
        QuotaWindow? primary,
        QuotaWindow? secondary,
        CancellationToken cancellationToken,
        string? sessionsDirectory = null,
        DateTimeOffset? nowOverride = null,
        IReadOnlyCollection<string>? excludedBillingModels = null)
    {
        var now = nowOverride ?? DateTimeOffset.Now;
        var (from, label) = ResolveRange(range, primary, secondary, now);
        var root = sessionsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        var rows = new Dictionary<string, MutableUsage>(StringComparer.OrdinalIgnoreCase);
        var seenEvents = new HashSet<string>(StringComparer.Ordinal);
        var filesRead = 0;

        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCandidate(file, from)) continue;
                try
                {
                    await ReadFileAsync(file, from, now, rows, seenEvents, cancellationToken);
                    filesRead++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        var models = rows.Select(pair =>
        {
            var tokens = pair.Value.Freeze();
            return new CodexModelUsage(pair.Key, tokens,
                pair.Value.Unpriced == 0 ? pair.Value.Cost : null, pair.Value.Unpriced);
        }).OrderByDescending(x => x.Tokens.TotalTokens).ThenBy(x => x.Model, StringComparer.OrdinalIgnoreCase).ToArray();
        var total = new MutableUsage();
        foreach (var row in rows.Values)
        {
            total.Add(row.Freeze()); total.Cost += row.Cost; total.Unpriced += row.Unpriced;
        }
        return ApplyCostExclusions(new(label, from, now, total.Freeze(), total.Cost, total.Unpriced,
            models, filesRead, DateTimeOffset.UtcNow), excludedBillingModels);
    }

    public static CodexTokenReport ApplyCostExclusions(CodexTokenReport report, IReadOnlyCollection<string>? excludedBillingModels)
    {
        if (excludedBillingModels is null || excludedBillingModels.Count == 0) return report;
        var excluded = new HashSet<string>(excludedBillingModels, StringComparer.OrdinalIgnoreCase);
        var models = report.Models.Select(model => excluded.Contains(model.Model)
            ? model with { EstimatedCostUsd = 0m, UnpricedTokens = 0, ExcludedFromCost = true }
            : model).ToArray();
        return report with
        {
            Models = models,
            EstimatedCostUsd = models.Sum(model => model.EstimatedCostUsd ?? 0m),
            UnpricedTokens = models.Sum(model => model.UnpricedTokens)
        };
    }

    private static bool IsCandidate(string file, DateTimeOffset from)
    {
        var dayDirectory = Directory.GetParent(file);
        var monthDirectory = dayDirectory?.Parent;
        var yearDirectory = monthDirectory?.Parent;
        if (dayDirectory is null || monthDirectory is null || yearDirectory is null
            || !int.TryParse(yearDirectory.Name, out var year) || !int.TryParse(monthDirectory.Name, out var month)
            || !int.TryParse(dayDirectory.Name, out var day)) return true;
        DateTime pathDay;
        try { pathDay = new DateTime(year, month, day); }
        catch (ArgumentOutOfRangeException) { return true; }
        if (pathDay >= from.ToLocalTime().Date.AddDays(-1)) return true;
        try { return File.GetLastWriteTimeUtc(file) >= from.UtcDateTime; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static (DateTimeOffset From, string Label) ResolveRange(
        CodexTokenRangeKind range, QuotaWindow? primary, QuotaWindow? secondary, DateTimeOffset now)
    {
        var windows = new[] { primary, secondary }.Where(x => x?.DurationMinutes is > 0 && x.ResetsAt is not null)
            .Cast<QuotaWindow>().OrderBy(x => x.DurationMinutes).ToArray();
        if (range is CodexTokenRangeKind.ShortReset or CodexTokenRangeKind.LongReset && windows.Length > 0)
        {
            var window = range == CodexTokenRangeKind.ShortReset ? windows[0] : windows[^1];
            var from = window.ResetsAt!.Value.AddMinutes(-window.DurationMinutes!.Value);
            return (from, range == CodexTokenRangeKind.ShortReset ? "短周期重置后" : "长周期重置后");
        }
        var localNow = now.ToLocalTime();
        var today = new DateTimeOffset(localNow.Date, TimeZoneInfo.Local.GetUtcOffset(localNow.Date));
        return range switch
        {
            CodexTokenRangeKind.Last7Days => (today.AddDays(-6), "最近 7 个自然日"),
            CodexTokenRangeKind.Last30Days => (today.AddDays(-29), "最近 30 个自然日"),
            CodexTokenRangeKind.ShortReset => (today, "今天（未取得短周期重置点）"),
            CodexTokenRangeKind.LongReset => (today, "今天（未取得长周期重置点）"),
            _ => (today, "今天")
        };
    }

    private static async Task ReadFileAsync(string path, DateTimeOffset from, DateTimeOffset to,
        Dictionary<string, MutableUsage> rows, HashSet<string> seenEvents, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        var model = "unknown";
        var state = new DeltaState();
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (!line.Contains("token_count", StringComparison.Ordinal)
                && !line.Contains("turn_context", StringComparison.Ordinal)) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "turn_context"
                    && root.TryGetProperty("payload", out var context) && context.ValueKind == JsonValueKind.Object
                    && context.TryGetProperty("model", out var modelValue) && modelValue.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(modelValue.GetString()))
                {
                    model = modelValue.GetString()!.Trim();
                    continue;
                }
                if (!TryGetTokenInfo(root, out var info) || !TryReadTimestamp(root, out var timestamp)) continue;
                var last = TryReadUsage(info, "last_token_usage");
                var cumulative = TryReadUsage(info, "total_token_usage");
                var delta = ConsumeDelta(state, last, cumulative);
                if (delta is null || timestamp < from || timestamp > to) continue;
                var eventKey = timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "|" + Signature(last) + "|" + Signature(cumulative);
                if (!seenEvents.Add(eventKey)) continue;
                Add(rows, model, delta);
            }
        }
    }

    private static bool TryGetTokenInfo(JsonElement root, out JsonElement info)
    {
        info = default;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "event_msg"
            || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return false;
        var token = payload;
        if (!payload.TryGetProperty("type", out var payloadType) || payloadType.GetString() != "token_count")
        {
            if (!payload.TryGetProperty("msg", out token) || token.ValueKind != JsonValueKind.Object
                || !token.TryGetProperty("type", out var messageType) || messageType.GetString() != "token_count") return false;
        }
        return token.TryGetProperty("info", out info) && info.ValueKind == JsonValueKind.Object;
    }

    private static bool TryReadTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return root.TryGetProperty("timestamp", out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
    }

    private static RawUsage? TryReadUsage(JsonElement info, string name)
    {
        if (!info.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) return null;
        static long Read(JsonElement element, string property, string? alternate = null)
        {
            if (!element.TryGetProperty(property, out var number) && alternate is not null)
                element.TryGetProperty(alternate, out number);
            return number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out var result) && result >= 0 ? result : 0;
        }
        return new(Read(value, "input_tokens"), Read(value, "cached_input_tokens"),
            Read(value, "cache_creation_input_tokens", "cache_write_input_tokens"),
            Read(value, "output_tokens"), Read(value, "reasoning_output_tokens"), Read(value, "total_tokens"));
    }

    private static RawUsage? ConsumeDelta(DeltaState state, RawUsage? last, RawUsage? total)
    {
        if (total is null) return last;
        var duplicate = state.Baselines.FindIndex(x => x == total);
        if (duplicate >= 0) { Touch(state, duplicate, total); return null; }
        if (last is not null)
        {
            var previous = Subtract(total, last);
            var lineage = previous is null ? -1 : state.Baselines.FindIndex(x => x == previous);
            if (lineage >= 0) { Touch(state, lineage, total); return last; }
            if (previous is not null) { Touch(state, -1, total); return last; }
        }
        if (state.LastTotal is { } active && total.Total >= active.Total)
        {
            var delta = Subtract(total, active);
            if (delta is not null && (last is null || delta.Total <= last.Total))
            { Touch(state, state.Baselines.FindIndex(x => x == active), total); return delta; }
        }
        Touch(state, -1, total);
        return last ?? total;
    }

    private static RawUsage? Subtract(RawUsage total, RawUsage part)
    {
        if (total.Input < part.Input || total.CachedInput < part.CachedInput || total.CacheWrite < part.CacheWrite
            || total.Output < part.Output || total.Reasoning < part.Reasoning || total.Total < part.Total) return null;
        return new(total.Input - part.Input, total.CachedInput - part.CachedInput, total.CacheWrite - part.CacheWrite,
            total.Output - part.Output, total.Reasoning - part.Reasoning, total.Total - part.Total);
    }

    private static void Touch(DeltaState state, int index, RawUsage usage)
    {
        if (index >= 0) state.Baselines.RemoveAt(index);
        else state.Baselines.RemoveAll(x => x == usage);
        state.Baselines.Add(usage);
        while (state.Baselines.Count > 32) state.Baselines.RemoveAt(0);
        state.LastTotal = usage;
    }

    private static string Signature(RawUsage? value) => value is null ? "" : string.Join(':',
        value.Input, value.CachedInput, value.CacheWrite, value.Output, value.Reasoning, value.Total);

    private static void Add(Dictionary<string, MutableUsage> rows, string model, RawUsage raw)
    {
        var cachedInput = Math.Min(raw.CachedInput, raw.Input);
        var cacheWrite = Math.Min(raw.CacheWrite, raw.Input - cachedInput);
        var normalized = new CodexTokenTotals(raw.Input - cachedInput - cacheWrite, cachedInput,
            cacheWrite, raw.Output, raw.Reasoning);
        if (normalized.TotalTokens <= 0) return;
        if (!rows.TryGetValue(model, out var row)) rows[model] = row = new();
        row.Add(normalized);
        var rates = FindRates(model);
        if (rates is null) { row.Unpriced += normalized.TotalTokens; return; }
        var inputMultiplier = rates.LongContext && raw.Input > LongContextThreshold ? 2m : 1m;
        var outputMultiplier = rates.LongContext && raw.Input > LongContextThreshold ? 1.5m : 1m;
        row.Cost += (normalized.InputTokens * rates.Input * inputMultiplier
            + normalized.CachedInputTokens * rates.CachedInput * inputMultiplier
            + normalized.CacheWriteTokens * rates.CacheWrite * inputMultiplier
            + normalized.OutputTokens * rates.Output * outputMultiplier) / 1_000_000m;
    }

    private static Rates? FindRates(string model)
    {
        var value = model.ToLowerInvariant();
        if (value.Contains("gpt-6-astra")) return new(10m, 1m, 12.5m, 50m, true);
        if (value.Contains("gpt-6-sol")) return new(2m, .2m, 2.5m, 10m, true);
        if (value.Contains("gpt-6-luna")) return new(.1m, .01m, .125m, .5m, true);
        if (value.Contains("gpt-5.6-sol") || value == "gpt-5.6") return new(4m, .4m, 5m, 20m, true);
        if (value.Contains("gpt-5.6-terra")) return new(2m, .2m, 2.5m, 12m, true);
        if (value.Contains("gpt-5.6-luna")) return new(.2m, .02m, .25m, 1.2m, true);
        if (value.Contains("gpt-5.5")) return new(5m, .5m, 0m, 30m, true);
        if (value.Contains("gpt-5.4")) return new(2.5m, .25m, 0m, 15m, true);
        if (value.Contains("gpt-5.3-codex")) return new(1.75m, .175m, 0m, 14m);
        return null;
    }
}
