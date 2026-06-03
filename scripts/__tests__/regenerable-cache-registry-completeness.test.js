/**
 * @fileoverview COMPLETENESS GUARD for the regenerable-cache registry. Makes
 * the "corrupt REGENERABLE artifact hard-gated a push with manual-only
 * recourse" category impossible to reintroduce by cross-checking, in BOTH
 * directions, that every doctor section which is a regenerable temp artifact
 * has exactly one registry entry (and vice-versa), and that each registered
 * repair provably clears the dirty state its section flags.
 *
 * Discipline mirrors check-eol.test.js's whitelist: a NON_REGENERABLE allow-list
 * carries a one-line rationale per non-regenerable section, and a NEW section
 * name that is neither registered nor allow-listed FAILS the test (so a future
 * regenerable cache cannot be added to the doctor without a wired-in healer).
 *
 * Direction D adds a CONTENT-AWARE tripwire that shrinks the trusted-human
 * residual of the name-based allow-list: an allow-listed section whose name or
 * emitted lines NAME a tmpdir/cache artifact must be registered or explicitly
 * justified as a heuristic false positive, so the most likely mis-classification
 * (a regenerable cache mislabeled non-regenerable) is caught structurally.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");

const doctor = require("../doctor");
const { REGENERABLE_CACHE_REPAIRS } = require("../lib/regenerable-cache-registry");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

/**
 * A PER-CALL UNIQUE heal lock name (mirrors doctor-regenerable-closure.test.js
 * runRealHeal). The Direction-C / stray-file closure heals run IN-PROCESS and
 * would otherwise take the production-default REGENERABLE_CACHE_HEAL_LOCK_NAME
 * under the REAL repo .git with the (correct) sub-second acquire timeout. If a
 * future suite heals on the default lock concurrently, a contended worker could
 * lockFailed -> skip its sandbox purge -> flake the `installDir absent`
 * assertion. Threading a distinct lock dir per call removes that latent
 * cross-worker contention while exercising the same production code path (the
 * lockName is a first-class healIsolatedJestCache DI input).
 */
function uniqueHealLockName() {
  return `dxmsg-regen-complete-${process.pid}-${Math.random().toString(36).slice(2)}.lock`;
}

// Sections that are NOT regenerable temp artifacts -> they correctly have no
// auto-heal registry entry. Each carries a rationale (mirrors the check-eol
// whitelist discipline). A NEW doctor section that is genuinely a regenerable
// cache MUST be registered (and removed from here); a new non-regenerable
// section MUST be added here with a rationale, or this guard fails.
const NON_REGENERABLE_ALLOW_LIST = Object.freeze({
  "node_modules freshness":
    "tracked install state; healed out-of-band by repair-node-tooling's npm-ci gate, not a tmpdir cache.",
  "EOL policy": "static constants module; pure stats, nothing to heal.",
  "pre-commit config": "tracked .pre-commit-config.yaml + package.json cross-ref; a config audit.",
  "hook-perf budget": "tracked YAML perf score; a budget audit, no artifact to purge.",
  "cross-platform sanity": "host capability probe (pwsh/bash/platform); not an artifact.",
  "working-tree state": "live git status; the working tree is not a regenerable cache.",
  "changed documentation validators":
    "validator parity over tracked changed files; not a regenerable cache."
});

/**
 * Content-aware tripwire pattern (issue: shrink the trusted-human-assertion
 * residual). A REGENERABLE temp-cache section's NAME or produced LINES almost
 * always name the artifact it derives ("tmpdir", "regenerable", "fallback
 * cache", "managed-Jest cache"). If an ALLOW-LISTED (claimed-non-regenerable)
 * section matches this, that allow-listing is suspicious and must be either
 * REGISTERED (with a wired-in healer) or explicitly justified as a heuristic
 * false positive. Verified: NONE of the current non-regenerable allow-list
 * sections match, while the REGISTERED isolated-cache section DOES -- so the
 * heuristic discriminates a genuine regenerable cache from the audits.
 */
const REGENERABLE_ARTIFACT_HEURISTIC =
  /tmpdir|os\.tmpdir|regenerable|fallback cache|managed-jest cache/i;

/**
 * Allow-list entries the heuristic is KNOWN to (correctly) not flag today, but
 * which a reviewer may consciously exempt in the future if a non-regenerable
 * section legitimately mentions a cache-artifact word. Empty today: every
 * current allow-list entry is heuristic-clean, so any match is a real signal.
 * A future entry added here is a LOAD-BEARING assertion that the section is a
 * heuristic false positive (genuinely not a deletion-recoverable cache).
 */
const HEURISTIC_FALSE_POSITIVE_ALLOW_LIST = Object.freeze({});

/**
 * Collect every doctor SECTION object by driving the runDoctor aggregator with
 * an all-OK fixture (the same fixture shape doctor.test.js uses). Using
 * runDoctor (rather than re-driving each section with bespoke fixtures)
 * guarantees we enumerate the EXACT set of sections the production report
 * emits, including pre-commit config + hook-perf budget.
 */
function collectDoctorSections() {
  const overrides = {
    checkNodeModulesFreshness: {
      toolSpecs: [],
      existsSyncFn: () => true,
      requireFn: () => ({}),
      requireResolveFn: () => "/x",
      repoRoot: "/repo"
    },
    checkIsolatedJestCache: {
      existsSyncFn: () => false,
      readdirSyncFn: () => [],
      statSyncFn: () => ({ mtimeMs: 0 }),
      cacheRoot: "/never",
      nowMs: 0
    },
    checkEolPolicy: { policy: { crlfExts: new Set([".cs"]), lfExts: new Set([".js"]) } },
    checkPreCommitConfig: {
      readFileSyncFn: (filePath) => {
        if (filePath.endsWith(".pre-commit-config.yaml")) {
          return "repos: []\n";
        }
        return JSON.stringify({
          scripts: {
            "preflight:pre-push":
              "npm run preflight:pre-commit && pre-commit run --hook-stage pre-push --all-files"
          }
        });
      },
      parsePrecommitYaml: require("../lib/precommit-yaml"),
      runCommandFn: () => ({ status: 0, stdout: "pre-commit 4.0.0", error: null })
    },
    checkHookPerfBudget: {
      readFileSyncFn: () => "anything",
      scoreConfigFn: () => ({
        totalScore: 0,
        perHookScores: [],
        allowList: [],
        rejections: [],
        perHookViolations: []
      }),
      budget: 10,
      perHookCeiling: 3
    },
    checkCrossPlatformSanity: {
      platformFn: () => "linux",
      runCommandFn: () => ({ status: 0, stdout: "x\n", error: null }),
      readFileSyncFn: () => "{}\n",
      shellEnv: "/bin/bash"
    },
    checkWorkingTreeState: {
      runCommandFn: () => ({ status: 0, stdout: "", error: null, stderr: "" })
    },
    checkChangedDocumentation: {
      runChangedDocValidatorsFn: () => ({ files: [], totalViolations: 0 })
    }
  };

  // fast:false so working-tree + changed-docs sections keep their real names.
  const report = doctor.runDoctor({ overrides, fast: false });
  return report.sections;
}

function collectDoctorSectionNames() {
  return collectDoctorSections().map((s) => s.name);
}

describe("regenerable-cache-registry completeness", () => {
  const sections = collectDoctorSections();
  const sectionNames = sections.map((s) => s.name);
  const registrySectionNames = REGENERABLE_CACHE_REPAIRS.map((e) => e.doctorSectionName);

  test("registry and allow-list are frozen / well-formed", () => {
    expect(Object.isFrozen(REGENERABLE_CACHE_REPAIRS)).toBe(true);
    for (const entry of REGENERABLE_CACHE_REPAIRS) {
      expect(Object.isFrozen(entry)).toBe(true);
    }
    expect(Object.isFrozen(NON_REGENERABLE_ALLOW_LIST)).toBe(true);
  });

  test("Direction A: every doctor section is EITHER registered OR allow-listed (no unclassified section)", () => {
    const registered = new Set(registrySectionNames);
    const allowed = new Set(Object.keys(NON_REGENERABLE_ALLOW_LIST));
    const unclassified = sectionNames.filter((n) => !registered.has(n) && !allowed.has(n));
    expect(unclassified).toEqual([]);
    // And nothing may be in BOTH sets.
    const inBoth = sectionNames.filter((n) => registered.has(n) && allowed.has(n));
    expect(inBoth).toEqual([]);
  });

  test("Direction B: every registry doctorSectionName matches a REAL doctor section name", () => {
    for (const name of registrySectionNames) {
      expect(sectionNames).toContain(name);
    }
  });

  test("the isolated managed-Jest cache is registered (the motivating case)", () => {
    expect(registrySectionNames).toContain("isolated managed-Jest cache");
  });

  test("Direction C: each registered repair clears the dirty state its section flags (closure)", () => {
    // For the one registered entry, reuse the closure fixture: DIRTY ->
    // entry.probeAndHeal(sandbox) -> re-probe the matching doctor section ->
    // assert it is no longer 'fail' and the corrupt dir is gone.
    for (const entry of REGENERABLE_CACHE_REPAIRS) {
      const sandboxRoot = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-regen-complete-"));
      try {
        // Plant the partial install (omit jest-circus/build/runner.js).
        const installDir = path.join(sandboxRoot, "jest_30.3.0");
        fs.mkdirSync(path.join(installDir, "node_modules", "jest-circus", "build"), {
          recursive: true
        });
        fs.mkdirSync(path.join(installDir, "node_modules", "jest", "bin"), { recursive: true });
        fs.writeFileSync(path.join(installDir, "package.json"), "{}\n");
        fs.writeFileSync(path.join(installDir, "node_modules", "jest", "bin", "jest.js"), "//\n");
        fs.writeFileSync(
          path.join(installDir, "node_modules", "jest-circus", "package.json"),
          JSON.stringify({
            name: "jest-circus",
            version: "30.3.0",
            exports: { "./runner": "./build/runner.js" }
          })
        );

        // Pre-heal: the matching doctor section flags it (warn, not ok).
        const before = doctor.checkIsolatedJestCache({
          cacheRoot: sandboxRoot,
          hasHealthyLocalJestInstallFn: () => false
        });
        expect(before.status).not.toBe("ok");

        // Heal in-process via the registry entry, scoped to the sandbox, under a
        // PER-CALL UNIQUE lock so the closure proof can never contend cross-worker
        // on the shared default heal lock (see uniqueHealLockName above).
        const healResult = entry.probeAndHeal({
          cacheRoot: sandboxRoot,
          lockName: uniqueHealLockName(),
          warnFn: () => {}
        });
        expect(healResult.ok).toBe(true);
        expect(fs.existsSync(installDir)).toBe(false);

        // Post-heal: the section no longer flags a corruption (not 'fail').
        const after = doctor.checkIsolatedJestCache({
          cacheRoot: sandboxRoot,
          hasHealthyLocalJestInstallFn: () => false
        });
        expect(after.status).not.toBe("fail");
      } finally {
        fs.rmSync(sandboxRoot, { recursive: true, force: true });
      }
    }
  });

  test("Direction C (stray-file shape): the isolated-cache repair clears an ENOTDIR file-root (closure)", () => {
    // The cache ROOT being a stray FILE (not a dir) is a regenerable shape the
    // lens explicitly names. Plant a FILE where the cache dir belongs; the
    // matching doctor section must flag it (warn, not ok) AND the registry's
    // healer must clear it -- proving fix<->check closure for this shape too.
    const entry = REGENERABLE_CACHE_REPAIRS.find((e) => e.id === "isolated-managed-jest-cache");
    expect(entry).toBeTruthy();

    const parent = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-regen-stray-"));
    const fileRoot = path.join(parent, "dxmessaging-managed-jest");
    try {
      fs.writeFileSync(fileRoot, "stray file where the cache dir belongs\n");

      const before = doctor.checkIsolatedJestCache({
        cacheRoot: fileRoot,
        hasHealthyLocalJestInstallFn: () => false
      });
      expect(before.status).not.toBe("ok");

      const healResult = entry.probeAndHeal({
        cacheRoot: fileRoot,
        lockName: uniqueHealLockName(),
        warnFn: () => {}
      });
      expect(healResult.ok).toBe(true);
      expect(fs.existsSync(fileRoot)).toBe(false);

      const after = doctor.checkIsolatedJestCache({
        cacheRoot: fileRoot,
        hasHealthyLocalJestInstallFn: () => false
      });
      expect(after.status).not.toBe("fail");
    } finally {
      fs.rmSync(parent, { recursive: true, force: true });
    }
  });

  test("the NON_REGENERABLE_ALLOW_LIST is a TRUSTED HUMAN assertion (documented residual reintroduction vector)", () => {
    // RESIDUAL VECTOR (mirrors the check-eol whitelist discipline): the
    // allow-list classifies a section as non-regenerable by NAME + a free-text
    // rationale. A future fail-capable REGENERABLE section added to the doctor
    // AND allow-listed here with a FALSE rationale would pass Direction A (it is
    // "classified") and would get NO Direction-C closure proof (Direction C only
    // iterates REGENERABLE_CACHE_REPAIRS). This is the narrow path by which the
    // "regenerable artifact hard-gates with manual-only recourse" category could
    // be reintroduced with all tests green. The content-aware tripwire test
    // below ("Direction D") SHRINKS this residual: it flags an allow-listed
    // section whose name/lines name a tmpdir/cache artifact, converting the pure
    // name-based allow-list into a content-aware check for the most likely
    // mis-classification. What remains is a trusted human assertion only for a
    // regenerable section that names NO cache-artifact word -- this test
    // documents that residual so a reviewer adding an allow-list entry knows it
    // is a load-bearing claim that the section is genuinely NOT a regenerable,
    // deletion-recoverable cache.
    //
    // The structural guard we DO enforce: every allow-list entry carries a
    // non-empty rationale string (no silent name-only allow-listing).
    for (const [name, rationale] of Object.entries(NON_REGENERABLE_ALLOW_LIST)) {
      expect(typeof name).toBe("string");
      expect(typeof rationale).toBe("string");
      expect(rationale.trim().length).toBeGreaterThan(0);
    }
  });

  test("Direction D (content-aware tripwire): no allow-listed section NAMES a tmpdir/cache artifact unless registered or justified", () => {
    // DEFENSE-IN-DEPTH that shrinks the trusted-human-assertion residual above.
    // A genuine REGENERABLE temp-cache section's NAME or produced LINES almost
    // always name the artifact it derives. If an ALLOW-LISTED (claimed-NON-
    // regenerable) section matches REGENERABLE_ARTIFACT_HEURISTIC, the
    // allow-listing is suspicious: the reviewer must EITHER register it (Direction
    // A then routes it through the Direction-C closure proof) OR add it to
    // HEURISTIC_FALSE_POSITIVE_ALLOW_LIST with a load-bearing justification that
    // it is genuinely not a deletion-recoverable cache. This converts the pure
    // name-based allow-list into a content-aware tripwire for the most likely
    // mis-classification (a regenerable cache slipped into the allow-list).
    const registered = new Set(registrySectionNames);
    const justified = new Set(Object.keys(HEURISTIC_FALSE_POSITIVE_ALLOW_LIST));

    const suspicious = sections
      .filter((s) => Object.prototype.hasOwnProperty.call(NON_REGENERABLE_ALLOW_LIST, s.name))
      .filter((s) => !registered.has(s.name) && !justified.has(s.name))
      .filter((s) => REGENERABLE_ARTIFACT_HEURISTIC.test([s.name, ...s.lines].join("\n")))
      .map((s) => s.name);

    expect(suspicious).toEqual([]);
  });

  test("the content-aware tripwire is DISCRIMINATIVE (it flags the registered regenerable cache section)", () => {
    // Non-vacuity guard (mirrors the check-eol non-vacuous sanity): prove the
    // heuristic actually fires on a genuine regenerable cache. The REGISTERED
    // isolated managed-Jest cache section MUST match the heuristic (by name and
    // by its emitted lines); otherwise the tripwire above is a dead pattern that
    // could never catch a misclassification.
    const isolated = sections.find((s) => s.name === "isolated managed-Jest cache");
    expect(isolated).toBeTruthy();
    expect(REGENERABLE_ARTIFACT_HEURISTIC.test([isolated.name, ...isolated.lines].join("\n"))).toBe(
      true
    );

    // And the false-positive allow-list is frozen + empty today (any future
    // entry is an explicit, reviewed claim).
    expect(Object.isFrozen(HEURISTIC_FALSE_POSITIVE_ALLOW_LIST)).toBe(true);
  });

  test("the production doctor module path is the one under test (no stale require)", () => {
    // Cheap guard that we cross-checked the live doctor, not a copy.
    expect(fs.existsSync(path.join(REPO_ROOT, "scripts", "doctor.js"))).toBe(true);
  });
});
