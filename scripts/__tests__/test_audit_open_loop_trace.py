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


class AuditOpenLoopTraceTests(unittest.TestCase):
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
