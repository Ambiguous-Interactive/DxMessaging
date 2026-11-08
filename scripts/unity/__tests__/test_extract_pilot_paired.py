"""Contract checks for the #510 raw paired launch extractor."""

import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path


SOURCE = Path(__file__).parents[1] / "extract-pilot-paired.py"
SPEC = importlib.util.spec_from_file_location("extract_pilot_paired", SOURCE)
PILOT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PILOT)


def fixture_rows(order="BAABABBA", work=23):
    rows = []
    for scenario in sorted(PILOT.SCENARIOS):
        row = {
            "scenario": scenario,
            "first": "DxMessaging",
            "second": "MessagePipe",
            "platform": "Standalone IL2CPP x64 Release (WindowsPlayer; Unity 6000.5.2f1)",
            "commit": "a" * 40,
            "protocol": PILOT.PROTOCOLS[order],
            "batchOrder": order,
            "pilotCpuWorkIterationsPerBatch": (
                (work[scenario] if type(work) is dict else work)
                if scenario in PILOT.TARGETS
                else 0
            ),
            "cycles": 4,
            "minimumCycleActiveMilliseconds": 625,
            "batchOperations": 10_000,
            "cycleRatios": [0.5] * 4,
            "cycleMeasurements": [
                {
                    "firstOperations": 40_000,
                    "firstActiveSeconds": 1.25,
                    "secondOperations": 40_000,
                    "secondActiveSeconds": 0.625,
                    "firstToSecondRatio": 0.5,
                }
                for _ in range(4)
            ],
            "firstToSecondRatio": 0.5,
            "aggregateRateRatio": 0.5,
            "cycleRatioSpreadPercent": 0.0,
        }
        rows.append(row)
    return rows


def write_xml(path, rows):
    root = ET.Element("test-run", failed="0")
    for row in rows:
        case = ET.SubElement(root, "test-case", result="Passed")
        ET.SubElement(case, "output").text = "DXM_PAIRED_COMPARISON " + json.dumps(row)
    ET.ElementTree(root).write(path, encoding="unicode")


class PilotExtractorTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.path = Path(self.directory.name) / "results.xml"

    def tearDown(self):
        self.directory.cleanup()

    def test_extracts_seven_independent_rows(self):
        write_xml(self.path, fixture_rows())
        result = PILOT.extract(self.path, "BAABABBA", 23)
        self.assertEqual(set(result["rows"]), PILOT.SCENARIOS)
        self.assertEqual(result["rows"]["GlobalToOne"]["ratio"], 0.5)
        self.assertEqual(PILOT.extract_xml(self.path.read_bytes(), "BAABABBA", 23), result)

    def test_extracts_named_vector_and_checks_each_target(self):
        work = dict(zip(PILOT.TARGET_ORDER, (11, 22, 33, 44, 55)))
        write_xml(self.path, fixture_rows(work=work))
        result = PILOT.extract(self.path, "BAABABBA", work)
        self.assertEqual(result["pilotCpuWorkByScenario"], work)
        self.assertNotIn("pilotCpuWorkIterationsPerBatch", result)
        rows = fixture_rows(work=work)
        next(row for row in rows if row["scenario"] == "Filtered")[
            "pilotCpuWorkIterationsPerBatch"
        ] = 22
        write_xml(self.path, rows)
        with self.assertRaisesRegex(ValueError, "Filtered: scheduled CPU work drift"):
            PILOT.extract(self.path, "BAABABBA", work)

    def test_vector_cli_and_invalid_vectors(self):
        work = dict(zip(PILOT.TARGET_ORDER, (11, 22, 33, 44, 55)))
        write_xml(self.path, fixture_rows(work=work))
        command = [
            sys.executable,
            str(SOURCE),
            str(self.path),
            "--batch-order",
            "BAABABBA",
            "--cpu-work-by-scenario",
        ]
        valid = subprocess.run(
            command + ["11,22,33,44,55"], capture_output=True, text=True, check=True
        )
        self.assertEqual(json.loads(valid.stdout)["pilotCpuWorkByScenario"], work)
        for vector in (
            "1,2,3,4",
            "1,2,3,4,5,6",
            "1,2,-3,4,5",
            "1,2,3,4,1000001",
            "1,2,3,4,05",
        ):
            with self.subTest(vector=vector):
                invalid = subprocess.run(command + [vector], capture_output=True, text=True)
                self.assertEqual(invalid.returncode, 1)
                self.assertIn("invalid scheduled CPU work vector", invalid.stderr)

    def test_rejects_noncanonical_vector_mapping(self):
        for work in (
            {"GlobalToOne": 1},
            dict(zip(PILOT.TARGET_ORDER, (1, 2, 3, 4, True))),
        ):
            with self.subTest(work=work), self.assertRaisesRegex(
                ValueError, "invalid scheduled CPU work vector"
            ):
                PILOT.extract_xml(b"<test-run/>", "BAABABBA", work)

    def test_rejects_scheduled_order_drift(self):
        write_xml(self.path, fixture_rows())
        with self.assertRaisesRegex(ValueError, "scheduled order drift"):
            PILOT.extract(self.path, "ABBABAAB", 23)

    def test_rejects_work_on_sentinel(self):
        rows = fixture_rows()
        next(row for row in rows if row["scenario"] == "GlobalToMany")[
            "pilotCpuWorkIterationsPerBatch"
        ] = 23
        write_xml(self.path, rows)
        with self.assertRaisesRegex(ValueError, "scheduled CPU work drift"):
            PILOT.extract(self.path, "BAABABBA", 23)

    def test_rejects_boolean_work_marker(self):
        rows = fixture_rows(work=0)
        next(row for row in rows if row["scenario"] == "GlobalToOne")[
            "pilotCpuWorkIterationsPerBatch"
        ] = False
        write_xml(self.path, rows)
        with self.assertRaisesRegex(ValueError, "scheduled CPU work drift"):
            PILOT.extract(self.path, "BAABABBA", 0)

    def test_rejects_pseudoreplicated_or_tampered_cycles(self):
        rows = fixture_rows()
        rows[0]["cycleMeasurements"][0]["firstActiveSeconds"] = 1.3
        write_xml(self.path, rows)
        with self.assertRaisesRegex(ValueError, "does not match raw cycles"):
            PILOT.extract(self.path, "BAABABBA", 23)

    def test_rejects_duplicate_scenario(self):
        rows = fixture_rows()
        rows.append(rows[0].copy())
        write_xml(self.path, rows)
        with self.assertRaisesRegex(ValueError, "duplicate paired scenario"):
            PILOT.extract(self.path, "BAABABBA", 23)


if __name__ == "__main__":
    unittest.main()
