using DccwPlus.Core.Calibration;
using Xunit;
using static DccwPlus.Windows.WindowsGammaCalibrationBackend;

namespace DccwPlus.Windows.Tests;

public sealed class AssociationFingerprintTests
{
    private static readonly string ProfileHash = new('A', 64);
    private static readonly string OtherHash = new('B', 64);

    [Fact]
    public void InstalledFileMustMatchTheJournalEvenWhenTheNameMatches()
    {
        const string name = "DCCWPlus-sample.icc";
        Assert.Equal(ProfileHash, VerifyProfileFingerprint(name, ProfileHash.ToLowerInvariant(), _ => ProfileHash));
        var error = Assert.Throws<ProfilePreviewException>(() => VerifyProfileFingerprint(name, ProfileHash, _ => OtherHash));
        Assert.Equal("profile_file_changed", error.Code);
    }

    [Fact]
    public void MissingInstalledFileIsReportedAsUnreadable()
    {
        var error = Assert.Throws<ProfilePreviewException>(() => VerifyProfileFingerprint("missing.icc", ProfileHash,
            _ => throw new FileNotFoundException()));
        Assert.Equal("profile_file_unavailable", error.Code);
    }

    [Fact]
    public void RestoringAnAssociationChecksBothOriginalFilesWhenTheyDiffer()
    {
        var backup = new IccAssociationBackup(0, "user.icc", 1, "test-target", "candidate.icc", "test-device",
            "system.icc", ProfileHash, OtherHash, ProfileHash);
        List<string> reads = [];
        var error = Assert.Throws<ProfilePreviewException>(() => VerifyOriginalFingerprints(backup, name =>
        {
            reads.Add(name);
            return ProfileHash;
        }));
        Assert.Equal("profile_file_changed", error.Code);
        Assert.Equal(new[] { "system.icc", "user.icc" }, reads);
    }

    [Fact]
    public void AJournalWithoutOldFingerprintsStillUsesTheLegacyRecoveryPath()
    {
        var backup = new IccAssociationBackup(1, "user.icc", 1, "test-target", "candidate.icc", "test-device",
            "user.icc");
        VerifyOriginalFingerprints(backup, _ => throw new InvalidOperationException("Legacy records have no digest to verify."));
    }

    [Theory]
    [InlineData(0, "dormant.icc")]
    [InlineData(1, null)]
    public void RestoringASystemDefaultRequiresThePreviousSelection(int scope, string? userDefault)
    {
        var backup = new IccAssociationBackup(scope, userDefault, 1, "test-target", "candidate.icc", "test-device",
            "system.icc");
        Assert.True(PreviousSystemDefaultMatches(backup, backup.EffectiveDefault, () => "SYSTEM.ICC"));
        Assert.False(PreviousSystemDefaultMatches(backup, backup.EffectiveDefault, () => "new-default.icc"));
    }

    [Fact]
    public void RestoringAnExplicitUserDefaultDoesNotDependOnTheSystemDefault()
    {
        var backup = new IccAssociationBackup(1, "user.icc", 1, "test-target", "candidate.icc", "test-device", "user.icc");
        Assert.True(PreviousSystemDefaultMatches(backup, backup.EffectiveDefault,
            () => throw new InvalidOperationException("An explicit per-user profile does not fall back to system-wide.")));
    }
}
