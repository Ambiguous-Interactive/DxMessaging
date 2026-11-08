#!/usr/bin/env python3
"""Fail-closed admission checks for one protocol-only #511 native capture."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
from typing import Any
import unicodedata

ROOT = Path(__file__).resolve().parents[2]
SCHEMA = ROOT / ".github" / "perf" / "native-attribution-capture.v1.schema.json"
EXPECTED_SUFFIXES = {
    "player": ".exe",
    "pdb": ".pdb",
    "generatedCppManifest": ".json",
    "cpuProfile": ".json",
    "hostHealth": ".json",
    "wprProfile": ".wprp",
    "trace": ".etl",
    "markerSchema": ".json",
    "markerEvents": ".json",
    "sampledStacks": ".json",
    "mappingTable": ".json",
    "disassembly": ".txt",
    "pmuTable": ".json",
    "reducerOutput": ".json",
}
REQUIRED_TOOLS = {"wpr", "wpa-exporter", "capture-profile", "mapper", "reducer"}


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise ValueError(f'duplicate JSON key "{key}"')
        value[key] = item
    return value


def read_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=reject_duplicate_keys)
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"{path} is not readable strict UTF-8 JSON: {error}") from error
    if not isinstance(value, dict):
        raise ValueError(f"{path} must contain a JSON object")
    return value


def validate_schema(manifest_path: Path, schema_path: Path = SCHEMA) -> None:
    program = """
const fs = require('fs');
const Ajv = require('ajv');
const schema = JSON.parse(fs.readFileSync(process.argv[1], 'utf8'));
const value = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const validate = new Ajv({allErrors: true, strict: false}).compile(schema);
if (!validate(value)) {
  process.stderr.write(JSON.stringify(validate.errors));
  process.exit(1);
}
"""
    result = subprocess.run(
        ["node", "-e", program, str(schema_path), str(manifest_path)],
        cwd=ROOT,
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode != 0:
        raise ValueError(f"schema validation failed: {result.stderr.strip()}")


def require_portable_path(value: str, label: str) -> PurePosixPath:
    if "\\" in value or ":" in value or value.startswith("/"):
        raise ValueError(f"{label} must be a relative POSIX path")
    segments = value.split("/")
    path = PurePosixPath(value)
    if not value or any(part in ("", ".", "..") for part in segments):
        raise ValueError(f"{label} must not contain empty, current, or parent segments")
    if any(
        re.search(r'[<>"|?*]', part)
        or part.endswith((" ", "."))
        or re.match(r"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)", part, re.IGNORECASE)
        for part in segments
    ):
        raise ValueError(f"{label} is not portable to Windows")
    return path


def verify_artifacts(manifest: dict[str, Any], artifact_root: Path | None) -> None:
    seen: set[str] = set()
    folded: set[str] = set()
    for name, expected_suffix in EXPECTED_SUFFIXES.items():
        descriptor = manifest["artifacts"][name]
        relative = str(require_portable_path(descriptor["path"], f"artifacts.{name}.path"))
        if relative in seen:
            raise ValueError(f'artifact path "{relative}" is reused')
        seen.add(relative)
        portable_key = unicodedata.normalize("NFC", relative).casefold()
        if portable_key in folded:
            raise ValueError(f'artifact path "{relative}" collides on a case-insensitive file system')
        folded.add(portable_key)
        if PurePosixPath(relative).suffix.lower() != expected_suffix:
            raise ValueError(f"artifacts.{name}.path must end with {expected_suffix}")
        if artifact_root is None:
            continue
        candidate = artifact_root.joinpath(*PurePosixPath(relative).parts)
        try:
            current = artifact_root
            for part in PurePosixPath(relative).parts:
                current = current / part
                if current.is_symlink():
                    raise ValueError(f"artifact {relative} contains a symbolic-link component")
            stat = candidate.lstat()
        except OSError as error:
            raise ValueError(f"artifact {relative} is unreadable: {error}") from error
        if not candidate.is_file() or candidate.is_symlink() or stat.st_nlink != 1:
            raise ValueError(f"artifact {relative} must be one private regular file")
        data = candidate.read_bytes()
        if len(data) != descriptor["bytes"] or sha256(data) != descriptor["sha256"]:
            raise ValueError(f"artifact {relative} disagrees with its bytes or SHA-256")


def validate_semantics(manifest: dict[str, Any], artifact_root: Path | None = None) -> dict[str, Any]:
    tools = manifest["tools"]
    tool_ids = [item["id"] for item in tools]
    if len(set(tool_ids)) != len(tool_ids) or not REQUIRED_TOOLS.issubset(tool_ids):
        raise ValueError(
            "tools must be unique and include wpr, wpa-exporter, capture-profile, mapper, and reducer"
        )
    verify_artifacts(manifest, artifact_root)

    environment = manifest["environment"]
    artifacts = manifest["artifacts"]
    if artifacts["cpuProfile"]["sha256"] != environment["cpuProfileSha256"]:
        raise ValueError("CPU profile artifact does not match the environment identity")
    if artifacts["hostHealth"]["sha256"] != environment["hostHealthSha256"]:
        raise ValueError("host health artifact does not match the environment identity")
    if artifacts["markerSchema"]["sha256"] != manifest["markers"]["schemaSha256"]:
        raise ValueError("marker schema artifact does not match the marker identity")
    markers = manifest["markers"]["intervals"]
    marker_ids = [item["intervalId"] for item in markers]
    if len(set(marker_ids)) != len(marker_ids):
        raise ValueError("marker interval IDs must be unique")
    ordered = sorted(markers, key=lambda item: item["startQpc"])
    previous_end = -1
    for marker in ordered:
        if marker["processId"] != environment["playerProcessId"]:
            raise ValueError(f"interval {marker['intervalId']} is from another process")
        if marker["threadId"] != environment["mainThreadId"]:
            raise ValueError(f"interval {marker['intervalId']} is not on the player main thread")
        if marker["endQpc"] <= marker["startQpc"]:
            raise ValueError(f"interval {marker['intervalId']} has a nonpositive duration")
        if marker["startQpc"] < previous_end:
            raise ValueError("outer workload intervals must not overlap")
        previous_end = marker["endQpc"]

    attribution = manifest["attribution"]["intervals"]
    attribution_ids = [item["intervalId"] for item in attribution]
    if len(set(attribution_ids)) != len(attribution_ids) or set(attribution_ids) != set(marker_ids):
        raise ValueError("attribution interval IDs must match marker intervals exactly")
    material = manifest["decision"]["materialShareThresholdExclusive"]
    maximum_unresolved = manifest["decision"]["maximumUnresolvedShareInclusive"]
    passing = 0
    for interval in attribution:
        label = f"attribution interval {interval['intervalId']}"
        total = interval["totalSamples"]
        resolved = interval["resolvedSamples"]
        unresolved = interval["unresolvedSamples"]
        if resolved + unresolved != total:
            raise ValueError(f"{label} sample totals do not reconcile")
        centers = interval["costCenters"]
        center_ids = [center["id"] for center in centers]
        if len(set(center_ids)) != len(center_ids):
            raise ValueError(f"{label} cost-center IDs must be unique")
        if sum(center["sampleCount"] for center in centers) != resolved:
            raise ValueError(f"{label} resolved samples do not equal cost-center samples")
        for center in centers:
            source_path = require_portable_path(
                center["generatedSourcePath"], f"{label} generatedSourcePath"
            )
            if source_path.suffix.lower() != ".cpp":
                raise ValueError(f"{label} generated source must be C++")
            if center["nativeEndRva"] <= center["nativeStartRva"]:
                raise ValueError(f"{label} has an invalid native symbol extent")
        try:
            primary = next(
                center for center in centers if center["id"] == interval["primaryCostCenterId"]
            )
        except StopIteration as error:
            raise ValueError(f"{label} primary cost center is absent") from error
        computed_unresolved = unresolved / total
        computed_primary = primary["sampleCount"] / total
        if not math.isclose(interval["unresolvedShare"], computed_unresolved, abs_tol=1e-12):
            raise ValueError(f"{label} unresolved share disagrees with counts")
        if not math.isclose(interval["primaryResolvedShare"], computed_primary, abs_tol=1e-12):
            raise ValueError(f"{label} primary share disagrees with counts")
        computed_pass = computed_primary > material and computed_unresolved <= maximum_unresolved
        if interval["passesSingleTraceScreen"] is not computed_pass:
            raise ValueError(f"{label} single-trace screen verdict disagrees with frozen thresholds")
        passing += int(computed_pass)

    sources = manifest["pmu"]["sources"]
    source_ids = [source["id"] for source in sources]
    if len(set(source_ids)) != len(source_ids):
        raise ValueError("PMU source IDs must be unique")
    for source in sources:
        counts = source["countsByInterval"]
        if source["available"] and set(counts) != set(marker_ids):
            raise ValueError(f"available PMU source {source['id']} must cover every interval")
        if not source["available"] and counts:
            raise ValueError(f"unavailable PMU source {source['id']} must not report counts")
    pmu_by_id = {source["id"]: source for source in sources}
    for name in ("branchControl", "memoryControl"):
        control = manifest["controls"][name]
        source_id = control["counterSource"]
        if control["state"] == "passed":
            if not source_id or source_id not in pmu_by_id or not pmu_by_id[source_id]["available"]:
                raise ValueError(f"{name} passed without an available matching PMU source")
        elif source_id is not None:
            raise ValueError(f"{name} is unavailable but declares a counter source")
    duty_levels = manifest["controls"]["dutyLevels"]
    if len(set(duty_levels)) != len(duty_levels) or duty_levels != sorted(duty_levels):
        raise ValueError("duty levels must be unique and increasing")
    observer = manifest["controls"]["observerEffect"]
    if observer["wprOffBuildId"] == observer["wprOnBuildId"]:
        raise ValueError("WPR observer controls must use different independent builds")

    return {
        "schemaVersion": 1,
        "captureId": manifest["captureId"],
        "intervalCount": len(marker_ids),
        "passingSingleTraceIntervalCount": passing,
        "unavailablePmuSources": sorted(
            source["id"] for source in sources if not source["available"]
        ),
        "state": "protocol-only",
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--artifact-root", type=Path)
    parser.add_argument("--source-commit")
    return parser.parse_args()


def main() -> int:
    arguments = parse_args()
    try:
        validate_schema(arguments.manifest)
        manifest = read_json(arguments.manifest)
        if arguments.source_commit and manifest["source"]["sourceCommit"] != arguments.source_commit:
            raise ValueError("manifest source commit does not match --source-commit")
        summary = validate_semantics(manifest, arguments.artifact_root)
    except ValueError as error:
        print(f"native-attribution capture rejected: {error}", file=sys.stderr)
        return 1
    print(json.dumps(summary, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
