using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LiveSlides.App;

/// <summary>Pixel rectangle on the Windows virtual desktop (real device pixels, not WPF units).</summary>
public record ScreenBounds(int X, int Y, int Width, int Height);

/// <summary>Where the show goes: a physical monitor (full screen) or a resizable test window.</summary>
public record OutputTarget(string Id, string Label, ScreenBounds? Bounds, bool IsPrimary)
{
    public const string WindowId = "window";
    public bool IsWindow => Bounds is null;
    public override string ToString() => Label;
}

/// <summary>Enumerates monitors via Win32 (same approach as LiveCue's DisplayManager).</summary>
public static class Displays
{
    public static List<OutputTarget> GetTargets()
    {
        var result = new List<OutputTarget>();
        var device = new DISPLAY_DEVICE();
        device.cb = Marshal.SizeOf(device);

        for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++, device.cb = Marshal.SizeOf(device))
        {
            if ((device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            var mode = new DEVMODE();
            mode.dmSize = (short)Marshal.SizeOf(mode);
            if (!EnumDisplaySettings(device.DeviceName, ENUM_CURRENT_SETTINGS, ref mode)) continue;

            var isPrimary = (device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
            result.Add(new OutputTarget(device.DeviceName, GetMonitorName(device.DeviceName),
                new ScreenBounds(mode.dmPositionX, mode.dmPositionY, mode.dmPelsWidth, mode.dmPelsHeight), isPrimary));
        }

        // Named by position, not by number: the number in "\\.\DISPLAY3" is often not the one
        // Windows' Display settings show, and two different numbers for one screen confuse.
        var byPosition = result.OrderBy(t => t.Bounds!.X).ThenBy(t => t.Bounds!.Y).ToList();
        for (var i = 0; i < byPosition.Count; i++)
        {
            var t = byPosition[i];
            var where = byPosition.Count switch
            {
                1 => "",
                2 => i == 0 ? "a sinistra" : "a destra",
                3 => i switch { 0 => "a sinistra", 1 => "al centro", _ => "a destra" },
                _ => $"{i + 1}° da sinistra"
            };
            var label = (t.IsPrimary ? "Principale" : "Schermo") + (where == "" ? "" : " " + where)
                        + $" ({t.Bounds!.Width}×{t.Bounds.Height}) — {t.Label}";
            result[result.IndexOf(t)] = t with { Label = label };
        }

        result.Add(new OutputTarget(OutputTarget.WindowId, "Finestra di prova (ridimensionabile)", null, false));
        return result;
    }

    private static string GetMonitorName(string adapterDeviceName)
    {
        var monitor = new DISPLAY_DEVICE();
        monitor.cb = Marshal.SizeOf(monitor);
        if (EnumDisplayDevices(adapterDeviceName, 0, ref monitor, 0) && !string.IsNullOrWhiteSpace(monitor.DeviceString))
            return monitor.DeviceString;
        return "Schermo";
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
}
