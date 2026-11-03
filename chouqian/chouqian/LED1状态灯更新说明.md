# LED1状态灯版本说明（2026-10-29）

当前源码与build、firmware-release已同步更新并使用ESP-IDF v5.4.4编译通过。

状态灯改为GPIO3、高电平点亮，依据esp-light的LED1电量指示灯映射；实体板的贴装情况仍需烧录验证。实体按钮保持GPIO19，按钮灯控制保持GPIO23。

开机：LED1与按钮灯一起亮300ms、灭300ms，共三次。
待机、取消准备、完成及蓝牙断开：按钮灯灭，LED1亮。
准备：按钮灯亮，LED1灭。

启动日志增加：
Firmware: chouqian-LED1-GPIO3-v1 | button=GPIO19 | status=GPIO3 active-high | lamp=GPIO23

在VS Code打开E:\tnjs\chouqian\chouqian，选择实体板的COM端口，执行ESP-IDF: Flash (UART) Your Project。烧录后执行Monitor Device并复位，核对上述版本日志与开机LED1闪烁。尚未代为烧录，不保证未经实测的物理LED一定响应；如果LED1仍完全不亮，应检查实际电路映射、贴装、驱动及供电。
