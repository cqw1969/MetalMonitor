/* bsp_crc16.h */
#ifndef __BSP_CRC16_H
#define __BSP_CRC16_H
#include "stm32f10x.h"
uint16_t CRC16_Modbus(const uint8_t *data, uint16_t len);
#endif
