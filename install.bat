@echo off
rem Installs the Bolt Tool so AutoCAD loads it automatically at every start.
rem Run build.bat first. Close AutoCAD before running this.
set SRC=%~dp0bin\Release\net10.0-windows
set DST=%APPDATA%\Autodesk\ApplicationPlugins\WS.BoltTool.bundle
if not exist "%SRC%\WS.BoltTool.dll" (
  echo WS.BoltTool.dll not found - run build.bat first.
  pause & exit /b 1
)
if not exist "%DST%\Contents" mkdir "%DST%\Contents"
copy /y "%~dp0PackageContents.xml" "%DST%\" >nul
copy /y "%SRC%\WS.BoltTool.*" "%DST%\Contents\" >nul
if errorlevel 1 (
  echo Copy failed - is AutoCAD still running? Close it and try again.
  pause & exit /b 1
)
echo Installed to: %DST%
echo Start AutoCAD - BOLTTOOL, BOLTEDIT and BOLTTORQUE are available right away.
pause
