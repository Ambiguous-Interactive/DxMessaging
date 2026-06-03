---
title: "Regex Pattern Documentation"
id: "regex-documentation"
category: "scripting"
version: "1.0.0"
created: "2026-01-29"
updated: "2026-01-29"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/"
    - path: "docs/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "regex"
  - "documentation"
  - "scripting"
  - "patterns"
  - "comments"
  - "maintenance"

complexity:
  level: "intermediate"
  reasoning: "Requires regex knowledge to understand flag behaviors and documentation implications"

impact:
  performance:
    rating: "none"
    details: "Documentation only; no runtime performance impact"
  maintainability:
    rating: "high"
    details: "Improves code clarity and prevents maintenance bugs from misleading comments"
  testability:
    rating: "medium"
    details: "Helps verify regex behavior matches documented expectations"

prerequisites:
  - "Basic regex syntax"
  - "Understanding of regex flags"

dependencies:
  packages: []
  skills:
    - "shell-best-practices"

applies_to:
  languages:
    - "JavaScript"
    - "PowerShell"
    - "C#"
    - "Bash"
  frameworks: []
  versions: {}

aliases:
  - "Regex comments"
  - "Pattern documentation"
  - "Regular expression documentation"

related:
  - shell-best-practices
  - cross-platform-compatibility

status: "stable"
---

# Regex Pattern Documentation

> **One-line summary**: Document regex patterns accurately by describing what they actually match,
> including all flag-dependent behavior.

## Overview

This skill covers best practices for documenting regular expression patterns. Accurate comments are critical because regex behavior changes dramatically based on flags, and misleading comments cause maintenance bugs. Many subtle bugs arise when comments describe intended behavior rather than actual behavior.

## Solution

1. **Describe actual behavior** - Comments must answer "What will this pattern match?" not "What do I hope it matches?"
1. **Document flag effects** - Global, case-insensitive, multiline, and dotall flags change matching behavior significantly
1. **Use precise language** - Say "matches all occurrences" (global) vs "matches first occurrence" (non-global)
1. **Include character class details** - Specify whether patterns match Unicode, newlines, or specific character ranges

## Core Principle

### Comments must describe actual behavior, not intended behavior

A regex comment should answer: "What will this pattern actually match?" not "What do I hope it matches?"

## Whitespace Character Classes

### `\s` vs `[ \t]` - A Critical Distinction

The `\s` shorthand matches **all whitespace characters including newlines**. This is a frequent source of bugs when you only want to match spaces and tabs.

| Pattern     | Matches                                                                     | Use When                                                         |
| ----------- | --------------------------------------------------------------------------- | ---------------------------------------------------------------- |
| `\s`        | Space, tab, newline (`\n`), carriage return (`\r`), form feed, vertical tab | You want to match ANY whitespace including line breaks           |
| `[ \t]`     | Only space and tab                                                          | You want horizontal whitespace only, preserving newlines         |
| `[\r\n]`    | Only newline characters                                                     | You want to match line breaks specifically                       |
| `[^\S\r\n]` | Whitespace except newlines (double negative trick)                          | Alternative to `[ \t]` that includes other horizontal whitespace |

#### Wrong Pattern

```javascript
// WRONG: \s* consumes newlines, concatenating lines when replaced
const DIRECTIVE_PATTERN = /^%%\{init:.*?\}%%\s*/gm;
// Intended: Strip directive and trailing spaces
// Actual: Also consumes the newline, joining this line with the next
```

#### Correct Pattern

```javascript
// CORRECT: [ \t]* only matches horizontal whitespace
const DIRECTIVE_PATTERN = /^[ \t]*%%\{init:.*?\}%%[ \t]*\r?\n?/gm;
// Strips directive, surrounding spaces/tabs, and just its own line ending
// Preserves separation between other lines
```

#### Practical Impact

```javascript
const text = "line1\n%%{init:...}%%\nline2";

// Bug: \s* consumes the newline AFTER the directive
text.replace(/%%\{init:.*?\}%%\s*/g, "");
// Result: "line1\nline2" with missing newline before line2

// Fixed: [ \t]* preserves newlines
text.replace(/%%\{init:.*?\}%%[ \t]*/g, "");
// Result: "line1\n\nline2" - newlines preserved
```

## Flag Reference Table

| Flag | Name                 | Effect                                                    | Comment Implications                                            |
| ---- | -------------------- | --------------------------------------------------------- | --------------------------------------------------------------- |
| `g`  | Global               | Match all occurrences, not just the first                 | Comments must use plural ("matches all X" not "matches X")      |
| `i`  | Case-insensitive     | `[A-Za-z]` matches regardless of case                     | Comments must mention "case-insensitive" explicitly             |
| `m`  | Multiline            | `^` and `$` match start/end of each line, not just string | Comments must say "at start/end of any line" not "at start/end" |
| `s`  | DotAll (Single-line) | `.` matches newline characters too                        | Comments must mention "including newlines" when using `.`       |
| `u`  | Unicode              | Enables full Unicode support, `\p{}` escapes              | Comments should note Unicode-aware matching if relevant         |

## See Also

- [regex documentation part 1](./regex-documentation-part-1.md)
- [regex documentation part 2](./regex-documentation-part-2.md)
