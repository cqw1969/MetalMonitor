#include "ds18b20/bsp_ds18b20.h"

/* DWT 微秒延时（比 SysTick 更精确，适合 us 级短延时）*/
static void Delay_us(uint32_t us)
{
    uint32_t ticks;
    uint32_t told, tnow, tcnt = 0;
    uint32_t reload = SysTick->LOAD;
    
    ticks = us * (SystemCoreClock / 1000000);  /* 需要的节拍数 SystemCoreClock=72000000 */
    told = SysTick->VAL;
    
    while (1)
    {
        tnow = SysTick->VAL;
        if (tnow != told)
        {
            if (tnow < told) tcnt += told - tnow;
            else            tcnt += reload - tnow + told;
            told = tnow;
            if (tcnt >= ticks) break;
        }
    }
}

/* 切换 DQ 引脚方向 */
static void DS18B20_GPIO_Mode(GPIOMode_TypeDef mode)
{
    GPIO_InitTypeDef GPIO_InitStructure;
    GPIO_InitStructure.GPIO_Pin   = DS18B20_PIN;
    GPIO_InitStructure.GPIO_Speed = GPIO_Speed_50MHz;
    GPIO_InitStructure.GPIO_Mode  = mode;
    GPIO_Init(DS18B20_PORT, &GPIO_InitStructure);
}

/* 复位 DS18B20，返回 1=检测到存在脉冲 */
uint8_t DS18B20_Init(void)
{
    uint8_t presence;
    
    RCC_APB2PeriphClockCmd(DS18B20_CLK, ENABLE);
    
    DS18B20_DQ_OUT();       /* 配置为输出 */
    DS18B20_DQ_LOW();       /* 拉低总线 */
    Delay_us(750);          /* 保持 750us 复位脉冲 */
    DS18B20_DQ_HIGH();      /* 释放总线 */
    Delay_us(15);           /* 等待 15~60us */
    
    DS18B20_DQ_IN();        /* 切换为输入，读存在脉冲 */
    Delay_us(60);
    presence = (DS18B20_DQ_READ() == 0) ? 1 : 0;  /* 低电平=存在 */
    Delay_us(500);          /* 等待复位时序结束 */
    
    return presence;
}

/* 写一个字节（先写低位）*/
static void DS18B20_WriteByte(uint8_t byte)
{
    uint8_t i;
    DS18B20_DQ_OUT();
    for (i = 0; i < 8; i++)
    {
        DS18B20_DQ_LOW();
        Delay_us(2);                 /* 拉低 2us 启动写时序 */
        if (byte & 0x01) DS18B20_DQ_HIGH();  /* 写 1 */
        else             DS18B20_DQ_LOW();   /* 写 0 */
        Delay_us(60);                /* 保持 60us */
        DS18B20_DQ_HIGH();           /* 释放总线 */
        byte >>= 1;
    }
}

/* 读一个字节（先读低位）*/
static uint8_t DS18B20_ReadByte(void)
{
    uint8_t i, byte = 0;
    for (i = 0; i < 8; i++)
    {
        DS18B20_DQ_OUT();
        DS18B20_DQ_LOW();
        Delay_us(2);                 /* 拉低 2us 启动读时序 */
        DS18B20_DQ_HIGH();           /* 释放总线 */
        Delay_us(2);                 /* 等 2us 再采样 */
        
        DS18B20_DQ_IN();
        if (DS18B20_DQ_READ()) byte |= (1 << i);
        Delay_us(60);
    }
    return byte;
}

/* 读取温度，返回 ℃ */
//float DS18B20_GetTemp(void)
//{
//    uint8_t temp_l, temp_h;
//    int16_t temp_raw;
//    float temp;
//    
//    DS18B20_Init();                          /* 复位 */
//    DS18B20_WriteByte(0xCC);                 /* 跳过 ROM（单设备）*/
//    DS18B20_WriteByte(0x44);                 /* 启动温度转换 */
//    Delay_us(750000);                        /* 等待转换完成（12位最长750ms）*/

//    DS18B20_Init();                          /* 再次复位 */
//    DS18B20_WriteByte(0xCC);                 /* 跳过 ROM */
//    DS18B20_WriteByte(0xBE);                 /* 读暂存器 */
//    
//    temp_l = DS18B20_ReadByte();             /* 温度低字节 */
//    temp_h = DS18B20_ReadByte();             /* 温度高字节 */
//    
//    temp_raw = (temp_h << 8) | temp_l;       /* 组合 16 位 */
//    
//    /* 12 位分辨率：1 LSB = 0.0625℃ */
//    temp = (float)temp_raw * 0.0625f;
//    
//    return temp;
//}


/* 下令：开始测温，发完立即返回（约1ms），芯片自己测750ms */
uint8_t DS18B20_StartConvert(void)
{
    uint8_t ok = DS18B20_Init();
    DS18B20_WriteByte(0xCC);
    DS18B20_WriteByte(0x44);
    return ok;
}

/* 收粮：把早已测好的结果读回来（约1ms）*/
float DS18B20_ReadResult(void)
{
    uint8_t temp_l, temp_h;
    int16_t temp_raw;
    DS18B20_Init();
    DS18B20_WriteByte(0xCC);
    DS18B20_WriteByte(0xBE);
    temp_l = DS18B20_ReadByte();
    temp_h = DS18B20_ReadByte();
    temp_raw = (temp_h << 8) | temp_l;
    return (float)temp_raw * 0.0625f;
}
