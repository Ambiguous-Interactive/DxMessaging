---
title: "Git Renormalize Pattern Validation"
id: "git-renormalize-patterns"
category: "github-actions"
version: "1.0.0"
created: "2026-01-28"
updated: "2026-01-28"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: ".github/workflows/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "github-actions"
  - "git"
  - "ci-cd"
  - "line-endings"
  - "gitattributes"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of git pathspecs, gitattributes, and CI failure modes"

impact:
  performance:
    rating: "low"
    details: "No performance impact; this is about correctness"
  maintainability:
    rating: "high"
    details: "Prevents CI failures and ensures pattern synchronization"
  testability:
    rating: "low"
    details: "Validated by actionlint and manual file existence checks"

prerequisites:
  - "Understanding of git pathspec patterns"
  - "Familiarity with .gitattributes line ending normalization"
  - "Knowledge of GitHub Actions workflow structure"

dependencies:
  packages: []
  skills:
    - "workflow-consistency"
    - "cross-platform-compatibility"

applies_to:
  languages:
    - "YAML"
  frameworks:
    - "GitHub Actions"
  versions:
    github-actions: "current"

aliases:
  - "git add renormalize"
  - "pathspec validation"
  - "line ending normalization"

related:
  - "workflow-consistency"
  - "cross-platform-compatibility"
  - "git-workflow-robustness"

status: "stable"
---

# Git Renormalize Pattern Validation

> **One-line summary**: Ensure `git add --renormalize` patterns match actual repository
> files to prevent CI failures from unmatched pathspecs.

## Overview

The `git add --renormalize` command applies `.gitattributes` normalization rules to files
matching specified patterns. This command **fails with exit code 128** when a pathspec
pattern matches zero files. This skill documents best practices for pattern validation
and synchronization across workflow sections.

## Solution

Apply these requirements when using `git add --renormalize` in CI:

1. **Use per-extension loops** (REQUIRED) - Process each extension separately with existence checks
1. **Validate patterns** match at least one file in the repository
1. **Synchronize patterns** between `git add --renormalize`, `file_pattern`, and path triggers

### Required Pattern: Per-Extension Loop

**ALWAYS** use this pattern when renormalizing multiple file types:

```bash
# REQUIRED: Per-extension loop with existence check
# yaml excluded: dotfiles match git ls-files but not git add globs
for ext in cs md json asmdef yml; do
  if git ls-files "*.$ext" "**/*.$ext" | grep -q .; then
    git add --renormalize -- "*.$ext" "**/*.$ext"
  fi
done
```

**NEVER** use single-line multi-pattern commands (fails with exit 128 if any pattern matches no files):

```bash
# FORBIDDEN: Fails if ANY pattern matches no files
git add --renormalize -- '*.md' '**/*.md' '*.json' '**/*.json'
```

## The Problem

When `git add --renormalize` is given a pattern that matches no files, it fails with exit code 128:

```bash
fatal: pathspec '*.markdown' did not match any files
```

### Dotfile Glob Behavior Difference

**CRITICAL**: `git ls-files` matches dotfiles but `git add` does not:

```bash
git ls-files "*.yaml"              # Returns: .pre-commit-config.yaml
git add --renormalize -- "*.yaml"  # FAILS: pathspec did not match any files
```

**Solution**: Exclude `yaml` from extension loops since `.yaml` files are typically dotfiles.
Use `yml` for non-dotfile YAML files.

**Generalized rule**: Exclude any extension when ALL tracked files of that extension are
dotfiles. Verify with: `git ls-files "*.$ext" "**/*.$ext"` -- if all results start with `.`,
exclude that extension.

### `file_pattern` in `git-auto-commit-action`

The same dotfile limitation applies to `file_pattern`:

```yaml
# CORRECT: Exclude yaml if all .yaml files are dotfiles
file_pattern: "**/*.md **/*.json **/*.yml"
```

## Command Behavior

| Scenario                     | Exit Code |
| ---------------------------- | --------- |
| All patterns match files     | 0         |
| Any pattern matches no files | 128       |
| `.gitattributes` rules       | 0         |

## Best Practices

### Validate Patterns Match Actual Files

```bash
# Count files by extension
git ls-files | sed 's/.*\.//' | sort | uniq -c | sort -rn | head -10

# Find all unique extensions
git ls-files | grep -E '\.[^/]+$' | sed 's/.*\./\./' | sort -u
```

### Keep Workflow Triggers Synchronized

```yaml
# GOOD: Only patterns that match existing files
on:
  pull_request:
    paths:
      - "**/*.md" # Verified: files exist
      - "**/*.json" # Verified: files exist
```

### Use Consistent Pattern Syntax

```yaml
# Preferred: Double-star for recursive matching
- "**/*.md"
- "**/*.json"
```

## Common Mistakes

```yaml
# Mistake 1: Assuming success when pattern matches nothing
- run: git add --renormalize -- '*.markdown'  # Fails if no .markdown files

# Mistake 2: Case sensitivity on Linux
git ls-files '*.MD'   # Different from '*.md' on Linux

# Mistake 3: Over-broad patterns (stages everything)
- run: git add --renormalize .  # Use per-extension loop instead
```

## Integration with Workflow Consistency

This skill complements the [Workflow Consistency skill](./workflow-consistency.md) by
ensuring pattern validation for renormalize commands and path trigger synchronization.

## Validation Checklist

- [ ] Uses per-extension loop pattern with existence checks (REQUIRED)
- [ ] All path patterns match at least one file
- [ ] Patterns consistent between `pull_request` and `pull_request_target`
- [ ] No patterns for dotfile-only extensions (e.g., `yaml`)

## Verification

Test patterns locally before committing:

```bash
# Verify file types exist
for ext in cs md json asmdef yml; do
  count=$(git ls-files "*.$ext" "**/*.$ext" | wc -l)
  echo "$ext: $count files"
done
```

Workflow files are linted by actionlint in CI.

## See Also

- [Workflow Consistency skill](./workflow-consistency.md) - General workflow structure requirements
- [Cross-Platform Compatibility skill](../scripting/cross-platform-compatibility.md) - Path handling across OSes
- [Git Workflow Robustness skill](../testing/git-workflow-robustness.md) - Git operations in CI
