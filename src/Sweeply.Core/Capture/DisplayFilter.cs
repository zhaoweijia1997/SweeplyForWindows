using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Sweeply.Core.Capture;

public enum FilterError
{
    /// <summary>Something that doesn't fit where it is.</summary>
    Unexpected,

    /// <summary>The filter stops in the middle of something.</summary>
    UnexpectedEnd,

    /// <summary>A value that is neither a number, an address, bytes nor text.</summary>
    BadValue,

    /// <summary>The regular expression after "matches" is wrong.</summary>
    BadRegex,

    /// <summary>A string, parenthesis or set that is opened and never closed.</summary>
    Unclosed,
}

/// <summary>Why a display filter can't be read: what kind of mistake, where it is (0-based), and the text there.</summary>
public sealed class FilterSyntaxException(FilterError error, int position, string near) : Exception($"{error} at {position}: {near}")
{
    public FilterError Error { get; } = error;
    public int Position { get; } = position;
    public string Near { get; } = near;
}

/// <summary>
/// A Wireshark display filter — the part people use: protocols and fields on their own ("dns",
/// "tcp.analysis.retransmission"), comparisons (== != &gt; &lt; &gt;= &lt;=, also written eq ne gt lt ge le,
/// contains, matches or ~, in {…} with ranges a..b) joined by and/or/xor/not (&amp;&amp; || ^^ !) and parentheses.
/// Values are numbers (10, 0x1f, 1.5), "text", IPv4/IPv6 addresses with an optional /prefix, bytes or MAC addresses
/// (aa:bb:cc) and true/false. As in Wireshark, a field that occurs more than once matches if any occurrence does,
/// "a != b" means "not (a == b)", text comparisons are case-sensitive and "matches" is not; "ip.addr", "tcp.port"
/// and the like stand for either end. This app adds "process.name" (without regard to case, like Windows file
/// names) and "process.pid", the program a packet belongs to.
/// </summary>
public sealed class DisplayFilter
{
    /// <summary>Names that stand for either end, as Wireshark's "ip.addr" does.</summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
    {
        ["ip.addr"] = new[] { "ip.src", "ip.dst" },
        ["ipv6.addr"] = new[] { "ipv6.src", "ipv6.dst" },
        ["tcp.port"] = new[] { "tcp.srcport", "tcp.dstport" },
        ["udp.port"] = new[] { "udp.srcport", "udp.dstport" },
        ["eth.addr"] = new[] { "eth.src", "eth.dst" },
    };

    public const string ProcessName = "process.name", ProcessId = "process.pid", FrameComment = "frame.comment";

    private readonly Node _root;
    private readonly HashSet<string> _needed;

    private DisplayFilter(string text, Node root, List<string> fields)
    {
        Text = text;
        _root = root;
        Fields = fields;
        _needed = fields.SelectMany(f => Aliases.TryGetValue(f, out var real) ? real : new[] { f }).ToHashSet(StringComparer.Ordinal);
    }

    public string Text { get; }

    /// <summary>The fields and protocols the filter names, in order of first use (as written, aliases included).</summary>
    public IReadOnlyList<string> Fields { get; }

    /// <summary>Reads a filter; throws <see cref="FilterSyntaxException"/> with where and why when it can't.</summary>
    public static DisplayFilter Parse(string text)
    {
        var parser = new Parser(Tokenize(text));
        var root = parser.ParseAll();
        return new DisplayFilter(text.Trim(), root, parser.Fields);
    }

    /// <summary>
    /// Whether the packet passes. <paramref name="seen"/> (as long as <see cref="Fields"/>), when given, gets true
    /// for each field the packet has, to tell later which names never occurred at all (likely misspelt).
    /// </summary>
    public bool Matches(Dissection dissection, CapturedPacket packet, bool[]? seen = null)
    {
        var values = new Values(_needed);
        foreach (var layer in dissection.Layers)
            foreach (var node in layer.Walk())
                if (node.Field is { } field && _needed.Contains(field)) values.Add(field, node.Value);
        foreach (var protocol in dissection.Protocols)
            if (_needed.Contains(protocol)) values.Add(protocol, null);
        if (_needed.Contains(ProcessName) && packet.ProcessName is { } name) values.Add(ProcessName, name);
        if (_needed.Contains(ProcessId) && packet.ProcessId > 0) values.Add(ProcessId, packet.ProcessId);
        if (_needed.Contains(FrameComment) && packet.Comment is { } comment) values.Add(FrameComment, comment);
        if (seen is not null)
            for (int i = 0; i < Fields.Count && i < seen.Length; i++)
                if (!seen[i] && values.Has(Fields[i])) seen[i] = true;
        return _root.Eval(values);
    }

    /// <summary>A filter that picks this line of the details: "tcp.dstport == 443", "dns.qry.name == \"example.com\"", "tls".</summary>
    public static string? For(ProtoNode node)
    {
        if (node.Field is not { } field) return null;
        if (node.IsLayer || node.Value is null) return field;
        // A note shown in brackets ([TCP Retransmission]) is there or not; a real 1-bit field compares to 1.
        if (node.Value is true && field.Contains(".analysis.", StringComparison.Ordinal)) return field;
        return $"{field} == {WriteValue(node.Value)}";
    }

    /// <summary>A value written so that <see cref="Parse"/> reads it back as the same value.</summary>
    public static string WriteValue(object value) => value switch
    {
        bool b => b ? "1" : "0",
        string s => Quote(s),
        IPAddress address => address.ToString(),
        byte[] bytes => bytes.Length == 0 ? "\"\"" : string.Join(":", bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture))),
        DateTime time => Quote(time.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture)),
        double real => real.ToString("R", CultureInfo.InvariantCulture), // reads back as exactly the same number
        float real => real.ToString("R", CultureInfo.InvariantCulture),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => Quote(value.ToString() ?? ""),
    };

    public static string Quote(string text)
    {
        var quoted = new StringBuilder(text.Length + 2).Append('"');
        foreach (char c in text)
            quoted.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when c < ' ' => $"\\x{(int)c:x2}",
                _ => c.ToString(),
            });
        return quoted.Append('"').ToString();
    }

    // ---------------------------------------------------------------- values of one packet

    private sealed class Values(HashSet<string> needed)
    {
        private readonly Dictionary<string, List<object?>> _map = new(needed.Count, StringComparer.Ordinal);

        public void Add(string field, object? value)
        {
            if (!_map.TryGetValue(field, out var list)) _map[field] = list = new List<object?>(1);
            list.Add(value);
        }

        public bool Has(string field) =>
            Aliases.TryGetValue(field, out var real) ? real.Any(_map.ContainsKey) : _map.ContainsKey(field);

        public IEnumerable<object?> Of(string field)
        {
            if (Aliases.TryGetValue(field, out var real))
            {
                foreach (var name in real)
                    if (_map.TryGetValue(name, out var list))
                        foreach (var v in list) yield return v;
            }
            else if (_map.TryGetValue(field, out var list))
                foreach (var v in list) yield return v;
        }
    }

    // ---------------------------------------------------------------- the tree

    private abstract class Node
    {
        public abstract bool Eval(Values values);
    }

    private sealed class And(Node a, Node b) : Node
    {
        public override bool Eval(Values values) => a.Eval(values) && b.Eval(values);
    }

    private sealed class Or(Node a, Node b) : Node
    {
        public override bool Eval(Values values) => a.Eval(values) || b.Eval(values);
    }

    private sealed class Xor(Node a, Node b) : Node
    {
        public override bool Eval(Values values) => a.Eval(values) ^ b.Eval(values);
    }

    private sealed class Not(Node a) : Node
    {
        public override bool Eval(Values values) => !a.Eval(values);
    }

    private sealed class Exists(string field) : Node
    {
        public override bool Eval(Values values) => values.Has(field);
    }

    private enum Op { Eq, Ne, Gt, Lt, Ge, Le, Contains, Matches }

    private sealed class Compare(string field, Op op, Literal literal) : Node
    {
        private readonly bool _ignoreCase = field == ProcessName;

        public override bool Eval(Values values)
        {
            // "a != b" is "not (a == b)": true when no occurrence equals (also when there is none).
            if (op == Op.Ne) return !values.Of(field).Any(v => Test(v, Op.Eq));
            foreach (var v in values.Of(field))
                if (Test(v, op)) return true;
            return false;
        }

        /// <summary>One occurrence against this comparison (for sets).</summary>
        public bool TestOne(object? value) => Test(value, op);

        private bool Test(object? value, Op test)
        {
            if (value is null) return false;
            if (test == Op.Matches)
            {
                try { return literal.Pattern!.IsMatch(TextOf(value)); }
                catch (RegexMatchTimeoutException) { return false; } // a pattern that takes forever on this text
            }
            if (test == Op.Contains) return Contains(value);
            int? order = CompareTo(value);
            if (order is not int c) return false;
            return test switch
            {
                Op.Eq => c == 0,
                Op.Gt => c > 0,
                Op.Lt => c < 0,
                Op.Ge => c >= 0,
                Op.Le => c <= 0,
                _ => false,
            };
        }

        private bool Contains(object value)
        {
            if (value is byte[] bytes)
            {
                var needle = literal.Bytes ?? Encoding.UTF8.GetBytes(literal.Text);
                return needle.Length > 0 && bytes.AsSpan().IndexOf(needle) >= 0;
            }
            return TextOf(value).Contains(literal.Text, _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        /// <summary>How the value compares with the literal, read the way the value's type asks; null when they can't be compared.</summary>
        private int? CompareTo(object value)
        {
            switch (value)
            {
                case bool b:
                    return literal.Number is decimal n ? (b ? 1m : 0m).CompareTo(n) : null;
                case IPAddress address:
                    if (literal.Address is not { } other)
                        return IPAddress.TryParse(literal.Text, out var quoted) && Normalize(quoted).Equals(Normalize(address)) ? 0 : null;
                    if (literal.Prefix >= 0) return InSubnet(address, other, literal.Prefix) ? 0 : 1; // equal = inside the subnet
                    return CompareBytes(Normalize(address).GetAddressBytes(), Normalize(other).GetAddressBytes());
                case byte[] bytes:
                    var expected = literal.Bytes ?? (literal.IsString ? Encoding.UTF8.GetBytes(literal.Text) : null);
                    return expected is null ? null : CompareBytes(bytes, expected);
                case string text:
                    return _ignoreCase ? string.Compare(text, literal.Text, StringComparison.OrdinalIgnoreCase) : string.CompareOrdinal(text, literal.Text);
                case DateTime time:
                    return DateTime.TryParse(literal.Text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? time.CompareTo(at) : null;
                case double or float:
                    return literal.Real is double r ? Convert.ToDouble(value, CultureInfo.InvariantCulture).CompareTo(r) : null;
                case IConvertible when value is sbyte or byte or short or ushort or int or uint or long or ulong or decimal:
                    if (literal.Number is decimal whole) return Convert.ToDecimal(value, CultureInfo.InvariantCulture).CompareTo(whole);
                    return literal.Real is double real ? Convert.ToDouble(value, CultureInfo.InvariantCulture).CompareTo(real) : null;
                default:
                    return string.CompareOrdinal(TextOf(value), literal.Text);
            }
        }

        private static string TextOf(object value) => value as string ?? Format.Value(value);
    }

    private sealed class In(string field, List<(Literal From, Literal? To)> members) : Node
    {
        private readonly List<(Compare Low, Compare? High)> _tests = members.Select(m => m.To is { } to
            ? (new Compare(field, Op.Ge, m.From), new Compare(field, Op.Le, to))
            : (new Compare(field, Op.Eq, m.From), (Compare?)null)).ToList();

        // Each occurrence is tested on its own: tcp.port in {80..90} is true for 85, not for "70 and 100".
        public override bool Eval(Values values)
        {
            foreach (var v in values.Of(field))
                foreach (var (low, high) in _tests)
                    if (low.TestOne(v) && (high is null || high.TestOne(v))) return true;
            return false;
        }
    }

    private static int CompareBytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool InSubnet(IPAddress address, IPAddress network, int prefix)
    {
        var a = Normalize(address).GetAddressBytes();
        var n = Normalize(network).GetAddressBytes();
        if (a.Length != n.Length) return false;
        int full = prefix / 8, rest = prefix % 8;
        if (!a.AsSpan(0, full).SequenceEqual(n.AsSpan(0, full))) return false;
        if (rest == 0) return true;
        int mask = 0xFF << (8 - rest) & 0xFF;
        return (a[full] & mask) == (n[full] & mask);
    }

    // ---------------------------------------------------------------- values as written

    /// <summary>
    /// A value from the filter, with every way it can be read: "aa:bb:cc:dd:ee:ff:00:11" is both bytes and an IPv6
    /// address; which one counts depends on the field it is compared with.
    /// </summary>
    private sealed class Literal
    {
        public required string Text { get; init; }
        public bool IsString { get; init; }
        public decimal? Number { get; init; }
        public double? Real { get; init; }
        public IPAddress? Address { get; init; }
        public int Prefix { get; init; } = -1;
        public byte[]? Bytes { get; init; }
        public Regex? Pattern { get; set; }
    }

    private static readonly Regex Ipv4Pattern = new(@"^\d{1,3}(\.\d{1,3}){3}(/\d{1,2})?$", RegexOptions.CultureInvariant);
    private static readonly Regex BytesPattern = new(@"^[0-9A-Fa-f]{2}([:\-.][0-9A-Fa-f]{2})+$", RegexOptions.CultureInvariant);

    private static Literal ReadValue(Token token)
    {
        if (token.Kind == Kind.String) return new Literal { Text = token.Text, IsString = true };
        string word = token.Text;
        decimal? number = null;
        double? real = null;
        IPAddress? address = null;
        int prefix = -1;
        byte[]? bytes = null;

        if (word.Equals("true", StringComparison.OrdinalIgnoreCase)) { number = 1; real = 1; }
        else if (word.Equals("false", StringComparison.OrdinalIgnoreCase)) { number = 0; real = 0; }

        if (word.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(word.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hex))
        {
            number = hex;
            real = hex;
        }
        else if (Regex.IsMatch(word, @"^-?\d+$") && decimal.TryParse(word, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
        {
            number = whole;
            real = (double)whole;
        }
        else if (Regex.IsMatch(word, @"^-?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$") && double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double r))
        {
            real = r;
        }

        if (Ipv4Pattern.IsMatch(word))
        {
            string[] parts = word.Split('/');
            if (!IPAddress.TryParse(parts[0], out address) || parts[0].Split('.').Any(p => int.Parse(p, CultureInfo.InvariantCulture) > 255))
                throw new FilterSyntaxException(FilterError.BadValue, token.At, word);
            if (parts.Length == 2)
            {
                prefix = int.Parse(parts[1], CultureInfo.InvariantCulture);
                if (prefix > 32) throw new FilterSyntaxException(FilterError.BadValue, token.At, word);
            }
        }
        else if (word.Contains(':'))
        {
            string[] parts = word.Split('/');
            if (parts.Length <= 2 && IPAddress.TryParse(parts[0], out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6)
            {
                address = v6;
                if (parts.Length == 2)
                {
                    if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > 128)
                        throw new FilterSyntaxException(FilterError.BadValue, token.At, word);
                }
                }
        }
        if (BytesPattern.IsMatch(word))
        {
            bytes = word.Split(':', '-', '.').Select(p => byte.Parse(p, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)).ToArray();
        }
        // Anything else is text written without quotes (http.request.method == GET), as Wireshark allows.
        return new Literal { Text = word, Number = number, Real = real, Address = address, Prefix = prefix, Bytes = bytes };
    }

    // ---------------------------------------------------------------- reading the text

    private enum Kind { Word, String, Op, Open, Close, OpenSet, CloseSet, Comma, End }

    private readonly record struct Token(Kind Kind, string Text, int At);

    private static readonly string[] Operators = { "!==", "===", "==", "!=", ">=", "<=", "&&", "||", "^^", "~=", ">", "<", "!", "~" };

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            int start = i;
            switch (c)
            {
                case '(': tokens.Add(new Token(Kind.Open, "(", i++)); continue;
                case ')': tokens.Add(new Token(Kind.Close, ")", i++)); continue;
                case '{': tokens.Add(new Token(Kind.OpenSet, "{", i++)); continue;
                case '}': tokens.Add(new Token(Kind.CloseSet, "}", i++)); continue;
                case ',': tokens.Add(new Token(Kind.Comma, ",", i++)); continue;
                case '"':
                    tokens.Add(new Token(Kind.String, ReadString(text, ref i), start));
                    continue;
            }
            string? op = Operators.FirstOrDefault(o => string.CompareOrdinal(text, i, o, 0, o.Length) == 0);
            if (op is not null)
            {
                tokens.Add(new Token(Kind.Op, op, i));
                i += op.Length;
                continue;
            }
            if (IsWordChar(c))
            {
                while (i < text.Length && IsWordChar(text[i])) i++;
                tokens.Add(new Token(Kind.Word, text[start..i], start));
                continue;
            }
            throw new FilterSyntaxException(FilterError.Unexpected, i, c.ToString());
        }
        tokens.Add(new Token(Kind.End, "", text.Length));
        return tokens;
    }

    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-' or '/' or '+';

    private static string ReadString(string text, ref int i)
    {
        int start = i++;
        var value = new StringBuilder();
        while (i < text.Length)
        {
            char c = text[i++];
            if (c == '"') return value.ToString();
            if (c != '\\') { value.Append(c); continue; }
            if (i >= text.Length) break;
            char e = text[i++];
            switch (e)
            {
                case 'n': value.Append('\n'); break;
                case 'r': value.Append('\r'); break;
                case 't': value.Append('\t'); break;
                case 'x' when i + 2 <= text.Length && byte.TryParse(text.AsSpan(i, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte b):
                    value.Append((char)b);
                    i += 2;
                    break;
                default: value.Append(e); break; // \" \\ and anything else stand for themselves
            }
        }
        throw new FilterSyntaxException(FilterError.Unclosed, start, "\"");
    }

    private sealed class Parser(List<Token> tokens)
    {
        private static readonly Regex FieldName = new(@"^[A-Za-z_][A-Za-z0-9_\-]*(\.[A-Za-z0-9_\-]+)*$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
            { "and", "or", "xor", "not", "eq", "ne", "gt", "lt", "ge", "le", "contains", "matches", "in" };

        private int _at;

        public List<string> Fields { get; } = new();

        private Token Peek => tokens[_at];

        public Node ParseAll()
        {
            if (Peek.Kind == Kind.End) throw new FilterSyntaxException(FilterError.UnexpectedEnd, Peek.At, "");
            var node = ParseOr();
            if (Peek.Kind != Kind.End) throw Unexpected();
            return node;
        }

        private Node ParseOr()
        {
            var node = ParseXor();
            while (Is("or", "||")) { _at++; node = new Or(node, ParseXor()); }
            return node;
        }

        private Node ParseXor()
        {
            var node = ParseAnd();
            while (Is("xor", "^^")) { _at++; node = new Xor(node, ParseAnd()); }
            return node;
        }

        private Node ParseAnd()
        {
            var node = ParseNot();
            while (Is("and", "&&")) { _at++; node = new And(node, ParseNot()); }
            return node;
        }

        private Node ParseNot()
        {
            if (Is("not", "!")) { _at++; return new Not(ParseNot()); }
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            var token = Peek;
            if (token.Kind == Kind.Open)
            {
                _at++;
                var inner = ParseOr();
                if (Peek.Kind == Kind.End) throw new FilterSyntaxException(FilterError.Unclosed, token.At, "(");
                if (Peek.Kind != Kind.Close) throw Unexpected();
                _at++;
                return inner;
            }
            if (token.Kind == Kind.End) throw new FilterSyntaxException(FilterError.UnexpectedEnd, token.At, "");
            if (token.Kind != Kind.Word || Keywords.Contains(token.Text) || !FieldName.IsMatch(token.Text)) throw Unexpected();
            _at++;
            string field = token.Text;
            if (!Fields.Contains(field)) Fields.Add(field);

            if (Is("in"))
            {
                _at++;
                return new In(field, ParseSet());
            }
            if (OperatorHere() is not Op op) return new Exists(field);
            _at++;
            var value = ParseValue();
            if (op == Op.Matches)
            {
                try { value.Pattern = new Regex(value.Text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException e) { throw new FilterSyntaxException(FilterError.BadRegex, tokens[_at - 1].At, e.Message); }
            }
            return new Compare(field, op, value);
        }

        private Op? OperatorHere()
        {
            var t = Peek;
            if (t.Kind is not (Kind.Op or Kind.Word)) return null;
            return t.Text.ToLowerInvariant() switch
            {
                "==" or "===" or "eq" => Op.Eq,
                "!=" or "!==" or "ne" => Op.Ne,
                ">" or "gt" => Op.Gt,
                "<" or "lt" => Op.Lt,
                ">=" or "ge" => Op.Ge,
                "<=" or "le" => Op.Le,
                "contains" => Op.Contains,
                "matches" or "~" => Op.Matches,
                _ => null,
            };
        }

        private Literal ParseValue()
        {
            var token = Peek;
            if (token.Kind == Kind.End) throw new FilterSyntaxException(FilterError.UnexpectedEnd, token.At, "");
            if (token.Kind is not (Kind.Word or Kind.String) || (token.Kind == Kind.Word && Keywords.Contains(token.Text))) throw Unexpected();
            _at++;
            return ReadValue(token);
        }

        private List<(Literal, Literal?)> ParseSet()
        {
            var open = Peek;
            if (open.Kind == Kind.End) throw new FilterSyntaxException(FilterError.UnexpectedEnd, open.At, "");
            if (open.Kind != Kind.OpenSet) throw Unexpected();
            _at++;
            var members = new List<(Literal, Literal?)>();
            while (true)
            {
                var t = Peek;
                if (t.Kind == Kind.CloseSet && members.Count > 0) { _at++; return members; }
                if (t.Kind == Kind.End) throw new FilterSyntaxException(FilterError.Unclosed, open.At, "{");
                if (t.Kind == Kind.Comma && members.Count > 0) { _at++; continue; }
                if (t.Kind == Kind.Word && t.Text.Contains("..", StringComparison.Ordinal))
                {
                    _at++;
                    int dots = t.Text.IndexOf("..", StringComparison.Ordinal);
                    string from = t.Text[..dots], to = t.Text[(dots + 2)..];
                    if (from.Length == 0 || to.Length == 0) throw new FilterSyntaxException(FilterError.BadValue, t.At, t.Text);
                    members.Add((ReadValue(new Token(Kind.Word, from, t.At)), ReadValue(new Token(Kind.Word, to, t.At + dots + 2))));
                    continue;
                }
                members.Add((ParseValue(), null));
            }
        }

        private bool Is(params string[] words) =>
            Peek.Kind is Kind.Word or Kind.Op && words.Any(w => string.Equals(Peek.Text, w, StringComparison.OrdinalIgnoreCase));

        private FilterSyntaxException Unexpected() =>
            Peek.Kind == Kind.End
                ? new FilterSyntaxException(FilterError.UnexpectedEnd, Peek.At, "")
                : new FilterSyntaxException(FilterError.Unexpected, Peek.At, Peek.Kind == Kind.String ? DisplayFilter.Quote(Peek.Text) : Peek.Text);
    }
}
