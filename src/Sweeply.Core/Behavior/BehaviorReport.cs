using System.Net;

namespace Sweeply.Core.Behavior;

/// <summary>A process seen while recording.</summary>
public sealed class ProcessEntry
{
    public required int Id { get; init; }
    public int ParentId { get; init; }
    public string Path { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public DateTime StartedUtc { get; init; }
    public DateTime? ExitedUtc { get; set; }
    public int? ExitCode { get; set; }

    /// <summary>The one chosen (or started) to record, as opposed to one it started.</summary>
    public bool IsRoot { get; set; }

    public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : $"#{Id}";
}

/// <summary>What the recorded processes did to one file.</summary>
public sealed class FileEntry
{
    public required string Path { get; init; }
    public bool Created { get; set; }
    public int Writes { get; set; }
    public long Bytes { get; set; }
    public bool Deleted { get; set; }
    public bool Renamed { get; set; }
    public HashSet<int> Processes { get; } = new();
    public DateTime FirstUtc { get; init; }
    public DateTime LastUtc { get; set; }
}

/// <summary>What the recorded processes did to one registry key, or to one value of it.</summary>
public sealed class RegistryEntry
{
    public required string Key { get; init; }

    /// <summary>The value's name ("" for the default value); null when the key itself was created or deleted.</summary>
    public string? Value { get; init; }

    public bool Created { get; set; }
    public int Sets { get; set; }
    public bool Deleted { get; set; }

    /// <summary>The value's last data, as text.</summary>
    public string Data { get; set; } = "";

    public HashSet<int> Processes { get; } = new();
    public DateTime FirstUtc { get; init; }
    public DateTime LastUtc { get; set; }
}

/// <summary>One address the recorded processes talked to.</summary>
public sealed class ConnectionEntry
{
    /// <summary>"203.0.113.10:443" or "[2001:db8::1]:443".</summary>
    public required string Remote { get; init; }

    public required string Protocol { get; init; }
    public bool Inbound { get; init; }
    public int Count { get; set; }

    /// <summary>The name that was looked up for its address while recording, if any.</summary>
    public string Host { get; set; } = "";

    public HashSet<int> Processes { get; } = new();
    public DateTime FirstUtc { get; init; }
}

/// <summary>One name the recorded processes looked up.</summary>
public sealed class LookupEntry
{
    public required string Name { get; init; }
    public int Count { get; set; }
    public HashSet<string> Addresses { get; } = new();
    public HashSet<int> Processes { get; } = new();
    public DateTime FirstUtc { get; init; }
}

/// <summary>Changes worth a look of their own: what makes a program start by itself, or touches Windows.</summary>
public enum NotableKind
{
    /// <summary>A "Run" registry value, Winlogon's Shell/Userinit, AppInit_DLLs: started at every sign-in.</summary>
    Autostart,

    /// <summary>A shortcut or program put in a Startup folder.</summary>
    StartupFolder,

    /// <summary>A Windows service created or changed.</summary>
    Service,

    /// <summary>A scheduled task created or changed.</summary>
    ScheduledTask,

    /// <summary>A program set to start in place of another ("Image File Execution Options").</summary>
    Debugger,

    /// <summary>The hosts file changed: names can then lead to other addresses.</summary>
    Hosts,

    /// <summary>A program or script written (.exe, .dll, .ps1, …).</summary>
    Executable,

    /// <summary>Something in the Windows or Program Files folder changed.</summary>
    SystemFolder,
}

/// <summary>A change worth a look; <paramref name="Data"/> is what a registry value holds (a Run entry: the program it starts).</summary>
public sealed record Notable(NotableKind Kind, string Target, int ProcessId, DateTime TimeUtc, string Data = "");

/// <summary>A folder in which files were changed: how many files, created, deleted, and bytes written.</summary>
public sealed record FolderSummary(string Folder, int Files, int Created, int Deleted, long Bytes);

/// <summary>Where the notable things are on this PC.</summary>
public sealed record KnownLocations(string Windows, IReadOnlyList<string> ProgramFiles, IReadOnlyList<string> StartupFolders, string Tasks, string Hosts)
{
    public static KnownLocations ForThisPc()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var startup = new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup }
            .Select(Environment.GetFolderPath).Where(p => p.Length > 0).ToList();
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return new KnownLocations(windows, programFiles, startup, Path.Combine(system, "Tasks"), Path.Combine(system, "drivers", "etc", "hosts"));
    }
}

/// <summary>
/// Adds up what the recorded processes did: the processes, every file and registry value they changed, whom they
/// talked to and which names they looked up, and the changes worth a look of their own (<see cref="NotableKind"/>).
/// Takes events whose paths are already "C:\…" and "HKLM\…" (<see cref="SystemPaths.Normalize"/>). Keeps every event
/// for the list of details up to <see cref="MaxEvents"/>, and adds up all of them. Use from one thread.
/// </summary>
public sealed class BehaviorReport(KnownLocations places)
{
    public const int MaxEvents = 200_000;
    private static readonly string[] Executables = { ".exe", ".dll", ".sys", ".scr", ".com", ".cpl", ".ocx", ".msi", ".ps1", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".hta", ".lnk" };

    private readonly Dictionary<int, ProcessEntry> _processes = new();
    private readonly Dictionary<string, FileEntry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, string?), RegistryEntry> _registry = new(KeyComparer.Instance);
    private readonly Dictionary<(string, string, bool), ConnectionEntry> _connections = new();
    private readonly Dictionary<string, LookupEntry> _lookups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _names = new(); // address → the name looked up for it
    private readonly List<Notable> _notables = new();
    private readonly HashSet<(NotableKind, string)> _notableSeen = new();
    private readonly List<BehaviorEvent> _events = new();
    private readonly List<string> _ownFolders = new();

    public IReadOnlyDictionary<int, ProcessEntry> Processes => _processes;
    public IReadOnlyCollection<FileEntry> Files => _files.Values;
    public IReadOnlyCollection<RegistryEntry> Registry => _registry.Values;
    public IReadOnlyCollection<ConnectionEntry> Connections => _connections.Values;
    public IReadOnlyCollection<LookupEntry> Lookups => _lookups.Values;
    public IReadOnlyList<Notable> Notables => _notables;
    public IReadOnlyList<BehaviorEvent> Events => _events;

    /// <summary>How many events came in; more than <see cref="Events"/> holds once it is full.</summary>
    public long EventCount { get; private set; }

    /// <summary>Counts every time anything changes, for views that refresh only when there is something new.</summary>
    public int Version { get; private set; }

    /// <summary>A process recorded from the start (the chosen one, or one it had started before recording began).</summary>
    public void AddRoot(int id, string path, string commandLine, DateTime startedUtc, int parentId = 0)
    {
        _processes[id] = new ProcessEntry { Id = id, ParentId = parentId, Path = path, CommandLine = commandLine, StartedUtc = startedUtc, IsRoot = true };
        AddOwnFolder(path);
        Version++;
    }

    /// <summary>A process recorded already is the one chosen (a program the app started, known once it had).</summary>
    public void MarkRoot(int id)
    {
        if (!_processes.TryGetValue(id, out var p)) return;
        p.IsRoot = true;
        AddOwnFolder(p.Path);
        Version++;
    }

    /// <summary>
    /// The chosen program's own folder: its product's folder under Program Files ("C:\Program Files (x86)\Vendor"),
    /// or else the folder it is in. What it writes there is its own business, not a change to Windows.
    /// </summary>
    private void AddOwnFolder(string path)
    {
        if (Path.GetDirectoryName(path) is not { Length: > 3 } folder) return;
        foreach (string programFiles in places.ProgramFiles)
            if (IsIn(folder, programFiles) || folder.Equals(programFiles, StringComparison.OrdinalIgnoreCase))
            {
                string rest = folder.Length > programFiles.Length ? folder[(programFiles.Length + 1)..] : "";
                if (rest.Length == 0) return; // a program right in Program Files: no folder of its own
                folder = Path.Combine(programFiles, rest.Split('\\')[0]);
                break;
            }
        if (IsIn(folder, places.Windows) || folder.Equals(places.Windows, StringComparison.OrdinalIgnoreCase)) return; // Windows' own programs
        if (!_ownFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) _ownFolders.Add(folder);
    }

    /// <summary>The folders with changed files, the most changed first.</summary>
    public List<FolderSummary> Folders(int most) => _files.Values
        .GroupBy(f => Path.GetDirectoryName(f.Path) ?? "", StringComparer.OrdinalIgnoreCase)
        .Select(g => new FolderSummary(g.Key, g.Count(), g.Count(f => f.Created), g.Count(f => f.Deleted), g.Sum(f => f.Bytes)))
        .OrderByDescending(f => f.Files).ThenByDescending(f => f.Bytes).ThenBy(f => f.Folder, StringComparer.OrdinalIgnoreCase)
        .Take(most).ToList();

    public void Add(BehaviorEvent e)
    {
        EventCount++;
        Version++;
        if (_events.Count < MaxEvents) _events.Add(e);
        switch (e.Kind)
        {
            case BehaviorKind.ProcessStarted:
                _processes[e.ProcessId] = new ProcessEntry { Id = e.ProcessId, ParentId = e.Number, Path = e.Target, CommandLine = e.Detail, StartedUtc = e.TimeUtc };
                break;
            case BehaviorKind.ProcessExited:
                if (_processes.TryGetValue(e.ProcessId, out var ended))
                {
                    ended.ExitedUtc = e.TimeUtc;
                    ended.ExitCode = e.Number;
                    if (ended.Path.Length == 0) ended.Path = e.Target;
                }
                break;
            case BehaviorKind.FileCreated or BehaviorKind.FileWritten or BehaviorKind.FileDeleted or BehaviorKind.FileRenamed:
                AddFile(e);
                break;
            case BehaviorKind.KeyCreated or BehaviorKind.KeyDeleted or BehaviorKind.ValueSet or BehaviorKind.ValueDeleted:
                AddRegistry(e);
                break;
            case BehaviorKind.Connected or BehaviorKind.Accepted:
                var key = (e.Target, e.Detail, e.Kind == BehaviorKind.Accepted);
                if (!_connections.TryGetValue(key, out var connection))
                {
                    _connections[key] = connection = new ConnectionEntry { Remote = e.Target, Protocol = e.Detail, Inbound = key.Item3, FirstUtc = e.TimeUtc };
                    if (Address(e.Target) is { } address && _names.TryGetValue(address, out var host)) connection.Host = host;
                }
                connection.Count++;
                connection.Processes.Add(e.ProcessId);
                break;
            case BehaviorKind.DnsLookup:
                if (!_lookups.TryGetValue(e.Target, out var lookup)) _lookups[e.Target] = lookup = new LookupEntry { Name = e.Target, FirstUtc = e.TimeUtc };
                lookup.Count++;
                lookup.Processes.Add(e.ProcessId);
                foreach (string answer in LookupAddresses(e.Detail))
                {
                    lookup.Addresses.Add(answer);
                    _names.TryAdd(answer, e.Target);
                    foreach (var c in _connections.Values)
                        if (c.Host.Length == 0 && Address(c.Remote) == answer) c.Host = e.Target;
                }
                break;
        }
    }

    private void AddFile(BehaviorEvent e)
    {
        if (!_files.TryGetValue(e.Target, out var file)) _files[e.Target] = file = new FileEntry { Path = e.Target, FirstUtc = e.TimeUtc };
        file.LastUtc = e.TimeUtc;
        file.Processes.Add(e.ProcessId);
        switch (e.Kind)
        {
            case BehaviorKind.FileCreated: file.Created = true; break;
            case BehaviorKind.FileWritten: file.Writes++; file.Bytes += e.Size; break;
            case BehaviorKind.FileDeleted: file.Deleted = true; break;
            case BehaviorKind.FileRenamed: file.Renamed = true; break;
        }
        if (FileNotable(e) is { } kind) Note(kind, e.Target, e);
    }

    private void AddRegistry(BehaviorEvent e)
    {
        string? value = e.Kind is BehaviorKind.ValueSet or BehaviorKind.ValueDeleted ? e.Detail : null;
        if (!_registry.TryGetValue((e.Target, value), out var entry))
            _registry[(e.Target, value)] = entry = new RegistryEntry { Key = e.Target, Value = value, FirstUtc = e.TimeUtc };
        entry.LastUtc = e.TimeUtc;
        entry.Processes.Add(e.ProcessId);
        switch (e.Kind)
        {
            case BehaviorKind.KeyCreated: entry.Created = true; break;
            case BehaviorKind.KeyDeleted or BehaviorKind.ValueDeleted: entry.Deleted = true; break;
            case BehaviorKind.ValueSet: entry.Sets++; entry.Data = e.Data; entry.Deleted = false; break;
        }
        if (RegistryNotable(e.Kind, e.Target, value) is { } kind) Note(kind, value is null ? e.Target : e.Target + @"\" + value, e);
    }

    private void Note(NotableKind kind, string target, BehaviorEvent e)
    {
        if (_notableSeen.Add((kind, target.ToUpperInvariant())))
        {
            _notables.Add(new Notable(kind, target, e.ProcessId, e.TimeUtc, e.Data));
            return;
        }
        // Noted already: keep the latest data of a value set again.
        if (e.Data.Length == 0) return;
        int i = _notables.FindIndex(n => n.Kind == kind && n.Target.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && _notables[i].Data != e.Data) _notables[i] = _notables[i] with { Data = e.Data };
    }

    private NotableKind? FileNotable(BehaviorEvent e)
    {
        string path = e.Target;
        bool changes = e.Kind is BehaviorKind.FileCreated or BehaviorKind.FileWritten or BehaviorKind.FileRenamed;
        if (changes && places.StartupFolders.Any(folder => IsIn(path, folder))) return NotableKind.StartupFolder;
        if (IsIn(path, places.Tasks)) return NotableKind.ScheduledTask;
        if (path.Equals(places.Hosts, StringComparison.OrdinalIgnoreCase) && e.Kind != BehaviorKind.FileDeleted) return NotableKind.Hosts;
        if (changes && Executables.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return NotableKind.Executable;
        if ((IsIn(path, places.Windows) && !IsIn(path, Path.Combine(places.Windows, "Temp")) || places.ProgramFiles.Any(folder => IsIn(path, folder)))
            && !_ownFolders.Any(folder => IsIn(path, folder)))
            return NotableKind.SystemFolder;
        return null;
    }

    /// <summary>Which notable kind a registry change is, by where it is ("HKLM\…" or "HKCU\…").</summary>
    public static NotableKind? RegistryNotable(BehaviorKind kind, string key, string? value)
    {
        bool sets = kind is BehaviorKind.ValueSet or BehaviorKind.KeyCreated;
        string k = key.Replace(@"\WOW6432Node", "", StringComparison.OrdinalIgnoreCase);
        int hive = k.IndexOf('\\');
        if (hive < 0) return null;
        string root = k[..hive], rest = k[(hive + 1)..];
        if (root is not ("HKLM" or "HKCU")) return null;
        const string current = @"Software\Microsoft\Windows\CurrentVersion\", nt = @"Software\Microsoft\Windows NT\CurrentVersion\";
        if (sets && value is not null)
        {
            foreach (string run in new[] { "Run", "RunOnce", "RunOnceEx", @"Policies\Explorer\Run" })
                if (rest.Equals(current + run, StringComparison.OrdinalIgnoreCase)) return NotableKind.Autostart;
            if (rest.Equals(nt + "Winlogon", StringComparison.OrdinalIgnoreCase) && value.ToLowerInvariant() is "shell" or "userinit" or "taskman")
                return NotableKind.Autostart;
            if (rest.Equals(nt + "Windows", StringComparison.OrdinalIgnoreCase) && value.Equals("AppInit_DLLs", StringComparison.OrdinalIgnoreCase))
                return NotableKind.Autostart;
            if (rest.StartsWith(nt + @"Image File Execution Options\", StringComparison.OrdinalIgnoreCase) && value.Equals("Debugger", StringComparison.OrdinalIgnoreCase))
                return NotableKind.Debugger;
        }
        if (root == "HKLM")
        {
            // A service's own key (not the settings under it), as created or as its program set.
            if (System.Text.RegularExpressions.Regex.IsMatch(rest, @"^SYSTEM\\(CurrentControlSet|ControlSet\d{3})\\Services\\[^\\]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && (kind == BehaviorKind.KeyCreated || value is not null && value.ToLowerInvariant() is "imagepath" or "start" or "servicedll"))
                return NotableKind.Service;
            if (rest.StartsWith(nt + @"Schedule\TaskCache\Tree\", StringComparison.OrdinalIgnoreCase) && kind is BehaviorKind.KeyCreated or BehaviorKind.KeyDeleted)
                return NotableKind.ScheduledTask;
        }
        return null;
    }

    private static bool IsIn(string path, string folder) =>
        folder.Length > 0 && path.Length > folder.Length && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && path[folder.Length] == '\\';

    /// <summary>The address part of "203.0.113.10:443" or "[2001:db8::1]:443".</summary>
    private static string? Address(string remote)
    {
        int colon = remote.LastIndexOf(':');
        if (colon <= 0) return null;
        return remote[..colon].Trim('[', ']');
    }

    /// <summary>
    /// The addresses in a lookup's answers as Windows' DNS client lists them: "type:  5 cdn.example.net;::ffff:203.0.113.10;",
    /// with IPv4 addresses mapped into IPv6.
    /// </summary>
    public static IEnumerable<string> LookupAddresses(string answers)
    {
        foreach (string part in answers.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPAddress.TryParse(part, out var address)) continue;
            yield return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        }
    }

    private sealed class KeyComparer : IEqualityComparer<(string, string?)>
    {
        public static readonly KeyComparer Instance = new();
        public bool Equals((string, string?) a, (string, string?) b) =>
            string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, string?) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Item1), key.Item2 is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(key.Item2));
    }
}
