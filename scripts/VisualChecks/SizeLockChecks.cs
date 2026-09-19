using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using System.Runtime.CompilerServices;
using DeskMonitor;
using DeskMonitor.Core;

internal static class SizeLockChecks
{
    public static void InitializeApplication([CallerFilePath] string source = "")
    {
        // Load the actual theme without App.OnStartup's mutex, personal settings or feeds.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var document = XDocument.Load(Path.Combine(Path.GetDirectoryName(source)!, "../../DeskMonitor/App.xaml"));
        var resources = document.Root!.Elements().Single();
        resources.Name = resources.Name.Namespace + "ResourceDictionary";
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
    }
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    public static void Run()
    {
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
        void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        object? Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
        var persisted = new Dictionary<MainWindow, string>();
        MainWindow Create(Preferences preferences)
        {
            MainWindow window = null!;
            Action<Preferences> save = candidate => persisted[window] = JsonSerializer.Serialize(Preferences.Normalize(candidate));
            window = (MainWindow)Activator.CreateInstance(typeof(MainWindow), Private, null, new object[] { save }, null)!;
            window.ShowActivated = false; window.Opacity = 0;
            // Exercise a native WPF window without loading personal settings or starting feeds.
            window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
                typeof(MainWindow).GetMethod("WindowLoaded", Private)!);
            typeof(MainWindow).GetField("_preferences", Private)!.SetValue(window, preferences);
            new WindowInteropHelper(window).EnsureHandle();
            Call(window, "ApplyLayout", false);
            Call(window, "BuildCards");
            window.Show(); Pump();
            typeof(MainWindow).GetField("_ready", Private)!.SetValue(window, true);
            return window;
        }
        Preferences SaveForm(MainWindow window, Preferences preferences, Action<SettingsWindow> edit)
        {
            Preferences? saved = null;
            var settings = new SettingsWindow(preferences, new(), async (candidate, startup) =>
            {
                await (Task)Call(window, "ApplySettingsAsync", candidate, startup)!;
                saved = Preferences.Normalize(JsonSerializer.Deserialize<Preferences>(persisted[window])!);
            }) { Owner = window, Opacity = 0, ShowActivated = false };
            settings.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), settings,
                typeof(SettingsWindow).GetMethod("WindowLoaded", Private)!);
            typeof(SettingsWindow).GetField("_ready", Private)!.SetValue(settings, true);
            settings.Dispatcher.BeginInvoke(() =>
            {
                edit(settings);
                Call(settings, "SaveClicked", settings, new RoutedEventArgs());
            }, DispatcherPriority.ApplicationIdle);
            settings.ShowDialog();
            Pump();
            return saved ?? throw new Exception("Settings did not produce a save candidate.");
        }

        foreach (var style in Enum.GetValues<CardStyle>())
        foreach (var dateBelow in new[] { false, true })
        foreach (var clickLock in new[] { false, true })
        {
            var preferences = Preferences.Normalize(new() { Style = style, AllowResize = true, Pinned = false, SnapToEdges = false,
                DateProgress = new() { Enabled = dateBelow, Placement = DateProgressPlacement.Bottom, Period = DateProgressPeriod.Month } });
            var window = Create(preferences);
            try
            {
                var scale = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformToDevice;
                var resized = SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, 0, 0,
                    (int)Math.Round(Math.Max(window.MinWidth, window.Width - 40) * scale.M11), (int)Math.Round(270 * scale.M22), 0x0016);
                Check(resized, $"native resize succeeds (error {Marshal.GetLastWin32Error()}, handle {new WindowInteropHelper(window).Handle})");
                Pump();
                var width = window.ActualWidth;
                var height = window.ActualHeight;
                preferences = preferences with { Width = width, Height = height };
                var locked = SaveForm(window, preferences, settings =>
                    ((CheckBox)settings.FindName(clickLock ? "LockSizeOption" : "ResizeOption")).IsChecked = clickLock);
                Check(locked.LockCustomSize && !locked.AllowResize, $"{style}: disabling manual resize saves the lock");
                Call(window, "FitUsageHeight"); Pump();
                Check(Math.Abs(window.ActualWidth - width) < 1 && Math.Abs(window.ActualHeight - height) < 1,
                    $"{style}/date={dateBelow}: saved dimensions {window.ActualWidth}x{window.ActualHeight} match {width}x{height}; min={window.MinHeight}");

                locked = SaveForm(window, locked, settings => ((Slider)settings.FindName("MarketHeightSlider")).Value = 20);
                Check(locked.LockCustomSize && !WidgetLayout.ShouldResetSize(true, false, false, locked.LockCustomSize),
                    $"{style}: subsequent layout save retains lock");
                var restored = Preferences.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(locked))!);
                var restarted = Create(restored);
                try
                {
                    Call(restarted, "FitUsageHeight"); Pump();
                    Check(Math.Abs(restarted.ActualWidth - width) < 1 && Math.Abs(restarted.ActualHeight - height) < 1,
                        $"{style}: a new window restores serialized dimensions");
                }
                finally { restarted.Close(); Pump(); }

                var unlocked = SaveForm(window, locked, settings => ((CheckBox)settings.FindName("LockSizeOption")).IsChecked = false);
                Check(!unlocked.LockCustomSize && !unlocked.AllowResize, $"{style}: explicit unlock restores automatic sizing");
                Check(Math.Abs(window.ActualWidth - width) > 1 || Math.Abs(window.ActualHeight - height) > 1,
                    $"{style}: automatic size is applied after unlock");
                var manual = SaveForm(window, unlocked, settings => ((CheckBox)settings.FindName("ResizeOption")).IsChecked = true);
                Check(manual.AllowResize && !manual.LockCustomSize, $"{style}: re-enabling manual resize clears lock");
                window.Height = window.MaxHeight; Pump();
                var fullHeight = window.ActualHeight;
                var full = SaveForm(window, manual with { Width = window.ActualWidth, Height = fullHeight },
                    settings => ((CheckBox)settings.FindName("LockSizeOption")).IsChecked = true);
                Check(Math.Abs(window.ActualHeight - fullHeight) < 1 && Math.Abs(full.Height!.Value - fullHeight) < 1,
                    $"{style}: locking a full-work-area height does not subtract the automatic 24-DIP margin");
            }
            finally { window.Close(); Pump(); }
        }
        Console.WriteLine("Size lock integration checks passed; no network or personal settings writes.");
    }
}
