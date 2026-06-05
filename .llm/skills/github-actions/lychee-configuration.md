---
title: "Lychee Link Checker Configuration Management"
id: "lychee-configuration"
category: "github-actions"
version: "1.2.0"
created: "2026-03-16"
updated: "2026-06-04"

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
    lychee: "v0.24.2"

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
versions. The repository pins the lychee binary at install time so a new lychee release
cannot silently change which config fields are valid.

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

### Pinned Binary Installer

Do not use `lycheeverse/lychee-action@v2` in active workflows. Its v0.24.2 Linux
installer expected a flat archive path while the release asset extracted the binary under
a platform directory, so the job failed before lychee ran. Active workflows must use
`./.github/actions/install-pinned-lychee` with `version: v0.24.2`, then invoke the
`lychee` CLI directly. The installer uses the musl archive, prints the archive entries,
finds the nested binary, and verifies `lychee --version`.

### Known Deprecated Field Mappings (pre-v0.23.0 to v0.23.0)

| Deprecated Field | Replacement Field | Change Type                                                          |
| ---------------- | ----------------- | -------------------------------------------------------------------- |
| `exclude_mail`   | `include_mail`    | Inverted boolean                                                     |
| `retries`        | `max_retries`     | Renamed                                                              |
| `verbosity`      | `verbose`         | Type change (string enum: "error", "warn", "info", "debug", "trace") |

## Solution

### 1. Validation Script

The `scripts/validate-lychee-config.js` script validates `.lychee.toml` against a
known-good field list for lychee v0.24.2:

```bash
# Run from repository root
node scripts/validate-lychee-config.js
```

The script:

1. Reads `.lychee.toml` and parses top-level TOML keys
   (including `[table]` and `[[array_of_tables]]` headers by extracting the top-level
   table segment)
1. Checks each key against a `VALID_FIELDS` set containing all valid v0.24.2 options
1. Validates field values where applicable (e.g., `verbose` must be one of the allowed
   string enum values: "error", "warn", "info", "debug", "trace", including when
   key-value pairs are defined inside TOML tables)
1. Requires properly paired quote boundaries before unquoting string-enum values (invalid
   quote forms like `"info` or `"info'` are rejected, not normalized)
1. Keeps v0.24.2's `accept_timeouts` in `VALID_FIELDS` but rejects it in the shared
   config because timeout acceptance is command-line-only in the blocking workflow.
1. Enforces the link-acceptance policy via `validateStrictLinkPolicy`: it FORBIDS
   accepting 404 or 410 (those mean the link is gone) and REQUIRES accepting 403 and
   429 (sites return these to CI bots even when the page is valid). This is the inverse
   of the retired rule that forbade accepting 403.
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

Both link-checking workflows share the single `.lychee.toml`. The blocking workflow
validates it before lychee; the scheduled advisory scan does not run
`validate-lychee-config.js` and relies on hooks/preflight when config changes.

- **`lint-doc-links.yml`** (blocking PR/push check) runs two passes. An OFFLINE pass
  validates relative/local links and in-repo `#anchor` fragments against the working
  tree with zero network, so it can never flake. A lenient external-liveness pass then
  fails only on 404/410 or a DNS/connection error; the broad `accept` list absorbs
  bot-detection and transient codes.
- **`markdown-link-validity.yml`** (scheduled advisory scan) runs daily on a cron plus
  `workflow_dispatch`. It NEVER fails the run; it captures the lychee exit code, opens
  or updates one tracking issue from `./lychee/out.md`, installs the pinned binary, and
  does not run the config validator. Its lychee step writes Markdown to `./lychee/out.md`
  so the issue sync step has a body file on dead-link runs.

```yaml
- name: Validate lychee configuration
  run: node scripts/validate-lychee-config.js
- name: Collect tracked docs
  run: |
    git ls-files -z '*.md' '*.markdown' | tr '\0' '\n' \
      > "${RUNNER_TEMP}/doc-files.txt"
    echo "Checking $(wc -l < "${RUNNER_TEMP}/doc-files.txt") tracked doc(s)."
- name: Install pinned lychee
  uses: ./.github/actions/install-pinned-lychee
  with:
    version: v0.24.2

# Gate 1: offline -- relative links and in-repo "#anchor" fragments (PR-blocking).
- name: Check internal links and anchors (offline)
  run: |
    lychee \
      -c .lychee.toml \
      --offline \
      --include-fragments \
      --files-from "${RUNNER_TEMP}/doc-files.txt"

# Gate 2: lenient external liveness -- only 404/410/DNS fail (accept list in config).
- name: Check external links (lenient liveness)
  run: |
    lychee \
      -c .lychee.toml \
      --scheme https \
      --scheme http \
      --accept-timeouts=true \
      --files-from "${RUNNER_TEMP}/doc-files.txt"
```

In the blocking workflow, validation runs first, so invalid config fails fast with a
clear message instead of a cryptic lychee parse failure.

## Current Valid Configuration

The `.lychee.toml` file uses these v0.24.2 field names (values mirror the live config):

```toml
verbose = "info"              # string enum ("error","warn","info","debug","trace"), not "verbosity = 1"
no_progress = true
max_concurrency = 4
include_mail = false          # inverted from "exclude_mail = true"

timeout = 20                  # seconds per request
max_retries = 2               # renamed from "retries"
retry_wait_time = 2           # seconds between retries
max_redirects = 10

# Accept every status proving the server answered EXCEPT 404/410 (gone). Widen this
# list (never add a per-domain exclude) if a site adopts a new blocking status.
# validate-lychee-config.js forbids 404/410 and requires 403/429.
accept = [
  "200..=299",
  "401",
  "403",
  "405",
  "406",
  "408",
  "415",
  "418",
  "429",
  "451",
  "999",
  "500..=599",
]

# Reserved for endpoints CI cannot reach -- NOT for sites that block bots. There is
# no exclude_path denylist: both jobs feed lychee the tracked docs via `git ls-files
# | --files-from`, so untracked vendored / generated / scratch trees are skipped.
exclude = [
  "^https?://localhost",
  "^https?://127\\.0\\.0\\.1",
  "^https?://0\\.0\\.0\\.0",
  "^https://github\\.com/Ambiguous-Interactive/DxMessaging/(?:blob|tree)/",  # validated offline
]
```

`scheme` is NOT set in the config; the two jobs pass `--scheme https --scheme http` on
the command line for the external-liveness pass only (the offline pass omits it).
`accept_timeouts` is valid in v0.24.2 but is NOT set here: the blocking workflow passes
`--accept-timeouts=true`, while the advisory scan should still report slow hosts.

## Best Practices for Tool Config Drift

### Pin and Validate

When a CI tool can float, pin its effective binary version, add a validation script that
checks config against that schema, run it before the tool in CI and hooks, and document
the target version in the script.

### Keep the Valid Field List Updated

When lychee releases a new version, diff the
[lychee example config](https://github.com/lycheeverse/lychee/blob/lychee-v0.24.2/lychee.example.toml)
for added/removed/renamed fields, update the `VALID_FIELDS` set and version comment in
`scripts/validate-lychee-config.js`, then re-run validation against `.lychee.toml`.
