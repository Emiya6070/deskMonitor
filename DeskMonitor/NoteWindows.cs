using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskMonitor.Core;

namespace DeskMonitor;

internal static class NoteUi
{
    public static Button Button(string text, Action click)
    {
        var button = new Button { Content = text, Margin = new Thickness(2) };
        button.Click += (_, _) => click(); return button;
    }
    public static TextBox Field(Panel panel, string label, string value, int height = 30)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
        var box = new TextBox { Text = value, Height = height, Padding = new Thickness(6),
            AcceptsReturn = height > 30, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        box.SetResourceReference(Control.BackgroundProperty, "CardBackground");
        box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        panel.Children.Add(box); return box;
    }
    public static void OpenUrl(Window owner, string url)
    {
        try { NoteProtocol.ValidateUrl(url); Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.InvalidDataException)
        { MessageBox.Show(owner, ex.Message, "无法打开链接"); }
    }
}

public sealed class DesktopNoteWindow : Window
{
    private DesktopNote _note;
    private readonly TextBlock _title = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { FontSize = 15, TextWrapping = TextWrapping.Wrap, LineHeight = 23 };
    private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _original;
    public DesktopNoteWindow(DesktopNote note, bool remote, Action edit, Action hide)
    {
        _note = note;
        Width = 330; Height = 300; MinWidth = 250; MinHeight = 200;
        ShowInTaskbar = false; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        var panel = new DockPanel { Margin = new Thickness(15) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var hideButton = NoteUi.Button("收起", hide); DockPanel.SetDock(hideButton, Dock.Right); header.Children.Add(hideButton);
        var dragArea = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 4, 8, 4),
            Cursor = Cursors.SizeAll, ToolTip = "按住标题区域拖动便签", Child = _title };
        dragArea.MouseLeftButtonDown += (_, e) => { e.Handled = true; DragMove(); };
        header.Children.Add(dragArea);
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom);
        var actions = new WrapPanel();
        _original = NoteUi.Button("打开原文 ↗", () => { if (_note.SourceUrl is { } url) NoteUi.OpenUrl(this, url); });
        actions.Children.Add(_original);
        actions.Children.Add(NoteUi.Button("查看全文", () => new NoteDetailWindow(_note) { Owner = this }.Show()));
        actions.Children.Add(NoteUi.Button("编辑", edit));
        var pin = new CheckBox { Content = "置顶", Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
        pin.Checked += (_, _) => Topmost = true; pin.Unchecked += (_, _) => Topmost = false;
        Loaded += (_, _) => pin.IsChecked = Topmost;
        actions.Children.Add(pin); footer.Children.Add(actions); footer.Children.Add(_status); panel.Children.Add(footer);
        panel.Children.Add(new ScrollViewer { Content = _summary, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 8) });
        Content = panel;
        Update(note, remote ? "服务便签" : "本地便签");
    }
    public void Update(DesktopNote note, string status)
    {
        _note = note; Title = "便签 · " + note.Title; _title.Text = note.Title; _summary.Text = note.Summary;
        _original.IsEnabled = !string.IsNullOrEmpty(note.SourceUrl);
        _original.ToolTip = note.SourceUrl ?? "没有原文链接，可查看保存的全文";
        foreach (Window owned in OwnedWindows)
            if (owned is NoteDetailWindow detail) detail.Update(note);
        SetStatus(status);
    }
    public void SetStatus(string status) => _status.Text = $"{status} · {_note.UpdatedAt.ToLocalTime():MM-dd HH:mm}";
}

internal sealed class NoteDetailWindow : Window
{
    private readonly TextBox _content;
    public NoteDetailWindow(DesktopNote note)
    {
        Title = note.Title; Width = 680; Height = 580; MinWidth = 350; MinHeight = 250;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        _content = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20), FontSize = 15,
            Background = Brushes.Transparent, Foreground = (Brush)Application.Current.FindResource("TextBrush") };
        Content = _content;
        Update(note);
    }
    public void Update(DesktopNote note)
    {
        Title = note.Title;
        if (_content.Text != note.Content) _content.Text = note.Content;
    }
}

internal sealed class NoteEditorWindow : Window
{
    private bool _saving;
    public NoteEditorWindow(DesktopNote? note, bool remote, Func<NoteInput, Task> save)
    {
        Title = (note is null ? "新建" : "编辑") + (remote ? "服务便签" : "本地便签");
        Width = 570; Height = 650; MinWidth = 400; MinHeight = 400;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        var panel = new StackPanel { Margin = new Thickness(20) };
        var title = NoteUi.Field(panel, "标题", note?.Title ?? ""); title.MaxLength = 120;
        var summary = NoteUi.Field(panel, "桌面重点（独立字段，只在便签上显示这部分）", note?.Summary ?? "", 100); summary.MaxLength = 2000;
        var content = NoteUi.Field(panel, "完整内容", note?.Content ?? "", 220); content.MaxLength = 100000;
        var url = NoteUi.Field(panel, "原文 / ChatGPT 对话链接（可选）", note?.SourceUrl ?? "");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var button = new Button { Content = "保存便签", Margin = new Thickness(0, 12, 0, 8) };
        button.Click += async (_, _) =>
        {
            if (_saving) return;
            _saving = true; button.IsEnabled = false;
            try
            {
                var input = new NoteInput(title.Text, summary.Text, content.Text, url.Text.Trim(), note?.Revision ?? 0);
                input.Validate(); await save(input); _saving = false; Close();
            }
            catch (Exception ex) when (NotesController.IsExpectedError(ex)) { status.Text = "未保存：" + ex.Message; }
            finally { _saving = false; button.IsEnabled = true; }
        };
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
        panel.Children.Add(button); panel.Children.Add(status);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
