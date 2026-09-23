using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using DeskMonitor.Core;

namespace DeskMonitor;
public partial class SettingsWindow : Window
{
    private readonly Preferences _original;
    private readonly BinanceFeed _feed;
    private readonly Func<Preferences, bool, Task> _save;
    private readonly ObservableCollection<MarketSymbol> _selected;
    private readonly ObservableCollection<HttpMessageSource> _httpSources;
    private sealed record CardEntry(string Key, string Label);
    private readonly ObservableCollection<CardEntry> _cardOrder = new();
    private Dictionary<string, CardPlacement> _placements = new();
    private bool _loadingPlacement;
    private void CardSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CardColumnChoice is null || CardGroupName is null) return;
        _loadingPlacement = true;
        var placement = CardOrderList.SelectedItem is CardEntry card ? _placements.GetValueOrDefault(card.Key) ?? new() : new CardPlacement();
        CardColumnChoice.SelectedIndex = placement.Column;
        CardGroupName.Text = placement.Group;
        CardColumnChoice.IsEnabled = CardGroupName.IsEnabled = CardOrderList.SelectedItem is CardEntry;
        _loadingPlacement = false;
    }
    private void CardPlacementChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingPlacement || CardGroupName is null || CardColumnChoice is null || CardOrderList.SelectedItem is not CardEntry card) return;
        _placements[card.Key] = new(Math.Max(0, CardColumnChoice.SelectedIndex), CardGroupName.Text.Trim());
    }
    private readonly Dictionary<MarketKind, IReadOnlyList<MarketSymbol>> _catalogs;
    private readonly CancellationTokenSource _lifetime = new();
    private int _request;
    private int _tokenAnalysisRequest;
    private bool _ready, _saving;
    private readonly HashSet<string> _excludedBillingModels;
    private CodexTokenReport? _rawTokenReport;
    private TokenModelRow[] _tokenModelRows = [];
    private string? _tokenQuotaNote;
    private sealed class TokenModelRow(CodexModelUsage usage) : INotifyPropertyChanged
    {
        public string Model { get; } = usage.Model;
        public string Total { get; } = FormatTokens(usage.Tokens.TotalTokens);
        public string Breakdown { get; } = $"{FormatTokens(usage.Tokens.InputTokens)} / {FormatTokens(usage.Tokens.CachedInputTokens)} / {FormatTokens(usage.Tokens.OutputTokens)}";
        public bool Excluded { get; private set; }
        public string Cost { get; private set; } = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(CodexModelUsage model)
        {
            Excluded = model.ExcludedFromCost;
            Cost = Excluded ? "已排除" : model.EstimatedCostUsd is { } cost ? $"${cost:0.0000}" : "未计价";
            PropertyChanged?.Invoke(this, new(nameof(Excluded)));
            PropertyChanged?.Invoke(this, new(nameof(Cost)));
        }
    }
    private MarketKind Kind => StockOption.IsChecked == true ? MarketKind.UsStock : UsdcOption.IsChecked == true ? MarketKind.UsdcPerpetual : FuturesOption.IsChecked == true ? MarketKind.UsdtPerpetual : MarketKind.Spot;
    public SettingsWindow(Preferences preferences, Dictionary<MarketKind, IReadOnlyList<MarketSymbol>> catalogs, Func<Preferences, bool, Task> save)
    {
        InitializeComponent();
        _placements = new(preferences.CardPlacements);
        using (var icon = MainWindow.CreateTrayIcon())
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); Icon = source;
        }
        AboutIcon.Source = Icon;
        var assembly = typeof(SettingsWindow).Assembly;
        AboutVersion.Text = "v" + assembly.GetName().Version!.ToString(3);
        var releaseDate = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ReleaseDate").Value!;
        AboutReleaseDate.Text = DateOnly.ParseExact(releaseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        AboutRuntime.Text = $".NET {Environment.Version} · {RuntimeInformation.ProcessArchitecture}";
        preferences = Preferences.Normalize(preferences);
        _original = preferences;
        _excludedBillingModels = new(preferences.CodexExcludedBillingModels, StringComparer.OrdinalIgnoreCase);
        _feed = new BinanceFeed(preferences.Proxy);
        _catalogs = catalogs;
        _save = save;
        _selected = new(preferences.Markets!);
        _httpSources = new(preferences.HttpSources);
        foreach (var item in ProviderUsageSettings.Defaults())
        {
            var editor = new ProviderUsageEditor(preferences.ProviderUsages.FirstOrDefault(x => x.Key == item.Key) ?? item);
            editor.Changed += (_, _) => SyncCardOrder();
            ProviderUsageEditors.Children.Add(editor);
        }
        WatchList.ItemsSource = _selected;
        HttpSourceList.ItemsSource = _httpSources;
        HttpRefresh.ItemsSource = new[] { "每 5 秒", "每 15 秒", "每 30 秒", "每 1 分钟", "每 5 分钟", "每 15 分钟", "每 1 小时" };
        HttpRefresh.SelectedIndex = 3;
        foreach (var key in preferences.CardOrder) _cardOrder.Add(new(key, key));
        CardOrderList.ItemsSource = _cardOrder;
        _selected.CollectionChanged += (_, _) => SyncCardOrder();
        _httpSources.CollectionChanged += (_, _) => SyncCardOrder();
        SmallOption.IsChecked = preferences.Style == CardStyle.Small;
        MediumOption.IsChecked = preferences.Style == CardStyle.Medium;
        LargeOption.IsChecked = preferences.Style == CardStyle.Large;
        PinnedOption.IsChecked = preferences.Pinned;
        ResizeOption.IsChecked = preferences.AllowResize;
        LockSizeOption.IsChecked = preferences.LockCustomSize;
        HideHeaderOption.IsChecked = preferences.HideHeader;
        SmallRadiusSlider.Value = preferences.SmallCornerRadius ?? 12;
        SmallRadiusDefault.IsChecked = preferences.SmallCornerRadius is null;
        UpdateSmallRadius();
        ProxyChoice.ItemsSource = new[] { "跟随系统", "直连", "自定义代理" };
        ProxyChoice.SelectedIndex = (int)preferences.Proxy.Mode;
        ProxyAddress.Text = preferences.Proxy.Address;
        StockProviderChoice.ItemsSource = new[] { "Alpaca（推荐）", "Yahoo Finance（免密实验源）" };
        StockProviderChoice.SelectedIndex = preferences.UsStockProvider == UsStockProvider.Alpaca ? 0 : 1;
        AlpacaFeedChoice.ItemsSource = new[] { "IEX · 免费实时", "SIP · 延迟 15 分钟", "SIP · 付费实时" };
        AlpacaFeedChoice.SelectedIndex = (int)preferences.AlpacaFeed;
        AlpacaKeyId.Text = preferences.AlpacaKeyId;
        AlpacaSecretKey.Password = preferences.AlpacaSecretKey;
        UpdateStockProvider();
        SnapOption.IsChecked = preferences.SnapToEdges;
        UsageOption.IsChecked = preferences.ShowCodexUsage;
        UsageHeightSlider.Value = preferences.UsageExtraHeight;
        UpdateUsageHeight();
        CodexTokenRangeChoice.ItemsSource = new[] { "短周期重置后", "长周期重置后", "今天", "最近 7 个自然日", "最近 30 个自然日" };
        CodexTokenRangeChoice.SelectedIndex = (int)preferences.CodexTokenRange;
        CodexPath.Text = preferences.CodexExecutable ?? "";
        SkinChoice.ItemsSource = ThemeManager.Options;
        SkinChoice.SelectedItem = ThemeManager.Options.First(x => x.Id == preferences.Skin);
        TextScaleSlider.Value = preferences.TextScale;
        NumberScaleSlider.Value = preferences.NumberScale;
        MarketHeightSlider.Value = preferences.MarketHeightAdjustment;
        MonospaceOption.IsChecked = preferences.MonospaceNumbers;
        TrendsOption.IsChecked = preferences.ShowTrends;
        TwoColumnOption.IsChecked = preferences.TwoColumnMode;
        DateProgressOption.IsChecked = preferences.DateProgress.Enabled;
        DatePlacementChoice.ItemsSource = new[] { "左侧 · 竖排", "卡片下方 · 横排" };
        DatePlacementChoice.SelectedIndex = (int)preferences.DateProgress.Placement;
        DateHorizontalLayoutChoice.ItemsSource = new[] { "左对齐", "居中", "拉伸 · 每行均匀铺满" };
        DateHorizontalLayoutChoice.SelectedIndex = (int)preferences.DateProgress.HorizontalLayout;
        DatePeriodChoice.ItemsSource = new[] { "本周（周一至周日）", "本月" };
        DatePeriodChoice.SelectedIndex = (int)preferences.DateProgress.Period;
        DateStyleChoice.ItemsSource = new[] { "地铁站点式", "紧凑圆点网格" };
        DateStyleChoice.SelectedIndex = (int)preferences.DateProgress.Style;
        DateDotSize.Value = preferences.DateProgress.DotSize;
        DateDotSpacing.Value = preferences.DateProgress.Spacing;
        DatePastColor.Text = preferences.DateProgress.PastColor;
        DateFutureColor.Text = preferences.DateProgress.FutureColor;
        DateHeadingOption.IsChecked = preferences.DateProgress.ShowHeading;
        DateLabelsOption.IsChecked = preferences.DateProgress.ShowLabels;
        RefreshChoice.ItemsSource = new[] { "每 1 秒 · 实时", "每 2 秒 · 均衡", "每 5 秒 · 省电" };
        RefreshChoice.SelectedIndex = Array.IndexOf(new[] { 1, 2, 5 }, preferences.RefreshSeconds);
        RefreshAppearancePreview();
        try { StartupOption.IsChecked = StartupRegistration.IsEnabled(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { StartupOption.IsEnabled = false; ErrorText.Text = "无法读取自启动设置：" + ex.Message; }
        UpdateCount();
        SyncCardOrder();
    }
    private void OpenAboutLink(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { ErrorText.Text = "无法打开链接：" + ex.Message; }
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        var tokenAnalysis = LoadTokenAnalysisAsync();
        await LoadCatalogAsync();
        await tokenAnalysis;
    }
    private void WindowClosed(object? sender, EventArgs e) { _ready = false; _lifetime.Cancel(); _feed.Dispose(); }
    private async void MarketChanged(object sender, RoutedEventArgs e) { if (_ready) await LoadCatalogAsync(); }
    private async void ReloadCatalog(object sender, RoutedEventArgs e) { _catalogs.Remove(Kind); await LoadCatalogAsync(); }
    private async Task LoadCatalogAsync()
    {
        var kind = Kind;
        var request = ++_request;
        CatalogList.ItemsSource = null;
        CatalogStatus.Text = "正在加载…";
        try
        {
            if (kind == MarketKind.UsStock)
            {
                FilterCatalog();
                return;
            }
            if (!_catalogs.TryGetValue(kind, out var markets))
            {
                markets = await _feed.GetSymbolsAsync(_lifetime.Token, kind);
                _catalogs[kind] = markets;
            }
            if (_ready && request == _request) FilterCatalog();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException)
        { if (_ready && request == _request) CatalogStatus.Text = "加载失败，请点击刷新。" + ex.Message; }
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (_ready) FilterCatalog(); }
    private void FilterCatalog()
    {
        if (Kind == MarketKind.UsStock)
        {
            var stockSearch = SearchBox.Text.Trim().ToUpperInvariant();
            string[] popularStocks = ["AAPL", "MSFT", "NVDA", "GOOGL", "AMZN", "META", "TSLA", "AMD", "NFLX", "SPY", "QQQ"];
            var symbols = popularStocks.Where(x => x.Contains(stockSearch, StringComparison.OrdinalIgnoreCase)).ToList();
            if (stockSearch.Length is >= 1 and <= 10 && stockSearch.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-')) symbols.Insert(0, stockSearch);
            var stockResult = symbols.Distinct(StringComparer.OrdinalIgnoreCase).Select(x => new MarketSymbol(x, x, "USD", MarketKind.UsStock)).ToArray();
            CatalogList.ItemsSource = stockResult;
            CatalogStatus.Text = stockResult.Length == 0 ? "请输入合法的美股代码" : $"{stockResult.Length} 个代码 · 公开行情可能延迟";
            return;
        }
        if (!_catalogs.TryGetValue(Kind, out var markets)) return;
        var search = SearchBox.Text.Trim();
        string[] popular = ["BTC", "ETH", "SOL", "BNB", "XRP", "DOGE"];
        var result = markets.Where(x => x.BaseAsset.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => { var index = Array.IndexOf(popular, x.BaseAsset); return index < 0 ? int.MaxValue : index; })
            .ThenBy(x => x.BaseAsset, StringComparer.Ordinal).ToArray();
        CatalogList.ItemsSource = result;
        CatalogStatus.Text = result.Length == 0 ? "没有匹配的币种" : $"{result.Length} 个可用交易对 · 最新成交价";
    }
    private void AddMarket(object sender, RoutedEventArgs e)
    {
        if (CatalogList.SelectedItem is not MarketSymbol market) { ErrorText.Text = "请先在下方选择一个币种。"; return; }
        if (_selected.Any(x => x.Key == market.Key)) { ErrorText.Text = "该行情已在列表中。"; return; }
        if (_selected.Count >= Preferences.MaxMarkets) { ErrorText.Text = $"最多显示 {Preferences.MaxMarkets} 项行情。"; return; }
        _selected.Add(market);
        WatchList.SelectedItem = market;
        WatchList.ScrollIntoView(market);
        ErrorText.Text = "";
        UpdateCount();
    }
    private void RemoveMarket(object sender, RoutedEventArgs e)
    {
        if (WatchList.SelectedItem is MarketSymbol market) _selected.Remove(market);
        UpdateCount();
    }
    private void MoveCardUp(object sender, RoutedEventArgs e) => MoveCard(-1);
    private void MoveCardDown(object sender, RoutedEventArgs e) => MoveCard(1);
    private void MoveCard(int offset)
    {
        var index = CardOrderList.SelectedIndex;
        if (index < 0 || index + offset < 0 || index + offset >= _cardOrder.Count) return;
        _cardOrder.Move(index, index + offset);
        CardOrderList.SelectedIndex = index + offset;
        CardOrderList.ScrollIntoView(CardOrderList.SelectedItem);
    }
    private void UsageChanged(object sender, RoutedEventArgs e) { if (_selected is not null) SyncCardOrder(); }
    private async void TokenAnalysisRangeChanged(object sender, SelectionChangedEventArgs e)
    { if (_ready) await LoadTokenAnalysisAsync(); }
    private async void RefreshTokenAnalysis(object sender, RoutedEventArgs e) => await LoadTokenAnalysisAsync();
    private void TokenModelExcludeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: TokenModelRow row } option) return;
        if (option.IsChecked == true) _excludedBillingModels.Add(row.Model);
        else _excludedBillingModels.Remove(row.Model);
        RenderTokenAnalysis();
    }
    private void RenderTokenAnalysis()
    {
        if (_rawTokenReport is null) return;
        var report = CodexTokenAnalyzer.ApplyCostExclusions(_rawTokenReport, _excludedBillingModels);
        foreach (var (row, model) in _tokenModelRows.Zip(report.Models)) row.Update(model);
        TokenAnalysisStatus.Text = $"{report.RangeLabel} · 共 {FormatTokens(report.Tokens.TotalTokens)} token · 估算 ${report.EstimatedCostUsd:0.0000}"
            + (report.UnpricedTokens > 0 ? $" · {FormatTokens(report.UnpricedTokens)} 未计价" : "")
            + (_excludedBillingModels.Count > 0 ? $" · 已排除 {_excludedBillingModels.Count} 个模型计费" : "") + _tokenQuotaNote;
    }
    private async Task LoadTokenAnalysisAsync()
    {
        if (CodexTokenRangeChoice.SelectedIndex < 0) return;
        var request = ++_tokenAnalysisRequest;
        TokenAnalysisStatus.Text = "正在分析本地 Codex 会话…";
        try
        {
            var range = (CodexTokenRangeKind)CodexTokenRangeChoice.SelectedIndex;
            QuotaWindow? primary = null, secondary = null;
            string? quotaNote = null;
            if (range is CodexTokenRangeKind.ShortReset or CodexTokenRangeKind.LongReset)
            {
                try
                {
                    var quota = await CodexUsageClient.ReadAsync(string.IsNullOrWhiteSpace(CodexPath.Text) ? null : CodexPath.Text.Trim(), _lifetime.Token);
                    primary = quota.Primary; secondary = quota.Secondary;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or Win32Exception or InvalidOperationException)
                { quotaNote = "；未取得重置点，已按今天统计"; }
            }
            var report = await CodexTokenAnalyzer.ReadAsync(range, primary, secondary, _lifetime.Token);
            if (request != _tokenAnalysisRequest || !_ready) return;
            _rawTokenReport = report;
            _tokenQuotaNote = quotaNote;
            _tokenModelRows = report.Models.Select(x => new TokenModelRow(x)).ToArray();
            TokenModelList.ItemsSource = _tokenModelRows;
            RenderTokenAnalysis();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            if (request == _tokenAnalysisRequest) TokenAnalysisStatus.Text = "Token 分析失败：" + ex.Message;
        }
    }
    private static string FormatTokens(long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000d:0.##}M",
        >= 1_000 => $"{value / 1_000d:0.##}K",
        _ => value.ToString("N0", CultureInfo.InvariantCulture)
    };
    private void NewHttpSource(object sender, RoutedEventArgs e)
    {
        if (_httpSources.Count >= 10) { ErrorText.Text = "最多添加 10 个自定义消息源。"; return; }
        var source = new HttpMessageSource { Title = "新消息", Url = "https://example.com/", RefreshSeconds = 60 };
        _httpSources.Add(source);
        HttpSourceList.SelectedItem = source;
        ErrorText.Text = "";
    }
    private void DeleteHttpSource(object sender, RoutedEventArgs e)
    {
        if (HttpSourceList.SelectedItem is HttpMessageSource source) _httpSources.Remove(source);
    }
    private void HttpSourceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (HttpSourceList.SelectedItem is not HttpMessageSource source) return;
        HttpTitle.Text = source.Title;
        HttpUrl.Text = source.Url;
        HttpJsonPath.Text = source.JsonPath;
        HttpRefresh.SelectedIndex = Array.IndexOf(new[] { 5, 15, 30, 60, 300, 900, 3600 }, source.RefreshSeconds);
    }
    private void ApplyHttpSource(object sender, RoutedEventArgs e)
    {
        if (HttpSourceList.SelectedItem is not HttpMessageSource source) { ErrorText.Text = "请先新建或选择一个消息源。"; return; }
        var updated = source with { Title = HttpTitle.Text.Trim(), Url = HttpUrl.Text.Trim(), JsonPath = HttpJsonPath.Text.Trim(), RefreshSeconds = new[] { 5, 15, 30, 60, 300, 900, 3600 }[Math.Max(0, HttpRefresh.SelectedIndex)] };
        try { updated.Validate(); }
        catch (InvalidDataException ex) { ErrorText.Text = ex.Message; return; }
        var index = _httpSources.IndexOf(source);
        _httpSources[index] = updated;
        HttpSourceList.SelectedIndex = index;
        ErrorText.Text = "";
    }
    private void UsageHeightChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateUsageHeight();
    private void UpdateUsageHeight()
    {
        if (UsageHeightValue is not null && UsageHeightSlider is not null)
            UsageHeightValue.Text = UsageHeightSlider.Value == 0 ? "紧凑（自动）" : $"内容高度 + {UsageHeightSlider.Value:0} px";
    }
    private void SyncCardOrder()
    {
        var cards = _selected.Select(m => new CardEntry(m.Key, m.Label)).ToDictionary(c => c.Key);
        cards.Add(Preferences.CodexCardKey, new(Preferences.CodexCardKey, UsageOption.IsChecked == true ? "Codex 用量" : "Codex 用量（未开启）"));
        foreach (var source in _httpSources) cards.Add(source.Key, new(source.Key, "消息 · " + source.Title));
        foreach (var editor in ProviderUsageEditors.Children.OfType<ProviderUsageEditor>())
        {
            var usage = editor.Value;
            cards.Add(usage.Key, new(usage.Key, usage.Title + (usage.Enabled ? "" : "（未开启）")));
        }
        var ordered = _cardOrder.Where(c => cards.ContainsKey(c.Key)).Select(c => c.Key)
            .Concat(cards.Keys.Where(key => !_cardOrder.Any(c => c.Key == key))).ToArray();
        var selectedKey = (CardOrderList.SelectedItem as CardEntry)?.Key;
        _cardOrder.Clear();
        foreach (var key in ordered) _cardOrder.Add(cards[key]);
        CardOrderList.SelectedItem = _cardOrder.FirstOrDefault(c => c.Key == selectedKey);
    }
    private void UpdateCount() => CountText.Text = $"{_selected.Count} / {Preferences.MaxMarkets}";
    private void AppearanceSelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshAppearancePreview();
    private void AppearanceScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => RefreshAppearancePreview();
    private void AppearanceChecked(object sender, RoutedEventArgs e) => RefreshAppearancePreview();
    private void RefreshAppearancePreview()
    {
        if (PreviewRoot is null || PreviewCard is null || SkinChoice.SelectedItem is not SkinOption skin || NumberScaleSlider is null || MarketHeightSlider is null || MonospaceOption is null) return;
        PreviewRoot.Resources = ThemeManager.CreateResources(skin.Id);
        PreviewContent.LayoutTransform = new ScaleTransform(TextScaleSlider.Value, TextScaleSlider.Value);
        PreviewNumber.FontSize = 30 * NumberScaleSlider.Value;
        PreviewNumber.FontFamily = new FontFamily(MonospaceOption.IsChecked == true ? "Consolas" : "Segoe UI");
        var heightAdjustment = MarketHeightSlider.Value;
        PreviewCard.MinHeight = 96 + heightAdjustment;
        PreviewCard.Padding = new Thickness(14, Math.Max(8, 14 + heightAdjustment / 8), 14, Math.Max(8, 14 + heightAdjustment / 8));
        SkinDescription.Text = skin.Description;
        SmallRadiusSample.Resources = PreviewRoot.Resources;
        TextScaleValue.Text = $"{TextScaleSlider.Value:P0}";
        NumberScaleValue.Text = $"{NumberScaleSlider.Value:P0}";
        MarketHeightValue.Text = heightAdjustment == 0 ? "默认" : heightAdjustment < 0 ? $"紧凑 {heightAdjustment:0} px" : $"宽松 +{heightAdjustment:0} px";
    }
    private void ProxyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProxyAddress is not null) ProxyAddress.IsEnabled = ProxyChoice.SelectedIndex == 2;
    }
    private void StockProviderChanged(object sender, SelectionChangedEventArgs e) => UpdateStockProvider();
    private void UpdateStockProvider()
    {
        if (AlpacaFeedChoice is null || AlpacaKeyId is null || AlpacaSecretKey is null || StockProviderHelp is null) return;
        var alpaca = StockProviderChoice.SelectedIndex == 0;
        AlpacaFeedChoice.IsEnabled = AlpacaKeyId.IsEnabled = AlpacaSecretKey.IsEnabled = alpaca;
        StockProviderHelp.Text = alpaca
            ? "Alpaca 使用官方 WebSocket；免费账户请选择 IEX。Key 与 Secret 保存在当前 Windows 用户的本机设置文件中，不会上传。"
            : "Yahoo 为无需密钥的实验性 HTTP 源，可能延迟或因接口变化失效；不会作为 Alpaca 的静默备用源。";
    }
    private void SmallRadiusChecked(object sender, RoutedEventArgs e) => UpdateSmallRadius();
    private void SmallRadiusChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSmallRadius();
    private void UpdateSmallRadius()
    {
        if (SmallRadiusSlider is null || SmallRadiusDefault is null || SmallRadiusValue is null) return;
        SmallRadiusSlider.IsEnabled = SmallRadiusDefault.IsChecked != true;
        SmallRadiusValue.Text = SmallRadiusDefault.IsChecked == true ? "跟随皮肤" : $"{SmallRadiusSlider.Value:0} DIP";
        if (SmallRadiusSample is not null) {
            if (SmallRadiusDefault.IsChecked == true) SmallRadiusSample.SetResourceReference(Border.CornerRadiusProperty,"CardRadius");
            else SmallRadiusSample.CornerRadius = new CornerRadius(SmallRadiusSlider.Value);
        }
    }
    private void ChooseCodex(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择 Codex 程序", Filter = "Codex 程序 (*.exe)|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) CodexPath.Text = picker.FileName;
    }
    private void ResizeOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        LockSizeOption.IsChecked = ResizeOption.IsChecked != true;
    }
    private void LockSizeOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ResizeOption.IsChecked = false;
    }
    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        if (_selected.Count == 0 && _httpSources.Count == 0 && UsageOption.IsChecked != true && !ProviderUsageEditors.Children.OfType<ProviderUsageEditor>().Any(x => x.IsEnabledCard)) { ErrorText.Text = "至少保留一项行情、消息源或开启用量卡片。"; return; }
        _saving = true;
        IsEnabled = false;
        try
        {
            var style = SmallOption.IsChecked == true ? CardStyle.Small : MediumOption.IsChecked == true ? CardStyle.Medium : CardStyle.Large;
            var stockProvider = StockProviderChoice.SelectedIndex == 0 ? UsStockProvider.Alpaca : UsStockProvider.Yahoo;
            if (_selected.Any(m => m.Kind == MarketKind.UsStock) && stockProvider == UsStockProvider.Alpaca
                && (string.IsNullOrWhiteSpace(AlpacaKeyId.Text) || string.IsNullOrWhiteSpace(AlpacaSecretKey.Password)))
                throw new InvalidDataException("使用 Alpaca 美股行情时必须填写 API Key ID 和 Secret Key。");
            var candidate = _original with { Markets = _selected.ToArray(), Style = style, Pinned = PinnedOption.IsChecked == true,
                CardOrder = _cardOrder.Select(c => c.Key).ToArray(),
                CardPlacements = new(_placements),
                ProviderUsages = ProviderUsageEditors.Children.OfType<ProviderUsageEditor>().Select(x => x.Value).ToArray(),
                DateProgress = new DateProgressSettings { Enabled = DateProgressOption.IsChecked == true, Period = (DateProgressPeriod)DatePeriodChoice.SelectedIndex,
                    Placement = (DateProgressPlacement)DatePlacementChoice.SelectedIndex,
                    HorizontalLayout = (DateProgressHorizontalLayout)DateHorizontalLayoutChoice.SelectedIndex,
                    Style = (DateProgressStyle)DateStyleChoice.SelectedIndex, DotSize = DateDotSize.Value, Spacing = DateDotSpacing.Value,
                    PastColor = DatePastColor.Text.Trim(), FutureColor = DateFutureColor.Text.Trim(), ShowHeading = DateHeadingOption.IsChecked == true,
                    ShowLabels = DateLabelsOption.IsChecked == true },
                Proxy = new NetworkProxy { Mode = (ProxyMode)ProxyChoice.SelectedIndex, Address = ProxyAddress.Text.Trim() },
                HideHeader = HideHeaderOption.IsChecked == true,
                SmallCornerRadius = SmallRadiusDefault.IsChecked == true ? null : SmallRadiusSlider.Value,
                UsageExtraHeight = UsageHeightSlider.Value,
                CodexTokenRange = (CodexTokenRangeKind)CodexTokenRangeChoice.SelectedIndex,
                CodexExcludedBillingModels = _excludedBillingModels.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                Skin = ((SkinOption)SkinChoice.SelectedItem).Id, TextScale = TextScaleSlider.Value, NumberScale = NumberScaleSlider.Value,
                MarketHeightAdjustment = MarketHeightSlider.Value,
                MonospaceNumbers = MonospaceOption.IsChecked == true, ShowTrends = TrendsOption.IsChecked == true,
                TwoColumnMode = TwoColumnOption.IsChecked == true,
                RefreshSeconds = new[] { 1, 2, 5 }[RefreshChoice.SelectedIndex],
                AllowResize = ResizeOption.IsChecked == true, SnapToEdges = SnapOption.IsChecked == true,
                LockCustomSize = LockSizeOption.IsChecked == true,
                ShowCodexUsage = UsageOption.IsChecked == true, CodexExecutable = string.IsNullOrWhiteSpace(CodexPath.Text) ? null : CodexPath.Text.Trim(),
                HttpSources = _httpSources.ToArray(), UsStockProvider = stockProvider, AlpacaFeed = (AlpacaFeed)AlpacaFeedChoice.SelectedIndex,
                AlpacaKeyId = AlpacaKeyId.Text.Trim(), AlpacaSecretKey = AlpacaSecretKey.Password.Trim() };
            candidate = Preferences.Normalize(candidate);
            if (candidate.ShowCodexUsage && candidate.CodexExecutable is not null) CodexUsageClient.ResolveExecutable(candidate.CodexExecutable);
            await _save(candidate, StartupOption.IsChecked == true);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidDataException)
        { ErrorText.Text = "保存失败：" + ex.Message; }
        finally { _saving = false; IsEnabled = true; }
    }
}
