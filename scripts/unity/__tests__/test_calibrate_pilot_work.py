"""The #510 calibration reducer accepts six clean, healthy raw artifact trees."""

import importlib.util
import hashlib
import json
import math
import platform
import subprocess
import sys
import tempfile
import unittest
import uuid
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

from test_extract_pilot_paired import fixture_rows
from test_reduce_pilot_effects import PILOT


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
        ET.SubElement(case, "output").text = "DXM_PAIRED_COMPARISON " + json.dumps(row, separators=(",", ":"))
    return ET.tostring(root)


def artifact(path, work, *, healthy=True, build_id=None):
    manifest = {"schemaVersion": 1, "fileCount": 1, "files": [{"path": "player.exe", "length": 1, "sha256": "0" * 64}]}
    position = CALIBRATION.DISPATCH_ORDER.index(work)
    host_profile = {"schemaVersion": 1, "executionProfileId": "highest-efficiency-class-affinity-normal-v1", "cpuModel": "13th Gen Intel(R) Core(TM) i9-13900KF", "processorGroup": 0, "logicalProcessorCount": 32, "selectedLogicalProcessorIndices": list(range(16)), "selectedLogicalProcessorCount": 16, "selectedCoreCount": 8, "affinityMask": "0xFFFF", "priorityClass": "Normal"}
    host_profile_bytes = json.dumps(host_profile).encode()
    host_profile_sha = hashlib.sha256(host_profile_bytes).hexdigest()
    build = {"schemaVersion": 1, "projectWasAbsentBefore": True, "sourceCommit": COMMIT,
             "sourceTree": "b" * 40, "buildInvocationId": build_id or str(uuid.uuid4()),
             "buildStartedUtc": f"2026-09-17T00:{position:02d}:00Z",
             "buildFinishedUtc": f"2026-09-17T00:{position:02d}:30Z",
             "unityVersion": "6000.5.2f1", "canonicalProfileId": "canonical-il2cpp-verdict-player-v1",
             "canonicalProfileSha256": hashlib.sha256(PILOT.PROFILE_PATH.read_bytes()).hexdigest(),
             "playerDirectoryManifest": manifest}
    runs = []
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("pilot-build-evidence.json", json.dumps(build))
        archive.writestr("performance-cpu-profile.json", host_profile_bytes)
        for index in range(1, 6):
            order = CALIBRATION.ORDERS[(index - 1) % 2]
            results = "results.xml" if index == 1 else f"same-player-repeats/run-{index:02d}/results.xml"
            health = f"run-{index:02d}-host-telemetry.json.health.json"
            telemetry = f"run-{index:02d}-host-telemetry.json"
            envelope = f"{telemetry}.envelope.json"
            runs.append({"runIndex": index, "batchOrder": order,
                         "pilotCpuWorkIterationsPerBatch": work, "pilotCpuWorkByScenario": None,
                         "resultsPath": results, "hostTelemetryFile": telemetry,
                         "hostTelemetryEnvelopeFile": envelope, "hostTelemetryHealthFile": health})
            archive.writestr(results, results_xml(order, work))
            archive.writestr(f"same-player-repeats/{telemetry}", json.dumps({"sourceSha256": hashlib.sha256(PILOT.COLLECTOR_PATH.read_bytes()).hexdigest()}))
            archive.writestr(f"same-player-repeats/{envelope}", json.dumps({"unredactedTelemetrySha256": "e" * 64, "telemetryProcessorAffinityMask": "0xFFFF0000"}))
            archive.writestr(f"same-player-repeats/{health}", json.dumps({"schemaVersion": 1, "valid": healthy, "reasons": [] if healthy else ["sensor-read-error"], "cpuProfileSha256": host_profile_sha, "telemetrySha256": "e" * 64}))
        archive.writestr("same-player-repeats/same-player-evidence.json", json.dumps({
            "schemaVersion": 1, "runCount": 5, "playerDirectoryManifestMatches": True,
            "playerDirectoryManifestBefore": manifest, "playerDirectoryManifestAfter": manifest,
            "runs": runs,
        }))


def job(path, work, *, return_success=True):
    position = CALIBRATION.DISPATCH_ORDER.index(work)
    steps = [{"name": name, "status": "completed", "conclusion": "success"} for name in PILOT.REQUIRED_JOB_STEPS]
    if not return_success:
        next(step for step in steps if step["name"] == "Return Unity license")["conclusion"] = "failure"
    path.write_text(json.dumps({"id": 1000 + position, "run_id": 2000 + position, "head_sha": COMMIT, "name": "Pilot IL2CPP contract, calibration, vector, or license recovery on ELI", "runner_name": "ELI-MACHINE", "status": "completed", "conclusion": "success", "started_at": f"2026-09-17T00:{position:02d}:00Z", "completed_at": f"2026-09-17T00:{position:02d}:40Z", "steps": steps}))


class PilotCalibrationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.artifacts = {}
        self.manifest = {"schemaVersion": 1, "purpose": "510-control-only-work-calibration-evidence", "builds": []}
        for level in CALIBRATION.WORK_LEVELS:
            path = self.root / f"{level}.zip"
            artifact(path, level)
            self.artifacts[level] = path
            job_path = self.root / f"{level}.job.json"
            job(job_path, level)
            self.manifest["builds"].append({"work": level, "artifactPath": path.name, "artifactSha256": hashlib.sha256(path.read_bytes()).hexdigest(), "workflowRunId": 2000 + CALIBRATION.DISPATCH_ORDER.index(level), "jobEvidencePath": job_path.name, "jobEvidenceSha256": hashlib.sha256(job_path.read_bytes()).hexdigest()})
        self.manifest["builds"].sort(key=lambda entry: CALIBRATION.DISPATCH_ORDER.index(entry["work"]))

    def tearDown(self):
        self.directory.cleanup()

    def test_calibrates_each_target_from_six_distinct_builds(self):
        report = CALIBRATION.calibrate(self.artifacts, COMMIT, CALIBRATION.load_extractor())
        self.assertEqual(set(report["builds"]), {str(level) for level in CALIBRATION.WORK_LEVELS})
        for row in report["targets"].values():
            self.assertEqual(row["proposedIterations"], {"P03": 2956, "P05": 4880, "P10": 9532})

    def test_all_six_terminal_jobs_preflight_before_rates(self):
        artifacts, jobs = CALIBRATION.preflight(self.manifest, COMMIT, self.root)
        self.assertEqual(artifacts, self.artifacts)
        self.assertEqual(len(jobs), 6)
        self.assertEqual(sum(row["jobSeconds"] for row in jobs.values()), 240)

    def test_failed_return_prevents_calibration_preflight(self):
        job_path = self.root / "2048.job.json"
        job(job_path, 2048, return_success=False)
        next(entry for entry in self.manifest["builds"] if entry["work"] == 2048)["jobEvidenceSha256"] = hashlib.sha256(job_path.read_bytes()).hexdigest()
        with self.assertRaisesRegex(ValueError, "unconfirmed workflow step: Return Unity license"):
            CALIBRATION.preflight(self.manifest, COMMIT, self.root)

    def test_cli_seals_six_terminal_jobs_before_calibration_report(self):
        manifest_path = self.root / "manifest.json"
        manifest_path.write_text(json.dumps(self.manifest))
        output_path = self.root / "calibration.json"
        frozen_config = Path(__file__).parents[3] / ".github/perf/pilot-control-calibration.v1.json"
        config = json.loads(frozen_config.read_text())
        config["pythonVersion"] = platform.python_version()
        config_path = self.root / "config.json"
        config_path.write_text(json.dumps(config))
        command = [sys.executable, str(SOURCE), "--config", str(config_path), "--expected-commit", COMMIT, "--artifact-manifest", str(manifest_path), "--output", str(output_path)]
        result = subprocess.run(command, capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads(output_path.read_text())
        self.assertEqual(report["calibrationJobSeconds"], 240)
        self.assertEqual(set(report["workflowJobs"]), {str(level) for level in CALIBRATION.WORK_LEVELS})
        output_path.unlink()
        config_path.write_text(json.dumps({**config, "pythonVersion": "0.0.0"}))
        drift = subprocess.run(command, capture_output=True, text=True, check=False)
        self.assertNotEqual(drift.returncode, 0)
        self.assertIn("Python runtime drift", drift.stderr)
        self.assertFalse(output_path.exists())
        config_path.write_text(json.dumps(config))
        job_path = self.root / "2048.job.json"
        job(job_path, 2048, return_success=False)
        next(entry for entry in self.manifest["builds"] if entry["work"] == 2048)["jobEvidenceSha256"] = hashlib.sha256(job_path.read_bytes()).hexdigest()
        manifest_path.write_text(json.dumps(self.manifest))
        failed = subprocess.run(command, capture_output=True, text=True, check=False)
        self.assertNotEqual(failed.returncode, 0)
        self.assertIn("unconfirmed workflow step: Return Unity license", failed.stderr)
        self.assertFalse(output_path.exists())

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
