"""Exercise the real Unity classifier and static matrix validation tiers."""

import base64
import hashlib
import json
import os
import shutil
from pathlib import Path
import subprocess
import tempfile
import unittest
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parents[2]


def workflow(name):
    result = subprocess.run(
        ["node", "-e", "process.stdout.write(JSON.stringify(require('yaml').parse(require('fs').readFileSync(process.argv[1], 'utf8'))))", str(ROOT / ".github/workflows" / name)],
        cwd=ROOT, capture_output=True, text=True, check=True,
    )
    return json.loads(result.stdout)


class UnityValidationTiersTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.unity = workflow("unity-tests.yml")
        cls.perf = workflow("perf-numbers.yml")
        cls.docs = workflow("unity-docs-gate.yml")

    def test_required_context_keeps_the_reviewed_docs_companion(self):
        self.assertIn("paths-ignore", self.unity["on"]["pull_request"])
        self.assertNotIn("push", self.unity["on"])
        self.assertEqual(self.docs["on"]["pull_request"]["paths"][1:], self.unity["on"]["pull_request"]["paths-ignore"])
        gate = self.unity["jobs"]["unity-ci-success"]
        self.assertEqual(gate["name"], "Unity CI Success")
        self.assertEqual(gate["if"], "${{ always() }}")

    def test_literal_matrix_selects_only_latest_on_pr(self):
        canonical = json.loads((ROOT / ".github/unity-versions.json").read_text())["all"]
        matrix = self.unity["jobs"]["unity-tests"]["strategy"]["matrix"]
        self.assertEqual(matrix["unity-version"], canonical)
        self.assertEqual(matrix["test-mode"], ["standalone"])
        self.assertEqual(matrix["exclude"], [
            {"unity-version": "${{ github.event_name == 'pull_request' && '" + version + "' || '' }}"}
            for version in canonical[:-1]
        ])
        self.assertNotIn("include", matrix)

    def test_transport_research_compiles_only_with_the_admitted_pipeline_version(self):
        directory = ROOT / "Tests/Editor/PipelineTransportResearch"
        definition_path, = directory.glob("*.asmdef")
        definition = json.loads(definition_path.read_text(encoding="utf-8"))
        symbol = "DXM_621_PIPELINE_PRESENT"
        self.assertIn(symbol, definition["defineConstraints"])
        definitions = [
            (entry["name"], entry["expression"])
            for entry in definition["versionDefines"]
            if entry["define"] == symbol
        ]
        self.assertEqual(definitions, [("com.unity.pipeline", "[0.8.0-exp.1]")])

    def test_performance_is_manual_or_nightly_dispatch_only(self):
        self.assertEqual(set(self.perf["on"]), {"workflow_dispatch"})
        self.assertNotIn("comment-perf-doc", self.perf["jobs"])
        self.assertNotIn("head-check", self.perf["jobs"])
        publisher = self.perf["jobs"]["commit-perf-doc"]
        self.assertIn("github.event_name == 'workflow_dispatch'", publisher["if"])
        self.assertIn("github.ref == format('refs/heads/{0}', github.event.repository.default_branch)", publisher["if"])
        self.assertIn("report-perf-deltas", publisher["needs"])
        self.assertIn("report-perf-deltas", self.perf["jobs"])

    @unittest.skipIf(os.name == "nt", "bash classifier is exercised on Linux and macOS")
    def test_large_changed_path_lists_keep_relevant_managed_and_script_lanes(self):
        document = workflow("ci.yml")
        script = next(step["run"] for step in document["jobs"]["changes"]["steps"] if step.get("id") == "filter")
        for expression, value in (("github.event_name", "pull_request"), ("github.repository", "example/repo"), ("github.event.pull_request.number", "1")):
            script = script.replace("${{ " + expression + " }}", value)
        scripts = [f"scripts/{index:04d}-{'x' * 160}.js" for index in range(2000)]
        cases = [
            ("documentation last", scripts + ["docs/guides/testing.md"], "true", "true"),
            ("documentation first", ["docs/guides/testing.md"] + scripts, "true", "true"),
            ("publication only", ["docs/architecture/performance.md", "docs/architecture/perf-baseline.csv", "docs/architecture/perf-baseline-profile.json"], "false", "true"),
        ]
        for name, paths, docs_expected, scripts_expected in cases:
            with self.subTest(name=name), tempfile.TemporaryDirectory(prefix="dxm-large-pr-filter-") as tmp:
                root = Path(tmp)
                records = root / "records.jsonl"
                records.write_text("".join(json.dumps([path, None]) + "\n" for path in paths))
                output = root / "output"
                env = dict(os.environ, CHANGED_RECORDS=str(records), EXPECTED_FILE_COUNT=str(len(paths)), GITHUB_OUTPUT=str(output))
                result = subprocess.run(["bash", "-c", 'gh() { cat "$CHANGED_RECORDS"; };\n' + script], env=env, capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                outputs = dict(line.split("=", 1) for line in output.read_text().splitlines())
                self.assertEqual(outputs["docs_dotnet"], docs_expected)
                self.assertEqual(outputs["scripts"], scripts_expected)

    @unittest.skipIf(os.name == "nt", "bash classifier is exercised on Linux and macOS")
    def test_classifier_handles_renames_and_fails_closed(self):
        script = next(step["run"] for step in self.docs["jobs"]["classify"]["steps"] if step.get("id") == "classify")
        cases = [
            ("docs", [["docs/a.md", None]], 1, 0, 0, "false"),
            ("docs meta", [["README.md.meta", None]], 1, 0, 0, "true"),
            ("runtime", [["Runtime/Core/A.cs", None]], 1, 0, 0, "true"),
            ("mixed", [["docs/a.md", None], ["Editor/A.cs", None]], 2, 0, 0, "true"),
            ("renamed out of code", [["docs/a.md", "Runtime/A.cs"]], 1, 0, 0, "true"),
            ("renamed into code", [["Runtime/A.cs", "docs/a.md"]], 1, 0, 0, "true"),
            ("unknown", [["future/build.config", None]], 1, 0, 0, "true"),
            ("empty", [], 1, 0, 1, None),
            ("incomplete", [["docs/a.md", None]], 2, 0, 1, None),
            ("api failed", [], 1, 1, 1, None),
                        ("malformed", [[None, None]], 1, 0, 1, None),
            ("bad count", [["docs/a.md", None]], "", 0, 1, None),
        ]
        for name, records, count, api_status, expected_status, relevance in cases:
            with self.subTest(name=name), tempfile.TemporaryDirectory(prefix="dxm-unity-tier-") as tmp:
                root = Path(tmp)
                stub = root / "gh"
                stub.write_text("#!/bin/sh\ncat <<'FILES'\n" + "\n".join(json.dumps(record) for record in records) + "\nFILES\nexit " + str(api_status) + "\n")
                stub.chmod(0o755)
                output = root / "output"
                env = dict(os.environ, PATH=str(root) + os.pathsep + os.environ["PATH"], EVENT_NAME="pull_request", PR_NUMBER="1", EXPECTED_FILE_COUNT=str(count), GITHUB_REPOSITORY="example/repo", GITHUB_OUTPUT=str(output))
                result = subprocess.run(["bash", "-c", script], env=env, capture_output=True, text=True)
                if expected_status == 0:
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                else:
                    self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                written = output.read_text() if output.exists() else ""
                self.assertEqual(written.strip(), "documentation_only=" + ("true" if relevance == "false" else "false") if relevance is not None else "")


class NightlyDispatcherTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.document = workflow("nightly-validation.yml")
        cls.script = cls.document["jobs"]["dispatch"]["steps"][0]["run"]

    def test_scheduler_has_no_unity_credentials(self):
        self.assertEqual(self.document["permissions"], {"contents": "read"})
        self.assertEqual(self.document["jobs"]["dispatch"]["permissions"], {"actions": "write", "contents": "read"})
        self.assertEqual(self.document["on"]["schedule"], [{"cron": "0 1 * * *"}, {"cron": "0 3 * * *"}])
        self.assertNotIn("UNITY_SERIAL", json.dumps(self.document))
        self.assertNotIn("BUILD_LOCK_APP", json.dumps(self.document))

    @unittest.skipIf(os.name == "nt", "bash scheduler is exercised on Linux and macOS")
    def test_scheduler_dispatches_once_and_propagates_failures(self):
        cases = [
            ("correctness", "schedule", "0 1 * * *", "current", "", "", 0, ["unity-tests.yml"]),
            ("performance", "schedule", "0 3 * * *", "current", "", "", 0, ["perf-numbers.yml"]),
            ("manual", "workflow_dispatch", "", "current", "", "", 0, ["unity-tests.yml", "perf-numbers.yml"]),
            ("duplicate", "schedule", "0 1 * * *", "current", "123\tin_progress", "", 0, []),
            ("tag performance cannot suppress publication", "schedule", "0 3 * * *", "current", "123\tcompleted", "tag-only", 0, ["perf-numbers.yml"]),
            ("complete evidence", "schedule", "0 1 * * *", "current", "123\tcompleted", "", 0, []),
            ("consumer-only evidence", "schedule", "0 1 * * *", "current", "123\tcompleted", "incomplete", 0, ["unity-tests.yml"]),
            ("stale", "schedule", "0 1 * * *", "stale", "", "", 0, []),
            ("list failed", "schedule", "0 1 * * *", "current", "", "list", 1, []),
            ("head failed", "schedule", "0 1 * * *", "current", "", "head", 1, []),
            ("dispatch failed", "schedule", "0 1 * * *", "current", "", "dispatch", 1, ["unity-tests.yml"]),
            ("unknown schedule", "schedule", "* * * * *", "current", "", "", 1, []),
            ("untrusted event", "pull_request", "", "current", "", "", 1, []),
        ]
        for name, event, schedule, head, existing, failure, expected, dispatched in cases:
            with self.subTest(name=name), tempfile.TemporaryDirectory(prefix="dxm-nightly-") as tmp:
                root = Path(tmp)
                stub = root / "gh"
                stub.write_text("""#!/bin/sh
case "$*" in
  *git/ref/heads/master*)
    [ "$FAILURE" != head ] || exit 1
    if [ "$LIVE_HEAD" = current ]; then echo "$EXPECTED_SHA"; else echo stale; fi ;;
  *actions/workflows/*/runs?*)
    [ "$FAILURE" != list ] || exit 1
    if [ "$FAILURE" = tag-only ]; then
      case "$*" in *'&branch=master&'*) ;; *) echo "$EXISTING" ;; esac
    else
      [ -z "$EXISTING" ] || echo "$EXISTING"
    fi ;;
  *contents/.github/unity-versions.json*) echo "$VERSIONS_BASE64" ;;
  *actions/runs/*/artifacts*)
    if [ "$FAILURE" = incomplete ]; then echo "unity-old-consumer-install"; else printf '%s\\n' "$COMPLETE_ARTIFACTS"; fi ;;
  "workflow run "*)
    echo "$3" >> "$DISPATCH_LOG"
    [ "$FAILURE" != dispatch ] || exit 1 ;;
  *) echo "Unexpected gh invocation: $*" >&2; exit 2 ;;
esac
""")
                stub.chmod(0o755)
                dispatch_log = root / "dispatches"
                versions = json.loads((ROOT / ".github/unity-versions.json").read_text())["all"]
                complete = [f"unity-{version}-{mode}" for version in versions for mode in ("editmode", "playmode", "standalone")]
                complete += [f"perf-{versions[-1]}-standalone-{suite}" for suite in ("internal", "comparisons")]
                env = dict(os.environ, PATH=str(root) + os.pathsep + os.environ["PATH"], EVENT_NAME=event, EVENT_SCHEDULE=schedule, EXPECTED_SHA="a" * 40, DEFAULT_BRANCH="master", GITHUB_REPOSITORY="example/repo", LIVE_HEAD=head, EXISTING=existing, FAILURE=failure, DISPATCH_LOG=str(dispatch_log), VERSIONS_BASE64=base64.b64encode(json.dumps({"all": versions}).encode()).decode(), COMPLETE_ARTIFACTS="\n".join(complete))
                result = subprocess.run(["bash", "-c", self.script], env=env, capture_output=True, text=True)
                if expected == 0:
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                else:
                    self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(dispatch_log.read_text().splitlines() if dispatch_log.exists() else [], dispatched)


class DispatchedPerformanceRegressionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.document = workflow("perf-numbers.yml")
        cls.job = cls.document["jobs"]["report-perf-deltas"]
        cls.script = next(step["run"] for step in cls.job["steps"] if step.get("id") == "delta")

    def test_report_blocks_publication_independently_of_app_secrets(self):
        self.assertNotIn("if", self.job)
        self.assertEqual(self.job["needs"], ["perf-benchmarks", "perf-unity-success"])
        self.assertIn("report-perf-deltas", self.document["jobs"]["commit-perf-doc"]["needs"])
        self.assertTrue(any(step.get("run") == "npm ci --ignore-scripts --no-audit --no-fund" for step in self.job["steps"]))

    @unittest.skipIf(os.name == "nt", "bash report is exercised on Linux and macOS")
    def test_actual_dispatch_report_preserves_regression_and_compatibility_guards(self):
        cases = ["stable", "throughput regression", "allocation regression", "bootstrap", "profile changed", "methodology changed", "renamed out of methodology", "wrong source", "wrong platform", "baseline platform", "missing scenario", "missing inputs", "bad baseline source"]
        for name in cases:
            with self.subTest(name=name), tempfile.TemporaryDirectory(prefix="dxm-perf-report-") as tmp:
                root = Path(tmp)
                def git(*args):
                    return subprocess.run(["git", *args], cwd=root, capture_output=True, text=True, check=True).stdout.strip()
                git("init", "-q")
                git("config", "user.name", "Fixture")
                git("config", "user.email", "fixture@example.com")
                git("config", "commit.gpgsign", "false")
                method = root / "Tests/Runtime/Benchmarks/original.cs"
                method.parent.mkdir(parents=True)
                method.write_text("methodology\n")
                git("add", ".")
                git("commit", "-qm", "baseline")
                baseline_sha = git("rev-parse", "HEAD")
                if name == "methodology changed":
                    method.write_text("changed methodology\n")
                elif name == "renamed out of methodology":
                    method.rename(root / "moved.cs")
                else:
                    (root / "runtime.cs").write_text("runtime change\n")
                git("add", "-A")
                git("commit", "-qm", "candidate")
                measured_sha = git("rev-parse", "HEAD")
                (root / "scripts").mkdir()
                shutil.copytree(ROOT / "scripts/unity", root / "scripts/unity")
                artifact = root / ".artifacts/perf-download/comparisons"
                artifact.mkdir(parents=True)
                profile = {"executionProfileId": "profile", "affinityMask": "0xF", "priorityClass": "Normal"}
                baseline = root / ".artifacts/reference.csv"
                baseline_profile = root / ".artifacts/reference-profile.json"
                baseline_profile.write_text(json.dumps(profile))
                if name == "profile changed":
                    profile["affinityMask"] = "0x3"
                (artifact / "performance-cpu-profile.json").write_text(json.dumps(profile))
                platform = "Standalone IL2CPP x64 Release (WindowsPlayer; Unity 6000.5.2f1)"
                header = "scenario,platform,commit,runIndex,emitsPerSecond,gcAllocations,wallClockMs,gcAllocatedBytes\n"
                baseline_platform = "other platform" if name == "baseline platform" else platform
                baseline_stamp = "invalid" if name == "bad baseline source" else baseline_sha
                baseline.write_text(header + ("" if name == "bootstrap" else f"UntargetedFlood_OneHandler,{baseline_platform},{baseline_stamp},0,1000,0,10,0\n"))
                current_stamp = baseline_sha if name == "wrong source" else measured_sha
                current_platform = platform.replace("WindowsPlayer", "LinuxPlayer") if name == "wrong platform" else platform
                scenario = "TargetedFlood_OneListener" if name == "missing scenario" else "UntargetedFlood_OneHandler"
                throughput = 500 if name == "throughput regression" else 1000
                allocations = 1 if name == "allocation regression" else 0
                if name != "missing inputs":
                    (artifact / "unity.log").write_text(header + f"{scenario},{current_platform},{current_stamp},0,{throughput},{allocations},10,0\n")
                summary = root / "summary.md"
                delta = root / ".artifacts/delta.md"
                env = dict(os.environ, NODE_PATH=str(ROOT / "node_modules"), LATEST_VERSION="6000.5.2f1", MEASURED_SHA=measured_sha, PERF_BASELINE=str(baseline), PERF_BASELINE_PROFILE=str(baseline_profile), PERF_DELTA=str(delta), GITHUB_STEP_SUMMARY=str(summary))
                result = subprocess.run(["bash", "-c", self.script], cwd=root, env=env, capture_output=True, text=True)
                omitted = name in {"bootstrap", "profile changed", "methodology changed", "renamed out of methodology"}
                if name == "stable" or omitted:
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                    self.assertEqual("omitted" in summary.read_text(), omitted)
                else:
                    self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                if name in {"throughput regression", "allocation regression"}:
                    self.assertIn("exceeded the existing diagnostic regression threshold", result.stdout)
                    self.assertTrue(summary.exists())


class ReleaseQualificationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.document = workflow("release.yml")
        cls.script = cls.document["jobs"]["release-qualification"]["steps"][1]["run"]
        cls.versions = json.loads((ROOT / ".github/unity-versions.json").read_text())["all"]

    @unittest.skipIf(os.name == "nt", "release approval runs in Bash")
    def test_publish_requires_wallstop_review_for_the_release_environment(self):
        publish = self.document["jobs"]["publish"]
        self.assertEqual(publish.get("environment"), "release")
        self.assertEqual(publish["permissions"]["actions"], "read")
        gate = publish["steps"][0]
        self.assertEqual(gate["name"], "Verify release approval")
        self.assertNotIn("if", gate)
        self.assertFalse(gate.get("continue-on-error"))
        review = {"user": {"login": "wallstop"}, "state": "approved", "environments": [{"name": "release"}]}
        cases = [
            ("no review", [], False), ("wrong reviewer", [dict(review, user={"login": "other"})], False),
            ("wrong environment", [dict(review, environments=[{"name": "staging"}])], False),
            ("rejected", [dict(review, state="rejected")], False), ("approved", [review], True),
            ("API unavailable", None, False),
        ]
        mock = """gh() {
          [[ "$*" == "api repos/o/r/actions/runs/42/approvals" && -n "$REVIEWS" ]] || return 1
          echo "$REVIEWS"
        }
"""
        for name, reviews, allowed in cases:
            with self.subTest(name=name):
                env = dict(os.environ, GITHUB_REPOSITORY="o/r", GITHUB_RUN_ID="42",
                           REVIEWS=json.dumps(reviews) if reviews is not None else "")
                run = subprocess.run(["bash", "-c", mock + gate["run"]], env=env, capture_output=True, text=True)
                self.assertEqual(run.returncode == 0, allowed, run.stdout + run.stderr)

    def test_publication_requires_all_release_lanes(self):
        needs = self.document["jobs"]["publish"]["needs"]
        self.assertIn("release-qualification", needs)
        self.assertIn("managed-release-tests", needs)
        receipt = next(step for step in self.document["jobs"]["release-qualification"]["steps"] if step.get("name") == "Upload release qualification receipt")
        self.assertTrue(receipt["with"].get("overwrite"))
        managed = self.document["jobs"]["managed-release-tests"]["steps"][-1]["run"]
        self.assertIn("dotnet test .net-tests/DxMessaging.Net.Tests.csproj", managed)
        self.assertNotIn("--filter", managed)
        self.assertIn("/p:CopyAnalyzerPayload=false", managed)
        self.assertIn(".docs-tests/", managed)
        upload = next(step for step in workflow("unity-tests.yml")["jobs"]["unity-tests"]["steps"] if step.get("name") == "Upload EditMode test artifacts")
        self.assertIn("'consumer-install' || 'editmode'", upload["with"]["name"])

    @unittest.skipIf(os.name == "nt", "NuGet.org verification runs in Bash")
    def test_attributes_verification_requires_public_source_and_matching_payload(self):
        steps = workflow("attributes-package.yml")["jobs"]["package"]["steps"]
        script = next(step["run"] for step in steps if step.get("name") == "Restore and verify the live package")
        feed = "https://api.nuget.org/v3/index.json"
        version = "4.0.0"
        cases = [
            ("matching public package", feed, b"attribute assembly", True, True),
            ("local cache", "/local-feed", b"attribute assembly", False, False),
            ("missing source metadata", None, b"attribute assembly", False, False),
            ("different public payload", feed, b"other assembly", False, False),
        ]
        dotnet = r"""
dotnet() {
    case "$1" in
        restore) printf '%s\n' "$@" > restore-args ;;
        test) printf '%s\n' "$@" > test-args ;;
        *) return 87 ;;
    esac
}
"""
        for name, source, payload, succeeds, runs_consumer in cases:
            with self.subTest(name=name), tempfile.TemporaryDirectory(prefix="dxm-nuget-origin-") as tmp:
                root = Path(tmp)
                cache = root / "packages"
                installed = cache / "wallstopstudios.dxmessaging.attributes" / version
                library = "lib/netstandard2.0/WallstopStudios.DxMessaging.Attributes.dll"
                (installed / library).parent.mkdir(parents=True)
                (installed / library).write_bytes(payload)
                if source is not None:
                    (installed / ".nupkg.metadata").write_text(json.dumps({"source": source}))
                package = root / ".artifacts/nuget" / f"WallstopStudios.DxMessaging.Attributes.{version}.nupkg"
                package.parent.mkdir(parents=True)
                with ZipFile(package, "w") as archive:
                    archive.writestr(library, b"attribute assembly")
                env = dict(os.environ, NUGET_PACKAGES=str(cache), PACKAGE_VERSION=version)
                result = subprocess.run(["bash", "-c", dotnet + script], cwd=root, env=env,
                                        capture_output=True, text=True)
                self.assertEqual(result.returncode == 0, succeeds, result.stdout + result.stderr)
                self.assertEqual((root / "test-args").exists(), runs_consumer)
                restore = (root / "restore-args").read_text().splitlines()
                self.assertEqual(restore.count("--source"), 1)
                self.assertEqual(restore[restore.index("--source") + 1], feed)
                self.assertIn("--no-http-cache", restore)
                self.assertIn("-p:RestoreAdditionalProjectSources=", restore)
                if runs_consumer:
                    self.assertIn("--no-restore", (root / "test-args").read_text().splitlines())

    @unittest.skipIf(os.name == "nt", "npm publication runs in Bash")
    def test_npm_retry_requires_matching_bytes_or_confirmed_absence(self):
        step = next(step for step in self.document["jobs"]["publish"]["steps"]
                    if step["name"] == "Publish to npm with provenance")
        name, version, archive = "com.example.fixture", "1.2.3", "fixture.tgz"
        payload = b"staged npm package"
        digest = base64.b64encode(hashlib.sha512(payload).digest()).decode("ascii")
        metadata = {"name": name, "version": version, "dist": {"integrity": f"sha512-{digest}"}}
        cases = [
            ("matching bytes", "200", json.dumps(metadata), payload, True, False),
            ("different bytes", "200", json.dumps(metadata), b"different", False, False),
            ("wrong version", "200", json.dumps({**metadata, "version": "1.2.4"}), payload, False, False),
            ("missing digest", "200", json.dumps({**metadata, "dist": {}}), payload, False, False),
            ("malformed JSON", "200", "unexpected response", payload, False, False),
            ("absent version", "404", "", payload, True, True),
            ("registry error", "503", "", payload, False, False),
            ("redirect", "302", "", payload, False, False),
            ("transport error", "transport", "", payload, False, False),
        ]
        stubs = r"""
curl() {
    [[ "$1" == --silent && "$2" == --show-error && "$3" == --output && "$5" == --write-out ]] || return 87
    [[ "$6" == '%{http_code}' && "$7" == "https://registry.npmjs.org/com.example.fixture/1.2.3" ]] || return 87
    if [[ "$REGISTRY_STATUS" == transport ]]; then return 7; fi
    printf '%s' "$REGISTRY_BODY" > "$4"
    printf '%s' "$REGISTRY_STATUS"
}
npx() {
    case "$*" in
        '--yes --package=npm@12.2.2 npm --version') printf '12.2.2\n' ;;
        '--yes --package=npm@12.2.2 npm view com.example.fixture@1.2.3 version')
            [[ "$REGISTRY_STATUS" == 200 ]]
            ;;
        '--yes --package=npm@12.2.2 npm publish fixture.tgz --provenance --access public'|'--yes --package=npm@12.2.2 npm publish fixture.tgz --provenance --access public --registry https://registry.npmjs.org')
            touch publish-called
            ;;
        *) return 87 ;;
    esac
}
"""
        script = step["run"].replace(
            "${{ steps.release_package.outputs.package-file }}", archive
        ).replace("${{ needs.verify-tag.outputs.package-version }}", version)
        for case, status, body, content, success, publish in cases:
            with self.subTest(case=case), tempfile.TemporaryDirectory(prefix="dxm-npm-retry-") as tmp:
                root = Path(tmp)
                (root / "package.json").write_text(json.dumps(metadata))
                (root / archive).write_bytes(content)
                env = dict(os.environ, PACKAGE_FILE=archive, PACKAGE_VERSION=version,
                           REGISTRY_STATUS=status, REGISTRY_BODY=body)
                result = subprocess.run(["bash", "-c", stubs + script], cwd=root, env=env,
                                        capture_output=True, text=True)
                self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
                self.assertEqual((root / "publish-called").exists(), publish)

    @unittest.skipIf(os.name == "nt", "bash producer qualification is exercised on Linux and macOS")
    def test_qualification_checks_actual_producer_and_artifact_metadata(self):
        revision = "a" * 40
        names = {
            101: [f"unity-{version}-{mode}" for version in self.versions for mode in ("editmode", "playmode", "standalone")],
            102: [f"unity-{version}-shipping" for version in (self.versions[0], self.versions[-1])],
            103: [f"unity-{version}-consumer-install" for version in self.versions],
            201: [f"perf-{self.versions[-1]}-standalone-{suite}" for suite in ("internal", "comparisons")],
        }
        runs = {"unity-tests.yml": [101, 102, 103], "perf-numbers.yml": [201]}
        baseline = {
            "runs": {workflow: [{"id": run_id, "head_sha": revision, "event": "workflow_dispatch", "status": "completed", "conclusion": "success"} for run_id in ids] for workflow, ids in runs.items()},
            "artifacts": {str(run_id): [{"id": 1000 * run_id + index, "name": name, "digest": "sha256:" + "b" * 64, "size_in_bytes": 1, "expired": False, "workflow_run": {"id": run_id, "head_sha": revision}} for index, name in enumerate(values)] for run_id, values in names.items()},
        }
        cases = ["valid", "wrong run revision", "wrong run event", "failed producer", "missing consumer", "expired artifact", "invalid digest", "wrong artifact revision", "empty artifact", "duplicate artifact", "api failure"]
        for name in cases:
            with self.subTest(name=name), tempfile.TemporaryDirectory(prefix="dxm-release-qualification-") as tmp:
                root = Path(tmp)
                data = json.loads(json.dumps(baseline))
                if name == "wrong run revision":
                    data["runs"]["perf-numbers.yml"][0]["head_sha"] = "c" * 40
                elif name == "wrong run event":
                    data["runs"]["perf-numbers.yml"][0]["event"] = "pull_request"
                elif name == "failed producer":
                    data["runs"]["perf-numbers.yml"][0]["conclusion"] = "failure"
                elif name == "missing consumer":
                    data["artifacts"]["103"].pop()
                elif name == "expired artifact":
                    data["artifacts"]["201"][0]["expired"] = True
                elif name == "invalid digest":
                    data["artifacts"]["201"][0]["digest"] = "sha256:bad"
                elif name == "wrong artifact revision":
                    data["artifacts"]["201"][0]["workflow_run"]["head_sha"] = "c" * 40
                elif name == "empty artifact":
                    data["artifacts"]["201"][0]["size_in_bytes"] = 0
                elif name == "duplicate artifact":
                    data["artifacts"]["201"].append(data["artifacts"]["201"][0])
                (root / "fixtures.json").write_text(json.dumps(data))
                (root / ".github").mkdir()
                (root / ".github/unity-versions.json").write_text(json.dumps({"all": self.versions}))
                stub = root / "gh"
                stub.write_text("""#!/usr/bin/env python3
import json, os, re, sys
from pathlib import Path
if os.environ['FIXTURE_FAIL'] == 'yes': sys.exit(1)
if sys.argv[1:4] != ['api', '--paginate', '--slurp']: sys.exit(2)
data = json.loads(Path('fixtures.json').read_text())
endpoint = sys.argv[4]
match = re.search(r'actions/workflows/([^/]+)/runs', endpoint)
if match:
    print(json.dumps([{'workflow_runs': data['runs'][match[1]]}]))
else:
    match = re.search(r'actions/runs/(\\d+)/artifacts', endpoint)
    if not match: sys.exit(3)
    print(json.dumps([{'artifacts': data['artifacts'][match[1]]}]))
""")
                stub.chmod(0o755)
                env = dict(os.environ, PATH=str(root) + os.pathsep + os.environ["PATH"], GITHUB_REPOSITORY="example/repo", GITHUB_SHA=revision, FIXTURE_FAIL="yes" if name == "api failure" else "no")
                result = subprocess.run(["bash", "-c", self.script], cwd=root, env=env, capture_output=True, text=True)
                receipt = root / ".artifacts/release/qualification.json"
                if name == "valid":
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                    qualified = json.loads(receipt.read_text())
                    self.assertEqual(qualified["source_commit"], revision)
                    self.assertEqual(set(qualified["qualifications"]), {"compatibility", "shipping", "consumer-install", "performance"})
                else:
                    self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                    self.assertFalse(receipt.exists())


if __name__ == "__main__":
    unittest.main()
