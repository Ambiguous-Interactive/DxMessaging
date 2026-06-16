"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

// Drift-guard for the test-suite-performance contract: Initialize-EphemeralProject
// must emit a ProjectSettings/EditorSettings.asset that disables enter-play-mode
// domain + scene reload (value 3) so the PlayMode CI legs skip the per-entry
// reload. This guards the COMMITTED CI enforcement; the local .unity-test-project
// copy is gitignored, so the runner emit is the source of truth for CI. See
// docs/runbooks/test-suite-performance.md and the Fast Unity Tests skill.
const runCiTests = fs.readFileSync(path.join(__dirname, "..", "unity", "run-ci-tests.ps1"), "utf8");

test("run-ci-tests emits EnterPlayModeOptions reload-disable for CI projects", () => {
  assert.match(
    runCiTests,
    /m_EnterPlayModeOptionsEnabled:\s*1/,
    "run-ci-tests.ps1 must emit m_EnterPlayModeOptionsEnabled: 1"
  );
  assert.match(
    runCiTests,
    /m_EnterPlayModeOptions:\s*3/,
    "run-ci-tests.ps1 must emit m_EnterPlayModeOptions: 3 (DisableDomainReload | DisableSceneReload)"
  );
  assert.match(
    runCiTests,
    /Set-Content[^\r\n]*ProjectSettings\\EditorSettings\.asset/,
    "the EnterPlayModeOptions block must be written to ProjectSettings/EditorSettings.asset"
  );
});
