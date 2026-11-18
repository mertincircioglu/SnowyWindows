# ❄️ Snowy for Windows

**Snowy for Windows** is a lightweight, native C# and Win32 Per-Pixel Alpha interactive desktop winter simulation for Windows 10 and Windows 11. It brings real-time falling snow, mass-conservative window-top snow accumulation, snowball fights, buildable snowmen, a throwable snowplow, fairy lights, icicles, and campfires directly onto your Windows desktop.

Inspired by the macOS project [soloiaros/snowy-mac](https://github.com/soloiaros/snowy-mac/blob/main/Package.swift) by [Archie Auburn](https://github.com/soloiaros), built from scratch for Windows using native Win32 Layered Windows (`UpdateLayeredWindow`), Desktop Window Manager (`DWM`), and `SetWinEventHook` APIs with zero external dependencies.

## ✨ Key Features / Özellikler

* **Real-Time Window Surface Tracking (`SetWinEventHook` + `DWM`):** Detects open application windows (Spotify, File Explorer, VS Code, browsers) and the Windows Taskbar in real time at 60 FPS using `DWMWA_EXTENDED_FRAME_BOUNDS` and Z-order occlusion masking (`ExcludeClip`).
* **Mass-Conservative Continuum Snow Physics:** Fixed `1/60s` timestep physics with window-acceleration inertia sloshing, symmetric angle-of-repose avalanches, wind spindrift, and gravity snow spills when windows move, minimize, or go behind maximized apps.
* **True 32-Bit Per-Pixel Alpha Rendering:** Uses a persistent top-down `CreateDIBSection` buffer blitted via `UpdateLayeredWindow` (`ULW_ALPHA`) for zero-allocation per-frame rendering, depth-of-field bokeh snowflakes, and soft radial light bloom without dark halos.
* **Manual Snowman Building & Snowball Fight:** Roll snowballs across snow-covered windows (growing with volume conservation $r^3$), stack 3 snowballs to build a snowman with a winter beanie or top hat, and throw snowballs to knock off its hat and head or shatter it back into 3 snowballs.
* **Throwable & Bouncing Snowplow (`🚜`):** Drives along the taskbar and window ledges pushing a growing curl of snow in front of its blade, and can be grabbed and flung across the screen to bounce off monitor and window borders.
* **Interactive Winter Toys (`🎄`, `🧊`, `🔥`):** Includes pendulum-swinging window fairy lights, growing and breakable window icicles, and a crackling campfire that locally melts nearby snowbanks.
* **Multi-Monitor Support (Up to 3 Displays):** Toggle seamlessly between primary-screen-only mode and multi-monitor mode spanning up to 3 connected displays.

## 🎮 Controls & Shortcuts / Kontroller ve Kısayollar

| Shortcut / Action | Mode | Description / Açıklama |
| :--- | :--- | :--- |
| **`F8`** | Global | Toggle between **Click-Through Mode** (work normally in your apps) and **Interactive Mode** (grab & throw objects). |
| **`F7`** | Global | Pause or resume the snow simulation. |
| **`F9`** | Global | Clear all accumulated snow, snowballs, snowmen, and campfires. |
| **Left Click + Drag (on Snow)** | Interactive (`F8`) | Roll a snowball along a window ledge to grow it, or stack 3 snowballs to build a snowman (`⛄`). |
| **Left Click + Drag (Empty Air)** | Interactive (`F8`) | Instantly pack a fresh snowball in your hand and fling it at snowmen or campfires. |
| **Left Click + Fling (`🚜` / `⛄` / `🔥`)** | Interactive (`F8`) | Grab and throw the snowplow, snowballs, snowmen, or campfires across the screen. |
| **Right Click (on Object)** | Interactive (`F8`) | Burst and remove the clicked snowman, snowball, or campfire. |

## 🚀 Quick Start / Kurulum ve Çalıştırma

### Option 1: Zero-Setup One-Click Build (`Derle_ve_Calistir.bat`)

Windows 10 and Windows 11 include the native .NET Framework C# compiler (`csc.exe`) pre-installed out of the box. No IDE or SDK installation is required:

1. Clone or download this repository.
2. Double-click **`Derle_ve_Calistir.bat`**.
3. The script automatically compiles `SnowyWindows.cs`, generates and embeds the custom snowman `.ico` icon into `SnowyWindows.exe`, and launches the app.

### Option 2: .NET SDK / Visual Studio (`SnowyWindows.csproj`)

If you have the .NET SDK or Visual Studio installed:

```bash
dotnet build -c Release
dotnet run -c Release
```

## 🛠️ Technical Architecture / Teknik Mimari

| Subsystem | Implementation Detail |
| :--- | :--- |
| **Overlay Compositor** | Win32 `WS_EX_LAYERED \| WS_EX_TRANSPARENT \| WS_EX_TOOLWINDOW` + `UpdateLayeredWindow` (`AC_SRC_ALPHA`) |
| **Window & Z-Order Engine** | `SetWinEventHook` (`EVENT_SYSTEM_FOREGROUND`, `EVENT_OBJECT_LOCATIONCHANGE`, `EVENT_OBJECT_SHOW/HIDE`) + `EnumWindows` + `DwmGetWindowAttribute` |
| **Memory & GC Profile** | Pre-allocated value-type `struct` particle pools (`Snowflake[]`, `PhysicalSnowChunk[]`) + cached GDI+ brushes and curve buffers |
| **Persistence** | Automatic configuration saving in `%AppData%\SnowyWindows\settings.ini` |

## 📄 License & Credits

* Licensed under the **MIT License** (see `LICENSE`).
* Inspired by [soloiaros/snowy-mac](https://github.com/soloiaros/snowy-mac/blob/main/Package.swift) created by [Archie Auburn](https://github.com/soloiaros) for macOS.
