using System.ComponentModel;
using System.Runtime.CompilerServices;
using FitnessTimer.Core;

namespace FitnessTimer.Bluetooth;

public sealed class BleDeviceOption(ulong address, string name, bool randomAddress = false) : INotifyPropertyChanged
{
    public ulong Address { get; } = address;
    public bool RandomAddress { get; } = randomAddress;
    private string deviceName = name;
    public string Name
    {
        get => deviceName;
        set { if (deviceName==value) return; deviceName=value; PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Name))); PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Label))); }
    }
    public string Label => $"{(string.IsNullOrWhiteSpace(Name) ? "未命名设备" : Name)}  [{Mac}]";
    public string Mac => string.Join(":", Enumerable.Range(0, 6).Select(i => ((Address >> ((5-i)*8)) & 0xFF).ToString("X2")));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class BleLaneBinding(int lane) : INotifyPropertyChanged
{
    public int Lane { get; } = lane;
    public string Label => Lane==0?"开始键":$"{Lane} 道";
    public string CompactLabel => Lane==0?"开始":$"{Lane}道";
    private int? batteryPercent;
    private DateTime batteryAt;
    public int? BatteryPercent=>IsConnected&&DateTime.UtcNow-batteryAt<TimeSpan.FromMinutes(2)?batteryPercent:null;
    public string BatteryDisplay=>!IsConnected?"未连接":BatteryPercent is int percent?$"{percent}%":"电量未知";
    public string BatteryHint=>!IsConnected?$"{Label}：未连接":BatteryPercent is int percent?$"{Label}：已连接，电量 {percent}%":$"{Label}：已连接，设备未提供电量或暂时无法读取";
    public bool BatteryLow=>BatteryPercent is int percent&&percent<=20;
    internal void UpdateBattery(int? percent)
    {batteryPercent=percent is >=0 and <=100?percent:null;batteryAt=DateTime.UtcNow;RefreshBattery();}
    public void RefreshBattery(){Changed(nameof(BatteryPercent));Changed(nameof(BatteryDisplay));Changed(nameof(BatteryHint));Changed(nameof(BatteryLow));}
    private int? signalRssi;
    private DateTime signalAt;
    public int SignalLevel=>BluetoothSignal.Level(signalRssi,IsConnected,DateTime.UtcNow-signalAt<TimeSpan.FromSeconds(15));
    public string SignalGlyph=>SignalLevel switch{1=>"▂▁▁▁",2=>"▂▄▁▁",3=>"▂▄▆▁",4=>"▂▄▆█",_=>IsConnected?"?":"▁▁▁▁"};
    public string SignalHint=>!IsConnected?$"{Label}：未连接":SignalLevel==0?$"{Label}：已连接，信号强度暂不可读取":$"{Label}：已连接，广播信号 {signalRssi} dBm（{SignalLevel}/4 格）";
    internal void UpdateSignal(int rssi){signalRssi=rssi;signalAt=DateTime.UtcNow;RefreshSignal();}
    public void RefreshSignal(){Changed(nameof(SignalLevel));Changed(nameof(SignalHint));Changed(nameof(SignalGlyph));}
    private BleDeviceOption? device;
    private string serviceUuid = "", characteristicUuid = "", pressHex = "01", releaseHex = "", status = "未连接", lastPacket = "尚未收到数据";
    private bool matchPrefix, connected, busy, automatic=true, protocolConfirmed,learning;
    private int presses;
    internal IBleButtonConnection? Connection { get; set; }
    public BleDeviceOption? Device { get => device; set { if(ReferenceEquals(device,value))return;Set(ref device,value);ProtocolConfirmed=false;signalRssi=null;signalAt=default;RefreshSignal();ClearCandidate(); } }
    public bool Automatic { get=>automatic;set=>Set(ref automatic,value); }
    public bool ProtocolConfirmed { get=>protocolConfirmed;internal set=>Set(ref protocolConfirmed,value); }
    public bool IsLearning { get=>learning;internal set=>Set(ref learning,value); }
    internal ButtonPacketGate? PacketGate;
    internal byte[]? CandidateBytes;
    internal long LearningStartedAt;
    public bool HasCandidate=>CandidateBytes!=null;
    internal void SetCandidate(byte[] bytes){CandidateBytes=bytes.ToArray();Changed(nameof(HasCandidate));}
    internal void ClearCandidate(){CandidateBytes=null;Changed(nameof(HasCandidate));IsLearning=false;}
    public string ServiceUuid { get => serviceUuid; set => Set(ref serviceUuid, value); }
    public string CharacteristicUuid { get => characteristicUuid; set => Set(ref characteristicUuid, value); }
    public string PressHex { get => pressHex; set => Set(ref pressHex, value); }
    public string ReleaseHex { get => releaseHex; set => Set(ref releaseHex, value); }
    public bool MatchPrefix { get => matchPrefix; set => Set(ref matchPrefix, value); }
    public string Status { get => status; internal set => Set(ref status, value); }
    public string LastPacket { get => lastPacket; internal set => Set(ref lastPacket, value); }
    public int Presses { get => presses; internal set => Set(ref presses, value); }
    public bool IsConnected { get => connected; internal set { if(connected!=value)UpdateBattery(null);Set(ref connected, value); Changed(nameof(CanEdit));RefreshSignal();RefreshBattery(); } }
    public bool IsBusy { get => busy; internal set { Set(ref busy, value); Changed(nameof(CanEdit)); } }
    private bool configurationAllowed=true;
    public bool ConfigurationAllowed {get=>configurationAllowed;internal set{Set(ref configurationAllowed,value);Changed(nameof(CanEdit));}}
    public bool CanEdit => ConfigurationAllowed && !IsConnected && !IsBusy && Connection == null;
    public bool IsReadyForTiming => IsConnected && Connection?.Ready==true && PacketGate!=null && !IsLearning && !HasCandidate;
    public string ProtocolLabel=>Connection?.IsQuizzer==true
        ? (Connection.SupportsHeartbeat?"抽签按钮 · 独立心跳已启用（3秒） · 断线自动重连":"抽签按钮 · 旧固件不支持心跳，升级后启用 · 断线自动重连")
        : "通用BLE按钮";
    public bool NeedsPrepare=>Lane!=0&&IsConnected&&Connection?.IsQuizzer==true;
    public bool CanLearn=>IsConnected&&Connection?.IsQuizzer!=true;
    internal void RefreshEditState(){Changed(nameof(CanEdit));Changed(nameof(ProtocolLabel));Changed(nameof(NeedsPrepare));Changed(nameof(CanLearn));}
    public BluetoothButtonProfile Profile() => new(Lane, Device?.Address ?? 0, Device?.Name ?? "", ServiceUuid.Trim(), CharacteristicUuid.Trim(), PressHex.Trim(), MatchPrefix, ReleaseHex.Trim(), Device?.RandomAddress??false,Automatic,ProtocolConfirmed);
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; Changed(name); }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
