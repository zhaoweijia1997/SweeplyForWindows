using System.ComponentModel;
using Sweeply.Core.Capture;

namespace Sweeply.Core.Behavior;

/// <summary>
/// Recording behaviour in the helper, with administrator rights: an ETW session of its own with the kernel's process,
/// file, registry and network providers and the DNS client's. The tracked processes' events go to the app in batches,
/// five times a second. Everything else the kernel reports is dropped at once, before its data is copied.
/// </summary>
public sealed class BehaviorJob : IHelperJob
{
    public const string SessionName = "SweeplyForWindows Behaviour Recorder";
    public const string Backend = "Behavior";
    private const int BatchBytes = 256 * 1024; // well below the pipe's message limit

    private readonly TrackedProcesses _tracked;
    private readonly KernelEventReader _reader;
    private readonly List<int> _initial;
    private readonly object _lock = new();
    private List<BehaviorEvent> _batch = new();
    private EtwSession? _session;
    private Timer? _timer;
    private Action<byte[]>? _send;
    private long _events;
    private int _disposed;

    /// <param name="launched">The app has just started it, suspended: it has started nothing yet.</param>
    public BehaviorJob(int processId, bool launched)
    {
        _initial = launched ? new List<int> { processId } : TrackedProcesses.WithDescendants(processId, Running()).ToList();
        _tracked = new TrackedProcesses(_initial);
        _reader = new KernelEventReader(_tracked, pid => ProcessDetails.CommandLine(pid) ?? "");
    }

    private static IEnumerable<(int, int, DateTime)> Running() =>
        ProcessDetails.Snapshot().Select(p => (p.Id, p.ParentId, ProcessDetails.StartedUtc(p.Id) ?? DateTime.MinValue));

    public void Start(Action<byte[]> message, Action<HelperError> failed)
    {
        _send = message;
        try
        {
            _session = new EtwSession(SessionName, bufferKb: 1024, minBuffers: 16, maxBuffers: 128);
            _session.Enable(KernelEventReader.ProcessProvider, level: 5, matchAnyKeyword: KernelEventReader.ProcessKeywords);
            _session.Enable(KernelEventReader.FileProvider, level: 5, matchAnyKeyword: KernelEventReader.FileKeywords);
            _session.Enable(KernelEventReader.RegistryProvider, level: 5, matchAnyKeyword: KernelEventReader.RegistryKeywords);
            _session.Enable(KernelEventReader.NetworkProvider, level: 5, matchAnyKeyword: KernelEventReader.NetworkKeywords);
            _session.Enable(KernelEventReader.DnsProvider, level: 5, matchAnyKeyword: ulong.MaxValue);
            _session.Start(OnEvent, ended =>
            {
                if (ended.Length > 0) failed(new HelperError("EtwFailed", ended));
            });
        }
        catch (Win32Exception e)
        {
            _session?.Dispose();
            _session = null;
            failed(new HelperError(e.NativeErrorCode == 5 /* ERROR_ACCESS_DENIED */ ? "NeedAdmin" : "EtwFailed", e.Message));
            return;
        }
        _timer = new Timer(_ => Flush(), null, 200, 200);
    }

    /// <summary>On the ETW thread; the only one that reads events, so the tracked set needs no lock until a flush.</summary>
    private void OnEvent(in EtwEvent e)
    {
        if (!_reader.Wanted(e.Provider, e.Id, e.ProcessId, e.ReadUInt32(0))) return;
        var data = e.Copy(0, e.UserDataLength);
        lock (_lock)
        {
            if (_reader.Read(e.Provider, e.Id, e.Version, e.ProcessId, DateTime.FromFileTimeUtc(e.FileTime), data) is { } found)
            {
                _batch.Add(Short(found));
                Interlocked.Increment(ref _events);
            }
        }
    }

    private void Flush()
    {
        List<BehaviorEvent> batch;
        lock (_lock)
        {
            foreach (var write in _reader.Flush()) _batch.Add(Short(write));
            if (_batch.Count == 0) return;
            batch = _batch;
            _batch = new List<BehaviorEvent>();
        }
        if (_send is not { } send) return;
        foreach (var part in Batches(batch)) send(HelperProtocol.EncodeJson(HelperMessage.Behavior, part));
    }

    /// <summary>Events split into batches of about <see cref="BatchBytes"/> each when written out.</summary>
    public static IEnumerable<List<BehaviorEvent>> Batches(IReadOnlyList<BehaviorEvent> events)
    {
        var part = new List<BehaviorEvent>();
        long size = 0;
        foreach (var e in events)
        {
            long bytes = 2 * (e.Target.Length + e.Detail.Length + e.Data.Length) + 120;
            if (part.Count > 0 && size + bytes > BatchBytes)
            {
                yield return part;
                part = new List<BehaviorEvent>();
                size = 0;
            }
            part.Add(e);
            size += bytes;
        }
        if (part.Count > 0) yield return part;
    }

    /// <summary>Strings cut short, so one event can never be too big to send (Windows' paths are at most 32,767 characters).</summary>
    private static BehaviorEvent Short(BehaviorEvent e) =>
        e.Target.Length <= 2048 && e.Detail.Length <= 2048 ? e : e with { Target = Cut(e.Target), Detail = Cut(e.Detail) };

    private static string Cut(string text) => text.Length <= 2048 ? text : text[..2048] + "…";

    public byte[] Hello() => HelperProtocol.EncodeJson(HelperMessage.Hello, new HelperHello(Backend, new List<CaptureInterface>(), null, _initial));

    public byte[] Stats(long droppedByHost) => HelperProtocol.EncodeStats(Interlocked.Read(ref _events), (_session?.EventsLost ?? 0) + droppedByHost);

    /// <summary>Stops the session (the kernel delivers what it still has first), then hands over the last batch.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _timer?.Dispose();
        _session?.Dispose();
        Flush();
    }

    /// <summary>A session left running by a helper that was ended by force (see <see cref="NdisCaptureSource.StopLeftover"/>).</summary>
    public static void StopLeftover()
    {
        if (EtwSession.CanStart) EtwSession.Stop(SessionName);
    }
}
