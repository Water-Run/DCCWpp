using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Localization;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;

namespace DccwPlus.Windows;

internal sealed class CalibrationCompletionService(string root, IDisplayService displays, Func<string, GammaDeviceState> readDisplay,
    Func<GammaCalibrationSession, string> readConfirmedHash)
{
    private readonly GammaCalibrationStore _sessions = new(root);
    private readonly CalibrationBindingStore _bindings = new(root);
    private readonly PresetStore _presets = new(root);

    public CalibrationCompletionService(string root) : this(root, new WindowsGammaCalibrationBackend(root)) { }

    private CalibrationCompletionService(string root, WindowsGammaCalibrationBackend backend)
        : this(root, new WindowsDisplayService(), backend.Read, backend.ReadConfirmedProfileHash) { }

    // Called by the worker while it holds the display mutation lock, before committing the ICC.
    public BindingSaveIntent? CaptureIntent(GammaCalibrationSession session)
    {
        if (session.UndoOf is not null || session.Original.PersistenceKey is not { } key) return null;
        try { return new(FindBinding(key)?.ProfileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { return null; }
    }

    public GammaCalibrationSession Recover(Guid id)
    {
        using var gate = DisplayMutationLock.Acquire();
        var session = _sessions.Load(id);
        if (session.NeedsBindingCompletion
            && (_sessions.Sessions().Any(other => other.NeedsRecovery) || new ProfilePreviewStore(root).Incomplete().Count > 0))
            throw new ProfilePreviewException("recovery_required", new LocalizationService()["RecoveryMessage"]);
        return CompleteLocked(session, replay: true);
    }

    // Finalizes local metadata only. The caller holds the display mutation lock.
    public GammaCalibrationSession CompleteLocked(GammaCalibrationSession session, bool replay)
    {
        if (!session.NeedsBindingCompletion) return session;
        (string Status, string? Reason) result;
        try
        {
            if (!session.ProfileCommitStarted || !session.CurveReadbackVerified)
                throw new InvalidDataException("The calibration journal does not record a verified commit.");
            result = session.UndoOf is Guid undoOf ? CompleteUndo(session, _sessions.Load(undoOf), replay) : CompleteSave(session, replay);
        }
        catch (ProfilePreviewException ex)
        {
            result = (ex.Code is "display_disconnected" or "profile_state_unavailable" or "profile_file_unavailable"
                or "remote_session_blocked" ? "pending" : "failed", ex.Code);
        }
        catch (PresetOperationException ex)
        {
            result = (ex.Code == "preset_library_busy" ? "pending" : "failed", ex.Code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            result = ("failed", "binding_save_failed");
        }

        var completed = session with { BindingStatus = result.Status, BindingReasonCode = result.Reason };
        _sessions.Save(completed);
        return completed;
    }

    public void RetirePending(Func<GammaCalibrationSession, bool> matches, string reason)
    {
        foreach (var session in _sessions.Sessions().Where(session => session.NeedsBindingCompletion && matches(session)))
            _sessions.Save(session with { BindingStatus = "unchanged", BindingReasonCode = reason });
    }

    private (string Status, string? Reason) CompleteSave(GammaCalibrationSession session, bool replay)
    {
        if (!replay) EnsurePreset(session.Preset);
        string profile = session.InstalledProfile ?? throw new InvalidDataException("A confirmed calibration profile is required.");
        var (display, current) = ReadTarget(session, replay);
        if (current.ProfileScope != "current-user" || !ProfilePreviewPolicy.EqualName(current.ProfileName, profile))
            throw new ProfilePreviewException("profile_selection_changed", "The confirmed profile is no longer selected.");

        var previous = FindBinding(display.PersistenceKey!);
        bool alreadySaved = ProfilePreviewPolicy.EqualName(previous?.ProfileName, profile);
        if (replay && !alreadySaved)
        {
            if (session.BindingIntent is null) return ("unchanged", "binding_intent_unavailable");
            if (!ProfilePreviewPolicy.EqualName(session.BindingIntent.PreviousProfileName, previous?.ProfileName))
                return ("unchanged", "binding_changed");
        }

        string hash = readConfirmedHash(session);
        if (alreadySaved && (previous!.PresetId != session.Preset.Id || !previous.Ramp.Matches(session.Candidate)
            || !string.Equals(previous.ProfileSha256, hash, StringComparison.OrdinalIgnoreCase)))
            return ("unchanged", "binding_changed");
        if (replay) EnsurePreset(session.Preset);
        if (alreadySaved) return ("saved", null);

        _bindings.Save(new()
        {
            PersistenceKey = display.PersistenceKey!, IdentityKind = display.PersistenceKind!, DisplayName = display.Name,
            PresetId = session.Preset.Id, ProfileName = profile, ProfileSha256 = hash,
            Ramp = session.Candidate.Copy(), OriginalRamp = session.Original.Ramp.Copy(),
            RestoreEnabled = previous?.RestoreEnabled ?? false, SavedAt = DateTimeOffset.UtcNow
        });
        return ("saved", null);
    }

    private (string Status, string? Reason) CompleteUndo(GammaCalibrationSession undo, GammaCalibrationSession source, bool replay)
    {
        CalibrationUndoPolicy.VerifySource(source);
        var binding = FindBinding(source.Original.PersistenceKey);
        if (binding is null || !ProfilePreviewPolicy.EqualName(binding.ProfileName, source.InstalledProfile))
            return ("unchanged", null);
        if (replay)
        {
            var (_, current) = ReadTarget(undo, replay: true);
            if (!GammaCalibrationTransaction.SameProfile(source.Original, current)
                || !ProfilePreviewPolicy.EqualName(current.ProfileName, undo.InstalledProfile))
                return ("unchanged", "profile_selection_changed");
        }
        _bindings.Remove(binding.PersistenceKey);
        return ("removed", null);
    }

    private (DisplayInfo Display, GammaDeviceState Current) ReadTarget(GammaCalibrationSession session, bool replay)
    {
        var connected = displays.Enumerate();
        var display = connected.SingleOrDefault(display => display.Id == session.Original.DisplayId)
            ?? throw new ProfilePreviewException("display_disconnected", "The display is not connected.");
        if (display.PersistenceKey is null || display.PersistenceKind is null || display.PersistenceKey != session.Original.PersistenceKey
            || connected.Count(other => other.PersistenceKey == display.PersistenceKey) != 1)
            throw new ProfilePreviewException("display_identity_unavailable", "A unique persistent display identity is required.");
        var current = readDisplay(display.Id);
        bool same = replay ? GammaCalibrationTransaction.SameRecoveryTarget(session.Original, current)
            : GammaCalibrationTransaction.SameTarget(session.Original, current);
        if (!same) throw new ProfilePreviewException("display_state_changed", "The display changed before the binding could be saved.");
        return (display, current);
    }

    private CalibrationBinding? FindBinding(string? key) => key is null ? null : _bindings.List().SingleOrDefault(binding => binding.PersistenceKey == key);

    private void EnsurePreset(CalibrationPreset preset)
    {
        try { _ = _presets.Get(preset.Id); }
        catch (FileNotFoundException) { _presets.Add(preset); }
        catch (DirectoryNotFoundException) { _presets.Add(preset); }
    }
}
