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
    
    private SafeFileHandle? _fileHandle;
    private IntPtr _winUsbHandle = IntPtr.Zero;

    private WinUsbDevice(string devicePath, int vid, int pid)
    {
        DevicePath = devicePath;
        Vid = vid;
        Pid = pid;
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

                        // Parse VID and PID from the path (e.g. \\?\usb#vid_17ef&pid_201c#...)
                        var match = Regex.Match(path, @"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            int vid = Convert.ToInt32(match.Groups[1].Value, 16);
                            int pid = Convert.ToInt32(match.Groups[2].Value, 16);
                            devices.Add(new WinUsbDevice(path, vid, pid));
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

        if (!WinUsb_Initialize(_fileHandle, out _winUsbHandle))
        {
            Console.WriteLine($"[WinUSB] WinUsb_Initialize failed for {DevicePath} - error {Marshal.GetLastWin32Error()}");
            _fileHandle.Close();
            return false;
        }

        return true;
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
