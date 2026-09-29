using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Shell;
using Sweeply.Core;
using SweeplyForWindows.Localization;
using SweeplyForWindows.Platform;

namespace SweeplyForWindows.ViewModels;

public sealed class GroupViewModel : ObservableObject
{
    public GroupViewModel(CategoryGroup group) => Group = group;
    public CategoryGroup Group { get; }
    public string Name => Loc.Instance[$"clean.group.{Group}"];
    public ObservableCollection<CategoryViewModel> Categories { get; } = new();
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

    public MainViewModel(KnownPaths paths, Settings settings)
    {
        _settings = settings;
        foreach (var category in CategoryCatalog.Create(paths))
        {
            var group = Groups.FirstOrDefault(g => g.Group == category.Group);
            if (group is null) Groups.Add(group = new GroupViewModel(category.Group));
            group.Categories.Add(new CategoryViewModel(category, OnSelectionChanged));
        }

        ScanCommand = new RelayCommand(async _ => await ScanAsync(), () => !IsBusy);
        CleanCommand = new RelayCommand(async _ => await CleanAsync(), () => CanClean);
        OpenUrlCommand = new RelayCommand(p => OpenUrl(p as string));
        Loc.Instance.LanguageChanged += Relocalize;
    }

    public ObservableCollection<GroupViewModel> Groups { get; } = new();
    public IEnumerable<CategoryViewModel> AllCategories => Groups.SelectMany(g => g.Categories);

    public ICommand ScanCommand { get; }
    public ICommand CleanCommand { get; }
    public ICommand OpenUrlCommand { get; }

    /// <summary>Asks the user to confirm (title, message). Set by the window.</summary>
    public Func<string, string, bool>? Confirm { get; set; }

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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanClean));
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

    public bool CanClean => !IsBusy && SelectedCount > 0;

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

    public async Task ScanAsync(bool keepMessage = false)
    {
        if (IsBusy) return;
        IsBusy = true;
        if (!keepMessage) StatusMessage = null;
        var categories = AllCategories.ToList();
        foreach (var c in categories) c.IsScanning = true;
        TaskbarState = TaskbarItemProgressState.Normal;
        ReportProgress("clean.scanProgress", 0, categories.Count);

        for (int i = 0; i < categories.Count; i++)
        {
            var c = categories[i];
            var scan = await Task.Run(() => Scanner.Scan(c.Category, DateTime.UtcNow));
            c.SetScan(scan);
            ReportProgress("clean.scanProgress", i + 1, categories.Count);
        }

        HasScanned = true;
        TaskbarState = TaskbarItemProgressState.None;
        IsBusy = false;
    }

    private async Task CleanAsync()
    {
        var selection = AllCategories
            .SelectMany(c => c.SelectedItems.Select(item => (item, c.Category)))
            .ToList();
        if (selection.Count == 0) return;

        string size = SizeFormatter.Format(selection.Sum(s => s.item.Bytes), Loc.Instance.Culture);
        bool ok = Confirm?.Invoke(Loc.Instance["clean.confirm.title"],
            Loc.Instance.Format("clean.confirm.body", selection.Count, size)) ?? false;
        if (!ok) return;

        IsBusy = true;
        StatusMessage = null;
        TaskbarState = TaskbarItemProgressState.Normal;
        ReportProgress("clean.progress", 0, selection.Count);
        IntPtr owner = OwnerHandle?.Invoke() ?? IntPtr.Zero;
        var progress = new Progress<(int Done, int Total)>(p => ReportProgress("clean.progress", p.Done, p.Total));

        // The shell's Recycle Bin operation may show a warning window: run it on an STA thread.
        var outcome = await RunOnStaThread(() => Cleaner.Clean(selection, new ShellRecycleBin(owner), DateTime.UtcNow, progress));

        string moved = SizeFormatter.Format(outcome.MovedBytes, Loc.Instance.Culture);
        StatusMessage = outcome.Skipped.Count == 0
            ? Loc.Instance.Format("clean.done", outcome.MovedCount, moved)
            : Loc.Instance.Format("clean.doneSkipped", outcome.MovedCount, moved, outcome.Skipped.Count);
        TaskbarState = TaskbarItemProgressState.None;
        IsBusy = false;

        await ScanAsync(keepMessage: true);
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
        CommandManager.InvalidateRequerySuggested();
    }

    private void Relocalize()
    {
        foreach (var g in Groups)
        {
            g.Relocalize();
            foreach (var c in g.Categories) c.Relocalize();
        }
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
            ["npm-cache"] = (ScanStatus.Found, new[] { Dir($@"{local}\npm-cache\_cacache", 1310) }, null),
            ["pip-cache"] = (ScanStatus.NothingFound, Array.Empty<CleanupItem>(), null),
            ["nuget-cache"] = (ScanStatus.Found, new[] { Dir($@"{local}\NuGet\v3-cache", 452) }, null),
            ["yarn-cache"] = (ScanStatus.NothingFound, Array.Empty<CleanupItem>(), null),
            ["gradle-cache"] = (ScanStatus.Found, new[] { Dir(@"C:\Users\Alex\.gradle\caches\modules-2", 2210), Dir(@"C:\Users\Alex\.gradle\caches\transforms-4", 870) }, null),
            ["download-installers"] = (ScanStatus.Found, new[] { File(@"C:\Users\Alex\Downloads\VSCodeUserSetup-x64.exe", 98), File(@"C:\Users\Alex\Downloads\python-3.13.0-amd64.exe", 27) }, null),
        };
        foreach (var c in AllCategories)
        {
            if (!samples.TryGetValue(c.Id, out var s)) continue;
            c.SetScan(new CategoryScan(c.Category, s.Items, s.Status, s.Blocking));
        }
        var first = AllCategories.First(c => c.Id == "temp-files");
        first.IsExpanded = true;
        HasScanned = true;
    }
}
