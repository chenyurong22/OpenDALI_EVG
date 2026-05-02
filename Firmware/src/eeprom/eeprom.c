/*
    eeprom.c - AT24C256 I2C EEPROM driver for CH32V003

    Uses the hardware I2C1 peripheral (PC1=SDA, PC2=SCL).
    100 kHz standard mode at 48 MHz PCLK.
*/

#include "ch32fun.h"
#include "eeprom.h"

/* Delay helper (SysTick counts up at HCLK) */
static inline void ee_delay(uint32_t cycles) {
    uint32_t start = SysTick->CNT;
    while ((uint32_t)(SysTick->CNT - start) < cycles);
}

void eeprom_init(void) {
    /* Enable I2C1 + GPIOC clocks */
    RCC->APB1PCENR |= RCC_APB1Periph_I2C1;

    /* PC1 = SDA, PC2 = SCL: AF open-drain, 10 MHz
     * CFGLR bits [7:4] = PC1, [11:8] = PC2
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

void eeprom_write(uint16_t addr, const uint8_t *data, uint8_t len) {
    /* START */
    I2C1->CTLR1 |= I2C_CTLR1_START;
    while (!(I2C1->STAR1 & I2C_STAR1_SB));

    /* Device address (write) */
    I2C1->DATAR = EEPROM_I2C_ADDR;
    while (!(I2C1->STAR1 & I2C_STAR1_ADDR));
    (void)I2C1->STAR2;

    /* Memory address (16-bit, MSB first) */
    I2C1->DATAR = addr >> 8;
    while (!(I2C1->STAR1 & I2C_STAR1_TXE));
    I2C1->DATAR = addr & 0xFF;
    while (!(I2C1->STAR1 & I2C_STAR1_TXE));

    /* Data bytes */
    for (uint8_t i = 0; i < len; i++) {
        I2C1->DATAR = data[i];
        while (!(I2C1->STAR1 & I2C_STAR1_TXE));
    }

    /* Wait for last byte + STOP */
    while (!(I2C1->STAR1 & I2C_STAR1_BTF));
    I2C1->CTLR1 |= I2C_CTLR1_STOP;

    /* AT24C256 write cycle: 5 ms max */
    ee_delay(48000 * 5);    /* 5 ms at 48 MHz */
}

void eeprom_read(uint16_t addr, uint8_t *buf, uint8_t len) {
    /* START + set memory address (write phase) */
    I2C1->CTLR1 |= I2C_CTLR1_START;
    while (!(I2C1->STAR1 & I2C_STAR1_SB));
    I2C1->DATAR = EEPROM_I2C_ADDR;
    while (!(I2C1->STAR1 & I2C_STAR1_ADDR));
    (void)I2C1->STAR2;

    I2C1->DATAR = addr >> 8;
    while (!(I2C1->STAR1 & I2C_STAR1_TXE));
    I2C1->DATAR = addr & 0xFF;
    while (!(I2C1->STAR1 & I2C_STAR1_BTF));

    /* Repeated START + read phase */
    I2C1->CTLR1 |= I2C_CTLR1_START;
    while (!(I2C1->STAR1 & I2C_STAR1_SB));
    I2C1->DATAR = EEPROM_I2C_ADDR | 1;
    I2C1->CTLR1 |= I2C_CTLR1_ACK;
    while (!(I2C1->STAR1 & I2C_STAR1_ADDR));
    (void)I2C1->STAR2;

    for (uint8_t i = 0; i < len; i++) {
        if (i == len - 1)
            I2C1->CTLR1 &= ~I2C_CTLR1_ACK;    /* NACK last byte */
        while (!(I2C1->STAR1 & I2C_STAR1_RXNE));
        buf[i] = I2C1->DATAR;
    }

    I2C1->CTLR1 |= I2C_CTLR1_STOP;
}
