using System.Runtime.InteropServices;
using Discap.Host.Protocol;

namespace Discap.Host.Input;

/// <summary>
/// Injects absolute mouse events into Windows using User32.SetCursorPos and SendInput.
/// </summary>
public static class MouseInjector
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
    private static bool _wasMouseDown = false;

    public static void EnableDpiAwareness()
    {
        try
        {
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch
        {
            try
            {
                SetProcessDPIAware();
            }
            catch { }
        }
    }

    public static void ProcessInput(InputPacket packet, int boundsX, int boundsY, int width, int height)
    {
        // 1. Convert normalized coordinates (0..65535) back to float (0..1)
        float xNorm = packet.X / 65535.0f;
        float yNorm = packet.Y / 65535.0f;

        // 2. Map normalized touch position directly to local virtual display bounds
        int absoluteX = boundsX + (int)(xNorm * width);
        int absoluteY = boundsY + (int)(yNorm * height);

        // 4. Move mouse cursor onto the Virtual Display
        SetCursorPos(absoluteX, absoluteY);

        // 5. Handle Clicks
        bool isMouseDown = packet.Action == 1 || packet.Action == 2; // Down or Move

        if (isMouseDown && !_wasMouseDown)
        {
            // Inject Mouse Down
            InjectClick(MOUSEEVENTF_LEFTDOWN);
        }
        else if (!isMouseDown && _wasMouseDown)
        {
            // Inject Mouse Up
            InjectClick(MOUSEEVENTF_LEFTUP);
        }

        _wasMouseDown = isMouseDown;
    }

    private static void InjectClick(uint flag)
    {
        INPUT[] inputs = new INPUT[1];
        inputs[0] = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dwFlags = flag
            }
        };

        SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
    }
}
