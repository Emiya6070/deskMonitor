using System.Globalization;
using System.Text.Json;

namespace DeskMonitor.Core;

// A closed one-minute last-trade candle from the same venue and quote currency.
// It precedes the requested rolling boundary by at most one minute; UI marks the result approximate.
public sealed record MarketChangeReference(string Key, int Minutes, decimal Price, DateTimeOffset ObservedAt, DateTimeOffset FetchedAt)
{
    public static bool IsSupportedPeriod(int minutes) => minutes is 0 or 5 or 15 or 60 or 240 or 1440;
    public static string PeriodLabel(int minutes) => minutes switch
    {
        5 => "5m", 15 => "15m", 60 => "1H", 240 => "4H", 1440 => "24H",
        _ => throw new ArgumentOutOfRangeException(nameof(minutes))
    };
    public static DateTimeOffset ReferenceTime(DateTimeOffset at, int minutes)
    {
        if (minutes is not (5 or 15 or 60 or 240)) throw new ArgumentOutOfRangeException(nameof(minutes));
        return DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds() / 60000 * 60000 - minutes * 60000L - 1);
    }
    public decimal? Percent(Ticker ticker, DateTimeOffset now)
    {
        if (ticker.Key != Key) throw new InvalidOperationException("涨跌幅历史价格与交易对不匹配。");
        if (ticker.IsStale(now) || ObservedAt != ReferenceTime(ticker.ObservedAt, Minutes)) return null;
        return (ticker.Last / Price - 1m) * 100m;
    }
    public static MarketChangeReference Parse(string json, MarketSymbol market, int minutes, DateTimeOffset asOf, DateTimeOffset fetchedAt)
    {
        var expectedClose = ReferenceTime(asOf, minutes).ToUnixTimeMilliseconds();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1)
            throw new InvalidDataException("未取得对应分钟的历史价格。");
        var candle = root[0];
        if (candle.ValueKind != JsonValueKind.Array || candle.GetArrayLength() != 12
            || candle[0].ValueKind != JsonValueKind.Number || !candle[0].TryGetInt64(out var openTime)
            || candle[6].ValueKind != JsonValueKind.Number || !candle[6].TryGetInt64(out var closeTime)
            || closeTime != expectedClose || openTime != expectedClose - 59999)
            throw new InvalidDataException("历史价格的分钟边界或时间单位无效。");
        decimal PriceAt(int index)
        {
            if (candle[index].ValueKind != JsonValueKind.String
                || !decimal.TryParse(candle[index].GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value <= 0)
                throw new InvalidDataException("历史价格数值无效。");
            return value;
        }
        var high = PriceAt(2); var low = PriceAt(3); var close = PriceAt(4);
        if (high < low || close < low || close > high) throw new InvalidDataException("历史价格超出 K 线范围。");
        return new(market.Key, minutes, close, DateTimeOffset.FromUnixTimeMilliseconds(closeTime), fetchedAt);
    }
}
