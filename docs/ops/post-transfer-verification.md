---
title: Post-Transfer Verification
description: End-to-end checklist after moving repository and release ownership
---

# Post-Transfer Verification

Run this after GitHub transfer, npm Trusted Publishing setup, OpenUPM metadata
updates, and Unity Asset Store account checks.

## Repository

1. Clone from `git@github.com:Ambiguous-Interactive/DxMessaging.git` or the
   HTTPS equivalent.
1. Run `git fetch --all --tags --prune`.
1. Confirm default branch, tags, release drafts, and protected branches.
1. Confirm maintainers can open and review pull requests.
1. Confirm stale links are gone:

```bash
git grep -i "wallstop-studios/DxMessaging"
```

The grep should return no tracked matches.

## Validation

Run from a clean tracked state:

```bash
npm test
npm run validate:all
npm pack --json --dry-run --ignore-scripts
```

## CI

1. Trigger Unity Tests manually for one Unity version and one mode (the
   `standalone` mode covers IL2CPP via the native game-ci `testMode: standalone`
   player build).
1. Trigger Unity Benchmarks only when runner capacity is available.
1. Open a same-repository pull request and confirm licensed Unity checks run.
1. Open or simulate a fork pull request and confirm licensed Unity checks skip.
1. Confirm GitHub-hosted checks still run for fork PRs.

## Release Dry Run

There is no release dry-run workflow. Use local validation before tagging:

```bash
npm pack --json --dry-run --ignore-scripts
```

Do not push a real `vX.Y.Z` tag until npm Trusted Publishing, runner access,
GitHub Release permissions, and OpenUPM metadata are all verified.

## Public Distribution

After the first Ambiguous release:

1. GitHub Release contains `.tgz` and `.sha256` assets.
1. npm page shows the new version and provenance.
1. OpenUPM page shows the new version.
1. Git URL install works from a clean Unity project.
1. npm scoped registry install works from a clean Unity project.
1. Documentation site resolves at
   `https://ambiguous-interactive.github.io/DxMessaging/`.
1. README badges point at `Ambiguous-Interactive/DxMessaging`.
1. Unity Asset Store public listing URL is recorded locally, if applicable.

## Local Runbook Closeout

In `.operator-runbooks/ambiguous-release-setup.md`, record only:

- public URLs
- public PR or issue links
- dates when public verification was completed
- non-sensitive next actions

Do not record secrets, account screenshots, publisher identifiers, recovery
codes, private reviewer messages, or private account metadata.
