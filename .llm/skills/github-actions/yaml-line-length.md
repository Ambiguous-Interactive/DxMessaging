---
title: "YAML Line-Length Budget"
id: "yaml-line-length"
category: "github-actions"
version: "1.0.0"
created: "2026-05-19"
updated: "2026-05-19"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: ".yamllint.yaml"
    - path: "scripts/lib/yaml-line-length.js"
    - path: "scripts/fix-yaml-comments-line-length.js"
    - path: "scripts/fix-yaml-block-scalar-line-length.js"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "github-actions"
  - "yaml"
  - "yamllint"
  - "line-length"
  - "powershell"
  - "ci-cd"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of yamllint exemptions and block-scalar context"

impact:
  performance:
    rating: "low"
    details: "Auto-fixers run on staged YAML; cost is one Node process per commit"
  maintainability:
    rating: "high"
    details: "Keeps workflow YAML inside one budget so hooks stay a last resort"
  testability:
    rating: "medium"
    details: "Check-mode CLIs and a faithful yamllint port make drift detectable"

prerequisites:
  - "Understanding of GitHub Actions workflow and composite-action YAML"
  - "Familiarity with yamllint line-length rule semantics"
  - "Basic PowerShell string-literal syntax"

dependencies:
  packages: []
  skills:
    - "workflow-consistency"

applies_to:
  languages:
    - "YAML"
    - "PowerShell"
  frameworks:
    - "GitHub Actions"
  versions:
    yamllint: "1.38.0"

aliases:
  - "YAML 200-char limit"
  - "yamllint line-length"
  - "Long run block fix"

related:
  - "workflow-consistency"
  - "cross-platform-compatibility"

status: "stable"
---

# YAML Line-Length Budget

> **One-line summary**: Keep every YAML line at or under 200 characters by
> auto-wrapping comments, auto-rewriting long pwsh `run:` strings, or
> externalizing the script, so the yamllint hook stays a last resort.

## Overview

Every tracked `.yml`/`.yaml` file in this repository is held to a 200-character
line-length ceiling. The single source of truth is `.yamllint.yaml`:

```yaml
line-length:
  max: 200
  allow-non-breakable-words: true
  allow-non-breakable-inline-mappings: true
```

The same budget applies to `.github/workflows/**` AND `.github/actions/**`
composite-action files. The dominant offender is code embedded in a `run:`
block scalar: a single long PowerShell string (for example a
`Write-Output "::error title=... long message ..."`) exceeds 200 columns and
yamllint flags it. Because that code is not prose, a generic comment wrapper
cannot touch it, so historically it fell through to the last-resort yamllint
hook and broke commits.

## Solution

Use the remediation ladder below. Each rung is automated where it can be done
safely; the rest is shorten-or-externalize.

### (a) Long `#` comments: auto-wrap

`npm run format:yaml:comments` (write) and `npm run check:yaml:comments`
(check) wrap breakable `#` comment lines to the ceiling. The pre-commit hook
`fix-yaml-comments-line-length` runs this on staged YAML before yamllint, so a
wrappable over-length comment never reaches yamllint.

### (b) Long PowerShell strings in pwsh `run:` blocks: auto-rewrite

`npm run format:yaml:lines` (write) and `npm run check:yaml:lines` (check)
rewrite a long PowerShell double-quoted string literal into a parenthesized
multi-line `+` concatenation that reproduces the identical runtime string:

```yaml
- shell: pwsh
  run: |
    Write-Output ("::error title=Long::first part of the message " +
    "second part of the message that would otherwise overflow")
```

The rewrite is PowerShell 5.1-safe (only the binary `+` operator; no ternary,
null-coalescing, or here-strings). It applies ONLY inside a `run:` block scalar
whose step has an explicit `shell: pwsh` or `shell: powershell`, and ONLY to a
line of the shape `<indent><code>"<string>"`. Split points are plain literal
spaces outside any `$(...)`/`${...}` subexpression and never a backtick-escaped
space, so interpolation tokens such as `$env:RUNNER_NAME` stay intact and the
result is byte-identical. The pre-commit hook
`fix-yaml-block-scalar-line-length` runs this on staged YAML before yamllint.

### (c) Everything else: shorten or externalize

Any other over-length line (bash `run:` code, a folded `>-` prose body, a plain
mapping value, or a pwsh line that is not the safe shape) is left
byte-identical and reported for manual remediation. Shorten the line, or move
the script body into a versioned file invoked from the workflow. Open-source
PowerShell (`pwsh`) is the repository convention; `.js` and `.sh` are also fine:

```yaml
- shell: pwsh
  run: pwsh -NoProfile -File scripts/ci/emit-error.ps1 -Title "Long"
```

Externalizing also makes the script unit-testable and removes it from the
yamllint budget entirely.

## Why The Exemptions Exist

The two `allow-*` settings come from this repo's `.yamllint.yaml`, not yamllint's
upstream defaults (`max: 80`, inline-mappings off), and are why some lines aren't flagged:

- `allow-non-breakable-words: true` exempts a line whose over-length remainder
  (after leading spaces, and after a leading `#` or `-`) contains no space.
  A single unbreakable token, such as a long URL, has nowhere to wrap.
- `allow-non-breakable-inline-mappings: true` exempts a `key: value` line whose
  value has no internal space, even past column 200. This is why long
  `files: '<regex>'` filter lines in `.pre-commit-config.yaml` and in workflow
  steps are allowed: the regex value is a single non-breakable token.

The Node port in `scripts/lib/yaml-line-length.js` reproduces these exemptions
exactly, so the auto-fixers and check-mode never touch a line yamllint would
exempt. Real yamllint (pre-commit + CI) remains the authoritative final gate.

## See Also

- [GitHub Actions Workflow Consistency](./workflow-consistency.md)
- [Cross-Platform Script Compatibility](../scripting/cross-platform-compatibility.md)
