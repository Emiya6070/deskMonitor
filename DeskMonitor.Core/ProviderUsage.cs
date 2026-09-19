using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskMonitor.Core;

public enum UsageProvider { Cursor, Claude, Grok }
public enum UsageAccountKind { Subscription, Api }
public sealed record ProviderUsageSettings
{
    public UsageProvider Provider { get; init; }
    public UsageAccountKind Kind { get; init; }
    public bool Enabled { get; init; }
    public string CredentialEnvironment { get; init; } = "";
    public string TeamId { get; init; } = "";
    public string SnapshotPath { get; init; } = "";
    [JsonIgnore] public string Key => $"usage:{Provider}:{Kind}";
    [JsonIgnore] public string Title => $"{Provider} · {(Kind == UsageAccountKind.Subscription ? "个人订阅" : Provider == UsageProvider.Cursor ? "团队费用（Admin API）" : "API 费用")}";
    public void Validate()
    {
        if (!Enum.IsDefined(Provider) || !Enum.IsDefined(Kind) || CredentialEnvironment is null || TeamId is null || SnapshotPath is null
            || CredentialEnvironment.Length > 128 || CredentialEnvironment.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')
            || TeamId.Length > 128 || TeamId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
            || SnapshotPath.Length > 1024 || SnapshotPath.Any(char.IsControl)
            || (SnapshotPath.Length > 0 && !Path.IsPathFullyQualified(SnapshotPath)))
            throw new InvalidDataException("用量配置无效：请使用环境变量名及快照文件绝对路径。");
    }
    public static ProviderUsageSettings[] Defaults() => Enum.GetValues<UsageProvider>().SelectMany(provider => Enum.GetValues<UsageAccountKind>().Select(kind => new ProviderUsageSettings
    {
        Provider = provider, Kind = kind, CredentialEnvironment = provider switch
        { UsageProvider.Cursor => "CURSOR_ADMIN_API_KEY", UsageProvider.Claude => "ANTHROPIC_ADMIN_KEY", _ => "XAI_MANAGEMENT_KEY" }
    })).ToArray();
}
public sealed record UsageMetric(string Label, string Value, double? RemainingPercent = null, DateTimeOffset? ResetsAt = null);
public sealed record ProviderUsageSnapshot(UsageMetric[] Metrics, DateTimeOffset ObservedAt, string Scope);

public static class ProviderUsageParser
{
    public static JsonElement Field(JsonElement root, string name, JsonValueKind kind)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new InvalidDataException($"用量响应缺少有效的 {name}。");
        return value;
    }
    public static decimal Number(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        throw new InvalidDataException("用量数值无效。");
    }
    public static string Usd(decimal amount) => amount.ToString("0.00##", CultureInfo.InvariantCulture) + " USD";
    public static ProviderUsageSnapshot Subscription(string json, UsageProvider provider, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!Enum.TryParse<UsageProvider>(Field(root, "provider", JsonValueKind.String).GetString(), true, out var actual) || actual != provider)
            throw new InvalidDataException("快照所属服务与卡片不匹配。");
        if (!DateTimeOffset.TryParse(Field(root, "observedAt", JsonValueKind.String).GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) || at > now.AddMinutes(1))
            throw new InvalidDataException("快照时间无效。");
        var windows = Field(root, "windows", JsonValueKind.Array);
        if (windows.GetArrayLength() is < 1 or > 8) throw new InvalidDataException("快照需要 1–8 个额度窗口。");
        var metrics = windows.EnumerateArray().Select(w =>
        {
            var label = Field(w, "label", JsonValueKind.String).GetString()!;
            if (string.IsNullOrWhiteSpace(label) || label.Length > 60) throw new InvalidDataException("额度窗口名称无效。");
            var used = Number(Field(w, "usedPercent", JsonValueKind.Number));
            if (used < 0 || used > 100) throw new InvalidDataException("已用百分比须在 0–100 之间。");
            DateTimeOffset? reset = null;
            if (w.TryGetProperty("resetsAt", out var r) && r.ValueKind != JsonValueKind.Null)
            {
                if (r.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) throw new InvalidDataException("重置时间无效。");
                reset = time;
            }
            return new UsageMetric(label, $"剩余 {100 - used:0.#}%", (double)(100 - used), reset);
        }).ToArray();
        return new(metrics, at, "个人订阅 · 本地快照（非 API 账单）");
    }
    public static (decimal Total, decimal OnDemand, int Pages) CursorPage(JsonElement root)
    {
        var rows = Field(root, "teamMemberSpend", JsonValueKind.Array);
        decimal total = 0, onDemand = 0;
        foreach (var row in rows.EnumerateArray())
        {
            total += Number(Field(row, "overallSpendCents", JsonValueKind.Number)) / 100;
            onDemand += Number(Field(row, "spendCents", JsonValueKind.Number)) / 100;
        }
        if (!Field(root, "totalPages", JsonValueKind.Number).TryGetInt32(out var pages) || pages is < 0 or > 100) throw new InvalidDataException("团队费用分页无效。");
        return (total, onDemand, Math.Max(1, pages));
    }
    public static (decimal Total, string? Next) ClaudePage(JsonElement root)
    {
        decimal total = 0;
        foreach (var bucket in Field(root, "data", JsonValueKind.Array).EnumerateArray())
            foreach (var row in Field(bucket, "results", JsonValueKind.Array).EnumerateArray())
            {
                if (Field(row, "currency", JsonValueKind.String).GetString() != "USD") throw new InvalidDataException("费用币种不是 USD。");
                total += Number(Field(row, "amount", JsonValueKind.String)) / 100;
            }
        if (!root.TryGetProperty("has_more", out var more) || more.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("费用分页标记缺失。");
        var next = more.GetBoolean() ? Field(root, "next_page", JsonValueKind.String).GetString() : null;
        if (more.GetBoolean() && string.IsNullOrWhiteSpace(next)) throw new InvalidDataException("费用分页游标缺失。");
        return (total, next);
    }
    public static decimal GrokCost(JsonElement root)
    {
        if (!root.TryGetProperty("limitReached", out var limited) || limited.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || limited.GetBoolean())
            throw new InvalidDataException("xAI 费用结果不完整。");
        decimal total = 0;
        foreach (var series in Field(root, "timeSeries", JsonValueKind.Array).EnumerateArray())
            foreach (var point in Field(series, "dataPoints", JsonValueKind.Array).EnumerateArray())
            {
                var values = Field(point, "values", JsonValueKind.Array);
                if (values.GetArrayLength() != 1) throw new InvalidDataException("xAI 费用字段数量异常。");
                total += Number(values[0]);
            }
        return total;
    }
}

public sealed class ProviderUsageClient : IDisposable
{
    private readonly HttpClient _http;
    public ProviderUsageClient(NetworkProxy proxy) : this(new SocketsHttpHandler { Proxy = proxy.CreateProxy(), UseProxy = proxy.Mode != ProxyMode.Direct, AllowAutoRedirect = false }) { }
    public ProviderUsageClient(HttpMessageHandler handler) => _http = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
    public async Task<ProviderUsageSnapshot> ReadAsync(ProviderUsageSettings settings, CancellationToken token)
    {
        settings.Validate();
        var now = DateTimeOffset.UtcNow;
        if (settings.Kind == UsageAccountKind.Subscription)
        {
            if (string.IsNullOrWhiteSpace(settings.SnapshotPath)) throw new InvalidDataException(settings.Provider == UsageProvider.Claude
                ? "请配置 Claude Code 状态栏额度快照。" : "暂无内置个人订阅接口；请配置本地额度快照。");
            await using var file = new FileStream(settings.SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            var json = await ReadBoundedAsync(file, token);
            return ProviderUsageParser.Subscription(json, settings.Provider, now);
        }
        var key = Environment.GetEnvironmentVariable(settings.CredentialEnvironment);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("未配置环境变量：" + settings.CredentialEnvironment);
        if (key.Any(char.IsControl)) throw new InvalidDataException("凭据格式无效。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        token = timeout.Token;
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        if (settings.Provider == UsageProvider.Cursor)
        {
            decimal total = 0, onDemand = 0;
            for (var page = 1; page <= 100; page++)
            {
                using var request = Request("https://api.cursor.com/teams/spend", new { page, pageSize = 100 });
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":")));
                using var doc = await SendAsync(request, token);
                var result = ProviderUsageParser.CursorPage(doc.RootElement);
                total += result.Total; onDemand += result.OnDemand;
                if (page >= result.Pages) return new([new("账期总用量价值", ProviderUsageParser.Usd(total)), new("其中按量支出", ProviderUsageParser.Usd(onDemand))], now, "团队全体 · 当前账期 · 含套餐用量价值");
            }
        }
        else if (settings.Provider == UsageProvider.Claude)
        {
            decimal total = 0; string? cursor = null;
            var seen = new HashSet<string>();
            for (var page = 0; page < 100; page++)
            {
                var url = $"https://api.anthropic.com/v1/organizations/cost_report?starting_at={Uri.EscapeDataString(start.ToString("O"))}&ending_at={Uri.EscapeDataString(now.ToString("O"))}&limit=31";
                if (cursor is not null) url += "&page=" + Uri.EscapeDataString(cursor);
                using var request = Request(url);
                request.Headers.Add("x-api-key", key); request.Headers.Add("anthropic-version", "2023-06-01");
                using var doc = await SendAsync(request, token);
                var result = ProviderUsageParser.ClaudePage(doc.RootElement);
                total += result.Total; cursor = result.Next;
                if (cursor is null) return new([new("本月累计费用", ProviderUsageParser.Usd(total))], now, $"组织 API · {start:yyyy-MM-dd} 至今（UTC）· 不含 Priority Tier");
                if (!seen.Add(cursor)) throw new InvalidDataException("费用分页游标重复。");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.TeamId)) throw new InvalidDataException("请配置 xAI Team ID。");
            using var request = Request($"https://management-api.x.ai/v1/billing/teams/{Uri.EscapeDataString(settings.TeamId)}/usage", new
            {
                analyticsRequest = new { timeRange = new { startTime = start.ToString("yyyy-MM-dd HH:mm:ss"), endTime = now.ToString("yyyy-MM-dd HH:mm:ss"), timezone = "Etc/GMT" },
                    timeUnit = "TIME_UNIT_DAY", values = new[] { new { name = "usd", aggregation = "AGGREGATION_SUM" } }, groupBy = Array.Empty<string>(), filters = Array.Empty<object>() }
            });
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var doc = await SendAsync(request, token);
            return new([new("本月累计费用", ProviderUsageParser.Usd(ProviderUsageParser.GrokCost(doc.RootElement)))], now, $"xAI 团队 API · {start:yyyy-MM-dd} 至今（UTC）");
        }
        throw new InvalidDataException("费用分页超出上限；未展示不完整的汇总。");
    }
    private static HttpRequestMessage Request(string url, object? body = null) => new(body is null ? HttpMethod.Get : HttpMethod.Post, url)
    { Content = body is null ? null : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"用量接口 HTTP {(int)response.StatusCode}（请检查权限、凭据或稍后重试）。");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return JsonDocument.Parse(await ReadBoundedAsync(stream, token));
    }
    private static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        const int limit = 1024 * 1024;
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("用量响应超过 1 MB。");
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    public void Dispose() => _http.Dispose();
}
