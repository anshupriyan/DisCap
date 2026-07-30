using System.Runtime.InteropServices;
using Discap.Host.Protocol;

namespace Discap.Host.Input;

public static class TouchInjector
{
    private const uint TOUCH_FEEDBACK_INDIRECT = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InitializeTouchInjection(uint maxCount = 10, uint dwMode = TOUCH_FEEDBACK_INDIRECT);

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

    private const int IDC_ARROW = 32512;
    private const uint OCR_NORMAL = 32512;

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
        public uint orientation;
        public uint pressure;
    }

    private static bool _initialized = false;
    private static IntPtr _hOriginalCursor = IntPtr.Zero;
    private static IntPtr _hBlankCursor = IntPtr.Zero;
    private static bool _isCursorHidden = false;

    private static readonly Dictionary<byte, uint> _idMap = new();
    private static readonly Dictionary<uint, PointerState> _shadowStates = new();
    private static uint _nextWindowsId = 1;
    private static readonly object _lock = new();

    private enum PointerState { Up, Down, Move }

    public static void Initialize()
    {
        lock (_lock)
        {
            if (_initialized) return;

            try
            {
                _initialized = InitializeTouchInjection(10, TOUCH_FEEDBACK_INDIRECT);
                Console.WriteLine($"[TOUCH] InitializeTouchInjection initialized: {_initialized}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[TOUCH] InitializeTouchInjection error: {ex.Message}");
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
        }
    }

    public static void ProcessMultiTouch(MultiTouchPacket packet, int boundsX, int boundsY, int width, int height)
    {
        if (!_initialized || packet.PointerCount == 0) return;

        lock (_lock)
        {
            var contacts = new List<POINTER_TOUCH_INFO>();

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
                    var touchInfo = new POINTER_TOUCH_INFO();
                    touchInfo.pointerInfo.pointerType = (uint)PT_TOUCH;
                    touchInfo.pointerInfo.pointerId = winId;
                    touchInfo.pointerInfo.pointerFlags = POINTER_FLAG_UP;
                    contacts.Add(touchInfo);

                    _shadowStates[winId] = PointerState.Up;
                    _idMap.Remove(missingId);
                }
            }

            for (int i = 0; i < packet.PointerCount; i++)
            {
                var record = packet.Pointers[i];

                if (!_idMap.TryGetValue(record.AndroidPointerId, out uint winId))
                {
                    winId = _nextWindowsId++;
                    if (_nextWindowsId > 1000) _nextWindowsId = 1;
                    _idMap[record.AndroidPointerId] = winId;
                }

                _shadowStates.TryGetValue(winId, out var currentState);

                int absX = boundsX + (int)((record.NormX / 65535.0f) * width);
                int absY = boundsY + (int)((record.NormY / 65535.0f) * height);
                uint pVal = (uint)((record.Pressure / 65535.0f) * 1024);

                var touchInfo = new POINTER_TOUCH_INFO
                {
                    touchFlags = TOUCH_FLAG_NONE,
                    touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_ORIENTATION | TOUCH_MASK_PRESSURE,
                    rcContact = new RECT { Left = absX - 2, Top = absY - 2, Right = absX + 2, Bottom = absY + 2 },
                    orientation = 0,
                    pressure = pVal
                };

                touchInfo.pointerInfo.pointerType = (uint)PT_TOUCH;
                touchInfo.pointerInfo.pointerId = winId;
                touchInfo.pointerInfo.ptPixelLocation = new POINT { X = absX, Y = absY };

                uint flags = POINTER_FLAG_NONE;

                // Action: 0=Down, 1=Move, 2=Up, 3=Cancel
                if (record.Action == 0) // DOWN
                {
                    if (currentState == PointerState.Down || currentState == PointerState.Move)
                    {
                        // Sanitize: emit UP first if already down
                        EmitSingleStateChange(winId, absX, absY, POINTER_FLAG_UP);
                    }
                    flags = POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
                    _shadowStates[winId] = PointerState.Down;
                }
                else if (record.Action == 1) // MOVE
                {
                    if (currentState == PointerState.Up)
                    {
                        // Sanitize: emit DOWN first if was up
                        EmitSingleStateChange(winId, absX, absY, POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT);
                    }
                    flags = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
                    _shadowStates[winId] = PointerState.Move;
                }
                else // UP or CANCEL
                {
                    flags = POINTER_FLAG_UP;
                    _shadowStates[winId] = PointerState.Up;
                    _idMap.Remove(record.AndroidPointerId);
                }

                touchInfo.pointerInfo.pointerFlags = flags;
                contacts.Add(touchInfo);
            }

            if (contacts.Count > 0)
            {
                InjectTouchInput((uint)contacts.Count, contacts.ToArray());
            }

            // Hide/Show Cursor Management
            bool anyActiveInShadow = _shadowStates.Values.Any(s => s == PointerState.Down || s == PointerState.Move);
            if (anyActiveInShadow && !_isCursorHidden)
            {
                HideCursorInternal();
            }
            else if (!anyActiveInShadow && _isCursorHidden)
            {
                RestoreCursorInternal();
            }
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
                    var touchInfo = new POINTER_TOUCH_INFO();
                    touchInfo.pointerInfo.pointerType = (uint)PT_TOUCH;
                    touchInfo.pointerInfo.pointerId = kvp.Key;
                    touchInfo.pointerInfo.pointerFlags = POINTER_FLAG_UP;
                    contacts.Add(touchInfo);
                }
            }

            if (contacts.Count > 0)
            {
                InjectTouchInput((uint)contacts.Count, contacts.ToArray());
                Console.WriteLine($"[TOUCH-WATCHDOG] Flushed {contacts.Count} active touch points on disconnect.");
            }

            _shadowStates.Clear();
            _idMap.Clear();
            RestoreCursorInternal();
        }
    }

    private static void EmitSingleStateChange(uint winId, int absX, int absY, uint pointerFlags)
    {
        var touchInfo = new POINTER_TOUCH_INFO
        {
            touchFlags = TOUCH_FLAG_NONE,
            touchMask = TOUCH_MASK_CONTACTAREA,
            rcContact = new RECT { Left = absX - 2, Top = absY - 2, Right = absX + 2, Bottom = absY + 2 }
        };
        touchInfo.pointerInfo.pointerType = (uint)PT_TOUCH;
        touchInfo.pointerInfo.pointerId = winId;
        touchInfo.pointerInfo.ptPixelLocation = new POINT { X = absX, Y = absY };
        touchInfo.pointerInfo.pointerFlags = pointerFlags;

        InjectTouchInput(1, new[] { touchInfo });
    }

    private static void HideCursorInternal()
    {
        if (_hBlankCursor != IntPtr.Zero)
        {
            SetSystemCursor(_hBlankCursor, OCR_NORMAL);
            _isCursorHidden = true;
        }
    }

    private static void RestoreCursorInternal()
    {
        if (_isCursorHidden)
        {
            if (_hOriginalCursor != IntPtr.Zero)
            {
                SetSystemCursor(_hOriginalCursor, OCR_NORMAL);
            }
            else
            {
                SystemParametersInfo(0x0057, 0, IntPtr.Zero, 0x0001);
            }
            _isCursorHidden = false;
        }
    }

    private static IntPtr CreateBlankCursor()
    {
        byte[] andMask = new byte[] { 0xFF };
        byte[] xorMask = new byte[] { 0x00 };
        return CreateCursor(IntPtr.Zero, 0, 0, 1, 1, andMask, xorMask);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);
}
