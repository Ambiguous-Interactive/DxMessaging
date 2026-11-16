"""Execute the actual read-only postmortem workflow against temporary files."""

# cspell:ignore mscorlib mklink

import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / ".github/workflows/runner-bootstrap.yml").read_text(encoding="utf-8")
BEGIN = "# BEGIN IL2CPP POSTMORTEM CAPTURE"
END = "# END IL2CPP POSTMORTEM CAPTURE"
assert SOURCE.count(BEGIN) == SOURCE.count(END) == 1
CAPTURE = textwrap.dedent(SOURCE[SOURCE.rfind("\n", 0, SOURCE.index(BEGIN)) + 1:
                               SOURCE.index(END) + len(END)])
NAMES = re.findall(r"^    '([^']+\.dll)'$", CAPTURE, re.MULTILINE)

PRELUDE = r'''
function Get-CimInstance {
    if ($env:TEST_SCENARIO -ceq 'active process') { [pscustomobject]@{ Name = 'Unity.exe' } }
}
function Get-ChildItem {
    $items = @(Microsoft.PowerShell.Management\Get-ChildItem @args)
    foreach ($item in $items) {
        $bound = $env:TEST_SCENARIO
        if (($bound -ceq 'tool byte bound' -and $item.Name -ceq 'il2cpp.exe') -or
            ($bound -ceq 'input byte bound' -and $item.Name -ceq 'mscorlib.dll') -or
            ($bound -ceq 'owned byte bound' -and $item.Name -ceq 'Assembly-CSharp.dll')) {
            $length = if ($bound -ceq 'tool byte bound') { 1GB + 1 } elseif ($bound -ceq 'input byte bound') { 128MB + 1 } else { 64MB + 1 }
            [pscustomobject]@{ Name = $item.Name; FullName = $item.FullName; DirectoryName = $item.DirectoryName;
                PSIsContainer = $item.PSIsContainer; Attributes = $item.Attributes; Extension = $item.Extension; Length = $length }
        } else { $item }
    }
}
function Copy-Item {
    Microsoft.PowerShell.Management\Copy-Item @args
    if ($env:TEST_SCENARIO -ceq 'changed source') {
        [IO.File]::AppendAllText($args[1], 'changed during copy')
    }
}
'''

SCENARIOS = {
    "complete": None,
    "wrong request": "original DAD-MACHINE",
    "wrong runner": "original DAD-MACHINE",
    "wrong machine": "original DAD-MACHINE",
    "wrong repository": "original DAD-MACHINE",
    "other mode": "isolated mode",
    "bad source identity": "complete workflow identity",
    "active process": "process is active",
    "missing marker": "does not exist",
    "wrong marker": "ownership marker differs",
    "missing input": "66 declared converter operands",
    "extra input": "66 declared converter operands",
    "wrong input name": "66 declared converter operands",
    "missing converter": "fixed input/tool/owned-byte bounds",
    "existing output": "output already exists",
    "redirected project": "without redirection",
    "tool count bound": "fixed input/tool/owned-byte bounds",
    "tool byte bound": "fixed input/tool/owned-byte bounds",
    "input byte bound": "fixed input/tool/owned-byte bounds",
    "owned byte bound": "fixed input/tool/owned-byte bounds",
    "changed source": "source file changed during observation",
}


class Il2CppPostmortem(unittest.TestCase):
    def test_artifact_binding_and_mode_isolation(self):
        job = SOURCE.split("\n  il2cpp-postmortem:\n", 1)[1].split("\n  bootstrap:\n", 1)[0]
        self.assertIn("path: .artifacts/770-source", job)
        self.assertIn("working-directory: .artifacts/770-source", job)
        self.assertIn('node scripts/unity/redact-unity-artifacts.js "$env:DXM_POSTMORTEM_ROOT"', job)
        self.assertIn("DXM_POSTMORTEM_ROOT: ${{ steps.postmortem.outputs.artifact-root }}", job)
        self.assertIn("path: ${{ steps.postmortem.outputs.artifact-root }}", job)
        self.assertIn("steps.postmortem_node.outcome == 'success'", job)
        self.assertIn("steps.redact_postmortem.outcome == 'success'", job)
        self.assertLess(job.index("Redact retained compiler artifacts"), job.index("Upload retained compiler artifacts"))
        jobs = re.findall(r"^  ([a-z][a-z-]+):$", SOURCE.split("\njobs:\n", 1)[1], re.MULTILINE)
        for name in jobs:
            if name in ["runner-preflight", "il2cpp-postmortem"]:
                continue
            body = SOURCE.split("\n  " + name + ":\n", 1)[1]
            condition = re.search(r"    if:.*?(?=\n    [^ ])", body, re.DOTALL)[0]
            self.assertIn("!inputs['il2cpp-postmortem'] && (", condition, name)

    def exercise(self, scenario, failure):
        self.assertEqual(len(NAMES), 66, "Retain all original declared operands.")
        with tempfile.TemporaryDirectory(prefix="dxm-postmortem-") as directory:
            base = Path(directory)
            workspace = base / "workspace"
            project = workspace / "dxm-u/t/6000.5.2f1-standalone-935565f2"
            managed = project / "Library/Bee/artifacts/WinPlayerBuildProgram/ManagedStripped"
            converter = base / "tools/u6-v3/6000.5.2f1/Editor/Data/il2cpp/build/deploy"
            temporary = base / "temporary"
            managed.mkdir(parents=True)
            converter.mkdir(parents=True)
            temporary.mkdir()
            marker = project / ".dxmessaging-ci-project"
            marker.write_text("com.wallstop-studios.dxmessaging unity ci ephemeral project")
            for name in NAMES:
                (managed / name).write_bytes(b"MZ fixture input " + name.encode("ascii"))
            (converter / "il2cpp.exe").write_bytes(b"MZ fixture converter; never execute")
            (converter / "runtime.json").write_text('{"fixture":true}')
            output = temporary / "dxm-770-12345-1"
            github_output = base / "github-output"
            github_output.touch()
            environment = dict(os.environ, RUNNER_WORKSPACE=str(workspace),
                               RUNNER_TOOL_CACHE=str(base / "tools"), RUNNER_TEMP=str(temporary),
                               RUNNER_NAME="DAD-MACHINE", COMPUTERNAME="DAD-MACHINE",
                               DXM_REQUESTED_RUNNER="DAD-MACHINE", DXM_OTHER_POSTMORTEM_MODES="false",
                               GITHUB_REPOSITORY="Ambiguous-Interactive/DxMessaging", GITHUB_SHA="a" * 40,
                               GITHUB_RUN_ID="12345", GITHUB_RUN_ATTEMPT="1",
                               GITHUB_OUTPUT=str(github_output), TEST_SCENARIO=scenario)
            substitutions = {
                "wrong request": ("DXM_REQUESTED_RUNNER", "ELI-MACHINE"),
                "wrong runner": ("RUNNER_NAME", "ELI-MACHINE"),
                "wrong machine": ("COMPUTERNAME", "ELI-MACHINE"),
                "wrong repository": ("GITHUB_REPOSITORY", "fixture/other"),
                "other mode": ("DXM_OTHER_POSTMORTEM_MODES", "true"),
                "bad source identity": ("GITHUB_SHA", "short"),
            }
            if scenario in substitutions:
                key, value = substitutions[scenario]
                environment[key] = value
            if scenario == "missing marker":
                marker.unlink()
            elif scenario == "wrong marker":
                marker.write_text("not the owned project")
            elif scenario == "missing input":
                (managed / NAMES[0]).unlink()
            elif scenario == "extra input":
                (managed / "Unexpected.dll").write_bytes(b"extra")
            elif scenario == "wrong input name":
                (managed / NAMES[0]).rename(managed / "Unexpected.dll")
            elif scenario == "missing converter":
                (converter / "il2cpp.exe").unlink()
            elif scenario == "existing output":
                output.mkdir()
                (output / "existing.txt").write_text("retain existing evidence")
            elif scenario == "redirected project":
                backing = project.with_name("backing-project")
                project.rename(backing)
                if os.name == "nt":
                    subprocess.run(["cmd", "/c", "mklink", "/J", str(project), str(backing)],
                                   capture_output=True, check=True, timeout=10)
                else:
                    project.symlink_to(backing, target_is_directory=True)
            elif scenario == "tool count bound":
                for index in range(1023):
                    (converter / f"extra-{index}.txt").write_text("fixture")
            before = {str(p.relative_to(base)): (p.read_bytes(), p.stat().st_mtime_ns)
                      for root in [workspace, base / "tools"] for p in root.rglob("*") if p.is_file()}
            script = base / "fixture.ps1"
            script.write_text(PRELUDE + "\ntry {\n" + CAPTURE +
                              "\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }\n",
                              encoding="utf-8")
            result = subprocess.run(["pwsh", "-NoProfile", "-NonInteractive", "-File", str(script)],
                                    env=environment, cwd=ROOT,
                                    capture_output=True, encoding="utf-8", timeout=30, check=False)
            context = f"{scenario}: {result.stdout}\n{result.stderr}"
            if failure:
                self.assertNotEqual(result.returncode, 0, context)
                self.assertIn(failure, result.stdout + result.stderr, context)
            else:
                self.assertEqual(result.returncode, 0, context)
                receipt = json.loads((output / "postmortem.json").read_text(encoding="utf-8-sig"))
                self.assertEqual(receipt["state"], "complete-later-observation", context)
                self.assertEqual((receipt["inputCount"], receipt["toolCount"]), (66, 2), context)
                self.assertEqual(receipt["historicalPreimage"], "not-established", context)
                self.assertFalse(receipt["globallyAtomic"], context)
                self.assertEqual(receipt["loadedToolClosure"], "not-measured", context)
                expected_paths = {"inputs/" + name for name in NAMES} | {
                    "converter/il2cpp.exe", "converter/runtime.json"}
                self.assertEqual(len(receipt["files"]), len(expected_paths), context)
                self.assertEqual({r["path"] for r in receipt["files"]}, expected_paths, context)
                for record in receipt["files"]:
                    kind, relative = record["path"].split("/", 1)
                    source = (managed if kind == "inputs" else converter) / relative
                    data = source.read_bytes()
                    self.assertEqual(hashlib.sha256(data).hexdigest(), record["sha256"], context)
                    self.assertEqual(len(data), record["bytes"], context)
                owned = [r for r in receipt["files"] if r["retainedPath"]]
                self.assertEqual(len(owned), 12, context)
                for record in owned:
                    data = (output / record["retainedPath"]).read_bytes()
                    self.assertEqual(hashlib.sha256(data).hexdigest(), record["sha256"], context)
                    self.assertEqual(len(data), record["bytes"], context)
                self.assertEqual(len(list((output / "owned-inputs").iterdir())), 12, context)
                self.assertIn(str(output), github_output.read_text(), context)
            if scenario == "changed source":
                receipt = json.loads((output / "postmortem.json").read_text(encoding="utf-8-sig"))
                self.assertEqual(receipt["state"], "incomplete", context)
            else:
                after = {str(p.relative_to(base)): (p.read_bytes(), p.stat().st_mtime_ns)
                         for root in [workspace, base / "tools"] for p in root.rglob("*") if p.is_file()}
                self.assertEqual(before, after, "Source bytes and timestamps must stay unchanged: " + context)
            if scenario == "existing output":
                self.assertEqual((output / "existing.txt").read_text(), "retain existing evidence", context)


for scenario, failure in SCENARIOS.items():
    def check(self, scenario=scenario, failure=failure):
        self.exercise(scenario, failure)
    setattr(Il2CppPostmortem, "test_" + scenario.replace(" ", "_"), check)


if __name__ == "__main__":
    unittest.main()
