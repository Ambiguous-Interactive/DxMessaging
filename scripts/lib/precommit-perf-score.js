"use strict";

/**
 * Hook performance budget scorer for .pre-commit-config.yaml.
 *
 * Pre-commit must finish within seconds for a single-file commit on Windows
 * machines. Hooks that scan unfiltered file sets, spawn one process per file,
 * trigger network downloads, or run test runners blow that budget. This module
 * walks every hook in the config, ignores hooks that do not run at the
 * pre-commit stage, and scores each pre-commit hook against a fixed list of
 * anti-patterns.
 *
 * Hooks may opt out of specific scoring rules by placing a
 * `# perf-allow[<rule-ids>]: <reason>` comment immediately before the
 * `- id: <hookId>` line. The waived rule list MUST be enumerated and the
 * reason MUST be substantive (>=25 chars, not a stop-word). A rule that
 * fires but is not in the waiver list still contributes to the budget.
 *
 * Public API:
 *   - scoreConfig(content)
 *       returns { totalScore, perHookScores, allowList, rejections,
 *                 perHookViolations }
 *   - PERF_BUDGET       - integer score budget for the whole pre-commit pipeline
 *   - PER_HOOK_CEILING  - integer score above which any single hook is "doing
 *                         too much" and fails the budget on its own. Set so
 *                         that single-rule regressions (e.g. a `bash -lc`
 *                         that adds 3 points to one hook) cannot hide under
 *                         the cumulative slack of the total budget.
 *   - SCORING_RULES     - human-readable list of rule descriptions
 *   - parseAllowDirective(commentLine)
 *       returns { ruleIds, reason, valid, error } | null
 */

const { findAllHookBlocks, extractStagesFromHookBlock, getIndent } = require("./precommit-yaml");
const { normalizeToLf } = require("./quote-parser");

const PERF_BUDGET = 10;
// PER_HOOK_CEILING is intentionally lower than PERF_BUDGET. The total budget
// catches accumulated drift across many hooks; the per-hook ceiling catches
// single-rule regressions on one hook (e.g. a `bash -lc` (5) or
// `npm-spawn` (5) added to one entry would otherwise sit comfortably under
// the total budget while doing real per-commit damage). 3 is the threshold
// because every defined rule is either =< 3 (acceptable singly) or >= 5
// (immediately over the ceiling); a hook scoring 3 is at the edge but not
// in violation, while a hook scoring 4+ is.
const PER_HOOK_CEILING = 3;

const MIN_REASON_LENGTH = 25;
const FORBIDDEN_REASON_TOKENS = new Set([
  "x",
  "n/a",
  "todo",
  "tbd",
  "noop",
  "fixme",
  "idk",
  "meh",
  "ok",
  "legacy",
  "because"
]);

const SCORING_RULES = [
  {
    id: "scans-the-world",
    score: 5,
    description: "pass_filenames: false with no files: filter -> hook scans every file in the repo"
  },
  {
    id: "scans-the-world-with-files",
    score: 3,
    description:
      "pass_filenames: false with a files: filter -> still pays the scan cost (no incremental input)"
  },
  {
    id: "always-run",
    score: 5,
    description: "always_run: true -> hook fires on every commit regardless of staged input"
  },
  {
    id: "npm-spawn",
    score: 5,
    description:
      "Entry contains npm pack/install/exec/test or npm run validate:npm-meta -> heavy npm child process"
  },
  {
    id: "dotnet-no-batch",
    score: 5,
    description:
      "Entry uses 'dotnet tool run' without require_serial: true -> N file spawns instead of one batched call"
  },
  {
    id: "jest-at-pre-commit",
    score: 5,
    description:
      "Entry runs Jest (run-managed-jest.js or bare jest) at pre-commit -> tests belong at pre-push"
  },
  {
    id: "npx-cold-start",
    score: 2,
    description: "Entry uses 'npx --yes' -> may download package on cold caches"
  },
  {
    id: "bash-login-shell",
    score: 3,
    description:
      "Entry uses 'bash -lc' or 'bash --login -c' -> loads login profiles (nvm init etc.) and adds 100-500ms per fire"
  },
  {
    id: "node-double-spawn",
    score: 3,
    description:
      "Entry runs node scripts/run-managed-*.js where the wrapper just spawns a second node/npx process -> ~600-1200ms double-spawn cost"
  },
  {
    id: "npm-run-at-hook",
    score: 3,
    description:
      "Entry uses 'npm run <script>' -> npm wraps the actual script and adds ~500ms-1s of node startup before the work begins"
  }
];

const RULE_INDEX = new Map(SCORING_RULES.map((rule) => [rule.id, rule]));
const VALID_RULE_IDS = new Set(SCORING_RULES.map((rule) => rule.id));

function getEntryText(blockLines) {
  // Capture the entry value, including folded/literal block scalars.
  const out = [];
  for (let i = 0; i < blockLines.length; i++) {
    const line = blockLines[i];
    const entryMatch = /^(\s*)entry:\s*(.*)$/.exec(line);
    if (!entryMatch) {
      continue;
    }

    const entryIndent = entryMatch[1].length;
    const value = entryMatch[2].trim();

    if ([">", ">-", "|", "|-"].includes(value)) {
      for (let j = i + 1; j < blockLines.length; j++) {
        const continuation = blockLines[j];
        if (continuation.trim().length === 0) {
          continue;
        }
        if (getIndent(continuation) <= entryIndent) {
          break;
        }
        out.push(continuation.trim());
      }
    } else {
      out.push(value);
    }
    break;
  }
  return out.join(" ");
}

function findFilesLine(blockLines) {
  return blockLines.some((line) => /^\s*files:\s*\S/.test(line));
}

function findPassFilenamesFalse(blockLines) {
  return blockLines.some((line) => /^\s*pass_filenames:\s*false\s*$/.test(line));
}

function findAlwaysRunTrue(blockLines) {
  return blockLines.some((line) => /^\s*always_run:\s*true\s*$/.test(line));
}

function findRequireSerialTrue(blockLines) {
  return blockLines.some((line) => /^\s*require_serial:\s*true\s*$/.test(line));
}

function entryUsesNpmHeavy(entry) {
  if (/\bnpm\s+pack\b/.test(entry)) return true;
  if (/\bnpm\s+install\b/.test(entry)) return true;
  if (/\bnpm\s+exec\b/.test(entry)) return true;
  if (/\bnpm\s+test\b/.test(entry)) return true;
  if (/\bnpm\s+run\s+validate:npm-meta\b/.test(entry)) return true;
  return false;
}

function entryUsesDotnetTool(entry) {
  return /\bdotnet\s+tool\s+run\b/.test(entry);
}

function entryUsesJest(entry) {
  if (/\brun-managed-jest\.js\b/.test(entry)) return true;
  if (/(?:^|[\s/])jest(?:\s|$)/.test(entry) && !/run-managed-jest\.js/.test(entry)) {
    return true;
  }
  return false;
}

// Match `npx --yes` or `npx <pkg-or-flags...> --yes`. The scorer reads only
// `entry:` values, never prose, so this regex never sees comment text.
function entryUsesNpxYes(entry) {
  return /\bnpx\s+(?:[^&|;]*\s)?--yes\b/.test(entry) || /\bnpx\s+--yes\b/.test(entry);
}

// Round-3 anti-patterns: the previous round shipped wrappers and shells that
// looked harmless to the static scorer but cost real wall-clock per fire.
function entryUsesBashLoginShell(entry) {
  if (/\bbash\s+-lc\b/.test(entry)) return true;
  if (/\bbash\s+(?:-l\s+-c|-c\s+-l|--login\s+-c|-c\s+--login)\b/.test(entry)) return true;
  return false;
}

// Detects "node scripts/run-managed-<name>.js" where <name> wraps a bin in a
// way that spawns a second process. Only the Jest wrapper is exempt: managed
// Jest orchestrates a deterministic local-vs-fallback Jest invocation that
// cannot be expressed as an inline `bash -c` entry without losing the
// fallback semantics, and Jest only fires at pre-push so the double-spawn
// cost is paid once per push rather than once per commit. Every other
// managed wrapper that a hook pipeline might invoke must be inlined the
// same way the cspell, markdownlint, and (round-4) prettier hooks were
// inlined: a `bash -c` entry that prefers the local devDependency bin and
// falls back to a pinned `npx --yes --package=<spec>` install, paired with
// a version-parity test against `package.json`.
function entryUsesNodeDoubleSpawn(entry) {
  const m = entry.match(/\bnode\s+scripts\/(run-managed-[A-Za-z0-9-]+)\.js\b/);
  if (!m) return false;
  const name = m[1];
  if (name === "run-managed-jest") {
    return false;
  }
  return true;
}

function entryUsesNpmRun(entry) {
  return /\bnpm\s+run\s+\S+/.test(entry);
}

function effectiveStages(stages) {
  if (!stages || stages.length === 0) {
    return ["pre-commit"];
  }
  return stages;
}

function isReasonSubstantive(reason) {
  const trimmed = String(reason || "").trim();
  if (trimmed.length === 0) {
    return { valid: false, error: "empty reason" };
  }

  // Pure punctuation / whitespace.
  if (!/[A-Za-z0-9]/.test(trimmed)) {
    return { valid: false, error: "reason has no alphanumeric content" };
  }

  if (trimmed.length < MIN_REASON_LENGTH) {
    return {
      valid: false,
      error: `reason is shorter than ${MIN_REASON_LENGTH} chars (got ${trimmed.length})`
    };
  }

  const lowered = trimmed.toLowerCase();
  if (FORBIDDEN_REASON_TOKENS.has(lowered)) {
    return { valid: false, error: `reason is a stop-word ("${trimmed}")` };
  }

  return { valid: true };
}

function parseAllowDirective(commentLine) {
  if (typeof commentLine !== "string") {
    return null;
  }

  // Reject legacy `# perf-allow: <reason>` (no brackets).
  const legacyMatch = /^\s*#\s*perf-allow:\s*(.*)$/.exec(commentLine);
  if (legacyMatch && !/^\s*#\s*perf-allow\[/.test(commentLine)) {
    return {
      ruleIds: [],
      reason: legacyMatch[1].trim(),
      valid: false,
      error:
        "perf-allow must declare which rule IDs it waives, e.g. " +
        "# perf-allow[scans-the-world]: <substantive reason>"
    };
  }

  const bracketMatch = /^\s*#\s*perf-allow\[([^\]]*)\]\s*:\s*(.*)$/.exec(commentLine);
  if (!bracketMatch) {
    return null;
  }

  const ruleIdsRaw = bracketMatch[1].trim();
  const reason = bracketMatch[2].trim();

  if (ruleIdsRaw.length === 0) {
    return {
      ruleIds: [],
      reason,
      valid: false,
      error: "perf-allow rule list is empty; declare at least one rule ID inside brackets"
    };
  }

  const ruleIds = ruleIdsRaw
    .split(",")
    .map((token) => token.trim())
    .filter((token) => token.length > 0);

  const unknown = ruleIds.filter((id) => !VALID_RULE_IDS.has(id));
  if (unknown.length > 0) {
    return {
      ruleIds,
      reason,
      valid: false,
      error:
        `perf-allow rule ID(s) not recognized: ${unknown.join(", ")}. ` +
        `Valid IDs: ${[...VALID_RULE_IDS].sort().join(", ")}`
    };
  }

  const reasonCheck = isReasonSubstantive(reason);
  if (!reasonCheck.valid) {
    return {
      ruleIds,
      reason,
      valid: false,
      error: `perf-allow reason rejected: ${reasonCheck.error}`
    };
  }

  return {
    ruleIds,
    reason,
    valid: true
  };
}

function findAllowDirective(allLines, hookStartLine) {
  // hookStartLine is 1-based. Walk backward over blank lines to find the
  // closest preceding non-blank comment.
  let i = hookStartLine - 2; // 0-based index of the line above
  while (i >= 0 && allLines[i].trim().length === 0) {
    i--;
  }
  if (i < 0) {
    return null;
  }
  return parseAllowDirective(allLines[i]);
}

function scoreHookBlock(block, allLines) {
  const stages = effectiveStages(extractStagesFromHookBlock(block));
  if (!stages.includes("pre-commit")) {
    return { applicable: false };
  }

  const entry = getEntryText(block.lines);
  const passFalse = findPassFilenamesFalse(block.lines);
  const hasFiles = findFilesLine(block.lines);
  const alwaysRun = findAlwaysRunTrue(block.lines);
  const requireSerial = findRequireSerialTrue(block.lines);

  const reasons = [];

  if (passFalse && !hasFiles) {
    reasons.push({ ruleId: "scans-the-world", score: 5 });
  } else if (passFalse && hasFiles) {
    reasons.push({ ruleId: "scans-the-world-with-files", score: 3 });
  }

  if (alwaysRun) {
    reasons.push({ ruleId: "always-run", score: 5 });
  }

  if (entryUsesNpmHeavy(entry)) {
    reasons.push({ ruleId: "npm-spawn", score: 5 });
  }

  if (entryUsesDotnetTool(entry) && !requireSerial) {
    reasons.push({ ruleId: "dotnet-no-batch", score: 5 });
  }

  if (entryUsesJest(entry)) {
    reasons.push({ ruleId: "jest-at-pre-commit", score: 5 });
  }

  if (entryUsesNpxYes(entry)) {
    reasons.push({ ruleId: "npx-cold-start", score: 2 });
  }

  if (entryUsesBashLoginShell(entry)) {
    reasons.push({ ruleId: "bash-login-shell", score: 3 });
  }

  if (entryUsesNodeDoubleSpawn(entry)) {
    reasons.push({ ruleId: "node-double-spawn", score: 3 });
  }

  if (entryUsesNpmRun(entry)) {
    reasons.push({ ruleId: "npm-run-at-hook", score: 3 });
  }

  const baseScore = reasons.reduce((sum, r) => sum + r.score, 0);
  const directive = findAllowDirective(allLines, block.startLine);

  let waivedRuleIds = new Set();
  let allowReason = null;
  let rejection = null;

  if (directive) {
    if (directive.valid) {
      waivedRuleIds = new Set(directive.ruleIds);
      allowReason = directive.reason;
    } else {
      rejection = {
        error: directive.error,
        reason: directive.reason,
        ruleIds: directive.ruleIds || []
      };
    }
  }

  // Subtract scores only for rules that fired AND are explicitly waived.
  let waivedScore = 0;
  const waivedReasons = [];
  const unwaivedReasons = [];
  for (const reason of reasons) {
    if (waivedRuleIds.has(reason.ruleId)) {
      waivedScore += reason.score;
      waivedReasons.push(reason);
    } else {
      unwaivedReasons.push(reason);
    }
  }

  const finalScore = baseScore - waivedScore;

  return {
    applicable: true,
    id: block.id,
    startLine: block.startLine,
    baseScore,
    reasons,
    entry,
    allowReason,
    waivedRuleIds: [...waivedRuleIds],
    waivedReasons,
    unwaivedReasons,
    rejection,
    finalScore
  };
}

function scoreConfig(content) {
  const lines = normalizeToLf(content).split("\n");
  const blocks = findAllHookBlocks(lines);

  const perHookScores = [];
  const allowList = [];
  const rejections = [];
  const perHookViolations = [];
  let totalScore = 0;

  for (const block of blocks) {
    const result = scoreHookBlock(block, lines);
    if (!result.applicable) {
      continue;
    }
    if (result.baseScore === 0 && !result.rejection) {
      continue;
    }

    perHookScores.push(result);

    if (result.rejection) {
      rejections.push({
        id: result.id,
        startLine: result.startLine,
        ...result.rejection
      });
    }

    if (result.waivedRuleIds.length > 0 && result.allowReason) {
      allowList.push({
        id: result.id,
        reason: result.allowReason,
        ruleIds: result.waivedRuleIds,
        waivedScore: result.baseScore - result.finalScore
      });
    }

    totalScore += result.finalScore;

    // Per-hook ceiling: any single hook whose post-waiver score is
    // strictly greater than PER_HOOK_CEILING fails on its own. A waived
    // hook that nets out under the ceiling is fine; we only report the
    // unwaived portion to avoid counting a legitimate exemption as a
    // single-hook regression.
    if (result.finalScore > PER_HOOK_CEILING) {
      perHookViolations.push({
        id: result.id,
        startLine: result.startLine,
        score: result.finalScore,
        ceiling: PER_HOOK_CEILING,
        contributingRules: result.unwaivedReasons.map((r) => ({
          ruleId: r.ruleId,
          score: r.score
        }))
      });
    }
  }

  return { totalScore, perHookScores, allowList, rejections, perHookViolations };
}

function formatReport(result) {
  const lines = [];
  lines.push(
    `Pre-commit perf score: ${result.totalScore} (total budget ${PERF_BUDGET}, per-hook ceiling ${PER_HOOK_CEILING}).`
  );

  if (result.rejections && result.rejections.length > 0) {
    lines.push("Rejected perf-allow directives:");
    for (const rejection of result.rejections) {
      lines.push(
        `  - rejected reason: '${rejection.reason}' on hook ${rejection.id} ` +
          `(line ${rejection.startLine}): ${rejection.error}`
      );
    }
  }

  if (result.perHookViolations && result.perHookViolations.length > 0) {
    lines.push(
      `Per-hook ceiling violations (any single hook above ${PER_HOOK_CEILING} fails on its own):`
    );
    for (const violation of result.perHookViolations) {
      const ruleSummary = violation.contributingRules
        .map((r) => `${r.ruleId}(+${r.score})`)
        .join(", ");
      lines.push(
        `  - ${violation.id} (line ${violation.startLine}): score=${violation.score} > ceiling=${violation.ceiling} ` +
          `[contributing rules: ${ruleSummary || "(none unwaived; check waiver scope)"}]`
      );
      lines.push(
        "      remediation: split the hook, narrow its files: filter, move to pre-push, or " +
          "add a `# perf-allow[<rule-ids>]: <substantive 25+ char reason>` immediately above " +
          "the `- id:` line that names the specific rules to waive."
      );
    }
  }

  if (result.perHookScores.length === 0) {
    lines.push("No anti-patterns detected.");
    return lines.join("\n");
  }

  lines.push("Contributing hooks:");
  for (const hook of result.perHookScores) {
    const allowSuffix = hook.allowReason
      ? ` [waived: ${hook.waivedRuleIds.join(",")} | reason: ${hook.allowReason}]`
      : "";
    lines.push(
      `  - ${hook.id} (line ${hook.startLine}): base=${hook.baseScore} final=${hook.finalScore}${allowSuffix}`
    );
    for (const reason of hook.reasons) {
      const rule = RULE_INDEX.get(reason.ruleId);
      const waivedMark = hook.waivedRuleIds.includes(reason.ruleId) ? " (waived)" : "";
      lines.push(`      +${reason.score} [${reason.ruleId}]${waivedMark} ${rule.description}`);
    }
  }
  return lines.join("\n");
}

module.exports = {
  PERF_BUDGET,
  PER_HOOK_CEILING,
  MIN_REASON_LENGTH,
  FORBIDDEN_REASON_TOKENS,
  SCORING_RULES,
  RULE_INDEX,
  VALID_RULE_IDS,
  isReasonSubstantive,
  parseAllowDirective,
  scoreConfig,
  scoreHookBlock,
  formatReport,
  getEntryText,
  effectiveStages,
  findAllowDirective,
  entryUsesBashLoginShell,
  entryUsesNodeDoubleSpawn,
  entryUsesNpmRun
};
