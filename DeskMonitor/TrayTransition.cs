using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using DeskMonitor.Core;

namespace DeskMonitor;

internal static class TrayTransition
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    public static async Task RunAsync(Window window, DockedCorners corners, bool show, bool desktop, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SystemParameters.ClientAreaAnimation || !SystemParameters.MinimizeAnimation)
        {
            if (show) ShowWithoutActivation(window); else window.Hide();
            return;
        }
        // Fade one temporary snapshot. The real widget stays opaque and does not
        // retain a layered rendering surface or animation clock while idle.
        var dpi = VisualTreeHelper.GetDpi(window);
        var snapshot = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        snapshot.Render(background);
        // The root Window stops drawing when hidden; its content visual retains
        // the card rendering, so a restore can fade in without showing it first.
        snapshot.Render((Visual)window.Content);
        snapshot.Freeze();
        var overlay = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            IsHitTestVisible = false, Topmost = desktop || window.Topmost, Opacity = show ? 0 : 1,
            Left = window.Left, Top = window.Top, Width = window.ActualWidth, Height = window.ActualHeight,
            Content = new Border
            {
                Background = new ImageBrush(snapshot) { Stretch = Stretch.Fill },
                CornerRadius = new CornerRadius(corners.HasFlag(DockedCorners.TopLeft) ? 0 : 8,
                    corners.HasFlag(DockedCorners.TopRight) ? 0 : 8,
                    corners.HasFlag(DockedCorners.BottomRight) ? 0 : 8,
                    corners.HasFlag(DockedCorners.BottomLeft) ? 0 : 8)
            }
        };
        // Explorer raises the desktop above tray-only windows. Keep just this
        // short-lived, non-activating animation above it until the fade ends.
        void KeepAboveDesktop(object? sender, EventArgs e)
        {
            if (!SetWindowPos(new WindowInteropHelper(overlay).Handle, new IntPtr(-1), 0, 0, 0, 0, 0x13)) throw new Win32Exception();
        }
        try
        {
            overlay.Show();
            if (desktop) CompositionTarget.Rendering += KeepAboveDesktop;
            window.Hide();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fade = new DoubleAnimation(show ? 0 : 1, show ? 1 : 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            fade.Completed += (_, _) => completed.TrySetResult();
            overlay.BeginAnimation(UIElement.OpacityProperty, fade);
            await completed.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (show) ShowWithoutActivation(window);
        }
        finally
        {
            if (desktop) CompositionTarget.Rendering -= KeepAboveDesktop;
            overlay.BeginAnimation(UIElement.OpacityProperty, null);
            overlay.Close();
            overlay.Content = null;
        }
    }

    private static void ShowWithoutActivation(Window window)
    {
        var activated = window.ShowActivated;
        try { window.ShowActivated = false; window.Show(); }
        finally { window.ShowActivated = activated; }
    }
}
