# Discap AOAP Driver Bundle

This directory contains the WinUSB driver installer used for AOAP (Android Open Accessory Protocol) mode.

## Required File

### `Zadig.exe`

Download from the official Zadig website:
<https://zadig.akeo.ie/downloads/>

1. Download the latest `zadig-X.X.exe` (e.g., `zadig-2.9.exe`, ~5MB)
2. **Rename it** to `Zadig.exe` and place it in this directory (`drivers/Zadig.exe`)
3. The file must be named exactly `Zadig.exe`

## What It Does

When you run Discap with `--transport aoap`, the driver manager:

1. Generates a `zadig.ini` config file that pre-fills the correct settings
2. Launches Zadig with UAC elevation — a window will open
3. You select your Android device and click **"Install Driver"**
4. Zadig installs the WinUSB driver and closes automatically on success
5. This is done once per device — subsequent runs skip the driver install

This allows LibUsbDotNet to enumerate your phone and send the AOA control transfers
needed to switch it into accessory mode.

## ⚠️ Warning

Installing WinUSB for your phone replaces the ADB driver. While in AOAP mode:
- ADB commands will NOT work
- File transfer (MTP) will NOT work
- To revert: run `Discap.Host --revert-driver` for step-by-step instructions

## License

Zadig is licensed under GPL v3. See <https://github.com/pbatard/libwdi/blob/master/LICENSE>.
It is a standalone tool and is not linked into the Discap binary.
