using System.Text.Json;
using System.Collections.Concurrent;
using DeskMonitor;
using DeskMonitor.Core;

var now = DateTimeOffset.UtcNow;
var payload = JsonSerializer.Serialize(new { e = "24hrTicker", E = now.ToUnixTimeMilliseconds(), s = "BTCUSDT", c = "110", o = "100", h = "120", l = "90" });
var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception($"FAIL: {name}"); Console.WriteLine($"PASS: {name}"); Interlocked.Increment(ref passed); }
void Reject(string json, string name)
{
    try { MarketParser.ParseTicker(json, "BTCUSDT", true, now); }
    catch (InvalidDataException) { Check(true, name); return; }
    throw new Exception($"FAIL: {name}");
}
var ticker = MarketParser.ParseTicker(payload, "BTCUSDT", true, now);
Check(ticker.ChangePercent == 10m, "rolling 24h return uses percent units");
Check(!ticker.IsStale(now.AddSeconds(14)) && ticker.IsStale(now.AddSeconds(16)), "15-second freshness boundary");
Check((ticker with { ObservedAt = now.AddMinutes(-1) }).IsStale(now), "fresh receive time cannot hide stale source");
Check((ticker with { FetchedAt = now.AddMinutes(-1) }).IsStale(now), "stale local receipt is marked");
Reject(payload.Replace("\"110\"", "\"\""), "empty price is not zero");
Reject(payload.Replace("\"110\"", "null"), "null price rejected");
Reject(payload.Replace("\"110\"", "\"NaN\""), "nonfinite price rejected");
Reject(payload.Replace("\"100\"", "\"0\""), "zero denominator rejected");
Reject(payload.Replace("BTCUSDT", "ETHUSDT"), "wrong market rejected");
Reject(payload.Replace("\"110\"", "\"150\""), "invalid range rejected");
Reject(payload.Replace(now.ToUnixTimeMilliseconds().ToString(), now.ToUnixTimeSeconds().ToString()), "seconds are not accepted as milliseconds");
Reject(payload.Replace(now.ToUnixTimeMilliseconds().ToString(), now.AddMinutes(2).ToUnixTimeMilliseconds().ToString()), "future source timestamp rejected");
Reject(payload.Replace(now.ToUnixTimeMilliseconds().ToString(), "\"invalid\""), "incorrect timestamp type rejected");
Reject("[]", "incorrect root type rejected");
Check(PriceFormatter.Format(0.00000001m) == "0.00000001", "small asset price retains precision");
Check(PriceFormatter.Format(75678.12m) == "75,678.12", "large price is readable");
var catalog = MarketParser.ParseSymbols("""{"symbols":[{"symbol":"BTCUSDT","baseAsset":"BTC","quoteAsset":"USDT","status":"TRADING"},{"symbol":"ETHBTC","baseAsset":"ETH","quoteAsset":"BTC","status":"TRADING"},{"symbol":"OLDUSDT","baseAsset":"OLD","quoteAsset":"USDT","status":"BREAK"}]}""");
Check(catalog.Count == 1 && catalog[0].Symbol == "BTCUSDT", "catalog excludes inactive and non-USDT symbols");
var rest = JsonSerializer.Serialize(new { symbol = "BTCUSDT", lastPrice = "90", openPrice = "100", highPrice = "120", lowPrice = "80", closeTime = now.ToUnixTimeMilliseconds() });
Check(MarketParser.ParseTicker(rest, "BTCUSDT", false, now).ChangePercent == -10m, "REST schema and negative return");
var futuresCatalog = MarketParser.ParseSymbols("""{"symbols":[{"symbol":"BTCUSDT","baseAsset":"BTC","quoteAsset":"USDT","status":"TRADING","contractType":"PERPETUAL"},{"symbol":"BTCUSDT_260925","baseAsset":"BTC","quoteAsset":"USDT","status":"TRADING","contractType":"CURRENT_QUARTER"},{"symbol":"BTCUSDC","baseAsset":"BTC","quoteAsset":"USDC","status":"TRADING","contractType":"PERPETUAL"}]}""", MarketKind.UsdtPerpetual);
Check(futuresCatalog.Count == 1 && futuresCatalog[0].Kind == MarketKind.UsdtPerpetual, "futures catalog selects active USDT perpetuals only");
var combined = "{\"stream\":\"btcusdt@ticker\",\"data\":" + payload + "}";
var watchSymbols = new HashSet<string> { "BTCUSDT", "ETHUSDT" };
var perpetual = MarketParser.ParseCombined(combined, watchSymbols, MarketKind.UsdtPerpetual, now);
Check(perpetual.Key != ticker.Key && perpetual.Source != ticker.Source, "same symbol on spot and futures has independent identity and source");
foreach (var invalid in new[] { combined.Replace("btcusdt@ticker", "ethusdt@ticker"), combined.Replace("BTCUSDT", "DOGEUSDT"), "{}" })
{
    var rejected = false;
    try { MarketParser.ParseCombined(invalid, watchSymbols, MarketKind.Spot, now); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "combined stream rejects unknown or mismatched messages");
}
var migrated = Preferences.Normalize(JsonSerializer.Deserialize<Preferences>("""{"Symbol":"ETHUSDT","Pinned":false,"Left":-300,"Top":40}""")!);
Check(migrated.Markets![0].Symbol == "ETHUSDT" && migrated.Symbol is null && !migrated.Pinned && migrated.Left == -300, "old single-symbol settings migrate without losing placement");
var settings = Preferences.Normalize(new() { Style = CardStyle.Small, AllowResize = true, SnapToEdges = false,
    Markets = [new("BTCUSDT", "BTC", "USDT"), new("BTCUSDT", "BTC", "USDT", MarketKind.UsdtPerpetual)], Width = 330, Height = 160 });
var restored = Preferences.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(settings))!);
Check(restored.Markets!.Length == 2 && restored.Style == CardStyle.Small && restored.Width == 330 && !restored.SnapToEdges, "multi-market layout settings round-trip");
foreach (var invalid in new[] { settings with { Markets = [] }, settings with { Markets = [settings.Markets![0], settings.Markets[0]] }, settings with { Width = double.NaN } })
{
    var rejected = false;
    try { Preferences.Normalize(invalid); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "invalid or duplicate settings rejected");
}
var area = new PixelRect(-1920, 0, 0, 1040);
Check(WidgetLayout.Snap(new(-1913, 8, -1633, 128), area, 12) == new PixelRect(-1920, 0, -1640, 120), "snap left/top works on negative-coordinate monitor");
Check(WidgetLayout.Snap(new(-288, 912, -8, 1032), area, 12) == new PixelRect(-280, 920, 0, 1040), "snap right/bottom preserves card size");
var far = new PixelRect(-1500, 200, -1220, 320);
Check(WidgetLayout.Snap(far, area, 12) == far, "outside threshold leaves window unchanged");
Check(WidgetLayout.Preset(CardStyle.Small, 2).Height == 120 && WidgetLayout.Preset(CardStyle.Large, 2).Height > WidgetLayout.Preset(CardStyle.Medium, 2).Height, "preset heights accommodate multiple cards");
CodexUsage ParseUsage(string json) { using var doc = JsonDocument.Parse(json); return CodexUsage.Parse(doc.RootElement, now); }
var quotaJson = """{"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":2000000000},"secondary":{"usedPercent":42,"windowDurationMins":10080,"resetsAt":2000100000}},"other":{"primary":{"usedPercent":90}}}}""";
var quota = ParseUsage(quotaJson);
Check(quota.Primary!.RemainingPercent == 75 && quota.Secondary!.RemainingPercent == 58, "quota prefers Codex bucket and converts used to remaining");
Check(quota.Primary.ResetsAt == DateTimeOffset.FromUnixTimeSeconds(2000000000) && quota.Secondary!.Label == "每周", "quota reset uses seconds and labels actual durations");
Check(!quota.IsStale(now.AddSeconds(119)) && quota.IsStale(now.AddSeconds(121)), "quota data expires after two minutes");
Check(quota.Primary.AwaitingReset(DateTimeOffset.FromUnixTimeSeconds(2000000001)), "elapsed reset is pending rather than assumed full");
var partial = ParseUsage("""{"rateLimits":{"primary":null,"secondary":{"usedPercent":101,"windowDurationMins":null,"resetsAt":null}}}""");
Check(partial.Primary is null && partial.Secondary!.RemainingPercent == 0 && partial.Secondary.ResetsAt is null, "missing windows stay unknown and overuse clamps remaining");
Check(ParseUsage("""{"rateLimits":{"primary":{"usedPercent":0,"windowDurationMins":15}}}""").Primary!.Label == "15 分钟", "quota labels are not hardcoded to five hours");
foreach (var invalid in new[] { "{}", "[]", """{"rateLimitsByLimitId":{"other":{}},"rateLimits":{"primary":{"usedPercent":1}}}""", """{"rateLimits":{"primary":{"usedPercent":null}}}""", """{"rateLimits":{"primary":{"usedPercent":-1}}}""", """{"rateLimits":{"primary":{"usedPercent":4,"resetsAt":2000000000000}}}""", """{"rateLimits":{"primary":{"usedPercent":4,"windowDurationMins":"300"}}}""" })
{
    var rejected = false;
    try { ParseUsage(invalid); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "malformed or absent Codex quota is not fabricated");
}
var usageOnly = Preferences.Normalize(new() { Markets = [], ShowCodexUsage = true });
Check(Preferences.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(usageOnly))!).ShowCodexUsage, "usage-only preference persists");
Check(WidgetLayout.Preset(CardStyle.Small, 0, true).Height == 108 && WidgetLayout.Preset(CardStyle.Medium, 3, true).Height == 704, "compact usage card reduces height while medium layout is unchanged");
var dragArea = new PixelRect(0, 0, 1000, 800);
var dragged = new PixelRect(0, 0, 280, 120);
var drag = new SnapDrag(dragged, 100, 50, dragArea);
for (var step = 1; step < 24; step++)
{
    dragged = drag.Move(100 + step, 50, dragged, dragArea, 12, 24);
    if (dragged.Left != 0) throw new Exception("Snap released below threshold");
}
Check(dragged.Left == 0, "slow drag holds below 24 px release threshold");
dragged = drag.Move(124, 50, dragged, dragArea, 12, 24);
Check(dragged.Left == 24 && dragged.Top == 0, "cumulative pointer movement escapes even with snapped rectangles fed back");
dragged = drag.Move(130, 80, dragged, dragArea, 12, 24);
Check(dragged.Left == 30 && dragged.Top == 30 && dragged.Right - dragged.Left == 280, "corner axes release independently and preserve size");
dragged = drag.Move(110, 80, dragged, dragArea, 12, 24);
Check(dragged.Left == 0 && dragged.Top == 30, "returning within capture distance snaps again");
var bottomRight = new PixelRect(720, 680, 1000, 800);
var reverse = new SnapDrag(bottomRight, 800, 700, dragArea);
Check(reverse.Move(776, 676, bottomRight, dragArea, 12, 24) == new PixelRect(696, 656, 976, 776), "right and bottom edges release inward at threshold");
var negative = new SnapDrag(new(-1920, 0, -1640, 120), -1800, 50, area);
Check(negative.Move(-1752, 98, new(-1920, 0, -1640, 120), area, 24, 48).Left == -1872, "negative monitor coordinates and scaled release threshold work");
var exact = new SnapDrag(new(100, 100, 380, 220), 150, 150, dragArea);
var atEdge = exact.Move(50, 150, new(0, 100, 280, 220), dragArea, 12, 24);
Check(exact.Move(70, 150, atEdge, dragArea, 12, 24).Left == 0, "exact edge arrival also enters hysteresis");
var crossed = new SnapDrag(new(0, 100, 280, 220), 50, 150, dragArea);
Check(crossed.Move(20, 150, new(-30, 100, 250, 220), new(-1000, 0, 0, 800), 12, 24).Left == -30, "changing work area clears previous edge lock");
var trend = new TrendHistory();
for (var seconds = -7200; seconds <= 0; seconds++)
{
    var observed = now.AddSeconds(seconds);
    trend.Add(ticker with { ObservedAt = observed, FetchedAt = observed }, observed);
}
Check(trend.Window(now, 60).Length == 721, "trend retains only one hour with five-second spacing");
Check(trend.Window(now, 2).Length == 25 && trend.Window(now, 5).Length == 61 && trend.Window(now, 15).Length == 181, "trend selectors use independent trailing time windows");
trend.Add(ticker with { ObservedAt = now, FetchedAt = now }, now);
trend.Add(ticker with { ObservedAt = now.AddSeconds(-20), FetchedAt = now }, now);
Check(trend.Window(now, 60).Length == 721, "duplicate and stale observations do not add chart samples");
Check(trend.Window(now.AddMinutes(61), 60).Length == 0, "chart clears when all observations age out");
var gaps = new TrendHistory();
gaps.Add(ticker with { ObservedAt = now.AddSeconds(-60), FetchedAt = now.AddSeconds(-60) }, now.AddSeconds(-60));
gaps.Add(ticker with { ObservedAt = now, FetchedAt = now }, now);
Check(gaps.Window(now, 2).Length == 2 && gaps.Window(now, 2)[1].ObservedAt - gaps.Window(now, 2)[0].ObservedAt == TimeSpan.FromSeconds(60), "trend preserves real gaps without filling observations");
var spans = settings with { TrendMinutes = new() { [settings.Markets![0].Key] = 5, [settings.Markets[1].Key] = 60 } };
var restoredSpans = Preferences.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(spans))!);
Check(restoredSpans.TrendMinutes[settings.Markets[0].Key] == 5 && restoredSpans.TrendMinutes[settings.Markets[1].Key] == 60 && migrated.TrendMinutes.Count == 0, "per-market trend spans persist independently and old preferences default to two minutes");
var invalidSpanRejected = false;
try { Preferences.Normalize(settings with { TrendMinutes = new() { [settings.Markets[0].Key] = 0 } }); } catch (InvalidDataException) { invalidSpanRejected = true; }
Check(invalidSpanRejected, "unsupported trend spans are rejected");
var appearance = settings with { Skin = Skin.Glacier, TextScale = 1.3, NumberScale = 1.2, MonospaceNumbers = true, ShowTrends = false, RefreshSeconds = 5 };
var restoredAppearance = Preferences.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(appearance))!);
Check(restoredAppearance.Skin == Skin.Glacier && restoredAppearance.TextScale == 1.3 && restoredAppearance.NumberScale == 1.2 && restoredAppearance.MonospaceNumbers && !restoredAppearance.ShowTrends && restoredAppearance.RefreshSeconds == 5, "appearance and refresh preferences persist");
Check(migrated.Skin == Skin.Forest && migrated.TextScale == 1 && migrated.NumberScale == 1 && migrated.ShowTrends && migrated.RefreshSeconds == 1, "old preferences keep their appearance and update frequency");
foreach (var invalid in new[] { appearance with { Skin = (Skin)99 }, appearance with { TextScale = 4 }, appearance with { NumberScale = double.NaN }, appearance with { RefreshSeconds = 0 } })
{
    var rejected = false; try { Preferences.Normalize(invalid); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "invalid appearance or refresh setting rejected");
}
var bigger = WidgetLayout.Preset(CardStyle.Medium, 3, true, 1.3, 1.3);
Check(bigger.Width == 468 && bigger.Height > WidgetLayout.Preset(CardStyle.Medium, 3, true).Height, "larger text reserves actual layout space");
Check(WidgetLayout.CornersAtWorkArea(new(0,0,280,120),dragArea)==DockedCorners.TopLeft,"only top-left corner squares at top-left work-area corner");
Check(WidgetLayout.CornersAtWorkArea(new(720,0,1000,120),dragArea)==DockedCorners.TopRight,"top-right attachment squares top-right corner");
Check(WidgetLayout.CornersAtWorkArea(new(720,680,1000,800),dragArea)==DockedCorners.BottomRight,"bottom-right attachment squares bottom-right corner");
Check(WidgetLayout.CornersAtWorkArea(new(0,680,280,800),dragArea)==DockedCorners.BottomLeft,"bottom-left attachment squares bottom-left corner");
Check(WidgetLayout.CornersAtWorkArea(new(0,100,280,220),dragArea)==DockedCorners.None,"a single edge does not square unrelated corners");
Check(WidgetLayout.CornersAtWorkArea(new(24,24,304,144),dragArea)==DockedCorners.None,"detached window restores all round corners");
Check(WidgetLayout.CornersAtWorkArea(new(-1920,0,-1640,120),area)==DockedCorners.TopLeft,"corner detection supports negative monitor coordinates");
Check(WidgetLayout.CornersAtWorkArea(new(1,1,281,121),dragArea)==DockedCorners.TopLeft && WidgetLayout.CornersAtWorkArea(new(2,2,282,122),dragArea)==DockedCorners.None,"corner tolerance is one physical pixel, not snap capture distance");
Check(WidgetLayout.CornersAtWorkArea(dragArea,dragArea)==(DockedCorners.TopLeft|DockedCorners.TopRight|DockedCorners.BottomRight|DockedCorners.BottomLeft),"all touching corners square for a work-area sized window");
Check(migrated.CardOrder.SequenceEqual(new[]{Preferences.CodexCardKey,migrated.Markets![0].Key}),"old layouts migrate with the usage card first");
var ordered = Preferences.Normalize(settings with { ShowCodexUsage=true,CardOrder=[settings.Markets![1].Key,Preferences.CodexCardKey,settings.Markets[0].Key] });
Check(Preferences.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(ordered))!).CardOrder.SequenceEqual(ordered.CardOrder),"mixed spot, quota and futures card order persists");
Check(Preferences.Normalize(ordered with { ShowCodexUsage=false }).CardOrder.SequenceEqual(ordered.CardOrder),"disabled usage card retains its slot");
var changedCards=Preferences.Normalize(ordered with { Markets=[settings.Markets[0],new("ETHUSDT","ETH","USDT")] });
Check(changedCards.CardOrder.SequenceEqual(new[]{Preferences.CodexCardKey,settings.Markets[0].Key,"Spot:ETHUSDT"}),"removing and adding markets reconciles card order without stale slots");
foreach (var badOrder in new string[][] { [Preferences.CodexCardKey,Preferences.CodexCardKey], [""] })
{
    var rejected=false;try { Preferences.Normalize(settings with { CardOrder=badOrder }); } catch(InvalidDataException) { rejected=true; }
    Check(rejected,"malformed card ordering is rejected");
}
var usdcMarket = new MarketSymbol("BTCUSDC","BTC","USDC",MarketKind.UsdcPerpetual);
var usdcPrefs=Preferences.Normalize(settings with {Markets=[settings.Markets[0],usdcMarket],HideHeader=true,SmallCornerRadius=0,Proxy=new NetworkProxy {Mode=ProxyMode.Custom,Address="socks5://127.0.0.1:7890"}});
var usdcRestored=Preferences.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(usdcPrefs))!);
Check(usdcRestored.Markets![1]==usdcMarket && usdcRestored.HideHeader && usdcRestored.SmallCornerRadius==0 && usdcRestored.Proxy==usdcPrefs.Proxy,"USDC, header, small radius and proxy preferences round-trip");
Check(migrated.Proxy.Mode==ProxyMode.System && !migrated.HideHeader && migrated.SmallCornerRadius is null,"old configuration keeps header and skin-defined small corners");
var usdcCatalog=MarketParser.ParseSymbols("""{"symbols":[{"symbol":"BTCUSDC","baseAsset":"BTC","quoteAsset":"USDC","status":"TRADING","contractType":"PERPETUAL"},{"symbol":"BTCUSDT","baseAsset":"BTC","quoteAsset":"USDT","status":"TRADING","contractType":"PERPETUAL"},{"symbol":"OLDUSDC","baseAsset":"OLD","quoteAsset":"USDC","status":"BREAK","contractType":"PERPETUAL"}]}""",MarketKind.UsdcPerpetual);
Check(usdcCatalog.Count==1 && usdcCatalog[0]==usdcMarket,"USDC catalog filters quote currency and trading status");
var usdcPayload=combined.Replace("BTCUSDT","BTCUSDC").Replace("btcusdt","btcusdc");
var mixedKinds=new Dictionary<string,MarketKind>{{"BTCUSDT",MarketKind.UsdtPerpetual},{"BTCUSDC",MarketKind.UsdcPerpetual}};
var usdcTicker=MarketParser.ParseCombined(usdcPayload,mixedKinds,now);
Check(usdcTicker.Kind==MarketKind.UsdcPerpetual && usdcTicker.Key!=perpetual.Key && usdcTicker.Source.Contains("USDC"),"shared futures stream preserves USDC identity and source");
Check(MarketParser.ParseCombined(combined,mixedKinds,now).Kind==MarketKind.UsdtPerpetual,"shared futures stream preserves USDT identity");
Check(WidgetLayout.Preset(CardStyle.Medium,1,hideHeader:true).Height==WidgetLayout.Preset(CardStyle.Medium,1).Height-44 && WidgetLayout.Preset(CardStyle.Small,1,hideHeader:true)==WidgetLayout.Preset(CardStyle.Small,1),"hiding header reclaims space only for medium/large layouts");
Check(new NetworkProxy{Mode=ProxyMode.Direct}.CreateProxy() is null && new NetworkProxy().CreateProxy() is not null,"direct and system proxy modes map to network transport");
foreach(var address in new[]{"http://127.0.0.1:7890","socks5://localhost:1080"}) {
    var proxy=new NetworkProxy{Mode=ProxyMode.Custom,Address=address}.CreateProxy()!;
    Check(proxy.GetProxy(new Uri("https://fapi.binance.com"))!.ToString().TrimEnd('/')==address,"custom proxy is passed through without bypass");
}
foreach(var address in new[]{"", "127.0.0.1:7890", "ftp://localhost:22", "http://user:pass@localhost:80", "http://localhost:80/path", "http://localhost:80?x=1"}) {
    var rejected=false;try {new NetworkProxy{Mode=ProxyMode.Custom,Address=address}.Validate();}catch(InvalidDataException){rejected=true;}Check(rejected,"invalid proxy address rejected");
}
foreach(var radius in new[]{-1d,25d,double.NaN}) {var rejected=false;try{Preferences.Normalize(settings with {SmallCornerRadius=radius});}catch(InvalidDataException){rejected=true;}Check(rejected,"invalid small corner radius rejected");}
Check(migrated.UsageExtraHeight == 0, "existing usage cards default to automatic compact height");
var usageSize = Preferences.Normalize(settings with { UsageExtraHeight = 48 });
Check(Preferences.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(usageSize))!).UsageExtraHeight == 48, "usage height persists");
foreach (var height in new[] { -1d, 121d, double.NaN })
{
    var rejected = false;
    try { Preferences.Normalize(settings with { UsageExtraHeight = height }); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "invalid usage extra height rejected");
}
var fundingMarkets = new Dictionary<string, MarketKind> { ["BTCUSDT"] = MarketKind.UsdtPerpetual, ["BTCUSDC"] = MarketKind.UsdcPerpetual };
string FundingPayload(string rate = "0.0001", string symbol = "BTCUSDT") => JsonSerializer.Serialize(new { stream = symbol.ToLowerInvariant() + "@markPrice", data = new { e = "markPriceUpdate", E = now.ToUnixTimeMilliseconds(), s = symbol, r = rate, T = now.AddHours(1).ToUnixTimeMilliseconds(), st = 1 } });
FundingQuote ParseFunding(string json) { using var document = JsonDocument.Parse(json); return FundingQuote.ParseCombined(document.RootElement, fundingMarkets, now); }
var fundingSample = ParseFunding(FundingPayload());
Check(fundingSample.PercentText == "+0.0100%" && fundingSample.NextFundingAt == DateTimeOffset.FromUnixTimeMilliseconds(now.AddHours(1).ToUnixTimeMilliseconds()), "funding fraction converts to signed percent and preserves actual settlement time");
Check(ParseFunding(FundingPayload("-0.000025")).PercentText == "-0.0025%" && ParseFunding(FundingPayload("0")).PercentText == "0.0000%", "negative and zero funding remain valid");
Check(ParseFunding(FundingPayload("0.00000001")).PercentText == "+0.000001%", "small nonzero funding retains source precision");
{
    var rejected = false;
    try { ParseFunding(FundingPayload(decimal.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture))); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "unrepresentable funding percentage rejected before display");
}
Check(ParseFunding(FundingPayload(symbol: "BTCUSDC")).Kind == MarketKind.UsdcPerpetual, "USDC funding retains market identity");
Check(!fundingSample.IsStale(now.AddSeconds(14)) && fundingSample.IsStale(now.AddSeconds(16)), "funding stale independently of price");
Check((fundingSample with { FetchedAt = now.AddMinutes(-1) }).IsStale(now), "funding receive timestamp participates in freshness");
foreach (var badFunding in new[] { FundingPayload(""), FundingPayload("NaN"), FundingPayload().Replace("\"r\":\"0.0001\"", "\"r\":null"), FundingPayload(symbol: "ETHUSDT"), FundingPayload().Replace("btcusdt@markPrice", "btcusdc@markPrice"), FundingPayload().Replace("markPriceUpdate", "24hrTicker"), FundingPayload().Replace("\"st\":1", "\"st\":2"), FundingPayload().Replace(now.ToUnixTimeMilliseconds().ToString(), now.ToUnixTimeSeconds().ToString()), FundingPayload().Replace(now.ToUnixTimeMilliseconds().ToString(), now.AddMinutes(2).ToUnixTimeMilliseconds().ToString()) })
{
    var rejected = false;
    try { ParseFunding(badFunding); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "malformed, wrong product or unrequested funding rejected");
}
using (var spotFundingDocument = JsonDocument.Parse(FundingPayload()))
{
    var rejected = false;
    try { FundingQuote.ParseCombined(spotFundingDocument.RootElement, new Dictionary<string, MarketKind> { ["BTCUSDT"] = MarketKind.Spot }, now); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "spot never accepts funding rates");
}
var tradFiMarkets = MarketParser.ParseSymbols("""{"symbols":[{"symbol":"TSLAUSDT","baseAsset":"TSLA","quoteAsset":"USDT","status":"TRADING","contractType":"TRADIFI_PERPETUAL"}]}""", MarketKind.UsdtPerpetual);
Check(tradFiMarkets.Single().TradFi && tradFiMarkets.Single().MarketLabel == "TradFi 永续", "TradFi perpetual catalog includes and labels stock contracts");
var tradFiPreferences = Preferences.Normalize(settings with { Markets = [tradFiMarkets.Single()] });
Check(Preferences.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(tradFiPreferences))!).Markets!.Single().TradFi, "TradFi identity persists through settings");
Check(WidgetLayout.Preset(CardStyle.Medium, 2, contractCount: 1).Height - WidgetLayout.Preset(CardStyle.Medium, 2).Height == 24 && WidgetLayout.Preset(CardStyle.Small, 2, contractCount: 2).Height == WidgetLayout.Preset(CardStyle.Small, 2).Height, "funding reserves full-card space while compact height stays unchanged");
Console.WriteLine($"{passed} deterministic checks passed.");

if(args.Contains("--live-usdc")) {
    using var usdcFeed=new BinanceFeed();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(35));
    var liveCatalog=await usdcFeed.GetSymbolsAsync(stop.Token,MarketKind.UsdcPerpetual);
    Check(liveCatalog.Any(m=>m==usdcMarket),"live USDC catalog contains BTCUSDC");
    var snapshot=await usdcFeed.GetSnapshotAsync("BTCUSDC",stop.Token,MarketKind.UsdcPerpetual);
    Check(!snapshot.IsStale(DateTimeOffset.UtcNow) && snapshot.Kind==MarketKind.UsdcPerpetual,"real BTCUSDC REST snapshot is fresh and correctly labeled");
    var got=new ConcurrentDictionary<MarketKind,int>();
    await usdcFeed.RunAsync([new("BTCUSDT","BTC","USDT",MarketKind.UsdtPerpetual),usdcMarket],tick=>{got.AddOrUpdate(tick.Kind,1,(_,v)=>v+1);if(got.Count==2 && got.Values.All(v=>v>=2))stop.Cancel();},(_,_)=>{},stop.Token);
    Check(got.Count==2 && got.Values.All(v=>v>=2),"one combined futures connection delivers real USDT and USDC prices");
}

if (args.Contains("--live-usage"))
{
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var usage = await CodexUsageClient.ReadAsync(null, lifetime.Token);
    Check(!usage.IsStale(DateTimeOffset.UtcNow) && (usage.Primary is not null || usage.Secondary is not null), "live Codex quota retrieved and query process closed");
    Console.WriteLine(JsonSerializer.Serialize(usage));
}

if (args.Contains("--live"))
{
    using var feed = new BinanceFeed();
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var symbols = await feed.GetSymbolsAsync(lifetime.Token);
    Check(symbols.Any(x => x.Symbol == "BTCUSDT"), $"live catalog: {symbols.Count} USDT markets");
    var futures = await feed.GetSymbolsAsync(lifetime.Token, MarketKind.UsdtPerpetual);
    Check(futures.Any(x => x.Symbol == "BTCUSDT"), $"live futures catalog: {futures.Count} USDT perpetuals");
    var markets = new[] { symbols.First(x => x.Symbol == "BTCUSDT"), symbols.First(x => x.Symbol == "ETHUSDT"),
        futures.First(x => x.Symbol == "BTCUSDT"), futures.First(x => x.Symbol == "ETHUSDT") };
    foreach (var market in markets)
    {
        var snapshot = await feed.GetSnapshotAsync(market.Symbol, lifetime.Token, market.Kind);
        Check(snapshot.Key == market.Key && !snapshot.IsStale(DateTimeOffset.UtcNow), $"{market.Key} REST timestamp is fresh");
    }
    using var streamStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    var counts = new ConcurrentDictionary<string, int>();
    await feed.RunAsync(markets, value =>
    {
        Check(markets.Any(m => m.Key == value.Key) && !value.IsStale(DateTimeOffset.UtcNow), $"{value.Key} live {value.Last} USDT");
        counts.AddOrUpdate(value.Key, 1, (_, count) => count + 1);
        if (markets.All(m => counts.GetValueOrDefault(m.Key) >= 2)) streamStop.Cancel();
    }, (kind, status) => Console.WriteLine($"  {kind} {status.Phase}: {status.Detail}"), streamStop.Token);
    Check(markets.All(m => counts.GetValueOrDefault(m.Key) >= 2), "both combined connections deliver all markets and shut down on cancellation");
}

if (args.Contains("--live-funding"))
{
    using var fundingFeed = new BinanceFeed();
    using var fundingStop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var fundingCatalog = await fundingFeed.GetSymbolsAsync(fundingStop.Token, MarketKind.UsdtPerpetual);
    var fundingUsdc = await fundingFeed.GetSymbolsAsync(fundingStop.Token, MarketKind.UsdcPerpetual);
    var fundingSelection = new[] { fundingCatalog.Single(m => m.Symbol == "BTCUSDT"), fundingCatalog.Single(m => m.Symbol == "TSLAUSDT"), fundingUsdc.Single(m => m.Symbol == "BTCUSDC") };
    Check(fundingSelection[1].TradFi, "live catalog recognizes TSLA TradFi");
    var fundingPrices = new ConcurrentDictionary<string, Ticker>();
    var fundingRates = new ConcurrentDictionary<string, FundingQuote>();
    void FinishFunding() { if (fundingPrices.Count == 3 && fundingRates.Count == 3) fundingStop.Cancel(); }
    await fundingFeed.RunAsync(fundingSelection, ticker => { fundingPrices[ticker.Key] = ticker; FinishFunding(); }, (kind, state) => Console.WriteLine($"{kind}: {state.Detail}"), fundingStop.Token,
        quote => { fundingRates[quote.Key] = quote; Console.WriteLine($"FUNDING {quote.Symbol} {quote.PercentText}, next {quote.NextFundingAt:O}"); FinishFunding(); });
    Check(fundingPrices.Count == 3 && fundingRates.Count == 3, "one futures connection carries prices and funding for USDT, USDC and TradFi");
    Check(fundingPrices.Values.All(t => !t.IsStale(DateTimeOffset.UtcNow)) && fundingRates.Values.All(f => !f.IsStale(DateTimeOffset.UtcNow) && f.NextFundingAt > DateTimeOffset.UtcNow), "live funding and ticker timestamps fresh and next settlements in future");
}
