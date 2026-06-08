/**
 * @fileoverview Repository-wide policy guard that makes the ENTIRE category of
 * "fragile phrase assertion on RAW PowerShell-rendered output" impossible to
 * reintroduce. It covers the direct member-access, tainted-local-variable, and
 * renamed-merge-helper forms, AND the four formerly-residual bypasses that are now
 * CLOSED: an INTERPOLATED-template phrase argument, a phrase stored in a VARIABLE,
 * a STRING-CONCATENATION raw merge in a renamed helper, and a tainted-local raw
 * merge whose initializer spans REAL source newlines (now also via a comma
 * declarator or a paren-wrapped initializer). A small set of intentional residuals
 * remains (a helper returning a BARE single member; a string-less `+` of two raw
 * members; general runtime indirection); see "Residual limits".
 *
 * Background:
 *   When `pwsh -File <script>` lets an unhandled `throw` reach the top of the
 *   script, PowerShell's ConciseView error formatter WORD-WRAPS the message at
 *   the host console width, inserting a `\n     | ` continuation gutter (plus
 *   ANSI color escapes) between words. On the narrower Windows CI runner this
 *   splits an asserted phrase -- e.g. "outside the managed root" became
 *   "...outside the\n     | managed root." -- so a literal `.toContain(...)` on
 *   the raw stdout/stderr found nothing and the test FAILED on Windows only
 *   (it passed on Linux/macOS, where the default width kept the phrase together).
 *   The production message was CORRECT; the assertion was width-dependent.
 *
 *   The fix is scripts/lib/pwsh-output.js's `normalizePwshText`, which strips
 *   ANSI/OSC escapes, rejoins the gutter, and collapses whitespace before the
 *   assertion. This guard ensures every phrase/error substring assertion against
 *   pwsh output in a pwsh-spawning test file goes through that normalizer (or a
 *   recognized wrapper helper), so the regression is loud at pre-push time on
 *   EVERY host, not just Windows.
 *
 *   Policy -- In a test file that SPAWNS pwsh, a phrase assertion (a
 *   `.toContain(<multi-word string literal>)` / `.not.toContain(<multi-word
 *   string literal>)` / `.toMatch(<regex literal>)`) applied to a RAW
 *   pwsh-output receiver is forbidden. A "raw pwsh-output receiver" is either:
 *     (a) a MEMBER-ACCESS on a spawn result that ends in `.stdout` / `.stderr`
 *         (e.g. `result.stdout`, `out.stderr`, `run.stdout`), or a template
 *         literal that merges such members (e.g. `` `${out.stdout}\n${out.stderr}` ``);
 *         OR
 *     (b) a LOCAL VARIABLE bound to such a raw merge in the same file
 *         (`const combined = `${out.stdout}\n${out.stderr}`; ... expect(combined)`).
 *   The receiver is COMPLIANT when it is wrapped in `normalizePwshText(...)`, or
 *   is (or is bound to) a recognized normalizing wrapper helper -- by convention
 *   `combinedText(...)` / `stdoutText(...)`, which this repo defines as
 *   `normalizePwshText`-backed one-liners. A single-word `.toContain("token")`
 *   (no interior whitespace) is NOT a wrappable phrase -- a single token cannot
 *   be split by a space-driven word wrap -- so it is intentionally not flagged.
 *
 *   A SEPARATE rule closes the "renamed helper" door: the receiver check above
 *   recognizes the normalizing wrappers by NAME, so a differently-named helper
 *   whose body rebuilds the raw `${out.stdout}\n${out.stderr}` template merge
 *   WITHOUT `normalizePwshText` would launder the anti-pattern past it. The guard
 *   therefore also forbids DEFINING, in a pwsh-spawning test file, a `return`/
 *   arrow-implicit-return whose value is an un-normalized `.stdout`/`.stderr`
 *   template merge -- the merge is flagged at its definition, independent of how it
 *   is later called (see findRenamedMergeHelpers).
 *
 * How to mutation-test this guard (do this when you touch it):
 *   1. In a consumer test (e.g. unity-ensure-editor-il2cpp-idempotency.test.js),
 *      change `const combined = combinedText(out);` back to
 *      `const combined = `${out.stdout || ""}\n${out.stderr || ""}`;`.
 *      Run this suite -> it must FAIL (tainted-local form). Revert.
 *   2. Add `expect(result.stdout).toContain("outside the managed root");` to a
 *      pwsh-spawning test. Run this suite -> it must FAIL (member-access form).
 *      Revert.
 *   3. In a consumer test, change `combinedText`'s body from
 *      `return normalizePwshText(`${run.stdout || ""}\n${run.stderr || ""}`);` to
 *      the un-normalized `return `${run.stdout || ""}\n${run.stderr || ""}`;`.
 *      Run this suite -> it must FAIL (renamed-merge-helper form). Revert.
 *   4. Add `expect(result.stdout).toContain(`saw ${n} outside the managed root`);`
 *      to a pwsh-spawning test. Run this suite -> it must FAIL (interpolated-
 *      argument form: after dropping `${n}` the literal text still has a wrappable
 *      word boundary). Revert.
 *   5. Add `const phrase = "outside the managed root";` then
 *      `expect(result.stdout).toContain(phrase);` to a pwsh-spawning test. Run this
 *      suite -> it must FAIL (phrase-variable form). Revert.
 *   6. In a consumer test, change `combinedText`'s body to the string-CONCATENATION
 *      `return run.stdout + "\n" + run.stderr;`. Run this suite -> it must FAIL
 *      (concat renamed-merge-helper form). Revert.
 *   7. In a consumer test, replace the `const combined = combinedText(out);` line
 *      with a MULTI-LINE raw merge that breaks the initializer across real source
 *      newlines:
 *        `const combined =\n  `${out.stdout}\n` +\n  `${out.stderr}`;`.
 *      Run this suite -> it must FAIL (multi-line tainted-local form). Revert.
 *   If a mutation does NOT fail, the guard is inadequate and must be hardened.
 *   Self-tests below feed crafted source strings through the same detector to
 *   prove each form fires (and that its near-miss compliant shape does not
 *   false-positive) without touching real files.
 *
 * Residual limits (the long tail this guard does NOT chase):
 *   The four bypasses listed here historically were CLOSED (each now has a
 *   FLAGGED self-test plus a near-miss COMPLIANT self-test, and a repo-wide
 *   zero-false-positive proof across all pwsh-spawning files). They are kept on the
 *   record so a future reader does not re-add them as "new" gaps:
 *     - INTERPOLATED / TEMPLATE phrase ARGUMENT -- now CLOSED. argumentIsPhrase
 *       strips the `${...}` interpolation segments from a template argument and
 *       treats the remaining literal text as a phrase when it still has a
 *       wrappable interior word boundary, so `expect(out.stdout).toContain(
 *       `prefix ${x} outside the managed root`)` IS flagged on a raw receiver. A
 *       NORMALIZED receiver (`stdoutText(result)`) still passes -- the receiver
 *       classification returns compliant before the argument is consulted.
 *     - PHRASE STORED IN A VARIABLE then asserted -- now CLOSED. collectPhraseVariables
 *       gathers `const|let|var NAME = "<multi-word string literal>"` bindings; an
 *       identifier matcher argument that is such a phrase variable counts as a
 *       phrase, so `const phrase = "outside the managed root"; expect(
 *       out.stdout).toContain(phrase);` IS flagged on a raw receiver. Single-word
 *       string variables and non-string bindings are NOT collected.
 *     - STRING-CONCATENATION raw merge in a renamed helper -- now CLOSED.
 *       isRawMergeExpression treats a top-level `+` concatenation as a raw STRING
 *       merge only when it is GENUINE string concatenation of raw output: it requires
 *       BOTH a raw-output operand (a BARE member `o.stdout` / `(o.stdout || "")`, or a
 *       template literal that interpolates a raw member) AND a string/template-literal
 *       operand. So `=> o.stdout + "\n" + o.stderr` IS flagged, but ARITHMETIC `+`
 *       touching raw output (`a.stdout.length + b.length`, `idx + r.stdout.indexOf(x)`,
 *       `result.stdout.length + 1`) and a string-only `+` with no raw-output operand
 *       (`"x" + y`) are NOT (see B1). A member chain with no top-level `+`
 *       (`return result.stdout.split(...)`), a result-shaper object (`return {
 *       stdout, stderr, status }`), and a wrapped concat (`return
 *       normalizePwshText(a.stdout + b)`) are NOT flagged.
 *     - MULTI-LINE tainted-local initializer -- now CLOSED. collectTaintedRawVariables
 *       reads the FULL initializer expression of EVERY declarator (including the
 *       second-or-later declarator of a comma list, and a PAREN-WRAPPED initializer)
 *       with balanced bracket/paren/template awareness over the offset-preserving MASK
 *       projection (maskCommentsAndStrings), so a raw merge split across REAL source
 *       newlines, wrapped in `(...)`, or hidden after `const x = 1,` still taints the
 *       variable. A multi-line / paren-wrapped NORMALIZED merge is not.
 *
 *   Call-site comment/string safety: the `expect(...)` locator scans the
 *   offset-preserving MASK projection (code kept verbatim, comment AND string/template
 *   payloads blanked), so an `expect(...stdout...).toContain("multi word")` spelled in
 *   a COMMENT or a STRING LITERAL is invisible to the scan; the receiver and argument
 *   text are then read from the RAW source at the offset-aligned positions, so phrase
 *   analysis is exact (see B3). The renamed-merge and collector scans likewise run on
 *   stripped/masked source, so data in comments/strings cannot false-positive.
 *
 *   Remaining intentional residuals (deliberately NOT flagged, to avoid real false
 *   positives; each would need a FLAGGED self-test, a near-miss COMPLIANT self-test,
 *   AND a repo-wide zero-false-positive proof before being closed):
 *     - A helper that returns a BARE single member (`return result.stdout;`): it is
 *       indistinguishable from the legitimate structural single-member reads this repo
 *       relies on (`return result.stdout.split(...)`, `return result.stdout.trim()`,
 *       `return { stdout, stderr, status }`). The member-access detector (violation
 *       (a)) still catches the direct `expect(x.stdout).toContain("...")` call site.
 *     - A `+` concatenation of raw output with NO string/template-literal operand
 *       (e.g. `o.stdout + o.stderr`): without a string operand the expression is
 *       ambiguous with arithmetic, so the B1 rule requires a string operand to flag
 *       it. This repo's only `+` merge spelling includes the `"\n"` separator (which
 *       is flagged); the bare two-member `+` is not used.
 *     - Runtime indirection generally (a phrase or merge assembled through a function
 *       parameter, an array join, etc.). The normalizer's own unit test
 *       (scripts/lib/__tests__/pwsh-output.test.js) is the primary correctness
 *       backstop for the rendered-output normalization itself.
 *
 * @cross-platform-regression -- this marker requires the file to be gated on
 * ubuntu/windows/macos via the targeted step in
 * .github/workflows/cross-platform-preflight.yml; enforced by
 * scripts/__tests__/cross-platform-preflight-coverage.test.js.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");

const {
  collectPwshOutputContext,
  findRenamedMergeHelpers: findRenamedMergeHelpersFromFixer,
  hasPwshSpawn,
  isPwshResultAt,
  isRawMergeVariableAt
} = require("../fix-pwsh-output-assertions");
const { readUtf8, lineNumberAt, toRepoRelative } = require("../lib/repo-files");
const { stripJsCommentsAndStrings, maskCommentsAndStrings } = require("../lib/source-stripping");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const TESTS_ROOT = path.join(REPO_ROOT, "scripts", "__tests__");
const LIB_TESTS_ROOT = path.join(REPO_ROOT, "scripts", "lib", "__tests__");

// Matchers that take a phrase/regex and assert it against the `expect(...)`
// receiver. `.not.` is handled by the chain scanner, so both positive and
// negative forms are covered.
const PHRASE_MATCHERS = new Set(["toContain", "toMatch"]);

// Recognized helpers that internally route through normalizePwshText. A receiver
// that is a call to one of these (or a variable bound to such a call) is
// compliant. Keep this list small and intentional.
const NORMALIZING_HELPERS = new Set(["normalizePwshText", "combinedText", "stdoutText"]);

// This guard file embeds crafted source strings (the detector self-tests) and
// prose describing exactly what is forbidden; those literals are data, not real
// assertions. The normalizer's own unit test references the helper by name in
// prose/imports but performs no pwsh spawn.
const SELF = path.join("scripts", "__tests__", "pwsh-output-assertion-policy.test.js");

function listTestFiles() {
  const out = [];
  for (const root of [TESTS_ROOT, LIB_TESTS_ROOT]) {
    out.push(...listTestFilesUnder(root));
  }
  return out;
}

function listTestFilesUnder(root) {
  const out = [];
  if (!fs.existsSync(root)) {
    return out;
  }
  for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
    const absolutePath = path.join(root, entry.name);
    if (entry.isDirectory()) {
      out.push(...listTestFilesUnder(absolutePath));
    } else if (entry.isFile() && entry.name.endsWith(".test.js")) {
      out.push(absolutePath);
    }
  }
  return out;
}

// The child_process spawners (plus this repo's cross-platform helper from
// scripts/lib/shell-command.js) whose FIRST string-literal argument is the command
// to run. spawnPlatformCommandSync is included so a future test that spawns
// PowerShell through the repo's own helper is still recognized.
const SPAWNERS = "(?:spawnSync|spawn|execFileSync|execFile|spawnPlatformCommandSync)";

// A command LITERAL that names a PowerShell executable: open-source `pwsh` /
// `pwsh.exe`, or legacy Windows `powershell` / `powershell.exe` (case-insensitive).
// Legacy Windows PowerShell's default error view ALSO word-wraps at the console
// width, and normalizePwshText recovers both the gutter and plain wraps, so a
// phrase assertion on either's raw output is the same regression. This stays
// PRECISE: only a pwsh/powershell executable name matches, never `node`, `npm`, etc.
const POWERSHELL_COMMAND = /^(?:pwsh|powershell)(?:\.exe)?$/i;

// First string-literal argument of a spawn call, captured (group 1 is the literal
// text inside the quotes). Tolerates whitespace/newlines after the `(`. GLOBAL so
// matchAll can iterate every spawn call; matchAll seeds its internal clone from
// this regex's `lastIndex` but never writes it back. Since nothing in this file
// leaves `lastIndex` non-zero, there is no stale-state hazard and no manual reset
// is needed.
const SPAWN_FIRST_STRING_ARG = new RegExp(`\\b${SPAWNERS}\\s*\\(\\s*["'\`]([^"'\`]*)["'\`]`, "g");

// A spawn whose command IDENTIFIER (not a string literal) is a pwsh-path variable
// the harnesses bind from a pwsh probe (`(Get-Command pwsh).Source`). Built once
// from SPAWNERS, like its siblings above.
const SPAWN_PWSH_INDIRECTION = new RegExp(
  `\\b${SPAWNERS}\\s*\\(\\s*(?:REAL_PWSH|PWSH|pwshPath|PWSH_PATH)\\b`
);

/**
 * True when the source SPAWNS pwsh / PowerShell via child_process (so its
 * stdout/stderr is real, console-width-wrapped error output). Recognizes:
 *   - a DIRECT command literal that is a PowerShell executable -- `pwsh`,
 *     `pwsh.exe`, legacy `powershell` / `powershell.exe` (case-insensitive) -- as
 *     the first string-literal argument of `spawnSync` / `spawn` / `execFileSync` /
 *     `execFile` / the repo's own `spawnPlatformCommandSync`, in single/double/
 *     backtick quoting and across whitespace/newlines; AND
 *   - the `REAL_PWSH` / `PWSH` / `pwshPath` / `PWSH_PATH` indirection used by this
 *     repo's harnesses (a pwsh path resolved from a probe).
 * It stays PRECISE: a spawn of an unrelated command (`spawnSync("node", ...)`,
 * `spawnPlatformCommandSync("npm", ...)`) does NOT count.
 *
 * @param {string} source - Raw source (LF-normalized).
 * @returns {boolean}
 */
function spawnsPwsh(source) {
  return hasPwshSpawn(source);
}

/**
 * Find the index just past the matching close paren for an open paren at
 * `openIndex`, skipping over string/template-literal payloads so parens inside
 * strings never unbalance the scan. Returns -1 if unbalanced.
 *
 * @param {string} source - Raw source.
 * @param {number} openIndex - Index of the `(`.
 * @returns {number} Index of the matching `)`, or -1.
 */
function matchParen(source, openIndex) {
  let depth = 0;
  let quote = null;
  for (let i = openIndex; i < source.length; i++) {
    const ch = source[i];
    const prev = i > 0 ? source[i - 1] : "";
    if (quote) {
      if (ch === quote && prev !== "\\") {
        quote = null;
      }
      continue;
    }
    if (ch === '"' || ch === "'" || ch === "`") {
      quote = ch;
      continue;
    }
    if (ch === "(") {
      depth++;
    } else if (ch === ")") {
      depth--;
      if (depth === 0) {
        return i;
      }
    }
  }
  return -1;
}

function matchBracket(source, openIndex, openChar, closeChar) {
  let depth = 0;
  for (let i = openIndex; i < source.length; i++) {
    const ch = source[i];
    if (ch === openChar) {
      depth++;
    } else if (ch === closeChar) {
      depth--;
      if (depth === 0) {
        return i;
      }
    }
  }
  return -1;
}

// A receiver is a RAW pwsh-output member access if it contains a `.stdout` or
// `.stderr` property access that is NOT the argument of a normalizing helper.
// (When wrapped, the `.stdout` lives inside `normalizePwshText(... .stdout ...)`,
// and the OUTERMOST callee is the helper -- see classifyReceiver.)
const STDOUT_STDERR_MEMBER = /\.\s*(?:stdout|stderr)\b/;

/**
 * The OUTERMOST callee name of a receiver expression, if the whole receiver is a
 * single call `name(...)`. Returns null when the receiver is not a bare call
 * (e.g. a plain identifier, or a member access). Used to recognize a receiver
 * that is `normalizePwshText(...)` / `combinedText(...)`.
 *
 * @param {string} receiver - Trimmed receiver source.
 * @returns {string|null}
 */
function outermostCalleeName(receiver) {
  const m = /^([A-Za-z_$][\w$]*)\s*\(/.exec(receiver);
  if (!m) {
    return null;
  }
  // Ensure the call spans the WHOLE receiver (its close paren is the last char),
  // so `a(b).c` (which is not simply a normalizing call) is not misread.
  const close = matchParen(receiver, m.index + m[0].length - 1);
  if (close !== receiver.length - 1) {
    return null;
  }
  return m[1];
}

/**
 * Walk EVERY declarator of every `const|let|var` declaration -- including the
 * SECOND-and-later declarators of a comma list (`const x = 1, combined = ...;`) --
 * over the offset-preserving MASK projection (maskCommentsAndStrings: code kept
 * verbatim, comment/string payloads blanked to spaces, total length unchanged).
 * Operating on the mask means (1) a declaration spelled inside a STRING LITERAL or
 * comment is invisible (its keyword/`=` are blanked), so it cannot false-positive,
 * and (2) every returned offset aligns 1:1 with the RAW source, so a caller can read
 * a string-literal payload from the raw source at the reported offsets.
 *
 * For each declarator with an initializer it yields `{ name, initializerStart,
 * initializer }`: `initializer` is the trimmed initializer text read from the MASK
 * with balanced bracket/paren/template awareness (so a merge split across REAL source
 * newlines is captured whole), and `initializerStart` is the absolute offset of the
 * first non-space initializer character (aligned to raw). A declarator with no
 * initializer (`let a, b = ...` -> the `a`) is skipped.
 *
 * @param {string} masked - The maskCommentsAndStrings projection of the source.
 * @returns {Array<{name:string, initializerStart:number, initializer:string}>}
 */
function collectDeclaratorBindings(masked) {
  const bindings = [];
  const keywordRe = /\b(?:const|let|var)\s+/g;
  let kw = keywordRe.exec(masked);
  while (kw !== null) {
    let i = kw.index + kw[0].length;
    // Parse a comma-separated declarator list until `;`, an unmatched close
    // bracket, or end of input.
    while (i < masked.length) {
      // Skip whitespace before each declarator name (a second-or-later declarator
      // begins after `, ` and may have leading spaces/newlines).
      while (i < masked.length && /\s/.test(masked[i])) {
        i++;
      }
      const nameMatch = /^([A-Za-z_$][\w$]*)\s*/.exec(masked.slice(i));
      if (!nameMatch) {
        break;
      }
      const name = nameMatch[1];
      i += nameMatch[0].length;
      if (masked[i] !== "=" || masked[i + 1] === "=") {
        // Declarator with no initializer (e.g. `let a, b = ...` -> `a`), or a
        // stray `==`/`=>` that is not an assignment. Advance to the next
        // top-level `,` (another declarator) or end the statement.
        const after = declaratorInitializerEnd(masked, i);
        if (after.terminator === ",") {
          i = after.end + 1;
          continue;
        }
        break;
      }
      i += 1; // past the `=`
      let initializerStart = i;
      while (initializerStart < masked.length && /\s/.test(masked[initializerStart])) {
        initializerStart++;
      }
      const end = declaratorInitializerEnd(masked, initializerStart);
      const initializer = masked.slice(initializerStart, end.end).trim();
      bindings.push({ name, initializerStart, initializer });
      if (end.terminator === ",") {
        i = end.end + 1; // next declarator in the same statement
        continue;
      }
      break; // `;` / unmatched close / EOF ends this declaration
    }
    keywordRe.lastIndex = Math.max(keywordRe.lastIndex, kw.index + kw[0].length);
    kw = keywordRe.exec(masked);
  }
  return bindings;
}

/**
 * Find where an initializer expression beginning at `start` ends, reporting the
 * terminator. Mirrors readReturnExpression's balanced scan but returns the index of
 * (and the kind of) the terminator so a declarator-list walker can decide whether
 * another declarator follows (`,`) or the statement ended (`;` / unmatched close).
 *
 * @param {string} source - Masked (or comment/string-stripped) source.
 * @param {number} start - Index just past the `=`.
 * @returns {{end:number, terminator:(","|";"|"close"|"eof")}}
 */
function declaratorInitializerEnd(source, start) {
  let depth = 0;
  for (let i = start; i < source.length; i++) {
    const ch = source[i];
    if (ch === "(" || ch === "[" || ch === "{") {
      depth++;
    } else if (ch === ")" || ch === "]" || ch === "}") {
      if (depth === 0) {
        return { end: i, terminator: "close" };
      }
      depth--;
    } else if (ch === "," && depth === 0) {
      return { end: i, terminator: "," };
    } else if (ch === ";" && depth === 0) {
      return { end: i, terminator: ";" };
    }
  }
  return { end: source.length, terminator: "eof" };
}

/**
 * Collect local variable names that are bound to a RAW pwsh-output merge -- e.g.
 * `const combined = `${out.stdout}\n${out.stderr}`;`,
 * `const combined = `${result.stdout || ""}\n${result.stderr || ""}`;`, the
 * string-concatenation `const combined = out.stdout + "\n" + out.stderr;`, a
 * MULTI-LINE merge whose initializer is broken across real source newlines
 * (`const combined =\n  `${out.stdout}\n` +\n  `${out.stderr}`;`), a PAREN-WRAPPED
 * merge (`const combined = (\n  `${out.stdout}` +\n  `${out.stderr}`\n);`), or a
 * SECOND-or-later declarator in a comma list (`const x = 1, combined = `${...}`;`).
 * These are "tainted": a phrase assertion on them is the anti-pattern. A binding to
 * a normalizing helper (`const combined = combinedText(out);`) is NOT tainted and
 * is intentionally excluded.
 *
 * The scan runs over the offset-preserving MASK projection (maskCommentsAndStrings)
 * for two reasons: (1) it neutralizes string/comment data, so a binding spelled out
 * inside a STRING LITERAL or a comment cannot false-positive -- only real code is
 * seen; and (2) the FULL initializer expression (every declarator) can be read with
 * the same balanced bracket/paren/template reader as the renamed-helper path
 * (readReturnExpression), which is what lets a merge split across REAL source
 * newlines or wrapped in parens still taint the variable. The `.stdout`/`.stderr`
 * members survive masking because they live in real code -- inside `${...}`
 * interpolations or a plain `+` concatenation -- which the mask keeps verbatim. The
 * shared isRawMergeExpression predicate decides template-merge vs. `+`-concat-merge,
 * keeping the definition of "raw merge" identical here and in findRenamedMergeHelpers.
 *
 * @param {string} source - Raw source (LF-normalized).
 * @param {string} [maskedSource] - Precomputed maskCommentsAndStrings(source); pass
 *   it to avoid recomputing the projection in the per-file hot path.
 * @returns {Set<string>} Tainted local variable names.
 */
function collectTaintedRawVariables(source, maskedSource) {
  const masked = maskedSource !== undefined ? maskedSource : maskCommentsAndStrings(source);
  const tainted = new Set();
  for (const binding of collectDeclaratorBindings(masked)) {
    if (isRawMergeExpression(binding.initializer)) {
      tainted.add(binding.name);
    }
  }
  return tainted;
}

/**
 * Collect local variable names that are bound to a fixed MULTI-WORD STRING LITERAL
 * -- e.g. `const phrase = "outside the managed root";`, including the second-or-later
 * declarator of a comma list (`const x = 1, phrase = "outside the managed root";`). A
 * phrase assertion whose matcher argument is one of these identifiers asserts a
 * width-wrappable phrase just as a literal argument would, so storing the phrase in a
 * variable must not launder it past argumentIsPhrase (closes the phrase-in-a-variable
 * residual).
 *
 * Robustness: the binding must be a plain string literal AS REAL CODE. The walk runs
 * over the offset-preserving MASK projection, where a real string-literal binding
 * keeps its quote markers (`"`/`'`/`` ` ``) but blanks the payload to spaces, while a
 * declaration spelled inside a STRING LITERAL or comment is blanked away entirely
 * (its keyword/`=`/quotes gone). A name is a phrase variable iff its initializer (a)
 * is a STATIC string literal -- opens with a quote, closes with the matching quote,
 * and (for a backtick) contains no `${...}` interpolation -- AND (b) has interior
 * whitespace in its RAW payload, read from the raw source at the offset-aligned span
 * between the quotes. Single-word string variables, interpolated-template bindings,
 * and non-string bindings (`= path.join(...)`, `= combinedText(out)`, a
 * `for (const x of ...)` loop binding -- which has no `=` and so is never collected)
 * are all excluded.
 *
 * @param {string} source - Raw source (LF-normalized).
 * @param {string} [maskedSource] - Precomputed maskCommentsAndStrings(source); pass
 *   it to avoid recomputing the projection in the per-file hot path.
 * @returns {Set<string>} Phrase-variable names.
 */
function collectPhraseVariables(source, maskedSource) {
  const masked = maskedSource !== undefined ? maskedSource : maskCommentsAndStrings(source);
  const phraseVars = new Set();
  for (const binding of collectDeclaratorBindings(masked)) {
    const init = binding.initializer;
    const quote = init[0];
    if (quote !== '"' && quote !== "'" && quote !== "`") {
      continue;
    }
    // A STATIC string literal: opens and closes with the same quote, and (for a
    // backtick) interpolates nothing. The mask keeps quote markers and `${`/`}`
    // verbatim, so an interpolated template still shows its `${`.
    if (init.length < 2 || init[init.length - 1] !== quote) {
      continue;
    }
    if (quote === "`" && init.includes("${")) {
      continue;
    }
    // Read the RAW payload between the quotes at the offset-aligned span. The
    // opening quote is at initializerStart; the closing quote is the last char of
    // the (trimmed) initializer, whose absolute offset we recompute from the mask.
    const openAbs = binding.initializerStart;
    const closeAbs = openAbs + init.length - 1;
    const payload = source.slice(openAbs + 1, closeAbs);
    if (/\s/.test(payload)) {
      phraseVars.add(binding.name);
    }
  }
  return phraseVars;
}

/**
 * Decide whether a phrase-assertion receiver is COMPLIANT (normalized) given the
 * set of tainted raw-output variables in the file.
 *
 * @param {string} receiver - Trimmed receiver source from `expect(RECEIVER)`.
 * @param {Set<string>} taintedVars - Names bound to raw pwsh-output merges.
 * @returns {{ flagged: boolean, reason?: string }}
 */
function expressionHasRawPwshMember(expression, expressionStart, context) {
  const receiverMask = maskCommentsAndStrings(expression);
  const memberRe = /\b([A-Za-z_$][\w$]*)\s*\.\s*(?:stdout|stderr)\b/g;
  let match;
  while ((match = memberRe.exec(receiverMask)) !== null) {
    if (isPwshResultAt(context.variableBindings, match[1], expressionStart + match.index)) {
      return true;
    }
  }
  return false;
}

function activeOutputBindingAt(bindings, name, index) {
  let active = null;
  for (const binding of bindings) {
    if (binding.name !== name || binding.start > index || binding.end <= index) {
      continue;
    }
    if (active === null || binding.start > active.start) {
      active = binding;
    }
  }
  return active;
}

function collectTaintedRawPwshBindings(masked, context) {
  const raw = [];
  for (const binding of collectDeclaratorBindings(masked)) {
    if (
      (isRawMergeExpression(binding.initializer) || isBareRawOutputMember(binding.initializer)) &&
      expressionHasRawPwshMember(binding.initializer, binding.initializerStart, context)
    ) {
      const active = activeOutputBindingAt(
        context.variableBindings,
        binding.name,
        binding.initializerStart
      );
      if (active) {
        raw.push(active);
      }
    }
  }
  return raw;
}

function classifyReceiver(receiver, receiverStart, context) {
  // Compliant: the receiver is itself a normalizing-helper call.
  const callee = outermostCalleeName(receiver);
  if (callee !== null && NORMALIZING_HELPERS.has(callee)) {
    return { flagged: false };
  }

  // Violation (b): a bare identifier that is a tainted raw-output variable.
  if (
    /^[A-Za-z_$][\w$]*$/.test(receiver) &&
    isRawMergeVariableAt(
      context.variableBindings,
      context.rawMergeBindings,
      receiver,
      receiverStart
    )
  ) {
    return { flagged: true, reason: `raw pwsh-output variable '${receiver}'` };
  }

  // Violation (a): a member access / merge expression that touches .stdout or
  // .stderr without being wrapped by a normalizing helper. (A wrapped receiver
  // hit the compliant branch above because its outermost callee is the helper.)
  if (expressionHasRawPwshMember(receiver, receiverStart, context)) {
    return { flagged: true, reason: `raw pwsh-output member access \`${receiver.trim()}\`` };
  }

  return { flagged: false };
}

/**
 * Count the run of backslashes immediately preceding index `i` in `s`. An ODD count
 * means the character at `i` is backslash-escaped; an EVEN count (including zero)
 * means it is not. Used to tell a real `${` interpolation opener from a literal,
 * backslash-escaped `\${`.
 *
 * @param {string} s
 * @param {number} i - Index of the character whose escaping is in question.
 * @returns {number} Number of consecutive `\` ending at `i - 1`.
 */
function precedingBackslashRun(s, i) {
  let count = 0;
  let k = i - 1;
  while (k >= 0 && s[k] === "\\") {
    count++;
    k--;
  }
  return count;
}

/**
 * Remove the `${...}` interpolation segments from a template literal's INNER text
 * (the chars between the backticks), leaving only the fixed literal fragments. The
 * scan is brace-balanced AND quote/escape-aware:
 *   - QUOTE-AWARE: while skipping a `${...}` span it tracks string/template quote
 *     state, so a `}` that appears INSIDE a string or template literal in the
 *     interpolation (e.g. `${ out["x }y z"] }`) does NOT end the span early.
 *   - ESCAPE-AWARE: a backslash-escaped `\${` is LITERAL text, not an interpolation
 *     opener, so the words after it (`prefix \${a b}` -> `a b`) count as fixed text.
 * A nested `${ a ? `${b}` : c }` interpolation is dropped whole, and the fixed text
 * on either side of each `${...}` is preserved. Used to decide whether the fixed
 * words of an interpolated template still form a width-wrappable phrase (closes the
 * interpolated-argument residual): the runtime substitution only widens the rendered
 * text, so a word boundary in the FIXED text is a real wrap point regardless of what
 * the interpolation expands to.
 *
 * @param {string} inner - Template inner text (between the backticks), no quotes.
 * @returns {string} The fixed literal fragments concatenated, interpolations gone.
 */
function stripTemplateInterpolations(inner) {
  let out = "";
  let i = 0;
  while (i < inner.length) {
    // A `${` opens an interpolation only when the `$` is NOT backslash-escaped.
    if (inner[i] === "$" && inner[i + 1] === "{" && precedingBackslashRun(inner, i) % 2 === 0) {
      // Skip the `${ ... }` span. Track brace depth AND string/template quote
      // state so a `}` inside a string literal in the interpolation cannot end the
      // span early.
      let depth = 0;
      let quote = null;
      i += 2;
      while (i < inner.length) {
        const ch = inner[i];
        if (quote) {
          if (ch === quote && precedingBackslashRun(inner, i) % 2 === 0) {
            quote = null;
          }
          i++;
          continue;
        }
        if (ch === '"' || ch === "'" || ch === "`") {
          quote = ch;
          i++;
          continue;
        }
        if (ch === "{") {
          depth++;
        } else if (ch === "}") {
          if (depth === 0) {
            i++;
            break;
          }
          depth--;
        }
        i++;
      }
      continue;
    }
    out += inner[i];
    i++;
  }
  return out;
}

/**
 * A `.toContain(arg)` argument is a "phrase" when it is a multi-word value that a
 * console word-wrap could split. That is true when the argument is:
 *   - a plain string literal (`'...'` / `"..."` / un-interpolated `` `...` ``) whose
 *     text has interior whitespace; OR
 *   - an INTERPOLATED template (`` `...${x}...` ``) whose FIXED text (the literal
 *     fragments left after `${...}` segments are removed) still has interior
 *     whitespace -- the interpolation only widens the rendered string, so a word
 *     boundary in the fixed text is a genuine wrap point; OR
 *   - a bare IDENTIFIER that is a known phrase variable (a `const|let|var` bound to
 *     a multi-word string literal elsewhere in the file; see collectPhraseVariables).
 * A single-token literal, an interpolated template with no fixed word boundary, or a
 * non-phrase identifier/expression is not flagged. `.toMatch(...)` is always treated
 * as a phrase assertion (regexes routinely span multiple words).
 *
 * @param {string} matcher - "toContain" | "toMatch".
 * @param {string} argText - Raw text of the matcher's first argument.
 * @param {Set<string>} [phraseVars] - Names bound to multi-word string literals.
 * @returns {boolean}
 */
function argumentIsPhrase(matcher, argText, phraseVars) {
  if (matcher === "toMatch") {
    return true;
  }
  const trimmed = argText.trim();
  // Bare identifier argument: a phrase iff it is a known phrase variable.
  if (/^[A-Za-z_$][\w$]*$/.test(trimmed)) {
    return phraseVars instanceof Set && phraseVars.has(trimmed);
  }
  // String / template literal: '...' | "..." | `...`. Require interior whitespace
  // to count as a multi-word phrase.
  const literal = /^(["'`])([\s\S]*)\1$/.exec(trimmed);
  if (!literal) {
    return false;
  }
  if (literal[1] === "`" && /\$\{/.test(literal[2])) {
    // Interpolated template: it is a phrase when the FIXED text that survives the
    // removal of every `${...}` segment still has a wrappable interior boundary.
    return /\s/.test(stripTemplateInterpolations(literal[2]));
  }
  return /\s/.test(literal[2]);
}

/**
 * Scan a single test file's source for the anti-pattern. Returns an array of
 * `{ line, matcher, receiver, reason }` violations.
 *
 * The `expect(...)` call sites and the matcher chain are LOCATED on the
 * offset-preserving MASK projection (maskCommentsAndStrings), so an `expect(...)`
 * spelled inside a COMMENT or a STRING LITERAL (this guard's own self-test data, an
 * error-message string, docs) is blanked away and never scanned -- it is invisible to
 * the locator. Because the mask is offset-aligned 1:1 with the raw source, the
 * RECEIVER and the matcher ARGUMENT text are then read from the RAW source at the
 * aligned offsets, so phrase analysis sees the exact, real string content. The two
 * collectors run over the same MASK projection (computed once here and passed in).
 *
 * @param {string} source - Raw source (LF-normalized).
 * @returns {Array<{line:number, matcher:string, receiver:string, reason:string}>}
 */
function findRawPwshPhraseAssertions(source) {
  if (!spawnsPwsh(source)) {
    return [];
  }
  // Compute each projection ONCE for this file and thread it through.
  const masked = maskCommentsAndStrings(source);
  const outputContext = collectPwshOutputContext(source, masked);
  outputContext.rawMergeBindings = collectTaintedRawPwshBindings(masked, outputContext);
  const phraseVars = collectPhraseVariables(source, masked);
  const violations = [];

  // Locate `expect(` on the MASK: a comment/string `expect(` is blanked, so only
  // real-code call sites survive. Offsets align 1:1 with raw.
  const expectRe = /\bexpect\s*\(/g;
  let m = expectRe.exec(masked);
  while (m !== null) {
    const openIndex = m.index + m[0].length - 1;
    const closeIndex = matchParen(masked, openIndex);
    if (closeIndex === -1) {
      m = expectRe.exec(masked);
      continue;
    }
    // Read the receiver from RAW source at the aligned offsets (real text).
    const receiver = source.slice(openIndex + 1, closeIndex).trim();

    // Walk the matcher chain after `expect(...)` on the MASK: an optional `.not`,
    // then the matcher call. Allow whitespace between tokens.
    const tail = masked.slice(closeIndex + 1);
    const chain = /^\s*(?:\.\s*not\s*)?\.\s*([A-Za-z_$][\w$]*)\s*\(/.exec(tail);
    if (chain && PHRASE_MATCHERS.has(chain[1])) {
      const matcher = chain[1];
      const matcherOpen = closeIndex + 1 + chain.index + chain[0].length - 1;
      const matcherClose = matchParen(masked, matcherOpen);
      if (matcherClose !== -1) {
        // Read the argument list from RAW source at the aligned offsets so the
        // phrase string content is the real text, not a blanked mask.
        const argList = source.slice(matcherOpen + 1, matcherClose);
        const firstArg = splitTopLevelArgs(argList)[0] || "";
        if (argumentIsPhrase(matcher, firstArg, phraseVars)) {
          const verdict = classifyReceiver(receiver, openIndex + 1, outputContext);
          if (verdict.flagged) {
            violations.push({
              line: lineNumberAt(source, m.index),
              matcher,
              receiver,
              reason: verdict.reason
            });
          }
        }
      }
    }

    m = expectRe.exec(masked);
  }

  return violations;
}

/**
 * Split a call's argument-list source into TOP-LEVEL comma-separated arguments,
 * ignoring commas nested in parens/brackets/braces/strings/templates. Returns the
 * trimmed text of each argument.
 *
 * @param {string} argsText - Text between a call's outermost parens.
 * @returns {string[]}
 */
function splitTopLevelArgs(argsText) {
  const args = [];
  let depth = 0;
  let current = "";
  let quote = null;
  for (let i = 0; i < argsText.length; i++) {
    const ch = argsText[i];
    const prev = i > 0 ? argsText[i - 1] : "";
    if (quote) {
      current += ch;
      if (ch === quote && prev !== "\\") {
        quote = null;
      }
      continue;
    }
    if (ch === '"' || ch === "'" || ch === "`") {
      quote = ch;
      current += ch;
      continue;
    }
    if (ch === "(" || ch === "[" || ch === "{") {
      depth++;
      current += ch;
      continue;
    }
    if (ch === ")" || ch === "]" || ch === "}") {
      depth--;
      current += ch;
      continue;
    }
    if (ch === "," && depth === 0) {
      args.push(current.trim());
      current = "";
      continue;
    }
    current += ch;
  }
  if (current.trim().length > 0) {
    args.push(current.trim());
  }
  return args;
}

// ---------------------------------------------------------------------------
// "Renamed helper" door: a LOCALLY-DEFINED helper that merges raw pwsh output
// without normalizing.
//
// The receiver scanner above recognizes `combinedText(...)` / `stdoutText(...)`
// by NAME. That leaves a bypass: define a DIFFERENTLY-named helper whose body
// reconstructs the same raw `${out.stdout}\n${out.stderr}` merge but omits
// `normalizePwshText`, then phrase-assert on its result -- the call-site receiver
// is just `myHelper(out)`, which the name-based check waves through. The
// width-wrap regression rides back in through that helper.
//
// We kill that class at the SOURCE: a helper DEFINITION (named `return` body or
// arrow implicit-return) whose value is a TEMPLATE LITERAL interpolating
// `.stdout`/`.stderr` and is NOT wrapped by a normalizing helper IS the raw merge,
// regardless of what it is later called. Flagging the definition needs no
// call-graph and is robust: the merge is dead on arrival.
//
// Robustness: the scan runs over COMMENT/STRING-STRIPPED source
// (stripJsCommentsAndStrings), so the word "return" in prose, a backtick phrase
// in a comment, or a merge spelled out inside a STRING LITERAL (this guard's own
// self-test data, error-message text, docs) cannot false-positive -- only real
// code is seen. We scope to template-literal merges specifically: that is the
// exact shape combinedText uses, and it is what distinguishes a raw merge from
// the legitimate `return { stdout, stderr, status }` result-shaper and from
// structural single-member reads like `return result.stdout.split(...)` (a member
// chain, not a backtick), neither of which is flagged. See "Residual limits" in
// the file header for the merge spellings this intentionally does NOT cover.
// ---------------------------------------------------------------------------

/**
 * Read the expression that begins at `start` (on COMMENT/STRING-STRIPPED or MASKED
 * source) up to its top-level terminator: a `;` / `,` at bracket depth 0, or the
 * first UNMATCHED close bracket (the `}` / `)` that ends the enclosing body). Bracket
 * depth is tracked (via declaratorInitializerEnd) so an object-literal `{...}`, call
 * `(...)`, or array `[...]` inside the expression is read whole. String/template
 * payloads were already neutralized (stripped or masked to spaces) so quotes need no
 * special handling here.
 *
 * @param {string} source - Comment/string-stripped or masked source.
 * @param {number} start - Index just past the `return` keyword, `=>` token, or `=`.
 * @returns {string} The trimmed expression text.
 */
function readReturnExpression(source, start) {
  return readReturnExpressionInfo(source, start).expr;
}

function readReturnExpressionInfo(source, start) {
  let exprStart = start;
  while (exprStart < source.length && /\s/.test(source[exprStart])) {
    exprStart++;
  }
  const { end } = declaratorInitializerEnd(source, exprStart);
  return { expr: source.slice(exprStart, end).trim(), start: exprStart };
}

/**
 * Strip a SINGLE layer of enclosing parens from `expr` when the opening `(` matches
 * the final `)` (i.e. the whole expression is parenthesized). This lets a
 * paren-wrapped merge (`(`${a.stdout}` + `${a.stderr}`)`) be seen as the merge it is,
 * and lets a paren-wrapped normalizing call (`(normalizePwshText(...))`) be recognized
 * as compliant. Operates on masked/stripped source, so quotes inside need no special
 * handling (matchParen already skips quoted spans).
 *
 * @param {string} expr - Trimmed expression text (masked/stripped source).
 * @returns {string} `expr` with one enclosing paren layer removed, or `expr`.
 */
function stripEnclosingParens(expr) {
  const e = expr.trim();
  if (e.startsWith("(")) {
    const close = matchParen(e, 0);
    if (close === e.length - 1) {
      return e.slice(1, -1).trim();
    }
  }
  return e;
}

/**
 * Split `expr` on TOP-LEVEL `+` operators (depth 0), ignoring `+` nested in
 * parens/brackets/braces/strings/templates and ignoring `++`. Returns the trimmed
 * text of each operand. Operates on masked/stripped source; string payloads are
 * blanked but quote markers survive, so the quote-tracking keeps a `+` inside a
 * string literal from splitting.
 *
 * @param {string} expr - Trimmed expression text (masked/stripped source).
 * @returns {string[]} Top-level `+`-separated operands (length 1 when no top `+`).
 */
function splitTopLevelPlus(expr) {
  const operands = [];
  let depth = 0;
  let quote = null;
  let current = "";
  for (let i = 0; i < expr.length; i++) {
    const ch = expr[i];
    const prev = i > 0 ? expr[i - 1] : "";
    if (quote) {
      current += ch;
      if (ch === quote && prev !== "\\") {
        quote = null;
      }
      continue;
    }
    if (ch === '"' || ch === "'" || ch === "`") {
      quote = ch;
      current += ch;
      continue;
    }
    if (ch === "(" || ch === "[" || ch === "{") {
      depth++;
      current += ch;
      continue;
    }
    if (ch === ")" || ch === "]" || ch === "}") {
      depth--;
      current += ch;
      continue;
    }
    if (ch === "+" && depth === 0 && expr[i + 1] !== "+" && prev !== "+") {
      operands.push(current.trim());
      current = "";
      continue;
    }
    current += ch;
  }
  operands.push(current.trim());
  return operands;
}

/**
 * True when an operand is a BARE raw-output member: its trimmed text (after dropping
 * one enclosing paren layer and an optional `|| "<...>"` guard) ENDS in `.stdout` /
 * `.stderr` with nothing after it. Recognizes `o.stdout`, `run.stderr`, and the
 * guarded `(o.stdout || "")` form. A member with a trailing accessor/call
 * (`a.stdout.length`, `r.stdout.indexOf(x)`) is NOT bare -- the trailing `.length` /
 * `.indexOf(...)` means the value is no longer the raw output string itself.
 *
 * @param {string} operand - Trimmed operand text (masked/stripped source).
 * @returns {boolean}
 */
function isBareRawOutputMember(operand) {
  let o = stripEnclosingParens(operand);
  // Drop a `|| "<fallback>"` guard (the combinedText spelling `o.stdout || ""`).
  o = o.replace(/\|\|\s*(["'`])[\s\S]*?\1\s*$/, "").trim();
  o = o.replace(/\?\?\s*(["'`])[\s\S]*?\1\s*$/, "").trim();
  o = stripEnclosingParens(o);
  return /\.\s*(?:stdout|stderr)\s*$/.test(o);
}

/**
 * True when an operand is itself a STRING or TEMPLATE literal -- it opens and closes
 * with the same quote/backtick. On masked/stripped source a string payload is blanked
 * but the quote markers survive, so `"\n"` reads as `"  "` and still matches. This is
 * the "proof of string concatenation" half of the `+`-merge rule.
 *
 * @param {string} operand - Trimmed operand text (masked/stripped source).
 * @returns {boolean}
 */
function isStringOrTemplateOperand(operand) {
  const o = operand.trim();
  return o.length >= 2 && /^["'`]/.test(o) && o[o.length - 1] === o[0];
}

/**
 * True when an operand is a TEMPLATE LITERAL that interpolates `.stdout`/`.stderr`
 * (`` `${a.stdout}\n` ``) -- it opens and closes with a backtick and touches a raw
 * member. Such an operand is itself raw output rendered as a string, so a `+` chain
 * of these is a string merge of raw output (`` `${a.stdout}` + `${a.stderr}` ``). This
 * is the "touches raw output" half of the `+`-merge rule, alongside a bare member.
 *
 * @param {string} operand - Trimmed operand text (masked/stripped source).
 * @returns {boolean}
 */
function isTemplateInterpolatingRawOutput(operand) {
  const o = operand.trim();
  return o.length >= 2 && o[0] === "`" && o[o.length - 1] === "`" && STDOUT_STDERR_MEMBER.test(o);
}

/**
 * True when a returned / arrow-body / initializer expression is a RAW MERGE of pwsh
 * output that is NOT wrapped by a recognized normalizing helper. After dropping one
 * enclosing paren layer (so a parenthesized merge is seen), two merge spellings count,
 * and they are the SAME definition used by the tainted-local scan:
 *   - a TEMPLATE LITERAL interpolating `.stdout`/`.stderr`
 *     (`` `${a.stdout}\n${a.stderr}` ``); OR
 *   - a top-level `+` CONCATENATION that is genuine STRING concatenation of raw
 *     output: at least one top-level operand is a BARE raw-output member
 *     (`o.stdout`, `run.stderr`, or the guarded `(o.stdout || "")`) AND at least one
 *     top-level operand is a STRING/TEMPLATE literal -- proving string concat, not
 *     arithmetic (`a.stdout + "\n" + a.stderr`).
 * `normalizePwshText(`${a.stdout}...`)` / `normalizePwshText(a.stdout + b)` and the
 * by-convention `combinedText(...)` / `stdoutText(...)` are compliant because the
 * outermost callee is a normalizing helper. NOT raw merges (not flagged): a member
 * CHAIN with no top-level `+` (`result.stdout.split(...)`); a result-shaper object
 * literal (`{ stdout, stderr, status }`, shorthand properties, no leading dot);
 * ARITHMETIC `+` touching raw output (`a.stdout.length + b.length`,
 * `idx + r.stdout.indexOf(x)`, `result.stdout.length + 1`) -- no bare member and/or
 * no string operand; and a `+` with a string but no bare raw member (`"x" + y`).
 *
 * @param {string} expr - Trimmed return/arrow-body/initializer expression (masked/stripped).
 * @returns {boolean}
 */
function isRawMergeExpression(expr) {
  const core = stripEnclosingParens(expr);
  const callee = outermostCalleeName(core);
  if (callee !== null && NORMALIZING_HELPERS.has(callee)) {
    return false;
  }
  if (!STDOUT_STDERR_MEMBER.test(core)) {
    return false;
  }
  const operands = splitTopLevelPlus(core);
  if (operands.length === 1) {
    // No top-level `+`. The only single-operand raw merge is a TEMPLATE LITERAL
    // that interpolates `.stdout`/`.stderr` (`` `${a.stdout}\n${a.stderr}` ``, the
    // spelling this repo uses) -- it opens with a backtick. A bare single member
    // (`result.stdout`) or a member chain (`result.stdout.split(...)`) is NOT a
    // merge (the bare single member is the documented intentional residual).
    return core.startsWith("`");
  }
  // Genuine STRING concatenation of raw output: require BOTH (a) a RAW-OUTPUT
  // operand -- a bare member (`o.stdout`, `(o.stdout || "")`) or a template literal
  // that interpolates a raw member (`` `${o.stdout}\n` ``) -- AND (b) a
  // string/template-literal operand (proof of string concat, not arithmetic). This
  // rejects ARITHMETIC `+` touching raw output (`a.stdout.length + b.length`,
  // `idx + r.stdout.indexOf(x)`, `result.stdout.length + 1`) -- no raw-output
  // operand -- and a string-only `+` with no raw-output operand (`"x" + y`).
  const hasRawOutputOperand = operands.some(
    (op) => isBareRawOutputMember(op) || isTemplateInterpolatingRawOutput(op)
  );
  const hasStringLiteral = operands.some(isStringOrTemplateOperand);
  return hasRawOutputOperand && hasStringLiteral;
}

function parseParamNames(source, argsOpen, argsClose) {
  return splitTopLevelArgs(source.slice(argsOpen + 1, argsClose)).map((param) => {
    const match = /^([A-Za-z_$][\w$]*)\b/.exec(param.trim());
    return match ? match[1] : "";
  });
}

function rawMergeReceiverNames(expr) {
  const names = new Set();
  const memberRe = /\b([A-Za-z_$][\w$]*)\s*\.\s*(?:stdout|stderr)\b/g;
  let match;
  while ((match = memberRe.exec(expr)) !== null) {
    names.add(match[1]);
  }
  return names;
}

function previousNonWhitespaceIndex(source, index) {
  for (let i = index - 1; i >= 0; i--) {
    if (!/\s/.test(source[i])) {
      return i;
    }
  }
  return -1;
}

function helperScopeForIndex(source, index) {
  const stack = [];
  for (let i = 0; i < index; i++) {
    if (source[i] === "{") {
      stack.push(i);
    } else if (source[i] === "}" && stack.length > 0) {
      stack.pop();
    }
  }
  const open = stack.length > 0 ? stack[stack.length - 1] : -1;
  if (open < 0) {
    return { start: 0, end: source.length };
  }
  const close = matchBracket(source, open, "{", "}");
  return { start: open + 1, end: close >= 0 ? close : source.length };
}

function helperCalledWithPwshResult(hit, source, masked, context) {
  const receiverNames = rawMergeReceiverNames(hit.expr);
  const parameterIndexes = [];
  hit.params.forEach((param, index) => {
    if (param && receiverNames.has(param)) {
      parameterIndexes.push(index);
    }
  });
  if (parameterIndexes.length === 0) {
    return false;
  }

  const callRe = new RegExp(String.raw`\b${hit.name}\s*\(`, "g");
  let match;
  while ((match = callRe.exec(masked)) !== null) {
    if (match.index < hit.scopeStart || match.index >= hit.scopeEnd) {
      continue;
    }
    const prev = previousNonWhitespaceIndex(masked, match.index);
    if (prev >= 0 && masked[prev] === ".") {
      continue;
    }
    if (/\bfunction\s*$/.test(masked.slice(Math.max(0, match.index - 32), match.index))) {
      continue;
    }
    const openIndex = match.index + match[0].lastIndexOf("(");
    const closeIndex = matchParen(masked, openIndex);
    if (closeIndex < 0) {
      continue;
    }
    const args = splitTopLevelArgs(source.slice(openIndex + 1, closeIndex));
    for (const parameterIndex of parameterIndexes) {
      const arg = args[parameterIndex]?.trim() ?? "";
      if (
        /^[A-Za-z_$][\w$]*$/.test(arg) &&
        isPwshResultAt(context.variableBindings, arg, match.index)
      ) {
        return true;
      }
    }
  }
  return false;
}

function rawMergeHelperTouchesPwshOutput(hit, source, masked, context) {
  if (expressionHasRawPwshMember(hit.expr, hit.exprStart, context)) {
    return true;
  }
  if (!hit.name) {
    return false;
  }
  return helperCalledWithPwshResult(hit, source, masked, context);
}

/**
 * Find "renamed helper" raw-merge definitions in a pwsh-spawning test file: a
 * `return <template merge>` or an arrow implicit-return `=> <template merge>`
 * whose value is an un-normalized `.stdout`/`.stderr` template merge. Operates on
 * comment/string-stripped source so prose and string-literal data cannot
 * false-positive. Non-pwsh-spawning files are skipped (their stdout is not real
 * ConciseView output).
 *
 * @param {string} rawSource - Raw source (any line endings).
 * @returns {Array<{line:number, kind:string, expr:string}>}
 */
function findRenamedMergeHelpers(rawSource) {
  return findRenamedMergeHelpersFromFixer(rawSource);
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe("pwsh-output-assertion-policy (repo-wide)", () => {
  test("no pwsh-spawning test asserts a phrase against RAW (un-normalized) pwsh output", () => {
    const offenders = [];

    for (const abs of listTestFiles()) {
      const relative = path.relative(REPO_ROOT, abs);
      if (relative === SELF) {
        continue;
      }
      const source = readUtf8(abs, { stripBom: false });
      for (const hit of findRawPwshPhraseAssertions(source)) {
        offenders.push({ file: toRepoRelative(abs), ...hit });
      }
    }

    if (offenders.length > 0) {
      const details = offenders
        .map((o) => `  ${o.file}:${o.line} [.${o.matcher} on ${o.reason}]`)
        .join("\n");
      throw new Error(
        "pwsh-output-assertion-policy violation: phrase assertion(s) run against RAW " +
          "PowerShell-rendered output.\n" +
          "When `pwsh -File` renders an unhandled `throw`, PowerShell's ConciseView formatter " +
          "WORD-WRAPS the message at the host console width (inserting a `\\n     | ` gutter), " +
          'splitting phrases like "outside the managed root" on the narrower Windows runner -- ' +
          "the assertion then FAILS on Windows only and rots silently on Linux.\n" +
          "FIX: normalize the output before the phrase assertion via normalizePwshText(...) " +
          "(scripts/lib/pwsh-output.js), or a recognized wrapper helper " +
          "(combinedText(...) / stdoutText(...)), e.g.\n" +
          "  const combined = combinedText(out); // = normalizePwshText(`${out.stdout}\\n${out.stderr}`)\n" +
          '  expect(combined).toContain("outside the managed root");\n\n' +
          "Offending assertions:\n" +
          details
      );
    }
  });

  test("no pwsh-spawning test defines a 'renamed helper' that merges raw pwsh output without normalizing", () => {
    const offenders = [];

    for (const abs of listTestFiles()) {
      const relative = path.relative(REPO_ROOT, abs);
      if (relative === SELF) {
        continue;
      }
      const source = readUtf8(abs, { stripBom: false });
      for (const hit of findRenamedMergeHelpers(source)) {
        offenders.push({ file: toRepoRelative(abs), ...hit });
      }
    }

    if (offenders.length > 0) {
      const details = offenders
        .map((o) => `  ${o.file}:${o.line} [${o.kind} of raw merge \`${o.expr}\`]`)
        .join("\n");
      throw new Error(
        "pwsh-output-assertion-policy violation: a locally-defined helper merges RAW " +
          "PowerShell output without normalizing it.\n" +
          "A helper that returns `${run.stdout}\\n${run.stderr}` (or an arrow `=> `${...}``) " +
          "rebuilds the width-wrap-fragile raw merge under a NEW name, so phrase assertions " +
          "on its result dodge the receiver guard and rot on the narrower Windows runner.\n" +
          "FIX: wrap the merge in normalizePwshText(...) (scripts/lib/pwsh-output.js) inside the " +
          "helper, e.g.\n" +
          "  function combinedText(run) {\n" +
          '    return normalizePwshText(`${run.stdout || ""}\\n${run.stderr || ""}`);\n' +
          "  }\n" +
          "(or add the helper name to NORMALIZING_HELPERS only if it provably routes through " +
          "normalizePwshText).\n\n" +
          "Offending helper definitions:\n" +
          details
      );
    }
  });

  test("test-file discovery includes nested test directories", () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "pwsh-policy-tests-"));
    try {
      const nested = path.join(root, "nested", "deeper");
      fs.mkdirSync(nested, { recursive: true });
      fs.writeFileSync(path.join(root, "top.test.js"), '"use strict";\n', "utf8");
      fs.writeFileSync(path.join(nested, "child.test.js"), '"use strict";\n', "utf8");
      fs.writeFileSync(path.join(nested, "not-a-test.js"), '"use strict";\n', "utf8");

      const discovered = listTestFilesUnder(root)
        .map((file) => path.relative(root, file).split(path.sep).join("/"))
        .sort();

      expect(discovered).toEqual(["nested/deeper/child.test.js", "top.test.js"]);
    } finally {
      fs.rmSync(root, { recursive: true, force: true });
    }
  });

  // -------------------------------------------------------------------------
  // Self-tests: prove the detector fires on the exact anti-patterns and does
  // NOT false-positive on the compliant shapes, using crafted source strings.
  //
  // The two large, uniform families -- "does findRawPwshPhraseAssertions(src)
  // return N hits?" and "does findRenamedMergeHelpers(src) return N hits?" -- are
  // DATA-DRIVEN via test.each tables (RAW_PHRASE_CASES / RENAMED_MERGE_CASES), one
  // row per crafted source. Each row keeps its original descriptive `name` and its
  // expected `hits`; FLAGGED rows expect 1 and near-miss COMPLIANT rows expect 0.
  // Per-row `//` comments carry the rationale that used to live in section banners
  // (CLOSED RESIDUAL 1/2/4, B1/B2/B3/M1/M2/M3). The remaining direct-helper unit
  // tests (multi-assertion or non-length shapes) stay as individual test() blocks
  // below, where tabulating would obscure them.
  // -------------------------------------------------------------------------
  describe("detector self-tests", () => {
    const SPAWN =
      'const result = spawnSync("pwsh", ["-File", s], {});\n' +
      'const out = spawnSync("pwsh", ["-File", s], {});\n' +
      'const r = spawnSync("pwsh", ["-File", s], {});\n';

    // Each row is one crafted source fed through findRawPwshPhraseAssertions.
    // `hits: 1` == FLAGGED (the anti-pattern fires); `hits: 0` == COMPLIANT
    // (the near-miss shape must NOT false-positive).
    const RAW_PHRASE_CASES = [
      // --- Direct member-access / merge / tainted-local FLAGGED forms ---
      {
        name: "flags a member-access phrase assertion on result.stdout",
        src: SPAWN + 'expect(result.stdout).toContain("outside the managed root");',
        hits: 1
      },
      {
        name: "flags a raw merge template-literal receiver",
        src: SPAWN + 'expect(`${out.stdout}\\n${out.stderr}`).toContain("cannot mutate editors");',
        hits: 1
      },
      {
        name: "flags a tainted local variable bound to a raw merge",
        src:
          SPAWN +
          'const combined = `${out.stdout || ""}\\n${out.stderr || ""}`;\n' +
          'expect(combined).toContain("outside the managed root");',
        hits: 1
      },
      {
        name: "flags a tainted local variable bound to a raw stdout alias",
        src:
          SPAWN +
          'const stdout = out.stdout || "";\n' +
          'expect(stdout).toContain("JSON=Installing Android NDK...");',
        hits: 1
      },
      {
        name: "flags a .not.toContain phrase on raw output",
        src: SPAWN + 'expect(result.stdout).not.toContain("cannot be found on this object");',
        hits: 1
      },
      {
        name: "flags a .toMatch regex on raw output",
        src: SPAWN + "expect(out.stderr).toMatch(/did not produce NUnit results/);",
        hits: 1
      },
      {
        name: "flags a member access on a .stdout member nested in a merge",
        src: SPAWN + 'expect(`${result.stdout}`).toContain("multi word phrase");',
        hits: 1
      },
      {
        name: "flags a parenthesized PowerShell spawn result binding",
        src:
          'const result = (spawnSync("pwsh", ["-File", s], {}));\n' +
          'expect(result.stdout).toContain("outside the managed root");',
        hits: 1
      },
      {
        name: "flags a ternary PowerShell spawn result binding",
        src:
          'const result = usePwsh ? spawnSync("pwsh", ["-File", s], {}) : spawnSync("git", ["status"], {});\n' +
          'expect(result.stderr).toContain("outside the managed root");',
        hits: 1
      },
      {
        name: "flags a helper result whose helper returns a parenthesized PowerShell spawn",
        src:
          "function run() {\n" +
          '  return (spawnSync("pwsh", ["-File", s], {}));\n' +
          "}\n" +
          "const result = run();\n" +
          'expect(result.stderr).toContain("outside the managed root");',
        hits: 1
      },
      {
        name: "flags a helper result when the helper returns pwsh inside control flow",
        src:
          "function run() {\n" +
          "  if (process.env.USE_PWSH) {\n" +
          '    return spawnSync("pwsh", ["-File", s], {});\n' +
          "  }\n" +
          '  return spawnSync("git", ["status"], {});\n' +
          "}\n" +
          "const result = run();\n" +
          'expect(result.stderr).toContain("outside the managed root");',
        hits: 1
      },
      {
        name: "flags an object method helper result when the method returns pwsh",
        src:
          "const runner = {\n" +
          '  run() { return spawnSync("pwsh", ["-File", s], {}); }\n' +
          "};\n" +
          "const result = runner.run();\n" +
          'expect(result.stderr).toContain("outside the managed root");',
        hits: 1
      },
      {
        name: "flags nested object method helper results without flattening sibling method names",
        src:
          "const runner = {\n" +
          '  nested: { run() { return spawnSync("pwsh", ["-File", s], {}); } },\n' +
          '  run() { return spawnSync("git", ["status"], {}); }\n' +
          "};\n" +
          "const result = runner.nested.run();\n" +
          'expect(result.stderr).toContain("outside the managed root");\n' +
          "const git = runner.run();\n" +
          'expect(git.stdout).toContain("working tree clean");',
        hits: 1
      },
      {
        name: "does NOT trust an expression-bodied nested arrow inside a non-PowerShell helper",
        src:
          "function runGit() {\n" +
          '  return useCallback ? (() => spawnSync("pwsh", ["-Command", "exit 0"], {})) : spawnSync("git", ["status"], {});\n' +
          "}\n" +
          "const result = runGit();\n" +
          'expect(result.stdout).toContain("working tree clean");',
        hits: 0
      },
      {
        name: "does NOT let function parameters inherit an outer PowerShell result binding",
        src:
          SPAWN +
          "function assertGit(result) {\n" +
          '  expect(result.stdout).toContain("working tree clean");\n' +
          "}\n" +
          'expect(result.stderr).toContain("outside the managed root");',
        hits: 1
      },
      {
        name: "flags a var pwsh result asserted after its nested block",
        src:
          'if (process.env.USE_PWSH) {\n  var result = spawnSync("pwsh", ["-File", s], {});\n}\n' +
          'expect(result.stderr).toContain("outside the managed root");',
        hits: 1
      },
      {
        name: "flags a var pwsh result asserted after a nested block inside an object method",
        src:
          "const suite = {\n" +
          "  run() {\n" +
          '    if (ok) { var result = spawnSync("pwsh", ["-File", s], {}); }\n' +
          '    expect(result.stderr).toContain("outside the managed root");\n' +
          "  }\n" +
          "};",
        hits: 1
      },
      {
        name: "flags var pwsh results inside quoted computed and numeric object methods only inside those methods",
        src:
          "const suite = {\n" +
          '  "quoted"() {\n' +
          '    var quotedResult = spawnSync("pwsh", ["-File", s], {});\n' +
          '    expect(quotedResult.stderr).toContain("outside the managed root");\n' +
          "  },\n" +
          '  ["computed"]() {\n' +
          '    var computedResult = spawnSync("pwsh", ["-File", s], {});\n' +
          '    expect(computedResult.stderr).toContain("outside the managed root");\n' +
          "  },\n" +
          "  7() {\n" +
          '    var numericResult = spawnSync("pwsh", ["-File", s], {});\n' +
          '    expect(numericResult.stderr).toContain("outside the managed root");\n' +
          "  }\n" +
          "};\n" +
          'expect(quotedResult.stderr).toContain("not in scope here");\n' +
          'expect(computedResult.stderr).toContain("not in scope here");\n' +
          'expect(numericResult.stderr).toContain("not in scope here");',
        hits: 3
      },
      // --- Compliant receivers: normalizePwshText / combinedText / stdoutText ---
      {
        name: "does NOT flag a normalizePwshText-wrapped receiver",
        src:
          SPAWN +
          'expect(normalizePwshText(`${out.stdout}\\n${out.stderr}`)).toContain("outside the managed root");',
        hits: 0
      },
      {
        name: "does NOT flag a combinedText(...) wrapper receiver",
        src: SPAWN + 'expect(combinedText(out)).toContain("outside the managed root");',
        hits: 0
      },
      {
        name: "does NOT flag a variable bound to combinedText(...)",
        src:
          SPAWN +
          "const combined = combinedText(out);\n" +
          'expect(combined).toContain("outside the managed root");',
        hits: 0
      },
      {
        name: "does NOT flag a stdoutText(...) wrapper receiver",
        src: SPAWN + 'expect(stdoutText(result)).toContain("did not write baseline CSV");',
        hits: 0
      },
      // --- Compliant non-phrase / non-pwsh / non-matcher shapes ---
      {
        name: "does NOT flag a single-token .toContain on raw output (cannot be wrapped)",
        src: SPAWN + 'expect(result.stdout).toContain("windows-il2cpp");',
        hits: 0
      },
      {
        name: "does NOT flag a structural last-line read on raw stdout",
        src: SPAWN + "expect(stdout.trim().split(/\\r?\\n/).pop()).toBe(editorExe);",
        hits: 0
      },
      {
        name: "does NOT flag a phrase assertion in a file that does not spawn pwsh",
        src: 'const text = render();\nexpect(result.stdout).toContain("multi word phrase");',
        hits: 0
      },
      {
        name: "does NOT trust object method returns inside a non-PowerShell helper",
        src:
          "function runGit() {\n" +
          '  const probe = { run() { return spawnSync("pwsh", ["-Command", "exit 0"], {}); } };\n' +
          "  probe.run();\n" +
          '  return spawnSync("git", ["status"], {});\n' +
          "}\n" +
          "const result = runGit();\n" +
          'expect(result.stdout).toContain("working tree clean");',
        hits: 0
      },
      {
        name: "does NOT trust quoted computed or numeric method returns inside a non-PowerShell helper",
        src:
          "function runGit() {\n" +
          "  const probe = {\n" +
          '    "quoted"() { return spawnSync("pwsh", ["-Command", "exit 0"], {}); },\n' +
          '    ["computed"]() { return spawnSync("pwsh", ["-Command", "exit 0"], {}); },\n' +
          '    7() { return spawnSync("pwsh", ["-Command", "exit 0"], {}); }\n' +
          "  };\n" +
          '  return spawnSync("git", ["status"], {});\n' +
          "}\n" +
          "const result = runGit();\n" +
          'expect(result.stdout).toContain("working tree clean");',
        hits: 0
      },
      {
        name: "does NOT flag unrelated git output in a file that also probes pwsh",
        src:
          SPAWN +
          'const git = spawnSync("git", ["status"], { encoding: "utf8" });\n' +
          'expect(git.stdout).toContain("working tree clean");',
        hits: 0
      },
      {
        name: "does NOT flag a raw stdout alias for non-PowerShell output",
        src:
          SPAWN +
          'const git = spawnSync("git", ["status"], { encoding: "utf8" });\n' +
          'const stdout = git.stdout || "";\n' +
          'expect(stdout).toContain("working tree clean");',
        hits: 0
      },
      {
        name: "does NOT treat a PowerShell probe in a ternary condition as the result",
        src:
          'const result = spawnSync("pwsh", ["-Command", "exit 0"], {}).status === 0 ?\n' +
          '  spawnSync("git", ["status"], {}) :\n' +
          '  spawnSync("git", ["rev-parse", "HEAD"], {});\n' +
          'expect(result.stdout).toContain("working tree clean");',
        hits: 0
      },
      {
        name: "does NOT count a commented pwsh spawn as a real pwsh spawn",
        src:
          '// const r = spawnSync("pwsh", ["-File", s], {});\n' +
          'const result = spawnSync("git", ["status"], { encoding: "utf8" });\n' +
          'expect(result.stdout).toContain("working tree clean");',
        hits: 0
      },
      {
        name: "does NOT count a fixture-string pwsh spawn as a real pwsh spawn",
        src:
          'const fixture = \'const r = spawnSync("pwsh", ["-File", s], {});\';\n' +
          'const result = spawnSync("git", ["status"], { encoding: "utf8" });\n' +
          'expect(result.stdout).toContain("working tree clean");',
        hits: 0
      },
      {
        name: "does NOT flag a phrase assertion on file content read from disk",
        src:
          SPAWN +
          "const content = fs.readFileSync(p, 'utf8');\n" +
          'expect(content).toContain("multi word phrase");',
        hits: 0
      },
      {
        name: "does NOT flag an .toBe equality on raw stdout (not a phrase matcher)",
        src: SPAWN + 'expect(out.stdout).toBe("False");',
        hits: 0
      },
      {
        // doctor.test.js shape: a synthetic stdout string, no child_process pwsh spawn.
        name: "does NOT flag a synthetic-spawn file (mocked runCommandFn, no real pwsh spawn)",
        src:
          "const result = { status: 0, stdout: 'PowerShell 7.4.0' };\n" +
          'expect(result.stdout).toContain("PowerShell 7.4.0");',
        hits: 0
      },
      // --- CLOSED RESIDUAL 1 -- interpolated-template phrase ARGUMENT on a raw
      // receiver. Flagged when the FIXED text (post-`${...}`-removal) is wrappable;
      // the near-miss (same arg on a NORMALIZED receiver) stays compliant because
      // the receiver check returns first.
      {
        name: "flags an interpolated-template phrase argument on a raw receiver",
        src: SPAWN + "expect(out.stdout).toContain(`saw ${n} outside the managed root`);",
        hits: 1
      },
      {
        // Mirrors unity-perf-baseline-script-contract.test.js:230 -- the receiver is
        // stdoutText(result), so the receiver classification passes before the
        // (now-phrase) interpolated argument is ever consulted.
        name: "does NOT flag an interpolated-template argument on a normalized receiver",
        src: SPAWN + "expect(stdoutText(result)).toContain(`fake unity stdout for ${commit}`);",
        hits: 0
      },
      {
        // After `${analyzerPath}` is removed the fixed text is `-a:""` -- no interior
        // whitespace -> not a wrappable phrase, so even a raw receiver is not flagged.
        name: "does NOT flag an interpolated-template argument with no fixed word boundary on a raw receiver",
        src: SPAWN + 'expect(out.stdout).toContain(`-a:"${analyzerPath}"`);',
        hits: 0
      },
      // --- B2/M3 -- stripTemplateInterpolations is QUOTE- and ESCAPE-aware.
      //   B2: a `}` inside a string literal WITHIN a `${...}` span must not end the
      //       span early (else stray words leak and a non-phrase looks like a phrase).
      //   M3: a backslash-escaped `\${` is LITERAL text (not an interpolation), so its
      //       following words DO count as fixed phrase text.
      {
        // The `}` inside the string "x }y z" must NOT end the `${...}` span early; the
        // fixed text is just `pre`+`post` (no interior whitespace) -> not a phrase.
        name: "does NOT flag an interpolated arg whose `${...}` contains a quoted `}` (B2)",
        src: SPAWN + 'expect(out.stdout).toContain(`pre${out["x }y z"]}post`);',
        hits: 0
      },
      {
        // `wrap${`inner ${x}`}post` -> fixed text `wrap`+`post`, no interior space.
        name: "does NOT flag an interpolated arg with a NESTED template `${...}` (B2 nested)",
        src: SPAWN + "expect(out.stdout).toContain(`wrap${`inner ${x}`}post`);",
        hits: 0
      },
      {
        // The `\${` is an escaped literal `${`, NOT an interpolation, so `a b` is fixed
        // text with a wrappable interior boundary -> a phrase on a raw receiver. The
        // ONLY interior whitespace is INSIDE the escaped literal (no space before the
        // `\`), so an escape-BLIND scan would treat `${a b}` as an interpolation, drop
        // it, leave `prefix` with no interior space, and NOT flag -- i.e. this witness
        // genuinely depends on the escape-awareness fix.
        name: "flags an escaped `\\${a b}` literal phrase on a raw receiver (M3)",
        src: SPAWN + "expect(out.stdout).toContain(`prefix\\${a b}`);",
        hits: 1
      },
      // --- B3 -- the call-site scanner reads real-CODE `expect(...)` only. An
      // `expect(...stdout...).toContain("multi word")` spelled inside a COMMENT or a
      // STRING LITERAL is data, not an assertion, and must NOT be flagged; the same
      // text as real code IS flagged. (The decoy-precedes-real-code line-number
      // variant is kept as a standalone test below, since it also asserts hits[0].line.)
      {
        name: "does NOT flag an `expect(...)` that appears inside a COMMENT (B3)",
        src:
          SPAWN + '// expect(out.stdout).toContain("outside the managed root")\n' + "const z = 1;",
        hits: 0
      },
      {
        name: "does NOT flag an `expect(...)` that appears inside a STRING LITERAL (B3)",
        src: SPAWN + "const s = 'expect(result.stdout).toContain(\"outside the managed root\")';",
        hits: 0
      },
      {
        name: "STILL flags the same `expect(...)` as REAL CODE (B3 control)",
        src: SPAWN + 'expect(out.stdout).toContain("outside the managed root");',
        hits: 1
      },
      // --- CLOSED RESIDUAL 2 -- phrase stored in a VARIABLE then asserted on a raw
      // receiver. Flagged when the identifier argument is a collected phrase
      // variable; the near-misses (single-word string variable; phrase variable on
      // a normalized receiver; non-string path variable) stay compliant.
      {
        name: "flags a phrase-variable argument on a raw receiver",
        src:
          SPAWN +
          'const phrase = "outside the managed root";\n' +
          "expect(result.stdout).toContain(phrase);",
        hits: 1
      },
      {
        // `token` is bound to a single word -> not a phrase variable -> not flagged
        // (a single token cannot be split by a space-driven word wrap).
        name: "does NOT flag a SINGLE-WORD string variable argument on a raw receiver",
        src:
          SPAWN + 'const token = "windows-il2cpp";\n' + "expect(result.stdout).toContain(token);",
        hits: 0
      },
      {
        name: "does NOT flag a phrase-variable argument on a NORMALIZED receiver",
        src:
          SPAWN +
          'const phrase = "outside the managed root";\n' +
          "expect(stdoutText(result)).toContain(phrase);",
        hits: 0
      },
      {
        // Mirrors the externalRoot/leakEditor style: a path-bound identifier is not a
        // phrase variable, so the (otherwise raw) receiver is not flagged.
        name: "does NOT flag a non-string variable argument (path.join) on a raw receiver",
        src:
          SPAWN +
          'const externalRoot = path.join(base, "external cli root");\n' +
          "expect(combined).toContain(externalRoot);",
        hits: 0
      },
      // --- CLOSED RESIDUAL 4 -- a tainted-local raw merge whose initializer is split
      // across REAL source newlines. The full balanced initializer read taints the
      // variable; a multi-line NORMALIZED merge stays compliant.
      //
      // D1: the single-template witness puts the `.stdout` member on a THIRD source
      // line of the initializer. The PRE-change scanner read only up to the first
      // newline of the initializer, so it captured just the lone opening backtick and
      // MISSED this -- reverting the full-balanced-read change makes that row FAIL.
      // (The `+`-concat witness was caught even by the old scanner -- its `\s*` ate
      // the leading newline -- so it was near-vacuous; the single-template one is not.)
      {
        name: "flags a tainted local whose single-template raw merge spans real newlines",
        src:
          SPAWN +
          "const combined = `\n  ${out.stdout}\n  ${out.stderr}`;\n" +
          'expect(combined).toContain("outside the managed root");',
        hits: 1
      },
      {
        name: "flags a tainted local whose +-concat raw merge spans real newlines",
        src:
          SPAWN +
          "const combined =\n  `${out.stdout}\\n` +\n  `${out.stderr}`;\n" +
          'expect(combined).toContain("outside the managed root");',
        hits: 1
      },
      {
        name: "does NOT flag a multi-line NORMALIZED merge initializer",
        src:
          SPAWN +
          "const combined = normalizePwshText(\n  `${out.stdout}\\n` +\n  `${out.stderr}`\n);\n" +
          'expect(combined).toContain("outside the managed root");',
        hits: 0
      },
      // --- M1 -- comma-separated declarators. The SECOND-and-later declarator of a
      // `const|let|var x = ..., y = ...;` list must be seen by both collectors.
      {
        name: "flags a tainted raw-merge in a SECOND comma declarator (M1)",
        src:
          SPAWN +
          "const x = 1, combined = `${out.stdout}\\n${out.stderr}`;\n" +
          'expect(combined).toContain("outside the managed root");',
        hits: 1
      },
      {
        name: "flags a phrase variable in a SECOND comma declarator (M1)",
        src:
          SPAWN +
          'const x = 1, phrase = "outside the managed root";\n' +
          "expect(result.stdout).toContain(phrase);",
        hits: 1
      },
      {
        // No raw merge and no multi-word string literal among the declarators.
        name: "does NOT flag a comma declarator list with only benign bindings (M1)",
        src:
          SPAWN +
          'const x = 1, y = combinedText(out), z = "single";\n' +
          'expect(y).toContain("outside the managed root");',
        hits: 0
      },
      // --- M2 -- a PAREN-WRAPPED multi-line raw merge. The `+`/backtick sits at depth
      // 1 inside `(...)`, so the merge is only visible after stripping one paren layer.
      {
        name: "flags a paren-wrapped multi-line raw merge as a tainted local (M2)",
        src:
          SPAWN +
          "const combined = (\n  `${out.stdout}` +\n  `${out.stderr}`\n);\n" +
          'expect(combined).toContain("outside the managed root");',
        hits: 1
      },
      {
        name: "does NOT flag a paren-wrapped NORMALIZED merge (M2 near-miss)",
        src:
          SPAWN +
          "const combined = (\n  normalizePwshText(`${a.stdout}`)\n);\n" +
          'expect(combined).toContain("outside the managed root");',
        hits: 0
      },
      // --- M-1 -- a REGEX LITERAL with an unbalanced quote must NOT corrupt the
      // offset-preserving mask. Before source-stripping grew a `regex` state, the
      // `"` inside `/["\n]/` was read as a string opener, so the phantom "string"
      // ran THROUGH the following `expect(...)`, blanking it on the mask -- the
      // call-site locator then never saw the real raw-output phrase assertion (a
      // FALSE NEGATIVE; a regression vs HEAD's raw-source scan). With the regex
      // state the regex body is neutralized and the real `expect(...)` is flagged.
      {
        name: "flags a real phrase assertion that follows a quote-containing regex (M-1)",
        src:
          SPAWN +
          'out.stdout.split(/["\\n]/);\n' +
          'expect(out.stdout).toContain("outside the managed root");',
        hits: 1
      },
      {
        // Near-miss: a SELF-CONTAINED quote-containing regex used only as a
        // `.toMatch(/.../)` on a NORMALIZED receiver. The regex no longer leaks
        // string state, so the compliant receiver classifies correctly and the
        // file stays clean (no false positive introduced by the regex).
        name: "does NOT flag a self-contained quote-regex .toMatch on a normalized receiver (M-1 near-miss)",
        src:
          SPAWN +
          'const lines = combined.split(/["\\n]/);\n' +
          "expect(stdoutText(result)).toMatch(/some multi word pattern/);",
        hits: 0
      }
    ];

    test.each(RAW_PHRASE_CASES)("findRawPwshPhraseAssertions: $name -> $hits", ({ src, hits }) => {
      expect(findRawPwshPhraseAssertions(src)).toHaveLength(hits);
    });

    // --- spawnsPwsh BREADTH end-to-end: prove the broadened spawn recognition
    // actually gates findRawPwshPhraseAssertions. Each row carries its OWN spawn
    // line (not the shared SPAWN constant) so the spawn form under test is exact: a
    // file that spawns PowerShell through any recognized command literal must have its
    // raw phrase assertion FLAGGED; a file that spawns an UNRELATED command must NOT.
    const SPAWN_BREADTH_CASES = [
      {
        name: 'FLAGGED: raw phrase assert in a spawnPlatformCommandSync("pwsh", ...) file',
        spawn: 'const result = spawnPlatformCommandSync("pwsh", ["-File", s], {});\n',
        hits: 1
      },
      {
        name: 'FLAGGED: raw phrase assert with legacy spawnSync("powershell", ...)',
        spawn: 'const result = spawnSync("powershell", ["-File", s], {});\n',
        hits: 1
      },
      {
        name: 'FLAGGED: raw phrase assert with spawnSync("pwsh.exe", ...)',
        spawn: 'const result = spawnSync("pwsh.exe", ["-File", s], {});\n',
        hits: 1
      },
      {
        name: 'FLAGGED: raw phrase assert with spawnSync("powershell.exe", ...)',
        spawn: 'const result = spawnSync("powershell.exe", ["-File", s], {});\n',
        hits: 1
      },
      {
        name: 'COMPLIANT: spawnPlatformCommandSync("npm", ...) is not a PowerShell spawn',
        spawn: 'const result = spawnPlatformCommandSync("npm", ["pack"], {});\n',
        hits: 0
      }
    ];

    test.each(SPAWN_BREADTH_CASES)(
      "findRawPwshPhraseAssertions (spawn breadth): $name -> $hits",
      ({ spawn, hits }) => {
        // Raw receiver: a member-access phrase assert that is only flagged when the
        // file is recognized as a PowerShell spawn.
        const rawSrc = spawn + 'expect(result.stdout).toContain("outside the managed root");';
        expect(findRawPwshPhraseAssertions(rawSrc)).toHaveLength(hits);
        // Near-miss control: the SAME PowerShell spawn but a NORMALIZED receiver is
        // never flagged (the receiver classification passes regardless of spawn form).
        const normalizedSrc =
          spawn + 'expect(combinedText(result)).toContain("outside the managed root");';
        expect(findRawPwshPhraseAssertions(normalizedSrc)).toHaveLength(0);
      }
    );

    // B3 (kept standalone): asserts BOTH the hit count AND hits[0].line, so it does
    // not share the single-assertion shape of the RAW_PHRASE_CASES table. The comment
    // decoy is invisible; only the real-code assertion (on line 3) is flagged.
    test("flags real-code `expect(...)` even when a COMMENT decoy precedes it (B3)", () => {
      const src =
        SPAWN +
        '// expect(out.stdout).toContain("decoy phrase in comment")\n' +
        'expect(out.stdout).toContain("outside the managed root");';
      const hits = findRawPwshPhraseAssertions(src);
      expect(hits).toHaveLength(1);
      expect(hits[0].line).toBe(5);
    });

    // spawnsPwsh: a boolean-returning helper. Uniform shape -> its own data-driven
    // table with a toBe assertion. Covers the original direct/indirection forms AND
    // the broadened command-literal recognition (spawnPlatformCommandSync; legacy
    // `powershell`; the `.exe` suffixes; case-insensitivity), plus the precision
    // near-misses that must NOT count (`node`, `npm`).
    const SPAWNS_PWSH_CASES = [
      {
        name: "spawnsPwsh recognizes the REAL_PWSH indirection",
        src: 'spawnSync(REAL_PWSH, ["-File", s], {});',
        expected: true
      },
      {
        name: "spawnsPwsh recognizes a direct pwsh literal across newlines",
        src: 'spawnSync(\n  "pwsh",\n  ["-File", s]\n);',
        expected: true
      },
      {
        name: 'spawnsPwsh recognizes spawnPlatformCommandSync("pwsh", ...)',
        src: 'spawnPlatformCommandSync("pwsh", ["-File", s], {});',
        expected: true
      },
      {
        name: 'spawnsPwsh recognizes legacy spawnSync("powershell", ...)',
        src: 'spawnSync("powershell", ["-File", s], {});',
        expected: true
      },
      {
        name: 'spawnsPwsh recognizes spawnSync("pwsh.exe", ...)',
        src: 'spawnSync("pwsh.exe", ["-File", s], {});',
        expected: true
      },
      {
        name: 'spawnsPwsh recognizes spawnSync("powershell.exe", ...)',
        src: 'spawnSync("powershell.exe", ["-File", s], {});',
        expected: true
      },
      {
        name: "spawnsPwsh is case-insensitive on the command literal (PowerShell.EXE)",
        src: 'spawnSync("PowerShell.EXE", ["-File", s], {});',
        expected: true
      },
      {
        name: 'spawnsPwsh does NOT count spawnSync("node", ...)',
        src: 'spawnSync("node", ["script.js"], {});',
        expected: false
      },
      {
        name: 'spawnsPwsh does NOT count spawnPlatformCommandSync("npm", ...)',
        src: 'spawnPlatformCommandSync("npm", ["pack"], {});',
        expected: false
      }
    ];

    test.each(SPAWNS_PWSH_CASES)("$name", ({ src, expected }) => {
      expect(spawnsPwsh(src)).toBe(expected);
    });

    // -------------------------------------------------------------------------
    // Direct-helper unit tests kept as individual test() blocks: each bundles
    // multiple distinct assertions (or asserts a Set/string, not a hit count), so
    // tabulating to a single shared assertion shape would obscure them.
    // -------------------------------------------------------------------------

    test("stripTemplateInterpolations is quote- and escape-aware (unit)", () => {
      // B2: quoted `}` does not close the span early.
      expect(stripTemplateInterpolations('pre${out["x }y z"]}post')).toBe("prepost");
      // B2 nested: a nested template inside the span is dropped whole.
      expect(stripTemplateInterpolations("wrap${`inner ${x}`}post")).toBe("wrappost");
      // M3: escaped `\${` is literal, its text survives.
      expect(stripTemplateInterpolations("prefix \\${a b}")).toBe("prefix \\${a b}");
      // A plain interpolation is still dropped, fixed text on both sides preserved.
      expect(stripTemplateInterpolations("saw ${n} the managed root")).toBe(
        "saw  the managed root"
      );
    });

    test("collectPhraseVariables ignores a for-of loop binding and a multi-word string in DATA", () => {
      // A `for (const phrase of ...)` binding is NOT a string-literal binding, and a
      // multi-word string that lives inside a STRING LITERAL is data, not a binding.
      const src =
        "for (const phrase of expectedPhrases) {}\n" +
        'const doc = "const decoy = \\"multi word decoy\\"";';
      const vars = collectPhraseVariables(src);
      expect(vars.has("phrase")).toBe(false);
      expect(vars.has("decoy")).toBe(false);
    });

    test("collectTaintedRawVariables / collectPhraseVariables see comma declarators (M1 unit)", () => {
      expect(
        collectTaintedRawVariables("const x = 1, combined = `${o.stdout}\\n${o.stderr}`;").has(
          "combined"
        )
      ).toBe(true);
      expect(
        collectPhraseVariables('const x = 1, phrase = "outside the managed root";').has("phrase")
      ).toBe(true);
      // A benign comma list taints/collects nothing.
      expect([...collectTaintedRawVariables("const x = 1, y = 2, z = foo();")]).toEqual([]);
      expect([...collectPhraseVariables('const x = 1, y = "single", z = foo();')]).toEqual([]);
    });

    test("collectTaintedRawVariables sees a paren-wrapped merge / skips a paren-wrapped normalize (M2 unit)", () => {
      expect(
        collectTaintedRawVariables(
          "const combined = (\n  `${out.stdout}` +\n  `${out.stderr}`\n);"
        ).has("combined")
      ).toBe(true);
      expect(
        collectTaintedRawVariables(
          "const combined = (\n  normalizePwshText(`${a.stdout}`)\n);"
        ).has("combined")
      ).toBe(false);
    });

    test("collectTaintedRawVariables ignores a combinedText-bound variable", () => {
      const tainted = collectTaintedRawVariables("const combined = combinedText(out);");
      expect(tainted.has("combined")).toBe(false);
    });

    test("argumentIsPhrase treats an interpolated template with fixed word boundaries as a phrase", () => {
      // The fixed text ("saw " + " outside the managed root") still has interior
      // whitespace after `${n}` is removed -> wrappable -> a phrase.
      expect(argumentIsPhrase("toContain", "`saw ${n} outside the managed root`")).toBe(true);
    });

    test("argumentIsPhrase treats an interpolated template with NO fixed boundary as a non-phrase", () => {
      // Removing `${sourceGeneratorPath}` leaves `-a:""` -- no interior whitespace,
      // so it is not a wrappable phrase (mirrors the real csc.rsp assertion).
      expect(argumentIsPhrase("toContain", '`-a:"${sourceGeneratorPath}"`')).toBe(false);
      // A template that is ONE interpolation (the whole value substituted) has no
      // fixed text at all -> not a phrase.
      expect(argumentIsPhrase("toContain", "`${commit}`")).toBe(false);
    });

    test("argumentIsPhrase treats a non-phrase identifier argument as a non-phrase", () => {
      // An identifier that is NOT a collected phrase variable is not a phrase.
      expect(argumentIsPhrase("toContain", "outputPath", new Set())).toBe(false);
    });

    // -----------------------------------------------------------------------
    // "Renamed helper" detector: prove findRenamedMergeHelpers catches a
    // differently-named raw-merge helper and does NOT false-positive on the
    // compliant combinedText/stdoutText shapes or on legitimate structural code.
    // -----------------------------------------------------------------------
    describe("renamed-helper detector", () => {
      // Each row is one crafted source fed through findRenamedMergeHelpers.
      // `hits: 1` == FLAGGED raw-merge definition; `hits: 0` == COMPLIANT /
      // structural / data-in-comment-or-string shape that must NOT false-positive.
      const RENAMED_MERGE_CASES = [
        // --- FLAGGED raw-merge helper definitions (template + string-concat) ---
        {
          name: "flags a renamed function helper that returns a raw .stdout/.stderr merge",
          src:
            SPAWN +
            "function merged(out) { return `${out.stdout}\\n${out.stderr}`; }\n" +
            "merged(result);",
          hits: 1
        },
        {
          name: "flags a renamed ARROW helper with an implicit raw-merge return",
          src:
            SPAWN +
            "const merged = (out) => `${out.stdout}\\n${out.stderr}`;\n" +
            "merged(result);",
          hits: 1
        },
        {
          name: "flags a renamed block-bodied ARROW helper with a raw-merge return",
          src:
            SPAWN +
            "const merged = (out) => { return `${out.stdout}\\n${out.stderr}`; };\n" +
            "merged(result);",
          hits: 1
        },
        {
          name: "flags a renamed object method helper with a raw-merge return",
          src:
            SPAWN +
            "const helper = { merged(out) { return `${out.stdout}\\n${out.stderr}`; } };\n" +
            "helper.merged(result);",
          hits: 1
        },
        {
          name: "flags a renamed helper that merges a single member into a template",
          src: SPAWN + "function only(out) { return `prefix ${out.stderr}`; }\n" + "only(result);",
          hits: 1
        },
        // CLOSED RESIDUAL 3 -- string-CONCATENATION raw merge in a renamed helper.
        {
          name: "flags a renamed function helper that returns a string-CONCAT raw merge",
          src:
            SPAWN +
            'function merged(o) { return o.stdout + "\\n" + o.stderr; }\n' +
            "merged(result);",
          hits: 1
        },
        {
          name: "flags a renamed ARROW helper with an implicit string-CONCAT raw merge",
          src: SPAWN + 'const merged = (o) => o.stdout + "\\n" + o.stderr;\n' + "merged(result);",
          hits: 1
        },
        // --- B1 -- ARITHMETIC `+` touching `.stdout`/`.stderr` is NOT a string raw
        // merge. A `+` is a raw STRING merge only when it has BOTH a bare raw-output
        // member operand AND a string/template-literal operand (proof of string
        // concat). The arithmetic forms below have a `.stdout` member but no bare
        // member and/or no string operand, so they must NOT be flagged. The genuine
        // string-CONCAT form above (`o.stdout + "\n" + o.stderr`) stays flagged.
        {
          name: "does NOT flag arithmetic `a.stdout.length + b.length` (function body)",
          src: SPAWN + "function f(a, b) { return a.stdout.length + b.length; }",
          hits: 0
        },
        {
          name: "does NOT flag arithmetic `idx + r.stdout.indexOf(x)` (arrow body)",
          src: SPAWN + "const g = (idx, r, x) => idx + r.stdout.indexOf(x);",
          hits: 0
        },
        {
          name: 'does NOT flag a `+` with a string operand but NO bare raw member (`"x" + y`)',
          src: SPAWN + 'function h(y) { return "x" + y; }',
          hits: 0
        },
        // --- Compliant / structural shapes that must NOT be flagged ---
        {
          // Mirrors the legitimate structural read in powershell-syntax.test.js -- a
          // `.split(...)` chain has no top-level `+`, so it is not a concat merge.
          name: "does NOT flag a member chain with NO top-level + (return result.stdout.split)",
          src:
            SPAWN + 'function list() { return result.stdout.split("\\n").map((x) => x.trim()); }',
          hits: 0
        },
        {
          // Mirrors unity-perf-baseline-script-contract.test.js:44.
          name: "does NOT flag a single-member trim chain (return result.stdout.trim())",
          src: SPAWN + "function last() { return result.stdout.trim(); }",
          hits: 0
        },
        {
          name: "does NOT flag a normalizePwshText-wrapped string-CONCAT (compliant)",
          src: SPAWN + 'function c(a, b) { return normalizePwshText(a.stdout + "\\n" + b); }',
          hits: 0
        },
        {
          name: "does NOT flag the compliant combinedText shape (normalizePwshText-wrapped)",
          src:
            SPAWN +
            'function combinedText(run) { return normalizePwshText(`${run.stdout || ""}\\n${run.stderr || ""}`); }',
          hits: 0
        },
        {
          name: "does NOT flag the compliant stdoutText shape (single member, wrapped, no template)",
          src:
            SPAWN +
            'function stdoutText(result) { return normalizePwshText(result.stdout || ""); }',
          hits: 0
        },
        {
          name: "does NOT flag a compliant normalizePwshText-wrapped arrow helper",
          src: SPAWN + "const c = (run) => normalizePwshText(`${run.stdout}\\n${run.stderr}`);",
          hits: 0
        },
        {
          name: "does NOT flag a result-shaper that returns an object literal of stdout/stderr",
          src:
            SPAWN +
            'function probe() { return { stdout: (run.stdout || "").trim(), stderr: run.stderr || "", status: run.status }; }',
          hits: 0
        },
        {
          name: "does NOT flag a structural single-member read (.stdout.split chain, not a template)",
          src:
            SPAWN + 'function list() { return result.stdout.split("\\n").map((x) => x.trim()); }',
          hits: 0
        },
        {
          name: "does NOT flag a throw-error template (not a return/arrow body)",
          src:
            SPAWN +
            "if (run.status !== 0) { throw new Error(`failed: ${run.stderr || run.stdout}`); }",
          hits: 0
        },
        // --- Comment/string safety: merge text spelled in data must NOT be flagged ---
        {
          name: "does NOT flag the merge text when it lives inside a STRING LITERAL (data, not code)",
          src: SPAWN + 'const doc = "return `${out.stdout}\\n${out.stderr}`";',
          hits: 0
        },
        {
          name: "does NOT flag the word 'return' + backtick + .stdout inside a COMMENT (prose)",
          src: SPAWN + "// after this `return` see `${x.stdout}` in this comment\nconst a = 1;",
          hits: 0
        },
        {
          name: "does NOT flag a renamed raw merge in a file that does not spawn pwsh",
          src: "function merged(out) { return `${out.stdout}\\n${out.stderr}`; }",
          hits: 0
        },
        {
          name: "does NOT flag a raw merge helper used only for non-PowerShell output",
          src:
            SPAWN +
            "function gitText(run) { return `${run.stdout}\\n${run.stderr}`; }\n" +
            'const git = spawnSync("git", ["status"], {});\n' +
            "gitText(git);",
          hits: 0
        },
        {
          name: "does NOT let helper parameters inherit an outer PowerShell result binding",
          src:
            SPAWN +
            "function gitText(result) { return `${result.stdout}\\n${result.stderr}`; }\n" +
            'const git = spawnSync("git", ["status"], {});\n' +
            "gitText(git);",
          hits: 0
        },
        {
          name: "does NOT match a raw-merge helper call to a shadowed same-name helper",
          src:
            "{\n" +
            '  const result = spawnSync("git", ["status"], {});\n' +
            "  function merged(result) { return `${result.stdout}\\n${result.stderr}`; }\n" +
            "  merged(result);\n" +
            "}\n" +
            "{\n" +
            '  const result = spawnSync("pwsh", ["-File", s], {});\n' +
            "  function merged(result) { return normalizePwshText(`${result.stdout}\\n${result.stderr}`); }\n" +
            "  merged(result);\n" +
            "}",
          hits: 0
        },
        {
          name: "does NOT let a nested shadowing helper call taint an outer raw helper",
          src:
            "function merged(result) { return `${result.stdout}\\n${result.stderr}`; }\n" +
            "{\n" +
            '  const result = spawnSync("pwsh", ["-File", s], {});\n' +
            "  function merged(result) { return normalizePwshText(`${result.stdout}\\n${result.stderr}`); }\n" +
            "  merged(result);\n" +
            "}",
          hits: 0
        }
      ];

      test.each(RENAMED_MERGE_CASES)("findRenamedMergeHelpers: $name -> $hits", ({ src, hits }) => {
        expect(findRenamedMergeHelpers(src)).toHaveLength(hits);
      });

      // Kept standalone (collectTaintedRawVariables, not findRenamedMergeHelpers):
      test("does NOT flag arithmetic `result.stdout.length + 1` as a tainted local", () => {
        // The `.stdout.length` operand is not bare and there is no string operand,
        // so `count` is not tainted (a phrase assertion on `count` would be a type
        // error anyway -- this just proves the arithmetic shape is not a raw merge).
        const src =
          SPAWN +
          "const count = result.stdout.length + 1;\n" +
          'expect(combined).toContain("outside the managed root");';
        expect(collectTaintedRawVariables(src).has("count")).toBe(false);
      });

      // Kept standalone (isRawMergeExpression, multiple distinct assertions):
      test("isRawMergeExpression: flags genuine string concat, rejects arithmetic", () => {
        // FLAGGED string concat (bare member + string operand).
        expect(isRawMergeExpression('o.stdout + "\\n" + o.stderr')).toBe(true);
        // FLAGGED guarded-member string concat (the combinedText `|| ""` spelling).
        expect(isRawMergeExpression('(run.stdout || "") + "\\n" + (run.stderr || "")')).toBe(true);
        // NOT flagged: arithmetic / non-string-concat shapes.
        expect(isRawMergeExpression("a.stdout.length + b.length")).toBe(false);
        expect(isRawMergeExpression("idx + r.stdout.indexOf(x)")).toBe(false);
        expect(isRawMergeExpression("result.stdout.length + 1")).toBe(false);
        expect(isRawMergeExpression('"x" + y')).toBe(false);
        // NOT flagged: two bare members with no string operand (per the rule, a
        // string operand is required to prove string concatenation).
        expect(isRawMergeExpression("o.stdout + o.stderr")).toBe(false);
      });
    });
  });
});
