# ❄️ Snowy for Windows

**Snowy for Windows** is a lightweight, native C# and Win32 Per-Pixel Alpha interactive desktop winter simulation for Windows 10 and Windows 11. It brings real-time falling snow, mass-conservative window-top snow accumulation, snowball fights, buildable snowmen, a throwable snowplow, fairy lights, icicles, and campfires directly onto your Windows desktop.

Inspired by the macOS project [soloiaros/snowy-mac](https://github.com/soloiaros/snowy-mac/blob/main/Package.swift) by [Archie Auburn](https://github.com/soloiaros), built from scratch for Windows using native Win32 Layered Windows (`UpdateLayeredWindow`), Desktop Window Manager (`DWM`), and `SetWinEventHook` APIs with zero external dependencies.

## ✨ Key Features / Özellikler

* **Redesigned 2-Column 4-Tab Fluent Studio Control Panel (`v1.5`):** Wide, spacious frameless dark-glass dashboard (`668×610`) with Weather Radar, Physics Sliders, and Quick Actions on the left, plus 4 uncluttered right-side studio tabs (**Toys**, **Scene**, **Theme**, **System**).
* **Winter Desktop Wallpaper Switcher with Safe Auto-Restore (`🖼️`):** Switch your real Windows desktop wallpaper with one click between **5 embedded high-res winter themes** (Arctic Aurora Night, Full Moon Forest, Frozen Nordic Fjord, Festive Pine Bokeh, and Christmas-Lit Mountain Cabin) or pick **your own custom photo** — and Snowy **automatically backs up and restores your original desktop wallpaper** when you close the app or shut down Windows (with crash-safe `wallpaper_backup.ini` protection!).
* **Customizable & Resizable Christmas Trees (`🎄`):** Spawn decorated Christmas trees on your taskbar or any window ledge, drag them anywhere in Interactive Mode (`F8`), and **right-click any tree** to open the **dark-glass Christmas Tree Studio** with live preview, **60%–160% size slider**, 4 tree styles (Classic Pine, Snowy Pine, Ice White, Rose Gold), toppers (Gold Star, Snowflake, Red Bow), animated lights (Steady, Twinkle, Wave, **WASAPI Music Sync**), ornaments & color palette, garlands, and under-tree gift boxes — automatically saved & restored across restarts!
* **New Year Countdown Widget & Midnight Celebration (`⏳`, `🎉`):** Draggable translucent countdown card showing days/hours/minutes/seconds until the New Year; automatically erupts at midnight (or on demand via **Test Celebration** / right-click) with confetti, fireworks, jingle bells, and a glowing *"Happy New Year!"* banner.
* **Floating Wish Lanterns (`🏮`) & Winter Birds (`🐦`):** Warm glowing paper lanterns drift skyward with the wind (click in `F8` mode for a golden wish burst!), while adorable **Robins & Snowy Owls** fly across your desktop and perch on open windows and **your Christmas tree branches** (fluttering away when your mouse gets close).
* **Cursor Magic Trail (`✨`) & One-Click Winter Postcard (`📸 F10`):** Enable optional sparkling cursor ice trails, and press **`F10`** anytime to save a frost-framed PNG Winter Postcard straight to your Desktop.
* **Real-Time Window Surface Tracking (`SetWinEventHook` + `DWM`):** Detects open application windows (Spotify, File Explorer, VS Code, browsers) and the Windows Taskbar in real time at 60 FPS using `DWMWA_EXTENDED_FRAME_BOUNDS` and Z-order occlusion masking (`ExcludeClip`).
* **Mass-Conservative Continuum Snow Physics:** Fixed `1/60s` timestep physics with window-acceleration inertia sloshing, symmetric angle-of-repose avalanches, wind spindrift, and gravity snow spills when windows move, minimize, or go behind maximized apps.
* **True 32-Bit Per-Pixel Alpha Rendering:** Uses a persistent top-down `CreateDIBSection` buffer blitted via `UpdateLayeredWindow` (`ULW_ALPHA`) for zero-allocation per-frame rendering, depth-of-field bokeh snowflakes, and soft radial light bloom without dark halos.
* **Manual Snowman Building, Dress-Up Wardrobe, Colored Name Tags & AI Snowball Fights:** Roll snowballs across snow-covered windows (growing with volume conservation $r^3$) or spawn a snowman from the panel, right-click any snowman in Interactive Mode to open the **modern dark-glass Snowman Wardrobe** (hats, crowns, wizard hats, eyewear, expressions, scarves, vests, sweaters, capes, arms, color palettes, live preview, size slider & **customizable colored Name Tag badge**), persist/restore your dressed snowmen automatically across restarts, and watch multiple snowmen toss playful arcing snowballs at each other!
* **Desktop Penguins (`🐧`) & Polar Express Taskbar Train (`🚂`):** Penguins waddle, belly-slide, hop/chirp when clicked, and can be flung; the navy-and-gold Polar Express steam locomotive with warm-lit coaches chugs along the taskbar and toots its whistle when clicked or hit with a snowball.
* **Interactive Screen-Corner Frost (`❄️`), Zero-Dependency Sound FX & Music-Reactive Mode (`🔊`, `🎵`):** Organic radial frost & ice crystals grow in the 4 screen corners and melt near your cursor or campfire; built-in in-memory PCM WAV sound synthesizer + native Windows CoreAudio **WASAPI** peak metering syncs tree lights, Aurora Borealis, fairy lights, snowmen, and penguins to your Spotify / YouTube music.
* **Background Santa Claus & Reindeer (`🎅`), Northern Lights (`🌌`), Fireworks (`🎆`), Snowplow (`🚜`) & Multi-Monitor (`🖥️`):** Random Santa flybys dropping parachuting gifts, silky Aurora curtains, shooting stars, `F6` fireworks show, throwable snowplow, fairy lights, icicles, ski gondola cable car, campfires, up to 3 monitors, and Start with Windows.

## 🎮 Controls & Shortcuts / Kontroller ve Kısayollar

| Shortcut / Action | Mode | Description / Açıklama |
| :--- | :--- | :--- |
| **`F8`** | Global | Toggle between **Click-Through Mode** (work normally in your apps) and **Interactive Mode** (grab, throw & decorate). |
| **`F6`** | Global | Launch the **Winter Fireworks & Aurora Surprise Show** (and summon Santa!). |
| **`F10`** | Global | Capture a **Winter Postcard** screenshot with a frost frame directly to your Desktop (`PNG`). |
| **`F7`** | Global | Pause or resume the snow simulation. |
| **`F9`** | Global | Clear all accumulated snow, snowballs, snowmen, trees, gifts, and campfires. |
| **Right Click (on Tree `🎄`)** | Interactive (`F8`) | Open the **Christmas Tree Studio** to decorate style, topper, lights, ornaments, garland, gifts & resize (`60%–160%`). |
| **Right Click (on Snowman `⛄`)** | Interactive (`F8`) | Open the **Modern Snowman Wardrobe** to customize hat, eyewear, face, scarf, outfit, arms, colors, size & **colored Name Tag** live. |
| **Drag / Right Click (Countdown `⏳`)** | Interactive (`F8`) | Drag the New Year Countdown widget anywhere on screen, or right-click to trigger a celebration! |
| **Left Click (on Lantern `🏮` / Train `🚂`)** | Interactive (`F8`) | Pop a wish lantern for golden sparkles, or toot the Polar Express steam whistle! |
| **Click / Drag / Right Click (`🐧`)** | Interactive (`F8`) | Tap a penguin to make it hop & chirp, drag to fling it, or right-click to remove it. |
| **Left / Right Click (on Gift `🎁`)** | Interactive (`F8`) | Pop open Santa's parachuted gift box for fireworks and surprises! |
| **Middle Click (Anywhere)** | Interactive (`F8`) | Launch a festive firework straight to your cursor. |
| **Left Click + Drag (Snow / Air)** | Interactive (`F8`) | Roll & stack snowballs into snowmen, or pack a fresh snowball and fling it at snowmen, trees, gifts, or Santa! |
| **Left Click + Fling (`🚜` / `⛄` / `🎄` / `🔥`)** | Interactive (`F8`) | Grab and move or throw the snowplow, snowmen, Christmas trees, snowballs, or campfires across windows. |
| **Right Click (on Snowball / Fire)** | Interactive (`F8`) | Burst and remove the clicked snowball or campfire. |

## 🚀 Quick Start / Kurulum ve Çalıştırma

### Option 1: Prebuilt Windows Executable (GitHub Releases)

Download the latest **`SnowyWindows.exe`** directly from the [GitHub Releases](https://github.com/mertincircioglu/SnowyWindows/releases/latest) page and double-click to run — no installation required!

### Option 2: Zero-Setup One-Click Build (`Derle_ve_Calistir.bat`)

Windows 10 and Windows 11 include the native .NET Framework C# compiler (`csc.exe`) pre-installed out of the box. No IDE or SDK installation is required:

1. Clone or download this repository.
2. Double-click **`Derle_ve_Calistir.bat`**.
3. The script automatically compiles `SnowyWindows.cs`, embeds the 5 winter wallpapers and custom snowman `.ico` icon into `SnowyWindows.exe`, and launches the app.

### Option 3: .NET SDK / Visual Studio (`SnowyWindows.csproj`)

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
| **Desktop Wallpaper Engine** | Win32 `SystemParametersInfo` (`SPI_GETDESKWALLPAPER` / `SPI_SETDESKWALLPAPER`) + 5 embedded JPEG manifest resources + crash-safe `wallpaper_backup.ini` auto-restore on exit & Windows shutdown |
| **Procedural Audio & WASAPI** | In-memory 16-bit 22kHz PCM WAV synth (`winmm.dll` `PlaySound` with pinned `GCHandle`) + native CoreAudio `IAudioMeterInformation` COM interop (zero external DLLs) |
| **Memory & GC Profile** | Pre-allocated value-type `struct` particle pools (`Snowflake[]`, `PhysicalSnowChunk[]`, `TrainSmoke[]`, `WishLantern[]`, `ConfettiParticle[]`, `MagicSparkle[]`) + cached GDI+ brushes |
| **Persistence & Startup** | Automatic settings (`settings.ini`), saved snowmen & name tags (`snowmen.ini`), saved decorated Christmas trees (`trees.ini`), and wallpaper backup (`wallpaper_backup.ini`) in `%AppData%\SnowyWindows\` |

## 📄 License & Credits

* Licensed under the **MIT License** (see `LICENSE`).
* Inspired by [soloiaros/snowy-mac](https://github.com/soloiaros/snowy-mac/blob/main/Package.swift) created by [Archie Auburn](https://github.com/soloiaros) for macOS.
