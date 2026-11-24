using DccwPlus.Core.Presets;
using System.Text.Json.Serialization;

namespace DccwPlus.Core.Calibration;

public sealed record GammaDeviceState(string DisplayId, string DeviceName, string TargetKey,
    string TopologyFingerprint, bool ExplicitSdr, bool SharedSource, GammaRamp Ramp,
    string? ProfileName = null, string? ProfileScope = null, uint? SourceId = null, string? PersistenceKey = null,
    bool? ProfileMhc2Present = null, string? SelectedProfileScope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActiveTopologySha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProfileDeviceSha256 = null);

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
    void Write(GammaDeviceState expected, GammaRamp ramp);
    string? Commit(GammaCalibrationSession session);
    // Returns true when the effective selection needed restoring and the pre-restore state still matched expected.
    bool RestoreProfile(GammaCalibrationSession session, GammaDeviceState expected);
}
public interface IGammaCalibrationJournal { void Save(GammaCalibrationSession session); }

public sealed class GammaCalibrationTransaction(IGammaCalibrationBackend backend, IGammaCalibrationJournal journal,
    TimeProvider? timeProvider = null)
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
        if (RevertIfExpired()) return Session;
        try
        {
            var now = backend.Read(Session.Original.DisplayId);
            if (!SameTarget(Session.Original, now)) return Revert("display_state_changed");
            if (!SameProfile(Session.Original, now)) { Persist("superseded", "external_profile_change"); return Session; }
            if (!now.Ramp.Matches(Session.LastWritten, ReadbackTolerance)) return Revert("external_curve_change");
        }
        catch { return Revert("display_unavailable"); }
        RevertIfExpired();
        return Session;
    }
    public void Renew()
    {
        if (!_allowsWindowEdits || Session is not { Status: "previewing", IsReviewing: false }) return;
        RestartDeadline(TimeSpan.FromSeconds(60));
    }
    private void RestartDeadline(TimeSpan duration, bool? reviewing = null)
    {
        long now = _time.GetTimestamp();
        DateTimeOffset utcNow = _time.GetUtcNow();
        if (RevertIfExpired(now, utcNow)) return;
        TimeSpan remaining = WindowPreviewLimit - _time.GetElapsedTime(_sessionStart, now);
        TimeSpan effectiveDuration = duration < remaining ? duration : remaining;
        DateTimeOffset expiresAt = utcNow + effectiveDuration;
        DateTimeOffset latest = Session!.StartedAt + WindowPreviewLimit;
        var next = Session with { ExpiresAt = expiresAt < latest ? expiresAt : latest, IsReviewing = reviewing ?? Session.IsReviewing };
        journal.Save(next);
        _deadlineStart = now;
        _deadlineDuration = effectiveDuration;
        Session = next;
        RevertIfExpired();
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
            if (Session.Status != "previewing" || RevertIfExpired()) return Session;
            Session = Session! with { ProfileCommitStarted = true, ShowingOriginal = false, BindingIntent = bindingIntent };
            Persist("committing", "saving_profile");
            if (RevertIfExpired()) return Session;
            string? installed = backend.Commit(Session!);
            Session = Session! with { InstalledProfile = installed };
            var current = backend.Read(Session.Original.DisplayId);
            if (!SameTarget(Session.Original, current) || !ProfilePreviewPolicy.EqualName(current.ProfileName, installed)
                || !current.Ramp.Matches(Session.Candidate, ReadbackTolerance))
                throw new IOException("Calibration curve changed while saving the profile.");
            if (!Session.IsBindingRestore) Session = Session with { BindingStatus = "pending" };
            Persist("confirmed", "confirmed", true);
        }
        catch { Revert("commit_failed"); throw; }
        return Session!;
    }
    public GammaCalibrationSession Revert(string reason = "user_reverted") => Revert(reason, interrupted: false);

    private GammaCalibrationSession Revert(string reason, bool interrupted)
    {
        if (Session is null) throw new InvalidOperationException("No calibration preview exists.");
        if (!Session.NeedsRecovery) return Session;
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
                selectionRestored = backend.RestoreProfile(Session, current);
                current = backend.Read(target.DisplayId);
            }
            if (!SameRecoveryTarget(target, current)) return Pending("recovery_target_changed");
            if (!SameProfile(Session.Original, current)) return Pending("profile_restore_not_verified");
            if (!current.Ramp.Matches(Session.Original.Ramp, ReadbackTolerance))
            {
                if (!ownsCurve) return Superseded("external_curve_change");
                if (!IsKnownCurve(Session, current.Ramp))
                {
                    if (!selectionRestored || !SameTarget(beforeProfileRestore, current)
                        || !current.Ramp.Matches(GammaRamp.Identity(), ReadbackTolerance))
                        return Superseded("external_curve_change");

                    // Keep the observed reset as a known starting point if the rollback write is interrupted.
                    var intent = Session with { BeforeWrite = current.Ramp.Copy() };
                    journal.Save(intent);
                    Session = intent;
                }
                backend.Write(current, Session.Original.Ramp);
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
    public GammaCalibrationSession Recover(GammaCalibrationSession saved)
    {
        if (Session is not null) throw new InvalidOperationException("This transaction already has a session.");
        Session = saved; return saved.NeedsRecovery ? Revert("interrupted_preview", interrupted: true) : saved;
    }
    private bool RevertIfExpired() => RevertIfExpired(_time.GetTimestamp(), _time.GetUtcNow());
    private bool RevertIfExpired(long now, DateTimeOffset utcNow)
    {
        if (_time.GetElapsedTime(_deadlineStart, now) < _deadlineDuration && utcNow < Session!.ExpiresAt) return false;
        Revert("preview_expired");
        return true;
    }
    private void Apply(GammaRamp ramp)
    {
        var current = backend.Read(Session!.Original.DisplayId);
        EnsureSupported(current);
        if (!SameTarget(Session.Original, current) || !SameProfile(Session.Original, current) || !current.Ramp.Matches(Session.LastWritten, ReadbackTolerance))
            throw new ProfilePreviewException("display_state_changed", "Display state changed during calibration.");
        // Persist the intended write before issuing it, including on partial native failure.
        Session = Session with { BeforeWrite = current.Ramp.Copy(), LastWritten = ramp.Copy() };
        Persist("applying", "applying");
        if (RevertIfExpired()) return;
        backend.Write(current, ramp);
        var applied = backend.Read(current.DisplayId);
        if (!SameTarget(Session.Original, applied) || !SameProfile(Session.Original, applied) || !applied.Ramp.Matches(ramp, ReadbackTolerance))
            throw new ProfilePreviewException("curve_not_verified", "The display driver did not report the requested curve.");
        if (RevertIfExpired()) return;
        Session = Session with { BeforeWrite = null };
        Persist("previewing", "previewing", true);
        RevertIfExpired();
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
