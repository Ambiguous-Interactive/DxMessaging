/**
 * @fileoverview POLICY GUARD: de-institutionalize manual recourse for the
 * regenerable isolated managed-Jest cache. A corrupt REGENERABLE artifact must
 * be AUTO-HEALED; a manual `rm` one-liner must never be the advertised
 * resolution. This pure source-scan (no spawn -- a fast parser-class test)
 * enforces:
 *
 *   1. scripts/doctor.js checkIsolatedJestCache contains NO "Manual reset"
 *      literal at all (the lines are deleted; the doctor stays read-only and
 *      points at the automated heal instead).
 *   2. In ANY scanned source file (a FULL recursive scripts/**\/*.js walk, every
 *      .llm/**\/*.md, AND every docs/**\/*.md), IF the manual
 *      rmSync(...dxmessaging-managed-jest...) one-liner appears, THEN an
 *      automated-repair reference appears in the SAME file. This lets the
 *      line-capped skill files stay untouched while concentrating the
 *      requirement where the rm string actually lives -- and CLOSES the
 *      reintroduction hole (the category could previously be re-added in any
 *      scripts/*.js file other than two hardcoded ones, or in any docs/ runbook
 *      outside .llm/, with all tests still green).
 *
 * Mirrors the sibling policy scans (no-testrunner-injection-policy.test.js
 * Policy 7): a recursive `listFiles(scripts/, *.js)` walk that walk-skips
 * node_modules/.git/Temp, with a small ALLOWED whitelist (rationale per entry)
 * for files that legitimately CONTAIN the cache-dir rm string for a reason
 * OTHER than advertising it as the resolution (the registry's own guarded rm
 * logic; the wrapper that owns the real isolated-cache reset; the test fixtures
 * that plant the string as data).
 */

"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

const WALK_SKIP_DIRS = new Set(["node_modules", ".git", ".venv", "__pycache__", "Temp"]);

// The raw, manual, cross-platform rm one-liner for the isolated cache. Matching
// is structural (the os.tmpdir() join + the cache dir name) so reflowed
// whitespace still matches.
const MANUAL_RM_NEEDLE = "dxmessaging-managed-jest";
function looksLikeManualRm(content) {
  // A line that BOTH constructs the rm AND names the cache dir AND uses rmSync.
  return /rmSync\([^\n]*dxmessaging-managed-jest/.test(content);
}

// Any of these in the SAME file satisfies the "automated reference present"
// requirement.
const AUTOMATED_REFERENCES = [
  "repair:node-tooling",
  "repair-node-tooling.js",
  "run-managed-jest.js --version",
  "isolated-managed-jest-cache", // registry repair id
  "agentic preflight",
  "auto-cleared",
  "auto-heal"
];
function hasAutomatedReference(content) {
  return AUTOMATED_REFERENCES.some((ref) => content.includes(ref));
}

/**
 * Files that legitimately contain a `rmSync(...dxmessaging-managed-jest...)`
 * shape for a reason OTHER than advertising it to a human as THE resolution, so
 * the "manual rm => automated reference present" offender rule is waived for
 * them (mirrors the sibling no-testrunner-injection-policy.test.js PERMITTED_PATHS
 * allow-list discipline: every entry carries a rationale and Policy 8-style
 * existence sanity below proves the slot is live). Stored as repo-relative
 * POSIX-style strings (path.join canonicalizes per platform on read).
 *
 * NB: today NONE of these actually trip looksLikeManualRm (the registry +
 * wrapper rm the cache via a VARIABLE, not an inline cache-dir literal on the
 * rmSync line; the test fixtures that DO embed the literal also reference the
 * automated heal so they pass the rule anyway). The whitelist is defense in
 * depth: it pins WHO is allowed to own the raw cache-dir rm logic so a future
 * refactor that inlines the literal into the registry/wrapper guard cannot be
 * mistaken for a reintroduced manual-recourse advertisement.
 */
const MANUAL_RM_OFFENDER_ALLOW_LIST = new Set([
  // The automated healer itself: it owns the guarded, path-checked rm of corrupt
  // isolated-cache dirs. Its rm IS the automated remediation, not a manual step.
  path.join("scripts", "lib", "regenerable-cache-registry.js"),
  // The managed-Jest wrapper: owns attemptIsolatedCacheReset /
  // removeDirIfStrictDescendant, the reactive isolated-cache reset the healer
  // reuses. Again automated remediation, not a human instruction.
  path.join("scripts", "run-managed-jest.js"),
  // This policy test embeds the rm-string-as-data (MANUAL_RM_NEEDLE / the
  // looksLikeManualRm regex) to DETECT offenders; it is not advertising it.
  path.join("scripts", "__tests__", "no-manual-regenerable-recourse-policy.test.js")
]);

function isAllowed(repoRelativePath) {
  return MANUAL_RM_OFFENDER_ALLOW_LIST.has(repoRelativePath);
}

/**
 * Recursive file walk shared by the markdown and scripts scans, mirroring the
 * sibling no-testrunner-injection-policy.test.js listFiles: skip
 * node_modules/.git/Temp (and the other WALK_SKIP_DIRS), collect files matching
 * the predicate.
 */
function listFiles(dir, predicate, acc) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return acc;
  }
  for (const entry of entries) {
    if (entry.isDirectory()) {
      if (WALK_SKIP_DIRS.has(entry.name)) {
        continue;
      }
      listFiles(path.join(dir, entry.name), predicate, acc);
      continue;
    }
    if (!entry.isFile()) {
      continue;
    }
    const abs = path.join(dir, entry.name);
    if (predicate(abs)) {
      acc.push(abs);
    }
  }
  return acc;
}

function isMarkdown(abs) {
  return abs.endsWith(".md");
}

function isJs(abs) {
  return abs.endsWith(".js");
}

// FULL recursive scripts/**/*.js walk (closes the per-file hole that previously
// only covered doctor.js + jest-error-decoder.js) + every .llm and docs
// markdown file (closes the doc hole outside .llm/). The doctor + decoder remain
// covered because they live under scripts/.
function collectScannedSourceFiles() {
  return [
    ...listFiles(path.join(REPO_ROOT, "scripts"), isJs, []),
    ...listFiles(path.join(REPO_ROOT, ".llm"), isMarkdown, []),
    ...listFiles(path.join(REPO_ROOT, "docs"), isMarkdown, [])
  ];
}

describe("no manual recourse for the regenerable isolated-Jest cache", () => {
  test("doctor.js checkIsolatedJestCache contains NO 'Manual reset' literal", () => {
    const doctorSrc = fs.readFileSync(path.join(REPO_ROOT, "scripts", "doctor.js"), "utf8");
    const fnStart = doctorSrc.indexOf("function checkIsolatedJestCache");
    expect(fnStart).toBeGreaterThan(-1);
    // Bound the slice to the next top-level function so we only assert about
    // checkIsolatedJestCache's body.
    const after = doctorSrc.slice(fnStart);
    const nextFn = after.indexOf("\nfunction ", 1);
    const body = nextFn > -1 ? after.slice(0, nextFn) : after;
    expect(body).not.toContain("Manual reset");
    // It MUST instead reference the automated heal.
    expect(hasAutomatedReference(body)).toBe(true);
  });

  test("every scanned source file (scripts/**/*.js + .llm + docs markdown) with the manual rm one-liner ALSO references the automated heal", () => {
    const targets = collectScannedSourceFiles();

    const offenders = [];
    for (const file of targets) {
      const repoRelative = path.relative(REPO_ROOT, file);
      if (isAllowed(repoRelative)) {
        continue;
      }
      let content;
      try {
        content = fs.readFileSync(file, "utf8");
      } catch {
        continue;
      }
      if (!content.includes(MANUAL_RM_NEEDLE)) {
        continue;
      }
      if (looksLikeManualRm(content) && !hasAutomatedReference(content)) {
        offenders.push(repoRelative);
      }
    }

    expect(offenders).toEqual([]);
  });

  test("the scan actually covers the canonical files (non-vacuity: doctor.js, jest-error-decoder.js, the runbook are all in scope)", () => {
    // Guard against a walk regression silently emptying the target set (which
    // would make the offender test vacuously pass). Assert the recursive
    // collector reaches every file that previously had to be hardcoded, PLUS the
    // docs runbook that was the unguarded reintroduction vector.
    const targets = collectScannedSourceFiles().map((f) => path.relative(REPO_ROOT, f));
    expect(targets).toContain(path.join("scripts", "doctor.js"));
    expect(targets).toContain(path.join("scripts", "lib", "jest-error-decoder.js"));
    expect(targets).toContain(path.join("docs", "runbooks", "windows-partial-extract.md"));
    // And the walk is broad: a recursive scripts/**/*.js scan finds far more
    // than the two files the previous hardcoded list covered.
    const jsCount = targets.filter((p) => p.endsWith(".js")).length;
    expect(jsCount).toBeGreaterThan(50);
  });

  test("MANUAL_RM_OFFENDER_ALLOW_LIST sanity: every whitelisted file exists and is genuinely relevant to the cache-rm policy (no dead/over-broad exemption)", () => {
    // Mirrors no-testrunner-injection-policy.test.js Policy 8: a dead or
    // irrelevant allow-list entry is a silent hole. Each whitelisted file MUST
    // exist AND must genuinely reference the cache dir (MANUAL_RM_NEEDLE) -- so
    // the exemption can only ever cover a file that actually participates in the
    // isolated-cache rm story (the automated healer, the wrapper that owns the
    // reactive reset, or this detection test), never an unrelated file silently
    // exempted from the scan.
    for (const repoRelative of MANUAL_RM_OFFENDER_ALLOW_LIST) {
      const abs = path.join(REPO_ROOT, repoRelative);
      expect(fs.existsSync(abs)).toBe(true);
      const content = fs.readFileSync(abs, "utf8");
      expect(content.includes(MANUAL_RM_NEEDLE)).toBe(true);
    }
  });

  test("jest-error-decoder CORRUPT_ISOLATED_CACHE leads with the automated entrypoint, not the rm", () => {
    const decoder = require("../lib/jest-error-decoder");
    const entry = decoder.PATTERNS.find((p) => p.kind === "CORRUPT_ISOLATED_CACHE");
    expect(entry).toBeTruthy();
    const commands = entry.repairCommands;
    // First command is the automated heal; the rm (if present) is NOT first.
    expect(commands[0]).toBe("node scripts/repair-node-tooling.js");
    const rmIndex = commands.findIndex((c) => /rmSync\(.*dxmessaging-managed-jest/.test(c));
    if (rmIndex !== -1) {
      expect(rmIndex).toBeGreaterThan(0); // demoted below the automated path
      expect(commands[rmIndex]).toContain("last resort");
    }
  });

  test("NO decoder pattern leads with the raw rmSync one-liner (per-pattern generalization)", () => {
    // GENERALIZED GUARD (closes the per-pattern hole): the previous assertion
    // only pinned CORRUPT_ISOLATED_CACHE. If someone later adds a bare
    // rmSync(...dxmessaging-managed-jest...) as the FIRST command of a DIFFERENT
    // pattern (e.g. MISSING_TEST_RUNNER, which today leads with 'npm ci'), neither
    // that assertion nor the same-file source scan would catch it. Iterate ALL
    // PATTERNS: any pattern whose repairCommands contains a raw rm one-liner must
    // place it AFTER index 0 and label it 'last resort'.
    const decoder = require("../lib/jest-error-decoder");
    const offenders = [];
    for (const pattern of decoder.PATTERNS) {
      const commands = Array.isArray(pattern.repairCommands) ? pattern.repairCommands : [];
      const rmIndex = commands.findIndex((c) =>
        /rmSync\([^\n]*dxmessaging-managed-jest/.test(String(c))
      );
      if (rmIndex === -1) {
        continue;
      }
      if (rmIndex === 0) {
        offenders.push(`${pattern.kind}: raw rm is the FIRST repair command`);
      }
      if (!String(commands[rmIndex]).includes("last resort")) {
        offenders.push(`${pattern.kind}: raw rm at index ${rmIndex} is not labeled 'last resort'`);
      }
    }
    expect(offenders).toEqual([]);
  });

  test("the jest-hook-robustness skill no longer teaches a 'Manual repair' step for the isolated cache", () => {
    const skillPath = path.join(
      REPO_ROOT,
      ".llm",
      "skills",
      "scripting",
      "jest-hook-robustness.md"
    );
    const content = fs.readFileSync(skillPath, "utf8");
    // The old "Manual repair (only after the wrapper has already failed twice)"
    // step must be gone.
    expect(content).not.toMatch(/Manual repair/i);
    // And the skill must point at the automated heal.
    expect(hasAutomatedReference(content)).toBe(true);
  });
});
