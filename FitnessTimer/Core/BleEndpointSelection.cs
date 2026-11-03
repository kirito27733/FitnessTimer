namespace FitnessTimer.Core;

public sealed record BleEndpoint(Guid Service,Guid Characteristic);
public static class BleEndpointSelection
{
    // Never mistake battery, device information, GAP/GATT, or HID reports for a finish button.
    public static bool CanAutoInspect(Guid service) => !new[]{"1800","1801","180A","180F","1812"}.Any(s=>ButtonPacket.ParseUuid(s)==service);
    public static BleEndpoint Choose(IEnumerable<BleEndpoint> endpoints)
    {
        var candidates=endpoints.Where(e=>CanAutoInspect(e.Service)).ToList();
        if(candidates.Count==0)throw new InvalidOperationException("此设备未提供可自动识别的按钮通道。请确认是 BLE 按钮，或提供按钮型号进行适配。");
        if(candidates.Count>1)throw new InvalidOperationException("此设备有多个通知通道，暂不能自动判断哪个是按钮。请使用高级设置，或提供型号进行适配。");
        return candidates[0];
    }
}
