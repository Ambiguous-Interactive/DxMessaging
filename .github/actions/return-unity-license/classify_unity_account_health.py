#!/usr/bin/env python3
"""Classify bounded Unity account-health evidence without printing log text."""

from __future__ import annotations

import hashlib
import os
import re
import sys
import tempfile
from pathlib import Path


MAX_EVIDENCE_BYTES = 25 * 1024 * 1024
EVIDENCE_EXTENSIONS = {".log", ".txt"}
ACCOUNT_BLOCKED_PATTERN = re.compile(rb"(?:^|[^0-9])20111(?:$|[^0-9])")


def collect_evidence_files(candidate_paths: list[str]) -> list[Path]:
    """Collect bounded regular evidence files without traversing symlinks."""

    files: set[Path] = set()

    def visit(candidate: Path) -> None:
        try:
            if candidate.is_symlink():
                return
            if candidate.is_file():
                size = candidate.stat().st_size
                if size <= MAX_EVIDENCE_BYTES and candidate.suffix.lower() in EVIDENCE_EXTENSIONS:
                    files.add(candidate.resolve())
                return
            if not candidate.is_dir():
                return
            for child in candidate.iterdir():
                visit(child)
        except OSError:
            print("::warning::Could not inspect a sanitized Unity evidence path; account health remains healthy.")

    for candidate in candidate_paths:
        visit(Path(candidate))
    return sorted(files)


def classify(files: list[Path]) -> tuple[str, str, str]:
    """Return health, reason, and a digest without exposing evidence content."""

    digest = hashlib.sha256()
    account_blocked = False
    for evidence_file in files:
        try:
            data = evidence_file.read_bytes()
            digest.update(evidence_file.name.encode("utf-8"))
            digest.update(b"\0")
            digest.update(data)
            if ACCOUNT_BLOCKED_PATTERN.search(data):
                account_blocked = True
        except OSError:
            print("::warning::Could not inspect a sanitized Unity evidence file; account health remains unchanged.")

    if account_blocked:
        return "blocked", "unity-account-limit-20111", digest.hexdigest()
    return "healthy", os.environ.get("HEALTHY_REASON", "return-missing-positive-evidence"), digest.hexdigest()


def main() -> None:
    output_path = os.environ.get("GITHUB_OUTPUT", "").strip()
    if not output_path:
        raise RuntimeError("GitHub output path is required.")

    candidates = [line.strip() for line in os.environ.get("EVIDENCE_PATHS", "").splitlines() if line.strip()]
    health, reason, evidence_digest = classify(collect_evidence_files(candidates))
    with Path(output_path).open("a", encoding="utf-8", newline="\n") as output:
        output.write(f"resource-health={health}\nresource-reason={reason}\nevidence-digest={evidence_digest}\n")

    print(f"::notice::Unity account-health classification reason={reason} evidence-digest={evidence_digest}")
    if health == "blocked":
        print("::error title=Unity account blocked::Observed unity-account-limit-20111; central admission will stop when schema 5 is active.")


def self_test() -> None:
    fixtures = [
        ("Licensing failed with error code 20111\n", "blocked", "unity-account-limit-20111"),
        ("[Licensing] Error [20111]: activation limit reached\n", "blocked", "unity-account-limit-20111"),
        ("Licensing failed with error code 20113\n", "healthy", "return-missing-positive-evidence"),
        ("Licensing failed with error code 400006\n", "healthy", "return-missing-positive-evidence"),
        ("Diagnostic identifier 1201119\n", "healthy", "return-missing-positive-evidence"),
    ]
    os.environ["HEALTHY_REASON"] = "return-missing-positive-evidence"
    with tempfile.TemporaryDirectory(prefix="dxm-unity-health-") as temp_root:
        evidence = Path(temp_root) / "unity.log"
        for log_text, expected_health, expected_reason in fixtures:
            evidence.write_text(log_text, encoding="utf-8")
            health, reason, evidence_digest = classify([evidence])
            if health != expected_health or reason != expected_reason or len(evidence_digest) != 64:
                raise AssertionError(f"Unexpected classification: {health}/{reason}/{evidence_digest}")
    print("Unity account-health classifier self-test passed.")


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    elif sys.argv[1:]:
        raise SystemExit("Usage: classify_unity_account_health.py [--self-test]")
    else:
        main()
