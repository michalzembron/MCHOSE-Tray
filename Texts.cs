using MCHOSE.Protocol;

namespace MCHOSE;

/// <summary>User-facing strings (single place to hook up translations later).</summary>
public static class Texts
{
    public const string AppName = "MCHOSE Tray";
    public const string Author = "Michał Zembroń";
    public const string RepositoryUrl = "https://github.com/michalzembron/MCHOSE-Tray";

    /// <summary>Version from the assembly (set in MCHOSE.csproj), e.g. "1.0.0".</summary>
    public static string Version
    {
        get
        {
            var v = typeof(Texts).Assembly.GetName().Version;
            return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>Mouse name reported by the device; the app name until the first reading.</summary>
    public static string DeviceName(MonitorSnapshot s) => s.LastValid?.ProductName ?? AppName;

    public static string Connection(ConnectionType c) => c switch
    {
        ConnectionType.Usb => "USB cable",
        ConnectionType.Wireless24G => "2.4 GHz",
        ConnectionType.Bluetooth => "Bluetooth",
        ConnectionType.Sleeping => "Asleep",
        ConnectionType.Off => "Off",
        _ => "Unknown",
    };

    public static string State(MonitorSnapshot s) => s.State switch
    {
        MonitorState.Searching => "Looking for the mouse…",
        MonitorState.NoDevice => "No receiver or mouse found",
        MonitorState.NoResponse => "Mouse not responding (off or out of range)",
        MonitorState.Sleeping => "Mouse asleep - showing the last reading",
        _ => Charge(s.LastValid),
    };

    /// <summary>"Charging" / "Fully charged - you can unplug" / "On battery".</summary>
    public static string Charge(MouseStatus? st) => st switch
    {
        { IsFull: true } => "Fully charged - you can unplug",
        { IsCharging: true } => "Charging",
        _ => "On battery",
    };

    public static string ToolTip(MonitorSnapshot s)
    {
        if (s.LastValid is not { } st)
            return $"{AppName}\n{State(s)}";
        var line = $"Battery: {st.BatteryPercent}%";
        if (s.State == MonitorState.Ok)
            line += st.IsFull ? " (full)" : st.IsCharging ? " (charging)" : "";
        else
            line += $" (as of {s.LastValidAt:HH:mm})";
        return $"{st.ProductName}\n{line}\n{State(s)}";
    }
}
