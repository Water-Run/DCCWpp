using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Storage;

namespace DccwPlus.Windows;

public sealed record CalibrationVerificationReport(Guid CalibrationId, string SavedStatus, string Status, string ReasonCode)
{
    public DateTimeOffset? CapturedAt { get; init; }
    public string? DisplayId { get; init; }
    public bool? IsRemoteSession { get; init; }
    public string? SavedProfile { get; init; }
    public string? CurrentProfile { get; init; }
    public string? SelectedProfileScope { get; init; }
    public bool? ProfileSelectionMatches { get; init; }
    public bool? ProfileFileMatches { get; init; }
    public bool? LogicalCurveMatches { get; init; }
    public bool? WindowsCalibrationLoaderEnabled { get; init; }
    public bool ColorAccuracyVerified => false;
}

public sealed record DisplayCalibrationInspection(string DisplayId, string ReasonCode, DateTimeOffset? CapturedAt = null,
    string? PresetName = null, CalibrationVerificationReport? Verification = null);

public sealed class CalibrationVerification
{
    private readonly GammaCalibrationStore _sessions;
    private readonly PreviewRecoveryService _recovery;
    private readonly Func<string?, CalibrationCaptureEvidence> _capture;
    private readonly Func<GammaCalibrationSession, IccAssociationBackup> _readSavedAssociation;
    private readonly Func<bool?> _readLoader;

    public CalibrationVerification(string root) : this(root, new CalibrationCapture().ReadEvidence,
        new WindowsGammaCalibrationBackend(root).ReadAssociationBackup, WindowsGammaCalibrationBackend.ReadCalibrationLoader) { }

    internal CalibrationVerification(string root, Func<string?, CalibrationCaptureEvidence> capture,
        Func<GammaCalibrationSession, IccAssociationBackup> readSavedAssociation, Func<bool?> readLoader)
    {
        _sessions = new(root);
        _recovery = new(root);
        _capture = capture;
        _readSavedAssociation = readSavedAssociation;
        _readLoader = readLoader;
    }

    public CalibrationVerificationReport Read(Guid id)
    {
        var session = _sessions.Load(id);
        if (!IsSavedCalibration(session))
            return Inconclusive(session, "calibration_not_confirmed");

        using var gate = DisplayMutationLock.Acquire();
        if (HasInterruptedPreview(_sessions.Sessions()))
            return Inconclusive(session, "recovery_required");
        return Evaluate(session);
    }

    public DisplayCalibrationInspection InspectDisplay(string displayId, DisplayInfo? expected = null)
    {
        using var gate = DisplayMutationLock.Acquire();
        var sessions = _sessions.Sessions();
        if (HasInterruptedPreview(sessions)) return new(displayId, "recovery_required");

        var capture = _capture(displayId);
        var inspection = new DisplayCalibrationInspection(displayId, "display_identity_ambiguous", capture.Report.CapturedAt);
        if (capture.Displays.Count != 1) return inspection;

        var evidence = capture.Displays[0];
        var connected = capture.Connected;
        var display = evidence.Display;
        if (expected is not null && MonitorIdentity.ResolveSelectedDisplay(expected, connected, hadAmbiguousIdentity: true)?.Id != displayId)
            return inspection with { ReasonCode = "capture_state_changed" };

        var candidates = sessions.Where(session => IsSavedCalibration(session)
            && MonitorIdentity.MatchesSavedDisplay(session.Original, display))
            .OrderByDescending(session => ProfilePreviewPolicy.EqualName(session.InstalledProfile, display.ProfileName));
        string reason = "no_saved_calibration";
        foreach (var session in candidates)
        {
            if (connected.Count(current => MonitorIdentity.MatchesSavedDisplay(session.Original, current)) != 1)
                return inspection;

            var association = ReadSavedAssociation(session);
            string? profileDevice = SavedProfileDevice(session, association);
            // Older journals without a device key can only identify the original active path here.
            bool deviceMatches = profileDevice is null
                ? MonitorIdentity.MatchesSavedDisplay(session.Original with { PersistenceKey = null }, display)
                : WindowsDisplayService.HasUniqueProfileDevice(display, connected, profileDevice);
            if (!deviceMatches)
            {
                reason = "profile_device_unavailable";
                continue;
            }

            var verification = Compare(session, evidence, association, CapturedReport(session, capture, "display_disconnected"));
            return new(displayId, verification.ReasonCode, capture.Report.CapturedAt, session.Preset.Name, verification);
        }
        return inspection with { ReasonCode = reason };
    }

    internal CalibrationVerificationReport Evaluate(GammaCalibrationSession session)
    {
        var capture = _capture(null);
        var report = CapturedReport(session, capture, "display_disconnected");

        var matches = capture.Displays.Where(item => MonitorIdentity.MatchesSavedDisplay(session.Original, item.Display)).ToArray();
        if (matches.Length != 1)
            return report with { ReasonCode = matches.Length > 1 ? "display_identity_ambiguous"
                : report.IsRemoteSession == true ? "remote_session_blocked" : "display_disconnected" };

        var evidence = matches[0];
        var association = ReadSavedAssociation(session);
        string? profileDevice = SavedProfileDevice(session, association);
        if (profileDevice is not null && !WindowsDisplayService.HasUniqueProfileDevice(evidence.Display, capture.Connected, profileDevice))
            return report with { ReasonCode = "profile_device_unavailable" };

        return Compare(session, evidence, association, report);
    }

    private static CalibrationVerificationReport Compare(GammaCalibrationSession session, DisplayCalibrationEvidence evidence,
        IccAssociationBackup? association, CalibrationVerificationReport report)
    {
        var sample = evidence.Sample;
        report = report with { DisplayId = sample.DisplayId, CurrentProfile = sample.ProfileName, SelectedProfileScope = sample.SelectedProfileScope };
        if (!CalibrationService.IsExplicitSdr(evidence.Display)) return report with { ReasonCode = "sdr_path_blocked" };
        if (sample.CurveStatus is "shared_source_blocked" or "display_identity_unavailable")
            return report with { ReasonCode = sample.CurveStatus };

        if (sample.ProfileQueryStatus is "read" or "not-found")
        {
            bool selected = sample.ProfileScope == "current-user" && sample.SelectedProfileScope == "current-user"
                && ProfilePreviewPolicy.EqualName(sample.ProfileName, session.InstalledProfile);
            report = report with { ProfileSelectionMatches = selected };
            if (selected && sample.ProfileSha256 is not null && association?.CandidateHash is { } savedHash && ProfilePreviewPolicy.IsHash(savedHash))
                report = report with { ProfileFileMatches = string.Equals(savedHash, sample.ProfileSha256, StringComparison.OrdinalIgnoreCase) };
        }
        if (evidence.LogicalRamp is { } ramp)
            report = report with { LogicalCurveMatches = session.Candidate.Matches(ramp, GammaCalibrationTransaction.ReadbackTolerance) };

        if (report.IsRemoteSession == true) return report with { ReasonCode = "remote_session_blocked" };
        if (report.ProfileSelectionMatches == false) return report with { Status = "changed", ReasonCode = "profile_selection_changed" };
        if (report.ProfileFileMatches == false) return report with { Status = "changed", ReasonCode = "profile_file_changed" };
        if (report.LogicalCurveMatches == false) return report with { Status = "changed", ReasonCode = "logical_curve_changed" };
        if (report.ProfileSelectionMatches is null) return report with { ReasonCode = "profile_state_unavailable" };
        if (sample.ProfileSha256 is null) return report with { ReasonCode = "profile_file_unavailable" };
        if (report.ProfileFileMatches is null) return report with { ReasonCode = "profile_fingerprint_unavailable" };
        return report.LogicalCurveMatches == true ? report with { Status = "matched", ReasonCode = "calibration_matches" }
            : report with { ReasonCode = sample.CurveStatus };
    }

    private CalibrationVerificationReport CapturedReport(GammaCalibrationSession session, CalibrationCaptureEvidence capture, string reason) =>
        Inconclusive(session, reason) with
        {
            CapturedAt = capture.Report.CapturedAt,
            IsRemoteSession = capture.Report.IsRemoteSession,
            WindowsCalibrationLoaderEnabled = _readLoader()
        };

    private static bool IsSavedCalibration(GammaCalibrationSession session) =>
        session.Status == "confirmed" && !session.IsBindingRestore && session.UndoOf is null && session.InstalledProfile is not null;

    private static string? SavedProfileDevice(GammaCalibrationSession session, IccAssociationBackup? association) =>
        session.Original.ProfileDeviceSha256 ?? WindowsDisplayService.HashProfileDeviceKey(association?.DeviceKey);

    // A stopped worker can leave a temporary curve or ICC selected after its lock is released.
    private bool HasInterruptedPreview(IReadOnlyList<GammaCalibrationSession> sessions) =>
        _recovery.List(sessions).Any(item => item.Kind != "binding" && item.Status != "prepared");

    private static CalibrationVerificationReport Inconclusive(GammaCalibrationSession session, string reason) =>
        new(session.Id, session.Status, "inconclusive", reason) { SavedProfile = session.InstalledProfile };

    private IccAssociationBackup? ReadSavedAssociation(GammaCalibrationSession session)
    {
        try { return _readSavedAssociation(session); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { return null; }
    }
}
