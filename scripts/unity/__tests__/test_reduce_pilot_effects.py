"""Independent-unit and manifest RED checks for the #510 pilot reducer."""

import importlib.util
import math
import unittest
from pathlib import Path


SOURCE = Path(__file__).parents[1] / "reduce-pilot-effects.py"
SPEC = importlib.util.spec_from_file_location("reduce_pilot_effects", SOURCE)
PILOT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PILOT)


def fixture():
    schedule = []
    assignments = []
    builds = []
    for block in (1, 2, 3):
        for condition_index, condition in enumerate(("AA", "P03", "P05", "P10")):
            for orientation_index, orientation in enumerate(("ABA", "BAB")):
                unit_id = f"B{block:02d}U{condition_index * 2 + orientation_index + 1:02d}"
                scheduled_builds = []
                for slot, arm in enumerate(orientation, 1):
                    scheduled_launches = [
                        {"index": index, "batchOrder": "ABBABAAB" if index % 2 else "BAABABBA"}
                        for index in range(1, 6)
                    ]
                    scheduled_builds.append(
                        {"slot": slot, "arm": arm, "cleanBuild": True, "launches": scheduled_launches}
                    )
                    ratio = 0.5
                    if condition == "P05" and arm == "A":
                        ratio /= 1.05
                    launches = [
                        {
                            "index": launch["index"],
                            "batchOrder": launch["batchOrder"],
                            "launchId": f"{unit_id}-{slot}-{launch['index']}",
                            "ratios": {
                                scenario: (0.8 if scenario in PILOT.SENTINELS else ratio)
                                for scenario in PILOT.SCENARIOS
                            },
                        }
                        for launch in scheduled_launches
                    ]
                    builds.append(
                        {
                            "unitId": unit_id,
                            "slot": slot,
                            "buildId": f"{unit_id}-{slot}",
                            "sourceTree": "a" * 64,
                            "launches": launches,
                        }
                    )
                schedule.append(
                    {"unitId": unit_id, "block": block, "builds": scheduled_builds}
                )
                assignments.append(
                    {
                        "unitId": unit_id,
                        "condition": condition,
                        "treatmentArm": "B",
                        "shimArm": None if condition == "AA" else "A",
                    }
                )
    return schedule, assignments, builds


class PilotReducerTests(unittest.TestCase):
    def test_one_effect_per_palindrome_with_correct_direction(self):
        effects = PILOT.reduce(*fixture())
        self.assertEqual(len(effects["P05"]["GlobalToOne"]), 6)
        for unit in effects["P05"]["GlobalToOne"]:
            self.assertAlmostEqual(unit["logEffect"], math.log(1.05))
        for unit in effects["AA"]["GlobalToOne"]:
            self.assertAlmostEqual(unit["logEffect"], 0.0)
        for unit in effects["P05"]["GlobalToMany"]:
            self.assertAlmostEqual(unit["logEffect"], 0.0)

    def test_rejects_shared_binary(self):
        schedule, assignments, builds = fixture()
        builds[1]["buildId"] = builds[0]["buildId"]
        with self.assertRaisesRegex(ValueError, "shared or missing build identity"):
            PILOT.reduce(schedule, assignments, builds)

    def test_rejects_shared_launch(self):
        schedule, assignments, builds = fixture()
        builds[1]["launches"][0]["launchId"] = builds[0]["launches"][0]["launchId"]
        with self.assertRaisesRegex(ValueError, "shared or missing launch identity"):
            PILOT.reduce(schedule, assignments, builds)

    def test_rejects_schedule_drift(self):
        schedule, assignments, builds = fixture()
        builds[0]["launches"][0]["batchOrder"] = "BAABABBA"
        with self.assertRaisesRegex(ValueError, "launch batch order drift"):
            PILOT.reduce(schedule, assignments, builds)

    def test_rejects_changed_outer_arm(self):
        schedule, assignments, builds = fixture()
        schedule[0]["builds"][2]["arm"] = "B"
        with self.assertRaisesRegex(ValueError, "changed outer arms"):
            PILOT.reduce(schedule, assignments, builds)

    def test_rejects_missing_launch(self):
        schedule, assignments, builds = fixture()
        builds[0]["launches"].pop()
        with self.assertRaisesRegex(ValueError, "five launches"):
            PILOT.reduce(schedule, assignments, builds)

    def test_rejects_source_mismatch(self):
        schedule, assignments, builds = fixture()
        builds[0]["sourceTree"] = "b" * 64
        with self.assertRaisesRegex(ValueError, "source tree mismatch"):
            PILOT.reduce(schedule, assignments, builds)


if __name__ == "__main__":
    unittest.main()
