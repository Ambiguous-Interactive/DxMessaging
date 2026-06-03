"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawnSync } = require("child_process");
const yaml = require("yaml");
const { combinedText } = require("../lib/pwsh-output");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const ACTION_PATH = path.join(
  REPO_ROOT,
  ".github",
  "actions",
  "verify-unity-results",
  "action.yml"
);

function pwshAvailable() {
  return (
    spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], {
      encoding: "utf8"
    }).status === 0
  );
}

const PWSH_PRESENT = pwshAvailable();
const workspaces = [];

afterAll(() => {
  for (const workspace of workspaces) {
    fs.rmSync(workspace, { recursive: true, force: true });
  }
});

function escapePwshDoubleQuoted(value) {
  return value.replace(/`/g, "``").replace(/\$/g, "`$").replace(/"/g, '`"');
}

function getActionRunScript(resultsDir, label = "Unity action test") {
  const action = yaml.parse(fs.readFileSync(ACTION_PATH, "utf8"));
  const step = action.runs.steps.find(
    (candidate) => candidate.name === "Verify tests actually ran"
  );
  expect(step).toBeDefined();
  return step.run
    .replaceAll("${{ inputs.results-dir }}", escapePwshDoubleQuoted(resultsDir))
    .replaceAll("${{ inputs.label }}", escapePwshDoubleQuoted(label));
}

function runActionScript(resultsDir, label, cwd, extraEnv) {
  const script = getActionRunScript(resultsDir, label);
  const harness = path.join(cwd, "verify-unity-results-action.ps1");
  fs.writeFileSync(harness, script, "utf8");
  // The action maps the `expected-empty` input to the DXM_EXPECTED_EMPTY env in
  // the step's `env:` block; this spawn harness substitutes only results-dir and
  // label, so the skip signal must be injected through the environment to
  // exercise (and pin) the skip-on-empty branch. Always set DXM_EXPECTED_EMPTY
  // explicitly so an ambient value in the test runner cannot make a case flaky.
  return spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-File", harness], {
    cwd,
    encoding: "utf8",
    maxBuffer: 16 * 1024 * 1024,
    env: { ...process.env, DXM_EXPECTED_EMPTY: "false", ...(extraEnv || {}) }
  });
}

function makeWorkspace() {
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-verify-unity-results-"));
  workspaces.push(workspace);
  return workspace;
}

function writeResultsXml(resultsDir, attrs) {
  fs.mkdirSync(resultsDir, { recursive: true });
  fs.writeFileSync(
    path.join(resultsDir, "results.xml"),
    `<test-run total="${attrs.total}" passed="${attrs.passed}" failed="${attrs.failed}" skipped="${attrs.skipped}"></test-run>`,
    "utf8"
  );
}

// A failing NUnit3 results.xml that exercises BOTH classes the action now
// enumerates: (1) a failed leaf <test-case> (an ordinary assertion failure) and
// (2) a failed <test-suite> whose OWN <failure> is a [OneTimeTearDown]
// Assert.Fail with no failed child case -- the SuiteWallClockBudgetTest shape.
// The outer aggregate suites are result="Failed" too but must NOT be enumerated
// (the assembly suite has no direct <failure>; the fixture is covered by its
// failed child). Mirrors the synthetic fixture in the runner contract test.
function writeFailingResultsXml(resultsDir) {
  fs.mkdirSync(resultsDir, { recursive: true });
  const xml = [
    '<test-run total="3" passed="1" failed="2" skipped="0" result="Failed">',
    '  <test-suite type="Assembly" name="Tests.dll" fullname="Tests.dll" result="Failed">',
    '    <test-suite type="SetUpFixture" name="SuiteWallClockBudgetTest"',
    '        fullname="DxMessaging.Tests.Runtime.SuiteWallClockBudgetTest" result="Failed">',
    "      <failure>",
    "        <message>OneTimeTearDown: Default suite wall clock (200.00s) exceeded the hard",
    "budget (180.0s). Reduce iteration counts.</message>",
    "        <stack-trace>at SuiteWallClockBudgetTest.EndSuiteTimer()</stack-trace>",
    "      </failure>",
    "    </test-suite>",
    '    <test-suite type="TestFixture" name="MessageBusTests"',
    '        fullname="DxMessaging.Tests.Runtime.Core.MessageBusTests" result="Failed">',
    '      <test-case name="DispatchOrder"',
    '          fullname="DxMessaging.Tests.Runtime.Core.MessageBusTests.DispatchOrder" result="Failed">',
    "        <failure>",
    "          <message>  Expected: 5\n  But was:  4</message>",
    "          <stack-trace>at MessageBusTests.DispatchOrder() in MessageBusTests.cs:line 42</stack-trace>",
    "        </failure>",
    "      </test-case>",
    '      <test-case name="PassingOne"',
    '          fullname="DxMessaging.Tests.Runtime.Core.MessageBusTests.PassingOne" result="Passed" />',
    "    </test-suite>",
    "  </test-suite>",
    "</test-run>"
  ].join("\n");
  fs.writeFileSync(path.join(resultsDir, "results.xml"), xml, "utf8");
}

describe("verify-unity-results composite action", () => {
  test("action exists and uses pwsh", () => {
    expect(fs.existsSync(ACTION_PATH)).toBe(true);
    const action = yaml.parse(fs.readFileSync(ACTION_PATH, "utf8"));
    expect(action.runs.steps[0].shell).toBe("pwsh");
  });

  if (!PWSH_PRESENT) {
    test.skip.each([
      "missing results directory",
      "provisioning diagnostics without XML",
      "zero tests",
      "failed count",
      "passing results"
    ])("%s", () => {});
    return;
  }

  test("expected-empty=true short-circuits to success even with NO results directory", () => {
    const workspace = makeWorkspace();
    const missingDir = path.join(workspace, ".artifacts", "unity", "skipped");
    // No results dir exists. Normally this is exit 1 ("No artifacts directory"),
    // but a target that resolved an empty DxMessaging assembly list skipped the
    // Unity run, so the verifier must report success and explain the skip.
    const result = runActionScript(missingDir, "Unity 6000 standalone", workspace, {
      DXM_EXPECTED_EMPTY: "true"
    });
    expect(result.status).toBe(0);
    const out = combinedText(result);
    expect(out).toMatch(/No DxMessaging test assemblies were selected/i);
    expect(out).not.toMatch(/No artifacts directory/);
  });

  test("expected-empty=false does NOT skip -- a missing results directory still fails", () => {
    const workspace = makeWorkspace();
    const missingDir = path.join(workspace, ".artifacts", "unity", "notskipped");
    const result = runActionScript(missingDir, "Unity action test", workspace, {
      DXM_EXPECTED_EMPTY: "false"
    });
    expect(result.status).toBe(1);
    expect(combinedText(result)).toMatch(/No artifacts directory/);
  });

  test("missing results directory lists provisioning diagnostics when available", () => {
    const workspace = makeWorkspace();
    const summaryDir = path.join(workspace, ".artifacts", "unity", "missing", "provisioning");
    fs.mkdirSync(summaryDir, { recursive: true });
    fs.writeFileSync(
      path.join(summaryDir, "ensure-editor-summary.json"),
      JSON.stringify({
        finalClassification: "failed: provisioning",
        provisioningProfile: "EditorOnly"
      }),
      "utf8"
    );

    const result = runActionScript(
      path.join(workspace, ".artifacts", "unity", "does-not-exist"),
      "Missing Dir",
      workspace
    );

    expect(result.status).toBe(1);
    const text = combinedText(result);
    expect(text).toContain("No artifacts directory");
    expect(text).toContain("Provisioning diagnostics files");
    expect(text).toContain("ensure-editor-summary.json");
    expect(text).toContain("classification=failed: provisioning profile=EditorOnly");
  });

  test("directory with provisioning diagnostics but no XML points at the summary", () => {
    const workspace = makeWorkspace();
    const resultsDir = path.join(workspace, ".artifacts", "unity", "cell");
    const summaryDir = path.join(resultsDir, "provisioning");
    fs.mkdirSync(summaryDir, { recursive: true });
    fs.writeFileSync(
      path.join(summaryDir, "ensure-editor-summary.json"),
      JSON.stringify({
        finalClassification: "failed: Android NDK",
        provisioningProfile: "StandaloneWindowsIl2Cpp"
      }),
      "utf8"
    );

    const result = runActionScript(resultsDir, "No XML", workspace);

    expect(result.status).toBe(1);
    const text = combinedText(result);
    expect(text).toContain("No NUnit results.xml");
    expect(text).toContain("Provisioning summary:");
    expect(text).toContain("ensure-editor-summary.json");
    expect(text).toContain("classification=failed: Android NDK profile=StandaloneWindowsIl2Cpp");
  });

  test("zero-test XML fails with an explicit annotation", () => {
    const workspace = makeWorkspace();
    const resultsDir = path.join(workspace, ".artifacts", "unity", "zero");
    writeResultsXml(resultsDir, { total: 0, passed: 0, failed: 0, skipped: 0 });

    const result = runActionScript(resultsDir, "Zero Tests", workspace);

    expect(result.status).toBe(1);
    expect(combinedText(result)).toContain("0 tests ran for Zero Tests");
  });

  test("failed-count XML fails and reports the failed count", () => {
    const workspace = makeWorkspace();
    const resultsDir = path.join(workspace, ".artifacts", "unity", "failed");
    writeResultsXml(resultsDir, { total: 2, passed: 1, failed: 1, skipped: 0 });

    const result = runActionScript(resultsDir, "Failed Tests", workspace);

    expect(result.status).toBe(1);
    expect(combinedText(result)).toContain("Failed Tests reported 1 failed test(s)");
  });

  test("failed-count XML enumerates WHICH tests failed (leaf case + OneTimeTearDown suite)", () => {
    // The diagnostic gap this closes: the action previously printed only the
    // aggregate "$failed reported N failed test(s)" line, never naming WHICH
    // tests failed. It now enumerates each failed test from the NUnit3 XML with
    // a single-line ::error:: annotation (collapsed message) plus a full
    // ::group:: console block, for BOTH failed leaf cases AND failed
    // OneTimeSetUp/OneTimeTearDown suites.
    const workspace = makeWorkspace();
    const resultsDir = path.join(workspace, ".artifacts", "unity", "enumerate");
    writeFailingResultsXml(resultsDir);

    const result = runActionScript(resultsDir, "PlayMode", workspace);
    const text = combinedText(result);

    expect(result.status).toBe(1);
    // The aggregate line is PRESERVED (existing behavior).
    expect(text).toContain("PlayMode reported 2 failed test(s)");

    // (1) Failed leaf case: single-line ::error:: annotation carrying the
    // fullname + first line of the message, plus the recovered multi-line
    // message inside the ::group:: block.
    expect(text).toContain(
      "::error::PlayMode failed test: DxMessaging.Tests.Runtime.Core.MessageBusTests.DispatchOrder"
    );
    expect(text).toContain(
      "::group::Failed test: DxMessaging.Tests.Runtime.Core.MessageBusTests.DispatchOrder"
    );
    expect(text).toContain("Expected: 5");
    expect(text).toContain("But was: 4");

    // (2) OneTimeTearDown suite failure: enumerated as its own failed node.
    expect(text).toContain(
      "::error::PlayMode failed test: DxMessaging.Tests.Runtime.SuiteWallClockBudgetTest"
    );
    expect(text).toContain("exceeded the hard budget");

    // (3) No double-print of aggregate-only suites and no passing-test noise.
    expect(text).not.toContain("failed test: Tests.dll");
    expect(text).not.toContain("PassingOne");
  });

  test("enumerates a failed test-case that has only name (no fullname) without degrading", () => {
    // FIX 1 (behavior synchronized with run-ci-tests.ps1): a Failed <test-case>
    // with only a `name` attribute (no `fullname`). The GetAttribute-based
    // resolver must fall back to the name and still print the annotation + group
    // block; it must NOT degrade to the generic "Could not enumerate" warning.
    const workspace = makeWorkspace();
    const resultsDir = path.join(workspace, ".artifacts", "unity", "noname");
    fs.mkdirSync(resultsDir, { recursive: true });
    const xml = [
      '<test-run total="1" passed="0" failed="1" skipped="0" result="Failed">',
      '  <test-suite type="TestFixture" name="NoFullName" result="Failed">',
      '    <test-case name="OnlyHasName" result="Failed">',
      "      <failure><message>boom</message></failure>",
      "    </test-case>",
      "  </test-suite>",
      "</test-run>"
    ].join("\n");
    fs.writeFileSync(path.join(resultsDir, "results.xml"), xml, "utf8");

    const result = runActionScript(resultsDir, "NoName", workspace);
    const text = combinedText(result);

    expect(result.status).toBe(1);
    expect(text).toContain("::error::NoName failed test: OnlyHasName");
    expect(text).toContain("::group::Failed test: OnlyHasName");
    expect(text).toContain("boom");
    expect(text).not.toContain("Could not enumerate failed tests");
  });

  test("fences the raw failure message in ::stop-commands:: so injected workflow commands are neutralized", () => {
    // FIX 3: an assertion <message> containing `::error::INJECTED` and
    // `::set-output name=x::` must be emitted inside a ::stop-commands::<token>
    // ... ::<token>:: fence so GitHub does not execute the injected directives.
    // The token is a RANDOM per-dump GUID (defense-in-depth so a crafted message
    // containing the exact `::<literal>::` close line cannot end the fence
    // early), so we EXTRACT the actual token from the output rather than
    // asserting a fixed literal.
    const workspace = makeWorkspace();
    const resultsDir = path.join(workspace, ".artifacts", "unity", "inject");
    fs.mkdirSync(resultsDir, { recursive: true });
    const xml = [
      '<test-run total="1" passed="0" failed="1" skipped="0" result="Failed">',
      '  <test-suite type="TestFixture" name="Inj" fullname="Ns.Inj" result="Failed">',
      '    <test-case name="Evil" fullname="Ns.Inj.Evil" result="Failed">',
      "      <failure>",
      "        <message>line one",
      "::error::INJECTED",
      "::set-output name=x::pwned</message>",
      "        <stack-trace>at Ns.Inj.Evil()</stack-trace>",
      "      </failure>",
      "    </test-case>",
      "  </test-suite>",
      "</test-run>"
    ].join("\n");
    fs.writeFileSync(path.join(resultsDir, "results.xml"), xml, "utf8");

    const result = runActionScript(resultsDir, "Inject", workspace);
    const text = combinedText(result);

    expect(result.status).toBe(1);
    // Extract the ACTUAL random token emitted after `::stop-commands::` and
    // assert the matching `::<that-token>::` close line exists. The token shape
    // is `dxm-stop-commands-<32-hex-guid>` (a GUID 'N' form).
    const openMatch = /::stop-commands::(dxm-stop-commands-[0-9a-fA-F]{32})/.exec(text);
    expect(openMatch).not.toBeNull();
    const token = openMatch[1];
    const openMarker = `::stop-commands::${token}`;
    const closeMarker = `::${token}::`;
    expect(text).toContain(openMarker);
    expect(text).toContain(closeMarker);
    // The injected directives land BETWEEN the opening fence and its close.
    // (combinedText flattens newlines, so order is verified by index. The
    // injected strings ALSO appear earlier in the flattened single-line
    // ::error:: annotation, so we search for the raw-body copy AFTER the fence.)
    const openIdx = text.indexOf(openMarker);
    const closeIdx = text.indexOf(closeMarker, openIdx + openMarker.length);
    const injectedErrorIdx = text.indexOf("::error::INJECTED", openIdx + 1);
    const injectedOutputIdx = text.indexOf("::set-output name=x::pwned", openIdx + 1);
    expect(openIdx).toBeGreaterThanOrEqual(0);
    expect(closeIdx).toBeGreaterThan(openIdx);
    expect(injectedErrorIdx).toBeGreaterThan(openIdx);
    expect(injectedErrorIdx).toBeLessThan(closeIdx);
    expect(injectedOutputIdx).toBeGreaterThan(openIdx);
    expect(injectedOutputIdx).toBeLessThan(closeIdx);
  });

  test("reports a suite's own teardown failure even when it also has a failed child case", () => {
    // LOW: a fixture that fails a test AND throws in [OneTimeTearDown] must
    // surface BOTH the child case's assertion message AND the suite's own
    // teardown message (the suite is reported on its direct <failure> regardless
    // of failed descendants; fullname de-dup keeps it distinct from the child).
    const workspace = makeWorkspace();
    const resultsDir = path.join(workspace, ".artifacts", "unity", "both");
    fs.mkdirSync(resultsDir, { recursive: true });
    const xml = [
      '<test-run total="2" passed="0" failed="2" skipped="0" result="Failed">',
      '  <test-suite type="TestFixture" name="BothFix"',
      '      fullname="Ns.BothFix" result="Failed">',
      "    <failure>",
      "      <message>OneTimeTearDown: teardown blew up</message>",
      "      <stack-trace>at Ns.BothFix.TearDown()</stack-trace>",
      "    </failure>",
      '    <test-case name="ChildFail" fullname="Ns.BothFix.ChildFail" result="Failed">',
      "      <failure><message>child assert failed</message></failure>",
      "    </test-case>",
      "  </test-suite>",
      "</test-run>"
    ].join("\n");
    fs.writeFileSync(path.join(resultsDir, "results.xml"), xml, "utf8");

    const result = runActionScript(resultsDir, "Both", workspace);
    const text = combinedText(result);

    expect(result.status).toBe(1);
    expect(text).toContain("::error::Both failed test: Ns.BothFix.ChildFail");
    expect(text).toContain("child assert failed");
    expect(text).toContain("::error::Both failed test: Ns.BothFix");
    expect(text).toContain("teardown blew up");
  });

  test("passing XML reports totals and succeeds", () => {
    const workspace = makeWorkspace();
    const resultsDir = path.join(workspace, ".artifacts", "unity", "passed");
    writeResultsXml(resultsDir, { total: 3, passed: 3, failed: 0, skipped: 0 });

    const result = runActionScript(resultsDir, "Passed Tests", workspace);

    expect(result.status).toBe(0);
    expect(combinedText(result)).toContain("Passed Tests: total=3 passed=3 failed=0 skipped=0");
  });
});
