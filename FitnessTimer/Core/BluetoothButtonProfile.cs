namespace FitnessTimer.Core;

public sealed record BluetoothButtonProfile(int Lane, ulong Address, string DeviceName,
    string ServiceUuid, string CharacteristicUuid, string PressHex, bool MatchPrefix, string ReleaseHex = "", bool RandomAddress = false,
    bool Automatic = false, bool ProtocolConfirmed = false);

public static class ButtonPacket
{
    public static byte[] ParseHex(string text)
    {
        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c is not ':' and not '-'));
        if (compact.Length == 0 || compact.Length > 512 || compact.Length % 2 != 0)
            throw new FormatException("按键数据需为 1～256 字节的十六进制，例如 01 或 AA 01 FF。");
        try { return Convert.FromHexString(compact); }
        catch (FormatException) { throw new FormatException("按键数据只能包含十六进制字符 0-9、A-F，字节之间可用空格分隔。"); }
    }
    public static Guid ParseUuid(string text)
    {
        text = text.Trim();
        if (text.Length is 4 or 8 && text.All(Uri.IsHexDigit))
            text = text.PadLeft(8, '0') + "-0000-1000-8000-00805f9b34fb";
        if (!Guid.TryParse(text, out var uuid) || uuid == Guid.Empty)
            throw new FormatException("请输入厂商提供的服务或特征 UUID，支持 4 位、8 位十六进制或完整 UUID。");
        return uuid;
    }
    public static bool Matches(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> expected, bool prefix) =>
        !expected.IsEmpty && (prefix ? packet.StartsWith(expected) : packet.SequenceEqual(expected));
}

// Optional press/release latch keeps a held button from completing the next heat.
public sealed class ButtonPacketGate
{
    private readonly byte[] press, release;
    private readonly bool prefix;
    private bool held;
    private readonly object sync = new();
    public ButtonPacketGate(byte[] press, byte[] release, bool prefix)
    {
        if (press.Length == 0) throw new ArgumentException("按下数据不能为空。");
        if (release.Length > 0 && (ButtonPacket.Matches(press,release,prefix) || ButtonPacket.Matches(release,press,prefix)))
            throw new ArgumentException("按下和松开数据不能相同或存在前缀重叠。");
        this.press=press.ToArray(); this.release=release.ToArray(); this.prefix=prefix;
    }
    public bool TryPress(byte[] packet)
    {
        lock(sync)
        {
            if (release.Length>0 && ButtonPacket.Matches(packet,release,prefix)) { held=false; return false; }
            if (!ButtonPacket.Matches(packet,press,prefix)) return false;
            if (release.Length>0 && held) return false;
            held=true; return true;
        }
    }
}
