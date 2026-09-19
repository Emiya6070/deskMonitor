using System;
using System.Windows;
using System.Windows.Controls;

namespace DeskMonitor;

/// <summary>Equal-width columns in reading order; each row fits its tallest card.</summary>
public sealed class CardColumnsPanel : Panel
{
    public bool FreeLayout { get; set; }
    public static readonly DependencyProperty CardColumnProperty = DependencyProperty.RegisterAttached(
        "CardColumn", typeof(int), typeof(CardColumnsPanel), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static void SetCardColumn(UIElement child, int column) => child.SetValue(CardColumnProperty, column);
    public static int GetCardColumn(UIElement child) => (int)child.GetValue(CardColumnProperty);
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(CardColumnsPanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is int count && count is 1 or 2);
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    private double Gap => Columns == 2 ? 10 : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 400 * Columns : availableSize.Width;
        var cellWidth = Math.Max(0, (width - Gap) / Columns);
        if (FreeLayout)
        {
            var heights = new double[Columns];
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(cellWidth, double.PositiveInfinity));
                heights[Math.Clamp(GetCardColumn(child), 0, Columns - 1)] += child.DesiredSize.Height;
            }
            return new Size(width, Columns == 1 ? heights[0] : Math.Max(heights[0], heights[1]));
        }
        double height = 0;
        for (var i = 0; i < InternalChildren.Count; i += Columns)
        {
            double rowHeight = 0;
            for (var j = i; j < Math.Min(i + Columns, InternalChildren.Count); j++)
            {
                InternalChildren[j].Measure(new Size(cellWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, InternalChildren[j].DesiredSize.Height);
            }
            height += rowHeight;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = Math.Max(0, (finalSize.Width - Gap) / Columns);
        if (FreeLayout)
        {
            var heights = new double[Columns];
            foreach (UIElement child in InternalChildren)
            {
                var column = Math.Clamp(GetCardColumn(child), 0, Columns - 1);
                child.Arrange(new Rect(column * (width + Gap), heights[column], width, child.DesiredSize.Height));
                heights[column] += child.DesiredSize.Height;
            }
            return finalSize;
        }
        double y = 0;
        for (var i = 0; i < InternalChildren.Count; i += Columns)
        {
            double rowHeight = 0;
            for (var j = i; j < Math.Min(i + Columns, InternalChildren.Count); j++)
            {
                var child = InternalChildren[j];
                child.Arrange(new Rect((j - i) * (width + Gap), y, width, child.DesiredSize.Height));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            y += rowHeight;
        }
        return finalSize;
    }
}
