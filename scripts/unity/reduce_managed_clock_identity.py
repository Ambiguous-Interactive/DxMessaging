#!/usr/bin/env python3
"""Pure text replay for the declared managed clock-fault identity observations."""

from __future__ import annotations

import base64
import hashlib
import itertools
import json
import re
import sys
from collections import Counter

from audit_open_loop_trace import GUID, SHA256, fields, identity, parse_json


FILES = {
    "observations.json", "source-bindings.json", "pre-execution-bindings.json",
    "debug-source-binding.json", "observer-debug-binding.json", "observer-source.cs.txt",
    "observer-project.csproj.txt", "execution.log", "scope.json", "observer-source-projection.json",
}
SCOPE = {
    "schemaVersion": 1, "artifactClass": "managed-clock-fault-identity",
    "measurementClass": "recorded-managed-observation", "binariesRetained": False,
    "runtimeSourceSnapshotsRetained": False, "runtimeReexecutedByReplay": False,
    "nativeQualified": False, "previousRowsRequalified": False,
    "clockContract": "UNRESOLVED",
    "observerRedactionReexecutedByReplay": False,
}
ROOT_STRINGS = {
    "runtime", "architecture", "os", "productLocation", "productSha256", "productMvid",
    "observerMvid", "observerSha256", "constructionBoundary", "scope", "clockContract",
}
ROW_STRINGS = {"Kind", "Layout", "Trigger", "ExpectedFaultMessage", "CapturedSite"}
ROW_NULLABLE = {"CapturedType", "CapturedMessage", "CapturedException", "UnexpectedException", "CleanupException"}
ROW_BOOLEANS = {
    "Escape", "CapturedReferenceMatches", "ReplacementTypeAndMessageMatch",
    "ReplacementRejected", "OuterReturned", "CleanupReturned",
}
ROW_TEXT_ARRAYS = {"Trace", "Deliveries", "ObservedOuterDeliveries", "SubsequentDeliveries", "Failures"}
ROW_INT_ARRAYS = {"ClockScopedIds", "InitialCounters", "AfterOuterCounters", "AfterCleanupCounters"}
ROW_INTEGERS = {"ClockActions", "AfterOuterEmissionId"}
DIMENSIONS = (
    ("Untargeted", "Targeted", "Broadcast"), ("EqualPriority", "DistinctPriorities"),
    ("SampledChild", "ForcedTrim"), (False, True),
)
PRODUCT = "WallstopStudios.DxMessaging"
OBSERVER = "DxMessaging.ClockFaultReferenceIdentityObservation"
CLOCK_CONTRACT_RECORD = "Arbitrary getter effects remain contract-unresolved under cheap-clock requirement."


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def integer(value: object) -> bool:
    return type(value) is int and 0 <= value <= 9007199254740991


def digest(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def read(contents: dict[str, bytes], name: str) -> dict:
    result = parse_json(contents[name])
    require(isinstance(result, dict), f"{name} must contain an object")
    return result


def check_hash(value: object, label: str, pattern: re.Pattern = SHA256) -> None:
    require(isinstance(value, str) and pattern.fullmatch(value) is not None, f"invalid {label}")


def validate_projection(contents: dict[str, bytes]) -> dict:
    projection = fields(read(contents, "observer-source-projection.json"), {
        "schemaVersion", "mode", "originalSha256", "originalBytes", "retainedSha256",
        "retainedBytes", "markers", "redactor",
    }, "observer source projection")
    require(type(projection["schemaVersion"]) is int and projection["schemaVersion"] == 1,
            "unknown observer source projection schema")
    require(projection["mode"] in ("exact", "redacted"), "unknown source projection mode")
    for key in ("originalSha256", "retainedSha256"):
        check_hash(projection[key], key)
    for key in ("originalBytes", "retainedBytes"):
        require(integer(projection[key]) and projection[key] > 0, "invalid projected source size")
    retained = contents["observer-source.cs.txt"]
    require(projection["retainedSha256"] == digest(retained) and projection["retainedBytes"] == len(retained),
            "retained observer source projection mismatch")
    markers = dict(Counter(re.findall(r"\[redacted:([a-z0-9-]+)\]", retained.decode("utf-8"))))
    require(isinstance(projection["markers"], dict) and
            all(type(value) is int and value > 0 for value in projection["markers"].values()) and
            projection["markers"] == markers, "source projection marker inventory mismatch")
    if projection["mode"] == "exact":
        require(not markers and projection["redactor"] is None and
                projection["originalSha256"] == projection["retainedSha256"] and
                projection["originalBytes"] == projection["retainedBytes"],
                "exact source projection must retain original bytes without redaction")
    else:
        require(markers and projection["originalSha256"] != projection["retainedSha256"],
                "redacted source projection must expose changed source and markers")
        redactor = fields(projection["redactor"], {"module", "function", "moduleSha256"}, "recorded redactor")
        require(redactor["module"] == "scripts/unity/credential-patterns.js" and
                redactor["function"] == "redactSensitiveData", "unknown recorded source transform")
        check_hash(redactor["moduleSha256"], "recorded redactor module hash")
    return projection


def validate_bindings(contents: dict[str, bytes], source_commit: str, capture: dict) -> dict:
    check_hash(source_commit, "source commit", re.compile(r"(?:[0-9a-f]{40}|[0-9a-f]{64})\Z"))
    scope = read(contents, "scope.json")
    source = read(contents, "source-bindings.json")
    pre = read(contents, "pre-execution-bindings.json")
    fields(source, {"head", "protocolComment", "parentQualification", "parentQualificationSha256", "runtimeSources", "requalifiedCurrentSourceAndGitBlobs", "productPdbMetadataReusedForIdenticalBytes", "previous60RowPacketIntact"}, "source bindings")
    fields(pre, {"head", "protocolComment", "runtimeSources", "files", "buildWarnings", "buildErrors", "formatExplicitFile", "targetExecuted"}, "pre-execution bindings")
    require(source["head"] == pre["head"] == source_commit, "source commit mismatch")
    require(integer(source["protocolComment"]) and source["protocolComment"] > 0, "invalid protocol identity")
    require(type(pre["protocolComment"]) is int and pre["protocolComment"] == source["protocolComment"], "protocol identity mismatch")
    projection = validate_projection(contents)
    expected_scope = {**SCOPE, "sourceCommit": source_commit, "protocolComment": source["protocolComment"],
                      "originalObserverSourceRetained": projection["mode"] == "exact",
                      "observerSourceProjectionMode": projection["mode"]}
    fields(scope, set(expected_scope), "scope")
    require(all(type(scope[key]) is type(value) and scope[key] == value for key, value in expected_scope.items()), "text-only scope descriptor mismatch")
    require(all(source[key] is True for key in ("requalifiedCurrentSourceAndGitBlobs", "productPdbMetadataReusedForIdenticalBytes", "previous60RowPacketIntact")), "source binding is not qualified")
    require(pre["buildWarnings"] == 0 and type(pre["buildWarnings"]) is int and pre["buildErrors"] == 0 and type(pre["buildErrors"]) is int and pre["formatExplicitFile"] == "PASS" and pre["targetExecuted"] is False, "invalid pre-execution qualification")
    identity(source["parentQualification"], "parent qualification")
    check_hash(source["parentQualificationSha256"], "parent qualification hash")
    roster = source["runtimeSources"]
    require(isinstance(roster, list) and roster and integer(pre["runtimeSources"]) and len(roster) == pre["runtimeSources"], "source document count mismatch")
    sources = {}
    for entry in roster:
        fields(entry, {"path", "sha256", "gitBlob"}, "source entry")
        name = identity(entry["path"], "source path")
        require(name.startswith("Runtime/") and name.endswith(".cs") and all(part not in ("", ".", "..") for part in name.split("/")) and "\\" not in name and ":" not in name and name not in sources, "invalid or duplicate source path")
        check_hash(entry["sha256"], "source hash")
        check_hash(entry["gitBlob"], "source blob", re.compile(r"(?:[0-9a-f]{40}|[0-9a-f]{64})\Z"))
        sources[name] = entry["sha256"]
    require(isinstance(pre["files"], list), "input roster must be an array")
    inputs = {}
    for entry in pre["files"]:
        fields(entry, {"path", "sha256", "bytes"}, "input entry")
        name = identity(entry["path"], "input path")
        require(name not in inputs and integer(entry["bytes"]) and entry["bytes"] > 0, "duplicate or invalid input")
        check_hash(entry["sha256"], "input hash")
        inputs[name] = entry
    build = "observation/bin/Release/net10.0/"
    expected = {"observation/Program.cs", "observation/Observation.csproj"}
    expected.update(f"{prefix}{name}.{extension}" for prefix, name in (("retained-build/", PRODUCT), (build, PRODUCT), (build, OBSERVER)) for extension in ("dll", "pdb"))
    require(set(inputs) == expected, "frozen input roster mismatch")
    require(inputs["observation/Program.cs"]["sha256"] == projection["originalSha256"] and
            inputs["observation/Program.cs"]["bytes"] == projection["originalBytes"],
            "original observer source/frozen input mismatch")
    project = contents["observer-project.csproj.txt"]
    require(inputs["observation/Observation.csproj"]["sha256"] == digest(project) and
            inputs["observation/Observation.csproj"]["bytes"] == len(project), "observer project hash mismatch")
    for extension in ("dll", "pdb"):
        require(inputs[f"retained-build/{PRODUCT}.{extension}"] == {**inputs[f"{build}{PRODUCT}.{extension}"], "path": f"retained-build/{PRODUCT}.{extension}"}, "copied product identity mismatch")
    require(capture["productSha256"] == inputs[f"retained-build/{PRODUCT}.dll"]["sha256"] and capture["observerSha256"] == inputs[f"{build}{OBSERVER}.dll"]["sha256"], "loaded module hash mismatch")
    for filename, mvid in (("debug-source-binding.json", "productMvid"), ("observer-debug-binding.json", "observerMvid")):
        debug = fields(read(contents, filename), {"AssemblyPdbPairMatches", "CodeViewGuid", "Stamp", "Age", "AssemblyMvid", "Documents", "InspectedAssemblyExecuted"}, filename)
        for key in ("CodeViewGuid", "AssemblyMvid"):
            check_hash(debug[key], key, GUID)
        require(debug["AssemblyMvid"] == capture[mvid] and debug["AssemblyPdbPairMatches"] is True and integer(debug["Stamp"]) and debug["Stamp"] <= 4294967295 and type(debug["Age"]) is int and debug["Age"] == 1 and debug["InspectedAssemblyExecuted"] is False, "recorded DLL/PDB pairing mismatch")
        require(isinstance(debug["Documents"], list), "debug documents must be an array")
        documents = {}
        for entry in debug["Documents"]:
            fields(entry, {"Document", "Sha256", "CurrentSourceMatches"} | ({"Relative"} if mvid == "productMvid" else set()), "debug document")
            identity(entry["Document"], "document path")
            name = entry["Relative"] if mvid == "productMvid" else "observation/Program.cs"
            require(name not in documents and entry["CurrentSourceMatches"] is True, "duplicate or unmatched debug document")
            require(entry["Document"].replace("\\", "/").endswith("/" + name), "debug document path mismatch")
            documents[name] = entry["Sha256"]
        wanted = sources if mvid == "productMvid" else {"observation/Program.cs": projection["originalSha256"]}
        require(documents == wanted, "debug/source checksums mismatch")
    return scope


def reduce_row(row: dict) -> dict:
    fields(row, ROW_STRINGS | ROW_NULLABLE | ROW_BOOLEANS | ROW_TEXT_ARRAYS | ROW_INT_ARRAYS | ROW_INTEGERS, "observation row")
    require(all(isinstance(row[key], str) for key in ROW_STRINGS), "invalid row string")
    require(all(row[key] is None or isinstance(row[key], str) for key in ROW_NULLABLE), "invalid nullable diagnostic")
    require(all(type(row[key]) is bool for key in ROW_BOOLEANS), "invalid row boolean")
    require(all(isinstance(row[key], list) and all(isinstance(value, str) for value in row[key]) for key in ROW_TEXT_ARRAYS), "invalid row text array")
    require(all(isinstance(row[key], list) and all(integer(value) for value in row[key]) for key in ROW_INT_ARRAYS) and all(integer(row[key]) for key in ROW_INTEGERS), "invalid row counter")
    key = (row["Kind"], row["Layout"], row["Trigger"], row["Escape"])
    require(all(value in options for value, options in zip(key, DIMENSIONS)), "unknown case dimension")
    expected_fault = f"ClockFault:{key[0]}/{key[1]}/{key[2]}/Escape{str(key[3])}"
    expected_outer = ["outer:A:15"] + ([] if key[3] else ["outer:B:15", "outer:C:15"])
    expected_next = [f"subsequent:{slot}:16" for slot in "ABC"]
    counters = [0] * 6
    counters[DIMENSIONS[0].index(key[0])] = 1 if key[1] == "EqualPriority" else 3
    checks = {
        "exception-reference": row["CapturedReferenceMatches"],
        "replacement-identity-control": row["ReplacementTypeAndMessageMatch"] and row["ReplacementRejected"],
        "capture-site-and-message": row["CapturedSite"] == ("outer" if key[3] else "local") and row["CapturedType"] == "Program+ClockFault" and row["ExpectedFaultMessage"] == row["CapturedMessage"] == expected_fault and isinstance(row["CapturedException"], str) and row["CapturedException"].startswith("Program+ClockFault: " + expected_fault),
        "active-outer-clock": row["ClockActions"] == 1 and row["ClockScopedIds"] == [15],
        "outer-return": row["OuterReturned"] == (not key[3]),
        "outer-callback-order": row["ObservedOuterDeliveries"] == expected_outer,
        "subsequent-callback-order": row["SubsequentDeliveries"] == expected_next,
        "complete-delivery-record": row["Deliveries"] == expected_outer + expected_next,
        "receive-trace": [value[8:] for value in row["Trace"] if value.startswith("receive:")] == row["Deliveries"],
        "clock-read-trace": [value for value in row["Trace"] if value.startswith("clock-read:")] == [f"clock-read:{index}" for index in range(2, 6)],
        "fault-trace": row["Trace"].count("throw-precreated-sentinel") == 1 and [value for value in row["Trace"] if value.startswith(("local-capture:", "outer-capture:"))] == [("outer" if key[3] else "local") + "-capture:ClockFault"],
        "child-emission-id": row["AfterOuterEmissionId"] == 15,
        "registration-ownership": row["InitialCounters"] == row["AfterOuterCounters"] == counters,
        "cleanup": row["CleanupReturned"] and row["AfterCleanupCounters"] == [0] * 6,
        "reported-diagnostics": not row["Failures"] and row["UnexpectedException"] is None and row["CleanupException"] is None,
    }
    return {"recorded": row, "violations": sorted(name for name, passed in checks.items() if not passed)}


def reduce_contents(contents: dict[str, bytes], source_commit: str) -> dict:
    require(set(contents) == FILES and all(isinstance(raw, bytes) for raw in contents.values()), "managed clock bundle requires exactly ten text inputs")
    capture = fields(read(contents, "observations.json"), ROOT_STRINGS | {"rows"}, "capture")
    for key in ROOT_STRINGS:
        identity(capture[key], key)
    require(capture["clockContract"] == CLOCK_CONTRACT_RECORD, "recorded clock contract mismatch")
    scope = validate_bindings(contents, source_commit, capture)
    require(isinstance(capture["rows"], list), "rows must be an array")
    rows = [reduce_row(row) for row in capture["rows"]]
    keys = [tuple(row["recorded"][name] for name in ("Kind", "Layout", "Trigger", "Escape")) for row in rows]
    require(len(keys) == len(set(keys)) and set(keys) == set(itertools.product(*DIMENSIONS)), "missing or duplicate case coverage")
    rows = [row for _, row in sorted(zip(keys, rows), key=lambda pair: pair[0])]
    reported = sum(len(row["recorded"]["Failures"]) for row in rows)
    expected_log = f"Observed 24 fixed fault-reference rows and 24 local identity controls; failed declared checks: {reported}.\n"
    require(contents["execution.log"].decode("utf-8").replace("\r\n", "\n") == expected_log, "execution log disagrees with reported rows")
    violations = sum(len(row["violations"]) for row in rows)
    return {
        "schemaVersion": 1, "reducer": "managed-clock-fault-identity-v1", "sourceCommit": source_commit,
        "scope": scope, "recordedEnvironment": {key: capture[key] for key in sorted(ROOT_STRINGS)},
        "observerSourceProjection": read(contents, "observer-source-projection.json"),
        "inputSha256": {name: digest(raw) for name, raw in sorted(contents.items())},
        "verdict": "MATCHED" if violations == 0 else "ADVERSE", "derivedViolationCount": violations,
        "reportedFailureCount": reported, "exactReferencesRecorded": sum(row["recorded"]["CapturedReferenceMatches"] for row in rows),
        "negativeControlsRejected": sum(row["recorded"]["ReplacementRejected"] and row["recorded"]["ReplacementTypeAndMessageMatch"] for row in rows),
        "rows": rows,
    }


def main() -> None:
    require(sys.argv[1:] == ["--bundle-stdin"], "this reducer accepts only --bundle-stdin")
    request = fields(parse_json(sys.stdin.buffer.read()), {"sourceCommit", "contents"}, "bundle request")
    require(isinstance(request["contents"], dict), "contents must be an object")
    contents = {name: base64.b64decode(raw, validate=True) for name, raw in request["contents"].items()}
    print(json.dumps(reduce_contents(contents, request["sourceCommit"]), sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, UnicodeError) as error:
        print(f"managed clock evidence error: {error}", file=sys.stderr)
        raise SystemExit(1) from None
