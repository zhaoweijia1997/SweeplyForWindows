using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Sweeply.Core;

public interface IRecycleBin
{
    /// <summary>Moves one file or folder to the Recycle Bin. Returns false with a reason if it didn't happen.</summary>
    bool TryRecycle(string path, out string? error);
}

/// <summary>The Windows Recycle Bin, through the shell's own file operation.</summary>
public sealed class ShellRecycleBin : IRecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;          // Recycle Bin instead of permanent delete
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;    // ask before deleting permanently (item too big for the bin)

    private readonly IntPtr _owner;

    /// <param name="owner">Window that owns the warning Windows shows if something can't go to the Recycle Bin.</param>
    public ShellRecycleBin(IntPtr owner = default) => _owner = owner;

    public bool TryRecycle(string path, out string? error)
    {
        var op = new SHFILEOPSTRUCT
        {
            hwnd = _owner,
            wFunc = FO_DELETE,
            pFrom = path + '\0',  // the marshaller adds the second terminating null
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_NOERRORUI | FOF_SILENT,
        };
        int result = SHFileOperation(ref op);

        // Don't trust the return code alone: the item must really be gone.
        bool gone = !File.Exists(path) && !Directory.Exists(path);
        if (result == 0 && !op.fAnyOperationsAborted && gone)
        {
            error = null;
            return true;
        }
        error = op.fAnyOperationsAborted ? "cancelled" : result != 0 ? $"shell error 0x{result:X}" : "still there (in use?)";
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}

/// <summary>
/// Whether an item would really go to the Recycle Bin. Windows deletes for good (after asking) what is
/// bigger than its drive's Recycle Bin, and everything when that Recycle Bin is set to "Don't move files
/// to the Recycle Bin" or a policy turns recycling off. A clean nobody is watching leaves such items
/// alone, so it asks here first. Reads what the Recycle Bin's Properties window writes.
/// </summary>
public static class RecycleBinCapacity
{
    private const string VolumeKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\";
    private const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    public static bool Fits(string path, long bytes)
    {
        if (TurnedOffByPolicy()) return false;
        var (nukeOnDelete, maxCapacityMb) = ReadVolume(path);
        return Fits(bytes, nukeOnDelete, maxCapacityMb);
    }

    /// <param name="maxCapacityMb">
    /// The drive's Recycle Bin size; null when never set, which leaves Windows' default: a share of the
    /// drive, many gigabytes on any usual disk.
    /// </param>
    public static bool Fits(long bytes, bool nukeOnDelete, long? maxCapacityMb) =>
        !nukeOnDelete && (maxCapacityMb is not { } max || bytes <= max * 1024 * 1024);

    private static (bool NukeOnDelete, long? MaxCapacityMb) ReadVolume(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return (false, null);
            if (!root.EndsWith('\\')) root += '\\';
            var volume = new StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPoint(root, volume, volume.Capacity)) return (false, null);
            string name = volume.ToString(); // \\?\Volume{guid}\
            int open = name.IndexOf('{'), close = name.IndexOf('}');
            if (open < 0 || close < open) return (false, null);
            using var key = Registry.CurrentUser.OpenSubKey(VolumeKey + name[open..(close + 1)]);
            if (key is null) return (false, null);
            bool nuke = key.GetValue("NukeOnDelete") is int n && n != 0;
            long? max = key.GetValue("MaxCapacity") is int m && m > 0 ? m : null;
            return (nuke, max);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            return (false, null);
        }
    }

    private static bool TurnedOffByPolicy()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(PolicyKey);
                if (key?.GetValue("NoRecycleFiles") is int v && v != 0) return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int length);
}
