using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Sweeply.Core;
using Sweeply.Core.Capture;
using SweeplyForWindows.Localization;

namespace SweeplyForWindows.ViewModels;

/// <summary>The packets of the capture page, with what the stream tracker worked out for each, in capture order.</summary>
public sealed class CaptureStore
{
    public List<CapturedPacket> Packets { get; } = new();
    public List<StreamInfo> Streams { get; } = new();
    public List<CaptureInterface> Interfaces { get; } = new();
    public StreamTracker Tracker { get; private set; } = new();
    public long Bytes { get; private set; }

    public DateTime FirstUtc => Packets.Count > 0 ? Packets[0].TimestampUtc : DateTime.UtcNow;

    public void Add(CapturedPacket packet) => Add(packet, Tracker.Process(packet, Packets.Count + 1));

    /// <summary>A packet whose stream information was already worked out (by the store it comes from).</summary>
    public void Add(CapturedPacket packet, StreamInfo stream)
    {
        Streams.Add(stream);
        Packets.Add(packet);
        Bytes += packet.Data.Length;
    }

    /// <summary>Takes over another store's packets and tracker (a file read in the background).</summary>
    public void TakeOver(CaptureStore other)
    {
        Clear();
        Interfaces.AddRange(other.Interfaces);
        for (int i = 0; i < other.Packets.Count; i++) Add(other.Packets[i], other.Streams[i]);
        Tracker = other.Tracker;
    }

    public void Clear()
    {
        Packets.Clear();
        Streams.Clear();
        Interfaces.Clear();
        Tracker = new StreamTracker();
        Bytes = 0;
    }

    public string InterfaceName(CapturedPacket p) => p.InterfaceId >= 0 && p.InterfaceId < Interfaces.Count ? Interfaces[p.InterfaceId].Name : "";

    public Dissection Dissect(int index) => Dissector.Dissect(Packets[index], index + 1, Streams[index], FirstUtc, InterfaceName(Packets[index]));
}

/// <summary>One line of the packet list. The columns are worked out the first time the line is shown.</summary>
public sealed class PacketRow
{
    private static readonly Dictionary<PacketColor, Brush> Brushes = new()
    {
        [PacketColor.Default] = System.Windows.Media.Brushes.Transparent,
        [PacketColor.Broadcast] = System.Windows.Media.Brushes.Transparent,
        [PacketColor.Tcp] = Tint(0x7B, 0x6C, 0xF6, 0x26),
        [PacketColor.TcpSynFin] = Tint(0x80, 0x80, 0x80, 0x48),
        [PacketColor.TcpReset] = Tint(0xC4, 0x2B, 0x1C, 0x58),
        [PacketColor.BadTcp] = Tint(0xC4, 0x2B, 0x1C, 0x90),
        [PacketColor.Udp] = Tint(0x3A, 0x96, 0xDD, 0x2E),
        [PacketColor.Arp] = Tint(0xE2, 0xB0, 0x4A, 0x45),
        [PacketColor.Icmp] = Tint(0xE3, 0x6C, 0xE6, 0x30),
        [PacketColor.IcmpError] = Tint(0x6B, 0x3F, 0xA0, 0x60),
        [PacketColor.Http] = Tint(0x6C, 0xCB, 0x5F, 0x40),
    };

    private readonly CaptureStore _store;
    private string? _source, _destination, _protocol, _info;
    private Brush? _background;

    public PacketRow(CaptureStore store, int index)
    {
        _store = store;
        Index = index;
    }

    public int Index { get; }
    public int Number => Index + 1;
    private CapturedPacket Packet => _store.Packets[Index];

    public string Time => Format.Seconds((Packet.TimestampUtc - _store.FirstUtc).TotalSeconds);
    public string Program => Packet.ProcessName ?? "";
    public string Length => Packet.Length.ToString(CultureInfo.InvariantCulture);
    public string Source { get { Summarize(); return _source!; } }
    public string Destination { get { Summarize(); return _destination!; } }
    public string Protocol { get { Summarize(); return _protocol!; } }
    public string Info { get { Summarize(); return _info!; } }
    public Brush Background { get { Summarize(); return _background!; } }

    private void Summarize()
    {
        if (_info is not null) return;
        var d = _store.Dissect(Index);
        _source = d.Source;
        _destination = d.Destination;
        _protocol = d.Protocol;
        _info = d.Info.Length > 400 ? d.Info[..400] + "…" : d.Info;
        _background = Brushes[d.Color];
    }

    private static Brush Tint(byte r, byte g, byte b, byte alpha)
    {
        // Semi-transparent, so the same tint reads on the light and the dark theme.
        var brush = new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
        brush.Freeze();
        return brush;
    }

    public override string ToString() => string.Join('\t', Number, Time, Program, Source, Destination, Protocol, Length, Info);
}

/// <summary>A line of the packet details, expandable.</summary>
public sealed class DetailNode : ObservableObject
{
    private bool _isExpanded, _isSelected;

    public DetailNode(ProtoNode node, DetailNode? parent, string key)
    {
        Node = node;
        Parent = parent;
        Key = key;
        Children = node.Children is null ? Array.Empty<DetailNode>()
            : node.Children.Select((c, i) => new DetailNode(c, this, key + "/" + KeyOf(c, i))).ToList();
    }

    public ProtoNode Node { get; }
    public DetailNode? Parent { get; }

    /// <summary>The same line in another packet: by protocol, field name or label, not by position.</summary>
    public string Key { get; }

    public string Text => Node.Text;
    public bool IsWarning => Node.IsWarning;
    public bool IsLayer => Node.IsLayer;
    public IReadOnlyList<DetailNode> Children { get; }
    public bool IsExpanded { get => _isExpanded; set => SetField(ref _isExpanded, value); }
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }

    private static string KeyOf(ProtoNode n, int i)
    {
        if (n.Field is { } field) return field;
        int colon = n.Text.IndexOf(':');
        return colon > 0 ? n.Text[..colon] : n.Text.Length > 40 ? n.Text[..40] : n.Text;
    }

    public IEnumerable<DetailNode> Walk()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.Walk())
                yield return node;
    }

    /// <summary>Also what screen readers announce for the line.</summary>
    public override string ToString() => Text;
}

/// <summary>
/// The capture page: a capture file opened (or, later, a live capture) shown as Wireshark shows it — the packet
/// list, the selected packet's details and its bytes. Nothing is read from the network here; files are only read
/// when the user opens them, and only written when they save.
/// </summary>
public sealed class CaptureViewModel : ObservableObject
{
    /// <summary>The most a capture keeps (in packets and bytes); a file with more is read up to that.</summary>
    public const int MaxPackets = 200_000;
    public const long MaxBytes = 256L * 1024 * 1024;

    private readonly HashSet<string> _expanded = new(); // what the user opened, kept from packet to packet
    private PacketRow? _selected;
    private DetailNode? _selectedNode;
    private byte[]? _bytes;
    private int _highlightStart = -1, _highlightLength;
    private string _statusText = "", _fileName = "", _note = "";
    private bool _isLoading;
    private IReadOnlyList<DetailNode> _details = Array.Empty<DetailNode>();

    public CaptureViewModel()
    {
        OpenCommand = new RelayCommand(_ => { if (PickOpenFile?.Invoke() is string path) _ = OpenAsync(path); }, () => !IsLoading);
        SaveCommand = new RelayCommand(_ => Save(), () => !IsLoading && Store.Packets.Count > 0);
        CopyRowCommand = new RelayCommand(p => Copy((p as PacketRow ?? Selected)?.ToString()));
        CopyBytesCommand = new RelayCommand(p => { if ((p as PacketRow ?? Selected) is { } row) Copy(Format.Hex(Store.Packets[row.Index].Data, int.MaxValue)); });
        CopyNodeCommand = new RelayCommand(p => Copy((p as DetailNode ?? SelectedNode)?.Text));
        ByteClickedCommand = new RelayCommand(p => { if (p is int offset) SelectByte(offset); });
        ExpandAllCommand = new RelayCommand(_ => SetAllExpanded(true));
        CollapseAllCommand = new RelayCommand(_ => SetAllExpanded(false));
    }

    public CaptureStore Store { get; } = new();

    /// <summary>The packets shown; replaced as a whole when a file is opened (one change instead of thousands).</summary>
    public ObservableCollection<PacketRow> Rows { get; private set; } = new();

    public PacketRow? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value)) return;
            ShowDetails();
        }
    }

    public IReadOnlyList<DetailNode> Details { get => _details; private set => SetField(ref _details, value); }

    public DetailNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!SetField(ref _selectedNode, value)) return;
            HighlightStart = value?.Node.Offset ?? -1;
            HighlightLength = value is { Node.Offset: >= 0 } ? value.Node.Length : 0;
        }
    }

    public byte[]? Bytes { get => _bytes; private set => SetField(ref _bytes, value); }
    public int HighlightStart { get => _highlightStart; private set => SetField(ref _highlightStart, value); }
    public int HighlightLength { get => _highlightLength; private set => SetField(ref _highlightLength, value); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string FileName { get => _fileName; private set => SetField(ref _fileName, value); }

    /// <summary>A line about the capture (cut short, read only in part); empty when there's nothing to say.</summary>
    public string Note { get => _note; private set => SetField(ref _note, value); }

    public bool IsLoading
    {
        get => _isLoading;
        private set { if (SetField(ref _isLoading, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public bool HasPackets => Store.Packets.Count > 0;

    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CopyRowCommand { get; }
    public ICommand CopyBytesCommand { get; }
    public ICommand CopyNodeCommand { get; }
    public ICommand ByteClickedCommand { get; }
    public ICommand ExpandAllCommand { get; }
    public ICommand CollapseAllCommand { get; }

    /// <summary>Set by the window: asks for a capture file to open, or where to save.</summary>
    public Func<string?>? PickOpenFile { get; set; }
    public Func<string, string?>? PickSaveFile { get; set; }

    /// <summary>Opens a pcap or pcapng file (from the button, a drop on the page, or "--open-capture").</summary>
    public async Task OpenAsync(string path)
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusText = Loc.Instance.Format("cap.opening", Path.GetFileName(path));
        try
        {
            var (file, store) = await Task.Run(() =>
            {
                var read = PcapReader.Read(path, new PcapReader.Limits(MaxPackets, MaxBytes));
                var built = new CaptureStore();
                built.Interfaces.AddRange(read.Interfaces);
                foreach (var p in read.Packets) built.Add(p);
                return (read, built);
            });
            Replace(store);
            FileName = Path.GetFileName(path);
            Note = file.Truncated ? Loc.Instance.Format("cap.truncated", file.Packets.Count) : "";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OutOfMemoryException)
        {
            Note = Loc.Instance.Format("cap.openFailed", Path.GetFileName(path), e.Message);
        }
        finally
        {
            IsLoading = false;
            UpdateStatus();
        }
    }

    private void Replace(CaptureStore loaded)
    {
        Selected = null;
        Store.TakeOver(loaded);
        Rows = new ObservableCollection<PacketRow>(Enumerable.Range(0, Store.Packets.Count).Select(i => new PacketRow(Store, i)));
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(HasPackets));
        Selected = Rows.FirstOrDefault();
    }

    private void Save()
    {
        string suggested = FileName.Length > 0 ? Path.ChangeExtension(FileName, ".pcapng") : $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.pcapng";
        if (PickSaveFile?.Invoke(suggested) is not string path) return;
        try
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var writer = new PcapNgWriter(stream, "SweeplyForWindows");
            foreach (var packet in Store.Packets)
            {
                string name = Store.InterfaceName(packet);
                writer.Write(packet, writer.Interface(packet.Link, name));
            }
            Note = Loc.Instance.Format("cap.saved", Path.GetFileName(path), Store.Packets.Count);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Note = Loc.Instance.Format("cap.saveFailed", e.Message);
        }
    }

    private void ShowDetails()
    {
        if (Selected is not { } row)
        {
            Details = Array.Empty<DetailNode>();
            Bytes = null;
            SelectedNode = null;
            return;
        }
        var d = Store.Dissect(row.Index);
        var nodes = d.Layers.Select(l => new DetailNode(l, null, l.Field ?? l.Text)).ToList();
        foreach (var node in nodes.SelectMany(n => n.Walk()))
            node.IsExpanded = _expanded.Contains(node.Key);
        foreach (var node in nodes.SelectMany(n => n.Walk()))
            node.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != nameof(DetailNode.IsExpanded) || s is not DetailNode n) return;
                if (n.IsExpanded) _expanded.Add(n.Key);
                else _expanded.Remove(n.Key);
            };
        Details = nodes;
        Bytes = Store.Packets[row.Index].Data;
        SelectedNode = null;
        HighlightStart = -1;
        HighlightLength = 0;
    }

    /// <summary>A byte was clicked in the hex view: select the smallest field that covers it, opening its parents.</summary>
    private void SelectByte(int offset)
    {
        DetailNode? best = null;
        foreach (var node in Details.SelectMany(n => n.Walk()))
        {
            if (node.Node.Offset < 0 || offset < node.Node.Offset || offset >= node.Node.Offset + node.Node.Length) continue;
            if (best is null || node.Node.Length <= best.Node.Length) best = node;
        }
        if (best is null) return;
        for (var p = best.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
        if (SelectedNode is { } old) old.IsSelected = false;
        best.IsSelected = true;
        SelectedNode = best;
    }

    private void SetAllExpanded(bool expanded)
    {
        foreach (var node in Details.SelectMany(n => n.Walk()).Where(n => n.Children.Count > 0)) node.IsExpanded = expanded;
    }

    private void UpdateStatus()
    {
        var culture = Loc.Instance.Culture;
        StatusText = Store.Packets.Count == 0 ? "" : Loc.Instance.Format("cap.status", Store.Packets.Count.ToString("N0", culture), SizeFormatter.Format(Store.Bytes, culture));
        CommandManager.InvalidateRequerySuggested();
    }

    public void Relocalize() => UpdateStatus();

    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { } // the clipboard is busy
    }

    /// <summary>Made-up packets for screenshots (documentation addresses only), as if a capture file was open.</summary>
    public void LoadSample()
    {
        var store = new CaptureStore();
        store.Interfaces.Add(new CaptureInterface(LinkType.Ethernet, "Wi-Fi"));
        foreach (var packet in SamplePackets.Build()) store.Add(packet);
        Replace(store);
        FileName = "example.pcapng";
        Selected = Rows.Count > 5 ? Rows[5] : Rows.FirstOrDefault();
        foreach (var node in Details) node.IsExpanded = node.Node.Field is "tls";
        UpdateStatus();
    }
}
