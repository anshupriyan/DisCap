using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Discap.Host.Protocol;

/// <summary>
/// 12-byte control packet from the Android client.
/// Kept the same size as InputPacket so the input reader can parse one fixed frame.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ControlPacket
{
    public const uint MAGIC = 0x4C525443; // "CTRL" in little-endian
    public const int SIZE = 12;

    public const byte EncoderAuto = 0;
    public const byte EncoderH264 = 1;
    public const byte EncoderLz4 = 2;

    public uint Magic;
    public byte BitrateMbps;
    public byte FpsCap;
    public byte ResolutionScale;
    public byte EncoderMode;
    public byte ShowStats;
    public byte Reserved0;
    public byte Reserved1;
    public byte Reserved2;

    public static bool TryReadFrom(ReadOnlySpan<byte> buffer, out ControlPacket packet)
    {
        if (buffer.Length < SIZE)
        {
            packet = default;
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0..]);
        if (magic != MAGIC)
        {
            packet = default;
            return false;
        }

        packet = new ControlPacket
        {
            Magic = magic,
            BitrateMbps = buffer[4],
            FpsCap = buffer[5],
            ResolutionScale = buffer[6],
            EncoderMode = buffer[7],
            ShowStats = buffer[8],
            Reserved0 = buffer[9],
            Reserved1 = buffer[10],
            Reserved2 = buffer[11]
        };
        return true;
    }
}
