@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem BoltTool force reinstall for AutoCAD 2027.
rem Run with all AutoCAD windows closed.
rem This script rebuilds, stages a clean bundle, swaps it in, and verifies DLL bytes.
rem Usage: install.bat ["C:\path\to\AutoCAD 2027"]

set "ROOT=%~dp0"
set "ACAD=%~1"
if not defined ACAD set "ACAD=C:\Program Files\Autodesk\AutoCAD 2027"
set "BUILT=%ROOT%bin\Release\net10.0-windows"
set "APPS=%APPDATA%\Autodesk\ApplicationPlugins"
set "DEST=%APPS%\BoltTool.bundle"
set "STAGE=%APPS%\BoltTool.bundle.installing"
set "PREVIOUS=%APPS%\BoltTool.bundle.previous"
set "LEGACY=%APPS%\WS.BoltTool.bundle"

echo.
echo === BoltTool: FORCE REINSTALL ===
echo Project: "%ROOT%"
echo AutoCAD: "%ACAD%"
echo.

if not defined APPDATA (
  echo ERROR: APPDATA is not defined.
  goto :failed
)
if not exist "%ROOT%BoltTool.csproj" (
  echo ERROR: BoltTool.csproj was not found beside install.bat.
  echo You may be running the installer from the wrong folder.
  goto :failed
)
if not exist "%ROOT%PackageContents.xml" (
  echo ERROR: PackageContents.xml was not found.
  goto :failed
)
where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: dotnet SDK is not available on PATH.
  goto :failed
)

rem A loaded AutoCAD .NET plugin cannot be replaced safely in a running process.
tasklist /FI "IMAGENAME eq acad.exe" /NH 2>nul | find /I "acad.exe" >nul
if not errorlevel 1 (
  echo ERROR: AutoCAD is still running. Close ALL AutoCAD windows first.
  goto :failed
)

rem Refuse to reinstall obsolete source that cannot display the requested v2 controls.
if not exist "%ROOT%UI\BoltToolWindow.xaml" (
  echo ERROR: New UI source not found in this project folder.
  goto :failed
)
findstr /L /C:"SnapshotBtn" "%ROOT%UI\BoltToolWindow.xaml" >nul
if errorlevel 1 (
  echo ERROR: This project does not contain the new Save JPG control.
  echo ERROR: You are trying to reinstall the OLD BoltTool source.
  echo Extract the reviewed source into this exact project folder first.
  goto :failed
)
if not exist "%ROOT%UI\BoltToolWindow.xaml.cs" (
  echo ERROR: New UI code-behind is missing.
  goto :failed
)
findstr /L /C:"RebuildStackUi" "%ROOT%UI\BoltToolWindow.xaml.cs" >nul
if errorlevel 1 (
  echo ERROR: The updated editable Assembly element stack is missing.
  echo Extract the reviewed source into this exact project folder first.
  goto :failed
)

echo [1/5] Rebuilding the CURRENT project source...
dotnet build "%ROOT%BoltTool.csproj" -c Release -p:AcadDir="%ACAD%"
if errorlevel 1 (
  echo ERROR: Build failed. Existing installation was NOT changed.
  goto :failed
)
if not exist "%BUILT%\BoltTool.dll" (
  echo ERROR: Built DLL not found: "%BUILT%\BoltTool.dll"
  goto :failed
)

echo.
echo [2/5] Creating a clean, staged plugin bundle...
if not exist "%APPS%" mkdir "%APPS%"
if errorlevel 1 goto :stage_failed
if exist "%STAGE%" rmdir /S /Q "%STAGE%"
if exist "%STAGE%" goto :stage_failed
mkdir "%STAGE%\Contents"
if errorlevel 1 goto :stage_failed
copy /Y "%ROOT%PackageContents.xml" "%STAGE%\PackageContents.xml" >nul
if errorlevel 1 goto :stage_failed
copy /Y "%BUILT%\BoltTool.dll" "%STAGE%\Contents\BoltTool.dll" >nul
if errorlevel 1 goto :stage_failed
if exist "%BUILT%\BoltTool.pdb" copy /Y "%BUILT%\BoltTool.pdb" "%STAGE%\Contents\" >nul
if exist "%BUILT%\BoltTool.deps.json" copy /Y "%BUILT%\BoltTool.deps.json" "%STAGE%\Contents\" >nul
if exist "%BUILT%\BoltTool.runtimeconfig.json" copy /Y "%BUILT%\BoltTool.runtimeconfig.json" "%STAGE%\Contents\" >nul
fc /B "%BUILT%\BoltTool.dll" "%STAGE%\Contents\BoltTool.dll" >nul
if errorlevel 1 goto :stage_failed

echo [3/5] Replacing the installed bundle (not skipping existing files)...
rem Keep one previous installation as a non-autoloading backup.
if exist "%PREVIOUS%" rmdir /S /Q "%PREVIOUS%"
if exist "%PREVIOUS%" goto :swap_failed
if exist "%DEST%" (
  move /Y "%DEST%" "%PREVIOUS%" >nul
  if errorlevel 1 goto :swap_failed
)
move /Y "%STAGE%" "%DEST%" >nul
if errorlevel 1 goto :rollback

 echo [4/5] Verifying installed DLL equals the just-built DLL...
fc /B "%BUILT%\BoltTool.dll" "%DEST%\Contents\BoltTool.dll" >nul
if errorlevel 1 goto :rollback

 echo [5/5] Disabling the known older WS.BoltTool.bundle, if present...
if exist "%LEGACY%" (
  ren "%LEGACY%" "WS.BoltTool.bundle.disabled.%RANDOM%"
  if errorlevel 1 (
    echo WARNING: Could not disable "%LEGACY%"
    echo WARNING: Rename it manually while AutoCAD is closed.
  ) else (
    echo Disabled old WS.BoltTool.bundle.
  )
)

rem Detect other possible plugin copies without deleting unrelated directories.
for /D %%D in ("%APPS%\*.bundle") do (
  if /I not "%%~fD"=="%DEST%" if exist "%%~fD\Contents\BoltTool.dll" (
    echo WARNING: Other user bundle containing BoltTool.dll: "%%~fD"
  )
)
if defined PROGRAMDATA (
  for /D %%D in ("%PROGRAMDATA%\Autodesk\ApplicationPlugins\*.bundle") do (
    if exist "%%~fD\Contents\BoltTool.dll" (
      echo WARNING: System-wide bundle containing BoltTool.dll: "%%~fD"
      echo Check it manually; administrator permissions may be required.
    )
  )
)

echo.
echo SUCCESS: New DLL installed and byte-for-byte verified.
echo Installed: "%DEST%\Contents\BoltTool.dll"
echo Previous version backup: "%PREVIOUS%"
echo Saved user settings in %%APPDATA%%\BoltTool were not changed.
echo Start a NEW AutoCAD 2027 session and enter BOLTTOOL.
echo If the old branded title still appears, another old copy is loading.
echo.
pause
exit /B 0

:stage_failed
echo ERROR: Cannot create or verify the staged bundle.
echo Existing installed bundle has not been changed.
goto :failed

:swap_failed
echo ERROR: Could not replace the existing bundle. Check folder permissions.
goto :failed

:rollback
echo ERROR: Swap or verification failed. Trying to restore the prior installation.
if exist "%DEST%" rmdir /S /Q "%DEST%"
if exist "%PREVIOUS%" move /Y "%PREVIOUS%" "%DEST%" >nul
if exist "%PREVIOUS%" echo WARNING: Old version may not have been restored; check "%DEST%".
goto :failed

:failed
echo.
echo INSTALL FAILED. Check the error above. AutoCAD must be closed.
echo.
pause
exit /B 1
