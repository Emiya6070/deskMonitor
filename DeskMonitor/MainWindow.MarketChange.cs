using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeskMonitor.Core;

namespace DeskMonitor;
public partial class MainWindow
{
    private readonly Dictionary<string, (MarketChangeReference? Reference, string? Error)> _marketChanges = new();
    private readonly Dictionary<string, DateTimeOffset> _changeRequested = new();
    private readonly Dictionary<string, Task> _changeTasks = new();
    private readonly SemaphoreSlim _changeSlots = new(4);
    private CancellationTokenSource _changeLifetime = new();
    private bool _changeChanging;

    private void RefreshMarketChanges()
    {
        var minutes = _preferences.CompactChangeMinutes;
        if (!_ready || _closing || _changeChanging || !IsVisible || !_preferences.CompactMarkets || minutes is 0 or 1440) return;
        foreach (var market in _preferences.Markets!.Where(m => m.Kind != MarketKind.UsStock))
        {
            Ticker? ticker;
            lock (_gate) _latest.TryGetValue(market.Key, out ticker);
            if (ticker is null || ticker.IsStale(DateTimeOffset.UtcNow)) continue;
            var target = MarketChangeReference.ReferenceTime(ticker.ObservedAt, minutes);
            if (_changeRequested.GetValueOrDefault(market.Key) == target
                || (_changeTasks.TryGetValue(market.Key, out var task) && !task.IsCompleted)) continue;
            // At most one read per symbol/minute, including failures. No extra retry loop.
            _changeRequested[market.Key] = target;
            _changeTasks[market.Key] = ReadMarketChangeAsync(market, minutes, ticker.ObservedAt, _changeLifetime.Token);
        }
    }
    private async Task ReadMarketChangeAsync(MarketSymbol market, int minutes, DateTimeOffset asOf, CancellationToken token)
    {
        var entered = false;
        try
        {
            await _changeSlots.WaitAsync(token); entered = true;
            var reference = await _feed.GetChangeReferenceAsync(market, minutes, asOf, token);
            if (!token.IsCancellationRequested) _marketChanges[market.Key] = (reference, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            if (!token.IsCancellationRequested)
                _marketChanges[market.Key] = (null, ex is InvalidDataException ? ex.Message : "历史价格读取失败，下一分钟更新");
        }
        finally { if (entered) _changeSlots.Release(); }
    }
    private async Task ResetMarketChangesAsync()
    {
        _changeChanging = true;
        _changeLifetime.Cancel();
        await Task.WhenAll(_changeTasks.Values);
        _changeLifetime.Dispose(); _changeLifetime = new();
        _changeTasks.Clear(); _changeRequested.Clear(); _marketChanges.Clear();
        // ApplySettingsAsync resumes reads only after applying the new settings/feed.
    }
}
