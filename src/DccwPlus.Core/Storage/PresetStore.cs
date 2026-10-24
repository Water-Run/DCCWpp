using DccwPlus.Core.Presets;

namespace DccwPlus.Core.Storage;

public sealed record PresetLibrary(IReadOnlyList<CalibrationPreset> Presets, IReadOnlyList<string> InvalidFiles);

public sealed class PresetStore(string root)
{
    private readonly string _directory = Path.Combine(Path.GetFullPath(root), "presets");
    private string FileFor(Guid id) => Path.Combine(_directory, $"{id:D}.dccw.json");

    public PresetLibrary List()
    {
        if (!Directory.Exists(_directory)) return new([], []);
        List<CalibrationPreset> presets = [];
        List<string> invalid = [];
        foreach (string path in Directory.EnumerateFiles(_directory, "*.dccw.json"))
        {
            try
            {
                var preset = PresetCodec.Decode(PresetCodec.ReadBounded(path));
                if (!string.Equals(Path.GetFileName(path), $"{preset.Id:D}.dccw.json", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Preset ID does not match its file name.");
                presets.Add(preset);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            { invalid.Add(Path.GetFileName(path)); }
        }
        return new(presets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray(), invalid);
    }

    public CalibrationPreset Get(Guid id)
    {
        var preset = PresetCodec.Decode(PresetCodec.ReadBounded(FileFor(id)));
        if (preset.Id != id) throw new InvalidDataException("Preset ID does not match its file name.");
        return preset;
    }
    public void Add(CalibrationPreset preset) => AtomicFile.Write(FileFor(preset.Id), PresetCodec.Encode(preset), overwrite: false);

    public CalibrationPreset Import(string path)
    {
        var incoming = PresetCodec.Decode(PresetCodec.ReadBounded(path));
        var local = incoming with { Id = Guid.NewGuid() };
        Add(local);
        return local;
    }

    public void Export(Guid id, string path, bool overwrite = false) => AtomicFile.Write(path, PresetCodec.Encode(Get(id)), overwrite);
}
