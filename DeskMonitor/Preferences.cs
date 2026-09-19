using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskMonitor.Core;

namespace DeskMonitor;
public sealed record CardPlacement(int Column = 0, string Group = "");
public sealed record Preferences
{
    public const int MaxMarkets = 20;
    public const string CodexCardKey = "usage:codex";
    public string[] CardOrder { get; init; } = [];
    public Dictionary<string, CardPlacement> CardPlacements { get; init; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Symbol { get; init; }
    public MarketSymbol[]? Markets { get; init; }
    public CardStyle Style { get; init; } = CardStyle.Large;
    public Skin Skin { get; init; } = Skin.Forest;
    public double TextScale { get; init; } = 1;
    public double NumberScale { get; init; } = 1;
    public double MarketHeightAdjustment { get; init; }
    public bool MonospaceNumbers { get; init; }
    public bool ShowTrends { get; init; } = true;
    public bool TwoColumnMode { get; init; }
    public int RefreshSeconds { get; init; } = 1;
    public NetworkProxy Proxy { get; init; } = new();
    public bool HideHeader { get; init; }
    public double? SmallCornerRadius { get; init; }
    public bool Pinned { get; init; } = true;
    public bool AllowResize { get; init; }
    public bool LockCustomSize { get; init; }
    public bool SnapToEdges { get; init; } = true;
    public bool ShowCodexUsage { get; init; }
    public ProviderUsageSettings[] ProviderUsages { get; init; } = [];
    public DateProgressSettings DateProgress { get; init; } = new();
    public double UsageExtraHeight { get; init; }
    public CodexTokenRangeKind CodexTokenRange { get; init; } = CodexTokenRangeKind.ShortReset;
    public string? CodexExecutable { get; init; }
    public Dictionary<string, int> TrendMinutes { get; init; } = new();
    public HttpMessageSource[] HttpSources { get; init; } = [];
    public UsStockProvider UsStockProvider { get; init; } = UsStockProvider.Alpaca;
    public AlpacaFeed AlpacaFeed { get; init; } = AlpacaFeed.Iex;
    public string AlpacaKeyId { get; init; } = "";
    public string AlpacaSecretKey { get; init; } = "";
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
        if (result.LockCustomSize && (result.Width is null || result.Height is null))
            result = result with { LockCustomSize = false };
        if (result.CardPlacements is null || result.CardPlacements.Any(x => x.Value is null
            || x.Value.Column is < 0 or > 2 || x.Value.Group is null || x.Value.Group.Length > 30
            || x.Value.Group.Any(char.IsControl))) throw new InvalidDataException("卡片分列或组名无效（组名最多 30 字）。");
        if (result.Proxy is null) throw new InvalidDataException("代理设置为空。");
        result.Proxy.Validate();
        if (result.DateProgress is null) throw new InvalidDataException("日期进度设置为空。");
        result.DateProgress.Validate();
        if (result.ProviderUsages is null || result.ProviderUsages.Any(x => x is null) || result.ProviderUsages.Length > 6
            || result.ProviderUsages.Select(x => x.Key).Distinct().Count() != result.ProviderUsages.Length) throw new InvalidDataException("用量卡片配置无效。");
        foreach (var usage in result.ProviderUsages) usage.Validate();
        // Migrate the real v0.1 single-symbol settings without losing the user's choice.
        if (result.Markets is null)
        {
            var symbol = result.Symbol ?? "BTCUSDT";
            if (!symbol.EndsWith("USDT", StringComparison.Ordinal) || symbol.Length <= 4) throw new InvalidDataException("旧版币种设置无效。");
            result = result with { Markets = [new(symbol, symbol[..^4], "USDT")] };
        }
        if (result.Markets.Length > MaxMarkets || (result.Markets.Length == 0 && !result.ShowCodexUsage && !result.ProviderUsages.Any(x => x.Enabled) && result.HttpSources is { Length: 0 }) || !Enum.IsDefined(result.Style)
            || result.CardOrder is null || result.CardOrder.Any(string.IsNullOrWhiteSpace) || result.CardOrder.Distinct().Count() != result.CardOrder.Length
            || !Enum.IsDefined(result.Skin) || !double.IsFinite(result.TextScale) || result.TextScale is < 0.85 or > 1.3
            || !double.IsFinite(result.NumberScale) || result.NumberScale is < 0.9 or > 1.3
            || !double.IsFinite(result.MarketHeightAdjustment) || result.MarketHeightAdjustment is < -12 or > 48
            || !double.IsFinite(result.UsageExtraHeight) || result.UsageExtraHeight is < 0 or > 120
            || !Enum.IsDefined(result.CodexTokenRange)
            || result.RefreshSeconds is not (1 or 2 or 5)
            || (result.SmallCornerRadius is { } radius && (!double.IsFinite(radius) || radius is < 0 or > 24))
            || result.TrendMinutes is null || result.TrendMinutes.Any(x => !TrendHistory.IsSupportedSpan(x.Value))
            || result.HttpSources is null || result.HttpSources.Length > 10 || result.HttpSources.Any(x => x is null)
            || result.HttpSources.Select(x => x.Id).Distinct().Count() != result.HttpSources.Length
            || !Enum.IsDefined(result.UsStockProvider) || !Enum.IsDefined(result.AlpacaFeed)
            || result.AlpacaKeyId.Length > 200 || result.AlpacaSecretKey.Length > 200
            || result.Markets.Any(x => x is null || !Enum.IsDefined(x.Kind) || string.IsNullOrWhiteSpace(x.BaseAsset)
                || (x.TradFi && x.Kind == MarketKind.Spot)
                || x.QuoteAsset != (x.Kind == MarketKind.UsStock ? "USD" : x.Kind == MarketKind.UsdcPerpetual ? "USDC" : "USDT")
                || x.Symbol != (x.Kind == MarketKind.UsStock ? x.BaseAsset : x.BaseAsset + x.QuoteAsset))
            || result.Markets.Select(x => x.Key).Distinct().Count() != result.Markets.Length
            || new[] { result.Left, result.Top, result.Width, result.Height }.Any(x => x.HasValue && !double.IsFinite(x.Value))
            || result.Width is <= 0 || result.Height is <= 0)
            throw new InvalidDataException("设置文件内容无效。");
        foreach (var source in result.HttpSources) source.Validate();
        // Migrate the old usage-first layout; remove deleted markets, append new cards.
        // Keep the usage slot even while disabled so toggling it preserves its position.
        var available = new[] { CodexCardKey }.Concat(result.Markets.Select(m => m.Key)).Concat(result.HttpSources.Select(s => s.Key)).Concat(result.ProviderUsages.Select(u => u.Key)).ToArray();
        var order = result.CardOrder.Where(available.Contains).Concat(available.Where(key => !result.CardOrder.Contains(key))).ToArray();
        return result with { Symbol = null, CardOrder = order,
            CardPlacements = result.CardPlacements.Where(x => available.Contains(x.Key))
                .ToDictionary(x => x.Key, x => x.Value with { Group = x.Value.Group.Trim() }) };
    }
    public void Save()
    {
        var validated = Normalize(this);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(validated));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}
