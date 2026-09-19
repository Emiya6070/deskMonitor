using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskMonitor;
using DeskMonitor.Core;

internal static class DateLayoutChecks
{
    public static void Run(string output)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
        foreach (var skin in new[] { Skin.Forest, Skin.Paper })
        foreach (var style in Enum.GetValues<DateProgressStyle>())
        foreach (var period in Enum.GetValues<DateProgressPeriod>())
        foreach (var layout in Enum.GetValues<DateProgressHorizontalLayout>())
        {
            ThemeManager.Apply(skin);
            var showHeading = layout != DateProgressHorizontalLayout.Stretch;
            var date = new DateProgressColumn(new() { Enabled = true, Placement = DateProgressPlacement.Bottom, Period = period, Style = style,
                HorizontalLayout = layout, ShowHeading = showHeading, DotSize = 16, Spacing = 12 }, 1.3);
            var root = new Border { Background = ThemeManager.Brush("WindowBackground"), Child = date };
            root.Measure(new Size(270, double.PositiveInfinity));
            var height = (int)Math.Ceiling(root.DesiredSize.Height);
            Render(root, Path.Combine(output, $"date-{skin}-{style}-{period}-{layout}.png"), 270, height);
            var scroll = (ScrollViewer)date.Child;
            Check(scroll.ScrollableWidth == 0 && scroll.ScrollableHeight < 1, $"{skin}/{style}/{period}/{layout}: narrow scaled date fits without clipping");
            var content = (StackPanel)scroll.Content;
            Check(content.Children.Count == (showHeading ? 2 : 1), "date heading visibility is applied");
            var days = (Panel)content.Children[^1];
            Check(days.Children[1].TranslatePoint(new Point(), days).X > days.Children[0].TranslatePoint(new Point(), days).X, "dates advance horizontally");
            Check(days.Children.Count == DateProgress.Days(DateOnly.FromDateTime(DateTime.Now), period).Length, "all current dates present");
            var firstY = days.Children[0].TranslatePoint(new Point(), days).Y;
            var firstRow = days.Children.Cast<FrameworkElement>().TakeWhile(child => Math.Abs(child.TranslatePoint(new Point(), days).Y - firstY) < 1).ToArray();
            var rowLeft = firstRow[0].TranslatePoint(new Point(), days).X - firstRow[0].Margin.Left;
            var rowRight = firstRow[^1].TranslatePoint(new Point(), days).X + firstRow[^1].ActualWidth + firstRow[^1].Margin.Right;
            if (layout == DateProgressHorizontalLayout.Left) Check(rowLeft < 1 && rowRight < days.ActualWidth - 1, "left date layout keeps free space on the right");
            if (layout == DateProgressHorizontalLayout.Center) Check(rowLeft > 1 && Math.Abs(rowLeft - (days.ActualWidth - rowRight)) < 1, "center date layout balances both sides");
            if (layout == DateProgressHorizontalLayout.Stretch) Check(rowLeft < 1 && rowRight >= days.ActualWidth - 1, "stretch date layout fills the row");
            foreach (FrameworkElement child in days.Children)
            {
                var origin = child.TranslatePoint(new Point(), days);
                Check(origin.X >= 0 && origin.X + child.ActualWidth <= days.ActualWidth + 1, "date cell stays within horizontal bounds");
            }
        }
        foreach (var columns in new[] { false, true })
        foreach (var cardStyle in Enum.GetValues<CardStyle>())
        {
            var window = new MainWindow { ShowActivated = false, Topmost = false };
            // Create only the native handle for monitor sizing; never load personal
            // preferences, start feeds, show the window, or write settings.
            new WindowInteropHelper(window).EnsureHandle();
            void Apply(Preferences p)
            {
                typeof(MainWindow).GetField("_preferences", flags)!.SetValue(window, Preferences.Normalize(p));
                typeof(MainWindow).GetMethod("ApplyLayout", flags)!.Invoke(window, new object[] { true });
                typeof(MainWindow).GetMethod("BuildCards", flags)!.Invoke(window, null);
                typeof(MainWindow).GetMethod("FitUsageHeight", flags)!.Invoke(window, null);
                var root = (FrameworkElement)window.Content;
                Render(root, Path.Combine(output, $"window-{cardStyle}-{columns}-{p.DateProgress.Placement}-{p.DateProgress.Enabled}.png"), (int)Math.Ceiling(window.Width), (int)Math.Ceiling(window.Height));
            }
            var prefs = new Preferences { Style = cardStyle, TwoColumnMode = columns, Pinned = false,
                DateProgress = new() { Enabled = true, Placement = DateProgressPlacement.Bottom, Period = DateProgressPeriod.Month } };
            Apply(prefs);
            var host = (ContentControl)window.FindName("DateProgressHost");
            var cards = (ScrollViewer)window.FindName("CardsScroll");
            var parent = (UIElement)host.Parent;
            Check(Grid.GetRow(host) == 1 && Grid.GetColumnSpan(host) == 2, "bottom date spans all card columns");
            Check(host.TranslatePoint(new Point(), parent).Y >= cards.ActualHeight - 1, "date is below market cards");
            Check(cards.ScrollableHeight < 2, "auto height accommodates cards and date footer");
            var bottomWidth = window.Width;
            Apply(prefs with { DateProgress = prefs.DateProgress with { Placement = DateProgressPlacement.Left } });
            Check(Math.Abs(window.Width - bottomWidth - 116) < 1 && Grid.GetColumn(cards) == 1 && Grid.GetRow(host) == 0, "left placement restores sidebar width");
            Apply(prefs with { DateProgress = prefs.DateProgress with { Enabled = false } });
            Check(host.Visibility == Visibility.Collapsed && Math.Abs(window.Width - bottomWidth) < 1, "disabled date leaves no sidebar space");
            window.Close();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        var settings = new SettingsWindow(Preferences.Normalize(new() { DateProgress = new() { Placement = DateProgressPlacement.Bottom,
            HorizontalLayout = DateProgressHorizontalLayout.Stretch, ShowHeading = false } }), new(), (_, _) => Task.CompletedTask);
        Check(((ComboBox)settings.FindName("DatePlacementChoice")).SelectedIndex == 1, "settings restore bottom placement");
        Check(((ComboBox)settings.FindName("DateHorizontalLayoutChoice")).SelectedIndex == 2, "settings restore stretch date layout");
        Check(((CheckBox)settings.FindName("DateHeadingOption")).IsChecked == false, "settings restore hidden date heading");
        settings.Close();
        Console.WriteLine("Date layout checks passed without network or personal settings changes.");
    }

    private static void Render(FrameworkElement element, string path, int width, int height)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); png.Save(file);
    }
}
