using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DeskMonitor.Core;

public enum FeedPhase { Connecting, Live, Reconnecting }
public sealed record FeedStatus(FeedPhase Phase, string Detail);

public sealed class BinanceFeed : IDisposable
{
    private readonly HttpClient _http;
    private readonly System.Net.IWebProxy? _proxy;
    public BinanceFeed(NetworkProxy? proxy = null)
    {
        var settings = proxy ?? new NetworkProxy();
        _proxy = settings.CreateProxy();
        _http = new HttpClient(new SocketsHttpHandler { Proxy = _proxy, UseProxy = settings.Mode != ProxyMode.Direct }) { Timeout = TimeSpan.FromSeconds(15) };
    }
    public async Task<IReadOnlyList<MarketSymbol>> GetSymbolsAsync(CancellationToken token, MarketKind kind = MarketKind.Spot)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var url = kind == MarketKind.Spot
            ? "https://data-api.binance.vision/api/v3/exchangeInfo?permissions=SPOT&showPermissionSets=false"
            : "https://fapi.binance.com/fapi/v1/exchangeInfo";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        return await MarketParser.ParseSymbolsAsync(stream, timeout.Token, kind).ConfigureAwait(false);
    }
    public async Task<Ticker> GetSnapshotAsync(string symbol, CancellationToken token, MarketKind kind = MarketKind.Spot)
    {
        var endpoint = kind == MarketKind.Spot ? "https://data-api.binance.vision/api/v3/ticker/24hr" : "https://fapi.binance.com/fapi/v1/ticker/24hr";
        var json = await _http.GetStringAsync($"{endpoint}?symbol={Uri.EscapeDataString(symbol)}", token).ConfigureAwait(false);
        return MarketParser.ParseTicker(json, symbol, false, DateTimeOffset.UtcNow, kind);
    }
    // One combined stream per venue. A futures outage does not stop spot prices.
    public Task RunAsync(IReadOnlyList<MarketSymbol> markets, Action<Ticker> onTicker,
        Action<MarketKind, FeedStatus> onStatus, CancellationToken token, Action<FundingQuote>? onFunding = null) =>
        Task.WhenAll(markets.GroupBy(x => x.Kind == MarketKind.Spot).Select(group => RunGroupAsync(
            group.ToDictionary(x => x.Symbol, x => x.Kind, StringComparer.Ordinal), group.Key, onTicker, onStatus, token, onFunding)));

    private async Task RunGroupAsync(Dictionary<string, MarketKind> symbols, bool spot, Action<Ticker> onTicker,
        Action<MarketKind, FeedStatus> onStatus, CancellationToken token, Action<FundingQuote>? onFunding)
    {
        var delaySeconds = 2;
        var lastObserved = new Dictionary<string, DateTimeOffset>();
        var lastFundingObserved = new Dictionary<string, DateTimeOffset>();
        var kinds = symbols.Values.Distinct().ToArray();
        void Report(FeedStatus status) { foreach (var kind in kinds) onStatus(kind, status); }
        var endpoint = spot ? "wss://data-stream.binance.vision/stream?streams=" : "wss://fstream.binance.com/market/stream?streams=";
        var streams = symbols.Keys.Order().SelectMany(x => !spot && onFunding is not null
            ? new[] { x.ToLowerInvariant() + "@ticker", x.ToLowerInvariant() + "@markPrice" }
            : new[] { x.ToLowerInvariant() + "@ticker" });
        var url = endpoint + string.Join('/', streams.Select(Uri.EscapeDataString));
        while (!token.IsCancellationRequested)
        {
            Report(new(FeedPhase.Connecting, "正在连接币安…"));
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.Proxy = _proxy;
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await socket.ConnectAsync(new Uri(url), connectTimeout.Token).ConfigureAwait(false);
                }
                var buffer = new byte[8192];
                while (!token.IsCancellationRequested)
                {
                    var count = 0;
                    ValueWebSocketReceiveResult part;
                    using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    receiveTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                    do
                    {
                        if (count == buffer.Length) throw new InvalidDataException("行情消息超出长度限制。");
                        part = await socket.ReceiveAsync(buffer.AsMemory(count), receiveTimeout.Token).ConfigureAwait(false);
                        if (part.MessageType == WebSocketMessageType.Close) throw new WebSocketException("行情连接已关闭。");
                        if (part.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("行情消息类型异常。");
                        count += part.Count;
                    } while (!part.EndOfMessage);
                    var json = Encoding.UTF8.GetString(buffer, 0, count);
                    using var message = JsonDocument.Parse(json);
                    if (!spot && onFunding is not null && message.RootElement.ValueKind == JsonValueKind.Object
                        && message.RootElement.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.String
                        && stream.GetString()!.EndsWith("@markPrice", StringComparison.Ordinal))
                    {
                        var funding = FundingQuote.ParseCombined(message.RootElement, symbols, DateTimeOffset.UtcNow);
                        if (lastFundingObserved.TryGetValue(funding.Symbol, out var previous) && funding.ObservedAt <= previous) continue;
                        lastFundingObserved[funding.Symbol] = funding.ObservedAt;
                        onFunding(funding);
                        continue;
                    }
                    var ticker = MarketParser.ParseCombined(message.RootElement, symbols, DateTimeOffset.UtcNow);
                    if (lastObserved.TryGetValue(ticker.Symbol, out var last) && ticker.ObservedAt <= last) continue;
                    lastObserved[ticker.Symbol] = ticker.ObservedAt;
                    onTicker(ticker);
                    delaySeconds = 2;
                    Report(new(FeedPhase.Live, "实时行情"));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException or InvalidDataException or JsonException)
            {
                var reason = ex is OperationCanceledException ? "连接超时" : ex.Message;
                Report(new(FeedPhase.Reconnecting, $"{reason} · {delaySeconds} 秒后重连"));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds + Random.Shared.NextDouble()), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            delaySeconds = Math.Min(30, delaySeconds * 2);
        }
    }
    public void Dispose() => _http.Dispose();
}
