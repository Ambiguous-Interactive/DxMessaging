---
title: "Code Samples Must Compile"
id: "code-samples-must-compile"
category: "documentation"
version: "1.0.0"
created: "2026-04-30"
updated: "2026-04-30"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "docs/"
    - path: "Runtime/"
    - path: "Editor/"
    - path: "SourceGenerators/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "documentation"
  - "code-samples"
  - "compilation"
  - "linting"
  - "anti-patterns"
  - "tooling"

complexity:
  level: "basic"
  reasoning: "Pattern catalog enforced by lint plus Roslyn compilation harness"

impact:
  performance:
    rating: "none"
    details: "Documentation only"
  maintainability:
    rating: "high"
    details: "Compiling samples eliminate the entire copy/paste-broken-doc support burden"
  testability:
    rating: "high"
    details: "Roslyn-backed test asserts every fenced block, table-cell inline span, and XML doc <code> block compiles"

prerequisites:
  - "Familiarity with C# extension method semantics"
  - "Familiarity with the [Dx*Message] / [DxAutoConstructor] API surface"

dependencies:
  packages: []
  skills:
    - "documentation-code-samples"

applies_to:
  languages:
    - "C#"
    - "Markdown"
  frameworks:
    - "Unity"
    - ".NET"

aliases:
  - "Compiling samples"
  - "Doc snippet compilation"

related:
  - "documentation-code-samples"
  - "documentation-xml-docs"
  - "ascii-only-docs"

status: "stable"
---

# Code Samples Must Compile

> **One-line summary**: Every C# code sample in every doc - inline backticks, fenced blocks, and XML doc `<code>` blocks - must compile. The pattern lint is the canonical defense for the struct-rvalue-Emit bug class (samples like `new X().Emit()` that won't compile); the Roslyn harness provides supplementary semantic checks for the rest.

## Overview

DxMessaging documentation is held to a "samples-compile" bar. The bar is enforced both proactively (a pattern lint catches known-broken samples before they merge) and as a compile-time safety net (a Roslyn-backed NUnit test compiles every extracted snippet against a stub harness).

## Specific Gotcha (the trigger for this skill)

The `Emit` shorthands are extension methods on **`this ref TMessage`** where `TMessage : struct, I*Message`. A `new X(...)` expression is an rvalue and not addressable, so the form `new X(...).Emit(...)` does not compile. The compiler emits `CS1612` ("cannot modify the return value of ... because it is not a variable") or `CS1510` ("a ref or out value must be an assignable variable") depending on context.

```csharp
new SceneLoaded(1).Emit(); // Forbidden - does not compile.

// Correct - assign to a local first.
var msg = new SceneLoaded(1);
msg.Emit();
```

This pattern slipped past the original snippet-compile harness because the offending samples lived in markdown table cells (inline backticks, not fenced blocks). The pattern lint and the table-cell extraction in the Roslyn harness both now cover this surface.

## Pattern Catalog

Add new entries to this catalog as new broken-sample classes are discovered. Each entry corresponds to a rule in `scripts/validate-doc-code-patterns.js` (the `BANNED_PATTERNS` array).

### `struct-emit-temporary`

- **Regex:** `(?:(?<![\w)([])new\s+[\w.]+\s*\((?:[^()]|\([^()]*\))*\)|(?<![\w)])\(\s*new\s+[\w.]+\s*\((?:[^()]|\([^()]*\))*\)\s*\))\s*\.\s*Emit\w*\s*\(`
- **Why it fails:** `Emit*` extensions take `this ref TMessage`; `new` produces a non-addressable rvalue. Note: this is the canonical defense - the Roslyn compilation test cannot reliably catch this bug class because the stub setup produces `CS1510` (not `CS1612`), and `CS1510` must stay in the harness's ignore list to suppress false positives on legitimate snippets that touch unstubbed ref-returning members.
- **Variants caught (all of these will not compile):** bare form `new X().Emit()`, parenthesized form `(new X()).Emit()`, namespaced form `new Ns.X().Emit()`, all `Emit*` shorthands (`EmitTargeted`, `EmitFrom`, `EmitGameObjectTargeted`, etc.), and whitespace variants like `new X () . Emit ( )`. False-positive guard: `someMethod(new X()).Emit()` does NOT match (the trailing `.Emit` belongs to the method's return value, not a `new X()` rvalue).
- **Fix:** Assign to a local first: `var msg = new X(...); msg.Emit();`. For table cells where space is tight, use a compact two-statement form (`var m = new X(); m.Emit();`) or rewrite the cell to show the API signature only.
- **Counter-example marker:** Lines containing one of the phrases `won't compile`, `will not compile`, `does not compile`, `do not compile`, `fails to compile` are treated as deliberate negative examples and skipped.

## Enforcement

Three layers, all wired up. The two layers split responsibility cleanly:

1. **`scripts/validate-doc-code-patterns.js`** - pluggable pattern lint. **Canonical defense for the struct-rvalue-Emit bug class** (broken samples like `new X().Emit()` that won't compile) and any other pattern catalog entries. Scans `.md` files (fenced blocks, inline code, table cells, prose) and `.cs` files (`///` XML doc comment lines). Exits non-zero on any violation. Run with `--list-rules` to inspect the active catalog. Unit tested via `scripts/__tests__/validate-doc-code-patterns.test.js`.
1. **`DocsSnippetCompilationTests`** in `SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.Tests/`. **Supplementary semantic checks** - catches type errors, return-type mismatches, missing-identifier diagnostics not in the ignore list, and other compile-time issues that survive the stub-only environment. **Cannot reliably catch the struct-rvalue-Emit bug** (e.g. `new X().Emit()` will not compile) because the stub setup emits `CS1510` (not `CS1612`) for that pattern, and `CS1510` is in `IgnoredSnippetDiagnosticIds` to suppress false positives from legitimate snippets that touch unstubbed ref-returning members. Three test case sources:
   - `DocumentationSnippetsCompile` - fenced ` ```csharp ` blocks across `docs/`.
   - `InlineTableSnippetsCompile` - inline backtick code spans inside table rows. Filtered via `IsApiSignatureDocumentation` and a "must contain `(` and end with `)` or `;`" heuristic so single identifiers and bare type names don't get tested.
   - `XmlDocCodeBlocksCompile` - `<code>...</code>` and `<example><code>...</code></example>` blocks across `Runtime/`, `Editor/`, `SourceGenerators/`.
1. **Pre-commit hooks** - the validator runs as part of `run-staged-md-pipeline` (for `.md` / `.markdown` files) and `run-staged-validators` (for `.cs` files) in `.pre-commit-config.yaml`. The standalone CLI `node scripts/validate-doc-code-patterns.js` is preserved for ad-hoc invocations. The same validator runs on every PR via the **CI workflow** at `.github/workflows/docs-lint.yml`.

The harness uses a minimal stub set (`GeneratorTestUtilities.SharedStubs`) rather than the full runtime, so doc snippets that reference real DxMessaging APIs without redeclaring them work. The corresponding diagnostic IDs (`CS0103`, `CS0246`, `CS1061`, etc., for missing identifiers and types) are tolerated via `IgnoredSnippetDiagnosticIds` so the test focuses on real semantic bugs that don't depend on external symbols. The trade-off: stub coverage gaps require ignoring `CS1510`, which means the textual lint is the only mechanism that catches the struct-rvalue-Emit bug class.

## How to Fix Violations

1. Run `node scripts/validate-doc-code-patterns.js` locally to see the file:line:column report.
1. For each hit, follow the rule's `fix` suggestion. The `struct-emit-temporary` rule's fix is "assign to local first."
1. Re-run the validator until clean.
1. Run `dotnet test` in `SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.Tests/` to confirm the Roslyn harness still passes.

When changing a snippet that the Roslyn test was previously skipping (via `ShouldSkipSnippet`), prefer making the snippet standalone-compilable over extending the skip heuristic. If the snippet truly is partial (showing only a method body or a usage pattern), document the rationale in the surrounding prose.

## How to Add a New Pattern

1. Identify the broken-sample class. Confirm it cannot be caught by the existing Roslyn harness (often because the broken pattern is in a context the harness skips, or its compile error is in `IgnoredSnippetDiagnosticIds`).
1. Add an entry to `BANNED_PATTERNS` in `scripts/validate-doc-code-patterns.js` with a unique `id`, the regex, the `why`, and the `fix`.
1. Run `node scripts/validate-doc-code-patterns.js` to catch any existing instances.
1. Add the pattern to the catalog above.
1. If the pattern's diagnostic ID is reliably caught by Roslyn, consider removing it from `IgnoredSnippetDiagnosticIds` so the harness becomes the canonical enforcement.

## See Also

- [Documentation Code Samples](./documentation-code-samples.md)
- [XML Documentation Standards](./documentation-xml-docs.md)
- [ASCII-Only Documentation Policy](./ascii-only-docs.md)
