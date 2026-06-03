using Microsoft.Win32.SafeHandles;

namespace Discap.Host.Display;

/// <summary>
/// Manages the lifecycle of a Parsec VDD virtual display.
/// Handles opening the device, adding/removing displays, and the critical
/// keep-alive ping timer (must fire every ~100ms or displays disconnect).
/// </summary>
public sealed class VirtualDisplayManager : IDisposable
{
    private SafeFileHandle? _deviceHandle;
    private int _displayIndex = -1;
    private Timer? _pingTimer;
    private volatile bool _disposed;

    /// <summary>True if a virtual display is currently active.</summary>
    public bool IsDisplayActive => _displayIndex >= 0 && _deviceHandle is { IsInvalid: false };

    /// <summary>The index of the currently active virtual display, or -1.</summary>
    public int DisplayIndex => _displayIndex;

    /// <summary>
    /// Initializes the virtual display:
    /// 1. Opens device handle to the Parsec VDD driver
    /// 2. Adds a virtual display
    /// 3. Starts the 100ms keep-alive ping timer
    /// </summary>
    /// <returns>True if the display was created successfully.</returns>
    public bool Start()
    {
        // Check driver status first.
        var status = ParsecVdd.QueryDriverStatus();
        if (status != ParsecVdd.DeviceStatus.Ok)
        {
            Console.Error.WriteLine($"[VDD] {ParsecVdd.GetStatusMessage(status)}");
            return false;
        }

        Console.WriteLine("[VDD] Driver status: OK");

        // Open device handle.
        _deviceHandle = ParsecVdd.OpenDeviceHandle();
        if (_deviceHandle == null || _deviceHandle.IsInvalid)
        {
            Console.Error.WriteLine("[VDD] Failed to open device handle — are you running as administrator?");
            return false;
        }

        Console.WriteLine("[VDD] Device handle opened");

        // Add a virtual display.
        _displayIndex = ParsecVdd.AddDisplay(_deviceHandle);
        if (_displayIndex < 0)
        {
            Console.Error.WriteLine("[VDD] Failed to add virtual display");
            _deviceHandle.Dispose();
            _deviceHandle = null;
            return false;
        }

        Console.WriteLine($"[VDD] Virtual display added (index: {_displayIndex})");

        // Start the keep-alive ping timer.
        // The driver disconnects all displays if not pinged for ~1 second.
        // We ping every 100ms to stay well within the deadline.
        _pingTimer = new Timer(PingCallback, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        Console.WriteLine("[VDD] Keep-alive ping timer started (100ms interval)");

        return true;
    }

    /// <summary>
    /// Stops the virtual display:
    /// 1. Stops the ping timer
    /// 2. Removes the virtual display
    /// 3. Closes the device handle
    /// </summary>
    public void Stop()
    {
        if (_disposed) return;

        // Stop ping timer first.
        if (_pingTimer != null)
        {
            _pingTimer.Dispose();
            _pingTimer = null;
            Console.WriteLine("[VDD] Ping timer stopped");
        }

        // Remove the display.
        if (_displayIndex >= 0 && _deviceHandle is { IsInvalid: false })
        {
            ParsecVdd.RemoveDisplay(_deviceHandle, _displayIndex);
            Console.WriteLine($"[VDD] Virtual display removed (index: {_displayIndex})");
            _displayIndex = -1;
        }

        // Close the device handle.
        if (_deviceHandle != null)
        {
            _deviceHandle.Dispose();
            _deviceHandle = null;
            Console.WriteLine("[VDD] Device handle closed");
        }
    }

    private void PingCallback(object? state)
    {
        if (_disposed) return;

        try
        {
            if (_deviceHandle is { IsInvalid: false })
            {
                if (!ParsecVdd.Update(_deviceHandle))
                {
                    Console.Error.WriteLine("[VDD] WARNING: Ping failed — display may disconnect!");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VDD] Ping error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
