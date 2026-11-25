using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using DccwPlus.Core.Storage;
using DccwPlus.Windows;

namespace DccwPlus.App;

public partial class App : Application
{
    private Window? _window;
    public App()
    {
        UnhandledException += (_, e) => RecordFailure(e.Exception);
        DesktopIdentity.Initialize();
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
        var request = WizardLaunchRequest.Parse(Environment.GetCommandLineArgs()[1..]);
        try
        {
            var window = new MainWindow();
            _window = window;
            var outcome = request is null ? WizardLaunchOutcome.Opened : window.PrepareWizardLaunch(request);
            window.Activate();
            if (request is not null) await WizardLaunch.RespondAsync(request, outcome);
        }
        catch (Exception ex) when (request is not null)
        {
            RecordFailure(ex);
            await WizardLaunch.RespondAsync(request, WizardLaunchOutcome.Failed);
            Exit();
        }
    }
}
