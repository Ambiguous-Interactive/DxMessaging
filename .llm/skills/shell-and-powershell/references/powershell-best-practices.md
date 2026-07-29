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
