/* app_temperature.c */
#include "temperature/app_temperature.h"
#include "ds18b20/bsp_ds18b20.h"
#include "debug/bsp_debug.h"
#include "usart/usart_com.h"
#include "crc16/bsp_crc16.h"
#include "adc/bsp_adc.h"

Temperature_TaskInfo temperature_task = {0};

/**
 * @brief  Temperature_ADC 计数复位
 * @param  无
 * @retval 无
 */
void Temperature_TaskReset(void)
{
    temperature_task.timer  = temperature_task.cycle;
    temperature_task.flag   = 0;
}

/**
 * @brief  Temperature_ADC 任务初始化
 * @param  temperature_task_cycle: 任务轮询周期 单位ms(可修改系统节拍定时器)
 * @retval 无
 */
void Temperature_TaskInit(uint32_t cycle_ms)
{
    temperature_task.cycle   = cycle_ms;
    Temperature_TaskReset();
    temperature_task.status = DS18B20_StartConvert(); //开始读
}

/**
 * @brief  Temperature_ADC 任务  
 * @param  无
 * @retval 无
 */
void Temperature_Task(void)
{
    if(temperature_task.flag == 1)
    {
        temperature_task.last_temp = DS18B20_ReadResult(); /* 收1秒前的返回的数据 */
        DS18B20_StartConvert(); /* 立刻下令测下一次 */
        
        //printf ( "\r\n Temperature = %.2f C" , temperature_task.last_temp);
        /*convert_start*/
        uint8_t frame[12];
        uint16_t crc;
        int16_t temp_x10 = (int16_t)(temperature_task.last_temp * 10.0f + 0.5f);//此处0.5为四舍五入修正
        //ADC 的满量程电压 = 3.3V = 3300mV 
        //STM32 是 12 位 ADC ，2¹²−1 = 4095
        uint16_t mv = (uint16_t)((uint32_t)adc_source_convertedvalue[0] * 3300 / 4095);
        
        frame[0] = 0xAA; frame[1] = 0x55;
        frame[2] = DEVICE_NODE_ID; frame[3] = 6; frame[4] = 0x01;
        frame[5] = (uint8_t)(temp_x10 & 0xFF);
        frame[6] = (uint8_t)(temp_x10 >> 8);
        frame[7] = (uint8_t)(mv & 0xFF);
        frame[8] = (uint8_t)(mv >> 8);
        frame[9] = temperature_task.status;
        crc = CRC16_Modbus(&frame[2], 8);      /* 覆盖 NODE_ID(1) + LEN(1) + CMD(1) + 温度(2) + mV(2) + 状态(1) = 8 字节 */
        frame[10]  = (uint8_t)(crc & 0xFF);
        frame[11] = (uint8_t)(crc >> 8);
        //printf("\r\n");
        {
            uint8_t i;
            for (i = 0; i < 12; i++)
                USARTX_SendByte(DEBUG_USARTX, frame[i]);
        }
        //printf("\r\n");
        /*convert_end*/
        
        Temperature_TaskReset();
    
    }
}

float Temperature_Get(void){
    return temperature_task.last_temp;
}
 
 /*****************************END OF FILE***************************************/
