/**
 * @fileoverview Self-maintaining completeness guard for the cross-OS targeted
 * regression gate in .github/workflows/cross-platform-preflight.yml.
 *
 * WHY THIS EXISTS: that workflow runs a FAST, FIRST step
 * ("Run cross-platform spawn + host-env hermeticity regression suite") that
 * executes a hand-listed set of Jest files via
 * `node scripts/run-managed-jest.js --runTestsByPath <files>` on
 * ubuntu-latest + windows-latest + macos-latest. A regression in one of those
 * files then fails FAST with clear attribution instead of deep inside the long
 * full-preflight log. The list is hand-maintained, so it can rot two ways:
 *   (a) a new cross-platform regression test is added but never wired into the
 *       targeted step, so a Windows/macOS regression in it only surfaces (if at
 *       all) inside the full preflight; and
 *   (b) a listed path is renamed/removed/typo'd, leaving a stale entry that
 *       silently runs nothing.
 *
 * The fix is a MARKER, not a duplicate list. A test opts into the cross-OS
 * targeted gate by placing the literal token `@cross-platform-regression`
 * inside a COMMENT anywhere in its file (conventionally the file header). This
 * guard:
 *   1. Scans every scripts/**\/__tests__/*.test.js (both scripts/__tests__ and
 *      scripts/lib/__tests__) for the marker (only when it appears in a
 *      comment, never in a string), then asserts each marked file's
 *      repo-relative POSIX path appears inside the targeted step's
 *      `--runTestsByPath` block. (closes gap (a))
 *   2. Asserts every path listed in that block EXISTS on disk and is itself
 *      marked, so the list and the markers stay in sync both directions.
 *      (closes gap (b))
 *   3. Asserts the workflow's job matrix still contains ubuntu-latest AND
 *      windows-latest AND macos-latest, so an OS cannot be silently dropped
 *      from the cross-OS gate.
 *
 * The marker set is INTENTIONALLY curated to the fast-attribution subset, NOT
 * "every cross-platform test". The second workflow step (`npm run
 * preflight:pre-push`, the "Run pre-push preflight" step) already runs the
 * ENTIRE Jest suite on all three OSes, so an unmarked platform-divergent test
 * still gets full cross-OS coverage there. The marker only PROMOTES a test into
 * the first, targeted step so a regression in it fails fast with clear
 * attribution instead of deep inside the long combined log.
 *
 * Robustness: Node stdlib only, no shell-outs. Files are read CRLF/BOM-safe
 * (normalizeToLf + a BOM strip) and compared via POSIX-normalized
 * repo-relative paths. The marker is matched ONLY inside real comment SPANS via
 * `extractCommentsOnly` -- the inverse projection of stripJsCommentsAndStrings
 * that shares the SAME single-pass tokenizer: it preserves comment payloads
 * (line / block / JSDoc, multi-line, with OR without a leading `*`) verbatim and
 * blanks code + string/template payloads to spaces. A marker that survives that
 * projection lived in a genuine comment, never in a string (so `"a // b
 * @marker"` is correctly NOT a marker) and never on a starless block-comment
 * continuation line that an old per-line heuristic would have missed. All
 * regexes are literal-substring / linear (ReDoS-free). THIS guard file is
 * allow-listed so its own definition/self-test fixtures of the marker token do
 * not count as an opt-in.
 *
 * How to mutation-test this guard (do this when you touch it):
 *   1. Remove one listed path from the targeted step in
 *      cross-platform-preflight.yml -> this suite FAILS naming that file ->
 *      restore -> GREEN.
 *   2. Add the marker to a scratch fixture string fed to the pure detector (the
 *      self-tests below already do this) or remove an OS from the matrix
 *      in-place -> the matrix assertion FAILS -> restore -> GREEN.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { normalizeToLf } = require("../lib/quote-parser");
const { extractCommentsOnly } = require("../lib/source-stripping");
const { readUtf8, walkFiles } = require("../lib/repo-files");
const {
  TARGETED_STEP_NAME,
  extractTargetedStepRunBlock,
  extractListedTestPaths
} = require("../lib/cross-platform-preflight-gate");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SCRIPTS_ROOT = path.join(REPO_ROOT, "scripts");
const WORKFLOW_PATH = path.join(REPO_ROOT, ".github", "workflows", "cross-platform-preflight.yml");

// The opt-in token. A cross-platform regression test declares itself part of
// the cross-OS targeted gate by placing this literal inside a comment anywhere
// in the file (conventionally the header). Detection is comment-SPAN based, so
// any real comment context counts and string/code occurrences never do.
const MARKER = "@cross-platform-regression";

// The OSes the cross-OS gate must keep covering.
const REQUIRED_MATRIX_OSES = ["ubuntu-latest", "windows-latest", "macos-latest"];

const WALK_SKIP_DIRS = new Set(["node_modules", ".git", ".venv", "__pycache__", "Temp"]);

// THIS guard file references the marker in its own JSDoc/self-tests as data, so
// it must not count as a real opt-in. Allow-listed by repo-relative POSIX path.
// cross-toolchain-path-comparison-policy.test.js likewise documents the marker
// token in its header/error messages as DATA while being a pure static scan
// (no shell spawn, no platform divergence), so it is NOT promoted onto the
// cross-OS gate either.
const ALLOW_LIST = new Set([
  "scripts/__tests__/cross-platform-preflight-coverage.test.js",
  "scripts/__tests__/cross-toolchain-path-comparison-policy.test.js"
]);

function toRepoPosixRelative(absolutePath) {
  return path.relative(REPO_ROOT, absolutePath).split(path.sep).join("/");
}

function listTestFiles() {
  return walkFiles(SCRIPTS_ROOT, {
    match: (abs) => {
      if (!abs.endsWith(".test.js")) {
        return false;
      }
      return path.relative(SCRIPTS_ROOT, abs).split(path.sep).includes("__tests__");
    },
    excludeDir: (abs, entry) => WALK_SKIP_DIRS.has(entry.name)
  });
}

/**
 * Pure detector: is the marker present in this source inside a real COMMENT
 * span (anywhere in the file -- not in a string, template literal, or code)?
 *
 * Comment-SPAN detection, not a per-line heuristic. We project the source
 * through `extractCommentsOnly` (the inverse of `stripJsCommentsAndStrings`,
 * sharing the SAME single-pass tokenizer): comment payloads survive verbatim
 * while code and string/template payloads are blanked to spaces, with line
 * breaks preserved. A marker that appears in that projection therefore lived in
 * a genuine comment span -- line (`//`), block (`/* ... *\/`, multi-line, with
 * OR without a leading `*`), or JSDoc -- and NEVER in a string/template literal
 * or code. This closes both per-line failure modes the old heuristic had:
 *   - false NEGATIVE: a marker on a plain block-comment continuation line with
 *     no leading `*` and no `/*`/`//` on that line is now detected, because the
 *     tokenizer knows it is still inside the open block-comment span; and
 *   - false POSITIVE: a marker inside a string such as `"a // b @marker"` is
 *     NOT detected, because `//` inside a string is string content (blanked),
 *     not a comment opener.
 * Reusing the shared tokenizer means the marked-in-comment and stripped-in-code
 * views can never disagree about what a comment is. BOM is stripped and CRLF is
 * normalized to LF by the caller (`readUtf8`), so the projection is
 * BOM/CRLF-safe by construction.
 *
 * @param {string} rawSource - LF-normalized, BOM-stripped source.
 * @returns {boolean} True when the marker appears inside a comment span.
 */
function sourceHasMarkerInComment(rawSource) {
  if (typeof rawSource !== "string" || rawSource.indexOf(MARKER) === -1) {
    return false;
  }
  return extractCommentsOnly(rawSource).indexOf(MARKER) !== -1;
}

/**
 * Strip a YAML trailing line comment (` # ...`) while ignoring `#` inside
 * single/double quotes. Linear scan, ReDoS-free.
 *
 * @param {string} text
 * @returns {string}
 */
function stripYamlInlineComment(text) {
  let quote = null;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (quote) {
      if (ch === quote) {
        quote = null;
      }
      continue;
    }
    if (ch === '"' || ch === "'") {
      quote = ch;
      continue;
    }
    if (ch === "#") {
      // A `#` starts a comment only when preceded by whitespace or at line
      // start (YAML rule), which is always true for our trailing-comment case.
      if (i === 0 || /\s/.test(text[i - 1])) {
        return text.slice(0, i);
      }
    }
  }
  return text;
}

function unquoteYamlScalar(value) {
  const v = value.trim();
  if ((v.startsWith('"') && v.endsWith('"')) || (v.startsWith("'") && v.endsWith("'"))) {
    return v.slice(1, -1);
  }
  return v;
}

/**
 * Extract the matrix OS entries from the workflow. Scoped to a matrix block
 * (an `os:` key nested under `strategy:` -> `matrix:`) so an unrelated `os:`
 * elsewhere in the file cannot merge in. Handles BOTH YAML forms:
 *   - block sequence:  `os:` then indented `- ubuntu-latest` lines
 *   - flow sequence:   `os: [ubuntu-latest, windows-latest, macos-latest]`
 * Trailing `# comments` are ignored in both forms. Errs safe: if no matrix-
 * scoped `os:` is found the result is empty, which makes the OS-coverage
 * assertion RED (a missing/renamed matrix is a failure, not a silent pass).
 *
 * @param {string} rawWorkflow - LF-normalized workflow source.
 * @returns {Set<string>}
 */
function extractMatrixOses(rawWorkflow) {
  const lines = rawWorkflow.split("\n");
  const oses = new Set();

  // Track whether we are inside a `strategy:` -> `matrix:` scope so a stray
  // top-level/other `os:` cannot pollute the set. matrixIndent is the indent of
  // the `matrix:` key; its children are more deeply indented.
  let matrixIndent = -1;
  let inOsBlock = false;
  let osIndent = -1;

  const indentOf = (line) => line.length - line.trimStart().length;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const indent = indentOf(line);
    const trimmedFull = line.trim();
    if (trimmedFull.length === 0 || trimmedFull.startsWith("#")) {
      continue;
    }
    const trimmed = stripYamlInlineComment(line).trim();

    // Enter a matrix scope on `matrix:` (it lives under `strategy:`; we anchor
    // on `matrix:` directly which is sufficient and robust to formatting).
    if (/^matrix:\s*$/.test(trimmed) || /^matrix:\s*#/.test(trimmedFull)) {
      matrixIndent = indent;
      inOsBlock = false;
      osIndent = -1;
      continue;
    }

    // Leaving the matrix scope: a key at or below matrixIndent that is not a
    // matrix child closes it (unless we are still consuming an os block).
    if (matrixIndent !== -1 && indent <= matrixIndent && !inOsBlock) {
      matrixIndent = -1;
    }

    if (matrixIndent === -1) {
      continue;
    }

    if (!inOsBlock) {
      // Flow form: `os: [a, b, c]`
      const flow = /^os:\s*\[(.*)\]\s*$/.exec(trimmed);
      if (flow) {
        for (const part of flow[1].split(",")) {
          const value = unquoteYamlScalar(part);
          if (value.length > 0) {
            oses.add(value);
          }
        }
        continue;
      }
      // Block form: bare `os:` then `- <name>` children.
      if (/^os:\s*$/.test(trimmed)) {
        inOsBlock = true;
        osIndent = indent;
      }
      continue;
    }

    // Inside a block-form os list: collect more-indented `- name` entries.
    if (indent > osIndent && /^-\s+/.test(trimmed)) {
      const value = unquoteYamlScalar(trimmed.replace(/^-\s+/, ""));
      if (value.length > 0) {
        oses.add(value);
      }
      continue;
    }

    // Dedent at/below the `os:` key closes the block. Re-evaluate this line for
    // matrix-scope exit on the next iteration by stepping back one index.
    if (indent <= osIndent) {
      inOsBlock = false;
      osIndent = -1;
      i--;
    }
  }

  return oses;
}

// ---------------------------------------------------------------------------
// Shared fixtures: read the workflow + scan the marked files ONCE.
// ---------------------------------------------------------------------------
const workflowSource = readUtf8(WORKFLOW_PATH);
const targetedRunBlock = extractTargetedStepRunBlock(workflowSource, TARGETED_STEP_NAME);

// Parse the path list defensively: a malformed/truncated continuation makes
// `extractListedTestPaths` throw (Finding 3). Capture that here so it surfaces
// as a deterministic, named TEST FAILURE rather than crashing the whole file at
// module-load time (which would report "0 tests" with no actionable assertion).
let listedPaths = [];
let listedParseError = null;
try {
  listedPaths = extractListedTestPaths(targetedRunBlock || "");
} catch (err) {
  listedParseError = err;
}
const listedSet = new Set(listedPaths);

function collectMarkedTestPaths() {
  const marked = [];
  for (const abs of listTestFiles()) {
    const rel = toRepoPosixRelative(abs);
    if (ALLOW_LIST.has(rel)) {
      continue;
    }
    const raw = readUtf8(abs);
    if (sourceHasMarkerInComment(raw)) {
      marked.push(rel);
    }
  }
  return marked;
}

// Guard the suite registration so the module can be `require()`d for its pure
// helpers OUTSIDE Jest (where `describe`/`test`/`expect` are undefined) without
// throwing at load time. Under Jest this runs exactly as before. (Finding 7)
// We register the suite via `maybeDescribe`, which is the real `describe` under
// Jest and a no-op otherwise, so the body's indentation is unchanged.
const maybeDescribe = typeof describe === "function" ? describe : () => {};

maybeDescribe("cross-platform-preflight targeted-gate coverage", () => {
  test("the targeted regression step exists and parses to a non-empty list", () => {
    if (listedParseError) {
      throw listedParseError;
    }
    expect(targetedRunBlock).not.toBeNull();
    expect(listedPaths.length).toBeGreaterThan(0);
  });

  test("every @cross-platform-regression-marked test is in the targeted step", () => {
    const marked = collectMarkedTestPaths();
    expect(marked.length).toBeGreaterThan(0);

    const missing = marked.filter((rel) => !listedSet.has(rel));
    if (missing.length > 0) {
      const details = missing.map((rel) => `  ${rel}`).join("\n");
      throw new Error(
        "Marked cross-platform regression test(s) are not gated on win+mac:\n" +
          details +
          "\nAdd each to the targeted regression step in " +
          ".github/workflows/cross-platform-preflight.yml " +
          `(the "${TARGETED_STEP_NAME}" step's --runTestsByPath list).`
      );
    }
  });

  test("every test listed in the targeted step exists on disk", () => {
    const missingOnDisk = listedPaths.filter((rel) => !fs.existsSync(path.join(REPO_ROOT, rel)));
    if (missingOnDisk.length > 0) {
      const details = missingOnDisk.map((rel) => `  ${rel}`).join("\n");
      throw new Error(
        "Stale or mistyped entries in the targeted regression step of " +
          ".github/workflows/cross-platform-preflight.yml -- these paths do not " +
          "exist on disk:\n" +
          details
      );
    }
  });

  test("every test listed in the targeted step carries the marker", () => {
    // Bidirectional sync: a path in the list that lost its marker (or was added
    // without one) is a drift signal -- the list and the markers must agree.
    const marked = new Set(collectMarkedTestPaths());
    const unmarked = listedPaths.filter((rel) => !marked.has(rel));
    if (unmarked.length > 0) {
      const details = unmarked.map((rel) => `  ${rel}`).join("\n");
      throw new Error(
        "Test(s) listed in the targeted regression step of " +
          ".github/workflows/cross-platform-preflight.yml are missing the " +
          `${MARKER} marker comment:\n` +
          details +
          `\nAdd a header comment containing ${MARKER} so the list and the ` +
          "markers stay in sync."
      );
    }
  });

  test("the cross-OS matrix still covers ubuntu, windows, and macos", () => {
    const oses = extractMatrixOses(workflowSource);
    for (const required of REQUIRED_MATRIX_OSES) {
      expect(oses.has(required)).toBe(true);
    }
  });

  // -------------------------------------------------------------------------
  // Self-tests: prove the pure detectors fire on the exact shapes and do NOT
  // false-positive, WITHOUT writing real repo files.
  // -------------------------------------------------------------------------
  describe("detector self-tests", () => {
    // -- sourceHasMarkerInComment: comment-SPAN detection (Findings 1 & 2) ----
    test("flags a JSDoc-body marker (leading star)", () => {
      const src = `/**\n * ${MARKER}: gated cross-OS.\n */\n"use strict";\n`;
      expect(sourceHasMarkerInComment(src)).toBe(true);
    });

    test("flags a // line-comment marker", () => {
      const src = `"use strict";\n// ${MARKER} -- runs on win+mac\nconst x = 1;\n`;
      expect(sourceHasMarkerInComment(src)).toBe(true);
    });

    test("flags a marker on a PLAIN block-comment continuation line (no leading star) -- Finding 1", () => {
      // The old per-line heuristic missed this and silently treated the file
      // as unmarked, letting it escape the gate.
      const src = `/*\n   ${MARKER}\n*/\n"use strict";\n`;
      expect(sourceHasMarkerInComment(src)).toBe(true);
    });

    test("flags a marker in INDENTED block-comment prose (no leading star)", () => {
      const src = `/*\n      see ${MARKER} for the gated set\n*/\n`;
      expect(sourceHasMarkerInComment(src)).toBe(true);
    });

    test("does NOT flag the marker inside a double/single/template string", () => {
      expect(sourceHasMarkerInComment(`const a = "${MARKER}";\n`)).toBe(false);
      expect(sourceHasMarkerInComment(`const a = '${MARKER}';\n`)).toBe(false);
      expect(sourceHasMarkerInComment(`const a = \`${MARKER}\`;\n`)).toBe(false);
    });

    test("does NOT flag a marker in a string that contains `//` or `/*` -- Finding 2", () => {
      // The old per-line heuristic saw the `//` / `/*` earlier in the string
      // and wrongly counted these as comment markers.
      expect(sourceHasMarkerInComment(`const u = "a // b ${MARKER}";\n`)).toBe(false);
      expect(sourceHasMarkerInComment(`const u = "a /* b ${MARKER}";\n`)).toBe(false);
    });

    test("does NOT flag the marker as a bare code identifier", () => {
      // `@cross-platform-regression` is not a valid bare identifier, but the
      // detector must still reject any non-comment occurrence; emulate a code
      // hit by checking a marker spliced into code via a property-ish token.
      const src = `const obj = { x: 1 }; obj["${MARKER}"];\n`;
      expect(sourceHasMarkerInComment(src)).toBe(false);
    });

    test("does NOT flag a file without the marker", () => {
      const src = `/**\n * @fileoverview Some unrelated test.\n */\n`;
      expect(sourceHasMarkerInComment(src)).toBe(false);
    });

    test("is BOM + CRLF safe (block comment with CRLF and a BOM)", () => {
      // readUtf8 normalizes CRLF -> LF and strips the BOM; emulate that here.
      const src = normalizeToLf(`﻿/*\r\n   ${MARKER}\r\n*/\r\n`).replace(/^﻿/, "");
      expect(sourceHasMarkerInComment(src)).toBe(true);
    });

    // -- existing desync directions ------------------------------------------
    test("a marked-but-unlisted file is detected by the listed-set check", () => {
      const marked = ["scripts/__tests__/imaginary-new-regression.test.js"];
      const missing = marked.filter((rel) => !listedSet.has(rel));
      expect(missing).toEqual(["scripts/__tests__/imaginary-new-regression.test.js"]);
    });

    test("a listed-but-missing-file is detected by the on-disk check", () => {
      const fakeListed = ["scripts/__tests__/does-not-exist.test.js"];
      const missingOnDisk = fakeListed.filter((rel) => !fs.existsSync(path.join(REPO_ROOT, rel)));
      expect(missingOnDisk).toEqual(["scripts/__tests__/does-not-exist.test.js"]);
    });

    // -- extractListedTestPaths: strict continuation parsing (Finding 3) ------
    test("happy path: contiguous backslash-continued list parses cleanly", () => {
      const block =
        "          set -euo pipefail\n" +
        "          node scripts/run-managed-jest.js --runTestsByPath \\\n" +
        "            scripts/__tests__/a.test.js \\\n" +
        "            scripts/lib/__tests__/b.test.js";
      expect(extractListedTestPaths(block)).toEqual([
        "scripts/__tests__/a.test.js",
        "scripts/lib/__tests__/b.test.js"
      ]);
    });

    test("happy path: PowerShell array splat list parses cleanly", () => {
      const block =
        "          $tests = @(\n" +
        '            "scripts/__tests__/a.test.js"\n' +
        '            "scripts/lib/__tests__/b.test.js"\n' +
        "          )\n" +
        "          node scripts/run-managed-jest.js --runTestsByPath @tests";
      expect(extractListedTestPaths(block)).toEqual([
        "scripts/__tests__/a.test.js",
        "scripts/lib/__tests__/b.test.js"
      ]);
    });

    test("an unresolved PowerShell splat is detected before the hook backstop", () => {
      const block = "          node scripts/run-managed-jest.js --runTestsByPath @tests";
      expect(() => extractListedTestPaths(block)).toThrow(/Unresolved PowerShell array splat/);
    });

    test("a PowerShell splat declared after the Jest command is unresolved", () => {
      const block =
        "          node scripts/run-managed-jest.js --runTestsByPath @tests\n" +
        "          $tests = @(\n" +
        '            "scripts/__tests__/a.test.js"\n' +
        "          )";
      expect(() => extractListedTestPaths(block)).toThrow(/Unresolved PowerShell array splat/);
    });

    test("an empty PowerShell splat is detected before the hook backstop", () => {
      const block =
        "          $tests = @(\n" +
        "          )\n" +
        "          node scripts/run-managed-jest.js --runTestsByPath @tests";
      expect(() => extractListedTestPaths(block)).toThrow(/Empty PowerShell test array/);
    });

    test("a DROPPED continuation backslash (truncation) is detected -- Finding 3", () => {
      const block =
        "          node scripts/run-managed-jest.js --runTestsByPath \\\n" +
        "            scripts/__tests__/a.test.js\n" + // <-- backslash dropped
        "            scripts/__tests__/b.test.js \\\n" +
        "            scripts/__tests__/c.test.js";
      expect(() => extractListedTestPaths(block)).toThrow(/Truncated --runTestsByPath/);
    });

    test("a TRAILING SPACE after a continuation backslash is a hard error -- Finding 3", () => {
      // Documented design: `\ ` (backslash + trailing space) is an escaped
      // space in bash, NOT a line continuation -> we throw, matching CI.
      const block =
        "          node scripts/run-managed-jest.js --runTestsByPath \\ \n" +
        "            scripts/__tests__/a.test.js";
      expect(() => extractListedTestPaths(block)).toThrow(/Malformed line continuation/);
    });

    // -- extractMatrixOses: block + flow + comments + scoping (Finding 4) -----
    test("parses the BLOCK-sequence os list", () => {
      const wf =
        "    strategy:\n" +
        "      matrix:\n" +
        "        os:\n" +
        "          - ubuntu-latest\n" +
        "          - windows-latest\n" +
        "          - macos-latest\n" +
        "    steps:\n";
      const oses = extractMatrixOses(wf);
      for (const required of REQUIRED_MATRIX_OSES) {
        expect(oses.has(required)).toBe(true);
      }
    });

    test("parses the FLOW-sequence os list", () => {
      const wf =
        "    strategy:\n" +
        "      matrix:\n" +
        "        os: [ubuntu-latest, windows-latest, macos-latest]\n" +
        "    steps:\n";
      const oses = extractMatrixOses(wf);
      for (const required of REQUIRED_MATRIX_OSES) {
        expect(oses.has(required)).toBe(true);
      }
    });

    test("ignores a trailing comment on the os line and entries", () => {
      const wf =
        "    strategy:\n" +
        "      matrix:\n" +
        "        os: # the cross-OS gate\n" +
        "          - ubuntu-latest\n" +
        "          - windows-latest # win-only regressions\n" +
        "          - macos-latest\n" +
        "    steps:\n";
      const oses = extractMatrixOses(wf);
      for (const required of REQUIRED_MATRIX_OSES) {
        expect(oses.has(required)).toBe(true);
      }
    });

    test("an UNRELATED os: outside the matrix scope is NOT merged in", () => {
      const wf =
        "env:\n" +
        "  os: some-unrelated-value\n" +
        "    strategy:\n" +
        "      matrix:\n" +
        "        os:\n" +
        "          - ubuntu-latest\n" +
        "          - windows-latest\n" +
        "          - macos-latest\n";
      const oses = extractMatrixOses(wf);
      expect(oses.has("some-unrelated-value")).toBe(false);
      for (const required of REQUIRED_MATRIX_OSES) {
        expect(oses.has(required)).toBe(true);
      }
    });

    test("each required OS removed -> the matrix assertion goes RED (block form)", () => {
      for (const dropped of REQUIRED_MATRIX_OSES) {
        const kept = REQUIRED_MATRIX_OSES.filter((o) => o !== dropped);
        const wf =
          "    strategy:\n" +
          "      matrix:\n" +
          "        os:\n" +
          kept.map((o) => `          - ${o}\n`).join("");
        const oses = extractMatrixOses(wf);
        expect(oses.has(dropped)).toBe(false);
      }
    });

    test("each required OS removed -> the matrix assertion goes RED (flow form)", () => {
      for (const dropped of REQUIRED_MATRIX_OSES) {
        const kept = REQUIRED_MATRIX_OSES.filter((o) => o !== dropped);
        const wf = "    strategy:\n" + "      matrix:\n" + `        os: [${kept.join(", ")}]\n`;
        const oses = extractMatrixOses(wf);
        expect(oses.has(dropped)).toBe(false);
      }
    });
  });
});

if (typeof module !== "undefined" && module.exports) {
  module.exports = {
    MARKER,
    TARGETED_STEP_NAME,
    REQUIRED_MATRIX_OSES,
    sourceHasMarkerInComment,
    extractTargetedStepRunBlock,
    extractListedTestPaths,
    extractMatrixOses
  };
}
