"""Exercise Unity's three NUnit result gates without launching the Editor."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
UNITY_SCRIPTS = ROOT / "scripts" / "unity"
ACTION_PATH = ROOT / ".github" / "actions" / "verify-unity-results" / "action.yml"
PWSH = shutil.which("pwsh")
LOAD_FUNCTIONS = r"""
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $env:DXM_NUNIT_GATE_SCRIPT, [ref]$tokens, [ref]$parseErrors
)
if ($parseErrors.Count -ne 0) { throw 'Result gate script parse failed.' }
foreach ($definition in $ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -in $functions
}, $false)) {
    . ([scriptblock]::Create($definition.Extent.Text))
}
"""
CONSUMER_GATE = "$functions = @('Assert-ConsumerResults')\n" + LOAD_FUNCTIONS + r"""
Assert-ConsumerResults -ResultPath $env:DXM_NUNIT_XML -SourceKind 'classic' |
    ConvertTo-Json -Compress
"""
EDITOR_GATE = r"""
$functions = @(
    'Test-NUnitResults', 'Write-SuiteWallClockSummary', 'Write-UnityFailedTestAnnotations',
    'Get-NUnitNodeFullName', 'ConvertTo-SingleLineDiagnostic', 'New-WorkflowCommandStopToken',
    'Write-CiError', 'Write-CiNotice'
)
""" + LOAD_FUNCTIONS + r"""
Test-NUnitResults -Path $env:DXM_NUNIT_XML -Label 'ordinary Unity'
"""


def composite_gate():
    result = subprocess.run(
        [
            "node",
            "-e",
            "const fs = require('node:fs'); const yaml = require('yaml'); "
            "process.stdout.write(yaml.parse(fs.readFileSync(process.argv[1], 'utf8')).runs.steps[0].run);",
            str(ACTION_PATH),
        ],
        cwd=ROOT,
        capture_output=True,
        text=True,
        check=True,
    )
    return result.stdout.replace("${{ inputs.results-dir }}", "$env:DXM_NUNIT_DIR").replace(
        "${{ inputs.label }}", "ordinary Unity"
    )


@unittest.skipUnless(PWSH, "PowerShell is not available")
class UnityNUnitResultsTests(unittest.TestCase):
    def validate(self, verdict, passed=8, *, command=CONSUMER_GATE, script="export-unitypackage.ps1"):
        report = ET.Element("test-run", total=str(passed), passed=str(passed), failed="0", skipped="0")
        if verdict is not None:
            report.set("result", verdict)
        suite = ET.SubElement(
            report, "test-suite", type="TestFixture", fullname="SampleFixture", result=verdict or "Passed"
        )
        for index in range(passed):
            ET.SubElement(suite, "test-case", name=f"SampleContract{index + 1}", result="Passed")
        if verdict == "Failed":
            suite.set("site", "TearDown")
            ET.SubElement(ET.SubElement(suite, "failure"), "message").text = "Fixture cleanup failed."
        with tempfile.TemporaryDirectory(prefix="dxm-consumer-result-") as temp:
            path = Path(temp) / "test-results.xml"
            ET.ElementTree(report).write(path, encoding="utf-8", xml_declaration=True)
            return self.run_gate(path, command, script)

    def run_gate(self, path, command, script):
        entry = path.parent / "result-gate.ps1"
        entry.write_text(command, encoding="utf-8")
        return subprocess.run(
            [PWSH, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(entry)],
            cwd=path.parent,
            env=dict(
                os.environ,
                DXM_NUNIT_GATE_SCRIPT=str(UNITY_SCRIPTS / script),
                DXM_NUNIT_XML=str(path),
                DXM_NUNIT_DIR=str(path.parent),
                DXM_RESULTS_FILE=str(path),
                DXM_EXPECTED_EMPTY="false",
            ),
            capture_output=True,
            text=True,
            check=False,
        )

    def test_eight_passing_cases_and_passing_run_succeed(self):
        result = self.validate("Passed")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout), dict(total=8, passed=8, failed=0, skipped=0))

    def test_fixture_teardown_failure_rejects_eight_passing_cases(self):
        result = self.validate("Failed")
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn("classic consumer NUnit run result was 'Failed'", result.stderr)

    def test_missing_run_verdict_rejects_eight_passing_cases(self):
        result = self.validate(None)
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn("classic consumer NUnit run result was 'missing'", result.stderr)

    def test_passing_run_with_seven_cases_still_fails_the_count_gate(self):
        result = self.validate("Passed", passed=7)
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn("classic consumer expected 8 passed / 0 failed / 0 skipped", result.stderr)

    def test_unity_runner_and_composite_reject_failed_fixture_with_passing_cases(self):
        gates = [
            ("Unity runner", EDITOR_GATE, "run-ci-tests.ps1"),
            ("GitHub composite", composite_gate(), "run-ci-tests.ps1"),
        ]
        for name, command, script in gates:
            with self.subTest(gate=name, verdict="Passed"):
                result = self.validate("Passed", command=command, script=script)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            with self.subTest(gate=name, verdict="Failed"):
                result = self.validate("Failed", command=command, script=script)
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertIn("SampleFixture -- Fixture cleanup failed.", result.stdout)
                self.assertIn("failed NUnit run without failed test cases", result.stdout)
