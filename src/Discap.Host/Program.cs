using System.Diagnostics;
using Discap.Host.Capture;
using Discap.Host.Compression;
using Discap.Host.Config;
using Discap.Host.Display;
using Discap.Host.Protocol;
using Discap.Host.Transport;

namespace Discap.Host;

/// <summary>
/// Discap — Open-Source Virtual Display Streamer
///
/// Main orchestrator that ties together:
///   1. Virtual display creation (Parsec VDD)
///   2. Screen capture (DXGI Desktop Duplication)
///   3. Adaptive compression (LZ4 for static, NVENC for motion)
///   4. Binary protocol framing (32-byte DCAP headers)
///   5. ADB socket streaming to Android tablet
///
/// Run with --help to see all options.
/// Requires administrator privileges for VDD driver access.
/// </summary>
public static class Program
{
    private static volatile bool _running = true;

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("""

            ██████╗ ██╗███████╗ ██████╗ █████╗ ██████╗
            ██╔══██╗██║██╔════╝██╔════╝██╔══██╗██╔══██╗
            ██║  ██║██║███████╗██║     ███████║██████╔╝
            ██║  ██║██║╚════██║██║     ██╔══██║██╔═══╝
            ██████╔╝██║███████║╚██████╗██║  ██║██║
            ╚═════╝ ╚═╝╚══════╝ ╚═════╝╚═╝  ╚═╝╚═╝
                Open-Source Virtual Display Streamer v0.1.0

            """);

        // Parse configuration.
        var config = DiscapConfig.FromArgs(args);

        Console.WriteLine($"[CFG] Resolution: {config.Width}x{config.Height} @ {config.RefreshRate}Hz");
        Console.WriteLine($"[CFG] Port: {config.Port}");
        Console.WriteLine($"[CFG] Motion threshold: {config.MotionThreshold:P0}");
        Console.WriteLine($"[CFG] LZ4-only mode: {config.ForceLz4Only}");
        Console.WriteLine();

        // Setup Ctrl+C handler for clean shutdown.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _running = false;
            Console.WriteLine("\n[SYS] Shutdown requested (Ctrl+C)...");
        };

        using var vddManager = new VirtualDisplayManager();
        using var duplicator = new DesktopDuplicator(config.AdapterIndex, config.CaptureTimeoutMs);
        using var encoder = new HardwareEncoder();
        using var adbManager = new AdbManager();
        using var server = new StreamServer(config.Port);

        var lz4 = new Lz4Compressor();
        var analyzer = new FrameAnalyzer(config.MotionThreshold);
        var packetWriter = new PacketWriter();

        // ─── Step 1: Check and start Parsec VDD ───────────────────────
        Console.WriteLine("═══ Step 1: Virtual Display ═══");
        var driverStatus = ParsecVdd.QueryDriverStatus();
        Console.WriteLine($"[VDD] Driver status: {ParsecVdd.GetStatusMessage(driverStatus)}");

        if (driverStatus != ParsecVdd.DeviceStatus.Ok)
        {
            Console.Error.WriteLine("[VDD] Cannot proceed without the Parsec VDD driver.");
            Console.Error.WriteLine("[VDD] Install from: https://github.com/nomi-san/parsec-vdd/releases");
            return 1;
        }

        if (!vddManager.Start())
        {
            Console.Error.WriteLine("[VDD] Failed to create virtual display.");
            return 1;
        }

        // Give Windows time to register the new display.
        Console.WriteLine("[VDD] Waiting for display to initialize...");
        await Task.Delay(2000);
        Console.WriteLine();

        // ─── Step 2: Initialize screen capture ────────────────────────
        Console.WriteLine("═══ Step 2: Screen Capture ═══");

        // Target the last output (the newly created virtual display).
        if (!duplicator.Initialize(-1))
        {
            Console.Error.WriteLine("[CAP] Failed to initialize Desktop Duplication.");
            Console.Error.WriteLine("[CAP] The virtual display may need a moment to appear.");
            Console.Error.WriteLine("[CAP] Try running again, or check Display Settings.");
            return 1;
        }
        Console.WriteLine();

        // ─── Step 3: Initialize hardware encoder (optional) ───────────
        Console.WriteLine("═══ Step 3: Compression ═══");

        bool nvencAvailable = false;
        if (!config.ForceLz4Only)
        {
            nvencAvailable = encoder.Initialize(
                duplicator.Width, duplicator.Height, config.RefreshRate);
        }
        else
        {
            Console.WriteLine("[ENC] LZ4-only mode — hardware encoding disabled");
        }

        Console.WriteLine("[LZ4] LZ4 compressor ready");
        Console.WriteLine();

        // ─── Step 4: Setup ADB ────────────────────────────────────────
        Console.WriteLine("═══ Step 4: ADB Transport ═══");

        if (!adbManager.FindAdb(config.AdbPath))
        {
            Console.Error.WriteLine("[ADB] Cannot proceed without ADB.");
            return 1;
        }

        if (!adbManager.IsDeviceConnected())
        {
            Console.Error.WriteLine("[ADB] No Android device detected!");
            Console.Error.WriteLine("[ADB] Connect your tablet via USB and enable USB debugging.");
            Console.Error.WriteLine("[ADB] Waiting for device...");

            // Poll for device connection.
            while (_running && !adbManager.IsDeviceConnected())
            {
                await Task.Delay(2000);
            }

            if (!_running) return 0;
        }

        var serial = adbManager.GetDeviceSerial();
        Console.WriteLine($"[ADB] Device connected: {serial}");

        if (!adbManager.SetupForward(config.Port))
        {
            Console.Error.WriteLine("[ADB] Failed to setup port forwarding.");
            return 1;
        }
        Console.WriteLine();

        // ─── Step 5: Start streaming server ───────────────────────────
        Console.WriteLine("═══ Step 5: Streaming ═══");
        server.Start();

        // Main loop: accept clients and stream frames.
        var cts = new CancellationTokenSource();
        uint sequenceNumber = 0;
        var streamStartTime = Stopwatch.GetTimestamp();

        while (_running)
        {
            // Wait for a client to connect.
            if (!await server.WaitForClientAsync(cts.Token))
            {
                if (!_running) break;
                await Task.Delay(1000);
                continue;
            }

            Console.WriteLine("[STREAM] Starting capture loop...");
            Console.WriteLine("[STREAM] Press Ctrl+C to stop.");
            Console.WriteLine();

            // Reset counters for this session.
            sequenceNumber = 0;
            streamStartTime = Stopwatch.GetTimestamp();
            int fpsCounter = 0;
            long lastFpsTime = Stopwatch.GetTimestamp();
            int lz4Frames = 0;
            int nvencFrames = 0;
            long totalBytesSent = 0;

            // ─── Capture loop ─────────────────────────────────────────
            while (_running && server.IsClientConnected)
            {
                // Capture next frame.
                using var frame = duplicator.AcquireNextFrame();
                if (frame == null)
                {
                    // No new frame — screen hasn't changed, or timeout.
                    continue;
                }

                // Determine encoding type based on motion analysis.
                var frameType = FrameType.LZ4; // Default
                byte[] compressedData;
                int compressedSize;

                if (nvencAvailable && !config.ForceLz4Only)
                {
                    frameType = analyzer.Analyze(frame);
                }

                // Compress or encode the frame.
                if (frameType == FrameType.NVENC && nvencAvailable)
                {
                    compressedSize = encoder.Encode(frame, out compressedData);
                    if (compressedSize <= 0)
                    {
                        // NVENC failed — fall back to LZ4 for this frame.
                        frameType = FrameType.LZ4;
                        var tightPixels = frame.GetTightPixels();
                        compressedSize = lz4.Compress(tightPixels, out compressedData);
                    }
                    else
                    {
                        nvencFrames++;
                    }
                }
                else
                {
                    frameType = FrameType.LZ4;
                    var tightPixels = frame.GetTightPixels();
                    compressedSize = lz4.Compress(tightPixels, out compressedData);
                    lz4Frames++;
                }

                // Calculate timestamp.
                long elapsedTicks = frame.TimestampTicks - streamStartTime;
                long elapsedUs = elapsedTicks * 1_000_000 / Stopwatch.Frequency;

                // Build packet header.
                int originalSize = frame.Width * frame.Height * 4; // BGRA = 4 bytes/pixel
                ushort flags = sequenceNumber == 0 ? PacketHeader.FLAG_KEYFRAME : (ushort)0;

                var header = PacketHeader.Create(
                    frameType,
                    (ushort)frame.Width,
                    (ushort)frame.Height,
                    (uint)originalSize,
                    (uint)compressedSize,
                    elapsedUs,
                    sequenceNumber++,
                    flags);

                // Send packet to client.
                try
                {
                    packetWriter.WritePacket(server.ClientStream!, header, compressedData, 0, compressedSize);
                    totalBytesSent += PacketHeader.SIZE + compressedSize;
                }
                catch (Exception)
                {
                    Console.Error.WriteLine("[STREAM] Write failed — client disconnected");
                    break;
                }

                // FPS counter — update every second.
                fpsCounter++;
                long now = Stopwatch.GetTimestamp();
                long elapsed = now - lastFpsTime;
                if (elapsed >= Stopwatch.Frequency) // 1 second
                {
                    double fps = fpsCounter * (double)Stopwatch.Frequency / elapsed;
                    double mbps = totalBytesSent * 8.0 / 1_000_000;
                    float ratio = originalSize > 0 ? (float)compressedSize / originalSize : 0;
                    float dirtyRatio = analyzer.ComputeDirtyRatio(frame);

                    Console.Write($"\r[STREAM] {fps:F1} fps | {mbps:F1} Mbps total | " +
                                  $"LZ4:{lz4Frames} NVENC:{nvencFrames} | " +
                                  $"ratio:{ratio:P0} | dirty:{dirtyRatio:P0}   ");

                    fpsCounter = 0;
                    lastFpsTime = now;
                    lz4Frames = 0;
                    nvencFrames = 0;
                    totalBytesSent = 0;
                }
            }

            Console.WriteLine();
            Console.WriteLine("[STREAM] Session ended.");
            Console.WriteLine();

            if (_running)
            {
                Console.WriteLine("[NET] Waiting for reconnection...");
            }
        }

        // ─── Clean shutdown ───────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("═══ Shutting down ═══");
        cts.Cancel();

        return 0;
    }
}
