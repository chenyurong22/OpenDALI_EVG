/*
    logger.h - Debug logging macros

    Standardized prefixes for UART debug output (PD5, 115200 baud).
    All macros append a newline automatically.
*/
#ifndef _LOGGER_H
#define _LOGGER_H

#include <stdio.h>

#define LOG_NVM(fmt, ...)       printf("[NVM] " fmt "\n", ##__VA_ARGS__)
#define LOG_PHY(fmt, ...)       printf("[PHY] " fmt "\n", ##__VA_ARGS__)
#define LOG_CMD(fmt, ...)       printf("[CMD] " fmt "\n", ##__VA_ARGS__)
#define LOG_ERR(fmt, ...)       printf("[ERR] " fmt "\n", ##__VA_ARGS__)
#define LOG_BOOT(fmt, ...)      printf("[BOOT] " fmt "\n", ##__VA_ARGS__)

#endif /* _LOGGER_H */
