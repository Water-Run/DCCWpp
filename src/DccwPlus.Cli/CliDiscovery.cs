internal static class CliDiscovery
{
    internal const string ProductName = "DCCW++";
    internal const string Version = "0.2.0";

    private const string Read = "read";
    private const string LocalWrite = "local-write";
    private const string DisplayPreview = "display-preview";
    private const string DisplayChange = "display-change";

    private static readonly string[] GlobalOptions = ["--json", "--data-dir <directory>"];
    private static readonly Command[] Commands =
    [
        new("status", Read),
        new("displays list", Read),
        new("displays snapshot", LocalWrite),
        new("displays snapshot show", Read),
        new("displays restore-plan", Read, "--display <id>"),
        new("presets list", Read),
        new("presets show", Read, "<id>"),
        new("presets create", LocalWrite,
            "<name> [--gamma <value>] [--red-gain <value>] [--green-gain <value>] [--blue-gain <value>] [--author <text>] [--description <text>]"),
        new("presets import", LocalWrite, "<path> [--copy]"),
        new("presets rename", LocalWrite, "<id> <name>"),
        new("presets delete", LocalWrite, "<id>"),
        new("presets bindings", Read, "<id>"),
        new("presets export", LocalWrite, "<id> <path>"),
        new("presets validate", Read, "<path>"),
        new("apply", DisplayPreview, "<id> --display <id> [--seconds 30] [--request-id <uuid>] [--dry-run]",
            supportsDryRun: true, supportsRequestId: true),
        new("startup status", Read),
        new("startup last-restore", Read),
        new("bindings list", Read),
        new("bindings enable", LocalWrite, "<persistence-key>"),
        new("bindings disable", LocalWrite, "<persistence-key>"),
        new("bindings remove", LocalWrite, "<persistence-key>"),
        new("bindings restore", DisplayChange, "[--dry-run]", supportsDryRun: true),
        new("profiles list", Read, "--display <id>"),
        new("profiles preview", DisplayPreview, "<profile-name> --display <id> [--seconds 30] [--request-id <uuid>] [--dry-run]",
            supportsDryRun: true, supportsRequestId: true),
        new("profiles status", Read, "<session-id>"),
        new("profiles confirm", DisplayChange, "<session-id>"),
        new("profiles revert", DisplayChange, "<session-id>"),
        new("profiles recover", DisplayChange, "<session-id>"),
        new("calibration diagnose", Read, "--display <id>"),
        new("calibration sessions", Read),
        new("calibration status", Read, "<session-id>"),
        new("calibration confirm", DisplayChange, "<session-id>"),
        new("calibration revert", DisplayChange, "<session-id>"),
        new("calibration recover", DisplayChange, "<session-id>"),
        new("calibration undo", DisplayPreview, "<calibration-id> [--seconds 30] [--request-id <uuid>] [--dry-run]",
            supportsDryRun: true, supportsRequestId: true),
        new("calibration update", DisplayPreview, "<session-id> --preset <preset-id> [--original]"),
        new("recovery status", Read),
        new("recovery run", DisplayChange),
        new("calibration capture", Read, "[--display <display-id>]"),
        new("calibration compare", Read, "<before.json> <after.json>"),
        new("capabilities", Read)
    ];

    public static object Help() => new
    {
        name = ProductName,
        version = Version,
        commands = Commands.Select(command => command.Usage).ToArray(),
        globalOptions = GlobalOptions
    };

    public static object Capabilities() => new
    {
        name = ProductName,
        version = Version,
        contractVersion = 1,
        commands = Commands,
        globalOptions = GlobalOptions
    };

    private sealed class Command(string name, string effect, string arguments = "",
        bool supportsDryRun = false, bool supportsRequestId = false)
    {
        public string Name { get; } = name;
        public string Usage { get; } = arguments.Length == 0 ? name : $"{name} {arguments}";
        public string Effect { get; } = effect;
        public bool SupportsDryRun { get; } = supportsDryRun;
        public bool SupportsRequestId { get; } = supportsRequestId;
    }
}
