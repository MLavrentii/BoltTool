@echo off
rem Removes the Bolt Tool autoload. Close AutoCAD first.
set DST=%APPDATA%\Autodesk\ApplicationPlugins\WS.BoltTool.bundle
if exist "%DST%" rmdir /s /q "%DST%"
echo Bolt Tool autoload removed.
pause
