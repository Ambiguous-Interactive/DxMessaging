---
title: "Lychee Link Checker Configuration Management"
id: "lychee-configuration"
category: "github-actions"
version: "1.1.0"
created: "2026-03-16"
updated: "2026-03-16"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: ".lychee.toml"
    - path: "scripts/validate-lychee-config.js"
    - path: ".github/workflows/lint-doc-links.yml"
    - path: ".github/workflows/markdown-link-validity.yml"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "github-actions"
  - "ci-cd"
  - "lychee"
  - "link-checking"
  - "configuration"
  - "validation"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of TOML configuration, lychee versioning, and CI pipeline integration"

impact:
  performance:
    rating: "low"
    details: "Configuration validation is fast; impact is on CI reliability rather than performance"
  maintainability:
    rating: "high"
    details: "Prevents silent CI failures from deprecated config fields across lychee upgrades"
  testability:
    rating: "medium"
    details: "Validation script has unit tests and runs in both pre-push hooks and CI"

prerequisites:
  - "Understanding of TOML configuration format"
  - "Familiarity with lychee link checker"
  - "Knowledge of GitHub Actions workflow structure"
  - "Understanding of semantic versioning and floating version tags"

dependencies:
  packages: []
  skills:
    - "workflow-consistency"

applies_to:
  languages:
    - "TOML"
    - "JavaScript"
  frameworks:
    - "GitHub Actions"
    - "lychee"
  versions:
    lychee: ">=0.23.0"

aliases:
  - "lychee config validation"
  - "link checker configuration"
  - "dead link checker setup"

related:
  - "workflow-consistency"
  - "link-quality-guidelines"
  - "validation-patterns"

status: "stable"
---

# Lychee Link Checker Configuration Management

> **One-line summary**: Validate `.lychee.toml` configuration fields against the target
> lychee version to prevent CI breakage from deprecated or renamed options.

## Overview

Lychee is a fast link checker used in CI to validate URLs across documentation and source
files. Its configuration lives in `.lychee.toml`, but field names can change between major
versions. Because `lycheeverse/lychee-action@v2` uses a **floating major version tag**,
a new lychee release can ship at any time and silently break CI if the config contains
deprecated fields.

This skill documents the field deprecation patterns observed in lychee, the validation
tooling built to catch these issues proactively, and best practices for maintaining
third-party tool configurations that can drift.

## Problem Statement

### How Config Fields Become Invalid

When lychee upgrades from one version to the next, configuration field names may be:

- **Renamed** for clarity (e.g., `retries` became `max_retries`)
- **Inverted** in semantics (e.g., `exclude_mail` became `include_mail`)
- **Changed in type** (e.g., `verbosity` as an integer became `verbose` as a string enum)
- **Removed entirely** when features are dropped

Lychee treats unknown fields as hard errors, so any deprecated field causes an immediate
CI failure with an unhelpful error message.

### Floating Version Tags

The `lycheeverse/lychee-action@v2` action uses a floating major version tag. This means:

- You pin to `@v2` for stability across minor/patch updates
- A new minor or patch release can change which config fields are valid
- There is no advance warning; CI simply starts failing

### Known Deprecated Field Mappings (pre-v0.23.0 to v0.23.0)

| Deprecated Field | Replacement Field | Change Type                                                          |
| ---------------- | ----------------- | -------------------------------------------------------------------- |
| `exclude_mail`   | `include_mail`    | Inverted boolean                                                     |
| `retries`        | `max_retries`     | Renamed                                                              |
| `verbosity`      | `verbose`         | Type change (string enum: "error", "warn", "info", "debug", "trace") |

## Solution

### 1. Validation Script

The `scripts/validate-lychee-config.js` script validates `.lychee.toml` against a
known-good field list for lychee v0.23.0:

```bash
# Run from repository root
node scripts/validate-lychee-config.js
```

The script:

1. Reads `.lychee.toml` and parses top-level TOML keys
   (including `[table]` and `[[array_of_tables]]` headers by extracting the top-level
   table segment)
1. Checks each key against a `VALID_FIELDS` set containing all valid v0.23.0 options
1. Validates field values where applicable (e.g., `verbose` must be one of the allowed
   string enum values: "error", "warn", "info", "debug", "trace", including when
   key-value pairs are defined inside TOML tables)
1. Requires properly paired quote boundaries before unquoting string-enum values (invalid
   quote forms like `"info` or `"info'` are rejected, not normalized)
1. Reports errors for any unrecognized fields
1. Reports warnings for duplicate fields
1. Exits with code 1 on validation failure

When a field is invalid, the script prints the full list of valid fields, making it
straightforward to find the correct replacement.

### 2. Git Hook Integration

The validation runs in both `pre-commit` and `pre-push` via `.pre-commit-config.yaml`:

```yaml
- repo: local
  hooks:
    - id: validate-lychee-config
      name: Validate lychee configuration
      entry: node scripts/validate-lychee-config.js
      language: system
      pass_filenames: false
      files: '^\.lychee\.toml$'
      stages:
        - pre-commit
        - pre-push
```

Key design decisions:

- **Runs on both commit and push**: Catches config errors early (`pre-commit`) while still
  enforcing at push boundaries (`pre-push`)
- **File filter**: Only triggers when `.lychee.toml` is in the changeset
- **Non-interactive**: Uses `pass_filenames: false` since the script finds the config itself

### 3. CI Workflow Integration

Both link-checking workflows validate the config before running lychee:

```yaml
- name: Validate lychee configuration
  run: node scripts/validate-lychee-config.js

- name: Check dead links (lychee)
  uses: lycheeverse/lychee-action@v2
  with:
    args: >-
      -c .lychee.toml
      --no-progress
      --include-fragments
      --verbose
```

The validation step runs first, so if the config is invalid, the workflow fails fast
with a clear error message instead of a cryptic lychee parse failure.

## Current Valid Configuration

The `.lychee.toml` file should use these field names (v0.23.0):

```toml
verbose = "info"              # string enum ("error","warn","info","debug","trace"), not "verbosity = 1"
no_progress = true
max_concurrency = 4
include_mail = false          # inverted from "exclude_mail = true"

timeout = 20                  # seconds per request
max_retries = 3               # renamed from "retries"
retry_wait_time = 2           # seconds between retries
max_redirects = 10
user_agent = "..."

accept = ["200..=299", 429, 502]
scheme = ["https", "http"]

exclude = [
  "^https?://localhost",
  # ... exclusion patterns
]
```

## Best Practices for Tool Config Drift

### Pin and Validate

When a CI tool uses floating version tags:

1. **Add a validation script** that checks config against the current version's schema
1. **Run validation before the tool** in CI so failures are clear
1. **Add validation to git hooks** so developers catch issues before pushing
1. **Document the target version** in the validation script comments

### Keep the Valid Field List Updated

When lychee releases a new version:

1. Check the [lychee example config](https://github.com/lycheeverse/lychee/blob/master/lychee.example.toml)
   for the current valid field list
1. Update the `VALID_FIELDS` set in `scripts/validate-lychee-config.js`
1. Update the version comment in the script
1. Run the validation against the existing `.lychee.toml`

### Test the Validation Script

Unit tests exist at `scripts/__tests__/validate-lychee-config.test.js`. When updating
the valid field list, also update test expectations.

## See Also

- [lychee configuration part 1](./lychee-configuration-part-1.md)
