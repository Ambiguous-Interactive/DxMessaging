---
name: data-driven-tests
description: "NUnit parameterized test mechanics: choosing between [TestCase], [TestCaseSource], and [ValueSource]; building TestCaseData sources as static methods, static properties, or shared external data classes; naming cases with SetName; stacking multiple sources on one method; and writing failure messages that print every parameter. Use when several test methods differ only by input data, when adding cases to an existing parameterized test, or when a data-driven failure does not say which case broke."
metadata:
  category: "testing"
  tags: "testing, parameterized, data-driven, nunit, test-cases"
---

# Data-Driven Tests

Parameterized tests separate test logic from test data so one method covers many inputs. Adding a case becomes a one-line change instead of a copy-pasted method.

## When to use

- Two or more test methods share a body and differ only in inputs or expected values.
- Expanding boundary or edge coverage (null, empty, whitespace, min, max) on an existing test.
- Sharing the same case set across several fixtures.
- A parameterized test fails and the report does not identify which case.

## Rules

### Choosing the attribute

- `[TestCase(args...)]` for a small set of inline literal cases (roughly 2-5). Stack the attributes above a single `[Test]` method.
- `[TestCaseSource(nameof(Source))]` when cases are numerous, need non-literal values (`Vector3`, `Color`, `Type`), or need explicit names. Do not use it for a single case; the reflection overhead is not worth it.
- `[ValueSource(typeof(Source), nameof(Source.Member))]` for a single parameter drawn from a shared set. DxMessaging dispatch tests use this form with `MessageScenarios`; see the `test-coverage-design` skill.
- Sources may be a static method returning `IEnumerable<TestCaseData>`, a static property with the same return type, or a static array of raw values consumed positionally.

### Writing sources

- Put shared case sets in a dedicated static data class (for example `ColorConversionTestData`) and reference them with `[TestCaseSource(typeof(TheData), nameof(TheData.Cases))]` so several fixtures reuse one definition.
- Call `.SetName("...")` on each `TestCaseData` so the runner reports a readable case identity instead of a positional argument dump.
- Group sources by intent: separate `HappyPathCases()`, `EdgeCases()`, and `ErrorCases()` methods, or split by feature. Multiple `[TestCaseSource]` attributes on one method union their cases, which is how invalid-low and invalid-high sets share a single rejection test.
- Keep case generation simple. A source that computes its own expectations can pass while the production code is wrong.

### Assertions

- Every assertion in a parameterized test must print all parameters in its failure message, including a `null` rendering such as `messageId ?? "null"`. Without it a failure names the method but not the input.
- Test cases are constructed once per run, and execution cost is the same as a plain test; only discovery pays a small reflection cost.

## References

| Document                                                                  | Purpose                                                                                                                 |
| ------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| [data-driven-tests-sources.md](./references/data-driven-tests-sources.md) | `TestCaseSource` forms: static methods, static properties, shared external data classes, and stacking multiple sources. |
| [data-driven-tests-usage.md](./references/data-driven-tests-usage.md)     | Failure-message content and `SetName` conventions for readable case identities.                                         |
| [data-driven-tests.md](./references/data-driven-tests.md)                 | The duplication problem, `[TestCase]` basics, performance notes, and the do/don't list.                                 |
