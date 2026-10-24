"""Exercise the real console executable with isolated data and structured responses."""
import json
import subprocess
import sys
import tempfile
from pathlib import Path

exe = Path(sys.argv[1]).resolve()
root = Path(__file__).resolve().parents[1]
checks = 0
with tempfile.TemporaryDirectory(prefix="dccwpp-cli-") as directory:
    data = Path(directory) / "data"
    def call(*args, expected=0):
        global checks
        result = subprocess.run([str(exe), *args, "--data-dir", str(data), "--json"], capture_output=True, text=True, encoding="utf-8", timeout=30)
        assert result.returncode == expected, (args, result.returncode, result.stdout, result.stderr)
        assert not result.stderr.strip(), result.stderr
        response = json.loads(result.stdout)
        assert response["schemaVersion"] == 1
        assert response["ok"] == (expected == 0)
        checks += 1
        return response.get("data", response.get("error"))

    call("--help")
    assert call("status")["calibrationAvailable"] is False
    call("presets", "validate", str(root / "examples/neutral.dccw.json"))
    created = call("presets", "create", "CLI 中文预设")
    assert call("presets", "show", created["id"])["name"] == "CLI 中文预设"
    target = Path(directory) / "shared preset.json"
    call("presets", "export", created["id"], str(target))
    imported = call("presets", "import", str(target))
    assert imported["id"] != created["id"]
    assert len(call("presets", "list")["presets"]) == 2
    call("presets", "export", created["id"], str(target), expected=5)
    assert json.loads(target.read_text(encoding="utf-8"))["id"] == created["id"]
    call("presets", "show", "invalid-id", expected=2)
    call("presets", "import", str(Path(directory) / "missing.json"), expected=3)
    call("nonsense", expected=2)
    call("status", "--unknown-option", expected=2)
    call("startup", "status")
    displays = call("displays", "list")
    allowed = {"sdr", "wcg", "hdr", "advanced-color", "unknown", "not-probed"}
    for display in displays:
        assert display["colorState"] in allowed, display
        assert display["identityScope"] == "topology-query"
        assert display["hdrActive"] is not True or display["colorState"] != "sdr"
    observed = call("displays", "snapshot")
    assert observed["applied"] is False
    assert observed["capability"] == "read-only"
    assert observed["kind"] == "display-observation"
    assert call("displays", "snapshot", "show")["capturedAt"] == observed["capturedAt"]
    call("apply", created["id"], "--display", "missing-monitor", "--dry-run", expected=3)
    if displays:
        display = displays[0]["id"]
        assert call("apply", created["id"], "--display", display, "--dry-run")["canApply"] is False
        assert call("apply", created["id"], "--display", display, expected=4)["code"] == "calibration_backend_pending"
print(f"PASS: {checks} CLI process checks; data isolated, no calibration performed.")
