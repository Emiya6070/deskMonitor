using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using DeskMonitor.Core;
using Forms = System.Windows.Forms;

namespace DeskMonitor;
public partial class MainWindow : Window
{
    private BinanceFeed _feed = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly object _gate = new();
    private readonly Dictionary<string, Ticker> _latest = new();
    private readonly Dictionary<MarketKind, FeedStatus> _statuses = new();
    private readonly Dictionary<string, TrendHistory> _history = new();
    private readonly Dictionary<MarketKind, IReadOnlyList<MarketSymbol>> _catalogs = new();
    private readonly Forms.NotifyIcon _tray;
    private readonly System.Drawing.Icon _trayIcon;
    private Preferences _preferences = Preferences.Normalize(new());
    private CancellationTokenSource? _subscription;
    private Task _streamTask = Task.CompletedTask;
    private bool _closing, _ready, _settingsOpen;
    private SettingsWindow? _settings;
    private UsageCard? _usageCard;
    private readonly Dictionary<string, FundingQuote> _funding = new();
    private CodexUsage? _usage;
    private string? _usageError;
    private Task _usageTask = Task.CompletedTask;
    private CancellationTokenSource? _usageRequest;
    private DateTimeOffset _nextUsageRead;
    private SnapDrag? _snapDrag;
    private DockedCorners _dockedCorners;
    private bool _updatingCorners;

    public MainWindow()
    {
        InitializeComponent();
        LocationChanged += (_, _) => UpdateDockedCorners();
        SizeChanged += (_, _) => UpdateDockedCorners();
        _timer.Tick += (_, _) => RenderLatest();
        _trayIcon = CreateTrayIcon();
        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("显示挂件", null, (_, _) => Dispatcher.Invoke(ShowWidget));
        trayMenu.Items.Add("设置与币种管理", null, (_, _) => Dispatcher.Invoke(ShowSettings));
        trayMenu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Close));
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "DeskMonitor · 右键管理币种和样式", Visible = true, ContextMenuStrip = trayMenu };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWidget);
        var menu = new ContextMenu();
        AddMenu(menu, "设置与币种管理", ShowSettings);
        AddMenu(menu, "刷新 Codex 用量", () => StartUsageRefresh(true));
        AddMenu(menu, "收起到托盘", () => HideToTray(this, new RoutedEventArgs()));
        AddMenu(menu, "退出", Close);
        ContextMenu = menu;
        IsVisibleChanged += (_, _) => { if (IsVisible && _ready) { RenderLatest(); _timer.Start(); } else { _timer.Stop(); _usageRequest?.Cancel(); } };
    }
    private static void AddMenu(ContextMenu menu, string title, Action action)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready) return;
        try { _preferences = Preferences.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { MessageBox.Show(this, $"无法读取设置，将使用默认设置。\n{ex.Message}", "DeskMonitor", MessageBoxButton.OK, MessageBoxImage.Warning); }
        _feed.Dispose(); _feed = new BinanceFeed(_preferences.Proxy);
        var area = SystemParameters.WorkArea;
        Left = _preferences.Left ?? area.Right - Width - 28;
        Top = _preferences.Top ?? area.Top + 60;
        ApplyLayout(false);
        BuildCards();
        _ready = true;
        _timer.Start();
        await RestartStreamsAsync();
    }
    private void BuildCards()
    {
        CardsPanel.Children.Clear();
        _usageCard = null;
        foreach (var key in _preferences.CardOrder)
        {
            if (key == Preferences.CodexCardKey)
            {
                if (_preferences.ShowCodexUsage)
                {
                    _usageCard = new UsageCard(_preferences.Style, _preferences.TextScale, _preferences.NumberScale, _preferences.MonospaceNumbers, _preferences.SmallCornerRadius, _preferences.UsageExtraHeight);
                    _usageCard.RefreshRequested += (_, _) => StartUsageRefresh(true);
                    _usageCard.SizeChanged += (_, _) => { if (!_preferences.AllowResize) FitUsageHeight(); };
                    CardsPanel.Children.Add(_usageCard);
                }
                continue;
            }
            var market = _preferences.Markets!.Single(m => m.Key == key);
            var card = new MarketCard(market, _preferences.Style, _preferences.TrendMinutes.GetValueOrDefault(market.Key, 2), _preferences.TextScale, _preferences.NumberScale, _preferences.MonospaceNumbers, _preferences.ShowTrends, _preferences.SmallCornerRadius);
            card.TrendSpanChanged += (_, _) =>
            {
                var spans = new Dictionary<string, int>(_preferences.TrendMinutes) { [market.Key] = card.TrendMinutes };
                _preferences = _preferences with { TrendMinutes = spans };
                SavePreferences();
                RenderLatest();
            };
            CardsPanel.Children.Add(card);
        }
        foreach (var key in _history.Keys.Where(key => !_preferences.Markets!.Any(m => m.Key == key)).ToArray()) _history.Remove(key);
        RenderLatest();
    }
    private async Task RestartStreamsAsync()
    {
        _subscription?.Cancel();
        await _streamTask;
        _subscription?.Dispose();
        if (_closing) return;
        var subscription = new CancellationTokenSource();
        _subscription = subscription;
        lock (_gate)
        {
            foreach (var key in _latest.Keys.Where(key => !_preferences.Markets!.Any(m => m.Key == key)).ToArray()) _latest.Remove(key);
            foreach (var key in _funding.Keys.Where(key => !_preferences.Markets!.Any(m => m.Key == key)).ToArray()) _funding.Remove(key);
            _statuses.Clear();
        }
        _streamTask = _feed.RunAsync(_preferences.Markets!,
            ticker => { lock (_gate) { if (_subscription == subscription) _latest[ticker.Key] = ticker; } },
            (kind, status) => { lock (_gate) { if (_subscription == subscription) _statuses[kind] = status; } }, subscription.Token,
            funding => { lock (_gate) { if (_subscription == subscription) _funding[funding.Key] = funding; } });
    }
    private void RenderLatest()
    {
        if (_closing) return;
        StartUsageRefresh(false);
        _usageCard?.Update(_usage, _usageError, !_usageTask.IsCompleted);
        foreach (var card in CardsPanel.Children.OfType<MarketCard>())
        {
            Ticker? ticker;
            FundingQuote? funding;
            FeedStatus status;
            lock (_gate)
            {
                _latest.TryGetValue(card.Market.Key, out ticker);
                _funding.TryGetValue(card.Market.Key, out funding);
                status = _statuses.GetValueOrDefault(card.Market.Kind) ?? new(FeedPhase.Connecting, "等待连接…");
            }
            if (!_history.TryGetValue(card.Market.Key, out var history)) _history[card.Market.Key] = history = new();
            var now = DateTimeOffset.UtcNow;
            if (ticker is not null) history.Add(ticker, now);
            card.Update(ticker, status, card.NeedsTrendRefresh ? history.Window(now, card.TrendMinutes) : null, funding);
        }
    }
    private void StartUsageRefresh(bool force)
    {
        if (_closing || !_ready || !IsVisible || !_preferences.ShowCodexUsage || !_usageTask.IsCompleted
            || (!force && DateTimeOffset.UtcNow < _nextUsageRead)) return;
        _nextUsageRead = DateTimeOffset.UtcNow.AddMinutes(1);
        _usageRequest?.Dispose();
        _usageRequest = new CancellationTokenSource();
        _usageTask = ReadUsageAsync(_usageRequest.Token);
    }
    private async Task ReadUsageAsync(CancellationToken token)
    {
        try
        {
            var usage = await CodexUsageClient.ReadAsync(_preferences.CodexExecutable, token);
            if (!token.IsCancellationRequested) { _usage = usage; _usageError = null; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { _nextUsageRead = DateTimeOffset.MinValue; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or Win32Exception or InvalidOperationException)
        {
            if (!token.IsCancellationRequested) { _usage = null; _usageError = ex.Message; }
        }
        finally
        {
            if (token.IsCancellationRequested) _nextUsageRead = DateTimeOffset.MinValue;
            if (!_closing) _usageCard?.Update(_usage, _usageError, false);
        }
    }
    private void ApplyLayout(bool resetSize)
    {
        ThemeManager.Apply(_preferences.Skin);
        _timer.Interval = TimeSpan.FromSeconds(_preferences.RefreshSeconds);
        var small = _preferences.Style == CardStyle.Small;
        Header.Visibility = small || _preferences.HideHeader ? Visibility.Collapsed : Visibility.Visible;
        Frame.Padding = small ? new Thickness(5, 5, 5, 0) : new Thickness(12, 10, 12, 0);
        Topmost = _preferences.Pinned;
        PinButton.Foreground = Topmost ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
        PinButton.ToolTip = Topmost ? "取消置顶" : "置顶显示";
        ResizeMode = _preferences.AllowResize ? ResizeMode.CanResize : ResizeMode.NoResize;
        WindowChrome.GetWindowChrome(this).ResizeBorderThickness = new Thickness(_preferences.AllowResize ? 6 : 0);
        var preset = WidgetLayout.Preset(_preferences.Style, _preferences.Markets!.Length, _preferences.ShowCodexUsage, _preferences.TextScale, _preferences.NumberScale, _preferences.HideHeader, _preferences.Markets.Count(m => m.Kind != MarketKind.Spot));
        var work = WindowPlacement.WorkSize(this);
        MinWidth = (small ? 220 : _preferences.Style == CardStyle.Medium ? 300 : 340) * _preferences.TextScale;
        MinHeight = (_preferences.Markets.Length == 0 ? (small ? 50 : _preferences.HideHeader ? 70 : 114) : small ? 66 : _preferences.HideHeader ? 120 : 180) * _preferences.TextScale;
        MaxWidth = Math.Max(MinWidth, work.Width);
        MaxHeight = Math.Max(MinHeight, work.Height);
        Width = Math.Clamp(!resetSize && _preferences.AllowResize ? _preferences.Width ?? preset.Width : preset.Width, MinWidth, MaxWidth);
        Height = Math.Clamp(!resetSize && _preferences.AllowResize ? _preferences.Height ?? preset.Height : preset.Height, MinHeight, Math.Max(MinHeight, MaxHeight - 24));
        WindowPlacement.EnsureVisible(this);
        UpdateDockedCorners();
    }
    private void FitUsageHeight()
    {
        if (_usageCard is null || _closing) return;
        // Use measured content rather than reserving two quota windows unconditionally.
        var preset = WidgetLayout.Preset(_preferences.Style, _preferences.Markets!.Length, false, _preferences.TextScale, _preferences.NumberScale, _preferences.HideHeader, _preferences.Markets.Count(m => m.Kind != MarketKind.Spot));
        var height = preset.Height + (_usageCard.ActualHeight + 8) * _preferences.TextScale;
        Height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, MaxHeight - 24));
        WindowPlacement.EnsureVisible(this);
        UpdateDockedCorners();
    }
    private void UpdateDockedCorners()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || _updatingCorners || _closing) return;
        var corners = _preferences.SnapToEdges ? WindowPlacement.DockedCornersFor(this) : DockedCorners.None;
        if (corners == _dockedCorners) return;
        _updatingCorners = true;
        try
        {
            var chrome = WindowChrome.GetWindowChrome(this);
            // DWM only supports uniform corners. Use a native region only while docked
            // to a corner; returning to free placement restores the smooth DWM outline.
            var preference = corners == DockedCorners.None ? 2 : 1;
            Marshal.ThrowExceptionForHR(WindowPlacement.DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int)));
            chrome.CornerRadius = corners == DockedCorners.None ? new CornerRadius(0) : new CornerRadius(
                corners.HasFlag(DockedCorners.TopLeft) ? 0 : 8, corners.HasFlag(DockedCorners.TopRight) ? 0 : 8,
                corners.HasFlag(DockedCorners.BottomRight) ? 0 : 8, corners.HasFlag(DockedCorners.BottomLeft) ? 0 : 8);
            chrome.GlassFrameThickness = new Thickness(corners == DockedCorners.None ? 1 : 0);
            _dockedCorners = corners;
        }
        finally { _updatingCorners = false; }
    }
    private void OpenSettings(object sender, RoutedEventArgs e) => ShowSettings();
    private void ShowSettings()
    {
        if (!_ready || _closing) return;
        if (_settingsOpen) { _settings?.Activate(); return; }
        ShowWidget();
        _settingsOpen = true;
        try
        {
            _settings = new SettingsWindow(_preferences, _catalogs, ApplySettingsAsync) { Owner = this, Topmost = Topmost };
            _settings.ShowDialog();
        }
        finally { _settings = null; _settingsOpen = false; }
    }
    private async Task ApplySettingsAsync(Preferences candidate, bool startup)
    {
        candidate = Preferences.Normalize(candidate);
        var oldUsageExtraHeight = _preferences.UsageExtraHeight;
        var oldStartup = StartupRegistration.IsEnabled();
        var marketsChanged = !_preferences.Markets!.Select(x => x.Key).Order().SequenceEqual(candidate.Markets!.Select(x => x.Key).Order());
        var usageChanged = candidate.ShowCodexUsage != _preferences.ShowCodexUsage || candidate.CodexExecutable != _preferences.CodexExecutable;
        var proxyChanged = candidate.Proxy != _preferences.Proxy;
        var reset = candidate.HideHeader != _preferences.HideHeader || candidate.Style != _preferences.Style || candidate.Markets!.Length != _preferences.Markets!.Length || candidate.ShowCodexUsage != _preferences.ShowCodexUsage || candidate.TextScale != _preferences.TextScale || candidate.NumberScale != _preferences.NumberScale || !candidate.AllowResize;
        candidate = candidate with { Left = Left, Top = Top, Width = reset ? null : Width, Height = reset ? null : Height };
        if (startup != oldStartup) StartupRegistration.SetEnabled(startup);
        try { candidate.Save(); }
        catch { if (startup != oldStartup) StartupRegistration.SetEnabled(oldStartup); throw; }
        if (usageChanged)
        {
            _usageRequest?.Cancel();
            await _usageTask;
            _usage = null; _usageError = null; _nextUsageRead = DateTimeOffset.MinValue;
        }
        if (proxyChanged)
        {
            _subscription?.Cancel(); await _streamTask;
            _feed.Dispose(); _feed = new BinanceFeed(candidate.Proxy);
            _catalogs.Clear();
            lock (_gate) { _latest.Clear(); _funding.Clear(); }
            _history.Clear();
        }
        _preferences = candidate;
        ApplyLayout(reset);
        BuildCards();
        if (reset || candidate.UsageExtraHeight != oldUsageExtraHeight)
        {
            UpdateLayout();
            FitUsageHeight();
        }
        if (marketsChanged || proxyChanged) await RestartStreamsAsync();
        SavePreferences();
    }
    private void TogglePin(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        _preferences = _preferences with { Pinned = Topmost };
        PinButton.Foreground = Topmost ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
        PinButton.ToolTip = Topmost ? "取消置顶" : "置顶显示";
        SavePreferences();
    }
    private void HideToTray(object sender, RoutedEventArgs e) { SavePreferences(); Hide(); }
    internal void ShowWidget() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void ExitApp(object sender, RoutedEventArgs e) => Close();
    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        for (var parent = e.OriginalSource as DependencyObject; parent is not null && parent != this;
             parent = parent is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(parent))
            if (parent is ButtonBase or TextBoxBase or Selector or ScrollBar or Thumb) return;
        e.Handled = true;
        DragMove();
    }
    private void SavePreferences()
    {
        if (!_ready || _closing) return;
        _preferences = _preferences with { Left = Left, Top = Top, Width = Width, Height = Height };
        try { _preferences.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "设置保存失败：" + ex.Message, "DeskMonitor", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        SavePreferences();
        _closing = true;
        _settings?.Close();
        _timer.Stop();
        _subscription?.Cancel();
        _usageRequest?.Cancel();
        await _usageTask;
        _usageRequest?.Dispose();
        await _streamTask;
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose(); _trayIcon.Dispose(); _subscription?.Dispose(); _feed.Dispose();
        Close();
    }
    private void WindowSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var corner = 2;
        Marshal.ThrowExceptionForHR(WindowPlacement.DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int)));
        // DWMWA_BORDER_COLOR / DWMWA_COLOR_NONE: suppress the system's light outline,
        // including the top edge when the widget touches the monitor work area.
        var noBorder = unchecked((int)0xFFFFFFFE);
        Marshal.ThrowExceptionForHR(WindowPlacement.DwmSetWindowAttribute(handle, 34, ref noBorder, sizeof(int)));
        var dark = 1;
        WindowPlacement.DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        HwndSource.FromHwnd(handle).AddHook(WindowMessage);
        _dockedCorners = DockedCorners.None;
        UpdateDockedCorners();
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0231 && _preferences.SnapToEdges) _snapDrag = WindowPlacement.BeginSnapDrag(hwnd);
        if (message == 0x0216 && _preferences.SnapToEdges)
        {
            WindowPlacement.SnapMovingRect(hwnd, lParam, _snapDrag ?? throw new InvalidOperationException("吸附拖动尚未开始。"));
            handled = true; return new IntPtr(1);
        }
        if (message == 0x0232) { _snapDrag = null; Dispatcher.BeginInvoke(SavePreferences, DispatcherPriority.Background); }
        if (message == App.ShowMessage) { ShowWidget(); handled = true; }
        return IntPtr.Zero;
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    internal static System.Drawing.Icon CreateTrayIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(System.Drawing.Color.Transparent);
        using var background = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(28, 42, 31));
        graphics.FillEllipse(background, 1, 1, 30, 30);
        using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(187, 229, 140), 3);
        graphics.DrawLines(pen, new System.Drawing.Point[] { new(7, 21), new(13, 15), new(18, 18), new(25, 10) });
        var handle = bitmap.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }
}
