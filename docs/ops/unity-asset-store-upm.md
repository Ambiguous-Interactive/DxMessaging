---
title: Unity Asset Store UPM
description: Manual onboarding checklist for Unity Asset Store UPM publishing
---

# Unity Asset Store UPM

Unity Asset Store UPM publishing is separate from npm and OpenUPM. npm
provenance and GitHub artifact attestations do not replace Unity-controlled
package signing or Asset Store review.

Unity's current UPM publishing page says the workflow is available for all
tools, extensions, and SDKs, while the official publishing-tool listing still
describes selected early access. Treat UPM Asset Store publishing as conditional
until the Ambiguous publisher account reaches `Active` UPM admittance and the
UPM Publisher Portal appears.

## Publisher Account Setup

Verify in the Unity publisher account:

1. Publisher profile is active.
1. Organization verification requirements are complete.
1. Any required identity, domain, tax, or business verification is complete.
1. UPM enrollment has `Active` admittance and the UPM Publisher Portal appears.
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
- Image assets in `package.json` `files` are limited to assets referenced by
  shipped package docs. Today that means the README banner at
  `docs/images/DxMessaging-banner.svg` and its `.meta`; the MkDocs
  `dxmessaging-mark.svg` logo, favicon, Open Graph image, and store media are
  tracked for GitHub Pages and release staging only, because the generated
  documentation site is not part of the npm/UPM payload.
- The ignored `design-system/` source tree, design scraps, and exploration PNGs
  are not release assets. If a future package-shipped document references another
  brand image, add that image and its `.meta` to `package.json` `files` in the
  same change and re-run package validation.

Run:

```bash
npm pack --dry-run
```

## Brand Card Sources

The Open Graph card and the Asset Store card ship as PNG, because social
scrapers and the Asset Store submission form do not accept SVG. Each one is
rendered from the SVG beside it:

| Source                                           | Output                                           | Size     |
| ------------------------------------------------ | ------------------------------------------------ | -------- |
| `docs/images/dxmessaging-og-1200x630.svg`        | `docs/images/dxmessaging-og-1200x630.png`        | 1200x630 |
| `docs/images/dxmessaging-store-card-420x280.svg` | `docs/images/dxmessaging-store-card-420x280.png` | 420x280  |

Edit the SVG, then render both PNGs:

```bash
python3 -m venv .artifacts/docs-venv
.artifacts/docs-venv/bin/python -m pip install -r requirements-brand.txt
.artifacts/docs-venv/bin/python scripts/render-brand-cards.py
```

The render needs `requirements-brand.txt` (the compiled lock holding
`cairosvg`), the system cairo library (`libcairo2`), and the three brand
faces installed for fontconfig: Space Grotesk, IBM Plex Sans, and JetBrains
Mono. All three are SIL Open Font License 1.1. The script prints the exact
download commands and stops before writing anything when a face is missing,
because cairo substitutes a default face instead of failing.

The script also checks that every SVG drawing the mark inline still matches
`dxmessaging-mark.svg`, and refuses to render when one has drifted. There is no
portable way to reference an external SVG that both a browser and cairo honour,
so the geometry is duplicated in `dxmessaging-icon-tile.svg` and in both cards.
The duplication is checked rather than hidden.

The other brand PNGs are direct renders of a single SVG and are not produced by
this script: `icon-256.png` and `dxmessaging-store-icon-320.png` come from
`dxmessaging-icon-tile.svg`, and the favicons come from `dxmessaging-mark.svg`.
The release staging generator pins the SHA-256 of every Asset Store SVG source
and PNG output. After an intentional render change, update the matching
`STORE_MEDIA` source/output hashes in `scripts/release/asset-store-submission.js`;
otherwise release staging fails before publication.

## Release Staging Artifact

Every tagged release stages the Asset Store submission inputs automatically.
The `release.yml` publish job uploads an `asset-store-submission` workflow
artifact containing:

- the `.unitypackage` exported by `scripts/unity/export-unitypackage.ps1`
- the npm `.tgz` (the exact UPM payload, for reference and diffing)
- `.sha256` checksums for both
- tracked store media under `media/`
- ordered product screenshots under `screenshots/`
- generated `CLASSIC-UPLOAD-CHECKLIST.md`, `UPM-UPLOAD-CHECKLIST.md`,
  `ASSET-STORE-LISTING.json`, `EXPECTED-UPM-FIELDS.json`, and `MANIFEST.json`

`.github/asset-store-listing.json` is the canonical listing source. Release
staging validates it, adds the package version, minimum Unity version, and
matching changelog section, then writes the exact portal-ready fields and media
order to `ASSET-STORE-LISTING.json`.

The export stages the `npm pack` payload into an ephemeral Unity project
under `Assets/WallstopStudios/DxMessaging/` with two Assets-form changes:
`SourceGenerators/**` is excluded (the loose generator sources would compile
into `Assembly-CSharp` under `Assets/` and fail; consumers get the source
generator and analyzer from the RoslynAnalyzer-labeled DLLs shipped under
`Runtime/Analyzers/`), and `Samples~` is renamed to `Samples` so samples
import visibly.

There is no sanctioned CLI or API for Unity Asset Store uploads (re-verified
2026-08-28). Unity now ships the official `com.unity.upm-publishing-tools`
package alongside the classic publishing tool, but the official material for
both describes interactive Editor workflows and does not document a headless
entry point or service credential. Community batch uploaders still drive
undocumented internal Editor APIs that can break without warning. The pipeline
therefore stops at staging; the upload below is manual. The release-time
procedure -- and the full automation determination with its re-evaluation
trigger -- lives in the
[Asset Store Publishing runbook](../runbooks/asset-store-publishing.md).

## Submission Path

The per-release classic `.unitypackage` upload procedure -- download the staged
`asset-store-submission` artifact, drive the in-Editor uploader, fill the
metadata, and submit for review -- lives in the
[Asset Store Publishing runbook](../runbooks/asset-store-publishing.md). This
page stays focused on the account-onboarding context and the conditional UPM
variant below.

If Ambiguous has `Active` UPM admittance, the UPM submission flow can replace
the `.unitypackage` upload:

1. Open an editor version listed as compatible with the installed UPM publishing
   tool, then install the tool from Unity's official channel.
1. Add the staged `.tgz` through
   `Window > Package Manager > Add package from tarball...`.
1. Open `Window > Tools > Asset Store > Validator`, select the UPM validation
   type, and validate the installed package.
1. Open `Window > Tools > Asset Store > Uploader`, select the `UPM Packages`
   tab, and upload the exact package version.
1. Apply every listing field and ordered screenshot from
   `ASSET-STORE-LISTING.json`, then complete the remaining review fields.
1. Submit for review.
1. Repeat for two versions. In a clean Unity project where neither version is
   installed, open Package Manager Version History, expand both versions, and
   verify that each non-installed row displays its own expected release notes.
   This UI result is the acceptance proof.
1. If the Publisher Portal or supported Unity tooling exposes a version
   manifest, also compare its `name`, `version`, and `_upm.changelog` values
   with `EXPECTED-UPM-FIELDS.json`. This file records expected field values; it
   is not a complete response schema. Do not query undocumented endpoints to
   obtain the manifest.

If Ambiguous does not have `Active` UPM admittance:

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
- Asset Store submission asks for documentation included offline. The npm
  package includes only the README banner image under `docs/images/`, not the
  full generated documentation site.
- The `.unitypackage` export job failed. The release is atomic, so the whole
  release (including the npm publish) is blocked rather than shipping without
  the `.unitypackage`; fix the export and re-run the release workflow.
- Someone scripts an Asset Store upload against an unsanctioned endpoint; no
  supported CLI exists, so the upload must stay manual.
- Unity rejects unnecessary dependencies or files.
- Private publisher identifiers leak into tracked docs.
