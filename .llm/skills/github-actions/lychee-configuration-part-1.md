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
| Skipping validation in CI                   | Config errors surface as cryptic lychee failures           | Add validation step before lychee-action                           |
| Not updating VALID_FIELDS after lychee bump | New valid fields flagged as errors                         | Sync the set with upstream example config                          |
| Missing `lycheeVersion` in workflow steps   | New lychee binaries can break config silently              | Pin every active lychee-action step to `v0.24.2`                   |
| Ignoring TOML table headers in validators   | Invalid table-based config bypasses validation             | Parse `[table]` and `[[array]]` headers as top-level fields        |
| Reading config files at test module scope   | Jest can fail during test collection with poor context     | Read files in `beforeAll` with an existence guard                  |
| Per-domain `exclude` to silence a 403/429   | Fragile whack-a-mole; the next bot-blocked site repeats it | Widen `accept` instead; reserve `exclude` for unreachable hosts    |
| Swapping a flaky link to a "stable" domain  | The new domain blocks CI bots too (webaim -> w3 failed)    | Keep the correct URL; widen `accept` for the blocking status       |
| Accepting 404/410, or omitting 403/429      | Hides dead links, or reintroduces flaky bot-block failures | Follow `validateStrictLinkPolicy`: forbid 404/410, require 403/429 |
| Setting `accept_timeouts` in `.lychee.toml` | Scheduled scans stop reporting persistent slow hosts       | Pass `--accept-timeouts=true` only in the blocking workflow        |

Additional parser guard: malformed quoted values such as `"info` or `"info'`
must be treated as invalid and rejected unless opening/closing quotes are present
and use the same quote character.

## Validation Checklist

Before modifying `.lychee.toml`:

- [ ] All field names are in the `VALID_FIELDS` set in `validate-lychee-config.js`
- [ ] `accept_timeouts` remains absent from `.lychee.toml`; timeout acceptance is CLI-only
      in `lint-doc-links.yml`
- [ ] No deprecated field names used (check the mapping table above)
- [ ] Boolean fields use `true`/`false`, not integers; `verbose` uses a string enum value
- [ ] Validation script passes: `node scripts/validate-lychee-config.js`
- [ ] Unit tests pass: `npx jest scripts/__tests__/validate-lychee-config.test.js`

After a lychee version upgrade:

- [ ] Compare upstream example config for new/removed/renamed fields
- [ ] Update `VALID_FIELDS` in `validate-lychee-config.js`
- [ ] Update version comment in the script
- [ ] Update every active lychee-action `lycheeVersion` pin
- [ ] Run validation against existing `.lychee.toml`
- [ ] Update unit tests if field list changed

## See Also

- [GitHub Actions Workflow Consistency](./workflow-consistency.md) -- consistent workflow
  structure and security practices
- [Link Quality and External URL Management](../documentation/link-quality-guidelines.md) --
  guidelines for maintaining documentation links
- [Validation Patterns](../scripting/validation-patterns.md) -- general validation script
  patterns and duplicate warning prevention

## Related Links

- [Lychee Link Checker Configuration Management](./lychee-configuration.md)
