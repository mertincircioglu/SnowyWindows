// ============================================================================
// Snowy for Windows (SnowyWindows.cs)
// ============================================================================
// Native C# & Win32 Per-Pixel Alpha interactive desktop snow simulation
// for Windows 10 & Windows 11.
//
// Inspired by Snowy for macOS (https://github.com/soloiaros/snowy-mac).
//
// Architecture Highlights:
// - Rendering Engine: Zero-allocation 32-bit premultiplied ARGB DIBSection
//   blitted directly via Win32 UpdateLayeredWindow (ULW_ALPHA) at 60 FPS.
// - Window Tracking: Event-driven SetWinEventHook (foreground, show/hide,
//   cloak, location change) combined with DwmGetWindowAttribute
//   (DWMWA_EXTENDED_FRAME_BOUNDS) and Z-order occlusion masking.
// - Continuum Snow Physics: Fixed 1/60s timestep mass-conservative advection
//   flux solver, two-pass symmetric angle-of-repose avalanche solver,
//   wind spindrift, and volume-conserving (r^3) snowball growth.
// - Multi-Monitor Support: Dynamically spans up to 3 connected displays
//   with per-monitor DPI awareness and coordinate mapping.
//
// Build (Zero-Dependency via Windows Built-in C# Compiler):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize+ /out:SnowyWindows.exe SnowyWindows.cs
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnowyWindows
{
    // ========================================================================
    // 1. WIN32 & DWM NATIVE API TANIMLAMALARI
    // ========================================================================
    internal static class NativeMethods
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TOPMOST = 0x00000008;

        public const int ULW_ALPHA = 0x00000002;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;

        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int DWMWA_CLOAKED = 14;

        public const int VK_F6 = 0x75;
        public const int VK_F7 = 0x76;
        public const int VK_F8 = 0x77;
        public const int VK_F9 = 0x78;
        public const int VK_F10 = 0x79;

        // SetWinEventHook sabitleri (Spotify/Discord gibi sonradan açılan/öne gelen pencereleri 0 gecikmeyle yakalar)
        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
        public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        public const uint EVENT_OBJECT_CREATE = 0x8000;
        public const uint EVENT_OBJECT_DESTROY = 0x8001;
        public const uint EVENT_OBJECT_SHOW = 0x8002;
        public const uint EVENT_OBJECT_HIDE = 0x8003;
        public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        public const uint EVENT_OBJECT_CLOAKED = 0x8017;
        public const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
        public const int OBJID_WINDOW = 0;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        public const uint GA_ROOT = 2;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public delegate void WinEventDelegate(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool UpdateLayeredWindow(
            IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, uint crKey,
            [In] ref BLENDFUNCTION pblend, uint dwFlags);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr CreateDIBSection(
            IntPtr hdc, [In] ref BITMAPINFO pbmi, uint pila,
            out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLengthW(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_ROUND = 2;

        [DllImport("shcore.dll")]
        public static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);

        public const uint SND_ASYNC = 0x0001;
        public const uint SND_NODEFAULT = 0x0002;
        public const uint SND_MEMORY = 0x0004;

        public const uint SPI_GETDESKWALLPAPER = 0x0073;
        public const uint SPI_SETDESKWALLPAPER = 0x0014;
        public const uint SPIF_UPDATEINIFILE = 0x01;
        public const uint SPIF_SENDCHANGE = 0x02;

        public const int SRCCOPY = 0x00CC0020;
        public const int CAPTUREBLT = 0x40000000;

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool BitBlt(
            IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
            IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, System.Text.StringBuilder pvParam, uint fWinIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

        public static bool IsWindowCloaked(IntPtr hWnd)
        {
            int cloaked = 0;
            DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
            return cloaked != 0;
        }

        public static bool TryGetExtendedFrameBounds(IntPtr hWnd, out RECT rect)
        {
            int hr = DwmGetWindowAttribute(
                hWnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                out rect, Marshal.SizeOf(typeof(RECT)));
            if (hr != 0)
                return GetWindowRect(hWnd, out rect);
            return true;
        }
    }

    // ========================================================================
    // 1b. HARİCİ DOSYASIZ PROSEDÜREL KIŞ SES MOTORU (BELLEK İÇİ PCM WAV)
    // ========================================================================
    internal sealed class WinterAudioSynth : IDisposable
    {
        private readonly GCHandle[] _handles = new GCHandle[6];
        private readonly IntPtr[] _ptrs = new IntPtr[6];
        private readonly Stopwatch _cooldown = Stopwatch.StartNew();
        private long _lastPlayMs = -500;
        private bool _disposed;

        public WinterAudioSynth()
        {
            try
            {
                PinSound(0, BuildJingleBellsWav());
                PinSound(1, BuildFireworkPopWav());
                PinSound(2, BuildSnowballHitWav());
                PinSound(3, BuildGiftChimeWav());
                PinSound(4, BuildPenguinChirpWav());
                PinSound(5, BuildTrainWhistleWav());
            }
            catch { }
        }

        private void PinSound(int slot, byte[] wav)
        {
            if (wav == null || wav.Length == 0) return;
            _handles[slot] = GCHandle.Alloc(wav, GCHandleType.Pinned);
            _ptrs[slot] = _handles[slot].AddrOfPinnedObject();
        }

        public void Play(int slot)
        {
            if (_disposed || slot < 0 || slot >= _ptrs.Length || _ptrs[slot] == IntPtr.Zero) return;
            long now = _cooldown.ElapsedMilliseconds;
            if (now - _lastPlayMs < 75) return;
            _lastPlayMs = now;
            try
            {
                NativeMethods.PlaySound(
                    _ptrs[slot], IntPtr.Zero,
                    NativeMethods.SND_MEMORY | NativeMethods.SND_ASYNC | NativeMethods.SND_NODEFAULT);
            }
            catch { }
        }

        public void PlayJingleBells() { Play(0); }
        public void PlayFireworkPop() { Play(1); }
        public void PlaySnowHit() { Play(2); }
        public void PlayGiftOpen() { Play(3); }
        public void PlayPenguinChirp() { Play(4); }
        public void PlayTrainWhistle() { Play(5); }

        private static byte[] EncodePcm16Wav(short[] samples, int sampleRate)
        {
            int dataBytes = samples.Length * 2;
            byte[] buf = new byte[44 + dataBytes];
            using (System.IO.MemoryStream ms = new System.IO.MemoryStream(buf))
            using (System.IO.BinaryWriter bw = new System.IO.BinaryWriter(ms))
            {
                bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + dataBytes);
                bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16);
                bw.Write((short)1); // PCM
                bw.Write((short)1); // Mono
                bw.Write(sampleRate);
                bw.Write(sampleRate * 2);
                bw.Write((short)2);
                bw.Write((short)16);
                bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                bw.Write(dataBytes);
                for (int i = 0; i < samples.Length; i++)
                    bw.Write(samples[i]);
            }
            return buf;
        }

        private static byte[] BuildJingleBellsWav()
        {
            const int sr = 22050;
            int n = (int)(sr * 0.62);
            short[] s = new short[n];
            double[] notes = new double[] { 1318.5, 1318.5, 1318.5, 1568.0 }; // E6 E6 E6 G6
            double[] starts = new double[] { 0.0, 0.13, 0.26, 0.39 };
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / sr;
                double v = 0.0;
                for (int k = 0; k < notes.Length; k++)
                {
                    double dt = t - starts[k];
                    if (dt < 0.0 || dt > 0.28) continue;
                    double env = Math.Exp(-dt * 15.0) * Math.Min(1.0, dt * 120.0);
                    double f = notes[k];
                    v += env * (Math.Sin(2.0 * Math.PI * f * dt) * 0.65 +
                                Math.Sin(2.0 * Math.PI * f * 2.02 * dt) * 0.25 +
                                Math.Sin(2.0 * Math.PI * f * 3.11 * dt) * 0.10);
                }
                s[i] = (short)Math.Max(-28000, Math.Min(28000, (int)(v * 7200.0)));
            }
            return EncodePcm16Wav(s, sr);
        }

        private static byte[] BuildFireworkPopWav()
        {
            const int sr = 22050;
            int n = (int)(sr * 0.44);
            short[] s = new short[n];
            Random rng = new Random(1337);
            double lp = 0.0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / sr;
                double boomEnv = Math.Exp(-t * 14.0);
                double freq = 95.0 - t * 90.0;
                if (freq < 35.0) freq = 35.0;
                double boom = Math.Sin(2.0 * Math.PI * freq * t) * boomEnv * 0.72;
                double noise = (rng.NextDouble() * 2.0 - 1.0);
                lp = lp * 0.68 + noise * 0.32;
                double crackle = lp * Math.Exp(-t * 8.5) * 0.48;
                s[i] = (short)Math.Max(-28000, Math.Min(28000, (int)((boom + crackle) * 9500.0)));
            }
            return EncodePcm16Wav(s, sr);
        }

        private static byte[] BuildSnowballHitWav()
        {
            const int sr = 22050;
            int n = (int)(sr * 0.14);
            short[] s = new short[n];
            Random rng = new Random(77);
            double lp = 0.0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / sr;
                double env = Math.Exp(-t * 28.0) * Math.Min(1.0, t * 240.0);
                double noise = rng.NextDouble() * 2.0 - 1.0;
                lp = lp * 0.78 + noise * 0.22;
                double thud = Math.Sin(2.0 * Math.PI * (150.0 - t * 400.0) * t) * 0.45;
                s[i] = (short)Math.Max(-28000, Math.Min(28000, (int)((lp * 0.75 + thud) * env * 9000.0)));
            }
            return EncodePcm16Wav(s, sr);
        }

        private static byte[] BuildGiftChimeWav()
        {
            const int sr = 22050;
            int n = (int)(sr * 0.52);
            short[] s = new short[n];
            double[] notes = new double[] { 1046.5, 1318.5, 1568.0, 2093.0 }; // C6 E6 G6 C7
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / sr;
                double v = 0.0;
                for (int k = 0; k < notes.Length; k++)
                {
                    double dt = t - k * 0.075;
                    if (dt < 0.0) continue;
                    double env = Math.Exp(-dt * 11.0) * Math.Min(1.0, dt * 160.0);
                    v += env * (Math.Sin(2.0 * Math.PI * notes[k] * dt) * 0.75 +
                                Math.Sin(2.0 * Math.PI * notes[k] * 2.0 * dt) * 0.25);
                }
                s[i] = (short)Math.Max(-28000, Math.Min(28000, (int)(v * 7000.0)));
            }
            return EncodePcm16Wav(s, sr);
        }

        private static byte[] BuildPenguinChirpWav()
        {
            const int sr = 22050;
            int n = (int)(sr * 0.19);
            short[] s = new short[n];
            double phase = 0.0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / sr;
                double env = Math.Sin(Math.PI * (t / 0.19)) * Math.Exp(-t * 4.5);
                double f = 880.0 + 520.0 * Math.Sin(Math.PI * (t / 0.19));
                phase += (2.0 * Math.PI * f) / sr;
                double v = (Math.Sin(phase) * 0.7 + Math.Sin(phase * 2.0) * 0.22) * env;
                s[i] = (short)Math.Max(-28000, Math.Min(28000, (int)(v * 7500.0)));
            }
            return EncodePcm16Wav(s, sr);
        }

        private static byte[] BuildTrainWhistleWav()
        {
            const int sr = 22050;
            int n = (int)(sr * 0.56);
            short[] s = new short[n];
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / sr;
                double env = Math.Min(1.0, t * 18.0) * Math.Min(1.0, (0.56 - t) * 7.5);
                double vib = 1.0 + 0.008 * Math.Sin(2.0 * Math.PI * 6.0 * t);
                double v = (Math.Sin(2.0 * Math.PI * 349.2 * vib * t) * 0.45 +
                            Math.Sin(2.0 * Math.PI * 440.0 * vib * t) * 0.35 +
                            Math.Sin(2.0 * Math.PI * 523.3 * vib * t) * 0.25) * env;
                s[i] = (short)Math.Max(-28000, Math.Min(28000, (int)(v * 6500.0)));
            }
            return EncodePcm16Wav(s, sr);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = 0; i < _handles.Length; i++)
            {
                if (_handles[i].IsAllocated)
                    _handles[i].Free();
                _ptrs[i] = IntPtr.Zero;
            }
        }
    }

    // ========================================================================
    // 1c. WINDOWS COREAUDIO (WASAPI) MÜZİK RİTMİ ÖLÇER (HARİCİ DLL YOK)
    // ========================================================================
    internal sealed class SystemAudioMeter : IDisposable
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            int GetPeakValue(out float pfPeak);
        }

        private IAudioMeterInformation _meter;
        private int _retryCooldown;

        public float ReadPeakLevel()
        {
            try
            {
                if (_meter == null)
                {
                    if (_retryCooldown > 0)
                    {
                        _retryCooldown--;
                        return 0f;
                    }
                    InitMeter();
                    if (_meter == null)
                    {
                        _retryCooldown = 90;
                        return 0f;
                    }
                }
                float peak;
                int hr = _meter.GetPeakValue(out peak);
                if (hr != 0)
                {
                    ReleaseMeter();
                    _retryCooldown = 60;
                    return 0f;
                }
                return Math.Max(0f, Math.Min(1f, peak));
            }
            catch
            {
                ReleaseMeter();
                _retryCooldown = 90;
                return 0f;
            }
        }

        private void InitMeter()
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                int hr = enumerator.GetDefaultAudioEndpoint(0, 1, out device); // eRender, eMultimedia
                if (hr != 0 || device == null) return;
                Guid iidMeter = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");
                object meterObj;
                hr = device.Activate(ref iidMeter, 23, IntPtr.Zero, out meterObj);
                if (hr == 0 && meterObj != null)
                    _meter = (IAudioMeterInformation)meterObj;
            }
            catch
            {
                ReleaseMeter();
            }
            finally
            {
                if (device != null) try { Marshal.ReleaseComObject(device); } catch { }
                if (enumerator != null) try { Marshal.ReleaseComObject(enumerator); } catch { }
            }
        }

        private void ReleaseMeter()
        {
            if (_meter != null)
            {
                try { Marshal.ReleaseComObject(_meter); } catch { }
                _meter = null;
            }
        }

        public void Dispose()
        {
            ReleaseMeter();
        }
    }

    // ========================================================================
    // 1d. WINDOWS BAŞLANGIÇ YÖNETİCİSİ (HKCU\...\Run)
    // ========================================================================
    internal static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppValueName = "SnowyWindows";

        public static bool IsEnabled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    object val = key.GetValue(AppValueName);
                    return val != null && !string.IsNullOrEmpty(val.ToString());
                }
            }
            catch { return false; }
        }

        public static bool SetEnabled(bool enable)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return false;
                    if (enable)
                        key.SetValue(AppValueName, "\"" + Application.ExecutablePath + "\"");
                    else
                        key.DeleteValue(AppValueName, false);
                    return true;
                }
            }
            catch { return false; }
        }
    }

    internal static class WallpaperManager
    {
        private static readonly string[] ThemeResources = new string[]
        {
            "",
            "wp_arctic_night.jpg",   // 1: 1A — Kuzey Işıklı Kutup Gecesi
            "wp_full_moon.jpg",      // 2: 2A — Dolunaylı Sisli Çam Vadisi
            "wp_arctic_fjord.jpg",   // 3: 3B — Buzlu İskandinav Fiyordu
            "wp_festive_bokeh.jpg",  // 4: 4B — Karlı Çam Dalları & Yılbaşı Bokeh
            "wp_mountain_cabin.jpg"  // 5: 5A — Yılbaşı Işıklı Kütük Dağ Evi
        };

        private static string AppDir
        {
            get
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnowyWindows");
            }
        }

        private static string CacheDir
        {
            get { return System.IO.Path.Combine(AppDir, "wallpapers"); }
        }

        private static string BackupFilePath
        {
            get { return System.IO.Path.Combine(AppDir, "wallpaper_backup.ini"); }
        }

        private static bool _hasAppliedThisSession;
        private static string _origPath;
        private static string _origStyle;
        private static string _origTile;

        public static string GetCurrentWindowsWallpaper()
        {
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(1024);
                if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
                    return sb.ToString();
            }
            catch { }
            return "";
        }

        private static bool IsOurManagedWallpaper(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string full = System.IO.Path.GetFullPath(path);
                string cache = System.IO.Path.GetFullPath(CacheDir);
                return full.StartsWith(cache, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void EnsureOriginalBackupSaved()
        {
            try
            {
                // Diskte zaten bir yedek varsa (ör. önceki oturum beklenmedik kapandıysa) onu koru ve belleğe yükle!
                if (System.IO.File.Exists(BackupFilePath))
                {
                    if (_origPath == null)
                        LoadBackupFromDisk();
                    return;
                }

                string curPath = GetCurrentWindowsWallpaper();
                if (IsOurManagedWallpaper(curPath))
                    return;

                string style = "10";
                string tile = "0";
                try
                {
                    using (Microsoft.Win32.RegistryKey rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false))
                    {
                        if (rk != null)
                        {
                            object sv = rk.GetValue("WallpaperStyle");
                            object tv = rk.GetValue("TileWallpaper");
                            if (sv != null) style = sv.ToString();
                            if (tv != null) tile = tv.ToString();
                        }
                    }
                }
                catch { }

                _origPath = curPath ?? "";
                _origStyle = style;
                _origTile = tile;

                System.IO.Directory.CreateDirectory(AppDir);
                string pathB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(_origPath));
                System.IO.File.WriteAllLines(BackupFilePath, new string[]
                {
                    "pathB64=" + pathB64,
                    "style=" + _origStyle,
                    "tile=" + _origTile
                });
            }
            catch { }
        }

        private static bool LoadBackupFromDisk()
        {
            try
            {
                if (!System.IO.File.Exists(BackupFilePath)) return false;
                string[] lines = System.IO.File.ReadAllLines(BackupFilePath);
                string p = "";
                string st = "10";
                string tl = "0";
                for (int i = 0; i < lines.Length; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq <= 0) continue;
                    string k = lines[i].Substring(0, eq).Trim();
                    string v = lines[i].Substring(eq + 1).Trim();
                    if (k == "pathB64")
                        p = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(v));
                    else if (k == "style")
                        st = v;
                    else if (k == "tile")
                        tl = v;
                }
                _origPath = p;
                _origStyle = st;
                _origTile = tl;
                return true;
            }
            catch { return false; }
        }

        private static string ExtractEmbeddedWallpaper(int themeIndex)
        {
            if (themeIndex < 1 || themeIndex >= ThemeResources.Length) return null;
            string resName = ThemeResources[themeIndex];
            string outPath = System.IO.Path.Combine(CacheDir, resName);
            System.IO.Directory.CreateDirectory(CacheDir);

            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (System.IO.Stream st = asm.GetManifestResourceStream(resName))
                {
                    if (st != null)
                    {
                        using (System.IO.FileStream fs = new System.IO.FileStream(outPath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                        {
                            byte[] buf = new byte[32768];
                            int read;
                            while ((read = st.Read(buf, 0, buf.Length)) > 0)
                                fs.Write(buf, 0, read);
                        }
                        return outPath;
                    }
                }
            }
            catch { }

            // Yedek yol: EXE yanındaki wallpapers/ klasörü
            string sidePath = System.IO.Path.Combine(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wallpapers"), resName);
            if (System.IO.File.Exists(sidePath))
            {
                try
                {
                    System.IO.File.Copy(sidePath, outPath, true);
                    return outPath;
                }
                catch { return sidePath; }
            }
            return null;
        }

        private static void SetWallpaperStyle(string style, string tile)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                {
                    if (rk != null)
                    {
                        rk.SetValue("WallpaperStyle", style ?? "10");
                        rk.SetValue("TileWallpaper", tile ?? "0");
                    }
                }
            }
            catch { }
        }

        public static bool ApplyTheme(int themeIndex, string customPath)
        {
            if (themeIndex <= 0)
            {
                RestoreOriginal();
                return true;
            }

            string targetPath = null;
            if (themeIndex >= 1 && themeIndex <= 5)
            {
                targetPath = ExtractEmbeddedWallpaper(themeIndex);
            }
            else if (themeIndex == 6 && !string.IsNullOrEmpty(customPath) && System.IO.File.Exists(customPath))
            {
                // Özel fotoğrafı kendi önbelleğimize kopyalayarak uygula (orijinal yedek kontrolüyle çakışmasın)
                try
                {
                    System.IO.Directory.CreateDirectory(CacheDir);
                    string ext = System.IO.Path.GetExtension(customPath);
                    if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                    targetPath = System.IO.Path.Combine(CacheDir, "wp_custom_user" + ext);
                    System.IO.File.Copy(customPath, targetPath, true);
                }
                catch
                {
                    targetPath = customPath;
                }
            }

            if (string.IsNullOrEmpty(targetPath) || !System.IO.File.Exists(targetPath))
                return false;

            EnsureOriginalBackupSaved();
            SetWallpaperStyle("10", "0"); // Fill (tam ekran orantılı doldurma)
            bool ok = NativeMethods.SystemParametersInfo(
                NativeMethods.SPI_SETDESKWALLPAPER, 0, targetPath,
                NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);
            if (ok) _hasAppliedThisSession = true;
            return ok;
        }

        public static void RestoreOriginal()
        {
            try
            {
                bool hasBackup = (_origPath != null) || LoadBackupFromDisk();
                if (!hasBackup && !_hasAppliedThisSession)
                    return;

                string restorePath = _origPath ?? "";
                if (!IsOurManagedWallpaper(restorePath))
                {
                    SetWallpaperStyle(_origStyle ?? "10", _origTile ?? "0");
                    NativeMethods.SystemParametersInfo(
                        NativeMethods.SPI_SETDESKWALLPAPER, 0, restorePath,
                        NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);
                }

                _hasAppliedThisSession = false;
                _origPath = null;
                if (System.IO.File.Exists(BackupFilePath))
                    System.IO.File.Delete(BackupFilePath);
            }
            catch { }
        }
    }

    // ========================================================================
    // 2. FİZİKSEL PARÇACIK YAPILARI
    // ========================================================================
    internal struct Snowflake
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float Size;
        public float Phase;
        public float SpeedMult;
        public byte BaseAlpha;
        public byte Layer; // 0: Arka plan bokeh, 1: Orta, 2: Ön plan kristal
        public float Angle;
        public float SpinSpeed;
    }

    internal struct PhysicalSnowChunk
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float Size;
        public float Mass;
        public int Ttl;
        public int MaxTtl;
        public bool CanAccumulate; // Düştüğü pencerenin üstünde tekrar kar yığınına katılır
        public Color Tint;
        public bool Active;
    }

    internal struct FlyingHat
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float Angle;
        public float AngularVel;
        public float Scale;
        public int HatStyle; // 0: Ponponlu Kırmızı Bere, 1: Silindir Şapka, 2: Ponponlu Mavi/Yeşil Bere
        public Color HatColor;
        public int Ttl;
        public bool Active;
    }

    internal struct FallingIcicle
    {
        public float X;
        public float Y;
        public float Vy;
        public float Length;
        public float Width;
        public bool Active;
    }

    internal sealed class CampfireEntity
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public IntPtr SurfaceHwnd;
        public bool Dragging;
        public float FlamePhase;

        public CampfireEntity(float x, float y, IntPtr surfaceHwnd)
        {
            X = x;
            Y = y;
            SurfaceHwnd = surfaceHwnd;
        }
    }

    internal struct FloatingPopup
    {
        public float X;
        public float Y;
        public float Vy;
        public string Text;
        public Color Color;
        public int Ttl;
        public int MaxTtl;
        public bool Active;
    }

    internal struct SantaSparkle
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float Size;
        public Color Color;
        public int Ttl;
        public int MaxTtl;
        public bool Active;
    }

    internal struct ShootingStar
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float TailLen;
        public int Ttl;
        public int MaxTtl;
        public Color Color;
        public bool Active;
    }

    internal struct FireworkRocket
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float TargetY;
        public Color PrimaryColor;
        public Color SecondaryColor;
        public int Pattern; // 0: Klasik Küre, 1: Çift Halka, 2: Altın Salkım (Willow)
        public bool Active;
    }

    internal struct FireworkSpark
    {
        public float X;
        public float Y;
        public float PrevX;
        public float PrevY;
        public float Vx;
        public float Vy;
        public float Drag;
        public float Gravity;
        public float Size;
        public Color Color;
        public int Ttl;
        public int MaxTtl;
        public bool Twinkle;
        public bool Active;
    }

    internal sealed class GiftBoxEntity
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float SwayPhase;
        public float Size;
        public bool HasParachute;
        public IntPtr SurfaceHwnd;
        public Color BoxColor;
        public Color RibbonColor;
        public int LifeTicks;
        public float PulsePhase;

        public GiftBoxEntity(float x, float y, Random rng)
        {
            X = x;
            Y = y;
            Vx = (float)(rng.NextDouble() * 0.6 - 0.3);
            Vy = 1.05f + (float)rng.NextDouble() * 0.35f;
            SwayPhase = (float)(rng.NextDouble() * Math.PI * 2.0);
            Size = 18f + (float)rng.NextDouble() * 4f;
            HasParachute = true;
            SurfaceHwnd = IntPtr.Zero;
            LifeTicks = 60 * 75; // 75 saniye sonra nazikçe kaybolur

            Color[] boxes = {
                Color.FromArgb(248, 220, 38, 38),
                Color.FromArgb(248, 37, 99, 235),
                Color.FromArgb(248, 16, 185, 129),
                Color.FromArgb(248, 147, 51, 234),
                Color.FromArgb(248, 217, 70, 239)
            };
            Color[] ribbons = {
                Color.FromArgb(255, 250, 204, 21),
                Color.FromArgb(255, 248, 250, 252),
                Color.FromArgb(255, 251, 146, 60),
                Color.FromArgb(255, 125, 211, 252)
            };
            BoxColor = boxes[rng.Next(boxes.Length)];
            RibbonColor = ribbons[rng.Next(ribbons.Length)];
        }
    }

    // ========================================================================
    // 3. YÜKSEK ÇÖZÜNÜRLÜKLÜ PENCERE KAR YÜZEYİ (SÜREKLİ FİZİK MOTORU)
    // ========================================================================
    internal sealed class WindowSurface
    {
        public const int BinSize = 2; // 2 piksel çözünürlüklü pürüzsüz fizik ızgarası
        public IntPtr Hwnd;
        public string Title;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int ZOrder;
        public bool IsTaskbar;
        public bool IsMaximized; // Tam ekran/maksimize pencere (örn. Opera GX): Üstüne kar almaz ama arkasındaki pencereleri kapatır!

        public float[] Heights;
        private float[] _delta;      // Akı / çığ hesabı için geçici tampon (kütle korunumlu)
        private float[] _smooth;     // Yumuşatma tamponu
        public PointF[] CurveBuf;    // Çizimde her karede yeniden kullanılan nokta tamponu
        public float MaxH;           // En yüksek kar sütunu (uyku optimizasyonu + çizim için)
        public float VelX;
        public float VelY;
        public float AccelX;         // Pencerenin ivmesi (eylemsizlik/sarsıntı için)
        public float AccelY;
        public float LightSwingAngle;
        public float LightSwingVel;
        public float LostLeft;       // Sol kenardan dökülen, henüz parçacığa dönüşmemiş kar
        public float LostRight;
        public float[] Icicles;      // Pencere üst kenarından aşağı sarkan buz sarkıtları

        public int Width { get { return Math.Max(16, Right - Left); } }

        public WindowSurface(IntPtr hwnd, string title, int left, int top, int right, int bottom, int zOrder, bool isTaskbar, bool isMaximized = false)
        {
            Hwnd = hwnd;
            Title = title;
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
            ZOrder = zOrder;
            IsTaskbar = isTaskbar;
            IsMaximized = isMaximized;
            int bins = Math.Max(8, Width / BinSize);
            Heights = new float[bins];
            _delta = new float[bins];
            _smooth = new float[bins];
            Icicles = new float[Math.Max(4, Width / 28)];
        }

        public void StepResizeIfNeeded(int newLeft, int newTop, int newRight, int newBottom)
        {
            Left = newLeft;
            Top = newTop;
            Right = newRight;
            Bottom = newBottom;

            int newBins = Math.Max(8, Width / BinSize);
            if (newBins != Heights.Length)
            {
                float[] nextH = new float[newBins];
                int oldLen = Heights.Length;
                for (int i = 0; i < newBins; i++)
                {
                    int srcIdx = Math.Min(oldLen - 1, (i * oldLen) / newBins);
                    nextH[i] = Heights[srcIdx];
                }
                Heights = nextH;
                _delta = new float[newBins];
                _smooth = new float[newBins];
                CurveBuf = null;
            }
            int icCount = Math.Max(4, Width / 28);
            if (Icicles == null || Icicles.Length != icCount)
                Icicles = new float[icCount];
        }

        private void Raise(int j, float add, float cap)
        {
            if (Heights[j] < cap)
            {
                Heights[j] = Math.Min(cap, Heights[j] + add);
                if (Heights[j] > MaxH) MaxH = Heights[j];
            }
        }

        public bool AddSnowAtX(float screenX, float amount, float maxHeight)
        {
            int idx = (int)((screenX - Left) / BinSize);
            int n = Heights.Length;
            if (idx < 3 || idx >= n - 3) return false;

            int edgeDist = Math.Min(idx, n - 1 - idx);
            float edgeFactor = Math.Min(1f, edgeDist / 10f);
            float effMax = maxHeight * edgeFactor;

            // Sütun doluysa kar en yakın alçak sütuna kayar (yığın taşmaz, yamaçtan akar)
            if (Heights[idx] >= effMax)
            {
                int best = idx;
                float bestH = Heights[idx];
                for (int k = -8; k <= 8; k++)
                {
                    int j = idx + k;
                    if (j < 3 || j >= n - 3) continue;
                    if (Heights[j] < bestH) { bestH = Heights[j]; best = j; }
                }
                if (best == idx) return true; // Tamamen doygun: tane yığına karışıp kaybolur
                idx = best;
                edgeDist = Math.Min(idx, n - 1 - idx);
                edgeFactor = Math.Min(1f, edgeDist / 10f);
                effMax = maxHeight * edgeFactor;
            }

            // Gauss dağılımlı yumuşak kar birikimi
            Raise(idx, amount * 0.40f, effMax);
            Raise(idx - 1, amount * 0.22f, effMax);
            Raise(idx + 1, amount * 0.22f, effMax);
            Raise(idx - 2, amount * 0.08f, effMax);
            Raise(idx + 2, amount * 0.08f, effMax);
            return true;
        }

        // Büyük kar parçaları düştüğünde kütle geniş bir alana üçgen dağılımla yayılır
        public void AddSnowSpread(float screenX, float amount, float maxHeight, int radiusBins)
        {
            int center = (int)((screenX - Left) / BinSize);
            int n = Heights.Length;
            if (radiusBins < 1) radiusBins = 1;
            float totalW = 0f;
            for (int k = -radiusBins; k <= radiusBins; k++)
                totalW += (radiusBins + 1 - Math.Abs(k));

            for (int k = -radiusBins; k <= radiusBins; k++)
            {
                int j = center + k;
                if (j < 3 || j >= n - 3) continue;
                float w = (radiusBins + 1 - Math.Abs(k)) / totalW;
                int edgeDist = Math.Min(j, n - 1 - j);
                float cap = maxHeight * Math.Min(1f, edgeDist / 10f);
                Raise(j, amount * w, cap);
            }
        }

        public float GetSnowHeightAtX(float screenX)
        {
            int idx = (int)((screenX - Left) / BinSize);
            if (idx >= 0 && idx < Heights.Length) return Heights[idx];
            return 0f;
        }

        public float HarvestOrPushSnow(float screenX, int radiusPx, float maxTake, int pushDir, float pushRatio)
        {
            int center = (int)((screenX - Left) / BinSize);
            int radBins = Math.Max(1, radiusPx / BinSize);
            float harvested = 0f;
            int n = Heights.Length;
            int start = Math.Max(2, center - radBins);
            int end = Math.Min(n - 3, center + radBins);

            for (int i = start; i <= end; i++)
            {
                float take = Math.Min(Heights[i], maxTake);
                if (take > 0f)
                {
                    Heights[i] -= take;
                    harvested += take;

                    // Küreme yönündeki ön sütunlara kar yığma etkisi
                    if (pushDir != 0 && pushRatio > 0f)
                    {
                        int frontIdx = i + pushDir * (radBins + 2);
                        if (frontIdx >= 3 && frontIdx < n - 3)
                        {
                            Heights[frontIdx] = Math.Min(48f, Heights[frontIdx] + take * pushRatio);
                            if (Heights[frontIdx] > MaxH) MaxH = Heights[frontIdx];
                        }
                    }
                }
            }
            return harvested;
        }

        // ====================================================================
        // GERÇEK KAR FİZİĞİ (kütle korunumlu, 60 Hz sabit adım)
        //  - Eylemsizlik: pencere ivmelenince kar tabakası kayar (akı tabanlı)
        //  - Rüzgar sürüklenmesi: tepeler aşınır, kar rüzgar altına taşınır
        //  - Yığılma açısı (angle of repose): simetrik çığ, sarsıntıda yassılaşır
        //  - Kenarlardan dökülme, hafif sıkışma/difüzyon, erime
        // ====================================================================
        public void StepContinuumPhysics(float maxPileHeight, float wind, float melt)
        {
            int n = Heights.Length;
            if (n < 8) return;

            // Kış ışıklarının sarkaç fiziği (pencere hareket ettikçe sallanır)
            float targetSwing = Math.Max(-0.65f, Math.Min(0.65f, -VelX * 0.035f));
            LightSwingVel += (targetSwing - LightSwingAngle) * 0.14f;
            LightSwingVel *= 0.86f;
            LightSwingAngle += LightSwingVel;

            // Uyku optimizasyonu: karı olmayan pencerelerde hiç hesap yapma
            if (MaxH < 0.02f)
            {
                MaxH = 0f;
                return;
            }

            float absAccel = Math.Abs(AccelX) + Math.Abs(AccelY);

            // 1. Yanal taşınım (akı): pencere ivmesi + rüzgar. Toplam kar korunur.
            float inertiaC = Math.Max(-0.45f, Math.Min(0.45f, -AccelX * 0.05f));
            int ws = (wind >= 0f) ? 1 : -1;
            float kWind = Math.Abs(wind) * 0.008f;
            if (Math.Abs(inertiaC) > 0.002f || kWind > 0.00025f)
            {
                Array.Clear(_delta, 0, n);
                for (int i = 0; i < n - 1; i++)
                {
                    float a = Heights[i];
                    float b = Heights[i + 1];
                    float flux = (inertiaC > 0f) ? inertiaC * a : inertiaC * b;

                    // Rüzgar: yüksekten alçağa (tepeden çukura) daha çok kar taşır
                    if (ws > 0) flux += kWind * a * (a > b ? 1f : 0.3f);
                    else flux -= kWind * b * (b > a ? 1f : 0.3f);

                    _delta[i] -= flux;
                    _delta[i + 1] += flux;
                }
                for (int i = 0; i < n; i++)
                    Heights[i] = Math.Max(0f, Heights[i] + _delta[i]);
            }

            // 2. Yığılma açısı: iki geçişli, simetrik ve kütle korunumlu çığ.
            //    Pencere sarsılırsa karın tutunma açısı düşer ve yığın yassılaşır.
            float shake = Math.Min(1f, absAccel * 0.06f);
            float maxSlope = 1.85f - shake * 1.05f;
            for (int iter = 0; iter < 2; iter++)
            {
                Array.Clear(_delta, 0, n);
                bool any = false;
                for (int i = 0; i < n - 1; i++)
                {
                    float diff = Heights[i] - Heights[i + 1];
                    if (diff > maxSlope)
                    {
                        float t = (diff - maxSlope) * 0.30f;
                        _delta[i] -= t;
                        _delta[i + 1] += t;
                        any = true;
                    }
                    else if (-diff > maxSlope)
                    {
                        float t = (-diff - maxSlope) * 0.30f;
                        _delta[i + 1] -= t;
                        _delta[i] += t;
                        any = true;
                    }
                }
                if (!any) break;
                for (int i = 0; i < n; i++)
                    Heights[i] = Math.Max(0f, Heights[i] + _delta[i]);
            }

            // 3. Kenarlar: pencere dışına taşan kar kaybolmaz, dökülen kar olarak kaydedilir
            LostLeft += Heights[0] + Heights[1] * 0.5f;
            Heights[0] = 0f;
            Heights[1] *= 0.5f;
            LostRight += Heights[n - 1] + Heights[n - 2] * 0.5f;
            Heights[n - 1] = 0f;
            Heights[n - 2] *= 0.5f;

            // 4. Hafif viskoz difüzyon (yüzey yumuşatma) + erime + tavan sınırı
            for (int i = 1; i < n - 1; i++)
                _smooth[i] = Heights[i] * 0.90f + (Heights[i - 1] + Heights[i + 1]) * 0.05f;
            _smooth[0] = Heights[0];
            _smooth[n - 1] = Heights[n - 1];

            float cap = maxPileHeight * 1.25f;
            float keep = 1f - melt;
            float mx = 0f;
            for (int i = 0; i < n; i++)
            {
                float h = _smooth[i] * keep;
                if (h < 0.01f) h = 0f;
                if (h > cap) h = cap;
                Heights[i] = h;
                if (h > mx) mx = h;
            }
            MaxH = mx;
        }
    }

    // ========================================================================
    // 4. FİZİKSEL KARTOPU, 3'LÜ İSTİFLENEBİLİR KARDAN ADAM VE KAR KÜREYİCİ
    // ========================================================================
    internal struct TrainSmoke
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float Radius;
        public int Ttl;
        public int MaxTtl;
        public bool Active;
    }

    internal sealed class PenguinEntity
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public IntPtr SurfaceHwnd;
        public int Direction = 1;
        public float WalkPhase;
        public float FlipperPhase;
        public int State;      // 0: Yürü, 1: Karnında Kay (Toboggan), 2: Dur & El Salla
        public int StateTimer;
        public bool Dragging;
        public float Scale;
        public bool HasScarf;
        public Color ScarfColor;
        public int HopTimer;

        public PenguinEntity(float x, float y, IntPtr surfaceHwnd, Random rng)
        {
            X = x;
            Y = y;
            SurfaceHwnd = surfaceHwnd;
            Direction = (rng.NextDouble() < 0.5) ? 1 : -1;
            Scale = 0.92f + (float)rng.NextDouble() * 0.22f;
            State = 0;
            StateTimer = 110 + rng.Next(150);
            HasScarf = rng.NextDouble() < 0.72;
            Color[] colors = new Color[] {
                Color.FromArgb(248, 239, 68, 68),
                Color.FromArgb(248, 56, 189, 248),
                Color.FromArgb(248, 250, 204, 21),
                Color.FromArgb(248, 52, 211, 153),
                Color.FromArgb(248, 244, 114, 182)
            };
            ScarfColor = colors[rng.Next(colors.Length)];
        }
    }

    internal sealed class SnowballEntity
    {
        public float X;
        public float Y; // Yüzey temas noktası (alt merkez)
        public float Vx;
        public float Vy;
        public float Radius;
        public float RollAngle;
        public IntPtr SurfaceHwnd;
        public bool Dragging;
        public bool IsAiSnowball;
        public SnowmanEntity IgnoreSnowman;

        // Üst üste koyma (Stacking: 1 = tek kartopu, 2 = gövde eklendi)
        public int StackCount = 1;
        public float BaseRadius;
        public float MidRadius;
        public float WobblePhase;
        public float WobbleAmp;

        public SnowballEntity(float x, float y, float radius, IntPtr surfaceHwnd)
        {
            X = x;
            Y = y;
            Radius = Math.Max(6f, Math.Min(26f, radius));
            BaseRadius = Radius;
            MidRadius = 0f;
            SurfaceHwnd = surfaceHwnd;
        }

        public float TotalHeight
        {
            get
            {
                if (StackCount == 1) return BaseRadius * 2f;
                return BaseRadius * 2f + MidRadius * 1.65f;
            }
        }
    }

    internal sealed class SnowmanEntity
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float BaseR1;
        public float BaseR2;
        public float BaseR3;
        public float Scale = 1.0f;
        public float R1; // Alt
        public float R2; // Orta
        public float R3; // Kafa
        public IntPtr SurfaceHwnd;
        public bool Dragging;
        public float TiltAngle;
        public float TiltVel;

        // Gardırop & Görünüm Ayarları
        public bool HasHat = true;
        public int HatStyle;     // 0: Ponponlu Bere, 1: Silindir Şapka, 2: Noel Şapkası, 3: Altın Taç, 4: Kulaklıklı Şapka, 5: Büyücü Şapkası
        public Color HatColor;
        public int GlassesStyle; // 0: Yok, 1: Güneş Gözlüğü, 2: Yuvarlak Gözlük, 3: Monokl, 4: Kayak Gözlüğü
        public int FaceStyle;    // 0: Klasik, 1: Pembe Yanak & Gülümseme, 2: Bıyık, 3: Pipo
        public int ScarfStyle;   // 0: Yok, 1: Klasik Atkı, 2: Çizgili Atkı, 3: Papyon, 4: Kürklü Yaka
        public Color ScarfColor;
        public int OutfitStyle;  // 0: Kömür Düğme, 1: Kış Yeleği, 2: Yılbaşı Kazağı, 3: Kraliyet Pelerini
        public Color OutfitColor;
        public int ArmStyle;     // 0: Dal Kollar, 1: Eldivenli, 2: Süpürge, 3: Baston Şeker, 4: Yok
        public string NameTag = "";
        public Color NameTagColor = Color.FromArgb(255, 224, 242, 254);

        public int SurpriseTimer; // Vurulunca şaşkın yüz ifadesi süresi
        public int BlinkTimer;
        public int HitCooldown;
        public int FightCooldown; // AI kartopu savaşı bekleme sayacı

        public static readonly Color[] WardrobePalette = new Color[]
        {
            Color.FromArgb(250, 220, 38, 38),   // 0: Noel Kırmızısı
            Color.FromArgb(250, 30, 41, 59),    // 1: Gece Laciverti
            Color.FromArgb(250, 37, 99, 235),   // 2: Kraliyet Mavisi
            Color.FromArgb(250, 16, 185, 129),  // 3: Zümrüt Yeşili
            Color.FromArgb(250, 245, 158, 11),  // 4: Altın Sarısı
            Color.FromArgb(250, 147, 51, 234),  // 5: Büyücü Moru
            Color.FromArgb(250, 236, 72, 153),  // 6: Pembe
            Color.FromArgb(250, 226, 232, 240)  // 7: Kar Beyazı
        };

        public static readonly Color[] NameTagPalette = new Color[]
        {
            Color.FromArgb(255, 248, 250, 252), // 0: Kar Beyazı
            Color.FromArgb(255, 56, 189, 248),  // 1: Buz Mavisi
            Color.FromArgb(255, 250, 204, 21),  // 2: Altın Sarısı
            Color.FromArgb(255, 52, 211, 153),  // 3: Zümrüt Yeşili
            Color.FromArgb(255, 244, 114, 182), // 4: Neon Pembe
            Color.FromArgb(255, 192, 132, 252), // 5: Lavanta Moru
            Color.FromArgb(255, 251, 146, 60),  // 6: Turuncu
            Color.FromArgb(255, 248, 113, 113)  // 7: Mercan Kırmızısı
        };

        public SnowmanEntity(float x, float y, float r1, float r2, float r3, IntPtr surfaceHwnd, Random rng)
        {
            X = x;
            Y = y;
            BaseR1 = Math.Max(10f, Math.Min(26f, r1));
            BaseR2 = Math.Max(8f, Math.Min(20f, r2));
            BaseR3 = Math.Max(6.5f, Math.Min(16f, r3));
            ApplyScale(1.0f);
            SurfaceHwnd = surfaceHwnd;

            // Varsayılan kış kombini
            double roll = rng.NextDouble();
            if (roll < 0.45)
            {
                HatStyle = 0;
                HatColor = WardrobePalette[0];
            }
            else if (roll < 0.70)
            {
                HatStyle = 1;
                HatColor = WardrobePalette[1];
            }
            else if (roll < 0.88)
            {
                HatStyle = 2;
                HatColor = WardrobePalette[0];
            }
            else
            {
                HatStyle = 4;
                HatColor = WardrobePalette[2];
            }

            ScarfStyle = (rng.NextDouble() < 0.65) ? 1 : 2;
            ScarfColor = WardrobePalette[rng.Next(0, 7)];
            OutfitStyle = 0;
            OutfitColor = WardrobePalette[rng.Next(0, 6)];
            ArmStyle = (rng.NextDouble() < 0.6) ? 0 : 1;
            FaceStyle = (rng.NextDouble() < 0.55) ? 1 : 0;
            GlassesStyle = 0;
            NameTag = "";
            NameTagColor = NameTagPalette[1];
            BlinkTimer = 60 + rng.Next(140);
            FightCooldown = 140 + rng.Next(240);
        }

        public void ApplyScale(float newScale)
        {
            Scale = Math.Max(0.75f, Math.Min(1.45f, newScale));
            R1 = BaseR1 * Scale;
            R2 = BaseR2 * Scale;
            R3 = BaseR3 * Scale;
        }

        public void ApplyPreset(int presetId)
        {
            HasHat = true;
            if (presetId == 0)
            {
                // Noel Baba Stili
                HatStyle = 2;
                HatColor = WardrobePalette[0];
                GlassesStyle = 2;
                FaceStyle = 1;
                ScarfStyle = 4;
                ScarfColor = WardrobePalette[0];
                OutfitStyle = 2;
                OutfitColor = WardrobePalette[0];
                ArmStyle = 3; // Baston şeker
            }
            else if (presetId == 1)
            {
                // Beyefendi Stili
                HatStyle = 1;
                HatColor = WardrobePalette[1];
                GlassesStyle = 3; // Monokl
                FaceStyle = 2;    // Bıyık
                ScarfStyle = 3;   // Papyon
                ScarfColor = WardrobePalette[0];
                OutfitStyle = 1;  // Yelek
                OutfitColor = WardrobePalette[1];
                ArmStyle = 2;     // Süpürge
            }
            else if (presetId == 2)
            {
                // Havalı Kayakçı Stili
                HatStyle = 0;
                HatColor = WardrobePalette[2];
                GlassesStyle = 1; // Güneş gözlüğü
                FaceStyle = 1;
                ScarfStyle = 2;   // Çizgili atkı
                ScarfColor = WardrobePalette[4];
                OutfitStyle = 2;  // Kazak
                OutfitColor = WardrobePalette[3];
                ArmStyle = 1;     // Eldiven
            }
            else
            {
                // Kış Büyücüsü Stili
                HatStyle = 5;
                HatColor = WardrobePalette[5];
                GlassesStyle = 2;
                FaceStyle = 3;    // Pipo
                ScarfStyle = 4;
                ScarfColor = WardrobePalette[4];
                OutfitStyle = 3;  // Pelerin
                OutfitColor = WardrobePalette[5];
                ArmStyle = 2;
            }
        }

        public void RandomizeOutfit(Random rng)
        {
            HasHat = rng.NextDouble() > 0.08;
            HatStyle = rng.Next(6);
            HatColor = WardrobePalette[rng.Next(WardrobePalette.Length)];
            GlassesStyle = rng.Next(5);
            FaceStyle = rng.Next(4);
            ScarfStyle = 1 + rng.Next(4);
            ScarfColor = WardrobePalette[rng.Next(WardrobePalette.Length)];
            OutfitStyle = rng.Next(4);
            OutfitColor = WardrobePalette[rng.Next(WardrobePalette.Length)];
            ArmStyle = rng.Next(4);
        }
    }

    internal static class SnowmanRenderer
    {
        private static readonly Pen OutlinePen = new Pen(Color.FromArgb(185, 148, 163, 184), 1.3f);
        private static readonly SolidBrush CoalBrush = new SolidBrush(Color.FromArgb(248, 15, 23, 42));
        private static readonly SolidBrush CarrotBrush = new SolidBrush(Color.FromArgb(252, 249, 115, 22));
        private static readonly SolidBrush WhiteBrush = new SolidBrush(Color.FromArgb(252, 255, 255, 255));

        public static void DrawShadedSnowSphere(Graphics g, float cx, float cy, float r)
        {
            RectangleF rect = new RectangleF(cx - r, cy - r, r * 2f, r * 2f);
            using (LinearGradientBrush sphereBrush = new LinearGradientBrush(
                rect,
                Color.FromArgb(255, 255, 255, 255),
                Color.FromArgb(245, 191, 219, 254),
                45f))
            {
                g.FillEllipse(sphereBrush, rect);
            }
            g.DrawEllipse(OutlinePen, rect);
        }

        public static void DrawSnowman(
            Graphics g, SnowmanEntity sm,
            float drawX, float drawY, float scaleOverride,
            float lookTargetX, float lookTargetY, float time, bool isHighlighted)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(drawX, drawY);
            if (scaleOverride != 1.0f)
                g.ScaleTransform(scaleOverride, scaleOverride);
            g.RotateTransform(sm.TiltAngle);

            float r1 = sm.R1;
            float y1 = -r1 + 2f;
            float r2 = sm.R2;
            float y2 = y1 - r1 - r2 * 0.64f;
            float r3 = sm.R3;
            float y3 = y2 - r2 - r3 * 0.64f;
            float s = r2 / 8.5f;

            // Seçili kardan adam için zarif alt ışık halkası (gardırop açıkken)
            if (isHighlighted)
            {
                float pulse = 0.65f + 0.35f * (float)Math.Sin(time * 5.0f);
                int glowA = (int)(85 + 55 * pulse);
                using (SolidBrush halo = new SolidBrush(Color.FromArgb(glowA / 3, 56, 189, 248)))
                using (Pen ring = new Pen(Color.FromArgb(glowA, 56, 189, 248), 2.0f))
                {
                    g.FillEllipse(halo, -r1 * 1.35f, -6f, r1 * 2.7f, 12f);
                    g.DrawEllipse(ring, -r1 * 1.35f, -6f, r1 * 2.7f, 12f);
                }
            }

            // 1. Pelerin (Vücudun ARKASINDA çizilir!)
            if (sm.OutfitStyle == 3)
            {
                float capeSway = (float)Math.Sin(time * 2.4f) * 2.5f * s;
                PointF[] capePts = new PointF[]
                {
                    new PointF(-r2 * 0.75f, y2 - r2 * 0.65f),
                    new PointF(r2 * 0.75f, y2 - r2 * 0.65f),
                    new PointF(r1 * 1.32f + capeSway, y1 + r1 * 0.75f),
                    new PointF(-r1 * 1.32f + capeSway, y1 + r1 * 0.75f)
                };
                using (SolidBrush capeBrush = new SolidBrush(sm.OutfitColor))
                using (Pen capeTrim = new Pen(Color.FromArgb(230, 250, 204, 21), Math.Max(1.4f, 1.6f * s)))
                {
                    g.FillPolygon(capeBrush, capePts);
                    g.DrawPolygon(capeTrim, capePts);
                }
            }

            // 2. Kollar & Elde Tutulan Aksesuarlar (Gövde yanlarından çıkar)
            if (sm.ArmStyle != 4)
                DrawArms(g, sm, r2, y2, s, time);

            // 3. Kardan Adamın 3 Gölgeli Kar Küresi
            DrawShadedSnowSphere(g, 0f, y1, r1);
            DrawShadedSnowSphere(g, 0f, y2, r2);
            DrawShadedSnowSphere(g, 0f, y3, r3);

            // 4. Gövde Kıyafeti (Düğmeler, Yelek veya Yılbaşı Kazağı)
            DrawBodyOutfit(g, sm, r2, y2, s);

            // 5. Boyun / Atkı / Papyon / Kürklü Yaka
            if (sm.ScarfStyle != 0)
                DrawScarfOrNeckwear(g, sm, r2, y2, s);

            // 6. Yüz İfadesi, Gözler, Pembe Yanaklar, Bıyık, Pipo & Havuç Burun
            DrawFace(g, sm, r3, y3, s, drawX, drawY, lookTargetX, lookTargetY, time);

            // 7. Gözlük / Monokl / Kayak Gözlüğü
            if (sm.GlassesStyle != 0)
                DrawGlasses(g, sm.GlassesStyle, r3, y3, s);

            // 8. Şapka
            if (sm.HasHat)
            {
                float hy = y3 - r3 * 0.78f;
                DrawHatGeometry(g, 0f, hy, r3, s, sm.HatStyle, sm.HatColor);
            }

            g.Restore(state);

            // 9. Özelleştirilebilir Renkli İsim Etiketi (Şapkanın hemen üzerinde, düz okunaklı)
            if (!string.IsNullOrEmpty(sm.NameTag))
            {
                float hatClearance = 3.5f * s;
                if (sm.HasHat)
                {
                    if (sm.HatStyle == 5) hatClearance = 16.5f * s;      // Büyücü Şapkası
                    else if (sm.HatStyle == 1) hatClearance = 13.0f * s; // Silindir Şapka
                    else hatClearance = 11.2f * s;                       // Bere / Noel / Taç / Ushanka
                }
                float topOffset = (y3 - r3 * 0.92f - hatClearance) * scaleOverride;
                float fontSize = Math.Max(7.5f, Math.Min(11.0f, 8.2f * scaleOverride));
                using (Font tagFont = new Font("Segoe UI Semibold", fontSize, FontStyle.Bold))
                {
                    SizeF sz = g.MeasureString(sm.NameTag, tagFont);
                    float padX = 7f;
                    float padY = 2.5f;
                    float pw = sz.Width + padX * 2f;
                    float ph = sz.Height + padY * 2f;
                    float px = drawX - pw * 0.5f;
                    float py = Math.Max(6f, drawY + topOffset - ph);

                    RectangleF pill = new RectangleF(px, py, pw, ph);
                    Color nc = sm.NameTagColor.A > 0 ? sm.NameTagColor : Color.White;
                    using (GraphicsPath pp = GraphicsExt.CreateRoundedRect(pill, ph * 0.48f))
                    using (SolidBrush pillBg = new SolidBrush(Color.FromArgb(205, 11, 17, 30)))
                    using (Pen pillRim = new Pen(Color.FromArgb(195, nc.R, nc.G, nc.B), 1.2f))
                    using (SolidBrush txtBrush = new SolidBrush(Color.FromArgb(252, nc.R, nc.G, nc.B)))
                    {
                        g.FillPath(pillBg, pp);
                        g.DrawPath(pillRim, pp);
                        g.DrawString(sm.NameTag, tagFont, txtBrush, px + padX, py + padY);
                    }
                }
            }
        }

        private static void DrawArms(Graphics g, SnowmanEntity sm, float r2, float y2, float s, float time)
        {
            float wave = (float)Math.Sin(time * 2.1f) * 2.2f * s;
            using (Pen twigPen = new Pen(Color.FromArgb(245, 120, 74, 42), Math.Max(1.8f, 2.2f * s))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            })
            {
                // Sol dal kol
                float lx1 = -r2 * 0.85f;
                float ly1 = y2 - r2 * 0.1f;
                float lx2 = -r2 * 1.95f;
                float ly2 = y2 - r2 * 0.65f + wave;
                g.DrawLine(twigPen, lx1, ly1, lx2, ly2);
                if (sm.ArmStyle == 0)
                {
                    g.DrawLine(twigPen, lx2 + 3f * s, ly2 + 1.5f * s, lx2 - 2.5f * s, ly2 - 3.2f * s);
                    g.DrawLine(twigPen, lx2 + 3f * s, ly2 + 1.5f * s, lx2 - 3.5f * s, ly2 + 1.5f * s);
                }

                // Sağ dal kol
                float rx1 = r2 * 0.85f;
                float ry1 = y2 - r2 * 0.1f;
                float rx2 = r2 * 1.95f;
                float ry2 = y2 - r2 * 0.65f - wave;
                g.DrawLine(twigPen, rx1, ry1, rx2, ry2);
                if (sm.ArmStyle == 0)
                {
                    g.DrawLine(twigPen, rx2 - 3f * s, ry2 + 1.5f * s, rx2 + 2.5f * s, ry2 - 3.2f * s);
                    g.DrawLine(twigPen, rx2 - 3f * s, ry2 + 1.5f * s, rx2 + 3.5f * s, ry2 + 1.5f * s);
                }

                // 1: Eldivenler
                if (sm.ArmStyle == 1)
                {
                    Color mitCol = sm.ScarfColor.A > 0 ? sm.ScarfColor : Color.FromArgb(245, 220, 38, 38);
                    using (SolidBrush mb = new SolidBrush(mitCol))
                    {
                        float mr = Math.Max(3.2f, 3.6f * s);
                        g.FillEllipse(mb, lx2 - mr, ly2 - mr, mr * 2f, mr * 2f);
                        g.FillEllipse(mb, rx2 - mr, ry2 - mr, mr * 2f, mr * 2f);
                        g.FillEllipse(WhiteBrush, lx2 + mr * 0.2f, ly2 - mr * 0.8f, mr * 0.85f, mr * 0.85f);
                        g.FillEllipse(WhiteBrush, rx2 - mr * 1.0f, ry2 - mr * 0.8f, mr * 0.85f, mr * 0.85f);
                    }
                }
                // 2: Kış Süpürgesi (sağ elde)
                else if (sm.ArmStyle == 2)
                {
                    using (Pen handlePen = new Pen(Color.FromArgb(245, 161, 98, 47), Math.Max(2.0f, 2.4f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(handlePen, rx2 + 1f * s, ry2 - 14f * s, rx2 - 4f * s, ry2 + 16f * s);
                    }
                    PointF[] bristles = new PointF[]
                    {
                        new PointF(rx2 + 1f * s, ry2 - 12f * s),
                        new PointF(rx2 - 5f * s, ry2 - 22f * s),
                        new PointF(rx2 + 7f * s, ry2 - 21f * s)
                    };
                    using (SolidBrush strawBrush = new SolidBrush(Color.FromArgb(245, 234, 179, 8)))
                    using (Pen tiePen = new Pen(Color.FromArgb(245, 220, 38, 38), Math.Max(1.6f, 1.8f * s)))
                    {
                        g.FillPolygon(strawBrush, bristles);
                        g.DrawLine(tiePen, rx2 - 1.5f * s, ry2 - 13.5f * s, rx2 + 3.5f * s, ry2 - 13.5f * s);
                    }
                }
                // 3: Baston Şeker (sağ elde)
                else if (sm.ArmStyle == 3)
                {
                    using (Pen caneWhite = new Pen(Color.White, Math.Max(2.8f, 3.4f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    using (Pen caneRed = new Pen(Color.FromArgb(250, 220, 38, 38), Math.Max(2.8f, 3.4f * s)) { DashStyle = DashStyle.Dash })
                    {
                        g.DrawLine(caneWhite, rx2, ry2 + 8f * s, rx2, ry2 - 10f * s);
                        g.DrawArc(caneWhite, rx2, ry2 - 15f * s, 8f * s, 8f * s, 180, 180);
                        g.DrawLine(caneRed, rx2, ry2 + 8f * s, rx2, ry2 - 10f * s);
                        g.DrawArc(caneRed, rx2, ry2 - 15f * s, 8f * s, 8f * s, 180, 180);
                    }
                }
            }
        }

        private static void DrawBodyOutfit(Graphics g, SnowmanEntity sm, float r2, float y2, float s)
        {
            if (sm.OutfitStyle == 1)
            {
                // Kış Yeleği (Butonlu şık yelek)
                GraphicsState st = g.Save();
                using (GraphicsPath clip = new GraphicsPath())
                {
                    clip.AddEllipse(-r2 + 0.5f, y2 - r2 + 0.5f, (r2 - 0.5f) * 2f, (r2 - 0.5f) * 2f);
                    g.SetClip(clip, CombineMode.Intersect);

                    using (SolidBrush vestBrush = new SolidBrush(sm.OutfitColor))
                    {
                        g.FillRectangle(vestBrush, -r2 * 0.9f, y2 - r2 * 0.72f, r2 * 1.8f, r2 * 1.55f);
                    }
                    // Yaka V kesimi
                    PointF[] vNeck = new PointF[]
                    {
                        new PointF(-r2 * 0.35f, y2 - r2 * 0.75f),
                        new PointF(r2 * 0.35f, y2 - r2 * 0.75f),
                        new PointF(0f, y2 - r2 * 0.15f)
                    };
                    g.FillPolygon(WhiteBrush, vNeck);

                    // Ortada dikiş & altın düğmeler
                    using (Pen seam = new Pen(Color.FromArgb(140, 15, 23, 42), 1.1f))
                        g.DrawLine(seam, 0f, y2 - r2 * 0.15f, 0f, y2 + r2 * 0.85f);
                    using (SolidBrush btnBrush = new SolidBrush(Color.FromArgb(250, 250, 204, 21)))
                    {
                        g.FillEllipse(btnBrush, -1.6f * s, y2 - 0.8f * s, 3.2f * s, 3.2f * s);
                        g.FillEllipse(btnBrush, -1.6f * s, y2 + 3.6f * s, 3.2f * s, 3.2f * s);
                    }
                }
                g.Restore(st);
            }
            else if (sm.OutfitStyle == 2)
            {
                // Yılbaşı Kazağı (Desenli sıcak kış kazağı)
                GraphicsState st = g.Save();
                using (GraphicsPath clip = new GraphicsPath())
                {
                    clip.AddEllipse(-r2 + 0.5f, y2 - r2 + 0.5f, (r2 - 0.5f) * 2f, (r2 - 0.5f) * 2f);
                    g.SetClip(clip, CombineMode.Intersect);

                    using (SolidBrush swBrush = new SolidBrush(sm.OutfitColor))
                        g.FillRectangle(swBrush, -r2, y2 - r2 * 0.68f, r2 * 2f, r2 * 1.55f);

                    using (Pen trimPen = new Pen(Color.FromArgb(240, 255, 255, 255), Math.Max(1.6f, 2.0f * s)))
                    {
                        g.DrawLine(trimPen, -r2, y2 - r2 * 0.18f, r2, y2 - r2 * 0.18f);
                        g.DrawLine(trimPen, -r2, y2 + r2 * 0.36f, r2, y2 + r2 * 0.36f);
                    }
                    // Kar tanesi motif noktaları
                    using (SolidBrush goldDot = new SolidBrush(Color.FromArgb(248, 253, 224, 71)))
                    {
                        float dotS = Math.Max(2.2f, 2.5f * s);
                        for (int k = -2; k <= 2; k++)
                            g.FillEllipse(goldDot, k * 3.8f * s - dotS * 0.5f, y2 + 0.5f * s, dotS, dotS);
                    }
                }
                g.Restore(st);
            }
            else
            {
                // Klasik Kömür Düğmeler (veya Pelerin ön düğmeleri)
                g.FillEllipse(CoalBrush, -1.8f * s, y2 - 2.8f * s, 3.6f * s, 3.6f * s);
                g.FillEllipse(CoalBrush, -1.8f * s, y2 + 2.8f * s, 3.6f * s, 3.6f * s);
            }
        }

        private static void DrawScarfOrNeckwear(Graphics g, SnowmanEntity sm, float r2, float y2, float s)
        {
            float sy = y2 - r2 * 0.72f;
            if (sm.ScarfStyle == 1 || sm.ScarfStyle == 2)
            {
                float w = Math.Max(2.6f, 3.9f * s);
                using (Pen scarfPen = new Pen(sm.ScarfColor, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(scarfPen, -r2 * 0.84f, sy, r2 * 0.84f, sy);
                    g.DrawLine(scarfPen, r2 * 0.35f, sy, r2 * 0.58f, sy + 8.8f * s);
                }
                if (sm.ScarfStyle == 2)
                {
                    // Çizgili atkı şeritleri
                    using (Pen stripePen = new Pen(Color.FromArgb(235, 255, 255, 255), Math.Max(1.3f, 1.5f * s)))
                    {
                        g.DrawLine(stripePen, -r2 * 0.48f, sy - w * 0.45f, -r2 * 0.32f, sy + w * 0.45f);
                        g.DrawLine(stripePen, -r2 * 0.04f, sy - w * 0.45f, r2 * 0.12f, sy + w * 0.45f);
                        g.DrawLine(stripePen, r2 * 0.38f, sy + 3.2f * s, r2 * 0.62f, sy + 4.4f * s);
                        g.DrawLine(stripePen, r2 * 0.44f, sy + 6.4f * s, r2 * 0.68f, sy + 7.6f * s);
                    }
                }
            }
            else if (sm.ScarfStyle == 3)
            {
                // Şık Papyon
                using (SolidBrush bowBrush = new SolidBrush(sm.ScarfColor))
                using (Pen bowOutline = new Pen(Color.FromArgb(140, 15, 23, 42), 1.0f))
                {
                    PointF[] leftWing = new PointF[] {
                        new PointF(0f, sy),
                        new PointF(-6.2f * s, sy - 3.2f * s),
                        new PointF(-6.2f * s, sy + 3.2f * s)
                    };
                    PointF[] rightWing = new PointF[] {
                        new PointF(0f, sy),
                        new PointF(6.2f * s, sy - 3.2f * s),
                        new PointF(6.2f * s, sy + 3.2f * s)
                    };
                    g.FillPolygon(bowBrush, leftWing);
                    g.FillPolygon(bowBrush, rightWing);
                    g.DrawPolygon(bowOutline, leftWing);
                    g.DrawPolygon(bowOutline, rightWing);
                    g.FillEllipse(bowBrush, -2.0f * s, sy - 2.0f * s, 4.0f * s, 4.0f * s);
                }
            }
            else if (sm.ScarfStyle == 4)
            {
                // Kürklü Kış Yakası + Altın Broş
                using (Pen furPen = new Pen(Color.FromArgb(245, 241, 245, 249), Math.Max(3.6f, 4.8f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (SolidBrush gemBrush = new SolidBrush(sm.ScarfColor))
                using (Pen goldRim = new Pen(Color.FromArgb(250, 250, 204, 21), Math.Max(1.2f, 1.4f * s)))
                {
                    g.DrawArc(furPen, -r2 * 0.85f, sy - 4.5f * s, r2 * 1.7f, 8.5f * s, 10f, 160f);
                    float gr = Math.Max(2.2f, 2.5f * s);
                    g.FillEllipse(gemBrush, -gr, sy + 1.8f * s - gr, gr * 2f, gr * 2f);
                    g.DrawEllipse(goldRim, -gr, sy + 1.8f * s - gr, gr * 2f, gr * 2f);
                }
            }
        }

        private static void DrawFace(
            Graphics g, SnowmanEntity sm, float r3, float y3, float s,
            float drawX, float drawY, float lookTargetX, float lookTargetY, float time)
        {
            // Pembe yanaklar (Gülümseme stilinde)
            if (sm.FaceStyle == 1)
            {
                using (SolidBrush blush = new SolidBrush(Color.FromArgb(95, 248, 113, 113)))
                {
                    g.FillEllipse(blush, -5.6f * s, y3 + 0.8f * s, 3.2f * s, 2.0f * s);
                    g.FillEllipse(blush, 2.4f * s, y3 + 0.8f * s, 3.2f * s, 2.0f * s);
                }
            }

            // Gözler (Fare imlecine doğru hafifçe bakar ve göz kırpar)
            float lookX = Math.Max(-1.0f, Math.Min(1.0f, (lookTargetX - drawX) / 220f)) * s;
            float lookY = Math.Max(-0.8f, Math.Min(0.8f, (lookTargetY - (drawY + y3)) / 220f)) * s;

            if (sm.BlinkTimer > 0 && sm.BlinkTimer < 6 && sm.SurpriseTimer == 0)
            {
                using (Pen blinkPen = new Pen(Color.FromArgb(245, 15, 23, 42), 1.4f))
                {
                    g.DrawLine(blinkPen, -3.8f * s, y3 - 1.5f * s, -1.2f * s, y3 - 1.5f * s);
                    g.DrawLine(blinkPen, 1.0f * s, y3 - 1.5f * s, 3.6f * s, y3 - 1.5f * s);
                }
            }
            else
            {
                float eyeR = (sm.SurpriseTimer > 0) ? 3.0f * s : 2.4f * s;
                g.FillEllipse(CoalBrush, -3.4f * s + lookX, y3 - 2.6f * s + lookY, eyeR, eyeR);
                g.FillEllipse(CoalBrush, 1.2f * s + lookX, y3 - 2.6f * s + lookY, eyeR, eyeR);
            }

            // Ağız / İfade
            if (sm.SurpriseTimer > 0)
            {
                g.FillEllipse(CoalBrush, -1.6f * s, y3 + 2.8f * s, 3.2f * s, 3.6f * s);
            }
            else if (sm.FaceStyle == 1)
            {
                // Gülümseyen kömür noktaları
                float d = Math.Max(1.3f, 1.4f * s);
                g.FillEllipse(CoalBrush, -3.4f * s, y3 + 3.0f * s, d, d);
                g.FillEllipse(CoalBrush, -1.5f * s, y3 + 4.1f * s, d, d);
                g.FillEllipse(CoalBrush, 0.5f * s, y3 + 4.1f * s, d, d);
                g.FillEllipse(CoalBrush, 2.4f * s, y3 + 3.0f * s, d, d);
            }
            else if (sm.FaceStyle == 2)
            {
                // Beyefendi Bıyığı
                using (Pen stache = new Pen(Color.FromArgb(245, 51, 65, 85), Math.Max(1.8f, 2.2f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawBezier(stache,
                        0f, y3 + 2.7f * s,
                        -2.2f * s, y3 + 1.8f * s,
                        -3.8f * s, y3 + 4.4f * s,
                        -5.4f * s, y3 + 2.8f * s);
                    g.DrawBezier(stache,
                        0f, y3 + 2.7f * s,
                        2.2f * s, y3 + 1.8f * s,
                        3.8f * s, y3 + 4.4f * s,
                        5.4f * s, y3 + 2.8f * s);
                }
            }
            else if (sm.FaceStyle == 3)
            {
                // Klasik Kış Piposu + Minik Duman Halkaları
                using (Pen pipeStem = new Pen(Color.FromArgb(248, 120, 53, 15), Math.Max(1.5f, 1.8f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (SolidBrush bowlBrush = new SolidBrush(Color.FromArgb(250, 146, 64, 14)))
                {
                    g.DrawLine(pipeStem, 1.2f * s, y3 + 3.6f * s, 6.2f * s, y3 + 5.2f * s);
                    g.FillRectangle(bowlBrush, 5.2f * s, y3 + 2.6f * s, 3.0f * s, 3.6f * s);
                }
                float puffPhase = (time * 1.4f) % 1.0f;
                int puffA = (int)((1f - puffPhase) * 140f);
                if (puffA > 15)
                {
                    using (SolidBrush puff = new SolidBrush(Color.FromArgb(puffA, 226, 232, 240)))
                    {
                        float pr = (1.2f + puffPhase * 2.2f) * s;
                        g.FillEllipse(puff, 6.4f * s + puffPhase * 2.8f * s - pr, y3 + 1.5f * s - puffPhase * 7.5f * s - pr, pr * 2f, pr * 2f);
                    }
                }
            }

            // Havuç burun
            PointF[] nose = new PointF[] {
                new PointF(0f, y3 - 0.4f * s),
                new PointF(8.5f * s, y3 + 1.5f * s),
                new PointF(0f, y3 + 3.0f * s)
            };
            g.FillPolygon(CarrotBrush, nose);
        }

        private static void DrawGlasses(Graphics g, int style, float r3, float y3, float s)
        {
            float ey = y3 - 1.4f * s;
            if (style == 1)
            {
                // Havalı Güneş Gözlüğü
                using (SolidBrush lens = new SolidBrush(Color.FromArgb(240, 15, 23, 42)))
                using (Pen frame = new Pen(Color.FromArgb(245, 51, 65, 85), Math.Max(1.2f, 1.4f * s)))
                using (Pen glint = new Pen(Color.FromArgb(170, 255, 255, 255), Math.Max(1.0f, 1.1f * s)))
                {
                    RectangleF lRect = new RectangleF(-5.4f * s, ey - 2.0f * s, 4.4f * s, 3.6f * s);
                    RectangleF rRect = new RectangleF(1.0f * s, ey - 2.0f * s, 4.4f * s, 3.6f * s);
                    g.FillRectangle(lens, lRect);
                    g.FillRectangle(lens, rRect);
                    g.DrawRectangle(frame, lRect.X, lRect.Y, lRect.Width, lRect.Height);
                    g.DrawRectangle(frame, rRect.X, rRect.Y, rRect.Width, rRect.Height);
                    g.DrawLine(frame, -1.0f * s, ey - 0.5f * s, 1.0f * s, ey - 0.5f * s);
                    g.DrawLine(glint, -4.6f * s, ey - 1.2f * s, -3.0f * s, ey + 0.8f * s);
                    g.DrawLine(glint, 1.8f * s, ey - 1.2f * s, 3.4f * s, ey + 0.8f * s);
                }
            }
            else if (style == 2)
            {
                // Yuvarlak Altın Gözlük
                using (SolidBrush tint = new SolidBrush(Color.FromArgb(45, 186, 230, 253)))
                using (Pen gold = new Pen(Color.FromArgb(248, 250, 204, 21), Math.Max(1.2f, 1.35f * s)))
                {
                    float gr = 2.5f * s;
                    g.FillEllipse(tint, -2.5f * s - gr, ey - gr, gr * 2f, gr * 2f);
                    g.FillEllipse(tint, 2.5f * s - gr, ey - gr, gr * 2f, gr * 2f);
                    g.DrawEllipse(gold, -2.5f * s - gr, ey - gr, gr * 2f, gr * 2f);
                    g.DrawEllipse(gold, 2.5f * s - gr, ey - gr, gr * 2f, gr * 2f);
                    g.DrawLine(gold, -0.1f * s, ey, 0.1f * s, ey);
                }
            }
            else if (style == 3)
            {
                // Altın Monokl + Zincir
                using (SolidBrush tint = new SolidBrush(Color.FromArgb(65, 224, 242, 254)))
                using (Pen gold = new Pen(Color.FromArgb(250, 250, 204, 21), Math.Max(1.3f, 1.5f * s)))
                using (Pen chain = new Pen(Color.FromArgb(200, 250, 204, 21), Math.Max(0.9f, 1.0f * s)) { DashStyle = DashStyle.Dot })
                {
                    float gr = 2.6f * s;
                    g.FillEllipse(tint, 2.4f * s - gr, ey - gr, gr * 2f, gr * 2f);
                    g.DrawEllipse(gold, 2.4f * s - gr, ey - gr, gr * 2f, gr * 2f);
                    g.DrawBezier(chain,
                        5.0f * s, ey + 0.6f * s,
                        6.8f * s, ey + 4.0f * s,
                        5.0f * s, ey + 8.0f * s,
                        3.6f * s, ey + 10.5f * s);
                }
            }
            else if (style == 4)
            {
                // Parlak Kayak Gözlüğü
                using (Pen strap = new Pen(Color.FromArgb(235, 30, 41, 59), Math.Max(2.2f, 2.6f * s)))
                    g.DrawLine(strap, -r3 * 0.96f, ey, r3 * 0.96f, ey);
                RectangleF visor = new RectangleF(-5.6f * s, ey - 2.4f * s, 11.2f * s, 4.6f * s);
                using (GraphicsPath vp = GraphicsExt.CreateRoundedRect(visor, 2.0f * s))
                using (LinearGradientBrush vb = new LinearGradientBrush(
                    visor,
                    Color.FromArgb(245, 56, 189, 248),
                    Color.FromArgb(245, 249, 115, 22),
                    LinearGradientMode.Horizontal))
                using (Pen vFrame = new Pen(Color.FromArgb(245, 248, 250, 252), Math.Max(1.1f, 1.3f * s)))
                {
                    g.FillPath(vb, vp);
                    g.DrawPath(vFrame, vp);
                }
            }
        }

        public static void DrawHatGeometry(Graphics g, float cx, float hy, float r3, float s, int style, Color color)
        {
            if (style == 0)
            {
                // 0: Ponponlu Kış Beresi
                using (SolidBrush hatBrush = new SolidBrush(color))
                {
                    float domeW = r3 * 1.95f;
                    float domeH = r3 * 1.55f;
                    g.FillPie(hatBrush, cx - domeW * 0.5f, hy - domeH * 0.75f, domeW, domeH * 1.4f, 180, 180);
                }
                float brimW = r3 * 2.15f;
                float brimH = Math.Max(3.5f, 4.2f * s);
                g.FillEllipse(WhiteBrush, cx - brimW * 0.5f, hy - brimH * 0.5f, brimW, brimH);
                g.DrawEllipse(OutlinePen, cx - brimW * 0.5f, hy - brimH * 0.5f, brimW, brimH);

                float pomR = Math.Max(3.2f, 3.8f * s);
                float pomY = hy - r3 * 1.22f;
                g.FillEllipse(WhiteBrush, cx - pomR, pomY - pomR, pomR * 2f, pomR * 2f);
                g.DrawEllipse(OutlinePen, cx - pomR, pomY - pomR, pomR * 2f, pomR * 2f);
            }
            else if (style == 1)
            {
                // 1: Klasik Silindir Şapka (Şeritli)
                using (SolidBrush hatBrush = new SolidBrush(color))
                using (Pen brimPen = new Pen(color, Math.Max(2.4f, 3.0f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (SolidBrush bandBrush = new SolidBrush(Color.FromArgb(245, 220, 38, 38)))
                {
                    g.DrawLine(brimPen, cx - r3 * 1.24f, hy, cx + r3 * 1.24f, hy);
                    g.FillRectangle(hatBrush, cx - r3 * 0.76f, hy - 9.4f * s, r3 * 1.52f, 9.4f * s);
                    g.FillRectangle(bandBrush, cx - r3 * 0.76f, hy - 2.6f * s, r3 * 1.52f, 2.0f * s);
                }
            }
            else if (style == 2)
            {
                // 2: Kıvrık Noel Şapkası
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddPolygon(new PointF[] {
                        new PointF(cx - r3 * 0.96f, hy),
                        new PointF(cx + r3 * 0.96f, hy),
                        new PointF(cx + r3 * 1.15f, hy - r3 * 1.15f),
                        new PointF(cx - r3 * 0.10f, hy - r3 * 1.35f)
                    });
                    using (SolidBrush capBrush = new SolidBrush(color))
                        g.FillPath(capBrush, gp);
                }
                float brimW = r3 * 2.25f;
                float brimH = Math.Max(3.8f, 4.6f * s);
                g.FillEllipse(WhiteBrush, cx - brimW * 0.5f, hy - brimH * 0.5f, brimW, brimH);
                g.DrawEllipse(OutlinePen, cx - brimW * 0.5f, hy - brimH * 0.5f, brimW, brimH);

                float pomR = Math.Max(3.2f, 3.8f * s);
                float pomX = cx + r3 * 1.20f;
                float pomY = hy - r3 * 0.95f;
                g.FillEllipse(WhiteBrush, pomX - pomR, pomY - pomR, pomR * 2f, pomR * 2f);
                g.DrawEllipse(OutlinePen, pomX - pomR, pomY - pomR, pomR * 2f, pomR * 2f);
            }
            else if (style == 3)
            {
                // 3: Mücevherli Altın Taç
                Color crownColor = Color.FromArgb(252, 250, 204, 21);
                PointF[] pts = new PointF[] {
                    new PointF(cx - r3 * 0.92f, hy + 0.5f * s),
                    new PointF(cx - r3 * 1.05f, hy - 7.5f * s),
                    new PointF(cx - r3 * 0.42f, hy - 3.6f * s),
                    new PointF(cx, hy - 9.2f * s),
                    new PointF(cx + r3 * 0.42f, hy - 3.6f * s),
                    new PointF(cx + r3 * 1.05f, hy - 7.5f * s),
                    new PointF(cx + r3 * 0.92f, hy + 0.5f * s)
                };
                using (SolidBrush cb = new SolidBrush(crownColor))
                using (Pen cp = new Pen(Color.FromArgb(240, 202, 138, 4), Math.Max(1.2f, 1.3f * s)))
                using (SolidBrush gem = new SolidBrush(color))
                {
                    g.FillPolygon(cb, pts);
                    g.DrawPolygon(cp, pts);
                    float jr = Math.Max(1.8f, 2.0f * s);
                    g.FillEllipse(gem, cx - jr, hy - 3.6f * s, jr * 2f, jr * 2f);
                }
            }
            else if (style == 4)
            {
                // 4: Kulaklıklı Kış Şapkası (Ushanka)
                using (SolidBrush hatBrush = new SolidBrush(color))
                {
                    float domeW = r3 * 2.05f;
                    float domeH = r3 * 1.55f;
                    g.FillPie(hatBrush, cx - domeW * 0.5f, hy - domeH * 0.72f, domeW, domeH * 1.35f, 180, 180);
                    // Yan kulaklıklar
                    g.FillEllipse(hatBrush, cx - r3 * 1.25f, hy - 1.2f * s, 4.4f * s, 7.8f * s);
                    g.FillEllipse(hatBrush, cx + r3 * 1.25f - 4.4f * s, hy - 1.2f * s, 4.4f * s, 7.8f * s);
                }
                // Ön kalın beyaz kürk şeridi
                RectangleF furFront = new RectangleF(cx - r3 * 0.92f, hy - 4.5f * s, r3 * 1.84f, 4.8f * s);
                using (GraphicsPath fp = GraphicsExt.CreateRoundedRect(furFront, 2.0f * s))
                {
                    g.FillPath(WhiteBrush, fp);
                    g.DrawPath(OutlinePen, fp);
                }
            }
            else
            {
                // 5: Yıldızlı Büyücü Şapkası
                using (SolidBrush wizBrush = new SolidBrush(color))
                using (Pen brimPen = new Pen(color, Math.Max(2.6f, 3.2f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (SolidBrush starBrush = new SolidBrush(Color.FromArgb(250, 253, 224, 71)))
                {
                    g.DrawLine(brimPen, cx - r3 * 1.45f, hy + 0.4f * s, cx + r3 * 1.45f, hy + 0.4f * s);
                    PointF[] cone = new PointF[] {
                        new PointF(cx - r3 * 0.88f, hy),
                        new PointF(cx + r3 * 0.88f, hy),
                        new PointF(cx + r3 * 0.25f, hy - 13.8f * s)
                    };
                    g.FillPolygon(wizBrush, cone);
                    // Altın kuşak & yıldızlar
                    g.FillRectangle(starBrush, cx - r3 * 0.84f, hy - 2.3f * s, r3 * 1.68f, 1.8f * s);
                    g.FillEllipse(starBrush, cx - 1.5f * s, hy - 6.8f * s, 2.6f * s, 2.6f * s);
                    g.FillEllipse(starBrush, cx + 1.4f * s, hy - 9.8f * s, 2.0f * s, 2.0f * s);
                }
            }
        }
    }

    internal struct WishLantern
    {
        public bool Active;
        public float X;
        public float Y;
        public float Vy;
        public float SwayPhase;
        public float Scale;
        public float FlickerPhase;
        public int WarmHue; // 0: Amber, 1: Gold, 2: Soft Coral
    }

    internal struct ConfettiParticle
    {
        public bool Active;
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float Angle;
        public float Spin;
        public float Size;
        public int Ttl;
        public int MaxTtl;
        public Color Color;
    }

    internal struct MagicSparkle
    {
        public bool Active;
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float Size;
        public int Ttl;
        public int MaxTtl;
        public bool IsRing;
        public Color Color;
    }

    internal sealed class WinterBirdEntity
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float TargetX;
        public float TargetY;
        public IntPtr SurfaceHwnd;
        public ChristmasTreeEntity PerchedTree;
        public float TreeOffsetX;
        public float TreeOffsetY;
        public int Species; // 0: Kızılgerdan (Robin), 1: Kar Baykuşu (Snowy Owl)
        public int Direction = 1;
        public int State; // 0: Perched, 1: Flying
        public int StateTimer;
        public float WingPhase;
        public float HopOffset;
        public float LookPhase;

        public WinterBirdEntity(float x, float y, int species, Random rng)
        {
            X = x;
            Y = y;
            Species = species;
            Direction = rng.Next(2) == 0 ? 1 : -1;
            State = 1;
            StateTimer = 40 + rng.Next(60);
            WingPhase = (float)(rng.NextDouble() * Math.PI * 2.0);
            LookPhase = (float)(rng.NextDouble() * Math.PI * 2.0);
        }
    }

    internal sealed class ChristmasTreeEntity
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public IntPtr SurfaceHwnd;
        public bool Dragging;
        public float WobbleAngle;
        public float WobbleVel;
        public float Scale = 1.0f;         // 0.60 .. 1.60
        public int TreeStyle;              // 0: Klasik Çam, 1: Karlı Çam, 2: Buz Beyazı, 3: Pembe/Altın
        public int TopperStyle = 1;        // 0: Yok, 1: Altın Yıldız, 2: Kar Tanesi, 3: Kırmızı Fiyonk
        public int LightStyle = 1;         // 0: Yok, 1: Sıcak Altın, 2: Çok Renkli, 3: Buz Mavisi
        public int LightAnim = 1;          // 0: Sabit, 1: Parıldayan, 2: Dalga, 3: Müziğe Duyarlı
        public int OrnamentStyle = 1;      // 0: Yok, 1: Klasik Toplar, 2: Şeker Baston, 3: Mini Yıldızlar
        public Color OrnamentColor = Color.FromArgb(255, 220, 38, 38);
        public int GarlandStyle = 1;       // 0: Yok, 1: Altın Sarmal, 2: Kırmızı Kurdele, 3: Gümüş İnci
        public bool HasGifts = true;
        public int HitCooldown;

        public static readonly Color[] OrnamentPalette = new Color[]
        {
            Color.FromArgb(255, 220, 38, 38),   // Yılbaşı Kırmızısı
            Color.FromArgb(255, 250, 204, 21),  // Parlak Altın
            Color.FromArgb(255, 56, 189, 248),  // Buz Mavisi
            Color.FromArgb(255, 244, 114, 182), // Şeker Pembe
            Color.FromArgb(255, 168, 85, 247),  // Lavanta Mor
            Color.FromArgb(255, 52, 211, 153),  // Zümrüt Yeşil
            Color.FromArgb(255, 249, 115, 22),  // Sıcak Turuncu
            Color.FromArgb(255, 248, 250, 252)  // İnci Beyazı
        };

        public float TotalHeight { get { return 118f * Scale; } }
        public float HalfWidth { get { return 42f * Scale; } }

        public ChristmasTreeEntity(float x, float y, IntPtr surfaceHwnd, Random rng)
        {
            X = x;
            Y = y;
            SurfaceHwnd = surfaceHwnd;
            TreeStyle = rng.Next(2); // Klasik veya Karlı Çam
            TopperStyle = 1;
            LightStyle = 1 + rng.Next(2);
            LightAnim = 1;
            OrnamentStyle = 1;
            OrnamentColor = OrnamentPalette[rng.Next(4)];
            GarlandStyle = 1;
            HasGifts = true;
        }

        public void ApplyScale(float scale)
        {
            Scale = Math.Max(0.60f, Math.Min(1.60f, scale));
        }

        public void ApplyPreset(int preset)
        {
            if (preset == 0)
            {
                // Klasik Yılbaşı
                TreeStyle = 1; TopperStyle = 1; LightStyle = 1; LightAnim = 1;
                OrnamentStyle = 1; OrnamentColor = OrnamentPalette[0]; GarlandStyle = 1; HasGifts = true;
            }
            else if (preset == 1)
            {
                // Buzul Kristali
                TreeStyle = 2; TopperStyle = 2; LightStyle = 3; LightAnim = 2;
                OrnamentStyle = 3; OrnamentColor = OrnamentPalette[2]; GarlandStyle = 3; HasGifts = true;
            }
            else if (preset == 2)
            {
                // Altın Gala
                TreeStyle = 0; TopperStyle = 1; LightStyle = 1; LightAnim = 1;
                OrnamentStyle = 1; OrnamentColor = OrnamentPalette[1]; GarlandStyle = 1; HasGifts = true;
            }
            else
            {
                // Şeker & Parti
                TreeStyle = 3; TopperStyle = 3; LightStyle = 2; LightAnim = 2;
                OrnamentStyle = 2; OrnamentColor = OrnamentPalette[3]; GarlandStyle = 2; HasGifts = true;
            }
        }

        public void Randomize(Random rng)
        {
            TreeStyle = rng.Next(4);
            TopperStyle = 1 + rng.Next(3);
            LightStyle = 1 + rng.Next(3);
            LightAnim = rng.Next(4);
            OrnamentStyle = 1 + rng.Next(3);
            OrnamentColor = OrnamentPalette[rng.Next(OrnamentPalette.Length)];
            GarlandStyle = rng.Next(4);
            HasGifts = rng.Next(100) < 85;
        }

        public bool HitTest(float px, float py)
        {
            float hw = HalfWidth + 8f;
            float th = TotalHeight + 14f;
            if (Math.Abs(px - X) > hw || py > Y + 8f || py < Y - th) return false;
            // Üçgen gövde yaklaşık kontrolü (alt kısım geniş, tepe dar)
            float relY = (Y - py) / Math.Max(1f, th); // 0 = taban, 1 = tepe
            float allowedW = hw * (1.12f - relY * 0.72f);
            return Math.Abs(px - X) <= allowedW;
        }

        public PointF GetPerchOffset(int slot)
        {
            // Ağaç dallarında kuşların konabileceği noktalar (yerel ofset)
            if (slot == 0) return new PointF(-20f * Scale, -56f * Scale);
            if (slot == 1) return new PointF(18f * Scale, -64f * Scale);
            return new PointF(-12f * Scale, -86f * Scale);
        }
    }

    internal static class TreeRenderer
    {
        private static readonly PointF[] OrnamentOffsets = new PointF[]
        {
            new PointF(-22f, -32f), new PointF(-4f, -27f), new PointF(16f, -31f), new PointF(28f, -24f),
            new PointF(-17f, -52f), new PointF(3f, -48f),  new PointF(20f, -53f),
            new PointF(-13f, -72f), new PointF(7f, -70f),  new PointF(-4f, -88f), new PointF(10f, -86f)
        };

        private static readonly PointF[] LightOffsets = new PointF[]
        {
            new PointF(-29f, -23f), new PointF(-14f, -30f), new PointF(4f, -22f),   new PointF(21f, -29f),
            new PointF(-24f, -43f), new PointF(-8f, -49f),  new PointF(10f, -43f),  new PointF(24f, -47f),
            new PointF(-18f, -63f), new PointF(0f, -67f),   new PointF(16f, -62f),
            new PointF(-12f, -82f), new PointF(6f, -80f),   new PointF(-2f, -96f)
        };

        private static readonly Color[] MultiLightColors = new Color[]
        {
            Color.FromArgb(255, 250, 204, 21),
            Color.FromArgb(255, 248, 113, 113),
            Color.FromArgb(255, 56, 189, 248),
            Color.FromArgb(255, 52, 211, 153),
            Color.FromArgb(255, 244, 114, 182)
        };

        public static void DrawTree(
            Graphics g, ChristmasTreeEntity tree,
            float drawX, float drawY, float scaleOverride,
            float time, float audioLevel, float audioBeat, bool isHighlighted)
        {
            float sc = tree.Scale * scaleOverride;
            GraphicsState st = g.Save();
            g.TranslateTransform(drawX, drawY);
            g.ScaleTransform(sc, sc);
            g.RotateTransform(tree.WobbleAngle);

            // Seçili ağaç için alt ışık halkası (Süsleme penceresi açıkken)
            if (isHighlighted)
            {
                float pulse = 0.65f + 0.35f * (float)Math.Sin(time * 5.0f);
                int ga = (int)(85 + 55 * pulse);
                using (SolidBrush halo = new SolidBrush(Color.FromArgb(ga / 3, 52, 211, 153)))
                using (Pen ring = new Pen(Color.FromArgb(ga, 52, 211, 153), 2.0f))
                {
                    g.FillEllipse(halo, -42f, -7f, 84f, 14f);
                    g.DrawEllipse(ring, -42f, -7f, 84f, 14f);
                }
            }

            // 1. Alt gölge + Kütük gövde
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(75, 15, 23, 42)))
                g.FillEllipse(shadow, -34f, -5f, 68f, 10f);
            using (LinearGradientBrush trunkGrad = new LinearGradientBrush(
                new RectangleF(-8f, -20f, 16f, 20f),
                Color.FromArgb(252, 120, 68, 36),
                Color.FromArgb(252, 72, 38, 18),
                LinearGradientMode.Horizontal))
            {
                g.FillRectangle(trunkGrad, -7.5f, -19f, 15f, 19f);
            }

            // 2. Ağaç yaprak renk paleti
            Color cTop, cBot, cRim;
            bool snowTips = false;
            if (tree.TreeStyle == 0)
            {
                // Klasik Çam
                cTop = Color.FromArgb(252, 34, 139, 76);
                cBot = Color.FromArgb(252, 14, 72, 42);
                cRim = Color.FromArgb(220, 12, 54, 32);
            }
            else if (tree.TreeStyle == 1)
            {
                // Karlı Çam
                cTop = Color.FromArgb(252, 30, 128, 86);
                cBot = Color.FromArgb(252, 15, 76, 54);
                cRim = Color.FromArgb(220, 14, 58, 44);
                snowTips = true;
            }
            else if (tree.TreeStyle == 2)
            {
                // Buz Beyazı / Mavi Modern Ağaç
                cTop = Color.FromArgb(252, 236, 248, 255);
                cBot = Color.FromArgb(252, 147, 197, 253);
                cRim = Color.FromArgb(210, 96, 165, 250);
            }
            else
            {
                // Pembe / Altın Kış Ağacı
                cTop = Color.FromArgb(252, 251, 207, 232);
                cBot = Color.FromArgb(252, 219, 112, 147);
                cRim = Color.FromArgb(215, 190, 74, 116);
                snowTips = true;
            }

            // 3. 4 Katmanlı Çam Dalları (alttan üste, yumuşak kavisli eteklerle)
            float[] tierBaseY = new float[] { -15f, -40f, -64f, -85f };
            float[] tierTipY = new float[] { -54f, -76f, -97f, -113f };
            float[] tierHalfW = new float[] { 40f, 33f, 25f, 16.5f };

            for (int t = 0; t < 4; t++)
            {
                float by = tierBaseY[t];
                float ty = tierTipY[t];
                float hw = tierHalfW[t];
                using (GraphicsPath gp = new GraphicsPath())
                {
                    PointF[] pts = new PointF[]
                    {
                        new PointF(0f, ty),
                        new PointF(hw, by - 2f),
                        new PointF(hw * 0.55f, by + 3.5f),
                        new PointF(0f, by + 1.0f),
                        new PointF(-hw * 0.55f, by + 3.5f),
                        new PointF(-hw, by - 2f)
                    };
                    gp.AddPolygon(pts);

                    RectangleF gradR = new RectangleF(-hw - 1f, ty - 1f, hw * 2f + 2f, (by - ty) + 8f);
                    using (LinearGradientBrush tb = new LinearGradientBrush(gradR, cTop, cBot, LinearGradientMode.Vertical))
                    using (Pen tp = new Pen(cRim, 1.15f))
                    {
                        g.FillPath(tb, gp);
                        g.DrawPath(tp, gp);
                    }
                }

                // Dalların uçlarında yumuşak kar birikintisi
                if (snowTips)
                {
                    using (Pen snowPen = new Pen(Color.FromArgb(230, 248, 252, 255), 3.0f)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    })
                    {
                        g.DrawLine(snowPen, -hw + 2f, by - 2.5f, -hw * 0.45f, by + 2.2f);
                        g.DrawLine(snowPen, hw - 2f, by - 2.5f, hw * 0.45f, by + 2.2f);
                    }
                }
            }

            // 4. Sarmal Kurdele / Çelenk (Garland)
            if (tree.GarlandStyle != 0)
            {
                Color gc = tree.GarlandStyle == 1
                    ? Color.FromArgb(245, 250, 204, 21)
                    : (tree.GarlandStyle == 2 ? Color.FromArgb(245, 239, 68, 68) : Color.FromArgb(245, 226, 232, 240));
                using (Pen gPen = new Pen(gc, tree.GarlandStyle == 3 ? 2.1f : 2.8f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    DashStyle = tree.GarlandStyle == 3 ? DashStyle.Dot : DashStyle.Solid
                })
                {
                    g.DrawBezier(gPen, -14f, -84f, -4f, -77f, 6f, -77f, 15f, -82f);
                    g.DrawBezier(gPen, -22f, -62f, -8f, -53f, 8f, -53f, 23f, -59f);
                    g.DrawBezier(gPen, -31f, -38f, -10f, -27f, 12f, -27f, 32f, -35f);
                }
            }

            // 5. Ağaç Süsleri (Ornaments)
            if (tree.OrnamentStyle != 0)
            {
                Color oc = tree.OrnamentColor.A > 0 ? tree.OrnamentColor : Color.FromArgb(255, 220, 38, 38);
                Color altGold = Color.FromArgb(255, 250, 204, 21);
                using (SolidBrush bMain = new SolidBrush(oc))
                using (SolidBrush bAlt = new SolidBrush(altGold))
                using (SolidBrush bSpec = new SolidBrush(Color.FromArgb(190, 255, 255, 255)))
                {
                    for (int i = 0; i < OrnamentOffsets.Length; i++)
                    {
                        float ox = OrnamentOffsets[i].X;
                        float oy = OrnamentOffsets[i].Y;
                        if (tree.OrnamentStyle == 1)
                        {
                            // Klasik parlak yılbaşı topları
                            SolidBrush fill = (i % 3 == 1) ? bAlt : bMain;
                            g.FillEllipse(fill, ox - 3.6f, oy - 3.6f, 7.2f, 7.2f);
                            g.FillEllipse(bSpec, ox - 2.0f, oy - 2.2f, 2.2f, 2.2f);
                        }
                        else if (tree.OrnamentStyle == 2)
                        {
                            // Mini Baston Şekerler
                            using (Pen pw = new Pen(Color.White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            using (Pen pr = new Pen(oc, 2.2f) { DashStyle = DashStyle.Dash })
                            {
                                g.DrawLine(pw, ox, oy + 4f, ox, oy - 3f);
                                g.DrawArc(pw, ox, oy - 6f, 4.5f, 4.5f, 180f, 180f);
                                g.DrawLine(pr, ox, oy + 4f, ox, oy - 3f);
                            }
                        }
                        else
                        {
                            // Parlayan Mini Yıldızlar / Kar Taneleri
                            SolidBrush fill = (i % 2 == 0) ? bMain : bAlt;
                            DrawMiniStar(g, fill, ox, oy, 4.2f);
                        }
                    }
                }
            }

            // 6. Animasyonlu Yılbaşı Işıkları
            if (tree.LightStyle != 0)
            {
                for (int i = 0; i < LightOffsets.Length; i++)
                {
                    float lx = LightOffsets[i].X;
                    float ly = LightOffsets[i].Y;

                    Color lc;
                    if (tree.LightStyle == 1) lc = Color.FromArgb(255, 254, 240, 138);
                    else if (tree.LightStyle == 2) lc = MultiLightColors[i % MultiLightColors.Length];
                    else lc = Color.FromArgb(255, 125, 211, 252);

                    float b = 1.0f;
                    if (tree.LightAnim == 1)
                        b = 0.45f + 0.55f * (float)(Math.Sin(time * 4.2f + i * 1.45f) * 0.5 + 0.5);
                    else if (tree.LightAnim == 2)
                        b = 0.35f + 0.65f * (float)(Math.Sin(time * 5.0f - ly * 0.09f) * 0.5 + 0.5);
                    else if (tree.LightAnim == 3)
                        b = Math.Min(1.35f, 0.38f + audioLevel * 1.15f + audioBeat * 0.55f);

                    int glowA = Math.Max(18, Math.Min(115, (int)(78f * b)));
                    int coreA = Math.Max(110, Math.Min(255, (int)(245f * Math.Min(1f, b))));
                    float gr = 5.5f + b * 2.5f;

                    using (SolidBrush gb = new SolidBrush(Color.FromArgb(glowA, lc.R, lc.G, lc.B)))
                    using (SolidBrush cb = new SolidBrush(Color.FromArgb(coreA, lc.R, lc.G, lc.B)))
                    using (SolidBrush wb = new SolidBrush(Color.FromArgb(Math.Min(230, coreA), 255, 255, 255)))
                    {
                        g.FillEllipse(gb, lx - gr, ly - gr, gr * 2f, gr * 2f);
                        g.FillEllipse(cb, lx - 2.3f, ly - 2.3f, 4.6f, 4.6f);
                        g.FillEllipse(wb, lx - 1.0f, ly - 1.0f, 2.0f, 2.0f);
                    }
                }
            }

            // 7. Tepe Süsü (Topper)
            if (tree.TopperStyle == 1)
            {
                // Parlayan Altın Yıldız
                float pulse = 0.85f + 0.2f * (float)Math.Sin(time * 4.5f) + audioBeat * 0.25f;
                using (SolidBrush halo = new SolidBrush(Color.FromArgb(72, 253, 224, 71)))
                using (SolidBrush starB = new SolidBrush(Color.FromArgb(255, 250, 204, 21)))
                using (Pen starP = new Pen(Color.FromArgb(250, 254, 249, 195), 1.0f))
                {
                    float hr = 13f * pulse;
                    g.FillEllipse(halo, -hr, -115f - hr, hr * 2f, hr * 2f);
                    DrawFivePointStar(g, starB, starP, 0f, -115f, 9.5f * pulse, 4.2f * pulse);
                }
            }
            else if (tree.TopperStyle == 2)
            {
                // Kristal Kar Tanesi Tepe Süsü
                using (SolidBrush halo = new SolidBrush(Color.FromArgb(72, 125, 211, 252)))
                using (Pen sp = new Pen(Color.FromArgb(252, 224, 242, 254), 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.FillEllipse(halo, -12f, -127f, 24f, 24f);
                    for (int a = 0; a < 6; a++)
                    {
                        double rad = a * Math.PI / 3.0;
                        float dx = (float)Math.Cos(rad) * 8.5f;
                        float dy = (float)Math.Sin(rad) * 8.5f;
                        g.DrawLine(sp, 0f, -115f, dx, -115f + dy);
                    }
                }
            }
            else if (tree.TopperStyle == 3)
            {
                // Kırmızı Kadife Fiyonk
                using (SolidBrush bow = new SolidBrush(Color.FromArgb(252, 220, 38, 38)))
                using (Pen gold = new Pen(Color.FromArgb(240, 250, 204, 21), 1.2f))
                {
                    g.FillEllipse(bow, -11f, -120f, 11f, 8f);
                    g.FillEllipse(bow, 0f, -120f, 11f, 8f);
                    g.DrawEllipse(gold, -11f, -120f, 11f, 8f);
                    g.DrawEllipse(gold, 0f, -120f, 11f, 8f);
                    g.FillEllipse(bow, -3.5f, -119f, 7f, 6.5f);
                }
            }

            // 8. Ağaç Altı Minik Hediye Paketleri
            if (tree.HasGifts)
            {
                DrawMiniGift(g, -23f, -11f, 13f, 11f, Color.FromArgb(250, 220, 38, 38), Color.FromArgb(250, 250, 204, 21));
                DrawMiniGift(g, 11f, -13f, 14f, 13f, Color.FromArgb(250, 37, 99, 235), Color.FromArgb(250, 248, 250, 252));
                DrawMiniGift(g, -5f, -9f, 11f, 9f, Color.FromArgb(250, 147, 51, 234), Color.FromArgb(250, 253, 224, 71));
            }

            g.Restore(st);
        }

        private static void DrawMiniGift(Graphics g, float x, float y, float w, float h, Color boxCol, Color ribCol)
        {
            using (SolidBrush bb = new SolidBrush(boxCol))
            using (SolidBrush rb = new SolidBrush(ribCol))
            using (Pen rim = new Pen(Color.FromArgb(140, 15, 23, 42), 0.9f))
            {
                g.FillRectangle(bb, x, y, w, h);
                g.FillRectangle(rb, x + w * 0.5f - 1.4f, y, 2.8f, h);
                g.FillRectangle(rb, x, y + h * 0.45f - 1.2f, w, 2.4f);
                g.DrawRectangle(rim, x, y, w, h);
            }
        }

        private static void DrawMiniStar(Graphics g, Brush b, float cx, float cy, float r)
        {
            PointF[] pts = new PointF[8];
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4.0;
                float rad = (i % 2 == 0) ? r : r * 0.38f;
                pts[i] = new PointF(cx + (float)Math.Cos(a) * rad, cy + (float)Math.Sin(a) * rad);
            }
            g.FillPolygon(b, pts);
        }

        private static void DrawFivePointStar(Graphics g, Brush b, Pen p, float cx, float cy, float outerR, float innerR)
        {
            PointF[] pts = new PointF[10];
            for (int i = 0; i < 10; i++)
            {
                double a = -Math.PI * 0.5 + i * Math.PI / 5.0;
                float r = (i % 2 == 0) ? outerR : innerR;
                pts[i] = new PointF(cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r);
            }
            g.FillPolygon(b, pts);
            g.DrawPolygon(p, pts);
        }
    }

    internal sealed class SnowPlowEntity
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float Angle; // Havada fırlatıldığında dönüş açısı (derece)
        public float AngularVel;
        public IntPtr SurfaceHwnd;
        public int Direction = 1;
        public float DriveSpeed = 2.6f;
        public float WheelPhase = 0f;
        public bool Dragging;
        public bool IsAirborne;
        public float PlowedSnowMass; // Bıçağın önünde biriken görünür kar kütlesi (0..100)

        public SnowPlowEntity(float x, float y, IntPtr surfaceHwnd)
        {
            X = x;
            Y = y;
            SurfaceHwnd = surfaceHwnd;
            PlowedSnowMass = 0f; // Kepçe boş başlar; küredikçe dolar
        }
    }

    // ========================================================================
    // 5. PER-PIXEL ALPHA TAM EKRAN FİZİK VE ÇİZİM KATMANI (OVERLAY)
    // ========================================================================
    internal sealed class SnowOverlayForm : Form
    {
        public const int MaxSnowflakes = 2600;
        public const int MaxChunks = 2200;

        private int _originX;
        private int _originY;
        private int _screenW;
        private int _screenH;
        private int _activeMonitorCount = 1;
        private Rectangle[] _monitorBounds = new Rectangle[1];
        private Rectangle[] _monitorWorkAreas = new Rectangle[1];
        private readonly Random _rng = new Random();

        // Kalıcı 32-bit DIBSection GDI kaynakları
        private IntPtr _screenDc = IntPtr.Zero;
        private IntPtr _memDc = IntPtr.Zero;
        private IntPtr _dibBitmap = IntPtr.Zero;
        private IntPtr _oldBitmap = IntPtr.Zero;
        private IntPtr _dibBits = IntPtr.Zero;
        private Bitmap _surfaceBitmap;
        private Graphics _surfaceGraphics;

        // Kullanıcı Ayarları
        public bool InteractiveMode { get; private set; }
        public bool FairyLightsEnabled { get; set; }
        public bool IciclesEnabled { get; set; }
        public bool CableCarEnabled { get; set; }
        public bool SantaEnabled { get; set; }
        public bool AuroraEnabled { get; set; }
        public bool FrostEnabled { get; set; }
        public bool TrainEnabled { get; set; }
        public bool SnowmanFightEnabled { get; set; }
        public bool SoundEnabled { get; set; }
        public bool AudioReactiveEnabled { get; set; }
        public bool RememberSnowmen { get; set; }
        public bool CountdownEnabled { get; set; }
        public bool LanternsEnabled { get; set; }
        public bool BirdsEnabled { get; set; }
        public bool CursorTrailEnabled { get; set; }
        public int WallpaperTheme { get; set; }         // 0: Orijinal, 1..5: Gömülü Kış Temaları, 6: Özel Fotoğraf
        public string CustomWallpaperPath { get; set; }
        public bool RememberWallpaper { get; set; }
        public float AudioLevel { get { return _audioLevelSmooth; } }
        public bool BlizzardMode { get; set; }
        public bool MultiMonitorEnabled { get; private set; }
        public int ActiveMonitorCount { get { return _activeMonitorCount; } }
        public bool IsEnglish { get; set; }
        public int ActiveSnowCount { get; set; }
        public float MaxPileHeight { get; set; }
        public float WindNx { get; set; }
        public float WindNy { get; set; }
        public float WindowInteriorSnowOpacity { get; set; } // 0.0f .. 1.0f (Uygulamaların içine yağarken şeffaflık)
        public IntPtr ControlPanelHwnd { get; set; }
        public IntPtr WardrobeHwnd { get; set; }
        public IntPtr TreeDecoratorHwnd { get; set; }
        public bool Paused { get; private set; }
        public float GustAmount { get; set; }          // 0..1 doğal rüzgar hamlesi şiddeti
        public float MeltRate { get; set; }            // Karelik erime oranı (0 = erimez)
        public bool AutoPauseOnFullscreen { get; set; }
        public bool PerformanceMode
        {
            get { return _performanceMode; }
            set
            {
                _performanceMode = value;
                if (_surfaceGraphics != null)
                    _surfaceGraphics.SmoothingMode = value ? SmoothingMode.HighSpeed : SmoothingMode.AntiAlias;
            }
        }
        public event Action OnPauseChanged;

        // Havuzlar ve Varlıklar
        private readonly Snowflake[] _flakes = new Snowflake[MaxSnowflakes];
        private readonly PhysicalSnowChunk[] _chunks = new PhysicalSnowChunk[MaxChunks];
        private readonly FlyingHat[] _flyingHats = new FlyingHat[32];
        private readonly FallingIcicle[] _fallingIcicles = new FallingIcicle[96];
        private readonly Dictionary<IntPtr, WindowSurface> _surfaces = new Dictionary<IntPtr, WindowSurface>();
        private readonly List<WindowSurface> _orderedSurfaces = new List<WindowSurface>();
        private readonly List<SnowballEntity> _snowballs = new List<SnowballEntity>();
        private readonly List<SnowmanEntity> _snowmen = new List<SnowmanEntity>();
        private readonly List<ChristmasTreeEntity> _trees = new List<ChristmasTreeEntity>();
        private readonly List<WinterBirdEntity> _birds = new List<WinterBirdEntity>();
        private readonly List<SnowPlowEntity> _plows = new List<SnowPlowEntity>();
        private readonly List<CampfireEntity> _campfires = new List<CampfireEntity>();
        private readonly List<GiftBoxEntity> _gifts = new List<GiftBoxEntity>();
        private readonly List<PenguinEntity> _penguins = new List<PenguinEntity>();
        private readonly FloatingPopup[] _popups = new FloatingPopup[24];
        private readonly WishLantern[] _lanterns = new WishLantern[18];
        private readonly ConfettiParticle[] _confetti = new ConfettiParticle[220];
        private readonly MagicSparkle[] _magicSparkles = new MagicSparkle[96];
        private CampfireEntity _draggedCampfire;
        private PenguinEntity _draggedPenguin;
        private ChristmasTreeEntity _draggedTree;
        private Point _penguinMouseDownPos;
        private Point _lastCursorPos;

        // v1.4 Yılbaşı Geri Sayımı, Dilek Fenerleri, Kar Küresi Sallama & Kartpostal Bildirimi
        private TreeDecoratorForm _treeDecoratorForm;
        private float _countdownX = -1f;
        private float _countdownY = 26f;
        private bool _draggingCountdown;
        private PointF _countdownDragOffset;
        private int _celebrationBannerTicks;
        private int _celebrationYear;
        private int _lastCelebratedYear;
        private int _lanternSpawnCooldown = 90;
        private Point _lastTrailCursor;
        private bool _hasLastTrailCursor;
        private string _toastText = "";
        private int _toastTicks;
        private bool _f10WasDown;

        // Ses Efektleri & Müziğe Duyarlı Sistem (WASAPI)
        private readonly WinterAudioSynth _audioSynth = new WinterAudioSynth();
        private readonly SystemAudioMeter _audioMeter = new SystemAudioMeter();
        private float _audioLevelSmooth;
        private float _audioAvg;
        private float _audioBeat;

        // Ekran Köşesi Buzlanması (0: SolÜst, 1: SağÜst, 2: SolAlt, 3: SağAlt)
        private readonly float[] _cornerFrost = new float[4];

        // Kutup Ekspresi (Polar Express) Görev Çubuğu Treni
        private float _trainX = -260f;
        private float _trainY;
        private int _trainDir = 1;
        private float _trainWheelPhase;
        private bool _trainVisible;
        private int _trainWhistleCooldown;
        private readonly TrainSmoke[] _trainSmoke = new TrainSmoke[48];

        // Noel Baba ve Ren Geyikleri (Arka Plan Rastgele Uçuş Sistemi)
        private bool _santaActive;
        private int _santaCooldownTicks;
        private float _santaX;
        private float _santaY;
        private float _santaBaseY;
        private float _santaPrevY;
        private float _santaSpeed;
        private float _santaScale;
        private int _santaDir = 1;
        private float _santaWavePhase;
        private float _santaGallopPhase;
        private float _santaGiftDropX1;
        private float _santaGiftDropX2;
        private bool _santaDroppedGift1;
        private bool _santaDroppedGift2;
        private int _santaHitCooldown;
        private readonly SantaSparkle[] _santaSparkles = new SantaSparkle[120];

        // Sürpriz Sistemi: Kuzey Işıkları (Aurora Borealis), Kayan Yıldızlar & Havai Fişek Gösterisi
        private float _auroraSurge;
        private const int AuroraBufW = 160;
        private const int AuroraBufH = 48;
        private readonly object _auroraLock = new object();
        private readonly int[] _auroraSmallBuf = new int[AuroraBufW * AuroraBufH];
        private readonly float[] _auroraRowFade = new float[AuroraBufH];
        private readonly float[] _auroraWaveTop0 = new float[AuroraBufW];
        private readonly float[] _auroraWaveThick0 = new float[AuroraBufW];
        private readonly float[] _auroraWaveCurtain0 = new float[AuroraBufW];
        private readonly float[] _auroraWaveTop1 = new float[AuroraBufW];
        private readonly float[] _auroraWaveThick1 = new float[AuroraBufW];
        private readonly float[] _auroraWaveCurtain1 = new float[AuroraBufW];
        private bool _auroraRowFadeInit;
        private int _auroraW;
        private int _auroraH;
        private int[] _auroraFront;
        private int[] _auroraSpare;
        private int[] _auroraTmpHoriz;
        private int[] _auroraX0Map;
        private int[] _auroraX1Map;
        private int[] _auroraXFracMap;
        private int _auroraWorking;
        private bool _auroraHasFrame;
        private bool _auroraDisposed;
        private int _shootingStarCooldown;
        private readonly ShootingStar[] _shootingStars = new ShootingStar[4];
        private readonly FireworkRocket[] _rockets = new FireworkRocket[16];
        private readonly FireworkSpark[] _sparks = new FireworkSpark[420];
        private int _fireworkShowRemaining;
        private int _fireworkShowTimer;
        private SnowmanWardrobeForm _wardrobeForm;

        // İki uygulama arası Teleferik (Cable Car / Ski Gondola) durumu
        private float _cableCarT = 0.25f;          // 0.0 (İstasyon A) .. 1.0 (İstasyon B)
        private int _cableCarDir = 1;              // 1: A->B, -1: B->A
        private int _cableCarWaitTicks = 0;        // İstasyonda yolcu indirme/bindirme beklemesi
        private float _cableCarSwingAngle = 0f;    // Kabin sarkaç salınım açısı (derece)
        private float _cableCarSwingVel = 0f;
        private float _cableWheelAngle = 0f;       // Direk makarası dönüş açısı
        private bool _cableCarHasValidSpan = false;
        private float _cableAx, _cableAy, _cableBx, _cableBy;

        // Fare Sürükleme & Fırlatma Hız Geçmişi (Velocity Tracker)
        private SnowPlowEntity _draggedPlow;
        private SnowballEntity _draggedSnowball;
        private SnowmanEntity _draggedSnowman;
        private bool _creatingNewSnowballOnDrag;
        private readonly PointF[] _mouseHistory = new PointF[6];
        private int _mouseHistoryIdx;

        private int _frameCount;
        private DateTime _lastFullEnumScan = DateTime.MinValue;
        private bool _needsImmediateFullScan;
        private IntPtr _lastForegroundHwnd = IntPtr.Zero;
        private IntPtr _hookSystem = IntPtr.Zero;
        private IntPtr _hookObject = IntPtr.Zero;
        private NativeMethods.WinEventDelegate _winEventProc; // GC tarafından toplanmaması için referans tutulur
        private bool _f6WasDown;
        private bool _f7WasDown;
        private bool _f8WasDown;
        private bool _f9WasDown;
        private readonly Timer _renderTimer;

        // Sabit zaman adımlı fizik
        private const double PhysicsStep = 1.0 / 60.0;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastTickTime;
        private double _accum;
        private float _time;
        private int _frameTick;
        private bool _fullscreenSuspended;
        private bool _clearedForSuspend;
        private bool _performanceMode;

        // Önbelleğe alınmış çizim nesneleri (her karede yeniden oluşturulmaz)
        private readonly SolidBrush[] _alphaYellowBrushes = new SolidBrush[256];
        private SolidBrush[] _lightGlowBrushes;
        private SolidBrush[] _lightMidBrushes;
        private SolidBrush _lightCoreBrush;
        private readonly Dictionary<int, PointF[]> _wireBuffers = new Dictionary<int, PointF[]>();

        public event Action OnInteractiveModeChanged;

        // Önceden oluşturulmuş fırçalar
        private readonly SolidBrush[] _alphaWhiteBrushes = new SolidBrush[256];
        private readonly SolidBrush[] _alphaIceBrushes = new SolidBrush[256];
        private readonly Pen _wirePen = new Pen(Color.FromArgb(190, 51, 65, 85), 1.5f);
        private readonly Pen _snowHighlightPen = new Pen(Color.FromArgb(245, 255, 255, 255), 2.2f);
        private readonly Pen _snowOutlinePen = new Pen(Color.FromArgb(185, 148, 163, 184), 1.3f);
        private readonly SolidBrush _coalBrush = new SolidBrush(Color.FromArgb(248, 15, 23, 42));
        private readonly SolidBrush _carrotBrush = new SolidBrush(Color.FromArgb(252, 249, 115, 22));

        public SnowOverlayForm()
        {
            ComputeMonitorBounds(false);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(_originX, _originY, _screenW, _screenH);
            TopMost = true;
            ShowInTaskbar = false;

            InteractiveMode = false;
            FairyLightsEnabled = true;
            IciclesEnabled = true;
            CableCarEnabled = true;
            SantaEnabled = true;
            AuroraEnabled = true;
            FrostEnabled = true;
            TrainEnabled = true;
            SnowmanFightEnabled = true;
            SoundEnabled = true;
            AudioReactiveEnabled = false;
            RememberSnowmen = true;
            CountdownEnabled = true;
            LanternsEnabled = true;
            BirdsEnabled = true;
            CursorTrailEnabled = false;
            WallpaperTheme = 0;
            CustomWallpaperPath = "";
            RememberWallpaper = true;
            BlizzardMode = false;
            ActiveSnowCount = 420;
            MaxPileHeight = 34f;
            WindNx = -0.22f;
            WindNy = 0.08f;
            WindowInteriorSnowOpacity = 0.30f; // Varsayılan: pencerelerin içinde %30 hafif şeffaf
            GustAmount = 0.25f;
            MeltRate = 0f;
            AutoPauseOnFullscreen = true;

            // İlk Noel Baba geçişi 12..28 sn sonra, ilk kayan yıldız 8..20 sn sonra
            _santaCooldownTicks = 60 * (12 + _rng.Next(16));
            _shootingStarCooldown = 60 * (8 + _rng.Next(12));

            for (int i = 0; i < 256; i++)
            {
                _alphaWhiteBrushes[i] = new SolidBrush(Color.FromArgb(i, 255, 255, 255));
                _alphaIceBrushes[i] = new SolidBrush(Color.FromArgb(i, 219, 234, 254));
                _alphaYellowBrushes[i] = new SolidBrush(Color.FromArgb(i, 253, 224, 71));
            }
            InitCachedDrawingObjects();

            InitParticles();
            InitDibSurface();

            MouseDown += OnOverlayMouseDown;
            MouseMove += OnOverlayMouseMove;
            MouseUp += OnOverlayMouseUp;

            _renderTimer = new Timer { Interval = 15 }; // ~66 FPS
            _renderTimer.Tick += OnTick;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_LAYERED |
                              NativeMethods.WS_EX_TOPMOST |
                              NativeMethods.WS_EX_TOOLWINDOW |
                              NativeMethods.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        public void StartSimulation()
        {
            InstallWinEventHooks();
            FullEnumWindowsScan();
            SpawnInitialPlowOnly();
            if (RememberSnowmen)
            {
                RestoreSavedSnowmen();
                RestoreSavedTrees();
            }
            if (_trees.Count == 0)
                SpawnChristmasTree(true);
            SpawnPenguin(true);
            EnsureWinterBirdsCount();
            if (RememberWallpaper && WallpaperTheme > 0)
                WallpaperManager.ApplyTheme(WallpaperTheme, CustomWallpaperPath);
            ApplyClickThroughStyle();
            _lastTickTime = _clock.Elapsed.TotalSeconds;
            _renderTimer.Start();
        }

        private void InstallWinEventHooks()
        {
            if (_winEventProc != null) return;
            _winEventProc = new NativeMethods.WinEventDelegate(OnWinEventCallback);
            uint flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;

            // 1. Ön plana pencere gelmesi (Spotify/Discord açılması veya tıklanması), küçültme ve taşıma olayları
            _hookSystem = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
                IntPtr.Zero, _winEventProc, 0, 0, flags);

            // 2. Pencere açılma, kapanma, gizlenme, cloak ve konum/boyut değişimi olayları
            _hookObject = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_CREATE,
                NativeMethods.EVENT_OBJECT_UNCLOAKED,
                IntPtr.Zero, _winEventProc, 0, 0, flags);
        }

        private void OnWinEventCallback(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (idObject != NativeMethods.OBJID_WINDOW || hwnd == IntPtr.Zero)
                return;
            if (hwnd == Handle || hwnd == ControlPanelHwnd ||
                (WardrobeHwnd != IntPtr.Zero && hwnd == WardrobeHwnd) ||
                (TreeDecoratorHwnd != IntPtr.Zero && hwnd == TreeDecoratorHwnd))
                return;

            if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND ||
                eventType == NativeMethods.EVENT_SYSTEM_MINIMIZESTART ||
                eventType == NativeMethods.EVENT_SYSTEM_MINIMIZEEND ||
                eventType == NativeMethods.EVENT_SYSTEM_MOVESIZEEND ||
                eventType == NativeMethods.EVENT_OBJECT_SHOW ||
                eventType == NativeMethods.EVENT_OBJECT_HIDE ||
                eventType == NativeMethods.EVENT_OBJECT_DESTROY ||
                eventType == NativeMethods.EVENT_OBJECT_CLOAKED ||
                eventType == NativeMethods.EVENT_OBJECT_UNCLOAKED)
            {
                // Yeni bir pencere açıldığında, kapandığında veya öne geldiğinde Z-sırasını anında güncelle!
                _needsImmediateFullScan = true;
            }
            else if (eventType == NativeMethods.EVENT_OBJECT_LOCATIONCHANGE)
            {
                // Bilinen bir pencere hareket ediyorsa konumunu anında güncelle; bilinmeyen görünür pencereyse tarama başlat
                if (!_surfaces.ContainsKey(hwnd))
                {
                    if (NativeMethods.IsWindowVisible(hwnd) && !NativeMethods.IsIconic(hwnd))
                        _needsImmediateFullScan = true;
                }
            }
        }

        private void ComputeMonitorBounds(bool multiMonitor)
        {
            MultiMonitorEnabled = multiMonitor;
            Screen[] all = Screen.AllScreens;
            if (!multiMonitor || all == null || all.Length <= 1)
            {
                Screen prim = Screen.PrimaryScreen;
                _activeMonitorCount = 1;
                _monitorBounds = new Rectangle[] { prim.Bounds };
                _monitorWorkAreas = new Rectangle[] { prim.WorkingArea };
                _originX = prim.Bounds.Left;
                _originY = prim.Bounds.Top;
                _screenW = prim.Bounds.Width;
                _screenH = prim.Bounds.Height;
                return;
            }

            // En fazla 3 ekranı birleştir (Önce ana ekran, ardından diğer ekranlar)
            List<Screen> selected = new List<Screen>(3);
            selected.Add(Screen.PrimaryScreen);
            for (int i = 0; i < all.Length && selected.Count < 3; i++)
            {
                if (!all[i].Primary)
                    selected.Add(all[i]);
            }

            _activeMonitorCount = selected.Count;
            _monitorBounds = new Rectangle[_activeMonitorCount];
            _monitorWorkAreas = new Rectangle[_activeMonitorCount];

            int minX = int.MaxValue, minY = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue;
            for (int i = 0; i < _activeMonitorCount; i++)
            {
                Rectangle b = selected[i].Bounds;
                _monitorBounds[i] = b;
                _monitorWorkAreas[i] = selected[i].WorkingArea;
                if (b.Left < minX) minX = b.Left;
                if (b.Top < minY) minY = b.Top;
                if (b.Right > maxX) maxX = b.Right;
                if (b.Bottom > maxY) maxY = b.Bottom;
            }

            _originX = minX;
            _originY = minY;
            _screenW = Math.Max(640, maxX - minX);
            _screenH = Math.Max(480, maxY - minY);
        }

        public void SetMultiMonitorMode(bool enable)
        {
            if (MultiMonitorEnabled == enable && _surfaceBitmap != null)
                return;

            ComputeMonitorBounds(enable);
            Bounds = new Rectangle(_originX, _originY, _screenW, _screenH);
            ReallocateDibSurface();

            _surfaces.Clear();
            _orderedSurfaces.Clear();
            FullEnumWindowsScan();

            for (int i = 0; i < MaxSnowflakes; i++)
                ResetFlake(ref _flakes[i], true);

            // Çoklu ekran açıldığında diğer ekranların görev çubuğuna da küreyici/etkileşim alanı hazırla
            if (_plows.Count == 0)
                SpawnInitialPlowOnly();
        }

        private void ReallocateDibSurface()
        {
            if (_surfaceGraphics != null) { _surfaceGraphics.Dispose(); _surfaceGraphics = null; }
            if (_surfaceBitmap != null) { _surfaceBitmap.Dispose(); _surfaceBitmap = null; }
            if (_memDc != IntPtr.Zero)
            {
                if (_oldBitmap != IntPtr.Zero)
                {
                    NativeMethods.SelectObject(_memDc, _oldBitmap);
                    _oldBitmap = IntPtr.Zero;
                }
                if (_dibBitmap != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(_dibBitmap);
                    _dibBitmap = IntPtr.Zero;
                }
                NativeMethods.DeleteDC(_memDc);
                _memDc = IntPtr.Zero;
            }
            if (_screenDc != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
                _screenDc = IntPtr.Zero;
            }
            InitDibSurface();
        }

        private void InitDibSurface()
        {
            _screenDc = NativeMethods.GetDC(IntPtr.Zero);
            _memDc = NativeMethods.CreateCompatibleDC(_screenDc);

            NativeMethods.BITMAPINFO bmi = new NativeMethods.BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(NativeMethods.BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = _screenW;
            bmi.bmiHeader.biHeight = -_screenH;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;

            _dibBitmap = NativeMethods.CreateDIBSection(_memDc, ref bmi, 0, out _dibBits, IntPtr.Zero, 0);
            _oldBitmap = NativeMethods.SelectObject(_memDc, _dibBitmap);

            _surfaceBitmap = new Bitmap(_screenW, _screenH, _screenW * 4, PixelFormat.Format32bppPArgb, _dibBits);
            _surfaceGraphics = Graphics.FromImage(_surfaceBitmap);
            _surfaceGraphics.SmoothingMode = _performanceMode ? SmoothingMode.HighSpeed : SmoothingMode.AntiAlias;
            _surfaceGraphics.CompositingQuality = CompositingQuality.HighSpeed;
            _surfaceGraphics.InterpolationMode = InterpolationMode.Bilinear;
            _surfaceGraphics.PixelOffsetMode = PixelOffsetMode.Half;
        }

        private void InitParticles()
        {
            for (int i = 0; i < MaxSnowflakes; i++)
                ResetFlake(ref _flakes[i], true);
        }

        private void ResetFlake(ref Snowflake f, bool randomY)
        {
            f.X = (float)(_rng.NextDouble() * _screenW);
            f.Y = randomY ? (float)(_rng.NextDouble() * _screenH) : -16f - (float)(_rng.NextDouble() * 55.0);

            double r = _rng.NextDouble();
            if (r < 0.42)
            {
                // Arka plan flu kar tanesi
                f.Layer = 0;
                f.Size = 2.0f + (float)_rng.NextDouble() * 1.8f;
                f.SpeedMult = 0.48f + (float)_rng.NextDouble() * 0.30f;
                f.BaseAlpha = (byte)(90 + _rng.Next(65));
            }
            else if (r < 0.86)
            {
                // Orta plan parlak kar tanesi
                f.Layer = 1;
                f.Size = 3.2f + (float)_rng.NextDouble() * 2.2f;
                f.SpeedMult = 0.75f + (float)_rng.NextDouble() * 0.38f;
                f.BaseAlpha = (byte)(165 + _rng.Next(75));
            }
            else
            {
                // Ön plan kristal yıldız kar tanesi
                f.Layer = 2;
                f.Size = 5.2f + (float)_rng.NextDouble() * 2.8f;
                f.SpeedMult = 1.05f + (float)_rng.NextDouble() * 0.42f;
                f.BaseAlpha = (byte)(220 + _rng.Next(36));
            }

            f.Phase = (float)(_rng.NextDouble() * Math.PI * 2.0);
            f.Angle = (float)(_rng.NextDouble() * 360.0);
            f.SpinSpeed = (float)(_rng.NextDouble() * 3.0 - 1.5);
            f.Vx = 0f;
            f.Vy = 0f;
        }

        public void ToggleInteractiveMode()
        {
            InteractiveMode = !InteractiveMode;
            ApplyClickThroughStyle();
            if (OnInteractiveModeChanged != null)
                OnInteractiveModeChanged();
        }

        private void ApplyClickThroughStyle()
        {
            if (Handle == IntPtr.Zero) return;
            int exStyle = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
            exStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW;
            if (InteractiveMode)
            {
                exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
                Cursor = Cursors.Hand;
            }
            else
            {
                exStyle |= NativeMethods.WS_EX_TRANSPARENT;
                Cursor = Cursors.Default;
            }
            NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, exStyle);
        }

        private void SpawnInitialPlowOnly()
        {
            // Kardan adamlar kullanıcı tarafından 3 kartopunun üst üste konulmasıyla inşa edilir.
            // Başlangıçta görev çubuğuna sadece sarı kar küreme aracı ve örnek 1 kartopu bırakılır.
            WindowSurface taskbar = null;
            for (int i = 0; i < _orderedSurfaces.Count; i++)
            {
                if (_orderedSurfaces[i].IsTaskbar)
                {
                    taskbar = _orderedSurfaces[i];
                    break;
                }
            }
            if (taskbar == null && _orderedSurfaces.Count > 0)
                taskbar = _orderedSurfaces[_orderedSurfaces.Count - 1];

            if (taskbar != null)
            {
                _plows.Add(new SnowPlowEntity(taskbar.Left + 120f, taskbar.Top, taskbar.Hwnd));
            }
        }

        public void SpawnSingleSnowballOnActiveWindow()
        {
            WindowSurface target = null;
            for (int i = 0; i < _orderedSurfaces.Count; i++)
            {
                WindowSurface cand = _orderedSurfaces[i];
                if (!cand.IsTaskbar && !cand.IsMaximized && !IsOccludedByFrontWindow(cand.Left + cand.Width * 0.5f, cand.Top, cand.ZOrder))
                {
                    target = cand;
                    break;
                }
            }
            if (target == null && _orderedSurfaces.Count > 0)
                target = _orderedSurfaces[0];

            if (target != null)
            {
                float x = target.Left + 50f + (float)_rng.NextDouble() * Math.Max(30f, target.Width - 100f);
                float r = 10f + (float)_rng.NextDouble() * 4f;
                _snowballs.Add(new SnowballEntity(x, target.Top - 25f, r, IntPtr.Zero));
            }
        }

        public void RemoveLastSnowPlow()
        {
            if (_plows.Count == 0) return;
            int idx = _plows.Count - 1;
            SnowPlowEntity p = _plows[idx];
            if (p.PlowedSnowMass > 0.4f)
                ShedPlowMound(p, 1f);
            BurstSnowSparkles(p.X, p.Y - 10f, 16);
            if (_draggedPlow == p) _draggedPlow = null;
            _plows.RemoveAt(idx);
        }

        public void RemoveLastCampfire()
        {
            if (_campfires.Count == 0) return;
            int idx = _campfires.Count - 1;
            CampfireEntity cf = _campfires[idx];
            BurstSnowSparkles(cf.X, cf.Y - 10f, 16);
            if (_draggedCampfire == cf) _draggedCampfire = null;
            _campfires.RemoveAt(idx);
        }

        public void SpawnSnowPlow()
        {
            if (_plows.Count >= 4)
                _plows.RemoveAt(0);

            WindowSurface target = null;
            for (int i = 0; i < _orderedSurfaces.Count; i++)
            {
                WindowSurface cand = _orderedSurfaces[i];
                if (!cand.IsTaskbar && !cand.IsMaximized && !IsOccludedByFrontWindow(cand.Left + cand.Width * 0.5f, cand.Top, cand.ZOrder))
                {
                    target = cand;
                    break;
                }
            }
            if (target == null && _orderedSurfaces.Count > 0)
                target = _orderedSurfaces[_orderedSurfaces.Count - 1];

            if (target != null)
                _plows.Add(new SnowPlowEntity(target.Left + 60f, target.Top, target.Hwnd));
        }

        public void ClearAllSnow()
        {
            foreach (WindowSurface surf in _surfaces.Values)
            {
                SpillEntireSurfaceToPhysicalChunks(surf, 0f, -2.5f);
                if (surf.Icicles != null)
                    Array.Clear(surf.Icicles, 0, surf.Icicles.Length);
            }
            _snowballs.Clear();
            _snowmen.Clear();
            _trees.Clear();
            _campfires.Clear();
            _gifts.Clear();
            _penguins.Clear();
            for (int c = 0; c < 4; c++) _cornerFrost[c] = 0f;
            for (int i = 0; i < _confetti.Length; i++) _confetti[i].Active = false;
            CloseWardrobeIfOpen();
            CloseTreeDecoratorIfOpen();
            for (int i = 0; i < _plows.Count; i++)
                ShedPlowMound(_plows[i], 1f);
        }

        public void SpawnPenguin(bool onTaskbarOnly)
        {
            if (_penguins.Count >= 6)
                _penguins.RemoveAt(0);

            WindowSurface target = null;
            if (!onTaskbarOnly)
            {
                for (int i = 0; i < _orderedSurfaces.Count; i++)
                {
                    WindowSurface cand = _orderedSurfaces[i];
                    if (!cand.IsTaskbar && !cand.IsMaximized && !IsOccludedByFrontWindow(cand.Left + cand.Width * 0.5f, cand.Top, cand.ZOrder))
                    {
                        target = cand;
                        break;
                    }
                }
            }
            if (target == null)
            {
                for (int i = 0; i < _orderedSurfaces.Count; i++)
                {
                    if (_orderedSurfaces[i].IsTaskbar)
                    {
                        target = _orderedSurfaces[i];
                        break;
                    }
                }
            }
            if (target == null && _orderedSurfaces.Count > 0)
                target = _orderedSurfaces[_orderedSurfaces.Count - 1];

            if (target != null)
            {
                float x = target.Left + 80f + (float)_rng.NextDouble() * Math.Max(40f, target.Width - 160f);
                PenguinEntity p = new PenguinEntity(x, target.Top, target.Hwnd, _rng);
                _penguins.Add(p);
                if (!onTaskbarOnly)
                {
                    BurstSnowSparkles(x, target.Top - 16f, 16);
                    if (SoundEnabled) _audioSynth.PlayPenguinChirp();
                }
            }
        }

        public void RemoveLastPenguin()
        {
            if (_penguins.Count == 0) return;
            int idx = _penguins.Count - 1;
            PenguinEntity p = _penguins[idx];
            BurstSnowSparkles(p.X, p.Y - 12f, 16);
            if (_draggedPenguin == p) _draggedPenguin = null;
            _penguins.RemoveAt(idx);
        }

        public void SpawnChristmasTree(bool onTaskbarOnly)
        {
            if (_trees.Count >= 6)
                _trees.RemoveAt(0);

            WindowSurface target = null;
            if (!onTaskbarOnly)
            {
                for (int i = 0; i < _orderedSurfaces.Count; i++)
                {
                    WindowSurface cand = _orderedSurfaces[i];
                    if (!cand.IsTaskbar && !cand.IsMaximized && !IsOccludedByFrontWindow(cand.Left + cand.Width * 0.5f, cand.Top, cand.ZOrder))
                    {
                        target = cand;
                        break;
                    }
                }
            }
            if (target == null)
            {
                for (int i = 0; i < _orderedSurfaces.Count; i++)
                {
                    if (_orderedSurfaces[i].IsTaskbar) { target = _orderedSurfaces[i]; break; }
                }
            }
            if (target == null && _orderedSurfaces.Count > 0)
                target = _orderedSurfaces[_orderedSurfaces.Count - 1];

            if (target != null)
            {
                float x = onTaskbarOnly
                    ? Math.Max(target.Left + 85f, target.Right - 140f)
                    : (target.Left + 75f + (float)_rng.NextDouble() * Math.Max(40f, target.Width - 150f));
                ChristmasTreeEntity tree = new ChristmasTreeEntity(x, target.Top, target.Hwnd, _rng);
                _trees.Add(tree);
                if (!onTaskbarOnly)
                {
                    BurstSnowSparkles(x, target.Top - 45f, 22);
                    if (SoundEnabled) _audioSynth.PlayGiftOpen();
                    SaveTreesNow();
                }
            }
        }

        public void RemoveLastChristmasTree()
        {
            if (_trees.Count == 0) return;
            DeleteChristmasTree(_trees[_trees.Count - 1]);
        }

        public void DeleteChristmasTree(ChristmasTreeEntity tree)
        {
            if (tree == null) return;
            int idx = _trees.IndexOf(tree);
            if (idx >= 0)
            {
                BurstSnowSparkles(tree.X, tree.Y - 40f * tree.Scale, 28);
                if (_draggedTree == tree) _draggedTree = null;
                for (int b = 0; b < _birds.Count; b++)
                {
                    if (_birds[b].PerchedTree == tree)
                    {
                        _birds[b].PerchedTree = null;
                        _birds[b].State = 1;
                    }
                }
                _trees.RemoveAt(idx);
                SaveTreesNow();
            }
            if (_treeDecoratorForm != null && !_treeDecoratorForm.IsDisposed && _treeDecoratorForm.TargetTree == tree)
                CloseTreeDecoratorIfOpen();
        }

        public void OpenTreeDecorator(ChristmasTreeEntity tree)
        {
            if (tree == null) return;
            if (_treeDecoratorForm == null || _treeDecoratorForm.IsDisposed)
            {
                _treeDecoratorForm = new TreeDecoratorForm(this, tree);
                _treeDecoratorForm.FormClosed += (s, e) =>
                {
                    TreeDecoratorHwnd = IntPtr.Zero;
                    _treeDecoratorForm = null;
                    SaveTreesNow();
                };
                _treeDecoratorForm.Show();
                TreeDecoratorHwnd = _treeDecoratorForm.Handle;
            }
            else
            {
                _treeDecoratorForm.SetTargetTree(tree);
                if (!_treeDecoratorForm.Visible) _treeDecoratorForm.Show();
                _treeDecoratorForm.Activate();
                TreeDecoratorHwnd = _treeDecoratorForm.Handle;
            }

            int screenTx = _originX + (int)tree.X;
            int screenTy = _originY + (int)(tree.Y - tree.TotalHeight * 0.5f);
            Screen scr = Screen.FromPoint(new Point(screenTx, screenTy));
            Rectangle wa = scr.WorkingArea;

            int wx = screenTx + (int)tree.HalfWidth + 24;
            if (wx + _treeDecoratorForm.Width > wa.Right - 12)
                wx = screenTx - _treeDecoratorForm.Width - (int)tree.HalfWidth - 24;
            wx = Math.Max(wa.Left + 12, Math.Min(wa.Right - _treeDecoratorForm.Width - 12, wx));

            int wy = screenTy - _treeDecoratorForm.Height / 2;
            wy = Math.Max(wa.Top + 12, Math.Min(wa.Bottom - _treeDecoratorForm.Height - 12, wy));

            _treeDecoratorForm.Location = new Point(wx, wy);
            _treeDecoratorForm.BringToFront();
        }

        private void CloseTreeDecoratorIfOpen()
        {
            if (_treeDecoratorForm != null && !_treeDecoratorForm.IsDisposed)
                _treeDecoratorForm.Close();
            TreeDecoratorHwnd = IntPtr.Zero;
            _treeDecoratorForm = null;
        }

        public void SaveSnowmenNow()
        {
            if (RememberSnowmen)
            {
                SnowmenStore.Save(_snowmen, Math.Max(1, _screenW));
                TreesStore.Save(_trees, Math.Max(1, _screenW));
            }
        }

        public void SaveTreesNow()
        {
            if (RememberSnowmen)
                TreesStore.Save(_trees, Math.Max(1, _screenW));
        }

        public void RestoreSavedSnowmen()
        {
            List<SnowmanEntity> loaded = SnowmenStore.Load(Math.Max(1, _screenW), _rng);
            if (loaded == null || loaded.Count == 0) return;
            WindowSurface tb = null;
            for (int i = 0; i < _orderedSurfaces.Count; i++)
            {
                if (_orderedSurfaces[i].IsTaskbar) { tb = _orderedSurfaces[i]; break; }
            }
            if (tb == null && _orderedSurfaces.Count > 0)
                tb = _orderedSurfaces[_orderedSurfaces.Count - 1];
            if (tb == null) return;

            for (int i = 0; i < loaded.Count; i++)
            {
                SnowmanEntity sm = loaded[i];
                sm.X = Math.Max(tb.Left + 36f, Math.Min(tb.Right - 36f, sm.X));
                sm.Y = tb.Top;
                sm.SurfaceHwnd = tb.Hwnd;
                _snowmen.Add(sm);
            }
        }

        public void RestoreSavedTrees()
        {
            List<ChristmasTreeEntity> loaded = TreesStore.Load(Math.Max(1, _screenW), _rng);
            if (loaded == null || loaded.Count == 0) return;
            WindowSurface tb = null;
            for (int i = 0; i < _orderedSurfaces.Count; i++)
            {
                if (_orderedSurfaces[i].IsTaskbar) { tb = _orderedSurfaces[i]; break; }
            }
            if (tb == null && _orderedSurfaces.Count > 0)
                tb = _orderedSurfaces[_orderedSurfaces.Count - 1];
            if (tb == null) return;

            for (int i = 0; i < loaded.Count; i++)
            {
                ChristmasTreeEntity tr = loaded[i];
                tr.X = Math.Max(tb.Left + 52f, Math.Min(tb.Right - 52f, tr.X));
                tr.Y = tb.Top;
                tr.SurfaceHwnd = tb.Hwnd;
                _trees.Add(tr);
            }
        }

        public void EnsureWinterBirdsCount()
        {
            if (!BirdsEnabled)
            {
                _birds.Clear();
                return;
            }
            int targetCount = 3;
            while (_birds.Count < targetCount)
            {
                float bx = (_rng.Next(2) == 0) ? -24f : (_screenW + 24f);
                float by = 80f + (float)_rng.NextDouble() * Math.Max(120f, _screenH * 0.45f);
                int sp = (_birds.Count % 2 == 0) ? 0 : 1; // Kızılgerdan & Kar Baykuşu karışık
                WinterBirdEntity bird = new WinterBirdEntity(bx, by, sp, _rng);
                AssignNewBirdPerch(bird);
                _birds.Add(bird);
            }
        }

        public void TriggerMidnightCelebration()
        {
            _celebrationYear = (DateTime.Now.Month == 1 && DateTime.Now.Day <= 2)
                ? DateTime.Now.Year
                : (DateTime.Now.Year + 1);
            _celebrationBannerTicks = 60 * 7; // 7 saniyelik parlak kutlama pankartı
            _fireworkShowRemaining = Math.Max(_fireworkShowRemaining, 14);
            _fireworkShowTimer = 1;
            _auroraSurge = 1.0f;

            // Ekranın üstünden rengarenk konfeti yağmuru başlat
            SpawnConfettiBurst(_screenW * 0.25f, _screenH * 0.22f, 55);
            SpawnConfettiBurst(_screenW * 0.50f, _screenH * 0.18f, 70);
            SpawnConfettiBurst(_screenW * 0.75f, _screenH * 0.22f, 55);

            // Kardan adamlar & penguenler neşeyle zıplasın, ağaçlar sallansın
            for (int i = 0; i < _snowmen.Count; i++)
            {
                _snowmen[i].Vy = -6.2f;
                _snowmen[i].SurfaceHwnd = IntPtr.Zero;
                _snowmen[i].TiltVel += (i % 2 == 0 ? 10f : -10f);
            }
            for (int i = 0; i < _trees.Count; i++)
                _trees[i].WobbleVel += (i % 2 == 0 ? 4.5f : -4.5f);
            for (int i = 0; i < _penguins.Count; i++)
            {
                _penguins[i].Vy = -5.8f;
                _penguins[i].HopTimer = 28;
            }
            if (SoundEnabled) _audioSynth.PlayJingleBells();
        }

        public void ApplyWallpaperTheme(int themeIndex, string customPath, bool showToast)
        {
            WallpaperTheme = Math.Max(0, Math.Min(6, themeIndex));
            if (themeIndex == 6 && !string.IsNullOrEmpty(customPath))
                CustomWallpaperPath = customPath;

            if (WallpaperTheme == 0)
            {
                WallpaperManager.RestoreOriginal();
                if (showToast)
                    ShowToast(IsEnglish ? "↩️ Restored original desktop wallpaper" : "↩️ Orijinal masaüstü arka planına dönüldü");
                return;
            }

            if (WallpaperManager.ApplyTheme(WallpaperTheme, CustomWallpaperPath))
            {
                if (showToast)
                    ShowToast(IsEnglish ? "🖼️ Winter wallpaper applied (reverts on exit)" : "🖼️ Kış arka planı uygulandı (kapanınca eskiye döner)");
            }
            else if (showToast)
            {
                ShowToast(IsEnglish ? "⚠️ Could not apply wallpaper" : "⚠️ Arka plan uygulanamadı");
            }
        }

        public void RestoreOriginalWallpaper(bool showToast)
        {
            WallpaperTheme = 0;
            WallpaperManager.RestoreOriginal();
            if (showToast)
                ShowToast(IsEnglish ? "↩️ Restored original desktop wallpaper" : "↩️ Orijinal masaüstü arka planına dönüldü");
        }

        public void CaptureWinterPostcard()
        {
            try
            {
                Rectangle bounds = Screen.PrimaryScreen.Bounds;
                using (Bitmap shot = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(shot))
                    {
                        bool blitted = false;
                        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
                        if (screenDc != IntPtr.Zero)
                        {
                            IntPtr destDc = IntPtr.Zero;
                            try
                            {
                                destDc = g.GetHdc();
                                blitted = NativeMethods.BitBlt(
                                    destDc, 0, 0, bounds.Width, bounds.Height,
                                    screenDc, bounds.Left, bounds.Top,
                                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
                            }
                            finally
                            {
                                if (destDc != IntPtr.Zero) g.ReleaseHdc(destDc);
                                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
                            }
                        }

                        if (!blitted)
                        {
                            // Yedek yol: Geçerli tek enum değeriyle CopyFromScreen (InvalidEnumArgumentException atmaz)
                            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                        }

                        g.SmoothingMode = SmoothingMode.AntiAlias;

                        // Zarif ince buzlu kış kartpostalı çerçevesi & köşe damgası
                        using (Pen outerRim = new Pen(Color.FromArgb(145, 224, 242, 254), 10f))
                            g.DrawRectangle(outerRim, 5, 5, bounds.Width - 10, bounds.Height - 10);
                        using (Pen innerRim = new Pen(Color.FromArgb(180, 56, 189, 248), 2f))
                            g.DrawRectangle(innerRim, 14, 14, bounds.Width - 28, bounds.Height - 28);

                        string stamp = "❄️ Snowy for Windows  •  " + DateTime.Now.ToString("dd.MM.yyyy");
                        using (Font f = new Font("Segoe UI Semibold", 11f, FontStyle.Bold))
                        {
                            SizeF sz = g.MeasureString(stamp, f);
                            float pw = sz.Width + 22f;
                            float ph = sz.Height + 12f;
                            float px = bounds.Width - pw - 26f;
                            float py = bounds.Height - ph - 26f;
                            RectangleF badge = new RectangleF(px, py, pw, ph);
                            using (GraphicsPath bp = GraphicsExt.CreateRoundedRect(badge, 10f))
                            using (SolidBrush bg = new SolidBrush(Color.FromArgb(195, 11, 17, 30)))
                            using (Pen rim = new Pen(Color.FromArgb(180, 56, 189, 248), 1.3f))
                            using (SolidBrush txt = new SolidBrush(Color.FromArgb(245, 224, 242, 254)))
                            {
                                g.FillPath(bg, bp);
                                g.DrawPath(rim, bp);
                                g.DrawString(stamp, f, txt, px + 11f, py + 6f);
                            }
                        }
                    }

                    // Graphics kapandıktan sonra kaydet (Masaüstü -> Resimler -> AppData güvenli yedek zinciri)
                    string fileName = "SnowyPostcard_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                    string[] candidateDirs = new string[]
                    {
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                        System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "SnowyWindows\\Postcards")
                    };
                    string[] dirLabelsTr = new string[] { "Masaüstüne", "Resimler klasörüne", "AppData\\SnowyWindows\\Postcards içine" };
                    string[] dirLabelsEn = new string[] { "Desktop", "Pictures", "AppData\\SnowyWindows\\Postcards" };

                    int savedIdx = -1;
                    for (int d = 0; d < candidateDirs.Length; d++)
                    {
                        if (string.IsNullOrEmpty(candidateDirs[d])) continue;
                        try
                        {
                            System.IO.Directory.CreateDirectory(candidateDirs[d]);
                            string fullPath = System.IO.Path.Combine(candidateDirs[d], fileName);
                            shot.Save(fullPath, ImageFormat.Png);
                            savedIdx = d;
                            break;
                        }
                        catch { }
                    }

                    if (savedIdx >= 0)
                    {
                        if (SoundEnabled) _audioSynth.PlayGiftOpen();
                        ShowToast(IsEnglish
                            ? ("📸 Winter Postcard saved to " + dirLabelsEn[savedIdx] + "!")
                            : ("📸 Kış Kartpostalı " + dirLabelsTr[savedIdx] + " kaydedildi!"));
                    }
                    else
                    {
                        ShowToast(IsEnglish ? "⚠️ Could not write postcard file." : "⚠️ Kartpostal dosyası yazılamadı.");
                    }
                }
            }
            catch (Exception ex)
            {
                ShowToast(IsEnglish
                    ? ("⚠️ Postcard error: " + ex.Message)
                    : ("⚠️ Kartpostal hatası: " + ex.Message));
            }
        }

        public void ShowToast(string message)
        {
            _toastText = message ?? "";
            _toastTicks = 175;
        }

        private void SpawnConfettiBurst(float x, float y, int count)
        {
            Color[] palette = new Color[]
            {
                Color.FromArgb(255, 250, 204, 21),
                Color.FromArgb(255, 56, 189, 248),
                Color.FromArgb(255, 244, 114, 182),
                Color.FromArgb(255, 52, 211, 153),
                Color.FromArgb(255, 248, 113, 113),
                Color.FromArgb(255, 168, 85, 247)
            };
            int spawned = 0;
            for (int i = 0; i < _confetti.Length && spawned < count; i++)
            {
                if (_confetti[i].Active) continue;
                double a = _rng.NextDouble() * Math.PI * 2.0;
                float spd = 2.0f + (float)_rng.NextDouble() * 6.5f;
                int ttl = 140 + _rng.Next(90);
                _confetti[i].Active = true;
                _confetti[i].X = x + (float)(_rng.NextDouble() * 24.0 - 12.0);
                _confetti[i].Y = y + (float)(_rng.NextDouble() * 24.0 - 12.0);
                _confetti[i].Vx = (float)Math.Cos(a) * spd;
                _confetti[i].Vy = (float)Math.Sin(a) * spd - 3.0f;
                _confetti[i].Angle = (float)(_rng.NextDouble() * 360.0);
                _confetti[i].Spin = (float)(_rng.NextDouble() * 14.0 - 7.0);
                _confetti[i].Size = 4.5f + (float)_rng.NextDouble() * 4.5f;
                _confetti[i].Ttl = ttl;
                _confetti[i].MaxTtl = ttl;
                _confetti[i].Color = palette[_rng.Next(palette.Length)];
                spawned++;
            }
        }

        private void SpawnMagicSparkle(float x, float y, bool isRing)
        {
            for (int i = 0; i < _magicSparkles.Length; i++)
            {
                if (_magicSparkles[i].Active) continue;
                int ttl = isRing ? 22 : (26 + _rng.Next(20));
                _magicSparkles[i].Active = true;
                _magicSparkles[i].X = x;
                _magicSparkles[i].Y = y;
                _magicSparkles[i].Vx = isRing ? 0f : (float)(_rng.NextDouble() * 1.4 - 0.7);
                _magicSparkles[i].Vy = isRing ? 0f : (0.25f + (float)_rng.NextDouble() * 0.9f);
                _magicSparkles[i].Size = isRing ? 4f : (2.2f + (float)_rng.NextDouble() * 2.8f);
                _magicSparkles[i].Ttl = ttl;
                _magicSparkles[i].MaxTtl = ttl;
                _magicSparkles[i].IsRing = isRing;
                int c = _rng.Next(3);
                _magicSparkles[i].Color = c == 0
                    ? Color.FromArgb(255, 186, 230, 253)
                    : (c == 1 ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 253, 224, 71));
                return;
            }
        }

        public void ShutdownExtras()
        {
            WallpaperManager.RestoreOriginal();
            if (RememberSnowmen)
            {
                SnowmenStore.Save(_snowmen, Math.Max(1, _screenW));
                TreesStore.Save(_trees, Math.Max(1, _screenW));
            }
            CloseTreeDecoratorIfOpen();
            _audioSynth.Dispose();
            _audioMeter.Dispose();
        }

        public void SpawnDressedSnowmanOnActiveWindow(bool vipRandom)
        {
            WindowSurface target = null;
            for (int i = 0; i < _orderedSurfaces.Count; i++)
            {
                WindowSurface cand = _orderedSurfaces[i];
                if (!cand.IsTaskbar && !cand.IsMaximized && !IsOccludedByFrontWindow(cand.Left + cand.Width * 0.5f, cand.Top, cand.ZOrder))
                {
                    target = cand;
                    break;
                }
            }
            if (target == null && _orderedSurfaces.Count > 0)
                target = _orderedSurfaces[_orderedSurfaces.Count - 1];

            if (target != null)
            {
                float x = target.Left + 70f + (float)_rng.NextDouble() * Math.Max(30f, target.Width - 140f);
                SnowmanEntity sm = new SnowmanEntity(x, target.Top, 17f, 13f, 9.5f, target.Hwnd, _rng);
                if (vipRandom)
                    sm.RandomizeOutfit(_rng);
                sm.TiltVel = 5.5f;
                _snowmen.Add(sm);
                BurstSnowSparkles(x, target.Top - 36f, 28);
                SpawnPopupText(
                    x, target.Top - 78f,
                    IsEnglish ? "⛄ Right-click (F8) to dress!" : "⛄ Giydirmek için sağ tık (F8)!",
                    Color.FromArgb(255, 125, 211, 252));
            }
        }

        public void SummonSantaSoon()
        {
            if (!_santaActive && SantaEnabled)
            {
                _santaCooldownTicks = Math.Min(_santaCooldownTicks, 60 * 3); // 3 saniye içinde gelir!
            }
        }

        public void TriggerSurpriseShow()
        {
            // 1. Kuzey Işıkları parlar
            _auroraSurge = 1.0f;

            // 2. Koreografili 7'li Kış Havai Fişek Gösterisi başlat
            _fireworkShowRemaining = 7;
            _fireworkShowTimer = 1;

            // 3. Eğer Noel Baba henüz uçmuyorsa ve açıksa hemen yola çıkar!
            if (SantaEnabled && !_santaActive)
                StartSantaSleighPass();

            // 4. Sahnede kardan adamlar varsa sevinçle zıplar!
            for (int i = 0; i < _snowmen.Count; i++)
            {
                SnowmanEntity sm = _snowmen[i];
                sm.Vy = -5.5f - (float)_rng.NextDouble() * 2.5f;
                sm.SurfaceHwnd = IntPtr.Zero;
                sm.TiltVel += ((i % 2 == 0) ? 10f : -10f);
            }

            SpawnPopupText(
                _screenW * 0.5f, _screenH * 0.24f,
                IsEnglish ? "✨ WINTER MAGIC SHOW! ✨" : "✨ KIŞ SÜRPRİZİ! ✨",
                Color.FromArgb(255, 253, 224, 71));
        }

        public void LaunchFireworkAt(float targetX, float targetY)
        {
            Color[][] palettes = new Color[][]
            {
                new Color[] { Color.FromArgb(255, 56, 189, 248), Color.FromArgb(255, 224, 242, 254) },  // Buz Mavisi
                new Color[] { Color.FromArgb(255, 250, 204, 21), Color.FromArgb(255, 251, 146, 60) },   // Altın & Kehribar
                new Color[] { Color.FromArgb(255, 244, 114, 182), Color.FromArgb(255, 192, 132, 252) }, // Pembe & Mor
                new Color[] { Color.FromArgb(255, 52, 211, 153), Color.FromArgb(255, 167, 243, 208) },  // Zümrüt Aurora
                new Color[] { Color.FromArgb(255, 248, 113, 113), Color.FromArgb(255, 253, 224, 71) }   // Noel Kırmızısı & Altın
            };
            Color[] pal = palettes[_rng.Next(palettes.Length)];

            for (int i = 0; i < _rockets.Length; i++)
            {
                if (!_rockets[i].Active)
                {
                    float startX = targetX + (float)(_rng.NextDouble() * 90.0 - 45.0);
                    float startY = _screenH - 20f;
                    float distY = Math.Max(120f, startY - targetY);
                    float frames = Math.Max(22f, Math.Min(46f, distY / 13.5f));
                    _rockets[i].X = startX;
                    _rockets[i].Y = startY;
                    _rockets[i].Vx = (targetX - startX) / frames;
                    _rockets[i].Vy = -(distY / frames);
                    _rockets[i].TargetY = Math.Max(60f, Math.Min(_screenH * 0.65f, targetY));
                    _rockets[i].PrimaryColor = pal[0];
                    _rockets[i].SecondaryColor = pal[1];
                    _rockets[i].Pattern = _rng.Next(3);
                    _rockets[i].Active = true;
                    return;
                }
            }
        }

        private void ExplodeFirework(float x, float y, Color c1, Color c2, int pattern)
        {
            int count = (pattern == 1) ? 46 : 38;
            for (int k = 0; k < count; k++)
            {
                double ang = (Math.PI * 2.0 * k) / count + (_rng.NextDouble() * 0.16 - 0.08);
                float spd;
                Color col;
                if (pattern == 1)
                {
                    bool inner = (k % 2 == 0);
                    spd = inner ? (2.6f + (float)_rng.NextDouble() * 1.1f) : (5.0f + (float)_rng.NextDouble() * 1.6f);
                    col = inner ? c2 : c1;
                }
                else if (pattern == 2)
                {
                    spd = 2.2f + (float)_rng.NextDouble() * 4.4f;
                    col = (k % 3 == 0) ? Color.FromArgb(255, 254, 240, 138) : c1;
                }
                else
                {
                    spd = 2.4f + (float)_rng.NextDouble() * 4.5f;
                    col = (k % 2 == 0) ? c1 : c2;
                }

                SpawnFireworkSpark(
                    x, y,
                    (float)Math.Cos(ang) * spd,
                    (float)Math.Sin(ang) * spd,
                    (pattern == 2) ? 0.962f : 0.945f,
                    (pattern == 2) ? 0.11f : 0.075f,
                    2.4f + (float)_rng.NextDouble() * 2.2f,
                    col,
                    42 + _rng.Next(26),
                    (k % 3 == 0));
            }
            _auroraSurge = Math.Min(1.0f, _auroraSurge + 0.25f);
            if (SoundEnabled) _audioSynth.PlayFireworkPop();
        }

        private void SpawnFireworkSpark(
            float x, float y, float vx, float vy,
            float drag, float grav, float size, Color color, int ttl, bool twinkle)
        {
            for (int i = 0; i < _sparks.Length; i++)
            {
                if (!_sparks[i].Active)
                {
                    _sparks[i].X = x;
                    _sparks[i].Y = y;
                    _sparks[i].PrevX = x;
                    _sparks[i].PrevY = y;
                    _sparks[i].Vx = vx;
                    _sparks[i].Vy = vy;
                    _sparks[i].Drag = drag;
                    _sparks[i].Gravity = grav;
                    _sparks[i].Size = size;
                    _sparks[i].Color = color;
                    _sparks[i].Ttl = ttl;
                    _sparks[i].MaxTtl = ttl;
                    _sparks[i].Twinkle = twinkle;
                    _sparks[i].Active = true;
                    return;
                }
            }
        }

        private void OpenGiftBox(GiftBoxEntity gift)
        {
            float gx = gift.X;
            float gy = gift.Y - gift.Size * 0.5f;
            _gifts.Remove(gift);

            ExplodeFirework(gx, gy - 12f, gift.BoxColor, gift.RibbonColor, 1);
            BurstSnowSparkles(gx, gy, 24);
            if (SoundEnabled) _audioSynth.PlayGiftOpen();

            int roll = _rng.Next(3);
            if (roll == 0 || _snowmen.Count == 0)
            {
                // Sürpriz 1: Şık giyinmiş VIP Kardan Adam fırlar!
                SnowmanEntity vip = new SnowmanEntity(gx, gift.Y, 16.5f, 12.5f, 9.2f, gift.SurfaceHwnd, _rng);
                vip.ApplyPreset(_rng.Next(4));
                vip.Vy = -4.2f;
                vip.TiltVel = 9f;
                _snowmen.Add(vip);
                SpawnPopupText(gx, gy - 26f, IsEnglish ? "🎁 VIP SNOWMAN!" : "🎁 SÜRPRİZ KARDAN ADAM!", Color.FromArgb(255, 253, 224, 71));
            }
            else if (roll == 1)
            {
                // Sürpriz 2: Mini Havai Fişek Kutlaması!
                _fireworkShowRemaining = Math.Max(_fireworkShowRemaining, 4);
                _fireworkShowTimer = 1;
                _auroraSurge = 0.85f;
                SpawnPopupText(gx, gy - 26f, IsEnglish ? "🎆 HAPPY WINTER!" : "🎆 MUTLU KIŞLAR!", Color.FromArgb(255, 56, 189, 248));
            }
            else
            {
                // Sürpriz 3: Kardan adamlar dans eder & altın yıldız yağmuru!
                for (int i = 0; i < _snowmen.Count; i++)
                {
                    _snowmen[i].Vy = -6.0f;
                    _snowmen[i].SurfaceHwnd = IntPtr.Zero;
                    _snowmen[i].TiltVel += 12f;
                }
                for (int k = 0; k < 22; k++)
                {
                    SpawnSantaSparkle(
                        gx + (float)(_rng.NextDouble() * 40.0 - 20.0),
                        gy + (float)(_rng.NextDouble() * 20.0 - 10.0),
                        (float)(_rng.NextDouble() * 5.0 - 2.5),
                        -2.0f - (float)_rng.NextDouble() * 3.5f);
                }
                SpawnPopupText(gx, gy - 26f, IsEnglish ? "✨ GOLDEN MAGIC!" : "✨ ALTIN SİHİR!", Color.FromArgb(255, 250, 204, 21));
            }
        }

        public void OpenSnowmanWardrobe(SnowmanEntity sm)
        {
            if (sm == null) return;
            if (_wardrobeForm == null || _wardrobeForm.IsDisposed)
            {
                _wardrobeForm = new SnowmanWardrobeForm(this, sm);
                _wardrobeForm.FormClosed += (s, e) =>
                {
                    WardrobeHwnd = IntPtr.Zero;
                    _wardrobeForm = null;
                    SaveSnowmenNow();
                };
                _wardrobeForm.Show();
                WardrobeHwnd = _wardrobeForm.Handle;
            }
            else
            {
                _wardrobeForm.SetTargetSnowman(sm);
                if (!_wardrobeForm.Visible) _wardrobeForm.Show();
                _wardrobeForm.Activate();
                WardrobeHwnd = _wardrobeForm.Handle;
            }

            // Gardırop penceresini tıklanan kardan adamın hemen yanına zarifçe yerleştir
            int screenSmX = _originX + (int)sm.X;
            int screenSmY = _originY + (int)(sm.Y - sm.R1 * 2f);
            Screen scr = Screen.FromPoint(new Point(screenSmX, screenSmY));
            Rectangle wa = scr.WorkingArea;

            int wx = screenSmX + (int)(sm.R1 * 2.2f) + 20;
            if (wx + _wardrobeForm.Width > wa.Right - 12)
                wx = screenSmX - _wardrobeForm.Width - (int)(sm.R1 * 2.2f) - 20;
            wx = Math.Max(wa.Left + 12, Math.Min(wa.Right - _wardrobeForm.Width - 12, wx));

            int wy = screenSmY - _wardrobeForm.Height / 2;
            wy = Math.Max(wa.Top + 12, Math.Min(wa.Bottom - _wardrobeForm.Height - 12, wy));

            _wardrobeForm.Location = new Point(wx, wy);
            _wardrobeForm.BringToFront();
        }

        public void DeleteSnowman(SnowmanEntity sm)
        {
            if (sm == null) return;
            int idx = _snowmen.IndexOf(sm);
            if (idx >= 0)
            {
                BurstSnowSparkles(sm.X, sm.Y - 20f, 30);
                _snowmen.RemoveAt(idx);
            }
            if (_wardrobeForm != null && !_wardrobeForm.IsDisposed && _wardrobeForm.TargetSnowman == sm)
                CloseWardrobeIfOpen();
        }

        private void CloseWardrobeIfOpen()
        {
            if (_wardrobeForm != null && !_wardrobeForm.IsDisposed)
            {
                _wardrobeForm.Close();
            }
            WardrobeHwnd = IntPtr.Zero;
            _wardrobeForm = null;
        }

        public void RefreshWardrobeLanguage()
        {
            if (_wardrobeForm != null && !_wardrobeForm.IsDisposed)
                _wardrobeForm.ApplyLanguage();
            if (_treeDecoratorForm != null && !_treeDecoratorForm.IsDisposed)
                _treeDecoratorForm.ApplyLanguage();
        }

        public void SpawnCampfire()
        {
            if (_campfires.Count >= 4)
                _campfires.RemoveAt(0);

            WindowSurface target = null;
            for (int i = 0; i < _orderedSurfaces.Count; i++)
            {
                WindowSurface cand = _orderedSurfaces[i];
                if (!cand.IsTaskbar && !cand.IsMaximized && !IsOccludedByFrontWindow(cand.Left + cand.Width * 0.5f, cand.Top, cand.ZOrder))
                {
                    target = cand;
                    break;
                }
            }
            if (target == null && _orderedSurfaces.Count > 0)
                target = _orderedSurfaces[_orderedSurfaces.Count - 1];

            if (target != null)
            {
                float x = target.Left + 70f + (float)_rng.NextDouble() * Math.Max(30f, target.Width - 140f);
                _campfires.Add(new CampfireEntity(x, target.Top, target.Hwnd));
                BurstSnowSparkles(x, target.Top - 10f, 12);
            }
        }

        private void SpawnPopupText(float x, float y, string text, Color color)
        {
            for (int i = 0; i < _popups.Length; i++)
            {
                if (!_popups[i].Active)
                {
                    _popups[i].X = x;
                    _popups[i].Y = y;
                    _popups[i].Vy = -1.35f;
                    _popups[i].Text = text;
                    _popups[i].Color = color;
                    _popups[i].Ttl = 48;
                    _popups[i].MaxTtl = 48;
                    _popups[i].Active = true;
                    return;
                }
            }
        }

        // ====================================================================
        // 6. 60 FPS SIFIR GECİKMELİ PENCERE TAKİBİ & EYLEMSİZLİK FİZİĞİ
        // ====================================================================
        // Pencerenin ekran koordinatlarını (global) katman içi yerel koordinatlara (0.._screenW, 0.._screenH) dönüştürür
        // ve bulunduğu monitöre göre maksimize olup olmadığını + görev çubuğu sınırını hesaplar.
        private bool TryMapWindowRectToOverlay(
            IntPtr hWnd, NativeMethods.RECT rawRect, bool isTaskbar,
            out int localLeft, out int localTop, out int localRight, out int localBottom, out bool isMaximized)
        {
            localLeft = 0; localTop = 0; localRight = 0; localBottom = 0; isMaximized = false;

            int rawW = rawRect.Right - rawRect.Left;
            int rawH = rawRect.Bottom - rawRect.Top;
            if (rawW < 140 || rawH < 28) return false;

            // Pencerenin merkezi hangi aktif monitörde?
            int cx = (rawRect.Left + rawRect.Right) / 2;
            int cy = (rawRect.Top + rawRect.Bottom) / 2;
            int monIdx = -1;
            for (int i = 0; i < _activeMonitorCount; i++)
            {
                if (_monitorBounds[i].Contains(cx, cy))
                {
                    monIdx = i;
                    break;
                }
            }
            if (monIdx < 0)
            {
                // Merkez aktif monitörlerde değilse en azından kesişiyor mu kontrol et
                Rectangle winRect = new Rectangle(rawRect.Left, rawRect.Top, rawW, rawH);
                for (int i = 0; i < _activeMonitorCount; i++)
                {
                    if (_monitorBounds[i].IntersectsWith(winRect))
                    {
                        monIdx = i;
                        break;
                    }
                }
                if (monIdx < 0) return false;
            }

            Rectangle monB = _monitorBounds[monIdx];
            Rectangle monWa = _monitorWorkAreas[monIdx];

            if (!isTaskbar)
            {
                // Bu monitörde maksimize mi? (Örn. Opera GX)
                if (rawRect.Top <= monB.Top + 10 && rawW >= monB.Width - 28 && rawH >= monWa.Height - 24)
                {
                    isMaximized = true;
                    // Maksimize pencerenin alt sınırı görev çubuğunun (WorkingArea.Bottom) üstünde tutulur;
                    // altına inmez! Böylece görev çubuğundaki kepçe (snowplow) asla "örtüldü" sanılıp düşmez!
                    if (rawRect.Bottom > monWa.Bottom - 2)
                        rawRect.Bottom = monWa.Bottom - 2;
                }
            }

            int l = rawRect.Left - _originX;
            int t = rawRect.Top - _originY;
            int r = rawRect.Right - _originX;
            int b = rawRect.Bottom - _originY;

            if (r <= 8 || l >= _screenW - 8 || b <= 8 || t >= _screenH - 4)
                return false;

            localLeft = Math.Max(0, l);
            localTop = Math.Max(0, t);
            localRight = Math.Min(_screenW, r);
            localBottom = Math.Min(_screenH, b);
            return (localRight - localLeft) >= 100;
        }

        private void FastUpdateKnownWindowPositions60Fps()
        {
            // Her karede (16 ms) mevcut pencerelerin koordinatlarını ve görünürlüğünü DWM'den anında okur.
            for (int i = _orderedSurfaces.Count - 1; i >= 0; i--)
            {
                WindowSurface surf = _orderedSurfaces[i];
                if (surf.Hwnd.ToInt64() < 0) continue; // Sentetik görev çubuğu

                // Pencere kapandıysa, küçültüldüyse veya gizlendiyse ANINDA kaldır ve karlarını aşağı dök!
                if (!NativeMethods.IsWindowVisible(surf.Hwnd) ||
                    NativeMethods.IsIconic(surf.Hwnd) ||
                    NativeMethods.IsWindowCloaked(surf.Hwnd))
                {
                    RemoveSurfaceAndSpillSnow(surf);
                    _orderedSurfaces.RemoveAt(i);
                    continue;
                }

                NativeMethods.RECT rect;
                if (!NativeMethods.TryGetExtendedFrameBounds(surf.Hwnd, out rect))
                    continue;

                int newLeft, newTop, newRight, newBottom;
                bool nowMaximized;
                if (!TryMapWindowRectToOverlay(surf.Hwnd, rect, surf.IsTaskbar, out newLeft, out newTop, out newRight, out newBottom, out nowMaximized))
                {
                    RemoveSurfaceAndSpillSnow(surf);
                    _orderedSurfaces.RemoveAt(i);
                    continue;
                }

                if (nowMaximized && !surf.IsMaximized)
                {
                    SpillEntireSurfaceToPhysicalChunks(surf, 0f, -0.8f);
                    surf.MaxH = 0f;
                    if (surf.Icicles != null)
                        Array.Clear(surf.Icicles, 0, surf.Icicles.Length);
                }
                surf.IsMaximized = nowMaximized;

                int dx = newLeft - surf.Left;
                int dy = newTop - surf.Top;

                float newVelX = surf.VelX * 0.55f + dx * 0.45f;
                float newVelY = surf.VelY * 0.55f + dy * 0.45f;
                surf.AccelX = newVelX - surf.VelX;
                surf.AccelY = newVelY - surf.VelY;
                surf.VelX = newVelX;
                surf.VelY = newVelY;

                if (dx != 0 || dy != 0 || (newRight - newLeft) != surf.Width)
                {
                    ApplyWindowMotionPhysicsToSnow(surf, dx, dy);
                    surf.StepResizeIfNeeded(newLeft, newTop, newRight, newBottom);
                    MoveAttachedEntities60Fps(surf, dx, dy);
                    BulldozeOtherWindowEntities(surf, dx, dy);
                }
            }
        }

        private void RemoveSurfaceAndSpillSnow(WindowSurface old)
        {
            _surfaces.Remove(old.Hwnd);
            SpillEntireSurfaceToPhysicalChunks(old, 0f, -0.8f);
            if (old.Icicles != null)
            {
                for (int k = 0; k < old.Icicles.Length; k++)
                {
                    if (old.Icicles[k] > 5f)
                        SpawnFallingIcicle(old.Left + 18f + k * 28f, old.Top + 2f, old.Icicles[k]);
                    old.Icicles[k] = 0f;
                }
            }
            for (int j = 0; j < _snowballs.Count; j++)
                if (_snowballs[j].SurfaceHwnd == old.Hwnd) _snowballs[j].SurfaceHwnd = IntPtr.Zero;
            for (int j = 0; j < _snowmen.Count; j++)
                if (_snowmen[j].SurfaceHwnd == old.Hwnd) _snowmen[j].SurfaceHwnd = IntPtr.Zero;
            for (int j = 0; j < _plows.Count; j++)
                if (_plows[j].SurfaceHwnd == old.Hwnd) { _plows[j].SurfaceHwnd = IntPtr.Zero; _plows[j].IsAirborne = true; }
        }

        private void ApplyWindowMotionPhysicsToSnow(WindowSurface surf, int dx, int dy)
        {
            int n = surf.Heights.Length;

            // 0. Pencere ani sıçradıysa veya çok hızlı çekildiyse eski konumundaki tüm karlar yerçekimiyle aşağı dökülür!
            if (Math.Abs(dx) > 55 || dy > 18)
            {
                SpillEntireSurfaceToPhysicalChunks(surf, dx * 0.15f, -0.5f);
                surf.MaxH = 0f;
                return;
            }

            // 1. Pencere aşağı çekilirse (dy > 2), kar eski yüksekliğinde (surf.Top) kopup yerçekimiyle yeni pencereye düşer!
            if (dy > 2)
            {
                float liftFactor = Math.Min(0.85f, (dy - 1) * 0.085f);
                for (int i = 2; i < n - 2; i++)
                {
                    float h = surf.Heights[i];
                    if (h > 1.0f)
                    {
                        float detached = h * liftFactor;
                        surf.Heights[i] -= detached;
                        if (_rng.NextDouble() < 0.65)
                        {
                            float sx = surf.Left + i * WindowSurface.BinSize;
                            float sy = surf.Top - h * 0.6f;
                            SpawnChunk(
                                sx, sy,
                                surf.VelX * 0.22f + (float)(_rng.NextDouble() * 1.6 - 0.8),
                                0.4f + (float)(_rng.NextDouble() * 1.4),
                                Math.Min(6.5f, 2.5f + detached * 0.45f),
                                detached * 0.95f,
                                105,
                                true,
                                Color.White);
                        }
                    }
                }
            }

            // 2. Pencere yatayda hızla hareket ederse kenarlardaki karlar savrulup aşağı dökülür!
            if (Math.Abs(dx) > 2)
            {
                int edgeZone = Math.Min(n / 3, 4 + Math.Abs(dx));
                for (int k = 1; k < edgeZone; k++)
                {
                    int idx = (dx > 0) ? k : (n - 1 - k);
                    if (idx >= 0 && idx < n && surf.Heights[idx] > 1.2f)
                    {
                        float spill = surf.Heights[idx] * Math.Min(0.75f, Math.Abs(dx) * 0.04f);
                        surf.Heights[idx] -= spill;
                        if (_rng.NextDouble() < 0.65)
                        {
                            float sx = surf.Left + idx * WindowSurface.BinSize;
                            SpawnChunk(
                                sx, surf.Top - 3f,
                                -dx * 0.35f + (float)(_rng.NextDouble() * 2.4 - 1.2),
                                -1.2f + (float)(_rng.NextDouble() * 1.5),
                                3.2f + (float)_rng.NextDouble() * 2.2f,
                                spill * 1.2f,
                                90,
                                true,
                                Color.White);
                        }
                    }
                }
            }
        }

        private void FullEnumWindowsScan()
        {
            List<WindowSurface> detected = new List<WindowSurface>();
            IntPtr myHwnd = Handle;
            IntPtr panelHwnd = ControlPanelHwnd;
            IntPtr wardrobeHwnd = WardrobeHwnd;
            IntPtr treeDecHwnd = TreeDecoratorHwnd;
            int zCounter = 0;

            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (hWnd == myHwnd || hWnd == panelHwnd ||
                    (wardrobeHwnd != IntPtr.Zero && hWnd == wardrobeHwnd) ||
                    (treeDecHwnd != IntPtr.Zero && hWnd == treeDecHwnd)) return true;
                if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd)) return true;

                int cloaked = 0;
                NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out cloaked, sizeof(int));
                if (cloaked != 0) return true;

                System.Text.StringBuilder clsSb = new System.Text.StringBuilder(64);
                NativeMethods.GetClassNameW(hWnd, clsSb, 64);
                string cls = clsSb.ToString();

                if (cls == "Progman" || cls == "WorkerW" || cls == "Windows.UI.Core.CoreWindow")
                    return true;

                bool isTaskbar = (cls == "Shell_TrayWnd" || cls == "SecondaryTrayWnd");
                int exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);

                if (!isTaskbar)
                {
                    if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
                    if ((exStyle & NativeMethods.WS_EX_TRANSPARENT) != 0) return true;
                    if (NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOT) != hWnd) return true;
                    if (NativeMethods.GetWindowTextLengthW(hWnd) == 0) return true;
                    if (cls == "#32768" || cls == "SysShadow" || cls == "tooltips_class32") return true;
                }

                NativeMethods.RECT rect;
                if (!NativeMethods.TryGetExtendedFrameBounds(hWnd, out rect))
                    return true;

                int localLeft, localTop, localRight, localBottom;
                bool isMaximized;
                if (!TryMapWindowRectToOverlay(hWnd, rect, isTaskbar, out localLeft, out localTop, out localRight, out localBottom, out isMaximized))
                    return true;

                System.Text.StringBuilder titleSb = new System.Text.StringBuilder(128);
                NativeMethods.GetWindowTextW(hWnd, titleSb, 128);
                string title = titleSb.Length > 0 ? titleSb.ToString() : (isTaskbar ? "Taskbar" : "Window");

                // Görev çubuklarının Z-sırası her zaman normal pencerelerden bağımsız en altta sabitlenir
                zCounter++;
                int zOrd = isTaskbar ? (9000 + zCounter) : zCounter;
                detected.Add(new WindowSurface(
                    hWnd, title,
                    localLeft, localTop, localRight, localBottom,
                    zOrd, isTaskbar, isMaximized));
                return true;
            }, IntPtr.Zero);

            // Aktif her monitör için en altta bir görev çubuğu / ekran tabanı yüzeyi bulunduğundan emin ol
            for (int m = 0; m < _activeMonitorCount; m++)
            {
                Rectangle mb = _monitorBounds[m];
                Rectangle mw = _monitorWorkAreas[m];
                int mLeft = mb.Left - _originX;
                int mRight = mb.Right - _originX;
                int mBottom = mb.Bottom - _originY;
                int tbTop = (mw.Bottom < mb.Bottom - 4) ? (mw.Bottom - _originY) : (mBottom - 48);

                bool monitorHasTaskbar = false;
                for (int i = 0; i < detected.Count; i++)
                {
                    if (detected[i].IsTaskbar)
                    {
                        int midX = (detected[i].Left + detected[i].Right) / 2;
                        if (midX >= mLeft && midX <= mRight)
                        {
                            monitorHasTaskbar = true;
                            break;
                        }
                    }
                }
                if (!monitorHasTaskbar)
                {
                    detected.Add(new WindowSurface(
                        new IntPtr(-990 - m), "Taskbar_" + m,
                        Math.Max(0, mLeft), Math.Max(0, tbTop),
                        Math.Min(_screenW, mRight), Math.Min(_screenH, mBottom),
                        9990 + m, true, false));
                }
            }

            HashSet<IntPtr> seen = new HashSet<IntPtr>();
            for (int i = 0; i < detected.Count; i++)
            {
                WindowSurface d = detected[i];
                seen.Add(d.Hwnd);
                WindowSurface existing;
                if (_surfaces.TryGetValue(d.Hwnd, out existing))
                {
                    existing.ZOrder = d.ZOrder;
                    existing.IsMaximized = d.IsMaximized;
                }
                else
                {
                    _surfaces[d.Hwnd] = d;
                }
            }

            List<IntPtr> toRemove = new List<IntPtr>();
            foreach (KeyValuePair<IntPtr, WindowSurface> kv in _surfaces)
            {
                if (!seen.Contains(kv.Key))
                    toRemove.Add(kv.Key);
            }
            for (int i = 0; i < toRemove.Count; i++)
            {
                IntPtr key = toRemove[i];
                WindowSurface old = _surfaces[key];
                RemoveSurfaceAndSpillSnow(old);
            }

            _orderedSurfaces.Clear();
            _orderedSurfaces.AddRange(_surfaces.Values);
            _orderedSurfaces.Sort((a, b) => a.ZOrder.CompareTo(b.ZOrder));

            // Z-sırası değiştiği anda (örn. Opera GX öne geldiğinde) arkada kalan pencerelerin karlarını anında dök!
            for (int i = 0; i < _orderedSurfaces.Count; i++)
            {
                WindowSurface s = _orderedSurfaces[i];
                if (!s.IsMaximized && s.ZOrder > 1)
                    HandleSurfaceSnowLoss(s, 0f);
            }

            _lastFullEnumScan = DateTime.UtcNow;
        }

        private void BulldozeOtherWindowEntities(WindowSurface movingSurf, int dx, int dy)
        {
            if (movingSurf.IsTaskbar) return;
            if (Math.Abs(dx) + Math.Abs(dy) < 2) return;

            // Hareket ettirilen pencere başka bir penceredeki kardan adama veya kartopuna çarparsa fiziksel darbe uygular.
            for (int i = 0; i < _snowmen.Count; i++)
            {
                SnowmanEntity sm = _snowmen[i];
                if (sm.SurfaceHwnd == movingSurf.Hwnd || sm.Dragging) continue;
                float topY = sm.Y - (sm.R1 + sm.R2 + sm.R3) * 1.7f;
                if (sm.X + sm.R1 >= movingSurf.Left && sm.X - sm.R1 <= movingSurf.Right &&
                    sm.Y >= movingSurf.Top && topY <= movingSurf.Bottom)
                {
                    sm.SurfaceHwnd = IntPtr.Zero;
                    sm.Vx = dx * 0.75f + (sm.X < (movingSurf.Left + movingSurf.Right) * 0.5f ? -3.5f : 3.5f);
                    sm.Vy = Math.Min(-2.5f, dy * 0.5f - 2.0f);
                    sm.TiltVel = sm.Vx * 2.4f;
                    sm.SurpriseTimer = 45;
                    BurstSnowSparkles(sm.X, sm.Y - sm.R1, 14);
                }
            }

            for (int i = 0; i < _snowballs.Count; i++)
            {
                SnowballEntity sb = _snowballs[i];
                if (sb.SurfaceHwnd == movingSurf.Hwnd || sb.Dragging) continue;
                if (sb.X + sb.BaseRadius >= movingSurf.Left && sb.X - sb.BaseRadius <= movingSurf.Right &&
                    sb.Y >= movingSurf.Top && (sb.Y - sb.TotalHeight) <= movingSurf.Bottom)
                {
                    sb.SurfaceHwnd = IntPtr.Zero;
                    sb.Vx = dx * 0.8f + (sb.X < (movingSurf.Left + movingSurf.Right) * 0.5f ? -3.0f : 3.0f);
                    sb.Vy = Math.Min(-2.0f, dy * 0.5f - 1.5f);
                }
            }

            // Sarsıntıda penceredeki uzun buz sarkıtları kırılıp düşer
            if (IciclesEnabled && movingSurf.Icicles != null && (Math.Abs(dx) > 9 || Math.Abs(dy) > 7))
            {
                for (int i = 0; i < movingSurf.Icicles.Length; i++)
                {
                    float len = movingSurf.Icicles[i];
                    if (len > 9f && _rng.NextDouble() < 0.35)
                    {
                        SpawnFallingIcicle(movingSurf.Left + 18f + i * 28f, movingSurf.Top + 2f, len);
                        movingSurf.Icicles[i] = 0f;
                    }
                }
            }
        }

        private void SpawnFallingIcicle(float x, float y, float len)
        {
            for (int i = 0; i < _fallingIcicles.Length; i++)
            {
                if (!_fallingIcicles[i].Active)
                {
                    _fallingIcicles[i].X = x;
                    _fallingIcicles[i].Y = y;
                    _fallingIcicles[i].Vy = 0.8f;
                    _fallingIcicles[i].Length = len;
                    _fallingIcicles[i].Width = Math.Min(7f, 2.8f + len * 0.14f);
                    _fallingIcicles[i].Active = true;
                    return;
                }
            }
        }

        private void SpawnFlyingHat(float x, float y, float vx, float vy, float scale, int style, Color color)
        {
            for (int i = 0; i < _flyingHats.Length; i++)
            {
                if (!_flyingHats[i].Active)
                {
                    _flyingHats[i].X = x;
                    _flyingHats[i].Y = y;
                    _flyingHats[i].Vx = vx;
                    _flyingHats[i].Vy = vy;
                    _flyingHats[i].Angle = 0f;
                    _flyingHats[i].AngularVel = (vx >= 0f ? 9f : -9f) + (float)(_rng.NextDouble() * 6.0 - 3.0);
                    _flyingHats[i].Scale = scale;
                    _flyingHats[i].HatStyle = style;
                    _flyingHats[i].HatColor = color;
                    _flyingHats[i].Ttl = 110;
                    _flyingHats[i].Active = true;
                    return;
                }
            }
        }

        private void MoveAttachedEntities60Fps(WindowSurface surf, int dx, int dy)
        {
            for (int i = 0; i < _snowballs.Count; i++)
            {
                SnowballEntity sb = _snowballs[i];
                if (sb.SurfaceHwnd == surf.Hwnd && !sb.Dragging)
                {
                    sb.X += dx;
                    sb.Y = surf.Top - surf.GetSnowHeightAtX(sb.X) * 0.35f;
                    // Pencere çok hızlı sallanırsa kartopu da ivmelenip yuvarlanır
                    sb.Vx += -dx * 0.08f;
                }
            }

            for (int i = 0; i < _snowmen.Count; i++)
            {
                SnowmanEntity sm = _snowmen[i];
                if (sm.SurfaceHwnd == surf.Hwnd && !sm.Dragging)
                {
                    sm.X = Math.Max(surf.Left + 18f, Math.Min(surf.Right - 18f, sm.X + dx));
                    sm.Y = surf.Top - surf.GetSnowHeightAtX(sm.X) * 0.35f;
                    sm.TiltVel += -dx * 0.65f;
                }
            }

            for (int i = 0; i < _plows.Count; i++)
            {
                SnowPlowEntity p = _plows[i];
                if (p.SurfaceHwnd == surf.Hwnd && !p.Dragging && !p.IsAirborne)
                {
                    p.X = Math.Max(surf.Left + 28f, Math.Min(surf.Right - 28f, p.X + dx));
                    p.Y = surf.Top;
                }
            }

            for (int i = 0; i < _trees.Count; i++)
            {
                ChristmasTreeEntity tr = _trees[i];
                if (tr.SurfaceHwnd == surf.Hwnd && !tr.Dragging)
                {
                    tr.X = Math.Max(surf.Left + 24f, Math.Min(surf.Right - 24f, tr.X + dx));
                    tr.Y = surf.Top;
                    tr.WobbleVel -= Math.Max(-4.5f, Math.Min(4.5f, dx * 0.35f));
                }
            }

            for (int i = 0; i < _birds.Count; i++)
            {
                WinterBirdEntity b = _birds[i];
                if (b.State == 0 && b.PerchedTree == null && b.SurfaceHwnd == surf.Hwnd)
                {
                    b.X = Math.Max(surf.Left + 14f, Math.Min(surf.Right - 14f, b.X + dx));
                    b.Y = surf.Top;
                }
            }
        }

        private void SpillEntireSurfaceToPhysicalChunks(WindowSurface surf, float boostVx, float boostVy)
        {
            for (int i = 0; i < surf.Heights.Length; i += 2)
            {
                float h = surf.Heights[i];
                if (h > 1.2f)
                {
                    SpawnChunk(
                        surf.Left + i * WindowSurface.BinSize,
                        surf.Top - (float)(_rng.NextDouble() * h),
                        boostVx + (float)(_rng.NextDouble() * 4.5 - 2.25),
                        boostVy + (float)(_rng.NextDouble() * 2.5 - 1.5),
                        2.8f + (float)_rng.NextDouble() * 2.8f,
                        h * 0.45f,
                        95,
                        true,
                        Color.White);
                }
                surf.Heights[i] = 0f;
                if (i + 1 < surf.Heights.Length) surf.Heights[i + 1] = 0f;
            }
        }

        private void SpawnChunk(float x, float y, float vx, float vy, float size, float mass, int ttl, bool canAccumulate, Color tint)
        {
            for (int i = 0; i < MaxChunks; i++)
            {
                if (!_chunks[i].Active)
                {
                    _chunks[i].X = x;
                    _chunks[i].Y = y;
                    _chunks[i].Vx = vx;
                    _chunks[i].Vy = vy;
                    _chunks[i].Size = size;
                    _chunks[i].Mass = mass;
                    _chunks[i].Ttl = ttl;
                    _chunks[i].MaxTtl = ttl;
                    _chunks[i].CanAccumulate = canAccumulate;
                    _chunks[i].Tint = tint;
                    _chunks[i].Active = true;
                    return;
                }
            }
        }

        private WindowSurface FindTaskbarAtX(float x)
        {
            WindowSurface fallback = null;
            for (int i = 0; i < _orderedSurfaces.Count; i++)
            {
                WindowSurface s = _orderedSurfaces[i];
                if (s.IsTaskbar)
                {
                    fallback = s;
                    if (x >= s.Left && x <= s.Right)
                        return s;
                }
            }
            return fallback;
        }

        private bool IsOccludedByFrontWindow(float x, float y, int targetZ)
        {
            // Görev çubukları (z >= 9000) her zaman ekranın en alt tabanıdır; maksimize pencereler onları örtmez!
            if (targetZ >= 9000) return false;
            for (int i = 0; i < _orderedSurfaces.Count; i++)
            {
                WindowSurface s = _orderedSurfaces[i];
                if (s.ZOrder >= targetZ) break;
                if (s.IsTaskbar) continue;
                if (x >= s.Left && x <= s.Right && y >= s.Top && y <= s.Bottom)
                    return true;
            }
            return false;
        }

        private bool IsPointInsideAnyApplicationWindow(float x, float y)
        {
            for (int i = 0; i < _orderedSurfaces.Count; i++)
            {
                WindowSurface s = _orderedSurfaces[i];
                if (s.IsTaskbar) continue;
                if (x >= s.Left && x <= s.Right && y >= s.Top + 6 && y <= s.Bottom)
                    return true;
            }
            return false;
        }

        // ====================================================================
        // 7. FARE ETKİLEŞİMLERİ: FIRLATMA, YUVARLAMA VE 3'LÜ KARTOPU İSTİFLEME
        // ====================================================================
        private void RecordMousePosition(int x, int y)
        {
            _mouseHistory[_mouseHistoryIdx] = new PointF(x, y);
            _mouseHistoryIdx = (_mouseHistoryIdx + 1) % _mouseHistory.Length;
        }

        private void GetThrowVelocity(out float vx, out float vy)
        {
            PointF newest = _mouseHistory[(_mouseHistoryIdx - 1 + _mouseHistory.Length) % _mouseHistory.Length];
            PointF oldest = _mouseHistory[_mouseHistoryIdx];
            vx = Math.Max(-36f, Math.Min(36f, (newest.X - oldest.X) * 0.42f));
            vy = Math.Max(-36f, Math.Min(36f, (newest.Y - oldest.Y) * 0.42f));
        }

        private void OnOverlayMouseDown(object sender, MouseEventArgs e)
        {
            if (!InteractiveMode) return;

            for (int i = 0; i < _mouseHistory.Length; i++)
                _mouseHistory[i] = new PointF(e.X, e.Y);

            // Orta tık: İmleç noktasına sürpriz havai fişek fırlat!
            if (e.Button == MouseButtons.Middle)
            {
                LaunchFireworkAt(e.X, e.Y);
                return;
            }

            // Sağ tık: Kardan adama sağ tık -> Gardırop, Yılbaşı Ağacına sağ tık -> Süsleme Penceresi!
            if (e.Button == MouseButtons.Right)
            {
                if (CountdownEnabled && IsPointInsideCountdownWidget(e.X, e.Y))
                {
                    TriggerMidnightCelebration();
                    return;
                }
                for (int i = _snowmen.Count - 1; i >= 0; i--)
                {
                    SnowmanEntity sm = _snowmen[i];
                    if (Math.Abs(e.X - sm.X) <= sm.R1 * 1.75f && e.Y <= sm.Y + 10f && e.Y >= sm.Y - (sm.R1 + sm.R2 + sm.R3) * 2.25f)
                    {
                        OpenSnowmanWardrobe(sm);
                        return;
                    }
                }
                for (int i = _trees.Count - 1; i >= 0; i--)
                {
                    if (_trees[i].HitTest(e.X, e.Y))
                    {
                        OpenTreeDecorator(_trees[i]);
                        return;
                    }
                }
                for (int i = _gifts.Count - 1; i >= 0; i--)
                {
                    GiftBoxEntity g = _gifts[i];
                    if (Math.Abs(e.X - g.X) <= g.Size * 1.2f && Math.Abs(e.Y - (g.Y - g.Size * 0.5f)) <= g.Size * 1.4f)
                    {
                        OpenGiftBox(g);
                        return;
                    }
                }
                for (int i = _snowballs.Count - 1; i >= 0; i--)
                {
                    SnowballEntity sb = _snowballs[i];
                    if (Math.Abs(e.X - sb.X) <= sb.BaseRadius * 1.4f && Math.Abs(e.Y - (sb.Y - sb.BaseRadius)) <= sb.TotalHeight)
                    {
                        BurstSnowSparkles(sb.X, sb.Y - sb.BaseRadius, 18);
                        _snowballs.RemoveAt(i);
                        return;
                    }
                }
                for (int i = _campfires.Count - 1; i >= 0; i--)
                {
                    CampfireEntity cf = _campfires[i];
                    if (Math.Abs(e.X - cf.X) <= 26f && Math.Abs(e.Y - (cf.Y - 12f)) <= 26f)
                    {
                        BurstSnowSparkles(cf.X, cf.Y - 10f, 16);
                        _campfires.RemoveAt(i);
                        return;
                    }
                }
                for (int i = _penguins.Count - 1; i >= 0; i--)
                {
                    PenguinEntity pg = _penguins[i];
                    if (Math.Abs(e.X - pg.X) <= 24f && Math.Abs(e.Y - (pg.Y - 16f)) <= 24f)
                    {
                        BurstSnowSparkles(pg.X, pg.Y - 14f, 18);
                        _penguins.RemoveAt(i);
                        return;
                    }
                }
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            if (CursorTrailEnabled)
                SpawnMagicSparkle(e.X, e.Y, true);

            // -1.2. Öncelik: Geri Sayım Widget'ını tutup ekranda taşı
            if (CountdownEnabled && IsPointInsideCountdownWidget(e.X, e.Y))
            {
                EnsureCountdownPos();
                _draggingCountdown = true;
                _countdownDragOffset = new PointF(e.X - _countdownX, e.Y - _countdownY);
                return;
            }

            // -1.1. Öncelik: Süzülen Dilek Fenerine tıklayınca altın dilek parıltısı patlasın!
            if (LanternsEnabled)
            {
                for (int i = 0; i < _lanterns.Length; i++)
                {
                    if (!_lanterns[i].Active) continue;
                    if (Math.Abs(e.X - _lanterns[i].X) <= 18f * _lanterns[i].Scale && Math.Abs(e.Y - _lanterns[i].Y) <= 22f * _lanterns[i].Scale)
                    {
                        float lx = _lanterns[i].X;
                        float ly = _lanterns[i].Y;
                        _lanterns[i].Active = false;
                        for (int k = 0; k < 18; k++)
                            SpawnSantaSparkle(lx, ly, (float)(_rng.NextDouble() * 4.4 - 2.2), (float)(_rng.NextDouble() * 4.4 - 2.6));
                        if (SoundEnabled) _audioSynth.PlayGiftOpen();
                        SpawnPopupText(lx, ly - 16f, IsEnglish ? "🏮 WISH GRANTED!" : "🏮 DİLEK TUTULDU!", Color.FromArgb(255, 253, 224, 71));
                        return;
                    }
                }
            }

            // -1. Öncelik: Noel Baba'nın bıraktığı Hediye Paketine sol tıklayınca patlatarak aç!
            for (int i = _gifts.Count - 1; i >= 0; i--)
            {
                GiftBoxEntity g = _gifts[i];
                if (Math.Abs(e.X - g.X) <= g.Size * 1.2f && Math.Abs(e.Y - (g.Y - g.Size * 0.5f)) <= g.Size * 1.4f)
                {
                    OpenGiftBox(g);
                    return;
                }
            }

            // -0.5. Öncelik: Kutup Ekspresi trenine tıklayınca düdük çalsın & buhar püskürtsün!
            if (TrainEnabled && _trainVisible && Math.Abs(e.X - _trainX) <= 105f && Math.Abs(e.Y - (_trainY - 16f)) <= 26f)
            {
                TootPolarExpressWhistle();
                return;
            }

            // -0.2. Öncelik: Paytak Penguen tut / zıplat / fırlat!
            for (int i = _penguins.Count - 1; i >= 0; i--)
            {
                PenguinEntity pg = _penguins[i];
                if (Math.Abs(e.X - pg.X) <= 22f && Math.Abs(e.Y - (pg.Y - 16f)) <= 24f)
                {
                    pg.Dragging = true;
                    pg.SurfaceHwnd = IntPtr.Zero;
                    pg.Vx = 0f;
                    pg.Vy = 0f;
                    _draggedPenguin = pg;
                    _penguinMouseDownPos = e.Location;
                    return;
                }
            }

            // 0. Öncelik: Kamp ateşini (Campfire) tut ve taşı
            for (int i = _campfires.Count - 1; i >= 0; i--)
            {
                CampfireEntity cf = _campfires[i];
                if (Math.Abs(e.X - cf.X) <= 24f && Math.Abs(e.Y - (cf.Y - 12f)) <= 24f)
                {
                    cf.Dragging = true;
                    cf.SurfaceHwnd = IntPtr.Zero;
                    _draggedCampfire = cf;
                    return;
                }
            }

            // 1. Öncelik: Kar küreme aracını (Snowplow) tut ve fırlatmaya hazırla!
            for (int i = _plows.Count - 1; i >= 0; i--)
            {
                SnowPlowEntity p = _plows[i];
                if (Math.Abs(e.X - p.X) <= 36f && Math.Abs(e.Y - (p.Y - 14f)) <= 28f)
                {
                    p.Dragging = true;
                    p.IsAirborne = true;
                    p.SurfaceHwnd = IntPtr.Zero;
                    p.Vx = 0f;
                    p.Vy = 0f;
                    ShedPlowMound(p, 0.25f); // İlk tutuşta biraz dökülür, kalanı fırlatma hızıyla savrulur!
                    _draggedPlow = p;
                    return;
                }
            }

            // 2. Öncelik: Mevcut bir kartopunu (Snowball) tut (yuvarlamak veya başka kartopunun üstüne koymak için)
            for (int i = _snowballs.Count - 1; i >= 0; i--)
            {
                SnowballEntity sb = _snowballs[i];
                float centerY = sb.Y - sb.TotalHeight * 0.5f;
                if (Math.Abs(e.X - sb.X) <= sb.BaseRadius + 10f && Math.Abs(e.Y - centerY) <= sb.TotalHeight * 0.65f + 10f)
                {
                    sb.Dragging = true;
                    sb.Vx = 0f;
                    sb.Vy = 0f;
                    _draggedSnowball = sb;
                    _creatingNewSnowballOnDrag = false;
                    return;
                }
            }

            // 3. Öncelik: Tamamlanmış bir kardan adamı tut ve taşı/fırlat
            for (int i = _snowmen.Count - 1; i >= 0; i--)
            {
                SnowmanEntity sm = _snowmen[i];
                float totalH = (sm.R1 + sm.R2 + sm.R3) * 1.8f;
                if (Math.Abs(e.X - sm.X) <= sm.R1 + 8f && e.Y <= sm.Y + 8f && e.Y >= sm.Y - totalH - 10f)
                {
                    sm.Dragging = true;
                    sm.SurfaceHwnd = IntPtr.Zero;
                    _draggedSnowman = sm;
                    return;
                }
            }

            // 3.5. Öncelik: Yılbaşı Ağacını tut ve taşı / başka pencereye yerleştir
            for (int i = _trees.Count - 1; i >= 0; i--)
            {
                ChristmasTreeEntity tr = _trees[i];
                if (tr.HitTest(e.X, e.Y))
                {
                    tr.Dragging = true;
                    tr.SurfaceHwnd = IntPtr.Zero;
                    tr.Vx = 0f;
                    tr.Vy = 0f;
                    _draggedTree = tr;
                    return;
                }
            }

            // 4. Öncelik: Pencere üstündeki kar yığınına tıklandıysa yeni bir kartopu oluşturup yuvarlamaya başla!
            for (int i = 0; i < _orderedSurfaces.Count; i++)
            {
                WindowSurface s = _orderedSurfaces[i];
                if (s.IsMaximized) continue;
                if (e.X >= s.Left && e.X <= s.Right && Math.Abs(e.Y - s.Top) <= 42 && !IsOccludedByFrontWindow(e.X, s.Top, s.ZOrder))
                {
                    SnowballEntity newBall = new SnowballEntity(e.X, s.Top, 8.5f, s.Hwnd);
                    newBall.Dragging = true;
                    _snowballs.Add(newBall);
                    _draggedSnowball = newBall;
                    _creatingNewSnowballOnDrag = true;
                    return;
                }
            }

            // 5. Öncelik: Boşluğa sol tıklayıp sürüklediğinde kardan adamlara atmak için anında kartopu yoğur!
            SnowballEntity quickBall = new SnowballEntity(e.X, e.Y, 10.5f, IntPtr.Zero);
            quickBall.Dragging = true;
            _snowballs.Add(quickBall);
            _draggedSnowball = quickBall;
            _creatingNewSnowballOnDrag = true;
            BurstSnowSparkles(e.X, e.Y, 6);
        }

        private void OnOverlayMouseMove(object sender, MouseEventArgs e)
        {
            if (!InteractiveMode) return;
            RecordMousePosition(e.X, e.Y);

            if (_draggingCountdown)
            {
                _countdownX = Math.Max(8f, Math.Min(_screenW - 224f, e.X - _countdownDragOffset.X));
                _countdownY = Math.Max(8f, Math.Min(_screenH - 76f, e.Y - _countdownDragOffset.Y));
                return;
            }

            if (_draggedTree != null)
            {
                _draggedTree.X = e.X;
                _draggedTree.Y = e.Y + _draggedTree.TotalHeight * 0.42f;
                _draggedTree.Vx = 0f;
                _draggedTree.Vy = 0f;
                return;
            }

            if (_draggedPenguin != null)
            {
                _draggedPenguin.X = e.X;
                _draggedPenguin.Y = e.Y + 12f;
                _draggedPenguin.FlipperPhase += 0.6f;
                return;
            }

            if (_draggedCampfire != null)
            {
                _draggedCampfire.X = e.X;
                _draggedCampfire.Y = e.Y;
                _draggedCampfire.Vx = 0f;
                _draggedCampfire.Vy = 0f;
                return;
            }

            if (_draggedPlow != null)
            {
                _draggedPlow.X = e.X;
                _draggedPlow.Y = e.Y;
                float vx, vy;
                GetThrowVelocity(out vx, out vy);
                _draggedPlow.Angle = Math.Max(-45f, Math.Min(45f, vx * 1.8f));
                return;
            }

            if (_draggedSnowball != null)
            {
                SnowballEntity sb = _draggedSnowball;
                float prevX = sb.X;
                sb.X = e.X;
                sb.Y = e.Y;

                // Eğer kartopu bir pencerenin üst kenarına yakın sürükleniyorsa kardan beslenip büyür!
                for (int i = 0; i < _orderedSurfaces.Count; i++)
                {
                    WindowSurface s = _orderedSurfaces[i];
                    if (s.IsMaximized) continue;
                    if (e.X >= s.Left + 8 && e.X <= s.Right - 8 && Math.Abs(e.Y - s.Top) <= 32 && !IsOccludedByFrontWindow(e.X, s.Top, s.ZOrder))
                    {
                        sb.Y = s.Top;
                        sb.SurfaceHwnd = s.Hwnd;
                        float dx = e.X - prevX;
                        sb.RollAngle += dx / Math.Max(4f, sb.BaseRadius);

                        if (sb.StackCount == 1 && Math.Abs(dx) > 0.5f)
                        {
                            float harvested = s.HarvestOrPushSnow(e.X, 14, 1.8f, 0, 0f);
                            GrowSnowball(sb, harvested, 9f);
                        }
                        break;
                    }
                    else if (_creatingNewSnowballOnDrag && Math.Abs(e.Y - s.Top) > 45)
                    {
                        sb.SurfaceHwnd = IntPtr.Zero;
                    }
                }
                return;
            }

            if (_draggedSnowman != null)
            {
                _draggedSnowman.X = e.X;
                _draggedSnowman.Y = e.Y;
                _draggedSnowman.Vx = 0f;
                _draggedSnowman.Vy = 0f;
            }
        }

        private void OnOverlayMouseUp(object sender, MouseEventArgs e)
        {
            float throwVx, throwVy;
            GetThrowVelocity(out throwVx, out throwVy);

            if (_draggedPenguin != null)
            {
                PenguinEntity pg = _draggedPenguin;
                pg.Dragging = false;
                _draggedPenguin = null;
                float moveDist = (float)Math.Sqrt(
                    (e.X - _penguinMouseDownPos.X) * (e.X - _penguinMouseDownPos.X) +
                    (e.Y - _penguinMouseDownPos.Y) * (e.Y - _penguinMouseDownPos.Y));
                if (moveDist < 10f)
                {
                    // Kısa tıklama: Neşeyle zıplar, kanat çırpar ve öter!
                    pg.Vy = -6.5f;
                    pg.HopTimer = 24;
                    pg.State = 1; // Karnının üstünde kaymaya başlasın!
                    pg.StateTimer = 140;
                    if (SoundEnabled) _audioSynth.PlayPenguinChirp();
                    SpawnPopupText(pg.X, pg.Y - 34f, "🐧 *NOOT!*", Color.FromArgb(255, 125, 211, 252));
                }
                else
                {
                    pg.Vx = throwVx * 0.78f;
                    pg.Vy = throwVy * 0.78f;
                    if (Math.Abs(pg.Vx) > 1.2f) pg.Direction = (pg.Vx >= 0f) ? 1 : -1;
                    if (SoundEnabled) _audioSynth.PlayPenguinChirp();
                }
                return;
            }

            if (_draggedCampfire != null)
            {
                _draggedCampfire.Dragging = false;
                _draggedCampfire.Vx = throwVx * 0.75f;
                _draggedCampfire.Vy = throwVy * 0.75f;
                _draggedCampfire = null;
                return;
            }

            if (_draggedPlow != null)
            {
                _draggedPlow.Dragging = false;
                _draggedPlow.IsAirborne = true;
                _draggedPlow.Vx = throwVx;
                _draggedPlow.Vy = throwVy;
                _draggedPlow.AngularVel = throwVx * 1.45f;
                _draggedPlow = null;
                return;
            }

            if (_draggedSnowball != null)
            {
                SnowballEntity sb = _draggedSnowball;
                sb.Dragging = false;
                _draggedSnowball = null;
                _creatingNewSnowballOnDrag = false;

                double throwSpd = Math.Sqrt(throwVx * throwVx + throwVy * throwVy);

                // Yavaşça bırakıldıysa (fırlatılmadıysa) doğrudan üst üste koymayı dene (3 Kartopu -> Kardan Adam)
                if (throwSpd <= 5.2)
                {
                    if (TryStackSnowballOntoAnother(sb))
                        return;
                    if (TryRepairDecapitatedOrHitSnowmanGentle(sb))
                        return;
                }

                // Hızlı fırlatıldıysa havaya uçsun (hedefteki kardan adamı vurabilir!)
                if (throwSpd > 3.2)
                {
                    sb.SurfaceHwnd = IntPtr.Zero;
                    sb.Vx = throwVx * 0.92f;
                    sb.Vy = throwVy * 0.92f;
                }
                return;
            }

            if (_draggingCountdown)
            {
                _draggingCountdown = false;
                return;
            }

            if (_draggedTree != null)
            {
                _draggedTree.Dragging = false;
                _draggedTree.Vx = throwVx * 0.55f;
                _draggedTree.Vy = throwVy * 0.55f;
                _draggedTree.WobbleVel += Math.Max(-8f, Math.Min(8f, throwVx * 0.6f));
                _draggedTree = null;
                SaveTreesNow();
                return;
            }

            if (_draggedSnowman != null)
            {
                _draggedSnowman.Dragging = false;
                _draggedSnowman.Vx = throwVx * 0.75f;
                _draggedSnowman.Vy = throwVy * 0.75f;
                _draggedSnowman = null;
            }
        }

        private bool TryRepairDecapitatedOrHitSnowmanGentle(SnowballEntity dropped)
        {
            // Şapkası düşmüş bir kardan adamın kafasına yavaşça kartopu bırakılırsa yeni şapka takar
            for (int i = 0; i < _snowmen.Count; i++)
            {
                SnowmanEntity sm = _snowmen[i];
                float headY = sm.Y - sm.R1 * 2f - sm.R2 * 1.3f - sm.R3;
                if (!sm.HasHat && Math.Abs(dropped.X - sm.X) <= sm.R1 * 1.4f && Math.Abs(dropped.Y - headY) <= sm.R3 * 2.5f)
                {
                    sm.HasHat = true;
                    sm.TiltVel = 3.5f;
                    BurstSnowSparkles(sm.X, headY, 18);
                    _snowballs.Remove(dropped);
                    return true;
                }
            }
            return false;
        }

        // KARDAN ADAMLARA VE İSTİFLERE KARTOPU ÇARPIŞMA FİZİĞİ
        private bool CheckSnowballCollisionsWithSnowmen(SnowballEntity sb)
        {
            if (sb.Dragging) return false;
            float speed = (float)Math.Sqrt(sb.Vx * sb.Vx + sb.Vy * sb.Vy);

            // 0) Yılbaşı Ağacına çarpan kartopu ağacı neşeyle sallar ve dallardan kar döker!
            if (speed >= 3.2f && !sb.IsAiSnowball)
            {
                for (int i = _trees.Count - 1; i >= 0; i--)
                {
                    ChristmasTreeEntity tr = _trees[i];
                    if (tr.Dragging || tr.HitCooldown > 0) continue;
                    if (tr.HitTest(sb.X, sb.Y - sb.BaseRadius))
                    {
                        tr.WobbleVel += (sb.Vx >= 0f ? 6.5f : -6.5f);
                        tr.HitCooldown = 25;
                        BurstSnowSparkles(sb.X, sb.Y - sb.BaseRadius, 22);
                        if (SoundEnabled) _audioSynth.PlaySnowHit();
                        _snowballs.Remove(sb);
                        return true;
                    }
                }
            }

            // A) 2 katlı kartopu istifine hızlı bir kartopu çarptıysa: üstteki topu devirip 2 ayrı kartopuna ayır!
            if (speed >= 6.2f && sb.StackCount == 1)
            {
                for (int i = 0; i < _snowballs.Count; i++)
                {
                    SnowballEntity target = _snowballs[i];
                    if (target == sb || target.StackCount != 2) continue;
                    float topBallY = target.Y - target.BaseRadius * 2f - target.MidRadius * 0.65f;
                    if (Math.Abs(sb.X - target.X) <= target.BaseRadius + sb.BaseRadius + 4f &&
                        Math.Abs((sb.Y - sb.BaseRadius) - topBallY) <= target.MidRadius + sb.BaseRadius + 8f)
                    {
                        float knockedR = target.MidRadius;
                        target.StackCount = 1;
                        target.MidRadius = 0f;
                        target.Vx = sb.Vx * 0.35f;

                        SnowballEntity knockedBall = new SnowballEntity(target.X, topBallY, knockedR, IntPtr.Zero);
                        knockedBall.Vx = sb.Vx * 0.72f + (float)(_rng.NextDouble() * 2.0 - 1.0);
                        knockedBall.Vy = -3.8f + sb.Vy * 0.25f;
                        _snowballs.Add(knockedBall);

                        BurstSnowSparkles(target.X, topBallY, 22);
                        _snowballs.Remove(sb);
                        return true;
                    }
                }
            }

            // B) Tamamlanmış Kardan Adamlara (SnowmanEntity) çarptıysa:
            if (speed < 2.6f) return false;

            for (int i = _snowmen.Count - 1; i >= 0; i--)
            {
                SnowmanEntity sm = _snowmen[i];
                if (sm.Dragging || sm.HitCooldown > 0 || sb.IgnoreSnowman == sm) continue;

                float y1 = sm.Y - sm.R1 + 2f;
                float y2 = y1 - sm.R1 - sm.R2 * 0.64f;
                float y3 = y2 - sm.R2 - sm.R3 * 0.64f;
                float sbCenterY = sb.Y - sb.BaseRadius;

                float dx = sb.X - sm.X;
                if (Math.Abs(dx) > sm.R1 + sb.BaseRadius + 8f) continue;

                float distHead = (float)Math.Sqrt(dx * dx + (sbCenterY - y3) * (sbCenterY - y3));
                float distTorso = (float)Math.Sqrt(dx * dx + (sbCenterY - y2) * (sbCenterY - y2));
                float distBase = (float)Math.Sqrt(dx * dx + (sbCenterY - y1) * (sbCenterY - y1));

                bool hitHead = distHead <= (sm.R3 + sb.BaseRadius + 5f);
                bool hitTorso = distTorso <= (sm.R2 + sb.BaseRadius + 5f);
                bool hitBase = distBase <= (sm.R1 + sb.BaseRadius + 5f);

                if (!hitHead && !hitTorso && !hitBase) continue;

                float hitDir = (Math.Abs(sb.Vx) > 0.5f) ? Math.Sign(sb.Vx) : (dx >= 0f ? -1f : 1f);
                if (SoundEnabled) _audioSynth.PlaySnowHit();

                // Kardan adamlar arası AI kartopu savaşı: Kardan adamı yok etmeden eğlenceli tepki verir!
                if (sb.IsAiSnowball)
                {
                    sm.HitCooldown = 14;
                    sm.SurpriseTimer = 44;
                    sm.TiltVel += hitDir * 13f;
                    sm.Vy = -2.2f;
                    sm.SurfaceHwnd = IntPtr.Zero;
                    BurstSnowSparkles(sb.X, sbCenterY, 16);
                    SpawnPopupText(
                        sm.X, y3 - 14f,
                        hitHead ? (IsEnglish ? "❄️ BONK!" : "❄️ PAT!") : (IsEnglish ? "⛄ HEY!" : "⛄ HEY!"),
                        Color.FromArgb(255, 186, 230, 253));
                    _snowballs.Remove(sb);
                    return true;
                }

                if (hitHead && speed >= 3.8f)
                {
                    // KAFADAN VURUŞ (HEADSHOT):
                    // 1. Şapkası varsa havaya döne döne fırlar!
                    if (sm.HasHat)
                    {
                        SpawnFlyingHat(
                            sm.X, y3 - sm.R3,
                            sb.Vx * 0.55f + hitDir * 2.5f,
                            -5.5f - (float)_rng.NextDouble() * 2.5f,
                            sm.R2 / 8.5f, sm.HatStyle, sm.HatColor);
                        sm.HasHat = false;
                    }

                    // 2. Kafa küresi (R3) kopup bağımsız bir kartopu olarak fırlar!
                    SnowballEntity headBall = new SnowballEntity(sm.X, y3 + sm.R3, sm.R3, IntPtr.Zero);
                    headBall.Vx = sb.Vx * 0.68f + hitDir * 2.0f;
                    headBall.Vy = -4.2f + Math.Min(0f, sb.Vy * 0.3f);
                    _snowballs.Add(headBall);

                    // 3. Alt iki küre (R1 + R2) pencerede 2 katlı sallanan gövde olarak kalır!
                    SnowballEntity bodyStack = new SnowballEntity(sm.X, sm.Y, sm.R1, sm.SurfaceHwnd);
                    bodyStack.StackCount = 2;
                    bodyStack.BaseRadius = sm.R1;
                    bodyStack.MidRadius = sm.R2;
                    bodyStack.WobbleAmp = 0.65f;
                    bodyStack.Vx = sb.Vx * 0.25f;
                    _snowballs.Add(bodyStack);

                    SpawnPopupText(sm.X, y3 - 16f, "🎯 HEADSHOT!", Color.FromArgb(255, 253, 224, 71));
                    SpawnChunk(sm.X, y3, hitDir * 4.2f, -3.2f, 4.5f, 0.2f, 45, false, Color.FromArgb(255, 249, 115, 22));
                    BurstSnowSparkles(sm.X, y3, 28);

                    _snowmen.RemoveAt(i);
                    _snowballs.Remove(sb);
                    return true;
                }

                if (speed >= 11.0f)
                {
                    // ÇOK SERT GÖVDE VURUŞU: Kardan adam 3 ayrı kartopuna parçalanır!
                    if (sm.HasHat)
                    {
                        SpawnFlyingHat(
                            sm.X, y3 - sm.R3,
                            sb.Vx * 0.6f, -6.2f,
                            sm.R2 / 8.5f, sm.HatStyle, sm.HatColor);
                    }

                    SnowballEntity b1 = new SnowballEntity(sm.X - 6f, sm.Y, sm.R1, IntPtr.Zero)
                    {
                        Vx = sb.Vx * 0.42f - 2.2f,
                        Vy = -3.0f
                    };
                    SnowballEntity b2 = new SnowballEntity(sm.X + 4f, y2 + sm.R2, sm.R2, IntPtr.Zero)
                    {
                        Vx = sb.Vx * 0.62f + 2.0f,
                        Vy = -4.8f
                    };
                    SnowballEntity b3 = new SnowballEntity(sm.X, y3 + sm.R3, sm.R3, IntPtr.Zero)
                    {
                        Vx = sb.Vx * 0.78f,
                        Vy = -6.2f
                    };
                    _snowballs.Add(b1);
                    _snowballs.Add(b2);
                    _snowballs.Add(b3);

                    SpawnPopupText(sm.X, y2 - 12f, "💥 BOOM!", Color.FromArgb(255, 248, 113, 113));
                    SpawnChunk(sm.X, y3, hitDir * 4.5f, -4.0f, 4.5f, 0.2f, 45, false, Color.FromArgb(255, 249, 115, 22));
                    BurstSnowSparkles(sm.X, y2, 36);

                    _snowmen.RemoveAt(i);
                    _snowballs.Remove(sb);
                    return true;
                }
                else
                {
                    // ORTA ŞİDDETLİ VURUŞ: Kardan adam sarsılır, geriye fırlar (pencere kenarından aşağı düşebilir!)
                    sm.HitCooldown = 8;
                    sm.SurpriseTimer = 55;
                    sm.Vx += sb.Vx * 0.62f + hitDir * 1.8f;
                    sm.Vy = -3.4f + Math.Min(0f, sb.Vy * 0.35f);
                    sm.SurfaceHwnd = IntPtr.Zero;
                    sm.TiltVel += (sb.Vx >= 0f ? 14f : -14f);

                    if (sm.HasHat && speed >= 7.2f)
                    {
                        SpawnFlyingHat(
                            sm.X, y3 - sm.R3,
                            sb.Vx * 0.45f, -4.5f,
                            sm.R2 / 8.5f, sm.HatStyle, sm.HatColor);
                        sm.HasHat = false;
                    }

                    SpawnPopupText(sm.X, y3 - 10f, "❄️ OUCH!", Color.FromArgb(255, 125, 211, 252));
                    BurstSnowSparkles(sb.X, sbCenterY, 22);
                    _snowballs.Remove(sb);
                    return true;
                }
            }

            return false;
        }

        private bool TryStackSnowballOntoAnother(SnowballEntity dropped)
        {
            if (dropped.StackCount != 1) return false;

            for (int i = 0; i < _snowballs.Count; i++)
            {
                SnowballEntity target = _snowballs[i];
                if (target == dropped) continue;

                float distX = Math.Abs(dropped.X - target.X);
                float targetTopY = target.Y - target.TotalHeight;
                float distY = Math.Abs(dropped.Y - targetTopY);

                if (distX <= target.BaseRadius * 1.65f + 14f && distY <= target.BaseRadius * 2.2f + 24f)
                {
                    if (target.StackCount == 1)
                    {
                        // 2. Kartopu eklendi! (Taban + Orta Gövde)
                        float rA = Math.Max(target.BaseRadius, dropped.BaseRadius);
                        float rB = Math.Min(target.BaseRadius, dropped.BaseRadius);
                        if (rB > rA * 0.92f) rB = rA * 0.82f;

                        target.BaseRadius = rA;
                        target.MidRadius = Math.Max(8f, rB);
                        target.StackCount = 2;
                        target.WobbleAmp = 0.25f;
                        target.WobblePhase = 0f;

                        BurstSnowSparkles(target.X, target.Y - target.BaseRadius * 2f, 14);
                        _snowballs.Remove(dropped);
                        return true;
                    }
                    else if (target.StackCount == 2)
                    {
                        // 3. Kartopu eklendi! -> KARDAN ADAMA DÖNÜŞÜR!
                        float r1 = target.BaseRadius;
                        float r2 = target.MidRadius;
                        float r3 = Math.Max(6.5f, Math.Min(r2 * 0.85f, dropped.BaseRadius));

                        SnowmanEntity sm = new SnowmanEntity(
                            target.X, target.Y,
                            r1, r2, r3,
                            target.SurfaceHwnd, _rng);
                        sm.TiltVel = 4.5f;
                        _snowmen.Add(sm);

                        BurstSnowSparkles(target.X, target.Y - (r1 + r2) * 1.5f, 38);
                        _snowballs.Remove(dropped);
                        _snowballs.Remove(target);
                        return true;
                    }
                }
            }
            return false;
        }

        private void BurstSnowSparkles(float x, float y, int count)
        {
            for (int i = 0; i < count; i++)
            {
                double ang = _rng.NextDouble() * Math.PI * 2.0;
                float spd = 1.5f + (float)_rng.NextDouble() * 4.5f;
                SpawnChunk(
                    x, y,
                    (float)Math.Cos(ang) * spd,
                    (float)Math.Sin(ang) * spd - 1.8f,
                    2.8f + (float)_rng.NextDouble() * 2.6f,
                    0.5f,
                    32 + _rng.Next(22),
                    false,
                    (i % 3 == 0) ? Color.FromArgb(255, 253, 224, 71) : Color.White);
            }
        }

        // ====================================================================
        // 8. ANA FİZİK DÖNGÜSÜ (KAR, PARÇACIKLAR, KARTOPU VE SEKME FİZİĞİ)
        // ====================================================================
        private void OnTick(object sender, EventArgs e)
        {
            // Gerçek zaman farkı ölçülür; fizik her zaman sabit 1/60 sn adımlarla çalışır.
            // Böylece yavaş bilgisayarda da hızlı olanla aynı hızda, kararlı bir kar davranışı elde edilir.
            double now = _clock.Elapsed.TotalSeconds;
            double dt = now - _lastTickTime;
            _lastTickTime = now;
            if (dt > 0.1) dt = 0.1;
            if (dt < 0.0) dt = 0.0;

            PollGlobalHotkeys();

            // Tam ekran oyun/video açıkken (yaklaşık 3 kez/sn kontrol) tüm hesaplamayı durdur
            if ((_frameTick++ % 20) == 0)
                UpdateFullscreenSuspend();

            if (_fullscreenSuspended)
            {
                if (!_clearedForSuspend) ClearOverlay();
                return;
            }

            // Aktif ön plan penceresi (örn. Opera'ya tıklanması) değiştiği salisede yakala!
            IntPtr currentFg = NativeMethods.GetForegroundWindow();
            if (currentFg != _lastForegroundHwnd && currentFg != Handle && currentFg != ControlPanelHwnd)
            {
                _lastForegroundHwnd = currentFg;
                _needsImmediateFullScan = true;
            }

            // 1. SetWinEventHook veya ön plan pencere değişimi tetiklendiyse ANINDA (0 gecikmeyle) tara!
            if (_needsImmediateFullScan || (DateTime.UtcNow - _lastFullEnumScan).TotalMilliseconds > 180)
            {
                _needsImmediateFullScan = false;
                FullEnumWindowsScan();
            }

            // 2. Bilinen pencereleri her karede (60 FPS) DWM Extended Frame Bounds ile sıfır gecikmeyle takip et
            FastUpdateKnownWindowPositions60Fps();

            if (!Paused)
            {
                _accum += dt;
                int steps = 0;
                while (_accum >= PhysicsStep && steps < 3)
                {
                    UpdatePhysics();
                    _accum -= PhysicsStep;
                    steps++;
                }
                if (steps >= 3) _accum = 0.0;
            }

            RenderFrameToLayeredWindow();
        }

        private int GetActiveFlakeCount()
        {
            int cap = PerformanceMode ? (int)(MaxSnowflakes * 0.55f) : MaxSnowflakes;
            float mult = MultiMonitorEnabled ? (1f + (_activeMonitorCount - 1) * 0.65f) : 1f;
            int scaled = (int)(ActiveSnowCount * mult);
            return Math.Min(cap, Math.Max(40, scaled));
        }

        public void TogglePause()
        {
            Paused = !Paused;
            if (OnPauseChanged != null)
                OnPauseChanged();
        }

        private void UpdateFullscreenSuspend()
        {
            bool suspend = false;
            if (AutoPauseOnFullscreen)
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != Handle && fg != ControlPanelHwnd)
                {
                    System.Text.StringBuilder clsSb = new System.Text.StringBuilder(64);
                    NativeMethods.GetClassNameW(fg, clsSb, 64);
                    string cls = clsSb.ToString();
                    if (cls != "Progman" && cls != "WorkerW" && cls != "Shell_TrayWnd" && cls != "SecondaryTrayWnd")
                    {
                        NativeMethods.RECT r;
                        if (NativeMethods.GetWindowRect(fg, out r))
                            suspend = (r.Left <= 0 && r.Top <= 0 && r.Right >= _screenW && r.Bottom >= _screenH);
                    }
                }
            }

            if (suspend != _fullscreenSuspended)
            {
                _fullscreenSuspended = suspend;
                _clearedForSuspend = false;
                _accum = 0.0;
                _lastTickTime = _clock.Elapsed.TotalSeconds;
            }
        }

        private void ClearOverlay()
        {
            _surfaceGraphics.Clear(Color.Transparent);
            PresentFrame();
            _clearedForSuspend = true;
        }

        private void PresentFrame()
        {
            NativeMethods.POINT topPos = new NativeMethods.POINT(_originX, _originY);
            NativeMethods.SIZE wndSize = new NativeMethods.SIZE(_screenW, _screenH);
            NativeMethods.POINT srcPt = new NativeMethods.POINT(0, 0);
            NativeMethods.BLENDFUNCTION blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA
            };

            NativeMethods.UpdateLayeredWindow(
                Handle, _screenDc, ref topPos, ref wndSize,
                _memDc, ref srcPt, 0, ref blend, NativeMethods.ULW_ALPHA);
        }

        private void InitCachedDrawingObjects()
        {
            _lightGlowBrushes = new SolidBrush[LightPalette.Length];
            _lightMidBrushes = new SolidBrush[LightPalette.Length];
            for (int i = 0; i < LightPalette.Length; i++)
            {
                Color c = LightPalette[i];
                _lightGlowBrushes[i] = new SolidBrush(Color.FromArgb(45, c.R, c.G, c.B));
                _lightMidBrushes[i] = new SolidBrush(Color.FromArgb(120, c.R, c.G, c.B));
            }
            _lightCoreBrush = new SolidBrush(Color.FromArgb(252, 255, 255, 245));
        }

        // Kar küreme aracının bıçağındaki kar kütlesini (fraction oranında) fiziksel parçacıklar olarak savurur.
        // Araç fırlatıldığında / tutulduğunda / havadayken çağrılır: kepçede kar kalmaz.
        private void ShedPlowMound(SnowPlowEntity plow, float fraction)
        {
            float shed = plow.PlowedSnowMass * fraction;
            if (shed < 0.4f)
            {
                plow.PlowedSnowMass = 0f;
                return;
            }

            int count = (int)Math.Min(26f, 3f + shed * 0.28f);
            float perMass = Math.Max(0.6f, shed * 0.35f / count);
            for (int i = 0; i < count; i++)
            {
                SpawnChunk(
                    plow.X + plow.Direction * (20f + (float)_rng.NextDouble() * 16f),
                    plow.Y - 6f - (float)_rng.NextDouble() * 14f,
                    plow.Vx * 0.6f + plow.Direction * (float)(_rng.NextDouble() * 2.4) + (float)(_rng.NextDouble() * 3.0 - 1.5),
                    plow.Vy * 0.4f - 0.8f - (float)_rng.NextDouble() * 2.4f,
                    2.8f + (float)_rng.NextDouble() * 2.8f,
                    perMass,
                    70 + _rng.Next(30),
                    true,
                    Color.White);
            }

            plow.PlowedSnowMass -= shed;
            if (plow.PlowedSnowMass < 0.4f) plow.PlowedSnowMass = 0f;
        }

        private void SpawnImpactPuff(float x, float y, float vx, int count)
        {
            for (int i = 0; i < count; i++)
            {
                SpawnChunk(
                    x + (float)(_rng.NextDouble() * 6.0 - 3.0), y - 1f,
                    vx * 0.3f + (float)(_rng.NextDouble() * 3.2 - 1.6),
                    -0.6f - (float)_rng.NextDouble() * 1.8f,
                    1.6f + (float)_rng.NextDouble() * 1.0f,
                    0.1f,
                    14 + _rng.Next(12),
                    false,
                    Color.White);
            }
        }

        // Pencere kenarından dökülen kar, öne açılan pencere (örn. Spotify) altında kalan karların düşmesi ve spindrift
        private void HandleSurfaceSnowLoss(WindowSurface s, float windVx)
        {
            // Görev çubuğu (IsTaskbar) hiçbir zaman normal/maksimize pencereler tarafından örtülmüş sayılmaz!
            if (!s.IsTaskbar && !s.IsMaximized && s.ZOrder > 1 && (s.MaxH > 0.2f || s.Icicles != null))
            {
                int n = s.Heights.Length;
                float newMax = 0f;
                for (int i = 0; i < n; i++)
                {
                    float h = s.Heights[i];
                    if (h <= 0.01f) continue;
                    float sx = s.Left + i * WindowSurface.BinSize;
                    if (IsOccludedByFrontWindow(sx, s.Top, s.ZOrder))
                    {
                        if (h > 1.2f && (i % 3 == 0) && _rng.NextDouble() < 0.45)
                        {
                            SpawnChunk(
                                sx, s.Top - (float)(_rng.NextDouble() * h),
                                (float)(_rng.NextDouble() * 1.6 - 0.8),
                                0.8f + (float)(_rng.NextDouble() * 1.8),
                                2.6f + (float)_rng.NextDouble() * 2.2f,
                                h * 0.65f, 85, true, Color.White);
                        }
                        s.Heights[i] = 0f;
                    }
                    else if (h > newMax)
                    {
                        newMax = h;
                    }
                }
                s.MaxH = newMax;

                if (s.Icicles != null)
                {
                    for (int k = 0; k < s.Icicles.Length; k++)
                    {
                        if (s.Icicles[k] > 0.5f)
                        {
                            float ix = s.Left + 18f + k * 28f;
                            if (IsOccludedByFrontWindow(ix, s.Top, s.ZOrder))
                                s.Icicles[k] = 0f;
                        }
                    }
                }
            }
            if (s.LostLeft > 1.0f)
            {
                SpawnChunk(s.Left + 1f, s.Top - 1f,
                    -0.6f - (float)_rng.NextDouble() * 1.2f, -0.3f - (float)_rng.NextDouble() * 0.6f,
                    Math.Min(6f, 2.6f + s.LostLeft * 0.5f), s.LostLeft, 80, true, Color.White);
                s.LostLeft = 0f;
            }
            if (s.LostRight > 1.0f)
            {
                SpawnChunk(s.Right - 1f, s.Top - 1f,
                    0.6f + (float)_rng.NextDouble() * 1.2f, -0.3f - (float)_rng.NextDouble() * 0.6f,
                    Math.Min(6f, 2.6f + s.LostRight * 0.5f), s.LostRight, 80, true, Color.White);
                s.LostRight = 0f;
            }

            float aw = Math.Abs(windVx);
            if (!PerformanceMode && s.MaxH > 3f && aw > 1.6f && _rng.NextDouble() < Math.Min(0.35, aw * 0.04))
            {
                int n = s.Heights.Length;
                int idx = 3 + _rng.Next(Math.Max(1, n - 6));
                float h = s.Heights[idx];
                if (h > 2.5f)
                {
                    s.Heights[idx] = h - 0.12f;
                    SpawnChunk(
                        s.Left + idx * WindowSurface.BinSize, s.Top - h * 0.8f,
                        windVx * 0.55f + (float)(_rng.NextDouble() * 1.0 - 0.5),
                        -0.2f - (float)_rng.NextDouble() * 0.8f,
                        1.4f + (float)_rng.NextDouble() * 1.1f,
                        0.12f,
                        40 + _rng.Next(25),
                        true,
                        Color.White);
                }
            }
        }

        // Kartopu hacmi korunarak büyür (r^3 ~ hacim): büyüdükçe aynı kar daha az yarıçap kazandırır
        private static void GrowSnowball(SnowballEntity sb, float harvested, float k)
        {
            if (harvested <= 0f) return;
            double r = sb.BaseRadius;
            double v = r * r * r + harvested * k;
            sb.BaseRadius = Math.Min(24f, (float)Math.Pow(v, 1.0 / 3.0));
            sb.Radius = sb.BaseRadius;
        }


        private void PollGlobalHotkeys()
        {
            bool f8Down = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_F8) & 0x8000) != 0;
            if (f8Down && !_f8WasDown)
                ToggleInteractiveMode();
            _f8WasDown = f8Down;

            bool f9Down = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_F9) & 0x8000) != 0;
            if (f9Down && !_f9WasDown)
                ClearAllSnow();
            _f9WasDown = f9Down;

            bool f7Down = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_F7) & 0x8000) != 0;
            if (f7Down && !_f7WasDown)
                TogglePause();
            _f7WasDown = f7Down;

            bool f6Down = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_F6) & 0x8000) != 0;
            if (f6Down && !_f6WasDown)
                TriggerSurpriseShow();
            _f6WasDown = f6Down;

            bool f10Down = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_F10) & 0x8000) != 0;
            if (f10Down && !_f10WasDown)
                CaptureWinterPostcard();
            _f10WasDown = f10Down;
        }

        private void UpdatePhysics()
        {
            _frameCount++;
            _time += (float)PhysicsStep;
            if (_toastTicks > 0) _toastTicks--;

            // Doğal rüzgar: farklı hızlarda üst üste binen sinüslerle rüzgar hamleleri (gust)
            float gust = 0f;
            if (GustAmount > 0.001f)
            {
                gust = (float)(Math.Sin(_time * 0.37) * 0.50 +
                               Math.Sin(_time * 0.93 + 1.7) * 0.30 +
                               Math.Sin(_time * 2.30 + 0.6) * 0.20) * GustAmount;
            }
            float maxNx = BlizzardMode ? 1.65f : 1.3f;
            float effNx = Math.Max(-maxNx, Math.Min(maxNx, WindNx + gust));
            float windVx = effNx * (BlizzardMode ? 8.8f : 6.8f);
            float windVy = WindNy * (BlizzardMode ? 3.8f : 2.1f) + (BlizzardMode ? 1.35f : 0f);
            int activeCount = GetActiveFlakeCount();

            // 1. Yağan Kar Taneleri (hafif taneler rüzgara çabuk uyar, türbülans + salınım)
            for (int i = 0; i < activeCount; i++)
            {
                Snowflake f = _flakes[i];
                f.Phase += 0.042f * (0.8f + f.SpeedMult * 0.3f);
                f.Angle += f.SpinSpeed;
                float flutter = (float)Math.Sin(f.Phase) * 0.62f;
                float turb = (float)Math.Sin(f.Y * 0.011f + _time * 0.9f + f.Phase * 0.25f)
                             * 0.34f * (0.6f + Math.Abs(windVx) * 0.10f);

                float follow = (f.Layer == 0) ? 0.20f : ((f.Layer == 1) ? 0.15f : 0.11f);
                f.Vx = f.Vx * (1f - follow) + (windVx * f.SpeedMult + flutter + turb) * follow;
                f.Vy = Math.Max(0.75f, 1.48f * f.SpeedMult + windVy + (float)Math.Sin(f.Phase * 0.5f) * 0.12f);

                float nx = f.X + f.Vx;
                if (nx < 0) nx += _screenW;
                else if (nx >= _screenW) nx -= _screenW;
                float ny = f.Y + f.Vy;
                if (ny < -8f) ny = -8f;

                bool landed = false;
                for (int sIdx = 0; sIdx < _orderedSurfaces.Count; sIdx++)
                {
                    WindowSurface s = _orderedSurfaces[sIdx];
                    if (s.IsMaximized) continue;
                    if (nx >= s.Left + 4 && nx <= s.Right - 4)
                    {
                        float pileH = s.GetSnowHeightAtX(nx);
                        float surfaceY = s.Top - pileH * 0.72f;
                        if (f.Y <= surfaceY && ny + 2.5f >= surfaceY)
                        {
                            if (!IsOccludedByFrontWindow(nx, s.Top, s.ZOrder))
                            {
                                if (s.AddSnowAtX(nx, f.Size * 0.45f, MaxPileHeight))
                                {
                                    landed = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (landed || ny >= _screenH)
                {
                    ResetFlake(ref _flakes[i], false);
                }
                else
                {
                    f.X = nx;
                    f.Y = ny;
                    _flakes[i] = f;
                }
            }

            // 2. Fiziksel Kar Parçaları (hava sürtünmesi + rüzgar + toz püskürmesi)
            for (int i = 0; i < MaxChunks; i++)
            {
                if (!_chunks[i].Active) continue;
                PhysicalSnowChunk ch = _chunks[i];

                float prevY = ch.Y;

                // Küçük parçacıklar rüzgara daha çabuk uyar, iri kümeler ağırdır
                float drag = 0.012f + 0.05f / (1f + ch.Size);
                ch.Vx += (windVx - ch.Vx) * drag * 0.35f;
                ch.Vy *= (1f - drag * 0.35f);
                ch.X += ch.Vx;
                ch.Y += ch.Vy;
                ch.Vy += 0.28f; // Yerçekimi
                float terminal = 6.5f + ch.Size * 0.5f;
                if (ch.Vy > terminal) ch.Vy = terminal;
                ch.Ttl--;

                // Ekran yan kenarlarından sekme
                if (ch.X < 4f) { ch.X = 4f; ch.Vx = -ch.Vx * 0.6f; }
                else if (ch.X > _screenW - 4f) { ch.X = _screenW - 4f; ch.Vx = -ch.Vx * 0.6f; }

                bool absorbed = false;
                if (ch.CanAccumulate && ch.Vy > 0f)
                {
                    for (int sIdx = 0; sIdx < _orderedSurfaces.Count; sIdx++)
                    {
                        WindowSurface s = _orderedSurfaces[sIdx];
                        if (s.IsMaximized) continue;
                        if (ch.X >= s.Left + 6 && ch.X <= s.Right - 6)
                        {
                            float surfY = s.Top - s.GetSnowHeightAtX(ch.X) * 0.65f;
                            if (prevY <= surfY + 3f && ch.Y >= surfY)
                            {
                                if (!IsOccludedByFrontWindow(ch.X, s.Top, s.ZOrder))
                                {
                                    float add = Math.Max(0.1f, ch.Mass);
                                    if (add > 2.5f)
                                        s.AddSnowSpread(ch.X, add, MaxPileHeight, Math.Min(6, 1 + (int)(add / 3f)));
                                    else
                                        s.AddSnowAtX(ch.X, add, MaxPileHeight);

                                    // Sert çarpışmada toz kar püskürür
                                    if (ch.Vy > 4.5f && ch.Size > 3.4f && !PerformanceMode)
                                        SpawnImpactPuff(ch.X, surfY, ch.Vx, 2 + _rng.Next(2));

                                    absorbed = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (absorbed || ch.Ttl <= 0 || ch.Y >= _screenH)
                {
                    _chunks[i].Active = false;
                }
                else
                {
                    _chunks[i] = ch;
                }
            }

            // 3. Pencere Üstü Sürekli Kar Fiziği (akı, rüzgar sürüklenmesi, çığ, erime + buz sarkıtları)
            for (int i = 0; i < _orderedSurfaces.Count; i++)
            {
                WindowSurface s = _orderedSurfaces[i];
                s.StepContinuumPhysics(MaxPileHeight, effNx, MeltRate);
                HandleSurfaceSnowLoss(s, windVx);

                // Buz sarkıtlarının (Icicles) kar kalınlığına göre yavaşça uzaması
                if (IciclesEnabled && !s.IsTaskbar && s.Icicles != null && s.MaxH > 3.5f && (_frameCount % 6 == 0))
                {
                    for (int k = 0; k < s.Icicles.Length; k++)
                    {
                        float sx = s.Left + 18f + k * 28f;
                        float localH = s.GetSnowHeightAtX(sx);
                        if (localH > 4f)
                        {
                            float targetMax = Math.Min(32f, 6f + localH * 0.85f + ((k % 3) * 4f));
                            if (s.Icicles[k] < targetMax)
                                s.Icicles[k] += 0.045f + MeltRate * 120f;
                        }
                        else if (MeltRate > 0f && s.Icicles[k] > 0f)
                        {
                            s.Icicles[k] = Math.Max(0f, s.Icicles[k] - 0.03f);
                        }
                    }
                }
            }

            // 3b. Kırılıp Düşen Buz Sarkıtları ve Uçan Şapkalar
            for (int i = 0; i < _fallingIcicles.Length; i++)
            {
                if (!_fallingIcicles[i].Active) continue;
                FallingIcicle ic = _fallingIcicles[i];
                float prevY = ic.Y;
                ic.Vy = Math.Min(16f, ic.Vy + 0.48f);
                ic.Y += ic.Vy;

                bool shattered = false;
                for (int sIdx = 0; sIdx < _orderedSurfaces.Count; sIdx++)
                {
                    WindowSurface s = _orderedSurfaces[sIdx];
                    if (s.IsMaximized) continue;
                    if (ic.X >= s.Left && ic.X <= s.Right && prevY <= s.Top && ic.Y + ic.Length >= s.Top && !IsOccludedByFrontWindow(ic.X, s.Top, s.ZOrder))
                    {
                        SpawnImpactPuff(ic.X, s.Top, 0f, 8);
                        shattered = true;
                        break;
                    }
                }
                if (shattered || ic.Y >= _screenH)
                    _fallingIcicles[i].Active = false;
                else
                    _fallingIcicles[i] = ic;
            }

            for (int i = 0; i < _flyingHats.Length; i++)
            {
                if (!_flyingHats[i].Active) continue;
                FlyingHat fh = _flyingHats[i];
                fh.X += fh.Vx + windVx * 0.12f;
                fh.Y += fh.Vy;
                fh.Vy += 0.32f;
                fh.Vx *= 0.98f;
                fh.Angle += fh.AngularVel;
                fh.Ttl--;
                if (fh.Ttl <= 0 || fh.Y >= _screenH + 20f)
                    _flyingHats[i].Active = false;
                else
                    _flyingHats[i] = fh;
            }

            // 4. Kartopu Fiziği (Yuvarlanma, Duvarlardan Sekme ve Üst Üste Binme)
            for (int i = _snowballs.Count - 1; i >= 0; i--)
            {
                SnowballEntity sb = _snowballs[i];
                if (sb.Dragging) continue;

                if (sb.WobbleAmp > 0.005f)
                {
                    sb.WobblePhase += 0.25f;
                    sb.WobbleAmp *= 0.92f;
                }

                WindowSurface attached;
                if (sb.SurfaceHwnd != IntPtr.Zero && _surfaces.TryGetValue(sb.SurfaceHwnd, out attached) &&
                    !attached.IsMaximized && (attached.IsTaskbar || !IsOccludedByFrontWindow(sb.X, attached.Top, attached.ZOrder)))
                {
                    // Eğimli kar yüzeyinde kartopu yokuş aşağı yuvarlanır
                    float slopeL = attached.GetSnowHeightAtX(sb.X - 6f);
                    float slopeR = attached.GetSnowHeightAtX(sb.X + 6f);
                    if (Math.Abs(slopeL - slopeR) > 0.6f)
                        sb.Vx += (slopeL - slopeR) * 0.010f;

                    sb.X += sb.Vx;
                    sb.RollAngle += sb.Vx / Math.Max(4f, sb.BaseRadius); // Gerçek yuvarlanma: açı = yol / yarıçap
                    sb.Vx *= 0.93f;

                    if (Math.Abs(sb.Vx) > 0.25f && sb.StackCount == 1)
                    {
                        float harvested = attached.HarvestOrPushSnow(sb.X, 10, 0.8f, 0, 0f);
                        GrowSnowball(sb, harvested, 7.5f);
                    }

                    if (CheckSnowballCollisionsWithSnowmen(sb))
                        continue;

                    // Pencere kenarından dışarı yuvarlandıysa boşluğa düşer!
                    if (sb.X < attached.Left + 6f || sb.X > attached.Right - 6f)
                    {
                        sb.SurfaceHwnd = IntPtr.Zero;
                    }
                    else
                    {
                        sb.Y = attached.Top - attached.GetSnowHeightAtX(sb.X) * 0.35f;
                        sb.Vy = 0f;
                    }
                }
                else
                {
                    float prevY = sb.Y;
                    sb.Vy = Math.Min(18f, sb.Vy + 0.52f);
                    sb.X += sb.Vx;
                    sb.Y += sb.Vy;
                    sb.RollAngle += sb.Vx * 0.1f;

                    // Ekran kenarlarından sekme
                    if (sb.X - sb.BaseRadius < 0f)
                    {
                        sb.X = sb.BaseRadius;
                        sb.Vx = -sb.Vx * 0.72f;
                        BurstSnowSparkles(sb.X, sb.Y - sb.BaseRadius, 6);
                    }
                    else if (sb.X + sb.BaseRadius > _screenW)
                    {
                        sb.X = _screenW - sb.BaseRadius;
                        sb.Vx = -sb.Vx * 0.72f;
                        BurstSnowSparkles(sb.X, sb.Y - sb.BaseRadius, 6);
                    }
                    if (sb.Y - sb.TotalHeight < 0f && sb.Vy < 0f)
                    {
                        sb.Y = sb.TotalHeight;
                        sb.Vy = -sb.Vy * 0.65f;
                    }

                    // Havada uçarken bir kardan adama veya ikili istife çarptı mı kontrol et!
                    if (CheckSnowballCollisionsWithSnowmen(sb))
                        continue;

                    // Havada yavaşça düşerken başka bir kartopunun üstüne denk gelirse otomatik istiflenir!
                    float sbSpd = (float)Math.Sqrt(sb.Vx * sb.Vx + sb.Vy * sb.Vy);
                    if (sb.StackCount == 1 && sb.Vy > 0f && sbSpd < 6.2f && TryStackSnowballOntoAnother(sb))
                        continue;

                    // Pencere üstüne iniş kontrolü
                    if (sb.Vy > 0f)
                    {
                        for (int sIdx = 0; sIdx < _orderedSurfaces.Count; sIdx++)
                        {
                            WindowSurface s = _orderedSurfaces[sIdx];
                            if (s.IsMaximized) continue;
                            if (sb.X >= s.Left + 8 && sb.X <= s.Right - 8)
                            {
                                if (prevY <= s.Top + 10f && sb.Y >= s.Top)
                                {
                                    if (!IsOccludedByFrontWindow(sb.X, s.Top, s.ZOrder))
                                    {
                                        if (sb.Vy > 7.5f)
                                        {
                                            sb.Vy = -sb.Vy * 0.42f;
                                            sb.Vx *= 0.8f;
                                            BurstSnowSparkles(sb.X, s.Top, 8);
                                        }
                                        else
                                        {
                                            sb.SurfaceHwnd = s.Hwnd;
                                            sb.Y = s.Top;
                                            sb.Vy = 0f;
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (sb.Y >= _screenH - 4f)
                    {
                        WindowSurface tb = _orderedSurfaces.Count > 0 ? _orderedSurfaces[_orderedSurfaces.Count - 1] : null;
                        if (tb != null)
                        {
                            sb.SurfaceHwnd = tb.Hwnd;
                            sb.Y = tb.Top;
                            sb.Vy = 0f;
                        }
                    }
                }
            }

            // 5. Kardan Adam Fiziği (Sallanma, Fırlatılma ve Kenarlardan Sekme)
            for (int i = 0; i < _snowmen.Count; i++)
            {
                SnowmanEntity sm = _snowmen[i];
                if (sm.HitCooldown > 0) sm.HitCooldown--;
                if (sm.SurpriseTimer > 0) sm.SurpriseTimer--;
                if (sm.BlinkTimer > 0) sm.BlinkTimer--;
                else sm.BlinkTimer = 90 + _rng.Next(160);

                if (sm.Dragging) continue;

                sm.TiltVel += (-sm.TiltAngle) * 0.16f;
                sm.TiltVel *= 0.84f;
                sm.TiltAngle = Math.Max(-42f, Math.Min(42f, sm.TiltAngle + sm.TiltVel));

                WindowSurface attached;
                if (sm.SurfaceHwnd != IntPtr.Zero && _surfaces.TryGetValue(sm.SurfaceHwnd, out attached) &&
                    !attached.IsMaximized && (attached.IsTaskbar || !IsOccludedByFrontWindow(sm.X, attached.Top, attached.ZOrder)))
                {
                    sm.X += sm.Vx;
                    sm.Vx *= 0.86f;
                    if (sm.X < attached.Left + 6f || sm.X > attached.Right - 6f)
                    {
                        // Pencerenin kenarına kadar itildiyse boşluğa düşer!
                        sm.SurfaceHwnd = IntPtr.Zero;
                    }
                    else
                    {
                        sm.Y = attached.Top - attached.GetSnowHeightAtX(sm.X) * 0.35f;
                        sm.Vy = 0f;
                    }
                }
                else
                {
                    float prevY = sm.Y;
                    sm.Vy = Math.Min(18f, sm.Vy + 0.52f);
                    sm.X += sm.Vx;
                    sm.Y += sm.Vy;

                    if (sm.X - sm.R1 < 0f) { sm.X = sm.R1; sm.Vx = -sm.Vx * 0.7f; sm.TiltVel = 8f; }
                    else if (sm.X + sm.R1 > _screenW) { sm.X = _screenW - sm.R1; sm.Vx = -sm.Vx * 0.7f; sm.TiltVel = -8f; }

                    if (sm.Vy > 0f)
                    {
                        for (int sIdx = 0; sIdx < _orderedSurfaces.Count; sIdx++)
                        {
                            WindowSurface s = _orderedSurfaces[sIdx];
                            if (s.IsMaximized) continue;
                            if (sm.X >= s.Left + 12 && sm.X <= s.Right - 12)
                            {
                                if (prevY <= s.Top + 12f && sm.Y >= s.Top)
                                {
                                    if (!IsOccludedByFrontWindow(sm.X, s.Top, s.ZOrder))
                                    {
                                        sm.SurfaceHwnd = s.Hwnd;
                                        sm.Y = s.Top;
                                        sm.Vy = 0f;
                                        sm.Vx *= 0.5f;
                                        sm.TiltVel = sm.Vx * 1.2f;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (sm.Y >= _screenH - 4f && _orderedSurfaces.Count > 0)
                    {
                        WindowSurface tb = _orderedSurfaces[_orderedSurfaces.Count - 1];
                        sm.SurfaceHwnd = tb.Hwnd;
                        sm.Y = tb.Top;
                        sm.Vy = 0f;
                    }
                }
            }

            // 6. Sarı Kar Küreme Aracı (Fırlatma & Kenarlardan Sekme + Önünde Görünür Kar Yığma!)
            for (int i = 0; i < _plows.Count; i++)
            {
                SnowPlowEntity plow = _plows[i];
                if (plow.Dragging)
                {
                    if (plow.PlowedSnowMass > 0.4f) ShedPlowMound(plow, 0.5f);
                    continue;
                }

                WindowSurface plowSurf;
                bool plowSurfaceValid = (plow.SurfaceHwnd != IntPtr.Zero) &&
                                        _surfaces.TryGetValue(plow.SurfaceHwnd, out plowSurf) &&
                                        !plowSurf.IsMaximized &&
                                        (plowSurf.IsTaskbar || !IsOccludedByFrontWindow(plow.X, plowSurf.Top, plowSurf.ZOrder));
                if (plow.IsAirborne || !plowSurfaceValid)
                {
                    // Havadaki / fırlatılan araçta kepçede kar kalmaz: hemen savrulur
                    if (plow.PlowedSnowMass > 0.4f) ShedPlowMound(plow, 0.35f);
                    // HAVADA FIRLATILMA VE EKRAN/PENCERE KENARLARINDAN SEKME FİZİĞİ
                    float prevY = plow.Y;
                    plow.Vy = Math.Min(22f, plow.Vy + 0.55f);
                    plow.X += plow.Vx;
                    plow.Y += plow.Vy;
                    plow.Angle += plow.AngularVel;
                    plow.Vx *= 0.992f;
                    plow.AngularVel *= 0.985f;

                    // Sol ve Sağ ekran kenarlarına çarpıp sekme
                    if (plow.X < 28f)
                    {
                        plow.X = 28f;
                        plow.Vx = -plow.Vx * 0.78f;
                        plow.AngularVel = -plow.AngularVel * 0.7f + 6f;
                        plow.Direction = 1;
                        BurstSnowSparkles(plow.X, plow.Y - 12f, 12);
                    }
                    else if (plow.X > _screenW - 28f)
                    {
                        plow.X = _screenW - 28f;
                        plow.Vx = -plow.Vx * 0.78f;
                        plow.AngularVel = -plow.AngularVel * 0.7f - 6f;
                        plow.Direction = -1;
                        BurstSnowSparkles(plow.X, plow.Y - 12f, 12);
                    }

                    // Üst ekran kenarına çarpıp sekme
                    if (plow.Y < 28f && plow.Vy < 0f)
                    {
                        plow.Y = 28f;
                        plow.Vy = -plow.Vy * 0.72f;
                        BurstSnowSparkles(plow.X, plow.Y, 10);
                    }

                    // Açık pencerelerin yan duvarlarına çarpıp sekme
                    for (int sIdx = 0; sIdx < _orderedSurfaces.Count; sIdx++)
                    {
                        WindowSurface s = _orderedSurfaces[sIdx];
                        if (s.IsTaskbar || s.IsMaximized) continue;
                        if (plow.Y > s.Top + 14 && plow.Y < s.Bottom - 10)
                        {
                            if (plow.Vx > 0 && plow.X >= s.Left - 20 && plow.X <= s.Left + 12)
                            {
                                plow.X = s.Left - 20;
                                plow.Vx = -plow.Vx * 0.75f;
                                plow.Direction = -1;
                                BurstSnowSparkles(s.Left, plow.Y, 10);
                            }
                            else if (plow.Vx < 0 && plow.X <= s.Right + 20 && plow.X >= s.Right - 12)
                            {
                                plow.X = s.Right + 20;
                                plow.Vx = -plow.Vx * 0.75f;
                                plow.Direction = 1;
                                BurstSnowSparkles(s.Right, plow.Y, 10);
                            }
                        }
                    }

                    // Pencere üst kenarına veya görev çubuğuna çarpma / iniş
                    if (plow.Vy > 0f)
                    {
                        for (int sIdx = 0; sIdx < _orderedSurfaces.Count; sIdx++)
                        {
                            WindowSurface s = _orderedSurfaces[sIdx];
                            if (s.IsMaximized) continue;
                            if (plow.X >= s.Left + 16 && plow.X <= s.Right - 16)
                            {
                                if (prevY <= s.Top + 18f && plow.Y >= s.Top)
                                {
                                    if (s.IsTaskbar || !IsOccludedByFrontWindow(plow.X, s.Top, s.ZOrder))
                                    {
                                        // Sadece yüksekten çok hızlı fırlatıldıysa 1 kez zıplasın, görev çubuğunda asla sonsuz döngüye girmesin!
                                        if (plow.Vy > 12.5f && !s.IsTaskbar)
                                        {
                                            plow.Y = s.Top - 1f;
                                            plow.Vy = -plow.Vy * 0.42f;
                                            plow.Vx *= 0.80f;
                                            plow.AngularVel = plow.Vx * 1.2f;
                                            BurstSnowSparkles(plow.X, s.Top, 12);
                                        }
                                        else
                                        {
                                            plow.IsAirborne = false;
                                            plow.SurfaceHwnd = s.Hwnd;
                                            plow.Y = s.Top;
                                            plow.Vy = 0f;
                                            plow.Angle = 0f;
                                            plow.AngularVel = 0f;
                                            if (Math.Abs(plow.Vx) > 1.5f)
                                                plow.Direction = (plow.Vx >= 0f) ? 1 : -1;
                                            plow.Vx = 0f;
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    // Bulunduğu monitörün görev çubuğu seviyesinin altına indiyse o monitörün görev çubuğuna güvenle oturt!
                    WindowSurface targetTb = FindTaskbarAtX(plow.X);
                    if (targetTb != null && plow.Y >= targetTb.Top)
                    {
                        plow.IsAirborne = false;
                        plow.SurfaceHwnd = targetTb.Hwnd;
                        plow.X = Math.Max(targetTb.Left + 38f, Math.Min(targetTb.Right - 38f, plow.X));
                        plow.Y = targetTb.Top;
                        plow.Vy = 0f;
                        plow.Vx = 0f;
                        plow.Angle = 0f;
                        plow.AngularVel = 0f;
                    }
                    continue;
                }

                // YÜZEY ÜZERİNDE SÜRÜŞ VE BIÇAK ÖNÜNDE KAR BİRİKTİRME FİZİĞİ
                WindowSurface surf = _surfaces[plow.SurfaceHwnd];
                plow.Y = surf.Top;
                plow.Angle = plow.Angle * 0.7f;
                plow.X += plow.Direction * plow.DriveSpeed;
                plow.WheelPhase += 0.42f;

                float bladeX = plow.X + plow.Direction * 24f;
                float harvested = surf.HarvestOrPushSnow(bladeX, 16, 3.8f, plow.Direction, 0.35f);

                // Kar küreme aracı yol üzerindeki kartoplarını ve kardan adamları iter;
                // kenara/sona sıkıştırdığı anda bug'a girmemeleri için parçalayarak direkt yok eder!
                for (int bIdx = _snowballs.Count - 1; bIdx >= 0; bIdx--)
                {
                    SnowballEntity sb = _snowballs[bIdx];
                    if (sb.Dragging) continue;
                    if (Math.Abs(sb.X - bladeX) <= sb.BaseRadius + 14f && Math.Abs(sb.Y - plow.Y) <= 32f)
                    {
                        bool nearLeftEnd = (plow.Direction < 0) && (sb.X <= surf.Left + 85f || sb.X <= 75f);
                        bool nearRightEnd = (plow.Direction > 0) && (sb.X >= surf.Right - 85f || sb.X >= _screenW - 75f);
                        if (nearLeftEnd || nearRightEnd)
                        {
                            // Sona sıkıştı: kartopunu kar parçacıklarına patlatıp direkt yok et!
                            plow.PlowedSnowMass = Math.Min(110f, plow.PlowedSnowMass + sb.BaseRadius * 1.4f);
                            BurstSnowSparkles(sb.X, sb.Y - sb.BaseRadius, 24);
                            _snowballs.RemoveAt(bIdx);
                        }
                        else
                        {
                            sb.Vx = plow.Direction * (plow.DriveSpeed + 1.6f);
                            sb.X += plow.Direction * 4.5f;
                        }
                    }
                }
                for (int mIdx = _snowmen.Count - 1; mIdx >= 0; mIdx--)
                {
                    SnowmanEntity sm = _snowmen[mIdx];
                    if (sm.Dragging) continue;
                    if (Math.Abs(sm.X - bladeX) <= sm.R1 + 14f && Math.Abs(sm.Y - plow.Y) <= 34f)
                    {
                        bool nearLeftEnd = (plow.Direction < 0) && (sm.X <= surf.Left + 95f || sm.X <= 85f);
                        bool nearRightEnd = (plow.Direction > 0) && (sm.X >= surf.Right - 95f || sm.X >= _screenW - 85f);
                        if (nearLeftEnd || nearRightEnd)
                        {
                            // Sona sıkıştı: kardan adamı şapkasını uçurup kar parçacıklarına patlatarak direkt yok et!
                            if (sm.HasHat)
                            {
                                float y3 = sm.Y - (sm.R1 + sm.R2 + sm.R3) * 1.5f;
                                SpawnFlyingHat(sm.X, y3, -plow.Direction * 3.5f, -5.5f, sm.R2 / 8.5f, sm.HatStyle, sm.HatColor);
                            }
                            plow.PlowedSnowMass = Math.Min(110f, plow.PlowedSnowMass + (sm.R1 + sm.R2) * 1.2f);
                            BurstSnowSparkles(sm.X, sm.Y - sm.R1 - sm.R2, 36);
                            _snowmen.RemoveAt(mIdx);
                        }
                        else
                        {
                            sm.SurpriseTimer = 45;
                            sm.Vx = plow.Direction * (plow.DriveSpeed + 1.5f);
                            sm.X += plow.Direction * 4.5f;
                            sm.TiltVel = plow.Direction * 6f;
                        }
                    }
                }

                // Bıçağın önündeki kar kütlesi topladıkça büyür!
                if (harvested > 0.1f)
                {
                    plow.PlowedSnowMass = Math.Min(110f, plow.PlowedSnowMass + harvested * 1.35f);
                }
                else
                {
                    // Kar yoksa bıçaktaki kar yavaşça savrulur, sonunda kepçe boşalır
                    plow.PlowedSnowMass *= 0.994f;
                    if (plow.PlowedSnowMass < 1.2f) plow.PlowedSnowMass = 0f;
                }

                // Kepçe kapasitesi dolunca fazlalık öne/yanlara taşar
                if (plow.PlowedSnowMass > 85f && (_frameCount % 3 == 0))
                    ShedPlowMound(plow, 0.10f);

                // Bıçak önündeki kar kütlesinden sürekli öne/yukarı kar fıskiyesi (plume) fırlat
                if ((harvested > 0.35f || plow.PlowedSnowMass > 22f) && (_frameCount % 2 == 0))
                {
                    SpawnChunk(
                        bladeX + plow.Direction * 8f,
                        surf.Top - 8f - Math.Min(14f, plow.PlowedSnowMass * 0.16f),
                        plow.Direction * (2.2f + (float)_rng.NextDouble() * 3.2f),
                        -2.0f - (float)_rng.NextDouble() * 2.8f,
                        3.0f + (float)_rng.NextDouble() * 2.4f,
                        1.1f,
                        36 + _rng.Next(20),
                        true,
                        Color.White);
                }

                // Pencerenin sonuna ulaştığında biriktirdiği dev kar yığınını kenardan aşağı fırlatıp döner!
                if (plow.X >= surf.Right - 38f)
                {
                    plow.X = surf.Right - 38f;
                    DumpPlowMoundAtEdge(plow, surf.Right - 10f, surf.Top, 1);
                    plow.Direction = -1;
                }
                else if (plow.X <= surf.Left + 38f)
                {
                    plow.X = surf.Left + 38f;
                    DumpPlowMoundAtEdge(plow, surf.Left + 10f, surf.Top, -1);
                    plow.Direction = 1;
                }
            }

            UpdateCampfiresAndPopups(windVx);
            UpdateCableCarPhysics(windVx);
            UpdateAudioReactive();
            UpdateAuroraAndShootingStars();
            UpdateSantaSleighPhysics(windVx);
            UpdateGiftsPhysics(windVx);
            UpdateFireworksPhysics(windVx);
            UpdatePenguinsPhysics(windVx);
            UpdateSnowmanAiFight();
            UpdateCornerFrost();
            UpdatePolarExpressTrain(windVx);
            UpdateTreesPhysics();
            UpdateLanternsPhysics(windVx);
            UpdateWinterBirdsPhysics();
            UpdateCursorTrail();
            UpdateCountdownAndConfetti(windVx);

            // Gardırop veya Ağaç Süsleme penceresi açıkken hedef nesne silindiyse kapat
            if (_wardrobeForm != null && !_wardrobeForm.IsDisposed &&
                _wardrobeForm.TargetSnowman != null && !_snowmen.Contains(_wardrobeForm.TargetSnowman))
            {
                CloseWardrobeIfOpen();
            }
            if (_treeDecoratorForm != null && !_treeDecoratorForm.IsDisposed &&
                _treeDecoratorForm.TargetTree != null && !_trees.Contains(_treeDecoratorForm.TargetTree))
            {
                CloseTreeDecoratorIfOpen();
            }
        }

        private void EnsureCountdownPos()
        {
            if (_countdownX < 0f)
            {
                _countdownX = Math.Max(20f, _screenW - 246f);
                _countdownY = 26f;
            }
        }

        private bool IsPointInsideCountdownWidget(float px, float py)
        {
            EnsureCountdownPos();
            return px >= _countdownX && px <= _countdownX + 216f && py >= _countdownY && py <= _countdownY + 66f;
        }

        private void UpdateTreesPhysics()
        {
            for (int i = 0; i < _trees.Count; i++)
            {
                ChristmasTreeEntity tr = _trees[i];
                if (tr.HitCooldown > 0) tr.HitCooldown--;

                // Yaylı zarif sallanma fiziği
                tr.WobbleVel += -tr.WobbleAngle * 0.14f;
                tr.WobbleVel *= 0.85f;
                tr.WobbleAngle += tr.WobbleVel;
                if (Math.Abs(tr.WobbleAngle) > 22f)
                    tr.WobbleAngle = Math.Sign(tr.WobbleAngle) * 22f;

                if (tr.Dragging) continue;

                WindowSurface surf = null;
                if (tr.SurfaceHwnd != IntPtr.Zero)
                    _surfaces.TryGetValue(tr.SurfaceHwnd, out surf);

                if (surf != null && !surf.IsMaximized)
                {
                    tr.Y = surf.Top;
                    if (tr.X < surf.Left + 18f || tr.X > surf.Right - 18f)
                        tr.SurfaceHwnd = IntPtr.Zero;
                }
                else
                {
                    tr.SurfaceHwnd = IntPtr.Zero;
                    float prevY = tr.Y;
                    tr.X += tr.Vx;
                    tr.Y += tr.Vy;
                    tr.Vy += 0.42f;
                    tr.Vx *= 0.96f;

                    if (tr.X < 36f) { tr.X = 36f; tr.Vx = -tr.Vx * 0.4f; }
                    else if (tr.X > _screenW - 36f) { tr.X = _screenW - 36f; tr.Vx = -tr.Vx * 0.4f; }

                    if (tr.Vy > 0f)
                    {
                        for (int sIdx = 0; sIdx < _orderedSurfaces.Count; sIdx++)
                        {
                            WindowSurface s = _orderedSurfaces[sIdx];
                            if (s.IsMaximized) continue;
                            if (tr.X >= s.Left + 24f && tr.X <= s.Right - 24f &&
                                prevY <= s.Top + 14f && tr.Y >= s.Top &&
                                !IsOccludedByFrontWindow(tr.X, s.Top, s.ZOrder))
                            {
                                tr.SurfaceHwnd = s.Hwnd;
                                tr.Y = s.Top;
                                tr.Vx = 0f;
                                tr.Vy = 0f;
                                tr.WobbleVel += 2.5f;
                                break;
                            }
                        }
                    }

                    if (tr.Y >= _screenH - 2f && tr.SurfaceHwnd == IntPtr.Zero)
                    {
                        WindowSurface tb = _orderedSurfaces.Count > 0 ? _orderedSurfaces[_orderedSurfaces.Count - 1] : null;
                        if (tb != null)
                        {
                            tr.SurfaceHwnd = tb.Hwnd;
                            tr.Y = tb.Top;
                            tr.Vx = 0f;
                            tr.Vy = 0f;
                        }
                    }
                }
            }
        }

        private void UpdateLanternsPhysics(float windVx)
        {
            if (LanternsEnabled && !PerformanceMode)
            {
                _lanternSpawnCooldown--;
                if (_lanternSpawnCooldown <= 0)
                {
                    _lanternSpawnCooldown = 110 + _rng.Next(130);
                    for (int i = 0; i < _lanterns.Length; i++)
                    {
                        if (_lanterns[i].Active) continue;
                        _lanterns[i].Active = true;
                        _lanterns[i].X = 60f + (float)_rng.NextDouble() * Math.Max(120f, _screenW - 120f);
                        _lanterns[i].Y = _screenH + 24f;
                        _lanterns[i].Vy = -(0.52f + (float)_rng.NextDouble() * 0.45f);
                        _lanterns[i].SwayPhase = (float)(_rng.NextDouble() * Math.PI * 2.0);
                        _lanterns[i].FlickerPhase = (float)(_rng.NextDouble() * Math.PI * 2.0);
                        _lanterns[i].Scale = 0.68f + (float)_rng.NextDouble() * 0.52f;
                        _lanterns[i].WarmHue = _rng.Next(3);
                        break;
                    }
                }
            }

            for (int i = 0; i < _lanterns.Length; i++)
            {
                if (!_lanterns[i].Active) continue;
                WishLantern l = _lanterns[i];
                l.SwayPhase += 0.028f;
                l.FlickerPhase += 0.14f;
                l.X += windVx * 0.14f + (float)Math.Sin(l.SwayPhase) * 0.42f;
                l.Y += l.Vy;
                if (l.X < -40f) l.X = _screenW + 30f;
                else if (l.X > _screenW + 40f) l.X = -30f;
                if (l.Y < -50f || !LanternsEnabled)
                    l.Active = false;
                _lanterns[i] = l;
            }
        }

        private void AssignNewBirdPerch(WinterBirdEntity bird)
        {
            bird.PerchedTree = null;
            bird.SurfaceHwnd = IntPtr.Zero;
            bird.State = 1;

            // %45 ihtimalle varsa bir Yılbaşı Ağacının dalına kon!
            if (_trees.Count > 0 && _rng.Next(100) < 48)
            {
                ChristmasTreeEntity tr = _trees[_rng.Next(_trees.Count)];
                PointF off = tr.GetPerchOffset(_rng.Next(3));
                bird.PerchedTree = tr;
                bird.TreeOffsetX = off.X;
                bird.TreeOffsetY = off.Y;
                bird.TargetX = tr.X + off.X;
                bird.TargetY = tr.Y + off.Y;
                bird.Direction = (bird.TargetX >= bird.X) ? 1 : -1;
                return;
            }

            // Değilse açık bir pencere üstüne veya görev çubuğuna kon
            List<WindowSurface> cands = new List<WindowSurface>();
            for (int i = 0; i < _orderedSurfaces.Count; i++)
            {
                WindowSurface s = _orderedSurfaces[i];
                if (!s.IsMaximized && s.Width >= 140)
                    cands.Add(s);
            }
            if (cands.Count > 0)
            {
                WindowSurface chosen = cands[_rng.Next(cands.Count)];
                float tx = chosen.Left + 36f + (float)_rng.NextDouble() * Math.Max(30f, chosen.Width - 72f);
                bird.SurfaceHwnd = chosen.Hwnd;
                bird.TargetX = tx;
                bird.TargetY = chosen.Top;
                bird.Direction = (bird.TargetX >= bird.X) ? 1 : -1;
            }
            else
            {
                bird.TargetX = 100f + (float)_rng.NextDouble() * Math.Max(100f, _screenW - 200f);
                bird.TargetY = Math.Max(80f, _screenH - 55f);
            }
        }

        private void UpdateWinterBirdsPhysics()
        {
            if (!BirdsEnabled)
            {
                if (_birds.Count > 0) _birds.Clear();
                return;
            }
            if (_birds.Count < 3 && (_frameCount % 90 == 0))
                EnsureWinterBirdsCount();

            Point cur = _lastCursorPos;
            for (int i = 0; i < _birds.Count; i++)
            {
                WinterBirdEntity b = _birds[i];
                b.LookPhase += 0.05f;

                if (b.State == 0)
                {
                    // Konmuş durum: pencereyi veya yılbaşı ağacını takip et
                    b.StateTimer--;
                    if (b.PerchedTree != null)
                    {
                        if (!_trees.Contains(b.PerchedTree) || b.PerchedTree.Dragging)
                        {
                            AssignNewBirdPerch(b);
                            continue;
                        }
                        b.X = b.PerchedTree.X + b.TreeOffsetX;
                        b.Y = b.PerchedTree.Y + b.TreeOffsetY;
                    }
                    else if (b.SurfaceHwnd != IntPtr.Zero)
                    {
                        WindowSurface surf;
                        if (_surfaces.TryGetValue(b.SurfaceHwnd, out surf) && !surf.IsMaximized)
                        {
                            b.Y = surf.Top;
                            if (b.X < surf.Left + 14f || b.X > surf.Right - 14f)
                            {
                                AssignNewBirdPerch(b);
                                continue;
                            }
                        }
                        else
                        {
                            AssignNewBirdPerch(b);
                            continue;
                        }
                    }

                    // Ara sıra minik zıplama veya sağa sola bakma
                    if (_rng.Next(180) == 0) b.Direction = -b.Direction;
                    if (_rng.Next(220) == 0) b.HopOffset = -5.5f;
                    b.HopOffset *= 0.78f;

                    // Fare imleci çok yaklaşırsa ürküp kanat çırparak başka yere uçsun!
                    float dx = cur.X - b.X;
                    float dy = cur.Y - (b.Y - 8f);
                    if (dx * dx + dy * dy < 72f * 72f || b.StateTimer <= 0)
                    {
                        AssignNewBirdPerch(b);
                        b.Vy = -2.2f;
                    }
                }
                else
                {
                    // Uçuş durumu: hedefe zarif bir yay çizerek uç
                    b.WingPhase += 0.42f;
                    if (b.PerchedTree != null && _trees.Contains(b.PerchedTree))
                    {
                        b.TargetX = b.PerchedTree.X + b.TreeOffsetX;
                        b.TargetY = b.PerchedTree.Y + b.TreeOffsetY;
                    }
                    else if (b.SurfaceHwnd != IntPtr.Zero)
                    {
                        WindowSurface s;
                        if (_surfaces.TryGetValue(b.SurfaceHwnd, out s) && !s.IsMaximized)
                            b.TargetY = s.Top;
                    }

                    float dx = b.TargetX - b.X;
                    float dy = b.TargetY - b.Y;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (dist < 8f)
                    {
                        b.X = b.TargetX;
                        b.Y = b.TargetY;
                        b.Vx = 0f;
                        b.Vy = 0f;
                        b.State = 0;
                        b.StateTimer = 240 + _rng.Next(360);
                    }
                    else
                    {
                        float spd = Math.Min(5.2f, Math.Max(2.4f, dist * 0.045f));
                        float lift = dist > 90f ? -0.85f : 0f;
                        b.Vx = b.Vx * 0.86f + (dx / dist) * spd * 0.14f;
                        b.Vy = b.Vy * 0.86f + ((dy / dist) * spd + lift) * 0.14f;
                        b.X += b.Vx;
                        b.Y += b.Vy;
                        if (Math.Abs(b.Vx) > 0.25f)
                            b.Direction = (b.Vx >= 0f) ? 1 : -1;
                    }
                }
            }
        }

        private void UpdateCursorTrail()
        {
            Point raw = Cursor.Position;
            Point cur = new Point(raw.X - _originX, raw.Y - _originY);

            if (_hasLastTrailCursor)
            {
                int dx = cur.X - _lastTrailCursor.X;
                int dy = cur.Y - _lastTrailCursor.Y;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy);

                // İmleç Kış Işıltısı (Cursor Magic Trail)
                if (CursorTrailEnabled && !PerformanceMode && dist > 6f)
                {
                    int spawnN = dist > 28f ? 2 : 1;
                    for (int k = 0; k < spawnN; k++)
                    {
                        SpawnMagicSparkle(
                            cur.X + (float)(_rng.NextDouble() * 10.0 - 5.0),
                            cur.Y + (float)(_rng.NextDouble() * 10.0 - 5.0),
                            false);
                    }
                }
            }

            _lastTrailCursor = cur;
            _hasLastTrailCursor = true;

            for (int i = 0; i < _magicSparkles.Length; i++)
            {
                if (!_magicSparkles[i].Active) continue;
                MagicSparkle sp = _magicSparkles[i];
                if (sp.IsRing)
                {
                    sp.Size += 2.3f;
                }
                else
                {
                    sp.X += sp.Vx;
                    sp.Y += sp.Vy;
                    sp.Vy += 0.025f;
                }
                sp.Ttl--;
                if (sp.Ttl <= 0) sp.Active = false;
                _magicSparkles[i] = sp;
            }
        }

        private void UpdateCountdownAndConfetti(float windVx)
        {
            if (_celebrationBannerTicks > 0) _celebrationBannerTicks--;

            // Gerçek gece yarısı (1 Ocak 00:00:00) yakalandığında otomatik kutlama başlat!
            if (CountdownEnabled && (_frameCount % 30 == 0))
            {
                DateTime now = DateTime.Now;
                if (now.Month == 1 && now.Day == 1 && now.Hour == 0 && now.Minute == 0 && _lastCelebratedYear != now.Year)
                {
                    _lastCelebratedYear = now.Year;
                    TriggerMidnightCelebration();
                }
            }

            for (int i = 0; i < _confetti.Length; i++)
            {
                if (!_confetti[i].Active) continue;
                ConfettiParticle c = _confetti[i];
                c.Vx = c.Vx * 0.97f + windVx * 0.025f;
                c.Vy = Math.Min(4.2f, c.Vy + 0.09f);
                c.X += c.Vx + (float)Math.Sin(_time * 4.5f + i) * 0.55f;
                c.Y += c.Vy;
                c.Angle += c.Spin;
                c.Ttl--;
                if (c.Ttl <= 0 || c.Y > _screenH + 20f)
                    c.Active = false;
                _confetti[i] = c;
            }
        }

        private void UpdateAudioReactive()
        {
            if (!AudioReactiveEnabled)
            {
                _audioLevelSmooth *= 0.85f;
                _audioBeat *= 0.82f;
                return;
            }
            if ((_frameCount & 1) == 0)
            {
                float peak = _audioMeter.ReadPeakLevel();
                _audioLevelSmooth = _audioLevelSmooth * 0.65f + peak * 0.35f;
                _audioAvg = _audioAvg * 0.94f + peak * 0.06f;
                if (peak > _audioAvg + 0.09f && peak > 0.14f)
                    _audioBeat = Math.Min(1.0f, _audioBeat + 0.55f);
            }
            _audioBeat *= 0.86f;
        }

        private void UpdatePenguinsPhysics(float windVx)
        {
            for (int i = 0; i < _penguins.Count; i++)
            {
                PenguinEntity p = _penguins[i];
                if (p.HopTimer > 0) p.HopTimer--;
                if (p.Dragging) continue;

                WindowSurface attached;
                bool validSurf = (p.SurfaceHwnd != IntPtr.Zero) &&
                                 _surfaces.TryGetValue(p.SurfaceHwnd, out attached) &&
                                 !attached.IsMaximized &&
                                 (attached.IsTaskbar || !IsOccludedByFrontWindow(p.X, attached.Top, attached.ZOrder));

                if (!validSurf)
                {
                    // Havada süzülme & iniş fiziği
                    p.SurfaceHwnd = IntPtr.Zero;
                    float prevY = p.Y;
                    p.Vy = Math.Min(16f, p.Vy + 0.48f);
                    p.X += p.Vx;
                    p.Y += p.Vy;
                    p.Vx *= 0.99f;
                    p.FlipperPhase += 0.55f;

                    if (p.X < 20f) { p.X = 20f; p.Vx = Math.Abs(p.Vx) * 0.7f; p.Direction = 1; }
                    else if (p.X > _screenW - 20f) { p.X = _screenW - 20f; p.Vx = -Math.Abs(p.Vx) * 0.7f; p.Direction = -1; }

                    if (p.Vy > 0f)
                    {
                        for (int sIdx = 0; sIdx < _orderedSurfaces.Count; sIdx++)
                        {
                            WindowSurface s = _orderedSurfaces[sIdx];
                            if (s.IsMaximized) continue;
                            if (p.X >= s.Left + 14 && p.X <= s.Right - 14 && prevY <= s.Top + 10f && p.Y >= s.Top)
                            {
                                if (s.IsTaskbar || !IsOccludedByFrontWindow(p.X, s.Top, s.ZOrder))
                                {
                                    p.SurfaceHwnd = s.Hwnd;
                                    p.Y = s.Top;
                                    p.Vy = 0f;
                                    p.Vx = 0f;
                                    BurstSnowSparkles(p.X, s.Top - 4f, 8);
                                    break;
                                }
                            }
                        }
                    }

                    if (p.SurfaceHwnd == IntPtr.Zero)
                    {
                        WindowSurface tb = FindTaskbarAtX(p.X);
                        if (tb != null && p.Y >= tb.Top)
                        {
                            p.SurfaceHwnd = tb.Hwnd;
                            p.Y = tb.Top;
                            p.Vy = 0f;
                            p.Vx = 0f;
                        }
                    }
                    continue;
                }

                // Yüzey üzerinde yürüme / karnında kayma / el sallama davranışları
                WindowSurface surf = _surfaces[p.SurfaceHwnd];
                p.Y = surf.Top;
                p.StateTimer--;
                if (p.StateTimer <= 0)
                {
                    double r = _rng.NextDouble();
                    if (r < 0.52)
                    {
                        p.State = 0; // Paytak yürü
                        p.StateTimer = 140 + _rng.Next(200);
                        if (_rng.NextDouble() < 0.35) p.Direction = -p.Direction;
                    }
                    else if (r < 0.80)
                    {
                        p.State = 1; // Karnının üstünde kay (Toboggan)!
                        p.StateTimer = 95 + _rng.Next(95);
                    }
                    else
                    {
                        p.State = 2; // Dur & kanat çırp
                        p.StateTimer = 70 + _rng.Next(70);
                    }
                }

                if (p.State == 0)
                {
                    p.X += p.Direction * 0.95f;
                    p.WalkPhase += 0.24f;
                    p.FlipperPhase += 0.14f;
                }
                else if (p.State == 1)
                {
                    p.X += p.Direction * 2.55f;
                    p.FlipperPhase += 0.18f;
                    if (_frameCount % 4 == 0)
                    {
                        SpawnChunk(
                            p.X - p.Direction * 12f, p.Y - 3f,
                            -p.Direction * (0.8f + (float)_rng.NextDouble() * 1.4f),
                            -0.9f - (float)_rng.NextDouble() * 1.0f,
                            2.4f, 0.2f, 22, false, Color.White);
                    }
                }
                else
                {
                    p.FlipperPhase += 0.35f;
                }

                // Pencere kenarlarından geri dön
                if (p.X <= surf.Left + 24f)
                {
                    p.X = surf.Left + 24f;
                    p.Direction = 1;
                }
                else if (p.X >= surf.Right - 24f)
                {
                    p.X = surf.Right - 24f;
                    p.Direction = -1;
                }
            }
        }

        private void UpdateSnowmanAiFight()
        {
            if (!SnowmanFightEnabled || _snowmen.Count < 2 || _snowballs.Count >= 18) return;

            for (int i = 0; i < _snowmen.Count; i++)
            {
                SnowmanEntity sm = _snowmen[i];
                if (sm.Dragging || sm.SurfaceHwnd == IntPtr.Zero) continue;

                sm.FightCooldown--;
                if (sm.FightCooldown > 0) continue;

                sm.FightCooldown = 190 + _rng.Next(260); // 3..7.5 saniye arası

                // Uygun bir hedef kardan adam seç
                int targetIdx = _rng.Next(_snowmen.Count);
                if (targetIdx == i) targetIdx = (i + 1) % _snowmen.Count;
                SnowmanEntity target = _snowmen[targetIdx];
                if (target.Dragging) continue;

                float dx = target.X - sm.X;
                float dist = Math.Abs(dx);
                if (dist < 55f || dist > 950f) continue;

                // Parabolik kartopu atış hızı hesapla (yerçekimi ~ 0.52f)
                float launchX = sm.X + Math.Sign(dx) * (sm.R2 + 6f);
                float launchY = sm.Y - sm.R1 * 2f - sm.R2 * 0.8f;
                float targetY = target.Y - target.R1 * 2f - target.R2 * 1.2f;

                float flightFrames = Math.Max(22f, Math.Min(52f, dist / 14.5f));
                float vx = (target.X - launchX) / flightFrames + (float)(_rng.NextDouble() * 1.3 - 0.65);
                float vy = (targetY - launchY - 0.5f * 0.52f * flightFrames * flightFrames) / flightFrames;
                vy = Math.Max(-19f, Math.Min(-3.5f, vy));

                SnowballEntity aiBall = new SnowballEntity(launchX, launchY, 6.8f, IntPtr.Zero);
                aiBall.Vx = vx;
                aiBall.Vy = vy;
                aiBall.IsAiSnowball = true;
                aiBall.IgnoreSnowman = sm;
                _snowballs.Add(aiBall);

                sm.TiltVel += Math.Sign(dx) * 8.5f;
                BurstSnowSparkles(launchX, launchY, 6);
                break; // Aynı karede tek atış
            }
        }

        private void UpdateCornerFrost()
        {
            if (!FrostEnabled)
            {
                for (int i = 0; i < 4; i++)
                    _cornerFrost[i] = Math.Max(0f, _cornerFrost[i] - 0.025f);
                return;
            }

            float targetCap = BlizzardMode ? 0.94f : Math.Min(0.68f, 0.25f + (ActiveSnowCount / 1200f) * 0.45f);
            float growRate = BlizzardMode ? 0.0022f : 0.00055f;

            Point rawCur = Cursor.Position;
            float cx = rawCur.X - _originX;
            float cy = rawCur.Y - _originY;

            PointF[] corners = new PointF[]
            {
                new PointF(0f, 0f),
                new PointF(_screenW, 0f),
                new PointF(0f, _screenH),
                new PointF(_screenW, _screenH)
            };

            for (int i = 0; i < 4; i++)
            {
                // Doğal buzlanma artışı
                if (_cornerFrost[i] < targetCap)
                    _cornerFrost[i] = Math.Min(targetCap, _cornerFrost[i] + growRate);
                else
                    _cornerFrost[i] = Math.Max(targetCap, _cornerFrost[i] - 0.0008f);

                // Fare imleci köşeye yaklaşınca buğu gibi erit!
                float mdx = cx - corners[i].X;
                float mdy = cy - corners[i].Y;
                float mDist = (float)Math.Sqrt(mdx * mdx + mdy * mdy);
                if (mDist < 225f && _cornerFrost[i] > 0.02f)
                {
                    float melt = (1f - mDist / 225f) * 0.026f;
                    _cornerFrost[i] = Math.Max(0f, _cornerFrost[i] - melt);
                }

                // Yakındaki kamp ateşi köşe buzunu eritir
                for (int k = 0; k < _campfires.Count; k++)
                {
                    float fdx = _campfires[k].X - corners[i].X;
                    float fdy = _campfires[k].Y - corners[i].Y;
                    if (fdx * fdx + fdy * fdy < 270f * 270f)
                        _cornerFrost[i] = Math.Max(0f, _cornerFrost[i] - 0.015f);
                }
            }
        }

        private void SpawnTrainSmoke(float x, float y, float vx, float vy, float radius)
        {
            for (int i = 0; i < _trainSmoke.Length; i++)
            {
                if (!_trainSmoke[i].Active)
                {
                    _trainSmoke[i].X = x;
                    _trainSmoke[i].Y = y;
                    _trainSmoke[i].Vx = vx;
                    _trainSmoke[i].Vy = vy;
                    _trainSmoke[i].Radius = radius;
                    _trainSmoke[i].Ttl = 44 + _rng.Next(24);
                    _trainSmoke[i].MaxTtl = _trainSmoke[i].Ttl;
                    _trainSmoke[i].Active = true;
                    return;
                }
            }
        }

        private void TootPolarExpressWhistle()
        {
            if (_trainWhistleCooldown > 0) return;
            _trainWhistleCooldown = 50;
            if (SoundEnabled) _audioSynth.PlayTrainWhistle();
            float stackX = _trainX + _trainDir * 72f;
            for (int i = 0; i < 6; i++)
            {
                SpawnTrainSmoke(
                    stackX + (float)(_rng.NextDouble() * 8.0 - 4.0),
                    _trainY - 34f - i * 3f,
                    -_trainDir * 0.4f + (float)(_rng.NextDouble() * 0.8 - 0.4),
                    -1.4f - (float)_rng.NextDouble() * 0.8f,
                    4.5f + i * 0.9f);
            }
            SpawnPopupText(stackX, _trainY - 54f, IsEnglish ? "🚂 CHOO CHOO!" : "🚂 ÇUF ÇUF!", Color.FromArgb(255, 253, 224, 71));
        }

        private void UpdatePolarExpressTrain(float windVx)
        {
            if (_trainWhistleCooldown > 0) _trainWhistleCooldown--;

            // Duman halkalarını güncelle
            for (int i = 0; i < _trainSmoke.Length; i++)
            {
                if (!_trainSmoke[i].Active) continue;
                _trainSmoke[i].X += _trainSmoke[i].Vx + windVx * 0.08f;
                _trainSmoke[i].Y += _trainSmoke[i].Vy;
                _trainSmoke[i].Radius += 0.14f;
                _trainSmoke[i].Ttl--;
                if (_trainSmoke[i].Ttl <= 0)
                    _trainSmoke[i].Active = false;
            }

            if (!TrainEnabled)
            {
                _trainVisible = false;
                return;
            }

            WindowSurface tb = FindTaskbarAtX(Math.Max(10f, Math.Min(_screenW - 10f, _trainX)));
            if (tb == null && _orderedSurfaces.Count > 0)
                tb = _orderedSurfaces[_orderedSurfaces.Count - 1];
            if (tb == null)
            {
                _trainVisible = false;
                return;
            }

            _trainVisible = true;
            _trainY = tb.Top;
            _trainX += _trainDir * 1.75f;
            _trainWheelPhase += 0.24f;

            // Bacadan düzenli beyaz buhar halkaları çıkar
            if (_frameCount % 7 == 0)
            {
                float stackX = _trainX + _trainDir * 72f;
                SpawnTrainSmoke(
                    stackX, _trainY - 34f,
                    -_trainDir * 0.55f,
                    -0.85f - (float)_rng.NextDouble() * 0.35f,
                    3.6f);
            }

            // Kartopu trene çarparsa düdük çalsın!
            for (int i = _snowballs.Count - 1; i >= 0; i--)
            {
                SnowballEntity sb = _snowballs[i];
                if (sb.Dragging) continue;
                float spd = (float)Math.Sqrt(sb.Vx * sb.Vx + sb.Vy * sb.Vy);
                if (spd > 3.2f && Math.Abs(sb.X - _trainX) <= 105f && Math.Abs((sb.Y - sb.BaseRadius) - (_trainY - 16f)) <= 24f)
                {
                    _snowballs.RemoveAt(i);
                    BurstSnowSparkles(sb.X, sb.Y - sb.BaseRadius, 14);
                    TootPolarExpressWhistle();
                    break;
                }
            }

            // Ekranın sonundan tamamen çıkınca öbür yöne dönüp tekrar girsin
            if (_trainDir > 0 && _trainX > _screenW + 220f)
                _trainDir = -1;
            else if (_trainDir < 0 && _trainX < -220f)
                _trainDir = 1;
        }

        private void UpdateAuroraAndShootingStars()
        {
            if (_auroraSurge > 0f)
                _auroraSurge = Math.Max(0f, _auroraSurge - 0.0045f);

            // Kayan yıldızlar (Kuzey ışıkları açıkken rastgele 15..36 sn aralıkla)
            if (AuroraEnabled && !PerformanceMode)
            {
                _shootingStarCooldown--;
                if (_shootingStarCooldown <= 0)
                {
                    _shootingStarCooldown = 60 * (15 + _rng.Next(22));
                    for (int i = 0; i < _shootingStars.Length; i++)
                    {
                        if (!_shootingStars[i].Active)
                        {
                            bool leftToRight = _rng.NextDouble() < 0.65;
                            float dir = leftToRight ? 1f : -1f;
                            float spd = 12f + (float)_rng.NextDouble() * 7f;
                            _shootingStars[i].X = leftToRight
                                ? (float)(_screenW * (0.08 + _rng.NextDouble() * 0.45))
                                : (float)(_screenW * (0.48 + _rng.NextDouble() * 0.44));
                            _shootingStars[i].Y = (float)(_screenH * (0.04 + _rng.NextDouble() * 0.16));
                            _shootingStars[i].Vx = dir * spd;
                            _shootingStars[i].Vy = 2.6f + (float)_rng.NextDouble() * 2.4f;
                            _shootingStars[i].TailLen = 68f + (float)_rng.NextDouble() * 55f;
                            _shootingStars[i].Ttl = 34 + _rng.Next(20);
                            _shootingStars[i].MaxTtl = _shootingStars[i].Ttl;
                            _shootingStars[i].Color = (_rng.NextDouble() < 0.5)
                                ? Color.FromArgb(255, 186, 230, 253)
                                : Color.FromArgb(255, 254, 249, 195);
                            _shootingStars[i].Active = true;
                            break;
                        }
                    }
                }
            }

            for (int i = 0; i < _shootingStars.Length; i++)
            {
                if (!_shootingStars[i].Active) continue;
                _shootingStars[i].X += _shootingStars[i].Vx;
                _shootingStars[i].Y += _shootingStars[i].Vy;
                _shootingStars[i].Ttl--;
                if (_shootingStars[i].Ttl <= 0 || _shootingStars[i].X < -180f || _shootingStars[i].X > _screenW + 180f)
                    _shootingStars[i].Active = false;
            }
        }

        private void StartSantaSleighPass()
        {
            _santaActive = true;
            if (SoundEnabled) _audioSynth.PlayJingleBells();
            _santaDir = (_rng.NextDouble() < 0.5) ? 1 : -1;
            _santaScale = 0.80f + (float)_rng.NextDouble() * 0.28f; // Uzaklık derinliği hissi
            _santaSpeed = (2.45f + (float)_rng.NextDouble() * 1.15f) * (0.85f + _santaScale * 0.25f);
            _santaX = (_santaDir > 0) ? -210f : (_screenW + 210f);
            _santaBaseY = _screenH * (0.10f + (float)_rng.NextDouble() * 0.18f);
            _santaY = _santaBaseY;
            _santaPrevY = _santaBaseY;
            _santaWavePhase = (float)(_rng.NextDouble() * Math.PI * 2.0);
            _santaGallopPhase = 0f;
            _santaHitCooldown = 0;

            // Geçiş sırasında rastgele 1-2 paraşütlü hediye bırakma noktası
            _santaGiftDropX1 = _screenW * (0.20f + (float)_rng.NextDouble() * 0.28f);
            _santaGiftDropX2 = _screenW * (0.54f + (float)_rng.NextDouble() * 0.28f);
            _santaDroppedGift1 = false;
            _santaDroppedGift2 = (_rng.NextDouble() < 0.45); // %55 ihtimalle 2 hediye, %45 ihtimalle 1 hediye
        }

        private void SpawnSantaSparkle(float x, float y, float vx, float vy)
        {
            for (int i = 0; i < _santaSparkles.Length; i++)
            {
                if (!_santaSparkles[i].Active)
                {
                    _santaSparkles[i].X = x;
                    _santaSparkles[i].Y = y;
                    _santaSparkles[i].Vx = vx;
                    _santaSparkles[i].Vy = vy;
                    _santaSparkles[i].Size = 2.6f + (float)_rng.NextDouble() * 3.0f;
                    double r = _rng.NextDouble();
                    _santaSparkles[i].Color = (r < 0.55)
                        ? Color.FromArgb(255, 253, 224, 71)   // Altın
                        : (r < 0.82 ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 125, 211, 252)); // Beyaz / Buz
                    _santaSparkles[i].Ttl = 30 + _rng.Next(26);
                    _santaSparkles[i].MaxTtl = _santaSparkles[i].Ttl;
                    _santaSparkles[i].Active = true;
                    return;
                }
            }
        }

        private void UpdateSantaSleighPhysics(float windVx)
        {
            // Yıldız tozu izini her zaman güncelle
            for (int i = 0; i < _santaSparkles.Length; i++)
            {
                if (!_santaSparkles[i].Active) continue;
                _santaSparkles[i].X += _santaSparkles[i].Vx + windVx * 0.04f;
                _santaSparkles[i].Y += _santaSparkles[i].Vy;
                _santaSparkles[i].Vy += 0.025f;
                _santaSparkles[i].Vx *= 0.97f;
                _santaSparkles[i].Ttl--;
                if (_santaSparkles[i].Ttl <= 0)
                    _santaSparkles[i].Active = false;
            }

            if (_santaHitCooldown > 0) _santaHitCooldown--;

            if (!_santaActive)
            {
                if (!SantaEnabled) return;
                _santaCooldownTicks--;
                if (_santaCooldownTicks <= 0)
                {
                    StartSantaSleighPass();
                }
                return;
            }

            // Aktif uçuş hareketi
            _santaPrevY = _santaY;
            _santaX += _santaDir * _santaSpeed;
            _santaWavePhase += 0.036f;
            _santaGallopPhase += 0.24f;
            _santaY = _santaBaseY + (float)Math.Sin(_santaWavePhase) * 24f * _santaScale;

            // Kızak arkasından sihirli altın yıldız tozu dök
            if (!PerformanceMode && (_frameCount % 2 == 0))
            {
                float tailX = _santaX - _santaDir * 68f * _santaScale;
                float tailY = _santaY + 8f * _santaScale + (float)(_rng.NextDouble() * 14.0 - 7.0);
                SpawnSantaSparkle(
                    tailX, tailY,
                    -_santaDir * (0.4f + (float)_rng.NextDouble() * 0.8f),
                    0.2f + (float)_rng.NextDouble() * 0.6f);
            }

            // Rastgele noktalarda paraşütlü sürpriz hediye bırak (sahnede en fazla 4 hediye olsun)
            if (_gifts.Count < 4)
            {
                float sleighWorldX = _santaX - _santaDir * 48f * _santaScale;
                if (!_santaDroppedGift1 &&
                    ((_santaDir > 0 && sleighWorldX >= _santaGiftDropX1) ||
                     (_santaDir < 0 && sleighWorldX <= _santaGiftDropX1)))
                {
                    _santaDroppedGift1 = true;
                    _gifts.Add(new GiftBoxEntity(sleighWorldX, _santaY + 14f * _santaScale, _rng));
                    SpawnPopupText(sleighWorldX, _santaY - 26f * _santaScale, "🎅 HO HO HO!", Color.FromArgb(255, 253, 224, 71));
                }
                else if (!_santaDroppedGift2 &&
                    ((_santaDir > 0 && sleighWorldX >= _santaGiftDropX2) ||
                     (_santaDir < 0 && sleighWorldX <= _santaGiftDropX2)))
                {
                    _santaDroppedGift2 = true;
                    _gifts.Add(new GiftBoxEntity(sleighWorldX, _santaY + 14f * _santaScale, _rng));
                }
            }

            // Etkileşim sürprizi: Atılan kartopu Noel Baba'nın kızağına çarparsa kahkaha atıp ekstra hediye bırakır!
            if (_santaHitCooldown == 0)
            {
                for (int i = _snowballs.Count - 1; i >= 0; i--)
                {
                    SnowballEntity sb = _snowballs[i];
                    if (sb.Dragging) continue;
                    float spd = (float)Math.Sqrt(sb.Vx * sb.Vx + sb.Vy * sb.Vy);
                    if (spd > 3.5f &&
                        Math.Abs(sb.X - _santaX) <= 105f * _santaScale &&
                        Math.Abs((sb.Y - sb.BaseRadius) - _santaY) <= 32f * _santaScale)
                    {
                        _santaHitCooldown = 45;
                        _snowballs.RemoveAt(i);
                        BurstSnowSparkles(_santaX, _santaY, 26);
                        SpawnPopupText(_santaX, _santaY - 32f, "🎅 HO HO HO! 🎁", Color.FromArgb(255, 253, 224, 71));
                        if (_gifts.Count < 5)
                            _gifts.Add(new GiftBoxEntity(_santaX, _santaY + 12f, _rng));
                        break;
                    }
                }
            }

            // Ekrandan tamamen çıkınca bir sonraki rastgele süre için bekleme sayacını kur (45..135 sn)
            if ((_santaDir > 0 && _santaX > _screenW + 250f) ||
                (_santaDir < 0 && _santaX < -250f))
            {
                _santaActive = false;
                _santaCooldownTicks = 60 * (45 + _rng.Next(91));
            }
        }

        private void UpdateGiftsPhysics(float windVx)
        {
            for (int i = _gifts.Count - 1; i >= 0; i--)
            {
                GiftBoxEntity g = _gifts[i];
                g.LifeTicks--;
                g.PulsePhase += 0.08f;
                if (g.LifeTicks <= 0)
                {
                    BurstSnowSparkles(g.X, g.Y - g.Size * 0.5f, 10);
                    _gifts.RemoveAt(i);
                    continue;
                }

                // Atılan bir kartopu hediye kutusuna çarparsa kutuyu patlatarak aç!
                bool openedByBall = false;
                for (int bIdx = _snowballs.Count - 1; bIdx >= 0; bIdx--)
                {
                    SnowballEntity sb = _snowballs[bIdx];
                    if (sb.Dragging) continue;
                    float spd = (float)Math.Sqrt(sb.Vx * sb.Vx + sb.Vy * sb.Vy);
                    if (spd > 2.8f &&
                        Math.Abs(sb.X - g.X) <= sb.BaseRadius + g.Size * 0.8f &&
                        Math.Abs((sb.Y - sb.BaseRadius) - (g.Y - g.Size * 0.5f)) <= sb.BaseRadius + g.Size * 0.8f)
                    {
                        _snowballs.RemoveAt(bIdx);
                        OpenGiftBox(g);
                        openedByBall = true;
                        break;
                    }
                }
                if (openedByBall) continue;

                WindowSurface attached;
                if (g.SurfaceHwnd != IntPtr.Zero && _surfaces.TryGetValue(g.SurfaceHwnd, out attached) &&
                    !attached.IsMaximized && (attached.IsTaskbar || !IsOccludedByFrontWindow(g.X, attached.Top, attached.ZOrder)))
                {
                    g.X = Math.Max(attached.Left + 18f, Math.Min(attached.Right - 18f, g.X));
                    g.Y = attached.Top;
                    g.HasParachute = false;
                }
                else
                {
                    g.SurfaceHwnd = IntPtr.Zero;
                    float prevY = g.Y;
                    if (g.HasParachute)
                    {
                        g.SwayPhase += 0.055f;
                        g.X += (float)Math.Sin(g.SwayPhase) * 0.95f + windVx * 0.14f;
                        g.Y += g.Vy;
                    }
                    else
                    {
                        g.Vy = Math.Min(12f, g.Vy + 0.42f);
                        g.Y += g.Vy;
                    }
                    g.X = Math.Max(28f, Math.Min(_screenW - 28f, g.X));

                    // Açık pencerelerin üstüne veya görev çubuğuna yumuşak iniş
                    for (int sIdx = 0; sIdx < _orderedSurfaces.Count; sIdx++)
                    {
                        WindowSurface s = _orderedSurfaces[sIdx];
                        if (s.IsMaximized) continue;
                        if (g.X >= s.Left + 18 && g.X <= s.Right - 18 && prevY <= s.Top + 8f && g.Y >= s.Top)
                        {
                            if (s.IsTaskbar || !IsOccludedByFrontWindow(g.X, s.Top, s.ZOrder))
                            {
                                g.SurfaceHwnd = s.Hwnd;
                                g.Y = s.Top;
                                g.Vy = 0f;
                                g.HasParachute = false;
                                BurstSnowSparkles(g.X, g.Y - 8f, 10);
                                break;
                            }
                        }
                    }

                    if (g.SurfaceHwnd == IntPtr.Zero)
                    {
                        WindowSurface tb = FindTaskbarAtX(g.X);
                        if (tb != null && g.Y >= tb.Top)
                        {
                            g.SurfaceHwnd = tb.Hwnd;
                            g.Y = tb.Top;
                            g.Vy = 0f;
                            g.HasParachute = false;
                        }
                    }
                }
            }
        }

        private void UpdateFireworksPhysics(float windVx)
        {
            // Koreografili sürpriz gösterisi roket fırlatıcı
            if (_fireworkShowRemaining > 0)
            {
                _fireworkShowTimer--;
                if (_fireworkShowTimer <= 0)
                {
                    _fireworkShowRemaining--;
                    _fireworkShowTimer = 14 + _rng.Next(14);
                    float tx = _screenW * (0.14f + (float)_rng.NextDouble() * 0.72f);
                    float ty = _screenH * (0.12f + (float)_rng.NextDouble() * 0.28f);
                    LaunchFireworkAt(tx, ty);
                }
            }

            // Yükselen roketler
            for (int i = 0; i < _rockets.Length; i++)
            {
                if (!_rockets[i].Active) continue;
                _rockets[i].X += _rockets[i].Vx;
                _rockets[i].Y += _rockets[i].Vy;

                if (_frameCount % 2 == 0)
                {
                    SpawnFireworkSpark(
                        _rockets[i].X, _rockets[i].Y,
                        (float)(_rng.NextDouble() * 0.8 - 0.4),
                        0.8f + (float)_rng.NextDouble() * 0.8f,
                        0.92f, 0.04f, 2.0f,
                        Color.FromArgb(255, 254, 240, 138), 14, false);
                }

                if (_rockets[i].Y <= _rockets[i].TargetY)
                {
                    _rockets[i].Active = false;
                    ExplodeFirework(
                        _rockets[i].X, _rockets[i].Y,
                        _rockets[i].PrimaryColor, _rockets[i].SecondaryColor,
                        _rockets[i].Pattern);
                }
            }

            // Patlayan havai fişek kıvılcımları
            for (int i = 0; i < _sparks.Length; i++)
            {
                if (!_sparks[i].Active) continue;
                _sparks[i].PrevX = _sparks[i].X;
                _sparks[i].PrevY = _sparks[i].Y;
                _sparks[i].Vx = _sparks[i].Vx * _sparks[i].Drag + windVx * 0.008f;
                _sparks[i].Vy = _sparks[i].Vy * _sparks[i].Drag + _sparks[i].Gravity;
                _sparks[i].X += _sparks[i].Vx;
                _sparks[i].Y += _sparks[i].Vy;
                _sparks[i].Ttl--;
                if (_sparks[i].Ttl <= 0)
                    _sparks[i].Active = false;
            }
        }

        private bool TryFindCableCarWindows(out WindowSurface leftWin, out WindowSurface rightWin)
        {
            leftWin = null;
            rightWin = null;
            if (!CableCarEnabled) return false;

            // Önde ve görünür olan (maksimize veya görev çubuğu olmayan) en üstteki 2 uygulama penceresini seç
            WindowSurface first = null;
            WindowSurface second = null;

            for (int i = 0; i < _orderedSurfaces.Count; i++)
            {
                WindowSurface s = _orderedSurfaces[i];
                if (s.IsTaskbar || s.IsMaximized || s.Width < 160) continue;

                // Pencerenin üst kenarının ortası başka bir pencere tarafından kapatılmış mı?
                float midX = s.Left + s.Width * 0.5f;
                if (IsOccludedByFrontWindow(midX, s.Top, s.ZOrder)) continue;

                if (first == null)
                    first = s;
                else
                {
                    second = s;
                    break;
                }
            }

            if (first == null || second == null) return false;

            float c1 = first.Left + first.Width * 0.5f;
            float c2 = second.Left + second.Width * 0.5f;
            if (c1 <= c2)
            {
                leftWin = first;
                rightWin = second;
            }
            else
            {
                leftWin = second;
                rightWin = first;
            }
            return true;
        }

        private void ComputeCableAnchors(WindowSurface leftWin, WindowSurface rightWin, out float ax, out float ay, out float bx, out float by)
        {
            // İki pencerenin birbirine bakan iç kenarlarına yakın kule noktaları seçilir
            float lInner = leftWin.Right - Math.Min(65f, leftWin.Width * 0.24f);
            float rInner = rightWin.Left + Math.Min(65f, rightWin.Width * 0.24f);

            if (rInner - lInner >= 70f)
            {
                ax = lInner;
                bx = rInner;
            }
            else
            {
                // Pencereler üst üste / dikey hizalıysa sol pencerenin soluna, sağ pencerenin sağına kur
                ax = leftWin.Left + Math.Min(65f, leftWin.Width * 0.25f);
                bx = rightWin.Right - Math.Min(65f, rightWin.Width * 0.25f);
                if (Math.Abs(bx - ax) < 60f)
                {
                    ax = leftWin.Left + 35f;
                    bx = rightWin.Right - 35f;
                }
            }
            ay = leftWin.Top;
            by = rightWin.Top;
        }

        private static PointF EvaluateCableCatenary(float p0x, float p0y, float p1x, float p1y, float t, float sag)
        {
            float x = p0x + (p1x - p0x) * t;
            float y = p0y + (p1y - p0y) * t + 4f * sag * t * (1f - t);
            return new PointF(x, y);
        }

        private void UpdateCableCarPhysics(float windVx)
        {
            WindowSurface leftWin, rightWin;
            if (!TryFindCableCarWindows(out leftWin, out rightWin))
            {
                _cableCarHasValidSpan = false;
                return;
            }

            float ax, ay, bx, by;
            ComputeCableAnchors(leftWin, rightWin, out ax, out ay, out bx, out by);

            // Direk noktaları başka bir pencere tarafından kapatıldıysa çizme
            if (IsOccludedByFrontWindow(ax, ay, leftWin.ZOrder) || IsOccludedByFrontWindow(bx, by, rightWin.ZOrder))
            {
                _cableCarHasValidSpan = false;
                return;
            }

            float prevAx = _cableAx, prevBx = _cableBx;
            _cableAx = ax; _cableAy = ay;
            _cableBx = bx; _cableBy = by;
            _cableCarHasValidSpan = true;

            float spanDist = (float)Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            if (spanDist < 40f)
            {
                _cableCarHasValidSpan = false;
                return;
            }

            // Pencereler hareket ettirildiğinde kabin eylemsizlikle sallanır
            float anchorMoveDx = (ax - prevAx) * (1f - _cableCarT) + (bx - prevBx) * _cableCarT;
            if (Math.Abs(anchorMoveDx) > 0.5f && Math.Abs(anchorMoveDx) < 80f)
                _cableCarSwingVel -= anchorMoveDx * 0.35f;

            if (_cableCarWaitTicks > 0)
            {
                _cableCarWaitTicks--;
            }
            else
            {
                // İstasyonlara yaklaşırken yumuşak yavaşlama / hızlanma (smoothstep)
                float edgeDist = Math.Min(_cableCarT, 1f - _cableCarT);
                float speedFactor = 0.38f + Math.Min(1f, edgeDist * 5.5f) * 0.62f;
                float step = (2.1f * speedFactor) / Math.Max(140f, spanDist);

                _cableCarT += _cableCarDir * step;
                _cableWheelAngle += _cableCarDir * 5.5f * speedFactor;

                if (_cableCarT >= 1f)
                {
                    _cableCarT = 1f;
                    _cableCarDir = -1;
                    _cableCarWaitTicks = 38; // İstasyonda kısa mola
                    _cableCarSwingVel += 3.2f;
                }
                else if (_cableCarT <= 0f)
                {
                    _cableCarT = 0f;
                    _cableCarDir = 1;
                    _cableCarWaitTicks = 38;
                    _cableCarSwingVel -= 3.2f;
                }
            }

            // Rüzgar ve sarkaç dinamiği
            float targetSwing = Math.Max(-22f, Math.Min(22f, windVx * 1.65f));
            _cableCarSwingVel += (targetSwing - _cableCarSwingAngle) * 0.10f;
            _cableCarSwingVel *= 0.88f;
            _cableCarSwingAngle = Math.Max(-42f, Math.Min(42f, _cableCarSwingAngle + _cableCarSwingVel));

            // Kartopu teleferik kabinine çarparsa kabin şiddetle sallanır ve çatısından kar dökülür!
            float towerH = 28f;
            float sag = Math.Min(48f, 12f + spanDist * 0.055f);
            PointF trolleyPt = EvaluateCableCatenary(ax, ay - towerH, bx, by - towerH, _cableCarT, sag);
            float cabinCx = trolleyPt.X;
            float cabinCy = trolleyPt.Y + 20f;

            for (int i = _snowballs.Count - 1; i >= 0; i--)
            {
                SnowballEntity sb = _snowballs[i];
                if (sb.Dragging) continue;
                float spd = (float)Math.Sqrt(sb.Vx * sb.Vx + sb.Vy * sb.Vy);
                if (spd > 3.2f && Math.Abs(sb.X - cabinCx) <= sb.BaseRadius + 18f && Math.Abs((sb.Y - sb.BaseRadius) - cabinCy) <= sb.BaseRadius + 15f)
                {
                    _cableCarSwingVel += Math.Max(-18f, Math.Min(18f, sb.Vx * 1.8f));
                    SpawnPopupText(cabinCx, cabinCy - 24f, IsEnglish ? "🚡 DING!" : "🚡 ÇUF!", Color.FromArgb(255, 253, 224, 71));
                    BurstSnowSparkles(cabinCx, cabinCy, 18);
                    _snowballs.RemoveAt(i);
                    break;
                }
            }
        }

        private void UpdateCampfiresAndPopups(float windVx)
        {
            for (int i = _campfires.Count - 1; i >= 0; i--)
            {
                CampfireEntity cf = _campfires[i];
                cf.FlamePhase += 0.22f;
                if (cf.Dragging) continue;

                WindowSurface attached;
                if (cf.SurfaceHwnd != IntPtr.Zero && _surfaces.TryGetValue(cf.SurfaceHwnd, out attached) &&
                    !attached.IsMaximized && (attached.IsTaskbar || !IsOccludedByFrontWindow(cf.X, attached.Top, attached.ZOrder)))
                {
                    cf.X = Math.Max(attached.Left + 20f, Math.Min(attached.Right - 20f, cf.X + cf.Vx));
                    cf.Vx *= 0.82f;
                    cf.Y = attached.Top;
                    cf.Vy = 0f;

                    // Kamp ateşi etrafındaki (55px yarıçap) karları ısıtarak yavaşça eritir!
                    attached.HarvestOrPushSnow(cf.X, 48, 0.35f, 0, 0f);
                }
                else
                {
                    float prevY = cf.Y;
                    cf.Vy = Math.Min(16f, cf.Vy + 0.50f);
                    cf.X += cf.Vx;
                    cf.Y += cf.Vy;

                    if (cf.Vy > 0f)
                    {
                        for (int sIdx = 0; sIdx < _orderedSurfaces.Count; sIdx++)
                        {
                            WindowSurface s = _orderedSurfaces[sIdx];
                            if (s.IsMaximized) continue;
                            if (cf.X >= s.Left + 14 && cf.X <= s.Right - 14 && prevY <= s.Top + 14f && cf.Y >= s.Top)
                            {
                                if (s.IsTaskbar || !IsOccludedByFrontWindow(cf.X, s.Top, s.ZOrder))
                                {
                                    cf.SurfaceHwnd = s.Hwnd;
                                    cf.Y = s.Top;
                                    cf.Vy = 0f;
                                    cf.Vx *= 0.4f;
                                    break;
                                }
                            }
                        }
                    }

                    WindowSurface tb = FindTaskbarAtX(cf.X);
                    if (tb != null && cf.Y >= tb.Top)
                    {
                        cf.SurfaceHwnd = tb.Hwnd;
                        cf.Y = tb.Top;
                        cf.Vy = 0f;
                    }
                }

                // Ateşten yükselen sıcak kıvılcımlar
                if (!PerformanceMode && (_frameCount % 4 == 0))
                {
                    SpawnChunk(
                        cf.X + (float)(_rng.NextDouble() * 10.0 - 5.0),
                        cf.Y - 14f,
                        windVx * 0.18f + (float)(_rng.NextDouble() * 1.2 - 0.6),
                        -1.6f - (float)_rng.NextDouble() * 1.4f,
                        2.6f, 0f, 24 + _rng.Next(16), false,
                        Color.FromArgb(255, 253, 224, 71));
                }

                // Kartopu ateşe çarparsa ateşi söndürür!
                for (int bIdx = _snowballs.Count - 1; bIdx >= 0; bIdx--)
                {
                    SnowballEntity sb = _snowballs[bIdx];
                    if (!sb.Dragging && Math.Abs(sb.X - cf.X) <= sb.BaseRadius + 14f && Math.Abs(sb.Y - cf.Y) <= 22f)
                    {
                        float spd = (float)Math.Sqrt(sb.Vx * sb.Vx + sb.Vy * sb.Vy);
                        if (spd > 3.5f)
                        {
                            SpawnPopupText(cf.X, cf.Y - 22f, IsEnglish ? "💨 DOUSED!" : "💨 SÖNDÜ!", Color.FromArgb(255, 186, 230, 253));
                            BurstSnowSparkles(cf.X, cf.Y - 10f, 20);
                            _snowballs.RemoveAt(bIdx);
                            _campfires.RemoveAt(i);
                            break;
                        }
                    }
                }
            }

            for (int i = 0; i < _popups.Length; i++)
            {
                if (!_popups[i].Active) continue;
                _popups[i].Y += _popups[i].Vy;
                _popups[i].Ttl--;
                if (_popups[i].Ttl <= 0)
                    _popups[i].Active = false;
            }
        }

        private void DumpPlowMoundAtEdge(SnowPlowEntity plow, float edgeX, float edgeY, int dir)
        {
            // Köşeye sıkışmış kartopu veya kardan adam kalmışsa anında yok et
            for (int i = _snowballs.Count - 1; i >= 0; i--)
            {
                SnowballEntity sb = _snowballs[i];
                if (!sb.Dragging && Math.Abs(sb.X - edgeX) <= 95f && Math.Abs(sb.Y - edgeY) <= 40f)
                {
                    BurstSnowSparkles(sb.X, sb.Y - sb.BaseRadius, 20);
                    _snowballs.RemoveAt(i);
                }
            }
            for (int i = _snowmen.Count - 1; i >= 0; i--)
            {
                SnowmanEntity sm = _snowmen[i];
                if (!sm.Dragging && Math.Abs(sm.X - edgeX) <= 105f && Math.Abs(sm.Y - edgeY) <= 45f)
                {
                    BurstSnowSparkles(sm.X, sm.Y - sm.R1 - sm.R2, 32);
                    _snowmen.RemoveAt(i);
                }
            }

            int burstCount = (int)Math.Min(28, 6 + plow.PlowedSnowMass * 0.25f);
            for (int i = 0; i < burstCount; i++)
            {
                SpawnChunk(
                    edgeX,
                    edgeY - 6f,
                    dir * (1.5f + (float)_rng.NextDouble() * 4.0f),
                    -2.5f - (float)_rng.NextDouble() * 3.0f,
                    3.2f + (float)_rng.NextDouble() * 2.8f,
                    1.6f,
                    75 + _rng.Next(30),
                    true,
                    Color.White);
            }
            plow.PlowedSnowMass = 2f;
        }

        // ====================================================================
        // 9. YÜKSEK KALİTELİ GÖRSEL ÇİZİM MOTORU (PER-PIXEL ALPHA + SPLINE)
        // ====================================================================
        private void RenderFrameToLayeredWindow()
        {
            Graphics g = _surfaceGraphics;
            g.Clear(Color.Transparent);

            // 0a. Arka Plan Kuzey Işıkları (Aurora Borealis) ve Kayan Yıldızlar
            if (AuroraEnabled && !PerformanceMode)
            {
                DrawAuroraBorealis(g);
                DrawShootingStars(g);
            }

            // 0b. Arka Planda Rastgele Geçen Noel Baba ve Ren Geyikleri (🎅🦌)
            DrawSantaSleighAndTrail(g);

            // 1. Pencerelerin Üstündeki Pürüzsüz Spline Kar Yığınları ve Sallanan Kış Işıkları
            //    Önde bulunan pencereler (örn. sonradan açılan Spotify) ExcludeClip ile maskelenir;
            //    böylece arkadaki pencerenin karı veya ışıkları ASLA öndeki pencerenin içinde havada görünmez!
            for (int sIdx = 0; sIdx < _orderedSurfaces.Count; sIdx++)
            {
                WindowSurface surf = _orderedSurfaces[sIdx];
                if (surf.IsMaximized) continue; // Maksimize pencerenin (örn. Opera GX) kendi tepesinde kar/ışık çizilmez
                int n = surf.Heights.Length;
                if (n < 6) continue;

                // Görev çubuğu (IsTaskbar) her zaman görünürdür; diğer pencereler ise öndeki pencere (örn. Opera GX) tarafından örtülürse gizlenir!
                if (!surf.IsTaskbar)
                {
                    bool completelyCovered = false;
                    for (int fIdx = 0; fIdx < sIdx; fIdx++)
                    {
                        WindowSurface front = _orderedSurfaces[fIdx];
                        if (!front.IsTaskbar &&
                            front.Left <= surf.Left && front.Right >= surf.Right &&
                            front.Top <= surf.Top - 45 && front.Bottom >= surf.Top + 30)
                        {
                            completelyCovered = true;
                            break;
                        }
                    }
                    if (completelyCovered) continue;
                }

                GraphicsState clipState = g.Save();
                if (!surf.IsTaskbar)
                {
                    for (int fIdx = 0; fIdx < sIdx; fIdx++)
                    {
                        WindowSurface front = _orderedSurfaces[fIdx];
                        if (!front.IsTaskbar)
                        {
                            g.ExcludeClip(new Rectangle(front.Left, front.Top, front.Width, Math.Max(1, front.Bottom - front.Top)));
                        }
                    }
                }

                if (IciclesEnabled && !surf.IsTaskbar && surf.Icicles != null && surf.Width >= 160)
                    DrawWindowIcicles(g, surf);

                if (FairyLightsEnabled && !surf.IsTaskbar && surf.Width >= 200)
                    DrawWindowFairyLights(g, surf);

                float maxH = surf.MaxH;
                if (maxH > 0.5f)
                {
                    // Her 6 pikselde bir spline kontrol noktası (tampon her karede yeniden kullanılır)
                    const int step = 3;
                    int ctrl = (n + step - 1) / step;
                    int len = ctrl + 2;
                    if (surf.CurveBuf == null || surf.CurveBuf.Length != len)
                        surf.CurveBuf = new PointF[len];

                    PointF[] curvePts = surf.CurveBuf;
                    curvePts[0] = new PointF(surf.Left, surf.Top + 2f);
                    int k = 1;
                    for (int i = 0; i < n; i += step, k++)
                        curvePts[k] = new PointF(surf.Left + i * WindowSurface.BinSize, surf.Top - surf.Heights[i]);
                    curvePts[len - 1] = new PointF(surf.Right, surf.Top + 2f);

                    using (GraphicsPath path = new GraphicsPath())
                    {
                        path.AddLine(surf.Left, surf.Top + 3f, curvePts[0].X, curvePts[0].Y);
                        path.AddCurve(curvePts, 0.38f);
                        path.AddLine(curvePts[len - 1].X, curvePts[len - 1].Y, surf.Right, surf.Top + 3f);
                        path.CloseFigure();

                        RectangleF gradRect = new RectangleF(
                            surf.Left, surf.Top - maxH - 6f,
                            surf.Width, maxH + 14f);
                        using (LinearGradientBrush snowBrush = new LinearGradientBrush(
                            gradRect,
                            Color.FromArgb(255, 255, 255, 255),
                            Color.FromArgb(238, 186, 218, 252),
                            LinearGradientMode.Vertical))
                        {
                            g.FillPath(snowBrush, path);
                        }
                    }

                    // Üst parlak kar sırtı çizgisi
                    g.DrawCurve(_snowHighlightPen, curvePts, 0.38f);
                }

                g.Restore(clipState);
            }

            // 2. Tekil veya 2'li İstiflenmiş Kartopları (Snowballs)
            for (int i = 0; i < _snowballs.Count; i++)
                DrawSnowballEntity(g, _snowballs[i]);

            // 2a. İki Uygulama Arası Teleferik Hattı ve Kabini (🚡)
            if (CableCarEnabled && _cableCarHasValidSpan)
                DrawCableCarSystem(g);

            // 2b. Kamp Ateşleri (🔥)
            for (int i = 0; i < _campfires.Count; i++)
                DrawCampfire(g, _campfires[i]);

            // 2c-0. Gökyüzüne Süzülen Sıcak Dilek Fenerleri (🏮)
            if (LanternsEnabled)
                DrawWishLanterns(g);

            // 2c. Noel Baba'nın Bıraktığı Sürpriz Paraşütlü Hediyeler (🎁)
            for (int i = 0; i < _gifts.Count; i++)
                DrawGiftBox(g, _gifts[i]);

            // 2c-1. Özelleştirilebilir Yılbaşı Ağaçları (🎄)
            ChristmasTreeEntity activeTreeTarget = (_treeDecoratorForm != null && !_treeDecoratorForm.IsDisposed) ? _treeDecoratorForm.TargetTree : null;
            for (int i = 0; i < _trees.Count; i++)
            {
                ChristmasTreeEntity tr = _trees[i];
                TreeRenderer.DrawTree(g, tr, tr.X, tr.Y, 1.0f, _time, _audioLevelSmooth, _audioBeat, tr == activeTreeTarget);
            }

            // 2d. Kutup Ekspresi Görev Çubuğu Treni (🚂)
            if (TrainEnabled && _trainVisible)
                DrawPolarExpressTrain(g);

            // 2e. Paytak Masaüstü Penguenleri (🐧)
            for (int i = 0; i < _penguins.Count; i++)
                DrawPenguin(g, _penguins[i]);

            // 2f. Pencere & Ağaç Kış Kuşları (🐦 Kızılgerdan & Kar Baykuşu)
            for (int i = 0; i < _birds.Count; i++)
                DrawWinterBird(g, _birds[i]);

            // 3. 3 Kartopuyla Oluşturulmuş / Giydirilmiş Kardan Adamlar (⛄)
            Point rawCur = Cursor.Position;
            _lastCursorPos = new Point(rawCur.X - _originX, rawCur.Y - _originY);
            SnowmanEntity activeWardrobeTarget = (_wardrobeForm != null && !_wardrobeForm.IsDisposed) ? _wardrobeForm.TargetSnowman : null;
            for (int i = 0; i < _snowmen.Count; i++)
                DrawSnowman(g, _snowmen[i], _snowmen[i] == activeWardrobeTarget);

            // 3b. Havada Uçan Şapkalar ve Düşen Buz Sarkıtları
            for (int i = 0; i < _flyingHats.Length; i++)
            {
                if (_flyingHats[i].Active)
                    DrawFlyingHat(g, _flyingHats[i]);
            }
            for (int i = 0; i < _fallingIcicles.Length; i++)
            {
                if (_fallingIcicles[i].Active)
                    DrawSingleIcicle(g, _fallingIcicles[i].X, _fallingIcicles[i].Y, _fallingIcicles[i].Width, _fallingIcicles[i].Length);
            }

            // 4. Sarı Kar Küreme Araçları ve Önündeki Dev Kar Yığını (🚜)
            for (int i = 0; i < _plows.Count; i++)
                DrawSnowPlowWithFrontSnowMound(g, _plows[i]);

            // 5. Fiziksel Kar Parçaları / Küreme Fıskiyesi
            for (int i = 0; i < MaxChunks; i++)
            {
                if (!_chunks[i].Active) continue;
                PhysicalSnowChunk ch = _chunks[i];
                byte a = (byte)Math.Max(25, Math.Min(250, (ch.Ttl * 250) / Math.Max(1, ch.MaxTtl)));
                float r = ch.Size * 0.5f;
                if (ch.Tint.R == 255 && ch.Tint.G == 255 && ch.Tint.B == 255)
                {
                    g.FillEllipse(_alphaWhiteBrushes[a], ch.X - r, ch.Y - r, ch.Size, ch.Size);
                }
                else if (ch.Tint.R == 253 && ch.Tint.G == 224 && ch.Tint.B == 71)
                {
                    g.FillEllipse(_alphaYellowBrushes[a], ch.X - r, ch.Y - r, ch.Size, ch.Size);
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(a, ch.Tint.R, ch.Tint.G, ch.Tint.B)))
                        g.FillEllipse(b, ch.X - r, ch.Y - r, ch.Size, ch.Size);
                }
            }

            // 6. Kar Taneleri (Uygulama İçi Şeffaflık Çarpanı ile)
            int activeCount = GetActiveFlakeCount();
            bool checkInterior = WindowInteriorSnowOpacity < 0.99f;
            for (int i = 0; i < activeCount; i++)
            {
                Snowflake f = _flakes[i];
                float alphaMult = 1.0f;

                if (checkInterior && IsPointInsideAnyApplicationWindow(f.X, f.Y))
                {
                    alphaMult = WindowInteriorSnowOpacity;
                }

                int effAlpha = (int)(f.BaseAlpha * alphaMult);
                if (effAlpha <= 3) continue;
                byte a = (byte)Math.Min(255, effAlpha);

                if (f.Layer == 2 && effAlpha > 40 && !PerformanceMode)
                {
                    // Ön plan kristal kar tanesi + yumuşak ışık halesi
                    byte glowA = (byte)(a / 4);
                    float gr = f.Size * 1.15f;
                    g.FillEllipse(_alphaIceBrushes[glowA], f.X - gr, f.Y - gr, gr * 2f, gr * 2f);
                }

                float r = f.Size * 0.5f;
                g.FillEllipse(_alphaWhiteBrushes[a], f.X - r, f.Y - r, f.Size, f.Size);
            }

            // 6a. Sürpriz Havai Fişekler & Kutlama Konfetileri (🎆)
            DrawFireworks(g);
            DrawConfetti(g);

            // 6a-2. Fare İmleci Kış Işıltısı (✨)
            DrawMagicSparkles(g);

            // 6b. Vuruş & Sürpriz Efekt Yazıları (🎯 HEADSHOT!, 🎅 HO HO HO!)
            for (int i = 0; i < _popups.Length; i++)
            {
                if (_popups[i].Active)
                    DrawFloatingPopup(g, _popups[i]);
            }

            // 6c. Tipi (Blizzard) Modunda Atmosferik Hızlı Rüzgar Çizgileri
            if (!PerformanceMode && (BlizzardMode || Math.Abs(WindNx) > 0.68f))
                DrawBlizzardWindStreaks(g);

            // 6d. Ekran 4 Köşesinde İnteraktif Buzlanma / Kırağı Kristalleri (❄️)
            if (!PerformanceMode)
                DrawCornerFrost(g);

            // 6e. Yılbaşı Geri Sayım Widget'ı, Gece Yarısı Kutlama Pankartı & Bildirim Toast'u
            if (CountdownEnabled)
                DrawCountdownWidget(g);
            if (_celebrationBannerTicks > 0)
                DrawCelebrationBanner(g);
            if (_toastTicks > 0 && !string.IsNullOrEmpty(_toastText))
                DrawToastNotification(g);

            // 7. UpdateLayeredWindow ile ekrana bas
            PresentFrame();
        }

        private void DrawWishLanterns(Graphics g)
        {
            for (int i = 0; i < _lanterns.Length; i++)
            {
                if (!_lanterns[i].Active) continue;
                WishLantern l = _lanterns[i];
                float sc = l.Scale;
                float flicker = 0.86f + 0.14f * (float)Math.Sin(l.FlickerPhase);

                Color topCol = l.WarmHue == 0
                    ? Color.FromArgb(225, 234, 88, 12)
                    : (l.WarmHue == 1 ? Color.FromArgb(225, 245, 158, 11) : Color.FromArgb(225, 244, 63, 94));
                Color botCol = Color.FromArgb(245, 254, 240, 138);

                GraphicsState st = g.Save();
                g.TranslateTransform(l.X, l.Y);
                g.RotateTransform((float)Math.Sin(l.SwayPhase) * 6.5f);
                g.ScaleTransform(sc, sc);

                // Yumuşak sıcak dış ışık halesi
                float hr = 24f * flicker;
                using (GraphicsPath hp = new GraphicsPath())
                {
                    hp.AddEllipse(-hr, -hr, hr * 2f, hr * 2f);
                    using (PathGradientBrush halo = new PathGradientBrush(hp))
                    {
                        halo.CenterColor = Color.FromArgb((int)(88 * flicker), 251, 191, 36);
                        halo.SurroundColors = new Color[] { Color.FromArgb(0, 245, 158, 11) };
                        g.FillPath(halo, hp);
                    }
                }

                // Fener kağıt gövdesi
                RectangleF body = new RectangleF(-9f, -14f, 18f, 24f);
                using (GraphicsPath bp = GraphicsExt.CreateRoundedRect(body, 6f))
                using (LinearGradientBrush grad = new LinearGradientBrush(body, topCol, botCol, LinearGradientMode.Vertical))
                using (Pen rim = new Pen(Color.FromArgb(165, 180, 83, 9), 1.0f))
                {
                    g.FillPath(grad, bp);
                    g.DrawPath(rim, bp);
                }

                // İç alev çekirdeği & alt çerçeve
                using (SolidBrush flame = new SolidBrush(Color.FromArgb(235, 255, 255, 224)))
                using (Pen baseRim = new Pen(Color.FromArgb(210, 120, 53, 15), 1.8f))
                {
                    g.FillEllipse(flame, -3.2f, 2f, 6.4f, 6.4f);
                    g.DrawLine(baseRim, -6.5f, 10.5f, 6.5f, 10.5f);
                }

                g.Restore(st);
            }
        }

        private void DrawWinterBird(Graphics g, WinterBirdEntity b)
        {
            GraphicsState st = g.Save();
            g.TranslateTransform(b.X, b.Y + b.HopOffset);
            g.ScaleTransform(b.Direction, 1f);

            float wingY = (b.State == 1) ? ((float)Math.Sin(b.WingPhase) * 8.5f) : 0f;

            if (b.Species == 0)
            {
                // Kızılgerdan (Winter Robin)
                using (SolidBrush back = new SolidBrush(Color.FromArgb(252, 120, 74, 48)))
                using (SolidBrush breast = new SolidBrush(Color.FromArgb(252, 239, 68, 68)))
                using (SolidBrush eye = new SolidBrush(Color.FromArgb(252, 15, 23, 42)))
                using (SolidBrush beak = new SolidBrush(Color.FromArgb(252, 245, 158, 11)))
                using (Pen legPen = new Pen(Color.FromArgb(230, 82, 54, 38), 1.3f))
                {
                    if (b.State == 0)
                    {
                        g.DrawLine(legPen, -2f, -3f, -2f, 0.5f);
                        g.DrawLine(legPen, 2f, -3f, 2f, 0.5f);
                    }
                    // Kuyruk
                    g.FillPolygon(back, new PointF[] { new PointF(-7f, -7f), new PointF(-15f, -10f), new PointF(-13f, -5f) });
                    // Gövde & Kırmızı göğüs
                    g.FillEllipse(back, -8.5f, -14f, 16f, 11.5f);
                    g.FillEllipse(breast, -1.5f, -11.8f, 9.2f, 8.2f);
                    // Kafa, göz & gaga
                    g.FillEllipse(back, 1.5f, -19f, 9.5f, 9.2f);
                    g.FillEllipse(eye, 6.2f, -16.5f, 2.5f, 2.5f);
                    g.FillPolygon(beak, new PointF[] { new PointF(10.5f, -15.6f), new PointF(15.2f, -14.2f), new PointF(10.5f, -13.0f) });
                    // Kanat
                    if (b.State == 1)
                    {
                        g.FillPolygon(back, new PointF[]
                        {
                            new PointF(-3f, -10f),
                            new PointF(3f, -10f),
                            new PointF(-4f, -14f - wingY)
                        });
                    }
                    else
                    {
                        g.FillEllipse(back, -6.5f, -12.5f, 9.5f, 5.2f);
                    }
                }
            }
            else
            {
                // Kar Baykuşu (Snowy Owl)
                using (SolidBrush white = new SolidBrush(Color.FromArgb(252, 248, 250, 252)))
                using (SolidBrush speck = new SolidBrush(Color.FromArgb(165, 148, 163, 184)))
                using (SolidBrush goldEye = new SolidBrush(Color.FromArgb(252, 250, 204, 21)))
                using (SolidBrush pupil = new SolidBrush(Color.FromArgb(252, 15, 23, 42)))
                using (SolidBrush beak = new SolidBrush(Color.FromArgb(252, 71, 85, 105)))
                using (Pen outline = new Pen(Color.FromArgb(185, 148, 163, 184), 1.0f))
                {
                    // Gövde
                    g.FillEllipse(white, -8f, -17f, 16f, 16f);
                    g.DrawEllipse(outline, -8f, -17f, 16f, 16f);
                    g.FillEllipse(speck, -5f, -11f, 3.2f, 2.0f);
                    g.FillEllipse(speck, 0f, -8f, 3.2f, 2.0f);
                    g.FillEllipse(speck, -3f, -6f, 3.0f, 1.8f);
                    // Kafa & minik tüy kulaklar
                    g.FillEllipse(white, -6.5f, -25.5f, 13.5f, 11.5f);
                    g.DrawEllipse(outline, -6.5f, -25.5f, 13.5f, 11.5f);
                    // Büyük altın baykuş gözleri
                    g.FillEllipse(goldEye, -3.5f, -22.5f, 4.0f, 4.0f);
                    g.FillEllipse(goldEye, 1.8f, -22.5f, 4.0f, 4.0f);
                    g.FillEllipse(pupil, -2.3f, -21.3f, 1.8f, 1.8f);
                    g.FillEllipse(pupil, 3.0f, -21.3f, 1.8f, 1.8f);
                    // Gaga
                    g.FillPolygon(beak, new PointF[] { new PointF(0.2f, -19.2f), new PointF(2.2f, -19.2f), new PointF(1.2f, -16.8f) });
                    // Kanatlar (uçarken genişçe açılır)
                    if (b.State == 1)
                    {
                        g.FillPolygon(white, new PointF[]
                        {
                            new PointF(-6f, -12f),
                            new PointF(6f, -12f),
                            new PointF(-12f, -16f - wingY)
                        });
                        g.FillPolygon(white, new PointF[]
                        {
                            new PointF(-4f, -12f),
                            new PointF(6f, -12f),
                            new PointF(13f, -16f - wingY)
                        });
                    }
                }
            }

            g.Restore(st);
        }

        private void DrawConfetti(Graphics g)
        {
            for (int i = 0; i < _confetti.Length; i++)
            {
                if (!_confetti[i].Active) continue;
                ConfettiParticle c = _confetti[i];
                int a = Math.Max(20, Math.Min(250, (c.Ttl * 250) / Math.Max(1, c.MaxTtl)));
                GraphicsState st = g.Save();
                g.TranslateTransform(c.X, c.Y);
                g.RotateTransform(c.Angle);
                using (SolidBrush cb = new SolidBrush(Color.FromArgb(a, c.Color.R, c.Color.G, c.Color.B)))
                    g.FillRectangle(cb, -c.Size * 0.5f, -c.Size * 0.28f, c.Size, c.Size * 0.56f);
                g.Restore(st);
            }
        }

        private void DrawMagicSparkles(Graphics g)
        {
            for (int i = 0; i < _magicSparkles.Length; i++)
            {
                if (!_magicSparkles[i].Active) continue;
                MagicSparkle sp = _magicSparkles[i];
                int a = Math.Max(15, Math.Min(235, (sp.Ttl * 235) / Math.Max(1, sp.MaxTtl)));
                if (sp.IsRing)
                {
                    using (Pen rp = new Pen(Color.FromArgb(a, 125, 211, 252), 1.6f))
                        g.DrawEllipse(rp, sp.X - sp.Size, sp.Y - sp.Size, sp.Size * 2f, sp.Size * 2f);
                }
                else
                {
                    float s = sp.Size;
                    using (Pen p = new Pen(Color.FromArgb(a, sp.Color.R, sp.Color.G, sp.Color.B), 1.3f))
                    {
                        g.DrawLine(p, sp.X - s, sp.Y, sp.X + s, sp.Y);
                        g.DrawLine(p, sp.X, sp.Y - s, sp.X, sp.Y + s);
                    }
                }
            }
        }

        private void DrawCountdownWidget(Graphics g)
        {
            EnsureCountdownPos();
            float x = _countdownX;
            float y = _countdownY;
            const float w = 216f;
            const float h = 66f;

            DateTime now = DateTime.Now;
            DateTime target = new DateTime(now.Year + 1, 1, 1, 0, 0, 0);
            TimeSpan rem = target - now;
            if (rem.TotalSeconds < 0) rem = TimeSpan.Zero;

            string title = IsEnglish
                ? ("⏳ NEW YEAR " + target.Year)
                : ("⏳ YENİ YILA KALAN (" + target.Year + ")");
            string timeText = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:D2}{1}  {2:D2}:{3:D2}:{4:D2}",
                (int)rem.TotalDays,
                IsEnglish ? "d" : "g",
                rem.Hours, rem.Minutes, rem.Seconds);

            RectangleF card = new RectangleF(x, y, w, h);
            using (GraphicsPath cp = GraphicsExt.CreateRoundedRect(card, 12f))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(196, 11, 17, 30)))
            using (Pen border = new Pen(Color.FromArgb(165, 56, 189, 248), 1.25f))
            {
                g.FillPath(bg, cp);
                g.DrawPath(border, cp);
            }

            // Üst ince Aurora vurgu çizgisi
            using (LinearGradientBrush accent = new LinearGradientBrush(
                new RectangleF(x + 12f, y + 1f, w - 24f, 3f),
                Color.FromArgb(220, 56, 189, 248),
                Color.FromArgb(220, 250, 204, 21),
                LinearGradientMode.Horizontal))
            {
                g.FillRectangle(accent, x + 14f, y + 1f, w - 28f, 2.5f);
            }

            using (Font fTitle = new Font("Segoe UI Semibold", 8.0f, FontStyle.Bold))
            using (Font fTime = new Font("Consolas", 14.2f, FontStyle.Bold))
            using (SolidBrush bTitle = new SolidBrush(Color.FromArgb(225, 148, 163, 184)))
            using (SolidBrush bTime = new SolidBrush(Color.FromArgb(252, 254, 240, 138)))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center })
            {
                g.DrawString(title, fTitle, bTitle, new RectangleF(x, y + 10f, w, 18f), sf);
                g.DrawString(timeText, fTime, bTime, new RectangleF(x, y + 30f, w, 28f), sf);
            }
        }

        private void DrawCelebrationBanner(Graphics g)
        {
            float alphaFrac = Math.Min(1f, Math.Min(_celebrationBannerTicks / 50f, (420 - _celebrationBannerTicks) / 25f));
            int a = Math.Max(0, Math.Min(245, (int)(245f * alphaFrac)));
            if (a <= 5) return;

            string text = IsEnglish
                ? ("✨ HAPPY NEW YEAR " + _celebrationYear + "! ✨")
                : ("✨ MUTLU YILLAR " + _celebrationYear + "! ✨");

            using (Font f = new Font("Segoe UI", 26f, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(text, f);
                float pw = sz.Width + 48f;
                float ph = sz.Height + 24f;
                float px = (_screenW - pw) * 0.5f;
                float py = Math.Max(92f, _screenH * 0.16f) + (float)Math.Sin(_time * 4.0f) * 6f;

                RectangleF rect = new RectangleF(px, py, pw, ph);
                using (GraphicsPath gp = GraphicsExt.CreateRoundedRect(rect, 18f))
                using (SolidBrush bg = new SolidBrush(Color.FromArgb((int)(a * 0.78f), 15, 23, 42)))
                using (Pen rim = new Pen(Color.FromArgb(a, 250, 204, 21), 2.5f))
                using (SolidBrush glow = new SolidBrush(Color.FromArgb((int)(a * 0.35f), 253, 224, 71)))
                using (SolidBrush txt = new SolidBrush(Color.FromArgb(a, 254, 240, 138)))
                {
                    g.FillPath(bg, gp);
                    g.DrawPath(rim, gp);
                    g.DrawString(text, f, glow, px + 26f, py + 14f);
                    g.DrawString(text, f, txt, px + 24f, py + 12f);
                }
            }
        }

        private void DrawToastNotification(Graphics g)
        {
            float alphaFrac = Math.Min(1f, Math.Min(_toastTicks / 28f, (175 - _toastTicks) / 14f));
            int a = Math.Max(0, Math.Min(240, (int)(240f * alphaFrac)));
            if (a <= 5) return;

            using (Font f = new Font("Segoe UI Semibold", 9.8f, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(_toastText, f);
                float pw = sz.Width + 28f;
                float ph = sz.Height + 14f;
                float px = (_screenW - pw) * 0.5f;
                float py = _screenH - ph - 66f;
                RectangleF rect = new RectangleF(px, py, pw, ph);
                using (GraphicsPath gp = GraphicsExt.CreateRoundedRect(rect, 11f))
                using (SolidBrush bg = new SolidBrush(Color.FromArgb((int)(a * 0.88f), 15, 23, 42)))
                using (Pen rim = new Pen(Color.FromArgb(a, 56, 189, 248), 1.4f))
                using (SolidBrush txt = new SolidBrush(Color.FromArgb(a, 224, 242, 254)))
                {
                    g.FillPath(bg, gp);
                    g.DrawPath(rim, gp);
                    g.DrawString(_toastText, f, txt, px + 14f, py + 7f);
                }
            }
        }

        private void DrawCornerFrost(Graphics g)
        {
            for (int c = 0; c < 4; c++)
            {
                float level = _cornerFrost[c];
                if (level <= 0.02f) continue;

                float cx = (c == 0 || c == 2) ? 0f : _screenW;
                float cy = (c == 0 || c == 1) ? 0f : _screenH;
                float dx = (c == 0 || c == 2) ? 1f : -1f;
                float dy = (c == 0 || c == 1) ? 1f : -1f;

                float maxSpan = 105f + level * 110f;
                int baseAlpha = Math.Min(125, (int)(level * 95f));

                // 1. Yumuşak organik radyal buzlu köşe buğusu (keskin kenarsız)
                using (GraphicsPath gp = new GraphicsPath())
                {
                    float r = maxSpan * 1.12f;
                    gp.AddEllipse(cx - r, cy - r, r * 2f, r * 2f);
                    using (PathGradientBrush frostGrad = new PathGradientBrush(gp))
                    {
                        frostGrad.CenterPoint = new PointF(cx, cy);
                        frostGrad.CenterColor = Color.FromArgb(baseAlpha, 224, 242, 254);
                        frostGrad.SurroundColors = new Color[] { Color.FromArgb(0, 186, 230, 253) };
                        g.FillPath(frostGrad, gp);
                    }
                }

                // 2. Dallı buz kristali eğrelti desenleri
                using (Pen crystalPen = new Pen(Color.FromArgb(Math.Min(165, (int)(level * 135f)), 240, 249, 255), 1.35f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                })
                {
                    float[] angles = new float[] { 0.24f, 0.56f, 0.88f, 1.24f };
                    for (int aIdx = 0; aIdx < angles.Length; aIdx++)
                    {
                        float len = maxSpan * (0.52f + (aIdx % 2) * 0.26f);
                        float ex = cx + dx * (float)Math.Cos(angles[aIdx]) * len;
                        float ey = cy + dy * (float)Math.Sin(angles[aIdx]) * len;
                        g.DrawLine(crystalPen, cx, cy, ex, ey);

                        // Yan dallar
                        for (int b = 1; b <= 2; b++)
                        {
                            float frac = b * 0.36f;
                            float bx = cx + (ex - cx) * frac;
                            float by = cy + (ey - cy) * frac;
                            float bLen = len * 0.22f;
                            g.DrawLine(crystalPen, bx, by,
                                bx + dx * (float)Math.Cos(angles[aIdx] + 0.55f) * bLen,
                                by + dy * (float)Math.Sin(angles[aIdx] + 0.55f) * bLen);
                            g.DrawLine(crystalPen, bx, by,
                                bx + dx * (float)Math.Cos(angles[aIdx] - 0.55f) * bLen,
                                by + dy * (float)Math.Sin(angles[aIdx] - 0.55f) * bLen);
                        }
                    }
                }
            }
        }

        private void DrawPolarExpressTrain(Graphics g)
        {
            // 1. Yükselen beyaz buhar halkaları
            for (int i = 0; i < _trainSmoke.Length; i++)
            {
                if (!_trainSmoke[i].Active) continue;
                TrainSmoke sm = _trainSmoke[i];
                int a = Math.Max(12, Math.Min(185, (sm.Ttl * 185) / Math.Max(1, sm.MaxTtl)));
                g.FillEllipse(_alphaWhiteBrushes[a], sm.X - sm.Radius, sm.Y - sm.Radius, sm.Radius * 2f, sm.Radius * 2f);
            }

            GraphicsState st = g.Save();
            g.TranslateTransform(_trainX, _trainY);
            g.ScaleTransform(_trainDir, 1f);

            // 2. Ön Far Işık Hüzmesi (uç nokta poligondan taşırılarak GDI+ 1px sarma çizgisi önlenir)
            using (GraphicsPath beam = new GraphicsPath())
            {
                beam.AddPolygon(new PointF[]
                {
                    new PointF(92f, -18f),
                    new PointF(178f, -34f),
                    new PointF(178f, 2f)
                });
                using (LinearGradientBrush beamBrush = new LinearGradientBrush(
                    new PointF(90f, -16f), new PointF(186f, -16f),
                    Color.FromArgb(90, 254, 240, 138),
                    Color.FromArgb(0, 254, 240, 138)))
                {
                    g.FillPath(beamBrush, beam);
                }
            }

            // 3. İki Adet Sıcak Işıklı Yolcu Vagonu (Sol tarafta, x = -112 ve x = -48)
            DrawTrainCoach(g, -116f);
            DrawTrainCoach(g, -50f);

            // Bağlantı kancaları
            using (Pen linkPen = new Pen(Color.FromArgb(240, 250, 204, 21), 2.4f))
            {
                g.DrawLine(linkPen, -58f, -9f, -50f, -9f);
                g.DrawLine(linkPen, 8f, -9f, 18f, -9f);
            }

            // 4. Klasik Buharlı Lokomotif (x = 18 .. 94)
            using (SolidBrush navy = new SolidBrush(Color.FromArgb(250, 15, 23, 42)))
            using (SolidBrush crimson = new SolidBrush(Color.FromArgb(250, 185, 28, 28)))
            using (SolidBrush gold = new SolidBrush(Color.FromArgb(250, 250, 204, 21)))
            using (SolidBrush warmWin = new SolidBrush(Color.FromArgb(252, 254, 240, 138)))
            using (Pen goldPen = new Pen(Color.FromArgb(245, 250, 204, 21), 1.6f))
            {
                // Kabin
                g.FillRectangle(navy, 18f, -31f, 26f, 25f);
                g.FillRectangle(crimson, 15f, -34f, 32f, 4f);
                g.FillRectangle(warmWin, 23f, -26f, 14f, 11f);
                g.DrawRectangle(goldPen, 23f, -26f, 14f, 11f);

                // Kazan (Boiler)
                g.FillRectangle(navy, 44f, -24f, 42f, 18f);
                g.FillRectangle(gold, 54f, -24f, 3f, 18f);
                g.FillRectangle(gold, 70f, -24f, 3f, 18f);
                // Buhar kubbesi & Baca
                g.FillEllipse(gold, 52f, -30f, 11f, 9f);
                g.FillRectangle(navy, 69f, -36f, 7f, 13f);
                g.FillRectangle(gold, 67f, -38f, 11f, 3f);
                // Far
                g.FillRectangle(gold, 85f, -22f, 6f, 7f);
                g.FillEllipse(warmWin, 88f, -21f, 5f, 5f);

                // Kırmızı Ön Kar Küreyici (Cowcatcher)
                g.FillPolygon(crimson, new PointF[]
                {
                    new PointF(85f, -12f),
                    new PointF(99f, -2f),
                    new PointF(85f, -2f)
                });

                // Alt şasi
                g.FillRectangle(crimson, 17f, -8f, 72f, 4f);
            }

            // Lokomotif Tekerlekleri & Hareketli Biyel Kolu
            DrawTrainWheel(g, 28f, -5.5f, 5.5f);
            DrawTrainWheel(g, 49f, -5.5f, 5.5f);
            DrawTrainWheel(g, 64f, -5.5f, 5.5f);
            DrawTrainWheel(g, 79f, -4.5f, 4.5f);
            float rodX = (float)Math.Cos(_trainWheelPhase) * 3.0f;
            float rodY = (float)Math.Sin(_trainWheelPhase) * 3.0f;
            using (Pen rodPen = new Pen(Color.FromArgb(245, 253, 224, 71), 1.8f))
                g.DrawLine(rodPen, 49f + rodX, -5.5f + rodY, 64f + rodX, -5.5f + rodY);

            g.Restore(st);
        }

        private void DrawTrainCoach(Graphics g, float x)
        {
            using (SolidBrush body = new SolidBrush(Color.FromArgb(248, 30, 41, 59)))
            using (SolidBrush roof = new SolidBrush(Color.FromArgb(248, 226, 232, 240)))
            using (SolidBrush stripe = new SolidBrush(Color.FromArgb(248, 185, 28, 28)))
            using (SolidBrush winGlow = new SolidBrush(Color.FromArgb(48, 253, 224, 71)))
            using (SolidBrush winCore = new SolidBrush(Color.FromArgb(250, 254, 240, 138)))
            using (Pen goldRim = new Pen(Color.FromArgb(230, 250, 204, 21), 1.1f))
            {
                g.FillRectangle(body, x, -25f, 58f, 19f);
                g.FillRectangle(stripe, x, -11f, 58f, 4f);
                g.FillRectangle(roof, x - 2f, -28f, 62f, 4f);
                g.DrawRectangle(goldRim, x, -25f, 58f, 19f);

                for (int w = 0; w < 4; w++)
                {
                    float wx = x + 5f + w * 13f;
                    g.FillEllipse(winGlow, wx - 3f, -24f, 15f, 15f);
                    g.FillRectangle(winCore, wx, -21f, 9f, 8f);
                }
            }
            DrawTrainWheel(g, x + 11f, -4.5f, 4.5f);
            DrawTrainWheel(g, x + 47f, -4.5f, 4.5f);
        }

        private void DrawTrainWheel(Graphics g, float cx, float cy, float r)
        {
            using (SolidBrush hub = new SolidBrush(Color.FromArgb(250, 30, 41, 59)))
            using (Pen rim = new Pen(Color.FromArgb(250, 250, 204, 21), 1.8f))
            {
                g.FillEllipse(hub, cx - r, cy - r, r * 2f, r * 2f);
                g.DrawEllipse(rim, cx - r, cy - r, r * 2f, r * 2f);
                float sx = (float)Math.Cos(_trainWheelPhase) * r * 0.8f;
                float sy = (float)Math.Sin(_trainWheelPhase) * r * 0.8f;
                g.DrawLine(rim, cx - sx, cy - sy, cx + sx, cy + sy);
            }
        }

        private void DrawPenguin(Graphics g, PenguinEntity p)
        {
            float audioHop = (AudioReactiveEnabled && p.SurfaceHwnd != IntPtr.Zero) ? (-_audioBeat * 4.5f) : 0f;
            GraphicsState st = g.Save();
            g.TranslateTransform(p.X, p.Y + audioHop);
            g.ScaleTransform(p.Direction * p.Scale, p.Scale);

            if (p.State == 1 && p.SurfaceHwnd != IntPtr.Zero)
            {
                // Karnının üstünde kayma pozu (Toboggan!)
                g.TranslateTransform(0f, -7f);
                g.RotateTransform(78f);
            }
            else if (p.State == 0 && p.SurfaceHwnd != IntPtr.Zero)
            {
                // Paytak yürüme salınımı
                g.RotateTransform((float)Math.Sin(p.WalkPhase) * 8.5f);
            }

            float footL = (p.State == 0) ? (float)Math.Sin(p.WalkPhase) * 3.5f : 0f;
            float footR = (p.State == 0) ? -(float)Math.Sin(p.WalkPhase) * 3.5f : 0f;
            float flap = (float)Math.Sin(p.FlipperPhase) * ((p.State == 2 || p.SurfaceHwnd == IntPtr.Zero) ? 38f : 14f);

            using (SolidBrush dark = new SolidBrush(Color.FromArgb(252, 22, 32, 52)))
            using (SolidBrush belly = new SolidBrush(Color.FromArgb(252, 248, 250, 252)))
            using (SolidBrush orange = new SolidBrush(Color.FromArgb(252, 249, 115, 22)))
            using (SolidBrush cheek = new SolidBrush(Color.FromArgb(115, 244, 114, 182)))
            {
                // Turuncu ayaklar
                g.FillEllipse(orange, -8f + footL, -4.5f, 8.5f, 4.5f);
                g.FillEllipse(orange, 1f + footR, -4.5f, 8.5f, 4.5f);

                // Gövde & Beyaz Karın
                g.FillEllipse(dark, -10.5f, -24f, 21f, 21.5f);
                g.FillEllipse(belly, -6.5f, -20.5f, 14f, 16.5f);

                // Kafa
                g.FillEllipse(dark, -8.5f, -34f, 17.5f, 16.5f);
                g.FillEllipse(belly, -2.5f, -30.5f, 10f, 10.5f);

                // Şirin göz & beyaz parıltı + pembe yanak
                g.FillEllipse(dark, 1.5f, -28.5f, 3.6f, 3.6f);
                g.FillEllipse(belly, 2.7f, -28.0f, 1.3f, 1.3f);
                g.FillEllipse(cheek, -1.0f, -24.8f, 3.8f, 2.4f);

                // Turuncu Gaga
                g.FillPolygon(orange, new PointF[]
                {
                    new PointF(7.2f, -26.8f),
                    new PointF(14.5f, -24.5f),
                    new PointF(7.2f, -22.2f)
                });

                // Renkli Mini Atkı
                if (p.HasScarf)
                {
                    using (Pen scarfPen = new Pen(p.ScarfColor, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(scarfPen, -8.2f, -19.5f, 7.8f, -19.5f);
                        g.DrawLine(scarfPen, -4.5f, -19.5f, -8.0f, -12.5f);
                    }
                }

                // Hareketli Kanat (Flipper)
                GraphicsState fSt = g.Save();
                g.TranslateTransform(-3f, -17f);
                g.RotateTransform(-18f + flap);
                g.FillEllipse(dark, -3.5f, -2f, 7f, 14f);
                g.Restore(fSt);
            }

            g.Restore(st);
        }

        private void EnsureAuroraBuffers(int w, int h)
        {
            if (!_auroraRowFadeInit)
            {
                for (int y = 0; y < AuroraBufH; y++)
                {
                    float v = (y + 0.5f) / AuroraBufH;
                    float s = (float)Math.Sin(v * Math.PI);
                    _auroraRowFade[y] = s * s; // Üst ve alt kenarda sıfıra eriyen ipeksi çan eğrisi
                }
                _auroraRowFadeInit = true;
            }

            if (_auroraW == w && _auroraH == h && _auroraTmpHoriz != null && _auroraX0Map != null)
                return;

            int[] tmpHoriz = new int[w * AuroraBufH];
            int[] x0Map = new int[w];
            int[] x1Map = new int[w];
            int[] xFracMap = new int[w];
            float scaleX = (float)(AuroraBufW - 1) / Math.Max(1, w - 1);
            for (int dx = 0; dx < w; dx++)
            {
                float fx = dx * scaleX;
                int ix = (int)fx;
                x0Map[dx] = ix;
                x1Map[dx] = (ix + 1 < AuroraBufW) ? (ix + 1) : (AuroraBufW - 1);
                xFracMap[dx] = (int)((fx - ix) * 256f + 0.5f);
            }

            lock (_auroraLock)
            {
                _auroraW = w;
                _auroraH = h;
                _auroraFront = null;
                _auroraSpare = new int[w * h];
                _auroraTmpHoriz = tmpHoriz;
                _auroraX0Map = x0Map;
                _auroraX1Map = x1Map;
                _auroraXFracMap = xFracMap;
                _auroraHasFrame = false;
            }
        }

        private void FillSilkyAuroraPixels(float time, float surgeBoost)
        {
            Array.Clear(_auroraSmallBuf, 0, _auroraSmallBuf.Length);

            float phase0 = time * 0.34f;
            float phase1 = -time * 0.26f + 1.9f;

            for (int x = 0; x < AuroraBufW; x++)
            {
                float t = (float)x / (AuroraBufW - 1);
                float edge = (float)Math.Sin(t * Math.PI);
                edge = edge * (0.35f + 0.65f * edge);

                float w10 = (float)(Math.Sin(t * 4.8f + phase0) * 0.55 + Math.Sin(t * 10.2f - phase0 * 0.65f) * 0.45);
                float w20 = (float)(Math.Cos(t * 6.2f - phase0 * 0.8f) * 0.5 + 0.5);
                float ray0 = (float)(0.72 + 0.28 * Math.Sin(t * 27.0f + phase0 * 2.1f) * Math.Cos(t * 13.5f - phase0 * 1.4f));
                _auroraWaveTop0[x] = 0.04f + (w10 * 0.5f + 0.5f) * 0.18f;
                _auroraWaveThick0[x] = Math.Max(0.22f, (0.38f + 0.44f * w20) * (0.35f + 0.65f * edge));
                _auroraWaveCurtain0[x] = edge * ray0;

                float w11 = (float)(Math.Sin(t * 6.2f + phase1) * 0.55 + Math.Sin(t * 8.5f - phase1 * 0.65f) * 0.45);
                float w21 = (float)(Math.Cos(t * 7.6f - phase1 * 0.8f) * 0.5 + 0.5);
                float ray1 = (float)(0.74 + 0.26 * Math.Sin(t * 23.0f - phase1 * 1.9f) * Math.Cos(t * 11.0f + phase1 * 1.3f));
                _auroraWaveTop1[x] = 0.09f + (w11 * 0.5f + 0.5f) * 0.20f;
                _auroraWaveThick1[x] = Math.Max(0.22f, (0.36f + 0.44f * w21) * (0.35f + 0.65f * edge));
                _auroraWaveCurtain1[x] = edge * ray1;
            }

            float baseA0 = Math.Min(165f, 96f * surgeBoost);
            float baseA1 = Math.Min(140f, 72f * surgeBoost);

            for (int y = 0; y < AuroraBufH; y++)
            {
                float vy = (y + 0.5f) / AuroraBufH;
                float globalFade = _auroraRowFade[y];
                int rowOff = y * AuroraBufW;

                for (int x = 0; x < AuroraBufW; x++)
                {
                    float rSum = 0f, gSum = 0f, bSum = 0f, aSum = 0f;

                    // Katman 0: Zümrüt -> Turkuaz ipeksi perde
                    float u0 = (vy - _auroraWaveTop0[x]) / _auroraWaveThick0[x];
                    if (u0 > 0f && u0 < 1f)
                    {
                        float s0 = (float)Math.Sin(u0 * Math.PI);
                        float intensity0 = s0 * s0 * globalFade * _auroraWaveCurtain0[x];
                        float a0 = (baseA0 * intensity0) / 255f;
                        if (a0 > 0.002f)
                        {
                            float r0 = 18f + 38f * u0;
                            float g0 = 220f - 28f * u0;
                            float b0 = 152f + 98f * u0;
                            rSum += r0 * a0;
                            gSum += g0 * a0;
                            bSum += b0 * a0;
                            aSum += a0;
                        }
                    }

                    // Katman 1: Çivit -> Mor -> Eflatun ipeksi perde
                    float u1 = (vy - _auroraWaveTop1[x]) / _auroraWaveThick1[x];
                    if (u1 > 0f && u1 < 1f)
                    {
                        float s1 = (float)Math.Sin(u1 * Math.PI);
                        float intensity1 = s1 * s1 * globalFade * _auroraWaveCurtain1[x];
                        float a1 = (baseA1 * intensity1) / 255f;
                        if (a1 > 0.002f)
                        {
                            float r1 = 115f + 115f * u1;
                            float g1 = 92f + 26f * u1;
                            float b1 = 244f + 8f * u1;
                            float invA = 1f - aSum;
                            rSum += r1 * a1 * invA;
                            gSum += g1 * a1 * invA;
                            bSum += b1 * a1 * invA;
                            aSum += a1 * invA;
                        }
                    }

                    if (aSum > 0.002f)
                    {
                        int ai = (int)(aSum * 255f + 0.5f);
                        if (ai > 255) ai = 255;
                        int ri = (int)(rSum + 0.5f); if (ri > ai) ri = ai;
                        int gi = (int)(gSum + 0.5f); if (gi > ai) gi = ai;
                        int bi = (int)(bSum + 0.5f); if (bi > ai) bi = ai;
                        _auroraSmallBuf[rowOff + x] = (ai << 24) | (ri << 16) | (gi << 8) | bi;
                    }
                }
            }
        }

        private static void BilinearUpscaleAuroraSwar(
            int[] src, int[] tmpHoriz, int[] dst, int dstW, int dstH,
            int[] x0Map, int[] x1Map, int[] xFracMap)
        {
            // Geçiş 1: Yatay çift-doğrusal büyütme (48 satır x dstW sütun)
            for (int sy = 0; sy < AuroraBufH; sy++)
            {
                int sOff = sy * AuroraBufW;
                int tOff = sy * dstW;
                for (int dx = 0; dx < dstW; dx++)
                {
                    int c0 = src[sOff + x0Map[dx]];
                    int c1 = src[sOff + x1Map[dx]];
                    if (c0 == 0 && c1 == 0)
                    {
                        tmpHoriz[tOff + dx] = 0;
                        continue;
                    }
                    uint w = (uint)xFracMap[dx];
                    uint iw = 256u - w;
                    uint rb = ((((uint)c0 & 0x00FF00FFu) * iw + ((uint)c1 & 0x00FF00FFu) * w) >> 8) & 0x00FF00FFu;
                    uint ag = (((((uint)c0 >> 8) & 0x00FF00FFu) * iw) + ((((uint)c1 >> 8) & 0x00FF00FFu) * w)) & 0xFF00FF00u;
                    tmpHoriz[tOff + dx] = (int)(rb | ag);
                }
            }

            // Geçiş 2: Dikey çift-doğrusal büyütme (dstH satır x dstW sütun)
            float scaleY = (float)(AuroraBufH - 1) / Math.Max(1, dstH - 1);
            for (int dy = 0; dy < dstH; dy++)
            {
                float fy = dy * scaleY;
                int sy0 = (int)fy;
                int sy1 = sy0 + 1;
                if (sy1 >= AuroraBufH) sy1 = AuroraBufH - 1;
                uint wy = (uint)(int)((fy - sy0) * 256f + 0.5f);
                uint iwy = 256u - wy;
                int tOff0 = sy0 * dstW;
                int tOff1 = sy1 * dstW;
                int dOff = dy * dstW;

                for (int dx = 0; dx < dstW; dx++)
                {
                    int c0 = tmpHoriz[tOff0 + dx];
                    int c1 = tmpHoriz[tOff1 + dx];
                    if (c0 == 0 && c1 == 0)
                    {
                        dst[dOff + dx] = 0;
                        continue;
                    }
                    uint rb = ((((uint)c0 & 0x00FF00FFu) * iwy + ((uint)c1 & 0x00FF00FFu) * wy) >> 8) & 0x00FF00FFu;
                    uint ag = (((((uint)c0 >> 8) & 0x00FF00FFu) * iwy) + ((((uint)c1 >> 8) & 0x00FF00FFu) * wy)) & 0xFF00FF00u;
                    dst[dOff + dx] = (int)(rb | ag);
                }
            }
        }

        private void DrawAuroraBorealis(Graphics g)
        {
            int destW = _screenW;
            int destH = (int)Math.Min(260f, _screenH * 0.27f);
            if (destH < 20 || destW < 20 || _dibBits == IntPtr.Zero) return;

            EnsureAuroraBuffers(destW, destH);

            // Kuzey ışıkları yavaş süzüldüğü için hesaplama ve büyütmeyi 3 karede bir (20 Hz)
            // arka plan iş parçacığında (ThreadPool) yap; 60 FPS ana çizim döngüsü ASLA beklemesin!
            if ((!_auroraHasFrame || (_frameCount % 3) == 0) &&
                System.Threading.Interlocked.CompareExchange(ref _auroraWorking, 1, 0) == 0)
            {
                int[] workBuf;
                int[] tmpHoriz;
                int[] x0Map;
                int[] x1Map;
                int[] xFracMap;
                int jobW;
                int jobH;
                lock (_auroraLock)
                {
                    jobW = _auroraW;
                    jobH = _auroraH;
                    tmpHoriz = _auroraTmpHoriz;
                    x0Map = _auroraX0Map;
                    x1Map = _auroraX1Map;
                    xFracMap = _auroraXFracMap;
                    workBuf = _auroraSpare;
                    _auroraSpare = null;
                }

                if (workBuf == null || workBuf.Length != jobW * jobH)
                    workBuf = new int[jobW * jobH];

                float audioBoost = AudioReactiveEnabled ? (_audioLevelSmooth * 0.95f + _audioBeat * 0.45f) : 0f;
                float surgeBoost = 1.0f + _auroraSurge * 0.90f + audioBoost;
                float snapTime = _time;

                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        FillSilkyAuroraPixels(snapTime, surgeBoost);
                        BilinearUpscaleAuroraSwar(_auroraSmallBuf, tmpHoriz, workBuf, jobW, jobH, x0Map, x1Map, xFracMap);
                        lock (_auroraLock)
                        {
                            if (!_auroraDisposed && jobW == _auroraW && jobH == _auroraH)
                            {
                                int[] oldFront = _auroraFront;
                                _auroraFront = workBuf;
                                _auroraSpare = oldFront;
                                _auroraHasFrame = true;
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        System.Threading.Interlocked.Exchange(ref _auroraWorking, 0);
                    }
                });
            }

            // Ana 60 FPS çizim döngüsü sadece hazır tamponu doğrudan DIBSection belleğine kopyalar (~0.17 ms!)
            if (_auroraHasFrame)
            {
                g.Flush(FlushIntention.Sync);
                lock (_auroraLock)
                {
                    if (_auroraHasFrame && _auroraFront != null && _auroraW == _screenW && _auroraH == destH)
                    {
                        int count = _auroraW * _auroraH;
                        if (count > 0 && count <= _screenW * _screenH)
                            Marshal.Copy(_auroraFront, 0, _dibBits, count);
                    }
                }
            }
        }

        private void DrawShootingStars(Graphics g)
        {
            for (int i = 0; i < _shootingStars.Length; i++)
            {
                if (!_shootingStars[i].Active) continue;
                ShootingStar st = _shootingStars[i];
                float life = (float)st.Ttl / Math.Max(1, st.MaxTtl);
                float fade = (life < 0.25f) ? (life / 0.25f) : (life > 0.85f ? (1f - life) / 0.15f : 1f);
                int headA = Math.Max(0, Math.Min(255, (int)(245 * fade)));
                if (headA < 6) continue;

                float spd = (float)Math.Sqrt(st.Vx * st.Vx + st.Vy * st.Vy);
                float ux = st.Vx / Math.Max(0.1f, spd);
                float uy = st.Vy / Math.Max(0.1f, spd);
                float tailX = st.X - ux * st.TailLen;
                float tailY = st.Y - uy * st.TailLen;

                using (LinearGradientBrush tailBrush = new LinearGradientBrush(
                    new PointF(tailX, tailY),
                    new PointF(st.X, st.Y),
                    Color.FromArgb(0, st.Color.R, st.Color.G, st.Color.B),
                    Color.FromArgb(headA, st.Color.R, st.Color.G, st.Color.B)))
                using (Pen tailPen = new Pen(tailBrush, 2.2f) { EndCap = LineCap.Round })
                {
                    g.DrawLine(tailPen, tailX, tailY, st.X, st.Y);
                }

                // Parlayan 4 köşeli yıldız başı
                using (SolidBrush glow = new SolidBrush(Color.FromArgb(headA / 3, st.Color.R, st.Color.G, st.Color.B)))
                using (SolidBrush core = new SolidBrush(Color.FromArgb(headA, 255, 255, 255)))
                {
                    g.FillEllipse(glow, st.X - 6f, st.Y - 6f, 12f, 12f);
                    g.FillEllipse(core, st.X - 2.2f, st.Y - 2.2f, 4.4f, 4.4f);
                }
            }
        }

        private void DrawSantaSleighAndTrail(Graphics g)
        {
            // 1. Sihirli Yıldız Tozu İzi (4 köşeli parlayan yıldızlar)
            for (int i = 0; i < _santaSparkles.Length; i++)
            {
                if (!_santaSparkles[i].Active) continue;
                SantaSparkle sp = _santaSparkles[i];
                int a = Math.Max(15, Math.Min(240, (sp.Ttl * 240) / Math.Max(1, sp.MaxTtl)));
                float r = sp.Size * (0.5f + 0.5f * ((float)sp.Ttl / Math.Max(1, sp.MaxTtl)));
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, sp.Color.R, sp.Color.G, sp.Color.B)))
                {
                    PointF[] star = new PointF[]
                    {
                        new PointF(sp.X, sp.Y - r * 1.4f),
                        new PointF(sp.X + r * 0.42f, sp.Y - r * 0.42f),
                        new PointF(sp.X + r * 1.4f, sp.Y),
                        new PointF(sp.X + r * 0.42f, sp.Y + r * 0.42f),
                        new PointF(sp.X, sp.Y + r * 1.4f),
                        new PointF(sp.X - r * 0.42f, sp.Y + r * 0.42f),
                        new PointF(sp.X - r * 1.4f, sp.Y),
                        new PointF(sp.X - r * 0.42f, sp.Y - r * 0.42f)
                    };
                    g.FillPolygon(b, star);
                }
            }

            if (!_santaActive) return;

            // Arka planda uygulama penceresinin üzerinden geçerken nazikçe yarı-saydamlaşarak rahatsız etmez
            float alphaMult = IsPointInsideAnyApplicationWindow(_santaX, _santaY) ? 0.62f : 0.96f;
            int aFull = (int)(250 * alphaMult);
            int aSoft = (int)(175 * alphaMult);

            GraphicsState st = g.Save();
            g.TranslateTransform(_santaX, _santaY);
            g.ScaleTransform(_santaDir * _santaScale, _santaScale);
            float climbTilt = Math.Max(-12f, Math.Min(12f, (_santaY - _santaPrevY) * 6.0f));
            g.RotateTransform(climbTilt);

            // 2. Kızak Ön Feneri Sıcak Işık Halesi
            using (SolidBrush lampHalo = new SolidBrush(Color.FromArgb((int)(46 * alphaMult), 253, 224, 71)))
                g.FillEllipse(lampHalo, -44f, -22f, 38f, 38f);

            // 3. Altın Dizginler (Noel Baba'dan 3 ren geyiğine zarif kavis)
            using (Pen reinPen = new Pen(Color.FromArgb(aSoft, 250, 204, 21), 1.4f))
            {
                float r0y = (float)Math.Sin(_santaGallopPhase) * 3.5f;
                float r1y = (float)Math.Sin(_santaGallopPhase - 1.15f) * 3.5f - 1.5f;
                float r2y = (float)Math.Sin(_santaGallopPhase - 2.30f) * 3.5f - 3.0f;
                g.DrawBezier(reinPen, -44f, -11f, -18f, -4f, 0f, r0y - 4f, 18f, r0y - 6f);
                g.DrawBezier(reinPen, 18f, r0y - 6f, 40f, r0y - 2f, 48f, r1y - 4f, 64f, r1y - 6f);
                g.DrawBezier(reinPen, 64f, r1y - 6f, 84f, r1y - 2f, 94f, r2y - 4f, 110f, r2y - 6f);
            }

            // 4. 3 Adet Dörtnala Uçan Ren Geyiği (Öndeki Rudolph Kırmızı Parlayan Burunlu!)
            DrawReindeer(g, 18f, (float)Math.Sin(_santaGallopPhase) * 3.5f, _santaGallopPhase, false, alphaMult);
            DrawReindeer(g, 64f, (float)Math.Sin(_santaGallopPhase - 1.15f) * 3.5f - 1.5f, _santaGallopPhase - 1.15f, false, alphaMult);
            DrawReindeer(g, 110f, (float)Math.Sin(_santaGallopPhase - 2.30f) * 3.5f - 3.0f, _santaGallopPhase - 2.30f, true, alphaMult);

            // 5. Hediye Çuvalı & Taşan Paketler (Kızağın arkasında)
            using (SolidBrush sackBrush = new SolidBrush(Color.FromArgb(aFull, 146, 64, 14)))
            using (SolidBrush giftGreen = new SolidBrush(Color.FromArgb(aFull, 16, 185, 129)))
            using (SolidBrush giftBlue = new SolidBrush(Color.FromArgb(aFull, 59, 130, 246)))
            using (SolidBrush giftGold = new SolidBrush(Color.FromArgb(aFull, 250, 204, 21)))
            {
                g.FillEllipse(sackBrush, -98f, -24f, 34f, 28f);
                g.FillRectangle(giftGreen, -92f, -30f, 10f, 10f);
                g.FillRectangle(giftBlue, -80f, -28f, 9f, 9f);
                g.FillRectangle(giftGold, -88f, -30f, 2f, 10f);
                g.FillRectangle(giftGold, -76f, -28f, 2f, 9f);
            }

            // 6. Noel Baba (Kızak içinde, sallanan kol ve ponponlu şapka)
            float waveArm = (float)Math.Sin(_santaWavePhase * 3.2f) * 5.5f;
            using (SolidBrush redCoat = new SolidBrush(Color.FromArgb(aFull, 220, 38, 38)))
            using (SolidBrush whiteFur = new SolidBrush(Color.FromArgb(aFull, 255, 255, 255)))
            using (SolidBrush skin = new SolidBrush(Color.FromArgb(aFull, 254, 215, 170)))
            using (SolidBrush eyeB = new SolidBrush(Color.FromArgb(aFull, 15, 23, 42)))
            using (SolidBrush cheekB = new SolidBrush(Color.FromArgb((int)(120 * alphaMult), 248, 113, 113)))
            using (Pen armPen = new Pen(Color.FromArgb(aFull, 220, 38, 38), 5.0f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                // Gövde
                g.FillEllipse(redCoat, -72f, -22f, 24f, 24f);
                // El sallayan / dizgin tutan kol
                g.DrawLine(armPen, -56f, -15f, -44f, -12f + waveArm * 0.45f);
                g.FillEllipse(whiteFur, -46f, -15f + waveArm * 0.45f, 5.5f, 5.5f);
                // Kafa, göz & pembe yanak
                g.FillEllipse(skin, -65f, -34f, 14f, 14f);
                g.FillEllipse(eyeB, -55.5f, -29.2f, 2.3f, 2.3f);
                g.FillEllipse(cheekB, -57.5f, -26.5f, 3.8f, 2.4f);
                // Kabarık beyaz sakal
                g.FillEllipse(whiteFur, -63f, -26f, 14f, 11f);
                // Noel Şapkası
                PointF[] hatPts = new PointF[]
                {
                    new PointF(-66f, -32f),
                    new PointF(-50f, -32f),
                    new PointF(-70f, -41f)
                };
                g.FillPolygon(redCoat, hatPts);
                g.FillEllipse(whiteFur, -67f, -34f, 18f, 5f);
                g.FillEllipse(whiteFur, -74f, -44f, 6f, 6f);
            }

            // 7. Süslü Kırmızı & Altın Kızak Gövdesi + Kıvrık Kızak Ayakları
            using (Pen runnerPen = new Pen(Color.FromArgb(aFull, 250, 204, 21), 2.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (Pen strutPen = new Pen(Color.FromArgb(aFull, 217, 119, 6), 2.2f))
            using (SolidBrush sleighBody = new SolidBrush(Color.FromArgb(aFull, 185, 28, 28)))
            using (Pen goldTrim = new Pen(Color.FromArgb(aFull, 253, 224, 71), 2.0f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                // Kızak ayakları & dikmeler
                g.DrawLine(strutPen, -86f, 4f, -86f, 16f);
                g.DrawLine(strutPen, -46f, 4f, -46f, 16f);
                g.DrawLine(runnerPen, -104f, 16f, -34f, 16f);
                g.DrawBezier(runnerPen, -34f, 16f, -20f, 16f, -14f, 6f, -22f, 1f);

                // Kırmızı kızak gövdesi
                PointF[] body = new PointF[]
                {
                    new PointF(-102f, -10f),
                    new PointF(-74f, -8f),
                    new PointF(-44f, -8f),
                    new PointF(-26f, -14f),
                    new PointF(-32f, 7f),
                    new PointF(-94f, 7f)
                };
                g.FillPolygon(sleighBody, body);
                g.DrawLines(goldTrim, new PointF[] {
                    new PointF(-102f, -10f),
                    new PointF(-74f, -8f),
                    new PointF(-44f, -8f),
                    new PointF(-26f, -14f)
                });
                g.DrawLine(goldTrim, -92f, 5f, -34f, 5f);

                // Ön altın fener
                using (SolidBrush lampCore = new SolidBrush(Color.FromArgb(aFull, 254, 240, 138)))
                    g.FillEllipse(lampCore, -29f, -11f, 6f, 6f);
            }

            g.Restore(st);
        }

        private static void DrawReindeer(Graphics g, float x, float y, float gallopPhase, bool isRudolph, float alphaMult)
        {
            int a = (int)(248 * alphaMult);
            float legSwing = (float)Math.Sin(gallopPhase) * 9.5f;
            float backSwing = (float)Math.Sin(gallopPhase + Math.PI * 0.85) * 9.5f;

            using (SolidBrush bodyBrush = new SolidBrush(Color.FromArgb(a, 138, 86, 48)))
            using (SolidBrush bellyBrush = new SolidBrush(Color.FromArgb(a, 180, 128, 82)))
            using (Pen legPen = new Pen(Color.FromArgb(a, 110, 66, 35), 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (Pen antlerPen = new Pen(Color.FromArgb(a, 78, 46, 24), 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (Pen harnessPen = new Pen(Color.FromArgb(a, 220, 38, 38), 2.2f))
            {
                // Arka & ön bacaklar (dörtnala hareketli)
                g.DrawLine(legPen, x - 9f, y + 3f, x - 14f - backSwing * 0.7f, y + 14f - Math.Abs(backSwing) * 0.25f);
                g.DrawLine(legPen, x - 6f, y + 3f, x - 10f + backSwing * 0.5f, y + 14f);
                g.DrawLine(legPen, x + 7f, y + 3f, x + 13f + legSwing * 0.75f, y + 13f - Math.Abs(legSwing) * 0.3f);
                g.DrawLine(legPen, x + 4f, y + 3f, x + 9f - legSwing * 0.5f, y + 14f);

                // Kuyruk
                g.FillEllipse(bellyBrush, x - 15f, y - 5f, 5f, 4f);

                // Gövde
                g.FillEllipse(bodyBrush, x - 13f, y - 6f, 25f, 12f);
                g.FillEllipse(bellyBrush, x - 8f, y + 0.5f, 16f, 5f);

                // Kırmızı koşum kayışı & altın zil
                g.DrawLine(harnessPen, x + 3f, y - 6f, x + 1f, y + 5f);
                using (SolidBrush bell = new SolidBrush(Color.FromArgb(a, 250, 204, 21)))
                    g.FillEllipse(bell, x + 5f, y - 3f, 3.6f, 3.6f);

                // Boyun ve Kafa
                PointF[] neck = new PointF[] {
                    new PointF(x + 6f, y - 2f),
                    new PointF(x + 12f, y - 14f),
                    new PointF(x + 17f, y - 12f),
                    new PointF(x + 11f, y + 2f)
                };
                g.FillPolygon(bodyBrush, neck);
                g.FillEllipse(bodyBrush, x + 10f, y - 17f, 12f, 7.5f);
                using (SolidBrush eyeB = new SolidBrush(Color.FromArgb(a, 15, 23, 42)))
                    g.FillEllipse(eyeB, x + 16.5f, y - 14.8f, 1.8f, 1.8f);

                // Dallı Boynuzlar
                g.DrawLine(antlerPen, x + 13f, y - 16f, x + 10f, y - 25f);
                g.DrawLine(antlerPen, x + 11f, y - 21f, x + 15f, y - 24f);
                g.DrawLine(antlerPen, x + 15f, y - 16f, x + 17f, y - 24f);
                g.DrawLine(antlerPen, x + 16f, y - 20f, x + 20f, y - 22f);

                // Rudolph'un Parlayan Kırmızı Burnu!
                if (isRudolph)
                {
                    using (SolidBrush noseGlow = new SolidBrush(Color.FromArgb((int)(95 * alphaMult), 239, 68, 68)))
                    using (SolidBrush noseCore = new SolidBrush(Color.FromArgb(a, 248, 45, 45)))
                    {
                        g.FillEllipse(noseGlow, x + 16.5f, y - 19.5f, 12f, 12f);
                        g.FillEllipse(noseCore, x + 20.0f, y - 15.8f, 4.6f, 4.6f);
                    }
                }
            }
        }

        private void DrawGiftBox(Graphics g, GiftBoxEntity gift)
        {
            float sz = gift.Size;
            float cx = gift.X;
            float by = gift.Y;
            float swayAngle = gift.HasParachute ? (float)Math.Sin(gift.SwayPhase) * 14f : 0f;

            GraphicsState st = g.Save();
            g.TranslateTransform(cx, by - sz * 0.5f);
            g.RotateTransform(swayAngle);

            // Havadaysa şirin beyaz paraşüt çiz
            if (gift.HasParachute)
            {
                float canopyY = -sz * 1.85f;
                using (Pen cord = new Pen(Color.FromArgb(190, 226, 232, 240), 1.1f))
                {
                    g.DrawLine(cord, -sz * 0.9f, canopyY + sz * 0.55f, -sz * 0.35f, -sz * 0.45f);
                    g.DrawLine(cord, 0f, canopyY + sz * 0.45f, 0f, -sz * 0.45f);
                    g.DrawLine(cord, sz * 0.9f, canopyY + sz * 0.55f, sz * 0.35f, -sz * 0.45f);
                }
                using (SolidBrush canopy = new SolidBrush(Color.FromArgb(238, 248, 250, 252)))
                using (SolidBrush stripe = new SolidBrush(Color.FromArgb(220, gift.BoxColor.R, gift.BoxColor.G, gift.BoxColor.B)))
                using (Pen canopyRim = new Pen(Color.FromArgb(180, 148, 163, 184), 1.2f))
                {
                    RectangleF cRect = new RectangleF(-sz * 1.05f, canopyY, sz * 2.1f, sz * 1.25f);
                    g.FillPie(canopy, cRect.X, cRect.Y, cRect.Width, cRect.Height, 180, 180);
                    g.FillPie(stripe, cRect.X, cRect.Y, cRect.Width, cRect.Height, 240, 60);
                    g.DrawPie(canopyRim, cRect.X, cRect.Y, cRect.Width, cRect.Height, 180, 180);
                }
            }
            else
            {
                // Yere indiyse tıklamaya davet eden hafif altın ışıltı halesi
                float pulse = 0.55f + 0.45f * (float)Math.Sin(gift.PulsePhase);
                using (SolidBrush halo = new SolidBrush(Color.FromArgb((int)(42 * pulse), 253, 224, 71)))
                    g.FillEllipse(halo, -sz * 0.9f, -sz * 0.9f, sz * 1.8f, sz * 1.8f);
            }

            // Hediye Kutusu
            RectangleF boxRect = new RectangleF(-sz * 0.5f, -sz * 0.5f, sz, sz);
            using (GraphicsPath bp = GraphicsExt.CreateRoundedRect(boxRect, 3.5f))
            using (SolidBrush boxBrush = new SolidBrush(gift.BoxColor))
            using (SolidBrush ribBrush = new SolidBrush(gift.RibbonColor))
            using (Pen outline = new Pen(Color.FromArgb(180, 15, 23, 42), 1.2f))
            {
                g.FillPath(boxBrush, bp);
                g.FillRectangle(ribBrush, -sz * 0.12f, -sz * 0.5f, sz * 0.24f, sz);
                g.FillRectangle(ribBrush, -sz * 0.5f, -sz * 0.12f, sz, sz * 0.24f);
                g.DrawPath(outline, bp);

                // Üst kurdele fiyongu
                using (Pen bowPen = new Pen(gift.RibbonColor, 2.2f))
                {
                    g.DrawEllipse(bowPen, -sz * 0.34f, -sz * 0.74f, sz * 0.34f, sz * 0.26f);
                    g.DrawEllipse(bowPen, 0f, -sz * 0.74f, sz * 0.34f, sz * 0.26f);
                }
            }

            g.Restore(st);
        }

        private void DrawFireworks(Graphics g)
        {
            // Yükselen roket başları
            for (int i = 0; i < _rockets.Length; i++)
            {
                if (!_rockets[i].Active) continue;
                FireworkRocket r = _rockets[i];
                using (SolidBrush glow = new SolidBrush(Color.FromArgb(90, r.PrimaryColor.R, r.PrimaryColor.G, r.PrimaryColor.B)))
                using (SolidBrush core = new SolidBrush(Color.White))
                {
                    g.FillEllipse(glow, r.X - 6f, r.Y - 6f, 12f, 12f);
                    g.FillEllipse(core, r.X - 2.5f, r.Y - 2.5f, 5f, 5f);
                }
            }

            // Patlayan kıvılcım çizgileri ve ışıldayan uçlar
            for (int i = 0; i < _sparks.Length; i++)
            {
                if (!_sparks[i].Active) continue;
                FireworkSpark sp = _sparks[i];
                float life = (float)sp.Ttl / Math.Max(1, sp.MaxTtl);
                if (sp.Twinkle && (sp.Ttl % 4 == 0)) continue;
                int a = Math.Max(10, Math.Min(255, (int)(255 * life)));

                using (Pen trail = new Pen(Color.FromArgb(a, sp.Color.R, sp.Color.G, sp.Color.B), Math.Max(1.2f, sp.Size * 0.72f))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                })
                {
                    g.DrawLine(trail, sp.PrevX, sp.PrevY, sp.X, sp.Y);
                }
                if (life > 0.45f)
                {
                    float cr = sp.Size * 0.48f;
                    g.FillEllipse(_alphaWhiteBrushes[a], sp.X - cr, sp.Y - cr, cr * 2f, cr * 2f);
                }
            }
        }

        private void DrawShadedSnowSphere(Graphics g, float cx, float cy, float r)
        {
            SnowmanRenderer.DrawShadedSnowSphere(g, cx, cy, r);
        }

        private void DrawSnowballEntity(Graphics g, SnowballEntity sb)
        {
            float r1 = sb.BaseRadius;
            float y1 = sb.Y - r1 + 2f;
            DrawShadedSnowSphere(g, sb.X, y1, r1);

            // Yuvarlanma dokusu (küçük kar benekleri)
            float dotX = sb.X + (float)Math.Cos(sb.RollAngle) * (r1 * 0.45f);
            float dotY = y1 + (float)Math.Sin(sb.RollAngle) * (r1 * 0.45f);
            g.FillEllipse(_alphaIceBrushes[210], dotX - 2f, dotY - 2f, 4f, 4f);

            if (sb.StackCount == 2)
            {
                float r2 = sb.MidRadius;
                float wobbleX = (float)Math.Sin(sb.WobblePhase) * sb.WobbleAmp * 10f;
                float y2 = y1 - r1 - r2 * 0.65f;
                DrawShadedSnowSphere(g, sb.X + wobbleX, y2, r2);

                // Etkileşim modunda 3. kartopunu koyabileceğini gösteren zarif hedef halkası
                if (InteractiveMode)
                {
                    float r3Hint = r2 * 0.78f;
                    float y3Hint = y2 - r2 - r3Hint * 0.65f;
                    using (Pen dashedPen = new Pen(Color.FromArgb(150, 125, 211, 252), 1.4f))
                    {
                        dashedPen.DashStyle = DashStyle.Dash;
                        g.DrawEllipse(dashedPen, sb.X - r3Hint, y3Hint - r3Hint, r3Hint * 2f, r3Hint * 2f);
                    }
                }
            }
        }

        private void DrawSnowman(Graphics g, SnowmanEntity sm, bool isHighlighted)
        {
            float beatBounce = (AudioReactiveEnabled && !sm.Dragging) ? (-_audioBeat * 5.2f) : 0f;
            SnowmanRenderer.DrawSnowman(g, sm, sm.X, sm.Y + beatBounce, 1.0f, _lastCursorPos.X, _lastCursorPos.Y, _time, isHighlighted);
        }

        private void DrawFlyingHat(Graphics g, FlyingHat fh)
        {
            GraphicsState st = g.Save();
            g.TranslateTransform(fh.X, fh.Y);
            g.RotateTransform(fh.Angle);
            SnowmanRenderer.DrawHatGeometry(g, 0f, 0f, 7.5f * fh.Scale, fh.Scale, fh.HatStyle, fh.HatColor);
            g.Restore(st);
        }

        private void DrawWindowIcicles(Graphics g, WindowSurface surf)
        {
            for (int i = 0; i < surf.Icicles.Length; i++)
            {
                float len = surf.Icicles[i];
                if (len < 2.5f) continue;
                float ix = surf.Left + 18f + i * 28f;
                if (ix > surf.Right - 16f) break;
                float w = Math.Min(7.5f, 2.6f + len * 0.15f);
                DrawSingleIcicle(g, ix, surf.Top + 1f, w, len);
            }
        }

        private void DrawSingleIcicle(Graphics g, float x, float topY, float w, float len)
        {
            PointF[] tri = {
                new PointF(x - w * 0.5f, topY),
                new PointF(x + w * 0.5f, topY),
                new PointF(x, topY + len)
            };
            g.FillPolygon(_alphaIceBrushes[185], tri);
            g.DrawLine(_snowHighlightPen, x - w * 0.2f, topY + 1f, x, topY + len * 0.85f);
        }

        private void DrawThrowTrajectoryArc(Graphics g)
        {
            float vx, vy;
            GetThrowVelocity(out vx, out vy);
            float spd = (float)Math.Sqrt(vx * vx + vy * vy);
            if (spd < 3.2f) return;

            float sx = (_draggedSnowball != null) ? _draggedSnowball.X : _draggedPlow.X;
            float sy = (_draggedSnowball != null) ? (_draggedSnowball.Y - _draggedSnowball.BaseRadius) : (_draggedPlow.Y - 12f);
            float mult = (_draggedSnowball != null) ? 0.92f : 1.0f;
            float grav = (_draggedSnowball != null) ? 0.52f : 0.55f;

            float px = sx;
            float py = sy;
            float pvx = vx * mult;
            float pvy = vy * mult;

            for (int step = 1; step <= 14; step++)
            {
                pvy += grav;
                px += pvx;
                py += pvy;
                if (step % 2 == 0)
                {
                    byte a = (byte)Math.Max(30, 195 - step * 11);
                    float r = Math.Max(2.0f, 5.2f - step * 0.22f);
                    g.FillEllipse(_alphaIceBrushes[a], px - r, py - r, r * 2f, r * 2f);
                }
            }
        }

        private static readonly Font PopupFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);

        private void DrawCableCarSystem(Graphics g)
        {
            float ax = _cableAx, ay = _cableAy;
            float bx = _cableBx, by = _cableBy;
            float towerH = 28f;
            float p0x = ax, p0y = ay - towerH;
            float p1x = bx, p1y = by - towerH;

            float spanDist = (float)Math.Sqrt((p1x - p0x) * (p1x - p0x) + (p1y - p0y) * (p1y - p0y));
            float baseSag = Math.Min(48f, 12f + spanDist * 0.055f);
            // Kabinin bulunduğu noktada ağırlıktan dolayı hafif ekstra esneme
            float weightBoost = 1f + 0.18f * (float)Math.Sin(_cableCarT * Math.PI);
            float sag = baseSag * weightBoost;

            // 1. Sarkan Çelik Halat (Catenary Curve)
            const int segs = 20;
            PointF[] cablePts = new PointF[segs + 1];
            for (int i = 0; i <= segs; i++)
            {
                float t = i / (float)segs;
                cablePts[i] = EvaluateCableCatenary(p0x, p0y, p1x, p1y, t, sag);
            }

            using (Pen cableShadowPen = new Pen(Color.FromArgb(140, 15, 23, 42), 2.8f))
            using (Pen cableSteelPen = new Pen(Color.FromArgb(235, 148, 163, 184), 1.8f))
            {
                g.DrawLines(cableShadowPen, cablePts);
                g.DrawLines(cableSteelPen, cablePts);
            }

            // 2. İki Pencere Üstündeki Teleferik İstasyon Direkleri (A ve B)
            DrawCableTower(g, ax, ay, towerH);
            DrawCableTower(g, bx, by, towerH);

            // 3. Halat Üzerinde Kayan Tekerlek (Trolley) ve Sallanan Kırmızı Teleferik Kabini
            PointF trolley = EvaluateCableCatenary(p0x, p0y, p1x, p1y, _cableCarT, sag);

            // Halat eğim açısı
            float dt = 0.02f;
            PointF ptA = EvaluateCableCatenary(p0x, p0y, p1x, p1y, Math.Max(0f, _cableCarT - dt), sag);
            PointF ptB = EvaluateCableCatenary(p0x, p0y, p1x, p1y, Math.Min(1f, _cableCarT + dt), sag);
            float cableAngleDeg = (float)(Math.Atan2(ptB.Y - ptA.Y, ptB.X - ptA.X) * 180.0 / Math.PI);

            // Trolley makaraları
            GraphicsState trolleySt = g.Save();
            g.TranslateTransform(trolley.X, trolley.Y);
            g.RotateTransform(cableAngleDeg);
            using (SolidBrush trolleyBrush = new SolidBrush(Color.FromArgb(245, 71, 85, 105)))
            using (SolidBrush wheelBrush = new SolidBrush(Color.FromArgb(250, 234, 179, 8)))
            {
                g.FillRectangle(trolleyBrush, -7f, -4f, 14f, 5f);
                g.FillEllipse(wheelBrush, -6f, -3.5f, 4f, 4f);
                g.FillEllipse(wheelBrush, 2f, -3.5f, 4f, 4f);
            }
            g.Restore(trolleySt);

            // Sallanan Askı Kolu ve Kabin
            GraphicsState cabinSt = g.Save();
            g.TranslateTransform(trolley.X, trolley.Y);
            g.RotateTransform(_cableCarSwingAngle);

            using (Pen hangerPen = new Pen(Color.FromArgb(240, 100, 116, 139), 2.2f))
            {
                g.DrawLine(hangerPen, 0f, 0f, 0f, 13f);
                g.DrawLine(hangerPen, -5f, 13f, 5f, 13f);
            }

            // Kabin içi sıcak sarı ışık halesi (Per-Pixel Alpha)
            if (!PerformanceMode)
            {
                using (SolidBrush cabinGlow = new SolidBrush(Color.FromArgb(48, 253, 224, 71)))
                    g.FillEllipse(cabinGlow, -22f, 6f, 44f, 36f);
            }

            // Kırmızı Teleferik Kabin Gövdesi
            RectangleF cabinRect = new RectangleF(-15f, 13f, 30f, 20f);
            using (GraphicsPath cabinPath = GraphicsExt.CreateRoundedRect(cabinRect, 5f))
            using (LinearGradientBrush cabinBrush = new LinearGradientBrush(
                cabinRect,
                Color.FromArgb(252, 239, 68, 68),
                Color.FromArgb(252, 185, 28, 28),
                LinearGradientMode.Vertical))
            using (Pen cabinBorder = new Pen(Color.FromArgb(240, 127, 29, 29), 1.3f))
            {
                g.FillPath(cabinBrush, cabinPath);
                g.DrawPath(cabinBorder, cabinPath);
            }

            // Kabin Alt Sarı/Beyaz Şerit
            using (SolidBrush stripeBrush = new SolidBrush(Color.FromArgb(240, 254, 240, 138)))
                g.FillRectangle(stripeBrush, -14f, 27f, 28f, 2.2f);

            // Panoramik Pencereler (İçeride minik kardan adam yolcu silüeti ile!)
            using (SolidBrush winGlass = new SolidBrush(Color.FromArgb(240, 254, 249, 195)))
            using (Pen winFrame = new Pen(Color.FromArgb(220, 127, 29, 29), 1f))
            {
                g.FillRectangle(winGlass, -11.5f, 16f, 10f, 8.5f);
                g.DrawRectangle(winFrame, -11.5f, 16f, 10f, 8.5f);

                g.FillRectangle(winGlass, 1.5f, 16f, 10f, 8.5f);
                g.DrawRectangle(winFrame, 1.5f, 16f, 10f, 8.5f);

                // Sol pencerede seyahat eden minik kardan adam yolcu kafası
                g.FillEllipse(_alphaWhiteBrushes[250], -8.5f, 18f, 5.5f, 5.5f);
                g.FillEllipse(_coalBrush, -7f, 19.5f, 1.3f, 1.3f);
                g.FillEllipse(_coalBrush, -5f, 19.5f, 1.3f, 1.3f);
            }

            // Kabin Üstündeki Beyaz Kar Şapkası
            using (SolidBrush roofSnow = new SolidBrush(Color.FromArgb(250, 248, 250, 252)))
            {
                g.FillEllipse(roofSnow, -14f, 11f, 28f, 4.5f);
            }

            g.Restore(cabinSt);
        }

        private void DrawCableTower(Graphics g, float x, float y, float h)
        {
            float topY = y - h;

            // A-Çerçeveli Çelik Kule Direği
            using (Pen legPen = new Pen(Color.FromArgb(245, 71, 85, 105), 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (Pen bracePen = new Pen(Color.FromArgb(220, 100, 116, 139), 1.4f))
            using (SolidBrush baseBrush = new SolidBrush(Color.FromArgb(245, 51, 65, 85)))
            {
                // Taban beton kaidesi
                g.FillRectangle(baseBrush, x - 10f, y - 4f, 20f, 5f);

                // İki yan çelik ayak ve çapraz kirişler
                g.DrawLine(legPen, x - 8f, y - 3f, x - 2f, topY);
                g.DrawLine(legPen, x + 8f, y - 3f, x + 2f, topY);
                g.DrawLine(bracePen, x - 6f, y - 10f, x + 5f, y - 17f);
                g.DrawLine(bracePen, x + 6f, y - 10f, x - 5f, y - 17f);
                g.DrawLine(bracePen, x - 7f, y - 10f, x + 7f, y - 10f);
            }

            // Üst Dönen Makara Çarkı (Bullwheel)
            float wr = 6.5f;
            using (SolidBrush wheelFill = new SolidBrush(Color.FromArgb(230, 30, 41, 59)))
            using (Pen wheelRim = new Pen(Color.FromArgb(250, 239, 68, 68), 2.2f))
            using (Pen spokePen = new Pen(Color.FromArgb(220, 203, 213, 225), 1.2f))
            {
                g.FillEllipse(wheelFill, x - wr, topY - wr, wr * 2f, wr * 2f);
                g.DrawEllipse(wheelRim, x - wr, topY - wr, wr * 2f, wr * 2f);

                double a1 = _cableWheelAngle * Math.PI / 180.0;
                double a2 = a1 + Math.PI * 0.5;
                g.DrawLine(spokePen,
                    x - (float)Math.Cos(a1) * (wr - 1f), topY - (float)Math.Sin(a1) * (wr - 1f),
                    x + (float)Math.Cos(a1) * (wr - 1f), topY + (float)Math.Sin(a1) * (wr - 1f));
                g.DrawLine(spokePen,
                    x - (float)Math.Cos(a2) * (wr - 1f), topY - (float)Math.Sin(a2) * (wr - 1f),
                    x + (float)Math.Cos(a2) * (wr - 1f), topY + (float)Math.Sin(a2) * (wr - 1f));
            }

            // Kule Tepe Uyarı Işığı
            bool blink = ((_frameCount / 20) % 2) == 0;
            if (blink && !PerformanceMode)
            {
                using (SolidBrush beaconGlow = new SolidBrush(Color.FromArgb(65, 56, 189, 248)))
                    g.FillEllipse(beaconGlow, x - 7f, topY - wr - 9f, 14f, 14f);
            }
            using (SolidBrush beaconCore = new SolidBrush(blink ? Color.FromArgb(255, 56, 189, 248) : Color.FromArgb(200, 14, 116, 144)))
                g.FillEllipse(beaconCore, x - 2.2f, topY - wr - 4f, 4.4f, 4.4f);
        }

        private void DrawCampfire(Graphics g, CampfireEntity cf)
        {
            float x = cf.X;
            float y = cf.Y;
            float pulse = (float)Math.Sin(cf.FlamePhase) * 2.5f;

            // Sıcak turuncu ışık halesi (Per-Pixel Alpha)
            if (!PerformanceMode)
            {
                using (SolidBrush outerGlow = new SolidBrush(Color.FromArgb(38, 251, 146, 60)))
                using (SolidBrush midGlow = new SolidBrush(Color.FromArgb(85, 249, 115, 22)))
                {
                    g.FillEllipse(outerGlow, x - 34f - pulse, y - 42f - pulse, 68f + pulse * 2f, 54f + pulse * 2f);
                    g.FillEllipse(midGlow, x - 18f, y - 28f, 36f, 30f);
                }
            }

            // Çapraz odun kütükleri
            using (Pen logPen = new Pen(Color.FromArgb(250, 120, 53, 15), 5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(logPen, x - 13f, y - 2f, x + 13f, y - 8f);
                g.DrawLine(logPen, x - 13f, y - 8f, x + 13f, y - 2f);
            }

            // Alev katmanları
            PointF[] outerFlame = {
                new PointF(x - 10f, y - 5f),
                new PointF(x - 6f + pulse * 0.4f, y - 22f - pulse),
                new PointF(x + pulse * 0.7f, y - 30f - pulse),
                new PointF(x + 7f + pulse * 0.3f, y - 20f - pulse),
                new PointF(x + 10f, y - 5f)
            };
            using (SolidBrush flameOrange = new SolidBrush(Color.FromArgb(245, 249, 115, 22)))
            using (SolidBrush flameYellow = new SolidBrush(Color.FromArgb(250, 253, 224, 71)))
            {
                g.FillClosedCurve(flameOrange, outerFlame);
                g.FillEllipse(flameYellow, x - 5f, y - 19f - pulse * 0.5f, 10f, 13f);
            }
        }

        private void DrawFloatingPopup(Graphics g, FloatingPopup fp)
        {
            byte a = (byte)Math.Max(20, Math.Min(255, (fp.Ttl * 255) / Math.Max(1, fp.MaxTtl)));
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb((int)(a * 0.7f), 15, 23, 42)))
            using (SolidBrush fg = new SolidBrush(Color.FromArgb(a, fp.Color.R, fp.Color.G, fp.Color.B)))
            {
                g.DrawString(fp.Text, PopupFont, shadow, fp.X - 34f, fp.Y + 1f);
                g.DrawString(fp.Text, PopupFont, fg, fp.X - 35f, fp.Y);
            }
        }

        private void DrawBlizzardWindStreaks(Graphics g)
        {
            float vx = WindNx * 18f;
            float vy = Math.Max(2.5f, WindNy * 8f + 3.5f);
            using (Pen streakPen = new Pen(Color.FromArgb(36, 219, 234, 254), 1.4f))
            {
                for (int i = 0; i < 22; i++)
                {
                    float sx = ((i * 197 + _frameCount * (int)(vx * 1.6f)) % _screenW + _screenW) % _screenW;
                    float sy = ((i * 131 + _frameCount * (int)(vy * 2.2f)) % _screenH + _screenH) % _screenH;
                    g.DrawLine(streakPen, sx, sy, sx + vx * 3.2f, sy + vy * 3.2f);
                }
            }
        }

        private void DrawSnowPlowWithFrontSnowMound(Graphics g, SnowPlowEntity plow)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(plow.X, plow.Y);
            g.RotateTransform(plow.Angle);

            int d = plow.Direction;

            // 1. BIÇAK ÖNÜNDE BİRİKEN DİNAMİK KAR KÜTLESİ
            if (plow.PlowedSnowMass >= 3f)
            {
            float moundScale = Math.Max(0.15f, Math.Min(1.85f, plow.PlowedSnowMass / 38f));
            float mw = 18f * moundScale;
            float mh = 16f * moundScale;
            float bx = d * 21f;

            PointF[] snowMound = {
                new PointF(bx, -1f),
                new PointF(bx + d * 3f, -mh * 0.95f),
                new PointF(bx + d * (mw * 0.65f), -mh * 0.82f),
                new PointF(bx + d * mw, -mh * 0.32f),
                new PointF(bx + d * (mw + 4f), 1f)
            };

            RectangleF moundRect = new RectangleF(
                Math.Min(bx, bx + d * (mw + 6f)) - 4f,
                -mh - 6f,
                mw + 14f,
                mh + 10f);

            using (LinearGradientBrush moundBrush = new LinearGradientBrush(
                moundRect,
                Color.FromArgb(255, 255, 255, 255),
                Color.FromArgb(245, 186, 218, 252),
                LinearGradientMode.Vertical))
            {
                g.FillClosedCurve(moundBrush, snowMound, FillMode.Alternate, 0.35f);
            }
            g.DrawClosedCurve(_snowHighlightPen, snowMound, 0.35f, FillMode.Alternate);

            // Bıçağın üst kısmında kıvrılan ekstra kar topakları
            float rollR = 4.5f * moundScale;
            DrawShadedSnowSphere(g, bx + d * (mw * 0.45f), -mh * 0.62f, rollR);
            DrawShadedSnowSphere(g, bx + d * (mw * 0.78f), -mh * 0.28f, rollR * 0.85f);
            }

            // 2. FAR HÜZMESİ (Sıcak sarı yarı saydam ışık konisi)
            PointF[] headlightCone = {
                new PointF(d * 16f, -12f),
                new PointF(d * 68f, -26f),
                new PointF(d * 68f, 2f)
            };
            using (SolidBrush beamBrush = new SolidBrush(Color.FromArgb(32, 254, 240, 138)))
            {
                g.FillPolygon(beamBrush, headlightCone);
            }

            // 3. KAR KÜREME ARACI GÖVDESİ VE METALİK BIÇAĞI
            using (SolidBrush bodyBrush = new SolidBrush(Color.FromArgb(252, 234, 179, 8)))
            using (SolidBrush cabBrush = new SolidBrush(Color.FromArgb(252, 250, 204, 21)))
            using (SolidBrush glassBrush = new SolidBrush(Color.FromArgb(235, 186, 230, 253)))
            using (SolidBrush bladeBrush = new SolidBrush(Color.FromArgb(250, 148, 163, 184)))
            using (SolidBrush tireBrush = new SolidBrush(Color.FromArgb(252, 30, 41, 59)))
            using (Pen darkOutline = new Pen(Color.FromArgb(220, 161, 98, 7), 1.2f))
            {
                // Ana Şasi
                g.FillRectangle(bodyBrush, -18f, -15f, 36f, 11f);
                g.DrawRectangle(darkOutline, -18, -15, 36, 11);

                // Kabin
                float cabX = (d > 0) ? -9f : -11f;
                g.FillRectangle(cabBrush, cabX, -24f, 20f, 9f);
                g.DrawRectangle(darkOutline, (int)cabX, -24, 20, 9);

                // Ön Cam
                float winX = (d > 0) ? 1f : -9f;
                g.FillRectangle(glassBrush, winX, -22.5f, 8f, 6.5f);

                // Egzoz Borusu
                float exX = -d * 12f;
                g.FillRectangle(tireBrush, exX - 1.5f, -23f, 3f, 8f);

                // Tepe Lambası (Gerçek alfa parlamalı)
                bool flashOn = ((_frameCount / 9) % 2) == 0;
                if (flashOn)
                {
                    using (SolidBrush glow = new SolidBrush(Color.FromArgb(85, 239, 68, 68)))
                        g.FillEllipse(glow, -9f, -34f, 18f, 18f);
                }
                using (SolidBrush lamp = new SolidBrush(flashOn ? Color.FromArgb(255, 239, 68, 68) : Color.FromArgb(220, 252, 165, 165)))
                    g.FillEllipse(lamp, -3.5f, -28f, 7f, 5f);

                // Hidrolik Kol ve Kavisli Kar Küreme Bıçağı
                using (Pen armPen = new Pen(Color.FromArgb(245, 71, 85, 105), 3f))
                {
                    g.DrawLine(armPen, d * 15f, -9f, d * 21f, -8f);
                }

                PointF[] blade = {
                    new PointF(d * 18f, -16f),
                    new PointF(d * 24f, -19f),
                    new PointF(d * 25f, -1f),
                    new PointF(d * 18f, -3f)
                };
                g.FillPolygon(bladeBrush, blade);

                // Paletli Tekerlekler (3 adet)
                float[] wheelOffsets = { -11.5f, 0f, 11.5f };
                for (int i = 0; i < wheelOffsets.Length; i++)
                {
                    float wx = wheelOffsets[i];
                    g.FillEllipse(tireBrush, wx - 5f, -9f, 10f, 10f);
                    g.FillEllipse(bodyBrush, wx - 2f, -6f, 4f, 4f);
                }
            }

            g.Restore(state);
        }

        private static readonly Color[] LightPalette = {
            Color.FromArgb(255, 253, 224, 71),
            Color.FromArgb(255, 251, 146, 60),
            Color.FromArgb(255, 248, 113, 113),
            Color.FromArgb(255, 125, 211, 252),
            Color.FromArgb(255, 167, 243, 208)
        };

        private void DrawWindowFairyLights(Graphics g, WindowSurface surf)
        {
            int count = 0;
            for (int lx = surf.Left + 22; lx < surf.Right - 18; lx += 38)
                count++;
            if (count < 2) return;

            PointF[] pts;
            if (!_wireBuffers.TryGetValue(count, out pts))
            {
                pts = new PointF[count];
                _wireBuffers[count] = pts;
            }

            float swingX = (float)Math.Sin(surf.LightSwingAngle) * 6f;
            int colorShift = _frameCount / 24;
            int idx = 0;

            GraphicsState lightClip = g.Save();
            g.SetClip(new Rectangle(surf.Left, Math.Max(0, surf.Top - 12), surf.Width, 42), CombineMode.Intersect);

            for (int lx = surf.Left + 22; lx < surf.Right - 18 && idx < count; lx += 38)
            {
                float sag = (idx % 2 == 0) ? 5.5f : 11f;
                float bx = Math.Max(surf.Left + 6f, Math.Min(surf.Right - 6f, lx + swingX * ((idx % 2 == 0) ? 0.6f : 1.0f)));
                float by = surf.Top + sag;
                pts[idx] = new PointF(bx, by);

                int ci = (idx + colorShift) % LightPalette.Length;
                if (!PerformanceMode)
                {
                    float glowBoost = AudioReactiveEnabled ? (1f + _audioLevelSmooth * 0.45f + _audioBeat * 0.55f) : 1f;
                    float gw = 19f * glowBoost;
                    g.FillEllipse(_lightGlowBrushes[ci], bx - gw * 0.5f, by - gw * 0.5f + 2f, gw, gw);
                }
                g.FillEllipse(_lightMidBrushes[ci], bx - 5.2f, by - 3.2f, 10.4f, 10.4f);
                g.FillEllipse(_lightCoreBrush, bx - 2.4f, by - 0.4f, 4.8f, 4.8f);
                idx++;
            }

            g.DrawCurve(_wirePen, pts, 0.35f);
            g.Restore(lightClip);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_renderTimer != null) _renderTimer.Dispose();
                if (_audioSynth != null) _audioSynth.Dispose();
                if (_audioMeter != null) _audioMeter.Dispose();
                if (_surfaceGraphics != null) _surfaceGraphics.Dispose();
                if (_surfaceBitmap != null) _surfaceBitmap.Dispose();
                lock (_auroraLock)
                {
                    _auroraDisposed = true;
                    _auroraHasFrame = false;
                    _auroraFront = null;
                    _auroraSpare = null;
                    _auroraTmpHoriz = null;
                }
                for (int i = 0; i < 256; i++)
                {
                    if (_alphaWhiteBrushes[i] != null) _alphaWhiteBrushes[i].Dispose();
                    if (_alphaIceBrushes[i] != null) _alphaIceBrushes[i].Dispose();
                }
                _wirePen.Dispose();
                _snowHighlightPen.Dispose();
                _snowOutlinePen.Dispose();
                _coalBrush.Dispose();
                _carrotBrush.Dispose();
                for (int i = 0; i < 256; i++)
                {
                    if (_alphaYellowBrushes[i] != null) _alphaYellowBrushes[i].Dispose();
                }
                if (_lightGlowBrushes != null)
                    for (int i = 0; i < _lightGlowBrushes.Length; i++) _lightGlowBrushes[i].Dispose();
                if (_lightMidBrushes != null)
                    for (int i = 0; i < _lightMidBrushes.Length; i++) _lightMidBrushes[i].Dispose();
                if (_lightCoreBrush != null) _lightCoreBrush.Dispose();
            }

            if (_hookSystem != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_hookSystem);
                _hookSystem = IntPtr.Zero;
            }
            if (_hookObject != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_hookObject);
                _hookObject = IntPtr.Zero;
            }

            if (_memDc != IntPtr.Zero)
            {
                if (_oldBitmap != IntPtr.Zero)
                    NativeMethods.SelectObject(_memDc, _oldBitmap);
                if (_dibBitmap != IntPtr.Zero)
                    NativeMethods.DeleteObject(_dibBitmap);
                NativeMethods.DeleteDC(_memDc);
                _memDc = IntPtr.Zero;
            }
            if (_screenDc != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
                _screenDc = IntPtr.Zero;
            }

            base.Dispose(disposing);
        }
    }

    // ========================================================================
    // 10. MODERN FLUENT KONTROL PANELİ, ÖZEL BİLEŞENLER VE ÇİFT DİL (TR / EN)
    // ========================================================================
    internal static class GraphicsExt
    {
        public static GraphicsPath CreateRoundedRect(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2f;
            if (d <= 0f)
            {
                path.AddRectangle(bounds);
                return path;
            }
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class ModernCard : Panel
    {
        public int CornerRadius { get; set; }
        public Color FillColor { get; set; }
        public Color BorderColor { get; set; }

        public ModernCard()
        {
            DoubleBuffered = true;
            CornerRadius = 12;
            FillColor = Color.FromArgb(20, 30, 48);
            BorderColor = Color.FromArgb(38, 52, 74);
            BackColor = Color.FromArgb(11, 17, 30);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = GraphicsExt.CreateRoundedRect(rect, CornerRadius))
            using (SolidBrush fill = new SolidBrush(FillColor))
            using (Pen border = new Pen(BorderColor, 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
        }
    }

    internal sealed class ModernButton : Control
    {
        private bool _hovered;
        private bool _pressed;

        public int CornerRadius { get; set; }
        public Color FillColor { get; set; }
        public Color BorderColor { get; set; }

        public ModernButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true;
            CornerRadius = 8;
            FillColor = Color.FromArgb(26, 38, 58);
            BorderColor = Color.FromArgb(46, 62, 88);
            ForeColor = Color.FromArgb(241, 245, 249);
            BackColor = Color.Transparent;
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
            Cursor = Cursors.Hand;
        }

        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Color bg = FillColor;
            if (_pressed)
                bg = Color.FromArgb(Math.Max(0, bg.R - 12), Math.Max(0, bg.G - 12), Math.Max(0, bg.B - 12));
            else if (_hovered)
                bg = Color.FromArgb(Math.Min(255, bg.R + 16), Math.Min(255, bg.G + 18), Math.Min(255, bg.B + 22));

            RectangleF rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = GraphicsExt.CreateRoundedRect(rect, CornerRadius))
            using (SolidBrush fill = new SolidBrush(bg))
            using (Pen border = new Pen(_hovered ? Color.FromArgb(90, 120, 165) : BorderColor, 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            TextRenderer.DrawText(
                g, Text, Font,
                new Rectangle(4, 0, Width - 8, Height),
                ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class ModernSlider : Control
    {
        private int _min = 0;
        private int _max = 100;
        private int _value = 50;
        private bool _dragging;
        private bool _hovered;

        public int Minimum { get { return _min; } set { _min = value; if (_value < _min) _value = _min; Invalidate(); } }
        public int Maximum { get { return _max; } set { _max = value; if (_value > _max) _value = _max; Invalidate(); } }
        public int Value
        {
            get { return _value; }
            set
            {
                int clamped = Math.Max(_min, Math.Min(_max, value));
                if (clamped != _value)
                {
                    _value = clamped;
                    Invalidate();
                }
            }
        }

        public event EventHandler Scroll;

        public ModernSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true;
            BackColor = Color.Transparent;
            Size = new Size(272, 22);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                UpdateFromX(e.X);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) UpdateFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Invalidate();
        }

        private void UpdateFromX(int mx)
        {
            float pad = 8f;
            float trackW = Math.Max(10f, Width - pad * 2f);
            float t = Math.Max(0f, Math.Min(1f, (mx - pad) / trackW));
            int nextVal = _min + (int)Math.Round(t * (_max - _min));
            if (nextVal != _value)
            {
                _value = nextVal;
                Invalidate();
                if (Scroll != null) Scroll(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float pad = 8f;
            float cy = Height * 0.5f;
            float trackW = Math.Max(10f, Width - pad * 2f);
            float trackH = 5f;
            float ratio = (_max > _min) ? (float)(_value - _min) / (_max - _min) : 0f;
            float thumbX = pad + ratio * trackW;

            // Arka plan yolu
            RectangleF bgRect = new RectangleF(pad, cy - trackH * 0.5f, trackW, trackH);
            using (GraphicsPath bgPath = GraphicsExt.CreateRoundedRect(bgRect, trackH * 0.5f))
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(30, 41, 59)))
                g.FillPath(bgBrush, bgPath);

            // Aktif dolgu yolu (Gradyan)
            if (ratio > 0.005f)
            {
                float activeW = Math.Max(trackH, ratio * trackW);
                RectangleF actRect = new RectangleF(pad, cy - trackH * 0.5f, activeW, trackH);
                using (GraphicsPath actPath = GraphicsExt.CreateRoundedRect(actRect, trackH * 0.5f))
                using (LinearGradientBrush grad = new LinearGradientBrush(
                    actRect,
                    Color.FromArgb(56, 189, 248),
                    Color.FromArgb(59, 130, 246),
                    LinearGradientMode.Horizontal))
                {
                    g.FillPath(grad, actPath);
                }
            }

            // Başparmak (Thumb) & Hover halkası
            float r = (_dragging || _hovered) ? 7.2f : 6.2f;
            if (_dragging || _hovered)
            {
                using (SolidBrush halo = new SolidBrush(Color.FromArgb(55, 56, 189, 248)))
                    g.FillEllipse(halo, thumbX - 10.5f, cy - 10.5f, 21f, 21f);
            }
            using (SolidBrush thumbBrush = new SolidBrush(Color.FromArgb(248, 250, 252)))
            using (Pen thumbRing = new Pen(Color.FromArgb(56, 189, 248), 2f))
            {
                g.FillEllipse(thumbBrush, thumbX - r, cy - r, r * 2f, r * 2f);
                g.DrawEllipse(thumbRing, thumbX - r, cy - r, r * 2f, r * 2f);
            }
        }
    }

    internal sealed class ModernToggle : Control
    {
        private bool _checked;
        public bool Checked
        {
            get { return _checked; }
            set { if (_checked != value) { _checked = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }
        }

        public event EventHandler CheckedChanged;

        public ModernToggle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true;
            BackColor = Color.Transparent;
            Size = new Size(272, 24);
            Font = new Font("Segoe UI", 8.5f);
            ForeColor = Color.FromArgb(203, 213, 225);
            Cursor = Cursors.Hand;
        }

        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
                Checked = !Checked;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            float sw = 36f;
            float sh = 19f;
            float sx = Width - sw - 2f;
            float sy = (Height - sh) * 0.5f;

            RectangleF pillRect = new RectangleF(sx, sy, sw, sh);
            using (GraphicsPath pillPath = GraphicsExt.CreateRoundedRect(pillRect, sh * 0.5f))
            using (SolidBrush pillBrush = new SolidBrush(_checked ? Color.FromArgb(14, 165, 233) : Color.FromArgb(51, 65, 85)))
            {
                g.FillPath(pillBrush, pillPath);
            }

            float knobSize = 13f;
            float kx = _checked ? (sx + sw - knobSize - 3f) : (sx + 3f);
            float ky = sy + (sh - knobSize) * 0.5f;
            using (SolidBrush knobBrush = new SolidBrush(Color.White))
                g.FillEllipse(knobBrush, kx, ky, knobSize, knobSize);

            TextRenderer.DrawText(
                g, Text, Font,
                new Rectangle(0, 0, (int)sx - 8, Height),
                ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class WindRadarControl : Control
    {
        public float Nx { get; set; }
        public float Ny { get; set; }
        public event Action<float, float> OnWindChanged;

        public WindRadarControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true;
            BackColor = Color.Transparent;
            Size = new Size(116, 116);
            Cursor = Cursors.Cross;
            Nx = -0.22f;
            Ny = 0.08f;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) UpdateVector(e.X, e.Y);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button == MouseButtons.Left) UpdateVector(e.X, e.Y);
        }

        private void UpdateVector(int mx, int my)
        {
            float cx = Width * 0.5f;
            float cy = Height * 0.5f;
            float maxR = (Width * 0.5f) - 12f;
            float dx = (mx - cx) / maxR;
            float dy = (my - cy) / maxR;
            float mag = (float)Math.Sqrt(dx * dx + dy * dy);
            if (mag > 1f)
            {
                dx /= mag;
                dy /= mag;
            }
            Nx = dx;
            Ny = dy;
            Invalidate();
            if (OnWindChanged != null)
                OnWindChanged(Nx, Ny);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float cx = Width * 0.5f;
            float cy = Height * 0.5f;
            float maxR = (Width * 0.5f) - 10f;

            // Dairesel modern radar arka planı
            RectangleF outerCircle = new RectangleF(cx - maxR, cy - maxR, maxR * 2f, maxR * 2f);
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(12, 19, 34)))
                g.FillEllipse(bgBrush, outerCircle);

            using (Pen ringPen = new Pen(Color.FromArgb(40, 56, 80), 1f))
            {
                g.DrawEllipse(ringPen, outerCircle);
                g.DrawEllipse(ringPen, cx - maxR * 0.66f, cy - maxR * 0.66f, maxR * 1.32f, maxR * 1.32f);
                g.DrawEllipse(ringPen, cx - maxR * 0.33f, cy - maxR * 0.33f, maxR * 0.66f, maxR * 0.66f);
                g.DrawLine(ringPen, cx - maxR, cy, cx + maxR, cy);
                g.DrawLine(ringPen, cx, cy - maxR, cx, cy + maxR);
            }

            float tx = cx + Nx * (maxR - 4f);
            float ty = cy + Ny * (maxR - 4f);

            using (SolidBrush centerBrush = new SolidBrush(Color.FromArgb(148, 163, 184)))
                g.FillEllipse(centerBrush, cx - 3f, cy - 3f, 6f, 6f);

            using (Pen arrowGlow = new Pen(Color.FromArgb(65, 56, 189, 248), 6f) { StartCap = LineCap.Round })
                g.DrawLine(arrowGlow, cx, cy, tx, ty);

            using (Pen arrowPen = new Pen(Color.FromArgb(96, 165, 250), 2.8f))
            {
                arrowPen.StartCap = LineCap.Round;
                arrowPen.CustomEndCap = new AdjustableArrowCap(4.5f, 5.5f, true);
                g.DrawLine(arrowPen, cx, cy, tx, ty);
            }
        }
    }

    // ========================================================================
    // 10a. MODERN KARDAN ADAM GARDIROBU PENCERESİ & ÖZEL MODERN BİLEŞENLER
    // ========================================================================
    internal sealed class SegmentedTabBar : Control
    {
        private string[] _tabs = new string[0];
        private int _selectedIndex;
        private int _hoverIndex = -1;

        public event Action<int> SelectedIndexChanged;

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                if (_tabs.Length == 0) return;
                int clamped = Math.Max(0, Math.Min(_tabs.Length - 1, value));
                if (clamped != _selectedIndex)
                {
                    _selectedIndex = clamped;
                    Invalidate();
                    if (SelectedIndexChanged != null) SelectedIndexChanged(_selectedIndex);
                }
            }
        }

        public SegmentedTabBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true;
            BackColor = Color.Transparent;
            Font = new Font("Segoe UI Semibold", 8.4f, FontStyle.Bold);
            Cursor = Cursors.Hand;
        }

        public void SetTabs(string[] labels)
        {
            _tabs = labels ?? new string[0];
            if (_selectedIndex >= _tabs.Length) _selectedIndex = 0;
            Invalidate();
        }

        private int HitTest(int x)
        {
            if (_tabs.Length == 0 || Width <= 0) return -1;
            int idx = (x * _tabs.Length) / Width;
            return Math.Max(0, Math.Min(_tabs.Length - 1, idx));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.X);
            if (h != _hoverIndex) { _hoverIndex = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverIndex = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                int idx = HitTest(e.X);
                if (idx >= 0) SelectedIndex = idx;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            RectangleF track = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath tp = GraphicsExt.CreateRoundedRect(track, 10f))
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(14, 22, 38)))
            using (Pen tpen = new Pen(Color.FromArgb(36, 52, 78), 1f))
            {
                g.FillPath(tb, tp);
                g.DrawPath(tpen, tp);
            }

            if (_tabs.Length == 0) return;
            float segW = (Width - 6f) / _tabs.Length;
            for (int i = 0; i < _tabs.Length; i++)
            {
                RectangleF segRect = new RectangleF(3f + i * segW, 3f, segW, Height - 6f);
                if (i == _selectedIndex)
                {
                    using (GraphicsPath sp = GraphicsExt.CreateRoundedRect(segRect, 8f))
                    using (LinearGradientBrush sb = new LinearGradientBrush(
                        segRect,
                        Color.FromArgb(14, 165, 233),
                        Color.FromArgb(59, 130, 246),
                        LinearGradientMode.Horizontal))
                    {
                        g.FillPath(sb, sp);
                    }
                }
                else if (i == _hoverIndex)
                {
                    using (GraphicsPath sp = GraphicsExt.CreateRoundedRect(segRect, 8f))
                    using (SolidBrush hb = new SolidBrush(Color.FromArgb(26, 40, 64)))
                        g.FillPath(hb, sp);
                }

                Color txtCol = (i == _selectedIndex) ? Color.White : Color.FromArgb(160, 178, 202);
                TextRenderer.DrawText(
                    g, _tabs[i], Font,
                    Rectangle.Round(segRect),
                    txtCol,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }

    internal sealed class OptionChipSelector : Control
    {
        private string[] _items = new string[0];
        private int _columns = 3;
        private int _selectedIndex;
        private int _hoverIndex = -1;

        public event Action<int> SelectedIndexChanged;

        public int Columns
        {
            get { return _columns; }
            set { _columns = Math.Max(1, value); Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                if (_selectedIndex != value)
                {
                    _selectedIndex = value;
                    Invalidate();
                }
            }
        }

        public OptionChipSelector()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true;
            BackColor = Color.Transparent;
            Font = new Font("Segoe UI Semibold", 8.1f, FontStyle.Bold);
            Cursor = Cursors.Hand;
        }

        public void SetItems(string[] items, int columns)
        {
            _items = items ?? new string[0];
            _columns = Math.Max(1, columns);
            Invalidate();
        }

        private RectangleF GetChipRect(int index)
        {
            const float gap = 6f;
            int rows = Math.Max(1, (_items.Length + _columns - 1) / _columns);
            float cellW = (Width - gap * (_columns - 1)) / _columns;
            float cellH = Math.Min(32f, (Height - gap * (rows - 1)) / rows);
            int col = index % _columns;
            int row = index / _columns;
            return new RectangleF(col * (cellW + gap), row * (cellH + gap), cellW, cellH);
        }

        private int HitTest(int mx, int my)
        {
            for (int i = 0; i < _items.Length; i++)
            {
                if (GetChipRect(i).Contains(mx, my)) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.X, e.Y);
            if (h != _hoverIndex) { _hoverIndex = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverIndex = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                int idx = HitTest(e.X, e.Y);
                if (idx >= 0)
                {
                    _selectedIndex = idx;
                    Invalidate();
                    if (SelectedIndexChanged != null) SelectedIndexChanged(idx);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            for (int i = 0; i < _items.Length; i++)
            {
                RectangleF r = GetChipRect(i);
                r.Width -= 1f;
                r.Height -= 1f;
                bool active = (i == _selectedIndex);
                bool hover = (i == _hoverIndex);

                Color fill = active
                    ? Color.FromArgb(24, 58, 92)
                    : (hover ? Color.FromArgb(30, 44, 68) : Color.FromArgb(20, 30, 48));
                Color border = active
                    ? Color.FromArgb(56, 189, 248)
                    : (hover ? Color.FromArgb(74, 98, 134) : Color.FromArgb(38, 52, 74));

                using (GraphicsPath path = GraphicsExt.CreateRoundedRect(r, 8f))
                using (SolidBrush fb = new SolidBrush(fill))
                using (Pen bp = new Pen(border, active ? 1.5f : 1f))
                {
                    g.FillPath(fb, path);
                    g.DrawPath(bp, path);
                }

                Color textCol = active ? Color.FromArgb(224, 242, 254) : Color.FromArgb(203, 213, 225);
                TextRenderer.DrawText(
                    g, _items[i], Font,
                    Rectangle.Round(r),
                    textCol,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }

    internal sealed class ColorSwatchBar : Control
    {
        private Color[] _colors = SnowmanEntity.WardrobePalette;
        private int _selectedIndex;
        private int _hoverIndex = -1;

        public event Action<Color> ColorSelected;

        public ColorSwatchBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true;
            BackColor = Color.Transparent;
            Size = new Size(292, 32);
            Cursor = Cursors.Hand;
        }

        public void SetPalette(Color[] palette)
        {
            if (palette != null && palette.Length > 0)
            {
                _colors = palette;
                if (_selectedIndex >= _colors.Length) _selectedIndex = 0;
                Invalidate();
            }
        }

        public void SelectClosestColor(Color c)
        {
            int best = 0;
            int bestDist = int.MaxValue;
            for (int i = 0; i < _colors.Length; i++)
            {
                int dr = _colors[i].R - c.R;
                int dg = _colors[i].G - c.G;
                int db = _colors[i].B - c.B;
                int d = dr * dr + dg * dg + db * db;
                if (d < bestDist) { bestDist = d; best = i; }
            }
            _selectedIndex = best;
            Invalidate();
        }

        private RectangleF GetDotRect(int index)
        {
            float spacing = Width / (float)Math.Max(1, _colors.Length);
            float cx = spacing * (index + 0.5f);
            float cy = Height * 0.5f;
            float r = 10.5f;
            return new RectangleF(cx - r, cy - r, r * 2f, r * 2f);
        }

        private int HitTest(int mx, int my)
        {
            for (int i = 0; i < _colors.Length; i++)
            {
                RectangleF r = GetDotRect(i);
                r.Inflate(4f, 4f);
                if (r.Contains(mx, my)) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.X, e.Y);
            if (h != _hoverIndex) { _hoverIndex = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverIndex = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                int idx = HitTest(e.X, e.Y);
                if (idx >= 0)
                {
                    _selectedIndex = idx;
                    Invalidate();
                    if (ColorSelected != null) ColorSelected(_colors[idx]);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            for (int i = 0; i < _colors.Length; i++)
            {
                RectangleF r = GetDotRect(i);
                bool active = (i == _selectedIndex);
                bool hover = (i == _hoverIndex);

                if (active || hover)
                {
                    RectangleF ring = r;
                    ring.Inflate(3.5f, 3.5f);
                    using (Pen halo = new Pen(active ? Color.FromArgb(245, 56, 189, 248) : Color.FromArgb(140, 148, 163, 184), active ? 2.2f : 1.4f))
                        g.DrawEllipse(halo, ring);
                }

                using (SolidBrush cb = new SolidBrush(_colors[i]))
                using (Pen rim = new Pen(Color.FromArgb(150, 255, 255, 255), 1.1f))
                {
                    g.FillEllipse(cb, r);
                    g.DrawEllipse(rim, r);
                }

                // Parlak cam yansıması
                using (SolidBrush shine = new SolidBrush(Color.FromArgb(95, 255, 255, 255)))
                    g.FillEllipse(shine, r.X + 4f, r.Y + 3f, 6f, 4f);
            }
        }
    }

    internal sealed class SnowmanPreviewCanvas : Control
    {
        public SnowmanEntity Target;
        private readonly PointF[] _snowPts = new PointF[22];
        private readonly float[] _snowSpd = new float[22];
        private float _animTime;
        private Point _mousePos;

        public SnowmanPreviewCanvas()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true;
            BackColor = Color.Transparent;

            Random rng = new Random(42);
            for (int i = 0; i < _snowPts.Length; i++)
            {
                _snowPts[i] = new PointF((float)(rng.NextDouble() * 190.0), (float)(rng.NextDouble() * 240.0));
                _snowSpd[i] = 0.45f + (float)rng.NextDouble() * 0.9f;
            }
            _mousePos = new Point(120, 100);
        }

        public void StepAnimation()
        {
            _animTime += 0.033f;
            for (int i = 0; i < _snowPts.Length; i++)
            {
                _snowPts[i].Y += _snowSpd[i];
                _snowPts[i].X += (float)Math.Sin(_animTime * 1.4f + i) * 0.25f;
                if (_snowPts[i].Y > Height - 16)
                {
                    _snowPts[i].Y = 6f;
                    _snowPts[i].X = (float)((i * 37 + (int)(_animTime * 40)) % Math.Max(10, Width - 12)) + 6f;
                }
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mousePos = e.Location;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            RectangleF card = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath clipPath = GraphicsExt.CreateRoundedRect(card, 14f))
            {
                GraphicsState st = g.Save();
                g.SetClip(clipPath);

                // Kış gecesi gradyanı
                using (LinearGradientBrush sky = new LinearGradientBrush(
                    card,
                    Color.FromArgb(10, 20, 42),
                    Color.FromArgb(20, 35, 64),
                    LinearGradientMode.Vertical))
                {
                    g.FillRectangle(sky, card);
                }

                // Hafif Kuzey Işığı parlaklığı (stüdyo arka ışığı)
                using (LinearGradientBrush aurora = new LinearGradientBrush(
                    new RectangleF(0f, 0f, Width, 84f),
                    Color.FromArgb(50, 16, 185, 129),
                    Color.FromArgb(0, 56, 189, 248),
                    LinearGradientMode.Vertical))
                {
                    g.FillRectangle(aurora, 0f, 0f, Width, 84f);
                }

                // Karlı zemin tepesi
                using (LinearGradientBrush hill = new LinearGradientBrush(
                    new RectangleF(0f, Height - 52f, Width, 56f),
                    Color.FromArgb(235, 224, 242, 254),
                    Color.FromArgb(245, 148, 197, 253),
                    LinearGradientMode.Vertical))
                {
                    g.FillEllipse(hill, -30f, Height - 44f, Width + 60f, 74f);
                }

                // Canlı Kardan Adam Çizimi (Büyük Ölçekli!)
                if (Target != null)
                {
                    float totalH = (Target.R1 + Target.R2 + Target.R3) * 2.1f;
                    float fitScale = Math.Max(1.05f, Math.Min(2.25f, 136f / Math.Max(40f, totalH)));
                    float savedTilt = Target.TiltAngle;
                    Target.TiltAngle = (float)Math.Sin(_animTime * 1.6f) * 2.2f;
                    SnowmanRenderer.DrawSnowman(
                        g, Target,
                        Width * 0.5f, Height - 30f, fitScale,
                        _mousePos.X, _mousePos.Y, _animTime, false);
                    Target.TiltAngle = savedTilt;
                }

                // Ön planda süzülen minik kar taneleri
                using (SolidBrush flakeBrush = new SolidBrush(Color.FromArgb(175, 255, 255, 255)))
                {
                    for (int i = 0; i < _snowPts.Length; i++)
                    {
                        float sz = (i % 3 == 0) ? 3.2f : 2.1f;
                        g.FillEllipse(flakeBrush, _snowPts[i].X, _snowPts[i].Y, sz, sz);
                    }
                }

                g.Restore(st);

                using (Pen border = new Pen(Color.FromArgb(56, 82, 118), 1.2f))
                    g.DrawPath(border, clipPath);
            }
        }
    }

    internal sealed class SnowmanWardrobeForm : Form
    {
        private readonly SnowOverlayForm _overlay;
        public SnowmanEntity TargetSnowman { get; private set; }

        private readonly Label _titleLbl;
        private readonly Label _liveBadge;
        private readonly ModernButton _randomBtn;
        private readonly SnowmanPreviewCanvas _preview;
        private readonly ModernButton[] _presetBtns = new ModernButton[4];
        private readonly ModernButton _deleteBtn;

        private readonly SegmentedTabBar _tabs;
        private readonly Panel[] _tabPanels = new Panel[5];

        // Tab 0: Hat
        private readonly Label _hatStyleLbl;
        private readonly OptionChipSelector _hatChips;
        private readonly Label _hatColorLbl;
        private readonly ColorSwatchBar _hatColors;

        // Tab 1: Face & Glasses
        private readonly Label _glassesLbl;
        private readonly OptionChipSelector _glassesChips;
        private readonly Label _faceLbl;
        private readonly OptionChipSelector _faceChips;

        // Tab 2: Scarf
        private readonly Label _scarfStyleLbl;
        private readonly OptionChipSelector _scarfChips;
        private readonly Label _scarfColorLbl;
        private readonly ColorSwatchBar _scarfColors;

        // Tab 3: Outfit & Arms
        private readonly Label _outfitStyleLbl;
        private readonly OptionChipSelector _outfitChips;
        private readonly Label _armStyleLbl;
        private readonly OptionChipSelector _armChips;
        private readonly Label _outfitColorLbl;
        private readonly ColorSwatchBar _outfitColors;

        // Tab 4: Name Tag & Color
        private readonly Label _nameInputLbl;
        private readonly TextBox _nameBox;
        private readonly ModernButton _clearNameBtn;
        private readonly Label _nameColorLbl;
        private readonly ColorSwatchBar _nameColors;
        private readonly Label _quickNameLbl;
        private readonly OptionChipSelector _quickNameChips;
        private bool _suppressNameEvent;

        // Size slider
        private readonly Label _sizeLbl;
        private readonly Label _sizeValLbl;
        private readonly ModernSlider _sizeSlider;

        private readonly Timer _animTimer;
        private double _fadeTarget = 0.97;

        public SnowmanWardrobeForm(SnowOverlayForm overlay, SnowmanEntity target)
        {
            _overlay = overlay;
            TargetSnowman = target;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(548, 444);
            BackColor = Color.FromArgb(11, 17, 30);
            ForeColor = Color.FromArgb(248, 250, 252);
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            Opacity = 0.05;

            // ---- Sürüklenebilir Modern Başlık Çubuğu ----
            Panel header = new Panel
            {
                Location = new Point(0, 3),
                Size = new Size(548, 42),
                BackColor = Color.FromArgb(15, 24, 42)
            };
            MouseEventHandler dragHandler = (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    NativeMethods.ReleaseCapture();
                    NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HT_CAPTION, 0);
                }
            };
            header.MouseDown += dragHandler;
            Controls.Add(header);

            _titleLbl = new Label
            {
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(248, 250, 252),
                BackColor = Color.Transparent,
                Location = new Point(16, 10),
                AutoSize = true
            };
            _titleLbl.MouseDown += dragHandler;
            header.Controls.Add(_titleLbl);

            _liveBadge = new Label
            {
                Font = new Font("Segoe UI", 7.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(52, 211, 153),
                BackColor = Color.FromArgb(14, 48, 42),
                Location = new Point(236, 11),
                Size = new Size(54, 19),
                TextAlign = ContentAlignment.MiddleCenter
            };
            header.Controls.Add(_liveBadge);

            _randomBtn = new ModernButton
            {
                Location = new Point(416, 8),
                Size = new Size(92, 26),
                CornerRadius = 8,
                FillColor = Color.FromArgb(30, 48, 78),
                BorderColor = Color.FromArgb(56, 189, 248),
                ForeColor = Color.FromArgb(186, 230, 253),
                Font = new Font("Segoe UI Semibold", 8.0f, FontStyle.Bold)
            };
            _randomBtn.Click += (s, e) =>
            {
                if (TargetSnowman != null)
                {
                    TargetSnowman.RandomizeOutfit(new Random());
                    TargetSnowman.TiltVel = 8f;
                    SyncControlsFromSnowman();
                }
            };
            header.Controls.Add(_randomBtn);

            ModernButton closeBtn = new ModernButton
            {
                Text = "✕",
                Location = new Point(514, 8),
                Size = new Size(26, 26),
                CornerRadius = 8,
                FillColor = Color.FromArgb(58, 24, 34),
                BorderColor = Color.FromArgb(110, 42, 56),
                ForeColor = Color.FromArgb(252, 165, 165),
                Font = new Font("Segoe UI", 8.2f, FontStyle.Bold)
            };
            closeBtn.Click += (s, e) => Close();
            header.Controls.Add(closeBtn);

            // ---- Sol Sütun: Canlı Animasyonlu Kardan Adam Önizleme Stüdyosu ----
            _preview = new SnowmanPreviewCanvas
            {
                Location = new Point(16, 56),
                Size = new Size(192, 244),
                Target = target
            };
            Controls.Add(_preview);

            // 2x2 Hazır Stil Butonları
            for (int i = 0; i < 4; i++)
            {
                int presetIdx = i;
                int col = i % 2;
                int row = i / 2;
                ModernButton pb = new ModernButton
                {
                    Location = new Point(16 + col * 99, 308 + row * 34),
                    Size = new Size(93, 28),
                    CornerRadius = 8,
                    FillColor = Color.FromArgb(22, 34, 54),
                    BorderColor = Color.FromArgb(46, 66, 98),
                    Font = new Font("Segoe UI Semibold", 7.9f, FontStyle.Bold)
                };
                pb.Click += (s, e) =>
                {
                    if (TargetSnowman != null)
                    {
                        TargetSnowman.ApplyPreset(presetIdx);
                        TargetSnowman.TiltVel = 7f;
                        SyncControlsFromSnowman();
                    }
                };
                _presetBtns[i] = pb;
                Controls.Add(pb);
            }

            _deleteBtn = new ModernButton
            {
                Location = new Point(16, 382),
                Size = new Size(192, 32),
                CornerRadius = 9,
                FillColor = Color.FromArgb(52, 22, 30),
                BorderColor = Color.FromArgb(120, 42, 56),
                ForeColor = Color.FromArgb(252, 165, 165),
                Font = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold)
            };
            _deleteBtn.Click += (s, e) =>
            {
                if (TargetSnowman != null)
                    _overlay.DeleteSnowman(TargetSnowman);
            };
            Controls.Add(_deleteBtn);

            // ---- Sağ Sütun: Modern 5 Sekmeli Gardırop Düzenleyici ----
            const int rx = 222;
            const int rw = 310;

            _tabs = new SegmentedTabBar
            {
                Location = new Point(rx, 56),
                Size = new Size(rw, 34)
            };
            _tabs.SelectedIndexChanged += ShowTab;
            Controls.Add(_tabs);

            for (int i = 0; i < 5; i++)
            {
                _tabPanels[i] = new Panel
                {
                    Location = new Point(rx, 100),
                    Size = new Size(rw, 268),
                    BackColor = Color.FromArgb(11, 17, 30),
                    Visible = (i == 0)
                };
                Controls.Add(_tabPanels[i]);
            }

            // -- TAB 0: Şapka --
            _hatStyleLbl = CreateSectionLabel(0, 0);
            _tabPanels[0].Controls.Add(_hatStyleLbl);

            _hatChips = new OptionChipSelector
            {
                Location = new Point(0, 22),
                Size = new Size(rw, 114)
            };
            _hatChips.SelectedIndexChanged += idx =>
            {
                if (TargetSnowman == null) return;
                if (idx == 0)
                {
                    TargetSnowman.HasHat = false;
                }
                else
                {
                    TargetSnowman.HasHat = true;
                    TargetSnowman.HatStyle = idx - 1;
                }
                TargetSnowman.TiltVel = 4.5f;
            };
            _tabPanels[0].Controls.Add(_hatChips);

            _hatColorLbl = CreateSectionLabel(0, 152);
            _tabPanels[0].Controls.Add(_hatColorLbl);

            _hatColors = new ColorSwatchBar
            {
                Location = new Point(0, 176),
                Size = new Size(rw, 34)
            };
            _hatColors.ColorSelected += c =>
            {
                if (TargetSnowman != null)
                {
                    TargetSnowman.HatColor = c;
                    TargetSnowman.HasHat = true;
                }
            };
            _tabPanels[0].Controls.Add(_hatColors);

            // -- TAB 1: Gözlük & Yüz --
            _glassesLbl = CreateSectionLabel(0, 0);
            _tabPanels[1].Controls.Add(_glassesLbl);

            _glassesChips = new OptionChipSelector
            {
                Location = new Point(0, 22),
                Size = new Size(rw, 76)
            };
            _glassesChips.SelectedIndexChanged += idx =>
            {
                if (TargetSnowman != null) TargetSnowman.GlassesStyle = idx;
            };
            _tabPanels[1].Controls.Add(_glassesChips);

            _faceLbl = CreateSectionLabel(0, 116);
            _tabPanels[1].Controls.Add(_faceLbl);

            _faceChips = new OptionChipSelector
            {
                Location = new Point(0, 138),
                Size = new Size(rw, 76)
            };
            _faceChips.SelectedIndexChanged += idx =>
            {
                if (TargetSnowman != null) TargetSnowman.FaceStyle = idx;
            };
            _tabPanels[1].Controls.Add(_faceChips);

            // -- TAB 2: Atkı & Boyun --
            _scarfStyleLbl = CreateSectionLabel(0, 0);
            _tabPanels[2].Controls.Add(_scarfStyleLbl);

            _scarfChips = new OptionChipSelector
            {
                Location = new Point(0, 22),
                Size = new Size(rw, 78)
            };
            _scarfChips.SelectedIndexChanged += idx =>
            {
                if (TargetSnowman != null) TargetSnowman.ScarfStyle = idx;
            };
            _tabPanels[2].Controls.Add(_scarfChips);

            _scarfColorLbl = CreateSectionLabel(0, 120);
            _tabPanels[2].Controls.Add(_scarfColorLbl);

            _scarfColors = new ColorSwatchBar
            {
                Location = new Point(0, 144),
                Size = new Size(rw, 34)
            };
            _scarfColors.ColorSelected += c =>
            {
                if (TargetSnowman != null)
                {
                    TargetSnowman.ScarfColor = c;
                    if (TargetSnowman.ScarfStyle == 0)
                    {
                        TargetSnowman.ScarfStyle = 1;
                        _scarfChips.SelectedIndex = 1;
                    }
                }
            };
            _tabPanels[2].Controls.Add(_scarfColors);

            // -- TAB 3: Kıyafet & Kollar --
            _outfitStyleLbl = CreateSectionLabel(0, 0);
            _tabPanels[3].Controls.Add(_outfitStyleLbl);

            _outfitChips = new OptionChipSelector
            {
                Location = new Point(0, 20),
                Size = new Size(rw, 68)
            };
            _outfitChips.SelectedIndexChanged += idx =>
            {
                if (TargetSnowman != null) TargetSnowman.OutfitStyle = idx;
            };
            _tabPanels[3].Controls.Add(_outfitChips);

            _armStyleLbl = CreateSectionLabel(0, 96);
            _tabPanels[3].Controls.Add(_armStyleLbl);

            _armChips = new OptionChipSelector
            {
                Location = new Point(0, 116),
                Size = new Size(rw, 68)
            };
            _armChips.SelectedIndexChanged += idx =>
            {
                if (TargetSnowman != null) TargetSnowman.ArmStyle = idx;
            };
            _tabPanels[3].Controls.Add(_armChips);

            _outfitColorLbl = CreateSectionLabel(0, 192);
            _tabPanels[3].Controls.Add(_outfitColorLbl);

            _outfitColors = new ColorSwatchBar
            {
                Location = new Point(0, 214),
                Size = new Size(rw, 34)
            };
            _outfitColors.ColorSelected += c =>
            {
                if (TargetSnowman != null)
                {
                    TargetSnowman.OutfitColor = c;
                    if (TargetSnowman.OutfitStyle == 0)
                    {
                        TargetSnowman.OutfitStyle = 2;
                        _outfitChips.SelectedIndex = 2;
                    }
                }
            };
            _tabPanels[3].Controls.Add(_outfitColors);

            // -- TAB 4: İsim Etiketi & Etiket Rengi --
            _nameInputLbl = CreateSectionLabel(0, 0);
            _tabPanels[4].Controls.Add(_nameInputLbl);

            ModernCard nameInputCard = new ModernCard
            {
                Location = new Point(0, 22),
                Size = new Size(rw - 42, 34),
                CornerRadius = 9,
                FillColor = Color.FromArgb(18, 28, 46),
                BorderColor = Color.FromArgb(56, 189, 248)
            };
            _tabPanels[4].Controls.Add(nameInputCard);

            _nameBox = new TextBox
            {
                Location = new Point(10, 7),
                Size = new Size(rw - 62, 20),
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(18, 28, 46),
                ForeColor = Color.FromArgb(248, 250, 252),
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                MaxLength = 18
            };
            _nameBox.TextChanged += (s, e) =>
            {
                if (_suppressNameEvent || TargetSnowman == null) return;
                TargetSnowman.NameTag = _nameBox.Text.Trim();
            };
            nameInputCard.Controls.Add(_nameBox);

            _clearNameBtn = new ModernButton
            {
                Text = "✕",
                Location = new Point(rw - 36, 22),
                Size = new Size(36, 34),
                CornerRadius = 9,
                FillColor = Color.FromArgb(44, 24, 34),
                BorderColor = Color.FromArgb(95, 42, 56),
                ForeColor = Color.FromArgb(252, 165, 165)
            };
            _clearNameBtn.Click += (s, e) =>
            {
                _nameBox.Text = "";
                if (TargetSnowman != null) TargetSnowman.NameTag = "";
            };
            _tabPanels[4].Controls.Add(_clearNameBtn);

            _nameColorLbl = CreateSectionLabel(0, 72);
            _tabPanels[4].Controls.Add(_nameColorLbl);

            _nameColors = new ColorSwatchBar
            {
                Location = new Point(0, 96),
                Size = new Size(rw, 34)
            };
            _nameColors.SetPalette(SnowmanEntity.NameTagPalette);
            _nameColors.ColorSelected += c =>
            {
                if (TargetSnowman != null)
                {
                    TargetSnowman.NameTagColor = c;
                    _nameBox.ForeColor = c;
                }
            };
            _tabPanels[4].Controls.Add(_nameColors);

            _quickNameLbl = CreateSectionLabel(0, 146);
            _tabPanels[4].Controls.Add(_quickNameLbl);

            _quickNameChips = new OptionChipSelector
            {
                Location = new Point(0, 168),
                Size = new Size(rw, 76)
            };
            _quickNameChips.SelectedIndexChanged += idx =>
            {
                string[] quickTr = new string[] { "Frosty", "Olaf", "Kral Kar", "Buzul", "Noel", "Kartopu" };
                string[] quickEn = new string[] { "Frosty", "Olaf", "King Snow", "Blizzard", "Santa Jr.", "Snowball" };
                string chosen = (_overlay.IsEnglish ? quickEn : quickTr)[idx];
                _nameBox.Text = chosen;
            };
            _tabPanels[4].Controls.Add(_quickNameChips);

            // ---- Alt Kalıcı Boyut Kaydırıcısı ----
            ModernCard sizeCard = new ModernCard
            {
                Location = new Point(rx, 372),
                Size = new Size(rw, 52),
                CornerRadius = 10,
                FillColor = Color.FromArgb(18, 28, 46),
                BorderColor = Color.FromArgb(38, 52, 74)
            };
            Controls.Add(sizeCard);

            _sizeLbl = new Label
            {
                Font = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(203, 213, 225),
                BackColor = Color.FromArgb(18, 28, 46),
                Location = new Point(12, 7),
                AutoSize = true
            };
            sizeCard.Controls.Add(_sizeLbl);

            _sizeValLbl = new Label
            {
                Font = new Font("Consolas", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                BackColor = Color.FromArgb(18, 28, 46),
                Location = new Point(rw - 72, 7),
                Size = new Size(60, 16),
                TextAlign = ContentAlignment.MiddleRight
            };
            sizeCard.Controls.Add(_sizeValLbl);

            _sizeSlider = new ModernSlider
            {
                Location = new Point(8, 26),
                Size = new Size(rw - 16, 20),
                Minimum = 75,
                Maximum = 140,
                Value = 100
            };
            _sizeSlider.Scroll += (s, e) =>
            {
                if (TargetSnowman != null)
                {
                    TargetSnowman.ApplyScale(_sizeSlider.Value / 100f);
                    _sizeValLbl.Text = "%" + _sizeSlider.Value;
                }
            };
            sizeCard.Controls.Add(_sizeSlider);

            _animTimer = new Timer { Interval = 30 };
            _animTimer.Tick += (s, e) =>
            {
                if (Opacity < _fadeTarget)
                    Opacity = Math.Min(_fadeTarget, Opacity + 0.14);
                _preview.StepAnimation();
            };
            _animTimer.Start();

            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                    Close();
            };

            FormClosed += (s, e) =>
            {
                _animTimer.Stop();
                _animTimer.Dispose();
            };

            ApplyLanguage();
            SyncControlsFromSnowman();
            ResumeLayout(true);
        }

        private static Label CreateSectionLabel(int x, int y)
        {
            return new Label
            {
                Location = new Point(x, y),
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 8.3f, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184),
                BackColor = Color.Transparent
            };
        }

        private string Tr(string tr, string en)
        {
            return _overlay.IsEnglish ? en : tr;
        }

        public void SetTargetSnowman(SnowmanEntity sm)
        {
            TargetSnowman = sm;
            _preview.Target = sm;
            SyncControlsFromSnowman();
        }

        public void ApplyLanguage()
        {
            _titleLbl.Text = Tr("⛄ Kardan Adam Gardırobu", "⛄ Snowman Wardrobe");
            _liveBadge.Text = Tr("CANLI", "LIVE");
            _randomBtn.Text = Tr("🎲 Rastgele", "🎲 Random");
            _deleteBtn.Text = Tr("🗑 Kardan Adamı Sil", "🗑 Delete Snowman");

            _presetBtns[0].Text = Tr("🎅 Noel", "🎅 Santa");
            _presetBtns[1].Text = Tr("🎩 Beyefendi", "🎩 Dapper");
            _presetBtns[2].Text = Tr("😎 Havalı", "😎 Cool");
            _presetBtns[3].Text = Tr("🧙 Büyücü", "🧙 Wizard");

            _tabs.SetTabs(new string[] {
                Tr("Şapka", "Hat"),
                Tr("Yüz", "Face"),
                Tr("Atkı", "Scarf"),
                Tr("Giyim", "Outfit"),
                Tr("İsim", "Tag")
            });

            _hatStyleLbl.Text = Tr("Şapka Stili", "Hat Style");
            _hatChips.SetItems(new string[] {
                Tr("Yok", "None"),
                Tr("🧶 Bere", "🧶 Beanie"),
                Tr("🎩 Silindir", "🎩 Top Hat"),
                Tr("🎅 Noel", "🎅 Santa"),
                Tr("👑 Taç", "👑 Crown"),
                Tr("🧢 Kulaklıklı", "🧢 Ushanka"),
                Tr("🧙 Büyücü", "🧙 Wizard")
            }, 3);
            _hatColorLbl.Text = Tr("Şapka Rengi", "Hat Color");

            _glassesLbl.Text = Tr("Gözlük & Vizör", "Eyewear");
            _glassesChips.SetItems(new string[] {
                Tr("Yok", "None"),
                Tr("🕶 Güneş", "🕶 Shades"),
                Tr("👓 Yuvarlak", "👓 Round"),
                Tr("🧐 Monokl", "🧐 Monocle"),
                Tr("🥽 Kayak", "🥽 Goggles")
            }, 3);

            _faceLbl.Text = Tr("Yüz İfadesi", "Expression");
            _faceChips.SetItems(new string[] {
                Tr("🥕 Klasik", "🥕 Classic"),
                Tr("😊 Gülümse", "😊 Rosy Smile"),
                Tr("🥸 Bıyık", "🥸 Mustache"),
                Tr("🪈 Pipo", "🪈 Pipe")
            }, 2);

            _scarfStyleLbl.Text = Tr("Atkı & Boyunluk", "Scarf & Neckwear");
            _scarfChips.SetItems(new string[] {
                Tr("Yok", "None"),
                Tr("🧣 Klasik", "🧣 Classic"),
                Tr("🎀 Çizgili", "🎀 Striped"),
                Tr("🤵 Papyon", "🤵 Bow Tie"),
                Tr("❄️ Kürklü", "❄️ Fur Collar")
            }, 3);
            _scarfColorLbl.Text = Tr("Atkı / Aksesuar Rengi", "Scarf / Accent Color");

            _outfitStyleLbl.Text = Tr("Gövde Kıyafeti", "Body Outfit");
            _outfitChips.SetItems(new string[] {
                Tr("⚫ Düğme", "⚫ Coal"),
                Tr("🦺 Yelek", "🦺 Vest"),
                Tr("🧶 Kazak", "🧶 Sweater"),
                Tr("🦸 Pelerin", "🦸 Cape")
            }, 2);

            _armStyleLbl.Text = Tr("Kollar & Elde Aksesuar", "Arms & Handheld");
            _armChips.SetItems(new string[] {
                Tr("🌿 Dal", "🌿 Twigs"),
                Tr("🧤 Eldiven", "🧤 Mittens"),
                Tr("🧹 Süpürge", "🧹 Broom"),
                Tr("🍬 Şeker", "🍬 Candy"),
                Tr("Yok", "None")
            }, 3);

            _outfitColorLbl.Text = Tr("Kıyafet Rengi", "Outfit Color");

            _nameInputLbl.Text = Tr("İsim Etiketi (Üstünde görünür)", "Name Tag (Shown above hat)");
            _nameColorLbl.Text = Tr("Etiket Yazı & Çerçeve Rengi", "Name Tag Color");
            _quickNameLbl.Text = Tr("Hızlı İsim Önerileri", "Quick Name Ideas");
            _quickNameChips.SetItems(_overlay.IsEnglish
                ? new string[] { "Frosty", "Olaf", "King Snow", "Blizzard", "Santa Jr.", "Snowball" }
                : new string[] { "Frosty", "Olaf", "Kral Kar", "Buzul", "Noel", "Kartopu" }, 3);

            _sizeLbl.Text = Tr("Kardan Adam Boyutu", "Snowman Size");
        }

        private void SyncControlsFromSnowman()
        {
            if (TargetSnowman == null) return;
            _hatChips.SelectedIndex = TargetSnowman.HasHat ? (TargetSnowman.HatStyle + 1) : 0;
            _hatColors.SelectClosestColor(TargetSnowman.HatColor);
            _glassesChips.SelectedIndex = TargetSnowman.GlassesStyle;
            _faceChips.SelectedIndex = TargetSnowman.FaceStyle;
            _scarfChips.SelectedIndex = TargetSnowman.ScarfStyle;
            _scarfColors.SelectClosestColor(TargetSnowman.ScarfColor);
            _outfitChips.SelectedIndex = TargetSnowman.OutfitStyle;
            _armChips.SelectedIndex = TargetSnowman.ArmStyle;
            _outfitColors.SelectClosestColor(TargetSnowman.OutfitColor);

            _suppressNameEvent = true;
            _nameBox.Text = TargetSnowman.NameTag ?? "";
            _nameBox.ForeColor = TargetSnowman.NameTagColor.A > 0 ? TargetSnowman.NameTagColor : Color.White;
            _suppressNameEvent = false;
            _nameColors.SelectClosestColor(TargetSnowman.NameTagColor);

            int pct = (int)Math.Round(TargetSnowman.Scale * 100f);
            _sizeSlider.Value = Math.Max(75, Math.Min(140, pct));
            _sizeValLbl.Text = "%" + _sizeSlider.Value;
        }

        private void ShowTab(int index)
        {
            for (int i = 0; i < _tabPanels.Length; i++)
                _tabPanels[i].Visible = (i == index);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int dark = 1;
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                int corner = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            // Üst ince Aurora cam vurgusu
            using (LinearGradientBrush accent = new LinearGradientBrush(
                new Rectangle(0, 0, Width, 3),
                Color.FromArgb(56, 189, 248),
                Color.FromArgb(52, 211, 153),
                LinearGradientMode.Horizontal))
            {
                g.FillRectangle(accent, 0, 0, Width, 3);
            }
            using (Pen borderPen = new Pen(Color.FromArgb(56, 82, 118), 1f))
                g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
        }
    }

    internal sealed class TreePreviewCanvas : Control
    {
        public ChristmasTreeEntity Target;
        private float _animTime;
        private readonly PointF[] _snowPts = new PointF[22];

        public TreePreviewCanvas()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            DoubleBuffered = true;
            Random r = new Random(99);
            for (int i = 0; i < _snowPts.Length; i++)
                _snowPts[i] = new PointF((float)(r.NextDouble() * 190.0), (float)(r.NextDouble() * 230.0));
        }

        public void StepAnimation()
        {
            _animTime += 0.035f;
            for (int i = 0; i < _snowPts.Length; i++)
            {
                _snowPts[i].Y += 0.55f + (i % 3) * 0.25f;
                _snowPts[i].X += (float)Math.Sin(_animTime + i) * 0.22f;
                if (_snowPts[i].Y > Height) { _snowPts[i].Y = -4f; _snowPts[i].X = (i * 31) % Math.Max(20, Width); }
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath clipPath = GraphicsExt.CreateRoundedRect(rect, 12f))
            {
                GraphicsState st = g.Save();
                g.SetClip(clipPath);

                using (LinearGradientBrush sky = new LinearGradientBrush(
                    rect,
                    Color.FromArgb(14, 24, 44),
                    Color.FromArgb(24, 42, 70),
                    LinearGradientMode.Vertical))
                {
                    g.FillRectangle(sky, rect);
                }

                // Karlı zemin tepesi
                using (LinearGradientBrush hill = new LinearGradientBrush(
                    new RectangleF(0f, Height - 44f, Width, 48f),
                    Color.FromArgb(235, 224, 242, 254),
                    Color.FromArgb(245, 148, 197, 253),
                    LinearGradientMode.Vertical))
                {
                    g.FillEllipse(hill, -30f, Height - 36f, Width + 60f, 64f);
                }

                if (Target != null)
                {
                    // Önizleme içine sığacak şekilde zarifçe ölçekle (boyut değişimini de hissettirerek!)
                    float previewMult = Math.Max(0.85f, Math.Min(1.55f, 1.38f / Math.Max(0.85f, Target.Scale * 0.85f)));
                    float savedWob = Target.WobbleAngle;
                    Target.WobbleAngle = (float)Math.Sin(_animTime * 1.4f) * 1.4f;
                    TreeRenderer.DrawTree(g, Target, Width * 0.5f, Height - 20f, previewMult, _animTime, 0.65f, 0.25f, false);
                    Target.WobbleAngle = savedWob;
                }

                using (SolidBrush flakeBrush = new SolidBrush(Color.FromArgb(170, 255, 255, 255)))
                {
                    for (int i = 0; i < _snowPts.Length; i++)
                    {
                        float sz = (i % 3 == 0) ? 3.0f : 2.0f;
                        g.FillEllipse(flakeBrush, _snowPts[i].X, _snowPts[i].Y, sz, sz);
                    }
                }

                g.Restore(st);
                using (Pen border = new Pen(Color.FromArgb(56, 82, 118), 1.2f))
                    g.DrawPath(border, clipPath);
            }
        }
    }

    internal sealed class TreeDecoratorForm : Form
    {
        private readonly SnowOverlayForm _overlay;
        public ChristmasTreeEntity TargetTree { get; private set; }

        private readonly TreePreviewCanvas _preview;
        private readonly Timer _animTimer;
        private double _fadeTarget = 0.97;

        private readonly Label _titleLbl;
        private readonly Label _liveBadge;
        private readonly ModernButton _randomBtn;
        private readonly ModernButton _deleteBtn;
        private readonly ModernButton[] _presetBtns = new ModernButton[4];

        private readonly SegmentedTabBar _tabs;
        private readonly Panel[] _tabPanels = new Panel[4];

        // Tab 0: Ağaç & Tepe
        private readonly Label _treeStyleLbl;
        private readonly OptionChipSelector _treeStyleChips;
        private readonly Label _topperLbl;
        private readonly OptionChipSelector _topperChips;

        // Tab 1: Işıklar
        private readonly Label _lightStyleLbl;
        private readonly OptionChipSelector _lightStyleChips;
        private readonly Label _lightAnimLbl;
        private readonly OptionChipSelector _lightAnimChips;

        // Tab 2: Süsler & Kurdele
        private readonly Label _ornStyleLbl;
        private readonly OptionChipSelector _ornStyleChips;
        private readonly Label _ornColorLbl;
        private readonly ColorSwatchBar _ornColors;
        private readonly Label _garlandLbl;
        private readonly OptionChipSelector _garlandChips;

        // Tab 3: Altlık & Hediyeler
        private readonly ModernToggle _giftsToggle;
        private readonly Label _baseTipLbl;

        // Alt Boyut Kaydırıcısı (%60 - %160)
        private readonly Label _sizeLbl;
        private readonly Label _sizeValLbl;
        private readonly ModernSlider _sizeSlider;

        public TreeDecoratorForm(SnowOverlayForm overlay, ChristmasTreeEntity target)
        {
            _overlay = overlay;
            TargetTree = target;

            SuspendLayout();
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(548, 444);
            BackColor = Color.FromArgb(11, 17, 30);
            ForeColor = Color.White;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            Opacity = 0.0;

            Panel headerBar = new Panel
            {
                Location = new Point(0, 3),
                Size = new Size(548, 42),
                BackColor = Color.FromArgb(15, 24, 42)
            };
            MouseEventHandler dragForm = (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    NativeMethods.ReleaseCapture();
                    NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HT_CAPTION, 0);
                }
            };
            headerBar.MouseDown += dragForm;
            Controls.Add(headerBar);

            _titleLbl = new Label
            {
                Location = new Point(16, 11),
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 10.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(248, 250, 252),
                BackColor = Color.Transparent
            };
            _titleLbl.MouseDown += dragForm;
            headerBar.Controls.Add(_titleLbl);

            _liveBadge = new Label
            {
                Location = new Point(236, 12),
                Size = new Size(56, 18),
                Font = new Font("Segoe UI", 7.3f, FontStyle.Bold),
                ForeColor = Color.FromArgb(52, 211, 153),
                BackColor = Color.FromArgb(16, 46, 42),
                TextAlign = ContentAlignment.MiddleCenter
            };
            headerBar.Controls.Add(_liveBadge);

            ModernButton closeBtn = new ModernButton
            {
                Text = "✕",
                Location = new Point(512, 8),
                Size = new Size(26, 26),
                CornerRadius = 7,
                FillColor = Color.FromArgb(58, 24, 34),
                BorderColor = Color.FromArgb(105, 42, 54),
                ForeColor = Color.FromArgb(252, 165, 165),
                Font = new Font("Segoe UI", 8.2f, FontStyle.Bold)
            };
            closeBtn.Click += (s, e) => Close();
            headerBar.Controls.Add(closeBtn);

            // ---- Sol Önizleme Sütunu ----
            _preview = new TreePreviewCanvas
            {
                Location = new Point(16, 56),
                Size = new Size(176, 232),
                Target = target
            };
            Controls.Add(_preview);

            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                _presetBtns[i] = new ModernButton
                {
                    Location = new Point(16 + (i % 2) * 90, 298 + (i / 2) * 32),
                    Size = new Size(86, 27),
                    CornerRadius = 7,
                    FillColor = Color.FromArgb(22, 34, 54),
                    BorderColor = Color.FromArgb(46, 66, 98),
                    Font = new Font("Segoe UI Semibold", 7.8f, FontStyle.Bold)
                };
                _presetBtns[i].Click += (s, e) =>
                {
                    if (TargetTree != null)
                    {
                        TargetTree.ApplyPreset(idx);
                        SyncControlsFromTree();
                    }
                };
                Controls.Add(_presetBtns[i]);
            }

            _randomBtn = new ModernButton
            {
                Location = new Point(16, 366),
                Size = new Size(176, 28),
                CornerRadius = 8,
                FillColor = Color.FromArgb(20, 83, 45),
                BorderColor = Color.FromArgb(52, 211, 153),
                ForeColor = Color.FromArgb(209, 250, 229),
                Font = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold)
            };
            _randomBtn.Click += (s, e) =>
            {
                if (TargetTree != null)
                {
                    TargetTree.Randomize(new Random(Environment.TickCount));
                    SyncControlsFromTree();
                }
            };
            Controls.Add(_randomBtn);

            _deleteBtn = new ModernButton
            {
                Location = new Point(16, 400),
                Size = new Size(176, 28),
                CornerRadius = 8,
                FillColor = Color.FromArgb(58, 22, 34),
                BorderColor = Color.FromArgb(127, 29, 29),
                ForeColor = Color.FromArgb(252, 165, 165),
                Font = new Font("Segoe UI Semibold", 8.1f, FontStyle.Bold)
            };
            _deleteBtn.Click += (s, e) =>
            {
                if (TargetTree != null)
                    _overlay.DeleteChristmasTree(TargetTree);
                Close();
            };
            Controls.Add(_deleteBtn);

            // ---- Sağ Süsleme Sekmeleri ----
            int rx = 208;
            int rw = 324;

            _tabs = new SegmentedTabBar
            {
                Location = new Point(rx, 56),
                Size = new Size(rw, 34)
            };
            _tabs.SelectedIndexChanged += ShowTab;
            Controls.Add(_tabs);

            for (int i = 0; i < 4; i++)
            {
                _tabPanels[i] = new Panel
                {
                    Location = new Point(rx, 100),
                    Size = new Size(rw, 260),
                    BackColor = Color.FromArgb(11, 17, 30),
                    Visible = (i == 0)
                };
                Controls.Add(_tabPanels[i]);
            }

            // Tab 0: Ağaç & Tepe Süsü
            _treeStyleLbl = CreateSectionLabel(0, 0);
            _tabPanels[0].Controls.Add(_treeStyleLbl);
            _treeStyleChips = new OptionChipSelector { Location = new Point(0, 22), Size = new Size(rw, 76) };
            _treeStyleChips.SelectedIndexChanged += idx => { if (TargetTree != null) TargetTree.TreeStyle = idx; };
            _tabPanels[0].Controls.Add(_treeStyleChips);

            _topperLbl = CreateSectionLabel(0, 114);
            _tabPanels[0].Controls.Add(_topperLbl);
            _topperChips = new OptionChipSelector { Location = new Point(0, 136), Size = new Size(rw, 76) };
            _topperChips.SelectedIndexChanged += idx => { if (TargetTree != null) TargetTree.TopperStyle = idx; };
            _tabPanels[0].Controls.Add(_topperChips);

            // Tab 1: Işıklar
            _lightStyleLbl = CreateSectionLabel(0, 0);
            _tabPanels[1].Controls.Add(_lightStyleLbl);
            _lightStyleChips = new OptionChipSelector { Location = new Point(0, 22), Size = new Size(rw, 76) };
            _lightStyleChips.SelectedIndexChanged += idx => { if (TargetTree != null) TargetTree.LightStyle = idx; };
            _tabPanels[1].Controls.Add(_lightStyleChips);

            _lightAnimLbl = CreateSectionLabel(0, 114);
            _tabPanels[1].Controls.Add(_lightAnimLbl);
            _lightAnimChips = new OptionChipSelector { Location = new Point(0, 136), Size = new Size(rw, 76) };
            _lightAnimChips.SelectedIndexChanged += idx => { if (TargetTree != null) TargetTree.LightAnim = idx; };
            _tabPanels[1].Controls.Add(_lightAnimChips);

            // Tab 2: Süsler & Kurdele
            _ornStyleLbl = CreateSectionLabel(0, 0);
            _tabPanels[2].Controls.Add(_ornStyleLbl);
            _ornStyleChips = new OptionChipSelector { Location = new Point(0, 20), Size = new Size(rw, 38) };
            _ornStyleChips.SelectedIndexChanged += idx => { if (TargetTree != null) TargetTree.OrnamentStyle = idx; };
            _tabPanels[2].Controls.Add(_ornStyleChips);

            _ornColorLbl = CreateSectionLabel(0, 70);
            _tabPanels[2].Controls.Add(_ornColorLbl);
            _ornColors = new ColorSwatchBar { Location = new Point(0, 92), Size = new Size(rw, 34) };
            _ornColors.SetPalette(ChristmasTreeEntity.OrnamentPalette);
            _ornColors.ColorSelected += c => { if (TargetTree != null) TargetTree.OrnamentColor = c; };
            _tabPanels[2].Controls.Add(_ornColors);

            _garlandLbl = CreateSectionLabel(0, 140);
            _tabPanels[2].Controls.Add(_garlandLbl);
            _garlandChips = new OptionChipSelector { Location = new Point(0, 162), Size = new Size(rw, 76) };
            _garlandChips.SelectedIndexChanged += idx => { if (TargetTree != null) TargetTree.GarlandStyle = idx; };
            _tabPanels[2].Controls.Add(_garlandChips);

            // Tab 3: Altlık & Hediyeler
            ModernCard baseCard = new ModernCard
            {
                Location = new Point(0, 4),
                Size = new Size(rw, 132),
                CornerRadius = 10,
                FillColor = Color.FromArgb(18, 28, 46),
                BorderColor = Color.FromArgb(44, 62, 92)
            };
            _tabPanels[3].Controls.Add(baseCard);

            _giftsToggle = new ModernToggle
            {
                Location = new Point(14, 16),
                Size = new Size(rw - 28, 26),
                Checked = target.HasGifts
            };
            _giftsToggle.CheckedChanged += (s, e) => { if (TargetTree != null) TargetTree.HasGifts = _giftsToggle.Checked; };
            baseCard.Controls.Add(_giftsToggle);

            _baseTipLbl = new Label
            {
                Location = new Point(14, 54),
                Size = new Size(rw - 28, 66),
                Font = new Font("Segoe UI", 8.1f),
                ForeColor = Color.FromArgb(148, 163, 184),
                BackColor = Color.FromArgb(18, 28, 46)
            };
            baseCard.Controls.Add(_baseTipLbl);

            // ---- Alt Kalıcı Boyut Kaydırıcısı (%60 - %160) ----
            ModernCard sizeCard = new ModernCard
            {
                Location = new Point(rx, 372),
                Size = new Size(rw, 52),
                CornerRadius = 10,
                FillColor = Color.FromArgb(18, 28, 46),
                BorderColor = Color.FromArgb(38, 52, 74)
            };
            Controls.Add(sizeCard);

            _sizeLbl = new Label
            {
                Font = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(203, 213, 225),
                BackColor = Color.FromArgb(18, 28, 46),
                Location = new Point(12, 7),
                AutoSize = true
            };
            sizeCard.Controls.Add(_sizeLbl);

            _sizeValLbl = new Label
            {
                Font = new Font("Consolas", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(52, 211, 153),
                BackColor = Color.FromArgb(18, 28, 46),
                Location = new Point(rw - 72, 7),
                Size = new Size(60, 16),
                TextAlign = ContentAlignment.MiddleRight
            };
            sizeCard.Controls.Add(_sizeValLbl);

            _sizeSlider = new ModernSlider
            {
                Location = new Point(8, 26),
                Size = new Size(rw - 16, 20),
                Minimum = 60,
                Maximum = 160,
                Value = 100
            };
            _sizeSlider.Scroll += (s, e) =>
            {
                if (TargetTree != null)
                {
                    TargetTree.ApplyScale(_sizeSlider.Value / 100f);
                    _sizeValLbl.Text = "%" + _sizeSlider.Value;
                }
            };
            sizeCard.Controls.Add(_sizeSlider);

            _animTimer = new Timer { Interval = 30 };
            _animTimer.Tick += (s, e) =>
            {
                if (Opacity < _fadeTarget)
                    Opacity = Math.Min(_fadeTarget, Opacity + 0.14);
                _preview.StepAnimation();
            };
            _animTimer.Start();

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            FormClosed += (s, e) => { _animTimer.Stop(); _animTimer.Dispose(); };

            ApplyLanguage();
            SyncControlsFromTree();
            ResumeLayout(true);
        }

        private static Label CreateSectionLabel(int x, int y)
        {
            return new Label
            {
                Location = new Point(x, y),
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 8.3f, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184),
                BackColor = Color.Transparent
            };
        }

        private string Tr(string tr, string en) { return _overlay.IsEnglish ? en : tr; }

        public void SetTargetTree(ChristmasTreeEntity tree)
        {
            TargetTree = tree;
            _preview.Target = tree;
            SyncControlsFromTree();
        }

        public void ApplyLanguage()
        {
            _titleLbl.Text = Tr("🎄 Yılbaşı Ağacı Süsleme", "🎄 Christmas Tree Studio");
            _liveBadge.Text = Tr("CANLI", "LIVE");
            _randomBtn.Text = Tr("🎲 Rastgele Süsle", "🎲 Randomize");
            _deleteBtn.Text = Tr("🗑 Ağacı Sil", "🗑 Delete Tree");

            _presetBtns[0].Text = Tr("🎄 Klasik", "🎄 Classic");
            _presetBtns[1].Text = Tr("❄️ Buzul", "❄️ Frost");
            _presetBtns[2].Text = Tr("🌟 Altın", "🌟 Gold");
            _presetBtns[3].Text = Tr("🍬 Şeker", "🍬 Candy");

            _tabs.SetTabs(new string[] {
                Tr("Ağaç", "Tree"),
                Tr("Işıklar", "Lights"),
                Tr("Süsler", "Decor"),
                Tr("Altlık", "Base")
            });

            _treeStyleLbl.Text = Tr("Ağaç Türü & Rengi", "Tree Style & Color");
            _treeStyleChips.SetItems(new string[] {
                Tr("🌲 Klasik Çam", "🌲 Pine"),
                Tr("❄️ Karlı Çam", "❄️ Snowy"),
                Tr("🧊 Buz Beyazı", "🧊 Ice White"),
                Tr("🌸 Pembe Kış", "🌸 Rose Gold")
            }, 2);

            _topperLbl.Text = Tr("Tepe Süsü (Topper)", "Tree Topper");
            _topperChips.SetItems(new string[] {
                Tr("Yok", "None"),
                Tr("⭐ Altın Yıldız", "⭐ Gold Star"),
                Tr("❄️ Kar Tanesi", "❄️ Snowflake"),
                Tr("🎀 Kırmızı Fiyonk", "🎀 Red Bow")
            }, 2);

            _lightStyleLbl.Text = Tr("Işık Rengi", "Light Color");
            _lightStyleChips.SetItems(new string[] {
                Tr("Kapalı", "Off"),
                Tr("✨ Sıcak Altın", "✨ Warm Gold"),
                Tr("🌈 Çok Renkli", "🌈 Multicolor"),
                Tr("💎 Buz Mavisi", "💎 Ice Blue")
            }, 2);

            _lightAnimLbl.Text = Tr("Işık Animasyonu", "Light Animation");
            _lightAnimChips.SetItems(new string[] {
                Tr("Sabit", "Steady"),
                Tr("✨ Parılda", "✨ Twinkle"),
                Tr("🌊 Dalga", "🌊 Wave"),
                Tr("🎵 Müzik (WASAPI)", "🎵 Music Sync")
            }, 2);

            _ornStyleLbl.Text = Tr("Süs Çeşidi", "Ornament Style");
            _ornStyleChips.SetItems(new string[] {
                Tr("Yok", "None"),
                Tr("🔴 Toplar", "🔴 Baubles"),
                Tr("🍬 Şeker", "🍬 Canes"),
                Tr("⭐ Yıldız", "⭐ Stars")
            }, 4);

            _ornColorLbl.Text = Tr("Süs Ana Rengi", "Ornament Accent Color");
            _garlandLbl.Text = Tr("Sarmal Kurdele (Garland)", "Tinsel / Garland");
            _garlandChips.SetItems(new string[] {
                Tr("Yok", "None"),
                Tr("✨ Altın Sarmal", "✨ Gold Tinsel"),
                Tr("🎀 Kırmızı Kurdele", "🎀 Red Ribbon"),
                Tr("⚪ Gümüş İnci", "⚪ Silver Pearls")
            }, 2);

            _giftsToggle.Text = Tr("🎁 Ağaç Altı Hediye Paketleri", "🎁 Mini Gift Boxes Under Tree");
            _baseTipLbl.Text = Tr(
                "İpucu: Alttaki kaydırıcı ile ağacı %60–%160 arasında boyutlandırabilir, F8 etkileşim modunda tutup dilediğiniz pencereye taşıyabilirsiniz. Kış kuşları da dallarına konar!",
                "Tip: Resize the tree from 60%–160% below, drag it onto any window in F8 Interactive Mode, and watch winter birds perch right on its branches!");

            _sizeLbl.Text = Tr("Yılbaşı Ağacı Boyutu", "Christmas Tree Size");
        }

        private void SyncControlsFromTree()
        {
            if (TargetTree == null) return;
            _treeStyleChips.SelectedIndex = TargetTree.TreeStyle;
            _topperChips.SelectedIndex = TargetTree.TopperStyle;
            _lightStyleChips.SelectedIndex = TargetTree.LightStyle;
            _lightAnimChips.SelectedIndex = TargetTree.LightAnim;
            _ornStyleChips.SelectedIndex = TargetTree.OrnamentStyle;
            _ornColors.SelectClosestColor(TargetTree.OrnamentColor);
            _garlandChips.SelectedIndex = TargetTree.GarlandStyle;
            _giftsToggle.Checked = TargetTree.HasGifts;

            int pct = (int)Math.Round(TargetTree.Scale * 100f);
            _sizeSlider.Value = Math.Max(60, Math.Min(160, pct));
            _sizeValLbl.Text = "%" + _sizeSlider.Value;
        }

        private void ShowTab(int index)
        {
            for (int i = 0; i < _tabPanels.Length; i++)
                _tabPanels[i].Visible = (i == index);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int dark = 1;
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                int corner = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (LinearGradientBrush accent = new LinearGradientBrush(
                new Rectangle(0, 0, Width, 3),
                Color.FromArgb(52, 211, 153),
                Color.FromArgb(250, 204, 21),
                LinearGradientMode.Horizontal))
            {
                e.Graphics.FillRectangle(accent, 0, 0, Width, 3);
            }
            using (Pen borderPen = new Pen(Color.FromArgb(56, 82, 118), 1f))
                e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
        }
    }

    internal sealed class AudioLevelBar : Control
    {
        private float _level;
        public float Level
        {
            get { return _level; }
            set
            {
                float c = Math.Max(0f, Math.Min(1f, value));
                if (Math.Abs(c - _level) > 0.008f)
                {
                    _level = c;
                    Invalidate();
                }
            }
        }

        public AudioLevelBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true;
            BackColor = Color.Transparent;
            Size = new Size(256, 10);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF bg = new RectangleF(0.5f, 1.5f, Width - 1.5f, Height - 3f);
            using (GraphicsPath bp = GraphicsExt.CreateRoundedRect(bg, 3.5f))
            using (SolidBrush bb = new SolidBrush(Color.FromArgb(14, 22, 38)))
                g.FillPath(bb, bp);

            if (_level > 0.01f)
            {
                float fw = Math.Max(7f, (Width - 2f) * _level);
                RectangleF fg = new RectangleF(0.5f, 1.5f, fw, Height - 3f);
                using (GraphicsPath fp = GraphicsExt.CreateRoundedRect(fg, 3.5f))
                using (LinearGradientBrush grad = new LinearGradientBrush(
                    bg,
                    Color.FromArgb(52, 211, 153),
                    Color.FromArgb(168, 85, 247),
                    LinearGradientMode.Horizontal))
                {
                    g.FillPath(grad, fp);
                }
            }
        }
    }

    internal sealed class ControlPanelForm : Form
    {
        private readonly SnowOverlayForm _overlay;
        private readonly WindRadarControl _windRadar;
        private readonly AudioLevelBar _audioBar;
        private readonly Timer _uiTimer;

        // Başlık & Durum
        private readonly Label _titleLbl;
        private readonly Label _modeBadge;
        private readonly ModernButton _surpriseBtn;
        private readonly ModernButton _langBtn;

        // Sol Sütun (Hava Durumu, Kaydırıcılar, Ana Kontroller)
        private readonly Label _windHdr;
        private readonly Label _windHint;
        private readonly Label _windValueLabel;
        private readonly ModernButton _resetBtn;
        private readonly ModernButton _lightBtn;
        private readonly ModernButton _normalBtn;
        private readonly ModernButton _blizzardBtn;

        private readonly ModernSlider _intensitySlider;
        private readonly ModernSlider _opacitySlider;
        private readonly ModernSlider _pileSlider;
        private readonly ModernSlider _meltSlider;
        private readonly Label _densityCapLabel;
        private readonly Label _opacityCapLabel;
        private readonly Label _pileCapLabel;
        private readonly Label _meltCapLabel;
        private readonly Label _densityValLabel;
        private readonly Label _opacityValLabel;
        private readonly Label _pileValLabel;
        private readonly Label _meltValLabel;

        private readonly ModernButton _interactBtn;
        private readonly ModernButton _pauseBtn;
        private readonly ModernButton _clearBtn;
        private readonly ModernButton _postcardQuickBtn;

        // Sağ Sütun 4 Sekme (Oyuncak / Sahne / Kutlama / Sistem)
        private readonly SegmentedTabBar _rightTabs;
        private readonly Panel[] _tabPanels = new Panel[4];

        // Tab 0: Oyuncaklar (Kardan Adam, Yılbaşı Ağacı, Penguen, Kürek/Ateş)
        private readonly Label _snowmenCardHdr;
        private readonly ModernButton _snowmanBtn;
        private readonly ModernButton _ballBtn;
        private readonly ModernToggle _fightToggle;
        private readonly Label _snowmanTipLbl;
        private readonly Label _treeCardHdr;
        private readonly ModernButton _treeBtn;
        private readonly ModernButton _removeTreeBtn;
        private readonly Label _treeTipLbl;
        private readonly Label _penguinCardHdr;
        private readonly ModernButton _penguinBtn;
        private readonly ModernButton _removePenguinBtn;
        private readonly Label _toolsCardHdr;
        private readonly ModernButton _plowBtn;
        private readonly ModernButton _removePlowBtn;
        private readonly ModernButton _fireBtn;
        private readonly ModernButton _removeFireBtn;

        // Tab 1: Sahne & Doğa
        private readonly Label _atmoCardHdr;
        private readonly ModernToggle _lightsToggle;
        private readonly ModernToggle _iciclesToggle;
        private readonly ModernToggle _frostToggle;
        private readonly ModernToggle _auroraToggle;
        private readonly ModernToggle _santaToggle;
        private readonly ModernToggle _lanternsToggle;
        private readonly Label _lifeCardHdr;
        private readonly ModernToggle _cableCarToggle;
        private readonly ModernToggle _trainToggle;
        private readonly ModernToggle _birdsToggle;
        private readonly ModernToggle _cursorTrailToggle;
        private readonly Label _toysHintLbl;

        // Tab 2: Arka Plan, Kutlama & Ses
        private readonly Label _wpCardHdr;
        private readonly OptionChipSelector _wpChips;
        private readonly ModernButton _wpCustomBtn;
        private readonly ModernButton _wpRestoreBtn;
        private readonly ModernToggle _wpRememberToggle;
        private readonly Label _celebCardHdr;
        private readonly ModernToggle _countdownToggle;
        private readonly ModernButton _testCelebBtn;
        private readonly Label _audioCardHdr;
        private readonly ModernToggle _soundToggle;
        private readonly ModernToggle _audioReactiveToggle;
        private readonly Label _audioMeterLbl;
        private readonly Label _audioHintLbl;

        // Tab 3: Sistem & Kayıt
        private readonly Label _sysDisplayHdr;
        private readonly ModernButton _multiMonBtn;
        private readonly ModernToggle _perfToggle;
        private readonly ModernToggle _autoPauseToggle;
        private readonly Label _sysSaveHdr;
        private readonly ModernToggle _startupToggle;
        private readonly ModernToggle _rememberSnowmenToggle;
        private readonly ModernButton _saveNowBtn;
        private readonly Label _shortcutsHdr;
        private readonly Label _shortcutsBody;

        // Sistem Tepsisi (Tray)
        private readonly NotifyIcon _trayIcon;
        private readonly MenuItem _trayShowItem;
        private readonly MenuItem _trayInteractItem;
        private readonly MenuItem _traySurpriseItem;
        private readonly MenuItem _trayPostcardItem;
        private readonly MenuItem _trayPauseItem;
        private readonly MenuItem _trayClearItem;
        private readonly MenuItem _trayExitItem;

        private const float MeltScale = 0.000008f;

        public ControlPanelForm(SnowOverlayForm overlay)
        {
            _overlay = overlay;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "Snowy for Windows";
            ClientSize = new Size(668, 610);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(36, 42);
            BackColor = Color.FromArgb(11, 17, 30);
            ForeColor = Color.FromArgb(248, 250, 252);
            TopMost = true;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            DoubleBuffered = true;

            // ---- Üst Sürüklenebilir Modern Başlık Çubuğu ----
            Panel headerBar = new Panel
            {
                Location = new Point(0, 3),
                Size = new Size(668, 44),
                BackColor = Color.FromArgb(15, 24, 42)
            };
            MouseEventHandler dragForm = (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    NativeMethods.ReleaseCapture();
                    NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HT_CAPTION, 0);
                }
            };
            headerBar.MouseDown += dragForm;
            Controls.Add(headerBar);

            _titleLbl = new Label
            {
                Text = "❄️ Snowy",
                Font = new Font("Segoe UI Semibold", 11.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(248, 250, 252),
                Location = new Point(16, 10),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            _titleLbl.MouseDown += dragForm;
            headerBar.Controls.Add(_titleLbl);

            Label verBadge = new Label
            {
                Text = "v1.5.2",
                Font = new Font("Consolas", 7.4f, FontStyle.Bold),
                ForeColor = Color.FromArgb(52, 211, 153),
                BackColor = Color.FromArgb(18, 42, 46),
                Location = new Point(106, 13),
                Size = new Size(44, 18),
                TextAlign = ContentAlignment.MiddleCenter
            };
            headerBar.Controls.Add(verBadge);

            _modeBadge = new Label
            {
                Font = new Font("Segoe UI", 7.8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                BackColor = Color.FromArgb(24, 38, 60),
                Location = new Point(154, 12),
                Size = new Size(92, 20),
                TextAlign = ContentAlignment.MiddleCenter
            };
            headerBar.Controls.Add(_modeBadge);

            _surpriseBtn = new ModernButton
            {
                Location = new Point(388, 9),
                Size = new Size(134, 26),
                CornerRadius = 8,
                FillColor = Color.FromArgb(76, 29, 149),
                BorderColor = Color.FromArgb(167, 139, 250),
                ForeColor = Color.FromArgb(254, 240, 138),
                Font = new Font("Segoe UI Semibold", 8.0f, FontStyle.Bold)
            };
            _surpriseBtn.Click += (s, e) => _overlay.TriggerSurpriseShow();
            headerBar.Controls.Add(_surpriseBtn);

            _langBtn = new ModernButton
            {
                Location = new Point(530, 9),
                Size = new Size(58, 26),
                CornerRadius = 8,
                FillColor = Color.FromArgb(26, 40, 62),
                BorderColor = Color.FromArgb(56, 189, 248),
                ForeColor = Color.FromArgb(186, 230, 253),
                Font = new Font("Segoe UI Semibold", 7.8f, FontStyle.Bold)
            };
            _langBtn.Click += (s, e) =>
            {
                _overlay.IsEnglish = !_overlay.IsEnglish;
                ApplyLanguage();
                _overlay.RefreshWardrobeLanguage();
            };
            headerBar.Controls.Add(_langBtn);

            ModernButton minBtn = new ModernButton
            {
                Text = "—",
                Location = new Point(596, 9),
                Size = new Size(28, 26),
                CornerRadius = 7,
                FillColor = Color.FromArgb(24, 35, 54),
                BorderColor = Color.FromArgb(40, 54, 78),
                Font = new Font("Segoe UI", 8.2f, FontStyle.Bold)
            };
            minBtn.Click += (s, e) => { WindowState = FormWindowState.Minimized; };
            headerBar.Controls.Add(minBtn);

            ModernButton closeBtn = new ModernButton
            {
                Text = "✕",
                Location = new Point(630, 9),
                Size = new Size(28, 26),
                CornerRadius = 7,
                FillColor = Color.FromArgb(58, 24, 34),
                BorderColor = Color.FromArgb(105, 42, 54),
                ForeColor = Color.FromArgb(252, 165, 165),
                Font = new Font("Segoe UI", 8.2f, FontStyle.Bold)
            };
            closeBtn.Click += (s, e) => { Close(); };
            headerBar.Controls.Add(closeBtn);

            // ================================================================
            // SOL SÜTUN: HAVA DURUMU, FİZİK KAYDIRICILARI & HIZLI AKSİYONLAR
            // ================================================================
            const int LX = 18;
            const int LW = 294;

            ModernCard windCard = new ModernCard
            {
                Location = new Point(LX, 58),
                Size = new Size(LW, 218)
            };
            Controls.Add(windCard);

            _windHdr = CreateCardHeader(14, 10);
            windCard.Controls.Add(_windHdr);

            _resetBtn = CreateModernButton(new Point(LW - 68, 8), new Size(56, 22), Color.FromArgb(32, 46, 70));
            _resetBtn.CornerRadius = 6;
            _resetBtn.Font = new Font("Segoe UI", 7.8f, FontStyle.Bold);
            _resetBtn.Click += (s, e) =>
            {
                _windRadar.Nx = 0f;
                _windRadar.Ny = 0f;
                _overlay.GustAmount = 0.04f;
                _overlay.BlizzardMode = false;
                _windRadar.Invalidate();
                UpdateWindStatus(0f, 0f);
            };
            windCard.Controls.Add(_resetBtn);

            _windRadar = new WindRadarControl
            {
                Size = new Size(106, 106),
                Location = new Point((LW - 106) / 2, 28),
                Nx = _overlay.WindNx,
                Ny = _overlay.WindNy
            };
            _windRadar.OnWindChanged += (nx, ny) =>
            {
                float mag = (float)Math.Min(1.0, Math.Sqrt(nx * nx + ny * ny));
                _overlay.GustAmount = 0.06f + mag * 0.34f;
                _overlay.BlizzardMode = (mag > 0.88f);
                UpdateWindStatus(nx, ny);
            };
            windCard.Controls.Add(_windRadar);

            _windValueLabel = new Label
            {
                Text = "",
                Font = new Font("Consolas", 9.4f, FontStyle.Bold),
                ForeColor = Color.FromArgb(147, 197, 253),
                BackColor = Color.FromArgb(20, 30, 48),
                Location = new Point(12, 136),
                Size = new Size(LW - 24, 16),
                TextAlign = ContentAlignment.MiddleCenter
            };
            windCard.Controls.Add(_windValueLabel);

            _windHint = new Label
            {
                Font = new Font("Segoe UI", 7.2f),
                ForeColor = Color.FromArgb(148, 163, 184),
                BackColor = Color.FromArgb(20, 30, 48),
                Location = new Point(12, 154),
                Size = new Size(LW - 24, 14),
                TextAlign = ContentAlignment.MiddleCenter
            };
            windCard.Controls.Add(_windHint);

            _lightBtn = CreateModernButton(new Point(12, 178), new Size(86, 28), Color.FromArgb(26, 39, 62));
            _lightBtn.Click += (s, e) => ApplyPreset(220, -0.12f, 0.05f, 0.12f, false);
            windCard.Controls.Add(_lightBtn);

            _normalBtn = CreateModernButton(new Point(104, 178), new Size(86, 28), Color.FromArgb(26, 39, 62));
            _normalBtn.Click += (s, e) => ApplyPreset(420, -0.22f, 0.08f, 0.22f, false);
            windCard.Controls.Add(_normalBtn);

            _blizzardBtn = CreateModernButton(new Point(196, 178), new Size(86, 28), Color.FromArgb(26, 39, 62));
            _blizzardBtn.Click += (s, e) => ApplyPreset(1150, -0.82f, 0.52f, 0.65f, true);
            windCard.Controls.Add(_blizzardBtn);

            // ---- Kart 2: Kar Fizik Kaydırıcıları ----
            ModernCard sliderCard = new ModernCard
            {
                Location = new Point(LX, 286),
                Size = new Size(LW, 184)
            };
            Controls.Add(sliderCard);

            _intensitySlider = AddSliderRowInCard(sliderCard, 12, LW - 24, 80, 1200, _overlay.ActiveSnowCount, out _densityCapLabel, out _densityValLabel);
            _densityValLabel.Text = _intensitySlider.Value.ToString();
            _intensitySlider.Scroll += (s, e) =>
            {
                _overlay.ActiveSnowCount = _intensitySlider.Value;
                _densityValLabel.Text = _intensitySlider.Value.ToString();
            };

            _opacitySlider = AddSliderRowInCard(sliderCard, 54, LW - 24, 0, 100, (int)Math.Round(_overlay.WindowInteriorSnowOpacity * 100f), out _opacityCapLabel, out _opacityValLabel);
            _opacityValLabel.Text = "%" + _opacitySlider.Value;
            _opacitySlider.Scroll += (s, e) =>
            {
                _overlay.WindowInteriorSnowOpacity = _opacitySlider.Value / 100f;
                _opacityValLabel.Text = "%" + _opacitySlider.Value;
            };

            _pileSlider = AddSliderRowInCard(sliderCard, 96, LW - 24, 10, 80, (int)Math.Round(_overlay.MaxPileHeight), out _pileCapLabel, out _pileValLabel);
            _pileValLabel.Text = _pileSlider.Value + " px";
            _pileSlider.Scroll += (s, e) =>
            {
                _overlay.MaxPileHeight = _pileSlider.Value;
                _pileValLabel.Text = _pileSlider.Value + " px";
            };

            _meltSlider = AddSliderRowInCard(sliderCard, 138, LW - 24, 0, 100, (int)Math.Round(_overlay.MeltRate / MeltScale), out _meltCapLabel, out _meltValLabel);
            _meltSlider.Scroll += (s, e) =>
            {
                _overlay.MeltRate = _meltSlider.Value * MeltScale;
                SyncMeltLabel();
            };

            // ---- Alt Ana Aksiyonlar (F8 / F7 / F9 / Salla / F10 Kartpostal) ----
            _interactBtn = CreateModernButton(new Point(LX, 482), new Size(LW, 34), Color.FromArgb(2, 132, 199));
            _interactBtn.BorderColor = Color.FromArgb(56, 189, 248);
            _interactBtn.Click += (s, e) => _overlay.ToggleInteractiveMode();
            Controls.Add(_interactBtn);

            _pauseBtn = CreateModernButton(new Point(LX, 524), new Size(143, 32), Color.FromArgb(24, 35, 54));
            _pauseBtn.Click += (s, e) => _overlay.TogglePause();
            Controls.Add(_pauseBtn);

            _clearBtn = CreateModernButton(new Point(LX + 151, 524), new Size(143, 32), Color.FromArgb(44, 24, 34));
            _clearBtn.BorderColor = Color.FromArgb(95, 42, 56);
            _clearBtn.ForeColor = Color.FromArgb(252, 165, 165);
            _clearBtn.Click += (s, e) => _overlay.ClearAllSnow();
            Controls.Add(_clearBtn);

            _postcardQuickBtn = CreateModernButton(new Point(LX, 564), new Size(LW, 32), Color.FromArgb(17, 64, 72));
            _postcardQuickBtn.BorderColor = Color.FromArgb(45, 212, 191);
            _postcardQuickBtn.ForeColor = Color.FromArgb(204, 251, 241);
            _postcardQuickBtn.Click += (s, e) => _overlay.CaptureWinterPostcard();
            Controls.Add(_postcardQuickBtn);

            // ================================================================
            // SAĞ SÜTUN: FERAH 4 SEKMELİ STÜDYO (OYUNCAK / SAHNE / KUTLAMA / SİSTEM)
            // ================================================================
            const int RX = 326;
            const int RW = 324;

            _rightTabs = new SegmentedTabBar
            {
                Location = new Point(RX, 58),
                Size = new Size(RW, 36)
            };
            _rightTabs.SelectedIndexChanged += ShowRightTab;
            Controls.Add(_rightTabs);

            for (int i = 0; i < 4; i++)
            {
                _tabPanels[i] = new Panel
                {
                    Location = new Point(RX, 104),
                    Size = new Size(RW, 492),
                    BackColor = Color.FromArgb(11, 17, 30),
                    Visible = (i == 0)
                };
                Controls.Add(_tabPanels[i]);
            }

            // ---- TAB 0: OYUNCAKLAR ----
            ModernCard snowmanCard = new ModernCard { Location = new Point(0, 0), Size = new Size(RW, 128) };
            _tabPanels[0].Controls.Add(snowmanCard);

            _snowmenCardHdr = CreateCardHeader(14, 9);
            snowmanCard.Controls.Add(_snowmenCardHdr);

            _snowmanBtn = CreateModernButton(new Point(12, 34), new Size(146, 30), Color.FromArgb(20, 56, 88));
            _snowmanBtn.BorderColor = Color.FromArgb(56, 189, 248);
            _snowmanBtn.ForeColor = Color.FromArgb(224, 242, 254);
            _snowmanBtn.Click += (s, e) => _overlay.SpawnDressedSnowmanOnActiveWindow(false);
            snowmanCard.Controls.Add(_snowmanBtn);

            _ballBtn = CreateModernButton(new Point(166, 34), new Size(146, 30), Color.FromArgb(28, 42, 66));
            _ballBtn.Click += (s, e) => _overlay.SpawnSingleSnowballOnActiveWindow();
            snowmanCard.Controls.Add(_ballBtn);

            _fightToggle = CreateCardToggle(snowmanCard, 72, RW, _overlay.SnowmanFightEnabled);
            _fightToggle.CheckedChanged += (s, e) => { _overlay.SnowmanFightEnabled = _fightToggle.Checked; };

            _snowmanTipLbl = new Label
            {
                Location = new Point(14, 100),
                Size = new Size(RW - 28, 22),
                Font = new Font("Segoe UI", 7.3f),
                ForeColor = Color.FromArgb(125, 211, 252),
                BackColor = Color.FromArgb(20, 30, 48)
            };
            snowmanCard.Controls.Add(_snowmanTipLbl);

            // Yılbaşı Ağacı Kartı (YENİ!)
            ModernCard treeCard = new ModernCard { Location = new Point(0, 138), Size = new Size(RW, 102) };
            _tabPanels[0].Controls.Add(treeCard);

            _treeCardHdr = CreateCardHeader(14, 9);
            treeCard.Controls.Add(_treeCardHdr);

            _treeBtn = CreateModernButton(new Point(12, 34), new Size(146, 30), Color.FromArgb(18, 72, 48));
            _treeBtn.BorderColor = Color.FromArgb(52, 211, 153);
            _treeBtn.ForeColor = Color.FromArgb(209, 250, 229);
            _treeBtn.Click += (s, e) => _overlay.SpawnChristmasTree(false);
            treeCard.Controls.Add(_treeBtn);

            _removeTreeBtn = CreateModernButton(new Point(166, 34), new Size(146, 30), Color.FromArgb(46, 26, 34));
            _removeTreeBtn.BorderColor = Color.FromArgb(110, 48, 62);
            _removeTreeBtn.ForeColor = Color.FromArgb(254, 205, 211);
            _removeTreeBtn.Click += (s, e) => _overlay.RemoveLastChristmasTree();
            treeCard.Controls.Add(_removeTreeBtn);

            _treeTipLbl = new Label
            {
                Location = new Point(14, 70),
                Size = new Size(RW - 28, 24),
                Font = new Font("Segoe UI", 7.3f),
                ForeColor = Color.FromArgb(110, 231, 183),
                BackColor = Color.FromArgb(20, 30, 48)
            };
            treeCard.Controls.Add(_treeTipLbl);

            // Penguen Kartı
            ModernCard penguinCard = new ModernCard { Location = new Point(0, 250), Size = new Size(RW, 80) };
            _tabPanels[0].Controls.Add(penguinCard);

            _penguinCardHdr = CreateCardHeader(14, 9);
            penguinCard.Controls.Add(_penguinCardHdr);

            _penguinBtn = CreateModernButton(new Point(12, 36), new Size(146, 30), Color.FromArgb(24, 52, 74));
            _penguinBtn.BorderColor = Color.FromArgb(56, 189, 248);
            _penguinBtn.ForeColor = Color.FromArgb(224, 242, 254);
            _penguinBtn.Click += (s, e) => _overlay.SpawnPenguin(false);
            penguinCard.Controls.Add(_penguinBtn);

            _removePenguinBtn = CreateModernButton(new Point(166, 36), new Size(146, 30), Color.FromArgb(44, 28, 38));
            _removePenguinBtn.BorderColor = Color.FromArgb(95, 50, 72);
            _removePenguinBtn.ForeColor = Color.FromArgb(251, 207, 232);
            _removePenguinBtn.Click += (s, e) => _overlay.RemoveLastPenguin();
            penguinCard.Controls.Add(_removePenguinBtn);

            // Kürek & Kamp Ateşi Kartı
            ModernCard toolsCard = new ModernCard { Location = new Point(0, 340), Size = new Size(RW, 124) };
            _tabPanels[0].Controls.Add(toolsCard);

            _toolsCardHdr = CreateCardHeader(14, 9);
            toolsCard.Controls.Add(_toolsCardHdr);

            _plowBtn = CreateModernButton(new Point(12, 36), new Size(146, 30), Color.FromArgb(145, 86, 10));
            _plowBtn.BorderColor = Color.FromArgb(234, 179, 8);
            _plowBtn.Click += (s, e) => _overlay.SpawnSnowPlow();
            toolsCard.Controls.Add(_plowBtn);

            _removePlowBtn = CreateModernButton(new Point(166, 36), new Size(146, 30), Color.FromArgb(52, 34, 24));
            _removePlowBtn.BorderColor = Color.FromArgb(161, 98, 7);
            _removePlowBtn.ForeColor = Color.FromArgb(254, 240, 138);
            _removePlowBtn.Click += (s, e) => _overlay.RemoveLastSnowPlow();
            toolsCard.Controls.Add(_removePlowBtn);

            _fireBtn = CreateModernButton(new Point(12, 76), new Size(146, 30), Color.FromArgb(124, 45, 18));
            _fireBtn.BorderColor = Color.FromArgb(234, 88, 12);
            _fireBtn.ForeColor = Color.FromArgb(254, 215, 170);
            _fireBtn.Click += (s, e) => _overlay.SpawnCampfire();
            toolsCard.Controls.Add(_fireBtn);

            _removeFireBtn = CreateModernButton(new Point(166, 76), new Size(146, 30), Color.FromArgb(55, 24, 30));
            _removeFireBtn.BorderColor = Color.FromArgb(154, 52, 18);
            _removeFireBtn.ForeColor = Color.FromArgb(253, 186, 116);
            _removeFireBtn.Click += (s, e) => _overlay.RemoveLastCampfire();
            toolsCard.Controls.Add(_removeFireBtn);

            // ---- TAB 1: SAHNE & DOĞA ----
            ModernCard atmoCard = new ModernCard { Location = new Point(0, 0), Size = new Size(RW, 224) };
            _tabPanels[1].Controls.Add(atmoCard);

            _atmoCardHdr = CreateCardHeader(14, 10);
            atmoCard.Controls.Add(_atmoCardHdr);

            _lightsToggle = CreateCardToggle(atmoCard, 38, RW, _overlay.FairyLightsEnabled);
            _lightsToggle.CheckedChanged += (s, e) => { _overlay.FairyLightsEnabled = _lightsToggle.Checked; };

            _iciclesToggle = CreateCardToggle(atmoCard, 68, RW, _overlay.IciclesEnabled);
            _iciclesToggle.CheckedChanged += (s, e) => { _overlay.IciclesEnabled = _iciclesToggle.Checked; };

            _frostToggle = CreateCardToggle(atmoCard, 98, RW, _overlay.FrostEnabled);
            _frostToggle.CheckedChanged += (s, e) => { _overlay.FrostEnabled = _frostToggle.Checked; };

            _auroraToggle = CreateCardToggle(atmoCard, 128, RW, _overlay.AuroraEnabled);
            _auroraToggle.CheckedChanged += (s, e) => { _overlay.AuroraEnabled = _auroraToggle.Checked; };

            _santaToggle = CreateCardToggle(atmoCard, 158, RW, _overlay.SantaEnabled);
            _santaToggle.CheckedChanged += (s, e) =>
            {
                _overlay.SantaEnabled = _santaToggle.Checked;
                if (_overlay.SantaEnabled) _overlay.SummonSantaSoon();
            };

            _lanternsToggle = CreateCardToggle(atmoCard, 188, RW, _overlay.LanternsEnabled);
            _lanternsToggle.CheckedChanged += (s, e) => { _overlay.LanternsEnabled = _lanternsToggle.Checked; };

            ModernCard lifeCard = new ModernCard { Location = new Point(0, 236), Size = new Size(RW, 162) };
            _tabPanels[1].Controls.Add(lifeCard);

            _lifeCardHdr = CreateCardHeader(14, 10);
            lifeCard.Controls.Add(_lifeCardHdr);

            _cableCarToggle = CreateCardToggle(lifeCard, 38, RW, _overlay.CableCarEnabled);
            _cableCarToggle.CheckedChanged += (s, e) => { _overlay.CableCarEnabled = _cableCarToggle.Checked; };

            _trainToggle = CreateCardToggle(lifeCard, 68, RW, _overlay.TrainEnabled);
            _trainToggle.CheckedChanged += (s, e) => { _overlay.TrainEnabled = _trainToggle.Checked; };

            _birdsToggle = CreateCardToggle(lifeCard, 98, RW, _overlay.BirdsEnabled);
            _birdsToggle.CheckedChanged += (s, e) =>
            {
                _overlay.BirdsEnabled = _birdsToggle.Checked;
                _overlay.EnsureWinterBirdsCount();
            };

            _cursorTrailToggle = CreateCardToggle(lifeCard, 128, RW, _overlay.CursorTrailEnabled);
            _cursorTrailToggle.CheckedChanged += (s, e) => { _overlay.CursorTrailEnabled = _cursorTrailToggle.Checked; };

            ModernCard tipCard = new ModernCard { Location = new Point(0, 410), Size = new Size(RW, 72) };
            _tabPanels[1].Controls.Add(tipCard);

            _toysHintLbl = new Label
            {
                Location = new Point(12, 10),
                Size = new Size(RW - 24, 52),
                Font = new Font("Segoe UI", 7.6f),
                ForeColor = Color.FromArgb(148, 163, 184),
                BackColor = Color.FromArgb(20, 30, 48)
            };
            tipCard.Controls.Add(_toysHintLbl);

            // ---- TAB 2: ARKA PLAN, KUTLAMA & SES ----
            ModernCard wpCard = new ModernCard { Location = new Point(0, 0), Size = new Size(RW, 174) };
            _tabPanels[2].Controls.Add(wpCard);

            _wpCardHdr = CreateCardHeader(14, 9);
            wpCard.Controls.Add(_wpCardHdr);

            _wpChips = new OptionChipSelector
            {
                Location = new Point(12, 33),
                Size = new Size(RW - 24, 68)
            };
            _wpChips.SelectedIndexChanged += idx =>
            {
                _overlay.ApplyWallpaperTheme(idx, _overlay.CustomWallpaperPath, true);
            };
            wpCard.Controls.Add(_wpChips);

            _wpCustomBtn = CreateModernButton(new Point(12, 108), new Size(146, 28), Color.FromArgb(24, 44, 72));
            _wpCustomBtn.BorderColor = Color.FromArgb(56, 189, 248);
            _wpCustomBtn.ForeColor = Color.FromArgb(224, 242, 254);
            _wpCustomBtn.Click += (s, e) =>
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Title = Tr("Kış Masaüstü Arka Planı Seç", "Choose Winter Desktop Wallpaper");
                    ofd.Filter = Tr("Resim Dosyaları (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp", "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp");
                    if (ofd.ShowDialog(this) == DialogResult.OK)
                    {
                        _wpChips.SelectedIndex = -1;
                        _overlay.ApplyWallpaperTheme(6, ofd.FileName, true);
                    }
                }
            };
            wpCard.Controls.Add(_wpCustomBtn);

            _wpRestoreBtn = CreateModernButton(new Point(166, 108), new Size(146, 28), Color.FromArgb(44, 28, 38));
            _wpRestoreBtn.BorderColor = Color.FromArgb(110, 52, 74);
            _wpRestoreBtn.ForeColor = Color.FromArgb(254, 205, 211);
            _wpRestoreBtn.Click += (s, e) =>
            {
                _wpChips.SelectedIndex = 0;
                _overlay.RestoreOriginalWallpaper(true);
            };
            wpCard.Controls.Add(_wpRestoreBtn);

            _wpRememberToggle = CreateCardToggle(wpCard, 142, RW, _overlay.RememberWallpaper);
            _wpRememberToggle.CheckedChanged += (s, e) => { _overlay.RememberWallpaper = _wpRememberToggle.Checked; };

            ModernCard celebCard = new ModernCard { Location = new Point(0, 184), Size = new Size(RW, 106) };
            _tabPanels[2].Controls.Add(celebCard);

            _celebCardHdr = CreateCardHeader(14, 9);
            celebCard.Controls.Add(_celebCardHdr);

            _countdownToggle = CreateCardToggle(celebCard, 35, RW, _overlay.CountdownEnabled);
            _countdownToggle.CheckedChanged += (s, e) => { _overlay.CountdownEnabled = _countdownToggle.Checked; };

            _testCelebBtn = CreateModernButton(new Point(12, 66), new Size(RW - 24, 30), Color.FromArgb(113, 63, 18));
            _testCelebBtn.BorderColor = Color.FromArgb(250, 204, 21);
            _testCelebBtn.ForeColor = Color.FromArgb(254, 240, 138);
            _testCelebBtn.Click += (s, e) => _overlay.TriggerMidnightCelebration();
            celebCard.Controls.Add(_testCelebBtn);

            ModernCard audioCard = new ModernCard { Location = new Point(0, 300), Size = new Size(RW, 182) };
            _tabPanels[2].Controls.Add(audioCard);

            _audioCardHdr = CreateCardHeader(14, 10);
            audioCard.Controls.Add(_audioCardHdr);

            _soundToggle = CreateCardToggle(audioCard, 38, RW, _overlay.SoundEnabled);
            _soundToggle.CheckedChanged += (s, e) => { _overlay.SoundEnabled = _soundToggle.Checked; };

            _audioReactiveToggle = CreateCardToggle(audioCard, 72, RW, _overlay.AudioReactiveEnabled);
            _audioReactiveToggle.CheckedChanged += (s, e) => { _overlay.AudioReactiveEnabled = _audioReactiveToggle.Checked; };

            _audioMeterLbl = new Label
            {
                Location = new Point(14, 106),
                AutoSize = true,
                Font = new Font("Segoe UI", 7.6f),
                ForeColor = Color.FromArgb(148, 163, 184),
                BackColor = Color.FromArgb(20, 30, 48)
            };
            audioCard.Controls.Add(_audioMeterLbl);

            _audioBar = new AudioLevelBar
            {
                Location = new Point(14, 125),
                Size = new Size(RW - 28, 12)
            };
            audioCard.Controls.Add(_audioBar);

            _audioHintLbl = new Label
            {
                Location = new Point(14, 142),
                Size = new Size(RW - 28, 30),
                Font = new Font("Segoe UI", 7.2f),
                ForeColor = Color.FromArgb(125, 211, 252),
                BackColor = Color.FromArgb(20, 30, 48)
            };
            audioCard.Controls.Add(_audioHintLbl);

            // ---- TAB 3: SİSTEM & KAYIT ----
            ModernCard displayCard = new ModernCard { Location = new Point(0, 0), Size = new Size(RW, 148) };
            _tabPanels[3].Controls.Add(displayCard);

            _sysDisplayHdr = CreateCardHeader(14, 10);
            displayCard.Controls.Add(_sysDisplayHdr);

            _multiMonBtn = CreateModernButton(new Point(12, 36), new Size(RW - 24, 32), Color.FromArgb(24, 35, 54));
            _multiMonBtn.Click += (s, e) =>
            {
                _overlay.SetMultiMonitorMode(!_overlay.MultiMonitorEnabled);
                SyncMultiMonitorButton();
            };
            displayCard.Controls.Add(_multiMonBtn);

            _perfToggle = CreateCardToggle(displayCard, 78, RW, _overlay.PerformanceMode);
            _perfToggle.CheckedChanged += (s, e) => { _overlay.PerformanceMode = _perfToggle.Checked; };

            _autoPauseToggle = CreateCardToggle(displayCard, 110, RW, _overlay.AutoPauseOnFullscreen);
            _autoPauseToggle.CheckedChanged += (s, e) => { _overlay.AutoPauseOnFullscreen = _autoPauseToggle.Checked; };

            ModernCard saveCard = new ModernCard { Location = new Point(0, 160), Size = new Size(RW, 148) };
            _tabPanels[3].Controls.Add(saveCard);

            _sysSaveHdr = CreateCardHeader(14, 10);
            saveCard.Controls.Add(_sysSaveHdr);

            _startupToggle = CreateCardToggle(saveCard, 38, RW, StartupManager.IsEnabled());
            _startupToggle.CheckedChanged += (s, e) => { StartupManager.SetEnabled(_startupToggle.Checked); };

            _rememberSnowmenToggle = CreateCardToggle(saveCard, 70, RW, _overlay.RememberSnowmen);
            _rememberSnowmenToggle.CheckedChanged += (s, e) => { _overlay.RememberSnowmen = _rememberSnowmenToggle.Checked; };

            _saveNowBtn = CreateModernButton(new Point(12, 104), new Size(RW - 24, 32), Color.FromArgb(20, 83, 45));
            _saveNowBtn.BorderColor = Color.FromArgb(52, 211, 153);
            _saveNowBtn.ForeColor = Color.FromArgb(209, 250, 229);
            _saveNowBtn.Click += (s, e) =>
            {
                SettingsStore.Save(_overlay);
                _overlay.SaveSnowmenNow();
                _saveNowBtn.Text = Tr("✓ Ayarlar & Sahne Kaydedildi!", "✓ Settings & Scene Saved!");
            };
            saveCard.Controls.Add(_saveNowBtn);

            ModernCard shortcutCard = new ModernCard { Location = new Point(0, 320), Size = new Size(RW, 162) };
            _tabPanels[3].Controls.Add(shortcutCard);

            _shortcutsHdr = CreateCardHeader(14, 10);
            shortcutCard.Controls.Add(_shortcutsHdr);

            _shortcutsBody = new Label
            {
                Location = new Point(14, 34),
                Size = new Size(RW - 28, 118),
                Font = new Font("Segoe UI", 7.8f),
                ForeColor = Color.FromArgb(180, 196, 216),
                BackColor = Color.FromArgb(20, 30, 48)
            };
            shortcutCard.Controls.Add(_shortcutsBody);

            _overlay.OnInteractiveModeChanged += SyncInteractiveBadge;
            _overlay.OnPauseChanged += SyncPauseButton;

            // Canlı Ses Barı UI Zamanlayıcısı (Tab 2 açıkken güncellenir)
            _uiTimer = new Timer { Interval = 40 };
            _uiTimer.Tick += (s, e) =>
            {
                if (Visible && _tabPanels[2].Visible)
                    _audioBar.Level = _overlay.AudioReactiveEnabled ? _overlay.AudioLevel : 0f;
            };
            _uiTimer.Start();

            Icon customAppIcon = AppIconGenerator.CreateRuntimeIcon();
            this.Icon = customAppIcon;

            _trayIcon = new NotifyIcon
            {
                Icon = customAppIcon,
                Text = "Snowy for Windows",
                Visible = true
            };
            ContextMenu trayMenu = new ContextMenu();
            _trayShowItem = new MenuItem("", (s, e) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
            _trayInteractItem = new MenuItem("", (s, e) => _overlay.ToggleInteractiveMode());
            _traySurpriseItem = new MenuItem("", (s, e) => _overlay.TriggerSurpriseShow());
            _trayPostcardItem = new MenuItem("", (s, e) => _overlay.CaptureWinterPostcard());
            _trayPauseItem = new MenuItem("", (s, e) => _overlay.TogglePause());
            _trayClearItem = new MenuItem("", (s, e) => _overlay.ClearAllSnow());
            _trayExitItem = new MenuItem("", (s, e) => Application.Exit());
            trayMenu.MenuItems.Add(_trayShowItem);
            trayMenu.MenuItems.Add(_trayInteractItem);
            trayMenu.MenuItems.Add(_traySurpriseItem);
            trayMenu.MenuItems.Add(_trayPostcardItem);
            trayMenu.MenuItems.Add(_trayPauseItem);
            trayMenu.MenuItems.Add(_trayClearItem);
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add(_trayExitItem);
            _trayIcon.ContextMenu = trayMenu;
            _trayIcon.DoubleClick += (s, e) => { Show(); WindowState = FormWindowState.Normal; Activate(); };

            ApplyLanguage();
            _wpChips.SelectedIndex = (_overlay.WallpaperTheme >= 0 && _overlay.WallpaperTheme <= 5) ? _overlay.WallpaperTheme : -1;
            UpdateWindStatus(_overlay.WindNx, _overlay.WindNy);

            FormClosing += (s, e) =>
            {
                _uiTimer.Stop();
                _uiTimer.Dispose();
                SettingsStore.Save(_overlay);
                _overlay.ShutdownExtras();
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                Application.Exit();
            };

            ResumeLayout(true);
        }

        private void ShowRightTab(int idx)
        {
            for (int i = 0; i < _tabPanels.Length; i++)
                _tabPanels[i].Visible = (i == idx);
        }

        private static Label CreateCardHeader(int x, int y)
        {
            return new Label
            {
                Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(226, 232, 240),
                BackColor = Color.FromArgb(20, 30, 48),
                Location = new Point(x, y),
                AutoSize = true
            };
        }

        private static ModernToggle CreateCardToggle(ModernCard card, int y, int cardW, bool initial)
        {
            ModernToggle t = new ModernToggle
            {
                Location = new Point(14, y),
                Size = new Size(cardW - 28, 24),
                Checked = initial
            };
            card.Controls.Add(t);
            return t;
        }

        private static ModernSlider AddSliderRowInCard(ModernCard card, int y, int width, int min, int max, int value, out Label capLabel, out Label valueLabel)
        {
            capLabel = new Label
            {
                Font = new Font("Segoe UI", 8.2f),
                ForeColor = Color.FromArgb(203, 213, 225),
                BackColor = Color.FromArgb(20, 30, 48),
                Location = new Point(12, y),
                AutoSize = true
            };
            card.Controls.Add(capLabel);

            valueLabel = new Label
            {
                Font = new Font("Consolas", 8.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                BackColor = Color.FromArgb(20, 30, 48),
                Location = new Point(12 + width - 86, y),
                Size = new Size(86, 16),
                TextAlign = ContentAlignment.MiddleRight
            };
            card.Controls.Add(valueLabel);

            ModernSlider sl = new ModernSlider
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Max(min, Math.Min(max, value)),
                Location = new Point(12, y + 16),
                Size = new Size(width, 20)
            };
            card.Controls.Add(sl);
            return sl;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int dark = 1;
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                int corner = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (LinearGradientBrush accent = new LinearGradientBrush(
                new Rectangle(0, 0, Width, 3),
                Color.FromArgb(56, 189, 248),
                Color.FromArgb(52, 211, 153),
                LinearGradientMode.Horizontal))
            {
                e.Graphics.FillRectangle(accent, 0, 0, Width, 3);
            }
            using (Pen borderPen = new Pen(Color.FromArgb(46, 66, 98), 1f))
                e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int maxH = wa.Height - 20;
            if (Height > maxH) Height = maxH;
            Left = wa.Left + 24;
            Top = wa.Top + 18;
        }

        private string Tr(string tr, string en)
        {
            return _overlay.IsEnglish ? en : tr;
        }

        private void ApplyLanguage()
        {
            _langBtn.Text = _overlay.IsEnglish ? "🌐 EN" : "🌐 TR";
            _surpriseBtn.Text = Tr("🎆 Sürpriz (F6)", "🎆 Surprise (F6)");
            _windHdr.Text = Tr("💨 Rüzgar & Hava Durumu", "💨 Wind & Weather");
            _resetBtn.Text = Tr("Sıfırla", "Reset");
            _windHint.Text = Tr("Merkezden sürükleyerek yönü ayarlayın", "Drag from center to set wind vector");

            _densityCapLabel.Text = Tr("Kar Yoğunluğu", "Snow Density");
            _opacityCapLabel.Text = Tr("Uygulama İçi Kar", "Window Snow Opacity");
            _pileCapLabel.Text = Tr("Maks. Kar Kalınlığı", "Max Pile Thickness");
            _meltCapLabel.Text = Tr("Erime Hızı", "Melt Speed");

            _lightBtn.Text = Tr("🌤 Hafif", "🌤 Light");
            _normalBtn.Text = Tr("❄ Normal", "❄ Normal");
            _blizzardBtn.Text = Tr("🌪 Tipi", "🌪 Blizzard");
            _clearBtn.Text = Tr("🧹 Temizle (F9)", "🧹 Clear (F9)");
            _postcardQuickBtn.Text = Tr("📸 Kış Kartpostalı Çek (F10)", "📸 Capture Winter Postcard (F10)");

            _rightTabs.SetTabs(new string[] {
                Tr("⛄ Oyuncak", "⛄ Toys"),
                Tr("🌌 Sahne", "🌌 Scene"),
                Tr("🖼️ Tema", "🖼️ Theme"),
                Tr("⚙️ Sistem", "⚙️ System")
            });

            // Tab 0: Oyuncaklar
            _snowmenCardHdr.Text = Tr("⛄ Kardan Adamlar & Kartopu", "⛄ Snowmen & Snowballs");
            _snowmanBtn.Text = Tr("⛄ Kardan Adam", "⛄ Add Snowman");
            _ballBtn.Text = Tr("⚪ Kartopu Ekle", "⚪ Add Snowball");
            _fightToggle.Text = Tr("🥊 Kardan Adam Savaşı (AI)", "🥊 Snowman Fight (AI)");
            _snowmanTipLbl.Text = Tr("F8'de sağ tıkla → Gardırop & Renkli İsim Etiketi!", "Right-click in F8 → Wardrobe & Colored Name Tag!");

            _treeCardHdr.Text = Tr("🎄 Özelleştirilebilir Yılbaşı Ağacı", "🎄 Custom Christmas Tree");
            _treeBtn.Text = Tr("🎄 Ağaç Ekle", "🎄 Add Tree");
            _removeTreeBtn.Text = Tr("🗑 Ağaç Sil", "🗑 Remove Tree");
            _treeTipLbl.Text = Tr("F8'de sağ tıkla → Süsle, Işık Seç & %60–%160 Boyutlandır!", "Right-click in F8 → Decorate, Lights & Resize 60%–160%!");

            _penguinCardHdr.Text = Tr("🐧 Masaüstü Penguenleri", "🐧 Desktop Penguins");
            _penguinBtn.Text = Tr("🐧 Penguen Ekle", "🐧 Add Penguin");
            _removePenguinBtn.Text = Tr("🗑 Penguen Sil", "🗑 Remove");

            _toolsCardHdr.Text = Tr("🚜 Kar Küreme & 🔥 Kamp Ateşi", "🚜 Snowplow & 🔥 Campfire");
            _plowBtn.Text = Tr("🚜 Kürek Ekle", "🚜 Add Plow");
            _removePlowBtn.Text = Tr("🗑 Kürek Sil", "🗑 Remove Plow");
            _fireBtn.Text = Tr("🔥 Kamp Ateşi", "🔥 Add Campfire");
            _removeFireBtn.Text = Tr("🗑 Ateş Sil", "🗑 Remove Fire");

            // Tab 1: Sahne & Doğa
            _atmoCardHdr.Text = Tr("✨ Gökyüzü, Dekor & Fenerler", "✨ Sky, Decor & Lanterns");
            _lightsToggle.Text = Tr("🎄 Pencere Işıkları", "🎄 Window Fairy Lights");
            _iciclesToggle.Text = Tr("🧊 Buz Sarkıtları", "🧊 Window Icicles");
            _frostToggle.Text = Tr("❄️ Ekran Köşesi Buzlanması", "❄️ Interactive Corner Frost");
            _auroraToggle.Text = Tr("🌌 Kuzey Işıkları & Yıldızlar", "🌌 Aurora & Shooting Stars");
            _santaToggle.Text = Tr("🎅 Noel Baba & Ren Geyikleri", "🎅 Santa & Reindeer Flyby");
            _lanternsToggle.Text = Tr("🏮 Süzülen Dilek Fenerleri", "🏮 Floating Wish Lanterns");

            _lifeCardHdr.Text = Tr("🚂 Ulaşım, Kuşlar & İmleç Büyüsü", "🚂 Transit, Birds & Magic Trail");
            _cableCarToggle.Text = Tr("🚡 Pencereler Arası Teleferik", "🚡 Inter-Window Cable Car");
            _trainToggle.Text = Tr("🚂 Kutup Ekspresi Treni", "🚂 Polar Express Train");
            _birdsToggle.Text = Tr("🐦 Kış Kuşları (Ağaç & Pencere)", "🐦 Winter Birds (Robin & Owl)");
            _cursorTrailToggle.Text = Tr("✨ Fare İmleci Kış Işıltısı", "✨ Cursor Magic Snow Trail");

            _toysHintLbl.Text = Tr(
                "💡 İpucu: Kış kuşları pencerelere ve yılbaşı ağacının dallarına konar! F8 modunda dilek fenerlerine, hediyelere veya trene tıklayabilirsiniz.",
                "💡 Tip: Winter birds perch on windows and Christmas tree branches! In F8 mode, click wish lanterns, gifts, or the Polar Express.");

            // Tab 2: Arka Plan, Kutlama & Ses
            _wpCardHdr.Text = Tr("🖼️ Masaüstü Arka Planı (Çıkınca Eskiye Döner)", "🖼️ Desktop Wallpaper (Reverts on Exit)");
            _wpChips.SetItems(new string[] {
                Tr("↩️ Orijinal", "↩️ Original"),
                Tr("🌌 Kutup", "🌌 Arctic"),
                Tr("🌕 Dolunay", "🌕 Moon"),
                Tr("❄️ Fiyort", "❄️ Fjord"),
                Tr("✨ Bokeh", "✨ Bokeh"),
                Tr("🏡 Dağ Evi", "🏡 Cabin")
            }, 3);
            _wpCustomBtn.Text = Tr("📂 Özel Fotoğraf", "📂 Custom Image");
            _wpRestoreBtn.Text = Tr("↩️ Orijinale Dön", "↩️ Restore Original");
            _wpRememberToggle.Text = Tr("🔄 Açılışta Seçili Temayı Hatırla", "🔄 Remember Theme on Startup");

            _celebCardHdr.Text = Tr("⏳ Yılbaşı Geri Sayımı & Kutlama", "⏳ New Year Countdown & Party");
            _countdownToggle.Text = Tr("⏳ Yeni Yıl Geri Sayım Sayacı", "⏳ New Year Countdown Widget");
            _testCelebBtn.Text = Tr("🎉 Gece Yarısı Kutlamasını Test Et", "🎉 Test Midnight Celebration");

            _audioCardHdr.Text = Tr("🎧 Ses & Müzik Etkileşimi", "🎧 Sound & Music Sync");
            _soundToggle.Text = Tr("🔊 Kış Ses Efektleri", "🔊 Winter Sound Effects");
            _audioReactiveToggle.Text = Tr("🎵 Müziğe Duyarlı Mod (WASAPI)", "🎵 Music Reactive (WASAPI)");
            _audioMeterLbl.Text = Tr("Canlı Sistem Ses Seviyesi", "Live System Audio Level");
            _audioHintLbl.Text = Tr("Spotify / YouTube ritmiyle ağaç ışıkları, aurora ve kardan adamlar tepki verir.", "Tree lights, aurora, penguins & snowmen pulse with Spotify / YouTube music.");

            // Tab 3: Sistem & Kayıt
            _sysDisplayHdr.Text = Tr("🖥️ Ekran & Performans", "🖥️ Display & Performance");
            _perfToggle.Text = Tr("⚡ Performans Modu (Hafif)", "⚡ Performance Mode (Light)");
            _autoPauseToggle.Text = Tr("🎬 Tam Ekranda Duraklat", "🎬 Auto-Pause in Fullscreen");

            _sysSaveHdr.Text = Tr("💾 Başlangıç & Sahne Kaydı", "💾 Startup & Persistence");
            _startupToggle.Text = Tr("🚀 Windows ile Otomatik Başlat", "🚀 Start with Windows");
            _rememberSnowmenToggle.Text = Tr("💾 Kardan Adam & Ağaçları Hatırla", "💾 Remember Snowmen & Trees");
            _saveNowBtn.Text = Tr("💾 Ayarları & Sahneyi Şimdi Kaydet", "💾 Save Settings & Scene Now");

            _shortcutsHdr.Text = Tr("⌨️ Hızlı Kısayollar", "⌨️ Quick Shortcuts");
            _shortcutsBody.Text = Tr(
                "• F8 : Etkileşim / Geçirgen Mod\n• F6 : Sürpriz Havai Fişek & Noel Baba\n• F10 : Kış Kartpostalı Çek (PNG)\n• F7 : Duraklat   •   F9 : Tümünü Temizle\n• Sağ Tık ⛄ / 🎄 : Gardırop / Ağaç Süsle\n• Orta Tık : Havai Fişek   •   Kapanınca Arka Plan Eskiye Döner",
                "• F8 : Interactive / Click-Through Mode\n• F6 : Surprise Fireworks & Santa Show\n• F10 : Capture Winter Postcard (PNG)\n• F7 : Pause   •   F9 : Clear All\n• Right-Click ⛄ / 🎄 : Wardrobe / Tree Studio\n• Middle-Click : Firework   •   Wallpaper Auto-Restores on Exit");

            // Tray
            _trayShowItem.Text = Tr("Kontrol Panelini Göster", "Show Control Panel");
            _trayInteractItem.Text = Tr("Etkileşim Modu (F8)", "Interactive Mode (F8)");
            _traySurpriseItem.Text = Tr("Sürpriz Gösteri! (F6)", "Surprise Show! (F6)");
            _trayPostcardItem.Text = Tr("Kış Kartpostalı Çek (F10)", "Capture Postcard (F10)");
            _trayPauseItem.Text = Tr("Duraklat / Devam (F7)", "Pause / Resume (F7)");
            _trayClearItem.Text = Tr("Karları Temizle (F9)", "Clear Snow (F9)");
            _trayExitItem.Text = Tr("Çıkış", "Exit");

            SyncMeltLabel();
            SyncPauseButton();
            SyncInteractiveBadge();
            SyncMultiMonitorButton();
        }

        private void SyncMeltLabel()
        {
            _meltValLabel.Text = _meltSlider.Value == 0 ? Tr("Kapalı", "Off") : "%" + _meltSlider.Value;
        }

        private void ApplyPreset(int density, float nx, float ny, float gust, bool blizzard)
        {
            _intensitySlider.Value = Math.Max(_intensitySlider.Minimum, Math.Min(_intensitySlider.Maximum, density));
            _overlay.ActiveSnowCount = _intensitySlider.Value;
            _densityValLabel.Text = _intensitySlider.Value.ToString();

            _windRadar.Nx = nx;
            _windRadar.Ny = ny;
            _windRadar.Invalidate();
            _overlay.GustAmount = gust;
            _overlay.BlizzardMode = blizzard;
            UpdateWindStatus(nx, ny);
        }

        private static ModernButton CreateModernButton(Point loc, Size size, Color bg)
        {
            return new ModernButton
            {
                Location = loc,
                Size = size,
                FillColor = bg,
                CornerRadius = 8
            };
        }

        private void UpdateWindStatus(float nx, float ny)
        {
            _overlay.WindNx = nx;
            _overlay.WindNy = ny;
            float mag = (float)Math.Min(1.0, Math.Sqrt(nx * nx + ny * ny));
            int pct = (int)Math.Round(mag * 100f);
            string dirStr;
            if (pct < 4)
            {
                dirStr = "CALM";
            }
            else
            {
                double deg = (Math.Atan2(ny, nx) * 180.0 / Math.PI + 360.0) % 360.0;
                string[] dirs = { "E", "SE", "S", "SW", "W", "NW", "N", "NE" };
                dirStr = dirs[(int)(((deg + 22.5) % 360.0) / 45.0)];
            }
            _windValueLabel.Text = pct + "% - " + dirStr;
        }

        private void SyncMultiMonitorButton()
        {
            if (_overlay.MultiMonitorEnabled)
            {
                int total = Math.Min(3, Screen.AllScreens != null ? Screen.AllScreens.Length : 1);
                _multiMonBtn.Text = Tr("🖥️ Çoklu Ekran: Açık (" + total + " Ekran)", "🖥️ Multi-Monitor: On (" + total + " Screens)");
                _multiMonBtn.FillColor = Color.FromArgb(14, 116, 144);
                _multiMonBtn.BorderColor = Color.FromArgb(56, 189, 248);
                _multiMonBtn.ForeColor = Color.FromArgb(224, 242, 254);
            }
            else
            {
                _multiMonBtn.Text = Tr("🖥️ Çoklu Ekran: Kapalı (Ana Ekran)", "🖥️ Multi-Monitor: Off (Primary)");
                _multiMonBtn.FillColor = Color.FromArgb(24, 35, 54);
                _multiMonBtn.BorderColor = Color.FromArgb(46, 62, 88);
                _multiMonBtn.ForeColor = Color.White;
            }
            _multiMonBtn.Invalidate();
        }

        private void SyncPauseButton()
        {
            if (_overlay.Paused)
            {
                _pauseBtn.Text = Tr("▶ Devam Et (F7)", "▶ Resume (F7)");
                _pauseBtn.FillColor = Color.FromArgb(180, 83, 9);
                _pauseBtn.BorderColor = Color.FromArgb(251, 191, 36);
            }
            else
            {
                _pauseBtn.Text = Tr("⏸ Duraklat (F7)", "⏸ Pause (F7)");
                _pauseBtn.FillColor = Color.FromArgb(24, 35, 54);
                _pauseBtn.BorderColor = Color.FromArgb(46, 62, 88);
            }
            _pauseBtn.Invalidate();
        }

        private void SyncInteractiveBadge()
        {
            if (_overlay.InteractiveMode)
            {
                _modeBadge.Text = Tr("Etkileşim", "Interactive");
                _modeBadge.ForeColor = Color.FromArgb(253, 224, 71);
                _modeBadge.BackColor = Color.FromArgb(66, 32, 6);
                _interactBtn.Text = Tr("🔓 Geçirgen Moda Dön (F8)", "🔓 Click-Through Mode (F8)");
                _interactBtn.FillColor = Color.FromArgb(180, 118, 8);
                _interactBtn.BorderColor = Color.FromArgb(250, 204, 21);
            }
            else
            {
                _modeBadge.Text = Tr("Geçirgen", "Pass-Thru");
                _modeBadge.ForeColor = Color.FromArgb(56, 189, 248);
                _modeBadge.BackColor = Color.FromArgb(24, 38, 60);
                _interactBtn.Text = Tr("🖱️ Etkileşim Modunu Aç (F8)", "🖱️ Interactive Mode (F8)");
                _interactBtn.FillColor = Color.FromArgb(2, 132, 199);
                _interactBtn.BorderColor = Color.FromArgb(56, 189, 248);
            }
            _interactBtn.Invalidate();
        }
    }

    // ========================================================================
    // 10b. AYARLAR, KARDAN ADAMLAR & YILBAŞI AĞAÇLARI KALICI KAYDI (%AppData%\SnowyWindows\)
    // ========================================================================
    internal static class SettingsStore
    {
        private static string FilePath
        {
            get
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnowyWindows");
                return System.IO.Path.Combine(dir, "settings.ini");
            }
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return Math.Max(lo, Math.Min(hi, v));
        }

        public static void Load(SnowOverlayForm o)
        {
            try
            {
                string path = FilePath;
                if (!System.IO.File.Exists(path)) return;

                string[] lines = System.IO.File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq <= 0) continue;
                    string key = lines[i].Substring(0, eq).Trim();
                    string val = lines[i].Substring(eq + 1).Trim();
                    if (key == "customWallpaperB64")
                    {
                        if (!string.IsNullOrEmpty(val))
                            o.CustomWallpaperPath = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(val));
                        continue;
                    }
                    float f;
                    if (!float.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out f))
                        continue;

                    switch (key)
                    {
                        case "density": o.ActiveSnowCount = (int)Clamp(f, 80f, 1200f); break;
                        case "interiorOpacity": o.WindowInteriorSnowOpacity = Clamp(f, 0f, 1f); break;
                        case "maxPile": o.MaxPileHeight = Clamp(f, 10f, 80f); break;
                        case "windNx": o.WindNx = Clamp(f, -1f, 1f); break;
                        case "windNy": o.WindNy = Clamp(f, -1f, 1f); break;
                        case "gust": o.GustAmount = Clamp(f, 0f, 1f); break;
                        case "melt": o.MeltRate = Clamp(f, 0f, 0.0008f); break;
                        case "lights": o.FairyLightsEnabled = f > 0.5f; break;
                        case "icicles": o.IciclesEnabled = f > 0.5f; break;
                        case "cableCar": o.CableCarEnabled = f > 0.5f; break;
                        case "santa": o.SantaEnabled = f > 0.5f; break;
                        case "aurora": o.AuroraEnabled = f > 0.5f; break;
                        case "frost": o.FrostEnabled = f > 0.5f; break;
                        case "train": o.TrainEnabled = f > 0.5f; break;
                        case "fight": o.SnowmanFightEnabled = f > 0.5f; break;
                        case "sound": o.SoundEnabled = f > 0.5f; break;
                        case "audioReactive": o.AudioReactiveEnabled = f > 0.5f; break;
                        case "rememberSnowmen": o.RememberSnowmen = f > 0.5f; break;
                        case "countdown": o.CountdownEnabled = f > 0.5f; break;
                        case "lanterns": o.LanternsEnabled = f > 0.5f; break;
                        case "birds": o.BirdsEnabled = f > 0.5f; break;
                        case "cursorTrail": o.CursorTrailEnabled = f > 0.5f; break;
                        case "wallpaperTheme": o.WallpaperTheme = (int)Clamp(f, 0f, 6f); break;
                        case "rememberWallpaper": o.RememberWallpaper = f > 0.5f; break;
                        case "blizzard": o.BlizzardMode = f > 0.5f; break;
                        case "multiMonitor": if (f > 0.5f) o.SetMultiMonitorMode(true); break;
                        case "autoPause": o.AutoPauseOnFullscreen = f > 0.5f; break;
                        case "perf": o.PerformanceMode = f > 0.5f; break;
                        case "lang": o.IsEnglish = f > 0.5f; break;
                    }
                }
            }
            catch { }
        }

        public static void Save(SnowOverlayForm o)
        {
            try
            {
                string path = FilePath;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.Globalization.CultureInfo ci = System.Globalization.CultureInfo.InvariantCulture;
                string customWpB64 = string.IsNullOrEmpty(o.CustomWallpaperPath)
                    ? ""
                    : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(o.CustomWallpaperPath));
                string[] lines =
                {
                    "density=" + o.ActiveSnowCount.ToString(ci),
                    "interiorOpacity=" + o.WindowInteriorSnowOpacity.ToString("0.###", ci),
                    "maxPile=" + o.MaxPileHeight.ToString("0.#", ci),
                    "windNx=" + o.WindNx.ToString("0.###", ci),
                    "windNy=" + o.WindNy.ToString("0.###", ci),
                    "gust=" + o.GustAmount.ToString("0.###", ci),
                    "melt=" + o.MeltRate.ToString("0.#######", ci),
                    "lights=" + (o.FairyLightsEnabled ? "1" : "0"),
                    "icicles=" + (o.IciclesEnabled ? "1" : "0"),
                    "cableCar=" + (o.CableCarEnabled ? "1" : "0"),
                    "santa=" + (o.SantaEnabled ? "1" : "0"),
                    "aurora=" + (o.AuroraEnabled ? "1" : "0"),
                    "frost=" + (o.FrostEnabled ? "1" : "0"),
                    "train=" + (o.TrainEnabled ? "1" : "0"),
                    "fight=" + (o.SnowmanFightEnabled ? "1" : "0"),
                    "sound=" + (o.SoundEnabled ? "1" : "0"),
                    "audioReactive=" + (o.AudioReactiveEnabled ? "1" : "0"),
                    "rememberSnowmen=" + (o.RememberSnowmen ? "1" : "0"),
                    "countdown=" + (o.CountdownEnabled ? "1" : "0"),
                    "lanterns=" + (o.LanternsEnabled ? "1" : "0"),
                    "birds=" + (o.BirdsEnabled ? "1" : "0"),
                    "cursorTrail=" + (o.CursorTrailEnabled ? "1" : "0"),
                    "wallpaperTheme=" + o.WallpaperTheme.ToString(ci),
                    "rememberWallpaper=" + (o.RememberWallpaper ? "1" : "0"),
                    "customWallpaperB64=" + customWpB64,
                    "blizzard=" + (o.BlizzardMode ? "1" : "0"),
                    "multiMonitor=" + (o.MultiMonitorEnabled ? "1" : "0"),
                    "autoPause=" + (o.AutoPauseOnFullscreen ? "1" : "0"),
                    "perf=" + (o.PerformanceMode ? "1" : "0"),
                    "lang=" + (o.IsEnglish ? "1" : "0")
                };
                System.IO.File.WriteAllLines(path, lines);
            }
            catch { }
        }
    }

    internal static class SnowmenStore
    {
        private static string FilePath
        {
            get
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnowyWindows");
                return System.IO.Path.Combine(dir, "snowmen.ini");
            }
        }

        public static void Save(List<SnowmanEntity> snowmen, int screenW)
        {
            try
            {
                string path = FilePath;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.Globalization.CultureInfo ci = System.Globalization.CultureInfo.InvariantCulture;
                List<string> lines = new List<string>();
                int count = Math.Min(12, snowmen.Count);
                for (int i = 0; i < count; i++)
                {
                    SnowmanEntity sm = snowmen[i];
                    float xRatio = Math.Max(0.04f, Math.Min(0.96f, sm.X / Math.Max(1f, screenW)));
                    string nameB64 = string.IsNullOrEmpty(sm.NameTag)
                        ? ""
                        : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sm.NameTag));
                    string line = string.Join("|", new string[]
                    {
                        xRatio.ToString("0.####", ci),
                        sm.BaseR1.ToString("0.##", ci),
                        sm.BaseR2.ToString("0.##", ci),
                        sm.BaseR3.ToString("0.##", ci),
                        sm.Scale.ToString("0.###", ci),
                        sm.HasHat ? "1" : "0",
                        sm.HatStyle.ToString(ci),
                        sm.HatColor.ToArgb().ToString(ci),
                        sm.GlassesStyle.ToString(ci),
                        sm.FaceStyle.ToString(ci),
                        sm.ScarfStyle.ToString(ci),
                        sm.ScarfColor.ToArgb().ToString(ci),
                        sm.OutfitStyle.ToString(ci),
                        sm.OutfitColor.ToArgb().ToString(ci),
                        sm.ArmStyle.ToString(ci),
                        sm.NameTagColor.ToArgb().ToString(ci),
                        nameB64
                    });
                    lines.Add(line);
                }
                System.IO.File.WriteAllLines(path, lines.ToArray());
            }
            catch { }
        }

        public static List<SnowmanEntity> Load(int screenW, Random rng)
        {
            List<SnowmanEntity> result = new List<SnowmanEntity>();
            try
            {
                string path = FilePath;
                if (!System.IO.File.Exists(path)) return result;
                System.Globalization.CultureInfo ci = System.Globalization.CultureInfo.InvariantCulture;
                string[] lines = System.IO.File.ReadAllLines(path);
                for (int i = 0; i < lines.Length && result.Count < 12; i++)
                {
                    string[] p = lines[i].Split('|');
                    if (p.Length < 17) continue;
                    float xRatio = float.Parse(p[0], ci);
                    float r1 = float.Parse(p[1], ci);
                    float r2 = float.Parse(p[2], ci);
                    float r3 = float.Parse(p[3], ci);
                    float sc = float.Parse(p[4], ci);

                    SnowmanEntity sm = new SnowmanEntity(xRatio * screenW, 0f, r1, r2, r3, IntPtr.Zero, rng);
                    sm.ApplyScale(sc);
                    sm.HasHat = (p[5] == "1");
                    sm.HatStyle = Math.Max(0, Math.Min(5, int.Parse(p[6], ci)));
                    sm.HatColor = Color.FromArgb(int.Parse(p[7], ci));
                    sm.GlassesStyle = Math.Max(0, Math.Min(4, int.Parse(p[8], ci)));
                    sm.FaceStyle = Math.Max(0, Math.Min(3, int.Parse(p[9], ci)));
                    sm.ScarfStyle = Math.Max(0, Math.Min(4, int.Parse(p[10], ci)));
                    sm.ScarfColor = Color.FromArgb(int.Parse(p[11], ci));
                    sm.OutfitStyle = Math.Max(0, Math.Min(3, int.Parse(p[12], ci)));
                    sm.OutfitColor = Color.FromArgb(int.Parse(p[13], ci));
                    sm.ArmStyle = Math.Max(0, Math.Min(4, int.Parse(p[14], ci)));
                    sm.NameTagColor = Color.FromArgb(int.Parse(p[15], ci));
                    if (!string.IsNullOrEmpty(p[16]))
                        sm.NameTag = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(p[16]));
                    result.Add(sm);
                }
            }
            catch { }
            return result;
        }
    }

    internal static class TreesStore
    {
        private static string FilePath
        {
            get
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnowyWindows");
                return System.IO.Path.Combine(dir, "trees.ini");
            }
        }

        public static void Save(List<ChristmasTreeEntity> trees, int screenW)
        {
            try
            {
                string path = FilePath;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.Globalization.CultureInfo ci = System.Globalization.CultureInfo.InvariantCulture;
                List<string> lines = new List<string>();
                int count = Math.Min(6, trees.Count);
                for (int i = 0; i < count; i++)
                {
                    ChristmasTreeEntity tr = trees[i];
                    float xRatio = Math.Max(0.05f, Math.Min(0.95f, tr.X / Math.Max(1f, screenW)));
                    string line = string.Join("|", new string[]
                    {
                        xRatio.ToString("0.####", ci),
                        tr.Scale.ToString("0.###", ci),
                        tr.TreeStyle.ToString(ci),
                        tr.TopperStyle.ToString(ci),
                        tr.LightStyle.ToString(ci),
                        tr.LightAnim.ToString(ci),
                        tr.OrnamentStyle.ToString(ci),
                        tr.OrnamentColor.ToArgb().ToString(ci),
                        tr.GarlandStyle.ToString(ci),
                        tr.HasGifts ? "1" : "0"
                    });
                    lines.Add(line);
                }
                System.IO.File.WriteAllLines(path, lines.ToArray());
            }
            catch { }
        }

        public static List<ChristmasTreeEntity> Load(int screenW, Random rng)
        {
            List<ChristmasTreeEntity> result = new List<ChristmasTreeEntity>();
            try
            {
                string path = FilePath;
                if (!System.IO.File.Exists(path)) return result;
                System.Globalization.CultureInfo ci = System.Globalization.CultureInfo.InvariantCulture;
                string[] lines = System.IO.File.ReadAllLines(path);
                for (int i = 0; i < lines.Length && result.Count < 6; i++)
                {
                    string[] p = lines[i].Split('|');
                    if (p.Length < 10) continue;
                    float xRatio = float.Parse(p[0], ci);
                    float sc = float.Parse(p[1], ci);
                    ChristmasTreeEntity tr = new ChristmasTreeEntity(xRatio * screenW, 0f, IntPtr.Zero, rng);
                    tr.ApplyScale(sc);
                    tr.TreeStyle = Math.Max(0, Math.Min(3, int.Parse(p[2], ci)));
                    tr.TopperStyle = Math.Max(0, Math.Min(3, int.Parse(p[3], ci)));
                    tr.LightStyle = Math.Max(0, Math.Min(3, int.Parse(p[4], ci)));
                    tr.LightAnim = Math.Max(0, Math.Min(3, int.Parse(p[5], ci)));
                    tr.OrnamentStyle = Math.Max(0, Math.Min(3, int.Parse(p[6], ci)));
                    tr.OrnamentColor = Color.FromArgb(int.Parse(p[7], ci));
                    tr.GarlandStyle = Math.Max(0, Math.Min(3, int.Parse(p[8], ci)));
                    tr.HasGifts = (p[9] == "1");
                    result.Add(tr);
                }
            }
            catch { }
            return result;
        }
    }

    // ========================================================================
    // 10c. ÖZEL UYGULAMA İKONU ÜRETİCİSİ (KARDAN ADAM KAFASI & KAR TANESİ .ICO)
    // ========================================================================
    internal static class AppIconGenerator
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Bitmap RenderSnowmanIconBitmap(int size)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float s = size / 256f;

                // 1. Yuvarlatılmış gece mavisi kış rozeti arka planı
                RectangleF bgRect = new RectangleF(10f * s, 10f * s, 236f * s, 236f * s);
                using (LinearGradientBrush bgBrush = new LinearGradientBrush(
                    bgRect,
                    Color.FromArgb(255, 15, 23, 42),
                    Color.FromArgb(255, 30, 58, 138),
                    LinearGradientMode.Vertical))
                {
                    g.FillEllipse(bgBrush, bgRect);
                }
                using (Pen ringPen = new Pen(Color.FromArgb(220, 56, 189, 248), 6f * s))
                {
                    g.DrawEllipse(ringPen, 13f * s, 13f * s, 230f * s, 230f * s);
                }

                // 2. Arka planda parlayan küçük kar yıldızları
                using (SolidBrush starBrush = new SolidBrush(Color.FromArgb(190, 186, 230, 253)))
                {
                    g.FillEllipse(starBrush, 42f * s, 52f * s, 10f * s, 10f * s);
                    g.FillEllipse(starBrush, 198f * s, 58f * s, 12f * s, 12f * s);
                    g.FillEllipse(starBrush, 36f * s, 138f * s, 8f * s, 8f * s);
                    g.FillEllipse(starBrush, 208f * s, 142f * s, 9f * s, 9f * s);
                }

                // 3. Alt kırmızı atkı kıvrımı
                using (Pen scarfPen = new Pen(Color.FromArgb(255, 239, 68, 68), 22f * s)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                })
                {
                    g.DrawArc(scarfPen, 62f * s, 168f * s, 132f * s, 52f * s, 10f, 160f);
                }

                // 4. Kardan Adam Kafası (3B gölgeli beyaz küre)
                RectangleF headRect = new RectangleF(54f * s, 74f * s, 148f * s, 142f * s);
                using (LinearGradientBrush headBrush = new LinearGradientBrush(
                    headRect,
                    Color.FromArgb(255, 255, 255, 255),
                    Color.FromArgb(255, 191, 219, 254),
                    45f))
                {
                    g.FillEllipse(headBrush, headRect);
                }
                using (Pen headOutline = new Pen(Color.FromArgb(200, 148, 163, 184), 3.5f * s))
                {
                    g.DrawEllipse(headOutline, headRect);
                }

                // 5. Pembe yanaklar
                using (SolidBrush cheekBrush = new SolidBrush(Color.FromArgb(95, 248, 113, 113)))
                {
                    g.FillEllipse(cheekBrush, 68f * s, 146f * s, 26f * s, 16f * s);
                    g.FillEllipse(cheekBrush, 162f * s, 146f * s, 26f * s, 16f * s);
                }

                // 6. Parlak kömür gözler ve beyaz ışık yansıması
                using (SolidBrush coalBrush = new SolidBrush(Color.FromArgb(255, 15, 23, 42)))
                using (SolidBrush catchlight = new SolidBrush(Color.White))
                {
                    g.FillEllipse(coalBrush, 88f * s, 118f * s, 22f * s, 24f * s);
                    g.FillEllipse(coalBrush, 146f * s, 118f * s, 22f * s, 24f * s);

                    g.FillEllipse(catchlight, 93f * s, 122f * s, 7f * s, 7f * s);
                    g.FillEllipse(catchlight, 151f * s, 122f * s, 7f * s, 7f * s);

                    // Tebessüm noktaları
                    g.FillEllipse(coalBrush, 96f * s, 174f * s, 8f * s, 8f * s);
                    g.FillEllipse(coalBrush, 112f * s, 181f * s, 9f * s, 9f * s);
                    g.FillEllipse(coalBrush, 131f * s, 181f * s, 9f * s, 9f * s);
                    g.FillEllipse(coalBrush, 148f * s, 174f * s, 8f * s, 8f * s);
                }

                // 7. Turuncu Havuç Burun
                PointF[] nosePts = {
                    new PointF(122f * s, 138f * s),
                    new PointF(178f * s, 150f * s),
                    new PointF(122f * s, 160f * s)
                };
                using (SolidBrush carrotBrush = new SolidBrush(Color.FromArgb(255, 249, 115, 22)))
                using (Pen carrotPen = new Pen(Color.FromArgb(230, 194, 65, 12), 2.5f * s))
                {
                    g.FillPolygon(carrotBrush, nosePts);
                    g.DrawPolygon(carrotPen, nosePts);
                }

                // 8. Kırmızı Ponponlu Kış / Noel Beresi
                using (SolidBrush beanieBrush = new SolidBrush(Color.FromArgb(255, 220, 38, 38)))
                {
                    g.FillPie(beanieBrush, 56f * s, 32f * s, 144f * s, 112f * s, 180f, 180f);
                }
                // Beyaz kürk şerit
                RectangleF furRect = new RectangleF(48f * s, 72f * s, 160f * s, 26f * s);
                using (SolidBrush furBrush = new SolidBrush(Color.FromArgb(255, 248, 250, 252)))
                using (Pen furPen = new Pen(Color.FromArgb(210, 203, 213, 225), 3f * s))
                {
                    g.FillEllipse(furBrush, furRect);
                    g.DrawEllipse(furPen, furRect);
                }
                // Beyaz Tepe Ponponu
                RectangleF pomRect = new RectangleF(106f * s, 14f * s, 44f * s, 42f * s);
                using (SolidBrush pomBrush = new SolidBrush(Color.White))
                using (Pen pomPen = new Pen(Color.FromArgb(210, 203, 213, 225), 3f * s))
                {
                    g.FillEllipse(pomBrush, pomRect);
                    g.DrawEllipse(pomPen, pomRect);
                }
            }
            return bmp;
        }

        public static Icon CreateRuntimeIcon()
        {
            using (Bitmap bmp = RenderSnowmanIconBitmap(64))
            {
                IntPtr hIcon = bmp.GetHicon();
                Icon clone = (Icon)Icon.FromHandle(hIcon).Clone();
                DestroyIcon(hIcon);
                return clone;
            }
        }

        public static void SaveIcoFile(string filePath)
        {
            using (Bitmap bmp256 = RenderSnowmanIconBitmap(256))
            using (System.IO.MemoryStream pngStream = new System.IO.MemoryStream())
            {
                bmp256.Save(pngStream, ImageFormat.Png);
                byte[] pngBytes = pngStream.ToArray();

                using (System.IO.FileStream fs = new System.IO.FileStream(filePath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                using (System.IO.BinaryWriter bw = new System.IO.BinaryWriter(fs))
                {
                    // Standart Windows ICO Başlığı (256x256 PNG sıkıştırmalı)
                    bw.Write((ushort)0); // Reserved
                    bw.Write((ushort)1); // Type: 1 = ICO
                    bw.Write((ushort)1); // Image Count: 1
                    bw.Write((byte)0);   // Width: 0 = 256px
                    bw.Write((byte)0);   // Height: 0 = 256px
                    bw.Write((byte)0);   // Colors
                    bw.Write((byte)0);   // Reserved
                    bw.Write((ushort)1); // Planes
                    bw.Write((ushort)32);// BPP
                    bw.Write((uint)pngBytes.Length);
                    bw.Write((uint)22);  // Offset (6 + 16 = 22)
                    bw.Write(pngBytes);
                }
            }
        }
    }

    // ========================================================================
    // 11. PROGRAM GİRİŞ NOKTASI
    // ========================================================================
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && args[0] == "--make-icon")
            {
                string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SnowyIcon.ico");
                AppIconGenerator.SaveIcoFile(icoPath);
                return;
            }
            try
            {
                NativeMethods.SetProcessDpiAwareness(2);
            }
            catch
            {
                try { NativeMethods.SetProcessDPIAware(); } catch { }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ApplicationExit += (s, e) => WallpaperManager.RestoreOriginal();
            AppDomain.CurrentDomain.ProcessExit += (s, e) => WallpaperManager.RestoreOriginal();
            try
            {
                Microsoft.Win32.SystemEvents.SessionEnding += (s, e) => WallpaperManager.RestoreOriginal();
            }
            catch { }

            SnowOverlayForm overlay = new SnowOverlayForm();
            SettingsStore.Load(overlay);
            ControlPanelForm panel = new ControlPanelForm(overlay);

            overlay.Show();
            panel.Show();
            overlay.ControlPanelHwnd = panel.Handle;
            overlay.StartSimulation();

            try
            {
                Application.Run(panel);
            }
            finally
            {
                WallpaperManager.RestoreOriginal();
            }
        }
    }
}