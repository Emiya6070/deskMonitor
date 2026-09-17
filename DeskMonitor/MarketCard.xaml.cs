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
    private DateTimeOffset _lastChartDraw;
    public bool NeedsTrendRefresh => _showTrends && (_style != CardStyle.Small || SmallChart.Visibility == Visibility.Visible) && DateTimeOffset.UtcNow - _lastChartDraw >= TimeSpan.FromSeconds(5);
    public int TrendMinutes { get; private set; }
    public event EventHandler? TrendSpanChanged;
    private sealed record TrendOption(int Minutes, string Label);
    public MarketSymbol Market { get; }
    public MarketCard(MarketSymbol market, CardStyle style, int trendMinutes = 2, double textScale = 1, double numberScale = 1, bool monospace = false, bool showTrends = true, double? smallCornerRadius = null)
    {
        InitializeComponent();
        Market = market;
        _style = style;
        _showTrends = showTrends;
        LayoutTransform = new ScaleTransform(textScale, textScale);
        if (!TrendHistory.IsSupportedSpan(trendMinutes)) throw new ArgumentOutOfRangeException(nameof(trendMinutes));
        TrendMinutes = trendMinutes;
        TrendSelector.ItemsSource = new[] { new TrendOption(2, "2 分钟"), new TrendOption(5, "5 分钟"), new TrendOption(15, "15 分钟"), new TrendOption(60, "1 小时") };
        TrendSelector.SelectedIndex = Array.IndexOf(new[] { 2, 5, 15, 60 }, trendMinutes);
        SymbolText.Text = $"{market.BaseAsset} / {market.QuoteAsset}";
        HighLabel.Text = $"24H 最高 · {market.QuoteAsset}";
        LowLabel.Text = $"24H 最低 · {market.QuoteAsset}";
        KindText.Text = market.MarketLabel;
        CompactSymbol.Text = market.Label;
        Height = WidgetLayout.MarketHeight(style, numberScale) - 8;
        if (market.Kind != MarketKind.Spot)
        {
            Height += WidgetLayout.FundingHeight(style);
            CompactFunding.Visibility = FundingText.Visibility = Visibility.Visible;
            CompactFunding.Text = "费率 —";
            FundingText.Text = "资金费率 — · 等待数据";
            CompactSymbol.TextWrapping = TextWrapping.NoWrap;
            CompactSymbol.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        if (style == CardStyle.Small)
        {
            if (smallCornerRadius is { } radius) CardRoot.CornerRadius = new CornerRadius(radius);
            CardRoot.Padding = new Thickness(10, 6, 10, 6);
            CompactPanel.Visibility = Visibility.Visible;
            FullPanel.Visibility = Visibility.Collapsed;
        }
        DetailsPanel.Visibility = style == CardStyle.Large ? Visibility.Visible : Visibility.Collapsed;
        if (style == CardStyle.Medium)
        {
            TrendColumn.Width = new GridLength(104);
            Grid.SetRow(TrendPanel, 0); Grid.SetColumn(TrendPanel, 1);
            TrendPanel.Margin = new Thickness(12, 4, 0, 0);
            TrendTitle.Visibility = Visibility.Collapsed;
            PriceText.FontSize = 32;
        }
        if (!showTrends) { TrendPanel.Visibility = Visibility.Collapsed; TrendColumn.Width = new GridLength(0); }
        PriceText.FontSize *= numberScale;
        CompactPrice.FontSize *= numberScale;
        PriceBox.Height *= numberScale;
        var numberFont = new FontFamily(monospace ? "Consolas" : "Segoe UI");
        PriceText.FontFamily = CompactPrice.FontFamily = HighText.FontFamily = LowText.FontFamily = numberFont;
        _ready = true;
    }
    public void Update(Ticker? ticker, FeedStatus status, IReadOnlyList<Ticker>? history, FundingQuote? funding = null)
    {
        var stale = ticker?.IsStale(DateTimeOffset.UtcNow) ?? false;
        var healthy = ticker is not null && !stale && status.Phase == FeedPhase.Live;
        var state = stale ? "行情已过期" : ticker is null && status.Phase == FeedPhase.Live ? "等待行情"
            : status.Phase == FeedPhase.Live ? "实时" : status.Phase == FeedPhase.Connecting ? "连接中" : "连接中断";
        if (ticker is not null && ticker != _rendered)
        {
            _rendered = ticker;
            CompactPrice.Text = PriceText.Text = PriceFormatter.Format(ticker.Last);
            UpdateCompactChartVisibility();
            var positive = ticker.ChangePercent >= 0;
            var brush = ticker.ChangePercent > 0 ? Up : ticker.ChangePercent < 0 ? Down : Neutral;
            ChangeText.Text = $"{(positive ? "↗ +" : "↘ ")}{ticker.ChangePercent.ToString("0.00", CultureInfo.InvariantCulture)}%";
            ChangeText.Foreground = PriceText.Foreground = CompactPrice.Foreground = brush;
            HighText.Text = PriceFormatter.Format(ticker.High24h);
            LowText.Text = PriceFormatter.Format(ticker.Low24h);
            TimeText.Text = ticker.ObservedAt.LocalDateTime.ToString("HH:mm:ss");
        }
        if (history is not null) { _history = history; DrawChart(); }
        PriceText.Opacity = CompactPrice.Opacity = healthy ? 1 : 0.45;
        StatusDot.Fill = healthy ? Up : Amber;
        StatusText.Text = state;
        var fundingTip = "";
        if (Market.Kind != MarketKind.Spot)
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
        ToolTip = $"{Market.Label} · Binance · 最新成交价（{Market.QuoteAsset}）\n{state} · {status.Detail}"
            + (ticker is null ? "" : $"\n行情时间：{ticker.ObservedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n24H：{ticker.ChangePercent:+0.00;-0.00;0.00}%")
            + fundingTip
            + $"\n价格颜色：24H 涨绿跌红，持平中性\n趋势：最近 {TrendMinutes} 分钟 · 本次运行采样\n按住拖动；右键打开设置";
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
        if (!_ready || _style != CardStyle.Small) return;
        double TextWidth(TextBlock text) => new FormattedText(text.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Neutral,
            VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
        var symbolWidth = Math.Max(TextWidth(CompactSymbol), Market.Kind == MarketKind.Spot ? 0 : TextWidth(CompactFunding));
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
}
