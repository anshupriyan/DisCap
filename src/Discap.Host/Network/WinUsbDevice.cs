using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Discap.Host.Network;

public class WinUsbDevice : IDisposable
{
    // GUID_DEVINTERFACE_USB_DEVICE - this matches ALL USB devices
    public static readonly Guid GUID_DEVINTERFACE_USB_DEVICE = new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    #region P/Invoke Definitions

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr SecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(SafeFileHandle DeviceHandle, out IntPtr InterfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Free(IntPtr InterfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ControlTransfer(IntPtr InterfaceHandle, WINUSB_SETUP_PACKET SetupPacket, byte[] Buffer, uint BufferLength, out uint LengthTransferred, IntPtr Overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryInterfaceSettings(IntPtr InterfaceHandle, byte AlternateInterfaceNumber, out USB_INTERFACE_DESCRIPTOR UsbAltInterfaceDescriptor);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryPipe(IntPtr InterfaceHandle, byte AlternateInterfaceNumber, byte PipeIndex, out WINUSB_PIPE_INFORMATION PipeInformation);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ReadPipe(IntPtr InterfaceHandle, byte PipeID, byte[] Buffer, uint BufferLength, out uint LengthTransferred, IntPtr Overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_WritePipe(IntPtr InterfaceHandle, byte PipeID, byte[] Buffer, uint BufferLength, out uint LengthTransferred, IntPtr Overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(IntPtr InterfaceHandle, byte PipeID, uint PolicyType, uint ValueLength, ref byte Value);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(IntPtr InterfaceHandle, byte PipeID, uint PolicyType, uint ValueLength, ref uint Value);

    public const uint POLICY_PIPE_TRANSFER_TIMEOUT = 0x03;
    public const uint POLICY_RAW_IO = 0x07;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WINUSB_SETUP_PACKET
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct USB_INTERFACE_DESCRIPTOR
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WINUSB_PIPE_INFORMATION
    {
        public USBD_PIPE_TYPE PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    public enum USBD_PIPE_TYPE : uint
    {
        UsbdPipeTypeControl,
        UsbdPipeTypeIsochronous,
        UsbdPipeTypeBulk,
        UsbdPipeTypeInterrupt
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    #endregion

    public string DevicePath { get; }
    public int Vid { get; }
    public int Pid { get; }
    public int? Mi { get; }
    
    private SafeFileHandle? _fileHandle;
    private IntPtr _winUsbHandle = IntPtr.Zero;

    private WinUsbDevice(string devicePath, int vid, int pid, int? mi)
    {
        DevicePath = devicePath;
        Vid = vid;
        Pid = pid;
        Mi = mi;
    }

    public static List<Guid> GetDeviceInterfaceGuids(int vid, int pid, int? mi = null)
    {
        var guids = new List<Guid>();
        try
        {
            using var usbKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usbKey == null) return guids;

            string searchStr = $"VID_{vid:X4}&PID_{pid:X4}";
            if (mi.HasValue)
            {
                searchStr += $"&MI_{mi.Value:X2}";
            }

            foreach (var devId in usbKey.GetSubKeyNames())
            {
                if (devId.IndexOf(searchStr, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    using var devKey = usbKey.OpenSubKey(devId);
                    if (devKey == null) continue;

                    foreach (var instanceId in devKey.GetSubKeyNames())
                    {
                        using var instanceKey = devKey.OpenSubKey(instanceId);
                        using var paramKey = instanceKey?.OpenSubKey("Device Parameters");
                        if (paramKey != null)
                        {
                            object? val = paramKey.GetValue("DeviceInterfaceGUIDs") ?? paramKey.GetValue("DeviceInterfaceGUID");
                            if (val is string[] arr)
                            {
                                foreach (var s in arr)
                                {
                                    if (Guid.TryParse(s, out Guid g)) guids.Add(g);
                                }
                            }
                            else if (val is string str)
                            {
                                if (Guid.TryParse(str, out Guid g)) guids.Add(g);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WinUSB] Failed to read registry for device GUIDs: {ex.Message}");
        }
        return guids;
    }

    public static List<WinUsbDevice> EnumerateDevices(Guid interfaceGuid)
    {
        var devices = new List<WinUsbDevice>();
        IntPtr infoSet = SetupDiGetClassDevs(ref interfaceGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        
        if (infoSet == IntPtr.Zero || infoSet == new IntPtr(-1))
        {
            Console.Error.WriteLine($"[WinUSB] SetupDiGetClassDevs failed with error {Marshal.GetLastWin32Error()}");
            return devices;
        }

        try
        {
            SP_DEVICE_INTERFACE_DATA interfaceData = new SP_DEVICE_INTERFACE_DATA();
            interfaceData.cbSize = (uint)Marshal.SizeOf(interfaceData);

            uint index = 0;
            while (SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref interfaceGuid, index, ref interfaceData))
            {
                uint requiredSize = 0;
                SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData, IntPtr.Zero, 0, out requiredSize, IntPtr.Zero);

                IntPtr detailData = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    // The cbSize field is dependent on the architecture
                    Marshal.WriteInt32(detailData, (IntPtr.Size == 8) ? 8 : 5); // 8 on 64-bit, 5 on 32-bit (rare)

                    if (SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData, detailData, requiredSize, out _, IntPtr.Zero))
                    {
                        // Extract the device path
                        IntPtr stringPtr = new IntPtr(detailData.ToInt64() + 4);
                        string path = Marshal.PtrToStringAuto(stringPtr) ?? string.Empty;

                        // Parse VID, PID and optionally MI from the path
                        var match = Regex.Match(path, @"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})(?:&mi_([0-9a-f]{2}))?", RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            int vid = Convert.ToInt32(match.Groups[1].Value, 16);
                            int pid = Convert.ToInt32(match.Groups[2].Value, 16);
                            int? mi = null;
                            if (match.Groups[3].Success)
                            {
                                mi = Convert.ToInt32(match.Groups[3].Value, 16);
                            }
                            devices.Add(new WinUsbDevice(path, vid, pid, mi));
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailData);
                }
                index++;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(infoSet);
        }

        return devices;
    }

    public bool Open()
    {
        _fileHandle = CreateFile(DevicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);

        if (_fileHandle.IsInvalid)
        {
            Console.WriteLine($"[WinUSB] Failed to open CreateFile handle for {DevicePath} - error {Marshal.GetLastWin32Error()}");
            return false;
        }

        Console.WriteLine($"[WinUSB] CreateFile succeeded. Attempting WinUsb_Initialize for {DevicePath}...");

        if (!WinUsb_Initialize(_fileHandle, out _winUsbHandle))
        {
            Console.WriteLine($"[WinUSB] WinUsb_Initialize failed for {DevicePath} - error {Marshal.GetLastWin32Error()}");
            _fileHandle.Close();
            return false;
        }

        Console.WriteLine($"[WinUSB] WinUsb_Initialize succeeded.");
        return true;
    }

    public bool ControlTransfer(WINUSB_SETUP_PACKET setupPacket, byte[]? buffer, out uint lengthTransferred)
    {
        lengthTransferred = 0;
        if (_winUsbHandle == IntPtr.Zero) return false;

        uint bufferLength = buffer != null ? (uint)buffer.Length : 0;
        return WinUsb_ControlTransfer(_winUsbHandle, setupPacket, buffer ?? Array.Empty<byte>(), bufferLength, out lengthTransferred, IntPtr.Zero);
    }

    public bool DiscoverPipes(out byte readPipe, out byte writePipe, out ushort writeMaxPacketSize)
    {
        readPipe = 0;
        writePipe = 0;
        writeMaxPacketSize = 512;

        if (_winUsbHandle == IntPtr.Zero) return false;

        if (!WinUsb_QueryInterfaceSettings(_winUsbHandle, 0, out USB_INTERFACE_DESCRIPTOR ifaceDescriptor))
        {
            Console.Error.WriteLine($"[WinUSB] WinUsb_QueryInterfaceSettings failed - error {Marshal.GetLastWin32Error()}");
            return false;
        }

        for (byte i = 0; i < ifaceDescriptor.bNumEndpoints; i++)
        {
            if (WinUsb_QueryPipe(_winUsbHandle, 0, i, out WINUSB_PIPE_INFORMATION pipeInfo))
            {
                if (pipeInfo.PipeType == USBD_PIPE_TYPE.UsbdPipeTypeBulk)
                {
                    bool isRead = (pipeInfo.PipeId & 0x80) != 0;
                    if (isRead)
                    {
                        readPipe = pipeInfo.PipeId;
                        Console.WriteLine($"[WinUSB] Found bulk IN pipe: 0x{readPipe:X2} (MaxPacketSize: {pipeInfo.MaximumPacketSize})");
                    }
                    else
                    {
                        writePipe = pipeInfo.PipeId;
                        writeMaxPacketSize = 512; // HARDCODED for testing
                        Console.WriteLine($"[WinUSB] Found bulk OUT pipe: 0x{writePipe:X2} (MaxPacketSize: 512 (Hardcoded) from {pipeInfo.MaximumPacketSize})");

                        // Apply Optimizations to OUT pipe
                        byte rawIo = 1;
                        if (!WinUsb_SetPipePolicy(_winUsbHandle, writePipe, POLICY_RAW_IO, 1, ref rawIo))
                        {
                            Console.WriteLine($"[WinUSB] Warning: Failed to enable RAW_IO on OUT pipe - error {Marshal.GetLastWin32Error()}");
                        }

                        uint timeoutMs = 5000;
                        if (!WinUsb_SetPipePolicy(_winUsbHandle, writePipe, POLICY_PIPE_TRANSFER_TIMEOUT, 4, ref timeoutMs))
                        {
                            Console.WriteLine($"[WinUSB] Warning: Failed to set TRANSFER_TIMEOUT on OUT pipe - error {Marshal.GetLastWin32Error()}");
                        }
                    }
                }
            }
        }

        return readPipe != 0 && writePipe != 0;
    }

    public bool WritePipe(byte pipeId, byte[] buffer, int offset, int length, out uint transferred)
    {
        transferred = 0;
        if (_winUsbHandle == IntPtr.Zero) return false;

        byte[] slice = buffer;
        if (offset != 0 || length != buffer.Length)
        {
            slice = new byte[length];
            Buffer.BlockCopy(buffer, offset, slice, 0, length);
        }

        return WinUsb_WritePipe(_winUsbHandle, pipeId, slice, (uint)length, out transferred, IntPtr.Zero);
    }

    public bool ReadPipe(byte pipeId, byte[] buffer, int offset, int length, out uint transferred)
    {
        transferred = 0;
        if (_winUsbHandle == IntPtr.Zero) return false;

        byte[] slice = new byte[length];
        bool success = WinUsb_ReadPipe(_winUsbHandle, pipeId, slice, (uint)length, out transferred, IntPtr.Zero);
        
        if (success && transferred > 0)
        {
            Buffer.BlockCopy(slice, 0, buffer, offset, (int)transferred);
        }

        return success;
    }

    public void Dispose()
    {
        if (_winUsbHandle != IntPtr.Zero)
        {
            WinUsb_Free(_winUsbHandle);
            _winUsbHandle = IntPtr.Zero;
        }
        
        _fileHandle?.Dispose();
    }
}
