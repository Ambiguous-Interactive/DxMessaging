# Asset Store Publishing Runbook

This runbook is the release-time procedure for shipping a tagged
`com.wallstop-studios.dxmessaging` release to the Unity Asset Store. It assumes
the publisher account is already onboarded; for account setup, package-content
rules, and the UPM-vs-classic submission choice, see
[Unity Asset Store UPM](../ops/unity-asset-store-upm.md).

The Asset Store upload is **manual by design**: Unity now provides official
Editor tools for both classic and UPM submissions, but neither tool documents a
non-interactive upload path (see the determination below). The release pipeline
removes every manual step it can -- it stages the release source payloads and
generated checklists as a workflow artifact -- so the human step is reduced to
"download the artifact, then drive the Editor uploader." Treat this runbook as
the source of truth for that step; keep account IDs, screenshots, and review
correspondence out of it.

## Automation determination (re-verified 2026-08-28)

There is **no sanctioned (official, documented, supported) CLI, API, or headless
mode** for uploading a package to the Unity Asset Store. The upload must be
driven interactively through the Unity Editor by a signed-in publisher. Evidence:

1. **Official Asset Store Publishing Tools** (`com.unity.asset-store-tools`):
   its documented workflow is the Editor GUI
   (`Tools > Asset Store > Uploader`),
   which requires an interactive Unity ID login and an upload button click.
   There is no documented `-batchmode`, `-executeMethod`, command-line,
   API-token, or CI entry point.
1. **Official Asset Store UPM Publishing Tools**
   (`com.unity.upm-publishing-tools` v0.3.1, released 2026-06-08): Unity's
   [UPM publishing page](https://assetstore.unity.com/publishing/upm-publishing)
   says UPM publishing is available for all tools, extensions, and SDKs. The
   [tool listing](https://assetstore.unity.com/packages/tools/asset-store-upm-publishing-tools-5368745)
   still describes selected early access. Treat `Active` admittance as a
   Publisher Portal preflight until Unity resolves that conflict. Unity's
   [enrollment instructions](https://docs.unity.com/en-us/asset-store/publishing/upm-packages/apply)
   say the account must reach `Active` admittance before the UPM Publisher
   Portal appears. The official material describes an Editor tool for
   validation, upload, and publication; it does not document a headless API or
   service credential. Unity 2022.3 is the oldest editor version listed for the
   v0.3.1 tool.
1. **Unity Manual** ("Validate and upload assets to your package"): documents
   only the in-Editor Uploader window and interactive Unity ID login. It makes
   no mention of CI/CD, automation, headless mode, or programmatic upload.
1. **Community batch tools** exist (command-line `-executeMethod` uploaders and
   reverse-engineered publisher-portal API clients), but every one of them
   drives **undocumented internal Editor APIs** or a harvested session cookie.
   They are self-described as unsupported -- one states the API "may break
   without warning" and recommends a throwaway Unity account because it is
   unofficial. Wiring any of them into CI is exactly the unsanctioned-endpoint
   failure mode this project refuses (see
   [Unity Asset Store UPM](../ops/unity-asset-store-upm.md) "Failure Modes").

**Decision (accepted by the maintainer):** auto-publish was approved _only if_ a
sanctioned non-interactive path exists. Because none does, the project does
**not** fake one against an internal API. The publish step stays an interactive
Editor procedure, backed by the staged artifact below. This is revisited each release; see
[Re-evaluating automation](#re-evaluating-automation).

Public-documentation checks for unsupported upload experiments are quarantined in the manual-only
`Asset Store Unsupported Upload Research` workflow. It has no tag trigger,
requires the protected `asset-store-experimental` environment, and is limited to
recording the official source URLs and the current supported-automation verdict.
It cannot inspect the package until an enrolled owner installs it, and it must
not upload, store publisher credentials, call undocumented endpoints, or
harvest Unity ID browser sessions.

## What the release pipeline stages for you

Every tagged release runs the `publish` job in
[`.github/workflows/release.yml`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/release.yml).
Before npm publish, that job uploads an
`asset-store-submission` workflow artifact (30-day retention) containing the
exact inputs for the Asset Store upload:

- the `.unitypackage` exported by
  [`scripts/unity/export-unitypackage.ps1`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/unity/export-unitypackage.ps1)
  (the Assets-form payload: `Samples~` renamed to `Samples`, the loose generator
  sources replaced by the shipped `Runtime/Analyzers/` RoslynAnalyzer DLLs);
- the npm `.tgz` (the exact UPM payload, for reference and diffing);
- `.sha256` checksums for both;
- the tracked store media under `media/`;
- four ordered product screenshots under `screenshots/`;
- generated `ASSET-STORE-LISTING.json` with the exact listing title,
  description, keywords, links, artwork paths, screenshot captions, package
  version, minimum Unity version, and release notes;
- generated `CLASSIC-UPLOAD-CHECKLIST.md` and
  `UPM-UPLOAD-CHECKLIST.md` files carrying package metadata and the matching
  changelog section;
- `EXPECTED-UPM-FIELDS.json` with the exact package name, version, and
  `_upm.changelog` field values expected after registry publication. This is a
  field-value reference, not a complete registry response schema;
- `MANIFEST.json` with filenames, sizes, and SHA-256 hashes for the staged
  files.

The canonical listing source is `.github/asset-store-listing.json`. The release
generator validates it and adds release-specific fields to
`ASSET-STORE-LISTING.json`. The Unity Publisher Portal remains the submission
interface, but it is not the source of truth for the listing text or media
order.

The `.unitypackage` is also attached to the GitHub Release, so a maintainer can
grab it from either place. The pipeline stops at staging; it never contacts the
Asset Store.

## Release-time procedure

Run this once the release workflow for the tag is green.

1. **Choose the submission format.** Use the classic path unless the account has
   `Active` UPM admittance and the UPM Publisher Portal is visible. Use the UPM
   path after both conditions hold.
1. **Get the payload.** Download the `asset-store-submission` artifact from the
   release workflow run (Actions tab -> the `release` run for the tag ->
   Artifacts), or download the `.unitypackage` from the matching GitHub Release
   assets. Open `CLASSIC-UPLOAD-CHECKLIST.md` from the artifact and keep it
   beside you -- it is generated for this exact version. Use
   `UPM-UPLOAD-CHECKLIST.md` only after the account reaches `Active` UPM
   admittance and the UPM Publisher Portal appears.
1. **Verify integrity.** Confirm the selected `.unitypackage` or `.tgz` SHA-256
   matches its `.sha256` sidecar before uploading (use `Get-FileHash` on
   Windows, `shasum -a 256` elsewhere). This catches a truncated download
   before it reaches review.
1. **Sign in.** Open a Unity Editor (any version on the supported matrix for the
   classic path; an editor version supported by the installed UPM publishing
   tool for the UPM path). The classic payload was exported from the pinned
   2022.3.45f1. Install the applicable official Asset Store publishing tool and
   sign in to the publisher account via the Editor's Unity ID login. Complete
   two-factor authentication when prompted. This cannot be automated because
   the documented upload is bound to an interactive Unity ID session and Unity
   documents no service credential.
1. **Upload the classic package.** Skip this step for a UPM submission. Import
   the staged `.unitypackage` into a
   clean project. Confirm the import created
   `Assets/WallstopStudios/DxMessaging/` and no unrelated top-level content.
   Resolve every import or duplicate-GUID error in the Unity Console. Open
   `Tools > Asset Store > Validator`, add the DxMessaging folder, run
   validation, and resolve every finding. In
   `Tools > Asset Store > Uploader`, select the DxMessaging draft and
   run `Export and Upload`. The official tool creates a new archive from the
   inspected imported assets; archive hashes are not expected to match.
1. **Upload the UPM package.** Skip this step for a classic submission. Choose
   `Window > Package Manager > Add package from tarball...` and select the
   staged `.tgz`. Open `Window > Tools > Asset Store > Validator`, select the
   UPM validation type, and validate the installed package. Open
   `Window > Tools > Asset Store > Uploader`, select the `UPM Packages` tab,
   select the exact package version, and upload it. Follow
   `UPM-UPLOAD-CHECKLIST.md` from the artifact; do not install a working-tree
   copy.
1. **Apply the listing.** Open `ASSET-STORE-LISTING.json` from the artifact and
   apply its title, description, keywords, links, artwork, ordered screenshots,
   captions, version, minimum Unity version, and release notes to the Publisher
   Portal draft. Do not copy stale values from an earlier portal version.
1. **Submit for review.** Submit the draft and record that the version was
   submitted (date + reviewer-facing version) in the approved tracker, not in
   this repository.
1. **Check UPM Version History.** After Unity publishes a UPM version, use a
   clean Unity project where it is not installed. Open Package Manager Version
   History, expand the version, and confirm its notes match
   `EXPECTED-UPM-FIELDS.json`. For the
   [per-version changelog experiment](https://github.com/Ambiguous-Interactive/DxMessaging/issues/403),
   repeat this for two versions and verify that both non-installed rows display
   their own expected notes. If the Publisher Portal or supported Unity tooling
   exposes a version manifest, also compare its `name`, `version`, and
   `_upm.changelog` values with the expected fields. Do not query undocumented
   endpoints to obtain it; the Version History result is the acceptance proof.

If the `.unitypackage` export itself failed, the whole release is blocked rather
than shipping a half-release (the export is a required asset); fix the export and
re-run the release workflow before starting this runbook.

## Credentials and access

The upload uses **no GitHub Actions secrets** -- it never runs in CI. The only
credential is the **Unity publisher account**, used interactively in the Editor:

- The account must be an approved Asset Store publisher with the package draft
  created, and the person uploading must hold a publisher role that can submit.
- Complete two-factor authentication when the account requires it. More
  generally, Unity documents only interactive Unity ID authentication and no
  service-account or upload-token alternative.
- Do not store the publisher password, recovery codes, or session cookies in the
  repository or in CI secrets. Keep them in the approved organization password
  manager. (If automation ever becomes sanctioned, wire its credentials as
  secrets through the preflight-degrades-to-no-op pattern used elsewhere in
  `release.yml`; until then there are no Asset Store secrets to manage.)

## Asset Store review constraints

- Submission enters Unity's manual review queue; approval is not immediate and
  is outside the project's control. Plan releases so the Asset Store listing
  trails the npm/OpenUPM publish rather than gating it.
- Review commonly rejects packages for unnecessary dependencies, files that do
  not belong in a shipped package, or metadata links that point at an old
  organization. The staged payload is already pruned to the npm/UPM file set, so
  most of these are pre-empted -- but re-check the listing's links and
  description each release.
- The npm Trusted Publishing provenance and GitHub artifact attestations are
  **not** Unity Asset Store signing; do not describe them as such in the
  listing. Asset Store signing is controlled entirely by Unity's pipeline.

## Re-evaluating automation

Re-check the determination above at each release, or sooner if Unity announces
publishing-tool changes. Switch from this manual runbook to a CI
`publish-asset-store` job **only** when _all_ of these hold:

1. Unity ships a **sanctioned** non-interactive upload entry point -- an
   official `-batchmode`/`-executeMethod` method in `com.unity.asset-store-tools`
   or a documented publisher-portal upload API with a real service token (not a
   harvested browser cookie).
1. The path authenticates **without** an interactive 2FA challenge (a service
   credential or upload token), so a headless runner can complete it.
1. It is documented and supported by Unity, so CI is not coupled to an internal
   API that can break without warning.

When that day comes, add the job after the GitHub Release step in `release.yml`
(the Asset Store upload is the last, most-reversible release action), gate it on
a credential preflight that degrades to a logged no-op when the secret is absent
(mirroring the `AUTO_COMMIT_APP_*` preflight in `perf-numbers.yml` /
`release-prepare.yml`), and update this runbook to describe the automated path
with the manual steps kept as the fallback.

## See also

- [Unity Asset Store UPM](../ops/unity-asset-store-upm.md) -- publisher account
  onboarding, package-content rules, and the conditional UPM submission flow.
- [Release Operations](../ops/release-operations.md) -- the full tagged-release
  pipeline this runbook plugs into.
- [npm Release Publishing](../ops/npm-release-publishing.md) -- the npm/OpenUPM
  half of a release.
