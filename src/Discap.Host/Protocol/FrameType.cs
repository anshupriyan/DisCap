namespace Discap.Host.Protocol;

/// <summary>
/// Identifies the compression/encoding type of a frame payload.
/// The Android receiver uses this to choose the correct decoder.
/// </summary>
public enum FrameType : byte
{
    /// <summary>
    /// Frame is LZ4 block-compressed raw BGRA pixels.
    /// Used for static/low-motion content — lossless, perfect quality.
    /// </summary>
    LZ4 = 0x01,

    /// <summary>
    /// Frame is H.264 encoded via hardware (NVENC).
    /// Used for high-motion content — lossy but smooth at high framerates.
    /// </summary>
    NVENC = 0x02
}
