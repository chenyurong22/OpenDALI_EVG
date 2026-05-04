# DALI Firmware Updater

C# WinForms application for updating OpenDALI EVG firmware via the DALI bus, using an OpenKNX GW-REG1-Dali gateway (Lunatone DALI-2 IoT API compatible).

Implements the IEC 62386-105 firmware update protocol over 32-bit DALI forward frames.

## Requirements

- .NET 8.0 SDK
- OpenKNX GW-REG1-Dali gateway (or Lunatone DALI-2 IoT compatible) on the network
- Target device running OpenDALI EVG firmware with IEC 62386-105 bootloader in boot area

## Build

```bash
dotnet build DALI_Updater.sln
```

## Usage

### GUI Mode

Run without arguments to launch the graphical interface:

```bash
DALI_Updater.exe
```

The GUI provides:
- Gateway IP configuration (default: 192.168.178.131)
- Firmware .bin file selection
- DALI short address (0-63)
- GTIN and EVG Mode ID for Block 0 validation
- Progress bar and scrollable log output

### CLI Mode

Pass a firmware file as the first argument for headless operation:

```bash
DALI_Updater.exe firmware.bin [options]
```

Options:

| Option | Default | Description |
|--------|---------|-------------|
| `--ip <address>` | 192.168.178.131 | Gateway IP address |
| `--addr <0-63>` | 0 | DALI short address of target device |
| `--gtin <hex>` | 3452334E0CAD | 6-byte GTIN in hex (must match device EEPROM) |
| `--mode <1-8>` | 5 (RGBW) | EVG mode ID (must match device EEPROM) |
| `--help` | | Show usage help |

Exit codes:
- `0` — Success
- `1` — Argument or connection error
- `2` — Firmware update failed

### Examples

```bash
# Update device at address 0 with default settings
DALI_Updater.exe firmware.bin

# Update device at address 5, RGB mode
DALI_Updater.exe firmware.bin --addr 5 --mode 4

# Use a different gateway
DALI_Updater.exe firmware.bin --ip 10.0.0.50

# Build and flash in one command (from Firmware/ directory)
pio run && ../DALI_Updater/DALI_Updater.exe .pio/build/genericCH32V003F4P6/firmware.bin
```

## EVG Mode IDs

| ID | Mode | Description |
|----|------|-------------|
| 1 | ONOFF | Relay/switch output |
| 2 | SINGLE | Single-channel PWM |
| 3 | CCT | 2-channel warm/cool white |
| 4 | RGB | 3-channel RGB PWM |
| 5 | RGBW | 4-channel RGBW PWM |
| 6 | WS2812 | Addressable LED strip (GRB) |
| 7 | SK6812_RGB | Addressable LED strip (GRB) |
| 8 | SK6812_RGBW | Addressable LED strip (GRBW) |

## Update Protocol (IEC 62386-105)

The update runs in 4 phases:

1. **Enter Bootloader** — Send QUERY FW UPDATE FEATURES (verify device supports updates), then START FW TRANSFER (config repeat, 2x within 100ms). Device responds YES and resets into bootloader.

2. **Block 0 Validation** — Send BEGIN BLOCK 0 followed by TRANSFER BLOCK DATA frames containing GTIN and device key (EVG mode ID). Bootloader validates against EEPROM identity. QUERY BLOCK FAULT to check.

3. **Firmware Transfer** — Send BEGIN BLOCK 1, then TRANSFER BLOCK DATA frames (3 firmware bytes per frame). Data is staged in AT24C256 EEPROM (32 KB, starting at 0x0080).

4. **Commit** — Send FINISH FW UPDATE (config repeat). Bootloader copies EEPROM staging area to flash and reboots into new firmware.

## Gateway API

Communication uses the Lunatone DALI-2 IoT WebSocket API (protocol v3.0). The gateway sends/receives JSON messages over `ws://<ip>`.

Send a DALI frame:
```json
{
  "type": "daliFrame",
  "data": {
    "line": 0,
    "numberOfBits": 32,
    "mode": {
      "sendTwice": false,
      "waitForAnswer": true,
      "priority": 3
    },
    "daliData": [0, 251, 5, 0]
  }
}
```

API documentation: `references/89453886_DALI2_IOT_API_Dokumentation_GER_M0023.pdf`

## Project Structure

```
DALI_Updater/
├── DALI_Updater.sln              Solution file (open in Visual Studio)
├── README.md                     This file
├── dali_bootloader_test.py       Python test script for gateway/bootloader
└── DALI_Updater/
    ├── DALI_Updater.csproj       .NET 8.0 WinForms project
    ├── Program.cs                Entry point (GUI or CLI based on args)
    ├── MainForm.cs               WinForms UI code-behind
    ├── MainForm.Designer.cs      WinForms UI layout
    ├── DaliGateway.cs            WebSocket client for gateway communication
    └── DaliBootloader.cs         IEC 62386-105 protocol state machine
```
