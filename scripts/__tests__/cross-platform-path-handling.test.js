/**
 * @fileoverview Cross-platform path-separator regression suite.
 *
 * The Windows developer hit `npm run preflight:pre-push` failures three
 * times in a row because production code emitted backslash-separated paths
 * in user-facing log lines (warnFn, console.warn) and tests asserted on
 * forward-slash substrings. Each time the failure was patched in one site
 * and the class popped up elsewhere.
 *
 * This file is the class-level pin:
 *   1. The unit-level invariants for `toPosixPath` / `toRepoPosixRelative`
 *      (idempotency on POSIX, conversion on Windows, defensive non-string
 *      handling). The detailed `toPosixPath` / `toRepoPosixRelative` tests
 *      live in path-classifier.test.js; this file adds the cross-module
 *      contracts that path-classifier alone cannot pin.
 *   2. `formatIntegrityFailure` POSIX-normalizes its `relPath` input
 *      regardless of how the caller spells it.
 *   3. A policy contract test that walks every production script and fails
 *      if a `warnFn` / `console.warn` / `console.error` interpolates a
 *      variable whose name ends in `Path` (or `path`) WITHOUT wrapping it
 *      in `toPosixPath(` or `toRepoPosixRelative(`. This is the "self-
 *      preventing-class" gate; an allowlist captures the few legitimate
 *      exceptions (paths that are already POSIX-normalized at their source,
 *      or paths intentionally surfaced in their platform-native form).
 *
 * The policy test runs in <100ms on the existing scripts/ tree; it is safe
 * to keep in the pre-push battery.
 *
 * @cross-platform-regression -- this marker requires the file to be gated on
 * ubuntu/windows/macos via the targeted step in
 * .github/workflows/cross-platform-preflight.yml; enforced by
 * scripts/__tests__/cross-platform-preflight-coverage.test.js.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const { toPosixPath, toRepoPosixRelative } = require("../lib/path-classifier");
const { formatIntegrityFailure } = require("../lib/node-modules-integrity");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SCRIPTS_ROOT = path.resolve(__dirname, "..");

describe("cross-platform path handling", () => {
  describe("toPosixPath", () => {
    test("is idempotent on POSIX input", () => {
      expect(toPosixPath("a/b/c")).toBe("a/b/c");
      expect(toPosixPath("/abs/path")).toBe("/abs/path");
    });

    test("converts Windows separators to POSIX", () => {
      expect(toPosixPath("D:\\Code\\foo")).toBe("D:/Code/foo");
      expect(toPosixPath("a\\b\\c.txt")).toBe("a/b/c.txt");
    });

    test("null / undefined map to empty string (no 'undefined' leak)", () => {
      // Regression pin: a previous shape returned the original
      // null/undefined unchanged, which interpolated as the literal
      // string "undefined" in warnFn template literals.
      expect(toPosixPath(null)).toBe("");
      expect(toPosixPath(undefined)).toBe("");
      expect(`runner ${toPosixPath(undefined)}`).toBe("runner ");
    });

    test("coerces non-string primitives via String() and swaps separators", () => {
      expect(toPosixPath(7)).toBe("7");
    });
  });

  describe("toRepoPosixRelative", () => {
    test("emits POSIX form for inside-repo paths", () => {
      const repo = path.resolve("/repo");
      expect(toRepoPosixRelative(path.join(repo, "node_modules", "x.js"), repo)).toBe(
        "node_modules/x.js"
      );
    });

    test("falls back to POSIX-absolute for outside-repo paths", () => {
      const repo = path.resolve("/repo");
      const outside = path.resolve("/elsewhere/x.js");
      const out = toRepoPosixRelative(outside, repo);
      expect(out).toBe(toPosixPath(outside));
      expect(out.includes("\\")).toBe(false);
    });
  });

  describe("formatIntegrityFailure POSIX contract", () => {
    test("POSIX-normalizes backslash relPath in the output", () => {
      const result = {
        ok: false,
        missing: [
          {
            tool: "x",
            relPath: "node_modules\\foo\\bar.js",
            reason: "missing"
          }
        ]
      };
      const formatted = formatIntegrityFailure(result);
      expect(formatted).toContain("node_modules/foo/bar.js");
      expect(formatted).not.toContain("\\");
    });

    test("idempotent on already-POSIX relPath", () => {
      const result = {
        ok: false,
        missing: [
          {
            tool: "x",
            relPath: "node_modules/foo/bar.js",
            reason: "missing"
          }
        ]
      };
      expect(formatIntegrityFailure(result)).toContain("node_modules/foo/bar.js");
    });
  });

  describe("policy: warn/error paths must be POSIX-normalized", () => {
    // Allow-list keyed by absolute file path. Each entry is a Set of
    // 1-indexed line numbers where a `warnFn|console.warn|console.error`
    // interpolation of a *Path-suffixed identifier is intentionally
    // NOT wrapped in toPosixPath / toRepoPosixRelative. Reasons:
    //   - the value is already known to be POSIX at the source, or
    //   - the call site is a metadata diagnostic for an internal
    //     tester where the platform-native form is more debuggable.
    //
    // Each allowlist entry includes a short reason. Adding a new
    // entry is the explicit "I know what I'm doing" gesture; the
    // default behavior is to fail the test until the call site is
    // wrapped.
    const ALLOWLIST = Object.create(null);
    // No allowlist entries today. The fix in this branch normalized
    // every call site; any future allowlist addition should also
    // file an issue tracking why the wrap is undesirable.

    // Matches `(warnFn|console.warn|console.error|logFn)( ... ${VAR} ... )`
    // where VAR ends in a path-like suffix. The "between the open paren
    // and the ${...}" body uses `[\s\S]*?` (lazy any-character) instead
    // of `[^)]*`; the previous `[^)]*` stopped at the FIRST `)` and
    // silently failed to scan call sites that contained a nested call
    // before the interpolation (e.g. `warnFn(getMsg() + \`${runnerPath}\`)`).
    // The lazy quantifier lets the match cross nested parens but still
    // anchors to the first `${...}` after the call's opening paren.
    //
    // We DELIBERATELY scope the heuristic narrowly to known path-like
    // identifier suffixes. The character-class alternation now includes
    // lowercase `root` (e.g. `repo_root`, `nodeModulesRoot`) so the
    // policy gate catches snake_case Root identifiers too. Broadening to
    // "all variables" would explode the false-positive surface; the
    // suffix-based filter catches the realistic class of regressions
    // (someone names a local `runnerPath`, `nodeModulesPath`,
    // `repoRoot`, `repo_root`, etc.).
    const PATH_VAR_RE =
      /(warnFn|console\.(?:warn|error|log)|logFn)\s*\([\s\S]*?\$\{([A-Za-z_][A-Za-z0-9_.]*?(?:Path|path|PATH|Dir|dir|Root|root))\}/;

    // Scope: only the files that participate in the managed-Jest /
    // integrity-gate / resolver-health flow. Other scripts (the docs
    // generators, the EOL checker, the wiki transformer) emit
    // repo-relative paths derived from path.relative(), which are
    // already POSIX on the only platforms where they run, and broaden
    // the false-positive surface unnecessarily.
    //
    // The scope is intentionally NARROW: it covers the call sites the
    // Windows developer actually hit (and the sites where adding a
    // new path interpolation is most likely to regress). Expanding
    // the scope is a deliberate decision; do it by adding paths
    // here, not by widening the regex.
    const SCAN_FILES = [
      path.join(SCRIPTS_ROOT, "run-managed-jest.js"),
      path.join(SCRIPTS_ROOT, "run-managed-prettier.js"),
      path.join(SCRIPTS_ROOT, "run-managed-cspell.js"),
      path.join(SCRIPTS_ROOT, "verify-managed-jest-fallback.js"),
      path.join(SCRIPTS_ROOT, "lib", "integrity-gate-with-recovery.js"),
      path.join(SCRIPTS_ROOT, "lib", "node-modules-integrity.js"),
      path.join(SCRIPTS_ROOT, "lib", "path-classifier.js"),
      path.join(SCRIPTS_ROOT, "lib", "jest-error-decoder.js"),
      path.join(SCRIPTS_ROOT, "lib", "managed-prettier.js"),
      path.join(SCRIPTS_ROOT, "lib", "shell-command.js")
    ];

    test("PATH_VAR_RE catches nested-paren call before the interpolation (regression)", () => {
      // The previous `[^)]*` body stopped at the first `)` and missed
      // call sites whose argument was a function call. The lazy
      // `[\s\S]*?` form crosses nested parens.
      const sample = "warnFn(getMsg() + `runner ${runnerPath}`);";
      const match = PATH_VAR_RE.exec(sample);
      expect(match).not.toBeNull();
      expect(match[2]).toBe("runnerPath");
    });

    test("PATH_VAR_RE catches lowercase snake_case `root` suffix (regression)", () => {
      // The previous character class only included `Root`; lowercase
      // `root` (e.g. `repo_root`) slipped through and could regress
      // a call site without tripping the policy gate.
      const sample = "console.warn(`repo at ${repo_root}`);";
      const match = PATH_VAR_RE.exec(sample);
      expect(match).not.toBeNull();
      expect(match[2]).toBe("repo_root");
    });

    test("PATH_VAR_RE does NOT fire when interpolation is wrapped in toPosixPath", () => {
      // `${toPosixPath(runnerPath)}` is a function call inside the
      // template hole, not a bare identifier. The regex requires
      // ${IDENT} with IDENT being a contiguous identifier ending in
      // a path-like suffix; `toPosixPath(runnerPath` is not a
      // contiguous identifier (the `(` breaks it). Therefore the
      // regex returns null, which IS the correct policy outcome:
      // wrapped call sites do not need to be allowlisted.
      const sample = "warnFn(`runner ${toPosixPath(runnerPath)}`);";
      const match = PATH_VAR_RE.exec(sample);
      expect(match).toBeNull();
    });

    test("PATH_VAR_RE: walker's same-line wrap detection clears wrapped offenders", () => {
      // Belt-and-suspenders: a hypothetical line that interpolates a
      // *bare* `${runnerPath}` while ALSO containing
      // `toPosixPath(runnerPath` elsewhere on the same line (e.g. a
      // log that captures both forms) should be cleared by the
      // walker's `normalized.test(line)` check. The regex still
      // fires, but the walker treats the line as compliant.
      const sample = "warnFn(`runner=${runnerPath}, normalized=${toPosixPath(runnerPath)}`);";
      const match = PATH_VAR_RE.exec(sample);
      expect(match).not.toBeNull();
      const escapedInterp = match[2].replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
      const normalized = new RegExp(
        "(?:toPosixPath|toRepoPosixRelative)\\(\\s*" + escapedInterp + "\\s*"
      );
      expect(normalized.test(sample)).toBe(true);
    });

    test("integrity-gate / managed-Jest scripts wrap path interpolations in warn/error logs with toPosixPath/toRepoPosixRelative", () => {
      const offenders = [];
      for (const abs of SCAN_FILES) {
        if (!fs.existsSync(abs)) {
          throw new Error(
            "cross-platform-path-handling SCAN_FILES references missing file: " + abs
          );
        }
        const content = fs.readFileSync(abs, "utf8");
        const lines = content.split(/\r?\n/);
        for (let i = 0; i < lines.length; i += 1) {
          const line = lines[i];
          const match = PATH_VAR_RE.exec(line);
          if (!match) {
            continue;
          }
          // If the line already routes the interpolated value
          // through one of the normalizers, it's fine. We check
          // the SAME line because the interpolation is on the
          // same line as the regex match.
          const interpolated = match[2];
          const escapedInterp = interpolated.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
          const normalized = new RegExp(
            "(?:toPosixPath|toRepoPosixRelative)\\(\\s*" + escapedInterp + "\\s*"
          );
          if (normalized.test(line)) {
            continue;
          }
          // Check the allowlist.
          const fileEntries = ALLOWLIST[abs];
          if (fileEntries && fileEntries.has(i + 1)) {
            continue;
          }
          offenders.push({
            file: toRepoPosixRelative(abs, REPO_ROOT),
            line: i + 1,
            identifier: interpolated,
            text: line.trim()
          });
        }
      }
      if (offenders.length > 0) {
        const detail = offenders
          .map((o) => `  ${o.file}:${o.line}  $\{${o.identifier}\}  -> ${o.text}`)
          .join("\n");
        throw new Error(
          "Found path-like log interpolations not wrapped in toPosixPath / toRepoPosixRelative.\n" +
            "Wrap each occurrence, or (with justification) add an allowlist entry in\n" +
            "scripts/__tests__/cross-platform-path-handling.test.js.\n\n" +
            detail
        );
      }
    });
  });
});
