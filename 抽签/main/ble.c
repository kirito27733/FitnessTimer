#include <string.h>
#include <stdio.h>
#include <stdatomic.h>
#include "esp_log.h"
#include "nvs_flash.h"
#include "esp_mac.h"
#include "nimble/nimble_port.h"
#include "nimble/nimble_port_freertos.h"
#include "host/ble_hs.h"
#include "host/ble_store.h"
#include "host/util/util.h"
#include "os/os_mbuf.h"
#include "services/gap/ble_svc_gap.h"
#include "services/gatt/ble_svc_gatt.h"
#include "ble.h"

// ble_store_config_init 在 ESP-IDF 5.5.5 没有公开头文件声明（官方 bleprph
// 例子也是手动前向声明的），这里补上，让 NimBLE 用 NVS 存密钥。
void ble_store_config_init(void);

static const char *TAG = "ble";

// 状态：是否有待处理的"开始"命令
static atomic_int s_pending_command = -1;
// ANS 特征值的值句柄（通知时用）
static uint16_t s_ans_handle = 0; 
// 当前连接的句柄（通知时用）
static uint16_t s_conn_handle = 0;
// 是否已连接（不能用 conn_handle==0 判断，因为首次连接句柄就是 0）
static bool s_connected = false;

// 设备名（蓝牙 MAC 后两字节拼进去，多台板子靠名字区分，如 Quizzer-3F5A）
static char s_dev_name[16];

// 服务/特征值 UUID（16-bit 自定义）
static const ble_uuid16_t s_svc_uuid = BLE_UUID16_INIT(0xFFE0);
static const ble_uuid16_t s_cmd_uuid = BLE_UUID16_INIT(0xFFE1);
static const ble_uuid16_t s_ans_uuid = BLE_UUID16_INIT(0xFFE2);
static const ble_uuid16_t s_clr_uuid = BLE_UUID16_INIT(0xFFE3);
static const ble_uuid16_t s_heartbeat_uuid = BLE_UUID16_INIT(0xFFE4);
// 独立心跳：版本1 + 最近收到的4字节序号，不改变抢答/灯光状态。
static uint8_t s_heartbeat_reply[5] = {2, 0, 0, 0, 0};

// 特征值标识：通过 access_cb 的 arg 区分是 CMD 还是 CLR
#define CHAR_CMD 1
#define CHAR_CLR 2
#define CHAR_HEARTBEAT 3
// 最大绑定数（对应 sdkconfig 的 CONFIG_BT_NIMBLE_MAX_BONDS=3，改了那要同步改这）
#define MAX_BONDED 3

// 读 FFE3：返回当前绑定设备的地址列表（每台 7 字节 = type + 6 字节 MAC）
static int clr_read(struct ble_gatt_access_ctxt *ctxt)
{
    ble_addr_t addrs[MAX_BONDED];
    int num = 0;
    int rc = ble_store_util_bonded_peers(addrs, &num, MAX_BONDED);
    if (rc != 0) {
        return BLE_ATT_ERR_UNLIKELY;
    }

    uint8_t buf[7 * MAX_BONDED];
    int len = 0;
    for (int i = 0; i < num; i++) {
        buf[len++] = addrs[i].type;
        memcpy(&buf[len], addrs[i].val, 6);
        len += 6;
    }

    rc = os_mbuf_append(ctxt->om, buf, len);
    return (rc == 0) ? 0 : BLE_ATT_ERR_INSUFFICIENT_RES;
}

// 写 FFE3：删除绑定命令
//   [0x01]              = 删最旧
//   [0x02]              = 清空全部
//   [0x03][type][6MAC]  = 删指定那台
static int clr_write(struct ble_gatt_access_ctxt *ctxt)
{
    uint8_t *data = ctxt->om->om_data;
    uint16_t len = ctxt->om->om_len;
    if (len < 1) {
        return 0;
    }

    uint8_t cmd = data[0];
    if (cmd == 0x01) {
        int rc = ble_store_util_delete_oldest_peer();
        ESP_LOGI(TAG, "删除最旧绑定 rc=%d", rc);
    } else if (cmd == 0x02) {
        // 清空全部：列出所有绑定逐个删
        ble_addr_t addrs[MAX_BONDED];
        int num = 0;
        int rc = ble_store_util_bonded_peers(addrs, &num, MAX_BONDED);
        for (int i = 0; rc == 0 && i < num; i++) {
            ble_store_util_delete_peer(&addrs[i]);
        }
        ESP_LOGI(TAG, "清空全部绑定，共删除 %d 台", num);
    } else if (cmd == 0x03 && len >= 7) {
        ble_addr_t addr;
        addr.type = data[1];
        memcpy(addr.val, &data[2], 6);
        int rc = ble_store_util_delete_peer(&addr);
        ESP_LOGI(TAG, "删除指定绑定 rc=%d", rc);
    }
    return 0;
}

// 特征值访问回调：根据 arg 区分 CMD（开始命令）/ CLR（清除绑定）
static int gatt_access_cb(uint16_t conn_handle, uint16_t attr_handle,
                          struct ble_gatt_access_ctxt *ctxt, void *arg)
{
    int char_id = (int)(intptr_t)arg;

    if (char_id == CHAR_HEARTBEAT) {
        if (ctxt->op == BLE_GATT_ACCESS_OP_WRITE_CHR) {
            uint8_t nonce[4];
            if (!ctxt->om || OS_MBUF_PKTLEN(ctxt->om) != sizeof(nonce)) return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
            if (os_mbuf_copydata(ctxt->om, 0, sizeof(nonce), nonce) != 0) return BLE_ATT_ERR_UNLIKELY;
            memcpy(&s_heartbeat_reply[1], nonce, sizeof(nonce));
            return 0;
        }
        if (ctxt->op == BLE_GATT_ACCESS_OP_READ_CHR)
            return os_mbuf_append(ctxt->om, s_heartbeat_reply, sizeof(s_heartbeat_reply)) == 0 ? 0 : BLE_ATT_ERR_INSUFFICIENT_RES;
        return BLE_ATT_ERR_UNLIKELY;
    } else if (char_id == CHAR_CMD) {
        // CMD：写 0x01 = 开始
        if (ctxt->op == BLE_GATT_ACCESS_OP_WRITE_CHR) {
            if (ctxt->om && ctxt->om->om_len >= 1) {
                uint8_t val = ctxt->om->om_data[0];
                if (val == 0x01 || val == 0x00) {
                    atomic_store(&s_pending_command, val);
                    ESP_LOGI(TAG, "收到%s命令", val ? "准备" : "取消准备");
                }
            }
        }
    } else if (char_id == CHAR_CLR) {
        // CLR：读 = 绑定列表，写 = 删除命令
        if (ctxt->op == BLE_GATT_ACCESS_OP_READ_CHR) {
            return clr_read(ctxt);
        } else if (ctxt->op == BLE_GATT_ACCESS_OP_WRITE_CHR) {
            return clr_write(ctxt);
        }
    }
    return 0;
}

// GATT 服务表：一个主服务 + 四个特征值（CMD / ANS / CLR / HEARTBEAT）
static const struct ble_gatt_svc_def gatt_svcs[] = {
    {
        .type = BLE_GATT_SVC_TYPE_PRIMARY,
        .uuid = &s_svc_uuid.u,
        .characteristics = (struct ble_gatt_chr_def[]) {
            {
                .uuid = &s_cmd_uuid.u,
                .access_cb = gatt_access_cb,
                .arg = (void *)CHAR_CMD,
                .flags = BLE_GATT_CHR_F_WRITE | BLE_GATT_CHR_F_WRITE_NO_RSP,
            },
            {
                .uuid = &s_ans_uuid.u,
                .access_cb = gatt_access_cb,
                .val_handle = &s_ans_handle,
                .flags = BLE_GATT_CHR_F_NOTIFY,
            },
            {
                .uuid = &s_clr_uuid.u,
                .access_cb = gatt_access_cb,
                .arg = (void *)CHAR_CLR,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
            },
            {
                .uuid = &s_heartbeat_uuid.u,
                .access_cb = gatt_access_cb,
                .arg = (void *)CHAR_HEARTBEAT,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
            },
            { 0 }
        },
    },
    { 0 }
};

// 前向声明：gap_event_cb 定义在下面，这里先声明，供 start_advertise 调用
static int gap_event_cb(struct ble_gap_event *event, void *arg);

// 启动广播（连接模式 + 通用发现模式）
static void start_advertise(void)
{
    // ① 把名字直接放进主广播包（这样手机扫描时一定能看到名字）
    struct ble_hs_adv_fields adv_fields;
    memset(&adv_fields, 0, sizeof(adv_fields));
    adv_fields.flags = BLE_HS_ADV_F_DISC_GEN | BLE_HS_ADV_F_BREDR_UNSUP;
    adv_fields.name = (uint8_t *)s_dev_name;
    adv_fields.name_len = strlen(s_dev_name);
    adv_fields.name_is_complete = 1;
    ble_gap_adv_set_fields(&adv_fields);

    // ② 原有广播参数
    struct ble_gap_adv_params adv_params;
    memset(&adv_params, 0, sizeof(adv_params));
    adv_params.conn_mode = BLE_GAP_CONN_MODE_UND;   // 可连接
    adv_params.disc_mode = BLE_GAP_DISC_MODE_GEN;   // 可被发现

    int rc = ble_gap_adv_start(BLE_OWN_ADDR_PUBLIC, NULL, BLE_HS_FOREVER,
                               &adv_params, gap_event_cb, NULL);
    if (rc != 0) {
        ESP_LOGE(TAG, "广播启动失败 %d", rc);
    } else {
        ESP_LOGI(TAG, "已开始广播");
    }
}

// 同步完成回调：此时 GATT 服务已提交完毕，只需启动广播
static void on_sync(void)
{
    start_advertise();
}

// 连接/断开事件：记录连接句柄
static int gap_event_cb(struct ble_gap_event *event, void *arg)
{
    switch (event->type) {
    case BLE_GAP_EVENT_CONNECT:
        if (event->connect.status != 0) {
            s_connected = false;
            start_advertise();
            break;
        }
        s_conn_handle = event->connect.conn_handle;
        s_connected = true;
        ESP_LOGI(TAG, "已连接 conn_handle=%d", s_conn_handle);
        break;
    case BLE_GAP_EVENT_ENC_CHANGE:
        ESP_LOGI(TAG, "加密状态变化 status=%d（0=配对/加密成功）", event->enc_change.status);
        break;
    case BLE_GAP_EVENT_DISCONNECT:
        atomic_store(&s_pending_command, 0);
        s_conn_handle = 0;
        s_connected = false;
        ESP_LOGI(TAG, "已断开");
        start_advertise();                       // 重新广播，等待下次连接
        break;
    default:
        break;
    }
    return 0;
}

// BLE 主机任务
static void host_task(void *param)
{
    nimble_port_run();
    nimble_port_freertos_deinit();
}

void ble_init(void)
{
    // NVS 初始化（NimBLE 需要）
    esp_err_t ret = nvs_flash_init();
    if (ret == ESP_ERR_NVS_NO_FREE_PAGES || ret == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        nvs_flash_erase();
        nvs_flash_init();
    }

    ESP_ERROR_CHECK(nimble_port_init());

    // 注册同步回调
    ble_hs_cfg.sync_cb = on_sync;

    // 开启绑定 + 密钥持久化（Just Works 免 PIN，密钥存 NVS Flash，断电不丢）
    ble_store_config_init();
    ble_hs_cfg.store_status_cb = ble_store_util_status_rr;
    ble_hs_cfg.sm_io_cap = BLE_SM_IO_CAP_NO_IO;      // Just Works：不设 PIN，自动配对
    ble_hs_cfg.sm_bonding = 1;                       // 开启绑定
    ble_hs_cfg.sm_our_key_dist |= BLE_SM_PAIR_KEY_DIST_ENC;
    ble_hs_cfg.sm_their_key_dist |= BLE_SM_PAIR_KEY_DIST_ENC;

    // 基础 GAP/GATT 服务
    ble_svc_gap_init();
    ble_svc_gatt_init();

    // 设备名 + 注册自定义服务：必须放在 nimble_port_freertos_init 之前，
    // 因为 ble_gatts_start() 在主机启动时会一次性提交所有已注册的服务，
    // 之后再注册就太晚了，服务不会出现在手机的发现列表里。
    //
    // 设备名取蓝牙 MAC 后两字节拼进去（如 Quizzer-3F5A），多台板子好区分。
    uint8_t mac[6];
    if (esp_read_mac(mac, ESP_MAC_BT) == ESP_OK) {
        snprintf(s_dev_name, sizeof(s_dev_name), "Quizzer-%02X%02X", mac[4], mac[5]);
    } else {
        strcpy(s_dev_name, "Quizzer");
    }
    ble_svc_gap_device_name_set(s_dev_name);

    int rc = ble_gatts_count_cfg(gatt_svcs);
    if (rc != 0) { ESP_LOGE(TAG, "count_cfg failed %d", rc); return; }
    rc = ble_gatts_add_svcs(gatt_svcs);
    if (rc != 0) { ESP_LOGE(TAG, "add_svcs failed %d", rc); return; }

    // 启动主机任务（内部会触发 on_sync）
    nimble_port_freertos_init(host_task);
}

void ble_send_answer(void)
{
    if (!s_connected || s_ans_handle == 0) {
        ESP_LOGW(TAG, "无连接或句柄无效，跳过通知");
        return;
    }
    uint8_t val = 0x01;
    struct os_mbuf *om = ble_hs_mbuf_from_flat(&val, 1);
    int rc = ble_gatts_notify_custom(s_conn_handle, s_ans_handle, om);
    if (rc != 0) {
        ESP_LOGE(TAG, "notify failed %d", rc);
    } else {
        ESP_LOGI(TAG, "已发送按下通知");
    }
}

int ble_take_command(void)
{
    return atomic_exchange(&s_pending_command, -1);
}
