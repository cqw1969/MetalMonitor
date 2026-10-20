#ifndef __BSP_ADC_H
#define __BSP_ADC_H

#include "stm32f10x.h"

// POTENTIOMETER_ADC_DATA

#define POTENTIOMETER_ADCX   			                ADC1                                 /* 外设序号 */
#define POTENTIOMETER_ADC_CLK_PORT 	                    RCC_APB2Periph_ADC1				     /* 外设时钟 */
#define POTENTIOMETER_ADC_APBXCLKCMD   			        RCC_APB2PeriphClockCmd
#define POTENTIOMETER_ADC_CHANNEL			            ADC_Channel_6	       		        /* 采样通道线 */

#define POTENTIOMETER_INT_ADC_IRQ			            ADC1_2_IRQn                         /* 外部中断向量号 */
#define POTENTIOMETER_INT_ADC_IRQHANDLER			    ADC1_2_IRQHandler                   /* 中断处理函数*/

extern __IO uint16_t adc_source_convertedvalue[20];//转化后的源始值

void ADCX_NVIC_Config(void);
void ADCX_Mode_Config(void);
void ADCX_Init(void);


#endif /* __BSP_ADC_H  */
