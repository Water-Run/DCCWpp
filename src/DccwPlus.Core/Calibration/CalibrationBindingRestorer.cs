using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;

namespace DccwPlus.Core.Calibration;

public sealed record BindingRestoreResult(string PersistenceKey, string? DisplayId, string Status, string ReasonCode);

public sealed class CalibrationBindingRestorer(IDisplayService displays, IGammaCalibrationBackend backend,
    IGammaCalibrationJournal journal, Func<string, string> hashInstalledProfile, Func<Guid, CalibrationPreset> readPreset)
{
    public BindingRestoreResult Restore(CalibrationBinding binding, bool dryRun)
    {
        string? displayId = null;
        BindingRestoreResult Result(string status, string reason) => new(binding.PersistenceKey, displayId, status, reason);
        if (!binding.RestoreEnabled) return Result("skipped", "binding_disabled");
        try
        {
            CalibrationBindingStore.Validate(binding);
            var connected = displays.Enumerate();
            var matches = connected.Where(d => d.PersistenceKey == binding.PersistenceKey).ToArray();
            if (matches.Length == 0) return Result("skipped", "display_disconnected");
            if (matches.Length != 1) return Result("skipped", "display_identity_ambiguous");

            var display = matches[0];
            displayId = display.Id;
            if (!CalibrationService.IsExplicitSdr(display)) return Result("skipped", "sdr_path_blocked");
            if (display.Topology == "clone" || connected.Count(d => d.DeviceName == display.DeviceName) > 1)
                return Result("skipped", "shared_source_blocked");
            var state = backend.Read(display.Id);
            if (!state.ExplicitSdr || state.SharedSource || state.PersistenceKey != binding.PersistenceKey
                || state.SourceId != display.SourceId || state.TargetKey != display.TargetKey)
                return Result("skipped", "display_state_changed");
            var checkedBackend = new SelectedProfileBackend(backend, binding, hashInstalledProfile);
            checkedBackend.VerifyProfile(state);
            if (state.Ramp.Matches(binding.Ramp, 257)) return Result("unchanged", "already_active");
            // The known original curve and the linear reset curve are the only eligible baselines.
            if (!state.Ramp.Matches(GammaRamp.Identity(), 257) && binding.OriginalRamp?.Matches(state.Ramp, 257) != true)
                return Result("skipped", "external_curve_change");
            var preset = readPreset(binding.PresetId);
            if (dryRun) return Result("planned", "restore_ready");

            var transaction = new GammaCalibrationTransaction(checkedBackend, journal);
            transaction.Begin(Guid.NewGuid(), preset, display.Id, 30, binding.Ramp, state);
            var result = transaction.Confirm();
            return Result(result.Status == "confirmed" ? "restored" : result.Status, result.ReasonCode);
        }
        catch (ProfilePreviewException ex)
        {
            return Result(ex.Code is "profile_selection_changed" or "profile_file_changed" ? "skipped" : "failed", ex.Code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidDataException)
        {
            return Result("failed", "restore_data_unavailable");
        }
    }

    private sealed class SelectedProfileBackend(IGammaCalibrationBackend backend, CalibrationBinding binding,
        Func<string, string> hashInstalledProfile) : IGammaCalibrationBackend
    {
        public GammaDeviceState Read(string displayId) => backend.Read(displayId);

        public void VerifyProfile(GammaDeviceState state)
        {
            if (state.ProfileScope != "current-user" || !ProfilePreviewPolicy.EqualName(state.ProfileName, binding.ProfileName))
                throw new ProfilePreviewException("profile_selection_changed", "The confirmed profile is no longer selected for this user.");
            if (!string.Equals(hashInstalledProfile(binding.ProfileName), binding.ProfileSha256, StringComparison.OrdinalIgnoreCase))
                throw new ProfilePreviewException("profile_file_changed", "The confirmed profile file has changed.");
        }

        public void Write(GammaDeviceState expected, GammaRamp ramp)
        {
            // Rollback must remain possible if the selected file disappeared after the write.
            if (ramp.Matches(binding.Ramp)) VerifyProfile(expected);
            backend.Write(expected, ramp);
        }

        public string Commit(GammaCalibrationSession session)
        {
            VerifyProfile(backend.Read(session.Original.DisplayId));
            return binding.ProfileName;
        }

        public void RestoreProfile(GammaCalibrationSession session) { }
    }
}
