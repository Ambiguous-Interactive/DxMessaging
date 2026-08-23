---
name: changelog-management
description: "How to write and maintain CHANGELOG.md for DxMessaging using Keep a Changelog format: which changes earn an entry and under which category (Added/Changed/Deprecated/Removed/Fixed/Security), how to phrase user-facing entries, how semantic versioning maps to change type, and how to cut a release by converting [Unreleased] and updating the compare links. Use when adding a changelog entry, flagging a BREAKING change with migration notes, preparing a release, or reviewing a vague entry like 'Fixed bugs'."
metadata:
  category: "documentation"
  tags: "changelog, documentation, versioning, semantic-versioning, release-notes, keep-a-changelog, user-communication"
---

# Changelog Management

`CHANGELOG.md` follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). It records what package consumers
experience, not internal tooling. It is also the source the release pipeline extracts the
published GitHub Release body from, so its section headings must stay well-formed.

## When to use

- Landing any user-facing change: new API, behavior change, bug fix, deprecation, removal.
- Writing or reviewing a `BREAKING` entry that needs migration instructions.
- Cutting a release: converting `[Unreleased]` to a versioned section and fixing links.
- Deciding whether a change belongs in the changelog at all.

## Rules

### Categories and eligibility

- Use only these section headings: `Added`, `Changed`, `Deprecated`, `Removed`, `Fixed`,
  `Security`.
- Always add an entry for: new public API (Added), bug fix (Fixed), performance improvement
  (Changed), breaking change (Changed or Removed), deprecation (Deprecated), vulnerability
  patch (Security).
- Never add an entry for: internal refactoring with no API change, test additions, AI agent
  guidance, skill files, or other developer-facing process documentation. Package consumers
  never see them.
- The changelog records library behavior: what a package consumer experiences in Unity. Runtime
  and editor code, public API, samples, and shipped settings qualify. Documentation-site content
  or layout, README text, brand artwork (marks, banners, cards, favicons), CI, and repository
  tooling never earn entries, even when the changed files ship inside the package.
- Dependency updates earn an entry only when they change what a consumer has to do.

### Entry length

An entry is read in the Unity Package Manager and in the GitHub Release body, by someone
deciding whether to upgrade. Keep it to what that reader needs:

- One sentence. A second short clause is allowed only when the change removes a limit or
  changes what the reader must do. Never a paragraph.
- Two wrapped source lines, three when the issue link does not fit.
- No implementation narrative: internal data structures, retained capacity, snapshot
  materialization order, CI policy, pinned test dependencies, and benchmark mechanics are all
  invisible to the reader and belong in the pull request.
- Fold entries that describe one user-visible change into one entry with both issue links.
- Marketing adjectives ("comprehensive", "robust") are banned here exactly as in the docs.

### Entry quality

- Describe user impact, not implementation. "Improved message routing performance for buses
  with many handlers", not "Refactored MessageRouter to use Dictionary instead of List".
- Start with a verb: Add, Fix, Change, Remove.
- Be specific about scope. Name the exact type, assembly, or component. "Updated test assembly
  definitions" is too broad to be true; name the asmdef and the effect.
- Link the issue or PR: `([#178](https://github.com/Ambiguous-Interactive/DxMessaging/issues/178))`.
- Mark breaking changes explicitly with a leading `**BREAKING**:` and give the migration path,
  either inline or in a `### Migration` subsection with before/after `csharp` blocks.
- Banned entries: `Fixed bugs`, `Various improvements`, `Code cleanup`, and any entry that
  restates a method signature change without saying it is breaking.

### Semantic versioning

- MAJOR: public API removed or behavior changed incompatibly.
- MINOR: backward-compatible feature added, or an API deprecated.
- PATCH: bug fix or a compatible performance improvement.

### Release workflow

- During development, entries accumulate under `## [Unreleased]`.
- To cut a release, insert a new `## [X.Y.Z] - YYYY-MM-DD` heading below `## [Unreleased]` and
  move the accumulated entries into it. Leave `## [Unreleased]` in place and empty.
- Version headings are bracketed and unprefixed: `## [2.1.4] - 2026-01-22`. Not `v2.1.4`, not
  bare `2.1.4`. Dates are ISO 8601.
- Maintain reference links at the bottom of the file. Each released version compares against
  its predecessor; the oldest one points at its release tag:

  ```markdown
  [Unreleased]: https://github.com/Ambiguous-Interactive/DxMessaging/compare/v2.1.4...HEAD
  [2.1.4]: https://github.com/Ambiguous-Interactive/DxMessaging/compare/v2.1.3...v2.1.4
  [2.1.2]: https://github.com/Ambiguous-Interactive/DxMessaging/releases/tag/v2.1.2
  ```

- Never edit a released section after the fact. A fix discovered post-release goes under
  `[Unreleased]`; retroactive edits desync the published release notes from what shipped.
- CI can assert the shape: `grep -q "## \[Unreleased\]" CHANGELOG.md` plus a check that the
  `package.json` version has a matching `## [${VERSION}]` heading.

### Where consumers read it

- The Package Manager's Version History tab renders `package.json`'s `_upm.changelog`, which
  mirrors the section for the shipped version. `npm run sync:upm-changelog` writes it,
  `release-prepare.yml` regenerates it after the version bump, and `check:upm-changelog` gates
  drift in `validate:all`. Never hand-edit the field.
- The details panel and Version History also expose one **Changelog** link built from
  `changelogUrl`, with the packaged `CHANGELOG.md` as the offline option. Keep `changelogUrl`
  pointing at a RENDERED page; a raw Markdown URL opens unformatted text in the browser.
- Both surfaces were traced in the editor, including why the field ships in the manifest rather
  than in registry metadata. See
  [upm-changelog-surface.md](./references/upm-changelog-surface.md) before changing either.

### Interaction with the release pipeline

- `scripts/release/release-notes.js` (backed by `scripts/release/changelog.js`) extracts the
  matching `## [version]` section as the published Release body. A malformed or empty section
  fails the release loudly. See the `github-workflow-consistency` skill for those invariants.

## References

| Document                                                                            | Purpose                                                                                               |
| ----------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------- |
| [changelog-entry-writing-part-1.md](./references/changelog-entry-writing-part-1.md) | Why retroactive edits to a released section are wrong                                                 |
| [changelog-entry-writing.md](./references/changelog-entry-writing.md)               | Entry template, worked examples for added/fixed/deprecated/breaking, and the anti-pattern catalog     |
| [changelog-management.md](./references/changelog-management.md)                     | Keep a Changelog categories, file structure, the change-type eligibility table, and semver mapping    |
| [changelog-release-workflow.md](./references/changelog-release-workflow.md)         | Unreleased-to-release conversion, version and date formats, compare links, and CI format validation   |
| [upm-changelog-surface.md](./references/upm-changelog-surface.md)                   | Where the Unity Package Manager reads the changelog, and why inline per-version notes are unavailable |
