using System;
using System.Buffers.Binary;

namespace Discap.Host.Protocol;

public struct CursorPositionInfo
{
    public int X;
    public int Y;
    public bool Visible;

    public byte[] Serialize(ushort desktopWidth, ushort desktopHeight)
    {
        byte[] buffer = new byte[PacketHeader.SIZE + 9];
        var header = PacketHeader.Create(
            FrameType.CURSOR_POSITION,
            desktopWidth,
            desktopHeight,
            9,
            9,
            0,
            0);
        header.WriteTo(buffer);

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(PacketHeader.SIZE), X);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(PacketHeader.SIZE + 4), Y);
        buffer[PacketHeader.SIZE + 8] = (byte)(Visible ? 1 : 0);

        return buffer;
    }
}

public struct CursorShapeInfo
{
    public uint ShapeType; // 1=Mono, 2=Color, 4=MaskedColor
    public uint Width;
    public uint Height; // actual height of shape
    public int HotspotX;
    public int HotspotY;
    public byte[] PixelData;

    public byte[] Serialize(ushort desktopWidth, ushort desktopHeight)
    {
        uint payloadSize = (uint)(20 + PixelData.Length);
        byte[] buffer = new byte[PacketHeader.SIZE + payloadSize];
        var header = PacketHeader.Create(
            FrameType.CURSOR_SHAPE,
            desktopWidth,
            desktopHeight,
            payloadSize,
            payloadSize,
            0,
            0);
        header.WriteTo(buffer);

        int offset = PacketHeader.SIZE;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), ShapeType);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4), Width);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 8), Height);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset + 12), HotspotX);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset + 16), HotspotY);
        Array.Copy(PixelData, 0, buffer, offset + 20, PixelData.Length);

        return buffer;
    }
}
