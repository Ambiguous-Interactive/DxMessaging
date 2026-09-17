"""Contract checks for the #510 raw paired launch extractor."""

import importlib.util
import json
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
            "pilotCpuWorkIterationsPerBatch": work if scenario in PILOT.TARGETS else 0,
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
