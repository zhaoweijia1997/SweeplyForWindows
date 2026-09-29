using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Sweeply.Core;
using SweeplyForWindows.Localization;

namespace SweeplyForWindows.ViewModels;

/// <summary>One card on the Clean up page.</summary>
public sealed class CategoryViewModel : ObservableObject
{
    private static readonly Dictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = "Microsoft Edge",
        ["chrome"] = "Google Chrome",
        ["studio64"] = "Android Studio",
        ["idea64"] = "IntelliJ IDEA",
    };

    /// <summary>
    /// Only the largest items are drawn one by one: a card can hold thousands (a chat app's pictures),
    /// and drawing them all would freeze the page. The rest are still counted and cleaned.
    /// </summary>
    private const int ListLimit = 200;

    private readonly Action _selectionChanged;
    private CategoryScan? _scan;
    private bool _isSelected;
    private bool _isExpanded;
    private bool _isScanning;
    private bool _showAll;

    public CategoryViewModel(CleanupCategory category, Action selectionChanged)
    {
        Category = category;
        _selectionChanged = selectionChanged;
        ShowAllCommand = new RelayCommand(_ =>
        {
            _showAll = true;
            OnPropertyChanged(nameof(VisibleItems));
            OnPropertyChanged(nameof(HasHiddenItems));
        });
    }

    public CleanupCategory Category { get; }
    public string Id => Category.Id;

    public string Name => Loc.Instance[$"cat.{Id}.name"];
    public string Description => Loc.Instance[$"cat.{Id}.desc"];

    public ObservableCollection<ItemViewModel> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    /// <summary>The items drawn in the expanded card: the largest <see cref="ListLimit"/>, or all after "Show all".</summary>
    public IReadOnlyList<ItemViewModel> VisibleItems =>
        _showAll || Items.Count <= ListLimit ? Items : Items.Take(ListLimit).ToList();

    public bool HasHiddenItems => !_showAll && Items.Count > ListLimit;

    public string HiddenItemsText => HasHiddenItems
        ? Loc.Instance.Format("clean.moreItems", Items.Count - ListLimit,
            SizeFormatter.Format(Items.Skip(ListLimit).Sum(i => i.Item.Bytes), Loc.Instance.Culture))
        : "";

    public ICommand ShowAllCommand { get; }
    public bool CanSelect => _scan?.Status == ScanStatus.Found;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value && CanSelect))
            {
                OnPropertyChanged(nameof(IsDimmed));
                _selectionChanged();
            }
        }
    }

    /// <summary>Shown greyed out: nothing to clean, app running, or not scanned.</summary>
    public bool IsDimmed => !CanSelect;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (SetField(ref _isScanning, value)) OnPropertyChanged(nameof(SizeText));
        }
    }

    public long FoundBytes => _scan?.TotalBytes ?? 0;

    public long SelectedBytes => IsSelected ? Items.Where(i => i.IsSelected).Sum(i => i.Item.Bytes) : 0;

    public IEnumerable<CleanupItem> SelectedItems =>
        IsSelected ? Items.Where(i => i.IsSelected).Select(i => i.Item) : Enumerable.Empty<CleanupItem>();

    public string SizeText
    {
        get
        {
            if (IsScanning) return Loc.Instance["clean.scanning"];
            if (_scan is null) return Loc.Instance["clean.notScanned"];
            return _scan.Status switch
            {
                ScanStatus.Found => SizeFormatter.Format(_scan.TotalBytes, Loc.Instance.Culture),
                ScanStatus.BlockedByRunningApp => Loc.Instance.Format("clean.appRunning", AppName(_scan.BlockingProcess)),
                _ => Loc.Instance["clean.nothingFound"],
            };
        }
    }

    public string ItemsText => Loc.Instance.Format("clean.items", Items.Count);

    public void SetScan(CategoryScan scan)
    {
        bool keepSelection = _scan is not null;
        bool wasSelected = _isSelected;
        _scan = scan;
        _showAll = false;
        Items.Clear();
        foreach (var item in scan.Items) Items.Add(new ItemViewModel(item, Loc.Instance.Culture, _selectionChanged));
        _isSelected = CanSelect && (keepSelection ? wasSelected : Category.SelectedByDefault);
        IsScanning = false;
        OnAllPropertiesChanged();
        _selectionChanged();
    }

    public void Relocalize()
    {
        foreach (var item in Items) item.Relocalize(Loc.Instance.Culture);
        OnAllPropertiesChanged();
    }

    /// <summary>
    /// The name people know the app by: from the language files when it differs by language
    /// ("proc.Weixin" is 微信 / WeChat), otherwise the brand name, otherwise the process name.
    /// </summary>
    private static string AppName(string? process)
    {
        if (process is null) return "?";
        string key = $"proc.{process}";
        string localized = Loc.Instance[key];
        if (localized != key) return localized;
        return AppNames.TryGetValue(process, out string? name) ? name : process;
    }
}

/// <summary>One file or folder inside a card.</summary>
public sealed class ItemViewModel : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _isSelected = true;
    private System.Globalization.CultureInfo _culture;

    public ItemViewModel(CleanupItem item, System.Globalization.CultureInfo culture, Action selectionChanged)
    {
        Item = item;
        _culture = culture;
        _selectionChanged = selectionChanged;
        RevealCommand = new RelayCommand(_ => Reveal());
    }

    public CleanupItem Item { get; }
    public string Path => Item.Path;
    public string SizeText => SizeFormatter.Format(Item.Bytes, _culture);
    public ICommand RevealCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value)) _selectionChanged();
        }
    }

    public void Relocalize(System.Globalization.CultureInfo culture)
    {
        _culture = culture;
        OnPropertyChanged(nameof(SizeText));
    }

    private void Reveal()
    {
        string arg = System.IO.File.Exists(Path) || System.IO.Directory.Exists(Path)
            ? $"/select,\"{Path}\""
            : $"\"{System.IO.Path.GetDirectoryName(Path)}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arg) { UseShellExecute = true });
    }
}
