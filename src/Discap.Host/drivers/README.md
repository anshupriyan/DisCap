# Discap AOAP Driver Bundle

This directory contains the WinUSB driver installer used for AOAP (Android Open Accessory Protocol) mode.

## Required File

### `wdi-simple.exe`

Download from the official **libwdi** releases:
<https://github.com/pbatard/libwdi/releases>

1. Download the latest `wdi-simple.exe` (~200KB) from the release assets
2. Place it in this directory (`drivers/wdi-simple.exe`)
3. The file must be named exactly `wdi-simple.exe`

## What It Does

When you run Discap with `--transport aoap`, the driver manager uses `wdi-simple.exe` to:

1. Install the WinUSB driver for your phone's VID/PID (replacing the ADB driver)
2. Install the WinUSB driver for Google's AOA PIDs (0x18D1/0x2D00 and 0x18D1/0x2D01)

This allows LibUsbDotNet to enumerate your phone and send the AOA control transfers
needed to switch it into accessory mode.

## ⚠️ Warning

Installing WinUSB for your phone replaces the ADB driver. While in AOAP mode:
- ADB commands will NOT work
- File transfer (MTP) will NOT work
- To revert: run `Discap.Host --revert-driver` for instructions

## License

libwdi / wdi-simple is licensed under LGPL v3. See <https://github.com/pbatard/libwdi/blob/master/LICENSE>.
