---
name: documentation-style
description: "Prose rules for DxMessaging documentation: ASCII-only Markdown and /// XML comments, the banned marketing/LLM-filler phrase list backed by Vale, action-first active-voice writing, brand capitalization, the same-PR doc update checklist, and the split between the user perf page and the dev perf runbook. Use when writing or editing README.md, docs/, or XML doc comments, when a review flags em-dashes, curly quotes, arrows, or words like 'robust', 'comprehensive', or 'delve into', or when deciding whether perf content belongs in docs/architecture/performance.md or docs/runbooks/perf-benchmark-methodology.md."
metadata:
  category: "documentation"
  tags: "documentation, style, writing, clarity"
---

# Documentation Style

DxMessaging documentation is reference material for humans: pure ASCII, plain prose, action
first, and code instead of paragraphs describing code. These rules apply to every `.md` file
and every `///` XML doc comment in `Runtime/`, `Editor/`, and `SourceGenerators/`.

## When to use

- Writing or editing anything under `docs/`, `README.md`, or other tracked `.md` files.
- Writing or editing `///` XML doc comments on public APIs.
- A review or lint flags non-ASCII characters, marketing adjectives, or hedge transitions.
- Shipping a code change that alters user-facing behavior and needs matching doc updates.
- Adding performance content and deciding which page it belongs on.

## Rules

### ASCII only

- Allowed: printable ASCII `U+0020` - `U+007E`, plus `\t`, `\n`, `\r`. Variation selectors
  `U+FE0E` / `U+FE0F` are allowed anywhere. A BOM (`U+FEFF`) is tolerated only as the first
  character of a file.
- Real emoji (`U+1F300` and above) are allowed only on a blockquote/admonition line starting
  with `>`, with a soft cap of five per file. Anywhere else they are a violation.
- Substitute by hand; there is no auto-fixer. Em-dash `U+2014` becomes `--`, en-dash
  `U+2013` becomes `-`, curly quotes `U+2018`/`U+2019`/`U+201C`/`U+201D` become `'` and `"`,
  ellipsis `U+2026` becomes `...`, bullet `U+2022` becomes `-`, `U+2264`/`U+2265`/`U+2260`
  become `<=`/`>=`/`!=`, `U+00D7` becomes `x`, `U+00B1` becomes `+/-`, and `U+00A0` becomes a
  normal space.
- Arrows: use the words `to` and `from` in prose; keep `>` in menu paths such as
  `Tools > Wallstop Studios > DxMessaging`. Box-drawing characters `U+2500` - `U+257F` must be
  rewritten as an ASCII tree or a Mermaid diagram.
- Spot violations in changed files with `grep -nP '[^\x00-\x7F]' <files...>`.

### Human prose

- Banned marketing adjectives include `robust`, `powerful`, `seamless`, `elegant`,
  `comprehensive`, `cutting-edge`, `blazing fast`, `world-class`, `industry-leading`,
  `state-of-the-art`, `production-ready`, `enterprise-grade`, `battle-tested`, `bulletproof`,
  `rock-solid`.
- Banned LLM filler includes `delve into`, `dive into`, `dive deep into`, `harness the power`,
  `navigate the complexities`, `unlock the potential`, `at the heart of`, `lies the`,
  `tapestry`, `realm of`, `treasure trove`, `it goes without saying`, `needless to say`.
- Banned hedge transitions at the start of a sentence or list item: `Furthermore`, `Moreover`,
  `In conclusion`, `In essence`, `In summary`, `It's important to note`, `It's worth noting`,
  `That said`, `Overall`, `Ultimately`.
- Banned vague quantifiers: `a wide variety of`, `a wide array of`, `a plethora of`, `myriad`,
  `numerous`. Banned soft fluff: `provides you with`, `helps you to`, `enables you to`,
  `allows you to easily`, `gives you the best`.
- Exemptions: files under `.llm/skills/documentation/`, the word `comprehensive` in
  `CHANGELOG.md`, the generated `llms.txt`, and YAML frontmatter blocks. Legacy
  `<!-- prose-allow -->` HTML comments are dead markers; do not add new ones.
- Enforcement is Vale (`.vale.ini` plus `.vale/styles/DxMessaging/`). Run
  `vale docs/ README.md` locally. There is no grandfather list: fix banned phrases in any file
  you touch by rewriting the sentence with a concrete claim.

### Style

- Be concise, use active voice, and lead with the action ("Call `RegisterMessageHandler` to
  subscribe"), not with preamble.
- Show a `csharp` code block instead of describing a signature in prose.
- Brand capitalization: GitHub, JavaScript, TypeScript, Node.js, npm (always lowercase), C#,
  .NET, Unity, NuGet, Visual Studio, VS Code.
- Never ship placeholder XML docs (`TODO: Add documentation`) or docs that restate the member
  name. State what the member does, what the return value indicates, and the edge cases.

### Update workflow

- Documentation changes ship in the same PR as the code change, never as a follow-up.
- New public API needs XML docs plus a `docs/` article plus a README mention if significant.
  A behavior change needs a `docs/` update plus a version annotation. A deprecation needs
  `[Obsolete]` plus migration notes. A perf change updates `docs/architecture/performance.md`
  and `CHANGELOG.md`.
- Before commit, run `npx prettier --write <changed-docs.md ...>` then
  `npx markdownlint-cli2 <changed-docs.md ...>`.
- Ordered lists use MD029 `one` style (every item prefixed `1.`). Internal fragment links must
  resolve (MD051).

### User vs dev performance pages

- `docs/architecture/performance.md` is the USER page: headline throughput numbers, the
  cross-library comparison matrix, scope labels (Standalone, PlayMode), a "what this means"
  section, and exactly ONE marker pair
  `<!-- AUTOGENERATED:DISPATCH-THROUGHPUT BEGIN -->` / `<!-- ... END -->`.
- `scripts/unity/render-perf-doc.js` owns the region between the markers and fails if the pair
  is missing or duplicated. Never hand-edit inside it and never add a second pair.
- `docs/runbooks/perf-benchmark-methodology.md` is the DEV page: methodology internals, GC
  sampling, per-leg build configuration, baseline capture, the regression smoke gate, the
  hot-path PR rule, and the comparison-package how-to.

## References

| Document                                                                              | Purpose                                                                                          |
| ------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------ |
| [ascii-only-docs.md](./references/ascii-only-docs.md)                                 | Allowed and banned codepoints, the full substitution table, and the callout-emoji exception      |
| [documentation-style-guide.md](./references/documentation-style-guide.md)             | Concise, active, action-first writing; brand capitalization table; XML-doc anti-patterns         |
| [documentation-update-workflow.md](./references/documentation-update-workflow.md)     | Step-by-step doc update process and the pre-commit prettier/markdownlint checklist               |
| [documentation-updates.md](./references/documentation-updates.md)                     | Which change types require which documentation surfaces                                          |
| [human-prose-policy.md](./references/human-prose-policy.md)                           | The canonical banned-phrase lists, exemptions, and before/after rewrites                         |
| [user-vs-dev-perf-doc-separation.md](./references/user-vs-dev-perf-doc-separation.md) | What belongs on the user perf page versus the dev perf runbook, and the renderer marker contract |
