using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DeskMonitor.Core;

namespace DeskMonitor;

internal static class TrayTransition
{
    public static async Task HideAsync(Window window, DockedCorners corners, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SystemParameters.ClientAreaAnimation || !SystemParameters.MinimizeAnimation) { window.Hide(); return; }
        // Fade one temporary snapshot. The real widget stays opaque and does not
        // retain a layered rendering surface or animation clock while idle.
        var dpi = VisualTreeHelper.GetDpi(window);
        var snapshot = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        snapshot.Render(window);
        snapshot.Freeze();
        var overlay = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            IsHitTestVisible = false, Topmost = window.Topmost,
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
        try
        {
            overlay.Show();
            window.Hide();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            fade.Completed += (_, _) => completed.TrySetResult();
            overlay.BeginAnimation(UIElement.OpacityProperty, fade);
            await completed.Task.WaitAsync(token);
        }
        finally
        {
            overlay.BeginAnimation(UIElement.OpacityProperty, null);
            overlay.Close();
            overlay.Content = null;
        }
    }
}
