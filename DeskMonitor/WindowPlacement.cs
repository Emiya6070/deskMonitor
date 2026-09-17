using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeskMonitor.Core;

namespace DeskMonitor;
internal static class WindowPlacement
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public readonly PixelRect Pixels => new(Left, Top, Right, Bottom); }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private static Rect WorkArea(Rect rect)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromRect(ref rect, 2), ref info)) throw new InvalidOperationException("无法获取显示器工作区。");
        return info.Work;
    }
    public static Size WorkSize(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("无法获取窗口位置。");
        var area = WorkArea(rect);
        var scale = GetDpiForWindow(hwnd) / 96d;
        return new Size((area.Right - area.Left) / scale, (area.Bottom - area.Top) / scale);
    }
    public static DockedCorners DockedCornersFor(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("无法获取窗口位置。");
        return WidgetLayout.CornersAtWorkArea(rect.Pixels, WorkArea(rect).Pixels);
    }
    public static void EnsureVisible(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("无法获取窗口位置。");
        var area = WorkArea(rect);
        var x = Math.Clamp(rect.Left, area.Left, Math.Max(area.Left, area.Right - (rect.Right - rect.Left)));
        var y = Math.Clamp(rect.Top, area.Top, Math.Max(area.Top, area.Bottom - (rect.Bottom - rect.Top)));
        if (x != rect.Left || y != rect.Top) SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x15); // No size, z-order or activation change.
    }
    public static SnapDrag BeginSnapDrag(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect) || !GetCursorPos(out var cursor)) throw new InvalidOperationException("无法获取拖动起点。");
        return new SnapDrag(rect.Pixels, cursor.X, cursor.Y, WorkArea(rect).Pixels);
    }
    public static void SnapMovingRect(IntPtr hwnd, IntPtr pointer, SnapDrag drag)
    {
        var rect = Marshal.PtrToStructure<Rect>(pointer);
        if (!GetCursorPos(out var cursor)) throw new InvalidOperationException("无法获取拖动位置。");
        var scale = GetDpiForWindow(hwnd) / 96d;
        var result = drag.Move(cursor.X, cursor.Y, rect.Pixels, WorkArea(rect).Pixels, (int)Math.Round(12 * scale), (int)Math.Round(24 * scale));
        rect.Left = result.Left; rect.Top = result.Top; rect.Right = result.Right; rect.Bottom = result.Bottom;
        Marshal.StructureToPtr(rect, pointer, false);
    }
}
