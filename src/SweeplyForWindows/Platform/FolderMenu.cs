using System.IO;
using System.Security;
using Microsoft.Win32;
using Sweeply.Core;

namespace SweeplyForWindows.Platform;

/// <summary>
/// "Clean up with SweeplyForWindows" on the folder right-click menu: on a folder, and on the empty space
/// inside an open folder. Written under the current user's Classes key, so no administrator rights are
/// needed and nothing changes for other users; turning it off removes both keys again.
/// On Windows 11 the entry is under "Show more options".
/// </summary>
internal static class FolderMenu
{
    public const string ScanFolderArg = FolderJunk.ScanFolderArg;
    private const string VerbName = "SweeplyForWindows";

    // (key, the placeholder Explorer replaces with the folder)
    private static readonly (string Key, string Placeholder)[] Places =
    {
        (@"Software\Classes\Directory\shell\" + VerbName, "%1"),
        (@"Software\Classes\Directory\Background\shell\" + VerbName, "%V"),
    };

    public static string Command(string exePath, string placeholder) => $"\"{exePath}\" {ScanFolderArg} \"{placeholder}\"";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(Places[0].Key + @"\command");
                return key?.GetValue(null) is string;
            }
            catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <param name="label">The menu text, in the app's language.</param>
    public static void Set(bool enabled, string label)
    {
        try
        {
            foreach (var (keyPath, placeholder) in Places)
            {
                if (!enabled)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
                    continue;
                }
                using var key = Registry.CurrentUser.CreateSubKey(keyPath);
                key.SetValue("MUIVerb", label);
                key.SetValue("Icon", $"\"{Environment.ProcessPath}\",0");
                using var command = key.CreateSubKey("command");
                command.SetValue(null, Command(Environment.ProcessPath!, placeholder));
            }
        }
        catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Keeps an entry that is on pointing at a working exe, with the menu text in the current language.
    /// Like start-up, an entry for another copy that still exists is left alone.
    /// </summary>
    public static void Refresh(string label)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Places[0].Key + @"\command");
            if (key?.GetValue(null) is not string current) return;
            string? registered = Autostart.RegisteredExe(current);
            bool ours = registered is not null && string.Equals(Path.GetFullPath(registered), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
            if (ours || registered is null || !File.Exists(registered)) Set(true, label);
        }
        catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
