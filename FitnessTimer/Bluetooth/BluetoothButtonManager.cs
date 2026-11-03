using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Windows.Threading;
using FitnessTimer.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;

namespace FitnessTimer.Bluetooth;

public sealed class BluetoothButtonManager : IDisposable, INotifyPropertyChanged
{
    public ObservableCollection<BleLaneBinding> Lanes { get; } = new(Enumerable.Range(1,4).Select(i => new BleLaneBinding(i)));
    public BleLaneBinding StartButton { get; } = new(0);
    public IEnumerable<BleLaneBinding> Bindings => new[]{StartButton}.Concat(Lanes);
    public ObservableCollection<BleDeviceOption> Devices { get; } = [];
    public ICollectionView VisibleDevices { get; }
    private bool showAllDevices;
    public bool ShowAllDevices
    {
        get => showAllDevices;
        set
        {
            if(showAllDevices==value)return;
            showAllDevices=value;
            VisibleDevices.Refresh();
            PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(ShowAllDevices)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<string> Log { get; } = [];
    private readonly Dictionary<BleLaneBinding,CancellationTokenSource> connectionCancellation=[];
    internal TimeSpan ConnectionTimeout {get;set;}=TimeSpan.FromSeconds(12);
    private bool preparing;
    public bool IsPreparing=>preparing;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IBleButtonConnection,int> pressVersions=[];
    private readonly Dictionary<IBleButtonConnection,int> preparedVersions=[];
    public bool IsBusy => preparing||Bindings.Any(r => r.IsBusy);
    public bool AllConnected => Lanes.All(r => r.IsConnected && r.Connection?.Ready == true);
    public bool AllValidated => AllConnected && Lanes.All(r=>r.Presses>0&&r.PacketGate!=null&&!r.IsLearning&&!r.HasCandidate);
    public bool IsScanning=>watcher?.Status==BluetoothLEAdvertisementWatcherStatus.Started;
    public event Action<int, long>? PressReceived;
    public event Action? StateChanged;
    public event Action<int>? ConnectionLost;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer scanTimer;
    private BluetoothLEAdvertisementWatcher? watcher;
    private BluetoothLEAdvertisementWatcher? signalWatcher;
    private volatile bool disposed;
    private int scanGeneration;
    private readonly string settingsPath;
    private readonly Func<IBleButtonConnection> connectionFactory;
    private readonly Dictionary<BleLaneBinding,int> connectionIntents=[];
    private readonly Dictionary<BleLaneBinding,int> retries=[];
    internal TimeSpan HeartbeatInterval {get;set;}=TimeSpan.FromSeconds(3);
    internal TimeSpan HeartbeatTimeout {get;set;}=TimeSpan.FromSeconds(9);
    internal TimeSpan ReconnectDelay {get;set;}=TimeSpan.FromSeconds(3);
    internal TimeSpan BatteryPollInterval {get;set;}=TimeSpan.FromSeconds(60);
    internal TimeSpan BatteryReadTimeout {get;set;}=TimeSpan.FromSeconds(5);
    private int NextIntent(BleLaneBinding row)
    {var intent=connectionIntents.GetValueOrDefault(row)+1;connectionIntents[row]=intent;return intent;}
    private bool WantsConnection(BleLaneBinding row,int intent)=>!disposed&&connectionIntents.GetValueOrDefault(row)==intent;
    public BluetoothButtonManager(Dispatcher dispatcher,string? settingsDirectory=null):this(dispatcher,settingsDirectory,()=>new BleButtonConnection()){}
    internal BluetoothButtonManager(Dispatcher dispatcher,string? settingsDirectory,Func<IBleButtonConnection> connectionFactory)
    {
        this.connectionFactory=connectionFactory;
        settingsPath=Path.Combine(settingsDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FitnessTimer"),"bluetooth-buttons.json");
        this.dispatcher = dispatcher;
        VisibleDevices = new ListCollectionView(Devices);
        VisibleDevices.Filter = item => item is BleDeviceOption device &&
            (ShowAllDevices || device.Name.StartsWith("Quizzer",StringComparison.OrdinalIgnoreCase) ||
             Bindings.Any(row=>row.Device?.Address==device.Address));
        foreach(var row in Bindings)
            row.PropertyChanged += (_,args) =>
            {
                // Defer until ComboBox has finished changing its selection.
                if(args.PropertyName==nameof(BleLaneBinding.Device))
                    dispatcher.BeginInvoke(new Action(()=>{if(!disposed)VisibleDevices.Refresh();}));
            };
        scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        scanTimer.Tick += (_,_) => StopScan();
        LoadSettings();
        VisibleDevices.Refresh();
    }
    private void OnUi(Action action)
    {
        if (disposed || dispatcher.HasShutdownStarted) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(new Action(() => { if (!disposed) action(); }));
    }
    public void AddLog(string message)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {message}");
        while (Log.Count > 150) Log.RemoveAt(Log.Count-1);
    }
    public async Task StartScanAsync()
    {
        StopScan();
        var generation = scanGeneration;
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (disposed || generation!=scanGeneration) return;
        if (adapter == null || !adapter.IsLowEnergySupported) throw new InvalidOperationException("没有可用的 BLE 蓝牙适配器，请连接支持 BLE 的适配器。");
        var radio = await adapter.GetRadioAsync();
        if (disposed || generation!=scanGeneration) return;
        if (radio != null && radio.State != RadioState.On) throw new InvalidOperationException("蓝牙未开启，请先在 Windows 设置中打开蓝牙。");
        watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += AdvertisementReceived;
        watcher.Stopped += WatcherStopped;
        watcher.Start(); scanTimer.Start();
        StateChanged?.Invoke();
        AddLog("扫描已开始，持续 15 秒。请唤醒按钮并使其广播；扫描结果不表示设备已兼容。");
    }
    public void StopScan()
    {
        scanGeneration++;
        scanTimer.Stop();
        if (watcher == null) return;
        watcher.Received -= AdvertisementReceived; watcher.Stopped -= WatcherStopped;
        if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop();
        watcher = null;
        StateChanged?.Invoke();
    }
    private void AdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var address = args.BluetoothAddress; var name = args.Advertisement.LocalName;
        var randomAddress = args.BluetoothAddressType==BluetoothAddressType.Random;
        OnUi(() =>
        {
            if (!ReferenceEquals(watcher, sender)) return;
            foreach(var row in Bindings.Where(r=>r.Device?.Address==address))row.UpdateSignal(args.RawSignalStrengthInDBm);
            RecordDiscoveredDevice(address,name,randomAddress);
        });
    }
    internal void RecordDiscoveredDevice(ulong address,string name,bool randomAddress)
    {
        var existing=Devices.FirstOrDefault(d=>d.Address==address);
        if(existing==null){Devices.Add(new BleDeviceOption(address,name,randomAddress));return;}
        // Active scan responses may supply a name after the initial unnamed advertisement.
        if(!string.IsNullOrWhiteSpace(name)&&existing.Name!=name)
        {existing.Name=name;VisibleDevices.Refresh();}
    }
    private void WatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args) =>
        OnUi(() => { if (args.Error != BluetoothError.Success) AddLog($"扫描停止：{args.Error}。请检查蓝牙开关、适配器和 Windows 设备权限。"); });
    public async Task ConnectAsync(BleLaneBinding row)
    {
        if(row.IsBusy)throw new InvalidOperationException("正在连接该按钮，请等待。");
        if(row.Connection!=null&&!row.Connection.Ready)Disconnect(row);
        if(row.Connection!=null)throw new InvalidOperationException("该按钮已连接，无需重复连接；更换设备前请先断开。");
        await ConnectCoreAsync(row,NextIntent(row));
    }
    private async Task ConnectCoreAsync(BleLaneBinding row,int intent)
    {
        if (row.Device == null) throw new InvalidOperationException("请先扫描并选择要绑定的蓝牙按钮。");
        if (Bindings.Any(r => r != row && r.Device?.Address == row.Device.Address)) throw new InvalidOperationException("同一按钮不能同时绑定开始键和赛道，也不能绑定多个赛道，请选择不同的按钮。");
        Guid? service=row.Automatic?null:ButtonPacket.ParseUuid(row.ServiceUuid);
        Guid? characteristic=row.Automatic?null:ButtonPacket.ParseUuid(row.CharacteristicUuid);
        row.ClearCandidate();
        row.PacketGate=!row.Automatic||row.ProtocolConfirmed ? CreateGate(row) : null;
        var cancellation=new CancellationTokenSource();connectionCancellation[row]=cancellation;
        var connection = connectionFactory(); row.Connection = connection;
        connection.Packet += (bytes, timestamp) =>
        {
            if(!ReferenceEquals(row.Connection,connection)||!connection.Ready)return;
            if(connection.IsQuizzer&&bytes.Length==1&&bytes[0]==1)pressVersions.AddOrUpdate(connection,1,(_,v)=>v+1);
            var packetGate=Volatile.Read(ref row.PacketGate);
            var matched = packetGate?.TryPress(bytes)==true;
            OnUi(() =>
            {
                if (!ReferenceEquals(row.Connection, connection)) return;
                row.LastPacket = $"{DateTime.Now:HH:mm:ss}  {Convert.ToHexString(bytes.AsSpan(0,Math.Min(bytes.Length,64)))}{(bytes.Length>64 ? "…" : "")}";
                if(row.IsLearning&&timestamp>=row.LearningStartedAt&&bytes.Length>0&&bytes.Length<=256)
                {
                    row.SetCandidate(bytes);row.IsLearning=false;row.Status="已收到信号，请确认后再次试按";
                }
                if (matched&&ReferenceEquals(packetGate,row.PacketGate)){row.Presses++;row.Status="已连接 · 按键已验证";}
                if(matched&&row.Lane==0&&connection.IsQuizzer&&ReferenceEquals(packetGate,row.PacketGate))SetStartButtonAvailable(false);
                AddLog($"{row.Label}收到 {Convert.ToHexString(bytes.AsSpan(0,Math.Min(bytes.Length,64)))}：{(matched ? "匹配按下事件" : "松开、重复或未匹配，已忽略")}");
            });
            // Keep the original callback timestamp, before any UI queue delay.
            if (!disposed && ReferenceEquals(row.Connection,connection) && connection.Ready && matched) PressReceived?.Invoke(row.Lane, timestamp);
        };
        connection.Disconnected += () => OnUi(() =>
        {
            if (!ReferenceEquals(row.Connection, connection)) return;
            row.IsConnected = false; row.Status = "已断开，等待自动重连";
            AddLog(row.Lane==0?"开始按钮连接丢失，可点击界面统一开始；已开始的计时不受影响。":$"{row.Label}按钮连接丢失，计时继续，可手动完成。");
            StateChanged?.Invoke(); ConnectionLost?.Invoke(row.Lane);
            ScheduleReconnect(row,intent);
        });
        row.IsBusy = true; row.Presses=0; row.LastPacket="等待按键数据"; row.Status = "正在连接和订阅…"; StateChanged?.Invoke();
        try
        {
            await connection.ConnectAsync(row.Device.Address, row.Device.RandomAddress, service, characteristic).WaitAsync(ConnectionTimeout,cancellation.Token);
            if(!WantsConnection(row,intent)||!ReferenceEquals(row.Connection,connection))throw new OperationCanceledException("连接已取消。");
            if (disposed) { connection.Dispose(); return; }

            if(connection.IsQuizzer&&row.Automatic)
            {
                row.PressHex="01";row.ReleaseHex="";row.MatchPrefix=false;row.ProtocolConfirmed=true;
                row.PacketGate=CreateGate(row);
                AddLog($"{row.Label}已识别抽签按钮：FFE0服务、FFE1准备命令、FFE2按下通知01，无需手动学习。");
            }
            if(row.Lane==0)await connection.DisarmAsync().WaitAsync(TimeSpan.FromSeconds(3),cancellation.Token);else await EnsurePreparedAsync(connection);
            if(!WantsConnection(row,intent)||!connection.Ready)throw new OperationCanceledException("连接已取消或失效。");
            row.IsConnected=true;
            EnsureSignalWatcher();
            row.ServiceUuid=connection.ServiceId.ToString();row.CharacteristicUuid=connection.CharacteristicId.ToString();
            row.Status = row.IsConnected ? (connection.IsQuizzer?(row.Lane==0?"已连接 · 开始灯随现场待发令状态变化":"已连接 · 抽签按钮已准备，可试按"):row.PacketGate==null?"已连接，请识别按键":"已连接，请试按按钮") : "连接丢失，请重新连接";
            if(row.IsConnected&&row.PacketGate==null)StartLearning(row);
            AddLog($"{row.Label}连接成功。{(row.PacketGate==null?"请点击识别按键，按一次按钮并确认，再次试按验证。":"请试按按钮验证。")}");
            TrySaveSettings();
            AddLog($"{row.Label}：{(connection.SupportsHeartbeat?"已启用FFE4独立心跳，每3秒检查一次":"固件不支持心跳，使用系统长连接保持；升级按钮固件后启用心跳")}。");
            if(connection.SupportsHeartbeat)_=MonitorHeartbeatAsync(row,connection,intent);
            _=MonitorBatteryAsync(row,connection,intent);
        }
        catch
        {
            connection.Dispose();preparedVersions.Remove(connection);pressVersions.TryRemove(connection,out _);
            if(ReferenceEquals(row.Connection,connection)){row.Connection=null;row.IsConnected=false;row.Status="连接失败，可重试";}throw;
        }
        finally
        {
            if(connectionCancellation.GetValueOrDefault(row)==cancellation)connectionCancellation.Remove(row);
            cancellation.Dispose();
            if(ReferenceEquals(row.Connection,connection)||(row.Connection==null&&WantsConnection(row,intent)))
            {row.IsBusy=false;row.RefreshEditState();StateChanged?.Invoke();if(row.Lane==0)SyncStartButtonLight();}
        }
    }
    public void Disconnect(BleLaneBinding row)
    {
        NextIntent(row); // Explicit disconnect also cancels pending automatic retries.
        if(connectionCancellation.Remove(row,out var cancellation))cancellation.Cancel();
        row.IsBusy=false;
        if(row.Connection is {} old){preparedVersions.Remove(old);pressVersions.TryRemove(old,out _);old.Dispose();} row.Connection = null;
        row.ClearCandidate();row.PacketGate=null;
        row.IsConnected = false; row.Status = "未连接"; row.RefreshEditState(); StateChanged?.Invoke();
    }
    private async Task MonitorHeartbeatAsync(BleLaneBinding row,IBleButtonConnection connection,int intent)
    {
        var failures=0;
        while(WantsConnection(row,intent)&&ReferenceEquals(row.Connection,connection)&&connection.Ready)
        {
            await Task.Delay(HeartbeatInterval);
            if(!WantsConnection(row,intent)||!ReferenceEquals(row.Connection,connection)||!connection.Ready)return;
            if(row.IsBusy||preparing)continue;
            try
            {
                await connection.HeartbeatAsync().WaitAsync(HeartbeatTimeout);
                failures=0;
            }
            catch(Exception ex)
            {
                if(!WantsConnection(row,intent)||!ReferenceEquals(row.Connection,connection))return;
                failures++;
                // A hung Windows GATT operation cannot be safely queued repeatedly.
                // Nine seconds without completion represents three missed heartbeats.
                if(ex is not TimeoutException&&failures<3)
                {AddLog($"{row.Label}心跳失败 {failures}/3：{ex.Message}");continue;}
                connection.Dispose();row.IsConnected=false;row.PacketGate=null;
                row.Status="心跳异常，等待自动重连";row.RefreshEditState();
                AddLog($"{row.Label}心跳失联：{ex.Message}；计时继续，终点可手动完成。");
                StateChanged?.Invoke();ConnectionLost?.Invoke(row.Lane);ScheduleReconnect(row,intent);return;
            }
        }
    }
    private async Task MonitorBatteryAsync(BleLaneBinding row,IBleButtonConnection connection,int intent)
    {
        while(WantsConnection(row,intent)&&ReferenceEquals(row.Connection,connection)&&connection.Ready)
        {
            try
            {
                var percent=await connection.ReadBatteryAsync().WaitAsync(BatteryReadTimeout);
                if(!WantsConnection(row,intent)||!ReferenceEquals(row.Connection,connection)||!row.IsConnected)return;
                row.UpdateBattery(percent);
                if(!connection.SupportsBattery)return;
            }
            catch(Exception ex)
            {
                if(!WantsConnection(row,intent)||!ReferenceEquals(row.Connection,connection))return;
                row.UpdateBattery(null);AddLog($"{row.Label}电量暂不可读取：{ex.Message}");
                // Do not queue new reads behind a hung Windows request; timing and heartbeat continue.
                if(ex is TimeoutException)return;
            }
            await Task.Delay(BatteryPollInterval);
        }
    }
    private async void ScheduleReconnect(BleLaneBinding row,int intent)
    {
        if(!WantsConnection(row,intent)||retries.GetValueOrDefault(row)==intent)return;
        retries[row]=intent;var attempt=0;
        try
        {
            while(WantsConnection(row,intent))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(30000,ReconnectDelay.TotalMilliseconds*Math.Pow(2,Math.Min(attempt,4)))));
                if(!WantsConnection(row,intent))return;
                if(row.IsBusy)continue;
                if(row.Connection is {} old){preparedVersions.Remove(old);pressVersions.TryRemove(old,out _);old.Dispose();}row.Connection=null;row.IsConnected=false;row.PacketGate=null;
                row.Status=$"正在自动重连（第 {++attempt} 次）";
                try
                {
                    await ConnectCoreAsync(row,intent);
                    AddLog($"{row.Label}已自动重连并重新准备；断线期间的按键无法补回，请核对该道成绩。");return;
                }
                catch(Exception ex)
                {
                    if(!WantsConnection(row,intent))return;
                    row.Status="自动重连失败，稍后重试（可点击断开停止）";
                    AddLog($"{row.Label}重连失败：{ex.Message}");StateChanged?.Invoke();
                }
            }
        }
        finally{if(retries.GetValueOrDefault(row)==intent)retries.Remove(row);}
    }
    private bool startAvailable,startLightSyncRunning;
    private IBleButtonConnection? startLightConnection;
    private bool? startLightApplied;
    public void SetStartButtonAvailable(bool available)
    {startAvailable=available;SyncStartButtonLight();}
    private async void SyncStartButtonLight()
    {
        if(startLightSyncRunning||disposed)return;
        startLightSyncRunning=true;
        try
        {
            while(!disposed&&StartButton.Connection is {Ready:true,IsQuizzer:true} connection&&!StartButton.IsBusy)
            {
                var desired=startAvailable;
                if(ReferenceEquals(connection,startLightConnection)&&startLightApplied==desired)return;
                try
                {
                    if(desired)await connection.ArmAsync().WaitAsync(TimeSpan.FromSeconds(3));else await connection.DisarmAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    startLightConnection=connection;startLightApplied=desired;
                    if(!desired&&!connection.SupportsDisarm)AddLog("开始键旧固件不支持熄灯指令，请刷入版本2固件；软件仍会拦截非待发令状态的按下。");
                }
                catch(Exception ex)
                {
                    startLightApplied=null;
                    if(ReferenceEquals(StartButton.Connection,connection))
                    {StartButton.Status="开始键灯光同步失败，请重连";AddLog($"开始键灯光同步失败：{ex.Message}");}
                    return;
                }
            }
        }
        finally{startLightSyncRunning=false;}
    }
    private async Task EnsurePreparedAsync(IBleButtonConnection connection)
    {
        var version=pressVersions.GetValueOrDefault(connection);
        if(preparedVersions.TryGetValue(connection,out var prepared)&&prepared==version)return;
        await connection.ArmAsync().WaitAsync(TimeSpan.FromSeconds(3));
        if(!connection.Ready)throw new IOException("准备期间连接已断开。");
        if(pressVersions.GetValueOrDefault(connection)!=version)throw new IOException("准备期间收到按下，请松开按钮后重试。");
        preparedVersions[connection]=version;
    }
    public async Task PrepareFinishButtonsAsync(IEnumerable<int> lanes)
    {
        if(preparing)throw new InvalidOperationException("正在准备终点按钮，请稍候。");
        preparing=true;StateChanged?.Invoke();
        try
        {
            var rows=lanes.Select(n=>Lanes[n-1]).Where(r=>r.IsReadyForTiming&&r.Connection?.IsQuizzer==true).ToList();
            await Task.WhenAll(rows.Select(async row=>
            {
                var connection=row.Connection!;
                try{await EnsurePreparedAsync(connection);}
                catch(Exception ex){throw new InvalidOperationException($"{row.Label}按钮准备失败：{ex.Message}",ex);}
            }));
        }
        finally{preparing=false;StateChanged?.Invoke();}
    }
    public async Task PrepareForTestAsync(BleLaneBinding row)
    {
        if(row.Lane==0)throw new InvalidOperationException("开始键仅在现场计时载入批次、可以开始时亮灯，请通过实际发令验证。");
        if(IsBusy||!row.NeedsPrepare)throw new InvalidOperationException("请先连接抽签按钮，等待其他操作完成。");
        row.IsBusy=true;StateChanged?.Invoke();
        try
        {
            preparedVersions.Remove(row.Connection!);
            await EnsurePreparedAsync(row.Connection!);
            if(row.Automatic&&row.PacketGate==null)row.PacketGate=CreateGate(row);
            row.Status="已发送FFE1=01，请按一下实体按钮";AddLog($"{row.Label}已准备试按。");
        }
        finally{row.IsBusy=false;row.RefreshEditState();StateChanged?.Invoke();}
    }
    private void EnsureSignalWatcher()
    {
        if(signalWatcher!=null)return;
        try
        {
            signalWatcher=new BluetoothLEAdvertisementWatcher{ScanningMode=BluetoothLEScanningMode.Passive};
            signalWatcher.Received+=SignalReceived;signalWatcher.Start();
        }
        catch(Exception ex)
        {
            if(signalWatcher!=null){signalWatcher.Received-=SignalReceived;signalWatcher=null;}
            AddLog($"信号强度暂不可读取，按钮连接仍可使用：{ex.Message}");
        }
    }
    private void SignalReceived(BluetoothLEAdvertisementWatcher sender,BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var address=args.BluetoothAddress;var rssi=args.RawSignalStrengthInDBm;
        OnUi(()=>{if(ReferenceEquals(signalWatcher,sender))foreach(var row in Bindings.Where(r=>r.Device?.Address==address))row.UpdateSignal(rssi);});
    }
    private static ButtonPacketGate CreateGate(BleLaneBinding row)=>new(ButtonPacket.ParseHex(row.PressHex),string.IsNullOrWhiteSpace(row.ReleaseHex)?[]:ButtonPacket.ParseHex(row.ReleaseHex),row.MatchPrefix);
    public void StartLearning(BleLaneBinding row)
    {
        if(!row.IsConnected||row.Connection?.Ready!=true)throw new InvalidOperationException("请先连接要识别的按钮。");
        row.ClearCandidate();row.PacketGate=null;row.ProtocolConfirmed=false;row.Presses=0;
        row.LearningStartedAt=Stopwatch.GetTimestamp();row.IsLearning=true;row.Status="已连接，请按一下实体按钮";StateChanged?.Invoke();
    }
    public void ConfirmLearning(BleLaneBinding row)
    {
        if(!row.IsConnected||row.Connection?.Ready!=true||row.CandidateBytes==null)throw new InvalidOperationException("请先点击识别按键，再按一下实体按钮。");
        row.PressHex=Convert.ToHexString(row.CandidateBytes);row.ReleaseHex="";row.MatchPrefix=false;
        Interlocked.Exchange(ref row.PacketGate,CreateGate(row));row.ProtocolConfirmed=true;row.ClearCandidate();row.Presses=0;
        row.Status="已记住按键，请再次试按验证";TrySaveSettings();StateChanged?.Invoke();
    }
    private void TrySaveSettings(){try{SaveSettings();}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or InvalidOperationException){AddLog($"连接已保留，配置保存失败：{ex.Message}");}}
    public void SaveSettings()
    {
        var profiles = Bindings.Select(r => r.Profile()).ToList();
        if (profiles.Where(p => p.Address != 0).GroupBy(p => p.Address).Any(g => g.Count()>1))
            throw new InvalidOperationException("开始键及四条赛道必须使用不同的按钮。");
        foreach (var profile in profiles.Where(p => p.Address != 0))
        {
            if(!profile.Automatic){ButtonPacket.ParseUuid(profile.ServiceUuid);ButtonPacket.ParseUuid(profile.CharacteristicUuid);}
            if(!profile.Automatic||profile.ProtocolConfirmed)_ = new ButtonPacketGate(ButtonPacket.ParseHex(profile.PressHex),string.IsNullOrWhiteSpace(profile.ReleaseHex) ? [] : ButtonPacket.ParseHex(profile.ReleaseHex),profile.MatchPrefix);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath+".tmp", JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(settingsPath+".tmp",settingsPath,true); AddLog("按钮绑定和协议配置已保存。重新启动后连接即可生效。");
    }
    private void LoadSettings()
    {
        if (!File.Exists(settingsPath)) return;
        try
        {
            var profiles = JsonSerializer.Deserialize<List<BluetoothButtonProfile>>(File.ReadAllText(settingsPath));
            if (profiles == null || profiles.Any(p=>p==null) ||
                !(profiles.Select(p=>p.Lane).Order().SequenceEqual(new[]{1,2,3,4})||profiles.Select(p=>p.Lane).Order().SequenceEqual(new[]{0,1,2,3,4})))
                throw new InvalidDataException("按钮配置必须包含四条赛道，可额外包含开始键，且不能重复。");
            foreach (var profile in profiles)
            {
                var row = profile.Lane==0?StartButton:Lanes[profile.Lane-1];
                if (profile.Address != 0)
                {
                    var option = new BleDeviceOption(profile.Address,profile.DeviceName??"",profile.RandomAddress); Devices.Add(option); row.Device=option;
                }
                row.ServiceUuid=profile.ServiceUuid??""; row.CharacteristicUuid=profile.CharacteristicUuid??"";
                row.PressHex=profile.PressHex??"01"; row.MatchPrefix=profile.MatchPrefix;
                row.ReleaseHex=profile.ReleaseHex??"";
                row.Automatic=profile.Automatic||(string.IsNullOrWhiteSpace(profile.ServiceUuid)&&string.IsNullOrWhiteSpace(profile.CharacteristicUuid));
                row.ProtocolConfirmed=profile.ProtocolConfirmed;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { AddLog($"配置读取失败，未自动覆盖原文件：{ex.Message}"); }
    }
    public void Dispose()
    {
        if (disposed) return;
        StopScan(); disposed = true;
        foreach(var cancellation in connectionCancellation.Values.ToArray())cancellation.Cancel();
        if(signalWatcher!=null){signalWatcher.Received-=SignalReceived;if(signalWatcher.Status==BluetoothLEAdvertisementWatcherStatus.Started)signalWatcher.Stop();signalWatcher=null;}
        foreach (var row in Bindings) row.Connection?.Dispose();
        PressReceived = null; StateChanged = null; ConnectionLost = null;
    }
}
