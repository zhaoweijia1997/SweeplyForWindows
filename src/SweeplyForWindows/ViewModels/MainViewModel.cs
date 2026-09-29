using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;
using Sweeply.Core;
using SweeplyForWindows.Localization;
using SweeplyForWindows.Monitor;
using SweeplyForWindows.Platform;

namespace SweeplyForWindows.ViewModels;

public sealed class GroupViewModel : ObservableObject
{
    public GroupViewModel(CategoryGroup group) => Group = group;
    public CategoryGroup Group { get; }
    public string Name => Loc.Instance[$"clean.group.{Group}"];
    public ObservableCollection<CategoryViewModel> Categories { get; } = new();

    /// <summary>Hidden when none of its apps are on this PC.</summary>
    public bool IsShown => Categories.Any(c => c.IsShown);

    public void Add(CategoryViewModel category)
    {
        Categories.Add(category);
        category.PropertyChanged += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(CategoryViewModel.IsShown))
                OnPropertyChanged(nameof(IsShown));
        };
    }

    public void Relocalize() => OnPropertyChanged(nameof(Name));
}

public sealed class MainViewModel : ObservableObject
{
    private readonly Settings _settings;
    private int _pageIndex;
    private bool _isBusy;
    private bool _hasScanned;
    private string? _statusMessage;
    private double _progress;
    private string _progressKey = "clean.scanProgress";
    private int _progressDone;
    private int _progressTotal;
    private TaskbarItemProgressState _taskbarState = TaskbarItemProgressState.None;
    private bool _isReviewing;
    private bool _confirmReady;
    private string? _reviewNote;
    private readonly List<ReviewRow> _reviewItems = new();
    private readonly DispatcherTimer _confirmDelay = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CleanHistory _history;
    private CleanRecord? _lastRecord;
    private string? _historyNote;

    /// <summary>Where past cleans are kept: a file of its own, written by the app only.</summary>
    public static string HistoryFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SweeplyForWindows", "history.json");

    /// <param name="history">Past cleans; null loads them from <see cref="HistoryFile"/>.</param>
    public MainViewModel(KnownPaths paths, Settings settings, CleanHistory? history = null)
    {
        _settings = settings;
        _history = history ?? CleanHistory.Load(HistoryFile);
        foreach (var category in CategoryCatalog.Create(paths))
        {
            var group = Groups.FirstOrDefault(g => g.Group == category.Group);
            if (group is null) Groups.Add(group = new GroupViewModel(category.Group));
            CategoryViewModel? vm = null;
            vm = new CategoryViewModel(category, OnSelectionChanged, path => _ = ExcludeItemAsync(vm!, path));
            group.Add(vm);
        }
        foreach (string path in _settings.ExcludedFolders) ExcludedPaths.Add(path);

        ScanCommand = new RelayCommand(async _ => await ScanAsync(), () => !IsBusy && !IsReviewing);
        CleanCommand = new RelayCommand(_ => OpenReview(), () => CanClean);
        ConfirmCleanCommand = new RelayCommand(async _ => await ConfirmCleanAsync(), () => CanConfirmReview);
        CancelReviewCommand = new RelayCommand(_ => CloseReview());
        ExportReviewCommand = new RelayCommand(_ => ExportReview(), () => IsReviewing);
        UndoLastCommand = new RelayCommand(async _ => { if (_lastRecord is not null) await UndoAsync(_lastRecord); }, () => CanUndoLast);
        RefreshHistory();
        AddExclusionCommand = new RelayCommand(_ =>
        {
            if (PickFolder?.Invoke() is string folder) _ = ChangeExclusionsAsync(() => AddExclusion(folder), null);
        });
        RemoveExclusionCommand = new RelayCommand(p =>
        {
            if (p is string path) _ = ChangeExclusionsAsync(() => ExcludedPaths.Remove(path), null);
        });
        OpenUrlCommand = new RelayCommand(p => OpenUrl(p as string));
        Loc.Instance.LanguageChanged += Relocalize;
        _confirmDelay.Tick += (_, _) =>
        {
            _confirmDelay.Stop();
            _confirmReady = true;
            OnPropertyChanged(nameof(CanConfirmReview));
            CommandManager.InvalidateRequerySuggested();
        };
    }

    public ObservableCollection<GroupViewModel> Groups { get; } = new();
    public IEnumerable<CategoryViewModel> AllCategories => Groups.SelectMany(g => g.Categories);

    public ICommand ScanCommand { get; }

    /// <summary>Opens the list of everything that would be moved; nothing moves yet.</summary>
    public ICommand CleanCommand { get; }
    public ICommand ConfirmCleanCommand { get; }
    public ICommand CancelReviewCommand { get; }
    public ICommand ExportReviewCommand { get; }
    public ICommand UndoLastCommand { get; }
    public ICommand OpenUrlCommand { get; }

    /// <summary>The clean that just finished can still be undone (shown next to its result).</summary>
    public bool CanUndoLast => !IsBusy && _lastRecord is { State: not UndoState.Undone };

    /// <summary>Settings page: the last few cleans, newest first.</summary>
    public ObservableCollection<HistoryRowViewModel> HistoryRows { get; } = new();

    public bool HasHistory => HistoryRows.Count > 0;

    /// <summary>Settings page: the result of the last undo.</summary>
    public string? HistoryNote
    {
        get => _historyNote;
        private set => SetField(ref _historyNote, value);
    }

    /// <summary>Asks where to save the list (suggested file name); null when cancelled. Set by the window.</summary>
    public Func<string, string?>? PickSaveFile { get; set; }

    /// <summary>Asks for a folder to add to the "Never clean" list; null when cancelled. Set by the window.</summary>
    public Func<string?>? PickFolder { get; set; }

    /// <summary>
    /// Settings page: the "Never clean" list. Scans leave these out, and the check right before
    /// moving refuses them too, so adding one while a list is already showing is still safe.
    /// </summary>
    public ObservableCollection<string> ExcludedPaths { get; } = new();

    public bool HasExclusions => ExcludedPaths.Count > 0;

    public ICommand AddExclusionCommand { get; }
    public ICommand RemoveExclusionCommand { get; }

    /// <summary>
    /// The list of what would be moved is showing. It is the only way to start cleaning: the user sees
    /// every item, can untick any of them, and must press the confirm button (not the default button,
    /// and only after a second, so stray key presses or clicks cannot start a clean).
    /// </summary>
    public bool IsReviewing
    {
        get => _isReviewing;
        private set
        {
            if (!SetField(ref _isReviewing, value)) return;
            OnPropertyChanged(nameof(CanClean));
            OnPropertyChanged(nameof(CanConfirmReview));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public ObservableCollection<ReviewRow> ReviewRows { get; } = new();

    private IEnumerable<ReviewRow> ReviewSelected => _reviewItems.Where(r => r.Item!.IsSelected);

    public string ReviewSummary => Loc.Instance.Format("review.summary", ReviewSelected.Count(),
        SizeFormatter.Format(ReviewSelected.Sum(r => r.Item!.Item.Bytes), Loc.Instance.Culture));

    public bool CanConfirmReview => IsReviewing && _confirmReady && !IsBusy && ReviewSelected.Any();

    /// <summary>Shown under the summary, e.g. where the list was saved.</summary>
    public string? ReviewNote
    {
        get => _reviewNote;
        private set => SetField(ref _reviewNote, value);
    }

    /// <summary>Window handle that owns the Recycle Bin warnings. Set by the window.</summary>
    public Func<IntPtr>? OwnerHandle { get; set; }

    public int PageIndex
    {
        get => _pageIndex;
        set => SetField(ref _pageIndex, value);
    }

    public IReadOnlyList<LanguageOption> Languages => Loc.Languages;

    public LanguageOption SelectedLanguage
    {
        get => Loc.Languages.First(l => l.Code == Loc.Instance.Language);
        set
        {
            if (value is null || value.Code == Loc.Instance.Language) return;
            Loc.Instance.SetLanguage(value.Code);
            _settings.Language = value.Code;
            _settings.Save();
        }
    }

    /// <summary>Settings page: start in the notification area when the user signs in.</summary>
    public bool StartWithWindows
    {
        get => Autostart.IsEnabled;
        set
        {
            Autostart.Set(value);
            OnPropertyChanged();
        }
    }

    /// <summary>Settings page: closing the window keeps the app in the notification area.</summary>
    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set
        {
            if (_settings.CloseToTray == value) return;
            _settings.CloseToTray = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Raised when a Settings-page choice about the activity monitor changed.</summary>
    public event Action? MonitorSettingsChanged;

    public IReadOnlyList<TrayDisplayOption> TrayDisplayOptions { get; } =
        Enum.GetValues<TrayDisplay>().Select(d => new TrayDisplayOption(d)).ToList();

    public TrayDisplay SelectedTrayDisplay
    {
        get => _settings.TrayDisplay;
        set => SetMonitorSetting(_settings.TrayDisplay, value, v => _settings.TrayDisplay = v);
    }

    public bool TrayToolTipStats
    {
        get => _settings.TrayToolTipStats;
        set => SetMonitorSetting(_settings.TrayToolTipStats, value, v => _settings.TrayToolTipStats = v);
    }

    /// <summary>Also switched from the icon's menu and the bar's own menu.</summary>
    public bool ShowMonitorBar
    {
        get => _settings.ShowMonitorBar;
        set => SetMonitorSetting(_settings.ShowMonitorBar, value, v => _settings.ShowMonitorBar = v);
    }

    private void SetMonitorSetting<T>(T current, T value, Action<T> store, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        store(value);
        _settings.Save();
        OnPropertyChanged(name);
        MonitorSettingsChanged?.Invoke();
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanClean));
                OnPropertyChanged(nameof(CanUndoLast));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasScanned
    {
        get => _hasScanned;
        private set
        {
            if (SetField(ref _hasScanned, value)) OnPropertyChanged(nameof(ScanButtonText));
        }
    }

    public string ScanButtonText => Loc.Instance[HasScanned ? "clean.rescan" : "clean.scan"];

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public long SelectedBytes => AllCategories.Sum(c => c.SelectedBytes);
    public int SelectedCount => AllCategories.Sum(c => c.SelectedItems.Count());
    public long FoundBytes => AllCategories.Sum(c => c.FoundBytes);

    public string SelectedText => Loc.Instance.Format("clean.selected", SizeFormatter.Format(SelectedBytes, Loc.Instance.Culture));
    public string FoundText => Loc.Instance.Format("clean.found", SizeFormatter.Format(FoundBytes, Loc.Instance.Culture));

    public bool CanClean => !IsBusy && !IsReviewing && SelectedCount > 0;

    /// <summary>0..1, shown in the window footer and on the taskbar button.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetField(ref _progress, value);
    }

    public string ProgressText => Loc.Instance.Format(_progressKey, _progressDone, _progressTotal);

    public TaskbarItemProgressState TaskbarState
    {
        get => _taskbarState;
        private set => SetField(ref _taskbarState, value);
    }

    public string VersionText
    {
        get
        {
            string v = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            return Loc.Instance.Format("about.version", v.Split('+')[0]);
        }
    }

    public Task ScanAsync(bool keepMessage = false) => ScanAsync(AllCategories.ToList(), keepMessage);

    private async Task ScanAsync(List<CategoryViewModel> categories, bool keepMessage)
    {
        if (IsBusy) return;
        IsBusy = true;
        if (!keepMessage) StatusMessage = null;
        var excluded = _settings.ExcludedFolders.ToArray();
        foreach (var c in categories) c.IsScanning = true;
        TaskbarState = TaskbarItemProgressState.Normal;
        ReportProgress("clean.scanProgress", 0, categories.Count);

        for (int i = 0; i < categories.Count; i++)
        {
            var c = categories[i];
            var scan = await Task.Run(() => Scanner.Scan(c.Category, DateTime.UtcNow, null, excluded));
            c.SetScan(scan);
            ReportProgress("clean.scanProgress", i + 1, categories.Count);
        }

        HasScanned = true;
        TaskbarState = TaskbarItemProgressState.None;
        IsBusy = false;
    }

    /// <summary>Shows every selected item, grouped by category. Nothing is moved here.</summary>
    public void OpenReview()
    {
        if (IsBusy) return;
        ReviewRows.Clear();
        _reviewItems.Clear();
        var culture = Loc.Instance.Culture;
        foreach (var c in AllCategories)
        {
            var items = c.SelectedItemViewModels.ToList();
            if (items.Count == 0) continue;
            ReviewRows.Add(new ReviewRow(Loc.Instance.Format("review.category", c.Name, items.Count,
                SizeFormatter.Format(items.Sum(i => i.Item.Bytes), culture))));
            foreach (var item in items)
            {
                var row = new ReviewRow(item, c.Category);
                ReviewRows.Add(row);
                _reviewItems.Add(row);
            }
        }
        if (_reviewItems.Count == 0) return;
        ReviewNote = null;
        _confirmReady = false;
        _confirmDelay.Stop();
        _confirmDelay.Start();
        IsReviewing = true;
        OnPropertyChanged(nameof(ReviewSummary));
    }

    private void CloseReview()
    {
        _confirmDelay.Stop();
        _confirmReady = false;
        IsReviewing = false;
        ReviewRows.Clear();
        _reviewItems.Clear();
    }

    private async Task ConfirmCleanAsync()
    {
        if (!CanConfirmReview) return;
        // Exactly what the list showed, minus anything unticked there.
        var selection = ReviewSelected.Select(r => (r.Item!.Item, r.Category!)).ToList();
        CloseReview();
        await CleanAsync(selection);
    }

    private void ExportReview()
    {
        string? path = PickSaveFile?.Invoke($"SweeplyForWindows-{DateTime.Now:yyyyMMdd-HHmm}.txt");
        if (path is null) return;
        var culture = Loc.Instance.Culture;
        var text = new System.Text.StringBuilder()
            .AppendLine(Loc.Instance["review.title"])
            .AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm", culture))
            .AppendLine(ReviewSummary);
        foreach (var row in ReviewRows)
        {
            if (row.IsHeading) text.AppendLine().AppendLine(row.Heading);
            else if (row.Item!.IsSelected) text.Append(row.Item.Path).Append('\t').AppendLine(row.Item.SizeText);
        }
        try
        {
            System.IO.File.WriteAllText(path, text.ToString(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            ReviewNote = Loc.Instance.Format("review.exported", path);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            ReviewNote = e.Message;
        }
    }

    private async Task CleanAsync(List<(CleanupItem Item, CleanupCategory Category)> selection)
    {
        if (selection.Count == 0) return;
        IsBusy = true;
        _lastRecord = null;
        var startedUtc = DateTime.UtcNow;
        StatusMessage = null;
        TaskbarState = TaskbarItemProgressState.Normal;
        ReportProgress("clean.progress", 0, selection.Count);
        IntPtr owner = OwnerHandle?.Invoke() ?? IntPtr.Zero;
        var progress = new Progress<(int Done, int Total)>(p => ReportProgress("clean.progress", p.Done, p.Total));

        // The shell's Recycle Bin operation may show a warning window: run it on an STA thread.
        var excluded = _settings.ExcludedFolders.ToArray();
        var outcome = await RunOnStaThread(() => Cleaner.Clean(selection, new ShellRecycleBin(owner), DateTime.UtcNow, progress, excluded: excluded));

        // Remember exactly what went to the Recycle Bin, so this clean can be undone.
        if (outcome.Moved.Count > 0)
        {
            _lastRecord = new CleanRecord
            {
                StartedUtc = startedUtc,
                FinishedUtc = DateTime.UtcNow,
                Items = outcome.Moved.Select(i => new RecordedItem(i.Path, i.IsDirectory, i.Bytes)).ToList(),
            };
            _history.Add(_lastRecord);
            RefreshHistory();
        }

        string moved = SizeFormatter.Format(outcome.MovedBytes, Loc.Instance.Culture);
        StatusMessage = outcome.Skipped.Count == 0
            ? Loc.Instance.Format("clean.done", outcome.MovedCount, moved)
            : Loc.Instance.Format("clean.doneSkipped", outcome.MovedCount, moved, outcome.Skipped.Count);
        TaskbarState = TaskbarItemProgressState.None;
        IsBusy = false;
        OnPropertyChanged(nameof(CanUndoLast));

        await ScanAsync(keepMessage: true);
    }

    /// <summary>Puts back what one clean moved, from the Recycle Bin to where it was.</summary>
    private async Task UndoAsync(CleanRecord record)
    {
        if (IsBusy) return;
        IsBusy = true;
        var outcome = await Task.Run(() => Undo.Restore(record, RecycleBinReader.CurrentUserFolders()));
        _history.Save();
        string message = outcome.Skipped.Count == 0
            ? Loc.Instance.Format("undo.done", outcome.Restored)
            : Loc.Instance.Format("undo.partial", outcome.Restored, outcome.Skipped.Count);
        StatusMessage = message;
        HistoryNote = message;
        IsBusy = false;
        RefreshHistory();
        OnPropertyChanged(nameof(CanUndoLast));
        await ScanAsync(keepMessage: true);
    }

    /// <summary>The lock button on an item: never offer it again. Only that card is scanned again.</summary>
    private Task ExcludeItemAsync(CategoryViewModel category, string path)
    {
        if (IsReviewing) return Task.CompletedTask;
        return ChangeExclusionsAsync(() => AddExclusion(path), new List<CategoryViewModel> { category });
    }

    private void AddExclusion(string path)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { return; }
        if (full.EndsWith(':')) full += '\\'; // a whole drive
        if (ExcludedPaths.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase))) return;
        ExcludedPaths.Add(full);
    }

    /// <summary>Saves the list and scans again, so the cards show what can still be cleaned.</summary>
    private async Task ChangeExclusionsAsync(Action change, List<CategoryViewModel>? rescan)
    {
        change();
        _settings.ExcludedFolders = ExcludedPaths.ToList();
        _settings.Save();
        OnPropertyChanged(nameof(HasExclusions));
        // Nothing to update before the first scan; a scan already running is fine too, because the
        // check right before moving always reads the current list.
        if (HasScanned && !IsBusy) await ScanAsync(rescan ?? AllCategories.ToList(), keepMessage: true);
    }

    private void RefreshHistory()
    {
        HistoryRows.Clear();
        foreach (var record in _history.Records)
            HistoryRows.Add(new HistoryRowViewModel(record, () => !IsBusy, UndoAsync));
        OnPropertyChanged(nameof(HasHistory));
    }

    private void ReportProgress(string key, int done, int total)
    {
        _progressKey = key;
        _progressDone = done;
        _progressTotal = total;
        Progress = total == 0 ? 0 : done / (double)total;
        OnPropertyChanged(nameof(ProgressText));
    }

    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(FoundText));
        OnPropertyChanged(nameof(CanClean));
        if (IsReviewing)
        {
            OnPropertyChanged(nameof(ReviewSummary));
            OnPropertyChanged(nameof(CanConfirmReview));
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void Relocalize()
    {
        foreach (var g in Groups)
        {
            g.Relocalize();
            foreach (var c in g.Categories) c.Relocalize();
        }
        foreach (var option in TrayDisplayOptions) option.Relocalize();
        RefreshHistory();
        OnAllPropertiesChanged();
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://github.com/", StringComparison.Ordinal)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>Made-up results for screenshots: no real paths from this computer.</summary>
    public void LoadSample()
    {
        const string temp = @"C:\Users\Alex\AppData\Local\Temp";
        const string local = @"C:\Users\Alex\AppData\Local";
        const string roaming = @"C:\Users\Alex\AppData\Roaming";
        const string wechat = @"C:\Users\Alex\Documents\xwechat_files\alex_1234";
        var old = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        CleanupItem Dir(string path, double mb) => new(path, true, (long)(mb * 1024 * 1024), old);
        CleanupItem File(string path, double mb) => new(path, false, (long)(mb * 1024 * 1024), old);

        var samples = new Dictionary<string, (ScanStatus Status, CleanupItem[] Items, string? Blocking)>
        {
            ["temp-files"] = (ScanStatus.Found, new[] { Dir($@"{temp}\vs-setup-cache", 842), Dir($@"{temp}\7zS4A1B", 96), File($@"{temp}\installer.log", 3.2) }, null),
            ["crash-dumps"] = (ScanStatus.Found, new[] { File($@"{local}\CrashDumps\photos.exe.12840.dmp", 212) }, null),
            ["error-reports"] = (ScanStatus.Found, new[] { Dir($@"{local}\Microsoft\Windows\WER\ReportArchive\AppCrash_game.exe_1f2e", 18.5) }, null),
            ["edge-cache"] = (ScanStatus.BlockedByRunningApp, Array.Empty<CleanupItem>(), "msedge"),
            ["chrome-cache"] = (ScanStatus.Found, new[] { Dir($@"{local}\Google\Chrome\User Data\Default\Cache", 640), Dir($@"{local}\Google\Chrome\User Data\Default\Code Cache", 188) }, null),
            ["wechat-cache"] = (ScanStatus.Found, new[] { Dir($@"{roaming}\Tencent\xwechat\radium\web\profiles\multitab_3f2a\Cache\Cache_Data", 363), Dir($@"{wechat}\cache\2026-08", 96) }, null),
            ["wechat-logs"] = (ScanStatus.Found, new[] { File($@"{roaming}\Tencent\xwechat\log\mm_20260928.xlog", 85.7), File($@"{roaming}\Tencent\xwechat\log\mm_20260927.xlog", 51.3) }, null),
            ["wechat-media"] = (ScanStatus.Found, new[] { Dir($@"{wechat}\msg\video\2025-12", 1240), Dir($@"{wechat}\msg\attach\9e20f4ab51c7d3e8\2025-11", 412) }, null),
            ["wechat-files"] = (ScanStatus.NothingFound, Array.Empty<CleanupItem>(), null),
            ["npm-cache"] = (ScanStatus.Found, new[] { Dir($@"{local}\npm-cache\_cacache", 1310) }, null),
            ["pip-cache"] = (ScanStatus.NothingFound, Array.Empty<CleanupItem>(), null),
            ["nuget-cache"] = (ScanStatus.Found, new[] { Dir($@"{local}\NuGet\v3-cache", 452) }, null),
            ["yarn-cache"] = (ScanStatus.NothingFound, Array.Empty<CleanupItem>(), null),
            ["gradle-cache"] = (ScanStatus.Found, new[] { Dir(@"C:\Users\Alex\.gradle\caches\modules-2", 2210), Dir(@"C:\Users\Alex\.gradle\caches\transforms-4", 870) }, null),
            ["download-installers"] = (ScanStatus.Found, new[] { File(@"C:\Users\Alex\Downloads\VSCodeUserSetup-x64.exe", 98), File(@"C:\Users\Alex\Downloads\python-3.13.0-amd64.exe", 27) }, null),
        };
        foreach (var c in AllCategories)
        {
            if (!samples.TryGetValue(c.Id, out var s))
            {
                // Only the sample apps, whatever is installed on the PC making the screenshots.
                c.SetScan(new CategoryScan(c.Category, Array.Empty<CleanupItem>(), ScanStatus.NotInstalled));
                continue;
            }
            c.SetScan(new CategoryScan(c.Category, s.Items, s.Status, s.Blocking) { Excluded = c.Id == "temp-files" ? 1 : 0 });
        }
        ExcludedPaths.Clear();
        ExcludedPaths.Add($@"{temp}\my-build-output");
        OnPropertyChanged(nameof(HasExclusions));
        var first = AllCategories.First(c => c.Id == "temp-files");
        first.IsExpanded = true;
        HasScanned = true;

        // Two made-up past cleans for the Settings page.
        _history.Records.Clear();
        _history.Records.Add(new CleanRecord
        {
            StartedUtc = new DateTime(2026, 9, 28, 1, 30, 0, DateTimeKind.Utc),
            FinishedUtc = new DateTime(2026, 9, 28, 1, 30, 12, DateTimeKind.Utc),
            Items = { new RecordedItem($@"{temp}\vs-setup-cache", true, 842L << 20), new RecordedItem($@"{local}\npm-cache\_cacache", true, 1310L << 20) },
        });
        _history.Records.Add(new CleanRecord
        {
            StartedUtc = new DateTime(2026, 9, 21, 8, 5, 0, DateTimeKind.Utc),
            FinishedUtc = new DateTime(2026, 9, 21, 8, 5, 3, DateTimeKind.Utc),
            Items = { new RecordedItem($@"{local}\CrashDumps\game.exe.4412.dmp", false, 96L << 20) },
            State = UndoState.Undone,
            RestoredCount = 1,
        });
        RefreshHistory();
    }
}
