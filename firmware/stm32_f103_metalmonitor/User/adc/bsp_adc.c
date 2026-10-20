/**
  ******************************************************************************
  * @file       bsp_adc.c
  * @author     embedfire
  * @version     V1.0
  * @date        2024
  * @brief      ADC配置函数接口
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
#include "adc/bsp_adc.h"


__IO uint16_t adc_source_convertedvalue[20] = {0};//转化后的源始值

/**
  * @brief  配置POTENTIOMETER_ADC中断配置
  * @param  无
  * @retval 无
  */
void ADCX_NVIC_Config(void)
{
    /* 定义一个中断控制器结构体 */
    NVIC_InitTypeDef nvic_initstructure;
#if 1
    // 配置中断优先级
    nvic_initstructure.NVIC_IRQChannel = POTENTIOMETER_INT_ADC_IRQ;
    nvic_initstructure.NVIC_IRQChannelPreemptionPriority = 1;
    nvic_initstructure.NVIC_IRQChannelSubPriority = 0;
    nvic_initstructure.NVIC_IRQChannelCmd = ENABLE;
    NVIC_Init(&nvic_initstructure);

#endif    
}

/**
 * @brief  配置ADCX模式
 * @param  无
 * @retval 无
 */
void ADCX_Mode_Config(void)
{
  
    /* 定义一个ADC结构体 */
    ADC_InitTypeDef adc_initstruct = {0};
#if 1  
    /* 开启ADC相关的GPIO外设/端口时钟 */
    POTENTIOMETER_ADC_APBXCLKCMD(POTENTIOMETER_ADC_CLK_PORT,ENABLE);
    
    /* ADC 模式配置 */
    adc_initstruct.ADC_Mode = ADC_Mode_Independent;                     //只有一个ADC,属于独立模式
    adc_initstruct.ADC_ScanConvMode = DISABLE;                          //禁止扫描模式，单通道不需要
    adc_initstruct.ADC_ContinuousConvMode =  ENABLE;                    //使能连续扫描模式
    adc_initstruct.ADC_ExternalTrigConv = ADC_ExternalTrigConv_None;    //不需要外部触发转换,使用软件开启
    adc_initstruct.ADC_DataAlign = ADC_DataAlign_Right;                 //转换结构右对齐
    adc_initstruct.ADC_NbrOfChannel = 1;
    
    ADC_Init(POTENTIOMETER_ADCX,&adc_initstruct);                       //初始化ADC  
#endif
    
    /* ADC的转化配置 */
    RCC_ADCCLKConfig(RCC_PCLK2_Div8);                                   //配置ADC的时钟为PLL2的8分频,9MHz
    ADC_RegularChannelConfig(POTENTIOMETER_ADCX,POTENTIOMETER_ADC_CHANNEL,1,ADC_SampleTime_55Cycles5);    //配置ADC通道的采样顺序和时间
    ADC_ITConfig(POTENTIOMETER_ADCX,ADC_IT_EOC,ENABLE);                 //配置转换结束中断，用于读取转换值
    ADC_Cmd(POTENTIOMETER_ADCX,ENABLE);                                 //开启ADC转换
    
    ADC_ResetCalibration(POTENTIOMETER_ADCX);                   //选择需要校准的ADC初始化
    while(ADC_GetResetCalibrationStatus(POTENTIOMETER_ADCX));   //等待校准初始化完成
    ADC_StartCalibration(POTENTIOMETER_ADCX);                   //开始校准
    while(ADC_GetCalibrationStatus(POTENTIOMETER_ADCX));        //等待校准完成
    
    ADC_SoftwareStartConvCmd(POTENTIOMETER_ADCX,ENABLE);  //由于没有采用外部触发,所以配置软件触发ADC转换
}

/** 
 * @brief  ADCX初始化
 * @param  无
 * @retval 无
 */
void ADCX_Init(void)
{
    /* 配置ADCX模式 */
    ADCX_Mode_Config();

    /* 对应的GPIO的配置 */
    POTENTIOMETER_GPIO_Config();
    
    /* 配置对应的中断 */
    ADCX_NVIC_Config();
    
}
/*****************************END OF FILE***************************************/
