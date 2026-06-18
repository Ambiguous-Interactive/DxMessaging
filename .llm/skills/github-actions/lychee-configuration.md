---
title: "Lychee Link Checker Configuration Management"
id: "lychee-configuration"
category: "github-actions"
version: "1.3.0"
created: "2026-03-16"
updated: "2026-06-18"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: ".lychee.toml"
    - path: ".github/workflows/ci.yml"
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
    details: "Enforced by the pinned lychee binary (install-pinned-lychee) plus the offline/online passes in ci.yml"

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

This skill documents the field deprecation patterns observed in lychee, the version
pinning that keeps the config schema stable, and best practices for maintaining
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

### 1. Pin the lychee version

Both workflows install a pinned lychee binary (see `install-pinned-lychee`), so
`.lychee.toml` is written against one known schema (v0.24.2). When bumping the
pinned version, diff the upstream example config for added/removed/renamed
fields and update `.lychee.toml` in the same change.

### 2. Config policy

- Keep only fields valid for the pinned version; prefer the new names
  (`max_retries`, `include_mail`, `verbose`).
- Link-acceptance policy: never accept 404 or 410 (those mean the link is
  gone); always accept 403 and 429 (sites return these to CI bots even when
  the page is valid).
- `accept_timeouts` stays command-line-only (`--accept-timeouts=true` in the
  blocking workflow) so the advisory scan still reports slow hosts.

### 3. CI Workflow Integration

Both link-checking workflows share the single `.lychee.toml`. Keep the config aligned
with the pinned lychee version in both.

- **`ci.yml` / `Lint docs links`** (blocking PR/push check) runs two passes. An OFFLINE pass
  validates relative/local links and in-repo `#anchor` fragments against the working
  tree with zero network, so it can never flake. A lenient external-liveness pass then
  fails only on 404/410 or a DNS/connection error; the broad `accept` list absorbs
  bot-detection and transient codes.
- **`markdown-link-validity.yml`** (scheduled advisory scan) runs daily on a cron plus
  `workflow_dispatch`. It NEVER fails the run; it captures the lychee exit code, opens
  or updates one tracking issue from `./lychee/out.md`, and installs the pinned binary.
  Its lychee step writes Markdown to `./lychee/out.md` so the issue sync step has a
  body file on dead-link runs.

```yaml
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
# Policy: never accept 404/410; always accept 403/429.
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

### Pin and Document

When a CI tool can float, pin its effective binary version and document the target
version next to the config.

### Keep the Config Aligned With the Pinned Version

When lychee releases a new version, diff the
[lychee example config](https://github.com/lycheeverse/lychee/blob/lychee-v0.24.2/lychee.example.toml)
for added/removed/renamed fields and update `.lychee.toml` accordingly.
