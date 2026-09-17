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


def audit(schedule_bytes: bytes, observations: Any) -> dict[str, Any]:
    """Return descriptive exact rates and every raw timestamp for a hash-bound schedule."""
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
    started_within = 0
    completed_within = 0
    for event_id, arrival in offered.items():
        begun, completed = observed[event_id]
        started_within += begun < end
        completed_within += completed < end
        rows.append({
            "id": event_id,
            "arrivalTick": str(arrival),
            "startTick": str(begun),
            "completionTick": str(completed),
            "queueDelayTicks": str(begun - arrival),
            "serviceTicks": str(completed - begun),
            "completionLatencyTicks": str(completed - arrival),
        })
    count = len(offered)
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
        "events": rows,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("schedule", type=Path, help="predeclared raw schedule JSON")
    parser.add_argument("observations", type=Path, help="observation JSON bound to schedule SHA-256")
    args = parser.parse_args()
    result = audit(args.schedule.read_bytes(), parse_json(args.observations.read_bytes()))
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
