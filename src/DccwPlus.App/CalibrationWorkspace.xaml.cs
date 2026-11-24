using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;
using DccwPlus.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DccwPlus.App;

public sealed partial class CalibrationWorkspace : UserControl
{
    private MainViewModel? _model;
    private Window? _window;
    private Window? _patternWindow;
    private GammaPreviewClient? _client;
    private readonly PreviewRecoveryService _recovery = new(DataPaths.DefaultRoot);
    private GammaCalibrationSession? _session;
    private CalibrationPreset _draft = CalibrationPreset.Create("Calibration");
    private DispatcherQueueTimer? _timer, _debounce;
    private bool _setting, _busy, _pushing, _dirty, _timerBusy;
    private bool _reviewMode;
    private DateTimeOffset _lastHeartbeat;
    private bool Active => _session?.Status == "previewing";
    private bool UndoActive => Active && _session?.UndoOf is not null;

    public CalibrationWorkspace() => InitializeComponent();
    public void Initialize(MainViewModel model, Window window)
    {
        _setting = true; _model = model; _window = window; WorkspaceRoot.DataContext = model;
        _client = new(DataPaths.DefaultRoot, Path.Combine(AppContext.BaseDirectory, "Cli", "dccwpp.exe"));
        GammaLowPattern.SetPattern(ReferencePatternKind.Gamma, GammaExample.TooLow);
        GammaTargetPattern.SetPattern(ReferencePatternKind.Gamma, GammaExample.Balanced);
        GammaHighPattern.SetPattern(ReferencePatternKind.Gamma, GammaExample.TooHigh);
        PresetName.Text = model.Text["DefaultPresetName"];
        if (model.Displays.Count > 0) DisplayPicker.SelectedIndex = 0;
        StepPicker.SelectedIndex = 0;
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromSeconds(1); _timer.Tick += TimerTick;
        _debounce = DispatcherQueue.CreateTimer(); _debounce.Interval = TimeSpan.FromMilliseconds(180); _debounce.IsRepeating = false;
        _debounce.Tick += async (_, _) => await PushAsync();
        model.Text.LanguageChanged += (_, _) => { UpdateStep(); UpdateUi(); };
        KeyDown += async (_, e) =>
        { if (e.Key == global::Windows.System.VirtualKey.Escape && Active) { e.Handled = true; await CancelAsync(); } };
        window.Closed += (_, _) => ClosePatternWindow();
        window.Activated += (_, e) =>
        {
            if (!_busy && e.WindowActivationState != WindowActivationState.Deactivated) RefreshRecovery();
        };
        _setting = false; UpdateStep(); UpdateUi(); RefreshRecovery();
    }
    public void LoadPreset(CalibrationPreset preset)
    {
        if (Active || _busy) { Show("FinishOrCancel", InfoBarSeverity.Warning); return; }
        _setting = true;
        _draft = preset with { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
        PresetName.Text = preset.Name; GammaSlider.Value = preset.Calibration.Gamma;
        RedSlider.Value = preset.Calibration.RedGain; GreenSlider.Value = preset.Calibration.GreenGain; BlueSlider.Value = preset.Calibration.BlueGain;
        _setting = false; Show("DraftLoaded", InfoBarSeverity.Informational);
    }
    private CalibrationPreset CurrentPreset()
    {
        string name = string.IsNullOrWhiteSpace(PresetName.Text) ? _model!.Text["DefaultPresetName"] : PresetName.Text.Trim();
        return _draft with { Name = name, Calibration = new() { Gamma = GammaSlider.Value, RedGain = RedSlider.Value,
            GreenGain = GreenSlider.Value, BlueGain = BlueSlider.Value } };
    }
    private async void StartClicked(object sender, RoutedEventArgs e)
    {
        if (_model is null || _client is null || _busy || Active) return;
        if (DisplayPicker.SelectedItem is not DisplayInfo selected) { Show("SelectDisplay", InfoBarSeverity.Warning); return; }
        _busy = true; UpdateUi();
        try
        {
            var connected = new WindowsDisplayService().Enumerate();
            var display = connected.SingleOrDefault(d => d.Id == selected.Id);
            if (display is null || (selected.PersistenceKey is not null && selected.PersistenceKey != display.PersistenceKey))
            { Show("DisplayUnavailable", InfoBarSeverity.Warning); return; }
            if (!CalibrationService.IsExplicitSdr(display)) { Show("SdrRequired", InfoBarSeverity.Warning); return; }
            if (display.Topology == "clone" || connected.Count(d => d.DeviceName == display.DeviceName) > 1)
            { Show("MirroredDisplay", InfoBarSeverity.Warning); return; }
            if (display.Profile?.Mhc2Present == true) { Show("Mhc2ProfileBlocked", InfoBarSeverity.Warning); return; }
            if (display.ProfileQueryStatus == "unavailable") { Show("ProfileStateUnavailable", InfoBarSeverity.Warning); return; }
            if (!DisplayWindowPlacement.MoveToDisplay(_window!, display.DeviceName))
            { Show("DisplayUnavailable", InfoBarSeverity.Warning); return; }
            _draft = CurrentPreset() with { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
            OriginalToggle.IsOn = false;
            _reviewMode = false;
            _session = await _client.BeginAsync(display.Id, _draft, 60, windowOwned: true);
            if (Active) { _timer!.Start(); StepPicker.SelectedIndex = 1; Show("PreviewActive", InfoBarSeverity.Informational); }
            else ShowState();
        }
        catch (Exception ex) { ShowError(ex); RefreshRecovery(); }
        finally { _busy = false; UpdateUi(); }
    }
    private async Task<bool> PushAsync()
    {
        if (_model is null || _client is null || !Active || UndoActive || _busy || _pushing) return false;
        _pushing = true; UpdateUi();
        try
        {
            do
            {
                _dirty = false; _draft = CurrentPreset();
                _session = await _client.CommandAsync(_session!.Id, "update", _draft, OriginalToggle.IsOn);
            } while (_dirty && Active && !_busy);
            if (!Active) ShowState();
            return Active;
        }
        catch (Exception ex) { ReloadSession(); ShowError(ex); return false; }
        finally { _pushing = false; UpdateUi(); }
    }
    private async void TimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_timerBusy || _session is null || _client is null || _busy || _pushing) return;
        _timerBusy = true;
        try
        {
            ReloadSession();
            if (Active && !_busy && !OnPreviewDisplay())
            {
                await CancelAsync(targetMoved: true);
                return;
            }
            if (Active && StepPicker.SelectedIndex < 5 && !_busy && !_pushing && DateTimeOffset.UtcNow - _lastHeartbeat > TimeSpan.FromSeconds(3))
            { _lastHeartbeat = DateTimeOffset.UtcNow; _session = await _client.CommandAsync(_session!.Id, "heartbeat"); }
            if (Active)
                CountdownText.Text = string.Format(_model!.Text[UndoActive ? "UndoCountdown" : "PreviewCountdown"],
                    Math.Max(0, (int)Math.Ceiling((_session!.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds)));
            else if (_session?.NeedsRecovery != true || _session.Status == "recovery-pending")
            { _timer!.Stop(); ClosePatternWindow(); ShowState(); RefreshRecovery(); }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _timerBusy = false; UpdateUi(); }
    }
    private async void FinishClicked(object sender, RoutedEventArgs e)
    {
        if (!Active || _busy || _pushing || _client is null) return;
        bool undo = UndoActive;
        if (!undo && string.IsNullOrWhiteSpace(PresetName.Text)) { Show("PresetNameRequired", InfoBarSeverity.Warning); return; }
        if (!OnPreviewDisplay()) { await CancelAsync(targetMoved: true); return; }
        if (!undo && !await PushAsync()) return;
        if (!OnPreviewDisplay()) { await CancelAsync(targetMoved: true); return; }
        _busy = true; UpdateUi();
        try
        {
            ClosePatternWindow();
            _session = await _client.CommandAsync(_session!.Id, "confirm");
            if (_session.Status == "confirmed" && undo)
                Show(_session.BindingStatus == "failed" ? "UndoSavedBindingWarning" : "UndoSaved",
                    _session.BindingStatus == "failed" ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            else if (_session.Status == "confirmed")
            {
                _model!.RefreshPresets();
                bool bindingSaved = _session.BindingStatus == "saved";
                Show(bindingSaved ? "CalibrationSaved" : "CalibrationSavedWithoutBinding",
                    bindingSaved ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
                Outcome.Message += " " + _session.InstalledProfile;
                if (ClearTypeOption.IsChecked == true)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cttune.exe")) { UseShellExecute = true });
            }
            else ShowState();
        }
        catch (Exception ex) { ReloadSession(); ShowError(ex); }
        finally { _busy = false; if (!Active) _timer!.Stop(); UpdateUi(); RefreshRecovery(); }
    }
    private async void UndoCalibrationClicked(object sender, RoutedEventArgs e)
    {
        if (Active || _busy || _client is null || _window is null || DisplayPicker.SelectedItem is not DisplayInfo selected) return;
        _busy = true;
        UpdateUi();
        try
        {
            var backend = new WindowsGammaCalibrationBackend(DataPaths.DefaultRoot);
            var current = backend.Read(selected.Id);
            var saved = new GammaCalibrationStore(DataPaths.DefaultRoot).Sessions().FirstOrDefault(s =>
                s.Status == "confirmed" && !s.IsBindingRestore && s.UndoOf is null && s.Original.DisplayId == current.DisplayId
                && s.InstalledProfile is not null && ProfilePreviewPolicy.EqualName(s.InstalledProfile, current.ProfileName));
            if (saved is null)
            {
                Show("NoUndoCalibration", InfoBarSeverity.Informational);
                return;
            }
            if (!backend.PlanUndo(saved.Id).CanPreview)
            {
                Show("UndoUnavailable", InfoBarSeverity.Warning);
                return;
            }
            if (!DisplayWindowPlacement.MoveToDisplay(_window, current.DeviceName))
            {
                Show("DisplayUnavailable", InfoBarSeverity.Warning);
                return;
            }
            OriginalToggle.IsOn = false;
            _session = await _client.BeginUndoAsync(saved.Id, windowOwned: true);
            if (Active)
            {
                _reviewMode = true;
                StepPicker.SelectedIndex = 5;
                _timer!.Start();
                Show("UndoPreview", InfoBarSeverity.Informational);
            }
            else ShowState();
        }
        catch (ProfilePreviewException ex) when (ex.Code.StartsWith("undo_", StringComparison.Ordinal))
        { Show("UndoUnavailable", InfoBarSeverity.Warning); }
        catch (Exception ex) { ShowError(ex); }
        finally { _busy = false; UpdateUi(); RefreshRecovery(); }
    }
    private async void CancelClicked(object sender, RoutedEventArgs e) => await CancelAsync();
    private async Task CancelAsync(bool targetMoved = false)
    {
        if (!Active || _client is null || _busy) return;
        _busy = true; UpdateUi();
        try
        {
            ClosePatternWindow();
            _session = await _client.CommandAsync(_session!.Id, "revert");
            if (targetMoved && _session.Status == "reverted") Show("PreviewMoved", InfoBarSeverity.Warning);
            else ShowState();
        }
        catch (Exception ex) { ReloadSession(); ShowError(ex); }
        finally { _busy = false; if (!Active) _timer!.Stop(); UpdateUi(); RefreshRecovery(); }
    }
    private async void StepChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_setting || _model is null) return;
        UpdateStep();
        bool reviewing = StepPicker.SelectedIndex == 5;
        if (Active && !_busy && _client is not null && reviewing != _reviewMode)
        {
            _busy = true; UpdateUi();
            try
            {
                _session = await _client.CommandAsync(_session!.Id, reviewing ? "review" : "edit");
                _reviewMode = reviewing;
                if (Active && !reviewing && OriginalToggle.IsOn)
                {
                    OriginalToggle.IsOn = false;
                    _session = await _client.CommandAsync(_session.Id, "update", CurrentPreset());
                }
                if (!Active) ShowState();
            }
            catch (Exception ex) { ShowError(ex); }
            finally { _busy = false; }
        }
        UpdateUi();
    }
    private void UpdateStep()
    {
        if (_model is null) return;
        int step = Math.Max(0, StepPicker.SelectedIndex);
        StepInstructions.Text = _model.Text["StepInstruction" + step];
        GammaControls.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        BalanceControls.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        ReviewControls.Visibility = step == 5 ? Visibility.Visible : Visibility.Collapsed;
        SkipHardwareButton.Visibility = step is 2 or 3 ? Visibility.Visible : Visibility.Collapsed;
        Pattern.SetPattern((ReferencePatternKind)step);
    }
    private void UpdateUi()
    {
        if (_model is null) return;
        bool hasDisplay = DisplayPicker.SelectedItem is DisplayInfo;
        bool undo = UndoActive;
        DisplayPicker.IsEnabled = !Active && !_busy;
        MoveDisplayButton.IsEnabled = hasDisplay && !Active && !_busy;
        RefreshDisplaysButton.IsEnabled = !Active && !_busy;
        RecoveryButton.IsEnabled = !Active && !_busy;
        FullScreenButton.IsEnabled = hasDisplay && !_busy;
        StartButton.IsEnabled = !Active && !_busy && DisplayPicker.SelectedItem is DisplayInfo display
            && CalibrationService.IsExplicitSdr(display) && display.Topology != "clone"
            && display.Profile?.Mhc2Present != true && display.ProfileQueryStatus != "unavailable";
        StartButton.Visibility = Active ? Visibility.Collapsed : Visibility.Visible;
        UndoCalibrationButton.Visibility = Active ? Visibility.Collapsed : Visibility.Visible;
        UndoCalibrationButton.IsEnabled = StartButton.IsEnabled;
        CancelButton.IsEnabled = Active && !_busy;
        CancelButton.Visibility = Active ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = StepPicker.SelectedIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.IsEnabled = Active && !_busy && !_pushing && _reviewMode;
        FinishButton.Content = _model.Text[undo ? "ConfirmUndo" : "FinishCalibration"];
        PreviousButton.IsEnabled = StepPicker.SelectedIndex > 0 && !_busy && !_pushing && !undo;
        NextButton.IsEnabled = StepPicker.SelectedIndex < 5 && !_busy && !_pushing && !undo;
        StepPicker.IsEnabled = !_busy && !_pushing && !undo;
        SkipHardwareButton.IsEnabled = !_busy && !_pushing;
        OriginalToggle.IsEnabled = Active && !_busy && !_pushing && _reviewMode && !undo;
        OriginalToggle.Visibility = undo ? Visibility.Collapsed : Visibility.Visible;
        PresetName.Visibility = undo ? Visibility.Collapsed : Visibility.Visible;
        ClearTypeOption.Visibility = undo ? Visibility.Collapsed : Visibility.Visible;
        SaveDraftButton.Visibility = Active ? Visibility.Collapsed : Visibility.Visible;
        CountdownText.Visibility = Active ? Visibility.Visible : Visibility.Collapsed;
        StepInstructions.Text = _model.Text[undo ? "UndoPreview" : "StepInstruction" + Math.Max(0, StepPicker.SelectedIndex)];
        ComparisonState.Text = _model.Text[undo ? "ShowingPreviousSavedCurve" : !Active ? "ComparisonRequiresPreview"
            : _session!.ShowingOriginal ? "ShowingOriginal" : "ShowingAdjusted"];
        UpdateDisplaySummary();
        ResetButton.Visibility = StepPicker.SelectedIndex is 1 or 4 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SaveDraftClicked(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        try { var preset = CurrentPreset() with { Id = Guid.NewGuid() }; _model.PresetStore.Add(preset); _model.RefreshPresets(); Show("Saved", InfoBarSeverity.Success); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void ValuesChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    { if (_setting || _model is null) return; _dirty = true; _debounce?.Stop(); _debounce?.Start(); }
    private void NameChanged(object sender, TextChangedEventArgs e)
    { if (_setting || _model is null) return; _dirty = true; _debounce?.Stop(); _debounce?.Start(); }
    private void CompareChanged(object sender, RoutedEventArgs e)
    { if (_setting || _model is null || !Active || _busy) return; _dirty = true; _debounce?.Stop(); _debounce?.Start(); }
    private void PreviousClicked(object sender, RoutedEventArgs e) => StepPicker.SelectedIndex = Math.Max(0, StepPicker.SelectedIndex - 1);
    private void NextClicked(object sender, RoutedEventArgs e) => StepPicker.SelectedIndex = Math.Min(5, StepPicker.SelectedIndex + 1);
    private void SkipHardwareClicked(object sender, RoutedEventArgs e) => StepPicker.SelectedIndex = 4;
    private void ResetClicked(object sender, RoutedEventArgs e)
    { if (StepPicker.SelectedIndex == 1) GammaSlider.Value = 2.2; else { RedSlider.Value = GreenSlider.Value = BlueSlider.Value = 1; } }
    private void DisplayChanged(object sender, SelectionChangedEventArgs e)
    { if (_model is not null && !_setting) { Outcome.IsOpen = false; UpdateUi(); } }
    private void UpdateDisplaySummary()
    {
        var text = _model!.Text;
        if (DisplayPicker.SelectedItem is not DisplayInfo display)
        {
            DisplaySummary.Text = text["SelectDisplay"];
            DisplaySettingsLink.Visibility = Visibility.Collapsed;
            HdrCalibrationLink.Visibility = Visibility.Collapsed;
            DisplayGuidance.Visibility = Visibility.Collapsed;
            return;
        }
        string mode = display.ColorState switch
        {
            "hdr" => "DisplayModeHdr",
            "advanced-color" or "wcg" => "DisplayModeAdvanced",
            "sdr" when CalibrationService.IsExplicitSdr(display) => "DisplayModeSdr",
            _ => "DisplayModeUnknown"
        };
        string details = text[mode];
        if (display.Width is int width && display.Height is int height) details += $" · {width} × {height}";
        if (display.PositionX is int x && display.PositionY is int y)
            details += " · " + string.Format(text["DisplayDesktopPosition"], x, y);
        if (display.Topology == "clone") details += " · " + text["MirroredDisplay"];
        DisplaySummary.Text = details;
        string? guidance = mode switch
        {
            "DisplayModeHdr" => "HdrCalibrationGuidance",
            "DisplayModeAdvanced" => "AdvancedColorGuidance",
            _ when display.Profile?.Mhc2Present == true => "Mhc2ProfileBlocked",
            _ when display.ProfileQueryStatus == "unavailable" => "ProfileStateUnavailable",
            _ => null
        };
        DisplayGuidance.Text = guidance is null ? "" : text[guidance];
        DisplayGuidance.Visibility = guidance is not null ? Visibility.Visible : Visibility.Collapsed;
        DisplaySettingsLink.Visibility = mode != "DisplayModeSdr" || display.Topology == "clone" || guidance is not null
            ? Visibility.Visible : Visibility.Collapsed;
        HdrCalibrationLink.Visibility = mode == "DisplayModeHdr" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void MoveDisplayClicked(object sender, RoutedEventArgs e)
    {
        if (_window is null || DisplayPicker.SelectedItem is not DisplayInfo display || Active) return;
        try
        {
            if (!DisplayWindowPlacement.MoveToDisplay(_window, display.DeviceName)) Show("DisplayUnavailable", InfoBarSeverity.Warning);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void RefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_model is null || Active) return;
        string? selected = (DisplayPicker.SelectedItem as DisplayInfo)?.Id;
        try { _model.Refresh(); DisplayPicker.SelectedItem = _model.Displays.FirstOrDefault(d => d.Id == selected) ?? _model.Displays.FirstOrDefault(); }
        catch (Exception ex) { ShowError(ex); }
        RefreshRecovery();
    }
    private void FullScreenClicked(object sender, RoutedEventArgs e)
    {
        if (_model is null || DisplayPicker.SelectedItem is not DisplayInfo display) return;
        if (_patternWindow is not null) { _patternWindow.Activate(); return; }
        var text = _model.Text;
        var pattern = new PatternSurface();
        pattern.SetPattern((ReferencePatternKind)Math.Max(0, StepPicker.SelectedIndex));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pattern, text["CalibrationPattern"]);
        var exit = new Button { Content = text["ExitFullScreen"], Margin = new Thickness(16, 10, 16, 10) };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(pattern);
        Grid.SetRow(exit, 1);
        root.Children.Add(exit);
        var window = new Window { Title = text["CalibrationPattern"], Content = root };
        _patternWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_patternWindow, window))
                _patternWindow = null;
        };
        exit.Click += (_, _) => ClosePatternWindow();
        exit.Loaded += (_, _) => exit.Focus(FocusState.Programmatic);
        root.KeyDown += (_, key) =>
        {
            if (key.Key != global::Windows.System.VirtualKey.Escape) return;
            key.Handled = true;
            ClosePatternWindow();
        };
        try
        {
            if (!DisplayWindowPlacement.MoveToDisplay(window, display.DeviceName))
            { ClosePatternWindow(); Show("DisplayUnavailable", InfoBarSeverity.Warning); return; }
            window.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            if (!DisplayWindowPlacement.IsOnDisplay(window, display.DeviceName))
            { ClosePatternWindow(); Show("DisplayUnavailable", InfoBarSeverity.Warning); return; }
            window.Activate();
        }
        catch (Exception ex) { ClosePatternWindow(); ShowError(ex); }
    }
    private bool OnPreviewDisplay()
    {
        return _session is not null && _window is not null
            && DisplayWindowPlacement.IsOnDisplay(_window, _session.Original.DeviceName)
            && (_patternWindow is null || DisplayWindowPlacement.IsOnDisplay(_patternWindow, _session.Original.DeviceName));
    }
    private void ClosePatternWindow()
    {
        var window = _patternWindow;
        _patternWindow = null;
        window?.Close();
    }
    private void ReloadSession()
    { if (_session is not null && _client is not null) _session = _client.Status(_session.Id); }
    private void RefreshRecovery()
    {
        try
        {
            RecoveryNotice.IsOpen = _recovery.List().Any(s => s.Kind != "calibration" || s.SessionId != _session?.Id
                || s.Status == "recovery-pending");
        }
        catch (Exception ex) { ShowError(ex); RecoveryNotice.IsOpen = true; }
    }
    private async void RecoverClicked(object sender, RoutedEventArgs e)
    {
        if (_model is null || Active || _busy) return;
        _busy = true; UpdateUi();
        try
        {
            var report = await Task.Run(_recovery.Recover);
            ReloadSession();
            if (report.Reason == "preview_busy") Show("RecoveryBusy", InfoBarSeverity.Warning);
            else if (report.Reason == "recovery_required") Show("RecoveryMessage", InfoBarSeverity.Warning);
            else if (report.Reason == "recovery_finished") Show("RecoveryFinished", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _busy = false; UpdateUi(); RefreshRecovery(); }
    }
    private void ShowState()
    {
        if (_session is null) return;
        string key = _session.Status switch { "confirmed" => "CalibrationSaved", "reverted" => "CalibrationReverted", "superseded" => "CalibrationSuperseded", "recovery-pending" => "RecoveryMessage", _ => "PreviewActive" };
        Show(key, _session.NeedsRecovery && !Active ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
    }
    private void Show(string key, InfoBarSeverity severity)
    { if (_model is null) return; Outcome.Message = _model.Text[key]; Outcome.Severity = severity; Outcome.IsOpen = true; }
    private void ShowError(Exception ex)
    {
        if (_model is null) return;
        if (ex is ProfilePreviewException { Code: "mhc2_profile_blocked" })
        { Show("Mhc2ProfileBlocked", InfoBarSeverity.Warning); return; }
        if (ex is ProfilePreviewException { Code: "profile_state_unavailable" or "profile_scope_unavailable" })
        { Show("ProfileStateUnavailable", InfoBarSeverity.Warning); return; }
        Outcome.Message = _model.Text["Error"] + " " + ex.Message;
        Outcome.Severity = InfoBarSeverity.Error;
        Outcome.IsOpen = true;
    }
}
