using System.Runtime.InteropServices;
using Discap.Host.Protocol;

namespace Discap.Host.Input;

public static class TouchInjector
{
    private const uint MAX_TOUCH_COUNT = 10;
    private const uint TOUCH_FEEDBACK_INDIRECT = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InitializeTouchInjection(uint maxCount = MAX_TOUCH_COUNT, uint dwMode = TOUCH_FEEDBACK_INDIRECT);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CopyCursor(IntPtr hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private const int IDC_ARROW = 32512;
    private const uint OCR_NORMAL = 32512;
    private const uint OCR_IBEAM = 32513;
    private const uint OCR_WAIT = 32514;
    private const uint OCR_CROSS = 32515;
    private const uint OCR_HAND = 32649;

    private const uint SPI_SETCURSORS = 0x0057;
    private const uint SPIF_SENDWININICHANGE = 0x02;

    private const uint POINTER_FLAG_NONE = 0x00000000;
    private const uint POINTER_FLAG_INRANGE = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint POINTER_FLAG_DOWN = 0x00010000;
    private const uint POINTER_FLAG_UPDATE = 0x00020000;
    private const uint POINTER_FLAG_UP = 0x00040000;

    private const uint TOUCH_FLAG_NONE = 0x00000000;
    private const uint TOUCH_MASK_NONE = 0x00000000;
    private const uint TOUCH_MASK_CONTACTAREA = 0x00000001;
    private const uint TOUCH_MASK_ORIENTATION = 0x00000002;
    private const uint TOUCH_MASK_PRESSURE = 0x00000004;

    private const int PT_TOUCH = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public uint InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    private static bool _initialized = false;
    private static IntPtr _hOriginalCursor = IntPtr.Zero;
    private static IntPtr _hBlankCursor = IntPtr.Zero;
    private static bool _isCursorHidden = false;
    private static bool _suppressHideUntilNextTouchDown = false;

    // Rule 1: ID Pool (IDs 1..10) strictly bounded by MAX_TOUCH_COUNT
    private static readonly Queue<uint> _idPool = new();
    private static readonly Dictionary<byte, uint> _idMap = new();
    private static readonly Dictionary<uint, byte> _reverseIdMap = new();
    private static readonly Dictionary<uint, PointerState> _shadowStates = new();
    private static readonly Dictionary<uint, POINT> _lastLocation = new();
    private static readonly object _lock = new();

    private static bool _watcherStarted = false;
    private static volatile int _lastWatcherX = 0;
    private static volatile int _lastWatcherY = 0;
    private static long _lastTouchTicks = 0;

    private enum PointerState { Up, Down, Move }

    public static bool IsCursorHidden => _isCursorHidden;

    public static void RestoreCursorIfHidden()
    {
        lock (_lock)
        {
            if (_isCursorHidden)
            {
                RestoreCursorInternal();
                _suppressHideUntilNextTouchDown = true;
            }
        }
    }

    public static void Initialize()
    {
        lock (_lock)
        {
            if (_initialized) return;

            // Reset and populate the Pointer ID Pool (1 to MAX_TOUCH_COUNT)
            _idPool.Clear();
            for (uint i = 1; i <= MAX_TOUCH_COUNT; i++)
            {
                _idPool.Enqueue(i);
            }
            _idMap.Clear();
            _reverseIdMap.Clear();
            _shadowStates.Clear();
            _lastLocation.Clear();

            try
            {
                _initialized = InitializeTouchInjection(MAX_TOUCH_COUNT, TOUCH_FEEDBACK_INDIRECT);
                if (!_initialized)
                {
                    _initialized = InitializeTouchInjection(MAX_TOUCH_COUNT, 0);
                }

                if (!_initialized)
                {
                    int err = Marshal.GetLastWin32Error();
                    Console.Error.WriteLine($"[TOUCH-INIT] InitializeTouchInjection failed. Win32 Error: {err}");
                }
                else
                {
                    Console.WriteLine($"[TOUCH-INIT] InitializeTouchInjection initialized successfully with pool 1..{MAX_TOUCH_COUNT}!");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[TOUCH-INIT] Exception during InitializeTouchInjection: {ex.Message}");
                _initialized = false;
            }

            try
            {
                IntPtr systemArrow = LoadCursor(IntPtr.Zero, IDC_ARROW);
                if (systemArrow != IntPtr.Zero)
                {
                    _hOriginalCursor = CopyCursor(systemArrow);
                }
                _hBlankCursor = CreateBlankCursor();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[TOUCH] Cursor initialization error: {ex.Message}");
            }

            StartPhysicalMouseWatcher();
        }
    }

    private static void StartPhysicalMouseWatcher()
    {
        if (_watcherStarted) return;
        _watcherStarted = true;

        Task.Run(async () =>
        {
            if (GetCursorPos(out var initPos))
            {
                _lastWatcherX = initPos.X;
                _lastWatcherY = initPos.Y;
            }

            while (true)
            {
                await Task.Delay(15); // ~60Hz polling decoupled from DXGI capture loop

                long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                long lastTouch = Volatile.Read(ref _lastTouchTicks);
                double msSinceTouch = (nowTicks - lastTouch) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                if (GetCursorPos(out var currentPos))
                {
                    // 100ms Suppression Window: Ignore OS touch-to-mouse promotion position jumps during and 100ms after touch
                    if (msSinceTouch < 100.0)
                    {
                        _lastWatcherX = currentPos.X;
                        _lastWatcherY = currentPos.Y;
                        continue;
                    }

                    // Physical mouse movement check (outside 100ms touch suppression window)
                    if (_isCursorHidden && (currentPos.X != _lastWatcherX || currentPos.Y != _lastWatcherY))
                    {
                        lock (_lock)
                        {
                            RestoreCursorInternal();
                            _suppressHideUntilNextTouchDown = true;
                        }
                        _lastWatcherX = currentPos.X;
                        _lastWatcherY = currentPos.Y;
                    }
                    else if (!_isCursorHidden)
                    {
                        _lastWatcherX = currentPos.X;
                        _lastWatcherY = currentPos.Y;
                    }
                }
            }
        });
    }

    private static int _streamBoundsX = 0;
    private static int _streamBoundsY = 0;
    private static int _streamWidth = 0;
    private static int _streamHeight = 0;

    public static bool IsCursorOnStreamedDisplay(int mouseX, int mouseY)
    {
        if (_streamWidth == 0 || _streamHeight == 0) return true;
        return mouseX >= _streamBoundsX &&
               mouseX < (_streamBoundsX + _streamWidth) &&
               mouseY >= _streamBoundsY &&
               mouseY < (_streamBoundsY + _streamHeight);
    }

    public static void ProcessMultiTouch(MultiTouchPacket packet, int boundsX, int boundsY, int width, int height)
    {
        if (!_initialized || packet.PointerCount == 0) return;

        _streamBoundsX = boundsX;
        _streamBoundsY = boundsY;
        _streamWidth = width;
        _streamHeight = height;

        // Stamp last touch timestamp using Volatile.Write to trigger 100ms motion watcher suppression
        Volatile.Write(ref _lastTouchTicks, System.Diagnostics.Stopwatch.GetTimestamp());

        lock (_lock)
        {
            var upContacts = new List<POINTER_TOUCH_INFO>();
            var activeContacts = new List<POINTER_TOUCH_INFO>();
            var winIdsToRecycle = new List<uint>();

            // 1. Implicit UP Sweep: Detect active pointers missing from incoming payload (dropped UP packet protection)
            var incomingAndroidIds = new HashSet<byte>();
            for (int i = 0; i < packet.PointerCount; i++)
            {
                incomingAndroidIds.Add(packet.Pointers[i].AndroidPointerId);
            }

            var missingAndroidIds = _idMap.Keys.Where(id => !incomingAndroidIds.Contains(id)).ToList();
            foreach (var missingId in missingAndroidIds)
            {
                if (_idMap.TryGetValue(missingId, out uint winId))
                {
                    _lastLocation.TryGetValue(winId, out var lastPt);
                    var touchInfo = new POINTER_TOUCH_INFO();
                    touchInfo.pointerInfo.pointerType = (uint)PT_TOUCH;
                    touchInfo.pointerInfo.pointerId = winId;
                    touchInfo.pointerInfo.ptPixelLocation = lastPt;
                    touchInfo.pointerInfo.pointerFlags = POINTER_FLAG_UP;
                    touchInfo.touchMask = TOUCH_MASK_NONE;
                    upContacts.Add(touchInfo);

                    _shadowStates[winId] = PointerState.Up;
                    winIdsToRecycle.Add(winId);
                }
            }

            // 2. Process incoming touch pointers
            for (int i = 0; i < packet.PointerCount; i++)
            {
                var record = packet.Pointers[i];

                int absX = Math.Clamp(boundsX + (int)((record.NormX / 65535.0f) * width), boundsX, boundsX + width - 1);
                int absY = Math.Clamp(boundsY + (int)((record.NormY / 65535.0f) * height), boundsY, boundsY + height - 1);
                uint pVal = (uint)((record.Pressure / 65535.0f) * 1024);

                // Action: 0=Down, 1=Move, 2=Up, 3=Cancel
                if (record.Action == 0) // DOWN
                {
                    _suppressHideUntilNextTouchDown = false; // New touch gesture enables hiding cursor again

                    if (!_idMap.TryGetValue(record.AndroidPointerId, out uint winId))
                    {
                        if (_idPool.Count == 0)
                        {
                            Console.WriteLine($"[TOUCH-WARN] Touch ID pool depleted (>{MAX_TOUCH_COUNT} touches). Dropping finger {record.AndroidPointerId}");
                            continue;
                        }
                        winId = _idPool.Dequeue();
                        _idMap[record.AndroidPointerId] = winId;
                        _reverseIdMap[winId] = record.AndroidPointerId;
                    }

                    _shadowStates.TryGetValue(winId, out var currentState);
                    if (currentState == PointerState.Down || currentState == PointerState.Move)
                    {
                        EmitSingleStateChange(winId, absX, absY, POINTER_FLAG_UP);
                    }

                    _lastLocation[winId] = new POINT { X = absX, Y = absY };

                    var touchInfo = new POINTER_TOUCH_INFO
                    {
                        touchFlags = TOUCH_FLAG_NONE,
                        touchMask = TOUCH_MASK_NONE,
                        orientation = 0,
                        pressure = pVal
                    };
                    touchInfo.pointerInfo.pointerType = (uint)PT_TOUCH;
                    touchInfo.pointerInfo.pointerId = winId;
                    touchInfo.pointerInfo.ptPixelLocation = new POINT { X = absX, Y = absY };
                    touchInfo.pointerInfo.pointerFlags = POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;

                    _shadowStates[winId] = PointerState.Down;
                    activeContacts.Add(touchInfo);
                }
                else if (record.Action == 1) // MOVE
                {
                    _suppressHideUntilNextTouchDown = false; // Active touch motion resets suppression flag

                    // Rule 3: Drop Stray Events for unmapped IDs silently (No synthetic DOWN)
                    if (!_idMap.TryGetValue(record.AndroidPointerId, out uint winId))
                    {
                        continue;
                    }

                    _shadowStates.TryGetValue(winId, out var currentState);
                    if (currentState == PointerState.Up)
                    {
                        EmitSingleStateChange(winId, absX, absY, POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT);
                    }

                    _lastLocation[winId] = new POINT { X = absX, Y = absY };

                    var touchInfo = new POINTER_TOUCH_INFO
                    {
                        touchFlags = TOUCH_FLAG_NONE,
                        touchMask = TOUCH_MASK_NONE,
                        orientation = 0,
                        pressure = pVal
                    };
                    touchInfo.pointerInfo.pointerType = (uint)PT_TOUCH;
                    touchInfo.pointerInfo.pointerId = winId;
                    touchInfo.pointerInfo.ptPixelLocation = new POINT { X = absX, Y = absY };
                    touchInfo.pointerInfo.pointerFlags = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;

                    _shadowStates[winId] = PointerState.Move;
                    activeContacts.Add(touchInfo);
                }
                else // UP or CANCEL
                {
                    // Rule 3: Drop Stray Events for unmapped IDs silently
                    if (!_idMap.TryGetValue(record.AndroidPointerId, out uint winId))
                    {
                        continue;
                    }

                    _lastLocation.TryGetValue(winId, out var lastPt);
                    if (lastPt.X == 0 && lastPt.Y == 0)
                    {
                        lastPt = new POINT { X = absX, Y = absY };
                    }

                    var touchInfo = new POINTER_TOUCH_INFO
                    {
                        touchFlags = TOUCH_FLAG_NONE,
                        touchMask = TOUCH_MASK_NONE,
                        orientation = 0,
                        pressure = 0
                    };
                    touchInfo.pointerInfo.pointerType = (uint)PT_TOUCH;
                    touchInfo.pointerInfo.pointerId = winId;
                    touchInfo.pointerInfo.ptPixelLocation = lastPt;
                    touchInfo.pointerInfo.pointerFlags = POINTER_FLAG_UP;

                    _shadowStates[winId] = PointerState.Up;
                    upContacts.Add(touchInfo);
                    winIdsToRecycle.Add(winId);
                }
            }

            // Rule 5: PASS 1 — Inject all UP (liftoff) events first
            if (upContacts.Count > 0)
            {
                if (!InjectTouchInput((uint)upContacts.Count, upContacts.ToArray()))
                {
                    int err = Marshal.GetLastWin32Error();
                    Console.Error.WriteLine($"[TOUCH-INJECT] PASS-1 (UP) failed for {upContacts.Count} contacts. Win32 Error: {err}");
                }

                // Rule 2: Delayed Unmapping — recycle IDs back into pool after injection
                foreach (var winId in winIdsToRecycle)
                {
                    RecycleWinIdInternal(winId);
                }
            }

            // Rule 5: PASS 2 — Inject all active DOWN/UPDATE events second
            if (activeContacts.Count > 0)
            {
                if (!InjectTouchInput((uint)activeContacts.Count, activeContacts.ToArray()))
                {
                    int err = Marshal.GetLastWin32Error();
                    Console.Error.WriteLine($"[TOUCH-INJECT] PASS-2 (ACTIVE) failed for {activeContacts.Count} contacts. Win32 Error: {err}");
                }
            }

            // Hide/Show Cursor Management
            bool anyActiveInShadow = _shadowStates.Values.Any(s => s == PointerState.Down || s == PointerState.Move);

            if (anyActiveInShadow && !_isCursorHidden && !_suppressHideUntilNextTouchDown)
            {
                HideCursorInternal();
            }
        }
    }

    private static void RecycleWinIdInternal(uint winId)
    {
        if (_reverseIdMap.TryGetValue(winId, out byte androidId))
        {
            _idMap.Remove(androidId);
            _reverseIdMap.Remove(winId);
        }
        _shadowStates.Remove(winId);
        _lastLocation.Remove(winId);

        if (!_idPool.Contains(winId) && winId >= 1 && winId <= MAX_TOUCH_COUNT)
        {
            _idPool.Enqueue(winId);
        }
    }

    public static void FlushAllTouches()
    {
        lock (_lock)
        {
            if (_shadowStates.Count == 0) return;

            var contacts = new List<POINTER_TOUCH_INFO>();
            foreach (var kvp in _shadowStates)
            {
                if (kvp.Value == PointerState.Down || kvp.Value == PointerState.Move)
                {
                    _lastLocation.TryGetValue(kvp.Key, out var lastPt);
                    var touchInfo = new POINTER_TOUCH_INFO
                    {
                        touchFlags = TOUCH_FLAG_NONE,
                        touchMask = TOUCH_MASK_NONE
                    };
                    touchInfo.pointerInfo.pointerType = (uint)PT_TOUCH;
                    touchInfo.pointerInfo.pointerId = kvp.Key;
                    touchInfo.pointerInfo.ptPixelLocation = lastPt;
                    touchInfo.pointerInfo.pointerFlags = POINTER_FLAG_UP;
                    contacts.Add(touchInfo);
                }
            }

            if (contacts.Count > 0)
            {
                InjectTouchInput((uint)contacts.Count, contacts.ToArray());
            }

            _shadowStates.Clear();
            _idMap.Clear();
            _reverseIdMap.Clear();
            _lastLocation.Clear();

            _idPool.Clear();
            for (uint i = 1; i <= MAX_TOUCH_COUNT; i++)
            {
                _idPool.Enqueue(i);
            }

            RestoreCursorInternal();
        }
    }

    private static void EmitSingleStateChange(uint winId, int absX, int absY, uint pointerFlags)
    {
        var touchInfo = new POINTER_TOUCH_INFO
        {
            touchFlags = TOUCH_FLAG_NONE,
            touchMask = TOUCH_MASK_NONE
        };
        touchInfo.pointerInfo.pointerType = (uint)PT_TOUCH;
        touchInfo.pointerInfo.pointerId = winId;
        touchInfo.pointerInfo.ptPixelLocation = new POINT { X = absX, Y = absY };
        touchInfo.pointerInfo.pointerFlags = pointerFlags;

        InjectTouchInput(1, new[] { touchInfo });
    }

    private static int _savedMouseX = -10000;
    private static int _savedMouseY = -10000;

    private static void HideCursorInternal()
    {
        if (!_isCursorHidden && GetCursorPos(out var pt))
        {
            _savedMouseX = pt.X;
            _savedMouseY = pt.Y;
            SetCursorPos(-10000, -10000);
        }

        if (_hBlankCursor != IntPtr.Zero)
        {
            bool s1 = SetSystemCursor(CopyCursor(_hBlankCursor), OCR_NORMAL);
            bool s2 = SetSystemCursor(CopyCursor(_hBlankCursor), OCR_IBEAM);
            bool s3 = SetSystemCursor(CopyCursor(_hBlankCursor), OCR_WAIT);
            bool s4 = SetSystemCursor(CopyCursor(_hBlankCursor), OCR_CROSS);
            bool s5 = SetSystemCursor(CopyCursor(_hBlankCursor), OCR_HAND);
            _isCursorHidden = true;
            Console.WriteLine($"[CURSOR] System cursor hidden for touch interaction (status: N={s1}, I={s2}, W={s3}, C={s4}, H={s5}).");
        }
        else
        {
            _isCursorHidden = true;
            Console.Error.WriteLine("[CURSOR] System cursor parked off-screen for touch interaction.");
        }
    }

    private static void RestoreCursorInternal()
    {
        if (_isCursorHidden)
        {
            if (_savedMouseX != -10000 && _savedMouseY != -10000)
            {
                SetCursorPos(_savedMouseX, _savedMouseY);
                _savedMouseX = -10000;
                _savedMouseY = -10000;
            }

            // SPI_SETCURSORS (0x0057) cleanly restores all user system cursors from registry
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_SENDWININICHANGE);
            _isCursorHidden = false;
            Console.WriteLine("[CURSOR] System cursor restored.");
        }
    }

    private const int SM_CXCURSOR = 13;
    private const int SM_CYCURSOR = 14;

    private static IntPtr CreateBlankCursor()
    {
        int width = GetSystemMetrics(SM_CXCURSOR);
        int height = GetSystemMetrics(SM_CYCURSOR);
        if (width <= 0) width = 32;
        if (height <= 0) height = 32;

        int stride = ((width + 15) / 16) * 2; // DWORD/WORD aligned 1bpp stride
        int maskSize = stride * height;

        byte[] andMask = new byte[maskSize];
        Array.Fill(andMask, (byte)0xFF); // 100% transparent
        byte[] xorMask = new byte[maskSize]; // 0% color inversion

        IntPtr hCursor = CreateCursor(IntPtr.Zero, 0, 0, width, height, andMask, xorMask);
        if (hCursor == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"[CURSOR-ERROR] CreateCursor ({width}x{height}, {maskSize}b) failed. Win32 Error: {err}");
        }
        else
        {
            Console.WriteLine($"[CURSOR-INIT] Created {width}x{height} transparent blank cursor handle: 0x{hCursor:X}");
        }
        return hCursor;
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight, [In] byte[] pvANDPlane, [In] byte[] pvXORPlane);
}
