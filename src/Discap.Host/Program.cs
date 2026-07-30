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
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct Win32Point
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Win32Point lpPoint);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint uMilliseconds);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint uMilliseconds);

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

        // Enable Per-Monitor DPI Awareness so Windows Win32 coordinate calls use physical pixels.
        MouseInjector.EnableDpiAwareness();

        // Enable 1.0ms high-precision Windows OS timer resolution for smooth sub-millisecond pacing.
        TimeBeginPeriod(1);
        Console.WriteLine("[SYS] Set Windows OS timer resolution to 1.0ms (timeBeginPeriod).");

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
        IVideoEncoder? encoder = null;
        using var adbManager = new AdbManager();
        using var server = new StreamServer(config.Port);
        using var usbTransport = new UsbTransport();

        var lz4 = new Lz4Compressor();
        var analyzer = new FrameAnalyzer(config.MotionThreshold);
        var packetWriter = new PacketWriter();
        var streamSettings = new StreamSettings(
            config.Bitrate / 1_000_000,
            0,  // Native — no FPS cap, matches display refresh rate
            100,
            ControlPacket.EncoderAuto,
            false,
            28); // Default quality = 28

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

        bool nvencAvailable = !config.ForceLz4Only;
        if (config.ForceLz4Only)
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
            bool isAlreadyAoa = phoneVid == 0x18D1 && (phonePid == 0x2D00 || phonePid == 0x2D01);

            // Step 2: Install WinUSB for phone's normal VID/PID
            if (!isAlreadyAoa)
            {
                if (!driverManager.EnsureWinUsbDriverInstalled(phoneVid, phonePid, "Discap AOAP"))
                {
                    Console.Error.WriteLine("[AOAP] Failed to install WinUSB driver for phone.");
                    Console.Error.WriteLine("[AOAP] Ensure drivers/Zadig.exe exists.");
                    return 1;
                }
            }

            // Step 3: Install WinUSB for Google AOA PIDs
            if (isAlreadyAoa)
            {
                Console.WriteLine($"[AOAP] Device is in AOA mode. Verifying WinUSB driver is active for PID=0x{phonePid:X4}...");
                bool isDriverValid = false;
                
                var guidsToTry = new HashSet<Guid> { WinUsbDevice.GUID_DEVINTERFACE_USB_DEVICE };
                var customGuids = WinUsbDevice.GetDeviceInterfaceGuids(phoneVid, phonePid, 0);
                foreach (var g in customGuids) guidsToTry.Add(g);

                var devices = new List<WinUsbDevice>();
                foreach (var guid in guidsToTry)
                {
                    devices.AddRange(WinUsbDevice.EnumerateDevices(guid));
                }

                foreach (var dev in devices)
                {
                    if (!isDriverValid && dev.Vid == phoneVid && dev.Pid == phonePid)
                    {
                        Console.WriteLine($"[AOAP]   Live Check Candidate: VID=0x{dev.Vid:X4} PID=0x{dev.Pid:X4} MI={(dev.Mi.HasValue ? dev.Mi.Value.ToString("X2") : "none")} Path={dev.DevicePath}");
                        
                        // We strictly want MI_00 if it exists, or no MI if simple device
                        if (dev.Mi == 0 || !dev.Mi.HasValue)
                        {
                            if (dev.Open())
                            {
                                isDriverValid = true;
                            }
                        }
                    }
                    dev.Dispose();
                }

                if (!isDriverValid)
                {
                    Console.Error.WriteLine("[AOAP] Live check failed! WinUSB is NOT currently bound to this AOA PID.");
                    if (!driverManager.EnsureWinUsbDriverInstalled(phoneVid, phonePid, "Discap AOA Mode", forceZadig: true))
                    {
                        Console.Error.WriteLine("[AOAP] Failed to force-install WinUSB for AOA PID.");
                        return 1;
                    }
                }
                else
                {
                    Console.WriteLine("[AOAP] Live check passed! WinUSB is correctly bound.");
                }
            }
            else
            {
                if (!driverManager.EnsureAoaDriversInstalled())
                {
                    Console.Error.WriteLine("[AOAP] Failed to install WinUSB for AOA PIDs.");
                    return 1;
                }
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
            TouchInjector.Initialize();
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

            // Initialize/recreate encoder for this session's resolution
            encoder?.Dispose();
            encoder = null;
            if (!config.ForceLz4Only)
            {
                var hw = new HardwareEncoder();
                encoder = hw;
                nvencAvailable = hw.Initialize(duplicator.Width, duplicator.Height, duplicator.CurrentRefreshRate, config.Bitrate);
                if (nvencAvailable)
                {
                    nvencAvailable = hw.OpenDevice(duplicator.Device!.NativePointer, config.RcMode);
                }
                
                if (nvencAvailable)
                {
                    Console.WriteLine($"[ENC] Direct NVENC hardware encoder active ({duplicator.Width}x{duplicator.Height})");
                }
                else
                {
                    Console.WriteLine("[ENC] HardwareEncoder initialization failed, falling back to ffmpeg.");
                    hw.Dispose();
                    var ff = new FfmpegEncoder();
                    encoder = ff;
                    nvencAvailable = ff.Initialize(duplicator.Width, duplicator.Height, duplicator.CurrentRefreshRate, config.Bitrate);
                }
            }

            Console.WriteLine("[STREAM] Starting capture loop...");
            Console.WriteLine("[STREAM] Press Ctrl+C to stop.");
            Console.WriteLine();

            // Spawn background task to read input events from the client
            var inputTask = Task.Run(() => HandleInput(clientStream!, duplicator, streamSettings, encoder));

            // Reset counters for this session.
            sequenceNumber = 0;
            streamStartTime = Stopwatch.GetTimestamp();
            int fpsCounter = 0;
            long lastFpsTime = Stopwatch.GetTimestamp();
            int lz4Frames = 0;
            int nvencFrames = 0;
            int nalCounter = 0;
            long totalBytesSent = 0;
            long nextFrameDueTicks = 0;
            long lastLoopTicks = streamStartTime;
            long totalLoopIntervalTicks = 0;
            encoder.ForceKeyFrame();

            int droppedFrames = 0;
            long totalEncodeTicks = 0;
            long totalSendTicks = 0;
            long loopIteration = 0;
            FrameBuffer? lastFrame = null; // used to resend when screen is static
            bool wasIdle = false;
            long resumeTimeTicks = 0;
            int postIdleFrameCount = 0;
            int lastSetBitrate = -1;
            int lastSetFps = -1;
            int lastSetQuality = -1;
            var dirtyRatioHistory = new Queue<float>();
            long lastReconfigureTicks = 0;
            long lastRepeatFrameTicks = 0;

            // Idle resume & diagnostic metrics tracking
            long lastAcquireSuccessTicks = 0;
            double steadyAcquireMs = 0;
            double steadyReadbackMs = 0;
            double steadyEncodeMs = 0;
            double steadyAccumulatedFrames = 0;
            int steadySampleCount = 0;

            // GPU keep-alive: throttled to every ~500ms during idle to prevent
            // GPU power-state downclocking that causes AcquireNextFrame spikes.
            long lastGpuKeepAliveTicks = 0;
            long gpuKeepAliveIntervalTicks = Stopwatch.Frequency / 2; // 500ms





            var streamLock = new object();
            bool isClientConnected() => usbActive ? usbTransport.IsConnected : server.IsClientConnected;

            // Spawn background task for true out-of-band cursor polling (~500Hz)
            var cursorTask = Task.Run(() =>
            {
                int lastSentCursorX = -10000;
                int lastSentCursorY = -10000;
                bool lastSentCursorVisible = true;
                bool isCursorHiddenByTouch = false;
                byte[]? cachedShapeBuffer = null;
                uint cachedShapeType = 0;

                while (_running && isClientConnected())
                {
                    try
                    {
                        if (clientStream != null && clientStream.CanWrite)
                        {
                            GetCursorPos(out var globalPoint);
                            int relX = globalPoint.X - duplicator.BoundsX;
                            int relY = globalPoint.Y - duplicator.BoundsY;
                            bool isTouchActiveNow = TouchInjector.IsTouchActive;
                            bool mouseMoved = (relX != lastSentCursorX || relY != lastSentCursorY);

                            if (isTouchActiveNow)
                            {
                                isCursorHiddenByTouch = true;
                            }
                            else if (!isTouchActiveNow && isCursorHiddenByTouch && mouseMoved)
                            {
                                isCursorHiddenByTouch = false;
                            }

                            bool isMouseOnStreamedDisplay = TouchInjector.IsCursorOnStreamedDisplay(globalPoint.X, globalPoint.Y);
                            bool isCursorVisible = isMouseOnStreamedDisplay && !isCursorHiddenByTouch;

                            // Send Position Packet (Type 3) - Only send when position moves while visible, or when visibility state toggles
                            bool visibilityToggled = (isCursorVisible != lastSentCursorVisible);
                            bool shouldSendPos = (mouseMoved && isCursorVisible) || visibilityToggled;

                            if (shouldSendPos)
                            {
                                lastSentCursorX = relX;
                                lastSentCursorY = relY;
                                lastSentCursorVisible = isCursorVisible;

                                var posPayload = CursorPackets.SerializeCursorPos(relX, relY, isCursorVisible);
                                long elapsedTicks = Stopwatch.GetTimestamp() - streamStartTime;
                                long elapsedUs = elapsedTicks * 1_000_000 / Stopwatch.Frequency;

                                lock (streamLock)
                                {
                                    uint seq = sequenceNumber++;
                                    var posHeader = PacketHeader.Create(
                                        FrameType.CursorPos,
                                        0, 0,
                                        (uint)posPayload.Length, (uint)posPayload.Length,
                                        elapsedUs,
                                        seq,
                                        0);
                                    packetWriter.WritePacket(clientStream!, posHeader, posPayload, 0, posPayload.Length);
                                }
                            }

                            // Send Shape Packet (Type 4) - Only process and transmit shape updates when cursor is visible
                            if (isCursorVisible)
                            {
                                var currentShapeInfo = duplicator.PointerShapeInfo;
                                var currentShapeBuffer = duplicator.PointerShapeBuffer;
                                if (currentShapeInfo.Width > 0 && currentShapeInfo.Height > 0 && currentShapeBuffer.Length > 0)
                                {
                                    int actualSize = (int)(currentShapeInfo.Height * currentShapeInfo.Pitch);
                                    if (actualSize <= 0 || actualSize > currentShapeBuffer.Length)
                                        actualSize = currentShapeBuffer.Length;
                                    var activeBuffer = currentShapeBuffer.AsSpan(0, actualSize);

                                    bool shapeChanged = cachedShapeBuffer == null ||
                                                         cachedShapeType != currentShapeInfo.Type ||
                                                         !cachedShapeBuffer.AsSpan().SequenceEqual(activeBuffer);

                                    if (shapeChanged)
                                    {
                                        cachedShapeBuffer = activeBuffer.ToArray();
                                        cachedShapeType = currentShapeInfo.Type;

                                        var shapePayload = CursorPackets.SerializeCursorShape(
                                            currentShapeInfo.Type,
                                            currentShapeInfo.Width,
                                            currentShapeInfo.Height,
                                            currentShapeInfo.Pitch,
                                            currentShapeInfo.HotSpot.X,
                                            currentShapeInfo.HotSpot.Y,
                                            activeBuffer);

                                        long elapsedTicks = Stopwatch.GetTimestamp() - streamStartTime;
                                        long elapsedUs = elapsedTicks * 1_000_000 / Stopwatch.Frequency;

                                        lock (streamLock)
                                        {
                                            uint seq = sequenceNumber++;
                                            var shapeHeader = PacketHeader.Create(
                                                FrameType.CursorShape,
                                                (ushort)currentShapeInfo.Width,
                                                (ushort)currentShapeInfo.Height,
                                                (uint)shapePayload.Length, (uint)shapePayload.Length,
                                                elapsedUs,
                                                seq,
                                                0);
                                            packetWriter.WritePacket(clientStream!, shapeHeader, shapePayload, 0, shapePayload.Length);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore cursor stream write errors — client disconnection handled by main video loop
                    }

                    Thread.Sleep(2); // ~500Hz polling rate
                }
            });

            // ─── Single-Slot Atomic Frame Bundle & Send Worker ─────────
            // Instead of a deep queue (which adds latency), we use a single
            // slot that always holds the FRESHEST complete frame bundle.
            // All NALs from one encode are grouped into one FrameBundle.
            var sendSignal = new System.Threading.ManualResetEventSlim(false);
            (PacketHeader Header, byte[] Payload)[]? _pendingFrame = null;

            void EnqueueFrameBundle((PacketHeader Header, byte[] Payload)[] bundle)
            {
                System.Threading.Interlocked.Exchange(ref _pendingFrame, bundle);
                sendSignal.Set();
            }

            var sendWorkerThread = new Thread(() =>
            {
                Console.WriteLine("[SEND-WORKER] Single-slot atomic send worker started.");
                while (_running && isClientConnected())
                {
                    try
                    {
                        // Atomically grab the pending frame (and clear the slot)
                        var frame = System.Threading.Interlocked.Exchange(ref _pendingFrame, null);
                        if (frame != null)
                        {
                            if (clientStream == null || !clientStream.CanWrite) break;
                            lock (streamLock)
                            {
                                long sendStartTicks = Stopwatch.GetTimestamp();
                                try
                                {
                                    // Combine all NAL payloads in the bundle into a single Access Unit
                                    int totalPayloadLength = 0;
                                    foreach (var nal in frame) totalPayloadLength += nal.Payload.Length;

                                    byte[] combinedPayload = new byte[totalPayloadLength];
                                    int offset = 0;
                                    foreach (var nal in frame)
                                    {
                                        Buffer.BlockCopy(nal.Payload, 0, combinedPayload, offset, nal.Payload.Length);
                                        offset += nal.Payload.Length;
                                    }

                                    var firstHeader = frame[0].Header;
                                    var combinedHeader = PacketHeader.Create(
                                        firstHeader.FrameType,
                                        firstHeader.Width,
                                        firstHeader.Height,
                                        firstHeader.OriginalSize,
                                        (uint)totalPayloadLength,
                                        firstHeader.Timestamp,
                                        firstHeader.SequenceNumber,
                                        firstHeader.Flags);

                                    packetWriter.WritePacket(clientStream!, combinedHeader, combinedPayload, 0, combinedPayload.Length);
                                    totalBytesSent += PacketHeader.SIZE + combinedPayload.Length;

                                    long tSendEnd = Stopwatch.GetTimestamp();
                                    double sendMs = (tSendEnd - sendStartTicks) * 1000.0 / Stopwatch.Frequency;
                                    totalSendTicks += (tSendEnd - sendStartTicks);
                                    nvencFrames++;
                                }
                                catch (Exception)
                                {
                                    Console.Error.WriteLine("[STREAM] Write failed — client disconnected");
                                    break;
                                }
                            }
                        }
                        else
                        {
                            // Nothing pending — wait for signal (timeout prevents permanent deadlock)
                            sendSignal.Wait(50);
                            sendSignal.Reset();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[SEND-WORKER] Error: {ex.Message}");
                    }
                }
                Console.WriteLine("[SEND-WORKER] Worker thread exiting.");
            });
            sendWorkerThread.IsBackground = true;
            sendWorkerThread.Name = "DisCap-SendWorker";
            sendWorkerThread.Start();


            // ─── Capture loop ─────────────────────────────────────────
            while (_running && isClientConnected())
            {
                try
                {
                    ++loopIteration;
                    if (encoder is HardwareEncoder hwIter) hwIter.DiagIteration = loopIteration;

                    if (loopIteration % 10 == 0)
                    {
                        duplicator.GpuKeepAlive();
                    }

                    int fpsCap = streamSettings.FpsCap;
                    // When Native (0), pace to the display's actual refresh rate
                    int effectivePacingFps = fpsCap > 0 ? fpsCap : duplicator.CurrentRefreshRate;
                    long currentLoopTicks = Stopwatch.GetTimestamp();
                    if (loopIteration > 1)
                    {
                        totalLoopIntervalTicks += (currentLoopTicks - lastLoopTicks);
                    }
                    lastLoopTicks = currentLoopTicks;
                    long currentCaptureTicks = Stopwatch.GetTimestamp();

                    if (effectivePacingFps > 0 && nextFrameDueTicks != 0)
                    {
                        if (currentCaptureTicks < nextFrameDueTicks - (Stopwatch.Frequency / 1000))
                        {
                            Thread.Sleep(1); continue;
                        }
                    }
                    
                    if (effectivePacingFps > 0)
                    {
                        long minFrameTicks = Stopwatch.Frequency / effectivePacingFps;
                        if (nextFrameDueTicks == 0 || currentCaptureTicks > nextFrameDueTicks + (minFrameTicks / 2))
                            nextFrameDueTicks = currentCaptureTicks + minFrameTicks;
                        else
                            nextFrameDueTicks += minFrameTicks;
                    }


                // Capture next frame.
                // On timeout (static screen) DXGI returns null — reuse last frame so the
                // encoder pipeline keeps ticking rather than stalling indefinitely.
                var newFrame = duplicator.AcquireNextFrame();

                FrameBuffer? frame;
                bool isRepeatFrame;
                bool isIdleResumeFrame = false;
                double timeSinceLastAcquiredFrameMs = 0;

                if (newFrame != null)
                {
                    long nowTicks = Stopwatch.GetTimestamp();
                    timeSinceLastAcquiredFrameMs = lastAcquireSuccessTicks == 0 ? 0 : (nowTicks - lastAcquireSuccessTicks) * 1000.0 / Stopwatch.Frequency;

                    const double IDLE_GAP_THRESHOLD_MS = 500.0;

                    if (lastAcquireSuccessTicks != 0 && timeSinceLastAcquiredFrameMs >= IDLE_GAP_THRESHOLD_MS)
                    {
                        lastAcquireSuccessTicks = nowTicks;
                        newFrame.Dispose();
                        continue;
                    }

                    lastFrame?.Dispose();
                    lastFrame = newFrame;
                    frame = newFrame;
                    isRepeatFrame = false;
                    lastAcquireSuccessTicks = nowTicks;

                    if (wasIdle)
                    {
                        wasIdle = false;
                        resumeTimeTicks = Stopwatch.GetTimestamp();
                        postIdleFrameCount = 0;
                    }
                }
                else
                {
                    // Screen is static — timeout on AcquireNextFrame.
                    // Send a light 10 FPS keep-alive repeat frame (every 100ms) to keep MediaCodec active
                    // without clogging the network socket or causing latency spikes on motion resume.
                    long nowTicks = Stopwatch.GetTimestamp();
                    bool shouldSendKeepAlive = (lastRepeatFrameTicks == 0) || 
                        ((nowTicks - lastRepeatFrameTicks) * 1000.0 / Stopwatch.Frequency >= 100.0);

                    if (lastFrame != null && shouldSendKeepAlive)
                    {
                        frame = lastFrame;
                        isRepeatFrame = true;
                        lastRepeatFrameTicks = nowTicks;
                    }
                    else
                    {
                        Thread.Sleep(1);
                        continue;
                    }
                }

                // Increment frame counter if we are in the active diagnostic window
                if (resumeTimeTicks != 0)
                {
                    double msSinceResume = (Stopwatch.GetTimestamp() - resumeTimeTicks) * 1000.0 / Stopwatch.Frequency;
                    if (msSinceResume <= 3000.0)
                    {
                        postIdleFrameCount++;
                    }
                    else
                    {
                        resumeTimeTicks = 0;
                    }
                }

                // Check if resolution or refresh rate changed mid-stream (e.g. user changed OS settings)
                // Check if resolution or refresh rate changed mid-stream (e.g. user changed OS settings)
                if (nvencAvailable && (duplicator.Width != encoder.CurrentWidth || duplicator.Height != encoder.CurrentHeight || duplicator.CurrentRefreshRate != encoder.CurrentFrameRate))
                {
                    Console.WriteLine($"[ENC] Display change detected: {encoder.CurrentWidth}x{encoder.CurrentHeight}@{encoder.CurrentFrameRate}Hz -> {duplicator.Width}x{duplicator.Height}@{duplicator.CurrentRefreshRate}Hz");
                    Console.WriteLine("[ENC] Resolution/display changed. Disconnecting client to force a full stream recreation...");
                    server.DisconnectClient();
                    break;
                }

                // If encoder is unavailable (e.g. still initializing), skip frame
                if (nvencAvailable && !encoder.IsAvailable)
                {
                    continue;
                }

                // For NVENC, always encode regardless of dirty-area — DXGI dirty rects are
                // unreliable and were silently killing the stream after the first IDR.
                // For LZ4-only mode, skip unchanged frames to save bandwidth.
                if (!isRepeatFrame && frame.TotalDirtyArea == 0 && (config.ForceLz4Only || !nvencAvailable))
                {
                    continue;
                }
                
                // Determine encoding type based on motion analysis.
                var frameType = FrameType.LZ4; // Default
                byte[] compressedData;
                int compressedSize;
                float dirtyRatio = analyzer.ComputeDirtyRatio(frame);
                dirtyRatioHistory.Enqueue(dirtyRatio);
                if (dirtyRatioHistory.Count > 5)
                {
                    dirtyRatioHistory.Dequeue();
                }
                float avgDirtyRatio = 0f;
                foreach (var val in dirtyRatioHistory)
                {
                    avgDirtyRatio += val;
                }
                avgDirtyRatio /= Math.Max(1, dirtyRatioHistory.Count);

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
                    int effectiveFps = fpsCap > 0 ? fpsCap : duplicator.CurrentRefreshRate;
                    byte targetQuality = (byte)streamSettings.TargetQuality;
                    long currentTicks = Stopwatch.GetTimestamp();

                    int targetBitrate = GetTargetBitrate(streamSettings.BitrateMbps, avgDirtyRatio, config.MotionThreshold);
                    bool canReconfigure = (lastReconfigureTicks == 0) || (currentTicks - lastReconfigureTicks >= 3 * Stopwatch.Frequency);

                    bool initialSetupTrigger = lastSetBitrate == -1;
                    bool fpsChangeTrigger = lastSetFps != effectiveFps;
                    bool qualityChangeTrigger = lastSetQuality != targetQuality;
                    bool bitrateSwingTrigger = Math.Abs(targetBitrate - lastSetBitrate) > lastSetBitrate * 0.50;

                        if (canReconfigure && (initialSetupTrigger || fpsChangeTrigger || qualityChangeTrigger || bitrateSwingTrigger))
                        {
                            string reason = initialSetupTrigger ? "Initial Encoder Setup" :
                                            fpsChangeTrigger ? $"FPS Cap Change (Old: {lastSetFps}, New: {effectiveFps})" :
                                            qualityChangeTrigger ? $"CQ Target Quality Change (Old: {lastSetQuality}, New: {targetQuality})" :
                                            $"Bitrate Swing >50% (Old: {lastSetBitrate / 1_000_000}Mbps, New: {targetBitrate / 1_000_000}Mbps, avgDirtyRatio: {avgDirtyRatio:F4}, threshold: {config.MotionThreshold:F4})";

                            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
                            Console.WriteLine($"[RECONFIGURE-DIAG] [{ts}] RECONFIGURE TRIGGER: {reason} | TargetBitrate: {targetBitrate / 1_000_000}Mbps | ForceIDR: true");
                            encoder.Reconfigure(targetBitrate, effectiveFps, targetQuality);
                            lastSetBitrate = targetBitrate;
                            lastSetFps = effectiveFps;
                            lastSetQuality = targetQuality;
                            lastReconfigureTicks = currentTicks;
                        }
                    encoder.SubmitFrame(frame);

                    bool sentAny = false;
                    var nalBundle = new System.Collections.Generic.List<(PacketHeader Header, byte[] Payload)>();

                    while (encoder.TryGetNextPacket(out compressedData, out compressedSize, sentAny ? 0 : 50))
                    {
                        sentAny = true;
                        nalCounter++;

                        long elapsedTicks = Stopwatch.GetTimestamp() - streamStartTime;
                        long elapsedUs = elapsedTicks * 1_000_000 / Stopwatch.Frequency;
                        uint seq = sequenceNumber++;
                        ushort flags = seq == 0 ? PacketHeader.FLAG_KEYFRAME : (ushort)0;

                        var header = PacketHeader.Create(
                            FrameType.NVENC,
                            (ushort)duplicator.Width,
                            (ushort)duplicator.Height,
                            (uint)(duplicator.Width * duplicator.Height * 4),
                            (uint)compressedSize,
                            elapsedUs,
                            seq,
                            flags);

                        byte[] nalCopy = new byte[compressedSize];
                        Array.Copy(compressedData, nalCopy, compressedSize);
                        nalBundle.Add((header, nalCopy));
                    }

                    if (nalBundle.Count > 0)
                    {
                        EnqueueFrameBundle(nalBundle.ToArray());
                    }


                    long encodeTicks = Stopwatch.GetTimestamp() - encodeStartTicks;
                    totalEncodeTicks += encodeTicks;
                    
                    fpsCounter++;
                    continue; // NALs enqueued to sendQueue, sendWorkerThread handles network transport
                }
                
                // Fallback to LZ4
                frameType = FrameType.LZ4;
                var tightPixels = frame.GetTightPixels();
                compressedSize = lz4.Compress(tightPixels, out compressedData);
                lz4Frames++;

                long lz4EncodeTicks = Stopwatch.GetTimestamp() - encodeStartTicks;
                totalEncodeTicks += lz4EncodeTicks;

                lock (streamLock)
                {
                    long lz4ElapsedTicks = Stopwatch.GetTimestamp() - streamStartTime;
                    long lz4ElapsedUs = lz4ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
                    int lz4OriginalSize = frame.Width * frame.Height * 4;
                    uint lz4Seq = sequenceNumber++;
                    ushort lz4Flags = lz4Seq == 0 ? PacketHeader.FLAG_KEYFRAME : (ushort)0;

                    var lz4Header = PacketHeader.Create(
                        frameType,
                        (ushort)frame.Width,
                        (ushort)frame.Height,
                        (uint)lz4OriginalSize,
                        (uint)compressedSize,
                        lz4ElapsedUs,
                        lz4Seq,
                        lz4Flags);

                    long lz4SendStartTicks = Stopwatch.GetTimestamp();
                    try
                    {
                        packetWriter.WritePacket(clientStream!, lz4Header, compressedData, 0, compressedSize);
                        long tSendEnd = Stopwatch.GetTimestamp();
                        totalBytesSent += PacketHeader.SIZE + compressedSize;
                        totalSendTicks += (tSendEnd - lz4SendStartTicks);
                    }
                    catch (Exception)
                    {
                        Console.Error.WriteLine("[STREAM] Write failed — client disconnected");
                        break;
                    }
                }

                // FPS counter — update every second.
                fpsCounter++;
                long now = Stopwatch.GetTimestamp();
                long elapsed = now - lastFpsTime;
                if (elapsed >= Stopwatch.Frequency) // 1 second
                {
                    // Re-query OS refresh rate to detect midstream changes
                    duplicator.RefreshCurrentRefreshRate();
                    
                    double fps = fpsCounter * (double)Stopwatch.Frequency / elapsed;
                    double mbps = totalBytesSent * 8.0 / 1_000_000;
                    double avgFrameKb = totalBytesSent / 1024.0 / Math.Max(1, fpsCounter);
                    double avgEncodeMs = totalEncodeTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, fpsCounter);
                    double avgSendMs = totalSendTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, fpsCounter);
                    string encName = nvencFrames > lz4Frames ? "NVENC" : "LZ4";

                    // The exact STATS format requested
                    Console.WriteLine($"[STATS] FPS: {fps:F0} | Encoder: {encName} | Avg frame: {avgFrameKb:F0}KB | Encode time: {avgEncodeMs:F1}ms | Send time: {avgSendMs:F1}ms | Dropped: {droppedFrames} | Net: {mbps:F1} Mbps");

                    int currentFpsCap = streamSettings.FpsCap;
                    int currentEffectiveFps = currentFpsCap > 0 ? currentFpsCap : duplicator.CurrentRefreshRate;
                    string fpsCapLabel = currentFpsCap == 0 ? $"Native ({duplicator.CurrentRefreshRate}Hz)" : $"{currentFpsCap}";
                    Console.WriteLine($"[FPS] Display refresh rate: {duplicator.CurrentRefreshRate}Hz | App FPS cap: {fpsCapLabel} | Effective pacing: {currentEffectiveFps}fps | Measured: {fps:F0}fps");
                    Console.WriteLine($"[ENC-STATS] Encoder Output FPS: {nvencFrames}");

                    double avgLoopMs = totalLoopIntervalTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, fpsCounter);
                    Console.WriteLine($"[PERF] Encode: {nalCounter} NAL/s | Transport write: {avgSendMs:F2}ms | Loop interval: {avgLoopMs:F2}ms | Target: {currentEffectiveFps}fps");

                    fpsCounter = 0;
                    lastFpsTime = now;
                    lz4Frames = 0;
                    nvencFrames = 0;
                    nalCounter = 0;
                    totalBytesSent = 0;
                    droppedFrames = 0;
                    totalEncodeTicks = 0;
                    totalSendTicks = 0;
                    totalLoopIntervalTicks = 0;
                }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FATAL ERROR] Loop iteration {loopIteration} crashed: {ex.Message}");
                    Console.WriteLine($"[FATAL ERROR] Stack Trace:\n{ex.StackTrace}");
                    break;
                }

                if (!isClientConnected())
                {
                    Console.WriteLine("[NET] Client disconnected. Exiting stream loop.");
                    break;
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
        TimeEndPeriod(1);
        Console.WriteLine();
        Console.WriteLine("═══ Shutting down ═══");
        cts.Cancel();

        return 0;
    }

    private static int GetTargetBitrate(int requestedMbps, float dirtyRatio, float motionThreshold)
    {
        if (requestedMbps >= 150)
        {
            return 150_000_000; // Uncapped (Safety ceiling of 150 Mbps)
        }
        return Math.Clamp(requestedMbps, 5, 100) * 1_000_000;
    }

    private static void HandleInput(Stream stream, DesktopDuplicator duplicator, StreamSettings settings, IVideoEncoder? encoder)
    {
        byte[] headerBuffer = new byte[4];
        try
        {
            while (true)
            {
                stream.ReadExactly(headerBuffer, 0, 4);
                uint magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer);

                if (magic == InputPacket.MAGIC) // "INPT"
                {
                    byte[] rest = new byte[8];
                    stream.ReadExactly(rest, 0, 8);
                    byte[] full = new byte[12];
                    Array.Copy(headerBuffer, 0, full, 0, 4);
                    Array.Copy(rest, 0, full, 4, 8);

                    if (InputPacket.TryReadFrom(full, out var packet))
                    {
                        MouseInjector.ProcessInput(packet, duplicator.BoundsX, duplicator.BoundsY, duplicator.Width, duplicator.Height);
                    }
                }
                else if (magic == MultiTouchPacket.MAGIC_MTCH) // "MTCH"
                {
                    int countByte = stream.ReadByte();
                    if (countByte < 0) break;
                    byte count = (byte)countByte;
                    int payloadSize = count * 10;
                    byte[] payload = new byte[5 + payloadSize];
                    Array.Copy(headerBuffer, 0, payload, 0, 4);
                    payload[4] = count;
                    if (payloadSize > 0)
                    {
                        stream.ReadExactly(payload, 5, payloadSize);
                    }

                    if (MultiTouchPacket.TryReadFrom(payload, out var mtPacket))
                    {
                        TouchInjector.ProcessMultiTouch(mtPacket, duplicator.BoundsX, duplicator.BoundsY, duplicator.Width, duplicator.Height);
                    }
                }
                else if (magic == ControlPacket.MAGIC) // "CTRL"
                {
                    byte[] rest = new byte[8];
                    stream.ReadExactly(rest, 0, 8);
                    byte[] full = new byte[12];
                    Array.Copy(headerBuffer, 0, full, 0, 4);
                    Array.Copy(rest, 0, full, 4, 8);

                    if (ControlPacket.TryReadFrom(full, out var control))
                    {
                        settings.Update(control);
                        Console.WriteLine($"\n[CFG] Client settings: {settings.BitrateMbps}Mbps, {settings.FpsCap}fps, {settings.ResolutionScale}%, mode={settings.EncoderMode}, stats={settings.ShowStats}, quality={settings.TargetQuality}");
                    }
                }
                else if (magic == 0x52494C50) // "PLIR"
                {
                    byte[] rest = new byte[8];
                    stream.ReadExactly(rest, 0, 8);
                    Console.WriteLine("\n[PLI] Received IDR request from Android client — forcing keyframe.");
                    encoder?.ForceKeyFrame();
                }
            }
        }
        catch
        {
            // Client disconnected or stream closed
        }
        finally
        {
            TouchInjector.FlushAllTouches();
        }
    }

    private sealed class StreamSettings
    {
        private volatile int _bitrateMbps;
        private volatile int _fpsCap;
        private volatile int _resolutionScale;
        private volatile int _encoderMode;
        private volatile bool _showStats;
        private volatile int _targetQuality;

        public StreamSettings(int bitrateMbps, int fpsCap, int resolutionScale, int encoderMode, bool showStats, int targetQuality)
        {
            _bitrateMbps = Math.Clamp(bitrateMbps, 5, 100);
            _fpsCap = NormalizeFps(fpsCap);
            _resolutionScale = NormalizeScale(resolutionScale);
            _encoderMode = NormalizeEncoderMode(encoderMode);
            _showStats = showStats;
            _targetQuality = Math.Clamp(targetQuality, 15, 40);
        }

        public int BitrateMbps => _bitrateMbps;
        public int FpsCap => _fpsCap;
        public int ResolutionScale => _resolutionScale;
        public int EncoderMode => _encoderMode;
        public bool ShowStats => _showStats;
        public int TargetQuality => _targetQuality;

        public void Update(ControlPacket packet)
        {
            _bitrateMbps = Math.Clamp(packet.BitrateMbps, (byte)5, (byte)100);
            _fpsCap = NormalizeFps(packet.FpsCap);
            _resolutionScale = NormalizeScale(packet.ResolutionScale);
            _encoderMode = NormalizeEncoderMode(packet.EncoderMode);
            _showStats = packet.ShowStats != 0;
            _targetQuality = Math.Clamp((int)packet.TargetQuality, 15, 40);
        }

        private static int NormalizeFps(int fps) => fps switch
        {
            0 => 0,    // Native — no cap, matches display refresh rate
            30 => 30,
            120 => 120,
            144 => 144,
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
