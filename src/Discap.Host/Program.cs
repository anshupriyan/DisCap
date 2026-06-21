using System.Diagnostics;
using Discap.Host.Capture;
using Discap.Host.Compression;
using Discap.Host.Config;
using Discap.Host.Display;
using Discap.Host.Network;
using Discap.Host.Protocol;
using Discap.Host.Transport;
using Discap.Host.Input;
using System.Net.Sockets;

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
        Console.WriteLine($"[CFG] Transport: {config.TransportMode.ToUpper()}");
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

        // Setup a background timer to keep the cursor visible when idle
        using var cursorKeepAliveTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                var pos = System.Windows.Forms.Cursor.Position;
                System.Windows.Forms.Cursor.Position = new System.Drawing.Point(pos.X + 1, pos.Y);
                System.Windows.Forms.Cursor.Position = pos;
            }
            catch { }
        }, null, 5000, 5000);


        // ─── Handle --revert-driver (early exit) ──────────────────────
        if (config.RevertDriver)
        {
            var driverMgr = new AoapDriverManager();
            driverMgr.PrintRevertInstructions();
            return 0;
        }

        using var vddManager = new VirtualDisplayManager();
        using var duplicator = new DesktopDuplicator(config.AdapterIndex, config.CaptureTimeoutMs);
        IVideoEncoder encoder = new HardwareEncoder();
        using var adbManager = new AdbManager();
        using var server = new StreamServer(config.Port);
        using var usbTransport = new UsbTransport();

        var lz4 = new Lz4Compressor();
        var analyzer = new FrameAnalyzer(config.MotionThreshold);
        var packetWriter = new PacketWriter();
        var streamSettings = new StreamSettings(
            config.Bitrate / 1_000_000,
            config.RefreshRate,
            100,
            ControlPacket.EncoderAuto,
            false);

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
            nvencAvailable = encoder.Initialize(duplicator.Width, duplicator.Height, config.RefreshRate, config.Bitrate);
            if (nvencAvailable && encoder is HardwareEncoder hw)
            {
                nvencAvailable = hw.OpenDevice(duplicator.Device!.NativePointer);
            }
            
            if (nvencAvailable)
            {
                Console.WriteLine("[ENC] Direct NVENC hardware encoder active");
            }
            else
            {
                Console.WriteLine("[ENC] HardwareEncoder initialization failed, falling back to ffmpeg.");
                encoder.Dispose();
                encoder = new FfmpegEncoder();
                nvencAvailable = encoder.Initialize(duplicator.Width, duplicator.Height, config.RefreshRate, config.Bitrate);
            }
        }
        else
        {
            Console.WriteLine("[ENC] LZ4-only mode — hardware encoding disabled");
        }

        Console.WriteLine("[LZ4] LZ4 compressor ready");
        Console.WriteLine();

        // ─── Step 4 & 5: Transport and Streaming ──────────────────────
        Console.WriteLine("═══ Step 4: Transport ═══");

        Stream? clientStream = null;
        bool usbActive = false;

        if (config.TransportMode == "aoap")
        {
            // ── AOAP Mode ─────────────────────────────────────────────
            Console.WriteLine("[AOAP] AOAP transport mode selected");
            Console.WriteLine("[AOAP] WARNING: This mode replaces your phone's USB driver.");
            Console.WriteLine("[AOAP] ADB and file transfer will NOT work while active.");
            Console.WriteLine("[AOAP] To revert: run with --revert-driver");
            Console.WriteLine();

            var driverManager = new AoapDriverManager();

            // Step 1: Detect phone VID/PID via WMI
            var detected = driverManager.DetectAndroidDeviceViaWmi();
            if (detected == null)
            {
                Console.Error.WriteLine("[AOAP] No Android device detected via WMI.");
                Console.Error.WriteLine("[AOAP] Connect your tablet via USB and try again.");
                return 1;
            }

            var (phoneVid, phonePid) = detected.Value;

            // Step 2: Install WinUSB for phone's normal VID/PID
            if (!driverManager.EnsureWinUsbDriverInstalled(phoneVid, phonePid, "Discap AOAP"))
            {
                Console.Error.WriteLine("[AOAP] Failed to install WinUSB driver for phone.");
                Console.Error.WriteLine("[AOAP] Ensure drivers/Zadig.exe exists.");
                return 1;
            }

            // Step 3: Install WinUSB for Google AOA PIDs
            if (!driverManager.EnsureAoaDriversInstalled())
            {
                Console.Error.WriteLine("[AOAP] Failed to install WinUSB for AOA PIDs.");
                return 1;
            }

            // Step 4: Wait a moment for driver to settle, then connect
            Console.WriteLine("[AOAP] Drivers ready. Waiting 2s for driver to settle...");
            await Task.Delay(2000);

            // Step 5: AOA handshake (existing UsbTransport logic)
            usbActive = usbTransport.TryConnect(20_000, isAoapMode: true); // 20s timeout for AOAP
            if (usbActive)
            {
                Console.WriteLine("[AOAP] AOAP bulk transfer active");
                clientStream = usbTransport.Stream;
            }
            else
            {
                Console.Error.WriteLine("[AOAP] AOA handshake failed after driver installation.");
                Console.Error.WriteLine("[AOAP] The phone may need to be unplugged and replugged.");
                Console.Error.WriteLine("[AOAP] Try running again — the driver is now installed.");
                return 1;
            }
        }
        else
        {
            // ── ADB Mode (default) ────────────────────────────────────
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
            
            Console.WriteLine("═══ Step 5: Streaming ═══");
            server.Start();
        }

        var cts = new CancellationTokenSource();
        uint sequenceNumber = 0;
        var streamStartTime = Stopwatch.GetTimestamp();

        while (_running)
        {
            if (!usbActive)
            {
                // Wait for ADB client to connect
                if (!await server.WaitForClientAsync(cts.Token))
                {
                    if (!_running) break;
                    await Task.Delay(1000);
                    continue;
                }
                clientStream = server.ClientStream;
            }
            else
            {
                // If using USB and it disconnected, break inner loop to try reconnecting
                if (clientStream == null) break;
            }

            Console.WriteLine("[STREAM] Starting capture loop...");
            Console.WriteLine("[STREAM] Press Ctrl+C to stop.");
            Console.WriteLine();

            // Spawn background task to read input events from the client
            var inputTask = Task.Run(() => HandleInput(clientStream!, duplicator.BoundsX, duplicator.BoundsY, duplicator.Width, duplicator.Height, streamSettings));

            // Reset counters for this session.
            sequenceNumber = 0;
            streamStartTime = Stopwatch.GetTimestamp();
            int fpsCounter = 0;
            long lastFpsTime = Stopwatch.GetTimestamp();
            int lz4Frames = 0;
            int nvencFrames = 0;
            long totalBytesSent = 0;
            long lastSentTicks = 0;
            encoder.ForceKeyFrame();

            int droppedFrames = 0;
            long totalEncodeTicks = 0;
            long totalSendTicks = 0;
            long loopIteration = 0;
            FrameBuffer? lastFrame = null; // used to resend when screen is static

            // ─── Capture loop ─────────────────────────────────────────
            bool isClientConnected() => usbActive ? usbTransport.IsConnected : server.IsClientConnected;
            while (_running && isClientConnected())
            {
                Console.WriteLine($"[LOOP] Iteration {++loopIteration} starting");
                if (encoder is HardwareEncoder hwIter) hwIter.DiagIteration = loopIteration;

                int fpsCap = streamSettings.FpsCap;
                if (fpsCap > 0 && lastSentTicks != 0)
                {
                    long minFrameTicks = Stopwatch.Frequency / fpsCap;
                    long elapsedSinceSend = Stopwatch.GetTimestamp() - lastSentTicks;
                    long remainingTicks = minFrameTicks - elapsedSinceSend;
                    if (remainingTicks > 0)
                    {
                        int delayMs = Math.Max(1, (int)(remainingTicks * 1000 / Stopwatch.Frequency));
                        await Task.Delay(delayMs);
                    }
                }

                // Capture next frame.
                // On timeout (static screen) DXGI returns null — reuse last frame so the
                // encoder pipeline keeps ticking rather than stalling indefinitely.
                Console.WriteLine($"[LOOP] {loopIteration}: waiting for AcquireNextFrame...");
                var newFrame = duplicator.AcquireNextFrame();
                Console.WriteLine($"[LOOP] {loopIteration}: AcquireNextFrame returned {(newFrame == null ? "null (timeout)" : "frame")}" );
                FrameBuffer? frame;
                bool isRepeatFrame;
                if (newFrame != null)
                {
                    lastFrame?.Dispose();
                    lastFrame = newFrame;
                    frame = newFrame;
                    isRepeatFrame = false;
                    Console.WriteLine($"[LOOP] {loopIteration}: new frame captured, dirtyArea={frame.TotalDirtyArea}");
                }
                else if (lastFrame != null && nvencAvailable && !config.ForceLz4Only)
                {
                    // Screen is static — feed last frame to NVENC so it emits inter frames.
                    frame = lastFrame;
                    isRepeatFrame = true;
                    Console.WriteLine($"[LOOP] {loopIteration}: timeout — resending last frame to keep encoder alive");
                }
                else
                {
                    Console.WriteLine($"[LOOP] {loopIteration}: timeout, no previous frame — skipping");
                    continue;
                }

                // For NVENC, always encode regardless of dirty-area — DXGI dirty rects are
                // unreliable and were silently killing the stream after the first IDR.
                // For LZ4-only mode, skip unchanged frames to save bandwidth.
                if (!isRepeatFrame && frame.TotalDirtyArea == 0 && (config.ForceLz4Only || !nvencAvailable))
                {
                    Console.WriteLine($"[LOOP] {loopIteration}: zero dirty area (LZ4 mode) — skipping");
                    continue;
                }
                
                // Determine encoding type based on motion analysis.
                var frameType = FrameType.LZ4; // Default
                byte[] compressedData;
                int compressedSize;
                float dirtyRatio = analyzer.ComputeDirtyRatio(frame);
                int encoderMode = streamSettings.EncoderMode;

                long encodeStartTicks = Stopwatch.GetTimestamp();

                // ALWAYS use NVENC when available. The old analyzer-based routing
                // was the root cause: DXGI dirty rects are often tiny even during
                // full-screen video, so motionThreshold was never met and every
                // frame went to LZ4 (6.78MB each!). H.264 produces ~100KB frames.
                if (nvencAvailable && !config.ForceLz4Only && encoderMode != ControlPacket.EncoderLz4)
                {
                    frameType = FrameType.NVENC;
                }

                // Compress or encode the frame.
                if (frameType == FrameType.NVENC && nvencAvailable)
                {
                    encoder.SetTargetBitrate(GetTargetBitrate(streamSettings.BitrateMbps, dirtyRatio, config.MotionThreshold));
                    Console.WriteLine($"[ENC] {loopIteration}: calling SubmitFrame (NvEncEncodePicture)...");
                    encoder.SubmitFrame(frame);
                    Console.WriteLine($"[ENC] {loopIteration}: SubmitFrame returned");

                    bool sentAny = false;
                    
                    // Wait up to 100ms for at least one NAL unit to arrive, then drain the queue of all immediately available NAL units.
                    while (encoder.TryGetNextPacket(out compressedData, out compressedSize, sentAny ? 0 : 100))
                    {
                        sentAny = true;
                        
                        long elapsedTicks = frame.TimestampTicks - streamStartTime;
                        long elapsedUs = elapsedTicks * 1_000_000 / Stopwatch.Frequency;
                        int originalSize = frame.Width * frame.Height * 4;
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

                        long sendStartTicks = Stopwatch.GetTimestamp();
                        try
                        {
                            Console.WriteLine($"[NET] Sending packet: magic=DCAP type={(int)header.FrameType} size={compressedSize}");
                            packetWriter.WritePacket(clientStream!, header, compressedData, 0, compressedSize);
                            long tSendEnd = Stopwatch.GetTimestamp();
                            
                            double encodeMs = (sendStartTicks - encodeStartTicks) * 1000.0 / Stopwatch.Frequency;
                            double sendMs = (tSendEnd - sendStartTicks) * 1000.0 / Stopwatch.Frequency;
                            
                            Console.WriteLine($"[TIMING] Capture: {frame.CaptureTimeMs:F2}ms | Convert: {frame.ConvertTimeMs:F2}ms | Encode: {encodeMs:F2}ms | Send: {sendMs:F2}ms");

                            totalBytesSent += PacketHeader.SIZE + compressedSize;
                            lastSentTicks = Stopwatch.GetTimestamp();
                            totalSendTicks += (lastSentTicks - sendStartTicks);
                        }
                        catch (Exception)
                        {
                            Console.Error.WriteLine("[STREAM] Write failed — client disconnected");
                            break;
                        }
                    }

                    if (!sentAny) droppedFrames++;
                    else nvencFrames++;
                    
                    long encodeTicks = Stopwatch.GetTimestamp() - encodeStartTicks;
                    totalEncodeTicks += encodeTicks;
                    
                    fpsCounter++;
                    continue; // Skip the LZ4 packet sending logic below
                }
                
                // Fallback to LZ4
                frameType = FrameType.LZ4;
                var tightPixels = frame.GetTightPixels();
                compressedSize = lz4.Compress(tightPixels, out compressedData);
                lz4Frames++;

                long lz4EncodeTicks = Stopwatch.GetTimestamp() - encodeStartTicks;
                totalEncodeTicks += lz4EncodeTicks;

                long lz4ElapsedTicks = frame.TimestampTicks - streamStartTime;
                long lz4ElapsedUs = lz4ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
                int lz4OriginalSize = frame.Width * frame.Height * 4;
                ushort lz4Flags = sequenceNumber == 0 ? PacketHeader.FLAG_KEYFRAME : (ushort)0;

                var lz4Header = PacketHeader.Create(
                    frameType,
                    (ushort)frame.Width,
                    (ushort)frame.Height,
                    (uint)lz4OriginalSize,
                    (uint)compressedSize,
                    lz4ElapsedUs,
                    sequenceNumber++,
                    lz4Flags);

                long lz4SendStartTicks = Stopwatch.GetTimestamp();
                try
                {
                    packetWriter.WritePacket(clientStream!, lz4Header, compressedData, 0, compressedSize);
                    totalBytesSent += PacketHeader.SIZE + compressedSize;
                    lastSentTicks = Stopwatch.GetTimestamp();
                    totalSendTicks += (lastSentTicks - lz4SendStartTicks);
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
                    double avgFrameKb = totalBytesSent / 1024.0 / Math.Max(1, fpsCounter);
                    double avgEncodeMs = totalEncodeTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, fpsCounter);
                    double avgSendMs = totalSendTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, fpsCounter);
                    string encName = nvencFrames > lz4Frames ? "NVENC" : "LZ4";

                    // The exact STATS format requested
                    Console.WriteLine($"[STATS] FPS: {fps:F0} | Encoder: {encName} | Avg frame: {avgFrameKb:F0}KB | Encode time: {avgEncodeMs:F1}ms | Send time: {avgSendMs:F1}ms | Dropped: {droppedFrames} | Net: {mbps:F1} Mbps");

                    fpsCounter = 0;
                    lastFpsTime = now;
                    lz4Frames = 0;
                    nvencFrames = 0;
                    totalBytesSent = 0;
                    droppedFrames = 0;
                    totalEncodeTicks = 0;
                    totalSendTicks = 0;
                }
            }

            lastFrame?.Dispose();
            lastFrame = null;

            Console.WriteLine();
            Console.WriteLine("[STREAM] Session ended.");
            Console.WriteLine();

            if (_running)
            {
                Console.WriteLine("[NET] Waiting for reconnection...");
            }
        }

        // ─── Clean shutdown ───────────────────────────────────────────
        encoder.Dispose();
        Console.WriteLine();
        Console.WriteLine("═══ Shutting down ═══");
        cts.Cancel();

        return 0;
    }

    private static int GetTargetBitrate(int requestedMbps, float dirtyRatio, float motionThreshold)
    {
        int requested = Math.Clamp(requestedMbps, 5, 50) * 1_000_000;
        return dirtyRatio >= motionThreshold ? requested : Math.Min(requested, 8_000_000);
    }

    private static void HandleInput(Stream stream, int boundsX, int boundsY, int width, int height, StreamSettings settings)
    {
        byte[] buffer = new byte[InputPacket.SIZE];
        try
        {
            while (true)
            {
                stream.ReadExactly(buffer, 0, InputPacket.SIZE);
                if (InputPacket.TryReadFrom(buffer, out var packet))
                {
                    MouseInjector.ProcessInput(packet, boundsX, boundsY, width, height);
                }
                else if (ControlPacket.TryReadFrom(buffer, out var control))
                {
                    settings.Update(control);
                    Console.WriteLine($"\n[CFG] Client settings: {settings.BitrateMbps}Mbps, {settings.FpsCap}fps, {settings.ResolutionScale}%, mode={settings.EncoderMode}, stats={settings.ShowStats}");
                }
            }
        }
        catch
        {
            // Client disconnected or stream closed
        }
    }

    private sealed class StreamSettings
    {
        private volatile int _bitrateMbps;
        private volatile int _fpsCap;
        private volatile int _resolutionScale;
        private volatile int _encoderMode;
        private volatile bool _showStats;

        public StreamSettings(int bitrateMbps, int fpsCap, int resolutionScale, int encoderMode, bool showStats)
        {
            _bitrateMbps = Math.Clamp(bitrateMbps, 5, 50);
            _fpsCap = NormalizeFps(fpsCap);
            _resolutionScale = NormalizeScale(resolutionScale);
            _encoderMode = NormalizeEncoderMode(encoderMode);
            _showStats = showStats;
        }

        public int BitrateMbps => _bitrateMbps;
        public int FpsCap => _fpsCap;
        public int ResolutionScale => _resolutionScale;
        public int EncoderMode => _encoderMode;
        public bool ShowStats => _showStats;

        public void Update(ControlPacket packet)
        {
            _bitrateMbps = Math.Clamp(packet.BitrateMbps, (byte)5, (byte)50);
            _fpsCap = NormalizeFps(packet.FpsCap);
            _resolutionScale = NormalizeScale(packet.ResolutionScale);
            _encoderMode = NormalizeEncoderMode(packet.EncoderMode);
            _showStats = packet.ShowStats != 0;
        }

        private static int NormalizeFps(int fps) => fps switch
        {
            30 => 30,
            120 => 120,
            _ => 60
        };

        private static int NormalizeScale(int scale) => scale switch
        {
            50 => 50,
            75 => 75,
            _ => 100
        };

        private static int NormalizeEncoderMode(int mode) => mode switch
        {
            ControlPacket.EncoderH264 => ControlPacket.EncoderH264,
            ControlPacket.EncoderLz4 => ControlPacket.EncoderLz4,
            _ => ControlPacket.EncoderAuto
        };
    }
}
