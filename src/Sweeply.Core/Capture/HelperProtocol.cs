using System.Buffers.Binary;
using System.Text.Json;

namespace Sweeply.Core.Capture;

/// <summary>The messages between the app and its helper, one at a time in either direction.</summary>
public enum HelperMessage : byte
{
    /// <summary>Helper to app: it is running, which way it captures and on which interfaces (JSON <see cref="HelperHello"/>).</summary>
    Hello = 1,
    Packet = 2,

    /// <summary>Helper to app, every second: packets received and dropped so far (two 64-bit numbers).</summary>
    Stats = 3,

    /// <summary>Helper to app: it failed or stopped with a reason (JSON <see cref="HelperError"/>); nothing follows.</summary>
    Error = 4,

    /// <summary>App to helper, first: what to do (JSON <see cref="HelperRequest"/>).</summary>
    Start = 16,

    /// <summary>App to helper: stop and end. Closing the pipe does the same.</summary>
    Stop = 17,
}

/// <summary>
/// What the app asks its helper to do. The helper's command line only says how to reach the app; the job comes
/// over the pipe once the helper has checked that the app at the other end is this same program.
/// </summary>
public sealed record HelperRequest
{
    public const string CaptureMode = "capture";
    public const string RawSocketBackend = "RawSocket", NpcapBackend = "Npcap", ReplayBackend = "Replay";

    public string Mode { get; init; } = CaptureMode;
    public string Backend { get; init; } = RawSocketBackend;

    /// <summary>Which network adapters (<see cref="Monitoring.NetworkAdapter.Id"/>).</summary>
    public List<string> Adapters { get; init; } = new();

    /// <summary>For <see cref="ReplayBackend"/>: the capture file to play back.</summary>
    public string? File { get; init; }

    /// <summary>For <see cref="ReplayBackend"/>: keep the original gaps between packets (up to half a second), or go as fast as possible.</summary>
    public bool RealTime { get; init; } = true;
}

public sealed record HelperHello(string Backend, List<CaptureInterface> Interfaces);

/// <summary>Why capturing failed: a code the app turns into words (see the cap.error.* strings), and what Windows said.</summary>
public sealed record HelperError(string Code, string Detail);

/// <summary>
/// The capture helper runs with the rights capturing needs and the app without; they talk over a named pipe.
/// Each message is a type byte, a 4-byte little-endian length, then the payload. A packet's payload is its
/// time (ticks, UTC), link type, direction, interface and original length, then its bytes.
/// </summary>
public static class HelperProtocol
{
    public const int MaxPayload = 1 << 20; // larger than any packet (64 KB) or hello; anything bigger is broken
    private const int PacketHeader = 8 + 2 + 1 + 2 + 4;

    public static byte[] Encode(HelperMessage type, ReadOnlySpan<byte> payload)
    {
        var message = new byte[5 + payload.Length];
        message[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(1), payload.Length);
        payload.CopyTo(message.AsSpan(5));
        return message;
    }

    public static byte[] EncodePacket(CapturedPacket packet)
    {
        var payload = new byte[PacketHeader + packet.Data.Length];
        var span = payload.AsSpan();
        BinaryPrimitives.WriteInt64LittleEndian(span, packet.TimestampUtc.Ticks);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], (ushort)packet.Link);
        span[10] = (byte)packet.Direction;
        BinaryPrimitives.WriteUInt16LittleEndian(span[11..], (ushort)packet.InterfaceId);
        BinaryPrimitives.WriteInt32LittleEndian(span[13..], packet.Length);
        packet.Data.CopyTo(span[PacketHeader..]);
        return Encode(HelperMessage.Packet, payload);
    }

    public static CapturedPacket DecodePacket(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < PacketHeader) throw new InvalidDataException("A packet message is too short.");
        long ticks = BinaryPrimitives.ReadInt64LittleEndian(payload);
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) throw new InvalidDataException("A packet's time is out of range.");
        return new CapturedPacket
        {
            TimestampUtc = new DateTime(ticks, DateTimeKind.Utc),
            Link = (LinkType)BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]),
            Direction = (PacketDirection)Math.Min(payload[10], (byte)2),
            InterfaceId = BinaryPrimitives.ReadUInt16LittleEndian(payload[11..]),
            OriginalLength = BinaryPrimitives.ReadInt32LittleEndian(payload[13..]),
            Data = payload[PacketHeader..].ToArray(),
        };
    }

    public static byte[] EncodeStats(long received, long dropped)
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(payload, received);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), dropped);
        return Encode(HelperMessage.Stats, payload);
    }

    public static (long Received, long Dropped) DecodeStats(ReadOnlySpan<byte> payload) =>
        payload.Length < 16 ? (0, 0) : (BinaryPrimitives.ReadInt64LittleEndian(payload), BinaryPrimitives.ReadInt64LittleEndian(payload[8..]));

    public static byte[] EncodeJson<T>(HelperMessage type, T value) => Encode(type, JsonSerializer.SerializeToUtf8Bytes(value));

    public static T? DecodeJson<T>(ReadOnlySpan<byte> payload) => JsonSerializer.Deserialize<T>(payload);

    /// <summary>The next message, or null at the end of the stream.</summary>
    public static async Task<(HelperMessage Type, byte[] Payload)?> ReadAsync(Stream stream, CancellationToken cancel)
    {
        var header = new byte[5];
        if (!await ReadExactly(stream, header, cancel)) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length < 0 || length > MaxPayload) throw new InvalidDataException("A helper message has an impossible length.");
        var payload = new byte[length];
        if (!await ReadExactly(stream, payload, cancel)) return null;
        return ((HelperMessage)header[0], payload);
    }

    private static async Task<bool> ReadExactly(Stream stream, byte[] buffer, CancellationToken cancel)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), cancel);
            if (n == 0) return false;
            total += n;
        }
        return true;
    }
}
