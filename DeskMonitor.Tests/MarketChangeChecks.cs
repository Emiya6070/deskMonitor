using System.Text.Json;
using DeskMonitor;
using DeskMonitor.Core;

internal static class MarketChangeChecks
{
    public static void Run(Action<bool, string> check)
    {
        var at = new DateTimeOffset(2026, 11, 11, 12, 34, 42, TimeSpan.Zero);
        var market = new MarketSymbol("BTCUSDT", "BTC", "USDT");
        var ticker = new Ticker(market.Symbol, 110m, 100m, 120m, 90m, at, at, market.Kind);
        foreach (var minutes in new[] { 5, 15, 60, 240 })
        {
            var boundary = at.AddSeconds(-42).AddMinutes(-minutes).AddMilliseconds(-1);
            check(MarketChangeReference.ReferenceTime(at, minutes) == boundary, $"{minutes}m uses the last closed minute preceding the rolling boundary");
            var reference = MarketChangeReference.Parse(Candle(boundary), market, minutes, at, at);
            check(reference.Key == market.Key && reference.Price == 100m && reference.FetchedAt == at && reference.Percent(ticker, at) == 10m,
                $"{minutes}m keeps market identity, decimal price and observation times and computes percent units");
            check(reference.Percent(ticker with { Last = 90m }, at) == -10m && reference.Percent(ticker with { Last = 100m }, at) == 0m,
                $"{minutes}m negative and unchanged prices calculate correctly");
            check(reference.Percent(ticker, at.AddSeconds(16)) is null, $"{minutes}m stale current prices cannot produce a fresh change");
            var nextMinute = at.AddSeconds(18);
            check(reference.Percent(ticker with { ObservedAt = nextMinute, FetchedAt = nextMinute }, nextMinute) is null,
                $"{minutes}m previous minute reference is not silently reused for a new window");
        }
        var end = MarketChangeReference.ReferenceTime(at, 5);
        var valid = Candle(end);
        foreach (var invalid in new[]
        {
            "[]", "{}", "[null]", "[[1,2,3]]", "[" + valid[1..^1] + "," + valid[1..^1] + "]",
            valid.Replace("\"100\"", "null"), valid.Replace("\"100\"", "\"0\""), valid.Replace("\"100\"", "\"NaN\""),
            valid.Replace("\"120\"", "\"95\""), valid.Replace("\"90\"", "\"105\""),
            valid.Replace(end.ToUnixTimeMilliseconds().ToString(), end.ToUnixTimeSeconds().ToString()),
            Candle(end.AddMinutes(1)), Candle(end.AddMinutes(-1))
        })
        {
            var rejected = false;
            try { MarketChangeReference.Parse(invalid, market, 5, at, at); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "missing, malformed, nonpositive, out-of-range or misaligned historical candles are rejected");
        }
        var source = MarketChangeReference.Parse(valid, market, 5, at, at);
        var mismatch = false;
        try { source.Percent(ticker with { Kind = MarketKind.UsdtPerpetual }, at); }
        catch (InvalidOperationException) { mismatch = true; }
        check(mismatch, "spot historical prices cannot be substituted for perpetuals");
        check(new Preferences().CompactChangeMinutes == 0, "compact change defaults to hidden for existing settings");
        foreach (var period in new[] { 0, 5, 15, 60, 240, 1440 })
        {
            var settings = Preferences.Normalize(new() { CompactMarkets = true, CompactChangeMinutes = period });
            var restored = Preferences.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(settings))!);
            check(restored.CompactMarkets && restored.CompactChangeMinutes == period, $"compact {period}m selection survives settings serialization");
        }
        var invalidPeriod = false;
        try { Preferences.Normalize(new() { CompactChangeMinutes = 30 }); }
        catch (InvalidDataException) { invalidPeriod = true; }
        check(invalidPeriod, "unsupported compact change periods fail settings validation");
    }
    private static string Candle(DateTimeOffset close) => JsonSerializer.Serialize(new object[][]
    {
        new object[] { close.ToUnixTimeMilliseconds() - 59999, "100", "120", "90", "100", "1", close.ToUnixTimeMilliseconds(), "100", 1, "1", "100", "0" }
    });
    public static async Task RunLiveAsync(Action<bool, string> check)
    {
        using var feed = new BinanceFeed();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        foreach (var market in new MarketSymbol[]
        {
            new("BTCUSDT", "BTC", "USDT"), new("BTCUSDT", "BTC", "USDT", MarketKind.UsdtPerpetual),
            new("BTCUSDC", "BTC", "USDC", MarketKind.UsdcPerpetual)
        })
        {
            var ticker = await feed.GetSnapshotAsync(market.Symbol, lifetime.Token, market.Kind);
            var reference = await feed.GetChangeReferenceAsync(market, 5, ticker.ObservedAt, lifetime.Token);
            check(reference.Key == ticker.Key && reference.Price > 0 && reference.Percent(ticker, DateTimeOffset.UtcNow) is not null,
                $"live {market.Key}: matching minute-close reference and fresh price from the official venue");
        }
    }
}
