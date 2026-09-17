#!/usr/bin/env python3
"""Reduce the fixed six-build #510 control-only CPU-work sweep.

The output proposes work settings for independent physical confirmation. Six
builds are calibration material, never independent pilot palindromes.
"""

import argparse
import hashlib
import importlib.util
import json
import math
import platform
import re
import sys
import xml.etree.ElementTree as ET
import zipfile
from datetime import datetime
from pathlib import Path, PurePosixPath


WORK_LEVELS = (0, 2048, 8192, 32768, 131072, 524288)
DISPATCH_ORDER = (32768, 0, 131072, 2048, 524288, 8192)
TARGETS = ("GlobalToOne", "StructNoBox", "Filtered", "PostProcess", "FilteredPostProcess")
SENTINELS = ("GlobalToMany", "KeyedToOne")
TRUTH_RATIOS = {"P03": 1.03, "P05": 1.05, "P10": 1.10}
ORDERS = ("ABBABAAB", "BAABABBA")
EXTRACTOR_PATH = Path(__file__).with_name("extract-pilot-paired.py")
PILOT_REDUCER_PATH = Path(__file__).with_name("reduce-pilot-effects.py")


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


def load_extractor():
    spec = importlib.util.spec_from_file_location("pilot_paired_extractor", EXTRACTOR_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def load_pilot_reducer():
    spec = importlib.util.spec_from_file_location("pilot_effects", PILOT_REDUCER_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def preflight(manifest, expected_commit, base_path):
    """Validate all six builds and terminal jobs before opening paired rates."""
    pilot = load_pilot_reducer()
    require(isinstance(manifest, dict) and manifest.get("schemaVersion") == 1 and manifest.get("purpose") == "510-control-only-work-calibration-evidence", "calibration evidence manifest drift")
    entries = manifest.get("builds")
    require(isinstance(entries, list) and len(entries) == 6, "six calibration artifact and job commitments required")
    profile_sha = pilot.sha256_file(pilot.PROFILE_PATH)
    collector_sha = pilot.sha256_file(pilot.COLLECTOR_PATH)
    fixed_orders = (ORDERS[0], ORDERS[1], ORDERS[0], ORDERS[1], ORDERS[0])
    artifacts = {}
    jobs = {}
    seen_builds = set()
    seen_runs = set()
    seen_jobs = set()
    trees = set()
    previous_job_end = None
    for entry, level in zip(entries, DISPATCH_ORDER):
        require(isinstance(entry, dict) and type(entry.get("work")) is int and entry["work"] == level, "calibration dispatch order drift")
        path = base_path / pilot.checked_member(entry.get("artifactPath"))
        job_path = base_path / pilot.checked_member(entry.get("jobEvidencePath"))
        artifact_sha = entry.get("artifactSha256")
        job_sha = entry.get("jobEvidenceSha256")
        require(isinstance(artifact_sha, str) and re.fullmatch(r"[0-9a-f]{64}", artifact_sha) and isinstance(job_sha, str) and re.fullmatch(r"[0-9a-f]{64}", job_sha), "calibration evidence SHA-256 missing")
        build = pilot.inspect_pilot_zip(path, artifact_sha, expected_commit, fixed_orders, profile_sha, collector_sha)
        with zipfile.ZipFile(path) as archive:
            same = pilot.read_json(archive, "same-player-repeats/same-player-evidence.json")
            require(all(run.get("pilotCpuWorkIterationsPerBatch") == level and run.get("pilotCpuWorkByScenario") is None for run in same["runs"]), "calibration scalar work drift")
        run_id = entry.get("workflowRunId")
        require(type(run_id) is int and run_id > 0 and run_id not in seen_runs, "reused calibration workflow run")
        job = pilot.inspect_workflow_job(job_path, job_sha, run_id, expected_commit, build)
        require(build["buildId"] not in seen_builds and job["workflowJobId"] not in seen_jobs, "reused calibration build or job")
        started = pilot.utc_time(build["buildStartedUtc"], "buildStartedUtc")
        require(previous_job_end is None or previous_job_end < started, "calibration job order drift")
        previous_job_end = pilot.utc_time(job["jobCompletedUtc"], "job.completed_at")
        seen_builds.add(build["buildId"])
        seen_runs.add(run_id)
        seen_jobs.add(job["workflowJobId"])
        trees.add(build["sourceTree"])
        artifacts[level] = path
        jobs[str(level)] = job
    require(len(trees) == 1, "calibration source tree drift")
    return artifacts, jobs


def isotonic_nonnegative(values):
    """Equal-weight PAV for nonzero work levels, anchored at zero work/effect."""
    blocks = []
    for value in values[1:]:
        require(math.isfinite(value), "nonfinite calibration response")
        blocks.append([value, 1])
        while len(blocks) > 1 and blocks[-2][0] > blocks[-1][0]:
            right_mean, right_count = blocks.pop()
            left_mean, left_count = blocks.pop()
            count = left_count + right_count
            blocks.append([(left_mean * left_count + right_mean * right_count) / count, count])
    return [0.0] + [max(0.0, mean) for mean, count in blocks for _ in range(count)]


def interpolated_work(levels, responses, target):
    require(len(levels) == len(responses) and levels[0] == 0, "calibration grid mismatch")
    require(all(a <= b for a, b in zip(responses, responses[1:])), "nonmonotone projected response")
    if responses[-1] < target:
        return None
    for index in range(1, len(levels)):
        if responses[index] >= target:
            left, right = responses[index - 1], responses[index]
            if right == left:
                return levels[index - 1]
            fraction = (target - left) / (right - left)
            return math.ceil(levels[index - 1] + fraction * (levels[index] - levels[index - 1]))
    raise AssertionError("reachable target was not bracketed")


def inspect_artifact(path, work, expected_commit, extractor):
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)), f"{path}: duplicate archive member")
        for name in names:
            parts = PurePosixPath(name).parts
            require(name and not name.startswith("/") and ".." not in parts, f"{path}: unsafe archive member")
        build = read_json(archive, "pilot-build-evidence.json")
        same = read_json(archive, "same-player-repeats/same-player-evidence.json")
        require(build.get("schemaVersion") == 1 and build.get("projectWasAbsentBefore") is True, f"{path}: unclean build")
        require(build.get("sourceCommit") == expected_commit, f"{path}: source commit drift")
        require(re.fullmatch(r"[0-9a-f]{40}", build.get("sourceTree", "")), f"{path}: invalid source tree")
        require(re.fullmatch(r"[0-9a-f-]{36}", build.get("buildInvocationId", "")), f"{path}: invalid build ID")
        require(build.get("unityVersion") == "6000.5.2f1", f"{path}: Unity version drift")
        require(build.get("canonicalProfileId") == "canonical-il2cpp-verdict-player-v1", f"{path}: profile drift")
        started_text = build.get("buildStartedUtc", "")
        require(isinstance(started_text, str) and started_text.endswith("Z"), f"{path}: build start must be UTC")
        datetime.fromisoformat(started_text.replace("Z", "+00:00"))
        require(same.get("schemaVersion") == 1 and same.get("runCount") == 5, f"{path}: launch count drift")
        require(same.get("playerDirectoryManifestMatches") is True, f"{path}: player bytes changed")
        manifest = build.get("playerDirectoryManifest")
        require(manifest == same.get("playerDirectoryManifestBefore") == same.get("playerDirectoryManifestAfter"), f"{path}: build/player manifest drift")
        runs = same.get("runs")
        require(isinstance(runs, list) and len(runs) == 5, f"{path}: five launch records required")
        require([run.get("runIndex") for run in runs] == [1, 2, 3, 4, 5], f"{path}: launch order drift")
        effects = {scenario: [] for scenario in (*TARGETS, *SENTINELS)}
        orders = []
        for run in runs:
            index = run["runIndex"]
            order = run.get("batchOrder")
            require(order in ORDERS and run.get("pilotCpuWorkIterationsPerBatch") == work, f"{path}: scheduled work/order drift")
            health = read_json(archive, f"same-player-repeats/{run['hostTelemetryHealthFile']}")
            require(health.get("schemaVersion") == 1 and health.get("valid") is True and health.get("reasons") == [], f"{path}: invalid host health, launch {index}")
            rows = extractor.extract_xml(archive.read(run["resultsPath"]), order, work)
            require(rows["commit"] == expected_commit, f"{path}: row source drift, launch {index}")
            sentinel = sum(math.log(rows["rows"][scenario]["ratio"]) for scenario in SENTINELS) / 2
            for scenario in effects:
                effects[scenario].append(math.log(rows["rows"][scenario]["ratio"]) - sentinel)
            orders.append(order)
        require(orders.count(ORDERS[0]) in (2, 3) and orders.count(ORDERS[1]) in (2, 3), f"{path}: unbalanced batch orders")
        return {
            "archiveSha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "buildInvocationId": build["buildInvocationId"],
            "sourceTree": build["sourceTree"],
            "buildStartedUtc": build["buildStartedUtc"],
            "meanNormalizedLogRatio": {scenario: sum(values) / 5 for scenario, values in effects.items()},
        }


def calibrate(artifacts, expected_commit, extractor):
    require(set(artifacts) == set(WORK_LEVELS), "all six fixed calibration levels are required")
    builds = {level: inspect_artifact(artifacts[level], level, expected_commit, extractor) for level in WORK_LEVELS}
    require(len({build["buildInvocationId"] for build in builds.values()}) == 6, "calibration reused a clean build")
    require(len({build["sourceTree"] for build in builds.values()}) == 1, "calibration source tree drift")
    actual_order = tuple(sorted(WORK_LEVELS, key=lambda level: datetime.fromisoformat(builds[level]["buildStartedUtc"].replace("Z", "+00:00"))))
    require(actual_order == DISPATCH_ORDER, "calibration dispatch order drift")
    report = {"schemaVersion": 1, "purpose": "control-only-work-calibration", "sourceCommit": expected_commit,
              "levels": list(WORK_LEVELS), "builds": {str(level): {key: value for key, value in builds[level].items() if key != "meanNormalizedLogRatio"} for level in WORK_LEVELS}, "targets": {}}
    for scenario in TARGETS:
        responses = [builds[0]["meanNormalizedLogRatio"][scenario] - builds[level]["meanNormalizedLogRatio"][scenario] for level in WORK_LEVELS]
        projected = isotonic_nonnegative(responses)
        report["targets"][scenario] = {
            "observedLogEffects": responses,
            "projectedLogEffects": projected,
            "proposedIterations": {condition: interpolated_work(WORK_LEVELS, projected, math.log(ratio)) for condition, ratio in TRUTH_RATIOS.items()},
        }
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--expected-commit", required=True)
    parser.add_argument("--artifact-manifest", type=Path, required=True, help="six ordered artifact ZIP and raw Actions job commitments")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        config_bytes = args.config.read_bytes()
        config = json.loads(config_bytes, object_pairs_hook=unique_json)
        require(config.get("schemaVersion") == 1 and config.get("purpose") == "510-control-only-work-calibration" and config.get("workLevels") == list(WORK_LEVELS), "calibration config drift")
        require(config.get("playerLaunchesPerCleanBuild") == 5, "calibration launch count drift")
        require(config.get("dispatchOrder") == list(DISPATCH_ORDER), "calibration dispatch order config drift")
        require(config.get("pythonVersion") == platform.python_version(), "Python runtime drift")
        require(config.get("targetRateRatios") == TRUTH_RATIOS, "calibration target drift")
        require(config.get("sourceSha256") == hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "calibration reducer source drift")
        require(config.get("pilotReducerSha256") == hashlib.sha256(PILOT_REDUCER_PATH.read_bytes()).hexdigest(), "shared pilot validity source drift")
        require(config.get("extractorSha256") == hashlib.sha256(EXTRACTOR_PATH.read_bytes()).hexdigest(), "paired extractor source drift")
        require(re.fullmatch(r"[0-9a-f]{40}", args.expected_commit), "expected commit must be a 40-character Git ID")
        manifest_bytes = args.artifact_manifest.read_bytes()
        manifest = json.loads(manifest_bytes, object_pairs_hook=unique_json)
        artifacts, jobs = preflight(manifest, args.expected_commit, args.artifact_manifest.parent)
        report = calibrate(artifacts, args.expected_commit, load_extractor())
        report["configSha256"] = hashlib.sha256(config_bytes).hexdigest()
        report["evidenceManifestSha256"] = hashlib.sha256(manifest_bytes).hexdigest()
        report["workflowJobs"] = jobs
        report["calibrationJobSeconds"] = sum(job["jobSeconds"] for job in jobs.values())
        with args.output.open("x", encoding="utf-8") as output:
            output.write(json.dumps(report, sort_keys=True, separators=(",", ":")) + "\n")
    except (OSError, ValueError, TypeError, KeyError, ET.ParseError, zipfile.BadZipFile) as error:
        print(f"invalid pilot calibration: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
