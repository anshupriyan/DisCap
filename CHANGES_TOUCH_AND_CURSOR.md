# Commit Summary: Win32 Multi-Touch Injection & Dynamic Cursor Visibility

- **Branch**: `color-fix-and-touch-update`
- **Commit Hash**: `4ac3f16`
- **Commit Message**: `feat(touch-cursor): fix Win32 multi-touch injection & dynamic per-display cursor visibility`
- **Date**: July 30, 2026

---

## 🚀 Overview of Changes

This update delivers a complete overhaul of the Win32 multi-touch digitizer engine and dynamic cursor visibility system in DisCap 2.0, resolving Win32 Error 87 digitizer lockups, DWM software cursor composition leaks, and multi-monitor cursor displacement.

---

## 🛠️ Detailed Breakdown of Fixes & Features

### 1. Win32 Multi-Touch Injection Fixes (`TouchInjector.cs`)
* **x64 Memory Alignment**: Added missing `rcContactRaw` 16-byte `RECT` struct field to `POINTER_TOUCH_INFO` for x64 C# marshalling alignment.
* **Border Coordinate Bounds**: Switched to `TOUCH_MASK_NONE` (`0`) to prevent negative contact rectangle coordinates when touching near screen edges ($X=0$ or $Y=0$).
* **2-Pass Frame Injection**: Separated `POINTER_FLAG_UP` (liftoff) and `POINTER_FLAG_UPDATE` (dragging) into two sequential `InjectTouchInput` API calls, preventing Windows kernel array rejection.
* **Bounded Pointer ID Pool**: Enforced a `Queue<uint>` ID pool holding IDs `1..10` matching Win32 `InitializeTouchInjection(10, ...)`, eliminating touch lockups after 10 taps.
* **Stray Event Protection**: Silently drop out-of-order `MOVE` packets for unmapped IDs to eliminate ghost touches.

### 2. Win32 High-DPI Cursor Mask (`TouchInjector.cs`)
* **Dynamic DPI Metrics**: Dynamically queries `GetSystemMetrics(SM_CXCURSOR)` / `SM_CYCURSOR` (handling 100%, 125%, 150%, 200% High-DPI displays).
* **DWORD-Aligned Mask**: Generates exact 1bpp transparent AND/XOR masks (`new byte[maskSize]`), preventing `CreateCursor` from failing with Win32 Error 1400.

### 3. Per-Display Off-Screen Cursor Parking & Main Screen Lock (`TouchInjector.cs`)
* **DWM Composition Bypass**: Parks the system cursor at `(-10000, -10000)` during touch, bypassing Windows DWM Software Cursor Composition and removing all cursor pixels from the H.264 video stream.
* **Main Screen Coordinate Lock**: Saves pre-touch physical mouse coordinates `(_savedMouseX, _savedMouseY)`. When restoring, moves the cursor directly back to your Main PC Screen, preventing cursor displacement across monitors.
* **Persistent Touch Hide**: Lifting your finger off the touch screen leaves the cursor hidden; it **only reappears when physical PC mouse or trackpad movement is detected**.

### 4. Decoupled 15ms Motion Watcher & Touch Suppression (`TouchInjector.cs`)
* **15ms Polling Thread**: Background thread polling `GetCursorPos()` at ~60Hz decoupled from the DXGI capture loop.
* **100ms Touch Suppression**: Suppresses OS touch-to-mouse promotion position jumps for 100ms following any touch packet (`msSinceTouch < 100.0`), preventing false cursor unhiding during active touch.

### 5. Stream Protocol & Remote Trackpad Integration (`Program.cs`, `MouseInjector.cs`, `MainActivity.kt`)
* **Dynamic Visibility Gate**: `Program.cs` monitors `TouchInjector.IsCursorHidden` and `lastSentCursorVisible`, suppressing `CursorPos` stream packets while touch is active and resuming packet transmission when physical mouse movement occurs.
* **Remote Trackpad Integration**: Incoming remote trackpad mouse packets trigger `TouchInjector.RestoreCursorIfHidden()`.

---

## 📁 Modified Files

- `src/Discap.Host/Input/TouchInjector.cs`: Multi-touch digitizer, high-DPI blank cursor, per-display off-screen parking, 15ms motion watcher thread.
- `src/Discap.Host/Input/MouseInjector.cs`: Remote trackpad cursor restoration integration.
- `src/Discap.Host/Program.cs`: Stream visibility gating & `lastSentCursorVisible` state tracking.
- `src/Discap.Android/app/src/main/java/com/discap/android/MainActivity.kt`: Multi-touch gesture handling.
- `CHANGES_TOUCH_AND_CURSOR.md`: Summary documentation file.
