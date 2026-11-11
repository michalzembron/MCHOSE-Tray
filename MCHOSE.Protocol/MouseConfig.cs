using System.Buffers.Binary;

namespace MCHOSE.Protocol;

public enum SensorMode
{
    Performance,
    Gaming,
    Extreme,
}

public enum SensorToggle
{
    Ripple,
    AngleSnapping,
    MotionSync,
}

/// <summary>
/// The mouse configuration block returned by the <c>0x12 0x67</c> read command (63 bytes).
/// Layout documented in docs/PROTOCOL.md.
/// </summary>
public sealed record MouseConfig(
    int ProfileIndex,
    int WirelessDpiStage,
    int WirelessRateIndex,
    int UsbDpiStage,
    int UsbRateIndex,
    int[] DpiX,
    int[] DpiY,
    int EnabledStages,
    byte SensorBits,
    int DebounceMs,
    int SleepMinutes,
    int RotationDegrees,
    byte[] Raw)
{
    public const int Length = 63;

    /// <summary>Polling rate index → Hz.</summary>
    public static readonly int[] PollingRates = [125, 500, 1000, 2000, 4000, 8000];

    /// <summary>Lift-off distance index → millimetres.</summary>
    public static readonly double[] LodValues = [0.7, 1.0, 2.0];

    public byte LodIndex => (byte)(SensorBits & 0x03);
    public double? LiftOffMm => LodIndex < LodValues.Length ? LodValues[LodIndex] : null;

    /// <summary>True when every DPI stage has the same value on X and Y.</summary>
    public bool DpiAxesEqual => DpiX.SequenceEqual(DpiY);
    public bool RippleControl => (SensorBits & 0x04) != 0;
    public bool AngleSnapping => (SensorBits & 0x08) != 0;
    public bool MotionSync => (SensorBits & 0x10) != 0;

    public SensorMode Mode => (SensorBits & 0xC0) switch
    {
        0xC0 => SensorMode.Extreme,
        0x80 => SensorMode.Gaming,
        _ => SensorMode.Performance,
    };

    /// <summary>The DPI stage / polling rate in effect for the given connection.</summary>
    public int DpiStage(ConnectionType c) => c == ConnectionType.Usb ? UsbDpiStage : WirelessDpiStage;

    public int? ActiveDpi(ConnectionType c) => DpiStage(c) is var s && s < DpiX.Length ? DpiX[s] : null;

    public int? PollingRateHz(ConnectionType c)
    {
        int i = c == ConnectionType.Usb ? UsbRateIndex : WirelessRateIndex;
        return i < PollingRates.Length ? PollingRates[i] : null;
    }

    public static MouseConfig Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < Length)
            throw new ArgumentException($"Config block must be {Length} bytes, got {b.Length}.");

        var dpiX = new int[6];
        var dpiY = new int[6];
        for (int i = 0; i < 6; i++)
        {
            dpiX[i] = BinaryPrimitives.ReadUInt16LittleEndian(b[(4 + 2 * i)..]);
            dpiY[i] = BinaryPrimitives.ReadUInt16LittleEndian(b[(51 + 2 * i)..]);
        }

        return new MouseConfig(
            ProfileIndex: b[0],
            // Verified by writing the 2.4G polling rate: byte 1 = USB (cable), byte 2 = 2.4G
            // (the M HUB read parser labels them the other way round).
            // Low nibble = DPI stage, high nibble = polling rate index.
            UsbDpiStage: b[1] & 0x0F,
            UsbRateIndex: b[1] >> 4,
            WirelessDpiStage: b[2] & 0x0F,
            WirelessRateIndex: b[2] >> 4,
            DpiX: dpiX,
            DpiY: dpiY,
            EnabledStages: b[16],
            SensorBits: b[17],
            DebounceMs: b[18],
            SleepMinutes: b[19],
            RotationDegrees: (sbyte)b[49],
            Raw: b[..Length].ToArray());
    }
}

/// <summary>Firmware versions; the receiver version is null when the mouse is on the cable.</summary>
public sealed record FirmwareInfo(string? Mouse, string? Receiver);
