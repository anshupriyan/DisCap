using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Discap.Host.Display;

/// <summary>
/// P/Invoke bindings for the Parsec Virtual Display Driver (parsec-vdd).
/// Ported from the C header: https://github.com/nomi-san/parsec-vdd/blob/main/core/parsec-vdd.h
///
/// The driver communicates via IOCTL codes sent through a device handle.
/// The handle is obtained via SetupAPI using the driver's interface GUID.
/// </summary>
public static class ParsecVdd
{
    // ─── Driver identification ──────────────────────────────────────────

    /// <summary>Device interface GUID for the Parsec VDD driver.</summary>
    public static readonly Guid INTERFACE_GUID = new("00b41627-04c4-429e-a26e-0265cf50c8fa");

    /// <summary>Device class GUID (Display adapters).</summary>
    public static readonly Guid CLASS_GUID = new("4d36e968-e325-11ce-bfc1-08002be10318");

    /// <summary>Hardware ID for the Parsec VDA device.</summary>
    public const string HARDWARE_ID = "Root\\Parsec\\VDA";

    /// <summary>Maximum number of virtual displays per adapter.</summary>
    public const int MAX_DISPLAYS = 16;

    // ─── IOCTL codes ────────────────────────────────────────────────────
    // These are computed from the device type, function codes, and access flags.
    // FILE_DEVICE_BUS_EXTENDER = 0x2A, METHOD_BUFFERED = 0, FILE_ANY_ACCESS = 0

    private const uint IOCTL_ADD = 0x0022e004;
    private const uint IOCTL_REMOVE = 0x0022a008;
    private const uint IOCTL_UPDATE = 0x0022a00c;

    // ─── Device status ──────────────────────────────────────────────────

    public enum DeviceStatus
    {
        Ok = 0,
        Inaccessible,
        Unknown,
        UnknownProblem,
        Disabled,
        DriverError,
        RestartRequired,
        DisabledService,
        NotInstalled
    }

    // ─── Win32 constants ────────────────────────────────────────────────

    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_DEVICEINTERFACE = 0x10;
    private const int SPDRP_HARDWAREID = 0x01;
    private const int CR_SUCCESS = 0;
    private const int DN_HAS_PROBLEM = 0x00000400;
    private const int DN_DISABLEABLE = 0x00002000;
    private const int CM_PROB_DISABLED = 0x16;
    private const int CM_PROB_HARDWARE_DISABLED = 0x1D;
    private const int CM_PROB_DISABLED_SERVICE = 0x20;
    private const int CM_PROB_NEED_RESTART = 0x0E;

    // ─── Win32 structs ──────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SP_DEVICE_INTERFACE_DETAIL_DATA
    {
        public uint cbSize;
        // The first character of the device path string.
        // The actual path extends beyond this struct.
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DevicePath;
    }

    // ─── SetupAPI imports ───────────────────────────────────────────────

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint property, out uint propertyRegDataType,
        byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        ref SP_DEVICE_INTERFACE_DETAIL_DATA deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    // ─── CfgMgr32 imports ───────────────────────────────────────────────

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(
        out uint status, out uint problemNumber, uint devInst, uint flags);

    // ─── Kernel32 imports ───────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint ioControlCode,
        byte[]? inBuffer, uint inBufferSize,
        byte[]? outBuffer, uint outBufferSize,
        out uint bytesReturned, IntPtr overlapped);

    // ─── Public API ─────────────────────────────────────────────────────

    /// <summary>
    /// Queries the current status of the Parsec VDD driver.
    /// </summary>
    public static DeviceStatus QueryDriverStatus()
    {
        var classGuid = CLASS_GUID;
        var status = DeviceStatus.Inaccessible;

        var devInfo = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, DIGCF_PRESENT);
        if (devInfo == new IntPtr(-1))
            return DeviceStatus.NotInstalled;

        try
        {
            var devInfoData = new SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
            };

            uint deviceIndex = 0;
            while (SetupDiEnumDeviceInfo(devInfo, deviceIndex++, ref devInfoData))
            {
                // Get required buffer size for hardware ID.
                SetupDiGetDeviceRegistryProperty(
                    devInfo, ref devInfoData, SPDRP_HARDWAREID,
                    out _, null, 0, out uint requiredSize);

                if (requiredSize == 0) continue;

                var buffer = new byte[requiredSize];
                if (!SetupDiGetDeviceRegistryProperty(
                    devInfo, ref devInfoData, SPDRP_HARDWAREID,
                    out _, buffer, requiredSize, out _))
                    continue;

                // Check if this device matches our hardware ID.
                var hardwareId = System.Text.Encoding.Unicode
                    .GetString(buffer)
                    .TrimEnd('\0');

                if (!hardwareId.Contains(HARDWARE_ID, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Found our device — check its status via CfgMgr32.
                if (CM_Get_DevNode_Status(out uint devStatus, out uint problemNumber,
                    devInfoData.DevInst, 0) != CR_SUCCESS)
                {
                    status = DeviceStatus.Unknown;
                    break;
                }

                if ((devStatus & DN_HAS_PROBLEM) != 0)
                {
                    status = problemNumber switch
                    {
                        CM_PROB_DISABLED => DeviceStatus.Disabled,
                        CM_PROB_HARDWARE_DISABLED => DeviceStatus.Disabled,
                        CM_PROB_DISABLED_SERVICE => DeviceStatus.DisabledService,
                        CM_PROB_NEED_RESTART => DeviceStatus.RestartRequired,
                        _ => DeviceStatus.DriverError
                    };
                }
                else
                {
                    status = DeviceStatus.Ok;
                }
                break;
            }

            // If we never found the device at all.
            if (deviceIndex == 1 && status == DeviceStatus.Inaccessible)
                status = DeviceStatus.NotInstalled;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }

        return status;
    }

    /// <summary>
    /// Opens a device handle to the Parsec VDD interface.
    /// Returns null if the device cannot be opened.
    /// Must be called with administrator privileges.
    /// </summary>
    public static SafeFileHandle? OpenDeviceHandle()
    {
        var interfaceGuid = INTERFACE_GUID;

        var devInfo = SetupDiGetClassDevs(
            ref interfaceGuid, null, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

        if (devInfo == new IntPtr(-1))
            return null;

        try
        {
            var interfaceData = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
            };

            if (!SetupDiEnumDeviceInterfaces(
                devInfo, IntPtr.Zero, ref interfaceGuid, 0, ref interfaceData))
                return null;

            // Get device path.
            var detailData = new SP_DEVICE_INTERFACE_DETAIL_DATA
            {
                cbSize = IntPtr.Size == 8 ? 8u : (4u + (uint)Marshal.SystemDefaultCharSize)
            };

            if (!SetupDiGetDeviceInterfaceDetail(
                devInfo, ref interfaceData, ref detailData,
                (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DETAIL_DATA>(),
                out _, IntPtr.Zero))
                return null;

            const uint GENERIC_READ = 0x80000000;
            const uint GENERIC_WRITE = 0x40000000;
            const uint OPEN_EXISTING = 3;
            const uint FILE_ATTRIBUTE_NORMAL = 0x80;
            const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
            const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

            var handle = CreateFile(
                detailData.DevicePath,
                GENERIC_READ | GENERIC_WRITE,
                3, // FILE_SHARE_READ | FILE_SHARE_WRITE
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH,
                IntPtr.Zero);

            return handle.IsInvalid ? null : handle;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }
    }

    /// <summary>
    /// Adds a virtual display. Returns the display index (0-based), or -1 on failure.
    /// </summary>
    public static int AddDisplay(SafeFileHandle deviceHandle)
    {
        var inBuffer = new byte[32];
        var outBuffer = new byte[4];
        if (DeviceIoControl(deviceHandle, IOCTL_ADD, inBuffer, 32,
            outBuffer, 4, out _, IntPtr.Zero))
        {
            Update(deviceHandle);
            return BitConverter.ToInt32(outBuffer, 0);
        }
        int error = Marshal.GetLastWin32Error();
        Console.WriteLine($"[VDD] DeviceIoControl(IOCTL_ADD) failed with error: {error}");
        return -1;
    }

    /// <summary>
    /// Removes a virtual display by its index.
    /// </summary>
    public static bool RemoveDisplay(SafeFileHandle deviceHandle, int displayIndex)
    {
        var inBuffer = new byte[32];
        // 16-bit big-endian index
        inBuffer[0] = (byte)((displayIndex >> 8) & 0xFF);
        inBuffer[1] = (byte)(displayIndex & 0xFF);

        bool success = DeviceIoControl(deviceHandle, IOCTL_REMOVE,
            inBuffer, 32, null, 0, out _, IntPtr.Zero);
        if (success) Update(deviceHandle);
        return success;
    }

    /// <summary>
    /// Sends a keep-alive ping to the driver. Must be called at least every ~100ms.
    /// If not called for more than ~1 second, all virtual displays are automatically removed.
    /// </summary>
    public static bool Update(SafeFileHandle deviceHandle)
    {
        var inBuffer = new byte[32];
        return DeviceIoControl(deviceHandle, IOCTL_UPDATE,
            inBuffer, 32, null, 0, out _, IntPtr.Zero);
    }

    /// <summary>
    /// Returns a human-readable description for a DeviceStatus value.
    /// </summary>
    public static string GetStatusMessage(DeviceStatus status)
    {
        return status switch
        {
            DeviceStatus.Ok => "Parsec VDD driver is ready",
            DeviceStatus.Inaccessible => "Cannot access Parsec VDD driver — check permissions",
            DeviceStatus.Unknown => "Parsec VDD driver status is unknown",
            DeviceStatus.UnknownProblem => "Parsec VDD driver has an unknown problem",
            DeviceStatus.Disabled => "Parsec VDD driver is disabled — enable it in Device Manager",
            DeviceStatus.DriverError => "Parsec VDD driver encountered an error",
            DeviceStatus.RestartRequired => "Restart required to use Parsec VDD driver",
            DeviceStatus.DisabledService => "Parsec VDD driver service is disabled",
            DeviceStatus.NotInstalled => "Parsec VDD driver is NOT installed — " +
                "install parsec-vdd-0.41 from https://github.com/nomi-san/parsec-vdd/releases",
            _ => $"Unknown status: {status}"
        };
    }
}
