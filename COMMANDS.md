# Discap Commands Reference Guide

This document contains all the commands needed to build, configure, run, and troubleshoot **Discap** (both the Windows Host and the Android Client).

---

## 📋 Table of Contents
1. [Prerequisites & System Diagnostics](#1-prerequisites--system-diagnostics)
2. [ADB Setup & Port Forwarding](#2-adb-setup--port-forwarding)
3. [Windows Host (.NET 9 C#) Commands](#3-windows-host-net-9-c-commands)
4. [Android Client (Gradle/Kotlin) Commands](#4-android-client-gradlekotlin-commands)
5. [Troubleshooting & Log Commands](#5-troubleshooting--log-commands)
6. [Complete Quickstart Workflow](#6-complete-quickstart-workflow)

---

## 1. Prerequisites & System Diagnostics

Before running Discap, verify your development environment and connected devices:

### Check Installed Tools
```powershell
# Verify .NET SDK installation (requires .NET 9.0 SDK)
dotnet --version

# Verify ADB installation
adb version

# Verify Java JDK installation (for Android build)
java -version
```

### Check Connected USB Devices
```powershell
# List connected Android devices over USB
adb devices
```
*Ensure USB Debugging is enabled on your Android tablet and the authorization prompt on the device has been accepted.*

---

## 2. ADB Setup & Port Forwarding

Discap streams screen data over TCP socket forwarding via USB connection.

```powershell
# Set up ADB port forwarding (Host TCP port 53516 -> Android TCP port 53516)
adb forward tcp:53516 tcp:53516

# Verify active port forwards
adb forward --list

# Remove Discap port forwarding rule (if resetting connection)
adb forward --remove tcp:53516

# Remove all ADB port forwarding rules
adb forward --remove-all
```

---

## 3. Windows Host (.NET 9 C#) Commands

> ⚠️ **Important:** Discap Host requires **Administrator privileges** to manage the Parsec VDD virtual display driver. Run your command prompt or terminal as Administrator.

### Building the Host

```powershell
# Build in Debug mode
dotnet build src/Discap.Host/Discap.Host.csproj

# Build in Release mode (optimized performance)
dotnet build src/Discap.Host/Discap.Host.csproj -c Release

# Clean build artifacts
dotnet clean src/Discap.Host/Discap.Host.csproj
```

### Running the Host via `dotnet run`

```powershell
# Run with default settings (1920x1200 @ 60Hz, 20 Mbps, ADB mode)
dotnet run --project src/Discap.Host

# Custom resolution matching tablet screen (e.g. 2560x1600 @ 120Hz)
dotnet run --project src/Discap.Host -- --width 2560 --height 1600 --fps 120

# Higher bitrate (e.g., 30 Mbps for high motion / fine detail)
dotnet run --project src/Discap.Host -- --bitrate 30

# Force LZ4-only compression mode (disables NVENC hardware encoding)
dotnet run --project src/Discap.Host -- --lz4-only

# Specify GPU adapter index (0 = primary GPU)
dotnet run --project src/Discap.Host -- --adapter 0

# Set NVENC Rate Control Mode (vbr, cbr, or vbr-hq)
dotnet run --project src/Discap.Host -- --rc-mode cbr

# Run in AOAP mode (Android Open Accessory Protocol for direct USB transfer)
dotnet run --project src/Discap.Host -- --transport aoap

# Print instructions to revert WinUSB driver back to stock ADB driver (after AOAP mode)
dotnet run --project src/Discap.Host -- --revert-driver

# Display all CLI arguments and help text
dotnet run --project src/Discap.Host -- --help
```

### Running Compiled Executable Directly

```powershell
# Navigate to Release build directory (PowerShell / CMD)
.\src\Discap.Host\bin\Release\net9.0-windows10.0.22621.0\win-x64\Discap.Host.exe

# With custom parameters
.\src\Discap.Host\bin\Release\net9.0-windows10.0.22621.0\win-x64\Discap.Host.exe --width 2560 --height 1600
```

---

## 4. Android Client (Gradle/Kotlin) Commands

Commands for compiling and deploying the receiver app to your Android device.

### Building the Android App

Navigate to the Android project root or execute Gradle wrapper:

**On Windows (PowerShell / CMD):**
```powershell
cd src/Discap.Android

# Build Debug APK
.\gradlew.bat assembleDebug

# Build Release APK
.\gradlew.bat assembleRelease

# Clean build outputs
.\gradlew.bat clean
```

**On Linux / macOS / Bash:**
```bash
cd src/Discap.Android

# Build Debug APK
./gradlew assembleDebug

# Build Release APK
./gradlew assembleRelease

# Clean build outputs
./gradlew clean
```

### Installing and Running on Android Device

```powershell
# Install Debug APK directly via Gradle
cd src/Discap.Android
.\gradlew.bat installDebug

# Alternatively install compiled APK via ADB directly
adb install src/Discap.Android/app/build/outputs/apk/debug/app-debug.apk

# Re-install existing APK (overwriting previous installation)
adb install -r src/Discap.Android/app/build/outputs/apk/debug/app-debug.apk

# Launch the Discap App on device
adb shell am start -n com.discap.android/.MainActivity

# Uninstall the Discap App from device
adb uninstall com.discap.android
```

---

## 5. Troubleshooting & Log Commands

### Android Logcat Monitoring
```powershell
# View realtime logcat output filtered for Discap
adb logcat -s DiscapReceiver

# View all Discap related tags and errors
adb logcat *:E | Select-String "Discap"

# Clear logcat buffer
adb logcat -c
```

### ADB Connection Diagnostics
```powershell
# Restart ADB server if device is not responding
adb kill-server
adb start-server

# Re-check device status
adb devices
```

---

## 6. Complete Quickstart Workflow

Here is the exact step-by-step sequence of commands to get Discap running from scratch:

```powershell
# 1. Clone & enter repository
git clone https://github.com/anshupriyan/DisCap.git
cd DisCap

# 2. Connect tablet via USB & setup ADB port forwarding
adb devices
adb forward tcp:53516 tcp:53516

# 3. Build & Install Android App
cd src/Discap.Android
.\gradlew.bat installDebug
cd ..\..

# 4. Launch Android App on tablet
adb shell am start -n com.discap.android/.MainActivity

# 5. Build and run Windows Host (Run PowerShell terminal as Administrator)
dotnet run --project src/Discap.Host -- --width 1920 --height 1200
```
