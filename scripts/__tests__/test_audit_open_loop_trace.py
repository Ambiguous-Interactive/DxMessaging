#!/usr/bin/env python3
"""Fixed-arrival trace reconciliation for issue #512."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "unity" / "audit_open_loop_trace.py"
SPEC = importlib.util.spec_from_file_location("audit_open_loop_trace", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
BASE = 9007199254740993000000


def schedule() -> dict:
    return {
        "schemaVersion": 1,
        "traceId": "trace-1",
        "frequencyHz": "10000000",
        "horizonStartTick": str(BASE),
        "horizonEndTick": str(BASE + 10000000),
        "arrivals": [
            {"id": "a", "arrivalTick": str(BASE)},
            {"id": "b", "arrivalTick": str(BASE + 5000000)},
            {"id": "c", "arrivalTick": str(BASE + 5000000)},
        ],
    }


def raw_schedule(value: dict | None = None) -> bytes:
    return json.dumps(value if value is not None else schedule(), separators=(",", ":")).encode()


def observations(raw: bytes | None = None) -> dict:
    raw = raw if raw is not None else raw_schedule()
    return {
        "schemaVersion": 1,
        "scheduleSha256": hashlib.sha256(raw).hexdigest(),
        "events": [
            {"id": "c", "startTick": str(BASE + 12000000), "completionTick": str(BASE + 13000000)},
            {"id": "a", "startTick": str(BASE + 1), "completionTick": str(BASE + 100)},
            {"id": "b", "startTick": str(BASE + 5000000), "completionTick": str(BASE + 11000000)},
        ],
    }


def runner_result(pairs: list[tuple[bytes, dict]] | None = None) -> dict:
    pairs = pairs if pairs is not None else [(raw_schedule(), observations())]
    output = "\n".join(
        line
        for raw, observed in pairs
        for line in (
            "DXM_OPEN_LOOP_SCHEDULE_V1 " + raw.decode(),
            "DXM_OPEN_LOOP_OBSERVATIONS_V1 " + json.dumps(observed),
        )
    )
    return {
        "passCount": 1,
        "failCount": 0,
        "skipCount": 0,
        "inconclusiveCount": 0,
        "nodes": [{"name": "Fixture.Case", "isSuite": False, "status": "Passed", "output": output}],
        "failures": [],
    }


def regular_trace() -> tuple[bytes, dict, dict]:
    planned = schedule()
    planned["horizonEndTick"] = str(BASE + 15000000)
    planned["arrivals"][2]["arrivalTick"] = str(BASE + 10000000)
    for index, arrival in enumerate(planned["arrivals"]):
        arrival["id"] = str(index)
    raw = raw_schedule(planned)
    observed = observations(raw)
    for event in observed["events"]:
        event["id"] = {"a": "0", "b": "1", "c": "2"}[event["id"]]
    trace_plan = {
        "traceId": "trace-1",
        "fixture": "Fixture.Case",
        "frequencyHz": "10000000",
        "horizonSpanTicks": "15000000",
        "arrivalCount": 3,
        "arrivalStepTicks": "5000000",
    }
    return raw, observed, trace_plan


def tail_runner_result() -> dict:
    pairs = []
    for trace_id, latencies in (("editor-tail-baseline", (1, 2, 3)), ("editor-tail-stalled", (1, 2, 30))):
        planned = {
            "schemaVersion": 1,
            "traceId": trace_id,
            "frequencyHz": "10000000",
            "horizonStartTick": str(BASE),
            "horizonEndTick": str(BASE + 100),
            "arrivals": [{"id": str(index), "arrivalTick": str(BASE)} for index in range(3)],
        }
        raw = raw_schedule(planned)
        observed = {
            "schemaVersion": 1,
            "scheduleSha256": hashlib.sha256(raw).hexdigest(),
            "events": [
                {"id": str(index), "startTick": str(BASE), "completionTick": str(BASE + ticks)}
                for index, ticks in enumerate(latencies)
            ],
        }
        pairs.append((raw, observed))
    result = runner_result(pairs)
    result["nodes"][0]["output"] += (
        "\nDXM_OPEN_LOOP_TAIL_EFFECT_V1 p99ShiftTicks=27 "
        "meanShiftNumeratorTicks=27 meanShiftDenominator=3"
    )
    return result


def clock_runner_result() -> dict:
    def marker(control: str, calls: int, offset: int) -> str:
        pairs = ";".join(f"{index * 10}:{index * 10 + (index + offset) % 3}"
                         for index in range(256))
        return f"DXM_LATENCY_CLOCK_V1 control={control} frequencyHz=10000000 callbackCalls={calls} pairs={pairs}"
    return {
        "passCount": 1, "failCount": 0, "skipCount": 0, "inconclusiveCount": 0,
        "nodes": [{
            "name": "TimerOnlyAndEmptyCallbackRetainMonotonicRawPairs", "isSuite": False,
            "status": "Passed", "output": "\n".join((marker("timer-only", 0, 0), marker("empty-callback", 256, 1))),
        }],
        "failures": [],
    }


def clock_batch_runner_result() -> dict:
    pairs = ";".join(f"{index * 100}:{index * 100 + 20 + index % 3}" for index in range(128))
    return {
        "passCount": 1, "failCount": 0, "skipCount": 0, "inconclusiveCount": 0,
        "nodes": [{
            "name": "TimestampCallBatchesRetainRawWindowPairs", "isSuite": False,
            "status": "Passed", "output": (
                "DXM_LATENCY_CLOCK_BATCH_V1 frequencyHz=10000000 "
                f"callsPerBatch=4096 batchCount=128 pairs={pairs}"
            ),
        }],
        "failures": [],
    }


def clock_capture_fixture(batch: bool) -> tuple[dict[str, bytes], str]:
    name = "clock.json"
    guid = "12345678-1234-1234-1234-123456789abc"
    path = "Packages/com.wallstop-studios.dxmessaging/.artifacts/clock.json"
    raw = json.dumps(clock_batch_runner_result() if batch else clock_runner_result()).encode()
    run = json.dumps({"runGuid": guid, "resultPath": path}).encode()
    cleanup = json.dumps({
        "observedUtc": "2026-09-17T00:00:00Z", "observationError": "",
        "frameworkActive": False, "playing": False, "compiling": False, "updating": False,
        "mainStage": True, "activeScene": "Assets/Saved.unity",
        "scenes": [{"path": "Assets/Saved.unity", "dirty": False, "loaded": True}],
        "runGuid": guid, "resultPath": path, "ownedResultPath": path,
        "legacyObserverResultPath": "", "frameworkErrors": "",
    }).encode()
    commit = "a" * 40
    environment = {
        "schemaVersion": 1, "claimClass": "descriptive-only",
        "evidenceClass": "editor-latency-clock-batch-control" if batch else "editor-latency-clock-control",
        "executionScope": "Editor PlayMode Mono", "sourceCommit": commit,
        "sourceTree": "b" * 40, "runtimeTree": "c" * 40,
        "unityVersion": "6000.4.6f1", "runGuid": guid,
    }
    audit = MODULE.audit_clock_batch_capture if batch else MODULE.audit_clock_capture
    replay = audit(raw, run, cleanup, b"done", b"done", name)
    return {
        "clock-environment.json": json.dumps(environment).encode(),
        "clock-replay.json": json.dumps(replay).encode(), name: raw,
        name + ".run.json": run, name + ".cleanup.json": cleanup,
        name + ".status": b"done", name + ".cleanup.status": b"done",
    }, commit


class AuditOpenLoopTraceTests(unittest.TestCase):
    def test_clock_content_map_and_bundle_replay_bind_both_probe_classes(self) -> None:
        bundle_tool = SCRIPT.with_name("perf-evidence-bundle.js")
        for batch in (False, True):
            with self.subTest(batch=batch):
                contents, commit = clock_capture_fixture(batch)
                reduced = MODULE.reduce_clock_capture_contents(contents, commit)
                self.assertEqual(reduced["replay"]["runGuid"], "12345678-1234-1234-1234-123456789abc")
                with self.assertRaisesRegex(ValueError, "source"):
                    MODULE.reduce_clock_capture_contents(contents, "d" * 40)
                with self.assertRaisesRegex(ValueError, "missing"):
                    MODULE.reduce_clock_capture_contents({k: v for k, v in contents.items() if k != "clock.json.cleanup.status"}, commit)
                with self.assertRaisesRegex(ValueError, "unexpected"):
                    MODULE.reduce_clock_capture_contents({**contents, "extra.txt": b"ignored"}, commit)
                with self.assertRaisesRegex(ValueError, "status"):
                    MODULE.reduce_clock_capture_contents({**contents, "clock.json.status": b"running"}, commit)
                changed = {**json.loads(contents["clock-environment.json"]), "runGuid": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}
                with self.assertRaisesRegex(ValueError, "GUID"):
                    MODULE.reduce_clock_capture_contents({**contents, "clock-environment.json": json.dumps(changed).encode()}, commit)
                changed = {**json.loads(contents["clock-replay.json"]), "frequencyHz": "1"}
                with self.assertRaisesRegex(ValueError, "retained"):
                    MODULE.reduce_clock_capture_contents({**contents, "clock-replay.json": json.dumps(changed).encode()}, commit)
                with self.assertRaisesRegex(ValueError, "one pass"):
                    MODULE.reduce_clock_capture_contents({**contents, "clock.json": contents["clock.json"].replace(b'"passCount": 1', b'"passCount": 2')}, commit)
                with tempfile.TemporaryDirectory() as temporary:
                    root = Path(temporary)
                    for filename, data in contents.items():
                        (root / filename).write_bytes(data)
                    command = [
                        "node", str(bundle_tool), "seal", str(root), "--experiment-id", "clock-fixture",
                        "--artifact-class", "editor-latency-clock-capture",
                        "--reducer", "editor-latency-clock-capture-v1", "--source-commit", commit,
                    ]
                    sealed = subprocess.run(command, capture_output=True, text=True)
                    self.assertEqual(sealed.returncode, 0, sealed.stderr)
                    replayed = subprocess.run(
                        ["node", str(bundle_tool), "replay", str(root / "evidence-manifest.json")],
                        capture_output=True, text=True,
                    )
                    self.assertEqual(replayed.returncode, 0, replayed.stderr)
                    (root / "clock.json").write_bytes(contents["clock.json"].replace(b'"passCount": 1', b'"passCount": 2'))
                    corrupted = subprocess.run(
                        ["node", str(bundle_tool), "replay", str(root / "evidence-manifest.json")],
                        capture_output=True, text=True,
                    )
                    self.assertNotEqual(corrupted.returncode, 0)
                    self.assertIn("hashes to", corrupted.stderr)

    def test_batched_clock_windows_rederive_exact_total_and_reject_faults(self) -> None:
        result = clock_batch_runner_result()
        reduced = MODULE.audit_clock_batch_result(json.dumps(result).encode())
        self.assertEqual(reduced["totalCalls"], 524288)
        self.assertEqual(reduced["totalElapsedTicks"], "2687")
        self.assertEqual(reduced["zeroWindowCount"], 0)
        self.assertEqual(reduced["windowQuantilesTicks"], {"p50": "21", "p95": "22", "p99": "22"})
        output = result["nodes"][0]["output"]
        for changed in (
            "",
            output + "\n" + output,
            output.replace("callsPerBatch=4096", "callsPerBatch=4095"),
            output.replace("batchCount=128", "batchCount=127"),
            output.replace("frequencyHz=10000000", "frequencyHz=0"),
            output.replace("pairs=0:20;100:121", "pairs=0:20;0:121"),
            output.replace("pairs=0:20;100:121", "pairs=0:20;100:99"),
            output.replace("pairs=0:20;100:121;", "pairs=0:20;", 1),
        ):
            with self.subTest(change=changed[:80]), self.assertRaises(ValueError):
                MODULE.audit_clock_batch_result(json.dumps({**result, "nodes": [{**result["nodes"][0], "output": changed}]}).encode())
        with tempfile.TemporaryDirectory() as temporary:
            raw_path = Path(temporary) / "clock-batch.json"
            raw_path.write_bytes(json.dumps(result).encode())
            command = subprocess.run(
                [sys.executable, str(SCRIPT), "--clock-batch-result", str(raw_path)],
                capture_output=True, text=True,
            )
            self.assertEqual(command.returncode, 0, command.stderr)
            self.assertEqual(json.loads(command.stdout), reduced)

    def test_clock_controls_rederive_exact_elapsed_ticks_and_reject_faults(self) -> None:
        result = clock_runner_result()
        reduced = MODULE.audit_clock_result(json.dumps(result).encode())
        self.assertEqual(reduced["sampleCountPerControl"], 256)
        self.assertEqual([control["callbackCalls"] for control in reduced["controls"]], [0, 256])
        self.assertEqual(reduced["controls"][0]["zeroElapsedCount"], 86)
        self.assertEqual(reduced["controls"][0]["elapsedQuantiles"]["p99"]["ticks"], "2")
        output = result["nodes"][0]["output"]
        for changed in (
            output.splitlines()[0],
            output + "\n" + output.splitlines()[0],
            output.replace("callbackCalls=256", "callbackCalls=255"),
            output.replace("frequencyHz=10000000 callbackCalls=256", "frequencyHz=10000001 callbackCalls=256"),
            output.replace("pairs=0:0;10:11", "pairs=0:1;0:11"),
            output.replace("pairs=0:0;10:11", "pairs=0:1;10:9"),
            output.replace("pairs=0:0;10:11;", "pairs=0:0;", 1),
        ):
            with self.subTest(change=changed[:80]), self.assertRaises(ValueError):
                MODULE.audit_clock_result(json.dumps({**result, "nodes": [{**result["nodes"][0], "output": changed}]}).encode())
        with tempfile.TemporaryDirectory() as temporary:
            raw_path = Path(temporary) / "clock.json"
            raw_path.write_bytes(json.dumps(result).encode())
            command = subprocess.run(
                [sys.executable, str(SCRIPT), "--clock-result", str(raw_path)],
                capture_output=True, text=True,
            )
            self.assertEqual(command.returncode, 0, command.stderr)
            self.assertEqual(json.loads(command.stdout), reduced)

    def test_content_map_rederives_capture_and_binds_source(self) -> None:
        raw, observed, planned = regular_trace()
        result = json.dumps(runner_result([(raw, observed)])).encode()
        plan = json.dumps({"schemaVersion": 1, "traces": [planned]}).encode()
        guid = "12345678-1234-1234-1234-123456789abc"
        name = "result.json"
        path = "Packages/com.wallstop-studios.dxmessaging/.artifacts/result.json"
        run = {"runGuid": guid, "resultPath": path}
        cleanup = {
            "observedUtc": "2026-09-17T00:00:00Z", "observationError": "",
            "frameworkActive": False, "playing": False, "compiling": False, "updating": False,
            "mainStage": True, "activeScene": "Assets/Saved.unity",
            "scenes": [{"path": "Assets/Saved.unity", "dirty": False, "loaded": True}],
            "runGuid": guid, "resultPath": path, "ownedResultPath": path,
            "legacyObserverResultPath": "", "frameworkErrors": "",
        }
        commit = "a" * 40
        environment = {
            "claimClass": "descriptive-only", "evidenceClass": "editor-open-loop-protocol-screen",
            "executionScope": "Editor PlayMode Mono", "planSha256": hashlib.sha256(plan).hexdigest(),
            "runGuid": guid, "runtimeTree": "b" * 40, "schemaVersion": 1,
            "sourceCommit": commit, "sourceTree": "c" * 40, "unityVersion": "6000.4.6f1",
        }
        replay = MODULE.audit_unity_capture(
            result, [], plan, json.dumps(run).encode(), json.dumps(cleanup).encode(),
            b"done", b"done", name,
        )
        contents = {
            "capture-environment.json": json.dumps(environment).encode(),
            "capture-replay.json": json.dumps(replay).encode(),
            "open-loop-editor-plan.json": plan, name: result,
            name + ".run.json": json.dumps(run).encode(),
            name + ".cleanup.json": json.dumps(cleanup).encode(),
            name + ".status": b"done", name + ".cleanup.status": b"done",
        }
        self.assertEqual(MODULE.reduce_capture_contents(contents, commit)["replay"], replay)
        with self.assertRaisesRegex(ValueError, "source"):
            MODULE.reduce_capture_contents(contents, "d" * 40)
        with self.assertRaisesRegex(ValueError, "missing"):
            MODULE.reduce_capture_contents({key: value for key, value in contents.items() if key != name + ".cleanup.status"}, commit)
        with self.assertRaisesRegex(ValueError, "unexpected"):
            MODULE.reduce_capture_contents({**contents, "extra.txt": b"ignored"}, commit)
        changed = {**replay, "traceCount": 2}
        with self.assertRaisesRegex(ValueError, "retained"):
            MODULE.reduce_capture_contents({**contents, "capture-replay.json": json.dumps(changed).encode()}, commit)
        changed = {**environment, "runGuid": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}
        with self.assertRaisesRegex(ValueError, "GUID"):
            MODULE.reduce_capture_contents({**contents, "capture-environment.json": json.dumps(changed).encode()}, commit)

        bundle_tool = SCRIPT.with_name("perf-evidence-bundle.js")
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for filename, data in contents.items():
                (root / filename).write_bytes(data)
            command = [
                "node", str(bundle_tool), "seal", str(root), "--experiment-id", "open-loop-fixture",
                "--artifact-class", "open-loop-editor-capture", "--reducer", "open-loop-editor-capture-v1",
                "--source-commit", commit,
            ]
            sealed = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(sealed.returncode, 0, sealed.stderr)
            replayed = subprocess.run(
                ["node", str(bundle_tool), "replay", str(root / "evidence-manifest.json")],
                capture_output=True, text=True,
            )
            self.assertEqual(replayed.returncode, 0, replayed.stderr)
            (root / name).write_bytes(result.replace(b'"passCount": 1', b'"passCount": 2'))
            corrupted = subprocess.run(
                ["node", str(bundle_tool), "replay", str(root / "evidence-manifest.json")],
                capture_output=True, text=True,
            )
            self.assertNotEqual(corrupted.returncode, 0)
            self.assertIn("hashes to", corrupted.stderr)
            (root / "evidence-manifest.json").unlink()
            (root / name).write_bytes(result)
            (root / (name + ".cleanup.status")).unlink()
            missing = subprocess.run(command, capture_output=True, text=True)
            self.assertNotEqual(missing.returncode, 0)
            self.assertIn("missing", missing.stderr)
            status_file = root / (name + ".cleanup.status")
            status_file.write_bytes(b"Machine ID: FAKEmachineID000000000000=\n")
            private = subprocess.run(command, capture_output=True, text=True)
            self.assertNotEqual(private.returncode, 0)
            self.assertIn("scrub it before sealing", private.stderr)
            status_file.write_bytes(b"done")
            (root / "capture-replay.json").write_bytes(json.dumps({**replay, "traceCount": 2}).encode())
            mismatched = subprocess.run(command, capture_output=True, text=True)
            self.assertNotEqual(mismatched.returncode, 0)
            self.assertIn("retained", mismatched.stderr)

    def test_capture_sidecars_bind_run_and_terminal_clean_scene(self) -> None:
        raw = json.dumps(runner_result()).encode()
        guid = "12345678-1234-1234-1234-123456789abc"
        path = r"C:\lab\result.json"
        run = {"runGuid": guid, "resultPath": path}
        cleanup = {
            "observedUtc": "2026-09-17T00:00:00Z",
            "observationError": "",
            "frameworkActive": False,
            "playing": False,
            "compiling": False,
            "updating": False,
            "mainStage": True,
            "activeScene": "Assets/Saved.unity",
            "scenes": [{"path": "Assets/Saved.unity", "dirty": False, "loaded": True}],
            "runGuid": guid,
            "resultPath": path,
            "ownedResultPath": path,
            "legacyObserverResultPath": "",
            "frameworkErrors": "",
        }
        capture = lambda run_record, cleanup_record, status=b"done", terminal=b"done": MODULE.audit_unity_capture(
            raw, ["trace-1"], None, json.dumps(run_record).encode(), json.dumps(cleanup_record).encode(),
            status, terminal, "result.json",
        )
        replay = capture(run, cleanup)
        self.assertEqual(replay["runGuid"], guid)
        self.assertEqual(replay["runRecordSha256"], hashlib.sha256(json.dumps(run).encode()).hexdigest())
        self.assertEqual(replay["traceCount"], 1)
        clock = MODULE.audit_clock_capture(
            json.dumps(clock_runner_result()).encode(), json.dumps(run).encode(),
            json.dumps(cleanup).encode(), b"done", b"done", "result.json",
        )
        self.assertEqual(clock["runGuid"], guid)
        self.assertEqual(clock["sampleCountPerControl"], 256)
        batch = MODULE.audit_clock_batch_capture(
            json.dumps(clock_batch_runner_result()).encode(), json.dumps(run).encode(),
            json.dumps(cleanup).encode(), b"done", b"done", "result.json",
        )
        self.assertEqual(batch["runGuid"], guid)
        self.assertEqual(batch["totalCalls"], 524288)
        with self.assertRaisesRegex(ValueError, "status"):
            MODULE.audit_clock_capture(
                json.dumps(clock_runner_result()).encode(), json.dumps(run).encode(),
                json.dumps(cleanup).encode(), b"running", b"done", "result.json",
            )
        for field, value in (
            ("runGuid", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ("resultPath", r"C:\lab\other.json"),
            ("ownedResultPath", r"C:\lab\other.json"),
            ("frameworkActive", True),
            ("playing", True),
            ("compiling", True),
            ("updating", True),
            ("mainStage", False),
            ("observationError", "error"),
            ("frameworkErrors", "error"),
            ("legacyObserverResultPath", path),
            ("activeScene", "Assets/Other.unity"),
            ("scenes", [{"path": "Assets/Saved.unity", "dirty": True, "loaded": True}]),
            ("scenes", [{"path": "Assets/Saved.unity", "dirty": False, "loaded": False}]),
            ("scenes", []),
        ):
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                capture(run, {**cleanup, field: value})
        for status, terminal in ((b"running", b"done"), (b"done", b"running"), (b"done\n", b"done")):
            with self.subTest(status=status, terminal=terminal), self.assertRaises(ValueError):
                capture(run, cleanup, status, terminal)
        with self.assertRaises(ValueError):
            capture({**run, "runGuid": "not-a-guid"}, cleanup)
        with self.assertRaises(ValueError):
            MODULE.audit_unity_capture(
                raw, ["trace-1"], None, b'{"runGuid":"x","runGuid":"y"}',
                json.dumps(cleanup).encode(), b"done", b"done", "result.json",
            )
        with self.assertRaises(ValueError):
            MODULE.audit_unity_capture(
                raw, ["trace-1"], None, json.dumps(run).encode(),
                b'{"runGuid":"x","runGuid":"y"}', b"done", b"done", "result.json",
            )

    def test_tail_marker_is_recomputed_from_raw_events(self) -> None:
        result = tail_runner_result()
        expected = ["editor-tail-baseline", "editor-tail-stalled"]
        replay = MODULE.audit_unity_result(json.dumps(result).encode(), expected)
        self.assertEqual(replay["effects"], [{
            "fixture": "Fixture.Case",
            "p99ShiftTicks": "27",
            "meanShiftNumeratorTicks": "27",
            "meanShiftDenominator": "3",
        }])
        output = result["nodes"][0]["output"]
        for altered in (
            output.replace("p99ShiftTicks=27", "p99ShiftTicks=28"),
            output.replace("meanShiftNumeratorTicks=27", "meanShiftNumeratorTicks=28"),
            output.replace("meanShiftDenominator=3", "meanShiftDenominator=2"),
            output.replace("meanShiftDenominator=3", "meanShiftDenominator=03"),
            output.rsplit("\n", 1)[0],
            output + "\n" + output.splitlines()[-1],
        ):
            with self.subTest(altered=altered[-100:]), self.assertRaises(ValueError):
                changed = {**result, "nodes": [{**result["nodes"][0], "output": altered}]}
                MODULE.audit_unity_result(json.dumps(changed).encode(), expected)
        unrelated = runner_result()
        unrelated["nodes"][0]["output"] += "\n" + output.splitlines()[-1]
        with self.assertRaises(ValueError):
            MODULE.audit_unity_result(json.dumps(unrelated).encode(), ["trace-1"])

    def test_committed_plan_binds_normalized_arrivals_and_fixture(self) -> None:
        raw, observed, trace_plan = regular_trace()
        result = json.dumps(runner_result([(raw, observed)])).encode()
        plan = json.dumps({"schemaVersion": 1, "traces": [trace_plan]}).encode()
        replay = MODULE.audit_unity_result(result, [], plan)
        self.assertEqual(replay["planSha256"], hashlib.sha256(plan).hexdigest())
        self.assertEqual(replay["expectedTraceIds"], ["trace-1"])

        mutations = [
            {**trace_plan, "fixture": "Other.Case"},
            {**trace_plan, "frequencyHz": "10000001"},
            {**trace_plan, "horizonSpanTicks": "14999999"},
            {**trace_plan, "arrivalCount": 2},
            {**trace_plan, "arrivalStepTicks": "5000001"},
            {**trace_plan, "traceId": "other"},
            {**trace_plan, "arrivalCount": True},
            {**trace_plan, "arrivalStepTicks": "01"},
            {**trace_plan, "horizonSpanTicks": "10000000"},
        ]
        for changed in mutations:
            with self.subTest(changed=changed), self.assertRaises(ValueError):
                MODULE.audit_unity_result(result, [], json.dumps({"schemaVersion": 1, "traces": [changed]}).encode())
        with self.assertRaises(ValueError):
            MODULE.audit_unity_result(result, ["wrong"], plan)
        with self.assertRaises(ValueError):
            MODULE.audit_unity_result(result, [], b'{"schemaVersion":1,"schemaVersion":1}')
        with self.assertRaises(ValueError):
            MODULE.audit_unity_result(result, [], json.dumps({"schemaVersion": 1, "traces": [trace_plan, trace_plan]}).encode())

    def test_committed_plan_rejects_rebased_arrival_even_with_matching_observation_hash(self) -> None:
        raw, observed, trace_plan = regular_trace()
        changed = json.loads(raw)
        changed["arrivals"][1]["arrivalTick"] = str(BASE + 5000001)
        changed_raw = raw_schedule(changed)
        observed["scheduleSha256"] = hashlib.sha256(changed_raw).hexdigest()
        observed["events"][2]["startTick"] = str(BASE + 5000001)
        result = json.dumps(runner_result([(changed_raw, observed)])).encode()
        plan = json.dumps({"schemaVersion": 1, "traces": [trace_plan]}).encode()
        with self.assertRaisesRegex(ValueError, "planned arrival mismatch"):
            MODULE.audit_unity_result(result, [], plan)

    def test_raw_unity_result_replays_every_declared_trace(self) -> None:
        second = {**schedule(), "traceId": "trace-2"}
        raw_second = raw_schedule(second)
        result = runner_result([(raw_schedule(), observations()), (raw_second, observations(raw_second))])
        raw_result = json.dumps(result).encode()
        replay = MODULE.audit_unity_result(raw_result, ["trace-1", "trace-2"])
        self.assertEqual(replay["rawResultSha256"], hashlib.sha256(raw_result).hexdigest())
        self.assertEqual(replay["traceCount"], 2)
        self.assertEqual([item["traceId"] for item in replay["traces"]], ["trace-1", "trace-2"])
        self.assertEqual(replay["traces"][0]["fixture"], "Fixture.Case")
        self.assertEqual(replay["traces"][0]["backlogAtHorizon"], 2)
        self.assertEqual(replay["traces"][0]["completionLatencyQuantiles"]["p99"]["id"], "c")

    def test_raw_unity_result_rejects_incomplete_or_ambiguous_capture(self) -> None:
        valid = runner_result()
        marker = valid["nodes"][0]["output"]
        first_line = marker.splitlines()[0]
        failures = [
            ({**valid, "passCount": 2}, ["trace-1"]),
            ({**valid, "failCount": 1}, ["trace-1"]),
            ({**valid, "skipCount": 1}, ["trace-1"]),
            ({**valid, "inconclusiveCount": 1}, ["trace-1"]),
            ({**valid, "failures": ["failure"]}, ["trace-1"]),
            ({**valid, "nodes": [{**valid["nodes"][0], "status": "Failed"}]}, ["trace-1"]),
            ({**valid, "nodes": [{"name": "Suite", "isSuite": True, "status": "Failed", "output": ""}, valid["nodes"][0]]}, ["trace-1"]),
            ({**valid, "nodes": [{**valid["nodes"][0], "output": first_line}]}, ["trace-1"]),
            ({**valid, "nodes": [{**valid["nodes"][0], "output": marker + "\n" + first_line}]}, ["trace-1"]),
            ({**valid, "nodes": [{**valid["nodes"][0], "output": marker + "\n" + marker}]}, ["trace-1"]),
            (valid, ["missing"]),
            (valid, ["trace-1", "trace-1"]),
            (valid, []),
        ]
        for result, expected in failures:
            with self.subTest(result=result, expected=expected), self.assertRaises(ValueError):
                MODULE.audit_unity_result(json.dumps(result).encode(), expected)
        with self.assertRaises(ValueError):
            MODULE.audit_unity_result(b'{"passCount":1,"passCount":1}', ["trace-1"])

    def test_raw_unity_result_cli_requires_expected_ids(self) -> None:
        with tempfile.TemporaryDirectory(prefix="dxm-open-loop-result-") as temporary:
            path = Path(temporary) / "result.json"
            path.write_text(json.dumps(runner_result()), encoding="utf-8")
            good = subprocess.run(
                [sys.executable, str(SCRIPT), "--unity-result", str(path), "--expected-trace-id", "trace-1"],
                capture_output=True,
                text=True,
            )
            self.assertEqual(good.returncode, 0, good.stderr)
            self.assertEqual(json.loads(good.stdout)["traceCount"], 1)
            bad = subprocess.run([sys.executable, str(SCRIPT), "--unity-result", str(path)], capture_output=True, text=True)
            self.assertNotEqual(bad.returncode, 0)
            partial = subprocess.run(
                [sys.executable, str(SCRIPT), "--unity-result", str(path), "--expected-trace-id", "trace-1",
                 "--run-record", str(path)], capture_output=True, text=True,
            )
            self.assertNotEqual(partial.returncode, 0)
            self.assertIn("capture sidecars must be supplied together", partial.stderr)

    def test_exact_rates_late_completions_and_large_ticks(self) -> None:
        raw = raw_schedule()
        result = MODULE.audit(raw, observations(raw))
        self.assertEqual(result["resultClass"], "descriptive-only")
        self.assertEqual(result["scheduleSha256"], hashlib.sha256(raw).hexdigest())
        self.assertEqual(result["offeredCount"], 3)
        self.assertEqual(result["startedWithinHorizon"], 2)
        self.assertEqual(result["completedWithinHorizon"], 1)
        self.assertEqual(result["completedEventual"], 3)
        self.assertEqual(result["backlogAtHorizon"], 2)
        self.assertEqual(result["offeredPerSecond"], {"numerator": "3", "denominator": "1"})
        self.assertEqual(result["achievedWithinHorizonPerSecond"], {"numerator": "1", "denominator": "1"})
        self.assertEqual([event["id"] for event in result["events"]], ["a", "b", "c"])
        self.assertEqual(result["events"][2]["queueDelayTicks"], "7000000")
        self.assertEqual(result["events"][2]["completionLatencyTicks"], "8000000")
        self.assertEqual(result["events"][0]["arrivalTick"], str(BASE))
        self.assertEqual(result["completionLatencyQuantiles"]["p50"]["latencyTicks"], "6000000")
        self.assertEqual(result["completionLatencyQuantiles"]["p99"]["id"], "c")
        self.assertEqual(result["meanCompletionLatencyTicks"], {"numerator": "4666700", "denominator": "1"})

    def test_strict_upper_p99_selects_one_delayed_completion_in_100(self) -> None:
        planned = schedule()
        planned["frequencyHz"] = "3"
        planned["arrivals"] = [{"id": f"event-{index:03}", "arrivalTick": str(BASE)} for index in range(100)]
        raw = raw_schedule(planned)
        observed = {
            "schemaVersion": 1,
            "scheduleSha256": hashlib.sha256(raw).hexdigest(),
            "events": [
                {
                    "id": f"event-{index:03}",
                    "startTick": str(BASE),
                    "completionTick": str(BASE + (1000 if index == 99 else 1)),
                }
                for index in range(100)
            ],
        }
        result = MODULE.audit(raw, observed)
        self.assertEqual(result["completionLatencyQuantiles"]["p95"]["latencyTicks"], "1")
        self.assertEqual(result["completionLatencyQuantiles"]["p99"], {
            "id": "event-099",
            "latencyTicks": "1000",
            "latencyNanoseconds": {"numerator": "1000000000000", "denominator": "3"},
        })
        self.assertEqual(result["meanCompletionLatencyTicks"], {"numerator": "1099", "denominator": "100"})

    def test_missing_extra_duplicate_or_invalid_observation_is_red(self) -> None:
        valid = observations()
        rows = valid["events"]
        invalid = [
            {**valid, "events": rows[:-1]},
            {**valid, "events": rows + [{**rows[0], "id": "unknown"}]},
            {**valid, "events": rows + [rows[0]]},
            {**valid, "events": [{**rows[0], "startTick": str(BASE + 1)}] + rows[1:]},
            {**valid, "events": [{**rows[0], "completionTick": str(BASE + 100)}] + rows[1:]},
            {**valid, "events": [{**rows[0], "startTick": str(BASE)}] + rows[1:]},
            {**valid, "events": [{**rows[0], "completionTick": str(BASE)}] + rows[1:]},
            {**valid, "events": [{**rows[0], "startTick": "01"}] + rows[1:]},
            {**valid, "schemaVersion": True},
        ]
        for case in invalid:
            with self.subTest(case=case), self.assertRaises(ValueError):
                MODULE.audit(raw_schedule(), case)

    def test_invalid_or_rebased_schedule_is_red(self) -> None:
        base = schedule()
        invalid = [
            {**base, "arrivals": []},
            {**base, "arrivals": base["arrivals"] + [base["arrivals"][0]]},
            {**base, "arrivals": list(reversed(base["arrivals"]))},
            {**base, "arrivals": [{**base["arrivals"][0], "arrivalTick": str(BASE - 1)}] + base["arrivals"][1:]},
            {**base, "arrivals": base["arrivals"][:-1] + [{**base["arrivals"][2], "arrivalTick": str(BASE + 10000000)}]},
            {**base, "frequencyHz": "0"},
            {**base, "frequencyHz": "01"},
            {**base, "horizonEndTick": str(BASE)},
            {**base, "schemaVersion": True},
            {**base, "extra": "field"},
        ]
        for case in invalid:
            raw = raw_schedule(case)
            with self.subTest(case=case), self.assertRaises(ValueError):
                MODULE.audit(raw, observations(raw))
        rebased = {**base, "horizonStartTick": str(BASE + 1)}
        with self.assertRaises(ValueError):
            MODULE.audit(raw_schedule(rebased), observations())
        with self.assertRaises(ValueError):
            MODULE.audit(b'{"schemaVersion":1,"schemaVersion":1}', observations())

    def test_cli_rejects_duplicate_observation_json_keys(self) -> None:
        with tempfile.TemporaryDirectory(prefix="dxm-open-loop-") as temporary:
            schedule_path = Path(temporary) / "schedule.json"
            observed_path = Path(temporary) / "observed.json"
            schedule_path.write_bytes(raw_schedule())
            observed_path.write_text(json.dumps(observations()), encoding="utf-8")
            success = subprocess.run([sys.executable, str(SCRIPT), str(schedule_path), str(observed_path)], capture_output=True, text=True)
            self.assertEqual(success.returncode, 0, success.stderr)
            self.assertEqual(json.loads(success.stdout)["backlogAtHorizon"], 2)
            observed_path.write_text('{"schemaVersion":1,"schemaVersion":1}', encoding="utf-8")
            failure = subprocess.run([sys.executable, str(SCRIPT), str(schedule_path), str(observed_path)], capture_output=True, text=True)
            self.assertNotEqual(failure.returncode, 0)
            self.assertIn("duplicate JSON key", failure.stderr)


if __name__ == "__main__":
    unittest.main()
