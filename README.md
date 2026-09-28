# OMT Capture Studio

A standalone broadcast-grade desktop GUI client for **Open Media Transport (OMT)** to discover, preview, capture, and record video and multi-channel audio directly to disk (`.mp4`, `.mkv`, `.mov`) without requiring OBS Studio or commercial switchers.

---

## Features

- **Automatic Network Discovery**: Integrated mDNS/DNS-SD discovery via `OMTDiscovery` to automatically detect live OMT feeds across the local network.
- **Manual URL & Port Entry**: Direct connection support via `omt://<ip>:<port>`.
- **High-Performance Video Viewport**: Zero-copy/low-overhead BGRA rendering via WPF `WriteableBitmap` with aspect ratio preservation.
- **Multi-Channel Audio VU Metering**: Real-time dBFS peak and RMS volume bars with peak hold decay, clipping indicators, and optional local headphone/speaker monitoring via WASAPI.
- **Recording Engine**: Asynchronous dual named pipes feeding `FFmpeg` to record without frame drops or memory leaks:
  - Formats: **MP4** (H.264/HEVC), **MKV** (crash-resilient), **MOV** (ProRes).
  - Hardware Encoders: Auto Hardware (NVIDIA NVENC, Intel QuickSync, AMD AMF) or CPU (`libx264`).
- **Built-in Test Signal Generator**: Generates 1080p60 SMPTE color bars with motion tick and stereo sine wave tones (440 Hz / 880 Hz) using `OMTSend` for instant offline testing and calibration.

---

## Solution Structure

The project is structured as a standard .NET Solution:
- **`OmtCaptureStudio.sln` / `OmtCaptureStudio.slnx`**: The master solution file.
  - **`OmtCaptureStudio`**: Main WPF GUI application.
  - **`OmtCaptureStudio.Tests`**: Automated end-to-end verification and diagnostic test suite.

---

## Quick Start

### 1. Building the Solution
Open `OmtCaptureStudio.sln` in Visual Studio 2022, JetBrains Rider, or VS Code, or build via CLI:
```powershell
dotnet build OmtCaptureStudio.sln -c Release
```

### 2. Launching the App
Double-click `Launch-OmtCaptureStudio.bat` or run:
```powershell
dotnet run --project OmtCaptureStudio -c Release
```

### 2. Testing with the Built-in Signal Generator
1. Click the **Test Signal** button in the top right.
2. The client will start broadcasting an OMT feed locally and automatically connect to it.
3. You will see the animated SMPTE color bars in the viewport, the audio VU meters bouncing, and stream specs showing `1920x1080 @ 60 fps, 2 ch 48 kHz`.
4. Click **● START RECORDING** to capture to MP4.
5. Click **■ STOP RECORDING** after a few seconds. The recorded video is saved to `Videos\OMT_Captures`. Click **Open Folder** to view or play it in VLC / Media Player.

### 3. Capturing an External OMT Stream
1. Ensure your camera or production system (e.g., vMix, another OMT sender) is on the same local network.
2. Select the stream from the **Source** dropdown (or type its URL in **Manual URL**).
3. Click **Connect**.
