using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using DccwPlus.Core.Storage;

namespace DccwPlus.App;

public partial class App : Application
{
    private Window? _window;
    public App()
    {
        UnhandledException += (_, e) => RecordFailure(e.Exception);
        DccwPlus.Windows.DesktopIdentity.Initialize();
        InitializeComponent();
    }
    private static void RecordFailure(Exception exception)
    {
        try
        {
            AtomicFile.Write(Path.Combine(DataPaths.DefaultRoot, "last-error.txt"),
                System.Text.Encoding.UTF8.GetBytes($"{DateTimeOffset.UtcNow:O}\n{exception}"));
        }
        catch (Exception) { System.Diagnostics.Debug.WriteLine(exception); }
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.StartupTask)
        {
            // Persist an honest result until the recovery/calibration engine is implemented.
            AtomicFile.Write(Path.Combine(DataPaths.DefaultRoot, "startup-status.json"),
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, applied = false, reason = "calibration_backend_pending", time = DateTimeOffset.UtcNow }));
            Exit();
            return;
        }
        _window = new MainWindow();
        _window.Activate();
    }
}
