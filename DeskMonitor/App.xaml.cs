using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
namespace DeskMonitor;
public partial class App : Application
{
    private Mutex? _instance;
    internal static readonly int ShowMessage = RegisterWindowMessage("DeskMonitor.ShowWidget");
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, @"Local\DeskMonitor.Widget", out var created);
        if (!created)
        {
            PostMessage(FindWindow(null, "DeskMonitor · 加密货币"), ShowMessage, IntPtr.Zero, IntPtr.Zero);
            _instance.Dispose(); _instance = null;
            Shutdown(); return;
        }
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.ReleaseMutex(); _instance?.Dispose(); base.OnExit(e); }
}
