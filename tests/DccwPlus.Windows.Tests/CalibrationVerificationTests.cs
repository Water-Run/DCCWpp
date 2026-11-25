using System.Security.Cryptography;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;
using DccwPlus.Windows;
using Xunit;

namespace DccwPlus.Windows.Tests;

[Collection("Desktop mutation lock")]
public sealed class CalibrationVerificationTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dccwpp-inspection-");
    private static readonly byte[] IccBytes = [1, 2, 3];
    private static readonly string IccHash = Convert.ToHexString(SHA256.HashData(IccBytes));
    private static readonly string Identity = "path:" + new string('F', 64);
    private const string DeviceKey = @"\Registry\Machine\System\VerificationFixture";

    [Fact]
    public void VerifiesAUniquePersistentDisplayWhenItsActivePathChanges()
    {
        var saved = SavedWithProfileDevice();
        var display = Connected(saved);
        var result = Verify(saved, [display], saved.Candidate);

        Assert.Equal("matched", result.Status);
        Assert.Equal(display.Id, result.DisplayId);
        Assert.NotEqual(saved.Original.DisplayId, result.DisplayId);
        Assert.True(result.ProfileSelectionMatches);
        Assert.True(result.ProfileFileMatches);
        Assert.True(result.LogicalCurveMatches);
        Assert.False(result.WindowsCalibrationLoaderEnabled);
        Assert.False(result.ColorAccuracyVerified);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("shared")]
    public void ARecordedProfileDeviceMustMatchUniquelyBeforeComparingTheCalibration(string change)
    {
        var saved = SavedWithProfileDevice();
        var display = Connected(saved);
        DisplayInfo[] displays = change switch
        {
            "changed" => [display with { ProfileDeviceKey = DeviceKey + "-changed" }],
            "missing" => [display with { ProfileDeviceKey = null }],
            _ => [display, display with { Id = "0000000000000001", PersistenceKey = "path:" + new string('B', 64),
                DeviceName = "Display3", SourceId = 3, ProfileDeviceKey = DeviceKey.ToUpperInvariant() }]
        };

        var result = Verify(saved, displays, saved.Candidate);

        Assert.Equal("inconclusive", result.Status);
        Assert.Equal("profile_device_unavailable", result.ReasonCode);
        Assert.Null(result.DisplayId);
        Assert.Null(result.ProfileSelectionMatches);
        Assert.Null(result.ProfileFileMatches);
        Assert.Null(result.LogicalCurveMatches);
    }

    [Fact]
    public void AnOldJournalUsesItsAssociationBackupToIdentifyTheProfileDevice()
    {
        var saved = Saved();
        var display = Connected(saved);
        Assert.Equal("matched", Verify(saved, [display], saved.Candidate, backedUpDevice: DeviceKey).Status);

        var replacement = display with { ProfileDeviceKey = DeviceKey + "-replacement" };
        var result = Verify(saved, [replacement], saved.Candidate, backedUpDevice: DeviceKey);
        Assert.Equal("inconclusive", result.Status);
        Assert.Equal("profile_device_unavailable", result.ReasonCode);
        Assert.Null(result.LogicalCurveMatches);
    }

    [Theory]
    [InlineData("profile_selection_changed")]
    [InlineData("profile_file_changed")]
    [InlineData("logical_curve_changed")]
    public void DistinguishesChangedAssociationFileAndLogicalCurve(string reason)
    {
        var saved = Saved();
        var display = Connected(saved);
        if (reason == "profile_selection_changed") display = display with { SelectedProfileScope = "system-wide" };
        string expectedHash = reason == "profile_file_changed" ? new string('A', 64) : IccHash;
        var ramp = reason == "logical_curve_changed" ? GammaRamp.Identity() : saved.Candidate;

        var result = Verify(saved, [display], ramp, savedHash: expectedHash);

        Assert.Equal("changed", result.Status);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Equal(reason != "profile_selection_changed", result.ProfileSelectionMatches);
        Assert.Equal(reason != "logical_curve_changed", result.LogicalCurveMatches);
        Assert.Equal(reason == "profile_selection_changed" ? null : reason != "profile_file_changed", result.ProfileFileMatches);
    }

    [Fact]
    public void RemoteProfileEvidenceDoesNotProveThePhysicalCurve()
    {
        var saved = Saved();
        var result = Verify(saved, [Connected(saved)], null, remote: true);
        Assert.Equal("inconclusive", result.Status);
        Assert.Equal("remote_session_blocked", result.ReasonCode);
        Assert.True(result.ProfileSelectionMatches);
        Assert.True(result.ProfileFileMatches);
        Assert.Null(result.LogicalCurveMatches);
    }

    [Fact]
    public void MissingEvidenceAndAmbiguousDisplaysRemainInconclusive()
    {
        var saved = Saved();
        var display = Connected(saved);
        Assert.Equal("profile_fingerprint_unavailable", Verify(saved, [display], saved.Candidate, savedHash: "").ReasonCode);
        Assert.Equal("display_disconnected", Verify(saved, [], null).ReasonCode);
        Assert.Equal("display_identity_ambiguous", Verify(saved, [display, display with { Id = saved.Original.DisplayId }],
            saved.Candidate).ReasonCode);
        Assert.Equal("sdr_path_blocked", Verify(saved, [display with { ColorState = "hdr", HdrActive = true }], null).ReasonCode);
    }

    [Fact]
    public void InspectionPrefersTheSelectedIccAndSkipsCurveReadsOnOtherDisplays()
    {
        var selected = SavedWithProfileDevice();
        var display = Connected(selected);
        var newer = SavedWithProfileDevice() with { InstalledProfile = "Newer.icc", StartedAt = selected.StartedAt.AddSeconds(1) };
        var other = display with { Id = "0000000000000001", PersistenceKey = null, ProfileDeviceKey = null, DeviceName = "Display3" };
        var store = new GammaCalibrationStore(_root.FullName);
        store.Save(selected);
        store.Save(newer);
        var capture = new CalibrationCapture(() => new("extend", [display, other]), name =>
        {
            Assert.Equal(display.DeviceName, name);
            return selected.Candidate;
        }, _ => IccBytes);

        var inspection = Service(capture).InspectDisplay(display.Id);

        Assert.Equal("calibration_matches", inspection.ReasonCode);
        Assert.Equal(selected.Preset.Name, inspection.PresetName);
        var result = Assert.IsType<CalibrationVerificationReport>(inspection.Verification);
        Assert.Equal(selected.Id, result.CalibrationId);
        Assert.Equal(display.Id, result.DisplayId);
        Assert.Equal(result.CapturedAt, inspection.CapturedAt);
        Assert.False(result.ColorAccuracyVerified);
    }

    [Fact]
    public void InspectionReportsAChangedProfileAgainstTheNewestSaveForTheSameDevice()
    {
        var old = SavedWithProfileDevice();
        var newest = SavedWithProfileDevice() with { InstalledProfile = "Newer.icc", StartedAt = old.StartedAt.AddSeconds(1) };
        var wrongDevice = SavedWithProfileDevice() with { InstalledProfile = "External.icc", StartedAt = old.StartedAt.AddSeconds(2) };
        wrongDevice = wrongDevice with { Original = wrongDevice.Original with { ProfileDeviceSha256 = new string('B', 64) } };
        var display = Connected(old) with { ProfileName = wrongDevice.InstalledProfile };
        var store = new GammaCalibrationStore(_root.FullName);
        store.Save(old);
        store.Save(newest);
        store.Save(wrongDevice);

        var inspection = Service(new(() => new("extend", [display]), _ => old.Candidate, _ => IccBytes)).InspectDisplay(display.Id);

        Assert.Equal("profile_selection_changed", inspection.ReasonCode);
        Assert.Equal(newest.Id, inspection.Verification?.CalibrationId);
        Assert.Equal("changed", inspection.Verification?.Status);
        Assert.Equal(display.ProfileName, inspection.Verification?.CurrentProfile);
    }

    [Theory]
    [InlineData("display_identity_ambiguous")]
    [InlineData("profile_device_unavailable")]
    [InlineData("capture_state_changed")]
    public void InspectionDoesNotAttributeSavedCalibrationWhenItCannotBindTheDisplay(string reason)
    {
        var saved = SavedWithProfileDevice();
        var display = Connected(saved);
        var other = display with { Id = "0000000000000001", DeviceName = "Display3" };
        DisplayInfo[] displays = reason switch
        {
            "display_identity_ambiguous" => [display, other with { ProfileDeviceKey = DeviceKey + "-other" }],
            "profile_device_unavailable" => [display, other with { PersistenceKey = null }],
            _ => [display]
        };
        new GammaCalibrationStore(_root.FullName).Save(saved);
        var expected = reason == "capture_state_changed" ? display with { ProfileDeviceKey = DeviceKey + "-stale" } : display;

        var inspection = Service(new(() => new("extend", displays), _ => saved.Candidate, _ => IccBytes)).InspectDisplay(display.Id, expected);

        Assert.Equal(reason, inspection.ReasonCode);
        Assert.Null(inspection.PresetName);
        Assert.Null(inspection.Verification);
    }

    [Fact]
    public void InspectionSkipsUndoAndRestoreJournalsAndStopsBeforeAnInterruptedPreview()
    {
        var saved = SavedWithProfileDevice();
        var display = Connected(saved);
        var store = new GammaCalibrationStore(_root.FullName);
        store.Save(SavedWithProfileDevice() with { UndoOf = saved.Id });
        store.Save(SavedWithProfileDevice() with { IsBindingRestore = true });
        var verifier = Service(new(() => new("extend", [display]), _ => saved.Candidate, _ => IccBytes));

        var noSave = verifier.InspectDisplay(display.Id);
        Assert.Equal("no_saved_calibration", noSave.ReasonCode);
        Assert.Null(noSave.Verification);

        store.Save(saved with { Status = "previewing" });
        var interruption = verifier.InspectDisplay(display.Id);
        Assert.Equal("recovery_required", interruption.ReasonCode);
        Assert.Null(interruption.CapturedAt);
        Assert.Null(interruption.Verification);
        Assert.Equal("previewing", store.Load(saved.Id).Status);
    }

    [Fact]
    public void AJournalWithoutADeviceKeyNeedsItsOriginalPathForDisplayInspection()
    {
        var saved = Saved();
        var display = Connected(saved);
        new GammaCalibrationStore(_root.FullName).Save(saved);
        var capture = new CalibrationCapture(() => new("extend", [display]), _ => saved.Candidate, _ => IccBytes);
        var verifier = new CalibrationVerification(_root.FullName, capture.ReadEvidence, _ => throw new FileNotFoundException(), () => false);

        Assert.Equal("profile_device_unavailable", verifier.InspectDisplay(display.Id).ReasonCode);
        display = display with { Id = saved.Original.DisplayId, TargetKey = saved.Original.TargetKey,
            DeviceName = saved.Original.DeviceName, SourceId = saved.Original.SourceId };
        var originalPath = verifier.InspectDisplay(display.Id);
        Assert.Equal("profile_fingerprint_unavailable", originalPath.ReasonCode);
        Assert.Equal(saved.Id, originalPath.Verification?.CalibrationId);
    }

    private CalibrationVerification Service(CalibrationCapture capture) => new(_root.FullName, capture.ReadEvidence,
        session => new(0, null, session.Original.SourceId ?? 0, session.Original.TargetKey, session.InstalledProfile!, DeviceKey,
            CandidateHash: IccHash), () => false);

    public void Dispose() => _root.Delete(recursive: true);

    private static CalibrationVerificationReport Verify(GammaCalibrationSession saved, DisplayInfo[] displays,
        GammaRamp? ramp, bool remote = false, string? savedHash = null, string? backedUpDevice = null)
    {
        var capture = new CalibrationCapture(() => new("extend", displays),
            _ => ramp ?? throw new ProfilePreviewException("curve_read_failed", "No fixture curve."),
            _ => IccBytes, () => remote);
        return new CalibrationVerification(Path.GetTempPath(), capture.ReadEvidence,
            _ => new(0, null, saved.Original.SourceId ?? 0, saved.Original.TargetKey, saved.InstalledProfile!, backedUpDevice ?? "",
                CandidateHash: savedHash ?? IccHash), () => false).Evaluate(saved);
    }

    private static GammaCalibrationSession Saved()
    {
        var original = new GammaDeviceState("0123456789abcdef", "Display1", "00000000:00000001:1", new string('A', 64),
            true, false, GammaRamp.Identity(), PersistenceKey: Identity, SourceId: 1);
        var preset = CalibrationPreset.Create("Fixture", new() { RedGain = .93 });
        var ramp = GammaRampMath.Compose(original.Ramp, preset.Calibration);
        return new()
        {
            Id = Guid.NewGuid(), Original = original, Preset = preset, Candidate = ramp, LastWritten = ramp,
            Status = "confirmed", InstalledProfile = "Saved.icc", StartedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow
        };
    }

    private static GammaCalibrationSession SavedWithProfileDevice()
    {
        var saved = Saved();
        return saved with { Original = saved.Original with { ProfileDeviceSha256 = WindowsDisplayService.HashProfileDeviceKey(DeviceKey) } };
    }

    private static DisplayInfo Connected(GammaCalibrationSession saved) => new("fedcba9876543210", "Panel", "Display2", true, "sdr",
        Topology: "extend", TargetKey: "00000000:00000002:2", SourceId: 2, PersistenceKey: saved.Original.PersistenceKey,
        ProfileDeviceKey: DeviceKey,
        ProfileName: saved.InstalledProfile, ProfileScope: "current-user", SelectedProfileScope: "current-user", ProfileQueryStatus: "read",
        HdrActive: false, AdvancedColorActive: false);
}
