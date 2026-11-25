namespace DccwPlus.Core.Storage;

public static class AtomicFile
{
    public static void Write(string path, byte[] contents, bool overwrite = true, Action? beforePublish = null)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(contents);
                file.Flush(flushToDisk: true);
            }
            beforePublish?.Invoke();
            try { File.Move(temporary, path, overwrite); }
            catch (Exception ex) when (overwrite && OperatingSystem.IsWindows()
                && (ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xFFFF) is 32 or 33)
                && File.Exists(path))
            {
                // On Windows, Move can reject an existing destination even when its readers share delete access.
                beforePublish?.Invoke();
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
