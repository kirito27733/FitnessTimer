using System.Windows;
using System.Windows.Controls;
using FitnessTimer.Bluetooth;

namespace FitnessTimer;

public partial class BluetoothButtonWindow : Window
{
    private readonly BluetoothButtonManager manager;
    private readonly Func<bool> canConfigure;
    private bool closed;
    public BluetoothButtonWindow(BluetoothButtonManager manager, Func<bool> canConfigure)
    {
        InitializeComponent(); this.manager=manager; this.canConfigure=canConfigure; DataContext=manager;
        manager.StateChanged += UpdateControls;
        Closed += (_,_) => { closed=true; manager.StateChanged -= UpdateControls; };
        UpdateControls();
    }
    public void UpdateControls()
    {
        var allowed = canConfigure();
        SettingsPanel.IsEnabled = allowed && !manager.IsBusy;
        Toolbar.IsEnabled = allowed && !manager.IsBusy;
        ModeText.Text = !allowed ? "测试进行中或蓝牙终点已启用：仅查看连接和按键记录。停用蓝牙终点且结束本组后可修改绑定。"
            : manager.IsBusy ? "正在连接，请等待。设备离线时 Windows 蓝牙请求可能需要较长时间。"
            : "仅支持提供 GATT 通知协议的 BLE 按钮。普通自拍遥控器、键盘式 HID 按钮不一定兼容；扫描成功不代表能接收按键。";
    }
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (!canConfigure() || manager.IsBusy) return;
        try { await manager.StartScanAsync(); }
        catch (Exception ex) { if (!closed) Error(ex); }
    }
    private void StopScan_Click(object sender, RoutedEventArgs e) => manager.StopScan();
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!canConfigure() || manager.IsBusy) return;
        try { await manager.ConnectAsync((BleLaneBinding)((Button)sender).DataContext); }
        catch (Exception ex) { if (!closed) Error(ex); }
    }
    private void Disconnect_Click(object sender, RoutedEventArgs e)
    { if (canConfigure() && !manager.IsBusy) manager.Disconnect((BleLaneBinding)((Button)sender).DataContext); }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!canConfigure() || manager.IsBusy) return;
        try { manager.SaveSettings(); }
        catch (Exception ex) { Error(ex); }
    }
    private void Error(Exception ex)
    {
        manager.AddLog(ex.Message);
        MessageBox.Show(this,ex.Message,"蓝牙按钮",MessageBoxButton.OK,MessageBoxImage.Warning);
    }
}
