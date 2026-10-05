using System.Runtime.InteropServices;
using Sweeply.Core.Capture;
using Sweeply.Core.Monitoring;

namespace Sweeply.Core.Behavior;

/// <summary>A process in a snapshot of the running ones.</summary>
public readonly record struct RunningProcess(int Id, int ParentId, string Name);

/// <summary>What a process has used so far: bytes read and written (files, network, devices), processor time, peak memory.</summary>
public readonly record struct ProcessUsage(long ReadBytes, long WriteBytes, TimeSpan ProcessorTime, long PeakMemory);

/// <summary>
/// What a normal user may read about the processes on this PC: the running ones with their parents, a process's
/// command line, start time and usage. Processes of other users and protected ones may refuse; then null.
/// </summary>
public static class ProcessDetails
{
    private const int QueryLimited = 0x1000;

    public static List<RunningProcess> Snapshot()
    {
        var result = new List<RunningProcess>();
        IntPtr snapshot = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry32 { Size = Marshal.SizeOf<ProcessEntry32>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                result.Add(new RunningProcess((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }

    public static string? ImagePath(int processId) => ProgramNames.ImagePath(processId);

    public static DateTime? StartedUtc(int processId)
    {
        IntPtr process = OpenProcess(QueryLimited, false, processId);
        if (process == IntPtr.Zero) return null;
        try { return GetProcessTimes(process, out long created, out _, out _, out _) ? DateTime.FromFileTimeUtc(created) : null; }
        finally { CloseHandle(process); }
    }

    /// <summary>The command line the process was started with (from the process itself, as Task Manager shows it).</summary>
    public static string? CommandLine(int processId)
    {
        IntPtr process = OpenProcess(QueryLimited, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            // ProcessCommandLineInformation: a UNICODE_STRING followed by its characters.
            int size = 4096;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    int status = NtQueryInformationProcess(process, 60, buffer, size, out int needed);
                    if (status == unchecked((int)0xC0000004) /* STATUS_INFO_LENGTH_MISMATCH */ && needed > size) { size = needed; continue; }
                    if (status != 0) return null;
                    int length = (ushort)Marshal.ReadInt16(buffer);
                    IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                    return text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, length / 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public static ProcessUsage? Usage(int processId)
    {
        IntPtr process = OpenProcess(QueryLimited, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!GetProcessIoCounters(process, out var io)) return null;
            GetProcessTimes(process, out _, out _, out long kernel, out long user);
            var memory = new ProcessMemoryCounters { Size = Marshal.SizeOf<ProcessMemoryCounters>() };
            long peak = K32GetProcessMemoryInfo(process, ref memory, memory.Size) ? (long)memory.PeakWorkingSetSize : 0;
            return new ProcessUsage((long)io.ReadTransferCount, (long)io.WriteTransferCount, TimeSpan.FromTicks(kernel + user), peak);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>
    /// A process kept open, so that how it ended can still be read once it has (a process opened again by its id after
    /// the end may be gone, or another one by now).
    /// </summary>
    public sealed class Watched : IDisposable
    {
        private IntPtr _handle;

        private Watched(IntPtr handle) => _handle = handle;

        public static Watched? Open(int processId)
        {
            IntPtr handle = OpenProcess(0x00100000 /* SYNCHRONIZE */ | QueryLimited, false, processId);
            return handle == IntPtr.Zero ? null : new Watched(handle);
        }

        public bool HasExited => _handle != IntPtr.Zero && WaitForSingleObject(_handle, 0) == 0;

        public int ExitCode => GetExitCodeProcess(_handle, out uint code) ? unchecked((int)code) : 0;

        public DateTime? ExitedUtc => GetProcessTimes(_handle, out _, out long exit, out _, out _) && exit > 0 ? DateTime.FromFileTimeUtc(exit) : null;

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public int Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public int Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage,
            QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("kernel32.dll")]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, int size);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returned);
}

/// <summary>
/// Recording without administrator rights: watches the running processes for the tracked ones' children (every half
/// second; one that starts and ends in between is missed) and how they end, the connection table for whom they
/// connect to (every second; TCP only, since UDP has no other end there), and, where this account may trace
/// (Performance Log Users), the DNS client for the names they look up. Files and the registry need the kernel's
/// events, which need administrator rights. Events go to <paramref name="emit"/> on a background thread.
/// </summary>
public sealed class BehaviorPoller(TrackedProcesses tracked, Action<BehaviorEvent> emit) : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<int, ProcessDetails.Watched?> _handles = new(); // kept open to learn exit codes
    private readonly HashSet<string> _connections = new();
    private Timer? _timer;
    private EtwSession? _dns;
    private int _ticks, _busy;

    public const string DnsSessionName = "SweeplyForWindows Behaviour";

    /// <summary>Whether names looked up are seen too (only where this account may trace).</summary>
    public bool SeesLookups => _dns is not null;

    public void Start()
    {
        lock (_lock) foreach (int id in tracked.Ids()) Watch(id);
        if (EtwSession.CanStart)
        {
            try
            {
                var reader = new KernelEventReader(tracked);
                var session = new EtwSession(DnsSessionName, bufferKb: 64, minBuffers: 4, maxBuffers: 16);
                session.Enable(KernelEventReader.DnsProvider, level: 5, matchAnyKeyword: ulong.MaxValue);
                session.Start((in EtwEvent e) =>
                {
                    if (e.Provider != KernelEventReader.DnsProvider) return;
                    BehaviorEvent? found;
                    lock (_lock)
                    {
                        if (!reader.Wanted(e.Provider, e.Id, e.ProcessId, 0)) return;
                        found = reader.Read(e.Provider, e.Id, e.Version, e.ProcessId, DateTime.FromFileTimeUtc(e.FileTime), e.Copy(0, e.UserDataLength));
                    }
                    if (found is not null) emit(found);
                });
                _dns = session;
            }
            catch (System.ComponentModel.Win32Exception) { } // names are a nicety here
        }
        _timer = new Timer(_ => Poll(), null, 0, 500);
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var events = new List<BehaviorEvent>();
            var now = DateTime.UtcNow;
            var running = ProcessDetails.Snapshot();
            lock (_lock)
            {
                // Children first (a parent's child may have started before the parent ended), then the ended ones.
                bool added;
                do
                {
                    added = false;
                    foreach (var p in running)
                        if (!tracked.Contains(p.Id) && tracked.Contains(p.ParentId) && Newer(p.Id, p.ParentId) && tracked.Started(p.Id, p.ParentId))
                        {
                            added = true;
                            Watch(p.Id);
                            events.Add(new BehaviorEvent
                            {
                                Kind = BehaviorKind.ProcessStarted, ProcessId = p.Id, Number = p.ParentId,
                                Target = ProcessDetails.ImagePath(p.Id) ?? p.Name, Detail = ProcessDetails.CommandLine(p.Id) ?? "",
                                TimeUtc = ProcessDetails.StartedUtc(p.Id) ?? now,
                            });
                        }
                } while (added);
                var alive = running.Select(p => p.Id).ToHashSet();
                foreach (int id in tracked.Ids().ToList())
                {
                    var handle = _handles.GetValueOrDefault(id);
                    bool ended = handle is not null ? handle.HasExited : !alive.Contains(id);
                    if (!ended) continue;
                    tracked.Exited(id);
                    int code = handle?.ExitCode ?? 0;
                    var when = handle?.ExitedUtc ?? now;
                    handle?.Dispose();
                    _handles.Remove(id);
                    events.Add(new BehaviorEvent { Kind = BehaviorKind.ProcessExited, ProcessId = id, Number = code, TimeUtc = when });
                }
            }
            if (_ticks++ % 2 == 0) events.AddRange(NewConnections(now));
            foreach (var e in events) emit(e);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>A child started after its parent did; an older process with a reused parent id isn't one.</summary>
    private static bool Newer(int child, int parent) =>
        ProcessDetails.StartedUtc(child) is not { } c || ProcessDetails.StartedUtc(parent) is not { } p || c >= p;

    private List<BehaviorEvent> NewConnections(DateTime now)
    {
        var result = new List<BehaviorEvent>();
        List<Connection> table;
        try { table = ConnectionTable.Read(); }
        catch (Exception e) when (e is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException) { return result; }
        lock (_lock)
        {
            var listening = table.Where(c => c.Protocol == TransportProtocol.Tcp && c.State == TcpConnectionState.Listen)
                .Select(c => (c.ProcessId, c.LocalPort)).ToHashSet();
            foreach (var c in table)
            {
                if (c.Protocol != TransportProtocol.Tcp || c.RemoteAddress is not { } remote || c.IsListening || c.IsLoopback || !tracked.Contains(c.ProcessId)) continue;
                string endpoint = remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{remote}]:{c.RemotePort}" : $"{remote}:{c.RemotePort}";
                bool inbound = listening.Contains((c.ProcessId, c.LocalPort));
                if (!_connections.Add($"{c.ProcessId}|{endpoint}|{c.LocalPort}")) continue;
                result.Add(new BehaviorEvent
                {
                    Kind = inbound ? BehaviorKind.Accepted : BehaviorKind.Connected, ProcessId = c.ProcessId, Target = endpoint, Detail = "TCP", TimeUtc = now,
                });
            }
        }
        return result;
    }

    /// <summary>Opens the process now, while its id surely is the one meant; one that refuses is watched through the snapshots.</summary>
    private void Watch(int id)
    {
        if (!_handles.ContainsKey(id)) _handles[id] = ProcessDetails.Watched.Open(id);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _dns?.Dispose();
        lock (_lock)
        {
            foreach (var handle in _handles.Values) handle?.Dispose();
            _handles.Clear();
        }
    }
}
