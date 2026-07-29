---
name: documentation-code-samples
description: "Every C# sample in DxMessaging docs must compile: fenced csharp blocks, inline backtick spans in table cells, samples in docs/overrides/*.html, and XML doc <code> blocks are all extracted and compiled by DocsSnippetCompilationTests in .docs-tests/. Covers the struct-rvalue-Emit bug class (new X().Emit() does not compile because Emit* extensions take this ref TMessage), the text-pattern guard that catches it, XML doc requirements, and version annotations. Use when writing or fixing a code sample, adding XML docs to a public API, or triaging a docs-tests failure such as CS1510, CS1612, or DocumentationDoesNotEmitStructMessagesFromTemporaries."
metadata:
  category: "documentation"
  tags: "documentation, code-samples, compilation, linting, anti-patterns, tooling"
---

# Documentation Code Samples

Docs samples are copy-paste contracts. Every C# sample in the repository - fenced block,
inline backtick span, HTML template, or `///` `<code>` block - is extracted and compiled by
the docs test project, and the samples that the compiler cannot catch are covered by an
explicit text-pattern guard.

## When to use

- Writing or editing a `csharp` sample in `docs/`, `README.md`, or a table cell.
- Adding or revising `///` XML documentation on a public API.
- A `.docs-tests` run fails, or a review flags `new X().Emit()`.
- Documenting a new or changed API and deciding where the version annotation goes.

## Rules

### The struct-rvalue-Emit bug class

- `Emit`, `EmitTargeted`, `EmitFrom`, `EmitGameObjectTargeted`, and the other `Emit*`
  shorthands are extension methods on `this ref TMessage` where
  `TMessage : struct, I*Message`. A `new X(...)` expression is an rvalue and is not
  addressable, so `new X(...).Emit(...)` does NOT compile (`CS1612` or `CS1510`).
- Always assign to a local first:

  ```csharp
  // Correct
  var msg = new SceneLoaded(1);
  msg.Emit();
  ```

- The forbidden variants are the bare form `new X().Emit()`, the parenthesized form
  `(new X()).Emit()`, the namespaced form `new Ns.X().Emit()`, every `Emit*` shorthand, and
  whitespace variants such as `new X () . Emit ( )`. `someMethod(new X()).Emit()` is fine: the
  `.Emit` belongs to the method's return value.
- In a tight table cell use the compact two-statement form `var m = new X(); m.Emit();` or
  show only the API signature.
- A line containing `won't compile`, `will not compile`, `does not compile`, `do not compile`,
  or `fails to compile` is treated as a deliberate negative example and skipped.
- This class is enforced by the test `DocumentationDoesNotEmitStructMessagesFromTemporaries`,
  not by Roslyn, because the stub environment surfaces `CS1510`, which must stay in
  `IgnoredSnippetDiagnosticIds` to suppress false positives on legitimate ref-returning
  snippets.

### The docs test harness

- `.docs-tests/WallstopStudios.DxMessaging.Docs.Tests.csproj` runs in the dotnet CI job on
  every PR that touches docs, C#, or project files. Run it locally with
  `dotnet test .docs-tests/WallstopStudios.DxMessaging.Docs.Tests.csproj`.
- `DocsSnippetCompilationTests` case sources: `DocumentationSnippetsCompile` (fenced
  ` ```csharp ` blocks under `docs/`), `HtmlOverrideCSharpSnippetsCompile`
  (`docs/overrides/*.html`), `InlineTableSnippetsCompile` (backtick spans inside table rows,
  filtered by `IsApiSignatureDocumentation` and a "contains `(` and ends with `)` or `;`"
  heuristic), and `XmlDocCodeBlocksCompile` (`<code>` and `<example><code>` across `Runtime/`,
  `Editor/`, `SourceGenerators/`).
- Snippets compile against `DocsSnippetCompiler.SharedStubs`, not the full runtime, so
  missing-symbol diagnostics (`CS0103`, `CS0246`, `CS1061`, `CS1510`) are tolerated. The tests
  target real semantic bugs: type errors, return-type mismatches, wrong signatures.
- `DocsObsoleteApiReferenceTests` scans published docs for references to APIs marked obsolete
  in `Runtime/` and `Editor/`.
- When a snippet is being skipped by `ShouldSkipSnippet`, prefer making it standalone
  compilable over widening the skip heuristic. If it truly is a fragment, say so in the
  surrounding prose.
- To add a new broken-sample class: confirm Roslyn cannot catch it, add the regex plus the
  "why" and the "fix" to the pattern catalog, grep the docs for existing hits, and consider
  removing the diagnostic ID from `IgnoredSnippetDiagnosticIds` if Roslyn can own it instead.

### Sample content

- Samples must be Correct (compiles), Complete (`using` directives and enclosing class
  present), Current (no deprecated API), and Tested.
- Handler signatures take the message by reference: `private void HandleDamage(ref DamageMessage message)`.
- Register through the token in `RegisterMessageHandlers`, calling `base.RegisterMessageHandlers()` first.
- For anti-pattern examples of Markdown itself, fence with `text` or `none` rather than
  `markdown` so documentation linters do not treat the sample as real content. C# anti-patterns
  keep the `csharp` fence.
- Annotate new or changed behavior with a version: `> **Added in v2.1.0**` in prose, or
  `<para><b>Added in v2.1.0.</b></para>` inside `<remarks>`.

### XML documentation

- Every public member needs `<summary>`. Add `<typeparam>` and `<param>` for every generic and
  value parameter, `<returns>` stating what the value indicates, `<remarks>` for version notes
  and behavioral caveats, and `<example><code>` for a minimal working usage.
- `<returns>` describes meaning, not type. State the false/null case explicitly.
- Private and internal members need a `<summary>` only.

## References

| Document                                                                                  | Purpose                                                                                                          |
| ----------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------- |
| [code-samples-must-compile.md](./references/code-samples-must-compile.md)                 | The struct-rvalue-Emit pattern catalog with regex and fix, plus the four-layer enforcement and harness internals |
| [documentation-code-samples-part-1.md](./references/documentation-code-samples-part-1.md) | Example of an NUnit test that pins a documented pattern, and the sample review checklist                         |
| [documentation-code-samples.md](./references/documentation-code-samples.md)               | Correct/complete/current/tested requirements, a full worked sample, and fence-language choice for anti-patterns  |
| [documentation-xml-docs.md](./references/documentation-xml-docs.md)                       | Required XML tags per member kind, version annotation format, and minimal versus full doc variations             |
