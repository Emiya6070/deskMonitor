using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskMonitor.Core;

namespace DeskMonitor;
public partial class HttpMessageCard : UserControl
{
    public HttpMessageSource Source { get; }
    public HttpMessageCard(HttpMessageSource source, CardStyle style, double textScale, double? smallCornerRadius)
    {
        InitializeComponent();
        Source = source;
        TitleText.Text = source.Title;
        LayoutTransform = new ScaleTransform(textScale, textScale);
        if (style == CardStyle.Small)
        {
            CardRoot.Padding = new Thickness(10, 7, 10, 7);
            MessageText.Margin = new Thickness(0, 4, 0, 3);
            MessageText.MaxHeight = 42;
            if (smallCornerRadius is { } radius) CardRoot.CornerRadius = new CornerRadius(radius);
        }
        MessageText.Text = "等待获取消息…";
        StatusText.Text = $"每 {FormatInterval(source.RefreshSeconds)}更新";
    }
    public void Update(string? message, string? error, DateTimeOffset? updatedAt, bool loading)
    {
        if (message is not null) MessageText.Text = message;
        MessageText.Opacity = error is null ? 1 : 0.55;
        StatusText.Text = loading ? "正在更新…" : error is not null ? "更新失败 · " + error
            : updatedAt is { } time ? $"{time.LocalDateTime:HH:mm:ss} 更新 · 每 {FormatInterval(Source.RefreshSeconds)}" : "等待更新";
        ToolTip = Source.Url + (string.IsNullOrWhiteSpace(Source.JsonPath) ? "" : "\nJSON 路径：" + Source.JsonPath) + (error is null ? "" : "\n" + error);
    }
    private static string FormatInterval(int seconds) => seconds >= 3600 ? $"{seconds / 3600} 小时" : seconds >= 60 ? $"{seconds / 60} 分钟" : $"{seconds} 秒";
}
