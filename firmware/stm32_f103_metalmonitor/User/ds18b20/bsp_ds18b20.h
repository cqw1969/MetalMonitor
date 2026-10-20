#ifndef __BSP_DS18B20_H
#define __BSP_DS18B20_H

#include "stm32f10x.h"

/* DQ 引脚定义：PA7 */
#define DS18B20_PORT     GPIOA
#define DS18B20_PIN      GPIO_Pin_7
#define DS18B20_CLK      RCC_APB2Periph_GPIOA

/* DQ 方向切换（推挽输出 / 浮空输入）*/
#define DS18B20_DQ_OUT()  DS18B20_GPIO_Mode(GPIO_Mode_Out_OD)//原PP
#define DS18B20_DQ_IN()   DS18B20_GPIO_Mode(GPIO_Mode_IN_FLOATING)

/* DQ 读写 */
#define DS18B20_DQ_HIGH() GPIO_SetBits(DS18B20_PORT, DS18B20_PIN)
#define DS18B20_DQ_LOW()  GPIO_ResetBits(DS18B20_PORT, DS18B20_PIN)
#define DS18B20_DQ_READ() GPIO_ReadInputDataBit(DS18B20_PORT, DS18B20_PIN)

/* 对外函数 */
uint8_t  DS18B20_Init(void);          /* 初始化，返回 1=存在 0=不存在 */
//float    DS18B20_GetTemp(void);       /* 读取温度（℃） */
uint8_t  DS18B20_StartConvert(void);
float DS18B20_ReadResult(void);

#endif
