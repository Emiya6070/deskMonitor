using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DeskMonitor.Core;

public sealed class UsStockFeed : IDisposable
{
    private readonly HttpClient _http;
    private readonly System.Net.IWebProxy? _proxy;
    private readonly UsStockProvider _provider;
    private readonly AlpacaFeed _feed;
    private readonly string _keyId, _secretKey;
    public UsStockFeed(NetworkProxy proxy, UsStockProvider provider, AlpacaFeed feed, string keyId, string secretKey)
    {
        _provider = provider; _feed = feed; _keyId = keyId; _secretKey = secretKey;
        _proxy = proxy.CreateProxy();
        _http = new HttpClient(new SocketsHttpHandler { Proxy = _proxy, UseProxy = proxy.Mode != ProxyMode.Direct }) { Timeout = TimeSpan.FromSeconds(15) };
    }
    public Task RunAsync(IReadOnlyList<MarketSymbol> markets, Action<Ticker> onTicker, Action<string, FeedStatus> onStatus, CancellationToken token) =>
        markets.Count == 0 ? Task.CompletedTask : _provider == UsStockProvider.Alpaca
            ? RunAlpacaAsync(markets, onTicker, onStatus, token) : RunYahooAsync(markets, onTicker, onStatus, token);

    private async Task RunYahooAsync(IReadOnlyList<MarketSymbol> markets, Action<Ticker> onTicker, Action<string, FeedStatus> onStatus, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            foreach (var market in markets)
            {
                try
                {
                    var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(market.Symbol)}?interval=1m&range=1d";
                    var json = await _http.GetStringAsync(url, token).ConfigureAwait(false);
                    onTicker(UsStockParser.Parse(json, market.Symbol, DateTimeOffset.UtcNow));
                    onStatus(market.Key, new(FeedPhase.Live, "Yahoo Finance 实验源 · 可能延迟"));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
                { onStatus(market.Key, new(FeedPhase.Reconnecting, "Yahoo 更新失败：" + ex.Message)); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }

    private async Task RunAlpacaAsync(IReadOnlyList<MarketSymbol> markets, Action<Ticker> onTicker, Action<string, FeedStatus> onStatus, CancellationToken token)
    {
        var latest = new Dictionary<string, Ticker>(StringComparer.OrdinalIgnoreCase);
        var delay = 2;
        while (!token.IsCancellationRequested)
        {
            foreach (var market in markets) onStatus(market.Key, new(FeedPhase.Connecting, "正在连接 Alpaca…"));
            try
            {
                if (string.IsNullOrWhiteSpace(_keyId) || string.IsNullOrWhiteSpace(_secretKey)) throw new InvalidDataException("尚未配置 Alpaca API Key。");
                foreach (var market in markets)
                {
                    var ticker = await ReadAlpacaSnapshotAsync(market, token).ConfigureAwait(false);
                    latest[market.Symbol] = ticker; onTicker(ticker);
                }
                using var socket = new ClientWebSocket();
                socket.Options.Proxy = _proxy;
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await socket.ConnectAsync(new Uri($"wss://stream.data.alpaca.markets/v2/{FeedName(_feed)}"), connectTimeout.Token).ConfigureAwait(false);
                }
                await ReceiveControlAsync(socket, "connected", token).ConfigureAwait(false);
                await SendAsync(socket, JsonSerializer.Serialize(new { action = "auth", key = _keyId, secret = _secretKey }), token).ConfigureAwait(false);
                await ReceiveControlAsync(socket, "authenticated", token).ConfigureAwait(false);
                await SendAsync(socket, JsonSerializer.Serialize(new { action = "subscribe", trades = markets.Select(m => m.Symbol).ToArray() }), token).ConfigureAwait(false);
                foreach (var market in markets) onStatus(market.Key, new(FeedPhase.Live, $"Alpaca {FeedLabel(_feed)}"));
                delay = 2;
                while (!token.IsCancellationRequested)
                {
                    var json = await ReceiveAsync(socket, token).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Alpaca 消息格式异常。");
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        var type = item.TryGetProperty("T", out var typeNode) ? typeNode.GetString() : null;
                        if (type == "error") throw new InvalidDataException("Alpaca：" + (item.TryGetProperty("msg", out var msg) ? msg.GetString() : "未知错误"));
                        if (type != "t" || !item.TryGetProperty("S", out var symbolNode) || !item.TryGetProperty("p", out var priceNode)
                            || symbolNode.GetString() is not { } symbol || !priceNode.TryGetDecimal(out var price) || price <= 0 || !latest.TryGetValue(symbol, out var previous)) continue;
                        var now = DateTimeOffset.UtcNow;
                        var ticker = previous with { Last = price, High24h = Math.Max(previous.High24h, price), Low24h = Math.Min(previous.Low24h, price), ObservedAt = now, FetchedAt = now };
                        latest[symbol] = ticker; onTicker(ticker);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or JsonException or InvalidDataException or OperationCanceledException)
            {
                foreach (var market in markets) onStatus(market.Key, new(FeedPhase.Reconnecting, "Alpaca 连接失败：" + ex.Message));
                try { await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                delay = Math.Min(30, delay * 2);
            }
        }
    }
    private async Task<Ticker> ReadAlpacaSnapshotAsync(MarketSymbol market, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://data.alpaca.markets/v2/stocks/{Uri.EscapeDataString(market.Symbol)}/snapshot?feed={FeedName(_feed)}");
        request.Headers.Add("APCA-API-KEY-ID", _keyId); request.Headers.Add("APCA-API-SECRET-KEY", _secretKey);
        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseAlpacaSnapshot(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false), market, _feed, DateTimeOffset.UtcNow);
    }
    public static Ticker ParseAlpacaSnapshot(string json, MarketSymbol market, AlpacaFeed feed, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var trade = root.GetProperty("latestTrade");
        var day = root.GetProperty("dailyBar");
        var previous = root.GetProperty("prevDailyBar");
        decimal Number(JsonElement value, string name) => value.TryGetProperty(name, out var node) && node.TryGetDecimal(out var number) && number > 0 ? number : throw new InvalidDataException($"Alpaca 快照缺少 {name}。");
        var last = Number(trade, "p"); var close = Number(previous, "c");
        var high = day.ValueKind == JsonValueKind.Object ? Number(day, "h") : Math.Max(last, close);
        var low = day.ValueKind == JsonValueKind.Object ? Number(day, "l") : Math.Min(last, close);
        return new Ticker(market.Symbol, last, close, Math.Max(high, last), Math.Min(low, last), now, now, MarketKind.UsStock) { SourceOverride = "Alpaca " + FeedLabel(feed) };
    }
    private static async Task SendAsync(ClientWebSocket socket, string json, CancellationToken token) => await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
    private static async Task ReceiveControlAsync(ClientWebSocket socket, string expected, CancellationToken token)
    {
        var json = await ReceiveAsync(socket, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0 ? document.RootElement[0] : throw new InvalidDataException("Alpaca 控制消息格式异常。");
        var message = item.TryGetProperty("msg", out var node) ? node.GetString() : null;
        if (item.TryGetProperty("T", out var type) && type.GetString() == "error") throw new InvalidDataException("Alpaca：" + message);
        if (message != expected) throw new InvalidDataException($"Alpaca 未返回 {expected} 确认。");
    }
    private static async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[65536]; var count = 0;
        ValueWebSocketReceiveResult part;
        do
        {
            if (count == buffer.Length) throw new InvalidDataException("Alpaca 消息过大。");
            part = await socket.ReceiveAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
            if (part.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Alpaca 连接已关闭。");
            count += part.Count;
        } while (!part.EndOfMessage);
        return Encoding.UTF8.GetString(buffer, 0, count);
    }
    private static string FeedName(AlpacaFeed feed) => feed switch { AlpacaFeed.Iex => "iex", AlpacaFeed.DelayedSip => "delayed_sip", AlpacaFeed.Sip => "sip", _ => throw new ArgumentOutOfRangeException(nameof(feed)) };
    private static string FeedLabel(AlpacaFeed feed) => feed switch { AlpacaFeed.Iex => "IEX 实时", AlpacaFeed.DelayedSip => "SIP 延迟 15 分钟", AlpacaFeed.Sip => "SIP 实时", _ => throw new ArgumentOutOfRangeException(nameof(feed)) };
    public void Dispose() => _http.Dispose();
}
