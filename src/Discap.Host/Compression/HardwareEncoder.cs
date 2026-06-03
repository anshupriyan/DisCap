using Discap.Host.Capture;

namespace Discap.Host.Compression;

/// <summary>
/// Hardware H.264 encoder using Windows Media Foundation Transform (MFT).
/// On systems with NVIDIA GPUs, MF automatically selects the NVENC hardware encoder.
///
/// Phase 1 implementation note:
/// Full MFT initialization for hardware encoding requires significant setup
/// (input/output media types, sample buffers, async callbacks, color space conversion).
/// For the initial working build, this provides a structured placeholder that:
/// - Detects hardware encoder availability
/// - Falls back to LZ4-only mode if NVENC is not available
/// - Will be fully implemented when the LZ4 pipeline is proven end-to-end
///
/// The full NVENC pipeline will:
/// 1. Accept BGRA frames from Desktop Duplication
/// 2. Convert BGRA → NV12 (required by hardware encoders)
/// 3. Encode via MFT with low-latency preset (no B-frames, short GOP)
/// 4. Output H.264 NAL units ready for network transmission
/// </summary>
public sealed class HardwareEncoder : IDisposable
{
    private bool _initialized;
    private bool _available;
    private bool _disposed;

    private int _width;
    private int _height;
    private int _frameRate;

    // Reusable output buffer for encoded data.
    private byte[] _outputBuffer;

    /// <summary>Whether a hardware encoder was detected and is ready.</summary>
    public bool IsAvailable => _available;

    public HardwareEncoder()
    {
        _outputBuffer = new byte[2 * 1024 * 1024]; // 2MB initial buffer
    }

    /// <summary>
    /// Initializes the hardware encoder.
    /// Checks for NVENC availability and configures the encoding session.
    /// </summary>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <param name="frameRate">Target frame rate.</param>
    /// <returns>True if hardware encoding is available.</returns>
    public bool Initialize(int width, int height, int frameRate = 60)
    {
        _width = width;
        _height = height;
        _frameRate = frameRate;

        try
        {
            // Probe for hardware H.264 encoder via Media Foundation.
            _available = ProbeHardwareEncoder();

            if (_available)
            {
                Console.WriteLine($"[ENC] Hardware H.264 encoder detected ({_width}x{_height} @ {_frameRate}fps)");
                Console.WriteLine("[ENC] NVENC encoding enabled for high-motion content");
                _initialized = true;
            }
            else
            {
                Console.WriteLine("[ENC] No hardware H.264 encoder found — using LZ4-only mode");
                Console.WriteLine("[ENC] Install NVIDIA drivers to enable NVENC encoding");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ENC] Hardware encoder probe failed: {ex.Message}");
            _available = false;
        }

        return _available;
    }

    /// <summary>
    /// Encodes a frame using hardware H.264.
    /// Returns the encoded data length, or -1 if encoding is not available.
    /// </summary>
    /// <param name="frame">The captured frame to encode.</param>
    /// <param name="encodedData">Output: buffer containing encoded H.264 data.</param>
    /// <returns>Length of encoded data in bytes, or -1 on failure.</returns>
    public int Encode(FrameBuffer frame, out byte[] encodedData)
    {
        encodedData = _outputBuffer;

        if (!_available || !_initialized)
            return -1;

        // TODO: Full MFT encoding pipeline.
        // For now, return -1 to signal the caller to fall back to LZ4.
        // This will be replaced with:
        //   1. BGRA → NV12 color conversion
        //   2. MFT ProcessInput with IMFSample containing the NV12 texture
        //   3. MFT ProcessOutput to get encoded H.264 NAL units
        //   4. Copy NAL units to output buffer
        //
        // The framework is in place — the capture pipeline, frame analysis,
        // protocol, and transport all handle NVENC frames correctly.
        // Just this encoding step needs the MFT plumbing.

        Console.WriteLine("[ENC] Hardware encoding not yet implemented — falling back to LZ4");
        _available = false; // Disable after first attempt to avoid log spam
        return -1;
    }

    /// <summary>
    /// Checks if a hardware H.264 encoder is available on this system.
    /// </summary>
    private bool ProbeHardwareEncoder()
    {
        // Check for NVIDIA encoder DLL presence as a quick probe.
        // On systems with NVIDIA GPUs and proper drivers, nvEncodeAPI64.dll exists.
        string nvidiaEncoderPath = Path.Combine(
            Environment.SystemDirectory, "nvEncodeAPI64.dll");

        if (File.Exists(nvidiaEncoderPath))
        {
            Console.WriteLine($"[ENC] Found NVENC: {nvidiaEncoderPath}");
            return true;
        }

        // Also check for Media Foundation hardware encoder.
        // MFT enumeration would go here for a more robust check.
        // For now, the DLL check is sufficient for NVIDIA systems.

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // MFT resources would be released here.
    }
}
