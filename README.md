# OpenDALI_EVG

A completely open-source Proof of concept for a ultra low standby current **DALI-2-compatible** Electronic Control Gear (ECG/EVG) for controlling LEDs. Built around the **CH32V003** RISC-V microcontroller. Implements IEC 62386 — not certified by, affiliated with, or endorsed by the DALI Alliance (DiiA).

> [!WARNING]
> This project is a work in progress and not yet ready for production use. Hardware designs and firmware are subject to change.

> [!NOTE]
> **Trademark notice:** *DALI*, *DALI-2*, *D4i*, *DALI+* and *DiiA* are trademarks of the Digital Illumination Interface Alliance (DiiA). This project is an independent open-source implementation and compatible with the IEC 62386 standard. It is referred to as *DALI-2-compatible*, it has not undergone DiiA certification and may not be marketed as a DALI product.
> Use for educational purposes only!

## Overview

This project implements a DALI-2-compatible control gear device per **IEC 62386-101** (bus/protocol) and **IEC 62386-102** (control gear commands), including **DT8 colour control** (IEC 62386-209) for RGBW and colour temperature mixing.

Key highlights:
- Full DALI-2-compatible protocol stack in bare-metal C, under 10 KB flash
- 4-channel PWM output (RGBW) at 20 kHz with 2400-step resolution
- WS2812/SK6812 addressable LED strip support (SPI+DMA, up to ~300 LEDs)
- Logarithmic dimming curve per IEC 62386-102
- 16 scenes, 16 groups, fade engine with configurable fade time/rate
- Full commissioning support (INITIALISE, RANDOMISE, SEARCHADDR, PROGRAM)
- All configuration persisted to flash (survives power cycles)
- Bus-powered design possible (< 2 mA quiescent from DALI bus)
- Over-the-air firmware updates via DALI bus (DALI bootloader)

## Project Structure

```
OpenDALI_EVG/
├── Firmware/           CH32V003 DALI slave firmware (PlatformIO project)
├── Bootloader/         IEC 62386-105 compatible DALI bootloader (1876 bytes, I2C EEPROM staging)
├── EVG_Updater/        C# Tool for updating Firmware over IEC 62386-105 compatible communication
├── Hardware/           PCB Schematics and Gerbers
└── Simulationen/       LTspice PHY and power supply simulations
```

### Firmware

The firmware is a standalone PlatformIO project targeting the CH32V003F4U6, built on [ch32v003fun](https://github.com/cnlohr/ch32v003fun). Supports 7 LED output modes selected via a single `EVG_MODE_xxx` define:

- **PWM modes**: SINGLE (1ch), CCT (2ch), RGB (3ch), RGBW (4ch) — TIM1 at 20 kHz, 2400-step resolution
- **Digital LED modes**: WS2812, SK6812_RGB, SK6812_RGBW — SPI1+DMA on PC6

See [Firmware/README.md](Firmware/README.md) for architecture, commands, and test documentation.

### Bootloader

IEC 62386-105 compatible firmware-over-DALI-bus bootloader (1876 / 1920 bytes). Uses 32-bit forward frames for bulk data transfer (3 bytes/frame, ~2.5 min for 10 KB). Firmware is staged in an I2C EEPROM before committing to flash. Validates Block 0 GTIN and EVG mode ID. See [Bootloader/README.md](Bootloader/README.md).

### Hardware

PCB designs (schematics, Gerbers, JLCPCB assembly files). See [Hardware/README.md](Hardware/README.md) for board descriptions and details.

- **Controller** — DALI-compatible PHY + CH32V003 MCU board
- **LoadBoard 250W** — 4-channel RGBW LED driver + AC mains switching (ACST410 triac, MOC3043)

**Design goals:**
- **Ultra-low standby (~30 mW):** When LEDs are off, the triac disconnects the external PSU entirely from mains — eliminating its standby consumption. The Controller remains fully operational, powered solely from the DALI bus (< 2 mA).
- **Modular architecture:** The Controller is a standalone unit handling all communication and signal generation. LoadBoards are interchangeable and connect via a standardized FFC interface — swap them to match different power classes without changing the Controller.

### Simulations

LTspice simulations for the DALI-compatible PHY layer:
- **Phy_Non-Isolated.asc** — Bus-powered PHY with direct MCU connection
- **Phy_Isolated.asc** — Optocoupler-isolated PHY variant

## Hardware Platform

| Parameter | Value |
|-----------|-------|
| MCU | CH32V003F4U6 (RISC-V, 48 MHz, 16 KB Flash, 2 KB RAM) |
| DALI-compatible Interface | Via PHY transceiver (PC3 RX, PC4 TX) |
| PWM Channels | 4 (RGBW via TIM1, partial remap 1) |
| Digital LED | WS2812/SK6812 via SPI1+DMA (same pin as PWM CH1, compile-time select) |
| Supply | Bus-powered from DALI (with DC-DC converter) or external |

See [Hardware/README.md](Hardware/README.md) for the full pin assignment table and board details.

## Status

The firmware is functional and tested with an OpenKNX-DALI Gateway

## License

MIT
