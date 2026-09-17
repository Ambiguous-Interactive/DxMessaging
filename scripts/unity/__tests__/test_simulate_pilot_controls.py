"""Deterministic exact-transform calibration checks for the #510 pilot."""

import importlib.util
import json
import platform
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SOURCE = Path(__file__).parents[1] / "simulate-pilot-controls.py"
SPEC = importlib.util.spec_from_file_location("simulate_pilot_controls", SOURCE)
PILOT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PILOT)
SEED = "d63c087d8a9512305cfd728b49d8a1a473399d68a662f4ecaa5aa804ebe85f86"
CONFIG = Path(__file__).parents[3] / ".github/perf/pilot-analysis-controls.v1.json"


def fixture(bias=0.0):
    return {
        scenario: [
            {"unitId": f"AA-{index}", "logEffect": bias}
            for index in range(6)
        ]
        for scenario in PILOT.SCENARIOS
    }


class PilotSimulationTests(unittest.TestCase):
    def test_committed_config_runs_and_source_drift_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            aa_path = root / "aa.json"
            result_path = root / "simulation.json"
            aa_path.write_text(json.dumps(fixture()))
            config = json.loads(CONFIG.read_text())
            self.assertEqual(config["pythonVersion"], "3.11.2")
            config["pythonVersion"] = platform.python_version()
            config_path = root / "runtime-compatible.json"
            config_path.write_text(json.dumps(config))
            command = [
                sys.executable,
                str(SOURCE),
                "--aa-effects",
                str(aa_path),
                "--config",
                str(config_path),
                "--output",
                str(result_path),
            ]
            result = subprocess.run(command, capture_output=True, text=True, check=False)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(json.loads(result_path.read_text())["rows"]["GlobalToOne"]["coverage95"]["count"], 100_000)
            drifted = dict(config)
            drifted["sourceSha256"] = "0" * 64
            drift_path = root / "drifted.json"
            drift_path.write_text(json.dumps(drifted))
            command[command.index(str(config_path))] = str(drift_path)
            result = subprocess.run(command, capture_output=True, text=True, check=False)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("source hash drift", result.stderr)

    def test_wilson_extremes_and_nominal_cutoffs(self):
        self.assertAlmostEqual(PILOT.wilson(0, 100_000)[1], 0.000038413, places=8)
        self.assertAlmostEqual(PILOT.wilson(100_000, 100_000)[0], 0.999961587, places=8)
        self.assertLess(PILOT.wilson(5000, 100_000)[1], 0.055)
        self.assertLess(PILOT.wilson(90000, 100_000)[0], 0.90)

    def test_exact_transforms_on_zero_noise_fixture(self):
        report = PILOT.simulate(fixture(), SEED)
        self.assertEqual(report["replicatesPerTruthScenario"], 100_000)
        for row in report["rows"].values():
            self.assertEqual(set(row["truths"]), set(PILOT.TRUTHS))
            self.assertTrue(all(truth["coverage95Count"] == 100_000 for truth in row["truths"].values()))
            self.assertEqual(row["coverage95"]["count"], 100_000)
            self.assertEqual(row["boundaryFalseReject5"]["count"], 0)
            self.assertEqual(row["powerAtFivePercent"]["count"], 100_000)
            self.assertEqual(row["equivalenceAtNull"]["count"], 100_000)
            self.assertTrue(row["passesFalseRejectLimit"])
            self.assertTrue(row["passesCoverageFloor"])
            self.assertTrue(row["passesDeclaredPowerFloor"])

    def test_fixed_replicate_floor_and_whole_unit_identity(self):
        with self.assertRaisesRegex(ValueError, "100000 fixed replicates"):
            PILOT.simulate(fixture(), SEED, 99_999)
        aa = fixture()
        aa["GlobalToOne"][1]["unitId"] = aa["GlobalToOne"][0]["unitId"]
        with self.assertRaisesRegex(ValueError, "duplicate A/A unit"):
            PILOT.simulate(aa, SEED)

    def test_biased_fixture_fails_boundary_and_coverage(self):
        report = PILOT.simulate(fixture(bias=0.05), SEED)
        row = report["rows"]["GlobalToOne"]
        self.assertEqual(row["boundaryFalseReject5"]["count"], 100_000)
        self.assertEqual(row["coverage95"]["count"], 0)
        self.assertFalse(row["passesFalseRejectLimit"])
        self.assertFalse(row["passesCoverageFloor"])


if __name__ == "__main__":
    unittest.main()
