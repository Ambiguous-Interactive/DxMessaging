---
title: "PowerShell Scripting Best Practices Part 2"
id: "powershell-best-practices-part-2"
category: "scripting"
version: "1.1.0"
created: "2026-01-27"
updated: "2026-05-21"
status: "stable"
tags:
  - migration
  - split
complexity:
  level: "intermediate"
impact:
  performance:
    rating: "low"
---

## Overview

Continuation material extracted from `powershell-best-practices.md` to keep .llm files within the 300-line budget.

## Solution

## Pre-Commit Hook Terminology

Precise terminology prevents confusion about when hooks execute.

### Problem: Misleading "On Every Commit" Phrasing

```powershell
# MISLEADING comment:
# This script runs on every commit to validate line endings
```

This phrasing suggests the script runs _after_ commits are created, which is incorrect for
pre-commit hooks.

### Solution: Precise Timing Language

```powershell
# CORRECT: Precise timing
# This script runs before each commit is created (pre-commit hook)

# ALSO CORRECT: Alternative phrasing
# This pre-commit hook validates line endings before allowing commits
```

### Terminology Reference

| Phrase                               | Meaning                            | Correct For       |
| ------------------------------------ | ---------------------------------- | ----------------- |
| "runs before each commit is created" | Executes prior to commit creation  | pre-commit hooks  |
| "runs as a pre-commit hook"          | Explicitly names the hook type     | pre-commit hooks  |
| "runs after each commit"             | Executes after commit is finalized | post-commit hooks |
| "runs on every commit"               | Ambiguous -- avoid this phrasing   | Neither           |
| "runs when commits are pushed"       | Executes during push operation     | pre-push hooks    |

## .NET File Encoding Behaviors

PowerShell's file operations have different encoding defaults than older cmdlets.

### WriteAllText Default Encoding

The .NET method `[System.IO.File]::WriteAllText()` uses UTF-8 **without BOM** by default:

```powershell
# This writes UTF-8 WITHOUT BOM (no byte order mark)
[System.IO.File]::WriteAllText($path, $content)

# Explicitly equivalent:
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($path, $content, $utf8NoBom)
```

### Common Confusion Sources

| Method                                   | Default Encoding | BOM     |
| ---------------------------------------- | ---------------- | ------- |
| `[System.IO.File]::WriteAllText()`       | UTF-8            | BOM     |
| `Set-Content` (PS 5.1)                   | System default   | Varies  |
| `Set-Content -Encoding UTF8` (PS 5.1)    | UTF-8            | Has BOM |
| `Set-Content -Encoding utf8NoBOM` (PS 7) | UTF-8            | BOM     |
| `Out-File` (PS 5.1)                      | UTF-16 LE        | Has BOM |

### Anti-Pattern: "Fixing" Correct Code

```powershell
# ALREADY CORRECT: Writes UTF-8 without BOM
[System.IO.File]::WriteAllText($path, $content)

# WRONG "FIX": Adding BOM when not needed
$utf8WithBom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($path, $content, $utf8WithBom)
```

Before "fixing" encoding code, verify the actual behavior matches requirements.

## StrictMode Collection Safety (the 0/1/many gotcha)

Capturing a possibly-empty command or function result and then reading `.Count`
/ `.Length`, or indexing it, is a latent crash (verified on pwsh 7.6.1, the CI
runtime). Reading `.Count` / `.Length` throws under `Set-StrictMode -Version` 2.0
or higher; only `-Version 1.0` (or Off) avoids that throw. Indexing throws at
EVERY StrictMode level. This burned a real CI run with the error:
`The property 'Count' cannot be found on this object.`

### The Rule: 0, 1, or Many

PowerShell collapses pipeline output by count:

- ZERO objects -> `$null`
- ONE object -> the scalar itself (NOT a 1-element array)
- TWO or more -> an array

A function that `return @()` on its empty path emits ZERO objects. Capturing it
with a bare assignment therefore stores AutomationNull (it compares equal to
`$null`), because the empty array unwraps to nothing:

```powershell
function Get-Args {
    if (-not $Endpoint) { return @() }   # emits ZERO objects
    return @('-a', $Endpoint)
}

# BROKEN under StrictMode 2.0+: $argv is AutomationNull when Get-Args took the empty path
$argv = Get-Args
if ($argv.Count -gt 0) { ... }           # .Count THROWS under 2.0+: "property 'Count' cannot be found"
$first = $argv[0]                          # indexing THROWS at EVERY level: "Cannot index into a null array"
$all = @('-x') + $argv                     # fine: + DROPS the AutomationNull capture, no element added
```

The `+` concatenation was never the failure here: `+` DROPS an AutomationNull
operand, so no element is added. A LITERAL `$null` operand is different
-- `@('-x') + $null` ADDS a spurious element -- but a captured empty result is
AutomationNull, not a literal `$null`, so the only real bug above is the
`.Count` read (and any indexing).

### The Fix: Always `@()`-Wrap the Capture at the Source

```powershell
# CORRECT: @() forces an array. Count 0 when empty; indexing is safe too.
$argv = @(Get-Args)
if ($argv.Count -gt 0) { ... }            # safe: 0 when empty
$first = if ($argv.Count) { $argv[0] }     # safe: no index into $null
```

Use `@(...)`, not the unary comma. `,Get-Args` is a parse error, and `,(Get-Args)`
wraps the result in a one-element array (Count 1 even when the call returned
nothing), so only `@(...)` yields the correct 0/1/many counts.

### Note: Reading `.Count`/`.Length` Throws Under StrictMode 2.0+ on PowerShell 7; Indexing Throws at EVERY Level

Two distinct failure modes apply to an empty capture (AutomationNull), verified
on pwsh 7.6.1 (the CI runtime):

- `.Count` / `.Length`: under `Set-StrictMode -Version` 2.0 and every higher
  level (including `Latest`) reading the property throws "The property 'Count'
  cannot be found on this object." Only `-Version 1.0` (or Off) avoids the throw
  -- and under 1.0 it returns the integer `0` (the synthetic count), NOT `$null`.
  Windows PowerShell 5.1 does not start throwing until 3.0, but treat 2.0+ as
  unsafe because CI runs pwsh 7.
- Indexing (`$x[0]` / `$x[-1]`): throws "Cannot index into a null array" at
  EVERY StrictMode level (including 1.0 and Off). Indexing-null is not
  StrictMode-gated, so a lax version never masks it.

The `.Count`/`.Length` throw hides under `-Version 1.0` until someone raises the
StrictMode version, so always wrap; do not rely on a lax StrictMode version
masking it. Indexing is never masked.

### Enforcement

This category is locked by two regression guards: an end-to-end smoke test that
runs the real script via pwsh under its StrictMode through the empty-collection
path, and a precise static guard that flags a bare capture of a locally-defined
function whose `.Count`/`.Length` or indexing (`$x[...]`) is later read (cmdlets
and guaranteed collections are not flagged; `.Count`/index text inside quoted
strings or here-strings is ignored; an inline suppression comment opts out a
reviewed exception).

## Validation Checklist

Before merging PowerShell scripts:

- [ ] Regex patterns use `.*?` not `[^x]*` when content may contain excluded character
- [ ] Documentation comments don't contain literal matched content
- [ ] Here-strings use single `"` not doubled `""`
- [ ] Pre-commit hooks use precise timing terminology
- [ ] File encoding matches project requirements (usually UTF-8 no BOM)
- [ ] Variable expansions in here-strings are intentional
- [ ] Captured command/function results are `@()`-wrapped before reading `.Count`/`.Length` (throws under StrictMode 2.0+) or indexing (throws at every StrictMode level)

## Testing PowerShell Scripts

Test regex patterns with edge cases before committing:

```powershell
$testCases = @('<!-- simple -->', '<!-- has > inside -->', '<!-- multiple >> chars -->')
foreach ($case in $testCases) {
    Write-Host "Input: $case - Match: $($case -match $pattern)"
}
```

PowerShell scripts are covered by the node --test suite (`npm test`), which spawns `pwsh` where needed.

## See Also

- [Cross-Platform Compatibility](./cross-platform-compatibility.md) - Case sensitivity patterns
- [Git Workflow Robustness](../testing/git-workflow-robustness.md) - Git command patterns
- [Script and Automation Conventions](../../context.md#script-and-automation-conventions) - Main context file patterns

## Changelog

| Version | Date       | Changes                                            |
| ------- | ---------- | -------------------------------------------------- |
| 1.0.0   | 2026-01-27 | Initial version from PR feedback cycle             |
| 1.1.0   | 2026-05-21 | Add StrictMode collection-safety (0/1/many) gotcha |

## Related Links

- [PowerShell Scripting Best Practices](./powershell-best-practices.md)
