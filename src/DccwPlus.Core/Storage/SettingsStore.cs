using System.Text.Json;

namespace DccwPlus.Core.Storage;

public sealed record AppSettings
{
    public string Language { get; init; } = "system";
}

public sealed class SettingsStore(string root)
{
    private readonly string _path = Path.Combine(Path.GetFullPath(root), "settings.json");
    public AppSettings Load() => File.Exists(_path)
        ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new()
        : new();
    public void Save(AppSettings settings) => AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(settings));
}

public static class DataPaths
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DCCW++");
}
