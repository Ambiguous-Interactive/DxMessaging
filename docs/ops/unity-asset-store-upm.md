---
title: Unity Asset Store UPM
description: Manual onboarding checklist for Unity Asset Store UPM publishing
---

# Unity Asset Store UPM

Unity Asset Store UPM publishing is separate from npm and OpenUPM. npm
provenance and GitHub artifact attestations do not replace Unity-controlled
package signing or Asset Store review.

Unity's public materials describe UPM publishing on the Asset Store as an
early-access workflow. Treat UPM Asset Store publishing as conditional until the
Ambiguous publisher account is approved for that workflow.

## Publisher Account Setup

Verify in the Unity publisher account:

1. Publisher profile is active.
1. Organization verification requirements are complete.
1. Any required identity, domain, tax, or business verification is complete.
1. The account has access to UPM publishing tools if using UPM submission.
1. Maintainers who submit packages have the needed role.

Do not commit publisher account IDs, screenshots, tax details, DUNS numbers, or
private review messages.

## Package Preparation

DxMessaging is a UPM package with package ID
`com.wallstop-studios.dxmessaging`. Before submission, verify:

- `package.json` metadata is current.
- `README.md`, `CHANGELOG.md`, `LICENSE.md`, and third-party notices are
  included in the npm/UPM package.
- Samples under `Samples~/` import correctly.
- Unity versions match the supported matrix.
- Dependencies are documented and minimal.
- No build artifacts, IDE files, local runbooks, `.llm`, `.github`, scripts,
  tests, devcontainer files, or Unity test harness files ship in the package.
- Every shipped Unity-relevant path has a paired `.meta` file.

Run:

```bash
npm pack --dry-run
```

## Release Staging Artifact

Every tagged release stages the Asset Store submission inputs automatically.
The `release.yml` publish job uploads an `asset-store-submission` workflow
artifact containing:

- the `.unitypackage` exported by `scripts/unity/export-unitypackage.ps1`
- the npm `.tgz` (the exact UPM payload, for reference and diffing)
- `.sha256` checksums for both
- a generated `SUBMISSION-CHECKLIST.md` with the version and upload steps

The export stages the `npm pack` payload into an ephemeral Unity project
under `Assets/WallstopStudios/DxMessaging/` with two Assets-form changes:
`SourceGenerators/**` is excluded (the loose generator sources would compile
into `Assembly-CSharp` under `Assets/` and fail; consumers get the source
generator and analyzer from the RoslynAnalyzer-labeled DLLs shipped under
`Editor/Analyzers/`), and `Samples~` is renamed to `Samples` so samples
import visibly.

There is no sanctioned CLI or API for Unity Asset Store uploads (verified
June 2026). The pipeline therefore stops at staging; the upload below is
manual.

## Submission Path

For each release:

1. Download the `asset-store-submission` artifact from the release workflow
   run (or the `.unitypackage` from the GitHub Release assets).
1. Sign in to `publisher.unity.com` with the Ambiguous publisher account.
1. In a Unity project with the Asset Store Publishing Tools package
   installed, import the staged `.unitypackage` and upload the package
   content through the publisher tooling.
1. Complete Asset Store metadata, screenshots, compatibility, and review
   fields; copy the changelog from the matching `## [X.Y.Z]` CHANGELOG.md
   section.
1. Submit for review.

If Ambiguous has UPM Asset Store early access, the UPM submission flow can
replace the `.unitypackage` upload:

1. Install Unity's UPM publishing tooling from Unity's official channel.
1. Validate the package with Unity's tooling.
1. Upload the UPM package through the UPM publishing workflow.
1. Complete Asset Store metadata, screenshots, compatibility, and review fields.
1. Submit for review.

If Ambiguous does not have UPM Asset Store early access:

1. Do not claim Asset Store UPM availability in package docs.
1. Continue publishing through npm and OpenUPM.
1. Track Unity approval status in Unity Publisher Portal or the approved
   organization password manager.
1. Use the staged `.unitypackage` from the release pipeline as the classic
   submission format.

## Signing and Provenance

Unity package signing is controlled by Unity's publishing pipeline. It is
independent from:

- npm Trusted Publishing provenance
- GitHub artifact attestations
- OpenUPM indexing

Do not describe npm or GitHub provenance as Unity Asset Store signing.

## Failure Modes

- The publisher account is not approved for UPM publishing.
- Package metadata links point to the old GitHub organization.
- Asset Store submission asks for documentation included offline, while the
  npm package excludes `docs/**`.
- The `.unitypackage` export job failed. The release is atomic, so the whole
  release (including the npm publish) is blocked rather than shipping without
  the `.unitypackage`; fix the export and re-run the release workflow.
- Someone scripts an Asset Store upload against an unsanctioned endpoint; no
  supported CLI exists, so the upload must stay manual.
- Unity rejects unnecessary dependencies or files.
- Private publisher identifiers leak into tracked docs.
