namespace Discap.Host.Protocol;

/// <summary>
/// Writes framed packets (header + payload) to a stream.
/// Uses synchronous writes with TCP_NODELAY for minimum latency.
/// Thread-safe: concurrent writes are serialized via lock.
/// </summary>
public sealed class PacketWriter
{
    private readonly object _writeLock = new();
    private readonly byte[] _headerBuffer = new byte[PacketHeader.SIZE];

    /// <summary>
    /// Writes a complete packet (32-byte header + compressed payload) to the stream.
    /// This is a single atomic operation — header and payload are written together.
    /// </summary>
    /// <param name="stream">The output stream (typically a NetworkStream with NoDelay).</param>
    /// <param name="header">The packet header describing this frame.</param>
    /// <param name="payload">The compressed frame payload.</param>
    /// <param name="payloadOffset">Start offset within the payload buffer.</param>
    /// <param name="payloadLength">Number of bytes to write from the payload.</param>
    public void WritePacket(
        Stream stream,
        PacketHeader header,
        byte[] payload,
        int payloadOffset,
        int payloadLength)
    {
        // Serialize the header into our reusable buffer.
        header.WriteTo(_headerBuffer);

        // Lock to ensure header + payload are written atomically.
        // Without this, concurrent writes could interleave.
        lock (_writeLock)
        {
            stream.Write(_headerBuffer, 0, PacketHeader.SIZE);
            stream.Write(payload, payloadOffset, payloadLength);
            // No Flush() — TCP_NODELAY is set on the socket, so writes
            // are pushed immediately without Nagle buffering.
        }
    }

    /// <summary>
    /// Convenience overload that writes the entire payload buffer.
    /// </summary>
    public void WritePacket(Stream stream, PacketHeader header, byte[] payload)
    {
        WritePacket(stream, header, payload, 0, payload.Length);
    }
}
