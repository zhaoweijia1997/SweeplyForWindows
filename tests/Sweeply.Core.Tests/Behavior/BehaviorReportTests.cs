using Sweeply.Core.Behavior;

namespace Sweeply.Core.Tests.Behavior;

public class BehaviorReportTests
{
    private static readonly DateTime T0 = new(2026, 10, 5, 2, 0, 0, DateTimeKind.Utc);
    private const string Sid = "S-1-5-21-1000-2000-3000-1001";

    private static readonly KnownLocations Places = new(
        @"C:\Windows", new[] { @"C:\Program Files", @"C:\Program Files (x86)" },
        new[] { @"C:\Users\Test\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp" },
        @"C:\Windows\System32\Tasks", @"C:\Windows\System32\drivers\etc\hosts");

    private static BehaviorEvent E(BehaviorKind kind, string target, int pid = 100, string detail = "", string data = "", int number = 0, long size = 0, double seconds = 0) =>
        new() { Kind = kind, Target = target, ProcessId = pid, Detail = detail, Data = data, Number = number, Size = size, TimeUtc = T0.AddSeconds(seconds) };

    [Fact]
    public void Processes_are_followed_from_the_chosen_one_to_what_it_starts()
    {
        var tracked = new TrackedProcesses(new[] { 100 });
        Assert.True(tracked.Started(200, 100));   // a child
        Assert.True(tracked.Started(300, 200));   // a grandchild
        Assert.False(tracked.Started(400, 999));  // someone else's
        Assert.True(tracked.Contains(300));
        Assert.True(tracked.Exited(200));
        Assert.False(tracked.Started(500, 200)); // 200 ended: its id may already belong to an unrelated process
        Assert.Equal((2, 2), (tracked.Seen, tracked.Running));
    }

    [Fact]
    public void A_program_about_to_start_is_known_by_who_starts_it_and_its_file_name()
    {
        var tracked = new TrackedProcesses(Array.Empty<int>());
        tracked.Await(50, "setup.exe");
        Assert.False(tracked.Started(60, 50, @"\Device\HarddiskVolume3\Windows\System32\conhost.exe"));
        Assert.False(tracked.Started(70, 99, @"\Device\HarddiskVolume3\Tools\setup.exe")); // started by someone else
        Assert.True(tracked.Started(80, 50, @"\Device\HarddiskVolume3\Tools\SETUP.EXE"));
        Assert.Equal(80, tracked.Arrived);
        Assert.True(tracked.Started(90, 80, @"\Device\HarddiskVolume3\Tools\helper.exe"));
        Assert.False(tracked.Started(100, 50, @"\Device\HarddiskVolume3\Tools\setup.exe")); // only the first one
        Assert.Equal(2, tracked.Running);
    }

    [Fact]
    public void A_running_program_is_taken_with_what_it_started_after_it()
    {
        var processes = new[]
        {
            (Id: 100, Parent: 50, StartedUtc: T0),
            (Id: 110, Parent: 100, StartedUtc: T0.AddSeconds(1)),
            (Id: 120, Parent: 110, StartedUtc: T0.AddSeconds(2)),
            (Id: 130, Parent: 100, StartedUtc: T0.AddSeconds(-60)), // older than 100: its parent was an earlier process with the same id
            (Id: 140, Parent: 999, StartedUtc: T0),
        };
        Assert.Equal(new[] { 100, 110, 120 }, TrackedProcesses.WithDescendants(100, processes));
    }

    [Fact]
    public void Kernel_paths_become_the_ones_people_know()
    {
        var paths = new SystemPaths(new[] { (@"\Device\HarddiskVolume3", "C:"), (@"\Device\HarddiskVolume11", "D:") }, Sid);
        Assert.Equal(@"C:\Users\Test\a.txt", paths.File(@"\Device\HarddiskVolume3\Users\Test\a.txt"));
        Assert.Equal(@"D:\x", paths.File(@"\Device\HarddiskVolume11\x")); // not taken for volume 1 followed by "1\x"
        Assert.Equal(@"C:\Windows\x.dll", paths.File(@"\??\C:\Windows\x.dll"));
        Assert.Equal(@"\\server\share\f", paths.File(@"\Device\Mup\server\share\f"));
        Assert.Equal(@"\Device\Unknown\f", paths.File(@"\Device\Unknown\f"));

        Assert.Equal(@"HKLM\SOFTWARE\Example", paths.Registry(@"\REGISTRY\MACHINE\SOFTWARE\Example"));
        Assert.Equal(@"HKCU\Software\Example", paths.Registry($@"\REGISTRY\USER\{Sid}\Software\Example"));
        Assert.Equal(@"HKCU\Software\Classes\.txt", paths.Registry($@"\REGISTRY\USER\{Sid}_Classes\.txt"));
        Assert.Equal(@"HKU\S-1-5-18\Software", paths.Registry(@"\Registry\User\S-1-5-18\Software"));
        Assert.Equal("HKCU", paths.Registry($@"\REGISTRY\USER\{Sid}"));

        var e = paths.Normalize(E(BehaviorKind.ValueSet, $@"\REGISTRY\USER\{Sid}\Software\Microsoft\Windows\CurrentVersion\Run", detail: "Updater"));
        Assert.Equal(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", e.Target);
        Assert.Equal("Updater", e.Detail);
    }

    [Fact]
    public void Files_are_added_up_and_what_matters_is_noted()
    {
        var report = new BehaviorReport(Places);
        report.Add(E(BehaviorKind.FileCreated, @"C:\Users\Test\AppData\Local\Example\log.txt"));
        report.Add(E(BehaviorKind.FileWritten, @"C:\Users\Test\AppData\Local\Example\log.txt", size: 100));
        report.Add(E(BehaviorKind.FileWritten, @"C:\Users\Test\AppData\Local\Example\LOG.txt", size: 50, pid: 200));
        report.Add(E(BehaviorKind.FileCreated, @"C:\Users\Test\AppData\Local\Example\update.exe"));
        report.Add(E(BehaviorKind.FileCreated, Places.StartupFolders[0] + @"\Example.lnk"));
        report.Add(E(BehaviorKind.FileWritten, @"C:\Windows\System32\drivers\etc\hosts"));
        report.Add(E(BehaviorKind.FileWritten, @"C:\Windows\Temp\setup.log"));
        report.Add(E(BehaviorKind.FileDeleted, @"C:\Program Files\Example\old.txt"));

        var log = report.Files.Single(f => f.Path.EndsWith("log.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Equal((true, 2, 150L, 2), (log.Created, log.Writes, log.Bytes, log.Processes.Count));
        Assert.Equal(new[]
        {
            (NotableKind.Executable, @"C:\Users\Test\AppData\Local\Example\update.exe"),
            (NotableKind.StartupFolder, Places.StartupFolders[0] + @"\Example.lnk"),
            (NotableKind.Hosts, @"C:\Windows\System32\drivers\etc\hosts"),
            (NotableKind.SystemFolder, @"C:\Program Files\Example\old.txt"),
        }, report.Notables.Select(n => (n.Kind, n.Target)));
        Assert.Equal(8, report.EventCount);
    }

    [Fact]
    public void Registry_changes_that_make_programs_start_by_themselves_are_noted()
    {
        Assert.Equal(NotableKind.Autostart, BehaviorReport.RegistryNotable(BehaviorKind.ValueSet, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Updater"));
        Assert.Equal(NotableKind.Autostart, BehaviorReport.RegistryNotable(BehaviorKind.ValueSet, @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", "x"));
        Assert.Null(BehaviorReport.RegistryNotable(BehaviorKind.ValueDeleted, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Updater")); // removing one isn't
        Assert.Equal(NotableKind.Autostart, BehaviorReport.RegistryNotable(BehaviorKind.ValueSet, @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Userinit"));
        Assert.Null(BehaviorReport.RegistryNotable(BehaviorKind.ValueSet, @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "LastUsedUsername"));
        Assert.Equal(NotableKind.Debugger, BehaviorReport.RegistryNotable(BehaviorKind.ValueSet, @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\notepad.exe", "Debugger"));
        Assert.Equal(NotableKind.Service, BehaviorReport.RegistryNotable(BehaviorKind.KeyCreated, @"HKLM\SYSTEM\CurrentControlSet\Services\ExampleSvc", null));
        Assert.Equal(NotableKind.Service, BehaviorReport.RegistryNotable(BehaviorKind.ValueSet, @"HKLM\SYSTEM\ControlSet001\Services\ExampleSvc", "ImagePath"));
        Assert.Null(BehaviorReport.RegistryNotable(BehaviorKind.ValueSet, @"HKLM\SYSTEM\CurrentControlSet\Services\ExampleSvc\Parameters", "Port"));
        Assert.Equal(NotableKind.ScheduledTask, BehaviorReport.RegistryNotable(BehaviorKind.KeyCreated, @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\Example", null));
        Assert.Null(BehaviorReport.RegistryNotable(BehaviorKind.ValueSet, @"HKCU\Software\Example\Settings", "Theme"));

        var report = new BehaviorReport(Places);
        report.Add(E(BehaviorKind.ValueSet, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", detail: "Updater", data: @"C:\x\update.exe /silent"));
        report.Add(E(BehaviorKind.ValueSet, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", detail: "Updater", data: @"C:\x\update.exe"));
        report.Add(E(BehaviorKind.KeyCreated, @"HKCU\Software\Example"));
        var run = report.Registry.Single(r => r.Value == "Updater");
        Assert.Equal((2, @"C:\x\update.exe"), (run.Sets, run.Data));
        Assert.Single(report.Notables); // the same value noted once
        Assert.Equal(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Updater", report.Notables[0].Target);
        Assert.Contains(report.Registry, r => r.Key == @"HKCU\Software\Example" && r.Value is null && r.Created);
    }

    [Fact]
    public void Connections_get_the_names_that_were_looked_up_for_them()
    {
        var report = new BehaviorReport(Places);
        report.Add(E(BehaviorKind.Connected, "203.0.113.10:443", detail: "TCP"));
        report.Add(E(BehaviorKind.DnsLookup, "www.example.com", detail: "type:  5 cdn.example.net;::ffff:203.0.113.10;::ffff:203.0.113.11;"));
        report.Add(E(BehaviorKind.Connected, "203.0.113.11:443", detail: "TCP"));
        report.Add(E(BehaviorKind.Connected, "203.0.113.11:443", detail: "TCP", pid: 200));
        report.Add(E(BehaviorKind.Connected, "[2001:db8::1]:53", detail: "UDP"));

        Assert.Equal(new[] { "203.0.113.10", "203.0.113.11" }, report.Lookups.Single().Addresses.Order());
        var connections = report.Connections.ToDictionary(c => c.Remote);
        Assert.Equal("www.example.com", connections["203.0.113.10:443"].Host); // named when the answer came later
        Assert.Equal(("www.example.com", 2, 2), (connections["203.0.113.11:443"].Host, connections["203.0.113.11:443"].Count, connections["203.0.113.11:443"].Processes.Count));
        Assert.Equal("", connections["[2001:db8::1]:53"].Host);
        Assert.Equal(new[] { "2001:db8::1" }, BehaviorReport.LookupAddresses("2001:db8::1;").ToArray());
    }

    [Fact]
    public void Processes_keep_their_command_line_and_how_they_ended()
    {
        var report = new BehaviorReport(Places);
        report.AddRoot(100, @"C:\Tools\setup.exe", "setup.exe /quiet", T0);
        report.Add(E(BehaviorKind.ProcessStarted, @"C:\Windows\System32\cmd.exe", pid: 200, detail: "cmd /c echo", number: 100, seconds: 1));
        report.Add(E(BehaviorKind.ProcessExited, @"C:\Windows\System32\cmd.exe", pid: 200, number: 1, seconds: 2));
        var child = report.Processes[200];
        Assert.Equal(("cmd.exe", 100, "cmd /c echo", 1, false), (child.Name, child.ParentId, child.CommandLine, child.ExitCode, child.IsRoot));
        Assert.True(report.Processes[100].IsRoot);
        Assert.Equal(T0.AddSeconds(2), child.ExitedUtc);
    }
}
