#!/usr/bin/env python3
"""Architecture admission fixtures, validated by the repository's Ajv dependency."""

from __future__ import annotations

import copy
import hashlib
import json
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
PERF = ROOT / ".github" / "perf"
EXACT = json.loads((PERF / "architecture-505-exact-sequential.v1.json").read_text())
PREPARED = json.loads((PERF / "architecture-505-prepared-dynamic.v1.json").read_text())
LOCKED_RING = json.loads((PERF / "architecture-505-locked-fixed-ring.v1.json").read_text())
NODE_VALIDATOR = """
const fs = require('fs');
const Ajv = require('ajv');
const schema = JSON.parse(fs.readFileSync('.github/perf/architecture-record.v1.schema.json', 'utf8'));
const validate = new Ajv({allErrors: true, strict: true}).compile(schema);
const candidates = JSON.parse(fs.readFileSync(0, 'utf8'));
process.stdout.write(JSON.stringify(candidates.map(candidate => !!validate(candidate))));
"""


def validate_many(*candidates: dict) -> list[bool]:
    result = subprocess.run(
        ["node", "-e", NODE_VALIDATOR],
        input=json.dumps(candidates),
        text=True,
        capture_output=True,
        cwd=ROOT,
        check=True,
    )
    return json.loads(result.stdout)


class ArchitectureRecordTests(unittest.TestCase):
    def test_records_are_admitted(self) -> None:
        self.assertEqual(validate_many(EXACT, PREPARED, LOCKED_RING), [True, True, True])

    def test_exact_control_record_matches_current_sources(self) -> None:
        for field, relative in (
            ("sourceSha256", "Tests/Runtime/TestUtilities/ExactSequentialBatchControl.cs"),
            ("testSourceSha256", "Tests/Runtime/Core/DifferentialBusTraceTests.cs"),
        ):
            with self.subTest(field=field):
                actual = hashlib.sha256((ROOT / relative).read_bytes()).hexdigest()
                self.assertEqual(EXACT["evidence"][field], actual)

    def test_locked_ring_record_matches_current_sources_and_contract(self) -> None:
        for field, relative in (
            ("sourceSha256", "Tests/Runtime/TestUtilities/LockedFixedRingControl.cs"),
            ("testSourceSha256", "Tests/Runtime/Core/LockedFixedRingControlTests.cs"),
        ):
            with self.subTest(field=field):
                actual = hashlib.sha256((ROOT / relative).read_bytes()).hexdigest()
                self.assertEqual(LOCKED_RING["evidence"][field], actual)
        self.assertEqual(LOCKED_RING["semanticClass"], "queued")
        self.assertEqual(
            LOCKED_RING["capacity"],
            {"policy": "fixed", "overflow": "try-fail", "wait": "blocking-lab-only"},
        )
        self.assertEqual(LOCKED_RING["topology"]["producers"], "multiple")
        self.assertEqual(LOCKED_RING["topology"]["consumers"], "single")

    def test_missing_admission_fields_are_rejected(self) -> None:
        candidates = []
        for parent, field in (
            ("ownership", "payloadLifetime"),
            ("topology", "threadAffinity"),
            ("ordering", "linearizationPoint"),
            ("capacity", "overflow"),
            ("lifecycle", "partialPublication"),
            ("decision", "stopRule"),
            ("evidence", "sourceSha256"),
            ("evidence", "testSourceSha256"),
        ):
            candidate = copy.deepcopy(EXACT)
            del candidate[parent][field]
            candidates.append(candidate)
        self.assertEqual(validate_many(*candidates), [False] * len(candidates))

    def test_exact_sequential_contradictions_are_rejected(self) -> None:
        changes = (
            ("ordering", "routeAmortization", True),
            ("ordering", "guarantee", "changed-cross-route"),
            ("capacity", "overflow", "blocking-lab-only"),
            ("capacity", "wait", "busy-spin-lab-only"),
            (None, "dynamicDifferences", "mid-batch mutations are deferred"),
        )
        candidates = []
        for parent, field, value in changes:
            candidate = copy.deepcopy(EXACT)
            target = candidate if parent is None else candidate[parent]
            target[field] = value
            candidates.append(candidate)
        self.assertEqual(validate_many(*candidates), [False] * len(candidates))

    def test_tested_evidence_requires_hashes(self) -> None:
        invalid = copy.deepcopy(EXACT)
        for field in ("sourceSha256", "testSourceSha256", "evidenceSha256"):
            invalid["evidence"][field] = None
        valid = copy.deepcopy(invalid)
        for field in ("sourceSha256", "testSourceSha256", "evidenceSha256"):
            valid["evidence"][field] = "a" * 64
        self.assertEqual(validate_many(invalid, valid), [False, True])

    def test_prepared_staleness_and_reclamation_contradictions_are_rejected(self) -> None:
        candidates = []
        for field in ("stalenessPolicy", "validatesAfterSweep", "retainsEvictedSinks", "dynamicFallback"):
            candidate = copy.deepcopy(PREPARED)
            del candidate["prepared"][field]
            candidates.append(candidate)
        for parent, field, value in (
            ("prepared", "validatesAfterSweep", False),
            ("prepared", "retainsEvictedSinks", True),
            ("prepared", "dynamicFallback", False),
            ("ordering", "guarantee", "changed-cross-route"),
            ("capacity", "wait", "blocking-lab-only"),
            (None, "dynamicDifferences", "stale routes may dispatch"),
        ):
            candidate = copy.deepcopy(PREPARED)
            target = candidate if parent is None else candidate[parent]
            target[field] = value
            candidates.append(candidate)
        no_prepared = copy.deepcopy(PREPARED)
        del no_prepared["prepared"]
        candidates.append(no_prepared)
        self.assertEqual(validate_many(*candidates), [False] * len(candidates))


if __name__ == "__main__":
    unittest.main()
