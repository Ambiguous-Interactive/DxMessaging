---
title: "GitHub Actions Workflow Consistency Part 1"
id: "workflow-consistency-part-1"
category: "github-actions"
version: "1.0.0"
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

Continuation material extracted from `workflow-consistency.md` to keep .llm files within the 300-line budget.

## Solution

## Path Filters

### Self-Referential Workflows

Workflows that check formatting or linting of specific file types MUST include
`.github/workflows/**` in their path filters. This ensures the workflow runs when
its own definition changes.

```yaml
on:
  pull_request:
    paths:
      - "**/*.yml"
      - "**/*.yaml"
      - ".github/workflows/**" # Critical: include workflow files
      - ".prettierrc*"
      - "package.json"
```

### Common Path Filter Patterns

| Workflow Type | Required Paths                                                                    |
| ------------- | --------------------------------------------------------------------------------- |
| YAML lint     | `**/*.yml`, `**/*.yaml`, `.github/workflows/**`, `.yamllint.yaml`, `.prettierrc*` |
| Markdown lint | `**/*.md`, `**/*.markdown`, `.markdownlint*`, `package.json`                      |
| JSON format   | `**/*.json`, `**/*.asmdef`, `**/*.asmref`, `.prettierrc*`                         |
| C# build      | `**/*.cs`, `**/*.csproj`, `**/*.sln`, `Directory.Build.props`                     |
| Tests         | Source paths + test paths + workflow config                                       |

### Trigger Best Practices

Include both `pull_request` and `push` triggers for validation workflows:

```yaml
on:
  pull_request:
    paths:
      # file patterns
  push:
    branches:
      - main
      - master
    paths:
      # same file patterns as pull_request
  workflow_dispatch: # Allow manual triggering
```

## Formatting Requirements

### Quote Style

Use **double quotes** for all strings (matches Prettier YAML defaults):

```yaml
# Correct
node-version: "22.18.0"
cache: "npm"

# Incorrect
node-version: '22.18.0'
cache: 'npm'
```

### Indentation

Use **2 spaces** for YAML indentation (not 4):

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v6
```

## Complete Workflow Template

```yaml
name: Example Workflow

on:
  pull_request:
    paths:
      - "**/*.ext"
      - ".github/workflows/**"
      - "config-file"
  push:
    branches:
      - main
      - master
    paths:
      - "**/*.ext"
      - ".github/workflows/**"
      - "config-file"
  workflow_dispatch:

concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true

permissions:
  contents: read

jobs:
  example-job:
    name: Descriptive job name
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - name: Checkout
        uses: actions/checkout@v6
        with:
          persist-credentials: false

      - name: Setup Node
        uses: actions/setup-node@v6
        with:
          node-version: "22.18.0"
          cache: "npm"
          cache-dependency-path: package.json

      - name: Install dependencies
        run: |
          if [ -f package-lock.json ]; then
            npm ci
          else
            npm i --no-audit --no-fund
          fi

      - name: Run checks
        run: npm run check
```

## Common Mistakes

| Mistake                                 | Problem                                      | Fix                                               |
| --------------------------------------- | -------------------------------------------- | ------------------------------------------------- |
| Missing `.github/workflows/**` in paths | Workflow won't run when its own file changes | Add to path filters                               |
| Missing concurrency group               | Duplicate runs waste resources               | Add concurrency block                             |
| Missing permissions block               | Implicit permissions are too broad           | Declare explicit minimal permissions              |
| Missing checkout credential policy      | Git credentials persist by default           | Add explicit `persist-credentials: false`         |
| Missing `timeout-minutes`               | Jobs can run indefinitely                    | Add timeout to every job                          |
| Single quotes for strings               | Inconsistent with Prettier                   | Use double quotes                                 |
| Wrong property order                    | Hard to review, fails formatting             | Use: name > on > concurrency > permissions > jobs |

## Validation Checklist

Before committing a workflow, verify:

- [ ] Properties ordered: `name` to `on` to `concurrency` to `permissions` to `jobs`
- [ ] Concurrency group defined with `cancel-in-progress: true`
- [ ] Explicit `permissions` block with minimal required permissions
- [ ] Every job has `timeout-minutes`
- [ ] Checkout steps declare `persist-credentials`; use `false`; auto-commit
      handoffs clean up tokenized remotes immediately
- [ ] Path filters include `.github/workflows/**` for self-referential checks
- [ ] Double quotes used for strings
- [ ] 2-space indentation throughout

## See Also

- [Git Renormalize Pattern Validation](./git-renormalize-patterns.md) -- ensuring pathspec patterns
  match actual repository files to prevent CI failures
- [Cross-Platform Compatibility](../scripting/cross-platform-compatibility.md) -- handling platform
  differences in CI scripts
- [Shell Best Practices](../scripting/shell-best-practices.md) -- patterns for shell commands in
  workflow steps

## Related Links

- [GitHub Actions Workflow Consistency](./workflow-consistency.md)
