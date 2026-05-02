/*
    eeprom_layout.h - AT24C256 EEPROM memory map

    Shared between firmware and bootloader. Defines addresses and
    structures for device identity, DALI config, and firmware staging.

    AT24C256: 32 KB = 0x0000–0x7FFF, 64-byte page write.
*/
#ifndef _EEPROM_LAYOUT_H
#define _EEPROM_LAYOUT_H

#include <stdint.h>

/* ── Memory regions ──────────────────────────────────────────────── */
#define EE_ADDR_IDENTITY    0x0000  /* Device identity (64 B) */
#define EE_ADDR_CONFIG      0x0040  /* DALI config (64 B) */
#define EE_ADDR_FW_STAGING  0x0080  /* Firmware staging area (32,640 B) */

#define EE_IDENTITY_SIZE    64
#define EE_CONFIG_SIZE      64
#define EE_FW_STAGING_SIZE  (32768 - EE_ADDR_FW_STAGING)

/* ── Magic number (shared with bootloader) ───────────────────────── */
#define EE_MAGIC            0x44414C49  /* "DALI" */

/* ── Device identity block (0x0000–0x003F) ───────────────────────
   Written by firmware at boot. Read by bootloader for Block 0
   validation (GTIN + device key matching).

   Layout:
     0x00  4B  magic         (0x44414C49)
     0x04  6B  gtin          (MSB first, from DALI_GTIN)
     0x0A  1B  evg_mode_id   (EVG mode identifier for device key check)
     0x0B  1B  hw_ver_major
     0x0C  1B  hw_ver_minor
     0x0D  1B  fw_ver_major
     0x0E  1B  fw_ver_minor
     0x0F  1B  short_address (0-63 or 0xFF, updated on address change)
     0x10  48B reserved (0xFF)
*/
typedef struct {
    uint32_t magic;
    uint8_t  gtin[6];
    uint8_t  evg_mode_id;
    uint8_t  hw_ver_major;
    uint8_t  hw_ver_minor;
    uint8_t  fw_ver_major;
    uint8_t  fw_ver_minor;
    uint8_t  short_address;
} ee_identity_t;

/* Offsets within identity block (for bootloader direct byte access) */
#define EE_ID_MAGIC_OFF     0x00
#define EE_ID_GTIN_OFF      0x04
#define EE_ID_EVG_MODE_OFF  0x0A
#define EE_ID_SHORT_ADDR_OFF 0x0F

/* ── EVG mode identifiers ────────────────────────────────────────── */
#define EVG_MODE_ID_ONOFF       0x01
#define EVG_MODE_ID_SINGLE      0x02
#define EVG_MODE_ID_CCT         0x03
#define EVG_MODE_ID_RGB         0x04
#define EVG_MODE_ID_RGBW        0x05
#define EVG_MODE_ID_WS2812      0x06
#define EVG_MODE_ID_SK6812_RGB  0x07
#define EVG_MODE_ID_SK6812_RGBW 0x08

#endif /* _EEPROM_LAYOUT_H */
