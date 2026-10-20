/**
  ******************************************************************************
  * @file       app_potentiometer.c
  * @author     embedfire
  * @version     V1.0
  * @date        2024
  * @brief      电位器ADC采集应用层功能接口
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
#include "potentiometer/app_potentiometer.h"
#include "adc/bsp_adc.h"
#include "debug/bsp_debug.h"

Potentiometer_TaskInfo potentiometer_task = {0};

/**
 * @brief  POTENTIOMETER_ADC 计数复位
 * @param  无
 * @retval 无
 */
void Potentiometer_TaskReset(void)
{
    potentiometer_task.timer  = potentiometer_task.cycle;
    potentiometer_task.flag   = 0;
}

/**
 * @brief  POTENTIOMETER_ADC 任务初始化
 * @param  potentiometer_task_cycle: 任务轮询周期 单位ms(可修改系统节拍定时器)
 * @retval 无
 */
void Potentiometer_TaskInit(uint32_t potentiometer_task_cycle)
{
    potentiometer_task.cycle   = potentiometer_task_cycle;
    Potentiometer_TaskReset();
}

/**
 * @brief  POTENTIOMETER_ADC 任务  
 * @param  无
 * @retval 无
 */
void Potentiometer_Task(void)
{
    if(potentiometer_task.flag == 1)
    {
        float potentiometer_sig_convertedvalue = 0;//转化后的源始值的计算值
        potentiometer_sig_convertedvalue = (float)adc_source_convertedvalue[0]*3.3/4095;
        printf("\r\n The current AD value = %f V(0x%04X)\r\n",potentiometer_sig_convertedvalue,adc_source_convertedvalue[0]);
        Potentiometer_TaskReset();
    
    }
}
 
 /*****************************END OF FILE***************************************/
