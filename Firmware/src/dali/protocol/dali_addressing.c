/*
    dali_addressing.c - DALI addressing protocol (IEC 62386-102 §9.6)

    Handles all special commands: INITIALISE, RANDOMISE, binary search
    (COMPARE, SEARCHADDR, WITHDRAW), PROGRAM SHORT ADDRESS, VERIFY SHORT,
    QUERY SHORT, TERMINATE, DTR0/1/2, and ENABLE DEVICE TYPE.
*/

#include "ch32fun.h"
#include <stdio.h>
#include "dali_addressing.h"
#include "../../logger.h"
#include "dali_config_repeat.h"
#include "../dali_state.h"
#include "../dali_dtr.h"
#include "../dali_physical.h"
#include "../phy/dali_phy.h"
#include "../nvm/dali_nvm.h"

/* millis() provided by main.c */
extern uint32_t millis(void);

/* ── Addressing state (module-private) ───────────────────────────── */
typedef enum {
    INIT_DISABLED,
    INIT_ENABLED,
    INIT_WITHDRAWN
} init_state_t;

static volatile init_state_t init_state = INIT_DISABLED;
static volatile uint32_t    init_start_time = 0;
/* IEC 62386-102:2009 Table 6: factory default of RANDOM ADDRESS is
 * 0xFFFFFF (not 0), and the field is NVM-persistent (no "RAM" tag).
 * The actual value is loaded from EEPROM by nvm_unpack_state() via
 * dali_addressing_set_random(); these BSS-init values are only used
 * on the very first boot, before any NVM save has occurred. */
static volatile uint8_t     random_h = 0xFF, random_m = 0xFF, random_l = 0xFF;
static volatile uint8_t     search_h = 0xFF, search_m = 0xFF, search_l = 0xFF;

/* Config repeat validation — shared implementation in dali_config_repeat.c */
#define check_config_repeat dali_check_config_repeat

/* ================================================================== *
 *  PUBLIC API                                                         *
 * ================================================================== */

void dali_addressing_process_special(uint8_t addr_byte, uint8_t data_byte) {
    uint32_t now = millis();

    switch (addr_byte) {
    case DALI_SPECIAL_TERMINATE:
        init_state = INIT_DISABLED;
        LOG_CMD("TERM");
        break;

    case DALI_SPECIAL_DTR:
        ds.dtr0 = data_byte;
        break;

    case DALI_SPECIAL_INITIALISE:
        if (check_config_repeat(addr_byte, data_byte, now)) {
            /* IEC 62386-102:2009 §12.5.1:
             *   0x00 → all control gear
             *   0xFF → only gear without a short address
             *   0xAA → specific gear by short address (bit0=1, bits1..6=addr) */
            uint8_t addressed = 0;
            if (data_byte == 0x00) {
                addressed = 1;
            } else if (data_byte == 0xFF) {
                addressed = (ds.short_address == 0xFF);
            } else if (data_byte & 1) {
                addressed = (((data_byte >> 1) & 0x3F) == ds.short_address);
            }
            if (addressed) {
                init_state = INIT_ENABLED;
                init_start_time = now;
                LOG_CMD("INIT ok");
            }
        }
        break;

    case DALI_SPECIAL_RANDOMISE:
        if (init_state != INIT_ENABLED) break;
        if (check_config_repeat(addr_byte, data_byte, now)) {
            /* Rotate UID words by different amounts before XOR — prevents
             * two chips with sequential WCH UIDs from cancelling to an
             * identical seed (observed in 2-EVG bus scan, 2026-05-15). */
            uint32_t uid0 = *(volatile uint32_t *)0x1FFFF7E8;
            uint32_t uid1 = *(volatile uint32_t *)0x1FFFF7EC;
            uint32_t uid2 = *(volatile uint32_t *)0x1FFFF7F0;
            uint32_t seed = SysTick->CNT;
            seed ^= uid0;
            seed ^= (uid1 << 7)  | (uid1 >> 25);
            seed ^= (uid2 << 17) | (uid2 >> 15);
            seed ^= (uint32_t)ds.short_address << 24;
            seed ^= (uint32_t)ds.actual_level << 8;
            seed *= 1103515245UL;
            seed += 12345;
            random_h = (seed >> 16) & 0xFF;
            random_m = (seed >> 8) & 0xFF;
            random_l = seed & 0xFF;
            nvm_mark_dirty();   /* persist across power cycles (IEC 102 Table 6) */
            LOG_CMD("RAND=%02X%02X%02X", random_h, random_m, random_l);
        }
        break;

    case DALI_SPECIAL_COMPARE:
        if (init_state != INIT_ENABLED) break;
        {
            uint32_t random = ((uint32_t)random_h << 16) | ((uint32_t)random_m << 8) | random_l;
            uint32_t search = ((uint32_t)search_h << 16) | ((uint32_t)search_m << 8) | search_l;
            if (random <= search) {
                dali_phy_send_backward(0xFF);
            }
        }
        break;

    case DALI_SPECIAL_WITHDRAW:
        if (init_state != INIT_ENABLED) break;
        {
            uint32_t random = ((uint32_t)random_h << 16) | ((uint32_t)random_m << 8) | random_l;
            uint32_t search = ((uint32_t)search_h << 16) | ((uint32_t)search_m << 8) | search_l;
            if (random == search) {
                init_state = INIT_WITHDRAWN;
                LOG_CMD("WITHDRAW");
            }
        }
        break;

    case DALI_SPECIAL_SEARCHADDRH:
        search_h = data_byte;
        break;

    case DALI_SPECIAL_SEARCHADDRM:
        search_m = data_byte;
        break;

    case DALI_SPECIAL_SEARCHADDRL:
        search_l = data_byte;
        break;

    case DALI_SPECIAL_PROGRAM_SHORT:
        if (init_state != INIT_ENABLED) break;
        {
            uint32_t random = ((uint32_t)random_h << 16) | ((uint32_t)random_m << 8) | random_l;
            uint32_t search = ((uint32_t)search_h << 16) | ((uint32_t)search_m << 8) | search_l;
            if (random == search) {
                if (data_byte == 0xFF) {
                    ds.short_address = 0xFF;
                } else {
                    ds.short_address = dali_decode_short_address(data_byte);
                }
                nvm_mark_dirty();
                LOG_CMD("PROG_SHORT=%d", ds.short_address);
            } else {
                LOG_ERR("PROG_SHORT: random!=search R=%06lX S=%06lX",
                        (unsigned long)random, (unsigned long)search);
            }
        }
        break;

    case DALI_SPECIAL_VERIFY_SHORT:
        if (init_state != INIT_ENABLED) break;
        {
            uint8_t addr = dali_decode_short_address(data_byte);
            if (addr == ds.short_address) {
                dali_phy_send_backward(0xFF);
            }
        }
        break;

    case DALI_SPECIAL_QUERY_SHORT:
        if (init_state != INIT_ENABLED) break;
        {
            uint32_t random = ((uint32_t)random_h << 16) | ((uint32_t)random_m << 8) | random_l;
            uint32_t search = ((uint32_t)search_h << 16) | ((uint32_t)search_m << 8) | search_l;
            if (random == search) {
                if (ds.short_address == 0xFF) {
                    dali_phy_send_backward(0xFF);
                } else {
                    dali_phy_send_backward((ds.short_address << 1) | 1);
                }
            }
        }
        break;

    case DALI_SPECIAL_DTR1:
        ds.dtr1 = data_byte;
        break;

    case DALI_SPECIAL_DTR2:
        ds.dtr2 = data_byte;
        break;

    case DALI_SPECIAL_ENABLE_DT:
        ds.enabled_device_type = data_byte;
        break;

    default:
        break;
    }
}

/* IEC 62386-102 §9.6.3: The control gear shall leave the initialisation
 * state no later than 15 minutes after the last INITIALISE command.
 * This prevents the device from being stuck in addressing mode forever
 * if the master crashes or never sends TERMINATE. Called every main loop
 * iteration — returns immediately when not in init state (cheap check). */
void dali_addressing_check_timeout(void) {
    if (init_state != INIT_DISABLED) {
        if (millis() - init_start_time > DALI_INIT_TIMEOUT_MS) {
            init_state = INIT_DISABLED;
        }
    }
}

uint8_t dali_addressing_in_init(void) {
    return (init_state == INIT_ENABLED);
}

uint8_t dali_addressing_random_h(void) { return random_h; }
uint8_t dali_addressing_random_m(void) { return random_m; }
uint8_t dali_addressing_random_l(void) { return random_l; }

void dali_addressing_set_random(uint8_t h, uint8_t m, uint8_t l) {
    random_h = h;
    random_m = m;
    random_l = l;
}
