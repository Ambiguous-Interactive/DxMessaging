---
title: Release Operations
description: Operator checklist for repository transfer, trusted releases, OpenUPM, and Unity Asset Store onboarding
---

# Release Operations

This section is for maintainers doing account, repository, registry, and store work for DxMessaging. It is not user-facing package documentation. Keep only non-sensitive execution
notes in `.operator-runbooks/`; keep private account, security, publisher, and approval status in the provider console or approved organization password manager.

Canonical public identifiers:

- GitHub repository: `Ambiguous-Interactive/DxMessaging`
- Package ID: `com.wallstop-studios.dxmessaging`
- Documentation site: `https://ambiguous-interactive.github.io/DxMessaging/`
- Release workflow: `.github/workflows/release.yml`
- Unity workflow lock: every Unity-credential-using job acquires `wallstop-organization-builds` through `Ambiguous-Interactive/ambiguous-organization-build-lock` before invoking
  Unity and releases it through the always-run cleanup steps. Licensed workflows call the maintained PowerShell runners directly; the correctness and release checks use
  `scripts/unity/run-ci-tests.ps1`. Native GitHub `concurrency` is repository-scoped, so `wallstop-organization-builds` must not be used as a native `concurrency.group`. IL2CPP is
  the final mode inside each editor-scoped correctness job. The release workflow has a separate EditMode check.
- Unity runner labels: correctness and release jobs use `runs-on: [self-hosted, Windows, RAM-64GB]`. Either ELI-MACHINE or DAD-MACHINE can pick up a matching job.
  `perf-numbers.yml` also requires `fast`; its runner configuration identifies ELI-MACHINE for that lane. See the [CI lanes](../runbooks/iteration-speed.md#ci-lanes) for PR, full
  compatibility and release selection.
- Stuck-job watchdog: `.github/workflows/stuck-job-watchdog.yml` declares a five-minute schedule on the default branch. It checks queued work and available runners before recovery,
  and excludes `release.yml` from automatic cancellation. Inspect recent Actions runs for the actual scheduling and recovery outcome. To target one queued run, dispatch
  `.github/workflows/unstick-run.yml` with its run ID. That workflow uses its own minimum-age and exclusion checks. For a manually dispatched run, select **Re-run all jobs** on the
  original run to retain its input choices. A fresh REST dispatch cannot recover those inputs; automatic redispatch is limited to push and schedule recovery. Read the workflow
  summary for the next step.

Tracked pages:

- [GitHub Transfer](github-transfer.md)
- [CI and GitHub Settings](ci-and-github-settings.md)
- [npm Release Publishing](npm-release-publishing.md)
- [OpenUPM Metadata](openupm-metadata.md)
- [Unity Asset Store UPM](unity-asset-store-upm.md)
- [Post-Transfer Verification](post-transfer-verification.md)

## Local Operator Runbook

Keep an ignored local checklist for non-sensitive execution notes at `.operator-runbooks/ambiguous-release-setup.md` (the `.operator-runbooks/` directory is gitignored and excluded
from npm packages); copy the checklist structure from [Ambiguous Release Migration](./ambiguous-release-migration.md).

Do not store secrets, tokens, recovery codes, screenshots, publisher identifiers, private account metadata, private contact details, or publisher portal notes in tracked files or
this local runbook. Keep secret values and publisher-only records in the appropriate provider consoles or approved organization password manager.

## Release Model

The [release-readiness criteria](https://github.com/Ambiguous-Interactive/DxMessaging/issues/575) define release acceptance. The pipeline runs prepare, readiness approval and
closure, PR merge, tag, exact-revision qualification, release dispatch and publication. Use the [release hold order](./npm-release-publishing.md#400-release-hold-and-order)
exactly:

1. Close every 4.0.0 milestone issue except #575.
1. An operator dispatches `.github/workflows/release-prepare.yml` from the default branch with a bump kind (`patch`/`minor`/`major`) or an explicit version. It runs
   `scripts/release/prepare-release.js` (package.json bump plus CHANGELOG rotation), syncs the banner SVG, regenerates `llms.txt` (both embed the package version), re-runs
   `npm test` and `npm run validate:all` against the bumped tree, and opens a `release/vX.Y.Z` pull request with the auto-commit GitHub App (`AUTO_COMMIT_APP_ID` /
   `AUTO_COMMIT_APP_PRIVATE_KEY`). The `dry-run` input prints the prepared diff and stops without pushing. If the App token cannot push the release branch, the failed run uploads a
   `release-prepare-recovery-*` artifact containing a patch for the prepared release commit; fix the App installation/permissions or matching branch rules, then rerun the workflow.
   Preparation is staging only: it refuses while any other milestone issue is open, but #575 may stay open so the exact candidate can be reviewed and approved.
1. Qualify the exact release-PR head, record wallstop's approval marker on #575 for that version, tree and package hash, then close #575 as completed. Do not close #575 before the
   marker exists.
1. A maintainer reviews the release PR. Re-run the `Release Merge Guard` check after #575 closes; it packs the exact PR head and applies the same hold. Squash-merge only when it
   passes, keeping the default `release: vX.Y.Z` title.
1. `.github/workflows/release-tag.yml` sees the release commit on the default branch (subject `release: vX.Y.Z`, package.json version `X.Y.Z`, exact `## [X.Y.Z]` changelog heading,
   tag absent) and pushes the annotated tag `vX.Y.Z` with the App token. The job runs in the protected `release-tag` environment and waits for wallstop's review. It refuses while
   any milestone issue is open or the approval marker does not match the merged tree. For 4.0.0, missing App credentials, a non-squash merge, or a failed App push fails closed with
   no manual fallback: only this hold-checked workflow may create the tag. Other versions retain the documented manual fallback when the App is unavailable.
1. Complete [tag qualification](#qualify-the-tag) and dispatch Release. The workflow repeats the hold before qualification, rejects a 4.0.0 tag whose tagger is not the auto-commit
   App, and rechecks the hold immediately before npm publish. Wallstop approves the `release` environment review before publication.

The tag must exactly match `package.json.version` with a leading `v`. For example, package version `3.0.1` must be released from tag `v3.0.1`.

`release.yml` is manual-dispatch only and must be dispatched from the reviewed strict-semver tag. A ref such as a branch, `3.0.1`, or `v3.0.1-rc.1` does not pass the release
verifier. Pushing a valid tag by hand is not an approved 4.0.0 path; only `release-tag.yml` may create the 4.0.0 tag. Other versions may use the documented fallback when the App is
unavailable.

## Qualify the Tag

Select the reviewed release tag in the Actions ref picker for each dispatch. Confirm the runs resolve to the same commit. The [CI lanes](../runbooks/iteration-speed.md#ci-lanes)
describe the managed and native release requirements.

1. Run Unity Tests (`unity-tests.yml`) with `shipping_fidelity` enabled and `consumer_install_validation` disabled. This run produces the EditMode, PlayMode, and standalone
   compatibility results for every supported Editor and the stripped-player shipping bundles for the oldest and newest Editors.
1. Wait for Unity Tests to finish successfully. Run Unity Tests again with `consumer_install_validation` enabled and `shipping_fidelity` disabled. This run produces Git, UPM
   tarball, and classic package install results for every supported Editor. Dispatches for the same tag share a concurrency group. A new Unity Tests dispatch for the tag cancels an
   unfinished one.
1. Run Performance Numbers (`perf-numbers.yml`) from the same tag and confirm that both internal and comparison standalone artifacts are available.
1. Verify all three runs succeeded and their compatibility, shipping, consumer-install, and performance artifacts are available. Review the test and benchmark evidence before
   publishing. Missing or expired artifacts block the publication gate.
1. Dispatch Release (`release.yml`) from the same tag.

## Release Gates

The release workflow performs these gates:

1. Apply the [4.0.0 release hold](./npm-release-publishing.md#400-release-hold-and-order): every milestone issue is closed, #575 is closed as completed, and wallstop's approval
   marker matches the version, source tree and packed tarball. `release-tag.yml` applies the same check before creating the tag, and `release.yml` repeats it immediately before npm
   publish.
1. Verify the semver tag, the package version, and the changelog heading.
1. Require successful manual compatibility, shipping, consumer-install and performance runs for the exact tag commit. The qualification job checks producer results and artifact
   metadata; it does not replay artifact contents.
1. Run the complete script suite with `npm test` and `npm run validate:all` (`package.json` is the source of truth for the current validation set). Repository identity checks are
   now the manual checklist in [GitHub Transfer](./github-transfer.md).
1. Run the complete managed release tests, including the source generator, runtime, and documentation suites.
1. Pack the npm tarball and write a `.sha256` checksum.
1. Attest the packed `.tgz` with GitHub artifact attestations.
1. Run the trusted EditMode release check through the PowerShell runner on a matching self-hosted Windows runner. The separate qualification producers supply full compatibility,
   stripped-player and consumer evidence.
1. Create a classic `.unitypackage` from the validated npm tarball on an `ubuntu-latest` runner (`scripts/unity/create_unitypackage.py`). The export checks the original tarball
   checksum and the embedded name and version. The writer does not open Unity or acquire a license seat; it runs in parallel with the separately required trusted Unity release
   check.
1. Assemble the `asset-store-submission` workflow artifact before the npm publish. The tested generator copies the `.unitypackage`, the `.tgz`, checksums, store media, and
   generated classic/UPM upload checklists plus ordered screenshots, `ASSET-STORE-LISTING.json`, `EXPECTED-UPM-FIELDS.json`, and `MANIFEST.json`. Upload the workflow artifact
   immediately; missing or invalid store collateral fails before the irreversible registry action.
1. Publish to npm with Trusted Publishing and provenance.
1. Create or resume a draft GitHub Release. Upload all four assets, download each by its asset ID, and compare its bytes with the staged file before publishing. An existing
   published release is verified without replacing its assets. The body is the matching `## [version]` `CHANGELOG.md` section plus an install footer, rendered by the shared
   `scripts/release/release-notes.js` extractor. Assets are the `.tgz`, its `.sha256`, the `.unitypackage`, and its `.sha256`; a final step asserts the published release carries
   all four.
1. Follow the generated `asset-store-submission` workflow artifact for the manual Unity Asset Store upload; the release-time procedure is the
   [Asset Store Publishing runbook](../runbooks/asset-store-publishing.md) (account onboarding: [Unity Asset Store UPM](./unity-asset-store-upm.md)).

Release assets are the npm `.tgz` plus `.sha256` and the `.unitypackage` plus `.sha256`. The `.unitypackage` is a required asset: a failed export blocks publication, including the
irreversible npm publish. Recovery is to fix the export and re-run; the npm publish is idempotent (it skips a version already on the registry). npm and GitHub publication are
separate operations; a later GitHub failure does not undo npm publication. Draft reruns may replace partial uploads. Published reruns require byte-identical assets, including
checksums; a missing or changed asset fails instead of overwriting the release. Use the original staged files for a rerun, or publish changed package contents as a new version. The
workflow supports immutable releases but does not enable the repository setting. The Unity Asset Store upload is manual; the official classic and UPM publishing tools do not
document a sanctioned headless entry point.

## Public References

- GitHub repository transfer docs: <https://docs.github.com/articles/about-repository-transfers>
- npm Trusted Publishing: <https://docs.npmjs.com/trusted-publishers>
- npm provenance: <https://docs.npmjs.com/generating-provenance-statements>
- OpenUPM package metadata: <https://openupm.com/docs/adding-upm-package.html>
- Unity Asset Store publishing: <https://support.unity.com/hc/en-us/sections/12259768837268-Publishing-on-the-Asset-Store>
- Unity package standards: <https://unity.com/core-standards>
