using System.Diagnostics;

namespace Discap.Host.Transport;

/// <summary>
/// Manages ADB (Android Debug Bridge) for device detection and port forwarding.
///
/// Port forwarding creates a tunnel from localhost:{port} on the PC to
/// tcp:{port} on the Android device. The Android app listens on that port,
/// and the Windows host connects to it through localhost.
///
/// ADB forward direction: PC → Android
///   adb forward tcp:53516 tcp:53516
///   PC app connects to localhost:53516 → reaches Android app on port 53516
/// </summary>
public sealed class AdbManager : IDisposable
{
    private string? _adbPath;
    private int _forwardedPort;
    private bool _disposed;

    /// <summary>Whether ADB was found and is usable.</summary>
    public bool IsAvailable => _adbPath != null;

    /// <summary>The resolved path to adb.exe.</summary>
    public string? AdbPath => _adbPath;

    /// <summary>
    /// Locates adb.exe on the system.
    /// Search order: explicit path → PATH → common install locations.
    /// </summary>
    /// <param name="explicitPath">Optional explicit path to adb.exe.</param>
    /// <returns>True if ADB was found.</returns>
    public bool FindAdb(string? explicitPath = null)
    {
        // 1. Explicit path from config.
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
        {
            _adbPath = explicitPath;
            Console.WriteLine($"[ADB] Using explicit path: {_adbPath}");
            return true;
        }

        // 2. Check PATH via `where adb`.
        try
        {
            var result = RunAdbCommand("where", "adb", waitMs: 5000);
            if (result.ExitCode == 0)
            {
                var path = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.Trim();
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    _adbPath = path;
                    Console.WriteLine($"[ADB] Found on PATH: {_adbPath}");
                    return true;
                }
            }
        }
        catch { }

        // 3. Common install locations.
        var searchPaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android", "Sdk", "platform-tools", "adb.exe"),
            @"C:\Program Files\SuperDisplay\adb\adb.exe",
            @"C:\android-sdk\platform-tools\adb.exe",
        };

        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
            {
                _adbPath = path;
                Console.WriteLine($"[ADB] Found at: {_adbPath}");
                return true;
            }
        }

        Console.Error.WriteLine("[ADB] adb.exe not found! Install Android SDK Platform Tools or specify --adb <path>");
        return false;
    }

    /// <summary>
    /// Checks whether at least one Android device is connected.
    /// </summary>
    public bool IsDeviceConnected()
    {
        if (_adbPath == null) return false;

        try
        {
            var result = RunAdbCommand(_adbPath, "devices", waitMs: 5000);
            if (result.ExitCode != 0) return false;

            // Output format:
            //   List of devices attached
            //   SERIAL\tdevice
            var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return lines.Any(line =>
                line.Contains('\t') &&
                (line.Contains("device") || line.Contains("recovery")) &&
                !line.StartsWith("List"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets up ADB port forwarding: localhost:{port} → Android device tcp:{port}.
    /// </summary>
    public bool SetupForward(int port)
    {
        if (_adbPath == null)
        {
            Console.Error.WriteLine("[ADB] Cannot setup forward — ADB not found");
            return false;
        }

        try
        {
            // Remove any existing forward on this port first.
            RunAdbCommand(_adbPath, $"forward --remove tcp:{port}", waitMs: 3000);

            // Set up the forward.
            var result = RunAdbCommand(_adbPath, $"forward tcp:{port} tcp:{port}", waitMs: 5000);
            if (result.ExitCode == 0)
            {
                _forwardedPort = port;
                Console.WriteLine($"[ADB] Port forward active: localhost:{port} → device tcp:{port}");
                return true;
            }
            else
            {
                Console.Error.WriteLine($"[ADB] Forward failed: {result.Error}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ADB] Forward error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes the ADB port forwarding.
    /// </summary>
    public void RemoveForward(int port)
    {
        if (_adbPath == null) return;

        try
        {
            RunAdbCommand(_adbPath, $"forward --remove tcp:{port}", waitMs: 3000);
            Console.WriteLine($"[ADB] Port forward removed: tcp:{port}");
            _forwardedPort = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ADB] Failed to remove forward: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the serial number of the first connected device.
    /// </summary>
    public string? GetDeviceSerial()
    {
        if (_adbPath == null) return null;

        try
        {
            var result = RunAdbCommand(_adbPath, "devices", waitMs: 5000);
            if (result.ExitCode != 0) return null;

            var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var deviceLine = lines.FirstOrDefault(line =>
                line.Contains('\t') && line.Contains("device") && !line.StartsWith("List"));

            return deviceLine?.Split('\t').FirstOrDefault()?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private (int ExitCode, string Output, string Error) RunAdbCommand(
        string executable, string arguments, int waitMs = 10000)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit(waitMs);

        return (process.ExitCode, output, error);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_forwardedPort > 0)
        {
            RemoveForward(_forwardedPort);
        }
    }
}
