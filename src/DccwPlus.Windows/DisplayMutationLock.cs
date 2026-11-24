using System.Security.Principal;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Localization;

namespace DccwPlus.Windows;

// This mutex covers all ICC association and SDR curve writes in the current user session.
// Acquire and release on the same thread.
internal sealed class DisplayMutationLock(Mutex mutex) : IDisposable
{
    public static DisplayMutationLock Acquire() => TryAcquire()
        ?? throw new ProfilePreviewException("preview_busy", new LocalizationService()["DisplayOperationBusy"]);

    public static DisplayMutationLock? TryAcquire()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var mutex = new Mutex(false, "Local\\DCCWPlus.ProfilePreview." + identity.User!.Value);
        try
        {
            if (mutex.WaitOne(0)) return new(mutex);
        }
        catch (AbandonedMutexException) { return new(mutex); }
        catch { mutex.Dispose(); throw; }
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
