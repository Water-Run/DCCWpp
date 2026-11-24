"""Inspect DCCW++ MSIX files and Store uploads, or check a source manifest."""
import argparse
import hashlib
import json
import re
import shutil
import struct
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path
from zipfile import BadZipFile, ZipFile

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
      "desktop4": "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4",
      "rescap": "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"}

identity_fields = {"name", "publisher", "publisherDisplayName"}
development_name = "DCCWPlus.Development"
development_publisher = "CN=DCCWPlus-Development"


def read_store_identity(path):
    config = json.loads(path.read_text(encoding="utf-8-sig"))
    if (not isinstance(config, dict) or config.keys() != identity_fields
            or any(not isinstance(value, str) or not value.strip() or value != value.strip()
                   for value in config.values())):
        raise ValueError("Store identity needs the name, publisher and publisherDisplayName values from Partner Center.")
    if config["name"].casefold() == development_name.casefold() or config["publisher"].casefold() == development_publisher.casefold():
        raise ValueError("Use the DCCW++ Partner Center identity in the local identity file.")
    return config


def verify_store_manifest(manifest, expected):
    identity = manifest.find("p:Identity", ns)
    if identity is None:
        raise ValueError("Missing package identity.")
    actual = {
        "name": identity.get("Name"),
        "publisher": identity.get("Publisher"),
        "publisherDisplayName": manifest.findtext("p:Properties/p:PublisherDisplayName", namespaces=ns),
    }
    for field in ("name", "publisher", "publisherDisplayName"):
        if actual[field] != expected[field]:
            raise ValueError(f"Package {field} differs from the local Partner Center identity.")
    version = identity.get("Version", "")
    if not re.fullmatch(r"[0-9]{1,5}(\.[0-9]{1,5}){3}", version):
        raise ValueError("Use a four-part MSIX submission version.")
    components = [int(part) for part in version.split(".")]
    if components[0] == 0 or components[-1] != 0 or max(components) > 65535:
        raise ValueError("Use a nonzero major version, a zero fourth part and components no greater than 65535.")


def verify_msix(package, store_identity):
    with ZipFile(package) as archive:
        names = set(archive.namelist())
        for required in ("DccwPlus.App.exe", "DccwPlus.Core.dll", "DccwPlus.Windows.dll", "Cli/dccwpp.exe",
                         "Cli/dccwpp.runtimeconfig.json", "Cli/DccwPlus.Core.dll", "Cli/DccwPlus.Windows.dll", "LICENSE.txt"):
            assert required in names, f"Package missing {required}"
        manifest = ET.fromstring(archive.read("AppxManifest.xml"))
        if store_identity is not None:
            verify_store_manifest(manifest, store_identity)
        identity = manifest.find("p:Identity", ns)
        architecture = identity.attrib["ProcessorArchitecture"]
        expected_machine = {"x64": 0x8664, "arm64": 0xAA64}[architecture]
        for binary in ("DccwPlus.App.exe", "Cli/dccwpp.exe", "Cli/coreclr.dll"):
            with archive.open(binary) as content:
                assert machine_type(content) == expected_machine, f"{binary} does not target {architecture}"
        for application in manifest.findall("p:Applications/p:Application", ns):
            assert application.attrib["Executable"].replace("\\", "/") in names
        app = manifest.find("p:Applications/p:Application[@Id='App']", ns)
        assert app is not None and app.attrib["Executable"] == "DccwPlus.App.exe"
        assert app.attrib["EntryPoint"] == "Windows.FullTrustApplication"
        assert app.attrib[f"{{{ns['desktop4']}}}SupportsMultipleInstances"] == "true"
        assert manifest.find("p:Capabilities/rescap:Capability[@Name='runFullTrust']", ns) is not None
        assert manifest.find("p:Dependencies/p:TargetDeviceFamily[@Name='Windows.Desktop']", ns).attrib["MinVersion"] == "10.0.22000.0"
        languages = {entry.attrib["Language"].casefold() for entry in manifest.findall("p:Resources/p:Resource[@Language]", ns)}
        assert {"en-us", "zh-cn", "zh-tw"}.issubset(languages), "Package missing a supported language"
        startup_extension = app.find("p:Extensions/uap5:Extension[@Category='windows.startupTask']", ns)
        assert startup_extension is not None and startup_extension.attrib["Executable"] == "DccwPlus.App.exe"
        assert startup_extension.attrib["EntryPoint"] == "Windows.FullTrustApplication"
        startup = startup_extension.find("uap5:StartupTask", ns)
        assert startup is not None and startup.attrib["TaskId"] == "DccwPlusStartup"
        assert startup.attrib["Enabled"] == "false"
        cli = app.find("p:Extensions/uap5:Extension[@Category='windows.appExecutionAlias']", ns)
        assert cli.attrib["EntryPoint"] == "Windows.FullTrustApplication"
        assert cli.attrib["Executable"].replace("\\", "/") == "Cli/dccwpp.exe"
        cli_alias = cli.find("uap5:AppExecutionAlias", ns)
        assert cli_alias.attrib[f"{{{ns['desktop4']}}}Subsystem"] == "console"
        alias = cli_alias.find("uap5:ExecutionAlias", ns)
        assert alias is not None and alias.attrib["Alias"] == "dccwpp.exe"
        logo = manifest.findtext("p:Properties/p:Logo", namespaces=ns)
        assert logo and logo.replace("\\", "/") in names, "Package missing its Store logo"
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
    return architecture, identity.attrib["Version"]


def verify_package(path, store_identity):
    if path.suffix.lower() == ".msix":
        return verify_msix(path, store_identity)
    if path.suffix.lower() != ".msixupload":
        raise ValueError("Pass a .msix or single-architecture .msixupload file.")
    with ZipFile(path) as upload:
        packages = [entry for entry in upload.infolist() if Path(entry.filename).suffix.lower() == ".msix"]
        if len(packages) != 1 or any(Path(entry.filename).suffix.lower() == ".msixbundle" for entry in upload.infolist()):
            raise ValueError("The upload must contain one standalone .msix file.")
        with upload.open(packages[0]) as nested, tempfile.TemporaryFile() as package:
            shutil.copyfileobj(nested, package)
            package.seek(0)
            return verify_msix(package, store_identity)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("packages", nargs="*", type=Path, help="One MSIX or MSIXUPLOAD, or the x64 and ARM64 pair")
    parser.add_argument("--store-identity", type=Path, help="Local JSON with the identity values from Partner Center")
    parser.add_argument("--manifest-only", type=Path, help="Check a source manifest before building")
    args = parser.parse_args()
    if args.manifest_only and (args.packages or not args.store_identity):
        parser.error("--manifest-only requires --store-identity and takes no package paths")
    if not args.manifest_only and not 1 <= len(args.packages) <= 2:
        parser.error("Pass one package or the x64 and ARM64 pair")
    if not __debug__:
        parser.error("Run the verifier without Python optimization so all package checks execute")
    try:
        expected = read_store_identity(args.store_identity) if args.store_identity else None
        if args.manifest_only:
            verify_store_manifest(ET.parse(args.manifest_only).getroot(), expected)
            print("PASS: source manifest matches the local Store identity and submission version rules.")
            return 0

        verified = [verify_package(package, expected) for package in args.packages]
        if len(verified) == 2:
            if {arch for arch, _ in verified} != {"x64", "arm64"}:
                raise ValueError("A package pair must contain one x64 and one ARM64 package.")
            if verified[0][1] != verified[1][1]:
                raise ValueError("The x64 and ARM64 packages use different versions.")
        for package, (architecture, version) in zip(args.packages, verified):
            print(f"PASS: {package.name} ({architecture}, {version}, {package.stat().st_size / 1024**2:.1f} MiB).")
        return 0
    except (AssertionError, AttributeError, BadZipFile, ET.ParseError, KeyError, OSError, ValueError, struct.error) as error:
        print(f"Package verification failed: {error or 'MSIX layout or payload does not match DCCW++.'}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
