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
│   ↓         ↓       │  tcp:53516   │ Render           │
│  LZ4    NVENC H.264 │               │ (SurfaceView)   │
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

## Project Status

| Phase | Component | Status |
|-------|-----------|--------|
| 1 | Windows Host (capture + stream) | ✅ Core complete |
| 1 | LZ4 compression | ✅ Working |
| 1 | NVENC encoding | 🔧 Detection done, encoding TODO |
| 2 | Android receiver | 📋 Planned |
| 3 | Touch/stylus input | 📋 Planned |

## License

MIT — see [LICENSE](LICENSE) for details.

## Credits

Built with these open-source projects:
- [parsec-vdd](https://github.com/nomi-san/parsec-vdd) — Virtual display driver
- [Vortice.Windows](https://github.com/amerkoleci/vortice.windows) — DirectX bindings
- [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) — LZ4 compression
