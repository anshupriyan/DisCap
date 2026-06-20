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

    public bool IsConnected => _connected && _usbDevice != null && _usbDevice.IsOpen;

    private readonly Stream _memoryStream;
    public Stream Stream => _memoryStream; // Wrap the endpoint writes in a Stream

    public UsbTransport()
    {
        _memoryStream = new UsbBulkStream(this);
    }

    public bool TryConnect(int timeoutMs)
    {
        Console.WriteLine("[USB] Attempting AOA negotiation...");
        long start = Environment.TickCount64;

        // Step 1: Find if a device is ALREADY in accessory mode
        var accessoryDevice = FindAccessoryDevice();
        if (accessoryDevice == null)
        {
            // Step 2: Try to find ANY Android device and put it into accessory mode
            var allDevices = UsbDevice.AllDevices;
            foreach (UsbRegistry registry in allDevices)
            {
                if (registry.Vid == GoogleVendorId && (registry.Pid == AccessoryPid1 || registry.Pid == AccessoryPid2)) continue;

                if (registry.Open(out UsbDevice tempDevice))
                {
                    try
                    {
                        if (TryStartAccessoryMode(tempDevice))
                        {
                            Console.WriteLine("[USB] Sent AOA start command. Waiting for reconnection...");
                            break; // Wait for it to reconnect
                        }
                    }
                    finally
                    {
                        if (tempDevice.IsOpen) tempDevice.Close();
                    }
                }
            }

            // Step 3: Wait for the device to reappear as an accessory
            while (Environment.TickCount64 - start < timeoutMs)
            {
                accessoryDevice = FindAccessoryDevice();
                if (accessoryDevice != null) break;
                Thread.Sleep(100);
            }
        }

        if (accessoryDevice == null)
        {
            return false;
        }

        // Step 4: Open the accessory device and endpoints
        if (accessoryDevice.Open(out _usbDevice))
        {
            IUsbDevice wholeUsbDevice = _usbDevice as IUsbDevice;
            if (!ReferenceEquals(wholeUsbDevice, null))
            {
                wholeUsbDevice.SetConfiguration(1);
                wholeUsbDevice.ClaimInterface(0);
            }

            _writer = _usbDevice.OpenEndpointWriter(WriteEndpointID.Ep01); // Standard AOA OUT
            _reader = _usbDevice.OpenEndpointReader(ReadEndpointID.Ep01);  // Standard AOA IN
            _connected = true;
            return true;
        }

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
        // 51: Get Protocol
        UsbSetupPacket getProtocol = new UsbSetupPacket(
            (byte)(UsbCtrlFlags.Direction_In | UsbCtrlFlags.RequestType_Vendor | UsbCtrlFlags.Recipient_Device),
            51, 0, 0, 2);
            
        byte[] protocolBuffer = new byte[2];
        int transferred;
        if (!device.ControlTransfer(ref getProtocol, protocolBuffer, 2, out transferred) || transferred != 2)
            return false;

        ushort protocol = BitConverter.ToUInt16(protocolBuffer, 0);
        if (protocol < 1) return false;

        // 52: Send Strings
        SendString(device, 0, "Discap"); // Manufacturer
        SendString(device, 1, "DiscapDisplay"); // Model
        SendString(device, 2, "Virtual Display Streamer"); // Description
        SendString(device, 3, "1.0"); // Version
        SendString(device, 4, "https://github.com/discap"); // URI
        SendString(device, 5, "0000000012345678"); // Serial

        // 53: Start Accessory
        UsbSetupPacket startRequest = new UsbSetupPacket(
            (byte)(UsbCtrlFlags.Direction_Out | UsbCtrlFlags.RequestType_Vendor | UsbCtrlFlags.Recipient_Device),
            53, 0, 0, 0);
            
        return device.ControlTransfer(ref startRequest, null, 0, out transferred);
    }

    private void SendString(UsbDevice device, short index, string str)
    {
        byte[] data = Encoding.UTF8.GetBytes(str + "\0");
        UsbSetupPacket request = new UsbSetupPacket(
            (byte)(UsbCtrlFlags.Direction_Out | UsbCtrlFlags.RequestType_Vendor | UsbCtrlFlags.Recipient_Device),
            52, 0, index, (short)data.Length);
            
        device.ControlTransfer(ref request, data, data.Length, out int _);
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
