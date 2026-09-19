using System;
using System.Windows;
using System.Windows.Controls;
using DeskMonitor.Core;

namespace DeskMonitor;
public sealed class ProviderUsageEditor : StackPanel
{
    private readonly ProviderUsageSettings _original;
    private readonly CheckBox _enabled;
    private readonly TextBox _environment, _team, _path;
    public event EventHandler? Changed;
    public bool IsEnabledCard => _enabled.IsChecked == true;
    public ProviderUsageEditor(ProviderUsageSettings settings)
    {
        _original = settings; Margin = new Thickness(0, 0, 0, 18);
        _enabled = new CheckBox { Content = settings.Title, IsChecked = settings.Enabled, FontWeight = FontWeights.SemiBold };
        _enabled.Checked += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _enabled.Unchecked += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        Children.Add(_enabled);
        _environment = Input("凭据环境变量名（不是密钥本身）", settings.CredentialEnvironment, settings.Kind == UsageAccountKind.Api);
        _team = Input("xAI Team ID", settings.TeamId, settings.Kind == UsageAccountKind.Api && settings.Provider == UsageProvider.Grok);
        var snapshotPath = settings.SnapshotPath;
        if (snapshotPath.Length == 0 && settings.Provider == UsageProvider.Claude && settings.Kind == UsageAccountKind.Subscription)
            snapshotPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskMonitor", "claude-subscription.json");
        _path = Input("订阅额度快照 JSON 的绝对路径", snapshotPath, settings.Kind == UsageAccountKind.Subscription);
        var help = settings.Kind == UsageAccountKind.Subscription
            ? settings.Provider == UsageProvider.Claude ? "使用 scripts/export-claude-usage.ps1 接收 Claude Code statusLine 额度；需支持 rate_limits 的版本。" : "暂不支持直接读取个人订阅；可由本地工具写入标准快照。未提供快照时显示未配置。"
            : settings.Provider switch { UsageProvider.Cursor => "需要团队 Admin Key；汇总团队当前账期支出，含套餐用量价值，不代表个人 API 剩余额度。", UsageProvider.Claude => "需要组织 Admin Key；显示本月 API 费用，不包含 Pro/Max 订阅或 Priority Tier。", _ => "需要 Management Key 和 Team ID；显示本月 API 费用，不包含 SuperGrok 订阅。" };
        var text = new TextBlock { Text = help, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); Children.Add(text);
    }
    private TextBox Input(string label, string value, bool visible)
    {
        var text = new TextBox { Text = value, Padding = new Thickness(6), Margin = new Thickness(0, 4, 0, 0) };
        text.SetResourceReference(Control.BackgroundProperty, "CardBackground");
        text.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        if (visible) { Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 0) }); Children.Add(text); }
        return text;
    }
    public ProviderUsageSettings Value => _original with { Enabled = IsEnabledCard, CredentialEnvironment = _environment.Text.Trim(), TeamId = _team.Text.Trim(), SnapshotPath = _path.Text.Trim() };
}
