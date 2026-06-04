using K4os.Compression.LZ4;

namespace Discap.Host.Compression;

/// <summary>
/// LZ4 block compressor for static/low-motion desktop frames.
///
/// LZ4 is ideal for desktop content because:
/// - Compression: 400+ MB/s per core (faster than USB bandwidth)
/// - Decompression: 2+ GB/s per core (essentially free on Android)
/// - Desktop/text content compresses 8-10x (mostly solid colors + text)
/// - Lossless — perfect pixel quality, no artifacts
///
/// Uses K4os.Compression.LZ4 with fastest preset (L00) for minimum latency.
/// </summary>
public sealed class Lz4Compressor
{
    // Reusable output buffer, grown as needed.
    private byte[] _outputBuffer;

    public Lz4Compressor()
    {
        // Start with 4MB — enough for most compressed desktop frames.
        _outputBuffer = new byte[4 * 1024 * 1024];
    }

    /// <summary>
    /// Compresses raw pixel data using LZ4 block compression.
    /// </summary>
    /// <param name="pixels">Raw BGRA pixel data to compress.</param>
    /// <param name="length">Number of bytes to compress from the pixel buffer.</param>
    /// <param name="compressedData">Output: the compressed data buffer.</param>
    /// <returns>The actual number of compressed bytes.</returns>
    public int Compress(byte[] pixels, int length, out byte[] compressedData)
    {
        // Ensure output buffer is large enough.
        int maxOutput = LZ4Codec.MaximumOutputSize(length);
        if (_outputBuffer.Length < maxOutput)
        {
            _outputBuffer = new byte[maxOutput];
        }

        // Compress using fastest preset (L00) for minimum latency.
        int compressedSize = LZ4Codec.Encode(
            pixels, 0, length,
            _outputBuffer, 0, _outputBuffer.Length,
            LZ4Level.L00_FAST);

        if (compressedSize <= 0)
        {
            throw new InvalidOperationException("LZ4 compression failed");
        }

        compressedData = _outputBuffer;
        return compressedSize;
    }

    /// <summary>
    /// Compresses a ReadOnlySpan of pixel data.
    /// </summary>
    public int Compress(ReadOnlySpan<byte> pixels, out byte[] compressedData)
    {
        int maxOutput = LZ4Codec.MaximumOutputSize(pixels.Length);
        if (_outputBuffer.Length < maxOutput)
        {
            _outputBuffer = new byte[maxOutput];
        }

        int compressedSize = LZ4Codec.Encode(
            pixels,
            _outputBuffer.AsSpan(),
            LZ4Level.L00_FAST);

        if (compressedSize <= 0)
        {
            throw new InvalidOperationException("LZ4 compression failed");
        }

        compressedData = _outputBuffer;
        return compressedSize;
    }
}
