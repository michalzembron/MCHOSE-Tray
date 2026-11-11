// Diagnostics: lists MCHOSE channels and, for each one that answers, the status,
// firmware versions and configuration block. Read-only unless --set-debounce is given.
using MCHOSE.Protocol;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var channels = MchoseDevice.FindChannels();

// --set-debounce <ms>: write test through the library, then report which config bytes changed.
if (args is ["--set-debounce", var msText] && int.TryParse(msText, out int newMs))
{
    foreach (var dev in channels)
    {
        if (MchoseDevice.ReadStatus(dev) is null) continue;
        var before = MchoseDevice.ReadConfig(dev)!;
        bool ok = MchoseDevice.SetDebounce(dev, newMs);
        Thread.Sleep(100);
        var after = MchoseDevice.ReadConfig(dev)!;
        var changed = Enumerable.Range(0, MouseConfig.Length).Where(i => before.Raw[i] != after.Raw[i])
            .Select(i => $"{i}:{before.Raw[i]:X2}->{after.Raw[i]:X2}");
        Console.WriteLine($"{dev.ProductID:X4}: ack {ok}, debounce {before.DebounceMs} -> {after.DebounceMs} ms, changed bytes [{string.Join(", ", changed)}]");
        return ok && after.DebounceMs == newMs ? 0 : 2;
    }
    Console.WriteLine("Mouse not found.");
    return 1;
}

if (channels.Count == 0)
{
    Console.WriteLine("No MCHOSE 0xFF01 channel found.");
    return 1;
}
foreach (var dev in channels)
{
    Console.WriteLine($"== {dev.VendorID:X4}:{dev.ProductID:X4}  {dev.DevicePath}");
    var s = MchoseDevice.ReadStatus(dev);
    if (s is null) { Console.WriteLine("   no response"); continue; }
    Console.WriteLine($"   {s.ProductName}: mouse {s.MouseVendorId:X4}:{s.MouseProductId:X4}");
    Console.WriteLine($"   connection {s.Connection} (flags 0x{s.Flags:X2})  battery {s.BatteryPercent}%  charging {s.ChargeRaw}");
    Console.WriteLine($"   status raw {Convert.ToHexString(s.Raw)}");

    var fw = MchoseDevice.ReadFirmware(dev);
    Console.WriteLine($"   firmware: mouse {fw.Mouse ?? "?"}, receiver {fw.Receiver ?? "-"}");

    var c = MchoseDevice.ReadConfig(dev);
    if (c is null) { Console.WriteLine("   config: no response"); continue; }
    Console.WriteLine($"   mode {c.Mode}, DPI {c.ActiveDpi(s.Connection)} (stage {c.DpiStage(s.Connection) + 1}/{c.EnabledStages}), " +
                      $"stages X [{string.Join(", ", c.DpiX)}] Y [{string.Join(", ", c.DpiY)}]");
    Console.WriteLine($"   polling {c.PollingRateHz(s.Connection)} Hz (USB idx {c.UsbRateIndex}, 2.4G idx {c.WirelessRateIndex}), LOD {c.LiftOffMm} mm");
    Console.WriteLine($"   motion sync {c.MotionSync}, angle snapping {c.AngleSnapping}, ripple {c.RippleControl}");
    Console.WriteLine($"   debounce {c.DebounceMs} ms, sleep {c.SleepMinutes} min, rotation {c.RotationDegrees}°");
    Console.WriteLine($"   config raw {Convert.ToHexString(c.Raw)}");
}
return 0;
