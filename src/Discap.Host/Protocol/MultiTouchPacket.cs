using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Discap.Host.Protocol;

public struct TouchPointerRecord
{
    public byte AndroidPointerId;
    public byte Action; // 0=Down, 1=Move, 2=Up, 3=Cancel
    public ushort NormX; // 0..65535
    public ushort NormY; // 0..65535
    public ushort Pressure; // 0..65535
    public ushort Reserved;
}

public class MultiTouchPacket
{
    public const uint MAGIC_MTCH = 0x4843544D; // "MTCH" in Little-Endian

    public byte PointerCount { get; set; }
    public TouchPointerRecord[] Pointers { get; set; } = Array.Empty<TouchPointerRecord>();

    public static bool TryReadFrom(ReadOnlySpan<byte> buffer, out MultiTouchPacket packet)
    {
        if (buffer.Length < 5)
        {
            packet = null!;
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0..]);
        if (magic != MAGIC_MTCH)
        {
            packet = null!;
            return false;
        }

        byte count = buffer[4];
        int requiredSize = 5 + (count * 10);
        if (buffer.Length < requiredSize)
        {
            packet = null!;
            return false;
        }

        var records = new TouchPointerRecord[count];
        int offset = 5;

        for (int i = 0; i < count; i++)
        {
            records[i] = new TouchPointerRecord
            {
                AndroidPointerId = buffer[offset],
                Action = buffer[offset + 1],
                NormX = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 2)..]),
                NormY = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 4)..]),
                Pressure = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 6)..]),
                Reserved = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 8)..])
            };
            offset += 10;
        }

        packet = new MultiTouchPacket
        {
            PointerCount = count,
            Pointers = records
        };
        return true;
    }
}
