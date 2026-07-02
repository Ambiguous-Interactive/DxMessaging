---
title: "Release Asset and Notes Invariants"
id: "release-asset-and-notes-invariants"
category: "github-actions"
version: "1.0.0"
created: "2026-06-20"
updated: "2026-06-20"
status: "stable"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: ".github/workflows/release.yml"
    - path: "scripts/release/changelog.js"
    - path: "scripts/release/release-notes.js"
    - path: "scripts/unity/export-unitypackage.ps1"
    - path: "scripts/unity/unity-builtin-modules.json"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "github-actions"
  - "ci-cd"
  - "release"
  - "unity"

complexity:
  level: "intermediate"
  reasoning: "Cross-cutting CI invariants spanning a shared Node extractor, three release workflows, and the ephemeral Unity export project's manifest."

impact:
  performance:
    rating: "none"
---

## Overview

The tag-triggered release pipeline (`release.yml`) ships two artifacts to each
GitHub Release: the npm tarball and a `.unitypackage`, with a body drawn from
`CHANGELOG.md`. Three invariants keep that release correct and complete. All
three regressed at once in `v3.1.0` (run `75151961234`): the release body was a
stub and the `.unitypackage` was silently absent. This skill records the
invariants, the root causes, and the guards so the failure class cannot return.

## Invariant 1: the published body is the CHANGELOG section, never a stub

The GitHub Release body MUST be the matching `## [version]` section of
`CHANGELOG.md` (plus an install footer), produced by the SINGLE shared extractor
`scripts/release/release-notes.js` (backed by `scripts/release/changelog.js`).

- **Anti-pattern (the v3.1.0 bug):** building release notes inline, e.g.
  `printf 'Release %s\n\nPackage: %s\n' ...`. That stub then went to
  `gh release create --notes-file`, so the changelog never reached the release.
- **Why one extractor:** the body, the release-PR excerpt
  (`release-prepare.yml`), and the release-drafter draft (`release-drafter.yml`)
  must agree. Three hand-rolled `awk '/^## \[/'` copies drift and, worse, are
  fence-blind -- a fenced `## [9.9.9]` inside an entry truncates the section.
  `changelog.js` reuses the wiki `CodeBlockTracker`, so fenced headings are never
  treated as boundaries.
- **`--version` takes the bare version** (`3.1.0`), not the tag (`v3.1.0`): the
  heading is `## [3.1.0]`. `verify-tag` already proved the heading exists.

```bash
# release.yml (validate job): authoritative body for the published release
node scripts/release/release-notes.js --version "${PACKAGE_VERSION}" \
  --footer --out .artifacts/release/release-notes.md
```

`extractSection` also accepts the unbracketed `## X.Y.Z` form (verify-tag accepts
both), reads the final section to EOF, and throws on a missing or empty section
so a bad release fails loudly instead of shipping blank notes.

## Invariant 2: the export project enables the full built-in module set

`export-unitypackage.ps1` builds an EPHEMERAL Unity project, stages the npm
payload under `Assets/`, and runs `AssetDatabase.ExportPackage`. That project's
`Packages/manifest.json` MUST enable Unity's built-in modules.

- **Anti-pattern (the v3.1.0 bug):** an empty manifest `{"dependencies": {}}`
  with the comment "built-in modules are enabled by default." They are NOT.
  An empty `dependencies` enables NO `com.unity.modules.*`, so
  `UnityEngine.IMGUIModule` is absent. The runtime settings file calls
  `EditorGUIUtility.PingObject` (inside `#if UNITY_EDITOR`), and
  `EditorGUIUtility`'s base class `GUIUtility` lives in IMGUIModule -> `CS0012`
  -> Unity aborts batchmode -> `ExportPackage` never runs -> no `.unitypackage`.
- **Why the test gate (`unity-checks`) did not catch it:** that job's project
  manifest references `com.unity.test-framework`, which pulls in
  `com.unity.modules.imgui` transitively. The export project references nothing,
  so nothing pulls in any module.
- **Fix:** write the canonical fresh-2022.3 dependency set -- every
  `com.unity.modules.*` plus `com.unity.ugui` -- from the single source
  `scripts/unity/unity-builtin-modules.json`, then merge the package's own UPM
  `dependencies` (empty today; future-proofs a real dependency). Every built-in
  package ships inside the editor install, so listing the full set needs no
  network. This mirrors what a real consumer project lists, so the export
  compiles the payload (and its samples) exactly as a consumer would.

The DI samples are `#if REFLEX_PRESENT` / `VCONTAINER_PRESENT` /
`ZENJECT_PRESENT`-gated, so they stay inert without those packages; the other
samples need only built-in modules. Do NOT add external DI/UI packages to the
export manifest to "fix" a sample -- gate the sample instead.

## Invariant 3: the `.unitypackage` is a required, release-blocking asset

`publish` runs only when every upstream job -- including `unitypackage` --
succeeds (plain `needs` gating, no `always()` escape hatch). A failed export
blocks the IRREVERSIBLE npm publish too, so neither npm nor the Release ever
advertises a version whose `.unitypackage` is missing. The Stage step treats a
missing/empty file as a HARD error. Before npm publish, the
`asset-store-submission.js` generator also stages the Asset Store artifact from
the checked release files, tracked store media, `STORE-LISTING.md`, and the
matching changelog section; missing or stale store collateral fails before the
registry action. A final step asserts the published release carries all four
assets (`.tgz` + `.sha256`, `.unitypackage` + `.sha256`). Recovery is to fix the
export or store collateral and re-run; the npm publish step is re-runnable (it
skips a version already on the registry).

## Guards (red-green)

- `scripts/__tests__/changelog-section.test.js` pins `extractSection`: correct
  section, stops at the next heading, last-section-to-EOF, **fenced `## [x]`
  ignored**, throws on missing/empty, and the `release-notes.js` footer.
- `scripts/__tests__/export-unitypackage-stage.test.js` stages with `-StageOnly`
  and asserts `Packages/manifest.json` enables `com.unity.modules.imgui` and
  every entry of `unity-builtin-modules.json` -- it reproduces the v3.1.0 compile
  failure at the staging level, no licensed editor required.
- `scripts/__tests__/asset-store-submission.test.js` pins the staged Asset Store
  artifact: release files, checksums, `STORE-LISTING.md`, store media, generated
  classic/UPM checklists, and `MANIFEST.json`, with stale checksum and missing
  media failures.
- The `unitypackage` job + the post-publish asset check are the integration
  backstop on a real release.

## How to apply

1. Never hand-build release notes; always call `release-notes.js`.
1. Touch changelog extraction in ONE place (`changelog.js`); the three workflows
   consume it.
1. When the export adds an engine dependency, extend
   `unity-builtin-modules.json` (the test locks the set); never reintroduce an
   empty manifest.
1. Keep the `.unitypackage` a hard gate on `publish`; do not reintroduce an
   `always()` opt-in that lets the release ship without it.
