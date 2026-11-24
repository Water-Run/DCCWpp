using System.Globalization;
using System.Text;
using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Localization;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;
using DccwPlus.Windows;

Console.OutputEncoding = new UTF8Encoding(false);
if (args is ["__profile-worker", "--session", var workerId, "--data-dir", var workerRoot])
    return Guid.TryParse(workerId, out var workerSession) ? ProfilePreviewHost.Run(workerRoot, workerSession) : 2;
if (args is ["__profile-worker", "--session", var profileId, "--data-dir", var profileRoot, "--attempt", var profileAttemptId])
    return Guid.TryParse(profileId, out var profileSession) && Guid.TryParse(profileAttemptId, out var profileAttempt)
        && profileAttempt != Guid.Empty ? ProfilePreviewHost.Run(profileRoot, profileSession, profileAttempt) : 2;
if (args is ["__gamma-worker", "--session", var gammaId, "--data-dir", var gammaRoot])
    return Guid.TryParse(gammaId, out var gammaSession) ? GammaPreviewHost.Run(gammaRoot, gammaSession) : 2;
if (args is ["__gamma-worker", "--session", var sessionId, "--data-dir", var root, "--attempt", var attemptId, .. var tail]
    && tail is [] or ["--recover"])
    return Guid.TryParse(sessionId, out var session) && Guid.TryParse(attemptId, out var attempt) && attempt != Guid.Empty
        ? GammaPreviewHost.Run(root, session, attemptId: attempt, recover: tail.Length == 1) : 2;
return await Cli.RunAsync(args);

internal static class Cli
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static async Task<int> RunAsync(string[] args)
    {
        bool json = args.Contains("--json");
        try
        {
            var remaining = args.ToList();
            remaining.RemoveAll(a => a == "--json");
            string? dataDirectory = TakeOption(remaining, "--data-dir");
            if (remaining is [] or ["--help"] or ["help"])
                return Success(CliDiscovery.Help(), json);
            if (remaining is ["capabilities"])
                return Success(CliDiscovery.Capabilities(), json);
            if (remaining is ["calibration", "compare", ..])
            {
                if (remaining is not ["calibration", "compare", var beforePath, var afterPath])
                    throw new ArgumentException(new LocalizationService()["CaptureCompareUsage"]);
                try
                {
                    var before = CalibrationCaptureCodec.Read(beforePath);
                    var after = CalibrationCaptureCodec.Read(afterPath);
                    return Success(CalibrationCaptureComparison.Compare(before, after), json);
                }
                catch (InvalidDataException)
                { return Failure("invalid_capture", new LocalizationService()["InvalidCaptureDocument"], 2, json); }
            }

            string root = dataDirectory ?? DataPaths.DefaultRoot;
            var store = new PresetStore(root);
            var displays = new WindowsDisplayService();

            switch (remaining.ToArray())
            {
                case ["status"]:
                    return Success(new
                    {
                        name = CliDiscovery.ProductName,
                        version = CliDiscovery.Version,
                        stage = "sdr-calibration",
                        calibrationAvailable = true,
                        packaged = StartupService.HasPackageIdentity(),
                        dataDirectory = Path.GetFullPath(root),
                        presetCount = store.List().Presets.Count,
                        displayCount = displays.Enumerate().Count
                    }, json);
                case ["displays", "list"]: return Success(displays.Probe().Displays, json);
                case ["displays", "snapshot"]:
                    var probe = displays.Probe();
                    var observation = DisplayObservationCodec.Capture(probe.Topology, probe.Displays);
                    new DisplayObservationStore(root).Save(observation);
                    return Success(observation, json);
                case ["displays", "snapshot", "show"]: return Success(new DisplayObservationStore(root).Load(), json);
                case ["presets", "list"]: return Success(store.List(), json);
                case ["presets", "show", var id]: return Success(store.Get(ParseId(id)), json);
                case ["presets", "import", var path]: return Success(store.Import(path).Preset, json);
                case ["presets", "import", var path, "--copy"]: return Success(store.Import(path, asCopy: true).Preset, json);
                case ["presets", "rename", var id, var name]: return Success(store.Rename(ParseId(id), name), json);
                case ["presets", "delete", var id]:
                    var deleted = ParseId(id);
                    new CalibrationBindingService(root).DeletePreset(deleted);
                    return Success(new { id = deleted, deleted = true }, json);
                case ["presets", "bindings", var id]:
                    var preset = store.Get(ParseId(id));
                    return Success(store.BindingsFor(preset.Id).Select(BindingSummary).ToArray(), json);
                case ["presets", "validate", var path]:
                    var validated = PresetCodec.Decode(PresetCodec.ReadBounded(path));
                    return Success(new { valid = true, validated.SchemaVersion, validated.Name }, json);
                case ["presets", "export", var id, var path]:
                    store.Export(ParseId(id), path); return Success(new { path = Path.GetFullPath(path) }, json);
                case ["startup", "status"]: return Success(await new StartupService().GetAsync(), json);
                case ["startup", "last-restore"]:
                    return Success(new { report = new CalibrationBindingService(root).LastStartupReport() }, json);
                case ["recovery", "status"]:
                    return Success(new { sessions = new PreviewRecoveryService(root).List() }, json);
                case ["recovery", "run"]:
                    return Success(new PreviewRecoveryService(root).Recover(), json);
                case ["bindings", "list"]:
                    return Success(new CalibrationBindingService(root).List().Select(BindingSummary).ToArray(), json);
                case ["bindings", "enable" or "disable", var key]:
                    return Success(BindingSummary(new CalibrationBindingService(root).SetEnabled(key, remaining[1] == "enable")), json);
                case ["bindings", "remove", var key]:
                    var removed = new CalibrationBindingService(root).Remove(key);
                    return Success(new { removed.PersistenceKey, removed.PresetId, removed = true }, json);
                case ["bindings", "restore"]:
                case ["bindings", "restore", "--dry-run"]:
                    return Success(new CalibrationBindingService(root).Restore(remaining.Count == 3), json);
            }

            if (remaining is ["presets", "create", ..])
            {
                var created = ParseNewPreset(remaining);
                store.Add(created);
                return Success(created, json);
            }

            if (remaining.FirstOrDefault() == "calibration")
            {
                if (remaining.ElementAtOrDefault(1) == "capture")
                {
                    string? target = TakeOption(remaining, "--display");
                    if (remaining.Count != 2)
                        throw new ArgumentException(new LocalizationService()["CaptureUsage"]);
                    return Success(new CalibrationCapture().Read(target), json);
                }
                if (remaining.ElementAtOrDefault(1) == "diagnose")
                {
                    string? displayId = TakeOption(remaining, "--display");
                    if (remaining.Count != 2 || displayId is null)
                        throw new ArgumentException("Use calibration diagnose --display <display-id>.");
                    return Success(new WindowsGammaCalibrationBackend(root).Diagnose(displayId), json);
                }
                var client = new GammaPreviewClient(root, Path.Combine(AppContext.BaseDirectory, "dccwpp.exe"));
                if (remaining is ["calibration", "preview", ..])
                {
                    string? display = TakeOption(remaining, "--display");
                    string? timeout = TakeOption(remaining, "--seconds");
                    string? request = TakeOption(remaining, "--request-id");
                    bool dryRun = remaining.Remove("--dry-run");
                    var options = ReadCalibrationOptions(remaining);
                    if (remaining.Count != 2 || string.IsNullOrWhiteSpace(display))
                        throw new ArgumentException(new LocalizationService()["CalibrationPreviewCliUsage"]);
                    Guid? requestId = request is null ? null : ParseRequestId(request);
                    var preset = CalibrationPreset.Create(new LocalizationService()["CliCalibrationPresetName"], options.ApplyTo(new()));
                    PresetCodec.Validate(preset);
                    return await PreviewAsync(client, display, preset, PreviewSeconds(timeout), requestId, dryRun, json,
                        inlineParameters: true);
                }
                if (remaining.ElementAtOrDefault(1) == "undo")
                {
                    bool dryRun = remaining.Remove("--dry-run");
                    string? timeout = TakeOption(remaining, "--seconds");
                    string? request = TakeOption(remaining, "--request-id");
                    if (remaining is not ["calibration", "undo", var sourceId])
                        throw new ArgumentException(new LocalizationService()["UndoUsage"]);
                    Guid? requestId = request is null ? null : ParseRequestId(request);
                    Guid calibrationId = ParseId(sourceId);
                    int seconds = timeout is null ? 30 : int.TryParse(timeout, NumberStyles.None, CultureInfo.InvariantCulture,
                        out int duration) ? duration : 0;
                    if (seconds is < 10 or > 60) throw new ArgumentException("Undo preview timeout must be 10–60 seconds.");
                    if (dryRun) return Success(new WindowsGammaCalibrationBackend(root).PlanUndo(calibrationId), json);
                    return Success(SessionSummary(await client.BeginUndoAsync(calibrationId, seconds, requestId: requestId)), json);
                }
                if (remaining is ["calibration", "sessions"])
                    return Success(new GammaCalibrationStore(root).Sessions().Select(SessionSummary).ToArray(), json);
                if (remaining is ["calibration", "status", var statusId]) return Success(SessionSummary(client.Status(ParseId(statusId))), json);
                if (remaining is ["calibration", "confirm" or "revert", var actionId])
                {
                    var result = await client.CommandAsync(ParseId(actionId), remaining[1]);
                    string desired = remaining[1] == "confirm" ? "confirmed" : "reverted";
                    if (result.Status != desired) return Failure(result.ReasonCode, $"Calibration session is {result.Status}.", 4, json);
                    return Success(SessionSummary(result), json);
                }
                if (remaining is ["calibration", "recover", var recoverId])
                {
                    var result = await client.RecoverAsync(ParseId(recoverId));
                    if (result.NeedsBindingCompletion)
                        return Failure(result.BindingReasonCode ?? "binding_pending", new LocalizationService()["BindingRecoveryMessage"], 4, json);
                    return result.NeedsRecovery ? Failure(result.ReasonCode, "The original calibration still needs recovery.", 4, json)
                        : Success(SessionSummary(result), json);
                }
                if (remaining is ["calibration", "update", ..])
                {
                    string? updatePreset = TakeOption(remaining, "--preset");
                    bool original = remaining.Remove("--original");
                    bool adjusted = remaining.Remove("--adjusted");
                    var options = ReadCalibrationOptions(remaining);
                    if (remaining is not ["calibration", "update", var updateId] || (original && adjusted)
                        || (updatePreset is not null && options.HasValues)
                        || (updatePreset is null && !options.HasValues && !original && !adjusted))
                        throw new ArgumentException(new LocalizationService()["CalibrationUpdateCliUsage"]);

                    Guid sessionId = ParseId(updateId);
                    if (updatePreset is not null)
                        return Success(SessionSummary(await client.CommandAsync(sessionId, "update", store.Get(ParseId(updatePreset)), original)), json);
                    if (options.HasValues)
                        PresetCodec.Validate(CalibrationPreset.Create(new LocalizationService()["CliCalibrationPresetName"], options.ApplyTo(new())));
                    return Success(SessionSummary(await client.EditAsync(sessionId, options.Edit, original)), json);
                }
                throw new ArgumentException("Run dccwpp --help for calibration commands.");
            }

            if (remaining.FirstOrDefault() == "profiles")
            {
                var client = new ProfilePreviewClient(root, Path.Combine(AppContext.BaseDirectory, "dccwpp.exe"));
                if (remaining is ["profiles", "status", var sessionId]) return Success(client.Status(ParseId(sessionId)), json);
                if (remaining is ["profiles", "confirm" or "revert", var commandId])
                    return Success(await client.CommandAsync(ParseId(commandId), remaining[1]), json);
                if (remaining is ["profiles", "recover", var recoveryId])
                    return Success(await client.RecoverAsync(ParseId(recoveryId)), json);
                string? displayId = TakeOption(remaining, "--display");
                if (remaining is ["profiles", "list"] && displayId is not null)
                    return Success(new WindowsProfileAssociationBackend().Read(displayId), json);
                string? duration = TakeOption(remaining, "--seconds");
                string? request = TakeOption(remaining, "--request-id");
                bool dryRun = remaining.Remove("--dry-run");
                if (remaining is not ["profiles", "preview", var candidate] || displayId is null)
                    throw new ArgumentException(new LocalizationService()["ProfilePreviewUsage"]);
                Guid? requestId = request is null ? null : ParseRequestId(request);
                int seconds = duration is null ? 30 : int.TryParse(duration, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : 0;
                if (seconds is < 10 or > 60) throw new ArgumentException("Preview timeout must be 10–60 seconds.");
                if (dryRun) return Success(ProfilePreviewPolicy.Plan(new WindowsProfileAssociationBackend().Read(displayId), candidate), json);
                return Success(await client.BeginAsync(displayId, candidate, seconds, requestId: requestId), json);
            }

            if (remaining.FirstOrDefault() == "displays" && remaining.ElementAtOrDefault(1) == "restore-plan")
            {
                string? displayId = TakeOption(remaining, "--display");
                if (displayId is null || remaining.Count != 2 || remaining[0] != "displays" || remaining[1] != "restore-plan")
                    throw new ArgumentException("Use displays restore-plan --display <display-id>.");
                var found = displays.Probe().Displays.FirstOrDefault(item => item.Id == displayId)
                    ?? throw new KeyNotFoundException("The requested display is not connected.");
                DisplayObservation? saved = null;
                try { saved = new DisplayObservationStore(root).Load(); }
                catch (FileNotFoundException) { saved = null; }
                return Success(RestorePlanner.Plan(found, saved), json);
            }

            if (remaining.FirstOrDefault() == "apply")
            {
                string? display = TakeOption(remaining, "--display");
                string? timeout = TakeOption(remaining, "--seconds");
                string? request = TakeOption(remaining, "--request-id");
                bool dryRun = remaining.Remove("--dry-run");
                if (remaining.Count != 2 || display is null)
                    throw new ArgumentException("Use apply <preset-id> --display <display-id> [--request-id <uuid>] [--dry-run].");
                Guid? requestId = request is null ? null : ParseRequestId(request);
                int seconds = PreviewSeconds(timeout);
                Guid presetId = ParseId(remaining[1]);
                var client = new GammaPreviewClient(root, Path.Combine(AppContext.BaseDirectory, "dccwpp.exe"));
                if (!dryRun && requestId is Guid replayId && client.FindByRequestId(replayId, display, presetId, seconds) is { } prior)
                    return Success(SessionSummary(prior), json);
                return await PreviewAsync(client, display, store.Get(presetId), seconds, requestId, dryRun, json);
            }
            throw new ArgumentException("Unknown command or option. Run dccwpp --help.");
        }
        catch (ProfilePreviewException ex) { return Failure(ex.Code, ex.Message, 4, json); }
        catch (PresetOperationException ex) { return Failure(ex.Code, ex.Message, 4, json); }
        catch (ArgumentException ex) { return Failure("invalid_arguments", ex.Message, 2, json); }
        catch (InvalidDataException ex) { return Failure("invalid_preset", ex.Message, 2, json); }
        catch (FileNotFoundException ex) { return Failure("not_found", ex.Message, 3, json); }
        catch (DirectoryNotFoundException ex) { return Failure("not_found", ex.Message, 3, json); }
        catch (KeyNotFoundException ex) { return Failure("not_found", ex.Message, 3, json); }
        catch (UnauthorizedAccessException ex) { return Failure("access_denied", ex.Message, 5, json); }
        catch (IOException ex) { return Failure("io_error", ex.Message, 5, json); }
        catch (Exception ex) { return Failure("internal_error", ex.Message, 1, json); }
    }

    private static async Task<int> PreviewAsync(GammaPreviewClient client, string displayId, CalibrationPreset preset,
        int seconds, Guid? requestId, bool dryRun, bool json, bool inlineParameters = false)
    {
        try
        {
            if (dryRun)
            {
                client.CheckCanPreview(displayId, preset);
                if (inlineParameters) return Success(InlinePlan(), json);
                return Success(new { presetId = preset.Id, displayId, backend = "sdr-gamma-icc",
                    canPreview = true, requiresConfirmation = true, timeoutSeconds = seconds, changesDisplay = false }, json);
            }
            return Success(SessionSummary(await client.BeginAsync(displayId, preset, seconds, requestId: requestId,
                inlineParameters: inlineParameters)), json);
        }
        catch (ProfilePreviewException ex) when (dryRun && ex.Code is ("sdr_path_blocked" or "mhc2_profile_blocked"))
        {
            return Success(inlineParameters ? InlinePlan(ex.Code) : CalibrationPlan.BlockedByColorMode(preset.Id, displayId, ex.Code), json);
        }
        catch (ProfilePreviewException ex) when (ex.Code == "display_disconnected")
        {
            return Failure("not_found", ex.Message, 3, json);
        }

        object InlinePlan(string? reasonCode = null) => new
        {
            displayId, backend = "sdr-gamma-icc", calibration = preset.Calibration, canPreview = reasonCode is null,
            reasonCode, requiresConfirmation = true, timeoutSeconds = seconds, changesDisplay = false
        };
    }

    private static object SessionSummary(GammaCalibrationSession session) => new
    {
        sessionId = session.Id, displayId = session.Original.DisplayId, presetId = session.Preset.Id,
        session.Status, session.ReasonCode, session.ExpiresAt, session.InstalledProfile,
        session.CurveReadbackVerified, session.ColorAccuracyVerified, session.NeedsRecovery,
        session.BindingStatus, session.BindingReasonCode, session.ShowingOriginal, session.UndoOf,
        calibration = session.Preset.Calibration
    };
    private static object BindingSummary(CalibrationBinding binding) => new
    {
        binding.PersistenceKey, binding.IdentityKind, binding.DisplayName, binding.PresetId, binding.ProfileName,
        binding.RestoreEnabled, binding.SavedAt
    };

    private static Guid ParseId(string value) => Guid.TryParse(value, out var id) ? id : throw new ArgumentException("Expected a preset UUID.");
    private static Guid ParseRequestId(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? id : throw new ArgumentException("Expected a nonempty request UUID.");
    private static int PreviewSeconds(string? value)
    {
        int seconds = value is null ? 30 : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int duration) ? duration : 0;
        if (seconds is < 10 or > 60) throw new ArgumentException(new LocalizationService()["CliPreviewTimeout"]);
        return seconds;
    }

    private static CalibrationPreset ParseNewPreset(List<string> args)
    {
        var options = ReadCalibrationOptions(args);
        string author = TakeOption(args, "--author") ?? "";
        string description = TakeOption(args, "--description") ?? "";
        if (args is not ["presets", "create", var name] || name.StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Use presets create <name> with optional gamma, RGB gain, author and description flags.");
        return CalibrationPreset.Create(name, options.ApplyTo(new()))
            with { Author = author, Description = description };
    }

    private static CalibrationOptions ReadCalibrationOptions(List<string> args) => new(
        ReadNumber(args, "--gamma"), ReadNumber(args, "--red-gain"), ReadNumber(args, "--green-gain"), ReadNumber(args, "--blue-gain"));

    private sealed record CalibrationOptions(double? Gamma, double? RedGain, double? GreenGain, double? BlueGain)
    {
        public bool HasValues => Gamma.HasValue || RedGain.HasValue || GreenGain.HasValue || BlueGain.HasValue;

        public SdrCalibration ApplyTo(SdrCalibration current) => current with
        {
            Gamma = Gamma ?? current.Gamma,
            RedGain = RedGain ?? current.RedGain,
            GreenGain = GreenGain ?? current.GreenGain,
            BlueGain = BlueGain ?? current.BlueGain
        };

        public CalibrationPreset Edit(CalibrationPreset current)
        {
            var settings = ApplyTo(current.Calibration);
            return settings == current.Calibration ? current : current with
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow,
                Calibration = settings
            };
        }
    }

    private static double? ReadNumber(List<string> args, string option)
    {
        string? value = TakeOption(args, option);
        if (value is null) return null;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)) return result;
        throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, new LocalizationService()["CliCalibrationNumberError"], option));
    }
    private static string? TakeOption(List<string> args, string name)
    {
        int index = args.IndexOf(name);
        if (index < 0) return null;
        if (args.Count <= index + 1 || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for {name}.");
        string value = args[index + 1]; args.RemoveRange(index, 2); return value;
    }
    private static int Success(object data, bool json)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(json ? new { schemaVersion = 1, ok = true, data } : data, OutputJson));
        return 0;
    }
    private static int Failure(string code, string message, int exitCode, bool json)
    {
        if (json) Console.Out.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, ok = false, error = new { code, message } }, OutputJson));
        else Console.Error.WriteLine($"{code}: {message}");
        return exitCode;
    }
}
