/**
 * @fileoverview Repository-wide policy guard against "neutralize host-default
 * folder discovery with a case-sensitive `delete`". It statically forbids the
 * ENFORCED forms of that anti-pattern -- the dot, bracket-string,
 * Reflect.deleteProperty-literal, and array-literal-correlated-with-its-own-
 * delete-loop forms (enumerated below) -- so the common, natural ways to write
 * the bug cannot be reintroduced unnoticed. It does NOT (and a source scanner
 * cannot) catch every conceivable indirection; the honestly-scoped residual is
 * documented below and is covered by behavioral backstops (the golden unit test
 * for sandboxHostFolderEnv + the use-the-helper policy).
 *
 * Background:
 *   Tests that spawn host-sensitive scripts (e.g. scripts/unity/ensure-editor.ps1
 *   and scripts/unity/run-ci-tests.ps1) must run hermetically: those scripts
 *   probe host-default FOLDER vars -- `${env:ProgramFiles}\Unity\Hub\Editor\...`,
 *   `${env:ProgramFiles(x86)}\...`, `$env:LOCALAPPDATA\Unity\...` -- to discover
 *   machine-installed software. If a test leaves those populated, a real Unity
 *   install on the runner leaks into resolution and the test passes/fails by
 *   accident.
 *
 *   The old neutralization pattern `delete env.ProgramFiles` is a LATENT
 *   cross-platform bug: Windows environment-variable NAMES are CASE-INSENSITIVE
 *   while a JavaScript `delete` is CASE-SENSITIVE. A surviving case-variant key
 *   (e.g. `PROGRAMFILES`) keeps `$env:ProgramFiles` populated inside the child
 *   pwsh, so the real folder leaks in. On Linux `${env:ProgramFiles}` is empty,
 *   so the bug is INVISIBLE locally and only bites on a Windows host that has
 *   Unity installed -- classic host-divergence that rots until run on Windows.
 *
 *   The fix is scripts/lib/spawn-env-sandbox.js's `sandboxHostFolderEnv`, which
 *   removes EVERY case-variant of the host-default folder vars and SETS them to
 *   empty sandbox dirs.
 *
 * Policy: no test under scripts/**\/__tests__/*.test.js may neutralize a
 * host-default folder var off an env-like object via any case-sensitive delete.
 * The guard flags these ENFORCED forms (any host-default folder var, name
 * compared case-insensitively against the SINGLE SOURCE OF TRUTH
 * HOST_FOLDER_DENYLIST in scripts/lib/spawn-env-sandbox.js):
 *   - dot:     `delete <ident>.ProgramFiles`
 *   - bracket: `delete <ident>["ProgramFiles(x86)"]`
 *   - reflect: `Reflect.deleteProperty(<ident>, "ProgramFiles")`
 *   - array:   a host-folder var quoted as a LITERAL element inside an ARRAY
 *              LITERAL where THAT SAME array drives a COMPUTED delete on an
 *              env-like object. The correlation is PER-CONSTRUCT (not a
 *              file-global co-occurrence): the array holding the literal must be
 *              the very array consumed by the delete. The recognized linkage
 *              shapes are:
 *                * `const ENV_TO_DELETE = [..., "ProgramFiles", ...];
 *                   for (const k of ENV_TO_DELETE) delete env[k];` and the inline
 *                   `for (const k of ["ProgramFiles"]) delete env[k];`
 *                * `<ARR>.forEach(k => ... delete env[k])` /
 *                   `.forEach(function (k) { ... })` and the inline
 *                   `["ProgramFiles"].forEach(k => delete env[k])`
 *                * direct index: `delete env[<ARR>[i]]`
 *                * Reflect forms of all the above: `for (const k of <ARR>)
 *                   Reflect.deleteProperty(env, k)` and
 *                   `Reflect.deleteProperty(env, <ARR>[i])`
 *              Re-adding a host-folder name to such a delete-driving array
 *              silently reintroduces the broken case-sensitive neutralization, so
 *              we close that vector too.
 * The message steers to sandboxHostFolderEnv.
 *
 * Precision of the array form: a computed delete on its own is legitimate (a test
 * may delete NON-host vars from a list), and a host-folder name listed in an
 * UNRELATED array is legitimate too, so we flag ONLY the dangerous correlation --
 * the host-folder literal lives in the SAME array that drives the delete. This is
 * why (a) unity-runner-strictmode-smoke.test.js -- whose ENV_TO_DELETE delete-
 * loop holds NO host-folder vars -- is NOT flagged, AND (b) a file that merely
 * lists host-folder names in a fixture array next to an unrelated cleanup loop
 * over a DIFFERENT array is NOT flagged (no file-global false positive).
 *
 * Residual (documented, not over-claimed): the array-form correlation recognizes
 * a host-folder name supplied as a LITERAL string element of the delete-driving
 * ARRAY LITERAL. It deliberately does NOT chase indirection where the host-folder
 * name is not a literal element of that same array, specifically:
 *   - a fully-runtime-assembled name (concatenation/`String.fromCharCode`, read
 *     from a file, or otherwise computed) fed to a computed delete;
 *   - a SCALAR-VARIABLE-ROUTED literal: `const k = "ProgramFiles"; delete env[k]`
 *     (the literal is real but it is not an array element);
 *   - a PUSH-BUILT array: `arr.push("ProgramFiles"); for (const k of arr) delete
 *     env[k]` (the name enters the array at runtime, not as a literal element);
 *   - the exotic inline DIRECT-INDEX literal `delete env[["ProgramFiles"][0]]`
 *     (precision over recall for this rare shape).
 * Source-scanning guards cannot see runtime values, and reaching further would
 * risk the false positives that get a guard disabled. The golden unit test for
 * sandboxHostFolderEnv is the behavioral backstop, and any test spawning a
 * host-sensitive script is required to use the sandbox helper. See the
 * "documented residual" self-tests below.
 *
 * Robustness: CRLF/BOM-safe (normalizeToLf strips both), Node stdlib only, no
 * shell-outs, path.join for all paths, all regexes linear (ReDoS-free). The
 * detector cross-checks every RAW hit against the STRIPPED source so a pattern
 * living in a comment or string (e.g. this file's own prose) is not flagged; the
 * array form additionally correlates the raw literal against the stripped
 * literal by per-line occurrence order so a name embedded in a comment/string is
 * filtered out, and then walks the STRIPPED source to confirm the enclosing array
 * literal is the very construct consumed by a computed delete (named-identifier
 * match or inline-literal bracket-offset match). The helper module and its golden
 * test are allow-listed because they legitimately reference the pattern as
 * fixtures/documentation.
 *
 * How to mutation-test this guard (do this when you touch it):
 *   1. Add a scratch test file under scripts/__tests__ containing
 *      `delete env.ProgramFiles;` (or the reflect/array forms). Run this suite ->
 *      the policy test must FAIL, naming that file. Delete the scratch -> GREEN.
 *   Self-tests below feed crafted source strings through the detector so the
 *   guard is verified without mutating real files.
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
const { HOST_FOLDER_DENYLIST } = require("../lib/spawn-env-sandbox");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SCRIPTS_ROOT = path.join(REPO_ROOT, "scripts");

const WALK_SKIP_DIRS = new Set(["node_modules", ".git", ".venv", "__pycache__", "Temp"]);

// Lowercased host-default FOLDER var names whose case-sensitive `delete` is the
// anti-pattern. SINGLE SOURCE OF TRUTH: reuse HOST_FOLDER_DENYLIST (already
// lowercased) from scripts/lib/spawn-env-sandbox.js so the guard and the helper
// can never drift -- adding a var to the helper's canonical list automatically
// extends what this guard polices.
const HOST_FOLDER_VAR_NAMES_LOWER = HOST_FOLDER_DENYLIST;

// Files PERMITTED to reference the anti-pattern as fixtures/documentation:
// this guard embeds crafted source strings (self-tests) and prose; the helper's
// golden test asserts on the canonical-cased keys; the helper module documents
// the gotcha. Those literals are data, not real env neutralization.
const ALLOW_LIST = new Set([
  path.join("scripts", "__tests__", "hermetic-host-env-policy.test.js"),
  path.join("scripts", "lib", "__tests__", "spawn-env-sandbox.test.js"),
  path.join("scripts", "lib", "spawn-env-sandbox.js")
]);

function readUtf8(absolutePath) {
  // Strip a leading UTF-8 BOM (normalizeToLf only handles CR/CRLF) so a
  // BOM-prefixed file's first token is still anchored correctly.
  return normalizeToLf(fs.readFileSync(absolutePath, "utf8")).replace(/^\uFEFF/, "");
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

function listTestFiles() {
  return listFilesRecursive(SCRIPTS_ROOT, (abs) => {
    if (!abs.endsWith(".test.js")) {
      return false;
    }
    return path.relative(SCRIPTS_ROOT, abs).split(path.sep).includes("__tests__");
  });
}

function lineNumberAt(text, index) {
  return text.slice(0, index).split("\n").length;
}

// `delete <ident>.<VarName>` -- dot member access. The var name is captured for a
// case-insensitive denylist check. `[\w$]+` allows nested member access bases
// like `process.env` because we only anchor on the FINAL `.VarName` segment.
// The terminator is a zero-width lookahead so a statement/block/array/EOF
// boundary all close the match without consuming it: `;`, newline, `)`, `}`
// (`if (x) { delete env.ProgramFiles }`), `]` (`[delete env.ProgramFiles]`), or
// end-of-input (a trailing no-terminator form). Lookahead keeps it linear/
// ReDoS-free (no nested or overlapping quantifiers).
const DELETE_DOT_RE = /\bdelete\s+[\w$.[\]"'`()]*?([A-Za-z_$][\w$]*)\s*(?=[;\n)}\]]|$)/g;
// `delete <ident>["VarName"]` / `delete <ident>['VarName']` -- bracket-string
// member access. The quoted name (which may contain `(x86)`) is captured.
const DELETE_BRACKET_RE = /\bdelete\s+[\w$.[\]"'`()]*?\[\s*["'`]([^"'`]+)["'`]\s*\]/g;
// `Reflect.deleteProperty(<anything>, "VarName")` -- the reflective delete form.
// The first argument (the target object) is skipped via a non-greedy run up to
// the comma; the quoted property name is captured. Single/double/backtick quotes
// are all accepted. `[^,()]*?` for the target keeps this anchored on a single
// call expression (no nested parens in the target) and ReDoS-free.
const DELETE_REFLECT_RE =
  /\bReflect\s*\.\s*deleteProperty\s*\(\s*[^,()]*?,\s*["'`]([^"'`]+)["'`]\s*\)/g;

// ---------------------------------------------------------------------------
// Array-driven computed-delete correlation (per-construct, NOT file-global).
//
// The dangerous shape is an array literal that BOTH (a) holds a host-folder var
// name as a quoted string element AND (b) is the iterable/index source of a
// COMPUTED delete on an env-like object. The two must be the SAME construct: an
// unrelated array holding a host-folder name plus an unrelated computed delete
// elsewhere in the file is NOT the anti-pattern and must NOT be flagged.
//
// All regexes below run on the STRIPPED source (string payloads are emptied, so
// the loop/forEach/delete STRUCTURE survives but no string content can spoof a
// match) and are linear / ReDoS-free (no nested or overlapping quantifiers).
// ---------------------------------------------------------------------------

// `for (<decl> <loopVar> of <ARR>)` -- a for...of whose iterable is a bare array
// IDENTIFIER. Captures the loop variable (g1) and the array identifier (g2). The
// optional `const`/`let`/`var` declarator is consumed but not captured.
const FOR_OF_NAMED_ARRAY_RE =
  /\bfor\s*\(\s*(?:const|let|var)?\s*([A-Za-z_$][\w$]*)\s+of\s+([A-Za-z_$][\w$]*)\s*\)/g;
// `for (<decl> <loopVar> of [ ... ])` -- a for...of over an INLINE array literal.
// Captures the loop variable (g1); the inline `[` opens at the match end.
const FOR_OF_INLINE_ARRAY_RE = /\bfor\s*\(\s*(?:const|let|var)?\s*([A-Za-z_$][\w$]*)\s+of\s+\[/g;
// `<ARR>.forEach(` -- forEach on a bare array identifier. Captures the array
// identifier (g1); the callback (and its first param == the element) follows.
const FOREACH_NAMED_ARRAY_RE = /\b([A-Za-z_$][\w$]*)\s*\.\s*forEach\s*\(/g;
// `[ ... ].forEach(` -- forEach on an INLINE array literal. The `[` that opens
// the literal is captured by position (match starts at it).
const FOREACH_INLINE_ARRAY_RE = /\[(?=[^\]]*\]\s*\.\s*forEach\s*\()/g;
// `delete <x>[<ARR>[` -- a direct index into a bare array identifier inside a
// computed delete. Captures the array identifier (g1).
const DELETE_INDEX_NAMED_ARRAY_RE = /\bdelete\s+[\w$.]+\s*\[\s*([A-Za-z_$][\w$]*)\s*\[/g;
// `Reflect.deleteProperty(<x>, <ARR>[` -- the Reflect direct-index form.
// Captures the array identifier (g1).
const REFLECT_INDEX_NAMED_ARRAY_RE =
  /\bReflect\s*\.\s*deleteProperty\s*\(\s*[^,()[\]]*?,\s*([A-Za-z_$][\w$]*)\s*\[/g;

// A COMPUTED delete whose property name is the bare identifier <V> (no literal):
//   `delete <x>[<V>]` and `Reflect.deleteProperty(<x>, <V>)`. Used to confirm a
// loop/forEach body actually deletes USING THE LOOP VARIABLE (so the array that
// supplies <V> is the one driving the delete). The loop variable is interpolated
// into a fresh, anchored, linear regex (escaped to keep it literal).
function escapeRegExp(text) {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function bodyDeletesWithVar(strippedBody, loopVar) {
  const v = escapeRegExp(loopVar);
  const bracket = new RegExp(`\\bdelete\\s+[\\w$.]+\\s*\\[\\s*${v}\\s*\\]`);
  const reflect = new RegExp(
    `\\bReflect\\s*\\.\\s*deleteProperty\\s*\\(\\s*[^,()]*?,\\s*${v}\\s*\\)`
  );
  return bracket.test(strippedBody) || reflect.test(strippedBody);
}

// The window of stripped source following a `for(...)`/`.forEach(...)` header in
// which we look for the `delete <x>[<loopVar>]` body. A generous fixed cap keeps
// the scan linear and avoids walking the whole file; real delete loops put the
// body within a few lines of the header.
const DELETE_BODY_WINDOW = 600;

/**
 * Decide whether a quoted-string occurrence sits in an ARRAY-ELEMENT slot, i.e.
 * its nearest non-whitespace token before the opening quote is `[` or `,` AND
 * after the closing quote is `,` or `]` (whitespace/newlines ignored). This is a
 * lightweight structural check (not a full parser) precise enough to catch
 * `["...", "ProgramFiles", ...]` (incl. multi-line) while not matching a name
 * used as a function argument or object value.
 *
 * @param {string} source
 * @param {number} startQuote - index of the opening quote
 * @param {number} endQuote - index just past the closing quote
 * @returns {boolean}
 */
function isArrayElementSlot(source, startQuote, endQuote) {
  let i = startQuote - 1;
  while (i >= 0 && /\s/.test(source[i])) {
    i--;
  }
  const before = i >= 0 ? source[i] : "";

  let j = endQuote;
  while (j < source.length && /\s/.test(source[j])) {
    j++;
  }
  const after = j < source.length ? source[j] : "";

  return (before === "[" || before === ",") && (after === "," || after === "]");
}

/**
 * Find every host-folder var name that appears as a REAL string-literal array
 * element. The name is located in RAW source (stripping erases payloads), then
 * confirmed to be genuine code -- not inside a comment or an outer string -- by
 * correlating it with the STRIPPED source: stripping preserves the COUNT and
 * ORDER of string literals per line (only emptying their payloads), so the Kth
 * quoted literal on a raw line maps to the Kth quoted literal on the stripped
 * line. A real array element survives as an EMPTY quote pair still sitting in an
 * array slot; a comment/string-embedded occurrence does not (its line is erased
 * or its array brackets become string payload), so it is correctly ignored.
 *
 * Each hit also carries the ABSOLUTE offset of its slot's opening quote in the
 * STRIPPED source (`slotStart`), so a caller can locate the array literal that
 * encloses the element and correlate it with a delete (see
 * findCorrelatedArrayDeletes).
 *
 * @param {string} rawSource
 * @param {string} strippedSource
 * @returns {Array<{line: number, name: string, slotStart: number}>}
 */
function findArrayLiteralHostFolderVars(rawSource, strippedSource) {
  const rawLines = rawSource.split("\n");
  const strippedLines = strippedSource.split("\n");
  const out = [];

  const quotedRe = /(["'`])([^"'`]*)\1/g;
  // Stripped literals are empty, so capture by the quote delimiters only.
  const strippedQuotedRe = /(["'`])\1/g;

  // Absolute offset of each stripped line's start in the full stripped source, so
  // the array-slot check can span lines (a multi-line array element's neighbor
  // `[`/`,` lives on a PREVIOUS line). split("\n") drops the separators; each
  // line therefore contributes its length + 1 (the removed "\n").
  let strippedLineStart = 0;

  for (let lineIdx = 0; lineIdx < rawLines.length; lineIdx++) {
    const rawLine = rawLines[lineIdx];
    const strippedLine = strippedLines[lineIdx] || "";

    // Enumerate the stripped line's empty quote literals (their slots), recorded
    // as ABSOLUTE offsets in the full stripped source.
    const strippedSlots = [];
    strippedQuotedRe.lastIndex = 0;
    let sm = strippedQuotedRe.exec(strippedLine);
    while (sm !== null) {
      strippedSlots.push({
        start: strippedLineStart + sm.index,
        end: strippedLineStart + sm.index + sm[0].length
      });
      sm = strippedQuotedRe.exec(strippedLine);
    }

    // Walk the raw line's quoted literals in order; the Kth maps to strippedSlots[K]
    // (stripping preserves per-line literal count and order).
    quotedRe.lastIndex = 0;
    let k = -1;
    let m = quotedRe.exec(rawLine);
    while (m !== null) {
      k++;
      const name = m[2];
      if (name && HOST_FOLDER_VAR_NAMES_LOWER.has(name.toLowerCase())) {
        const slot = strippedSlots[k];
        // Run the slot check against the FULL stripped source so a multi-line
        // array element (neighbor delimiter on an adjacent line) is recognized.
        if (slot && isArrayElementSlot(strippedSource, slot.start, slot.end)) {
          out.push({ line: lineIdx + 1, name, slotStart: slot.start });
        }
      }
      m = quotedRe.exec(rawLine);
    }

    strippedLineStart += strippedLine.length + 1;
  }

  return out;
}

/**
 * Find the absolute offset of the `[` that opens the array literal ENCLOSING the
 * element at `slotStart` in the stripped source. Scans backward, balancing `]`
 * with `[` so a nested inner array (`[..., [..], "ProgramFiles"]`) is skipped and
 * the OUTERMOST-relative enclosing `[` for this element is returned. Returns -1
 * if the element is not directly inside an array literal (defensive; the caller
 * already required an array-element slot, so this normally succeeds).
 *
 * @param {string} stripped
 * @param {number} slotStart - offset of the element's opening quote
 * @returns {number}
 */
function enclosingArrayOpenBracket(stripped, slotStart) {
  let depth = 0;
  for (let i = slotStart - 1; i >= 0; i--) {
    const c = stripped[i];
    if (c === "]") {
      depth++;
    } else if (c === "[") {
      if (depth === 0) {
        return i;
      }
      depth--;
    }
  }
  return -1;
}

/**
 * Given the offset of an array literal's opening `[`, return the IDENTIFIER it is
 * assigned to in a `const|let|var <ARR> = [` (or bare `<ARR> = [`) declaration,
 * by scanning backward over whitespace/`=` to the identifier. Returns null when
 * the `[` is not directly preceded by `<ident> =` (e.g. it is inline: `of [`,
 * `(` `[`, `forEach(` `[`, or an element of an outer array), so the caller treats
 * it as an inline literal and correlates by structure instead of by name.
 *
 * @param {string} stripped
 * @param {number} openBracketIdx
 * @returns {string|null}
 */
function arrayAssignmentTarget(stripped, openBracketIdx) {
  let i = openBracketIdx - 1;
  while (i >= 0 && /\s/.test(stripped[i])) {
    i--;
  }
  if (i < 0 || stripped[i] !== "=") {
    return null;
  }
  // A `==`/`<=`/`>=`/`!=` is not an assignment; require a plain `=`.
  if (i - 1 >= 0 && /[=<>!]/.test(stripped[i - 1])) {
    return null;
  }
  i--;
  while (i >= 0 && /\s/.test(stripped[i])) {
    i--;
  }
  let end = i + 1;
  while (i >= 0 && /[\w$]/.test(stripped[i])) {
    i--;
  }
  const ident = stripped.slice(i + 1, end);
  return /^[A-Za-z_$][\w$]*$/.test(ident) ? ident : null;
}

/**
 * Collect the set of array IDENTIFIERS that DRIVE a computed delete on an
 * env-like object -- i.e. an array consumed by one of the enforced delete
 * shapes. Operates entirely on the STRIPPED source (structure only). The shapes:
 *   - `for (<v> of <ARR>) ... delete <x>[<v>]` / `... Reflect.deleteProperty(<x>, <v>)`
 *   - `<ARR>.forEach(<v> => ... delete <x>[<v>])` / `.forEach(function(<v>){...})`
 *   - `delete <x>[<ARR>[<idx>]]`             (direct index)
 *   - `Reflect.deleteProperty(<x>, <ARR>[<idx>])`
 *
 * For the for...of and forEach forms we additionally CONFIRM the body deletes
 * USING THE LOOP VARIABLE (within a bounded window), so a for...of/forEach whose
 * body does something else does not falsely mark its array as delete-driving.
 *
 * @param {string} stripped
 * @returns {Set<string>}
 */
function collectDeleteDrivingArrayIdentifiers(stripped) {
  const driving = new Set();

  // for (<v> of <ARR>) ... delete <x>[<v>] | Reflect.deleteProperty(<x>, <v>)
  FOR_OF_NAMED_ARRAY_RE.lastIndex = 0;
  let m = FOR_OF_NAMED_ARRAY_RE.exec(stripped);
  while (m !== null) {
    const loopVar = m[1];
    const arrIdent = m[2];
    const body = stripped.slice(m.index, m.index + DELETE_BODY_WINDOW);
    if (bodyDeletesWithVar(body, loopVar)) {
      driving.add(arrIdent);
    }
    m = FOR_OF_NAMED_ARRAY_RE.exec(stripped);
  }

  // <ARR>.forEach(<v> => ... delete <x>[<v>]) / .forEach(function(<v>){...})
  FOREACH_NAMED_ARRAY_RE.lastIndex = 0;
  m = FOREACH_NAMED_ARRAY_RE.exec(stripped);
  while (m !== null) {
    const arrIdent = m[1];
    // The callback's first parameter is the element. Read it from just after the
    // `(` (supporting `function (v)`, `(v) =>`, and `v =>`).
    const afterParen = stripped.slice(m.index + m[0].length, m.index + m[0].length + 64);
    const paramMatch = /^\s*(?:function\s*)?\(?\s*([A-Za-z_$][\w$]*)/.exec(afterParen);
    if (paramMatch) {
      const elemVar = paramMatch[1];
      const body = stripped.slice(m.index, m.index + DELETE_BODY_WINDOW);
      if (bodyDeletesWithVar(body, elemVar)) {
        driving.add(arrIdent);
      }
    }
    m = FOREACH_NAMED_ARRAY_RE.exec(stripped);
  }

  // delete <x>[<ARR>[<idx>]]
  DELETE_INDEX_NAMED_ARRAY_RE.lastIndex = 0;
  m = DELETE_INDEX_NAMED_ARRAY_RE.exec(stripped);
  while (m !== null) {
    driving.add(m[1]);
    m = DELETE_INDEX_NAMED_ARRAY_RE.exec(stripped);
  }

  // Reflect.deleteProperty(<x>, <ARR>[<idx>])
  REFLECT_INDEX_NAMED_ARRAY_RE.lastIndex = 0;
  m = REFLECT_INDEX_NAMED_ARRAY_RE.exec(stripped);
  while (m !== null) {
    driving.add(m[1]);
    m = REFLECT_INDEX_NAMED_ARRAY_RE.exec(stripped);
  }

  return driving;
}

/**
 * Collect the set of INLINE-array-literal opening-`[` offsets (in the stripped
 * source) that DRIVE a computed delete -- the array literal is written directly
 * in the consuming expression rather than via a named identifier:
 *   - `for (<v> of [ ... ]) ... delete <x>[<v>]`
 *   - `[ ... ].forEach(<v> => ... delete <x>[<v>])`
 * (The direct-index inline form `delete x[[...][i]]` is exotic and intentionally
 * left to the documented residual -- see the JSDoc.) The body confirmation uses
 * the same loop-variable check as the named forms.
 *
 * @param {string} stripped
 * @returns {Set<number>}
 */
function collectInlineDrivingArrayBrackets(stripped) {
  const brackets = new Set();

  // for (<v> of [ ... ]) ... delete <x>[<v>]
  FOR_OF_INLINE_ARRAY_RE.lastIndex = 0;
  let m = FOR_OF_INLINE_ARRAY_RE.exec(stripped);
  while (m !== null) {
    const loopVar = m[1];
    const bracketIdx = m.index + m[0].length - 1; // position of the inline `[`
    const body = stripped.slice(m.index, m.index + DELETE_BODY_WINDOW);
    if (bodyDeletesWithVar(body, loopVar)) {
      brackets.add(bracketIdx);
    }
    m = FOR_OF_INLINE_ARRAY_RE.exec(stripped);
  }

  // [ ... ].forEach(<v> => ... delete <x>[<v>])
  FOREACH_INLINE_ARRAY_RE.lastIndex = 0;
  m = FOREACH_INLINE_ARRAY_RE.exec(stripped);
  while (m !== null) {
    const bracketIdx = m.index; // the `[` itself
    // Find the forEach callback param after the matching `].forEach(`.
    const tail = stripped.slice(bracketIdx, bracketIdx + DELETE_BODY_WINDOW);
    const cb = /\]\s*\.\s*forEach\s*\(\s*(?:function\s*)?\(?\s*([A-Za-z_$][\w$]*)/.exec(tail);
    if (cb) {
      const elemVar = cb[1];
      if (bodyDeletesWithVar(tail, elemVar)) {
        brackets.add(bracketIdx);
      }
    }
    m = FOREACH_INLINE_ARRAY_RE.exec(stripped);
  }

  return brackets;
}

/**
 * Per-construct correlation: flag a host-folder var quoted in an array literal
 * ONLY when THAT SAME array drives a computed delete -- either the array is a
 * named identifier consumed by a delete shape (collectDeleteDrivingArrayIdentifiers),
 * or the element sits in an inline array literal that is itself consumed by a
 * delete shape (collectInlineDrivingArrayBrackets). An unrelated array holding a
 * host-folder name (different identifier / not consumed by a delete) is NOT
 * flagged, eliminating the file-global co-occurrence false positive.
 *
 * @param {string} rawSource
 * @param {string} strippedSource
 * @returns {Array<{line: number, name: string}>}
 */
function findCorrelatedArrayDeletes(rawSource, strippedSource) {
  const elements = findArrayLiteralHostFolderVars(rawSource, strippedSource);
  if (elements.length === 0) {
    return [];
  }

  const drivingIdentifiers = collectDeleteDrivingArrayIdentifiers(strippedSource);
  const inlineBrackets = collectInlineDrivingArrayBrackets(strippedSource);
  if (drivingIdentifiers.size === 0 && inlineBrackets.size === 0) {
    return [];
  }

  const out = [];
  for (const el of elements) {
    const openBracket = enclosingArrayOpenBracket(strippedSource, el.slotStart);
    if (openBracket < 0) {
      continue;
    }
    // (a) Inline array literal directly consumed by a delete shape.
    if (inlineBrackets.has(openBracket)) {
      out.push({ line: el.line, name: el.name });
      continue;
    }
    // (b) Named array whose identifier drives a delete shape.
    const arrIdent = arrayAssignmentTarget(strippedSource, openBracket);
    if (arrIdent && drivingIdentifiers.has(arrIdent)) {
      out.push({ line: el.line, name: el.name });
    }
  }
  return out;
}

/**
 * Detect the case-sensitive host-folder-var delete anti-pattern in any of these
 * forms (all run on RAW source -- bracket-string/array member names are erased by
 * stripping -- with a stripped-source cross-check so a hit inside a comment or
 * string is discarded):
 *   - dot:     `delete <env>.ProgramFiles`
 *   - bracket: `delete <env>["ProgramFiles(x86)"]`
 *   - reflect: `Reflect.deleteProperty(<env>, "ProgramFiles")`
 *   - array:   a host-folder var quoted inside an ARRAY LITERAL where THAT SAME
 *              array drives a COMPUTED delete -- the
 *              `ENV_TO_DELETE = [..., "ProgramFiles", ...]; for (const k of
 *              ENV_TO_DELETE) delete env[k];` vector and its inline/forEach/
 *              direct-index siblings (see findCorrelatedArrayDeletes). An
 *              UNRELATED array holding a host-folder name (different identifier
 *              than the delete's source) is NOT flagged.
 *
 * @param {string} rawSource - Original source (LF-normalized, BOM-stripped).
 * @param {string} strippedSource - Source with comments/string payloads erased.
 * @returns {Array<{line: number, name: string, form: string}>}
 */
function findHostFolderDelete(rawSource, strippedSource) {
  const strippedLines = strippedSource.split("\n");
  const violations = [];

  // A hit is "real code" when its anchoring keyword survives the stripping pass
  // on its line (a keyword/identifier-token survives; a comment/string payload is
  // erased). The dot/bracket forms anchor on the `delete` keyword; the reflect
  // form anchors on `deleteProperty` (note `\bdelete\b` does NOT match inside
  // `deleteProperty`, so each form needs its own anchor token).
  const realCodeAt = (matchIndex, anchorRe) => {
    const line = lineNumberAt(rawSource, matchIndex);
    const strippedLine = strippedLines[line - 1] || "";
    return { isReal: anchorRe.test(strippedLine), line };
  };

  const scan = (pattern, form, anchorRe) => {
    pattern.lastIndex = 0;
    let match = pattern.exec(rawSource);
    while (match !== null) {
      const name = match[1];
      if (name && HOST_FOLDER_VAR_NAMES_LOWER.has(name.toLowerCase())) {
        const { isReal, line } = realCodeAt(match.index, anchorRe);
        if (isReal) {
          violations.push({ line, name, form });
        }
      }
      match = pattern.exec(rawSource);
    }
  };

  scan(DELETE_DOT_RE, "dot", /\bdelete\b/);
  scan(DELETE_BRACKET_RE, "bracket", /\bdelete\b/);
  scan(DELETE_REFLECT_RE, "reflect", /deleteProperty/);

  // Array-driven computed delete: flag a host-folder var quoted in an array
  // literal ONLY when THAT SAME array drives a computed delete (per-construct
  // correlation -- NOT a file-global co-occurrence). An unrelated array holding a
  // host-folder name plus an unrelated computed delete is NOT the anti-pattern,
  // so it stays silent -- which is why both unity-runner-strictmode-smoke.test.js
  // (computed delete loop over an array with NO host-folder vars) AND a file that
  // merely lists host-folder names in a fixture array next to an unrelated
  // cleanup loop are NOT flagged.
  for (const hit of findCorrelatedArrayDeletes(rawSource, strippedSource)) {
    violations.push({ line: hit.line, name: hit.name, form: "array" });
  }

  // De-duplicate by line+name+form so a line matched by multiple shapes reports
  // once per distinct form.
  const seen = new Set();
  return violations.filter((v) => {
    const key = `${v.line}:${v.name.toLowerCase()}:${v.form}`;
    if (seen.has(key)) {
      return false;
    }
    seen.add(key);
    return true;
  });
}

function toRepoRelative(absolutePath) {
  return path.relative(REPO_ROOT, absolutePath).split(path.sep).join("/");
}

describe("hermetic-host-env-policy (repo-wide)", () => {
  test("no test neutralizes host-default folder vars with a case-sensitive delete", () => {
    const offenders = [];

    for (const abs of listTestFiles()) {
      if (ALLOW_LIST.has(path.relative(REPO_ROOT, abs))) {
        continue;
      }
      const raw = readUtf8(abs);
      const stripped = stripJsCommentsAndStrings(raw);
      for (const hit of findHostFolderDelete(raw, stripped)) {
        offenders.push({ file: toRepoRelative(abs), ...hit });
      }
    }

    if (offenders.length > 0) {
      const details = offenders
        .map((o) => `  ${o.file}:${o.line} [${o.form} form]: host-folder var "${o.name}"`)
        .join("\n");
      throw new Error(
        "hermetic-host-env-policy violation: test(s) neutralize host-default folder " +
          "discovery with a case-sensitive delete.\n" +
          "Windows env-var NAMES are CASE-INSENSITIVE but a JS `delete env.ProgramFiles` " +
          "(or `delete env[k]` over an array holding the name, or " +
          "`Reflect.deleteProperty(env, 'ProgramFiles')`) is CASE-SENSITIVE, so a surviving " +
          "case-variant (PROGRAMFILES, ...) keeps the real folder visible to the child " +
          "process and a host install (e.g. real Unity) leaks into resolution. On Linux the " +
          "var is empty so the bug hides until run on Windows.\n" +
          "FIX: build the spawn env with sandboxHostFolderEnv(baseEnv, sandboxRootDir) from " +
          "scripts/lib/spawn-env-sandbox.js -- it removes EVERY case-variant of the " +
          "host-default folder vars and sets them to empty sandbox dirs.\n\n" +
          "Offending deletions:\n" +
          details
      );
    }
  });

  // -------------------------------------------------------------------------
  // Self-tests: prove the detector fires on the exact anti-patterns and does
  // NOT false-positive on the legitimate shapes.
  // -------------------------------------------------------------------------
  describe("detector self-tests", () => {
    const run = (raw) => findHostFolderDelete(raw, stripJsCommentsAndStrings(raw));

    test("flags delete env.ProgramFiles (dot form)", () => {
      expect(run("delete env.ProgramFiles;")).toHaveLength(1);
    });

    test("flags delete env.LOCALAPPDATA (dot form)", () => {
      expect(run("delete env.LOCALAPPDATA;")).toHaveLength(1);
    });

    test('flags delete env["ProgramFiles(x86)"] (bracket-string form)', () => {
      expect(run('delete env["ProgramFiles(x86)"];')).toHaveLength(1);
    });

    test("flags delete process.env.ProgramW6432 (nested base, dot form)", () => {
      expect(run("delete process.env.ProgramW6432;")).toHaveLength(1);
    });

    test("flags a case-variant var name (PROGRAMFILES)", () => {
      expect(run("delete env.PROGRAMFILES;")).toHaveLength(1);
    });

    test("flags delete env.CommonProgramFiles", () => {
      expect(run("delete env.CommonProgramFiles;")).toHaveLength(1);
    });

    test("does NOT flag delete env.UNITY_EDITOR_INSTALL_ROOT (not a host-folder var)", () => {
      expect(run("delete env.UNITY_EDITOR_INSTALL_ROOT;")).toHaveLength(0);
    });

    test("does NOT flag delete env.GITHUB_WORKSPACE", () => {
      expect(run("delete env.GITHUB_WORKSPACE;")).toHaveLength(0);
    });

    test("does NOT flag a commented-out delete env.ProgramFiles", () => {
      expect(run("// historical: delete env.ProgramFiles;")).toHaveLength(0);
    });

    test("does NOT flag the var name mentioned in a string literal", () => {
      expect(run('const note = "we used to delete env.ProgramFiles here";')).toHaveLength(0);
    });

    test("reports once when both an identifier and a host-folder var match (de-dup by name)", () => {
      // The dot pattern's `[\w$]*` final segment captures `ProgramFiles`; ensure
      // a single offending line yields exactly one violation.
      expect(run("delete env.ProgramFiles;")).toHaveLength(1);
    });

    test("is CRLF/BOM-safe: a BOM + CRLF source still flags the deletion", () => {
      const raw = normalizeToLf("\uFEFFdelete env.ProgramFiles;\r\n");
      expect(findHostFolderDelete(raw, stripJsCommentsAndStrings(raw))).toHaveLength(1);
    });

    // ---- Finding 4: extra terminators (`}`, `]`, EOF) for the dot form -------
    test("flags delete inside a block: if (x) { delete env.ProgramFiles }", () => {
      const hits = run("if (x) { delete env.ProgramFiles }");
      expect(hits).toHaveLength(1);
      expect(hits[0].form).toBe("dot");
    });

    test("flags delete as an array element: [delete env.ProgramFiles]", () => {
      expect(run("[delete env.ProgramFiles]")).toHaveLength(1);
    });

    test("flags a trailing delete with no terminator (EOF): delete env.ProgramFiles", () => {
      // No `;`, no newline, no closing bracket -- end of input must still close it.
      expect(run("delete env.ProgramFiles")).toHaveLength(1);
    });

    // ---- Finding 1b: Reflect.deleteProperty form ----------------------------
    test('flags Reflect.deleteProperty(env, "ProgramFiles")', () => {
      const hits = run('Reflect.deleteProperty(env, "ProgramFiles");');
      expect(hits).toHaveLength(1);
      expect(hits[0].form).toBe("reflect");
    });

    test("flags Reflect.deleteProperty with single quotes and a case-variant name", () => {
      expect(run("Reflect.deleteProperty(env, 'PROGRAMFILES');")).toHaveLength(1);
    });

    test('flags Reflect.deleteProperty on a nested base (Reflect.deleteProperty(process.env, "LOCALAPPDATA"))', () => {
      expect(run('Reflect.deleteProperty(process.env, "LOCALAPPDATA");')).toHaveLength(1);
    });

    test("does NOT flag Reflect.deleteProperty for a non-host var", () => {
      expect(run('Reflect.deleteProperty(env, "GITHUB_WORKSPACE");')).toHaveLength(0);
    });

    test("does NOT flag a commented-out Reflect.deleteProperty", () => {
      expect(run('// Reflect.deleteProperty(env, "ProgramFiles");')).toHaveLength(0);
    });

    // ---- Finding 1a: array-driven computed delete ---------------------------
    test("flags the array + computed-delete-loop vector (the exact evading shape)", () => {
      const raw = [
        'const ENV_TO_DELETE = ["ProgramFiles"];',
        "for (const k of ENV_TO_DELETE) delete env[k];"
      ].join("\n");
      const hits = run(raw);
      expect(hits).toHaveLength(1);
      expect(hits[0].form).toBe("array");
      expect(hits[0].name).toBe("ProgramFiles");
    });

    test("flags a multi-line array holding a host-folder var with a delete loop", () => {
      const raw = [
        "const ENV_TO_DELETE = [",
        '  "UNITY_LICENSE",',
        '  "ProgramFiles",',
        '  "GITHUB_WORKSPACE"',
        "];",
        "for (const key of ENV_TO_DELETE) {",
        "  delete env[key];",
        "}"
      ].join("\n");
      const hits = run(raw);
      expect(hits.some((h) => h.form === "array" && h.name === "ProgramFiles")).toBe(true);
    });

    test("flags the array vector when the computed delete uses Reflect.deleteProperty(env, key)", () => {
      const raw = [
        'const vars = ["LOCALAPPDATA"];',
        "for (const k of vars) Reflect.deleteProperty(env, k);"
      ].join("\n");
      const hits = run(raw);
      expect(hits.some((h) => h.form === "array")).toBe(true);
    });

    test("flags a case-variant host-folder name in the array (PROGRAMFILES)", () => {
      const raw = ['const vars = ["PROGRAMFILES"];', "for (const k of vars) delete env[k];"].join(
        "\n"
      );
      expect(run(raw).some((h) => h.form === "array")).toBe(true);
    });

    // ---- Array-form precision: NO false positives ---------------------------
    test("does NOT flag a computed delete over an array with NO host-folder vars (the strictmode-smoke shape)", () => {
      const raw = [
        'const ENV_TO_DELETE = ["UNITY_LICENSE", "GITHUB_WORKSPACE", "GITHUB_ACTIONS"];',
        "for (const key of ENV_TO_DELETE) {",
        "  delete env[key];",
        "}"
      ].join("\n");
      expect(run(raw)).toHaveLength(0);
    });

    test("does NOT flag a host-folder var in an array literal when there is NO computed delete", () => {
      // A host-folder name listed in an array but never deleted via a computed
      // member access is not the anti-pattern (e.g. it is SET, or documented).
      const raw = 'const canonical = ["ProgramFiles", "LOCALAPPDATA"];';
      expect(run(raw)).toHaveLength(0);
    });

    test("does NOT flag a host-folder var in a COMMENTED array even with a computed delete", () => {
      const raw = [
        '// const ENV_TO_DELETE = ["ProgramFiles"];',
        "for (const k of other) delete env[k];"
      ].join("\n");
      expect(run(raw)).toHaveLength(0);
    });

    test("does NOT flag a host-folder var embedded in a STRING that merely looks array-like", () => {
      // The array brackets are string payload, not real array structure.
      const raw = [
        'const note = "[\\"ProgramFiles\\"]";',
        "for (const k of other) delete env[k];"
      ].join("\n");
      expect(run(raw)).toHaveLength(0);
    });

    test("does NOT flag a host-folder name used as a plain (non-array) string argument", () => {
      const raw = ['foo("ProgramFiles");', "for (const k of other) delete env[k];"].join("\n");
      expect(run(raw)).toHaveLength(0);
    });

    // ---- Per-construct correlation: the Finding-1 false positive -------------
    test("does NOT flag an UNRELATED array holding host-folder names beside an UNRELATED computed delete (Finding 1 FP)", () => {
      // The expectedCanonical array and the staleKeys delete-loop are DIFFERENT
      // constructs; file-global co-occurrence must not flag them.
      const raw = [
        'const expectedCanonical = ["ProgramFiles", "LOCALAPPDATA"];',
        "expect(canonicalVars).toEqual(expectedCanonical);",
        'const staleKeys = ["a", "b"];',
        "for (const k of staleKeys) { delete cache[k]; }"
      ].join("\n");
      expect(run(raw)).toHaveLength(0);
    });

    test("does NOT flag a named array WITH a host-folder var that is NOT the delete's iterable (different identifier)", () => {
      const raw = [
        'const hostNames = ["ProgramFiles"];',
        "for (const n of hostNames) console.log(n);",
        'const toDrop = ["UNITY_LICENSE"];',
        "for (const k of toDrop) delete env[k];"
      ].join("\n");
      expect(run(raw)).toHaveLength(0);
    });

    // ---- Per-construct correlation: the additional ENFORCED shapes ----------
    test('flags an INLINE for-of array literal driving the delete (for (k of ["LOCALAPPDATA"]) delete env[k])', () => {
      const hits = run('for (const k of ["LOCALAPPDATA"]) delete env[k];');
      expect(hits).toHaveLength(1);
      expect(hits[0].form).toBe("array");
      expect(hits[0].name).toBe("LOCALAPPDATA");
    });

    test('flags an INLINE forEach array literal driving the delete (["ProgramFiles"].forEach(k => delete env[k]))', () => {
      const hits = run('["ProgramFiles"].forEach((k) => delete env[k]);');
      expect(hits.some((h) => h.form === "array" && h.name === "ProgramFiles")).toBe(true);
    });

    test("flags a NAMED forEach array driving the delete (vars.forEach(k => delete env[k]))", () => {
      const raw = ['const vars = ["ProgramFiles"];', "vars.forEach((k) => delete env[k]);"].join(
        "\n"
      );
      expect(run(raw).some((h) => h.form === "array" && h.name === "ProgramFiles")).toBe(true);
    });

    test("flags a NAMED forEach with the function(){} callback form", () => {
      const raw = [
        'const vars = ["CommonProgramFiles"];',
        "vars.forEach(function (k) { delete env[k]; });"
      ].join("\n");
      expect(run(raw).some((h) => h.form === "array")).toBe(true);
    });

    test("flags a DIRECT-INDEX delete into a named array (delete env[vars[0]])", () => {
      const raw = ['const vars = ["LOCALAPPDATA"];', "delete env[vars[0]];"].join("\n");
      expect(run(raw).some((h) => h.form === "array" && h.name === "LOCALAPPDATA")).toBe(true);
    });

    test("flags Reflect.deleteProperty DIRECT-INDEX into a named array (Reflect.deleteProperty(env, vars[0]))", () => {
      const raw = ['const vars = ["ProgramW6432"];', "Reflect.deleteProperty(env, vars[0]);"].join(
        "\n"
      );
      expect(run(raw).some((h) => h.form === "array")).toBe(true);
    });

    test("does NOT flag a named array driving a delete whose body does NOT use the loop variable", () => {
      // The loop iterates the host-folder array but the delete targets a fixed
      // non-host key, so the array does not actually drive a host-folder delete.
      const raw = [
        'const hostNames = ["ProgramFiles"];',
        "for (const n of hostNames) delete env.SOME_FIXED_KEY;"
      ].join("\n");
      // `delete env.SOME_FIXED_KEY` is a non-host dot delete (not flagged), and the
      // array is not correlated to a computed delete via `n`, so: zero hits.
      expect(run(raw)).toHaveLength(0);
    });

    // ---- Documented residual (see file JSDoc) -------------------------------
    test("DOCUMENTED RESIDUAL: a fully-indirect (runtime-assembled) host-folder name is NOT caught", () => {
      // The name is built at runtime (concatenation), so it never appears as a
      // literal string the source scanner can see. Source-scanning guards cannot
      // resolve runtime values; the sandboxHostFolderEnv golden test + the
      // policy that host-spawning tests use the helper are the backstops.
      const raw = [
        'const vars = ["Program" + "Files"];',
        "for (const k of vars) delete env[k];"
      ].join("\n");
      expect(run(raw)).toHaveLength(0);
    });

    test('DOCUMENTED RESIDUAL: a scalar-variable-routed literal (const k = "ProgramFiles"; delete env[k]) is NOT caught', () => {
      // The host-folder name is a literal but it is routed through a SCALAR, not a
      // literal element of an array that drives the delete. The array-form
      // correlation only sees array literals; this indirection is residual.
      const raw = ['const k = "ProgramFiles";', "delete env[k];"].join("\n");
      expect(run(raw)).toHaveLength(0);
    });

    test('DOCUMENTED RESIDUAL: a push-built array (arr.push("ProgramFiles"); for (k of arr) delete env[k]) is NOT caught', () => {
      // The name enters the delete-driving array via `.push()`, so it is never a
      // literal ELEMENT of the array LITERAL the scanner inspects. Residual.
      const raw = [
        "const arr = [];",
        'arr.push("ProgramFiles");',
        "for (const k of arr) delete env[k];"
      ].join("\n");
      expect(run(raw)).toHaveLength(0);
    });
  });
});
