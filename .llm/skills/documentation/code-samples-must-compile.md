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
  reasoning: "Pattern catalog applied at authoring/review time plus Roslyn compilation harness"

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

> **One-line summary**: Every C# code sample in every doc - inline backticks, fenced blocks, and XML doc `<code>` blocks - must compile. The pattern catalog below, applied manually when writing and reviewing, is the only defense for the struct-rvalue-Emit bug class (samples like `new X().Emit()` that won't compile); the Roslyn harness provides supplementary semantic checks for the rest.

## Overview

DxMessaging documentation is held to a "samples-compile" bar. The bar is enforced by author/reviewer vigilance against the pattern catalog below (the standalone pattern-lint script was removed in the tooling simplification) and by a compile-time safety net (a Roslyn-backed NUnit test compiles every extracted snippet against a stub harness).

## Specific Gotcha (the trigger for this skill)

The `Emit` shorthands are extension methods on **`this ref TMessage`** where `TMessage : struct, I*Message`. A `new X(...)` expression is an rvalue and not addressable, so the form `new X(...).Emit(...)` does not compile. The compiler emits `CS1612` ("cannot modify the return value of ... because it is not a variable") or `CS1510` ("a ref or out value must be an assignable variable") depending on context.

```csharp
new SceneLoaded(1).Emit(); // Forbidden - does not compile.

// Correct - assign to a local first.
var msg = new SceneLoaded(1);
msg.Emit();
```

This pattern slipped past the original snippet-compile harness because the offending samples lived in markdown table cells (inline backticks, not fenced blocks). The table-cell extraction in the Roslyn harness now covers this surface.

## Pattern Catalog

Add new entries to this catalog as new broken-sample classes are discovered. The catalog below is the reference for reviewers and authors; the docs test project enforces the high-risk patterns that need automation.

### `struct-emit-temporary`

- **Regex:** `(?:(?<![\w)([])new\s+[\w.]+\s*\((?:[^()]|\([^()]*\))*\)|(?<![\w)])\(\s*new\s+[\w.]+\s*\((?:[^()]|\([^()]*\))*\)\s*\))\s*\.\s*Emit\w*\s*\(`
- **Why it fails:** `Emit*` extensions take `this ref TMessage`; `new` produces a non-addressable rvalue. The docs test project enforces this as a text-pattern guard because the Roslyn compilation pass cannot reliably catch this bug class: the stub setup produces `CS1510` (not `CS1612`), and `CS1510` must stay in the harness's ignore list to suppress false positives on legitimate snippets that touch unstubbed ref-returning members.
- **Variants caught (all of these will not compile):** bare form `new X().Emit()`, parenthesized form `(new X()).Emit()`, namespaced form `new Ns.X().Emit()`, all `Emit*` shorthands (`EmitTargeted`, `EmitFrom`, `EmitGameObjectTargeted`, etc.), and whitespace variants like `new X () . Emit ( )`. False-positive guard: `someMethod(new X()).Emit()` does NOT match (the trailing `.Emit` belongs to the method's return value, not a `new X()` rvalue).
- **Fix:** Assign to a local first: `var msg = new X(...); msg.Emit();`. For table cells where space is tight, use a compact two-statement form (`var m = new X(); m.Emit();`) or rewrite the cell to show the API signature only.
- **Counter-example marker:** Lines containing one of the phrases `won't compile`, `will not compile`, `does not compile`, `do not compile`, `fails to compile` are treated as deliberate negative examples and skipped.

## Enforcement

1. **The pattern catalog above** - applied manually when writing or reviewing samples. The `struct-emit-temporary` pattern is also enforced by `DocumentationDoesNotEmitStructMessagesFromTemporaries`.
1. **`DocsSnippetCompilationTests`** in `.docs-tests/WallstopStudios.DxMessaging.Docs.Tests.csproj`. **Supplementary semantic checks** - catches type errors, return-type mismatches, missing-identifier diagnostics not in the ignore list, and other compile-time issues that survive the stub-only environment. **Cannot reliably catch the struct-rvalue-Emit bug through Roslyn diagnostics alone** (e.g. `new X().Emit()` will not compile) because the stub setup emits `CS1510` (not `CS1612`), and `CS1510` is in `IgnoredSnippetDiagnosticIds` to suppress false positives from legitimate snippets that touch unstubbed ref-returning members. Test case sources:
   - `DocumentationSnippetsCompile` - fenced ` ```csharp ` blocks across `docs/`.
   - `HtmlOverrideCSharpSnippetsCompile` - C# samples embedded in `docs/overrides/*.html` templates.
   - `InlineTableSnippetsCompile` - inline backtick code spans inside table rows. Filtered via `IsApiSignatureDocumentation` and a "must contain `(` and end with `)` or `;`" heuristic so single identifiers and bare type names don't get tested.
   - `XmlDocCodeBlocksCompile` - `<code>...</code>` and `<example><code>...</code></example>` blocks across `Runtime/`, `Editor/`, `SourceGenerators/`.
1. **`DocsObsoleteApiReferenceTests`** in the same project - scans published docs for obsolete API references derived from `Runtime/` and `Editor/`.
1. **CI** - the `.docs-tests` project runs in the dotnet job on every PR that changes docs, C#, or project files.

The harness uses a minimal stub set (`DocsSnippetCompiler.SharedStubs`) rather than the full runtime, so doc snippets that reference real DxMessaging APIs without redeclaring them work. The corresponding diagnostic IDs (`CS0103`, `CS0246`, `CS1061`, etc., for missing identifiers and types) are tolerated via `IgnoredSnippetDiagnosticIds` so the test focuses on real semantic bugs that don't depend on external symbols. The trade-off: stub coverage gaps require ignoring `CS1510`, so the text-pattern guard covers the struct-rvalue-Emit bug class.

## How to Fix Violations

1. For each hit, follow the rule's `fix` suggestion. The `struct-emit-temporary` rule's fix is "assign to local first."
1. Run `dotnet test .docs-tests/WallstopStudios.DxMessaging.Docs.Tests.csproj` to confirm the docs harness still passes.

When changing a snippet that the Roslyn test was previously skipping (via `ShouldSkipSnippet`), prefer making the snippet standalone-compilable over extending the skip heuristic. If the snippet truly is partial (showing only a method body or a usage pattern), document the rationale in the surrounding prose.

## How to Add a New Pattern

1. Identify the broken-sample class. Confirm it cannot be caught by the existing Roslyn harness (often because the broken pattern is in a context the harness skips, or its compile error is in `IgnoredSnippetDiagnosticIds`).
1. Add the pattern to the catalog above with the regex, the `why`, and the `fix`, then grep the docs for existing instances.
1. If the pattern's diagnostic ID is reliably caught by Roslyn, consider removing it from `IgnoredSnippetDiagnosticIds` so the harness becomes the canonical enforcement.

## See Also

- [Documentation Code Samples](./documentation-code-samples.md)
- [XML Documentation Standards](./documentation-xml-docs.md)
- [ASCII-Only Documentation Policy](./ascii-only-docs.md)
