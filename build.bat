@echo off
rem Builds the Spotify plugin for Muxevour: Muxevour.Streaming.dll (put it in %APPDATA%\Muxevour\Plugins).
rem Muxevour.exe has to be built first (the plugin is made against it): ..\..\XVR Player\build.bat
cd /d "%~dp0"
set MUX=..\..\XVR Player\Muxevour.exe
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /target:library /platform:anycpu /optimize+ /out:"Muxevour.Streaming.dll" "/r:%MUX%" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "/r:%FW%\WPF\PresentationCore.dll" "/r:%FW%\WPF\PresentationFramework.dll" "/r:%FW%\WPF\WindowsBase.dll" /r:System.Xaml.dll "/resource:Resources\spotify-icon-white.png,spotify-icon-white.png" "/resource:Resources\spotify-icon-black.png,spotify-icon-black.png" "/resource:Resources\spotify-logo-white.png,spotify-logo-white.png" "/resource:Resources\spotify-logo-black.png,spotify-logo-black.png" "/resource:Resources\spotify-like.png,spotify-like.png" "/resource:Resources\spotify-liked.png,spotify-liked.png" *.cs
if errorlevel 1 (exit /b 1)
echo Built "Muxevour.Streaming.dll"
