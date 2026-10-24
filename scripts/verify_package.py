"""Check the actual MSIX contents and entry points without installing it."""
import hashlib
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from zipfile import ZipFile

package = Path(sys.argv[1]).resolve()
root = Path(__file__).resolve().parents[1]
ns = {"p": "http://schemas.microsoft.com/appx/manifest/foundation/windows10",
      "uap": "http://schemas.microsoft.com/appx/manifest/uap/windows10",
      "uap5": "http://schemas.microsoft.com/appx/manifest/uap/windows10/5",
      "desktop4": "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4"}
with ZipFile(package) as archive:
    names = set(archive.namelist())
    for required in ("DccwPlus.App.exe", "DccwPlus.Core.dll", "DccwPlus.Windows.dll", "Cli/dccwpp.exe",
                     "Cli/dccwpp.runtimeconfig.json", "Cli/DccwPlus.Core.dll", "Cli/DccwPlus.Windows.dll", "LICENSE.txt"):
        assert required in names, f"Package missing {required}"
    manifest = ET.fromstring(archive.read("AppxManifest.xml"))
    for application in manifest.findall("p:Applications/p:Application", ns):
        assert application.attrib["Executable"].replace("\\", "/") in names
    startup = manifest.find(".//uap5:StartupTask", ns)
    assert startup is not None and startup.attrib["TaskId"] == "DccwPlusStartup"
    assert startup.attrib["Enabled"] == "false"
    alias = manifest.find(".//uap5:ExecutionAlias", ns)
    assert alias is not None and alias.attrib["Alias"] == "dccwpp.exe"
    cli = manifest.find("p:Applications/p:Application[@Id='Cli']", ns)
    assert cli.attrib[f"{{{ns['desktop4']}}}SupportsMultipleInstances"] == "true"
    for element in manifest.iter():
        for attribute in ("Square44x44Logo", "Square150x150Logo", "Wide310x150Logo", "Image"):
            if attribute in element.attrib:
                assert element.attrib[attribute].replace("\\", "/") in names
    assert hashlib.sha256(archive.read("LICENSE.txt")).digest() == hashlib.sha256((root / "LICENSE").read_bytes()).digest()
    assert any(name.lower().endswith(".pri") for name in names)
    assert "Cli/coreclr.dll" in names, "Console host must include its self-contained runtime"
    assert "microsoft.ui.xaml.dll" in {name.lower() for name in names}, "UI host must include its self-contained Windows App SDK"
print(f"PASS: MSIX entry points, CLI runtime, startup, alias, icons, PRI and license verified ({package.stat().st_size / 1024**2:.1f} MiB).")
