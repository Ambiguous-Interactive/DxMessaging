/**
 * @fileoverview Cross-platform spawn-policy + Node-direct-fallback contract for
 * the change-aware preflight engine and its hooks (design 7.4).
 *
 *   1. SOURCE-SCAN spawn policy: scripts/preflight.js, scripts/lib/changed-files.js,
 *      scripts/hooks/preflight-before-push-guard.js, and
 *      scripts/hooks/preflight-on-stop.js must route EVERY child spawn through
 *      spawnPlatformCommandSync (or buildSpawnInvocation). No raw
 *      spawnSync("git"/"node"/"npm", ...), no `shell: true`, no bash/sh. The scan
 *      runs on CODE only (comments/strings masked via maskCommentsAndStrings) so
 *      docstrings naming the forbidden shapes do not self-trip. The single
 *      sanctioned `childProcess.spawnSync` reference is the INJECTED default impl
 *      handed to spawnPlatformCommandSync, which is allow-listed by shape.
 *
 *   2. PLATFORM NORMALIZATION: git invocations build identically on
 *      win32 / linux / darwin (git is not a shim -> passthrough), proven via
 *      buildSpawnInvocation; and path normalization (toPosixPath) yields POSIX
 *      separators on all three.
 *
 *   3. NODE-DIRECT FALLBACK: with ensurePreCommit stubbed `{ok:false}`, the
 *      orchestrator enters node-direct mode, routes to npm/node `check:*` /
 *      `validate:*` entrypoints (NEVER parsing hook `entry` strings), emits the
 *      LOUD yamllint WARNING when YAML changed, and runs the policy/security
 *      hooks regardless (they never fail open). Every spawn is `node`.
 *
 * NOTE on the fast-attribution marker: this file deliberately does NOT carry
 * the cross-OS regression opt-in marker comment (the token the coverage guard
 * scans for in COMMENT spans). The curated fast-attribution subset is
 * intentionally small; the full pre-push suite already runs this file on
 * ubuntu/windows/macos, so it gets cross-OS coverage without being promoted
 * into the targeted gate. Carrying the marker would (correctly) require wiring
 * this path into .github/workflows/cross-platform-preflight.yml, which design
 * 7.4 explicitly avoids. See
 * scripts/__tests__/cross-platform-preflight-coverage.test.js for the rationale.
 *
 * Node stdlib + existing libs only; no shell-outs.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const { maskCommentsAndStrings } = require("../lib/source-stripping");
const { normalizeToLf } = require("../lib/quote-parser");
const { buildSpawnInvocation } = require("../lib/shell-command");
const { toPosixPath } = require("../lib/path-classifier");

const preflight = require("../preflight");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

const SCANNED_FILES = [
  "scripts/preflight.js",
  "scripts/lib/changed-files.js",
  "scripts/hooks/preflight-before-push-guard.js",
  "scripts/hooks/preflight-on-stop.js"
];

function readCode(rel) {
  const raw = normalizeToLf(fs.readFileSync(path.join(REPO_ROOT, rel), "utf8"));
  return maskCommentsAndStrings(raw);
}

describe("preflight cross-platform spawn policy + node-direct fallback", () => {
  // ---- (1) source-scan spawn policy -----------------------------------

  test.each(SCANNED_FILES.map((f) => [f]))(
    "%s spawns only through spawnPlatformCommandSync (no raw spawn, no shell)",
    (rel) => {
      const code = readCode(rel);

      // No `shell: true` anywhere.
      expect(code).not.toMatch(/shell\s*:\s*true/);
      // No bash/sh/pwsh/powershell program tokens.
      expect(code).not.toMatch(/\b(?:bash|sh|pwsh|powershell)\b/);

      // No DIRECT spawn-family CALL on a child-process program. The only
      // sanctioned `spawnSync` mention is the injected default impl passed as an
      // ARGUMENT to spawnPlatformCommandSync (e.g.
      // `spawnPlatformCommandSync(cmd, args, opts, childProcess.spawnSync)`),
      // which is a reference, not a call (no parens follow the name).
      //
      // The unambiguous child_process arg-vector/shell families never collide
      // with stdlib method names, so a `name(` or `obj.name(` call of any of
      // them is a violation: `spawnSync(`, `spawn(`, `execSync(`,
      // `execFileSync(`, `execFile(`. We DELIBERATELY exclude bare `exec` from
      // this list because `RegExp.prototype.exec` / `String#exec`-style member
      // calls (e.g. `re.exec(text)`) share the name; the genuine child_process
      // form `childProcess.exec(` is covered by its own explicit check below.
      const UNAMBIGUOUS_FAMILIES = ["spawnSync", "spawn", "execSync", "execFileSync", "execFile"];
      for (const family of UNAMBIGUOUS_FAMILIES) {
        // A call is `<name>(` OR `<receiver>.<name>(`. Forbid both; the injected
        // default impl is a bare reference (`childProcess.spawnSync` with no
        // following `(`), so it is not matched.
        const callPattern = new RegExp(`\\b${family}\\s*\\(`);
        if (callPattern.test(code)) {
          throw new Error(
            `${rel} contains a direct child_process call \`${family}(\`. ` +
              "All spawns must route through spawnPlatformCommandSync " +
              "(scripts/lib/shell-command.js); the only allowed reference is the " +
              "injected default impl passed as an argument (no call parens)."
          );
        }
      }
      // The bare-`exec` child_process form: only flag a clear child_process
      // receiver (`childProcess.exec(`, `cp.exec(`, `require("child_process").exec(`),
      // not a RegExp/String `.exec(`.
      expect(code).not.toMatch(/\b(?:childProcess|child_process|cp)\s*\.\s*exec\s*\(/);
      expect(code).not.toMatch(/require\(["']child_process["']\)\s*\.\s*exec\s*\(/);
    }
  );

  test.each(SCANNED_FILES.map((f) => [f]))(
    "%s requires spawnPlatformCommandSync from the shared shell-command lib",
    (rel) => {
      const raw = normalizeToLf(fs.readFileSync(path.join(REPO_ROOT, rel), "utf8"));
      expect(raw).toMatch(/require\(["'][^"']*shell-command["']\)/);
      expect(raw).toContain("spawnPlatformCommandSync");
    }
  );

  // ---- (2) platform normalization -------------------------------------

  test.each([["win32"], ["linux"], ["darwin"]])(
    "git invocations build identically on %s (git is not a shim -> passthrough)",
    (platform) => {
      const inv = buildSpawnInvocation(
        "git",
        ["diff", "--name-status", "-z", "HEAD"],
        { cwd: REPO_ROOT },
        platform
      );
      expect(inv.command).toBe("git");
      expect(inv.args).toEqual(["diff", "--name-status", "-z", "HEAD"]);
    }
  );

  test.each([["win32"], ["linux"], ["darwin"]])(
    "node invocations build identically on %s (node is not a shim -> passthrough)",
    (platform) => {
      const inv = buildSpawnInvocation("node", ["scripts/preflight.js", "--json"], {}, platform);
      expect(inv.command).toBe("node");
      expect(inv.args).toEqual(["scripts/preflight.js", "--json"]);
    }
  );

  test("toPosixPath yields POSIX separators regardless of input separator", () => {
    expect(toPosixPath("scripts\\hooks\\preflight-on-stop.js")).toBe(
      "scripts/hooks/preflight-on-stop.js"
    );
    expect(toPosixPath("scripts/hooks/preflight-on-stop.js")).toBe(
      "scripts/hooks/preflight-on-stop.js"
    );
  });

  // ---- (3) node-direct fallback ---------------------------------------

  /**
   * Drive runPreflight with ensurePreCommit stubbed `{ok:false}` and a fixed
   * change-set. Records every runCommand invocation.
   */
  function runNodeDirect({ files, profile = "guard", all = false }) {
    const calls = [];
    const sources = {
      committed: [],
      staged: files,
      unstaged: [],
      untracked: []
    };
    const deps = {
      computeChangeSetFn: () => ({
        files,
        base: null,
        mergeBase: null,
        scope: "worktree",
        sources
      }),
      stagesInConfigFn: () => new Set(["pre-commit", "pre-push"]),
      repairNodeToolingFn: () => ({ status: 0 }),
      ensurePreCommitFn: () => ({ ok: false, reason: "missing-python" }),
      runCommandFn: (command, args) => {
        calls.push({ command, args });
        return { status: 0, stdout: "", stderr: "" };
      },
      logFn: () => {},
      env: {}
    };
    const { report, exitCode } = preflight.runPreflight(
      { profile, scope: "worktree", recover: true, json: true, all },
      deps
    );
    return { report, exitCode, calls };
  }

  test("ensurePreCommit {ok:false} -> node-direct mode, every spawn is `node`", () => {
    const { report, calls } = runNodeDirect({ files: ["a.cs", "docs/x.md"] });
    expect(report.mode).toBe("node-direct");
    expect(calls.length).toBeGreaterThan(0);
    expect(calls.every((c) => c.command === "node")).toBe(true);
    // No `entry`-string parsing: every routed command targets a scripts/*.js
    // entrypoint (the args[0] is a scripts/ path), never a raw hook entry.
    for (const c of calls) {
      expect(String(c.args[0])).toMatch(/^scripts\/.*\.js$/);
    }
  });

  test("node-direct emits the LOUD yamllint WARNING when YAML changed", () => {
    const { report } = runNodeDirect({ files: ["a.yml"] });
    expect(report.status.warnings.join("\n")).toMatch(/yamllint requires pre-commit\/Python/i);
  });

  test("node-direct does NOT warn about yamllint when no YAML changed", () => {
    const { report } = runNodeDirect({ files: ["a.cs"] });
    expect(report.status.warnings.join("\n")).not.toMatch(/yamllint/i);
  });

  test("node-direct ALWAYS runs the policy/security hooks (never fail open)", () => {
    // No .cs/.md/.yml -- only a file that gates nothing -- yet policy hooks
    // (which have no gate) must still be invoked.
    const { calls } = runNodeDirect({ files: ["unrelated.bin"] });
    const ran = (name) => calls.some((c) => c.args.some((a) => String(a).includes(name)));
    for (const policyScript of [
      "validate-untracked-policy.js",
      "validate-no-plan-vocabulary.js",
      "validate-vscode-settings.js",
      "validate-pre-commit-tooling.js"
    ]) {
      expect(ran(policyScript)).toBe(true);
    }
  });

  test("a policy hook failure in node-direct forces checks-failed (policyFailures populated)", () => {
    const calls = [];
    const deps = {
      computeChangeSetFn: () => ({
        files: ["unrelated.bin"],
        base: null,
        mergeBase: null,
        scope: "worktree",
        sources: { committed: [], staged: ["unrelated.bin"], unstaged: [], untracked: [] }
      }),
      stagesInConfigFn: () => new Set(["pre-commit", "pre-push"]),
      repairNodeToolingFn: () => ({ status: 0 }),
      ensurePreCommitFn: () => ({ ok: false, reason: "missing-python" }),
      runCommandFn: (command, args) => {
        calls.push({ command, args });
        // Fail only the untracked-policy validator.
        const failed = args.some((a) => String(a).includes("validate-untracked-policy.js"));
        return { status: failed ? 1 : 0, stdout: "", stderr: "" };
      },
      logFn: () => {},
      env: {}
    };
    const { report, exitCode } = preflight.runPreflight(
      { profile: "guard", scope: "worktree", recover: true, json: true, all: false },
      deps
    );
    expect(report.status.policyFailures).toContain("validate-untracked-policy");
    expect(report.status.kind).toBe("checks-failed");
    expect(exitCode).toBe(1);
  });

  test("guard profile defers the heavy Jest suites in node-direct mode", () => {
    // script-parser-tests / script-tests / unity-contract-tests must NOT be
    // routed under --profile=guard.
    const { calls } = runNodeDirect({ files: ["scripts/preflight.js"], profile: "guard" });
    const ranJestSuites = calls.some((c) => c.args.some((a) => /run-managed-jest/.test(String(a))));
    expect(ranJestSuites).toBe(false);
  });

  // ---- (4) node-direct must stay READ-ONLY / idempotent ---------------
  //
  // The push-guard and Stop hook spawn preflight; the guard's safety rationale
  // (and design 5.2) rest on preflight being read-only and idempotent. In
  // node-direct mode the change-set includes the committed range, so any
  // in-place FIXER would silently rewrite committed files and then allow the
  // push. Every node-direct command must therefore be a check/validate-only
  // entrypoint -- never a mutating fixer entry.

  /**
   * Fixer entrypoints that mutate files in place (write / --write / --fix) and
   * MUST NOT appear in the node-direct routing without a read-only flag. The
   * markdown pipeline (run-staged-md-pipeline.js) is the canonical offender:
   * it has no --check mode, so node-direct routes to the read-only validators
   * it subsumes instead.
   */
  const MUTATING_ENTRYPOINTS = [
    "run-staged-md-pipeline.js",
    "run-and-restage.js",
    "normalize-docs-ascii.js",
    "fix-md036-headings.js",
    "fix-md029-md051.js"
  ];

  test("no node-direct command routes to an in-place markdown fixer", () => {
    // Drive with a markdown change so the markdown routing is exercised.
    const { calls } = runNodeDirect({ files: ["docs/x.md"], profile: "full" });
    for (const call of calls) {
      for (const arg of call.args) {
        for (const fixer of MUTATING_ENTRYPOINTS) {
          expect(String(arg)).not.toContain(fixer);
        }
      }
    }
  });

  test("a markdown change routes to the read-only doc validators (ASCII / code-patterns / prose)", () => {
    const { calls } = runNodeDirect({ files: ["docs/x.md"], profile: "full" });
    const ran = (name) => calls.some((c) => c.args.some((a) => String(a).includes(name)));
    expect(ran("validate-docs-ascii.js")).toBe(true);
    expect(ran("validate-doc-code-patterns.js")).toBe(true);
    expect(ran("validate-docs-prose.js")).toBe(true);
  });

  test("any prettier --check invocation is read-only (carries --check, never --write/--fix)", () => {
    const { calls } = runNodeDirect({ files: ["docs/x.md", "package.json"], profile: "full" });
    const prettierCalls = calls.filter((c) =>
      c.args.some((a) => /run-managed-prettier\.js/.test(String(a)))
    );
    expect(prettierCalls.length).toBeGreaterThan(0);
    for (const call of prettierCalls) {
      expect(call.args).toContain("--check");
      expect(call.args).not.toContain("--write");
      expect(call.args).not.toContain("--fix");
    }
  });

  test("a gated passFiles command receives ONLY the files matching its gate (no .cs to prettier)", () => {
    // A mixed change-set: prettier --check must never be handed the .cs file
    // (it errors "No parser could be inferred"); the doc validators must never
    // be handed package.json as markdown.
    const { calls } = runNodeDirect({
      files: ["docs/x.md", "Runtime/Core/MessageHandler.cs", "package.json"],
      profile: "full"
    });
    const filesFor = (script) =>
      calls
        .filter((c) => c.args.some((a) => String(a).includes(script)))
        .flatMap((c) => c.args.filter((a) => !String(a).startsWith("scripts/") && !String(a).startsWith("--")));

    // Markdown prettier: only the .md, never the .cs / .json.
    const mdPrettier = calls.filter(
      (c) =>
        c.args.some((a) => /run-managed-prettier\.js/.test(String(a))) &&
        c.args.some((a) => String(a).endsWith(".md"))
    );
    for (const call of mdPrettier) {
      expect(call.args).not.toContain("Runtime/Core/MessageHandler.cs");
    }

    // The doc validators only receive the markdown file.
    expect(filesFor("validate-docs-prose.js")).toEqual(["docs/x.md"]);

    // No prettier invocation anywhere carries the .cs path.
    const anyPrettierWithCs = calls.some(
      (c) =>
        c.args.some((a) => /run-managed-prettier\.js/.test(String(a))) &&
        c.args.some((a) => String(a) === "Runtime/Core/MessageHandler.cs")
    );
    expect(anyPrettierWithCs).toBe(false);
  });
});
