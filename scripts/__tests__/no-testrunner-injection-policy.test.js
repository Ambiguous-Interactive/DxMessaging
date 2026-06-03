/**
 * @fileoverview Repository-wide policy guards against re-introducing
 * `--testRunner` injection (and its env-driven cousins) into any hook,
 * workflow, package field, or Jest configuration file.
 *
 * Phase 2 of the Jest-driven pre-push hardening plan. The single-file guard
 * at run-managed-jest-no-injected-test-runner.test.js pins the wrapper
 * source; this file closes EVERY other ingress vector:
 *
 *   Policy 1 — `.pre-commit-config.yaml` hook entries/args/bash bodies.
 *   Policy 2 — `.github/workflows/*.{yml,yaml}` run-block bodies.
 *   Policy 3 — `package.json` `jest.testRunner` field (recursively, including
 *              nested `projects[*]` and `overrides` shapes).
 *   Policy 4 — `jest.config.*` / `.jestrc*` files at repo root and scripts/.
 *   Policy 5 — Absolute `--config` paths in hooks and workflows.
 *   Policy 6 — Env-driven `JEST_TEST_RUNNER` / `JEST_CONFIG` references,
 *              with a coarse unstripped-source fallback that catches variable-
 *              assignment indirection (false-positives allow-listed per file).
 *   Policy 7 — Literal `--testRunner` in any scripts/**\/*.js file.
 *   Policy 8 — PERMITTED_PATHS sanity: every allow-list entry resolves.
 *
 * Limitations (documented, not bugs):
 *   - Source-scan policies cannot detect runtime indirection. If code reads a
 *     forbidden flag from a non-obvious source (e.g. a file, an environment
 *     variable looked up via `process.env[dynamicKey]`, or a network call),
 *     the static scan will not see it. The narrow guard's structural
 *     "invocationArgs.push() must not reference a runner-path identifier" is
 *     the in-depth defense against the most realistic injection shape.
 *
 * Background:
 *   On Windows, jest-config's runner validator has been observed to reject
 *   absolute paths that `require.resolve("jest-circus/runner")` and
 *   `fs.existsSync` both report as valid. Re-introducing `--testRunner`
 *   anywhere in the invocation chain (CLI flag, env var, jest config block)
 *   reopens that failure surface. These policies make the regression loud at
 *   pre-push time.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const { stripJsCommentsAndStrings } = require("../lib/source-stripping");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

/**
 * Files that legitimately reference `--testRunner`, `JEST_TEST_RUNNER`,
 * or `JEST_CONFIG` because they document the policy, decode the failure
 * mode, forward a caller-supplied flag unchanged, or assert this exact
 * regression guard. Every other reference is forbidden.
 *
 * Paths are stored as repo-relative POSIX-style strings (`path.join`
 * canonicalizes per platform on read).
 */
const PERMITTED_PATHS = new Set([
  path.join("scripts", "run-managed-jest.js"),
  path.join("scripts", "__tests__", "run-managed-jest.test.js"),
  path.join("scripts", "__tests__", "run-managed-jest-no-injected-test-runner.test.js"),
  path.join("scripts", "__tests__", "no-testrunner-injection-policy.test.js"),
  path.join("scripts", "__tests__", "jest-error-decoder.test.js"),
  path.join("scripts", "lib", "jest-error-decoder.js"),
  // Note: scripts/lib/__tests__/source-stripping.test.js intentionally
  // omitted; the state-machine tokenizer correctly blanks the test's many
  // `--testRunner` string literals so Policy 7 sees no residue.
  path.join(".github", "workflows", "pre-commit-tooling-check.yml")
]);

const WALK_SKIP_DIRS = new Set(["node_modules", ".git", ".venv", "__pycache__", "Temp"]);

const JEST_CONFIG_CANDIDATES = [
  "jest.config.js",
  "jest.config.cjs",
  "jest.config.mjs",
  "jest.config.ts",
  "jest.config.json",
  ".jestrc",
  ".jestrc.json",
  ".jestrc.js"
];

const JEST_CONFIG_SEARCH_DIRS = ["", "scripts"];

const STRIP_FOR_EXTENSIONS = new Set([".js", ".cjs", ".mjs", ".ts"]);

const WORKFLOW_DIR = path.join(REPO_ROOT, ".github", "workflows");
const PRE_COMMIT_CONFIG_PATH = path.join(REPO_ROOT, ".pre-commit-config.yaml");
const PACKAGE_JSON_PATH = path.join(REPO_ROOT, "package.json");

function toRepoRelative(absolutePath) {
  return path.relative(REPO_ROOT, absolutePath);
}

function isPermitted(repoRelativePath) {
  return PERMITTED_PATHS.has(repoRelativePath);
}

function readUtf8(absolutePath) {
  return fs.readFileSync(absolutePath, "utf8");
}

function listFiles(absoluteDir, predicate) {
  const out = [];
  if (!fs.existsSync(absoluteDir)) {
    return out;
  }

  const stack = [absoluteDir];
  while (stack.length > 0) {
    const dir = stack.pop();
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch (error) {
      continue;
    }

    for (const entry of entries) {
      if (entry.isDirectory()) {
        if (WALK_SKIP_DIRS.has(entry.name)) {
          continue;
        }
        stack.push(path.join(dir, entry.name));
        continue;
      }

      if (!entry.isFile()) {
        continue;
      }

      const abs = path.join(dir, entry.name);
      if (predicate(abs)) {
        out.push(abs);
      }
    }
  }

  return out;
}

function formatViolationLines(violations) {
  return violations
    .map(({ file, line, snippet }) => {
      const where = line === null || line === undefined ? "" : `:${line}`;
      const text = snippet === undefined ? "" : `: ${snippet.trim()}`;
      return `  ${file}${where}${text}`;
    })
    .join("\n");
}

/**
 * Find every hook block in `.pre-commit-config.yaml`. Returns an array of
 * `{ idLine, idLineNumber, lines, blockStartLineNumber }` where `lines` is
 * the inclusive range from the hook's `- id:` line down to the line before
 * the next sibling hook (or end-of-file). We deliberately work line-by-line
 * because the file embeds folded scalars and bash one-liners that a YAML
 * parser would normalize away.
 */
function readPreCommitHookBlocks() {
  const rawLines = readUtf8(PRE_COMMIT_CONFIG_PATH).split("\n");
  const blocks = [];

  let current = null;
  for (let i = 0; i < rawLines.length; i++) {
    const line = rawLines[i];
    const idMatch = /^(\s*)-\s+id:\s*(.+?)\s*$/.exec(line);
    if (idMatch) {
      if (current) {
        blocks.push(current);
      }
      current = {
        indent: idMatch[1].length,
        id: idMatch[2].trim(),
        startLineNumber: i + 1,
        lines: [{ lineNumber: i + 1, text: line }]
      };
      continue;
    }

    if (!current) {
      continue;
    }

    // Stop appending to the current block when we hit a sibling or shallower
    // structural line that begins a new hook list element at the same depth.
    const siblingMatch = /^(\s*)-\s+id:\s*/.exec(line);
    if (siblingMatch && siblingMatch[1].length === current.indent) {
      blocks.push(current);
      current = {
        indent: siblingMatch[1].length,
        id: /^(\s*)-\s+id:\s*(.+?)\s*$/.exec(line)[2].trim(),
        startLineNumber: i + 1,
        lines: [{ lineNumber: i + 1, text: line }]
      };
      continue;
    }

    current.lines.push({ lineNumber: i + 1, text: line });
  }

  if (current) {
    blocks.push(current);
  }

  return blocks;
}

/**
 * Inspect a hook block and return all lines that mention the literal
 * `--testRunner`. Used for Policy 1.
 */
function findTestRunnerLinesInHookBlock(hookBlock) {
  return hookBlock.lines
    .filter(({ text }) => text.includes("--testRunner"))
    .map(({ lineNumber, text }) => ({
      file: ".pre-commit-config.yaml",
      line: lineNumber,
      snippet: text
    }));
}

/**
 * Inspect a hook block's `entry:` and `args:` content for `jest` indicators
 * (either `run-managed-jest.js` or the bare `jest` invocation token). The
 * hook must invoke Jest for Policy 1 to apply; pure-prettier or linter
 * hooks are out of scope.
 */
function hookInvokesJest(hookBlock) {
  const joined = hookBlock.lines.map((entry) => entry.text).join("\n");
  return /run-managed-jest\.js/.test(joined) || /\bjest\b/i.test(joined);
}

/**
 * Read a workflow file and return all `run:` block bodies along with the
 * line number where the body begins. Supports:
 *   - Single-line:  `run: echo hi`
 *   - Literal:      `run: |`         (followed by indented lines)
 *   - Folded:       `run: >-`        (followed by indented lines)
 *
 * The returned `body` is the full multi-line string of the block content.
 */
function extractWorkflowRunBlocks(workflowPath) {
  const rawText = readUtf8(workflowPath);
  const rawLines = rawText.split("\n");
  const blocks = [];

  for (let i = 0; i < rawLines.length; i++) {
    const line = rawLines[i];
    // Allow an optional leading list-element dash (`- run: ...`) which is
    // the canonical shape inside `steps:` blocks. Without this the
    // inline-run case missed every step body in `steps: - run: ...`
    // workflows.
    const inlineMatch = /^(\s*)(?:-\s+)?run:\s*(.+?)\s*$/.exec(line);
    const blockMatch = /^(\s*)(?:-\s+)?run:\s*([|>][+-]?)?\s*$/.exec(line);

    if (inlineMatch && !/^([|>])[+-]?$/.test(inlineMatch[2])) {
      blocks.push({
        file: workflowPath,
        startLine: i + 1,
        body: inlineMatch[2],
        bodyStartLine: i + 1,
        lines: [{ lineNumber: i + 1, text: inlineMatch[2] }]
      });
      continue;
    }

    if (blockMatch) {
      const indent = blockMatch[1].length;
      const bodyLines = [];
      let j = i + 1;
      while (j < rawLines.length) {
        const candidate = rawLines[j];
        if (candidate.trim() === "") {
          bodyLines.push({ lineNumber: j + 1, text: candidate });
          j++;
          continue;
        }
        const leading = candidate.length - candidate.trimStart().length;
        if (leading <= indent) {
          break;
        }
        bodyLines.push({ lineNumber: j + 1, text: candidate });
        j++;
      }
      blocks.push({
        file: workflowPath,
        startLine: i + 1,
        body: bodyLines.map((bl) => bl.text).join("\n"),
        bodyStartLine: i + 2,
        lines: bodyLines
      });
      i = j - 1;
    }
  }

  return blocks;
}

function listWorkflowFiles() {
  return listFiles(WORKFLOW_DIR, (abs) => /\.(yml|yaml)$/i.test(abs));
}

/**
 * Walk `--config <value>` and `--config=<value>` occurrences in a multi-line
 * string and return each value's text along with its 1-based line number.
 *
 * Handles quoted values: `--config="/abs/path"`, `--config '/abs/path'`,
 * `--config="/abs/path"`. Lines whose first non-whitespace character is `#`
 * are treated as YAML comments and skipped (avoids false positives on tips
 * like `# Hint: use --config /abs/path`).
 */
function findConfigFlagValues(text, baseLineNumber) {
  const violations = [];
  const lines = text.split("\n");
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    // Skip YAML comment lines so a doc-tip like `# use --config /abs`
    // does not produce a false-positive violation. We only skip lines
    // that begin with `#` after optional whitespace; inline comments
    // (`... # ...`) are not handled here because the policy intentionally
    // catches comment-disguised flag values when they appear after real
    // YAML content.
    if (/^\s*#/.test(line)) {
      continue;
    }

    // `--config=value` form, including quoted values:
    //   --config=foo
    //   --config="foo"
    //   --config='foo'
    // The capture group strips surrounding quotes from the value.
    const equalRe = /--config=(?:"([^"]*)"|'([^']*)'|([^\s'"`]+))/g;
    let match;
    while ((match = equalRe.exec(line)) !== null) {
      const value =
        match[1] !== undefined ? match[1] : match[2] !== undefined ? match[2] : match[3];
      violations.push({
        value,
        lineNumber: (baseLineNumber || 1) + i,
        snippet: line
      });
    }
    // `--config value` form (next whitespace-separated token), with
    // optional surrounding quotes.
    const spaceRe = /--config\s+(?:"([^"]*)"|'([^']*)'|([^\s'"`]+))/g;
    while ((match = spaceRe.exec(line)) !== null) {
      const value =
        match[1] !== undefined ? match[1] : match[2] !== undefined ? match[2] : match[3];
      if (typeof value !== "string" || value.length === 0) {
        continue;
      }
      violations.push({
        value,
        lineNumber: (baseLineNumber || 1) + i,
        snippet: line
      });
    }
  }
  return violations;
}

function isAbsoluteConfigPath(value) {
  if (typeof value !== "string" || value.length === 0) {
    return false;
  }
  if (value.startsWith("/")) {
    return true;
  }
  if (/^[A-Za-z]:[\\/]/.test(value)) {
    return true;
  }
  if (value.startsWith("\\\\")) {
    return true;
  }
  if (value.startsWith("~/") || value === "~") {
    return true;
  }
  if (value.startsWith("$HOME") || value.startsWith("${HOME}")) {
    return true;
  }
  return false;
}

/**
 * Recursively walk `scripts/` and return every `*.js` file (including tests).
 */
function listScriptJsFiles() {
  return listFiles(path.join(REPO_ROOT, "scripts"), (abs) => abs.endsWith(".js"));
}

function findLineNumbersContaining(text, needles) {
  const lines = text.split("\n");
  const hits = [];
  for (let i = 0; i < lines.length; i++) {
    for (const needle of needles) {
      if (lines[i].includes(needle)) {
        hits.push({ lineNumber: i + 1, snippet: lines[i], needle });
        break;
      }
    }
  }
  return hits;
}

/**
 * Walk a parsed JSON config tree and collect every path where the `testRunner`
 * key appears (e.g. `jest.testRunner`, `jest.projects[0].testRunner`,
 * `jest.overrides.unit.testRunner`). Returns an array of
 * `{ path, value }` records suitable for inclusion in an error message.
 *
 * The walk is bounded: it descends into plain objects and arrays only, and
 * skips primitive leaves. `__proto__`-style keys are not treated specially
 * because the input is parsed JSON, which yields plain own-property keys.
 */
function findTestRunnerKeysRecursive(node, currentPath) {
  const found = [];
  if (node === null || typeof node !== "object") {
    return found;
  }

  if (Array.isArray(node)) {
    for (let i = 0; i < node.length; i++) {
      const childPath = `${currentPath}[${i}]`;
      found.push(...findTestRunnerKeysRecursive(node[i], childPath));
    }
    return found;
  }

  for (const key of Object.keys(node)) {
    const value = node[key];
    const childPath = currentPath ? `${currentPath}.${key}` : key;
    if (key === "testRunner") {
      found.push({ path: childPath, value });
    }
    if (value !== null && typeof value === "object") {
      found.push(...findTestRunnerKeysRecursive(value, childPath));
    }
  }

  return found;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe("--testRunner injection policy (repo-wide)", () => {
  test("Policy 1: no '--testRunner' literal in pre-commit hook entry/args/bash bodies", () => {
    const blocks = readPreCommitHookBlocks();
    const offenders = [];

    for (const block of blocks) {
      if (!hookInvokesJest(block)) {
        continue;
      }

      const violations = findTestRunnerLinesInHookBlock(block);
      for (const v of violations) {
        offenders.push({
          file: ".pre-commit-config.yaml",
          line: v.line,
          snippet: `[hook=${block.id}] ${v.snippet}`
        });
      }
    }

    if (offenders.length > 0) {
      throw new Error(
        "Policy 1 violation: pre-commit hook(s) reference '--testRunner'.\n" +
          "Jest 27+ resolves jest-circus internally; injecting --testRunner " +
          "via a hook entry/args/bash body re-introduces the Windows runner " +
          "validator failure. Remove the flag.\n\nOffending lines:\n" +
          formatViolationLines(offenders)
      );
    }
  });

  test("Policy 2: no '--testRunner' literal in workflow run-block bodies", () => {
    const offenders = [];
    for (const workflowPath of listWorkflowFiles()) {
      const repoRelative = toRepoRelative(workflowPath);
      if (isPermitted(repoRelative)) {
        continue;
      }

      const blocks = extractWorkflowRunBlocks(workflowPath);
      for (const block of blocks) {
        for (const bl of block.lines) {
          if (bl.text.includes("--testRunner")) {
            offenders.push({
              file: repoRelative,
              line: bl.lineNumber,
              snippet: bl.text
            });
          }
        }
      }
    }

    if (offenders.length > 0) {
      throw new Error(
        "Policy 2 violation: workflow run-block(s) reference '--testRunner'.\n" +
          "Workflows must invoke jest via scripts/run-managed-jest.js without " +
          "the --testRunner flag. Remove the flag.\n\nOffending lines:\n" +
          formatViolationLines(offenders)
      );
    }
  });

  test("Policy 3: package.json must not configure jest.testRunner (recursively)", () => {
    const pkg = JSON.parse(readUtf8(PACKAGE_JSON_PATH));
    const jestField = pkg && typeof pkg === "object" ? pkg.jest : undefined;

    if (jestField === undefined || jestField === null || typeof jestField !== "object") {
      return;
    }

    // Recursive: catches `jest.testRunner`, `jest.projects[*].testRunner`,
    // and any future nested shape Jest may accept.
    const hits = findTestRunnerKeysRecursive(jestField, "jest");

    if (hits.length > 0) {
      const formatted = hits.map((h) => `  ${h.path} = ${JSON.stringify(h.value)}`).join("\n");
      throw new Error(
        "Policy 3 violation: package.json sets testRunner under the " +
          "jest field (possibly nested under projects[*] or similar).\n" +
          "Jest 27+ defaults to jest-circus; do not configure testRunner.\n\n" +
          "Offending keys:\n" +
          formatted
      );
    }
  });

  test("Policy 4: no jest config file sets a testRunner key", () => {
    const offenders = [];

    for (const dir of JEST_CONFIG_SEARCH_DIRS) {
      for (const name of JEST_CONFIG_CANDIDATES) {
        const abs = path.join(REPO_ROOT, dir, name);
        if (!fs.existsSync(abs)) {
          continue;
        }
        const repoRelative = toRepoRelative(abs);
        const ext = path.extname(abs).toLowerCase();
        const raw = readUtf8(abs);

        if (ext === ".json" || name === ".jestrc") {
          let parsed;
          try {
            parsed = JSON.parse(raw);
          } catch (error) {
            // Treat parse failure as a soft offense: a broken
            // config blocks Jest entirely; still report cleanly.
            offenders.push({
              file: repoRelative,
              line: null,
              snippet: `JSON parse failed: ${error.message}`
            });
            continue;
          }
          if (parsed !== null && typeof parsed === "object") {
            const hits = findTestRunnerKeysRecursive(parsed, "");
            for (const hit of hits) {
              offenders.push({
                file: repoRelative,
                line: null,
                snippet: `${hit.path} = ${JSON.stringify(hit.value)}`
              });
            }
          }
          continue;
        }

        let scanText = raw;
        if (STRIP_FOR_EXTENSIONS.has(ext)) {
          scanText = stripJsCommentsAndStrings(raw);
        }

        const lines = scanText.split("\n");
        for (let i = 0; i < lines.length; i++) {
          if (/\btestRunner\b/.test(lines[i])) {
            offenders.push({
              file: repoRelative,
              line: i + 1,
              snippet: lines[i]
            });
          }
        }
      }
    }

    if (offenders.length > 0) {
      throw new Error(
        "Policy 4 violation: jest config file(s) set 'testRunner'.\n" +
          "Remove the testRunner key; Jest 27+ resolves jest-circus " +
          "internally.\n\nOffending entries:\n" +
          formatViolationLines(offenders)
      );
    }
  });

  test("Policy 5: no absolute '--config' paths in hooks or workflows", () => {
    const offenders = [];

    // .pre-commit-config.yaml — scan every line; --config in hooks should
    // be relative to the repo root.
    const preCommitText = readUtf8(PRE_COMMIT_CONFIG_PATH);
    const preCommitConfigHits = findConfigFlagValues(preCommitText, 1);
    for (const hit of preCommitConfigHits) {
      if (isAbsoluteConfigPath(hit.value)) {
        offenders.push({
          file: ".pre-commit-config.yaml",
          line: hit.lineNumber,
          snippet: hit.snippet
        });
      }
    }

    // Workflow files — same scan.
    for (const workflowPath of listWorkflowFiles()) {
      const repoRelative = toRepoRelative(workflowPath);
      if (isPermitted(repoRelative)) {
        continue;
      }
      const text = readUtf8(workflowPath);
      const hits = findConfigFlagValues(text, 1);
      for (const hit of hits) {
        if (isAbsoluteConfigPath(hit.value)) {
          offenders.push({
            file: repoRelative,
            line: hit.lineNumber,
            snippet: hit.snippet
          });
        }
      }
    }

    if (offenders.length > 0) {
      throw new Error(
        "Policy 5 violation: absolute '--config <path>' detected.\n" +
          "Absolute config paths break cross-platform reproducibility and " +
          "are a vector for the --testRunner injection footgun. Use a " +
          "repo-relative path.\n\nOffending lines:\n" +
          formatViolationLines(offenders)
      );
    }
  });

  test("Policy 6: no env-driven 'JEST_TEST_RUNNER' or 'JEST_CONFIG' references outside allow-list", () => {
    const offenders = [];
    const needles = ["JEST_TEST_RUNNER", "JEST_CONFIG"];

    // scripts/**/*.js: scan BOTH stripped and unstripped source.
    //
    // Stripped scan catches direct references in code
    //   (e.g. `process.env.JEST_TEST_RUNNER = "..."`).
    // Unstripped fallback catches variable-assignment indirection
    //   (e.g. `const k = "JEST_TEST_RUNNER"; process.env[k] = "...";`).
    // The fallback may produce false positives in legitimate string-
    // handling code; allow-list any such file via PERMITTED_PATHS.
    for (const abs of listScriptJsFiles()) {
      const repoRelative = toRepoRelative(abs);
      if (isPermitted(repoRelative)) {
        continue;
      }
      const raw = readUtf8(abs);
      const stripped = stripJsCommentsAndStrings(raw);
      const seen = new Set();

      const strippedHits = findLineNumbersContaining(stripped, needles);
      for (const hit of strippedHits) {
        const key = `${hit.lineNumber}::${hit.needle}`;
        if (seen.has(key)) {
          continue;
        }
        seen.add(key);
        offenders.push({
          file: repoRelative,
          line: hit.lineNumber,
          snippet: hit.snippet
        });
      }

      // Coarse fallback: catches the string even when assigned to a
      // variable; may produce false positives in legitimate string-
      // handling code; allow-list any such file via PERMITTED_PATHS.
      const rawHits = findLineNumbersContaining(raw, needles);
      for (const hit of rawHits) {
        const key = `${hit.lineNumber}::${hit.needle}`;
        if (seen.has(key)) {
          continue;
        }
        seen.add(key);
        offenders.push({
          file: repoRelative,
          line: hit.lineNumber,
          snippet: hit.snippet
        });
      }
    }

    // .pre-commit-config.yaml — search raw text (YAML has no code/comment
    // distinction we care about here; any literal occurrence is a vector).
    const preCommitHits = findLineNumbersContaining(readUtf8(PRE_COMMIT_CONFIG_PATH), needles);
    for (const hit of preCommitHits) {
      offenders.push({
        file: ".pre-commit-config.yaml",
        line: hit.lineNumber,
        snippet: hit.snippet
      });
    }

    // .github/workflows/*.{yml,yaml}
    for (const workflowPath of listWorkflowFiles()) {
      const repoRelative = toRepoRelative(workflowPath);
      if (isPermitted(repoRelative)) {
        continue;
      }
      const hits = findLineNumbersContaining(readUtf8(workflowPath), needles);
      for (const hit of hits) {
        offenders.push({
          file: repoRelative,
          line: hit.lineNumber,
          snippet: hit.snippet
        });
      }
    }

    if (offenders.length > 0) {
      throw new Error(
        "Policy 6 violation: env-driven JEST_TEST_RUNNER / JEST_CONFIG " +
          "references detected outside the allow-list.\n" +
          "These env vars are the back-door equivalent of the --testRunner " +
          "flag and re-introduce the same Windows runner-validator failure.\n\n" +
          "Offending lines:\n" +
          formatViolationLines(offenders)
      );
    }
  });

  test("Policy 7: no '--testRunner' literal in any scripts/**/*.js after stripping", () => {
    const offenders = [];

    for (const abs of listScriptJsFiles()) {
      const repoRelative = toRepoRelative(abs);
      if (isPermitted(repoRelative)) {
        continue;
      }
      const stripped = stripJsCommentsAndStrings(readUtf8(abs));
      const lines = stripped.split("\n");
      for (let i = 0; i < lines.length; i++) {
        if (lines[i].includes("--testRunner")) {
          offenders.push({
            file: repoRelative,
            line: i + 1,
            snippet: lines[i]
          });
        }
      }
    }

    if (offenders.length > 0) {
      throw new Error(
        "Policy 7 violation: '--testRunner' literal detected in script " +
          "source after stripping comments and string literals.\n" +
          "Forward caller-supplied --testRunner via the input args array; " +
          "do not embed the flag in any wrapper or helper.\n\n" +
          "Offending lines:\n" +
          formatViolationLines(offenders)
      );
    }
  });

  test("Policy 8: PERMITTED_PATHS sanity — every allow-list entry resolves to an existing file", () => {
    const missing = [];
    for (const repoRelative of PERMITTED_PATHS) {
      const abs = path.join(REPO_ROOT, repoRelative);
      if (!fs.existsSync(abs)) {
        missing.push(repoRelative);
      }
    }

    if (missing.length > 0) {
      throw new Error(
        "Policy 8 violation: PERMITTED_PATHS contains entries that do " +
          "not point to existing files. Either delete the dead entry or " +
          "add an inline comment noting 'allow-list slot for future file' " +
          "immediately above the path in this test file.\n\nMissing entries:\n" +
          missing.map((p) => `  ${p}`).join("\n")
      );
    }
  });
});
