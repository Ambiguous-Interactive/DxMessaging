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

Release only by pushing a strict semver tag that points at the reviewed release
commit. Use a signed tag when signing is available, or the repository-approved
annotated tag fallback when signing is not available:

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

There is no manual release dispatch.

## Release Gates

The workflow runs these checks before publishing:

- `npm test`
- `npm run validate:all`
- trusted Unity editmode release check on the Ambiguous Windows runner

Run the same commands locally from a clean tracked state before tagging.

## Artifacts

The release workflow creates:

- npm `.tgz`
- `.sha256` checksum
- GitHub artifact attestation for the `.tgz`
- GitHub Release assets containing the `.tgz` and checksum
- npm package version published with provenance

It does not create a `.unitypackage`.

## Release Drafter

Release Drafter creates draft release notes from pull requests and changelog
content. The tag template is `v$RESOLVED_VERSION`, matching the release
workflow.

Current `release.yml` writes minimal generated release notes during publish.
If maintainers want rich Release Drafter notes to remain, copy the draft notes
into `CHANGELOG.md` or update the release workflow before the first production
release under Ambiguous.

## Failure Modes

- npm Trusted Publishing still points at the old GitHub repository.
- npm Trusted Publishing is configured for a GitHub environment that the
  workflow does not use.
- A maintainer adds `NPM_TOKEN`, bypassing the OIDC model.
- The GitHub Release step fails after npm publish. The workflow creates or
  updates the GitHub Release before npm publish to reduce that risk.
- Release assets are confused with Unity Asset Store uploads. The release
  assets are npm tarballs, not `.unitypackage` files.
