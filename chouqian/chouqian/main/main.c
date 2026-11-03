#include <stdio.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "driver/gpio.h"
#include "ble.h"
#include "serial.h"

#define BUTTON_GPIO 19   // 实体按钮开关接 IO19，保留本项目硬件接线
#define LED_GPIO    3   // LED1状态灯：按本板esp-light电量灯映射，GPIO3高电平点亮
#define RELAY_GPIO 23   // 继电器 DO3：驱动 5V 抢答灯（高电平吸合）

// 三个状态：待机 / 运行 / 已抢答
typedef enum {
    STATE_IDLE,      // 待机：灯灭，等手机发"开始"
    STATE_RUNNING,   // 运行：灯亮，等按下开关
    STATE_ANSWERED,  // 已抢答：灯灭，已通知手机
} state_t;

void app_main(void)
{
    printf("Firmware: chouqian-LED1-GPIO3-v1 | button=GPIO19 | status=GPIO3 active-high | lamp=GPIO23\n");
    // 配置按钮 IO19：输入 + 内部上拉（按下读到 0）
    gpio_config_t btn = {
        .pin_bit_mask = (1ULL << BUTTON_GPIO),
        .mode = GPIO_MODE_INPUT,
        .pull_up_en = 1,
        .pull_down_en = 0,
        .intr_type = GPIO_INTR_DISABLE,
    };
    gpio_config(&btn);

    // 配置 LED1 IO3：输出，高电平点亮
    gpio_config_t led = {
        .pin_bit_mask = (1ULL << LED_GPIO),
        .mode = GPIO_MODE_OUTPUT,
        .pull_up_en = 0,
        .pull_down_en = 0,
        .intr_type = GPIO_INTR_DISABLE,
    };
    gpio_config(&led);

    
    // 配置继电器 DO3（IO23）：输出，跟抢答逻辑联动
    gpio_config_t relay = {
        .pin_bit_mask = (1ULL << RELAY_GPIO),
        .mode = GPIO_MODE_OUTPUT,
        .pull_up_en = 0,
        .pull_down_en = 0,
        .intr_type = GPIO_INTR_DISABLE,
    };
    gpio_config(&relay);
    gpio_set_level(RELAY_GPIO, 0);   // 初始断开（灯灭，待机状态）

    // 开机自检：LED1(IO3)与继电器灯亮300ms、灭300ms，共三次
    for (int i = 0; i < 3; i++) {
        gpio_set_level(LED_GPIO, 1);
        gpio_set_level(RELAY_GPIO, 1);   // 初始断开（灯灭，待机状态）
        vTaskDelay(300 / portTICK_PERIOD_MS);
        gpio_set_level(LED_GPIO, 0);
        gpio_set_level(RELAY_GPIO, 0);   // 初始断开（灯灭，待机状态）
        vTaskDelay(300 / portTICK_PERIOD_MS);
    }
    gpio_set_level(LED_GPIO, 1);   // 闪完停在"亮"，表示待机


    ble_init();                    // 启动蓝牙，开始广播
    serial_init();                 // 启动串口接收（读 USB 上的命令字节）

    state_t state = STATE_IDLE;
    int last_button = 1 ;           // 上次按钮状态，1=没按

    printf("抢答器启动，等待手机连接...\n");
    gpio_set_level(RELAY_GPIO, 0);   // 初始断开（灯灭，待机状态）

    while (1) {
        // BLE：01准备、00取消准备；原有串口01准备保持兼容。
        int command = ble_take_command();
        if (serial_start_pending()) command = 1;
        if (command == 0) {
            state = STATE_IDLE;
            gpio_set_level(RELAY_GPIO, 0);
            gpio_set_level(LED_GPIO, 1);
            printf("取消准备，灯灭\n");
        } else if (command == 1) {
            state = STATE_RUNNING;
            gpio_set_level(RELAY_GPIO, 1);   // 继电器吸合，5V 灯亮
            gpio_set_level(LED_GPIO, 0);     // LED1(IO3)灭（运行中）
            printf("收到开始命令，灯亮\n");
        }

        // 2) 检测按钮下降沿：上次没按(1) 且 现在按下(0)
        int btn_now = gpio_get_level(BUTTON_GPIO);
        if (btn_now == 0 && last_button == 1) {
            if (state == STATE_RUNNING) {          // 只有"运行"中按才算抢答
                state = STATE_ANSWERED;
                gpio_set_level(RELAY_GPIO, 0);     // 继电器断开，5V 灯灭
                gpio_set_level(LED_GPIO, 1);       // LED1(IO3)亮（待机）
                ble_send_answer();                 // 通知手机（BLE）"已按下"
                serial_send_answer();              // 通知网页（串口）"已按下"
                printf("按下！已抢答，灯灭，已通知主机\n");
            }
            vTaskDelay(200 / portTICK_PERIOD_MS);  // 防抖：按一下只算一次
        }
        last_button = btn_now;

        vTaskDelay(10 / portTICK_PERIOD_MS);       // 每 10ms 轮询一次
    }
}

