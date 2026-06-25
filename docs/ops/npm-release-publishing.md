---
title: npm Release Publishing
description: Manual setup for npm Trusted Publishing and tag-driven releases
---

# npm Release Publishing

The package name is `com.wallstop-studios.dxmessaging`.

The release workflow publishes from GitHub Actions using npm Trusted Publishing.
There is no `NPM_TOKEN` secret. Do not add one unless the release model is
changed and reviewed.

## npm Package Access

In npm, verify:

1. The package exists and the current maintainers are correct.
1. Two-factor policy matches organization policy.
1. No stale maintainers remain from the transfer.
1. Package visibility is public.
1. Provenance is visible for versions published from GitHub Actions.

Keep only non-sensitive verification notes in the local ignored runbook, such
as the public package URL and the date access was checked. Keep maintainer
account details, private npm account notes, recovery codes, tokens, and other
private account metadata in the provider console or approved organization
password manager.

## Trusted Publishing Binding

Configure npm Trusted Publishing for:

- GitHub organization: `Ambiguous-Interactive`
- GitHub repository: `DxMessaging`
- Workflow: `.github/workflows/release.yml`
- Environment: only if the GitHub release job uses one

Trusted Publishing uses OIDC. npm's current docs require an npm CLI that
supports trusted publishing; this workflow invokes `npm@^11.5.1` for publish.

## Release Trigger

The normal path never creates the tag by hand:

1. Dispatch `.github/workflows/release-prepare.yml` from the default branch
   (bump kind or explicit version). It opens a `release/vX.Y.Z` PR containing
   the version bump, the rotated changelog, the synced banner, and the
   regenerated `llms.txt`.
1. Squash-merge that PR with its default `release: vX.Y.Z` title.
1. `.github/workflows/release-tag.yml` validates the merged commit and pushes
   the annotated tag `vX.Y.Z` with the auto-commit GitHub App token, which
   triggers the release workflow.

The manual fallback (App unavailable, or recovering from a partial run) is
pushing a strict semver tag that points at the reviewed release commit. Use a
signed tag when signing is available, or the repository-approved annotated
tag fallback when signing is not available:

```bash
git checkout <reviewed-release-commit>
git tag -s v3.0.2

# Approved fallback only when signed tags are unavailable:
git tag -a v3.0.2 -m "Release v3.0.2"
git push origin v3.0.2
```

Before tagging, `package.json.version` must be `3.0.2`. The workflow rejects:

- `3.0.2`
- `v3.0.2-rc.1`
- `v3.0.2` when `package.json.version` is still `3.0.1`

The only manual dispatch is `release-prepare.yml`; `release.yml` itself stays
tag-triggered.

## Release Gates

The workflow runs these checks before publishing:

- `npm test`
- `npm run validate:all`
- trusted Unity editmode release check on the Ambiguous Windows runner

Run the same commands locally from a clean tracked state before tagging.
`release-prepare.yml` runs `npm test` and `npm run validate:all` against the
bumped tree before it opens the release PR, so a broken prepare never reaches
the tag.

## Artifacts

The release workflow creates:

- npm `.tgz`
- `.sha256` checksum
- GitHub artifact attestation for the `.tgz`
- a classic `.unitypackage` (plus `.sha256`) exported from the npm payload on
  the self-hosted Windows runner; a REQUIRED release asset
- GitHub Release assets containing the `.tgz`, its checksum, and the
  `.unitypackage` pair, verified present by a final post-publish step
- npm package version published with provenance
- the `asset-store-submission` workflow artifact (the `.unitypackage`, the
  `.tgz`, checksums, and `SUBMISSION-CHECKLIST.md`) staged for the manual
  Unity Asset Store upload; the release-time procedure is the
  [Asset Store Publishing runbook](../runbooks/asset-store-publishing.md)

The npm publish always runs before the GitHub Release update. The
`.unitypackage` is a required asset, so a failed export blocks the entire
release (including the npm publish): releases are atomic. Recovery is to fix the
export and re-run; the npm publish is idempotent (it skips a version already on
the registry).

## Release Drafter

Release Drafter creates draft release notes from pull requests and changelog
content. The tag template is `v$RESOLVED_VERSION`, matching the release
workflow.

`release.yml` writes the published release body from `CHANGELOG.md`: the
matching `## [version]` section plus an install footer, via the shared
`scripts/release/release-notes.js` extractor (the same one Release Drafter and
`release-prepare.yml` use). `CHANGELOG.md` is the source of truth for the
release body; the Release Drafter draft is a PR-categorized preview.

## Failure Modes

- npm Trusted Publishing still points at the old GitHub repository.
- npm Trusted Publishing is configured for a GitHub environment that the
  workflow does not use.
- A maintainer adds `NPM_TOKEN`, bypassing the OIDC model.
- The GitHub Release step fails after npm publish. The publish step is
  idempotent (an already-published version is skipped), so re-running the
  workflow finishes the GitHub Release without a duplicate-publish error.
- Release assets are confused with Unity Asset Store uploads. The GitHub
  Release `.unitypackage` is the same file staged in the
  `asset-store-submission` artifact, but the Asset Store submission itself is
  a separate manual step through the publisher portal.
