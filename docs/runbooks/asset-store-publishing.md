# Asset Store Publishing Runbook

This runbook is the release-time procedure for shipping a tagged
`com.wallstop-studios.dxmessaging` release to the Unity Asset Store. It assumes
the publisher account is already onboarded; for account setup, package-content
rules, and the UPM-vs-classic submission choice, see
[Unity Asset Store UPM](../ops/unity-asset-store-upm.md).

The Asset Store upload is **manual by design**: there is no sanctioned
non-interactive upload path (see the determination below). The release pipeline
removes every manual step it can -- it stages the exact upload payload and a
generated checklist as a workflow artifact -- so the human step is reduced to
"download the artifact, then drive the Editor uploader." Treat this runbook as
the source of truth for that step; keep account IDs, screenshots, and review
correspondence out of it.

## Automation determination (re-verified 2026-06-22)

There is **no sanctioned (official, documented, supported) CLI, API, or headless
mode** for uploading a package to the Unity Asset Store. The upload must be
driven interactively through the Unity Editor by a signed-in publisher. Evidence:

1. **Official Asset Store Publishing Tools** (`com.unity.asset-store-tools`,
   latest **v12.0.0**, released 2025-01-13): the package's stated purpose is to
   "prepare, validate, and package your assets for submission through
   publisher.unity.com." Its entire documented workflow is the Editor GUI
   (`Tools > Asset Store > Uploader`), which requires an interactive Unity ID
   login and an `Export and Upload` button click. There is no `-batchmode`,
   `-executeMethod`, command-line, API-token, or CI entry point.
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
**not** fake one against an internal API. The publish step stays one-click
manual, backed by the staged artifact below. This is revisited each release; see
[Re-evaluating automation](#re-evaluating-automation).

## What the release pipeline stages for you

Every tagged release runs the `publish` job in
[`.github/workflows/release.yml`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/release.yml).
After the
npm publish and the GitHub Release succeed, that job uploads an
`asset-store-submission` workflow artifact (30-day retention) containing the
exact inputs for the Asset Store upload:

- the `.unitypackage` exported by
  [`scripts/unity/export-unitypackage.ps1`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/unity/export-unitypackage.ps1)
  (the Assets-form payload: `Samples~` renamed to `Samples`, the loose generator
  sources replaced by the shipped `Runtime/Analyzers/` RoslynAnalyzer DLLs);
- the npm `.tgz` (the exact UPM payload, for reference and diffing);
- `.sha256` checksums for both;
- a generated `SUBMISSION-CHECKLIST.md` carrying the version, the pinned build
  editor (Unity 2022.3.45f1), and the manual upload steps.

The `.unitypackage` is also attached to the GitHub Release, so a maintainer can
grab it from either place. The pipeline stops at staging; it never contacts the
Asset Store.

## Release-time procedure

Run this once the release workflow for the tag is green.

1. **Get the payload.** Download the `asset-store-submission` artifact from the
   release workflow run (Actions tab -> the `release` run for the tag ->
   Artifacts), or download the `.unitypackage` from the matching GitHub Release
   assets. Open `SUBMISSION-CHECKLIST.md` from the artifact and keep it beside
   you -- it is generated for this exact version.
1. **Verify integrity.** Confirm the `.unitypackage` SHA-256 matches its
   `.sha256` sidecar before uploading (use `Get-FileHash` on Windows,
   `shasum -a 256` elsewhere). This catches a truncated download before it
   reaches review.
1. **Sign in.** Open a Unity Editor (any version on the supported matrix; the
   payload was exported from the pinned 2022.3.45f1), install the **Asset Store
   Publishing Tools** package, and sign in to the publisher account via the
   Editor's Unity ID login. Complete two-factor authentication when prompted --
   this is the step that cannot be automated, because the upload is bound to an
   interactive, 2FA-gated session.
1. **Select the draft.** In `Tools > Asset Store > Uploader`, select the
   DxMessaging package draft (create the draft once in the publisher portal if
   it does not exist yet).
1. **Import and upload.** Import the staged `.unitypackage` into a clean project
   and upload its content through the uploader (`Export and Upload`). Do not
   re-export from a working tree -- upload the staged payload so the Asset Store
   build matches the npm/UPM build byte-for-byte.
1. **Fill metadata.** Set the version to the released version and paste the
   release notes from the matching `## [X.Y.Z]` section of
   [`CHANGELOG.md`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/CHANGELOG.md).
   Refresh screenshots, the compatibility
   matrix (supported Unity versions from `package.json`), and the description if
   they changed.
1. **Submit for review.** Submit the draft and record that the version was
   submitted (date + reviewer-facing version) in the approved tracker, not in
   this repository.

If the `.unitypackage` export itself failed, the whole release is blocked rather
than shipping a half-release (the export is a required asset); fix the export and
re-run the release workflow before starting this runbook.

## Credentials and access

The upload uses **no GitHub Actions secrets** -- it never runs in CI. The only
credential is the **Unity publisher account**, used interactively in the Editor:

- The account must be an approved Asset Store publisher with the package draft
  created, and the person uploading must hold a publisher role that can submit.
- Two-factor authentication is required and is the hard blocker against
  automation: a non-interactive job cannot satisfy the 2FA challenge, and Unity
  exposes no service-account or upload-token alternative.
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
  onboarding, package-content rules, and the UPM early-access submission flow.
- [Release Operations](../ops/release-operations.md) -- the full tagged-release
  pipeline this runbook plugs into.
- [npm Release Publishing](../ops/npm-release-publishing.md) -- the npm/OpenUPM
  half of a release.
