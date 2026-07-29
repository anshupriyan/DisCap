using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Discap.Host.Capture;

/// <summary>
/// Captures desktop frames using the DXGI Desktop Duplication API.
///
/// The pipeline:
/// 1. Creates a D3D11 device on the target GPU adapter
/// 2. Duplicates the specified output (monitor)
/// 3. AcquireNextFrame() gets the latest desktop texture (GPU memory)
/// 4. CopyResource to a CPU-readable staging texture
/// 5. Map/Unmap to read raw BGRA pixels into a FrameBuffer
///
/// Handles access-lost errors gracefully (display mode changes, UAC, etc.)
/// by automatically reinitializing the duplication session.
/// </summary>
public sealed class DesktopDuplicator : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    private const int ENUM_CURRENT_SETTINGS = -1;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private ID3D11Texture2D? _gpuTexture;
    private ID3D11ShaderResourceView? _gpuSrv;
    private ID3D11Texture2D? _stagingNv12Texture;
    private ID3D11Texture2D[] _nvencTextures = Array.Empty<ID3D11Texture2D>();
    private int _nvencTextureIndex = 0;
    private ColorConverter? _colorConverter;
    private byte[] _pointerShapeBuffer = Array.Empty<byte>();
    private OutduplPointerShapeInfo _pointerShapeInfo;
    private OutduplPointerPosition _lastPointerPosition;
    private bool _disposed;

    private readonly int _adapterIndex;
    private int _outputIndex;
    private int _width;
    private int _height;
    private readonly int _timeoutMs;
    private int _lastSentCursorX = -1;
    private int _lastSentCursorY = -1;
    private string _deviceName = string.Empty;

    /// <summary>Width of the captured output in pixels.</summary>
    public int Width => _width;

    /// <summary>Height of the captured output in pixels.</summary>
    public int Height => _height;

    /// <summary>X offset of the captured output on the virtual desktop.</summary>
    public int BoundsX { get; private set; }

    /// <summary>Y coordinate of the captured output on the virtual desktop.</summary>
    public int BoundsY { get; private set; }
    
    public int CurrentRefreshRate { get; private set; }

    /// <summary>The GDI device name of the captured display (e.g. \\.\DISPLAY2).</summary>
    public string DeviceName { get; private set; } = string.Empty;

    /// <summary>Whether the duplicator is initialized and ready to capture.</summary>
    public bool IsInitialized => _duplication != null;

    /// <summary>The D3D11 device used by the duplicator. Exposed for zero-copy GPU integration.</summary>
    public ID3D11Device? Device => _device;

    /// <summary>Exposes the last DXGI pointer position and visibility.</summary>
    public OutduplPointerPosition LastPointerPosition => _lastPointerPosition;

    /// <summary>Exposes the current DXGI pointer shape info.</summary>
    public OutduplPointerShapeInfo PointerShapeInfo => _pointerShapeInfo;

    /// <summary>Exposes the current raw DXGI pointer shape pixel/mask buffer.</summary>
    public byte[] PointerShapeBuffer => _pointerShapeBuffer;

    public DesktopDuplicator(int adapterIndex = 0, int timeoutMs = 100)
    {
        _adapterIndex = adapterIndex;
        _timeoutMs = timeoutMs;
    }

    /// <summary>
    /// Initializes the Desktop Duplication session.
    /// Optionally targets a specific output index. If -1, uses the last output
    /// (which is typically the newly added virtual display).
    /// </summary>
    /// <param name="targetOutputIndex">
    /// Output index to capture. Use -1 to auto-detect the last output (virtual display).
    /// </param>
    /// <returns>True if initialization succeeded.</returns>
    public bool Initialize(int targetOutputIndex = -1)
    {
        try
        {
            Cleanup();

            // Create DXGI factory and get the adapter.
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            factory.EnumAdapters1((uint)_adapterIndex, out var adapter);
            if (adapter == null)
            {
                Console.Error.WriteLine("[CAP] Failed to get GPU adapter");
                return false;
            }

            Console.WriteLine($"[CAP] Using adapter: {adapter.Description.Description}");

            // Create D3D11 device on this adapter.
            D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out _device,
                out _context).CheckError();

            if (_device == null || _context == null)
            {
                Console.Error.WriteLine("[CAP] Failed to create D3D11 device");
                adapter.Dispose();
                return false;
            }

            // Find the target output.
            // If targetOutputIndex == -1, use the last output (the virtual display).
            int outputCount = 0;
            while (true)
            {
                var hr = adapter.EnumOutputs((uint)outputCount, out var tempOut);
                if (hr.Failure)
                    break;
                tempOut?.Dispose();
                outputCount++;
            }

            if (outputCount == 0)
            {
                Console.Error.WriteLine("[CAP] No display outputs found on this adapter");
                adapter.Dispose();
                return false;
            }

            _outputIndex = targetOutputIndex >= 0 ? targetOutputIndex : outputCount - 1;
            Console.WriteLine($"[CAP] Targeting output index {_outputIndex} (of {outputCount} total)");

            // Get the target output and duplicate it.
            adapter.EnumOutputs((uint)_outputIndex, out var output);
            adapter.Dispose(); // Done with adapter.

            if (output == null)
            {
                Console.Error.WriteLine("[CAP] Failed to get target output");
                return false;
            }

            using var output1 = output.QueryInterface<IDXGIOutput1>();
            var outputDesc = output.Description;
            BoundsX = outputDesc.DesktopCoordinates.Left;
            BoundsY = outputDesc.DesktopCoordinates.Top;
            _width = outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left;
            _height = outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top;
            Console.WriteLine($"[INIT] Captured Display Bounds: X={BoundsX}, Y={BoundsY}, W={_width}, H={_height}");
            
            DEVMODE dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            int refreshRate = 60; // fallback
            DeviceName = outputDesc.DeviceName;
            if (EnumDisplaySettings(DeviceName, ENUM_CURRENT_SETTINGS, ref dm))
            {
                refreshRate = dm.dmDisplayFrequency;
            }
            CurrentRefreshRate = refreshRate;
            
            output.Dispose();

            Console.WriteLine($"[CAP] Virtual display active at {_width}x{_height} @ {refreshRate}Hz");

            _duplication = output1.DuplicateOutput(_device);

            // Create staging texture for CPU readback.
            var stagingDesc = new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };
            _stagingTexture = _device.CreateTexture2D(stagingDesc);

            // Create dedicated GPU texture for zero-copy encoding (e.g. Video Processor MFT).
            var gpuDesc = new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };
            _gpuTexture = _device.CreateTexture2D(gpuDesc);
            _gpuSrv = _device.CreateShaderResourceView(_gpuTexture);

            _colorConverter = new ColorConverter(_device, _context);

            var stagingNv12Desc = new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };
            _stagingNv12Texture = _device.CreateTexture2D(stagingNv12Desc);

            var nvencDesc = new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };
            _nvencTextures = new ID3D11Texture2D[3];
            for (int i = 0; i < 3; i++)
            {
                _nvencTextures[i] = _device.CreateTexture2D(nvencDesc);
            }

            SetDefaultCursorShape();

            SetDefaultCursorShape();

            Console.WriteLine("[CAP] Desktop Duplication initialized");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CAP] Initialization failed: {ex.Message}");
            Cleanup();
            return false;
        }
    }

    /// <summary>
    /// Captures the next desktop frame.
    /// Returns null if no new frame is available (timeout) or on error.
    /// On access-lost errors, attempts automatic reinitialization.
    /// </summary>
    public FrameBuffer? AcquireNextFrame()
    {
        if (_duplication == null || _device == null || _context == null || _stagingTexture == null)
            return null;

        try
        {
            long t0 = Stopwatch.GetTimestamp();
            var result = _duplication.AcquireNextFrame(
                (uint)_timeoutMs, out var frameInfo, out var desktopResource);
            long t1 = Stopwatch.GetTimestamp();
            double acquireMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;

            if (result.Failure)
            {
                // DXGI_ERROR_WAIT_TIMEOUT — no new frame, this is normal.
                if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
                    return null;

                // DXGI_ERROR_ACCESS_LOST — display mode changed, need to reinitialize.
                Console.Error.WriteLine($"[CAP] AcquireNextFrame failed: 0x{result.Code:X8} — reinitializing...");
                desktopResource?.Dispose();
                Reinitialize();
                return null;
            }

            Console.WriteLine($"[CAP] AcquireNextFrame returned: AccumulatedFrames={frameInfo.AccumulatedFrames}, acquireMs={acquireMs:F2}ms");

            UpdatePointerShape(frameInfo);

            if (frameInfo.LastMouseUpdateTime > 0)
            {
                _lastPointerPosition = frameInfo.PointerPosition;
            }

            if (desktopResource == null)
            {
                return null;
            }

            using (desktopResource)
            {
                // Copy the GPU texture to our dedicated GPU texture for compute processing.
                using var srcTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                _context.CopyResource(_gpuTexture!, srcTexture);
            }

            _lastSentCursorX = _lastPointerPosition.Position.X;
            _lastSentCursorY = _lastPointerPosition.Position.Y;

            var nv12Target = _colorConverter!.EnsureOutputTexture(_width, _height);
            
            long t2 = Stopwatch.GetTimestamp();
            // Pass -10000, -10000, 0, 0 to bypass burning cursor onto pure video texture
            _colorConverter.Convert(
                _gpuSrv!, 
                -10000, 
                -10000, 
                0, 
                0);

            _context.CopyResource(_stagingNv12Texture!, nv12Target);
            
            var currentNvencTexture = _nvencTextures[_nvencTextureIndex % 3];
            _nvencTextureIndex++;

            // Copy to the plain NVENC texture (must have no bind flags and NV12 format)
            _context.CopyResource(currentNvencTexture, nv12Target);
            
            // Flush the GPU context to ensure the compute shader and copy are completed
            // before NVENC attempts to read from the texture.
            _context.Flush();
            
            long t3 = Stopwatch.GetTimestamp();

            // Extract dirty rects from DXGI.
            var dirtyRects = GetDirtyRects();
            int totalDirtyArea = 0;
            foreach (var rect in dirtyRects)
            {
                totalDirtyArea += (rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            }

            long tCopyStart = Stopwatch.GetTimestamp();
            // Map the staging NV12 texture to read pixels.
            var mapped = _context.Map(_stagingNv12Texture, 0, MapMode.Read);
            try
            {
                var frame = new FrameBuffer(_width, _height, (int)mapped.RowPitch, PixelFormat.NV12);
                frame.TimestampTicks = t0;
                frame.CaptureTimeMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                frame.AcquireTimeMs = acquireMs;
                frame.ConvertTimeMs = (t3 - t2) * 1000.0 / Stopwatch.Frequency;
                frame.AccumulatedFrames = (int)frameInfo.AccumulatedFrames;
                frame.DirtyRects = dirtyRects;
                frame.TotalDirtyArea = totalDirtyArea;
                frame.GpuTexture = currentNvencTexture;

                // Copy NV12 pixel data from GPU mapped memory to our frame buffer.
                unsafe
                {
                    if (frame.Pixels != null)
                    {
                        Marshal.Copy(mapped.DataPointer, frame.Pixels, 0, frame.DataSize);
                    }
                }

                frame.ReadbackTimeMs = (Stopwatch.GetTimestamp() - tCopyStart) * 1000.0 / Stopwatch.Frequency;
                return frame;
            }
            finally
            {
                _context.Unmap(_stagingNv12Texture, 0);
            }
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (
            ex.HResult == Vortice.DXGI.ResultCode.AccessLost.Code)
        {
            Console.Error.WriteLine("[CAP] Access lost — reinitializing...");
            try { _duplication?.ReleaseFrame(); } catch { }
            Reinitialize();
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CAP] Frame capture error: {ex.Message}");
            try { _duplication?.ReleaseFrame(); } catch { }
            return null;
        }
        finally
        {
            try { _duplication?.ReleaseFrame(); } catch { }
        }
    }

    private void UpdatePointerShape(OutduplFrameInfo frameInfo)
    {
        if (_duplication == null || frameInfo.PointerShapeBufferSize == 0)
            return;

        if (_pointerShapeBuffer.Length < frameInfo.PointerShapeBufferSize)
            _pointerShapeBuffer = new byte[frameInfo.PointerShapeBufferSize];

        var handle = GCHandle.Alloc(_pointerShapeBuffer, GCHandleType.Pinned);
        try
        {
            var hr = _duplication.GetFramePointerShape(
                frameInfo.PointerShapeBufferSize,
                handle.AddrOfPinnedObject(),
                out uint required,
                out var shapeInfo);

            if (hr.Success)
            {
                _pointerShapeInfo = shapeInfo;
                if (required > 0 && required < _pointerShapeBuffer.Length)
                    Array.Clear(_pointerShapeBuffer, (int)required, _pointerShapeBuffer.Length - (int)required);

                var bitmap = CursorCompositor.ExtractCursorBitmap(shapeInfo, _pointerShapeBuffer);
                if (bitmap != null)
                {
                    int cursorHeight = shapeInfo.Type == (uint)PointerShapeType.Monochrome ? (int)(shapeInfo.Height / 2) : (int)shapeInfo.Height;
                    _colorConverter?.UpdateCursor((int)shapeInfo.Width, cursorHeight, bitmap);
                }
            }
        }
        catch
        {
            // Keep the last valid shape; DXGI still provides position updates on later frames.
        }
        finally
        {
            handle.Free();
        }
    }

    private int EstimatePointerArea()
    {
        int height = (int)_pointerShapeInfo.Height;
        if ((PointerShapeType)_pointerShapeInfo.Type == PointerShapeType.Monochrome)
            height /= 2;

        return Math.Max(1, (int)_pointerShapeInfo.Width * Math.Max(1, height));
    }

    private void UploadCpuFrameToGpuTexture(FrameBuffer frame)
    {
        if (_context == null || _gpuTexture == null || frame.Pixels == null)
            return;

        var handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            _context.UpdateSubresource(
                _gpuTexture,
                0,
                null,
                handle.AddrOfPinnedObject(),
                (uint)frame.Stride,
                0);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Gets the dirty rectangles from the last acquired frame.
    /// These indicate which screen regions changed since the previous frame.
    /// </summary>
    private RawRect[] GetDirtyRects()
    {
        if (_duplication == null) return Array.Empty<RawRect>();

        try
        {
            var rects = new RawRect[64]; // Space for up to 64 dirty rects.
            var hr = _duplication.GetFrameDirtyRects((uint)(rects.Length * Marshal.SizeOf<RawRect>()), rects, out uint rectsSize);
            if (hr.Failure || rectsSize == 0)
                return Array.Empty<RawRect>();

            uint rectCount = rectsSize / (uint)Marshal.SizeOf<RawRect>();
            if (rectCount == 0)
                return Array.Empty<RawRect>();

            // Return only the filled portion.
            var result = new RawRect[rectCount];
            Array.Copy(rects, result, (int)rectCount);
            return result;
        }
        catch
        {
            return Array.Empty<RawRect>();
        }
    }

    private void Reinitialize()
    {
        Console.WriteLine("[CAP] Reinitializing Desktop Duplication...");
        Thread.Sleep(500); // Brief pause before retry.
        Initialize(_outputIndex);
    }

    public bool RecompositeCursorIfMoved(FrameBuffer frame)
    {
        if (_device == null || _context == null || _colorConverter == null || _stagingNv12Texture == null || _gpuSrv == null)
            return false;

        if (frame.Width != _width || frame.Height != _height)
            return false;

        int cursorX = _lastPointerPosition.Position.X;
        int cursorY = _lastPointerPosition.Position.Y;

        if (cursorX == _lastSentCursorX && cursorY == _lastSentCursorY)
            return false;

        _lastSentCursorX = cursorX;
        _lastSentCursorY = cursorY;

        var nv12Target = _colorConverter.EnsureOutputTexture(_width, _height);
        int cursorHeight = _pointerShapeInfo.Type == (uint)PointerShapeType.Monochrome ? (int)(_pointerShapeInfo.Height / 2) : (int)_pointerShapeInfo.Height;

        _colorConverter.Convert(
            _gpuSrv, 
            cursorX, 
            cursorY, 
            (int)_pointerShapeInfo.Width, 
            cursorHeight);

        _context.CopyResource(_stagingNv12Texture, nv12Target);
        _context.CopyResource(_nvencTextures[(_nvencTextureIndex - 1 + 3) % 3], nv12Target);
        _context.Flush();

        var mapped = _context.Map(_stagingNv12Texture, 0, MapMode.Read);
        try
        {
            unsafe
            {
                if (frame.Pixels != null)
                {
                    Marshal.Copy(mapped.DataPointer, frame.Pixels, 0, frame.DataSize);
                }
            }
        }
        finally
        {
            _context.Unmap(_stagingNv12Texture, 0);
        }

        return true;
    }

    /// <summary>
    /// Performs a trivial GPU operation to prevent the GPU from entering a deep
    /// power-saving / clock-gated state during idle periods (no desktop changes).
    /// Uses a 1×1 texel copy between two already-allocated textures — effectively
    /// zero GPU workload but enough command-processor activity to keep clocks up.
    /// Call this periodically (e.g. every 500ms–1s) on the idle path only.
    /// </summary>
    public void GpuKeepAlive()
    {
        if (_context == null || _gpuTexture == null || _stagingTexture == null)
            return;

        // Copy a single 1×1 pixel region from the GPU texture to the staging texture.
        // This is a trivial DMA operation that keeps the GPU command processor awake
        // without any meaningful workload or pipeline disruption.
        var box = new Vortice.Mathematics.Box(0, 0, 0, 1, 1, 1);
        _context.CopySubresourceRegion(_stagingTexture, 0, 0, 0, 0, _gpuTexture, 0, box);
        _context.Flush();
    }

    /// <summary>
    /// Re-queries the current display refresh rate from the OS without
    /// reinitializing the duplication session. Call periodically (e.g.
    /// once per second) to detect midstream refresh rate changes.
    /// </summary>
    private void SetDefaultCursorShape()
    {
        _pointerShapeInfo = new OutduplPointerShapeInfo
        {
            Type = (uint)PointerShapeType.Color,
            Width = 32,
            Height = 32,
            Pitch = 32 * 4,
            HotSpot = default
        };

        _pointerShapeBuffer = new byte[32 * 32 * 4];

        string[] arrow = new string[]
        {
            "X...............",
            "XX..............",
            "XOX.............",
            "XOOX............",
            "XOOOX...........",
            "XOOOOX..........",
            "XOOOOOX.........",
            "XOOOOOOX........",
            "XOOOOOOOX.......",
            "XOOOOOOOOX......",
            "XOOOOOOOOOX.....",
            "XOOOOOOOOOOX....",
            "XOOOOOOOOOOOX...",
            "XOOOOOOOOOOOOX..",
            "XOOOOOOOOOOOOOX.",
            "XOOOOOOOOXXXXXXX",
            "XOOOXXOOOX......",
            "XOOX..XOOOX.....",
            "XOX....XOOOX....",
            "XX......XOOOX...",
            "X........XOOOX..",
            "..........XOOOX.",
            "...........XXXXX"
        };

        for (int y = 0; y < arrow.Length && y < 32; y++)
        {
            string row = arrow[y];
            for (int x = 0; x < row.Length && x < 32; x++)
            {
                char c = row[x];
                int offset = (y * 32 + x) * 4;
                if (c == 'X')
                {
                    _pointerShapeBuffer[offset + 0] = 0;   // B
                    _pointerShapeBuffer[offset + 1] = 0;   // G
                    _pointerShapeBuffer[offset + 2] = 0;   // R
                    _pointerShapeBuffer[offset + 3] = 255; // A
                }
                else if (c == 'O')
                {
                    _pointerShapeBuffer[offset + 0] = 255; // B
                    _pointerShapeBuffer[offset + 1] = 255; // G
                    _pointerShapeBuffer[offset + 2] = 255; // R
                    _pointerShapeBuffer[offset + 3] = 255; // A
                }
            }
        }
    }

    public void RefreshCurrentRefreshRate()
    {
        if (string.IsNullOrEmpty(_deviceName)) return;
        
        var dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        if (EnumDisplaySettings(_deviceName, ENUM_CURRENT_SETTINGS, ref dm))
        {
            int newRate = dm.dmDisplayFrequency;
            if (newRate != CurrentRefreshRate)
            {
                Console.WriteLine($"[CAP] Refresh rate changed: {CurrentRefreshRate}Hz -> {newRate}Hz");
                CurrentRefreshRate = newRate;
            }
        }
    }

    private void Cleanup()
    {
        _duplication?.Dispose();
        _duplication = null;
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _stagingNv12Texture?.Dispose();
        _stagingNv12Texture = null;
        if (_nvencTextures != null)
        {
            foreach (var tex in _nvencTextures)
            {
                tex?.Dispose();
            }
            _nvencTextures = Array.Empty<ID3D11Texture2D>();
        }
        _gpuTexture?.Dispose();
        _gpuTexture = null;
        _gpuSrv?.Dispose();
        _gpuSrv = null;
        _colorConverter?.Dispose();
        _colorConverter = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
    }
}
