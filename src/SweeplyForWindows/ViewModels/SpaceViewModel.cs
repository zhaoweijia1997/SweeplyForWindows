using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Sweeply.Core;
using SweeplyForWindows.Localization;

namespace SweeplyForWindows.ViewModels;

/// <summary>A drive offered on the Space page.</summary>
public sealed record DriveOption(string Root, string Text);

/// <summary>One folder of the path above the list; clicking it goes back up to it.</summary>
public sealed record SpaceCrumb(string Name, ICommand OpenCommand, bool IsLast);

/// <summary>
/// The Space page: what takes up the space on a drive or in a folder. Scanning only reads. What is ticked
/// goes through the same list, check right before moving, Recycle Bin and undo as a clean, with the rules
/// of <see cref="SpaceMoves"/> on top (nothing from Windows, programs or app data).
/// </summary>
public sealed class SpaceViewModel : ObservableObject
{
    /// <summary>Lines drawn for one folder; the rest are summed up in one line.</summary>
    private const int ListLimit = 500;

    private sealed record Picked(string Path, bool IsDirectory, long Bytes, DateTime LastWriteUtc);

    private readonly Func<IReadOnlyCollection<string>> _excluded;
    private readonly Action<List<(CleanupItem Item, CleanupCategory Category)>> _review;
    private readonly Func<bool> _mainIdle;
    private readonly SpaceMoves _moves;
    private readonly Dictionary<string, Picked> _picked = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cancel;
    private SpaceResult? _result;
    private SpaceFolder? _current;
    private string? _chosenFolder;
    private DriveOption? _selectedDrive;
    private bool _isScanning;
    private bool _sample;
    private string? _statusMessage;
    private string? _progressText;
    private string? _progressPath;
    private string? _moreText;
    private int _tabIndex;
    private int _loadVersion;

    /// <param name="excluded">The "Never clean" list as it is now.</param>
    /// <param name="review">Opens the list shown before anything moves.</param>
    /// <param name="mainIdle">No scan, clean or list in progress on the Clean up page.</param>
    public SpaceViewModel(KnownPaths paths, Func<IReadOnlyCollection<string>> excluded,
        Action<List<(CleanupItem Item, CleanupCategory Category)>> review, Func<bool> mainIdle)
    {
        _excluded = excluded;
        _review = review;
        _mainIdle = mainIdle;
        _moves = SpaceMoves.ForThisPc(paths);
        ScanCommand = new RelayCommand(_ =>
        {
            if (IsScanning) _cancel?.Cancel();
            else _ = ScanAsync();
        }, () => IsScanning || ScanTarget is not null);
        ChooseFolderCommand = new RelayCommand(_ =>
        {
            if (PickFolder?.Invoke() is not string folder) return;
            _chosenFolder = folder;
            OnPropertyChanged(nameof(ScanTargetText));
            _ = ScanAsync();
        }, () => !IsScanning);
        MoveCommand = new RelayCommand(_ => Move(), () => CanMove);
        UpCommand = new RelayCommand(_ => { if (_current?.Parent is { } p) Navigate(p); }, () => _current?.Parent is not null);
        RefreshDrives();
    }

    /// <summary>Asks for a folder to scan; null when cancelled. Set through the main view model.</summary>
    public Func<string?>? PickFolder { get; set; }

    public ObservableCollection<DriveOption> Drives { get; } = new();

    public DriveOption? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (!SetField(ref _selectedDrive, value)) return;
            _chosenFolder = null; // back to the whole drive
            OnPropertyChanged(nameof(ScanTargetText));
        }
    }

    private string? ScanTarget => _chosenFolder ?? SelectedDrive?.Root;

    /// <summary>Shown under the drive list when a folder was chosen instead of a whole drive.</summary>
    public string? ScanTargetText => _chosenFolder;

    public ICommand ScanCommand { get; }
    public ICommand ChooseFolderCommand { get; }
    public ICommand MoveCommand { get; }
    public ICommand UpCommand { get; }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetField(ref _isScanning, value)) return;
            OnPropertyChanged(nameof(ScanButtonText));
            OnPropertyChanged(nameof(CanMove));
            OnPropertyChanged(nameof(ShowIntro));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string ScanButtonText => Loc.Instance[IsScanning ? "space.stop" : "space.scan"];

    public bool HasResult => _result is not null;

    /// <summary>Before the first scan: what this page is for.</summary>
    public bool ShowIntro => !HasResult && !IsScanning;

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string? ProgressText
    {
        get => _progressText;
        private set => SetField(ref _progressText, value);
    }

    /// <summary>The folder being read right now.</summary>
    public string? ProgressPath
    {
        get => _progressPath;
        private set => SetField(ref _progressPath, value);
    }

    /// <summary>Above the tabs: what was scanned, how much, how long; and what could not be read.</summary>
    public string SummaryText => _result is null ? "" : Loc.Instance.Format("space.summary", _result.Root.Path,
        Size(_result.Root.Bytes), _result.Root.FileCount.ToString("N0", Loc.Instance.Culture),
        Math.Max(1, (int)Math.Round(_result.Duration.TotalSeconds)));

    public string? UnreadableText => _result is { UnreadableFolders: > 0 }
        ? Loc.Instance.Format("space.unreadable", _result.UnreadableFolders.ToString("N0", Loc.Instance.Culture))
        : null;

    /// <summary>
    /// A whole drive: how much less was found than Windows says is used (System Restore points and other
    /// places that can't be read), so a big difference does not look like a mistake.
    /// </summary>
    public string? UnaccountedText
    {
        get
        {
            if (_result is null || _sample) return null;
            string root = _result.Root.Path;
            if (Path.GetPathRoot(root) is not string drive || !string.Equals(drive, root, StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                var info = new DriveInfo(drive);
                long used = info.TotalSize - info.TotalFreeSpace;
                long missing = used - _result.Root.Bytes;
                return missing < 1L << 30 ? null
                    : Loc.Instance.Format("space.unaccounted", drive.TrimEnd('\\'), Size(used), Size(_result.Root.Bytes), Size(missing));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        }
    }

    /// <summary>From the command line: scan this folder instead of a whole drive.</summary>
    public void ScanFolder(string folder)
    {
        if (IsScanning) return;
        _chosenFolder = folder;
        OnPropertyChanged(nameof(ScanTargetText));
        _ = ScanAsync();
    }

    /// <summary>0 folders, 1 largest files, 2 by type.</summary>
    public int TabIndex
    {
        get => _tabIndex;
        set => SetField(ref _tabIndex, value);
    }

    // ---- Folders tab ----

    public ObservableCollection<SpaceCrumb> Crumbs { get; } = new();
    public ObservableCollection<SpaceEntryViewModel> Entries { get; } = new();

    public string CurrentText => _current is null ? "" : Loc.Instance.Format("space.current", Size(_current.Bytes),
        _current.FileCount.ToString("N0", Loc.Instance.Culture));

    public bool CurrentIncomplete => _current?.Incomplete == true;

    /// <summary>"…and 1,234 more, 2.1 GB" when a folder holds more than is drawn.</summary>
    public string? MoreText
    {
        get => _moreText;
        private set => SetField(ref _moreText, value);
    }

    // ---- Largest files and by type ----

    public ObservableCollection<SpaceEntryViewModel> LargestFiles { get; } = new();
    public ObservableCollection<SpaceKindRow> Kinds { get; } = new();

    // ---- Ticked items ----

    public bool HasPicked => _picked.Count > 0;

    public string SelectedText
    {
        get
        {
            var picked = Effective();
            return Loc.Instance.Format("space.selected", picked.Count, Size(picked.Sum(p => p.Bytes)));
        }
    }

    public bool CanMove => _picked.Count > 0 && !IsScanning && _mainIdle();

    internal bool IsPicked(string path) => _picked.ContainsKey(path);

    internal void SetPicked(SpaceEntryViewModel row, bool value)
    {
        if (value && !row.CanSelect) return;
        if (value) _picked[row.Path] = new Picked(row.Path, row.IsFolder, row.Bytes, row.LastWriteUtc);
        else _picked.Remove(row.Path);
        // The same file can be in both lists.
        foreach (var r in Entries.Concat(LargestFiles))
            if (string.Equals(r.Path, row.Path, StringComparison.OrdinalIgnoreCase)) r.RefreshSelection();
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(HasPicked));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(CanMove));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>What is ticked, without what lies inside something else that is ticked (it goes along).</summary>
    private List<Picked> Effective()
    {
        var all = _picked.Values.OrderBy(p => p.Path.Length).ToList();
        var kept = new List<Picked>();
        foreach (var p in all)
            if (!kept.Any(k => k.IsDirectory && p.Path.StartsWith(k.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                kept.Add(p);
        return kept;
    }

    private void Move()
    {
        if (!CanMove) return;
        var selection = Effective()
            .OrderByDescending(p => p.Bytes)
            .Select(p => (new CleanupItem(p.Path, p.IsDirectory, p.Bytes, p.LastWriteUtc), _moves.CategoryFor(p.Path)))
            .ToList();
        _review(selection);
    }

    /// <summary>After moving: takes what went out of the lists and the totals.</summary>
    public void AfterMoved(CleanOutcome outcome)
    {
        foreach (var item in outcome.Moved)
        {
            _picked.Remove(item.Path);
            if (_result is null) continue;
            if (item.IsDirectory)
            {
                var folder = _result.Find(item.Path);
                if (folder?.Parent is { } parent)
                {
                    parent.Folders.Remove(folder);
                    parent.Subtract(folder.Bytes, folder.FileCount);
                }
                string inside = item.Path.TrimEnd('\\') + "\\";
                _result.LargestFiles.RemoveAll(f => f.Path.StartsWith(inside, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                var parent = _result.Find(Path.GetDirectoryName(item.Path) ?? "");
                parent?.Subtract(item.Bytes, 1);
                if (parent is not null) parent.OwnFileBytes = Math.Max(0, parent.OwnFileBytes - item.Bytes);
                _result.LargestFiles.RemoveAll(f => string.Equals(f.Path, item.Path, StringComparison.OrdinalIgnoreCase));
            }
        }

        string moved = Size(outcome.MovedBytes);
        StatusMessage = outcome.Skipped.Count == 0
            ? Loc.Instance.Format("space.moved", outcome.MovedCount, moved)
            : Loc.Instance.Format("space.movedSkipped", outcome.MovedCount, moved, outcome.Skipped.Count);
        if (_result is not null)
        {
            // The folder on screen may itself have gone (moved from the largest files' own folder).
            var current = _current is null ? null : _result.Find(_current.Path);
            Navigate(current ?? _result.Root);
            FillLargest();
        }
        OnSelectionChanged();
        OnPropertyChanged(nameof(SummaryText));
    }

    // ---- Scanning ----

    public void RefreshDrives()
    {
        string? keep = SelectedDrive?.Root;
        Drives.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? Loc.Instance["space.localDisk"] : drive.VolumeLabel;
                Drives.Add(new DriveOption(drive.RootDirectory.FullName, Loc.Instance.Format("space.drive", label,
                    drive.RootDirectory.FullName.TrimEnd('\\'), Size(drive.AvailableFreeSpace), Size(drive.TotalSize))));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        string system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        _selectedDrive = Drives.FirstOrDefault(d => d.Root == keep)
            ?? Drives.FirstOrDefault(d => string.Equals(d.Root, system, StringComparison.OrdinalIgnoreCase))
            ?? Drives.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedDrive));
    }

    public async Task ScanAsync()
    {
        if (IsScanning || ScanTarget is not string target) return;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        StatusMessage = null;
        _picked.Clear();
        OnSelectionChanged();
        IsScanning = true;
        ProgressText = Loc.Instance["space.starting"];
        ProgressPath = target;
        var progress = new Progress<SpaceProgress>(p =>
        {
            ProgressText = Loc.Instance.Format("space.scanning", p.Folders.ToString("N0", Loc.Instance.Culture),
                p.Files.ToString("N0", Loc.Instance.Culture), Size(p.Bytes));
            ProgressPath = p.Current;
        });
        try
        {
            var result = await Task.Run(() => SpaceScanner.Scan(target, progress, token), token);
            SetResult(result);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Loc.Instance["space.cancelled"];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusMessage = Loc.Instance.Format("space.failed", e.Message);
        }
        finally
        {
            IsScanning = false;
            ProgressPath = null;
            _cancel.Dispose();
            _cancel = null;
        }
    }

    private void SetResult(SpaceResult result)
    {
        _result = result;
        TabIndex = 0;
        Navigate(result.Root);
        FillLargest();
        Kinds.Clear();
        foreach (var k in result.ByKind) Kinds.Add(new SpaceKindRow(k, result.Root.Bytes));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ShowIntro));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(UnreadableText));
        OnPropertyChanged(nameof(UnaccountedText));
    }

    private void FillLargest()
    {
        LargestFiles.Clear();
        if (_result is null) return;
        long whole = _result.Root.Bytes;
        foreach (var f in _result.LargestFiles)
            LargestFiles.Add(new SpaceEntryViewModel(this, null, f, () => whole, showFolder: true, Check(f.Path)));
    }

    /// <summary>Shows one folder: its subfolders and its files, largest first.</summary>
    public async void Navigate(SpaceFolder folder)
    {
        _current = folder;
        int version = ++_loadVersion;
        Crumbs.Clear();
        var chain = new List<SpaceFolder>();
        for (var f = folder; f is not null; f = f.Parent) chain.Insert(0, f);
        foreach (var f in chain)
        {
            var target = f;
            Crumbs.Add(new SpaceCrumb(f.Parent is null ? f.Path : f.Name, new RelayCommand(_ => Navigate(target)), ReferenceEquals(f, folder)));
        }
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(CurrentIncomplete));
        CommandManager.InvalidateRequerySuggested();

        string path = folder.Path;
        var files = _sample ? new List<SpaceFile>() : await Task.Run(() => SpaceScanner.FilesIn(path));
        if (version != _loadVersion) return; // another folder was opened meanwhile

        var rows = folder.Folders.Select(f => (Bytes: f.Bytes, Folder: (SpaceFolder?)f, File: (SpaceFile?)null))
            .Concat(files.Select(f => (Bytes: f.Bytes, Folder: (SpaceFolder?)null, File: (SpaceFile?)f)))
            .OrderByDescending(r => r.Bytes)
            .ToList();
        Entries.Clear();
        foreach (var r in rows.Take(ListLimit))
        {
            string p = r.Folder?.Path ?? r.File!.Path;
            Entries.Add(new SpaceEntryViewModel(this, r.Folder, r.File, () => folder.Bytes, showFolder: false,
                r.Folder?.IsLink == true ? SpaceMoveCheck.Link : Check(p)));
        }
        MoreText = rows.Count > ListLimit
            ? Loc.Instance.Format("space.more", (rows.Count - ListLimit).ToString("N0", Loc.Instance.Culture),
                Size(rows.Skip(ListLimit).Sum(r => r.Bytes)))
            : null;
    }

    private SpaceMoveCheck Check(string path) => _sample ? SampleCheck(path) : _moves.Check(path, _excluded());

    /// <summary>The same rules for the made-up results, without looking at the disk.</summary>
    private static SpaceMoveCheck SampleCheck(string path)
    {
        string name = Path.GetFileName(path);
        if (name is "pagefile.sys" or "$Recycle.Bin") return SpaceMoveCheck.HiddenSystem;
        string[] systemPlaces = { @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData", @"C:\Users\Alex\AppData" };
        if (Exclusions.IsExcluded(path, systemPlaces)) return SpaceMoveCheck.SystemOrAppData;
        string[] personal = { @"C:\Users\Alex", @"C:\Users\Alex\Videos", @"C:\Users\Alex\Downloads", @"C:\Users\Alex\Documents", @"C:\Users\Alex\Pictures" };
        return personal.Contains(path, StringComparer.OrdinalIgnoreCase) ? SpaceMoveCheck.PersonalFolder : SpaceMoveCheck.Ok;
    }

    public void Relocalize()
    {
        if (!_sample) RefreshDrives();
        foreach (var r in Entries.Concat(LargestFiles)) r.Refresh();
        foreach (var k in Kinds) k.Refresh();
        if (_current is not null) Navigate(_current);
        OnAllPropertiesChanged();
    }

    private static string Size(long bytes) => SizeFormatter.Format(bytes, Loc.Instance.Culture);

    /// <summary>Made-up results for screenshots: no real paths from this computer.</summary>
    public void LoadSample()
    {
        _sample = true;
        Drives.Clear();
        Drives.Add(new DriveOption(@"C:\", Loc.Instance.Format("space.drive", "Windows", "C:", "61.4 GB", "476 GB")));
        _selectedDrive = Drives[0];
        OnPropertyChanged(nameof(SelectedDrive));
        var root = new SpaceFolder(null, @"C:\");
        SpaceFolder Add(SpaceFolder parent, string name, double gb, long files, bool incomplete = false)
        {
            var f = new SpaceFolder(parent, name) { Bytes = (long)(gb * (1L << 30)), FileCount = files, Incomplete = incomplete };
            parent.Folders.Add(f);
            return f;
        }
        var users = Add(root, "Users", 142.6, 412_380);
        Add(root, "Windows", 31.2, 198_442, incomplete: true);
        Add(root, "Program Files", 18.9, 61_205);
        Add(root, "ProgramData", 7.4, 40_118, incomplete: true);
        Add(root, "Program Files (x86)", 5.1, 22_960);
        Add(root, "Games", 64.3, 18_204);
        Add(root, "$Recycle.Bin", 2.2, 311, incomplete: true);
        root.Folders.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        root.Bytes = root.Folders.Sum(f => f.Bytes) + (17L << 30);
        root.FileCount = root.Folders.Sum(f => f.FileCount) + 3;
        var alex = Add(users, "Alex", 142.6, 412_380);
        Add(alex, "Videos", 48.4, 312);
        Add(alex, "AppData", 39.7, 301_206, incomplete: true);
        Add(alex, "Downloads", 21.8, 1_204);
        Add(alex, "Documents", 18.3, 22_451);
        Add(alex, "Pictures", 12.9, 87_190);

        var old = new DateTime(2025, 11, 3, 0, 0, 0, DateTimeKind.Utc);
        SpaceFile File(string path, double gb) => new(path, (long)(gb * (1L << 30)), old);
        var result = new SpaceResult
        {
            Root = root,
            LargestFiles = new List<SpaceFile>
            {
                File(@"C:\pagefile.sys", 16.0),
                File(@"C:\Users\Alex\Videos\Family trip 2025.mp4", 12.4),
                File(@"C:\Users\Alex\Downloads\ubuntu-24.04-desktop-amd64.iso", 6.1),
                File(@"C:\Games\Starfield\Data\Starfield - Textures01.ba2", 5.8),
                File(@"C:\Users\Alex\Documents\Virtual Machines\Windows 11\Windows 11-disk1.vmdk", 4.9),
                File(@"C:\Users\Alex\Videos\Screen recordings\Recording 2025-09-14.mkv", 3.2),
                File(@"C:\Users\Alex\Downloads\photos-backup-2024.zip", 2.7),
            },
            ByKind = new List<SpaceKindTotal>
            {
                new(FileKind.Other, 98L << 30, 640_122),
                new(FileKind.Videos, 57L << 30, 1_412),
                new(FileKind.Programs, 38L << 30, 120_301),
                new(FileKind.Pictures, 14L << 30, 90_215),
                new(FileKind.DiskImages, 11L << 30, 6),
                new(FileKind.Archives, 6L << 30, 1_104),
                new(FileKind.Documents, 3L << 30, 21_877),
                new(FileKind.Music, 2L << 30, 3_240),
            },
            FolderCount = 211_802,
            UnreadableFolders = 37,
            Duration = TimeSpan.FromSeconds(48),
        };
        SetResult(result);
        _picked[@"C:\Users\Alex\Downloads\ubuntu-24.04-desktop-amd64.iso"] = new Picked(@"C:\Users\Alex\Downloads\ubuntu-24.04-desktop-amd64.iso", false, (long)(6.1 * (1L << 30)), old);
        _picked[@"C:\Users\Alex\Downloads\photos-backup-2024.zip"] = new Picked(@"C:\Users\Alex\Downloads\photos-backup-2024.zip", false, (long)(2.7 * (1L << 30)), old);
        foreach (var r in LargestFiles) r.RefreshSelection();
        OnSelectionChanged();
    }
}

/// <summary>One line in the folder list or the largest files: a folder or a file.</summary>
public sealed class SpaceEntryViewModel : ObservableObject
{
    private readonly SpaceViewModel _owner;
    private readonly Func<long> _whole;
    private readonly bool _showFolder;
    private readonly SpaceMoveCheck _check;

    /// <param name="whole">What the share is of: the folder on screen, or the whole scan.</param>
    /// <param name="showFolder">Show where the file is under its name (the largest files, from all over).</param>
    public SpaceEntryViewModel(SpaceViewModel owner, SpaceFolder? folder, SpaceFile? file, Func<long> whole, bool showFolder, SpaceMoveCheck check)
    {
        _owner = owner;
        Folder = folder;
        File = file;
        _whole = whole;
        _showFolder = showFolder;
        _check = check;
        OpenCommand = new RelayCommand(_ => { if (Folder is { IsLink: false } f) _owner.Navigate(f); });
        RevealCommand = new RelayCommand(_ => Reveal());
    }

    public SpaceFolder? Folder { get; }
    public SpaceFile? File { get; }
    public bool IsFolder => Folder is not null;
    public bool CanOpen => Folder is { IsLink: false };
    public string Path => Folder?.Path ?? File!.Path;
    public string Name => Folder?.Name ?? System.IO.Path.GetFileName(File!.Path);

    /// <summary>The folder the file is in, shown small under its name; null in the folder list.</summary>
    public string? SubPath => _showFolder ? System.IO.Path.GetDirectoryName(Path) : null;
    public long Bytes => Folder?.Bytes ?? File!.Bytes;
    public DateTime LastWriteUtc => File?.LastWriteUtc ?? DateTime.MinValue;

    public double Share
    {
        get
        {
            long whole = _whole();
            return whole <= 0 ? 0 : Math.Min(1.0, (double)Bytes / whole);
        }
    }

    /// <summary>The bar next to the size: the share of the folder (or drive) on screen.</summary>
    public double BarWidth => Bytes <= 0 ? 0 : Math.Max(1, Share * 64);
    public string ShareText => Share.ToString("P0", Loc.Instance.Culture);
    public string SizeText => SizeFormatter.Format(Bytes, Loc.Instance.Culture);

    public string Detail
    {
        get
        {
            if (Folder is { IsLink: true }) return Loc.Instance["space.link"];
            if (Folder is not null) return Loc.Instance.Format("space.files", Folder.FileCount.ToString("N0", Loc.Instance.Culture));
            return File!.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd", Loc.Instance.Culture);
        }
    }

    /// <summary>Part of the folder could not be read: a small warning sign next to the file count.</summary>
    public bool IsIncomplete => Folder is { Incomplete: true, IsLink: false };
    public string IncompleteText => Loc.Instance["space.incomplete"];

    /// <summary>Folder, link or file, in Segoe Fluent Icons.</summary>
    public string Glyph => Folder is { IsLink: true } ? "\uE71B" : Folder is not null ? "\uE8B7" : "\uE8A5";

    public bool CanSelect => _check == SpaceMoveCheck.Ok;

    /// <summary>Why this one can't be ticked; null when it can.</summary>
    public string? WhyNot => CanSelect ? null : Loc.Instance[$"space.why.{_check}"];

    public bool IsSelected
    {
        get => _owner.IsPicked(Path);
        set => _owner.SetPicked(this, value);
    }

    public ICommand OpenCommand { get; }
    public ICommand RevealCommand { get; }

    internal void RefreshSelection() => OnPropertyChanged(nameof(IsSelected));

    internal void Refresh() => OnAllPropertiesChanged();

    private void Reveal()
    {
        string arg = System.IO.File.Exists(Path) || Directory.Exists(Path)
            ? $"/select,\"{Path}\""
            : $"\"{System.IO.Path.GetDirectoryName(Path)}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arg) { UseShellExecute = true });
    }
}

/// <summary>One kind of file on the "By type" tab.</summary>
public sealed class SpaceKindRow : ObservableObject
{
    private readonly long _whole;

    public SpaceKindRow(SpaceKindTotal total, long whole)
    {
        Total = total;
        _whole = whole;
    }

    public SpaceKindTotal Total { get; }
    public string Name => Loc.Instance[$"kind.{Total.Kind}"];
    public string SizeText => SizeFormatter.Format(Total.Bytes, Loc.Instance.Culture);
    public string CountText => Loc.Instance.Format("space.files", Total.Count.ToString("N0", Loc.Instance.Culture));
    public double Share => _whole <= 0 ? 0 : Math.Min(1.0, (double)Total.Bytes / _whole);
    public double BarWidth => Total.Bytes <= 0 ? 0 : Math.Max(1, Share * 160);
    public string ShareText => Share.ToString("P0", Loc.Instance.Culture);

    internal void Refresh() => OnAllPropertiesChanged();
}
