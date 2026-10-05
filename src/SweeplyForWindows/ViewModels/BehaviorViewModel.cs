using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Sweeply.Core;
using Sweeply.Core.Behavior;
using Sweeply.Core.Capture;
using SweeplyForWindows.Capture;
using SweeplyForWindows.Localization;
using SweeplyForWindows.Platform;

namespace SweeplyForWindows.ViewModels;

/// <summary>A running program to choose on the Behaviour page.</summary>
public sealed class ProcessChoice
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public string Title { get; init; } = "";
    public string? Path { get; init; }
    public ImageSource? Icon { get; init; }
    public string Description => Title.Length > 0 ? $"{Title} · {Id}" : Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public override string ToString() => Name;
}

public sealed record NotableRow(string Kind, string Explanation, string Target, string Program, string Time)
{
    public override string ToString() => $"{Kind}  {Target}  {Program}";
}

public sealed record BehaviorProcessRow(string Name, int Id, string Parent, string CommandLine, string Started, string Ended, string Usage, ImageSource? Icon, double Indent)
{
    public string IdText => Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public override string ToString() => $"{Name}  {Id}  {Ended}";
}

public sealed record BehaviorFileRow(string Path, string What, string Programs, string Time)
{
    public override string ToString() => $"{Path}  {What}  {Programs}";
}

public sealed record BehaviorRegistryRow(string Key, string Value, string What, string Data, string Programs, string Time)
{
    public override string ToString() => $"{Key}  {Value}  {What}  {Data}";
}

public sealed record BehaviorConnectionRow(string Remote, string Host, string Protocol, string Direction, int Count, string Programs, string Time)
{
    public override string ToString() => $"{Remote}  {Host}  {Protocol}  {Programs}";
}

public sealed record BehaviorLookupRow(string Name, string Addresses, int Count, string Programs, string Time)
{
    public override string ToString() => $"{Name}  {Addresses}  {Programs}";
}

/// <summary>
/// The Behaviour page: choose a running program (it is recorded with what it started), or a program to start, then
/// record what it and every process it starts do, until Stop, until they have all ended, or up to the limits. With
/// administrator rights (one prompt) files and the registry too, through the helper; without, what a normal user
/// can watch (<see cref="BehaviorPoller"/>). Only records: nothing is blocked or changed.
/// </summary>
public sealed class BehaviorViewModel : ObservableObject
{
    public const int Overview = 0, ProcessesSection = 1, FilesSection = 2, RegistrySection = 3, NetworkSection = 4;
    public const int MaxRows = 5_000;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(30);
    public const long MaxEvents = 1_000_000;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly ConcurrentQueue<BehaviorEvent> _polled = new();
    private readonly Dictionary<int, ProcessUsage> _usage = new();
    private SystemPaths _paths = new(Array.Empty<(string, string)>(), null);
    private BehaviorReport? _report;
    private CaptureSession? _session;
    private BehaviorPoller? _poller;
    private int _shownVersion = -1, _sectionIndex, _ticks;
    private bool _launchMode, _full = true, _autoStop = true, _isRecording, _isStarting, _isStopping, _sampleMode;
    private string _programPath = "", _arguments = "", _statusText = "", _note = "", _filterText = "", _targetName = "";
    private ProcessChoice? _selectedProcess;
    private DateTime _startedUtc;

    public BehaviorViewModel()
    {
        StartCommand = new RelayCommand(_ => _ = StartAsync(), () => CanStart);
        StopCommand = new RelayCommand(_ => _ = StopAsync(), () => IsRecording && !_isStopping);
        RefreshCommand = new RelayCommand(_ => RefreshProcesses(), () => !IsRecording);
        BrowseCommand = new RelayCommand(_ => { if (PickProgram?.Invoke() is string path) ProgramPath = path; }, () => !IsRecording);
        ExportCommand = new RelayCommand(_ => Export(), () => _report is { EventCount: > 0 } && !IsRecording);
        CopyCommand = new RelayCommand(p => { if (p?.ToString() is { Length: > 0 } text) Copy(text); });
        _timer.Tick += (_, _) => Pump();
    }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CopyCommand { get; }

    /// <summary>Set by the window: choose a program (.exe) to start, save the CSV, the window UAC prompts belong to.</summary>
    public Func<string?>? PickProgram { get; set; }
    public Func<string, string?>? PickSaveFile { get; set; }
    public Func<IntPtr>? OwnerWindow { get; set; }

    public ObservableCollection<ProcessChoice> Processes { get; } = new();

    public ProcessChoice? SelectedProcess
    {
        get => _selectedProcess;
        set { if (SetField(ref _selectedProcess, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    /// <summary>Start a program and record it from its start, rather than one that is running.</summary>
    public bool LaunchMode
    {
        get => _launchMode;
        set { if (SetField(ref _launchMode, value)) { OnPropertyChanged(nameof(RunningMode)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public bool RunningMode
    {
        get => !_launchMode;
        set => LaunchMode = !value;
    }

    public string ProgramPath
    {
        get => _programPath;
        set { if (SetField(ref _programPath, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public string Arguments { get => _arguments; set => SetField(ref _arguments, value); }

    /// <summary>Files and the registry too: the kernel's events, through the helper with administrator rights.</summary>
    public bool FullRecording { get => _full; set => SetField(ref _full, value); }

    public bool AutoStop { get => _autoStop; set => SetField(ref _autoStop, value); }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (!SetField(ref _isRecording, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsIdle => !_isRecording && !_isStarting;

    public bool CanStart => IsIdle && (LaunchMode ? File.Exists(ProgramPath) : SelectedProcess is not null);

    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string Note { get => _note; private set => SetField(ref _note, value); }

    public bool HasReport => _report is not null;

    public int SectionIndex
    {
        get => _sectionIndex;
        set { if (SetField(ref _sectionIndex, value)) ShowSection(); }
    }

    public string FilterText
    {
        get => _filterText;
        set { if (SetField(ref _filterText, value)) ShowSection(); }
    }

    // Overview
    public string ProcessCount { get; private set; } = "0";
    public string FileCount { get; private set; } = "0";
    public string RegistryCount { get; private set; } = "0";
    public string AddressCount { get; private set; } = "0";
    public string NameCount { get; private set; } = "0";
    public ObservableCollection<NotableRow> Notables { get; } = new();
    public bool NoNotables => _report is not null && Notables.Count == 0;

    // Sections, rebuilt when shown and while recording
    public ObservableCollection<BehaviorProcessRow> ProcessRows { get; private set; } = new();
    public ObservableCollection<BehaviorFileRow> FileRows { get; private set; } = new();
    public ObservableCollection<BehaviorRegistryRow> RegistryRows { get; private set; } = new();
    public ObservableCollection<BehaviorConnectionRow> ConnectionRows { get; private set; } = new();
    public ObservableCollection<BehaviorLookupRow> LookupRows { get; private set; } = new();

    /// <summary>"Only the first 5,000 are shown" when a section has more.</summary>
    public string MoreText { get; private set; } = "";

    /// <summary>The page came into view: the running programs, unless recording.</summary>
    public void OnShown()
    {
        if (IsIdle && !_sampleMode) RefreshProcesses();
    }

    /// <summary>The programs this user runs, with a window first, by name.</summary>
    public void RefreshProcesses()
    {
        int keep = SelectedProcess?.Id ?? 0;
        int self = Environment.ProcessId;
        var choices = new List<ProcessChoice>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id is 0 or 4 || p.Id == self) continue;
                string? path = ProcessDetails.ImagePath(p.Id);
                if (path is null || IsHelperOf(path)) continue; // other users' and protected ones can't be opened anyway
                string title;
                try { title = p.MainWindowTitle; }
                catch (InvalidOperationException) { continue; } // ended meanwhile
                choices.Add(new ProcessChoice { Id = p.Id, Name = Path.GetFileName(path), Title = title, Path = path, Icon = ProgramIcons.For(path) });
            }
        }
        Processes.Clear();
        foreach (var c in choices.OrderByDescending(c => c.Title.Length > 0).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id))
            Processes.Add(c);
        SelectedProcess = Processes.FirstOrDefault(c => c.Id == keep);
    }

    /// <summary>This app's own helpers (the same program file) aren't something to record.</summary>
    private static bool IsHelperOf(string path) => string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

    private static readonly Lazy<bool> IsElevated = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    private async Task StartAsync()
    {
        if (!CanStart) return;
        _isStarting = true;
        OnPropertyChanged(nameof(IsIdle));
        CommandManager.InvalidateRequerySuggested();
        Note = "";
        try
        {
            _paths = await Task.Run(SystemPaths.ForThisPc);
            var report = new BehaviorReport(KnownLocations.ForThisPc());
            _usage.Clear();
            _polled.Clear();
            bool launch = LaunchMode;
            string path = ProgramPath.Trim();
            var chosen = SelectedProcess;
            if (!launch && (chosen is null || ProcessDetails.ImagePath(chosen.Id) is null))
            {
                Note = Loc.Instance["beh.error.gone"];
                return;
            }
            _targetName = launch ? Path.GetFileName(path) : chosen!.Name;

            if (FullRecording)
            {
                bool elevate = !IsElevated.Value;
                StatusText = Loc.Instance[elevate ? "cap.waitingAdmin" : "cap.starting"];
                var request = launch
                    ? new HelperRequest { Mode = HelperRequest.BehaviorMode, LaunchedBy = Environment.ProcessId, Program = path }
                    : new HelperRequest { Mode = HelperRequest.BehaviorMode, ProcessId = chosen!.Id };
                var (session, error) = await CaptureSession.StartAsync(request, elevate, null, OwnerWindow?.Invoke() ?? IntPtr.Zero);
                if (session is null)
                {
                    Note = ErrorText(error);
                    StatusText = "";
                    return;
                }
                _session = session;
                if (!launch) AddRoots(report, chosen!.Id, session.Connection.Hello?.Processes ?? new List<int> { chosen.Id });
            }
            else if (!launch)
            {
                var roots = TrackedProcesses.WithDescendants(chosen!.Id, Running()).ToList();
                AddRoots(report, chosen.Id, roots);
                _poller = new BehaviorPoller(new TrackedProcesses(roots), _polled.Enqueue);
            }

            if (launch)
            {
                StatusText = Loc.Instance.Format("beh.launching", _targetName);
                int? id;
                try
                {
                    using var started = Process.Start(new ProcessStartInfo(path, Arguments)
                    {
                        UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                    });
                    id = started?.Id;
                }
                catch (System.ComponentModel.Win32Exception e)
                {
                    Note = Loc.Instance.Format("beh.error.launch", _targetName, e.Message);
                    StopSources();
                    StatusText = "";
                    return;
                }
                if (!FullRecording)
                {
                    // Without the kernel's events the program is known by the id Windows gave it; what it starts in
                    // the first half second may already be missed.
                    if (id is not int pid)
                    {
                        Note = Loc.Instance.Format("beh.error.launch", _targetName, "");
                        StatusText = "";
                        return;
                    }
                    report.AddRoot(pid, path, ProcessDetails.CommandLine(pid) ?? $"\"{path}\" {Arguments}".Trim(), ProcessDetails.StartedUtc(pid) ?? DateTime.UtcNow, Environment.ProcessId);
                    _poller = new BehaviorPoller(new TrackedProcesses(new[] { pid }), _polled.Enqueue);
                }
            }
            _poller?.Start();
            if (_poller is not null && !_poller.SeesLookups) Note = Loc.Instance["beh.note.simpleNoNames"];
            else if (_poller is not null) Note = Loc.Instance["beh.note.simple"];

            _report = report;
            _shownVersion = -1;
            _startedUtc = DateTime.UtcNow;
            OnPropertyChanged(nameof(HasReport));
            IsRecording = true;
            _timer.Start();
            Pump();
        }
        finally
        {
            _isStarting = false;
            OnPropertyChanged(nameof(IsIdle));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static IEnumerable<(int, int, DateTime)> Running() =>
        ProcessDetails.Snapshot().Select(p => (p.Id, p.ParentId, ProcessDetails.StartedUtc(p.Id) ?? DateTime.MinValue));

    /// <summary>The chosen running program and what it had started, as the report's first processes.</summary>
    private static void AddRoots(BehaviorReport report, int chosen, IEnumerable<int> ids)
    {
        var parents = ProcessDetails.Snapshot().ToDictionary(p => p.Id, p => p.ParentId);
        foreach (int id in ids)
        {
            report.AddRoot(id, ProcessDetails.ImagePath(id) ?? "", ProcessDetails.CommandLine(id) ?? "",
                ProcessDetails.StartedUtc(id) ?? DateTime.UtcNow, parents.GetValueOrDefault(id));
            if (id != chosen) report.Processes[id].IsRoot = false;
        }
    }

    /// <summary>Takes in what arrived, keeps the counts and the shown section up to date, and stops when it is time.</summary>
    private void Pump()
    {
        if (_report is not { } report) return;
        int added = 0;
        if (_session is { } session)
        {
            while (added < 20_000 && session.Connection.Behavior.TryDequeue(out var e))
            {
                Add(report, e);
                added++;
            }
        }
        while (added < 20_000 && _polled.TryDequeue(out var polled))
        {
            Add(report, polled);
            added++;
        }
        if (_ticks++ % 4 == 0) ReadUsage(report);

        if (IsRecording && !_isStopping)
        {
            var elapsed = DateTime.UtcNow - _startedUtc;
            bool allEnded = report.Processes.Count > 0 && report.Processes.Values.All(p => p.ExitedUtc is not null);
            if (AutoStop && allEnded) _ = StopAsync(Loc.Instance["beh.note.ended"]);
            else if (elapsed >= MaxDuration || report.EventCount >= MaxEvents)
                _ = StopAsync(Loc.Instance.Format("beh.note.limit", Clock(elapsed), report.EventCount.ToString("N0", Loc.Instance.Culture)));
            else if (_session?.Connection.Completion.IsCompleted == true && _session.Connection.Behavior.IsEmpty)
                _ = StopAsync(ErrorText(_session.Connection.Error ?? new HelperError("HelperLost", "")));
        }
        UpdateStatus();
        if (report.Version != _shownVersion)
        {
            _shownVersion = report.Version;
            ShowOverview();
            if (SectionIndex != Overview && (!IsRecording || _ticks % 2 == 0)) ShowSection();
        }
    }

    private void Add(BehaviorReport report, BehaviorEvent raw)
    {
        var e = _paths.Normalize(raw);
        report.Add(e);
        // The program the app started (the helper knew it by name and by who started it).
        if (e.Kind == BehaviorKind.ProcessStarted && e.Number == Environment.ProcessId && report.Processes.TryGetValue(e.ProcessId, out var root))
            root.IsRoot = true;
    }

    private void ReadUsage(BehaviorReport report)
    {
        foreach (var p in report.Processes.Values)
            if (p.ExitedUtc is null && ProcessDetails.Usage(p.Id) is { } usage) _usage[p.Id] = usage;
    }

    private void UpdateStatus()
    {
        if (_report is not { } report) return;
        var culture = Loc.Instance.Culture;
        string count = report.EventCount.ToString("N0", culture), processes = report.Processes.Count.ToString("N0", culture);
        StatusText = IsRecording
            ? Loc.Instance.Format("beh.status.recording", _targetName, processes, count, Clock(DateTime.UtcNow - _startedUtc))
            : Loc.Instance.Format("beh.status.done", _targetName, processes, count);
    }

    public async Task StopAsync(string? note = null)
    {
        if (!IsRecording || _isStopping) return;
        _isStopping = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            if (_session is { } session)
            {
                await session.StopAsync(); // the helper sends what it still has, then the pipe closes
                long dropped = session.Connection.Dropped;
                BehaviorJob.StopLeftover();
                if (dropped > 0) note = ((note ?? "") + " " + Loc.Instance.Format("beh.note.dropped", dropped.ToString("N0", Loc.Instance.Culture))).Trim();
            }
            _poller?.Dispose();
            _poller = null;
            _timer.Stop();
            Pump(); // the last ones
            if (note is not null) Note = note;
        }
        finally
        {
            _session = null;
            _isStopping = false;
            IsRecording = false;
            UpdateStatus();
            _shownVersion = -1;
            Pump();
            ShowSection();
        }
    }

    private void StopSources()
    {
        _session?.Dispose();
        _session = null;
        if (_poller is not null) BehaviorJob.StopLeftover();
        _poller?.Dispose();
        _poller = null;
    }

    /// <summary>The app is exiting: the helper ends with the pipe.</summary>
    public void StopNow()
    {
        _timer.Stop();
        _session?.Dispose();
        _session = null;
        _poller?.Dispose();
        _poller = null;
    }

    private static string ErrorText(HelperError? error)
    {
        var loc = Loc.Instance;
        if (error is null) return loc["beh.error.HelperLost"];
        string key = "beh.error." + error.Code;
        string text = loc[key];
        if (text == key) text = loc["cap.error." + error.Code];
        if (text == "cap.error." + error.Code) return loc.Format("cap.error.Failed", error.Detail.Length > 0 ? error.Detail : error.Code);
        return string.Format(loc.Culture, text, error.Detail);
    }

    private string ProcessName(int id) =>
        _report?.Processes.TryGetValue(id, out var p) == true ? p.Name : "#" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private string Programs(IEnumerable<int> ids) =>
        string.Join(Loc.Instance["hw.listSeparator"], ids.Select(ProcessName).Distinct());

    private static string Time(DateTime utc) => utc.ToLocalTime().ToString("HH:mm:ss", Loc.Instance.Culture);

    /// <summary>"4:05" or "1:04:05", as the capture page writes how long it has been capturing.</summary>
    private static string Clock(TimeSpan span) => span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");

    private void ShowOverview()
    {
        if (_report is not { } report) return;
        var culture = Loc.Instance.Culture;
        ProcessCount = report.Processes.Count.ToString("N0", culture);
        FileCount = report.Files.Count.ToString("N0", culture);
        RegistryCount = report.Registry.Count.ToString("N0", culture);
        AddressCount = report.Connections.Select(c => c.Remote).Distinct().Count().ToString("N0", culture);
        NameCount = report.Lookups.Count.ToString("N0", culture);
        if (Notables.Count != report.Notables.Count)
        {
            Notables.Clear();
            foreach (var n in report.Notables)
                Notables.Add(new NotableRow(Loc.Instance["beh.notable." + n.Kind], Loc.Instance["beh.notable." + n.Kind + ".why"], n.Target, ProcessName(n.ProcessId), Time(n.TimeUtc)));
        }
        OnPropertyChanged(nameof(ProcessCount));
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(RegistryCount));
        OnPropertyChanged(nameof(AddressCount));
        OnPropertyChanged(nameof(NameCount));
        OnPropertyChanged(nameof(NoNotables));
        CommandManager.InvalidateRequerySuggested();
    }

    private bool Passes(string text) => _filterText.Length == 0 || text.Contains(_filterText.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Rebuilds the rows of the section in view (newest last), up to <see cref="MaxRows"/>.</summary>
    private void ShowSection()
    {
        if (_report is not { } report) return;
        var loc = Loc.Instance;
        var culture = loc.Culture;
        int total = 0;
        switch (SectionIndex)
        {
            case ProcessesSection:
            {
                var rows = new List<BehaviorProcessRow>();
                // As a tree: each process under the one that started it, in the order they started.
                var byParent = report.Processes.Values.ToLookup(p => p.ParentId);
                var visited = new HashSet<int>();
                void Walk(ProcessEntry p, int depth)
                {
                    if (!visited.Add(p.Id)) return;
                    var row = ProcessRow(p, depth);
                    if (Passes(row.ToString() + " " + row.CommandLine)) rows.Add(row);
                    foreach (var child in byParent[p.Id].OrderBy(c => c.StartedUtc)) Walk(child, depth + 1);
                }
                foreach (var top in report.Processes.Values.Where(p => !report.Processes.ContainsKey(p.ParentId) || p.ParentId == p.Id).OrderBy(p => p.StartedUtc))
                    Walk(top, 0);
                total = rows.Count;
                ProcessRows = new ObservableCollection<BehaviorProcessRow>(rows.Take(MaxRows));
                OnPropertyChanged(nameof(ProcessRows));
                break;
            }
            case FilesSection:
            {
                var rows = report.Files.OrderBy(f => f.FirstUtc)
                    .Select(f => new BehaviorFileRow(f.Path, FileWhat(f), Programs(f.Processes), Time(f.LastUtc)))
                    .Where(r => Passes(r.ToString())).ToList();
                total = rows.Count;
                FileRows = new ObservableCollection<BehaviorFileRow>(rows.Take(MaxRows));
                OnPropertyChanged(nameof(FileRows));
                break;
            }
            case RegistrySection:
            {
                var rows = report.Registry.OrderBy(r => r.FirstUtc)
                    .Select(r => new BehaviorRegistryRow(r.Key, r.Value is null ? "" : r.Value.Length == 0 ? loc["beh.defaultValue"] : r.Value,
                        RegistryWhat(r), r.Data, Programs(r.Processes), Time(r.LastUtc)))
                    .Where(r => Passes(r.ToString())).ToList();
                total = rows.Count;
                RegistryRows = new ObservableCollection<BehaviorRegistryRow>(rows.Take(MaxRows));
                OnPropertyChanged(nameof(RegistryRows));
                break;
            }
            case NetworkSection:
            {
                var connections = report.Connections.OrderBy(c => c.FirstUtc)
                    .Select(c => new BehaviorConnectionRow(c.Remote, c.Host, c.Protocol, loc[c.Inbound ? "beh.dir.in" : "beh.dir.out"], c.Count, Programs(c.Processes), Time(c.FirstUtc)))
                    .Where(r => Passes(r.ToString())).ToList();
                var lookups = report.Lookups.OrderBy(l => l.FirstUtc)
                    .Select(l => new BehaviorLookupRow(l.Name, string.Join(", ", l.Addresses), l.Count, Programs(l.Processes), Time(l.FirstUtc)))
                    .Where(r => Passes(r.ToString())).ToList();
                total = Math.Max(connections.Count, lookups.Count);
                ConnectionRows = new ObservableCollection<BehaviorConnectionRow>(connections.Take(MaxRows));
                LookupRows = new ObservableCollection<BehaviorLookupRow>(lookups.Take(MaxRows));
                OnPropertyChanged(nameof(ConnectionRows));
                OnPropertyChanged(nameof(LookupRows));
                break;
            }
        }
        MoreText = total > MaxRows ? loc.Format("beh.more", MaxRows.ToString("N0", culture)) : "";
        OnPropertyChanged(nameof(MoreText));
    }

    private BehaviorProcessRow ProcessRow(ProcessEntry p, int depth)
    {
        var loc = Loc.Instance;
        var culture = loc.Culture;
        string ended = p.ExitedUtc is { } exited ? loc.Format("beh.exited", Time(exited), p.ExitCode ?? 0) : loc["beh.running"];
        string usage = _usage.TryGetValue(p.Id, out var u)
            ? loc.Format("beh.usage", SizeFormatter.Format(u.ReadBytes, culture), SizeFormatter.Format(u.WriteBytes, culture),
                u.ProcessorTime.TotalSeconds.ToString("0.0", culture), SizeFormatter.Format(u.PeakMemory, culture))
            : "";
        string parent = _report!.Processes.TryGetValue(p.ParentId, out var parentEntry) ? parentEntry.Name : "";
        return new BehaviorProcessRow(p.Name, p.Id, parent, p.CommandLine, Time(p.StartedUtc), ended, usage, ProgramIcons.For(p.Path.Length > 0 ? p.Path : null), depth * 18);
    }

    private static string FileWhat(FileEntry f)
    {
        var loc = Loc.Instance;
        var parts = new List<string>();
        if (f.Created) parts.Add(loc["beh.what.created"]);
        if (f.Writes > 0) parts.Add(loc.Format("beh.what.written", SizeFormatter.Format(f.Bytes, loc.Culture)));
        if (f.Renamed) parts.Add(loc["beh.what.renamed"]);
        if (f.Deleted) parts.Add(loc["beh.what.deleted"]);
        return string.Join(loc["hw.listSeparator"], parts);
    }

    private static string RegistryWhat(RegistryEntry r)
    {
        var loc = Loc.Instance;
        var parts = new List<string>();
        if (r.Created) parts.Add(loc["beh.what.keyCreated"]);
        if (r.Sets > 0) parts.Add(r.Sets == 1 ? loc["beh.what.set"] : loc.Format("beh.what.setTimes", r.Sets));
        if (r.Deleted) parts.Add(loc["beh.what.deleted"]);
        return string.Join(loc["hw.listSeparator"], parts);
    }

    /// <summary>Every event as a CSV file (UTF-8, which Excel opens): time, process, what, on what, more, size.</summary>
    private void Export()
    {
        if (_report is not { } report) return;
        string suggested = $"behavior-{Path.GetFileNameWithoutExtension(_targetName)}-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        if (PickSaveFile?.Invoke(suggested) is not string path) return;
        var loc = Loc.Instance;
        var text = new StringBuilder();
        text.AppendLine(loc["beh.csv.header"]);
        foreach (var e in report.Events)
        {
            string detail = e.Kind == BehaviorKind.ValueSet ? $"{e.Detail} = {e.Data}" : e.Detail;
            text.AppendLine(string.Join(",", new[]
            {
                e.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
                e.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), ProcessName(e.ProcessId), loc["beh.kind." + e.Kind],
                e.Target, detail, e.Size > 0 ? e.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
            }.Select(Csv)));
        }
        try
        {
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
            Note = loc.Format("beh.exported", path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Note = loc.Format("beh.error.export", e.Message);
        }
    }

    private static string Csv(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";

    private static void Copy(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { } // the clipboard is busy
    }

    public void Relocalize()
    {
        if (_report is null) return;
        Notables.Clear();
        _shownVersion = -1;
        UpdateStatus();
        ShowOverview();
        ShowSection();
    }

    /// <summary>Made-up recording for screenshots: an installer, with documentation addresses and no real paths.</summary>
    public void LoadSample()
    {
        _sampleMode = true;
        var t0 = new DateTime(2026, 10, 5, 2, 0, 0, DateTimeKind.Utc);
        var places = new KnownLocations(@"C:\Windows", new[] { @"C:\Program Files", @"C:\Program Files (x86)" },
            new[] { @"C:\Users\Alex\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup" }, @"C:\Windows\System32\Tasks", @"C:\Windows\System32\drivers\etc\hosts");
        var report = new BehaviorReport(places);
        report.AddRoot(4120, @"C:\Users\Alex\Downloads\ExampleSetup.exe", @"""C:\Users\Alex\Downloads\ExampleSetup.exe"" /silent", t0);
        BehaviorEvent E(BehaviorKind kind, int pid, string target, double s, string detail = "", string data = "", int number = 0, long size = 0) =>
            new() { Kind = kind, ProcessId = pid, Target = target, Detail = detail, Data = data, Number = number, Size = size, TimeUtc = t0.AddSeconds(s) };
        foreach (var e in new[]
        {
            E(BehaviorKind.DnsLookup, 4120, "download.example.com", 0.4, "type:  5 cdn.example.net;::ffff:203.0.113.40;"),
            E(BehaviorKind.Connected, 4120, "203.0.113.40:443", 0.5, "TCP"),
            E(BehaviorKind.FileCreated, 4120, @"C:\Users\Alex\AppData\Local\Temp\ExampleSetup\payload.msi", 1.2),
            E(BehaviorKind.FileWritten, 4120, @"C:\Users\Alex\AppData\Local\Temp\ExampleSetup\payload.msi", 1.3, size: 48_200_000),
            E(BehaviorKind.ProcessStarted, 5236, @"C:\Windows\System32\msiexec.exe", 2.0, @"msiexec.exe /i ""C:\Users\Alex\AppData\Local\Temp\ExampleSetup\payload.msi"" /qn", number: 4120),
            E(BehaviorKind.FileCreated, 5236, @"C:\Program Files\Example\Example.exe", 3.1),
            E(BehaviorKind.FileWritten, 5236, @"C:\Program Files\Example\Example.exe", 3.2, size: 21_700_000),
            E(BehaviorKind.FileWritten, 5236, @"C:\Program Files\Example\settings.json", 3.4, size: 2_100),
            E(BehaviorKind.KeyCreated, 5236, @"HKLM\SOFTWARE\Example", 3.6),
            E(BehaviorKind.ValueSet, 5236, @"HKLM\SOFTWARE\Example", 3.7, "InstallDir", @"C:\Program Files\Example\"),
            E(BehaviorKind.ValueSet, 5236, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", 3.9, "ExampleUpdater", @"""C:\Program Files\Example\Example.exe"" --background"),
            E(BehaviorKind.ProcessExited, 5236, "", 4.5),
            E(BehaviorKind.FileDeleted, 4120, @"C:\Users\Alex\AppData\Local\Temp\ExampleSetup\payload.msi", 4.8),
            E(BehaviorKind.ProcessExited, 4120, "", 5.0),
        })
            report.Add(e);
        _report = report;
        _targetName = "ExampleSetup.exe";
        _usage[4120] = new ProcessUsage(52_000_000, 48_400_000, TimeSpan.FromSeconds(1.8), 64_000_000);
        _usage[5236] = new ProcessUsage(49_100_000, 23_900_000, TimeSpan.FromSeconds(3.2), 41_000_000);
        Processes.Add(new ProcessChoice { Id = 4120, Name = "ExampleSetup.exe", Title = "Example Setup" });
        SelectedProcess = Processes[0];
        OnPropertyChanged(nameof(HasReport));
        UpdateStatus();
        ShowOverview();
        ShowSection();
    }
}
