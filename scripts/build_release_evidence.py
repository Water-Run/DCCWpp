"""Build development MSIX packages from the same sources placed in the archive."""
import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo


ROOT = Path(__file__).resolve().parents[1]
ROOT_FILES = {".gitignore", "AGENTS.md", "Directory.Build.props", "DccwPlus.slnx", "global.json", "LICENSE", "README.md"}
SOURCE_TREES = {"assets", "design", "docs", "examples", "schemas", "scripts", "src", "tests"}
PRIVATE_EXTENSIONS = {".cer", ".crt", ".key", ".p12", ".pem", ".pfx"}
PRIVATE_DIRECTORIES = {".git", ".vs", "apppackages", "artifacts", "bin", "obj", "secrets"}
APPX = "{http://schemas.microsoft.com/appx/manifest/foundation/windows10}"


def file_hash(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def git(*arguments):
    return subprocess.check_output(["git", "-C", str(ROOT), *arguments])


def source_files():
    names = git("ls-files", "-z", "--cached", "--others", "--exclude-standard")
    for name in sorted(set(names.decode("utf-8").split("\0")) - {""}):
        path = PurePosixPath(name)
        if path.is_absolute() or ".." in path.parts or "\\" in name:
            raise ValueError(f"Invalid source path {name}")
        if name not in ROOT_FILES and path.parts[0] not in SOURCE_TREES:
            continue
        if (path.suffix.lower() in PRIVATE_EXTENSIONS or path.name.lower().startswith(".env")
                or path.name.lower().endswith(".local.json") or path.name.lower() == "package.storeassociation.xml"
                or any(part.lower() in PRIVATE_DIRECTORIES for part in path.parts)):
            raise ValueError(f"Private or generated path in source inputs: {name}")
        actual = ROOT.joinpath(*path.parts)
        if actual.is_symlink() or not actual.resolve().is_relative_to(ROOT):
            raise ValueError(f"Linked source path: {name}")
        if actual.is_file():
            yield name, actual


def stage_sources(directory, source_archive):
    inputs = {}
    with ZipFile(source_archive, "x", compression=ZIP_DEFLATED, compresslevel=9) as archive:
        for name, path in source_files():
            data = path.read_bytes()
            destination = directory / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(data)
            info = ZipInfo(f"DCCWPlus/{name}")
            info.compress_type = ZIP_DEFLATED
            archive.writestr(info, data)
            inputs[name] = hashlib.sha256(data).hexdigest()
    return inputs


def run_logged(arguments, directory, log):
    with log.open("wb") as output:
        result = subprocess.run(arguments, cwd=directory, stdout=output, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError(f"Build or validation failed with exit code {result.returncode}. See {log}")


def build_package(source, output, logs, architecture, run_tests):
    arguments = ["pwsh", "-NoProfile", "-File", str(source / "scripts/Build.ps1"),
                 "-Configuration", "Release", "-Architecture", architecture, "-Package", "-PackageRun", "evidence"]
    if run_tests:
        arguments.append("-Test")
    run_logged(arguments, source, logs / f"{architecture}.build.log")
    pattern = f"*_{architecture.lower()}_Test/*.msix"
    packages = list((source / "artifacts/packages/evidence").glob(pattern))
    if len(packages) != 1:
        raise ValueError(f"Expected one {architecture} MSIX, found {len(packages)}")
    package = output / packages[0].name
    shutil.copyfile(packages[0], package)
    run_logged([sys.executable, str(source / "scripts/verify_package.py"), str(package)], source,
               logs / f"{architecture}.verify.log")
    with ZipFile(package) as msix:
        manifest = ET.fromstring(msix.read("AppxManifest.xml"))
        identity = manifest.find(f"{APPX}Identity").attrib
        if identity["ProcessorArchitecture"] != architecture.lower() or identity["Name"] != "DCCWPlus.Development":
            raise ValueError("Unexpected development package identity or architecture")
        if "AppxSignature.p7x" in msix.namelist():
            raise ValueError("Development evidence expects unsigned packages")
        dependency_index = hashlib.sha256(msix.read("ThirdParty/index.json")).hexdigest()
    return {
        "file": f"packages/{package.name}", "sha256": file_hash(package), "bytes": package.stat().st_size,
        "identity": identity["Name"], "publisher": identity["Publisher"], "version": identity["Version"],
        "architecture": identity["ProcessorArchitecture"], "thirdPartyIndexSha256": dependency_index,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", help="Unused label under artifacts/release-evidence")
    parser.add_argument("--architecture", nargs="+", choices=("x64", "ARM64"), default=("x64", "ARM64"))
    args = parser.parse_args()
    if not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}", args.run):
        parser.error("Run label must contain only letters, digits, underscores or hyphens")
    architectures = tuple(dict.fromkeys(args.architecture))
    directory = ROOT / "artifacts/release-evidence" / args.run
    directory.mkdir(parents=True, exist_ok=False)
    source = directory / "build-input"
    output = directory / "packages"
    logs = directory / "logs"
    output.mkdir()
    logs.mkdir()
    snapshot = directory / "DCCWPlus-source.zip"
    git_head = git("rev-parse", "HEAD").decode().strip()
    inputs = stage_sources(source, snapshot)
    print(f"Archived {len(inputs)} source files. Build logs will be written to {logs}", flush=True)
    packages = []
    for index, architecture in enumerate(architectures):
        print(f"Building {architecture} from the archived sources.", flush=True)
        packages.append(build_package(source, output, logs, architecture, run_tests=index == 0))
    if any(file_hash(source / name) != expected for name, expected in inputs.items()):
        raise ValueError("A build changed a captured source file")
    evidence = {
        "schemaVersion": 1, "channel": "development", "builtFrom": "working-tree-snapshot",
        "gitHeadAtCapture": git_head,
        "sourceArchive": {"file": snapshot.name, "sha256": file_hash(snapshot), "files": inputs},
        "packages": packages,
    }
    (directory / "evidence.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    print(f"Source and package evidence: {directory / 'evidence.json'}")


if __name__ == "__main__":
    main()
