using System.Buffers;
using Vortice.Mathematics;

namespace Discap.Host.Capture;

/// <summary>
/// Container for a captured desktop frame.
/// Holds raw BGRA pixel data copied from a GPU staging texture.
/// Uses ArrayPool to avoid GC pressure on the hot capture path.
/// </summary>
public sealed class FrameBuffer : IDisposable
{
    private byte[]? _pixels;
    private bool _disposed;
    private readonly bool _fromPool;

    /// <summary>Raw BGRA32 pixel data. 4 bytes per pixel.</summary>
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(FrameBuffer));

    /// <summary>Frame width in pixels.</summary>
    public int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>Row pitch (stride) in bytes. May be larger than Width * 4 due to GPU alignment.</summary>
    public int Stride { get; }

    /// <summary>Total size of the raw pixel data in bytes (Stride * Height).</summary>
    public int DataSize { get; }

    /// <summary>Capture timestamp in Stopwatch ticks.</summary>
    public long TimestampTicks { get; set; }

    /// <summary>Rectangles that changed since the previous frame (from DXGI dirty rects).</summary>
    public RawRect[] DirtyRects { get; set; } = Array.Empty<RawRect>();

    /// <summary>Total area of dirty rects in pixels.</summary>
    public int TotalDirtyArea { get; set; }

    /// <summary>
    /// Creates a new FrameBuffer. Allocates pixel data from ArrayPool for reuse.
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="stride">Row pitch in bytes. If 0, defaults to width * 4.</param>
    public FrameBuffer(int width, int height, int stride = 0)
    {
        Width = width;
        Height = height;
        Stride = stride > 0 ? stride : width * 4;
        DataSize = Stride * Height;
        _pixels = ArrayPool<byte>.Shared.Rent(DataSize);
        _fromPool = true;
    }

    /// <summary>
    /// Creates a FrameBuffer wrapping existing pixel data (no pool allocation).
    /// </summary>
    public FrameBuffer(byte[] pixels, int width, int height, int stride)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        DataSize = stride * height;
        _fromPool = false;
    }

    /// <summary>
    /// Returns a Span over the tightly-packed pixel data (Width * 4 bytes per row).
    /// If Stride == Width * 4, this is the full buffer. Otherwise, requires row-by-row copy.
    /// </summary>
    public ReadOnlySpan<byte> GetTightPixels()
    {
        var pixels = Pixels;
        int tightStride = Width * 4;

        // Fast path: stride matches tight layout (no padding).
        if (Stride == tightStride)
            return pixels.AsSpan(0, DataSize);

        // Slow path: strip padding from each row.
        var tight = new byte[tightStride * Height];
        for (int y = 0; y < Height; y++)
        {
            Buffer.BlockCopy(pixels, y * Stride, tight, y * tightStride, tightStride);
        }
        return tight;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_fromPool && _pixels != null)
        {
            ArrayPool<byte>.Shared.Return(_pixels);
            _pixels = null;
        }
    }
}
