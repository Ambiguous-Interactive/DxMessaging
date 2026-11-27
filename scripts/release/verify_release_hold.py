#!/usr/bin/env python3
"""Fail closed unless the 4.0.0 release hold and exact-candidate approval are satisfied.

The release workflows call this before preparing, tagging and publishing. It treats a zero
open-issue count as necessary but not sufficient: tag and publish also require readiness issue
#575 to be closed as completed and to carry wallstop's approval marker for the exact version,
source tree and package bytes. The marker is a durable record, not the identity proof; the
authoritative human approval is the protected environment review (`release-tag` and `release`),
which the workflows verify separately. Only human wallstop may post the marker. Agents must never
create, edit or copy it.

Marker format (an HTML comment on #575):

    <!-- dxm-release-approval: {"version":"4.0.0","tree":"<40 hex>",
    "package_sha256":"<64 hex>","destinations":["npm","github-release"]} -->

Only human wallstop may post the marker. Agents must never create, edit or copy it.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
from pathlib import Path

MILESTONE = 1
READINESS_ISSUE = 575
HOLD_VERSION = "4.0.0"
APPROVER = "wallstop"
MARKER = re.compile(r"<!--\s*dxm-release-approval:\s*(\{.*?\})\s*-->", re.DOTALL)
TREE = re.compile(r"[0-9a-f]{40}")
SHA256 = re.compile(r"[0-9a-f]{64}")
KNOWN_DESTINATIONS = {"npm", "github-release", "asset-store"}
REQUIRED_DESTINATIONS = {"npm", "github-release"}


def _records(payload: object) -> list[dict]:
    """Flatten `gh api --paginate --slurp` output into one record list."""
    if not isinstance(payload, list):
        raise ValueError("Expected a paginated JSON list")
    records = []
    for page in payload:
        if isinstance(page, list):
            records.extend(item for item in page if isinstance(item, dict))
        elif isinstance(page, dict):
            records.append(page)
        else:
            raise ValueError("Unexpected paginated response shape")
    return records


def issues_only(records: list[dict]) -> list[dict]:
    """Drop pull requests: only issues hold the release."""
    return [record for record in records if "pull_request" not in record]


def _gh(*args: str) -> object:
    try:
        result = subprocess.run(
            ["gh", *args],
            check=True,
            capture_output=True,
            text=True,
        )
    except subprocess.CalledProcessError as error:
        detail = (error.stderr or error.stdout or "").strip()
        print(f"::error::GitHub query failed: {detail}", file=sys.stderr)
        raise SystemExit(1) from error
    return json.loads(result.stdout)


def open_milestone_issues(repository: str) -> list[dict]:
    payload = _gh(
        "api",
        "--paginate",
        "--slurp",
        f"repos/{repository}/issues?state=open&milestone={MILESTONE}&per_page=100",
    )
    return issues_only(_records(payload))


def readiness_issue(repository: str) -> dict:
    issue = _gh("api", f"repos/{repository}/issues/{READINESS_ISSUE}")
    if not isinstance(issue, dict):
        raise ValueError("Expected a single issue response")
    return issue


def readiness_comments(repository: str) -> list[dict]:
    return _records(
        _gh(
            "api",
            "--paginate",
            "--slurp",
            f"repos/{repository}/issues/{READINESS_ISSUE}/comments?per_page=100",
        )
    )


def approval_markers(comments: list[dict]) -> list[dict]:
    """Return every syntactically valid approval marker posted by wallstop."""
    markers = []
    for comment in comments:
        user = comment.get("user") or {}
        if user.get("login") != APPROVER:
            continue
        body = comment.get("body") or ""
        for match in MARKER.finditer(body):
            try:
                marker = json.loads(match.group(1))
            except json.JSONDecodeError:
                continue
            if isinstance(marker, dict):
                marker["_created_at"] = comment.get("created_at", "")
                marker["_updated_at"] = comment.get("updated_at", "")
                markers.append(marker)
    return markers


def matching_approval(
    comments: list[dict],
    version: str,
    tree: str,
    package_sha256: str | None = None,
) -> tuple[dict | None, list[str]]:
    """Find wallstop's approval for the exact version/tree/package/destinations."""
    problems = []
    for marker in approval_markers(comments):
        marker_version = marker.get("version")
        marker_tree = marker.get("tree")
        marker_sha = marker.get("package_sha256")
        destinations = marker.get("destinations")
        if marker_version != version:
            problems.append(f"version {marker_version!r} != {version!r}")
            continue
        if not isinstance(marker_tree, str) or not TREE.fullmatch(marker_tree):
            problems.append("tree is not a 40-character lowercase hex SHA")
            continue
        if marker_tree != tree.lower():
            problems.append(f"tree {marker_tree} != {tree.lower()}")
            continue
        if not isinstance(marker_sha, str) or not SHA256.fullmatch(marker_sha):
            problems.append("package_sha256 is not a 64-character lowercase hex digest")
            continue
        if package_sha256 is not None and marker_sha != package_sha256:
            problems.append(f"package_sha256 {marker_sha} != {package_sha256}")
            continue
        if (
            not isinstance(destinations, list)
            or not destinations
            or any(not isinstance(item, str) for item in destinations)
            or not set(destinations) <= KNOWN_DESTINATIONS
            or not REQUIRED_DESTINATIONS <= set(destinations)
        ):
            problems.append(
                "destinations must include npm and github-release and may also include asset-store"
            )
            continue
        return marker, []
    return None, problems


def evaluate(
    open_issues: list[dict],
    issue: dict | None,
    comments: list[dict],
    *,
    phase: str,
    version: str,
    tree: str,
    package_sha256: str | None = None,
) -> list[str]:
    """Return the blocking errors for one workflow phase."""
    if version != HOLD_VERSION:
        return []
    errors = []
    blocking = [
        item
        for item in open_issues
        if not (phase == "prepare" and item.get("number") == READINESS_ISSUE)
    ]
    if blocking:
        numbers = ", ".join(f"#{item.get('number')}" for item in blocking)
        errors.append(f"{len(blocking)} open 4.0.0 milestone issue(s) remain: {numbers}")
    if phase == "prepare":
        return errors

    if issue is None:
        errors.append(f"Readiness issue #{READINESS_ISSUE} could not be read")
        return errors
    if issue.get("state") != "closed":
        errors.append(f"Readiness issue #{READINESS_ISSUE} is still open")
        return errors
    if issue.get("state_reason") != "completed":
        errors.append(
            f"Readiness issue #{READINESS_ISSUE} closed as {issue.get('state_reason')!r}, not 'completed'"
        )
        return errors

    marker, problems = matching_approval(comments, version, tree, package_sha256)
    if marker is None:
        detail = f" ({'; '.join(problems)})" if problems else ""
        errors.append(
            f"No human approval marker for version {version}, tree {tree} on #{READINESS_ISSUE}{detail}"
        )
        return errors

    closed_at = issue.get("closed_at") or ""
    marker_time = max(
        marker.get("_created_at") or "", marker.get("_updated_at") or ""
    )
    if marker_time and closed_at and marker_time >= closed_at:
        errors.append(
            f"The approval marker was posted or edited at or after #{READINESS_ISSUE} closed; record approval before closing it"
        )
    return errors


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--phase", choices=("prepare", "tag", "publish"), required=True)
    parser.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY"))
    parser.add_argument("--version", default="")
    parser.add_argument("--tree", default="")
    parser.add_argument("--package-file", default="")
    args = parser.parse_args(argv)

    if not args.repository:
        parser.error("--repository or GITHUB_REPOSITORY is required")
    repository = args.repository

    version = args.version or json.loads(Path("package.json").read_text())["version"]
    tree = args.tree or subprocess.run(
        ["git", "rev-parse", "HEAD^{tree}"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()
    if not TREE.fullmatch(tree):
        parser.error("--tree must be a 40-character lowercase hex SHA")

    if version != HOLD_VERSION:
        print(
            f"::notice::The 4.0.0 release hold does not apply to version {version}."
        )
        return 0

    package_sha256 = None
    if args.package_file:
        package_path = Path(args.package_file)
        if not package_path.is_file() or package_path.stat().st_size == 0:
            print(
                f"::error::Release blocked: package file {package_path} is missing or empty.",
                file=sys.stderr,
            )
            return 1
        package_sha256 = sha256_file(package_path)
    elif args.phase == "publish":
        parser.error("--package-file is required for publish")

    if args.phase == "prepare":
        issue = None
        comments = []
    else:
        issue = readiness_issue(repository)
        comments = readiness_comments(repository)

    errors = evaluate(
        open_milestone_issues(repository),
        issue,
        comments,
        phase=args.phase,
        version=version,
        tree=tree,
        package_sha256=package_sha256,
    )
    if errors:
        print(f"::error::4.0.0 release hold blocks the {args.phase} phase.", file=sys.stderr)
        for error in errors:
            print(f"::error::{error}", file=sys.stderr)
        print(
            "See docs/ops/npm-release-publishing.md#release-approval for the required order.",
            file=sys.stderr,
        )
        return 1

    print(
        f"Release hold satisfied for the {args.phase} phase "
        f"(version {version}, tree {tree})."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
