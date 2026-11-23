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
                f"{root}/activity": [
                    {"after": "abc", "timestamp": "2025-12-10T10:00:00Z"}
                ],
                f"{root}/pulls/7": [{"created_at": start}],
                f"{root}/issues/7/events": [],
                # A third attempt starts after listing. Its live job has not finished.
                f"{root}/actions/runs/1/jobs": [job(11, "CI Success", None, None)],
                f"{root}/actions/runs/1/attempts/2/jobs": [job(10, "CI Success")],
                f"{root}/actions/runs/1/attempts/1/jobs": [
                    job(9, "CI Success", "failure", failed)
                ],
                f"{root}/actions/runs/2/jobs": [
                    job(20, "Unity CI Success"),
                    job(21, "Self-hosted runner registration preflight", end=failed),
                    {
                        **job(22, "Unity 6000 all modes"),
                        "steps": [
                            {"name": "Set up job", "started_at": "2026-01-02T10:05:00Z"}
                        ],
                    },
                ],
                f"{root}/actions/runs/4/jobs": [job(40, "Devcontainer CI Success")],
            }
        )
        for run_id in (2, 4):
            responses[f"{root}/actions/runs/{run_id}/attempts/1/jobs"] = responses[
                f"{root}/actions/runs/{run_id}/jobs"
            ]
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
        responses[f"{root}/actions/workflows/ci.yml/runs"] = []
        with patch.object(
            report, "pages", side_effect=lambda path, *a, **kw: iter(responses[path])
        ):
            (orphan,) = report.collect("owner/repo", 40)
        self.assertEqual(
            ("missing or ambiguous", None), (orphan["outcome"], orphan["seconds"])
        )
        self.assertEqual(240, row["native_jobs"][0]["preflight_to_setup_seconds"])
        self.assertTrue(row["retried"])
        self.assertEqual(
            ("success", 420, 60),
            (row["outcome"], row["seconds"], row["first_failed_gate_seconds"]),
        )
        self.assertEqual(0, report.summarize([row])["under_420s"])

    def test_shared_branch_and_commit_distinguish_forks(self):
        root = "repos/owner/repo"
        start = "2026-01-02T10:00:00Z"
        finish = "2026-01-02T10:07:00Z"
        forks = ("first/repo", "second/repo")
        workflow_gates = {
            "ci.yml": "CI Success",
            "unity-tests.yml": "Unity CI Success",
            "devcontainer-test.yml": "Devcontainer CI Success",
        }
        responses = {}
        for workflow in report.WORKFLOWS:
            responses[f"{root}/actions/workflows/{workflow}/runs"] = []
        for number, fork in enumerate(forks, 1):
            head = {"repo": {"full_name": fork}, "ref": "topic"}
            responses[f"{root}/pulls", fork.split("/")[0] + ":topic"] = [
                {"number": number, "head": head, "created_at": start, "closed_at": None}
            ]
            responses[f"{root}/pulls/{number}"] = [{"created_at": start}]
            responses[f"{root}/issues/{number}/events"] = []
            responses[f"repos/{fork}/activity"] = [{"after": "abc", "timestamp": start}]
            for offset, (workflow, gate) in enumerate(workflow_gates.items()):
                run_id = number * 10 + offset
                responses[f"{root}/actions/workflows/{workflow}/runs"].append(
                    {
                        "id": run_id,
                        "head_sha": "abc",
                        "head_branch": "topic",
                        "head_repository": {"full_name": fork},
                        "pull_requests": [],
                        "created_at": start,
                        "run_attempt": 1,
                        "conclusion": "success",
                    }
                )
                responses[f"{root}/actions/runs/{run_id}/attempts/1/jobs"] = [
                    {
                        "id": run_id,
                        "name": gate,
                        "conclusion": "success",
                        "completed_at": finish,
                    }
                ]

        def pages(path, *args, **kwargs):
            key = (path, kwargs["head"]) if path == f"{root}/pulls" else path
            return iter(responses[key])

        with patch.object(report, "pages", side_effect=pages):
            rows = report.collect("owner/repo", 2)
            self.assertEqual(
                [(1, "success", 420), (2, "success", 420)],
                sorted((row["pr"], row["outcome"], row["seconds"]) for row in rows),
            )

            anchors = responses[f"{root}/actions/workflows/ci.yml/runs"]
            anchors.append({**anchors[0], "id": 30})
            responses[f"{root}/actions/runs/30/attempts/1/jobs"] = [
                {**responses[f"{root}/actions/runs/10/attempts/1/jobs"][0], "id": 30}
            ]
            rows = report.collect("owner/repo", 3)
            self.assertEqual(
                [(1, "ambiguous update", None)] * 2 + [(2, "success", 420)],
                sorted((row["pr"], row["outcome"], row["seconds"]) for row in rows),
            )

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
