# Discap — Open-Source Virtual Display Streamer

Stream your Windows desktop to an Android tablet over USB with ultra-low latency.

## What is Discap?

Discap turns your Android tablet into a secondary display + drawing tablet for your Windows PC. It creates a virtual monitor on Windows, captures the screen content, compresses it adaptively, and streams it to your Android device over a USB connection.

**Key features:**
- 🖥️ **Virtual Display** — Creates a real Windows display via Parsec VDD (IddCx)
- ⚡ **Low Latency** — LZ4 compression for static content, NVENC for motion
- 🔌 **USB Connection** — ADB socket forwarding, no WiFi needed
- 🎨 **Stylus Support** — Pressure-sensitive input (Phase 3)
- 📖 **Open Source** — MIT licensed, fully hackable

## Architecture

```
Windows Host                           Android Tablet
┌─────────────────────┐               ┌──────────────────┐
│ Parsec VDD          │               │                  │
│   ↓                 │               │ Socket Client    │
│ Desktop Duplication │               │   ↓              │
│   ↓                 │    USB/ADB    │ Decompress       │
│ Frame Analysis      │──────────────→│   ↓              │
│   ↓         ↓       │  tcp:53516    │ Render           │
│  LZ4    NVENC H.264 │               │ (SurfaceView)    │
│   ↓         ↓       │               │                  │
│ Packet Protocol     │               │ Touch/Stylus     │
│ (32-byte DCAP hdr)  │←──────────────│ Input Events     │
└─────────────────────┘               └──────────────────┘
```

## Prerequisites

1. **Windows 10/11** (64-bit)
2. **.NET 8 SDK** — [Download](https://dotnet.microsoft.com/download/dotnet/8.0)
3. **Parsec VDD Driver** — Install `parsec-vdd-0.41` from [releases](https://github.com/nomi-san/parsec-vdd/releases)
4. **ADB** — [Android SDK Platform Tools](https://developer.android.com/tools/releases/platform-tools)
5. **NVIDIA GPU** (optional) — RTX series for NVENC hardware encoding

## Build

```bash
git clone https://github.com/discap/discap.git
cd discap
dotnet build -c Release
```

## Usage

```bash
# Run with default settings (1920x1200 @ 60Hz)
dotnet run --project src/Discap.Host

# Custom resolution matching your tablet
dotnet run --project src/Discap.Host -- --width 2560 --height 1600

# Force LZ4-only mode (no NVENC)
dotnet run --project src/Discap.Host -- --lz4-only

# See all options
dotnet run --project src/Discap.Host -- --help
```

> **Note:** Discap requires administrator privileges to access the virtual display driver.

## Display Configuration

Discap's virtual display (created by Parsec VDD) appears as a real monitor in Windows, so you control its **resolution**, **refresh rate**, and **aspect ratio** directly through native Windows settings — no custom in-app protocol needed.

### Changing Resolution & Refresh Rate

1. Open **Windows Settings → System → Display**
2. Select the Parsec VDD virtual display
3. Under **Display resolution**, choose your desired resolution
4. Under **Advanced display** → **Choose a refresh rate**, select your target (e.g. 60Hz, 120Hz, 144Hz)

Discap automatically detects and adapts to these changes mid-stream:
- The capture pipeline dynamically re-initializes the hardware encoder when the display resolution or refresh rate changes
- No restart required — changes take effect within a frame or two

### FPS Cap (Android App)

The Android app includes an **FPS cap** setting with the following options:

| Option | Behavior |
|--------|----------|
| **Native** (default) | No software cap — streams at the display's actual refresh rate |
| 30 / 60 / 120 / 144 | Throttles frame delivery to the selected rate |

**Native** is the recommended default. It means Discap streams exactly as fast as the virtual display produces frames (governed by VSync), so if Windows is set to 144Hz, Discap streams at 144 FPS automatically.

Use a fixed cap (e.g. 60) to **save bandwidth or battery** on the tablet, even when the desktop runs at a higher refresh rate.

### Aspect Ratio & Letterboxing

When the streamed resolution's aspect ratio differs from the tablet's physical screen (e.g. 16:9 content on a 16:10 tablet), the Android app automatically letterboxes or pillarboxes the video to preserve the correct aspect ratio — no stretching, with black bars filling the unused space.

## Project Status

| Phase | Component | Status |
|-------|-----------|--------|
| 1 | Windows Host (capture + stream) | ✅ Working |
| 1 | LZ4 compression | ✅ Working |
| 1 | NVENC H.264 hardware encoding | ✅ Working |
| 1 | Dynamic resolution/refresh rate | ✅ Working |
| 2 | Android receiver + H.264 decoder | ✅ Working |
| 2 | Letterboxing / aspect ratio | ✅ Working |
| 3 | Touch/stylus input | ✅ Basic touch working |

## License

MIT — see [LICENSE](LICENSE) for details.

## Credits

Built with these open-source projects:
- [parsec-vdd](https://github.com/nomi-san/parsec-vdd) — Virtual display driver
- [Vortice.Windows](https://github.com/amerkoleci/vortice.windows) — DirectX bindings
- [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) — LZ4 compression
