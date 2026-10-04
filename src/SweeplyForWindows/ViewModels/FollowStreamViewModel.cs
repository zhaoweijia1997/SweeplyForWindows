using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Sweeply.Core;
using Sweeply.Core.Capture;
using SweeplyForWindows.Localization;

namespace SweeplyForWindows.ViewModels;

/// <summary>One line of a followed stream: what one end sent (its colour says which), or a note on missing data.</summary>
public sealed class FollowLine
{
    private static readonly Brush FromABrush = Tint(0xE0, 0x3C, 0x3C, 0x2C), FromBBrush = Tint(0x3C, 0x6C, 0xE0, 0x2C);

    public required string Text { get; init; }
    public bool FromA { get; init; }
    public bool IsNote { get; init; }
    public Brush Background => IsNote ? Brushes.Transparent : FromA ? FromABrush : FromBBrush;

    private static Brush Tint(byte r, byte g, byte b, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, r, g, b)); // see-through: reads on light and dark
        brush.Freeze();
        return brush;
    }

    public override string ToString() => Text;
}

/// <summary>
/// The "Follow stream" window: a TCP or UDP conversation put back together, both ends in their own colour, shown as
/// text, as UTF-8 or as hex; copied, saved, or turned into a filter on the capture page.
/// </summary>
public sealed class FollowStreamViewModel : ObservableObject
{
    /// <summary>Lines shown at most; the rest can still be saved.</summary>
    public const int MaxLines = 100_000;
    private const int MaxLineLength = 160;

    private readonly FollowedStream _stream;
    private readonly Action<string> _applyFilter;
    private int _showAs, _direction;
    private IReadOnlyList<FollowLine> _lines = Array.Empty<FollowLine>();

    public FollowStreamViewModel(FollowedStream stream, string? nameA, string? nameB, Action<string> applyFilter)
    {
        _stream = stream;
        _applyFilter = applyFilter;
        var loc = Loc.Instance;
        string a = Endpoint(stream.AddressA, stream.PortA), b = Endpoint(stream.AddressB, stream.PortB);
        EndA = "A · " + (nameA is null ? a : $"{a} ({nameA})");
        EndB = "B · " + (nameB is null ? b : $"{b} ({nameB})");
        Title = loc.Format("cap.follow.title", stream.Protocol, a, b);
        var culture = loc.Culture;
        string summary = loc.Format("cap.follow.summary", SizeFormatter.Format(stream.BytesFromA, culture), SizeFormatter.Format(stream.BytesFromB, culture),
            stream.Packets.ToString("N0", culture));
        if (stream.Missing > 0) summary += " · " + loc.Format("cap.follow.missing", SizeFormatter.Format(stream.Missing, culture));
        if (stream.Truncated) summary += " · " + loc.Format("cap.follow.truncated", SizeFormatter.Format(StreamFollower.MaxBytes, culture));
        Summary = summary;
        // A TLS record (handshake, 0x16 0x03 …) at the start: what follows is encrypted.
        IsEncrypted = stream.Chunks.Count > 0 && stream.Chunks[0].Data is [0x16, 0x03, ..];
        CopyCommand = new RelayCommand(_ => Copy());
        SaveCommand = new RelayCommand(_ => Save());
        FilterCommand = new RelayCommand(_ => _applyFilter(_stream.Filter));
        Build();
    }

    public string Title { get; }
    public string EndA { get; }
    public string EndB { get; }
    public string Summary { get; }

    /// <summary>The conversation starts with a TLS handshake, so its content can't be read (that's how it should be).</summary>
    public bool IsEncrypted { get; }

    /// <summary>0 text (ASCII, other bytes as dots), 1 UTF-8, 2 hex dump.</summary>
    public int ShowAs
    {
        get => _showAs;
        set { if (SetField(ref _showAs, value)) Build(); }
    }

    /// <summary>0 both ends, 1 only A, 2 only B.</summary>
    public int Direction
    {
        get => _direction;
        set { if (SetField(ref _direction, value)) Build(); }
    }

    public IReadOnlyList<string> ShowAsChoices => new[] { Loc.Instance["cap.follow.ascii"], Loc.Instance["cap.follow.utf8"], Loc.Instance["cap.follow.hex"] };
    public IReadOnlyList<string> DirectionChoices => new[] { Loc.Instance["cap.follow.both"], Loc.Instance["cap.follow.onlyA"], Loc.Instance["cap.follow.onlyB"] };

    public IReadOnlyList<FollowLine> Lines { get => _lines; private set => SetField(ref _lines, value); }

    public ICommand CopyCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand FilterCommand { get; }

    /// <summary>Set by the window: where to save the bytes.</summary>
    public Func<string, string?>? PickSaveFile { get; set; }

    private bool? Only => _direction switch { 1 => true, 2 => false, _ => null };

    private void Build()
    {
        var lines = new List<FollowLine>();
        long hexOffsetA = 0, hexOffsetB = 0;
        foreach (var chunk in _stream.Chunks)
        {
            if (Only is bool only && chunk.FromA != only) continue;
            if (lines.Count >= MaxLines) break;
            if (chunk.MissingBefore > 0)
                lines.Add(new FollowLine { Text = Loc.Instance.Format("cap.follow.gap", SizeFormatter.Format(chunk.MissingBefore, Loc.Instance.Culture)), IsNote = true });
            if (_showAs == 2)
            {
                ref long offset = ref chunk.FromA ? ref hexOffsetA : ref hexOffsetB;
                for (int at = 0; at < chunk.Data.Length && lines.Count < MaxLines; at += 16)
                {
                    lines.Add(new FollowLine { Text = HexLine(chunk.Data, at, offset + at), FromA = chunk.FromA });
                }
                offset += chunk.Data.Length;
                continue;
            }
            string text = _showAs == 1 ? Encoding.UTF8.GetString(chunk.Data) : Ascii(chunk.Data);
            foreach (var line in Split(text))
            {
                if (lines.Count >= MaxLines) break;
                lines.Add(new FollowLine { Text = line, FromA = chunk.FromA });
            }
        }
        if (lines.Count >= MaxLines) lines.Add(new FollowLine { Text = Loc.Instance.Format("cap.follow.more", MaxLines.ToString("N0", Loc.Instance.Culture)), IsNote = true });
        Lines = lines;
    }

    /// <summary>Printable ASCII as it is, line breaks as line breaks, every other byte as a dot (as Wireshark shows it).</summary>
    private static string Ascii(byte[] data)
    {
        var text = new StringBuilder(data.Length);
        foreach (byte b in data) text.Append(b is >= 0x20 and < 0x7F or (byte)'\n' or (byte)'\t' ? (char)b : b == '\r' ? '\r' : '.');
        return text.ToString();
    }

    /// <summary>Lines split at line breaks; long ones wrapped, so no line is too long to show.</summary>
    private static IEnumerable<string> Split(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Replace('\t', ' ');
            if (line.Length <= MaxLineLength)
            {
                yield return line;
                continue;
            }
            for (int at = 0; at < line.Length; at += MaxLineLength)
                yield return line.Substring(at, Math.Min(MaxLineLength, line.Length - at));
        }
    }

    private static string HexLine(byte[] data, int at, long offset)
    {
        var line = new StringBuilder(80);
        line.Append(offset.ToString("x8", CultureInfo.InvariantCulture)).Append("  ");
        for (int i = 0; i < 16; i++)
        {
            if (i == 8) line.Append(' ');
            line.Append(at + i < data.Length ? data[at + i].ToString("x2", CultureInfo.InvariantCulture) + " " : "   ");
        }
        line.Append(' ');
        for (int i = 0; i < 16 && at + i < data.Length; i++)
        {
            byte b = data[at + i];
            line.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }
        return line.ToString();
    }

    private static string Endpoint(System.Net.IPAddress address, int port) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";

    private void Copy()
    {
        string text = string.Join(Environment.NewLine, Lines.Select(l => l.Text));
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { } // the clipboard is busy
    }

    private void Save()
    {
        string suggested = $"{_stream.Protocol.ToLowerInvariant()}-stream-{_stream.Stream}{(Only is null ? "" : Only == true ? "-a" : "-b")}.bin";
        if (PickSaveFile?.Invoke(suggested) is not string path) return;
        try { File.WriteAllBytes(path, _stream.Bytes(Only)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SaveError = e.Message; }
    }

    private string _saveError = "";
    public string SaveError { get => _saveError; private set => SetField(ref _saveError, value); }
}
