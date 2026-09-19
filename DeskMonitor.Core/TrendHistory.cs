namespace DeskMonitor.Core;

// Local observations, not exchange candles. At most one fresh sample per five seconds.
public sealed class TrendHistory
{
    private const int MaxSpanMinutes = 7 * 24 * 60;
    private const int MaxSamples = MaxSpanMinutes * 60 / 5 + 1;
    public static bool IsSupportedSpan(int minutes) => minutes is 2 or 5 or 15 or 60 or 240 or 1440 or MaxSpanMinutes;
    private readonly Queue<Ticker> _samples = new();
    private DateTimeOffset _lastObserved;
    public void Add(Ticker ticker, DateTimeOffset now)
    {
        if (ticker.IsStale(now) || ticker.ObservedAt > now) return;
        if (_samples.Count > 0 && ticker.ObservedAt - _lastObserved < TimeSpan.FromSeconds(5)) return;
        _samples.Enqueue(ticker);
        _lastObserved = ticker.ObservedAt;
        Trim(now);
    }
    public Ticker[] Window(DateTimeOffset now, int minutes)
    {
        if (!IsSupportedSpan(minutes)) throw new ArgumentOutOfRangeException(nameof(minutes));
        Trim(now);
        return _samples.Where(x => x.ObservedAt >= now.AddMinutes(-minutes) && x.ObservedAt <= now).ToArray();
    }
    private void Trim(DateTimeOffset now)
    {
        while (_samples.Count > 0 && (_samples.Peek().ObservedAt < now.AddMinutes(-MaxSpanMinutes) || _samples.Count > MaxSamples)) _samples.Dequeue();
    }
}
