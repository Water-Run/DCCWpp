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
        if (page == "settings") RefreshBindings();
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
        if (PhotoPicker.SelectedItem is not StorageFile file) return;
        try { using var stream = await file.OpenReadAsync(); var image = new BitmapImage(); await image.SetSourceAsync(stream); ReferencePhoto.Source = image; }
        catch (Exception ex) { ShowError(ex); }
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
    private async Task RefreshStartupAsync()
    {
        try
        {
            var state = await _startup.GetAsync(); _updating = true; StartupToggle.IsOn = state.IsEnabled;
            StartupToggle.IsEnabled = (state.CanEnable || state.IsEnabled) && !state.State.Contains("Policy", StringComparison.Ordinal);
            StartupStateText.Text = Model.Text["StartupState_" + state.State];
        }
        catch (Exception ex) { ShowError(ex); StartupToggle.IsEnabled = false; }
        finally { _updating = false; }
    }
    private async void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        try { await _startup.SetEnabledAsync(StartupToggle.IsOn); } catch (Exception ex) { ShowError(ex); }
        await RefreshStartupAsync();
    }
    private void RefreshBindings()
    {
        _updatingBindings = true;
        try
        {
            string? selectedKey = (BindingPicker.SelectedItem as BindingOption)?.Binding.PersistenceKey;
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
            _lastRestore = _bindings.LastStartupReport();
            BindingPicker.ItemsSource = options;
            BindingPicker.IsEnabled = options.Length > 0;
            BindingPicker.SelectedItem = options.FirstOrDefault(o => o.Binding.PersistenceKey == selectedKey) ?? options.FirstOrDefault();
            UpdateBindingState();
        }
        catch (Exception ex) { BindingToggle.IsEnabled = false; RemoveBindingButton.IsEnabled = false; ShowError(ex); }
        finally { _updatingBindings = false; }
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
            "failed" or "recovery-pending" => "BindingLastFailed",
            "skipped" => "BindingLastSkipped",
            _ => "BindingNoResult"
        };
        BindingStateText.Text = last is null
            ? Model.Text[key]
            : string.Format(Model.Text[key], _lastRestore!.Time.ToLocalTime().ToString("g"));
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
