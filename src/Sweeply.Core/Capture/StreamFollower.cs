using System.Net;

namespace Sweeply.Core.Capture;

/// <summary>A run of bytes one end sent, in the order the conversation went; before it, bytes that were never captured.</summary>
public sealed record StreamChunk(bool FromA, byte[] Data, int FirstFrame, DateTime Utc, int MissingBefore = 0);

/// <summary>What the two ends of a TCP or UDP stream said to each other, put back together.</summary>
public sealed class FollowedStream
{
    public required string Protocol { get; init; } // "TCP" or "UDP"
    public required int Stream { get; init; }
    public required IPAddress AddressA { get; init; }
    public required int PortA { get; init; }
    public required IPAddress AddressB { get; init; }
    public required int PortB { get; init; }
    public required IReadOnlyList<StreamChunk> Chunks { get; init; }
    public int Packets { get; init; }

    /// <summary>Following stopped at <see cref="StreamFollower.MaxBytes"/>.</summary>
    public bool Truncated { get; init; }

    public long BytesFromA => Chunks.Where(c => c.FromA).Sum(c => (long)c.Data.Length);
    public long BytesFromB => Chunks.Where(c => !c.FromA).Sum(c => (long)c.Data.Length);

    /// <summary>Bytes the capture never saw (lost before capturing, or sent before the capture started).</summary>
    public long Missing => Chunks.Sum(c => (long)c.MissingBefore);

    public string Filter => $"{Protocol.ToLowerInvariant()}.stream == {Stream}";

    /// <summary>Everything one end sent (or both, in order), as it went over the wire.</summary>
    public byte[] Bytes(bool? fromA = null)
    {
        var all = new MemoryStream();
        foreach (var chunk in Chunks)
            if (fromA is null || chunk.FromA == fromA) all.Write(chunk.Data);
        return all.ToArray();
    }
}

/// <summary>
/// Follows a TCP or UDP stream, as Wireshark's "Follow Stream" does. TCP data is put in sequence order: what was
/// sent again (a retransmission, an overlap) counts once, what came early waits for what is missing, and what never
/// arrives is marked as a gap. A is the end that sent first (for TCP, the one that opened the connection).
/// </summary>
public static class StreamFollower
{
    /// <summary>Most data kept per stream: following is for reading, not for carving out whole files.</summary>
    public const int MaxBytes = 64 * 1024 * 1024;

    /// <summary>Segments that may wait for a missing one; past that, the missing one is taken as never coming.</summary>
    private const int MaxWaiting = 256;

    public static FollowedStream? Follow(IReadOnlyList<CapturedPacket> packets, IReadOnlyList<StreamInfo> streams, bool tcp, int stream)
    {
        var output = new Output();
        AddressKey? keyA = null;
        AddressKey keyB = default;
        int portA = 0, portB = 0, count = 0;
        var sides = new[] { new Side(true), new Side(false) };

        for (int i = 0; i < packets.Count && !output.Full; i++)
        {
            if ((tcp ? streams[i].TcpStream : streams[i].UdpStream) != stream) continue;
            var p = packets[i];
            var h = QuickHeader.Read(p.Link, p.Data);
            if (tcp ? !h.IsTcp : !h.IsUdp) continue;
            count++;
            if (keyA is null)
            {
                // A SYN-ACK first (the capture started late) means the other end opened the connection.
                bool synAck = tcp && (h.TcpFlags & (QuickHeader.Syn | QuickHeader.AckFlag)) == (QuickHeader.Syn | QuickHeader.AckFlag);
                keyA = synAck ? h.Destination : h.Source;
                keyB = synAck ? h.Source : h.Destination;
                portA = synAck ? h.DestinationPort : h.SourcePort;
                portB = synAck ? h.SourcePort : h.DestinationPort;
            }
            bool fromA = h.Source == keyA && h.SourcePort == portA;
            int length = Math.Max(0, Math.Min(h.PayloadLength, p.Data.Length - h.PayloadOffset));
            var payload = length > 0 ? p.Data.AsSpan(h.PayloadOffset, length).ToArray() : Array.Empty<byte>();

            if (!tcp)
            {
                output.Append(fromA, payload, i + 1, p.TimestampUtc, 0);
                continue;
            }
            var side = sides[fromA ? 0 : 1];
            if ((h.TcpFlags & QuickHeader.Syn) != 0)
            {
                side.Next ??= h.Seq + 1; // data starts after the SYN
                continue;
            }
            if (payload.Length == 0) continue;
            side.Next ??= h.Seq; // no SYN seen: the first data starts the stream
            side.Add(new Segment(h.Seq, payload, i + 1, p.TimestampUtc), output);
        }
        // Whatever still waits lies behind data that was never captured.
        sides[0].Drain(output);
        sides[1].Drain(output);

        if (keyA is null) return null;
        return new FollowedStream
        {
            Protocol = tcp ? "TCP" : "UDP",
            Stream = stream,
            AddressA = keyA.Value.ToAddress(),
            PortA = portA,
            AddressB = keyB.ToAddress(),
            PortB = portB,
            Chunks = output.Chunks(),
            Packets = count,
            Truncated = output.Full,
        };
    }

    private sealed record Segment(uint Seq, byte[] Data, int Frame, DateTime Utc);

    /// <summary>One direction of a TCP stream: where its data has got to, and segments that came early.</summary>
    private sealed class Side(bool fromA)
    {
        public uint? Next;
        private readonly List<Segment> _waiting = new();

        public void Add(Segment segment, Output output)
        {
            _waiting.Add(segment);
            Advance(output);
            if (_waiting.Count > MaxWaiting) Skip(output); // what's missing isn't coming
        }

        /// <summary>At the end: everything that waited, in order, with the gaps marked.</summary>
        public void Drain(Output output)
        {
            while (_waiting.Count > 0 && !output.Full) Skip(output);
        }

        /// <summary>Takes in what now follows on without a hole (a segment sent again counts once).</summary>
        private void Advance(Output output)
        {
            bool moved = true;
            while (moved && Next is uint next)
            {
                moved = false;
                for (int k = 0; k < _waiting.Count; k++)
                {
                    var s = _waiting[k];
                    int behind = (int)(next - s.Seq); // > 0: starts before "next"
                    if (behind < 0) continue; // comes later
                    _waiting.RemoveAt(k);
                    if (behind < s.Data.Length)
                    {
                        output.Append(fromA, s.Data[behind..], s.Frame, s.Utc, 0);
                        Next = next + (uint)(s.Data.Length - behind);
                    }
                    moved = true;
                    break;
                }
            }
        }

        /// <summary>Jumps over a hole to the nearest waiting segment, noting how much is missing, then goes on.</summary>
        private void Skip(Output output)
        {
            if (Next is not uint next || _waiting.Count == 0) return;
            var nearest = _waiting.MinBy(s => s.Seq - next); // distance ahead, wrapping like sequence numbers do
            _waiting.Remove(nearest!);
            int missing = (int)Math.Min(int.MaxValue, nearest!.Seq - next);
            output.Append(fromA, nearest.Data, nearest.Frame, nearest.Utc, missing);
            Next = nearest.Seq + (uint)nearest.Data.Length;
            Advance(output);
        }
    }

    /// <summary>The chunks being built: data from the same end, without a gap, joins the last chunk.</summary>
    private sealed class Output
    {
        private readonly List<(bool FromA, MemoryStream Data, int Frame, DateTime Utc, int Missing)> _chunks = new();
        private long _kept;

        public bool Full => _kept >= MaxBytes;

        public void Append(bool fromA, byte[] data, int frame, DateTime utc, int missingBefore)
        {
            if (data.Length == 0 || Full) return;
            int take = (int)Math.Min(data.Length, MaxBytes - _kept);
            _kept += take;
            if (missingBefore == 0 && _chunks.Count > 0 && _chunks[^1].FromA == fromA)
            {
                _chunks[^1].Data.Write(data, 0, take);
                return;
            }
            var buffer = new MemoryStream();
            buffer.Write(data, 0, take);
            _chunks.Add((fromA, buffer, frame, utc, missingBefore));
        }

        public List<StreamChunk> Chunks() => _chunks.Select(c => new StreamChunk(c.FromA, c.Data.ToArray(), c.Frame, c.Utc, c.Missing)).ToList();
    }
}
