namespace Discap.Host.Config;

/// <summary>
/// Configuration for the Discap streaming host.
/// All values have sensible defaults and can be overridden via command-line arguments.
/// </summary>
public sealed class DiscapConfig
{
    /// <summary>
    /// Virtual display width in pixels.
    /// Default 1920 — widely compatible; use 2560 for high-DPI tablets.
    /// </summary>
    public int Width { get; set; } = 1920;

    /// <summary>
    /// Virtual display height in pixels.
    /// Default 1200 — matches common 16:10 tablet aspect ratio.
    /// </summary>
    public int Height { get; set; } = 1200;

    /// <summary>
    /// Virtual display refresh rate in Hz.
    /// </summary>
    public int RefreshRate { get; set; } = 60;

    /// <summary>
    /// TCP port for ADB socket forwarding.
    /// Both local and remote ports use this value.
    /// </summary>
    public int Port { get; set; } = 53516;

    /// <summary>
    /// Motion detection threshold (0.0 to 1.0).
    /// If the fraction of dirty screen area exceeds this, use NVENC encoding.
    /// Below this threshold, LZ4 is used for lossless compression.
    /// Default 0.15 (15% of screen area).
    /// </summary>
    public float MotionThreshold { get; set; } = 0.0001f;

    /// <summary>
    /// Timeout in milliseconds for Desktop Duplication AcquireNextFrame.
    /// Lower values = more responsive but higher CPU usage when idle.
    /// </summary>
    public int CaptureTimeoutMs { get; set; } = 100;

    /// <summary>
    /// Target video bitrate in bits per second.
    /// Default is 20,000,000 (20 Mbps) for smooth high-motion streaming.
    /// </summary>
    public int Bitrate { get; set; } = 20_000_000;

    /// <summary>
    /// Path to adb.exe. If null, searches PATH and common locations.
    /// </summary>
    public string? AdbPath { get; set; }

    /// <summary>
    /// Target adapter index for DXGI (0 = primary GPU).
    /// </summary>
    public int AdapterIndex { get; set; } = 0;

    /// <summary>
    /// Whether to force LZ4-only mode (disable NVENC).
    /// Useful for debugging or when no hardware encoder is available.
    /// </summary>
    public bool ForceLz4Only { get; set; } = false;

    /// <summary>
    /// Transport mode: "adb" (default, safe) or "aoap" (USB accessory, requires WinUSB driver).
    /// AOAP mode replaces the phone's ADB driver with WinUSB — ADB and file transfer
    /// will NOT work while active. Only use when explicitly requested by the user.
    /// </summary>
    public string TransportMode { get; set; } = "adb";

    /// <summary>
    /// When true, prints step-by-step instructions for reverting the WinUSB driver
    /// back to the stock ADB driver, then exits. Used after switching away from AOAP mode.
    /// </summary>
    public bool RevertDriver { get; set; } = false;

    /// <summary>
    /// Parse command-line arguments into a DiscapConfig.
    /// Supports: --width, --height, --fps, --port, --threshold, --adb, --lz4-only,
    ///           --transport, --revert-driver
    /// </summary>
    public static DiscapConfig FromArgs(string[] args)
    {
        var config = new DiscapConfig();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--width" when i + 1 < args.Length:
                    config.Width = int.Parse(args[++i]);
                    break;
                case "--height" when i + 1 < args.Length:
                    config.Height = int.Parse(args[++i]);
                    break;
                case "--fps" when i + 1 < args.Length:
                    config.RefreshRate = int.Parse(args[++i]);
                    break;
                case "--port" when i + 1 < args.Length:
                    config.Port = int.Parse(args[++i]);
                    break;
                case "--threshold" when i + 1 < args.Length:
                    config.MotionThreshold = float.Parse(args[++i]);
                    break;
                case "--adb" when i + 1 < args.Length:
                    config.AdbPath = args[++i];
                    break;
                case "--bitrate" when i + 1 < args.Length:
                    config.Bitrate = int.Parse(args[++i]) * 1_000_000; // Parse as Mbps
                    break;
                case "--adapter" when i + 1 < args.Length:
                    config.AdapterIndex = int.Parse(args[++i]);
                    break;
                case "--lz4-only":
                    config.ForceLz4Only = true;
                    break;
                case "--transport" when i + 1 < args.Length:
                    var mode = args[++i].ToLowerInvariant();
                    if (mode is "adb" or "aoap")
                        config.TransportMode = mode;
                    else
                        Console.Error.WriteLine($"[CFG] Unknown transport mode '{mode}', using 'adb'");
                    break;
                case "--revert-driver":
                    config.RevertDriver = true;
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        return config;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Discap — Open-Source Virtual Display Streamer
            =============================================

            Usage: Discap.Host [options]

            Options:
              --width <pixels>      Virtual display width (default: 1920)
              --height <pixels>     Virtual display height (default: 1200)
              --fps <rate>          Refresh rate in Hz (default: 60)
              --port <port>         ADB forward port (default: 53516)
              --threshold <0.0-1.0> Motion threshold for NVENC switch (default: 0.0001)
              --bitrate <Mbps>      Target video bitrate in Mbps (default: 20)
              --adb <path>          Path to adb.exe (default: auto-detect)
              --adapter <index>     GPU adapter index (default: 0)
              --lz4-only            Force LZ4-only mode, disable hardware encoding
              --transport <mode>    Transport mode: 'adb' (default) or 'aoap'
              --revert-driver       Print instructions to revert WinUSB → ADB driver
              --help, -h            Show this help message

            Transport Modes:
              adb   (default)  Uses ADB port forwarding over USB. Safe, works with
                               all devices. ADB and file transfer remain functional.
              aoap             Uses Android Open Accessory Protocol for direct USB
                               bulk transfer. Requires WinUSB driver installation
                               (replaces ADB driver). Lower latency, but ADB and
                               file transfer will NOT work while active.

            Examples:
              Discap.Host                                    # Default (ADB mode)
              Discap.Host --transport aoap                   # AOAP mode (first run installs driver)
              Discap.Host --revert-driver                    # Revert to ADB driver
              Discap.Host --width 2560 --height 1600         # High-res tablet
              Discap.Host --lz4-only                         # LZ4 only, no NVENC
            """);
    }
}

