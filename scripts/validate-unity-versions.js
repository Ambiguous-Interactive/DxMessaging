#!/usr/bin/env node
// Drift detector for Unity versions across CI. .github/unity-versions.json is
// authoritative; listed consumers mirror all/latest/release and other active
// workflows may contain no version literals. Disabled workflows are not scanned.

"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.join(__dirname, "..");
const CANONICAL_RELATIVE_PATH = ".github/unity-versions.json";

// A Unity version literal, e.g. 2021.3.45f1 / 6000.3.16f1 / 2022.1.0b3.
// The suffix letter is one of a (alpha), b (beta), f (final), p (patch).
// NOTE: this exported regex is GLOBAL (has the `g` flag), so it carries a
// mutable `lastIndex`. Callers must use `.match()` (or clone the source via
// `new RegExp(VERSION_LITERAL_REGEX.source, "g")`) -- never `.test()`/`.exec()`
// directly, whose stateful `lastIndex` advance is a footgun across calls.
const VERSION_LITERAL_REGEX = /[0-9]+\.[0-9]+\.[0-9]+[abfp][0-9]+/g;
const VERSION_LITERAL_ANCHORED_REGEX = /^[0-9]+\.[0-9]+\.[0-9]+[abfp][0-9]+$/;

const CONSUMER_POLICIES = Object.freeze({
  ".github/workflows/perf-numbers.yml": "mirror-latest",
  ".github/workflows/unity-tests.yml": "mirror-all",
  ".github/workflows/unity-benchmarks.yml": "mirror-all",
  ".github/workflows/runner-bootstrap.yml": "mirror-all",
  "scripts/unity/maintain-windows-runner.ps1": "mirror-all",
  "scripts/unity/install-runner-maintenance-task.ps1": "mirror-all",
  ".github/workflows/release.yml": "mirror-release"
});

function loadCanonical(repoRoot = REPO_ROOT) {
  const absolutePath = path.join(repoRoot, CANONICAL_RELATIVE_PATH);
  let raw;
  try {
    raw = fs.readFileSync(absolutePath, "utf8");
  } catch (error) {
    throw new Error(
      `Cannot read canonical Unity version file '${CANONICAL_RELATIVE_PATH}': ${error.message}`
    );
  }

  let data;
  try {
    data = JSON.parse(raw);
  } catch (error) {
    throw new Error(
      `Canonical Unity version file '${CANONICAL_RELATIVE_PATH}' is not valid JSON: ${error.message}`
    );
  }

  return { data, path: absolutePath };
}

function parseVersionTriple(version) {
  const match = /^([0-9]+)\.([0-9]+)\.([0-9]+)[abfp][0-9]+$/.exec(version);
  if (!match) {
    return null;
  }
  return [Number(match[1]), Number(match[2]), Number(match[3])];
}

function compareTriple(a, b) {
  for (let i = 0; i < 3; i++) {
    if (a[i] !== b[i]) {
      return a[i] - b[i];
    }
  }
  return 0;
}

function validateCanonicalSchema(data) {
  const errors = [];

  if (data === null || typeof data !== "object" || Array.isArray(data)) {
    return [`${CANONICAL_RELATIVE_PATH}: root must be a JSON object.`];
  }

  const all = data.all;
  if (!Array.isArray(all) || all.length === 0) {
    errors.push(
      `${CANONICAL_RELATIVE_PATH}: \`all\` must be a non-empty array of version strings.`
    );
    // Without a usable `all`, the remaining checks cannot run meaningfully.
    return errors;
  }

  const everyEntryIsString = all.every((entry) => typeof entry === "string");
  if (!everyEntryIsString) {
    errors.push(`${CANONICAL_RELATIVE_PATH}: every entry of \`all\` must be a string.`);
    return errors;
  }

  for (const entry of all) {
    if (!VERSION_LITERAL_ANCHORED_REGEX.test(entry)) {
      errors.push(
        `${CANONICAL_RELATIVE_PATH}: \`all\` entry '${entry}' is not a valid Unity version ` +
          "(expected major.minor.patch + suffix, e.g. 6000.3.16f1)."
      );
    }
  }

  const seen = new Set();
  for (const entry of all) {
    if (seen.has(entry)) {
      errors.push(`${CANONICAL_RELATIVE_PATH}: \`all\` contains duplicate entry '${entry}'.`);
    }
    seen.add(entry);
  }

  // Strict ascending by leading major.minor.patch triple. Only run when every
  // entry parsed (otherwise the format errors above already explain the failure).
  // CONTRACT: exactly one build per major.minor.patch line. Because the compare
  // ignores the build suffix (f1/p2/...), listing TWO builds of the same X.Y.Z
  // (e.g. 2022.3.45f1 and 2022.3.45p2) is intentionally REJECTED as non-strict.
  const triples = all.map((entry) => parseVersionTriple(entry));
  if (triples.every((triple) => triple !== null)) {
    for (let i = 1; i < triples.length; i++) {
      if (compareTriple(triples[i - 1], triples[i]) >= 0) {
        errors.push(
          `${CANONICAL_RELATIVE_PATH}: \`all\` must be strictly ascending by major.minor.patch; ` +
            `'${all[i - 1]}' is not before '${all[i]}'.`
        );
      }
    }
  }

  const release = data.release;
  if (typeof release !== "string") {
    errors.push(`${CANONICAL_RELATIVE_PATH}: \`release\` must be a string.`);
  } else if (!all.includes(release)) {
    errors.push(
      `${CANONICAL_RELATIVE_PATH}: \`release\` '${release}' must be a member of \`all\`.`
    );
  }

  return errors;
}

/**
 * Strips an inline comment from a single source line before version scanning.
 *
 * For `.yml`, `.yaml`, `.ps1`, and `.sh` we split on the first `#` (all four
 * use `#` for line comments). This is intentionally simple: it does NOT
 * understand `#` inside quoted strings, so a version that only appears inside a
 * quoted string AFTER an unquoted-looking `#` could be missed. None of the
 * policed consumer files place a version literal after a `#` on the same line,
 * so the simplification is safe here (documented per the spec). The literal
 * regex then scans the comment-stripped remainder.
 *
 * @param {string} line A single source line.
 * @param {string} extension The lowercased file extension including the dot.
 * @returns {string} The line with any inline comment removed.
 */
function stripInlineComment(line, extension) {
  if (
    extension === ".yml" ||
    extension === ".yaml" ||
    extension === ".ps1" ||
    extension === ".sh"
  ) {
    const hashIndex = line.indexOf("#");
    if (hashIndex !== -1) {
      return line.slice(0, hashIndex);
    }
  }
  return line;
}

function extractVersionLiterals(content, extension) {
  const results = [];
  const lines = content.split(/\r?\n/);

  // Use a FRESH global regex instance per call so the exported
  // VERSION_LITERAL_REGEX's mutable `lastIndex` can never make matching
  // stateful across invocations (a stateful-`.test()`/`.exec()` footgun).
  const re = new RegExp(VERSION_LITERAL_REGEX.source, "g");

  for (let i = 0; i < lines.length; i++) {
    const code = stripInlineComment(lines[i], extension);
    const matches = code.match(re);
    if (!matches) {
      continue;
    }
    for (const version of matches) {
      results.push({ version, line: i + 1 });
    }
  }

  return results;
}

function checkConsumer({ relativePath, policy, literals, all, release }) {
  const violations = [];

  if (policy === "no-literals") {
    for (const { version, line } of literals) {
      violations.push(
        `${relativePath}:${line}: hardcoded Unity version '${version}'; expected NONE ` +
          `(read .github/unity-versions.json at runtime instead).`
      );
    }
    return violations;
  }

  if (policy === "mirror-release") {
    if (literals.length === 0) {
      violations.push(
        `${relativePath}:0: no Unity version literal found; expected at least one equal to ` +
          `release '${release}'.`
      );
      return violations;
    }
    for (const { version, line } of literals) {
      if (version !== release) {
        violations.push(
          `${relativePath}:${line}: Unity version '${version}' does not match canonical ` +
            `release; expected '${release}'.`
        );
      }
    }
    return violations;
  }

  if (policy === "mirror-latest") {
    const latest = all.at(-1);
    if (literals.length === 0) {
      return [
        `${relativePath}:0: no Unity version literal found; expected at least one equal to ` +
          `latest '${latest}'.`
      ];
    }
    for (const { version, line } of literals) {
      if (version !== latest) {
        violations.push(
          `${relativePath}:${line}: Unity version '${version}' does not match canonical ` +
            `latest; expected '${latest}'.`
        );
      }
    }
    return violations;
  }

  if (policy === "mirror-all") {
    const allSet = new Set(all);
    const foundSet = new Set();

    for (const { version, line } of literals) {
      foundSet.add(version);
      if (!allSet.has(version)) {
        violations.push(
          `${relativePath}:${line}: Unity version '${version}' is not in canonical \`all\`; ` +
            `expected one of [${all.join(", ")}].`
        );
      }
    }

    for (const expected of all) {
      if (!foundSet.has(expected)) {
        violations.push(
          `${relativePath}:0: canonical Unity version '${expected}' is missing; expected the ` +
            `literal set to mirror \`all\` exactly [${all.join(", ")}].`
        );
      }
    }
    return violations;
  }

  throw new Error(`Unknown consumer policy '${policy}' for ${relativePath}.`);
}

function resolveWorkflowPolicy(relativePath) {
  if (Object.prototype.hasOwnProperty.call(CONSUMER_POLICIES, relativePath)) {
    return CONSUMER_POLICIES[relativePath];
  }
  // Default policy for any active workflow not explicitly listed.
  if (/^\.github\/workflows\/[^/]+\.ya?ml$/.test(relativePath)) {
    return "no-literals";
  }
  return null;
}

/**
 * Lists active workflow files (repo-relative POSIX paths) under
 * `.github/workflows/`. The disabled-archive directory is a sibling, not a
 * child, so it is excluded automatically.
 *
 * @param {string} repoRoot Repository root.
 * @returns {string[]} Sorted list of repo-relative workflow paths.
 */
function listActiveWorkflows(repoRoot) {
  const workflowsDir = path.join(repoRoot, ".github", "workflows");
  let entries = [];
  try {
    entries = fs.readdirSync(workflowsDir);
  } catch (_error) {
    return [];
  }
  return entries
    .filter((name) => name.endsWith(".yml") || name.endsWith(".yaml"))
    .map((name) => `.github/workflows/${name}`)
    .sort();
}

/**
 * Computes the full set of files to scan and their policies. Combines the
 * explicit CONSUMER_POLICIES entries with the default `no-literals` policy for
 * any other active workflow.
 *
 * @param {string} repoRoot Repository root.
 * @returns {Map<string, string>} Map of repo-relative path -> policy.
 */
function resolveScanTargets(repoRoot) {
  const targets = new Map();

  // Every active workflow (explicit policy or defaulted to no-literals).
  for (const relativePath of listActiveWorkflows(repoRoot)) {
    const policy = resolveWorkflowPolicy(relativePath);
    if (policy) {
      targets.set(relativePath, policy);
    }
  }

  // Explicitly-listed non-workflow consumers (the .ps1 scripts).
  for (const [relativePath, policy] of Object.entries(CONSUMER_POLICIES)) {
    if (!targets.has(relativePath)) {
      targets.set(relativePath, policy);
    }
  }

  return targets;
}

/**
 * Entry point. Loads + validates the canonical file, then enforces every
 * consumer policy. Prints a clear report and returns the process exit code.
 *
 * @param {object} [options] Options.
 * @param {string} [options.repoRoot] Repository root (defaults to this repo).
 * @param {(message?: unknown) => void} [options.log] stdout sink (default
 *   console.log).
 * @param {(message?: unknown) => void} [options.errorLog] stderr sink (default
 *   console.error).
 * @returns {number} 0 on success, 1 on any failure.
 */
function main(options = {}) {
  const repoRoot = options.repoRoot || REPO_ROOT;
  const log = options.log || console.log;
  const errorLog = options.errorLog || console.error;

  let canonical;
  try {
    canonical = loadCanonical(repoRoot);
  } catch (error) {
    errorLog(error.message);
    return 1;
  }

  const schemaErrors = validateCanonicalSchema(canonical.data);
  if (schemaErrors.length > 0) {
    errorLog("Canonical Unity version schema is invalid:");
    for (const message of schemaErrors) {
      errorLog(`  ${message}`);
    }
    return 1;
  }

  const all = canonical.data.all;
  const release = canonical.data.release;
  const latest = all[all.length - 1];

  const targets = resolveScanTargets(repoRoot);
  const violations = [];
  let filesChecked = 0;

  for (const [relativePath, policy] of targets) {
    const absolutePath = path.join(repoRoot, relativePath);
    let content;
    try {
      content = fs.readFileSync(absolutePath, "utf8");
    } catch (error) {
      violations.push(
        `${relativePath}:0: expected consumer file is missing or unreadable (${error.message}).`
      );
      continue;
    }

    filesChecked += 1;
    const extension = path.extname(relativePath).toLowerCase();
    const literals = extractVersionLiterals(content, extension);
    violations.push(...checkConsumer({ relativePath, policy, literals, all, release }));
  }

  if (violations.length > 0) {
    errorLog("Unity version drift detected (single source: .github/unity-versions.json):\n");
    for (const violation of violations) {
      errorLog(`  ${violation}`);
    }
    errorLog(
      `\n${violations.length} violation(s) across ${filesChecked} consumer file(s). ` +
        "Bump versions only in .github/unity-versions.json and re-run."
    );
    return 1;
  }

  log("Unity version single-source check passed.");
  log(`  canonical: ${CANONICAL_RELATIVE_PATH}`);
  log(`  all:       [${all.join(", ")}]`);
  log(`  latest:    ${latest}`);
  log(`  release:   ${release}`);
  log(`  consumers: ${filesChecked} file(s) checked, no drift.`);
  return 0;
}

module.exports = {
  validateCanonicalSchema,
  extractVersionLiterals,
  checkConsumer,
  resolveWorkflowPolicy,
  parseVersionTriple
};

if (require.main === module) {
  process.exit(main());
}
