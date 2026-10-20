/**
  ******************************************************************************
  * @file       main.c
  * @author     embedfire
  * @version     V1.0
  * @date        2024
  * @brief      ADC单通道中断规则采集电位器
  ******************************************************************************
  * @attention
  *
  * 实验平台  ：野火 STM32F103C8T6-STM32开发板 
  * 论坛      ：http://www.firebbs.cn
  * 官网      ：https://embedfire.com/
  * 淘宝      ：https://yehuosm.tmall.com/
  *
  ******************************************************************************
  */

#include "main.h"
#include "stm32f10x.h"
#include "debug/bsp_debug.h"
#include "dwt/bsp_dwt.h" 
#include "systick/bsp_systick.h"
#include "adc/bsp_adc.h"
#include "ds18b20/bsp_ds18b20.h"
#include "crc16/bsp_crc16.h"

/**
  * @brief  主函数
  * @param  无 
  * @note   无
  * @retval 无
  */
int main(void)
{
    NVIC_PriorityGroupConfig(NVIC_PriorityGroup_4);
    DEBUG_USART_Init();
    SysTick_Init();
    ADCX_Init();
    
    //Potentiometer_TaskInit(1000);
    Temperature_TaskInit( 1000 );
    //printf ( "\r\n CRC test = %04X (expect 4B37)" , CRC16_Modbus(( uint8_t *) "123456789" , 9 ));
    printf("\r\n ----ds18b20传感器实验开始---\r\n");
    
    while(1)
    {
        /* POTENTIOMETER_ADC 任务 */
//        Potentiometer_Task();
        
       Temperature_Task();

    }
        
}

