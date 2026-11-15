"""Exercise the maintained text reducer and actual bundle CLI, without running a product."""

import copy
import hashlib
import importlib
import itertools
import json
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS / "unity"))
REDUCER = importlib.import_module("reduce_managed_clock_identity")
BUNDLE = SCRIPTS / "unity" / "perf-evidence-bundle.js"
COMMIT = "a" * 40
PRODUCT = "WallstopStudios.DxMessaging"
OBSERVER = "DxMessaging.ClockFaultReferenceIdentityObservation"


def encoded(value):
    return (json.dumps(value) + "\n").encode()


def fixture():
    """Synthetic input data, including complete independent case identities."""
    source = b"synthetic observer source\n"
    project = b"synthetic observer project\n"
    hashes = {"product": "1" * 64, "pdb": "2" * 64, "observer": "3" * 64, "observerPdb": "4" * 64}
    product_id = "11111111-1111-1111-1111-111111111111"
    observer_id = "22222222-2222-2222-2222-222222222222"
    rows = []
    for kind, layout, trigger, escape in itertools.product(
        ("Untargeted", "Targeted", "Broadcast"), ("EqualPriority", "DistinctPriorities"),
        ("SampledChild", "ForcedTrim"), (False, True),
    ):
        outer = ["outer:A:15"] + ([] if escape else ["outer:B:15", "outer:C:15"])
        subsequent = [f"subsequent:{slot}:16" for slot in "ABC"]
        counts = [0] * 6
        counts[("Untargeted", "Targeted", "Broadcast").index(kind)] = 1 if layout == "EqualPriority" else 3
        marker = f"ClockFault:{kind}/{layout}/{trigger}/Escape{escape}"
        site = "outer" if escape else "local"
        rows.append({
            "Kind": kind, "Layout": layout, "Trigger": trigger, "Escape": escape,
            "ExpectedFaultMessage": marker, "CapturedType": "Program+ClockFault",
            "CapturedMessage": marker, "CapturedException": "Program+ClockFault: " + marker,
            "CapturedSite": site, "CapturedReferenceMatches": True,
            "ReplacementTypeAndMessageMatch": True, "ReplacementRejected": True,
            "OuterReturned": not escape, "ClockActions": 1, "ClockScopedIds": [15],
            "Trace": ["receive:outer:A:15", "clock-read:2", "throw-precreated-sentinel", site + "-capture:ClockFault"]
                + ["receive:" + value for value in outer[1:]] + ["clock-read:3", "clock-read:4"]
                + ["receive:" + value for value in subsequent] + ["clock-read:5"],
            "Deliveries": outer + subsequent, "ObservedOuterDeliveries": outer,
            "SubsequentDeliveries": subsequent, "Failures": [], "AfterOuterEmissionId": 15,
            "InitialCounters": counts, "AfterOuterCounters": counts[:], "AfterCleanupCounters": [0] * 6,
            "CleanupReturned": True, "UnexpectedException": None, "CleanupException": None,
        })
    inputs = []
    for name, raw in (("observation/Program.cs", source), ("observation/Observation.csproj", project)):
        inputs.append({"path": name, "sha256": hashlib.sha256(raw).hexdigest(), "bytes": len(raw)})
    for prefix, name, dll, pdb in (
        ("retained-build/", PRODUCT, hashes["product"], hashes["pdb"]),
        ("observation/bin/Release/net10.0/", PRODUCT, hashes["product"], hashes["pdb"]),
        ("observation/bin/Release/net10.0/", OBSERVER, hashes["observer"], hashes["observerPdb"]),
    ):
        for extension, digest in (("dll", dll), ("pdb", pdb)):
            inputs.append({"path": prefix + name + "." + extension, "sha256": digest, "bytes": 10})
    source_path = "Runtime/Core/Probe.cs"
    product_debug = {
        "AssemblyPdbPairMatches": True, "CodeViewGuid": product_id, "Stamp": 123,
        "Age": 1, "AssemblyMvid": product_id, "InspectedAssemblyExecuted": False,
        "Documents": [{"Document": "/synthetic/" + source_path, "Relative": source_path, "Sha256": "b" * 64, "CurrentSourceMatches": True}],
    }
    observer_debug = {**product_debug, "CodeViewGuid": observer_id, "AssemblyMvid": observer_id,
        "Documents": [{"Document": "/synthetic/observation/Program.cs", "Sha256": hashlib.sha256(source).hexdigest(), "CurrentSourceMatches": True}]}
    objects = {
        "observations.json": {
            "runtime": ".NET 10.0.15", "architecture": "Arm64", "os": "Synthetic Linux",
            "productLocation": "/synthetic/product.dll", "productSha256": hashes["product"],
            "productMvid": product_id, "observerMvid": observer_id, "observerSha256": hashes["observer"],
            "constructionBoundary": "synthetic recorded boundary", "scope": "synthetic text fixture",
            "clockContract": "Arbitrary getter effects remain contract-unresolved under cheap-clock requirement.", "rows": rows,
        },
        "source-bindings.json": {
            "head": COMMIT, "protocolComment": 123, "parentQualification": "local/qualification.json",
            "parentQualificationSha256": "c" * 64, "runtimeSources": [{"path": source_path, "sha256": "b" * 64, "gitBlob": "d" * 40}],
            "requalifiedCurrentSourceAndGitBlobs": True, "productPdbMetadataReusedForIdenticalBytes": True,
            "previous60RowPacketIntact": True,
        },
        "pre-execution-bindings.json": {
            "head": COMMIT, "protocolComment": 123, "runtimeSources": 1, "files": inputs,
            "buildWarnings": 0, "buildErrors": 0, "formatExplicitFile": "PASS", "targetExecuted": False,
        },
        "debug-source-binding.json": product_debug, "observer-debug-binding.json": observer_debug,
        "scope.json": {
            "schemaVersion": 1, "artifactClass": "managed-clock-fault-identity", "sourceCommit": COMMIT,
            "protocolComment": 123, "measurementClass": "recorded-managed-observation",
            "binariesRetained": False, "runtimeSourceSnapshotsRetained": False, "runtimeReexecutedByReplay": False,
            "nativeQualified": False, "previousRowsRequalified": False, "clockContract": "UNRESOLVED",
        },
    }
    return project_source({**{name: encoded(value) for name, value in objects.items()},
        "observer-source.cs.txt": source, "observer-project.csproj.txt": project,
        "execution.log": b"Observed 24 fixed fault-reference rows and 24 local identity controls; failed declared checks: 0.\n"})


def mutate(contents, name, change):
    value = json.loads(contents[name])
    change(value)
    contents[name] = encoded(value)


def project_source(contents, original=None):
    """Retain either exact text or actual standard-redactor output with provenance."""
    source = contents["observer-source.cs.txt"]
    original = source if original is None else original
    markers = {}
    if original != source:
        for marker in re.findall(r"\[redacted:([a-z0-9-]+)\]", source.decode()):
            markers[marker] = markers.get(marker, 0) + 1
    contents["observer-source-projection.json"] = encoded({
        "schemaVersion": 1, "mode": "redacted" if original != source else "exact",
        "originalSha256": hashlib.sha256(original).hexdigest(), "originalBytes": len(original),
        "retainedSha256": hashlib.sha256(source).hexdigest(), "retainedBytes": len(source),
        "markers": markers,
        "redactor": {"module": "scripts/unity/credential-patterns.js", "function": "redactSensitiveData",
            "moduleSha256": hashlib.sha256((SCRIPTS / "unity/credential-patterns.js").read_bytes()).hexdigest()}
            if original != source else None,
    })
    mutate(contents, "scope.json", lambda scope: scope.update(
        originalObserverSourceRetained=original == source,
        observerSourceProjectionMode="redacted" if original != source else "exact",
        observerRedactionReexecutedByReplay=False))
    return contents


def redacted_fixture():
    contents = fixture()
    original = b"MessageRegistrationToken token = CreateToken();\n"
    script = "const c=require(process.argv[1]);process.stdout.write(c.redactSensitiveData(require('fs').readFileSync(0,'utf8')).redacted)"
    result = subprocess.run(["node", "-e", script, str(SCRIPTS / "unity/credential-patterns.js")],
        input=original, capture_output=True, check=True)
    contents["observer-source.cs.txt"] = result.stdout
    mutate(contents, "pre-execution-bindings.json", lambda raw: raw["files"][0].update(
        sha256=hashlib.sha256(original).hexdigest(), bytes=len(original)))
    mutate(contents, "observer-debug-binding.json", lambda raw: raw["Documents"][0].update(
        Sha256=hashlib.sha256(original).hexdigest()))
    return project_source(contents, original)


class ManagedClockEvidenceTests(unittest.TestCase):
    def test_false_exact_projection_is_rejected_even_when_scope_claims_exact(self):
        contents = redacted_fixture()
        mutate(contents, "observer-source-projection.json", lambda raw: raw.update(mode="exact", redactor=None))
        mutate(contents, "scope.json", lambda raw: raw.update(originalObserverSourceRetained=True, observerSourceProjectionMode="exact"))
        with self.assertRaisesRegex(ValueError, "exact source projection"):
            REDUCER.reduce_contents(contents, COMMIT)

    def test_capture_cannot_override_the_unresolved_clock_contract(self):
        contents = fixture()
        mutate(contents, "observations.json", lambda raw: raw.update(clockContract="RESOLVED"))
        with self.assertRaisesRegex(ValueError, "clock contract"):
            REDUCER.reduce_contents(contents, COMMIT)

    def test_projection_retains_standard_redactor_output_with_explicit_limits(self):
        contents = redacted_fixture()
        result = REDUCER.reduce_contents(contents, COMMIT)
        self.assertEqual(result["verdict"], "MATCHED")
        self.assertEqual(result["observerSourceProjection"], json.loads(contents["observer-source-projection.json"]))
        self.assertFalse(result["scope"]["originalObserverSourceRetained"])
        self.assertEqual(result["scope"]["observerSourceProjectionMode"], "redacted")
        self.assertFalse(result["scope"]["observerRedactionReexecutedByReplay"])
        exact = REDUCER.reduce_contents(project_source(fixture()), COMMIT)
        self.assertTrue(exact["scope"]["originalObserverSourceRetained"])

    def test_projection_rejects_drift_false_exact_claims_and_hidden_limits(self):
        changes = (
            lambda data: mutate(data, "observer-source-projection.json", lambda raw: raw.update(retainedSha256="f" * 64)),
            lambda data: mutate(data, "observer-source-projection.json", lambda raw: raw.update(originalSha256="f" * 64)),
            lambda data: mutate(data, "observer-source-projection.json", lambda raw: raw.update(originalBytes=999)),
            lambda data: mutate(data, "observer-source-projection.json", lambda raw: raw.update(retainedBytes=999)),
            lambda data: mutate(data, "observer-source-projection.json", lambda raw: raw.update(mode="exact", markers={}, redactor=None)),
            lambda data: mutate(data, "observer-source-projection.json", lambda raw: raw.update(markers={})),
            lambda data: mutate(data, "observer-source-projection.json", lambda raw: raw.update(markers={"credential-assignment": 2})),
            lambda data: mutate(data, "observer-source-projection.json", lambda raw: raw.update(redactor=None)),
            lambda data: mutate(data, "scope.json", lambda raw: raw.pop("originalObserverSourceRetained")),
            lambda data: mutate(data, "scope.json", lambda raw: raw.update(originalObserverSourceRetained=True)),
            lambda data: mutate(data, "scope.json", lambda raw: raw.update(observerRedactionReexecutedByReplay=True)),
        )
        for index, change in enumerate(changes):
            with self.subTest(index=index):
                contents = redacted_fixture()
                change(contents)
                with self.assertRaises((ValueError, KeyError)):
                    REDUCER.reduce_contents(contents, COMMIT)

    def test_pure_replay_preserves_every_row_and_environment_field(self):
        contents = fixture()
        before = copy.deepcopy(contents)
        with patch("builtins.open", side_effect=AssertionError("ambient file read")), patch.object(Path, "read_bytes", side_effect=AssertionError("ambient bytes")), patch.object(Path, "read_text", side_effect=AssertionError("ambient text")):
            result = REDUCER.reduce_contents(contents, COMMIT)
        self.assertEqual(result["verdict"], "MATCHED")
        self.assertEqual(result["exactReferencesRecorded"], 24)
        self.assertFalse(result["scope"]["binariesRetained"])
        self.assertEqual(contents, before)
        reversed_report = json.loads(contents["observations.json"])
        reversed_report["rows"].reverse()
        contents["observations.json"] = encoded(reversed_report)
        reordered = REDUCER.reduce_contents(contents, COMMIT)
        self.assertEqual(result["rows"], reordered["rows"])
        self.assertNotEqual(result["inputSha256"], reordered["inputSha256"])

    def test_adverse_observations_are_retained_and_rederived(self):
        for field, value, violation in (
            ("CapturedReferenceMatches", False, "exception-reference"),
            ("ReplacementRejected", False, "replacement-identity-control"),
            ("ObservedOuterDeliveries", [], "outer-callback-order"),
            ("AfterCleanupCounters", [1, 0, 0, 0, 0, 0], "cleanup"),
            ("ClockScopedIds", [16], "active-outer-clock"),
            ("AfterOuterEmissionId", 16, "child-emission-id"),
        ):
            with self.subTest(field=field):
                contents = fixture()
                mutate(contents, "observations.json", lambda raw: raw["rows"][0].__setitem__(field, value))
                result = REDUCER.reduce_contents(contents, COMMIT)
                self.assertEqual(result["verdict"], "ADVERSE")
                adverse = next(row for row in result["rows"] if violation in row["violations"])
                self.assertEqual(adverse["recorded"][field], value)

    def test_reported_failures_and_unexpected_diagnostics_remain_adverse(self):
        contents = fixture()
        mutate(contents, "observations.json", lambda raw: raw["rows"][0].update(Failures=["retained-fault"], UnexpectedException="retained unexpected exception"))
        contents["execution.log"] = contents["execution.log"].replace(b"checks: 0", b"checks: 1")
        result = REDUCER.reduce_contents(contents, COMMIT)
        self.assertEqual(result["verdict"], "ADVERSE")
        self.assertEqual(result["reportedFailureCount"], 1)
        self.assertTrue(any(row["recorded"]["UnexpectedException"] == "retained unexpected exception" for row in result["rows"]))

    def test_missing_corrupt_duplicate_and_drifted_inputs_fail_closed(self):
        changes = (
            lambda data: data.pop("observer-source.cs.txt"),
            lambda data: data.__setitem__("observations.json", b"{broken"),
            lambda data: data.__setitem__("observations.json", b'{"rows": [], "rows": []}'),
            lambda data: mutate(data, "observations.json", lambda raw: raw["rows"].pop()),
            lambda data: mutate(data, "observations.json", lambda raw: raw["rows"].append(raw["rows"][0])),
            lambda data: mutate(data, "observations.json", lambda raw: raw.__setitem__("productMvid", "f" * 36)),
            lambda data: mutate(data, "observations.json", lambda raw: raw.__setitem__("observerSha256", "f" * 64)),
            lambda data: mutate(data, "source-bindings.json", lambda raw: raw.__setitem__("head", "e" * 40)),
            lambda data: mutate(data, "debug-source-binding.json", lambda raw: raw["Documents"][0].__setitem__("Sha256", "f" * 64)),
            lambda data: mutate(data, "scope.json", lambda raw: raw.__setitem__("nativeQualified", True)),
            lambda data: mutate(data, "scope.json", lambda raw: raw.__setitem__("binariesRetained", 0)),
            lambda data: data.__setitem__("observer-source.cs.txt", b"changed source"),
            lambda data: data.__setitem__("execution.log", b"false summary\n"),
            lambda data: mutate(data, "observations.json", lambda raw: raw["rows"][0].__setitem__("Escape", 0)),
            lambda data: data.__setitem__("observations.json", b'{"runtime": NaN}'),
        )
        for index, change in enumerate(changes):
            with self.subTest(index=index):
                contents = fixture()
                change(contents)
                with self.assertRaises((ValueError, KeyError)):
                    REDUCER.reduce_contents(contents, COMMIT)

    def test_real_bundle_cli_replays_and_rejects_false_normalized_summary(self):
        with tempfile.TemporaryDirectory(prefix="managed-clock-bundle-") as temporary:
            root = Path(temporary)
            for name, raw in fixture().items():
                (root / name).write_bytes(raw)
            def cli(*args):
                return subprocess.run(["node", str(BUNDLE), *map(str, args)], capture_output=True, text=True, check=False)
            sealed = cli("seal", root, "--experiment-id", "managed-clock-fixture", "--artifact-class", "managed-clock-fault-identity", "--reducer", "managed-clock-fault-identity-v1", "--source-commit", COMMIT)
            self.assertEqual(sealed.returncode, 0, sealed.stderr)
            manifest = root / "evidence-manifest.json"
            self.assertEqual(cli("verify", manifest).returncode, 0)
            replay = cli("replay", manifest)
            self.assertEqual(replay.returncode, 0, replay.stderr)
            script = "const fs=require('fs'),b=require(process.argv[1]);const p=process.argv[2],m=JSON.parse(fs.readFileSync(p));m.normalized.exactReferencesRecorded=999;m.bundleDigest=b.bundleDigest(m);fs.writeFileSync(p,JSON.stringify(m));"
            changed = subprocess.run(["node", "-e", script, str(BUNDLE), str(manifest)], capture_output=True, text=True, check=False)
            self.assertEqual(changed.returncode, 0, changed.stderr)
            self.assertEqual(cli("verify", manifest).returncode, 0)
            replay = cli("replay", manifest)
            self.assertNotEqual(replay.returncode, 0)
            self.assertIn("different normalized", replay.stderr)

    def test_capture_dispatch_preserves_python_selection_on_three_platforms(self):
        script = "const cp=require('child_process');cp.spawnSync=(command,args,options)=>{console.log(JSON.stringify({command,args,input:JSON.parse(options.input)}));return {status:0,stdout:'{}'}};Object.defineProperty(process,'platform',{value:process.argv[1]});require(process.argv[2]).reduceCapture(new Map([['x.txt',Buffer.from('payload')]]),{sourceCommit:process.argv[3]},process.argv[4]);"
        for platform in ("linux", "darwin", "win32"):
            for profile, filename, argument in (
                ("managed-clock-fault-identity", "reduce_managed_clock_identity.py", "--bundle-stdin"),
                ("open-loop-editor-capture", "audit_open_loop_trace.py", "--bundle-stdin"),
                ("editor-latency-clock-capture", "audit_open_loop_trace.py", "--clock-bundle-stdin"),
            ):
                with self.subTest(platform=platform, profile=profile):
                    run = subprocess.run(["node", "-e", script, platform, str(SCRIPTS / "unity/perf-evidence-reducers.js"), COMMIT, profile], capture_output=True, text=True, check=False)
                    self.assertEqual(run.returncode, 0, run.stderr)
                    actual = json.loads(run.stdout)
                    self.assertEqual(actual["command"], "python" if platform == "win32" else "python3")
                    self.assertTrue(actual["args"][0].endswith(filename))
                    self.assertEqual(actual["args"][1], argument)
                    self.assertEqual(actual["input"], {"sourceCommit": COMMIT, "contents": {"x.txt": "cGF5bG9hZA=="}})


if __name__ == "__main__":
    unittest.main()
