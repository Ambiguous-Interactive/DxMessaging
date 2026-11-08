#!/usr/bin/env python3
"""Validate and reduce six independent #510 physical-control confirmation builds.

The manifest lists six artifact and job-evidence commitments in the fixed
P03 B/A, P05 A/B, P10 B/A order. All six are validated before raw paired
ratios are extracted. The output is descriptive, with no interval claim.
The budget ledger contains complete raw Actions workflow/job snapshots through
the final confirmation job.
"""

import argparse
import hashlib
import importlib.util
import json
import math
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path


DISPATCH = (("P03", "B"), ("P03", "A"), ("P05", "A"), ("P05", "B"), ("P10", "B"), ("P10", "A"))
B_ORDERS = ("ABBABAAB", "BAABABBA", "ABBABAAB", "BAABABBA", "ABBABAAB")
A_ORDERS = ("BAABABBA", "ABBABAAB", "BAABABBA", "ABBABAAB", "BAABABBA")
TARGETS = ("GlobalToOne", "StructNoBox", "Filtered", "PostProcess", "FilteredPostProcess")
SENTINELS = ("GlobalToMany", "KeyedToOne")
REDUCER_PATH = Path(__file__).with_name("reduce-pilot-effects.py")
EXTRACTOR_PATH = Path(__file__).with_name("extract-pilot-paired.py")
CALIBRATION_CONFIG_PATH = Path(__file__).parents[2] / ".github/perf/pilot-control-calibration.v1.json"


def module_from(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def confirmation(manifest, calibration_bytes, expected_commit, base_path, budget, budget_sha):
    reducer = module_from(REDUCER_PATH, "pilot_effects")
    extractor = module_from(EXTRACTOR_PATH, "pilot_extractor")
    require = reducer.require
    require(isinstance(budget, dict) and type(budget.get("totalSeconds")) is int and isinstance(budget.get("eliJobs"), list), "validated ELI budget required")
    require(isinstance(budget_sha, str) and len(budget_sha) == 64, "ELI budget SHA-256 required")
    require(isinstance(manifest, dict) and manifest.get("schemaVersion") == 1 and manifest.get("purpose") == "510-independent-physical-control-confirmation", "confirmation manifest drift")
    entries = manifest.get("builds")
    require(isinstance(entries, list) and len(entries) == 6, "confirmation requires six fresh builds")
    calibration = json.loads(calibration_bytes, object_pairs_hook=reducer.unique_json)
    require(isinstance(calibration, dict) and calibration.get("schemaVersion") == 1 and calibration.get("purpose") == "control-only-work-calibration", "calibration report drift")
    levels = (0, 2048, 8192, 32768, 131072, 524288)
    require(calibration.get("levels") == list(levels) and isinstance(calibration.get("builds"), dict) and calibration["builds"].keys() == {str(level) for level in levels}, "complete six-build calibration report required")
    require(calibration.get("sourceCommit") == expected_commit and calibration.get("configSha256") == reducer.sha256_file(CALIBRATION_CONFIG_PATH), "calibration provenance drift")
    calibration_builds = calibration["builds"]
    require(all(isinstance(build, dict) for build in calibration_builds.values()), "malformed calibration build record")
    require(len({build.get("buildInvocationId") for build in calibration_builds.values()}) == 6 and len({build.get("sourceTree") for build in calibration_builds.values()}) == 1, "calibration build identity drift")
    for build in calibration_builds.values():
        require(isinstance(build.get("archiveSha256"), str) and len(build["archiveSha256"]) == 64 and isinstance(build.get("buildStartedUtc"), str), "calibration artifact provenance drift")
    calibration_jobs = calibration.get("workflowJobs")
    require(isinstance(calibration_jobs, dict) and calibration_jobs.keys() == calibration_builds.keys(), "complete six-job calibration cleanup evidence required")
    require(isinstance(calibration.get("evidenceManifestSha256"), str) and len(calibration["evidenceManifestSha256"]) == 64, "calibration evidence manifest commitment missing")
    require(all(isinstance(job, dict) and isinstance(job.get("jobEvidenceSha256"), str) and len(job["jobEvidenceSha256"]) == 64 and type(job.get("jobSeconds")) in (int, float) and math.isfinite(job["jobSeconds"]) and job["jobSeconds"] > 0 for job in calibration_jobs.values()), "calibration job provenance drift")
    require(len({job.get("workflowRunId") for job in calibration_jobs.values()}) == 6 and len({job.get("workflowJobId") for job in calibration_jobs.values()}) == 6, "reused calibration job evidence")
    require(calibration.get("calibrationJobSeconds") == sum(job["jobSeconds"] for job in calibration_jobs.values()), "calibration job time drift")
    calibration_end = max(reducer.utc_time(job.get("jobCompletedUtc"), "calibration.jobCompletedUtc") for job in calibration_jobs.values())
    proposals = {}
    for condition in ("P03", "P05", "P10"):
        proposals[condition] = {}
        for scenario in TARGETS:
            value = calibration.get("targets", {}).get(scenario, {}).get("proposedIterations", {}).get(condition)
            require(type(value) is int and 0 < value <= 1_000_000, f"{condition}/{scenario}: missing calibrated work")
            proposals[condition][scenario] = value
    for scenario in TARGETS:
        require(proposals["P03"][scenario] <= proposals["P05"][scenario] <= proposals["P10"][scenario], f"{scenario}: nonmonotone calibrated work")
    profile_sha = reducer.sha256_file(reducer.PROFILE_PATH)
    collector_sha = reducer.sha256_file(reducer.COLLECTOR_PATH)
    zero = {scenario: 0 for scenario in TARGETS}
    inspected = []
    seen_builds = set()
    seen_runs = set()
    seen_jobs = set()
    trees = set()
    previous_job_end = None
    total_job_seconds = 0.0
    for entry, (condition, arm) in zip(entries, DISPATCH):
        require(isinstance(entry, dict) and entry.get("condition") == condition and entry.get("arm") == arm, "confirmation dispatch order drift")
        path = base_path / reducer.checked_member(entry.get("artifactPath"))
        job_path = base_path / reducer.checked_member(entry.get("jobEvidencePath"))
        artifact_sha = entry.get("artifactSha256")
        job_sha = entry.get("jobEvidenceSha256")
        require(isinstance(artifact_sha, str) and len(artifact_sha) == 64 and isinstance(job_sha, str) and len(job_sha) == 64, "confirmation evidence SHA-256 missing")
        orders = A_ORDERS if arm == "A" else B_ORDERS
        build = reducer.inspect_pilot_zip(path, artifact_sha, expected_commit, orders, profile_sha, collector_sha)
        run_id = entry.get("workflowRunId")
        require(type(run_id) is int and run_id > 0 and run_id not in seen_runs, "reused confirmation workflow run")
        job = reducer.inspect_workflow_job(job_path, job_sha, run_id, expected_commit, build)
        require(build["buildId"] not in seen_builds and job["workflowJobId"] not in seen_jobs, "reused confirmation build or job")
        seen_builds.add(build["buildId"])
        seen_runs.add(run_id)
        seen_jobs.add(job["workflowJobId"])
        trees.add(build["sourceTree"])
        started = reducer.utc_time(build["buildStartedUtc"], "buildStartedUtc")
        require(calibration_end < started, "confirmation build predates calibration completion")
        require(previous_job_end is None or previous_job_end < started, "confirmation build order drift")
        previous_job_end = reducer.utc_time(job["jobCompletedUtc"], "job.completed_at")
        total_job_seconds += job["jobSeconds"]
        inspected.append({"condition": condition, "arm": arm, "artifactPath": path, "artifactSha256": artifact_sha, "jobEvidenceSha256": job_sha, **build, **job})
    require(len(trees) == 1, "confirmation source tree drift")
    require(next(iter(trees)) == next(iter(calibration_builds.values()))["sourceTree"], "confirmation/calibration source tree drift")
    budget_jobs = {job["jobId"]: job for job in budget["eliJobs"]}
    require(all(build["workflowJobId"] in budget_jobs and budget_jobs[build["workflowJobId"]]["runId"] == build["workflowRunId"] and budget_jobs[build["workflowJobId"]]["seconds"] == build["jobSeconds"] for build in inspected), "confirmation jobs missing from ELI budget")
    spent_before_seconds = budget["totalSeconds"] - total_job_seconds
    require(spent_before_seconds >= 0 and budget["totalSeconds"] <= 54_000, "approved serialized ELI time cap exceeded")

    platforms = set()
    values = {}
    for entry in inspected:
        condition, arm = entry["condition"], entry["arm"]
        work = proposals[condition] if arm == "A" else zero
        raw = {scenario: [] for scenario in (*TARGETS, *SENTINELS)}
        with zipfile.ZipFile(entry["artifactPath"]) as archive:
            same = reducer.read_json(archive, "same-player-repeats/same-player-evidence.json")
            for run in same["runs"]:
                require(run.get("pilotCpuWorkIterationsPerBatch") is None and run.get("pilotCpuWorkByScenario") == work, "confirmation work-vector drift")
                row = extractor.extract_xml(archive.read(run["resultsPath"]), run["batchOrder"], work)
                require(row["commit"] == expected_commit, "confirmation raw row source drift")
                platforms.add(row["platform"])
                for scenario in raw:
                    raw[scenario].append(math.log(row["rows"][scenario]["ratio"]))
        require(all(len(samples) == 5 for samples in raw.values()), "confirmation launch count drift")
        sentinel_mean = sum(sum(raw[scenario]) / 5 for scenario in SENTINELS) / 2
        values[(condition, arm)] = {
            "normalized": {scenario: sum(raw[scenario]) / 5 - sentinel_mean for scenario in TARGETS},
            "sentinels": {scenario: sum(raw[scenario]) / 5 for scenario in SENTINELS},
        }
    require(len(platforms) == 1, "confirmation platform drift")
    results = {}
    for condition in ("P03", "P05", "P10"):
        b, a = values[(condition, "B")], values[(condition, "A")]
        effects = {scenario: b["normalized"][scenario] - a["normalized"][scenario] for scenario in TARGETS}
        sentinel_differences = {scenario: b["sentinels"][scenario] - a["sentinels"][scenario] for scenario in SENTINELS}
        targets_pass = all(effect > {"P03": math.log(0.97), "P05": 0.0, "P10": math.log(1.03)}[condition] for effect in effects.values())
        sentinels_pass = all(math.log(0.97) < effect < math.log(1.03) for effect in sentinel_differences.values())
        results[condition] = {"proposedWork": proposals[condition], "targetLogEffects": effects, "sentinelLogDifferences": sentinel_differences, "targetsPass": targets_pass, "sentinelsPass": sentinels_pass}
    return {"schemaVersion": 1, "purpose": "510-independent-physical-control-confirmation", "sourceCommit": expected_commit, "sourceTree": next(iter(trees)), "platform": next(iter(platforms)), "calibrationReportSha256": hashlib.sha256(calibration_bytes).hexdigest(), "analyzerSourceSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "pilotReducerSourceSha256": hashlib.sha256(REDUCER_PATH.read_bytes()).hexdigest(), "extractorSourceSha256": hashlib.sha256(EXTRACTOR_PATH.read_bytes()).hexdigest(), "eliBudgetLedgerSha256": budget_sha, "spentBeforeSeconds": spent_before_seconds, "confirmationJobSeconds": total_job_seconds, "totalSerializedEliSeconds": budget["totalSeconds"], "builds": [{key: value for key, value in build.items() if key != "artifactPath"} for build in inspected], "conditions": results, "passesPhysicalSanity": all(row["targetsPass"] and row["sentinelsPass"] for row in results.values())}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifact-manifest", type=Path, required=True)
    parser.add_argument("--calibration-report", type=Path, required=True)
    parser.add_argument("--expected-commit", required=True)
    parser.add_argument("--budget-ledger", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        reducer = module_from(REDUCER_PATH, "pilot_effects")
        manifest = json.loads(args.artifact_manifest.read_bytes(), object_pairs_hook=reducer.unique_json)
        budget_bytes = args.budget_ledger.read_bytes()
        budget = reducer.validate_eli_budget(json.loads(budget_bytes, object_pairs_hook=reducer.unique_json), args.budget_ledger.parent)
        report = confirmation(manifest, args.calibration_report.read_bytes(), args.expected_commit, args.artifact_manifest.parent, budget, hashlib.sha256(budget_bytes).hexdigest())
        with args.output.open("x", encoding="utf-8") as output:
            output.write(json.dumps(report, sort_keys=True, separators=(",", ":")) + "\n")
    except (OSError, ValueError, TypeError, KeyError, ET.ParseError, zipfile.BadZipFile) as error:
        print(f"invalid physical confirmation: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
