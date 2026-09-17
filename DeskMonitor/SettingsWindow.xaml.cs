using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskMonitor.Core;

namespace DeskMonitor;
public partial class SettingsWindow : Window
{
    private readonly Preferences _original;
    private readonly BinanceFeed _feed;
    private readonly Func<Preferences, bool, Task> _save;
    private readonly ObservableCollection<MarketSymbol> _selected;
    private sealed record CardEntry(string Key, string Label);
    private readonly ObservableCollection<CardEntry> _cardOrder = new();
    private readonly Dictionary<MarketKind, IReadOnlyList<MarketSymbol>> _catalogs;
    private readonly CancellationTokenSource _lifetime = new();
    private int _request;
    private bool _ready, _saving;
    private MarketKind Kind => UsdcOption.IsChecked == true ? MarketKind.UsdcPerpetual : FuturesOption.IsChecked == true ? MarketKind.UsdtPerpetual : MarketKind.Spot;
    public SettingsWindow(Preferences preferences, Dictionary<MarketKind, IReadOnlyList<MarketSymbol>> catalogs, Func<Preferences, bool, Task> save)
    {
        InitializeComponent();
        using (var icon = MainWindow.CreateTrayIcon())
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); Icon = source;
        }
        preferences = Preferences.Normalize(preferences);
        _original = preferences;
        _feed = new BinanceFeed(preferences.Proxy);
        _catalogs = catalogs;
        _save = save;
        _selected = new(preferences.Markets!);
        WatchList.ItemsSource = _selected;
        foreach (var key in preferences.CardOrder) _cardOrder.Add(new(key, key));
        CardOrderList.ItemsSource = _cardOrder;
        _selected.CollectionChanged += (_, _) => SyncCardOrder();
        SmallOption.IsChecked = preferences.Style == CardStyle.Small;
        MediumOption.IsChecked = preferences.Style == CardStyle.Medium;
        LargeOption.IsChecked = preferences.Style == CardStyle.Large;
        PinnedOption.IsChecked = preferences.Pinned;
        ResizeOption.IsChecked = preferences.AllowResize;
        HideHeaderOption.IsChecked = preferences.HideHeader;
        SmallRadiusSlider.Value = preferences.SmallCornerRadius ?? 12;
        SmallRadiusDefault.IsChecked = preferences.SmallCornerRadius is null;
        UpdateSmallRadius();
        ProxyChoice.ItemsSource = new[] { "跟随系统", "直连", "自定义代理" };
        ProxyChoice.SelectedIndex = (int)preferences.Proxy.Mode;
        ProxyAddress.Text = preferences.Proxy.Address;
        SnapOption.IsChecked = preferences.SnapToEdges;
        UsageOption.IsChecked = preferences.ShowCodexUsage;
        UsageHeightSlider.Value = preferences.UsageExtraHeight;
        UpdateUsageHeight();
        CodexPath.Text = preferences.CodexExecutable ?? "";
        SkinChoice.ItemsSource = ThemeManager.Options;
        SkinChoice.SelectedItem = ThemeManager.Options.First(x => x.Id == preferences.Skin);
        TextScaleSlider.Value = preferences.TextScale;
        NumberScaleSlider.Value = preferences.NumberScale;
        MonospaceOption.IsChecked = preferences.MonospaceNumbers;
        TrendsOption.IsChecked = preferences.ShowTrends;
        RefreshChoice.ItemsSource = new[] { "每 1 秒 · 实时", "每 2 秒 · 均衡", "每 5 秒 · 省电" };
        RefreshChoice.SelectedIndex = Array.IndexOf(new[] { 1, 2, 5 }, preferences.RefreshSeconds);
        RefreshAppearancePreview();
        try { StartupOption.IsChecked = StartupRegistration.IsEnabled(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { StartupOption.IsEnabled = false; ErrorText.Text = "无法读取自启动设置：" + ex.Message; }
        UpdateCount();
        SyncCardOrder();
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e) { _ready = true; await LoadCatalogAsync(); }
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
        if (PreviewRoot is null || SkinChoice.SelectedItem is not SkinOption skin || NumberScaleSlider is null || MonospaceOption is null) return;
        PreviewRoot.Resources = ThemeManager.CreateResources(skin.Id);
        PreviewContent.LayoutTransform = new ScaleTransform(TextScaleSlider.Value, TextScaleSlider.Value);
        PreviewNumber.FontSize = 30 * NumberScaleSlider.Value;
        PreviewNumber.FontFamily = new FontFamily(MonospaceOption.IsChecked == true ? "Consolas" : "Segoe UI");
        SkinDescription.Text = skin.Description;
        SmallRadiusSample.Resources = PreviewRoot.Resources;
        TextScaleValue.Text = $"{TextScaleSlider.Value:P0}";
        NumberScaleValue.Text = $"{NumberScaleSlider.Value:P0}";
    }
    private void ProxyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProxyAddress is not null) ProxyAddress.IsEnabled = ProxyChoice.SelectedIndex == 2;
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
    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        if (_selected.Count == 0 && UsageOption.IsChecked != true) { ErrorText.Text = "至少保留一项行情或开启用量卡片。"; return; }
        _saving = true;
        IsEnabled = false;
        try
        {
            var style = SmallOption.IsChecked == true ? CardStyle.Small : MediumOption.IsChecked == true ? CardStyle.Medium : CardStyle.Large;
            var candidate = _original with { Markets = _selected.ToArray(), Style = style, Pinned = PinnedOption.IsChecked == true,
                CardOrder = _cardOrder.Select(c => c.Key).ToArray(),
                Proxy = new NetworkProxy { Mode = (ProxyMode)ProxyChoice.SelectedIndex, Address = ProxyAddress.Text.Trim() },
                HideHeader = HideHeaderOption.IsChecked == true,
                SmallCornerRadius = SmallRadiusDefault.IsChecked == true ? null : SmallRadiusSlider.Value,
                UsageExtraHeight = UsageHeightSlider.Value,
                Skin = ((SkinOption)SkinChoice.SelectedItem).Id, TextScale = TextScaleSlider.Value, NumberScale = NumberScaleSlider.Value,
                MonospaceNumbers = MonospaceOption.IsChecked == true, ShowTrends = TrendsOption.IsChecked == true,
                RefreshSeconds = new[] { 1, 2, 5 }[RefreshChoice.SelectedIndex],
                AllowResize = ResizeOption.IsChecked == true, SnapToEdges = SnapOption.IsChecked == true,
                ShowCodexUsage = UsageOption.IsChecked == true, CodexExecutable = string.IsNullOrWhiteSpace(CodexPath.Text) ? null : CodexPath.Text.Trim() };
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
