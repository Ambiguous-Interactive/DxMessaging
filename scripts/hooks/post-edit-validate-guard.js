#!/usr/bin/env node
"use strict";

/**
 * post-edit-validate-guard.js
 *
 * Claude Code PostToolUse hook (Edit / Write / MultiEdit). A change-aware,
 * advisory validator that runs the FAST, read-only validators relevant to the
 * file an agent just edited, IN-LOOP, so the failure class is caught during
 * editing instead of slipping through to the last-resort native git hook.
 *
 * Motivating gaps: editing `package.json` `files` or adding/removing a DLL or
 * .meta under `Editor/Analyzers/` changes the npm-pack output, and editing
 * user-visible Runtime/Editor/Samples/source-generator code requires
 * CHANGELOG.md coverage. Those failures used to surface at native hook time.
 * This guard closes those gaps by mapping the edited path to its relevant
 * validators through a dispatch table.
 *
 * Design (single responsibility, composable with the YAML guard):
 *   - YAML line-length is owned by `yaml-line-length-guard.js` (it auto-fixes).
 *     This guard still runs read-only spelling on YAML because the native
 *     pre-push cspell hook is a last-resort backstop, not the first signal.
 *   - This guard is READ-ONLY (it never mutates the working tree) and ADVISORY
 *     (always exit 0; it MUST NOT block tool use). It reports failing
 *     validators back to the agent via hookSpecificOutput.additionalContext
 *     plus systemMessage.
 *   - The dispatch table is the generalization point: adding a new
 *     `{ id, matches, validators }` entry extends edit-time coverage to a new
 *     category without touching the engine. The HOT path (doc-quality on
 *     `.cs` / `.md`) is sub-50ms and read-only. The npm-packaging entry is the
 *     one multi-second exception: it shells `npm pack --json --dry-run`
 *     (~1-2s on Linux, more on Windows via the cmd.exe shim), which is accepted
 *     because it fires ONLY on infrequent packaging-input edits
 *     (`package.json` / `.npmignore` / `*.dll` / `*.meta`), never on the hot
 *     `.cs` / `.md` edit loop. Changelog coverage is the sub-second global
 *     validator exception: it runs only when a likely user-visible path or
 *     CHANGELOG.md is edited, and it must inspect git metadata because coverage
 *     is a change-set invariant rather than a single-file invariant. The
 *     spelling entry uses cspell's Node API in-process and checks only the
 *     edited file. Cold cspell dictionary load is expected to be under about
 *     1s on the Linux devcontainer and warm checks are tens of ms. If the API
 *     path hits an infrastructure error, the guard falls back to the managed
 *     cspell runner so node_modules repair is still zero-touch.
 *
 * Cross-platform: pure Node plus `scripts/lib/shell-command.js`
 * (spawnPlatformCommandSync) for child processes; no bash, no shell:true, no
 * devcontainer assumption. Runs on native Linux, macOS, and Windows.
 */

const fs = require("fs");
const path = require("path");
const { spawnPlatformCommandSync, normalizeNodeColorEnv } = require("../lib/shell-command");
const { isOutsideRelative } = require("../lib/path-classifier");
const { isLikelyUserVisiblePath } = require("../validate-changelog");

const PACKAGING_SKILL = ".llm/skills/packaging/npm-package-configuration.md";
const DOCS_SKILL = ".llm/skills/documentation/documentation-style-guide.md";
const SPELLING_SKILL = ".llm/skills/scripting/change-aware-preflight.md";
const CHANGELOG_SKILL = ".llm/skills/scripting/change-aware-preflight.md";
// Unlike the other *_SKILL constants (which point at a focused .llm/skills page),
// the formatting-invariant XML-contract rule deliberately lives in the top-level
// AI-agent guidelines (.llm/context.md) alongside the other repo-wide rules, so
// the remediation links there. The target is a real, tracked file.
const XML_BUILD_CONTRACT_SKILL = ".llm/context.md";
const CSPELL_EXTENSIONS = Object.freeze([
  "cs",
  "js",
  "json",
  "markdown",
  "md",
  "ps1",
  "yaml",
  "yml"
]);
const CSPELL_EXTENSION_PATTERN = new RegExp(String.raw`\.(${CSPELL_EXTENSIONS.join("|")})$`, "i");

/**
 * Re-entrancy guard. A validator this guard spawns may itself trigger nested
 * tooling; the env flag lets those nested invocations short-circuit so the
 * guard never recurses on its own child processes.
 */
const ACTIVE_ENV = "DXMSG_POST_EDIT_GUARD_ACTIVE";

/**
 * Load the local cspell library API. The CLI package exposes its lint API as
 * ESM only, which Jest cannot exercise in-process; cspell-lib exposes the same
 * checker primitives through a CommonJS-compatible entrypoint.
 *
 * @param {string} repoRoot Absolute repository root.
 * @returns {Promise<object>} The cspell module namespace.
 */
function importCspell(repoRoot) {
  const modulePath = path.join(repoRoot, "node_modules", "cspell-lib", "dist", "index.js");
  return require(modulePath);
}

/**
 * Convert a cspell-lib issue to one-based line/column coordinates.
 *
 * @param {object} issue cspell issue object.
 * @returns {{row:number, col:number}} One-based coordinates.
 */
function cspellIssueLocation(issue) {
  const linePosition =
    issue && issue.line && issue.line.position ? issue.line.position : { line: 0, character: 0 };
  const row =
    typeof issue.row === "number"
      ? issue.row
      : typeof linePosition.line === "number"
        ? linePosition.line + 1
        : 1;
  const col =
    typeof issue.col === "number"
      ? issue.col
      : typeof issue.offset === "number" && issue.line && typeof issue.line.offset === "number"
        ? issue.offset - issue.line.offset + 1
        : typeof linePosition.character === "number"
          ? linePosition.character + 1
          : 1;
  return { row, col };
}

/**
 * True when the repo-relative path is one whose edit can change npm-pack output
 * (package.json `files`, `.npmignore`, or any shipped/removed `.dll` / `.meta`).
 * Deliberately NARROW: editing `.cs` CONTENT under a shipped dir does not change
 * packaging, so it is excluded here (it is covered by the doc-quality entry).
 *
 * @param {string} rel Repo-relative POSIX path.
 * @returns {boolean} True when packaging validation is relevant.
 */
function isPackagingRelevant(rel) {
  return (
    rel === "package.json" || rel === ".npmignore" || /\.dll$/i.test(rel) || /\.meta$/i.test(rel)
  );
}

/**
 * True when the path is a documentation-prose target (Markdown or C#) that the
 * ascii / code-pattern / prose validators inspect.
 *
 * @param {string} rel Repo-relative POSIX path.
 * @returns {boolean} True when doc-quality validation is relevant.
 */
function isDocQualityRelevant(rel) {
  return /\.(md|markdown|cs)$/i.test(rel);
}

/**
 * True when an edit can affect changelog coverage. Coverage is intentionally a
 * change-set invariant, so the validator reads the current git state instead of
 * receiving a single edited path.
 *
 * @param {string} rel Repo-relative POSIX path.
 * @returns {boolean} True when changelog coverage validation is relevant.
 */
function isChangelogCoverageRelevant(rel) {
  return rel === "CHANGELOG.md" || isLikelyUserVisiblePath(rel);
}

/**
 * True when the path is covered by the native pre-push cspell hook and should
 * therefore receive a file-scoped edit-time spellcheck here.
 *
 * @param {string} rel Repo-relative POSIX path.
 * @returns {boolean} True when spelling validation is relevant.
 */
function isSpellcheckRelevant(rel) {
  return CSPELL_EXTENSION_PATTERN.test(rel);
}

/**
 * True when an edited file is a SourceGenerators MSBuild build file
 * (.props/.csproj/.targets). Editing one of these can break the analyzer build
 * contract (reproducible-build properties, the .artifacts redirect, the Roslyn
 * 3.8 pin, the PostBuildCopyAnalyzers target). Focused on SourceGenerators/
 * because that is where the contract lives.
 *
 * @param {string} rel Repo-relative POSIX path.
 * @returns {boolean} True when XML-build-contract validation is relevant.
 */
function isXmlBuildContractRelevant(rel) {
  return /^SourceGenerators\/.*\.(props|csproj|targets)$/i.test(rel);
}

/**
 * Format cspell issues as compact file:line:column diagnostics.
 *
 * @param {string} rel Repo-relative POSIX path.
 * @param {Array<object>} issues cspell issue objects.
 * @returns {string} Human-readable diagnostic tail.
 */
function formatCspellIssues(rel, issues) {
  const shown = issues.slice(0, 3).map((issue) => {
    const { row, col } = cspellIssueLocation(issue);
    const word = issue.text || "(unknown)";
    return `${rel}:${row}:${col} Unknown word (${word})`;
  });
  const remaining = issues.length - shown.length;
  if (remaining > 0) {
    shown.push(`+${remaining} more`);
  }
  return shown.join(" | ");
}

/**
 * True when cspell's API result represents an infrastructure problem rather
 * than content spelling issues.
 *
 * @param {object|null|undefined} result cspell lint result.
 * @param {Array<string>} errors Reporter error messages.
 * @returns {boolean} True when the managed CLI should retry with auto-repair.
 */
function hasCspellInfrastructureError(result, errors) {
  const resultErrorCount =
    result && typeof result.errors === "number"
      ? result.errors
      : result && Array.isArray(result.errors)
        ? result.errors.length
        : 0;
  const errorCount = resultErrorCount + errors.length;
  return errorCount > 0;
}

/**
 * Run cspell through its local Node API. This is the hot edit-time path: one
 * process, one edited file, config/gitignore honored, and no cache writes.
 *
 * @param {string} absPath Absolute edited file path.
 * @param {string} repoRoot Absolute repository root.
 * @param {object} [deps] Injectable dependencies.
 * @param {Function} [deps.importFn] cspell import implementation.
 * @returns {Promise<{label:string, ok:boolean, detail:string}>} Outcome.
 */
async function runCspellApiValidator(absPath, repoRoot, deps = {}) {
  const importFn = deps.importFn || importCspell;
  const rel = toRepoRelativePosix(repoRoot, absPath);
  if (rel === null) {
    return { label: "cspell", ok: true, detail: "" };
  }

  const cspell = await importFn(repoRoot);
  if (
    !cspell ||
    typeof cspell.loadConfig !== "function" ||
    typeof cspell.spellCheckFile !== "function"
  ) {
    throw new Error("cspell library API is unavailable");
  }

  const config = await cspell.loadConfig(path.join(repoRoot, ".cspell.json"));
  const result = await cspell.spellCheckFile(absPath, { root: repoRoot }, config);
  const issues = Array.isArray(result.issues) ? result.issues : [];
  const errors = [
    ...(Array.isArray(result.errors) ? result.errors : []),
    ...(Array.isArray(result.configErrors) ? result.configErrors : []),
    ...(result.dictionaryErrors && typeof result.dictionaryErrors.values === "function"
      ? [...result.dictionaryErrors.values()]
      : [])
  ].map((error) => (error && error.message ? error.message : String(error)));

  if (issues.length > 0) {
    return { label: "cspell", ok: false, detail: formatCspellIssues(rel, issues) };
  }

  if (hasCspellInfrastructureError(result, errors)) {
    throw new Error(errors.filter(Boolean).slice(-3).join(" | ") || "cspell reported errors");
  }

  if (!result.checked) {
    return { label: "cspell", ok: true, detail: "" };
  }

  return { label: "cspell", ok: true, detail: "" };
}

/**
 * Run file-scoped spelling. Prefer the in-process API for speed; fall back to
 * the managed CLI so missing/corrupt local cspell can auto-repair instead of
 * requiring manual intervention.
 *
 * @param {string} absPath Absolute edited file path.
 * @param {string} repoRoot Absolute repository root.
 * @param {object} [deps] Injectable dependencies.
 * @returns {Promise<{label:string, ok:boolean, detail:string}>} Outcome.
 */
async function runSpellcheckValidator(absPath, repoRoot, deps = {}) {
  const rel = toRepoRelativePosix(repoRoot, absPath);
  if (rel === null) {
    return { label: "cspell", ok: true, detail: "" };
  }

  try {
    return await runCspellApiValidator(absPath, repoRoot, deps);
  } catch (_error) {
    return runSpawnValidator(
      {
        label: "cspell",
        args: () => [
          "scripts/run-managed-cspell.js",
          "--no-progress",
          "--no-summary",
          "--no-must-find-files",
          rel
        ]
      },
      absPath,
      repoRoot,
      deps
    );
  }
}

/**
 * The dispatch table. Each entry maps a class of edited file to the FAST,
 * read-only validators that should run in-loop. `validators[].args` is a
 * function of the absolute path for spawned validators; `validators[].run` is
 * used for in-process validators such as cspell.
 *
 * @returns {Array<{id:string, matches:(rel:string)=>boolean,
 *   validators:Array<{label:string, args?:(abs:string)=>string[],
 *   run?:Function}>,
 *   remediation:string}>}
 */
function buildDispatchTable() {
  return [
    {
      id: "npm-packaging",
      matches: isPackagingRelevant,
      remediation:
        `Editing packaging inputs changed what 'npm pack' ships. Run ` +
        `'node scripts/validate-npm-meta.js --check' and fix the package.json ` +
        `'files' array / .npmignore so every shipped .meta and the required ` +
        `analyzer DLLs stay in the tarball. See ${PACKAGING_SKILL}.`,
      validators: [
        {
          label: "validate-npm-meta",
          // pass_filenames-style: the validator resolves the whole package; it
          // does not take a single-file arg.
          args: () => ["scripts/validate-npm-meta.js", "--check"]
        }
      ]
    },
    {
      id: "doc-quality",
      matches: isDocQualityRelevant,
      remediation:
        `Documentation must be pure ASCII, compile in code samples, and avoid ` +
        `LLM-style filler. Run the normalizers (e.g. ` +
        `'node scripts/normalize-docs-ascii.js <file>') then re-check. See ` +
        `${DOCS_SKILL}.`,
      validators: [
        { label: "docs-ascii", args: (abs) => ["scripts/validate-docs-ascii.js", abs] },
        {
          label: "docs-code-patterns",
          args: (abs) => ["scripts/validate-doc-code-patterns.js", abs]
        },
        { label: "docs-prose", args: (abs) => ["scripts/validate-docs-prose.js", abs] }
      ]
    },
    {
      id: "changelog-coverage",
      matches: isChangelogCoverageRelevant,
      remediation:
        `Likely user-visible changes require a user-impact entry under ` +
        `CHANGELOG.md's Unreleased section. Update CHANGELOG.md in the same ` +
        `change, then rerun 'node scripts/validate-changelog.js ` +
        `--check-coverage'. See ${CHANGELOG_SKILL}.`,
      validators: [
        {
          label: "validate-changelog-policy",
          // Coverage is global by design: passing only the edited path would
          // hide the common case where several user-visible files changed but
          // no changelog entry exists.
          args: () => ["scripts/validate-changelog.js", "--check-coverage"]
        }
      ]
    },
    {
      id: "spelling",
      matches: isSpellcheckRelevant,
      remediation:
        `Fix the typo or add legitimate project vocabulary to .cspell.json in ` +
        `the same change. See ${SPELLING_SKILL}.`,
      validators: [{ label: "cspell", run: runSpellcheckValidator }]
    },
    {
      id: "xml-build-contract",
      matches: isXmlBuildContractRelevant,
      remediation:
        `Editing a SourceGenerators .props/.csproj/.targets can break the analyzer ` +
        `build contract (reproducible-build properties, the .artifacts obj/bin ` +
        `redirect, the Roslyn 3.8 pin, or the PostBuildCopyAnalyzers target). Run ` +
        `'node scripts/validate-analyzer-build-contract.js' and restore the ` +
        `required elements/values. Assertions are formatting-invariant (parsed ` +
        `via scripts/lib/msbuild-xml.js), so line-wrapping and attribute order are ` +
        `fine. See ${XML_BUILD_CONTRACT_SKILL}.`,
      validators: [
        {
          label: "analyzer-build-contract",
          // Whole-contract validator: it resolves the SourceGenerators build
          // files itself and does not take a single-file arg.
          args: () => ["scripts/validate-analyzer-build-contract.js"]
        }
      ]
    }
  ];
}

/**
 * Read stdin to completion as a UTF-8 string.
 *
 * @returns {Promise<string>} The full stdin payload.
 */
function readStdin() {
  return new Promise((resolve) => {
    let data = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (chunk) => {
      data += chunk;
    });
    process.stdin.on("end", () => resolve(data));
    process.stdin.on("error", () => resolve(data));
  });
}

/**
 * Resolve the repository root. Prefers the Claude Code project dir env var;
 * falls back to the repo root relative to this script.
 *
 * @returns {string} Absolute repository root.
 */
function resolveRepoRoot() {
  if (process.env.CLAUDE_PROJECT_DIR && process.env.CLAUDE_PROJECT_DIR.trim().length > 0) {
    return process.env.CLAUDE_PROJECT_DIR;
  }
  return path.resolve(__dirname, "..", "..");
}

/**
 * Convert an absolute path to a repo-relative POSIX path. Returns null when the
 * path is outside the repo (those are never validated here).
 *
 * @param {string} repoRoot Absolute repo root.
 * @param {string} absPath Absolute file path.
 * @returns {string|null} Repo-relative POSIX path, or null when outside repo.
 */
function toRepoRelativePosix(repoRoot, absPath) {
  const rawRel = path.relative(repoRoot, absPath);
  // `isOutsideRelative` is cross-drive-safe: on Windows where the edited file
  // and the repo live on different drives, `path.relative` returns an ABSOLUTE
  // target rather than a `..` chain, and the bare `rel.startsWith("..")` form
  // would wrongly treat that out-of-repo file as repo-relative and run
  // validators against a corrupted path. The empty string means absPath IS the
  // repo root (a directory, never validated here), so treat it as outside too.
  if (rawRel.length === 0 || isOutsideRelative(rawRel)) {
    return null;
  }
  return rawRel.replace(/\\/g, "/");
}

/**
 * Normalize the legacy "fourth arg is spawnImpl" test helper shape into the
 * deps object used by async/in-process validators.
 *
 * @param {Function|object} depsOrSpawn Injectable spawn or deps object.
 * @returns {object} Normalized deps object.
 */
function normalizeDeps(depsOrSpawn) {
  if (typeof depsOrSpawn === "function") {
    return { spawnImpl: depsOrSpawn };
  }
  return depsOrSpawn || {};
}

/**
 * Run a spawned validator (read-only) under the active repo root and capture
 * its outcome. Uses spawnPlatformCommandSync for cross-platform argv-array
 * execution (no shell).
 *
 * @param {{label:string, args:(abs:string)=>string[]}} validator Table entry.
 * @param {string} absPath Absolute path of the edited file.
 * @param {string} repoRoot Absolute repo root (cwd for the spawn).
 * @param {object} [deps] Injectable deps.
 * @returns {{label:string, ok:boolean, detail:string}} Outcome.
 */
function runSpawnValidator(validator, absPath, repoRoot, deps = {}) {
  const spawnImpl = deps.spawnImpl || spawnPlatformCommandSync;
  const args = validator.args(absPath);
  const result = spawnImpl(process.execPath, args, {
    cwd: repoRoot,
    encoding: "utf8",
    env: normalizeNodeColorEnv({ ...process.env, [ACTIVE_ENV]: "1" }),
    stdio: ["ignore", "pipe", "pipe"]
  });

  if (result.error && result.error.code === "ENOENT") {
    // Node missing is an environment problem, not a content failure; stay quiet
    // (advisory guards must never manufacture noise from infra gaps).
    return { label: validator.label, ok: true, detail: "" };
  }

  const status = typeof result.status === "number" ? result.status : 1;
  if (status === 0) {
    return { label: validator.label, ok: true, detail: "" };
  }

  const combined = `${result.stdout || ""}\n${result.stderr || ""}`.trim();
  const lastLines = combined.split("\n").filter(Boolean).slice(-4).join(" | ");
  return { label: validator.label, ok: false, detail: lastLines };
}

/**
 * Run one validator (read-only) under the active repo root and capture its
 * outcome. Uses the cspell API for spelling validators and
 * spawnPlatformCommandSync for the existing argv-array validators.
 *
 * @param {{label:string, args?:(abs:string)=>string[],
 *   run?:(abs:string, repoRoot:string, deps:object)=>Promise<object>|object}} validator
 *   Table entry.
 * @param {string} absPath Absolute path of the edited file.
 * @param {string} repoRoot Absolute repo root (cwd for spawned validators).
 * @param {Function|object} [depsOrSpawn] Injectable spawn or deps object.
 * @returns {Promise<{label:string, ok:boolean, detail:string}>} Outcome.
 */
async function runValidator(validator, absPath, repoRoot, depsOrSpawn = {}) {
  const deps = normalizeDeps(depsOrSpawn);
  if (typeof validator.run === "function") {
    return validator.run(absPath, repoRoot, deps);
  }
  return runSpawnValidator(validator, absPath, repoRoot, deps);
}

/**
 * Pure core: given the edited file's repo-relative + absolute path, run every
 * matching dispatch entry's validators and collect failures. No stdin / stdout.
 *
 * @param {string} rel Repo-relative POSIX path.
 * @param {string} absPath Absolute file path.
 * @param {string} repoRoot Absolute repo root.
 * @param {object} [deps] Injectable deps for tests.
 * @param {Function} [deps.spawnImpl] Spawn implementation.
 * @param {Array} [deps.table] Dispatch table override.
 * @returns {Promise<Array<{id:string, remediation:string,
 *   failures:Array<{label:string, detail:string}>}>} One result per matching
 *   entry that had at least one failing validator.
 */
async function evaluate(rel, absPath, repoRoot, deps = {}) {
  const spawnImpl = deps.spawnImpl || spawnPlatformCommandSync;
  const table = deps.table || buildDispatchTable();
  const reports = [];

  for (const entry of table) {
    if (!entry.matches(rel)) {
      continue;
    }

    const failures = [];
    for (const validator of entry.validators) {
      const outcome = await runValidator(validator, absPath, repoRoot, { ...deps, spawnImpl });
      if (!outcome.ok) {
        failures.push({ label: outcome.label, detail: outcome.detail });
      }
    }

    if (failures.length > 0) {
      reports.push({ id: entry.id, remediation: entry.remediation, failures });
    }
  }

  return reports;
}

/**
 * Build the agent-facing advisory message from evaluation reports. Returns null
 * when there is nothing to report.
 *
 * @param {string} rel Repo-relative path for display.
 * @param {Array} reports Output of {@link evaluate}.
 * @returns {string|null} Advisory text, or null when silent.
 */
function buildMessage(rel, reports) {
  if (reports.length === 0) {
    return null;
  }

  const parts = [`post-edit-validate-guard found issues after editing ${rel}:`];
  for (const report of reports) {
    const failing = report.failures
      .map((f) => (f.detail ? `${f.label} (${f.detail})` : f.label))
      .join("; ");
    parts.push(`[${report.id}] ${failing}. ${report.remediation}`);
  }
  return parts.join(" ");
}

/**
 * Main hook entry. Reads the PostToolUse payload, runs matching validators, and
 * emits advisory JSON. Always exits 0 (a guard must not block tool use).
 *
 * @param {string} stdinPayload Raw PostToolUse JSON from stdin.
 * @param {object} [env] Environment (injectable for tests).
 * @param {object} [deps] Injectable deps (spawnImpl, table).
 * @returns {number} Process exit code (always 0).
 */
async function run(stdinPayload, env = process.env, deps = {}) {
  // Never recurse on a child process the guard itself spawned.
  if (env[ACTIVE_ENV] === "1") {
    return 0;
  }

  let event;
  try {
    event = JSON.parse(stdinPayload);
  } catch (_error) {
    return 0;
  }

  const toolInput = event && event.tool_input ? event.tool_input : {};
  const filePath = toolInput.file_path;
  if (typeof filePath !== "string" || filePath.length === 0) {
    return 0;
  }

  const repoRoot = resolveRepoRoot();
  const rel = toRepoRelativePosix(repoRoot, filePath);
  if (rel === null) {
    return 0;
  }

  // Fast early-exit: only proceed when the file exists and matches an entry.
  const table = deps.table || buildDispatchTable();
  if (!table.some((entry) => entry.matches(rel))) {
    return 0;
  }

  try {
    if (!fs.statSync(filePath).isFile()) {
      return 0;
    }
  } catch (_error) {
    return 0;
  }

  const reports = await evaluate(rel, filePath, repoRoot, deps);
  const message = buildMessage(rel, reports);
  if (message === null) {
    return 0;
  }

  const output = {
    hookSpecificOutput: {
      hookEventName: "PostToolUse",
      additionalContext: message
    },
    systemMessage: message
  };
  process.stdout.write(`${JSON.stringify(output)}\n`);
  return 0;
}

module.exports = {
  ACTIVE_ENV,
  CSPELL_EXTENSIONS,
  CSPELL_EXTENSION_PATTERN,
  cspellIssueLocation,
  importCspell,
  isPackagingRelevant,
  isDocQualityRelevant,
  isChangelogCoverageRelevant,
  isSpellcheckRelevant,
  isXmlBuildContractRelevant,
  formatCspellIssues,
  hasCspellInfrastructureError,
  runCspellApiValidator,
  runSpellcheckValidator,
  buildDispatchTable,
  resolveRepoRoot,
  toRepoRelativePosix,
  normalizeDeps,
  runSpawnValidator,
  runValidator,
  evaluate,
  buildMessage,
  run
};

if (require.main === module) {
  readStdin().then(async (payload) => {
    process.exit(await run(payload));
  });
}
