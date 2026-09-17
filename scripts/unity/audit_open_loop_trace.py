#!/usr/bin/env python3
"""Audit fixed offered arrivals against complete open-loop observations (#512)."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from fractions import Fraction
from pathlib import Path
from typing import Any


DECIMAL = re.compile(r"(?:0|[1-9][0-9]*)\Z")
SHA256 = re.compile(r"[0-9a-f]{64}\Z")
SCHEDULE_FIELDS = {"schemaVersion", "traceId", "frequencyHz", "horizonStartTick", "horizonEndTick", "arrivals"}
OFFER_FIELDS = {"id", "arrivalTick"}
OBSERVATION_FIELDS = {"schemaVersion", "scheduleSha256", "events"}
EVENT_FIELDS = {"id", "startTick", "completionTick"}
PLAN_FIELDS = {"schemaVersion", "traces"}
PLAN_TRACE_FIELDS = {"traceId", "fixture", "frequencyHz", "horizonSpanTicks", "arrivalCount", "arrivalStepTicks"}


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
        if pending is not None or pairs == 0:
            raise ValueError(f"{name} has missing or unpaired trace markers")
    if passed_leaves != result["passCount"] or seen != expected:
        raise ValueError("Unity pass count or expected trace IDs do not match replayed fixtures")
    replay = {
        "schemaVersion": 1,
        "resultClass": "descriptive-only",
        "rawResultSha256": hashlib.sha256(raw_result).hexdigest(),
        "expectedTraceIds": sorted(expected),
        "traceCount": len(traces),
        "traces": traces,
    }
    if plan_bytes is not None:
        replay["planSha256"] = hashlib.sha256(plan_bytes).hexdigest()
    return replay


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("schedule", nargs="?", type=Path, help="predeclared raw schedule JSON")
    parser.add_argument("observations", nargs="?", type=Path, help="observation JSON bound to schedule SHA-256")
    parser.add_argument("--unity-result", type=Path, help="raw maintained-runner JSON with embedded traces")
    parser.add_argument("--plan", type=Path, help="committed normalized arrival plan for the Unity result")
    parser.add_argument("--expected-trace-id", action="append", default=[], help="one required trace ID; repeat per trace")
    args = parser.parse_args()
    if args.unity_result is not None:
        if args.schedule is not None or args.observations is not None or (not args.expected_trace_id and args.plan is None):
            parser.error("--unity-result requires expected trace IDs or --plan and no positional files")
        result = audit_unity_result(
            args.unity_result.read_bytes(),
            args.expected_trace_id,
            args.plan.read_bytes() if args.plan is not None else None,
        )
    else:
        if args.schedule is None or args.observations is None or args.expected_trace_id or args.plan is not None:
            parser.error("schedule and observations are required without --unity-result")
        result = audit(args.schedule.read_bytes(), parse_json(args.observations.read_bytes()))
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
