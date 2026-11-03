namespace FitnessTimer.Core;

// Protocol verified against 抽签/main/ble.c and main.c.
public static class QuizzerProtocol
{
    public static readonly Guid Service=ButtonPacket.ParseUuid("FFE0");
    public static readonly Guid Command=ButtonPacket.ParseUuid("FFE1");
    public static readonly Guid Notification=ButtonPacket.ParseUuid("FFE2");
    public static readonly Guid Heartbeat=ButtonPacket.ParseUuid("FFE4");
    public static bool Matches(Guid service,Guid notification)=>service==Service&&notification==Notification;
    public static byte[] ArmCommand()=>[0x01];
}
