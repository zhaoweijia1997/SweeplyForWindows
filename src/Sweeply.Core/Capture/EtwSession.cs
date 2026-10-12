using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sweeply.Core.Capture;

/// <summary>One event as Event Tracing for Windows delivers it, read straight from its EVENT_RECORD.</summary>
public readonly ref struct EtwEvent
{
    private readonly IntPtr _record;

    internal EtwEvent(IntPtr record) => _record = record;

    // EVENT_RECORD: EVENT_HEADER (80 bytes), ETW_BUFFER_CONTEXT (4), ExtendedDataCount (2), UserDataLength (2),
    // ExtendedData, UserData, UserContext. EVENT_HEADER: ... ProcessId at 12, TimeStamp at 16, ProviderId at 24,
    // EVENT_DESCRIPTOR at 40 (Id, Version, Channel, Level, Opcode, Task, Keyword at 48).
    public int ProcessId => Marshal.ReadInt32(_record, 12);
    public long FileTime => Marshal.ReadInt64(_record, 16); // converted to system time by ETW (high resolution clock)
    public Guid Provider => Marshal.PtrToStructure<Guid>(_record + 24);
    public int Id => (ushort)Marshal.ReadInt16(_record, 40);
    public int Version => Marshal.ReadByte(_record, 42);
    public int Opcode => Marshal.ReadByte(_record, 45);
    public ulong Keyword => (ulong)Marshal.ReadInt64(_record, 48);
    public int UserDataLength => (ushort)Marshal.ReadInt16(_record, 86);
    public IntPtr UserData => Marshal.ReadIntPtr(_record, 96);

    /// <summary>A 32-bit field of the event's data (0 past its end).</summary>
    public uint ReadUInt32(int offset) => offset + 4 <= UserDataLength ? (uint)Marshal.ReadInt32(UserData, offset) : 0;

    /// <summary>A copy of part of the event's data, cut to what the event has.</summary>
    public byte[] Copy(int offset, int length)
    {
        length = Math.Max(0, Math.Min(length, UserDataLength - offset));
        var bytes = new byte[length];
        if (length > 0) Marshal.Copy(UserData + offset, bytes, 0, length);
        return bytes;
    }
}

/// <summary>
/// A real-time Event Tracing for Windows session of its own (see <see cref="CanStart"/> for who may start one), and a
/// consumer that hands each event to a callback on a background thread. ETW sessions outlive the process that started
/// them, so a session of the same name left over from before is stopped first, and this one is stopped when disposed.
/// </summary>
public sealed class EtwSession : IDisposable
{
    /// <summary>
    /// Whether this process may start (and stop) sessions: with administrator rights, or when the user is in the
    /// Performance Log Users group, which Windows lets trace without them.
    /// </summary>
    public static bool CanStart => Rights.Value;

    private static readonly Lazy<bool> Rights = new(() =>
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)
            || principal.IsInRole(new System.Security.Principal.SecurityIdentifier("S-1-5-32-559")); // Performance Log Users
    });

    private const int PropertiesSize = 120, NameRoom = 1024 * 2;
    private const uint RealTimeMode = 0x100, TracedGuidFlag = 0x20000;
    private const uint ControlQuery = 0, ControlStop = 1;
    private const uint ErrorAlreadyExists = 183, ErrorMoreData = 234, ErrorCancelled = 1223;
    private static readonly ulong InvalidHandle = ulong.MaxValue;

    public delegate void EventHandler(in EtwEvent e);

    private readonly string _name;
    private ulong _session, _consumer = InvalidHandle;
    private Thread? _thread;
    private NativeCallback? _callback; // kept alive while ETW may call it
    private volatile bool _stopped;

    /// <param name="bufferKb">Size of each buffer (in KB); <paramref name="maxBuffers"/> of them at most, in non-paged memory.</param>
    public EtwSession(string name, uint bufferKb = 256, uint minBuffers = 8, uint maxBuffers = 64)
    {
        _name = name;
        IntPtr properties = Properties(bufferKb, minBuffers, maxBuffers);
        try
        {
            uint status = StartTraceW(out _session, name, properties);
            if (status == ErrorAlreadyExists)
            {
                Stop(name); // left over by a run that couldn't stop it
                status = StartTraceW(out _session, name, properties);
            }
            if (status != 0) throw new Win32Exception((int)status);
        }
        finally
        {
            Marshal.FreeHGlobal(properties);
        }
    }

    /// <summary>Turns a provider on for this session (levels and keywords as the provider defines them).</summary>
    public void Enable(Guid provider, byte level, ulong matchAnyKeyword, ulong matchAllKeyword = 0)
    {
        uint status = EnableTraceEx2(_session, ref provider, 1 /* EVENT_CONTROL_CODE_ENABLE_PROVIDER */, level,
            matchAnyKeyword, matchAllKeyword, 0, IntPtr.Zero);
        if (status != 0) throw new Win32Exception((int)status);
    }

    /// <summary>Starts delivering events to <paramref name="handler"/> on a thread of its own, until disposed; <paramref name="ended"/> when it stops by itself.</summary>
    public void Start(EventHandler handler, Action<string>? ended = null)
    {
        _callback = record =>
        {
            if (_stopped) return;
            handler(new EtwEvent(record));
        };
        IntPtr logFile = Marshal.AllocHGlobal(LogFileSize);
        IntPtr loggerName = Marshal.StringToHGlobalUni(_name);
        try
        {
            for (int i = 0; i < LogFileSize; i += 8) Marshal.WriteInt64(logFile, i, 0);
            // EVENT_TRACE_LOGFILEW: LoggerName at 8, ProcessTraceMode at 28, EventRecordCallback at 424.
            Marshal.WriteIntPtr(logFile, 8, loggerName);
            Marshal.WriteInt32(logFile, 28, 0x100 | 0x10000000); // PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD
            Marshal.WriteIntPtr(logFile, 424, Marshal.GetFunctionPointerForDelegate(_callback));
            _consumer = OpenTraceW(logFile);
            if (_consumer == InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(logFile);
            Marshal.FreeHGlobal(loggerName);
        }
        _thread = new Thread(() =>
        {
            var handles = new[] { _consumer };
            uint status = ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
            if (!_stopped) ended?.Invoke(status is 0 or ErrorCancelled ? "" : new Win32Exception((int)status).Message);
        }) { IsBackground = true, Name = "etw " + _name };
        _thread.Start();
    }

    /// <summary>Events ETW had to drop (its buffers were full), so far.</summary>
    public long EventsLost
    {
        get
        {
            IntPtr properties = Properties(0, 0, 0);
            try
            {
                if (ControlTraceW(0, _name, properties, ControlQuery) != 0) return 0;
                return (uint)Marshal.ReadInt32(properties, 88) + (uint)Marshal.ReadInt32(properties, 100); // EventsLost, RealTimeBuffersLost
            }
            finally
            {
                Marshal.FreeHGlobal(properties);
            }
        }
    }

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        Stop(_name); // ProcessTrace returns once the session is gone
        if (_consumer != InvalidHandle) CloseTrace(_consumer);
        _thread?.Join(2000);
    }

    /// <summary>Stops a session by name; nothing happens when there is none.</summary>
    public static void Stop(string name)
    {
        IntPtr properties = Properties(0, 0, 0);
        try { ControlTraceW(0, name, properties, ControlStop); }
        finally { Marshal.FreeHGlobal(properties); }
    }

    /// <summary>An EVENT_TRACE_PROPERTIES with room for the session name after it.</summary>
    private static IntPtr Properties(uint bufferKb, uint minBuffers, uint maxBuffers)
    {
        int size = PropertiesSize + NameRoom;
        IntPtr p = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i += 4) Marshal.WriteInt32(p, i, 0);
        Marshal.WriteInt32(p, 0, size);                 // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);                   // Wnode.ClientContext: the high-resolution clock
        Marshal.WriteInt32(p, 44, (int)TracedGuidFlag); // Wnode.Flags
        Marshal.WriteInt32(p, 48, (int)bufferKb);
        Marshal.WriteInt32(p, 52, (int)minBuffers);
        Marshal.WriteInt32(p, 56, (int)maxBuffers);
        Marshal.WriteInt32(p, 64, (int)RealTimeMode);   // LogFileMode
        Marshal.WriteInt32(p, 68, 1);                   // FlushTimer: deliver at least every second
        Marshal.WriteInt32(p, 116, PropertiesSize);     // LoggerNameOffset
        return p;
    }

    private const int LogFileSize = 448; // sizeof(EVENT_TRACE_LOGFILEW) on 64-bit Windows

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void NativeCallback(IntPtr eventRecord);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong handle, string name, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong handle, string? name, IntPtr properties, uint control);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern ulong OpenTraceW(IntPtr logFile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong handle);
}
