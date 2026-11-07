import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "unity" / "validate_native_attribution_capture.py"
SCHEMA = ROOT / ".github" / "perf" / "native-attribution-capture.v1.schema.json"
SPEC = importlib.util.spec_from_file_location("validate_native_attribution_capture", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)

HASH = "a" * 64
COMMIT = "b" * 40
TREE = "c" * 40


def artifact(path: str) -> dict:
    return {"path": path, "bytes": 1, "sha256": HASH}


def complete_manifest() -> dict:
    return {
        "schemaVersion": 1,
        "measurementClass": "native-attribution-capture",
        "captureId": "capture-001",
        "source": {
            "sourceCommit": COMMIT,
            "sourceTree": TREE,
            "unityVersion": "6000.5.2f1",
            "scriptingBackend": "IL2CPP",
            "platform": "WindowsPlayer",
            "architecture": "x86_64",
            "cleanBuild": True,
            "buildInvocationId": "build-001",
        },
        "environment": {
            "cpuProfileSha256": HASH,
            "hostHealthSha256": HASH,
            "playerProcessId": 100,
            "mainThreadId": 7,
        },
        "tools": [
            {"id": name, "version": "1", "sha256": HASH}
            for name in ("wpr", "wpa-exporter", "capture-profile", "mapper", "reducer")
        ],
        "commands": {"capture": "wpr capture", "analysis": "reducer analyze"},
        "artifacts": {
            "player": artifact("raw/player.exe"),
            "pdb": artifact("raw/player.pdb"),
            "generatedCppManifest": artifact("raw/generated-cpp.json"),
            "cpuProfile": artifact("raw/cpu-profile.json"),
            "hostHealth": artifact("raw/host-health.json"),
            "wprProfile": artifact("raw/profile.wprp"),
            "trace": artifact("raw/capture.etl"),
            "markerSchema": artifact("raw/marker-schema.json"),
            "markerEvents": artifact("derived/markers.json"),
            "sampledStacks": artifact("derived/stacks.json"),
            "mappingTable": artifact("derived/mapping.json"),
            "disassembly": artifact("derived/disassembly.txt"),
            "pmuTable": artifact("derived/pmu.json"),
            "reducerOutput": artifact("derived/result.json"),
        },
        "markers": {
            "mode": "outer-workload-only",
            "providerId": "dxm-provider-v1",
            "schemaSha256": HASH,
            "qpcFrequency": 10_000_000,
            "intervals": [
                {
                    "intervalId": "global-to-one-01",
                    "workloadId": "GlobalToOne",
                    "processId": 100,
                    "threadId": 7,
                    "startQpc": 1000,
                    "endQpc": 2000,
                }
            ],
        },
        "attribution": {
            "intervals": [
                {
                    "intervalId": "global-to-one-01",
                    "totalSamples": 1000,
                    "resolvedSamples": 995,
                    "unresolvedSamples": 5,
                    "unresolvedShare": 0.005,
                    "primaryCostCenterId": "dispatch",
                    "primaryResolvedShare": 0.04,
                    "passesSingleTraceScreen": True,
                    "costCenters": [
                        {
                            "id": "dispatch",
                            "sampleCount": 40,
                            "nativeSymbol": "MessageBus_Dispatch",
                            "nativeStartRva": 100,
                            "nativeEndRva": 200,
                            "generatedSourcePath": "cpp/GenericMethods.cpp",
                            "generatedLine": 1000,
                            "mappingStatus": "exact",
                        },
                        {
                            "id": "other-resolved",
                            "sampleCount": 955,
                            "nativeSymbol": "Other_Resolved",
                            "nativeStartRva": 200,
                            "nativeEndRva": 400,
                            "generatedSourcePath": "cpp/Other.cpp",
                            "generatedLine": 20,
                            "mappingStatus": "exact",
                        },
                    ],
                }
            ]
        },
        "pmu": {
            "sources": [
                {
                    "id": "branch-mispredicts",
                    "available": True,
                    "countsByInterval": {"global-to-one-01": 10},
                },
                {"id": "llc-misses", "available": False, "countsByInterval": {}},
            ]
        },
        "controls": {
            "pdbMissingRejected": True,
            "pdbMismatchRejected": True,
            "dutyLevels": [0, 1, 4],
            "branchControl": {"state": "passed", "counterSource": "branch-mispredicts"},
            "memoryControl": {"state": "unavailable", "counterSource": None},
            "observerEffect": {
                "wprOffBuildId": "build-off",
                "wprOnBuildId": "build-on",
                "independentBuilds": True,
                "effectMeasured": True,
            },
        },
        "decision": {
            "materialShareThresholdExclusive": 0.03,
            "maximumUnresolvedShareInclusive": 0.01,
            "state": "protocol-only",
        },
    }


class NativeAttributionCaptureTests(unittest.TestCase):
    def schema_result(self, manifest: dict) -> Exception | None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.json"
            path.write_text(json.dumps(manifest), encoding="utf-8")
            try:
                MODULE.validate_schema(path)
            except ValueError as error:
                return error
        return None

    def test_complete_protocol_unit_is_admitted(self) -> None:
        manifest = complete_manifest()
        self.assertIsNone(self.schema_result(manifest))
        self.assertEqual(
            MODULE.validate_semantics(manifest),
            {
                "schemaVersion": 1,
                "captureId": "capture-001",
                "intervalCount": 1,
                "passingSingleTraceIntervalCount": 1,
                "unavailablePmuSources": ["llc-misses"],
                "state": "protocol-only",
            },
        )

    def test_schema_rejects_dirty_build_bad_hash_per_emit_and_missing_mapping_controls(self) -> None:
        for label, mutate in (
            ("dirty", lambda value: value["source"].update(cleanBuild=False)),
            ("hash", lambda value: value["artifacts"]["trace"].update(sha256="bad")),
            ("markers", lambda value: value["markers"].update(mode="per-emit")),
            ("pdb control", lambda value: value["controls"].update(pdbMismatchRejected=False)),
        ):
            with self.subTest(label=label):
                manifest = complete_manifest()
                mutate(manifest)
                self.assertIsNotNone(self.schema_result(manifest))

    def test_semantics_reject_identity_interval_and_sample_drift(self) -> None:
        cases = (
            ("path", lambda value: value["artifacts"]["trace"].update(path="../capture.etl"), "parent segments"),
            ("process", lambda value: value["markers"]["intervals"][0].update(processId=101), "another process"),
            ("thread", lambda value: value["markers"]["intervals"][0].update(threadId=8), "main thread"),
            ("CPU identity", lambda value: value["environment"].update(cpuProfileSha256="d" * 64), "CPU profile"),
            ("marker identity", lambda value: value["markers"].update(schemaSha256="d" * 64), "marker schema"),
            ("arithmetic", lambda value: value["attribution"]["intervals"][0].update(unresolvedSamples=6), "do not reconcile"),
            ("share", lambda value: value["attribution"]["intervals"][0].update(primaryResolvedShare=0.05), "primary share"),
            ("verdict", lambda value: value["attribution"]["intervals"][0].update(passesSingleTraceScreen=False), "verdict"),
            ("observer", lambda value: value["controls"]["observerEffect"].update(wprOnBuildId="build-off"), "different independent"),
        )
        for label, mutate, pattern in cases:
            with self.subTest(label=label):
                manifest = complete_manifest()
                mutate(manifest)
                with self.assertRaisesRegex(ValueError, pattern):
                    MODULE.validate_semantics(manifest)

    def test_pmu_availability_cannot_be_silently_treated_as_zero(self) -> None:
        manifest = complete_manifest()
        manifest["pmu"]["sources"][1]["countsByInterval"] = {"global-to-one-01": 0}
        self.assertIsNotNone(self.schema_result(manifest))
        with self.assertRaisesRegex(ValueError, "must not report counts"):
            MODULE.validate_semantics(manifest)

    def test_available_pmu_and_passed_controls_require_matching_sources(self) -> None:
        manifest = complete_manifest()
        manifest["pmu"]["sources"][0]["countsByInterval"] = {}
        with self.assertRaisesRegex(ValueError, "cover every interval"):
            MODULE.validate_semantics(manifest)
        manifest = complete_manifest()
        manifest["controls"]["branchControl"]["counterSource"] = "unknown-counter"
        with self.assertRaisesRegex(ValueError, "available matching PMU"):
            MODULE.validate_semantics(manifest)

    def test_artifact_bytes_are_checked_when_root_is_supplied(self) -> None:
        manifest = complete_manifest()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for descriptor in manifest["artifacts"].values():
                path = root / descriptor["path"]
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"x")
                descriptor.update(bytes=1, sha256=hashlib.sha256(b"x").hexdigest())
            manifest["environment"]["cpuProfileSha256"] = manifest["artifacts"]["cpuProfile"][
                "sha256"
            ]
            manifest["environment"]["hostHealthSha256"] = manifest["artifacts"]["hostHealth"][
                "sha256"
            ]
            manifest["markers"]["schemaSha256"] = manifest["artifacts"]["markerSchema"][
                "sha256"
            ]
            MODULE.validate_semantics(manifest, root)
            (root / manifest["artifacts"]["trace"]["path"]).write_bytes(b"y")
            with self.assertRaisesRegex(ValueError, "disagrees with its bytes"):
                MODULE.validate_semantics(manifest, root)

    def test_artifact_paths_reject_case_collisions_and_symbolic_link_components(self) -> None:
        manifest = complete_manifest()
        manifest["artifacts"]["sampledStacks"]["path"] = "derived/MARKERS.json"
        with self.assertRaisesRegex(ValueError, "case-insensitive"):
            MODULE.validate_semantics(manifest)

        manifest = complete_manifest()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            target = root / "actual-raw"
            target.mkdir()
            (root / "raw").symlink_to(target, target_is_directory=True)
            (root / "derived").mkdir()
            for descriptor in manifest["artifacts"].values():
                path = root / descriptor["path"]
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"x")
                descriptor.update(bytes=1, sha256=hashlib.sha256(b"x").hexdigest())
            manifest["environment"]["cpuProfileSha256"] = manifest["artifacts"]["cpuProfile"][
                "sha256"
            ]
            manifest["environment"]["hostHealthSha256"] = manifest["artifacts"]["hostHealth"][
                "sha256"
            ]
            manifest["markers"]["schemaSha256"] = manifest["artifacts"]["markerSchema"][
                "sha256"
            ]
            with self.assertRaisesRegex(ValueError, "symbolic-link component"):
                MODULE.validate_semantics(manifest, root)

    def test_schema_hash_and_frozen_thresholds_are_reviewed(self) -> None:
        self.assertEqual(
            hashlib.sha256(SCHEMA.read_bytes()).hexdigest(),
            "62d1abab4f62216f52f9db8e5ac3cab06450a8bb794aaea2e1edafffb2c52e6c",
        )
        manifest = complete_manifest()
        self.assertEqual(manifest["decision"]["materialShareThresholdExclusive"], 0.03)
        self.assertEqual(manifest["decision"]["maximumUnresolvedShareInclusive"], 0.01)
        self.assertEqual(manifest["decision"]["state"], "protocol-only")


if __name__ == "__main__":
    unittest.main()
