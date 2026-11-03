#pragma once
#include <stdbool.h>

// 初始化并启动 BLE：广播名 Quizzer，服务 0xFFE0，CMD 0xFFE1（写），ANS 0xFFE2（通知）
void ble_init(void);

// 向已连接的主机发送"已按下"通知（值 0x01）
void ble_send_answer(void);

// -1=无命令，0=取消准备并熄灭抢答灯，1=准备并点亮抢答灯。
int ble_take_command(void);
