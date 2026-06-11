---
title: GitHub Transfer
description: Manual steps for moving DxMessaging to Ambiguous-Interactive
---

# GitHub Transfer

Use this checklist when transferring or verifying the repository under
`Ambiguous-Interactive/DxMessaging`.

## Before Transfer

1. Confirm the target organization has an owner who can accept transferred
   repositories.
1. Confirm the target repository name remains `DxMessaging`.
1. Confirm the package ID remains `com.wallstop-studios.dxmessaging`.
1. Record public state in the local ignored runbook:
   - current default branch
   - latest version tag
   - required workflow names
   - public package pages
1. Do not record personal access tokens, recovery codes, private emails,
   screenshots, or publisher account IDs.

## Transfer

1. Transfer the repository through GitHub repository settings.
1. Accept the transfer in the `Ambiguous-Interactive` organization.
1. Confirm the repository URL is
   `https://github.com/Ambiguous-Interactive/DxMessaging`.
1. Confirm existing tags and GitHub Releases are still visible.
1. Confirm old links redirect, but do not rely on redirects in tracked files.

GitHub states that webhooks, services, secrets, and deploy keys remain
associated with a transferred repository. Treat that as a starting point, not a
verification result. Recheck every automation binding after the transfer.

## Repository Identity Surfaces

After transfer, verify each surface points at the canonical repository:

- `package.json`:
  - `documentationUrl`
  - `changelogUrl`
  - `licensesUrl`
  - `repository.url`
  - `bugs.url`
  - `homepage`
- `mkdocs.yml`:
  - `site_url`
  - `repo_name`
  - `repo_url`
  - `pymdownx.magiclink.user`
  - `pymdownx.magiclink.repo`
- README badges and install URLs
- docs source links and GitHub sample links
- `.github/workflows/release-drafter.yml` repository guard
- `.github/release-drafter.yml` tag template
- `.github/dependabot.yml` assignees, reviewers, and ownership routing
- `scripts/update-llms-txt.js` and generated `llms.txt`
- `scripts/wiki/generate-wiki-sidebar.js`
- OpenUPM metadata
- npm Trusted Publishing binding

Run:

```bash
git grep -i "wallstop-studios/DxMessaging"
npm run check:llms-txt
```

The grep should return no tracked matches.

## Local Git Remotes

Update local remotes after transfer:

```bash
git remote set-url origin git@github.com:Ambiguous-Interactive/DxMessaging.git
git fetch --all --tags --prune
git remote -v
```

Use HTTPS instead of SSH if that is the maintainer's normal GitHub setup.

## Failure Modes

- GitHub Pages still publishes from the old organization URL.
- README badges point at the old repository.
- npm Trusted Publishing remains bound to the old repository.
- OpenUPM metadata still indexes the old repository.
- Dependabot still assigns or requests review from old-owner accounts.
- Release Drafter creates unprefixed tags while release workflow expects
  `vX.Y.Z`.
- Maintainers rely on old redirects and miss stale links in package metadata.
