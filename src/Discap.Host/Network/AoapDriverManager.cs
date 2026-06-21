using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Discap.Host.Network;

/// <summary>
/// Manages WinUSB driver installation for AOAP (Android Open Accessory Protocol) mode.
///
/// The problem: LibUsbDotNet can only see devices using WinUSB/libusb/libusbK drivers.
/// An Android phone in normal mode uses the ADB driver, so LibUsbDotNet can't enumerate it
/// and can't send the AOA control transfers (51/52/53) needed to switch it to accessory mode.
///
/// Solution: Use wdi-simple.exe (from libwdi, same engine as Zadig) to install WinUSB
/// for the phone's VID/PID. This replaces the ADB driver — ADB and file transfer stop working,
/// but LibUsbDotNet can now see the device and send AOA control transfers.
///
/// This class handles:
/// 1. Detecting the phone's VID/PID via WMI (works regardless of current driver)
/// 2. Installing WinUSB for the phone's normal VID/PID
/// 3. Installing WinUSB for Google's AOA PIDs (0x18D1/0x2D00 and 0x18D1/0x2D01)
/// 4. Printing revert instructions for switching back to ADB mode
/// </summary>
public sealed class AoapDriverManager
{
    private readonly string _wdiSimplePath;
    private readonly string _stateFilePath;

    private const int GoogleVendorId = 0x18D1;
    private const int AoaPid1 = 0x2D00;
    private const int AoaPid2 = 0x2D01;

    public AoapDriverManager()
    {
        var baseDir = AppContext.BaseDirectory;
        _wdiSimplePath = Path.Combine(baseDir, "drivers", "wdi-simple.exe");
        _stateFilePath = Path.Combine(baseDir, "drivers", "aoap_state.json");
    }

    /// <summary>
    /// Detects a connected Android device's VID/PID using WMI (Win32_PnPEntity).
    /// This works regardless of which driver the phone is using (ADB, MTP, WinUSB, etc.)
    /// because WMI queries the PnP device tree directly, not through any USB library.
    /// </summary>
    /// <returns>The VID/PID tuple, or null if no Android USB device is found.</returns>
    public (int vid, int pid)? DetectAndroidDeviceViaWmi()
    {
        Console.WriteLine("[AOAP] Detecting Android device via WMI...");

        try
        {
            // Query all USB devices from the PnP entity table.
            // DeviceID format: USB\VID_XXXX&PID_YYYY\serial
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, Status FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_%'");

            var results = searcher.Get();
            Console.WriteLine($"[AOAP] Found {results.Count} USB PnP entities");

            // Known Android vendor IDs (most common manufacturers).
            // We check these to avoid accidentally targeting a keyboard or mouse.
            var androidVids = new HashSet<int>
            {
                0x18D1, // Google
                0x04E8, // Samsung
                0x2717, // Xiaomi
                0x22B8, // Motorola
                0x0BB4, // HTC
                0x12D1, // Huawei
                0x2A70, // OnePlus
                0x1004, // LG
                0x0FCE, // Sony
                0x2916, // Yota
                0x1949, // Lab126 (Amazon)
                0x0E8D, // MediaTek
                0x2C7C, // Quectel
                0x05C6, // Qualcomm
                0x19D2, // ZTE
                0x1BBB, // T&A (Alcatel/TCL)
                0x0B05, // Asus
                0x2B4C, // Vivo
                0x2A45, // Meizu
                0x1532, // Razer
                0x2D95, // realme
            };

            foreach (ManagementObject device in results)
            {
                string? deviceId = device["DeviceID"]?.ToString();
                string? name = device["Name"]?.ToString();
                if (deviceId == null) continue;

                // Parse VID and PID from DeviceID string
                var match = Regex.Match(deviceId, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
                if (!match.Success) continue;

                int vid = Convert.ToInt32(match.Groups[1].Value, 16);
                int pid = Convert.ToInt32(match.Groups[2].Value, 16);

                // Skip Google AOA PIDs — we want the phone's NORMAL VID/PID
                if (vid == GoogleVendorId && (pid == AoaPid1 || pid == AoaPid2))
                {
                    Console.WriteLine($"[AOAP]   Skipping AOA device: VID=0x{vid:X4} PID=0x{pid:X4}");
                    continue;
                }

                if (androidVids.Contains(vid))
                {
                    Console.WriteLine($"[AOAP]   Found Android device: VID=0x{vid:X4} PID=0x{pid:X4} Name=\"{name}\"");
                    Console.WriteLine($"[AOAP]   DeviceID: {deviceId}");
                    return (vid, pid);
                }

                // Log non-Android devices at debug level
                Console.WriteLine($"[AOAP]   Non-Android USB device: VID=0x{vid:X4} PID=0x{pid:X4} Name=\"{name}\"");
            }

            Console.Error.WriteLine("[AOAP] No Android device found via WMI. Is your phone connected via USB?");
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AOAP] WMI query failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Ensures WinUSB is installed for a specific VID/PID using wdi-simple.exe.
    /// Skips installation if the state file indicates it was already done.
    /// </summary>
    public bool EnsureWinUsbDriverInstalled(int vid, int pid, string deviceName)
    {
        var state = LoadState();

        // Check if already installed
        string vidPidKey = $"{vid:X4}:{pid:X4}";
        if (state.InstalledDevices.Contains(vidPidKey))
        {
            Console.WriteLine($"[AOAP] WinUSB already installed for VID=0x{vid:X4} PID=0x{pid:X4} (cached)");
            return true;
        }

        // Verify wdi-simple.exe exists
        if (!File.Exists(_wdiSimplePath))
        {
            Console.Error.WriteLine($"[AOAP] ERROR: wdi-simple.exe not found at: {_wdiSimplePath}");
            Console.Error.WriteLine("[AOAP] Download it from https://github.com/pbatard/libwdi/releases");
            Console.Error.WriteLine("[AOAP] Place it at: drivers/wdi-simple.exe");
            return false;
        }

        // ── SAFETY WARNING ──
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  ⚠️  AOAP DRIVER INSTALLATION                                   ║");
        Console.WriteLine("║                                                                  ║");
        Console.WriteLine($"║  Installing WinUSB driver for VID=0x{vid:X4} PID=0x{pid:X4}              ║");
        Console.WriteLine("║                                                                  ║");
        Console.WriteLine("║  WARNING: This will REPLACE your phone's ADB driver with WinUSB. ║");
        Console.WriteLine("║  While this is active:                                           ║");
        Console.WriteLine("║    • ADB commands will NOT work                                  ║");
        Console.WriteLine("║    • File transfer (MTP) will NOT work                           ║");
        Console.WriteLine("║    • Only AOAP streaming will function                           ║");
        Console.WriteLine("║                                                                  ║");
        Console.WriteLine("║  To revert: run  Discap.Host --revert-driver                     ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Run wdi-simple.exe
        Console.WriteLine($"[AOAP] Running: wdi-simple.exe --vid 0x{vid:X4} --pid 0x{pid:X4} --type 0 --name \"{deviceName}\"");

        bool success = RunWdiSimple($"--vid 0x{vid:X4} --pid 0x{pid:X4} --type 0 --name \"{deviceName}\"");
        if (success)
        {
            state.InstalledDevices.Add(vidPidKey);
            SaveState(state);
            Console.WriteLine($"[AOAP] WinUSB driver installed successfully for VID=0x{vid:X4} PID=0x{pid:X4}");
        }
        else
        {
            Console.Error.WriteLine($"[AOAP] WinUSB driver installation FAILED for VID=0x{vid:X4} PID=0x{pid:X4}");
        }

        return success;
    }

    /// <summary>
    /// Pre-installs WinUSB for Google's AOA PIDs (0x18D1/0x2D00 and 0x18D1/0x2D01).
    /// These are the VID/PIDs the phone uses AFTER switching to accessory mode.
    /// Without WinUSB for these PIDs, the re-enumerated device won't be accessible.
    /// </summary>
    public bool EnsureAoaDriversInstalled()
    {
        var state = LoadState();
        if (state.AoaPidsInstalled)
        {
            Console.WriteLine("[AOAP] AOA PID drivers already installed (cached)");
            return true;
        }

        if (!File.Exists(_wdiSimplePath))
        {
            Console.Error.WriteLine($"[AOAP] ERROR: wdi-simple.exe not found at: {_wdiSimplePath}");
            return false;
        }

        Console.WriteLine("[AOAP] Installing WinUSB for Google AOA PIDs...");

        // PID 0x2D00 — AOA mode only
        Console.WriteLine($"[AOAP] Installing for VID=0x{GoogleVendorId:X4} PID=0x{AoaPid1:X4} (AOA mode)...");
        bool ok1 = RunWdiSimple($"--vid 0x{GoogleVendorId:X4} --pid 0x{AoaPid1:X4} --type 0 --name \"Discap AOA\"");

        // PID 0x2D01 — AOA + ADB mode
        Console.WriteLine($"[AOAP] Installing for VID=0x{GoogleVendorId:X4} PID=0x{AoaPid2:X4} (AOA+ADB mode)...");
        bool ok2 = RunWdiSimple($"--vid 0x{GoogleVendorId:X4} --pid 0x{AoaPid2:X4} --type 0 --name \"Discap AOA+ADB\"");

        if (ok1 && ok2)
        {
            state.AoaPidsInstalled = true;
            SaveState(state);
            Console.WriteLine("[AOAP] AOA PID drivers installed successfully");
            return true;
        }

        Console.Error.WriteLine("[AOAP] Failed to install one or more AOA PID drivers");
        return false;
    }

    /// <summary>
    /// Prints step-by-step instructions for reverting the WinUSB driver
    /// back to the stock ADB driver via Device Manager.
    /// </summary>
    public void PrintRevertInstructions()
    {
        var state = LoadState();
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("  How to Revert WinUSB Driver → ADB Driver");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine();
        Console.WriteLine("  Follow these steps to restore ADB and file transfer functionality:");
        Console.WriteLine();
        Console.WriteLine("  1. Open Device Manager (Win+X → Device Manager)");
        Console.WriteLine();
        Console.WriteLine("  2. Expand 'Universal Serial Bus devices'");
        Console.WriteLine();
        Console.WriteLine("  3. Find your phone (may be listed as 'Discap AOAP' or by its");
        Console.WriteLine("     manufacturer name). It will have a WinUSB-style icon.");
        Console.WriteLine();
        Console.WriteLine("  4. Right-click → 'Uninstall device'");
        Console.WriteLine("     ✓ CHECK the box: 'Attempt to remove the driver for this device'");
        Console.WriteLine("     → Click 'Uninstall'");
        Console.WriteLine();
        Console.WriteLine("  5. Unplug your phone and plug it back in");
        Console.WriteLine("     Windows will automatically re-install the stock ADB driver.");
        Console.WriteLine();
        Console.WriteLine("  6. Verify: run 'adb devices' — your phone should appear again.");
        Console.WriteLine();

        if (state.InstalledDevices.Count > 0)
        {
            Console.WriteLine("  Devices that had WinUSB installed by Discap:");
            foreach (var dev in state.InstalledDevices)
            {
                var parts = dev.Split(':');
                Console.WriteLine($"    • VID=0x{parts[0]} PID=0x{parts[1]}");
            }
            Console.WriteLine();
        }

        Console.WriteLine("  After reverting, you can also delete the state file to reset:");
        Console.WriteLine($"    {_stateFilePath}");
        Console.WriteLine();
        Console.WriteLine("  To use Discap again after reverting, run with default ADB mode:");
        Console.WriteLine("    Discap.Host              (uses ADB, no driver changes)");
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine();

        // Clear the state file so next AOAP activation re-installs
        state.InstalledDevices.Clear();
        state.AoaPidsInstalled = false;
        SaveState(state);
        Console.WriteLine("[AOAP] State file cleared. Next AOAP activation will re-install drivers.");
    }

    /// <summary>
    /// Runs wdi-simple.exe with the given arguments. Returns true on success.
    /// </summary>
    private bool RunWdiSimple(string arguments)
    {
        try
        {
            Console.WriteLine($"[AOAP]   Executing: {_wdiSimplePath} {arguments}");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _wdiSimplePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };

            process.Start();
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000); // 30 second timeout for driver install

            if (!string.IsNullOrWhiteSpace(stdout))
                Console.WriteLine($"[AOAP]   stdout: {stdout.Trim()}");
            if (!string.IsNullOrWhiteSpace(stderr))
                Console.Error.WriteLine($"[AOAP]   stderr: {stderr.Trim()}");

            Console.WriteLine($"[AOAP]   Exit code: {process.ExitCode}");
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AOAP]   Failed to run wdi-simple.exe: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    #region State file persistence

    private AoapState LoadState()
    {
        try
        {
            if (File.Exists(_stateFilePath))
            {
                var json = File.ReadAllText(_stateFilePath);
                return JsonSerializer.Deserialize<AoapState>(json) ?? new AoapState();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AOAP] Failed to read state file: {ex.Message}");
        }
        return new AoapState();
    }

    private void SaveState(AoapState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(_stateFilePath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_stateFilePath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AOAP] Failed to write state file: {ex.Message}");
        }
    }

    #endregion

    /// <summary>
    /// Persisted state tracking which VID/PIDs have had WinUSB installed.
    /// </summary>
    private sealed class AoapState
    {
        public List<string> InstalledDevices { get; set; } = new();
        public bool AoaPidsInstalled { get; set; }
    }
}
