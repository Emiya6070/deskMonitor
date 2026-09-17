using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskMonitor.Core;

namespace DeskMonitor;
public sealed record Preferences
{
    public const int MaxMarkets = 20;
    public const string CodexCardKey = "usage:codex";
    public string[] CardOrder { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Symbol { get; init; }
    public MarketSymbol[]? Markets { get; init; }
    public CardStyle Style { get; init; } = CardStyle.Large;
    public Skin Skin { get; init; } = Skin.Forest;
    public double TextScale { get; init; } = 1;
    public double NumberScale { get; init; } = 1;
    public bool MonospaceNumbers { get; init; }
    public bool ShowTrends { get; init; } = true;
    public int RefreshSeconds { get; init; } = 1;
    public NetworkProxy Proxy { get; init; } = new();
    public bool HideHeader { get; init; }
    public double? SmallCornerRadius { get; init; }
    public bool Pinned { get; init; } = true;
    public bool AllowResize { get; init; }
    public bool SnapToEdges { get; init; } = true;
    public bool ShowCodexUsage { get; init; }
    public double UsageExtraHeight { get; init; }
    public string? CodexExecutable { get; init; }
    public Dictionary<string, int> TrendMinutes { get; init; } = new();
    public double? Left { get; init; }
    public double? Top { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskMonitor", "settings.json");
    public static Preferences Load() => Normalize(File.Exists(FilePath)
        ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("设置文件为空。") : new());
    public static Preferences Normalize(Preferences input)
    {
        var result = input;
        if (result.Proxy is null) throw new InvalidDataException("代理设置为空。");
        result.Proxy.Validate();
        // Migrate the real v0.1 single-symbol settings without losing the user's choice.
        if (result.Markets is null)
        {
            var symbol = result.Symbol ?? "BTCUSDT";
            if (!symbol.EndsWith("USDT", StringComparison.Ordinal) || symbol.Length <= 4) throw new InvalidDataException("旧版币种设置无效。");
            result = result with { Markets = [new(symbol, symbol[..^4], "USDT")] };
        }
        if (result.Markets.Length > MaxMarkets || (result.Markets.Length == 0 && !result.ShowCodexUsage) || !Enum.IsDefined(result.Style)
            || result.CardOrder is null || result.CardOrder.Any(string.IsNullOrWhiteSpace) || result.CardOrder.Distinct().Count() != result.CardOrder.Length
            || !Enum.IsDefined(result.Skin) || !double.IsFinite(result.TextScale) || result.TextScale is < 0.85 or > 1.3
            || !double.IsFinite(result.NumberScale) || result.NumberScale is < 0.9 or > 1.3
            || !double.IsFinite(result.UsageExtraHeight) || result.UsageExtraHeight is < 0 or > 120
            || result.RefreshSeconds is not (1 or 2 or 5)
            || (result.SmallCornerRadius is { } radius && (!double.IsFinite(radius) || radius is < 0 or > 24))
            || result.TrendMinutes is null || result.TrendMinutes.Any(x => !TrendHistory.IsSupportedSpan(x.Value))
            || result.Markets.Any(x => x is null || !Enum.IsDefined(x.Kind) || string.IsNullOrWhiteSpace(x.BaseAsset)
                || (x.TradFi && x.Kind == MarketKind.Spot)
                || x.QuoteAsset != (x.Kind == MarketKind.UsdcPerpetual ? "USDC" : "USDT") || x.Symbol != x.BaseAsset + x.QuoteAsset)
            || result.Markets.Select(x => x.Key).Distinct().Count() != result.Markets.Length
            || new[] { result.Left, result.Top, result.Width, result.Height }.Any(x => x.HasValue && !double.IsFinite(x.Value))
            || result.Width is <= 0 || result.Height is <= 0)
            throw new InvalidDataException("设置文件内容无效。");
        // Migrate the old usage-first layout; remove deleted markets, append new cards.
        // Keep the usage slot even while disabled so toggling it preserves its position.
        var available = new[] { CodexCardKey }.Concat(result.Markets.Select(m => m.Key)).ToArray();
        var order = result.CardOrder.Where(available.Contains).Concat(available.Where(key => !result.CardOrder.Contains(key))).ToArray();
        return result with { Symbol = null, CardOrder = order };
    }
    public void Save()
    {
        var validated = Normalize(this);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(validated));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}
