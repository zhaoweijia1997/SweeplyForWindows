using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using Sweeply.Core.Capture;

namespace SweeplyForWindows.Capture;

/// <summary>
/// One live capture: starts the helper (through Windows' administrator prompt when capturing needs those rights),
/// makes sure the program that connects is the helper it started, and lets each packet's program be named as it
/// arrives. Stopping, or the app ending, closes the pipe, and the helper ends with it.
/// </summary>
internal sealed class CaptureSession : IDisposable
{
    private readonly Process _helper;

    private CaptureSession(CaptureConnection connection, Process helper)
    {
        Connection = connection;
        _helper = helper;
    }

    public CaptureConnection Connection { get; }

    /// <param name="elevate">Start the helper with administrator rights (one UAC prompt).</param>
    /// <param name="packetArrived">Called on a background thread for each packet (to name its program).</param>
    /// <param name="owner">The window the UAC prompt belongs to.</param>
    public static async Task<(CaptureSession? Session, HelperError? Error)> StartAsync(HelperRequest request, bool elevate,
        Action<CapturedPacket>? packetArrived, IntPtr owner)
    {
        string pipeName = $"SweeplyForWindows-capture-{Guid.NewGuid():N}";
        NamedPipeServerStream? server = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? helper = null;
        try
        {
            var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("no program path"))
            {
                Arguments = $"{HelperProcess.Argument} {pipeName} {Environment.ProcessId}",
                UseShellExecute = elevate,
                Verb = elevate ? "runas" : "",
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialogParentHandle = owner,
            };
            try { helper = await Task.Run(() => Process.Start(info)); } // the UAC prompt holds this call until it is answered
            catch (Win32Exception e) when (e.NativeErrorCode == 1223 /* ERROR_CANCELLED */) { return Fail("Cancelled", ""); }
            catch (Win32Exception e) { return Fail("HelperFailed", e.Message); }
            if (helper is null) return Fail("HelperFailed", "");

            // It connects within a second; if it ends first, something was wrong (another account's password was
            // typed into the prompt, say, and the pipe only lets this account in).
            using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var connected = server.WaitForConnectionAsync(wait.Token);
                await Task.WhenAny(connected, helper.WaitForExitAsync(wait.Token));
                if (!connected.IsCompletedSuccessfully)
                {
                    wait.Cancel();
                    bool ended = helper.HasExited;
                    return Fail(ended ? "HelperFailed" : "HelperTimeout", ended ? $"exit code {helper.ExitCode}" : "");
                }
            }
            if (HelperIdentity.ClientProcessId(server.SafePipeHandle) != helper.Id) return Fail("HelperFailed", "another program connected");

            var connection = new CaptureConnection(server) { PacketArrived = packetArrived };
            server = null; // the connection owns it now
            if (!await connection.StartAsync(request, TimeSpan.FromSeconds(15), CancellationToken.None))
            {
                var error = connection.Error;
                connection.Dispose();
                return (null, error);
            }
            var session = new CaptureSession(connection, helper);
            helper = null;
            return (session, null);
        }
        finally
        {
            server?.Dispose();
            helper?.Dispose();
        }

        static (CaptureSession?, HelperError?) Fail(string code, string detail) => (null, new HelperError(code, detail));
    }

    /// <summary>Asks the helper to stop, waits a moment for the last packets, then closes the pipe.</summary>
    public async Task StopAsync()
    {
        await Connection.StopAsync(TimeSpan.FromSeconds(3));
        Dispose();
    }

    /// <summary>Closes the pipe at once (the app is exiting); the helper ends by itself when it notices.</summary>
    public void Dispose()
    {
        Connection.Dispose();
        _helper.Dispose(); // only our handle: an elevated helper isn't ours to end, and it ends by itself anyway
    }
}
