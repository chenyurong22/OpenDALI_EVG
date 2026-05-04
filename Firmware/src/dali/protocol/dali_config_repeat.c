/*
    dali_config_repeat.c - Shared config repeat validation (IEC 62386-102)
*/

#include "dali_config_repeat.h"

static volatile uint8_t     last_addr_byte = 0;
static volatile uint8_t     last_command = 0;
static volatile uint32_t    last_command_time = 0;
static volatile uint8_t     config_repeat_pending = 0;

uint8_t dali_check_config_repeat(uint8_t addr, uint8_t cmd, uint32_t now) {
    if (config_repeat_pending && last_addr_byte == addr
        && last_command == cmd
        && (now - last_command_time) <= 100) {
        config_repeat_pending = 0;
        return 1;
    }
    last_addr_byte = addr;
    last_command = cmd;
    last_command_time = now;
    config_repeat_pending = 1;
    return 0;
}

void dali_config_repeat_reset(void) {
    config_repeat_pending = 0;
}
