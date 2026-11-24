using DccwPlus.Core.Calibration;

namespace DccwPlus.Windows;

// Reads an installed profile by file name from the system color directory. It never writes or loads a LUT.
internal static class InstalledProfileReader
{
    public static byte[] ReadBytes(string fileName)
    {
        IccProfileInspector.ValidateFileName(fileName);
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "spool", "drivers", "color", fileName);
        if (File.ResolveLinkTarget(path, returnFinalTarget: false) is not null)
            throw new InvalidDataException("Linked color profile files cannot be selected.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > IccProfileInspector.MaxBytes) throw new InvalidDataException("Color profile is too large.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public static ProfileReport? Read(string? fileName)
    {
        if (fileName is null) return null;
        try
        {
            IccProfileInspector.ValidateFileName(fileName);
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "spool", "drivers", "color");
            string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(directory, Path.GetFileName(fileName)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return IccProfileInspector.Unreadable(fileName);
            if (!File.Exists(full))
            {
                try
                {
                    if (File.ResolveLinkTarget(full, returnFinalTarget: false) is not null) return IccProfileInspector.Unreadable(fileName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return IccProfileInspector.Missing(fileName);
                }
                return IccProfileInspector.Missing(fileName);
            }
            if (File.ResolveLinkTarget(full, returnFinalTarget: false) is not null) return IccProfileInspector.Unreadable(fileName);
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > IccProfileInspector.MaxBytes) return IccProfileInspector.TooLarge(fileName);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return IccProfileInspector.Inspect(fileName, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return IccProfileInspector.Unreadable(fileName);
        }
    }
}
