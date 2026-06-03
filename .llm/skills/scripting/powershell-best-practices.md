---
title: "PowerShell Scripting Best Practices"
id: "powershell-best-practices"
category: "scripting"
version: "1.1.0"
created: "2026-01-27"
updated: "2026-05-21"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/"
    - path: ".github/workflows/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "powershell"
  - "scripting"
  - "regex"
  - "here-strings"
  - "encoding"
  - "ci-cd"
  - "pre-commit"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of PowerShell-specific behaviors that differ from other languages"

impact:
  performance:
    rating: "none"
    details: "Script patterns only; no runtime performance impact"
  maintainability:
    rating: "high"
    details: "Proper scripting patterns prevent subtle bugs and improve reliability"
  testability:
    rating: "medium"
    details: "Scripts should be tested but testing infrastructure varies"

prerequisites:
  - "Basic PowerShell syntax"
  - "Understanding of regular expressions"
  - "Familiarity with file encoding concepts"

dependencies:
  packages: []
  skills:
    - "git-workflow-robustness"

applies_to:
  languages:
    - "PowerShell"
  frameworks:
    - ".NET"
  versions:
    powershell: ">=5.1"
    dotnet: ">=netstandard2.0"

aliases:
  - "PowerShell patterns"
  - "PS1 best practices"
  - "Script reliability"

related:
  - "shell-best-practices"
  - "cross-platform-compatibility"
  - "git-workflow-robustness"
  - "documentation-updates"

status: "stable"
---

# PowerShell Scripting Best Practices

> **One-line summary**: Avoid common PowerShell pitfalls involving regex patterns, here-strings,
> file encoding, and terminology precision.

## Overview

PowerShell has unique behaviors that differ from other scripting languages and even other .NET
contexts. This skill documents lessons learned from real PR feedback cycles to help avoid
repeated mistakes involving regex patterns, here-string quoting, file encoding, and precise
terminology for git hooks.

## Solution

1. **Use non-greedy `.*?`** instead of `[^x]*` when content may contain the excluded character
1. **Verify file paths** in case-sensitive environments before committing
1. **Use single `"` in here-strings** - double quotes are NOT needed for escaping
1. **Use precise hook terminology** - say "runs before each commit is created" not "on every commit"
1. **Know your encoding defaults** - `WriteAllText()` uses UTF-8 without BOM
1. **`@()`-wrap captured results** before reading `.Count`/`.Length` or indexing under StrictMode 2.0+ (the 0/1/many gotcha)

## Case-Sensitive File Paths

PowerShell scripts that run fine on Windows fail on Linux due to case-sensitive file paths.
Verify paths with `git ls-files` or `Get-ChildItem` before hardcoding, and test in
case-sensitive environments (Docker, WSL) before committing.

See the [Cross-Platform Compatibility skill](./cross-platform-compatibility.md) for detailed patterns.

## See Also

- [powershell best practices part 1](./powershell-best-practices-part-1.md)
- [powershell best practices part 2](./powershell-best-practices-part-2.md)
