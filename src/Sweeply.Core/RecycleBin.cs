using System.Runtime.InteropServices;

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
