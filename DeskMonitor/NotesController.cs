using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DeskMonitor.Core;

namespace DeskMonitor;

internal sealed record NotePosition(double Left, double Top, double Width, double Height, bool Pinned, bool Hidden);
internal sealed record NotesPreferences
{
    public string ServiceUrl { get; init; } = "";
    public string Token { get; init; } = "";
    public Dictionary<string, NotePosition> Positions { get; init; } = new();
}
internal sealed record NoteEntry(DesktopNote Note, bool Remote)
{
    public override string ToString() => (Remote ? "[服务] " : "[本地] ") + Note.Title;
}

internal sealed class NotesController : IDisposable
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskMonitor");
    private static readonly string SettingsPath = Path.Combine(Folder, "notes-settings.json");
    private readonly NoteStore _local;
    private NotesPreferences _settings;
    private DesktopNote[] _remote = [];
    private readonly Dictionary<string, DesktopNoteWindow> _windows = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource _lifetime = new();
    private NotesManagerWindow? _manager;
    private bool _refreshing, _disposed;
    private int _generation;
    private bool _remoteStale;
    public event Action? Changed;
    public string Status { get; private set; } = "本地便签可直接使用；配置服务后每 30 秒同步。";
    public string ServiceUrl => _settings.ServiceUrl;
    public string Token => _settings.Token;
    public NoteEntry[] Entries => _local.List().Select(n => new NoteEntry(n, false))
        .Concat(_remote.Select(n => new NoteEntry(n, true))).ToArray();
    public NotesController()
    {
        _local = new(Path.Combine(Folder, "notes.json"));
        _settings = File.Exists(SettingsPath) ? JsonSerializer.Deserialize<NotesPreferences>(File.ReadAllText(SettingsPath), NoteProtocol.Json)
            ?? throw new InvalidDataException("便签设置为空。") : new();
        if (_settings.Positions is null || _settings.ServiceUrl is null || _settings.Token is null
            || _settings.Positions.Values.Any(p => p is null || !double.IsFinite(p.Left) || !double.IsFinite(p.Top)
                || !double.IsFinite(p.Width) || !double.IsFinite(p.Height) || p.Width < 250 || p.Height < 200))
            throw new InvalidDataException("便签窗口设置无效。");
        _timer.Tick += async (_, _) => await RefreshAsync();
    }
    public void Start() { Reconcile(); _timer.Start(); _ = RefreshAsync(); }
    public void ShowManager()
    {
        if (_manager is null) { _manager = new(this); _manager.Closed += (_, _) => _manager = null; }
        _manager.Show(); _manager.Activate();
    }
    private string Key(NoteEntry entry) => entry.Remote ? _settings.ServiceUrl.TrimEnd('/') + "/" + entry.Note.Id : "local/" + entry.Note.Id;
    public async Task ConfigureAsync(string url, string token)
    {
        url = url.Trim().TrimEnd('/'); token = token.Trim();
        if (url.Length > 0) { using var client = new NoteServiceClient(url, token); }
        foreach (var pair in _windows)
        {
            var w = pair.Value;
            _settings.Positions[pair.Key] = new(w.Left, w.Top, w.Width, w.Height, w.Topmost, false);
        }
        var candidate = _settings with { ServiceUrl = url, Token = url.Length == 0 ? "" : token };
        Save(candidate); _settings = candidate; _generation++; _remote = []; _remoteStale = false; Reconcile();
        await RefreshAsync();
    }
    private static void Save(NotesPreferences settings)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(SettingsPath + ".tmp", JsonSerializer.Serialize(settings, NoteProtocol.Json));
        File.Move(SettingsPath + ".tmp", SettingsPath, true);
    }
    public async Task RefreshAsync()
    {
        if (_refreshing || _disposed) return;
        if (ServiceUrl.Length == 0) { Status = "未连接服务；本地便签可用。"; Changed?.Invoke(); return; }
        _refreshing = true;
        var address = ServiceUrl; var token = Token;
        var generation = _generation;
        try
        {
            using var client = new NoteServiceClient(address, token);
            var notes = await client.ListAsync(_lifetime.Token);
            if (_disposed || generation != _generation) return;
            _remote = notes; _remoteStale = false; Status = $"同步成功 · {DateTime.Now:HH:mm:ss} · {notes.Length} 张服务便签"; Reconcile();
        }
        catch (Exception ex) when (IsExpectedError(ex))
        {
            if (_disposed || generation != _generation) return;
            Status = "同步失败，保留上次内容：" + ex.Message;
            _remoteStale = true;
            foreach (var entry in Entries.Where(e => e.Remote))
                if (_windows.TryGetValue(Key(entry), out var window)) window.SetStatus("同步失败 · 内容可能已过期");
        }
        finally
        {
            _refreshing = false;
            if (!_disposed)
            {
                Changed?.Invoke();
                if (generation != _generation) _ = RefreshAsync();
            }
        }
    }
    public void Edit(NoteEntry? entry, bool remote = false)
    {
        remote = entry?.Remote ?? remote;
        if (remote && ServiceUrl.Length == 0) { MessageBox.Show("请先配置服务地址和访问令牌。", "服务便签"); return; }
        var id = entry?.Note.Id ?? Guid.NewGuid().ToString("N");
        var address = ServiceUrl; var token = Token;
        var editor = new NoteEditorWindow(entry?.Note, remote, async input =>
        {
            if (_disposed) throw new InvalidOperationException("便签管理器已关闭。");
            if (remote)
            {
                if (address != ServiceUrl || token != Token) throw new InvalidDataException("服务配置已更改，请关闭编辑窗口后重试。");
                using var client = new NoteServiceClient(address, token);
                var note = await client.PutAsync(id, input, _lifetime.Token);
                if (_disposed || address != ServiceUrl || token != Token) return;
                _generation++;
                _remote = _remote.Where(n => n.Id != id).Append(note).ToArray();
            }
            else _local.Put(id, input);
            Reconcile(); Changed?.Invoke();
        });
        editor.Show();
    }
    public async Task DeleteAsync(NoteEntry entry)
    {
        if (MessageBox.Show($"删除“{entry.Note.Title}”？此操作会删除{(entry.Remote ? "服务端" : "本地")}保存的全文。", "删除便签",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (entry.Remote)
        {
            var address = ServiceUrl; var token = Token;
            using var client = new NoteServiceClient(address, token); await client.DeleteAsync(entry.Note, _lifetime.Token);
            if (_disposed || address != ServiceUrl || token != Token) return;
            _generation++;
            _remote = _remote.Where(n => n.Id != entry.Note.Id).ToArray();
        }
        else _local.Delete(entry.Note.Id, entry.Note.Revision);
        Reconcile(); Changed?.Invoke();
    }
    public void ShowNote(NoteEntry entry)
    {
        var key = Key(entry);
        if (_settings.Positions.TryGetValue(key, out var position)) _settings.Positions[key] = position with { Hidden = false };
        EnsureWindow(entry, true);
    }
    private void Reconcile()
    {
        var entries = Entries;
        foreach (var key in _windows.Keys.Where(k => !entries.Any(e => Key(e) == k)).ToArray())
        { var window = _windows[key]; _windows.Remove(key); window.Close(); }
        foreach (var entry in entries) EnsureWindow(entry, false);
        Changed?.Invoke();
    }
    private void EnsureWindow(NoteEntry entry, bool force)
    {
        var key = Key(entry);
        var status = entry.Remote ? (_remoteStale ? "同步失败 · 内容可能已过期" : "服务便签") : "本地便签";
        if (_windows.TryGetValue(key, out var existing)) { existing.Update(entry.Note, status); if (force) existing.Activate(); return; }
        var position = _settings.Positions.GetValueOrDefault(key);
        if (!force && position?.Hidden == true) return;
        DesktopNoteWindow? window = null;
        window = new(entry.Note, entry.Remote, () => Edit(Entries.Single(e => Key(e) == key)), () =>
        {
            Remember(key, window!, true); _windows.Remove(key); window!.Close();
        });
        var area = SystemParameters.WorkArea;
        window.Width = Math.Min(position?.Width ?? 330, area.Width); window.Height = Math.Min(position?.Height ?? 300, area.Height);
        window.Left = Math.Clamp(position?.Left ?? area.Left + 40 + _windows.Count % 8 * 30, area.Left, Math.Max(area.Left, area.Right - window.Width));
        window.Top = Math.Clamp(position?.Top ?? area.Top + 60 + _windows.Count % 8 * 30, area.Top, Math.Max(area.Top, area.Bottom - window.Height));
        window.Topmost = position?.Pinned ?? false;
        window.SetStatus(status);
        window.Closing += (_, _) =>
        {
            if (_windows.ContainsKey(key)) { Remember(key, window, !_disposed); _windows.Remove(key); }
        };
        _windows.Add(key, window); window.Show();
    }
    private void Remember(string key, DesktopNoteWindow window, bool hidden)
    {
        _settings.Positions[key] = new(window.Left, window.Top, window.Width, window.Height, window.Topmost, hidden);
        try { Save(_settings); }
        catch (Exception ex) when (IsExpectedError(ex)) { MessageBox.Show("便签位置未保存：" + ex.Message, "便签"); }
    }
    public static bool IsExpectedError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
        or HttpRequestException or OperationCanceledException or NoteConflictException;
    public void Dispose()
    {
        _disposed = true; _timer.Stop(); _lifetime.Cancel(); _manager?.Close();
        foreach (var window in _windows.Values.ToArray()) window.Close();
        _lifetime.Dispose();
    }
}
