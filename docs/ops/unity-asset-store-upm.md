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

## Submission Path

If Ambiguous has UPM Asset Store early access:

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
1. Decide separately whether a `.unitypackage` fallback is worth maintaining.

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
- A `.unitypackage` is expected, but the release workflow only creates `.tgz`.
- Unity rejects unnecessary dependencies or files.
- Private publisher identifiers leak into tracked docs.
