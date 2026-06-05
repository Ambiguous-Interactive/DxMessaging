#!/usr/bin/env node
/**
 * measure-hook-wallclock.js
 *
 * Wall-clock budget enforcer for git hooks. The static perf scorer
 * (scripts/lib/precommit-perf-score.js) catches structural regressions but
 * cannot measure real cost on a real machine. This script does that job:
 *
 *   1. Resolves a small set of representative scenarios (one .cs file, one
 *      generic .md file, one skill .md file, plus native pre-push stdin cases).
 *   2. For each scenario, touches the file's mtime (no content change), runs
 *      `node scripts/ensure-pre-commit.js run --hook-stage <stage> --files
 *      <file>`, or the native pre-push runner with synthetic Git stdin, and
 *      measures wall-clock.
 *   3. Reports per-scenario timings against per-scenario budgets and exits
 *      non-zero if any budget is exceeded.
 *
 * Stash protection: any unstaged changes to the touched file are stashed
 * before the run and restored after. The mtime touch is a no-op on the file
 * contents.
 *
 * This script is NOT a pre-commit hook. It is wired into the
 * .github/workflows/hook-perf-measurement.yml workflow to run on PRs that
 * touch hook configuration or scripts, and can be run locally for
 * debugging.
 *
 * CLI:
 *   node scripts/measure-hook-wallclock.js               # all scenarios
 *   node scripts/measure-hook-wallclock.js --json        # machine-readable output
 *   node scripts/measure-hook-wallclock.js --skip-touch  # do not touch files
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { execFileSync, spawnSync } = require("child_process");
const { writeHookValidationStamp } = require("./lib/hook-validation-stamp");

const REPO_ROOT = path.resolve(__dirname, "..");

// Per-scenario Linux budgets, in milliseconds. The hot native pre-push paths
// (no changed refs, or an agent-written stamp that covers the exact pushed
// range) are sub-second. The remaining multi-second budgets are fallback
// ceilings for unstamped validation paths that still run real format/lint/spell
// tools; they are intentionally measured so regressions stay visible while
// agentic preflight keeps them out of the common push path.
const SCENARIOS = [
  {
    id: "native-prepush-noop",
    kind: "native-prepush-noop",
    // A no-op push must stay a sub-second Git stdin parse/ref comparison path.
    // If this regresses, the native hook is doing bootstrap or validation work
    // before it has proven there is anything to validate.
    budgetMs: 1000
  },
  {
    id: "native-prepush-stamped-one-file",
    kind: "native-prepush-stamped-one-file",
    // Agentic preflight wrote a valid exact-range stamp; native pre-push should
    // only parse stdin, match the range, and skip repeated validators.
    budgetMs: 1000
  },
  {
    id: "native-prepush-one-file",
    kind: "native-prepush-one-file",
    // Unstamped fallback path: still validates the exact pushed range, but may
    // invoke all-skill freshness checks for .llm files and pay pre-commit stash
    // overhead in a dirty checkout. The stamped scenario above is the agentic
    // last-resort hot path that must stay sub-second.
    budgetMs: 18000
  },
  {
    id: "csharp-precommit",
    kind: "pre-commit-file",
    stage: "pre-commit",
    file: "Runtime/Core/MessageBus/MessageBus.cs",
    // Fallback framework path (no pre-commit stamp). Keep a realistic ceiling
    // so CI load does not make this measurement flaky; the stamped native
    // pre-commit wrapper is the sub-second hot path.
    budgetMs: 10000
  },
  {
    id: "skill-md-precommit",
    kind: "pre-commit-file",
    stage: "pre-commit",
    file: ".llm/skills/performance/git-hook-performance.md",
    // Fallback framework path that runs markdown/skill validators. This is not
    // the native hot path; keep enough headroom for CI load while still catching
    // broad regressions.
    budgetMs: 13000
  },
  {
    id: "csharp-prepush",
    kind: "pre-commit-file",
    stage: "pre-push",
    file: "Runtime/Core/MessageBus/MessageBus.cs",
    // Fallback framework path. The native push hot path is the stamped range
    // scenario; this ceiling keeps direct pre-commit-framework regressions
    // visible without making CI timing noise fail the measurement.
    budgetMs: 15000
  },
  {
    id: "skill-md-prepush",
    kind: "pre-commit-file",
    stage: "pre-push",
    file: ".llm/skills/performance/git-hook-performance.md",
    // Was 22.4 s before round-3, ~14 s post round-3, ~9 s post
    // round-4 (validators no longer fire here for .md because the
    // pipeline ran them at pre-commit). cspell (5.5 s) remains the
    // gate we accept.
    budgetMs: 13000
  }
];

// Re-exported for tests / consumers who want the native hot-path target.
const BUDGET_MS = 1000;

function parseArgs(argv) {
  const args = { json: false, skipTouch: false };
  for (const a of argv) {
    if (a === "--json") args.json = true;
    else if (a === "--skip-touch") args.skipTouch = true;
    else if (a === "--help" || a === "-h") {
      process.stdout.write(
        [
          "Usage: node scripts/measure-hook-wallclock.js [options]",
          "",
          "Options:",
          "  --json         Emit JSON instead of human-readable output.",
          "  --skip-touch   Do not touch file mtimes before measurement.",
          "  -h, --help     Show this message.",
          ""
        ].join("\n")
      );
      process.exit(0);
    }
  }
  return args;
}

function touchFile(absPath) {
  const now = new Date();
  fs.utimesSync(absPath, now, now);
}

function runPreCommit(stage, relPath) {
  const start = process.hrtime.bigint();
  const result = spawnSync(
    process.execPath,
    ["scripts/ensure-pre-commit.js", "run", "--hook-stage", stage, "--files", relPath],
    {
      cwd: REPO_ROOT,
      encoding: "utf8"
      // Inherit env; do NOT pass stdio: 'inherit' because we do not want
      // the noisy hook output flooding measurement output. We retain it
      // on failure for diagnostics.
    }
  );
  const elapsedNs = process.hrtime.bigint() - start;
  const elapsedMs = Number(elapsedNs / 1000000n);
  return {
    elapsedMs,
    status: result.status,
    signal: result.signal,
    stdout: result.stdout || "",
    stderr: result.stderr || "",
    error: result.error ? String(result.error.message) : null
  };
}

function runNativePrePush(stdin) {
  const start = process.hrtime.bigint();
  const result = spawnSync(process.execPath, ["scripts/run-native-prepush.js"], {
    cwd: REPO_ROOT,
    encoding: "utf8",
    input: stdin
  });
  const elapsedNs = process.hrtime.bigint() - start;
  const elapsedMs = Number(elapsedNs / 1000000n);
  return {
    elapsedMs,
    status: result.status,
    signal: result.signal,
    stdout: result.stdout || "",
    stderr: result.stderr || "",
    error: result.error ? String(result.error.message) : null
  };
}

function gitOutput(args) {
  try {
    return execFileSync("git", args, {
      cwd: REPO_ROOT,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"]
    }).trim();
  } catch {
    return "";
  }
}

function gitPath(relPath) {
  const resolved = gitOutput(["rev-parse", "--git-path", relPath]);
  if (!resolved) {
    return path.join(REPO_ROOT, ".git", relPath);
  }
  return path.isAbsolute(resolved) ? resolved : path.join(REPO_ROOT, resolved);
}

function withPreservedFile(filePath, action) {
  const existed = fs.existsSync(filePath);
  const before = existed ? fs.readFileSync(filePath) : null;
  try {
    return action();
  } finally {
    if (existed) {
      fs.mkdirSync(path.dirname(filePath), { recursive: true });
      fs.writeFileSync(filePath, before);
    } else {
      fs.rmSync(filePath, { force: true });
    }
  }
}

function findSingleFileCommitRange() {
  const commits = gitOutput(["rev-list", "--max-count=80", "--parents", "HEAD"])
    .split(/\r?\n/)
    .filter(Boolean);
  for (const line of commits) {
    const [commit, parent] = line.split(/\s+/);
    if (!commit || !parent) {
      continue;
    }
    const files = gitOutput([
      "diff-tree",
      "--no-commit-id",
      "--name-only",
      "-r",
      "--diff-filter=ACMR",
      commit
    ])
      .split(/\r?\n/)
      .filter(Boolean);
    if (files.length === 1) {
      return { from: parent, to: commit, file: files[0] };
    }
  }
  return null;
}

function checkPreCommitAvailable() {
  const probe = spawnSync(process.execPath, ["scripts/ensure-pre-commit.js"], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });
  if (probe.error || (probe.status !== 0 && probe.status !== null)) {
    return false;
  }
  return true;
}

function measureScenario(scenario, options) {
  if (scenario.kind === "native-prepush-noop") {
    const head = gitOutput(["rev-parse", "--verify", "HEAD"]);
    if (!head) {
      return {
        ...scenario,
        error: "could not resolve HEAD",
        elapsedMs: null,
        pass: false
      };
    }
    const run = runNativePrePush(`refs/heads/main ${head} refs/heads/main ${head}\n`);
    const pipelineOk = run.status === 0;
    const underBudget = run.elapsedMs <= scenario.budgetMs;
    return { ...scenario, ...run, pipelineOk, underBudget, pass: pipelineOk && underBudget };
  }

  if (scenario.kind === "native-prepush-one-file") {
    const range = findSingleFileCommitRange();
    if (!range) {
      return {
        ...scenario,
        skipped: true,
        error: "no single-file commit found in recent history",
        elapsedMs: null,
        pass: true
      };
    }
    const stampFile = gitPath("dxmsg-pre-push-stamp.json");
    const run = withPreservedFile(stampFile, () => {
      fs.rmSync(stampFile, { force: true });
      return runNativePrePush(`refs/heads/perf ${range.to} refs/heads/perf ${range.from}\n`);
    });
    const pipelineOk = run.status === 0;
    const underBudget = run.elapsedMs <= scenario.budgetMs;
    return {
      ...scenario,
      file: range.file,
      ...run,
      pipelineOk,
      underBudget,
      pass: pipelineOk && underBudget
    };
  }

  if (scenario.kind === "native-prepush-stamped-one-file") {
    const range = findSingleFileCommitRange();
    if (!range) {
      return {
        ...scenario,
        skipped: true,
        error: "no single-file commit found in recent history",
        elapsedMs: null,
        pass: true
      };
    }
    const stampFile = gitPath("dxmsg-pre-push-stamp.json");
    const run = withPreservedFile(stampFile, () => {
      writeHookValidationStamp(REPO_ROOT, "pre-push", {
        validatedFrom: range.from,
        validatedTo: range.to
      });
      return runNativePrePush(`refs/heads/perf ${range.to} refs/heads/perf ${range.from}\n`);
    });
    const pipelineOk = run.status === 0;
    const underBudget = run.elapsedMs <= scenario.budgetMs;
    return {
      ...scenario,
      file: range.file,
      ...run,
      pipelineOk,
      underBudget,
      pass: pipelineOk && underBudget
    };
  }

  const absFile = path.join(REPO_ROOT, scenario.file);
  if (!fs.existsSync(absFile)) {
    return {
      ...scenario,
      error: `target file does not exist: ${scenario.file}`,
      elapsedMs: null,
      pass: false
    };
  }
  if (!options.skipTouch) {
    try {
      touchFile(absFile);
    } catch (err) {
      return {
        ...scenario,
        error: `failed to touch file: ${err.message}`,
        elapsedMs: null,
        pass: false
      };
    }
  }
  const run = runPreCommit(scenario.stage, scenario.file);
  if (run.error) {
    return {
      ...scenario,
      error: run.error,
      elapsedMs: run.elapsedMs,
      pass: false,
      stdout: run.stdout,
      stderr: run.stderr
    };
  }
  // pre-commit returns non-zero if any hook fails. We treat that as a
  // measurement failure too -- a budget number is meaningless if the
  // pipeline rejected the touched file. The user's commit would have
  // been blocked.
  const pipelineOk = run.status === 0;
  const underBudget = run.elapsedMs <= scenario.budgetMs;
  return {
    ...scenario,
    elapsedMs: run.elapsedMs,
    status: run.status,
    pipelineOk,
    underBudget,
    pass: pipelineOk && underBudget,
    stdout: run.stdout,
    stderr: run.stderr
  };
}

function formatHuman(results) {
  const out = [];
  out.push(
    "Wall-clock measurement (Linux per-scenario budgets, see scripts/measure-hook-wallclock.js for rationale):"
  );
  out.push("");
  for (const r of results) {
    const elapsed = r.elapsedMs === null ? "n/a" : `${r.elapsedMs.toString().padStart(6, " ")} ms`;
    const budget = `${r.budgetMs} ms`;
    let verdict;
    if (r.error) {
      verdict = r.skipped ? `SKIPPED (${r.error})` : `ERROR (${r.error})`;
    } else if (!r.pipelineOk) {
      verdict = `HOOK FAILED (status=${r.status})`;
    } else if (!r.underBudget) {
      verdict = `OVER BUDGET`;
    } else {
      verdict = `ok`;
    }
    out.push(`  ${r.id.padEnd(22, " ")}  ${elapsed} / ${budget}  -> ${verdict}`);
  }
  out.push("");
  const failed = results.filter((r) => !r.pass);
  if (failed.length === 0) {
    out.push(`All ${results.length} scenarios passed.`);
  } else {
    out.push(`${failed.length}/${results.length} scenario(s) failed.`);
    for (const r of failed) {
      if (r.stderr) {
        out.push("");
        out.push(`-- stderr from ${r.id} --`);
        out.push(r.stderr.trimEnd());
      }
    }
  }
  return out.join("\n");
}

function main(argv) {
  const args = parseArgs(argv);

  if (!checkPreCommitAvailable()) {
    process.stderr.write(
      "Unable to ensure the pinned pre-commit runner. Run `node scripts/ensure-pre-commit.js install-hooks` before measuring.\n"
    );
    return 2;
  }

  const results = [];
  for (const scenario of SCENARIOS) {
    results.push(measureScenario(scenario, args));
  }

  if (args.json) {
    // Strip stdout/stderr from machine-readable output unless the run
    // failed; failures keep them so CI logs show why.
    const slim = results.map((r) => {
      if (r.pass) {
        const { stdout, stderr, ...rest } = r;
        return rest;
      }
      return r;
    });
    process.stdout.write(`${JSON.stringify({ budgetMs: BUDGET_MS, results: slim }, null, 2)}\n`);
  } else {
    process.stdout.write(`${formatHuman(results)}\n`);
  }

  const anyFailed = results.some((r) => !r.pass);
  return anyFailed ? 1 : 0;
}

module.exports = {
  BUDGET_MS,
  SCENARIOS,
  parseArgs,
  runPreCommit,
  measureScenario,
  formatHuman,
  main
};

if (require.main === module) {
  process.exit(main(process.argv.slice(2)));
}
