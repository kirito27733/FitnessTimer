using System.Windows;
using System.Windows.Controls;
using FitnessTimer.Bluetooth;

namespace FitnessTimer;

public partial class BluetoothButtonPanel : UserControl
{
    private BluetoothButtonManager manager = null!;
    private Func<bool> canConfigure = () => false;
    private bool closed;
    public BluetoothButtonPanel() { InitializeComponent(); }
    public void Initialize(BluetoothButtonManager manager, Func<bool> canConfigure)
    {
        this.manager=manager; this.canConfigure=canConfigure; DataContext=manager;
        manager.StateChanged += UpdateControls;
        Unloaded += (_,_) => { closed=true; manager.StateChanged -= UpdateControls; };
        Loaded += (_,_) => { if (closed) { closed=false; manager.StateChanged += UpdateControls; } UpdateControls(); };
        UpdateControls();
    }
    public void UpdateControls()
    {
        var allowed = canConfigure();
        SettingsPanel.IsEnabled = true;
        foreach(var row in manager.Bindings)row.ConfigurationAllowed=allowed;
        Toolbar.IsEnabled = allowed && !manager.IsBusy;
        ModeText.Text = !allowed ? "测试中不能更换绑定设备，但可断开并重连原按钮；计时操作不受影响。"
            : manager.IsBusy ? "正在连接，可点击该按钮的“断开”取消；超时后可重试。"
            : manager.IsScanning ? "正在扫描，请唤醒按钮；15 秒后自动停止。默认仅显示 Quizzer 按钮和已绑定设备，其他型号请勾选“显示全部设备”。"
            : "默认仅显示 Quizzer 按钮和已绑定设备，其他型号请勾选“显示全部设备”。扫描后为开始键或赛道选择设备，再点击连接。赛前试按不发令，请进入现场计时后使用开始键。";
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
        if (((BleLaneBinding)((Button)sender).DataContext).IsBusy) return;
        try { await manager.ConnectAsync((BleLaneBinding)((Button)sender).DataContext); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) Error(ex); }
    }
    private void Disconnect_Click(object sender, RoutedEventArgs e)
    { manager.Disconnect((BleLaneBinding)((Button)sender).DataContext); }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!canConfigure() || manager.IsBusy) return;
        try { manager.SaveSettings(); }
        catch (Exception ex) { Error(ex); }
    }
    private void Learn_Click(object sender,RoutedEventArgs e)
    {
        if(!canConfigure()||manager.IsBusy)return;
        try{manager.StartLearning((BleLaneBinding)((Button)sender).DataContext);}catch(Exception ex){Error(ex);}
    }
    private void ConfirmLearn_Click(object sender,RoutedEventArgs e)
    {
        if(!canConfigure()||manager.IsBusy)return;
        try{manager.ConfirmLearning((BleLaneBinding)((Button)sender).DataContext);}catch(Exception ex){Error(ex);}
    }
    private async void PrepareTest_Click(object sender,RoutedEventArgs e)
    {
        if(!canConfigure()||manager.IsBusy)return;
        try{await manager.PrepareForTestAsync((BleLaneBinding)((Button)sender).DataContext);}catch(Exception ex){if(!closed)Error(ex);}
    }
    private void Error(Exception ex)
    {
        manager.AddLog(ex.Message);
        MessageBox.Show(Window.GetWindow(this),ex.Message,"蓝牙配置",MessageBoxButton.OK,MessageBoxImage.Warning);
    }
}
