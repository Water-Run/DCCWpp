using System.Collections.ObjectModel;
using System.ComponentModel;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Localization;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;
using DccwPlus.Windows;

namespace DccwPlus.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    public LocalizationService Text { get; } = new();
    public PresetStore PresetStore { get; } = new(DataPaths.DefaultRoot);
    public SettingsStore SettingsStore { get; } = new(DataPaths.DefaultRoot);
    public ObservableCollection<DisplayInfo> Displays { get; } = [];
    public ObservableCollection<CalibrationPreset> Presets { get; } = [];
    public string DataDirectory => DataPaths.DefaultRoot;
    private string _status = "";
    public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public MainViewModel()
    {
        try
        {
            var settings = SettingsStore.Load();
            if (settings.Language == "system" || LocalizationService.Languages.Contains(settings.Language)) Text.Language = settings.Language;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Status = ex.Message; }
        Refresh();
    }
    public void Refresh()
    {
        Displays.Clear();
        foreach (var display in new WindowsDisplayService().Enumerate()) Displays.Add(display);
        RefreshPresets();
    }
    public void RefreshPresets()
    {
        Presets.Clear();
        var library = PresetStore.List();
        foreach (var preset in library.Presets) Presets.Add(preset);
        if (library.InvalidFiles.Count > 0) Status = Text["InvalidPresets"] + " " + string.Join(", ", library.InvalidFiles);
    }
}
