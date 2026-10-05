using System.Runtime.InteropServices;
using System.Text;

namespace Sweeply.Core.Behavior;

/// <summary>
/// Turns the paths Windows' kernel uses into the ones people know: "\Device\HarddiskVolume3\Users\…" into
/// "C:\Users\…", "\REGISTRY\MACHINE\SOFTWARE\…" into "HKLM\SOFTWARE\…", and this user's
/// "\REGISTRY\USER\S-1-5-21-…\Software\…" into "HKCU\Software\…".
/// </summary>
public sealed class SystemPaths
{
    private readonly List<(string Device, string Drive)> _drives; // the longest device names first
    private readonly string? _userSid;

    public SystemPaths(IEnumerable<(string Device, string Drive)> drives, string? userSid)
    {
        _drives = drives.OrderByDescending(d => d.Device.Length).ToList();
        _userSid = userSid;
    }

    /// <summary>This PC's drives (what each drive letter stands for) and this user.</summary>
    public static SystemPaths ForThisPc()
    {
        var drives = new List<(string, string)>();
        var buffer = new StringBuilder(1024);
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            string drive = letter + ":";
            // The first of what it stands for; "\??\…" is a SUBST drive, whose files the kernel names by their real place.
            if (QueryDosDeviceW(drive, buffer, buffer.Capacity) > 0 && buffer.ToString() is { Length: > 0 } device && !device.StartsWith(@"\??\", StringComparison.Ordinal))
                drives.Add((device, drive));
        }
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new SystemPaths(drives, identity.User?.Value);
    }

    /// <summary>The event with its path made readable: programs and files as "C:\…", registry keys as "HKLM\…".</summary>
    public BehaviorEvent Normalize(BehaviorEvent e) => e.Kind switch
    {
        BehaviorKind.ProcessStarted or BehaviorKind.ProcessExited or BehaviorKind.FileCreated or BehaviorKind.FileWritten
            or BehaviorKind.FileDeleted or BehaviorKind.FileRenamed => e with { Target = File(e.Target) },
        BehaviorKind.KeyCreated or BehaviorKind.KeyDeleted or BehaviorKind.ValueSet or BehaviorKind.ValueDeleted => e with { Target = Registry(e.Target) },
        _ => e,
    };

    public string File(string path)
    {
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) return path[4..]; // "\??\C:\…"
        foreach (var (device, drive) in _drives)
            if (Under(path, device)) return drive + path[device.Length..];
        const string network = @"\Device\Mup";
        if (Under(path, network) && path.Length > network.Length) return @"\" + path[network.Length..];
        return path;
    }

    public string Registry(string path)
    {
        const string machine = @"\REGISTRY\MACHINE", user = @"\REGISTRY\USER";
        if (Under(path, machine)) return "HKLM" + path[machine.Length..];
        if (!Under(path, user)) return path;
        string rest = path[user.Length..]; // "\S-1-5-21-…\Software…", or nothing
        if (rest.Length < 2) return "HKU";
        int end = rest.IndexOf('\\', 1);
        string sid = end < 0 ? rest[1..] : rest[1..end], tail = end < 0 ? "" : rest[end..];
        if (_userSid is not null)
        {
            if (sid.Equals(_userSid, StringComparison.OrdinalIgnoreCase)) return "HKCU" + tail;
            if (sid.Equals(_userSid + "_Classes", StringComparison.OrdinalIgnoreCase)) return @"HKCU\Software\Classes" + tail;
        }
        return @"HKU\" + sid + tail;
    }

    /// <summary>The path is <paramref name="prefix"/> or something in it.</summary>
    private static bool Under(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (path.Length == prefix.Length || path[prefix.Length] == '\\');

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int QueryDosDeviceW(string deviceName, StringBuilder targetPath, int max);
}
