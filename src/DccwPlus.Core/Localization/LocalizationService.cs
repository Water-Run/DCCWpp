using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace DccwPlus.Core.Localization;

public sealed class LocalizationService : INotifyPropertyChanged
{
    public static readonly string[] Languages = ["en-US", "zh-Hans", "zh-Hant"];
    private readonly Dictionary<string, Dictionary<string, string>> _catalogs = LoadCatalogs();
    private string _language = "system";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;
    public string Language
    {
        get => _language;
        set
        {
            if (value != "system" && !Languages.Contains(value)) throw new ArgumentException("Unsupported language.");
            if (_language == value) return;
            _language = value;
            PropertyChanged?.Invoke(this, new(nameof(Language)));
            PropertyChanged?.Invoke(this, new("Item[]"));
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public string EffectiveLanguage => Resolve(_language, CultureInfo.CurrentUICulture);
    public string this[string key] => _catalogs[EffectiveLanguage].GetValueOrDefault(key)
        ?? _catalogs["en-US"].GetValueOrDefault(key) ?? key;

    public static string Resolve(string language, CultureInfo culture)
    {
        if (language != "system") return Languages.Contains(language) ? language : "en-US";
        if (culture.TwoLetterISOLanguageName != "zh") return "en-US";
        string tag = culture.Name;
        return tag.Contains("Hant", StringComparison.OrdinalIgnoreCase) || tag.EndsWith("-TW", StringComparison.OrdinalIgnoreCase)
            || tag.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) || tag.EndsWith("-MO", StringComparison.OrdinalIgnoreCase)
            ? "zh-Hant" : "zh-Hans";
    }
    public IReadOnlyDictionary<string, string> GetCatalog(string language) => _catalogs[language];
    private static Dictionary<string, Dictionary<string, string>> LoadCatalogs()
    {
        var assembly = typeof(LocalizationService).Assembly;
        return Languages.ToDictionary(tag => tag, tag =>
        {
            using var stream = assembly.GetManifestResourceStream($"DccwPlus.Core.Localization.Catalogs.{tag}.json")
                ?? throw new InvalidOperationException($"Missing language catalog: {tag}");
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        });
    }
}
