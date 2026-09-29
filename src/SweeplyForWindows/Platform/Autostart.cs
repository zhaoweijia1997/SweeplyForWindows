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

    /// <summary>If start-up is on, points it at this copy of the app (the exe may have been moved).</summary>
    public static void Refresh()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is string current && current != Command(Environment.ProcessPath!)) Set(true);
        }
        catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException) { }
    }
}
