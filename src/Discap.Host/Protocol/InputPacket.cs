using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Discap.Host.Protocol;

/// <summary>
/// 12-byte input packet from the Android client.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct InputPacket
{
    public const uint MAGIC = 0x54504E49; // "INPT" in little-endian
    public const int SIZE = 12;

    public uint Magic;
    public ushort X;
    public ushort Y;
    public byte Action;
    public byte Button;
    public byte Pressure;
    public byte Reserved;

    public static bool TryReadFrom(ReadOnlySpan<byte> buffer, out InputPacket packet)
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

        packet = new InputPacket
        {
            Magic = magic,
            X = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]),
            Y = BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..]),
            Action = buffer[8],
            Button = buffer[9],
            Pressure = buffer[10],
            Reserved = buffer[11]
        };
        return true;
    }
}
