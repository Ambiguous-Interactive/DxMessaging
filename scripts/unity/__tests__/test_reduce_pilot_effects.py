"""Independent-unit and manifest RED checks for the #510 pilot reducer."""

import importlib.util
import hashlib
import json
import math
import subprocess
import sys
import tempfile
import unittest
import uuid
import xml.etree.ElementTree as ET
import zipfile
from datetime import datetime, timedelta, timezone
from pathlib import Path

from test_extract_pilot_paired import fixture_rows


SOURCE = Path(__file__).parents[1] / "reduce-pilot-effects.py"
SPEC = importlib.util.spec_from_file_location("reduce_pilot_effects", SOURCE)
PILOT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PILOT)
SCHEDULE = Path(__file__).parents[3] / ".github/perf/pilot-schedule.v1.json"


def fixture():
    schedule = []
    assignments = []
    builds = []
    for block in (1, 2, 3):
        for condition_index, condition in enumerate(("AA", "P03", "P05", "P10")):
            for orientation_index, orientation in enumerate(("ABA", "BAB")):
                unit_id = f"B{block:02d}U{condition_index * 2 + orientation_index + 1:02d}"
                scheduled_builds = []
                for slot, arm in enumerate(orientation, 1):
                    scheduled_launches = [
                        {"index": index, "batchOrder": "ABBABAAB" if index % 2 else "BAABABBA"}
                        for index in range(1, 6)
                    ]
                    scheduled_builds.append(
                        {"slot": slot, "arm": arm, "cleanBuild": True, "launches": scheduled_launches}
                    )
                    ratio = 0.5
                    if condition == "P05" and arm == "A":
                        ratio /= 1.05
                    launches = [
                        {
                            "index": launch["index"],
                            "batchOrder": launch["batchOrder"],
                            "launchId": f"{unit_id}-{slot}-{launch['index']}",
                            "ratios": {
                                scenario: (0.8 if scenario in PILOT.SENTINELS else ratio)
                                for scenario in PILOT.SCENARIOS
                            },
                        }
                        for launch in scheduled_launches
                    ]
                    builds.append(
                        {
                            "unitId": unit_id,
                            "slot": slot,
                            "buildId": f"{unit_id}-{slot}",
                            "sourceTree": "a" * 40,
                            "launches": launches,
                        }
                    )
                schedule.append(
                    {"unitId": unit_id, "block": block, "builds": scheduled_builds}
                )
                assignments.append(
                    {
                        "unitId": unit_id,
                        "condition": condition,
                        "treatmentArm": "B",
                        "shimArm": None if condition == "AA" else "A",
                        "nominalTreatmentRateRatio": PILOT.NOMINAL_RATIOS[condition],
                    }
                )
    return schedule, assignments, builds


class PilotReducerTests(unittest.TestCase):
    def test_one_effect_per_palindrome_with_correct_direction(self):
        effects = PILOT.reduce(*fixture())
        self.assertEqual(len(effects["P05"]["GlobalToOne"]), 6)
        for unit in effects["P05"]["GlobalToOne"]:
            self.assertAlmostEqual(unit["logEffect"], math.log(1.05))
        for unit in effects["AA"]["GlobalToOne"]:
            self.assertAlmostEqual(unit["logEffect"], 0.0)
        for unit in effects["P05"]["GlobalToMany"]:
            self.assertAlmostEqual(unit["logEffect"], 0.0)

    def test_rejects_shared_binary(self):
        schedule, assignments, builds = fixture()
        builds[1]["buildId"] = builds[0]["buildId"]
        with self.assertRaisesRegex(ValueError, "shared or missing build identity"):
            PILOT.reduce(schedule, assignments, builds)

    def test_rejects_shared_launch(self):
        schedule, assignments, builds = fixture()
        builds[1]["launches"][0]["launchId"] = builds[0]["launches"][0]["launchId"]
        with self.assertRaisesRegex(ValueError, "shared or missing launch identity"):
            PILOT.reduce(schedule, assignments, builds)

    def test_rejects_schedule_drift(self):
        schedule, assignments, builds = fixture()
        builds[0]["launches"][0]["batchOrder"] = "BAABABBA"
        with self.assertRaisesRegex(ValueError, "launch batch order drift"):
            PILOT.reduce(schedule, assignments, builds)

    def test_rejects_changed_outer_arm(self):
        schedule, assignments, builds = fixture()
        schedule[0]["builds"][2]["arm"] = "B"
        with self.assertRaisesRegex(ValueError, "changed outer arms"):
            PILOT.reduce(schedule, assignments, builds)

    def test_rejects_missing_launch(self):
        schedule, assignments, builds = fixture()
        builds[0]["launches"].pop()
        with self.assertRaisesRegex(ValueError, "five launches"):
            PILOT.reduce(schedule, assignments, builds)

    def test_rejects_source_mismatch(self):
        schedule, assignments, builds = fixture()
        builds[0]["sourceTree"] = "b" * 40
        with self.assertRaisesRegex(ValueError, "source tree mismatch"):
            PILOT.reduce(schedule, assignments, builds)

    def test_fixed_six_unit_t_interval_and_equivalence(self):
        intervals = PILOT.pilot_intervals(PILOT.reduce(*fixture()))
        positive = intervals["P05"]["GlobalToOne"]
        self.assertEqual(positive["nIndependentPalindromes"], 6)
        self.assertAlmostEqual(positive["meanLogEffect"], math.log(1.05))
        self.assertTrue(positive["aboveThreePercent"])
        self.assertTrue(intervals["AA"]["GlobalToOne"]["equivalentWithinThreePercent"])
        self.assertTrue(intervals["P05"]["GlobalToMany"]["equivalentWithinThreePercent"])

    def test_t_interval_uses_sample_variance_over_six_units(self):
        effects = PILOT.reduce(*fixture())
        units = effects["AA"]["GlobalToOne"]
        for index, unit in enumerate(units):
            unit["logEffect"] = index / 100
        interval = PILOT.pilot_intervals(effects)["AA"]["GlobalToOne"]
        expected_standard_error = math.sqrt(0.00035) / math.sqrt(6)
        self.assertAlmostEqual(interval["meanLogEffect"], 0.025)
        self.assertAlmostEqual(
            interval["lower95OneSidedLog"],
            0.025 - PILOT.PILOT_T_95_DF5 * expected_standard_error,
        )

    def test_interval_rejects_pseudoreplicated_units(self):
        effects = PILOT.reduce(*fixture())
        units = effects["AA"]["GlobalToOne"]
        units[1]["unitId"] = units[0]["unitId"]
        with self.assertRaisesRegex(ValueError, "pseudo-replicated palindrome"):
            PILOT.pilot_intervals(effects)


class PilotArtifactPreflightTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.schedule_bytes = SCHEDULE.read_bytes()
        self.schedule = json.loads(self.schedule_bytes)
        self.manifest = {"schemaVersion": 1, "builds": []}
        self.commit = "a" * 40
        self.profile_sha = hashlib.sha256(PILOT.PROFILE_PATH.read_bytes()).hexdigest()
        self.collector_sha = hashlib.sha256(PILOT.COLLECTOR_PATH.read_bytes()).hexdigest()
        self.host_profile = {"schemaVersion": 1, "executionProfileId": "highest-efficiency-class-affinity-normal-v1", "cpuModel": "13th Gen Intel(R) Core(TM) i9-13900KF", "processorGroup": 0, "logicalProcessorCount": 32, "selectedLogicalProcessorIndices": list(range(16)), "selectedLogicalProcessorCount": 16, "selectedCoreCount": 8, "affinityMask": "0xFFFF", "priorityClass": "Normal"}
        self.host_profile_bytes = json.dumps(self.host_profile).encode()
        self.host_profile_sha = hashlib.sha256(self.host_profile_bytes).hexdigest()
        self.player_manifest = {"files": [{"path": "player.exe", "sha256": "0" * 64}]}
        self.start = datetime(2026, 9, 17, tzinfo=timezone.utc)
        for build_index, (unit, build) in enumerate(
            (unit, build)
            for unit in self.schedule["units"]
            for build in unit["builds"]
        ):
            path = self.root / f"build-{build_index:02d}.zip"
            self.write_artifact(path, build_index, build)
            job_path = self.root / f"job-{build_index:02d}.json"
            job_started = self.start + timedelta(minutes=build_index, seconds=-5)
            job_completed = self.start + timedelta(minutes=build_index, seconds=35)
            timestamp = lambda value: value.isoformat().replace("+00:00", "Z")
            job_path.write_text(json.dumps({"id": 1000 + build_index, "run_id": 2000 + build_index, "head_sha": self.commit, "name": "Pilot IL2CPP contract, calibration, vector, or license recovery on ELI", "status": "completed", "conclusion": "success", "started_at": timestamp(job_started), "completed_at": timestamp(job_completed), "steps": [{"name": name, "status": "completed", "conclusion": "success"} for name in PILOT.REQUIRED_JOB_STEPS]}))
            self.manifest["builds"].append(
                {
                    "unitId": unit["unitId"],
                    "slot": build["slot"],
                    "artifactPath": path.name,
                    "artifactSha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                    "workflowRunId": 2000 + build_index,
                    "jobEvidencePath": job_path.name,
                    "jobEvidenceSha256": hashlib.sha256(job_path.read_bytes()).hexdigest(),
                }
            )

    def tearDown(self):
        self.directory.cleanup()

    def write_artifact(self, path, build_index, scheduled_build, *, health_valid=True, build_id=None, work=None, multiplier=1.0):
        start = self.start + timedelta(minutes=build_index)
        finished = start + timedelta(seconds=30)
        timestamp = lambda value: value.isoformat().replace("+00:00", "Z")
        build = {
            "schemaVersion": 1,
            "projectWasAbsentBefore": True,
            "sourceCommit": self.commit,
            "sourceTree": "b" * 40,
            "buildInvocationId": build_id or str(uuid.UUID(int=build_index + 1)),
            "unityVersion": "6000.5.2f1",
            "canonicalProfileId": "canonical-il2cpp-verdict-player-v1",
            "canonicalProfileSha256": self.profile_sha,
            "buildStartedUtc": timestamp(start),
            "buildFinishedUtc": timestamp(finished),
            "playerDirectoryManifest": self.player_manifest,
        }
        runs = []
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr("pilot-build-evidence.json", json.dumps(build))
            archive.writestr("performance-cpu-profile.json", self.host_profile_bytes)
            for index, launch in enumerate(scheduled_build["launches"], 1):
                result_path = "results.xml" if index == 1 else f"same-player-repeats/run-{index:02d}/results.xml"
                health_name = f"run-{index:02d}.health.json"
                telemetry_name = f"run-{index:02d}.telemetry.json"
                envelope_name = f"{telemetry_name}.envelope.json"
                runs.append({"runIndex": index, "batchOrder": launch["batchOrder"], "resultsPath": result_path, "hostTelemetryFile": telemetry_name, "hostTelemetryEnvelopeFile": envelope_name, "hostTelemetryHealthFile": health_name, "pilotCpuWorkByScenario": work, "pilotCpuWorkIterationsPerBatch": None})
                root = ET.Element("test-run", failed="0")
                rows = fixture_rows(order=launch["batchOrder"], work=work) if work is not None else None
                for scenario in sorted(PILOT.SCENARIOS):
                    case = ET.SubElement(root, "test-case", result="Passed")
                    if rows is None:
                        marker = {"scenario": scenario, "batchOrder": launch["batchOrder"], "firstToSecondRatio": 0.5}
                    else:
                        marker = next(row for row in rows if row["scenario"] == scenario)
                        if scenario in PILOT.TARGETS and multiplier != 1.0:
                            ratio = 0.5 / multiplier
                            marker["cycleRatios"] = [ratio] * 4
                            marker["firstToSecondRatio"] = ratio
                            marker["aggregateRateRatio"] = ratio
                            for cycle in marker["cycleMeasurements"]:
                                cycle["firstActiveSeconds"] = 0.625 / ratio
                                cycle["firstToSecondRatio"] = ratio
                    ET.SubElement(case, "output").text = "DXM_PAIRED_COMPARISON " + json.dumps(marker, separators=(",", ":"))
                archive.writestr(result_path, ET.tostring(root))
                archive.writestr(f"same-player-repeats/{telemetry_name}", json.dumps({"sourceSha256": self.collector_sha}))
                archive.writestr(f"same-player-repeats/{envelope_name}", json.dumps({"unredactedTelemetrySha256": "e" * 64, "telemetryProcessorAffinityMask": "0xFFFF0000"}))
                archive.writestr(
                    f"same-player-repeats/{health_name}",
                    json.dumps({"schemaVersion": 1, "valid": health_valid, "reasons": [] if health_valid else ["sensor-read-error"], "cpuProfileSha256": self.host_profile_sha, "telemetrySha256": "e" * 64}),
                )
            archive.writestr(
                "same-player-repeats/same-player-evidence.json",
                json.dumps({"schemaVersion": 1, "runCount": 5, "playerDirectoryManifestMatches": True, "playerDirectoryManifestBefore": self.player_manifest, "playerDirectoryManifestAfter": self.player_manifest, "runs": runs}),
            )

    def preflight(self):
        return PILOT.preflight(self.schedule_bytes, self.manifest, self.commit, self.root)

    def edit_member(self, build_index, member, edit):
        entry = self.manifest["builds"][build_index]
        path = self.root / entry["artifactPath"]
        with zipfile.ZipFile(path) as archive:
            members = {name: archive.read(name) for name in archive.namelist()}
        payload = json.loads(members[member])
        edit(payload)
        members[member] = json.dumps(payload).encode()
        with zipfile.ZipFile(path, "w") as archive:
            for name, content in members.items():
                archive.writestr(name, content)
        entry["artifactSha256"] = hashlib.sha256(path.read_bytes()).hexdigest()

    def test_complete_schedule_emits_only_arm_blind_validity(self):
        report = self.preflight()
        self.assertEqual(len(report["builds"]), 72)
        self.assertEqual(report["sourceTree"], "b" * 40)
        self.assertEqual(report["pilotJobSeconds"], 72 * 40)
        serialized = json.dumps(report)
        self.assertNotIn("firstToSecondRatio", serialized)
        self.assertNotIn("0.5", serialized)
        self.assertNotIn("condition", serialized)

    def test_preflight_cli_refuses_unblinding_inputs(self):
        schedule_path = self.root / "schedule.json"
        manifest_path = self.root / "manifest.json"
        output_path = self.root / "validity.json"
        schedule_path.write_bytes(self.schedule_bytes)
        manifest_path.write_text(json.dumps(self.manifest))
        command = [sys.executable, str(SOURCE), "--preflight", "--schedule", str(schedule_path), "--artifact-manifest", str(manifest_path), "--expected-commit", self.commit, "--output", str(output_path)]
        valid = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(valid.returncode, 0, valid.stderr)
        self.assertNotIn("firstToSecondRatio", output_path.read_text())
        refused = subprocess.run(command + ["--assignment-key", str(self.root / "sealed-key.json")], capture_output=True, text=True)
        self.assertEqual(refused.returncode, 1)
        self.assertIn("preflight must not read assignment", refused.stderr)

    def test_rejects_missing_or_reordered_artifact_before_reading_xml(self):
        self.manifest["builds"].pop()
        with self.assertRaisesRegex(ValueError, "exactly 72 artifact commitments"):
            self.preflight()
        self.manifest["builds"].append(self.manifest["builds"][0])
        with self.assertRaisesRegex(ValueError, "complete sealed dispatch order"):
            self.preflight()

    def test_rejects_invalid_health_and_reused_build(self):
        entry = self.manifest["builds"][1]
        path = self.root / entry["artifactPath"]
        self.write_artifact(path, 1, self.schedule["units"][0]["builds"][1], health_valid=False)
        entry["artifactSha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        with self.assertRaisesRegex(ValueError, "invalid host health"):
            self.preflight()
        self.write_artifact(path, 1, self.schedule["units"][0]["builds"][1], build_id=str(uuid.UUID(int=1)))
        entry["artifactSha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        with self.assertRaisesRegex(ValueError, "reused clean build identity"):
            self.preflight()

    def test_rejects_unbound_host_health(self):
        self.edit_member(0, "same-player-repeats/run-01.health.json", lambda health: health.update(telemetrySha256="f" * 64))
        with self.assertRaisesRegex(ValueError, "host health evidence binding drift"):
            self.preflight()

    def test_rejects_failed_terminal_license_return(self):
        entry = self.manifest["builds"][0]
        path = self.root / entry["jobEvidencePath"]
        job = json.loads(path.read_text())
        next(step for step in job["steps"] if step["name"] == "Return Unity license")["conclusion"] = "failure"
        path.write_text(json.dumps(job))
        entry["jobEvidenceSha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        with self.assertRaisesRegex(ValueError, "unconfirmed workflow step: Return Unity license"):
            self.preflight()

    def test_rejects_artifact_hash_and_schedule_drift(self):
        self.manifest["builds"][0]["artifactSha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "artifact SHA-256 drift"):
            self.preflight()
        self.manifest["builds"][0]["artifactSha256"] = hashlib.sha256((self.root / "build-00.zip").read_bytes()).hexdigest()
        with self.assertRaisesRegex(ValueError, "sealed pilot schedule SHA-256 drift"):
            PILOT.preflight(self.schedule_bytes + b" ", self.manifest, self.commit, self.root)

    def test_rejects_order_tree_and_player_manifest_drift(self):
        member = "same-player-repeats/same-player-evidence.json"
        self.edit_member(0, member, lambda same: same["runs"][0].update(batchOrder="INVALID"))
        with self.assertRaisesRegex(ValueError, "scheduled launch order drift"):
            self.preflight()
        self.write_artifact(self.root / "build-00.zip", 0, self.schedule["units"][0]["builds"][0])
        self.manifest["builds"][0]["artifactSha256"] = hashlib.sha256((self.root / "build-00.zip").read_bytes()).hexdigest()
        self.edit_member(0, "pilot-build-evidence.json", lambda build: build.update(sourceTree="c" * 40))
        with self.assertRaisesRegex(ValueError, "pilot source tree mismatch"):
            self.preflight()
        self.write_artifact(self.root / "build-00.zip", 0, self.schedule["units"][0]["builds"][0])
        self.manifest["builds"][0]["artifactSha256"] = hashlib.sha256((self.root / "build-00.zip").read_bytes()).hexdigest()
        self.edit_member(0, member, lambda same: same.update(playerDirectoryManifestAfter={"files": []}))
        with self.assertRaisesRegex(ValueError, "player manifest drift"):
            self.preflight()

    def test_rejects_missing_paired_marker_and_dirty_build(self):
        entry = self.manifest["builds"][0]
        path = self.root / entry["artifactPath"]
        with zipfile.ZipFile(path) as archive:
            members = {name: archive.read(name) for name in archive.namelist()}
        members["results.xml"] = b'<test-run failed="0"><test-case result="Passed"/></test-run>'
        with zipfile.ZipFile(path, "w") as archive:
            for name, content in members.items():
                archive.writestr(name, content)
        entry["artifactSha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        with self.assertRaisesRegex(ValueError, "exactly seven test cases"):
            self.preflight()
        self.write_artifact(path, 0, self.schedule["units"][0]["builds"][0])
        entry["artifactSha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        self.edit_member(0, "pilot-build-evidence.json", lambda build: build.update(projectWasAbsentBefore=False))
        with self.assertRaisesRegex(ValueError, "unclean build"):
            self.preflight()

    def test_synthetic_key_analyzes_only_after_sealed_validity(self):
        conditions = ("AA", "P03", "P05", "P10")
        assignments = []
        for block in (1, 2, 3):
            units = [unit for unit in self.schedule["units"] if unit["block"] == block]
            by_orientation = {
                orientation: [unit for unit in units if "".join(build["arm"] for build in unit["builds"]) == orientation]
                for orientation in ("ABA", "BAB")
            }
            for index, condition in enumerate(conditions):
                for orientation in ("ABA", "BAB"):
                    assignments.append({"unitId": by_orientation[orientation][index]["unitId"], "condition": condition, "treatmentArm": "B", "shimArm": None if condition == "AA" else "A", "nominalTreatmentRateRatio": PILOT.NOMINAL_RATIOS[condition]})
        synthetic_key = {"schemaVersion": 1, "purpose": "sealed-pilot-condition-key", "assignments": assignments}
        key_bytes = (json.dumps(synthetic_key, sort_keys=True, separators=(",", ":")) + "\n").encode()
        self.schedule["assignmentKeySha256"] = hashlib.sha256(key_bytes).hexdigest()
        self.schedule_bytes = (json.dumps(self.schedule, sort_keys=True, separators=(",", ":")) + "\n").encode()
        original_sha = PILOT.SCHEDULE_SHA256
        PILOT.SCHEDULE_SHA256 = hashlib.sha256(self.schedule_bytes).hexdigest()
        try:
            work = {condition: {scenario: 0 if condition == "AA" else (index + 1) * 10 for scenario in PILOT.TARGET_ORDER} for index, condition in enumerate(conditions)}
            calibration_bytes = json.dumps({"schemaVersion": 1, "purpose": "control-only-work-calibration", "targets": {scenario: {"proposedIterations": {condition: work[condition][scenario] for condition in ("P03", "P05", "P10")}} for scenario in PILOT.TARGET_ORDER}}).encode()
            confirmation_bytes = b'{"schemaVersion":1,"purpose":"synthetic-physical-confirmation"}'
            settings = {"schemaVersion": 1, "purpose": "510-pilot-work-settings", "scheduleSha256": PILOT.SCHEDULE_SHA256, "sourceCommit": self.commit, "sourceTree": "b" * 40, "serializedEliSecondsBeforePilot": 600, "reducerSha256": hashlib.sha256(SOURCE.read_bytes()).hexdigest(), "extractorSha256": hashlib.sha256(PILOT.EXTRACTOR_PATH.read_bytes()).hexdigest(), "calibrationReportSha256": hashlib.sha256(calibration_bytes).hexdigest(), "physicalConfirmationSha256": hashlib.sha256(confirmation_bytes).hexdigest(), "workByCondition": work}
            conditions_by_unit = {assignment["unitId"]: assignment["condition"] for assignment in assignments}
            zero = {scenario: 0 for scenario in PILOT.TARGET_ORDER}
            for build_index, entry in enumerate(self.manifest["builds"]):
                unit = next(unit for unit in self.schedule["units"] if unit["unitId"] == entry["unitId"])
                scheduled = unit["builds"][entry["slot"] - 1]
                condition = conditions_by_unit[unit["unitId"]]
                shim = scheduled["arm"] == "A" and condition != "AA"
                vector = work[condition] if shim else zero
                multiplier = PILOT.NOMINAL_RATIOS[condition] if shim else 1.0
                path = self.root / entry["artifactPath"]
                self.write_artifact(path, build_index, scheduled, work=vector, multiplier=multiplier)
                entry["artifactSha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
            validity = self.preflight()
            report = PILOT.analyze_artifacts(self.schedule_bytes, self.manifest, validity, key_bytes, settings, calibration_bytes, confirmation_bytes, self.commit, self.root)
            self.assertEqual(len(report["effects"]["P05"]["GlobalToOne"]), 6)
            self.assertAlmostEqual(report["intervals"]["P05"]["GlobalToOne"]["meanLogEffect"], math.log(1.05))
            self.assertTrue(report["intervals"]["P05"]["GlobalToOne"]["aboveThreePercent"])
            invalid_validity = json.loads(json.dumps(validity))
            invalid_validity["builds"][0]["sourceTree"] = "f" * 40
            with self.assertRaisesRegex(ValueError, "sealed arm-blind validity manifest drift"):
                PILOT.analyze_artifacts(self.schedule_bytes, self.manifest, invalid_validity, key_bytes, settings, calibration_bytes, confirmation_bytes, self.commit, self.root)
            with self.assertRaisesRegex(ValueError, "sealed assignment key SHA-256 drift"):
                PILOT.analyze_artifacts(self.schedule_bytes, self.manifest, validity, key_bytes + b" ", settings, calibration_bytes, confirmation_bytes, self.commit, self.root)
            with self.assertRaisesRegex(ValueError, "physical control evidence hash drift"):
                PILOT.analyze_artifacts(self.schedule_bytes, self.manifest, validity, key_bytes, settings, calibration_bytes, confirmation_bytes + b" ", self.commit, self.root)
            settings["serializedEliSecondsBeforePilot"] = 54_000
            with self.assertRaisesRegex(ValueError, "approved serialized ELI time cap exceeded"):
                PILOT.analyze_artifacts(self.schedule_bytes, self.manifest, validity, key_bytes, settings, calibration_bytes, confirmation_bytes, self.commit, self.root)
            settings["serializedEliSecondsBeforePilot"] = 600
            self.edit_member(0, "same-player-repeats/same-player-evidence.json", lambda same: same["runs"][0]["pilotCpuWorkByScenario"].update(GlobalToOne=999))
            changed_validity = self.preflight()
            with self.assertRaisesRegex(ValueError, "pilot vector assignment drift"):
                PILOT.analyze_artifacts(self.schedule_bytes, self.manifest, changed_validity, key_bytes, settings, calibration_bytes, confirmation_bytes, self.commit, self.root)
        finally:
            PILOT.SCHEDULE_SHA256 = original_sha


if __name__ == "__main__":
    unittest.main()
