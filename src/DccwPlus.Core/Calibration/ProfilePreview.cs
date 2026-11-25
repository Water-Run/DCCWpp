using System.Text.Json.Serialization;
using DccwPlus.Core.Localization;

namespace DccwPlus.Core.Calibration;

public sealed record AssociatedProfile(string FileName, string? Sha256, bool CanSelect,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Mhc2Present = null);
public sealed record ProfileAssociationState(string DisplayId, string TargetKey, uint SourceId,
    string TopologyFingerprint, string? CurrentProfile, string Scope, bool ExplicitSdr,
    bool SharedSource, IReadOnlyList<AssociatedProfile> Profiles,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PersistenceKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsRemoteSession = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActiveTopologySha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProfileDeviceSha256 = null);
public sealed record ProfilePreviewPlan(bool CanPreview, string ReasonCode, ProfileAssociationState State,
    string CandidateProfile, string? CandidateSha256);

public interface IProfileAssociationBackend
{
    ProfileAssociationState Read(string displayId);
    // Interrupted recovery may resolve a unique current path for the original display and profile device.
    ProfileAssociationState ReadRecoveryTarget(ProfileAssociationState original) => Read(original.DisplayId);
    // Recheck the expected state, then evaluate canWrite immediately before the native call.
    // A false return guarantees no write was attempted. Recovery does not supply this guard.
    bool TrySetDefault(ProfileAssociationState expected, string profile, string sha256, Func<bool>? canWrite = null);
}

public sealed class ProfilePreviewException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class ProfilePreviewPolicy
{
    public static ProfilePreviewPlan Plan(ProfileAssociationState state, string candidate)
    {
        IccProfileInspector.ValidateFileName(candidate);
        var original = ValidProfile(state, state.CurrentProfile);
        var proposed = ValidProfile(state, candidate);
        string reason = AssociationBlockReason(state, candidate, original, proposed)
            ?? (original?.Mhc2Present == true || proposed?.Mhc2Present == true ? "mhc2_profile_blocked" : "ready");
        return new(reason == "ready", reason, state, candidate, proposed?.Sha256);
    }

    // Recorded rollback evidence remains usable when a profile is outside the current preview policy.
    internal static bool HasRecordedAssociation(ProfileAssociationState state, string candidate, string sha256)
    {
        var proposed = ValidProfile(state, candidate);
        return AssociationBlockReason(state, candidate, ValidProfile(state, state.CurrentProfile), proposed) is null
            && proposed?.Sha256 == sha256;
    }

    private static string? AssociationBlockReason(ProfileAssociationState state, string candidate,
        AssociatedProfile? original, AssociatedProfile? proposed) =>
        state.IsRemoteSession ? "remote_session_blocked"
            : !state.ExplicitSdr ? "sdr_path_blocked"
            : state.SharedSource ? "shared_source_blocked"
            : state.Scope != "current-user" ? "user_scope_required"
            : string.IsNullOrWhiteSpace(state.TargetKey) || string.IsNullOrWhiteSpace(state.TopologyFingerprint) ? "display_identity_unavailable"
            : original is null ? "original_profile_unavailable"
            : proposed is null ? "candidate_profile_unavailable"
            : EqualName(state.CurrentProfile, candidate) ? "profile_already_selected" : null;

    public static AssociatedProfile? ValidProfile(ProfileAssociationState state, string? name) =>
        state.Profiles.FirstOrDefault(p => EqualName(p.FileName, name) && p.CanSelect && IsHash(p.Sha256));
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static bool EqualName(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    public static bool SameTarget(ProfileAssociationState before, ProfileAssociationState now) =>
        SamePath(before, now) && before.TopologyFingerprint == now.TopologyFingerprint
        && (before.ActiveTopologySha256 is null || before.ActiveTopologySha256 == now.ActiveTopologySha256);
    public static bool SameRecoveryTarget(ProfileAssociationState before, ProfileAssociationState now) =>
        SameTarget(before, now) || (before.PersistenceKey is not null && SamePath(before, now));
    internal static bool SameRelocatedTarget(ProfileAssociationState before, ProfileAssociationState now) =>
        SameUserSdrScope(before, now)
        && MonitorIdentity.SameProfileDevice(before.PersistenceKey, now.PersistenceKey, before.ProfileDeviceSha256, now.ProfileDeviceSha256);
    private static bool SamePath(ProfileAssociationState before, ProfileAssociationState now) =>
        before.DisplayId == now.DisplayId && before.TargetKey == now.TargetKey && before.SourceId == now.SourceId
        && (before.PersistenceKey is null || before.PersistenceKey == now.PersistenceKey)
        && (before.ProfileDeviceSha256 is null || string.Equals(before.ProfileDeviceSha256, now.ProfileDeviceSha256, StringComparison.OrdinalIgnoreCase))
        && SameUserSdrScope(before, now);
    private static bool SameUserSdrScope(ProfileAssociationState before, ProfileAssociationState now) =>
        now.ExplicitSdr && !now.SharedSource && !now.IsRemoteSession
        && before.Scope == now.Scope && now.Scope == "current-user";
}

public sealed record ProfilePreviewSession
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid Id { get; init; }
    public required ProfileAssociationState Original { get; init; }
    public required string CandidateProfile { get; init; }
    public required string CandidateSha256 { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public string Status { get; init; } = "prepared";
    public string ReasonCode { get; init; } = "prepared";
    // This checks Windows' default-profile association, never panel color accuracy.
    public bool AssociationVerified { get; init; }
    public bool ColorAccuracyVerified => false;
    public bool NeedsRecovery => Status is "prepared" or "applying" or "previewing" or "recovering" or "recovery-pending";
}

public interface IProfilePreviewJournal { void Save(ProfilePreviewSession session, Action? beforePublish = null); }

// Only the independent worker owns this synchronous state machine and its native writes.
public sealed class ProfilePreviewTransaction(IProfileAssociationBackend backend, IProfilePreviewJournal journal,
    TimeProvider? clock = null, Func<bool>? isOwnerAlive = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _started;
    private TimeSpan _duration;
    public ProfilePreviewSession? Session { get; private set; }

    public ProfilePreviewSession Begin(Guid id, string displayId, string candidate, int seconds)
    {
        if (Session is not null) throw new InvalidOperationException("A transaction can only be started once.");
        if (id == Guid.Empty || seconds is < 10 or > 60) throw new ArgumentException("Preview timeout must be 10–60 seconds.");
        var plan = ProfilePreviewPolicy.Plan(backend.Read(displayId), candidate);
        if (!plan.CanPreview)
            throw new ProfilePreviewException(plan.ReasonCode, plan.ReasonCode == "mhc2_profile_blocked"
                ? new LocalizationService()["Mhc2AssociationPreviewBlocked"] : "This profile cannot be previewed with a verified recovery path.");
        _duration = TimeSpan.FromSeconds(seconds);
        _started = _clock.GetTimestamp();
        Session = new() { Id = id, Original = plan.State, CandidateProfile = candidate,
            CandidateSha256 = plan.CandidateSha256!, ExpiresAt = _clock.GetUtcNow() + _duration };
        journal.Save(Session); // Durable recovery information must precede every write.
        bool writeAuthorized = false;
        string? refusal = null;
        try
        {
            if (StopReason() is { } notStarted) return FinishWithoutWrite(notStarted);
            Persist("applying", "applying");
            refusal = StopReason();
            if (refusal is not null || !backend.TrySetDefault(plan.State, candidate, plan.CandidateSha256!, AuthorizeWrite))
                return FinishWithoutWrite(refusal ?? "preview_not_applied");
            if (StopReason() is { } afterWrite) return Revert(afterWrite);
            if (!CandidateIsCurrent(backend.Read(displayId)))
                throw new ProfilePreviewException("association_not_verified", "Windows did not report the requested default association.");
            if (StopReason() is { } afterRead) return Revert(afterRead);
            return PublishVerified("previewing");
        }
        catch (Exception ex)
        {
            if (writeAuthorized) Revert("apply_failed");
            else if (Session.NeedsRecovery) FinishWithoutWrite(ex is ProfilePreviewException failure ? failure.Code : "apply_failed");
            throw;
        }

        bool AuthorizeWrite()
        {
            refusal = StopReason();
            writeAuthorized = refusal is null;
            return writeAuthorized;
        }
    }

    private ProfilePreviewSession FinishWithoutWrite(string reason)
    {
        // No association write was attempted. A journal failure must not trigger a rollback.
        Session = Session! with { Status = "reverted", ReasonCode = reason, AssociationVerified = false };
        journal.Save(Session);
        return Session;
    }

    public ProfilePreviewSession Tick(bool ownerAlive = true)
    {
        RequireSession();
        if (Session!.Status != "previewing") return Session;
        if (!ownerAlive) return Revert("owner_exited");
        return PreviewFailure() is { } reason ? Revert(reason) : Session;
    }

    public ProfilePreviewSession Confirm()
    {
        RequireSession();
        if (Session!.Status != "previewing") return Session; // Late/repeated commands never reapply.
        if (StopReason() is { } stopped) return Revert(stopped);
        try { return PublishVerified("confirmed"); }
        catch (Exception) { Revert("confirmation_not_saved"); throw; }
    }

    private ProfilePreviewSession PublishVerified(string status)
    {
        try
        {
            Persist(status, status, verified: true, beforePublish: () =>
            {
                if (PreviewFailure() is { } reason) throw new PreviewPublicationRejectedException(reason);
            });
        }
        catch (PreviewPublicationRejectedException ex) { return Revert(ex.ReasonCode); }
        return Session!;
    }

    private string? PreviewFailure()
    {
        if (StopReason() is { } stopped) return stopped;
        bool current;
        try { current = CandidateIsCurrent(backend.Read(Session!.Original.DisplayId)); }
        catch (Exception) { return "display_unavailable"; }
        return StopReason() ?? (current ? null : "display_state_changed");
    }

    public ProfilePreviewSession Revert(string reason = "user_reverted") => Revert(reason, interrupted: false);

    private ProfilePreviewSession Revert(string reason, bool interrupted)
    {
        RequireSession();
        if (!Session!.NeedsRecovery) return Session;
        // The applying journal must exist before any association write can begin.
        if (Session.Status == "prepared") return FinishWithoutWrite("preview_not_applied");
        try
        {
            // A journal failure must not prevent an already-required rollback.
            try { Persist("recovering", reason); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var current = interrupted ? backend.ReadRecoveryTarget(Session.Original) : backend.Read(Session.Original.DisplayId);
            if (current.IsRemoteSession) return Pending("remote_session_blocked");
            if (!ProfilePreviewPolicy.SameRecoveryTarget(Session.Original, current)
                && !(interrupted && ProfilePreviewPolicy.SameRelocatedTarget(Session.Original, current)))
                return Pending("recovery_target_changed");
            var original = ProfilePreviewPolicy.ValidProfile(Session.Original, Session.Original.CurrentProfile)!;
            bool originalSelected = ProfilePreviewPolicy.EqualName(current.CurrentProfile, original.FileName);
            if (!originalSelected && !ProfilePreviewPolicy.EqualName(current.CurrentProfile, Session.CandidateProfile))
            {
                // Do not overwrite a change made by Windows or another color application.
                Persist("superseded", "external_profile_change");
                return Session;
            }
            if (ProfilePreviewPolicy.ValidProfile(current, original.FileName)?.Sha256 != original.Sha256)
                return Pending("original_profile_changed");
            if (originalSelected)
            {
                Persist("reverted", reason, true);
                return Session;
            }
            if (ProfilePreviewPolicy.ValidProfile(current, Session.CandidateProfile)?.Sha256 != Session.CandidateSha256)
                return Pending("profile_file_changed");
            backend.TrySetDefault(current, original.FileName, original.Sha256!);
            var restored = backend.Read(current.DisplayId);
            if (!ProfilePreviewPolicy.SameTarget(current, restored)
                || !ProfilePreviewPolicy.EqualName(restored.CurrentProfile, original.FileName)
                || ProfilePreviewPolicy.ValidProfile(restored, original.FileName)?.Sha256 != original.Sha256)
                return Pending("restore_not_verified");
            Persist("reverted", reason, true);
        }
        catch (ProfilePreviewException ex) when (ex.Code is "display_disconnected" or "display_identity_ambiguous" or "recovery_target_changed"
            or "profile_device_unavailable" or "profile_scope_unavailable"
            or "profile_state_unavailable" or "profile_file_changed" or "remote_session_blocked") { return Pending(ex.Code); }
        catch (Exception) { return Pending("restore_failed"); }
        return Session!;
    }

    public ProfilePreviewSession Recover(ProfilePreviewSession saved)
    {
        if (Session is not null) throw new InvalidOperationException("This transaction already has a session.");
        Session = saved;
        return saved.NeedsRecovery ? Revert("interrupted_preview", interrupted: true) : saved;
    }

    private bool CandidateIsCurrent(ProfileAssociationState current) =>
        ProfilePreviewPolicy.SameTarget(Session!.Original, current)
        && ProfilePreviewPolicy.EqualName(current.CurrentProfile, Session.CandidateProfile)
        && ProfilePreviewPolicy.ValidProfile(current, Session.CandidateProfile)?.Sha256 == Session.CandidateSha256;
    private string? StopReason()
    {
        if (isOwnerAlive?.Invoke() == false) return "owner_exited";
        return _clock.GetElapsedTime(_started) >= _duration || _clock.GetUtcNow() >= Session!.ExpiresAt ? "preview_expired" : null;
    }
    private ProfilePreviewSession Pending(string reason)
    {
        Session = Session! with { Status = "recovery-pending", ReasonCode = reason, AssociationVerified = false };
        journal.Save(Session);
        return Session;
    }
    private void Persist(string status, string reason, bool verified = false, Action? beforePublish = null)
    {
        var next = Session! with { Status = status, ReasonCode = reason, AssociationVerified = verified };
        journal.Save(next, beforePublish);
        Session = next;
    }
    private void RequireSession() { if (Session is null) throw new InvalidOperationException("No preview has been started."); }

    private sealed class PreviewPublicationRejectedException(string reasonCode) : Exception(reasonCode)
    {
        public string ReasonCode { get; } = reasonCode;
    }
}
