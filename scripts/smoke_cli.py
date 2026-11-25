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
    for name in ("calibration preview", "calibration undo", "profiles preview"):
        assert public_commands[name]["supportsDryRun"] is True
        assert public_commands[name]["supportsRequestId"] is True
        assert public_commands[name]["effect"] == "display-preview"
    assert public_commands["calibration confirm"]["effect"] == "display-change"
    assert public_commands["calibration confirm"]["supportsDryRun"] is False
    assert public_commands["calibration verify"]["effect"] == "read"
    for action in ("enable", "disable"):
        assert public_commands[f"startup {action}"]["effect"] == "local-write"
    assert call("calibration", "verify", str(uuid4()), expected=3)["code"] == "not_found"
    for group in ("calibration", "profiles"):
        assert public_commands[f"{group} observe"]["effect"] == "read"
        observe = (group, "observe", str(uuid4()))
        assert call(group, "observe", "invalid-session", expected=2)["code"] == "invalid_arguments"
        assert call(*observe, "--wait-seconds", "60", expected=3)["code"] == "not_found"
        for invalid_wait in ("-1", "61", "1.5"):
            assert call(*observe, "--wait-seconds", invalid_wait, expected=2)["code"] == "invalid_arguments"
    assert not data.exists(), "Help and capabilities must leave app data untouched"

    inline_preview = ("calibration", "preview", "--display", "missing-monitor", "--dry-run")
    assert call(*inline_preview, expected=3)["code"] == "not_found"
    for args, error in ((["--gamma", "1,5"], "invalid_arguments"),
                        (["--red-gain", "NaN"], "invalid_preset"),
                        (["--blue-gain", "1.6"], "invalid_preset"),
                        (["--seconds", "61"], "invalid_arguments"),
                        (["--gamma", "2.2", "--gamma", "2.3"], "invalid_arguments")):
        assert call(*inline_preview, *args, expected=2)["code"] == error
    assert call("calibration", "preview", "--dry-run", expected=2)["code"] == "invalid_arguments"
    update = ("calibration", "update", str(uuid4()))
    assert call(*update, "--original", "--adjusted", expected=2)["code"] == "invalid_arguments"
    assert call(*update, "--preset", str(uuid4()), "--red-gain", "1", expected=2)["code"] == "invalid_arguments"
    assert call(*update, "--gamma", "4", expected=2)["code"] == "invalid_preset"
    assert call(*update, "--original", expected=3)["code"] == "not_found"
    assert not data.exists(), "Inline validation and a dry run must leave app data untouched"

    before_file = Path(directory) / "校准前 capture.json"
    after_file = Path(directory) / "校准后 capture.json"
    sample = dict(displayId="0123456789abcdef", name="Fixture", isPrimary=True, colorState="sdr",
                  selectedProfileScope="current-user", profileQueryStatus="not-found",
                  curveStatus="logical_ramp_readable", logicalRampSha256="B" * 64)
    capture = dict(schemaVersion=1, capturedAt="2026-11-24T04:00:00+00:00", windowsBuild=26100,
                   osArchitecture="x64", topology="extend", topologySha256="A" * 64, displays=[sample])
    wrapped = dict(schemaVersion=1, ok=True, data=capture)
    before_file.write_text(json.dumps(wrapped), encoding="utf-8-sig")
    after_file.write_text(json.dumps(capture), encoding="utf-16")
    compare_args = ("calibration", "compare", str(before_file), str(after_file))
    assert public_commands["calibration compare"]["effect"] == "read"
    comparison = call(*compare_args)
    assert comparison["status"] == "matched" and comparison["colorAccuracyVerified"] is False
    assert comparison["sameSessionType"] is None
    assert comparison["fullDesktopCoverage"] is None
    for active_count, full_coverage in ((1, True), (2, False)):
        scoped_capture = dict(capture, activeDisplayCount=active_count)
        for path in (before_file, after_file):
            path.write_text(json.dumps(scoped_capture), encoding="utf-8")
        comparison = call(*compare_args)
        assert comparison["status"] == "matched" and comparison["fullDesktopCoverage"] is full_coverage
    secondary_sample = dict(sample, displayId="fedcba9876543210", isPrimary=False)
    full_capture = dict(capture, activeDisplayCount=2, displays=[sample, secondary_sample])
    before_file.write_text(json.dumps(full_capture), encoding="utf-8")
    comparison = call(*compare_args)
    assert comparison["status"] == "inconclusive" and comparison["fullDesktopCoverage"] is False
    empty_capture = dict(capture, activeDisplayCount=0, displays=[], topologySha256=None)
    for path in (before_file, after_file):
        path.write_text(json.dumps(empty_capture), encoding="utf-8")
    comparison = call(*compare_args)
    assert comparison["status"] == "inconclusive" and comparison["fullDesktopCoverage"] is False
    before_file.write_text(json.dumps(wrapped), encoding="utf-8-sig")
    after_file.write_text(json.dumps(dict(capture, displays=[dict(sample, logicalRampSha256="C" * 64)])), encoding="utf-8")
    comparison = call(*compare_args)
    assert comparison["status"] == "changed" and comparison["topologyStatus"] == "matched"
    assert comparison["displays"][0]["logicalRampStatus"] == "changed"
    after_file.write_text(json.dumps(dict(capture, topologySha256=None)), encoding="utf-8")
    assert call(*compare_args)["status"] == "inconclusive"
    for malformed in (dict(capture, schemaVersion=2), dict(capture, displays=[sample, sample]),
                      dict(capture, activeDisplayCount=0),
                      dict(schemaVersion=1, ok=False, data=capture)):
        after_file.write_text(json.dumps(malformed), encoding="utf-8")
        assert call(*compare_args, expected=2)["code"] == "invalid_capture"
    after_file.write_bytes(b" " * (256 * 1024 + 1))
    assert call(*compare_args, expected=2)["code"] == "invalid_capture"
    assert not data.exists(), "Comparing capture documents must leave app data untouched"

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
    startup = call("startup", "status")
    if startup["state"] == "unpackaged":
        for action in ("enable", "disable"):
            assert call("startup", action, expected=4)["code"] == "startup_unavailable"
        assert call("startup", "status") == startup
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
    saved_binding = binding_path.read_bytes()
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
    observed = call("calibration", "observe", str(interrupted_id), "--wait-seconds", "2")
    assert observed["outcome"] == "recovery_required" and observed["session"]["status"] == "applying"
    assert not interrupted.with_name("worker.lock").exists()
    assert call("bindings", "restore", "--dry-run")["reason"] == "recovery_required"
    dry_blocked_id = uuid4()
    preflight = call("apply", created["id"], "--display", "0123456789abcdef", "--dry-run",
                     "--request-id", str(dry_blocked_id), expected=4)
    assert preflight["code"] == "recovery_required"
    assert not (data / "calibration-sessions" / dry_blocked_id.hex).exists()
    pending = call("recovery", "status")["sessions"]
    assert len(pending) == 1 and pending[0]["sessionId"] == str(interrupted_id)
    assert pending[0]["kind"] == "calibration" and pending[0]["needsRecovery"] is True
    assert call("calibration", "confirm", str(interrupted_id), expected=4)["code"] == "recovery_required"
    assert not interrupted.with_name("command.json").exists()
    assert interrupted.read_bytes() == document
    # Simulate the journal from a prior request. Its preset is deleted and its display ID is only a fixture.
    calibration_state.update(status="reverted", reasonCode="preview_expired", preset=copy)
    terminal = json.dumps(calibration_state).encode()
    interrupted.write_bytes(terminal)
    assert call("calibration", "update", str(interrupted_id), "--gamma", "2.3", expected=4)["code"] == "preview_expired"
    assert call("calibration", "update", str(interrupted_id), "--preset", created["id"], expected=4)["code"] == "preview_expired"
    assert not interrupted.with_name("command.json").exists()
    assert interrupted.read_bytes() == terminal
    assert call("recovery", "status")["sessions"] == []
    assert call("recovery", "run") == dict(schemaVersion=1, reason="no_recovery_needed", sessions=[])
    request = interrupted.parent / "request.json"
    reservation = json.dumps(dict(id=str(interrupted_id), displayId="0123456789abcdef",
                                  preset=copy, seconds=30, allowRetries=True)).encode()
    request.write_bytes(reservation)
    dry_undo_id = uuid4()
    undo_plan = call("calibration", "undo", str(interrupted_id), "--dry-run", "--request-id", str(dry_undo_id))
    assert undo_plan["canPreview"] is False and undo_plan["reasonCode"] == "undo_not_confirmed"
    assert undo_plan["calibrationId"] == str(interrupted_id)
    assert not (data / "calibration-sessions" / dry_undo_id.hex).exists()
    replay_args = ("apply", copy["id"], "--display", "0123456789abcdef", "--request-id", str(interrupted_id))
    prior = call("calibration", "status", str(interrupted_id))
    assert call("calibration", "observe", str(interrupted_id)) == dict(outcome="finished", session=prior)
    verification = call("calibration", "verify", str(interrupted_id))
    assert verification["savedStatus"] == "reverted" and verification["status"] == "inconclusive"
    assert verification["reasonCode"] == "calibration_not_confirmed" and verification["capturedAt"] is None
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

    inline_id = uuid4()
    inline_directory = interrupted.parent.parent / inline_id.hex
    inline_directory.mkdir()
    inline_state = inline_directory / "state.json"
    inline_request = inline_directory / "request.json"
    inline_final = json.dumps(dict(calibration_state, id=str(inline_id))).encode()
    inline_reservation = json.dumps(dict(id=str(inline_id), displayId="0123456789abcdef", preset=copy,
                                         seconds=30, allowRetries=True, inlineParameters=True)).encode()
    inline_state.write_bytes(inline_final)
    inline_request.write_bytes(inline_reservation)
    inline_args = ("calibration", "preview", "--display", "0123456789abcdef", "--request-id", str(inline_id))
    assert call(*inline_args, "--gamma", "2.20") == call("calibration", "status", str(inline_id))
    assert call(*inline_args)["calibration"] == copy["calibration"]
    assert call(*inline_args, "--gamma", "2.3", expected=4)["code"] == "request_id_conflict"
    assert call(*inline_args, "--seconds", "20", expected=4)["code"] == "request_id_conflict"
    assert call(*inline_args[:-1], str(interrupted_id), expected=4)["code"] == "request_id_conflict"
    assert call(*replay_args[:-1], str(inline_id), expected=4)["code"] == "request_id_conflict"
    assert inline_state.read_bytes() == inline_final and inline_request.read_bytes() == inline_reservation

    saved_id = uuid4()
    saved_state = data / "calibration-sessions" / saved_id.hex / "state.json"
    saved_state.parent.mkdir()
    pending_preset = call("presets", "create", "Pending completion fixture")
    confirmed = dict(calibration_state, id=str(saved_id), status="confirmed", reasonCode="confirmed", preset=pending_preset,
                     original=dict(calibration_state["original"], persistenceKey=key),
                     installedProfile=f"DCCWPlus-{saved_id.hex}.icc", profileCommitStarted=True, curveReadbackVerified=True,
                     bindingStatus="pending", bindingIntent=dict(previousProfileName=None))
    saved_state.write_text(json.dumps(confirmed), encoding="utf-8")
    observed = call("calibration", "observe", str(saved_id))
    assert observed["outcome"] == "recovery_required" and observed["session"]["bindingStatus"] == "pending"
    pending = call("recovery", "status")["sessions"]
    assert pending == [dict(sessionId=str(saved_id), kind="binding", status="pending",
                            reasonCode="binding_pending", needsRecovery=True)]
    assert call("bindings", "restore", "--dry-run")["reason"] == "no_bindings_enabled"
    verification = call("calibration", "verify", str(saved_id))
    assert verification["reasonCode"] in {"display_disconnected", "remote_session_blocked"}
    assert verification["capturedAt"] is not None
    interrupted.write_text(json.dumps(dict(calibration_state, id=str(interrupted_id), status="previewing")), encoding="utf-8")
    gamma_before = interrupted.read_bytes()
    saved_before = saved_state.read_bytes()
    verification = call("calibration", "verify", str(saved_id))
    assert verification["savedStatus"] == "confirmed" and verification["status"] == "inconclusive"
    assert verification["reasonCode"] == "recovery_required" and verification["capturedAt"] is None
    assert verification["profileSelectionMatches"] is None and verification["logicalCurveMatches"] is None
    assert interrupted.read_bytes() == gamma_before and saved_state.read_bytes() == saved_before
    undo_probe_id, profile_probe_id = uuid4(), uuid4()
    undo_plan = call("calibration", "undo", str(saved_id), "--dry-run", "--request-id", str(undo_probe_id))
    assert undo_plan["canPreview"] is False and undo_plan["reasonCode"] == "recovery_required"
    assert call("profiles", "preview", "fixture.icc", "--display", "0123456789abcdef", "--dry-run",
                "--request-id", str(profile_probe_id), expected=4)["code"] == "recovery_required"
    assert not (data / "calibration-sessions" / undo_probe_id.hex).exists()
    assert not (data / "profile-previews" / profile_probe_id.hex).exists()
    assert call("calibration", "recover", str(saved_id), expected=4)["code"] == "recovery_required"
    blocked_report = call("recovery", "run")
    assert blocked_report["reason"] == "recovery_required" and pending[0] in blocked_report["sessions"]
    interrupted.write_bytes(terminal)
    assert call("calibration", "recover", str(saved_id), expected=4)["code"] == "display_disconnected"
    report = call("recovery", "run")
    assert report["reason"] == "recovery_required" and report["sessions"][0]["kind"] == "binding"
    assert call("presets", "delete", pending_preset["id"])["deleted"] is True
    retired = call("calibration", "status", str(saved_id))
    assert retired["status"] == "confirmed" and retired["bindingReasonCode"] == "preset_removed_by_user"

    binding_path.write_bytes(saved_binding)
    saved_state.write_text(json.dumps(dict(confirmed, preset=created)), encoding="utf-8")
    assert call("bindings", "remove", key)["removed"] is True
    retired = call("calibration", "status", str(saved_id))
    assert retired["bindingStatus"] == "unchanged" and retired["bindingReasonCode"] == "binding_removed_by_user"
    assert call("recovery", "status")["sessions"] == []

    undo_id, source_id = uuid4(), uuid4()
    undo_state = data / "calibration-sessions" / undo_id.hex / "state.json"
    undo_state.parent.mkdir()
    active_display_id = "fedcba9876543210"
    undo_original = dict(calibration_state["original"], displayId=active_display_id)
    undo_state.write_text(json.dumps(dict(calibration_state, id=str(undo_id), undoOf=str(source_id),
                                          original=undo_original)), encoding="utf-8")
    undo_request = undo_state.with_name("request.json")
    undo_reservation = json.dumps(dict(id=str(undo_id), displayId="0123456789abcdef", seconds=30,
                                       allowRetries=True, undoOf=str(source_id))).encode()
    undo_request.write_bytes(undo_reservation)
    undo_args = ("calibration", "undo", str(source_id), "--request-id", str(undo_id))
    replayed_undo = call(*undo_args)
    assert replayed_undo == call("calibration", "status", str(undo_id)) and replayed_undo["displayId"] == active_display_id
    assert call(*undo_args, "--seconds", "20", expected=4)["code"] == "request_id_conflict"
    assert call(*replay_args[:-1], str(undo_id), expected=4)["code"] == "request_id_conflict"
    assert undo_request.read_bytes() == undo_reservation

    profile_id = uuid4()
    profile_display = "fixture-disconnected"
    profile_state = data / "profile-previews" / profile_id.hex / "state.json"
    profile_state.parent.mkdir(parents=True)
    original_profile, candidate_profile = "original.icc", "candidate.icc"
    original = dict(displayId=profile_display, targetKey="fixture", sourceId=1, topologyFingerprint="3" * 64,
                    currentProfile=original_profile, scope="current-user", explicitSdr=True, sharedSource=False,
                    profiles=[dict(fileName=original_profile, sha256="A" * 64, canSelect=True),
                              dict(fileName=candidate_profile, sha256="B" * 64, canSelect=True)])
    profile_journal = dict(schemaVersion=1, id=str(profile_id), original=original,
                           candidateProfile=candidate_profile, candidateSha256="B" * 64,
                           expiresAt=created["createdAt"], status="reverted", reasonCode="preview_expired")
    profile_state.write_text(json.dumps(profile_journal), encoding="utf-8")
    profile_request = profile_state.with_name("request.json")
    profile_reservation = json.dumps(dict(id=str(profile_id), displayId=profile_display, candidateProfile=candidate_profile,
                                          seconds=30, allowRetries=True)).encode()
    profile_request.write_bytes(profile_reservation)
    profile_args = ("profiles", "preview", candidate_profile, "--display", profile_display, "--request-id", str(profile_id))
    prior = call("profiles", "status", str(profile_id))
    assert all("mhc2Present" not in item for item in prior["original"]["profiles"])
    assert call(*profile_args) == prior
    assert call("profiles", "observe", str(profile_id)) == dict(outcome="finished", session=prior)
    assert call("profiles", "revert", str(profile_id)) == prior
    assert call("profiles", "confirm", str(profile_id), expected=4)["code"] == "preview_expired"
    assert call(*profile_args, "--seconds", "20", expected=4)["code"] == "request_id_conflict"
    assert call(*profile_args, "--dry-run", expected=4)["code"] == "display_disconnected"
    assert profile_request.read_bytes() == profile_reservation

    inspected_original = dict(original, profiles=[dict(original["profiles"][0], mhc2Present=False),
                                                  dict(original["profiles"][1], mhc2Present=True)])
    interrupted_profile = json.dumps(dict(profile_journal, status="previewing", original=inspected_original)).encode()
    profile_state.write_bytes(interrupted_profile)
    verification = call("calibration", "verify", str(saved_id))
    assert verification["reasonCode"] == "recovery_required" and verification["capturedAt"] is None
    assert profile_state.read_bytes() == interrupted_profile
    assert call("profiles", "preview", "fixture.icc", "--display", "0123456789abcdef", "--dry-run",
                expected=4)["code"] == "recovery_required"
    undo_plan = call("calibration", "undo", str(saved_id), "--dry-run")
    assert undo_plan["canPreview"] is False and undo_plan["reasonCode"] == "recovery_required"
    observed = call("profiles", "observe", str(profile_id))
    assert observed["outcome"] == "recovery_required" and observed["session"]["status"] == "previewing"
    assert [item["mhc2Present"] for item in observed["session"]["original"]["profiles"]] == [False, True]
    for action in ("confirm", "revert"):
        assert call("profiles", action, str(profile_id), expected=4)["code"] == "recovery_required"
        assert not profile_state.with_name(f"{action}.request").exists()
    assert profile_state.read_bytes() == interrupted_profile
    pending_profile = dict(profile_journal, status="recovery-pending", reasonCode="display_disconnected")
    profile_state.write_text(json.dumps(pending_profile), encoding="utf-8")
    for action in ("confirm", "revert"):
        assert call("profiles", action, str(profile_id), expected=4)["code"] == "display_disconnected"
        assert not profile_state.with_name(f"{action}.request").exists()
    failure = call("profiles", "recover", str(profile_id), expected=4)["code"]
    assert failure in {"display_disconnected", "remote_session_blocked"}
    pending = call("profiles", "status", str(profile_id))
    assert pending["status"] == "recovery-pending" and pending["reasonCode"] == failure

    prepared_gamma = dict(calibration_state, status="prepared", reasonCode="prepared")
    prepared_profile = dict(profile_journal, status="prepared", reasonCode="prepared")
    interrupted.write_text(json.dumps(prepared_gamma), encoding="utf-8")
    profile_state.write_text(json.dumps(prepared_profile), encoding="utf-8")
    verification = call("calibration", "verify", str(saved_id))
    assert verification["reasonCode"] in {"display_disconnected", "remote_session_blocked"}
    assert verification["capturedAt"] is not None
    recovered_gamma = call("calibration", "recover", str(interrupted_id))
    recovered_profile = call("profiles", "recover", str(profile_id))
    for recovered, verified_field in ((recovered_gamma, "curveReadbackVerified"), (recovered_profile, "associationVerified")):
        assert recovered["status"] == "reverted" and recovered["reasonCode"] == "preview_not_applied"
        assert recovered[verified_field] is False and recovered["needsRecovery"] is False
    interrupted.write_text(json.dumps(prepared_gamma), encoding="utf-8")
    profile_state.write_text(json.dumps(prepared_profile), encoding="utf-8")
    report = call("recovery", "run")
    assert report["reason"] == "recovery_finished" and len(report["sessions"]) == 2
    assert {session["kind"] for session in report["sessions"]} == {"calibration", "profile"}
    assert all(session["status"] == "reverted" and session["reasonCode"] == "preview_not_applied"
               and not session["needsRecovery"] for session in report["sessions"])
    assert call("recovery", "status")["sessions"] == []
    assert request.read_bytes() == reservation and profile_request.read_bytes() == profile_reservation

    committed_profile = dict(profile_journal, status="confirmed", reasonCode="confirmed", associationVerified=True)
    profile_state.write_text(json.dumps(committed_profile), encoding="utf-8")
    committed = call("profiles", "status", str(profile_id))
    assert call("profiles", "confirm", str(profile_id)) == committed
    assert call("profiles", "recover", str(profile_id)) == committed
    assert call("profiles", "revert", str(profile_id), expected=4)["code"] == "confirmed"
    assert not profile_state.with_name("revert.request").exists()

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
    assert call("apply", created["id"], "--display", "missing-monitor", expected=3)["code"] == "not_found"
    rejected_profile_id = uuid4()
    rejected_profile = call("profiles", "preview", "candidate.icc", "--display", "missing-monitor",
                            "--request-id", str(rejected_profile_id), expected=4)
    assert rejected_profile["code"] == "display_disconnected"
    assert not (data / "profile-previews" / rejected_profile_id.hex / "state.json").exists()
    call("calibration", "diagnose", "--display", "missing-monitor", expected=3)
    call("calibration", "capture", "--display", "missing-monitor", expected=3)
    call("displays", "restore-plan", "--display", "missing-monitor", expected=3)
    restore_reasons = {
        "observation_missing_display", "topology_changed", "sdr_path_blocked",
        "profile_not_recorded", "association_matches", "restore_backend_pending",
    }
    if displays:
        display = displays[0]["id"]
        capture = call("calibration", "capture", "--display", display, expected=(0, 4))
        if "displays" in capture:
            assert capture["windowsBuild"] > 0
            assert isinstance(capture["isRemoteSession"], bool)
            assert capture["activeDisplayCount"] >= 1
            assert len(capture["displays"]) == 1 and capture["displays"][0]["displayId"] == display
            sample = capture["displays"][0]
            for key in ("profileSha256", "logicalRampSha256"):
                value = sample[key]
                assert value is None or (len(value) == 64 and int(value, 16) >= 0)
            assert (sample["curveStatus"] == "logical_ramp_readable") == (sample["logicalRampSha256"] is not None)
            if capture["isRemoteSession"]:
                assert sample["curveStatus"] == "remote_session_blocked"
        else:
            assert capture["code"] == "capture_state_changed"
        diagnostic = call("calibration", "diagnose", "--display", display)
        assert diagnostic["displayId"] == display
        for key in ("windowsCalibrationLoaderEnabled", "driverSupportsVcgt", "logicalRampReadable"):
            assert diagnostic[key] is None or isinstance(diagnostic[key], bool), diagnostic
        if diagnostic["reasonCode"] in {"remote_session_blocked", "sdr_path_blocked", "display_identity_unavailable", "shared_source_blocked",
                                        "mhc2_profile_blocked", "profile_state_unavailable"}:
            assert diagnostic["logicalRampReadable"] is None
        assert "profileQueryStatus" in diagnostic and "selectedProfileScope" in diagnostic
        if diagnostic["reasonCode"] == "mhc2_profile_blocked":
            assert diagnostic["profileMhc2Present"] is True
        # Automated checks only probe the plan. Never invoke a real display preview here.
        dry_id = uuid4()
        dry = call("apply", created["id"], "--display", display, "--dry-run", "--request-id", str(dry_id), expected=(0, 4))
        assert not (data / "calibration-sessions" / dry_id.hex).exists()
        preflight_errors = {"remote_session_blocked", "curve_read_failed", "display_identity_unavailable", "shared_source_blocked",
                            "mhc2_profile_blocked", "profile_state_unavailable", "profile_scope_unavailable",
                            "profile_device_unavailable", "profile_file_unavailable", "icc_base_unsupported",
                            "curve_candidate_invalid", "external_profile_change", "preview_busy", "recovery_required"}
        if "canPreview" in dry:
            assert dry["canPreview"] is True and dry["changesDisplay"] is False
        elif "canApply" in dry:
            assert dry["canApply"] is False and dry["reasonCode"] in {"sdr_path_blocked", "mhc2_profile_blocked"}
        else:
            assert dry["code"] in preflight_errors
        inline_plan_id = uuid4()
        inline_plan = call("calibration", "preview", "--display", display, "--gamma", "2.35", "--red-gain", "0.95",
                           "--dry-run", "--request-id", str(inline_plan_id), expected=(0, 4))
        assert not (data / "calibration-sessions" / inline_plan_id.hex).exists()
        if "code" in inline_plan:
            assert inline_plan["code"] in preflight_errors
        else:
            assert inline_plan["displayId"] == display and inline_plan["changesDisplay"] is False
            assert inline_plan["calibration"] == dict(colorMode="sdr", gamma=2.35, redGain=.95, greenGain=1, blueGain=1)
            assert inline_plan["canPreview"] == (inline_plan["reasonCode"] is None)
            assert inline_plan["reasonCode"] in {None, "sdr_path_blocked", "mhc2_profile_blocked"}
        restore = call("displays", "restore-plan", "--display", display)
        assert restore["canRestore"] is False
        assert restore["displayId"] == display
        assert restore["reasonCode"] in restore_reasons
print(f"PASS: {checks} CLI process checks; data isolated, no calibration performed.")
