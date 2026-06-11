---
title: "ASCII-Only Documentation Policy"
id: "ascii-only-docs"
category: "documentation"
version: "1.0.0"
created: "2026-04-30"
updated: "2026-04-30"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "docs/"
    - path: "README.md"
    - path: "Runtime/"
    - path: "Editor/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "documentation"
  - "ascii"
  - "linting"
  - "policy"
  - "tooling"

complexity:
  level: "basic"
  reasoning: "Mechanical character-class enforcement with a small allow list"

impact:
  performance:
    rating: "none"
    details: "Documentation only"
  maintainability:
    rating: "high"
    details: "ASCII-only docs guarantee consistent grep/terminal/rg workflows and reduce LLM tokenization noise"
  testability:
    rating: "low"
    details: "Lint script and pre-commit hook fully cover the policy"

prerequisites:
  - "Awareness of the project's ASCII normalization tooling"

dependencies:
  packages: []
  skills:
    - "markdown-compatibility"
    - "documentation-style-guide"

applies_to:
  languages:
    - "Markdown"
    - "C#"
  frameworks:
    - "MkDocs"
    - "GitHub"

aliases:
  - "ASCII docs"
  - "Pure ASCII policy"
  - "No em-dash policy"

related:
  - "markdown-compatibility"
  - "documentation-code-samples"
  - "code-samples-must-compile"

status: "stable"
---

# ASCII-Only Documentation Policy

> **One-line summary**: All `.md` files and `///` XML doc comments must contain pure ASCII characters; real emojis are allowed only in callout positions and capped at 5 per file.

## Overview

The DxMessaging documentation surface (Markdown files, XML doc comments inside C# sources, and the generated `llms.txt`) is held to a strict ASCII-only standard. Real Unicode emojis (codepoint range `U+1F300` and above) are permitted only when used inside a markdown blockquote/admonition (a line beginning with `>`), with a soft per-file cap of five.

## Rationale

This rule did not start as a stylistic preference. It was adopted after a documentation cleanup pass uncovered roughly 5,350 non-ASCII characters scattered across 33+ files. Those characters - em-dashes, curly quotes, ellipses, bullets, arrows, geometric/dingbat glyphs, no-break spaces, mathematical operators, and box-drawing diagram characters - were:

- **Breaking grep/terminal/rg workflows.** A user searching for `--` (a real ASCII separator) would not find paragraphs that used the visually identical `-` (em-dash, `U+2014`).
- **Producing inconsistent rendering across viewers.** Curly quotes rendered as boxes in some terminals; box-drawing diagrams collapsed to garbage on platforms with non-standard fonts.
- **Increasing LLM tokenization cost without information gain.** Each non-ASCII glyph cost more tokens than its ASCII equivalent and added entropy that interfered with downstream processing.

The user explicitly required ASCII-only docs going forward and asked that the rule be enforced mechanically so it is never re-litigated.

## Allowed Characters

| Class                          | Codepoints                             | Notes                                           |
| ------------------------------ | -------------------------------------- | ----------------------------------------------- |
| Printable ASCII                | `U+0020` - `U+007E`                    | The full standard set                           |
| ASCII whitespace               | `\t \n \r`                             | Indentation and newlines                        |
| Variation selectors            | `U+FE0E`, `U+FE0F`                     | Allowed everywhere (ignored by readers)         |
| Real emojis in callout context | `U+1F300+` on lines beginning with `>` | Cap of 5 per file (warning only)                |
| BOM                            | `U+FEFF`                               | Tolerated only as the first character of a file |

## Banned Characters

The lint flags any character not in the allow list. The most common offenders are:

- Em-dash `-` and en-dash `-` (`U+2014`, `U+2013`)
- Curly double quotes `"` `"` (`U+201C`, `U+201D`)
- Curly single quotes `'` `'` (`U+2018`, `U+2019`)
- Ellipsis `...` (`U+2026`)
- Bullet `-` (`U+2022`)
- Arrows `->` `<-` `<->` `=>` `<=` (`U+2192`, `U+2190`, `U+2194`, `U+21D2`, `U+21D0`)
- Geometric/dingbat range `U+2300` - `U+27BF` (`Yes`, `No`, `Warning`, `->`, `-`, etc.)
- Box-drawing characters `+ -- |` (`U+2500` - `U+257F`)
- No-break space (`U+00A0`)
- Mathematical operators (`<=`, `>=`, `!=`, `x`, `+/-`)

Outside the curated callout-emoji exception, **any** non-ASCII character is a violation.

## Substitution Table

When rewriting content, apply these substitutions by hand (there is no auto-fixer script).

| Original           | Codepoint           | Replacement    | Notes                                         |
| ------------------ | ------------------- | -------------- | --------------------------------------------- |
| Em-dash            | `U+2014`            | `--`           | Spaces around                                 |
| En-dash            | `U+2013`            | `-`            | Except numeric ranges                         |
| Curly double quote | `U+201C`,`U+201D`   | `"`            | Always safe                                   |
| Curly single quote | `U+2018`,`U+2019`   | `'`            | Always safe                                   |
| Ellipsis           | `U+2026`            | `...`          | Three ASCII dots                              |
| Bullet             | `U+2022`            | `-`            | Outside fenced blocks only                    |
| Right arrow        | `U+2192`,`U+21D2`   | `to` or `->`   | Word form in prose; symbol form in menus/code |
| Left arrow         | `U+2190`,`U+21D0`   | `from` or `<-` | Same context rule                             |
| Both-ways arrow    | `U+2194`            | `<->`          | Outside fenced blocks                         |
| Checkmark          | `U+2713`,`U+2705`   | `Yes` / `[x]`  | Tables: words; lists: checkboxes              |
| Cross              | `U+2717`,`U+274C`   | `No` / `[ ]`   | Same as above                                 |
| Warning            | `U+26A0`            | `Warning:`     | Used as a callout prefix                      |
| No-break space     | `U+00A0`            | regular space  |                                               |
| Less-or-equal      | `U+2264`            | `<=`           |                                               |
| Greater-or-equal   | `U+2265`            | `>=`           |                                               |
| Not-equal          | `U+2260`            | `!=`           |                                               |
| Multiplication     | `U+00D7`            | `x`            |                                               |
| Plus-minus         | `U+00B1`            | `+/-`          |                                               |
| Box-drawing        | `U+2500` - `U+257F` | manual rewrite | Use ASCII trees (`+ --`) or Mermaid diagrams  |

## Allowed Exceptions

- **Callout-position emojis.** A real emoji at the start of an admonition line (`> 1F4DD Note`) is allowed; keep these to roughly 5 per file.
- **Skill emoji-shortcode example data.** `.llm/skills/documentation/markdown-compatibility-part-1.md` and `markdown-compatibility-part-2.md` are exempt from emoji and codepoint scanning entirely; they document the project's emoji-shortcode conventions and need to display the source forms.

## Enforcement

This is a writing convention applied at authoring time; there is no dedicated validator. Apply the substitution table when writing or editing docs, and fix any non-ASCII characters you encounter in files you touch.

To spot violations in changed files:

```bash
grep -nP '[^\x00-\x7F]' <changed-doc-files...>
```

For arrows, review carefully: `to` / `from` reads better in prose than `->` / `<-`, but a menu path like `Tools > Wallstop Studios > DxMessaging` should keep the `>` form.

For box-drawing characters, the normalizer flags but does not auto-fix. Rewrite as either an ASCII tree or a Mermaid diagram (preferred for non-tree shapes).

## See Also

- [Markdown Compatibility Guidelines](./markdown-compatibility.md)
- [Documentation Style Guide](./documentation-style-guide.md)
- [Code Samples Must Compile](./code-samples-must-compile.md)
