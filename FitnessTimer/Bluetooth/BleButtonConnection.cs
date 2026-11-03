using System.Diagnostics;
using System.IO;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using FitnessTimer.Core;

namespace FitnessTimer.Bluetooth;

internal sealed class BleButtonConnection : IBleButtonConnection
{
    private BluetoothLEDevice? device;
    private GattDeviceService? service;
    private GattCharacteristic? characteristic;
    private GattCharacteristic? command;
    private GattCharacteristic? heartbeat;
    private GattDeviceService? batteryService;
    private GattCharacteristic? batteryCharacteristic;
    private bool batteryInspected;
    public bool SupportsBattery => batteryCharacteristic!=null;
    private uint heartbeatSequence;
    public bool SupportsHeartbeat => heartbeat!=null;
    public bool SupportsDisarm {get;private set;}
    private readonly SemaphoreSlim writeLock=new(1);
    private readonly SemaphoreSlim heartbeatLock=new(1);
    public bool IsQuizzer=>command!=null;
    private GattSession? session;
    private volatile bool disposed, ready;
    public bool Ready => ready && !disposed;
    public Guid ServiceId {get;private set;}
    public Guid CharacteristicId {get;private set;}
    public event Action<byte[], long>? Packet;
    public event Action? Disconnected;
    public async Task ConnectAsync(ulong address, bool randomAddress, Guid? serviceId, Guid? characteristicId)
    {
        var discovered=new List<GattDeviceService>();
        try
        {
            var resolvedDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(address,randomAddress ? BluetoothAddressType.Random : BluetoothAddressType.Public);
            if (disposed) { resolvedDevice?.Dispose(); ThrowIfDisposed(); }
            device = resolvedDevice;
            ThrowIfDisposed();
            if (device == null) throw new InvalidOperationException("无法访问设备。请确认按钮已唤醒；如厂商要求配对，请先在 Windows 蓝牙设置中配对。");
            device.ConnectionStatusChanged += ConnectionChanged;
            var services = serviceId.HasValue ? await device.GetGattServicesForUuidAsync(serviceId.Value, BluetoothCacheMode.Uncached)
                : await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            discovered.AddRange(services.Services);ThrowIfDisposed();
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new InvalidOperationException($"设备无法连接或未提供可用服务（{services.Status}）。请唤醒按钮；如设备要求配对，请先在 Windows 中配对。");
            var candidates=new List<(GattDeviceService Service,GattCharacteristic Characteristic)>();
            foreach(var item in services.Services)
            {
                if(!serviceId.HasValue&&!BleEndpointSelection.CanAutoInspect(item.Uuid))continue;
                var result=characteristicId.HasValue ? await item.GetCharacteristicsForUuidAsync(characteristicId.Value,BluetoothCacheMode.Uncached)
                    : await item.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                ThrowIfDisposed();
                if(result.Status!=GattCommunicationStatus.Success)continue;
                foreach(var candidate in result.Characteristics)
                    if((candidate.CharacteristicProperties&(GattCharacteristicProperties.Notify|GattCharacteristicProperties.Indicate))!=0)candidates.Add((item,candidate));
            }
            if(serviceId.HasValue)
            {
                if(candidates.Count!=1)throw new InvalidOperationException("未找到指定的按钮通知通道，请核对高级设置或设备权限。");
                service=candidates[0].Service;characteristic=candidates[0].Characteristic;
            }
            else
            {
                var chosen=BleEndpointSelection.Choose(candidates.Select(c=>new BleEndpoint(c.Service.Uuid,c.Characteristic.Uuid)));
                var match=candidates.First(c=>c.Service.Uuid==chosen.Service&&c.Characteristic.Uuid==chosen.Characteristic);
                service=match.Service;characteristic=match.Characteristic;
            }
            ServiceId=service.Uuid;CharacteristicId=characteristic.Uuid;
            if(QuizzerProtocol.Matches(ServiceId,CharacteristicId))
            {
                var commands=await service.GetCharacteristicsForUuidAsync(QuizzerProtocol.Command,BluetoothCacheMode.Uncached);ThrowIfDisposed();
                if(commands.Status!=GattCommunicationStatus.Success||commands.Characteristics.Count!=1)
                    throw new InvalidOperationException("发现FFE0/FFE2按钮，但未找到FFE1准备通道，请核对抽签固件版本。");
                command=commands.Characteristics[0];
                if((command.CharacteristicProperties&(GattCharacteristicProperties.Write|GattCharacteristicProperties.WriteWithoutResponse))==0)
                    throw new InvalidOperationException("FFE1不支持写入，无法准备按钮。");
                var heartbeats=await service.GetCharacteristicsForUuidAsync(QuizzerProtocol.Heartbeat,BluetoothCacheMode.Uncached);ThrowIfDisposed();
                if(heartbeats.Status==GattCommunicationStatus.Success&&heartbeats.Characteristics.Count==1)
                {
                    var candidate=heartbeats.Characteristics[0];
                    if(candidate.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read|GattCharacteristicProperties.Write))
                    {
                        var probe=await candidate.ReadValueAsync(BluetoothCacheMode.Uncached);ThrowIfDisposed();
                        if(probe.Status==GattCommunicationStatus.Success&&probe.Value.Length==5)
                        {using var reader=DataReader.FromBuffer(probe.Value);var version=reader.ReadByte();if(version is 1 or 2){heartbeat=candidate;SupportsDisarm=version>=2;}}
                    }
                }
            }
            var properties = characteristic.CharacteristicProperties;
            var mode = properties.HasFlag(GattCharacteristicProperties.Notify) ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                : properties.HasFlag(GattCharacteristicProperties.Indicate) ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                : throw new InvalidOperationException("该特征不支持 Notify 或 Indicate，不能接收按键通知。");
            characteristic.ValueChanged += ValueChanged;
            var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(mode);
            ThrowIfDisposed();
            if (status != GattCommunicationStatus.Success) throw new InvalidOperationException($"订阅按键通知失败（{status}），请检查设备权限或配对状态。");
            var resolvedSession = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
            if (disposed) { resolvedSession?.Dispose(); ThrowIfDisposed(); }
            session = resolvedSession;
            ThrowIfDisposed();
            if (session?.CanMaintainConnection == true) session.MaintainConnection = true;
            ready = true;
            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected || !ready) throw new InvalidOperationException("设备已断开，请唤醒按钮后重新连接。");
        }
        catch { Dispose(); throw; }
        finally{foreach(var item in discovered)if(!ReferenceEquals(item,service))Release(item.Dispose);}
    }
    private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(BleButtonConnection)); }
    public async Task ArmAsync()
        =>await WriteCommandAsync(1);
    public async Task DisarmAsync()
    {if(SupportsDisarm)await WriteCommandAsync(0);}
    private async Task WriteCommandAsync(byte value)
    {
        if(command==null)return;
        await writeLock.WaitAsync();
        try
        {
            ThrowIfDisposed();if(!Ready)throw new InvalidOperationException("按钮已断开，无法准备。");
            using var writer=new DataWriter();writer.WriteByte(value);
            var option=command.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)?GattWriteOption.WriteWithResponse:GattWriteOption.WriteWithoutResponse;
            var result=await command.WriteValueWithResultAsync(writer.DetachBuffer(),option);ThrowIfDisposed();
            if(result.Status!=GattCommunicationStatus.Success)throw new InvalidOperationException($"发送FFE1=01失败：{result.Status}。");
        }
        finally{writeLock.Release();}
    }
    private void ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var timestamp = Stopwatch.GetTimestamp();
        if (!Ready || args.CharacteristicValue.Length > 1024) return;
        using var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var bytes = new byte[reader.UnconsumedBufferLength]; reader.ReadBytes(bytes);
        if (Ready) Packet?.Invoke(bytes, timestamp);
    }
    public async Task HeartbeatAsync()
    {
        if(heartbeat==null)return;
        await heartbeatLock.WaitAsync();
        try
        {
            ThrowIfDisposed();if(!Ready)throw new IOException("蓝牙已断开。");
            var sequence=unchecked(++heartbeatSequence);
            byte[] nonce=[(byte)sequence,(byte)(sequence>>8),(byte)(sequence>>16),(byte)(sequence>>24)];
            using var writer=new DataWriter();writer.WriteBytes(nonce);
            var sent=await heartbeat.WriteValueWithResultAsync(writer.DetachBuffer(),GattWriteOption.WriteWithResponse);ThrowIfDisposed();
            if(sent.Status!=GattCommunicationStatus.Success)throw new IOException($"心跳写入失败：{sent.Status}");
            var reply=await heartbeat.ReadValueAsync(BluetoothCacheMode.Uncached);ThrowIfDisposed();
            if(reply.Status!=GattCommunicationStatus.Success||reply.Value.Length!=5)throw new IOException("心跳应答无效。");
            using var reader=DataReader.FromBuffer(reply.Value);
            if(reader.ReadByte() is not (1 or 2))throw new IOException("心跳协议版本不匹配。");
            var echoed=new byte[4];reader.ReadBytes(echoed);
            if(!echoed.SequenceEqual(nonce))throw new IOException("心跳序号不匹配。");
        }
        finally{heartbeatLock.Release();}
    }
    public async Task<int?> ReadBatteryAsync()
    {
        ThrowIfDisposed();if(!Ready||device==null)return null;
        if(!batteryInspected)
        {
            var services=await device.GetGattServicesForUuidAsync(ButtonPacket.ParseUuid("180F"),BluetoothCacheMode.Uncached);
            if(disposed){foreach(var item in services.Services)Release(item.Dispose);ThrowIfDisposed();}
            if(services.Status!=GattCommunicationStatus.Success)
            {foreach(var item in services.Services)Release(item.Dispose);return null;}
            foreach(var item in services.Services)
            {
                if(batteryService==null)batteryService=item;else Release(item.Dispose);
            }
            if(batteryService!=null)
            {
                var characteristics=await batteryService.GetCharacteristicsForUuidAsync(ButtonPacket.ParseUuid("2A19"),BluetoothCacheMode.Uncached);ThrowIfDisposed();
                if(characteristics.Status!=GattCommunicationStatus.Success)return null;
                batteryCharacteristic=characteristics.Characteristics.FirstOrDefault(c=>c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read));
            }
            batteryInspected=true;
        }
        if(batteryCharacteristic==null)return null;
        var value=await batteryCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached);ThrowIfDisposed();
        if(value.Status!=GattCommunicationStatus.Success||value.Value.Length!=1)return null;
        using var reader=DataReader.FromBuffer(value.Value);var percentage=reader.ReadByte();
        return percentage<=100?percentage:null;
    }
    private void ConnectionChanged(BluetoothLEDevice sender, object args)
    {
        if (!disposed && ready && sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        { ready = false; Disconnected?.Invoke(); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; ready = false;
        if (characteristic != null) Release(() => characteristic.ValueChanged -= ValueChanged);
        if (device != null) Release(() => device.ConnectionStatusChanged -= ConnectionChanged);
        if (session != null)
        {
            Release(() => { if (session.CanMaintainConnection) session.MaintainConnection=false; });
            Release(session.Dispose);
        }
        if (service != null) Release(service.Dispose);
        if (batteryService != null) Release(batteryService.Dispose);
        if (device != null) Release(device.Dispose);
        Packet = null; Disconnected = null;
    }
    private static void Release(Action release)
    {
        try { release(); }
        catch (Exception ex) when (ex is ObjectDisposedException or System.Runtime.InteropServices.COMException) { }
    }
}
