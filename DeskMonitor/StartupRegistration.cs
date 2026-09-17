using System;
using Microsoft.Win32;
namespace DeskMonitor;
internal static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "DeskMonitor";
    private static string Command => $"\"{Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。")}\"";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return string.Equals(key?.GetValue(EntryName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (enabled) key.SetValue(EntryName, Command, RegistryValueKind.String);
        else key.DeleteValue(EntryName, false);
    }
}
