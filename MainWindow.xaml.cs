using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MCHOSE.Protocol;

namespace MCHOSE;

/// <summary>Details and settings window. After closing it the app keeps running in the tray.</summary>
public partial class MainWindow : Window
{
    private static readonly int[] SleepOptions = [0, 1, 2, 3, 5, 10, 15, 20, 30, 45, 60, 90, 100];

    private readonly AppSettings _settings;
    private bool _loading;
    private string _shownConfigKey = "";
    private MouseConfig? _config;
    private readonly List<TextBox> _dpiBoxes = [];

    public event Action? RefreshRequested;
    public event Action? SettingsChanged;

    /// <summary>Asks the app to send a setting change to the mouse; completes with true on success.</summary>
    public Func<MouseWrite, Task<bool>>? ApplyRequested { get; set; }

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        LoadSettings();
        AboutTitleText.Text = $"{Texts.AppName} {Texts.Version}";
        AboutAuthorRun.Text = $"© {Texts.Author}";
        RepoLinkRun.Text = "GitHub";
        RepoLink.ToolTip = Texts.RepositoryUrl;
    }

    /// <summary>Opens the project page in the default browser (only on click - the app itself never goes online).</summary>
    private void RepoLink_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Texts.RepositoryUrl) { UseShellExecute = true }); }
        catch (Exception) { /* no browser registered - nothing to do */ }
    }

    // ---------------------------------------------------------------- app settings page

    private void LoadSettings()
    {
        _loading = true;
        NotificationsBox.IsChecked = _settings.Notifications;
        AnimateChargingBox.IsChecked = _settings.AnimateCharging;
        Fill(LowBox, AppSettings.LowThresholdOptions, _settings.LowThreshold, v => $"{v}%");
        Fill(CriticalBox, AppSettings.CriticalThresholdOptions, _settings.CriticalThreshold, v => $"{v}%");
        Fill(IntervalBox, AppSettings.PollIntervalOptions, _settings.PollIntervalSeconds,
            v => v < 60 ? $"every {v} s" : $"every {v / 60} min");
        AutostartBox.IsChecked = Autostart.IsEnabled;
        _loading = false;
    }

    private static void Fill(ComboBox box, int[] options, int current, Func<int, string> label)
    {
        var values = options.Contains(current) ? options : options.Append(current).Order().ToArray();
        box.Items.Clear();
        foreach (var v in values)
        {
            var item = new ComboBoxItem { Content = label(v), Tag = v };
            box.Items.Add(item);
            if (v == current) box.SelectedItem = item;
        }
    }

    private static int Selected(ComboBox box, int fallback) =>
        box.SelectedItem is ComboBoxItem { Tag: int v } ? v : fallback;

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Notifications = NotificationsBox.IsChecked == true;
        _settings.AnimateCharging = AnimateChargingBox.IsChecked == true;
        _settings.LowThreshold = Selected(LowBox, _settings.LowThreshold);
        _settings.CriticalThreshold = Math.Min(Selected(CriticalBox, _settings.CriticalThreshold), _settings.LowThreshold);
        _settings.PollIntervalSeconds = Selected(IntervalBox, _settings.PollIntervalSeconds);
        _settings.Save();
        SettingsChanged?.Invoke();
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Autostart.Set(AutostartBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not change the startup setting:\n{ex.Message}", Texts.AppName);
        }
        AutostartBox.IsChecked = Autostart.IsEnabled;
    }

    // ---------------------------------------------------------------- battery page / header

    public void Update(MonitorSnapshot s)
    {
        DeviceNameRun.Text = Texts.DeviceName(s);
        Title = $"{Texts.DeviceName(s)} - {Texts.AppName}";
        StateRun.Text = Texts.State(s);

        if (s.LastValid is not { } st)
        {
            PercentText.Text = "–";
            ChargeText.Text = "";
            BatteryBar.Value = 0;
            ConnectionText.Text = DeviceText.Text = FirmwareText.Text = LastReadText.Text = "–";
            ShowMouseSettings(null, null, live: false);
            return;
        }

        bool live = s.State == MonitorState.Ok;
        if (live)
            StateRun.Text = st.Connection == ConnectionType.Usb ? "Connected via USB cable" : $"Connected ({Texts.Connection(st.Connection)})";
        PercentText.Text = st.BatteryPercent.ToString();
        ChargeText.Text = !live ? "" : st.IsFull ? "✓ Fully charged" : st.IsCharging ? "⚡ Charging" : "On battery";
        BatteryBar.Value = st.BatteryPercent;
        ConnectionText.Text = live ? Texts.Connection(st.Connection) : "–";
        DeviceText.Text = $"{st.MouseVendorId:X4}:{st.MouseProductId:X4}" +
                          (st.ChannelProductId != st.MouseProductId ? $" (receiver {MchoseDevice.VendorId:X4}:{st.ChannelProductId:X4})" : "");
        FirmwareText.Text = s.Firmware is { Mouse: not null } fw
            ? fw.Receiver is null ? fw.Mouse : $"{fw.Mouse} · receiver {fw.Receiver}"
            : st.FirmwareText;
        LastReadText.Text = s.LastValidAt?.ToString("HH:mm:ss") ?? "–";
        ShowMouseSettings(s.Config, st, live);
    }

    // ---------------------------------------------------------------- mouse page

    /// <summary>
    /// Builds the "Mouse" page. Rebuilt only when the config (or connection) actually changes,
    /// so an open drop-down is not closed by the periodic battery poll.
    /// </summary>
    private void ShowMouseSettings(MouseConfig? c, MouseStatus? st, bool live)
    {
        string key = c is null || st is null ? "" : $"{Convert.ToHexString(c.Raw)}|{st.Connection}|{st.ChannelProductId}|{live}";
        if (key == _shownConfigKey && MouseSettingsGrid.Children.Count > 0)
            return;
        _shownConfigKey = key;
        _config = c;

        var grid = MouseSettingsGrid;
        grid.Children.Clear();
        grid.RowDefinitions.Clear();

        if (c is null || st is null)
        {
            AddRow("Status", Text("Not read yet"));
            DpiCard.Visibility = Visibility.Collapsed;
            return;
        }

        // Writes need an awake mouse; otherwise show the last known values read-only.
        MouseSettingsRoot.IsEnabled = live;
        var connection = st.Connection;
        var inv = CultureInfo.InvariantCulture;

        AddRow("Mode", Combo(
            [("Performance", 0), ("Gaming", 1), ("Extreme", 2)], (int)c.Mode,
            v => (d, _, cfg, ct) => MchoseDevice.SetMode(d, (SensorMode)v, cfg, ct)));

        // Each link has its own polling rate; the command changes the one it is sent through.
        // The 1K receiver (100A) only offers 125/500/1000 Hz.
        bool viaCable = connection == ConnectionType.Usb;
        int maxRateIndex = st.ChannelProductId == 0x100A ? 2 : MouseConfig.PollingRates.Length - 1;
        AddRow(viaCable ? "Polling rate (cable)" : "Polling rate (2.4 GHz)", Combo(
            Enumerable.Range(0, maxRateIndex + 1).Select(i => ($"{MouseConfig.PollingRates[i]} Hz", i)).ToArray(),
            viaCable ? c.UsbRateIndex : c.WirelessRateIndex,
            v => (d, _, _, ct) => MchoseDevice.SetPollingRate(d, v, viaCable, ct)));

        AddRow("Lift-off distance", Combo(
            MouseConfig.LodValues.Select((mm, i) => ($"{mm.ToString("0.0", inv)} mm", i)).ToArray(), c.LodIndex,
            v => (d, _, _, ct) => MchoseDevice.SetLiftOff(d, v, ct)));

        AddRow("Motion sync", Toggle(c.MotionSync, SensorToggle.MotionSync));
        AddRow("Angle snapping", Toggle(c.AngleSnapping, SensorToggle.AngleSnapping));
        AddRow("Ripple control", Toggle(c.RippleControl, SensorToggle.Ripple));

        AddRow("Key debounce", Combo(
            Enumerable.Range(0, MchoseDevice.MaxDebounceMs + 1).Select(ms => ($"{ms} ms", ms)).ToArray(), c.DebounceMs,
            v => (d, _, _, ct) => MchoseDevice.SetDebounce(d, v, ct)));

        AddRow("Sleep", Combo(
            SleepOptions.Union([c.SleepMinutes]).Order().Select(m => (m == 0 ? "Never" : $"after {m} min", m)).ToArray(),
            c.SleepMinutes,
            v => (d, _, _, ct) => MchoseDevice.SetSleep(d, v, ct)));

        AddRow("Sensor rotation", Combo(
            Enumerable.Range(-MchoseDevice.MaxRotation, 2 * MchoseDevice.MaxRotation + 1).Select(deg => ($"{deg}°", deg)).ToArray(),
            c.RotationDegrees,
            v => (d, _, cfg, ct) => MchoseDevice.SetRotation(d, v, cfg, ct)));

        ShowDpi(c, connection);
    }

    private void AddRow(string label, FrameworkElement value)
    {
        var grid = MouseSettingsGrid;
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, Style = (Style)FindResource("Label") };
        Grid.SetRow(l, row);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(l);
        grid.Children.Add(value);
    }

    private TextBlock Text(string text) => new() { Text = text, Style = (Style)FindResource("Value") };

    /// <summary>A drop-down that sends <paramref name="write"/> for the chosen value.</summary>
    private ComboBox Combo((string Label, int Value)[] options, int current, Func<int, MouseWrite> write)
    {
        var box = new ComboBox { MinWidth = 140, Margin = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (label, value) in options)
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            box.Items.Add(item);
            if (value == current) box.SelectedItem = item;
        }
        box.SelectionChanged += async (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: int v, Content: string label } && v != current)
                await Apply(write(v), label);
        };
        return box;
    }

    private CheckBox Toggle(bool current, SensorToggle toggle)
    {
        var box = new CheckBox { IsChecked = current, Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
        box.Click += async (_, _) =>
        {
            bool on = box.IsChecked == true;
            await Apply((d, _, cfg, ct) => MchoseDevice.SetToggle(d, toggle, on, cfg, ct), on ? "On" : "Off");
        };
        return box;
    }

    /// <summary>Sends a change through the app's mouse monitor and reports the result.</summary>
    private async Task Apply(MouseWrite write, string what)
    {
        if (ApplyRequested is null) return;
        MouseSettingsRoot.IsEnabled = false;
        MouseHintText.Text = "Saving…";
        bool ok = await ApplyRequested(write);
        MouseHintText.Text = ok
            ? $"Saved ({what})."
            : "The mouse did not confirm the change. Make sure it is awake and connected.";
        MouseSettingsRoot.IsEnabled = true;
        // Rebuild from the config the mouse reports after the write (arrives through Update()).
        _shownConfigKey = "";
    }

    // ---------------------------------------------------------------- DPI card

    private void ShowDpi(MouseConfig c, ConnectionType connection)
    {
        DpiCard.Visibility = Visibility.Visible;
        _loading = true;

        int count = Math.Clamp(c.EnabledStages, 1, 6);
        DpiCountBox.Items.Clear();
        for (int n = 1; n <= 6; n++)
            DpiCountBox.Items.Add(new ComboBoxItem { Content = n.ToString(), Tag = n });
        DpiCountBox.SelectedIndex = count - 1;
        FillActiveStages(count, Math.Min(c.DpiStage(connection), count - 1));

        DpiStagesPanel.Children.Clear();
        _dpiBoxes.Clear();
        for (int i = 0; i < 6; i++)
        {
            var box = new TextBox
            {
                Text = c.DpiX[i].ToString(),
                Width = 76,
                Margin = new Thickness(0, 2, 6, 2),
                ToolTip = $"Stage {i + 1}",
            };
            box.TextChanged += (_, _) => Dpi_Edited(box, new RoutedEventArgs());
            _dpiBoxes.Add(box);
            DpiStagesPanel.Children.Add(box);
        }
        UpdateDpiStageBoxes();

        bool editable = c.DpiAxesEqual;
        DpiCountBox.IsEnabled = DpiActiveBox.IsEnabled = DpiStagesPanel.IsEnabled = editable;
        DpiHintText.Text = editable
            ? $"{MchoseDevice.MinDpi} – {MchoseDevice.MaxDpi} DPI"
            : "Separate X/Y DPI values can't be edited here yet.";
        DpiApplyButton.IsEnabled = false;
        _loading = false;
    }

    private void FillActiveStages(int count, int active)
    {
        DpiActiveBox.Items.Clear();
        for (int n = 1; n <= count; n++)
            DpiActiveBox.Items.Add(new ComboBoxItem { Content = $"Stage {n}", Tag = n - 1 });
        DpiActiveBox.SelectedIndex = Math.Clamp(active, 0, count - 1);
    }

    private void UpdateDpiStageBoxes()
    {
        int count = DpiCountBox.SelectedIndex + 1;
        for (int i = 0; i < _dpiBoxes.Count; i++)
            _dpiBoxes[i].Opacity = i < count ? 1.0 : 0.45;
    }

    private void Dpi_Edited(object sender, RoutedEventArgs e)
    {
        if (_loading || _config is null) return;

        if (sender == DpiCountBox)
        {
            _loading = true;
            int count = DpiCountBox.SelectedIndex + 1;
            FillActiveStages(count, Math.Min(DpiActiveBox.SelectedIndex, count - 1));
            _loading = false;
        }
        UpdateDpiStageBoxes();

        bool valid = TryReadDpi(out _, out string? error);
        DpiHintText.Text = error ?? $"{MchoseDevice.MinDpi} – {MchoseDevice.MaxDpi} DPI";
        DpiApplyButton.IsEnabled = valid;
    }

    private bool TryReadDpi(out int[] stages, out string? error)
    {
        stages = new int[6];
        for (int i = 0; i < 6; i++)
        {
            if (!int.TryParse(_dpiBoxes[i].Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int v)
                || v < MchoseDevice.MinDpi || v > MchoseDevice.MaxDpi)
            {
                error = $"Stage {i + 1}: enter a value between {MchoseDevice.MinDpi} and {MchoseDevice.MaxDpi}.";
                return false;
            }
            stages[i] = v;
        }
        error = null;
        return true;
    }

    private async void DpiApply_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadDpi(out var stages, out _)) return;
        int count = DpiCountBox.SelectedIndex + 1;
        int active = Math.Max(DpiActiveBox.SelectedIndex, 0);
        await Apply((d, _, _, ct) => MchoseDevice.SetDpi(d, stages, count, active, ct),
            $"{stages[active]} DPI, stage {active + 1} of {count}");
    }

    // ---------------------------------------------------------------- navigation

    /// <summary>Switches the page shown in the content area (NavigationView-style).</summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (BatteryPage is null) return; // fired during InitializeComponent
        BatteryPage.Visibility = sender == NavBattery ? Visibility.Visible : Visibility.Collapsed;
        MousePage.Visibility = sender == NavMouse ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = sender == NavSettings ? Visibility.Visible : Visibility.Collapsed;
        PageTitleText.Text = (string)((RadioButton)sender).Content;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        StateRun.Text = "Refreshing…";
        RefreshRequested?.Invoke();
    }
}
