namespace FitnessTimer.Bluetooth;

internal interface IBleButtonConnection : IDisposable
{
    bool Ready {get;}
    bool IsQuizzer {get;}
    Guid ServiceId {get;}
    Guid CharacteristicId {get;}
    event Action<byte[],long>? Packet;
    event Action? Disconnected;
    Task ConnectAsync(ulong address,bool randomAddress,Guid? serviceId,Guid? characteristicId);
    Task ArmAsync();
    bool SupportsDisarm => false;
    Task DisarmAsync() => Task.CompletedTask;
    bool SupportsHeartbeat => false;
    Task HeartbeatAsync() => Task.CompletedTask;
    bool SupportsBattery => false;
    Task<int?> ReadBatteryAsync() => Task.FromResult<int?>(null);
}
