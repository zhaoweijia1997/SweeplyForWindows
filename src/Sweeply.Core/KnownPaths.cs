using System.Runtime.InteropServices;

namespace Sweeply.Core;

/// <summary>
/// The user folders the cleanup categories live under. Tests pass their own folders;
/// the app uses <see cref="FromSystem"/>.
/// </summary>
public sealed record KnownPaths(
    string Temp, string LocalAppData, string UserProfile, string Downloads,
    string? RoamingAppData = null, string? Documents = null)
{
    /// <summary>%APPDATA%; defaults to the usual place under the user profile.</summary>
    public string Roaming => RoamingAppData ?? Path.Combine(UserProfile, "AppData", "Roaming");

    /// <summary>The Documents folder; defaults to the usual place under the user profile.</summary>
    public string DocumentsFolder => Documents ?? Path.Combine(UserProfile, "Documents");

    public static KnownPaths FromSystem() => new(
        Temp: Path.GetFullPath(Path.GetTempPath()),
        LocalAppData: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        UserProfile: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Downloads: GetDownloadsFolder(),
        RoamingAppData: Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Documents: Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    /// <summary>
    /// The Downloads folder as Windows knows it — honours a user who moved it to another drive.
    /// </summary>
    private static string GetDownloadsFolder()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
        if (SHGetKnownFolderPath(downloads, 0, IntPtr.Zero, out IntPtr ptr) == 0)
        {
            try { return Marshal.PtrToStringUni(ptr)!; }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
