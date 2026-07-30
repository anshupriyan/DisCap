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
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(ref RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "ClipCursor")]
    private static extern bool ReleaseClipCursor(IntPtr lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
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
    private static bool _isCursorClipped = false;
    private static POINT _savedMousePos;

    // Rule 1: ID Pool (IDs 1..10) strictly bounded by MAX_TOUCH_COUNT
    private static readonly Queue<uint> _idPool = new();
    private static readonly Dictionary<byte, uint> _idMap = new();
    private static readonly Dictionary<uint, byte> _reverseIdMap = new();
    private static readonly Dictionary<uint, PointerState> _shadowStates = new();
    private static readonly Dictionary<uint, POINT> _lastLocation = new();
    private static readonly object _lock = new();

    private enum PointerState { Up, Down, Move }

    public static bool IsTouchActive { get; private set; } = false;
    public static bool IsCursorHidden => false;

    public static void RestoreCursorIfHidden() { }

    private static void LockCursorToCurrentMonitor()
    {
        if (!_isCursorClipped && GetCursorPos(out POINT cursorPos))
        {
            if (!IsCursorOnStreamedDisplay(cursorPos.X, cursorPos.Y))
            {
                _savedMousePos = cursorPos;
                IntPtr hMonitor = MonitorFromPoint(cursorPos, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    if (ClipCursor(ref mi.rcMonitor))
                    {
                        _isCursorClipped = true;
                    }
                }
            }
        }
    }

    private static void ReleaseCursorLock()
    {
        if (_isCursorClipped)
        {
            ReleaseClipCursor(IntPtr.Zero);
            if (_savedMousePos.X != 0 || _savedMousePos.Y != 0)
            {
                SetCursorPos(_savedMousePos.X, _savedMousePos.Y);
            }
            _isCursorClipped = false;
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
        }
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
                    _shadowStates.TryGetValue(winId, out var state);
                    if (state == PointerState.Up)
                    {
                        // Pointer is already UP — ignore duplicate UP to prevent Win32 Error 87
                        continue;
                    }

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

                    _lastLocation[winId] = new POINT { X = absX, Y = absY };

                    uint tMask = pVal > 0 ? TOUCH_MASK_PRESSURE : TOUCH_MASK_NONE;
                    var touchInfo = new POINTER_TOUCH_INFO
                    {
                        touchFlags = TOUCH_FLAG_NONE,
                        touchMask = tMask,
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
                    if (!_idMap.TryGetValue(record.AndroidPointerId, out uint winId))
                    {
                        continue;
                    }

                    _shadowStates.TryGetValue(winId, out var currentState);
                    if (_lastLocation.TryGetValue(winId, out var prevPt) && prevPt.X == absX && prevPt.Y == absY && currentState == PointerState.Move)
                    {
                        // Skip 0-pixel delta move events to maintain clean DirectManipulation velocity math
                        continue;
                    }
                    _lastLocation[winId] = new POINT { X = absX, Y = absY };

                    uint tMask = pVal > 0 ? TOUCH_MASK_PRESSURE : TOUCH_MASK_NONE;
                    var touchInfo = new POINTER_TOUCH_INFO
                    {
                        touchFlags = TOUCH_FLAG_NONE,
                        touchMask = tMask,
                        orientation = 0,
                        pressure = pVal
                    };
                    touchInfo.pointerInfo.pointerType = (uint)PT_TOUCH;
                    touchInfo.pointerInfo.pointerId = winId;
                    touchInfo.pointerInfo.ptPixelLocation = new POINT { X = absX, Y = absY };
                    touchInfo.pointerInfo.pointerFlags = (currentState == PointerState.Up) 
                        ? (POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT)
                        : (POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT);

                    _shadowStates[winId] = PointerState.Move;
                    activeContacts.Add(touchInfo);
                }
                else // UP or CANCEL
                {
                    if (!_idMap.TryGetValue(record.AndroidPointerId, out uint winId))
                    {
                        continue;
                    }

                    _shadowStates.TryGetValue(winId, out var currentState);
                    if (currentState == PointerState.Up)
                    {
                        // Pointer is already UP — ignore duplicate UP to prevent Win32 Error 87
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
                    FlushAllTouches();
                    return;
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
                    FlushAllTouches();
                    return;
                }
            }

            // ClipCursor Lock Management
            bool anyActiveInShadow = _shadowStates.Values.Any(s => s == PointerState.Down || s == PointerState.Move);

            if (anyActiveInShadow && !IsTouchActive)
            {
                LockCursorToCurrentMonitor();
                IsTouchActive = true;
            }
            else if (!anyActiveInShadow && IsTouchActive)
            {
                ReleaseCursorLock();
                IsTouchActive = false;
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
            ReleaseCursorLock();
            IsTouchActive = false;

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
        }
    }
}
