@echo off
rem Build script for DesktopClock - double-click to compile.
rem Uses the built-in .NET Framework 4.x compiler. No Visual Studio required.
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:DesktopClock.exe /win32icon:clock.ico /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" /r:"%FW%\System.dll" DesktopClock.cs
if errorlevel 1 goto fail
echo.
echo [OK] DesktopClock.exe created with embedded icon.
echo      Keep clock.ico next to the exe for the window and taskbar icon.
goto end
:fail
echo.
echo [FAIL] Compilation failed. See errors above.
:end
pause
