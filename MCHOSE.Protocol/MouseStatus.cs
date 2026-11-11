namespace MCHOSE.Protocol;

public enum ConnectionType
{
    Usb,
    Wireless24G,
    Bluetooth,
    Sleeping,
    Off,
    Unknown,
}

/// <summary>The mouse's reply to the status command (0x06).</summary>
/// <param name="ProductName">Product name reported by the USB device (USB string descriptor).</param>
/// <param name="ChannelProductId">PID of the USB device the reply came through (receiver, or the mouse on the cable).</param>
/// <param name="ChannelPath">System path of the HID channel that replied.</param>
/// <param name="MouseVendorId">VID reported by the mouse itself.</param>
/// <param name="MouseProductId">PID reported by the mouse itself.</param>
/// <param name="Firmware">Firmware version (raw LE32 value).</param>
/// <param name="Flags">Raw flags byte; bits 0-2 are the connection type.</param>
/// <param name="BatteryPercent">Battery level in percent.</param>
/// <param name="ChargeRaw">Raw charging byte: 0 = on battery, 1 = charging.</param>
/// <param name="Raw">The reply body after removing XOR (without report ID and command byte).</param>
public sealed record MouseStatus(
    string ProductName,
    ushort ChannelProductId,
    string ChannelPath,
    ushort MouseVendorId,
    ushort MouseProductId,
    uint Firmware,
    byte Flags,
    byte BatteryPercent,
    byte ChargeRaw,
    byte[] Raw)
{
    public ConnectionType Connection => (Flags & 0x07) switch
    {
        0 => ConnectionType.Usb,
        1 => ConnectionType.Wireless24G,
        2 => ConnectionType.Bluetooth,
        6 => ConnectionType.Sleeping,
        7 => ConnectionType.Off,
        _ => ConnectionType.Unknown,
    };

    /// <summary>Connected to power. Verified: 0 = on battery, 1 = charging. A separate "full" value
    /// (2) is assumed but not yet observed - the mouse may instead keep reporting 1 at 100 %.</summary>
    public bool IsOnCharger => ChargeRaw is 1 or 2;

    /// <summary>On power and fully charged - safe to unplug.</summary>
    public bool IsFull => IsOnCharger && (ChargeRaw == 2 || BatteryPercent >= 100);

    /// <summary>On power and still charging.</summary>
    public bool IsCharging => IsOnCharger && !IsFull;

    /// <summary>Whether the battery reading is trustworthy (mouse awake).</summary>
    public bool IsBatteryValid =>
        Connection is ConnectionType.Usb or ConnectionType.Wireless24G or ConnectionType.Bluetooth
        && BatteryPercent <= 100;

    public string FirmwareText => $"0x{Firmware:X8}";
}
