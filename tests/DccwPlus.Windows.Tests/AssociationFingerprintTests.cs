using System.Buffers.Binary;
using System.Security.Cryptography;
using DccwPlus.Core.Calibration;
using Xunit;
using static DccwPlus.Windows.WindowsGammaCalibrationBackend;

namespace DccwPlus.Windows.Tests;

public sealed class AssociationFingerprintTests
{
    private static readonly string ProfileHash = new('A', 64);
    private static readonly string OtherHash = new('B', 64);
    private static readonly DisplayInfo Display = new("0123456789abcdef", "Monitor", @"\\.\DISPLAY1", true, "sdr",
        TargetKey: "00000000:00000001:256", SourceId: 1, ProfileDeviceKey: @"\Registry\Machine\System\DisplayFixture");
    private static readonly GammaDeviceState State = new(Display.Id, Display.DeviceName, Display.TargetKey!,
        ProfileHash, true, false, GammaRamp.Identity(), "system.icc", "system-wide", Display.SourceId,
        SelectedProfileScope: "system-wide");

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

    [Fact]
    public void PreparationChecksTheDormantUserProfileAndBuildsTheExactInstallPayload()
    {
        byte[] systemProfile = DisplayProfile();
        byte[] userProfile = DisplayProfile();
        userProfile[8] = 4;
        Guid id = Guid.NewGuid();
        var (backup, profile) = PrepareIcc(Display, State, State.Ramp, id,
            name => name == "system.icc" ? systemProfile : userProfile,
            scope => scope == 0 ? "system.icc" : "user.icc", () => 0);

        Assert.Equal($"DCCWPlus-{id:N}.icc", backup.CandidateName);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(systemProfile)), backup.EffectiveDefaultHash);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(userProfile)), backup.UserDefaultHash);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(profile)), backup.CandidateHash);
        Assert.True(IccProfileInspector.Inspect(backup.CandidateName, profile).VcgtPresent);
    }

    [Fact]
    public void PreparationPreservesAbsenceOfTheOriginalAssociation()
    {
        var original = State with { ProfileName = null, ProfileScope = null };
        var (backup, _) = PrepareIcc(Display, original, State.Ramp, Guid.Empty, name =>
        {
            Assert.Equal("sRGB Color Space Profile.icm", name);
            return DisplayProfile();
        }, _ => null, () => 0);
        Assert.Null(backup.EffectiveDefault);
        Assert.Null(backup.EffectiveDefaultHash);
        Assert.Null(backup.UserDefault);
    }

    [Theory]
    [InlineData("device", "profile_device_unavailable")]
    [InlineData("scope", "external_profile_change")]
    [InlineData("selection", "external_profile_change")]
    [InlineData("file", "profile_file_unavailable")]
    [InlineData("format", "icc_base_unsupported")]
    public void PreparationReportsThePreexistingSaveBlock(string change, string reason)
    {
        var display = change == "device" ? Display with { ProfileDeviceKey = null } : Display;
        int scopeReads = 0;
        var error = Assert.Throws<ProfilePreviewException>(() => PrepareIcc(display, State, State.Ramp, Guid.Empty,
            name => (change, name) switch
            {
                ("file", "user.icc") => throw new FileNotFoundException(),
                ("format", "system.icc") => new byte[132],
                _ => DisplayProfile()
            },
            scope => scope == 1 ? "user.icc" : change == "selection" ? "different.icc" : "system.icc",
            () => change == "scope" && ++scopeReads == 2 ? 1 : 0));
        Assert.Equal(reason, error.Code);
    }

    private static byte[] DisplayProfile()
    {
        var data = new byte[132];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)data.Length);
        data[8] = 2;
        "mntrRGB XYZ "u8.CopyTo(data.AsSpan(12));
        "acsp"u8.CopyTo(data.AsSpan(36));
        return data;
    }
}
