using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Sweeply.Core.Monitoring;
using SweeplyForWindows.Localization;
using SweeplyForWindows.Platform;

namespace SweeplyForWindows.ViewModels;

/// <summary>A connection just seen for the first time, one that is there, or one that just went away.</summary>
public enum RowStatus
{
    Normal,
    New,
    Closed,
}

/// <summary>One connection on the Connections section.</summary>
public sealed class ConnectionRow : ObservableObject
{
    private string _stateText = "", _remoteNote = "";
    private RowStatus _status;

    public ConnectionRow(Connection connection, ProgramInfo program)
    {
        Connection = connection;
        Program = program;
        bool v6 = connection.LocalAddress.AddressFamily == AddressFamily.InterNetworkV6;
        ProtocolText = (connection.Protocol == TransportProtocol.Tcp ? "TCP" : "UDP") + (v6 ? "v6" : "");
        LocalText = Endpoint(connection.LocalAddress, connection.LocalPort);
        RemoteText = connection.RemoteAddress is { } remote && !connection.IsListening ? Endpoint(remote, connection.RemotePort) : "";
    }

    internal Connection Connection { get; private set; }
    internal ProgramInfo Program { get; }
    internal DateTime ChangedUtc { get; set; }

    public ImageSource? Icon { get; set; }
    public string ProgramName => Program.Name.Length > 0 ? Program.Name : "—";

    /// <summary>
    /// For svchost.exe: the services it runs, which say much more than the name; by the names Windows shows for them
    /// ("Windows Update", in Windows' language), the short ones ("wuauserv") in the tip.
    /// </summary>
    public string ServicesText => Program.Services.Count == 0 ? "" : string.Join(", ", Program.Services.Take(3).Select(s => s.DisplayName)) + (Program.Services.Count > 3 ? " …" : "");

    public string ProgramTip => string.Join("\n", new[]
    {
        Program.Path ?? Program.Name,
        Loc.Instance.Format("conn.pid", Program.ProcessId),
        Program.Services.Count > 0
            ? Loc.Instance.Format("conn.services", string.Concat(Program.Services.Select(s => s.DisplayName == s.Name ? $"\n{s.Name}" : $"\n{s.DisplayName} ({s.Name})")))
            : "",
    }.Where(line => line.Length > 0));

    public string ProtocolText { get; }
    public string LocalText { get; }
    public string RemoteText { get; }
    public string RemoteNote { get => _remoteNote; set => SetField(ref _remoteNote, value); }
    public string StateText { get => _stateText; set => SetField(ref _stateText, value); }
    public RowStatus Status { get => _status; set => SetField(ref _status, value); }

    internal void Update(Connection connection) => Connection = connection;

    /// <summary>"192.0.2.1:443" or "[2001:db8::1]:443", as people write them.</summary>
    internal static string Endpoint(IPAddress address, int port) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";

    internal void RefreshTexts() => OnAllPropertiesChanged();

    /// <summary>Also what screen readers announce for the row.</summary>
    public override string ToString() => string.Join("  ", new[] { ProgramName, ProtocolText, LocalText, RemoteText, StateText }.Where(t => t.Length > 0));
}

/// <summary>
/// The Monitor page's Connections section: which program talks to where, as TCPView shows it. Read every 2 seconds
/// while in view; a connection that appears is green for a few seconds, one that goes away red, then it's gone.
/// Nothing is sent, unless host names are asked for (then the DNS server is asked).
/// </summary>
public sealed class ConnectionsViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Highlight = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NamesKept = TimeSpan.FromMinutes(10);

    public enum SortOrder
    {
        Program,
        Remote,
        Local,
        State,
    }

    private readonly DispatcherTimer _timer = new() { Interval = RefreshEvery };
    private readonly ProgramNames _programs = new();
    private readonly Dictionary<string, ConnectionRow> _rows = new();
    private readonly Dictionary<IPAddress, (string Name, DateTime Utc)> _hostNames = new();
    private readonly HashSet<IPAddress> _looking = new();
    private readonly SemaphoreSlim _lookups = new(4);
    private IReadOnlyList<NetworkAdapter> _adapters = Array.Empty<NetworkAdapter>();
    private DateTime _adaptersUtc = DateTime.MinValue;
    private bool _shown, _reading, _loaded, _sampleMode, _showAll, _lookUpNames, _hideLoopback = true;
    private string _filterText = "", _summaryText = "";
    private SortOrder _sort = SortOrder.Program;

    public ConnectionsViewModel()
    {
        _timer.Tick += (_, _) => _ = RefreshAsync();
        SortCommand = new RelayCommand(p => { if (p is string s && Enum.TryParse<SortOrder>(s, out var order)) Sort = order; });
        FilterProgramCommand = new RelayCommand(p => { if (p is string name) FilterText = name; });
        ClearFilterCommand = new RelayCommand(_ => FilterText = "");
        CopyCommand = new RelayCommand(p => Copy(p as ConnectionRow, remoteOnly: false));
        CopyRemoteCommand = new RelayCommand(p => Copy(p as ConnectionRow, remoteOnly: true));
        RevealCommand = new RelayCommand(p => Reveal(p as ConnectionRow));
    }

    public ObservableCollection<ConnectionRow> Rows { get; } = new();

    public string FilterText
    {
        get => _filterText;
        set { if (SetField(ref _filterText, value ?? "")) Show(); }
    }

    /// <summary>Also listening ports, UDP endpoints and connections that are closing down.</summary>
    public bool ShowAll
    {
        get => _showAll;
        set { if (SetField(ref _showAll, value)) Show(); }
    }

    public bool HideLoopback
    {
        get => _hideLoopback;
        set { if (SetField(ref _hideLoopback, value)) Show(); }
    }

    /// <summary>Ask the DNS server for the names of remote addresses (off by default: it sends queries).</summary>
    public bool LookUpNames
    {
        get => _lookUpNames;
        set { if (SetField(ref _lookUpNames, value)) Show(); }
    }

    public SortOrder Sort
    {
        get => _sort;
        set { if (SetField(ref _sort, value)) Show(); }
    }

    public string SummaryText { get => _summaryText; private set => SetField(ref _summaryText, value); }
    public bool HasFilter => FilterText.Length > 0;

    public ICommand SortCommand { get; }
    public ICommand FilterProgramCommand { get; }
    public ICommand ClearFilterCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand CopyRemoteCommand { get; }
    public ICommand RevealCommand { get; }

    public void SetShown(bool shown)
    {
        _shown = shown;
        if (_sampleMode) return;
        if (!shown)
        {
            _timer.Stop();
            return;
        }
        _ = RefreshAsync();
        _timer.Start();
    }

    public void Relocalize()
    {
        foreach (var row in _rows.Values)
        {
            row.StateText = StateText(row.Connection);
            row.RemoteNote = RemoteNote(row.Connection);
            row.RefreshTexts();
        }
        Show();
    }

    private async Task RefreshAsync()
    {
        if (_reading) return;
        _reading = true;
        try
        {
            bool adaptersDue = DateTime.UtcNow - _adaptersUtc > TimeSpan.FromSeconds(30);
            var (connections, adapters) = await Task.Run(() =>
            {
                var list = ConnectionTable.Read();
                foreach (var c in list) _programs.Get(c.ProcessId); // names are read here, off the UI thread
                return (list, adaptersDue ? NetworkAdapters.Read() : null);
            });
            if (adapters is not null)
            {
                _adapters = adapters;
                _adaptersUtc = DateTime.UtcNow;
            }
            Merge(connections, DateTime.UtcNow);
        }
        catch (Exception) { } // keep what is shown; the next read tries again
        finally
        {
            _reading = false;
        }
    }

    /// <summary>New connections come in, gone ones are marked and dropped a few seconds later.</summary>
    private void Merge(IReadOnlyList<Connection> connections, DateTime now)
    {
        var seen = new HashSet<string>();
        foreach (var c in connections)
        {
            string key = c.Key;
            if (!seen.Add(key)) continue;
            if (_rows.TryGetValue(key, out var row))
            {
                row.Update(c);
                if (row.Status == RowStatus.Closed) { row.Status = RowStatus.Normal; row.ChangedUtc = now; }
            }
            else
            {
                var program = _programs.Get(c.ProcessId);
                row = new ConnectionRow(c, program)
                {
                    Icon = ProgramIcons.For(program.Path),
                    Status = _loaded ? RowStatus.New : RowStatus.Normal, // the first read is not "new"
                    ChangedUtc = now,
                };
                _rows[key] = row;
            }
            row.StateText = StateText(c);
            row.RemoteNote = RemoteNote(c);
        }
        foreach (var (key, row) in _rows.ToList())
        {
            if (seen.Contains(key))
            {
                if (row.Status == RowStatus.New && now - row.ChangedUtc > Highlight) row.Status = RowStatus.Normal;
                continue;
            }
            if (row.Status != RowStatus.Closed) { row.Status = RowStatus.Closed; row.ChangedUtc = now; }
            else if (now - row.ChangedUtc > Highlight) _rows.Remove(key);
        }
        _loaded = true;
        Show();
    }

    /// <summary>Applies the options, the filter and the order to what is listed, changing as little as possible.</summary>
    private void Show()
    {
        string filter = FilterText.Trim();
        var visible = _rows.Values.Where(r =>
                (ShowAll || (!r.Connection.IsListening && !r.Connection.IsClosed) || r.Status == RowStatus.Closed && !r.Connection.IsListening) &&
                (!HideLoopback || !r.Connection.IsLoopback) &&
                (filter.Length == 0 || Matches(r, filter)))
            .ToList();
        visible.Sort(Compare);
        Sync(Rows, visible);

        int programs = visible.Select(r => r.Program.ProcessId).Distinct().Count();
        SummaryText = Loc.Instance.Format("conn.summary", visible.Count, programs, _rows.Count);
        OnPropertyChanged(nameof(HasFilter));
    }

    private static bool Matches(ConnectionRow row, string filter) =>
        new[] { row.ProgramName, row.LocalText, row.RemoteText, row.RemoteNote, row.StateText, row.ProtocolText }
            .Concat(row.Program.Services.SelectMany(s => new[] { s.Name, s.DisplayName })) // all of them, by either name
            .Any(text => text.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private int Compare(ConnectionRow a, ConnectionRow b)
    {
        int byProgram = string.Compare(a.ProgramName, b.ProgramName, StringComparison.OrdinalIgnoreCase);
        int result = Sort switch
        {
            SortOrder.Remote => string.CompareOrdinal(a.RemoteText, b.RemoteText),
            SortOrder.Local => a.Connection.LocalPort.CompareTo(b.Connection.LocalPort),
            SortOrder.State => string.Compare(a.StateText, b.StateText, StringComparison.CurrentCulture),
            _ => byProgram,
        };
        if (result == 0) result = byProgram;
        if (result == 0) result = string.CompareOrdinal(a.RemoteText, b.RemoteText);
        return result != 0 ? result : string.CompareOrdinal(a.LocalText, b.LocalText);
    }

    /// <summary>Makes <paramref name="target"/> list <paramref name="desired"/> in order with few changes, so nothing jumps.</summary>
    internal static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        var keep = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (int i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            int at = -1;
            for (int k = i + 1; k < target.Count; k++)
                if (ReferenceEquals(target[k], desired[i])) { at = k; break; }
            if (at >= 0) target.Move(at, i);
            else target.Insert(i, desired[i]);
        }
    }

    private static string StateText(Connection c) =>
        c.State is { } state ? Loc.Instance[$"conn.state.{state}"] : "";

    /// <summary>What the remote address is, without asking anyone; or its name when names are asked for.</summary>
    private string RemoteNote(Connection c)
    {
        if (c.RemoteAddress is not { } remote || c.IsListening) return "";
        if (remote.Equals(IPAddress.Any) || remote.Equals(IPAddress.IPv6Any)) return "";
        if (IPAddress.IsLoopback(remote)) return Loc.Instance["conn.thisPc"];
        if (NetworkTools.IsProxyFakeAddress(remote)) return Loc.Instance["conn.fake"];
        if (LookUpNames)
        {
            if (_hostNames.TryGetValue(remote, out var known) && DateTime.UtcNow - known.Utc < NamesKept)
            {
                if (known.Name.Length > 0) return known.Name;
            }
            else
            {
                _ = LookUpAsync(remote);
            }
        }
        return NetworkAdapters.IsOnLocalNetwork(remote, _adapters) ? Loc.Instance["conn.lan"] : "";
    }

    private async Task LookUpAsync(IPAddress address)
    {
        if (!_looking.Add(address)) return;
        try
        {
            await _lookups.WaitAsync();
            string? name;
            try { name = await NetworkTools.NameOfAsync(address, CancellationToken.None); }
            finally { _lookups.Release(); }
            _hostNames[address] = (name ?? "", DateTime.UtcNow);
            if (_hostNames.Count > 2000) _hostNames.Clear();
            foreach (var row in _rows.Values.Where(r => address.Equals(r.Connection.RemoteAddress)))
                row.RemoteNote = RemoteNote(row.Connection);
        }
        finally
        {
            _looking.Remove(address);
        }
    }

    private static void Copy(ConnectionRow? row, bool remoteOnly)
    {
        if (row is null) return;
        string text = remoteOnly ? row.RemoteText
            : string.Join("\t", new[] { row.ProgramName, row.ProtocolText, row.LocalText, row.RemoteText, row.RemoteNote, row.StateText }.Where(t => t.Length > 0));
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { } // the clipboard is busy
    }

    private static void Reveal(ConnectionRow? row)
    {
        if (row?.Program.Path is not { } path || !System.IO.File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>Made-up connections for screenshots: documentation addresses, no real programs' paths.</summary>
    public void LoadSample()
    {
        _sampleMode = true;
        var now = DateTime.UtcNow;
        IPAddress Ip(string s) => IPAddress.Parse(s);
        var own = Ip("192.0.2.23");
        // The names Windows shows for services, as a Chinese or an English Windows has them.
        bool chinese = Loc.Instance.Culture.Name == "zh-Hans";
        var dnsClient = new ServiceInfo("Dnscache", "DNS Client");
        var location = new ServiceInfo("nlasvc", chinese ? "网络位置感知" : "Network Location Awareness");
        var update = new ServiceInfo("wuauserv", chinese ? "Windows 更新" : "Windows Update");
        var samples = new (string Program, int Pid, ServiceInfo[] Services, string Remote, int Port, int Local, TcpConnectionState State, RowStatus Status, string Note)[]
        {
            ("chrome.exe", 9812, Array.Empty<ServiceInfo>(), "203.0.113.10", 443, 52211, TcpConnectionState.Established, RowStatus.Normal, "www.example.com"),
            ("chrome.exe", 9812, Array.Empty<ServiceInfo>(), "203.0.113.24", 443, 52240, TcpConnectionState.Established, RowStatus.New, "cdn.example.net"),
            ("chrome.exe", 9812, Array.Empty<ServiceInfo>(), "198.51.100.80", 443, 52102, TcpConnectionState.CloseWait, RowStatus.Normal, ""),
            ("Code.exe", 7340, Array.Empty<ServiceInfo>(), "203.0.113.77", 443, 51870, TcpConnectionState.Established, RowStatus.Normal, "update.example.org"),
            ("OneDrive.exe", 6124, Array.Empty<ServiceInfo>(), "198.51.100.33", 443, 51544, TcpConnectionState.Established, RowStatus.Normal, ""),
            ("Spotify.exe", 11420, Array.Empty<ServiceInfo>(), "203.0.113.150", 4070, 52007, TcpConnectionState.Established, RowStatus.Normal, ""),
            ("svchost.exe", 1388, new[] { dnsClient, location }, "192.0.2.1", 53, 50110, TcpConnectionState.SynSent, RowStatus.New, ""),
            ("svchost.exe", 2216, new[] { update }, "198.51.100.200", 443, 50981, TcpConnectionState.Established, RowStatus.Closed, ""),
            ("steam.exe", 15008, Array.Empty<ServiceInfo>(), "192.0.2.47", 27036, 27036, TcpConnectionState.Established, RowStatus.Normal, ""),
        };
        _adapters = new[] { new NetworkAdapter { Id = "wifi", Name = "Wi-Fi", IsUp = true, IPv4 = new[] { (own, 24) } } };
        _rows.Clear();
        foreach (var s in samples)
        {
            var c = new Connection(TransportProtocol.Tcp, own, s.Local, Ip(s.Remote), s.Port, s.State, s.Pid);
            var row = new ConnectionRow(c, new ProgramInfo(s.Pid, s.Program, null, s.Services))
            {
                Icon = ProgramIcons.Generic(), Status = s.Status, ChangedUtc = now,
                StateText = StateText(c),
            };
            row.RemoteNote = s.Note.Length > 0 ? s.Note : RemoteNote(c);
            _rows[c.Key] = row;
        }
        Show();
    }
}
