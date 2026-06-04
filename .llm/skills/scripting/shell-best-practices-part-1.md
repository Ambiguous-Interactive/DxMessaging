---
title: "Shell Scripting Best Practices Part 1"
id: "shell-best-practices-part-1"
category: "scripting"
version: "1.1.0"
created: "2026-01-28"
updated: "2026-03-16"
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

Continuation material extracted from `shell-best-practices.md` to keep .llm files within the 300-line budget.

## Solution

## Exit Code Handling

### Understanding Exit Codes

Shell commands communicate success/failure through exit codes:

- `0` = success
- Non-zero = failure (specific codes vary by command)

### Problem: Assuming Success Without Checking

```bash
#!/bin/bash

# BROKEN: No error handling, continues silently on failure
wget https://example.com/file.tar.gz
tar xzf file.tar.gz
cd extracted/
./configure && make && make install
```

If any command fails, subsequent commands may operate on missing/corrupt data.

### Solution: Check Exit Codes Explicitly

```bash
#!/bin/bash
set -e  # Exit on any error

# Or check explicitly:
if ! wget https://example.com/file.tar.gz; then
    echo "Download failed" >&2
    exit 1
fi

# Or use && for command chains:
wget https://example.com/file.tar.gz && \
    tar xzf file.tar.gz && \
    cd extracted/ && \
    ./configure && make && make install
```

## Variable Quoting

### Problem: Unquoted Variables

```bash
#!/bin/bash

file="my file.txt"
rm $file        # BROKEN: Tries to remove "my" and "file.txt" separately
grep $pattern   # BROKEN: Pattern with spaces is split
cd $dir         # BROKEN: Directory with spaces fails
```

### Solution: Always Quote Variables

```bash
#!/bin/bash

file="my file.txt"
rm "$file"        # CORRECT: Removes "my file.txt"
grep "$pattern"   # CORRECT: Pattern preserved
cd "$dir"         # CORRECT: Works with spaces

# Exception: Intentional word splitting (rare, document it)
# shellcheck disable=SC2086
flags="-a -b -c"
cmd $flags  # Intentional: pass as separate arguments
```

## Testing Shell Scripts

### Manual Verification Pattern

```bash
#!/bin/bash

# Test case-sensitivity
echo "Checking file paths..."
for path in scripts/build.sh Scripts/build.sh SCRIPTS/BUILD.SH; do
    if [[ -f "$path" ]]; then
        echo "EXISTS: $path"
    else
        echo "MISSING: $path"
    fi
done

# Test error handling
echo "Testing error scenarios..."
set +e  # Temporarily disable exit-on-error
false && echo "This should not print"
nonexistent_command 2>/dev/null || echo "Command failed as expected"
set -e
```

### ShellCheck Integration

Use [ShellCheck](https://www.shellcheck.net/) to catch common issues:

```bash
# Install
apt-get install shellcheck  # Debian/Ubuntu
brew install shellcheck     # macOS

# Run on scripts
shellcheck scripts/*.sh
shellcheck .husky/*
```

## Avoid Redundant Pipe Patterns

### Problem: Unnecessary `head` with `grep -q`

The `grep -q` flag (quiet mode) already stops reading after the first match, making a preceding
`head` command redundant:

```bash
# WRONG: head -1 is redundant when using grep -q
git ls-files "*.md" | head -1 | grep -q .

# WRONG: head -N with grep -q adds no value
some_command | head -5 | grep -q "pattern"
```

### Solution: Use `grep -q` Alone

```bash
# CORRECT: grep -q already stops on first match
git ls-files "*.md" | grep -q .

# CORRECT: Use if/then for clarity in conditional logic
if git ls-files "*.$ext" "**/*.$ext" | grep -q .; then
    git add --renormalize -- "*.$ext" "**/*.$ext"
fi
```

### Why `grep -q` Is Sufficient

The `-q` (quiet) flag has two behaviors:

1. **Suppresses output**: No matches are printed to stdout
1. **Exits immediately on first match**: Does not read remaining input

This means `grep -q` is inherently optimized for existence checks. Adding `head -1` before it:

- Adds a process fork overhead
- Provides no functional benefit
- Obscures the intent of the code

### When `head` IS Appropriate

Use `head` when you need to:

- **Limit display output**: `git log --oneline | head -10` (showing last 10 commits)
- **Process only first N lines**: `cat file.txt | head -100 | process_lines`
- **Debug/diagnose**: `some_command | head -5` (see first few lines of output)

But NOT when combined with `grep -q` for existence checks.

## See Also

- [PowerShell Best Practices](./powershell-best-practices.md) - Cross-language scripting patterns
- [Git Workflow Robustness](../testing/git-workflow-robustness.md) - Git command patterns
- [Script and Automation Conventions](../../context.md#script-and-automation-conventions) - Main context file patterns

## Changelog

| Version | Date       | Changes                                         |
| ------- | ---------- | ----------------------------------------------- |
| 1.1.0   | 2026-01-28 | Added redundant pipe patterns section (PR #150) |
| 1.0.0   | 2026-01-28 | Initial version from PR feedback cycle          |

## Related Links

- [Shell Scripting Best Practices](./shell-best-practices.md)
