---
name: test-diagnostics
description: "Making an opaque test failure explain itself: diagnostic collector classes that record an execution trace and render a BuildReport on failure, an EditorDiagnostics static with an Enabled flag plus NameFilter for zero-overhead-when-off logging, enabling and restoring diagnostics per fixture in SetUp/TearDown, and UNH-SUPPRESS markers for intentional edge cases. Use when an assertion only reports expected-vs-actual counts, when debugging spatial queries, state machines, or other multi-step algorithms, or when adding temporary logging to a test."
metadata:
  category: "testing"
  tags: "testing, diagnostics, debugging, logging, investigation"
---

# Test Diagnostics

When a stack trace and an expected-versus-actual count are not enough, add a diagnostic collector that records what the code under test actually did and renders it into the failure message.

## When to use

- An assertion fails with "Expected 5, got 3" and nothing explains which items were missing.
- Debugging an algorithm with internal steps: spatial queries, pathfinding, state machines, dispatch pipelines.
- Adding logging to a component temporarily, without leaving noise in normal test output.
- Marking a test that intentionally does something a linter or reviewer would flag.

## Rules

### Diagnostic collectors

- A collector is a plain class that the code under test writes into through an injected logger interface (for example `tree.SetQueryLogger(diagnostics)`). It records structured events into pre-sized `List<T>` fields and clears them at the start of each operation.
- Expose one `BuildReport(...)` method that returns a string. Include the inputs, the expected and actual counts, the MISSING set with per-item context, the EXTRA set, an aggregate summary of internal steps, and a sample of individual steps.
- Cap the report with a `maxItems` parameter (default 32) and print an ellipsis when truncating. Never collect or print unbounded data.
- Call it only on the failure path: compare first, and when the comparison disagrees call `Assert.Fail($"...\n\n{report}")`; keep the plain assertion afterward so the test still fails when the guard is edited away.

### Toggleable logging

- The `EditorDiagnostics` static exposes `Enabled` (default `false`) and `NameFilter` (null means all). Every entry point calls a private `ShouldLog(name)` gate and returns before any string interpolation, so disabled diagnostics cost nothing; enabled logging costs about 1 us per call.
- `NameFilter` matching is `IndexOf(..., StringComparison.OrdinalIgnoreCase)`, so it is a case-insensitive substring test, and a null name never matches a non-empty filter.
- Provide the shaped helpers rather than raw `Debug.Log`: `Log`, `LogFormat`, `LogStateChange(name, from, to)`, `LogMethodEntry`, `LogMethodExit`. Every line carries a consistent `[Diagnostics] [<name>]` prefix so log filtering works.
- A fixture that enables diagnostics sets `EditorDiagnostics.Enabled` and `NameFilter` in `CommonSetUp` and MUST restore both (`false` and `null`) in `TearDown` before calling the base method. Diagnostics are never left enabled.

### Suppression markers

- Mark deliberate edge-case code with a `// UNH-SUPPRESS: <reason>` comment on the line above: intentional `DestroyImmediate` in a test, deliberate null access, exception provocation.
- The reason is mandatory. A bare suppression is indistinguishable from an oversight.

## References

| Document                                                                  | Purpose                                                                                                                        |
| ------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------ |
| [test-diagnostics-patterns.md](./references/test-diagnostics-patterns.md) | The `EditorDiagnostics` toggleable-logging implementation and the `UNH-SUPPRESS` marker convention.                            |
| [test-diagnostics-usage.md](./references/test-diagnostics-usage.md)       | Wiring a collector into a test, enabling and restoring diagnostics per fixture, performance figures, and a collector template. |
| [test-diagnostics.md](./references/test-diagnostics.md)                   | The unhelpful-failure problem and a full diagnostic collector with a worked `BuildReport`.                                     |
