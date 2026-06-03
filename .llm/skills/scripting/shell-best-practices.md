---
title: "Shell Scripting Best Practices"
id: "shell-best-practices"
category: "scripting"
version: "1.1.0"
created: "2026-01-28"
updated: "2026-01-28"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/"
    - path: ".github/workflows/"
    - path: ".husky/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "shell"
  - "bash"
  - "scripting"
  - "error-handling"
  - "ci-cd"
  - "linux"
  - "case-sensitivity"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of shell-specific behaviors and Linux filesystem semantics"

impact:
  performance:
    rating: "none"
    details: "Script patterns only; no runtime performance impact"
  maintainability:
    rating: "high"
    details: "Proper scripting patterns prevent subtle bugs in CI/CD pipelines"
  testability:
    rating: "medium"
    details: "Shell scripts should be tested but infrastructure varies"

prerequisites:
  - "Basic Bash syntax"
  - "Understanding of exit codes"
  - "Familiarity with Linux filesystems"

dependencies:
  packages: []
  skills:
    - "powershell-best-practices"
    - "git-workflow-robustness"

applies_to:
  languages:
    - "Bash"
    - "Shell"
  frameworks:
    - "GitHub Actions"
    - "Husky"
  versions:
    bash: ">=4.0"

aliases:
  - "Bash patterns"
  - "Shell patterns"
  - "Script reliability"

related:
  - "powershell-best-practices"
  - "cross-platform-compatibility"
  - "git-workflow-robustness"

status: "stable"
---

# Shell Scripting Best Practices

> **One-line summary**: Avoid common shell scripting pitfalls involving `set -e`, error handling,
> case-sensitive paths, and command exit codes.

## Overview

Shell scripts in CI/CD pipelines and git hooks must handle errors correctly and account for
Linux filesystem semantics. This skill documents lessons learned from real PR feedback to help
avoid subtle bugs that cause intermittent failures.

## `set -e` and Error Handling

### The `set -e` Contract

When a script uses `set -e` (exit on error), the script terminates immediately when any command
returns a non-zero exit code. This creates a contract: **every command that can fail must either
be intentionally fatal or have explicit error handling**.

### Problem: Comments Claiming "Optional" Without Error Handling

```bash
#!/bin/bash
set -e

# Optional: Update the cache if available
npm cache verify  # CONTRADICTION: If this fails, script exits!

# Continue with build...
npm install
```

The comment claims the cache verification is "optional," but under `set -e`, a failure
terminates the entire script. This is a **semantic contradiction**.

### Solution: Explicit Error Handling for Non-Fatal Commands

```bash
#!/bin/bash
set -e

# Optional: Update the cache if available (failure is non-blocking)
npm cache verify || true

# Alternative: Log the failure but continue
npm cache verify || echo "Cache verification skipped (non-fatal)"

# Continue with build...
npm install
```

### Error Handling Patterns

- **`cmd || true`** -- Silently ignore failure. Use for truly optional operations.
- **`cmd || echo "..."`** -- Log failure but continue. Use for optional with diagnostic output.
- **`cmd || exit 1`** -- Explicit fatal (redundant with `-e`). Use for self-documenting intent.
- **`if cmd; then ... fi`** -- Conditional execution. Use for different paths on success/fail.
- **`cmd || { ...; }`** -- Multi-statement error handling. Use for complex recovery logic.

### Validation Checklist for `set -e` Scripts

Before merging shell scripts that use `set -e`:

- [ ] Every command that can fail has been categorized as fatal or non-fatal
- [ ] Non-fatal commands have explicit error handling (`|| true`, `|| echo`, etc.)
- [ ] Comments accurately describe whether failures are blocking or non-blocking
- [ ] No contradiction between comments and actual error behavior

### Common Commands That Can Fail Unexpectedly

These commands may fail in ways that aren't obvious:

- **`grep`** -- Fails with exit code 1 when no matches found. Use `grep ... || true` or `|| echo 0`.
- **`diff`** -- Fails with exit code 1 when files differ. Use `diff ... || true` for comparison.
- **`git diff`** -- Sometimes exits 1 when no changes. Check explicitly if needed.
- **`rm file`** -- Fails if file doesn't exist. Use `rm -f file` or `rm file || true`.
- **`cd dir`** -- Fails if directory doesn't exist. Check first or use `|| exit 1`.
- **`read var`** -- Fails with exit code 1 at EOF. Handle in loop condition.

## Case-Sensitive File Paths

### Linux Filesystem Semantics

Linux filesystems (ext4, XFS, etc.) are **case-sensitive**. This means:

- `README.md` and `readme.md` are different files
- `Scripts/` and `scripts/` are different directories
- Path references in scripts must match exact case on disk

### Problem: Case Mismatch in Script References

```bash
#!/bin/bash

# BROKEN: Wrong case for directory or file
source ./Scripts/helpers.sh  # Fails if directory is actually "scripts/"
node ./SRC/build.js          # Fails if directory is actually "src/"

# BROKEN: Case mismatch in file extension checks
if [[ "$file" == *.MD ]]; then  # Won't match "readme.md"
```

### Solution: Verify and Match Exact Case

```bash
#!/bin/bash

# CORRECT: Match exact case from filesystem
source ./scripts/helpers.sh
node ./src/build.js

# CORRECT: Case-insensitive comparison when needed
if [[ "${file,,}" == *.md ]]; then  # Bash 4.0+ lowercase conversion
# Or use shopt for case-insensitive globbing:
shopt -s nocasematch
if [[ "$file" == *.md ]]; then
```

### Verification Steps

When referencing files in scripts:

1. **Use `ls` or `find` to verify exact names**: `ls -la scripts/`
1. **Use tab-completion**: Let the shell complete the path to verify case
1. **Check git for tracked names**: `git ls-files | grep -i scriptname`
1. **Test on Linux**: macOS is case-insensitive by default, masking these bugs

### Cross-Platform Considerations

| Platform       | Default Filesystem | Case Behavior    | Risk               |
| -------------- | ------------------ | ---------------- | ------------------ |
| Linux          | ext4, XFS          | Case-sensitive   | Reference standard |
| macOS          | APFS               | Case-insensitive | Masks case bugs    |
| Windows        | NTFS               | Case-insensitive | Masks case bugs    |
| WSL            | ext4               | Case-sensitive   | Same as Linux      |
| Docker (Linux) | ext4               | Case-sensitive   | CI/CD environment  |

**Best Practice**: Always develop and test path-sensitive code on Linux or in Docker to catch
case mismatches before they reach CI/CD.

## See Also

- [shell best practices part 1](./shell-best-practices-part-1.md)
