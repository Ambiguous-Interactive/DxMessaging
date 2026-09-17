#!/usr/bin/env python3
"""Validate #510 pilot artifacts and reduce independent palindrome effects.

The preflight emits no ratios or condition assignments. Inputs to ``reduce``
must also pass raw-cycle validation after the arm-blind validity seal.

The artifact manifest is a JSON object with schemaVersion 1 and 72 ordered
builds: each has unitId, slot, a relative artifactPath, and artifactSha256.
Each also commits workflowRunId, a relative jobEvidencePath, and its SHA-256;
the job JSON is the raw GitHub Actions jobs/{job_id} response.
Run --preflight first and seal its output. --analyze requires that exact output,
the sealed assignment key, and work settings pinned to the final source tree,
calibration, physical confirmation, analysis source hashes, and audited ELI
time spent before the pilot.
"""

import argparse
import hashlib
import importlib.util
import json
import math
import re
import statistics
import sys
import xml.etree.ElementTree as ET
import zipfile
from collections import defaultdict
from datetime import datetime
from pathlib import Path, PurePosixPath


SCENARIOS = frozenset(
    (
        "GlobalToOne",
        "GlobalToMany",
        "KeyedToOne",
        "StructNoBox",
        "Filtered",
        "PostProcess",
        "FilteredPostProcess",
    )
)
SENTINELS = frozenset(("GlobalToMany", "KeyedToOne"))
TARGETS = SCENARIOS - SENTINELS
CONDITIONS = frozenset(("AA", "P03", "P05", "P10"))
NOMINAL_RATIOS = {"AA": 1.0, "P03": 1.03, "P05": 1.05, "P10": 1.10}
# scipy.stats.t.ppf(0.95, df=5), SciPy 1.16.2. Pilot N is fixed at six.
PILOT_T_95_DF5 = 2.0150483733330233
SCHEDULE_SHA256 = "49e71387743b8814d2e297f6bc433988041bf7944c1bfa61dc14de5d894c0429"
PROFILE_PATH = Path(__file__).parents[2] / ".github/perf/canonical-il2cpp-profile.v1.json"
EXTRACTOR_PATH = Path(__file__).with_name("extract-pilot-paired.py")
COLLECTOR_PATH = Path(__file__).with_name("collect-perf-host-characterization.ps1")
TARGET_ORDER = ("GlobalToOne", "StructNoBox", "Filtered", "PostProcess", "FilteredPostProcess")
REQUIRED_JOB_STEPS = (
    "Build and run pilot comparison contracts or calibration",
    "Require five control launches with complete paired rows",
    "Return Unity license",
    "Classify Unity cleanup evidence",
    "Release organization Unity lock",
    "Require confirmed Unity cleanup",
)
PAIRED_MARKER = re.compile(r"DXM_PAIRED_COMPARISON\s+(\{[^\r\n<]+\})")
SCENARIO_FIELD = re.compile(r'"scenario":"([A-Za-z]+)"')
ORDER_FIELD = re.compile(r'"batchOrder":"(ABBABAAB|BAABABBA)"')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def unique_json(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"duplicate JSON key: {key}")
        result[key] = value
    return result


def read_json(archive, name):
    return json.loads(archive.read(name), object_pairs_hook=unique_json)


def sha256_file(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def utc_time(value, label):
    require(isinstance(value, str) and value.endswith("Z"), f"{label}: UTC timestamp required")
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def checked_member(name, prefix=None):
    require(isinstance(name, str) and name and not name.startswith("/") and "\\" not in name, "unsafe archive member")
    parts = PurePosixPath(name).parts
    require(".." not in parts and ":" not in name, "unsafe archive member")
    if prefix is not None:
        require(name.startswith(prefix) and len(name) > len(prefix), "unexpected archive member path")
    return name


def paired_cases_valid(xml_bytes, expected_order):
    root = ET.fromstring(xml_bytes)
    require(root.tag == "test-run" and root.get("failed") == "0", "failed or malformed paired launch XML")
    cases = list(root.iter("test-case"))
    require(len(cases) == 7, "paired launch must contain exactly seven test cases")
    scenarios = set()
    for case in cases:
        require(case.get("result") == "Passed", "paired launch has a failing test case")
        markers = set(PAIRED_MARKER.findall(case.findtext("output") or ""))
        require(len(markers) == 1, "paired launch has missing or conflicting markers")
        marker = next(iter(markers))
        scenario = SCENARIO_FIELD.search(marker)
        order = ORDER_FIELD.search(marker)
        require(scenario is not None and scenario.group(1) in SCENARIOS, "paired launch has an unknown scenario")
        require(scenario.group(1) not in scenarios, "paired launch has a duplicate scenario")
        require(order is not None and order.group(1) == expected_order, "paired launch marker order drift")
        scenarios.add(scenario.group(1))
    require(scenarios == SCENARIOS, "paired launch scenario set drift")


def inspect_pilot_zip(path, expected_sha, expected_commit, scheduled_orders, profile_sha, collector_sha):
    require(sha256_file(path) == expected_sha, f"{path}: artifact SHA-256 drift")
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)), f"{path}: duplicate archive member")
        for name in names:
            checked_member(name)
        build = read_json(archive, "pilot-build-evidence.json")
        same = read_json(archive, "same-player-repeats/same-player-evidence.json")
        host_profile_bytes = archive.read("performance-cpu-profile.json")
        host_profile = json.loads(host_profile_bytes, object_pairs_hook=unique_json)
        require(all(isinstance(value, dict) for value in (build, same, host_profile)), f"{path}: malformed build or host evidence")
        host_profile_sha = hashlib.sha256(host_profile_bytes).hexdigest()
        require(host_profile.get("schemaVersion") == 1 and host_profile.get("executionProfileId") == "highest-efficiency-class-affinity-normal-v1" and "i9-13900KF" in host_profile.get("cpuModel", "") and host_profile.get("processorGroup") == 0 and host_profile.get("logicalProcessorCount") == 32 and host_profile.get("selectedLogicalProcessorIndices") == list(range(16)) and host_profile.get("selectedLogicalProcessorCount") == 16 and host_profile.get("selectedCoreCount") == 8 and host_profile.get("affinityMask") == "0xFFFF" and host_profile.get("priorityClass") == "Normal", f"{path}: ELI CPU profile drift")
        require(build.get("schemaVersion") == 1 and build.get("projectWasAbsentBefore") is True, f"{path}: unclean build")
        require(build.get("sourceCommit") == expected_commit, f"{path}: source commit drift")
        require(re.fullmatch(r"[0-9a-f]{40}", build.get("sourceTree", "")), f"{path}: source tree drift")
        require(re.fullmatch(r"[0-9a-f-]{36}", build.get("buildInvocationId", "")), f"{path}: invalid build ID")
        require(build.get("unityVersion") == "6000.5.2f1", f"{path}: Unity version drift")
        require(build.get("canonicalProfileId") == "canonical-il2cpp-verdict-player-v1" and build.get("canonicalProfileSha256") == profile_sha, f"{path}: canonical profile drift")
        started = utc_time(build.get("buildStartedUtc"), f"{path}.buildStartedUtc")
        finished = utc_time(build.get("buildFinishedUtc"), f"{path}.buildFinishedUtc")
        require(started < finished, f"{path}: build timestamps are reversed")
        require(same.get("schemaVersion") == 1 and same.get("runCount") == 5, f"{path}: launch count drift")
        require(same.get("playerDirectoryManifestMatches") is True, f"{path}: player changed between launches")
        manifest = build.get("playerDirectoryManifest")
        require(manifest == same.get("playerDirectoryManifestBefore") == same.get("playerDirectoryManifestAfter"), f"{path}: player manifest drift")
        runs = same.get("runs")
        require(isinstance(runs, list) and len(runs) == 5, f"{path}: five launch records required")
        seen_paths = set()
        launch_hashes = []
        for index, run in enumerate(runs, 1):
            require(isinstance(run, dict), f"{path}: malformed launch record")
            require(run.get("runIndex") == index and run.get("batchOrder") == scheduled_orders[index - 1], f"{path}: scheduled launch order drift")
            result_path = checked_member(run.get("resultsPath"))
            telemetry_name = checked_member(run.get("hostTelemetryFile"))
            envelope_name = checked_member(run.get("hostTelemetryEnvelopeFile"))
            health_name = checked_member(run.get("hostTelemetryHealthFile"))
            telemetry_path = checked_member(f"same-player-repeats/{telemetry_name}", "same-player-repeats/")
            envelope_path = checked_member(f"same-player-repeats/{envelope_name}", "same-player-repeats/")
            health_path = checked_member(f"same-player-repeats/{health_name}", "same-player-repeats/")
            new_paths = (result_path, telemetry_path, envelope_path, health_path)
            require(len(set(new_paths)) == 4 and not seen_paths.intersection(new_paths), f"{path}: reused launch evidence path")
            seen_paths.update(new_paths)
            health = read_json(archive, health_path)
            require(isinstance(health, dict), f"{path}: malformed host health")
            require(health.get("schemaVersion") == 1 and health.get("valid") is True and health.get("reasons") == [], f"{path}: invalid host health, launch {index}")
            telemetry = read_json(archive, telemetry_path)
            envelope = read_json(archive, envelope_path)
            require(isinstance(telemetry, dict) and isinstance(envelope, dict), f"{path}: malformed telemetry evidence")
            require(telemetry.get("sourceSha256") == collector_sha, f"{path}: telemetry collector source drift")
            require(health.get("cpuProfileSha256") == host_profile_sha and health.get("telemetrySha256") == envelope.get("unredactedTelemetrySha256") and envelope.get("telemetryProcessorAffinityMask") == "0xFFFF0000", f"{path}: host health evidence binding drift")
            xml_bytes = archive.read(result_path)
            paired_cases_valid(xml_bytes, run["batchOrder"])
            launch_hashes.append({"index": index, "batchOrder": run["batchOrder"], "resultsSha256": hashlib.sha256(xml_bytes).hexdigest(), "healthSha256": hashlib.sha256(archive.read(health_path)).hexdigest()})
        return {"buildId": build["buildInvocationId"], "sourceTree": build["sourceTree"], "hostCpuProfileSha256": host_profile_sha, "buildStartedUtc": build["buildStartedUtc"], "buildFinishedUtc": build["buildFinishedUtc"], "launches": launch_hashes}


def inspect_workflow_job(path, expected_sha, expected_run_id, expected_commit, build):
    require(sha256_file(path) == expected_sha, f"{path}: workflow job evidence SHA-256 drift")
    job = json.loads(path.read_bytes(), object_pairs_hook=unique_json)
    require(isinstance(job, dict), f"{path}: malformed workflow job evidence")
    require(job.get("run_id") == expected_run_id and type(job.get("id")) is int and job["id"] > 0, f"{path}: workflow job identity drift")
    require(job.get("head_sha") == expected_commit and job.get("name") == "Pilot IL2CPP contract, calibration, vector, or license recovery on ELI", f"{path}: workflow source or job drift")
    require(job.get("status") == "completed" and job.get("conclusion") == "success", f"{path}: workflow job did not succeed")
    steps = job.get("steps")
    require(isinstance(steps, list) and all(isinstance(step, dict) for step in steps), f"{path}: missing workflow step evidence")
    for name in REQUIRED_JOB_STEPS:
        matching = [step for step in steps if step.get("name") == name]
        require(len(matching) == 1 and matching[0].get("status") == "completed" and matching[0].get("conclusion") == "success", f"{path}: unconfirmed workflow step: {name}")
    started = utc_time(job.get("started_at"), f"{path}.started_at")
    completed = utc_time(job.get("completed_at"), f"{path}.completed_at")
    build_started = utc_time(build["buildStartedUtc"], "buildStartedUtc")
    build_finished = utc_time(build["buildFinishedUtc"], "buildFinishedUtc")
    require(started <= build_started < build_finished <= completed, f"{path}: job/build time envelope drift")
    return {"workflowRunId": expected_run_id, "workflowJobId": job["id"], "jobEvidenceSha256": expected_sha, "jobSeconds": (completed - started).total_seconds()}


def preflight(schedule_bytes, manifest, expected_commit, base_path):
    require(hashlib.sha256(schedule_bytes).hexdigest() == SCHEDULE_SHA256, "sealed pilot schedule SHA-256 drift")
    schedule = json.loads(schedule_bytes, object_pairs_hook=unique_json)
    require(isinstance(schedule, dict) and isinstance(manifest, dict), "pilot schedule or artifact manifest must be an object")
    require(schedule.get("schemaVersion") == 1 and schedule.get("purpose") == "blinded-three-build-palindrome-pilot" and schedule.get("approvedCleanBuilds") == 72 and schedule.get("approvedPlayerLaunches") == 360, "pilot schedule contract drift")
    require(re.fullmatch(r"[0-9a-f]{40}", expected_commit), "expected commit must be a 40-character Git ID")
    entries = manifest.get("builds")
    require(manifest.get("schemaVersion") == 1 and isinstance(entries, list) and len(entries) == 72, "exactly 72 artifact commitments required")
    expected = [(unit["unitId"], build["slot"], build) for unit in schedule["units"] for build in unit["builds"]]
    require(len(schedule["units"]) == 24 and len(expected) == 72, "schedule unit/build count drift")
    positions = [(entry.get("unitId"), entry.get("slot")) for entry in entries]
    require(positions == [(unit_id, slot) for unit_id, slot, _ in expected], "artifact manifest must follow the complete sealed dispatch order")
    profile_sha = hashlib.sha256(PROFILE_PATH.read_bytes()).hexdigest()
    collector_sha = hashlib.sha256(COLLECTOR_PATH.read_bytes()).hexdigest()
    result = []
    seen_build_ids = set()
    seen_trees = set()
    seen_run_ids = set()
    seen_job_ids = set()
    pilot_job_seconds = 0.0
    previous_finished = None
    for entry, (unit_id, slot, scheduled) in zip(entries, expected):
        require(re.fullmatch(r"[0-9a-f]{64}", entry.get("artifactSha256", "")), "invalid artifact SHA-256 commitment")
        artifact_name = checked_member(entry.get("artifactPath"))
        require(artifact_name.endswith(".zip"), "invalid artifact path")
        path = base_path / artifact_name
        require(scheduled["cleanBuild"] is True and scheduled["slot"] == slot, "schedule clean-build drift")
        orders = [launch["batchOrder"] for launch in scheduled["launches"]]
        require(len(orders) == 5, "scheduled launch count drift")
        inspected = inspect_pilot_zip(path, entry["artifactSha256"], expected_commit, orders, profile_sha, collector_sha)
        run_id = entry.get("workflowRunId")
        require(type(run_id) is int and run_id > 0 and run_id not in seen_run_ids, "reused or missing workflow run ID")
        job_name = checked_member(entry.get("jobEvidencePath"))
        require(re.fullmatch(r"[0-9a-f]{64}", entry.get("jobEvidenceSha256", "")), "invalid job evidence SHA-256")
        job = inspect_workflow_job(base_path / job_name, entry["jobEvidenceSha256"], run_id, expected_commit, inspected)
        require(job["workflowJobId"] not in seen_job_ids, "reused workflow job ID")
        seen_run_ids.add(run_id)
        seen_job_ids.add(job["workflowJobId"])
        pilot_job_seconds += job["jobSeconds"]
        require(inspected["buildId"] not in seen_build_ids, "reused clean build identity")
        seen_build_ids.add(inspected["buildId"])
        seen_trees.add(inspected["sourceTree"])
        started = utc_time(inspected["buildStartedUtc"], "buildStartedUtc")
        require(previous_finished is None or previous_finished < started, "pilot build dispatch order drift")
        previous_finished = utc_time(inspected["buildFinishedUtc"], "buildFinishedUtc")
        result.append({"unitId": unit_id, "slot": slot, "artifactSha256": entry["artifactSha256"], **job, **inspected})
    require(len(seen_trees) == 1, "pilot source tree mismatch")
    return {"schemaVersion": 1, "purpose": "510-arm-blind-pilot-validity", "scheduleSha256": SCHEDULE_SHA256, "artifactManifestCanonicalSha256": hashlib.sha256(json.dumps(manifest, sort_keys=True, separators=(",", ":")).encode()).hexdigest(), "sourceCommit": expected_commit, "sourceTree": next(iter(seen_trees)), "validatorSourceSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "pilotJobSeconds": pilot_job_seconds, "builds": result}


def validate_work_settings(settings, expected_commit, source_tree, calibration_bytes, confirmation_bytes):
    require(isinstance(settings, dict), "pilot work settings must be an object")
    require(settings.get("schemaVersion") == 1 and settings.get("purpose") == "510-pilot-work-settings", "pilot work-settings contract drift")
    require(settings.get("scheduleSha256") == SCHEDULE_SHA256 and settings.get("sourceCommit") == expected_commit and settings.get("sourceTree") == source_tree, "pilot work-settings provenance drift")
    prior_seconds = settings.get("serializedEliSecondsBeforePilot")
    require(type(prior_seconds) in (int, float) and math.isfinite(prior_seconds) and 0 <= prior_seconds, "invalid pre-pilot ELI usage")
    source_sha = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    extractor_sha = hashlib.sha256(EXTRACTOR_PATH.read_bytes()).hexdigest()
    require(settings.get("reducerSha256") == source_sha and settings.get("extractorSha256") == extractor_sha, "pilot analysis source hash drift")
    for field in ("calibrationReportSha256", "physicalConfirmationSha256"):
        require(re.fullmatch(r"[0-9a-f]{64}", settings.get(field, "")), f"invalid {field}")
    require(hashlib.sha256(calibration_bytes).hexdigest() == settings["calibrationReportSha256"] and hashlib.sha256(confirmation_bytes).hexdigest() == settings["physicalConfirmationSha256"], "physical control evidence hash drift")
    calibration = json.loads(calibration_bytes, object_pairs_hook=unique_json)
    require(isinstance(calibration, dict) and calibration.get("schemaVersion") == 1 and calibration.get("purpose") == "control-only-work-calibration", "control calibration report drift")
    vectors = settings.get("workByCondition")
    require(isinstance(vectors, dict) and vectors.keys() == CONDITIONS, "pilot work condition set drift")
    for condition, vector in vectors.items():
        require(isinstance(vector, dict) and vector.keys() == set(TARGET_ORDER), f"{condition}: target work set drift")
        require(all(type(value) is int and 0 <= value <= 1_000_000 for value in vector.values()), f"{condition}: invalid target work")
        if condition == "AA":
            require(all(value == 0 for value in vector.values()), "A/A control must have zero work")
        else:
            require(all(value > 0 for value in vector.values()), f"{condition}: physical control work is missing")
    for scenario in TARGET_ORDER:
        require(vectors["P03"][scenario] <= vectors["P05"][scenario] <= vectors["P10"][scenario], f"{scenario}: nonmonotone control work")
        proposals = calibration.get("targets", {}).get(scenario, {}).get("proposedIterations", {})
        require(all(proposals.get(condition) == vectors[condition][scenario] for condition in ("P03", "P05", "P10")), f"{scenario}: calibration proposal drift")
    return vectors


def analyze_artifacts(schedule_bytes, manifest, validity, key_bytes, settings, calibration_bytes, confirmation_bytes, expected_commit, base_path):
    verified = preflight(schedule_bytes, manifest, expected_commit, base_path)
    require(isinstance(validity, dict), "sealed validity manifest must be an object")
    require(verified == validity, "sealed arm-blind validity manifest drift")
    schedule = json.loads(schedule_bytes, object_pairs_hook=unique_json)
    require(hashlib.sha256(key_bytes).hexdigest() == schedule["assignmentKeySha256"], "sealed assignment key SHA-256 drift")
    key = json.loads(key_bytes, object_pairs_hook=unique_json)
    require(isinstance(key, dict), "sealed assignment key must be an object")
    require(key.get("schemaVersion") == 1 and key.get("purpose") == "sealed-pilot-condition-key", "pilot assignment key contract drift")
    assignments = key.get("assignments")
    require(isinstance(assignments, list) and len(assignments) == 24, "pilot assignment count drift")
    vectors = validate_work_settings(settings, expected_commit, validity["sourceTree"], calibration_bytes, confirmation_bytes)
    require(settings["serializedEliSecondsBeforePilot"] + validity["pilotJobSeconds"] <= schedule["hardSerializedRunnerSeconds"], "approved serialized ELI time cap exceeded")
    spec = importlib.util.spec_from_file_location("pilot_paired_extractor", EXTRACTOR_PATH)
    extractor = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(extractor)
    conditions = {assignment["unitId"]: assignment["condition"] for assignment in assignments}
    require(len(conditions) == 24, "duplicate pilot assignment")
    schedule_units = {unit["unitId"]: unit for unit in schedule["units"]}
    zero = {scenario: 0 for scenario in TARGET_ORDER}
    builds = []
    platforms = set()
    for entry, valid in zip(manifest["builds"], validity["builds"]):
        unit_id, slot = entry["unitId"], entry["slot"]
        require(unit_id in conditions and conditions[unit_id] in CONDITIONS, "unscheduled pilot condition")
        scheduled = schedule_units[unit_id]["builds"][slot - 1]
        condition = conditions[unit_id]
        work = vectors[condition] if scheduled["arm"] == "A" else zero
        launches = []
        with zipfile.ZipFile(base_path / entry["artifactPath"]) as archive:
            same = read_json(archive, "same-player-repeats/same-player-evidence.json")
            for run in same["runs"]:
                require(run.get("pilotCpuWorkIterationsPerBatch") is None and run.get("pilotCpuWorkByScenario") == work, "pilot vector assignment drift")
                extracted = extractor.extract_xml(archive.read(run["resultsPath"]), run["batchOrder"], work)
                require(extracted["commit"] == expected_commit, "pilot raw row source commit drift")
                platforms.add(extracted["platform"])
                launches.append({"index": run["runIndex"], "batchOrder": run["batchOrder"], "launchId": f"{valid['buildId']}:{run['runIndex']}", "ratios": {scenario: row["ratio"] for scenario, row in extracted["rows"].items()}})
        builds.append({"unitId": unit_id, "slot": slot, "buildId": valid["buildId"], "sourceTree": valid["sourceTree"], "launches": launches})
    require(len(platforms) == 1, "pilot platform drift")
    effects = reduce(schedule["units"], assignments, builds)
    intervals = pilot_intervals(effects)
    return {"schemaVersion": 1, "purpose": "510-pilot-hierarchical-effects", "sourceCommit": expected_commit, "sourceTree": validity["sourceTree"], "platform": next(iter(platforms)), "serializedEliSecondsTotal": settings["serializedEliSecondsBeforePilot"] + validity["pilotJobSeconds"], "serializedEliSecondsCap": schedule["hardSerializedRunnerSeconds"], "validityManifestCanonicalSha256": hashlib.sha256(json.dumps(validity, sort_keys=True, separators=(",", ":")).encode()).hexdigest(), "assignmentKeySha256": schedule["assignmentKeySha256"], "workSettingsCanonicalSha256": hashlib.sha256(json.dumps(settings, sort_keys=True, separators=(",", ":")).encode()).hexdigest(), "effects": effects, "intervals": intervals}


def launch_effects(ratios):
    require(isinstance(ratios, dict) and ratios.keys() == SCENARIOS, "launch scenario set drift")
    logs = {}
    for scenario, ratio in ratios.items():
        require(type(ratio) in (int, float) and math.isfinite(ratio) and ratio > 0, f"{scenario}: invalid ratio")
        logs[scenario] = math.log(ratio)
    sentinel_mean = sum(logs[scenario] for scenario in SENTINELS) / 2
    return {scenario: logs[scenario] - sentinel_mean for scenario in SCENARIOS}


def reduce(schedule, assignments, builds):
    """Return condition -> scenario -> six unit effects from 72 validated builds.

    A build record has unitId, slot, buildId, sourceTree, and five launches.
    Each launch has index, batchOrder, launchId, and seven ratio values. Callers
    must prove the build/health/raw-XML evidence before constructing records.
    """
    require(len(schedule) == 24, "schedule must contain 24 palindromes")
    require(len(assignments) == 24, "assignment key must contain 24 palindromes")
    require(len(builds) == 72, "pilot must contain exactly 72 independently clean builds")
    by_unit = {unit["unitId"]: unit for unit in schedule}
    require(len(by_unit) == 24, "duplicate scheduled unit")
    assignment_by_unit = {assignment["unitId"]: assignment for assignment in assignments}
    require(len(assignment_by_unit) == 24 and assignment_by_unit.keys() == by_unit.keys(), "assignment key drift")
    seen_builds = set()
    seen_launches = set()
    actual = {}
    source_trees = set()
    for build in builds:
        unit_id = build.get("unitId")
        slot = build.get("slot")
        require(unit_id in by_unit and type(slot) is int and slot in (1, 2, 3), "unscheduled build slot")
        position = (unit_id, slot)
        require(position not in actual, "duplicate build slot")
        scheduled = by_unit[unit_id]["builds"][slot - 1]
        require(scheduled["slot"] == slot and scheduled["cleanBuild"] is True, "schedule build drift")
        build_id = build.get("buildId")
        require(isinstance(build_id, str) and build_id and build_id not in seen_builds, "shared or missing build identity")
        seen_builds.add(build_id)
        source_tree = build.get("sourceTree")
        require(isinstance(source_tree, str) and re.fullmatch(r"[0-9a-f]{40}", source_tree), "missing source tree")
        source_trees.add(source_tree)
        launches = build.get("launches")
        require(isinstance(launches, list) and len(launches) == 5, "build requires five launches")
        launch_values = []
        for index, launch in enumerate(launches, 1):
            expected = scheduled["launches"][index - 1]
            require(launch.get("index") == index == expected["index"], "launch index drift")
            require(launch.get("batchOrder") == expected["batchOrder"], "launch batch order drift")
            launch_id = launch.get("launchId")
            require(isinstance(launch_id, str) and launch_id and launch_id not in seen_launches, "shared or missing launch identity")
            seen_launches.add(launch_id)
            launch_values.append(launch_effects(launch.get("ratios")))
        actual[position] = {
            "arm": scheduled["arm"],
            "values": {
                scenario: sum(value[scenario] for value in launch_values) / 5
                for scenario in SCENARIOS
            },
        }
    require(len(actual) == 72 and len(seen_launches) == 360, "missing scheduled build or launch")
    require(len(source_trees) == 1, "source tree mismatch")
    effects = defaultdict(lambda: defaultdict(list))
    orientation_counts = defaultdict(lambda: defaultdict(int))
    for unit in schedule:
        unit_id = unit["unitId"]
        assignment = assignment_by_unit[unit_id]
        condition = assignment.get("condition")
        require(condition in CONDITIONS and assignment.get("treatmentArm") == "B", "condition or treatment drift")
        require(assignment.get("shimArm") == (None if condition == "AA" else "A"), "shim arm drift")
        require(assignment.get("nominalTreatmentRateRatio") == NOMINAL_RATIOS[condition], "nominal ratio drift")
        slots = [actual[(unit_id, slot)] for slot in (1, 2, 3)]
        orientation = "".join(slot["arm"] for slot in slots)
        require(orientation in ("ABA", "BAB"), "changed outer arms")
        orientation_counts[(unit["block"], condition)][orientation] += 1
        for scenario in SCENARIOS:
            center = slots[1]["values"][scenario]
            outer = (slots[0]["values"][scenario] + slots[2]["values"][scenario]) / 2
            effect = center - outer if orientation == "ABA" else outer - center
            effects[condition][scenario].append({"unitId": unit_id, "logEffect": effect})
    for block in (1, 2, 3):
        for condition in CONDITIONS:
            require(orientation_counts[(block, condition)] == {"ABA": 1, "BAB": 1}, "block orientation imbalance")
    for condition in CONDITIONS:
        for scenario in SCENARIOS:
            require(len(effects[condition][scenario]) == 6, "condition lacks six independent units")
    return {condition: dict(rows) for condition, rows in effects.items()}


def pilot_intervals(effects):
    """Compute pilot Student-t intervals using six palindromes per condition.

    The one-sided 95% lower bound and two-sided 90% TOST interval share the
    same t critical value. Flags are row diagnostics, not a global pilot verdict.
    """
    require(isinstance(effects, dict) and effects.keys() == CONDITIONS, "pilot condition set drift")
    result = {}
    for condition in sorted(CONDITIONS):
        rows = effects[condition]
        require(isinstance(rows, dict) and rows.keys() == SCENARIOS, "pilot scenario set drift")
        result[condition] = {}
        for scenario in sorted(SCENARIOS):
            units = rows[scenario]
            require(isinstance(units, list) and len(units) == 6, "interval needs six independent palindromes")
            identities = [unit.get("unitId") for unit in units]
            require(all(isinstance(identity, str) and identity for identity in identities), "missing palindrome identity")
            require(len(set(identities)) == 6, "pseudo-replicated palindrome identity")
            values = [unit.get("logEffect") for unit in units]
            require(all(type(value) in (int, float) and math.isfinite(value) for value in values), "invalid unit effect")
            mean = statistics.mean(values)
            standard_error = statistics.stdev(values) / math.sqrt(6)
            half_width = PILOT_T_95_DF5 * standard_error
            lower = mean - half_width
            upper = mean + half_width
            result[condition][scenario] = {
                "nIndependentPalindromes": 6,
                "meanLogEffect": mean,
                "sampleStandardDeviation": statistics.stdev(values),
                "lower95OneSidedLog": lower,
                "lower90TwoSidedLog": lower,
                "upper90TwoSidedLog": upper,
                "aboveThreePercent": lower > math.log(1.03),
                "affectedRowSafe": lower > math.log(0.97),
                "equivalentWithinThreePercent": lower > math.log(0.97)
                and upper < math.log(1.03),
            }
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    phase = parser.add_mutually_exclusive_group(required=True)
    phase.add_argument("--preflight", action="store_true")
    phase.add_argument("--analyze", action="store_true")
    parser.add_argument("--schedule", type=Path, required=True)
    parser.add_argument("--artifact-manifest", type=Path, required=True)
    parser.add_argument("--expected-commit", required=True)
    parser.add_argument("--validity-manifest", type=Path)
    parser.add_argument("--assignment-key", type=Path)
    parser.add_argument("--work-settings", type=Path)
    parser.add_argument("--calibration-report", type=Path)
    parser.add_argument("--physical-confirmation-report", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        manifest = json.loads(args.artifact_manifest.read_bytes(), object_pairs_hook=unique_json)
        schedule_bytes = args.schedule.read_bytes()
        if args.preflight:
            require(not any((args.validity_manifest, args.assignment_key, args.work_settings, args.calibration_report, args.physical_confirmation_report)), "preflight must not read assignment or analysis inputs")
            report = preflight(schedule_bytes, manifest, args.expected_commit, args.artifact_manifest.parent)
        else:
            require(all((args.validity_manifest, args.assignment_key, args.work_settings, args.calibration_report, args.physical_confirmation_report)), "analysis requires sealed validity, assignment key, work settings, and physical control reports")
            validity = json.loads(args.validity_manifest.read_bytes(), object_pairs_hook=unique_json)
            settings = json.loads(args.work_settings.read_bytes(), object_pairs_hook=unique_json)
            report = analyze_artifacts(schedule_bytes, manifest, validity, args.assignment_key.read_bytes(), settings, args.calibration_report.read_bytes(), args.physical_confirmation_report.read_bytes(), args.expected_commit, args.artifact_manifest.parent)
        with args.output.open("x", encoding="utf-8") as output:
            output.write(json.dumps(report, sort_keys=True, separators=(",", ":")) + "\n")
    except (OSError, ValueError, TypeError, KeyError, ET.ParseError, zipfile.BadZipFile) as error:
        print(f"invalid pilot preflight: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
