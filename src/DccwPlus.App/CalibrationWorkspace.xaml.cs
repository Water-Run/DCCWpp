using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.Globalization.NumberFormatting;

namespace DccwPlus.App;

public sealed partial class CalibrationWorkspace : UserControl
{
    private static readonly string[] StepLabelKeys = ["StepPrepare", "StepGamma", "StepBrightness", "StepContrast", "StepBalance", "StepReview"];
    private MainViewModel? _model;
    private Window? _window;
    private Window? _fullScreenWindow;
    private Window? _activeWindow;
    private PatternSurface? _fullScreenPattern;
    private (Button Exit, Button Compare, Button Cancel, TextBlock Guide, TextBlock Countdown)? _fullScreenControls;
    private GammaPreviewClient? _client;
    private readonly PreviewRecoveryService _recovery = new(DataPaths.DefaultRoot);
    private GammaCalibrationSession? _session;
    private CalibrationPreset _draft = CalibrationPreset.Create("Calibration");
    private DispatcherQueueTimer? _timer, _debounce, _displayRefresh;
    private bool _setting, _busy, _pushing, _dirty, _timerBusy;
    private bool _displayRefreshPending, _closed;
    private readonly HashSet<string> _ambiguousDisplayIdentities = new(StringComparer.OrdinalIgnoreCase);
    private bool _previewReferenceLeftDisplay;
    private bool _workerExited;
    private bool _calibrationPageVisible = true;
    private DateTimeOffset _lastHeartbeat;
    private bool Active => !_workerExited && _session?.Status == "previewing";
    private bool UndoActive => Active && _session?.UndoOf is not null;
    private bool ReviewSelected => StepPicker.SelectedIndex == 5;
    private bool IsReviewing => Active && _session?.IsReviewing == true;
    private DisplayInfo? SelectedDisplay => (DisplayPicker.SelectedItem as DisplayOption)?.Display;
    private bool AnnounceWizardChanges => _calibrationPageVisible && ReferenceEquals(_activeWindow, _window);
    private bool PreviewUpdatePending => Active && !UndoActive && (_dirty || ReviewSelected != IsReviewing);
    private bool HasFullScreenPhoto => _fullScreenWindow is not null && _fullScreenPattern is null;
    internal bool HasActivePreview => Active;
    internal bool CanCancelPreview => Active && !_busy;
    internal bool CanOpenReferencePhoto => !_closed && !_busy && !_pushing;
    internal bool HasPendingPreviewRecovery => _session?.NeedsRecovery == true && (_workerExited || _session.Status == "recovery-pending");
    internal event Action? PreviewUiUpdated;
    internal event Action? ReferencePhotoPlacementFailed;

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
        _ambiguousDisplayIdentities.UnionWith(MonitorIdentity.AmbiguousPersistenceKeys(model.Displays));
        UpdateDisplayOptions(model.Displays.FirstOrDefault());
        UpdateStepLabels();
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromSeconds(1); _timer.Tick += TimerTick;
        _debounce = DispatcherQueue.CreateTimer(); _debounce.Interval = TimeSpan.FromMilliseconds(180); _debounce.IsRepeating = false;
        _debounce.Tick += async (_, _) => await PushAsync();
        var dispatcher = DispatcherQueue;
        _displayRefresh = dispatcher.CreateTimer();
        _displayRefresh.Interval = TimeSpan.FromMilliseconds(350);
        _displayRefresh.IsRepeating = false;
        _displayRefresh.Tick += RefreshDisplaysWhenIdle;
        DisplayPicker.DropDownClosed += (_, _) => ScheduleDisplayRefresh();
        EventHandler displaySettingsChanged = (_, _) => dispatcher.TryEnqueue(RequestDisplayRefresh);
        bool watchingDisplay = false;
        try
        {
            SystemEvents.DisplaySettingsChanged += displaySettingsChanged;
            watchingDisplay = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException) { Debug.WriteLine(ex); }
        model.Text.LanguageChanged += (_, _) =>
        {
            UpdateDisplayOptions(SelectedDisplay);
            UpdateStepLabels(); UpdateStep(); UpdateUi(); RefreshRecovery();
        };
        window.Closed += (_, _) =>
        {
            _closed = true;
            if (watchingDisplay) SystemEvents.DisplaySettingsChanged -= displaySettingsChanged;
            _displayRefresh?.Stop();
            CloseFullScreen();
        };
        window.Activated += (_, e) =>
        {
            TrackActiveWindow(window, e.WindowActivationState);
            if (e.WindowActivationState != WindowActivationState.Deactivated) RequestDisplayRefresh();
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
            CloseFullScreen();
        }
        else
        {
            RequestDisplayRefresh();
            if (PreviewUpdatePending) _debounce?.Start();
        }
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
        if (SelectedDisplay is not { } selected) { Show("SelectDisplay", InfoBarSeverity.Warning); return; }
        _busy = true; UpdateUi();
        try
        {
            var connected = new WindowsDisplayService().Enumerate();
            var display = CheckSelectedDisplay(selected, connected);
            if (display is null) return;
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
            _previewReferenceLeftDisplay = false;
            _session = await _client.BeginAsync(display.Id, _draft, 60, windowOwned: true);
            if (Active) { _timer!.Start(); StepPicker.SelectedIndex = 1; Show("PreviewActive", InfoBarSeverity.Informational); }
            else ShowState();
        }
        catch (Exception ex) { ShowError(ex); RefreshRecovery(); }
        finally { _busy = false; UpdateUi(); }
    }
    private async Task<bool> PushAsync(bool fromFullScreenPhoto = false)
    {
        if (_model is null || _client is null || !Active || UndoActive || _busy || _pushing) return false;
        if (!_calibrationPageVisible && !(fromFullScreenPhoto && HasFullScreenPhoto && IsReviewing && ReviewSelected)) return false;
        _debounce?.Stop();
        if (!PreviewUpdatePending) return true;
        _pushing = true; UpdateUi();
        bool sendingEdits = false;
        try
        {
            do
            {
                if (!OnPreviewDisplay()) { await CancelAsync(targetMoved: true); return false; }
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
            { _timer!.Stop(); CloseFullScreen(); ShowState(); RefreshRecovery(); }
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
        bool openClearType = !undo && ClearTypeOption.IsChecked == true;
        _busy = true; UpdateUi();
        try
        {
            CloseFullScreen();
            _session = await _client.CommandAsync(_session!.Id, "confirm");
            ShowState();
            if (_session.Status == "confirmed" && !undo)
                FinishCalibration(openClearType);
        }
        catch (Exception ex)
        {
            try { ReloadSession(); }
            catch (Exception readError) when (readError is IOException or InvalidDataException or UnauthorizedAccessException)
            { Debug.WriteLine(readError); }
            if (_session?.Status == "confirmed") ShowState();
            else ShowError(ex);
        }
        finally { _busy = false; if (!Active) _timer!.Stop(); UpdateUi(); RefreshRecovery(); }
    }
    private void FinishCalibration(bool openClearType)
    {
        try { _model!.RefreshPresets(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Debug.WriteLine(ex);
            Outcome.Message += " " + _model!.Text["PresetRefreshAfterSaveFailed"];
            Outcome.Severity = InfoBarSeverity.Warning;
        }
        if (openClearType) OpenClearType();
    }
    private void RetryClearTypeClicked(object sender, RoutedEventArgs e) => OpenClearType();
    private void OpenClearType()
    {
        try
        {
            string tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cttune.exe");
            using var process = Process.Start(new ProcessStartInfo(tool) { UseShellExecute = true });
            ClearTypeNotice.IsOpen = false;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Debug.WriteLine(ex);
            ClearTypeNotice.IsOpen = true;
        }
    }
    private async void UndoCalibrationClicked(object sender, RoutedEventArgs e)
    {
        if (Active || _busy || _client is null || _window is null || SelectedDisplay is not { } selected) return;
        _busy = true;
        UpdateUi();
        try
        {
            var display = CheckSelectedDisplay(selected, new WindowsDisplayService().Enumerate());
            if (display is null) return;
            var backend = new WindowsGammaCalibrationBackend(DataPaths.DefaultRoot);
            var current = backend.Read(display.Id);
            var saved = new GammaCalibrationStore(DataPaths.DefaultRoot).Sessions().FirstOrDefault(s =>
                s.Status == "confirmed" && !s.IsBindingRestore && s.UndoOf is null && MonitorIdentity.MatchesSavedDisplay(s.Original, display)
                && s.InstalledProfile is not null && ProfilePreviewPolicy.EqualName(s.InstalledProfile, current.ProfileName));
            if (saved is null)
            {
                Show("NoUndoCalibration", InfoBarSeverity.Informational);
                return;
            }
            var plan = backend.PlanUndo(saved.Id);
            if (!plan.CanPreview || plan.DisplayId != display.Id)
            {
                Show(plan.ReasonCode switch
                {
                    "preview_busy" => "DisplayOperationBusy",
                    "recovery_required" => "RecoveryMessage",
                    _ => "UndoUnavailable"
                }, InfoBarSeverity.Warning);
                return;
            }
            if (!DisplayWindowPlacement.MoveToDisplay(_window, current.DeviceName))
            {
                Show("DisplayUnavailable", InfoBarSeverity.Warning);
                return;
            }
            OriginalToggle.IsOn = false;
            _previewReferenceLeftDisplay = false;
            _session = await _client.BeginUndoAsync(saved.Id, windowOwned: true, expectedDisplayId: display.Id);
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
            CloseFullScreen();
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
        var display = SelectedDisplay;
        bool hasDisplay = display is not null;
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
        StartButton.IsEnabled = !Active && !HasPendingPreviewRecovery && !_busy && !remote && display is not null
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
            CountdownText.Text = PreviewCountdownText();
        UpdateStepInstructions(Math.Max(0, StepPicker.SelectedIndex));
        SetLiveText(ComparisonState, _model.Text[undo ? "ShowingPreviousSavedCurve" : !Active ? "ComparisonRequiresPreview"
            : updatingComparison ? "UpdatingComparison" : _session!.ShowingOriginal ? "ShowingOriginal" : "ShowingAdjusted"],
            AnnounceWizardChanges && ReviewSelected);
        UpdateFullScreenControls();
        UpdateDisplaySummary(remote);
        ResetButton.Visibility = StepPicker.SelectedIndex is 1 or 4 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleDisplayRefresh();
        PreviewUiUpdated?.Invoke();
    }

    private string PreviewCountdownText() => string.Format(_model!.Text[UndoActive ? "UndoCountdown" : "PreviewCountdown"],
        Math.Max(0, (int)Math.Ceiling((_session!.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds)));

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
        UpdateDisplaySelection();
    }
    private void UpdateDisplaySelection()
    {
        CloseFullScreen();
        Outcome.IsOpen = false;
        ClearTypeNotice.IsOpen = false;
        UpdateUi();
    }
    private void UpdateDisplayOptions(DisplayInfo? selected)
    {
        var options = _model!.Displays.Select(display =>
        {
            List<string> details = [];
            if (display.IsPrimary) details.Add(_model.Text["PrimaryDisplay"]);
            if (display.PositionX is int x && display.PositionY is int y)
                details.Add(string.Format(_model.Text["DisplayDesktopPosition"], x, y));
            if (details.Count == 0)
                details.Add(display.DeviceName.StartsWith(@"\\.\", StringComparison.Ordinal) ? display.DeviceName[4..] : display.DeviceName);
            return new DisplayOption(display, string.Join(" · ", details));
        }).ToArray();

        var repeatedLabels = options.GroupBy(option => option.ToString(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < options.Length; index++)
        {
            var option = options[index];
            if (!repeatedLabels.Contains(option.ToString())) continue;
            string id = string.Format(_model.Text["DisplayQueryId"], option.Display.Id);
            options[index] = option with { Details = option.Details + " · " + id };
        }

        bool previousSetting = _setting;
        _setting = true;
        try
        {
            DisplayPicker.ItemsSource = options;
            DisplayPicker.SelectedItem = options.FirstOrDefault(option => option.Display == selected);
        }
        finally { _setting = previousSetting; }
    }
    private void UpdateDisplaySummary(bool remote)
    {
        var text = _model!.Text;
        if (SelectedDisplay is not { } display)
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
        if (_window is null || SelectedDisplay is not { } selected || Active) return;
        try
        {
            var display = CheckSelectedDisplay(selected, new WindowsDisplayService().Enumerate());
            if (display is null) return;
            if (!DisplayWindowPlacement.MoveToDisplay(_window, display.DeviceName)) Show("DisplayUnavailable", InfoBarSeverity.Warning);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void RefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_model is null || Active || _busy) return;
        _displayRefreshPending = false;
        _displayRefresh?.Stop();
        try { RefreshDisplayOptions(manual: true); }
        catch (Exception ex) { ShowError(ex); }
        RefreshRecovery();
    }

    private bool CanRefreshDisplays => !_closed && _calibrationPageVisible && !Active && !_busy && !_pushing
        && !DisplayPicker.IsDropDownOpen;

    private void RequestDisplayRefresh()
    {
        _displayRefreshPending = true;
        ScheduleDisplayRefresh();
    }

    private void ScheduleDisplayRefresh()
    {
        if (_displayRefreshPending && CanRefreshDisplays && _displayRefresh is { IsRunning: false } timer)
            timer.Start();
    }

    private void RefreshDisplaysWhenIdle(DispatcherQueueTimer sender, object args)
    {
        if (!_displayRefreshPending || !CanRefreshDisplays) return;
        _displayRefreshPending = false;
        try { RefreshDisplayOptions(manual: false); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void RefreshDisplayOptions(bool manual)
    {
        var selected = SelectedDisplay;
        if (!_model!.RefreshDisplays() && !manual) return;

        var current = ResolveSelectedDisplay(selected, _model.Displays);
        UpdateDisplayOptions(current);
        bool lostSelection = selected is not null && current is null;
        if (manual || lostSelection) UpdateDisplaySelection();
        else
        {
            if (selected != current) CloseFullScreen();
            UpdateUi();
        }
        if (lostSelection) Show("DisplaySelectionLost", InfoBarSeverity.Warning);
    }

    private DisplayInfo? ResolveSelectedDisplay(DisplayInfo? selected, IReadOnlyList<DisplayInfo> connected)
    {
        _ambiguousDisplayIdentities.UnionWith(MonitorIdentity.AmbiguousPersistenceKeys(connected));
        bool ambiguous = selected?.PersistenceKey is { } key && _ambiguousDisplayIdentities.Contains(key);
        return MonitorIdentity.ResolveSelectedDisplay(selected, connected, ambiguous);
    }

    private DisplayInfo? CheckSelectedDisplay(DisplayInfo selected, IReadOnlyList<DisplayInfo> connected)
    {
        var current = ResolveSelectedDisplay(selected, connected);
        if (current?.Id == selected.Id) return current;
        RequestDisplayRefresh();
        Show("DisplayUnavailable", InfoBarSeverity.Warning);
        return null;
    }
    private void FullScreenClicked(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        var pattern = new PatternSurface();
        pattern.SetPattern((ReferencePatternKind)Math.Max(0, StepPicker.SelectedIndex));
        AutomationProperties.SetName(pattern, _model.Text["CalibrationPattern"]);
        ShowFullScreen(pattern, _model.Text["CalibrationPattern"], FullScreenButton);
    }

    internal void OpenReferencePhoto(ImageSource source, string fileName, Button launchButton)
    {
        if (!CanOpenReferencePhoto) return;
        var photo = new Image { Source = source, Stretch = Stretch.Uniform };
        AutomationProperties.SetName(photo, fileName);
        ShowFullScreen(photo, fileName, launchButton);
    }

    internal void CloseReferencePhoto()
    {
        if (HasFullScreenPhoto) CloseFullScreen();
    }

    private void ShowFullScreen(FrameworkElement content, string title, Button launchButton)
    {
        bool photo = content is Image;
        void Unavailable()
        {
            CloseFullScreen();
            ReportFullScreenPlacement(photo, "DisplayUnavailable");
        }

        DisplayInfo? display;
        try
        {
            var selected = SelectedDisplay;
            display = selected is null ? null : ResolveSelectedDisplay(selected, new WindowsDisplayService().Enumerate());
            if (display is null || display.Id != selected!.Id)
            {
                RequestDisplayRefresh();
                Unavailable();
                return;
            }
        }
        catch (Exception ex)
        {
            if (photo) { Debug.WriteLine(ex); Unavailable(); }
            else ShowError(ex);
            return;
        }
        if (Active && (!OnPreviewDisplay() || display.TargetKey != _session!.Original.TargetKey
            || display.SourceId != _session.Original.SourceId || display.DeviceName != _session.Original.DeviceName))
        {
            _previewReferenceLeftDisplay = true;
            Unavailable();
            return;
        }
        if (_fullScreenWindow is not null && HasFullScreenPhoto == photo
            && DisplayWindowPlacement.IsFullScreenOnDisplay(_fullScreenWindow, display.DeviceName))
        { _fullScreenWindow.Activate(); return; }
        CloseFullScreen();
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
        var root = new Grid { Background = photo ? new SolidColorBrush(Microsoft.UI.Colors.Black) : null };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(content);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        var window = new Window { Title = title, Content = root };
        _fullScreenWindow = window;
        _fullScreenPattern = content as PatternSurface;
        _fullScreenControls = (exit, compare, cancel, guide, countdown);
        UpdateFullScreenControls();
        window.Activated += (_, e) => TrackActiveWindow(window, e.WindowActivationState);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeWindow, window)) _activeWindow = null;
            if (ReferenceEquals(_fullScreenWindow, window))
            {
                _fullScreenWindow = null;
                _fullScreenPattern = null;
                _fullScreenControls = null;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_closed && _fullScreenWindow is null && launchButton.IsLoaded && launchButton.IsEnabled)
                        launchButton.Focus(FocusState.Programmatic);
                });
            }
        };
        exit.Click += (_, _) => window.Close();
        exit.Loaded += (_, _) => exit.Focus(FocusState.Programmatic);
        compare.Click += async (_, _) =>
        {
            if (!OriginalToggle.IsEnabled) return;
            if (!OnPreviewDisplay()) { await CancelAsync(targetMoved: true); return; }
            OriginalToggle.IsOn = !_session!.ShowingOriginal;
            await PushAsync(fromFullScreenPhoto: photo);
        };
        cancel.Click += async (_, _) => await CancelAsync();
        root.KeyDown += (_, key) =>
        {
            if (key.Key != global::Windows.System.VirtualKey.Escape) return;
            key.Handled = true;
            window.Close();
        };
        try
        {
            if (!DisplayWindowPlacement.MoveToDisplay(window, display.DeviceName)) { Unavailable(); return; }
            window.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            if (!DisplayWindowPlacement.IsOnDisplay(window, display.DeviceName)) { Unavailable(); return; }
            window.Activate();
            if (!DisplayWindowPlacement.IsFullScreenOnDisplay(window, display.DeviceName)) { Unavailable(); return; }
            window.AppWindow.Changed += (_, change) =>
            {
                if (change.DidPositionChange || change.DidSizeChange || change.DidPresenterChange)
                    DispatcherQueue.TryEnqueue(() => CheckFullScreenPlacement(window, display.DeviceName));
            };
            if (!photo && Outcome.Message == _model!.Text["FullScreenLost"]) Outcome.IsOpen = false;
        }
        catch (Exception ex)
        {
            if (photo) { Debug.WriteLine(ex); Unavailable(); }
            else { CloseFullScreen(); ShowError(ex); }
        }
    }

    private void CheckFullScreenPlacement(Window window, string deviceName)
    {
        if (!ReferenceEquals(_fullScreenWindow, window)) return;
        bool onDisplay = DisplayWindowPlacement.IsFrameOnDisplay(window, deviceName, out bool fillsDisplay);
        if (onDisplay && fillsDisplay) return;

        bool cancelPreview = Active && !onDisplay;
        if (cancelPreview) _previewReferenceLeftDisplay = true;
        bool photo = HasFullScreenPhoto;
        CloseFullScreen();
        if (!cancelPreview) ReportFullScreenPlacement(photo, "FullScreenLost");
    }

    private void ReportFullScreenPlacement(bool photo, string key)
    {
        if (photo) ReferencePhotoPlacementFailed?.Invoke();
        else Show(key, InfoBarSeverity.Warning);
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
        bool photo = HasFullScreenPhoto;
        controls.Guide.Visibility = !photo || Active ? Visibility.Visible : Visibility.Collapsed;
        SetLiveText(controls.Guide, photo || reviewing ? ComparisonState.Text : StepInstructions.Text,
            ReferenceEquals(_fullScreenWindow, _activeWindow));
        controls.Countdown.Visibility = photo && Active ? Visibility.Visible : CountdownText.Visibility;
        if (controls.Countdown.Visibility == Visibility.Visible) controls.Countdown.Text = PreviewCountdownText();
    }

    private void TrackActiveWindow(Window window, WindowActivationState activation)
    {
        if (activation != WindowActivationState.Deactivated) _activeWindow = window;
        else if (ReferenceEquals(_activeWindow, window)) _activeWindow = null;
    }
    private bool OnPreviewDisplay()
    {
        return !_previewReferenceLeftDisplay && _session is not null && _window is not null
            && DisplayWindowPlacement.IsOnDisplay(_window, _session.Original.DeviceName)
            && (_fullScreenWindow is null || DisplayWindowPlacement.IsFrameOnDisplay(_fullScreenWindow, _session.Original.DeviceName, out _));
    }
    private void CloseFullScreen()
    {
        var window = _fullScreenWindow;
        _fullScreenWindow = null;
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
        catch (Exception ex)
        {
            RecoveryNotice.Title = _model.Text["RecoveryTitle"];
            RecoveryNotice.Message = _model.Text["Error"] + " " + ex.Message;
            RecoveryNotice.IsOpen = true;
        }
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
        if (_session.Status == "confirmed")
        {
            ShowConfirmedOutcome(_session);
            return;
        }
        string key = HasPendingPreviewRecovery ? "RecoveryMessage" : _session.Status switch
        {
            "reverted" when _session.ReasonCode == "preview_expired" => _session.CurveReadbackVerified
                ? "PreviewExpiredRestored" : "PreviewExpiredBeforeWrite",
            "reverted" when _session.ReasonCode == "preview_not_applied" => "PreviewEndedBeforeWrite",
            "reverted" when _session.ReasonCode == "owner_exited" && !_session.CurveReadbackVerified => "PreviewEndedBeforeWrite",
            "reverted" when _session.ReasonCode == "profile_selection_changed" => "ProfileChangedBeforeSave",
            "reverted" => "CalibrationReverted",
            "superseded" => "CalibrationSuperseded",
            _ => "PreviewActive"
        };
        Show(key, (_session.NeedsRecovery && !Active) || key == "ProfileChangedBeforeSave"
            ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
    }
    private void ShowConfirmedOutcome(GammaCalibrationSession session)
    {
        if (session.UndoOf is not null)
        {
            bool bindingWarning = session.BindingStatus is "failed" or "pending";
            Show(bindingWarning ? "UndoSavedBindingWarning" : "UndoSaved",
                bindingWarning ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            return;
        }
        string key = session.BindingStatus switch
        {
            "saved" => "CalibrationSaved",
            "pending" => "CalibrationSavedBindingPending",
            _ => "CalibrationSavedWithoutBinding"
        };
        Show(key, session.BindingStatus == "saved" ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        if (session.InstalledProfile is { } profile) Outcome.Message += " " + profile;
    }
    private void Show(string key, InfoBarSeverity severity)
    {
        if (_model is null) return;
        ClearTypeNotice.IsOpen = false;
        Outcome.Message = _model.Text[key];
        Outcome.Severity = severity;
        Outcome.IsOpen = true;
    }
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
                "owner_exited" => "PreviewOwnerExited",
                "preview_busy" => "DisplayOperationBusy",
                "external_profile_change" or "profile_selection_changed" => "ProfileChangedBeforeSave",
                "recovery_required" => "RecoveryMessage",
                _ => null
            };
            if (guidance is not null) { Show(guidance, InfoBarSeverity.Warning); return; }
        }
        ClearTypeNotice.IsOpen = false;
        Outcome.Message = _model.Text["Error"] + " " + ex.Message;
        Outcome.Severity = InfoBarSeverity.Error;
        Outcome.IsOpen = true;
    }
}
