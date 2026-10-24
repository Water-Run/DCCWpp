using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;
using DccwPlus.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace DccwPlus.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel Model { get; } = new();
    private bool _updating = true;
    private readonly StartupService _startup = new();

    public MainWindow()
    {
        InitializeComponent();
        Root.DataContext = Model;
        Title = "DCCW++";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new(1180, 880));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        Navigation.SelectedItem = CalibrationNav;
        if (Model.Displays.Count > 0) DisplayPicker.SelectedIndex = 0;
        LanguagePicker.SelectedItem = LanguagePicker.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == Model.Text.Language);
        UpdateLanguage();
        _updating = false;
        Root.Loaded += async (_, _) => await RefreshStartupAsync();
    }

    private void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (CalibrationPanel is null) return;
        string page = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "calibration";
        CalibrationPanel.Visibility = page == "calibration" ? Visibility.Visible : Visibility.Collapsed;
        PresetsPanel.Visibility = page == "presets" ? Visibility.Visible : Visibility.Collapsed;
        ReferencesPanel.Visibility = page == "references" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        Model.Status = "";
    }

    private void RefreshClicked(object sender, RoutedEventArgs e)
    {
        string? selectedId = (DisplayPicker.SelectedItem as Core.Calibration.DisplayInfo)?.Id;
        try
        {
            Model.Refresh();
            DisplayPicker.SelectedItem = Model.Displays.FirstOrDefault(d => d.Id == selectedId) ?? Model.Displays.FirstOrDefault();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ResetClicked(object sender, RoutedEventArgs e)
    { GammaSlider.Value = 2.2; RedSlider.Value = GreenSlider.Value = BlueSlider.Value = 1; }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var preset = CalibrationPreset.Create(PresetName.Text, new()
            { Gamma = GammaSlider.Value, RedGain = RedSlider.Value, GreenGain = GreenSlider.Value, BlueGain = BlueSlider.Value });
            Model.PresetStore.Add(preset);
            Model.RefreshPresets();
            Model.Status = Model.Text["Saved"] + " " + preset.Name;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var imported = Model.PresetStore.Import(file.Path);
            Model.RefreshPresets();
            PresetList.SelectedItem = Model.Presets.First(p => p.Id == imported.Id);
            Model.Status = Model.Text["Imported"] + " " + imported.Name;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not CalibrationPreset preset) { Model.Status = Model.Text["SelectPreset"]; return; }
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "preset.dccw" };
            picker.FileTypeChoices.Add("DCCW++ preset", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            // The system save picker already confirms overwriting the selected destination.
            Model.PresetStore.Export(preset.Id, file.Path, overwrite: true);
            Model.Status = Model.Text["Exported"] + " " + file.Path;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void LoadClicked(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not CalibrationPreset preset) { Model.Status = Model.Text["SelectPreset"]; return; }
        PresetName.Text = preset.Name;
        GammaSlider.Value = preset.Calibration.Gamma;
        RedSlider.Value = preset.Calibration.RedGain; GreenSlider.Value = preset.Calibration.GreenGain; BlueSlider.Value = preset.Calibration.BlueGain;
        Navigation.SelectedItem = CalibrationNav;
        Model.Status = Model.Text["DraftLoaded"];
    }

    private async void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || LanguagePicker.SelectedItem is not ComboBoxItem item) return;
        try
        {
            Model.Text.Language = (string)item.Tag;
            Model.SettingsStore.Save(new AppSettings { Language = Model.Text.Language });
            UpdateLanguage();
            await RefreshStartupAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void UpdateLanguage()
    {
        Root.Language = Model.Text.EffectiveLanguage;
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Model.Text.EffectiveLanguage;
        if (Navigation.SettingsItem is NavigationViewItem settings) settings.Content = Model.Text["Settings"];
    }

    private async Task RefreshStartupAsync()
    {
        try
        {
            var state = await _startup.GetAsync();
            _updating = true;
            StartupToggle.IsOn = state.IsEnabled;
            StartupToggle.IsEnabled = (state.CanEnable || state.IsEnabled) && !state.State.Contains("Policy", StringComparison.Ordinal);
            StartupStateText.Text = Model.Text["StartupState_" + state.State];
        }
        catch (Exception ex) { ShowError(ex); StartupToggle.IsEnabled = false; }
        finally { _updating = false; }
    }

    private async void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        try { await _startup.SetEnabledAsync(StartupToggle.IsOn); }
        catch (Exception ex) { ShowError(ex); }
        await RefreshStartupAsync();
    }
    private void ShowError(Exception ex) => Model.Status = Model.Text["Error"] + " " + ex.Message;
}
