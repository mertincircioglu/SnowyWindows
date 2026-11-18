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

        public const int VK_F7 = 0x76;
        public const int VK_F8 = 0x77;
        public const int VK_F9 = 0x78;

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
            Radius = Math.Max(7f, Math.Min(26f, radius));
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
        public float R1; // Alt
        public float R2; // Orta
        public float R3; // Kafa
        public IntPtr SurfaceHwnd;
        public bool Dragging;
        public float TiltAngle;
        public float TiltVel;
        public int HatStyle; // 0: Kırmızı Ponponlu Kış Beresi, 1: Klasik Silindir Şapka, 2: Renkli Kış Beresi
        public bool HasHat = true;
        public Color HatColor;
        public Color ScarfColor;
        public int SurpriseTimer; // Vurulunca şaşkın yüz ifadesi süresi
        public int BlinkTimer;
        public int HitCooldown;

        public SnowmanEntity(float x, float y, float r1, float r2, float r3, IntPtr surfaceHwnd, Random rng)
        {
            X = x;
            Y = y;
            R1 = Math.Max(10f, Math.Min(26f, r1));
            R2 = Math.Max(8f, Math.Min(20f, r2));
            R3 = Math.Max(6.5f, Math.Min(16f, r3));
            SurfaceHwnd = surfaceHwnd;

            // Şapka stili dağılımı: Kırmızı ponponlu bere (%55), silindir şapka (%25), renkli bere (%20)
            double roll = rng.NextDouble();
            if (roll < 0.55)
            {
                HatStyle = 0;
                HatColor = Color.FromArgb(250, 220, 38, 38); // Parlak Noel beresi kırmızısı
            }
            else if (roll < 0.80)
            {
                HatStyle = 1;
                Color[] topHats = {
                    Color.FromArgb(248, 30, 41, 59),
                    Color.FromArgb(248, 127, 29, 29),
                    Color.FromArgb(248, 30, 58, 138)
                };
                HatColor = topHats[rng.Next(topHats.Length)];
            }
            else
            {
                HatStyle = 2;
                Color[] beanies = {
                    Color.FromArgb(248, 37, 99, 235),
                    Color.FromArgb(248, 5, 150, 105),
                    Color.FromArgb(248, 147, 51, 234)
                };
                HatColor = beanies[rng.Next(beanies.Length)];
            }

            Color[] scarves = {
                Color.FromArgb(248, 239, 68, 68),
                Color.FromArgb(248, 245, 158, 11),
                Color.FromArgb(248, 59, 130, 246),
                Color.FromArgb(248, 16, 185, 129),
                Color.FromArgb(248, 236, 72, 153)
            };
            ScarfColor = scarves[rng.Next(scarves.Length)];
            BlinkTimer = 60 + rng.Next(140);
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
        private readonly List<SnowPlowEntity> _plows = new List<SnowPlowEntity>();
        private readonly List<CampfireEntity> _campfires = new List<CampfireEntity>();
        private readonly FloatingPopup[] _popups = new FloatingPopup[24];
        private CampfireEntity _draggedCampfire;
        private Point _lastCursorPos;

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
            BlizzardMode = false;
            ActiveSnowCount = 420;
            MaxPileHeight = 34f;
            WindNx = -0.22f;
            WindNy = 0.08f;
            WindowInteriorSnowOpacity = 0.30f; // Varsayılan: pencerelerin içinde %30 hafif şeffaf
            GustAmount = 0.25f;
            MeltRate = 0f;
            AutoPauseOnFullscreen = true;

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
            if (hwnd == Handle || hwnd == ControlPanelHwnd)
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
            _surfaceGraphics.CompositingQuality = CompositingQuality.HighQuality;
            _surfaceGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
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
            _campfires.Clear();
            for (int i = 0; i < _plows.Count; i++)
                ShedPlowMound(_plows[i], 1f);
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
            int zCounter = 0;

            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (hWnd == myHwnd || hWnd == panelHwnd) return true;
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

            // Sağ tık: Tıklanan kardan adamı veya kartopunu kar parçacıklarına patlatarak sil
            if (e.Button == MouseButtons.Right)
            {
                for (int i = _snowmen.Count - 1; i >= 0; i--)
                {
                    SnowmanEntity sm = _snowmen[i];
                    if (Math.Abs(e.X - sm.X) <= sm.R1 * 1.5f && e.Y <= sm.Y + 8f && e.Y >= sm.Y - (sm.R1 + sm.R2 + sm.R3) * 2f)
                    {
                        BurstSnowSparkles(sm.X, sm.Y - 20f, 30);
                        _snowmen.RemoveAt(i);
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
                return;
            }

            if (e.Button != MouseButtons.Left) return;

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
                if (sm.Dragging || sm.HitCooldown > 0) continue;

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
        }

        private void UpdatePhysics()
        {
            _frameCount++;
            _time += (float)PhysicsStep;

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

            // 3. 3 Kartopuyla Oluşturulmuş Kardan Adamlar (⛄)
            Point rawCur = Cursor.Position;
            _lastCursorPos = new Point(rawCur.X - _originX, rawCur.Y - _originY);
            for (int i = 0; i < _snowmen.Count; i++)
                DrawSnowman(g, _snowmen[i]);

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

            // 6b. Vuruş Efekt Yazıları (🎯 HEADSHOT!, 💥 BOOM!)
            for (int i = 0; i < _popups.Length; i++)
            {
                if (_popups[i].Active)
                    DrawFloatingPopup(g, _popups[i]);
            }

            // 6c. Tipi (Blizzard) Modunda Atmosferik Hızlı Rüzgar Çizgileri
            if (!PerformanceMode && (BlizzardMode || Math.Abs(WindNx) > 0.68f))
                DrawBlizzardWindStreaks(g);

            // 7. UpdateLayeredWindow ile ekrana bas
            PresentFrame();
        }

        private void DrawShadedSnowSphere(Graphics g, float cx, float cy, float r)
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
            g.DrawEllipse(_snowOutlinePen, rect);
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

        private void DrawSnowman(Graphics g, SnowmanEntity sm)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(sm.X, sm.Y);
            g.RotateTransform(sm.TiltAngle);

            float r1 = sm.R1;
            float y1 = -r1 + 2f;
            float r2 = sm.R2;
            float y2 = y1 - r1 - r2 * 0.64f;
            float r3 = sm.R3;
            float y3 = y2 - r2 - r3 * 0.64f;

            DrawShadedSnowSphere(g, 0f, y1, r1);
            DrawShadedSnowSphere(g, 0f, y2, r2);
            DrawShadedSnowSphere(g, 0f, y3, r3);

            float s = r2 / 8.5f;

            // Kömür düğmeler
            g.FillEllipse(_coalBrush, -1.8f * s, y2 - 2.8f * s, 3.6f * s, 3.6f * s);
            g.FillEllipse(_coalBrush, -1.8f * s, y2 + 2.8f * s, 3.6f * s, 3.6f * s);

            // Gözler (Fare imlecine doğru hafifçe bakar ve göz kırpar)
            float lookX = Math.Max(-1.0f, Math.Min(1.0f, (_lastCursorPos.X - sm.X) / 220f)) * s;
            float lookY = Math.Max(-0.8f, Math.Min(0.8f, (_lastCursorPos.Y - (sm.Y + y3)) / 220f)) * s;

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
                g.FillEllipse(_coalBrush, -3.4f * s + lookX, y3 - 2.6f * s + lookY, eyeR, eyeR);
                g.FillEllipse(_coalBrush, 1.2f * s + lookX, y3 - 2.6f * s + lookY, eyeR, eyeR);
            }

            // Vurulunca şaşkın ağız ('O')
            if (sm.SurpriseTimer > 0)
            {
                g.FillEllipse(_coalBrush, -1.6f * s, y3 + 2.8f * s, 3.2f * s, 3.6f * s);
            }

            // Havuç burun
            PointF[] nose = {
                new PointF(0f, y3 - 0.4f * s),
                new PointF(8.5f * s, y3 + 1.5f * s),
                new PointF(0f, y3 + 3.0f * s)
            };
            g.FillPolygon(_carrotBrush, nose);

            // Atkı
            float sy = y2 - r2 * 0.72f;
            using (Pen scarfPen = new Pen(sm.ScarfColor, Math.Max(2.5f, 3.8f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(scarfPen, -r2 * 0.82f, sy, r2 * 0.82f, sy);
                g.DrawLine(scarfPen, r2 * 0.35f, sy, r2 * 0.58f, sy + 8.5f * s);
            }

            // Şapka (Kafadan vurulmadıysa)
            if (sm.HasHat)
            {
                float hy = y3 - r3 * 0.78f;
                DrawHatGeometry(g, 0f, hy, r3, s, sm.HatStyle, sm.HatColor);
            }

            g.Restore(state);
        }

        private void DrawHatGeometry(Graphics g, float cx, float hy, float r3, float s, int style, Color color)
        {
            if (style == 0 || style == 2)
            {
                // Ponponlu Kış Beresi (Beyaz kürklü alt şerit + kubbeli bere + tepe ponponu)
                using (SolidBrush hatBrush = new SolidBrush(color))
                {
                    float domeW = r3 * 1.95f;
                    float domeH = r3 * 1.55f;
                    g.FillPie(hatBrush, cx - domeW * 0.5f, hy - domeH * 0.75f, domeW, domeH * 1.4f, 180, 180);
                }
                // Beyaz yumuşak kürk şeridi
                float brimW = r3 * 2.15f;
                float brimH = Math.Max(3.5f, 4.2f * s);
                g.FillEllipse(_alphaWhiteBrushes[252], cx - brimW * 0.5f, hy - brimH * 0.5f, brimW, brimH);
                g.DrawEllipse(_snowOutlinePen, cx - brimW * 0.5f, hy - brimH * 0.5f, brimW, brimH);

                // Tepe beyaz ponponu
                float pomR = Math.Max(3.2f, 3.8f * s);
                float pomY = hy - r3 * 1.22f;
                g.FillEllipse(_alphaWhiteBrushes[255], cx - pomR, pomY - pomR, pomR * 2f, pomR * 2f);
                g.DrawEllipse(_snowOutlinePen, cx - pomR, pomY - pomR, pomR * 2f, pomR * 2f);
            }
            else
            {
                // Klasik Silindir Şapka
                using (SolidBrush hatBrush = new SolidBrush(color))
                using (Pen brimPen = new Pen(color, Math.Max(2.2f, 2.8f * s)))
                {
                    g.DrawLine(brimPen, cx - r3 * 1.18f, hy, cx + r3 * 1.18f, hy);
                    g.FillRectangle(hatBrush, cx - r3 * 0.74f, hy - 9.0f * s, r3 * 1.48f, 9.0f * s);
                }
            }
        }

        private void DrawFlyingHat(Graphics g, FlyingHat fh)
        {
            GraphicsState st = g.Save();
            g.TranslateTransform(fh.X, fh.Y);
            g.RotateTransform(fh.Angle);
            DrawHatGeometry(g, 0f, 0f, 7.5f * fh.Scale, fh.Scale, fh.HatStyle, fh.HatColor);
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
                    g.FillEllipse(_lightGlowBrushes[ci], bx - 9.5f, by - 7.5f, 19f, 19f);
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
                if (_surfaceGraphics != null) _surfaceGraphics.Dispose();
                if (_surfaceBitmap != null) _surfaceBitmap.Dispose();
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

    internal sealed class ControlPanelForm : Form
    {
        private const int L = 18;
        private const int W = 280;

        private readonly SnowOverlayForm _overlay;
        private readonly WindRadarControl _windRadar;
        private readonly Label _titleLbl;
        private readonly Label _windHdr;
        private readonly Label _windHint;
        private readonly Label _windValueLabel;
        private readonly Label _modeBadge;
        private readonly Label _footerHint;

        private readonly ModernButton _langBtn;
        private readonly ModernButton _resetBtn;
        private readonly ModernButton _lightBtn;
        private readonly ModernButton _normalBtn;
        private readonly ModernButton _blizzardBtn;
        private readonly ModernButton _interactBtn;
        private readonly ModernButton _ballBtn;
        private readonly ModernButton _plowBtn;
        private readonly ModernButton _removePlowBtn;
        private readonly ModernButton _lightsBtn;
        private readonly ModernButton _clearBtn;
        private readonly ModernButton _pauseBtn;
        private readonly ModernButton _perfBtn;
        private readonly ModernButton _iciclesBtn;
        private readonly ModernButton _fireBtn;
        private readonly ModernButton _removeFireBtn;
        private readonly ModernButton _cableCarBtn;
        private readonly ModernButton _multiMonBtn;
        private readonly ModernToggle _autoPauseToggle;

        private readonly NotifyIcon _trayIcon;
        private readonly MenuItem _trayShowItem;
        private readonly MenuItem _trayInteractItem;
        private readonly MenuItem _trayPauseItem;
        private readonly MenuItem _trayClearItem;
        private readonly MenuItem _trayExitItem;

        private ModernSlider _intensitySlider;
        private ModernSlider _opacitySlider;
        private ModernSlider _pileSlider;
        private ModernSlider _meltSlider;
        private Label _densityCapLabel;
        private Label _opacityCapLabel;
        private Label _pileCapLabel;
        private Label _meltCapLabel;
        private Label _densityValLabel;
        private Label _opacityValLabel;
        private Label _pileValLabel;
        private Label _meltValLabel;

        private const float MeltScale = 0.000008f;

        public ControlPanelForm(SnowOverlayForm overlay)
        {
            _overlay = overlay;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "Snowy for Windows";
            ClientSize = new Size(316, 774);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(36, 48);
            BackColor = Color.FromArgb(11, 17, 30);
            ForeColor = Color.FromArgb(248, 250, 252);
            TopMost = true;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            AutoScroll = true;

            // ---- Özel Sürüklenebilir Modern Başlık Çubuğu ----
            Panel headerBar = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(316, 42),
                BackColor = Color.FromArgb(15, 23, 42)
            };
            headerBar.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    NativeMethods.ReleaseCapture();
                    NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HT_CAPTION, 0);
                }
            };
            Controls.Add(headerBar);

            _titleLbl = new Label
            {
                Text = "❄️ Snowy",
                Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(248, 250, 252),
                Location = new Point(14, 10),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            _titleLbl.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    NativeMethods.ReleaseCapture();
                    NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HT_CAPTION, 0);
                }
            };
            headerBar.Controls.Add(_titleLbl);

            _modeBadge = new Label
            {
                Font = new Font("Segoe UI", 7.6f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                BackColor = Color.FromArgb(24, 38, 60),
                Location = new Point(108, 11),
                Size = new Size(88, 20),
                TextAlign = ContentAlignment.MiddleCenter
            };
            headerBar.Controls.Add(_modeBadge);

            // Dil Değiştirme Tuşu (🌐 TR / EN)
            _langBtn = new ModernButton
            {
                Location = new Point(202, 9),
                Size = new Size(52, 24),
                CornerRadius = 7,
                FillColor = Color.FromArgb(26, 40, 62),
                BorderColor = Color.FromArgb(56, 189, 248),
                ForeColor = Color.FromArgb(186, 230, 253),
                Font = new Font("Segoe UI Semibold", 7.6f, FontStyle.Bold)
            };
            _langBtn.Click += (s, e) =>
            {
                _overlay.IsEnglish = !_overlay.IsEnglish;
                ApplyLanguage();
            };
            headerBar.Controls.Add(_langBtn);

            ModernButton minBtn = new ModernButton
            {
                Text = "—",
                Location = new Point(259, 9),
                Size = new Size(24, 24),
                CornerRadius = 6,
                FillColor = Color.FromArgb(24, 35, 54),
                BorderColor = Color.FromArgb(40, 54, 78),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold)
            };
            minBtn.Click += (s, e) => { WindowState = FormWindowState.Minimized; };
            headerBar.Controls.Add(minBtn);

            ModernButton closeBtn = new ModernButton
            {
                Text = "✕",
                Location = new Point(286, 9),
                Size = new Size(24, 24),
                CornerRadius = 6,
                FillColor = Color.FromArgb(55, 24, 32),
                BorderColor = Color.FromArgb(95, 40, 50),
                ForeColor = Color.FromArgb(252, 165, 165),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold)
            };
            closeBtn.Click += (s, e) => { Close(); };
            headerBar.Controls.Add(closeBtn);

            // ---- Rüzgar Kartı ----
            ModernCard windCard = new ModernCard
            {
                Location = new Point(L, 50),
                Size = new Size(W, 174)
            };
            Controls.Add(windCard);

            _windHdr = new Label
            {
                Font = new Font("Segoe UI Semibold", 9.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(226, 232, 240),
                BackColor = Color.FromArgb(20, 30, 48),
                Location = new Point(12, 9),
                AutoSize = true
            };
            windCard.Controls.Add(_windHdr);

            _resetBtn = new ModernButton
            {
                Location = new Point(W - 64, 7),
                Size = new Size(52, 22),
                CornerRadius = 6,
                FillColor = Color.FromArgb(32, 46, 70),
                Font = new Font("Segoe UI", 7.8f, FontStyle.Bold)
            };
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
                Size = new Size(112, 112),
                Location = new Point((W - 112) / 2, 26),
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
                Font = new Font("Consolas", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(147, 197, 253),
                BackColor = Color.FromArgb(20, 30, 48),
                Location = new Point(12, 139),
                Size = new Size(W - 24, 16),
                TextAlign = ContentAlignment.MiddleCenter
            };
            windCard.Controls.Add(_windValueLabel);

            _windHint = new Label
            {
                Font = new Font("Segoe UI", 7.1f),
                ForeColor = Color.FromArgb(148, 163, 184),
                BackColor = Color.FromArgb(20, 30, 48),
                Location = new Point(12, 155),
                Size = new Size(W - 24, 14),
                TextAlign = ContentAlignment.MiddleCenter
            };
            windCard.Controls.Add(_windHint);

            // ---- Modern Kaydırıcılar ----
            _intensitySlider = AddSliderRow(232, 80, 1200, _overlay.ActiveSnowCount, out _densityCapLabel, out _densityValLabel);
            _densityValLabel.Text = _intensitySlider.Value.ToString();
            _intensitySlider.Scroll += (s, e) =>
            {
                _overlay.ActiveSnowCount = _intensitySlider.Value;
                _densityValLabel.Text = _intensitySlider.Value.ToString();
            };

            _opacitySlider = AddSliderRow(272, 0, 100, (int)Math.Round(_overlay.WindowInteriorSnowOpacity * 100f), out _opacityCapLabel, out _opacityValLabel);
            _opacityValLabel.Text = "%" + _opacitySlider.Value;
            _opacitySlider.Scroll += (s, e) =>
            {
                _overlay.WindowInteriorSnowOpacity = _opacitySlider.Value / 100f;
                _opacityValLabel.Text = "%" + _opacitySlider.Value;
            };

            _pileSlider = AddSliderRow(312, 10, 80, (int)Math.Round(_overlay.MaxPileHeight), out _pileCapLabel, out _pileValLabel);
            _pileValLabel.Text = _pileSlider.Value + " px";
            _pileSlider.Scroll += (s, e) =>
            {
                _overlay.MaxPileHeight = _pileSlider.Value;
                _pileValLabel.Text = _pileSlider.Value + " px";
            };

            _meltSlider = AddSliderRow(352, 0, 100, (int)Math.Round(_overlay.MeltRate / MeltScale), out _meltCapLabel, out _meltValLabel);
            _meltSlider.Scroll += (s, e) =>
            {
                _overlay.MeltRate = _meltSlider.Value * MeltScale;
                SyncMeltLabel();
            };

            // ---- Hava Durumu Hazır Ayarları ----
            _lightBtn = CreateModernButton(new Point(L, 396), new Size(88, 28), Color.FromArgb(24, 35, 54));
            _lightBtn.Click += (s, e) => ApplyPreset(220, -0.12f, 0.05f, 0.12f, false);
            Controls.Add(_lightBtn);

            _normalBtn = CreateModernButton(new Point(L + 96, 396), new Size(88, 28), Color.FromArgb(24, 35, 54));
            _normalBtn.Click += (s, e) => ApplyPreset(420, -0.22f, 0.08f, 0.22f, false);
            Controls.Add(_normalBtn);

            _blizzardBtn = CreateModernButton(new Point(L + 192, 396), new Size(88, 28), Color.FromArgb(24, 35, 54));
            _blizzardBtn.Click += (s, e) => ApplyPreset(1150, -0.82f, 0.52f, 0.65f, true);
            Controls.Add(_blizzardBtn);

            // ---- Etkileşim Modu ----
            _interactBtn = CreateModernButton(new Point(L, 432), new Size(W, 34), Color.FromArgb(2, 132, 199));
            _interactBtn.BorderColor = Color.FromArgb(56, 189, 248);
            _interactBtn.Click += (s, e) => _overlay.ToggleInteractiveMode();
            Controls.Add(_interactBtn);

            // ---- Araç & Efekt Butonları ----
            _ballBtn = CreateModernButton(new Point(L, 472), new Size(136, 30), Color.FromArgb(24, 35, 54));
            _ballBtn.Click += (s, e) => _overlay.SpawnSingleSnowballOnActiveWindow();
            Controls.Add(_ballBtn);

            _clearBtn = CreateModernButton(new Point(L + 144, 472), new Size(136, 30), Color.FromArgb(24, 35, 54));
            _clearBtn.ForeColor = Color.FromArgb(252, 165, 165);
            _clearBtn.Click += (s, e) => _overlay.ClearAllSnow();
            Controls.Add(_clearBtn);

            _plowBtn = CreateModernButton(new Point(L, 508), new Size(136, 30), Color.FromArgb(161, 98, 7));
            _plowBtn.BorderColor = Color.FromArgb(234, 179, 8);
            _plowBtn.Click += (s, e) => _overlay.SpawnSnowPlow();
            Controls.Add(_plowBtn);

            _removePlowBtn = CreateModernButton(new Point(L + 144, 508), new Size(136, 30), Color.FromArgb(52, 34, 24));
            _removePlowBtn.BorderColor = Color.FromArgb(161, 98, 7);
            _removePlowBtn.ForeColor = Color.FromArgb(254, 240, 138);
            _removePlowBtn.Click += (s, e) => _overlay.RemoveLastSnowPlow();
            Controls.Add(_removePlowBtn);

            _fireBtn = CreateModernButton(new Point(L, 544), new Size(136, 30), Color.FromArgb(124, 45, 18));
            _fireBtn.BorderColor = Color.FromArgb(234, 88, 12);
            _fireBtn.ForeColor = Color.FromArgb(254, 215, 170);
            _fireBtn.Click += (s, e) => _overlay.SpawnCampfire();
            Controls.Add(_fireBtn);

            _removeFireBtn = CreateModernButton(new Point(L + 144, 544), new Size(136, 30), Color.FromArgb(55, 24, 30));
            _removeFireBtn.BorderColor = Color.FromArgb(154, 52, 18);
            _removeFireBtn.ForeColor = Color.FromArgb(253, 186, 116);
            _removeFireBtn.Click += (s, e) => _overlay.RemoveLastCampfire();
            Controls.Add(_removeFireBtn);

            _lightsBtn = CreateModernButton(new Point(L, 580), new Size(136, 30), Color.FromArgb(24, 35, 54));
            _lightsBtn.ForeColor = Color.FromArgb(253, 224, 71);
            _lightsBtn.Click += (s, e) =>
            {
                _overlay.FairyLightsEnabled = !_overlay.FairyLightsEnabled;
                SyncLightsButton();
            };
            Controls.Add(_lightsBtn);

            _iciclesBtn = CreateModernButton(new Point(L + 144, 580), new Size(136, 30), Color.FromArgb(24, 35, 54));
            _iciclesBtn.ForeColor = Color.FromArgb(186, 230, 253);
            _iciclesBtn.Click += (s, e) =>
            {
                _overlay.IciclesEnabled = !_overlay.IciclesEnabled;
                SyncIciclesButton();
            };
            Controls.Add(_iciclesBtn);

            _pauseBtn = CreateModernButton(new Point(L, 616), new Size(136, 30), Color.FromArgb(24, 35, 54));
            _pauseBtn.Click += (s, e) => _overlay.TogglePause();
            Controls.Add(_pauseBtn);

            _perfBtn = CreateModernButton(new Point(L + 144, 616), new Size(136, 30), Color.FromArgb(24, 35, 54));
            _perfBtn.Click += (s, e) =>
            {
                _overlay.PerformanceMode = !_overlay.PerformanceMode;
                SyncPerfButton();
            };
            Controls.Add(_perfBtn);

            // İki Uygulama Arası Teleferik Aç/Kapat Butonu
            _cableCarBtn = CreateModernButton(new Point(L, 652), new Size(W, 30), Color.FromArgb(24, 35, 54));
            _cableCarBtn.Click += (s, e) =>
            {
                _overlay.CableCarEnabled = !_overlay.CableCarEnabled;
                SyncCableCarButton();
            };
            Controls.Add(_cableCarBtn);

            _multiMonBtn = CreateModernButton(new Point(L, 688), new Size(W, 30), Color.FromArgb(24, 35, 54));
            _multiMonBtn.Click += (s, e) =>
            {
                _overlay.SetMultiMonitorMode(!_overlay.MultiMonitorEnabled);
                SyncMultiMonitorButton();
            };
            Controls.Add(_multiMonBtn);

            _autoPauseToggle = new ModernToggle
            {
                Location = new Point(L, 724),
                Size = new Size(W, 22),
                Checked = _overlay.AutoPauseOnFullscreen
            };
            _autoPauseToggle.CheckedChanged += (s, e) => { _overlay.AutoPauseOnFullscreen = _autoPauseToggle.Checked; };
            Controls.Add(_autoPauseToggle);

            _footerHint = new Label
            {
                Font = new Font("Segoe UI", 7.2f),
                ForeColor = Color.FromArgb(100, 116, 139),
                Location = new Point(L, 748),
                Size = new Size(W, 24),
                TextAlign = ContentAlignment.TopCenter
            };
            Controls.Add(_footerHint);

            _overlay.OnInteractiveModeChanged += SyncInteractiveBadge;
            _overlay.OnPauseChanged += SyncPauseButton;

            // Uygulama Pencere ve Sistem Tepsisi (System Tray) İkonu
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
            _trayPauseItem = new MenuItem("", (s, e) => _overlay.TogglePause());
            _trayClearItem = new MenuItem("", (s, e) => _overlay.ClearAllSnow());
            _trayExitItem = new MenuItem("", (s, e) => Application.Exit());
            trayMenu.MenuItems.Add(_trayShowItem);
            trayMenu.MenuItems.Add(_trayInteractItem);
            trayMenu.MenuItems.Add(_trayPauseItem);
            trayMenu.MenuItems.Add(_trayClearItem);
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add(_trayExitItem);
            _trayIcon.ContextMenu = trayMenu;
            _trayIcon.DoubleClick += (s, e) => { Show(); WindowState = FormWindowState.Normal; Activate(); };

            ApplyLanguage();
            UpdateWindStatus(_overlay.WindNx, _overlay.WindNy);

            FormClosing += (s, e) =>
            {
                SettingsStore.Save(_overlay);
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                Application.Exit();
            };

            ResumeLayout(true);
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
            using (Pen borderPen = new Pen(Color.FromArgb(38, 52, 74), 1f))
                e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int maxH = wa.Height - 20;
            if (Height > maxH) Height = maxH;
            Left = wa.Left + 24;
            Top = wa.Top + 10;
        }

        private string Tr(string tr, string en)
        {
            return _overlay.IsEnglish ? en : tr;
        }

        private void ApplyLanguage()
        {
            _langBtn.Text = _overlay.IsEnglish ? "🌐 EN" : "🌐 TR";
            _windHdr.Text = Tr("💨 Rüzgar", "💨 Wind");
            _resetBtn.Text = Tr("Sıfırla", "Reset");
            _windHint.Text = Tr("Merkezden sürükleyerek yönü ayarlayın", "Drag from center to set wind vector");

            _densityCapLabel.Text = Tr("Kar Yoğunluğu", "Snow Density");
            _opacityCapLabel.Text = Tr("Uygulama İçi Kar", "Window Snow Opacity");
            _pileCapLabel.Text = Tr("Maks. Kar Kalınlığı", "Max Pile Thickness");
            _meltCapLabel.Text = Tr("Erime Hızı", "Melt Speed");

            _lightBtn.Text = Tr("🌤 Hafif", "🌤 Light");
            _normalBtn.Text = Tr("❄ Normal", "❄ Normal");
            _blizzardBtn.Text = Tr("🌪 Tipi", "🌪 Blizzard");

            _ballBtn.Text = Tr("⚪ Kartopu Ekle", "⚪ Add Snowball");
            _clearBtn.Text = Tr("🧹 Temizle (F9)", "🧹 Clear (F9)");
            _plowBtn.Text = Tr("🚜 Kürek Ekle", "🚜 Add Plow");
            _removePlowBtn.Text = Tr("🗑 Kürek Sil", "🗑 Remove Plow");
            _fireBtn.Text = Tr("🔥 Kamp Ateşi", "🔥 Add Campfire");
            _removeFireBtn.Text = Tr("🗑 Ateş Sil", "🗑 Remove Fire");
            _autoPauseToggle.Text = Tr("Tam ekranda otomatik duraklat", "Auto-pause in fullscreen");
            _footerHint.Text = Tr("F8: Kartopu at (⛄ vur) / 3 top = ⛄ / 🚜 & 🔥 fırlat", "F8: Throw snowballs (hit ⛄) / 3 balls = ⛄ / fling 🚜 & 🔥");

            _trayShowItem.Text = Tr("Kontrol Panelini Göster", "Show Control Panel");
            _trayInteractItem.Text = Tr("Etkileşim Modu (F8)", "Interactive Mode (F8)");
            _trayPauseItem.Text = Tr("Duraklat / Devam (F7)", "Pause / Resume (F7)");
            _trayClearItem.Text = Tr("Karları Temizle (F9)", "Clear Snow (F9)");
            _trayExitItem.Text = Tr("Çıkış", "Exit");

            SyncMeltLabel();
            SyncLightsButton();
            SyncIciclesButton();
            SyncCableCarButton();
            SyncPerfButton();
            SyncPauseButton();
            SyncInteractiveBadge();
            SyncMultiMonitorButton();
        }

        private ModernSlider AddSliderRow(int y, int min, int max, int value, out Label capLabel, out Label valueLabel)
        {
            capLabel = new Label
            {
                Font = new Font("Segoe UI", 8.2f),
                ForeColor = Color.FromArgb(203, 213, 225),
                Location = new Point(L, y),
                AutoSize = true
            };
            Controls.Add(capLabel);

            valueLabel = new Label
            {
                Font = new Font("Consolas", 8.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                Location = new Point(L + W - 90, y),
                Size = new Size(90, 16),
                TextAlign = ContentAlignment.MiddleRight
            };
            Controls.Add(valueLabel);

            ModernSlider sl = new ModernSlider
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Max(min, Math.Min(max, value)),
                Location = new Point(L, y + 16),
                Size = new Size(W, 20)
            };
            Controls.Add(sl);
            return sl;
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

        private void SyncLightsButton()
        {
            _lightsBtn.Text = _overlay.FairyLightsEnabled ? Tr("🎄 Işıklar: Açık", "🎄 Lights: On") : Tr("🎄 Işıklar: Kapalı", "🎄 Lights: Off");
        }

        private void SyncIciclesButton()
        {
            _iciclesBtn.Text = _overlay.IciclesEnabled ? Tr("🧊 Sarkıtlar: Açık", "🧊 Icicles: On") : Tr("🧊 Sarkıtlar: Kapalı", "🧊 Icicles: Off");
        }

        private void SyncCableCarButton()
        {
            if (_overlay.CableCarEnabled)
            {
                _cableCarBtn.Text = Tr("🚡 Teleferik: Açık (2 Pencere Arası)", "🚡 Cable Car: On (Between 2 Apps)");
                _cableCarBtn.FillColor = Color.FromArgb(127, 29, 29);
                _cableCarBtn.BorderColor = Color.FromArgb(248, 113, 113);
                _cableCarBtn.ForeColor = Color.FromArgb(254, 226, 226);
            }
            else
            {
                _cableCarBtn.Text = Tr("🚡 Teleferik: Kapalı", "🚡 Cable Car: Off");
                _cableCarBtn.FillColor = Color.FromArgb(24, 35, 54);
                _cableCarBtn.BorderColor = Color.FromArgb(46, 62, 88);
                _cableCarBtn.ForeColor = Color.FromArgb(203, 213, 225);
            }
            _cableCarBtn.Invalidate();
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

        private void SyncPerfButton()
        {
            if (_overlay.PerformanceMode)
            {
                _perfBtn.Text = Tr("⚡ Performans: Açık", "⚡ Perf Mode: On");
                _perfBtn.FillColor = Color.FromArgb(21, 128, 61);
                _perfBtn.BorderColor = Color.FromArgb(74, 222, 128);
            }
            else
            {
                _perfBtn.Text = Tr("⚡ Performans", "⚡ Perf Mode");
                _perfBtn.FillColor = Color.FromArgb(24, 35, 54);
                _perfBtn.BorderColor = Color.FromArgb(46, 62, 88);
            }
            _perfBtn.Invalidate();
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
    // 10b. AYARLARIN KALICI OLARAK SAKLANMASI (%AppData%\SnowyWindows\settings.ini)
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
                        case "blizzard": o.BlizzardMode = f > 0.5f; break;
                        case "multiMonitor": if (f > 0.5f) o.SetMultiMonitorMode(true); break;
                        case "autoPause": o.AutoPauseOnFullscreen = f > 0.5f; break;
                        case "perf": o.PerformanceMode = f > 0.5f; break;
                        case "lang": o.IsEnglish = f > 0.5f; break;
                    }
                }
            }
            catch
            {
                // Bozuk ayar dosyası uygulamayı engellemez; varsayılanlar kullanılır
            }
        }

        public static void Save(SnowOverlayForm o)
        {
            try
            {
                string path = FilePath;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.Globalization.CultureInfo ci = System.Globalization.CultureInfo.InvariantCulture;
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
                    "blizzard=" + (o.BlizzardMode ? "1" : "0"),
                    "multiMonitor=" + (o.MultiMonitorEnabled ? "1" : "0"),
                    "autoPause=" + (o.AutoPauseOnFullscreen ? "1" : "0"),
                    "perf=" + (o.PerformanceMode ? "1" : "0"),
                    "lang=" + (o.IsEnglish ? "1" : "0")
                };
                System.IO.File.WriteAllLines(path, lines);
            }
            catch
            {
                // Yazma izni yoksa sessizce geç
            }
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

            SnowOverlayForm overlay = new SnowOverlayForm();
            SettingsStore.Load(overlay);
            ControlPanelForm panel = new ControlPanelForm(overlay);

            overlay.Show();
            panel.Show();
            overlay.ControlPanelHwnd = panel.Handle;
            overlay.StartSimulation();

            Application.Run(panel);
        }
    }
}