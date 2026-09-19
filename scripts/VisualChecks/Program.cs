using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskMonitor;
using DeskMonitor.Core;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--size-lock")) SizeLockChecks.InitializeApplication();
        else { var app = new App(); app.InitializeComponent(); }
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        if (args.Contains("--size-lock")) { SizeLockChecks.Run(); return; }
        if (args.Contains("--groups-skins")) { GroupSkinChecks.Run(output); return; }
        if (args.Contains("--date-layout")) { DateLayoutChecks.Run(output); return; }
        var restored = System.Text.Json.JsonSerializer.Deserialize<Preferences>(
            System.Text.Json.JsonSerializer.Serialize(Preferences.Normalize(new() { TwoColumnMode = true })));
        if (restored?.TwoColumnMode != true || new Preferences().TwoColumnMode)
            throw new Exception("Column setting round-trip/default failed.");
        var layout = new CardColumnsPanel { Columns = 2 };
        foreach (var height in new[] { 40d, 80d, 30d }) layout.Children.Add(new Border { Height = height });
        layout.Measure(new Size(610, double.PositiveInfinity));
        if (layout.DesiredSize.Height != 110) throw new Exception("Two-column row measurement failed.");
        layout.Arrange(new Rect(0, 0, 610, 110));
        if (layout.Children[1].TranslatePoint(new Point(), layout).X != 310 || layout.Children[2].TranslatePoint(new Point(), layout).Y != 80)
            throw new Exception("Reading-order arrangement failed.");
        layout.Columns = 1; layout.Measure(new Size(300, double.PositiveInfinity));
        if (layout.DesiredSize.Height != 150) throw new Exception("Single-column restoration failed.");
        foreach (var skin in new[] { Skin.Forest, Skin.Paper })
        {
            ThemeManager.Apply(skin);
            foreach (var style in new[] { CardStyle.Small, CardStyle.Medium, CardStyle.Large })
            {
                var dual = new CardColumnsPanel { Columns = 2 };
                foreach (var symbol in new[] { "BTC", "ETH", "SOL" })
                    dual.Children.Add(new MarketCard(new(symbol + "USDT", symbol, "USDT"), style));
                var preview = new Grid { Background = ThemeManager.Brush("WindowBackground") };
                preview.ColumnDefinitions.Add(new() { Width = new GridLength(116) });
                preview.ColumnDefinitions.Add(new());
                preview.Children.Add(new DateProgressColumn(new() { Enabled = true }, 1));
                Grid.SetColumn(dual, 1); preview.Children.Add(dual);
                Render(preview, Path.Combine(output, skin + "-dual-" + style + ".png"), 916, 600);
            }
            var grid = new Grid { Width = 700, Height = 700, Background = ThemeManager.Brush("WindowBackground") };
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(116) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(116) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new DateProgressColumn(new() { Enabled = true }, 1));
            var month = new DateProgressColumn(new() { Enabled = true, Period = DateProgressPeriod.Month, Style = DateProgressStyle.Grid }, 1);
            Grid.SetColumn(month, 1); grid.Children.Add(month);
            var cards = new StackPanel { Margin = new Thickness(10) }; Grid.SetColumn(cards, 2); grid.Children.Add(cards);
            foreach (var setting in ProviderUsageSettings.Defaults())
            {
                var card = new ProviderUsageCard(setting, new());
                card.Update(setting.Kind == UsageAccountKind.Subscription && setting.Provider != UsageProvider.Claude ? null : new([new("示例数据", setting.Kind == UsageAccountKind.Api ? "12.50 USD" : "剩余 65%", setting.Kind == UsageAccountKind.Api ? null : 65)], DateTimeOffset.UtcNow, "视觉测试示例"), setting.Kind == UsageAccountKind.Subscription && setting.Provider != UsageProvider.Claude ? "暂无内置个人订阅接口；请配置本地额度快照。" : null);
                cards.Children.Add(card);
            }
            Render(grid, Path.Combine(output, skin + "-cards.png"), 700, 700);
            var settings = new SettingsWindow(Preferences.Normalize(new()), new(), (_, _) => Task.CompletedTask);
            var tabs = (TabControl)settings.FindName("SettingsTabs");
            foreach (var i in new[] { 0, 1, 4 })
            {
                tabs.SelectedIndex = i;
                var content = (FrameworkElement)settings.Content;
                ((Grid)content).Background = ThemeManager.Brush("WindowBackground");
                Render(content, Path.Combine(output, skin + "-settings-" + i + ".png"), 540, 720);
                if (i == 0)
                {
                    ((ScrollViewer)((TabItem)tabs.Items[0]).Content).ScrollToVerticalOffset(800);
                    Render(content, Path.Combine(output, skin + "-date-settings.png"), 540, 720);
                }
            }
            settings.Close();
        }
        Console.WriteLine("Rendered WPF cards and settings without reading account credentials.");
    }
    private static void Render(FrameworkElement element, string path, int width, int height)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
