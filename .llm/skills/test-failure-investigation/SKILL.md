---
name: test-failure-investigation
description: "The zero-flaky policy and the eight-step procedure for a failing or intermittent test: reproduce it in a loop, state the expected behavior, capture actual state, read the production code path, categorize the root cause (production bug, test bug, setup bug, order dependency, timing, environment), fix the cause, re-run repeatedly, and document the finding. Includes the banned fixes - Thread.Sleep, WaitForSeconds, [Ignore], retry loops, swallowed try/catch, raised timeouts, deleting the test. Use when a test fails, is flaky, passes locally but fails in CI, or passes alone but fails with the suite."
metadata:
  category: "testing"
  tags: "testing, investigation, debugging, quality, zero-flaky, root-cause-analysis"
---

# Test Failure Investigation

This project has a zero-flaky-test policy. Every failure is treated as a production bug until proven otherwise, and no fix lands before the root cause is identified.

## When to use

- Any test fails, in CI or locally.
- A test is intermittent, or passes locally and fails in CI.
- A test passes alone and fails as part of the suite, or only in one of PlayMode / EditMode.
- Reviewing a PR whose diff changes a test to make it pass.

## Rules

### Core policy

- All test failures indicate bugs - in production code or in test code. Both require a real fix.
- Never "make the test pass" without understanding why it failed.
- `[Ignore]`, `[Skip]`, commenting a test out, and deleting a failing test are all prohibited. They hide the bug and lose the coverage.
- Findings become institutional knowledge: record edge cases in an XML `<remarks>` block on the test or in the relevant doc.

### The procedure

1. **Reproduce.** Run the single test in a loop (50-100 iterations) and confirm the failure is real and its rate is known.
1. **State the expected behavior.** Write down the production behavior under test, its preconditions, the expected outcome, and why it matters to a user.
1. **Capture actual state.** Add diagnostic output at the failure point covering the values on both sides of the assertion plus surrounding bus/registration state. See the `test-diagnostics` skill.
1. **Read the production path.** Walk the code the test exercises: initialization order and assumptions, shared and static state, edge cases the test triggers.
1. **Categorize the root cause** as production bug, test bug, test setup bug, order dependency, timing issue, or environment issue. The category determines the fix.
1. **Fix the cause, not the symptom.** A wrong expectation gets a corrected assertion with a message stating the contract; a real defect gets a production fix.
1. **Verify.** Re-run the fixed test at least 10 times (50 for a former flake), then related tests, then the full suite before committing.
1. **Document.** Add a dated `<remarks>` note naming what was wrong and what the behavior now guarantees.

### Root-cause signatures

| Symptom                                    | Likely cause     | Where to look                                                               |
| ------------------------------------------ | ---------------- | --------------------------------------------------------------------------- |
| Passes locally, fails in CI                | Timing or race   | Async work without synchronization; callbacks firing after the assertion    |
| Passes alone, fails with the suite         | Shared state     | Static state not reset; fixture state built in `[OneTimeSetUp]` and mutated |
| Fails when the order changes               | Order dependency | A test relying on another test's side effects                               |
| Fails only in PlayMode or only in EditMode | Unity lifecycle  | Missing `yield return null` before asserting on `Start`/`OnEnable` effects  |

### Banned "fixes"

`Thread.Sleep` or `WaitForSeconds` to paper over a race; `[Ignore]` on a failing test; a retry loop around the assertion; `try`/`catch` that swallows the exception; raising a timeout; "works on my machine"; deleting the test. Each masks the defect and slows or weakens the suite. Fix the race, the shared state, or the expectation instead.

### Checklist before opening the PR

Can I reproduce it reliably? Do I know what behavior the test verifies? Have I inspected actual versus expected? Have I read the production path? Is this a production bug or a test bug? Is shared state or ordering involved? Is there a timing issue? Does the fix address the cause? Have I re-run it repeatedly?

## References

| Document                                                                                            | Purpose                                                                                                                               |
| --------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------- |
| [test-failure-investigation-procedure.md](./references/test-failure-investigation-procedure.md)     | The eight investigation steps in full, with the root-cause category table and reproduce/verify loop commands.                         |
| [test-failure-investigation-root-causes.md](./references/test-failure-investigation-root-causes.md) | Symptom-to-cause signatures for timing, shared state, ordering, and Unity-specific failures, plus the banned-fix table and checklist. |
| [test-failure-investigation.md](./references/test-failure-investigation.md)                         | The zero-flaky policy, its five core principles, and the high-level investigation flow.                                               |
