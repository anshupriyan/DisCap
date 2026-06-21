using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Collections.Generic;

namespace Discap.Host.Network;

public sealed class UsbTransport : IDisposable
{
    private WinUsbDevice? _accessoryDevice;
    private bool _connected;
    private byte _readPipe;
    private byte _writePipe;
    private ushort _writeMaxPacketSize;
    
    private const int GoogleVendorId = 0x18D1;
    private const int AccessoryPid1 = 0x2D00;
    private const int AccessoryPid2 = 0x2D01;

    /// <summary>Hard cap on total AOA attempt time so fallback to ADB isn't delayed too long.</summary>
    private const int MaxAoaAttemptMs = 10_000;

    public bool IsConnected => _connected && _accessoryDevice != null;

    private readonly Stream _memoryStream;
    public Stream Stream => _memoryStream; // Wrap the endpoint writes in a Stream

    public UsbTransport()
    {
        _memoryStream = new UsbBulkStream(this);
    }

    public bool TryConnect(int timeoutMs, bool isAoapMode = false)
    {
        // In ADB fallback mode, cap the timeout. In AOAP mode, use the full timeout.
        if (!isAoapMode)
            timeoutMs = Math.Min(timeoutMs, MaxAoaAttemptMs);
        
        string modeLabel = isAoapMode ? "AOAP" : "AOA-fallback";
        Console.WriteLine($"[USB] Attempting {modeLabel} negotiation (timeout={timeoutMs}ms)...");
        long start = Environment.TickCount64;

        // Step 1: Find if a device is ALREADY in accessory mode
        Console.WriteLine("[USB] Step 1: Checking for existing accessory device...");
        WinUsbDevice? accessoryDevice = FindAccessoryDevice();
        if (accessoryDevice != null)
        {
            Console.WriteLine($"[USB] Found device already in accessory mode: VID=0x{accessoryDevice.Vid:X4} PID=0x{accessoryDevice.Pid:X4}");
        }

        if (accessoryDevice == null)
        {
            // Step 2: Try to find ANY Android device and put it into accessory mode
            Console.WriteLine("[USB] Step 2: Scanning for Android devices to switch to accessory mode...");
            
            var usbGuid = WinUsbDevice.GUID_DEVINTERFACE_USB_DEVICE;
            Console.WriteLine($"[USB] Enumerating with GUID: {usbGuid}");
            var allDevices = WinUsbDevice.EnumerateDevices(usbGuid);
            Console.WriteLine($"[USB] Found {allDevices.Count} raw WinUSB device(s) total");
            
            bool sentAoaStart = false;

            foreach (var device in allDevices)
            {
                Console.WriteLine($"[USB]   Device: VID=0x{device.Vid:X4} PID=0x{device.Pid:X4} Path={device.DevicePath}");

                if (device.Vid == GoogleVendorId && (device.Pid == AccessoryPid1 || device.Pid == AccessoryPid2))
                {
                    Console.WriteLine("[USB]   Skipping — already an accessory PID");
                    device.Dispose();
                    continue;
                }

                Console.WriteLine("[USB]   Attempting to open device handle and WinUSB_Initialize...");
                if (device.Open())
                {
                    Console.WriteLine("[USB]   Device handle and WinUSB context successfully opened!");
                    try
                    {
                        if (TryStartAccessoryMode(device))
                        {
                            Console.WriteLine("[USB] Sent AOA start command. Waiting for reconnection...");
                            sentAoaStart = true;
                            device.Dispose();
                            break; // Wait for it to reconnect
                        }
                        else
                        {
                            Console.WriteLine("[USB]   TryStartAccessoryMode returned false");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[USB]   TryStartAccessoryMode threw exception: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        device.Dispose();
                    }
                }
                else
                {
                    Console.Error.WriteLine("[USB]   Failed to open device (expected for non-WinUSB devices like mice/keyboards)");
                    device.Dispose();
                }
            }

            if (!sentAoaStart)
            {
                Console.Error.WriteLine("[USB] No device accepted AOA handshake — giving up");
                return false;
            }

            // Step 3: Wait for the device to reappear as an accessory.
            int initialDelayMs = isAoapMode ? 5000 : 3000;
            Console.WriteLine($"[USB] Step 3: Waiting {initialDelayMs / 1000}s for device to re-enumerate in accessory mode...");
            Thread.Sleep(initialDelayMs);

            Console.WriteLine("[USB] Polling for accessory device (500ms interval)...");
            while (Environment.TickCount64 - start < timeoutMs)
            {
                accessoryDevice = FindAccessoryDevice();
                if (accessoryDevice != null)
                {
                    Console.WriteLine($"[USB] Accessory device appeared: VID=0x{accessoryDevice.Vid:X4} PID=0x{accessoryDevice.Pid:X4}");
                    break;
                }
                Thread.Sleep(500);
            }

            if (accessoryDevice == null)
            {
                long elapsedMs = Environment.TickCount64 - start;
                Console.Error.WriteLine($"[USB] Accessory device did not appear after {elapsedMs}ms — timeout");
                return false;
            }
        }

        // Step 4: Open the accessory device and endpoints
        Console.WriteLine("[USB] Step 4: Opening accessory device and discovering pipes...");
        if (accessoryDevice.Open())
        {
            Console.WriteLine("[USB] Accessory device opened successfully");
            _accessoryDevice = accessoryDevice;

            Console.WriteLine("[USB] Discovering endpoints...");
            if (_accessoryDevice.DiscoverPipes(out _readPipe, out _writePipe, out _writeMaxPacketSize))
            {
                Console.WriteLine($"[USB] Endpoints discovered: OUT=0x{_writePipe:X2}, IN=0x{_readPipe:X2}, MaxPacketSize={_writeMaxPacketSize}");
                _connected = true;
                long totalMs = Environment.TickCount64 - start;
                Console.WriteLine($"[USB] AOA connection established in {totalMs}ms");
                return true;
            }
            else
            {
                Console.Error.WriteLine("[USB] Failed to discover bulk pipes on the accessory device.");
            }
        }

        Console.Error.WriteLine("[USB] Failed to open accessory device");
        accessoryDevice?.Dispose();
        return false;
    }

    private WinUsbDevice? FindAccessoryDevice()
    {
        var guidsToTry = new HashSet<Guid> { WinUsbDevice.GUID_DEVINTERFACE_USB_DEVICE };
        
        // Find any custom GUIDs registered specifically for the AOA MI_00 interfaces
        var customGuids1 = WinUsbDevice.GetDeviceInterfaceGuids(GoogleVendorId, AccessoryPid1, 0);
        var customGuids2 = WinUsbDevice.GetDeviceInterfaceGuids(GoogleVendorId, AccessoryPid2, 0);
        foreach (var g in customGuids1) guidsToTry.Add(g);
        foreach (var g in customGuids2) guidsToTry.Add(g);

        var devices = new List<WinUsbDevice>();
        foreach (var guid in guidsToTry)
        {
            devices.AddRange(WinUsbDevice.EnumerateDevices(guid));
        }

        WinUsbDevice? bestMatch = null;
        var loggedPaths = new HashSet<string>();

        foreach (var device in devices)
        {
            if (loggedPaths.Add(device.DevicePath))
            {
                Console.WriteLine($"[USB]   Poll Device: VID=0x{device.Vid:X4} PID=0x{device.Pid:X4} MI={(device.Mi.HasValue ? device.Mi.Value.ToString("X2") : "none")} Path={device.DevicePath}");
            }

            if (device.Vid == GoogleVendorId && (device.Pid == AccessoryPid1 || device.Pid == AccessoryPid2))
            {
                Console.WriteLine($"[USB]   Found AOA candidate: VID=0x{device.Vid:X4} PID=0x{device.Pid:X4} MI={(device.Mi.HasValue ? device.Mi.Value.ToString("X2") : "none")} Path={device.DevicePath}");
                
                if (device.Mi == 0)
                {
                    bestMatch = device;
                }
                else if (!device.Mi.HasValue && bestMatch == null)
                {
                    bestMatch = device;
                }
            }
        }

        foreach (var device in devices)
        {
            if (device != bestMatch)
            {
                device.Dispose();
            }
        }
        return bestMatch;
    }

    private bool TryStartAccessoryMode(WinUsbDevice device)
    {
        // 51: Get Protocol — check if the device supports AOA
        Console.WriteLine("[USB]   Sending AOA GetProtocol (request=51)...");
        // Direction_In (0x80) | RequestType_Vendor (0x40) | Recipient_Device (0x00) = 0xC0
        var getProtocol = new WinUsbDevice.WINUSB_SETUP_PACKET
        {
            RequestType = 0xC0,
            Request = 51,
            Value = 0,
            Index = 0,
            Length = 2
        };
            
        byte[] protocolBuffer = new byte[2];
        bool result = device.ControlTransfer(getProtocol, protocolBuffer, out uint transferred);
        Console.WriteLine($"[USB]   GetProtocol result={result}, transferred={transferred} bytes");

        if (!result || transferred != 2)
        {
            Console.Error.WriteLine($"[USB]   GetProtocol failed — device may not support AOA (result={result}, transferred={transferred})");
            return false;
        }

        ushort protocol = BitConverter.ToUInt16(protocolBuffer, 0);
        Console.WriteLine($"[USB]   AOA protocol version: {protocol}");
        if (protocol < 1)
        {
            Console.Error.WriteLine("[USB]   Protocol version < 1 — device does not support AOA");
            return false;
        }

        // 52: Send Strings — these must match the Android app's accessory_filter.xml
        Console.WriteLine("[USB]   Sending AOA identification strings (request=52)...");
        bool stringsOk = true;
        stringsOk &= SendAoaString(device, 0, "Discap");                     // Manufacturer
        stringsOk &= SendAoaString(device, 1, "DiscapDisplay");              // Model
        stringsOk &= SendAoaString(device, 2, "Virtual Display Streamer");   // Description
        stringsOk &= SendAoaString(device, 3, "1.0");                        // Version
        stringsOk &= SendAoaString(device, 4, "https://github.com/discap");  // URI
        stringsOk &= SendAoaString(device, 5, "0000000012345678");           // Serial

        if (!stringsOk)
        {
            Console.Error.WriteLine("[USB]   One or more AOA strings failed to send");
            return false;
        }
        Console.WriteLine("[USB]   All AOA strings sent successfully");

        // 53: Start Accessory — tells the device to reboot into accessory mode
        Console.WriteLine("[USB]   Sending AOA Start (request=53)...");
        // Direction_Out (0x00) | RequestType_Vendor (0x40) | Recipient_Device (0x00) = 0x40
        var startRequest = new WinUsbDevice.WINUSB_SETUP_PACKET
        {
            RequestType = 0x40,
            Request = 53,
            Value = 0,
            Index = 0,
            Length = 0
        };
            
        result = device.ControlTransfer(startRequest, null, out transferred);
        Console.WriteLine($"[USB]   AOA Start result={result}, transferred={transferred}");

        if (!result)
        {
            Console.Error.WriteLine("[USB]   AOA Start command failed");
            return false;
        }

        return true;
    }

    private bool SendAoaString(WinUsbDevice device, ushort index, string str)
    {
        byte[] data = Encoding.UTF8.GetBytes(str + "\0");
        var request = new WinUsbDevice.WINUSB_SETUP_PACKET
        {
            RequestType = 0x40,
            Request = 52,
            Value = 0,
            Index = index,
            Length = (ushort)data.Length
        };

        bool result = device.ControlTransfer(request, data, out uint transferred);
        string status = result && transferred == data.Length ? "OK" : "FAILED";
        Console.WriteLine($"[USB]     String[{index}] \"{str}\": {status} (sent={transferred}/{data.Length})");

        if (!result)
        {
            Console.Error.WriteLine($"[USB]     ControlTransfer failed for string index {index}");
        }
        return result;
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        if (_accessoryDevice == null || !_connected) throw new InvalidOperationException("Not connected");
        
        // Chunk writes to a multiple of MaximumPacketSize. Using 32KB chunks as a good balance for bulk.
        int chunkSize = Math.Max((int)_writeMaxPacketSize, 32768);
        // Ensure it's exactly a multiple of _writeMaxPacketSize
        chunkSize = (chunkSize / _writeMaxPacketSize) * _writeMaxPacketSize;
        
        int bytesWritten = 0;
        while (bytesWritten < count)
        {
            int currentChunk = Math.Min(chunkSize, count - bytesWritten);
            long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            bool result = _accessoryDevice.WritePipe(_writePipe, buffer, offset + bytesWritten, currentChunk, out uint sent);
            
            long endTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            double ms = (endTicks - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

            Console.WriteLine($"[WinUSB] Chunk write: {sent} bytes in {ms:F2}ms");

            if (!result || sent != currentChunk)
            {
                throw new IOException($"USB write failed at offset {bytesWritten}. Sent {sent} of {currentChunk} bytes.");
            }

            bytesWritten += (int)sent;
        }
    }

    public void Dispose()
    {
        _connected = false;
        _accessoryDevice?.Dispose();
        _accessoryDevice = null;
        _memoryStream?.Dispose();
    }

    private class UsbBulkStream : Stream
    {
        private readonly UsbTransport _transport;
        public UsbBulkStream(UsbTransport transport) => _transport = transport;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        
        public override void Write(byte[] buffer, int offset, int count)
        {
            _transport.Write(buffer, offset, count);
        }
    }
}
