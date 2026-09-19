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
    private readonly Dictionary<string, (ProviderUsageSnapshot? Snapshot, string? Error)> _providerUsage = new();
    private readonly Dictionary<string, Task> _providerTasks = new();
    private readonly Dictionary<string, DateTimeOffset> _providerNext = new();
    private CancellationTokenSource _providerLifetime = new();
    private bool _providerChanging;
    private void RefreshProviderUsage()
    {
        if (!_ready || !IsVisible || _closing || _providerChanging) return;
        foreach (var card in CardsPanel.Children.OfType<ProviderUsageCard>())
        {
            var key = card.Settings.Key;
            if (DateTimeOffset.UtcNow >= _providerNext.GetValueOrDefault(key) && (!_providerTasks.TryGetValue(key, out var task) || task.IsCompleted))
            {
                _providerNext[key] = DateTimeOffset.UtcNow.AddMinutes(card.Settings.Kind == UsageAccountKind.Subscription ? 1 : 5);
                _providerTasks[key] = ReadProviderUsageAsync(card.Settings, _preferences.Proxy, _providerLifetime.Token);
            }
            var state = _providerUsage.GetValueOrDefault(key);
            card.Update(state.Snapshot, state.Error);
        }
    }
    private async Task ReadProviderUsageAsync(ProviderUsageSettings settings, NetworkProxy proxy, CancellationToken token)
    {
        try
        {
            using var client = new ProviderUsageClient(proxy);
            var snapshot = await client.ReadAsync(settings, token);
            if (!token.IsCancellationRequested) _providerUsage[settings.Key] = (snapshot, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { _providerNext.Remove(settings.Key); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or JsonException or OperationCanceledException or ArgumentException or OverflowException)
        {
            if (!token.IsCancellationRequested)
            {
                var previous = _providerUsage.GetValueOrDefault(settings.Key);
                // Never surface raw server bodies, tokens, or local file contents.
                var message = ex is InvalidDataException ? ex.Message : ex is HttpRequestException ? "用量接口请求失败，请检查凭据、权限或网络"
                    : ex is OperationCanceledException ? "读取超时，稍后自动重试" : "无法读取用量，请检查路径、权限或响应格式";
                _providerUsage[settings.Key] = (previous.Snapshot, message);
            }
        }
    }
    private async Task ResetProviderUsageAsync()
    {
        _providerChanging = true;
        _providerLifetime.Cancel();
        await Task.WhenAll(_providerTasks.Values);
        _providerLifetime.Dispose(); _providerLifetime = new();
        _providerTasks.Clear(); _providerNext.Clear(); _providerUsage.Clear();
        _providerChanging = false;
    }
}
