using System.Security.Cryptography;
using System.Text;
using DccwPlus.Core.Presets;

namespace DccwPlus.Core.Storage;

public sealed record PresetLibrary(IReadOnlyList<CalibrationPreset> Presets, IReadOnlyList<string> InvalidFiles);
public sealed record PresetImportResult(CalibrationPreset Preset, bool AlreadyPresent);

public sealed class PresetOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

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
    public void Add(CalibrationPreset preset) => Mutate(() =>
    {
        AtomicFile.Write(FileFor(preset.Id), PresetCodec.Encode(preset), overwrite: false);
        return preset;
    });

    public PresetImportResult Import(string path, bool asCopy = false)
    {
        var incoming = PresetCodec.Decode(PresetCodec.ReadBounded(path));
        return Mutate<PresetImportResult>(() =>
        {
            Guid id = asCopy ? Guid.NewGuid() : ImportId(incoming);
            if (!asCopy)
            {
                if (File.Exists(FileFor(id)))
                {
                    var existing = Get(id);
                    if (existing.Calibration != incoming.Calibration)
                        throw new PresetOperationException("preset_import_conflict", "The existing imported preset has a different calibration.");
                    return new(existing, true);
                }
                // Reuse an exact copy imported by an older build. Its local ID may already be bound.
                var legacy = List().Presets.FirstOrDefault(p => p.Id != incoming.Id && p == (incoming with { Id = p.Id }));
                if (legacy is not null) return new(legacy, true);
            }
            var local = incoming with { Id = id };
            AtomicFile.Write(FileFor(id), PresetCodec.Encode(local), overwrite: false);
            return new(local, false);
        });
    }

    public CalibrationPreset Rename(Guid id, string name) => Mutate(() =>
    {
        var current = Get(id);
        var renamed = current with { Name = name.Trim() };
        AtomicFile.Write(FileFor(id), PresetCodec.Encode(renamed));
        return renamed;
    });

    public IReadOnlyList<CalibrationBinding> BindingsFor(Guid id) =>
        new CalibrationBindingStore(root).List().Where(b => b.PresetId == id).ToArray();

    // The Windows caller also holds the display mutation lock while checking and removing references.
    public void Delete(Guid id) => Mutate(() =>
    {
        var preset = Get(id);
        if (BindingsFor(id).Count > 0)
            throw new PresetOperationException("preset_in_use", "Remove the display bindings before deleting this preset.");
        File.Delete(FileFor(id));
        return preset;
    });

    public void Export(Guid id, string path, bool overwrite = false)
    {
        var target = Path.GetFullPath(path);
        if (string.Equals(Path.GetDirectoryName(target), _directory, PathComparison))
            throw new PresetOperationException("preset_library_path", "Choose a destination outside the managed preset library.");
        AtomicFile.Write(target, PresetCodec.Encode(Get(id)), overwrite);
    }

    private T Mutate<T>(Func<T> action)
    {
        string path = OperatingSystem.IsWindows() ? _directory.ToUpperInvariant() : _directory;
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        using var mutex = new Mutex(false, "DCCWPlus.Presets." + key);
        bool owns;
        try { owns = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
        catch (AbandonedMutexException) { owns = true; }
        if (!owns) throw new PresetOperationException("preset_library_busy", "The preset library is busy.");
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }

    private static Guid ImportId(CalibrationPreset incoming)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("DCCW++ preset import v1"u8);
        hash.AppendData(PresetCodec.Encode(incoming));
        byte[] digest = hash.GetHashAndReset();
        digest[6] = (byte)((digest[6] & 0x0F) | 0x80); // Custom UUIDv8.
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
