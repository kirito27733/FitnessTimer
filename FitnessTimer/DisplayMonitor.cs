using System.Runtime.InteropServices;

namespace FitnessTimer;

// Native monitor coordinates are used directly when placing the full-screen window.
internal sealed record DisplayMonitor(IntPtr Handle, string Name, bool IsPrimary, int Left, int Top, int Width, int Height)
{
    public string Label => $"{Name}{(IsPrimary ? "（主屏）" : "（外屏）")} · {Width} × {Height}";
    public static IReadOnlyList<DisplayMonitor> GetAll()
    {
        var monitors = new List<DisplayMonitor>();
        MonitorCallback callback = (IntPtr handle, IntPtr hdc, ref NativeRect bounds, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = string.Empty };
            if (GetMonitorInfo(handle, ref info))
                monitors.Add(new DisplayMonitor(handle, info.Device.Replace("\\\\.\\", "屏幕 "),
                    (info.Flags & 1) != 0, info.Bounds.Left, info.Bounds.Top,
                    info.Bounds.Right - info.Bounds.Left, info.Bounds.Bottom - info.Bounds.Top));
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return monitors.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Name).ToList();
    }
    public void PlaceFullScreen(IntPtr window)
    {
        if (!SetWindowPos(window, IntPtr.Zero, Left, Top, Width, Height, 0x0004 | 0x0020 | 0x0040))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Bounds, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr hdc, ref NativeRect bounds, IntPtr data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
