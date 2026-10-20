/* app_temperature.h */
#ifndef __APP_TEMPERATURE_H
#define __APP_TEMPERATURE_H

#include "stm32f10x.h"//或#include "stdint.h"

/* 节点地址：帧格式的固定字段，见 docs/protocol/PROTOCOL.md §2.1
 * 0x00 = 单节点/未分配（当前值）；0x01~0xFE = 多节点地址；0xFF = 保留
 * 与 SEQ 不同，本字段已生效：固件固定发送，上位机固定按偏移 2 解析 */
#define DEVICE_NODE_ID   0x00

typedef struct
{
    uint32_t cycle;
    uint32_t timer;
    uint8_t  flag;
    uint8_t status;
    float last_temp;    //最近一次温度
}Temperature_TaskInfo;

extern Temperature_TaskInfo temperature_task;

void Temperature_TaskReset(void);
void Temperature_TaskInit(uint32_t Temperature_task_cycle);
void Temperature_Task(void);
float Temperature_Get(void);

#endif /* __APP_TEMPERATURE_H  */
