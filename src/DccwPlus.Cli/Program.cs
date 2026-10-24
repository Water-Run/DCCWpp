using System.Text;
using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;
using DccwPlus.Windows;

Console.OutputEncoding = new UTF8Encoding(false);
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
            string root = TakeOption(remaining, "--data-dir") ?? DataPaths.DefaultRoot;
            var store = new PresetStore(root);
            var displays = new WindowsDisplayService();
            if (remaining.Count == 0 || remaining.SequenceEqual(new[] { "--help" }) || remaining.SequenceEqual(new[] { "help" }))
                return Success(new
                {
                    name = "DCCW++",
                    version = "0.1.0",
                    commands = new[]
                {
                    "status", "displays list", "displays snapshot", "displays snapshot show",
                    "presets list", "presets show <id>", "presets create <name>", "presets import <path>",
                    "presets export <id> <path>", "presets validate <path>",
                    "apply <id> --display <id> --dry-run", "startup status"
                },
                    globalOptions = new[] { "--json", "--data-dir <directory>" }
                }, json);

            switch (remaining.ToArray())
            {
                case ["status"]:
                    return Success(new
                    {
                        name = "DCCW++",
                        version = "0.1.0",
                        stage = "foundation",
                        calibrationAvailable = false,
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
                case ["presets", "create", var name]:
                    var created = CalibrationPreset.Create(name); store.Add(created); return Success(created, json);
                case ["presets", "import", var path]: return Success(store.Import(path), json);
                case ["presets", "validate", var path]:
                    var validated = PresetCodec.Decode(PresetCodec.ReadBounded(path));
                    return Success(new { valid = true, validated.SchemaVersion, validated.Name }, json);
                case ["presets", "export", var id, var path]:
                    store.Export(ParseId(id), path); return Success(new { path = Path.GetFullPath(path) }, json);
                case ["startup", "status"]: return Success(await new StartupService().GetAsync(), json);
            }

            if (remaining.FirstOrDefault() == "apply")
            {
                string? display = TakeOption(remaining, "--display");
                bool dryRun = remaining.Remove("--dry-run");
                if (remaining.Count != 2 || display is null) throw new ArgumentException("Use apply <preset-id> --display <display-id> [--dry-run].");
                var plan = new CalibrationService(displays).Plan(store.Get(ParseId(remaining[1])), display);
                if (dryRun) return Success(plan, json);
                return Failure("calibration_backend_pending", "Calibration is not available in this foundation build. Use --dry-run to inspect the plan.", 4, json);
            }
            throw new ArgumentException("Unknown command or option. Run dccwpp --help.");
        }
        catch (ArgumentException ex) { return Failure("invalid_arguments", ex.Message, 2, json); }
        catch (InvalidDataException ex) { return Failure("invalid_preset", ex.Message, 2, json); }
        catch (FileNotFoundException ex) { return Failure("not_found", ex.Message, 3, json); }
        catch (DirectoryNotFoundException ex) { return Failure("not_found", ex.Message, 3, json); }
        catch (KeyNotFoundException ex) { return Failure("not_found", ex.Message, 3, json); }
        catch (UnauthorizedAccessException ex) { return Failure("access_denied", ex.Message, 5, json); }
        catch (IOException ex) { return Failure("io_error", ex.Message, 5, json); }
        catch (Exception ex) { return Failure("internal_error", ex.Message, 1, json); }
    }

    private static Guid ParseId(string value) => Guid.TryParse(value, out var id) ? id : throw new ArgumentException("Expected a preset UUID.");
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
