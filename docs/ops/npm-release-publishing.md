---
title: npm Release Publishing
description: Manual setup for npm Trusted Publishing and tag-driven releases
---

# npm Release Publishing

The package name is `com.wallstop-studios.dxmessaging`.

The release workflow publishes from GitHub Actions using npm Trusted Publishing. There is no `NPM_TOKEN` secret. Do not add one unless the release model is changed and reviewed.

## npm Package Access

In npm, verify:

1. The package exists and the current maintainers are correct.
1. Two-factor policy matches organization policy.
1. No stale maintainers remain from the transfer.
1. Package visibility is public.
1. Provenance is visible for versions published from GitHub Actions.

Keep only non-sensitive verification notes in the local ignored runbook, such as the public package URL and the date access was checked. Keep maintainer account details, private
npm account notes, recovery codes, tokens, and other private account metadata in the provider console or approved organization password manager.

## Trusted Publishing Binding

Configure npm Trusted Publishing for:

- GitHub organization: `Ambiguous-Interactive`
- GitHub repository: `DxMessaging`
- Workflow: `.github/workflows/release.yml`
- Environment: `release`

Trusted Publishing uses OIDC. The workflow uses a pinned npm CLI with Trusted Publishing support.

## Release Approval

Configure two protected GitHub environments:

- `release-tag` with wallstop as the required reviewer, restricted to the default branch, with administrator bypass disabled. `release-tag.yml` runs in this environment, waits for
  the review, and verifies the run's approval before creating the tag.
- `release` with wallstop as the required reviewer, restricted to release tags, with administrator bypass disabled. Match it in the npm trusted publisher binding. The publish job
  waits for this review and verifies it before publishing.

Reserve both approvals for human wallstop. Keep approval and bypass authority for these environments outside agent access. The `#575` marker is the durable exact-candidate record;
it is not the identity proof. GitHub's protected-environment review is the separately authenticated human approval, and the workflows require both.

Require the `Release Merge Guard` status check on the default branch. It runs from the trusted base workflow for `release/v*` pull requests and for any PR whose candidate declares
version 4.0.0, checks out and packs the exact candidate without executing candidate scripts, and applies the same hold before merge. Guard checks and issue invalidation share one
serialized concurrency group; any milestone issue change or an edit/delete of a #575 comment marks open release PR guard statuses `pending`, and the guard rechecks the milestone
immediately before posting success. Re-run the guard with `workflow_dispatch` after the milestone settles. The status is valid only for the candidate tree, milestone state and
approval comment at check time. The tag and publish gates always re-read the milestone and approval, so a stale merge-guard status cannot release the package.

Configure a tag ruleset that allows only the auto-commit App to create `v*` tags. The tagger check is defense in depth against accidental manual tags, not identity proof; the
ruleset and the protected environment reviews are the enforceable controls.

### 4.0.0 release hold and order

Do not merge the release PR, tag, dispatch, publish or submit while any open issue in the 4.0.0 milestone remains, or while #575's technical or human gates are incomplete. Use this
order:

1. Finish and close every 4.0.0 milestone issue except #575.
1. Prepare the release PR. Preparation is staging only: it may run while #575 stays open so the exact candidate can be reviewed and approved.
1. Qualify the exact release-PR head with full exact-source CI and package checks.
1. Record wallstop's approval on #575 with the exact version, source tree, package SHA-256 and destinations. Only human wallstop may post the marker. An agent operating through
   wallstop's account must never create, edit or copy it.
1. Close #575 as completed, last among the milestone issues, after the approval marker exists.
1. Re-run the `Release Merge Guard` check on the release PR. It packs the exact PR head and verifies the same hold; merge only when it passes. `release-tag.yml` then creates the
   tag only after wallstop approves the `release-tag` environment review, every milestone issue is closed, and #575's marker matches the release commit's tree.
1. Dispatch `release.yml` from that tag. It repeats the hold before qualification, rejects a 4.0.0 tag whose tagger is not the auto-commit App, and rechecks the hold and exact
   tarball immediately before npm publish. Wallstop approves the `release` environment review before publication.

Closing #575 before the tag does not skip final-revision qualification. The release-PR head is qualified as the final source tree, and the squash-merged release commit is
tree-identical. `release.yml` still requires the exact-revision qualification artifacts at the tag and refuses to publish without them; if that post-closure verification fails, no
publication occurs and #575 is reopened as a release blocker.

For 4.0.0, `release.yml` also requires the annotated tag's tagger to be `github-actions[bot] <github-actions[bot]@users.noreply.github.com>` and a successful `release-tag.yml` run
for the exact tag commit. That run waited on the protected `release-tag` environment review, so the run record and environment approval are the authenticated evidence; the tagger
metadata is defense in depth. A hand-created tag is rejected even when it points at the approved tree.

The approval is an HTML comment on #575, authored by wallstop:

```text
<!-- dxm-release-approval: {"version":"4.0.0","tree":"<40 hex>","package_sha256":"<64 hex>","destinations":["npm","github-release"]} -->
```

The marker must be posted before #575 closes. Add `"asset-store"` only when the Asset Store submission is separately approved for the same candidate. `release-prepare.yml` refuses
a real prepare while any other milestone issue is open; `release-tag.yml` and `release.yml` refuse to tag or publish unless the milestone is empty, #575 is closed as completed, and
the marker matches the exact version, tree and package bytes. Never bypass, disable or weaken these checks.

Compute the marker fields from a clean checkout of the qualified release-PR head. The release workflow packs with the same pinned npm version, so the hash is reproducible:

```bash
git worktree add /tmp/dxm-release-candidate <release-pr-head>
cd /tmp/dxm-release-candidate
git rev-parse 'HEAD^{tree}'
mkdir -p /tmp/dxm-release-candidate-package
npx --yes --package=npm@12.2.2 npm pack --ignore-scripts --pack-destination /tmp/dxm-release-candidate-package
sha256sum /tmp/dxm-release-candidate-package/com.wallstop-studios.dxmessaging-<version>.tgz
```

Confirm the merged tree matches the approved tree before dispatch. Obtain a new decision and a new marker if the candidate changes. This decision also applies to manual tagging,
registry publication and Asset Store submission.

## Release Trigger

The normal path never creates the tag by hand:

1. Dispatch `.github/workflows/release-prepare.yml` from the default branch (bump kind or explicit version). It opens a `release/vX.Y.Z` PR containing the version bump, the rotated
   changelog, the synced banner, and the regenerated `llms.txt`. The auto-commit GitHub App must have Contents write and Pull requests write, and no matching rule may block its
   `release/*` branch push or `v*` tag push.
1. Squash-merge that PR with its default `release: vX.Y.Z` title.
1. `.github/workflows/release-tag.yml` validates the merged commit and pushes the annotated tag `vX.Y.Z` with the auto-commit GitHub App token. It refuses to tag while any 4.0.0
   milestone issue is open or #575's approval marker does not match the merged tree.
1. Qualify the exact tag and dispatch Release after its required evidence is available. [Qualify the tag](./release-operations.md#qualify-the-tag)

Before dispatching `release.yml`, check the hold directly; the workflow repeats it, but a dispatch should never start on a known-blocked milestone:

```bash
gh api "repos/Ambiguous-Interactive/DxMessaging/issues?state=open&milestone=1&per_page=100" \
  --jq 'length'   # must print 0
```

Also confirm #575 is closed as completed and its approval marker names the tag's tree. The release workflow rechecks the milestone, the completed closure and the exact approval
immediately before the irreversible npm publish, and verifies the packed tarball's SHA-256 against the marker.

The tag-creation fallback below applies only to releases other than 4.0.0. 4.0.0 requires the hold-checked `release-tag.yml`: missing App credentials, a non-squash merge, or a
failed App push fails closed, and manual tag creation is not an approved 4.0.0 path. If a 4.0.0 tag is pushed by hand anyway, `release.yml` still refuses to publish unless the hold
is satisfied, and the tag must be deleted. Use a signed tag when signing is available, or the repository-approved annotated tag fallback when signing is not available:

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

## Release Gates

The workflow runs these checks before publishing:

- The 4.0.0 release hold: every milestone issue is closed, #575 is closed as completed, and its approval marker matches the release version, source tree and packed tarball. Real
  release preparation requires every milestone issue except #575 to be closed; preparation is staging only. The full hold is checked before the tag is created, before release
  qualification, and again immediately before npm publish
- Successful compatibility, shipping, consumer-install, and performance runs and retained artifacts for the exact tag commit
- `npm test`
- `npm run validate:all`
- complete managed release test suites
- trusted Unity EditMode release check on a matching self-hosted Windows runner

Run the script and repository validation commands locally from a clean tracked state before tagging. `release-prepare.yml` runs `npm test` and `npm run validate:all` against the
bumped tree before it opens the release PR, so a broken prepare never reaches the tag.

## Artifacts

The release workflow creates:

- npm `.tgz`
- `.sha256` checksum
- GitHub artifact attestation for the `.tgz`
- a required classic `.unitypackage` and its `.sha256`, exported on `ubuntu-latest` from the same validated npm tarball without opening Unity
- GitHub Release assets containing the `.tgz`, its checksum, and the `.unitypackage` pair, verified present by a final post-publish step
- npm package version published with provenance
- the `asset-store-submission` workflow artifact (the `.unitypackage`, the `.tgz`, checksums, store media, ordered screenshots, `CLASSIC-UPLOAD-CHECKLIST.md`,
  `UPM-UPLOAD-CHECKLIST.md`, `ASSET-STORE-LISTING.json`, `EXPECTED-UPM-FIELDS.json`, and `MANIFEST.json`) staged and uploaded for the manual Unity Asset Store upload before npm
  publish; the release-time procedure is the [Asset Store Publishing runbook](../runbooks/asset-store-publishing.md)

The npm publish always runs before the GitHub Release update. The `.unitypackage` is a required asset, so a failed export blocks the entire release (including the npm publish):
releases are atomic. Recovery is to fix the export and re-run; the npm publish is idempotent (it skips a version already on the registry).

## Release Drafter

Release Drafter creates draft release notes from pull requests and changelog content. The tag template is `v$RESOLVED_VERSION`, matching the release workflow.

`release.yml` writes the published release body from `CHANGELOG.md`: the matching `## [version]` section plus an install footer, via the shared `scripts/release/release-notes.js`
extractor (the same one Release Drafter and `release-prepare.yml` use). `CHANGELOG.md` is the source of truth for the release body; the Release Drafter draft is a PR-categorized
preview.

## Failure Modes

- npm Trusted Publishing still points at the old GitHub repository.
- npm Trusted Publishing is configured for a GitHub environment that the workflow does not use.
- A maintainer adds `NPM_TOKEN`, bypassing the OIDC model.
- The GitHub Release step fails after npm publish. The publish step is idempotent (an already-published version is skipped), so re-running the workflow finishes the GitHub Release
  without a duplicate-publish error.
- Release assets are confused with Unity Asset Store uploads. The GitHub Release `.unitypackage` is the same file staged in the `asset-store-submission` artifact, but the Asset
  Store submission itself is a separate manual step through the publisher portal.
