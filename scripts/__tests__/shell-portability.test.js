/**
 * @fileoverview Class-guard test enforcing bash-3.2 portability across every
 * tracked `*.sh` file in the repository.
 *
 * Root cause this guards against: macOS GitHub runners default to /bin/bash
 * 3.2. Scripts that use bash 4+ builtins (`mapfile`/`readarray`), associative
 * arrays, case-conversion `${VAR^^}`/`${VAR,,}` expansions, `shopt -s
 * globstar`, or the `|&` stderr-pipe shorthand abort with "command not found"
 * (or behave incorrectly) under bash 3.2. Separately, under `set -e` a
 * standalone post-increment arithmetic command `((COUNTER++))` returns exit
 * status 1 when the counter is currently 0 (post-increment yields the old
 * value, and `((expr))` exits non-zero when the value is 0), aborting the
 * script. The portable form is the pre-increment `((++COUNTER))`.
 *
 * Each forbidden pattern is data-driven so the whole class of bug cannot
 * recur: any new violation in any tracked .sh file fails this test with a
 * precise `file:line: <reason>` message.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

/**
 * Enumerate tracked shell scripts via `git ls-files` so we respect .gitignore,
 * never descend into node_modules, and only consider files that actually ship.
 *
 * @returns {string[]} Repo-relative paths to tracked `*.sh` files.
 */
function listTrackedShellScripts() {
  const result = childProcess.spawnSync("git", ["ls-files", "*.sh"], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });
  if (result.status !== 0) {
    throw new Error(
      `git ls-files '*.sh' failed (status=${result.status}): ${result.stderr || ""}`
    );
  }
  return result.stdout
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

/**
 * Read a tracked file's lines, tolerating CRLF and a possible race where the
 * file was removed after `git ls-files` enumerated it.
 *
 * @param {string} relPath - Repo-relative path.
 * @returns {string[]|null} Lines (LF-normalized) or null if the file is gone.
 */
function readLinesOrNull(relPath) {
  const absPath = path.join(REPO_ROOT, relPath);
  let content;
  try {
    content = fs.readFileSync(absPath, "utf8");
  } catch (err) {
    if (err && err.code === "ENOENT") {
      return null; // raced away between ls-files and read
    }
    throw err;
  }
  // Normalize CRLF and a lone trailing CR so line scanning is consistent.
  return content.replace(/\r\n/g, "\n").replace(/\r/g, "\n").split("\n");
}

/**
 * Strip a trailing shell comment from a line BEFORE pattern scanning so that a
 * benign comment that merely *mentions* a forbidden construct (e.g.
 * `# we avoid mapfile here`) is not flagged as a real violation.
 *
 * We only treat `#` as a comment when it starts the line (after optional
 * leading whitespace) or is immediately preceded by whitespace. This protects
 * `$#`, `${#arr[@]}` (the `#` is preceded by `{`, not whitespace), and a `#!`
 * shebang (also not whitespace-preceded), which are all left intact.
 *
 * Trade-off: a `#` inside a quoted string that is preceded by a space (e.g.
 * `echo "a # b"`) is also stripped. That can only cause UNDER-reporting of a
 * construct that appears after such a `#` (safe) -- it never produces a false
 * alarm, so we accept it for simplicity.
 *
 * @param {string} line - A single raw line.
 * @returns {string} The line with any trailing comment removed.
 */
function stripShellComment(line) {
  return line.replace(/(^|\s)#.*$/, "$1");
}

// Detects whether a script enables errexit (`set -e`, `set -eu`, `set -euo
// pipefail`, `set -eo pipefail`, `set -o errexit`, etc.). Only when errexit is
// on does a standalone `((COUNTER++))` become script-aborting.
const SET_E_RE = /^\s*set\s+-(?:[a-z]*e[a-z]*)\b|^\s*set\s+-o\s+errexit\b/m;

// A STANDALONE post-increment/decrement arithmetic command on its own line,
// e.g. `((PASS++))` or `  ((count--))`. We deliberately exclude C-style
// `for ((i=0; i<n; i++))` loop headers (handled by the caller) because their
// post-increment is the loop step, not a standalone errexit-sensitive command.
// The trailing `(?:\s*#.*)?` allows an inline comment such as `((PASS++)) # bump`
// to still be detected; comments are normally stripped before scanning, but
// keeping the regex tolerant means it matches regardless of strip order.
const STANDALONE_POSTFIX_INCDEC_RE =
  /^\s*\(\(\s*[A-Za-z_][A-Za-z0-9_]*(?:\+\+|--)\s*\)\)\s*(?:#.*)?$/;

// A STANDALONE `let` arithmetic increment/decrement, e.g. `let "i++"`,
// `let i++`, or `let i+=1`. Under `set -e` such a command aborts the script
// whenever its arithmetic result is 0 (same root cause as `((i++))`). The
// portable fix is `i=$((i+1))` (or guard with `|| true`). We only need to spot
// the leading `let <name><op>` form; C-style `for ((...))` loops never start
// with `let`, so no extra exclusion is required.
const STANDALONE_LET_INCDEC_RE =
  /^\s*let\s+["']?[A-Za-z_][A-Za-z0-9_]*(?:\+\+|--|\+=|-=)/;

/**
 * Forbidden-pattern table. Each row's `regex` is matched per-line; `name`/`why`
 * feed the failure message. The `set -e` post-increment check is special-cased
 * (it depends on file-level errexit state and a `for ((` exclusion) and lives
 * in scanFile rather than this table.
 */
const FORBIDDEN_PATTERNS = [
  {
    name: "mapfile builtin (bash 4+)",
    regex: /\bmapfile\b/,
    why: "mapfile is bash 4+; use a `while IFS= read -r line; do arr+=(\"$line\"); done` loop"
  },
  {
    name: "readarray builtin (bash 4+)",
    regex: /\breadarray\b/,
    why: "readarray is bash 4+; use a `while IFS= read -r line; do arr+=(\"$line\"); done` loop"
  },
  {
    name: "declare -A associative array (bash 4+)",
    // `-[A-Za-z]*A` catches combined option forms `declare -gA`/`declare -Ag`
    // too. Lowercase `declare -a` (indexed array, valid in bash 3.2) has no
    // uppercase `A` and is therefore NOT matched.
    regex: /declare\s+-[A-Za-z]*A\b/,
    why: "associative arrays are bash 4+; restructure with indexed arrays or a case statement"
  },
  {
    name: "local -A associative array (bash 4+)",
    // Same combined-flag handling as `declare -A`; lowercase `local -a` is safe
    // and unmatched.
    regex: /local\s+-[A-Za-z]*A\b/,
    why: "associative arrays are bash 4+; restructure with indexed arrays or a case statement"
  },
  {
    name: "uppercase case-conversion expansion ${VAR^^} (bash 4+)",
    regex: /\$\{[A-Za-z_][A-Za-z0-9_]*(?:\[[^\]]*\])?\^\^?[^}]*\}/,
    why: "${VAR^}/${VAR^^} case conversion is bash 4+; use `tr '[:lower:]' '[:upper:]'`"
  },
  {
    name: "lowercase case-conversion expansion ${VAR,,} (bash 4+)",
    regex: /\$\{[A-Za-z_][A-Za-z0-9_]*(?:\[[^\]]*\])?,,?[^}]*\}/,
    why: "${VAR,}/${VAR,,} case conversion is bash 4+; use `tr '[:upper:]' '[:lower:]'`"
  },
  {
    name: "shopt -s globstar (bash 4+)",
    regex: /shopt\s+-s\s+globstar/,
    why: "globstar (`**`) is bash 4+; enumerate matches with `find` instead"
  },
  {
    name: "|& pipe-stderr shorthand (bash 4+)",
    // Match `|&` but never the `||` logical-or operator.
    regex: /\|&(?!&)/,
    why: "`|&` (pipe stdout+stderr) is bash 4+; use `2>&1 |` instead"
  },
  {
    name: "${VAR@U}/${VAR@L} case-operator transformation (bash 4.4+)",
    // `@U`/`@L`/`@u` parameter transformations are bash 4.4+. The optional
    // `\[[^\]]*\]` allows an index/subscript. `${arr[@]}` and `${@}` are NOT
    // matched: their `@` is not followed by `@[ULu]}`.
    regex: /\$\{[A-Za-z_][A-Za-z0-9_]*(?:\[[^\]]*\])?@[ULu]\}/,
    why: "${VAR@U}/${VAR@L}/${VAR@u} case conversion is bash 4.4+; use `tr '[:lower:]' '[:upper:]'`"
  }
];

/**
 * Scan a single file's lines and collect all portability violations.
 *
 * @param {string} relPath - Repo-relative path (for messages).
 * @param {string[]} lines - LF-normalized lines.
 * @returns {string[]} `relPath:line: <reason>` violation strings.
 */
function scanFile(relPath, lines) {
  const violations = [];
  const fileHasSetE = SET_E_RE.test(lines.join("\n"));

  lines.forEach((rawLine, idx) => {
    const lineNo = idx + 1;
    // Strip trailing comments first so a benign mention of a forbidden
    // construct inside a comment is never flagged. (See stripShellComment.)
    const line = stripShellComment(rawLine);

    for (const pattern of FORBIDDEN_PATTERNS) {
      if (pattern.regex.test(line)) {
        violations.push(`${relPath}:${lineNo}: ${pattern.name} -- ${pattern.why}`);
      }
    }

    // set -e + standalone post-increment: only flag when errexit is enabled
    // for the file AND this line is NOT a C-style `for ((...))` loop header.
    if (
      fileHasSetE &&
      !line.includes("for ((") &&
      STANDALONE_POSTFIX_INCDEC_RE.test(line)
    ) {
      violations.push(
        `${relPath}:${lineNo}: standalone post-increment under \`set -e\` -- ` +
          "use the pre-increment form `((++NAME))` so the command yields the " +
          "new (non-zero) value and exits 0"
      );
    }

    // set -e + standalone `let` increment/decrement: same errexit hazard as
    // `((i++))` -- a 0-valued arithmetic result aborts the script.
    if (fileHasSetE && STANDALONE_LET_INCDEC_RE.test(line)) {
      violations.push(
        `${relPath}:${lineNo}: standalone \`let\` increment under \`set -e\` -- ` +
          "a 0-valued `let` result exits non-zero and aborts the script; use " +
          "`i=$((i+1))` (or guard with `|| true`)"
      );
    }
  });

  return violations;
}

describe("shell portability (bash 3.2) guard", () => {
  const shellScripts = listTrackedShellScripts();

  test("at least one tracked .sh file is discovered", () => {
    expect(shellScripts.length).toBeGreaterThan(0);
  });

  test.each(shellScripts)("%s is bash-3.2 portable", (relPath) => {
    const lines = readLinesOrNull(relPath);
    if (lines === null) {
      return; // file raced away after enumeration; nothing to assert
    }
    const violations = scanFile(relPath, lines);
    if (violations.length > 0) {
      throw new Error(
        `bash 3.2 portability violations:\n${violations.join("\n")}`
      );
    }
  });

  // Unit-level guards for the detector itself, so the `for ((` exclusion and
  // the errexit gating cannot silently regress.
  describe("detector self-checks", () => {
    test("flags standalone post-increment when set -e is present", () => {
      const lines = ["set -e", "((PASS++))"];
      const violations = scanFile("fake.sh", lines);
      expect(violations).toHaveLength(1);
      expect(violations[0]).toMatch(/fake\.sh:2: standalone post-increment/);
    });

    test("does NOT flag standalone post-increment without set -e", () => {
      const lines = ["#!/usr/bin/env bash", "((PASS++))"];
      const violations = scanFile("fake.sh", lines);
      expect(violations).toHaveLength(0);
    });

    test("does NOT false-positive on C-style for (( i++ )) loop headers", () => {
      const lines = ["set -euo pipefail", "for ((i=0; i<n; i++)); do echo $i; done"];
      const violations = scanFile("fake.sh", lines);
      expect(violations).toHaveLength(0);
    });

    test("accepts the portable pre-increment ((++PASS)) under set -e", () => {
      const lines = ["set -e", "((++PASS))"];
      const violations = scanFile("fake.sh", lines);
      expect(violations).toHaveLength(0);
    });

    test("flags mapfile and reports the correct line number", () => {
      const lines = ["#!/usr/bin/env bash", "", "mapfile -t arr <<< \"$x\""];
      const violations = scanFile("fake.sh", lines);
      expect(violations).toHaveLength(1);
      expect(violations[0]).toMatch(/fake\.sh:3: mapfile/);
    });

    test("flags ${VAR^^} / ${VAR,,} case-conversion expansions", () => {
      const upper = scanFile("fake.sh", ['echo "${name^^}"']);
      const lower = scanFile("fake.sh", ['echo "${name,,}"']);
      expect(upper).toHaveLength(1);
      expect(lower).toHaveLength(1);
      expect(upper[0]).toMatch(/uppercase case-conversion/);
      expect(lower[0]).toMatch(/lowercase case-conversion/);
    });

    test("flags |& but never the || logical-or operator", () => {
      expect(scanFile("fake.sh", ["foo |& bar"])).toHaveLength(1);
      expect(scanFile("fake.sh", ["foo || bar"])).toHaveLength(0);
    });

    test("flags declare -A / local -A associative arrays", () => {
      expect(scanFile("fake.sh", ["declare -A map"])).toHaveLength(1);
      expect(scanFile("fake.sh", ["  local -A map"])).toHaveLength(1);
    });

    test("flags shopt -s globstar", () => {
      expect(scanFile("fake.sh", ["shopt -s globstar"])).toHaveLength(1);
    });

    test("handles CRLF line endings", () => {
      const content = "set -e\r\n((PASS++))\r\n";
      const lines = content.replace(/\r\n/g, "\n").replace(/\r/g, "\n").split("\n");
      const violations = scanFile("fake.sh", lines);
      expect(violations).toHaveLength(1);
    });

    // CHANGE 1: comment stripping must not flag benign mentions, but must still
    // flag real code that precedes an inline comment.
    test("does NOT flag forbidden constructs that only appear in comments", () => {
      expect(scanFile("fake.sh", ["# uses mapfile fallback"])).toHaveLength(0);
      expect(scanFile("fake.sh", ['echo "x" # |& note'])).toHaveLength(0);
    });

    test("still flags real code preceding an inline comment", () => {
      const violations = scanFile("fake.sh", [
        'mapfile -t a <<<"$x"  # real code with comment'
      ]);
      expect(violations).toHaveLength(1);
      expect(violations[0]).toMatch(/mapfile/);
    });

    test("does NOT treat ${#arr[@]} length expansion as a comment or violation", () => {
      // `#` here is preceded by `{`, not whitespace, so it is not stripped, and
      // no forbidden pattern should match an array-length expansion.
      expect(scanFile("fake.sh", ['echo "${#arr[@]}"'])).toHaveLength(0);
    });

    // CHANGE 2: inline comment on the standalone post-increment, plus `let`.
    test("flags standalone post-increment with an inline comment under set -e", () => {
      const violations = scanFile("fake.sh", ["set -e", "((PASS++)) # bump"]);
      expect(violations).toHaveLength(1);
      expect(violations[0]).toMatch(/standalone post-increment/);
    });

    test("flags standalone `let` increment under set -e", () => {
      const violations = scanFile("fake.sh", ["set -e", 'let "i++"']);
      expect(violations).toHaveLength(1);
      expect(violations[0]).toMatch(/standalone `let` increment/);
    });

    test("does NOT flag `let` increment without set -e", () => {
      expect(scanFile("fake.sh", ["#!/bin/bash", 'let "i++"'])).toHaveLength(0);
    });

    // CHANGE 3: combined associative-array flags and `@U`/`@L` transforms.
    test("flags combined associative-array flags but not lowercase indexed -a", () => {
      expect(scanFile("fake.sh", ["declare -gA m"])).toHaveLength(1);
      expect(scanFile("fake.sh", ["local -A m"])).toHaveLength(1);
      expect(scanFile("fake.sh", ["declare -a arr"])).toHaveLength(0);
    });

    test("flags ${VAR@U} case-operator but not ${arr[@]}", () => {
      expect(scanFile("fake.sh", ['echo "${name@U}"'])).toHaveLength(1);
      expect(scanFile("fake.sh", ['echo "${arr[@]}"'])).toHaveLength(0);
    });
  });
});
