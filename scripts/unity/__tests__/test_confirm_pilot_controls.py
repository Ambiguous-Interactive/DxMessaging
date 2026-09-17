"""Six fresh clean-build physical confirmation contract checks for #510."""

import hashlib
import importlib.util
import json
import math
import subprocess
import sys
import unittest
import zipfile
from pathlib import Path

import test_reduce_pilot_effects as pilot_fixtures


SOURCE = Path(__file__).parents[1] / "confirm-pilot-controls.py"
SPEC = importlib.util.spec_from_file_location("confirm_pilot_controls", SOURCE)
CONFIRM = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CONFIRM)


class PhysicalConfirmationTests(unittest.TestCase):
    def setUp(self):
        self.fixtures = pilot_fixtures.PilotArtifactPreflightTests(
            "test_complete_schedule_emits_only_arm_blind_validity"
        )
        self.fixtures.setUp()
        self.commit = self.fixtures.commit
        self.root = self.fixtures.root
        self.proposals = {
            condition: {scenario: (index + 2) * 10 for scenario in CONFIRM.TARGETS}
            for index, condition in enumerate(("P03", "P05", "P10"))
        }
        self.calibration_bytes = json.dumps(
            {
                "schemaVersion": 1,
                "purpose": "control-only-work-calibration",
                "sourceCommit": self.commit,
                "configSha256": hashlib.sha256(CONFIRM.CALIBRATION_CONFIG_PATH.read_bytes()).hexdigest(),
                "levels": [0, 2048, 8192, 32768, 131072, 524288],
                "builds": {
                    str(level): {
                        "buildInvocationId": f"calibration-{index}",
                        "sourceTree": "b" * 40,
                        "archiveSha256": "a" * 64,
                        "buildStartedUtc": f"2026-09-17T00:{index:02d}:00Z",
                    }
                    for index, level in enumerate((0, 2048, 8192, 32768, 131072, 524288))
                },
                "evidenceManifestSha256": "c" * 64,
                "workflowJobs": {
                    str(level): {
                        "workflowRunId": 3000 + index,
                        "workflowJobId": 4000 + index,
                        "jobEvidenceSha256": "d" * 64,
                        "jobSeconds": 40,
                        "jobCompletedUtc": f"2026-09-16T23:{index:02d}:40Z",
                    }
                    for index, level in enumerate((0, 2048, 8192, 32768, 131072, 524288))
                },
                "calibrationJobSeconds": 240,
                "targets": {
                    scenario: {
                        "proposedIterations": {
                            condition: self.proposals[condition][scenario]
                            for condition in ("P03", "P05", "P10")
                        }
                    }
                    for scenario in CONFIRM.TARGETS
                },
            }
        ).encode()
        self.manifest = {
            "schemaVersion": 1,
            "purpose": "510-independent-physical-control-confirmation",
            "builds": [],
        }
        self.build_fixture()

    def tearDown(self):
        self.fixtures.tearDown()

    def build_fixture(self, failed_effect=None):
        self.manifest["builds"] = []
        for index, (condition, arm) in enumerate(CONFIRM.DISPATCH):
            prior = self.fixtures.manifest["builds"][index]
            path = self.root / prior["artifactPath"]
            vector = self.proposals[condition] if arm == "A" else {
                scenario: 0 for scenario in CONFIRM.TARGETS
            }
            multiplier = {"P03": 1.03, "P05": 1.05, "P10": 1.10}[condition]
            if arm == "B" or condition == failed_effect:
                multiplier = 1.0
            orders = CONFIRM.A_ORDERS if arm == "A" else CONFIRM.B_ORDERS
            scheduled = {
                "launches": [
                    {"index": launch, "batchOrder": order}
                    for launch, order in enumerate(orders, 1)
                ]
            }
            self.fixtures.write_artifact(
                path, index, scheduled, work=vector, multiplier=multiplier
            )
            self.manifest["builds"].append(
                {
                    "condition": condition,
                    "arm": arm,
                    "artifactPath": path.name,
                    "artifactSha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                    "workflowRunId": prior["workflowRunId"],
                    "jobEvidencePath": prior["jobEvidencePath"],
                    "jobEvidenceSha256": prior["jobEvidenceSha256"],
                }
            )

    def confirm(self):
        budget = {"totalSeconds": 840, "eliJobs": [{"runId": entry["workflowRunId"], "jobId": 1000 + index, "seconds": 40} for index, entry in enumerate(self.manifest["builds"])]}
        return CONFIRM.confirmation(
            self.manifest, self.calibration_bytes, self.commit, self.root, budget, "e" * 64
        )

    def test_fresh_pairs_confirm_target_direction_without_interval_claim(self):
        report = self.confirm()
        self.assertTrue(report["passesPhysicalSanity"])
        self.assertAlmostEqual(
            report["conditions"]["P10"]["targetLogEffects"]["GlobalToOne"],
            math.log(1.10),
        )
        self.assertEqual(report["confirmationJobSeconds"], 6 * 40)
        self.assertNotIn("intervals", report)

    def test_cli_reduces_with_complete_raw_actions_budget(self):
        manifest_path = self.root / "confirmation-manifest.json"
        calibration_path = self.root / "calibration-report.json"
        manifest_path.write_text(json.dumps(self.manifest))
        calibration_path.write_bytes(self.calibration_bytes)
        runs = []
        snapshots = []
        for index, entry in enumerate(self.manifest["builds"]):
            job = json.loads((self.root / entry["jobEvidencePath"]).read_text())
            runs.append({"id": entry["workflowRunId"], "name": "Runner Audit (Windows)", "event": "workflow_dispatch", "status": "completed", "created_at": job["started_at"], "head_sha": self.commit})
            path = self.root / f"budget-job-{index}.json"
            path.write_text(json.dumps({"total_count": 1, "jobs": [job]}))
            snapshots.append({"runId": entry["workflowRunId"], "path": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
        index_path = self.root / "budget-runs.json"
        index_path.write_text(json.dumps({"total_count": 6, "workflow_runs": runs}))
        ledger_path = self.root / "budget-ledger.json"
        ledger_path.write_text(json.dumps({"schemaVersion": 1, "purpose": "510-serialized-eli-budget", "workflowRunPages": [{"page": 1, "path": index_path.name, "sha256": hashlib.sha256(index_path.read_bytes()).hexdigest()}], "jobSnapshots": snapshots}))
        output_path = self.root / "confirmation-report.json"
        result = subprocess.run([sys.executable, str(SOURCE), "--artifact-manifest", str(manifest_path), "--calibration-report", str(calibration_path), "--expected-commit", self.commit, "--budget-ledger", str(ledger_path), "--output", str(output_path)], capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads(output_path.read_text())
        self.assertTrue(report["passesPhysicalSanity"])
        self.assertEqual(report["totalSerializedEliSeconds"], 240)

    def test_requires_all_six_calibration_terminal_jobs(self):
        calibration = json.loads(self.calibration_bytes)
        del calibration["workflowJobs"]["2048"]
        self.calibration_bytes = json.dumps(calibration).encode()
        with self.assertRaisesRegex(ValueError, "complete six-job calibration cleanup evidence"):
            self.confirm()

    def test_confirmation_must_follow_completed_calibration(self):
        calibration = json.loads(self.calibration_bytes)
        calibration["workflowJobs"]["2048"]["jobCompletedUtc"] = "2026-09-17T00:00:01Z"
        self.calibration_bytes = json.dumps(calibration).encode()
        with self.assertRaisesRegex(ValueError, "confirmation build predates calibration completion"):
            self.confirm()

    def test_failing_physical_effect_stops_confirmation(self):
        self.build_fixture(failed_effect="P10")
        report = self.confirm()
        self.assertFalse(report["passesPhysicalSanity"])
        self.assertFalse(report["conditions"]["P10"]["targetsPass"])

    def test_invalid_health_prevents_any_rate_report(self):
        entry = self.manifest["builds"][0]
        path = self.root / entry["artifactPath"]
        scheduled = {
            "launches": [
                {"index": index, "batchOrder": order}
                for index, order in enumerate(CONFIRM.B_ORDERS, 1)
            ]
        }
        self.fixtures.write_artifact(
            path,
            0,
            scheduled,
            work={scenario: 0 for scenario in CONFIRM.TARGETS},
            health_valid=False,
        )
        entry["artifactSha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        with self.assertRaisesRegex(ValueError, "invalid host health"):
            self.confirm()

    def test_failed_terminal_return_invalidates_confirmation(self):
        entry = self.manifest["builds"][0]
        path = self.root / entry["jobEvidencePath"]
        job = json.loads(path.read_text())
        next(
            step for step in job["steps"] if step["name"] == "Return Unity license"
        )["conclusion"] = "failure"
        path.write_text(json.dumps(job))
        entry["jobEvidenceSha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        with self.assertRaisesRegex(ValueError, "unconfirmed workflow step"):
            self.confirm()

    def test_incomplete_calibration_cannot_propose_confirmation_work(self):
        calibration = json.loads(self.calibration_bytes)
        calibration["builds"].pop("2048")
        self.calibration_bytes = json.dumps(calibration).encode()
        with self.assertRaisesRegex(ValueError, "complete six-build calibration report"):
            self.confirm()

    def test_hard_eli_cap_rejects_confirmation(self):
        with self.assertRaisesRegex(ValueError, "approved serialized ELI time cap exceeded"):
            budget = {"totalSeconds": 54_001, "eliJobs": [{"runId": entry["workflowRunId"], "jobId": 1000 + index, "seconds": 40} for index, entry in enumerate(self.manifest["builds"])]}
            CONFIRM.confirmation(
                self.manifest, self.calibration_bytes, self.commit, self.root, budget, "e" * 64
            )

    def test_confirmation_jobs_must_be_in_budget_ledger(self):
        budget = {"totalSeconds": 840, "eliJobs": [{"runId": entry["workflowRunId"], "jobId": 1000 + index, "seconds": 40} for index, entry in enumerate(self.manifest["builds"][:-1])]}
        with self.assertRaisesRegex(ValueError, "confirmation jobs missing from ELI budget"):
            CONFIRM.confirmation(self.manifest, self.calibration_bytes, self.commit, self.root, budget, "e" * 64)

    def test_vector_drift_rejects_confirmation(self):
        entry = self.manifest["builds"][1]
        path = self.root / entry["artifactPath"]
        with zipfile.ZipFile(path) as archive:
            members = {name: archive.read(name) for name in archive.namelist()}
        same = json.loads(members["same-player-repeats/same-player-evidence.json"])
        same["runs"][0]["pilotCpuWorkByScenario"]["GlobalToOne"] = 999
        members["same-player-repeats/same-player-evidence.json"] = json.dumps(same).encode()
        with zipfile.ZipFile(path, "w") as archive:
            for name, content in members.items():
                archive.writestr(name, content)
        entry["artifactSha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        with self.assertRaisesRegex(ValueError, "confirmation work-vector drift"):
            self.confirm()


if __name__ == "__main__":
    unittest.main()
