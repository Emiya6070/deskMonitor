using System.Globalization;
using System.Text.Json;

namespace DeskMonitor.Core;

public sealed record HttpMessageSource
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; init; } = "自定义消息";
    public string Url { get; init; } = "";
    public string JsonPath { get; init; } = "";
    public int RefreshSeconds { get; init; } = 60;
    public string Key => "http:" + Id;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_') || Id.Length > 64
            || string.IsNullOrWhiteSpace(Title) || Title.Length > 80 || Title.Any(char.IsControl)
            || !Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || JsonPath.Length > 200 || JsonPath.Any(char.IsControl)
            || RefreshSeconds is < 5 or > 3600)
            throw new InvalidDataException("自定义消息源设置无效。");
    }
}

public static class HttpMessageParser
{
    public static string Parse(string body, string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath)) return Clean(body);
        using var document = JsonDocument.Parse(body);
        var value = document.RootElement;
        foreach (var segment in jsonPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(segment, out var property)) value = property;
            else if (value.ValueKind == JsonValueKind.Array && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < value.GetArrayLength()) value = value[index];
            else throw new InvalidDataException($"JSON 路径不存在：{jsonPath}");
        }
        return Clean(value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText());
    }
    private static string Clean(string value)
    {
        value = value.Trim();
        if (value.Length == 0) throw new InvalidDataException("消息内容为空。");
        return value.Length <= 4000 ? value : value[..4000] + "…";
    }
}

public static class UsStockParser
{
    public static Ticker Parse(string json, string expectedSymbol, DateTimeOffset fetchedAt)
    {
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement.GetProperty("chart").GetProperty("result");
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0) throw new InvalidDataException("美股行情为空。");
        var meta = result[0].GetProperty("meta");
        if (!string.Equals(meta.GetProperty("symbol").GetString(), expectedSymbol, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("美股代码不匹配。");
        decimal Number(string name)
        {
            if (!meta.TryGetProperty(name, out var node) || !node.TryGetDecimal(out var number) || number <= 0) throw new InvalidDataException($"美股行情缺少 {name}。");
            return number;
        }
        var last = Number("regularMarketPrice");
        var previous = Number("chartPreviousClose");
        var high = meta.TryGetProperty("regularMarketDayHigh", out var highNode) && highNode.TryGetDecimal(out var highValue) ? highValue : Math.Max(last, previous);
        var low = meta.TryGetProperty("regularMarketDayLow", out var lowNode) && lowNode.TryGetDecimal(out var lowValue) ? lowValue : Math.Min(last, previous);
        if (high < last) high = last;
        if (low > last) low = last;
        // The chart records this app's HTTP observations, so use fetch time on the
        // x-axis; provider exchange timestamps can stop advancing outside trading.
        return new(expectedSymbol.ToUpperInvariant(), last, previous, high, low, fetchedAt, fetchedAt, MarketKind.UsStock);
    }
}
