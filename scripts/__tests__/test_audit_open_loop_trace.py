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


class AuditOpenLoopTraceTests(unittest.TestCase):
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
