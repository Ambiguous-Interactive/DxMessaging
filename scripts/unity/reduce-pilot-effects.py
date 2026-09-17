#!/usr/bin/env python3
"""Reduce validated #510 launch ratios to independent palindrome effects.

Inputs to ``reduce`` must already have passed raw XML, build identity, host-health,
and arm-blind validity checks. This module performs only the fixed hierarchy
reduction; it never treats launches or cycles as independent observations.
"""

import math
import re
import statistics
from collections import defaultdict


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
SENTINELS = frozenset(("GlobalToMany", "KeyedToOne"))
TARGETS = SCENARIOS - SENTINELS
CONDITIONS = frozenset(("AA", "P03", "P05", "P10"))
NOMINAL_RATIOS = {"AA": 1.0, "P03": 1.03, "P05": 1.05, "P10": 1.10}
# scipy.stats.t.ppf(0.95, df=5), SciPy 1.16.2. Pilot N is fixed at six.
PILOT_T_95_DF5 = 2.0150483733330233


def require(condition, message):
    if not condition:
        raise ValueError(message)


def launch_effects(ratios):
    require(isinstance(ratios, dict) and ratios.keys() == SCENARIOS, "launch scenario set drift")
    logs = {}
    for scenario, ratio in ratios.items():
        require(type(ratio) in (int, float) and math.isfinite(ratio) and ratio > 0, f"{scenario}: invalid ratio")
        logs[scenario] = math.log(ratio)
    sentinel_mean = sum(logs[scenario] for scenario in SENTINELS) / 2
    return {scenario: logs[scenario] - sentinel_mean for scenario in SCENARIOS}


def reduce(schedule, assignments, builds):
    """Return condition -> scenario -> six unit effects from 72 validated builds.

    A build record has unitId, slot, buildId, sourceTree, and five launches.
    Each launch has index, batchOrder, launchId, and seven ratio values. Callers
    must prove the build/health/raw-XML evidence before constructing records.
    """
    require(len(schedule) == 24, "schedule must contain 24 palindromes")
    require(len(assignments) == 24, "assignment key must contain 24 palindromes")
    require(len(builds) == 72, "pilot must contain exactly 72 independently clean builds")
    by_unit = {unit["unitId"]: unit for unit in schedule}
    require(len(by_unit) == 24, "duplicate scheduled unit")
    assignment_by_unit = {assignment["unitId"]: assignment for assignment in assignments}
    require(len(assignment_by_unit) == 24 and assignment_by_unit.keys() == by_unit.keys(), "assignment key drift")
    seen_builds = set()
    seen_launches = set()
    actual = {}
    source_trees = set()
    for build in builds:
        unit_id = build.get("unitId")
        slot = build.get("slot")
        require(unit_id in by_unit and type(slot) is int and slot in (1, 2, 3), "unscheduled build slot")
        position = (unit_id, slot)
        require(position not in actual, "duplicate build slot")
        scheduled = by_unit[unit_id]["builds"][slot - 1]
        require(scheduled["slot"] == slot and scheduled["cleanBuild"] is True, "schedule build drift")
        build_id = build.get("buildId")
        require(isinstance(build_id, str) and build_id and build_id not in seen_builds, "shared or missing build identity")
        seen_builds.add(build_id)
        source_tree = build.get("sourceTree")
        require(isinstance(source_tree, str) and re.fullmatch(r"[0-9a-f]{64}", source_tree), "missing source tree")
        source_trees.add(source_tree)
        launches = build.get("launches")
        require(isinstance(launches, list) and len(launches) == 5, "build requires five launches")
        launch_values = []
        for index, launch in enumerate(launches, 1):
            expected = scheduled["launches"][index - 1]
            require(launch.get("index") == index == expected["index"], "launch index drift")
            require(launch.get("batchOrder") == expected["batchOrder"], "launch batch order drift")
            launch_id = launch.get("launchId")
            require(isinstance(launch_id, str) and launch_id and launch_id not in seen_launches, "shared or missing launch identity")
            seen_launches.add(launch_id)
            launch_values.append(launch_effects(launch.get("ratios")))
        actual[position] = {
            "arm": scheduled["arm"],
            "values": {
                scenario: sum(value[scenario] for value in launch_values) / 5
                for scenario in SCENARIOS
            },
        }
    require(len(actual) == 72 and len(seen_launches) == 360, "missing scheduled build or launch")
    require(len(source_trees) == 1, "source tree mismatch")
    effects = defaultdict(lambda: defaultdict(list))
    orientation_counts = defaultdict(lambda: defaultdict(int))
    for unit in schedule:
        unit_id = unit["unitId"]
        assignment = assignment_by_unit[unit_id]
        condition = assignment.get("condition")
        require(condition in CONDITIONS and assignment.get("treatmentArm") == "B", "condition or treatment drift")
        require(assignment.get("shimArm") == (None if condition == "AA" else "A"), "shim arm drift")
        require(assignment.get("nominalTreatmentRateRatio") == NOMINAL_RATIOS[condition], "nominal ratio drift")
        slots = [actual[(unit_id, slot)] for slot in (1, 2, 3)]
        orientation = "".join(slot["arm"] for slot in slots)
        require(orientation in ("ABA", "BAB"), "changed outer arms")
        orientation_counts[(unit["block"], condition)][orientation] += 1
        for scenario in SCENARIOS:
            center = slots[1]["values"][scenario]
            outer = (slots[0]["values"][scenario] + slots[2]["values"][scenario]) / 2
            effect = center - outer if orientation == "ABA" else outer - center
            effects[condition][scenario].append({"unitId": unit_id, "logEffect": effect})
    for block in (1, 2, 3):
        for condition in CONDITIONS:
            require(orientation_counts[(block, condition)] == {"ABA": 1, "BAB": 1}, "block orientation imbalance")
    for condition in CONDITIONS:
        for scenario in SCENARIOS:
            require(len(effects[condition][scenario]) == 6, "condition lacks six independent units")
    return {condition: dict(rows) for condition, rows in effects.items()}


def pilot_intervals(effects):
    """Compute pilot Student-t intervals using six palindromes per condition.

    The one-sided 95% lower bound and two-sided 90% TOST interval share the
    same t critical value. Flags are row diagnostics, not a global pilot verdict.
    """
    require(isinstance(effects, dict) and effects.keys() == CONDITIONS, "pilot condition set drift")
    result = {}
    for condition in sorted(CONDITIONS):
        rows = effects[condition]
        require(isinstance(rows, dict) and rows.keys() == SCENARIOS, "pilot scenario set drift")
        result[condition] = {}
        for scenario in sorted(SCENARIOS):
            units = rows[scenario]
            require(isinstance(units, list) and len(units) == 6, "interval needs six independent palindromes")
            identities = [unit.get("unitId") for unit in units]
            require(all(isinstance(identity, str) and identity for identity in identities), "missing palindrome identity")
            require(len(set(identities)) == 6, "pseudo-replicated palindrome identity")
            values = [unit.get("logEffect") for unit in units]
            require(all(type(value) in (int, float) and math.isfinite(value) for value in values), "invalid unit effect")
            mean = statistics.mean(values)
            standard_error = statistics.stdev(values) / math.sqrt(6)
            half_width = PILOT_T_95_DF5 * standard_error
            lower = mean - half_width
            upper = mean + half_width
            result[condition][scenario] = {
                "nIndependentPalindromes": 6,
                "meanLogEffect": mean,
                "sampleStandardDeviation": statistics.stdev(values),
                "lower95OneSidedLog": lower,
                "lower90TwoSidedLog": lower,
                "upper90TwoSidedLog": upper,
                "aboveThreePercent": lower > math.log(1.03),
                "affectedRowSafe": lower > math.log(0.97),
                "equivalentWithinThreePercent": lower > math.log(0.97)
                and upper < math.log(1.03),
            }
    return result
