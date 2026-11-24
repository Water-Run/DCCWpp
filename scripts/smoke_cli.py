"""Exercise the real console executable with isolated data and structured responses."""
import hashlib
import json
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from threading import Lock
from uuid import uuid4

exe = Path(sys.argv[1]).resolve()
root = Path(__file__).resolve().parents[1]
checks = 0
checks_lock = Lock()
with tempfile.TemporaryDirectory(prefix="dccwpp-cli-") as directory:
    data = Path(directory) / "data"
    def call(*args, expected=0):
        global checks
        result = subprocess.run([str(exe), *args, "--data-dir", str(data), "--json"], capture_output=True, text=True, encoding="utf-8", timeout=30)
        allowed_codes = expected if isinstance(expected, tuple) else (expected,)
        assert result.returncode in allowed_codes, (args, result.returncode, result.stdout, result.stderr)
        assert not result.stderr.strip(), result.stderr
        response = json.loads(result.stdout)
        assert response["schemaVersion"] == 1
        assert response["ok"] == (result.returncode == 0)
        with checks_lock:
            checks += 1
        return response.get("data", response.get("error"))

    help_info = call("--help")
    capabilities = call("capabilities")
    public_commands = {command["name"]: command for command in capabilities["commands"]}
    assert capabilities["contractVersion"] == 1
    assert capabilities["version"] == help_info["version"]
    assert [command["usage"] for command in capabilities["commands"]] == help_info["commands"]
    assert len(public_commands) == len(help_info["commands"])
    assert all(not name.startswith("__") for name in public_commands)
    assert public_commands["capabilities"]["effect"] == "read"
    assert public_commands["presets import"]["effect"] == "local-write"
    assert public_commands["apply"]["effect"] == "display-preview"
    assert public_commands["apply"]["supportsDryRun"] is True
    assert public_commands["apply"]["supportsRequestId"] is True
    assert public_commands["calibration confirm"]["effect"] == "display-change"
    assert public_commands["calibration confirm"]["supportsDryRun"] is False
    assert not data.exists(), "Help and capabilities must leave app data untouched"
    assert call("status")["calibrationAvailable"] is True
    call("presets", "validate", str(root / "examples/neutral.dccw.json"))
    created = call("presets", "create", "CLI 中文预设")
    assert call("presets", "show", created["id"])["name"] == "CLI 中文预设"
    target = Path(directory) / "shared preset.json"
    call("presets", "export", created["id"], str(target))
    imported = call("presets", "import", str(target))
    assert imported["id"] != created["id"]
    assert len(call("presets", "list")["presets"]) == 2
    renamed = call("presets", "rename", imported["id"], "重命名的共享预设")
    assert renamed["id"] == imported["id"] and renamed["calibration"] == imported["calibration"]
    assert call("presets", "import", str(target)) == renamed
    copy = call("presets", "import", str(target), "--copy")
    assert copy["id"] not in {created["id"], imported["id"]}
    assert call("presets", "delete", copy["id"])["deleted"] is True
    assert call("presets", "show", copy["id"], expected=3)["code"] == "not_found"
    draft = call("presets", "create", "Scripted", "--gamma", "2.35", "--red-gain", "0.91", "--author", "CLI")
    assert draft["calibration"]["gamma"] == 2.35 and draft["calibration"]["redGain"] == 0.91 and draft["author"] == "CLI"
    call("presets", "create", "Invalid", "--blue-gain", "5", expected=2)
    concurrent_source = root / "examples/neutral.dccw.json"
    with ThreadPoolExecutor(max_workers=4) as pool:
        concurrent_imports = list(pool.map(lambda _: call("presets", "import", str(concurrent_source)), range(4)))
    assert len({item["id"] for item in concurrent_imports}) == 1
    assert len([item for item in call("presets", "list")["presets"]
                if item["id"] == concurrent_imports[0]["id"]]) == 1
    call("presets", "export", created["id"], str(target), expected=5)
    assert json.loads(target.read_text(encoding="utf-8"))["id"] == created["id"]
    call("presets", "show", "invalid-id", expected=2)
    call("presets", "import", str(Path(directory) / "missing.json"), expected=3)
    call("nonsense", expected=2)
    call("status", "--unknown-option", expected=2)
    call("startup", "status")
    assert call("startup", "last-restore")["report"] is None
    assert call("bindings", "list") == []
    plan = call("bindings", "restore", "--dry-run")
    assert plan["schemaVersion"] == 1 and plan["applied"] is False
    assert plan["reason"] == "no_bindings_enabled" and plan["recoveries"] == []
    assert not (data / "startup-status.json").exists()
    key = "path:" + "1" * 64
    binding_path = data / "display-bindings" / (hashlib.sha256(key.encode()).hexdigest().upper() + ".json")
    binding_path.parent.mkdir()
    linear = [i * 257 for i in range(256)]
    ramp = dict(red=linear, green=linear, blue=linear)
    binding_path.write_text(json.dumps(dict(
        schemaVersion=1, persistenceKey=key, identityKind="device-path", displayName="Fixture only",
        presetId=created["id"], profileName=f"DCCWPlus-{uuid4().hex}.icc", profileSha256="0" * 64,
        ramp=ramp, restoreEnabled=False, savedAt=created["createdAt"])), encoding="utf-8")
    assert call("bindings", "list")[0]["restoreEnabled"] is False
    assert call("bindings", "enable", key)["restoreEnabled"] is True
    plan = call("bindings", "restore", "--dry-run")
    assert plan["applied"] is False and plan["bindings"][0]["reasonCode"] == "display_disconnected"
    assert call("bindings", "disable", key)["restoreEnabled"] is False
    assert call("presets", "bindings", created["id"])[0]["persistenceKey"] == key
    assert call("presets", "delete", created["id"], expected=4)["code"] == "preset_in_use"
    assert call("bindings", "remove", key)["removed"] is True
    assert call("presets", "bindings", created["id"]) == []
    interrupted_id = uuid4()
    interrupted = data / "calibration-sessions" / interrupted_id.hex / "state.json"
    interrupted.parent.mkdir(parents=True)
    calibration_state = dict(
        schemaVersion=1, id=str(interrupted_id),
        original=dict(displayId="0123456789abcdef", deviceName="Unused fixture",
                      targetKey="00000000:00000001:1", topologyFingerprint="2" * 64,
                      explicitSdr=True, sharedSource=False, ramp=ramp),
        preset=created, candidate=ramp, lastWritten=ramp, status="applying",
        startedAt=created["createdAt"], expiresAt=created["createdAt"])
    document = json.dumps(calibration_state).encode()
    interrupted.write_bytes(document)
    assert call("bindings", "restore", "--dry-run")["reason"] == "recovery_required"
    pending = call("recovery", "status")["sessions"]
    assert len(pending) == 1 and pending[0]["sessionId"] == str(interrupted_id)
    assert pending[0]["kind"] == "calibration" and pending[0]["needsRecovery"] is True
    assert interrupted.read_bytes() == document
    # Simulate the journal from a prior request. Its preset is deleted and its display ID is only a fixture.
    calibration_state.update(status="reverted", reasonCode="preview_expired", preset=copy)
    terminal = json.dumps(calibration_state).encode()
    interrupted.write_bytes(terminal)
    assert call("recovery", "status")["sessions"] == []
    assert call("recovery", "run") == dict(schemaVersion=1, reason="no_recovery_needed", sessions=[])
    request = interrupted.parent / "request.json"
    reservation = json.dumps(dict(id=str(interrupted_id), displayId="0123456789abcdef",
                                  preset=copy, seconds=30, allowRetries=True)).encode()
    request.write_bytes(reservation)
    undo_plan = call("calibration", "undo", str(interrupted_id), "--dry-run")
    assert undo_plan["canPreview"] is False and undo_plan["reasonCode"] == "undo_not_confirmed"
    assert undo_plan["calibrationId"] == str(interrupted_id)
    replay_args = ("apply", copy["id"], "--display", "0123456789abcdef", "--request-id", str(interrupted_id))
    prior = call("calibration", "status", str(interrupted_id))
    with ThreadPoolExecutor(max_workers=3) as pool:
        replays = list(pool.map(lambda _: call(*replay_args), range(3)))
    assert all(result == prior and result["status"] == "reverted" for result in replays)
    assert request.read_bytes() == reservation and interrupted.read_bytes() == terminal
    assert call(*replay_args, "--seconds", "20", expected=4)["code"] == "request_id_conflict"
    assert call("apply", created["id"], "--display", "0123456789abcdef", "--request-id", str(interrupted_id),
                expected=4)["code"] == "request_id_conflict"
    assert call("apply", copy["id"], "--display", "0123456789abcdef", "--request-id", str(uuid4()), expected=3)["code"] == "not_found"
    assert call("apply", created["id"], "--display", "0123456789abcdef", "--request-id", "invalid",
                expected=2)["code"] == "invalid_arguments"
    for worker_mode in ((), ("--recover",)):
        worker = subprocess.run(
            [str(exe), "__gamma-worker", "--session", str(interrupted_id), "--data-dir", str(data),
             "--attempt", str(uuid4()), *worker_mode],
            capture_output=True, timeout=30)
        assert worker.returncode == 0 and not worker.stdout and not worker.stderr, worker
        assert request.read_bytes() == reservation and interrupted.read_bytes() == terminal
        checks += 1
    displays = call("displays", "list")
    allowed = {"sdr", "wcg", "hdr", "advanced-color", "unknown", "not-probed"}
    profile_statuses = {"read", "not-found", "too-large", "truncated", "unreadable"}
    for display in displays:
        assert display["colorState"] in allowed, display
        assert display["identityScope"] == "topology-query"
        assert display["selectedProfileScope"] in (None, "current-user", "system-wide")
        assert display["profileQueryStatus"] in (None, "read", "not-found", "unavailable")
        assert display["hdrActive"] is not True or display["colorState"] != "sdr"
        profile_name = display.get("profileName")
        profile = display.get("profile")
        if display["profileQueryStatus"] in ("not-found", "unavailable"):
            assert profile_name is None and profile is None
        if display["profileQueryStatus"] == "read":
            assert profile_name and display["selectedProfileScope"] and display["profileScope"]
        if profile_name:
            assert profile["fileName"] == profile_name
            assert profile["readStatus"] in profile_statuses
            assert isinstance(profile["signatureValid"], bool)
            if profile["readStatus"] not in ("read", "truncated"):
                assert profile["signatureValid"] is False
                assert profile["vcgtPresent"] is not True
                assert profile["trcPresent"] is not True
                assert profile["a2bPresent"] is not True
                assert profile["b2aPresent"] is not True
                assert profile["mhc2Present"] is not True
            if profile["mhc2Present"] is not None:
                assert isinstance(profile["mhc2Present"], bool), profile
        else:
            assert profile is None
    if displays:
        early = call("displays", "restore-plan", "--display", displays[0]["id"])
        assert early["canRestore"] is False
        assert early["reasonCode"] == "observation_not_found"
    observed = call("displays", "snapshot")
    assert observed["applied"] is False
    assert observed["capability"] == "read-only"
    assert observed["kind"] == "display-observation"
    assert call("displays", "snapshot", "show")["capturedAt"] == observed["capturedAt"]
    call("apply", created["id"], "--display", "missing-monitor", "--dry-run", expected=3)
    call("calibration", "diagnose", "--display", "missing-monitor", expected=3)
    call("displays", "restore-plan", "--display", "missing-monitor", expected=3)
    restore_reasons = {
        "observation_missing_display", "topology_changed", "sdr_path_blocked",
        "profile_not_recorded", "association_matches", "restore_backend_pending",
    }
    if displays:
        display = displays[0]["id"]
        diagnostic = call("calibration", "diagnose", "--display", display)
        assert diagnostic["displayId"] == display
        for key in ("windowsCalibrationLoaderEnabled", "driverSupportsVcgt", "logicalRampReadable"):
            assert diagnostic[key] is None or isinstance(diagnostic[key], bool), diagnostic
        if diagnostic["reasonCode"] in {"sdr_path_blocked", "display_identity_unavailable", "shared_source_blocked",
                                        "mhc2_profile_blocked", "profile_state_unavailable"}:
            assert diagnostic["logicalRampReadable"] is None
        assert "profileQueryStatus" in diagnostic and "selectedProfileScope" in diagnostic
        if diagnostic["reasonCode"] == "mhc2_profile_blocked":
            assert diagnostic["profileMhc2Present"] is True
        # Automated checks only probe the plan. Never invoke a real display preview here.
        dry_id = uuid4()
        dry = call("apply", created["id"], "--display", display, "--dry-run", "--request-id", str(dry_id), expected=(0, 4))
        assert not (data / "calibration-sessions" / dry_id.hex).exists()
        if "canPreview" in dry:
            assert dry["canPreview"] is True and dry["changesDisplay"] is False
        elif "canApply" in dry:
            assert dry["canApply"] is False and dry["reasonCode"] in {"sdr_path_blocked", "mhc2_profile_blocked"}
        else:
            assert dry["code"] in {"curve_read_failed", "display_identity_unavailable", "shared_source_blocked",
                                   "mhc2_profile_blocked", "profile_state_unavailable"}
        restore = call("displays", "restore-plan", "--display", display)
        assert restore["canRestore"] is False
        assert restore["displayId"] == display
        assert restore["reasonCode"] in restore_reasons
print(f"PASS: {checks} CLI process checks; data isolated, no calibration performed.")
