namespace FitnessTimer.Core;

public static class BluetoothSignal
{
    public static int Level(int? rssi,bool connected,bool recent)
    {
        if(!connected||!recent||rssi is null or >=0 or <=-127)return 0;
        return rssi>=-55?4:rssi>=-67?3:rssi>=-80?2:1;
    }
}
