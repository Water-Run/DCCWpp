using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;

namespace DccwPlus.Core.Storage;

public sealed class GammaCalibrationStore(string root) : IGammaCalibrationJournal
{
    private readonly string _root = Path.Combine(Path.GetFullPath(root), "calibration-sessions");
    public string DirectoryFor(Guid id) => Path.Combine(_root, id.ToString("N"));
    public void Save(GammaCalibrationSession session)
    {
        Validate(session);
        AtomicFile.Write(Path.Combine(DirectoryFor(session.Id), "state.json"),
            JsonSerializer.SerializeToUtf8Bytes(session, ProfilePreviewStore.Json));
    }
    public GammaCalibrationSession Load(Guid id)
    {
        var result = ProfilePreviewStore.Read<GammaCalibrationSession>(Path.Combine(DirectoryFor(id), "state.json"));
        Validate(result);
        if (result.Id != id) throw new InvalidDataException("Calibration journal identity is invalid.");
        return result;
    }
    public GammaCalibrationSession? Find(Guid id)
    {
        try { return Load(id); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    public IReadOnlyList<GammaCalibrationSession> Sessions()
    {
        if (!Directory.Exists(_root)) return [];
        return Directory.EnumerateDirectories(_root).Select(Path.GetFileName).Where(n => Guid.TryParseExact(n, "N", out _))
            .Select(n => Guid.ParseExact(n!, "N")).Where(id => File.Exists(Path.Combine(DirectoryFor(id), "state.json")))
            .Select(Load).OrderByDescending(s => s.StartedAt).ToArray();
    }
    private static void Validate(GammaCalibrationSession session)
    {
        if (session.SchemaVersion != 1 || session.Id == Guid.Empty || session.Original is null || session.Preset is null
            || session.ExpiresAt.Year is < 2020 or > 2100 || session.StartedAt.Year is < 2020 or > 2100
            || (session.ProfileRollbackMayResetCurve && (!session.ProfileCommitStarted || session.IsBindingRestore)))
            throw new InvalidDataException("Invalid calibration session.");
        if (session.Status is not ("prepared" or "applying" or "previewing" or "committing" or "recovering"
            or "confirmed" or "reverted" or "superseded" or "recovery-pending")) throw new InvalidDataException("Invalid calibration status.");
        if (session.Original.DisplayId is not { Length: 16 } || !session.Original.DisplayId.All(Uri.IsHexDigit)
            || !ProfilePreviewPolicy.IsHash(session.Original.TopologyFingerprint)
            || (session.Original.ActiveTopologySha256 is { } activeTopology && !ProfilePreviewPolicy.IsHash(activeTopology))
            || (session.Original.ProfileDeviceSha256 is { } profileDevice && !ProfilePreviewPolicy.IsHash(profileDevice))
            || string.IsNullOrWhiteSpace(session.Original.TargetKey) || session.Original.TargetKey.Length > 80)
            throw new InvalidDataException("Invalid calibration display identity.");
        PresetCodec.Validate(session.Preset);
        if (session.Original.SelectedProfileScope is not (null or "current-user" or "system-wide"))
            throw new InvalidDataException("Invalid selected color profile scope.");
        session.Original.Ramp.Validate(); session.Candidate.Validate(); session.LastWritten.Validate();
        session.BeforeWrite?.Validate();
        if (session.UndoOf == Guid.Empty || session.UndoOf == session.Id || (session.UndoOf is not null && session.IsBindingRestore))
            throw new InvalidDataException("Invalid calibration undo session.");
        if (!session.IsBindingRestore && session.UndoOf is null
            && !session.Candidate.Matches(GammaRampMath.Compose(session.Original.Ramp, session.Preset.Calibration)))
            throw new InvalidDataException("The saved candidate does not match its calibration intent.");
        if (session.InstalledProfile is not null) IccProfileInspector.ValidateFileName(session.InstalledProfile);
        if (session.BindingIntent?.PreviousProfileName is { } previousProfile) IccProfileInspector.ValidateFileName(previousProfile);
    }
}
