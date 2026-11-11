using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Win32;

namespace MCHOSE;

/// <summary>
/// The app lives in the system tray: the icon shows the battery percentage,
/// clicking it opens the details and settings window.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = @"Local\MchoseTray";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private TaskbarIcon? _tray;
    private MainWindow? _window;
    private MouseMonitor? _monitor;
    private AppSettings _settings = new();
    private TrayIconModel? _iconModel;
    private System.Drawing.Icon[]? _chargeFrames;
    private int _chargeFrame;
    private DispatcherTimer? _chargeTimer;
    private MonitorSnapshot _last = new(MonitorState.Searching, null, null);

    // Notification state - so the same warning is not repeated on every reading.
    private int _notifiedLevel = int.MaxValue;
    private bool _notifiedFull;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args is ["--export-icons", var dir])
        {
            TrayIconRenderer.ExportSamples(dir);
            Shutdown();
            return;
        }

        // Second instance: ask the running one to show its window, then exit.
        _singleInstance = new Mutex(true, InstanceName, out bool isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        if (!isFirst)
        {
            // We were started by the user, so we may hand the foreground right to the running instance.
            AllowSetForegroundWindow(ASFW_ANY);
            _showSignal.Set();
            Shutdown();
            return;
        }
        ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, Timeout.Infinite, executeOnlyOnce: false);

        ThemeMode = ThemeMode.System; // Fluent (Windows 11): Mica, light/dark following the system, accent colour
        _settings = AppSettings.Load();

        _tray = new TaskbarIcon
        {
            ToolTipText = Texts.AppName,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(ShowWindow),
            ContextMenu = BuildMenu(),
        };
        UpdateTray(_last, force: true);
        _tray.ForceCreate(enablesEfficiencyMode: false);

        // Redraw the icon when the taskbar theme (light/dark) or display scaling changes.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _monitor = new MouseMonitor { PollInterval = TimeSpan.FromSeconds(_settings.PollIntervalSeconds) };
        _monitor.Updated += s => Dispatcher.BeginInvoke(() => OnUpdated(s));
        _monitor.Start();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var show = new MenuItem { Header = "Show details" };
        show.Click += (_, _) => ShowWindow();

        var refresh = new MenuItem { Header = "Refresh now" };
        refresh.Click += (_, _) => _monitor?.RefreshAll();

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Shutdown();

        menu.Items.Add(show);
        menu.Items.Add(refresh);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }

    /// <summary>
    /// The window is created only when opened and released when closed -
    /// in the background only the tray icon and battery polling are running.
    /// </summary>
    private void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(_settings);
            _window.RefreshRequested += () => _monitor?.RefreshAll();
            _window.SettingsChanged += OnSettingsChanged;
            _window.ApplyRequested = write => _monitor?.ApplyAsync(write) ?? Task.FromResult(false);
            _window.Closed += (_, _) => _window = null;
            _window.Update(_last);
            _monitor?.RefreshAll(); // show fresh mouse settings when the window opens
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnSettingsChanged()
    {
        if (_monitor is not null)
            _monitor.PollInterval = TimeSpan.FromSeconds(_settings.PollIntervalSeconds);
        UpdateTray(_last, force: true);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => UpdateTray(_last, force: true));

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => UpdateTray(_last, force: true));

    private void OnUpdated(MonitorSnapshot s)
    {
        _last = s;
        UpdateTray(s);
        _window?.Update(s);
        if (s.State == MonitorState.Ok && s.LastValid is { } st)
            Notify(st.BatteryPercent, st.IsOnCharger, st.IsFull);
    }

    private void UpdateTray(MonitorSnapshot s, bool force = false)
    {
        if (_tray is null) return;
        _tray.ToolTipText = Texts.ToolTip(s);

        var st = s.LastValid;
        bool live = s.State == MonitorState.Ok;
        var model = st is null
            ? new TrayIconModel("?", 0, BarKind.None, Stale: true, Alert: false)
            : new TrayIconModel(
                st.BatteryPercent.ToString(),
                st.BatteryPercent,
                st.IsFull && live ? BarKind.Full          // static green bar + green bolt: safe to unplug
                : st.IsCharging && live ? BarKind.Charging
                : st.BatteryPercent <= _settings.LowThreshold ? BarKind.Low
                : st.BatteryPercent <= 50 ? BarKind.Medium
                : BarKind.Good,
                Stale: !live,
                Alert: live && !st.IsOnCharger && st.BatteryPercent <= _settings.LowThreshold);

        if (!force && model == _iconModel) return;
        _iconModel = model;

        StopChargeAnimation();
        if (model.Bar == BarKind.Charging && _settings.AnimateCharging)
            StartChargeAnimation(model);
        else
            ShowStaticIcon(model); // when charging without animation: blue bar at the real level + bolt
    }

    private void ShowStaticIcon(TrayIconModel model)
    {
        // The Icon property owns its value: H.NotifyIcon disposes the previous icon when it changes.
        _tray!.Icon = TrayIconRenderer.Render(model);
    }

    // Charging animation: the bar fills smoothly 0 → 100 %, holds briefly, then restarts from 0.
    // All frames are rendered once per state; the timer only swaps ready-made icons
    // (a handful of tray updates per second, no drawing).
    private const int ChargeFillMs = 2000;

    private void StartChargeAnimation(TrayIconModel model)
    {
        _chargeFrames = TrayIconRenderer.RenderChargingFrames(model);
        _chargeFrame = 0;
        _chargeTimer ??= CreateChargeTimer();
        // One frame per pixel of bar width while filling (icon size + 1 frames).
        _chargeTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(60, ChargeFillMs / (TrayIconRenderer.IconSize + 1)));
        OnChargeTick(null, EventArgs.Empty);
        _chargeTimer.Start();
    }

    private DispatcherTimer CreateChargeTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background);
        timer.Tick += OnChargeTick;
        return timer;
    }

    private void OnChargeTick(object? sender, EventArgs e)
    {
        if (_tray is null || _chargeFrames is not { Length: > 0 } frames) return;
        // UpdateIcon only pushes the handle to the tray. Assigning the Icon property would make
        // H.NotifyIcon dispose the previous icon - fatal for frames that are reused every cycle.
        _tray.UpdateIcon(frames[_chargeFrame]);
        _chargeFrame = (_chargeFrame + 1) % frames.Length;
    }

    private void StopChargeAnimation()
    {
        _chargeTimer?.Stop();
        if (_chargeFrames is not { } frames) return;
        _chargeFrames = null;
        // Dispose after the tray has been switched to the next icon.
        Dispatcher.BeginInvoke(() => { foreach (var f in frames) f.Dispose(); }, DispatcherPriority.Background);
    }

    /// <param name="charging">Connected to power (charging or already full).</param>
    /// <param name="full">Connected to power and fully charged.</param>
    private void Notify(int percent, bool charging, bool full)
    {
        if (_tray is null) return;

        if (charging || percent > _settings.LowThreshold)
            _notifiedLevel = int.MaxValue;
        if (!full)
            _notifiedFull = false;

        if (!_settings.Notifications) return;

        if (!charging && percent <= _settings.CriticalThreshold && _notifiedLevel > _settings.CriticalThreshold)
        {
            _notifiedLevel = _settings.CriticalThreshold;
            _tray.ShowNotification(Texts.AppName, $"Battery critically low: {percent}%. Plug in the cable.", NotificationIcon.Error);
        }
        else if (!charging && percent <= _settings.LowThreshold && _notifiedLevel > _settings.LowThreshold)
        {
            _notifiedLevel = _settings.LowThreshold;
            _tray.ShowNotification(Texts.AppName, $"Low battery: {percent}%.", NotificationIcon.Warning);
        }
        else if (full && !_notifiedFull)
        {
            _notifiedFull = true;
            _tray.ShowNotification(Texts.AppName, "Mouse fully charged - you can unplug the cable.", NotificationIcon.Info);
        }
    }

    private const int ASFW_ANY = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _chargeTimer?.Stop();
        _monitor?.Dispose();
        _tray?.Dispose();
        _showSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
