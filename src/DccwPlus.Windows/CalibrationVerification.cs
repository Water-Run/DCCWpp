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

public sealed class CalibrationVerification
{
    private readonly GammaCalibrationStore _sessions;
    private readonly Func<CalibrationCaptureEvidence> _capture;
    private readonly Func<GammaCalibrationSession, string?> _readSavedHash;
    private readonly Func<bool?> _readLoader;

    public CalibrationVerification(string root) : this(root, () => new CalibrationCapture().ReadEvidence(),
        new WindowsGammaCalibrationBackend(root).ReadSavedProfileFingerprint, WindowsGammaCalibrationBackend.ReadCalibrationLoader) { }

    internal CalibrationVerification(string root, Func<CalibrationCaptureEvidence> capture,
        Func<GammaCalibrationSession, string?> readSavedHash, Func<bool?> readLoader)
    {
        _sessions = new(root);
        _capture = capture;
        _readSavedHash = readSavedHash;
        _readLoader = readLoader;
    }

    public CalibrationVerificationReport Read(Guid id)
    {
        using var gate = DisplayMutationLock.Acquire();
        return Evaluate(_sessions.Load(id));
    }

    internal CalibrationVerificationReport Evaluate(GammaCalibrationSession session)
    {
        var report = new CalibrationVerificationReport(session.Id, session.Status, "inconclusive", "calibration_not_confirmed")
        {
            SavedProfile = session.InstalledProfile
        };
        if (session.Status != "confirmed" || session.IsBindingRestore || session.UndoOf is not null || session.InstalledProfile is null)
            return report;

        var capture = _capture();
        report = report with { CapturedAt = capture.Report.CapturedAt, IsRemoteSession = capture.Report.IsRemoteSession,
            WindowsCalibrationLoaderEnabled = _readLoader() };

        var matches = capture.Displays.Where(item => MonitorIdentity.MatchesSavedDisplay(session.Original, item.Display)).ToArray();
        if (matches.Length != 1)
            return report with { ReasonCode = matches.Length > 1 ? "display_identity_ambiguous"
                : report.IsRemoteSession == true ? "remote_session_blocked" : "display_disconnected" };

        var evidence = matches[0];
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
            if (selected && sample.ProfileSha256 is not null && ReadSavedHash(session) is { } savedHash)
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

    private string? ReadSavedHash(GammaCalibrationSession session)
    {
        try
        {
            string? hash = _readSavedHash(session);
            return ProfilePreviewPolicy.IsHash(hash) ? hash : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { return null; }
    }
}
