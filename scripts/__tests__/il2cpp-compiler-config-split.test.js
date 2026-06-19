"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

// Drift-guard for the IL2CPP C++ compiler-configuration split (Workstream L
// Task 10). run-ci-tests.ps1 is shared by the correctness standalone leg
// (unity-tests.yml -- excludes every perf scenario, publishes NO numbers) and
// the sole published Release-player leg (perf-numbers.yml). The native C++
// compile dominates the standalone wall-clock, so the correctness leg passes
// -Il2CppConfiguration Debug (a far faster compile) while the perf leg stays
// Release. Debug vs Release changes ONLY native C++ optimization, not the
// managed->C++ transpilation or IL2CPP runtime semantics the correctness leg
// verifies -- so fidelity is preserved. These assertions fail if a future edit
// re-hardcodes the config, flips the correctness leg to Release, or lets the
// perf leg drift to Debug. See docs/runbooks/test-suite-performance.md.

const read = (...rel) => fs.readFileSync(path.join(__dirname, "..", "..", ...rel), "utf8");
const runCiTests = read("scripts", "unity", "run-ci-tests.ps1");
const w = (name) => read(".github", "workflows", name);

test("run-ci-tests.ps1 parameterizes the IL2CPP compiler config (defaults Release)", () => {
  // Constrained to Debug/Release and defaulting to Release, so every caller that
  // omits the switch stays Release.
  assert.match(
    runCiTests,
    /\[ValidateSet\('Debug',\s*'Release'\)\]\s*\r?\n\s*\[string\]\$Il2CppConfiguration\s*=\s*'Release'/,
    "must declare [ValidateSet('Debug','Release')] [string]$Il2CppConfiguration = 'Release'"
  );
});

test("the generated configurator interpolates the param, not a hardcoded enum", () => {
  assert.match(
    runCiTests,
    /SetIl2CppCompilerConfiguration\(BuildTargetGroup\.Standalone,\s*Il2CppCompilerConfiguration\.\$Il2CppConfiguration\)/,
    "the configurator must call Il2CppCompilerConfiguration.$Il2CppConfiguration"
  );
  // Red-green anchor: the old hardcoded enum literal must be gone (the
  // GetIl2CppCompilerConfiguration log call has no .Debug/.Release/.Master access).
  assert.doesNotMatch(
    runCiTests,
    /Il2CppCompilerConfiguration\.(Debug|Release|Master)\b/,
    "the configurator must not hardcode an Il2CppCompilerConfiguration enum member"
  );
});

test("Il2CppConfiguration threads through Initialize-EphemeralProject", () => {
  assert.match(
    runCiTests,
    /function Initialize-EphemeralProject[\s\S]*?\[string\]\$Il2CppConfiguration\s*=\s*'Release'/,
    "Initialize-EphemeralProject must accept [string]$Il2CppConfiguration = 'Release'"
  );
  assert.match(
    runCiTests,
    /New-ConfiguratorSource\s+-Backend\s+\$Backend\s+-Il2CppConfiguration\s+\$Il2CppConfiguration/,
    "Initialize-EphemeralProject must forward -Il2CppConfiguration to New-ConfiguratorSource"
  );
  assert.match(
    runCiTests,
    /Initialize-EphemeralProject[^\r\n]*-Il2CppConfiguration\s+\$Il2CppConfiguration/,
    "the orchestration must forward -Il2CppConfiguration into Initialize-EphemeralProject"
  );
});

test("the correctness leg (unity-tests.yml) builds the standalone player with Debug C++", () => {
  const unityTests = w("unity-tests.yml");
  // Line-anchored so an explanatory comment mentioning the flag cannot satisfy it
  // -- the real invocation arg must be present, not just documented.
  assert.match(
    unityTests,
    /^[ \t]+-Il2CppConfiguration\s+'Debug'/m,
    "unity-tests.yml must invoke run-ci-tests.ps1 with -Il2CppConfiguration 'Debug'"
  );
  assert.doesNotMatch(
    unityTests,
    /^[ \t]+-Il2CppConfiguration\s+'Release'/m,
    "the correctness leg publishes no numbers; it must not pay for a Release C++ compile"
  );
});

test("the published perf leg (perf-numbers.yml) pins Release C++ explicitly", () => {
  const perf = w("perf-numbers.yml");
  // Anchor to the hashtable assignment, not a comment: the splat entry is indented
  // C# `Key = 'Value'`, while the explanatory comment line starts with `#`, so a
  // future edit that deletes the real pin but keeps the comment turns this RED.
  assert.match(
    perf,
    /^[ \t]+Il2CppConfiguration\s*=\s*'Release'/m,
    "perf-numbers.yml must pin Il2CppConfiguration = 'Release' in the splat (sole published Release leg)"
  );
  assert.doesNotMatch(
    perf,
    /^[ \t]+Il2CppConfiguration\s*=\s*'Debug'|-Il2CppConfiguration\s+'Debug'/m,
    "the published perf leg must never build a Debug C++ player"
  );
});

test("the benchmark and release legs never request a Debug C++ player", () => {
  // Both run editmode/playmode (the config is inert with no IL2CPP player), but
  // must not drift to Debug in case either ever grows a standalone leg.
  for (const name of ["unity-benchmarks.yml", "release.yml"]) {
    assert.doesNotMatch(
      w(name),
      /-Il2CppConfiguration\s+'Debug'|Il2CppConfiguration\s*=\s*'Debug'/,
      `${name} must not request a Debug IL2CPP compiler configuration`
    );
  }
});
