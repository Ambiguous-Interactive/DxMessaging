#!/usr/bin/env python3
"""Run fixed-N exact-transform checks on sealed #510 A/A palindrome effects."""

import argparse
import hashlib
import json
import math
import platform
import random
import re
import sys
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
T_95_DF5 = 2.0150483733330233
T_975_DF5 = 2.570581835636314
NORMAL_975 = 1.959963984540054
TRUTHS = {"AA": 1.0, "P03": 1.03, "P05": 1.05, "P10": 1.10}
DECISION_EPSILON = 1e-12


def require(condition, message):
    if not condition:
        raise ValueError(message)


def wilson(successes, trials):
    """Return two-sided 95% Wilson score bounds for a Bernoulli rate."""
    require(type(trials) is int and trials > 0, "Wilson trials must be positive")
    require(type(successes) is int and 0 <= successes <= trials, "Wilson successes out of range")
    p = successes / trials
    z2 = NORMAL_975 * NORMAL_975
    denominator = 1 + z2 / trials
    center = (p + z2 / (2 * trials)) / denominator
    half_width = NORMAL_975 * math.sqrt(p * (1 - p) / trials + z2 / (4 * trials * trials)) / denominator
    return center - half_width, center + half_width


def validate_aa(aa_effects):
    require(isinstance(aa_effects, dict) and aa_effects.keys() == SCENARIOS, "A/A scenario set drift")
    reference_ids = None
    values = {}
    for scenario in sorted(SCENARIOS):
        units = aa_effects[scenario]
        require(isinstance(units, list) and len(units) == 6, "A/A requires six whole palindromes")
        ids = [unit.get("unitId") for unit in units]
        require(all(isinstance(identity, str) and identity for identity in ids), "missing A/A unit identity")
        require(len(set(ids)) == 6, "duplicate A/A unit identity")
        if reference_ids is None:
            reference_ids = ids
        require(ids == reference_ids, "A/A scenario unit order drift")
        row = [unit.get("logEffect") for unit in units]
        require(all(type(value) in (int, float) and math.isfinite(value) for value in row), "invalid A/A effect")
        values[scenario] = row
    return values


def simulate(aa_effects, seed_hex, replicates=100_000):
    """Resample six whole A/A units, retaining their cross-row correlation.

    The same sampled hierarchy receives exact additive log transforms. Coverage
    is evaluated at the injected truth, while boundary rejection and positive
    power use the one-sided +3% margin. No data-dependent repeat count is used.
    """
    require(type(replicates) is int and replicates >= 100_000, "at least 100000 fixed replicates required")
    require(isinstance(seed_hex, str) and re.fullmatch(r"[0-9a-f]{64}", seed_hex), "separate 256-bit seed required")
    values = validate_aa(aa_effects)
    rng = random.Random(int(seed_hex, 16))
    draws = [bytes(rng.randrange(6) for _ in range(6)) for _ in range(replicates)]
    report = {"schemaVersion": 1, "replicatesPerTruthScenario": replicates, "seedHex": seed_hex, "rows": {}}
    margin = math.log(1.03)
    for scenario in sorted(SCENARIOS):
        baseline = values[scenario]
        covered = 0
        false_reject = 0
        positive_power = 0
        ten_percent_power = 0
        equivalent_at_null = 0
        for indices in draws:
            sample = [baseline[index] for index in indices]
            mean = sum(sample) / 6
            sum_squares = sum((value - mean) ** 2 for value in sample)
            se = math.sqrt(sum_squares / 5 / 6)
            lower95 = mean - T_95_DF5 * se
            lower95_two_sided = mean - T_975_DF5 * se
            upper95_two_sided = mean + T_975_DF5 * se
            lower90 = lower95
            upper90 = mean + T_95_DF5 * se
            covered += lower95_two_sided <= DECISION_EPSILON and upper95_two_sided >= -DECISION_EPSILON
            false_reject += lower95 > DECISION_EPSILON
            positive_power += lower95 + math.log(1.05) > margin + DECISION_EPSILON
            ten_percent_power += lower95 + math.log(1.10) > margin + DECISION_EPSILON
            equivalent_at_null += (
                lower90 > math.log(0.97) + DECISION_EPSILON
                and upper90 < margin - DECISION_EPSILON
            )
        coverage_lower, coverage_upper = wilson(covered, replicates)
        false_lower, false_upper = wilson(false_reject, replicates)
        power_lower, power_upper = wilson(positive_power, replicates)
        ten_lower, ten_upper = wilson(ten_percent_power, replicates)
        equivalent_lower, equivalent_upper = wilson(equivalent_at_null, replicates)
        report["rows"][scenario] = {
            "truths": {
                "AA": {"rateRatio": 1.0, "coverage95Count": covered, "equivalenceCount": equivalent_at_null},
                "P03": {"rateRatio": 1.03, "coverage95Count": covered, "falseRejectCount": false_reject},
                "P05": {"rateRatio": 1.05, "coverage95Count": covered, "superiorityCount": positive_power},
                "P10": {"rateRatio": 1.10, "coverage95Count": covered, "superiorityCount": ten_percent_power},
            },
            "coverage95": {"count": covered, "wilsonLower": coverage_lower, "wilsonUpper": coverage_upper},
            "boundaryFalseReject5": {"count": false_reject, "wilsonLower": false_lower, "wilsonUpper": false_upper},
            "powerAtFivePercent": {"count": positive_power, "wilsonLower": power_lower, "wilsonUpper": power_upper},
            "powerAtTenPercent": {"count": ten_percent_power, "wilsonLower": ten_lower, "wilsonUpper": ten_upper},
            "equivalenceAtNull": {"count": equivalent_at_null, "wilsonLower": equivalent_lower, "wilsonUpper": equivalent_upper},
            "passesFalseRejectLimit": false_upper <= 0.055,
            "passesCoverageFloor": coverage_lower >= 0.945,
            "passesDeclaredPowerFloor": power_lower >= 0.90,
        }
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--aa-effects", type=Path, required=True)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        config = json.loads(args.config.read_text())
        require(isinstance(config, dict), "simulation config must be an object")
        require(config.get("schemaVersion") == 1, "simulation config schema drift")
        require(config.get("replicatesPerTruthScenario") == 100_000, "simulation replicate count drift")
        require(config.get("pythonVersion") == platform.python_version(), "simulation Python version drift")
        require(config.get("truthRateRatios") == TRUTHS, "simulation truth levels drift")
        require(config.get("seedHex"), "missing simulation seed")
        source_hash = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        require(config.get("sourceSha256") == source_hash, "simulation source hash drift")
        effects = json.loads(args.aa_effects.read_text())
        report = simulate(effects, config["seedHex"])
        report["configSha256"] = hashlib.sha256(args.config.read_bytes()).hexdigest()
        args.output.write_text(json.dumps(report, sort_keys=True, separators=(",", ":")) + "\n")
    except (OSError, ValueError, TypeError) as error:
        print(f"pilot simulation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
