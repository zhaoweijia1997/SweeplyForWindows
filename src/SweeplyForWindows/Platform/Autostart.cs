using System.IO;
using System.Security;
using Microsoft.Win32;

namespace SweeplyForWindows.Platform;

/// <summary>
/// "Start with Windows": one value under the current user's Run key, so no administrator rights are
/// needed and nothing is written for other users. The app starts with <c>--background</c> (icon only).
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SweeplyForWindows";
    public const string BackgroundArg = "--background";

    public static string Command(string exePath) => $"\"{exePath}\" {BackgroundArg}";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, Command(Environment.ProcessPath!));
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// If start-up is on but its exe no longer exists (moved or deleted), points it at this copy.
    /// A registered exe that still exists is left alone: running another copy (a test build, a
    /// second download) must not take start-up over from the copy the user set up.
    /// </summary>
    public static void Refresh()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string current) return;
            string? registered = RegisteredExe(current);
            if (registered is null || !File.Exists(registered)) Set(true);
        }
        catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The exe path in a command line written by <see cref="Command"/>: <c>"C:\path\app.exe" --background</c>.</summary>
    internal static string? RegisteredExe(string command)
    {
        if (!command.StartsWith('"')) return null;
        int end = command.IndexOf('"', 1);
        return end > 1 ? command[1..end] : null;
    }
}
