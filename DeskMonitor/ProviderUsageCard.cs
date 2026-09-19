using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskMonitor.Core;

namespace DeskMonitor;
public sealed class ProviderUsageCard : UserControl
{
    public ProviderUsageSettings Settings { get; }
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = new() { FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) };
    private readonly double _numberScale;
    private ProviderUsageSnapshot? _last;
    private string? _lastError;
    private long _minute = -1;
    public ProviderUsageCard(ProviderUsageSettings settings, Preferences preferences)
    {
        Settings = settings; _numberScale = preferences.NumberScale;
        Margin = new Thickness(0, 0, 0, 8);
        LayoutTransform = new ScaleTransform(preferences.TextScale, preferences.TextScale);
        var panel = new StackPanel();
        var title = new TextBlock { Text = settings.Title, FontWeight = FontWeights.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.ForegroundProperty, "UsageAccent");
        panel.Children.Add(title); panel.Children.Add(_rows); panel.Children.Add(_status);
        var border = new Border { Child = panel, Padding = new Thickness(preferences.Style == CardStyle.Small ? 10 : 16, 10, 10, 10), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "UsageBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        border.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        if (preferences.Style == CardStyle.Small && preferences.SmallCornerRadius is { } radius) border.CornerRadius = new CornerRadius(radius);
        Content = border;
        Update(null, "等待读取…");
    }
    public void Update(ProviderUsageSnapshot? snapshot, string? error)
    {
        var now = DateTimeOffset.UtcNow;
        var minute = now.ToUnixTimeSeconds() / 60;
        if (ReferenceEquals(_last, snapshot) && error == _lastError && minute == _minute) return;
        _last = snapshot; _lastError = error; _minute = minute;
        var stale = snapshot is not null && now - snapshot.ObservedAt > TimeSpan.FromMinutes(15);
        _rows.Children.Clear();
        if (snapshot is not null)
            foreach (var metric in snapshot.Metrics)
            {
                var waiting = metric.ResetsAt is { } reset && reset <= now;
                var row = new TextBlock { Text = metric.Label + "  " + (waiting ? "待更新" : metric.Value), FontSize = 14 * _numberScale, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Opacity = stale || error is not null || waiting ? 0.5 : 1 };
                _rows.Children.Add(row);
                var low = !waiting && metric.RemainingPercent is <= 20;
                row.SetResourceReference(TextBlock.ForegroundProperty, low ? "DownBrush" : "TextBrush");
                if (metric.RemainingPercent is { } percent)
                {
                    var bar = new ProgressBar { Value = percent, Minimum = 0, Maximum = 100, Height = 3, Margin = new Thickness(0, 4, 0, 0), Opacity = row.Opacity };
                    bar.SetResourceReference(Control.ForegroundProperty, low ? "DownBrush" : "UsageAccent");
                    _rows.Children.Add(bar);
                }
                if (metric.ResetsAt is { } at) _rows.Children.Add(new TextBlock { Text = $"{at.LocalDateTime:MM-dd HH:mm} 重置", FontWeight = FontWeights.Bold, FontSize = 10, Margin = new Thickness(0, 3, 0, 0) });
            }
        _status.Text = error ?? (snapshot is null ? "尚无数据" : $"{(stale ? "快照已过期 · " : "")}{snapshot.ObservedAt.LocalDateTime:MM-dd HH:mm} · {snapshot.Scope}");
        _status.SetResourceReference(TextBlock.ForegroundProperty, error is not null || stale ? "WarningBrush" : "Muted");
        ToolTip = _status.Text + (snapshot is null ? "" : "\n" + snapshot.Scope);
    }
}
