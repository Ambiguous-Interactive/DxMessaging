# Skills Index

> Skill catalog. The Title, Complexity, Status, Performance, and Tags columns are
> hand-authored. The Lines column (canonical `wc -l`) and the Summary /
> Table-of-Contents counts are generated from disk by
> `scripts/generate-skills-index.js`. When adding or removing a skill file, add
> or remove its row here in the same change, then run `npm run update:skills-index`
> to refresh the counts. `npm run check:skills-index` gates drift in `validate:all`.
> Lines markers: `[draft]` < 120, `[ok]` 120-260, `[warn]` > 260.

---

## Summary

| Metric       | Value |
| ------------ | ----- |
| Total Skills | 157   |
| Categories   | 8     |

---

## Table of Contents

- [Documentation](#documentation) (27)
- [GitHub Actions](#github-actions) (8)
- [Packaging](#packaging) (3)
- [Performance](#performance) (46)
- [Scripting](#scripting) (9)
- [Solid](#solid) (15)
- [Testing](#testing) (39)
- [Unity](#unity) (10)

---

## Documentation

| Skill                                                                                                 | Lines       | Complexity     | Status   | Performance  | Tags                              |
| ----------------------------------------------------------------------------------------------------- | ----------- | -------------- | -------- | ------------ | --------------------------------- |
| [ASCII-Only Documentation Policy](./documentation/ascii-only-docs.md)                                 | [ok] 164    | [basic]        | [stable] | [risk: none] | documentation, ascii              |
| [Changelog Entry Writing and Anti-Patterns](./documentation/changelog-entry-writing.md)               | [warn] 295  | [basic]        | [stable] | [risk: none] | changelog, release-notes          |
| [Changelog Entry Writing and Anti-Patterns Part 1](./documentation/changelog-entry-writing-part-1.md) | [draft] 55  | [intermediate] | [stable] | [risk: low]  | migration, split                  |
| [Changelog Management](./documentation/changelog-management.md)                                       | [ok] 228    | [basic]        | [stable] | [risk: none] | changelog, documentation          |
| [Changelog Release Workflow](./documentation/changelog-release-workflow.md)                           | [ok] 249    | [basic]        | [stable] | [risk: none] | changelog, release-workflow       |
| [Code Samples Must Compile](./documentation/code-samples-must-compile.md)                             | [ok] 132    | [basic]        | [stable] | [risk: none] | documentation, code-samples       |
| [Documentation Code Samples](./documentation/documentation-code-samples.md)                           | [ok] 212    | [basic]        | [stable] | [risk: none] | documentation, code-samples       |
| [Documentation Code Samples Part 1](./documentation/documentation-code-samples-part-1.md)             | [draft] 81  | [intermediate] | [stable] | [risk: low]  | migration, split                  |
| [Documentation Style Guide](./documentation/documentation-style-guide.md)                             | [ok] 203    | [basic]        | [stable] | [risk: none] | documentation, style              |
| [Documentation Update Workflow](./documentation/documentation-update-workflow.md)                     | [ok] 154    | [basic]        | [stable] | [risk: none] | documentation, workflow           |
| [Documentation Updates and Maintenance](./documentation/documentation-updates.md)                     | [ok] 148    | [basic]        | [stable] | [risk: none] | documentation, code-comments      |
| [External URL Fragment Validation](./documentation/external-url-fragment-validation.md)               | [ok] 190    | [basic]        | [stable] | [risk: none] | documentation, links              |
| [GitHub Actions Version Consistency](./documentation/github-actions-version-consistency.md)           | [ok] 207    | [basic]        | [stable] | [risk: none] | github-actions, ci-cd             |
| [Human-Prose Documentation Policy](./documentation/human-prose-policy.md)                             | [ok] 138    | [basic]        | [stable] | [risk: none] | documentation, prose              |
| [Link Quality and External URL Management](./documentation/link-quality-guidelines.md)                | [draft] 119 | [basic]        | [stable] | [risk: none] | documentation, links              |
| [Link Quality and External URL Management Part 1](./documentation/link-quality-guidelines-part-1.md)  | [ok] 205    | [intermediate] | [stable] | [risk: low]  | migration, split                  |
| [Link Quality and External URL Management Part 2](./documentation/link-quality-guidelines-part-2.md)  | [draft] 65  | [intermediate] | [stable] | [risk: low]  | migration, split                  |
| [Markdown Compatibility Guidelines](./documentation/markdown-compatibility.md)                        | [ok] 135    | [basic]        | [stable] | [risk: none] | documentation, markdown           |
| [Markdown Compatibility Guidelines Part 1](./documentation/markdown-compatibility-part-1.md)          | [ok] 201    | [intermediate] | [stable] | [risk: low]  | migration, split                  |
| [Markdown Compatibility Guidelines Part 2](./documentation/markdown-compatibility-part-2.md)          | [ok] 209    | [intermediate] | [stable] | [risk: low]  | migration, split                  |
| [Memory Reclamation Documentation Maintenance](./documentation/memory-reclamation-docs.md)            | [ok] 138    | [basic]        | [stable] | [risk: none] | documentation, memory-reclamation |
| [Mermaid Diagram Theming](./documentation/mermaid-theming.md)                                         | [ok] 198    | [intermediate] | [stable] | [risk: none] | documentation, mermaid            |
| [Mermaid Diagram Theming Part 1](./documentation/mermaid-theming-part-1.md)                           | [ok] 159    | [intermediate] | [stable] | [risk: low]  | migration, split                  |
| [MkDocs Navigation Management](./documentation/mkdocs-navigation.md)                                  | [ok] 251    | [basic]        | [stable] | [risk: none] | documentation, mkdocs             |
| [MkDocs Navigation Management Part 1](./documentation/mkdocs-navigation-part-1.md)                    | [draft] 70  | [intermediate] | [stable] | [risk: low]  | migration, split                  |
| [User vs Dev Perf Doc Separation](./documentation/user-vs-dev-perf-doc-separation.md)                 | [ok] 168    | [basic]        | [stable] | [risk: none] | documentation, performance        |
| [XML Documentation Standards](./documentation/documentation-xml-docs.md)                              | [ok] 190    | [basic]        | [stable] | [risk: none] | documentation, xml-docs           |

## GitHub Actions

| Skill                                                                                                  | Lines      | Complexity     | Status   | Performance    | Tags                    |
| ------------------------------------------------------------------------------------------------------ | ---------- | -------------- | -------- | -------------- | ----------------------- |
| [CI/CD Devcontainer Workflows](./github-actions/cicd-devcontainer-workflows.md)                        | [ok] 255   | [intermediate] | [stable] | [risk: high]   | github-actions, ci-cd   |
| [Git Renormalize Pattern Validation](./github-actions/git-renormalize-patterns.md)                     | [ok] 225   | [intermediate] | [stable] | [risk: low]    | github-actions, git     |
| [GitHub Actions Workflow Consistency](./github-actions/workflow-consistency.md)                        | [ok] 215   | [intermediate] | [stable] | [risk: medium] | github-actions, ci-cd   |
| [GitHub Actions Workflow Consistency Part 1](./github-actions/workflow-consistency-part-1.md)          | [ok] 243   | [intermediate] | [stable] | [risk: low]    | migration, split        |
| [Lychee Link Checker Configuration Management](./github-actions/lychee-configuration.md)               | [ok] 249   | [intermediate] | [stable] | [risk: low]    | github-actions, ci-cd   |
| [Lychee Link Checker Configuration Management Part 1](./github-actions/lychee-configuration-part-1.md) | [draft] 66 | [intermediate] | [stable] | [risk: low]    | migration, split        |
| [Release Asset and Notes Invariants](./github-actions/release-asset-and-notes-invariants.md)           | [ok] 134   | [intermediate] | [stable] | [risk: none]   | github-actions, release |
| [Unity Version Single Source of Truth](./github-actions/unity-version-single-source.md)                | [ok] 199   | [intermediate] | [stable] | [risk: low]    | github-actions, ci-cd   |

## Packaging

| Skill                                                                                       | Lines       | Complexity     | Status   | Performance | Tags             |
| ------------------------------------------------------------------------------------------- | ----------- | -------------- | -------- | ----------- | ---------------- |
| [npm Package Configuration](./packaging/npm-package-configuration.md)                       | [ok] 245    | [intermediate] | [stable] | [risk: low] | npm, packaging   |
| [npm Package Configuration Part 1](./packaging/npm-package-configuration-part-1.md)         | [draft] 109 | [intermediate] | [stable] | [risk: low] | migration, split |
| [Shipping a Roslyn Analyzer in a Unity UPM Package](./packaging/unity-analyzer-shipping.md) | [ok] 171    | [intermediate] | [stable] | [risk: low] | unity, analyzer  |

## Performance

| Skill                                                                                                              | Lines       | Complexity     | Status   | Performance      | Tags                    |
| ------------------------------------------------------------------------------------------------------------------ | ----------- | -------------- | -------- | ---------------- | ----------------------- |
| [AggressiveInlining for Hot Path Optimization](./performance/aggressive-inlining.md)                               | [draft] 108 | [intermediate] | [stable] | [risk: medium]   | performance, inlining   |
| [AggressiveInlining for Hot Path Optimization Part 1](./performance/aggressive-inlining-part-1.md)                 | [ok] 170    | [intermediate] | [stable] | [risk: low]      | migration, split        |
| [AggressiveInlining for Hot Path Optimization Part 2](./performance/aggressive-inlining-part-2.md)                 | [ok] 130    | [intermediate] | [stable] | [risk: low]      | migration, split        |
| [AggressiveInlining Performance Notes](./performance/aggressive-inlining-performance-notes.md)                     | [draft] 94  | [intermediate] | [stable] | [risk: medium]   | performance, inlining   |
| [Array Pooling Usage Examples](./performance/array-pooling-usage-examples.md)                                      | [ok] 145    | [intermediate] | [stable] | [risk: high]     | memory, allocation      |
| [Array Pooling with ArrayPool and Custom Pools](./performance/array-pooling.md)                                    | [ok] 120    | [intermediate] | [stable] | [risk: critical] | memory, allocation      |
| [Array Pooling with ArrayPool and Custom Pools Part 1](./performance/array-pooling-part-1.md)                      | [ok] 204    | [intermediate] | [stable] | [risk: low]      | migration, split        |
| [Array Pooling with ArrayPool and Custom Pools Part 2](./performance/array-pooling-part-2.md)                      | [draft] 56  | [intermediate] | [stable] | [risk: low]      | migration, split        |
| [Auto-Load Singleton Attribute](./performance/singleton-autoload.md)                                               | [draft] 111 | [intermediate] | [stable] | [risk: low]      | unity, singleton        |
| [Benchmark Methodology: Total Over One Window](./performance/benchmark-methodology-total-over-window.md)           | [warn] 419  | [intermediate] | [stable] | [risk: high]     | performance, benchmarks |
| [Cache Builder Configuration](./performance/cache-eviction-builder.md)                                             | [ok] 203    | [intermediate] | [stable] | [risk: high]     | caching, builder        |
| [Cache Eviction Implementation](./performance/cache-eviction-implementation.md)                                    | [draft] 90  | [advanced]     | [stable] | [risk: high]     | caching, eviction       |
| [Cache Eviction Implementation Part 1](./performance/cache-eviction-implementation-part-1.md)                      | [ok] 244    | [intermediate] | [stable] | [risk: low]      | migration, split        |
| [Cache Eviction Implementation Part 2](./performance/cache-eviction-implementation-part-2.md)                      | [draft] 42  | [intermediate] | [stable] | [risk: low]      | migration, split        |
| [Collection Pooling with RAII Pattern](./performance/collection-pooling.md)                                        | [draft] 118 | [intermediate] | [stable] | [risk: high]     | memory, allocation      |
| [Collection Pooling with RAII Pattern Part 1](./performance/collection-pooling-part-1.md)                          | [ok] 205    | [intermediate] | [stable] | [risk: low]      | migration, split        |
| [Collection Pooling with RAII Pattern Part 2](./performance/collection-pooling-part-2.md)                          | [draft] 56  | [intermediate] | [stable] | [risk: low]      | migration, split        |
| [DxMessaging Dispatch Hot Path](./performance/dispatch-hot-path.md)                                                | [warn] 296  | [advanced]     | [stable] | [risk: critical] | dispatch, hot-path      |
| [DxMessaging Memory Reclamation](./performance/memory-reclamation.md)                                              | [ok] 197    | [advanced]     | [stable] | [risk: critical] | memory, reclamation     |
| [DxMessaging Sweep Gate Must Be Cheap](./performance/sweep-gate-must-be-cheap.md)                                  | [ok] 198    | [advanced]     | [stable] | [risk: critical] | sweep, eviction         |
| [High-Performance Cache with Eviction Policies](./performance/cache-eviction-policies.md)                          | [ok] 176    | [advanced]     | [stable] | [risk: high]     | caching, memory         |
| [Mono vs IL2CPP Optimization Split](./performance/mono-vs-il2cpp-optimization-split.md)                            | [ok] 216    | [advanced]     | [stable] | [risk: high]     | performance, mono       |
| [Object Pooling Anti-Patterns](./performance/object-pooling-anti-patterns.md)                                      | [ok] 144    | [intermediate] | [stable] | [risk: high]     | memory, allocation      |
| [Object Pooling for Zero-Allocation Messaging](./performance/object-pooling.md)                                    | [ok] 123    | [intermediate] | [stable] | [risk: high]     | memory, allocation      |
| [Object Pooling for Zero-Allocation Messaging Part 1](./performance/object-pooling-part-1.md)                      | [ok] 190    | [intermediate] | [stable] | [risk: low]      | migration, split        |
| [Object Pooling for Zero-Allocation Messaging Part 2](./performance/object-pooling-part-2.md)                      | [draft] 69  | [intermediate] | [stable] | [risk: low]      | migration, split        |
| [Object Pooling Usage Examples](./performance/object-pooling-usage-examples.md)                                    | [draft] 114 | [intermediate] | [stable] | [risk: high]     | memory, allocation      |
| [Object Pooling Variations](./performance/object-pooling-variations.md)                                            | [ok] 147    | [intermediate] | [stable] | [risk: high]     | memory, allocation      |
| [Perf Config: IL2CPP Release, .NET Standard 2.1](./performance/perf-config-il2cpp-release-netstandard21.md)        | [ok] 225    | [intermediate] | [stable] | [risk: high]     | performance, benchmarks |
| [Readonly Struct Cached Hash Performance Notes](./performance/readonly-struct-cached-hash-performance-notes.md)    | [draft] 91  | [intermediate] | [stable] | [risk: high]     | performance, struct     |
| [Readonly Struct with Cached Hash for Dictionary Keys](./performance/readonly-struct-cached-hash.md)               | [ok] 127    | [intermediate] | [stable] | [risk: high]     | performance, struct     |
| [Readonly Struct with Cached Hash for Dictionary Keys Part 1](./performance/readonly-struct-cached-hash-part-1.md) | [ok] 170    | [intermediate] | [stable] | [risk: low]      | migration, split        |
| [Readonly Struct with Cached Hash for Dictionary Keys Part 2](./performance/readonly-struct-cached-hash-part-2.md) | [draft] 106 | [intermediate] | [stable] | [risk: low]      | migration, split        |
| [Runtime Singleton Pattern](./performance/singleton-runtime.md)                                                    | [ok] 187    | [intermediate] | [stable] | [risk: low]      | unity, singleton        |
| [RuntimeSingleton and ScriptableObject Singleton Patterns](./performance/singleton-patterns.md)                    | [ok] 165    | [intermediate] | [stable] | [risk: medium]   | unity, singleton        |
| [ScriptableObject Singleton Pattern](./performance/singleton-scriptableobject.md)                                  | [ok] 177    | [intermediate] | [stable] | [risk: low]      | unity, singleton        |
| [Serializable Dictionary for Unity Inspector](./performance/serializable-dictionary.md)                            | [ok] 240    | [intermediate] | [stable] | [risk: low]      | unity, serialization    |
| [Serializable Dictionary for Unity Inspector Part 1](./performance/serializable-dictionary-part-1.md)              | [draft] 72  | [intermediate] | [stable] | [risk: low]      | migration, split        |
| [Serializable Dictionary Property Drawer](./performance/serializable-dictionary-property-drawer.md)                | [ok] 147    | [intermediate] | [stable] | [risk: medium]   | unity, serialization    |
| [Serializable Dictionary Usage Examples](./performance/serializable-dictionary-usage-examples.md)                  | [ok] 135    | [intermediate] | [stable] | [risk: medium]   | unity, serialization    |
| [Singleton Usage Examples](./performance/singleton-usage-examples.md)                                              | [ok] 139    | [intermediate] | [stable] | [risk: low]      | unity, singleton        |
| [StringBuilder Pooling for Zero-Allocation String Building](./performance/stringbuilder-pooling.md)                | [ok] 198    | [basic]        | [stable] | [risk: high]     | memory, allocation      |
| [StringBuilder Pooling for Zero-Allocation String Building Part 1](./performance/stringbuilder-pooling-part-1.md)  | [ok] 151    | [intermediate] | [stable] | [risk: low]      | migration, split        |
| [WaitForSeconds and Yield Instruction Pooling](./performance/yield-instruction-pooling.md)                         | [draft] 103 | [basic]        | [stable] | [risk: high]     | performance, unity      |
| [WaitForSeconds and Yield Instruction Pooling Part 1](./performance/yield-instruction-pooling-part-1.md)           | [ok] 163    | [intermediate] | [stable] | [risk: low]      | migration, split        |
| [WaitForSeconds and Yield Instruction Pooling Part 2](./performance/yield-instruction-pooling-part-2.md)           | [ok] 147    | [intermediate] | [stable] | [risk: low]      | migration, split        |

## Scripting

| Skill                                                                                         | Lines       | Complexity     | Status   | Performance  | Tags                             |
| --------------------------------------------------------------------------------------------- | ----------- | -------------- | -------- | ------------ | -------------------------------- |
| [Cross-Platform Script Compatibility](./scripting/cross-platform-compatibility.md)            | [warn] 299  | [intermediate] | [stable] | [risk: none] | cross-platform, case-sensitivity |
| [PowerShell Scripting Best Practices](./scripting/powershell-best-practices.md)               | [draft] 105 | [intermediate] | [stable] | [risk: none] | powershell, scripting            |
| [PowerShell Scripting Best Practices Part 1](./scripting/powershell-best-practices-part-1.md) | [ok] 203    | [intermediate] | [stable] | [risk: low]  | migration, split                 |
| [PowerShell Scripting Best Practices Part 2](./scripting/powershell-best-practices-part-2.md) | [ok] 221    | [intermediate] | [stable] | [risk: low]  | migration, split                 |
| [Regex Pattern Documentation](./scripting/regex-documentation.md)                             | [ok] 149    | [intermediate] | [stable] | [risk: none] | regex, documentation             |
| [Regex Pattern Documentation Part 1](./scripting/regex-documentation-part-1.md)               | [ok] 192    | [intermediate] | [stable] | [risk: low]  | migration, split                 |
| [Regex Pattern Documentation Part 2](./scripting/regex-documentation-part-2.md)               | [ok] 179    | [intermediate] | [stable] | [risk: low]  | migration, split                 |
| [Shell Scripting Best Practices](./scripting/shell-best-practices.md)                         | [ok] 217    | [intermediate] | [stable] | [risk: none] | shell, bash                      |
| [Shell Scripting Best Practices Part 1](./scripting/shell-best-practices-part-1.md)           | [ok] 200    | [intermediate] | [stable] | [risk: low]  | migration, split                 |

## Solid

| Skill                                                                                              | Lines       | Complexity     | Status   | Performance    | Tags                    |
| -------------------------------------------------------------------------------------------------- | ----------- | -------------- | -------- | -------------- | ----------------------- |
| [Collection Extension Methods with Performance Documentation](./solid/collection-extensions.md)    | [ok] 144    | [intermediate] | [stable] | [risk: high]   | solid, extensions       |
| [Collection Extensions: Accessors](./solid/collection-extensions-accessors.md)                     | [ok] 229    | [intermediate] | [stable] | [risk: high]   | collections, extensions |
| [Collection Extensions: Shuffle](./solid/collection-extensions-shuffle.md)                         | [ok] 144    | [basic]        | [stable] | [risk: medium] | collections, shuffle    |
| [Collection Extensions: Type Specialization](./solid/collection-extensions-type-specialization.md) | [ok] 168    | [intermediate] | [stable] | [risk: high]   | collections, extensions |
| [Fluent Builder Pattern with Struct Builders](./solid/fluent-builder-pattern.md)                   | [draft] 111 | [intermediate] | [stable] | [risk: medium] | solid, patterns         |
| [Fluent Builder Pattern with Struct Builders Part 1](./solid/fluent-builder-pattern-part-1.md)     | [ok] 202    | [intermediate] | [stable] | [risk: low]    | migration, split        |
| [Fluent Builder Pattern with Struct Builders Part 2](./solid/fluent-builder-pattern-part-2.md)     | [draft] 75  | [intermediate] | [stable] | [risk: low]    | migration, split        |
| [Fluent Builder Templates and Factories](./solid/fluent-builder-pattern-templates.md)              | [draft] 117 | [intermediate] | [stable] | [risk: medium] | solid, patterns         |
| [Fluent Builder Usage Examples](./solid/fluent-builder-pattern-usage-examples.md)                  | [ok] 128    | [intermediate] | [stable] | [risk: medium] | solid, patterns         |
| [IEquatable Implementation for Value Types](./solid/iequatable-implementation.md)                  | [ok] 239    | [intermediate] | [stable] | [risk: high]   | solid, performance      |
| [IEquatable Implementation Variants](./solid/iequatable-implementation-variants.md)                | [ok] 201    | [intermediate] | [stable] | [risk: medium] | solid, performance      |
| [IEquatable Usage Examples](./solid/iequatable-implementation-usage.md)                            | [draft] 111 | [intermediate] | [stable] | [risk: medium] | solid, performance      |
| [Try-Pattern API Usage Examples](./solid/try-pattern-apis-usage.md)                                | [ok] 136    | [basic]        | [stable] | [risk: medium] | solid, defensive        |
| [Try-Pattern API Variants](./solid/try-pattern-apis-variants.md)                                   | [ok] 233    | [basic]        | [stable] | [risk: medium] | solid, defensive        |
| [Try-Pattern APIs for Defensive Programming](./solid/try-pattern-apis.md)                          | [ok] 220    | [basic]        | [stable] | [risk: high]   | solid, defensive        |

## Testing

| Skill                                                                                                   | Lines       | Complexity     | Status   | Performance      | Tags                         |
| ------------------------------------------------------------------------------------------------------- | ----------- | -------------- | -------- | ---------------- | ---------------------------- |
| [Allocation Coverage Required for Dispatch](./testing/allocation-coverage-required-for-dispatch.md)     | [warn] 351  | [intermediate] | [stable] | [risk: critical] | testing, allocation          |
| [Benchmarks Run in the Highest-Fidelity Scope](./testing/benchmarks-run-in-highest-fidelity-scope.md)   | [ok] 183    | [intermediate] | [stable] | [risk: medium]   | testing, benchmarks          |
| [Comparison Parity and Package Single Source](./testing/comparison-parity-and-package-single-source.md) | [warn] 304  | [advanced]     | [stable] | [risk: medium]   | testing, benchmarks          |
| [Data-Driven Coverage Patterns](./testing/test-coverage-data-driven.md)                                 | [ok] 172    | [intermediate] | [stable] | [risk: none]     | testing, data-driven         |
| [Data-Driven Test Sources](./testing/data-driven-tests-sources.md)                                      | [ok] 255    | [intermediate] | [stable] | [risk: none]     | testing, parameterized       |
| [Data-Driven Test Usage Patterns](./testing/data-driven-tests-usage.md)                                 | [draft] 107 | [intermediate] | [stable] | [risk: none]     | testing, parameterized       |
| [Data-Driven Tests with TestCaseSource](./testing/data-driven-tests.md)                                 | [ok] 196    | [intermediate] | [stable] | [risk: low]      | testing, parameterized       |
| [Fast Unity Tests: Reload, Frame Tax, and Anti-Patterns](./testing/fast-unity-tests.md)                 | [warn] 265  | [intermediate] | [stable] | [risk: none]     | testing, performance         |
| [Git and Parser Robustness in CI/CD](./testing/git-workflow-robustness.md)                              | [ok] 213    | [intermediate] | [stable] | [risk: none]     | testing, git                 |
| [Git and Parser Robustness in CI/CD Part 1](./testing/git-workflow-robustness-part-1.md)                | [ok] 187    | [intermediate] | [stable] | [risk: low]      | migration, split             |
| [Inspector Overlay Invariants for MessageAwareComponent](./testing/inspector-overlay-invariants.md)     | [ok] 152    | [intermediate] | [stable] | [risk: low]      | testing, editor              |
| [LeakWatcher: Detecting Registration Leaks in Tests](./testing/leak-watcher-usage.md)                   | [ok] 259    | [basic]        | [stable] | [risk: low]      | testing, leaks               |
| [Lifecycle Edge-Case Test Coverage](./testing/lifecycle-edge-coverage.md)                               | [ok] 248    | [intermediate] | [stable] | [risk: none]     | testing, lifecycle           |
| [Memory Reclaim Coverage](./testing/memory-reclaim-coverage.md)                                         | [ok] 183    | [intermediate] | [stable] | [risk: high]     | testing, memory              |
| [Shared Fixtures: Generic Base](./testing/shared-test-fixtures-generic-base.md)                         | [ok] 185    | [advanced]     | [stable] | [risk: high]     | testing, fixtures            |
| [Shared Fixtures: Reference Counting](./testing/shared-test-fixtures-reference-counting.md)             | [ok] 252    | [advanced]     | [stable] | [risk: high]     | testing, fixtures            |
| [Shared Test Fixtures with Reference Counting](./testing/shared-test-fixtures.md)                       | [ok] 165    | [advanced]     | [stable] | [risk: high]     | testing, fixtures            |
| [Single Thread Contract](./testing/single-thread-contract.md)                                           | [ok] 200    | [intermediate] | [stable] | [risk: high]     | testing, concurrency         |
| [Test Base Class Cleanup Usage](./testing/test-base-class-cleanup-usage.md)                             | [ok] 218    | [intermediate] | [stable] | [risk: low]      | testing, cleanup             |
| [Test Base Class with Automatic Resource Cleanup](./testing/test-base-class-cleanup.md)                 | [ok] 124    | [intermediate] | [stable] | [risk: low]      | testing, cleanup             |
| [Test Base Class with Automatic Resource Cleanup Part 1](./testing/test-base-class-cleanup-part-1.md)   | [ok] 231    | [intermediate] | [stable] | [risk: low]      | migration, split             |
| [Test Categories for Selective Execution](./testing/test-categories.md)                                 | [ok] 252    | [basic]        | [stable] | [risk: high]     | testing, organization        |
| [Test Categories for Selective Execution Part 1](./testing/test-categories-part-1.md)                   | [draft] 66  | [intermediate] | [stable] | [risk: low]      | migration, split             |
| [Test Category Execution](./testing/test-categories-execution.md)                                       | [ok] 142    | [basic]        | [stable] | [risk: none]     | testing, organization        |
| [Test Code Quality and Accuracy](./testing/test-code-quality.md)                                        | [ok] 241    | [intermediate] | [stable] | [risk: medium]   | testing, documentation       |
| [Test Coverage Requirements](./testing/comprehensive-test-coverage.md)                                  | [ok] 141    | [intermediate] | [stable] | [risk: none]     | testing, coverage            |
| [Test Coverage Scenario Categories](./testing/test-coverage-scenario-categories.md)                     | [ok] 223    | [intermediate] | [stable] | [risk: none]     | testing, coverage            |
| [Test Diagnostics and Investigation Patterns](./testing/test-diagnostics.md)                            | [ok] 247    | [intermediate] | [stable] | [risk: low]      | testing, diagnostics         |
| [Test Diagnostics Patterns](./testing/test-diagnostics-patterns.md)                                     | [ok] 189    | [intermediate] | [stable] | [risk: low]      | testing, diagnostics         |
| [Test Diagnostics Usage](./testing/test-diagnostics-usage.md)                                           | [ok] 196    | [intermediate] | [stable] | [risk: low]      | testing, diagnostics         |
| [Test Failure Investigation and Zero-Flaky Policy](./testing/test-failure-investigation.md)             | [draft] 119 | [intermediate] | [stable] | [risk: none]     | testing, investigation       |
| [Test Failure Investigation Procedure](./testing/test-failure-investigation-procedure.md)               | [ok] 216    | [intermediate] | [stable] | [risk: none]     | testing, investigation       |
| [Test Failure Root Causes and Anti-Patterns](./testing/test-failure-investigation-root-causes.md)       | [ok] 186    | [intermediate] | [stable] | [risk: none]     | testing, root-cause-analysis |
| [Test Organization and Assertions](./testing/test-coverage-organization-assertions.md)                  | [ok] 173    | [basic]        | [stable] | [risk: none]     | testing, assertions          |
| [Test Production Code Directly](./testing/test-production-code.md)                                      | [ok] 142    | [intermediate] | [stable] | [risk: none]     | testing, anti-patterns       |
| [Test Production Code Directly Part 1](./testing/test-production-code-part-1.md)                        | [ok] 204    | [intermediate] | [stable] | [risk: low]      | migration, split             |
| [Test Production Code Directly Part 2](./testing/test-production-code-part-2.md)                        | [draft] 64  | [intermediate] | [stable] | [risk: low]      | migration, split             |
| [Tests Must Be Parameterized by Message Kind](./testing/tests-must-be-parameterized-by-message-kind.md) | [ok] 241    | [intermediate] | [stable] | [risk: none]     | testing, data-driven         |
| [Unity Test Considerations and Anti-Patterns](./testing/test-coverage-unity-anti-patterns.md)           | [warn] 269  | [basic]        | [stable] | [risk: none]     | testing, unity               |

## Unity

| Skill                                                                       | Lines      | Complexity     | Status   | Performance  | Tags                 |
| --------------------------------------------------------------------------- | ---------- | -------------- | -------- | ------------ | -------------------- |
| [Devcontainer Cache Contract](./unity/devcontainer-cache-contract.md)       | [ok] 208   | [intermediate] | [stable] | [risk: high] | devcontainer, docker |
| [MessageAwareComponent Base-Call Contract](./unity/base-call-contract.md)   | [warn] 266 | [intermediate] | [stable] | [risk: none] | unity, analyzer      |
| [Unity CI Matrix](./unity/unity-ci-matrix.md)                               | [ok] 225   | [intermediate] | [stable] | [risk: low]  | unity, ci            |
| [Unity Editor CLI Bootstrap](./unity/unity-editor-cli-bootstrap.md)         | [ok] 219   | [intermediate] | [stable] | [risk: none] | unity, cli           |
| [Unity License Bootstrap](./unity/unity-license-bootstrap.md)               | [ok] 194   | [basic]        | [stable] | [risk: none] | unity, license       |
| [Unity License Return Guarantee](./unity/unity-license-return-guarantee.md) | [warn] 273 | [intermediate] | [stable] | [risk: none] | unity, serial        |
| [Unity MCP Test Loop](./unity/mcp-test-loop.md)                             | [ok] 207   | [intermediate] | [stable] | [risk: none] | unity, mcp           |
| [Unity Perf Test Isolation](./unity/unity-perf-test-isolation.md)           | [ok] 208   | [intermediate] | [stable] | [risk: high] | unity, performance   |
| [Unity Runner Host Prerequisites](./unity/unity-runner-host-prereqs.md)     | [ok] 251   | [intermediate] | [stable] | [risk: none] | unity, windows       |
| [UPM Test Harness](./unity/upm-test-harness.md)                             | [ok] 190   | [basic]        | [stable] | [risk: none] | unity, upm           |

---
