using System;
using System.IO;
using System.Text;
using System.Threading;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using LibUsbDotNet.DeviceNotify;

namespace Discap.Host.Network;

public sealed class UsbTransport : IDisposable
{
    private UsbDevice? _usbDevice;
    private UsbEndpointWriter? _writer;
    private UsbEndpointReader? _reader;
    private bool _connected;
    private const int GoogleVendorId = 0x18D1;
    private const int AccessoryPid1 = 0x2D00;
    private const int AccessoryPid2 = 0x2D01;

    /// <summary>Hard cap on total AOA attempt time so fallback to ADB isn't delayed too long.</summary>
    private const int MaxAoaAttemptMs = 10_000;

    public bool IsConnected => _connected && _usbDevice != null && _usbDevice.IsOpen;

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
        var accessoryDevice = FindAccessoryDevice();
        if (accessoryDevice != null)
        {
            Console.WriteLine($"[USB] Found device already in accessory mode: VID=0x{accessoryDevice.Vid:X4} PID=0x{accessoryDevice.Pid:X4}");
        }

        if (accessoryDevice == null)
        {
            // Step 2: Try to find ANY Android device and put it into accessory mode
            Console.WriteLine("[USB] Step 2: Scanning for Android devices to switch to accessory mode...");
            var allDevices = UsbDevice.AllDevices;
            Console.WriteLine($"[USB] Found {allDevices.Count} USB device(s) total");
            bool sentAoaStart = false;

            foreach (UsbRegistry registry in allDevices)
            {
                Console.WriteLine($"[USB]   Device: VID=0x{registry.Vid:X4} PID=0x{registry.Pid:X4}");

                if (registry.Vid == GoogleVendorId && (registry.Pid == AccessoryPid1 || registry.Pid == AccessoryPid2))
                {
                    Console.WriteLine("[USB]   Skipping — already an accessory PID");
                    continue;
                }

                Console.WriteLine("[USB]   Attempting to open device...");
                if (registry.Open(out UsbDevice tempDevice))
                {
                    Console.WriteLine("[USB]   Device opened successfully");
                    try
                    {
                        if (TryStartAccessoryMode(tempDevice))
                        {
                            Console.WriteLine("[USB] Sent AOA start command. Waiting for reconnection...");
                            sentAoaStart = true;
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
                        if (tempDevice.IsOpen) tempDevice.Close();
                    }
                }
                else
                {
                    Console.Error.WriteLine("[USB]   Failed to open device (permissions? driver?)");
                }
            }

            if (!sentAoaStart)
            {
                Console.Error.WriteLine("[USB] No device accepted AOA handshake — giving up");
                return false;
            }

            // Step 3: Wait for the device to reappear as an accessory.
            // The Android device disconnects from USB and reconnects as a new
            // device in accessory mode — this takes at least a few seconds.
            int initialDelayMs = isAoapMode ? 5000 : 3000;
            Console.WriteLine($"[USB] Step 3: Waiting {initialDelayMs / 1000}s for device to re-enumerate in accessory mode...");
            Thread.Sleep(initialDelayMs); // Initial delay — device needs time to reboot into accessory mode

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
        Console.WriteLine("[USB] Step 4: Opening accessory device and claiming interface...");
        if (accessoryDevice.Open(out _usbDevice))
        {
            Console.WriteLine("[USB] Accessory device opened successfully");

            IUsbDevice wholeUsbDevice = _usbDevice as IUsbDevice;
            if (!ReferenceEquals(wholeUsbDevice, null))
            {
                Console.WriteLine("[USB] Setting configuration 1...");
                bool configOk = wholeUsbDevice.SetConfiguration(1);
                Console.WriteLine($"[USB] SetConfiguration(1): {(configOk ? "OK" : "FAILED")}");

                Console.WriteLine("[USB] Claiming interface 0...");
                bool claimOk = wholeUsbDevice.ClaimInterface(0);
                Console.WriteLine($"[USB] ClaimInterface(0): {(claimOk ? "OK" : "FAILED")}");

                if (!configOk || !claimOk)
                {
                    Console.Error.WriteLine("[USB] Failed to configure accessory device — may need WinUSB driver (install via Zadig)");
                }
            }

            Console.WriteLine("[USB] Opening endpoints (OUT=Ep01, IN=Ep01)...");
            _writer = _usbDevice.OpenEndpointWriter(WriteEndpointID.Ep01); // Standard AOA OUT
            _reader = _usbDevice.OpenEndpointReader(ReadEndpointID.Ep01);  // Standard AOA IN
            Console.WriteLine($"[USB] Writer endpoint: {(_writer != null ? "OK" : "NULL")}");
            Console.WriteLine($"[USB] Reader endpoint: {(_reader != null ? "OK" : "NULL")}");

            _connected = true;
            long totalMs = Environment.TickCount64 - start;
            Console.WriteLine($"[USB] AOA connection established in {totalMs}ms");
            return true;
        }

        Console.Error.WriteLine("[USB] Failed to open accessory device");
        return false;
    }

    private UsbRegistry? FindAccessoryDevice()
    {
        foreach (UsbRegistry registry in UsbDevice.AllDevices)
        {
            if (registry.Vid == GoogleVendorId && (registry.Pid == AccessoryPid1 || registry.Pid == AccessoryPid2))
            {
                return registry;
            }
        }
        return null;
    }

    private bool TryStartAccessoryMode(UsbDevice device)
    {
        // 51: Get Protocol — check if the device supports AOA
        Console.WriteLine("[USB]   Sending AOA GetProtocol (request=51)...");
        UsbSetupPacket getProtocol = new UsbSetupPacket(
            (byte)(UsbCtrlFlags.Direction_In | UsbCtrlFlags.RequestType_Vendor | UsbCtrlFlags.Recipient_Device),
            51, 0, 0, 2);
            
        byte[] protocolBuffer = new byte[2];
        int transferred;
        bool result = device.ControlTransfer(ref getProtocol, protocolBuffer, 2, out transferred);
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
        UsbSetupPacket startRequest = new UsbSetupPacket(
            (byte)(UsbCtrlFlags.Direction_Out | UsbCtrlFlags.RequestType_Vendor | UsbCtrlFlags.Recipient_Device),
            53, 0, 0, 0);
            
        result = device.ControlTransfer(ref startRequest, null, 0, out transferred);
        Console.WriteLine($"[USB]   AOA Start result={result}, transferred={transferred}");

        if (!result)
        {
            Console.Error.WriteLine("[USB]   AOA Start command failed");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Sends a single AOA identification string to the device.
    /// Returns true on success, false on failure.
    /// </summary>
    private bool SendAoaString(UsbDevice device, short index, string str)
    {
        byte[] data = Encoding.UTF8.GetBytes(str + "\0");
        UsbSetupPacket request = new UsbSetupPacket(
            (byte)(UsbCtrlFlags.Direction_Out | UsbCtrlFlags.RequestType_Vendor | UsbCtrlFlags.Recipient_Device),
            52, 0, index, (short)data.Length);

        bool result = device.ControlTransfer(ref request, data, data.Length, out int transferred);
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
        if (_writer == null) throw new InvalidOperationException("Not connected");
        
        // Ensure we send exactly what was requested. In Bulk transfers, large buffers are broken down automatically by the OS drivers.
        // However, LibUsbDotNet might need smaller chunks if it exceeds MaxTransferSize.
        int sent;
        if (offset == 0 && count == buffer.Length)
        {
            _writer.Write(buffer, 2000, out sent);
        }
        else
        {
            byte[] slice = new byte[count];
            Buffer.BlockCopy(buffer, offset, slice, 0, count);
            _writer.Write(slice, 2000, out sent);
        }
        
        if (sent != count)
        {
            throw new IOException($"USB write failed. Sent {sent} of {count} bytes.");
        }
    }

    public void Dispose()
    {
        _connected = false;
        
        if (_usbDevice != null)
        {
            if (_usbDevice.IsOpen)
            {
                IUsbDevice wholeUsbDevice = _usbDevice as IUsbDevice;
                if (!ReferenceEquals(wholeUsbDevice, null))
                {
                    wholeUsbDevice.ReleaseInterface(0);
                }
                _usbDevice.Close();
            }
            _usbDevice = null;
        }
        _writer?.Dispose();
        _reader?.Dispose();
        _memoryStream?.Dispose();
    }

    // Wrap the USB bulk write in a standard System.IO.Stream interface to avoid changing PacketWriter
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

