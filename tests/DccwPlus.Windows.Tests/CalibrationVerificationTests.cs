using System.Security.Cryptography;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
using DccwPlus.Windows;
using Xunit;

namespace DccwPlus.Windows.Tests;

public sealed class CalibrationVerificationTests
{
    private static readonly byte[] IccBytes = [1, 2, 3];
    private static readonly string IccHash = Convert.ToHexString(SHA256.HashData(IccBytes));
    private static readonly string Identity = "path:" + new string('F', 64);

    [Fact]
    public void VerifiesAUniquePersistentDisplayWhenItsActivePathChanges()
    {
        var saved = Saved();
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

    private static CalibrationVerificationReport Verify(GammaCalibrationSession saved, DisplayInfo[] displays,
        GammaRamp? ramp, bool remote = false, string? savedHash = null)
    {
        var capture = new CalibrationCapture(() => new("extend", displays),
            _ => ramp ?? throw new ProfilePreviewException("curve_read_failed", "No fixture curve."),
            _ => IccBytes, () => remote);
        return new CalibrationVerification(Path.GetTempPath(), () => capture.ReadEvidence(), _ => savedHash ?? IccHash, () => false)
            .Evaluate(saved);
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

    private static DisplayInfo Connected(GammaCalibrationSession saved) => new("fedcba9876543210", "Panel", "Display2", true, "sdr",
        Topology: "extend", TargetKey: "00000000:00000002:2", SourceId: 2, PersistenceKey: saved.Original.PersistenceKey,
        ProfileName: saved.InstalledProfile, ProfileScope: "current-user", SelectedProfileScope: "current-user", ProfileQueryStatus: "read",
        HdrActive: false, AdvancedColorActive: false);
}
