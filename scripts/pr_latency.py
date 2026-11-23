"""Read GitHub Actions receipts for the most recent pull request updates observed by static or devcontainer CI."""

import argparse
import json
import os
import re
from collections import Counter, defaultdict
from datetime import datetime, timedelta
from functools import lru_cache
from itertools import islice
from math import ceil
from statistics import median
from urllib.parse import urlencode
from urllib.request import Request, urlopen

GATES = {"CI Success", "Unity CI Success", "Devcontainer CI Success"}
WORKFLOWS = (
    "ci.yml",
    "unity-tests.yml",
    "unity-docs-gate.yml",
    "devcontainer-test.yml",
)


def instant(value):
    return datetime.fromisoformat(value)


def summarize(rows):
    measured = sorted(r["seconds"] for r in rows if r["seconds"] is not None)
    return {
        "updates": len(rows),
        "outcomes": dict(Counter(r["outcome"] for r in rows)),
        "updates_with_cancelled_runs": sum(
            any(r[2] == "cancelled" for r in row["runs"]) for row in rows
        ),
        "superseded": sum(row.get("superseded", False) for row in rows),
        "unmeasured": len(rows) - len(measured),
        "retried": sum(r["retried"] for r in rows),
        "measured": len(measured),
        "green_under_420s": sum(
            r["outcome"] == "success"
            and r["seconds"] is not None
            and r["seconds"] < 420
            for r in rows
        ),
        "under_420s": sum(s < 420 for s in measured),
        "p50_seconds": median(measured) if measured else None,
        "p95_seconds": measured[ceil(0.95 * len(measured)) - 1] if measured else None,
    }


def verdict(gates, start):
    if set(gates) != GATES or any(len(js) != 1 for js in gates.values()):
        return "missing or ambiguous", None
    jobs = [js[0] for js in gates.values()]
    if any(not j["completed_at"] or not j["conclusion"] for j in jobs):
        return "pending", None
    outcomes = {j["conclusion"] for j in jobs}
    outcome = (
        "success"
        if outcomes == {"success"}
        else "+".join(sorted(outcomes - {"success"}))
    )
    elapsed = (
        (max(instant(j["completed_at"]) for j in jobs) - start).total_seconds()
        if start
        else None
    )
    return outcome, elapsed if elapsed is not None and elapsed >= 0 else None


def native_receipts(jobs):
    preflights = [
        j.get("completed_at")
        for j in jobs
        if j["name"] == "Self-hosted runner registration preflight"
        and j["conclusion"] == "success"
    ]
    ready = preflights[0] if len(preflights) == 1 else None
    for job in jobs:
        if not (job["name"].startswith("Unity ") and "all modes" in job["name"]):
            continue
        setup = next(
            (
                s.get("started_at")
                for s in job.get("steps") or []
                if s["name"] == "Set up job"
            ),
            None,
        )
        yield {
            **{
                key: job.get(key)
                for key in (
                    "id",
                    "runner_name",
                    "created_at",
                    "started_at",
                    "completed_at",
                    "conclusion",
                    "steps",
                )
            },
            "preflight_completed_at": ready,
            "preflight_to_setup_seconds": max(
                0, (instant(setup) - instant(ready)).total_seconds()
            )
            if ready and setup
            else None,
        }


def pages(path, field=None, **params):
    url = "https://api.github.com/" + path + "?" + urlencode(dict(params, per_page=100))
    headers = {"Accept": "application/vnd.github+json"}
    token = os.getenv("GH_TOKEN") or os.getenv("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = "Bearer " + token
    while url:
        if not url.startswith("https://api.github.com/"):
            raise ValueError("Unexpected GitHub pagination URL")
        with urlopen(Request(url, headers=headers), timeout=30) as response:
            data = json.load(response)
            link = response.headers.get("Link", "")
        if field:
            data = data[field]
        yield from data if isinstance(data, list) else [data]
        next_page = re.search(r'<([^>]+)>; rel="next"', link)
        url = next_page.group(1) if next_page else None


def collect(repo, count, through=None):
    root = f"repos/{repo}"

    def runs(workflow, **params):
        return pages(
            f"{root}/actions/workflows/{workflow}/runs",
            "workflow_runs",
            event="pull_request",
            **params,
        )

    cutoff = {"created": "<=" + through} if through else {}
    static = list(islice(runs(WORKFLOWS[0], **cutoff), count + 1))
    containers = list(islice(runs(WORKFLOWS[-1], **cutoff), count + 1))

    def source(run):
        return (
            (run.get("head_repository") or {}).get("full_name"),
            run["head_branch"],
            run["head_sha"],
        )

    static_heads = {source(run) for run in static}
    orphaned = [run for run in containers if source(run) not in static_heads]
    anchors = sorted(
        static + orphaned, key=lambda r: (r["created_at"], r["id"]), reverse=True
    )[:count]
    if not anchors:
        return []
    since = min(instant(r["created_at"]) for r in anchors) - timedelta(days=1)
    until = max(instant(r["created_at"]) for r in anchors) + timedelta(days=1)
    window = f"{since.date().isoformat()}..{until.date().isoformat()}"
    siblings = defaultdict(list)
    for wf in WORKFLOWS[1:]:
        for run in runs(wf, created=window):
            siblings[run["head_sha"], run["head_branch"]].append(run)

    @lru_cache(None)
    def pr_dates(number):
        opened = next(pages(f"{root}/pulls/{number}"))["created_at"]
        reopened = [
            e["created_at"]
            for e in pages(f"{root}/issues/{number}/events")
            if e["event"] == "reopened"
        ]
        return [instant(t) for t in [opened, *reopened]]

    @lru_cache(None)
    def pushes(head_repo, branch):
        remaining = {
            (a["head_sha"], instant(a["created_at"]))
            for a in anchors
            if source(a)[:2] == (head_repo, branch)
        }
        result = defaultdict(list)
        for item in pages(
            f"repos/{head_repo}/activity", ref="refs/heads/" + branch, direction="desc"
        ):
            stamp = instant(item["timestamp"])
            sha = item["after"]
            matched = {(key, t) for key, t in remaining if key == sha and stamp <= t}
            if matched:
                result[sha].append(stamp)
                remaining -= matched
                if not remaining:
                    break
        return result

    @lru_cache(None)
    def branch_prs(origin, branch):
        return list(
            pages(
                f"{root}/pulls", state="all", head=origin.split("/")[0] + ":" + branch
            )
        )

    rows = []
    for anchor in anchors:
        sha, branch = anchor["head_sha"], anchor["head_branch"]
        linked = anchor["pull_requests"]
        number = linked[0]["number"] if len(linked) == 1 else None
        created = instant(anchor["created_at"])
        origin = (anchor.get("head_repository") or {}).get("full_name")
        if not linked and origin:
            matches = [
                p["number"]
                for p in branch_prs(origin, branch)
                if (p["head"].get("repo") or {}).get("full_name") == origin
                and p["head"]["ref"] == branch
                and instant(p["created_at"]) <= created
                and (not p["closed_at"] or instant(p["closed_at"]) >= created)
            ]
            number = matches[0] if len(matches) == 1 else None
        group = [anchor] + [
            r
            for r in siblings[sha, branch]
            if r["id"] != anchor["id"]
            and (r.get("head_repository") or {}).get("full_name") == origin
            and (
                not number
                or not r["pull_requests"]
                or number in [p["number"] for p in r["pull_requests"]]
            )
        ]
        start = None
        if number and origin:
            # For opens and reopens, the push can legitimately precede the PR event.
            candidates = [t for t in pushes(origin, branch)[sha] if t <= created]
            if candidates:
                candidates += [t for t in pr_dates(number) if t <= created]
                start = max(candidates)
        jobs, native, gates, history = [], [], defaultdict(list), {}
        for r in group:
            latest = list(
                pages(
                    f"{root}/actions/runs/{r['id']}/attempts/{r['run_attempt']}/jobs",
                    "jobs",
                )
            )
            jobs.extend(latest)
            native.extend(native_receipts(latest))
            for attempt in range(1, r["run_attempt"]):
                previous = pages(
                    f"{root}/actions/runs/{r['id']}/attempts/{attempt}/jobs", "jobs"
                )
                history[f"{r['id']}/{attempt}"] = [
                    [j["name"], j["conclusion"], j["completed_at"]] for j in previous
                ]
            for j in latest:
                if j["name"] in GATES:
                    gates[j["name"]].append(j)
        outcome, seconds = verdict(gates, start)
        if (
            not number
            or sum(
                source(a) == (origin, branch, sha)
                and a["pull_requests"] == linked
                for a in anchors
            )
            > 1
        ):
            outcome, start, seconds = "ambiguous update", None, None
        built = any(
            j["name"].startswith("Build + smoke-test devcontainer")
            and j["conclusion"] != "skipped"
            for j in jobs
        )
        lane = (
            "Unity"
            if native
            else "companion gate"
            if gates.get("Unity CI Success")
            else "unknown Unity lane"
        ) + (" + devcontainer" if built else "")
        terminal = [
            (name, j["conclusion"], j["completed_at"])
            for name, group_jobs in gates.items()
            for j in group_jobs
        ]
        failed_at = [
            instant(end)
            for name, status, end in terminal
            + [j for attempt in history.values() for j in attempt]
            if name in GATES and end and status in ("failure", "timed_out")
        ]
        rows.append(
            {
                "pr": number,
                "sha": sha,
                "lane": lane,
                "outcome": outcome,
                "seconds": seconds,
                "updated_at": start.isoformat() if start else None,
                "retried": any(r["run_attempt"] > 1 for r in group),
                "previous_attempts": history,
                "first_failed_gate_seconds": (min(failed_at) - start).total_seconds()
                if failed_at and start
                else None,
                "runs": [(r["id"], r["run_attempt"], r["conclusion"]) for r in group],
                "gates": {
                    k: [(j["id"], j["conclusion"], j["completed_at"]) for j in v]
                    for k, v in gates.items()
                },
                "queue_seconds": [
                    (
                        instant(j["started_at"]) - instant(j["created_at"])
                    ).total_seconds()
                    for j in native
                    if j.get("created_at") and j.get("started_at")
                ],
                "native_jobs": native,
            }
        )
    for index, row in enumerate(rows):
        cancelled_at = [
            instant(job[2])
            for group in row["gates"].values()
            for job in group
            if job[1] == "cancelled" and job[2]
        ]
        row["superseded"] = bool(
            cancelled_at
            and row["pr"]
            and any(
                newer["pr"] == row["pr"]
                and newer["sha"] != row["sha"]
                and newer["updated_at"]
                and instant(newer["updated_at"]) <= max(cancelled_at)
                for newer in rows[:index]
            )
        )
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default="Ambiguous-Interactive/DxMessaging")
    parser.add_argument("--count", type=int, default=40)
    parser.add_argument(
        "--through", help="latest workflow creation timestamp to include (UTC)"
    )
    args = parser.parse_args()
    if args.count < 1:
        parser.error("--count must be positive")
    rows = collect(args.repo, args.count, args.through)
    by_lane = {
        lane: summarize([r for r in rows if r["lane"] == lane])
        for lane in sorted({r["lane"] for r in rows})
    }
    print(
        json.dumps(
            {"total": summarize(rows), "by_lane": by_lane, "updates": rows}, indent=2
        )
    )


if __name__ == "__main__":
    main()
