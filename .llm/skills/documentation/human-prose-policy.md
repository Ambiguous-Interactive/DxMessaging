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
  reasoning: "A fixed phrase ban list applied when writing and reviewing prose"

impact:
  performance:
    rating: "none"
    details: "Documentation only"
  maintainability:
    rating: "high"
    details: "Removes LLM drift from docs and keeps voice consistent across contributors"
  testability:
    rating: "low"
    details: "Writing convention checked at review time; Vale rule packs cover the mechanical checks when run locally"

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

DxMessaging documentation is written for humans reading reference material. Prose that reads like a marketing landing page or a generic LLM completion costs the reader trust and the project tokens. This policy bans a specific set of LLM-signature phrasings; the Vale rule packs under `.vale/styles/DxMessaging/` cover the mechanical checks, and the rest is applied when writing and reviewing.

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

The Banned Categories lists above are the canonical set; apply them when writing and reviewing prose. There is no bespoke validator (see Enforcement below).

## Allowed Exceptions

- **Skill files about the policy.** Files under `.llm/skills/documentation/` are wholly exempt.
- **`CHANGELOG.md` and `comprehensive`.** Release notes legitimately use the term. The exemption is matched case-insensitively on the basename.
- **Auto-generated files.** `llms.txt` is exempt because it is regenerated mechanically by `scripts/update-llms-txt.js`. (`.llm/skills/index.md` is a hand-maintained catalog and follows the policy like any other doc.)
- **YAML frontmatter.** A leading `---\n...\n---\n` block at the top of `.md` files is out of scope. Schema strings inside frontmatter (such as `complexity` reasoning fields) are not prose.
- **Genuinely-right words.** When a banned term is the right word for a specific sentence (for example, quoting an error message or naming an upstream feature), keep it and be ready to defend it in review. Historical `<!-- prose-allow* -->` HTML comments in older docs were markers for a deleted validator; no tool processes them anymore, so do not add new ones. The default answer to a banned term is still to rewrite the sentence.

## Enforcement

This is a writing convention applied at authoring and review time; there is no bespoke validator. The Vale configuration (`.vale.ini` + `.vale/styles/DxMessaging/`) covers passive voice, weasel words, hedges, marketing language, and LLM filler when you run Vale locally (`vale docs/ README.md`).

There is no grandfather list: every banned phrase you encounter in a file you touch is a defect to fix.

## How to Fix Violations

There is no auto-fix. Each banned phrase is a sign that the sentence around it should be rewritten with a concrete claim or a plain statement.

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
