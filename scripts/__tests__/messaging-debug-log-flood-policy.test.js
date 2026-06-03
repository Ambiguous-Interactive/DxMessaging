/**
 * @fileoverview Static contract that locks the Unity test-harness log-flood fix.
 *
 * Background:
 *   A passing Unity 2022 PlayMode CI run produced a ~73MB unity.log. Root cause:
 *   `Tests/Runtime/Core/MessagingTestBase.cs` installed a `MessagingDebug.LogFunction`
 *   that routed `LogLevel.Debug` AND `LogLevel.Info` to `UnityEngine.Debug.Log`
 *   UNCONDITIONALLY. The bus emits an `Info` "Could not find a matching ... handler"
 *   line on normal deregistered-emit flow; over ~10,174 emits each `Debug.Log`
 *   captured a full stack trace, ballooning the log. The per-test
 *   `LogMessageBusStatus()` dump and the one-time seed line were secondary
 *   contributors via the same `Debug.Log` path.
 *
 *   The fix gates every high-frequency `Debug.Log` site behind an opt-in
 *   environment variable `DXM_TEST_VERBOSE_LOG` (default OFF), leaving the
 *   Warn/Error routing untouched. This file pins that fix so it cannot silently
 *   regress.
 *
 * Two layers:
 *   (A) NARROW contract on `MessagingTestBase.cs` only - the precise structural
 *       shape of the fix (env var name, the `VerboseConsoleLogging` member + its
 *       resolver, the gated Debug/Info arm, the preserved Warn/Error arms, the
 *       untouched `MessagingDebug.enabled` assignment, and the verbose-gated
 *       `LogMessageBusStatus` + seed line).
 *   (B) A FALSE-POSITIVE-RESISTANT repo-wide guard scoped to each
 *       `MessagingDebug.LogFunction = ...` lambda body. It flags ONLY a
 *       regression to UNCONDITIONAL `Info`/`Debug`-to-`Debug.Log` routing.
 *
 * WHY the repo-wide scan is intentionally narrowed:
 *   Most C# files under Tests/ install their OWN `MessagingDebug.LogFunction` for
 *   a single assertion and restore the previous one in a `finally`. These are
 *   legitimate and must NOT be flagged:
 *     - capturing installers push messages into a List (`=> logs.Add(...)`),
 *     - no-op installers use `(_, _) => { }`,
 *     - disabling installers set `= null`,
 *     - all of the above save the prior function (`previous`/`saved`) and
 *       restore it.
 *   A blunt "any file that routes Info to Debug.Log" scan would false-positive on
 *   every one of these. So the guard fires only when a lambda body BOTH routes an
 *   `Info`/`Debug` level to `Debug.Log(` AND lacks any verbose-gate token AND
 *   lacks any of the capturing/no-op/restoring exclusion tokens. In practice this
 *   reduces to: "MessagingTestBase's Setup arm must not regress to an
 *   unconditional `Debug.Log` under the Info/Debug case."
 *
 * Fast static text assertions only (no Unity, no spawn).
 */

"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const MESSAGING_TEST_BASE = path.join(
  REPO_ROOT,
  "Tests",
  "Runtime",
  "Core",
  "MessagingTestBase.cs"
);
const TESTS_DIR = path.join(REPO_ROOT, "Tests");

const VERBOSE_ENV_VAR = "DXM_TEST_VERBOSE_LOG";
const VERBOSE_FLAG_TOKEN = "VerboseConsoleLogging";

function read(file) {
  return fs.readFileSync(file, "utf8");
}

/**
 * Blank out C# line and block comments so commented-out code
 * cannot produce a false positive or hide a real regression. Newlines are
 * preserved so 1-based line reporting stays accurate. Verbatim/regular string
 * contents are intentionally left as-is: the tokens we search for
 * (`Debug.Log(`, `LogLevel.Info`, etc.) appearing inside a string literal would
 * still be a meaningful signal and are not expected in these test bodies.
 */
function stripCSharpComments(source) {
  let out = "";
  let i = 0;
  const n = source.length;
  let inLineComment = false;
  let inBlockComment = false;
  let inString = false;
  let inChar = false;
  let inVerbatim = false;

  while (i < n) {
    const c = source[i];
    const next = i + 1 < n ? source[i + 1] : "";

    if (inLineComment) {
      if (c === "\n") {
        inLineComment = false;
        out += c;
      } else {
        out += c === "\t" ? c : " ";
      }
      i++;
      continue;
    }

    if (inBlockComment) {
      if (c === "*" && next === "/") {
        inBlockComment = false;
        out += "  ";
        i += 2;
        continue;
      }
      out += c === "\n" ? "\n" : c === "\t" ? c : " ";
      i++;
      continue;
    }

    if (inVerbatim) {
      out += c;
      // `""` is an escaped quote inside a verbatim string.
      if (c === '"' && next === '"') {
        out += next;
        i += 2;
        continue;
      }
      if (c === '"') {
        inVerbatim = false;
      }
      i++;
      continue;
    }

    if (inString) {
      out += c;
      if (c === "\\" && next) {
        out += next;
        i += 2;
        continue;
      }
      if (c === '"') {
        inString = false;
      }
      i++;
      continue;
    }

    if (inChar) {
      out += c;
      if (c === "\\" && next) {
        out += next;
        i += 2;
        continue;
      }
      if (c === "'") {
        inChar = false;
      }
      i++;
      continue;
    }

    // Not currently inside any comment/string/char.
    if (c === "/" && next === "/") {
      inLineComment = true;
      out += "  ";
      i += 2;
      continue;
    }
    if (c === "/" && next === "*") {
      inBlockComment = true;
      out += "  ";
      i += 2;
      continue;
    }
    if (c === "@" && next === '"') {
      inVerbatim = true;
      out += c + next;
      i += 2;
      continue;
    }
    if (c === '"') {
      inString = true;
      out += c;
      i++;
      continue;
    }
    if (c === "'") {
      inChar = true;
      out += c;
      i++;
      continue;
    }

    out += c;
    i++;
  }

  return out;
}

/**
 * Recursively collect every `*.cs` file under `Tests/`.
 */
function listTestCsFiles() {
  const out = [];
  const stack = [TESTS_DIR];
  const skipDirs = new Set(["obj", "bin", ".vs"]);
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
        if (skipDirs.has(entry.name)) {
          continue;
        }
        stack.push(path.join(dir, entry.name));
        continue;
      }
      if (entry.isFile() && entry.name.endsWith(".cs")) {
        out.push(path.join(dir, entry.name));
      }
    }
  }
  out.sort();
  return out;
}

/**
 * Return the 1-based line number for a character offset into `text`.
 */
function lineNumberAt(text, offset) {
  let line = 1;
  for (let i = 0; i < offset && i < text.length; i++) {
    if (text[i] === "\n") {
      line++;
    }
  }
  return line;
}

/**
 * Extract every `MessagingDebug.LogFunction = ...` assignment body from
 * (comment-stripped) C# source. The "body" is a bounded window starting at the
 * assignment and running to the end of the statement: for a lambda assignment
 * we walk to the matching close brace + trailing `;` (balanced-brace scan); for
 * a simple expression assignment (`= null;`, `= previous;`) we stop at the
 * first `;`. A hard char cap bounds pathological input.
 *
 * @returns {Array<{startOffset:number, body:string}>}
 */
function extractLogFunctionAssignments(strippedSource) {
  const results = [];
  const marker = "MessagingDebug.LogFunction =";
  const MAX_WINDOW = 4000;
  let searchFrom = 0;

  for (;;) {
    const idx = strippedSource.indexOf(marker, searchFrom);
    if (idx === -1) {
      break;
    }
    searchFrom = idx + marker.length;

    const windowEnd = Math.min(strippedSource.length, idx + MAX_WINDOW);
    const slice = strippedSource.slice(idx, windowEnd);

    // Find the assignment's RHS start (after the `=`).
    const eq = slice.indexOf("=");
    const rhs = slice.slice(eq + 1);

    // Determine whether the RHS is a brace-bearing lambda or a simple value.
    const firstBrace = rhs.indexOf("{");
    const firstSemicolon = rhs.indexOf(";");

    let bodyEndInRhs;
    if (firstBrace !== -1 && (firstSemicolon === -1 || firstBrace < firstSemicolon)) {
      // Balanced-brace scan from the first `{`.
      let depth = 0;
      let j = firstBrace;
      let closed = -1;
      for (; j < rhs.length; j++) {
        const ch = rhs[j];
        if (ch === "{") {
          depth++;
        } else if (ch === "}") {
          depth--;
          if (depth === 0) {
            closed = j;
            break;
          }
        }
      }
      if (closed === -1) {
        // Unbalanced within the window; take the whole window.
        bodyEndInRhs = rhs.length;
      } else {
        // Include a trailing `;` if present right after the close brace.
        const afterBrace = rhs.slice(closed + 1, closed + 4);
        const semiOffset = afterBrace.indexOf(";");
        bodyEndInRhs = semiOffset === -1 ? closed + 1 : closed + 1 + semiOffset + 1;
      }
    } else if (firstSemicolon !== -1) {
      bodyEndInRhs = firstSemicolon + 1;
    } else {
      bodyEndInRhs = rhs.length;
    }

    const body = rhs.slice(0, bodyEndInRhs);
    results.push({ startOffset: idx, body });
  }

  return results;
}

// Tokens that mark an installer as a legitimate capturing/no-op/restoring shape
// (and therefore NOT a flood regression even if it mentions Debug.Log / Info).
const EXCLUSION_TOKENS = [
  "previous", // save+restore: `= previous;` / `previousLog`
  "restore",
  "saved", // `_savedLogFunction`
  ".Add(", // capturing into a List
  "=> { }", // no-op lambda (spaced)
  "=> {}", // no-op lambda (compact)
  "= null" // disabling installer
];

function bodyLooksExcluded(body) {
  for (const token of EXCLUSION_TOKENS) {
    if (body.includes(token)) {
      return true;
    }
  }
  return false;
}

function bodyIsVerboseGated(body) {
  return body.includes(VERBOSE_FLAG_TOKEN);
}

function bodyRoutesInfoOrDebugToConsole(body) {
  const routesToConsole = body.includes("Debug.Log(");
  const mentionsInfoOrDebug = body.includes("LogLevel.Info") || body.includes("LogLevel.Debug");
  return routesToConsole && mentionsInfoOrDebug;
}

// ---------------------------------------------------------------------------
// Layer A: narrow contract on MessagingTestBase.cs
// ---------------------------------------------------------------------------
describe("MessagingTestBase log-flood gate (narrow contract)", () => {
  const raw = read(MESSAGING_TEST_BASE);
  const stripped = stripCSharpComments(raw);

  test(`references the ${VERBOSE_ENV_VAR} environment variable`, () => {
    expect(stripped).toContain(VERBOSE_ENV_VAR);
  });

  test("declares a VerboseConsoleLogging member and a ResolveVerboseConsoleLogging helper", () => {
    expect(stripped).toMatch(/\bVerboseConsoleLogging\b/);
    expect(stripped).toMatch(/\bResolveVerboseConsoleLogging\s*\(/);
    // The resolver must actually read the env var (effective behavior, not a
    // dangling helper).
    expect(stripped).toMatch(
      new RegExp(
        `GetEnvironmentVariable\\([^)]*${VERBOSE_ENV_VAR}|GetEnvironmentVariable\\(\\s*VerboseLogEnvVar`
      )
    );
  });

  test("the Setup LogFunction Debug/Info arm guards Debug.Log behind the verbose flag", () => {
    const assignments = extractLogFunctionAssignments(stripped);
    expect(assignments.length).toBeGreaterThan(0);

    // MessagingTestBase installs exactly one routing lambda in Setup; find the
    // one that routes Info/Debug (the others, if any, would be excluded shapes).
    const routing = assignments.find((a) => bodyRoutesInfoOrDebugToConsole(a.body));
    expect(routing).toBeDefined();

    const body = routing.body;

    // The verbose flag must appear, and it must appear BEFORE the Debug.Log(
    // call so the guard wraps the call (not merely sit somewhere in the body).
    const guardIdx = body.indexOf(VERBOSE_FLAG_TOKEN);
    const logIdx = body.indexOf("Debug.Log(");
    expect(guardIdx).toBeGreaterThanOrEqual(0);
    expect(logIdx).toBeGreaterThanOrEqual(0);
    expect(guardIdx).toBeLessThan(logIdx);

    // Regression shape: an unconditional `Debug.Log(message);` sitting directly
    // under `case LogLevel.Info:` with no intervening verbose guard must NOT be
    // present.
    const unconditionalInfoArm = /case\s+LogLevel\.Info\s*:\s*Debug\.Log\s*\(\s*message\s*\)\s*;/;
    expect(unconditionalInfoArm.test(body)).toBe(false);
  });

  test("the Warn arm maps to Debug.LogWarning and the Error arm to Debug.LogError", () => {
    // Preserved exactly: Warn -> Debug.LogWarning, Error -> Debug.LogError.
    expect(stripped).toMatch(
      /case\s+LogLevel\.Warn\s*:\s*Debug\.LogWarning\s*\(\s*message\s*\)\s*;/
    );
    expect(stripped).toMatch(
      /case\s+LogLevel\.Error\s*:\s*Debug\.LogError\s*\(\s*message\s*\)\s*;/
    );
  });

  test("the MessagingDebug.enabled assignment is unchanged", () => {
    expect(stripped).toMatch(/MessagingDebug\.enabled\s*=\s*MessagingDebugEnabled\s*;/);
  });

  test("LogMessageBusStatus is verbose-gated", () => {
    const match = /void\s+LogMessageBusStatus\s*\([^)]*\)\s*\{([\s\S]*?)\n\s{8}\}/.exec(stripped);
    expect(match).not.toBeNull();
    expect(match[1]).toContain(VERBOSE_FLAG_TOKEN);
  });

  test("the one-time seed log line is verbose-gated", () => {
    const match = /void\s+LogTestSeedOnce\s*\([^)]*\)\s*\{([\s\S]*?)\n\s{8}\}/.exec(stripped);
    expect(match).not.toBeNull();
    expect(match[1]).toContain(VERBOSE_FLAG_TOKEN);
    // And the seed Debug.Log still exists in the body (gated, not deleted).
    expect(match[1]).toContain("Debug.Log(");
  });
});

// ---------------------------------------------------------------------------
// Layer B: false-positive-resistant repo-wide regression guard
// ---------------------------------------------------------------------------
describe("no unconditional Info/Debug -> Debug.Log routing in any test LogFunction", () => {
  test("every MessagingDebug.LogFunction lambda that routes Info/Debug to Debug.Log is verbose-gated", () => {
    const offenders = [];

    for (const file of listTestCsFiles()) {
      const raw = read(file);
      const stripped = stripCSharpComments(raw);
      const assignments = extractLogFunctionAssignments(stripped);

      for (const assignment of assignments) {
        const { body, startOffset } = assignment;

        if (!bodyRoutesInfoOrDebugToConsole(body)) {
          continue; // does not route Info/Debug to the console at all
        }
        if (bodyIsVerboseGated(body)) {
          continue; // gated behind the verbose flag - the intended shape
        }
        if (bodyLooksExcluded(body)) {
          continue; // capturing / no-op / restoring installer - legitimate
        }

        offenders.push({
          file: path.relative(REPO_ROOT, file),
          line: lineNumberAt(stripped, startOffset)
        });
      }
    }

    if (offenders.length > 0) {
      const formatted = offenders.map((o) => `  ${o.file}:${o.line}`).join("\n");
      throw new Error(
        "Log-flood regression: a test installed a MessagingDebug.LogFunction that " +
          "routes LogLevel.Info/Debug to UnityEngine.Debug.Log UNCONDITIONALLY.\n" +
          "Each such Debug.Log captures a full stack trace and floods the Unity log " +
          "(this is the ~73MB CI-log root cause). Gate the Debug.Log behind " +
          `${VERBOSE_FLAG_TOKEN} (env ${VERBOSE_ENV_VAR}), or - for a single-assertion ` +
          "installer - capture into a List / use a no-op lambda / set null and restore " +
          "the previous function in a finally.\n\nOffending installers:\n" +
          formatted
      );
    }
  });
});
