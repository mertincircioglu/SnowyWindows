# ❄️ Snowy for Windows

**Snowy for Windows** is a lightweight, native C# and Win32 Per-Pixel Alpha interactive desktop winter simulation for Windows 10 and Windows 11. It brings real-time falling snow, mass-conservative window-top snow accumulation, snowball fights, buildable snowmen, a throwable snowplow, fairy lights, icicles, and campfires directly onto your Windows desktop.

Inspired by the macOS project [soloiaros/snowy-mac](https://github.com/soloiaros/snowy-mac/blob/main/Package.swift) by [Archie Auburn](https://github.com/soloiaros), built from scratch for Windows using native Win32 Layered Windows (`UpdateLayeredWindow`), Desktop Window Manager (`DWM`), and `SetWinEventHook` APIs with zero external dependencies.

## ✨ Key Features / Özellikler

* **Redesigned 2-Column Fluent Studio Control Panel (`v1.3`):** Wide, spacious frameless dark-glass dashboard (`632×574`) with Weather Radar & Physics sliders on the left and a clean 3-tab right studio (**Toys**, **Atmosphere & Audio**, **System & Save**) with zero scrolling.
* **Real-Time Window Surface Tracking (`SetWinEventHook` + `DWM`):** Detects open application windows (Spotify, File Explorer, VS Code, browsers) and the Windows Taskbar in real time at 60 FPS using `DWMWA_EXTENDED_FRAME_BOUNDS` and Z-order occlusion masking (`ExcludeClip`).
* **Mass-Conservative Continuum Snow Physics:** Fixed `1/60s` timestep physics with window-acceleration inertia sloshing, symmetric angle-of-repose avalanches, wind spindrift, and gravity snow spills when windows move, minimize, or go behind maximized apps.
* **True 32-Bit Per-Pixel Alpha Rendering:** Uses a persistent top-down `CreateDIBSection` buffer blitted via `UpdateLayeredWindow` (`ULW_ALPHA`) for zero-allocation per-frame rendering, depth-of-field bokeh snowflakes, and soft radial light bloom without dark halos.
* **Manual Snowman Building, Dress-Up Wardrobe, Colored Name Tags & AI Snowball Fights:** Roll snowballs across snow-covered windows (growing with volume conservation $r^3$) or spawn a snowman from the panel, right-click any snowman in Interactive Mode to open the **modern dark-glass Snowman Wardrobe** (hats, crowns, wizard hats, eyewear, expressions, scarves, vests, sweaters, capes, arms, color palettes, live preview, size slider & **customizable colored Name Tag badge**), persist/restore your dressed snowmen automatically across restarts, and watch multiple snowmen toss playful arcing snowballs at each other!
* **Desktop Penguins (`🐧`):** Waddle along the taskbar and window ledges, belly-slide (toboggan) spraying snow puffs, wave flippers, hop/chirp when clicked, and can be grabbed and flung across the screen.
* **Polar Express Taskbar Train (`🚂`):** Classic navy-and-gold steam locomotive with a glowing headlight cone, 2 warm-lit passenger coaches, spinning wheels, and rising steam rings chugging along your taskbar — click or hit it with a snowball to toot the steam whistle!
* **Interactive Screen-Corner Frost (`❄️`):** Soft radial frost vignettes and branching ice crystals gradually form in the 4 screen corners (faster in Blizzard mode) and melt smoothly whenever your mouse cursor or a campfire comes near.
* **Zero-Dependency Procedural Winter Sound FX & Music-Reactive Mode (`🔊`, `🎵`):** Built-in in-memory PCM WAV synthesizer (`winmm.dll`) for jingle bells, firework pops, snowball hits, gift chimes, penguin chirps, and train whistles — plus native Windows CoreAudio **WASAPI** peak metering so Aurora Borealis, fairy lights, snowmen, and penguins pulse to your Spotify / YouTube music with a live audio meter bar!
* **Background Santa Claus & Reindeer Flyby (`🎅`):** At random intervals (45–135s), Santa and 3 galloping reindeer (led by glowing-nosed Rudolph) soar across the background sky leaving twinkling stardust and dropping **parachuting surprise gift boxes (`🎁`)** onto your windows.
* **Northern Lights, Shooting Stars & Winter Fireworks (`🌌`, `🎆`):** Features silky Aurora Borealis curtains, occasional shooting stars, interactive gift drops, and a choreographed **Winter Fireworks Surprise Show (`F6`)** plus middle-click sky fireworks.
* **Throwable & Bouncing Snowplow (`🚜`) & Winter Toys (`🎄`, `🧊`, `🔥`, `🚡`):** Includes a throwable snowplow, pendulum-swinging window fairy lights, growing and breakable window icicles, an inter-window ski gondola cable car, and a crackling campfire that locally melts nearby snowbanks and corner frost.
* **Multi-Monitor Support & Start with Windows (`🚀`):** Toggle seamlessly between primary-screen-only mode and multi-monitor mode (up to 3 displays), and optionally launch automatically with Windows via the System tab.

## 🎮 Controls & Shortcuts / Kontroller ve Kısayollar

| Shortcut / Action | Mode | Description / Açıklama |
| :--- | :--- | :--- |
| **`F8`** | Global | Toggle between **Click-Through Mode** (work normally in your apps) and **Interactive Mode** (grab, throw & dress up). |
| **`F7`** | Global | Pause or resume the snow simulation. |
| **`F9`** | Global | Clear all accumulated snow, snowballs, snowmen, gifts, and campfires. |
| **`F6`** | Global | Launch the **Winter Fireworks & Aurora Surprise Show** (and summon Santa!). |
| **Right Click (on Snowman `⛄`)** | Interactive (`F8`) | Open the **Modern Snowman Wardrobe** to customize hat, eyewear, face, scarf, outfit, arms, colors, size & **colored Name Tag** live. |
| **Click / Drag / Right Click (`🐧`)** | Interactive (`F8`) | Tap a penguin to make it hop & chirp, drag to fling it, or right-click to remove it. |
| **Left Click (on Train `🚂`)** | Interactive (`F8`) | Toot the Polar Express steam whistle and burst extra steam rings! |
| **Left / Right Click (on Gift `🎁`)** | Interactive (`F8`) | Pop open Santa's parachuted gift box for fireworks and surprises! |
| **Middle Click (Anywhere)** | Interactive (`F8`) | Launch a festive firework straight to your cursor. |
| **Left Click + Drag (on Snow)** | Interactive (`F8`) | Roll a snowball along a window ledge to grow it, or stack 3 snowballs to build a snowman (`⛄`). |
| **Left Click + Drag (Empty Air)** | Interactive (`F8`) | Instantly pack a fresh snowball in your hand and fling it at snowmen, gifts, the Polar Express, or Santa's sleigh. |
| **Left Click + Fling (`🚜` / `⛄` / `🔥`)** | Interactive (`F8`) | Grab and throw the snowplow, snowballs, snowmen, or campfires across the screen. |
| **Right Click (on Snowball / Fire)** | Interactive (`F8`) | Burst and remove the clicked snowball or campfire. |

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
| **Procedural Audio & WASAPI** | In-memory 16-bit 22kHz PCM WAV synth (`winmm.dll` `PlaySound` with pinned `GCHandle`) + native CoreAudio `IAudioMeterInformation` COM interop (zero external DLLs) |
| **Memory & GC Profile** | Pre-allocated value-type `struct` particle pools (`Snowflake[]`, `PhysicalSnowChunk[]`, `TrainSmoke[]`) + cached GDI+ brushes and curve buffers |
| **Persistence & Startup** | Automatic settings (`%AppData%\SnowyWindows\settings.ini`), saved dressed snowmen & name tags (`%AppData%\SnowyWindows\snowmen.ini`), and optional `HKCU\...\Run` Windows startup |

## 📄 License & Credits

* Licensed under the **MIT License** (see `LICENSE`).
* Inspired by [soloiaros/snowy-mac](https://github.com/soloiaros/snowy-mac/blob/main/Package.swift) created by [Archie Auburn](https://github.com/soloiaros) for macOS.
