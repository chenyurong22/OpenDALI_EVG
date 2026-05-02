/*
    eeprom.h - AT24C256 I2C EEPROM driver

    32 KB EEPROM on I2C1 (PC1=SDA, PC2=SCL), address 0x50.
    64-byte page write, 5 ms write cycle, 1 MHz I2C capable.

    Usage:
        eeprom_init();                          // once at startup
        eeprom_write(0x0000, data, 8);          // write up to 64 bytes
        eeprom_read(0x0000, buf, 8);            // sequential read
*/
#ifndef _EEPROM_H
#define _EEPROM_H

#include <stdint.h>

#define EEPROM_I2C_ADDR     0xA0    /* 0x50 << 1 */
#define EEPROM_PAGE_SIZE    64
#define EEPROM_SIZE         32768   /* 256 Kbit = 32 KB */

/* Initialize I2C1 peripheral for EEPROM access (PC1=SDA, PC2=SCL, 100 kHz) */
void eeprom_init(void);

/* Write up to 64 bytes. Must not cross a 64-byte page boundary.
 * Blocks for 5 ms write cycle after completion. */
void eeprom_write(uint16_t addr, const uint8_t *data, uint8_t len);

/* Sequential read of len bytes starting at addr. */
void eeprom_read(uint16_t addr, uint8_t *buf, uint8_t len);

#endif /* _EEPROM_H */
