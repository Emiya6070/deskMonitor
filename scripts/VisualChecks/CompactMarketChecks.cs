using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskMonitor;
using DeskMonitor.Core;

internal static class CompactMarketChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private static object? Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static readonly MarketSymbol[] Markets =
    [
        new("BTCUSDC", "BTC", "USDC", MarketKind.UsdcPerpetual),
        new("BNBUSDT", "BNB", "USDT", MarketKind.UsdtPerpetual),
        new("ETHUSDT", "ETH", "USDT"),
        new("CLUSDT", "CL", "USDT", MarketKind.UsdtPerpetual, true),
        new("GOOGLUSDT", "GOOGL", "USDT", MarketKind.UsdtPerpetual, true)
    ];
    public static void Run(string output)
    {
        foreach (var skin in new[] { Skin.Graphite, Skin.Paper })
        {
            ThemeManager.Apply(skin);
            VerifyChangeColors();
            var preview = new StackPanel { Background = ThemeManager.Brush("WindowBackground") };
            preview.Children.Add(new TextBlock { Text = "紧凑模式 · 界面验证示例数据", Margin = new Thickness(12), Foreground = ThemeManager.Brush("Muted") });
            foreach (var (market, i) in Markets.Select((m, i) => (m, i)))
            {
                var card = new MarketCard(market, CardStyle.Large, compactMode: true, changeMinutes: 1440);
                var now = DateTimeOffset.UtcNow;
                var price = new[] { 73754.5m, 680.61m, 2063.3m, 85.97m, 290.98m }[i];
                var ticker = new Ticker(market.Symbol, price, price * (i == 3 ? 0.99m : 1.01m), price * 1.1m, price * 0.9m, now, now, market.Kind);
                card.Update(ticker with { Last = price * (i % 2 == 0 ? 0.999m : 1.001m), ObservedAt = now.AddSeconds(-1) }, new(FeedPhase.Live, "视觉验证示例"), null);
                card.Update(ticker, new(FeedPhase.Live, "视觉验证示例"), null);
                Check(!card.NeedsTrendRefresh && ((UIElement)card.FindName("FullPanel")).Visibility == Visibility.Collapsed
                    && ((UIElement)card.FindName("SmallChart")).Visibility == Visibility.Collapsed
                    && ((UIElement)card.FindName("CompactFunding")).Visibility == Visibility.Collapsed, "compact rows contain no trend, funding or detail panels");
                preview.Children.Add(card);
            }
            var setting = ProviderUsageSettings.Defaults().Single(x => x.Provider == UsageProvider.OpenAI);
            var usage = new ProviderUsageCard(setting, new() { Style = CardStyle.Large, CompactMarkets = true }) { Margin = new Thickness(0, 14, 0, 0) };
            usage.Update(new([new("本月累计费用", "暂无费用记录")], DateTimeOffset.UtcNow, "OpenAI 组织 API · UTC · 非剩余余额 · 示例数据 · 费用接口尚无明细，不代表没有使用；请核对官网账单"), null);
            preview.Children.Add(usage);
            Render(preview, Path.Combine(output, $"compact-{skin}.png"), 400, 385);
            usage.Update(new([new("本月累计费用", "12.5956 USD")], DateTimeOffset.UtcNow.AddMinutes(-20), "示例数据"), "读取失败");
            var rows = (StackPanel)typeof(ProviderUsageCard).GetField("_rows", Private)!.GetValue(usage)!;
            Check(((TextBlock)rows.Children[0]).Opacity == 0.5 && ((TextBlock)rows.Children[0]).Text.Contains("12.5956"), "OpenAI failure preserves and dims old spending");
            var panel = (StackPanel)((Border)usage.Content).Child;
            Check(panel.Children.OfType<Button>().Single().Content.ToString() == "查看余额与账单 ↗", "OpenAI billing button remains available after a read failure");

            var narrow = new MarketCard(Markets[0], CardStyle.Small, textScale: 1.3, numberScale: 1.3, monospace: true, compactMode: true, changeMinutes: 1440);
            var time = DateTimeOffset.UtcNow;
            narrow.Update(new(Markets[0].Symbol, 1234567.89m, 1200000, 1300000, 1000000, time, time, Markets[0].Kind), new(FeedPhase.Live, "示例"), null);
            Render(narrow, Path.Combine(output, $"compact-narrow-{skin}.png"), 286, 55);
            var priceText = (TextBlock)narrow.FindName("CompactPrice");
            var bounds = priceText.TransformToAncestor(narrow).TransformBounds(new Rect(priceText.RenderSize));
            Check(bounds.Left >= 0 && bounds.Right <= narrow.ActualWidth + 1 && bounds.Bottom <= narrow.ActualHeight + 1, "compact prices fit narrow windows at maximum text and number scales");
            var priceScale = priceText.TransformToAncestor(narrow).TransformBounds(new Rect(0, 0, 1, 1)).Height;
            Check(priceText.FontSize * priceScale >= 10, "the change column cannot squeeze a long price into unreadable text in a locked narrow window");
            var changeText = (TextBlock)narrow.FindName("CompactChange");
            var changeBounds = changeText.TransformToAncestor(narrow).TransformBounds(new Rect(changeText.RenderSize));
            Check(changeBounds.Left >= bounds.Right && changeBounds.Right <= narrow.ActualWidth + 1, "compact interval changes fit without overlapping narrow prices");
            narrow.Update(new(Markets[0].Symbol, 1234567.89m, 1200000, 1300000, 1000000, time.AddMinutes(-1), time.AddMinutes(-1), Markets[0].Kind), new(FeedPhase.Live, "示例"), null);
            Check(priceText.Opacity == 0.45 && narrow.ToolTip.ToString()!.Contains("行情已过期"), "compact stale quotes retain visible health and hover details");
        }
        VerifySettingsAndSizing(output);
        Console.WriteLine("Compact market and OpenAI UI checks passed; no account requests or personal settings writes.");
    }
    private static void VerifyChangeColors()
    {
        var now = DateTimeOffset.UtcNow;
        var market = Markets[1];
        var live = new FeedStatus(FeedPhase.Live, "示例数据");
        var initial = new Ticker(market.Symbol, 110, 100, 130, 90, now.AddSeconds(-2), now, market.Kind);
        bool HasColor(TextBlock text, string resource) => text.Foreground.ToString() == ThemeManager.Brush(resource).ToString();
        foreach (var style in Enum.GetValues<CardStyle>())
        {
            var card = new MarketCard(market, style, compactMode: true, changeMinutes: 1440);
            var price = (TextBlock)card.FindName("CompactPrice");
            var change = (TextBlock)card.FindName("CompactChange");
            card.Update(initial, live, null);
            Check(HasColor(price, "TextBrush") && HasColor(change, "UpBrush"), $"{style}: first price is neutral while positive 24h change is green");
            card.Update(initial with { Last = 105, ObservedAt = now.AddSeconds(-1) }, live, null);
            Check(HasColor(price, "DownBrush") && HasColor(change, "UpBrush"), $"{style}: falling price is red independently of a positive daily change");
            card.Update(initial with { Last = 106, Open24h = 120, ObservedAt = now }, live, null);
            Check(HasColor(price, "UpBrush") && HasColor(change, "DownBrush"), $"{style}: rising price is green independently of a negative daily change");
            card.Update(initial with { Last = 106, Open24h = 106, ObservedAt = now }, live, null);
            Check(HasColor(price, "TextBrush") && HasColor(change, "TextBrush"), $"{style}: unchanged price and zero change use neutral colors");
            var original = new MarketCard(market, style);
            original.Update(initial, live, null);
            original.Update(initial with { Last = 105, ObservedAt = now }, live, null);
            Check(HasColor((TextBlock)original.FindName("PriceText"), "DownBrush") && HasColor((TextBlock)original.FindName("CompactPrice"), "DownBrush"),
                $"{style}: original card price also follows the last displayed price");
        }
        foreach (var minutes in new[] { 5, 15, 60, 240 })
        {
            var card = new MarketCard(market, CardStyle.Small, compactMode: true, changeMinutes: minutes);
            var change = (TextBlock)card.FindName("CompactChange");
            var ticker = initial with { ObservedAt = now };
            card.Update(ticker, live, null, changeError: "历史价格读取失败");
            Check(change.Text.EndsWith("—") && change.ToolTip.ToString()!.Contains("读取失败"), $"{minutes}m missing history stays unavailable with an explanation");
            var reference = new MarketChangeReference(market.Key, minutes, 100m, MarketChangeReference.ReferenceTime(now, minutes), now);
            card.Update(ticker, live, null, changeReference: reference);
            Check(change.Text == MarketChangeReference.PeriodLabel(minutes) + " ≈+10.00%" && HasColor(change, "UpBrush"), $"{minutes}m uses the chosen reference and marks the result approximate");
            card.Update(ticker, live, null, changeReference: reference with { ObservedAt = reference.ObservedAt.AddMinutes(-1) });
            Check(change.Text.EndsWith("—"), $"{minutes}m expired baseline is not shown as current change");
        }
        var hidden = new MarketCard(market, CardStyle.Small, compactMode: true);
        Check(((UIElement)hidden.FindName("CompactChangeBox")).Visibility == Visibility.Collapsed, "hidden change option keeps the two-column list");
    }
    private static void VerifySettingsAndSizing(string output)
    {
        foreach (var style in Enum.GetValues<CardStyle>())
        {
            var preferences = Preferences.Normalize(new() { Markets = Markets, Style = style, Pinned = false, SnapToEdges = false });
            Preferences? saved = null;
            Action<Preferences> persist = value => saved = Preferences.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(value))!);
            var window = (MainWindow)Activator.CreateInstance(typeof(MainWindow), Private, null, new object[] { persist }, null)!;
            window.Opacity = 0; window.ShowActivated = false;
            window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, typeof(MainWindow).GetMethod("WindowLoaded", Private)!);
            typeof(MainWindow).GetField("_preferences", Private)!.SetValue(window, preferences);
            new WindowInteropHelper(window).EnsureHandle();
            Call(window, "ApplyLayout", false); Call(window, "BuildCards");
            window.Show(); Pump();
            try
            {
                var fullHeight = window.Height;
                SaveCompact(true);
                Check(saved!.CompactMarkets && window.Height < fullHeight, $"{style}: saving compact mode persists and shrinks the actual window");
                var panel = (CardColumnsPanel)window.FindName("CardsPanel");
                Check(panel.Children.OfType<MarketCard>().All(c => c.Height == 36 && c.Margin.Bottom == 0), $"{style}: all markets become continuous single rows");
                var twoColumnWidth = window.Width;
                SaveCompact(true, 1440);
                Check(saved!.CompactChangeMinutes == 1440 && window.Width > twoColumnWidth,
                    $"{style}: enabling 24h change saves the interval and expands the automatic width");
                SaveCompact(true, 5);
                Check(saved!.CompactChangeMinutes == 5 && panel.Children.OfType<MarketCard>().All(c => ((UIElement)c.FindName("CompactChangeBox")).Visibility == Visibility.Visible),
                    $"{style}: selecting a short interval rebuilds visible change columns");
                SaveCompact(false);
                Check(!saved!.CompactMarkets && window.Height > 5 * 36, $"{style}: disabling compact mode restores original cards");
                var locked = saved with { LockCustomSize = true, Width = window.Width, Height = window.Height };
                var lockedHeight = window.Height;
                var lockedWidth = window.Width;
                typeof(MainWindow).GetField("_preferences", Private)!.SetValue(window, locked);
                saved = locked;
                SaveCompact(true, 1440);
                Check(saved!.LockCustomSize && Math.Abs(window.Height - lockedHeight) < 1 && Math.Abs(window.Width - lockedWidth) < 1, $"{style}: compact change toggle respects a locked window size");

                void SaveCompact(bool enabled, int period = 0)
                {
                    var settings = new SettingsWindow(saved ?? preferences, new(), async (candidate, startup) =>
                        await (Task)Call(window, "ApplySettingsAsync", candidate, startup)!) { Owner = window, Opacity = 0, ShowActivated = false };
                    settings.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), settings, typeof(SettingsWindow).GetMethod("WindowLoaded", Private)!);
                    typeof(SettingsWindow).GetField("_ready", Private)!.SetValue(settings, true);
                    settings.Dispatcher.BeginInvoke(() =>
                    {
                        ((CheckBox)settings.FindName("CompactMarketsOption")).IsChecked = enabled;
                        ((ComboBox)settings.FindName("CompactChangeChoice")).SelectedValue = period;
                        Call(settings, "SaveClicked", settings, new RoutedEventArgs());
                    }, DispatcherPriority.ApplicationIdle);
                    settings.ShowDialog(); Pump();
                    Check(saved is not null, "settings save completed");
                }
            }
            finally { window.Close(); Pump(); }
        }
        ThemeManager.Apply(Skin.Graphite);
        var form = new SettingsWindow(Preferences.Normalize(new() { CompactMarkets = true, CompactChangeMinutes = 1440 }), new(), (_, _) => Task.CompletedTask);
        var tabs = (TabControl)form.FindName("SettingsTabs");
        var root = (FrameworkElement)form.Content;
        ((Grid)root).Background = ThemeManager.Brush("WindowBackground");
        tabs.SelectedIndex = 1;
        Render(root, Path.Combine(output, "compact-settings.png"), 550, 750);
        tabs.SelectedIndex = 4;
        Render(root, Path.Combine(output, "openai-settings.png"), 550, 750);
        ((ScrollViewer)((TabItem)tabs.Items[4]).Content).ScrollToEnd();
        Render(root, Path.Combine(output, "openai-settings.png"), 550, 750);
        form.Close();
    }
    private static void Render(FrameworkElement element, string path, int width, int height)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
