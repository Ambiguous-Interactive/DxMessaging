# Session 185 -- IL2CPP-only PR benchmarks

Date: 2026-08-02
Branch: `dev/wallstop/session-185-il2cpp-only-pr-benchmarks`
PR: [#342](https://github.com/Ambiguous-Interactive/DxMessaging/pull/342)
Issue: **#341**

## Outcome

This session removed the serialized PlayMode Mono cell from the automatic
Performance Numbers workflow. Eligible pull requests and default-branch pushes
now run one Standalone IL2CPP x64 Release player, the only published scope and
the backend shipped games run.

Ordinary PR reports still compare the measured head with the committed current
master baseline. Workflow-file changes no longer suppress the comparison by path
alone; direct benchmark or harness changes remain non-comparable. The existing
exact platform, scenario-set, and commit-stamp checks remain fail-closed. The
report states the goodness-normalized sign convention directly: `+` means better
and `-` means worse for both throughput and wall-clock rows.

The raw TargetMap report now reads only the Standalone `player.log`, rejects
duplicate identities, and requires the exact expected identity set. The previous
cross-leg identity comparison and PlayMode allocation-table checks were removed
with the leg they guarded.

## Contract updates

The performance user page, methodology runbook, CI settings guide, pull request
template, and canonical benchmark/IL2CPP skills now describe the single published
Standalone leg. The renderer still supports PlayMode and EditMode input for local
and manually dispatched work. Allocation evidence remains available through those
editor scopes and the exact-zero `AllocationMatrixTests` contract; the automatic
PR performance comment no longer publishes Mono allocation data.

## Validation

- Full Node.js script suite: **406 passed / 0 failed**.
- Focused performance renderer suite: **28 passed / 0 failed**.
- Unity PR-policy validator passed, including extracted shell fixtures for
  complete, missing, duplicate, and unexpected Standalone TargetMap evidence.
- `npm run validate:all` passed after preserving the exact 17,612-line JavaScript
  budget.
- Prettier, spelling, markdownlint, package validation, analyzer reproducibility,
  generated skill mirrors, and `git diff --check` passed.
- Unity MCP host refresh compiled successfully on Unity 6000.4.6f1; the
  `DxMcpTestRunner` bridge was loaded and idle.
- `PerfRegressionGateMatchingTests`: **11 passed / 0 failed** in EditMode through
  the Unity MCP bridge (0.61 seconds).
- Repeated adversarial review of the final base-aware diff reached **zero
  findings**.

## Live evidence

The post-merge performance run for #340 demonstrated the scheduling cost that
#341 removes: its Standalone cell completed successfully while the serialized
PlayMode cell remained queued behind the correctness matrix. Because session 185
also corrects a stale benchmark-source comment, its own report is deliberately
non-comparable under the fail-closed harness-path rule. Before completion, it must
show one Standalone benchmark cell, an explicit sign-direction legend, all CI
checks green, and zero unresolved review feedback. The next ordinary PR must
demonstrate the historical delta against the refreshed master baseline.

The first published head explicitly requested Cursor Bugbot and GitHub Copilot
reviews by tagged comment, requested Copilot through the reviewer API, and tagged
the sole repository collaborator for human review.
