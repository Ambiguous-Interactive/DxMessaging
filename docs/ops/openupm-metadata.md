---
title: OpenUPM Metadata
description: Manual checklist for OpenUPM package metadata after repository transfer
---

# OpenUPM Metadata

OpenUPM indexes Git tags and package metadata to build Unity Package Manager
versions. DxMessaging must continue to publish as:

- Package ID: `com.wallstop-studios.dxmessaging`
- Repository URL: `https://github.com/Ambiguous-Interactive/DxMessaging.git`

## Metadata PR

OpenUPM package metadata lives in the `openupm/openupm` repository. Use the
OpenUPM package add form or edit the package YAML directly.

Verify the metadata includes:

- `name: com.wallstop-studios.dxmessaging`
- display name `DxMessaging`
- repository URL under `Ambiguous-Interactive/DxMessaging`
- correct license
- supported Unity version matching `package.json`
- root package layout

Open a PR if the metadata still points at the old repository. Use a public PR
description only. Keep only non-sensitive verification notes in the local
ignored runbook, such as the public OpenUPM PR URL. Keep npm account notes,
GitHub account notes, private review status, tokens, recovery codes, and other
private account metadata in the relevant provider console or approved
organization password manager.

## Tag Requirements

OpenUPM builds from version tags. The release process uses strict `vX.Y.Z`
tags. Confirm OpenUPM recognizes the next pushed tag after the metadata update.

## Verification

After the PR merges and the next release tag is pushed:

1. Open `https://openupm.com/packages/com.wallstop-studios.dxmessaging/`.
1. Confirm the latest version matches `package.json.version`.
1. Confirm the source repository is `Ambiguous-Interactive/DxMessaging`.
1. Confirm version history includes the `vX.Y.Z` tag.
1. Install in a clean Unity project:

```bash
openupm add com.wallstop-studios.dxmessaging
```

## Failure Modes

- Metadata still points at the old repository.
- The package page updates but version history does not include the new tag.
- OpenUPM cannot detect `package.json` at the repository root.
- README or package metadata links use the old documentation URL.
- A release tag exists but does not match `package.json.version`.
