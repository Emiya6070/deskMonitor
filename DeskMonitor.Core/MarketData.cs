using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskMonitor.Core;

public enum MarketKind { Spot, UsdtPerpetual, UsdcPerpetual }
public enum CardStyle { Small, Medium, Large }

public sealed record MarketSymbol(string Symbol, string BaseAsset, string QuoteAsset, MarketKind Kind = MarketKind.Spot, bool TradFi = false)
{
    [JsonIgnore] public string Key => $"{Kind}:{Symbol}";
    [JsonIgnore] public string MarketLabel => Kind == MarketKind.Spot ? "现货" : TradFi ? "TradFi 永续" : "永续";
    [JsonIgnore] public string Label => $"{BaseAsset} / {QuoteAsset} · {MarketLabel}";
}

// Observed Binance spot or USDT/USDC perpetual last-trade prices; not mark prices or USD.
public sealed record Ticker(string Symbol, decimal Last, decimal Open24h, decimal High24h,
    decimal Low24h, DateTimeOffset ObservedAt, DateTimeOffset FetchedAt, MarketKind Kind = MarketKind.Spot)
{
    public string Key => $"{Kind}:{Symbol}";
    public string Source => Kind switch { MarketKind.Spot => "Binance Spot", MarketKind.UsdtPerpetual => "Binance USDT Perpetual", MarketKind.UsdcPerpetual => "Binance USDC Perpetual", _ => throw new InvalidDataException("未知市场类型。") };
    // Derived simple return for the rolling 24-hour window, not the UTC calendar day.
    public decimal ChangePercent => (Last / Open24h - 1m) * 100m;
    public bool IsStale(DateTimeOffset now) =>
        now - ObservedAt > TimeSpan.FromSeconds(15) || now - FetchedAt > TimeSpan.FromSeconds(15);
}

public static class MarketParser
{
    public static IReadOnlyList<MarketSymbol> ParseSymbols(string json, MarketKind kind = MarketKind.Spot)
    {
        return SelectSymbols(JsonSerializer.Deserialize<ExchangeCatalog>(json), kind);
    }

    public static async Task<IReadOnlyList<MarketSymbol>> ParseSymbolsAsync(Stream stream, CancellationToken token, MarketKind kind = MarketKind.Spot) =>
        SelectSymbols(await JsonSerializer.DeserializeAsync<ExchangeCatalog>(stream, cancellationToken: token).ConfigureAwait(false), kind);

    private static IReadOnlyList<MarketSymbol> SelectSymbols(ExchangeCatalog? catalog, MarketKind kind)
    {
        if (catalog?.Symbols is null) throw new InvalidDataException("交易对列表格式异常。");
        var result = new List<MarketSymbol>();
        foreach (var item in catalog.Symbols)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Status) || string.IsNullOrWhiteSpace(item.QuoteAsset)
                || string.IsNullOrWhiteSpace(item.Symbol) || string.IsNullOrWhiteSpace(item.BaseAsset))
                throw new InvalidDataException("交易对字段缺失。");
            if (item.Status == "TRADING" && item.QuoteAsset == (kind == MarketKind.UsdcPerpetual ? "USDC" : "USDT")
                && (kind == MarketKind.Spot || item.ContractType is "PERPETUAL" or "TRADIFI_PERPETUAL"))
            {
                if (item.Symbol != item.BaseAsset + item.QuoteAsset) throw new InvalidDataException("交易对计价币不匹配。");
                result.Add(new(item.Symbol, item.BaseAsset, item.QuoteAsset, kind, kind != MarketKind.Spot && item.ContractType == "TRADIFI_PERPETUAL"));
            }
        }
        if (result.Count == 0) throw new InvalidDataException("币安未返回该市场的可用交易对。");
        return result.OrderBy(x => x.BaseAsset, StringComparer.Ordinal).ToArray();
    }

    public static Ticker ParseTicker(string json, string expectedSymbol, bool webSocket, DateTimeOffset now, MarketKind kind = MarketKind.Spot)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseTicker(doc.RootElement, expectedSymbol, webSocket, now, kind);
    }

    private static Ticker ParseTicker(JsonElement item, string expectedSymbol, bool webSocket, DateTimeOffset now, MarketKind kind)
    {
        var quote = kind == MarketKind.UsdcPerpetual ? "USDC" : "USDT";
        if (!Enum.IsDefined(kind) || !expectedSymbol.EndsWith(quote, StringComparison.Ordinal) || expectedSymbol.Length <= quote.Length)
            throw new InvalidDataException("行情市场与计价币不匹配。");
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("行情消息必须为对象。");
        if (webSocket && Text(item, "e") != "24hrTicker")
            throw new InvalidDataException("行情流返回了非 ticker 消息。");
        var symbol = Text(item, webSocket ? "s" : "symbol");
        if (symbol != expectedSymbol) throw new InvalidDataException("行情交易对与订阅不一致。");
        var last = Positive(item, webSocket ? "c" : "lastPrice");
        var open = Positive(item, webSocket ? "o" : "openPrice");
        var high = Positive(item, webSocket ? "h" : "highPrice");
        var low = Positive(item, webSocket ? "l" : "lowPrice");
        if (high < low || last > high || last < low)
            throw new InvalidDataException("行情价格范围异常。");
        if (!item.TryGetProperty(webSocket ? "E" : "closeTime", out var time) || time.ValueKind != JsonValueKind.Number || !time.TryGetInt64(out var ms)
            || ms < 1_230_768_000_000 || ms > 253_402_300_799_999)
            throw new InvalidDataException("行情时间戳异常。");
        var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        if (observedAt > now.AddMinutes(1)) throw new InvalidDataException("行情时间超前，请检查系统时钟。");
        return new Ticker(symbol, last, open, high, low, observedAt, now, kind);
    }

    public static Ticker ParseCombined(string json, IReadOnlySet<string> symbols, MarketKind kind, DateTimeOffset now)
        => ParseCombined(json, symbols.ToDictionary(symbol => symbol, _ => kind), now);

    public static Ticker ParseCombined(string json, IReadOnlyDictionary<string, MarketKind> symbols, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseCombined(doc.RootElement, symbols, now);
    }
    public static Ticker ParseCombined(JsonElement root, IReadOnlyDictionary<string, MarketKind> symbols, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("合并行情消息格式异常。");
        var symbol = Text(data, "s");
        if (!symbols.TryGetValue(symbol, out var kind) || Text(root, "stream") != symbol.ToLowerInvariant() + "@ticker")
            throw new InvalidDataException("收到未订阅或不匹配的行情。");
        if (kind != MarketKind.Spot && data.TryGetProperty("st", out var type)
            && (type.ValueKind != JsonValueKind.Number || !type.TryGetInt32(out var number) || number != 1))
            throw new InvalidDataException("合约行情不是 USDⓈ 本位产品。");
        return ParseTicker(data, symbol, true, now, kind);
    }

    private static string Text(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"行情字段缺失：{name}");
        return value.GetString()!;
    }

    private static decimal Positive(JsonElement item, string name)
    {
        if (!decimal.TryParse(Text(item, name), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw new InvalidDataException($"行情数值无效：{name}");
        return value;
    }
}

// Deserialize only these fields from the multi-megabyte catalog; skip order filters and permissions.
internal sealed class ExchangeCatalog
{
    [JsonPropertyName("symbols")] public ExchangeSymbol[]? Symbols { get; set; }
}
internal sealed class ExchangeSymbol
{
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    [JsonPropertyName("baseAsset")] public string? BaseAsset { get; set; }
    [JsonPropertyName("quoteAsset")] public string? QuoteAsset { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("contractType")] public string? ContractType { get; set; }
}

public static class PriceFormatter
{
    public static string Format(decimal value) => value.ToString(value >= 1000m ? "#,##0.00" : value >= 1m ? "0.00##" : "0.00##########", CultureInfo.InvariantCulture);
}
