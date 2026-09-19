using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using DeskMonitor.Core;

namespace DeskMonitor;
public sealed record SkinOption(Skin Id, string Name, string Description);
public static class ThemeManager
{
    public static IReadOnlyList<SkinOption> Options { get; } = new[]
    {
        new SkinOption(Skin.Forest, "森林绿", "经典深绿 · 柔和荧光"),
        new SkinOption(Skin.Graphite, "石墨", "Fluent 灵感 · 冷灰分层"),
        new SkinOption(Skin.Paper, "纸白", "简洁浅色 · 清晰细边框"),
        new SkinOption(Skin.Lavender, "柔雾紫", "Material 灵感 · 柔和大圆角"),
        new SkinOption(Skin.Glacier, "冰川", "玻璃风格 · 静态蓝色渐层"),
        new SkinOption(Skin.Midnight, "午夜蓝", "深海蓝黑 · 高对比青色"),
        new SkinOption(Skin.Sunset, "落日", "暖棕暗色 · 珊瑚与金色"),
        new SkinOption(Skin.Matcha, "抹茶", "温润浅绿 · 自然低饱和"),
        new SkinOption(Skin.Obsidian, "黑曜金", "哑光黑曜 · 香槟金细节"),
        new SkinOption(Skin.Porcelain, "暖瓷白", "温润瓷釉 · 黄铜色点缀"),
        new SkinOption(Skin.Titanium, "深海钛", "蓝灰金属 · 冷银渐层")
    };
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static void Apply(Skin skin)
    {
        foreach (System.Collections.DictionaryEntry entry in CreateResources(skin)) Application.Current.Resources[entry.Key] = entry.Value;
    }
    public static ResourceDictionary CreateResources(Skin skin)
    {
        // Opaque surfaces: no blur, backdrop capture, animation, or extra render process.
        var colors = skin switch
        {
            Skin.Forest => new[] { "#141D19", "#1A2720", "#1B252D", "#344338", "#EDF2ED", "#9BAAA0", "#BBE58C", "#BBE58C", "#F19B91", "#DAB87C", "#293B30", "#344B39", "#A9CFE8" },
            Skin.Graphite => new[] { "#15171C", "#20242B", "#232936", "#39414D", "#F5F7FA", "#AEB8C7", "#90B9FF", "#84DBAF", "#FFA7AF", "#EFCB8B", "#303947", "#364865", "#B6CFFF" },
            Skin.Paper => new[] { "#F0F2F5", "#FFFFFF", "#EAF0F7", "#CBD2DC", "#25303B", "#5B6573", "#285BB5", "#147D49", "#C63843", "#8C6309", "#E4EAF2", "#D3E1F5", "#285BB5" },
            Skin.Lavender => new[] { "#F4F0FA", "#FFFCFF", "#EEE5FB", "#D9D0E8", "#292333", "#6D6278", "#7955A3", "#187345", "#B83F53", "#825D16", "#EDE3F6", "#DED0EF", "#7955A3" },
            Skin.Glacier => new[] { "#101D2A", "#21384A", "#293B57", "#45647A", "#EFF8FF", "#A5B8CB", "#8CD6F1", "#8FE4BE", "#FFADAD", "#F1D49B", "#2F4D65", "#3A5779", "#B8D7FF" },
            Skin.Midnight => new[] { "#080E1A", "#101A2B", "#14243A", "#29415E", "#F1F7FF", "#8FA7C2", "#38D6E8", "#5DE1B1", "#FF788A", "#FFD166", "#182C45", "#1E3B5B", "#79E8F3" },
            Skin.Sunset => new[] { "#241411", "#33201C", "#422820", "#654236", "#FFF4EA", "#C9A89A", "#FF9C73", "#7FD6A5", "#FF7A7A", "#FFD07A", "#4A2D26", "#684035", "#FFC09F" },
            Skin.Matcha => new[] { "#E8EBDD", "#F8F9F2", "#EEF1E5", "#BCC5AE", "#263126", "#66705F", "#607D4F", "#327A51", "#B84747", "#8A681D", "#E3E8D7", "#D5DFC8", "#526F43" },
            Skin.Obsidian => new[] { "#131313", "#242321", "#292722", "#494339", "#F2ECE0", "#B6AD9E", "#D7B97C", "#91C8A5", "#FF8E8E", "#E5BA70", "#35312A", "#494033", "#DEC699" },
            Skin.Porcelain => new[] { "#EDE8DF", "#FFFCF5", "#F6EFE2", "#C9BDAC", "#322D26", "#706456", "#866021", "#276D4B", "#B3303E", "#825814", "#E8DECC", "#DFCFB4", "#795A2A" },
            Skin.Titanium => new[] { "#131D27", "#253440", "#293C4D", "#4B6071", "#EBF1F5", "#A8BBC9", "#B4D3E7", "#8ED7B5", "#FFA0A5", "#E8CA8A", "#344A5A", "#415D71", "#BDDCEE" },
            _ => throw new ArgumentOutOfRangeException(nameof(skin))
        };
        string[] keys = { "WindowBackground", "CardBackground", "UsageBackground", "BorderBrush", "TextBrush", "Muted", "Accent", "UpBrush", "DownBrush", "WarningBrush", "HoverBrush", "SelectedBrush", "UsageAccent" };
        var resources = new ResourceDictionary();
        for (var i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze(); resources[keys[i]] = brush;
        }
        if (skin == Skin.Glacier)
        {
            resources["CardBackground"] = Gradient("#21384A", "#172735");
            resources["UsageBackground"] = Gradient("#293B57", "#1C2C42");
        }
        if (skin is Skin.Obsidian or Skin.Porcelain or Skin.Titanium)
        {
            resources["CardBackground"] = skin switch
            {
                Skin.Obsidian => Gradient("#2D2B27", "#1D1D1C"),
                Skin.Porcelain => Gradient("#FFFEFA", "#F2ECE1"),
                _ => Gradient("#304452", "#202D38")
            };
            resources["UsageBackground"] = skin switch
            {
                Skin.Obsidian => Gradient("#322D24", "#23221F"),
                Skin.Porcelain => Gradient("#FCF7EB", "#EDE2CF"),
                _ => Gradient("#364D60", "#243544")
            };
        }
        resources["CardRadius"] = new CornerRadius(skin switch { Skin.Paper => 4, Skin.Lavender => 20, Skin.Graphite => 10, Skin.Matcha => 16, _ => 12 });
        resources["FrameRadius"] = new CornerRadius(skin switch { Skin.Paper => 8, Skin.Lavender => 24, Skin.Matcha => 20, _ => 16 });
        return resources;
    }
    private static Brush Gradient(string from, string to)
    {
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(from), (Color)ColorConverter.ConvertFromString(to), 60);
        brush.Freeze(); return brush;
    }
}
