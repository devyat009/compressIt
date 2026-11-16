<div align="center">

# CompressIt

<img src="https://raw.githubusercontent.com/devyat009/image-repo-for-my-repo/refs/heads/main/CompressIt/readme/CompressIt.png" alt="CompressIt Logo" width="140" />

**A fast, modern Windows 11 video cutter and GPU-accelerated compressor powered by .NET 10, WPF-UI, LibVLC, and FFmpeg.**

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11%20(x64)-0078D4?style=flat-square&logo=windows)](https://github.com/devyat009/compressIt/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![License: PolyForm Noncommercial 1.0.0](https://img.shields.io/badge/License-PolyForm%20Noncommercial%201.0.0-4ADE80?style=flat-square)](LICENSE)
[![Author](https://img.shields.io/badge/Author-Devyat009-181717?style=flat-square&logo=github)](https://github.com/devyat009/)

</div>

---

## Features

- **Automatic First-Run FFmpeg Setup** — Automatically downloads and verifies `ffmpeg.exe` and `ffprobe.exe` on first launch with a live progress screen.
- **GPU Acceleration by Default** — Auto-detects and verifies hardware encoders (**AMD AMF**, **NVIDIA NVENC**, **Intel QuickSync**) with a **Settings** toggle to switch to **CPU** software encoding (`libx264`, `libx265`, `libsvtav1`, `libvpx-vp9`).
- **Interactive Timeline & Precision Cutting**
  - Click or drag the playhead `|` across the thumbnail filmstrip to scrub.
  - Slice videos into segments using **Cut at `|`** (shortcut: `C` or `S`).
  - Drag the **`<`** and **`>`** handles on any segment edge to adjust cuts, with **LosslessCut-style gray cut-out regions** (`--- 🗑 ---`) showing trimmed/excluded parts.
  - Hold **`Alt` + Mouse Wheel** to zoom the timeline (`1.0x`–`24.0x`) around your cursor.
- **Flexible Segment Export** — Choose whether to **merge kept segments into one video** or **export each segment as a separate file**.
- **Left Compression Options Drawer** — Configure **Codec** (`H.264`, `H.265/HEVC`, `AV1`, `VP9`), **Encoding Speed**, **FPS**, **Quality** (`Low` / `Medium` / `High` slider with exact `%` readout), plus Container, Resolution, and Audio options.
- **CompressO-Style Size Comparison** — Displays the final compression result from **`X MB/GB`** (white) to **`Y MB/GB`** (green) with percentage saved.
- **Windows Explorer Integration** — Right-click any video file and choose **Open in CompressIt** (or select it from **Open with**), with single-instance window reuse.
- **Built-In Auto-Updater** — Automatically checks GitHub Releases on startup (or manually in **Settings → Check for Updates**) with an in-app **Update Available** popup.

---

## Screenshots

### First-Run Setup & Startup
<p align="center">
  <img src="https://raw.githubusercontent.com/devyat009/image-repo-for-my-repo/refs/heads/main/CompressIt/readme/startUp.png" alt="CompressIt Startup" width="820" />
</p>

### Video Editing, Timeline Cutting & Compression
<p align="center">
  <img src="https://raw.githubusercontent.com/devyat009/image-repo-for-my-repo/refs/heads/main/CompressIt/readme/editing.png" alt="CompressIt Editing" width="820" />
</p>

---

## Installation

### Option 1: Windows Installer (Recommended)
1. Download the latest **`CompressIt-Setup-v*.exe`** from the [GitHub Releases](https://github.com/devyat009/compressIt/releases) page.
2. Run the installer and accept the Administrator prompt (installs cleanly to `C:\Program Files\CompressIt`).
3. Choose whether to create Desktop / Start Menu shortcuts and register **Open in CompressIt** in Windows Explorer.
4. Launch **CompressIt** — on first startup it will automatically download FFmpeg and detect your GPU.

### Option 2: Portable ZIP
1. Download **`CompressIt-win-x64-v*.zip`** from [GitHub Releases](https://github.com/devyat009/compressIt/releases).
2. Extract to any folder and run `CompressIt.exe`.

> **Note:** No separate .NET Runtime installation is required — Release builds are self-contained for `win-x64`.

---

## How to Build

### Prerequisites
- Windows 10 or Windows 11 (`x64`)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Clone & Build
```powershell
git clone https://github.com/devyat009/compressIt.git
cd compressIt

# Build the solution (Release)
dotnet build CompressIt.slnx --configuration Release

# Run unit & integration tests
dotnet test CompressIt.Tests\CompressIt.Tests.csproj --configuration Release --no-build
```

### Build Installer & Portable Release Packages
Run the release packaging script to generate `dist\CompressIt-Setup-v*.exe` and `dist\CompressIt-win-x64-v*.zip`:
```powershell
.\scripts\build-release.ps1
```

---

## Author

Created by **[Devyat009](https://github.com/devyat009/)**.

---

## License

This project is open source for noncommercial use only under the [PolyForm Noncommercial License 1.0.0](LICENSE) — free to use, study, modify, and share, **not permitted to be sold or used commercially** without the author's written permission.
