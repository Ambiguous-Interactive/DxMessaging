---
title: "GitHub Actions Workflow Consistency"
id: "workflow-consistency"
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
  - "ci-cd"
  - "workflow"
  - "security"
  - "consistency"
  - "yaml"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of GitHub Actions features and security best practices"

impact:
  performance:
    rating: "medium"
    details: "Concurrency controls prevent resource waste from duplicate runs"
  maintainability:
    rating: "high"
    details: "Consistent structure makes workflows easier to review and modify"
  testability:
    rating: "medium"
    details: "Proper path filters ensure workflows run when needed"

prerequisites:
  - "Understanding of GitHub Actions workflow syntax"
  - "Familiarity with YAML formatting"
  - "Knowledge of security best practices for CI/CD"

dependencies:
  packages: []
  skills:
    - "cross-platform-compatibility"

applies_to:
  languages:
    - "YAML"
  frameworks:
    - "GitHub Actions"
  versions:
    github-actions: "current"

aliases:
  - "Workflow standards"
  - "CI/CD consistency"
  - "Actions best practices"

related:
  - "cross-platform-compatibility"
  - "shell-best-practices"
  - "git-renormalize-patterns"

status: "stable"
---

# GitHub Actions Workflow Consistency

> **One-line summary**: Ensure all GitHub Actions workflows follow consistent structure,
> security practices, and formatting to maintain reliability and reviewability.

## Overview

GitHub Actions workflows in this project must follow strict conventions for structure,
security, and formatting. This skill documents the required patterns to ensure all workflows
are consistent, secure, and maintainable.

## Solution

Apply these requirements to every workflow file:

1. Use consistent property ordering: `name` -> `on` -> `concurrency` -> `permissions` -> `jobs`
1. Always include a concurrency group with `cancel-in-progress: true`
1. Declare explicit minimal permissions
1. Set `timeout-minutes` on every job
1. Use `persist-credentials: false` on checkout steps (unless pushing)
1. Include `.github/workflows/**` in path filters for self-referential workflows
1. Use double quotes for strings (Prettier default)

## Required Property Order

All workflow files MUST use this exact property ordering at the top level:

```yaml
name: Workflow Name

on:
  # triggers

concurrency:
  # concurrency settings

permissions:
  # permission declarations

jobs:
  # job definitions
```

### Why Order Matters

1. **Readability**: Consistent ordering makes workflows scannable
1. **Review efficiency**: Reviewers know where to find specific sections
1. **Prettier compatibility**: Maintains formatting after auto-formatting

## Required Elements

Every workflow MUST include these elements:

### 1. Concurrency Group

Prevents duplicate workflow runs and cancels in-progress runs when new commits are pushed:

```yaml
concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true
```

### 2. Explicit Permissions

Always declare the minimum required permissions. Default to read-only:

```yaml
# Read-only access (most workflows)
permissions:
  contents: read

# For workflows that push changes
permissions:
  contents: write
  pull-requests: write
```

**Never omit permissions** -- implicit permissions are overly broad.

### 3. Job Timeout

Every job MUST have a `timeout-minutes` to prevent runaway jobs:

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    timeout-minutes: 10 # Required on every job
```

Recommended timeouts:

- **Lint/format checks**: 5 minutes
- **Build/compile**: 15-30 minutes
- **Full test suites**: 30-60 minutes
- **Deployment**: 10-15 minutes

### 4. Secure Checkout

Use `persist-credentials: false` on checkout steps unless credentials are explicitly needed:

```yaml
- name: Checkout
  uses: actions/checkout@v6
  with:
    persist-credentials: false
```

Only omit this when the workflow needs to push commits (e.g., auto-fix workflows).

## See Also

- [workflow consistency part 1](./workflow-consistency-part-1.md)
