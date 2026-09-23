using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
    private UsStockFeed _stockFeed = new(new NetworkProxy(), UsStockProvider.Alpaca, AlpacaFeed.Iex, "", "");
    private HttpClient _http = CreateHttpClient(new NetworkProxy());
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly object _gate = new();
    private readonly Dictionary<string, Ticker> _latest = new();
    private readonly Dictionary<MarketKind, FeedStatus> _statuses = new();
    private readonly Dictionary<string, FeedStatus> _stockStatuses = new();
    private readonly Dictionary<string, DateTimeOffset> _nextHttpRead = new();
    private readonly Dictionary<string, Task> _httpTasks = new();
    private readonly Dictionary<string, (string? Message, string? Error, DateTimeOffset? Updated)> _messages = new();
    private readonly Dictionary<string, TrendHistory> _history = new();
    private readonly Dictionary<MarketKind, IReadOnlyList<MarketSymbol>> _catalogs = new();
    private readonly Forms.NotifyIcon _tray;
    private readonly System.Drawing.Icon _trayIcon;
    private Preferences _preferences = Preferences.Normalize(new());
    private CancellationTokenSource? _subscription;
    private Task _streamTask = Task.CompletedTask;
    private bool _closing, _closeReady, _ready, _settingsOpen;
    private SettingsWindow? _settings;
    private UsageCard? _usageCard;
    private readonly Dictionary<string, FundingQuote> _funding = new();
    private CodexUsage? _usage;
    private string? _usageError;
    private Task _usageTask = Task.CompletedTask;
    private Task _hideTask = Task.CompletedTask;
    private CancellationTokenSource? _hideRequest;
    private CancellationTokenSource? _usageRequest;
    private DateTimeOffset _nextUsageRead;
    private SnapDrag? _snapDrag;
    private DockedCorners _dockedCorners;
    private bool _updatingCorners;
    private readonly Action<Preferences> _savePreferences;

    public MainWindow() : this(preferences => preferences.Save()) { }
    internal MainWindow(Action<Preferences> savePreferences)
    {
        _savePreferences = savePreferences;
        InitializeComponent();
        CardsPanel.SizeChanged += (_, _) =>
        {
            if (WidgetLayout.ShouldAutoFit(_preferences.AllowResize, _preferences.LockCustomSize) && !_closing)
                Dispatcher.BeginInvoke(new Action(FitUsageHeight), DispatcherPriority.Background);
        };
        DateProgressHost.SizeChanged += (_, _) =>
        {
            if (WidgetLayout.ShouldAutoFit(_preferences.AllowResize, _preferences.LockCustomSize) && !_closing)
                Dispatcher.BeginInvoke(new Action(FitUsageHeight), DispatcherPriority.Background);
        };
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
        var pinItem = new MenuItem { Header = "置顶显示", IsCheckable = true };
        pinItem.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(Topmost)) { Source = this, Mode = BindingMode.OneWay });
        pinItem.Click += TogglePin;
        menu.Items.Add(pinItem);
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
        _stockFeed.Dispose(); _stockFeed = new(_preferences.Proxy, _preferences.UsStockProvider, _preferences.AlpacaFeed, _preferences.AlpacaKeyId, _preferences.AlpacaSecretKey);
        _http.Dispose(); _http = CreateHttpClient(_preferences.Proxy);
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
            var providerUsage = _preferences.ProviderUsages.FirstOrDefault(x => x.Key == key);
            if (providerUsage is not null)
            {
                if (providerUsage.Enabled)
                {
                    var usageCard = new ProviderUsageCard(providerUsage, _preferences);
                    usageCard.SizeChanged += (_, _) => { if (WidgetLayout.ShouldAutoFit(_preferences.AllowResize, _preferences.LockCustomSize)) FitUsageHeight(); };
                    usageCard.Tag = key;
                    CardsPanel.Children.Add(usageCard);
                }
                continue;
            }
            if (key == Preferences.CodexCardKey)
            {
                if (_preferences.ShowCodexUsage)
                {
                    _usageCard = new UsageCard(_preferences.Style, _preferences.TextScale, _preferences.NumberScale, _preferences.MonospaceNumbers, _preferences.SmallCornerRadius, _preferences.UsageExtraHeight);
                    _usageCard.RefreshRequested += (_, _) => StartUsageRefresh(true);
                    _usageCard.SizeChanged += (_, _) => { if (WidgetLayout.ShouldAutoFit(_preferences.AllowResize, _preferences.LockCustomSize)) FitUsageHeight(); };
                    _usageCard.Tag = key;
                    CardsPanel.Children.Add(_usageCard);
                }
                continue;
            }
            var source = _preferences.HttpSources.FirstOrDefault(s => s.Key == key);
            if (source is not null)
            {
                CardsPanel.Children.Add(new HttpMessageCard(source, _preferences.Style, _preferences.TextScale, _preferences.SmallCornerRadius) { Tag = key });
                continue;
            }
            var market = _preferences.Markets!.Single(m => m.Key == key);
            var card = new MarketCard(market, _preferences.Style, _preferences.TrendMinutes.GetValueOrDefault(market.Key, 2), _preferences.TextScale, _preferences.NumberScale, _preferences.MonospaceNumbers, _preferences.ShowTrends, _preferences.SmallCornerRadius, _preferences.MarketHeightAdjustment);
            card.TrendSpanChanged += (_, _) =>
            {
                var spans = new Dictionary<string, int>(_preferences.TrendMinutes) { [market.Key] = card.TrendMinutes };
                _preferences = _preferences with { TrendMinutes = spans };
                SavePreferences();
                RenderLatest();
            };
            card.Tag = key;
            CardsPanel.Children.Add(card);
        }
        CardGrouping.Apply(CardsPanel, _preferences.CardPlacements, _preferences.TextScale);
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
            foreach (var key in _stockStatuses.Keys.Where(key => !_preferences.Markets!.Any(m => m.Key == key)).ToArray()) _stockStatuses.Remove(key);
            _statuses.Clear();
        }
        var cryptoTask = _feed.RunAsync(_preferences.Markets!.Where(m => m.Kind != MarketKind.UsStock).ToArray(),
            ticker => { lock (_gate) { if (_subscription == subscription) _latest[ticker.Key] = ticker; } },
            (kind, status) => { lock (_gate) { if (_subscription == subscription) _statuses[kind] = status; } }, subscription.Token,
            funding => { lock (_gate) { if (_subscription == subscription) _funding[funding.Key] = funding; } });
        var stockTask = _stockFeed.RunAsync(_preferences.Markets!.Where(m => m.Kind == MarketKind.UsStock).ToArray(),
            ticker => { lock (_gate) { if (_subscription == subscription) _latest[ticker.Key] = ticker; } },
            (key, status) => { lock (_gate) { if (_subscription == subscription) _stockStatuses[key] = status; } }, subscription.Token);
        _streamTask = Task.WhenAll(cryptoTask, stockTask);
    }
    private void RenderLatest()
    {
        if (_closing) return;
        (DateProgressHost.Content as DateProgressColumn)?.Refresh();
        StartUsageRefresh(false);
        RefreshProviderUsage();
        StartHttpRefreshes();
        _usageCard?.Update(_usage, _usageError, !_usageTask.IsCompleted);
        foreach (var card in CardsPanel.Children.OfType<HttpMessageCard>())
        {
            var state = _messages.GetValueOrDefault(card.Source.Key);
            card.Update(state.Message, state.Error, state.Updated, _httpTasks.TryGetValue(card.Source.Key, out var task) && !task.IsCompleted);
        }
        foreach (var card in CardsPanel.Children.OfType<MarketCard>())
        {
            Ticker? ticker;
            FundingQuote? funding;
            FeedStatus status;
            lock (_gate)
            {
                _latest.TryGetValue(card.Market.Key, out ticker);
                _funding.TryGetValue(card.Market.Key, out funding);
                status = card.Market.Kind == MarketKind.UsStock
                    ? _stockStatuses.GetValueOrDefault(card.Market.Key) ?? new(FeedPhase.Connecting, "等待 HTTP 行情…")
                    : _statuses.GetValueOrDefault(card.Market.Kind) ?? new(FeedPhase.Connecting, "等待连接…");
            }
            if (!_history.TryGetValue(card.Market.Key, out var history)) _history[card.Market.Key] = history = new();
            var now = DateTimeOffset.UtcNow;
            if (ticker is not null) history.Add(ticker, now);
            card.Update(ticker, status, card.NeedsTrendRefresh ? history.Window(now, card.TrendMinutes) : null, funding);
        }
    }
    private void StartHttpRefreshes()
    {
        if (_closing || !_ready || !IsVisible || _subscription is null) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var source in _preferences.HttpSources)
            if (now >= _nextHttpRead.GetValueOrDefault(source.Key) && (!_httpTasks.TryGetValue(source.Key, out var task) || task.IsCompleted))
            {
                _nextHttpRead[source.Key] = now.AddSeconds(source.RefreshSeconds);
                _httpTasks[source.Key] = ReadMessageAsync(source, _subscription.Token);
            }
    }
    private async Task ReadMessageAsync(HttpMessageSource source, CancellationToken token)
    {
        try
        {
            var body = await _http.GetStringAsync(source.Url, token);
            _messages[source.Key] = (HttpMessageParser.Parse(body, source.JsonPath), null, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
        {
            var previous = _messages.GetValueOrDefault(source.Key);
            _messages[source.Key] = (previous.Message, ex.Message, previous.Updated);
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
            var executable = _preferences.CodexExecutable;
            // Process startup and tree termination can block; keep them off the UI thread.
            var range = _preferences.CodexTokenRange;
            var excludedBillingModels = _preferences.CodexExcludedBillingModels;
            var usage = await Task.Run(() => CodexUsageClient.ReadAsync(executable, range, token, excludedBillingModels), token);
            if (!token.IsCancellationRequested) { _usage = usage; _usageError = null; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { _nextUsageRead = DateTimeOffset.MinValue; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or Win32Exception or InvalidOperationException)
        {
            if (!token.IsCancellationRequested) _usageError = ex.Message;
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
        CardsPanel.Columns = _preferences.TwoColumnMode ? 2 : 1;
        var dateBelow = _preferences.DateProgress.Placement == DateProgressPlacement.Bottom;
        DateProgressHost.Visibility = _preferences.DateProgress.Enabled ? Visibility.Visible : Visibility.Collapsed;
        DateProgressHost.Width = dateBelow ? double.NaN : 116 * _preferences.TextScale;
        Grid.SetRow(DateProgressHost, dateBelow ? 1 : 0);
        Grid.SetColumnSpan(DateProgressHost, dateBelow ? 2 : 1);
        Grid.SetColumn(CardsScroll, dateBelow ? 0 : 1);
        Grid.SetColumnSpan(CardsScroll, dateBelow ? 2 : 1);
        DateProgressHost.Content = _preferences.DateProgress.Enabled ? new DateProgressColumn(_preferences.DateProgress, _preferences.TextScale) : null;
        _timer.Interval = TimeSpan.FromSeconds(_preferences.RefreshSeconds);
        var small = _preferences.Style == CardStyle.Small;
        Header.Visibility = small || _preferences.HideHeader ? Visibility.Collapsed : Visibility.Visible;
        Frame.Padding = small ? new Thickness(5, 5, 5, 0) : new Thickness(12, 10, 12, 0);
        Topmost = _preferences.Pinned;
        PinButton.Foreground = Topmost ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
        PinButton.ToolTip = Topmost ? "取消置顶" : "置顶显示";
        ResizeMode = _preferences.AllowResize ? ResizeMode.CanResize : ResizeMode.NoResize;
        WindowChrome.GetWindowChrome(this).ResizeBorderThickness = new Thickness(_preferences.AllowResize ? 6 : 0);
        var cardCount = _preferences.Markets!.Length + _preferences.HttpSources.Length + _preferences.ProviderUsages.Count(x => x.Enabled);
        var preset = WidgetLayout.Preset(_preferences.Style, cardCount, _preferences.ShowCodexUsage, _preferences.TextScale, _preferences.NumberScale, _preferences.HideHeader, _preferences.Markets.Count(m => m.IsPerpetual), _preferences.MarketHeightAdjustment, _preferences.Markets.Length);
        var work = WindowPlacement.WorkSize(this);
        MinWidth = (small ? 220 : _preferences.Style == CardStyle.Medium ? 300 : 340) * _preferences.TextScale;
        var dateWidth = _preferences.DateProgress.Enabled && !dateBelow ? 116 * _preferences.TextScale : 0;
        MinWidth = MinWidth * CardsPanel.Columns + dateWidth;
        var presetWidth = preset.Width * CardsPanel.Columns + dateWidth;
        MinHeight = (_preferences.Markets.Length == 0 ? (small ? 50 : _preferences.HideHeader ? 70 : 114) : small ? 66 : _preferences.HideHeader ? 120 : 180) * _preferences.TextScale;
        if (_preferences.DateProgress.Enabled && !dateBelow)
        {
            var date = _preferences.DateProgress;
            var rows = date.Period == DateProgressPeriod.Week ? 7 : date.Style == DateProgressStyle.Grid ? 11 : 31;
            var labelHeight = date.Style == DateProgressStyle.Grid && date.ShowLabels ? 16 : 0;
            var dateHeight = Math.Min(420, (date.ShowHeading ? 58 : 8) + rows * (date.DotSize + 6 + date.Spacing + labelHeight));
            MinHeight = Math.Max(MinHeight, Math.Min(work.Height - 24, (dateHeight + (small || _preferences.HideHeader ? 12 : 66)) * _preferences.TextScale));
        }
        // A locked size was already accepted by the native resize interaction. Content
        // wrapping at that width must not introduce a larger minimum on the next save.
        if (_preferences.LockCustomSize && _preferences.Width is { } lockedWidth)
            MinWidth = Math.Min(MinWidth, lockedWidth);
        MaxWidth = Math.Max(MinWidth, work.Width);
        var useSavedSize = WidgetLayout.UseSavedSize(resetSize, _preferences.AllowResize, _preferences.LockCustomSize);
        Width = Math.Clamp(useSavedSize ? _preferences.Width ?? presetWidth : presetWidth, MinWidth, MaxWidth);
        var bottomHeight = MeasureBottomDate(Width - Frame.Padding.Left - Frame.Padding.Right);
        MinHeight += bottomHeight;
        if (_preferences.LockCustomSize && _preferences.Height is { } lockedHeight)
            MinHeight = Math.Min(MinHeight, lockedHeight);
        MaxHeight = Math.Max(MinHeight, work.Height);
        var presetHeight = preset.Height + bottomHeight;
        Height = Math.Clamp(useSavedSize ? _preferences.Height ?? presetHeight : presetHeight, MinHeight,
            useSavedSize ? MaxHeight : Math.Max(MinHeight, MaxHeight - 24));
        WindowPlacement.EnsureVisible(this);
        UpdateDockedCorners();
    }
    private void FitUsageHeight()
    {
        if (_closing || CardsPanel.Children.Count == 0
            || !WidgetLayout.ShouldAutoFit(_preferences.AllowResize, _preferences.LockCustomSize)) return;
        var availableWidth = Math.Max(1, Width - Frame.Padding.Left - Frame.Padding.Right
            - (_preferences.DateProgress.Enabled && _preferences.DateProgress.Placement == DateProgressPlacement.Left ? DateProgressHost.Width : 0));
        if (CardsPanel.ActualWidth > 0) availableWidth = CardsPanel.ActualWidth;
        CardsPanel.Measure(new Size(availableWidth, double.PositiveInfinity));
        var height = CardsPanel.DesiredSize.Height + Frame.Padding.Top + Frame.Padding.Bottom
            + (Header.Visibility == Visibility.Visible ? Header.Height + Header.Margin.Top + Header.Margin.Bottom : 0) + 2
            + MeasureBottomDate(availableWidth);
        Height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, MaxHeight - 24));
        WindowPlacement.EnsureVisible(this);
        UpdateDockedCorners();
    }
    private double MeasureBottomDate(double width)
    {
        if (!_preferences.DateProgress.Enabled || _preferences.DateProgress.Placement != DateProgressPlacement.Bottom) return 0;
        DateProgressHost.Measure(new Size(Math.Max(1, width), double.PositiveInfinity));
        return DateProgressHost.DesiredSize.Height;
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
            _settings = new SettingsWindow(_preferences with { Width = ActualWidth, Height = ActualHeight }, _catalogs, ApplySettingsAsync) { Owner = this, Topmost = Topmost };
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
        var httpChanged = !_preferences.HttpSources.SequenceEqual(candidate.HttpSources);
        var usageChanged = candidate.ShowCodexUsage != _preferences.ShowCodexUsage || candidate.CodexExecutable != _preferences.CodexExecutable
            || candidate.CodexTokenRange != _preferences.CodexTokenRange
            || !candidate.CodexExcludedBillingModels.Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(_preferences.CodexExcludedBillingModels.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var proxyChanged = candidate.Proxy != _preferences.Proxy;
        var stockSourceChanged = candidate.UsStockProvider != _preferences.UsStockProvider || candidate.AlpacaFeed != _preferences.AlpacaFeed
            || candidate.AlpacaKeyId != _preferences.AlpacaKeyId || candidate.AlpacaSecretKey != _preferences.AlpacaSecretKey;
        var layoutChanged = candidate.HideHeader != _preferences.HideHeader || candidate.Style != _preferences.Style || candidate.Markets!.Length != _preferences.Markets!.Length || candidate.HttpSources.Length != _preferences.HttpSources.Length || candidate.ShowCodexUsage != _preferences.ShowCodexUsage || candidate.TextScale != _preferences.TextScale || candidate.NumberScale != _preferences.NumberScale || candidate.MarketHeightAdjustment != _preferences.MarketHeightAdjustment;
        layoutChanged |= candidate.TwoColumnMode != _preferences.TwoColumnMode || candidate.DateProgress.Enabled != _preferences.DateProgress.Enabled || candidate.ProviderUsages.Count(x => x.Enabled) != _preferences.ProviderUsages.Count(x => x.Enabled);
        layoutChanged |= candidate.DateProgress.Placement != _preferences.DateProgress.Placement;
        var lockingCurrentSize = candidate.LockCustomSize && !_preferences.LockCustomSize;
        // ActualWidth/ActualHeight are the authoritative dimensions after a native edge resize.
        // Capture them before changing ResizeMode, which rebuilds the non-client frame.
        var lockedWidth = ActualWidth > 0 ? ActualWidth : Width;
        var lockedHeight = ActualHeight > 0 ? ActualHeight : Height;
        var resetSize = WidgetLayout.ShouldResetSize(layoutChanged, _preferences.AllowResize, candidate.AllowResize, candidate.LockCustomSize)
            || (_preferences.LockCustomSize && !candidate.LockCustomSize && !candidate.AllowResize);
        var lockCustomSize = !candidate.AllowResize && candidate.LockCustomSize;
        candidate = candidate with { Left = Left, Top = Top, Width = resetSize ? null : lockedWidth, Height = resetSize ? null : lockedHeight,
            LockCustomSize = lockCustomSize };
        if (startup != oldStartup) StartupRegistration.SetEnabled(startup);
        try { _savePreferences(candidate); }
        catch { if (startup != oldStartup) StartupRegistration.SetEnabled(oldStartup); throw; }
        await ResetProviderUsageAsync();
        if (usageChanged)
        {
            _usageRequest?.Cancel();
            await _usageTask;
            _usage = null; _usageError = null; _nextUsageRead = DateTimeOffset.MinValue;
        }
        if (proxyChanged || stockSourceChanged)
        {
            _subscription?.Cancel(); await _streamTask;
            if (proxyChanged)
            {
                _feed.Dispose(); _feed = new BinanceFeed(candidate.Proxy);
                _http.Dispose(); _http = CreateHttpClient(candidate.Proxy);
                _catalogs.Clear();
            }
            _stockFeed.Dispose(); _stockFeed = new(candidate.Proxy, candidate.UsStockProvider, candidate.AlpacaFeed, candidate.AlpacaKeyId, candidate.AlpacaSecretKey);
            lock (_gate) { _latest.Clear(); _funding.Clear(); }
            _history.Clear();
        }
        _preferences = candidate;
        ApplyLayout(resetSize);
        BuildCards();
        if (resetSize || layoutChanged || candidate.UsageExtraHeight != oldUsageExtraHeight)
        {
            UpdateLayout();
            FitUsageHeight();
        }
        if (lockingCurrentSize)
        {
            // ResizeMode and card reconstruction can finish their native/layout work after
            // ApplyLayout returns. Restore the captured user size after that pass, then save it.
            await Dispatcher.InvokeAsync(() =>
            {
                Width = Math.Clamp(lockedWidth, MinWidth, MaxWidth);
                Height = Math.Clamp(lockedHeight, MinHeight, MaxHeight);
                WindowPlacement.EnsureVisible(this);
                UpdateDockedCorners();
            }, DispatcherPriority.Loaded);
            _preferences = _preferences with { Width = Width, Height = Height, LockCustomSize = true };
        }
        if (marketsChanged || httpChanged || proxyChanged || stockSourceChanged) await RestartStreamsAsync();
        SavePreferences();
        if (usageChanged) StartUsageRefresh(true);
    }
    private void TogglePin(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        _preferences = _preferences with { Pinned = Topmost };
        PinButton.Foreground = Topmost ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
        PinButton.ToolTip = Topmost ? "取消置顶" : "置顶显示";
        SavePreferences();
    }
    private async void HideToTray(object sender, RoutedEventArgs e)
    {
        if (_closing || !IsVisible || !_hideTask.IsCompleted) return;
        SavePreferences();
        _timer.Stop();
        _usageRequest?.Cancel();
        _hideTask = HideToTrayAsync();
        await _hideTask;
    }
    private async Task HideToTrayAsync()
    {
        using var request = new CancellationTokenSource();
        _hideRequest = request;
        try { await TrayTransition.HideAsync(this, _dockedCorners, request.Token); }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            _hideRequest = null;
            if (IsVisible && _ready && !_closing) _timer.Start();
        }
    }
    internal void ShowWidget() { if (_closing) return; _hideRequest?.Cancel(); Show(); WindowState = WindowState.Normal; Activate(); }
    private void ExitApp(object sender, RoutedEventArgs e) => Close();
    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        for (var parent = e.OriginalSource as DependencyObject; parent is not null && parent != this;
             parent = parent is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(parent))
            // Popup items have a separate visual tree: walking upward never reaches their ComboBox.
            if (parent is ButtonBase or TextBoxBase or Selector or ComboBoxItem or ScrollBar or Thumb) return;
        e.Handled = true;
        DragMove();
    }
    private void SavePreferences()
    {
        if (!_ready || _closing) return;
        _preferences = _preferences with { Left = Left, Top = Top, Width = Width, Height = Height };
        try { _savePreferences(_preferences); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "设置保存失败：" + ex.Message, "DeskMonitor", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (_closing) return;
        SavePreferences();
        _closing = true;
        _providerLifetime.Cancel();
        _settings?.Close();
        _timer.Stop();
        _hideRequest?.Cancel();
        Hide();
        _tray.Visible = false;
        _subscription?.Cancel();
        _usageRequest?.Cancel();
        await Task.WhenAll(_usageTask, _streamTask, _hideTask);
        await Task.WhenAll(_providerTasks.Values);
        _providerLifetime.Dispose();
        _usageRequest?.Dispose();
        _usageRequest = null;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose(); _trayIcon.Dispose(); _subscription?.Dispose(); _feed.Dispose(); _stockFeed.Dispose(); _http.Dispose();
        _closeReady = true;
        // Tasks may already be complete; do not re-enter Close during Closing.
        _ = Dispatcher.BeginInvoke(new Action(Close));
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
    private static HttpClient CreateHttpClient(NetworkProxy settings) => new(new SocketsHttpHandler { Proxy = settings.CreateProxy(), UseProxy = settings.Mode != ProxyMode.Direct }) { Timeout = TimeSpan.FromSeconds(15) };
}
