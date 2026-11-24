using DccwPlus.Core.Calibration;
using DccwPlus.Core.Storage;

namespace DccwPlus.Windows;

public sealed record InterruptedPreviewResult(Guid SessionId, string Kind, string Status, string ReasonCode, bool NeedsRecovery);
public sealed record PreviewRecoveryReport(int SchemaVersion, string Reason, IReadOnlyList<InterruptedPreviewResult> Sessions);

public sealed class PreviewRecoveryService(string root)
{
    private readonly GammaCalibrationStore _gamma = new(root);
    private readonly ProfilePreviewStore _profiles = new(root);

    public IReadOnlyList<InterruptedPreviewResult> List()
    {
        var sessions = _gamma.Sessions();
        return sessions.Where(session => session.NeedsRecovery).Select(Summarize)
            .Concat(_profiles.Incomplete().Select(Summarize))
            .Concat(sessions.Where(session => session.NeedsBindingCompletion).Select(SummarizeBinding)).ToArray();
    }

    public PreviewRecoveryReport Recover()
    {
        using var gate = DisplayMutationLock.TryAcquire();
        return gate is null ? new(1, "preview_busy", []) : RecoverLocked();
    }

    // The caller holds the display mutation lock on this thread.
    internal PreviewRecoveryReport RecoverLocked() =>
        RecoverLocked(new WindowsGammaCalibrationBackend(root), new WindowsProfileAssociationBackend(), new CalibrationCompletionService(root));

    internal PreviewRecoveryReport RecoverLocked(IGammaCalibrationBackend gammaBackend, IProfileAssociationBackend profileBackend,
        CalibrationCompletionService? completion = null)
    {
        var gamma = _gamma.Sessions();
        var profiles = _profiles.Incomplete();
        List<InterruptedPreviewResult> results = [];
        foreach (var session in gamma.Where(session => session.NeedsRecovery))
            results.Add(Summarize(new GammaCalibrationTransaction(gammaBackend, _gamma).Recover(session)));
        foreach (var session in profiles)
            results.Add(Summarize(new ProfilePreviewTransaction(profileBackend, _profiles).Recover(session)));
        bool interrupted = results.Any(result => result.NeedsRecovery);
        if (completion is not null)
            foreach (var session in gamma.Where(session => session.NeedsBindingCompletion))
                results.Add(SummarizeBinding(interrupted ? session : completion.CompleteLocked(session, replay: true)));
        string reason = results.Count == 0 ? "no_recovery_needed"
            : results.Any(result => result.NeedsRecovery) ? "recovery_required" : "recovery_finished";
        return new(1, reason, results);
    }

    internal ProfilePreviewSession RecoverProfile(Guid id)
    {
        using var gate = DisplayMutationLock.Acquire();
        return new ProfilePreviewTransaction(new WindowsProfileAssociationBackend(), _profiles).Recover(_profiles.Load(id));
    }

    private static InterruptedPreviewResult Summarize(GammaCalibrationSession session) =>
        new(session.Id, "calibration", session.Status, session.ReasonCode, session.NeedsRecovery);
    private static InterruptedPreviewResult Summarize(ProfilePreviewSession session) =>
        new(session.Id, "profile", session.Status, session.ReasonCode, session.NeedsRecovery);

    private static InterruptedPreviewResult SummarizeBinding(GammaCalibrationSession session) =>
        new(session.Id, "binding", session.BindingStatus!, session.BindingReasonCode ?? "binding_" + session.BindingStatus, session.NeedsBindingCompletion);
}
