"""The #510 calibration reducer accepts six clean, healthy raw artifact trees."""

import importlib.util
import json
import math
import tempfile
import unittest
import uuid
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

from test_extract_pilot_paired import fixture_rows


SOURCE = Path(__file__).parents[1] / "calibrate-pilot-work.py"
SPEC = importlib.util.spec_from_file_location("calibrate_pilot_work", SOURCE)
CALIBRATION = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CALIBRATION)
COMMIT = "a" * 40


def results_xml(order, work):
    root = ET.Element("test-run", failed="0")
    for row in fixture_rows(order=order, work=work):
        if row["scenario"] in CALIBRATION.TARGETS:
            ratio = 0.5 * math.exp(-work / 100000)
            row["cycleRatios"] = [ratio] * 4
            row["firstToSecondRatio"] = ratio
            row["aggregateRateRatio"] = ratio
            for cycle in row["cycleMeasurements"]:
                cycle["firstActiveSeconds"] = 0.625 / ratio
                cycle["firstToSecondRatio"] = ratio
        case = ET.SubElement(root, "test-case", result="Passed")
        ET.SubElement(case, "output").text = "DXM_PAIRED_COMPARISON " + json.dumps(row)
    return ET.tostring(root)


def artifact(path, work, *, healthy=True, build_id=None):
    manifest = {"schemaVersion": 1, "fileCount": 1, "files": [{"path": "player.exe", "length": 1, "sha256": "0" * 64}]}
    build = {"schemaVersion": 1, "projectWasAbsentBefore": True, "sourceCommit": COMMIT,
             "sourceTree": "b" * 40, "buildInvocationId": build_id or str(uuid.uuid4()),
             "buildStartedUtc": f"2026-09-17T00:{CALIBRATION.DISPATCH_ORDER.index(work):02d}:00Z",
             "unityVersion": "6000.5.2f1", "canonicalProfileId": "canonical-il2cpp-verdict-player-v1",
             "playerDirectoryManifest": manifest}
    runs = []
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("pilot-build-evidence.json", json.dumps(build))
        for index in range(1, 6):
            order = CALIBRATION.ORDERS[(index - 1) % 2]
            results = "results.xml" if index == 1 else f"same-player-repeats/run-{index:02d}/results.xml"
            health = f"run-{index:02d}-host-telemetry.json.health.json"
            runs.append({"runIndex": index, "batchOrder": order,
                         "pilotCpuWorkIterationsPerBatch": work, "resultsPath": results,
                         "hostTelemetryHealthFile": health})
            archive.writestr(results, results_xml(order, work))
            archive.writestr(f"same-player-repeats/{health}", json.dumps({"schemaVersion": 1, "valid": healthy, "reasons": [] if healthy else ["sensor-read-error"]}))
        archive.writestr("same-player-repeats/same-player-evidence.json", json.dumps({
            "schemaVersion": 1, "runCount": 5, "playerDirectoryManifestMatches": True,
            "playerDirectoryManifestBefore": manifest, "playerDirectoryManifestAfter": manifest,
            "runs": runs,
        }))


class PilotCalibrationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.artifacts = {}
        for level in CALIBRATION.WORK_LEVELS:
            path = Path(self.directory.name) / f"{level}.zip"
            artifact(path, level)
            self.artifacts[level] = path

    def tearDown(self):
        self.directory.cleanup()

    def test_calibrates_each_target_from_six_distinct_builds(self):
        report = CALIBRATION.calibrate(self.artifacts, COMMIT, CALIBRATION.load_extractor())
        self.assertEqual(set(report["builds"]), {str(level) for level in CALIBRATION.WORK_LEVELS})
        for row in report["targets"].values():
            self.assertEqual(row["proposedIterations"], {"P03": 2956, "P05": 4880, "P10": 9532})

    def test_rejects_missing_level_before_reading_rates(self):
        self.artifacts.pop(2048)
        with self.assertRaisesRegex(ValueError, "all six fixed calibration levels"):
            CALIBRATION.calibrate(self.artifacts, COMMIT, CALIBRATION.load_extractor())

    def test_rejects_changed_dispatch_order(self):
        path = self.artifacts[0]
        artifact(path, 0)
        with zipfile.ZipFile(path) as archive:
            members = {name: archive.read(name) for name in archive.namelist()}
        build = json.loads(members["pilot-build-evidence.json"])
        build["buildStartedUtc"] = "2026-09-17T00:09:00Z"
        members["pilot-build-evidence.json"] = json.dumps(build).encode()
        with zipfile.ZipFile(path, "w") as archive:
            for name, content in members.items():
                archive.writestr(name, content)
        with self.assertRaisesRegex(ValueError, "dispatch order drift"):
            CALIBRATION.calibrate(self.artifacts, COMMIT, CALIBRATION.load_extractor())

    def test_rejects_invalid_health_and_reused_build(self):
        artifact(self.artifacts[8192], 8192, healthy=False)
        with self.assertRaisesRegex(ValueError, "invalid host health"):
            CALIBRATION.calibrate(self.artifacts, COMMIT, CALIBRATION.load_extractor())
        repeated_id = str(uuid.uuid4())
        artifact(self.artifacts[0], 0, build_id=repeated_id)
        artifact(self.artifacts[8192], 8192, build_id=repeated_id)
        with self.assertRaisesRegex(ValueError, "reused a clean build"):
            CALIBRATION.calibrate(self.artifacts, COMMIT, CALIBRATION.load_extractor())

    def test_isotonic_projection_and_unreachable_target(self):
        projected = CALIBRATION.isotonic_nonnegative([0, 0.02, 0.01, 0.04])
        self.assertEqual(projected, [0, 0.015, 0.015, 0.04])
        self.assertIsNone(CALIBRATION.interpolated_work((0, 1, 2, 3), projected, 0.05))


if __name__ == "__main__":
    unittest.main()
