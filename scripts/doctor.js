#!/usr/bin/env node
/**
 * doctor.js
 *
 * Agentic preflight gate: a pure-Node, read-only diagnostic that audits the
 * tooling pre-push hooks depend on. The doctor is the primary signal that
 * lets an automated agent answer "is this branch safe to push?" before the
 * hooks ever fire, by inspecting:
 *
 *   1. node_modules freshness for the TOOL_SPECS that validate-node-tooling
 *      enforces (including jest-circus/runner -- the exact dependency whose
 *      partial install caused the MISSING_TEST_RUNNER stderr that motivated
 *      this phase of work).
 *   2. The isolated managed-Jest cache under os.tmpdir(): does it resolve and
 *      is any entry stale? This is a REGENERABLE tmpdir fallback consulted by
 *      run-managed-jest ONLY when local node_modules Jest is unhealthy, so a
 *      corrupt/partial cache is reported at most WARN (never a hard push-block):
 *      the bootstrap (repair-node-tooling) auto-clears it before the doctor
 *      runs, and when local Jest is healthy the fallback is provably never
 *      consulted (so EVERY branch caps at WARN on that run). The one case that
 *      stays FAIL is an unreadable cache root (EACCES/EIO -- a host fault, not
 *      regenerable corruption) WHEN local Jest is also unhealthy; a stray-file
 *      (ENOTDIR) root is regenerable and stays WARN (auto-healed).
 *   3. The shared EOL policy constants (CRLF/LF extension counts).
 *   4. The pre-commit YAML: hook counts, pre-push subset, cross-reference
 *      with the preflight:pre-push npm script.
 *   5. The hook performance budget via scoreConfig() from precommit-perf-score.
 *   6. Cross-platform sanity (platform, pwsh + bash availability,
 *      package.json line-ending).
 *   7. Working-tree state: untracked-and-unignored paths (mirrors the
 *      validate-untracked-policy preflight step) plus a soft warning for
 *      modified-but-unstaged files. Untracked paths fail; unstaged
 *      modifications warn.
 *   8. Changed documentation validator parity: changed Markdown and C# files
 *      are scanned with the same ASCII/code-pattern/prose validators used by
 *      the pre-commit markdown and staged-doc hooks.
 *
 * The module is read-only by design: every probe is a query, never a fix.
 * Each section is a separately exported function that takes a
 * dependency-injection options bag so tests can drive it with deterministic
 * fakes; the real runtime uses fs / child_process / os defaults.
 *
 * Exit code semantics:
 *   0 = every section returned "ok" or "warn"; the push is safe to attempt
 *   1 = at least one section returned "fail"; the push will almost certainly
 *       fail at hook time, so the agent should repair before invoking
 *       `npm run preflight:pre-push`.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const childProcess = require("child_process");
const { createRequire } = require("module");

const { TOOL_SPECS } = require("./validate-node-tooling");
const { STEPS: PREPUSH_PREFLIGHT_STEPS } = require("./run-prepush-preflight");
const eolPolicy = require("./lib/eol-policy");
const precommitYaml = require("./lib/precommit-yaml");

function stepText(step) {
  const command = step.command === process.execPath ? "node" : step.command;
  return [command, ...step.args].join(" ");
}

function resolvePrePushScriptForCoverage(script) {
  if (typeof script !== "string") {
    return "";
  }
  if (script.trim() === "node scripts/run-prepush-preflight.js") {
    return PREPUSH_PREFLIGHT_STEPS.map(stepText).join(" && ");
  }
  return script;
}
const precommitPerfScore = require("./lib/precommit-perf-score");
const shellCommand = require("./lib/shell-command");
const { isTruthyEnv } = require("./lib/jest-error-decoder");
const { ISOLATED_JEST_CACHE_ROOT, hasHealthyLocalJestInstall } = require("./run-managed-jest");
const { INTEGRITY_TARGETS, probeIntegrity } = require("./lib/node-modules-integrity");
const {
  probeDependencyVersionParity,
  formatDriftLines
} = require("./lib/dependency-version-parity");
const { runChangedDocValidators } = require("./validate-changed-docs");
const { findPython, probePreCommitModule } = require("./ensure-pre-commit");

const REPO_ROOT = path.resolve(__dirname, "..");
const REPO_REQUIRE = createRequire(path.join(REPO_ROOT, "package.json"));
const PACKAGE_JSON_PATH = path.join(REPO_ROOT, "package.json");
const PRECOMMIT_CONFIG_PATH = path.join(REPO_ROOT, ".pre-commit-config.yaml");

const STALE_CACHE_DAYS = 30;
const STALE_CACHE_MS = STALE_CACHE_DAYS * 24 * 60 * 60 * 1000;

// Hot-path trim: the native pre-push hook sets DXMSG_DOCTOR_FAST=1 for the
// doctor call ONLY, so the two redundant git-walk sections (working-tree state
// + changed documentation) are skipped. Those exact validators run
// authoritatively later in the SAME push via preflight:pre-push
// (validate:untracked-policy + validate:changed-docs), so the fast path loses
// ZERO coverage; it removes a ~1.0s re-walk. Standalone `npm run doctor` (no
// env) keeps full coverage for fresh-clone / flaky-workstation triage.
const DOCTOR_FAST = isTruthyEnv(process.env.DXMSG_DOCTOR_FAST);

const ANSI_RESET = "\x1b[0m";
const ANSI_GREEN = "\x1b[32m";
const ANSI_YELLOW = "\x1b[33m";
const ANSI_RED = "\x1b[31m";
const ANSI_BOLD = "\x1b[1m";

const KNOWN_STATUSES = Object.freeze(["ok", "warn", "fail"]);

function statusRank(status) {
  if (status === "fail") return 2;
  if (status === "warn") return 1;
  return 0;
}

/**
 * Aggregate the worst section status. Sections that report an unrecognized
 * status string are fail-safe: the aggregator logs a warning and treats them
 * as "fail" so a typo in a new section cannot silently downgrade a real
 * failure to OK. Callers can override the warn sink in tests.
 *
 * @param {Array<{status: string, name?: string}>} sections
 * @param {{warn?: Function}} [options]
 * @returns {string}
 */
function aggregateStatus(sections, options = {}) {
  const warn = options.warn || ((message) => process.stderr.write(`${message}\n`));
  let worst = "ok";
  for (const section of sections) {
    let effectiveStatus = section.status;
    if (!KNOWN_STATUSES.includes(effectiveStatus)) {
      const name = section && section.name ? section.name : "(unnamed)";
      warn(
        `doctor: section '${name}' returned unrecognized status '${effectiveStatus}'; treating as 'fail'.`
      );
      effectiveStatus = "fail";
    }
    if (statusRank(effectiveStatus) > statusRank(worst)) {
      worst = effectiveStatus;
    }
  }
  return worst;
}

function statusToExitCode(status) {
  return status === "fail" ? 1 : 0;
}

/**
 * 1. Node modules freshness -- for each entry in validate-node-tooling's
 *    TOOL_SPECS, report a resolve + exists + load triple.
 */
function checkNodeModulesFreshness(options = {}) {
  const {
    requireResolveFn = (specifier) => REPO_REQUIRE.resolve(specifier),
    existsSyncFn = fs.existsSync,
    statSyncFn = fs.statSync,
    requireFn = (absPath) => REPO_REQUIRE(absPath),
    toolSpecs = TOOL_SPECS,
    integrityTargets = INTEGRITY_TARGETS,
    probeIntegrityFn = probeIntegrity,
    probeDependencyVersionParityFn = probeDependencyVersionParity,
    repoRoot = REPO_ROOT
  } = options;

  // Step 10: file-existence (and zero-byte) audits delegate to
  // probeIntegrity from scripts/lib/node-modules-integrity.js. The doctor
  // remains read-only; we only present the structured probe results in
  // the existing per-tool layout.
  const integrityResult = probeIntegrityFn({
    repoRoot,
    existsSyncFn,
    statSyncFn,
    targets: integrityTargets
  });
  const integrityMissingByTool = new Map();
  if (integrityResult && Array.isArray(integrityResult.missing)) {
    for (const entry of integrityResult.missing) {
      if (!integrityMissingByTool.has(entry.tool)) {
        integrityMissingByTool.set(entry.tool, []);
      }
      integrityMissingByTool.get(entry.tool).push(entry);
    }
  }

  const lines = [];
  let failed = 0;

  for (const tool of toolSpecs) {
    const issues = [];
    const checks = [];

    // Surface integrity probe entries scoped to this tool first.
    const integrityEntries = integrityMissingByTool.get(tool.name) || [];
    for (const entry of integrityEntries) {
      if (entry.reason === "missing") {
        issues.push(`missing required file ${entry.relPath}`);
      } else if (entry.reason === "empty") {
        issues.push(`${entry.relPath} is empty (size 0)`);
      } else {
        issues.push(`${entry.relPath} integrity probe: ${entry.reason}`);
      }
    }

    for (const requiredFile of tool.requiredFiles || []) {
      const absPath = path.join(repoRoot, ...requiredFile.split("/"));
      // Skip the existsSync check when integrity has already flagged
      // this same path; avoids duplicate lines in the report.
      const alreadyFlagged = integrityEntries.some((e) => e.relPath === requiredFile);
      if (alreadyFlagged) {
        continue;
      }
      if (!existsSyncFn(absPath)) {
        issues.push(`missing required file ${requiredFile}`);
      } else {
        checks.push(`exists: ${requiredFile}`);
      }
    }

    if (tool.entry) {
      const loadMode = tool.load || "exists";

      if (loadMode === "resolve") {
        // "resolve" entries are module specifiers (e.g. "jest-circus/runner"),
        // resolved against the repo's package.json. This mirrors
        // validate-node-tooling.js TOOL_SPECS semantics.
        try {
          const resolved = requireResolveFn(tool.entry);
          if (!resolved) {
            issues.push(`failed to resolve ${tool.entry}`);
          } else if (!existsSyncFn(resolved)) {
            issues.push(`resolved ${tool.entry} to missing path ${resolved}`);
          } else {
            checks.push(
              `resolved: ${tool.entry} -> ${path.relative(repoRoot, resolved) || resolved}`
            );
            try {
              requireFn(resolved);
              checks.push(`loaded: ${tool.entry}`);
            } catch (error) {
              const detail = error && error.message ? error.message : String(error);
              issues.push(`could not load ${tool.entry}: ${detail}`);
            }
          }
        } catch (error) {
          const detail = error && error.message ? error.message : String(error);
          issues.push(`could not resolve ${tool.entry}: ${detail}`);
        }
      } else if (loadMode === "require") {
        // "require" entries are repo-relative paths (e.g.
        // "node_modules/prettier/index.cjs") that must be made
        // absolute before require()-ing.
        const absEntry = path.join(repoRoot, ...tool.entry.split("/"));
        if (!existsSyncFn(absEntry)) {
          issues.push(`require entry missing on disk: ${tool.entry}`);
        } else {
          try {
            requireFn(absEntry);
            checks.push(`required: ${tool.entry}`);
          } catch (error) {
            const detail = error && error.message ? error.message : String(error);
            issues.push(`could not load ${tool.entry}: ${detail}`);
          }
        }
      } else if (loadMode === "import") {
        // ESM entries cannot be loaded synchronously here. The doctor
        // is a synchronous report by design (see file header); we
        // accept resolve+exists as the freshness signal for ESM
        // entries and rely on validate-node-tooling.js to do the
        // async import at preflight time.
        const absEntry = path.join(repoRoot, ...tool.entry.split("/"));
        if (!existsSyncFn(absEntry)) {
          issues.push(`import entry missing on disk: ${tool.entry}`);
        } else {
          checks.push(`import entry exists (async import deferred to preflight): ${tool.entry}`);
        }
      }
    }

    if (issues.length === 0) {
      lines.push(`  ok    ${tool.name}`);
      for (const check of checks) {
        lines.push(`          ${check}`);
      }
    } else {
      failed += 1;
      lines.push(`  FAIL  ${tool.name}`);
      for (const issue of issues) {
        lines.push(`          - ${issue}`);
      }
    }
  }

  // Dependency VERSION parity (offline): exact-pinned direct deps must match
  // package.json on disk + in the local lockfile; range pins must be
  // satisfied. Catches the gitignored-lockfile drift class that file-presence
  // probes structurally cannot (cspell-lib 10.0.0-vs-10.0.1). Reconcile is
  // `npm install` (not `npm ci`) because the lockfile is gitignored and may be
  // stale; `repair:node-tooling` does this automatically.
  let parityFailed = 0;
  try {
    const parity = probeDependencyVersionParityFn({ repoRoot });
    if (parity && parity.ok) {
      lines.push(`  ok    dependency version parity (${parity.checked} pinned deps)`);
    } else if (parity) {
      parityFailed = 1;
      lines.push("  FAIL  dependency version parity");
      for (const line of formatDriftLines(parity)) {
        lines.push(`          - ${line}`);
      }
      lines.push(
        "          Reconcile: run `npm install` (or `npm run repair:node-tooling`) to align " +
          "node_modules + the local lockfile with package.json."
      );
    }
  } catch (error) {
    const detail = error && error.message ? error.message : String(error);
    lines.push(`  ok    dependency version parity (probe skipped: ${detail})`);
  }

  if (failed > 0) {
    lines.push("");
    lines.push(
      "  Remediation: run `npm ci` to restore node_modules, then re-run `npm run doctor`."
    );
  }

  return {
    name: "node_modules freshness",
    status: failed === 0 && parityFailed === 0 ? "ok" : "fail",
    lines
  };
}

/**
 * 2. Isolated managed-Jest cache audit (REGENERABLE tmpdir fallback).
 *    - Lists each install directory under ISOLATED_JEST_CACHE_ROOT.
 *    - For each, verifies jest-circus/runner resolution from inside that dir.
 *    - Flags entries older than STALE_CACHE_DAYS as stale.
 *
 * SEVERITY CONTRACT (zero manual touch): this cache is a regenerable fallback
 * that run-managed-jest consults ONLY when the local node_modules Jest is
 * unhealthy. A corrupt/partial entry (missing or zero-byte runner, missing
 * package.json, or a stray FILE at the cache root) is auto-cleared by
 * `npm run repair:node-tooling` (native hooks + Stop hook + npm preflight) and
 * by the agentic preflight BEFORE this read-only probe ever runs; the next
 * managed-Jest run rebuilds it. So a corrupt regenerable entry -- including an
 * ENOTDIR stray-file root -- is reported at most WARN, never a push-blocking
 * FAIL, and the doctor emits NO manual rm command.
 *
 * RELEVANCE GATE: when local Jest is healthy the fallback is PROVABLY never
 * consulted on this run, so EVERY branch caps at WARN -- including a genuine
 * readdir/stat host read-error (EACCES/EIO), which is then purely
 * informational. The ONE case that stays FAIL is an unreadable cache root
 * (EACCES/EIO/other, NOT ENOTDIR) WHEN local Jest is ALSO unhealthy: only then
 * could the fallback actually be consulted, and a host fault that is not
 * regenerable corruption and not auto-deletable is a real blocker. The module
 * stays read-only (it never mutates the cache); the heal lives in
 * repair-node-tooling.
 */
function isResolvedRunnerNonEmpty(resolvedRunnerPath, statSyncFn) {
  try {
    return statSyncFn(resolvedRunnerPath).size > 0;
  } catch {
    // A stat throw on a path existsSync just confirmed (raced away) is treated
    // as a miss -> WARN, mirroring run-managed-jest's isUsableRunnerFile.
    return false;
  }
}

function checkIsolatedJestCache(options = {}) {
  const {
    readdirSyncFn = fs.readdirSync,
    statSyncFn = fs.statSync,
    existsSyncFn = fs.existsSync,
    createRequireFn = createRequire,
    hasHealthyLocalJestInstallFn = hasHealthyLocalJestInstall,
    cacheRoot = ISOLATED_JEST_CACHE_ROOT,
    nowMs = Date.now(),
    staleMs = STALE_CACHE_MS
  } = options;

  const lines = [];
  const autoHealNote =
    "A corrupt/partial cache is auto-cleared by `npm run repair:node-tooling` and the agentic preflight; the next managed-Jest run rebuilds it.";

  if (!existsSyncFn(cacheRoot)) {
    lines.push(`  ok    No isolated managed-Jest cache yet (${cacheRoot} does not exist).`);
    return {
      name: "isolated managed-Jest cache",
      status: "ok",
      lines
    };
  }

  let entries;
  try {
    entries = readdirSyncFn(cacheRoot, { withFileTypes: true });
  } catch (error) {
    const code = error && error.code;
    const detail = error && error.message ? error.message : String(error);
    const localJestHealthy = hasHealthyLocalJestInstallFn();

    // ENOTDIR: the cache ROOT is a stray FILE, not a directory (botched
    // extract, a `>` redirect, another tool). This IS regenerable corruption --
    // the bootstrap's healRegenerableCaches purges the stray file (strict-
    // equality-guarded) and the next managed-Jest run rebuilds the dir -- so it
    // is WARN, never a push-blocking FAIL, mirroring the partial-install WARN.
    if (code === "ENOTDIR") {
      lines.push(`  warn  Cache root ${cacheRoot} is a stray FILE, not a directory: ${detail}`);
      lines.push("");
      if (localJestHealthy) {
        lines.push(
          "  info  Local node_modules Jest is healthy; this fallback cache is not consulted on this run."
        );
      }
      lines.push(`  ${autoHealNote}`);
      return {
        name: "isolated managed-Jest cache",
        status: "warn",
        lines
      };
    }

    // Genuine host read-error (EACCES/EIO/other): not regenerable corruption and
    // not auto-deletable. Relevance gate (mirrors the runner-failure branch): if
    // local Jest is healthy the fallback is PROVABLY never consulted on this run
    // (pre-push.txt proof), so an unreadable root is purely informational ->
    // WARN. It stays FAIL only when local Jest is ALSO unhealthy: then the
    // fallback could actually be consulted and an unreadable root is a real,
    // non-auto-fixable blocker that must surface loudly.
    if (localJestHealthy) {
      lines.push(`  warn  Could not read cache root ${cacheRoot}: ${detail}`);
      lines.push("");
      lines.push(
        "  info  Local node_modules Jest is healthy; this fallback cache is not consulted on this run."
      );
      return {
        name: "isolated managed-Jest cache",
        status: "warn",
        lines
      };
    }
    lines.push(`  FAIL  Could not read cache root ${cacheRoot}: ${detail}`);
    return {
      name: "isolated managed-Jest cache",
      status: "fail",
      lines
    };
  }

  const installDirs = entries.filter((entry) => entry.isDirectory());

  if (installDirs.length === 0) {
    lines.push(`  ok    Cache root exists but contains no install dirs (${cacheRoot}).`);
    return {
      name: "isolated managed-Jest cache",
      status: "ok",
      lines
    };
  }

  let staleCount = 0;
  let runnerFailures = 0;

  for (const entry of installDirs) {
    const installDir = path.join(cacheRoot, entry.name);
    const packageJsonPath = path.join(installDir, "package.json");

    let mtimeMs = null;
    let ageDays = null;
    let stale = false;
    try {
      const stats = statSyncFn(installDir);
      mtimeMs = stats.mtimeMs;
      ageDays = Math.floor((nowMs - mtimeMs) / (24 * 60 * 60 * 1000));
      stale = nowMs - mtimeMs > staleMs;
    } catch (error) {
      // A per-dir stat throw is a corrupt/partial regenerable entry (the dir
      // raced away mid-walk, or is unreadable): WARN, auto-healed out-of-band.
      const detail = error && error.message ? error.message : String(error);
      lines.push(`  warn  ${entry.name}: stat failed (${detail})`);
      runnerFailures += 1;
      continue;
    }

    let runnerStatus = "ok";
    let runnerDetail = "";
    if (existsSyncFn(packageJsonPath)) {
      try {
        const isolatedRequire = createRequireFn(packageJsonPath);
        const resolved = isolatedRequire.resolve("jest-circus/runner");
        if (!resolved || !existsSyncFn(resolved)) {
          runnerStatus = "warn";
          runnerDetail = `jest-circus/runner resolved to missing path ${resolved || "(null)"}`;
          runnerFailures += 1;
        } else if (!isResolvedRunnerNonEmpty(resolved, statSyncFn)) {
          // Zero-byte runner.js (antivirus/Disk-Cleanup mid-write): present but
          // empty -> require() loads an empty module and Jest crashes. Mirror
          // node-modules-integrity's empty-file rule AND run-managed-jest's
          // isUsableRunnerFile predicate so the doctor flags exactly what the
          // healer/runner deem corrupt (regenerable -> WARN, auto-healed).
          runnerStatus = "warn";
          runnerDetail = `jest-circus/runner resolved to empty (size 0) path ${resolved}`;
          runnerFailures += 1;
        } else {
          runnerDetail = `jest-circus/runner -> ${resolved}`;
        }
      } catch (error) {
        runnerStatus = "warn";
        const message = error && error.message ? error.message : String(error);
        runnerDetail = `jest-circus/runner resolve threw: ${message}`;
        runnerFailures += 1;
      }
    } else {
      runnerStatus = "warn";
      runnerDetail = `package.json missing at ${packageJsonPath}`;
      runnerFailures += 1;
    }

    const ageLabel = ageDays === null ? "age=?" : `age=${ageDays}d`;
    const staleLabel = stale ? " STALE" : "";
    const tag = runnerStatus === "warn" ? "warn" : stale ? "warn" : "ok  ";
    lines.push(`  ${tag}  ${entry.name} (${ageLabel}${staleLabel})`);
    lines.push(`          ${runnerDetail}`);

    if (stale) {
      staleCount += 1;
    }
  }

  if (runnerFailures > 0) {
    // Corrupt/partial REGENERABLE entry: WARN, never a push-blocking FAIL. The
    // bootstrap auto-clears it before the doctor runs (see header). Relevance
    // gate: if local Jest is healthy the fallback is PROVABLY never consulted
    // (pre-push.txt proof), so the WARN is purely informational either way.
    lines.push("");
    if (hasHealthyLocalJestInstallFn()) {
      lines.push(
        "  info  Local node_modules Jest is healthy; this fallback cache is not consulted on this run."
      );
    }
    lines.push(`  ${autoHealNote}`);
    return {
      name: "isolated managed-Jest cache",
      status: "warn",
      lines
    };
  }

  if (staleCount > 0) {
    lines.push("");
    lines.push(`  ${autoHealNote}`);
    return {
      name: "isolated managed-Jest cache",
      status: "warn",
      lines
    };
  }

  return {
    name: "isolated managed-Jest cache",
    status: "ok",
    lines
  };
}

/**
 * 3. EOL policy stats -- pure stats output, no mutation. Imports the shared
 *    constants from scripts/lib/eol-policy.js so a future drift between this
 *    section and the live policy surfaces immediately.
 */
function checkEolPolicy(options = {}) {
  const {
    // readFileSyncFn is accepted for parity with the other section
    // signatures and so the test can prove the section does not read any
    // file (the policy is a static constants module).
    // eslint-disable-next-line no-unused-vars
    readFileSyncFn = fs.readFileSync,
    policy = eolPolicy
  } = options;

  const lines = [];
  const crlfExts = [...policy.crlfExts].sort();
  const lfExts = [...policy.lfExts].sort();
  const total = crlfExts.length + lfExts.length;

  lines.push(`  ok    EOL policy loaded from scripts/lib/eol-policy.js`);
  lines.push(`          CRLF extensions (${crlfExts.length}): ${crlfExts.join(", ")}`);
  lines.push(`          LF   extensions (${lfExts.length}): ${lfExts.join(", ")}`);
  lines.push(`          total tracked extensions: ${total}`);

  const overlapping = crlfExts.filter((ext) => policy.lfExts.has(ext));
  if (overlapping.length > 0) {
    lines.push(`  FAIL  Extensions appear in BOTH crlfExts and lfExts: ${overlapping.join(", ")}`);
    return {
      name: "EOL policy",
      status: "fail",
      lines
    };
  }

  return {
    name: "EOL policy",
    status: "ok",
    lines
  };
}

/**
 * 4. Pre-commit config audit -- counts every hook, the pre-push subset, and
 *    cross-references with preflight:pre-push. Also tries to print
 *    `pre-commit --version` when the binary is on PATH.
 */
function checkPreCommitConfig(options = {}) {
  const {
    readFileSyncFn = fs.readFileSync,
    parsePrecommitYaml = precommitYaml,
    runCommandFn = defaultRunCommand,
    configPath = PRECOMMIT_CONFIG_PATH,
    packageJsonPath = PACKAGE_JSON_PATH,
    // Pre-resolved pre-commit --version output. When provided, the section
    // does not spawn a child process; this lets runDoctor consolidate the
    // version probes into a single shared spawn (see runDoctor + M1).
    preCommitVersionResult = null
  } = options;

  const lines = [];
  let failed = false;

  let configContent;
  try {
    configContent = readFileSyncFn(configPath, "utf8");
  } catch (error) {
    const detail = error && error.message ? error.message : String(error);
    return {
      name: "pre-commit config",
      status: "fail",
      lines: [`  FAIL  Could not read ${configPath}: ${detail}`]
    };
  }

  const configLines = configContent.replace(/\r\n/g, "\n").split("\n");
  const blocks = parsePrecommitYaml.findAllHookBlocks(configLines);

  const prePushHookIds = [];
  for (const block of blocks) {
    const stages = parsePrecommitYaml.extractStagesFromHookBlock(block);
    const effective = stages.length === 0 ? ["pre-commit"] : stages;
    if (effective.includes("pre-push")) {
      prePushHookIds.push(block.id);
    }
  }

  lines.push(
    `  ok    Parsed ${blocks.length} hook block(s) from ${path.relative(REPO_ROOT, configPath) || configPath}`
  );
  lines.push(
    `          pre-push hooks (${prePushHookIds.length}): ${prePushHookIds.join(", ") || "(none)"}`
  );

  const versionResult =
    preCommitVersionResult !== null
      ? preCommitVersionResult
      : runCommandFn("pre-commit", ["--version"]);
  if (versionResult && versionResult.status === 0 && typeof versionResult.stdout === "string") {
    lines.push(`          pre-commit --version: ${versionResult.stdout.trim()}`);
  } else {
    const python = findPython({ runCommandFn });
    const moduleResult = probePreCommitModule(python, { runCommandFn });
    if (moduleResult) {
      lines.push(`          python -m pre_commit --version: ${moduleResult.version}`);
    } else if (versionResult && versionResult.error && versionResult.error.code === "ENOENT") {
      // Without either pre-commit on PATH or the Python module,
      // `npm run preflight:pre-push` cannot run its final pre-commit step.
      // The doctor's purpose is to predict whether a push will succeed; a
      // missing pre-commit runner makes that impossible, so this is FAIL.
      lines.push(
        "  FAIL  pre-commit --version: not on PATH (ENOENT), and python -m pre_commit is unavailable. preflight:pre-push cannot run without it."
      );
      lines.push(
        "          Run `npm run repair:pre-commit` to auto-install the pinned pre-commit version, then re-run `npm run doctor`."
      );
      failed = true;
    } else {
      const detail =
        versionResult && versionResult.error && versionResult.error.message
          ? versionResult.error.message
          : `status=${versionResult ? versionResult.status : "null"}`;
      lines.push(
        `  FAIL  pre-commit --version: not available (${detail}), and python -m pre_commit is unavailable. preflight:pre-push cannot run without it.`
      );
      failed = true;
    }
  }

  let packageJsonContent;
  try {
    packageJsonContent = readFileSyncFn(packageJsonPath, "utf8");
  } catch (error) {
    const detail = error && error.message ? error.message : String(error);
    lines.push(`  FAIL  Could not read ${packageJsonPath}: ${detail}`);
    failed = true;
    packageJsonContent = "";
  }

  let parsedPackageJson = null;
  if (packageJsonContent) {
    try {
      parsedPackageJson = JSON.parse(packageJsonContent);
    } catch (error) {
      const detail = error && error.message ? error.message : String(error);
      lines.push(`  FAIL  package.json is not valid JSON: ${detail}`);
      failed = true;
    }
  }

  const scripts = (parsedPackageJson && parsedPackageJson.scripts) || {};
  const preflightPrePush = scripts["preflight:pre-push"];
  if (typeof preflightPrePush === "string" && preflightPrePush.length > 0) {
    lines.push(`          preflight:pre-push script present`);
    const resolvedPreflightPrePush = resolvePrePushScriptForCoverage(preflightPrePush);
    if (
      !/(?:pre-commit|node\s+scripts\/ensure-pre-commit\.js)\s+run\s+--hook-stage\s+pre-push\s+--all-files/.test(
        resolvedPreflightPrePush
      )
    ) {
      lines.push(
        `  FAIL  preflight:pre-push does not invoke the full pre-push hook set through pre-commit`
      );
      failed = true;
    } else {
      lines.push(
        `          coverage: invokes the full pre-push hook set (covers all ${prePushHookIds.length} pre-push hooks)`
      );
    }
  } else {
    lines.push(`  FAIL  package.json scripts.preflight:pre-push is missing or empty`);
    failed = true;
  }

  return {
    name: "pre-commit config",
    status: failed ? "fail" : "ok",
    lines
  };
}

/**
 * 5. Hook-perf budget -- delegates entirely to scoreConfig() from
 *    precommit-perf-score.js. No reimplementation; we just summarize.
 */
function checkHookPerfBudget(options = {}) {
  const {
    readFileSyncFn = fs.readFileSync,
    scoreConfigFn = precommitPerfScore.scoreConfig,
    budget = precommitPerfScore.PERF_BUDGET,
    perHookCeiling = precommitPerfScore.PER_HOOK_CEILING,
    configPath = PRECOMMIT_CONFIG_PATH
  } = options;

  const lines = [];

  let content;
  try {
    content = readFileSyncFn(configPath, "utf8");
  } catch (error) {
    const detail = error && error.message ? error.message : String(error);
    return {
      name: "hook-perf budget",
      status: "fail",
      lines: [`  FAIL  Could not read ${configPath}: ${detail}`]
    };
  }

  const result = scoreConfigFn(content);
  const total = result.totalScore || 0;

  let status = "ok";
  const indicator = total > budget ? "FAIL" : "ok  ";
  lines.push(
    `  ${indicator}  pre-commit perf score: ${total} (budget=${budget}, per-hook ceiling=${perHookCeiling})`
  );

  if (total > budget) {
    status = "fail";
    lines.push(`          score exceeds whole-pipeline budget`);
  }

  if (result.perHookViolations && result.perHookViolations.length > 0) {
    status = "fail";
    for (const violation of result.perHookViolations) {
      lines.push(
        `  FAIL  per-hook ceiling violation: ${violation.id} scored ${violation.score} > ${violation.ceiling}`
      );
    }
  }

  if (result.rejections && result.rejections.length > 0) {
    status = "fail";
    for (const rejection of result.rejections) {
      lines.push(
        `  FAIL  rejected perf-allow directive on ${rejection.id} (line ${rejection.startLine}): ${rejection.error}`
      );
    }
  }

  if (result.allowList && result.allowList.length > 0) {
    lines.push(`          waivers in effect: ${result.allowList.length}`);
  }

  return {
    name: "hook-perf budget",
    status,
    lines
  };
}

/**
 * 6. Cross-platform sanity -- platform string, pwsh + bash availability,
 *    package.json line-ending check. The shells are diagnostic only: pwsh
 *    being absent on Linux is a warning, not a failure. Bash being absent
 *    on Windows is also a warning (Git Bash usually ships it). Only the
 *    package.json line-ending check is a hard failure.
 */
function checkCrossPlatformSanity(options = {}) {
  const {
    platformFn = () => process.platform,
    runCommandFn = defaultRunCommand,
    readFileSyncFn = fs.readFileSync,
    packageJsonPath = PACKAGE_JSON_PATH,
    shellEnv = process.env.SHELL || "",
    // Pre-resolved version probe results. When provided, the section
    // does not spawn child processes for these probes; runDoctor shares
    // the spawn output across header and section via this surface (M1).
    pwshVersionResult = null,
    bashVersionResult = null
  } = options;

  const lines = [];
  let failed = false;
  let warned = false;

  const platform = platformFn();
  lines.push(`  ok    process.platform: ${platform}`);
  lines.push(`          SHELL env: ${shellEnv || "(unset)"}`);

  const pwshResult =
    pwshVersionResult !== null ? pwshVersionResult : runCommandFn("pwsh", ["--version"]);
  if (pwshResult && pwshResult.status === 0 && typeof pwshResult.stdout === "string") {
    lines.push(`          pwsh:    ${pwshResult.stdout.trim().split("\n")[0]}`);
  } else if (pwshResult && pwshResult.error && pwshResult.error.code === "ENOENT") {
    lines.push(`          pwsh:    not installed (ENOENT) - acceptable on Linux/macOS`);
    if (platform === "win32") {
      warned = true;
    }
  } else {
    const detail =
      pwshResult && pwshResult.error && pwshResult.error.message
        ? pwshResult.error.message
        : `status=${pwshResult ? pwshResult.status : "null"}`;
    lines.push(`          pwsh:    not available (${detail})`);
    if (platform === "win32") {
      warned = true;
    }
  }

  const bashResult =
    bashVersionResult !== null ? bashVersionResult : runCommandFn("bash", ["--version"]);
  if (bashResult && bashResult.status === 0 && typeof bashResult.stdout === "string") {
    lines.push(`          bash:    ${bashResult.stdout.trim().split("\n")[0]}`);
  } else if (bashResult && bashResult.error && bashResult.error.code === "ENOENT") {
    lines.push(`          bash:    not installed (ENOENT)`);
    // On Linux/macOS bash absence is unusual but not a hard fail for the
    // doctor; pre-commit can still run hooks via system shells.
    warned = true;
  } else {
    const detail =
      bashResult && bashResult.error && bashResult.error.message
        ? bashResult.error.message
        : `status=${bashResult ? bashResult.status : "null"}`;
    lines.push(`          bash:    not available (${detail})`);
    warned = true;
  }

  let packageContent;
  try {
    packageContent = readFileSyncFn(packageJsonPath, "utf8");
  } catch (error) {
    const detail = error && error.message ? error.message : String(error);
    lines.push(`  FAIL  Could not read ${packageJsonPath} to check EOL: ${detail}`);
    return {
      name: "cross-platform sanity",
      status: "fail",
      lines
    };
  }

  if (packageContent.includes("\r\n")) {
    lines.push(
      `  FAIL  package.json contains CRLF line endings; policy requires LF (.json is in lfExts).`
    );
    failed = true;
  } else {
    lines.push(`  ok    package.json line endings: LF`);
  }

  return {
    name: "cross-platform sanity",
    status: failed ? "fail" : warned ? "warn" : "ok",
    lines
  };
}

/**
 * 7. Working-tree state -- mirrors the validate-untracked-policy preflight
 *    step so the doctor can predict whether `npm run preflight:pre-push` will
 *    even *start*. Runs `git status --porcelain` (NUL-terminated via -z for
 *    safety with weird filenames) and inspects each entry:
 *
 *      - Untracked paths ("??"): FAIL. validate-untracked-policy rejects
 *        these. The list of offending paths is included verbatim so an agent
 *        can either commit them or add them to .gitignore.
 *      - Modified-but-not-staged (" M", " D", " T", " R", " C"): WARN.
 *        preflight:pre-push does not fail on these, but flagging them helps
 *        the operator notice unintended drift.
 *      - Staged changes ("M ", "A ", "D ", etc.): OK. The push will carry
 *        these along.
 *      - Renames/copies ("R ", "C "): OK on the staged side; the destination
 *        path follows the arrow in -z output via a NUL pair.
 *      - Anything else: OK (status flags we do not inspect are still
 *        reported but never trigger fail/warn).
 *
 *    The `git add -N` intent-to-add state shows as " A" (staged-ish) in
 *    `git status --porcelain`, which falls into the "modified-but-not-staged"
 *    bucket -- it will warn, but it will NOT fail. This matches
 *    validate-untracked-policy semantics: `git ls-files --others
 *    --exclude-standard` does not list intent-to-add files.
 */
function checkWorkingTreeState(options = {}) {
  const { runCommandFn = defaultRunCommand, cwd = REPO_ROOT } = options;

  const lines = [];

  const result = runCommandFn("git", ["-c", "core.quotepath=false", "status", "--porcelain"], {
    cwd
  });

  if (result && result.error) {
    if (result.error.code === "ENOENT") {
      lines.push("  FAIL  git not found on PATH; cannot inspect working tree.");
      return {
        name: "working-tree state",
        status: "fail",
        lines
      };
    }
    const detail = result.error.message ? result.error.message : String(result.error);
    lines.push(`  FAIL  git status failed: ${detail}`);
    return {
      name: "working-tree state",
      status: "fail",
      lines
    };
  }

  if (!result || typeof result.status !== "number" || result.status !== 0) {
    const stderr = result && result.stderr ? String(result.stderr).trim() : "";
    const statusVal = result && typeof result.status === "number" ? result.status : "null";
    lines.push(`  FAIL  git status exited with status ${statusVal}: ${stderr || "no stderr"}`);
    return {
      name: "working-tree state",
      status: "fail",
      lines
    };
  }

  const stdoutText =
    typeof result.stdout === "string"
      ? result.stdout
      : result.stdout
        ? result.stdout.toString("utf8")
        : "";

  if (stdoutText.length === 0) {
    lines.push("  ok    Working tree is clean (no untracked, modified, or staged paths).");
    return {
      name: "working-tree state",
      status: "ok",
      lines
    };
  }

  // Each porcelain line: "XY path". Untracked is "?? path"; the first two
  // characters are the index (X) and worktree (Y) statuses respectively.
  // We split on \n (not \0) because we did not pass -z; weird filenames
  // would be quoted, but we already set core.quotepath=false to keep them
  // raw within their octet ranges. This matches validate-untracked-policy's
  // semantics for the relevant case (untracked paths from porcelain "??").
  const untracked = [];
  const modifiedUnstaged = [];
  const staged = [];

  const porcelainLines = stdoutText.replace(/\r\n/g, "\n").split("\n");
  for (const rawLine of porcelainLines) {
    if (rawLine.length === 0) {
      continue;
    }
    // Porcelain v1: positions 0 and 1 are the X (index) and Y (worktree)
    // codes; position 2 is a single space; positions 3+ are the path.
    const x = rawLine.charAt(0);
    const y = rawLine.charAt(1);
    const pathPart = rawLine.slice(3);

    if (x === "?" && y === "?") {
      untracked.push(pathPart);
      continue;
    }

    // X is a space means "no index entry" but the worktree differs from
    // HEAD, i.e. unstaged modification/deletion/etc. Y non-space and
    // non-"?" means worktree differs from index after the staged change.
    const hasStagedChange = x !== " " && x !== "?";
    const hasUnstagedChange = y !== " " && y !== "?";

    if (hasStagedChange) {
      staged.push(`${x}${y} ${pathPart}`);
    }
    if (hasUnstagedChange) {
      modifiedUnstaged.push(`${x}${y} ${pathPart}`);
    }
  }

  if (untracked.length > 0) {
    lines.push(
      `  FAIL  ${untracked.length} untracked-and-unignored path(s) (validate-untracked-policy will reject these):`
    );
    for (const entry of untracked) {
      lines.push(`          ?? ${entry}`);
    }
    lines.push("");
    lines.push(
      "  Remediation: commit each path, add it to .gitignore (and .npmignore if it should not ship)"
    );
    lines.push(
      "              with a one-line rationale, OR run `git add -N <path>` to mark intent-to-add"
    );
    lines.push(
      "              (intent-to-add files are NOT listed by `git ls-files --others --exclude-standard`)."
    );

    // Also include staged + modified-unstaged for situational awareness,
    // but only as informational lines below the FAIL diagnostic.
    if (staged.length > 0) {
      lines.push("");
      lines.push(`  info  ${staged.length} staged path(s) (will be included in the next commit):`);
      for (const entry of staged) {
        lines.push(`          ${entry}`);
      }
    }
    if (modifiedUnstaged.length > 0) {
      lines.push("");
      lines.push(`  info  ${modifiedUnstaged.length} modified-but-unstaged path(s):`);
      for (const entry of modifiedUnstaged) {
        lines.push(`          ${entry}`);
      }
    }

    return {
      name: "working-tree state",
      status: "fail",
      lines
    };
  }

  if (modifiedUnstaged.length > 0) {
    lines.push(
      `  warn  ${modifiedUnstaged.length} modified-but-unstaged path(s) (preflight does NOT fail on these, but the push will not carry them):`
    );
    for (const entry of modifiedUnstaged) {
      lines.push(`          ${entry}`);
    }
    if (staged.length > 0) {
      lines.push("");
      lines.push(`  info  ${staged.length} staged path(s) (will be included in the next commit):`);
      for (const entry of staged) {
        lines.push(`          ${entry}`);
      }
    }
    return {
      name: "working-tree state",
      status: "warn",
      lines
    };
  }

  // Only staged or otherwise-clean content -> OK.
  if (staged.length > 0) {
    lines.push(
      `  ok    ${staged.length} staged path(s), no untracked or unstaged-modification entries:`
    );
    for (const entry of staged) {
      lines.push(`          ${entry}`);
    }
  } else {
    lines.push(
      "  ok    Working tree is clean (porcelain returned non-empty but nothing actionable)."
    );
  }

  return {
    name: "working-tree state",
    status: "ok",
    lines
  };
}

function checkChangedDocumentation(options = {}) {
  const { runChangedDocValidatorsFn = runChangedDocValidators } = options;

  const lines = [];
  let result;
  try {
    result = runChangedDocValidatorsFn({ silent: true });
  } catch (error) {
    lines.push(`  FAIL  changed documentation validators failed to run: ${error.message}`);
    return {
      name: "changed documentation validators",
      status: "fail",
      lines
    };
  }

  const fileCount = result.files ? result.files.length : 0;
  const totalViolations = result.totalViolations || 0;
  if (fileCount === 0) {
    lines.push("  ok    No changed markdown or C# files to inspect.");
    return {
      name: "changed documentation validators",
      status: "ok",
      lines
    };
  }

  if (totalViolations === 0) {
    lines.push(
      `  ok    ${fileCount} changed documentation file(s) passed ASCII/code/prose validators.`
    );
    return {
      name: "changed documentation validators",
      status: "ok",
      lines
    };
  }

  lines.push(
    `  FAIL  ${totalViolations} documentation validator violation(s) across ${fileCount} changed file(s).`
  );
  lines.push(
    "  Remediation: run `npm run validate:changed-docs`, fix the reported files, then re-run `npm run doctor`."
  );
  return {
    name: "changed documentation validators",
    status: "fail",
    lines
  };
}

/**
 * Default runCommandFn used by section probes. Wraps spawnSync via the
 * cross-platform helper so npm/npx shimming on Windows is consistent with
 * the rest of the scripts/. Returns a normalized object with status/error/
 * stdout/stderr keys regardless of whether the underlying spawn succeeded.
 */
function defaultRunCommand(command, args, options = {}) {
  const spawnOptions = {
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
    ...options
  };

  let result;
  try {
    result = shellCommand.spawnPlatformCommandSync(
      command,
      args,
      spawnOptions,
      childProcess.spawnSync
    );
  } catch (error) {
    return { status: null, error, stdout: "", stderr: "" };
  }

  if (!result) {
    return { status: null, error: new Error("spawnSync returned null"), stdout: "", stderr: "" };
  }

  return {
    status: typeof result.status === "number" ? result.status : null,
    error: result.error || null,
    stdout:
      typeof result.stdout === "string"
        ? result.stdout
        : result.stdout
          ? result.stdout.toString("utf8")
          : "",
    stderr:
      typeof result.stderr === "string"
        ? result.stderr
        : result.stderr
          ? result.stderr.toString("utf8")
          : ""
  };
}

/**
 * Spawn every external version probe the doctor needs exactly once and
 * return a memoized lookup. Sections + the header banner consume the
 * pre-resolved entries through their `*VersionResult` options so we do not
 * fork the same subprocess multiple times per doctor run (M1).
 *
 * @param {{runCommandFn?: Function}} [options]
 * @returns {{npm: object, preCommit: object, pwsh: object, bash: object}}
 */
function probeToolVersions(options = {}) {
  const runCommandFn = options.runCommandFn || defaultRunCommand;
  return {
    npm: runCommandFn("npm", ["--version"]),
    preCommit: runCommandFn("pre-commit", ["--version"]),
    pwsh: runCommandFn("pwsh", ["--version"]),
    bash: runCommandFn("bash", ["--version"])
  };
}

/**
 * Run every doctor section in sequence, returning the aggregated structure.
 * No I/O on the result -- printing is the caller's job (see main()).
 *
 * @param {object} [opts]
 * @param {object} [opts.overrides] Per-section dependency-injection overrides
 *   keyed by the section function name. Tests use this to drive the whole
 *   doctor in one call with deterministic fakes.
 * @param {object} [opts.versionProbes] Pre-resolved version-probe results
 *   (see probeToolVersions). When omitted, every section spawns its own
 *   probes; tests can supply deterministic fakes here.
 */
/**
 * Build a stable "skipped" section so the report SHAPE stays constant (same
 * section names, same ordering, still visible in the footer) even when the
 * hot-path fast mode elides the section's git walk.
 *
 * Status is "ok" (NOT a bespoke "info" string): aggregateStatus is fail-safe
 * and treats any status outside KNOWN_STATUSES as "fail", which would WRONGLY
 * block the push. The "skipped" intent is carried by the line text; the OK
 * status keeps it aggregation-neutral. The redundant validators still run
 * authoritatively in preflight:pre-push on the same push.
 */
function skippedSection(name) {
  return {
    name,
    status: "ok",
    lines: ["  ok    skipped (covered by preflight:pre-push validators)."]
  };
}

function runDoctor({ overrides = {}, versionProbes = null, fast = DOCTOR_FAST } = {}) {
  const preCommitOverrides = { ...(overrides.checkPreCommitConfig || {}) };
  const crossPlatformOverrides = { ...(overrides.checkCrossPlatformSanity || {}) };

  if (versionProbes) {
    if (preCommitOverrides.preCommitVersionResult === undefined) {
      preCommitOverrides.preCommitVersionResult = versionProbes.preCommit;
    }
    if (crossPlatformOverrides.pwshVersionResult === undefined) {
      crossPlatformOverrides.pwshVersionResult = versionProbes.pwsh;
    }
    if (crossPlatformOverrides.bashVersionResult === undefined) {
      crossPlatformOverrides.bashVersionResult = versionProbes.bash;
    }
  }

  // Hot-path fast mode: SKIP the two redundant git-walk sections
  // (checkWorkingTreeState ~668ms + checkChangedDocumentation ~360ms). They are
  // re-run authoritatively in the SAME push by preflight:pre-push
  // (validate:untracked-policy + validate:changed-docs), so eliding them here
  // loses ZERO coverage. We emit a visible "skipped" info section for each so
  // the report shape is stable AND we never even invoke their git runners.
  const workingTreeSection = fast
    ? skippedSection("working-tree state")
    : checkWorkingTreeState(overrides.checkWorkingTreeState || {});
  const changedDocsSection = fast
    ? skippedSection("changed documentation validators")
    : checkChangedDocumentation(overrides.checkChangedDocumentation || {});

  const sections = [
    checkNodeModulesFreshness(overrides.checkNodeModulesFreshness || {}),
    checkIsolatedJestCache(overrides.checkIsolatedJestCache || {}),
    checkEolPolicy(overrides.checkEolPolicy || {}),
    checkPreCommitConfig(preCommitOverrides),
    checkHookPerfBudget(overrides.checkHookPerfBudget || {}),
    checkCrossPlatformSanity(crossPlatformOverrides),
    workingTreeSection,
    changedDocsSection
  ];

  const overall = aggregateStatus(sections);
  return {
    status: statusToExitCode(overall),
    overall,
    sections
  };
}

function decorateStatusLabel(status, useColor) {
  const label = status === "fail" ? "[FAIL]" : status === "warn" ? "[WARN]" : "[ OK ]";
  if (!useColor) {
    return label;
  }
  if (status === "fail") return `${ANSI_RED}${label}${ANSI_RESET}`;
  if (status === "warn") return `${ANSI_YELLOW}${label}${ANSI_RESET}`;
  return `${ANSI_GREEN}${label}${ANSI_RESET}`;
}

/**
 * Decide whether to emit ANSI color escapes. Precedence (highest first):
 *
 *   1. NO_COLOR present and non-empty -> never color. Per the official
 *      NO_COLOR spec (https://no-color.org/): "when present, regardless
 *      of its value, prevents the addition of ANSI color." That means
 *      NO_COLOR="0", NO_COLOR="false", NO_COLOR="off" all suppress color
 *      because they are non-empty strings. Only NO_COLOR unset or set to
 *      an empty string falls through.
 *   2. FORCE_COLOR -> follows Node.js convention. FORCE_COLOR="0" means
 *      "explicitly disable" (falls through to lower precedence rules);
 *      any other non-empty value means "force enable" (overrides CI and
 *      isTTY). This matches `chalk`'s and Node's documented behavior.
 *   3. CI truthy (per isTruthyEnv) -> never color. Many CI viewers strip
 *      escapes; `CI=""`, `CI="0"`, `CI="false"`, `CI="no"`, `CI="off"`
 *      are treated as "not in CI".
 *   4. Otherwise: color iff stdout is a TTY.
 *
 * Defaults pull from `process.env` for convenience at the CLI entry
 * point. Tests SHOULD supply all four parameters explicitly so a leaked
 * CI=true (or any other env var) cannot influence the outcome; in
 * practice the function still works when called with a subset because
 * the destructuring defaults read `process.env` directly. See
 * doctor.test.js for the data-driven truth table that covers both
 * fully-explicit and env-defaulted call shapes.
 *
 * `isForceColorOn` whitespace handling: a single space, a leading tab,
 * and `" 0 "` are all currently treated as ENABLED -- the function
 * trims and compares against the literal string "0", so anything that
 * does not trim to exactly "0" turns color on. Locked by truth-table
 * rows in doctor.test.js.
 */
function isNoColorPresent(value) {
  return typeof value === "string" && value.length > 0;
}

function isForceColorOn(value) {
  if (value == null) {
    return false;
  }
  const stringValue = String(value);
  if (stringValue.length === 0) {
    return false;
  }
  // Node convention: FORCE_COLOR="0" disables. Other non-empty values enable.
  // Whitespace handling: trim before the "0" comparison so " 0 " also
  // disables; pure-whitespace strings like " " and "\t" are non-empty
  // but do not equal "0" after trim, so they ENABLE color. See the
  // truth-table rows in doctor.test.js (M2).
  return stringValue.trim() !== "0";
}

function shouldUseColor({
  envCi = process.env.CI,
  envNoColor = process.env.NO_COLOR,
  envForceColor = process.env.FORCE_COLOR,
  isTTY = Boolean(process.stdout && process.stdout.isTTY)
} = {}) {
  if (isNoColorPresent(envNoColor)) {
    return false;
  }
  if (isForceColorOn(envForceColor)) {
    return true;
  }
  if (isTruthyEnv(envCi)) {
    return false;
  }
  return isTTY;
}

function formatHeaderBanner({
  repoRoot = REPO_ROOT,
  nodeVersion = process.version,
  platform = process.platform,
  arch = process.arch,
  shellEnv = process.env.SHELL || "",
  npmVersion = null,
  // Pre-resolved npm --version probe result (see probeToolVersions). When
  // provided, the banner does not spawn its own child process.
  npmVersionResult = null,
  runCommandFn = defaultRunCommand
} = {}) {
  let resolvedNpmVersion = npmVersion;
  if (resolvedNpmVersion === null) {
    const result =
      npmVersionResult !== null ? npmVersionResult : runCommandFn("npm", ["--version"]);
    if (result && result.status === 0 && typeof result.stdout === "string") {
      resolvedNpmVersion = result.stdout.trim();
    } else {
      resolvedNpmVersion = "(unknown)";
    }
  }

  const horizontal = "=".repeat(72);
  return [
    horizontal,
    "DxMessaging doctor: read-only preflight diagnostic",
    horizontal,
    `Repo:    ${repoRoot}`,
    `Node:    ${nodeVersion}`,
    `npm:     ${resolvedNpmVersion}`,
    `OS:      ${platform}/${arch}`,
    `Shell:   ${shellEnv || "(unset)"}`,
    horizontal
  ].join("\n");
}

function formatSection(section, useColor) {
  const horizontal = "-".repeat(72);
  const label = decorateStatusLabel(section.status, useColor);
  const titleLine = `${label} ${section.name}`;
  const decoratedTitle = useColor ? `${ANSI_BOLD}${titleLine}${ANSI_RESET}` : titleLine;
  const out = [horizontal, decoratedTitle];
  for (const line of section.lines) {
    out.push(line);
  }
  return out.join("\n");
}

function formatFooter(sections, overall, useColor) {
  const horizontal = "=".repeat(72);
  const lines = [horizontal, "Summary:"];
  for (const section of sections) {
    const label = decorateStatusLabel(section.status, useColor);
    lines.push(`  ${label} ${section.name}`);
  }
  const overallLabel = decorateStatusLabel(overall, useColor);
  lines.push(horizontal);
  lines.push(`Overall: ${overallLabel}`);
  lines.push(horizontal);
  return lines.join("\n");
}

function main({
  writeFn = (text) => process.stdout.write(text),
  envCi = process.env.CI,
  envNoColor = process.env.NO_COLOR,
  envForceColor = process.env.FORCE_COLOR,
  isTTY = Boolean(process.stdout && process.stdout.isTTY),
  runDoctorFn = runDoctor,
  runCommandFn = defaultRunCommand,
  probeToolVersionsFn = probeToolVersions
} = {}) {
  const useColor = shouldUseColor({ envCi, envNoColor, envForceColor, isTTY });

  // M1: spawn npm/pre-commit/pwsh/bash --version exactly once per doctor
  // run and thread the results into the header banner and the relevant
  // sections, instead of forking these subprocesses repeatedly.
  const versionProbes = probeToolVersionsFn({ runCommandFn });

  writeFn(`${formatHeaderBanner({ runCommandFn, npmVersionResult: versionProbes.npm })}\n`);

  const report = runDoctorFn({ versionProbes });
  for (const section of report.sections) {
    writeFn(`${formatSection(section, useColor)}\n`);
  }
  writeFn(`${formatFooter(report.sections, report.overall, useColor)}\n`);

  return report.status;
}

module.exports = {
  STALE_CACHE_DAYS,
  STALE_CACHE_MS,
  KNOWN_STATUSES,
  aggregateStatus,
  statusRank,
  statusToExitCode,
  checkNodeModulesFreshness,
  checkIsolatedJestCache,
  checkEolPolicy,
  checkPreCommitConfig,
  checkHookPerfBudget,
  checkCrossPlatformSanity,
  checkWorkingTreeState,
  checkChangedDocumentation,
  defaultRunCommand,
  probeToolVersions,
  runDoctor,
  shouldUseColor,
  isNoColorPresent,
  isForceColorOn,
  decorateStatusLabel,
  formatHeaderBanner,
  formatSection,
  formatFooter,
  main
};

if (require.main === module) {
  const exitCode = main();
  process.exit(exitCode);
}
