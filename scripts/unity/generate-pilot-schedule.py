#!/usr/bin/env python3
"""Generate the sealed #510 palindrome pilot schedule before outcome collection."""

import argparse
import hashlib
import json
import sys
from pathlib import Path

import numpy as np


CONDITIONS = ("AA", "P03", "P05", "P10")
MULTIPLIERS = {"AA": 1.0, "P03": 1.03, "P05": 1.05, "P10": 1.10}
ORDERS = ("ABBABAAB", "BAABABBA")


def encoded(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--seed", required=True, help="256-bit lowercase hexadecimal seed")
    parser.add_argument("--schedule", required=True, type=Path)
    parser.add_argument("--key", required=True, type=Path)
    args = parser.parse_args()
    if len(args.seed) != 64 or any(character not in "0123456789abcdef" for character in args.seed):
        parser.error("--seed must be exactly 64 lowercase hexadecimal characters")
    if np.__version__ != "2.3.3":
        parser.error("schedule generation requires numpy 2.3.3")
    if args.schedule.resolve() == args.key.resolve():
        parser.error("schedule and assignment key must have different paths")

    rng = np.random.Generator(np.random.PCG64(int(args.seed, 16)))
    source_hash = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    units = []
    assignments = []
    for block in range(1, 4):
        block_units = []
        for condition in CONDITIONS:
            orientations = ["ABA", "BAB"]
            rng.shuffle(orientations)
            launch_patterns = []
            for _ in range(3):
                first = [False] * 5
                for index in rng.choice(5, size=int(rng.integers(2, 4)), replace=False):
                    first[int(index)] = True
                launch_patterns.append(first)
            for within_condition, orientation in enumerate(orientations):
                builds = []
                for slot, arm in enumerate(orientation):
                    pattern = launch_patterns[slot]
                    if within_condition == 1:
                        pattern = [not value for value in pattern]
                    builds.append(
                        {
                            "slot": slot + 1,
                            "arm": arm,
                            "cleanBuild": True,
                            "launches": [
                                {"index": index + 1, "batchOrder": ORDERS[0 if value else 1]}
                                for index, value in enumerate(pattern)
                            ],
                        }
                    )
                block_units.append((condition, builds))
        rng.shuffle(block_units)
        for position, (condition, builds) in enumerate(block_units, 1):
            unit_id = f"B{block:02d}U{position:02d}"
            units.append({"unitId": unit_id, "block": block, "position": position, "builds": builds})
            assignments.append(
                {
                    "unitId": unit_id,
                    "condition": condition,
                    "treatmentArm": "B",
                    "shimArm": None if condition == "AA" else "A",
                    "nominalTreatmentRateRatio": MULTIPLIERS[condition],
                }
            )

    key = {
        "schemaVersion": 1,
        "purpose": "sealed-pilot-condition-key",
        "generatorSourceSha256": source_hash,
        "generatorRuntime": {
            "python": sys.version.split()[0],
            "NumPy": np.__version__,
            "bitGenerator": "PCG64",
        },
        "seedHex": args.seed,
        "assignments": assignments,
    }
    key_bytes = encoded(key)
    schedule = {
        "schemaVersion": 1,
        "purpose": "blinded-three-build-palindrome-pilot",
        "generatorSourceSha256": source_hash,
        "generatorRuntime": key["generatorRuntime"],
        "assignmentKeySha256": hashlib.sha256(key_bytes).hexdigest(),
        "hardSerializedRunnerSeconds": 15 * 60 * 60,
        "approvedCleanBuilds": 72,
        "approvedPlayerLaunches": 360,
        "units": units,
    }
    if len(units) != 24 or sum(len(unit["builds"]) for unit in units) != 72:
        raise RuntimeError("pilot schedule does not contain exactly 24 palindromes and 72 builds")
    if sum(len(build["launches"]) for unit in units for build in unit["builds"]) != 360:
        raise RuntimeError("pilot schedule does not contain exactly 360 launches")
    conditions_by_unit = {assignment["unitId"]: assignment["condition"] for assignment in assignments}
    for block in range(1, 4):
        for condition in CONDITIONS:
            pair = [
                unit
                for unit in units
                if unit["block"] == block and conditions_by_unit[unit["unitId"]] == condition
            ]
            orientations = sorted("".join(build["arm"] for build in unit["builds"]) for unit in pair)
            if orientations != ["ABA", "BAB"]:
                raise RuntimeError(f"unbalanced palindrome orientation: block {block}, {condition}")
            for launch_index in range(5):
                orders = [
                    build["launches"][launch_index]["batchOrder"]
                    for unit in pair
                    for build in unit["builds"]
                ]
                if orders.count(ORDERS[0]) != 3 or orders.count(ORDERS[1]) != 3:
                    raise RuntimeError(f"unbalanced launch order: block {block}, {condition}")
    for path, data in ((args.schedule, encoded(schedule)), (args.key, key_bytes)):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    print("schedule sha256", hashlib.sha256(encoded(schedule)).hexdigest())
    print("assignment key sha256", schedule["assignmentKeySha256"])


if __name__ == "__main__":
    main()
