using Discap.Host.Capture;
using Discap.Host.Protocol;

namespace Discap.Host.Compression;

/// <summary>
/// Analyzes frame content to decide between LZ4 (static) and NVENC (motion) encoding.
///
/// Uses the dirty rect information from DXGI Desktop Duplication API as the primary
/// signal — this is essentially free since the API already tracks which screen regions
/// changed. No per-pixel comparison needed.
///
/// Decision logic:
///   dirty_area / total_area < threshold → LZ4 (lossless, perfect for text/desktop)
///   dirty_area / total_area ≥ threshold → NVENC (smooth, good for video/games)
/// </summary>
public sealed class FrameAnalyzer
{
    private readonly float _motionThreshold;

    /// <summary>
    /// Creates a FrameAnalyzer with the specified motion threshold.
    /// </summary>
    /// <param name="motionThreshold">
    /// Fraction of screen area that must be dirty to trigger NVENC encoding.
    /// Range 0.0 to 1.0. Default 0.15 (15%).
    /// </param>
    public FrameAnalyzer(float motionThreshold = 0.15f)
    {
        _motionThreshold = Math.Clamp(motionThreshold, 0.01f, 1.0f);
    }

    /// <summary>
    /// Determines whether the frame should use LZ4 or NVENC encoding
    /// based on how much of the screen has changed.
    /// </summary>
    /// <param name="frame">The current captured frame with dirty rect data.</param>
    /// <returns>The recommended frame type for this frame.</returns>
    public FrameType Analyze(FrameBuffer frame)
    {
        int totalPixels = frame.Width * frame.Height;
        if (totalPixels == 0) return FrameType.LZ4;

        float dirtyRatio = (float)frame.TotalDirtyArea / totalPixels;

        return dirtyRatio >= _motionThreshold ? FrameType.NVENC : FrameType.LZ4;
    }

    /// <summary>
    /// Computes the dirty area ratio for logging/debugging.
    /// </summary>
    public float ComputeDirtyRatio(FrameBuffer frame)
    {
        int totalPixels = frame.Width * frame.Height;
        if (totalPixels == 0) return 0f;
        return (float)frame.TotalDirtyArea / totalPixels;
    }
}
