#pragma once
#include <stdbool.h>

// 初始化串口：启动一个后台任务，读 stdin（= USB-Serial-JTAG）上的命令字节
void serial_init(void);

// 查询是否有新的"开始"命令（0x01）待处理；返回 true 并清除标志
bool serial_start_pending(void);

// 往串口发送"已按下"（字节 0x01），跟 BLE 通知一致
void serial_send_answer(void);
