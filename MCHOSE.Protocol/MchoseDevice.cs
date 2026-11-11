using System.Buffers.Binary;
using System.Text;
using HidSharp;

namespace MCHOSE.Protocol;

/// <summary>
/// Communication with MCHOSE mice ("native 0x11" protocol, e.g. A7 V2 Ultra). See docs/PROTOCOL.md.
///
/// Channel: HID collection with usage page 0xFF01, feature reports 0x11 (20 data bytes) and
/// 0x12 (64 data bytes). Every data byte is XOR-ed with 0xFF.
/// Request: SET_FEATURE [reportId, cmd, args…] (before XOR). The device signals "reply ready"
/// with input report 0x13 starting with E5 cmd; the reply is then read with GET_FEATURE.
/// Until it is ready the device returns an echo of the request.
///
/// The 2.4 GHz receiver is 3837:100B; over the cable the mouse enumerates as a separate
/// device (e.g. 3837:4019) with its own channel - the receiver then stays silent.
///
/// Only whitelisted commands can be sent: reads (<see cref="ReadCommands"/>) and a small set of
/// range-checked setting writes (<see cref="WriteCommands"/>). Reset, whole-config writes and
/// anything firmware-related are intentionally impossible to send through this class.
/// </summary>
public static class MchoseDevice
{
    public const int VendorId = 0x3837;
    public const uint VendorUsagePage = 0xFF01;

    private const byte ShortReport = 0x11;
    private const byte LongReport = 0x12;
    private const byte ReadyReport = 0x13;
    private const byte ReadyMarker = 0xE5;

    private const byte CmdBasics = 0x03;
    private const byte CmdVersion = 0x04;
    private const byte CmdStatus = 0x06;
    private const byte CmdConfig = 0x67;
    private const byte ReceiverVersionArg = 0xAA;
    private const byte CmdSetSleep = 0x0A;
    private const byte CmdSetDpi = 0x40;
    private const byte CmdSetPollingRate = 0x41;
    private const byte CmdSetSensor = 0x42;
    private const byte CmdSetDebounce = 0x43;

    public const int MaxDebounceMs = 20;
    public const int MaxSleepMinutes = 100;
    public const int MinDpi = 200;
    public const int MaxDpi = 42000;
    public const int MaxRotation = 30;

    /// <summary>The only (report, command) pairs this library will ever send.</summary>
    private static readonly HashSet<(byte Report, byte Cmd)> ReadCommands =
    [
        (ShortReport, CmdBasics),
        (ShortReport, CmdVersion),
        (ShortReport, CmdStatus),
        (LongReport, CmdConfig),
    ];

    /// <summary>The only setting writes this library will ever send (each one verified on hardware).</summary>
    private static readonly HashSet<(byte Report, byte Cmd)> WriteCommands =
    [
        (ShortReport, CmdSetDebounce),
        (ShortReport, CmdSetSleep),
        (ShortReport, CmdSetPollingRate),
        (ShortReport, CmdSetSensor),
        (LongReport, CmdSetDpi),
    ];

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan InputPollTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan BetweenCommands = TimeSpan.FromMilliseconds(30);

    /// <summary>All MCHOSE HID collections exposing the 0xFF01 configuration channel.</summary>
    public static IReadOnlyList<HidDevice> FindChannels()
    {
        var result = new List<HidDevice>();
        foreach (var dev in DeviceList.Local.GetHidDevices(VendorId))
        {
            try
            {
                if (dev.GetMaxFeatureReportLength() < 21)
                    continue;
                var usages = dev.GetReportDescriptor().DeviceItems
                    .SelectMany(i => i.Usages.GetAllValues());
                if (usages.Any(u => u >> 16 == VendorUsagePage))
                    result.Add(dev);
            }
            catch
            {
                // Some collections (system mouse/keyboard) cannot be queried - skip them.
            }
        }
        return result;
    }

    public static HidDevice? FindChannel(string path) =>
        FindChannels().FirstOrDefault(d => d.DevicePath == path);

    /// <summary>
    /// Queries all channels and returns the first valid status reply, or null.
    /// </summary>
    /// <param name="preferredPath">Path of the channel that answered last time - queried first,
    /// so we don't wait for the silent receiver to time out while the mouse is on the cable.</param>
    public static MouseStatus? ReadStatus(string? preferredPath = null, CancellationToken ct = default)
    {
        var channels = FindChannels().OrderByDescending(d => d.DevicePath == preferredPath);
        foreach (var dev in channels)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var status = ReadStatus(dev, ct);
                if (status is not null)
                    return status;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Device unplugged meanwhile or busy - try the next one.
            }
        }
        return null;
    }

    /// <summary>Sends the status command to a specific channel.</summary>
    public static MouseStatus? ReadStatus(HidDevice device, CancellationToken ct = default)
    {
        // The echo of the request has a zero VID/PID - a real reply does not.
        var body = Query(device, ShortReport, CmdStatus, [], ct,
            accept: b => (b[0] | b[1] | b[2] | b[3]) != 0);
        return body is null ? null : ParseStatus(GetProductName(device), (ushort)device.ProductID, device.DevicePath, body);
    }

    /// <summary>Reads the configuration block (DPI, polling rate, LOD, …).</summary>
    public static MouseConfig? ReadConfig(HidDevice device, CancellationToken ct = default)
    {
        var body = Query(device, LongReport, CmdConfig, [], ct);
        return body is { Length: >= MouseConfig.Length } ? MouseConfig.Parse(body) : null;
    }

    /// <summary>Reads the firmware versions of the mouse and (when present) the receiver.</summary>
    public static FirmwareInfo ReadFirmware(HidDevice device, CancellationToken ct = default)
    {
        string? mouse = ParseVersion(Query(device, ShortReport, CmdVersion, [], ct));
        Wait(BetweenCommands, ct);
        string? receiver = ParseVersion(Query(device, ShortReport, CmdVersion, [ReceiverVersionArg], ct));
        // Over the cable there is no receiver; ignore a reply that just repeats the mouse version.
        if (receiver == mouse && device.ProductID != 0x100A && device.ProductID != 0x100B)
            receiver = null;
        return new FirmwareInfo(mouse, receiver);
    }

    /// <summary>Sets the key debounce time (0-20 ms). Returns true when the mouse acknowledged it.</summary>
    public static bool SetDebounce(HidDevice device, int ms, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ms, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ms, MaxDebounceMs);
        return Write(device, ShortReport, CmdSetDebounce, [(byte)ms], ct);
    }

    /// <summary>Sets the sleep timer in minutes (1-100), or 0 = never sleep.</summary>
    public static bool SetSleep(HidDevice device, int minutes, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, MaxSleepMinutes);
        return Write(device, ShortReport, CmdSetSleep, [(byte)(minutes > 0 ? 1 : 0), (byte)minutes], ct);
    }

    /// <summary>
    /// Sets the polling rate (index into <see cref="MouseConfig.PollingRates"/>) of the link the command
    /// goes through: (index, 0) on the cable, (0, index) through the receiver - as M HUB does.
    /// </summary>
    public static bool SetPollingRate(HidDevice device, int rateIndex, bool viaCable, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rateIndex, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(rateIndex, MouseConfig.PollingRates.Length);
        byte[] args = viaCable ? [(byte)rateIndex, 0] : [0, (byte)rateIndex];
        return Write(device, ShortReport, CmdSetPollingRate, args, ct);
    }

    /// <summary>Sets the lift-off distance (index into <see cref="MouseConfig.LodValues"/>).</summary>
    public static bool SetLiftOff(HidDevice device, int lodIndex, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lodIndex, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lodIndex, MouseConfig.LodValues.Length);
        return WriteSensor(device, (byte)lodIndex, ct);
    }

    public static bool SetToggle(HidDevice device, SensorToggle toggle, bool on, MouseConfig current, CancellationToken ct = default) =>
        WriteSensor(device, current.LodIndex, ct, toggle: (toggle, on));

    public static bool SetMode(HidDevice device, SensorMode mode, MouseConfig current, CancellationToken ct = default) =>
        WriteSensor(device, current.LodIndex, ct, mode: mode);

    /// <summary>Sets the sensor rotation in degrees (-30 to 30).</summary>
    public static bool SetRotation(HidDevice device, int degrees, MouseConfig current, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(degrees, -MaxRotation);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(degrees, MaxRotation);
        return WriteSensor(device, current.LodIndex, ct, rotation: degrees);
    }

    /// <summary>
    /// Command 0x42: lod ripple line motionSync 0 0 gameMode rotateOpen rotateVal.
    /// Like M HUB, LOD is always sent with its current value (0 means 0.7 mm, not "unchanged");
    /// toggles 1 = on, 2 = off, 0 = unchanged; mode 0 = unchanged; rotation only with rotateOpen = 1.
    /// </summary>
    private static bool WriteSensor(HidDevice device, byte lod, CancellationToken ct,
        (SensorToggle Toggle, bool On)? toggle = null, SensorMode? mode = null, int? rotation = null)
    {
        var args = new byte[9];
        args[0] = lod;
        if (toggle is { } t)
            args[t.Toggle switch { SensorToggle.Ripple => 1, SensorToggle.AngleSnapping => 2, _ => 3 }] = (byte)(t.On ? 1 : 2);
        if (mode is { } m)
            args[6] = (byte)((int)m + 1);
        if (rotation is { } r)
        {
            args[7] = 1;
            args[8] = (byte)(sbyte)r;
        }
        return Write(device, ShortReport, CmdSetSensor, args, ct);
    }

    /// <summary>
    /// Sets the six DPI stages (same value on X and Y), the number of enabled stages and the active
    /// stage. Format as M HUB: 12 40 usbIdx gIdx 00 dpi0..5 (X) count FF dpiY0..5.
    /// </summary>
    public static bool SetDpi(HidDevice device, IReadOnlyList<int> stages, int enabledStages, int activeStage,
        CancellationToken ct = default)
    {
        if (stages.Count != 6)
            throw new ArgumentException("Exactly 6 DPI stages are required.", nameof(stages));
        foreach (var dpi in stages)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(dpi, MinDpi);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(dpi, MaxDpi);
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(enabledStages, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(enabledStages, 6);
        ArgumentOutOfRangeException.ThrowIfLessThan(activeStage, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(activeStage, enabledStages);

        var args = new List<byte> { (byte)activeStage, (byte)activeStage, 0 };
        foreach (var dpi in stages)
            args.AddRange([(byte)dpi, (byte)(dpi >> 8)]);
        args.Add((byte)enabledStages);
        args.Add(0xFF);
        foreach (var dpi in stages)
            args.AddRange([(byte)dpi, (byte)(dpi >> 8)]);
        return Write(device, LongReport, CmdSetDpi, [.. args], ct);
    }

    /// <summary>Sends a whitelisted write command and waits for the device's acknowledgement.</summary>
    private static bool Write(HidDevice device, byte report, byte cmd, byte[] args, CancellationToken ct)
    {
        if (!WriteCommands.Contains((report, cmd)))
            throw new InvalidOperationException($"Command {report:X2} {cmd:X2} is not an allowed write command.");

        using var stream = Send(device, report, cmd, args, out int inputLen, out _);
        return stream is not null && WaitForReady(stream, inputLen, cmd, ct);
    }

    /// <summary>Opens the channel, drains stale input reports and sends one feature report.</summary>
    private static HidStream? Send(HidDevice device, byte report, byte cmd, byte[] args,
        out int inputLen, out int featureLen)
    {
        featureLen = device.GetMaxFeatureReportLength();
        inputLen = Math.Max(device.GetMaxInputReportLength(), 2);
        int dataLen = report == LongReport ? 64 : 20;
        if (featureLen < dataLen + 1 || args.Length > dataLen - 1)
            return null;

        var stream = device.Open();
        try
        {
            stream.ReadTimeout = (int)InputPollTimeout.TotalMilliseconds;
            DrainInput(stream, inputLen);

            var request = new byte[featureLen];
            request[0] = report;
            for (int i = 1; i <= dataLen; i++)
                request[i] = 0xFF; // 0x00 ^ 0xFF
            request[1] = (byte)(cmd ^ 0xFF);
            for (int i = 0; i < args.Length; i++)
                request[2 + i] = (byte)(args[i] ^ 0xFF);
            stream.SetFeature(request);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sends a read command and returns the reply body (after XOR, without report ID and command
    /// byte), or null when the device does not answer.
    /// </summary>
    private static byte[]? Query(HidDevice device, byte report, byte cmd, byte[] args,
        CancellationToken ct, Func<byte[], bool>? accept = null)
    {
        if (!ReadCommands.Contains((report, cmd)))
            throw new InvalidOperationException($"Command {report:X2} {cmd:X2} is not an allowed read command.");

        int dataLen = report == LongReport ? 64 : 20;
        using var stream = Send(device, report, cmd, args, out int inputLen, out int featureLen);
        if (stream is null)
            return null;

        bool ready = WaitForReady(stream, inputLen, cmd, ct);

        // Without a ready signal the GET_FEATURE most likely returns just the echo of the request,
        // so in that case only accept a reply that passes the caller's validation.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var buf = new byte[featureLen];
            buf[0] = report;
            stream.GetFeature(buf);

            if (buf[0] == report && (byte)(buf[1] ^ 0xFF) == cmd)
            {
                var body = new byte[dataLen - 1];
                for (int i = 0; i < body.Length; i++)
                    body[i] = (byte)(buf[i + 2] ^ 0xFF);
                if (accept?.Invoke(body) ?? ready)
                    return body;
            }
            Wait(TimeSpan.FromMilliseconds(ready ? 20 : 200), ct);
        }
        return null;
    }

    private static bool WaitForReady(HidStream stream, int inputLen, byte cmd, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReadyTimeout;
        var buf = new byte[inputLen];
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n >= 3 && buf[0] == ReadyReport
                    && (byte)(buf[1] ^ 0xFF) == ReadyMarker && (byte)(buf[2] ^ 0xFF) == cmd)
                    return true;
            }
            catch (TimeoutException)
            {
                // keep waiting
            }
        }
        return false;
    }

    private static void DrainInput(HidStream stream, int inputLen)
    {
        var buf = new byte[inputLen];
        try
        {
            for (int i = 0; i < 32; i++)
                _ = stream.Read(buf, 0, buf.Length); // discard stale reports
        }
        catch (TimeoutException)
        {
            // queue empty
        }
    }

    private static MouseStatus ParseStatus(string productName, ushort channelPid, string channelPath, byte[] b)
    {
        var s = b.AsSpan();
        return new MouseStatus(
            ProductName: productName,
            ChannelProductId: channelPid,
            ChannelPath: channelPath,
            MouseVendorId: BinaryPrimitives.ReadUInt16LittleEndian(s),
            MouseProductId: BinaryPrimitives.ReadUInt16LittleEndian(s[2..]),
            Firmware: BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
            Flags: s[8],
            BatteryPercent: s[9],
            ChargeRaw: s[10],
            Raw: b);
    }

    private static string? ParseVersion(byte[]? body)
    {
        if (body is null || body.Length < 2 || body[0] == 0 || body[0] >= body.Length)
            return null;
        var text = Encoding.ASCII.GetString(body, 1, body[0]).Trim('\0', ' ');
        return text.Length > 0 && text.All(c => char.IsAsciiDigit(c) || c == '.') ? text : null;
    }

    private static string GetProductName(HidDevice device)
    {
        try
        {
            var name = device.GetProductName()?.Trim();
            if (!string.IsNullOrEmpty(name))
                return name;
        }
        catch
        {
            // No name in the descriptor - use a fallback.
        }
        return $"MCHOSE mouse ({device.VendorID:X4}:{device.ProductID:X4})";
    }

    private static void Wait(TimeSpan delay, CancellationToken ct)
    {
        if (ct.WaitHandle.WaitOne(delay))
            ct.ThrowIfCancellationRequested();
    }
}
