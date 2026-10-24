using Windows.ApplicationModel;

namespace DccwPlus.Windows;

public sealed record StartupStatus(string State, bool CanEnable, bool IsEnabled);

public sealed class StartupService
{
    public const string TaskId = "DccwPlusStartup";
    public async Task<StartupStatus> GetAsync()
    {
        if (!HasPackageIdentity()) return new("unpackaged", false, false);
        return Map((await StartupTask.GetAsync(TaskId)).State);
    }

    // Called only from the settings page on the UI thread.
    public async Task<StartupStatus> SetEnabledAsync(bool enabled)
    {
        if (!HasPackageIdentity()) return new("unpackaged", false, false);
        var task = await StartupTask.GetAsync(TaskId);
        if (enabled) return Map(await task.RequestEnableAsync());
        task.Disable();
        return Map(task.State);
    }

    public static bool HasPackageIdentity()
    {
        try { return !string.IsNullOrEmpty(Package.Current.Id.Name); }
        catch (InvalidOperationException) { return false; }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }

    private static StartupStatus Map(StartupTaskState state) => new(state.ToString(),
        state == StartupTaskState.Disabled, state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy);
}
