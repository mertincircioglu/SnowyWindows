@echo off
chcp 65001 >nul
title Snowy Windows - Tek Tikla C# .EXE ve Ikon Derleyici
echo ============================================================
echo   Snowy for Windows - Yerel C# .EXE Derleyici (Kardan Adam Ikonlu)
echo ============================================================
echo.

REM Eger eski SnowyWindows.exe aciksa derleme kilidini onlemek icin kapat
taskkill /F /IM SnowyWindows.exe >nul 2>&1
timeout /t 1 /nobreak >nul

set "CSC64=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "CSC32=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
set "CSC="

if exist "%CSC64%" (
    set "CSC=%CSC64%"
) else if exist "%CSC32%" (
    set "CSC=%CSC32%"
)

if "%CSC%"=="" (
    echo [HATA] Windows yerlesik C# derleyicisi ^(csc.exe^) bulunamadi.
    pause
    exit /b 1
)

set "WP_RES="
if exist "%~dp0wallpapers\wp_arctic_night.jpg" set WP_RES=%WP_RES% "/resource:%~dp0wallpapers\wp_arctic_night.jpg,wp_arctic_night.jpg"
if exist "%~dp0wallpapers\wp_full_moon.jpg" set WP_RES=%WP_RES% "/resource:%~dp0wallpapers\wp_full_moon.jpg,wp_full_moon.jpg"
if exist "%~dp0wallpapers\wp_arctic_fjord.jpg" set WP_RES=%WP_RES% "/resource:%~dp0wallpapers\wp_arctic_fjord.jpg,wp_arctic_fjord.jpg"
if exist "%~dp0wallpapers\wp_festive_bokeh.jpg" set WP_RES=%WP_RES% "/resource:%~dp0wallpapers\wp_festive_bokeh.jpg,wp_festive_bokeh.jpg"
if exist "%~dp0wallpapers\wp_mountain_cabin.jpg" set WP_RES=%WP_RES% "/resource:%~dp0wallpapers\wp_mountain_cabin.jpg,wp_mountain_cabin.jpg"

echo [1/3] SnowyWindows.cs derleniyor...
"%CSC%" /nologo /target:winexe /optimize+ %WP_RES% /r:System.dll,System.Drawing.dll,System.Windows.Forms.dll /out:"%~dp0SnowyWindows.exe" "%~dp0SnowyWindows.cs"

if %ERRORLEVEL% neq 0 (
    echo.
    echo [HATA] Derleme sirasinda bir sorun olustu.
    pause
    exit /b %ERRORLEVEL%
)

echo [2/3] Kardan Adam uygulama ikonu ^(SnowyIcon.ico^) olusturuluyor ve .EXE dosyasina gomuluyor...
"%~dp0SnowyWindows.exe" --make-icon
if exist "%~dp0SnowyIcon.ico" (
    "%CSC%" /nologo /target:winexe /optimize+ /win32icon:"%~dp0SnowyIcon.ico" %WP_RES% /r:System.dll,System.Drawing.dll,System.Windows.Forms.dll /out:"%~dp0SnowyWindows.exe" "%~dp0SnowyWindows.cs"
)

echo [3/3] Derleme basarili! Ikonlu "SnowyWindows.exe" baslatiliyor...
start "" "%~dp0SnowyWindows.exe"
exit /b 0
