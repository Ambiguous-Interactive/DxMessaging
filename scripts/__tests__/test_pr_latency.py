"""PR latency must retain failed attempts and reject ambiguous or missing gate receipts."""

import importlib.util
import unittest
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location(
    "pr_latency", Path(__file__).resolve().parents[2] / "scripts/pr_latency.py"
)
report = importlib.util.module_from_spec(spec)
spec.loader.exec_module(report)


class LatencyTests(unittest.TestCase):
    def test_rerun_recovers_but_preserves_first_failed_verdict(self):
        root = "repos/owner/repo"
        start = "2026-01-02T10:00:00Z"
        failed = "2026-01-02T10:01:00Z"
        finish = "2026-01-02T10:07:00Z"

        def run(i):
            return {
                "id": i,
                "head_sha": "abc",
                "head_branch": "topic",
                "head_repository": {"full_name": "owner/repo"},
                "pull_requests": [{"number": 7}],
                "created_at": "2026-01-02T10:00:03Z",
                "run_attempt": 2 if i == 1 else 1,
                "conclusion": "success",
            }

        def job(i, name, status="success", end=finish):
            return {"id": i, "name": name, "conclusion": status, "completed_at": end}

        responses = {
            f"{root}/actions/workflows/{wf}/runs": [run(i)] if i != 3 else []
            for i, wf in enumerate(report.WORKFLOWS, 1)
        }
        responses.update(
            {
                f"{root}/activity": [{"after": "abc", "timestamp": start}],
                f"{root}/pulls/7": [{"created_at": "2026-01-01T10:00:00Z"}],
                f"{root}/issues/7/events": [],
                f"{root}/actions/runs/1/jobs": [job(10, "CI Success")],
                f"{root}/actions/runs/1/attempts/1/jobs": [
                    job(9, "CI Success", "failure", failed)
                ],
                f"{root}/actions/runs/2/jobs": [job(20, "Unity CI Success")],
                f"{root}/actions/runs/4/jobs": [job(40, "Devcontainer CI Success")],
            }
        )
        with patch.object(
            report, "pages", side_effect=lambda path, *a, **kw: iter(responses[path])
        ):
            (row,) = report.collect("owner/repo", 40)
        # GitHub drops embedded PR links after a source branch is deleted.
        for wf in report.WORKFLOWS:
            for item in responses[f"{root}/actions/workflows/{wf}/runs"]:
                item["pull_requests"] = []
        responses[f"{root}/pulls"] = [
            {
                "number": 7,
                "head": {"repo": {"full_name": "owner/repo"}, "ref": "topic"},
                "created_at": "2026-01-01T10:00:00Z",
                "closed_at": "2026-01-02T10:10:00Z",
            }
        ]
        with patch.object(
            report, "pages", side_effect=lambda path, *a, **kw: iter(responses[path])
        ):
            (recovered,) = report.collect("owner/repo", 40)
        self.assertEqual(row, recovered)
        self.assertTrue(row["retried"])
        self.assertEqual(
            ("success", 420, 60),
            (row["outcome"], row["seconds"], row["first_failed_gate_seconds"]),
        )
        self.assertEqual(0, report.summarize([row])["under_420s"])

    def test_missing_duplicate_and_pending_contexts(self):
        one = {"completed_at": "2026-01-02T10:00:05Z", "conclusion": "success"}
        gates = {key: [one] for key in report.GATES}
        self.assertEqual(("success", None), report.verdict(gates, None))
        for entry in ([], [one, one]):
            with self.subTest(entry=entry):
                self.assertEqual(
                    ("missing or ambiguous", None),
                    report.verdict({**gates, "CI Success": entry}, None),
                )
        self.assertEqual(
            ("pending", None),
            report.verdict({**gates, "CI Success": [{"completed_at": None}]}, None),
        )

    def test_percentiles_use_measured_verdicts_and_keep_missing_updates_visible(self):
        rows = [
            {
                "seconds": i,
                "retried": False,
                "outcome": "failure" if i == 38 else "success",
                "runs": [],
            }
            for i in range(1, 41)
        ]
        rows.append(
            {"seconds": None, "retried": False, "outcome": "pending", "runs": []}
        )
        result = report.summarize(rows)
        self.assertEqual(
            (41, 40, 1, 20.5, 38),
            (
                result["updates"],
                result["measured"],
                result["unmeasured"],
                result["p50_seconds"],
                result["p95_seconds"],
            ),
        )
        self.assertEqual(39, result["green_under_420s"])
