---
name: plan-maintenance
description: "Keep PLAN.md as a short routing page for active and future work. Use when creating, reviewing, updating, or resuming work from PLAN.md, especially when completed history, copied issue detail, research notes, or protocol text starts accumulating there."
metadata:
  category: "workflow"
  tags: "planning, task-management, context, progress"
---

# Plan Maintenance

Keep `PLAN.md` disposable and current. It answers only: what can start next, what follows, and what
external condition blocks a task. GitHub issues remain the durable tracker.

## Content boundary

Keep only:

- the active task and its next observable outcome;
- a short, ordered queue of future tasks;
- blocked tasks with the exact condition that makes them ready; and
- links to the canonical issue, runbook, skill, or context file.

Move everything else to its owner:

| Content                         | Owner                                                  |
| ------------------------------- | ------------------------------------------------------ |
| Completed work and session data | Relevant issue; `progress/session-*.md` supplements it |
| Requirements and task checklist | GitHub issue                                           |
| Reusable agent rules            | `.llm/context.md` or a focused `.llm/skills/` file     |
| Protocols and operating details | Focused skill reference or `docs/runbooks/`            |
| Decisions and negative results  | Decision ledger, progress record, and issue            |
| Raw evidence                    | The repository's designated evidence store             |

Do not copy session narratives, commit or check histories, benchmark tables, experiment catalogs,
research bibliographies, implementation notes, or completed checklists into `PLAN.md`.

## Task lifecycle

1. Confirm the linked issue is still open before selecting it.
1. Put the hypothesis, RED condition, GREEN checks, stop rule, and evidence location in the issue
   when issue writes are authorized and available. Otherwise, use the current progress record and
   keep a short pending-issue-sync item in `PLAN.md` with the exact access or authorization condition.
1. Keep at most one short status line for in-progress work in `PLAN.md`.
1. On completion, record the result and decision in the linked issue when issue writes are authorized
   and available. Otherwise, record them in the progress file and replace the completed task with a
   short pending-issue-sync item. Remove that item only after the durable issue is synchronized.
1. Promote the next ready task; do not append a session summary.

Keep `PLAN.md` below 100 lines. Treat that as a failure ceiling, not a target. If it approaches the
ceiling, delete duplication and move context to its canonical owner instead of compressing history
into denser prose.

When `PLAN.md` conflicts with a live issue, verify the issue and update the plan. Never preserve
stale plan text merely because it contains more detail.
