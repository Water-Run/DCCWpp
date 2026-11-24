#:project ../src/DccwPlus.Core/DccwPlus.Core.csproj
#:property PublishAot=false

using DccwPlus.Core.Calibration;

if (args is not [var directory])
    throw new ArgumentException("Supply the test fixture output directory.");

var source = Path.Combine(Environment.SystemDirectory, "spool", "drivers", "color", "sRGB Color Space Profile.icm");
var id = Guid.NewGuid();
var ramp = GammaRampMath.Compose(GammaRamp.Identity(), new() { RedGain = .9 });
var fixture = IccCalibrationWriter.Build(File.ReadAllBytes(source), ramp, id);
Directory.CreateDirectory(directory);
var path = Path.GetFullPath(Path.Combine(directory, $"DCCWPlus-{id:N}.icc"));
using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.Write(fixture);
Console.WriteLine(path);
