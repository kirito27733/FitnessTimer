#include <stdio.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_log.h"
#include "serial.h"

static const char *TAG = "serial";

// 是否有待处理的"开始"命令（0x01）
static volatile bool s_start_flag = false;

// 串口接收任务：读 stdin（= USB-Serial-JTAG），0x01 = "开始"命令
static void serial_rx_task(void *param)
{
    // stdin 改成无缓冲，getchar 才能逐字节读（不用等回车换行）
    setvbuf(stdin, NULL, _IONBF, 0);

    while (1) {
        int c = getchar();
        if (c == 0x01) {          // 0x01 = 开始（跟 BLE 命令一致）
            s_start_flag = true;
            ESP_LOGI(TAG, "串口收到开始命令");
        } else {
            // USB-Serial-JTAG 的 console 读是非阻塞的（没数据立刻返回 EOF），
            // 所以得歇一会儿再读，否则会满速空转把 CPU 占满、触发看门狗
            vTaskDelay(10 / portTICK_PERIOD_MS);
        }
    }
}

void serial_init(void)
{
    xTaskCreate(serial_rx_task, "serial_rx", 4096, NULL, 5, NULL);
    ESP_LOGI(TAG, "串口接收已启动");
}

bool serial_start_pending(void)
{
    bool v = s_start_flag;
    s_start_flag = false;
    return v;
}

void serial_send_answer(void)
{
    putchar(0x01);
    fflush(stdout);   // 立刻送出，不等缓冲
    ESP_LOGI(TAG, "串口已发送按下通知");
}
