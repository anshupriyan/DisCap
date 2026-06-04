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
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private bool _disposed;

    private readonly int _adapterIndex;
    private int _outputIndex;
    private int _width;
    private int _height;
    private readonly int _timeoutMs;

    /// <summary>Width of the captured output in pixels.</summary>
    public int Width => _width;

    /// <summary>Height of the captured output in pixels.</summary>
    public int Height => _height;

    /// <summary>Whether the duplicator is initialized and ready to capture.</summary>
    public bool IsInitialized => _duplication != null;

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
            _width = outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left;
            _height = outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top;
            output.Dispose();

            Console.WriteLine($"[CAP] Output resolution: {_width}x{_height}");

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
            var result = _duplication.AcquireNextFrame(
                (uint)_timeoutMs, out var frameInfo, out var desktopResource);

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

                // Copy the GPU texture to our CPU-readable staging texture.
                using var srcTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                _context.CopyResource(_stagingTexture, srcTexture);

                // Extract dirty rects from DXGI.
                var dirtyRects = GetDirtyRects();
                int totalDirtyArea = 0;
                foreach (var rect in dirtyRects)
                {
                    totalDirtyArea += (rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                }

                // Map the staging texture to read pixels.
                var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
                try
                {
                    var frame = new FrameBuffer(_width, _height, (int)mapped.RowPitch);
                    frame.TimestampTicks = Stopwatch.GetTimestamp();
                    frame.DirtyRects = dirtyRects;
                    frame.TotalDirtyArea = totalDirtyArea;

                    // Copy pixel data from GPU mapped memory to our frame buffer.
                    unsafe
                    {
                        var src = (byte*)mapped.DataPointer;
                        Marshal.Copy((IntPtr)src, frame.Pixels, 0, frame.DataSize);
                    }

                    return frame;
                }
                finally
                {
                    _context.Unmap(_stagingTexture, 0);
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

    private void Cleanup()
    {
        _duplication?.Dispose();
        _duplication = null;
        _stagingTexture?.Dispose();
        _stagingTexture = null;
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
