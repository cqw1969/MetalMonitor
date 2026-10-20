#ifndef __BSP_GPIO_POTENTIOMETER_H
#define __BSP_GPIO_POTENTIOMETER_H

#include "stm32f10x.h"
#include <stdio.h>

/* 定义 POTENTIOMETER 连接的GPIO端口, 用户只需要修改下面的代码即可改变控制的IO引脚 */
// POTENTIOMETER SIG

#define POTENTIOMETER_SIG_GPIO_PORT    			        GPIOA			                /* GPIO端口 */
#define POTENTIOMETER_SIG_GPIO_CLK_PORT 	            RCC_APB2Periph_GPIOA			/* GPIO端口时钟 */
#define POTENTIOMETER_SIG_GPIO_PIN			            GPIO_Pin_6	       				/* 连接到GPIO */

void POTENTIOMETER_GPIO_Config(void);

#endif /* __BSP_GPIO_POTENTIOMETER_H  */
