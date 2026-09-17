"""Six fresh clean-build physical confirmation contract checks for #510."""

import hashlib
import importlib.util
import json
import math
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
        return CONFIRM.confirmation(
            self.manifest, self.calibration_bytes, self.commit, self.root, 600
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
            CONFIRM.confirmation(
                self.manifest, self.calibration_bytes, self.commit, self.root, 54_000
            )

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
