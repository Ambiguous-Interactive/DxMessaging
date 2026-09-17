#!/usr/bin/env python3
"""Exact clock and quantile contracts for issue #512."""

from __future__ import annotations

import importlib.util
import json
import math
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "unity" / "reduce_latency_samples.py"
SPEC = importlib.util.spec_from_file_location("reduce_latency_samples", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def sample(unit: str, duration: int, frequency: int = 1_000_000_000) -> dict[str, str]:
    return {
        "unitId": unit,
        "playerSha256": "a" * 64,
        "profileSha256": "b" * 64,
        "experimentSha256": "c" * 64,
        "startTick": "9007199254740993000000",
        "endTick": str(9007199254740993000000 + duration),
        "frequencyHz": str(frequency),
    }


class ReduceLatencySamplesTests(unittest.TestCase):
    def test_exact_upper_empirical_ranks_and_large_ticks(self) -> None:
        records = [sample(f"unit-{index}", value) for index, value in enumerate(range(1, 101))]
        result = MODULE.reduce_samples(list(reversed(records)))
        self.assertEqual(result["resultClass"], "descriptive-only")
        self.assertEqual(result["independentUnitCount"], 100)
        self.assertEqual(
            {name: item["roundedNanoseconds"] for name, item in result["quantiles"].items()},
            {"p50": "51", "p95": "96", "p99": "100"},
        )
        self.assertEqual(result["orderedSamples"][0]["startTick"], records[0]["startTick"])

    def test_rational_order_precedes_rounding_and_half_ties_round_up(self) -> None:
        records = [sample("slow", 3, 2_000_000_000), sample("fast", 2, 2_000_000_000)]
        result = MODULE.reduce_samples(records)
        self.assertEqual([row["unitId"] for row in result["orderedSamples"]], ["fast", "slow"])
        self.assertEqual([row["roundedNanoseconds"] for row in result["orderedSamples"]], ["1", "2"])
        self.assertEqual(result["orderedSamples"][1]["nanosecondsNumerator"], "3")
        self.assertEqual(result["orderedSamples"][1]["nanosecondsDenominator"], "2")

    def test_exact_one_percent_tail_moves_p99(self) -> None:
        baseline = [sample(f"unit-{index}", 1) for index in range(100)]
        stalled = [*baseline[:-1], sample("unit-99", 1_000_001)]
        self.assertEqual(MODULE.reduce_samples(baseline)["quantiles"]["p99"]["roundedNanoseconds"], "1")
        result = MODULE.reduce_samples(stalled)
        self.assertEqual(result["quantileConvention"], "strict-upper-empirical")
        self.assertEqual(result["quantiles"]["p99"]["roundedNanoseconds"], "1000001")
        self.assertEqual(result["quantiles"]["p95"]["roundedNanoseconds"], "1")
        self.assertEqual(result["conditionalRankBounds95"]["p99"]["status"], "N/A")
        self.assertIsNone(result["conditionalRankBounds95"]["p99"]["upperRank"])

    def test_exact_binomial_rank_boundary_and_ties(self) -> None:
        self.assertEqual(MODULE._rank_bounds(10, 50), (2, 9))
        denominator = 100**368
        lower_tail = sum(math.comb(368, k) * 99**k for k in range(360))
        upper_tail = 99**368
        self.assertLessEqual(40 * lower_tail, denominator)
        self.assertLessEqual(40 * upper_tail, denominator)
        for count, finite in ((100, False), (367, False), (368, True)):
            records = [sample(f"session-{index}", 10) for index in range(count)]
            bounds = MODULE.reduce_samples(records)["conditionalRankBounds95"]["p99"]
            with self.subTest(count=count):
                self.assertEqual(bounds["status"] == "finite", finite)
                self.assertEqual(bounds["upperRank"], count if finite else None)
                self.assertIsNotNone(bounds["lowerRank"])
                if finite:
                    self.assertEqual(bounds["lowerSample"]["roundedNanoseconds"], "10")
                    self.assertEqual(bounds["upperSample"]["roundedNanoseconds"], "10")
                    self.assertEqual(bounds["lowerRank"], 360)

    def test_sparse_unit_count_has_no_two_sided_interval(self) -> None:
        result = MODULE.reduce_samples([sample("one", 1)])
        for bounds in result["conditionalRankBounds95"].values():
            self.assertEqual(bounds["status"], "N/A")
            self.assertTrue(bounds["lowerRank"] is None or bounds["upperRank"] is None)

    def test_mixed_profile_or_experiment_cohort_is_rejected(self) -> None:
        first = sample("first", 1)
        second = sample("second", 2)
        second["playerSha256"] = "d" * 64
        result = MODULE.reduce_samples([first, second])
        self.assertEqual(result["profileSha256"], "b" * 64)
        self.assertEqual(result["experimentSha256"], "c" * 64)
        for field in ("profileSha256", "experimentSha256"):
            with self.subTest(field=field), self.assertRaises(ValueError):
                MODULE.reduce_samples([first, {**second, field: "e" * 64}])
        without_experiment = dict(second)
        del without_experiment["experimentSha256"]
        with self.assertRaises(ValueError):
            MODULE.reduce_samples([first, without_experiment])

    def test_invalid_clock_provenance_and_independence_are_rejected(self) -> None:
        valid = sample("one", 1)
        failures = [
            [],
            [valid, valid],
            [{**valid, "unitId": " "}],
            [{**valid, "playerSha256": "bad"}],
            [{**valid, "profileSha256": ""}],
            [{**valid, "experimentSha256": ""}],
            [{**valid, "startTick": "-1"}],
            [{**valid, "endTick": str(int(valid["startTick"]) - 1)}],
            [{**valid, "frequencyHz": "0"}],
            [{**valid, "frequencyHz": "01"}],
            [{**valid, "extra": "silent schema drift"}],
        ]
        for records in failures:
            with self.subTest(records=records), self.assertRaises(ValueError):
                MODULE.reduce_samples(records)

    def test_cli_matches_pure_reducer(self) -> None:
        records = [sample("one", 10), sample("two", 20)]
        with tempfile.TemporaryDirectory(prefix="dxm-latency-reducer-") as temporary:
            input_path = Path(temporary) / "input.json"
            input_path.write_text(json.dumps(records), encoding="utf-8")
            completed = subprocess.run(
                [sys.executable, str(SCRIPT), str(input_path)],
                check=True,
                capture_output=True,
                text=True,
            )
        self.assertEqual(json.loads(completed.stdout), MODULE.reduce_samples(records))


if __name__ == "__main__":
    unittest.main()
