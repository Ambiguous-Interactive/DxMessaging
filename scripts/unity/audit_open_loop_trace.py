#!/usr/bin/env python3
"""Audit fixed offered arrivals against complete open-loop observations (#512)."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import re
import sys
from fractions import Fraction
from pathlib import Path
from typing import Any


DECIMAL = re.compile(r"(?:0|[1-9][0-9]*)\Z")
SHA256 = re.compile(r"[0-9a-f]{64}\Z")
GUID = re.compile(r"[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\Z")
SCHEDULE_FIELDS = {"schemaVersion", "traceId", "frequencyHz", "horizonStartTick", "horizonEndTick", "arrivals"}
OFFER_FIELDS = {"id", "arrivalTick"}
OBSERVATION_FIELDS = {"schemaVersion", "scheduleSha256", "events"}
EVENT_FIELDS = {"id", "startTick", "completionTick"}
PLAN_FIELDS = {"schemaVersion", "traces"}
PLAN_TRACE_FIELDS = {"traceId", "fixture", "frequencyHz", "horizonSpanTicks", "arrivalCount", "arrivalStepTicks"}
TAIL_IDS = {"editor-tail-baseline", "editor-tail-stalled"}
TAIL_EFFECT = re.compile(
    r"DXM_OPEN_LOOP_TAIL_EFFECT_V1 p99ShiftTicks=((?:0|[1-9][0-9]*)) "
    r"meanShiftNumeratorTicks=((?:0|[1-9][0-9]*)) "
    r"meanShiftDenominator=((?:0|[1-9][0-9]*))\Z"
)
RUN_FIELDS = {"runGuid", "resultPath"}
CLEANUP_FIELDS = {
    "observedUtc", "observationError", "frameworkActive", "playing", "compiling",
    "updating", "mainStage", "activeScene", "scenes", "runGuid", "resultPath",
    "ownedResultPath", "legacyObserverResultPath", "frameworkErrors",
}
SCENE_FIELDS = {"path", "dirty", "loaded"}
CAPTURE_ENVIRONMENT_FIELDS = {
    "claimClass", "evidenceClass", "executionScope", "planSha256", "runGuid",
    "runtimeTree", "schemaVersion", "sourceCommit", "sourceTree", "unityVersion",
}


def decimal(value: Any, field: str) -> int:
    if not isinstance(value, str) or DECIMAL.fullmatch(value) is None:
        raise ValueError(f"{field} must be a canonical nonnegative decimal string")
    return int(value)


def identity(value: Any, field: str) -> str:
    if not isinstance(value, str) or not value or value != value.strip():
        raise ValueError(f"{field} must be a nonblank trimmed string")
    return value


def fields(value: Any, expected: set[str], field: str) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != expected:
        raise ValueError(f"{field} must contain exactly {sorted(expected)}")
    return value


def unique_fields(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise ValueError(f"duplicate JSON key: {key}")
        value[key] = item
    return value


def parse_json(raw: bytes | str) -> Any:
    return json.loads(
        raw,
        object_pairs_hook=unique_fields,
        parse_constant=lambda value: (_ for _ in ()).throw(ValueError(f"invalid JSON constant: {value}")),
    )


def rate(count: int, frequency: int, span: int) -> dict[str, str]:
    value = Fraction(count * frequency, span)
    return {"numerator": str(value.numerator), "denominator": str(value.denominator)}


def rational(value: Fraction) -> dict[str, str]:
    return {"numerator": str(value.numerator), "denominator": str(value.denominator)}


def parse_plan(raw_plan: bytes) -> dict[str, dict[str, Any]]:
    if not isinstance(raw_plan, bytes):
        raise ValueError("plan must be raw bytes")
    plan = fields(parse_json(raw_plan), PLAN_FIELDS, "plan")
    if type(plan["schemaVersion"]) is not int or plan["schemaVersion"] != 1:
        raise ValueError("unsupported plan schemaVersion")
    traces = plan["traces"]
    if not isinstance(traces, list) or not traces:
        raise ValueError("plan traces must be a nonempty array")
    declared = {}
    for index, raw in enumerate(traces):
        trace = fields(raw, PLAN_TRACE_FIELDS, f"plan.traces[{index}]")
        trace_id = identity(trace["traceId"], f"plan.traces[{index}].traceId")
        identity(trace["fixture"], f"plan.traces[{index}].fixture")
        frequency = decimal(trace["frequencyHz"], f"plan.traces[{index}].frequencyHz")
        span = decimal(trace["horizonSpanTicks"], f"plan.traces[{index}].horizonSpanTicks")
        count = trace["arrivalCount"]
        step = decimal(trace["arrivalStepTicks"], f"plan.traces[{index}].arrivalStepTicks")
        if (
            trace_id in declared
            or frequency == 0
            or span == 0
            or type(count) is not int
            or count <= 0
            or (count - 1) * step >= span
        ):
            raise ValueError("duplicate or invalid planned trace")
        declared[trace_id] = trace
    return declared


def check_plan_trace(planned: dict[str, Any], fixture: str, reduced: dict[str, Any]) -> None:
    if planned["fixture"] != fixture or planned["frequencyHz"] != reduced["frequencyHz"]:
        raise ValueError(f"planned fixture or frequency mismatch: {reduced['traceId']}")
    span = int(reduced["horizonEndTick"]) - int(reduced["horizonStartTick"])
    if span != int(planned["horizonSpanTicks"]) or len(reduced["events"]) != planned["arrivalCount"]:
        raise ValueError(f"planned horizon or arrival count mismatch: {reduced['traceId']}")
    origin = int(reduced["horizonStartTick"])
    step = int(planned["arrivalStepTicks"])
    for index, event in enumerate(reduced["events"]):
        if event["id"] != str(index) or int(event["arrivalTick"]) - origin != index * step:
            raise ValueError(f"planned arrival mismatch: {reduced['traceId']} at {index}")


def check_tail_effect(fixture: str, traces: dict[str, dict[str, Any]], marker: tuple[str, str, str] | None) -> dict[str, str] | None:
    present = TAIL_IDS & traces.keys()
    if not present and marker is None:
        return None
    if present != TAIL_IDS or marker is None:
        raise ValueError(f"{fixture} has missing tail trace or effect marker")
    baseline = traces["editor-tail-baseline"]
    stalled = traces["editor-tail-stalled"]
    if baseline["frequencyHz"] != stalled["frequencyHz"] or len(baseline["events"]) != len(stalled["events"]):
        raise ValueError(f"{fixture} has incomparable tail schedules")
    base_origin = int(baseline["horizonStartTick"])
    stalled_origin = int(stalled["horizonStartTick"])
    if int(baseline["horizonEndTick"]) - base_origin != int(stalled["horizonEndTick"]) - stalled_origin:
        raise ValueError(f"{fixture} has incomparable tail horizons")
    for before, after in zip(baseline["events"], stalled["events"]):
        if before["id"] != after["id"] or int(before["arrivalTick"]) - base_origin != int(after["arrivalTick"]) - stalled_origin:
            raise ValueError(f"{fixture} has incomparable tail arrivals")
    count = len(baseline["events"])
    p99_shift = int(stalled["completionLatencyQuantiles"]["p99"]["latencyTicks"]) - int(
        baseline["completionLatencyQuantiles"]["p99"]["latencyTicks"]
    )
    mean_numerator = sum(int(row["completionLatencyTicks"]) for row in stalled["events"]) - sum(
        int(row["completionLatencyTicks"]) for row in baseline["events"]
    )
    expected = (str(p99_shift), str(mean_numerator), str(count))
    if marker != expected:
        raise ValueError(f"{fixture} tail effect marker disagrees with raw timestamps")
    return {
        "fixture": fixture,
        "p99ShiftTicks": expected[0],
        "meanShiftNumeratorTicks": expected[1],
        "meanShiftDenominator": expected[2],
    }


def audit(schedule_bytes: bytes, observations: Any) -> dict[str, Any]:
    """Return descriptive exact rates, latency ranks, and raw timestamps for a hash-bound schedule."""
    if not isinstance(schedule_bytes, bytes):
        raise ValueError("schedule must be raw bytes")
    schedule_sha = hashlib.sha256(schedule_bytes).hexdigest()
    schedule = fields(parse_json(schedule_bytes), SCHEDULE_FIELDS, "schedule")
    if type(schedule["schemaVersion"]) is not int or schedule["schemaVersion"] != 1:
        raise ValueError("unsupported schedule schemaVersion")
    trace_id = identity(schedule["traceId"], "traceId")
    frequency = decimal(schedule["frequencyHz"], "frequencyHz")
    start = decimal(schedule["horizonStartTick"], "horizonStartTick")
    end = decimal(schedule["horizonEndTick"], "horizonEndTick")
    if frequency == 0 or end <= start:
        raise ValueError("frequency and horizon span must be positive")
    arrivals = schedule["arrivals"]
    if not isinstance(arrivals, list) or not arrivals:
        raise ValueError("arrivals must be a nonempty array")
    offered: dict[str, int] = {}
    previous = start
    for index, raw in enumerate(arrivals):
        offer = fields(raw, OFFER_FIELDS, f"arrivals[{index}]")
        event_id = identity(offer["id"], f"arrivals[{index}].id")
        tick = decimal(offer["arrivalTick"], f"arrivals[{index}].arrivalTick")
        if event_id in offered or tick < previous or tick >= end:
            raise ValueError("duplicate, nonmonotone, or out-of-horizon offer")
        offered[event_id] = tick
        previous = tick

    observations = fields(observations, OBSERVATION_FIELDS, "observations")
    if type(observations["schemaVersion"]) is not int or observations["schemaVersion"] != 1:
        raise ValueError("unsupported observation schemaVersion")
    digest = observations["scheduleSha256"]
    if not isinstance(digest, str) or SHA256.fullmatch(digest) is None or digest != schedule_sha:
        raise ValueError("observations do not match the raw schedule SHA-256")
    events = observations["events"]
    if not isinstance(events, list):
        raise ValueError("events must be an array")
    observed: dict[str, tuple[int, int]] = {}
    for index, raw in enumerate(events):
        event = fields(raw, EVENT_FIELDS, f"events[{index}]")
        event_id = identity(event["id"], f"events[{index}].id")
        begun = decimal(event["startTick"], f"events[{index}].startTick")
        completed = decimal(event["completionTick"], f"events[{index}].completionTick")
        if event_id not in offered or event_id in observed or begun < offered[event_id] or completed < begun:
            raise ValueError("unknown, duplicate, early, or reversed observed event")
        observed[event_id] = begun, completed
    if observed.keys() != offered.keys():
        raise ValueError("every offered event needs one observed completion")

    rows = []
    latencies = []
    started_within = 0
    completed_within = 0
    for event_id, arrival in offered.items():
        begun, completed = observed[event_id]
        started_within += begun < end
        completed_within += completed < end
        latency = completed - arrival
        latencies.append((latency, event_id))
        rows.append({
            "id": event_id,
            "arrivalTick": str(arrival),
            "startTick": str(begun),
            "completionTick": str(completed),
            "queueDelayTicks": str(begun - arrival),
            "serviceTicks": str(completed - begun),
            "completionLatencyTicks": str(latency),
        })
    count = len(offered)
    ordered = sorted(latencies)
    quantiles = {}
    for name, percentile in (("p50", 50), ("p95", 95), ("p99", 99)):
        ticks, event_id = ordered[percentile * count // 100]
        quantiles[name] = {
            "id": event_id,
            "latencyTicks": str(ticks),
            "latencyNanoseconds": rational(Fraction(ticks * 1_000_000_000, frequency)),
        }
    return {
        "schemaVersion": 1,
        "resultClass": "descriptive-only",
        "traceId": trace_id,
        "scheduleSha256": schedule_sha,
        "frequencyHz": str(frequency),
        "horizonStartTick": str(start),
        "horizonEndTick": str(end),
        "offeredCount": count,
        "startedWithinHorizon": started_within,
        "completedWithinHorizon": completed_within,
        "completedEventual": count,
        "backlogAtHorizon": count - completed_within,
        "offeredPerSecond": rate(count, frequency, end - start),
        "achievedWithinHorizonPerSecond": rate(completed_within, frequency, end - start),
        "completionQuantileConvention": "strict-upper empirical floor(p*n/100), within-trace descriptive",
        "completionLatencyQuantiles": quantiles,
        "meanCompletionLatencyTicks": rational(Fraction(sum(ticks for ticks, _ in latencies), count)),
        "events": rows,
    }


def audit_unity_result(raw_result: bytes, expected_trace_ids: list[str], plan_bytes: bytes | None = None) -> dict[str, Any]:
    """Replay every trace from one raw maintained-runner result against declared IDs."""
    plan = parse_plan(plan_bytes) if plan_bytes is not None else None
    if not isinstance(raw_result, bytes) or (not expected_trace_ids and plan is None):
        raise ValueError("raw result bytes and expected trace IDs are required")
    if plan is not None and not expected_trace_ids:
        expected_trace_ids = list(plan)
    expected = {identity(value, "expected trace ID") for value in expected_trace_ids}
    if len(expected) != len(expected_trace_ids):
        raise ValueError("expected trace IDs must be distinct")
    if plan is not None and expected != plan.keys():
        raise ValueError("expected trace IDs do not match the plan")
    result = parse_json(raw_result)
    required = {"passCount", "failCount", "skipCount", "inconclusiveCount", "nodes", "failures"}
    if not isinstance(result, dict) or not required.issubset(result):
        raise ValueError("Unity result is missing required fields")
    for name in ("passCount", "failCount", "skipCount", "inconclusiveCount"):
        if type(result[name]) is not int or result[name] < 0:
            raise ValueError(f"Unity result {name} must be a nonnegative integer")
    if result["passCount"] == 0 or any(result[name] != 0 for name in ("failCount", "skipCount", "inconclusiveCount")):
        raise ValueError("Unity result must have positive passes and no failed, skipped, or inconclusive tests")
    if result["failures"] != [] or not isinstance(result["nodes"], list):
        raise ValueError("Unity result has failures or invalid nodes")

    passed_leaves = 0
    traces = []
    effects = []
    seen: set[str] = set()
    for index, node in enumerate(result["nodes"]):
        if not isinstance(node, dict) or not {"name", "isSuite", "status", "output"}.issubset(node):
            raise ValueError(f"nodes[{index}] has invalid shape")
        if type(node["isSuite"]) is not bool:
            raise ValueError(f"nodes[{index}].isSuite must be boolean")
        if node["isSuite"]:
            if node["status"] != "Passed":
                raise ValueError(f"nodes[{index}] is not a passed suite")
            continue
        name = identity(node["name"], f"nodes[{index}].name")
        if node["status"] != "Passed" or not isinstance(node["output"], str):
            raise ValueError(f"nodes[{index}] is not a passed trace fixture")
        passed_leaves += 1
        pending = None
        pairs = 0
        leaf_traces: dict[str, dict[str, Any]] = {}
        effect_marker = None
        for line in node["output"].splitlines():
            if line.startswith("DXM_OPEN_LOOP_SCHEDULE_V1 "):
                if pending is not None:
                    raise ValueError(f"{name} has an unpaired schedule")
                pending = line.removeprefix("DXM_OPEN_LOOP_SCHEDULE_V1 ").encode("utf-8")
            elif line.startswith("DXM_OPEN_LOOP_OBSERVATIONS_V1 "):
                if pending is None:
                    raise ValueError(f"{name} has an unpaired observation")
                observed = parse_json(line.removeprefix("DXM_OPEN_LOOP_OBSERVATIONS_V1 "))
                reduced = audit(pending, observed)
                trace_id = reduced["traceId"]
                if trace_id not in expected or trace_id in seen:
                    raise ValueError(f"unexpected or duplicate trace ID: {trace_id}")
                if plan is not None:
                    check_plan_trace(plan[trace_id], name, reduced)
                seen.add(trace_id)
                leaf_traces[trace_id] = reduced
                traces.append({
                    "fixture": name,
                    "traceId": trace_id,
                    "scheduleSha256": reduced["scheduleSha256"],
                    "frequencyHz": reduced["frequencyHz"],
                    "offeredCount": reduced["offeredCount"],
                    "startedWithinHorizon": reduced["startedWithinHorizon"],
                    "completedWithinHorizon": reduced["completedWithinHorizon"],
                    "completedEventual": reduced["completedEventual"],
                    "backlogAtHorizon": reduced["backlogAtHorizon"],
                    "offeredPerSecond": reduced["offeredPerSecond"],
                    "achievedWithinHorizonPerSecond": reduced["achievedWithinHorizonPerSecond"],
                    "completionQuantileConvention": reduced["completionQuantileConvention"],
                    "completionLatencyQuantiles": reduced["completionLatencyQuantiles"],
                    "meanCompletionLatencyTicks": reduced["meanCompletionLatencyTicks"],
                })
                pending = None
                pairs += 1
            elif line.startswith("DXM_OPEN_LOOP_TAIL_EFFECT_V1"):
                matched = TAIL_EFFECT.fullmatch(line)
                if matched is None or effect_marker is not None or pending is not None:
                    raise ValueError(f"{name} has malformed or duplicate tail effect marker")
                effect_marker = matched.groups()
        if pending is not None or pairs == 0:
            raise ValueError(f"{name} has missing or unpaired trace markers")
        effect = check_tail_effect(name, leaf_traces, effect_marker)
        if effect is not None:
            effects.append(effect)
    if passed_leaves != result["passCount"] or seen != expected:
        raise ValueError("Unity pass count or expected trace IDs do not match replayed fixtures")
    replay = {
        "schemaVersion": 1,
        "resultClass": "descriptive-only",
        "rawResultSha256": hashlib.sha256(raw_result).hexdigest(),
        "expectedTraceIds": sorted(expected),
        "traceCount": len(traces),
        "traces": traces,
        "effects": effects,
    }
    if plan_bytes is not None:
        replay["planSha256"] = hashlib.sha256(plan_bytes).hexdigest()
    return replay


def audit_unity_capture(
    raw_result: bytes,
    expected_trace_ids: list[str],
    plan_bytes: bytes | None,
    run_bytes: bytes,
    cleanup_bytes: bytes,
    result_status: bytes,
    cleanup_status: bytes,
    result_name: str,
) -> dict[str, Any]:
    """Bind replay to the maintained runner's terminal ownership and clean-scene record."""
    if result_status != b"done" or cleanup_status != b"done":
        raise ValueError("result and cleanup status must both be done")
    run = fields(parse_json(run_bytes), RUN_FIELDS, "run record")
    cleanup = fields(parse_json(cleanup_bytes), CLEANUP_FIELDS, "cleanup record")
    guid = run["runGuid"]
    path = identity(run["resultPath"], "run resultPath")
    if not isinstance(guid, str) or GUID.fullmatch(guid) is None:
        raise ValueError("run GUID must be canonical lowercase UUID")
    if not isinstance(result_name, str) or not result_name.endswith(".json") or path.replace("\\", "/").rsplit("/", 1)[-1] != result_name:
        raise ValueError("run resultPath does not match the supplied result file name")
    if cleanup["runGuid"] != guid or cleanup["resultPath"] != path or cleanup["ownedResultPath"] != path:
        raise ValueError("cleanup does not own the same run GUID and result path")
    for field in ("frameworkActive", "playing", "compiling", "updating"):
        if cleanup[field] is not False:
            raise ValueError(f"cleanup {field} must be false")
    if cleanup["mainStage"] is not True or cleanup["observationError"] != "" or cleanup["frameworkErrors"] != "":
        raise ValueError("cleanup has stage or framework errors")
    if cleanup["legacyObserverResultPath"] != "":
        raise ValueError("cleanup retains a legacy observer result path")
    identity(cleanup["observedUtc"], "cleanup observedUtc")
    active = identity(cleanup["activeScene"], "cleanup activeScene")
    scenes = cleanup["scenes"]
    if not isinstance(scenes, list) or not scenes:
        raise ValueError("cleanup scenes must be a nonempty array")
    paths = set()
    for index, raw in enumerate(scenes):
        scene = fields(raw, SCENE_FIELDS, f"cleanup.scenes[{index}]")
        scene_path = identity(scene["path"], f"cleanup.scenes[{index}].path")
        if scene_path in paths or scene["dirty"] is not False or scene["loaded"] is not True:
            raise ValueError("cleanup has duplicate, dirty, or unloaded scenes")
        paths.add(scene_path)
    if active not in paths:
        raise ValueError("cleanup active scene is not among loaded scenes")
    replay = audit_unity_result(raw_result, expected_trace_ids, plan_bytes)
    replay["runGuid"] = guid
    replay["resultName"] = result_name
    replay["runRecordSha256"] = hashlib.sha256(run_bytes).hexdigest()
    replay["cleanupRecordSha256"] = hashlib.sha256(cleanup_bytes).hexdigest()
    replay["resultStatusSha256"] = hashlib.sha256(result_status).hexdigest()
    replay["cleanupStatusSha256"] = hashlib.sha256(cleanup_status).hexdigest()
    return replay


def reduce_capture_contents(contents: dict[str, bytes], source_commit: str) -> dict[str, Any]:
    """Reduce one exact-tree Editor capture using only supplied bundle bytes."""
    if not isinstance(contents, dict) or any(
        type(name) is not str or type(raw) is not bytes for name, raw in contents.items()
    ):
        raise ValueError("capture contents must map names to raw bytes")
    common = {"capture-environment.json", "capture-replay.json", "open-loop-editor-plan.json"}
    candidates = [
        name for name in contents
        if name not in common and name.endswith(".json")
        and not name.endswith((".run.json", ".cleanup.json"))
    ]
    if len(candidates) != 1:
        raise ValueError("capture must contain exactly one raw Unity result")
    result_name = candidates[0]
    required = common | {
        result_name, result_name + ".run.json", result_name + ".cleanup.json",
        result_name + ".status", result_name + ".cleanup.status",
    }
    if set(contents) != required or "/" in result_name or "\\" in result_name:
        raise ValueError("capture has missing, unexpected, or nonportable files")
    environment = fields(
        parse_json(contents["capture-environment.json"]),
        CAPTURE_ENVIRONMENT_FIELDS,
        "capture environment",
    )
    if (
        type(environment["schemaVersion"]) is not int or environment["schemaVersion"] != 1
        or environment["sourceCommit"] != source_commit
        or not isinstance(source_commit, str) or re.fullmatch(r"[0-9a-f]{40}", source_commit) is None
        or any(
            not isinstance(environment[field], str)
            or re.fullmatch(r"[0-9a-f]{40}", environment[field]) is None
            for field in ("sourceTree", "runtimeTree")
        )
        or environment["claimClass"] != "descriptive-only"
        or environment["evidenceClass"] != "editor-open-loop-protocol-screen"
        or environment["executionScope"] != "Editor PlayMode Mono"
        or re.fullmatch(
            r"[0-9]+\.[0-9]+\.[0-9]+f[0-9]+",
            identity(environment["unityVersion"], "unityVersion"),
        ) is None
        or environment["planSha256"] != hashlib.sha256(contents["open-loop-editor-plan.json"]).hexdigest()
    ):
        raise ValueError("capture environment disagrees with source, scope, or plan")
    replay = audit_unity_capture(
        contents[result_name], [], contents["open-loop-editor-plan.json"],
        contents[result_name + ".run.json"], contents[result_name + ".cleanup.json"],
        contents[result_name + ".status"], contents[result_name + ".cleanup.status"], result_name,
    )
    if environment["runGuid"] != replay["runGuid"]:
        raise ValueError("capture environment run GUID disagrees with terminal records")
    retained = parse_json(contents["capture-replay.json"])
    if json.dumps(retained, sort_keys=True, separators=(",", ":")) != json.dumps(
        replay, sort_keys=True, separators=(",", ":")
    ):
        raise ValueError("retained capture replay disagrees with raw inputs")
    return {"environment": environment, "replay": replay}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle-stdin", action="store_true", help="read source identity and base64 evidence from stdin")
    parser.add_argument("schedule", nargs="?", type=Path, help="predeclared raw schedule JSON")
    parser.add_argument("observations", nargs="?", type=Path, help="observation JSON bound to schedule SHA-256")
    parser.add_argument("--unity-result", type=Path, help="raw maintained-runner JSON with embedded traces")
    parser.add_argument("--plan", type=Path, help="committed normalized arrival plan for the Unity result")
    parser.add_argument("--run-record", type=Path, help="maintained-runner run identity JSON")
    parser.add_argument("--cleanup-record", type=Path, help="terminal cleanup JSON")
    parser.add_argument("--result-status", type=Path, help="result status sidecar")
    parser.add_argument("--cleanup-status", type=Path, help="cleanup status sidecar")
    parser.add_argument("--expected-trace-id", action="append", default=[], help="one required trace ID; repeat per trace")
    args = parser.parse_args()
    if args.bundle_stdin:
        if any((args.schedule, args.observations, args.unity_result, args.plan, args.run_record,
                args.cleanup_record, args.result_status, args.cleanup_status, args.expected_trace_id)):
            parser.error("--bundle-stdin does not accept file arguments")
        request = fields(parse_json(sys.stdin.buffer.read()), {"sourceCommit", "contents"}, "bundle request")
        encoded = request["contents"]
        if not isinstance(encoded, dict):
            raise ValueError("bundle contents must be an object")
        contents = {
            name: base64.b64decode(value, validate=True)
            for name, value in encoded.items()
        }
        print(json.dumps(reduce_capture_contents(contents, request["sourceCommit"]), sort_keys=True))
        return
    capture_files = (args.run_record, args.cleanup_record, args.result_status, args.cleanup_status)
    if any(item is not None for item in capture_files) and not all(item is not None for item in capture_files):
        parser.error("capture sidecars must be supplied together")
    if args.unity_result is not None:
        if args.schedule is not None or args.observations is not None or (not args.expected_trace_id and args.plan is None):
            parser.error("--unity-result requires expected trace IDs or --plan and no positional files")
        raw_result = args.unity_result.read_bytes()
        plan_bytes = args.plan.read_bytes() if args.plan is not None else None
        if args.run_record is None:
            result = audit_unity_result(raw_result, args.expected_trace_id, plan_bytes)
        else:
            result = audit_unity_capture(
                raw_result, args.expected_trace_id, plan_bytes,
                args.run_record.read_bytes(), args.cleanup_record.read_bytes(),
                args.result_status.read_bytes(), args.cleanup_status.read_bytes(), args.unity_result.name,
            )
    else:
        if args.schedule is None or args.observations is None or args.expected_trace_id or args.plan is not None or args.run_record is not None:
            parser.error("schedule and observations are required without --unity-result")
        result = audit(args.schedule.read_bytes(), parse_json(args.observations.read_bytes()))
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, base64.binascii.Error) as error:
        print(f"open-loop evidence error: {error}", file=sys.stderr)
        raise SystemExit(1) from None
