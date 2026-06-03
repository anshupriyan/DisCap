using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Discap.Host.Protocol;

/// <summary>
/// Fixed 32-byte binary header prepended to every frame sent over the wire.
/// Little-endian byte order. The Android receiver reads this header first
/// to determine how to decode the following payload.
///
/// Layout:
///   [0..3]   Magic        — 0x44434150 ("DCAP")
///   [4]      Version      — Protocol version (currently 1)
///   [5]      FrameType    — 0x01 = LZ4, 0x02 = NVENC
///   [6..7]   Width        — Frame width in pixels
///   [8..9]   Height       — Frame height in pixels
///   [10..13] OriginalSize — Uncompressed size in bytes
///   [14..17] CompressedSize — Compressed payload size in bytes
///   [18..25] Timestamp    — Microseconds since stream start
///   [26..29] SequenceNum  — Monotonic frame counter
///   [30..31] Flags        — Reserved (bit 0 = keyframe)
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PacketHeader
{
    /// <summary>Magic bytes: "DCAP" = 0x44434150.</summary>
    public const uint MAGIC = 0x44434150;

    /// <summary>Current protocol version.</summary>
    public const byte PROTOCOL_VERSION = 1;

    /// <summary>Total header size in bytes — always 32.</summary>
    public const int SIZE = 32;

    /// <summary>Flag: this frame is a keyframe (IDR for NVENC, or first LZ4 frame).</summary>
    public const ushort FLAG_KEYFRAME = 0x0001;

    public uint Magic;
    public byte Version;
    public FrameType FrameType;
    public ushort Width;
    public ushort Height;
    public uint OriginalSize;
    public uint CompressedSize;
    public long Timestamp;
    public uint SequenceNumber;
    public ushort Flags;

    /// <summary>
    /// Creates a new packet header with magic and version pre-filled.
    /// </summary>
    public static PacketHeader Create(
        FrameType frameType,
        ushort width,
        ushort height,
        uint originalSize,
        uint compressedSize,
        long timestampUs,
        uint sequenceNumber,
        ushort flags = 0)
    {
        return new PacketHeader
        {
            Magic = MAGIC,
            Version = PROTOCOL_VERSION,
            FrameType = frameType,
            Width = width,
            Height = height,
            OriginalSize = originalSize,
            CompressedSize = compressedSize,
            Timestamp = timestampUs,
            SequenceNumber = sequenceNumber,
            Flags = flags
        };
    }

    /// <summary>
    /// Serializes this header into a 32-byte buffer (little-endian).
    /// </summary>
    public void WriteTo(Span<byte> buffer)
    {
        if (buffer.Length < SIZE)
            throw new ArgumentException($"Buffer must be at least {SIZE} bytes", nameof(buffer));

        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0..], Magic);
        buffer[4] = Version;
        buffer[5] = (byte)FrameType;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..], Width);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[8..], Height);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[10..], OriginalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[14..], CompressedSize);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[18..], Timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[26..], SequenceNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[30..], Flags);
    }

    /// <summary>
    /// Deserializes a 32-byte buffer into a PacketHeader (for testing/debugging).
    /// </summary>
    public static PacketHeader ReadFrom(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SIZE)
            throw new ArgumentException($"Buffer must be at least {SIZE} bytes", nameof(buffer));

        return new PacketHeader
        {
            Magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0..]),
            Version = buffer[4],
            FrameType = (FrameType)buffer[5],
            Width = BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..]),
            Height = BinaryPrimitives.ReadUInt16LittleEndian(buffer[8..]),
            OriginalSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer[10..]),
            CompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer[14..]),
            Timestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer[18..]),
            SequenceNumber = BinaryPrimitives.ReadUInt32LittleEndian(buffer[26..]),
            Flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer[30..])
        };
    }

    public override string ToString()
    {
        return $"[DCAP v{Version}] {FrameType} {Width}x{Height} " +
               $"orig={OriginalSize} comp={CompressedSize} " +
               $"seq={SequenceNumber} ts={Timestamp}µs flags=0x{Flags:X4}";
    }
}
