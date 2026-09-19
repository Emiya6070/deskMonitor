using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using DeskMonitor.Core;

namespace DeskMonitor;
public sealed class DateProgressColumn : Border
{
    private readonly DateProgressSettings _settings;
    private DateOnly? _lastDay;
    public DateProgressColumn(DateProgressSettings settings, double textScale)
    {
        _settings = settings;
        var horizontal = settings.Placement == DateProgressPlacement.Bottom;
        LayoutTransform = new ScaleTransform(textScale, textScale);
        Padding = new Thickness(8); Margin = horizontal ? new Thickness(0, 8, 0, 8) : new Thickness(0, 0, 8, 8);
        BorderThickness = horizontal ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 1, 0);
        SetResourceReference(BorderBrushProperty, "BorderBrush");
        Refresh();
    }
    public void Refresh()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today == _lastDay) return;
        _lastDay = today;
        var days = DateProgress.Days(today, _settings.Period);
        var horizontal = _settings.Placement == DateProgressPlacement.Bottom;
        var panel = new StackPanel();
        if (_settings.ShowHeading)
        {
            var heading = new StackPanel { Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical };
            heading.Children.Add(new TextBlock { Text = _settings.Period == DateProgressPeriod.Week ? "本周" : $"{today.Month} 月", FontWeight = FontWeights.SemiBold, FontSize = 12 });
            var caption = new TextBlock { Text = $"已过 {days.Count(d => d.IsPast)}/{days.Length} 天", FontSize = 10, Margin = horizontal ? new Thickness(10, 2, 0, 8) : new Thickness(0, 5, 0, 10) };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); heading.Children.Add(caption); panel.Children.Add(heading);
        }
        Panel dots = horizontal ? new DateDotsPanel(_settings.HorizontalLayout) : _settings.Style == DateProgressStyle.Metro
            ? new StackPanel() : new UniformGrid { Columns = _settings.Period == DateProgressPeriod.Week ? 1 : 3 };
        var past = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_settings.PastColor)); past.Freeze();
        var future = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_settings.FutureColor)); future.Freeze();
        foreach (var day in days)
        {
            var color = day.IsPast ? past : future;
            var beside = !horizontal && _settings.Style == DateProgressStyle.Metro;
            var row = new StackPanel { Orientation = beside ? Orientation.Horizontal : Orientation.Vertical, Margin = horizontal ? new Thickness(_settings.Spacing / 2, 4, _settings.Spacing / 2, 4) : new Thickness(2, _settings.Spacing / 2, 2, _settings.Spacing / 2), ToolTip = $"{day.Date:yyyy-MM-dd} · {(day.IsToday ? "今天" : day.IsPast ? "已过" : "未过")}" };
            var ring = new Border { Width = _settings.DotSize + 6, Height = _settings.DotSize + 6, CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(day.IsToday ? 1.5 : 0), Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = horizontal ? HorizontalAlignment.Center : HorizontalAlignment.Left };
            ring.SetResourceReference(BorderBrushProperty, "TextBrush");
            ring.Child = new Ellipse { Fill = color }; row.Children.Add(ring);
            if (_settings.ShowLabels)
            {
                var label = _settings.Period == DateProgressPeriod.Week ? "周" + "一二三四五六日"[((int)day.Date.DayOfWeek + 6) % 7] : day.Date.Day.ToString();
                row.Children.Add(new TextBlock { Text = day.IsToday && beside ? label + " · 今" : label, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = horizontal ? HorizontalAlignment.Center : HorizontalAlignment.Left, Margin = new Thickness(beside ? 5 : 0, 2, 0, 0) });
            }
            if (_settings.Style == DateProgressStyle.Metro)
            {
                var station = new Grid();
                var cellWidth = Math.Max(_settings.ShowLabels ? 28 : 0, _settings.DotSize + 6) + _settings.Spacing;
                if (horizontal) station.MinWidth = cellWidth;
                var rail = horizontal
                    ? new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(day == days[0] ? cellWidth / 2 : 0, 4 + (_settings.DotSize + 6) / 2 - 1, day == days[^1] ? cellWidth / 2 : 0, 0), Background = color, Opacity = 0.35 }
                    : new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2 + (_settings.DotSize + 6) / 2 - 1, day == days[0] ? (_settings.DotSize + 6) / 2 : 0, 0, day == days[^1] ? (_settings.DotSize + 6) / 2 : 0), Background = color, Opacity = 0.35 };
                station.Children.Add(rail); station.Children.Add(row); dots.Children.Add(station);
            }
            else
            {
                if (horizontal) row.MinWidth = Math.Max(_settings.ShowLabels ? 28 : 0, _settings.DotSize + 6);
                dots.Children.Add(row);
            }
        }
        panel.Children.Add(dots);
        Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}

internal sealed class DateDotsPanel(DateProgressHorizontalLayout layout) : Panel
{
    private double _cellWidth;
    private double _cellHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        _cellWidth = 0;
        _cellHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _cellWidth = Math.Max(_cellWidth, child.DesiredSize.Width);
            _cellHeight = Math.Max(_cellHeight, child.DesiredSize.Height);
        }
        if (InternalChildren.Count == 0) return new Size();
        var width = double.IsInfinity(availableSize.Width) ? _cellWidth * InternalChildren.Count : Math.Max(_cellWidth, availableSize.Width);
        var columns = Math.Max(1, Math.Min(InternalChildren.Count, (int)Math.Floor(width / Math.Max(1, _cellWidth))));
        var rows = (InternalChildren.Count + columns - 1) / columns;
        return new Size(width, rows * _cellHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0) return finalSize;
        var columns = Math.Max(1, Math.Min(InternalChildren.Count, (int)Math.Floor(finalSize.Width / Math.Max(1, _cellWidth))));
        var index = 0;
        var row = 0;
        while (index < InternalChildren.Count)
        {
            var count = Math.Min(columns, InternalChildren.Count - index);
            var width = layout == DateProgressHorizontalLayout.Stretch ? finalSize.Width / count : _cellWidth;
            var offset = layout == DateProgressHorizontalLayout.Center ? (finalSize.Width - width * count) / 2 : 0;
            for (var column = 0; column < count; column++)
                InternalChildren[index++].Arrange(new Rect(offset + column * width, row * _cellHeight, width, _cellHeight));
            row++;
        }
        return finalSize;
    }
}
