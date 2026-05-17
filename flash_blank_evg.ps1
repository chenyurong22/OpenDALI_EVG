# Flash blank CH32V003-based EVGs in a loop:
#   1. Bootloader        -> 0x1FFFF000 (boot area, 1920 B fixed)
#   2. configurebootloader -> 0x08000000 (sets option bytes on first run)
#   3. Firmware           -> 0x08000000 (PlatformIO build + upload, overwrites configurebootloader)
#   4. Verify option bytes (RDPR, USER complement, FLASH_OBR)
#
# Polls every second for an attached chip. When found -> run full sequence.
# After success or failure -> wait for chip removal, then poll again.
# Press ESC to quit.
#
# Script lives at OpenDALI_EVG/flash_blank_evg.ps1, so $PSScriptRoot is the
# OpenDALI_EVG project root. Bootloader + Firmware are direct subfolders.

$Root          = $PSScriptRoot
$Wlink         = Join-Path $env:USERPROFILE '.platformio\packages\tool-wlink\wlink.exe'
$PioExe        = Join-Path $env:USERPROFILE '.platformio\penv\Scripts\platformio.exe'
$BootloaderDir = Join-Path $Root 'Bootloader'
$FirmwareDir   = Join-Path $Root 'Firmware'
$BootloaderPio    = Join-Path $BootloaderDir '.pio\build\dali_bootloader\firmware.bin'
$BootloaderLegacy = Join-Path $BootloaderDir 'dali_bootloader.bin'
$FirmwarePio      = Join-Path $FirmwareDir   '.pio\build\genericCH32V003F4P6\firmware.bin'
$ConfigBoot       = Join-Path $BootloaderDir 'configurebootloader.bin'

function Wait-ForExit {  # 'Wait' is an approved PS verb; behaviour: pause until Enter

    Write-Host ''
    Write-Host 'Press Enter to close window...' -ForegroundColor Yellow
    try { [void](Read-Host) } catch {}
}

# Verify required tools + binaries exist BEFORE entering loop
foreach ($p in @($Wlink, $PioExe, $ConfigBoot, $BootloaderDir, $FirmwareDir)) {
    if (-not (Test-Path $p)) {
        Write-Host ("ERROR: Missing file: {0}" -f $p) -ForegroundColor Red
        Wait-ForExit
        exit 1
    }
}

# Ensure a PIO project has a build artifact at $ExpectedBin; if not, runs
# `pio run` in $ProjectDir and re-checks. Throws on build failure.
function Invoke-EnsureBuild {
    param(
        [Parameter(Mandatory=$true)][string]$ProjectDir,
        [Parameter(Mandatory=$true)][string]$ExpectedBin,
        [Parameter(Mandatory=$true)][string]$Label
    )
    if (Test-Path $ExpectedBin) {
        Write-Host ("  [{0}] build present" -f $Label) -ForegroundColor DarkGray
        return
    }
    Write-Host ("  [{0}] no build artifact, compiling..." -f $Label) -ForegroundColor Yellow
    Push-Location $ProjectDir
    try {
        & $PioExe run
        if ($LASTEXITCODE -ne 0) { throw ("{0} build failed (pio exit {1})" -f $Label, $LASTEXITCODE) }
    } finally { Pop-Location }
    if (-not (Test-Path $ExpectedBin)) {
        throw ("{0} build did not produce expected output: {1}" -f $Label, $ExpectedBin)
    }
    Write-Host ("  [{0}] build OK" -f $Label) -ForegroundColor Green
}

# Pre-flight: make sure both binaries exist before we ask anyone to plug a
# chip in. Bootloader has a legacy fallback (build.bat output) — if present
# we honor it without re-building. Firmware is PIO-only.
Write-Host 'Pre-flight: verifying builds...' -ForegroundColor Cyan
try {
    if (Test-Path $BootloaderPio) {
        $Bootloader    = $BootloaderPio
        $BootloaderSrc = 'PIO build'
        Write-Host '  [Bootloader] PIO build present' -ForegroundColor DarkGray
    } elseif (Test-Path $BootloaderLegacy) {
        $Bootloader    = $BootloaderLegacy
        $BootloaderSrc = 'build.bat legacy'
        Write-Host '  [Bootloader] using legacy build.bat output' -ForegroundColor DarkGray
    } else {
        Invoke-EnsureBuild -ProjectDir $BootloaderDir -ExpectedBin $BootloaderPio -Label 'Bootloader'
        $Bootloader    = $BootloaderPio
        $BootloaderSrc = 'PIO build (just compiled)'
    }
    Invoke-EnsureBuild -ProjectDir $FirmwareDir -ExpectedBin $FirmwarePio -Label 'Firmware'
} catch {
    Write-Host ''
    Write-Host ('PRE-FLIGHT FAILED: {0}' -f $_.Exception.Message) -ForegroundColor Red
    Wait-ForExit
    exit 1
}

# ESC detection — works on real consoles; silently no-ops if redirected.
$global:CanReadKey = $true
function Test-EscPressed {
    if (-not $global:CanReadKey) { return $false }
    try {
        while ([Console]::KeyAvailable) {
            $k = [Console]::ReadKey($true)
            if ($k.Key -eq 'Escape') { return $true }
        }
    } catch {
        $global:CanReadKey = $false
        Write-Host '(ESC quit disabled - console does not support raw key input. Use Ctrl+C.)' -ForegroundColor DarkYellow
    }
    return $false
}

function Test-ChipPresent {
    try {
        $out = & $Wlink status 2>&1 | Out-String
        return ($out -match 'Attached chip:\s*CH32V003')
    } catch {
        return $false
    }
}

function Get-Bytes($text, $addr, $count) {
    # Strip ANSI color escape sequences (wlink colors its output)
    $esc = [char]27
    $clean = [regex]::Replace($text, "$esc\[[0-9;]*m", '')
    # Match the data line specifically: starts with the address followed by ':'
    # (skips wlink INFO lines that merely mention the address in their text)
    $pattern = '^\s*' + [regex]::Escape($addr) + '\s*:'
    $line = ($clean -split "`n") | Where-Object { $_ -match $pattern } | Select-Object -First 1
    if (-not $line) { return $null }
    # Everything after the first colon is the hex byte payload + ASCII column
    $idx = $line.IndexOf(':')
    if ($idx -lt 0) { return $null }
    $rest = $line.Substring($idx + 1)
    $hex = [regex]::Matches($rest, '(?<![0-9a-fA-F])[0-9a-fA-F]{2}(?![0-9a-fA-F])')
    if ($hex.Count -lt $count) { return $null }
    $out = @()
    for ($i = 0; $i -lt $count; $i++) {
        $out += [Convert]::ToInt32($hex[$i].Value, 16)
    }
    return ,$out
}

function Invoke-FlashSequence {
    Write-Host ''
    Write-Host ('--- Step 1/4: bootloader ({0}) -> 0x1FFFF000 ---' -f $BootloaderSrc) -ForegroundColor Cyan
    & $Wlink flash --address 0x1FFFF000 $Bootloader
    if ($LASTEXITCODE -ne 0) { throw 'Bootloader flash failed' }

    Write-Host ''
    Write-Host '--- Step 2/4: configurebootloader -> 0x08000000 ---' -ForegroundColor Cyan
    & $Wlink flash $ConfigBoot
    if ($LASTEXITCODE -ne 0) { throw 'configurebootloader flash failed' }
    Start-Sleep -Milliseconds 500

    Write-Host ''
    Write-Host '--- Step 3/4: firmware -> 0x08000000 ---' -ForegroundColor Cyan
    Push-Location $FirmwareDir
    try {
        & $PioExe run -t upload
        if ($LASTEXITCODE -ne 0) { throw 'Firmware upload failed' }
    } finally { Pop-Location }

    Write-Host ''
    Write-Host '--- Step 4/4: verify option bytes ---' -ForegroundColor Cyan
    $dump = & $Wlink dump 0x1FFFF800 4 2>&1 | Out-String
    $obrd = & $Wlink dump 0x4002201C 4 2>&1 | Out-String
    $ob   = Get-Bytes $dump '1ffff800' 4
    $obr  = Get-Bytes $obrd '4002201c' 4
    if ($null -eq $ob  -or $ob.Count  -lt 4) { throw 'Could not parse option byte dump' }
    if ($null -eq $obr -or $obr.Count -lt 4) { throw 'Could not parse FLASH_OBR dump' }

    $rdpr     = $ob[0];  $rdprN = $ob[1]
    $user     = $ob[2];  $userN = $ob[3]
    $obrVal   = $obr[0] -bor ($obr[1] -shl 8) -bor ($obr[2] -shl 16) -bor ($obr[3] -shl 24)
    $opterr   = $obrVal -band 0x1
    $rdprt    = ($obrVal -shr 1)  -band 0x1
    $startmd  = ($obrVal -shr 14) -band 0x1

    Write-Host ("  RDPR=0x{0:X2}  ~RDPR=0x{1:X2}  USER=0x{2:X2}  ~USER=0x{3:X2}  OBR=0x{4:X8}  STARTMODE={5}" -f `
                $rdpr, $rdprN, $user, $userN, $obrVal, $startmd)

    $ok = ($rdpr -eq 0xA5) -and (($rdpr + $rdprN) -eq 0xFF) -and `
          ($user -eq 0xEF) -and (($user + $userN) -eq 0xFF) -and `
          ($opterr -eq 0) -and ($rdprt -eq 0) -and ($startmd -eq 1)
    if (-not $ok) { throw 'Option bytes deviate from expected values' }
}

# ── Main loop ──────────────────────────────────────────────────────────────
Write-Host 'Blank EVG flasher running. Connect chips one by one. Press ESC to quit.' -ForegroundColor Yellow

$count = 0
$shouldExit = $false
try {
    while (-not $shouldExit) {
        if (Test-EscPressed) { break }

        if (Test-ChipPresent) {
            $count++
            Write-Host ''
            Write-Host ('############# EVG #{0} - START #############' -f $count) -ForegroundColor Magenta
            $sw = [Diagnostics.Stopwatch]::StartNew()
            try {
                Invoke-FlashSequence
                $sw.Stop()
                Write-Host ''
                Write-Host ('############# EVG #{0} - PASS in {1:N1}s #############' -f $count, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
            } catch {
                $sw.Stop()
                Write-Host ''
                Write-Host ('############# EVG #{0} - FAIL: {1} #############' -f $count, $_.Exception.Message) -ForegroundColor Red
            }

            Write-Host ''
            Write-Host 'Disconnect chip to continue (or ESC to quit)...' -ForegroundColor Yellow
            while (Test-ChipPresent) {
                if (Test-EscPressed) { $shouldExit = $true; break }
                Start-Sleep -Milliseconds 500
            }
            if (-not $shouldExit) {
                Write-Host 'Chip removed. Waiting for next chip...' -ForegroundColor Yellow
            }
        } else {
            Start-Sleep -Milliseconds 1000
        }
    }
} catch {
    Write-Host ''
    Write-Host ('UNEXPECTED ERROR: {0}' -f $_.Exception.Message) -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkRed
}

Write-Host ''
Write-Host ('Done. Total chips processed: {0}' -f $count) -ForegroundColor Yellow
Wait-ForExit
