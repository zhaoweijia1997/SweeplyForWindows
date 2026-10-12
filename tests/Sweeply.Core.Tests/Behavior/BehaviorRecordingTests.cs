using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Sweeply.Core.Behavior;
using Sweeply.Core.Capture;

namespace Sweeply.Core.Tests.Behavior;

public class BehaviorRecordingTests
{
    [Fact]
    public void Events_go_in_batches_small_enough_for_the_pipe()
    {
        var events = Enumerable.Range(0, 3000).Select(i => new BehaviorEvent { Kind = BehaviorKind.FileWritten, Target = @"C:\x\" + new string('a', 200) + i }).ToList();
        var batches = BehaviorJob.Batches(events).ToList();
        Assert.True(batches.Count > 1);
        Assert.Equal(3000, batches.Sum(b => b.Count));
        Assert.All(batches, b => Assert.True(HelperProtocol.EncodeJson(HelperMessage.Behavior, b).Length < HelperProtocol.MaxPayload));
        Assert.Equal(events.Select(e => e.Target), batches.SelectMany(b => b).Select(e => e.Target)); // in order
        Assert.Empty(BehaviorJob.Batches(Array.Empty<BehaviorEvent>()));
    }

    [Fact]
    public void A_process_tells_its_command_line_parent_and_usage()
    {
        using var me = Process.GetCurrentProcess();
        var self = ProcessDetails.Snapshot().Single(p => p.Id == me.Id);
        Assert.True(self.ParentId > 0);
        Assert.Contains("testhost", ProcessDetails.CommandLine(me.Id) ?? "", StringComparison.OrdinalIgnoreCase); // as Windows has it, quotes and all
        Assert.NotNull(ProcessDetails.StartedUtc(me.Id));
        var usage = ProcessDetails.Usage(me.Id)!.Value;
        Assert.True(usage.ProcessorTime > TimeSpan.Zero && usage.PeakMemory > 0);
        Assert.Null(ProcessDetails.CommandLine(0)); // the idle process can't be opened
    }

    /// <summary>Without administrator rights: a program's child is seen starting and ending, with its command line and exit code.</summary>
    [Fact]
    public void Without_administrator_rights_children_and_how_they_end_are_seen()
    {
        var info = new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & exit 3") { UseShellExecute = false, CreateNoWindow = true };
        using var cmd = Process.Start(info)!;
        var events = new ConcurrentQueue<BehaviorEvent>();
        using (var poller = new BehaviorPoller(new TrackedProcesses(new[] { cmd.Id }), events.Enqueue))
        {
            poller.Start();
            cmd.WaitForExit(15_000);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until && !events.Any(e => e.Kind == BehaviorKind.ProcessExited && e.ProcessId == cmd.Id)) Thread.Sleep(100);
        }
        // cmd's console host (conhost.exe) is a child too.
        var ping = events.FirstOrDefault(e => e.Kind == BehaviorKind.ProcessStarted && e.Target.EndsWith("PING.EXE", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(ping);
        Assert.Equal(cmd.Id, ping!.Number);
        Assert.EndsWith("PING.EXE", ping.Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("127.0.0.1", ping.Detail);
        Assert.Contains(events, e => e.Kind == BehaviorKind.ProcessExited && e.ProcessId == ping.ProcessId);
        Assert.Equal(3, cmd.ExitCode);
        var rootEnd = events.Single(e => e.Kind == BehaviorKind.ProcessExited && e.ProcessId == cmd.Id);
        Assert.True(rootEnd.Number == 3, string.Join(" | ", events.Select(e => $"{e.Kind} {e.ProcessId} {e.Number} {e.Target}")));
    }

    private sealed class FakeJob(Action<Action<byte[]>> start) : IHelperJob
    {
        public void Start(Action<byte[]> message, Action<HelperError> failed) => start(message);
        public byte[] Hello() => HelperProtocol.EncodeJson(HelperMessage.Hello, new HelperHello(BehaviorJob.Backend, new(), null, new List<int> { 100, 110 }));
        public byte[] Stats(long droppedByHost) => HelperProtocol.EncodeStats(2, droppedByHost);
        public void Dispose() { }
    }

    [Fact]
    public async Task What_the_helper_records_reaches_the_app()
    {
        string name = "SweeplyForWindows-test-" + Guid.NewGuid().ToString("N");
        using var app = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var helper = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accepted = app.WaitForConnectionAsync();
        helper.Connect(5000);
        await accepted;
        var job = new FakeJob(send => send(HelperProtocol.EncodeJson(HelperMessage.Behavior, new List<BehaviorEvent>
        {
            new() { Kind = BehaviorKind.ProcessStarted, ProcessId = 120, Number = 110, Target = @"\Device\HarddiskVolume3\x.exe", Detail = "x.exe /y" },
            new() { Kind = BehaviorKind.ValueSet, ProcessId = 120, Target = @"\REGISTRY\MACHINE\SOFTWARE\Example", Detail = "V", Data = "1" },
        })));
        var running = Task.Run(async () =>
        {
            await HelperHost.RunAsync(helper, _ => (IHelperJob)job, CancellationToken.None);
            helper.Dispose();
        });

        var connection = new CaptureConnection(app);
        Assert.True(await connection.StartAsync(new HelperRequest { Mode = HelperRequest.BehaviorMode, ProcessId = 100 }, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal(BehaviorJob.Backend, connection.Hello!.Backend);
        Assert.Equal(new[] { 100, 110 }, connection.Hello.Processes!);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (connection.Behavior.Count < 2 && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.Equal(new[] { BehaviorKind.ProcessStarted, BehaviorKind.ValueSet }, connection.Behavior.Select(e => e.Kind));
        Assert.Equal(("x.exe /y", 110), (connection.Behavior.First().Detail, connection.Behavior.First().Number));
        await connection.StopAsync(TimeSpan.FromSeconds(5));
        await running;
        Assert.Null(connection.Error);
    }
}
