/**
 * @fileoverview Repository-wide policy guard that closes the idiomatic
 * reintroduction paths for TWO platform-dependent git-hook anti-patterns. Both
 * bit a real pre-push parity sweep on Windows (repo on D:\, OS temp on C:\) while
 * staying invisible on the Linux devcontainer/CI. CATEGORY A covers every shape
 * present in the tree -- the declarator-bound `rel` (`const|let|var rel =
 * path.relative(...)`) and the inline `path.relative(...).startsWith("..")` chain;
 * one non-idiomatic shape (a declarator-LESS reassignment, `let rel; rel =
 * path.relative(...)`) is a known, self-test-pinned boundary (see the "coverage
 * boundary" self-test) rather than a silent gap.
 *
 *   CATEGORY A -- Cross-drive path containment.
 *     A bare `path.relative(dir, file).startsWith("..")` (or `'..'`) is the WRONG
 *     way to ask "is `file` outside `dir`". On Windows when `file` and `dir` live
 *     on DIFFERENT drives, `path.relative` cannot express a traversal and returns
 *     the ABSOLUTE target (`C:\Users\...`), which does NOT start with "..". So the
 *     shortcut reports an out-of-tree path as INSIDE the directory -- the exact
 *     failure at claude-preflight-hooks-contract.test.js:579
 *     (`expect(rel.startsWith("..")).toBe(true)` returned false on cross-drive
 *     Windows). The repo standardizes the correct shape as the shared helpers
 *     `isPathInsideDirectory` / `isPathOutsideDirectory` / `isOutsideRelative`
 *     (scripts/lib/path-classifier.js), which include the `path.isAbsolute(rel)`
 *     branch (and symlink-resolve + case-fold on Windows). This guard FLAGS any
 *     `.startsWith("..")` / `.startsWith('..')` applied to a `path.relative(...)`
 *     result on the SAME logical statement WITHOUT a companion `path.isAbsolute(`
 *     (or a routing through the shared helper). Sanctioned remediation: call the
 *     helper, or pair the bare check with `path.isAbsolute(rel)`.
 *
 *   CATEGORY B -- os.tmpdir()-rooted fixtures spawning check-eol/fix-eol.
 *     check-eol.js drops any target whose ABSOLUTE path contains an excluded
 *     directory segment (its excludeRegexes: .git, node_modules, Library, obj,
 *     `Temp`, Samples~, .vs, .venv, .artifacts, site) BEFORE collecting text
 *     files. The `Temp` rule (/(^|[\/\\])Temp([\/\\]|$)/) is case-SENSITIVE and
 *     matches the capitalized `Temp` segment Windows os.tmpdir() always carries
 *     ('C:\\Users\\<u>\\AppData\\Local\\Temp\\...'). So on Windows EVERY fixture
 *     under os.tmpdir() is excluded: the checker prints "EOL check skipped" and
 *     exits 0, and a "dirty corpus must fail" / "must pass" precondition passes
 *     vacuously -- the exact platform asymmetry behind the pre-push failure
 *     (Linux /tmp has no `Temp` segment, so it only ever surfaced on Windows).
 *     This is independent of bare-vs-absolute targets and of git toplevel: once
 *     the resolved path is excluded, neither path resolution nor a `git init`
 *     can rescue it. (An EARLIER revision of this guard wrongly treated `git
 *     init`-ing the fixture dir as the "hermetic" remedy on the assumption the
 *     only failure was bare-name resolution; that was incomplete and is now
 *     corrected -- git-init is no longer an escape hatch.)
 *     This guard FLAGS, PER `test(...)`/`it(...)` BLOCK, any block that builds an
 *     `os.tmpdir()`-derived `mkdtempSync` fixture (directly or via a file-scoped
 *     helper such as `makeTempDir`) AND spawns check-eol/fix-eol against it
 *     (directly or via a `runNode(CHECK_EOL, ...)` wrapper). Per-block (not
 *     whole-file) scoping means a file mixing one safe block and one os.tmpdir()
 *     block is flagged on the bad block instead of passing vacuously on the good
 *     one. Sanctioned remedy: root the fixture in an in-repo, NON-excluded
 *     scratch dir (`fs.mkdtempSync(path.join(REPO_ROOT, ...))`) and assert
 *     admissibility with check-eol's exported `isPathExcluded()`; never under
 *     os.tmpdir().
 *
 * How to mutation-test this guard (do this when you touch it):
 *   1. Re-add `expect(rel.startsWith("..")).toBe(true)` to any test with a
 *      `path.relative(...)`-derived `rel` -> CATEGORY A must FAIL. Revert.
 *   2. Re-root check-eol.test.js's closure temp dir under `os.tmpdir()` (instead
 *      of REPO_ROOT) -> CATEGORY B must FAIL. Revert.
 *   The self-tests below feed crafted source strings through the same detectors so
 *   the guard is verified without mutating real repository files.
 *
 * Node stdlib only; pure readFileSync + linear regex over scripts/ (no shell-outs,
 * ReDoS-free), so it stays well under the parser-policy budget.
 *
 * @cross-platform-regression -- this marker requires the file to be gated on
 * ubuntu/windows/macos via the targeted step in
 * .github/workflows/cross-platform-preflight.yml; enforced by
 * scripts/__tests__/cross-platform-preflight-coverage.test.js.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const { stripJsCommentsAndStrings } = require("../lib/source-stripping");
const { normalizeToLf } = require("../lib/quote-parser");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SCRIPTS_ROOT = path.join(REPO_ROOT, "scripts");

const WALK_SKIP_DIRS = new Set(["node_modules", ".git", ".venv", "__pycache__", "Temp"]);

// ---------------------------------------------------------------------------
// CATEGORY A allow-list. The shared helper itself standardizes the correct
// shape and is the home of the sanctioned predicate, so its definition/tests
// reference the bare token as DATA. Each entry carries a per-file rationale.
// ---------------------------------------------------------------------------
const CATEGORY_A_ALLOW_LIST = new Map([
  [
    path.join("scripts", "lib", "path-classifier.js"),
    "Defines the sanctioned predicate; isOutsideRelative()/isPathInsideDirectory() " +
      "pair startsWith('..') WITH path.isAbsolute (or are the canonical check)."
  ],
  [
    path.join("scripts", "__tests__", "path-classifier.test.js"),
    "Unit-tests the helper; references the bare-shortcut token in prose/assertions as data."
  ],
  [
    path.join("scripts", "__tests__", "path-containment-policy.test.js"),
    "THIS guard: embeds crafted bare-startsWith source strings as detector self-test fixtures."
  ]
]);

// ---------------------------------------------------------------------------
// CATEGORY B allow-list: tests that legitimately reference an os.tmpdir()
// mkdtemp + check-eol/fix-eol spawn as DATA (e.g. this guard's own self-test
// fixture strings), not as a real fixture. No real test roots a check-eol/fix-eol
// fixture under os.tmpdir(); the sound shape uses an in-repo non-excluded dir.
// ---------------------------------------------------------------------------
const CATEGORY_B_ALLOW_LIST = new Map([
  [
    path.join("scripts", "__tests__", "path-containment-policy.test.js"),
    "THIS guard: embeds crafted check-eol/fix-eol spawn source strings as self-test fixtures."
  ]
]);

function readUtf8(absolutePath) {
  return normalizeToLf(fs.readFileSync(absolutePath, "utf8")).replace(/^﻿/, "");
}

function toRepoRelativeKey(absolutePath) {
  return path.relative(REPO_ROOT, absolutePath);
}

function toRepoRelativePosix(absolutePath) {
  return toRepoRelativeKey(absolutePath).split(path.sep).join("/");
}

function listFilesRecursive(absoluteDir, predicate) {
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
    } catch {
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
      if (entry.isFile() && predicate(path.join(dir, entry.name))) {
        out.push(path.join(dir, entry.name));
      }
    }
  }

  return out;
}

function listScriptAndTestFiles() {
  // Every scripts/**/*.js (production + tests). CATEGORY A scans all of them;
  // CATEGORY B only the *.test.js files (filtered by the caller).
  return listFilesRecursive(SCRIPTS_ROOT, (abs) => abs.endsWith(".js"));
}

function lineNumberAt(text, index) {
  return text.slice(0, index).split("\n").length;
}

// ---------------------------------------------------------------------------
// CATEGORY A detector.
//
// Strategy: containment is almost always written as
//     const rel = path.relative(dir, file);
//     ... rel.startsWith("..") ...        // <- the bug, when unguarded
// (occasionally inline: `path.relative(dir,file).startsWith("..")`). So we:
//   1. Collect every identifier bound to a `path.relative(...)` result (the
//      "relative vars"), within-file.
//   2. Flag any `<relativeVar>.startsWith("..")` (or `'..'`) UNLESS the file ALSO
//      guards `<relativeVar>` with `isAbsolute(<relativeVar>)` or routes it
//      through a shared helper (isPathInsideDirectory / isPathOutsideDirectory /
//      isOutsideRelative). A file-scoped guard check is intentional: the correct
//      idiom in this repo is `!rel.startsWith("..") && !path.isAbsolute(rel)` (or
//      the ternary `rel.startsWith("..") || path.isAbsolute(rel) ? ...`), where
//      both operate on the same var nearby. File scope keeps the detector simple
//      and ReDoS-free while matching every real shape in the tree.
//   3. Also flag the INLINE chain `path.relative(...).startsWith("..")` directly.
//
// The `.startsWith` LITERAL must be matched on RAW source so we can distinguish
// the traversal payloads `".."` / `"../"` (the anti-pattern) from unrelated
// prefix checks like `"."` (a module-path) or `"skills/"` (a known dir). A
// stripped-line cross-check then discards any hit that lived in a comment or
// string literal. This is the same raw-match + stripped-cross-check discipline
// used by spawn-invocation-policy.test.js (pushIfRealCode).
// ---------------------------------------------------------------------------

// `<ident>.startsWith("..")` or `("../")`, single/double/backtick quoted, on RAW
// source. The traversal payload is exactly `..` or `../` (nothing else).
const VAR_STARTSWITH_RE = /\b([A-Za-z_$][\w$]*)\s*\.\s*startsWith\(\s*(["'`])\.\.\/?\2\s*\)/g;
// The inline chain `....relative(...).startsWith("..")`: a `.startsWith("..")`
// whose receiver expression ends in a `relative(...)` call. Matched on RAW source.
const INLINE_STARTSWITH_RE = /\.\s*startsWith\(\s*(["'`])\.\.\/?\1\s*\)/g;
const RELATIVE_CALL_RE = /(?:\bpath\s*\.\s*)?\brelative\s*\(/;
// path.relative binding: `const|let|var <id> = ... relative(...) ...`. Within-file.
const RELATIVE_BINDING_RE = /\b(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=[^\n;]*?\brelative\s*\(/g;

/**
 * The hit at `rawIndex` is REAL code only if the same line in the STRIPPED
 * source still contains a `.startsWith(` call (so the occurrence was not inside
 * a comment or string literal). Mirrors spawn-invocation-policy's pushIfRealCode.
 *
 * @param {string} rawSource
 * @param {string} strippedSource
 * @param {number} rawIndex
 * @returns {boolean}
 */
function isRealCode(rawSource, strippedSource, rawIndex) {
  const line = lineNumberAt(rawSource, rawIndex);
  const strippedLine = strippedSource.split("\n")[line - 1] || "";
  return /\.\s*startsWith\s*\(/.test(strippedLine);
}

/**
 * Collect identifiers bound to a `path.relative(...)` result within the file.
 * Runs on RAW source (the binding structure survives stripping anyway, and a
 * destructured `relative` import is preserved).
 *
 * @param {string} rawSource
 * @returns {Set<string>}
 */
function collectRelativeVarNames(rawSource) {
  const names = new Set();
  RELATIVE_BINDING_RE.lastIndex = 0;
  let match = RELATIVE_BINDING_RE.exec(rawSource);
  while (match !== null) {
    names.add(match[1]);
    match = RELATIVE_BINDING_RE.exec(rawSource);
  }
  return names;
}

/**
 * Is `varName` guarded somewhere in the file -- i.e. paired with
 * `isAbsolute(varName)` or passed to a shared containment helper? Either makes
 * the `startsWith("..")` shape cross-drive-safe.
 *
 * @param {string} rawSource
 * @param {string} varName
 * @returns {boolean}
 */
function isRelativeVarGuarded(rawSource, varName) {
  const escaped = escapeRegex(varName);
  if (new RegExp(`isAbsolute\\(\\s*${escaped}\\s*\\)`).test(rawSource)) {
    return true;
  }
  if (
    new RegExp(
      `(?:isPathInsideDirectory|isPathOutsideDirectory|isOutsideRelative)\\(\\s*${escaped}\\b`
    ).test(rawSource)
  ) {
    return true;
  }
  return false;
}

/**
 * Find unguarded `<relativeVar>.startsWith("..")` / `("../")` containment checks,
 * plus the inline `path.relative(...).startsWith("..")` chain.
 *
 * @param {string} rawSource - LF-normalized source.
 * @param {string} strippedSource - Comments/string payloads blanked.
 * @returns {Array<{line: number}>}
 */
function findBareStartsWithDotDot(rawSource, strippedSource) {
  const violations = [];
  const relativeVars = collectRelativeVarNames(rawSource);

  // (1) Variable form.
  VAR_STARTSWITH_RE.lastIndex = 0;
  let match = VAR_STARTSWITH_RE.exec(rawSource);
  while (match !== null) {
    const varName = match[1];
    if (
      relativeVars.has(varName) &&
      !isRelativeVarGuarded(rawSource, varName) &&
      isRealCode(rawSource, strippedSource, match.index)
    ) {
      violations.push({ line: lineNumberAt(rawSource, match.index) });
    }
    match = VAR_STARTSWITH_RE.exec(rawSource);
  }

  // (2) Inline chain form.
  INLINE_STARTSWITH_RE.lastIndex = 0;
  let inline = INLINE_STARTSWITH_RE.exec(rawSource);
  while (inline !== null) {
    if (
      receiverEndsInRelativeCall(rawSource, inline.index) &&
      isRealCode(rawSource, strippedSource, inline.index)
    ) {
      const line = lineNumberAt(rawSource, inline.index);
      if (!violations.some((v) => v.line === line)) {
        violations.push({ line });
      }
    }
    inline = INLINE_STARTSWITH_RE.exec(rawSource);
  }

  return violations;
}

/**
 * Given the index of a `.startsWith(...)` in source, walk left over any
 * whitespace and a single balanced `(...)` group and report whether the call
 * immediately to the left is `relative(` (optionally `path.relative(`).
 *
 * @param {string} src - Source text.
 * @param {number} startsWithIndex - Index of the `.` before `startsWith`.
 * @returns {boolean}
 */
function receiverEndsInRelativeCall(src, startsWithIndex) {
  let i = startsWithIndex - 1;
  while (i >= 0 && /\s/.test(src[i])) {
    i--;
  }
  if (i < 0 || src[i] !== ")") {
    return false;
  }
  // Walk left to the matching open paren.
  let depth = 0;
  for (; i >= 0; i--) {
    if (src[i] === ")") {
      depth++;
    } else if (src[i] === "(") {
      depth--;
      if (depth === 0) {
        break;
      }
    }
  }
  if (i < 0) {
    return false;
  }
  // The text just before the matching `(` must end in `relative`.
  const before = src.slice(Math.max(0, i - 40), i);
  return RELATIVE_CALL_RE.test(before + "(");
}

// ---------------------------------------------------------------------------
// CATEGORY B detector.
// ---------------------------------------------------------------------------

// A tmpdir-derived mkdtemp fixture: a `tmpdir()` source appearing inside an
// `fs.mkdtempSync(...)` argument (the common pattern across the suite). The
// detection must be CATEGORICAL over the tmpdir-resolution spelling, because a
// naive `mkdtempSync\([^)]*os\.tmpdir\(` misses two realistic, in-repo idioms:
//   (a) inline require -- `fs.mkdtempSync(path.join(require("os").tmpdir(), ...))`
//       (the `[^)]*` class terminates on the nested `)` in `require("os")` before
//       reaching `tmpdir`); this exact shape is already used in the tree
//       (unity-test-harness-contract.test.js).
//   (b) destructured binding -- `const { tmpdir } = require("os"); ...
//       mkdtempSync(path.join(tmpdir(), ...))` (no `os.` prefix at the call site).
// A future check-eol/fix-eol test written in either house style would otherwise
// silently evade CATEGORY B and pass vacuously -- the precise failure mode this
// guard exists to prevent. So instead of a single regex we balance-match each
// `mkdtempSync(...)` call's full argument span and look inside it for ANY tmpdir
// source, including destructured binding names tracked from the file.

// `os.tmpdir(` with flexible whitespace/dots (the idiomatic form).
const OS_DOT_TMPDIR_RE = /\bos\s*\.\s*tmpdir\s*\(/;
// Inline `require("os").tmpdir(` / `require('os').tmpdir(` (single or double quoted).
const REQUIRE_OS_TMPDIR_RE = /\brequire\s*\(\s*(["'])os\1\s*\)\s*\.\s*tmpdir\s*\(/;
// A destructured `tmpdir` (optionally aliased) pulled off `require("os")`:
//   const { tmpdir } = require("os")           -> binding name `tmpdir`
//   const { tmpdir: t } = require("os")        -> binding name `t`
//   const { mkdtempSync, tmpdir } = require("os")
// Captures the LOCAL binding name (group 2 when aliased, else group 1).
const DESTRUCTURED_TMPDIR_RE =
  /\{[^}]*\btmpdir\b\s*(?::\s*([A-Za-z_$][\w$]*))?[^}]*\}\s*=\s*require\s*\(\s*(["'])os\2\s*\)/g;
// A bare `mkdtempSync(` call opener (basename match; covers `fs.mkdtempSync`,
// `fsp.mkdtempSync`, `promises.mkdtempSync`, or a destructured `mkdtempSync`).
const MKDTEMP_OPEN_RE = /\bmkdtempSync\s*\(/g;

/**
 * Collect the LOCAL binding names introduced by a destructured
 * `const { tmpdir[: alias] } = require("os")` anywhere in the source. The
 * call-site spelling is then `<binding>(` (no `os.` prefix), so the mkdtemp
 * argument scan must know these names to catch the destructured idiom.
 *
 * @param {string} rawSource
 * @returns {Set<string>}
 */
function collectDestructuredTmpdirNames(rawSource) {
  const names = new Set();
  DESTRUCTURED_TMPDIR_RE.lastIndex = 0;
  let match = DESTRUCTURED_TMPDIR_RE.exec(rawSource);
  while (match !== null) {
    names.add(match[1] || "tmpdir");
    match = DESTRUCTURED_TMPDIR_RE.exec(rawSource);
  }
  return names;
}

/**
 * Does `argSpan` (the balanced argument text of a single `mkdtempSync(...)`
 * call) derive its root from an os tmpdir source? Covers the idiomatic
 * `os.tmpdir()`, the inline `require("os").tmpdir()`, and any destructured
 * tmpdir binding name discovered in the whole file.
 *
 * @param {string} argSpan
 * @param {Set<string>} destructuredNames - Local names bound to os.tmpdir.
 * @returns {boolean}
 */
function argSpanReferencesTmpdir(argSpan, destructuredNames) {
  if (OS_DOT_TMPDIR_RE.test(argSpan) || REQUIRE_OS_TMPDIR_RE.test(argSpan)) {
    return true;
  }
  for (const name of destructuredNames) {
    if (name === "tmpdir") {
      // The destructured default name is also the property name; only treat a
      // BARE `tmpdir(` call (not preceded by `.`, which would be `os.tmpdir`)
      // as the destructured-call form. `os.tmpdir(` is already covered above.
      if (new RegExp(`(^|[^.\\w$])${name}\\s*\\(`).test(argSpan)) {
        return true;
      }
      continue;
    }
    if (new RegExp(`\\b${escapeRegex(name)}\\s*\\(`).test(argSpan)) {
      return true;
    }
  }
  return false;
}

/**
 * Categorical replacement for the old `MKDTEMP_TMPDIR_RE.test(text)`: true when
 * `text` contains a `mkdtempSync(...)` call whose (balance-matched) argument span
 * roots in an os tmpdir source, in ANY of the spellings above. `destructuredNames`
 * defaults to scanning `text` itself, but callers analyzing a sub-span (a test
 * block / helper body) pass the WHOLE-FILE name set so a binding destructured at
 * module scope is still recognized inside the block.
 *
 * @param {string} text - Source (raw, LF-normalized).
 * @param {Set<string>} [destructuredNames] - Pre-collected file-scope bindings.
 * @returns {boolean}
 */
function referencesTmpdirMkdtemp(text, destructuredNames) {
  const names = destructuredNames || collectDestructuredTmpdirNames(text);
  MKDTEMP_OPEN_RE.lastIndex = 0;
  let match = MKDTEMP_OPEN_RE.exec(text);
  while (match !== null) {
    const openParen = match.index + match[0].length - 1;
    const end = matchBalanced(text, openParen, "(", ")");
    if (end > openParen) {
      const argSpan = text.slice(openParen + 1, end);
      if (argSpanReferencesTmpdir(argSpan, names)) {
        return true;
      }
      MKDTEMP_OPEN_RE.lastIndex = end + 1;
    } else {
      // Unbalanced (truncated source fixture): fall back to a permissive scan of
      // the remainder so a crafted self-test string is not silently missed.
      const tail = text.slice(openParen + 1);
      if (argSpanReferencesTmpdir(tail, names)) {
        return true;
      }
    }
    match = MKDTEMP_OPEN_RE.exec(text);
  }
  return false;
}
// A spawn whose command literal references check-eol.js or fix-eol.js. These are
// the scripts that resolve bare targets against `git rev-parse --show-toplevel`.
// Match the script basename inside a string literal anywhere on a spawn line.
const EOL_SCRIPT_SPAWN_RE = /(?:check-eol|fix-eol)\.js/;
const SPAWN_FAMILY_RE = /\b(?:spawnSync|spawn|execFileSync|execFile)\s*\(/;
/**
 * Decide whether a test file contains an os.tmpdir()-rooted fixture that spawns
 * check-eol/fix-eol. Analysis is PER TEST/it BLOCK (not whole-file): a file is an
 * offender if ANY single `test(...)`/`it(...)` block (a) builds an os.tmpdir()
 * mkdtemp fixture (directly or via a file-scoped helper such as `makeTempDir`)
 * AND (b) spawns check-eol/fix-eol against it. Per-block scoping means a file
 * mixing one safe and one offending tmpdir+check-eol test is flagged on the bad
 * block rather than passing vacuously on the safe sibling.
 *
 * NOTE (round-3 correction): there is deliberately NO git-init escape hatch.
 * git-init was previously treated as the "hermetic" remedy on the theory that
 * the only failure was bare-name resolution against a foreign git toplevel. That
 * is incomplete: check-eol.js drops any path whose absolute form contains an
 * excluded directory segment (its excludeRegexes: .git, node_modules, Library,
 * obj, `Temp`, Samples~, .vs, .venv, .artifacts, site) BEFORE collecting text
 * files. The `Temp` rule is case-SENSITIVE and matches the capitalized `Temp`
 * segment Windows os.tmpdir() always carries
 * ('C:\\Users\\<u>\\AppData\\Local\\Temp\\...'), so EVERY os.tmpdir() fixture is
 * excluded there regardless of git toplevel and regardless of whether targets
 * are bare or absolute. git-init cannot change the resolved path, so it cannot
 * remedy the exclusion -- it was misdirection. The ONLY sound remedy is to root
 * the fixture where check-eol's exclusion list cannot drop it (an in-repo,
 * non-excluded scratch dir, e.g. fs.mkdtempSync(path.join(REPO_ROOT, ...)),
 * proven admissible via check-eol's exported isPathExcluded()).
 *
 * @param {string} rawSource - LF-normalized source.
 * @returns {{offending: boolean, line: number|null}}
 */
function findNonHermeticEolSpawn(rawSource) {
  // Destructured `const { tmpdir } = require("os")` bindings are module-scoped,
  // so collect them ONCE from the whole file and thread the set through every
  // per-span tmpdir check (block bodies and helper bodies inherit them).
  const tmpdirNames = collectDestructuredTmpdirNames(rawSource);

  // Fast path: a file that never builds a tmpdir fixture cannot offend.
  if (!referencesTmpdirMkdtemp(rawSource, tmpdirNames)) {
    return { offending: false, line: null };
  }

  // Classify every file-scoped `function` helper by whether it builds an
  // os.tmpdir() fixture, so a block whose mkdtemp lives in a helper (e.g.
  // `makeTempDir`) still counts as a tmpdir fixture.
  const helperProps = collectHelperProperties(rawSource, tmpdirNames);
  // CHECK_EOL/FIX_EOL-style consts are typically bound at `describe` scope
  // (OUTSIDE any test block), so collect them from the whole file and detect
  // their USE per-block.
  const scriptConsts = collectEolScriptConstants(rawSource);
  const blocks = extractTestBlocks(rawSource);

  // If the source carries no test/it block (e.g. a bare self-test fixture string),
  // analyze it as a single implicit block so the detector still fires.
  const units = blocks.length > 0 ? blocks : [{ startLine: 1, body: rawSource }];

  for (const block of units) {
    const result = analyzeBlockForEolSpawn(block, helperProps, scriptConsts, tmpdirNames);
    if (result.offending) {
      return result;
    }
  }
  return { offending: false, line: null };
}

/**
 * Analyze a single test-block body for the os.tmpdir()+check-eol/fix-eol shape.
 *
 * @param {{startLine: number, body: string}} block
 * @param {Map<string, {makesTmpdir: boolean}>} helperProps
 *   File-scoped helper -> the properties a caller inherits from it.
 * @param {Set<string>} scriptConsts - File-scoped check-eol/fix-eol path consts.
 * @param {Set<string>} tmpdirNames - Whole-file destructured os.tmpdir bindings.
 * @returns {{offending: boolean, line: number|null}}
 */
function analyzeBlockForEolSpawn(block, helperProps, scriptConsts, tmpdirNames) {
  const lines = block.body.split("\n");

  // Helpers this block calls, with the properties it inherits from each.
  const calledHelpers = [...helperProps.keys()].filter((name) =>
    new RegExp(`\\b${escapeRegex(name)}\\s*\\(`).test(block.body)
  );
  const inheritsTmpdir = calledHelpers.some((n) => helperProps.get(n).makesTmpdir);

  const usesTmpdirFixture = referencesTmpdirMkdtemp(block.body, tmpdirNames) || inheritsTmpdir;
  if (!usesTmpdirFixture) {
    return { offending: false, line: null };
  }

  // Any check-eol/fix-eol spawn against an os.tmpdir() fixture is an offender;
  // git-init is NOT a remedy (see findNonHermeticEolSpawn). The sound fix is to
  // root the fixture in an in-repo, non-excluded scratch dir.

  // Direct spawn line referencing check-eol/fix-eol.
  for (let i = 0; i < lines.length; i++) {
    if (SPAWN_FAMILY_RE.test(lines[i]) && EOL_SCRIPT_SPAWN_RE.test(lines[i])) {
      return { offending: true, line: block.startLine + i };
    }
  }

  // Wrapper form: a CHECK_EOL/FIX_EOL-style const (bound at file/describe scope)
  // passed to a helper call within this block, e.g. `runNode(CHECK_EOL, ...)`.
  if (scriptConsts.size > 0) {
    const constPattern = new RegExp(
      `\\b(?:${[...scriptConsts].map(escapeRegex).join("|")})\\b\\s*[,)]`
    );
    // Exclude in-block const DEFINITION lines (where the script path itself is
    // bound) -- those are not spawn calls.
    const defLines = new Set();
    EOL_SCRIPT_CONST_RE.lastIndex = 0;
    let defMatch = EOL_SCRIPT_CONST_RE.exec(block.body);
    while (defMatch !== null) {
      defLines.add(lineNumberAt(block.body, defMatch.index));
      defMatch = EOL_SCRIPT_CONST_RE.exec(block.body);
    }
    for (let i = 0; i < lines.length; i++) {
      if (defLines.has(i + 1)) {
        continue;
      }
      if (constPattern.test(lines[i]) && /\b\w+\s*\(/.test(lines[i])) {
        return { offending: true, line: block.startLine + i };
      }
    }
  }

  return { offending: false, line: null };
}

// A `test(...)` / `it(...)` block opener, capturing the byte index of the `(`.
const TEST_BLOCK_OPEN_RE = /\b(?:test|it)(?:\s*\.\s*(?:each|only|skip|concurrent))?\s*\(/g;

/**
 * Extract every top-level `test(...)`/`it(...)` block body by brace-balancing
 * from the opening `(` to its matching `)`. File-scoped helpers and `describe`
 * setup (defined OUTSIDE any test block) are intentionally excluded from the
 * block bodies; the tmpdir-fixture property those helpers provide is reattached
 * via collectHelperProperties in the caller. Pure linear scan -- no
 * backtracking, ReDoS-free.
 *
 * @param {string} rawSource
 * @returns {Array<{startLine: number, body: string}>}
 */
function extractTestBlocks(rawSource) {
  const blocks = [];
  TEST_BLOCK_OPEN_RE.lastIndex = 0;
  let opener = TEST_BLOCK_OPEN_RE.exec(rawSource);
  while (opener !== null) {
    const openParen = opener.index + opener[0].length - 1;
    const end = matchBalanced(rawSource, openParen, "(", ")");
    if (end > openParen) {
      blocks.push({
        startLine: lineNumberAt(rawSource, opener.index),
        body: rawSource.slice(openParen + 1, end)
      });
      TEST_BLOCK_OPEN_RE.lastIndex = end + 1;
    }
    opener = TEST_BLOCK_OPEN_RE.exec(rawSource);
  }
  return blocks;
}

/**
 * From an opening bracket at `openIndex`, return the index of the matching
 * closing bracket (brace-balanced, linear). Returns -1 if unbalanced.
 *
 * @param {string} src
 * @param {number} openIndex
 * @param {string} open
 * @param {string} close
 * @returns {number}
 */
function matchBalanced(src, openIndex, open, close) {
  let depth = 0;
  for (let i = openIndex; i < src.length; i++) {
    if (src[i] === open) {
      depth++;
    } else if (src[i] === close) {
      depth--;
      if (depth === 0) {
        return i;
      }
    }
  }
  return -1;
}

// A file-scoped helper `function name(...) { ... }`. We classify each by whether
// it builds an os.tmpdir() mkdtemp fixture, so the per-block analysis sees
// through helper indirection (a block whose mkdtemp lives in `makeTempDir` is
// still a tmpdir fixture).
const FUNCTION_DECL_RE = /\bfunction\s+([A-Za-z_$][\w$]*)\s*\(/g;

/**
 * Classify every file-scoped `function` declaration by whether its body builds an
 * os.tmpdir() mkdtemp fixture. A test block calling such a helper inherits the
 * property (a block whose mkdtemp lives in `makeTempDir` is still a tmpdir
 * fixture).
 *
 * One level of indirection is resolved transitively (a helper that itself calls
 * another classified helper inherits its property), covering helper chains.
 *
 * @param {string} rawSource
 * @param {Set<string>} [tmpdirNames] - Whole-file destructured os.tmpdir
 *   bindings (so a helper using a module-scoped destructured `tmpdir` is still
 *   classified as a tmpdir builder). Defaults to scanning `rawSource`.
 * @returns {Map<string, {makesTmpdir: boolean}>}
 */
function collectHelperProperties(rawSource, tmpdirNames) {
  const names = tmpdirNames || collectDestructuredTmpdirNames(rawSource);
  const helpers = new Map();
  const bodies = new Map();

  FUNCTION_DECL_RE.lastIndex = 0;
  let decl = FUNCTION_DECL_RE.exec(rawSource);
  while (decl !== null) {
    const name = decl[1];
    const bodyOpen = rawSource.indexOf("{", decl.index + decl[0].length - 1);
    if (bodyOpen !== -1) {
      const bodyClose = matchBalanced(rawSource, bodyOpen, "{", "}");
      if (bodyClose > bodyOpen) {
        const body = rawSource.slice(bodyOpen + 1, bodyClose);
        bodies.set(name, body);
        helpers.set(name, {
          makesTmpdir: referencesTmpdirMkdtemp(body, names)
        });
      }
    }
    decl = FUNCTION_DECL_RE.exec(rawSource);
  }

  // Resolve one transitive pass: a helper inherits the property from any OTHER
  // classified helper it calls. A fixed-point loop (bounded by helper count)
  // keeps it linear and order-independent.
  let changed = true;
  let guard = 0;
  while (changed && guard < helpers.size + 1) {
    changed = false;
    guard++;
    for (const [name, body] of bodies) {
      const props = helpers.get(name);
      for (const [other, otherProps] of helpers) {
        if (other === name) {
          continue;
        }
        if (!new RegExp(`\\b${escapeRegex(other)}\\s*\\(`).test(body)) {
          continue;
        }
        if (otherProps.makesTmpdir && !props.makesTmpdir) {
          props.makesTmpdir = true;
          changed = true;
        }
      }
    }
  }

  return helpers;
}

const EOL_SCRIPT_CONST_RE =
  /\b(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=[^\n;]*(?:check-eol|fix-eol)\.js/g;

/**
 * Collect identifiers bound to a check-eol.js / fix-eol.js path within the file.
 *
 * @param {string} rawSource
 * @returns {Set<string>}
 */
function collectEolScriptConstants(rawSource) {
  const names = new Set();
  EOL_SCRIPT_CONST_RE.lastIndex = 0;
  let match = EOL_SCRIPT_CONST_RE.exec(rawSource);
  while (match !== null) {
    names.add(match[1]);
    match = EOL_SCRIPT_CONST_RE.exec(rawSource);
  }
  return names;
}

function escapeRegex(text) {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

// Guard the suite registration so the module can be `require()`d for its pure
// helpers OUTSIDE Jest (where describe/test/expect are undefined) without
// throwing at load time. Under Jest this is the real `describe`.
const maybeDescribe = typeof describe === "function" ? describe : () => {};

maybeDescribe("path-containment-policy (repo-wide)", () => {
  test("CATEGORY A: no bare path.relative(...).startsWith('..') containment check outside the sanctioned helper", () => {
    const offenders = [];

    for (const abs of listScriptAndTestFiles()) {
      const relKey = toRepoRelativeKey(abs);
      if (CATEGORY_A_ALLOW_LIST.has(relKey)) {
        continue;
      }
      const raw = readUtf8(abs);
      const stripped = stripJsCommentsAndStrings(raw);
      for (const hit of findBareStartsWithDotDot(raw, stripped)) {
        offenders.push({ file: toRepoRelativePosix(abs), line: hit.line });
      }
    }

    if (offenders.length > 0) {
      const details = offenders.map((o) => `  ${o.file}:${o.line}`).join("\n");
      throw new Error(
        "CATEGORY A violation: bare `path.relative(...).startsWith('..')` containment " +
          "check(s) found.\n" +
          "On cross-drive Windows (D:\\ repo vs C:\\ os.tmpdir()) path.relative returns an " +
          "ABSOLUTE target that does NOT start with '..', so this shortcut mislabels an " +
          "out-of-tree path as INSIDE the directory.\n" +
          "FIX: route through isPathInsideDirectory / isPathOutsideDirectory / " +
          "isOutsideRelative (scripts/lib/path-classifier.js), or pair the check with a " +
          "same-statement path.isAbsolute(rel).\n\n" +
          "Offending sites:\n" +
          details
      );
    }
  });

  test("CATEGORY B: no test spawns check-eol/fix-eol against an os.tmpdir()-rooted fixture", () => {
    const offenders = [];

    const testFiles = listScriptAndTestFiles().filter((abs) => abs.endsWith(".test.js"));
    for (const abs of testFiles) {
      const relKey = toRepoRelativeKey(abs);
      if (CATEGORY_B_ALLOW_LIST.has(relKey)) {
        continue;
      }
      const result = findNonHermeticEolSpawn(readUtf8(abs));
      if (result.offending) {
        offenders.push({ file: toRepoRelativePosix(abs), line: result.line });
      }
    }

    if (offenders.length > 0) {
      const details = offenders.map((o) => `  ${o.file}:${o.line}`).join("\n");
      throw new Error(
        "CATEGORY B violation: a test spawns check-eol.js/fix-eol.js against an " +
          "os.tmpdir()-rooted fixture dir.\n" +
          "check-eol.js drops any target whose absolute path contains an excluded " +
          "directory segment (its excludeRegexes: .git, node_modules, Library, obj, " +
          "`Temp`, Samples~, .vs, .venv, .artifacts, site) BEFORE collecting text " +
          "files. The `Temp` rule is case-SENSITIVE and matches the capitalized `Temp` " +
          "segment Windows os.tmpdir() always carries " +
          "('C:\\\\Users\\\\<u>\\\\AppData\\\\Local\\\\Temp\\\\...'), so EVERY fixture " +
          "there is excluded -- the checker prints 'EOL check skipped', exits 0, and a " +
          "'dirty corpus must fail' / 'must pass' precondition passes vacuously. Bare vs " +
          "absolute targets and git-init do not help: they cannot change the resolved, " +
          "excluded path.\n" +
          "FIX: root the fixture in an in-repo, NON-excluded scratch dir, e.g. " +
          "fs.mkdtempSync(path.join(REPO_ROOT, 'dxm-...-')), and assert admissibility " +
          "with check-eol's exported isPathExcluded(); never under os.tmpdir().\n\n" +
          "Offending sites:\n" +
          details
      );
    }
  });

  // -------------------------------------------------------------------------
  // Allow-list sanity: every exemption must still exist AND still contain the
  // pattern it was exempted for, so a stale exemption fails loudly.
  // -------------------------------------------------------------------------
  test("CATEGORY A allow-list is non-vacuous: each entry exists and references the startsWith('..') token", () => {
    // The exemption is justified only while the file still carries the token it
    // was exempted for (a `.startsWith("..")` / `.startsWith('..')` occurrence).
    const tokenRe = /\.startsWith\(\s*(["'])\.\.\1\s*\)/;
    for (const [relKey] of CATEGORY_A_ALLOW_LIST) {
      const abs = path.join(REPO_ROOT, relKey);
      expect(fs.existsSync(abs)).toBe(true);
      expect(tokenRe.test(readUtf8(abs))).toBe(true);
    }
  });

  test("CATEGORY B allow-list is non-vacuous: each entry exists", () => {
    for (const [relKey] of CATEGORY_B_ALLOW_LIST) {
      expect(fs.existsSync(path.join(REPO_ROOT, relKey))).toBe(true);
    }
  });

  // -------------------------------------------------------------------------
  // Self-tests: prove the detectors fire on the exact anti-patterns and do NOT
  // false-positive on the sanctioned shapes, without mutating real files.
  // -------------------------------------------------------------------------
  describe("detector self-tests", () => {
    // Convenience: feed a raw source string through the detector exactly as the
    // real scan does (raw + stripped projection).
    const scanA = (raw) => findBareStartsWithDotDot(raw, stripJsCommentsAndStrings(raw));

    test("CATEGORY A flags an unguarded <relativeVar>.startsWith('..') (the post-edit-guard bug shape)", () => {
      expect(
        scanA('const rel = path.relative(root, p); if (rel.startsWith("..")) { skip(); }')
      ).toHaveLength(1);
    });

    test("CATEGORY A flags the inline chain expect(path.relative(...).startsWith('..')).toBe(true)", () => {
      // The exact pre-fix offender at claude-preflight-hooks-contract.test.js:579.
      expect(scanA('expect(path.relative(repo, cache).startsWith("..")).toBe(true);')).toHaveLength(
        1
      );
    });

    test("CATEGORY A flags the two-statement var form too (file-scoped var tracking)", () => {
      expect(
        scanA('const rel = path.relative(repo, cache);\nexpect(rel.startsWith("..")).toBe(true);')
      ).toHaveLength(1);
    });

    test("CATEGORY A flags the '../'-payload variant (relativePath.startsWith('../'))", () => {
      expect(
        scanA('const rel = path.relative(dir, p); if (rel.startsWith("../")) return false;')
      ).toHaveLength(1);
    });

    test("CATEGORY A does NOT flag a startsWith('..') paired with path.isAbsolute(<sameVar>)", () => {
      expect(
        scanA(
          'const rel = path.relative(a, b); const out = rel.startsWith("..") || path.isAbsolute(rel);'
        )
      ).toHaveLength(0);
    });

    test("CATEGORY A does NOT flag a relativeVar routed through the shared helper", () => {
      expect(
        scanA("const rel = path.relative(a, b); if (isOutsideRelative(rel)) return null;")
      ).toHaveLength(0);
      // Even a transitional inline `&& rel.startsWith("..")` is safe while the
      // same var is also passed to the helper.
      expect(
        scanA(
          'const rel = path.relative(a, b); const x = isOutsideRelative(rel) && rel.startsWith("..");'
        )
      ).toHaveLength(0);
    });

    test("CATEGORY A does NOT flag a startsWith('..') on a NON-relative variable", () => {
      // `name` is not bound to path.relative, so this is some other string check.
      expect(scanA('if (name.startsWith("..")) { reject(); }')).toHaveLength(0);
    });

    test("CATEGORY A does NOT flag a NON-traversal prefix like '.' or 'skills/' on a relativeVar", () => {
      // The literal payload must be exactly `..` or `../`; `"."` (a module path)
      // and `"skills/"` (a known dir) are legitimate prefix checks.
      expect(scanA('const rel = path.relative(a, b); return rel.startsWith(".");')).toHaveLength(0);
      expect(
        scanA('const rel = path.relative(a, b); if (rel.startsWith("skills/")) keep();')
      ).toHaveLength(0);
    });

    test("CATEGORY A ignores the pattern inside a comment or string literal", () => {
      expect(
        scanA(
          '// const rel = path.relative(a,b); rel.startsWith("..")\nconst s = "rel.startsWith(\\"..\\")";'
        )
      ).toHaveLength(0);
    });

    test("CATEGORY A coverage boundary: a declarator-less reassignment of a relative result is NOT var-tracked (documented limit; inline chain still covers the common shape)", () => {
      // Issue 4 (round 2): collectRelativeVarNames only binds the DECLARATOR forms
      // `const|let|var <id> = ...relative(...)`. A bare reassignment WITHOUT a
      // declarator keyword -- `let rel; rel = path.relative(a, b); rel.startsWith("..")`
      // -- is not tracked, so the variable-form detector does not flag it. This is a
      // deliberately documented boundary, NOT a live gap: no such shape exists in the
      // tree, and the idiomatic forms (declarator binding + the inline
      // `path.relative(...).startsWith("..")` chain) ARE covered. This self-test
      // PINS the boundary so any future change to the binding recognizer is a
      // conscious decision rather than a silent regression.
      expect(collectRelativeVarNames("let rel;\nrel = path.relative(a, b);")).not.toContain("rel");
      // The bare-reassignment + var-startsWith shape is therefore not flagged...
      expect(
        scanA('let rel;\nrel = path.relative(a, b);\nif (rel.startsWith("..")) skip();')
      ).toHaveLength(0);
      // ...but the equivalent INLINE chain (the actual pre-fix offender shape) IS,
      // so the common reintroduction path stays closed.
      expect(scanA('if (path.relative(a, b).startsWith("..")) skip();')).toHaveLength(1);
    });

    test("CATEGORY B flags a tmpdir fixture spawning check-eol", () => {
      const src = [
        'const dir = fs.mkdtempSync(path.join(os.tmpdir(), "x-"));',
        'const r = spawnSync(node, ["scripts/check-eol.js", "a.js"], { cwd: dir });'
      ].join("\n");
      expect(findNonHermeticEolSpawn(src).offending).toBe(true);
    });

    test("CATEGORY B flags the runNode(CHECK_EOL, ...) wrapper form", () => {
      const src = [
        'const CHECK_EOL = path.join(REPO_ROOT, "scripts", "check-eol.js");',
        'const dir = fs.mkdtempSync(path.join(os.tmpdir(), "x-"));',
        "const before = runNode(CHECK_EOL, names, dir);"
      ].join("\n");
      expect(findNonHermeticEolSpawn(src).offending).toBe(true);
    });

    test("CATEGORY B STILL flags an os.tmpdir() fixture even when the dir is git-init'd", () => {
      // Round-3 correction: git-init was previously treated as the remedy. It is
      // NOT -- check-eol's case-sensitive `Temp` exclusion drops the os.tmpdir()
      // path regardless of git toplevel, so the spawn is still vacuous on
      // Windows. The guard must flag it so the false "fix" cannot be reintroduced.
      const src = [
        'const CHECK_EOL = path.join(REPO_ROOT, "scripts", "check-eol.js");',
        'const dir = fs.mkdtempSync(path.join(os.tmpdir(), "x-"));',
        'expect(git(dir, ["init"]).status).toBe(0);',
        "const before = runNode(CHECK_EOL, names, dir);"
      ].join("\n");
      expect(findNonHermeticEolSpawn(src).offending).toBe(true);
    });

    test("CATEGORY B does NOT flag an in-repo, non-excluded scratch dir (the sound remedy)", () => {
      // The sanctioned shape: root the fixture under REPO_ROOT (no excluded
      // segment) instead of os.tmpdir(). referencesTmpdirMkdtemp keys on a
      // tmpdir() SOURCE inside the mkdtemp argument, so a REPO_ROOT-rooted
      // mkdtemp is correctly NOT treated as a tmpdir fixture and is never flagged.
      const src = [
        'const CHECK_EOL = path.join(REPO_ROOT, "scripts", "check-eol.js");',
        'const dir = fs.mkdtempSync(path.join(REPO_ROOT, "dxm-eol-closure-"));',
        "const before = runNode(CHECK_EOL, names, dir);"
      ].join("\n");
      expect(findNonHermeticEolSpawn(src).offending).toBe(false);
    });

    test("CATEGORY B does NOT flag a test with no tmpdir fixture", () => {
      const src = 'const r = spawnSync(node, ["scripts/check-eol.js", "a.js"], { cwd: REPO });';
      expect(findNonHermeticEolSpawn(src).offending).toBe(false);
    });

    test("CATEGORY B does NOT flag a tmpdir fixture spawning an UNRELATED script", () => {
      const src = [
        'const dir = fs.mkdtempSync(path.join(os.tmpdir(), "x-"));',
        'spawnSync(node, ["scripts/validate-skills.js", "a.md"], { cwd: dir });'
      ].join("\n");
      expect(findNonHermeticEolSpawn(src).offending).toBe(false);
    });

    test("CATEGORY B flags a block whose os.tmpdir() mkdtemp lives in a file-scoped helper", () => {
      // The mkdtemp is delegated to `makeTempDir`; the block still counts as a
      // tmpdir fixture via helper-property inheritance, so the check-eol spawn is
      // flagged. (Indirection must not let the os.tmpdir() rooting hide.)
      const src = [
        'const CHECK_EOL = path.join(REPO_ROOT, "scripts", "check-eol.js");',
        "function makeTempDir() {",
        '  return fs.mkdtempSync(path.join(os.tmpdir(), "x-"));',
        "}",
        'test("closure", () => {',
        "  const dir = makeTempDir();",
        "  const before = runNode(CHECK_EOL, names, dir);",
        "});"
      ].join("\n");
      expect(findNonHermeticEolSpawn(src).offending).toBe(true);
    });

    test("CATEGORY B is PER-BLOCK: flags only the os.tmpdir() block, not the in-repo sibling", () => {
      // Per-block scoping: a file mixing a safe in-repo-scratch block and an
      // offending os.tmpdir() block must be flagged on the bad block, on the
      // exact spawn line, without the safe sibling masking it.
      const src = [
        'const CHECK_EOL = path.join(REPO_ROOT, "scripts", "check-eol.js");',
        'test("safe one", () => {',
        '  const dir = fs.mkdtempSync(path.join(REPO_ROOT, "a-"));',
        "  runNode(CHECK_EOL, names, dir);",
        "});",
        'test("offending one", () => {',
        '  const dir2 = fs.mkdtempSync(path.join(os.tmpdir(), "b-"));',
        '  spawnSync(node, ["scripts/check-eol.js", "a.js"], { cwd: dir2 });',
        "});"
      ].join("\n");
      const result = findNonHermeticEolSpawn(src);
      expect(result.offending).toBe(true);
      // The flagged spawn lives inside the SECOND (os.tmpdir()) block (line 8).
      expect(result.line).toBe(8);
    });

    test("CATEGORY B flags the INLINE require('os').tmpdir() spelling (the [^)]* evasion path)", () => {
      // The naive `mkdtempSync\([^)]*os\.tmpdir\(` regex FAILS here: the `[^)]*`
      // class terminates on the nested `)` in `require("os")` before reaching
      // `tmpdir`. This shape is already an established idiom in this repo
      // (unity-test-harness-contract.test.js), so a future check-eol/fix-eol
      // test in the house style would otherwise evade the guard and pass
      // vacuously. Both quote styles must be covered.
      const dq = [
        'const dir = fs.mkdtempSync(path.join(require("os").tmpdir(), "x-"));',
        'const r = spawnSync(node, ["scripts/check-eol.js", "a.js"], { cwd: dir });'
      ].join("\n");
      expect(findNonHermeticEolSpawn(dq).offending).toBe(true);
      const sq = [
        "const dir = fs.mkdtempSync(path.join(require('os').tmpdir(), 'x-'));",
        'const r = spawnSync(node, ["scripts/fix-eol.js", "a.js"], { cwd: dir });'
      ].join("\n");
      expect(findNonHermeticEolSpawn(sq).offending).toBe(true);
    });

    test("CATEGORY B flags the DESTRUCTURED { tmpdir } = require('os') spelling (no os. prefix at the call site)", () => {
      // `const { tmpdir } = require("os"); ... mkdtempSync(path.join(tmpdir(), ...))`
      // has no `os.` prefix at the call site, so a prefix-keyed regex misses it.
      // The detector tracks the destructured binding name from the whole file.
      const src = [
        'const { tmpdir } = require("os");',
        'const dir = fs.mkdtempSync(path.join(tmpdir(), "x-"));',
        'const r = spawnSync(node, ["scripts/check-eol.js", "a.js"], { cwd: dir });'
      ].join("\n");
      expect(findNonHermeticEolSpawn(src).offending).toBe(true);
    });

    test("CATEGORY B flags the ALIASED destructured { tmpdir: t } = require('os') spelling", () => {
      // An aliased binding renames the call site to `t()`; the detector must
      // track the LOCAL name, not the property name.
      const src = [
        'const { tmpdir: t } = require("os");',
        'const dir = fs.mkdtempSync(path.join(t(), "x-"));',
        'const r = spawnSync(node, ["scripts/check-eol.js", "a.js"], { cwd: dir });'
      ].join("\n");
      expect(findNonHermeticEolSpawn(src).offending).toBe(true);
    });

    test("CATEGORY B inline/destructured spellings are also flagged through a file-scoped helper", () => {
      // Helper indirection must not let the alternate tmpdir spellings hide:
      // the helper builds the fixture via the inline-require form, and the
      // block calling it must still be flagged.
      const inlineHelper = [
        'const CHECK_EOL = path.join(REPO_ROOT, "scripts", "check-eol.js");',
        "function makeTempDir() {",
        '  return fs.mkdtempSync(path.join(require("os").tmpdir(), "x-"));',
        "}",
        'test("closure", () => {',
        "  const dir = makeTempDir();",
        "  const before = runNode(CHECK_EOL, names, dir);",
        "});"
      ].join("\n");
      expect(findNonHermeticEolSpawn(inlineHelper).offending).toBe(true);

      const destructuredHelper = [
        'const { tmpdir } = require("os");',
        'const CHECK_EOL = path.join(REPO_ROOT, "scripts", "check-eol.js");',
        "function makeTempDir() {",
        '  return fs.mkdtempSync(path.join(tmpdir(), "x-"));',
        "}",
        'test("closure", () => {',
        "  const dir = makeTempDir();",
        "  const before = runNode(CHECK_EOL, names, dir);",
        "});"
      ].join("\n");
      expect(findNonHermeticEolSpawn(destructuredHelper).offending).toBe(true);
    });

    test("CATEGORY B does NOT flag a bare local tmpdir() that is NOT bound to require('os')", () => {
      // A `tmpdir` that is not destructured from `require("os")` (e.g. a custom
      // helper of the same name) must not be treated as the os tmpdir source.
      const src = [
        'function tmpdir() { return path.join(REPO_ROOT, "scratch"); }',
        'const dir = fs.mkdtempSync(path.join(tmpdir(), "x-"));',
        'const r = spawnSync(node, ["scripts/check-eol.js", "a.js"], { cwd: dir });'
      ].join("\n");
      expect(findNonHermeticEolSpawn(src).offending).toBe(false);
    });
  });
});

if (typeof module !== "undefined" && module.exports) {
  module.exports = {
    findBareStartsWithDotDot,
    findNonHermeticEolSpawn,
    collectEolScriptConstants,
    collectRelativeVarNames
  };
}
