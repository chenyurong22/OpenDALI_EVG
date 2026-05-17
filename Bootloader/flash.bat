@echo off
REM Flash DALI bootloader onto CH32V003 via WCH-LinkE
REM Requires configurebootloader to have been run once before.
REM
REM Picks up the .bin from either build path:
REM   - .pio/build/dali_bootloader/firmware.bin  (PlatformIO output, preferred)
REM   - dali_bootloader.bin                      (build.bat legacy output)

cd /d "%~dp0"

set WLINK=%USERPROFILE%\.platformio\packages\tool-wlink\wlink.exe
set PIO_BIN=.pio\build\dali_bootloader\firmware.bin
set LEGACY_BIN=dali_bootloader.bin

if exist "%PIO_BIN%" (
    set BIN=%PIO_BIN%
    set BIN_SRC=PlatformIO build
) else if exist "%LEGACY_BIN%" (
    set BIN=%LEGACY_BIN%
    set BIN_SRC=build.bat legacy
) else (
    echo ERROR: no bootloader .bin found.
    echo   Expected: %PIO_BIN%  ^(run "pio run" first^)
    echo   or:       %LEGACY_BIN%  ^(run build.bat first^)
    pause
    exit /b 1
)

echo Using %BIN_SRC%: %BIN%
echo Flashing DALI bootloader to boot area (0x1FFFF000)...
"%WLINK%" flash --address 0x1FFFF000 "%BIN%"
if errorlevel 1 (
    echo.
    echo FLASH FAILED
    pause
    exit /b 1
)

echo.
echo Done. Hold PC7 low during reset to enter DALI bootloader mode.
echo.
pause
