using System;
using System.Runtime.InteropServices;
using System.Threading;
using Discap.Host.Capture;
using Lennox.NvEncSharp;

namespace Discap.Host.Compression;

public sealed class HardwareEncoder : IVideoEncoder
{
    private bool _available;
    public bool IsAvailable => _available;

    private NvEncoder _encoder;
    private NvEncRegisteredPtr _registeredTexture;
    private NvEncOutputPtr _bitstreamBuffer;
    
    private int _width;
    private int _height;
    private int _bitrate;
    private int _frameRate;
    
    private byte[] _outputBuffer = new byte[1024 * 1024 * 2]; // 2MB max frame size

    /// <summary>Set by the capture loop before each SubmitFrame call so encoder logs carry the same iteration number.</summary>
    public long DiagIteration { get; set; }

    public unsafe bool Initialize(int width, int height, int frameRate = 60, int bitrate = 8_000_000)
    {
        _width = width;
        _height = height;
        _bitrate = bitrate;
        _frameRate = frameRate;

        if (LibNvEnc.TryInitialize(out var error) != LibNcEncInitializeStatus.Success)
        {
            Console.Error.WriteLine($"[ENC] NVENC Initialize failed: {error}");
            return false;
        }

        Console.WriteLine("[ENC] NVENC Direct API Initialized.");
        _available = true;
        return true;
    }

    public unsafe bool OpenDevice(IntPtr d3d11DeviceHandle)
    {
        if (!_available) return false;

        var openParams = new NvEncOpenEncodeSessionExParams
        {
            Version = LibNvEnc.NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER,
            DeviceType = NvEncDeviceType.Directx,
            Device = d3d11DeviceHandle,
            ApiVersion = LibNvEnc.NVENCAPI_VERSION
        };

        var status = LibNvEnc.FunctionList.OpenEncodeSessionEx(ref openParams, out _encoder);
        if (status != NvEncStatus.Success)
        {
            Console.Error.WriteLine($"[ENC] OpenEncodeSessionEx failed: {status} (0x{(int)status:X8})");
            _available = false;
            return false;
        }

        var presetConfig = new NvEncPresetConfig
        {
            Version = LibNvEnc.NV_ENC_PRESET_CONFIG_VER,
            PresetCfg = new NvEncConfig { Version = LibNvEnc.NV_ENC_CONFIG_VER }
        };

        status = LibNvEnc.FunctionList.GetEncodePresetConfigEx(_encoder, NvEncCodecGuids.H264, NvEncPresetGuids.P3, (uint)NvEncTuningInfo.LowLatency, ref presetConfig);
        if (status != NvEncStatus.Success)
        {
            Console.Error.WriteLine($"[ENC] GetEncodePresetConfigEx failed: {status} (0x{(int)status:X8})");
            return false;
        }

        var initParams = new NvEncInitializeParams
        {
            Version = LibNvEnc.NV_ENC_INITIALIZE_PARAMS_VER,
            EncodeGuid = NvEncCodecGuids.H264,
            PresetGuid = NvEncPresetGuids.P3,
            TuningInfo = NvEncTuningInfo.LowLatency,
            EncodeWidth = (uint)_width,
            EncodeHeight = (uint)_height,
            DarWidth = (uint)_width,
            DarHeight = (uint)_height,
            FrameRateNum = (uint)_frameRate,
            FrameRateDen = 1,
            EnableEncodeAsync = 0,
            EnablePTD = 1,
            ReportSliceOffsets = false,
            MaxEncodeWidth = (uint)_width,
            MaxEncodeHeight = (uint)_height
        };

        initParams.EncodeConfig = &presetConfig.PresetCfg;
        initParams.EncodeConfig->RcParams.RateControlMode = NvEncParamsRcMode.Cbr;
        initParams.EncodeConfig->RcParams.AverageBitRate = 8000000;
        initParams.EncodeConfig->RcParams.MaxBitRate = 8000000;
        initParams.EncodeConfig->RcParams.ZeroReorderDelay = true;
        initParams.EncodeConfig->GopLength = 120;
        initParams.EncodeConfig->FrameIntervalP = 1; // B-frames = 0

        status = LibNvEnc.FunctionList.InitializeEncoder(_encoder, ref initParams);
        if (status != NvEncStatus.Success)
        {
            Console.Error.WriteLine($"[ENC] InitializeEncoder failed: {status} (0x{(int)status:X8})");
            _available = false;
            return false;
        }

        // Create bitstream buffer
        var bitstreamParams = new NvEncCreateBitstreamBuffer { Version = LibNvEnc.NV_ENC_CREATE_BITSTREAM_BUFFER_VER };
        status = LibNvEnc.FunctionList.CreateBitstreamBuffer(_encoder, ref bitstreamParams);
        if (status != NvEncStatus.Success)
        {
            Console.Error.WriteLine($"[ENC] CreateBitstreamBuffer failed: {status} (0x{(int)status:X8})");
            return false;
        }
        _bitstreamBuffer = bitstreamParams.BitstreamBuffer;

        Console.WriteLine("[ENC] NVENC Session configured for D3D11 NV12 streaming.");
        return true;
    }

    private IntPtr _lastTexturePointer;

    /// <summary>
    /// True between a successful EncodePicture call and the moment all output packets
    /// have been drained by TryGetNextPacket. Guards LockBitstream so it is never
    /// called on a buffer that has no pending output — which would block indefinitely.
    /// </summary>
    private bool _hasPendingOutput;

    public unsafe void SubmitFrame(FrameBuffer frame)
    {
        if (!_available) return;

        // In Direct NVENC, we expect FrameBuffer.GpuTexture to contain the ID3D11Texture2D.
        if (frame.GpuTexture == null) return;
        var texturePtr = frame.GpuTexture.NativePointer;
        if (texturePtr == IntPtr.Zero) return;

        // Register texture if it's new
        if (texturePtr != _lastTexturePointer)
        {
            if (_registeredTexture.Handle != IntPtr.Zero)
            {
                LibNvEnc.FunctionList.UnregisterResource(_encoder, _registeredTexture);
                _registeredTexture.Handle = IntPtr.Zero;
            }

            var regParams = new NvEncRegisterResource
            {
                Version = LibNvEnc.NV_ENC_REGISTER_RESOURCE_VER,
                ResourceType = NvEncInputResourceType.Directx,
                ResourceToRegister = texturePtr,
                Width = (uint)_width,
                Height = (uint)_height, // Ensure this is normal height
                Pitch = (uint)_width,   // NV12 pitch is width for Y plane
                BufferFormat = NvEncBufferFormat.Nv12
            };

            var status = LibNvEnc.FunctionList.RegisterResource(_encoder, ref regParams);
            if (status != NvEncStatus.Success)
            {
                Console.Error.WriteLine($"[ENC] RegisterResource failed: {status}");
                return;
            }
            _registeredTexture = regParams.RegisteredResource;
            _lastTexturePointer = texturePtr;
        }

        // Map resource
        var mapParams = new NvEncMapInputResource
        {
            Version = LibNvEnc.NV_ENC_MAP_INPUT_RESOURCE_VER,
            RegisteredResource = _registeredTexture
        };
        if (LibNvEnc.FunctionList.MapInputResource(_encoder, ref mapParams) != NvEncStatus.Success) return;

        // Encode picture
        var picParams = new NvEncPicParams
        {
            Version = LibNvEnc.NV_ENC_PIC_PARAMS_VER,
            InputWidth = (uint)_width,
            InputHeight = (uint)_height,
            InputPitch = (uint)_width,
            InputBuffer = mapParams.MappedResource,
            OutputBitstream = _bitstreamBuffer,
            BufferFmt = mapParams.MappedBufferFmt,
            PictureStruct = NvEncPicStruct.Frame
        };

        Console.WriteLine($"[ENC] {DiagIteration}: calling NvEncEncodePicture...");
        var encStatus = LibNvEnc.FunctionList.EncodePicture(_encoder, ref picParams);
        Console.WriteLine($"[ENC] {DiagIteration}: NvEncEncodePicture returned {encStatus}");

        // Signal that there is real output waiting to be read.
        if (encStatus == NvEncStatus.Success)
            _hasPendingOutput = true;

        // Unmap resource
        LibNvEnc.FunctionList.UnmapInputResource(_encoder, mapParams.MappedResource);
    }

    private Queue<byte[]> _naluQueue = new Queue<byte[]>();

    public unsafe bool TryGetNextPacket(out byte[] naluData, out int naluSize, int timeoutMs)
    {
        naluData = _outputBuffer;
        naluSize = 0;
        if (!_available) return false;

        // Nothing was submitted since the last full drain — bail out immediately.
        // This is the critical guard: without it, LockBitstream blocks forever
        // on a buffer that has no pending output.
        if (!_hasPendingOutput && _naluQueue.Count == 0) return false;

        if (_naluQueue.Count > 0)
        {
            var data = _naluQueue.Dequeue();
            naluSize = data.Length;
            if (naluSize <= _outputBuffer.Length)
            {
                Array.Copy(data, _outputBuffer, naluSize);
            }
            int nalType = 0;
            if (naluSize > 4 && _outputBuffer[0] == 0 && _outputBuffer[1] == 0 && _outputBuffer[2] == 0 && _outputBuffer[3] == 1)
                nalType = _outputBuffer[4] & 0x1F;
            else if (naluSize > 3 && _outputBuffer[0] == 0 && _outputBuffer[1] == 0 && _outputBuffer[2] == 1)
                nalType = _outputBuffer[3] & 0x1F;
                
            Console.WriteLine($"[ENC] NAL type={nalType} size={naluSize} bytes");
            // If queue is now empty, the pending output has been fully drained.
            if (_naluQueue.Count == 0) _hasPendingOutput = false;
            return true;
        }

        if (_bitstreamBuffer.Handle == IntPtr.Zero) return false;

        var lockParams = new NvEncLockBitstream
        {
            Version = LibNvEnc.NV_ENC_LOCK_BITSTREAM_VER,
            OutputBitstream = _bitstreamBuffer.Handle
        };

        var status = LibNvEnc.FunctionList.LockBitstream(_encoder, ref lockParams);
        if (status != NvEncStatus.Success) return false;

        int totalSize = (int)lockParams.BitstreamSizeInBytes;
        
        if (totalSize > 0)
        {
            byte[] frameData = new byte[totalSize];
            Marshal.Copy(lockParams.BitstreamBufferPtr, frameData, 0, totalSize);
            ExtractNalUnits(frameData);
        }

        LibNvEnc.FunctionList.UnlockBitstream(_encoder, _bitstreamBuffer);

        if (_naluQueue.Count > 0)
        {
            var data = _naluQueue.Dequeue();
            naluSize = data.Length;
            if (naluSize <= _outputBuffer.Length)
            {
                Array.Copy(data, _outputBuffer, naluSize);
            }
            int nalType = 0;
            if (naluSize > 4 && _outputBuffer[0] == 0 && _outputBuffer[1] == 0 && _outputBuffer[2] == 0 && _outputBuffer[3] == 1)
                nalType = _outputBuffer[4] & 0x1F;
            else if (naluSize > 3 && _outputBuffer[0] == 0 && _outputBuffer[1] == 0 && _outputBuffer[2] == 1)
                nalType = _outputBuffer[3] & 0x1F;
                
            Console.WriteLine($"[ENC] NAL type={nalType} size={naluSize} bytes");
            // If queue is now empty, the pending output has been fully drained.
            if (_naluQueue.Count == 0) _hasPendingOutput = false;
            return true;
        }

        // LockBitstream returned no data — treat as fully drained.
        _hasPendingOutput = false;
        return false;
    }

    private void ExtractNalUnits(byte[] stream)
    {
        int offset = IndexOfStartCode(stream, 0);
        if (offset == -1) return;

        while (offset < stream.Length)
        {
            int nextStart = IndexOfStartCode(stream, offset + 3);
            int naluLength = nextStart == -1 ? stream.Length - offset : nextStart - offset;
            
            byte[] nalu = new byte[naluLength];
            Array.Copy(stream, offset, nalu, 0, naluLength);
            _naluQueue.Enqueue(nalu);
            
            if (nextStart == -1) break;
            offset = nextStart;
        }
    }

    private int IndexOfStartCode(byte[] data, int startIndex)
    {
        for (int i = startIndex; i < data.Length - 2; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1)
                    return i;
                if (i < data.Length - 3 && data[i + 2] == 0 && data[i + 3] == 1)
                    return i;
            }
        }
        return -1;
    }

    public void SetTargetBitrate(int bitrate)
    {
        // Reconfiguration omitted for brevity, fallback to CBR
    }

    public void ForceKeyFrame()
    {
        // P/Invoke force IDR flag not strictly necessary for simple desktop streaming
    }

    public void Dispose()
    {
        _available = false;
        if (_encoder.Handle != IntPtr.Zero)
        {
            if (_bitstreamBuffer.Handle != IntPtr.Zero)
                LibNvEnc.FunctionList.DestroyBitstreamBuffer(_encoder, _bitstreamBuffer);
            if (_registeredTexture.Handle != IntPtr.Zero)
                LibNvEnc.FunctionList.UnregisterResource(_encoder, _registeredTexture);
                
            LibNvEnc.FunctionList.DestroyEncoder(_encoder);
            _encoder.Handle = IntPtr.Zero;
        }
    }
}
