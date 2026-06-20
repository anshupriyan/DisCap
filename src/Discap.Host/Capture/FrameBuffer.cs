using System.Buffers;
using Vortice;

namespace Discap.Host.Capture;

public enum PixelFormat
{
    BGRA,
    NV12
}

/// <summary>
/// Container for a captured desktop frame.
/// Holds raw BGRA or NV12 pixel data copied from a GPU staging texture.
/// Uses ArrayPool to avoid GC pressure on the hot capture path.
/// </summary>
public sealed class FrameBuffer : IDisposable
{
    private byte[]? _pixels;
    private bool _disposed;
    private readonly bool _fromPool;

    /// <summary>Raw pixel data. Null if only GPU texture is captured.</summary>
    public byte[]? Pixels => _pixels;

    /// <summary>Raw D3D11 GPU texture. Available if mapped directly from GPU.</summary>
    public Vortice.Direct3D11.ID3D11Texture2D? GpuTexture { get; set; }

    /// <summary>Frame width in pixels.</summary>
    public int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>Row pitch (stride) in bytes. May be larger than Width * bytesPerPixel due to GPU alignment.</summary>
    public int Stride { get; }

    /// <summary>Total size of the raw pixel data in bytes.</summary>
    public int DataSize { get; }

    /// <summary>Capture timestamp in Stopwatch ticks.</summary>
    public long TimestampTicks { get; set; }

    public double CaptureTimeMs { get; set; }
    public double ConvertTimeMs { get; set; }

    /// <summary>Rectangles that changed since the previous frame (from DXGI dirty rects).</summary>
    public RawRect[] DirtyRects { get; set; } = Array.Empty<RawRect>();

    /// <summary>Total area of dirty rects in pixels.</summary>
    public int TotalDirtyArea { get; set; }

    public PixelFormat Format { get; set; }

    /// <summary>
    /// Creates a new FrameBuffer. Allocates pixel data from ArrayPool for reuse.
    /// </summary>
    public FrameBuffer(int width, int height, int stride = 0, PixelFormat format = PixelFormat.BGRA)
    {
        Width = width;
        Height = height;
        Format = format;

        if (format == PixelFormat.BGRA)
        {
            Stride = stride > 0 ? stride : width * 4;
            DataSize = Stride * Height;
        }
        else
        {
            Stride = stride > 0 ? stride : width; // NV12 Y-plane is 1 byte per pixel
            DataSize = Stride * (height + height / 2);
        }

        _pixels = ArrayPool<byte>.Shared.Rent(DataSize);
        _fromPool = true;
    }

    /// <summary>
    /// Creates a FrameBuffer wrapping existing pixel data (no pool allocation).
    /// </summary>
    public FrameBuffer(byte[] pixels, int width, int height, int stride, PixelFormat format = PixelFormat.BGRA)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
        DataSize = format == PixelFormat.BGRA ? stride * height : stride * (height + height / 2);
        _fromPool = false;
    }

    /// <summary>
    /// Returns a Span over the tightly-packed pixel data.
    /// If Stride matches tight width, this is the full buffer. Otherwise, requires row-by-row copy.
    /// </summary>
    public ReadOnlySpan<byte> GetTightPixels()
    {
        var pixels = Pixels;
        if (pixels == null)
            throw new InvalidOperationException("FrameBuffer does not contain CPU pixel data.");

        if (Format == PixelFormat.NV12)
        {
            int tightStride = Width;
            if (Stride == tightStride)
                return pixels.AsSpan(0, DataSize);

            // Strip padding from Y and UV planes
            var tight = new byte[tightStride * (Height + Height / 2)];
            // Y plane
            for (int y = 0; y < Height; y++)
            {
                Buffer.BlockCopy(pixels, y * Stride, tight, y * tightStride, tightStride);
            }
            // UV plane
            int uvHeight = Height / 2;
            int uvOffset = Height * Stride;
            int tightUvOffset = Height * tightStride;
            for (int y = 0; y < uvHeight; y++)
            {
                Buffer.BlockCopy(pixels, uvOffset + y * Stride, tight, tightUvOffset + y * tightStride, tightStride);
            }
            return tight;
        }
        else
        {
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
