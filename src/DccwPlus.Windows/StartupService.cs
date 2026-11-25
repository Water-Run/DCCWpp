using Windows.ApplicationModel;

namespace DccwPlus.Windows;

public sealed record StartupStatus(string State, bool CanEnable, bool IsEnabled);

public sealed class StartupService
{
    public const string TaskId = "DccwPlusStartup";
    public async Task<StartupStatus> GetAsync()
    {
        if (!HasPackageIdentity()) return new("unpackaged", false, false);
        var task = await FindTaskAsync();
        return task is null ? new("unavailable", false, false) : Map(task.State);
    }

    public async Task<StartupStatus> SetEnabledAsync(bool enabled)
    {
        if (!HasPackageIdentity()) return new("unpackaged", false, false);
        var task = await FindTaskAsync();
        if (task is null) return new("unavailable", false, false);

        var state = task.State;
        if (enabled && state == StartupTaskState.Disabled)
            return Map(await task.RequestEnableAsync());
        if (!enabled && state == StartupTaskState.Enabled)
            task.Disable();
        return Map(task.State);
    }

    private static async Task<StartupTask?> FindTaskAsync() =>
        (await StartupTask.GetForCurrentPackageAsync()).SingleOrDefault(task => task.TaskId == TaskId);

    public static bool HasPackageIdentity()
    {
        try { return !string.IsNullOrEmpty(Package.Current.Id.Name); }
        catch (InvalidOperationException) { return false; }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }

    private static StartupStatus Map(StartupTaskState state) => new(state.ToString(),
        state == StartupTaskState.Disabled, state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy);
}
