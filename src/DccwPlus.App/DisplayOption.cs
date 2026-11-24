using DccwPlus.Core.Calibration;

namespace DccwPlus.App;

public sealed record DisplayOption(DisplayInfo Display, string Details)
{
    public string Name => Display.Name;

    // WinUI uses the item's string representation for the selector's accessible name.
    public override string ToString() => $"{Name}, {Details}";
}
