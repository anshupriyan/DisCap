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
    public float MotionThreshold { get; set; } = 0.15f;

    /// <summary>
    /// Timeout in milliseconds for Desktop Duplication AcquireNextFrame.
    /// Lower values = more responsive but higher CPU usage when idle.
    /// </summary>
    public int CaptureTimeoutMs { get; set; } = 100;

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
    /// Parse command-line arguments into a DiscapConfig.
    /// Supports: --width, --height, --fps, --port, --threshold, --adb, --lz4-only
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
                case "--adapter" when i + 1 < args.Length:
                    config.AdapterIndex = int.Parse(args[++i]);
                    break;
                case "--lz4-only":
                    config.ForceLz4Only = true;
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
              --threshold <0.0-1.0> Motion threshold for NVENC switch (default: 0.15)
              --adb <path>          Path to adb.exe (default: auto-detect)
              --adapter <index>     GPU adapter index (default: 0)
              --lz4-only            Force LZ4-only mode, disable hardware encoding
              --help, -h            Show this help message

            Examples:
              Discap.Host                                    # Default settings
              Discap.Host --width 2560 --height 1600         # High-res tablet
              Discap.Host --lz4-only                         # LZ4 only, no NVENC
              Discap.Host --port 9999 --fps 120              # Custom port and refresh rate
            """);
    }
}
