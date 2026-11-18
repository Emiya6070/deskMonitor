using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DeskMonitor;

internal sealed class NotesManagerWindow : Window
{
    public NotesManagerWindow(NotesController controller)
    {
        Title = "桌面便签 · 本地与 ChatGPT"; Width = 650; Height = 650; MinWidth = 500; MinHeight = 400;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        var panel = new DockPanel { Margin = new Thickness(20) };
        var config = new StackPanel(); DockPanel.SetDock(config, Dock.Top);
        var url = NoteUi.Field(config, "便签服务地址（基础地址，不含 /notes；留空只使用本地便签）", controller.ServiceUrl);
        config.Children.Add(new TextBlock { Text = "访问令牌（仅保存在当前 Windows 用户的本机设置文件中）", Margin = new Thickness(0, 8, 0, 4) });
        var token = new PasswordBox { Password = controller.Token, Padding = new Thickness(6) }; config.Children.Add(token);
        token.SetResourceReference(Control.BackgroundProperty, "CardBackground");
        token.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) };
        var list = new ListBox { DisplayMemberPath = "", Background = System.Windows.Media.Brushes.Transparent };
        void Changed()
        {
            var selected = list.SelectedItem as NoteEntry;
            list.ItemsSource = controller.Entries;
            if (selected is not null)
                foreach (NoteEntry item in list.Items) if (item.Remote == selected.Remote && item.Note.Id == selected.Note.Id) list.SelectedItem = item;
            status.Text = controller.Status;
        }
        var busy = false;
        async Task Run(Func<Task> action)
        {
            if (busy) return;
            busy = true;
            try { await action(); }
            catch (Exception ex) when (NotesController.IsExpectedError(ex)) { status.Text = ex.Message; }
            finally { busy = false; }
        }
        var connection = new WrapPanel();
        connection.Children.Add(NoteUi.Button("保存连接", async () => await Run(() => controller.ConfigureAsync(url.Text, token.Password))));
        connection.Children.Add(NoteUi.Button("立即刷新", async () => await Run(controller.RefreshAsync)));
        config.Children.Add(connection); config.Children.Add(status); panel.Children.Add(config);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom);
        actions.Children.Add(NoteUi.Button("新建本地便签", () => controller.Edit(null)));
        actions.Children.Add(NoteUi.Button("新建服务便签", () => controller.Edit(null, true)));
        actions.Children.Add(NoteUi.Button("显示", () => { if (list.SelectedItem is NoteEntry e) controller.ShowNote(e); }));
        actions.Children.Add(NoteUi.Button("编辑", () => { if (list.SelectedItem is NoteEntry e) controller.Edit(e); }));
        actions.Children.Add(NoteUi.Button("删除", async () => { if (list.SelectedItem is NoteEntry e) await Run(() => controller.DeleteAsync(e)); }));
        panel.Children.Add(actions); panel.Children.Add(list); Content = panel;
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is NoteEntry e) controller.ShowNote(e); };
        controller.Changed += Changed; Closed += (_, _) => controller.Changed -= Changed; Changed();
    }
}
