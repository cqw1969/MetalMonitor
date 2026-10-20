#ifndef __APP_POTENTIONMETER_H
#define __APP_POTENTIONMETER_H

#include "stm32f10x.h"//或#include "stdint.h"

typedef struct
{
    uint32_t cycle;
    uint32_t timer;
    uint8_t  flag;
}Potentiometer_TaskInfo;

extern Potentiometer_TaskInfo potentiometer_task;

void Potentiometer_TaskReset(void);
void Potentiometer_TaskInit(uint32_t potentiometer_task_cycle);
void Potentiometer_Task(void);

#endif /* __APP_POTENTIONMETER_H  */
