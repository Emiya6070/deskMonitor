using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskMonitor.Core;

namespace DeskMonitor;
public partial class UsageCard : UserControl
{
    private readonly Brush _muted = ThemeManager.Brush("Muted"), _warning = ThemeManager.Brush("DownBrush");
    private CodexUsage? _lastUsage;
    private string? _lastError;
    private bool _lastBusy, _lastStale;
    private int _lastReset;
    private long _lastMinute = -1;
    public event EventHandler? RefreshRequested;
    public UsageCard(CardStyle style, double textScale = 1, double numberScale = 1, bool monospace = false, double? smallCornerRadius = null, double extraHeight = 0)
    {
        InitializeComponent();
        // Let WPF measure the visible rows and font sizes; extra space is optional.
        QuotaRows.Margin = new Thickness(0, 6 + extraHeight / 2, 0, extraHeight / 2);
        LayoutTransform = new ScaleTransform(textScale, textScale);
        var compact = style == CardStyle.Small;
        if (compact && smallCornerRadius is { } radius) Root.CornerRadius = new CornerRadius(radius);
        Root.Padding = compact ? new Thickness(10, 6, 10, 6) : new Thickness(16, 10, 16, 10);
        foreach (var element in new UIElement[] { PrimaryBar, SecondaryBar, PrimaryReset, SecondaryReset, RefreshButton })
            element.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        DetailsText.Visibility = style == CardStyle.Large ? Visibility.Visible : Visibility.Collapsed;
        PrimaryValue.FontSize = SecondaryValue.FontSize = (compact ? 15 : 18) * numberScale;
        PrimaryValue.FontFamily = SecondaryValue.FontFamily = new FontFamily(monospace ? "Consolas" : "Segoe UI");
    }
    private void RefreshClicked(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    public void Update(CodexUsage? usage, string? error, bool busy)
    {
        var now = DateTimeOffset.UtcNow;
        var stale = usage is not null && (error is not null || usage.IsStale(now));
        var reset = (usage?.Primary?.AwaitingReset(now) == true ? 1 : 0) + (usage?.Secondary?.AwaitingReset(now) == true ? 2 : 0);
        var minute = now.ToUnixTimeSeconds() / 60;
        if (ReferenceEquals(usage, _lastUsage) && error == _lastError && busy == _lastBusy && stale == _lastStale && reset == _lastReset && minute == _lastMinute) return;
        _lastUsage = usage; _lastError = error; _lastBusy = busy; _lastStale = stale; _lastReset = reset; _lastMinute = minute;
        var first = usage?.Primary;
        var second = usage?.Secondary;
        if ((first?.DurationMinutes is >= 1440 && second is null)
            || (first?.DurationMinutes is { } a && second?.DurationMinutes is { } b && a > b))
            (first, second) = (second, first);
        SetWindow(first, PrimaryLabel, PrimaryValue, PrimaryBar, PrimaryReset, "短周期", now, stale);
        SetWindow(second, SecondaryLabel, SecondaryValue, SecondaryBar, SecondaryReset, "长周期", now, stale);
        PrimaryRow.Visibility = first is null ? Visibility.Collapsed : Visibility.Visible;
        SecondaryRow.Visibility = second is null ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = busy ? "正在更新…"
            : error is not null ? usage is null ? "读取失败，将自动重试" : $"更新失败 · 上次 {usage.FetchedAt.ToLocalTime():HH:mm:ss}"
            : usage is null ? "等待读取额度" : stale ? "数据已过期" : $"更新于 {usage.FetchedAt.ToLocalTime():HH:mm:ss}";
        StatusText.Foreground = error is not null || stale ? _warning : _muted;
        RefreshButton.IsEnabled = !busy;
        ToolTip = "Codex 账户共享额度（剩余百分比）"
            + (first is null ? "" : $"\n{PrimaryLabel.Text}：{PrimaryValue.Text} · {PrimaryReset.Text}")
            + (second is null ? "" : $"\n{SecondaryLabel.Text}：{SecondaryValue.Text} · {SecondaryReset.Text}")
            + $"\n{StatusText.Text}"
            + (error is null ? "" : $"\n{error}\n将按每分钟周期自动重试。" + (usage is null ? "" : "当前显示上次成功结果。"))
            + "\n右键可刷新用量或打开设置";
    }
    private static void SetWindow(QuotaWindow? window, TextBlock label, TextBlock value, ProgressBar bar, TextBlock reset, string fallback, DateTimeOffset now, bool stale)
    {
        label.Text = window?.Label ?? fallback;
        var waiting = window?.AwaitingReset(now) == true;
        value.Text = window is null ? "—" : waiting ? "待更新" : $"剩余 {window.RemainingPercent:0.#}%";
        value.Opacity = stale || waiting ? 0.5 : 1;
        bar.Value = window?.RemainingPercent ?? 0;
        bar.Opacity = stale || waiting ? 0.35 : 1;
        if (window?.ResetsAt is not { } at) { reset.Text = window is null ? "未提供该窗口" : "未提供重置时间"; return; }
        var left = at - now;
        var duration = left.TotalDays >= 1 ? $"{(int)left.TotalDays}天 {left.Hours}小时" : left.TotalHours >= 1 ? $"{(int)left.TotalHours}小时 {left.Minutes}分钟" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}分钟";
        reset.Text = waiting ? "已到重置时间，等待服务器确认" : $"{duration}后重置 · {at.ToLocalTime():MM-dd HH:mm}";
    }
}
