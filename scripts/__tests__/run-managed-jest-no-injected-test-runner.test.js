/**
 * @fileoverview Static-source regression guard for the managed Jest wrapper.
 *
 * Root cause history:
 *   On Windows, jest-config's runner validator has been observed to reject
 *   absolute paths that `require.resolve("jest-circus/runner")` and
 *   `fs.existsSync` both report as valid, producing:
 *     "Module <abs-path> in the testRunner option was not found.
 *      <rootDir> is: <repo>"
 *
 *   The original mitigation in `scripts/run-managed-jest.js` injected
 *   `--testRunner <abs-path>` into Jest's argv. This created the exact
 *   failure surface above. Removing the injection (Jest 27+ defaults to
 *   `jest-circus` and resolves its bundled runner internally) eliminates the
 *   entire category of failures.
 *
 * This test pins the policy at the source level. It scans the wrapper source
 * for any code that would push `--testRunner` into the Jest invocation
 * arguments. Caller-provided `--testRunner` is still forwarded unchanged via
 * the input `args` array (which is appended wholesale), so detection of
 * argv-construction patterns will not produce false positives.
 *
 * If you have a legitimate need to inject `--testRunner` (e.g., a sandbox
 * harness that cannot use Jest's default resolver), DO NOT relax this guard:
 * route the path through the caller-supplied `args` array instead, which
 * remains a supported public contract.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const { stripJsCommentsAndStrings } = require("../lib/source-stripping");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const RUN_MANAGED_JEST_PATH = path.join(REPO_ROOT, "scripts", "run-managed-jest.js");

describe("run-managed-jest source: forbidden --testRunner injection patterns", () => {
  let source;
  let strippedSource;

  beforeAll(() => {
    source = fs.readFileSync(RUN_MANAGED_JEST_PATH, "utf8");
    strippedSource = stripJsCommentsAndStrings(source);
  });

  test("source file is readable and non-trivial", () => {
    expect(typeof source).toBe("string");
    expect(source.length).toBeGreaterThan(1000);
  });

  test("source does not push '--testRunner' onto an invocation args array", () => {
    // Matches any *.push("--testRunner", ...) where the literal would
    // survive comment/string stripping only if it was actual code.
    // After stripping all string literals the substring "--testRunner"
    // cannot legally remain in code, so any match is a violation.
    const violations = strippedSource
      .split("\n")
      .map((line, index) => ({ line, lineNumber: index + 1 }))
      .filter(({ line }) => line.includes("--testRunner"));

    if (violations.length > 0) {
      const formatted = violations
        .map(({ line, lineNumber }) => `  ${RUN_MANAGED_JEST_PATH}:${lineNumber}: ${line.trim()}`)
        .join("\n");
      throw new Error(
        "Forbidden --testRunner code reference detected after stripping " +
          "comments and string literals. Jest 27+ resolves its bundled " +
          "jest-circus runner internally; injecting absolute paths is a " +
          "known Windows failure mode (jest-config runner validator " +
          "rejection). Forward caller-supplied --testRunner via the " +
          "input args array instead.\n\nOffending lines:\n" +
          formatted
      );
    }
  });

  test("source does not construct an invocationArgs push for --testRunner via concatenation", () => {
    // Defense-in-depth: catch attempts to hide the literal by splitting
    // it across concatenation (e.g., "--test" + "Runner"). After string
    // stripping, no such concatenation can produce the option, so this
    // test primarily documents intent and asserts the stripped source is
    // free of test-runner-flag concatenations.
    const suspiciousConcatenation = /testRunner\s*['"]?\s*\+/i;
    expect(suspiciousConcatenation.test(strippedSource)).toBe(false);
  });

  test("invocationArgs.push call sites do not reference a runner path identifier", () => {
    // Find all occurrences of `invocationArgs.push(...)` and ensure none
    // of them reference a known runner-path identifier as their first
    // argument. This is the structural guard: even if the literal were
    // obfuscated, pushing a *resolved jest-circus path* would still need
    // to reference one of these identifiers somewhere in the call.
    const forbiddenIdentifiers = ["resolvedRunnerPath", "jestRunnerPath", "circusRunnerPath"];
    const pushCallRegex = /invocationArgs\.push\s*\(([^)]*)\)/g;
    const violations = [];
    let match;
    while ((match = pushCallRegex.exec(strippedSource)) !== null) {
      const callArgs = match[1];
      for (const id of forbiddenIdentifiers) {
        const idRegex = new RegExp(`\\b${id}\\b`);
        if (idRegex.test(callArgs)) {
          violations.push({ callArgs: callArgs.trim(), id });
        }
      }
    }

    if (violations.length > 0) {
      const formatted = violations
        .map(
          (v) => `  invocationArgs.push(${v.callArgs}) referenced forbidden identifier '${v.id}'`
        )
        .join("\n");
      throw new Error(
        "invocationArgs.push() call references a runner-path identifier. " +
          "This re-introduces the Jest 30 / Windows '--testRunner' injection " +
          "failure mode. Forward caller-supplied --testRunner via the input " +
          "args array instead.\n\n" +
          formatted
      );
    }
  });
});
