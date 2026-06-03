/**
 * @fileoverview Self-preventing-class guard for cross-toolchain path-identity
 * comparisons in Jest tests.
 *
 * THE BUG CLASS: a test spawns a Windows-divergent shell (bash / sh / pwsh /
 * powershell / cmd) and then asserts that the path it printed is `.toBe(...)` a
 * path derived from Node's `path` API (`REPO_ROOT`, `path.resolve(...)`,
 * `path.join(...)`, `__dirname`, `process.cwd()`). On Linux and macOS the two
 * spellings are byte-identical (both POSIX), so the assertion is GREEN. On
 * Windows the shell git invokes (Git-Bash / MSYS / Cygwin / WSL) prints a POSIX
 * mount form (`/d/Code/...`, `/cygdrive/d/...`, `/mnt/d/...`) while Node prints
 * the native form (`D:\Code\...`) -- the SAME directory, NOT string-equal -- so
 * the assertion is RED. The divergence is invisible on the Linux/macOS dev box
 * and CI lanes and only surfaces on a Windows contributor's machine, typically
 * at the pre-push hook (the last resort). `devcontainer-cache-contract.test.js`
 * hit exactly this and reached a contributor's pre-push.
 *
 * THE FIX (per offending site): route BOTH sides of the comparison through ONE
 * toolchain so the spelling cannot diverge -- e.g. resolve the Node-side
 * reference through the same `bash ... pwd` that produced the shell-side value
 * (`bashResolveDir(REPO_ROOT)`), or normalize both with `toPosixPath` /
 * `toRepoPosixRelative`. See
 * .llm/skills/scripting/cross-toolchain-path-comparison.md.
 *
 * THE GUARD (this file): walk every `scripts/**\/__tests__/*.test.js`; a file
 * is an OFFENDER when it BOTH
 *   (a) spawns a Windows-divergent shell, AND
 *   (b) has a path-identity assertion (`.toBe` / `.toEqual` / `.toStrictEqual`)
 *       whose argument names a bare Node-path token NOT wrapped in a recognized
 *       normalizer,
 * UNLESS it carries the `@cross-platform-regression` marker IN A REAL COMMENT
 * SPAN (so the cross-OS CI gate actually executes it on windows-latest, turning
 * the silent drift into a fast, attributed CI failure) or is on the small
 * justified allow-list. The marker is recognized ONLY inside a comment (via the
 * same `extractCommentsOnly` projection the sibling coverage guard uses), so a
 * stray occurrence in a STRING literal can neither silence this guard nor sneak
 * past the coverage guard -- the two detectors agree on what "has the marker"
 * means.
 *
 * WHY THE REMEDY IS SAFE (non-fragile by construction): the cure for a flagged
 * file is cheap and beneficial, and is NEVER an incorrect rewrite. There are two
 * remedies, with DIFFERENT costs:
 *   - NORMALIZE the compare (best; truly zero-cost): route both sides through one
 *     toolchain (`bashResolveDir`) or normalize with `toPosixPath` /
 *     `toRepoPosixRelative`. The bare token is gone, so THIS guard clears and
 *     nothing else changes. This is the only remedy that is harmless in
 *     isolation.
 *   - MARK the file (`@cross-platform-regression` in a real COMMENT): this is a
 *     COORDINATED TWO-FILE edit, NOT a free promotion. The comment marker alone
 *     clears THIS guard but turns
 *     scripts/__tests__/cross-platform-preflight-coverage.test.js RED
 *     ("Marked cross-platform regression test(s) are not gated on win+mac") in
 *     the SAME run, because that coverage guard requires every marked file to ALSO
 *     be wired into the `--runTestsByPath` list of the targeted step in
 *     .github/workflows/cross-platform-preflight.yml. To use the marker remedy you
 *     MUST do both edits together (marker comment + workflow list entry).
 * Either way the worst case is "this test also gains win+mac CI execution", never
 * a wrong rewrite. The trigger is high-signal: across the whole tree it matches
 * ONLY genuine spawn+compare sites (measured: a single file at introduction).
 *
 * Pure Node stdlib, CRLF/BOM-safe. Every scan is LINEAR in input length: the
 * path-identity detector is split into two regexes that each have a single,
 * non-backtracking shape -- an equality-matcher finder
 * (`.toBe(`/`.toEqual(`/`.toStrictEqual(`) and a separate bare-path-token finder
 * -- so neither has a lazy quantifier feeding an alternation (the classic
 * quadratic shape). The matcher's argument span is bounded by a plain
 * `String.prototype.indexOf` (linear), not by a `[^)]*?` regex body. THIS guard
 * file is allow-listed against its own detector self-tests.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { extractCommentsOnly } = require("../lib/source-stripping");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SCRIPTS_ROOT = path.join(REPO_ROOT, "scripts");

const MARKER = "@cross-platform-regression";

const WALK_SKIP_DIRS = new Set(["node_modules", ".git", ".venv", "__pycache__", "Temp"]);

// Files intentionally exempt from "must spawn -> must mark", keyed by
// repo-relative POSIX path. THIS guard embeds the trigger tokens in its own
// detector self-tests as DATA, so it must not count as a real offender.
const ALLOW_LIST = new Map([
  [
    "scripts/__tests__/cross-toolchain-path-comparison-policy.test.js",
    "this guard's own self-test fixtures embed the trigger tokens as data"
  ]
]);

// Spawn of a shell whose path spelling diverges on Windows. We match the
// common Node spawn/exec families with the program as the FIRST string
// argument. `cmd`/`sh` are included for completeness even though bash/pwsh are
// the realistic cases. Linear, anchored on the call name; ReDoS-free.
const DIVERGENT_SHELL_SPAWN_RE =
  /\b(?:spawnSync|spawn|execSync|execFileSync|execFile|exec)\s*\(\s*["'`](?:bash|sh|pwsh|powershell(?:\.exe)?|cmd(?:\.exe)?)["'`]/;

// Equality matcher opener. Scoped to equality matchers (the failure mode is
// exact-string equality); we deliberately do NOT flag `toContain`/`toMatch`
// (substring/regex compares do not exhibit the whole-string drift). Global so we
// can walk every opener on a line. This regex has NO lazy quantifier and NO
// alternation-after-filler, so `.exec` is linear in line length.
const EQUALITY_MATCHER_RE = /\.(?:toBe|toEqual|toStrictEqual)\(/g;

// A bare Node-path token. Searched ONLY within a matcher's argument span (see
// `nodePathEqualityLines`), never against an unbounded `[^)]*?` filler. Each
// alternative is a fixed literal/word boundary with no quantifier feeding the
// alternation, so `.test` is linear in the span length.
const NODE_PATH_TOKEN_RE =
  /\bREPO_ROOT\b|\b__dirname\b|process\.cwd\(\)|\bpath\.(?:resolve|join|dirname|normalize)\(/;

// Recognized normalizers: when the SAME line routes the value through one of
// these, the spelling cannot diverge and the line is compliant.
const NORMALIZER_RE = /(?:toPosixPath|toRepoPosixRelative|bashResolveDir|resolveDirViaShell)\s*\(/;

/**
 * Does this source spawn a Windows-divergent shell anywhere?
 * @param {string} source
 * @returns {boolean}
 */
function spawnsDivergentShell(source) {
  return DIVERGENT_SHELL_SPAWN_RE.test(source);
}

/**
 * True when `line` contains an equality matcher (`.toBe(`/`.toEqual(`/
 * `.toStrictEqual(`) whose argument span carries a bare Node-path token. The
 * span is delimited by a plain `indexOf(")")` from each matcher opener (linear),
 * and the path-token search runs ONLY over that bounded span -- so there is no
 * lazy-quantifier-feeds-alternation shape and the whole check is linear in line
 * length even on pathological inputs. Equivalent in result to the old single
 * regex but without its quadratic backtracking.
 * @param {string} line
 * @returns {boolean}
 */
function lineHasBarePathEquality(line) {
  EQUALITY_MATCHER_RE.lastIndex = 0;
  let m;
  while ((m = EQUALITY_MATCHER_RE.exec(line)) !== null) {
    // Argument span starts just after the matcher's `(`. Bound it at the first
    // `)` on the line; if none, scan to end of line. `process.cwd()` is matched
    // by the token RE itself, so a span that opens with it still hits.
    const argStart = m.index + m[0].length;
    const closeRel = line.indexOf(")", argStart);
    const argEnd = closeRel === -1 ? line.length : closeRel + 1;
    const span = line.slice(argStart, argEnd);
    if (NODE_PATH_TOKEN_RE.test(span)) {
      return true;
    }
    // Guard against a zero-width match looping forever (defensive; the matcher
    // always consumes at least `.toBe(`).
    if (EQUALITY_MATCHER_RE.lastIndex <= m.index) {
      EQUALITY_MATCHER_RE.lastIndex = m.index + 1;
    }
  }
  return false;
}

/**
 * Return the 1-indexed line numbers carrying a bare Node-path equality
 * assertion that is NOT normalized on the same line. Empty when none.
 * @param {string} source
 * @returns {number[]}
 */
function nodePathEqualityLines(source) {
  const lines = source.split(/\r?\n/);
  const hits = [];
  for (let i = 0; i < lines.length; i += 1) {
    const line = lines[i];
    if (!lineHasBarePathEquality(line)) {
      continue;
    }
    if (NORMALIZER_RE.test(line)) {
      // Normalized on the same line -> spelling cannot diverge -> compliant.
      continue;
    }
    hits.push(i + 1);
  }
  return hits;
}

/**
 * Does this source carry the cross-OS marker INSIDE A REAL COMMENT SPAN? We
 * deliberately match the SAME comment-span semantics as the sibling coverage
 * guard (cross-platform-preflight-coverage.test.js `sourceHasMarkerInComment`)
 * by projecting the source through `extractCommentsOnly` (comment payloads
 * survive verbatim; code + string/template payloads are blanked to spaces). A
 * substring check is NOT sufficient: a marker buried in a STRING literal
 * (`const label = "@cross-platform-regression";`) would silence THIS guard
 * while the coverage guard ignores strings and therefore demands no
 * `--runTestsByPath` wiring -- a free silencing with no coordinated edit and no
 * CI backstop. Sharing the comment-span discriminator closes that loophole: a
 * string-literal marker no longer clears this guard, and the only clearing
 * remedy is a genuine comment marker (which DOES turn the coverage guard RED
 * until the workflow list is updated -- the coordinated two-file edit) or a
 * normalized compare.
 * @param {string} source
 * @returns {boolean}
 */
function hasMarker(source) {
  if (source.indexOf(MARKER) === -1) {
    return false;
  }
  return extractCommentsOnly(source).indexOf(MARKER) !== -1;
}

function toRepoPosixRelative(absolutePath) {
  return path.relative(REPO_ROOT, absolutePath).split(path.sep).join("/");
}

function listTestFiles() {
  const out = [];
  const stack = [SCRIPTS_ROOT];
  while (stack.length > 0) {
    const dir = stack.pop();
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      continue;
    }
    for (const entry of entries) {
      const abs = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        if (!WALK_SKIP_DIRS.has(entry.name)) {
          stack.push(abs);
        }
        continue;
      }
      if (
        entry.isFile() &&
        abs.endsWith(".test.js") &&
        path.relative(SCRIPTS_ROOT, abs).split(path.sep).includes("__tests__")
      ) {
        out.push(abs);
      }
    }
  }
  return out;
}

const maybeDescribe = typeof describe === "function" ? describe : () => {};

maybeDescribe("cross-toolchain path-comparison policy", () => {
  test("shell-spawning tests with bare Node-path equality asserts carry the cross-OS marker", () => {
    const offenders = [];
    for (const abs of listTestFiles()) {
      const rel = toRepoPosixRelative(abs);
      if (ALLOW_LIST.has(rel)) {
        continue;
      }
      const source = fs.readFileSync(abs, "utf8");
      if (!spawnsDivergentShell(source)) {
        continue;
      }
      const lines = nodePathEqualityLines(source);
      if (lines.length === 0) {
        continue;
      }
      if (hasMarker(source)) {
        continue;
      }
      offenders.push({ rel, lines });
    }

    if (offenders.length > 0) {
      const detail = offenders.map((o) => `  ${o.rel}  (lines ${o.lines.join(", ")})`).join("\n");
      throw new Error(
        "Cross-toolchain path-identity drift risk: the following test files spawn a\n" +
          "Windows-divergent shell (bash/sh/pwsh/powershell/cmd) AND assert a bare\n" +
          "Node-path token (REPO_ROOT / path.resolve / path.join / __dirname /\n" +
          "process.cwd()) with `.toBe`/`.toEqual`/`.toStrictEqual`. On Windows the\n" +
          "shell prints `/d/...` while Node prints `D:\\...` for the SAME directory,\n" +
          "so the compare passes on Linux/macOS and FAILS on Windows.\n\n" +
          "Fix EITHER by routing both sides through one toolchain (e.g.\n" +
          "`bashResolveDir(REPO_ROOT)`) or normalizing with toPosixPath /\n" +
          "toRepoPosixRelative (preferred -- the flag then clears), OR by adding the\n" +
          `${MARKER} marker comment + wiring the file into the targeted step of\n` +
          ".github/workflows/cross-platform-preflight.yml so it executes on\n" +
          "windows-latest. See .llm/skills/scripting/cross-toolchain-path-comparison.md.\n\n" +
          detail
      );
    }
  });

  // ---------------------------------------------------------------------------
  // Detector self-tests: prove the pure detectors fire on the exact shapes and
  // do NOT false-positive, WITHOUT writing real repo files.
  // ---------------------------------------------------------------------------
  describe("detector self-tests", () => {
    test("spawnsDivergentShell fires on bash / pwsh / sh / cmd spawns", () => {
      expect(spawnsDivergentShell('childProcess.spawnSync("bash", ["-c", x]);')).toBe(true);
      expect(spawnsDivergentShell('spawnSync("pwsh", [scriptPath]);')).toBe(true);
      expect(spawnsDivergentShell("execFileSync('sh', ['-c', x]);")).toBe(true);
      expect(spawnsDivergentShell('spawn("powershell.exe", a);')).toBe(true);
      expect(spawnsDivergentShell('execSync("cmd /c dir");')).toBe(false); // not a string-arg program
      expect(spawnsDivergentShell('spawnSync("cmd", ["/c", "dir"]);')).toBe(true);
    });

    test("spawnsDivergentShell does NOT fire on node/other spawns", () => {
      expect(spawnsDivergentShell('spawnSync("node", ["x.js"]);')).toBe(false);
      expect(spawnsDivergentShell('spawnSync("git", ["status"]);')).toBe(false);
      expect(spawnsDivergentShell("// the word bash in a comment")).toBe(false);
    });

    test("nodePathEqualityLines flags bare REPO_ROOT / path.resolve / __dirname asserts", () => {
      expect(nodePathEqualityLines("expect(out).toBe(REPO_ROOT);").length).toBe(1);
      expect(nodePathEqualityLines("expect(x).toEqual(path.resolve(a, b));").length).toBe(1);
      expect(
        nodePathEqualityLines("expect(`${r}/node_modules`).toBe(`${REPO_ROOT}/node_modules`);")
          .length
      ).toBe(1);
      expect(nodePathEqualityLines("expect(p).toStrictEqual(__dirname);").length).toBe(1);
    });

    test("nodePathEqualityLines does NOT flag normalized compares", () => {
      expect(nodePathEqualityLines("expect(out).toBe(bashResolveDir(REPO_ROOT));").length).toBe(0);
      expect(
        nodePathEqualityLines("expect(toPosixPath(p)).toBe(toRepoPosixRelative(path.join(a)));")
          .length
      ).toBe(0);
    });

    test("nodePathEqualityLines does NOT flag substring/regex compares or non-path equality", () => {
      expect(nodePathEqualityLines("expect(out).toContain(REPO_ROOT);").length).toBe(0);
      expect(nodePathEqualityLines("expect(out).toMatch(/REPO_ROOT/);").length).toBe(0);
      expect(nodePathEqualityLines("expect(count).toBe(3);").length).toBe(0);
      expect(nodePathEqualityLines('expect(name).toBe("repo");').length).toBe(0);
    });

    test("nodePathEqualityLines flags process.cwd() as the immediate argument", () => {
      // `process.cwd()` ends in `)`, so the argument-span bound must include the
      // matched `)` (argEnd = closeRel + 1) for the token RE to hit it.
      expect(nodePathEqualityLines("expect(p).toBe(process.cwd());").length).toBe(1);
    });

    test("nodePathEqualityLines is LINEAR (not quadratic) on a pathological long line", () => {
      // The old single regex used `.toBe(\s*[^)]*?(<alternation>)` -- a lazy
      // quantifier feeding an alternation -- which is QUADRATIC in line length
      // when the alternation fails (measured ~720ms for 80k trailing spaces).
      // The split predicate bounds the span with indexOf and runs each regex once,
      // so a 200k-space line resolves in well under the wall-clock budget below.
      const pathological = ".toBe(" + " ".repeat(200000) + ";";
      const start = Date.now();
      expect(nodePathEqualityLines(pathological).length).toBe(0);
      expect(Date.now() - start).toBeLessThan(150);
    });

    test("end-to-end: a synthetic offender (spawn + bare compare, no marker) is caught", () => {
      const src = ['spawnSync("bash", ["-c", cmd]);', "expect(out).toBe(REPO_ROOT);"].join("\n");
      const offends =
        spawnsDivergentShell(src) && nodePathEqualityLines(src).length > 0 && !hasMarker(src);
      expect(offends).toBe(true);
    });

    test("end-to-end: the same file becomes compliant once normalized", () => {
      const src = [
        'spawnSync("bash", ["-c", cmd]);',
        "expect(out).toBe(bashResolveDir(REPO_ROOT));"
      ].join("\n");
      const offends =
        spawnsDivergentShell(src) && nodePathEqualityLines(src).length > 0 && !hasMarker(src);
      expect(offends).toBe(false);
    });

    test("end-to-end: the same file becomes compliant once marked (comment)", () => {
      const src = [
        `// ${MARKER}`,
        'spawnSync("bash", ["-c", cmd]);',
        "expect(out).toBe(REPO_ROOT);"
      ].join("\n");
      const offends =
        spawnsDivergentShell(src) && nodePathEqualityLines(src).length > 0 && !hasMarker(src);
      expect(offends).toBe(false);
    });

    test("hasMarker fires ONLY inside a real comment span, not a string literal", () => {
      // Comment forms clear the guard (they coordinate with the coverage guard,
      // which then demands win+mac wiring).
      expect(hasMarker(`// ${MARKER}\n`)).toBe(true);
      expect(hasMarker(`/**\n * ${MARKER}\n */\n`)).toBe(true);
      expect(hasMarker(`/*\n   ${MARKER}\n*/\n`)).toBe(true);
      // String/template/code occurrences do NOT, matching the coverage guard's
      // comment-span semantics so the detectors cannot disagree.
      expect(hasMarker(`const label = "${MARKER}";\n`)).toBe(false);
      expect(hasMarker(`const label = '${MARKER}';\n`)).toBe(false);
      expect(hasMarker(`const label = \`${MARKER}\`;\n`)).toBe(false);
      expect(hasMarker(`const u = "a // b ${MARKER}";\n`)).toBe(false);
    });

    test("end-to-end: a string-literal marker does NOT clear the guard -- the offender remains caught", () => {
      // The exact loophole from the review: an offender that buries the token in
      // a STRING literal must STILL offend (was silenced by the old substring
      // hasMarker, while the coverage guard ignores strings and demanded no
      // wiring -- a free silencing with no backstop).
      const src = [
        `const label = "${MARKER}";`,
        'spawnSync("bash", ["-c", cmd]);',
        "expect(out).toBe(REPO_ROOT);"
      ].join("\n");
      const offends =
        spawnsDivergentShell(src) && nodePathEqualityLines(src).length > 0 && !hasMarker(src);
      expect(offends).toBe(true);
    });

    test("a file that only spawns (no path compare) is NOT an offender", () => {
      const src = 'spawnSync("bash", ["-c", "echo hi"]);\nexpect(out).toBe("hi");';
      const offends = spawnsDivergentShell(src) && nodePathEqualityLines(src).length > 0;
      expect(offends).toBe(false);
    });

    test("a file that compares paths but spawns NO shell is NOT an offender", () => {
      const src = "expect(p).toBe(path.resolve(REPO_ROOT, 'x'));";
      const offends = spawnsDivergentShell(src) && nodePathEqualityLines(src).length > 0;
      expect(offends).toBe(false);
    });
  });
});

if (typeof module !== "undefined" && module.exports) {
  module.exports = {
    MARKER,
    spawnsDivergentShell,
    nodePathEqualityLines,
    hasMarker
  };
}
