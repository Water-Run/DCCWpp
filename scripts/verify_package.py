"""Check the actual MSIX contents and entry points without installing it."""
import hashlib
import json
import struct
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from zipfile import ZipFile

package = Path(sys.argv[1]).resolve()
root = Path(__file__).resolve().parents[1]


def machine_type(binary):
    assert binary.read(2) == b"MZ", "Missing DOS header"
    binary.seek(60)
    (header_offset,) = struct.unpack("<I", binary.read(4))
    binary.seek(header_offset)
    assert binary.read(4) == b"PE\0\0", "Missing PE header"
    return struct.unpack("<H", binary.read(2))[0]


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
    identity = manifest.find("p:Identity", ns)
    architecture = identity.attrib["ProcessorArchitecture"]
    expected_machine = {"x64": 0x8664, "arm64": 0xAA64}[architecture]
    for binary in ("DccwPlus.App.exe", "Cli/dccwpp.exe", "Cli/coreclr.dll"):
        with archive.open(binary) as content:
            assert machine_type(content) == expected_machine, f"{binary} does not target {architecture}"
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
    assert "Microsoft.Win32.SystemEvents.dll" in names, "Startup monitor must include its Windows event dependency"
    notices = json.loads(archive.read("ThirdParty/index.json"))
    assert notices["schemaVersion"] == 1
    by_id = {(entry["id"] + "/" + entry["version"]).lower(): entry for entry in notices["packages"]}
    assert len(by_id) == len(notices["packages"]), "Duplicate third-party attribution"
    for deps_name in ("DccwPlus.App.deps.json", "Cli/dccwpp.deps.json"):
        deps = json.loads(archive.read(deps_name))
        for dependency, metadata in deps["libraries"].items():
            if metadata["type"] == "runtimepack":
                dependency = dependency.removeprefix("runtimepack.")
            elif metadata["type"] != "package":
                continue
            assert dependency.lower() in by_id, f"Missing redistribution attribution for {dependency}"
    for package_id, entry in by_id.items():
        assert entry["documents"] or entry["licenseUrl"], package_id
        for document in entry["documents"]:
            path = document["path"]
            assert path.startswith(package_id + "/") and ".." not in Path(path).parts
            assert hashlib.sha256(archive.read(f"ThirdParty/{path}")).hexdigest() == document["sha256"].lower(), path
print(f"PASS: MSIX entry points, CLI runtime, startup monitor, alias, icons, PRI and notices verified ({package.stat().st_size / 1024**2:.1f} MiB).")
