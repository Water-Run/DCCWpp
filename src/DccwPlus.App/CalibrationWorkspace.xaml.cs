using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;
using DccwPlus.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Globalization.NumberFormatting;

namespace DccwPlus.App;

public sealed partial class CalibrationWorkspace : UserControl
{
    private static readonly string[] StepLabelKeys = ["StepPrepare", "StepGamma", "StepBrightness", "StepContrast", "StepBalance", "StepReview"];
    private MainViewModel? _model;
    private Window? _window;
    private Window? _patternWindow;
    private Window? _activeWindow;
    private PatternSurface? _fullScreenPattern;
    private (Button Exit, Button Compare, Button Cancel, TextBlock Guide, TextBlock Countdown)? _fullScreenControls;
    private GammaPreviewClient? _client;
    private readonly PreviewRecoveryService _recovery = new(DataPaths.DefaultRoot);
    private GammaCalibrationSession? _session;
    private CalibrationPreset _draft = CalibrationPreset.Create("Calibration");
    private DispatcherQueueTimer? _timer, _debounce;
    private bool _setting, _busy, _pushing, _dirty, _timerBusy;
    private bool _workerExited;
    private bool _calibrationPageVisible = true;
    private DateTimeOffset _lastHeartbeat;
    private bool Active => !_workerExited && _session?.Status == "previewing";
    private bool UndoActive => Active && _session?.UndoOf is not null;
    private bool ReviewSelected => StepPicker.SelectedIndex == 5;
    private bool IsReviewing => Active && _session?.IsReviewing == true;
    private bool AnnounceWizardChanges => _calibrationPageVisible && ReferenceEquals(_activeWindow, _window);
    private bool PreviewUpdatePending => Active && !UndoActive && (_dirty || ReviewSelected != IsReviewing);
    internal bool HasActivePreview => Active;
    internal bool CanCancelPreview => Active && !_busy;
    internal bool HasPendingPreviewRecovery => _session?.NeedsRecovery == true && (_workerExited || _session.Status == "recovery-pending");
    internal event Action? PreviewUiUpdated;

    public CalibrationWorkspace() => InitializeComponent();
    public void Initialize(MainViewModel model, Window window)
    {
        _setting = true; _model = model; _window = window; WorkspaceRoot.DataContext = model;
        _client = new(DataPaths.DefaultRoot, Path.Combine(AppContext.BaseDirectory, "Cli", "dccwpp.exe"));
        var formatter = new DecimalFormatter
        {
            FractionDigits = 2,
            NumberRounder = new IncrementNumberRounder { Increment = 0.01, RoundingAlgorithm = RoundingAlgorithm.RoundHalfToEven }
        };
        foreach (NumberBox number in new[] { GammaNumber, RedNumber, GreenNumber, BlueNumber })
            number.NumberFormatter = formatter;
        GammaLowPattern.SetPattern(ReferencePatternKind.Gamma, GammaExample.TooLow);
        GammaTargetPattern.SetPattern(ReferencePatternKind.Gamma, GammaExample.Balanced);
        GammaHighPattern.SetPattern(ReferencePatternKind.Gamma, GammaExample.TooHigh);
        PresetName.Text = model.Text["DefaultPresetName"];
        if (model.Displays.Count > 0) DisplayPicker.SelectedIndex = 0;
        UpdateStepLabels();
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromSeconds(1); _timer.Tick += TimerTick;
        _debounce = DispatcherQueue.CreateTimer(); _debounce.Interval = TimeSpan.FromMilliseconds(180); _debounce.IsRepeating = false;
        _debounce.Tick += async (_, _) => await PushAsync();
        model.Text.LanguageChanged += (_, _) => { UpdateStepLabels(); UpdateStep(); UpdateUi(); RefreshRecovery(); };
        window.Closed += (_, _) => ClosePatternWindow();
        window.Activated += (_, e) =>
        {
            TrackActiveWindow(window, e.WindowActivationState);
            if (!_busy && e.WindowActivationState != WindowActivationState.Deactivated)
            {
                UpdateUi();
                RefreshRecovery();
            }
        };
        _setting = false; UpdateStep(); UpdateUi(); RefreshRecovery();
    }

    internal void SetCalibrationPageVisible(bool visible)
    {
        if (_calibrationPageVisible == visible) return;
        _calibrationPageVisible = visible;
        if (!visible)
        {
            _debounce?.Stop();
            ClosePatternWindow();
        }
        else if (PreviewUpdatePending) _debounce?.Start();
    }

    internal Task CancelPreviewAsync() => CancelAsync();
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
            _dirty = false;
            _session = await _client.BeginAsync(display.Id, _draft, 60, windowOwned: true);
            if (Active) { _timer!.Start(); StepPicker.SelectedIndex = 1; Show("PreviewActive", InfoBarSeverity.Informational); }
            else ShowState();
        }
        catch (Exception ex) { ShowError(ex); RefreshRecovery(); }
        finally { _busy = false; UpdateUi(); }
    }
    private async Task<bool> PushAsync()
    {
        if (_model is null || _client is null || !Active || UndoActive || _busy || _pushing || !_calibrationPageVisible) return false;
        _debounce?.Stop();
        if (!PreviewUpdatePending) return true;
        _pushing = true; UpdateUi();
        bool sendingEdits = false;
        try
        {
            do
            {
                sendingEdits = _dirty;
                _dirty = false;
                var preset = sendingEdits ? CurrentPreset() : null;
                bool reviewing = ReviewSelected;
                if (reviewing != IsReviewing)
                {
                    _session = await _client.CommandAsync(_session!.Id, reviewing ? "review" : "edit", preset);
                    if (Active && reviewing != IsReviewing)
                    {
                        _dirty |= sendingEdits;
                        return false;
                    }
                }
                else
                    _session = await _client.CommandAsync(_session!.Id, "update", preset, OriginalToggle.IsOn);
                sendingEdits = false;
                SyncComparisonToggle();
            } while (PreviewUpdatePending && !_busy && _calibrationPageVisible);
            if (!Active) ShowState();
            return Active && !PreviewUpdatePending;
        }
        catch (Exception ex) { _dirty |= sendingEdits; ReloadSession(); ShowError(ex); return false; }
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
            if (PreviewUpdatePending && _calibrationPageVisible) await PushAsync();
            if (Active && !IsReviewing && !_busy && !_pushing && _calibrationPageVisible
                && DateTimeOffset.UtcNow - _lastHeartbeat > TimeSpan.FromSeconds(3))
            {
                _lastHeartbeat = DateTimeOffset.UtcNow;
                await _client.CommandAsync(_session!.Id, "heartbeat");
                if (!_pushing && !_busy) ReloadSession();
            }
            if (!Active && (_session?.NeedsRecovery != true || HasPendingPreviewRecovery))
            { _timer!.Stop(); ClosePatternWindow(); ShowState(); RefreshRecovery(); }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _timerBusy = false; UpdateUi(); }
    }
    private async void FinishClicked(object sender, RoutedEventArgs e)
    {
        if (!IsReviewing || !ReviewSelected || _busy || _pushing || _client is null) return;
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
            {
                bool bindingWarning = _session.BindingStatus is "failed" or "pending";
                Show(bindingWarning ? "UndoSavedBindingWarning" : "UndoSaved",
                    bindingWarning ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            }
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
                s.Status == "confirmed" && !s.IsBindingRestore && s.UndoOf is null && MonitorIdentity.MatchesSavedDisplay(s.Original, selected)
                && s.InstalledProfile is not null && ProfilePreviewPolicy.EqualName(s.InstalledProfile, current.ProfileName));
            if (saved is null)
            {
                Show("NoUndoCalibration", InfoBarSeverity.Informational);
                return;
            }
            var plan = backend.PlanUndo(saved.Id);
            if (!plan.CanPreview || plan.DisplayId != selected.Id)
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
            _session = await _client.BeginUndoAsync(saved.Id, windowOwned: true, expectedDisplayId: selected.Id);
            if (Active)
            {
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
        catch (Exception ex) { ShowError(ex); }
        finally { _busy = false; if (!Active) _timer!.Stop(); UpdateUi(); RefreshRecovery(); }
    }
    private async void StepChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_setting || _model is null) return;
        UpdateStep();
        StepScroll.ChangeView(null, 0, null, disableAnimation: true);
        if (PreviewUpdatePending) await PushAsync();
        UpdateUi();
    }
    private void UpdateStep()
    {
        if (_model is null) return;
        int step = Math.Max(0, StepPicker.SelectedIndex);
        UpdateStepInstructions(step);
        PreparationControls.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        GammaControls.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        BalanceControls.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        ReviewControls.Visibility = step == 5 ? Visibility.Visible : Visibility.Collapsed;
        SkipHardwareButton.Visibility = step is 2 or 3 ? Visibility.Visible : Visibility.Collapsed;
        var pattern = (ReferencePatternKind)step;
        Pattern.SetPattern(pattern);
        _fullScreenPattern?.SetPattern(pattern);
    }
    private void StepScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Pattern is not null && e.NewSize.Height > 0)
            Pattern.Height = Math.Clamp(e.NewSize.Height * 0.45, 72, 242);
    }
    private void UpdateStepLabels()
    {
        if (_model is null) return;
        int selected = Math.Max(0, StepPicker.SelectedIndex);
        bool previousSetting = _setting;
        _setting = true;
        try
        {
            StepPicker.ItemsSource = StepLabelKeys.Select(key => _model.Text[key]).ToArray();
            StepPicker.SelectedIndex = selected;
        }
        finally { _setting = previousSetting; }
    }
    private void UpdateUi()
    {
        if (_model is null) return;
        bool hasDisplay = DisplayPicker.SelectedItem is DisplayInfo;
        bool undo = UndoActive;
        bool updatingComparison = Active && ReviewSelected && !undo && (!IsReviewing || ((_dirty || _pushing)
            && (_session!.Preset.Calibration != CurrentPreset().Calibration || OriginalToggle.IsOn != _session.ShowingOriginal)));
        bool comparisonReady = IsReviewing && ReviewSelected && !_busy && !_pushing && !updatingComparison;
        bool remote = WindowsDisplaySession.IsRemote();
        DisplayPicker.IsEnabled = !Active && !_busy;
        MoveDisplayButton.IsEnabled = hasDisplay && !Active && !_busy;
        RefreshDisplaysButton.IsEnabled = !Active && !_busy;
        RecoveryButton.IsEnabled = !Active && !_busy;
        FullScreenButton.IsEnabled = hasDisplay && !_busy;
        StartButton.IsEnabled = !Active && !HasPendingPreviewRecovery && !_busy && !remote && DisplayPicker.SelectedItem is DisplayInfo display
            && CalibrationService.IsExplicitSdr(display) && display.Topology != "clone"
            && display.Profile?.Mhc2Present != true && display.ProfileQueryStatus != "unavailable";
        StartButton.Visibility = Active ? Visibility.Collapsed : Visibility.Visible;
        UndoCalibrationButton.Visibility = Active ? Visibility.Collapsed : Visibility.Visible;
        UndoCalibrationButton.IsEnabled = StartButton.IsEnabled;
        CancelButton.IsEnabled = CanCancelPreview;
        CancelButton.Visibility = Active ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = ReviewSelected ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.IsEnabled = comparisonReady;
        FinishButton.Content = _model.Text[undo ? "ConfirmUndo" : "FinishCalibration"];
        PreviousButton.IsEnabled = StepPicker.SelectedIndex > 0 && !_busy && !_pushing && !undo;
        NextButton.IsEnabled = StepPicker.SelectedIndex < 5 && !_busy && !_pushing && !undo;
        StepPicker.IsEnabled = !_busy && !_pushing && !undo;
        SkipHardwareButton.IsEnabled = !_busy && !_pushing;
        OriginalToggle.IsEnabled = comparisonReady && !undo;
        OriginalToggle.Visibility = undo ? Visibility.Collapsed : Visibility.Visible;
        PresetName.Visibility = undo ? Visibility.Collapsed : Visibility.Visible;
        ClearTypeOption.Visibility = undo ? Visibility.Collapsed : Visibility.Visible;
        SaveDraftButton.Visibility = Active ? Visibility.Collapsed : Visibility.Visible;
        CountdownText.Visibility = IsReviewing && ReviewSelected ? Visibility.Visible : Visibility.Collapsed;
        if (CountdownText.Visibility == Visibility.Visible)
            CountdownText.Text = string.Format(_model.Text[undo ? "UndoCountdown" : "PreviewCountdown"],
                Math.Max(0, (int)Math.Ceiling((_session!.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds)));
        UpdateStepInstructions(Math.Max(0, StepPicker.SelectedIndex));
        SetLiveText(ComparisonState, _model.Text[undo ? "ShowingPreviousSavedCurve" : !Active ? "ComparisonRequiresPreview"
            : updatingComparison ? "UpdatingComparison" : _session!.ShowingOriginal ? "ShowingOriginal" : "ShowingAdjusted"],
            AnnounceWizardChanges && ReviewSelected);
        UpdateFullScreenControls();
        UpdateDisplaySummary(remote);
        ResetButton.Visibility = StepPicker.SelectedIndex is 1 or 4 ? Visibility.Visible : Visibility.Collapsed;
        PreviewUiUpdated?.Invoke();
    }

    private void UpdateStepInstructions(int step) => SetLiveText(StepInstructions,
        _model!.Text[UndoActive ? "UndoPreview" : "StepInstruction" + step], AnnounceWizardChanges);

    private static void SetLiveText(TextBlock element, string text, bool announce)
    {
        if (element.Text == text) return;
        element.Text = text;
        if (announce && element.IsLoaded && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            FrameworkElementAutomationPeer.CreatePeerForElement(element)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private void SaveDraftClicked(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        try { var preset = CurrentPreset() with { Id = Guid.NewGuid() }; _model.PresetStore.Add(preset); _model.RefreshPresets(); Show("Saved", InfoBarSeverity.Success); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void ValuesChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => QueuePreviewUpdate();
    private void NameChanged(object sender, TextChangedEventArgs e)
        => QueuePreviewUpdate();
    private void CompareChanged(object sender, RoutedEventArgs e)
    {
        if (_setting) return;
        QueuePreviewUpdate();
        if (Active && !_pushing) UpdateUi();
    }
    private void QueuePreviewUpdate()
    {
        if (_setting || _model is null || UndoActive) return;
        _dirty = true;
        if (!_calibrationPageVisible || !Active || _pushing) return;
        _debounce?.Stop();
        _debounce?.Start();
    }
    private void PreviousClicked(object sender, RoutedEventArgs e) => StepPicker.SelectedIndex = Math.Max(0, StepPicker.SelectedIndex - 1);
    private void NextClicked(object sender, RoutedEventArgs e) => StepPicker.SelectedIndex = Math.Min(5, StepPicker.SelectedIndex + 1);

    private void SkipHardwareClicked(object sender, RoutedEventArgs e)
    {
        bool keyboardFocused = SkipHardwareButton.FocusState == FocusState.Keyboard;
        StepPicker.SelectedIndex = 4;
        if (keyboardFocused) RedSlider.Focus(FocusState.Keyboard);
    }
    private void ResetClicked(object sender, RoutedEventArgs e)
    { if (StepPicker.SelectedIndex == 1) GammaSlider.Value = 2.2; else { RedSlider.Value = GreenSlider.Value = BlueSlider.Value = 1; } }
    private void DisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_model is null || _setting) return;
        ClosePatternWindow();
        Outcome.IsOpen = false;
        UpdateUi();
    }
    private void UpdateDisplaySummary(bool remote)
    {
        var text = _model!.Text;
        if (DisplayPicker.SelectedItem is not DisplayInfo display)
        {
            DisplaySummary.Text = text["SelectDisplay"];
            DisplaySettingsLink.Visibility = Visibility.Collapsed;
            HdrCalibrationLink.Visibility = Visibility.Collapsed;
            DisplayGuidance.Text = remote ? text["RemoteCalibrationBlocked"] : "";
            DisplayGuidance.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
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
            _ when remote => "RemoteCalibrationBlocked",
            "DisplayModeHdr" => "HdrCalibrationGuidance",
            "DisplayModeAdvanced" => "AdvancedColorGuidance",
            _ when display.Profile?.Mhc2Present == true => "Mhc2ProfileBlocked",
            _ when display.ProfileQueryStatus == "unavailable" => "ProfileStateUnavailable",
            _ => null
        };
        DisplayGuidance.Text = guidance is null ? "" : text[guidance];
        DisplayGuidance.Visibility = guidance is not null ? Visibility.Visible : Visibility.Collapsed;
        DisplaySettingsLink.Visibility = !remote && (mode != "DisplayModeSdr" || display.Topology == "clone" || guidance is not null)
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
        AutomationProperties.SetName(pattern, text["CalibrationPattern"]);
        var exit = new Button();
        var compare = new Button();
        var cancel = new Button();
        var guide = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(guide, AutomationLiveSetting.Polite);
        var countdown = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(exit);
        actions.Children.Add(compare);
        actions.Children.Add(cancel);
        var footer = new StackPanel
        {
            Style = (Style)Application.Current.Resources["FullScreenPatternFooter"],
            Padding = new Thickness(16, 10, 16, 10),
            Spacing = 6
        };
        footer.Children.Add(guide);
        footer.Children.Add(countdown);
        footer.Children.Add(new ScrollViewer
        {
            Content = actions, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(pattern);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        var window = new Window { Title = text["CalibrationPattern"], Content = root };
        _patternWindow = window;
        _fullScreenPattern = pattern;
        _fullScreenControls = (exit, compare, cancel, guide, countdown);
        UpdateFullScreenControls();
        window.Activated += (_, e) => TrackActiveWindow(window, e.WindowActivationState);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeWindow, window)) _activeWindow = null;
            if (ReferenceEquals(_patternWindow, window))
            {
                _patternWindow = null;
                _fullScreenPattern = null;
                _fullScreenControls = null;
            }
        };
        exit.Click += (_, _) => ClosePatternWindow();
        exit.Loaded += (_, _) => exit.Focus(FocusState.Programmatic);
        compare.Click += async (_, _) =>
        {
            if (!OriginalToggle.IsEnabled) return;
            if (!OnPreviewDisplay()) { await CancelAsync(targetMoved: true); return; }
            OriginalToggle.IsOn = !_session!.ShowingOriginal;
            await PushAsync();
        };
        cancel.Click += async (_, _) => await CancelAsync();
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
    private void UpdateFullScreenControls()
    {
        if (_model is null || _fullScreenControls is not { } controls) return;
        var text = _model.Text;
        bool reviewing = StepPicker.SelectedIndex == 5 && Active;
        controls.Exit.Content = text["ExitFullScreen"];
        controls.Compare.Content = text[_session?.ShowingOriginal == true ? "ShowAdjustedAction" : "ShowOriginalAction"];
        controls.Compare.Visibility = reviewing && !UndoActive ? Visibility.Visible : Visibility.Collapsed;
        controls.Compare.IsEnabled = OriginalToggle.IsEnabled;
        controls.Cancel.Content = text["CancelCalibration"];
        controls.Cancel.Visibility = CancelButton.Visibility;
        controls.Cancel.IsEnabled = CancelButton.IsEnabled;
        SetLiveText(controls.Guide, reviewing ? ComparisonState.Text : StepInstructions.Text,
            ReferenceEquals(_patternWindow, _activeWindow));
        controls.Countdown.Visibility = CountdownText.Visibility;
        controls.Countdown.Text = CountdownText.Text;
    }

    private void TrackActiveWindow(Window window, WindowActivationState activation)
    {
        if (activation != WindowActivationState.Deactivated) _activeWindow = window;
        else if (ReferenceEquals(_activeWindow, window)) _activeWindow = null;
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
        _fullScreenPattern = null;
        _fullScreenControls = null;
        window?.Close();
    }
    private void ReloadSession()
    {
        if (_session is null || _client is null) return;
        var (session, workerRunning) = _client.Observe(_session.Id);
        _session = session;
        _workerExited = session.NeedsRecovery && !workerRunning;
        SyncComparisonToggle();
    }
    private void SyncComparisonToggle()
    {
        if (!Active || UndoActive || _dirty || OriginalToggle.IsOn == _session!.ShowingOriginal) return;
        _setting = true;
        try { OriginalToggle.IsOn = _session.ShowingOriginal; }
        finally { _setting = false; }
    }
    private void RefreshRecovery()
    {
        if (_model is null) return;
        try
        {
            if (HasPendingPreviewRecovery)
            {
                ReloadSession();
                UpdateUi();
            }
            var pending = _recovery.List().Where(s => s.Kind != "calibration" || s.SessionId != _session?.Id
                || HasPendingPreviewRecovery).ToArray();
            bool onlyBindings = pending.Length > 0 && pending.All(s => s.Kind == "binding");
            RecoveryNotice.Title = _model.Text[onlyBindings ? "BindingRecoveryTitle" : "RecoveryTitle"];
            RecoveryNotice.Message = _model.Text[onlyBindings ? "BindingRecoveryMessage" : "RecoveryMessage"];
            RecoveryNotice.IsOpen = pending.Length > 0;
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
            bool hasBindings = report.Sessions.Any(s => s.Kind == "binding");
            if (hasBindings) _model.RefreshPresets();
            if (report.Reason == "preview_busy") Show("RecoveryBusy", InfoBarSeverity.Warning);
            else if (report.Reason == "recovery_required")
                Show(report.Sessions.Any(s => s.Kind != "binding" && s.NeedsRecovery) ? "RecoveryMessage" : "BindingRecoveryMessage",
                    InfoBarSeverity.Warning);
            else if (report.Reason == "recovery_finished")
            {
                bool bindingFailed = report.Sessions.Any(s => s.Kind == "binding" && s.Status == "failed");
                string message = bindingFailed ? "BindingRecoveryFailed" : hasBindings ? "BindingRecoveryFinished" : "RecoveryFinished";
                Show(message, bindingFailed ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _busy = false; UpdateUi(); RefreshRecovery(); }
    }
    private void ShowState()
    {
        if (_session is null) return;
        string key = HasPendingPreviewRecovery ? "RecoveryMessage" : _session.Status switch
        {
            "confirmed" => "CalibrationSaved",
            "reverted" when _session.ReasonCode == "preview_expired" => "PreviewExpiredRestored",
            "reverted" => "CalibrationReverted",
            "superseded" => "CalibrationSuperseded",
            _ => "PreviewActive"
        };
        Show(key, _session.NeedsRecovery && !Active ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
    }
    private void Show(string key, InfoBarSeverity severity)
    { if (_model is null) return; Outcome.Message = _model.Text[key]; Outcome.Severity = severity; Outcome.IsOpen = true; }
    private void ShowError(Exception ex)
    {
        if (_model is null) return;
        if (ex is ProfilePreviewException preview)
        {
            string? guidance = preview.Code switch
            {
                "remote_session_blocked" => "RemoteCalibrationBlocked",
                "mhc2_profile_blocked" => "Mhc2ProfileBlocked",
                "profile_state_unavailable" or "profile_scope_unavailable" => "ProfileStateUnavailable",
                "profile_device_unavailable" => "ProfileDeviceUnavailable",
                "profile_file_unavailable" => "IccFileUnavailable",
                "icc_base_unsupported" => "IccBaseUnsupported",
                "curve_read_failed" => "CurveReadUnavailable",
                "curve_candidate_invalid" => "CurveCandidateInvalid",
                "external_profile_change" => "ProfileChangedBeforeSave",
                "recovery_required" => "RecoveryMessage",
                _ => null
            };
            if (guidance is not null) { Show(guidance, InfoBarSeverity.Warning); return; }
        }
        Outcome.Message = _model.Text["Error"] + " " + ex.Message;
        Outcome.Severity = InfoBarSeverity.Error;
        Outcome.IsOpen = true;
    }
}
