using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Localization;

namespace DccwPlus.Windows;

public sealed class CalibrationCapture
{
    private readonly Func<DisplayProbe> _probe;
    private readonly Func<string, GammaRamp> _readRamp;
    private readonly Func<string, byte[]> _readProfile;

    public CalibrationCapture() : this(new WindowsDisplayService().Probe,
        WindowsGammaCalibrationBackend.ReadRamp, InstalledProfileReader.ReadBytes) { }

    internal CalibrationCapture(Func<DisplayProbe> probe, Func<string, GammaRamp> readRamp, Func<string, byte[]> readProfile)
    {
        _probe = probe;
        _readRamp = readRamp;
        _readProfile = readProfile;
    }

    public CalibrationCaptureReport Read(string? displayId = null)
    {
        var before = _probe();
        var displays = before.Displays.OrderBy(display => display.Id, StringComparer.Ordinal).ToArray();
        var selected = Select(displays, displayId);
        if (displayId is not null && selected.Length == 0)
            throw new KeyNotFoundException("The requested display is not connected.");

        var samples = selected.Select(display => ReadDisplay(display, displays)).ToArray();
        var after = _probe();
        var current = after.Displays.OrderBy(display => display.Id, StringComparer.Ordinal).ToArray();
        if (before.Topology != after.Topology || !displays.SequenceEqual(current)
            || !samples.SequenceEqual(Select(current, displayId).Select(display => ReadDisplay(display, current))))
            throw new ProfilePreviewException("capture_state_changed", new LocalizationService()["CaptureStateChanged"]);

        return new(1, DateTimeOffset.UtcNow, Environment.OSVersion.Version.Build,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), before.Topology, samples,
            TopologyHash(before.Topology, displays));
    }

    private static string? TopologyHash(string topology, DisplayInfo[] displays)
    {
        if (topology == "unknown" || displays.Length == 0 || displays.Any(display => display.TargetKey is null
            || display.SourceId is null || display.Width is null || display.Height is null || display.PositionX is null || display.PositionY is null))
            return null;

        // Hash the full active desktop even when the caller samples only one monitor.
        var paths = displays.Select(display => new
        {
            display.Id, display.DeviceName, display.TargetKey, display.SourceId, display.PersistenceKey, display.IsPrimary,
            display.Output, display.Width, display.Height, display.PositionX, display.PositionY, display.RefreshMillihertz, display.CloneGroup,
            display.ColorState, display.HdrActive, display.AdvancedColorActive
        });
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { topology, paths })));
    }

    private static DisplayInfo[] Select(DisplayInfo[] displays, string? id) =>
        id is null ? displays : displays.Where(display => display.Id == id).ToArray();

    private DisplayCalibrationCapture ReadDisplay(DisplayInfo display, IReadOnlyList<DisplayInfo> displays)
    {
        string? profileHash = null;
        if (display.ProfileName is { } profile)
        {
            try { profileHash = Convert.ToHexString(SHA256.HashData(_readProfile(profile))); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
        string? rampHash = null;
        string? blocked = WindowsGammaCalibrationBackend.SdrBlockReason(display, displays);
        string curveStatus = blocked ?? "logical_ramp_readable";
        if (blocked is null)
        {
            try { rampHash = Convert.ToHexString(SHA256.HashData(IccCalibrationWriter.Vcgt(_readRamp(display.DeviceName)))); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ProfilePreviewException) { curveStatus = "curve_read_failed"; }
        }
        return new(display.Id, display.Name, display.IsPrimary, display.ColorState, display.PersistenceKey,
            display.ProfileName, display.ProfileScope, display.SelectedProfileScope, display.ProfileQueryStatus,
            profileHash, curveStatus, rampHash);
    }
}
