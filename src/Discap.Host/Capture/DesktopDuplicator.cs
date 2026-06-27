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
    private ID3D11Texture2D? _nvencTexture;
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

    /// <summary>Whether the duplicator is initialized and ready to capture.</summary>
    public bool IsInitialized => _duplication != null;

    /// <summary>The D3D11 device used by the duplicator. Exposed for zero-copy GPU integration.</summary>
    public ID3D11Device? Device => _device;

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
            
            DEVMODE dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            int refreshRate = 60; // fallback
            _deviceName = outputDesc.DeviceName;
            if (EnumDisplaySettings(_deviceName, ENUM_CURRENT_SETTINGS, ref dm))
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
            _nvencTexture = _device.CreateTexture2D(nvencDesc);

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

            using (desktopResource)
            {
                if (desktopResource == null) return null;

                // Copy the GPU texture to our dedicated GPU texture for compute processing.
                using var srcTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                _context.CopyResource(_gpuTexture!, srcTexture);

                UpdatePointerShape(frameInfo);

                if (frameInfo.LastMouseUpdateTime > 0)
                {
                    _lastPointerPosition = frameInfo.PointerPosition;
                }
                
                // Always force cursor to be visible, ignoring hardware visibility state as requested
                _lastPointerPosition.Visible = true;

                var nv12Target = _colorConverter!.EnsureOutputTexture(_width, _height);
                int cursorHeight = _pointerShapeInfo.Type == (uint)PointerShapeType.Monochrome ? (int)(_pointerShapeInfo.Height / 2) : (int)_pointerShapeInfo.Height;
                
                long t2 = Stopwatch.GetTimestamp();
                _colorConverter.Convert(
                    _gpuSrv!, 
                    _lastPointerPosition.Position.X, 
                    _lastPointerPosition.Position.Y, 
                    (int)_pointerShapeInfo.Width, 
                    cursorHeight);

                _context.CopyResource(_stagingNv12Texture!, nv12Target);
                
                // Copy to the plain NVENC texture (must have no bind flags and NV12 format)
                _context.CopyResource(_nvencTexture!, nv12Target);
                
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

                // Map the staging NV12 texture to read pixels.
                var mapped = _context.Map(_stagingNv12Texture, 0, MapMode.Read);
                try
                {
                    var frame = new FrameBuffer(_width, _height, (int)mapped.RowPitch, PixelFormat.NV12);
                    frame.TimestampTicks = t0;
                    frame.CaptureTimeMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                    frame.ConvertTimeMs = (t3 - t2) * 1000.0 / Stopwatch.Frequency;
                    frame.DirtyRects = dirtyRects;
                    frame.TotalDirtyArea = totalDirtyArea;
                    frame.GpuTexture = _nvencTexture;

                    // Copy NV12 pixel data from GPU mapped memory to our frame buffer.
                    unsafe
                    {
                        if (frame.Pixels != null)
                        {
                            Marshal.Copy(mapped.DataPointer, frame.Pixels, 0, frame.DataSize);
                        }
                    }

                    return frame;
                }
                finally
                {
                    _context.Unmap(_stagingNv12Texture, 0);
                }
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

    /// <summary>
    /// Re-queries the current display refresh rate from the OS without
    /// reinitializing the duplication session. Call periodically (e.g.
    /// once per second) to detect midstream refresh rate changes.
    /// </summary>
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
        _nvencTexture?.Dispose();
        _nvencTexture = null;
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
