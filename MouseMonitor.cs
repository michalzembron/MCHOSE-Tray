using System.Collections.Concurrent;
using HidSharp;
using MCHOSE.Protocol;

namespace MCHOSE;

public enum MonitorState
{
    /// <summary>No reading yet.</summary>
    Searching,
    /// <summary>Neither a receiver nor a cabled mouse is present.</summary>
    NoDevice,
    /// <summary>The receiver is present but the mouse does not respond (off / out of range).</summary>
    NoResponse,
    /// <summary>The mouse responds but is asleep - the last known battery level is shown.</summary>
    Sleeping,
    Ok,
}

/// <param name="State">Current monitor state.</param>
/// <param name="LastValid">Last trustworthy battery reading (may be from before the mouse fell asleep).</param>
/// <param name="LastValidAt">Time of the last trustworthy reading.</param>
/// <param name="Config">Last configuration read from the mouse (DPI, polling rate, …).</param>
/// <param name="Firmware">Firmware versions of the mouse and the receiver.</param>
public sealed record MonitorSnapshot(
    MonitorState State,
    MouseStatus? LastValid,
    DateTime? LastValidAt,
    MouseConfig? Config = null,
    FirmwareInfo? Firmware = null);

/// <summary>A setting write executed on the monitor thread with the current status and config.</summary>
public delegate bool MouseWrite(HidDevice device, MouseStatus status, MouseConfig config, CancellationToken ct);

/// <summary>
/// Talks to the mouse on a single background thread (so commands never overlap - setting
/// changes are queued and executed on the same thread).
/// The battery is polled periodically; the configuration and firmware versions are read only
/// on connect, when the USB devices change, or on request - not on every poll.
/// </summary>
public sealed class MouseMonitor : IDisposable
{
    private static readonly TimeSpan DeviceSettleDelay = TimeSpan.FromMilliseconds(1500);

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _trigger = new(0);
    private MouseStatus? _lastValid;
    private DateTime? _lastValidAt;
    private string? _lastChannelPath;
    private MouseConfig? _config;
    private FirmwareInfo? _firmware;
    private volatile bool _detailsRequested = true;
    private readonly ConcurrentQueue<PendingWrite> _writes = new();

    private sealed record PendingWrite(MouseWrite Execute, TaskCompletionSource<bool> Done);
    private Task? _loop;

    /// <summary>Raised on a background thread after every reading.</summary>
    public event Action<MonitorSnapshot>? Updated;

    public MonitorSnapshot Current { get; private set; } = new(MonitorState.Searching, null, null);

    /// <summary>Time between battery readings; a change takes effect from the next cycle.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    public void Start()
    {
        DeviceList.Local.Changed += OnDeviceListChanged;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Reads the battery now.</summary>
    public void RefreshNow() => _trigger.Release();

    /// <summary>Reads the battery, configuration and firmware versions now.</summary>
    public void RefreshAll()
    {
        _detailsRequested = true;
        _trigger.Release();
    }

    /// <summary>
    /// Queues a setting change. It runs on the monitor thread with the current status and config
    /// (e.g. the 0x42 sensor command needs the current LOD); completes with true when the mouse
    /// acknowledged it. The config is re-read afterwards, so the UI shows what the mouse really stores.
    /// </summary>
    public Task<bool> ApplyAsync(MouseWrite write)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _writes.Enqueue(new PendingWrite(write, done));
        _detailsRequested = true; // re-read the config so the UI shows what the mouse really stores
        _trigger.Release();
        return done.Task;
    }

    private void OnDeviceListChanged(object? sender, DeviceListChangedEventArgs e) => RefreshAll();

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Current = Poll(ct);
                Updated?.Invoke(Current);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A single failed reading must not stop the monitor.
            }

            try
            {
                if (await _trigger.WaitAsync(PollInterval, ct) && _writes.IsEmpty)
                {
                    // USB events arrive in bursts - wait for the device to settle.
                    await Task.Delay(DeviceSettleDelay, ct);
                    while (_trigger.CurrentCount > 0)
                        _trigger.Wait(0);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private MonitorSnapshot Poll(CancellationToken ct)
    {
        if (MchoseDevice.FindChannels().Count == 0)
        {
            FailPendingWrites();
            return Snapshot(MonitorState.NoDevice);
        }

        var status = MchoseDevice.ReadStatus(_lastChannelPath, ct);
        if (status is null)
        {
            FailPendingWrites();
            return Snapshot(MonitorState.NoResponse);
        }

        if (status.ChannelPath != _lastChannelPath)
        {
            // Switched between cable and receiver - the receiver firmware etc. may differ.
            _lastChannelPath = status.ChannelPath;
            _firmware = null;
            _detailsRequested = true;
        }

        if (!status.IsBatteryValid)
        {
            FailPendingWrites();
            return Snapshot(MonitorState.Sleeping);
        }

        _lastValid = status;
        _lastValidAt = DateTime.Now;

        ExecutePendingWrites(status.ChannelPath, ct);

        if (_detailsRequested || _config is null)
            ReadDetails(status.ChannelPath, ct);

        return Snapshot(MonitorState.Ok);
    }

    private void ExecutePendingWrites(string channelPath, CancellationToken ct)
    {
        if (_writes.IsEmpty)
            return;
        var dev = MchoseDevice.FindChannel(channelPath);
        while (_writes.TryDequeue(out var w))
        {
            try
            {
                w.Done.TrySetResult(dev is not null && _lastValid is { } status && _config is { } config
                                    && w.Execute(dev, status, config, ct));
            }
            catch (OperationCanceledException)
            {
                w.Done.TrySetCanceled();
                throw;
            }
            catch
            {
                w.Done.TrySetResult(false);
            }
            ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(30)); // give the mouse a breather
        }
    }

    private void FailPendingWrites()
    {
        while (_writes.TryDequeue(out var w))
            w.Done.TrySetResult(false);
    }

    private void ReadDetails(string channelPath, CancellationToken ct)
    {
        var dev = MchoseDevice.FindChannel(channelPath);
        if (dev is null)
            return;
        try
        {
            _firmware ??= MchoseDevice.ReadFirmware(dev, ct);
            _config = MchoseDevice.ReadConfig(dev, ct) ?? _config;
            _detailsRequested = false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Keep the previous values; we'll try again on the next request.
        }
    }

    private MonitorSnapshot Snapshot(MonitorState state) =>
        new(state, _lastValid, _lastValidAt, _config, _firmware);

    public void Dispose()
    {
        DeviceList.Local.Changed -= OnDeviceListChanged;
        _cts.Cancel();
        FailPendingWrites();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { /* shutting down */ }
        _cts.Dispose();
        _trigger.Dispose();
    }
}
