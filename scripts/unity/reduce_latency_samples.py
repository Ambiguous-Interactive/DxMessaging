#!/usr/bin/env python3
"""Exact descriptive order statistics for independent latency units (#512)."""

from __future__ import annotations

import argparse
import json
import re
from fractions import Fraction
from pathlib import Path
from typing import Any


DECIMAL = re.compile(r"(?:0|[1-9][0-9]*)\Z")
SHA256 = re.compile(r"[0-9a-f]{64}\Z")
FIELDS = ("unitId", "playerSha256", "profileSha256", "startTick", "endTick", "frequencyHz")
PERCENTILES = (("p50", 50), ("p95", 95), ("p99", 99))


def _rank_bounds(count: int, percent: int) -> tuple[int | None, int | None]:
    """Exact equal-tail 95% binomial order ranks, with absent finite sides."""
    success = percent
    failure = 100 - percent
    total = 100**count
    tail_numerator = total  # Compare 40 * tail mass <= total (alpha/2 = 1/40).
    mass = failure**count
    cumulative = mass
    lower = None
    upper = None
    for rank in range(1, count + 1):
        if 40 * cumulative <= tail_numerator:
            lower = rank
        if upper is None and 40 * (total - cumulative) <= tail_numerator:
            upper = rank
        if rank < count:
            mass = mass * (count - rank + 1) * success // (rank * failure)
            cumulative += mass
    return lower, upper


def _decimal(value: Any, field: str) -> int:
    if not isinstance(value, str) or DECIMAL.fullmatch(value) is None:
        raise ValueError(f"{field} must be a canonical nonnegative decimal string")
    return int(value)


def reduce_samples(records: Any) -> dict[str, Any]:
    """Return strict-upper empirical quantiles without an adequacy or release verdict."""
    if not isinstance(records, list) or not records:
        raise ValueError("records must be a nonempty array of independent units")
    seen: set[str] = set()
    ordered: list[tuple[Fraction, dict[str, str]]] = []
    for index, record in enumerate(records):
        if not isinstance(record, dict) or set(record) != set(FIELDS):
            raise ValueError(f"records[{index}] must have exactly the required fields")
        unit = record["unitId"]
        if not isinstance(unit, str) or not unit.strip() or unit != unit.strip():
            raise ValueError(f"records[{index}].unitId must be a nonblank trimmed string")
        if unit in seen:
            raise ValueError(f"duplicate independent unitId: {unit}")
        seen.add(unit)
        for field in ("playerSha256", "profileSha256"):
            if not isinstance(record[field], str) or SHA256.fullmatch(record[field]) is None:
                raise ValueError(f"records[{index}].{field} must be a SHA-256 hex digest")
        start = _decimal(record["startTick"], f"records[{index}].startTick")
        end = _decimal(record["endTick"], f"records[{index}].endTick")
        frequency = _decimal(record["frequencyHz"], f"records[{index}].frequencyHz")
        if frequency == 0 or end < start:
            raise ValueError(f"records[{index}] has zero frequency or reversed timestamps")
        nanoseconds = Fraction((end - start) * 1_000_000_000, frequency)
        sample = dict(record)
        sample["nanosecondsNumerator"] = str(nanoseconds.numerator)
        sample["nanosecondsDenominator"] = str(nanoseconds.denominator)
        sample["roundedNanoseconds"] = str(
            (2 * nanoseconds.numerator + nanoseconds.denominator)
            // (2 * nanoseconds.denominator)
        )
        ordered.append((nanoseconds, sample))
    ordered.sort(key=lambda item: (item[0], item[1]["unitId"]))
    count = len(ordered)
    quantiles = {name: ordered[percent * count // 100][1] for name, percent in PERCENTILES}
    rank_bounds = {}
    for name, percent in PERCENTILES:
        lower, upper = _rank_bounds(count, percent)
        rank_bounds[name] = {
            "status": "finite" if lower is not None and upper is not None else "N/A",
            "lowerRank": lower,
            "upperRank": upper,
            "lowerSample": ordered[lower - 1][1] if lower is not None else None,
            "upperSample": ordered[upper - 1][1] if upper is not None else None,
        }
    return {
        "schemaVersion": 1,
        "resultClass": "descriptive-only",
        "quantileConvention": "strict-upper-empirical",
        "nanosecondRounding": "nearest-integer-half-up",
        "independentUnitCount": count,
        "orderedSamples": [sample for _, sample in ordered],
        "quantiles": quantiles,
        "conditionalRankBounds95": rank_bounds,
        "rankBoundMethod": "exact-binomial-equal-tail-order-statistics",
        "rankBoundConfidence": {"numerator": "19", "denominator": "20"},
        "rankBoundAssumptions": "iid-independent-units-unverified",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, help="JSON array of one timestamp pair per independent unit")
    args = parser.parse_args()
    result = reduce_samples(json.loads(args.input.read_text(encoding="utf-8")))
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
