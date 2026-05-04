/*
    dali_config_repeat.h - Shared config repeat validation (IEC 62386-102)

    IEC 62386-102 requires certain commands (32–128, INITIALISE, RANDOMISE)
    to be sent twice within 100 ms to take effect. This module provides
    a single shared implementation used by both dali_protocol.c and
    dali_addressing.c.
*/
#ifndef _DALI_CONFIG_REPEAT_H
#define _DALI_CONFIG_REPEAT_H

#include <stdint.h>

/* Check if a command is the valid second instance of a config repeat pair.
 * Returns 1 if this is the confirming repeat (execute the command).
 * Returns 0 if this is the first instance (store and wait for repeat).
 *
 * addr: address byte of the frame
 * cmd:  command/data byte of the frame
 * now:  current millis() timestamp
 */
uint8_t dali_check_config_repeat(uint8_t addr, uint8_t cmd, uint32_t now);

/* Reset the config repeat state (e.g. after TERMINATE). */
void dali_config_repeat_reset(void);

#endif /* _DALI_CONFIG_REPEAT_H */
