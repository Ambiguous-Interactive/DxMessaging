---
title: "Lychee Link Checker Configuration Management Part 1"
id: "lychee-configuration-part-1"
category: "github-actions"
version: "1.2.0"
created: "2026-03-16"
updated: "2026-06-04"
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

Continuation extracted from `lychee-configuration.md` to keep files within the repository line-budget policy.

## Solution

## Common Mistakes

| Mistake                                     | Problem                                                    | Fix                                                                |
| ------------------------------------------- | ---------------------------------------------------------- | ------------------------------------------------------------------ |
| Using `exclude_mail = true`                 | Deprecated in v0.23.0                                      | Use `include_mail = false`                                         |
| Using `retries = 3`                         | Renamed in v0.23.0                                         | Use `max_retries = 3`                                              |
| Using `verbosity = 1`                       | Changed to string enum in v0.23.0                          | Use `verbose = "info"` (or "error", "warn", "debug", "trace")      |
| Missing pinned installer in workflow steps  | New lychee binaries can break config silently              | Use `install-pinned-lychee` with `version: v0.24.2`                |
| Dropping advisory `out.md` generation       | Tracking issue create/update fails on dead-link runs       | Keep `--format markdown --output ./lychee/out.md` in the scan step |
| Per-domain `exclude` to silence a 403/429   | Fragile whack-a-mole; the next bot-blocked site repeats it | Widen `accept` instead; reserve `exclude` for unreachable hosts    |
| Swapping a flaky link to a "stable" domain  | The new domain blocks CI bots too (webaim -> w3 failed)    | Keep the correct URL; widen `accept` for the blocking status       |
| Accepting 404/410, or omitting 403/429      | Hides dead links, or reintroduces flaky bot-block failures | Forbid 404/410, require 403/429                                    |
| Setting `accept_timeouts` in `.lychee.toml` | Scheduled scans stop reporting persistent slow hosts       | Pass `--accept-timeouts=true` only in the blocking workflow        |

## Validation Checklist

Before modifying `.lychee.toml`:

- [ ] All field names are valid for the pinned lychee version (diff the upstream
      example config when unsure)
- [ ] `accept_timeouts` remains absent from `.lychee.toml`; timeout acceptance is CLI-only
      in `lint-doc-links.yml`
- [ ] No deprecated field names used (check the mapping table above)
- [ ] Boolean fields use `true`/`false`, not integers; `verbose` uses a string enum value

After a lychee version upgrade:

- [ ] Compare upstream example config for new/removed/renamed fields
- [ ] Update every active `install-pinned-lychee` `version` pin
- [ ] Keep scheduled scan `--output ./lychee/out.md` aligned with `gh issue --body-file`

## See Also

- [GitHub Actions Workflow Consistency](./workflow-consistency.md) -- consistent workflow
  structure and security practices
- [Link Quality and External URL Management](../documentation/link-quality-guidelines.md) --
  guidelines for maintaining documentation links
  patterns and duplicate warning prevention

## Related Links

- [Lychee Link Checker Configuration Management](./lychee-configuration.md)
