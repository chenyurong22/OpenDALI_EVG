/*
    dali_nvm.h - Non-volatile storage for DALI configuration

    Stores DALI operating parameters in the AT24C256 I2C EEPROM at
    address EE_ADDR_CONFIG (see eeprom_layout.h). Uses deferred write:
    config commands set a dirty flag, and nvm_tick() writes to EEPROM
    after 5 seconds of no changes (batches burst writes).

    AT24C256 endurance: 1,000,000 write cycles (vs 10,000 for flash).

    The API is unchanged from the original flash-based implementation.
    All callers use nvm_init/nvm_save/nvm_mark_dirty/nvm_tick.
*/
#ifndef _DALI_NVM_H
#define _DALI_NVM_H

#include <stdint.h>

/* Magic number to validate stored data ("DALI" in ASCII) */
#define NVM_MAGIC           0x44414C49

/* Deferred write delay: milliseconds after last change before saving */
#define NVM_SAVE_DELAY_MS   5000

/*
 * NVM data structure — stored in EEPROM config area (64 bytes).
 * Layout matches the former flash NVM struct for compatibility.
 */
typedef struct dali_nvm_t {
    uint32_t magic;              /* 0x44414C49 = valid data */
    uint8_t  short_address;      /* 0–63 or 0xFF (unassigned) */
    uint8_t  max_level;          /* 1–254, default 254 */
    uint8_t  min_level;          /* 1–254, default 1 */
    uint8_t  power_on_level;     /* 0–254 or 0xFF (=last level), default 254 */
    uint8_t  sys_fail_level;     /* 0–254 or 0xFF, default 254 */
    uint8_t  fade_time;          /* 0–15, default 0 */
    uint8_t  fade_rate;          /* 1–15, default 7 */
    uint8_t  _pad1;              /* Alignment padding */
    uint16_t group_membership;   /* Bit N = member of group N */
    uint8_t  scene_level[16];    /* 0–254 or 0xFF (MASK = not in scene) */
    uint8_t  colour[4];          /* DT8 RGBW levels, 0xFF = default (254) */
    uint16_t colour_tc;          /* DT8 colour temp in mirek, 0xFFFF = not set */
    uint8_t  ext_fade;           /* DALI-2 extended fade time: (mult<<4)|base, 0xFF = not set */
    uint8_t  _reserved[23];     /* Future use — initialized to 0xFF */
} dali_nvm_t;

/* Initialize NVM — reads EEPROM, restores state if valid.
 * Also writes device identity block (GTIN, EVG mode, versions).
 * Call eeprom_init() BEFORE this. Call BEFORE dali_power_on(). */
void nvm_init(void);

/* Write current state to EEPROM immediately. ~10 ms (write cycle). */
void nvm_save(void);

/* Mark NVM as dirty — a persistent variable has changed. */
void nvm_mark_dirty(void);

/* Main loop tick — saves to EEPROM after NVM_SAVE_DELAY_MS of no changes. */
void nvm_tick(void);

/* Pack current device state (ds) into NVM struct. */
void nvm_pack_state(dali_nvm_t *nvm);

/* Restore device state (ds) from NVM struct. */
void nvm_unpack_state(const dali_nvm_t *nvm);

#endif
