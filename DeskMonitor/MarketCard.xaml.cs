using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskMonitor.Core;

namespace DeskMonitor;
public partial class MarketCard : UserControl
{
    private readonly Brush Up = ThemeManager.Brush("UpBrush"), Down = ThemeManager.Brush("DownBrush"), Amber = ThemeManager.Brush("WarningBrush"), Neutral = ThemeManager.Brush("TextBrush");
    private IReadOnlyList<Ticker> _history = Array.Empty<Ticker>();
    private Ticker? _rendered;
    private readonly CardStyle _style;
    private bool _ready;
    private readonly bool _showTrends;
    private readonly bool _compactMode;
    private readonly int _changeMinutes;
    private DateTimeOffset _lastChartDraw;
    public bool NeedsTrendRefresh => _showTrends && (_style != CardStyle.Small || SmallChart.Visibility == Visibility.Visible) && DateTimeOffset.UtcNow - _lastChartDraw >= TimeSpan.FromSeconds(5);
    public int TrendMinutes { get; private set; }
    public event EventHandler? TrendSpanChanged;
    private sealed record TrendOption(int Minutes, string Label);
    public MarketSymbol Market { get; }
    public MarketCard(MarketSymbol market, CardStyle style, int trendMinutes = 2, double textScale = 1, double numberScale = 1, bool monospace = false, bool showTrends = true, double? smallCornerRadius = null, double heightAdjustment = 0, bool compactMode = false, int changeMinutes = 0)
    {
        InitializeComponent();
        Market = market;
        _style = style;
        _compactMode = compactMode;
        if (!MarketChangeReference.IsSupportedPeriod(changeMinutes)) throw new ArgumentOutOfRangeException(nameof(changeMinutes));
        _changeMinutes = changeMinutes;
        _showTrends = showTrends && !compactMode;
        LayoutTransform = new ScaleTransform(textScale, textScale);
        if (!TrendHistory.IsSupportedSpan(trendMinutes)) throw new ArgumentOutOfRangeException(nameof(trendMinutes));
        TrendMinutes = trendMinutes;
        var trendOptions = new[] { new TrendOption(2, "2 分钟"), new TrendOption(5, "5 分钟"), new TrendOption(15, "15 分钟"), new TrendOption(60, "1 小时"), new TrendOption(240, "4 小时"), new TrendOption(1440, "1 天"), new TrendOption(10080, "1 周") };
        TrendSelector.ItemsSource = trendOptions;
        TrendSelector.SelectedIndex = Array.FindIndex(trendOptions, option => option.Minutes == trendMinutes);
        SymbolText.Text = $"{market.BaseAsset} / {market.QuoteAsset}";
        HighLabel.Text = $"24H 最高 · {market.QuoteAsset}";
        LowLabel.Text = $"24H 最低 · {market.QuoteAsset}";
        KindText.Text = market.MarketLabel;
        CompactSymbol.Text = market.Label;
        Height = WidgetLayout.MarketHeight(style, numberScale, heightAdjustment, compactMode) - (compactMode ? 0 : 8);
        var verticalSpacing = heightAdjustment / 6;
        if (market.IsPerpetual && !compactMode)
        {
            Height += WidgetLayout.FundingHeight(style);
            CompactFunding.Visibility = FundingText.Visibility = Visibility.Visible;
            CompactFunding.Text = "费率 —";
            FundingText.Text = "资金费率 — · 等待数据";
            CompactSymbol.TextWrapping = TextWrapping.NoWrap;
            CompactSymbol.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        if (style == CardStyle.Small || compactMode)
        {
            if (smallCornerRadius is { } radius) CardRoot.CornerRadius = new CornerRadius(radius);
            CardRoot.Padding = new Thickness(10, Math.Max(3, 6 + heightAdjustment / 8), 10, Math.Max(3, 6 + heightAdjustment / 8));
            CompactPanel.Visibility = Visibility.Visible;
            FullPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            CardRoot.Padding = new Thickness(16, Math.Max(8, 12 + verticalSpacing / 2), 16, Math.Max(8, 12 + verticalSpacing / 2));
            PriceBox.Margin = new Thickness(0, Math.Max(1, 4 + verticalSpacing / 2), 0, Math.Max(0, 2 + verticalSpacing / 3));
            TrendPanel.Margin = new Thickness(TrendPanel.Margin.Left, Math.Max(2, 6 + verticalSpacing), 0, 0);
            DetailsPanel.Margin = new Thickness(0, Math.Max(3, 8 + verticalSpacing), 0, Math.Max(1, 4 + verticalSpacing / 2));
            FundingText.Margin = new Thickness(0, Math.Max(2, 5 + verticalSpacing / 2), 0, Math.Max(1, 2 + verticalSpacing / 3));
            StatusPanel.Margin = new Thickness(0, Math.Max(2, 5 + verticalSpacing / 2), 0, 0);
        }
        DetailsPanel.Visibility = style == CardStyle.Large ? Visibility.Visible : Visibility.Collapsed;
        if (style == CardStyle.Medium)
        {
            TrendColumn.Width = new GridLength(104);
            Grid.SetRow(TrendPanel, 0); Grid.SetColumn(TrendPanel, 1);
            TrendPanel.Margin = new Thickness(12, Math.Max(1, 4 + verticalSpacing / 2), 0, 0);
            TrendTitle.Visibility = Visibility.Collapsed;
            PriceText.FontSize = 32;
        }
        if (!_showTrends) { TrendPanel.Visibility = Visibility.Collapsed; TrendColumn.Width = new GridLength(0); }
        if (compactMode)
        {
            Margin = new Thickness(0);
            CardRoot.Padding = new Thickness(10, 5, 10, 5);
            CardRoot.CornerRadius = new CornerRadius(0);
            CardRoot.BorderThickness = new Thickness(0, 0, 0, 1);
            CompactSymbol.Text = $"{market.Symbol} · {market.MarketLabel}";
            CompactSymbol.FontSize = 12;
            CompactSymbol.TextWrapping = TextWrapping.NoWrap;
            CompactSymbol.TextTrimming = TextTrimming.CharacterEllipsis;
            CompactPrice.FontSize = 18;
            CompactChangeBox.Visibility = changeMinutes > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (changeMinutes > 0) CompactChangeColumn.Width = new GridLength(0.75, GridUnitType.Star);
            CompactChangeBox.MaxWidth *= numberScale;
        }
        PriceText.FontSize *= numberScale;
        CompactPrice.FontSize *= numberScale;
        CompactChange.FontSize *= numberScale;
        PriceBox.Height *= numberScale;
        var numberFont = new FontFamily(monospace ? "Consolas" : "Segoe UI");
        PriceText.FontFamily = CompactPrice.FontFamily = CompactChange.FontFamily = HighText.FontFamily = LowText.FontFamily = numberFont;
        _ready = true;
    }
    public void Update(Ticker? ticker, FeedStatus status, IReadOnlyList<Ticker>? history, FundingQuote? funding = null, MarketChangeReference? changeReference = null, string? changeError = null)
    {
        var stale = ticker?.IsStale(DateTimeOffset.UtcNow) ?? false;
        var healthy = ticker is not null && !stale && status.Phase == FeedPhase.Live;
        var state = stale ? "行情已过期" : ticker is null && status.Phase == FeedPhase.Live ? "等待行情"
            : status.Phase == FeedPhase.Live ? "实时" : status.Phase == FeedPhase.Connecting ? "连接中" : "连接中断";
        if (ticker is not null && ticker != _rendered)
        {
            var direction = _rendered is null ? 0 : ticker.Last.CompareTo(_rendered.Last);
            PriceText.Foreground = CompactPrice.Foreground = direction > 0 ? Up : direction < 0 ? Down : Neutral;
            _rendered = ticker;
            CompactPrice.Text = PriceText.Text = PriceFormatter.Format(ticker.Last);
            UpdateCompactChartVisibility();
            var positive = ticker.ChangePercent >= 0;
            var brush = ticker.ChangePercent > 0 ? Up : ticker.ChangePercent < 0 ? Down : Neutral;
            ChangeText.Text = $"{(positive ? "↗ +" : "↘ ")}{ticker.ChangePercent.ToString("0.00", CultureInfo.InvariantCulture)}%";
            ChangeText.Foreground = brush;
            HighText.Text = PriceFormatter.Format(ticker.High24h);
            LowText.Text = PriceFormatter.Format(ticker.Low24h);
            TimeText.Text = ticker.ObservedAt.LocalDateTime.ToString("HH:mm:ss");
        }
        if (history is not null) { _history = history; DrawChart(); }
        PriceText.Opacity = CompactPrice.Opacity = healthy ? 1 : 0.45;
        StatusDot.Fill = healthy ? Up : Amber;
        StatusText.Text = state;
        if (_compactMode && _changeMinutes > 0) UpdateCompactChange(ticker, healthy, changeReference, changeError);
        var fundingTip = "";
        if (Market.IsPerpetual)
        {
            if (funding is not null && funding.Key != Market.Key) throw new InvalidOperationException("资金费率与卡片交易对不匹配。");
            var now = DateTimeOffset.UtcNow;
            var expired = funding?.IsStale(now) == true || (funding is not null && status.Phase != FeedPhase.Live);
            var pending = funding is not null && funding.NextFundingAt <= now;
            var suffix = expired ? "数据已过期" : pending ? "待结算更新" : funding is null ? "等待数据" : $"{funding.NextFundingAt.LocalDateTime:HH:mm} 结算";
            var rate = funding?.PercentText ?? "—";
            FundingText.Text = $"资金费率 {rate} · {suffix}";
            CompactFunding.Text = $"费率 {rate}" + (expired ? " · 过期" : pending ? " · 待更新" : "");
            FundingText.Opacity = CompactFunding.Opacity = expired || pending ? 0.5 : 1;
            var direction = funding is null ? "" : funding.Rate > 0 ? "多头付给空头" : funding.Rate < 0 ? "空头付给多头" : "当前费率为零";
            fundingTip = $"\n资金费率：{rate} · {suffix}"
                + (funding is null ? "" : $"\n{direction}；结算前费率仍可能变化\n下次结算：{funding.NextFundingAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n费率时间：{funding.ObservedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}")
                + "\n来源：Binance markPrice · 当期费率，非年化";
            FundingText.ToolTip = CompactFunding.ToolTip = fundingTip.TrimStart('\n');
            UpdateCompactChartVisibility();
        }
        // Compact health and detailed provenance remain accessible on hover.
        ToolTip = $"{Market.Label} · {ticker?.Source ?? (Market.Kind == MarketKind.UsStock ? "美股行情" : "Binance")} · 最新成交价（{Market.QuoteAsset}）\n{state} · {status.Detail}"
            + (ticker is null ? "" : $"\n行情时间：{ticker.ObservedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n24H：{ticker.ChangePercent:+0.00;-0.00;0.00}%")
            + fundingTip
            + "\n价格颜色：相对最近一次显示价格，涨绿跌红，首次或持平中性"
            + (_compactMode && _changeMinutes > 0 ? "\n" + CompactChange.ToolTip : "")
            + (_showTrends ? $"\n趋势：最近 {FormatTrendSpan(TrendMinutes)} · 本次运行采样" : "")
            + "\n按住拖动；右键打开设置";
    }
    private void UpdateCompactChange(Ticker? ticker, bool healthy, MarketChangeReference? reference, string? error)
    {
        var label = Market.Kind == MarketKind.UsStock && _changeMinutes == 1440 ? "日" : MarketChangeReference.PeriodLabel(_changeMinutes);
        decimal? percent = null;
        var detail = error ?? "等待历史价格";
        if (_changeMinutes == 1440)
        {
            percent = ticker?.ChangePercent;
            detail = Market.Kind == MarketKind.UsStock ? "日涨跌：相对前一交易日收盘价" : "24H 涨跌：交易所滚动 24 小时最新价相对开盘价";
        }
        else if (Market.Kind == MarketKind.UsStock) detail = "美股暂不支持该短周期，请选择日涨跌。";
        else if (reference is not null && ticker is not null)
        {
            if (reference.Minutes != _changeMinutes) throw new InvalidOperationException("涨跌幅历史价格的区间不匹配。");
            percent = reference.Percent(ticker, DateTimeOffset.UtcNow);
            if (percent is not null) detail = $"{label} 涨跌（≈）：最新价相对 {reference.ObservedAt.LocalDateTime:MM-dd HH:mm:ss} 的分钟收盘价\n分钟粒度近似，基准最多早约 60 秒；来源：{ticker.Source} K 线\n历史价格读取时间：{reference.FetchedAt.LocalDateTime:HH:mm:ss}";
        }
        CompactChange.Text = label + " " + (percent is { } value ? (_changeMinutes == 1440 ? "" : "≈") + value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%" : "—");
        CompactChange.Foreground = percent > 0 ? Up : percent < 0 ? Down : Neutral;
        CompactChange.Opacity = healthy && percent is not null ? 1 : 0.45;
        CompactChange.ToolTip = detail;
    }
    private void DrawChart()
    {
        var canvas = _style == CardStyle.Small ? SmallChartCanvas : ChartCanvas;
        var line = _style == CardStyle.Small ? SmallChartLine : ChartLine;
        if (!_showTrends || (_style == CardStyle.Small && SmallChart.Visibility != Visibility.Visible) || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;
        var end = DateTimeOffset.UtcNow;
        _lastChartDraw = end;
        var values = _history.Where(x => x.ObservedAt <= end && end - x.ObservedAt <= TimeSpan.FromMinutes(TrendMinutes)).ToArray();
        ChartEmpty.Visibility = values.Length < 2 ? Visibility.Visible : Visibility.Collapsed;
        if (values.Length < 2) { line.Data = null; return; }
        line.Stroke = values[^1].Last > values[0].Last ? Up : values[^1].Last < values[0].Last ? Down : Neutral;
        var low = values.Min(x => x.Last);
        var range = values.Max(x => x.Last) - low;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
            for (var i = 0; i < values.Length; i++)
            {
                var point = new Point(canvas.ActualWidth * (1 - (end - values[i].ObservedAt).TotalSeconds / (TrendMinutes * 60)),
                    range == 0 ? canvas.ActualHeight / 2 : 2 + (1 - (double)((values[i].Last - low) / range)) * Math.Max(0, canvas.ActualHeight - 4));
                if (i == 0 || values[i].ObservedAt - values[i - 1].ObservedAt > TimeSpan.FromSeconds(15)) context.BeginFigure(point, false, false);
                else context.LineTo(point, true, false);
            }
        geometry.Freeze();
        line.Data = geometry;
    }
    private void CompactSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCompactChartVisibility();
    private void UpdateCompactChartVisibility()
    {
        if (!_ready || _compactMode || _style != CardStyle.Small) return;
        double TextWidth(TextBlock text) => new FormattedText(text.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Neutral,
            VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
        var symbolWidth = Math.Max(TextWidth(CompactSymbol), Market.IsPerpetual ? TextWidth(CompactFunding) : 0);
        var visible = _showTrends && CompactPanel.ActualWidth >= symbolWidth + TextWidth(CompactPrice) + 80;
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (SmallChart.Visibility == visibility) return;
        SmallChart.Visibility = visibility;
        CompactSymbolColumn.Width = visible ? GridLength.Auto : new GridLength(1.05, GridUnitType.Star);
        _lastChartDraw = DateTimeOffset.MinValue;
        if (!visible) SmallChartLine.Data = null;
    }
    private void ChartSizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();
    private void TrendSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || TrendSelector.SelectedItem is not TrendOption selected) return;
        TrendMinutes = selected.Minutes;
        _lastChartDraw = DateTimeOffset.MinValue;
        TrendSpanChanged?.Invoke(this, EventArgs.Empty);
        DrawChart();
    }
    private static string FormatTrendSpan(int minutes) => minutes switch
    {
        10080 => "1 周",
        1440 => "1 天",
        int value when value % 60 == 0 => $"{value / 60} 小时",
        _ => $"{minutes} 分钟"
    };
}
