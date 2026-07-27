using System.Buffers.Binary;

namespace Discap.Host.Protocol;

/// <summary>
/// Serializer for sidecar cursor position and cursor shape packets.
/// Uses little-endian binary framing matching PacketHeader specs.
/// </summary>
public static class CursorPackets
{
    /// <summary>
    /// Serializes a CursorPos packet payload (9 bytes total).
    /// </summary>
    public static byte[] SerializeCursorPos(int x, int y, bool visible)
    {
        byte[] payload = new byte[9];
        BitConverter.GetBytes(x).CopyTo(payload, 0);
        BitConverter.GetBytes(y).CopyTo(payload, 4);
        payload[8] = (byte)(visible ? 1 : 0);
        return payload;
    }

    /// <summary>
    /// Serializes a CursorShape packet payload (28 bytes metadata + ShapeBuffer).
    /// </summary>
    public static byte[] SerializeCursorShape(
        uint type,
        uint width,
        uint height,
        uint pitch,
        int hotspotX,
        int hotspotY,
        ReadOnlySpan<byte> shapeBuffer)
    {
        int bufferSize = shapeBuffer.Length;
        byte[] payload = new byte[28 + bufferSize];

        BitConverter.GetBytes((int)type).CopyTo(payload, 0);       // Offset 0: Type
        BitConverter.GetBytes((int)width).CopyTo(payload, 4);      // Offset 4: Width
        BitConverter.GetBytes((int)height).CopyTo(payload, 8);     // Offset 8: Height
        BitConverter.GetBytes((int)pitch).CopyTo(payload, 12);    // Offset 12: Pitch
        BitConverter.GetBytes((int)hotspotX).CopyTo(payload, 16);  // Offset 16: HotspotX
        BitConverter.GetBytes((int)hotspotY).CopyTo(payload, 20);  // Offset 20: HotspotY
        BitConverter.GetBytes((int)bufferSize).CopyTo(payload, 24);// Offset 24: BufferSize

        if (bufferSize > 0)
        {
            shapeBuffer.CopyTo(payload.AsSpan(28));
        }

        return payload;
    }
}
