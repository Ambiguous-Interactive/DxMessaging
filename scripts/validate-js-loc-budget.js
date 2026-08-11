#!/usr/bin/env node
"use strict";

/**
 * Fails when tracked JavaScript (*.js / *.cjs / *.mjs) exceeds the repo-wide
 * line budget. This repo is a Unity C# package; JS exists only as thin CI/docs
 * support and must stay small. Raising the budget is a reviewed decision in
 * the same change that needs it.
 */

const { execFileSync } = require("child_process");
const fs = require("fs");
const path = require("path");

// Budget history, newest last:
// 047 skills-index generation after zero-loss script cuts: 10600.
// 052 auto-commit force-refspec drift guard: 10650.
// 055 CI aggregate-workflow topology guards: 10890.
// 056 llms.txt/README skill-count validation: 11185.
// 057 update/check convergence validation: 11350.
// 058 release notes, changelog extraction, export staging: 11820.
// 059 cross-platform PowerShell project-path safety tests: 11960.
// 062 issue-template version generator and fetch-refspec guard: 12360.
// 064 allocation-honesty perf sentinel handling: 12390.
// 065 PlayMode allocation leg and perf-scenario sharing: 12664.
// 066 banner --check diagnostics: 12795.
// 067 package-script contract guard and validate:all issue-template gate: 12890.
// 068 per-operation gcAllocatedBytes perf metric (column, delta, backward-compat): 13033.
// 069 gcAllocatedBytes cross-library comparison matrix (renderer + scope reuse + tests): 13061.
// 070 omit all-unmeasured (n/a) memory columns/matrices/delta-segments so the
//     profiler-stripped IL2CPP leg stops publishing vacuous n/a (renderer column +
//     matrix gating, delta segment gating, red-green tests): 13140.
// 071 tested Asset Store submission generator (manifest, store media, classic
//     and UPM checklists) replacing inline release shell: 13600.
// 072 design-system editor CI guards (untracked-dump + complete-border +
//     blocked-capture-primitive checks, stable editor-window test-host teardown
//     guard) for the cleaner-site design-system convergence: 13700.
// 073 Unity MCP + Agent Skills harness, both tested, both replacing untested
//     tooling. Added: unity-mcp.mjs (1242) + its suite (655) replacing 404
//     lines of untested shell/PowerShell and the runtime supergateway
//     dependency; llm/harness.js (515) + its suite (192) replacing
//     generate-skills-index.js (311) + its suite (332). Net +1961 tested JS
//     for -1047 lines retired, and the two remaining generators now cover
//     spec validation, line limits, indexing, and agent mirrors that
//     previously had no automated check at all: 15700.
// 074 unity-mcp adversarial-review fixes. The bridge server, both CLI entry
//     points, and the JSONC/dotenv/rollback edge cases were all unreachable
//     from the suite (64% line coverage), so a fake-relay harness now boots the
//     bridge on an ephemeral port and asserts auth, session reuse, DELETE
//     teardown, relay reaping, the session cap, and the 400/408/413 client-error
//     responses. +207 source lines (failure-safe rollback, JSONC parsing, body
//     timeout and limits, session cap, debug logging) and +767 test lines take
//     unity-mcp.mjs coverage from 64% to 93%. The suite is now larger than the
//     script it covers, which is the intended ratio for a network-facing server
//     that ships no other verification: 16700.
// 075 .llm harness adversarial-review fixes. Mutation testing showed the
//     harness suite was inert: 10 of 11 mutations (gutting validateFrontmatter,
//     dropping the name/directory match, making artifactDrifts or staleMirrors
//     return nothing) left it green, so every rule the harness advertised was
//     unenforced. +117 source lines (marker-scoped mirror reaping so a
//     hand-authored .claude/skills entry is never deleted, a DX_LLM_ROOT
//     fixture seam, the four missed spec rules, references/ split from
//     scripts/+assets/) and +504 test lines of temp-directory rule fixtures
//     now catch 18 of 18 mutations: 17300.
//
//     NOTE for whoever touches this number next: at 17300 this repository's JS
//     is no longer the "thin CI/docs support" the header above describes, and
//     unity-mcp.mjs plus its suite (2886 lines) are 17% of the total. The
//     bridge subcommand is the largest severable piece: if the host is expected
//     to run an external Unity MCP bridge, deleting it and its tests reclaims
//     roughly 1200 lines. Prefer that to another raise.
// 076 Regressions the fix rounds introduced, caught by a verification pass:
//     stripJsonComments re-scanned its accumulated output on every closing
//     bracket, which flattened V8's rope and made the pass quadratic (a 188 KB
//     config took 150 s; it now takes 6 ms), and pruneEmptyDirectories swept
//     both mirror roots instead of only what a reap emptied, deleting the
//     hand-authored scaffolding that the sibling marker-scoping fix exists to
//     protect. +37 source and +112 test lines, including a loose timing guard
//     that only a return to quadratic can breach: 17500.
// 077 Stop llms.txt auto-committing a date it restamped for no reason. The
//     generator rewrote "Last Updated" on every run, so the default-branch
//     auto-commit fired on any day the workflow ran: 1efb7326, the last
//     llms.txt commit on master, changed exactly one line -- the date -- and
//     still re-triggered the whole push-side workflow set (#330). The date now
//     survives a byte-identical regeneration and moves only when the content
//     does, so it finally means what it says. +21 source lines
//     (preserveUnchangedDate plus a shared LAST_UPDATED_LINE that replaces a
//     duplicated literal) and +36 test lines driving it through the CLI, so the
//     real write path is covered rather than the helper alone; both mutations
//     (unconditional restamp, preserve-through-a-content-change) fail the
//     suite. Offset by -1 line in the fetch-refspec guard, whose two merged
//     workflow entries collapsed to one, and the comment lines of this very
//     entry. Adversarial review then added the malformed-date guard and its
//     regression case: preserving a date the generator itself rejects took
//     update mode's ability to REPAIR one away, because the normalized
//     comparison erases the date, so a file whose only defect was a corrupt
//     date line looked identical to a fresh generation and update exited 1
//     while advertising itself as the fix: 17612.
// 078 Parser-backed workflow consistency and fork-execution security contracts.
//     Actionlint cannot enforce repository key ordering, permissions, concurrency,
//     timeouts, checkout credential persistence, or same-repository formatting
//     guards, so issue #379 adds those checks to the existing workflow suite: 17710.
const TOTAL_BUDGET = 17710;
const LARGEST_FILE_COUNT = 10;
const REPO_ROOT = path.resolve(__dirname, "..");

function countLines(filePath) {
  const text = fs.readFileSync(filePath, "utf8");
  if (text.length === 0) {
    return 0;
  }
  const lines = text.split("\n").length;
  return text.endsWith("\n") ? lines - 1 : lines;
}

function main() {
  const output = execFileSync("git", ["ls-files", "*.js", "*.cjs", "*.mjs"], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });
  const files = output.split("\n").filter(Boolean);
  let total = 0;
  const counts = [];
  for (const file of files) {
    const lines = countLines(path.join(REPO_ROOT, file));
    total += lines;
    counts.push({ file, lines });
  }
  if (total > TOTAL_BUDGET) {
    const largest = counts
      .sort((a, b) => b.lines - a.lines || a.file.localeCompare(b.file))
      .slice(0, LARGEST_FILE_COUNT)
      .map(({ file, lines }) => `  ${lines.toString().padStart(5)} ${file}`)
      .join("\n");
    console.error(
      `validate-js-loc-budget: tracked JS is ${total} lines across ${files.length} files; ` +
        `budget is ${TOTAL_BUDGET} (${total - TOTAL_BUDGET} over). ` +
        "Delete or slim JS instead of raising the budget.\n" +
        `Largest tracked JS files:\n${largest}`
    );
    process.exit(1);
  }
  console.log(
    `validate-js-loc-budget: OK (${total}/${TOTAL_BUDGET} lines across ${files.length} files).`
  );
}

main();
