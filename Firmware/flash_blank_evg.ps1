# Flash blank CH32V003-based EVGs in a loop:
#   1. Bootloader        -> 0x1FFFF000 (boot area, 1920 B fixed)
#   2. configurebootloader -> 0x08000000 (sets option bytes on first run)
#   3. Firmware           -> 0x08000000 (PlatformIO build + upload, overwrites configurebootloader)
#   4. Verify option bytes (RDPR, USER complement, FLASH_OBR)
#
# Polls every second for an attached chip. When found -> run full sequence.
# After success or failure -> wait for chip removal, then poll again.
# Press ESC to quit.

$Root        = $PSScriptRoot
$RepoRoot    = Split-Path $Root -Parent
$Wlink       = Join-Path $env:USERPROFILE '.platformio\packages\tool-wlink\wlink.exe'
$PioExe      = Join-Path $env:USERPROFILE '.platformio\penv\Scripts\platformio.exe'
$Bootloader  = Join-Path $RepoRoot 'Bootloader\dali_bootloader.bin'
$ConfigBoot  = Join-Path $RepoRoot 'Bootloader\configurebootloader.bin'

function Pause-OnExit {
    Write-Host ''
    Write-Host 'Press Enter to close window...' -ForegroundColor Yellow
    try { [void](Read-Host) } catch {}
}

# Verify all required tools/binaries exist BEFORE entering loop
foreach ($p in @($Wlink, $PioExe, $Bootloader, $ConfigBoot)) {
    if (-not (Test-Path $p)) {
        Write-Host ("ERROR: Missing file: {0}" -f $p) -ForegroundColor Red
        Pause-OnExit
        exit 1
    }
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
    Write-Host '--- Step 1/4: bootloader -> 0x1FFFF000 ---' -ForegroundColor Cyan
    & $Wlink flash --address 0x1FFFF000 $Bootloader
    if ($LASTEXITCODE -ne 0) { throw 'Bootloader flash failed' }

    Write-Host ''
    Write-Host '--- Step 2/4: configurebootloader -> 0x08000000 ---' -ForegroundColor Cyan
    & $Wlink flash $ConfigBoot
    if ($LASTEXITCODE -ne 0) { throw 'configurebootloader flash failed' }
    Start-Sleep -Milliseconds 500

    Write-Host ''
    Write-Host '--- Step 3/4: firmware -> 0x08000000 ---' -ForegroundColor Cyan
    Push-Location $Root
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
Pause-OnExit
