using System.Buffers.Binary;
using System.Security.Cryptography;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
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

    [Theory]
    [InlineData("topology", "recovery_target_changed")]
    [InlineData("source", "recovery_target_changed")]
    [InlineData("hdr", "recovery_target_changed")]
    [InlineData("effective-profile", "external_profile_change")]
    [InlineData("selected-scope", "external_profile_change")]
    [InlineData("user-default", "external_profile_change")]
    public void AssociationWritesRejectStateChangesAfterPreparation(string change, string reason)
    {
        var prepared = State with { ActiveTopologySha256 = ProfileHash };
        var current = change switch
        {
            "topology" => prepared with { ActiveTopologySha256 = OtherHash },
            "source" => prepared with { SourceId = 2 },
            "hdr" => prepared with { ExplicitSdr = false },
            "effective-profile" => prepared with { ProfileName = "another.icc" },
            _ => prepared,
        };
        int scope = change == "selected-scope" ? 1 : 0;
        string userDefault = change == "user-default" ? "new-user.icc" : "USER.ICC";

        var error = Assert.Throws<ProfilePreviewException>(() =>
            VerifyAssociationWriteState(prepared, current, 0, scope, "user.icc", userDefault));

        Assert.Equal(reason, error.Code);
    }

    [Fact]
    public void AssociationWriteRecheckAcceptsTheSameEffectiveAndDormantDefaults()
    {
        VerifyAssociationWriteState(State, State with { ProfileName = "SYSTEM.ICC" }, 0, 0, "user.icc", "USER.ICC");
        VerifyAssociationWriteState(State, State, 0, 0, null, null);
    }

    [Theory]
    [InlineData("external_curve_change")]
    [InlineData("undo_curve_changed")]
    public void AProfileSwitchCanRewriteOnlyTheKnownOriginalOrLinearCurve(string conflict)
    {
        var original = State with { Ramp = GammaRampMath.Compose(State.Ramp, new() { Gamma = 1.8 }) };
        var preset = CalibrationPreset.Create("Test", new() { RedGain = .85 });
        var candidate = GammaRampMath.Compose(original.Ramp, preset.Calibration);
        var session = new GammaCalibrationSession
        {
            Id = Guid.NewGuid(), Original = original, Candidate = candidate, LastWritten = candidate, Preset = preset,
            StartedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30)
        };
        var external = GammaRampMath.Compose(State.Ramp, new() { BlueGain = .6 });

        Assert.False(NeedsCurveWriteAfterAssociation(session, candidate, conflict));
        Assert.True(NeedsCurveWriteAfterAssociation(session, original.Ramp, conflict));
        Assert.True(NeedsCurveWriteAfterAssociation(session, GammaRamp.Identity(), conflict));
        Assert.Equal(conflict, Assert.Throws<ProfilePreviewException>(() => NeedsCurveWriteAfterAssociation(session, external, conflict)).Code);
    }

    [Fact]
    public void UndoUsesTheNewAdapterAndSourceOnlyForTheSameProfileDevice()
    {
        string identity = "edid:" + ProfileHash;
        var saved = State with { PersistenceKey = identity };
        var backup = new IccAssociationBackup(0, null, 1, Display.TargetKey!, "candidate.icc", Display.ProfileDeviceKey!,
            "system.icc", ProfileHash, CandidateHash: OtherHash);
        var active = Display with { Id = "fedcba9876543210", DeviceName = @"\\.\DISPLAY2", TargetKey = "00000000:00000002:257",
            SourceId = 4, PersistenceKey = identity, HdrActive = false, AdvancedColorActive = false };

        var (target, association) = ResolveUndoAssociation(saved, backup, [active]);

        Assert.Equal(active, target);
        Assert.Equal(backup with { TargetKey = active.TargetKey!, SourceId = 4 }, association);
        Assert.Equal("display_disconnected", Assert.Throws<ProfilePreviewException>(
            () => ResolveUndoAssociation(State, backup, [active])).Code);
    }

    [Fact]
    public void RecoveryMapsAnAssociationToTheCurrentAdapterAndSourceWithoutChangingTheBackup()
    {
        var saved = State with
        {
            PersistenceKey = "edid:" + ProfileHash,
            ProfileDeviceSha256 = WindowsDisplayService.HashProfileDeviceKey(Display.ProfileDeviceKey)
        };
        var current = saved with { DisplayId = "fedcba9876543210", TargetKey = "00000000:00000002:2", SourceId = 4 };
        var backup = new IccAssociationBackup(0, null, State.SourceId!.Value, State.TargetKey, "candidate.icc", Display.ProfileDeviceKey!);

        Assert.Equal(backup with { TargetKey = current.TargetKey, SourceId = 4 }, ResolveRecoveryAssociation(saved, current, backup));
        Assert.Equal("recovery_target_changed", Assert.Throws<ProfilePreviewException>(() => ResolveRecoveryAssociation(saved, current,
            backup with { DeviceKey = @"\Registry\Machine\System\OtherDevice" })).Code);
        Assert.Equal(State.TargetKey, backup.TargetKey);
    }

    [Fact]
    public void AssociationRecoveryRechecksDeviceUniquenessEvenForAnOldJournal()
    {
        var backup = new IccAssociationBackup(0, null, State.SourceId!.Value, State.TargetKey, "candidate.icc", Display.ProfileDeviceKey!);
        var other = Display with
        {
            Id = "fedcba9876543210", DeviceName = @"\\.\DISPLAY2", TargetKey = "00000000:00000002:2",
            SourceId = 2, ProfileDeviceKey = Display.ProfileDeviceKey!.ToLowerInvariant()
        };

        VerifyAssociationTarget(State, backup, State, [Display]);

        var error = Assert.Throws<ProfilePreviewException>(() => VerifyAssociationTarget(State, backup, State, [Display, other]));
        Assert.Equal("profile_device_unavailable", error.Code);
    }

    [Theory]
    [InlineData("matched", null)]
    [InlineData("reused-path", "display_disconnected")]
    [InlineData("legacy", "display_disconnected")]
    [InlineData("device-changed", "profile_device_unavailable")]
    [InlineData("duplicate-identity", "display_identity_ambiguous")]
    [InlineData("duplicate-device", "profile_device_unavailable")]
    public void RecoveryResolvesOnlyTheSameUniqueDisplayAndProfileDevice(string condition, string? reason)
    {
        string identity = "edid:" + ProfileHash;
        var moved = Display with { Id = "fedcba9876543210", TargetKey = "00000000:00000002:2", SourceId = 4,
            PersistenceKey = identity, ProfileDeviceKey = Display.ProfileDeviceKey!.ToLowerInvariant() };
        var other = Display with { PersistenceKey = "path:" + OtherHash, ProfileDeviceKey = @"\Registry\Machine\System\OtherDevice" };
        var connected = condition switch
        {
            "reused-path" => [other],
            "legacy" => new[] { moved },
            "device-changed" => [moved with { ProfileDeviceKey = other.ProfileDeviceKey }],
            "duplicate-identity" => [moved, other with { PersistenceKey = identity }],
            "duplicate-device" => [moved, other with { ProfileDeviceKey = Display.ProfileDeviceKey }],
            _ => [moved, other]
        };
        string? device = condition == "legacy" ? null : WindowsDisplayService.HashProfileDeviceKey(Display.ProfileDeviceKey);
        DisplayInfo Resolve() => WindowsDisplayService.ResolveRecoveryDisplay(connected, Display.Id, identity, device);

        if (reason is null) Assert.Equal(moved, Resolve());
        else Assert.Equal(reason, Assert.Throws<ProfilePreviewException>(() => Resolve()).Code);
    }

    [Theory]
    [InlineData("different-monitor", "display_disconnected")]
    [InlineData("ambiguous", "undo_target_changed")]
    [InlineData("device-key", "undo_target_changed")]
    [InlineData("shared-device-key", "undo_target_changed")]
    [InlineData("clone", "undo_target_changed")]
    [InlineData("hdr", "undo_target_changed")]
    public void UndoRejectsAnUnsafeReboundPath(string changed, string reason)
    {
        string identity = "path:" + ProfileHash;
        var saved = State with { PersistenceKey = identity };
        var backup = new IccAssociationBackup(0, null, 1, Display.TargetKey!, "candidate.icc", Display.ProfileDeviceKey!);
        var active = Display with { PersistenceKey = identity, HdrActive = false, AdvancedColorActive = false };
        var other = active with { Id = "fedcba9876543210", DeviceName = @"\\.\DISPLAY2", SourceId = 4 };
        DisplayInfo[] connected = changed switch
        {
            "different-monitor" => [active with { PersistenceKey = "path:" + OtherHash }],
            "ambiguous" => [active, other],
            "device-key" => [active with { ProfileDeviceKey = null }],
            "shared-device-key" => [active, other with { PersistenceKey = "path:" + OtherHash }],
            "clone" => [active with { Topology = "clone" }],
            _ => [active with { ColorState = "hdr", HdrActive = true }]
        };

        Assert.Equal(reason, Assert.Throws<ProfilePreviewException>(() => ResolveUndoAssociation(saved, backup, connected)).Code);
    }

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
        var other = Display with
        {
            Id = "fedcba9876543210", DeviceName = @"\\.\DISPLAY2", TargetKey = "00000000:00000002:2",
            SourceId = 2, ProfileDeviceKey = Display.ProfileDeviceKey + "2"
        };
        var (backup, profile) = PrepareIcc(Display, [Display, other], State, State.Ramp, id,
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
        var (backup, _) = PrepareIcc(Display, [Display], original, State.Ramp, Guid.Empty, name =>
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
        var error = Assert.Throws<ProfilePreviewException>(() => PrepareIcc(display, [display], State, State.Ramp, Guid.Empty,
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

    [Fact]
    public void PreparationStopsBeforeReadingScopeWhenIndependentDisplaysShareAProfileDevice()
    {
        var other = Display with
        {
            Id = "fedcba9876543210", DeviceName = @"\\.\DISPLAY2", TargetKey = "00000000:00000002:2",
            SourceId = 2, ProfileDeviceKey = Display.ProfileDeviceKey!.ToUpperInvariant()
        };
        var error = Assert.Throws<ProfilePreviewException>(() => PrepareIcc(Display, [Display, other], State, State.Ramp, Guid.Empty,
            _ => throw new InvalidOperationException("The ICC file was read."),
            _ => throw new InvalidOperationException("The default ICC was read."),
            () => throw new InvalidOperationException("The display scope was read.")));
        Assert.Equal("profile_device_unavailable", error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstalledProfileInspectionReportsMhc2InTheAssociatedProfile(bool hasMhc2)
    {
        byte[] bytes = DisplayProfile();
        if (hasMhc2)
        {
            Array.Resize(ref bytes, 148);
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(128), 1);
            "MHC2"u8.CopyTo(bytes.AsSpan(132));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(136), 144);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(140), 4);
        }

        var profile = WindowsProfileAssociationBackend.InspectProfile("panel.icc", bytes);

        Assert.True(profile.CanSelect);
        Assert.Equal(hasMhc2, profile.Mhc2Present);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), profile.Sha256);
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
