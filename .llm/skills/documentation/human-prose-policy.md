---
title: "Human-Prose Documentation Policy"
id: "human-prose-policy"
category: "documentation"
version: "1.0.0"
created: "2026-05-02"
updated: "2026-05-02"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "docs/"
    - path: "README.md"
    - path: "Runtime/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "documentation"
  - "prose"
  - "linting"
  - "policy"
  - "tooling"

complexity:
  level: "basic"
  reasoning: "Mechanical phrase enforcement with a small allow-marker system"

impact:
  performance:
    rating: "none"
    details: "Documentation only"
  maintainability:
    rating: "high"
    details: "Removes LLM drift from docs and keeps voice consistent across contributors"
  testability:
    rating: "low"
    details: "Validator and Vale rule packs cover the policy automatically"

prerequisites:
  - "Awareness of the project's documentation linting toolchain"

dependencies:
  packages: []
  skills:
    - "ascii-only-docs"
    - "documentation-style-guide"

applies_to:
  languages:
    - "Markdown"
    - "C#"
  frameworks:
    - "MkDocs"
    - "GitHub"

aliases:
  - "Prose policy"
  - "Anti-LLM-prose policy"
  - "Human voice policy"

related:
  - "ascii-only-docs"
  - "documentation-style-guide"
  - "code-samples-must-compile"

status: "stable"
---

# Human-Prose Documentation Policy

> **One-line summary**: All documentation prose - in `.md` files and `///` XML doc comments - must avoid marketing adjectives, LLM filler idioms, hedge transitions, vague quantifiers, and soft conversational fluff.

## Overview

DxMessaging documentation is written for humans reading reference material. Prose that reads like a marketing landing page or a generic LLM completion costs the reader trust and the project tokens. This policy bans a specific set of LLM-signature phrasings and is enforced mechanically by `scripts/validate-docs-prose.js` (the source of truth) plus Vale rule packs under `.vale/styles/DxMessaging/` for structural prose checks.

## Rationale

Marketing adjectives without a measurement (`blazing fast`, `world-class`) signal that the writer did not have a number. Filler phrases like `it goes without saying` consume context and produce no signal. Banning a small set of phrases keeps voice convergent without per-PR debates.

## Banned Categories

Marketing adjectives (case-insensitive, whole-word):

`cutting-edge`, `cutting edge`, `blazing fast`, `seamless`, `seamlessly`, `seamlessness`, `powerful`, `powerfully`, `robust`, `robustly`, `elegant`, `elegantly`, `world-class`, `next-generation`, `industry-leading`, `state-of-the-art`, `comprehensive`, `comprehensively`, `unparalleled`, `revolutionary`, `game-changing`, `best-in-class`, `production-ready`, `enterprise-grade`, `lightning-fast`, `frictionless`, `battle-tested`, `bulletproof`, `rock-solid`.

LLM filler idioms (case-insensitive, phrase match):

`delve into`, `delving into`, `delved into`, `delves into`, `harness the power`, `navigate the complexities`, `unlock the potential`, `tapestry`, `realm of`, `dive deep into`, `dive into`, `at the heart of`, `lies the`, `treasure trove`, `it goes without saying`, `needless to say`.

Hedge transitions (only at the start of a sentence or list item; trailing comma optional):

`Furthermore`, `Moreover`, `In conclusion`, `In essence`, `In summary`, `It's important to note`, `It's worth noting`, `That said`, `Overall`, `Ultimately`.

Vague quantifiers (case-insensitive, whole-word):

`a wide variety of`, `a wide array of`, `a plethora of`, `myriad`, `numerous`.

Soft conversational fluff (regex):

`gives you (the )?best`, `provides you with`, `helps you to`, `allows you to easily`, `enables you to`.

The validator's `--list-rules` flag prints the canonical set with full term lists; the JS file is the source of truth.

## Allowed Exceptions

- **Skill files about the policy.** Files under `.llm/skills/documentation/` are wholly exempt.
- **`CHANGELOG.md` and `comprehensive`.** Release notes legitimately use the term. The exemption is matched case-insensitively on the basename.
- **Auto-generated files.** `.llm/skills/index.md` and `llms.txt` are exempt because they are regenerated mechanically.
- **YAML frontmatter.** A leading `---\n...\n---\n` block at the top of `.md` files is skipped entirely. Schema strings inside frontmatter (such as `complexity` reasoning fields) never trigger the validator.
- **Inline allow markers.** When a banned term is genuinely the right word for a specific sentence, mark it inline using one of:

  ```markdown
  <!-- prose-allow: powerful -->
  <!-- prose-allow-next-line: powerful, robust -->
  <!-- prose-allow-file: powerful -->
  ```

  Markers must fit on a single line: the opening `<!--` and the closing `-->` must be on the same line. A multi-line marker emits a `WARN` to stderr but does not fail the run. Marker comments are themselves stripped from the scan, so they never trigger on themselves.
  - `prose-allow` matches on the same line.
  - `prose-allow-next-line` applies to the next non-blank scanned line.
  - `prose-allow-file` applies file-wide.

  Skill files outside `.llm/skills/documentation/` should use `<!-- prose-allow-file: term -->` near the top when a banned term is necessary in the body. Use markers sparingly. The default answer to a flagged term is to rewrite the sentence.

## Enforcement

The policy is fully enforced going forward. There is no grandfather list: every violation reported by `scripts/validate-docs-prose.js` is a new defect to fix.

| Layer                                     | What it covers                                            | When it runs                                                                                                                  |
| ----------------------------------------- | --------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| `scripts/validate-docs-prose.js`          | All banned phrases, allow markers, exemptions             | Standalone CLI for ad-hoc runs; same module also called in-process by the consolidated runners below                          |
| `run-staged-md-pipeline` pre-commit hook  | Runs the prose validator in-process on staged `.md` files | Local pre-commit (consolidated `.md` pipeline that also handles fixers, prettier, markdownlint, ascii, and code-pattern lint) |
| `run-staged-validators` pre-commit hook   | Runs the prose validator in-process on staged `.cs` files | Local pre-commit (consolidated `.cs` validator runner that also handles ascii and code-pattern lint)                          |
| `.vale.ini` + `.vale/styles/DxMessaging/` | Passive voice, weasel words, additional style rules       | Local-only until committed and wired into CI                                                                                  |

The custom JS validator is the source of truth. The Vale configuration is additive and currently lives only in working trees; once it is committed and wired into a workflow, this row will move to "CI". File an issue if Vale flags something the JS validator missed so the `RULES` array can absorb the rule first.

An earlier transitional baseline list has been retired; the policy is now fully enforced from a clean slate.

## How to Fix Violations

There is no auto-fix. Each banned phrase is a sign that the sentence around it should be rewritten. The CLI tells you the rule and the suggested replacement strategy:

```bash
node scripts/validate-docs-prose.js
```

```text
docs/install.md:42:5 [marketing/marketing] 'cutting-edge' -- Marketing adjective; replace with a concrete claim. modern, current, or describe the specific feature
```

To see the per-category counts across the repository:

```bash
node scripts/validate-docs-prose.js --summary
```

To run a single rule (useful when you are sweeping one category):

```bash
node scripts/validate-docs-prose.js --rule marketing
```

To list every configured rule and its term list:

```bash
node scripts/validate-docs-prose.js --list-rules
```

### Before / After

Marketing - bad: `DxMessaging is a powerful, comprehensive messaging library.` Good: `DxMessaging is a synchronous, allocation-free message bus for Unity.`

LLM filler - bad: `At the heart of the system lies the MessageBus.` Good: `The MessageBus is the core of the system.`

Hedge - bad: `It's important to note that registrations are reference-counted.` Good: `Registrations are reference-counted.`

Soft fluff - bad: `The bus enables you to dispatch messages.` Good: `The bus dispatches messages.`

## See Also

- [ASCII-Only Documentation Policy](./ascii-only-docs.md)
- [Documentation Style Guide](./documentation-style-guide.md)
- [Code Samples Must Compile](./code-samples-must-compile.md)
- [Documentation Updates and Maintenance](./documentation-updates.md)
