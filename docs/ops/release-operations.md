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

Keep an ignored local checklist for non-sensitive execution notes at
`.operator-runbooks/ambiguous-release-setup.md` (the `.operator-runbooks/`
directory is gitignored and excluded from npm packages); copy the checklist
structure from [Ambiguous Release Migration](./ambiguous-release-migration.md).

Do not store secrets, tokens, recovery codes, screenshots, publisher
identifiers, private account metadata, private contact details, or publisher
portal notes in tracked files or this local runbook. Keep secret values and
publisher-only records in the appropriate provider consoles or approved
organization password manager.

## Release Model

The release pipeline runs dispatch, PR, tag, publish:

1. An operator dispatches `.github/workflows/release-prepare.yml` from the
   default branch with a bump kind (`patch`/`minor`/`major`) or an explicit
   version. It runs `scripts/release/prepare-release.js` (package.json bump
   plus CHANGELOG rotation), syncs the banner SVG, regenerates `llms.txt`
   (both embed the package version), re-runs `npm test` and
   `npm run validate:all` against the bumped tree, and opens a
   `release/vX.Y.Z` pull request with the auto-commit GitHub App
   (`AUTO_COMMIT_APP_ID` / `AUTO_COMMIT_APP_PRIVATE_KEY`). The `dry-run`
   input prints the prepared diff and stops without pushing.
1. A maintainer reviews the release PR and squash-merges it, keeping the
   default `release: vX.Y.Z` title.
1. `.github/workflows/release-tag.yml` sees the release commit on the default
   branch (subject `release: vX.Y.Z`, package.json version `X.Y.Z`, exact
   `## [X.Y.Z]` changelog heading, tag absent) and pushes the annotated tag
   `vX.Y.Z` with the App token. When the App secrets are absent it prints the
   manual `git tag -a vX.Y.Z -m "Release vX.Y.Z" && git push origin vX.Y.Z`
   commands as a warning instead.
1. The tag fires `.github/workflows/release.yml`, which performs the gates
   below.

The tag must exactly match `package.json.version` with a leading `v`. For
example, package version `3.0.1` must be released from tag `v3.0.1`.

`release.yml` itself has no manual `workflow_dispatch` path; the manual entry
point is `release-prepare.yml`. A tag such as `3.0.1` or `v3.0.1-rc.1` does
not pass the release verifier. Pushing a valid tag by hand remains a
supported fallback when the App is unavailable.

The release workflow performs these gates:

1. Verify the semver tag, the package version, and the changelog heading.
1. Run script tests (`node --test scripts/`) and `npm run validate:all`
   (asmdef references, Unity version matrix, JS LoC budget, analyzer payload,
   `llms.txt` freshness). Repository identity checks are now the manual
   checklist in [GitHub Transfer](./github-transfer.md).
1. Pack the npm tarball and write a `.sha256` checksum.
1. Attest the packed `.tgz` with GitHub artifact attestations.
1. Run the trusted Unity release check on the Ambiguous self-hosted Windows
   runner.
1. Export a classic `.unitypackage` from the npm payload on the self-hosted
   Windows runner (`scripts/unity/export-unitypackage.ps1`); the job follows
   the same Unity license and organization-lock discipline as the test jobs.
1. Publish to npm with Trusted Publishing and provenance.
1. Create or update the GitHub Release. The body is the matching `## [version]`
   `CHANGELOG.md` section plus an install footer, rendered by the shared
   `scripts/release/release-notes.js` extractor. Assets are the `.tgz`, its
   `.sha256`, the `.unitypackage`, and its `.sha256`; a final step asserts the
   published release carries all four.
1. Assemble the `asset-store-submission` workflow artifact (the
   `.unitypackage`, the `.tgz`, checksums, and a generated
   `SUBMISSION-CHECKLIST.md`) for the manual Unity Asset Store upload; the
   release-time procedure is the
   [Asset Store Publishing runbook](../runbooks/asset-store-publishing.md)
   (account onboarding: [Unity Asset Store UPM](./unity-asset-store-upm.md)).

Release assets are the npm `.tgz` plus `.sha256` and the `.unitypackage` plus
`.sha256`. The `.unitypackage` is a REQUIRED asset and the release is atomic: a
failed export blocks the entire release (including the irreversible npm publish)
rather than shipping an incomplete release. Recovery is to fix the export and
re-run; the npm publish is idempotent (it skips a version already on the
registry). The Unity Asset Store upload is manual; no sanctioned CLI exists for
it.

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
