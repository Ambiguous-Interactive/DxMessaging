/**
 * @fileoverview Class-prevention guard: any CI step that runs a repo node script
 * whose transitive (local-require) closure STATICALLY requires a third-party
 * package MUST be preceded in the same job by a node_modules install.
 *
 * THE CLASS THIS GUARDS. The Actionlint job ran `node scripts/validate-workflows.js`
 * after only `actions/setup-node` -- it never installed dependencies. The
 * validator imports the `yaml` package, so `require("yaml")` threw
 * MODULE_NOT_FOUND on a fresh CI checkout and EVERY workflow failed the
 * compute-unity-assemblies gate check (40 errors). The class is "a job runs a
 * node script that needs node_modules, but the job never installed them."
 *
 * THE RULE. For each job in every workflow, gather every repo node-script
 * invocation it reaches, across every vector that runs repo JS in CI:
 *   1. shell `node scripts/<file>.js` (quoted or unquoted) in a `run:` step;
 *   2. `npm run <name>` whose package.json script (transitively) runs such a
 *      node script;
 *   3. inline JS bodies -- `node -e/-p`/`--eval/--print "<js>"` and `node <<'EOF'`
 *      heredoc-stdin -- whose body `require()`s a repo script or a package; and
 *   4. `uses: ./.github/actions/<name>` composite actions, whose `run:` steps
 *      are scanned for the same forms -- recursively through nested
 *      `uses: ./.github/actions/<other>` composites (cycle-safe).
 * For each invoked script, compute its transitive third-party closure with the
 * shared `node-require-scan` helper. If a script STATICALLY require-closes to a
 * third-party package, an install step (`npm ci` / `npm install`) must run at or
 * before that step (and, within a single step, textually before the script).
 *
 * Scripts with an empty closure are self-contained (only builtins + local files)
 * -- the dependency-free wiki scripts, and run-managed-prettier.js which
 * bootstraps its own tooling -- so steps that run only those need no install and
 * are correctly NOT flagged. The check keys on the ACTUAL import graph, not an
 * allowlist, so it cannot rot as scripts gain or shed dependencies.
 *
 * Boundary (the accepted undecidable floor): the closure is STATIC. A script
 * that spawns `node other.js` or resolves a module by a computed path expresses
 * a need this guard cannot see; such needs are out of scope by construction.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const YAML = require("yaml");
const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");
const { collectTransitiveThirdParty } = require("../lib/node-require-scan");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const WORKFLOWS_DIR = path.join(REPO_ROOT, ".github", "workflows");
const ACTIONS_DIR = path.join(REPO_ROOT, ".github", "actions");

const pkg = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, "package.json"), "utf8"));
const NPM_SCRIPTS = pkg.scripts || {};

// `node scripts/x.js`, optionally quoted (`node "scripts/x.js"`) and optionally
// `./`-prefixed (the repo's `./scripts/...` invocation convention). The capture
// always yields the bare `scripts/...` path so closureForScriptFile's REPO_ROOT
// join is unchanged; `../scripts` stays excluded (out-of-tree). The negated
// class stops the capture before a closing quote/separator.
const NODE_SCRIPT_RE = /(?<![.\w$])node\s+["']?(?:\.\/)?(scripts\/[^\s'";|&)]+\.(?:js|cjs|mjs))/g;
// `node -e/-p "<js>"` / `node --eval/--print '<js>'` -- captures the quoted JS
// body. All four flags evaluate an inline body that can `require()` a package.
const NODE_EVAL_RE =
  /(?<![.\w$])node\s+(?:-e|--eval|-p|--print)\s+("(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*')/g;
// `node <<'EOF' ... EOF` heredoc-stdin: another inline-JS vector. Matches the
// node-launched heredoc (quoted or bare delimiter, `<<` or `<<-`) and captures
// the body up to the terminator line. YAML strips the block indentation, so the
// terminator sits at line start in the parsed run text.
const NODE_HEREDOC_RE =
  /(?<![.\w$])node\b[^\n]*?<<-?\s*(['"]?)([A-Za-z_]\w*)\1[^\n]*\n([\s\S]*?)\n[ \t]*\2(?=\s|$)/g;
const NPM_RUN_RE = /\bnpm\s+run\s+([A-Za-z0-9:_-]+)/g;
const INSTALL_RE = /\bnpm\s+(?:ci|install|i)\b/;

/** Third-party closure of an inline JS body whose relative requires resolve at REPO_ROOT (CI cwd). */
function inlineBodyNeeds(body) {
  return collectTransitiveThirdParty(path.join(REPO_ROOT, "__inline__.js"), { sourceText: body })
    .thirdParty;
}

/** Third-party packages a repo script file (by repo-relative path) require-closes to. */
function closureForScriptFile(scriptRel) {
  const abs = path.join(REPO_ROOT, scriptRel);
  if (!fs.existsSync(abs)) {
    return { missing: scriptRel, thirdParty: new Set() };
  }
  return { missing: null, thirdParty: collectTransitiveThirdParty(abs).thirdParty };
}

/** Decode a shell-quoted `node -e` argument into its raw JS body. */
function decodeShellQuoted(quoted) {
  const inner = quoted.slice(1, -1);
  // Both shell forms only escape the wrapping quote and backslash for our needs.
  return inner.replace(/\\(["'\\])/g, "$1");
}

/**
 * All third-party packages a single shell snippet's node invocations need:
 * direct script files, `npm run` chains, and inline `node -e`/heredoc bodies.
 * Returns { needs:Set<string>, missing:string[] } -- needs is the union closure.
 */
function shellNodeNeeds(shell) {
  const needs = new Set();
  const missing = [];
  let match;

  NODE_SCRIPT_RE.lastIndex = 0;
  while ((match = NODE_SCRIPT_RE.exec(shell)) !== null) {
    const { missing: miss, thirdParty } = closureForScriptFile(match[1]);
    if (miss) {
      missing.push(miss);
    }
    thirdParty.forEach((p) => needs.add(p));
  }

  NODE_EVAL_RE.lastIndex = 0;
  while ((match = NODE_EVAL_RE.exec(shell)) !== null) {
    inlineBodyNeeds(decodeShellQuoted(match[1])).forEach((p) => needs.add(p));
  }

  NODE_HEREDOC_RE.lastIndex = 0;
  while ((match = NODE_HEREDOC_RE.exec(shell)) !== null) {
    // match[3] is the raw heredoc body (no shell quote-escaping to undo).
    inlineBodyNeeds(match[3]).forEach((p) => needs.add(p));
  }

  // Follow `npm run <name>` into package.json scripts (transitively).
  const visited = new Set();
  const queue = [];
  NPM_RUN_RE.lastIndex = 0;
  while ((match = NPM_RUN_RE.exec(shell)) !== null) {
    queue.push(match[1]);
  }
  while (queue.length > 0) {
    const name = queue.pop();
    if (visited.has(name)) {
      continue;
    }
    visited.add(name);
    const body = NPM_SCRIPTS[name];
    if (typeof body !== "string") {
      continue;
    }
    let inner;
    NODE_SCRIPT_RE.lastIndex = 0;
    while ((inner = NODE_SCRIPT_RE.exec(body)) !== null) {
      const { missing: miss, thirdParty } = closureForScriptFile(inner[1]);
      if (miss) {
        missing.push(miss);
      }
      thirdParty.forEach((p) => needs.add(p));
    }
    NPM_RUN_RE.lastIndex = 0;
    while ((inner = NPM_RUN_RE.exec(body)) !== null) {
      queue.push(inner[1]);
    }
  }

  return { needs, missing };
}

/** Resolve a `uses: ./.github/actions/<name>` ref to the union node-needs of its composite run: steps. */
const compositeNeedsCache = new Map();
function compositeActionNeeds(usesRef, options = {}) {
  const actionsDir = options.actionsDir || ACTIONS_DIR;
  const cache = options.cache || compositeNeedsCache;
  const cacheKey = actionsDir === ACTIONS_DIR ? usesRef : `${actionsDir}\0${usesRef}`;
  if (cache.has(cacheKey)) {
    return cache.get(cacheKey);
  }
  const result = { needs: new Set(), missing: [] };
  // Seed the cache before scanning/recursing so a cyclic `uses:` graph
  // terminates (a composite reaching itself sees the in-progress result).
  cache.set(cacheKey, result);
  const rel = usesRef.replace(/^\.\//, "");
  const actionName = rel.replace(/^\.github\/actions\//, "");
  const actionDir = path.join(actionsDir, actionName);
  const actionFile = ["action.yml", "action.yaml"]
    .map((f) => path.join(actionDir, f))
    .find((f) => fs.existsSync(f));
  if (actionFile) {
    const action = YAML.parse(fs.readFileSync(actionFile, "utf8"));
    const steps =
      action && action.runs && Array.isArray(action.runs.steps) ? action.runs.steps : [];
    for (const step of steps) {
      if (step && typeof step.run === "string") {
        const { needs, missing } = shellNodeNeeds(step.run);
        needs.forEach((p) => result.needs.add(p));
        missing.forEach((m) => result.missing.push(m));
      } else if (
        step &&
        typeof step.uses === "string" &&
        step.uses.startsWith("./.github/actions/")
      ) {
        // Nested composite: follow the same `uses:` vector recursively so the
        // class is closed at any depth, not just one level.
        const nested = compositeActionNeeds(step.uses, { actionsDir, cache });
        nested.needs.forEach((p) => result.needs.add(p));
        nested.missing.forEach((m) => result.missing.push(m));
      }
    }
  }
  return result;
}

/**
 * Pure per-job analysis (no disk walk of its own; closures are read by the
 * helpers). Returns the first install-step index and each dependency-needing
 * step's node-needs, with the in-step install-vs-use ordering for run steps.
 * Exposed as a standalone function so the ordering logic can be unit-tested
 * with synthetic step lists, not only via the real workflows.
 *
 * @param {Array} steps - a job's `steps` array
 * @returns {{ firstInstallStep: number, needsSteps: Array }}
 */
function analyzeJobSteps(steps) {
  const ordered = Array.isArray(steps) ? steps : [];
  let firstInstallStep = Infinity;
  ordered.forEach((step, idx) => {
    const run = step && typeof step.run === "string" ? step.run : "";
    if (run && INSTALL_RE.test(run)) {
      firstInstallStep = Math.min(firstInstallStep, idx);
    }
  });

  const needsSteps = [];
  ordered.forEach((step, idx) => {
    if (!step) {
      return;
    }
    if (typeof step.run === "string" && step.run) {
      const { needs, missing } = shellNodeNeeds(step.run);
      if (needs.size > 0 || missing.length > 0) {
        // For a run step, an install in the SAME step counts only if it is
        // textually before the first node invocation (`npm ci && node x.js`).
        const installMatch = step.run.match(INSTALL_RE);
        const useMatch = step.run.match(/(?<![.\w$])node\s/);
        const installBeforeUseInStep =
          installMatch && useMatch ? installMatch.index < useMatch.index : false;
        needsSteps.push({ idx, needs, missing, installBeforeUseInStep, source: "run" });
      }
    } else if (typeof step.uses === "string" && step.uses.startsWith("./.github/actions/")) {
      const { needs, missing } = compositeActionNeeds(step.uses);
      if (needs.size > 0 || missing.length > 0) {
        // A composite `uses:` step cannot carry an inline install, so it is
        // only satisfied by an install in an EARLIER step.
        needsSteps.push({ idx, needs, missing, installBeforeUseInStep: false, source: step.uses });
      }
    }
  });

  return { firstInstallStep, needsSteps };
}

/** The install-ordering violations for one analyzed job. */
function jobViolations({ firstInstallStep, needsSteps }) {
  const violations = [];
  for (const step of needsSteps) {
    for (const miss of step.missing) {
      violations.push(`runs missing script ${miss}`);
    }
    if (step.needs.size === 0) {
      continue;
    }
    const installedEarlier = step.idx > firstInstallStep;
    const installedHere = step.idx === firstInstallStep && step.installBeforeUseInStep;
    if (!installedEarlier && !installedHere) {
      violations.push(
        `step #${step.idx} (${step.source}) needs ${[...step.needs].sort().join(", ")}, ` +
          `but the first npm install step is ` +
          `${firstInstallStep === Infinity ? "absent" : `#${firstInstallStep}`}`
      );
    }
  }
  return violations;
}

function analyzeJobs() {
  const rows = [];
  for (const file of fs.readdirSync(WORKFLOWS_DIR).filter((f) => /\.ya?ml$/.test(f))) {
    const doc = YAML.parse(fs.readFileSync(path.join(WORKFLOWS_DIR, file), "utf8"));
    const jobs = doc && typeof doc === "object" ? doc.jobs : null;
    if (!jobs || typeof jobs !== "object") {
      continue;
    }
    for (const [jobId, job] of Object.entries(jobs)) {
      const analyzed = analyzeJobSteps(job && job.steps);
      if (analyzed.needsSteps.length > 0) {
        rows.push({ file, jobId, ...analyzed });
      }
    }
  }
  return rows;
}

const JOB_ROWS = analyzeJobs();

describe("CI jobs install node_modules before running dependency-needing scripts", () => {
  test("workflow jobs that run node scripts are discovered (guard is not vacuous)", () => {
    expect(JOB_ROWS.length).toBeGreaterThan(0);
  });

  test.each(JOB_ROWS.map((r) => [`${r.file} :: ${r.jobId}`, r]))("%s", (_label, row) => {
    const label = `${row.file} :: ${row.jobId}`;
    expect({ job: label, violations: jobViolations(row) }).toEqual({ job: label, violations: [] });
  });

  // Same-step install-vs-use ordering. No real workflow currently runs install
  // and a dep-needing script in ONE step (the repo convention is a separate
  // "Install dependencies" step), so these synthetic fixtures pin that branch:
  // `npm ci && node x.js` is satisfied, the reverse order is flagged. Uses the
  // real validate-workflows.js (non-empty `yaml` closure) so the need is genuine.
  describe("same-step install ordering (synthetic fixtures)", () => {
    const DEP_SCRIPT = "node scripts/validate-workflows.js";

    test("install before use in one step is satisfied", () => {
      const analyzed = analyzeJobSteps([{ run: `npm ci && ${DEP_SCRIPT}` }]);
      expect(analyzed.needsSteps[0].needs.has("yaml")).toBe(true);
      expect(jobViolations(analyzed)).toEqual([]);
    });

    test("use before install in one step is flagged", () => {
      const analyzed = analyzeJobSteps([{ run: `${DEP_SCRIPT} && npm ci` }]);
      expect(jobViolations(analyzed)).toHaveLength(1);
      expect(jobViolations(analyzed)[0]).toContain("needs yaml");
    });

    test("a dep-needing script with no install anywhere is flagged", () => {
      const analyzed = analyzeJobSteps([{ run: "echo hi" }, { run: DEP_SCRIPT }]);
      expect(jobViolations(analyzed)).toHaveLength(1);
      expect(jobViolations(analyzed)[0]).toContain("install step is absent");
    });

    test("the `./scripts/` invocation convention is also matched", () => {
      const analyzed = analyzeJobSteps([{ run: "node ./scripts/validate-workflows.js" }]);
      expect(analyzed.needsSteps[0].needs.has("yaml")).toBe(true);
      expect(jobViolations(analyzed)).toHaveLength(1);
    });

    test("a node heredoc body that needs a package is scanned and flagged", () => {
      const analyzed = analyzeJobSteps([
        { run: "node <<'NODE'\nconst y = require('yaml');\nNODE\n" }
      ]);
      expect(analyzed.needsSteps[0].needs.has("yaml")).toBe(true);
      expect(jobViolations(analyzed)).toHaveLength(1);
    });
  });

  test("the composite-action vector is actually exercised (compute-unity-assemblies is scanned)", () => {
    // Regression anchor for the class extension: the compute-unity-assemblies
    // composite runs `node -e \"...require('./scripts/...')\"`. The scanner must
    // reach that script. It is self-contained today (empty closure), so it adds
    // no install requirement -- but if it ever gains a third-party import, the
    // 6 workflows using the composite would be flagged.
    const needs = compositeActionNeeds("./.github/actions/compute-unity-assemblies");
    expect(needs.missing).toEqual([]);
    // Sanity: the helper resolved a real action file (the cache holds an entry).
    expect(compositeNeedsCache.has("./.github/actions/compute-unity-assemblies")).toBe(true);
  });

  test("nested composite `uses:` is followed by the real resolver (recursion + cycle-safety)", () => {
    // print-self-hosted-runner-diagnostics nests assert-unity-host-prereqs and
    // is consumed by 5 workflows. Neither nested composite runs node today, so
    // there is no live requirement -- prove the recursion is non-vacuous by
    // exercising the SHIPPED compositeActionNeeds against doctored composites
    // under an isolated temp action root. This keeps Jest's parallel workers
    // from observing transient directories under the real .github/actions tree.
    // A nested third-party need must surface in the parent's closure, and a
    // self-referential `uses:` must terminate.
    const tempRoot = makeTempDir("nested-actions");
    const tempActionsDir = path.join(tempRoot, ".github", "actions");
    const childDir = path.join(tempActionsDir, "__nested_child__");
    const parentDir = path.join(tempActionsDir, "__nested_parent__");
    const cache = new Map();
    fs.mkdirSync(childDir, { recursive: true });
    fs.mkdirSync(parentDir, { recursive: true });
    try {
      fs.writeFileSync(
        path.join(childDir, "action.yml"),
        "runs:\n  using: composite\n  steps:\n" +
          "    - shell: bash\n      run: node -e \"require('yaml')\"\n"
      );
      // Parent runs no node itself; it only nests the child and references
      // itself to exercise the cycle guard.
      fs.writeFileSync(
        path.join(parentDir, "action.yml"),
        "runs:\n  using: composite\n  steps:\n" +
          "    - uses: ./.github/actions/__nested_child__\n" +
          "    - uses: ./.github/actions/__nested_parent__\n"
      );
      const got = compositeActionNeeds("./.github/actions/__nested_parent__", {
        actionsDir: tempActionsDir,
        cache
      });
      expect([...got.needs]).toEqual(["yaml"]);
      expect(got.missing).toEqual([]);
    } finally {
      cleanupDir(tempRoot);
    }

    // Confirm the real nested composite in the repo resolves cleanly.
    const real = compositeActionNeeds("./.github/actions/print-self-hosted-runner-diagnostics");
    expect(real.missing).toEqual([]);
  });
});
