namespace DeskMonitor.Core;
public enum DateProgressPeriod { Week, Month }
public enum DateProgressStyle { Metro, Grid }
public enum DateProgressPlacement { Left, Bottom }
public enum DateProgressHorizontalLayout { Left, Center, Stretch }
public sealed record DateProgressSettings
{
    public bool Enabled { get; init; }
    public DateProgressPeriod Period { get; init; } = DateProgressPeriod.Week;
    public DateProgressStyle Style { get; init; } = DateProgressStyle.Metro;
    public DateProgressPlacement Placement { get; init; } = DateProgressPlacement.Left;
    public DateProgressHorizontalLayout HorizontalLayout { get; init; } = DateProgressHorizontalLayout.Left;
    public double DotSize { get; init; } = 10;
    public double Spacing { get; init; } = 6;
    public string PastColor { get; init; } = "#EF7272";
    public string FutureColor { get; init; } = "#77C99B";
    public bool ShowLabels { get; init; } = true;
    public bool ShowHeading { get; init; } = true;
    public void Validate()
    {
        static bool Color(string? s) => s is { Length: 7 } && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);
        if (!Enum.IsDefined(Period) || !Enum.IsDefined(Style) || !Enum.IsDefined(Placement) || !Enum.IsDefined(HorizontalLayout)
            || !double.IsFinite(DotSize) || DotSize is < 4 or > 16
            || !double.IsFinite(Spacing) || Spacing is < 2 or > 12 || !Color(PastColor) || !Color(FutureColor))
            throw new InvalidDataException("日期进度样式无效（颜色格式为 #RRGGBB）。");
    }
}
public sealed record ProgressDay(DateOnly Date, bool IsPast, bool IsToday);
public static class DateProgress
{
    public static ProgressDay[] Days(DateOnly today, DateProgressPeriod period)
    {
        if (!Enum.IsDefined(period)) throw new ArgumentOutOfRangeException(nameof(period));
        var start = period == DateProgressPeriod.Week ? today.AddDays(-(((int)today.DayOfWeek + 6) % 7)) : new DateOnly(today.Year, today.Month, 1);
        var count = period == DateProgressPeriod.Week ? 7 : DateTime.DaysInMonth(today.Year, today.Month);
        return Enumerable.Range(0, count).Select(i => start.AddDays(i)).Select(day => new ProgressDay(day, day < today, day == today)).ToArray();
    }
}
