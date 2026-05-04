/*
    eeprom.c - AT24C256 I2C EEPROM driver for CH32V003

    Uses the hardware I2C1 peripheral (PC1=SDA, PC2=SCL).
    100 kHz standard mode at 48 MHz PCLK.
    All I2C waits have millis()-based timeouts to prevent hangs.
    (SysTick runs in auto-reload mode, so raw cycle counting is unusable.)
*/

#include "ch32fun.h"
#include "eeprom.h"
#include <stdio.h>

/* millis() provided by main.c */
extern uint32_t millis(void);

#define I2C_TIMEOUT_MS  20

/* Wait for an I2C status flag with timeout. Returns 1 on success, 0 on timeout. */
static int i2c_wait(volatile uint16_t *reg, uint16_t mask) {
    uint32_t t = millis();
    while (!(*reg & mask)) {
        if (millis() - t > I2C_TIMEOUT_MS) return 0;
    }
    return 1;
}

/* Reset I2C peripheral after a bus error */
static void i2c_reset(void) {
    I2C1->CTLR1 |= I2C_CTLR1_STOP;
    Delay_Ms(1);
    I2C1->CTLR1 |= I2C_CTLR1_SWRST;
    Delay_Ms(1);
    I2C1->CTLR1 = 0;

    /* Re-init */
    I2C1->CTLR2 = 48;
    I2C1->CKCFGR = 240;
    I2C1->CTLR1 = I2C_CTLR1_PE;
}

void eeprom_init(void) {
    /* Enable I2C1 clock */
    RCC->APB1PCENR |= RCC_APB1Periph_I2C1;

    /* Reset I2C peripheral for clean state */
    RCC->APB1PRSTR |= RCC_APB1Periph_I2C1;
    RCC->APB1PRSTR &= ~RCC_APB1Periph_I2C1;

    /* PC1 = SDA, PC2 = SCL: AF open-drain, 10 MHz
     * 0xD = CNF=11 (AF open-drain) + MODE=01 (10 MHz) */
    GPIOC->CFGLR &= ~(0xFFu << 4);
    GPIOC->CFGLR |= (0xDDu << 4);

    /* I2C1 clock config: 100 kHz at 48 MHz PCLK
     * CTLR2 FREQ = PCLK in MHz = 48
     * CCR = PCLK / (2 * 100000) = 240 */
    I2C1->CTLR2 = 48;
    I2C1->CKCFGR = 240;
    I2C1->CTLR1 = I2C_CTLR1_PE;
}

int eeprom_write(uint16_t addr, const uint8_t *data, uint8_t len) {
    /* START */
    I2C1->CTLR1 |= I2C_CTLR1_START;
    if (!i2c_wait(&I2C1->STAR1, I2C_STAR1_SB)) { i2c_reset(); return 0; }

    /* Device address (write) */
    I2C1->DATAR = EEPROM_I2C_ADDR;
    if (!i2c_wait(&I2C1->STAR1, I2C_STAR1_ADDR)) { i2c_reset(); return 0; }
    (void)I2C1->STAR2;

    /* Memory address (16-bit, MSB first) */
    I2C1->DATAR = addr >> 8;
    if (!i2c_wait(&I2C1->STAR1, I2C_STAR1_TXE)) { i2c_reset(); return 0; }
    I2C1->DATAR = addr & 0xFF;
    if (!i2c_wait(&I2C1->STAR1, I2C_STAR1_TXE)) { i2c_reset(); return 0; }

    /* Data bytes */
    for (uint8_t i = 0; i < len; i++) {
        I2C1->DATAR = data[i];
        if (!i2c_wait(&I2C1->STAR1, I2C_STAR1_TXE)) { i2c_reset(); return 0; }
    }

    /* Wait for last byte + STOP */
    if (!i2c_wait(&I2C1->STAR1, I2C_STAR1_BTF)) { i2c_reset(); return 0; }
    I2C1->CTLR1 |= I2C_CTLR1_STOP;

    Delay_Ms(5);    /* AT24C256 write cycle: 5 ms max */
    return 1;
}

int eeprom_read(uint16_t addr, uint8_t *buf, uint8_t len) {
    /* START + set memory address (write phase) */
    I2C1->CTLR1 |= I2C_CTLR1_START;
    if (!i2c_wait(&I2C1->STAR1, I2C_STAR1_SB)) { i2c_reset(); return 0; }
    I2C1->DATAR = EEPROM_I2C_ADDR;
    if (!i2c_wait(&I2C1->STAR1, I2C_STAR1_ADDR)) { i2c_reset(); return 0; }
    (void)I2C1->STAR2;

    I2C1->DATAR = addr >> 8;
    if (!i2c_wait(&I2C1->STAR1, I2C_STAR1_TXE)) { i2c_reset(); return 0; }
    I2C1->DATAR = addr & 0xFF;
    if (!i2c_wait(&I2C1->STAR1, I2C_STAR1_BTF)) { i2c_reset(); return 0; }

    /* Repeated START + read phase */
    I2C1->CTLR1 |= I2C_CTLR1_START;
    if (!i2c_wait(&I2C1->STAR1, I2C_STAR1_SB)) { i2c_reset(); return 0; }
    I2C1->DATAR = EEPROM_I2C_ADDR | 1;
    I2C1->CTLR1 |= I2C_CTLR1_ACK;
    if (!i2c_wait(&I2C1->STAR1, I2C_STAR1_ADDR)) { i2c_reset(); return 0; }
    (void)I2C1->STAR2;

    for (uint8_t i = 0; i < len; i++) {
        if (i == len - 1)
            I2C1->CTLR1 &= ~I2C_CTLR1_ACK;    /* NACK last byte */
        if (!i2c_wait(&I2C1->STAR1, I2C_STAR1_RXNE)) { i2c_reset(); return 0; }
        buf[i] = I2C1->DATAR;
    }

    I2C1->CTLR1 |= I2C_CTLR1_STOP;
    return 1;
}
