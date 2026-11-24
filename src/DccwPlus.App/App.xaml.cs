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
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.StartupTask)
        {
            try
            {
                await new StartupRestoreMonitor().RunAsync();
            }
            catch (Exception ex) { RecordFailure(ex); }
            finally { Exit(); }
            return;
        }
        _window = new MainWindow();
        _window.Activate();
    }
}
