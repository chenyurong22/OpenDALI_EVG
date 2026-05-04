/*
    dali_dtr.h - DTR (Data Transfer Register) accessor helpers

    Centralizes bit extraction patterns for DTR0/DTR1/DTR2 values
    as defined by IEC 62386-102. These are used across multiple
    protocol sub-modules (protocol, addressing, DT8).
*/
#ifndef _DALI_DTR_H
#define _DALI_DTR_H

#include <stdint.h>
#include "dali_state.h"

/* Extract fade time from DTR0 (lower nibble, 0–15) */
static inline uint8_t dtr_fade_time(void) {
    return ds.dtr0 & 0x0F;
}

/* Extract fade rate from DTR0 (lower nibble, 1–15, 0 = reserved) */
static inline uint8_t dtr_fade_rate(void) {
    return ds.dtr0 & 0x0F;
}

/* Extract short address from DTR0 (bits 6:1 → 0–63) */
static inline uint8_t dtr_short_address(void) {
    return (ds.dtr0 >> 1) & 0x3F;
}

/* Extract short address from any data byte (same encoding as DTR) */
static inline uint8_t dali_decode_short_address(uint8_t data_byte) {
    return (data_byte >> 1) & 0x3F;
}

/* Extract 16-bit colour temperature from DTR1:DTR0 (mirek) */
static inline uint16_t dtr_colour_temp(void) {
    return ((uint16_t)ds.dtr1 << 8) | ds.dtr0;
}

/* Extract extended fade parameters from DTR0 */
static inline uint8_t dtr_ext_fade_base(void) {
    return ds.dtr0 & 0x0F;
}

static inline uint8_t dtr_ext_fade_mult(void) {
    return (ds.dtr0 >> 4) & 0x07;
}

#endif /* _DALI_DTR_H */
