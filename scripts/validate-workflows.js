#!/usr/bin/env node
/**
 * validate-workflows.js
 *
 * Validates GitHub Actions workflow files for problematic patterns, specifically:
 * - Single-line multi-pattern `git add --renormalize` commands (FORBIDDEN)
 * - `git add --renormalize` commands without existence checks
 * - Bash syntax in Windows-targeting jobs without Bash-compatible shell overrides
 * - Object-form `runs-on.group:` (runner groups are not provisioned for this org)
 * - Native reuse of `concurrency.group: wallstop-organization-builds`.
 *   GitHub concurrency is repository-scoped and serializes whole jobs, so the
 *   organization Unity lock must be acquired through the central
 *   ambiguous-organization-build-lock actions instead.
 * - Jobs that declare BOTH `strategy.matrix` AND a `concurrency.group` without
 *   any `${{ matrix.* }}` expansion, `queue: max`, or `strategy.max-parallel: 1`
 *   mitigation.
 * - Self-hosted `runs-on` label sets that are not in the documented allowlist
 *   (catches typos like `RAM-64Gb` that produce jobs no runner can pick up).
 *   Dynamic
 *   `${{ fromJSON(needs.<job>.outputs.<output>) }}` values are accepted only
 *   when the emitting bash block produces allowlisted label sets.
 * - Jobs with `runs-on: ${{ fromJSON(needs.<jobId>.outputs.<output>) }}`
 *   that omit `<jobId>` from their `needs:` declaration (runtime would
 *   fail loudly but late; the static check surfaces typos at validation).
 * - Unity GameCI jobs that are not wrapped by the central organization lock
 *   acquire/release actions and the local Unity license preflight action.
 * - Direct Unity test jobs that run scripts/unity/run-ci-tests.ps1 without
 *   first provisioning a CI-managed Unity editor before the organization lock.
 * - Unsupported inputs on `game-ci/unity-test-runner@v4`.
 * - Workflow lines that exceed the yamllint line-length ceiling (loaded from
 *   .yamllint.yaml when available; defaults to 200). This provides earlier
 *   feedback in `npm run validate:workflows` before git-hook execution.
 * - .pre-commit-config.yaml lines that exceed the same yamllint line-length
 *   ceiling so hook-policy YAML drift is surfaced during preflight instead of
 *   only at hook-time.
 * - Active lycheeverse/lychee-action steps, which are intentionally forbidden
 *   after its v0.24.2 Linux installer expected a flat archive while the release
 *   asset extracted lychee under a platform directory. Docs link workflows must
 *   install lychee through the repo-owned pinned installer and invoke the CLI
 *   directly. The blocking workflow must use native timeout acceptance, while
 *   the scheduled advisory scan must report timeout/dead-link findings through
 *   the tracking issue instead of failing the workflow.
 *
 * @usage
 *   node scripts/validate-workflows.js
 *
 * @exitcodes
 *   0 - Success (no violations found)
 *   1 - Validation failed (one or more violations found)
 *
 * @example
 *   # Run from repository root
 *   node scripts/validate-workflows.js
 *
 *   # Run in CI pipeline
 *   node scripts/validate-workflows.js || exit 1
 */

"use strict";

const { execFileSync } = require("child_process");
const fs = require("fs");
const path = require("path");
const { normalizeToLf } = require("./lib/quote-parser");
const {
  TARGETED_STEP_NAME,
  extractTargetedStepRunBlock,
  extractListedTestPaths
} = require("./lib/cross-platform-preflight-gate");

const REPO_ROOT = path.join(__dirname, "..");
const WORKFLOWS_DIR = path.join(__dirname, "..", ".github", "workflows");
const PRE_COMMIT_CONFIG_PATH = path.join(__dirname, "..", ".pre-commit-config.yaml");
const DEFAULT_WORKFLOW_LINE_LENGTH = 200;

// Per-job Unity run budget (minutes) charged on top of the organization lock
// poll wait. A Unity-licensed job's `timeout-minutes` must cover BOTH the
// acquire-build-lock poll budget (counted against the job clock) AND this
// run budget; otherwise GitHub kills a job that is still waiting for the
// shared Unity activation seat. Mirrors the step-level
// `timeout-minutes: 120` guard on the Unity run step.
const UNITY_LOCK_RUN_BUDGET_MINUTES = 120;

// Lowercased `uses` refs for the organization build-lock action (the validator
// lowercases all `uses` values, so these constants are pre-lowercased).
const ORGANIZATION_BUILD_LOCK_ACQUIRE_USES =
  "ambiguous-interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1";

const ORGANIZATION_BUILD_LOCK_RELEASE_USES =
  "ambiguous-interactive/ambiguous-organization-build-lock/.github/actions/release-build-lock@v1";

// Lowercased `uses` ref for the game-ci test runner action.
const GAME_CI_UNITY_TEST_RUNNER_USES = "game-ci/unity-test-runner@";
const REQUIRED_LYCHEE_VERSION = "v0.24.2";
const BLOCKING_DOC_LINK_WORKFLOW = ".github/workflows/lint-doc-links.yml";
const ADVISORY_DOC_LINK_WORKFLOW = ".github/workflows/markdown-link-validity.yml";
const ADVISORY_LYCHEE_REPORT_PATH = "./lychee/out.md";
const REQUIRED_ACTIVE_WORKFLOW_PATHS = Object.freeze([
  BLOCKING_DOC_LINK_WORKFLOW,
  ADVISORY_DOC_LINK_WORKFLOW
]);

// The `yaml` package is used to parse workflows structurally (formatting-
// invariant) for the compute-unity-assemblies gate check. It is not a declared
// dependency of this package; it arrives transitively through devDependencies
// (cspell, markdownlint-cli2). Resolve it lazily and remember the outcome so a
// missing module surfaces as an actionable policy Violation instead of crashing
// the whole validator with an uncaught MODULE_NOT_FOUND. The result is cached
// (module on success, Error on failure) so the require runs at most once.
let cachedYamlModule;

/**
 * Loads the `yaml` package, caching the result across calls.
 *
 * @returns {{ module: object } | { error: Error }} The loaded module, or the
 *   load error when `yaml` cannot be resolved.
 */
function loadYamlModule() {
  if (cachedYamlModule === undefined) {
    try {
      cachedYamlModule = { module: require("yaml") };
    } catch (error) {
      cachedYamlModule = { error: error instanceof Error ? error : new Error(String(error)) };
    }
  }
  return cachedYamlModule;
}

/**
 * Represents a validation violation.
 */
class Violation {
  constructor(file, line, pattern, message, severity = "error") {
    this.file = file;
    this.line = line;
    this.pattern = pattern;
    this.message = message;
    this.severity = severity;
  }

  toString() {
    const prefix = this.severity === "error" ? "ERROR" : "WARN";
    return `[${prefix}] ${this.file}:${this.line}: ${this.message}\n  Pattern: ${this.pattern}`;
  }
}

function resolveWorkflowLineLengthMax(repoRoot = REPO_ROOT) {
  return resolveWorkflowLineLengthPolicy(repoRoot).max;
}

function parseYamlBoolean(rawValue) {
  if (typeof rawValue !== "string") {
    return null;
  }

  const normalized = rawValue.trim().toLowerCase();
  if (normalized === "true") {
    return true;
  }
  if (normalized === "false") {
    return false;
  }

  return null;
}

function resolveWorkflowLineLengthPolicy(repoRoot = REPO_ROOT) {
  const policy = {
    max: DEFAULT_WORKFLOW_LINE_LENGTH,
    allowNonBreakableWords: true,
    allowNonBreakableInlineMappings: false
  };

  const configPath = path.join(repoRoot, ".yamllint.yaml");

  let content;
  try {
    content = fs.readFileSync(configPath, "utf8");
  } catch (_error) {
    return policy;
  }

  const lines = normalizeToLf(content).split("\n");
  let inLineLengthBlock = false;
  let blockIndent = -1;

  for (const line of lines) {
    const trimmed = line.trim();

    if (!inLineLengthBlock) {
      if (/^\s*line-length:\s*(?:#.*)?$/.test(line)) {
        inLineLengthBlock = true;
        blockIndent = getIndent(line);
      }
      continue;
    }

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    const indent = getIndent(line);
    if (indent <= blockIndent) {
      break;
    }

    const maxMatch = /^\s*max:\s*([0-9]+)\s*(?:#.*)?$/.exec(line);
    if (maxMatch) {
      const parsedMax = Number.parseInt(maxMatch[1], 10);
      if (Number.isFinite(parsedMax) && parsedMax > 0) {
        policy.max = parsedMax;
      }
      continue;
    }

    const allowWordsMatch = /^\s*allow-non-breakable-words:\s*([^#]+?)\s*(?:#.*)?$/.exec(line);
    if (allowWordsMatch) {
      const parsedBoolean = parseYamlBoolean(allowWordsMatch[1]);
      if (parsedBoolean !== null) {
        policy.allowNonBreakableWords = parsedBoolean;
      }
      continue;
    }

    const allowInlineMappingsMatch =
      /^\s*allow-non-breakable-inline-mappings:\s*([^#]+?)\s*(?:#.*)?$/.exec(line);
    if (!allowInlineMappingsMatch) {
      continue;
    }

    const parsedBoolean = parseYamlBoolean(allowInlineMappingsMatch[1]);
    if (parsedBoolean !== null) {
      policy.allowNonBreakableInlineMappings = parsedBoolean;
    }
  }

  // yamllint semantics: enabling inline mappings implies non-breakable words.
  if (policy.allowNonBreakableInlineMappings) {
    policy.allowNonBreakableWords = true;
  }

  return policy;
}

function lineHasNonBreakableWordOverflow(line, maxLength) {
  const trimmed = line.trim();
  if (trimmed.length === 0) {
    return false;
  }

  const tokens = trimmed.split(/\s+/);
  return tokens.some((token) => token.length > maxLength);
}

function findWorkflowLineLengthViolations(relativePath, lines, maxLength, options = {}) {
  const violations = [];
  const allowNonBreakableWords = options.allowNonBreakableWords === true;
  const contextLabel = typeof options.contextLabel === "string" ? options.contextLabel : "Workflow";

  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    const length = line.length;

    if (length <= maxLength) {
      continue;
    }

    if (allowNonBreakableWords && lineHasNonBreakableWordOverflow(line, maxLength)) {
      continue;
    }

    violations.push(
      new Violation(
        relativePath,
        index + 1,
        line.trim(),
        `${contextLabel} line exceeds ${maxLength} characters (${length}). Break long lines so validate:workflows catches this before git hooks.`,
        "error"
      )
    );
  }

  return violations;
}

function validatePreCommitConfigLineLengths(options = {}) {
  const repoRoot = options.repoRoot || REPO_ROOT;
  const preCommitConfigPath = options.preCommitConfigPath || PRE_COMMIT_CONFIG_PATH;
  const relativePath = path.relative(repoRoot, preCommitConfigPath).replace(/\\/g, "/");
  const lineLengthPolicy = resolveWorkflowLineLengthPolicy(repoRoot);

  let content;
  try {
    content = fs.readFileSync(preCommitConfigPath, "utf8");
  } catch (error) {
    if (error && error.code === "ENOENT") {
      return [];
    }

    return [new Violation(relativePath, 0, "", `Failed to read file: ${error.message}`)];
  }

  const lines = normalizeToLf(content).split("\n");
  return findWorkflowLineLengthViolations(relativePath, lines, lineLengthPolicy.max, {
    ...lineLengthPolicy,
    contextLabel: "YAML"
  });
}

function getIndent(line) {
  return line.length - line.trimStart().length;
}

function usesVariableExtensionPattern(line) {
  return /\*\.\$\{?[A-Za-z_][A-Za-z0-9_]*\}?/.test(line);
}

/**
 * Checks if a line contains a problematic single-line multi-pattern renormalize command.
 * These commands fail with exit code 128 if any pattern matches no files.
 *
 * @param {string} line - The line to check
 * @returns {boolean} True if the line contains a forbidden pattern
 */
function isForbiddenRenormalizePattern(line) {
  // Match lines that have git add --renormalize with multiple file patterns
  // Forbidden: git add --renormalize -- '*.md' '*.json' '*.yml'
  // Allowed: git add --renormalize -- "*.$ext" "**/*.$ext" (single extension via variable)

  const trimmed = line.trim();

  // Must contain git add --renormalize
  if (!trimmed.includes("git add") || !trimmed.includes("--renormalize")) {
    return false;
  }

  // Skip lines that use shell variable expansion (part of a loop)
  if (usesVariableExtensionPattern(trimmed)) {
    return false;
  }

  const commandMatch = /git add --renormalize\s+--\s+(.+?)(?:\s*(?:&&|\|\||;|\|)\s*.+)?$/.exec(
    trimmed
  );
  const renormalizeArgs = commandMatch ? commandMatch[1] : trimmed;

  // Skip lines that target a single specific file (e.g., '.config/dotnet-tools.json')
  // These are safe because the file definitely exists or the step would have failed earlier
  const singleFilePattern = /^["']?[^"'*?\s]+["']?$/;
  if (singleFilePattern.test(renormalizeArgs)) {
    return false;
  }

  // Count distinct file extension patterns (*.ext or **/*.ext)
  // Use a Set to count unique extensions
  const extensionPatterns = renormalizeArgs.match(/\*\.(\w+)/g) || [];
  const uniqueExtensions = new Set(extensionPatterns.map((p) => p.replace("*.", "")));

  // If there are multiple unique extensions on one line, it's forbidden
  return uniqueExtensions.size > 1;
}

/**
 * Checks if a renormalize command is properly guarded by an existence check.
 * The existence check should be in a preceding line within the same block.
 *
 * @param {string[]} lines - All lines of the file
 * @param {number} lineIndex - Index of the renormalize line
 * @returns {boolean} True if properly guarded
 */
function hasExistenceCheck(lines, lineIndex) {
  // Look backwards for an existence check pattern
  // Pattern: if git ls-files "*.ext" | grep -q .; then
  // or: if git ls-files "*.$ext" | grep -q .; then
  const lookbackLines = 10;
  const startIndex = Math.max(0, lineIndex - lookbackLines);

  for (let i = lineIndex - 1; i >= startIndex; i--) {
    const line = lines[i];
    if (
      line.includes("git ls-files") &&
      line.includes("grep -q") &&
      (line.includes("then") || lines[i + 1]?.includes("then"))
    ) {
      return true;
    }
    // Also check for a for-loop with if-check pattern
    if (line.includes("for ext in") || line.includes("for EXT in")) {
      // Check if there's a git ls-files check between the for and the renormalize
      for (let j = i + 1; j < lineIndex; j++) {
        if (lines[j].includes("git ls-files") && lines[j].includes("grep -q")) {
          return true;
        }
      }
    }
  }
  return false;
}

function isGitIgnoredPath(repoRoot, relativePath, execFileSyncImpl = execFileSync) {
  if (typeof relativePath !== "string" || relativePath.trim().length === 0) {
    return false;
  }

  const runCheckIgnore = (args) =>
    execFileSyncImpl("git", args, {
      cwd: repoRoot,
      stdio: ["ignore", "ignore", "pipe"]
    });

  const isUnsupportedNoIndex = (error) => {
    const stderr = error && error.stderr ? String(error.stderr) : "";
    const message = error && error.message ? String(error.message) : "";
    const combined = `${message}\n${stderr}`;

    return (
      (error && typeof error.status === "number" && error.status === 129) ||
      /unknown option|unknown switch/i.test(combined) ||
      (/check-ignore/i.test(combined) && /no-index/i.test(combined))
    );
  };

  const throwGitUnavailableError = (phase) => {
    throw new Error(
      `Unable to evaluate git ignore status for '${relativePath}': git executable was not found on PATH (${phase}).`
    );
  };

  try {
    runCheckIgnore(["check-ignore", "--quiet", "--no-index", "--", relativePath]);
    return true;
  } catch (error) {
    if (error && typeof error.status === "number" && error.status === 1) {
      return false;
    }

    if (error && error.code === "ENOENT") {
      throwGitUnavailableError("check-ignore --no-index");
    }

    if (isUnsupportedNoIndex(error)) {
      try {
        runCheckIgnore(["check-ignore", "--quiet", "--", relativePath]);
        return true;
      } catch (fallbackError) {
        if (
          fallbackError &&
          typeof fallbackError.status === "number" &&
          fallbackError.status === 1
        ) {
          return false;
        }

        if (fallbackError && fallbackError.code === "ENOENT") {
          throwGitUnavailableError("check-ignore fallback");
        }

        const fallbackMessage =
          fallbackError && fallbackError.message ? fallbackError.message : String(fallbackError);
        throw new Error(
          `Unable to evaluate git ignore status for '${relativePath}' after falling back from --no-index: ${fallbackMessage}`
        );
      }
    }

    const message = error && error.message ? error.message : String(error);
    throw new Error(`Unable to evaluate git ignore status for '${relativePath}': ${message}`);
  }
}

function extractWorkflowPathEntries(lines) {
  const entries = [];
  let inPathsBlock = false;
  let pathsIndent = -1;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (!inPathsBlock && /^\s*paths:\s*$/.test(line)) {
      inPathsBlock = true;
      pathsIndent = indent;
      continue;
    }

    if (!inPathsBlock) {
      continue;
    }

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= pathsIndent && !/^\s*-\s+/.test(line)) {
      inPathsBlock = false;
      pathsIndent = -1;

      if (/^\s*paths:\s*$/.test(line)) {
        inPathsBlock = true;
        pathsIndent = indent;
      }
      continue;
    }

    const pathEntry = /^\s*-\s*["']?([^"'#]+)["']?\s*(?:#.*)?$/.exec(line);
    if (pathEntry) {
      entries.push({
        line: i + 1,
        path: pathEntry[1].trim()
      });
    }
  }

  return entries;
}

function isLiteralPath(pathValue) {
  return !/[\*\?\[\]\{\}]|\$\{\{/.test(pathValue) && !pathValue.startsWith("!");
}

function findIgnoredPathViolations(
  relativePath,
  lines,
  repoRoot = REPO_ROOT,
  isIgnoredPathFn = isGitIgnoredPath
) {
  const violations = [];
  const entries = extractWorkflowPathEntries(lines);

  for (const entry of entries) {
    if (!isLiteralPath(entry.path)) {
      continue;
    }

    if (!isIgnoredPathFn(repoRoot, entry.path)) {
      continue;
    }

    violations.push(
      new Violation(
        relativePath,
        entry.line,
        entry.path,
        `Workflow trigger path '${entry.path}' is ignored by git and cannot trigger this workflow. Remove it from paths filters or update ignore policy.`,
        "error"
      )
    );
  }

  return violations;
}

function extractRunBlocks(lines) {
  const blocks = [];

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const blockRunMatch = /^(\s*)(?:-\s+)?run:\s*[>|][+-]?\s*$/.exec(line);

    if (blockRunMatch) {
      const baseIndent = blockRunMatch[1].length;
      const blockLines = [];
      let j = i + 1;

      while (j < lines.length) {
        const nextLine = lines[j];
        const trimmed = nextLine.trim();
        const nextIndent = getIndent(nextLine);

        if (trimmed.length > 0 && nextIndent <= baseIndent) {
          break;
        }

        blockLines.push(nextLine.trim());
        j++;
      }

      blocks.push({
        startLine: i + 1,
        text: blockLines.join("\n").trim()
      });

      i = j - 1;
      continue;
    }

    const inlineRunMatch = /^\s*(?:-\s+)?run:\s*(.+?)\s*$/.exec(line);
    if (inlineRunMatch) {
      blocks.push({
        startLine: i + 1,
        text: inlineRunMatch[1].trim()
      });
    }
  }

  return blocks;
}

function findLockfileInstallViolations(relativePath, lines, packageLockIgnored) {
  const violations = [];

  if (!packageLockIgnored) {
    return violations;
  }

  const runBlocks = extractRunBlocks(lines);

  for (const block of runBlocks) {
    const hasNpmCi = /(^|\n|;|&&)\s*npm\s+ci\b/m.test(block.text);
    const hasNpmInstall = /(^|\n|;|&&)\s*npm\s+(?:install|i)\b/m.test(block.text);

    if (hasNpmInstall && !hasNpmCi) {
      violations.push(
        new Violation(
          relativePath,
          block.startLine,
          "npm install",
          "Repository ignores package-lock.json, so dependency install blocks must be lockfile-aware. Use npm ci when package-lock.json exists and npm install fallback when it does not.",
          "error"
        )
      );
      continue;
    }

    if (!hasNpmCi) {
      continue;
    }

    const hasLockfileCheck =
      /\[\s*-f\s+package-lock\.json\s*\]/.test(block.text) ||
      /\btest\s+-f\s+package-lock\.json\b/.test(block.text) ||
      /\bTest-Path\s+(?:-Path\s+)?['"]?package-lock\.json['"]?/i.test(block.text);
    const hasAnyIfElseFallback =
      /\bif\b[\s\S]*?\bnpm\s+ci\b[\s\S]*?\belse\b[\s\S]*?\bnpm\s+(?:install|i)\b/.test(block.text);
    const hasOrFallbackInstall = /\bnpm\s+ci\b\s*\|\|\s*\bnpm\s+(?:install|i)\b/.test(block.text);
    const hasMissingLockfileHardFail =
      /\[\s*!\s+-f\s+package-lock\.json\s*\][\s\S]*?\bexit\s+1\b/.test(block.text);

    if (hasOrFallbackInstall) {
      continue;
    }

    if (hasMissingLockfileHardFail) {
      violations.push(
        new Violation(
          relativePath,
          block.startLine,
          "npm ci",
          "Repository ignores package-lock.json, so workflows must not fail when the lockfile is absent. Use npm ci/npm install fallback.",
          "error"
        )
      );
      continue;
    }

    if (!hasLockfileCheck || !hasAnyIfElseFallback) {
      violations.push(
        new Violation(
          relativePath,
          block.startLine,
          "npm ci",
          "Repository ignores package-lock.json, so npm ci blocks must include a lockfile presence check and npm install fallback.",
          "error"
        )
      );
    }
  }

  return violations;
}

function findPreCommitInstallHookWriterViolations(relativePath, lines) {
  const violations = [];
  const runBlocks = extractRunBlocks(lines);

  for (const block of runBlocks) {
    const directHostPreCommitInstall =
      /(?:^|[\s;&|])(?:python(?:\d+(?:\.\d+)?)?|py)\s+-m\s+pip\s+install\b[^\n;&|]*\bpre-commit(?:==[^\s;&|]+)?\b/.test(
        block.text
      ) ||
      /(?:^|[\s;&|])pip(?:\d+(?:\.\d+)?)?\s+install\b[^\n;&|]*\bpre-commit(?:==[^\s;&|]+)?\b/.test(
        block.text
      ) ||
      /(?:^|[\s;&|])pipx\s+install\b[^\n;&|]*\bpre-commit(?:==[^\s;&|]+)?\b/.test(block.text) ||
      /(?:^|[\s;&|])uv\s+tool\s+install\b[^\n;&|]*\bpre-commit(?:==[^\s;&|]+)?\b/.test(
        block.text
      ) ||
      /(?:^|[\s;&|])brew\s+install\b[^\n;&|]*\bpre-commit\b/.test(block.text) ||
      /(?:^|[\s;&|])choco\s+install\b[^\n;&|]*\bpre-commit\b/.test(block.text) ||
      /(?:^|[\s;&|])scoop\s+install\b[^\n;&|]*\bpre-commit\b/.test(block.text) ||
      /(?:^|[\s;&|])(?:apt-get|apt|dnf|yum)\s+install\b[^\n;&|]*\bpre-commit\b/.test(block.text) ||
      /(?:^|[\s;&|])pacman\s+(?:-[A-Za-z]*S[A-Za-z]*|--sync)\b[^\n;&|]*\bpre-commit\b/.test(
        block.text
      );
    if (directHostPreCommitInstall) {
      violations.push(
        new Violation(
          relativePath,
          block.startLine,
          "install pre-commit",
          "Do not install pre-commit directly in CI. Use `node scripts/ensure-pre-commit.js` so the pinned version and auto-repair path are shared across Linux, macOS, and Windows.",
          "error"
        )
      );
      continue;
    }

    const directPreCommitRun =
      /(?:^|[\s;&|])pre-commit\s+(?:install(?:\s|$)|install-hooks|run)\b/.test(block.text) ||
      /(?:^|[\s;&|])(?:python(?:\d+(?:\.\d+)?)?|py)\s+-m\s+pre_commit\s+(?:install(?:\s|$)|install-hooks|run)\b/.test(
        block.text
      );
    if (directPreCommitRun) {
      violations.push(
        new Violation(
          relativePath,
          block.startLine,
          "pre-commit run",
          "Do not invoke pre-commit directly in CI. Use `node scripts/ensure-pre-commit.js run ...` or `node scripts/ensure-pre-commit.js install-hooks` so the pinned runner is used consistently.",
          "error"
        )
      );
      continue;
    }

    const invokesPreCommitInstall =
      /\bpre-commit\s+install\b/.test(block.text) ||
      /\b(?:python(?:\d+(?:\.\d+)?)?|py)\s+-m\s+pre_commit\s+install\b/.test(block.text);

    if (!invokesPreCommitInstall) {
      continue;
    }
    if (!/(^|\s)--install-hooks(?:\s|$)/.test(block.text)) {
      continue;
    }

    violations.push(
      new Violation(
        relativePath,
        block.startLine,
        "pre-commit install --install-hooks",
        "Do not run `pre-commit install --install-hooks` in CI. This repo owns native hooks through core.hooksPath=scripts/hooks, and pre-commit refuses to install hook scripts when core.hooksPath is set. Use `node scripts/ensure-pre-commit.js install-hooks` to create hook environments with the pinned runner.",
        "error"
      )
    );
  }

  return violations;
}

function extractJobs(lines) {
  const jobs = [];
  let inJobsBlock = false;
  let jobsIndent = -1;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (!inJobsBlock && /^\s*jobs:\s*$/.test(line)) {
      inJobsBlock = true;
      jobsIndent = indent;
      continue;
    }

    if (!inJobsBlock) {
      continue;
    }

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= jobsIndent) {
      break;
    }

    const jobHeader = /^\s*([A-Za-z0-9_-]+):\s*$/.exec(line);
    if (!jobHeader || indent !== jobsIndent + 2) {
      continue;
    }

    let endLine = lines.length - 1;
    for (let j = i + 1; j < lines.length; j++) {
      const nextLine = lines[j];
      const nextTrimmed = nextLine.trim();
      const nextIndent = getIndent(nextLine);

      if (nextTrimmed.length === 0 || nextTrimmed.startsWith("#")) {
        continue;
      }

      if (nextIndent <= jobsIndent) {
        endLine = j - 1;
        break;
      }

      if (nextIndent === jobsIndent + 2 && /^\s*[A-Za-z0-9_-]+:\s*$/.test(nextLine)) {
        endLine = j - 1;
        break;
      }
    }

    jobs.push({
      id: jobHeader[1],
      startLine: i + 1,
      endLine: endLine + 1,
      indent
    });

    i = endLine;
  }

  return jobs;
}

function extractDefaultRunShellFromBlock(lines, startIndex, endIndex, defaultsIndent) {
  let runIndent = -1;

  for (let i = startIndex + 1; i <= endIndex && i < lines.length; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= defaultsIndent) {
      break;
    }

    if (runIndent === -1 && /^\s*run:\s*$/.test(line) && indent === defaultsIndent + 2) {
      runIndent = indent;
      continue;
    }

    if (runIndent !== -1) {
      if (indent <= runIndent) {
        break;
      }

      const shellMatch = /^\s*shell:\s*["']?([^"'\s#]+)["']?\s*(?:#.*)?$/.exec(line);
      if (shellMatch && indent === runIndent + 2) {
        return shellMatch[1].toLowerCase();
      }
    }
  }

  return null;
}

function extractWorkflowDefaultsShell(lines) {
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (!/^\s*defaults:\s*$/.test(line) || getIndent(line) !== 0) {
      continue;
    }

    return extractDefaultRunShellFromBlock(lines, i, lines.length - 1, 0);
  }

  return null;
}

function extractJobDefaultsShell(lines, job) {
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= job.indent) {
      break;
    }

    if (!/^\s*defaults:\s*$/.test(line) || indent !== job.indent + 2) {
      continue;
    }

    return extractDefaultRunShellFromBlock(lines, i, endIndex, indent);
  }

  return null;
}

function jobTargetsWindows(lines, job) {
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;
  let runsOnValue = null;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= job.indent) {
      break;
    }

    if (indent !== job.indent + 2) {
      continue;
    }

    const runsOnMatch = /^\s*runs-on:\s*(.+?)\s*$/.exec(line);
    if (!runsOnMatch) {
      continue;
    }

    runsOnValue = runsOnMatch[1].trim();
    break;
  }

  if (!runsOnValue) {
    return false;
  }

  if (/\bwindows(?:-[a-z0-9]+)?\b/i.test(runsOnValue)) {
    return true;
  }

  if (!/matrix\./i.test(runsOnValue)) {
    return false;
  }

  let inMatrixBlock = false;
  let matrixIndent = -1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (!inMatrixBlock && /^\s*matrix:\s*$/.test(line)) {
      inMatrixBlock = true;
      matrixIndent = indent;
      continue;
    }

    if (!inMatrixBlock) {
      continue;
    }

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= matrixIndent) {
      inMatrixBlock = false;
      matrixIndent = -1;
      continue;
    }

    if (/\bwindows(?:-[a-z0-9]+)?\b/i.test(line)) {
      return true;
    }
  }

  return false;
}

function extractStepRun(lines, stepStartIndex, stepEndIndex) {
  for (let i = stepStartIndex; i <= stepEndIndex; i++) {
    const line = lines[i];
    const blockRunMatch = /^(\s*)(?:-\s+)?run:\s*[>|][+-]?\s*$/.exec(line);

    if (blockRunMatch) {
      const baseIndent = blockRunMatch[1].length;
      const blockLines = [];
      let j = i + 1;

      while (j <= stepEndIndex) {
        const nextLine = lines[j];
        const trimmed = nextLine.trim();
        const nextIndent = getIndent(nextLine);

        if (trimmed.length > 0 && nextIndent <= baseIndent) {
          break;
        }

        blockLines.push(nextLine.trim());
        j++;
      }

      return {
        line: i + 1,
        text: blockLines.join("\n").trim()
      };
    }

    const inlineRunMatch = /^\s*(?:-\s+)?run:\s*(.+?)\s*$/.exec(line);
    if (inlineRunMatch) {
      return {
        line: i + 1,
        text: inlineRunMatch[1].trim()
      };
    }
  }

  return null;
}

function extractStepShell(lines, stepStartIndex, stepEndIndex) {
  for (let i = stepStartIndex; i <= stepEndIndex; i++) {
    const line = lines[i];

    // Quoted shell string -- captures everything between matching quotes
    // (supports the Git Bash absolute-path escape hatch which contains
    // spaces). The previous regex split on whitespace and returned an
    // empty match for those cases.
    const quotedMatch = /^\s*shell:\s*(['"])(.*?)\1\s*(?:#.*)?$/.exec(line);
    if (quotedMatch) {
      return quotedMatch[2].toLowerCase();
    }

    // Unquoted (single token) shell value: `shell: pwsh`, `shell: bash`.
    const bareMatch = /^\s*shell:\s*([^\s'"#]+)\s*(?:#.*)?$/.exec(line);
    if (bareMatch) {
      return bareMatch[1].toLowerCase();
    }
  }

  return null;
}

function extractStepUses(lines, stepStartIndex, stepEndIndex) {
  for (let i = stepStartIndex; i <= stepEndIndex; i++) {
    const line = lines[i];
    const usesMatch = /^\s*(?:-\s+)?uses:\s*["']?([^"'\s#]+)["']?\s*(?:#.*)?$/i.exec(line);

    if (usesMatch) {
      return usesMatch[1].toLowerCase();
    }
  }

  return null;
}

function extractStepName(lines, stepStartIndex, stepEndIndex) {
  for (let i = stepStartIndex; i <= stepEndIndex; i++) {
    const line = lines[i];
    const nameMatch = /^\s*(?:-\s+)?name:\s*["']?(.+?)["']?\s*(?:#.*)?$/i.exec(line);

    if (nameMatch) {
      return nameMatch[1].trim();
    }
  }

  return null;
}

function extractStepIf(lines, stepStartIndex, stepEndIndex) {
  for (let i = stepStartIndex; i <= stepEndIndex; i++) {
    const line = lines[i];
    const ifMatch = /^\s*if:\s*(.+?)\s*(?:#.*)?$/i.exec(line);

    if (ifMatch) {
      return ifMatch[1].trim();
    }
  }

  return null;
}

function extractStepWithMap(lines, stepStartIndex, stepEndIndex) {
  const values = new Map();
  let withIndent = -1;

  for (let i = stepStartIndex; i <= stepEndIndex; i++) {
    const line = lines[i];
    if (/^\s*with:\s*(?:#.*)?$/i.test(line)) {
      withIndent = getIndent(line);
      continue;
    }

    if (withIndent === -1) {
      continue;
    }

    const trimmed = line.trim();
    const indent = getIndent(line);
    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }
    if (indent <= withIndent) {
      break;
    }

    const keyMatch = /^\s*([A-Za-z0-9_-]+)\s*:\s*(.*?)\s*(?:#.*)?$/.exec(line);
    if (keyMatch && indent === withIndent + 2) {
      values.set(keyMatch[1], keyMatch[2].replace(/^["']|["']$/g, ""));
    }
  }

  return values;
}

function extractStepEnvMap(lines, stepStartIndex, stepEndIndex) {
  const values = new Map();
  let envIndent = -1;

  for (let i = stepStartIndex; i <= stepEndIndex; i++) {
    const line = lines[i];
    if (/^\s*env:\s*(?:#.*)?$/i.test(line)) {
      envIndent = getIndent(line);
      continue;
    }

    if (envIndent === -1) {
      continue;
    }

    const trimmed = line.trim();
    const indent = getIndent(line);
    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }
    if (indent <= envIndent) {
      break;
    }

    const keyMatch = /^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(.*?)\s*(?:#.*)?$/.exec(line);
    if (keyMatch && indent === envIndent + 2) {
      values.set(keyMatch[1], keyMatch[2].replace(/^["']|["']$/g, ""));
    }
  }

  return values;
}

function extractJobSteps(lines, job) {
  const steps = [];
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;
  let stepsStartIndex = -1;
  let stepsIndent = -1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    if (/^\s*steps:\s*$/.test(line) && getIndent(line) === job.indent + 2) {
      stepsStartIndex = i;
      stepsIndent = getIndent(line);
      break;
    }
  }

  if (stepsStartIndex === -1) {
    return steps;
  }

  let i = stepsStartIndex + 1;
  while (i <= endIndex) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      i++;
      continue;
    }

    if (indent <= stepsIndent) {
      break;
    }

    if (!(indent === stepsIndent + 2 && /^\s*-\s+/.test(line))) {
      i++;
      continue;
    }

    const stepStartIndex = i;
    let stepEndIndex = endIndex;

    for (let j = i + 1; j <= endIndex; j++) {
      const nextLine = lines[j];
      const nextTrimmed = nextLine.trim();
      const nextIndent = getIndent(nextLine);

      if (nextTrimmed.length === 0 || nextTrimmed.startsWith("#")) {
        continue;
      }

      if (nextIndent <= stepsIndent) {
        stepEndIndex = j - 1;
        break;
      }

      if (nextIndent === stepsIndent + 2 && /^\s*-\s+/.test(nextLine)) {
        stepEndIndex = j - 1;
        break;
      }
    }

    const run = extractStepRun(lines, stepStartIndex, stepEndIndex);
    steps.push({
      startIndex: stepStartIndex,
      endIndex: stepEndIndex,
      name: extractStepName(lines, stepStartIndex, stepEndIndex),
      if: extractStepIf(lines, stepStartIndex, stepEndIndex),
      shell: extractStepShell(lines, stepStartIndex, stepEndIndex),
      uses: extractStepUses(lines, stepStartIndex, stepEndIndex),
      with: extractStepWithMap(lines, stepStartIndex, stepEndIndex),
      env: extractStepEnvMap(lines, stepStartIndex, stepEndIndex),
      run
    });

    i = stepEndIndex + 1;
  }

  return steps;
}

const BASH_SYNTAX_PATTERNS = [
  {
    label: "if/elif [ ... ] conditional",
    regex: /^(?:if|elif)\s+\[\[?/
  },
  {
    label: "for ... in loop",
    regex: /^for\s+[A-Za-z_][A-Za-z0-9_]*\s+in\b/
  },
  {
    label: "while [ ... ] loop",
    regex: /^while\s+\[\[?/
  },
  {
    label: "until [ ... ] loop",
    regex: /^until\s+\[\[?/
  },
  {
    label: "set -e/-o shell option",
    regex: /^set\s+-[A-Za-z]/
  },
  {
    label: "test -f/-d shell check",
    regex: /^test\s+-[A-Za-z]/
  },
  {
    label: "logical chaining operator (&&/||)",
    regex: /&&|\|\|/
  }
];

function detectBashSyntaxPattern(runText) {
  if (typeof runText !== "string" || runText.trim().length === 0) {
    return null;
  }

  const runLines = runText.split("\n");

  for (const rawLine of runLines) {
    const line = rawLine.trim();
    if (line.length === 0 || line.startsWith("#")) {
      continue;
    }

    for (const pattern of BASH_SYNTAX_PATTERNS) {
      if (pattern.regex.test(line)) {
        return pattern.label;
      }
    }
  }

  return null;
}

function isBashCompatibleShell(shell) {
  return shell === "bash" || shell === "sh";
}

function findWindowsBashPortabilityViolations(relativePath, lines) {
  const violations = [];
  const jobs = extractJobs(lines);
  const workflowDefaultsShell = extractWorkflowDefaultsShell(lines);

  for (const job of jobs) {
    if (!jobTargetsWindows(lines, job)) {
      continue;
    }

    const jobDefaultsShell = extractJobDefaultsShell(lines, job);
    const steps = extractJobSteps(lines, job);

    for (const step of steps) {
      if (!step.run || typeof step.run.text !== "string") {
        continue;
      }

      const bashPattern = detectBashSyntaxPattern(step.run.text);
      if (!bashPattern) {
        continue;
      }

      const effectiveShell = step.shell || jobDefaultsShell || workflowDefaultsShell;
      if (isBashCompatibleShell(effectiveShell)) {
        continue;
      }

      violations.push(
        new Violation(
          relativePath,
          step.run.line,
          bashPattern,
          `Windows-targeting workflow job '${job.id}' uses Bash syntax (${bashPattern}) without a Bash-compatible shell. Add 'shell: bash' to the step or set 'defaults.run.shell: bash' at job/workflow scope.`,
          "error"
        )
      );
    }
  }

  return violations;
}

/**
 * Allowlist of npm scripts whose execution requires git history (origin/<base>
 * refs etc.). The list MUST stay in sync with package.json's `scripts`
 * section. Any npm script that itself transitively invokes
 * `validate:changelog:coverage`, `git diff origin/*`, `git merge-base`, or
 * other ref-dependent commands must be added here so workflows can be checked
 * for a matching full-history checkout.
 *
 * `preflight:pre-push` is included because it chains through
 * `preflight:pre-commit` -> `validate:changelog:coverage`. This is the gap
 * that allowed cross-platform-preflight.yml to slip through the validator.
 *
 * `validate:changelog` (bare) is NOT in this list: it runs
 * scripts/validate-changelog.js without `--check-coverage`, which does not
 * touch origin/<base>. Only `validate:changelog:coverage` does.
 *
 * `validate:all` IS in this list because it chains through
 * `validate:changelog:coverage`.
 */
const NPM_SCRIPTS_REQUIRING_GIT_HISTORY = Object.freeze([
  "validate:changelog:coverage",
  "preflight:pre-commit",
  "preflight:pre-push",
  "validate:all"
]);

function buildNpmRunRegexAlternation(scriptNames) {
  // Escape regex metacharacters present in script names (e.g. ":").
  const escaped = scriptNames.map((name) => name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"));
  // No current prefix collision exists across the script names above
  // (the entries are not prefixes of each other under the regex `\b`
  // word-boundary terminator). The trailing `\b` in the consumer regex
  // ensures e.g. "preflight:pre-commit" would not be matched by a
  // hypothetical "preflight:pre" entry.
  return escaped.join("|");
}

function runTextInvokesChangelogCoverage(runText) {
  if (typeof runText !== "string" || runText.trim().length === 0) {
    return false;
  }

  const npmAlternation = buildNpmRunRegexAlternation(NPM_SCRIPTS_REQUIRING_GIT_HISTORY);
  const npmRunRegex = new RegExp(`npm\\s+run\\s+(?:${npmAlternation})\\b`);

  return (
    /validate-changelog\.js\b[\s\S]*--check-coverage/.test(runText) ||
    /pre-commit\s+run\b[\s\S]*\bvalidate-changelog-policy\b/.test(runText) ||
    npmRunRegex.test(runText) ||
    // Bare `git diff origin/...` / `git merge-base origin/...` / `git log
    // origin/...` style commands require origin/<base>; flag them too.
    /\bgit\s+diff\s+[^|]*origin\//.test(runText) ||
    /\bgit\s+merge-base\s+[^|]*origin\//.test(runText)
  );
}

function stepHasFullHistoryCheckout(lines, step) {
  if (!step || typeof step.uses !== "string" || !step.uses.startsWith("actions/checkout@")) {
    return false;
  }

  for (let i = step.startIndex; i <= step.endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (/^fetch-depth:\s*["']?0["']?\s*(?:#.*)?$/.test(trimmed)) {
      return true;
    }
  }

  return false;
}

/**
 * Detects use of the object-form `runs-on:` with a `group:` key, e.g.:
 *
 *     runs-on:
 *       group: some-runner-group
 *       labels:
 *         - self-hosted
 *
 * Runner groups are not provisioned for this org. The supported contract
 * is labels-only `runs-on`. Unity-credential-using jobs rely on natural
 * per-runner serialization (each self-hosted agent only runs one job at
 * a time, so per-machine Unity-cache workspaces cannot collide) and on
 * per-matrix concurrency expansion when serialization is needed; they no
 * longer share a single org-wide concurrency group.
 *
 * Strategy: walk each job's lines; when we see a `runs-on:` value that
 * is empty (i.e., the value is given via the next-line object block),
 * inspect the indented child block for a `group:` key. Inline mapping
 * forms (`runs-on: { group: ..., labels: [...] }`) are also covered by
 * the inline regex.
 */
function findForbiddenRunsOnGroupViolations(relativePath, lines) {
  const violations = [];
  const jobs = extractJobs(lines);

  for (const job of jobs) {
    const startIndex = job.startLine - 1;
    const endIndex = job.endLine - 1;

    for (let i = startIndex + 1; i <= endIndex; i++) {
      const line = lines[i];
      const trimmed = line.trim();
      const indent = getIndent(line);

      if (trimmed.length === 0 || trimmed.startsWith("#")) {
        continue;
      }

      if (indent <= job.indent) {
        break;
      }

      if (indent !== job.indent + 2) {
        continue;
      }

      // Inline mapping form: `runs-on: { group: foo, labels: [...] }`
      const inlineRunsOn = /^\s*runs-on:\s*\{([^}]*)\}\s*(?:#.*)?$/.exec(line);
      if (inlineRunsOn && /\bgroup\s*:/.test(inlineRunsOn[1])) {
        violations.push(
          new Violation(
            relativePath,
            i + 1,
            line.trim(),
            `Job '${job.id}': runs-on.group is forbidden -- runner groups are not provisioned for this org. Use labels-only runs-on; Unity-credential-using jobs rely on natural per-runner serialization (one job per runner agent) and per-matrix concurrency expansion when needed, not on a shared org-wide concurrency group.`,
            "error"
          )
        );
        continue;
      }

      // Multi-line object form: bare `runs-on:` followed by indented mapping.
      if (!/^\s*runs-on:\s*(?:#.*)?$/.test(line)) {
        continue;
      }

      const runsOnIndent = indent;

      for (let j = i + 1; j <= endIndex; j++) {
        const childLine = lines[j];
        const childTrimmed = childLine.trim();
        const childIndent = getIndent(childLine);

        if (childTrimmed.length === 0 || childTrimmed.startsWith("#")) {
          continue;
        }

        if (childIndent <= runsOnIndent) {
          break;
        }

        if (/^\s*group\s*:/.test(childLine)) {
          violations.push(
            new Violation(
              relativePath,
              j + 1,
              childLine.trim(),
              `Job '${job.id}': runs-on.group is forbidden -- runner groups are not provisioned for this org. Use labels-only runs-on; Unity-credential-using jobs rely on natural per-runner serialization (one job per runner agent) and per-matrix concurrency expansion when needed, not on a shared org-wide concurrency group.`,
              "error"
            )
          );
          break;
        }
      }
    }
  }

  return violations;
}

// Documented self-hosted runner label allowlist. Each entry is the sorted
// (case-sensitive) label set — preserving casing intentionally so that typos
// like `RAM-64Gb` are flagged as non-allowlisted. Order-insensitive.
const SELF_HOSTED_LABEL_ALLOWLIST = [
  ["RAM-64GB", "Windows", "self-hosted"],
  ["RAM-64GB", "Windows", "fast", "self-hosted"],
  ["Linux", "RAM-64GB", "X64", "self-hosted"],
  ["ARM64", "macOS", "self-hosted"]
];

function sortedLabelKey(labels) {
  return labels.slice().sort().join("|");
}

const ALLOWLIST_KEYS = new Set(SELF_HOSTED_LABEL_ALLOWLIST.map((set) => sortedLabelKey(set)));

function formatAllowlistForMessage() {
  return SELF_HOSTED_LABEL_ALLOWLIST.map((set) => `[${set.join(", ")}]`).join(", ");
}

/**
 * Extracts a concurrency.group value (string) and line number from a YAML
 * block whose key starts at the given containing indent (so any directly-
 * nested key sits at containingIndent + 2). Returns null when no
 * concurrency block / group key is found.
 *
 * Supports three forms:
 *
 *   1. Multi-line mapping form:
 *
 *        concurrency:
 *          group: foo
 *          cancel-in-progress: false
 *
 *   2. Inline mapping form:
 *
 *        concurrency: { group: foo, cancel-in-progress: false }
 *
 *   3. Scalar shorthand form (GitHub Actions treats the entire value as the
 *      group name; cancel-in-progress is implicitly false / the default):
 *
 *        concurrency: foo
 *        concurrency: "foo"
 *        concurrency: 'foo'
 *
 *   The shorthand was previously missed by the sentinel/matrix-eviction
 *   checks; that gap is closed here so a single line `concurrency:
 *   wallstop-organization-builds` is detected. For shorthand the returned
 *   cancelInProgress is undefined (GitHub Actions defaults the field).
 *
 * Returns { group, line, cancelInProgress, queue } where line is 1-indexed.
 */
function extractConcurrencyGroupFromBlock(lines, startIndex, endIndex, containingIndent) {
  const targetIndent = containingIndent + 2;

  for (let i = startIndex; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    // Workflow-level scanning passes containingIndent = -2 (so target
    // indent is 0). We still need to stop when we leave the block.
    if (indent < targetIndent && i !== startIndex) {
      break;
    }

    if (indent !== targetIndent) {
      continue;
    }

    // Inline mapping form: `concurrency: { group: foo, ... }`
    const inlineMapMatch = /^\s*concurrency:\s*\{([^}]*)\}\s*(?:#.*)?$/.exec(line);
    if (inlineMapMatch) {
      const inner = inlineMapMatch[1];
      const groupMatch = /\bgroup\s*:\s*["']?([^,"'}]+?)["']?\s*(?:,|$)/.exec(inner);
      const cancelMatch = /\bcancel-in-progress\s*:\s*(true|false)/.exec(inner);
      const queueMatch = /\bqueue\s*:\s*["']?([^,"'}]+?)["']?\s*(?:,|$)/.exec(inner);
      if (groupMatch) {
        return {
          group: groupMatch[1].trim(),
          line: i + 1,
          cancelInProgress: cancelMatch ? cancelMatch[1] === "true" : undefined,
          queue: queueMatch ? queueMatch[1].trim() : undefined
        };
      }
      return null;
    }

    // Multi-line block form: bare `concurrency:` followed by indented mapping.
    if (/^\s*concurrency:\s*(?:#.*)?$/.test(line)) {
      const concurrencyIndent = indent;
      let group = null;
      let groupLine = -1;
      let cancelInProgress;
      let queue;
      for (let j = i + 1; j <= endIndex; j++) {
        const childLine = lines[j];
        const childTrimmed = childLine.trim();
        const childIndent = getIndent(childLine);

        if (childTrimmed.length === 0 || childTrimmed.startsWith("#")) {
          continue;
        }

        if (childIndent <= concurrencyIndent) {
          break;
        }

        const childGroupMatch = /^\s*group\s*:\s*["']?(.+?)["']?\s*(?:#.*)?$/.exec(childLine);
        if (childGroupMatch && group === null) {
          group = childGroupMatch[1].trim();
          groupLine = j + 1;
          continue;
        }
        const childCancelMatch = /^\s*cancel-in-progress\s*:\s*(true|false)\s*(?:#.*)?$/.exec(
          childLine
        );
        if (childCancelMatch) {
          cancelInProgress = childCancelMatch[1] === "true";
        }
        const childQueueMatch = /^\s*queue\s*:\s*["']?(.+?)["']?\s*(?:#.*)?$/.exec(childLine);
        if (childQueueMatch) {
          queue = childQueueMatch[1].trim();
        }
      }

      if (group !== null) {
        return { group, line: groupLine, cancelInProgress, queue };
      }
      return null;
    }

    // Scalar shorthand form: `concurrency: <name>` where the value is a
    // bare identifier or quoted string (NOT an inline mapping, NOT a
    // YAML block/folded scalar, NOT empty, NOT the YAML null marker).
    const shorthandMatch = /^\s*concurrency:\s*(.+?)\s*(?:#.*)?$/.exec(line);
    if (shorthandMatch) {
      const rawValue = shorthandMatch[1].trim();
      // Skip non-scalar leaders: inline mapping `{`, block/folded
      // scalars `|`/`>`, YAML null `~`, or empty (the multi-line case
      // we already returned for above).
      if (
        rawValue.length === 0 ||
        rawValue === "~" ||
        rawValue.startsWith("{") ||
        rawValue.startsWith("|") ||
        rawValue.startsWith(">")
      ) {
        continue;
      }
      const stripped = rawValue.replace(/^(["'])(.*)\1$/, "$2");
      if (stripped.length === 0) {
        continue;
      }
      return {
        group: stripped,
        line: i + 1,
        cancelInProgress: undefined,
        queue: undefined
      };
    }
  }

  return null;
}

/**
 * Extracts a job's concurrency.group value (string) and line number, or
 * null when no concurrency block / group key is found. Delegates to
 * extractConcurrencyGroupFromBlock; preserved as a thin wrapper for
 * backwards-compatible exports and clarity at call sites.
 */
function extractJobConcurrencyGroup(lines, job) {
  return extractConcurrencyGroupFromBlock(
    lines,
    job.startLine, // 1-indexed job header; first child sits at startLine + 0 (0-indexed = job.startLine)
    job.endLine - 1,
    job.indent
  );
}

/**
 * Extracts a workflow-level (top-level) concurrency.group value and line
 * number, or null when no top-level concurrency block / group key is
 * found. Workflow-level concurrency applies to the whole workflow run,
 * not per-matrix-entry, so this is only useful for the sentinel-name
 * guard.
 */
function extractWorkflowConcurrencyGroup(lines) {
  return extractConcurrencyGroupFromBlock(lines, 0, lines.length - 1, -2);
}

/**
 * Returns true when the job declares a `strategy.matrix:` block. Detects the
 * standard multi-line form; this is the only form actually used in this repo.
 *
 * Limitation: the flow-style mapping form `strategy: { matrix: {...},
 * max-parallel: 1 }` is silently unanalyzable by this helper and would
 * return `false`. No active workflow uses flow style; if a future author
 * introduces it, expand this helper before relying on it.
 */
function jobHasMatrix(lines, job) {
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;

  let inStrategy = false;
  let strategyIndent = -1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= job.indent) {
      break;
    }

    if (!inStrategy) {
      if (indent === job.indent + 2 && /^\s*strategy:\s*(?:#.*)?$/.test(line)) {
        inStrategy = true;
        strategyIndent = indent;
      }
      continue;
    }

    if (indent <= strategyIndent) {
      inStrategy = false;
      strategyIndent = -1;
      continue;
    }

    if (/^\s*matrix:\s*(?:#.*)?$/.test(line) && indent === strategyIndent + 2) {
      return true;
    }
  }

  return false;
}

/**
 * Returns the integer value of `strategy.max-parallel:` for the given job, or
 * `null` when the key is absent or not parseable as a positive integer.
 *
 * The check accepts the standard form `max-parallel: <int>` (with optional
 * single or double quoting around the value) nested directly under
 * `strategy:` at the job's `indent + 4` column. This matches every form used
 * in this repository; expressions such as `${{ ... }}` are deliberately not
 * resolved (a non-literal value cannot be statically guaranteed to be 1).
 *
 * Limitations:
 *   - The flow-style mapping form `strategy: { matrix: {...},
 *     max-parallel: 1 }` is silently unanalyzable and returns `null`. No
 *     active workflow uses flow style; if a future author introduces it,
 *     expand this helper before relying on it.
 *   - Float-looking values like `max-parallel: 1.0` are intentionally
 *     rejected (return `null`). GitHub Actions documents `max-parallel`
 *     as an integer, and YAML tooling that round-trips floats can change
 *     the value's representation in surprising ways.
 */
function extractJobMatrixMaxParallel(lines, job) {
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;

  let inStrategy = false;
  let strategyIndent = -1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= job.indent) {
      break;
    }

    if (!inStrategy) {
      if (indent === job.indent + 2 && /^\s*strategy:\s*(?:#.*)?$/.test(line)) {
        inStrategy = true;
        strategyIndent = indent;
      }
      continue;
    }

    if (indent <= strategyIndent) {
      inStrategy = false;
      strategyIndent = -1;
      continue;
    }

    const maxParallelMatch = /^\s*max-parallel:\s*["']?([^"'\s#]+)["']?\s*(?:#.*)?$/.exec(line);
    if (maxParallelMatch && indent === strategyIndent + 2) {
      const raw = maxParallelMatch[1];
      if (/^[0-9]+$/.test(raw)) {
        const parsed = Number.parseInt(raw, 10);
        if (Number.isFinite(parsed) && parsed > 0) {
          return parsed;
        }
      }
      return null;
    }
  }

  return null;
}

/**
 * Returns the job-level `timeout-minutes` as an integer, or null when absent.
 * Only a line at EXACTLY `job.indent + 2` is considered so deeper step-level
 * `timeout-minutes:` declarations (and strategy/matrix keys) are ignored.
 */
function extractJobTimeoutMinutes(lines, job) {
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= job.indent) {
      break;
    }

    if (indent !== job.indent + 2) {
      continue;
    }

    const timeoutMatch = /^\s*timeout-minutes:\s*["']?([0-9]+)["']?\s*(?:#.*)?$/.exec(line);
    if (timeoutMatch) {
      return Number.parseInt(timeoutMatch[1], 10);
    }
  }

  return null;
}

/**
 * Returns a step's OWN `timeout-minutes` as an integer, or null when absent.
 * Only a line at EXACTLY the step key indent (`getIndent(step header) + 2`) is
 * considered, so a deeper `with.timeout-minutes:` (an action input, e.g. the
 * acquire step's `with: { timeout-minutes: "300" }`) is NOT mistaken for the
 * step's own GitHub-Actions `timeout-minutes` clock.
 */
function extractStepTimeoutMinutes(lines, step) {
  const stepKeyIndent = getIndent(lines[step.startIndex]) + 2;

  for (let i = step.startIndex; i <= step.endIndex; i++) {
    const line = lines[i];
    if (getIndent(line) !== stepKeyIndent) {
      continue;
    }

    const timeoutMatch = /^\s*timeout-minutes:\s*["']?([0-9]+)["']?\s*(?:#.*)?$/.exec(line);
    if (timeoutMatch) {
      return Number.parseInt(timeoutMatch[1], 10);
    }
  }

  return null;
}

/**
 * Scans the acquire step's line range for its `timeout-minutes:` input line and
 * returns the 1-based line number (so violations cite the exact input rather
 * than the step header). Falls back to the step header line if not found.
 */
function findAcquireTimeoutLine(lines, acquireStep) {
  for (let i = acquireStep.startIndex; i <= acquireStep.endIndex; i++) {
    if (/^\s*timeout-minutes:\s*/.test(lines[i])) {
      return i + 1;
    }
  }
  return acquireStep.startIndex + 1;
}

function textReferencesUnityCiTests(text) {
  return typeof text === "string" && /run-ci-tests\.ps1/i.test(text);
}

/**
 * Identifies the Unity execution step within a job: the step whose `run` text
 * invokes `run-ci-tests.ps1`, or whose `uses` is the game-ci test runner.
 */
function isUnityRunStep(step) {
  if (step.run && textReferencesUnityCiTests(step.run.text)) {
    return true;
  }
  if (typeof step.uses === "string" && step.uses.includes(GAME_CI_UNITY_TEST_RUNNER_USES)) {
    return true;
  }
  return false;
}

/**
 * Enforces the Unity organization-lock timeout invariant:
 *   job timeout-minutes >= acquire timeout-minutes + UNITY_LOCK_RUN_BUDGET_MINUTES.
 * The acquire input is the lock *poll* budget counted against the job clock, so
 * a job whose timeout cannot also cover the run budget is killed by GitHub while
 * still waiting for the shared Unity activation seat. A job that acquires the
 * lock with no job-level timeout can squat the shared seat indefinitely, so the
 * missing-timeout case is its own error. A `${{ }}` acquire timeout cannot be
 * resolved statically, so the numeric comparison is skipped (job-timeout
 * presence is still enforced).
 *
 * The Unity *run* step also needs its own step-level `timeout-minutes`: a hung
 * editor would otherwise squat the single shared seat for the entire job
 * timeout (the stuck-job-watchdog ignores any in_progress job). The step clock
 * must be >= UNITY_LOCK_RUN_BUDGET_MINUTES and STRICTLY below the job timeout so
 * the step fails first and releases the lock instead of the whole job being
 * cancelled with the seat still held.
 */
function findUnityLockTimeoutViolations(relativePath, lines) {
  const violations = [];
  const jobs = extractJobs(lines);

  for (const job of jobs) {
    const steps = extractJobSteps(lines, job);
    const acquireStep = steps.find((step) => step.uses === ORGANIZATION_BUILD_LOCK_ACQUIRE_USES);

    if (!acquireStep) {
      continue;
    }

    const jobTimeout = extractJobTimeoutMinutes(lines, job);
    if (jobTimeout === null) {
      violations.push(
        new Violation(
          relativePath,
          job.startLine,
          job.id,
          `Job '${job.id}' acquires the organization lock but declares no ` +
            `job-level timeout-minutes; an unbounded job can squat the shared ` +
            `Unity seat.`,
          "error"
        )
      );
    } else {
      const acquireTimeoutRaw = acquireStep.with.get("timeout-minutes");
      if (
        acquireTimeoutRaw !== undefined &&
        !/\$\{\{/.test(acquireTimeoutRaw) &&
        /^[0-9]+$/.test(acquireTimeoutRaw.trim())
      ) {
        const acquireTimeout = Number.parseInt(acquireTimeoutRaw.trim(), 10);
        if (jobTimeout < acquireTimeout + UNITY_LOCK_RUN_BUDGET_MINUTES) {
          violations.push(
            new Violation(
              relativePath,
              findAcquireTimeoutLine(lines, acquireStep),
              `timeout-minutes: "${acquireTimeoutRaw}"`,
              `Job '${job.id}' acquires the organization Unity lock with ` +
                `acquire timeout-minutes (${acquireTimeout}) but its job-level ` +
                `timeout-minutes (${jobTimeout}) is too small: job_timeout ` +
                `(${jobTimeout}) must be >= acquire_timeout (${acquireTimeout}) + ` +
                `RUN_BUDGET (${UNITY_LOCK_RUN_BUDGET_MINUTES}). GitHub counts the ` +
                `lock-wait against the job clock, so the job is killed before its ` +
                `lock wait can finish.`,
              "error"
            )
          );
        }
      }
    }

    // Step-level timeout guard on the Unity run step itself. Only a run that
    // happens AFTER the lock is acquired can squat the shared seat, so restrict
    // the search to steps following the acquire step. (A pre-lock
    // `run-ci-tests.ps1 -GenerateOnly` only scaffolds the ephemeral project and
    // does not hold the Unity seat.)
    const runStep = steps.find(
      (step) => step.startIndex > acquireStep.startIndex && isUnityRunStep(step)
    );
    if (runStep) {
      const stepTimeout = extractStepTimeoutMinutes(lines, runStep);
      if (stepTimeout === null) {
        violations.push(
          new Violation(
            relativePath,
            runStep.startIndex + 1,
            runStep.name || "Unity run step",
            `Job '${job.id}' Unity run step has no step-level timeout-minutes; ` +
              `a hung editor can squat the shared Unity seat for the entire job ` +
              `timeout. Add timeout-minutes: ${UNITY_LOCK_RUN_BUDGET_MINUTES} to ` +
              `the run step.`,
            "error"
          )
        );
      } else {
        if (stepTimeout < UNITY_LOCK_RUN_BUDGET_MINUTES) {
          violations.push(
            new Violation(
              relativePath,
              runStep.startIndex + 1,
              runStep.name || "Unity run step",
              `Job '${job.id}' Unity run step timeout-minutes (${stepTimeout}) ` +
                `is below the run budget: it must be >= ` +
                `${UNITY_LOCK_RUN_BUDGET_MINUTES} so a real Unity run is not ` +
                `prematurely killed while still holding the shared seat.`,
              "error"
            )
          );
        }
        if (jobTimeout !== null && stepTimeout >= jobTimeout) {
          violations.push(
            new Violation(
              relativePath,
              runStep.startIndex + 1,
              runStep.name || "Unity run step",
              `Job '${job.id}' Unity run step timeout-minutes (${stepTimeout}) ` +
                `must be STRICTLY less than the job timeout-minutes ` +
                `(${jobTimeout}) so the step clock fires first and releases the ` +
                `lock, instead of the whole job being cancelled with the shared ` +
                `Unity seat still held.`,
              "error"
            )
          );
        }
      }
    }
  }

  return violations;
}

const CROSS_PLATFORM_PREFLIGHT_WORKFLOW = ".github/workflows/cross-platform-preflight.yml";

function findCrossPlatformPreflightTargetedGateViolations(relativePath, lines) {
  const normalizedPath = String(relativePath || "").replace(/\\/g, "/");
  if (normalizedPath !== CROSS_PLATFORM_PREFLIGHT_WORKFLOW) {
    return [];
  }

  const stepLineIndex = lines.findIndex((line) => {
    const match = /^\s*-\s+name:\s*(.+?)\s*$/.exec(line);
    return match && match[1].replace(/^["']|["']$/g, "") === TARGETED_STEP_NAME;
  });
  const lineNumber = stepLineIndex === -1 ? 1 : stepLineIndex + 1;
  const workflowSource = lines.join("\n");
  const runBlock = extractTargetedStepRunBlock(workflowSource, TARGETED_STEP_NAME);
  if (runBlock === null) {
    return [
      new Violation(
        relativePath,
        lineNumber,
        TARGETED_STEP_NAME,
        `Workflow must keep the '${TARGETED_STEP_NAME}' step with a block-scalar run body so the cross-platform regression gate is parseable before pre-push.`,
        "error"
      )
    ];
  }

  let listedPaths;
  try {
    listedPaths = extractListedTestPaths(runBlock);
  } catch (error) {
    return [
      new Violation(
        relativePath,
        lineNumber,
        "--runTestsByPath",
        `Cross-platform targeted regression test list is not parseable: ${error.message}`,
        "error"
      )
    ];
  }

  if (listedPaths.length === 0) {
    return [
      new Violation(
        relativePath,
        lineNumber,
        "--runTestsByPath",
        "Cross-platform targeted regression step must list at least one .test.js path. Use a bash continued list or a PowerShell array splatted into --runTestsByPath.",
        "error"
      )
    ];
  }

  return [];
}

/**
 * Returns the set of job ids referenced by the job's `needs:` declaration,
 * as a string[] (or empty array when no needs are declared). Supports the
 * scalar form (`needs: foo`), the inline-array form (`needs: [foo, bar]`),
 * and the multi-line block-list form (`needs:` then `  - foo`).
 */
function extractJobNeeds(lines, job) {
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= job.indent) {
      break;
    }

    if (indent !== job.indent + 2) {
      continue;
    }

    const needsMatch = /^\s*needs:\s*(.*?)\s*(?:#.*)?$/.exec(line);
    if (!needsMatch) {
      continue;
    }

    const raw = needsMatch[1].trim();

    if (raw.length === 0) {
      // Multi-line block list form.
      const items = [];
      for (let j = i + 1; j <= endIndex; j++) {
        const childLine = lines[j];
        const childTrimmed = childLine.trim();
        const childIndent = getIndent(childLine);

        if (childTrimmed.length === 0 || childTrimmed.startsWith("#")) {
          continue;
        }

        if (childIndent <= indent) {
          break;
        }

        const itemMatch = /^\s*-\s*["']?([A-Za-z0-9_-]+)["']?\s*(?:#.*)?$/.exec(childLine);
        if (itemMatch) {
          items.push(itemMatch[1]);
        }
      }
      return items;
    }

    if (raw.startsWith("[")) {
      const inner = raw.slice(1, -1);
      return inner
        .split(",")
        .map((part) => part.trim().replace(/^["']|["']$/g, ""))
        .filter((part) => part.length > 0);
    }

    // Scalar form.
    const stripped = raw.replace(/^(["'])(.*)\1$/, "$2").trim();
    if (stripped.length === 0) {
      return [];
    }
    return [stripped];
  }

  return [];
}

/**
 * Returns the job's `runs-on:` value text and the 1-indexed line. The value
 * is the raw text following the colon (without the `runs-on:` prefix); for
 * multi-line block list form the value will be empty and `blockList` will
 * hold the gathered child entries.
 *
 * Result: { line, raw, blockList?: string[] } or null when no `runs-on:` is
 * declared at the job-key indent.
 */
function extractJobRunsOn(lines, job) {
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= job.indent) {
      break;
    }

    if (indent !== job.indent + 2) {
      continue;
    }

    const runsOnMatch = /^\s*runs-on:\s*(.*?)\s*(?:#.*)?$/.exec(line);
    if (!runsOnMatch) {
      continue;
    }

    const raw = runsOnMatch[1].trim();
    const lineNumber = i + 1;

    if (raw.length === 0) {
      // Multi-line: collect block list children or object children.
      const blockList = [];
      for (let j = i + 1; j <= endIndex; j++) {
        const childLine = lines[j];
        const childTrimmed = childLine.trim();
        const childIndent = getIndent(childLine);

        if (childTrimmed.length === 0 || childTrimmed.startsWith("#")) {
          continue;
        }

        if (childIndent <= indent) {
          break;
        }

        const itemMatch = /^\s*-\s*["']?([^"'#\s]+)["']?\s*(?:#.*)?$/.exec(childLine);
        if (itemMatch) {
          blockList.push(itemMatch[1].trim());
        }
      }

      return { line: lineNumber, raw: "", blockList };
    }

    return { line: lineNumber, raw };
  }

  return null;
}

/**
 * Parses an inline array form like `[self-hosted, Windows, RAM-64GB]` or
 * `["self-hosted", "Windows", "RAM-64GB"]` into a string[] of labels.
 * Returns null if the value is not a recognizable inline array.
 *
 * Throws on a trailing-comma form (`[a, b, c,]`); the empty element it
 * produces would otherwise show up as a phantom blank label in downstream
 * error messages and confuse the operator.
 */
function parseInlineLabelArray(raw) {
  const match = /^\[\s*(.*?)\s*\]$/.exec(raw);
  if (!match) {
    return null;
  }

  const inner = match[1].trim();
  if (inner.length === 0) {
    return [];
  }

  const parts = inner.split(",").map((part) => part.trim().replace(/^["']|["']$/g, ""));
  if (parts.some((part) => part.length === 0)) {
    throw new Error(
      `Trailing or duplicate comma in label list '${raw}'. Remove the empty element.`
    );
  }
  return parts;
}

/**
 * Extracts the bash run-text and outputs map of all jobs in the workflow,
 * indexed by jobId. Used to validate that a dynamic runs-on backed by
 * `${{ fromJSON(needs.<jobId>.outputs.<output>) }}` ultimately produces an
 * allowlisted label set.
 */
function extractJobOutputsSourceMap(lines) {
  const jobs = extractJobs(lines);
  const result = {};

  for (const job of jobs) {
    const startIndex = job.startLine - 1;
    const endIndex = job.endLine - 1;
    const outputs = {};

    // Map outputs key -> { stepId, outputKey }
    let inOutputs = false;
    let outputsIndent = -1;
    for (let i = startIndex + 1; i <= endIndex; i++) {
      const line = lines[i];
      const trimmed = line.trim();
      const indent = getIndent(line);

      if (trimmed.length === 0 || trimmed.startsWith("#")) {
        continue;
      }

      if (indent <= job.indent) {
        break;
      }

      if (!inOutputs) {
        if (indent === job.indent + 2 && /^\s*outputs:\s*(?:#.*)?$/.test(line)) {
          inOutputs = true;
          outputsIndent = indent;
        }
        continue;
      }

      if (indent <= outputsIndent) {
        inOutputs = false;
        outputsIndent = -1;
        continue;
      }

      const outputMatch =
        /^\s*([A-Za-z0-9_-]+):\s*\$\{\{\s*steps\.([A-Za-z0-9_-]+)\.outputs\.([A-Za-z0-9_-]+)\s*\}\}\s*(?:#.*)?$/.exec(
          line
        );
      if (outputMatch) {
        outputs[outputMatch[1]] = {
          stepId: outputMatch[2],
          outputKey: outputMatch[3]
        };
      }
    }

    // Collect each step's id + run text so we can resolve outputs->bash.
    const steps = extractJobSteps(lines, job);
    const stepsById = {};
    for (const step of steps) {
      let stepId = null;
      for (let i = step.startIndex; i <= step.endIndex; i++) {
        // Allow the optional `- ` step-list marker before `id:`.
        const stepIdMatch = /^\s*(?:-\s+)?id:\s*["']?([A-Za-z0-9_-]+)["']?\s*(?:#.*)?$/.exec(
          lines[i]
        );
        if (stepIdMatch) {
          stepId = stepIdMatch[1];
          break;
        }
      }
      if (stepId) {
        stepsById[stepId] = step;
      }
    }

    result[job.id] = { outputs, stepsById };
  }

  return result;
}

/**
 * Given an emitting step's run text, lexically scan for every `labels=...`
 * assignment. The value must be a JSON array. Supports two bash quoting
 * conventions used in workflow `echo` statements:
 *
 *   echo 'labels=[...]' >> "$GITHUB_OUTPUT"   # entire pair wrapped in single quotes
 *   echo "labels=[...]" >> "$GITHUB_OUTPUT"   # entire pair wrapped in double quotes
 *   labels=[...]                              # bare assignment
 *
 * Returns the array of parsed label arrays (each a string[]). Malformed
 * JSON yields a null entry so the caller can flag it. Lines that do not
 * contain `labels=` are ignored (they may be `if`/`else`/`fi` shell
 * control flow).
 */
function extractEmittedLabelSetsFromBash(runText) {
  if (typeof runText !== "string" || runText.length === 0) {
    return [];
  }

  const sets = [];

  // Walk the text once and lexically extract every `labels=[...]` payload
  // using balanced-bracket scanning. The previous regex `[^\]]*` would
  // mismatch any label literal that happened to contain a `]` character
  // (rare in practice but easy to step on with `${VAR}` expansions or
  // future label names like `foo[bar]`).
  let cursor = 0;
  while (cursor < runText.length) {
    const labelAnchor = runText.indexOf("labels=", cursor);
    if (labelAnchor === -1) {
      break;
    }
    const openBracket = labelAnchor + "labels=".length;
    if (runText[openBracket] !== "[") {
      // `labels=` without an array follower; skip past the anchor
      // entirely to avoid re-matching the same position.
      cursor = openBracket;
      continue;
    }

    // Walk from openBracket looking for the matching `]` while
    // tracking JSON-style string spans so a `]` inside a string is
    // ignored.
    let depth = 0;
    let inString = false;
    let escape = false;
    let endIndex = -1;
    for (let k = openBracket; k < runText.length; k++) {
      const ch = runText[k];
      if (inString) {
        if (escape) {
          escape = false;
          continue;
        }
        if (ch === "\\") {
          escape = true;
          continue;
        }
        if (ch === '"') {
          inString = false;
        }
        continue;
      }
      if (ch === '"') {
        inString = true;
        continue;
      }
      if (ch === "[") {
        depth++;
        continue;
      }
      if (ch === "]") {
        depth--;
        if (depth === 0) {
          endIndex = k;
          break;
        }
      }
    }

    if (endIndex === -1) {
      // Unterminated `labels=[...`; record as malformed and stop.
      sets.push(null);
      break;
    }

    const payload = runText.slice(openBracket, endIndex + 1);
    try {
      const parsed = JSON.parse(payload);
      if (Array.isArray(parsed)) {
        sets.push(parsed.map((item) => String(item)));
      } else {
        sets.push(null);
      }
    } catch (_error) {
      sets.push(null);
    }

    cursor = endIndex + 1;
  }

  return sets;
}

/**
 * Checks for native GitHub concurrency reuse of the organization Unity lock
 * name. GitHub native concurrency is repository-scoped and serializes whole
 * jobs, so it cannot provide the organization-level lock while still allowing
 * pre-Unity work to split across eligible runners. The lock name belongs in
 * the central ambiguous-organization-build-lock action inputs instead.
 */
function findForbiddenSharedConcurrencyViolations(relativePath, lines) {
  const violations = [];

  // Workflow-level scan first; the organization lock name is forbidden as a
  // native concurrency group at any scope.
  const workflowConcurrency = extractWorkflowConcurrencyGroup(lines);
  if (workflowConcurrency && workflowConcurrency.group === "wallstop-organization-builds") {
    violations.push(
      new Violation(
        relativePath,
        workflowConcurrency.line,
        `concurrency.group: ${workflowConcurrency.group}`,
        `Workflow-level concurrency.group 'wallstop-organization-builds' is forbidden. GitHub native concurrency is repository-scoped and serializes entire jobs; use the central Ambiguous-Interactive/ambiguous-organization-build-lock acquire/release actions with lock-name: wallstop-organization-builds instead.`,
        "error"
      )
    );
  }

  const jobs = extractJobs(lines);
  for (const job of jobs) {
    const concurrency = extractJobConcurrencyGroup(lines, job);
    if (!concurrency) {
      continue;
    }

    if (concurrency.group === "wallstop-organization-builds") {
      violations.push(
        new Violation(
          relativePath,
          concurrency.line,
          `concurrency.group: ${concurrency.group}`,
          `Job '${job.id}': concurrency.group 'wallstop-organization-builds' is forbidden. GitHub native concurrency is repository-scoped and would serialize the whole job before a runner is assigned; use the central Ambiguous-Interactive/ambiguous-organization-build-lock acquire/release actions with lock-name: wallstop-organization-builds instead.`,
          "error"
        )
      );
    }
  }

  return violations;
}

/**
 * Checks for jobs that combine `strategy.matrix:` with a shared
 * `concurrency.group:` that exposes the matrix-eviction footgun. GitHub
 * Actions concurrency historically reserved one in-progress slot and one
 * pending slot per group, so without mitigation every third matrix entry to
 * enqueue evicted the previously-queued one.
 *
 * A matrix + shared concurrency group is permitted IFF either:
 *   (a) The group expression includes a `${{ matrix.* }}` token, so each
 *       matrix entry occupies its own per-combination slot (no eviction by
 *       construction).
 *   (b) The concurrency block declares `queue: max` and
 *       `cancel-in-progress: false`, so pending entries are retained.
 *   (c) The strategy declares `max-parallel: 1`, which serializes matrix
 *       entries internally to the workflow run. Entries 2..N queue inside
 *       GitHub's matrix engine, not inside the concurrency group, so the
 *       group sees exactly one entrant per workflow run.
 *
 * Anything else is a violation.
 *
 * Workflow-level concurrency is intentionally not checked here: a workflow-
 * level concurrency group applies to the whole workflow run, not to each
 * matrix entry of a single job inside it. Matrix entries within one run all
 * share the workflow-level group by definition, so the per-matrix-eviction
 * concept (where successive matrix entries kick each other out) does not
 * apply. The sentinel-name guard above already covers the workflow-level
 * case.
 */
function findMatrixConcurrencyEvictionViolations(relativePath, lines) {
  const violations = [];
  const jobs = extractJobs(lines);

  for (const job of jobs) {
    if (!jobHasMatrix(lines, job)) {
      continue;
    }

    const concurrency = extractJobConcurrencyGroup(lines, job);
    if (!concurrency) {
      continue;
    }

    // Escape hatch (a): per-entry slot via ${{ matrix.* }} expansion.
    if (/\$\{\{\s*matrix\./.test(concurrency.group)) {
      continue;
    }

    // Escape hatch (b): larger queue with no cancellation.
    if (concurrency.queue === "max" && concurrency.cancelInProgress === false) {
      continue;
    }

    // Escape hatch (c): explicit serialization via max-parallel: 1.
    const maxParallel = extractJobMatrixMaxParallel(lines, job);
    if (maxParallel === 1) {
      continue;
    }

    const maxParallelDescription =
      maxParallel === null
        ? "no strategy.max-parallel declaration"
        : `strategy.max-parallel: ${maxParallel}`;

    violations.push(
      new Violation(
        relativePath,
        concurrency.line,
        `concurrency.group: ${concurrency.group}`,
        `Job '${job.id}' combines strategy.matrix with a shared concurrency.group ('${concurrency.group}') (${maxParallelDescription}). Without mitigation, GitHub Actions retains only 1 running + 1 pending slot per group, so every third matrix entry to enqueue will cancel the previously-queued one. Allowed escape hatches: (a) expand the group with at least one \${{ matrix.* }} expression so each combo gets its own slot, (b) declare 'queue: max' with 'cancel-in-progress: false', or (c) declare 'strategy.max-parallel: 1' so matrix entries serialize internally. Otherwise drop the job-level concurrency block entirely.`,
        "error"
      )
    );
  }

  return violations;
}

function findConcurrencyQueueViolations(relativePath, lines) {
  const violations = [];
  const allConcurrency = [];
  const workflowConcurrency = extractWorkflowConcurrencyGroup(lines);
  if (workflowConcurrency) {
    allConcurrency.push({ label: "Workflow-level", concurrency: workflowConcurrency });
  }
  for (const job of extractJobs(lines)) {
    const concurrency = extractJobConcurrencyGroup(lines, job);
    if (concurrency) {
      allConcurrency.push({ label: `Job '${job.id}'`, concurrency });
    }
  }

  for (const { label, concurrency } of allConcurrency) {
    if (concurrency.queue === "max" && concurrency.cancelInProgress === true) {
      violations.push(
        new Violation(
          relativePath,
          concurrency.line,
          "queue: max",
          `${label}: queue: max cannot be combined with cancel-in-progress: true. Use cancel-in-progress: false or remove queue: max.`,
          "error"
        )
      );
    }
  }

  return violations;
}

const GAME_CI_TEST_RUNNER_ALLOWED_INPUTS = new Set([
  "unityVersion",
  "customImage",
  "projectPath",
  "customParameters",
  "testMode",
  "coverageOptions",
  "artifactsPath",
  "useHostNetwork",
  "sshAgent",
  "sshPublicKeysDirectoryPath",
  "gitPrivateToken",
  "githubToken",
  "checkName",
  "packageMode",
  "scopedRegistryUrl",
  "registryScopes",
  "chownFilesTo",
  "dockerCpuLimit",
  "dockerMemoryLimit",
  "dockerIsolationMode",
  "unityLicensingServer",
  "containerRegistryRepository",
  "containerRegistryImageVersion",
  "runAsHostUser"
]);

function findGameCiTestRunnerInputViolations(relativePath, lines) {
  const violations = [];
  for (const job of extractJobs(lines)) {
    const steps = extractJobSteps(lines, job);
    for (const step of steps) {
      if (step.uses !== "game-ci/unity-test-runner@v4") {
        continue;
      }
      for (const key of step.with.keys()) {
        if (!GAME_CI_TEST_RUNNER_ALLOWED_INPUTS.has(key)) {
          violations.push(
            new Violation(
              relativePath,
              step.startIndex + 1,
              `${key}:`,
              `Job '${job.id}' uses unsupported game-ci/unity-test-runner@v4 input '${key}'. Remove it or update GAME_CI_TEST_RUNNER_ALLOWED_INPUTS after verifying the upstream action supports it.`,
              "error"
            )
          );
        }
      }
    }
  }
  return violations;
}

function findUnityGameCiLockAndPreflightViolations(relativePath, lines) {
  const violations = [];
  for (const job of extractJobs(lines)) {
    const steps = extractJobSteps(lines, job);
    for (let index = 0; index < steps.length; index++) {
      const step = steps[index];
      if (step.uses !== "game-ci/unity-test-runner@v4") {
        continue;
      }

      const acquireIndex = steps.findIndex(
        (candidate) =>
          candidate.uses === ORGANIZATION_BUILD_LOCK_ACQUIRE_USES &&
          candidate.startIndex < step.startIndex
      );
      const licensePreflightIndex = steps.findIndex(
        (candidate) =>
          candidate.uses === "./.github/actions/validate-unity-license" &&
          candidate.startIndex < step.startIndex
      );
      const releaseIndex = steps.findIndex(
        (candidate) =>
          candidate.uses === ORGANIZATION_BUILD_LOCK_RELEASE_USES &&
          candidate.startIndex > step.startIndex
      );
      const acquire = acquireIndex === -1 ? null : steps[acquireIndex];
      const licensePreflight = licensePreflightIndex === -1 ? null : steps[licensePreflightIndex];
      const release = releaseIndex === -1 ? null : steps[releaseIndex];

      if (!acquire) {
        violations.push(
          new Violation(
            relativePath,
            step.startIndex + 1,
            "game-ci/unity-test-runner@v4",
            `Job '${job.id}' runs Unity without first acquiring the central organization lock. Add Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1 before game-ci.`,
            "error"
          )
        );
      } else if (acquire.with.get("lock-name") !== "wallstop-organization-builds") {
        violations.push(
          new Violation(
            relativePath,
            acquire.startIndex + 1,
            "lock-name:",
            `Job '${job.id}' must acquire lock-name: wallstop-organization-builds before game-ci.`,
            "error"
          )
        );
      }

      if (!licensePreflight) {
        violations.push(
          new Violation(
            relativePath,
            step.startIndex + 1,
            "game-ci/unity-test-runner@v4",
            `Job '${job.id}' runs Unity without first validating Unity license secrets. Add ./.github/actions/validate-unity-license before game-ci.`,
            "error"
          )
        );
      } else if (acquire && licensePreflight.startIndex > acquire.startIndex) {
        violations.push(
          new Violation(
            relativePath,
            licensePreflight.startIndex + 1,
            "validate-unity-license",
            `Job '${job.id}' validates Unity license secrets after acquiring the organization lock. Move ./.github/actions/validate-unity-license before acquire-build-lock so missing secrets do not block the shared Unity seat.`,
            "error"
          )
        );
      }

      if (
        acquire &&
        acquire.env.get("BUILD_LOCK_TOKEN") !== "${{ secrets.ORG_BUILD_LOCK_TOKEN }}"
      ) {
        violations.push(
          new Violation(
            relativePath,
            acquire.startIndex + 1,
            "BUILD_LOCK_TOKEN:",
            `Job '${job.id}' acquire step must pass BUILD_LOCK_TOKEN: \${{ secrets.ORG_BUILD_LOCK_TOKEN }}.`,
            "error"
          )
        );
      }

      if (!release) {
        violations.push(
          new Violation(
            relativePath,
            step.startIndex + 1,
            "game-ci/unity-test-runner@v4",
            `Job '${job.id}' runs Unity without a later central-lock release step. Add Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/release-build-lock@v1 with if: always().`,
            "error"
          )
        );
      } else {
        if (
          acquire &&
          (release.with.get("holder-id-suffix") || "default") !==
            (acquire.with.get("holder-id-suffix") || "default")
        ) {
          violations.push(
            new Violation(
              relativePath,
              release.startIndex + 1,
              "holder-id-suffix:",
              `Job '${job.id}' release holder-id-suffix must match the acquire step so the same holder can release the lock.`,
              "error"
            )
          );
        }
        if (release.with.get("lock-name") !== "wallstop-organization-builds") {
          violations.push(
            new Violation(
              relativePath,
              release.startIndex + 1,
              "lock-name:",
              `Job '${job.id}' must release lock-name: wallstop-organization-builds after game-ci.`,
              "error"
            )
          );
        }
        if (release.env.get("BUILD_LOCK_TOKEN") !== "${{ secrets.ORG_BUILD_LOCK_TOKEN }}") {
          violations.push(
            new Violation(
              relativePath,
              release.startIndex + 1,
              "BUILD_LOCK_TOKEN:",
              `Job '${job.id}' release step must pass BUILD_LOCK_TOKEN: \${{ secrets.ORG_BUILD_LOCK_TOKEN }}.`,
              "error"
            )
          );
        }
        if (release.if !== "always()") {
          violations.push(
            new Violation(
              relativePath,
              release.startIndex + 1,
              "if:",
              `Job '${job.id}' central-lock release step must declare if: always() so failed Unity jobs do not leave the organization lock held.`,
              "error"
            )
          );
        }
      }
    }
  }
  return violations;
}

// Lowercased `uses` ref for the license seat-return backstop action.
const RETURN_UNITY_LICENSE_USES = "./.github/actions/return-unity-license";

// True when a relative workflow path lives under .github/workflows-disabled and
// should therefore be skipped by the active-workflow-only license rules. The
// retired UNITY_LICENSING_SERVER secret, the required serial secrets, and the
// license return step only matter for ACTIVE workflows; disabled archives are
// exempt.
function isDisabledWorkflowPath(relativePath) {
  return /(^|\/)workflows-disabled\//.test(relativePath);
}

// True when a step runs Unity natively: a `run:` block invoking run-ci-tests.ps1
// OR `uses: game-ci/unity-test-runner@v4`. Mirrors isUnityRunStep in
// unity-license-leak-safety.test.js.
function stepRunsUnityNatively(step) {
  if (!step) {
    return false;
  }
  if (step.uses === "game-ci/unity-test-runner@v4") {
    return true;
  }
  if (stepRunsUnityCiTestsDirectly(step) && !stepRunsUnityGenerateOnly(step)) {
    return true;
  }
  return false;
}

function stepRunsUnityCiTestsDirectly(step) {
  return Boolean(step?.run && textReferencesUnityCiTests(step.run.text));
}

function extractUnityCiTestCommandSegments(text) {
  const starts = [...text.matchAll(/run-ci-tests\.ps1/gi)].map((match) => match.index);
  return starts.map((start, index) => text.slice(start, starts[index + 1] ?? text.length));
}

function unityCiTestCommandSegmentIsGenerateOnly(segment) {
  if (/-GenerateOnly(?:\s*:)?\s*(?:\$?false|0)\b/i.test(segment)) {
    return false;
  }
  return (
    /-GenerateOnly(?:\s|$|`)/i.test(segment) ||
    /-GenerateOnly(?:\s*:)?\s*(?:\$?true|1)\b/i.test(segment)
  );
}

function stepRunsUnityGenerateOnly(step) {
  if (!step?.run || typeof step.run.text !== "string") {
    return false;
  }
  const commandSegments = extractUnityCiTestCommandSegments(step.run.text);
  if (commandSegments.length === 0) {
    return false;
  }
  return commandSegments.every(unityCiTestCommandSegmentIsGenerateOnly);
}

function stripPowerShellLineComments(text) {
  return text
    .split(/\r?\n/)
    .map((line) => {
      let inSingleQuote = false;
      let inDoubleQuote = false;

      for (let index = 0; index < line.length; index++) {
        const char = line[index];
        const nextChar = line[index + 1];

        if (char === "`") {
          index++;
          continue;
        }

        if (char === "'" && !inDoubleQuote) {
          if (inSingleQuote && nextChar === "'") {
            index++;
            continue;
          }
          inSingleQuote = !inSingleQuote;
          continue;
        }

        if (char === '"' && !inSingleQuote) {
          inDoubleQuote = !inDoubleQuote;
          continue;
        }

        if (char === "#" && !inSingleQuote && !inDoubleQuote) {
          return line.slice(0, index);
        }
      }

      return line;
    })
    .join("\n");
}

function stripPowerShellLineComment(line) {
  return stripPowerShellLineComments(line);
}

function splitPowerShellStatements(text) {
  const statements = [];
  let current = "";
  let inSingleQuote = false;
  let inDoubleQuote = false;

  for (let index = 0; index < text.length; index++) {
    const char = text[index];
    const nextChar = text[index + 1];

    if (char === "`") {
      current += char;
      if (index + 1 < text.length) {
        current += nextChar;
        index++;
      }
      continue;
    }

    if (char === "'" && !inDoubleQuote) {
      current += char;
      if (inSingleQuote && nextChar === "'") {
        current += nextChar;
        index++;
        continue;
      }
      inSingleQuote = !inSingleQuote;
      continue;
    }

    if (char === '"' && !inSingleQuote) {
      current += char;
      inDoubleQuote = !inDoubleQuote;
      continue;
    }

    if (char === ";" && !inSingleQuote && !inDoubleQuote) {
      if (current.trim().length > 0) {
        statements.push(current.trim());
      }
      current = "";
      continue;
    }

    current += char;
  }

  if (current.trim().length > 0) {
    statements.push(current.trim());
  }

  return statements;
}

function extractLogicalPowerShellCommandRecords(text) {
  const commands = [];
  let current = "";

  const flushCurrent = () => {
    for (const statement of splitPowerShellStatements(current)) {
      commands.push({
        text: statement,
        beforeText: commands.map((command) => command.text).join("\n")
      });
    }
    current = "";
  };

  for (const rawLine of text.split(/\r?\n/)) {
    const continues = /`\s*$/.test(rawLine.trimEnd());
    const strippedLine = stripPowerShellLineComment(rawLine).trimEnd();
    const withoutContinuation = continues ? strippedLine.replace(/`\s*$/, "") : strippedLine;
    current = current ? `${current} ${withoutContinuation.trim()}` : withoutContinuation.trim();
    if (!continues && current.length > 0) {
      flushCurrent();
    }
  }

  if (current.length > 0) {
    flushCurrent();
  }

  return commands;
}

function extractLogicalPowerShellCommands(text) {
  return extractLogicalPowerShellCommandRecords(text).map((command) => command.text);
}

function extractCiManagedEnsureEditorCommands(step) {
  if (!step?.run || typeof step.run.text !== "string") {
    return [];
  }
  return extractLogicalPowerShellCommandRecords(step.run.text).filter(
    (command) => /ensure-editor\.ps1/i.test(command.text) && /-CiManagedOnly\b/i.test(command.text)
  );
}

function extractCiManagedEnsureEditorCommandTexts(step) {
  return extractCiManagedEnsureEditorCommands(step).map((command) => command.text);
}

function stepRunsCiManagedEnsureEditor(step) {
  return extractCiManagedEnsureEditorCommandTexts(step).length > 0;
}

function stepExportsUnityEditorPath(step) {
  if (!step?.run || typeof step.run.text !== "string") {
    return false;
  }
  const text = stripPowerShellLineComments(step.run.text);
  return /UNITY_EDITOR_PATH\s*=\s*\$editor/i.test(text) && /GITHUB_ENV/i.test(text);
}

function normalizePowerShellArgumentValue(value) {
  if (typeof value !== "string") {
    return "";
  }
  return value.trim().replace(/^(["'])(.*)\1$/, "$2");
}

function extractUnityProvisioningProfileArguments(step) {
  return extractCiManagedEnsureEditorCommands(step).flatMap((command) =>
    extractUnityProvisioningProfileArgumentsFromCommand(command.text)
  );
}

function extractUnityProvisioningProfileArgumentsFromCommand(commandText) {
  const args = [];
  const profileArgPattern =
    /-ProvisioningProfile\b(?:\s*[:=]\s*|\s+)(?:"([^"]+)"|'([^']+)'|([^\s`]+))/gi;
  let match;
  while ((match = profileArgPattern.exec(commandText)) !== null) {
    args.push(normalizePowerShellArgumentValue(match[1] ?? match[2] ?? match[3]));
  }
  return args.filter((arg) => arg.length > 0);
}

function stepHasExplicitUnityProvisioningProfile(step) {
  return extractUnityProvisioningProfileArguments(step).length > 0;
}

function stepRequiresHealthyExistingUnityEditor(step) {
  return extractCiManagedEnsureEditorCommands(step).some((command) =>
    /-RequireHealthyExisting\b/i.test(command.text)
  );
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function profileVariableAssignmentMatches(beforeText, variableName) {
  const escapedVariableName = escapeRegExp(variableName.replace(/^\$/, ""));
  const testModeExpression = String.raw`['"]?\$\{\{\s*(?:matrix|inputs)\.test-mode\s*\}\}['"]?`;
  const assignments = [];
  const dynamicPattern = new RegExp(
    String.raw`\$${escapedVariableName}\s*=\s*if\s*\(\s*${testModeExpression}\s*-eq\s*['"]standalone['"]\s*\)\s*\{\s*['"](EditorOnly|StandaloneWindowsIl2Cpp)['"]\s*\}\s*else\s*\{\s*['"](EditorOnly|StandaloneWindowsIl2Cpp)['"]\s*\}`,
    "gi"
  );
  let dynamicMatch;
  while ((dynamicMatch = dynamicPattern.exec(beforeText)) !== null) {
    assignments.push({
      index: dynamicMatch.index,
      type: "dynamic",
      standalone: dynamicMatch[1],
      nonStandalone: dynamicMatch[2]
    });
  }

  const staticPattern = new RegExp(
    String.raw`\$${escapedVariableName}\s*=\s*['"](EditorOnly|StandaloneWindowsIl2Cpp|Android|Full)['"]`,
    "gi"
  );
  let staticMatch;
  while ((staticMatch = staticPattern.exec(beforeText)) !== null) {
    assignments.push({
      index: staticMatch.index,
      type: "static",
      profile: staticMatch[1]
    });
  }

  const anyAssignmentPattern = new RegExp(String.raw`\$${escapedVariableName}\s*=`, "gi");
  let lastAssignmentIndex = -1;
  let anyAssignmentMatch;
  while ((anyAssignmentMatch = anyAssignmentPattern.exec(beforeText)) !== null) {
    lastAssignmentIndex = anyAssignmentMatch.index;
  }

  if (lastAssignmentIndex === -1) {
    return null;
  }

  const recognizedLastAssignment = assignments.find(
    (assignment) => assignment.index === lastAssignmentIndex
  );
  return recognizedLastAssignment || { index: lastAssignmentIndex, type: "unknown" };
}

function stepProvidesUnityProvisioningProfile(step, profile) {
  return extractCiManagedEnsureEditorCommands(step).some((command) => {
    const profileArgs = extractUnityProvisioningProfileArgumentsFromCommand(command.text);

    return profileArgs.some((arg) => {
      if (arg.toLowerCase() === profile.toLowerCase()) {
        return true;
      }
      if (!arg.startsWith("$")) {
        return false;
      }

      const assignment = profileVariableAssignmentMatches(command.beforeText, arg);
      if (!assignment) {
        return false;
      }
      if (assignment.type === "static") {
        return assignment.profile.toLowerCase() === profile.toLowerCase();
      }
      if (assignment.type !== "dynamic") {
        return false;
      }
      return (
        assignment.standalone === "StandaloneWindowsIl2Cpp" &&
        assignment.nonStandalone === "EditorOnly" &&
        (profile === "StandaloneWindowsIl2Cpp" || profile === "EditorOnly")
      );
    });
  });
}

function directUnityRunRequiresStandaloneProfile(runSteps) {
  return runSteps.some((step) => /-TestMode\s+['"]?standalone\b/i.test(step.run.text));
}

function directUnityRunUsesDynamicTestMode(runSteps) {
  return runSteps.some((step) => /\b(matrix|inputs)\.test-mode\b/i.test(step.run.text));
}

function directUnityRunRequiresEditorOnlyProfile(runSteps) {
  return runSteps.some((step) => /-TestMode\s+['"]?(editmode|playmode)\b/i.test(step.run.text));
}

function stripYamlScalarQuotes(value) {
  return value.trim().replace(/^(["'])(.*)\1$/, "$2");
}

function parseInlineYamlList(value) {
  const trimmed = value.trim().replace(/\s+#.*$/, "");
  if (!trimmed.startsWith("[") || !trimmed.endsWith("]")) {
    return null;
  }

  return trimmed
    .slice(1, -1)
    .split(",")
    .map((entry) => stripYamlScalarQuotes(entry))
    .filter((entry) => entry.length > 0);
}

function parseLiteralMatrixScalar(value) {
  const trimmed = value.trim().replace(/\s+#.*$/, "");
  if (trimmed.length === 0) {
    return null;
  }
  if (/\$\{\{/.test(trimmed) || trimmed.startsWith("{") || trimmed.startsWith("|")) {
    return null;
  }
  if (trimmed.startsWith("[")) {
    return parseInlineYamlList(trimmed);
  }
  return [stripYamlScalarQuotes(trimmed)];
}

function extractJobMatrixValues(lines, job, matrixKey) {
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;
  const escapedKey = matrixKey.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const keyPattern = new RegExp(`^\\s*${escapedKey}\\s*:\\s*(.*?)\\s*(?:#.*)?$`);

  let inStrategy = false;
  let strategyIndent = -1;
  let inMatrix = false;
  let matrixIndent = -1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }
    if (indent <= job.indent) {
      break;
    }

    if (!inStrategy) {
      if (indent === job.indent + 2 && /^\s*strategy:\s*(?:#.*)?$/.test(line)) {
        inStrategy = true;
        strategyIndent = indent;
      }
      continue;
    }

    if (indent <= strategyIndent) {
      break;
    }

    if (!inMatrix) {
      if (indent === strategyIndent + 2 && /^\s*matrix:\s*(?:#.*)?$/.test(line)) {
        inMatrix = true;
        matrixIndent = indent;
      }
      continue;
    }

    if (indent <= matrixIndent) {
      break;
    }

    const keyMatch = keyPattern.exec(line);
    if (!keyMatch || indent !== matrixIndent + 2) {
      continue;
    }

    const inlineValues = parseLiteralMatrixScalar(keyMatch[1]);
    if (inlineValues !== null) {
      return inlineValues;
    }

    const values = [];
    for (let j = i + 1; j <= endIndex; j++) {
      const childLine = lines[j];
      const childTrimmed = childLine.trim();
      const childIndent = getIndent(childLine);
      if (childTrimmed.length === 0 || childTrimmed.startsWith("#")) {
        continue;
      }
      if (childIndent <= indent) {
        break;
      }
      const listMatch = /^\s*-\s+(.+?)\s*(?:#.*)?$/.exec(childLine);
      if (listMatch) {
        const parsed = parseLiteralMatrixScalar(listMatch[1]);
        if (parsed === null || parsed.length !== 1) {
          return null;
        }
        values.push(parsed[0]);
      }
    }
    return values.length > 0 ? values : null;
  }

  return null;
}

function dynamicUnityTestModeRequiresStandaloneProfile(lines, job, runSteps) {
  if (!directUnityRunUsesDynamicTestMode(runSteps)) {
    return false;
  }
  if (runSteps.some((step) => /\binputs\.test-mode\b/i.test(step.run.text))) {
    return true;
  }
  const values = extractJobMatrixValues(lines, job, "test-mode");
  if (!values) {
    return true;
  }
  return values.some((value) => value.toLowerCase() === "standalone");
}

function dynamicUnityTestModeRequiresEditorOnlyProfile(lines, job, runSteps) {
  if (!directUnityRunUsesDynamicTestMode(runSteps)) {
    return false;
  }
  if (runSteps.some((step) => /\binputs\.test-mode\b/i.test(step.run.text))) {
    return true;
  }
  const values = extractJobMatrixValues(lines, job, "test-mode");
  if (!values) {
    return true;
  }
  return values.some((value) => /^(editmode|playmode)$/i.test(value));
}

function stepProvisionsUnityEditorForCi(step) {
  return (
    stepRunsCiManagedEnsureEditor(step) &&
    stepHasExplicitUnityProvisioningProfile(step) &&
    stepExportsUnityEditorPath(step)
  );
}

function findUnityNativeProvisioningViolations(relativePath, lines) {
  const violations = [];
  if (isDisabledWorkflowPath(relativePath)) {
    return violations;
  }

  for (const job of extractJobs(lines)) {
    const steps = extractJobSteps(lines, job);
    const ciManagedEnsureSteps = steps.filter(stepRunsCiManagedEnsureEditor);
    for (const step of ciManagedEnsureSteps) {
      for (const command of extractCiManagedEnsureEditorCommands(step)) {
        if (extractUnityProvisioningProfileArgumentsFromCommand(command.text).length > 0) {
          continue;
        }
        violations.push(
          new Violation(
            relativePath,
            step.startIndex + 1,
            "ensure-editor.ps1 -ProvisioningProfile",
            `Job '${job.id}' runs ensure-editor.ps1 -CiManagedOnly without an explicit provisioning profile. Pass -ProvisioningProfile EditorOnly, StandaloneWindowsIl2Cpp, Android, or Full so CI provisioning cannot accidentally install unrelated Unity modules.`,
            "error"
          )
        );
      }
      if (!stepRequiresHealthyExistingUnityEditor(step)) {
        violations.push(
          new Violation(
            relativePath,
            step.startIndex + 1,
            "ensure-editor.ps1 -RequireHealthyExisting",
            `Job '${job.id}' runs ensure-editor.ps1 -CiManagedOnly without -RequireHealthyExisting. Unity test jobs must fail fast instead of installing or repairing editors/modules; run scripts/unity/maintain-windows-runner.ps1 or the runner-bootstrap workflow for repairs.`,
            "error"
          )
        );
      }
    }

    const runSteps = steps.filter(stepRunsUnityCiTestsDirectly);
    if (runSteps.length === 0) {
      continue;
    }

    const nonGenerateOnlyRunSteps = runSteps.filter((step) => !stepRunsUnityGenerateOnly(step));
    const acquireStep = steps.find((step) => step.uses === ORGANIZATION_BUILD_LOCK_ACQUIRE_USES);
    const provisionStep = steps.find(stepProvisionsUnityEditorForCi);
    const firstRunStep = runSteps[0];
    const firstNonGenerateOnlyRunStep = nonGenerateOnlyRunSteps[0];

    if (!acquireStep && firstNonGenerateOnlyRunStep) {
      violations.push(
        new Violation(
          relativePath,
          firstNonGenerateOnlyRunStep.startIndex + 1,
          "run-ci-tests.ps1",
          `Job '${job.id}' runs Unity tests directly but has no organization lock acquire step. Add acquire-build-lock before non-GenerateOnly run-ci-tests.ps1 steps and provision the editor before that lock.`,
          "error"
        )
      );
    }

    if (!provisionStep) {
      violations.push(
        new Violation(
          relativePath,
          firstRunStep.startIndex + 1,
          "ensure-editor.ps1 -CiManagedOnly -ProvisioningProfile",
          `Job '${job.id}' runs run-ci-tests.ps1 without a prior CI-managed editor provisioning step. Add a pre-lock step that runs scripts/unity/ensure-editor.ps1 -CiManagedOnly -ProvisioningProfile <profile> and writes UNITY_EDITOR_PATH=$editor to $GITHUB_ENV.`,
          "error"
        )
      );
      continue;
    }

    for (const runStep of runSteps) {
      if (provisionStep.startIndex > runStep.startIndex) {
        violations.push(
          new Violation(
            relativePath,
            runStep.startIndex + 1,
            "ensure-editor.ps1 -CiManagedOnly -ProvisioningProfile",
            `Job '${job.id}' runs run-ci-tests.ps1 before the CI-managed editor is provisioned. Move ensure-editor.ps1 -CiManagedOnly -ProvisioningProfile <profile> before every direct run-ci-tests.ps1 step.`,
            "error"
          )
        );
      }
    }

    if (acquireStep) {
      for (const runStep of nonGenerateOnlyRunSteps) {
        if (acquireStep.startIndex > runStep.startIndex) {
          violations.push(
            new Violation(
              relativePath,
              runStep.startIndex + 1,
              "acquire-build-lock",
              `Job '${job.id}' runs Unity tests before acquiring the organization lock. Move acquire-build-lock before every non-GenerateOnly run-ci-tests.ps1 step.`,
              "error"
            )
          );
        }
      }
    }

    if (acquireStep && provisionStep.startIndex > acquireStep.startIndex) {
      violations.push(
        new Violation(
          relativePath,
          provisionStep.startIndex + 1,
          "ensure-editor.ps1 -CiManagedOnly -ProvisioningProfile",
          `Job '${job.id}' provisions the Unity editor after acquiring the organization lock. Move ensure-editor.ps1 -CiManagedOnly -ProvisioningProfile <profile> before acquire-build-lock so install/repair never holds the licensed Unity seat.`,
          "error"
        )
      );
    }

    if (directUnityRunUsesDynamicTestMode(runSteps)) {
      if (
        dynamicUnityTestModeRequiresEditorOnlyProfile(lines, job, runSteps) &&
        !stepProvidesUnityProvisioningProfile(provisionStep, "EditorOnly")
      ) {
        violations.push(
          new Violation(
            relativePath,
            provisionStep.startIndex + 1,
            "-ProvisioningProfile",
            `Job '${job.id}' provisions Unity for a dynamic editmode/playmode test-mode matrix/input but does not provide EditorOnly.`,
            "error"
          )
        );
      }
      if (
        dynamicUnityTestModeRequiresStandaloneProfile(lines, job, runSteps) &&
        !stepProvidesUnityProvisioningProfile(provisionStep, "StandaloneWindowsIl2Cpp")
      ) {
        violations.push(
          new Violation(
            relativePath,
            provisionStep.startIndex + 1,
            "-ProvisioningProfile",
            `Job '${job.id}' provisions Unity for a dynamic standalone-capable test-mode matrix/input but does not provide StandaloneWindowsIl2Cpp.`,
            "error"
          )
        );
      }
    } else {
      if (
        directUnityRunRequiresStandaloneProfile(runSteps) &&
        !stepProvidesUnityProvisioningProfile(provisionStep, "StandaloneWindowsIl2Cpp")
      ) {
        violations.push(
          new Violation(
            relativePath,
            provisionStep.startIndex + 1,
            "StandaloneWindowsIl2Cpp",
            `Job '${job.id}' runs standalone Unity tests but its provisioning step does not provide StandaloneWindowsIl2Cpp.`,
            "error"
          )
        );
      }
      if (
        directUnityRunRequiresEditorOnlyProfile(runSteps) &&
        !stepProvidesUnityProvisioningProfile(provisionStep, "EditorOnly")
      ) {
        violations.push(
          new Violation(
            relativePath,
            provisionStep.startIndex + 1,
            "EditorOnly",
            `Job '${job.id}' runs editmode/playmode Unity tests but its provisioning step does not provide EditorOnly.`,
            "error"
          )
        );
      }
    }
  }

  return violations;
}

/**
 * For any job that runs Unity natively (run-ci-tests.ps1 step) OR uses
 * game-ci/unity-test-runner@v4, require a LATER step with `if: always()` that
 * `uses: ./.github/actions/return-unity-license`. A killed/crashed editor cannot
 * return its own activated seat, so this if:always() step is the workflow-level
 * backstop; without it (or mis-ordered before the run) a failed Unity job leaks
 * a seat from the serial's small (~2) shared activation pool until it is
 * reclaimed.
 *
 * Scoped to ACTIVE workflow files (skips .github/workflows-disabled).
 */
function findUnityLicenseReturnViolations(relativePath, lines) {
  const violations = [];
  if (isDisabledWorkflowPath(relativePath)) {
    return violations;
  }

  for (const job of extractJobs(lines)) {
    const steps = extractJobSteps(lines, job);

    // The LAST native Unity run step in the job (the return must come after it).
    let lastRunStep = null;
    for (const step of steps) {
      if (stepRunsUnityNatively(step)) {
        lastRunStep = step;
      }
    }
    if (!lastRunStep) {
      continue;
    }

    const returnStep = steps.find((step) => step.uses === RETURN_UNITY_LICENSE_USES);

    if (!returnStep) {
      violations.push(
        new Violation(
          relativePath,
          lastRunStep.startIndex + 1,
          RETURN_UNITY_LICENSE_USES,
          `Job '${job.id}' runs Unity natively but has no license return step. Add an if: always() step that uses ${RETURN_UNITY_LICENSE_USES} after the Unity run so a killed editor cannot leak the shared Unity seat.`,
          "error"
        )
      );
      continue;
    }

    if (returnStep.startIndex < lastRunStep.startIndex) {
      violations.push(
        new Violation(
          relativePath,
          returnStep.startIndex + 1,
          RETURN_UNITY_LICENSE_USES,
          `Job '${job.id}' places the ${RETURN_UNITY_LICENSE_USES} step BEFORE the Unity run. Move it AFTER the run so it can return the seat the run activated.`,
          "error"
        )
      );
    }

    if (returnStep.if !== "always()") {
      violations.push(
        new Violation(
          relativePath,
          returnStep.startIndex + 1,
          "if:",
          `Job '${job.id}' license return step must declare if: always() so a failed/killed Unity run still returns the shared seat.`,
          "error"
        )
      );
    }

    // The return action runs `Unity.exe -returnlicense -username $env:UNITY_EMAIL
    // -password $env:UNITY_PASSWORD`. With either credential absent from the
    // step's env the action does NOT fail loudly: it warns and exits 0, so the
    // shared Unity seat is SILENTLY never returned even though the step "passed".
    // Presence/order/if-always above are necessary but not sufficient; the seat
    // only comes back when BOTH creds are wired into the step env. Mirror the
    // BUILD_LOCK_TOKEN env check (acquire/release) and read the parsed
    // returnStep.env Map (populated by extractStepEnvMap). A `contains` check on
    // the secrets reference is intentional: extractStepEnvMap preserves the raw
    // `${{ ... }}` value, and we want to tolerate inner spacing variations while
    // still binding the env to the correct secret.
    for (const cred of ["UNITY_EMAIL", "UNITY_PASSWORD"]) {
      const value = returnStep.env.get(cred);
      if (typeof value !== "string" || !value.includes(`secrets.${cred}`)) {
        violations.push(
          new Violation(
            relativePath,
            returnStep.startIndex + 1,
            `${cred}:`,
            `Job '${job.id}' license return step must pass ${cred}: \${{ secrets.${cred} }} as env. Without both UNITY_EMAIL and UNITY_PASSWORD the return action silently no-ops (warns + exit 0) and the shared Unity seat is never returned.`,
            "error"
          )
        );
      }
    }
  }

  return violations;
}

// Retired Unity activation secret. The repo moved to classic serial activation
// (UNITY_SERIAL/UNITY_EMAIL/UNITY_PASSWORD); referencing the floating-license
// server secret in an ACTIVE workflow silently points CI back at the retired
// licensing-server path that no longer exists.
const FORBIDDEN_UNITY_LICENSE_SECRET_RE = /secrets\.UNITY_LICENSING_SERVER\b/;

/**
 * Errors on any line referencing the retired Unity floating-license server
 * secret (secrets.UNITY_LICENSING_SERVER) in an ACTIVE workflow file (skips
 * .github/workflows-disabled). Scoped to the `secrets.` prefix so it does not
 * false-fire on remediation prose that names the bare env var.
 */
function findForbiddenUnityLicenseSecretViolations(relativePath, lines) {
  const violations = [];
  if (isDisabledWorkflowPath(relativePath)) {
    return violations;
  }

  for (let index = 0; index < lines.length; index++) {
    if (!FORBIDDEN_UNITY_LICENSE_SECRET_RE.test(lines[index])) {
      continue;
    }
    violations.push(
      new Violation(
        relativePath,
        index + 1,
        lines[index].trim(),
        `Retired secret 'secrets.UNITY_LICENSING_SERVER' is referenced. This repo moved to classic serial activation (UNITY_SERIAL/UNITY_EMAIL/UNITY_PASSWORD); remove it.`,
        "error"
      )
    );
  }

  return violations;
}

// The three secrets that classic serial activation requires. A workflow with a
// Unity-native job MUST plumb all three somewhere or activation fails at runtime
// (validate-unity-license errors, then run-ci-tests.ps1 cannot activate -serial).
const REQUIRED_UNITY_LICENSE_SECRETS = [
  "secrets.UNITY_SERIAL",
  "secrets.UNITY_EMAIL",
  "secrets.UNITY_PASSWORD"
];

/**
 * For any ACTIVE workflow file containing a Unity-native job (same detection as
 * findUnityLicenseReturnViolations: run-ci-tests.ps1 OR
 * game-ci/unity-test-runner@v4), require the file to reference ALL THREE serial
 * activation secrets somewhere (secrets.UNITY_SERIAL / UNITY_EMAIL /
 * UNITY_PASSWORD). Missing any one means classic activation cannot complete on
 * the runner. Emits one error per missing secret, pointing at the first
 * Unity-run step line. Returns [] for files with no Unity-native job.
 *
 * Scoped to ACTIVE workflow files (skips .github/workflows-disabled).
 */
function findRequiredUnityLicenseSecretViolations(relativePath, lines) {
  const violations = [];
  if (isDisabledWorkflowPath(relativePath)) {
    return violations;
  }

  // Find the FIRST Unity-native run step across all jobs; its absence means the
  // file has no Unity-licensed work and the serial secrets are not required.
  let firstRunStep = null;
  for (const job of extractJobs(lines)) {
    for (const step of extractJobSteps(lines, job)) {
      if (stepRunsUnityNatively(step)) {
        if (!firstRunStep || step.startIndex < firstRunStep.startIndex) {
          firstRunStep = step;
        }
      }
    }
  }
  if (!firstRunStep) {
    return violations;
  }

  const fileText = lines.join("\n");
  for (const secret of REQUIRED_UNITY_LICENSE_SECRETS) {
    if (!fileText.includes(secret)) {
      violations.push(
        new Violation(
          relativePath,
          firstRunStep.startIndex + 1,
          secret,
          `Workflow runs Unity natively but never references '${secret}'. Classic serial activation needs all of secrets.UNITY_SERIAL/UNITY_EMAIL/UNITY_PASSWORD; add '${secret}' to the validate/run/return steps.`,
          "error"
        )
      );
    }
  }

  return violations;
}

/**
 * Checks every `runs-on:` value that references `self-hosted` against a
 * documented allowlist of valid label sets. Catches typos such as
 * `RAM-64Gb` that would silently produce a job no runner can pick up.
 *
 * Dynamic forms (`${{ fromJSON(needs.<jobId>.outputs.<output>) }}`) are
 * resolved by walking back to the emitting job and lexically scanning the
 * step that produces that output for `labels='...'` assignments. Every
 * emitted label set must be in the allowlist.
 */
function findSelfHostedLabelAllowlistViolations(relativePath, lines) {
  const violations = [];
  const jobs = extractJobs(lines);
  const outputsMap = extractJobOutputsSourceMap(lines);

  for (const job of jobs) {
    const runsOn = extractJobRunsOn(lines, job);
    if (!runsOn) {
      continue;
    }

    const raw = runsOn.raw;

    // Inline array form: [self-hosted, Windows, RAM-64GB]
    if (raw.startsWith("[")) {
      let labels;
      try {
        labels = parseInlineLabelArray(raw);
      } catch (parseError) {
        violations.push(
          new Violation(
            relativePath,
            runsOn.line,
            `runs-on: ${raw}`,
            `Job '${job.id}': malformed inline runs-on label array: ${parseError.message}`,
            "error"
          )
        );
        continue;
      }
      if (!labels) {
        continue;
      }
      if (!labels.some((label) => label === "self-hosted")) {
        continue;
      }
      if (!ALLOWLIST_KEYS.has(sortedLabelKey(labels))) {
        violations.push(
          new Violation(
            relativePath,
            runsOn.line,
            `runs-on: ${raw}`,
            `Job '${job.id}': self-hosted runs-on label set [${labels.join(", ")}] is not in the documented allowlist (${formatAllowlistForMessage()}). If a new runner topology is needed, update SELF_HOSTED_LABEL_ALLOWLIST in scripts/validate-workflows.js. To route dynamically by event, use the matrix-config 'runner-labels' output pattern.`,
            "error"
          )
        );
      }
      continue;
    }

    // Multi-line block list form.
    if (raw === "" && runsOn.blockList && runsOn.blockList.length > 0) {
      const labels = runsOn.blockList;
      if (!labels.some((label) => label === "self-hosted")) {
        continue;
      }
      if (!ALLOWLIST_KEYS.has(sortedLabelKey(labels))) {
        violations.push(
          new Violation(
            relativePath,
            runsOn.line,
            `runs-on: [${labels.join(", ")}]`,
            `Job '${job.id}': self-hosted runs-on label set [${labels.join(", ")}] is not in the documented allowlist (${formatAllowlistForMessage()}). If a new runner topology is needed, update SELF_HOSTED_LABEL_ALLOWLIST in scripts/validate-workflows.js. To route dynamically by event, use the matrix-config 'runner-labels' output pattern.`,
            "error"
          )
        );
      }
      continue;
    }

    // Dynamic form: ${{ fromJSON(needs.<jobId>.outputs.<output>) }}
    const dynamicMatch =
      /^\$\{\{\s*fromJSON\(\s*needs\.([A-Za-z0-9_-]+)\.outputs\.([A-Za-z0-9_-]+)\s*\)\s*\}\}$/.exec(
        raw
      );
    if (dynamicMatch) {
      const sourceJobId = dynamicMatch[1];
      const sourceOutputKey = dynamicMatch[2];
      const sourceJob = outputsMap[sourceJobId];

      if (!sourceJob || !sourceJob.outputs[sourceOutputKey]) {
        violations.push(
          new Violation(
            relativePath,
            runsOn.line,
            `runs-on: ${raw}`,
            `Job '${job.id}': dynamic runs-on references needs.${sourceJobId}.outputs.${sourceOutputKey} but that source job/output cannot be resolved. The validator must lexically prove the emitted label set is allowlisted (${formatAllowlistForMessage()}). Ensure the emitting job declares the output and its step uses an 'id:' that matches the outputs binding.`,
            "error"
          )
        );
        continue;
      }

      const { stepId } = sourceJob.outputs[sourceOutputKey];
      const sourceStep = sourceJob.stepsById[stepId];

      if (!sourceStep || !sourceStep.run || typeof sourceStep.run.text !== "string") {
        violations.push(
          new Violation(
            relativePath,
            runsOn.line,
            `runs-on: ${raw}`,
            `Job '${job.id}': dynamic runs-on points at step id '${stepId}' in job '${sourceJobId}' but its run: block could not be located. Use a literal bash run block with 'labels=[...]' assignments so the validator can prove each branch produces an allowlisted label set (${formatAllowlistForMessage()}).`,
            "error"
          )
        );
        continue;
      }

      const emittedSets = extractEmittedLabelSetsFromBash(sourceStep.run.text);
      if (emittedSets.length === 0) {
        violations.push(
          new Violation(
            relativePath,
            runsOn.line,
            `runs-on: ${raw}`,
            `Job '${job.id}': dynamic runs-on points at step id '${stepId}' in job '${sourceJobId}' but the run block emits no 'labels=[...]' assignments. Use literal 'echo \\'labels=[...]\\' >> "$GITHUB_OUTPUT"' lines so the validator can prove each branch produces an allowlisted label set (${formatAllowlistForMessage()}).`,
            "error"
          )
        );
        continue;
      }

      for (const labels of emittedSets) {
        if (labels === null) {
          violations.push(
            new Violation(
              relativePath,
              runsOn.line,
              `runs-on: ${raw}`,
              `Job '${job.id}': dynamic runs-on source step '${stepId}' (job '${sourceJobId}') has a malformed 'labels=...' JSON value. Emit a literal JSON array of labels.`,
              "error"
            )
          );
          continue;
        }
        if (!labels.some((label) => label === "self-hosted")) {
          continue;
        }
        if (!ALLOWLIST_KEYS.has(sortedLabelKey(labels))) {
          violations.push(
            new Violation(
              relativePath,
              runsOn.line,
              `runs-on: ${raw}`,
              `Job '${job.id}': dynamic runs-on source step '${stepId}' (job '${sourceJobId}') emits label set [${labels.join(", ")}] which is not in the documented allowlist (${formatAllowlistForMessage()}). Update the emitting bash branches or extend SELF_HOSTED_LABEL_ALLOWLIST in scripts/validate-workflows.js.`,
              "error"
            )
          );
        }
      }
      continue;
    }

    // Plain scalar form: `runs-on: ubuntu-latest`, `runs-on: 'self-hosted'`,
    // or `runs-on: "self-hosted"`. Strip outer quotes; if the remaining
    // identifier equals 'self-hosted' alone, it lacks the required
    // platform/RAM modifiers and is not in the allowlist.
    const scalarStripped = raw.replace(/^(["'])(.*)\1$/, "$2").trim();
    if (scalarStripped === "self-hosted") {
      violations.push(
        new Violation(
          relativePath,
          runsOn.line,
          `runs-on: ${raw}`,
          `Job '${job.id}': self-hosted runs-on label set [self-hosted] is not in the documented allowlist (${formatAllowlistForMessage()}). Bare 'self-hosted' lacks the required platform/RAM modifiers; specify the full label set or use the matrix-config 'runner-labels' output pattern.`,
          "error"
        )
      );
    }
  }

  return violations;
}

/**
 * Checks that every job whose `runs-on:` resolves a dependent job's
 * output via `${{ fromJSON(needs.<jobId>.outputs.<output>) }}` actually
 * declares `needs:` containing that `<jobId>`. Runtime would fail loudly
 * (the expression returns null and the runner picker errors out), but
 * static detection surfaces the typo at validation time.
 */
function findDynamicRunsOnMissingNeedsViolations(relativePath, lines) {
  const violations = [];
  const jobs = extractJobs(lines);

  for (const job of jobs) {
    const runsOn = extractJobRunsOn(lines, job);
    if (!runsOn || typeof runsOn.raw !== "string") {
      continue;
    }

    const dynamicMatch =
      /^\$\{\{\s*fromJSON\(\s*needs\.([A-Za-z0-9_-]+)\.outputs\.([A-Za-z0-9_-]+)\s*\)\s*\}\}$/.exec(
        runsOn.raw
      );
    if (!dynamicMatch) {
      continue;
    }

    const dependencyJobId = dynamicMatch[1];
    const declaredNeeds = extractJobNeeds(lines, job);
    if (declaredNeeds.includes(dependencyJobId)) {
      continue;
    }

    violations.push(
      new Violation(
        relativePath,
        runsOn.line,
        `runs-on: ${runsOn.raw}`,
        `Job '${job.id}': dynamic runs-on references needs.${dependencyJobId}.outputs.${dynamicMatch[2]} but '${dependencyJobId}' is not in the job's needs: list (declared: [${declaredNeeds.join(", ")}]). Add '${dependencyJobId}' to needs: so the expression resolves at runtime.`,
        "error"
      )
    );
  }

  return violations;
}

function findChangelogCoverageCheckoutViolations(relativePath, lines) {
  const violations = [];
  const jobs = extractJobs(lines);

  for (const job of jobs) {
    const steps = extractJobSteps(lines, job);
    let latestCheckoutHasFullHistory = false;

    for (const step of steps) {
      if (typeof step.uses === "string" && step.uses.startsWith("actions/checkout@")) {
        latestCheckoutHasFullHistory = stepHasFullHistoryCheckout(lines, step);
        continue;
      }

      if (!step.run || !runTextInvokesChangelogCoverage(step.run.text)) {
        continue;
      }

      if (latestCheckoutHasFullHistory) {
        continue;
      }

      violations.push(
        new Violation(
          relativePath,
          step.run.line,
          step.run.text,
          `Workflow job '${job.id}' runs a git-history-aware step without a preceding full-history checkout. Set actions/checkout fetch-depth: 0 so origin/<base> refs are available for changelog coverage / git diff origin commands. Allowlist of git-history-requiring npm scripts: ${NPM_SCRIPTS_REQUIRING_GIT_HISTORY.join(", ")}.`,
          "error"
        )
      );
    }
  }

  return violations;
}

function stringifyWorkflowScalar(value) {
  if (value === null || value === undefined) {
    return "";
  }
  return String(value).trim();
}

function workflowScalarIsTrue(value) {
  return value === true || stringifyWorkflowScalar(value).toLowerCase() === "true";
}

function workflowScalarIsFalse(value) {
  return value === false || stringifyWorkflowScalar(value).toLowerCase() === "false";
}

function workflowArgsContainToken(args, token) {
  return new RegExp(`(?:^|\\s)${escapeRegExp(token)}(?:\\s|$)`).test(stringifyWorkflowScalar(args));
}

function workflowArgsContainOptionValue(args, option, expectedValues) {
  const escapedValues = expectedValues.map((value) => escapeRegExp(value)).join("|");
  return new RegExp(
    `(?:^|\\s)${escapeRegExp(option)}(?:=|\\s+)(?:["']?(?:${escapedValues})["']?)(?:\\s|$)`
  ).test(stringifyWorkflowScalar(args));
}

function workflowArgsContainScheme(args, scheme) {
  return workflowArgsContainOptionValue(args, "--scheme", [scheme]);
}

function workflowArgsAcceptTimeouts(args) {
  return (
    workflowArgsContainToken(args, "--accept-timeouts") ||
    workflowArgsContainToken(args, "--accept-timeouts=true")
  );
}

function workflowArgsContainExternalScheme(args) {
  return workflowArgsContainScheme(args, "http") || workflowArgsContainScheme(args, "https");
}

function isLycheeActionUses(usesValue) {
  return stringifyWorkflowScalar(usesValue).toLowerCase().startsWith("lycheeverse/lychee-action@");
}

function isInstallPinnedLycheeUses(usesValue) {
  return stringifyWorkflowScalar(usesValue)
    .replace(/^\.\//, "")
    .toLowerCase()
    .replace(/\/$/, "") === ".github/actions/install-pinned-lychee";
}

function stepRunsLycheeCli(step) {
  const runText = stepRunText(step);
  return /(?:^|\n)\s*lychee(?:[\s\\]|$)/.test(runText);
}

function workflowArgsContainLycheeConfig(args) {
  const text = stringifyWorkflowScalar(args);
  return (
    workflowArgsContainOptionValue(text, "-c", [".lychee.toml"]) ||
    workflowArgsContainOptionValue(text, "--config", [".lychee.toml"])
  );
}

function workflowRunCapturesAdvisoryLycheeExitCode(runText) {
  return (
    /\bset\s+\+e\b/.test(runText) &&
    /\bexit_code\b/.test(runText) &&
    /\bGITHUB_OUTPUT\b/.test(runText) &&
    /\bexit\s+0\b/.test(runText)
  );
}

function workflowRunWritesAdvisoryLycheeReport(runText) {
  const text = stringifyWorkflowScalar(runText);
  return (
    /\bmkdir\s+-p\s+["']?\.\/lychee["']?(?:\s|$)/.test(text) &&
    workflowArgsContainOptionValue(text, "--format", ["markdown"]) &&
    workflowArgsContainOptionValue(text, "--output", [ADVISORY_LYCHEE_REPORT_PATH])
  );
}

function workflowRunUsesAdvisoryIssueBodyFile(runText) {
  return workflowArgsContainOptionValue(runText, "--body-file", [ADVISORY_LYCHEE_REPORT_PATH]);
}

function workflowStepWithMap(step) {
  if (!step || typeof step !== "object" || !step.with || typeof step.with !== "object") {
    return {};
  }
  return step.with;
}

function stepUsesBlockingLycheeJsonClassifier(step) {
  const runText = stepRunText(step);
  if (runText.length === 0) {
    return false;
  }

  const referencesOldReport =
    /external\.json/.test(runText) ||
    /steps\.external\.outputs\.exit_code/.test(runText) ||
    /status\.text\s*!=\s*["']Timeout["']/.test(runText);

  return referencesOldReport && /\bjq\b/.test(runText);
}

/**
 * Enforces this repo's lychee workflow contract with structural YAML parsing.
 *
 * The repo intentionally avoids lycheeverse/lychee-action because the v0.24.2
 * Linux asset extracts the binary under a platform directory, while the action
 * installer expected a flat path. Workflows install through the repo-owned
 * composite action, then call the lychee CLI directly so archive-layout drift is
 * diagnosed by our installer instead of failing before the link scan starts.
 */
function findLycheeActionPolicyViolations(relativePath, content) {
  const violations = [];
  const normalizedPath = String(relativePath || "").replace(/\\/g, "/");

  if (isDisabledWorkflowPath(normalizedPath)) {
    return violations;
  }

  const yamlResult = loadYamlModule();
  if (yamlResult.error) {
    violations.push(
      new Violation(
        relativePath,
        0,
        "lychee-action policy",
        `Unable to load the 'yaml' package needed to verify lychee-action ` +
          `workflow policy (${yamlResult.error.message}). Run 'npm ci' (or ` +
          `'npm install yaml') so the validator can parse workflows structurally.`,
        "error"
      )
    );
    return violations;
  }

  const yaml = yamlResult.module;
  let document;
  try {
    document = yaml.parseDocument(content, { prettyErrors: true });
  } catch (error) {
    const message = error && error.message ? error.message : String(error);
    violations.push(
      new Violation(
        relativePath,
        0,
        "lychee-action policy",
        `Unable to parse workflow YAML to verify lychee-action policy: ${message}`,
        "error"
      )
    );
    return violations;
  }

  if (document && Array.isArray(document.errors) && document.errors.length > 0) {
    const first = document.errors[0];
    const message = first && first.message ? first.message : String(first);
    violations.push(
      new Violation(
        relativePath,
        0,
        "lychee-action policy",
        `Unable to parse workflow YAML to verify lychee-action policy: ${message}`,
        "error"
      )
    );
    return violations;
  }

  let parsed;
  try {
    parsed = document.toJS({ maxAliasCount: -1 });
  } catch (error) {
    const message = error && error.message ? error.message : String(error);
    violations.push(
      new Violation(
        relativePath,
        0,
        "lychee-action policy",
        `Unable to materialize workflow YAML to verify lychee-action policy: ${message}`,
        "error"
      )
    );
    return violations;
  }

  const isBlockingDocsWorkflow = normalizedPath === BLOCKING_DOC_LINK_WORKFLOW;
  const isAdvisoryDocsWorkflow = normalizedPath === ADVISORY_DOC_LINK_WORKFLOW;
  const jobs = parsed && typeof parsed === "object" ? parsed.jobs : null;
  if (!jobs || typeof jobs !== "object") {
    if (isBlockingDocsWorkflow) {
      violations.push(
        new Violation(
          relativePath,
          0,
          "lychee CLI offline/internal step",
          "Blocking docs link workflow must include at least one active lychee CLI offline/internal step for local links and anchors.",
          "error"
        ),
        new Violation(
          relativePath,
          0,
          "lychee CLI external liveness step",
          "Blocking docs link workflow must include at least one active lychee CLI external liveness step.",
          "error"
        )
      );
    }
    if (isAdvisoryDocsWorkflow) {
      violations.push(
        new Violation(
          relativePath,
          0,
          "lychee CLI advisory scan step",
          "Scheduled advisory link workflow must include at least one active lychee CLI advisory scan step.",
          "error"
        )
      );
    }
    return violations;
  }

  const jobsNode = document.get("jobs", true);
  const workflowPolicyLine = resolveNodeStartLine(content, jobsNode);
  let hasBlockingOfflineLycheeStep = false;
  let hasBlockingExternalLycheeStep = false;
  let hasAdvisoryLycheeStep = false;
  let hasAdvisoryIssueReportBodyFile = false;

  for (const [jobId, job] of Object.entries(jobs)) {
    if (!job || typeof job !== "object" || !Array.isArray(job.steps)) {
      continue;
    }

    const stepsNode =
      jobsNode && typeof jobsNode.getIn === "function"
        ? jobsNode.getIn([jobId, "steps"], true)
        : null;
    const stepNodes = stepsNode && Array.isArray(stepsNode.items) ? stepsNode.items : [];
    let sawPinnedLycheeInstall = false;

    job.steps.forEach((step, index) => {
      if (!step || typeof step !== "object") {
        return;
      }

      const stepLine = resolveNodeStartLine(content, stepNodes[index]);
      const runText = stepRunText(step);

      if (isAdvisoryDocsWorkflow && workflowRunUsesAdvisoryIssueBodyFile(runText)) {
        hasAdvisoryIssueReportBodyFile = true;
      }

      if (isBlockingDocsWorkflow && stepUsesBlockingLycheeJsonClassifier(step)) {
        violations.push(
          new Violation(
            relativePath,
            stepLine,
            "jq external.json classifier",
            "Blocking docs link workflow must not use the retired JSON " +
              "post-classifier. Use lychee v0.24.2 native timeout acceptance " +
              "with fail: true and --accept-timeouts=true on the external " +
              "lychee step.",
            "error"
          )
        );
      }

      if (isLycheeActionUses(step.uses)) {
        violations.push(
          new Violation(
            relativePath,
            stepLine,
            "lycheeverse/lychee-action",
            "Active workflows must not use lycheeverse/lychee-action; its v0.24.2 Linux installer expected a flat archive path and failed before lychee ran. Use ./.github/actions/install-pinned-lychee and invoke the lychee CLI directly.",
            "error"
          )
        );
        return;
      }

      if (isInstallPinnedLycheeUses(step.uses)) {
        const withMap = workflowStepWithMap(step);
        const version = stringifyWorkflowScalar(withMap.version);
        if (version !== REQUIRED_LYCHEE_VERSION) {
          violations.push(
            new Violation(
              relativePath,
              stepLine,
              `version: ${version || "<missing>"}`,
              `Job '${jobId}' must install lychee ${REQUIRED_LYCHEE_VERSION} through ./.github/actions/install-pinned-lychee before running lychee.`,
              "error"
            )
          );
        } else {
          sawPinnedLycheeInstall = true;
        }
        return;
      }

      if (!stepRunsLycheeCli(step)) {
        return;
      }

      const args = runText;
      if (!sawPinnedLycheeInstall) {
        violations.push(
          new Violation(
            relativePath,
            stepLine,
            "missing install-pinned-lychee",
            `Job '${jobId}' runs lychee before installing ${REQUIRED_LYCHEE_VERSION} via ./.github/actions/install-pinned-lychee.`,
            "error"
          )
        );
      }

      if (!workflowArgsContainLycheeConfig(args)) {
        violations.push(
          new Violation(
            relativePath,
            stepLine,
            "-c .lychee.toml",
            "Lychee CLI steps must pass -c .lychee.toml so both blocking and advisory jobs share the validated repository policy.",
            "error"
          )
        );
      }

      if (!workflowArgsContainToken(args, "--files-from")) {
        violations.push(
          new Violation(
            relativePath,
            stepLine,
            "--files-from",
            "Lychee CLI steps must use --files-from with the tracked docs list; lychee globs do not honor .gitignore.",
            "error"
          )
        );
      }

      if (isAdvisoryDocsWorkflow) {
        hasAdvisoryLycheeStep = true;
      }

      if (isAdvisoryDocsWorkflow && workflowArgsAcceptTimeouts(args)) {
        violations.push(
          new Violation(
            relativePath,
            stepLine,
            "--accept-timeouts",
            "Scheduled advisory link workflow must not pass --accept-timeouts or --accept-timeouts=true; timeout reporting must stay enabled.",
            "error"
          )
        );
      }

      if (isAdvisoryDocsWorkflow && !workflowRunCapturesAdvisoryLycheeExitCode(args)) {
        violations.push(
          new Violation(
            relativePath,
            stepLine,
            "advisory exit_code output",
            "Scheduled advisory link workflow must capture lychee's exit_code to GITHUB_OUTPUT and exit 0 so dead links and timeouts are reported through the tracking issue instead of failing the run.",
            "error"
          )
        );
      }

      if (isAdvisoryDocsWorkflow && !workflowRunWritesAdvisoryLycheeReport(args)) {
        violations.push(
          new Violation(
            relativePath,
            stepLine,
            "advisory markdown report",
            `Scheduled advisory link workflow must write a Markdown lychee report to ${ADVISORY_LYCHEE_REPORT_PATH}; the issue sync step uses that file as the tracking issue body.`,
            "error"
          )
        );
      }

      if (!isBlockingDocsWorkflow) {
        return;
      }

      const hasOfflineMode = workflowArgsContainToken(args, "--offline");
      const hasHttpScheme = workflowArgsContainScheme(args, "http");
      const hasHttpsScheme = workflowArgsContainScheme(args, "https");
      const hasExternalScheme = hasHttpScheme || hasHttpsScheme;
      if (hasOfflineMode) {
        hasBlockingOfflineLycheeStep = true;
      }
      if (!hasOfflineMode || hasExternalScheme) {
        hasBlockingExternalLycheeStep = true;
      }

      if (hasOfflineMode && hasExternalScheme) {
        violations.push(
          new Violation(
            relativePath,
            stepLine,
            "--offline --scheme http/https",
            "Blocking docs lychee step must not combine --offline with external --scheme http/https checks; external liveness must remain a separate fail:true step with --accept-timeouts=true.",
            "error"
          )
        );
      }

      if (hasOfflineMode && !hasExternalScheme) {
        if (!workflowArgsContainToken(args, "--include-fragments")) {
          violations.push(
            new Violation(
              relativePath,
              stepLine,
              "--include-fragments",
              "Blocking docs offline/internal lychee step must pass --include-fragments so in-repo heading anchors are validated.",
              "error"
            )
          );
        }
        return;
      }

      if (!hasHttpsScheme || !hasHttpScheme) {
        const missingSchemes = [
          ...(hasHttpsScheme ? [] : ["--scheme https"]),
          ...(hasHttpScheme ? [] : ["--scheme http"])
        ].join(" and ");
        violations.push(
          new Violation(
            relativePath,
            stepLine,
            missingSchemes,
            "Blocking docs external lychee step must pass both --scheme https and --scheme http so both secure and legacy external links are checked.",
            "error"
          )
        );
      }

      if (!workflowArgsContainToken(args, "--accept-timeouts=true")) {
        violations.push(
          new Violation(
            relativePath,
            stepLine,
            "--accept-timeouts=true",
            "Blocking docs external lychee step must pass --accept-timeouts=true so slow hosts are advisory without JSON post-classification.",
            "error"
          )
        );
      }

      if (/\bexternal\.json\b/.test(args)) {
        violations.push(
          new Violation(
            relativePath,
            stepLine,
            "external.json report",
            "Blocking docs link workflow must not use fail:false plus format/output JSON report post-classification. Use fail: true with --accept-timeouts=true.",
            "error"
          )
        );
      }
    });
  }

  if (isBlockingDocsWorkflow && !hasBlockingOfflineLycheeStep) {
    violations.push(
      new Violation(
        relativePath,
        workflowPolicyLine,
        "lychee CLI offline/internal step",
        "Blocking docs link workflow must include at least one active lychee CLI offline/internal step for local links and anchors.",
        "error"
      )
    );
  }

  if (isBlockingDocsWorkflow && !hasBlockingExternalLycheeStep) {
    violations.push(
      new Violation(
        relativePath,
        workflowPolicyLine,
        "lychee CLI external liveness step",
        "Blocking docs link workflow must include at least one active lychee CLI external liveness step.",
        "error"
      )
    );
  }

  if (isAdvisoryDocsWorkflow && !hasAdvisoryLycheeStep) {
    violations.push(
      new Violation(
        relativePath,
        workflowPolicyLine,
        "lychee CLI advisory scan step",
        "Scheduled advisory link workflow must include at least one active lychee CLI advisory scan step.",
        "error"
      )
    );
  }

  if (isAdvisoryDocsWorkflow && hasAdvisoryLycheeStep && !hasAdvisoryIssueReportBodyFile) {
    violations.push(
      new Violation(
        relativePath,
        workflowPolicyLine,
        "advisory issue body file",
        `Scheduled advisory link workflow must pass ${ADVISORY_LYCHEE_REPORT_PATH} to gh issue --body-file so dead-link findings are preserved in the tracking issue.`,
        "error"
      )
    );
  }

  return violations;
}

/**
 * Returns the deduplicated list of labels referenced by a job's `runs-on:`
 * value, including inline-array, block-list, and scalar forms. Returns
 * `null` when the form is dynamic (`${{ fromJSON(...) }}` etc.) and the
 * caller cannot statically resolve the labels.
 */
function extractStaticJobLabels(lines, job) {
  const runsOn = extractJobRunsOn(lines, job);
  if (!runsOn) {
    return null;
  }
  const raw = runsOn.raw;

  if (raw.startsWith("[")) {
    try {
      return parseInlineLabelArray(raw);
    } catch (_error) {
      return null;
    }
  }

  if (raw === "" && Array.isArray(runsOn.blockList) && runsOn.blockList.length > 0) {
    return runsOn.blockList.slice();
  }

  if (raw.length === 0) {
    return null;
  }

  if (raw.startsWith("${{")) {
    return null;
  }

  return [raw.replace(/^(["'])(.*)\1$/, "$2").trim()];
}

/**
 * Detects whether a job's steps include a self-hosted runner access
 * preflight probe. Identified by the STRUCTURAL signature only -- the
 * job name is not consulted (callers may name the job `runner-preflight`,
 * `runner-access-preflight`, `preflight`, or anything else):
 *
 *   1. The job runs on a hosted runner (ubuntu-latest, ubuntu-22.04, etc.)
 *      and is NOT itself self-hosted -- a self-hosted job cannot gate
 *      self-hosted access.
 *   2. At least one of the job's steps' `run:` text references
 *      `actions/runners` (the GitHub REST endpoint, in either org or repo
 *      scope) which is the canonical marker for a runner-inventory probe.
 *
 * The probe lives in `.github/workflows/unity-tests.yml`,
 * `.github/workflows/unity-benchmarks.yml`,
 * and the `runner-preflight` job in `.github/workflows/release.yml`.
 */
function jobIsRunnerAccessPreflight(lines, job) {
  // (1) Must not itself be self-hosted (cannot gate self-hosted access).
  const labels = extractStaticJobLabels(lines, job);
  if (Array.isArray(labels) && labels.some((label) => label === "self-hosted")) {
    return false;
  }
  // The runs-on value must be statically resolvable AND look like a
  // hosted runner (ubuntu-*, windows-*, macos-*) for us to accept the
  // job as a preflight. A dynamic runs-on (matrix expression) cannot be
  // proved to run on a hosted runner.
  if (!Array.isArray(labels) || labels.length === 0) {
    return false;
  }
  const hostedRunnerPattern = /^(ubuntu|windows|macos)(?:-[A-Za-z0-9.+_-]+)?$/;
  const hasHostedLabel = labels.some((label) => hostedRunnerPattern.test(label));
  if (!hasHostedLabel) {
    return false;
  }
  // (2) At least one step must reference the runners endpoint marker.
  const steps = extractJobSteps(lines, job);
  for (const step of steps) {
    if (!step.run) {
      continue;
    }
    if (/actions\/runners\b/.test(step.run.text)) {
      return true;
    }
  }
  return false;
}

/**
 * Self-hosted runner contract: any job whose `runs-on:` declares
 * `self-hosted` (plus any other labels) MUST either be the preflight job
 * itself OR declare `needs:` on a preflight job that lives in the same
 * workflow file. This converts the "queued forever after a transfer"
 * symptom (see docs/runbooks/unity-runners-after-transfer.md) into a
 * fast, clearly-explained failure: the cheap `ubuntu-latest` preflight
 * surfaces the missing runner-group ACL before any self-hosted matrix
 * entry attempts to queue.
 *
 * The validator is intentionally conservative:
 *   - Only flags jobs whose labels statically include `self-hosted`.
 *   - Accepts the job itself when it is the preflight job (so the
 *     preflight job is not required to gate itself).
 *   - Accepts `needs:` targets whose own job satisfies the same predicate
 *     (`jobIsRunnerAccessPreflight`). The needs target must live in the
 *     same workflow file - cross-workflow `needs:` is not supported by
 *     GitHub Actions.
 */
function findSelfHostedRunnerPreflightViolations(relativePath, lines) {
  const violations = [];
  const jobs = extractJobs(lines);
  const jobsById = new Map();
  for (const job of jobs) {
    jobsById.set(job.id, job);
  }

  for (const job of jobs) {
    const labels = extractStaticJobLabels(lines, job);
    if (!labels) {
      continue;
    }
    if (!labels.some((label) => label === "self-hosted")) {
      continue;
    }
    if (jobIsRunnerAccessPreflight(lines, job)) {
      continue;
    }

    const needs = extractJobNeeds(lines, job) || [];
    let needsCoveredByPreflight = false;
    for (const needsTarget of needs) {
      const target = jobsById.get(needsTarget);
      if (!target) {
        continue;
      }
      if (jobIsRunnerAccessPreflight(lines, target)) {
        needsCoveredByPreflight = true;
        break;
      }
    }
    if (needsCoveredByPreflight) {
      continue;
    }

    const runsOn = extractJobRunsOn(lines, job);
    violations.push(
      new Violation(
        relativePath,
        runsOn ? runsOn.line : job.startLine,
        runsOn ? `runs-on: ${runsOn.raw || labels.join(", ")}` : `job: ${job.id}`,
        `Job '${job.id}' targets self-hosted labels [${labels.join(", ")}] without depending on a preflight job that probes runner availability via gh api orgs/<owner>/actions/runners (or repos/<repo>/actions/runners as fallback). Without the preflight, a missing runner-group ACL or offline runner causes the job to queue forever with no error. Add a preflight job running on a hosted runner (e.g. ubuntu-latest) whose step text references actions/runners, then reference it via 'needs:'. See docs/runbooks/unity-runners-after-transfer.md for the runbook the preflight points operators to.`,
        "error"
      )
    );
  }

  return violations;
}

/**
 * Self-hosted Windows runner contract: `shell: bash` on these runners is
 * unreliable because GitHub's runner agent resolves `bash` via WhichUtil.Which
 * against the host PATH. On the DAD-MACHINE Windows runner that PATH puts
 * `C:\Windows\System32` ahead of `C:\Program Files\Git\bin`, so `bash` resolves
 * to the WSL stub. If no WSL distro is installed (the default on a CI box),
 * the step fails immediately with
 *   "Windows Subsystem for Linux has no installed distributions."
 *
 * This rule forbids `shell: bash` (and unspecified `run:` that defaults to
 * `bash`) for any step inside a job whose `runs-on:` resolves to a
 * self-hosted Windows label set. Allowed alternatives:
 *   - `shell: pwsh` (preferred for shell-agnostic content)
 *   - `shell: powershell`
 *   - `shell: 'C:\Program Files\Git\bin\bash.EXE --noprofile --norc ... {0}'`
 *     (explicit absolute path -- escape hatch for steps that genuinely need
 *     POSIX bash semantics; the validator accepts any path ending in
 *     bash.exe/bash.EXE that does NOT live under System32).
 *   - Composite actions (`uses: ./.github/actions/...`) which encapsulate
 *     their own shell choice.
 */
function isSelfHostedWindowsLabelSet(labels) {
  if (!Array.isArray(labels) || labels.length === 0) {
    return false;
  }
  const hasSelfHosted = labels.some((label) => label === "self-hosted");
  const hasWindows = labels.some((label) => /^Windows$/i.test(label));
  return hasSelfHosted && hasWindows;
}

function isAllowedSelfHostedWindowsShell(shellValue) {
  if (typeof shellValue !== "string") {
    return false;
  }
  const trimmed = shellValue.trim();
  if (trimmed === "") {
    return false;
  }
  const lowered = trimmed.toLowerCase();
  if (lowered === "pwsh" || lowered === "powershell" || lowered === "cmd") {
    return true;
  }
  // Explicit-path bash escape hatch. Accept any shell line that names a
  // bash.exe whose directory clearly is NOT System32 (the WSL stub) and
  // contains a `{0}` placeholder (GitHub Actions shell-string contract).
  // extractStepShell lowercases the captured value so the regex matches
  // bash.exe in lowercase; we still preserve the case-insensitive
  // system32 reject.
  const explicitBashRe = /(?:^|["'\\/])bash\.exe\b[^{}]*\{0\}/;
  if (explicitBashRe.test(lowered) && !/[\\/]system32[\\/]bash\.exe/i.test(lowered)) {
    return true;
  }
  return false;
}

/**
 * Resolve a job's runs-on labels for the shell-bash policy. Returns
 *   { kind: "static", labelSets: [string[]] }      -- statically known
 *   { kind: "dynamic-resolved", labelSets: [string[], ...] }
 *                                                  -- dynamic but every
 *                                                     emitted branch is
 *                                                     statically known
 *   { kind: "dynamic-unresolved" }                 -- dynamic and cannot
 *                                                     be statically proven
 *   { kind: "none" }                               -- runs-on is missing
 *                                                     or unrecognized
 *
 * This is the same resolution strategy used by the
 * findSelfHostedLabelAllowlistViolations rule -- reuse keeps the two
 * rules in lock-step.
 */
function resolveJobLabelSetsForShellPolicy(lines, job, outputsMap) {
  const runsOn = extractJobRunsOn(lines, job);
  if (!runsOn) {
    return { kind: "none" };
  }
  const raw = runsOn.raw;

  // Static inline / block-list / scalar forms.
  const staticLabels = extractStaticJobLabels(lines, job);
  if (Array.isArray(staticLabels) && staticLabels.length > 0) {
    return { kind: "static", labelSets: [staticLabels] };
  }

  // Dynamic form: ${{ fromJSON(needs.<jobId>.outputs.<output>) }}.
  const dynamicMatch =
    /^\$\{\{\s*fromJSON\(\s*needs\.([A-Za-z0-9_-]+)\.outputs\.([A-Za-z0-9_-]+)\s*\)\s*\}\}$/.exec(
      raw
    );
  if (!dynamicMatch) {
    return { kind: "none" };
  }
  const sourceJobId = dynamicMatch[1];
  const sourceOutputKey = dynamicMatch[2];
  const sourceJob = outputsMap[sourceJobId];
  if (!sourceJob || !sourceJob.outputs[sourceOutputKey]) {
    return { kind: "dynamic-unresolved" };
  }
  const { stepId } = sourceJob.outputs[sourceOutputKey];
  const sourceStep = sourceJob.stepsById[stepId];
  if (!sourceStep || !sourceStep.run || typeof sourceStep.run.text !== "string") {
    return { kind: "dynamic-unresolved" };
  }
  const emittedSets = extractEmittedLabelSetsFromBash(sourceStep.run.text);
  if (emittedSets.length === 0) {
    return { kind: "dynamic-unresolved" };
  }
  // Reject malformed (null) sets so the caller can surface them.
  const labelSets = [];
  for (const labels of emittedSets) {
    if (labels === null) {
      return { kind: "dynamic-unresolved" };
    }
    labelSets.push(labels);
  }
  return { kind: "dynamic-resolved", labelSets };
}

function findForbidPlainShellBashOnSelfHostedWindowsViolations(relativePath, lines) {
  const violations = [];
  const jobs = extractJobs(lines);
  const workflowDefaultsShell = extractWorkflowDefaultsShell(lines);
  const outputsMap = extractJobOutputsSourceMap(lines);

  for (const job of jobs) {
    const resolution = resolveJobLabelSetsForShellPolicy(lines, job, outputsMap);

    if (resolution.kind === "none") {
      continue;
    }

    if (resolution.kind === "dynamic-unresolved") {
      // We can't statically prove the resolved label set, so we can't
      // mechanically apply (or skip) the shell:bash policy. Emit a
      // WARNING so the maintainer is aware the rule cannot statically
      // verify this job and must do an out-of-band review.
      const runsOn = extractJobRunsOn(lines, job);
      violations.push(
        new Violation(
          relativePath,
          runsOn ? runsOn.line : job.startLine,
          runsOn ? `runs-on: ${runsOn.raw}` : `job: ${job.id}`,
          `Job '${job.id}': dynamic runs-on cannot be statically resolved; shell:bash policy for self-hosted Windows runners cannot be mechanically verified. If the resolved label set may include self-hosted Windows, audit the steps' shell choices manually or refactor the emitting job to use literal echo 'labels=[...]' >> "$GITHUB_OUTPUT" lines so the validator can prove the labels.`,
          "warning"
        )
      );
      continue;
    }

    // Apply the policy only if ANY resolved label set is self-hosted
    // Windows. (For dynamic-resolved jobs, all branches matter -- if
    // even one branch is self-hosted-Windows, the steps' shells must
    // satisfy the policy because the runtime could pick that branch.)
    const appliesToAtLeastOneBranch = resolution.labelSets.some((labels) =>
      isSelfHostedWindowsLabelSet(labels)
    );
    if (!appliesToAtLeastOneBranch) {
      continue;
    }

    const jobDefaultsShell = extractJobDefaultsShell(lines, job);
    const steps = extractJobSteps(lines, job);

    for (const step of steps) {
      // `uses:` steps inherit the composite action's shell decisions;
      // the rule applies to inline `run:` steps only.
      if (!step.run) {
        continue;
      }

      const stepShell = step.shell;
      // Resolve `effectiveShell` AND record where it came from. The
      // source annotation is appended to any subsequent violation
      // message so a fixture can mutation-test the rule by NULL-ing
      // the workflow-defaults lookup: the resulting violation would
      // lose the `via workflow defaults` substring (or the message
      // would change shape entirely).
      let effectiveShell;
      let effectiveShellSource;
      if (typeof stepShell === "string" && stepShell.length > 0) {
        effectiveShell = stepShell;
        effectiveShellSource = "step";
      } else if (typeof jobDefaultsShell === "string" && jobDefaultsShell.length > 0) {
        effectiveShell = jobDefaultsShell;
        effectiveShellSource = "job defaults";
      } else if (typeof workflowDefaultsShell === "string" && workflowDefaultsShell.length > 0) {
        effectiveShell = workflowDefaultsShell;
        effectiveShellSource = "workflow defaults";
      } else {
        effectiveShell = "";
        effectiveShellSource = null;
      }

      // Step-level explicit shell wins. If it is an allowed value we
      // are done; if it is bash/sh, flag.
      if (typeof effectiveShell === "string" && effectiveShell.length > 0) {
        const isBash = effectiveShell === "bash" || effectiveShell === "sh";
        if (isBash) {
          const sourceAnnotation = effectiveShellSource
            ? ` (resolved via ${effectiveShellSource})`
            : "";
          violations.push(
            new Violation(
              relativePath,
              step.run.line,
              `shell: ${effectiveShell}${sourceAnnotation}`,
              `Job '${job.id}' step (line ${step.run.line}): shell: ${effectiveShell}${sourceAnnotation} on a self-hosted Windows runner is forbidden -- the runner agent's PATH resolves bash to the WSL stub (C:\\Windows\\System32\\bash.exe) which fails with "Windows Subsystem for Linux has no installed distributions." Use shell: pwsh, an explicit absolute Git Bash path with a {0} placeholder, or the composite action at .github/actions/print-self-hosted-runner-diagnostics for diagnostic blocks.`,
              "error"
            )
          );
          continue;
        }
        if (isAllowedSelfHostedWindowsShell(effectiveShell)) {
          continue;
        }
        // Other shell values (e.g. `python`) are out of scope; let
        // them through.
        continue;
      }

      // No shell specified anywhere. GitHub Actions defaults to bash
      // on Windows when the workflow has no defaults.run.shell. Flag.
      violations.push(
        new Violation(
          relativePath,
          step.run.line,
          "shell: <unspecified>",
          `Job '${job.id}' step (line ${step.run.line}): no shell specified for a self-hosted Windows runner -- the default bash resolves to the WSL stub (C:\\Windows\\System32\\bash.exe). Add shell: pwsh to the step (or set defaults.run.shell: pwsh on the job/workflow). For pure echo-style diagnostic blocks prefer the composite action at .github/actions/print-self-hosted-runner-diagnostics.`,
          "error"
        )
      );
    }
  }

  return violations;
}

// Lowercased, leading-`./`-stripped `uses` ref for the compute-unity-assemblies
// composite action. The validator normalizes each step's `uses` the same way
// before comparing.
const COMPUTE_UNITY_ASSEMBLIES_USES = ".github/actions/compute-unity-assemblies";

// Lowercased substring marker for the organization build-lock acquire action.
// A step whose `uses` contains this marker consumes the shared Unity
// activation seat (the lock is only taken for licensed Unity work).
const ORGANIZATION_BUILD_LOCK_ACQUIRE_MARKER = "acquire-build-lock";

function normalizeUsesRef(rawUses) {
  if (typeof rawUses !== "string") {
    return "";
  }

  return rawUses.trim().toLowerCase().replace(/^\.\//, "");
}

function stepRunText(step) {
  if (!step || typeof step !== "object") {
    return "";
  }

  const run = step.run;
  if (typeof run === "string") {
    return run;
  }

  // A list-form `run:` is invalid GitHub Actions YAML, but tolerate it
  // defensively by joining rather than throwing.
  if (Array.isArray(run)) {
    return run.filter((entry) => typeof entry === "string").join("\n");
  }

  return "";
}

function stepUsesComputeUnityAssemblies(step) {
  return normalizeUsesRef(step && step.uses) === COMPUTE_UNITY_ASSEMBLIES_USES;
}

/**
 * A "license-consuming" step is one that provisions or runs Unity under the
 * paid activation seat. It is identified by concrete markers, mirroring the
 * canonical is-empty gate in unity-tests.yml:
 *   - a `run:` that invokes scripts/unity/ensure-editor.ps1 (editor provision),
 *   - a `uses:` containing "acquire-build-lock" (the org Unity lock), OR
 *   - a `run:` that invokes scripts/unity/run-ci-tests.ps1 (the Unity run /
 *     ephemeral-project generation).
 * These are exactly the steps that waste paid Unity license / lock time when
 * the assembly discovery resolved an empty list, so each must be gated on the
 * compute step's is-empty output.
 */
function stepConsumesUnityLicense(step) {
  const runText = stepRunText(step);
  if (/\bensure-editor\.ps1\b/.test(runText)) {
    return true;
  }
  if (/\brun-ci-tests\.ps1\b/.test(runText)) {
    return true;
  }

  const usesRef = normalizeUsesRef(step && step.uses);
  return usesRef.includes(ORGANIZATION_BUILD_LOCK_ACQUIRE_MARKER);
}

/**
 * Returns true when `ifExpression` gates the step on any of the provided
 * compute step ids resolving a non-empty assembly list, i.e. contains the
 * normalized token `steps.<id>.outputs.is-empty != 'true'`. The match is
 * tolerant of surrounding whitespace and of single/double quote style around
 * the `true` literal, and allows the token to be ANDed (or otherwise combined)
 * with other conditions.
 */
function ifExpressionGatesOnComputeEmptiness(ifExpression, computeIds) {
  if (typeof ifExpression !== "string" || computeIds.length === 0) {
    return false;
  }

  // Strip whitespace and unify double quotes to single so quote/whitespace
  // variants normalize to one token form.
  const normalized = ifExpression.replace(/\s+/g, "").replace(/"/g, "'");

  return computeIds.some((id) => {
    const token = `steps.${id}.outputs.is-empty!='true'`;
    return normalized.includes(token);
  });
}

/**
 * Resolves the 1-indexed start line of a step's mapping node from the parsed
 * YAML document, for best-effort violation reporting. Falls back to line 0 when
 * the AST node carries no usable range.
 */
function resolveNodeStartLine(content, node) {
  if (!node || !Array.isArray(node.range) || typeof node.range[0] !== "number") {
    return 0;
  }

  return content.slice(0, node.range[0]).split("\n").length;
}

/**
 * Class-prevention rule for the compute-unity-assemblies is-empty gate.
 *
 * The canonical pattern lives in unity-tests.yml: the "Compute test assembly
 * list" step carries an `id`, and every step that consumes the paid Unity
 * activation seat (editor provision, org lock acquire, Unity run) is gated on
 * `steps.<id>.outputs.is-empty != 'true'`. Without that gate an empty
 * discovery still provisions the editor, takes the org Unity lock, and runs
 * with an empty assembly list, wasting paid license/lock time before failing
 * late at verify.
 *
 * For every job: if any step uses ./.github/actions/compute-unity-assemblies,
 * that step MUST declare an `id`; and every license-consuming step in the SAME
 * job MUST carry an `if:` whose expression gates on a compute id in this job.
 *
 * The workflow is parsed structurally with the `yaml` package so the check is
 * formatting-invariant (no assumptions about single-line tags, attribute
 * order, or line regex). A YAML parse failure is a HARD error, never a silent
 * pass. Workflows that do not use the action produce no violations.
 *
 * @param {string} relativePath - Repo-relative workflow path (for messages)
 * @param {string} content - Raw workflow file content
 * @returns {Violation[]} Array of violations found
 */
function findComputeUnityAssembliesGateViolations(relativePath, content) {
  const violations = [];

  const yamlResult = loadYamlModule();
  if (yamlResult.error) {
    // Treat an unresolved `yaml` package as a HARD error, never a silent pass:
    // without structural parsing the gate cannot be verified, so report an
    // actionable Violation rather than letting the require crash the validator.
    violations.push(
      new Violation(
        relativePath,
        0,
        "compute-unity-assemblies gate",
        `Unable to load the 'yaml' package needed to verify the compute-unity-assemblies is-empty gate (${yamlResult.error.message}). Run 'npm ci' (or 'npm install yaml') so the validator can parse workflows structurally.`,
        "error"
      )
    );
    return violations;
  }
  const yaml = yamlResult.module;

  let document;
  try {
    document = yaml.parseDocument(content, { prettyErrors: true });
  } catch (error) {
    const message = error && error.message ? error.message : String(error);
    violations.push(
      new Violation(
        relativePath,
        0,
        "compute-unity-assemblies gate",
        `Unable to parse workflow YAML to verify the compute-unity-assemblies is-empty gate: ${message}`,
        "error"
      )
    );
    return violations;
  }

  if (document && Array.isArray(document.errors) && document.errors.length > 0) {
    const first = document.errors[0];
    const message = first && first.message ? first.message : String(first);
    violations.push(
      new Violation(
        relativePath,
        0,
        "compute-unity-assemblies gate",
        `Unable to parse workflow YAML to verify the compute-unity-assemblies is-empty gate: ${message}`,
        "error"
      )
    );
    return violations;
  }

  let parsed;
  try {
    parsed = document.toJS({ maxAliasCount: -1 });
  } catch (error) {
    const message = error && error.message ? error.message : String(error);
    violations.push(
      new Violation(
        relativePath,
        0,
        "compute-unity-assemblies gate",
        `Unable to materialize workflow YAML to verify the compute-unity-assemblies is-empty gate: ${message}`,
        "error"
      )
    );
    return violations;
  }

  const jobs = parsed && typeof parsed === "object" ? parsed.jobs : null;
  if (!jobs || typeof jobs !== "object") {
    // A workflow with no jobs cannot use the action; nothing to gate.
    return violations;
  }

  const jobsNode = document.get("jobs", true);

  for (const [jobId, job] of Object.entries(jobs)) {
    if (!job || typeof job !== "object" || !Array.isArray(job.steps)) {
      continue;
    }

    const stepsNode =
      jobsNode && typeof jobsNode.getIn === "function"
        ? jobsNode.getIn([jobId, "steps"], true)
        : null;
    const stepNodes = stepsNode && Array.isArray(stepsNode.items) ? stepsNode.items : [];

    const computeSteps = [];
    job.steps.forEach((step, index) => {
      if (step && typeof step === "object" && stepUsesComputeUnityAssemblies(step)) {
        computeSteps.push({ step, index });
      }
    });

    if (computeSteps.length === 0) {
      // This job does not use the action -- no gate is required.
      continue;
    }

    // Every compute step must declare an id (the gate references it).
    const computeIds = [];
    for (const { step, index } of computeSteps) {
      const id = typeof step.id === "string" ? step.id.trim() : "";
      if (id.length > 0) {
        computeIds.push(id);
        continue;
      }

      violations.push(
        new Violation(
          relativePath,
          resolveNodeStartLine(content, stepNodes[index]),
          "compute-unity-assemblies without id",
          `Job '${jobId}': the step using ./.github/actions/compute-unity-assemblies must declare an 'id' so license-consuming steps can gate on its is-empty output (mirror unity-tests.yml's 'id: compute').`,
          "error"
        )
      );
    }

    // Without a usable compute id there is nothing for the license-consuming
    // steps to reference; the missing-id violation already covers this job.
    if (computeIds.length === 0) {
      continue;
    }

    job.steps.forEach((step, index) => {
      if (!step || typeof step !== "object" || !stepConsumesUnityLicense(step)) {
        return;
      }

      if (ifExpressionGatesOnComputeEmptiness(step.if, computeIds)) {
        return;
      }

      const stepLabel =
        typeof step.name === "string" && step.name.trim().length > 0
          ? `'${step.name.trim()}'`
          : `index ${index}`;

      violations.push(
        new Violation(
          relativePath,
          resolveNodeStartLine(content, stepNodes[index]),
          "Unity license-consuming step not gated on is-empty",
          `Job '${jobId}': license-consuming step ${stepLabel} must be gated on the compute step's is-empty output. Add an if: containing steps.${computeIds[0]}.outputs.is-empty != 'true' (combine with any existing condition) so an empty assembly discovery does not provision the editor, take the org Unity lock, or run Unity. Mirror unity-tests.yml.`,
          "error"
        )
      );
    });
  }

  return violations;
}

/**
 * Validates a single workflow file.
 *
 * @param {string} filePath - Absolute path to the workflow file
 * @returns {Violation[]} Array of violations found
 */
function validateWorkflow(filePath, options = {}) {
  const violations = [];
  const repoRoot = options.repoRoot || REPO_ROOT;
  const isIgnoredPathFn = options.isIgnoredPathFn || isGitIgnoredPath;
  const relativePath = path.relative(repoRoot, filePath).replace(/\\/g, "/");
  const lineLengthPolicy = resolveWorkflowLineLengthPolicy(repoRoot);
  const maxWorkflowLineLength = lineLengthPolicy.max;

  let content;
  try {
    content = fs.readFileSync(filePath, "utf8");
  } catch (error) {
    violations.push(new Violation(relativePath, 0, "", `Failed to read file: ${error.message}`));
    return violations;
  }

  const lines = normalizeToLf(content).split("\n");

  violations.push(
    ...findWorkflowLineLengthViolations(
      relativePath,
      lines,
      maxWorkflowLineLength,
      lineLengthPolicy
    )
  );

  lines.forEach((line, index) => {
    const lineNumber = index + 1;

    // Check for forbidden single-line multi-pattern renormalize
    if (isForbiddenRenormalizePattern(line)) {
      violations.push(
        new Violation(
          relativePath,
          lineNumber,
          line.trim(),
          "FORBIDDEN: Single-line multi-pattern git add --renormalize. Use per-extension loop pattern instead.",
          "error"
        )
      );
    }

    // Check for unguarded renormalize commands (warning only for non-variable patterns)
    if (
      line.includes("git add") &&
      line.includes("--renormalize") &&
      !usesVariableExtensionPattern(line) &&
      line.includes("*.")
    ) {
      if (!hasExistenceCheck(lines, index)) {
        violations.push(
          new Violation(
            relativePath,
            lineNumber,
            line.trim(),
            "WARNING: git add --renormalize without existence check may fail if pattern matches no files.",
            "warning"
          )
        );
      }
    }
  });

  try {
    violations.push(...findIgnoredPathViolations(relativePath, lines, repoRoot, isIgnoredPathFn));

    const packageLockIgnored = isIgnoredPathFn(repoRoot, "package-lock.json");
    violations.push(...findLockfileInstallViolations(relativePath, lines, packageLockIgnored));

    violations.push(...findPreCommitInstallHookWriterViolations(relativePath, lines));

    violations.push(...findWindowsBashPortabilityViolations(relativePath, lines));

    violations.push(...findForbiddenRunsOnGroupViolations(relativePath, lines));

    violations.push(...findForbiddenSharedConcurrencyViolations(relativePath, lines));

    violations.push(...findConcurrencyQueueViolations(relativePath, lines));

    violations.push(...findMatrixConcurrencyEvictionViolations(relativePath, lines));

    violations.push(...findGameCiTestRunnerInputViolations(relativePath, lines));

    violations.push(...findUnityGameCiLockAndPreflightViolations(relativePath, lines));

    violations.push(...findUnityNativeProvisioningViolations(relativePath, lines));

    violations.push(...findUnityLicenseReturnViolations(relativePath, lines));

    violations.push(...findForbiddenUnityLicenseSecretViolations(relativePath, lines));

    violations.push(...findRequiredUnityLicenseSecretViolations(relativePath, lines));

    violations.push(...findSelfHostedLabelAllowlistViolations(relativePath, lines));

    violations.push(...findDynamicRunsOnMissingNeedsViolations(relativePath, lines));

    violations.push(...findChangelogCoverageCheckoutViolations(relativePath, lines));

    violations.push(...findSelfHostedRunnerPreflightViolations(relativePath, lines));

    violations.push(...findForbidPlainShellBashOnSelfHostedWindowsViolations(relativePath, lines));

    violations.push(...findUnityLockTimeoutViolations(relativePath, lines));

    violations.push(...findCrossPlatformPreflightTargetedGateViolations(relativePath, lines));

    violations.push(...findLycheeActionPolicyViolations(relativePath, content));

    violations.push(...findComputeUnityAssembliesGateViolations(relativePath, content));
  } catch (error) {
    violations.push(
      new Violation(
        relativePath,
        0,
        "git check-ignore",
        `Workflow validation failed while evaluating ignore policy: ${error.message}`,
        "error"
      )
    );
  }

  return violations;
}

function normalizeRequiredWorkflowPath(workflowFile, workflowDirRelative = ".github/workflows") {
  const normalized = String(workflowFile || "").replace(/\\/g, "/");
  if (normalized.startsWith(".github/")) {
    return normalized;
  }
  return `${workflowDirRelative}/${normalized}`.replace(/\/+/g, "/");
}

function findRequiredWorkflowFileViolations(workflowFiles, options = {}) {
  const workflowDirRelative = options.workflowDirRelative || ".github/workflows";
  const present = new Set(
    Array.from(workflowFiles || [], (file) =>
      normalizeRequiredWorkflowPath(file, workflowDirRelative)
    )
  );
  const violations = [];

  for (const requiredPath of REQUIRED_ACTIVE_WORKFLOW_PATHS) {
    if (present.has(requiredPath)) {
      continue;
    }

    violations.push(
      new Violation(
        requiredPath,
        0,
        "required workflow file",
        `Required workflow file ${requiredPath} is missing. The docs link-check policy depends on this active workflow; restore it or update validate-workflows.js intentionally.`,
        "error"
      )
    );
  }

  return violations;
}

/**
 * Main entry point.
 */
function main() {
  console.log("Validating workflow files for policy and reliability patterns...\n");

  let workflowFiles = [];
  if (!fs.existsSync(WORKFLOWS_DIR)) {
    console.log(`Workflows directory not found: ${WORKFLOWS_DIR}`);
  } else {
    try {
      workflowFiles = fs
        .readdirSync(WORKFLOWS_DIR)
        .filter((file) => file.endsWith(".yml") || file.endsWith(".yaml"));
    } catch (error) {
      // Unlike recursive scanners, this validator cannot proceed without the workflows root.
      console.error(`Unable to read workflows directory: ${error.message}`);
      process.exit(1);
    }
  }

  if (workflowFiles.length > 0) {
    console.log(`Found ${workflowFiles.length} workflow file(s)\n`);
  } else {
    console.log("No workflow files found.\n");
  }

  const allViolations = [];
  allViolations.push(...findRequiredWorkflowFileViolations(workflowFiles));

  workflowFiles.forEach((file) => {
    const filePath = path.join(WORKFLOWS_DIR, file);
    const violations = validateWorkflow(filePath);
    allViolations.push(...violations);
  });

  allViolations.push(...validatePreCommitConfigLineLengths());

  const errors = allViolations.filter((v) => v.severity === "error");
  const warnings = allViolations.filter((v) => v.severity === "warning");

  if (allViolations.length === 0) {
    console.log("[OK] All workflow files passed validation.\n");
    console.log("No workflow policy violations detected.");
    process.exit(0);
  }

  if (errors.length > 0) {
    console.log(`\n[ERROR] Found ${errors.length} error(s):\n`);
    errors.forEach((v) => console.log(v.toString() + "\n"));
  }

  if (warnings.length > 0) {
    console.log(`\n[WARN] Found ${warnings.length} warning(s):\n`);
    warnings.forEach((v) => console.log(v.toString() + "\n"));
  }

  console.log("\n--- Summary ---");
  console.log(`Errors:   ${errors.length}`);
  console.log(`Warnings: ${warnings.length}`);

  if (errors.length > 0) {
    console.log("\nValidation FAILED. Please fix the errors above.");
    console.log(
      "\nSee .llm/skills/github-actions/git-renormalize-patterns.md for renormalize guidance."
    );
    process.exit(1);
  }

  console.log("\nValidation passed with warnings.");
  process.exit(0);
}

// Export for testing when required as a module
if (typeof module !== "undefined" && module.exports) {
  module.exports = {
    isForbiddenRenormalizePattern,
    hasExistenceCheck,
    isGitIgnoredPath,
    extractWorkflowPathEntries,
    findIgnoredPathViolations,
    extractRunBlocks,
    findLockfileInstallViolations,
    findPreCommitInstallHookWriterViolations,
    extractJobs,
    extractWorkflowDefaultsShell,
    extractJobDefaultsShell,
    jobTargetsWindows,
    extractJobSteps,
    detectBashSyntaxPattern,
    findWindowsBashPortabilityViolations,
    findForbiddenRunsOnGroupViolations,
    findForbiddenSharedConcurrencyViolations,
    findConcurrencyQueueViolations,
    findMatrixConcurrencyEvictionViolations,
    findGameCiTestRunnerInputViolations,
    findUnityGameCiLockAndPreflightViolations,
    findUnityNativeProvisioningViolations,
    findUnityLicenseReturnViolations,
    findForbiddenUnityLicenseSecretViolations,
    findRequiredUnityLicenseSecretViolations,
    findSelfHostedLabelAllowlistViolations,
    findDynamicRunsOnMissingNeedsViolations,
    extractJobConcurrencyGroup,
    extractWorkflowConcurrencyGroup,
    extractConcurrencyGroupFromBlock,
    jobHasMatrix,
    extractJobMatrixMaxParallel,
    extractJobTimeoutMinutes,
    extractStepTimeoutMinutes,
    findUnityLockTimeoutViolations,
    findCrossPlatformPreflightTargetedGateViolations,
    findLycheeActionPolicyViolations,
    findRequiredWorkflowFileViolations,
    findComputeUnityAssembliesGateViolations,
    stepUsesComputeUnityAssemblies,
    stepConsumesUnityLicense,
    ifExpressionGatesOnComputeEmptiness,
    UNITY_LOCK_RUN_BUDGET_MINUTES,
    extractJobRunsOn,
    extractJobNeeds,
    parseInlineLabelArray,
    extractJobOutputsSourceMap,
    extractEmittedLabelSetsFromBash,
    SELF_HOSTED_LABEL_ALLOWLIST,
    runTextInvokesChangelogCoverage,
    stepHasFullHistoryCheckout,
    findChangelogCoverageCheckoutViolations,
    NPM_SCRIPTS_REQUIRING_GIT_HISTORY,
    REQUIRED_LYCHEE_VERSION,
    ADVISORY_LYCHEE_REPORT_PATH,
    extractStaticJobLabels,
    jobIsRunnerAccessPreflight,
    findSelfHostedRunnerPreflightViolations,
    isSelfHostedWindowsLabelSet,
    isAllowedSelfHostedWindowsShell,
    findForbidPlainShellBashOnSelfHostedWindowsViolations,
    resolveJobLabelSetsForShellPolicy,
    resolveWorkflowLineLengthPolicy,
    resolveWorkflowLineLengthMax,
    findWorkflowLineLengthViolations,
    validatePreCommitConfigLineLengths,
    validateWorkflow,
    Violation
  };
}

// Only run main when executed directly (not when required as a module)
if (require.main === module) {
  main();
}
