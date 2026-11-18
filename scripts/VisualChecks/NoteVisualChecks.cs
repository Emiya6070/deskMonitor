using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskMonitor;
using DeskMonitor.Core;

internal static class NoteVisualChecks
{
    public static void Run(string output)
    {
        var note = new DesktopNote("visual", "DeskMonitor · 分析与下一步", "结论：桌面只保留重点，完整分析随时查看。\n\n下一步\n• 连接 ChatGPT 服务\n• 继续同一个分析主题\n\n待确认：真实对话链接。", "FULL_CONTENT_MUST_NOT_APPEAR_ON_DESKTOP", "https://chatgpt.com/c/example", 1, DateTimeOffset.UtcNow);
        foreach (var width in new[] { 250, 330, 480 })
        {
            var window = new DesktopNoteWindow(note, true, () => { }, () => { });
            var element = (FrameworkElement)window.Content;
            window.Content = null;
            var root = new Border { Background = (Brush)Application.Current.FindResource("WindowBackground"), Child = element };
            root.Measure(new Size(width, 300)); root.Arrange(new Rect(0, 0, width, 300)); root.UpdateLayout();
            var text = Descendants(root).OfType<TextBlock>().Select(t => t.Text).ToArray();
            if (!text.Contains(note.Summary) || text.Any(t => t.Contains(note.Content))) throw new Exception("Desktop must show only independent summary, never full content.");
            var bitmap = new RenderTargetBitmap(width, 300, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, $"note-{width}.png")); encoder.Save(stream);
            window.Update(note with { SourceUrl = null, Summary = "继续分析后的重点" }, "同步失败 · 内容可能已过期");
            var original = Descendants(root).OfType<Button>().Single(b => Equals(b.Content, "打开原文 ↗"));
            if (original.IsEnabled) throw new Exception("Missing source URL must disable navigation.");
            if (!Descendants(root).OfType<TextBlock>().Any(t => t.Text == "继续分析后的重点")) throw new Exception("New service version did not refresh summary.");
            Console.WriteLine($"PASS notes: {width}px render, independent summary, update and missing-link behavior");
            window.Close();
        }
        var live = new DesktopNoteWindow(note, true, () => { }, () => { });
        live.Show();
        var openDetail = Descendants(live).OfType<Button>().Single(b => Equals(b.Content, "查看全文"));
        openDetail.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        openDetail.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var details = live.OwnedWindows.Cast<Window>().ToArray();
        if (details.Length != 2) throw new Exception("Expected two open full-text windows.");
        var updated = note with { Title = "更新后的标题", Summary = "更新后的重点", Content = "更新后的完整正文", Revision = 2 };
        live.Update(updated, "服务便签");
        foreach (var detail in details)
        {
            var box = (TextBox)detail.Content;
            if (!detail.IsVisible || detail.Title != updated.Title || box.Text != updated.Content)
                throw new Exception("Existing detail window did not refresh in place.");
            box.Select(1, 3);
            live.Update(updated, "服务便签");
            if (box.SelectionStart != 1 || box.SelectionLength != 3)
                throw new Exception("Unchanged polling disrupted text selection.");
        }
        details[0].Close();
        live.Update(updated with { Content = "再次更新正文", Revision = 3 }, "服务便签");
        if (((TextBox)details[1].Content).Text != "再次更新正文") throw new Exception("Remaining detail stopped updating after another closed.");
        live.Close();
        Console.WriteLine("PASS notes: open full-text windows refresh in place; unchanged polling preserves selection; closing one keeps others updating");
        var editorType = typeof(App).Assembly.GetType("DeskMonitor.NoteEditorWindow")!;
        NoteInput? saved = null;
        Func<NoteInput, Task> save = value => { saved = value; return Task.CompletedTask; };
        var editor = (Window)Activator.CreateInstance(editorType, new object?[] { null, false, save })!;
        var form = (FrameworkElement)editor.Content;
        editor.Content = null;
        form.Measure(new Size(570, 650)); form.Arrange(new Rect(0, 0, 570, 650)); form.UpdateLayout();
        var fields = Descendants(form).OfType<TextBox>().ToArray();
        var saveButton = Descendants(form).OfType<Button>().Single(b => Equals(b.Content, "保存便签"));
        saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (saved is not null || !Descendants(form).OfType<TextBlock>().Any(t => t.Text.StartsWith("未保存：")))
            throw new Exception("Empty editor must retain form and report validation failure.");
        fields[0].Text = note.Title; fields[1].Text = note.Summary; fields[2].Text = "完整分析正文";
        saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (saved?.Summary != note.Summary || saved?.Content != "完整分析正文") throw new Exception("Editor did not save separate summary and content.");
        Console.WriteLine("PASS notes: editor validation and successful save preserve independent fields");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
