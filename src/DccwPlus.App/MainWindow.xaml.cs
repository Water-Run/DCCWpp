using System.Diagnostics;
using System.Globalization;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;
using DccwPlus.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace DccwPlus.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel Model { get; } = new();
    private bool _updating = true;
    private bool _updatingBindings;
    private bool _updatingLanguage;
    private long _photoRequest;
    private string _headingKey = "CalibrationTitle";
    private readonly StartupService _startup = new();
    private readonly CalibrationBindingService _bindings = new(DataPaths.DefaultRoot);
    private CalibrationRestoreReport? _lastRestore;
    private sealed record BindingOption(CalibrationBinding Binding, string Label);
    private sealed record LanguageOption(string Code, string Label);
    public MainWindow()
    {
        InitializeComponent(); Root.DataContext = Model; Title = "DCCW++";
        SystemBackdrop = new MicaBackdrop(); ExtendsContentIntoTitleBar = true; SetTitleBar(TitleBar);
        DisplayWindowPlacement.SizeInitialWindow(this, new(1180, 940));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        Workspace.Initialize(Model, this);
        Workspace.PreviewUiUpdated += UpdateBackgroundPreview;
        Navigation.SelectedItem = CalibrationNav;
        UpdateLanguage(); _updating = false;
        Root.Loaded += async (_, _) => await RefreshStartupAsync();
        Activated += async (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated && SettingsPanel.Visibility == Visibility.Visible)
                await RefreshStartupAsync();
        };
    }
    private void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (Workspace is null || PageScroll is null) return;
        string page = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "calibration";
        _headingKey = page switch
        {
            "presets" => "Presets",
            "references" => "References",
            "settings" => "Settings",
            _ => "CalibrationTitle"
        };
        PageHeading.Text = Model.Text[_headingKey];
        Workspace.Visibility = page == "calibration" ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.Visibility = page == "calibration" ? Visibility.Collapsed : Visibility.Visible;
        PresetsPanel.Visibility = page == "presets" ? Visibility.Visible : Visibility.Collapsed;
        ReferencesPanel.Visibility = page == "references" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        Workspace.SetCalibrationPageVisible(page == "calibration");
        UpdateBackgroundPreview();
        Model.Status = "";
        if (page == "settings")
        {
            RefreshBindings();
            _ = RefreshStartupAsync();
        }
        if (page == "presets") UpdatePresetSelection();
    }
    private void UpdateBackgroundPreview()
    {
        CancelPreviewKey.IsEnabled = Workspace.CanCancelPreview;
        bool pending = Workspace.HasPendingPreviewRecovery;
        bool show = Workspace.Visibility != Visibility.Visible && (Workspace.HasActivePreview || pending);
        if (show)
        {
            BackgroundPreview.Message = Model.Text[pending ? "RecoveryMessage" : "PreviewOutsideWizard"];
            BackgroundPreview.Severity = pending ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
            BackgroundPreviewAction.Content = Model.Text[pending ? "Calibration" : "CancelCalibration"];
            BackgroundPreviewAction.IsEnabled = pending || Workspace.CanCancelPreview;
        }
        BackgroundPreview.IsOpen = show;
    }
    private async void CancelPreviewKeyInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!Workspace.CanCancelPreview) return;
        args.Handled = true;
        await Workspace.CancelPreviewAsync();
    }
    private async void BackgroundPreviewActionClicked(object sender, RoutedEventArgs e)
    {
        if (Workspace.HasPendingPreviewRecovery) Navigation.SelectedItem = CalibrationNav;
        else await Workspace.CancelPreviewAsync();
    }
    private void RefreshClicked(object sender, RoutedEventArgs e)
    { try { RefreshPresetSelection((PresetList.SelectedItem as CalibrationPreset)?.Id); } catch (Exception ex) { ShowError(ex); } }
    private async void ImportClicked(object sender, RoutedEventArgs e) => await ImportPresetAsync(asCopy: false);
    private async void ImportCopyClicked(object sender, RoutedEventArgs e) => await ImportPresetAsync(asCopy: true);
    private async Task ImportPresetAsync(bool asCopy)
    {
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json"); InitializePicker(picker);
            var file = await picker.PickSingleFileAsync(); if (file is null) return;
            var imported = Model.PresetStore.Import(file.Path, asCopy);
            RefreshPresetSelection(imported.Preset.Id);
            Model.Status = imported.AlreadyPresent
                ? string.Format(Model.Text["PresetAlreadyImported"], imported.Preset.Name)
                : Model.Text["Imported"] + " " + imported.Preset.Name;
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not CalibrationPreset preset) { Model.Status = Model.Text["SelectPreset"]; return; }
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "preset.dccw" };
            picker.FileTypeChoices.Add("DCCW++", new List<string> { ".json" }); InitializePicker(picker);
            var file = await picker.PickSaveFileAsync(); if (file is null) return;
            Model.PresetStore.Export(preset.Id, file.Path, overwrite: true); Model.Status = Model.Text["Exported"] + " " + file.Path;
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void LoadClicked(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not CalibrationPreset preset) { Model.Status = Model.Text["SelectPreset"]; return; }
        Workspace.LoadPreset(preset); Navigation.SelectedItem = CalibrationNav;
    }
    private void RefreshPresetSelection(Guid? id)
    {
        Model.RefreshPresets();
        PresetList.SelectedItem = Model.Presets.FirstOrDefault(p => p.Id == id);
    }
    private void PresetSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePresetSelection();
    private void UpdatePresetSelection()
    {
        if (PresetList is null || RenamePresetButton is null || DeletePresetButton is null || PresetBindingsText is null) return;
        RenamePresetButton.IsEnabled = false;
        DeletePresetButton.IsEnabled = false;
        PresetBindingsText.Text = "";
        if (PresetList.SelectedItem is not CalibrationPreset preset) return;
        try
        {
            RenamePresetButton.IsEnabled = true;
            var bindings = Model.PresetStore.BindingsFor(preset.Id);
            DeletePresetButton.IsEnabled = bindings.Count == 0;
            if (bindings.Count > 0)
                PresetBindingsText.Text = string.Format(Model.Text["PresetBoundTo"],
                    string.Join(", ", bindings.Select(b => b.DisplayName).Distinct()));
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void RenamePresetClicked(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not CalibrationPreset preset) return;
        try
        {
            var name = new TextBox { Header = Model.Text["PresetName"], Text = preset.Name, MaxLength = 100 };
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = Model.Text["RenamePreset"],
                Content = name,
                PrimaryButtonText = Model.Text["RenamePreset"],
                CloseButtonText = Model.Text["Dismiss"],
                DefaultButton = ContentDialogButton.Primary,
            };
            name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var updated = Model.PresetStore.Rename(preset.Id, name.Text);
            RefreshPresetSelection(updated.Id);
            Model.Status = string.Format(Model.Text["PresetRenamed"], updated.Name);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void DeletePresetClicked(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not CalibrationPreset preset) return;
        try
        {
            if (!await ConfirmAsync("DeletePreset", "DeletePresetConfirm", preset.Name)) return;
            _bindings.DeletePreset(preset.Id);
            RefreshPresetSelection(null);
            Model.Status = string.Format(Model.Text["PresetDeleted"], preset.Name);
        }
        catch (PresetOperationException ex) when (ex.Code == "preset_in_use")
        {
            UpdatePresetSelection();
            Model.Status = Model.Text["PresetInUse"];
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async Task<bool> ConfirmAsync(string title, string question, string name)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Model.Text[title],
            Content = new TextBlock { Text = string.Format(Model.Text[question], name), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Model.Text[title],
            CloseButtonText = Model.Text["Dismiss"],
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    private async void ChoosePhotosClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(); foreach (string type in new[] { ".png", ".jpg", ".jpeg", ".tif", ".tiff" }) picker.FileTypeFilter.Add(type);
            InitializePicker(picker); var files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0) return;
            PhotoPicker.ItemsSource = files; PhotoPicker.SelectedIndex = 0;
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void PhotoChanged(object sender, SelectionChangedEventArgs e)
    {
        var request = ++_photoRequest;
        ReferencePhoto.Source = null;
        ReferencePhotoError.IsOpen = false;
        if (PhotoPicker.SelectedItem is not StorageFile file) return;
        try
        {
            using var stream = await file.OpenReadAsync();
            if (request != _photoRequest) return;
            var image = new BitmapImage();
            image.ImageFailed += (_, _) => ShowReferencePhotoError(request);
            await image.SetSourceAsync(stream);
            if (request == _photoRequest && !ReferencePhotoError.IsOpen) ReferencePhoto.Source = image;
        }
        catch (Exception) { ShowReferencePhotoError(request); }
    }
    private void ShowReferencePhotoError(long request)
    {
        if (request != _photoRequest) return;
        ReferencePhoto.Source = null;
        ReferencePhotoError.IsOpen = true;
    }
    private async void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLanguage || LanguagePicker.SelectedItem is not LanguageOption item || item.Code == Model.Text.Language) return;
        try
        {
            Model.Text.Language = item.Code;
            Model.SettingsStore.Save(new() { Language = Model.Text.Language });
            UpdateLanguage();
            RefreshBindings();
            UpdatePresetSelection();
            await RefreshStartupAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void UpdateLanguage()
    {
        Root.Language = Model.Text.EffectiveLanguage; Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Model.Text.EffectiveLanguage;
        PageHeading.Text = Model.Text[_headingKey];
        if (Navigation.SettingsItem is NavigationViewItem settings) settings.Content = Model.Text["Settings"];
        LanguageOption[] languages =
        [
            new("system", Model.Text["SystemLanguage"]), new("en-US", "English"),
            new("zh-Hans", "简体中文"), new("zh-Hant", "繁體中文")
        ];
        _updatingLanguage = true;
        try
        {
            LanguagePicker.ItemsSource = languages;
            LanguagePicker.SelectedItem = languages.First(item => item.Code == Model.Text.Language);
        }
        finally { _updatingLanguage = false; }
    }
    private async Task RefreshStartupAsync(bool? enabled = null)
    {
        if (_updating) return;
        _updating = true;
        StartupToggle.IsEnabled = false;
        try
        {
            if (enabled is bool request)
            {
                try { await _startup.SetEnabledAsync(request); }
                catch (Exception ex) { ShowError(ex); }
            }
            var state = await _startup.GetAsync();
            StartupToggle.IsOn = state.IsEnabled;
            StartupToggle.IsEnabled = (state.CanEnable || state.IsEnabled) && !state.State.Contains("Policy", StringComparison.Ordinal);
            StartupStateText.Text = Model.Text["StartupState_" + state.State];
            StartupSettingsLink.Visibility = state.State == "DisabledByUser" ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { ShowError(ex); StartupSettingsLink.Visibility = Visibility.Collapsed; }
        finally { _updating = false; }
    }
    private async void StartupChanged(object sender, RoutedEventArgs e) => await RefreshStartupAsync(StartupToggle.IsOn);

    private void OpenWindowsStartupSettingsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RefreshRestoreResultsClicked(object sender, RoutedEventArgs e) => RefreshBindings();

    private void RefreshBindings()
    {
        _updatingBindings = true;
        try
        {
            string? selectedKey = (BindingPicker.SelectedItem as BindingOption)?.Binding.PersistenceKey;
            _lastRestore = _bindings.LastStartupReport();
            UpdateRestoreReport();
            var bindings = _bindings.List();
            var displays = bindings.Count == 0 ? [] : new WindowsDisplayService().Enumerate();
            var options = bindings.Select(binding =>
            {
                var matches = displays.Where(d => d.PersistenceKey == binding.PersistenceKey).ToArray();
                string location = matches.Length switch
                {
                    0 => Model.Text["BindingDisconnected"],
                    1 => matches[0].DeviceName,
                    _ => Model.Text["BindingAmbiguous"]
                };
                return new BindingOption(binding, $"{binding.DisplayName} ({location})");
            }).ToArray();
            BindingPicker.ItemsSource = options;
            BindingPicker.IsEnabled = options.Length > 0;
            BindingPicker.SelectedItem = options.FirstOrDefault(o => o.Binding.PersistenceKey == selectedKey) ?? options.FirstOrDefault();
            UpdateBindingState();
        }
        catch (Exception ex) { BindingToggle.IsEnabled = false; RemoveBindingButton.IsEnabled = false; ShowError(ex); }
        finally { _updatingBindings = false; }
    }

    private void UpdateRestoreReport()
    {
        if (_lastRestore is not { } report)
        {
            RestoreReportText.Text = Model.Text["RestoreReportNone"];
            return;
        }

        string trigger = Model.Text[report.Trigger switch
        {
            "sign_in" => "RestoreTriggerSignIn",
            "resume" => "RestoreTriggerResume",
            "display_changed" => "RestoreTriggerDisplayChanged",
            _ => "RestoreTriggerUnknown"
        }];
        var culture = CultureInfo.GetCultureInfo(Model.Text.EffectiveLanguage);
        string time = report.Time.ToLocalTime().ToString("g", culture);
        string summary = string.Format(culture, Model.Text["RestoreReportSummary"], time, trigger);
        if (report.Reason != "restore_finished") summary += " " + DescribeRestoreReason(report.Reason);
        RestoreReportText.Text = summary;
    }

    private string DescribeRestoreReason(string reason)
    {
        string? key = reason switch
        {
            "no_bindings_enabled" => "RestoreNoBindings",
            "startup_disabled" => "RestoreStartupDisabled",
            "startup_unavailable" => "StartupState_unavailable",
            "preview_busy" => "RestorePreviewBusy",
            "recovery_required" => "RecoveryMessage",
            "binding_disabled" => "RestoreBindingDisabled",
            "display_disconnected" => "RestoreDisplayDisconnected",
            "display_identity_ambiguous" => "RestoreDisplayAmbiguous",
            "display_state_changed" or "recovery_target_changed" => "RestoreDisplayChanged",
            "sdr_path_blocked" => "SdrRequired",
            "shared_source_blocked" => "MirroredDisplay",
            "mhc2_profile_blocked" => "Mhc2ProfileBlocked",
            "remote_session_blocked" => "RemoteCalibrationBlocked",
            "profile_selection_changed" or "external_profile_change" => "RestoreProfileChanged",
            "profile_file_changed" => "IccFileChanged",
            "profile_file_unavailable" => "IccFileUnavailable",
            "profile_state_unavailable" or "profile_scope_unavailable" => "ProfileStateUnavailable",
            "profile_device_unavailable" => "ProfileDeviceUnavailable",
            "curve_read_failed" => "CurveReadUnavailable",
            "external_curve_change" => "RestoreCurveChanged",
            "restore_data_unavailable" => "RestoreDataUnavailable",
            _ => null
        };
        return key is null ? string.Format(Model.Text["RestoreReasonCode"], reason) : Model.Text[key];
    }

    private void BindingSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingBindings || BindingToggle is null) return;
        _updatingBindings = true;
        try { UpdateBindingState(); }
        finally { _updatingBindings = false; }
    }
    private void UpdateBindingState()
    {
        var option = BindingPicker.SelectedItem as BindingOption;
        BindingToggle.IsEnabled = option is not null;
        RemoveBindingButton.IsEnabled = option is not null;
        BindingToggle.IsOn = option?.Binding.RestoreEnabled == true;
        if (option is null)
        {
            BindingStateText.Text = Model.Text["BindingNone"];
            return;
        }
        var last = _lastRestore?.Bindings.FirstOrDefault(b => b.PersistenceKey == option.Binding.PersistenceKey);
        string key = last?.Status switch
        {
            "restored" => "BindingLastRestored",
            "unchanged" => "BindingLastUnchanged",
            "recovery-pending" => "BindingLastPending",
            "failed" or "reverted" => "BindingLastFailed",
            "skipped" or "superseded" => "BindingLastSkipped",
            _ => "BindingNoResult"
        };
        string message = Model.Text[key];
        if (last is not null && last.Status is not ("restored" or "unchanged"))
            message += " " + DescribeRestoreReason(last.ReasonCode);
        BindingStateText.Text = message;
    }
    private void BindingChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingBindings || BindingPicker.SelectedItem is not BindingOption option) return;
        try { _bindings.SetEnabled(option.Binding.PersistenceKey, BindingToggle.IsOn); }
        catch (Exception ex) { ShowError(ex); }
        RefreshBindings();
    }
    private async void RemoveBindingClicked(object sender, RoutedEventArgs e)
    {
        if (BindingPicker.SelectedItem is not BindingOption option) return;
        try
        {
            if (!await ConfirmAsync("RemoveDisplayBinding", "RemoveBindingConfirm", option.Binding.DisplayName)) return;
            _bindings.Remove(option.Binding.PersistenceKey);
            RefreshBindings();
            UpdatePresetSelection();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
    private void ShowError(Exception ex) => Model.Status = Model.Text["Error"] + " " + ex.Message;
}
