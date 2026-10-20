/**
  ******************************************************************************
  * @file       bsp_gpio_potentiometer.c
  * @author     embedfire
  * @version     V1.0
  * @date        2024
  * @brief      电位器函数接口
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
  
#include "potentiometer/bsp_gpio_potentiometer.h"


/**
 * @brief  初始化控制 电位器 的IO
 * @param  无
 * @retval 无
 */
void POTENTIOMETER_GPIO_Config(void)
{
    /* 定义一个GPIO结构体 */
    GPIO_InitTypeDef gpio_initstruct = {0};

#if 1    
  
    /* 开启POTENTIOMETER相关的GPIO外设/端口时钟 */
    RCC_APB2PeriphClockCmd(POTENTIOMETER_SIG_GPIO_CLK_PORT,ENABLE);

    /*选择要控制的GPIO引脚、设置GPIO模式为模拟输入、设置GPIO速率为50MHz*/
    gpio_initstruct.GPIO_Mode   = GPIO_Mode_AIN;
    gpio_initstruct.GPIO_Pin    = POTENTIOMETER_SIG_GPIO_PIN;
    gpio_initstruct.GPIO_Speed  = GPIO_Speed_50MHz;
    GPIO_Init(POTENTIOMETER_SIG_GPIO_PORT,&gpio_initstruct);
#endif    
   
}

/*****************************END OF FILE***************************************/
