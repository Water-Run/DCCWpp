using DccwPlus.Core.Calibration;

namespace DccwPlus.Core.Storage;

public sealed class DisplayObservationStore(string root)
{
    private readonly string _path = Path.Combine(Path.GetFullPath(root), "observations", "display.json");

    public void Save(DisplayObservation observation) => AtomicFile.Write(_path, DisplayObservationCodec.Encode(observation));

    public DisplayObservation Load()
    {
        if (!File.Exists(_path)) throw new FileNotFoundException("No display observation has been saved.", _path);
        using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > DisplayObservationCodec.MaxBytes) throw new InvalidDataException("Display observation exceeds 256 KiB.");
        using var buffer = new MemoryStream();
        file.CopyTo(buffer);
        return DisplayObservationCodec.Decode(buffer.ToArray());
    }
}
