using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace AnovaDesk;

public partial class MainWindow : Window
{
    private sealed class StateRow
    {
        public string Key { get; init; } = "";
        public string Value { get; init; } = "";
    }

    private readonly AnovaClient _client = new();
    private readonly ObservableCollection<StateRow> _stateRows = new();
    private readonly ObservableCollection<string> _log = new();

    private AnovaDevice? _selected;
    private IReadOnlyList<AnovaDevice> _devices = Array.Empty<AnovaDevice>();
    private double? _currentC;
    private bool _initializing = true;
    private bool _deviceSupported;
    private bool _autoSwitchDone;
    private bool _unitSynced;
    private DateTime? _startRequestAt;
    private bool _cookObserved;
    private string? _stickyAlert;
    private DateTime _stickyUntil;
    private readonly CookNotificationRules _cookRules = new();
    private Notifier? _notifier;
    private bool _allowClose;
    private bool _trayHintShown;
    private bool _closePromptActive;

    public MainWindow()
    {
        InitializeComponent();

        StateList.ItemsSource = _stateRows;
        LogList.ItemsSource = _log;

        _client.Log += s => Dispatcher.BeginInvoke(() => AddLog(s));
        _client.DevicesChanged += list => Dispatcher.BeginInvoke(() => UpdateDevices(list));
        _client.MessageReceived += (cmd, payload) => Dispatcher.BeginInvoke(() => OnMessage(cmd, payload));
        _client.ConnectionChanged += (ok, msg) => Dispatcher.BeginInvoke(() => SetConnection(ok, msg));

        UnitBox.SelectedIndex = 0;
        _initializing = false;
        UpdatePresetLabels();

        Settings.Load();

        _notifier = new Notifier(
            onOpen: RestoreWindow,
            onTest: () => Notify("AnovaDesk", "Notifications are working.", error: false),
            onExit: ExitApplication);

        // Never hold up a Windows shutdown/logoff with the confirmation prompt.
        System.Windows.Application.Current.SessionEnding += (_, _) => _allowClose = true;

        Loaded += OnLoaded;
        Closed += async (_, _) =>
        {
            _notifier?.Dispose();
            await _client.DisposeAsync();
        };
    }

    private void RestoreWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Leaves without the confirmation prompt (tray menu, or the user has already chosen).</summary>
    private void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        // The window's X must not silently quit a running cook, so ask first.
        e.Cancel = true;

        // WPF forbids ShowDialog/Close/Hide from inside Closing ("...while a Window is closing"),
        // so the prompt has to run after this event has unwound.
        if (_closePromptActive) return;
        _closePromptActive = true;
        Dispatcher.BeginInvoke(new Action(AskAboutClosing));
    }

    private void AskAboutClosing()
    {
        try
        {
            var prompt = new ClosePrompt { Owner = this };
            if (prompt.ShowDialog() != true) return;   // dismissed or Cancel

            switch (prompt.Choice)
            {
                case CloseChoice.Exit:
                    ExitApplication();
                    break;

                case CloseChoice.Minimize:
                    Hide();
                    NotifyTrayHintOnce();
                    break;
            }
        }
        finally
        {
            _closePromptActive = false;
        }
    }

    private void NotifyTrayHintOnce()
    {
        if (_trayHintShown) return;
        _trayHintShown = true;
        Notify("Still running",
            "AnovaDesk is in the notification area. Double-click the icon to reopen.",
            error: false);
    }

    private void Notify(string title, string body, bool error)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Notify(title, body, error));
            return;
        }

        var shown = _notifier?.Show(title, body, error) ?? false;
        AddLog($"notify({(shown ? "shown" : "failed")}): {title} \u2014 {body}  [{_notifier?.LastStatus}]");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetControlsEnabled(false);

        if (Environment.GetCommandLineArgs().Contains("--notify-test", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(2500);
                Notify("AnovaDesk test", "If you can see this, notifications are working.", error: false);
            });
        }

        var saved = TokenStore.Load();
        if (!string.IsNullOrWhiteSpace(saved))
        {
            TokenBox.Password = saved;
            AddLog("Loaded saved token from Windows Credential Manager.");
            Connect();
        }
        else
        {
            DeviceHint.Text = "No token registered yet.";
            ShowGetStarted();
        }
    }

    // ------------------------------------------------------------------ connection

    private void Connect_Click(object sender, RoutedEventArgs e) => Connect();

    private void ShowGetStarted()
    {
        GetStartedCard.Visibility = Visibility.Visible;
        ControlCard.Visibility = Visibility.Collapsed;
    }

    private void HideGetStarted()
    {
        GetStartedCard.Visibility = Visibility.Collapsed;
        ControlCard.Visibility = Visibility.Visible;
    }

    private void Connect()
    {
        var token = TokenBox.Password.Trim();
        if (token.Length == 0 || !token.StartsWith("anova-", StringComparison.OrdinalIgnoreCase))
        {
            DeviceHint.Text = "That does not look like a token. Anova tokens start with \"anova-\".";
            return;
        }

        TokenStore.Save(token);
        HideGetStarted();

        if (AppRuntime.Offline)
        {
            DeviceHint.Text = "Offline (test profile): no connection attempted.";
            SetConnection(false, "Offline");
            AddLog("offline mode: connection skipped.");
            return;
        }

        DeviceHint.Text = "Connecting...";
        _client.Start(token);
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        _client.Stop();
        TokenStore.Delete();
        TokenBox.Password = "";
        _stateRows.Clear();
        _selected = null;
        DeviceList.ItemsSource = null;
        TempText.Text = "--.-";
        StatusText.Text = "No device selected.";
        TargetText.Text = "";
        DeviceHint.Text = "No token registered yet.";
        ShowGetStarted();
        SetConnection(false, "Token removed");
        AddLog("Token removed from Credential Manager.");
    }

    private void SetConnection(bool connected, string message)
    {
        StatusDot.Fill = connected
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3D, 0xD5, 0x6B))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x72, 0x80));

        StatusPill.Text = connected ? "Connected" : "Offline";
        SetControlsEnabled(connected && _selected is not null);

        if (!connected && !string.IsNullOrWhiteSpace(message))
            StatusText.Text = message;
    }

    private void Advanced_Toggled(object sender, RoutedEventArgs e)
    {
        // The XAML parser can raise this before the named parts exist.
        if (StatePanel is null || LogPanel is null || StateRowDef is null || LogRow is null) return;

        var on = AdvancedToggle.IsChecked == true;

        StatePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        StateRowDef.Height = on ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        LogPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        LogRow.Height = on ? new GridLength(150) : new GridLength(0);
    }

    private void SetControlsEnabled(bool on)
    {
        var ready = on && _deviceSupported && (_selected?.IsOnline ?? false);
        StartButton.IsEnabled = ready;
        StopButton.IsEnabled = ready;
    }

    // ------------------------------------------------------------------ devices

    private void UpdateDevices(IReadOnlyList<AnovaDevice> devices)
    {
        _devices = devices;
        _autoSwitchDone = false;
        var previous = _selected?.CookerId;
        DeviceList.ItemsSource = devices;

        if (devices.Count == 0)
        {
            DeviceHint.Text = "No cookers returned for this account. Make sure the cooker is paired and online.";
            return;
        }

        DeviceHint.Text = devices.Count == 1
            ? "1 cooker found."
            : $"{devices.Count} cookers found.";

        // Prefer what we were showing, then the last device that actually worked,
        // then any device currently reporting, and only then the first entry.
        var list = devices.ToList();
        var wanted = previous ?? Settings.LastCookerId;

        var index = wanted is null ? -1 : list.FindIndex(d => d.CookerId == wanted);
        if (index < 0) index = list.FindIndex(d => d.IsOnline);
        if (index < 0) index = 0;

        DeviceList.SelectedIndex = index;
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = DeviceList.SelectedItem as AnovaDevice;
        if (_selected is null) return;

        // This build speaks the Precision Cooker (APC) command set only.
        _deviceSupported = string.Equals(_selected.Family, "APC", StringComparison.Ordinal);

        _unitSynced = false;
        _stateRows.Clear();
        _currentC = null;
        AlertText.Visibility = Visibility.Collapsed;
        TempText.Text = "--.-";
        StatusText.Text = _deviceSupported
            ? $"{_selected.Name} \u00B7 {_selected.Type} \u00B7 {_selected.CookerId}"
            : "Precision Oven selected \u2014 oven commands are not implemented in this build.";
        TargetText.Text = "";
        SetControlsEnabled(_client.IsConnected);
        AddLog($"Selected {_selected.Display}");

        if (_selected.IsOnline)
        {
            Settings.LastCookerId = _selected.CookerId;
            Settings.Save();
        }
    }

    // ------------------------------------------------------------------ messages

    private void OnMessage(string command, JsonNode? payload)
    {
        // Live device state: {"cookerId":..,"type":..,"state":{ job, job-status, temperature-info, pin-info, ... }}
        if (payload is JsonObject frame &&
            (command == "EVENT_APC_STATE" || command == "EVENT_APO_STATE"))
        {
            HandleCookerState(frame);
            return;
        }

        if (payload is JsonObject other)
            AddLog($"{command}: {Compact(other)}");
    }

    private void HandleCookerState(JsonObject frame)
    {
        var id = frame["cookerId"]?.GetValue<string>();
        var state = frame["state"] as JsonObject;
        if (string.IsNullOrEmpty(id) || state is null) return;

        var device = _devices.FirstOrDefault(d => d.CookerId == id);
        if (device is not null && !device.IsOnline)
        {
            device.IsOnline = true;

            // First contact: if nothing we are showing is online, follow the device that is.
            if (!_autoSwitchDone && _selected is not null && !_selected.IsOnline)
            {
                _autoSwitchDone = true;
                DeviceList.SelectedItem = device;
            }
        }

        if (_selected?.CookerId != id) return;

        if (Settings.LastCookerId != id)
        {
            Settings.LastCookerId = id;
            Settings.Save();
        }

        RenderState(state);
        SetControlsEnabled(_client.IsConnected);
    }

    private void RenderState(JsonObject state)
    {
        _stateRows.Clear();
        foreach (var row in Flatten(state))
            _stateRows.Add(row);

        // Adopt the unit the device is currently set to, once per selection.
        var deviceUnit = GetString(state, "job", "temperature-unit");
        if (!_unitSynced && deviceUnit is not null)
        {
            _unitSynced = true;
            _initializing = true;
            UnitBox.SelectedIndex = deviceUnit.Equals("F", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _initializing = false;
            TempUnitText.Text = UnitSuffix;
            UpdatePresetLabels();   // adopting the device unit must not leave Celsius captions
        }

        var waterC = GetNumber(state, "temperature-info", "water-temperature");
        if (waterC is not null)
        {
            _currentC = waterC;
            TempText.Text = FormatTemp(waterC.Value);
        }

        var mode = GetString(state, "job", "mode");
        var jobState = GetString(state, "job-status", "state");
        var cooking = string.Equals(mode, "COOK", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(jobState, "PREHEATING", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(jobState, "COOKING", StringComparison.OrdinalIgnoreCase);

        var heaterC = GetNumber(state, "temperature-info", "heater-temperature");
        var elementActive = heaterC is not null && waterC is not null && heaterC.Value - waterC.Value > 0.25;

        var statusParts = new[] { mode, jobState }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        var who = _selected?.Name ?? "Cooker";
        StatusText.Text = statusParts.Count > 0 ? $"{who} \u2014 {string.Join(" / ", statusParts)}" : who;
        if (elementActive)
            StatusText.Text += "  \u00B7  element active";

        // A start that never reaches COOK (or falls straight back out of it) is a failure the
        // device does not report as an error, so surface it rather than showing a calm "IDLE".
        if (_startRequestAt is { } requested)
        {
            var elapsed = (DateTime.UtcNow - requested).TotalSeconds;
            if (cooking)
            {
                _cookObserved = true;
                // Sustained COOK means the cook really is running; stop watching for an abort.
                if (elapsed > 10) _startRequestAt = null;
            }
            else if (elapsed > 5)
            {
                _stickyAlert = _cookObserved
                    ? "Cook stopped immediately after starting \u2014 check the water level and that the cooker sits below the minimum line."
                    : "Cook did not start \u2014 the cooker stayed idle. Check the water level.";
                _stickyUntil = DateTime.UtcNow.AddSeconds(60);

                if (!_cookObserved)
                    Notify("Cook failed to start",
                        "The cooker stayed idle after the start command. Check the water level.", error: true);

                _startRequestAt = null;
            }
        }

        var targetC = GetNumber(state, "job", "target-temperature");
        var remaining = GetNumber(state, "job-status", "cook-time-remaining");
        var parts = new List<string>();
        if (targetC is not null) parts.Add($"Target {FormatTemp(targetC.Value)}{UnitSuffix}");
        if (remaining is > 0 and var r) parts.Add($"Remaining {FormatDuration(r)}");
        TargetText.Text = string.Join("   \u00B7   ", parts);

        UpdateCookNotifications(cooking, waterC, targetC, remaining);

        var alerts = new List<string>();
        AddFlag(state, "pin-info", "water-leak", "water leak", alerts);
        AddFlag(state, "pin-info", "water-level-critical", "water level critical", alerts);
        AddFlag(state, "pin-info", "water-temp-too-high", "water too hot", alerts);
        AddFlag(state, "pin-info", "device-safe", "device fault", alerts);

        if (!cooking && elementActive)
            alerts.Add("Heating element is running while the job reads idle \u2014 check the water level.");

        if (_stickyAlert is not null && DateTime.UtcNow < _stickyUntil)
            alerts.Insert(0, _stickyAlert);

        AlertText.Text = alerts.Count > 0 ? "\u26A0  " + string.Join("\n", alerts) : "";
        AlertText.Visibility = alerts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Turns cook-rule events into Windows notifications. Called on every state frame;
    /// the rules themselves are edge-triggered so nothing repeats.
    /// </summary>
    private void UpdateCookNotifications(bool cooking, double? waterC, double? targetC, double? remaining)
    {
        foreach (var evt in _cookRules.Evaluate(cooking, waterC, targetC, remaining))
        {
            switch (evt)
            {
                case CookEvent.TargetReached:
                    Notify("Target temperature reached",
                        $"Water is at {FormatTemp(waterC!.Value)}{UnitSuffix} " +
                        $"(target {FormatTemp(targetC!.Value)}{UnitSuffix}).",
                        error: false);
                    break;

                case CookEvent.CookComplete:
                    Notify("Cook complete", "Cook time completed.", error: false);
                    break;

                case CookEvent.CookStopped:
                    Notify("Cook stopped",
                        $"The cooker stopped with {FormatDuration(remaining ?? 0)} remaining.",
                        error: true);
                    break;
            }
        }
    }

    private static IEnumerable<StateRow> Flatten(JsonObject obj, string prefix = "")
    {
        foreach (var kv in obj)
        {
            var key = string.IsNullOrEmpty(prefix) ? kv.Key : $"{prefix}.{kv.Key}";
            if (kv.Value is JsonObject child)
            {
                foreach (var nested in Flatten(child, key))
                    yield return nested;
            }
            else
            {
                yield return new StateRow { Key = key, Value = FormatNode(kv.Value) };
            }
        }
    }

    private static JsonNode? Walk(JsonObject root, params string[] path)
    {
        JsonNode? node = root;
        foreach (var step in path)
        {
            if (node is not JsonObject obj) return null;
            node = obj[step];
            if (node is null) return null;
        }
        return node;
    }

    private static string? GetString(JsonObject root, params string[] path) =>
        Walk(root, path) is JsonValue v
            ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString().Trim('"'))
            : null;

    private static double? GetNumber(JsonObject root, params string[] path)
    {
        if (Walk(root, path) is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var s) &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    private static void AddFlag(JsonObject root, string group, string key, string label, ICollection<string> alerts)
    {
        var value = GetNumber(root, group, key);
        if (value is not null && value.Value != 0) alerts.Add(label);
    }

    private static string FormatNode(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue v => FormatScalar(v),
        _ => Compact(node),
    };

    private static string FormatScalar(JsonValue v)
    {
        if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        if (v.TryGetValue<string>(out var s)) return s;
        return v.ToJsonString().Trim('"');
    }

    private static string Compact(JsonNode? node) =>
        node is null ? "null" : node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    // ------------------------------------------------------------------ formatting

    private bool ShowingFahrenheit => UnitBox.SelectedIndex == 1;
    public static string UnitSymbol(bool fahrenheit) => fahrenheit ? "\u00B0F" : "\u00B0C";

    private string UnitSuffix => UnitSymbol(ShowingFahrenheit);

    private string FormatTemp(double celsius) =>
        ShowingFahrenheit
            ? (celsius * 9 / 5 + 32).ToString("0.0", CultureInfo.CurrentCulture)
            : celsius.ToString("0.0", CultureInfo.CurrentCulture);

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0) return "0m";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes:00}m"
            : $"{t.Minutes}m {t.Seconds:00}s";
    }

    private void Unit_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;

        TempUnitText.Text = UnitSuffix;
        if (_currentC is not null)
            TempText.Text = FormatTemp(_currentC.Value);

        UpdatePresetLabels();

        if (TargetBox is not null && _selected is not null &&
            double.TryParse(TargetBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var shown))
        {
            // Re-express the entered target in the newly selected unit.
            TargetBox.Text = ShowingFahrenheit
                ? (shown * 9 / 5 + 32).ToString("0.0", CultureInfo.CurrentCulture)
                : ((shown - 32) * 5 / 9).ToString("0.0", CultureInfo.CurrentCulture);
        }

        if (_client.IsConnected && _selected is not null)
            _ = SendUnitAsync();
    }

    private async Task SendUnitAsync()
    {
        if (_selected is null) return;
        try
        {
            await _client.SetUnitAsync(_selected, ShowingFahrenheit ? "F" : "C");
        }
        catch (Exception ex)
        {
            AddLog("Unit change failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ commands

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || !_deviceSupported) return;

        if (!double.TryParse(TargetBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var target))
        {
            AddLog("Enter a numeric target temperature.");
            return;
        }

        var celsius = ShowingFahrenheit ? (target - 32) * 5 / 9 : target;
        if (celsius is < 0 or > 95)
        {
            AddLog("Target must be between 0 and 95 \u00B0C (32 and 203 \u00B0F).");
            return;
        }

        var hours = ParseOrZero(HoursBox.Text);
        var minutes = ParseOrZero(MinutesBox.Text);
        var seconds = (int)Math.Round(hours * 3600 + minutes * 60);
        if (seconds < 60)
        {
            AddLog("Cook time must be at least 1 minute (the API requires a timer).");
            return;
        }

        try
        {
            _cookRules.Reset();
            await _client.StartCookAsync(_selected, celsius, seconds);
            _startRequestAt = DateTime.UtcNow;
            _cookObserved = false;
            _stickyAlert = null;
            AddLog($"Start requested: {celsius:0.0} \u00B0C for {FormatDuration(seconds)}.");
        }
        catch (Exception ex)
        {
            AddLog("Start failed: " + ex.Message);
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || !_deviceSupported) return;
        try
        {
            _cookRules.RequestStop();  // a requested stop must not be reported back as a failure
            await _client.StopCookAsync(_selected);
            _startRequestAt = null;   // a deliberate stop must not look like an abort
            AddLog("Stop requested.");
        }
        catch (Exception ex)
        {
            AddLog("Stop failed: " + ex.Message);
        }
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string tag }) return;
        if (!double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var celsius)) return;

        TargetBox.Text = (ShowingFahrenheit ? celsius * 9 / 5 + 32 : celsius)
            .ToString("0.0", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Preset buttons store Celsius in Tag; their captions must follow the selected unit,
    /// otherwise "54.5" reads as 54.5 degrees F once the toggle is on Fahrenheit.
    /// </summary>
    private void UpdatePresetLabels()
    {
        SteakButton.Content = PresetLabel(54.5, "steak");
        ChickenButton.Content = PresetLabel(65, "chicken");
        EggsButton.Content = PresetLabel(63.5, "eggs");
        SalmonButton.Content = PresetLabel(52, "salmon");
    }

    private string PresetLabel(double celsius, string food) =>
        FormatPreset(celsius, food, ShowingFahrenheit);

    public static string FormatPreset(double celsius, string food, bool fahrenheit)
    {
        var value = fahrenheit ? celsius * 9 / 5 + 32 : celsius;
        return $"{value.ToString("0.#", CultureInfo.CurrentCulture)}\u00B0 {food}";
    }

    private static double ParseOrZero(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ? v : 0;

    // ------------------------------------------------------------------ log

    private void AddLog(string line)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (_log.Count > 500) _log.RemoveAt(0);

        // The traffic panel is hidden in the default view; scrolling a collapsed list throws.
        if (LogPanel is { Visibility: Visibility.Visible } && _log.Count > 0)
            LogList.ScrollIntoView(_log[^1]);
    }
}

