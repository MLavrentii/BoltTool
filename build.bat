@echo off
rem Build the Bolt Tool for AutoCAD 2027 (.NET 10 SDK required: https://dotnet.microsoft.com/download/dotnet/10.0)
rem If AutoCAD is installed elsewhere: build.bat "D:\Autodesk\AutoCAD 2027"
set ACAD=%~1
if "%ACAD%"=="" set ACAD=C:\Program Files\Autodesk\AutoCAD 2027
dotnet build "%~dp0BoltTool.csproj" -c Release -p:AcadDir="%ACAD%"
if errorlevel 1 (echo. & echo BUILD FAILED - send the error list to Claude. & pause & exit /b 1)
echo.
echo Built: %~dp0bin\Release\net10.0-windows\BoltTool.dll
echo In AutoCAD: NETLOAD that file, then type BOLTTOOL.
pause
