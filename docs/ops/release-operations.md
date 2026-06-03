---
title: Release Operations
description: Operator checklist for repository transfer, trusted releases, OpenUPM, and Unity Asset Store onboarding
---

# Release Operations

This section is for maintainers doing account, repository, registry, and store
work for DxMessaging. It is not user-facing package documentation. Keep only
non-sensitive execution notes in `.operator-runbooks/`; keep private account,
security, publisher, and approval status in the provider console or approved
organization password manager.

Canonical public identifiers:

- GitHub repository: `Ambiguous-Interactive/DxMessaging`
- Package ID: `com.wallstop-studios.dxmessaging`
- Documentation site: `https://ambiguous-interactive.github.io/DxMessaging/`
- Release workflow: `.github/workflows/release.yml`
- Unity workflow lock: every Unity-credential-using job acquires
  `wallstop-organization-builds` through
  `Ambiguous-Interactive/ambiguous-organization-build-lock` immediately
  before the licensed `game-ci/unity-test-runner@v4` section and releases
  it with `if: always()`. Native GitHub `concurrency` is repository-scoped,
  so `wallstop-organization-builds` must not be used as a native
  `concurrency.group`. IL2CPP is the `standalone` entry in the
  `unity-tests` `test-mode` matrix, not a separate job.
- Unity runner labels: uniform static `runs-on: [self-hosted, Windows,
RAM-64GB]` across all Unity-credential-using jobs, so either
  ELI-MACHINE or DAD-MACHINE can pick up any Unity job. The `fast`
  marker remains on ELI-MACHINE for a future opt-in hotfix dispatch but
  no currently-active workflow requests it.
- Stuck-job watchdog: `.github/workflows/stuck-job-watchdog.yml` runs
  every 5 minutes to detect and recover from the known GitHub Actions
  self-hosted dispatcher bug (Community Discussion #186811) where a
  queued run never receives an Online/Idle runner. The watchdog
  excludes `release.yml` from auto-cancellation to protect attestation
  and publishing flows. For immediate one-click recovery of a single
  stuck run, operators dispatch `.github/workflows/unstick-run.yml`
  from the Actions tab with the stuck run id (it bypasses the cron
  wait and the queue-age threshold). Note that GitHub `schedule:` cron
  triggers fire only from the repository default branch, so the
  watchdog cron is INACTIVE until `stuck-job-watchdog.yml` reaches
  `master`; until then, use `unstick-run.yml` or the watchdog's manual
  `workflow_dispatch` trigger.

Tracked pages:

- [GitHub Transfer](github-transfer.md)
- [CI and GitHub Settings](ci-and-github-settings.md)
- [npm Release Publishing](npm-release-publishing.md)
- [OpenUPM Metadata](openupm-metadata.md)
- [Unity Asset Store UPM](unity-asset-store-upm.md)
- [Post-Transfer Verification](post-transfer-verification.md)

## Local Operator Runbook

Generate an ignored local checklist for non-sensitive execution notes:

```bash
npm run generate:ambiguous-release-runbook
```

The command writes `.operator-runbooks/ambiguous-release-setup.md`. The file is
gitignored and excluded from npm packages. Generation refuses to overwrite an
existing runbook; use `node scripts/generate-ambiguous-release-runbook.js --force`
only after preserving local notes.

Do not store secrets, tokens, recovery codes, screenshots, publisher
identifiers, private account metadata, private contact details, or publisher
portal notes in tracked files or this local runbook. Keep secret values and
publisher-only records in the appropriate provider consoles or approved
organization password manager.

## Release Model

The release trigger is a pushed tag named `vX.Y.Z`. The tag must exactly match
`package.json.version` with a leading `v`. For example, package version `3.0.1`
must be released from tag `v3.0.1`.

There is no manual `workflow_dispatch` release path. A tag such as `3.0.1` or
`v3.0.1-rc.1` does not pass the release verifier.

The release workflow performs these gates:

1. Verify the semver tag and package version.
1. Run script tests, Unity workflow contract tests, npm package validation,
   `llms.txt` validation, repository identity validation, and `validate:all`.
1. Pack the npm tarball and write a `.sha256` checksum.
1. Attest the packed `.tgz` with GitHub artifact attestations.
1. Run the trusted Unity release check on the Ambiguous self-hosted Windows
   runner.
1. Create or update the GitHub Release with the `.tgz` and checksum.
1. Publish to npm with Trusted Publishing and provenance.

Release assets are currently npm `.tgz` plus `.sha256`. The workflow does not
build or upload a `.unitypackage`.

## Public References

- GitHub repository transfer docs:
  <https://docs.github.com/articles/about-repository-transfers>
- npm Trusted Publishing:
  <https://docs.npmjs.com/trusted-publishers>
- npm provenance:
  <https://docs.npmjs.com/generating-provenance-statements>
- OpenUPM package metadata:
  <https://openupm.com/docs/adding-upm-package.html>
- Unity Asset Store publishing:
  <https://support.unity.com/hc/en-us/sections/12259768837268-Publishing-on-the-Asset-Store>
- Unity package standards:
  <https://unity.com/core-standards>
