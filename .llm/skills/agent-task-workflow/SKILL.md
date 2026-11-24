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

## Session and review

1. Record the start time. Resume the open agent PR, including a draft from an earlier session.
   Check default-branch regressions and findings on merged PRs before new backlog work.
   A different contributor's independent PR does not occupy the slot.
1. Choose one testable outcome. Combine issues with the same cause and acceptance evidence.
   Close an evidence-only investigation directly when its criteria are met, including a
   demonstrated negative result. Preserve the verdict and limitations on the issue.
1. After practical local checks, open one PR. Repair failures at their cause. Verify all
   applicable checks and the three required contexts named in the repository guidelines on the
   current revision. Require completed automated reviews. Scan inline threads, review bodies
   and discussion, including out-of-diff findings. Fix or record a reasoned disposition for
   each actionable finding. Recheck review and CI evidence after every push.
1. Confirmed CodeRabbit throttling on the current head is an exception for that review only.
   Record the head, explicit limit notice, full feedback scan and prior finding dispositions.
   Pending or absent review alone is insufficient. Honor GitHub blocking review decisions.
1. Before merging, check the current head, base, merge eligibility and pending decisions.
   Use a normal protected merge bound to the verified head. Enable native auto-merge only if
   every prerequisite, including review, is enforced when it fires. Never use the maintenance
   App's bypass. Verify the merge SHA and linked issue closures before selecting the next task.
1. Budget elapsed time, including waits. Reserve time for the task, local checks, a
   representative CI and review cycle, and one repair. Use recent comparable lane timings.
   At three hours finish only the active PR and record why it overran. At an external
   blocker, hand off the PR head, evidence, blocker and next action. Resume it next session.

## Maintainer review and rollout

Use merged PRs as the human review queue. Record wallstop's last reviewed revision and the
outstanding compare range in [#575](https://github.com/Ambiguous-Interactive/DxMessaging/issues/575).
Advance the revision only on human attestation. Link each actionable finding to a new or
reopened issue. Give a default-branch regression priority for repair or revert.

Before enabling ordinary autonomous merges, verify a distinct agent identity, effective
branch protections, ordinary merge permission without bypass, and the protected release
environment. Until verified, prepare changes for human merge. Obtain advance human approval
for changes that weaken controls or grant publishing authority. For the 4.0.0 release PR,
tag, manual dispatch, registry publication and Store submission, follow the
[release approval procedure](../../../docs/ops/npm-release-publishing.md#release-approval).

For the first ten enabled sessions, record start/end time, overruns, blockers, merged PRs,
issues opened/closed/reopened, time from green and reviewed to merge, CI retries, reverts
and post-merge findings. Use GitHub timestamps and existing session records. Mark missing
clocks unmeasured. Compare like task types and report sample sizes and fraction within three
hours. Investigate each overrun. Suspend autonomous merges on a bypassed gate or an unguarded
launch path.

## Optional coordinator

Use one owner for a coupled change. A coordinator may assign independent work only when the
session authorizes delegation. Default pilot limit: two implementation workers, no nested delegation.
Give each worker its own worktree, fixed base revision, disjoint allowed files, checks, and stop rule.
Workers return a commit, evidence and unresolved findings. They do not push or operate shared Unity.

The coordinator owns integration, the active PR, combined verification and exclusive Unity
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
changes, why they help, measured results and remaining limitations. Report the actual open issue
count and external inputs. Keep #575 open until its human and technical criteria pass.
`GOAL.md`, when present, adds maintainer-local criteria; it is git-ignored and absent from fresh checkouts.
