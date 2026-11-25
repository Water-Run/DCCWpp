using DccwPlus.Core.Localization;
using DccwPlus.Core.Presets;
using System.Text.Json.Serialization;

namespace DccwPlus.Core.Calibration;

public sealed record GammaDeviceState(string DisplayId, string DeviceName, string TargetKey,
    string TopologyFingerprint, bool ExplicitSdr, bool SharedSource, GammaRamp Ramp,
    string? ProfileName = null, string? ProfileScope = null, uint? SourceId = null, string? PersistenceKey = null,
    bool? ProfileMhc2Present = null, string? SelectedProfileScope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActiveTopologySha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProfileDeviceSha256 = null);

public sealed record GammaProfileSelection(string? ProfileName, string? ProfileScope, string? SelectedProfileScope)
{
    public bool Matches(GammaDeviceState state) => ProfilePreviewPolicy.EqualName(ProfileName, state.ProfileName)
        && ProfileScope == state.ProfileScope && SelectedProfileScope == state.SelectedProfileScope;
}

public sealed record BindingSaveIntent(string? PreviousProfileName);
public sealed record GammaCalibrationSession
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid Id { get; init; }
    public required GammaDeviceState Original { get; init; }
    public required CalibrationPreset Preset { get; init; }
    public required GammaRamp Candidate { get; init; }
    public required GammaRamp LastWritten { get; init; }
    public GammaRamp? BeforeWrite { get; init; }
    public bool ShowingOriginal { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsReviewing { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public string Status { get; init; } = "prepared";
    public string ReasonCode { get; init; } = "prepared";
    public string? InstalledProfile { get; init; }
    public string? BindingStatus { get; init; }
    public string? BindingReasonCode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BindingSaveIntent? BindingIntent { get; init; }
    public bool CurveReadbackVerified { get; init; }
    public bool IsBindingRestore { get; init; }
    public Guid? UndoOf { get; init; }
    public bool ProfileCommitStarted { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ProfileRollbackMayResetCurve { get; init; }
    public bool ColorAccuracyVerified => false;
    public bool NeedsRecovery => Status is "prepared" or "applying" or "previewing" or "committing" or "recovering" or "recovery-pending";
    [JsonIgnore]
    public bool NeedsBindingCompletion => Status == "confirmed" && !IsBindingRestore && BindingStatus == "pending";
}
public interface IGammaCalibrationBackend
{
    GammaDeviceState Read(string displayId);
    // Interrupted recovery may resolve a unique current path for the original display and profile device.
    GammaDeviceState ReadRecoveryTarget(GammaDeviceState original) => Read(original.DisplayId);
    // Evaluate canWrite after validation, immediately before the native write. False means no write was attempted.
    bool TryWrite(GammaDeviceState expected, GammaRamp ramp, Func<bool>? canWrite = null);
    // Return the intended ICC selection for the transaction's final readback.
    GammaProfileSelection Commit(GammaCalibrationSession session);
    // Returns true for a verified reversal of the session's ICC selection from the expected pre-restore state.
    // Call beforeSelectionRestore after validating that state and before changing the effective selection.
    // recovery_target_changed and previous_system_profile_changed reject the operation before an ICC write.
    bool RestoreProfile(GammaCalibrationSession session, GammaDeviceState expected, Action? beforeSelectionRestore = null);
}
public interface IGammaCalibrationJournal { void Save(GammaCalibrationSession session); }

public sealed class GammaCalibrationTransaction(IGammaCalibrationBackend backend, IGammaCalibrationJournal journal,
    TimeProvider? timeProvider = null, Func<bool>? isOwnerAlive = null, Func<string?>? getCancellationReason = null)
{
    public const int ReadbackTolerance = 257;
    public static TimeSpan WindowPreviewLimit { get; } = TimeSpan.FromMinutes(30);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _deadlineStart;
    private TimeSpan _deadlineDuration;
    private long _sessionStart;
    private bool _allowsWindowEdits;
    public GammaCalibrationSession? Session { get; private set; }
    public GammaCalibrationSession Begin(Guid id, CalibrationPreset preset, string displayId, int seconds,
        GammaRamp? savedCurve = null, GammaDeviceState? expected = null, Guid? undoOf = null, bool windowOwned = false)
    {
        if (Session is not null || id == Guid.Empty || seconds is < 10 or > 60) throw new ArgumentException("Invalid calibration session or timeout.");
        if (undoOf is not null && (undoOf == Guid.Empty || undoOf == id || savedCurve is null || expected is null))
            throw new ArgumentException("An undo requires a saved curve and a checked display state.");
        PresetCodec.Validate(preset);
        var original = backend.Read(displayId); original.Ramp.Validate(); EnsureSupported(original);
        if (expected is not null && (!SameTarget(expected, original) || !SameProfile(expected, original)
            || !original.Ramp.Matches(expected.Ramp, ReadbackTolerance)))
            throw new ProfilePreviewException("display_state_changed", "The display changed after the preflight check.");
        _sessionStart = _deadlineStart = _time.GetTimestamp();
        _deadlineDuration = TimeSpan.FromSeconds(seconds);
        _allowsWindowEdits = windowOwned && savedCurve is null;
        var candidate = savedCurve?.Copy() ?? GammaRampMath.Compose(original.Ramp, preset.Calibration);
        candidate.Validate();
        DateTimeOffset startedAt = _time.GetUtcNow();
        Session = new() { Id = id, Original = original, Preset = preset, Candidate = candidate,
            LastWritten = original.Ramp.Copy(), IsBindingRestore = savedCurve is not null && !undoOf.HasValue, UndoOf = undoOf,
            IsReviewing = undoOf.HasValue, StartedAt = startedAt, ExpiresAt = startedAt + _deadlineDuration };
        journal.Save(Session);
        try { Apply(candidate); return Tick(); }
        catch { Revert("preview_failed"); throw; }
    }
    public GammaCalibrationSession Update(CalibrationPreset preset, bool showOriginal = false, bool renew = false)
    {
        Tick(); if (Session!.Status != "previewing") return Session;
        if (Session.UndoOf is not null)
            throw new ProfilePreviewException("undo_preview_fixed", "An undo preview uses the saved previous curve.");
        PresetCodec.Validate(preset);
        var candidate = GammaRampMath.Compose(Session.Original.Ramp, preset.Calibration);
        Session = Session with { Preset = preset, Candidate = candidate, ShowingOriginal = showOriginal };
        if (renew) Renew();
        if (Session.Status != "previewing") return Session;
        try { Apply(showOriginal ? Session.Original.Ramp : candidate); }
        catch { Revert("preview_failed"); throw; }
        return Session!;
    }
    public GammaCalibrationSession Tick(bool ownerAlive = true)
    {
        if (Session is null) throw new InvalidOperationException("No calibration preview exists.");
        if (Session.Status != "previewing") return Session;
        if (!ownerAlive) return Revert("owner_exited");
        if (RevertIfStopped()) return Session;
        try
        {
            var now = backend.Read(Session.Original.DisplayId);
            if (!SameTarget(Session.Original, now)) return Revert("display_state_changed");
            if (!SameProfile(Session.Original, now)) { Persist("superseded", "external_profile_change"); return Session; }
            if (!now.Ramp.Matches(Session.LastWritten, ReadbackTolerance)) return Revert("external_curve_change");
        }
        catch { return Revert("display_unavailable"); }
        RevertIfStopped();
        return Session;
    }
    public void Renew()
    {
        if (!_allowsWindowEdits || Session is not { Status: "previewing", IsReviewing: false }) return;
        RestartDeadline(TimeSpan.FromSeconds(60));
    }
    private void RestartDeadline(TimeSpan duration, bool? reviewing = null)
    {
        if (StopReason(out long now, out DateTimeOffset utcNow) is { } reason)
        {
            Revert(reason);
            return;
        }
        TimeSpan remaining = WindowPreviewLimit - _time.GetElapsedTime(_sessionStart, now);
        TimeSpan effectiveDuration = duration < remaining ? duration : remaining;
        DateTimeOffset expiresAt = utcNow + effectiveDuration;
        DateTimeOffset latest = Session!.StartedAt + WindowPreviewLimit;
        var next = Session with { ExpiresAt = expiresAt < latest ? expiresAt : latest, IsReviewing = reviewing ?? Session.IsReviewing };
        journal.Save(next);
        _deadlineStart = now;
        _deadlineDuration = effectiveDuration;
        Session = next;
        RevertIfStopped();
    }
    public void SetReviewMode(bool reviewing, CalibrationPreset? edits = null)
    {
        if (!_allowsWindowEdits || Tick() is not { Status: "previewing" } current) return;
        bool changingMode = current.IsReviewing != reviewing;
        if ((edits is not null && edits != current.Preset) || (changingMode && current.ShowingOriginal))
        {
            // Apply queued edits under the current deadline before opening the next phase.
            bool keepOriginal = !changingMode && current.ShowingOriginal;
            if (Update(edits ?? current.Preset, showOriginal: keepOriginal).Status != "previewing") return;
        }
        if (changingMode) RestartDeadline(TimeSpan.FromSeconds(reviewing ? 30 : 60), reviewing);
    }
    public GammaCalibrationSession Confirm(BindingSaveIntent? bindingIntent = null)
    {
        Tick(); if (Session!.Status != "previewing") return Session;
        try
        {
            Apply(Session.Candidate);
            if (Session.Status != "previewing" || RevertIfStopped()) return Session;
            Session = Session! with { ProfileCommitStarted = true, ShowingOriginal = false, BindingIntent = bindingIntent };
            Persist("committing", "saving_profile");
            if (StopReason() is { } stopped)
            {
                Session = Session with { ProfileCommitStarted = false, BindingIntent = null };
                return Revert(stopped);
            }
            var selection = backend.Commit(Session!);
            Session = Session! with { InstalledProfile = selection.ProfileName };
            var current = backend.Read(Session.Original.DisplayId);
            if (!SameTarget(Session.Original, current) || !current.Ramp.Matches(Session.Candidate, ReadbackTolerance))
                throw new IOException("Calibration curve changed while saving the profile.");
            if (!selection.Matches(current))
                throw new ProfilePreviewException("profile_selection_changed", new LocalizationService()["ProfileChangedBeforeSave"]);
            if (Session.IsBindingRestore && getCancellationReason?.Invoke() is { } canceled) return Revert(canceled);
            if (!Session.IsBindingRestore) Session = Session with { BindingStatus = "pending" };
            Persist("confirmed", "confirmed", true);
        }
        catch (ProfilePreviewException ex) when (ex.Code == "profile_selection_changed") { Revert(ex.Code); throw; }
        catch { Revert("commit_failed"); throw; }
        return Session!;
    }
    public GammaCalibrationSession Revert(string reason = "user_reverted") => Revert(reason, interrupted: false);

    private GammaCalibrationSession Revert(string reason, bool interrupted)
    {
        if (Session is null) throw new InvalidOperationException("No calibration preview exists.");
        if (!Session.NeedsRecovery) return Session;
        // The applying journal must exist before any curve write can begin.
        if (Session.Status == "prepared")
        {
            Persist("reverted", reason is "preview_expired" or "owner_exited" or "startup_disabled" or "startup_unavailable" ? reason : "preview_not_applied");
            return Session;
        }
        try
        {
            try { Persist("recovering", reason); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var original = Session.Original;
            var current = interrupted ? backend.ReadRecoveryTarget(original) : backend.Read(original.DisplayId);
            bool originalPath = SameRecoveryTarget(original, current);
            if (!originalPath && !(interrupted && SameRelocatedTarget(original, current))) return Pending("recovery_target_changed");
            var target = originalPath ? original : current;
            bool profileCommit = Session.ProfileCommitStarted && !Session.IsBindingRestore;
            if (!profileCommit && !SameProfile(Session.Original, current)) return Superseded("external_profile_change");

            var beforeProfileRestore = current;
            bool ownsCurve = IsKnownCurve(Session, current.Ramp);
            bool selectionRestored = false;
            if (profileCommit)
            {
                // Even if the curve has changed, remove an association created by an interrupted commit.
                bool canRecordReset = SameTarget(original, current) && !SameProfile(original, current)
                    && (ownsCurve || GammaRamp.Identity().Matches(current.Ramp, ReadbackTolerance));
                bool previousResetIntent = Session.ProfileRollbackMayResetCurve;
                try { selectionRestored = backend.RestoreProfile(Session, current, canRecordReset ? RecordProfileRollback : null); }
                catch (ProfilePreviewException ex) when (!previousResetIntent && Session!.ProfileRollbackMayResetCurve
                    && ex.Code is "recovery_target_changed" or "previous_system_profile_changed" or "profile_device_unavailable")
                {
                    // The final check refused this attempt before a native write. Keep evidence from earlier attempts only.
                    Session = Session with { ProfileRollbackMayResetCurve = false };
                    throw;
                }
                current = backend.Read(target.DisplayId);
            }
            if (!SameRecoveryTarget(target, current)) return Pending("recovery_target_changed");
            if (!SameProfile(Session.Original, current)) return Pending("profile_restore_not_verified");
            if (!current.Ramp.Matches(Session.Original.Ramp, ReadbackTolerance))
            {
                bool mayHaveReset = SameTarget(beforeProfileRestore, current)
                    && (selectionRestored || (Session.ProfileRollbackMayResetCurve && SameTarget(original, beforeProfileRestore)));
                var linear = mayHaveReset ? GammaRamp.Identity() : null;
                // The original ICC switch may already have reset the curve before rollback began.
                bool resetBeforeRestore = linear?.Matches(beforeProfileRestore.Ramp, ReadbackTolerance) == true
                    && SameTarget(original, beforeProfileRestore);
                if (!ownsCurve && !resetBeforeRestore) return Superseded("external_curve_change");
                if (!IsKnownCurve(Session, current.Ramp))
                {
                    if (linear?.Matches(current.Ramp, ReadbackTolerance) != true)
                        return Superseded("external_curve_change");

                    // Keep the observed reset as a known starting point if the rollback write is interrupted.
                    var intent = Session with { BeforeWrite = current.Ramp.Copy() };
                    journal.Save(intent);
                    Session = intent;
                }
                backend.TryWrite(current, Session.Original.Ramp);
            }
            var restored = backend.Read(target.DisplayId);
            if (!SameRecoveryTarget(target, restored) || !SameProfile(Session.Original, restored)
                || !restored.Ramp.Matches(Session.Original.Ramp, ReadbackTolerance)) return Pending("restore_not_verified");
            Persist("reverted", reason, true);
        }
        catch (ProfilePreviewException ex) when (ex.Code == "external_profile_change") { return Superseded(ex.Code); }
        catch (ProfilePreviewException ex) when (ex.Code is "display_disconnected" or "remote_session_blocked" or "display_identity_ambiguous"
            or "recovery_target_changed" or "profile_device_unavailable") { return Pending(ex.Code); }
        catch (ProfilePreviewException ex) when (ex.Code is "profile_state_unavailable" or "profile_scope_unavailable"
            or "profile_file_changed" or "profile_file_unavailable" or "previous_system_profile_changed")
        { return Pending(ex.Code); }
        catch { return Pending("restore_failed"); }
        return Session;
    }

    private void RecordProfileRollback()
    {
        if (Session!.ProfileRollbackMayResetCurve) return;
        // Keep the authorization if the process stops after switching ICC and before reading the curve.
        var intent = Session with { ProfileRollbackMayResetCurve = true };
        journal.Save(intent);
        Session = intent;
    }

    public GammaCalibrationSession Recover(GammaCalibrationSession saved)
    {
        if (Session is not null) throw new InvalidOperationException("This transaction already has a session.");
        Session = saved; return saved.NeedsRecovery ? Revert("interrupted_preview", interrupted: true) : saved;
    }
    private bool RevertIfStopped()
    {
        if (StopReason() is not { } reason) return false;
        Revert(reason);
        return true;
    }
    private string? StopReason() => StopReason(out _, out _);
    private string? StopReason(out long now, out DateTimeOffset utcNow)
    {
        bool ownerExited = isOwnerAlive?.Invoke() == false;
        string? canceled = getCancellationReason?.Invoke();
        now = _time.GetTimestamp();
        utcNow = _time.GetUtcNow();
        if (ownerExited) return "owner_exited";
        if (canceled is not null) return canceled;
        return _time.GetElapsedTime(_deadlineStart, now) >= _deadlineDuration || utcNow >= Session!.ExpiresAt ? "preview_expired" : null;
    }
    private void Apply(GammaRamp ramp)
    {
        var previous = Session!;
        var current = backend.Read(previous.Original.DisplayId);
        EnsureSupported(current);
        if (!SameTarget(previous.Original, current) || !SameProfile(previous.Original, current) || !current.Ramp.Matches(previous.LastWritten, ReadbackTolerance))
            throw new ProfilePreviewException("display_state_changed", "Display state changed during calibration.");
        if (RevertIfStopped()) return;
        // Persist the intended write before issuing it, including on partial native failure.
        bool writeAuthorized = false;
        string? refusal = null;
        bool written;
        try
        {
            Session = previous with { BeforeWrite = current.Ramp.Copy(), LastWritten = ramp.Copy() };
            Persist("applying", "applying");
            refusal = StopReason();
            written = refusal is null && backend.TryWrite(current, ramp, AuthorizeWrite);
        }
        catch
        {
            if (!writeAuthorized) Session = previous;
            throw;
        }
        if (!written)
        {
            // The current process knows the request did not reach the driver. Only the previous preview can need rollback.
            Session = previous;
            Revert(refusal ?? "preview_not_applied");
            return;
        }
        if (RevertIfStopped()) return;
        var applied = backend.Read(current.DisplayId);
        if (!SameTarget(Session.Original, applied) || !SameProfile(Session.Original, applied) || !applied.Ramp.Matches(ramp, ReadbackTolerance))
            throw new ProfilePreviewException("curve_not_verified", "The display driver did not report the requested curve.");
        if (RevertIfStopped()) return;
        Session = Session with { BeforeWrite = null };
        Persist("previewing", "previewing", true);
        RevertIfStopped();

        bool AuthorizeWrite()
        {
            refusal = StopReason();
            writeAuthorized = refusal is null;
            return writeAuthorized;
        }
    }
    public static bool SameTarget(GammaDeviceState before, GammaDeviceState now) =>
        SamePath(before, now)
        && before.TopologyFingerprint == now.TopologyFingerprint
        && (before.ActiveTopologySha256 is null || before.ActiveTopologySha256 == now.ActiveTopologySha256)
        && now.ExplicitSdr && !now.SharedSource;
    public static bool SameRecoveryTarget(GammaDeviceState before, GammaDeviceState now) =>
        SameTarget(before, now) || (now.ExplicitSdr && !now.SharedSource && SamePath(before, now)
            && before.SourceId is not null && before.PersistenceKey is not null);
    public static bool SameRelocatedTarget(GammaDeviceState before, GammaDeviceState now) =>
        now.ExplicitSdr && !now.SharedSource && now.ProfileMhc2Present != true && now.SourceId is not null
        && MonitorIdentity.SameProfileDevice(before.PersistenceKey, now.PersistenceKey, before.ProfileDeviceSha256, now.ProfileDeviceSha256);
    private static bool SamePath(GammaDeviceState before, GammaDeviceState now) =>
        before.DisplayId == now.DisplayId && before.TargetKey == now.TargetKey && before.DeviceName == now.DeviceName
        && (before.SourceId is null || before.SourceId == now.SourceId)
        && (before.PersistenceKey is null || before.PersistenceKey == now.PersistenceKey)
        && (before.ProfileDeviceSha256 is null || string.Equals(before.ProfileDeviceSha256, now.ProfileDeviceSha256, StringComparison.OrdinalIgnoreCase));
    public static bool SameProfile(GammaDeviceState before, GammaDeviceState now) =>
        ProfilePreviewPolicy.EqualName(before.ProfileName, now.ProfileName) && before.ProfileScope == now.ProfileScope
        && (before.SelectedProfileScope is null || before.SelectedProfileScope == now.SelectedProfileScope);
    private static bool IsKnownCurve(GammaCalibrationSession session, GammaRamp ramp) =>
        ramp.Matches(session.Original.Ramp, ReadbackTolerance) || ramp.Matches(session.LastWritten, ReadbackTolerance)
        || (session.BeforeWrite is not null && ramp.Matches(session.BeforeWrite, ReadbackTolerance));
    private static void EnsureSupported(GammaDeviceState state)
    {
        if (!state.ExplicitSdr) throw new ProfilePreviewException("sdr_path_blocked", "This calibration path requires SDR with Advanced Color disabled.");
        if (state.SharedSource) throw new ProfilePreviewException("shared_source_blocked", "Mirrored displays cannot be calibrated independently.");
        if (state.ProfileMhc2Present == true)
            throw new ProfilePreviewException("mhc2_profile_blocked", "The selected color profile contains MHC2 hardware calibration.");
    }
    private GammaCalibrationSession Pending(string reason)
    { Session = Session! with { Status = "recovery-pending", ReasonCode = reason, CurveReadbackVerified = false }; journal.Save(Session); return Session; }
    private GammaCalibrationSession Superseded(string reason)
    { Persist("superseded", reason); return Session!; }
    private void Persist(string status, string reason, bool verified = false)
    { var next = Session! with { Status = status, ReasonCode = reason, CurveReadbackVerified = verified }; journal.Save(next); Session = next; }
}
