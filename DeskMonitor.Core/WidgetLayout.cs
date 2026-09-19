namespace DeskMonitor.Core;
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom);
[Flags]
public enum DockedCorners { None = 0, TopLeft = 1, TopRight = 2, BottomRight = 4, BottomLeft = 8 }
// Windows can feed the corrected rectangle back into the next WM_MOVING event.
// Accumulate movement from the cursor instead, so slow drags can escape a snapped edge.
public sealed class SnapDrag
{
    private readonly PixelRect _start;
    private readonly int _cursorX, _cursorY;
    private PixelRect _area;
    private int? _lockedX, _lockedY;
    public SnapDrag(PixelRect start, int cursorX, int cursorY, PixelRect area)
    {
        _start = start; _cursorX = cursorX; _cursorY = cursorY; _area = area;
        if (start.Left == area.Left || start.Right == area.Right) _lockedX = start.Left;
        if (start.Top == area.Top || start.Bottom == area.Bottom) _lockedY = start.Top;
    }
    public PixelRect Move(int cursorX, int cursorY, PixelRect proposed, PixelRect area, int snapDistance, int releaseDistance)
    {
        if (snapDistance < 0 || releaseDistance <= snapDistance) throw new ArgumentOutOfRangeException(nameof(releaseDistance));
        if (area != _area) { _lockedX = null; _lockedY = null; _area = area; }
        var x = _start.Left + cursorX - _cursorX;
        var y = _start.Top + cursorY - _cursorY;
        var width = proposed.Right - proposed.Left;
        var height = proposed.Bottom - proposed.Top;
        var snapped = WidgetLayout.Snap(new(x, y, x + width, y + height), area, snapDistance);
        x = MoveAxis(x, snapped.Left, snapped.Left == area.Left || snapped.Right == area.Right, ref _lockedX, releaseDistance);
        y = MoveAxis(y, snapped.Top, snapped.Top == area.Top || snapped.Bottom == area.Bottom, ref _lockedY, releaseDistance);
        return new(x, y, x + width, y + height);
    }
    private static int MoveAxis(int raw, int snapped, bool atEdge, ref int? locked, int releaseDistance)
    {
        if (locked is { } edge)
        {
            if (Math.Abs(raw - edge) < releaseDistance) return edge;
            locked = null;
            return raw;
        }
        if (atEdge) locked = snapped;
        return snapped;
    }
}
public static class WidgetLayout
{
    public static bool UseSavedSize(bool resetSize, bool allowResize, bool lockCustomSize)
        => !resetSize && (allowResize || lockCustomSize);
    public static bool ShouldResetSize(bool layoutChanged, bool wasResizeAllowed, bool resizeAllowed, bool lockCustomSize = false)
        => layoutChanged && !lockCustomSize && !(wasResizeAllowed && !resizeAllowed);
    public static bool ShouldAutoFit(bool allowResize, bool lockCustomSize)
        => !allowResize && !lockCustomSize;
    public static DockedCorners CornersAtWorkArea(PixelRect rect, PixelRect area)
    {
        // One physical pixel accommodates rounding between WPF DIPs and monitor pixels.
        var left = Math.Abs(rect.Left - area.Left) <= 1;
        var right = Math.Abs(rect.Right - area.Right) <= 1;
        var top = Math.Abs(rect.Top - area.Top) <= 1;
        var bottom = Math.Abs(rect.Bottom - area.Bottom) <= 1;
        return (left && top ? DockedCorners.TopLeft : 0) | (right && top ? DockedCorners.TopRight : 0)
            | (right && bottom ? DockedCorners.BottomRight : 0) | (left && bottom ? DockedCorners.BottomLeft : 0);
    }
    public static double MarketHeight(CardStyle style, double numberScale = 1, double heightAdjustment = 0)
    {
        if (!double.IsFinite(heightAdjustment) || heightAdjustment is < -12 or > 48) throw new ArgumentOutOfRangeException(nameof(heightAdjustment));
        return (style switch { CardStyle.Small => 54, CardStyle.Medium => 150, CardStyle.Large => 266, _ => throw new ArgumentOutOfRangeException(nameof(style)) })
            + (style == CardStyle.Small ? 23 : 43) * Math.Max(0, numberScale - 1) + heightAdjustment;
    }
    public static double FundingHeight(CardStyle style) => style == CardStyle.Small ? 0 : 24;
    public static double UsageHeight(CardStyle style, double numberScale = 1) => (style switch { CardStyle.Small => 96, CardStyle.Medium => 188, CardStyle.Large => 232, _ => throw new ArgumentOutOfRangeException(nameof(style)) }) + 36 * Math.Max(0, numberScale - 1);
    public static (double Width, double Height) Preset(CardStyle style, int count, bool usage = false, double textScale = 1, double numberScale = 1, bool hideHeader = false, int contractCount = 0, double marketHeightAdjustment = 0, int? marketCount = null) => style switch
    {
        CardStyle.Small => (280 * textScale, 12 + (count * MarketHeight(style, numberScale) + (marketCount ?? count) * marketHeightAdjustment + contractCount * FundingHeight(style) + (usage ? UsageHeight(style, numberScale) : 0)) * textScale),
        CardStyle.Medium => (360 * textScale, (hideHeader ? 22 : 66) + (count * MarketHeight(style, numberScale) + (marketCount ?? count) * marketHeightAdjustment + contractCount * FundingHeight(style) + (usage ? UsageHeight(style, numberScale) : 0)) * textScale),
        CardStyle.Large => (400 * textScale, (hideHeader ? 22 : 66) + (count * MarketHeight(style, numberScale) + (marketCount ?? count) * marketHeightAdjustment + contractCount * FundingHeight(style) + (usage ? UsageHeight(style, numberScale) : 0)) * textScale),
        _ => throw new ArgumentOutOfRangeException(nameof(style))
    };
    public static PixelRect Snap(PixelRect rect, PixelRect area, int distance)
    {
        var dx = Math.Abs(rect.Left - area.Left) <= distance ? area.Left - rect.Left
            : Math.Abs(rect.Right - area.Right) <= distance ? area.Right - rect.Right : 0;
        var dy = Math.Abs(rect.Top - area.Top) <= distance ? area.Top - rect.Top
            : Math.Abs(rect.Bottom - area.Bottom) <= distance ? area.Bottom - rect.Bottom : 0;
        return new(rect.Left + dx, rect.Top + dy, rect.Right + dx, rect.Bottom + dy);
    }
}
