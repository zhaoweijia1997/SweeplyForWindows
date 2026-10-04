using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Sweeply.Core.Capture;
using Sweeply.Core.Monitoring;

namespace SweeplyForWindows.Capture;

/// <summary>
/// "SweeplyForWindows.exe --helper &lt;pipe&gt; &lt;app process id&gt;": this same program started a second time, with
/// the rights capturing needs, to capture for the app and hand it the packets over a named pipe. It takes orders only
/// from the app that started it (the process at the other end of the pipe must have that id and be this same program
/// file), and ends when the app says stop, closes the pipe or exits. It has no window and no tray icon.
/// </summary>
internal static class HelperProcess
{
    public const string Argument = "--helper";

    /// <summary>Exit codes, so the app can tell why a helper ended before talking to it.</summary>
    public const int BadArguments = 2, AppGone = 3, CannotConnect = 4, NotOurApp = 5;

    public static int Run(IReadOnlyList<string> args)
    {
        if (args.Count != 3 || args[0] != Argument || args[1].Length is 0 or > 200 || !int.TryParse(args[2], out int appId))
            return BadArguments;
        Process app;
        try { app = Process.GetProcessById(appId); }
        catch (ArgumentException) { return AppGone; }
        using (app)
        using (var cancel = new CancellationTokenSource())
        {
            // The app ending (closed, crashed, killed) ends the helper too.
            try
            {
                app.EnableRaisingEvents = true;
                app.Exited += (_, _) => cancel.Cancel();
                if (app.HasExited) return AppGone;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return AppGone; }

            // Not CurrentUserOnly: .NET checks it against the token's default owner, which for an elevated token is the
            // Administrators group, not the user, so an elevated helper would always refuse. The app's end only lets
            // this user in, and who is at that end is checked right below.
            using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
            try { pipe.Connect(10_000); }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { return CannotConnect; }
            int server = HelperIdentity.ServerProcessId(pipe.SafePipeHandle);
            if (server != appId || !HelperIdentity.IsSameProgram(server, Environment.ProcessPath)) return NotOurApp;

            // Off the UI thread, so the awaits inside never wait for a dispatcher that is blocked right here.
            Task.Run(() => HelperHost.RunAsync(pipe, CreateSource, cancel.Token)).GetAwaiter().GetResult();
            return 0;
        }
    }

    private static IPacketSource CreateSource(HelperRequest request)
    {
        if (request.Mode != HelperRequest.CaptureMode) throw new HelperException("Failed", "unknown mode: " + request.Mode);
        if (request.Backend == HelperRequest.ReplayBackend)
            return new ReplaySource(request.File ?? throw new HelperException("FileFailed"), request.RealTime);
        var adapters = NetworkAdapters.Read()
            .Where(a => a.IsUp && request.Adapters.Contains(a.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (adapters.Count == 0) throw new HelperException("NoAdapter");
        // When a way can't start, the next one takes over: Npcap, then Windows' own packet capture, then raw sockets.
        // Adapters Windows' capture doesn't cover (tunnels) get raw sockets alongside it.
        var uncovered = NdisCaptureSource.Uncovered(adapters.Select(a => a.Id));
        IPacketSource Ndis() => uncovered.Count == 0 ? new NdisCaptureSource(adapters)
            : uncovered.Count == adapters.Count ? Raw()
            : new CombinedSource(new NdisCaptureSource(adapters, skip: uncovered), new RawSocketSource(adapters, only: uncovered));
        IPacketSource Raw() => new RawSocketSource(adapters);
        return request.Backend switch
        {
            HelperRequest.NpcapBackend => new FallbackSource(() => new NpcapSource(adapters), Ndis, Raw),
            HelperRequest.RawSocketBackend => Raw(),
            _ => new FallbackSource(Ndis, Raw),
        };
    }
}
