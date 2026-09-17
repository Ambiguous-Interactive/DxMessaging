#!/usr/bin/env python3
"""Validate and extract one #510 pilot launch from raw NUnit results.xml."""

import argparse
import json
import math
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


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
TARGETS = frozenset(SCENARIOS - {"GlobalToMany", "KeyedToOne"})
PROTOCOLS = {
    "ABBABAAB": "interleaved-abba-baab-v1",
    "BAABABBA": "interleaved-baab-abba-pilot-v1",
}
MARKER = re.compile(r"DXM_PAIRED_COMPARISON\s+(\{[^\r\n<]+\})")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def exact_integer(value, label):
    require(type(value) is int, f"{label} must be an integer")
    return value


def positive_number(value, label):
    require(type(value) in (int, float), f"{label} must be numeric")
    require(math.isfinite(value) and value > 0, f"{label} must be positive and finite")
    return value


def close(actual, expected, label, tolerance=1e-12):
    require(abs(actual - expected) <= tolerance * expected, f"{label} does not match raw cycles")


def extract(path, order, work):
    require(order in PROTOCOLS, "unknown scheduled batch order")
    require(type(work) is int and 0 <= work <= 1_000_000, "invalid scheduled CPU work")
    root = ET.parse(path).getroot()
    require(root.tag == "test-run", "expected NUnit test-run XML")
    require(root.get("failed") == "0", "NUnit launch has failed tests")
    rows = {}
    commit = platform = None
    for case in root.iter("test-case"):
        output = case.findtext("output") or ""
        matches = MARKER.findall(output)
        if not matches:
            continue
        require(case.get("result") == "Passed", "paired test case did not pass")
        payloads = {json.dumps(json.loads(match), sort_keys=True) for match in matches}
        require(len(payloads) == 1, "conflicting paired markers in one test case")
        row = json.loads(next(iter(payloads)))
        scenario = row.get("scenario")
        require(scenario in SCENARIOS, f"unexpected paired scenario: {scenario}")
        require(scenario not in rows, f"duplicate paired scenario: {scenario}")
        require(row.get("first") == "DxMessaging" and row.get("second") == "MessagePipe", f"{scenario}: wrong bridge order")
        require(row.get("batchOrder") == order and row.get("protocol") == PROTOCOLS[order], f"{scenario}: scheduled order drift")
        expected_work = work if scenario in TARGETS else 0
        require(row.get("pilotCpuWorkIterationsPerBatch") == expected_work, f"{scenario}: scheduled CPU work drift")
        require(row.get("cycles") == 4 and row.get("batchOperations") == 10_000, f"{scenario}: cycle contract drift")
        minimum_ms = exact_integer(row.get("minimumCycleActiveMilliseconds"), f"{scenario}.minimumCycleActiveMilliseconds")
        require(minimum_ms == 625, f"{scenario}: minimum cycle time drift")
        ratios = row.get("cycleRatios")
        cycles = row.get("cycleMeasurements")
        require(isinstance(ratios, list) and len(ratios) == 4, f"{scenario}: missing cycle ratios")
        require(isinstance(cycles, list) and len(cycles) == 4, f"{scenario}: missing cycle measurements")
        first_ops = second_ops = 0
        first_seconds = second_seconds = 0.0
        for index, (ratio, cycle) in enumerate(zip(ratios, cycles)):
            label = f"{scenario}.cycle[{index}]"
            require(isinstance(cycle, dict), f"{label}: malformed measurement")
            a_ops = exact_integer(cycle.get("firstOperations"), f"{label}.firstOperations")
            b_ops = exact_integer(cycle.get("secondOperations"), f"{label}.secondOperations")
            require(a_ops > 0 and a_ops == b_ops and a_ops % 40_000 == 0, f"{label}: unbalanced operations")
            a_seconds = positive_number(cycle.get("firstActiveSeconds"), f"{label}.firstActiveSeconds")
            b_seconds = positive_number(cycle.get("secondActiveSeconds"), f"{label}.secondActiveSeconds")
            require(a_seconds >= 0.625 and b_seconds >= 0.625, f"{label}: short active time")
            computed = (a_ops / a_seconds) / (b_ops / b_seconds)
            close(positive_number(ratio, f"{label}.cycleRatio"), computed, f"{label}.cycleRatio")
            close(positive_number(cycle.get("firstToSecondRatio"), f"{label}.firstToSecondRatio"), computed, f"{label}.firstToSecondRatio")
            first_ops += a_ops
            second_ops += b_ops
            first_seconds += a_seconds
            second_seconds += b_seconds
        geometric = math.exp(sum(math.log(value) for value in ratios) / 4)
        close(positive_number(row.get("firstToSecondRatio"), f"{scenario}.headline"), geometric, f"{scenario}.headline")
        aggregate = (first_ops / first_seconds) / (second_ops / second_seconds)
        close(positive_number(row.get("aggregateRateRatio"), f"{scenario}.aggregate"), aggregate, f"{scenario}.aggregate")
        spread = (max(ratios) / min(ratios) - 1) * 100
        require(type(row.get("cycleRatioSpreadPercent")) in (int, float), f"{scenario}: missing spread")
        require(abs(row["cycleRatioSpreadPercent"] - spread) <= 1e-9, f"{scenario}: spread mismatch")
        require(isinstance(row.get("commit"), str) and re.fullmatch(r"[0-9a-f]{40}", row["commit"]), f"{scenario}: invalid commit")
        require(isinstance(row.get("platform"), str) and row["platform"].startswith("Standalone IL2CPP x64 Release"), f"{scenario}: invalid platform")
        if commit is None:
            commit, platform = row["commit"], row["platform"]
        require((row["commit"], row["platform"]) == (commit, platform), f"{scenario}: provenance drift")
        rows[scenario] = {"ratio": geometric, "cycleRatios": ratios}
    require(rows.keys() == SCENARIOS, f"paired scenario set mismatch: {sorted(SCENARIOS - rows.keys())}")
    return {"schemaVersion": 1, "commit": commit, "platform": platform, "batchOrder": order, "pilotCpuWorkIterationsPerBatch": work, "rows": rows}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results_xml", type=Path)
    parser.add_argument("--batch-order", choices=tuple(PROTOCOLS), required=True)
    parser.add_argument("--cpu-work", type=int, required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(extract(args.results_xml, args.batch_order, args.cpu_work), sort_keys=True, separators=(",", ":")))
    except (ET.ParseError, OSError, ValueError, TypeError) as error:
        print(f"invalid pilot launch: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
