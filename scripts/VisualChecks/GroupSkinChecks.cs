using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskMonitor;
using DeskMonitor.Core;

internal static class GroupSkinChecks
{
    public static void Run(string output)
    {
        var prefs = Preferences.Normalize(new() { CardPlacements = new() { ["usage:codex"] = new(2, " 工具 "), ["deleted"] = new(1, "删除") } });
        var restored = Preferences.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(prefs))!);
        Assert(restored.CardPlacements.Count == 1 && restored.CardPlacements["usage:codex"] == new CardPlacement(2, "工具"), "placement persistence/trim/deletion");
        try { Preferences.Normalize(new() { CardPlacements = new() { ["usage:codex"] = new(3) } }); throw new Exception("Invalid column accepted"); }
        catch (InvalidDataException) { }
        foreach (var skin in new[] { Skin.Obsidian, Skin.Porcelain, Skin.Titanium })
        {
            ThemeManager.Apply(skin);
            var codex = new UsageCard(CardStyle.Medium);
            var now = DateTimeOffset.UtcNow;
            codex.Update(new(new(80, 300, now.AddHours(1)), null, now), null, false);
            Assert(((TextBlock)codex.FindName("PrimaryValue")).Foreground == ThemeManager.Brush("DownBrush"), "Codex 20 percent boundary");
            Assert(((TextBlock)codex.FindName("PrimaryReset")).FontWeight == FontWeights.Bold, "Codex reset bold");
            codex.Update(new(new(79, 300, now.AddHours(1)), null, now), null, false);
            Assert(((TextBlock)codex.FindName("PrimaryValue")).Foreground == ThemeManager.Brush("TextBrush"), "Codex recovery clears warning");
            codex.Update(new(new(90, 300, now.AddMinutes(-1)), null, now), null, false);
            Assert(((TextBlock)codex.FindName("PrimaryValue")).Foreground == ThemeManager.Brush("TextBrush"), "awaiting reset is not low quota");
            var panel = new CardColumnsPanel { Columns = 2, Margin = new Thickness(12) };
            var placement = new Dictionary<string, CardPlacement>();
            foreach (var symbol in new[] { "BTC", "ETH", "SOL" })
            {
                panel.Children.Add(new MarketCard(new(symbol + "USDT", symbol, "USDT"), CardStyle.Medium) { Tag = symbol });
                placement[symbol] = new(1, "数字资产");
            }
            var setting = new ProviderUsageSettings { Provider = UsageProvider.Claude, Kind = UsageAccountKind.Subscription };
            var usage = new ProviderUsageCard(setting, new()) { Tag = "claude" };
            usage.Update(new([new("短周期", "剩余 15%", 15, DateTimeOffset.UtcNow.AddHours(2)), new("本周", "剩余 65%", 65, DateTimeOffset.UtcNow.AddDays(4))], DateTimeOffset.UtcNow, "视觉测试 · 示例数据"), null);
            panel.Children.Add(usage); placement["claude"] = new(2, "AI 额度");
            var rows = ((StackPanel)((Border)usage.Content).Child).Children.OfType<StackPanel>().Single();
            Assert(((TextBlock)rows.Children[0]).Foreground == ThemeManager.Brush("DownBrush"), "low quota red");
            Assert(((TextBlock)rows.Children[2]).FontWeight == FontWeights.Bold, "reset bold");
            Assert(((TextBlock)rows.Children[3]).Foreground == ThemeManager.Brush("TextBrush"), "healthy quota normal");
            CardGrouping.Apply(panel, placement, 1);
            panel.Measure(new Size(800, double.PositiveInfinity)); panel.Arrange(new Rect(0, 0, 800, panel.DesiredSize.Height));
            Assert(usage.TranslatePoint(new Point(), panel).X > 0, "usage in right column");
            Assert(panel.Children.OfType<TextBlock>().Count() == 2, "one heading per group");
            var grid = new Grid { Background = ThemeManager.Brush("WindowBackground") }; grid.Children.Add(panel);
            grid.Measure(new Size(800, 570)); grid.Arrange(new Rect(0, 0, 800, 570)); grid.UpdateLayout();
            var bitmap = new RenderTargetBitmap(800, 570, 96, 96, PixelFormats.Pbgra32); bitmap.Render(grid);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, skin + "-groups.png")); encoder.Save(stream);
            panel.Columns = 1; panel.Measure(new Size(400, double.PositiveInfinity)); panel.Arrange(new Rect(0, 0, 400, panel.DesiredSize.Height));
            Assert(usage.TranslatePoint(new Point(), panel).X == 0, "single column fallback");
        }
        Console.WriteLine("Group, placement persistence, quota styling and three skin checks passed.");
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
}
