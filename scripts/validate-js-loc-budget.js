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
// 079 package.json `_upm.changelog` mirror (#362). The Unity Package Manager
//     renders the Version History changelog from the resolved package's own
//     package.json, and `npm publish` strips every `_`-prefixed key from the
//     published metadata, so shipping the field in the manifest is the only
//     path that reaches every install route. 134 source + 180 test lines for a
//     generator whose `--check` gate is the only thing that would catch the
//     field going stale against CHANGELOG.md, including the regression that
//     keeps that gate from fighting `format:check` over package.json's
//     formatting: 18040.
// 080 Stop `unity:mcp:probe` reporting green through a window where nothing
//     editor-backed works (#418). The relay keeps advertising its whole tool
//     registry after the editor's discovery record goes stale, so a probe that
//     only reads `tools/list` said the loop was ready while every editor-backed
//     call answered "Unity not detected" -- the one failure this probe exists
//     to catch, and the only one it could not see. The probe now asks the
//     editor for its state and reports what it actually proved; a relay with no
//     editor tool keeps the tools-level verdict rather than turning into a
//     false red. +29 source and +59 test lines, the tests covering a live
//     editor, a stale discovery record, a reply carrying no editor state, an
//     empty reply, and both no-call paths: 18181.
// 081 Make the suite wall clock visible in the job summary (#410). A change
//     added 78 seconds to the EditMode step on every editor leg and stayed
//     green for two days, because nothing in CI reads how long a step takes.
//     `SuiteWallClockBudgetTest` already measures the suite and already warns
//     past its soft budget, into a log nobody opens on a green run. The CI
//     harness now lifts that one line into the job summary and warns when it is
//     over. +168 test lines covering under and over budget, the once-per-job
//     table header, a log with no line, a missing log, and the shared line
//     shape between the C# producer and the PowerShell consumer. No new script
//     and no new workflow, which is the option issue #410 asks for, plus the
//     11 lines this entry itself adds. Review then moved the once-per-job header
//     check into the summary FILE, because the workflow runs the harness once
//     per test mode, so an in-process flag printed the header three times per
//     job; the test now spawns one process per leg: 18367.
// 082 dependabot build-lock ignore contract. Dependabot bumps of the
//     first-party lock actions broke the copyable-example doc contract on
//     arrival (PR #447 failed eight checks; the 2026-08-18 bump needed a
//     silent manual docs realignment), so dependabot.yml now ignores those
//     actions and a new ci-aggregate-workflow test enforces the ignore list,
//     converting every future bump into one manual commit across workflows
//     and docs, plus the 7 lines this entry itself adds: 18398.
// 083 Fail closed on confounded three-run performance attribution. A 577-line
//     reducer validates the manifest, canonical roster, source-tree provenance,
//     profile, protocol, retained cycles, spreads, sentinels, and normalized
//     target and affected rows. Its 801-line suite covers both bracket orders,
//     CLI exit states, malformed evidence, non-finite arithmetic, and the exact
//     PR #468 ratios. Workflow and PowerShell tests pin manifest and source-tree
//     provenance into each authoritative summary: 19780.
// 084 Close a confirmed credential leak and add content-addressed evidence.
//     Unity writes its license serial into unity.log and configure.log, and
//     this repository is public, so every Unity artifact published the serial
//     for 14 days. credential-patterns.js (107) is the one pattern list;
//     redact-unity-artifacts.js (164) scrubs each artifact tree before upload
//     and its suite (343) mutation-proves all seven patterns, idempotence, and
//     the binary and false-positive paths; unity-artifact-redaction.test.js
//     (181) asserts the invariant, so a future workflow cannot upload a Unity
//     directory that was never scrubbed. perf-evidence-bundle.js (435) plus
//     perf-evidence-reducers.js (183) and their suite (444) seal, verify, and
//     replay #508 evidence bundles and refuse to seal credential material as a
//     backstop. Verified against a real 441-file CI artifact: 256 leaked
//     occurrences removed, then sealed and replayed. The devcontainer agent-CLI
//     suite also moved from grepping shell source to executing the installer
//     against a stub registry (+188). That is 1857 lines of new tested tooling
//     for a leak that had no detection at all, plus 46 lines across the mcp
//     configurator and its suite: 21980.
const TOTAL_BUDGET = 21980;
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
