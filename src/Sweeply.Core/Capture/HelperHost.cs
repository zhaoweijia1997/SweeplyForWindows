using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using Sweeply.Core.Monitoring;

namespace Sweeply.Core.Capture;

/// <summary>A reason the helper gives up with, as a <see cref="HelperError"/> code.</summary>
public sealed class HelperException(string code, string detail = "") : Exception(detail.Length > 0 ? detail : code)
{
    public string Code { get; } = code;
}

/// <summary>
/// The helper's end of the pipe. Reads what to do, starts the source, says Hello, then sends the packets and every
/// second the counts, until the app says stop or goes away, or the source fails (then Error is the last message).
/// Packets wait in a bounded queue: when the app can't keep up they are dropped and counted, so the helper never
/// piles up memory.
/// </summary>
public static class HelperHost
{
    public const int QueueLimit = 100_000;
    public const long QueueBytesLimit = 64L << 20;
    private const int FlushAt = 256 * 1024;

    public static async Task RunAsync(Stream pipe, Func<HelperRequest, IPacketSource> createSource, CancellationToken cancel)
    {
        HelperRequest? request = null;
        try
        {
            var first = await HelperProtocol.ReadAsync(pipe, cancel);
            if (first is { Type: HelperMessage.Start } start) request = HelperProtocol.DecodeJson<HelperRequest>(start.Payload);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or OperationCanceledException) { }
        if (request is null) return;

        IPacketSource source;
        try { source = createSource(request); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await TrySendAsync(pipe, HelperProtocol.EncodeJson(HelperMessage.Error, ErrorFor(e)), cancel);
            return;
        }

        using (source)
        {
            var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueLimit) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            long queuedBytes = 0, dropped = 0;
            HelperError? failure = null;
            source.Start(packet =>
            {
                var message = HelperProtocol.EncodePacket(packet);
                if (Interlocked.Add(ref queuedBytes, message.Length) > QueueBytesLimit || !queue.Writer.TryWrite(message))
                {
                    Interlocked.Add(ref queuedBytes, -message.Length);
                    Interlocked.Increment(ref dropped);
                }
            }, error =>
            {
                Interlocked.CompareExchange(ref failure, error, null);
                queue.Writer.TryComplete();
            });
            if (Volatile.Read(ref failure) is { } early)
            {
                await TrySendAsync(pipe, HelperProtocol.EncodeJson(HelperMessage.Error, early), cancel);
                return;
            }
            if (!await TrySendAsync(pipe, HelperProtocol.EncodeJson(HelperMessage.Hello, new HelperHello(source.Backend, source.Interfaces.ToList())), cancel))
                return;

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var watcher = WatchForStopAsync(pipe, stop);
            var buffer = new ArrayBufferWriter<byte>(FlushAt * 2);
            long nextStats = Environment.TickCount64 + 1000;
            bool appGone = false;
            byte[] Stats() => HelperProtocol.EncodeStats(source.Received, source.Dropped + Interlocked.Read(ref dropped));
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    bool more = true;
                    long wait = nextStats - Environment.TickCount64;
                    if (wait > 0 && !queue.Reader.TryPeek(out _))
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        timeout.CancelAfter(TimeSpan.FromMilliseconds(wait));
                        try { more = await queue.Reader.WaitToReadAsync(timeout.Token); }
                        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { } // time for the counts
                    }
                    if (!more) break; // the source failed; Error follows below
                    while (buffer.WrittenCount < FlushAt && queue.Reader.TryRead(out var message))
                    {
                        Interlocked.Add(ref queuedBytes, -message.Length);
                        buffer.Write(message);
                    }
                    if (Environment.TickCount64 >= nextStats)
                    {
                        buffer.Write(Stats());
                        nextStats = Environment.TickCount64 + 1000;
                    }
                    if (buffer.WrittenCount > 0)
                    {
                        // Never cancelled half way: a message cut in two would garble everything after it.
                        await pipe.WriteAsync(buffer.WrittenMemory, cancel);
                        await pipe.FlushAsync(cancel);
                        buffer.ResetWrittenCount();
                    }
                }
            }
            catch (OperationCanceledException) { } // the helper is being shut down
            catch (Exception e) when (e is IOException or ObjectDisposedException) { appGone = true; }
            finally
            {
                stop.Cancel();
            }
            await watcher;

            // Stop the source first so nothing more comes in and its counts are final, then send what is still
            // queued, the last counts, and why it ended when it failed.
            source.Dispose();
            if (appGone) return;
            buffer.ResetWrittenCount();
            while (queue.Reader.TryRead(out var rest))
            {
                buffer.Write(rest);
                if (buffer.WrittenCount >= FlushAt)
                {
                    if (!await TrySendAsync(pipe, buffer.WrittenSpan.ToArray(), cancel)) return;
                    buffer.ResetWrittenCount();
                }
            }
            buffer.Write(Stats());
            if (Volatile.Read(ref failure) is { } late) buffer.Write(HelperProtocol.EncodeJson(HelperMessage.Error, late));
            await TrySendAsync(pipe, buffer.WrittenSpan.ToArray(), cancel);
        }
    }

    /// <summary>Ends <paramref name="stop"/> when the app sends Stop, closes its end, or the helper is shutting down.</summary>
    private static async Task WatchForStopAsync(Stream pipe, CancellationTokenSource stop)
    {
        try
        {
            while (true)
            {
                var message = await HelperProtocol.ReadAsync(pipe, stop.Token);
                if (message is null || message.Value.Type == HelperMessage.Stop) break;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { }
        try { stop.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static HelperError ErrorFor(Exception e) => e switch
    {
        HelperException h => new HelperError(h.Code, h.Message == h.Code ? "" : h.Message),
        IOException or UnauthorizedAccessException or InvalidDataException => new HelperError("FileFailed", e.Message),
        _ => new HelperError("Failed", e.Message),
    };

    private static async Task<bool> TrySendAsync(Stream pipe, byte[] message, CancellationToken cancel)
    {
        try
        {
            await pipe.WriteAsync(message, cancel);
            await pipe.FlushAsync(cancel);
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { return false; }
    }
}

/// <summary>
/// The app's end of the pipe to a capture helper: sends the request, then collects what comes back on a
/// background task — packets into <see cref="Packets"/> (after <see cref="PacketArrived"/>, which can name their
/// program), the counts, and why it ended.
/// </summary>
public sealed class CaptureConnection : IDisposable
{
    private readonly Stream _pipe;
    private readonly CancellationTokenSource _stop = new();
    private Task _reading = Task.CompletedTask;
    private long _received, _dropped;
    private volatile HelperError? _error;
    private volatile bool _stopping; // the pipe closing is then the normal end

    public CaptureConnection(Stream pipe) => _pipe = pipe;

    public HelperHello? Hello { get; private set; }
    public HelperError? Error => _error;
    public ConcurrentQueue<CapturedPacket> Packets { get; } = new();
    public long Received => Interlocked.Read(ref _received);
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Called on the reading thread for each packet, before it is queued (to name its program).</summary>
    public Action<CapturedPacket>? PacketArrived { get; init; }

    /// <summary>Done when the helper has ended or the pipe broke.</summary>
    public Task Completion => _reading;

    /// <summary>Sends the request and waits for Hello. False, with <see cref="Error"/> set, when the helper could not start.</summary>
    public async Task<bool> StartAsync(HelperRequest request, TimeSpan timeout, CancellationToken cancel)
    {
        var reader = new BufferedStream(_pipe, 1 << 16);
        (HelperMessage Type, byte[] Payload)? first;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            wait.CancelAfter(timeout);
            try
            {
                await _pipe.WriteAsync(HelperProtocol.EncodeJson(HelperMessage.Start, request), wait.Token);
                await _pipe.FlushAsync(wait.Token);
                first = await HelperProtocol.ReadAsync(reader, wait.Token);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                _error = new HelperError("HelperTimeout", "");
                return false;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException)
            {
                _error = new HelperError("HelperLost", e.Message);
                return false;
            }
        }
        try
        {
            switch (first?.Type)
            {
                case HelperMessage.Hello:
                    Hello = HelperProtocol.DecodeJson<HelperHello>(first.Value.Payload);
                    break;
                case HelperMessage.Error:
                    _error = HelperProtocol.DecodeJson<HelperError>(first.Value.Payload) ?? new HelperError("Failed", "");
                    return false;
            }
        }
        catch (JsonException e)
        {
            _error = new HelperError("HelperLost", e.Message);
            return false;
        }
        if (Hello is null)
        {
            _error ??= new HelperError("HelperLost", "");
            return false;
        }
        _reading = Task.Run(() => ReadAsync(reader));
        return true;
    }

    private async Task ReadAsync(Stream reader)
    {
        try
        {
            while (true)
            {
                var message = await HelperProtocol.ReadAsync(reader, _stop.Token);
                if (message is not { } m)
                {
                    if (!_stopping) _error ??= new HelperError("HelperLost", "");
                    return;
                }
                switch (m.Type)
                {
                    case HelperMessage.Packet:
                        var packet = HelperProtocol.DecodePacket(m.Payload);
                        PacketArrived?.Invoke(packet);
                        Packets.Enqueue(packet);
                        break;
                    case HelperMessage.Stats:
                        var (received, dropped) = HelperProtocol.DecodeStats(m.Payload);
                        Interlocked.Exchange(ref _received, received);
                        Interlocked.Exchange(ref _dropped, dropped);
                        break;
                    case HelperMessage.Error:
                        _error = HelperProtocol.DecodeJson<HelperError>(m.Payload) ?? new HelperError("Failed", "");
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or ObjectDisposedException)
        {
            if (!_stopping) _error ??= new HelperError("HelperLost", e.Message);
        }
    }

    /// <summary>Asks the helper to stop, and waits a little for the last packets and counts.</summary>
    public async Task StopAsync(TimeSpan wait)
    {
        _stopping = true;
        try
        {
            await _pipe.WriteAsync(HelperProtocol.Encode(HelperMessage.Stop, ReadOnlySpan<byte>.Empty));
            await _pipe.FlushAsync();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        await Task.WhenAny(_reading, Task.Delay(wait));
        _stop.Cancel();
    }

    public void Dispose()
    {
        _stopping = true;
        _stop.Cancel();
        _pipe.Dispose();
    }
}

/// <summary>
/// Who is at the other end of a pipe. The helper only works for an app that is this same program file, and the app
/// only talks to the helper it started; another program that guessed the pipe's name gets nothing.
/// </summary>
public static class HelperIdentity
{
    public static int ServerProcessId(SafePipeHandle pipe) => GetNamedPipeServerProcessId(pipe, out uint id) ? (int)id : 0;

    public static int ClientProcessId(SafePipeHandle pipe) => GetNamedPipeClientProcessId(pipe, out uint id) ? (int)id : 0;

    /// <summary>Whether process <paramref name="processId"/> runs the program file at <paramref name="path"/>.</summary>
    public static bool IsSameProgram(int processId, string? path)
    {
        if (processId <= 0 || path is null) return false;
        string? other = ProgramNames.ImagePath(processId);
        return other is not null && string.Equals(Path.GetFullPath(other), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
