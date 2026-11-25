using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Localization;

namespace DccwPlus.Windows;

internal sealed record DisplayCalibrationEvidence(DisplayInfo Display, DisplayCalibrationCapture Sample, GammaRamp? LogicalRamp);
internal sealed record CalibrationCaptureEvidence(CalibrationCaptureReport Report, IReadOnlyList<DisplayCalibrationEvidence> Displays,
    IReadOnlyList<DisplayInfo> Connected);

public sealed class CalibrationCapture
{
    private readonly Func<DisplayProbe> _probe;
    private readonly Func<string, GammaRamp> _readRamp;
    private readonly Func<string, byte[]> _readProfile;
    private readonly Func<bool> _isRemoteSession;

    public CalibrationCapture() : this(new WindowsDisplayService().Probe,
        WindowsGammaCalibrationBackend.ReadRamp, InstalledProfileReader.ReadBytes, WindowsDisplaySession.IsRemote) { }

    internal CalibrationCapture(Func<DisplayProbe> probe, Func<string, GammaRamp> readRamp, Func<string, byte[]> readProfile,
        Func<bool>? isRemoteSession = null)
    {
        _probe = probe;
        _readRamp = readRamp;
        _readProfile = readProfile;
        _isRemoteSession = isRemoteSession ?? (() => false);
    }

    public CalibrationCaptureReport Read(string? displayId = null) => ReadEvidence(displayId).Report;

    internal CalibrationCaptureEvidence ReadEvidence(string? displayId = null)
    {
        bool remote = _isRemoteSession();
        var before = _probe();
        var displays = before.Displays.OrderBy(display => display.Id, StringComparer.Ordinal).ToArray();
        var selected = Select(displays, displayId);
        if (displayId is not null && selected.Length == 0)
            throw new KeyNotFoundException("The requested display is not connected.");

        var evidence = selected.Select(display => ReadDisplay(display, displays, remote)).ToArray();
        var samples = evidence.Select(item => item.Sample).ToArray();
        var after = _probe();
        var current = after.Displays.OrderBy(display => display.Id, StringComparer.Ordinal).ToArray();
        if (before.Topology != after.Topology || !displays.SequenceEqual(current)
            || !samples.SequenceEqual(Select(current, displayId).Select(display => ReadDisplay(display, current, remote).Sample))
            || _isRemoteSession() != remote)
            throw new ProfilePreviewException("capture_state_changed", new LocalizationService()["CaptureStateChanged"]);

        var report = new CalibrationCaptureReport(1, DateTimeOffset.UtcNow, Environment.OSVersion.Version.Build,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), before.Topology, samples,
            WindowsDisplayService.ComputeTopologyHash(before.Topology, displays), remote, displays.Length);
        return new(report, evidence, displays);
    }

    private static DisplayInfo[] Select(DisplayInfo[] displays, string? id) =>
        id is null ? displays : displays.Where(display => display.Id == id).ToArray();

    private DisplayCalibrationEvidence ReadDisplay(DisplayInfo display, IReadOnlyList<DisplayInfo> displays, bool remote)
    {
        string? profileHash = null;
        if (display.ProfileName is { } profile)
        {
            try { profileHash = Convert.ToHexString(SHA256.HashData(_readProfile(profile))); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
        GammaRamp? logicalRamp = null;
        string? rampHash = null;
        string? blocked = remote ? "remote_session_blocked" : WindowsGammaCalibrationBackend.SdrBlockReason(display, displays);
        string curveStatus = blocked ?? "logical_ramp_readable";
        if (blocked is null)
        {
            try
            {
                var ramp = _readRamp(display.DeviceName);
                rampHash = Convert.ToHexString(SHA256.HashData(IccCalibrationWriter.Vcgt(ramp)));
                logicalRamp = ramp;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ProfilePreviewException) { curveStatus = "curve_read_failed"; }
        }
        var sample = new DisplayCalibrationCapture(display.Id, display.Name, display.IsPrimary, display.ColorState, display.PersistenceKey,
            display.ProfileName, display.ProfileScope, display.SelectedProfileScope, display.ProfileQueryStatus,
            profileHash, curveStatus, rampHash);
        return new(display, sample, logicalRamp);
    }
}
