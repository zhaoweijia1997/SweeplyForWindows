using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace SweeplyForWindows.Platform;

/// <summary>
/// Keeps one copy of the app per signed-in user. A second launch hands its command line to the
/// running copy over a named pipe (only this user can connect) and exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    public SingleInstance(string appId)
    {
        string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _pipeName = $"{appId}-{user}";
        _mutex = new Mutex(initiallyOwned: true, @"Local\" + _pipeName, out bool createdNew);
        IsFirst = createdNew;
    }

    public bool IsFirst { get; }

    /// <summary>Sends <paramref name="args"/> to the running copy. False if it could not be reached.</summary>
    public bool SendToFirst(string[] args)
    {
        // The running copy may bring its window to the front: we are the foreground app, so we may allow it.
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            using var writer = new StreamWriter(client);
            writer.Write(JsonSerializer.Serialize(args));
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Calls <paramref name="received"/> (on a worker thread) with each later launch's command line.</summary>
    public void Listen(Action<string[]> received)
    {
        Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token);
                    using var reader = new StreamReader(server);
                    string text = await reader.ReadToEndAsync(_stop.Token);
                    received(JsonSerializer.Deserialize<string[]>(text) ?? []);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
                {
                    // A broken or garbled message: wait for the next one.
                }
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            if (IsFirst) _mutex.ReleaseMutex();
        }
        catch (ApplicationException) { } // not the owning thread: closing the handle releases it anyway
        _mutex.Dispose();
    }
}
