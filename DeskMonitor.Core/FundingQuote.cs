using System.Globalization;
using System.Text.Json;

namespace DeskMonitor.Core;

// Binance markPrice stream: r is a decimal fraction for the current funding period,
// T is the next settlement time in milliseconds. Neither is a fixed 8h/APR value.
public sealed record FundingQuote(string Symbol, MarketKind Kind, decimal Rate,
    DateTimeOffset NextFundingAt, DateTimeOffset ObservedAt, DateTimeOffset FetchedAt)
{
    public string Key => $"{Kind}:{Symbol}";
    public decimal Percent => Rate * 100m;
    public string PercentText => Percent.ToString("+0.0000##;-0.0000##;0.0000", CultureInfo.InvariantCulture) + "%";
    public bool IsStale(DateTimeOffset now) => now - ObservedAt > TimeSpan.FromSeconds(15) || now - FetchedAt > TimeSpan.FromSeconds(15);
    public static FundingQuote ParseCombined(JsonElement root, IReadOnlyDictionary<string, MarketKind> symbols, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("资金费率消息格式异常。");
        string Text(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidDataException($"资金费率字段缺失：{name}");
            return value.GetString()!;
        }
        DateTimeOffset Time(string name)
        {
            if (!data.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var ms)
                || ms < 1_230_768_000_000 || ms > 253_402_300_799_999)
                throw new InvalidDataException($"资金费率时间戳异常：{name}");
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        var symbol = Text(data, "s");
        if (!symbols.TryGetValue(symbol, out var kind) || kind is not (MarketKind.UsdtPerpetual or MarketKind.UsdcPerpetual)
            || !symbol.EndsWith(kind == MarketKind.UsdcPerpetual ? "USDC" : "USDT", StringComparison.Ordinal)
            || Text(root, "stream") != symbol.ToLowerInvariant() + "@markPrice" || Text(data, "e") != "markPriceUpdate")
            throw new InvalidDataException("收到未订阅或不匹配的资金费率。");
        if (data.TryGetProperty("st", out var type) && (type.ValueKind != JsonValueKind.Number || !type.TryGetInt32(out var st) || st != 1))
            throw new InvalidDataException("资金费率不是 USDⓈ 本位产品。");
        if (!decimal.TryParse(Text(data, "r"), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate)
            || rate > decimal.MaxValue / 100m || rate < decimal.MinValue / 100m)
            throw new InvalidDataException("资金费率数值无效。");
        var observed = Time("E");
        if (observed > now.AddMinutes(1)) throw new InvalidDataException("资金费率时间超前，请检查系统时钟。");
        // Expired next-funding timestamps remain observable and are shown as pending update.
        return new(symbol, kind, rate, Time("T"), observed, now);
    }
}
