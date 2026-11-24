"""Build development packages or Store uploads from the source archive."""
import argparse
import hashlib
import io
import json
import re
import shutil
import stat
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo
from verify_package import development_name, development_publisher, open_msix, read_store_identity, verify_store_manifest


ROOT = Path(__file__).resolve().parents[1]
ROOT_FILES = {".gitattributes", ".gitignore", "AGENTS.md", "Directory.Build.props", "DccwPlus.slnx", "global.json", "LICENSE", "README.md"}
SOURCE_TREES = {".github", "assets", "design", "docs", "examples", "schemas", "scripts", "src", "tests"}
PRIVATE_EXTENSIONS = {".cer", ".crt", ".key", ".p12", ".pem", ".pfx"}
PRIVATE_DIRECTORIES = {".git", ".vs", "__pycache__", "apppackages", "artifacts", "bin", "obj", "secrets"}
APPX = "{http://schemas.microsoft.com/appx/manifest/foundation/windows10}"


def file_hash(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def git(*arguments):
    return subprocess.check_output(["git", "-C", str(ROOT), *arguments])


def source_path(name):
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts or "\\" in name:
        raise ValueError(f"Invalid source path {name}")
    if name not in ROOT_FILES and path.parts[0] not in SOURCE_TREES:
        return None
    if (path.suffix.lower() in PRIVATE_EXTENSIONS or path.name.lower().startswith(".env")
            or path.name.lower().endswith(".local.json") or path.name.lower() == "package.storeassociation.xml"
            or any(part.lower() in PRIVATE_DIRECTORIES for part in path.parts)):
        raise ValueError(f"Private or generated path in source inputs: {name}")
    return path


def working_tree_sources():
    names = git("ls-files", "-z", "--cached", "--others", "--exclude-standard")
    for name in sorted(set(names.decode("utf-8").split("\0")) - {""}):
        path = source_path(name)
        if path is None:
            continue
        actual = ROOT.joinpath(*path.parts)
        if actual.is_symlink() or not actual.resolve().is_relative_to(ROOT):
            raise ValueError(f"Linked source path: {name}")
        if actual.is_file():
            yield name, actual.read_bytes()


def committed_sources(commit):
    with ZipFile(io.BytesIO(git("archive", "--format=zip", commit))) as archive:
        for entry in sorted(archive.infolist(), key=lambda item: item.filename):
            if entry.is_dir() or source_path(entry.filename) is None:
                continue
            if stat.S_ISLNK(entry.external_attr >> 16):
                raise ValueError(f"Linked source path: {entry.filename}")
            yield entry.filename, archive.read(entry)


def stage_sources(directory, source_archive, sources):
    inputs = {}
    with ZipFile(source_archive, "x", compression=ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sources:
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


def build_package(source, output, logs, architecture, run_tests, store_identity, expected_identity):
    arguments = ["pwsh", "-NoProfile", "-File", str(source / "scripts/Build.ps1"),
                 "-Configuration", "Release", "-Architecture", architecture, "-Package", "-PackageRun", "evidence"]
    verify_args = []
    if store_identity is not None:
        arguments.extend(["-Store", "-StoreIdentityFile", str(store_identity)])
        verify_args = ["--store-identity", str(store_identity)]
    if run_tests:
        arguments.append("-Test")
    run_logged(arguments, source, logs / f"{architecture}.build.log")
    if architecture == "x64":
        run_logged([sys.executable, str(source / "scripts/smoke_cli.py"), str(source / "artifacts/cli/win-x64/dccwpp.exe")],
                   source, logs / "x64.cli.log")
    pattern = (f"*_{architecture.lower()}.msixupload" if store_identity is not None
               else f"*_{architecture.lower()}_Test/*.msix")
    packages = list((source / "artifacts/packages/evidence").glob(pattern))
    if len(packages) != 1:
        raise ValueError(f"Expected one {architecture} build artifact matching {pattern}, found {len(packages)}")
    package = output / packages[0].name
    shutil.copyfile(packages[0], package)
    run_logged([sys.executable, str(source / "scripts/verify_package.py"), str(package), *verify_args], source,
               logs / f"{architecture}.verify.log")
    with open_msix(package) as payload:
        embedded_hash = hashlib.file_digest(payload, "sha256").hexdigest() if store_identity is not None else None
        with ZipFile(payload) as msix:
            manifest = ET.fromstring(msix.read("AppxManifest.xml"))
            identity = manifest.find(f"{APPX}Identity").attrib
            if identity["ProcessorArchitecture"] != architecture.lower():
                raise ValueError("Unexpected package architecture")
            if store_identity is None and (identity["Name"], identity["Publisher"]) != (development_name, development_publisher):
                raise ValueError("Expected the development package identity")
            if expected_identity is not None:
                verify_store_manifest(manifest, expected_identity)
            if "AppxSignature.p7x" in msix.namelist():
                raise ValueError("Release evidence expects an unsigned build")
            dependency_index = hashlib.sha256(msix.read("ThirdParty/index.json")).hexdigest()
    evidence = {
        "file": f"packages/{package.name}", "sha256": file_hash(package), "bytes": package.stat().st_size,
        "identity": identity["Name"], "publisher": identity["Publisher"], "version": identity["Version"],
        "architecture": identity["ProcessorArchitecture"], "thirdPartyIndexSha256": dependency_index,
    }
    if embedded_hash is not None:
        evidence["embeddedMsixSha256"] = embedded_hash
    return evidence


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", help="Unused label under artifacts/release-evidence")
    parser.add_argument("--architecture", nargs="+", choices=("x64", "ARM64"), default=("x64", "ARM64"))
    parser.add_argument("--commit", help="Build from a Git commit instead of the working tree")
    parser.add_argument("--store", action="store_true", help="Build uploads with the local Partner Center identity")
    args = parser.parse_args()
    if not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}", args.run):
        parser.error("Run label must contain only letters, digits, underscores or hyphens")
    architectures = tuple(dict.fromkeys(args.architecture))
    commit = git("rev-parse", "--verify", "--end-of-options", f"{args.commit}^{{commit}}").decode().strip() if args.commit else None
    git_head = git("rev-parse", "HEAD").decode().strip()
    store_identity = ROOT / "store/identity.local.json" if args.store else None
    if store_identity is not None and not store_identity.is_file():
        parser.error("--store requires store/identity.local.json. See docs/STORE.md.")
    expected = read_store_identity(store_identity) if store_identity is not None else None
    directory = ROOT / "artifacts/release-evidence" / args.run
    directory.mkdir(parents=True, exist_ok=False)
    source = directory / "build-input"
    output = directory / "packages"
    logs = directory / "logs"
    output.mkdir()
    logs.mkdir()
    snapshot = directory / "DCCWPlus-source.zip"
    sources = committed_sources(commit) if commit else working_tree_sources()
    inputs = stage_sources(source, snapshot, sources)
    if expected is not None:
        verify_store_manifest(ET.parse(source / "src/DccwPlus.App/Package.appxmanifest").getroot(), expected)
    print(f"Archived {len(inputs)} source files. Build logs will be written to {logs}", flush=True)
    packages = []
    for index, architecture in enumerate(architectures):
        print(f"Building {architecture} from the archived sources.", flush=True)
        packages.append(build_package(source, output, logs, architecture, run_tests=index == 0,
                                      store_identity=store_identity, expected_identity=expected))
    if len(packages) > 1:
        verify_args = ["--store-identity", str(store_identity)] if store_identity is not None else []
        run_logged([sys.executable, str(source / "scripts/verify_package.py"),
                    *(str(directory / package["file"]) for package in packages), *verify_args], source, logs / "architectures.verify.log")
    if store_identity is not None and read_store_identity(store_identity) != expected:
        raise ValueError("The local Store identity changed during the build")
    if any(file_hash(source / name) != expected for name, expected in inputs.items()):
        raise ValueError("A build changed a captured source file")
    evidence = {
        "schemaVersion": 1, "channel": "store-upload" if args.store else "development",
        "builtFrom": "git-commit" if commit else "working-tree-snapshot",
        "gitHeadAtCapture": git_head,
        "sourceArchive": {"file": snapshot.name, "sha256": file_hash(snapshot), "files": inputs},
        "packages": packages,
    }
    if commit:
        evidence["sourceCommit"] = commit
    (directory / "evidence.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    print(f"Source and package evidence: {directory / 'evidence.json'}")


if __name__ == "__main__":
    main()
