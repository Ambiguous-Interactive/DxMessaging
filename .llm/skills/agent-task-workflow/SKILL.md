---
name: agent-task-workflow
description: "Select focused local checks, implement a measured task, resume a handoff, or coordinate independent implementation workers in DxMessaging. Use for development-loop and coordinator decisions; Unity execution and licensing still follow their dedicated skills."
metadata:
  category: workflow
---

# Agent task workflow

Start from the requested outcome and its relevant open issue. Read recent decisions for that
subsystem rather than replaying every session. Keep one active implementation task per owner.

## Task contract

Record the question, source revision, baseline, hypothesis, RED observation, GREEN checks, stop
rule, and evidence location. Reproduce a behavioral defect before fixing it. For tooling changes,
exercise the actual command or classifier with normal, negative, and edge inputs.

Run the affected test file or fixture while editing. Run the affected broader lane once before
delivery; reserve full-repository audits for changes to their inputs or release qualification.
Keep failed and unfavorable observations. A passing replay does not explain an earlier failure.
Use existing reports, profilers and test runners; do not add a custom orchestration service.

[The iteration runbook](../../../docs/runbooks/iteration-speed.md) maps commands, lanes, evidence,
and measured costs. [Coding conventions](./references/repository-conventions.md) carries the
language, test and documentation rules; read only the relevant sections.

## Optional coordinator

Use one owner for a coupled change. A coordinator may assign independent work only when the
session authorizes delegation. Default pilot limit: two implementation workers, no nested delegation.
Give each worker its own worktree, fixed base revision, disjoint allowed files, checks, and stop rule.
Workers return a commit, evidence and unresolved findings. They do not push or operate shared Unity.

The coordinator owns integration, the session's one PR, combined verification and exclusive Unity
access. Serialize conflicting files and all host/editor changes. Never switch or project the shared
checkout while another owner is using its editor. Batch independent read-only tool calls.

Keep one short handoff: revision, active task, next action, worker ownership, live processes/editor
run GUID, evidence paths, and exact blockers. Keep historical narration in ignored progress records.

Evaluate coordination with three matched task pairs, alternate assignment order, and compare
verified completion time, total agent effort, conflicts and rework. Adopt for independent tasks only
if median time improves at least 20%, effort increases at most 25%, and checks remain equivalent.
One successful coordinated session is feasibility evidence, not that evaluation.

## Completion

Verify selected versus executed cases and preserve the source revision for each result. Report
changes, why they help, measured results and remaining limitations. Publish one prepared PR only
after practical local checks. Human acceptance and release-readiness remain separate gates.
`GOAL.md`, when present, adds maintainer-local criteria; it is git-ignored and absent from fresh checkouts.
