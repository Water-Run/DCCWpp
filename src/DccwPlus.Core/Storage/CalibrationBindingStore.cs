using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DccwPlus.Core.Calibration;

namespace DccwPlus.Core.Storage;

public sealed record CalibrationBinding
{
    public int SchemaVersion { get; init; } = 1;
    public required string PersistenceKey { get; init; }
    public required string IdentityKind { get; init; }
    public required string DisplayName { get; init; }
    public required Guid PresetId { get; init; }
    public required string ProfileName { get; init; }
    public required string ProfileSha256 { get; init; }
    public required GammaRamp Ramp { get; init; }
    public GammaRamp? OriginalRamp { get; init; }
    public bool RestoreEnabled { get; init; }
    public required DateTimeOffset SavedAt { get; init; }
}

public sealed class CalibrationBindingStore(string root)
{
    private readonly string _directory = Path.Combine(Path.GetFullPath(root), "display-bindings");
    private string FileFor(string key) => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
    public void Save(CalibrationBinding binding)
    {
        Validate(binding);
        AtomicFile.Write(FileFor(binding.PersistenceKey), JsonSerializer.SerializeToUtf8Bytes(binding, ProfilePreviewStore.Json));
    }
    public CalibrationBinding Remove(string persistenceKey, Action? beforeRemove = null)
    {
        var binding = List().SingleOrDefault(b => b.PersistenceKey == persistenceKey)
            ?? throw new KeyNotFoundException("No saved calibration is bound to that display.");
        beforeRemove?.Invoke();
        File.Delete(FileFor(binding.PersistenceKey));
        return binding;
    }
    public IReadOnlyList<CalibrationBinding> List()
    {
        if (!Directory.Exists(_directory)) return [];
        return Directory.EnumerateFiles(_directory, "*.json").Select(path =>
        {
            var binding = ProfilePreviewStore.Read<CalibrationBinding>(path); Validate(binding);
            if (!string.Equals(Path.GetFullPath(path), FileFor(binding.PersistenceKey), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Binding file name does not match the display identity.");
            return binding;
        }).ToArray();
    }
    public static void Validate(CalibrationBinding binding)
    {
        if (binding.SchemaVersion != 1 || !MonitorIdentity.IsPersistenceKey(binding.PersistenceKey)
            || binding.IdentityKind != (binding.PersistenceKey.StartsWith("edid:", StringComparison.Ordinal) ? "edid-serial" : "device-path")
            || binding.PresetId == Guid.Empty || string.IsNullOrWhiteSpace(binding.DisplayName) || binding.DisplayName.Length > 128
            || !ProfilePreviewPolicy.IsHash(binding.ProfileSha256) || binding.SavedAt.Year is < 2020 or > 2100)
            throw new InvalidDataException("Invalid display calibration binding.");
        IccProfileInspector.ValidateFileName(binding.ProfileName);
        if (!binding.ProfileName.StartsWith("DCCWPlus-", StringComparison.Ordinal)) throw new InvalidDataException("Only DCCW++ calibration profiles can be restored automatically.");
        binding.Ramp.Validate();
        binding.OriginalRamp?.Validate();
    }
}
