using DccwPlus.Core.Presets;

namespace DccwPlus.Core.Calibration;

public sealed record GammaDeviceState(string DisplayId, string DeviceName, string TargetKey,
    string TopologyFingerprint, bool ExplicitSdr, bool SharedSource, GammaRamp Ramp,
    string? ProfileName = null, string? ProfileScope = null, uint? SourceId = null, string? PersistenceKey = null,
    bool? ProfileMhc2Present = null, string? SelectedProfileScope = null);
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
    public required DateTimeOffset ExpiresAt { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public string Status { get; init; } = "prepared";
    public string ReasonCode { get; init; } = "prepared";
    public string? InstalledProfile { get; init; }
    public string? BindingStatus { get; init; }
    public string? BindingReasonCode { get; init; }
    public bool CurveReadbackVerified { get; init; }
    public bool IsBindingRestore { get; init; }
    public Guid? UndoOf { get; init; }
    public bool ProfileCommitStarted { get; init; }
    public bool ColorAccuracyVerified => false;
    public bool NeedsRecovery => Status is "prepared" or "applying" or "previewing" or "committing" or "recovering" or "recovery-pending";
}
public interface IGammaCalibrationBackend
{
    GammaDeviceState Read(string displayId);
    void Write(GammaDeviceState expected, GammaRamp ramp);
    string? Commit(GammaCalibrationSession session);
    void RestoreProfile(GammaCalibrationSession session);
}
public interface IGammaCalibrationJournal { void Save(GammaCalibrationSession session); }

public sealed class GammaCalibrationTransaction(IGammaCalibrationBackend backend, IGammaCalibrationJournal journal,
    TimeProvider? timeProvider = null)
{
    private const int ReadbackTolerance = 257;
    private static readonly TimeSpan WindowPreviewLimit = TimeSpan.FromMinutes(30);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _deadlineStart;
    private TimeSpan _deadlineDuration;
    private long _sessionStart;
    private bool _allowsWindowEdits;
    private bool _reviewing;
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
            throw new ProfilePreviewException("display_state_changed", "The display changed after the restore check.");
        _sessionStart = _deadlineStart = _time.GetTimestamp();
        _deadlineDuration = TimeSpan.FromSeconds(seconds);
        _allowsWindowEdits = windowOwned && savedCurve is null;
        _reviewing = undoOf.HasValue;
        var candidate = savedCurve?.Copy() ?? GammaRampMath.Compose(original.Ramp, preset.Calibration);
        candidate.Validate();
        DateTimeOffset startedAt = _time.GetUtcNow();
        Session = new() { Id = id, Original = original, Preset = preset, Candidate = candidate,
            LastWritten = original.Ramp.Copy(), IsBindingRestore = savedCurve is not null && !undoOf.HasValue, UndoOf = undoOf,
            StartedAt = startedAt, ExpiresAt = startedAt + _deadlineDuration };
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
        return Session;
    }
    public void Renew()
    {
        if (!_allowsWindowEdits || Session?.Status != "previewing" || _reviewing) return;
        RestartDeadline(TimeSpan.FromSeconds(60));
    }
    private bool RestartDeadline(TimeSpan duration)
    {
        long now = _time.GetTimestamp();
        DateTimeOffset utcNow = _time.GetUtcNow();
        if (RevertIfExpired(now, utcNow)) return false;
        TimeSpan remaining = WindowPreviewLimit - _time.GetElapsedTime(_sessionStart, now);
        TimeSpan effectiveDuration = duration < remaining ? duration : remaining;
        DateTimeOffset expiresAt = utcNow + effectiveDuration;
        DateTimeOffset latest = Session!.StartedAt + WindowPreviewLimit;
        var next = Session with { ExpiresAt = expiresAt < latest ? expiresAt : latest };
        journal.Save(next);
        _deadlineStart = now;
        _deadlineDuration = effectiveDuration;
        Session = next;
        return true;
    }
    public void SetReviewMode(bool reviewing)
    {
        if (!_allowsWindowEdits || _reviewing == reviewing || Tick().Status != "previewing") return;
        if (RestartDeadline(TimeSpan.FromSeconds(reviewing ? 30 : 60))) _reviewing = reviewing;
    }
    public GammaCalibrationSession Confirm()
    {
        Tick(); if (Session!.Status != "previewing") return Session;
        try
        {
            Apply(Session.Candidate);
            if (Session.Status != "previewing" || RevertIfExpired()) return Session;
            Session = Session! with { ProfileCommitStarted = true, ShowingOriginal = false };
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
    public GammaCalibrationSession Revert(string reason = "user_reverted")
    {
        if (Session is null) throw new InvalidOperationException("No calibration preview exists.");
        if (!Session.NeedsRecovery) return Session;
        try
        {
            try { Persist("recovering", reason); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var current = backend.Read(Session.Original.DisplayId);
            if (!SameRecoveryTarget(Session.Original, current)) return Pending("recovery_target_changed");
            bool profileCommit = Session.ProfileCommitStarted && !Session.IsBindingRestore;
            if (!profileCommit && !SameProfile(Session.Original, current)) return Superseded("external_profile_change");

            bool ownsCurve = IsKnownCurve(Session, current.Ramp);
            if (profileCommit)
            {
                // Even if the curve has changed, remove an association created by an interrupted commit.
                backend.RestoreProfile(Session);
                current = backend.Read(Session.Original.DisplayId);
            }
            if (!SameRecoveryTarget(Session.Original, current)) return Pending("recovery_target_changed");
            if (!SameProfile(Session.Original, current)) return Pending("profile_restore_not_verified");
            if (!current.Ramp.Matches(Session.Original.Ramp, ReadbackTolerance))
            {
                if (!ownsCurve || !IsKnownCurve(Session, current.Ramp)) return Superseded("external_curve_change");
                backend.Write(current, Session.Original.Ramp);
            }
            var restored = backend.Read(Session.Original.DisplayId);
            if (!SameRecoveryTarget(Session.Original, restored) || !SameProfile(Session.Original, restored)
                || !restored.Ramp.Matches(Session.Original.Ramp, ReadbackTolerance)) return Pending("restore_not_verified");
            Persist("reverted", reason, true);
        }
        catch (ProfilePreviewException ex) when (ex.Code == "external_profile_change") { return Superseded(ex.Code); }
        catch (ProfilePreviewException ex) when (ex.Code == "display_disconnected") { return Pending(ex.Code); }
        catch (ProfilePreviewException ex) when (ex.Code is "profile_state_unavailable" or "profile_scope_unavailable")
        { return Pending(ex.Code); }
        catch { return Pending("restore_failed"); }
        return Session;
    }
    public GammaCalibrationSession Recover(GammaCalibrationSession saved)
    {
        if (Session is not null) throw new InvalidOperationException("This transaction already has a session.");
        Session = saved; return saved.NeedsRecovery ? Revert("interrupted_preview") : saved;
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
    }
    public static bool SameTarget(GammaDeviceState before, GammaDeviceState now) =>
        SamePath(before, now)
        && before.TopologyFingerprint == now.TopologyFingerprint && now.ExplicitSdr && !now.SharedSource;
    public static bool SameRecoveryTarget(GammaDeviceState before, GammaDeviceState now) =>
        SameTarget(before, now) || (now.ExplicitSdr && !now.SharedSource && SamePath(before, now)
            && before.SourceId is not null && before.PersistenceKey is not null);
    private static bool SamePath(GammaDeviceState before, GammaDeviceState now) =>
        before.DisplayId == now.DisplayId && before.TargetKey == now.TargetKey && before.DeviceName == now.DeviceName
        && (before.SourceId is null || before.SourceId == now.SourceId)
        && (before.PersistenceKey is null || before.PersistenceKey == now.PersistenceKey);
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
