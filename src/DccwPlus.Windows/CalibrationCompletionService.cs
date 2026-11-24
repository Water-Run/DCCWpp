using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Localization;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;

namespace DccwPlus.Windows;

internal sealed class CalibrationCompletionService(string root, IDisplayService displays,
    Func<GammaCalibrationSession, IccAssociationBackup> readAssociation, Func<string, string> readInstalledHash, Action requireLocal)
{
    private readonly GammaCalibrationStore _sessions = new(root);
    private readonly CalibrationBindingStore _bindings = new(root);
    private readonly PresetStore _presets = new(root);

    public CalibrationCompletionService(string root) : this(root, new WindowsGammaCalibrationBackend(root)) { }

    private CalibrationCompletionService(string root, WindowsGammaCalibrationBackend backend)
        : this(root, new WindowsDisplayService(), backend.ReadAssociationBackup, InstalledProfileReader.ReadSha256, WindowsDisplaySession.RequireLocal) { }

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
            result = (ex.Code is "display_disconnected" or "display_state_changed" or "display_identity_ambiguous"
                or "profile_device_unavailable" or "profile_state_unavailable" or "profile_file_unavailable"
                or "sdr_path_blocked" or "shared_source_blocked" or "remote_session_blocked" ? "pending" : "failed", ex.Code);
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
        var display = ReadTarget(session, replay);
        if (display.ProfileScope != "current-user" || display.SelectedProfileScope != "current-user"
            || !ProfilePreviewPolicy.EqualName(display.ProfileName, profile))
            throw new ProfilePreviewException("profile_selection_changed", new LocalizationService()["ProfileChangedBeforeSave"]);

        var previous = FindBinding(display.PersistenceKey!);
        bool alreadySaved = ProfilePreviewPolicy.EqualName(previous?.ProfileName, profile);
        if (replay && !alreadySaved)
        {
            if (session.BindingIntent is null) return ("unchanged", "binding_intent_unavailable");
            if (!ProfilePreviewPolicy.EqualName(session.BindingIntent.PreviousProfileName, previous?.ProfileName))
                return ("unchanged", "binding_changed");
        }

        var backup = readAssociation(session);
        VerifyProfileDevice(display, backup);
        string hash = WindowsGammaCalibrationBackend.VerifyProfileFingerprint(profile, backup.CandidateHash, readInstalledHash);
        RequireStableTarget(session, replay, display);
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
        if (!MonitorIdentity.SamePersistentIdentity(source.Original.PersistenceKey, undo.Original.PersistenceKey)
            || !ProfilePreviewPolicy.EqualName(undo.Original.ProfileName, source.InstalledProfile))
            throw new InvalidDataException("The undo record does not match the saved calibration.");
        if (replay)
        {
            var display = ReadTarget(undo, replay: true);
            var previous = source.Original;
            if (!ProfilePreviewPolicy.EqualName(display.ProfileName, previous.ProfileName) || display.ProfileScope != previous.ProfileScope
                || (previous.SelectedProfileScope is not null && display.SelectedProfileScope != previous.SelectedProfileScope)
                || !ProfilePreviewPolicy.EqualName(display.ProfileName, undo.InstalledProfile))
                return ("unchanged", "profile_selection_changed");
            var backup = readAssociation(source);
            VerifyProfileDevice(display, backup);
            if (!ProfilePreviewPolicy.EqualName(backup.EffectiveDefault, previous.ProfileName))
                throw new InvalidDataException("The restored profile does not match the saved association.");
            if (previous.ProfileName is { } profile)
                WindowsGammaCalibrationBackend.VerifyProfileFingerprint(profile, backup.EffectiveDefaultHash, readInstalledHash);
            RequireStableTarget(undo, replay: true, display);
        }
        _bindings.Remove(binding.PersistenceKey);
        return ("removed", null);
    }

    private DisplayInfo ReadTarget(GammaCalibrationSession session, bool replay)
    {
        var text = new LocalizationService();
        var original = session.Original;
        if (!MonitorIdentity.IsPersistenceKey(original.PersistenceKey))
            throw new ProfilePreviewException("display_identity_unavailable", text["DisplayUnavailable"]);
        var connected = displays.Enumerate();
        var matches = connected.Where(display => MonitorIdentity.MatchesSavedDisplay(original, display)).ToArray();
        if (matches.Length == 0) throw new ProfilePreviewException("display_disconnected", text["DisplayUnavailable"]);
        if (matches.Length != 1) throw new ProfilePreviewException("display_identity_ambiguous", text["DisplayUnavailable"]);
        requireLocal();
        var display = matches[0];
        if (display.PersistenceKind is null)
            throw new ProfilePreviewException("display_identity_unavailable", text["DisplayUnavailable"]);
        if (WindowsGammaCalibrationBackend.SdrBlockReason(display, connected) is { } blocked)
            throw new ProfilePreviewException(blocked, text[blocked switch
            {
                "sdr_path_blocked" => "SdrRequired",
                "mhc2_profile_blocked" => "Mhc2ProfileBlocked",
                "shared_source_blocked" => "MirroredDisplay",
                "display_identity_unavailable" => "DisplayUnavailable",
                _ => "ProfileStateUnavailable"
            }]);
        if (display.ProfileQueryStatus is not ("read" or "not-found") || display.SelectedProfileScope is not ("current-user" or "system-wide"))
            throw new ProfilePreviewException("profile_state_unavailable", text["ProfileStateUnavailable"]);
        if (!replay && (display.Id != original.DisplayId || display.DeviceName != original.DeviceName || display.TargetKey != original.TargetKey
            || (original.SourceId is not null && display.SourceId != original.SourceId)
            || WindowsGammaCalibrationBackend.ReadTopologyFingerprint(connected) != original.TopologyFingerprint
            || (original.ActiveTopologySha256 is { } activeTopology && WindowsDisplayService.ComputeTopologyHash(display.Topology, connected) != activeTopology)))
            throw new ProfilePreviewException("display_state_changed", text["DisplayUnavailable"]);
        return display;
    }

    private void RequireStableTarget(GammaCalibrationSession session, bool replay, DisplayInfo display)
    {
        if (display != ReadTarget(session, replay))
            throw new ProfilePreviewException("display_state_changed", new LocalizationService()["DisplayUnavailable"]);
    }

    private static void VerifyProfileDevice(DisplayInfo display, IccAssociationBackup backup)
    {
        if (!string.Equals(display.ProfileDeviceKey, backup.DeviceKey, StringComparison.OrdinalIgnoreCase))
            throw new ProfilePreviewException("profile_device_unavailable", new LocalizationService()["ProfileDeviceUnavailable"]);
    }

    private CalibrationBinding? FindBinding(string? key) => key is null ? null : _bindings.List().SingleOrDefault(binding => binding.PersistenceKey == key);

    private void EnsurePreset(CalibrationPreset preset)
    {
        try { _ = _presets.Get(preset.Id); }
        catch (FileNotFoundException) { _presets.Add(preset); }
        catch (DirectoryNotFoundException) { _presets.Add(preset); }
    }
}
