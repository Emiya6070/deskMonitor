using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeskMonitor;

internal sealed class ShowDesktopInput : IDisposable
{
    private readonly ShowDesktopKeys _keys = new();
    private bool _disposed;
    [StructLayout(LayoutKind.Sequential)] private struct Device { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    {
        public uint Type, Size;
        public IntPtr Device, Parameter;
        public ushort ScanCode, Flags, Reserved, Key;
        public uint Message, Extra;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(ref Device device, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr input, uint command, out KeyboardInput data, ref uint size, uint headerSize);

    public ShowDesktopInput(IntPtr window)
    {
        // INPUTSINK observes input without claiming the system shortcut, suppressing
        // normal keyboard messages, installing a global hook, or polling keys.
        var device = new Device { Page = 1, Usage = 6, Flags = 0x100, Target = window };
        if (!RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<Device>())) throw new Win32Exception();
    }

    public bool Read(IntPtr input)
    {
        if (_disposed) return false;
        var size = (uint)Marshal.SizeOf<KeyboardInput>();
        var copied = GetRawInputData(input, 0x10000003, out var data, ref size, (uint)(8 + 2 * IntPtr.Size));
        if (copied == uint.MaxValue) throw new Win32Exception();
        if (data.Type != 1 || copied < Marshal.SizeOf<KeyboardInput>() || data.ScanCode == 0xFF) return false;
        return _keys.Update(data.Key, (data.Flags & 1) != 0, (data.Flags & 2) != 0, data.ScanCode);
    }

    public void Dispose()
    {
        if (_disposed) return;
        var device = new Device { Page = 1, Usage = 6, Flags = 1 };
        if (!RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<Device>())) throw new Win32Exception();
        _disposed = true;
    }
}
