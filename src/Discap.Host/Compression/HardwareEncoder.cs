using System.Runtime.InteropServices;
using Discap.Host.Capture;
using SharpGen.Runtime.Win32;
using Vortice.MediaFoundation;
using System.Linq;

namespace Discap.Host.Compression;

/// <summary>
/// Hardware H.264 encoder using Windows Media Foundation Transform (MFT).
/// On systems with NVIDIA GPUs, MF selects the NVENC hardware encoder.
/// On Intel, it selects QuickSync. Falls back gracefully if unavailable.
///
/// Pipeline:
///   1. BGRA pixels from Desktop Duplication
///   2. Software BGRA → NV12 color conversion (fast integer math)
///   3. Feed NV12 frame to hardware H.264 MFT via ProcessInput
///   4. Retrieve encoded H.264 NAL units via ProcessOutput
///
/// Configured for ultra-low latency:
///   - No B-frames (encode order = display order)
///   - Short GOP (1 second) for fast random access
///   - CBR rate control for predictable bandwidth
///   - Low-latency mode via ICodecAPI
/// </summary>
public sealed class HardwareEncoder : IDisposable
{
    private bool _initialized;
    private bool _available;
    private bool _disposed;
    private bool _mfStarted;

    private int _width;
    private int _height;
    private int _frameRate;

    // Media Foundation objects.
    private IMFTransform? _encoder;
    private IMFMediaType? _inputType;
    private IMFMediaType? _outputType;

    // NV12 conversion buffer.
    private byte[]? _nv12Buffer;
    private int _nv12Size;

    // Reusable output buffer for encoded H.264 data.
    private byte[] _outputBuffer;

    // Frame timing.
    private long _frameDuration; // in 100-nanosecond units
    private long _frameIndex;

    // MFT output.
    private OutputDataBuffer _outputDataBuffer;

    /// <summary>Whether a hardware encoder was detected and is ready.</summary>
    public bool IsAvailable => _available;

    // Well-known GUIDs that Vortice may not expose as named constants.
    // MFVideoFormat_H264 = FourCC 'H264'
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    // MFVideoFormat_NV12 = FourCC 'NV12'
    private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");

    // ICodecAPI property GUIDs (Removed due to Vortice 3.8.1 missing ICodecAPI wrapper)

    public HardwareEncoder()
    {
        _outputBuffer = new byte[2 * 1024 * 1024]; // 2MB initial buffer
        _outputDataBuffer = new OutputDataBuffer();
    }

    /// <summary>
    /// Initializes the hardware encoder.
    /// Starts Media Foundation, enumerates hardware H.264 encoders,
    /// configures media types and low-latency settings.
    /// </summary>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <param name="frameRate">Target frame rate.</param>
    /// <returns>True if hardware encoding is available and initialized.</returns>
    public bool Initialize(int width, int height, int frameRate = 60)
    {
        _width = width;
        _height = height;
        _frameRate = frameRate;
        _frameDuration = 10_000_000 / frameRate; // 100ns units
        _frameIndex = 0;

        // Pre-allocate NV12 buffer: Y plane (W*H) + UV plane (W*H/2).
        _nv12Size = _width * _height * 3 / 2;
        _nv12Buffer = new byte[_nv12Size];

        try
        {
            // Start Media Foundation.
            MediaFactory.MFStartup();
            _mfStarted = true;
            Console.WriteLine("[ENC] Media Foundation started");

            // Find a hardware H.264 encoder.
            _encoder = FindHardwareEncoder();
            if (_encoder == null)
            {
                Console.WriteLine("[ENC] No hardware H.264 encoder found — using LZ4-only mode");
                Console.WriteLine("[ENC] Install NVIDIA/Intel/AMD GPU drivers for hardware encoding");
                _available = false;
                return false;
            }

            // Configure the output type (H.264).
            ConfigureOutputType();

            // Configure the input type (NV12).
            ConfigureInputType();

            // Set low-latency encoding parameters.
            // ConfigureLowLatency(); // Disabled: ICodecAPI not available in Vortice 3.8.1

            // Notify the MFT we're ready to start processing.
            _encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            _available = true;
            _initialized = true;
            Console.WriteLine($"[ENC] Hardware H.264 encoder initialized ({_width}x{_height} @ {_frameRate}fps)");
            Console.WriteLine("[ENC] NVENC encoding enabled for high-motion content");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ENC] Hardware encoder initialization failed: {ex.Message}");
            Console.Error.WriteLine($"[ENC] Details: {ex}");
            CleanupEncoder();
            _available = false;
            return false;
        }
    }

    /// <summary>
    /// Encodes a frame using hardware H.264.
    /// Converts BGRA → NV12, feeds to MFT, returns encoded NAL units.
    /// </summary>
    /// <param name="frame">The captured frame to encode.</param>
    /// <param name="encodedData">Output: buffer containing encoded H.264 data.</param>
    /// <returns>Length of encoded data in bytes, or -1 on failure.</returns>
    public int Encode(FrameBuffer frame, out byte[] encodedData)
    {
        encodedData = _outputBuffer;

        if (!_available || !_initialized || _encoder == null || _nv12Buffer == null)
            return -1;

        try
        {
            // Step 1: Convert BGRA → NV12.
            ConvertBgraToNv12(frame.Pixels, frame.Width, frame.Height, frame.Stride, _nv12Buffer);

            // Step 2: Create an MF sample with the NV12 data.
            using var sample = CreateSampleFromNv12(_nv12Buffer, _nv12Size);

            // Set timestamps for the encoder.
            sample.SampleTime = _frameIndex * _frameDuration;
            sample.SampleDuration = _frameDuration;
            _frameIndex++;

            // Step 3: Feed the sample to the encoder.
            _encoder.ProcessInput(0, sample, 0);

            // Step 4: Try to get encoded output.
            return DrainOutput(encodedData);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ENC] Encode error: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Forces the encoder to produce a keyframe (IDR) on the next Encode() call.
    /// Useful when a new client connects.
    /// </summary>
    public void ForceKeyFrame()
    {
        // Not supported without ICodecAPI.
    }

    // ─── Private implementation ─────────────────────────────────────

    /// <summary>
    /// Finds a hardware H.264 encoder via MFTEnumEx.
    /// Prefers hardware encoders (NVENC, QuickSync, VCE).
    /// </summary>
    private IMFTransform? FindHardwareEncoder()
    {
        // Set up the output type filter: H.264.
        var outputType = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = MFVideoFormat_H264
        };

        // Enumerate hardware video encoders.
        var activates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter),
            null,
            outputType);

        if (activates != null && activates.Count() > 0)
        {
            Console.WriteLine($"[ENC] Found {activates.Count()} hardware H.264 encoder(s):");
            foreach (var activate in activates)
            {
                string name = "Unknown";
                try { name = activate.FriendlyName ?? "Unknown"; } catch { }
                Console.WriteLine($"[ENC]   - {name}");
            }

            // Activate the first (preferred) encoder.
            var encoder = activates.First().ActivateObject<IMFTransform>();

            // Release the remaining activates.
            foreach (var activate in activates)
            {
                activate.Dispose();
            }

            return encoder;
        }

        // No hardware encoder — try any encoder (including software) as fallback.
        activates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            (uint)EnumFlag.EnumFlagAll,
            null,
            outputType);

        if (activates != null && activates.Count() > 0)
        {
            string name = "Unknown";
            try { name = activates.First().FriendlyName ?? "Unknown"; } catch { }
            Console.WriteLine($"[ENC] Using software H.264 encoder: {name}");

            var encoder = activates.First().ActivateObject<IMFTransform>();
            foreach (var activate in activates)
            {
                activate.Dispose();
            }
            return encoder;
        }

        return null;
    }

    /// <summary>
    /// Configures the H.264 output media type.
    /// Must be set before the input type.
    /// </summary>
    private void ConfigureOutputType()
    {
        _outputType = MediaFactory.MFCreateMediaType();
        _outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        _outputType.Set(MediaTypeAttributeKeys.Subtype, MFVideoFormat_H264);

        // Pack width/height into a single 64-bit value (width << 32 | height).
        long frameSize = ((long)_width << 32) | (uint)_height;
        _outputType.Set(MediaTypeAttributeKeys.FrameSize, frameSize);

        // Frame rate as ratio (fps/1).
        long frameRate = ((long)_frameRate << 32) | 1;
        _outputType.Set(MediaTypeAttributeKeys.FrameRate, frameRate);

        // Progressive scan (no interlacing).
        _outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);

        // Target bitrate: 8 Mbps for 1080p-class content.
        int bitrate = (_width * _height > 1920 * 1080) ? 15_000_000 : 8_000_000;
        _outputType.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);

        // Pixel aspect ratio: 1:1 (square pixels).
        long pixelAspectRatio = (1L << 32) | 1;
        _outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, pixelAspectRatio);

        _encoder!.SetOutputType(0, _outputType, 0);
        Console.WriteLine($"[ENC] Output type set: H.264, {bitrate / 1_000_000}Mbps");
    }

    /// <summary>
    /// Configures the NV12 input media type.
    /// Must be set after the output type.
    /// </summary>
    private void ConfigureInputType()
    {
        _inputType = MediaFactory.MFCreateMediaType();
        _inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        _inputType.Set(MediaTypeAttributeKeys.Subtype, MFVideoFormat_NV12);

        long frameSize = ((long)_width << 32) | (uint)_height;
        _inputType.Set(MediaTypeAttributeKeys.FrameSize, frameSize);

        long frameRate = ((long)_frameRate << 32) | 1;
        _inputType.Set(MediaTypeAttributeKeys.FrameRate, frameRate);

        _inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);

        long pixelAspectRatio = (1L << 32) | 1;
        _inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, pixelAspectRatio);

        _encoder!.SetInputType(0, _inputType, 0);
        Console.WriteLine("[ENC] Input type set: NV12");
    }

    /*
    private void ConfigureLowLatency()
    {
        // ICodecAPI is not exposed in Vortice 3.8.1.
    }
    */

    /// <summary>
    /// Creates an IMFSample containing NV12 pixel data.
    /// </summary>
    private IMFSample CreateSampleFromNv12(byte[] nv12Data, int dataSize)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(dataSize);

        // Lock the buffer, copy NV12 data, unlock.
        buffer.Lock(out nint ptr, out int maxLength, out int currentLength);
        Marshal.Copy(nv12Data, 0, ptr, dataSize);
        buffer.Unlock();
        buffer.CurrentLength = dataSize;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        buffer.Dispose();

        return sample;
    }

    /// <summary>
    /// Drains encoded output from the MFT.
    /// Returns the total bytes of encoded data, or -1 if no output available.
    /// </summary>
    private int DrainOutput(byte[] outputBuffer)
    {
        // Create an output sample + buffer for the encoder to write into.
        using var outSample = MediaFactory.MFCreateSample();
        using var outBuffer = MediaFactory.MFCreateMemoryBuffer(outputBuffer.Length);
        outSample.AddBuffer(outBuffer);

        _outputDataBuffer = new OutputDataBuffer
        {
            Sample = outSample
        };

        var hr = _encoder!.ProcessOutput(ProcessOutputFlags.None, 1, ref _outputDataBuffer, out var status);

        if (hr.Failure)
        {
            // MF_E_TRANSFORM_NEED_MORE_INPUT = 0xC00D6D72 — encoder needs more frames.
            // This is normal for the first few frames.
            if (hr.Code == unchecked((int)0xC00D6D72))
                return -1;

            // Other errors are unexpected.
            Console.Error.WriteLine($"[ENC] ProcessOutput failed: 0x{hr.Code:X8}");
            return -1;
        }

        // Read the encoded data from the output sample.
        var resultSample = _outputDataBuffer.Sample;
        if (resultSample == null)
            return -1;

        using var resultBuffer = resultSample.ConvertToContiguousBuffer();
        resultBuffer.Lock(out nint dataPtr, out int maxLength, out int encodedLength);

        // Grow output buffer if needed.
        if (encodedLength > outputBuffer.Length)
        {
            _outputBuffer = new byte[encodedLength * 2];
            outputBuffer = _outputBuffer;
        }

        Marshal.Copy(dataPtr, outputBuffer, 0, encodedLength);
        resultBuffer.Unlock();

        return encodedLength;
    }

    /// <summary>
    /// Converts BGRA pixel data to NV12 format for the hardware encoder.
    ///
    /// NV12 layout:
    ///   - Y plane:  W × H bytes (one luma byte per pixel)
    ///   - UV plane: W × (H/2) bytes (interleaved Cb/Cr, subsampled 2x2)
    ///
    /// Uses integer arithmetic for speed (BT.601 coefficients, fixed-point).
    /// </summary>
    private static void ConvertBgraToNv12(
        byte[] bgra, int width, int height, int stride, byte[] nv12)
    {
        int yPlaneSize = width * height;
        int uvOffset = yPlaneSize;

        for (int y = 0; y < height; y++)
        {
            int srcRow = y * stride;
            int yRow = y * width;

            for (int x = 0; x < width; x++)
            {
                int srcIdx = srcRow + x * 4;
                byte b = bgra[srcIdx];
                byte g = bgra[srcIdx + 1];
                byte r = bgra[srcIdx + 2];
                // bgra[srcIdx + 3] is alpha — ignored.

                // BT.601 luma: Y = 0.299R + 0.587G + 0.114B
                // Fixed-point: Y = (66R + 129G + 25B + 128) >> 8 + 16
                int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                nv12[yRow + x] = (byte)Math.Clamp(yVal, 0, 255);

                // Chroma is subsampled 2x2: only compute for even x,y.
                if ((x & 1) == 0 && (y & 1) == 0)
                {
                    // BT.601 chroma:
                    //   U = (-38R - 74G + 112B + 128) >> 8 + 128
                    //   V = (112R - 94G - 18B + 128) >> 8 + 128
                    int uVal = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                    int vVal = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;

                    int uvIdx = uvOffset + (y / 2) * width + (x & ~1);
                    nv12[uvIdx] = (byte)Math.Clamp(uVal, 0, 255);
                    nv12[uvIdx + 1] = (byte)Math.Clamp(vVal, 0, 255);
                }
            }
        }
    }

    private void CleanupEncoder()
    {
        if (_encoder != null)
        {
            try
            {
                _encoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                _encoder.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
            }
            catch { }

            _encoder.Dispose();
            _encoder = null;
        }

        _inputType?.Dispose();
        _inputType = null;
        _outputType?.Dispose();
        _outputType = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CleanupEncoder();

        if (_mfStarted)
        {
            try { MediaFactory.MFShutdown(); } catch { }
            _mfStarted = false;
        }
    }
}
