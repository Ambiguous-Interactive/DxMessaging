---
title: Shipping a Roslyn Analyzer in a Unity UPM Package
id: unity-analyzer-shipping
category: packaging
description: How to ship a Roslyn source generator / analyzer in a Unity UPM package so it reaches consumer code, including Assembly-CSharp, with no asmdef required
version: 1.0.0
created: 2026-06-21
updated: 2026-06-21
date: 2026-06-21
author: AI Assistant
status: stable
complexity:
  level: intermediate
impact:
  performance:
    rating: low
related:
  - ./npm-package-configuration.md
  - ../unity/upm-test-harness.md
tags:
  - unity
  - analyzer
  - source-generator
  - packaging
  - roslyn
---

# Shipping a Roslyn Analyzer in a Unity UPM Package

## Overview

DxMessaging ships Roslyn source generators (`[DxUntargetedMessage]`,
`[DxTargetedMessage]`, `[DxBroadcastMessage]`, `[DxAutoConstructor]`) and a
companion analyzer as compiled DLLs labeled `RoslynAnalyzer`. The hard part is
not building them; it is placing them so Unity applies the generator to consumer
code. This page captures the durable lesson behind the issue #229 fix: where a
folder-resident analyzer must live in a UPM package, and why.

## Unity's analyzer folder-scoping rule

Unity scopes a folder-resident Roslyn analyzer (a DLL labeled `RoslynAnalyzer`)
to a precise set of compilations:

- Folder under an `.asmdef` -> the analyzer applies to **that** assembly **plus
  every assembly that references it**.
- Folder under **no** `.asmdef` -> the analyzer applies to **all predefined
  assemblies** (`Assembly-CSharp`, `Assembly-CSharp-Editor`, and friends).

The DLL's `.meta` carries the label as a YAML sequence:

```text
labels:
- RoslynAnalyzer
```

Without that label Unity treats the DLL as a plain managed plugin (it would try
to load it as runtime code), not a compiler analyzer.

## The editor-only-asmdef trap (issue #229)

The generator DLLs originally shipped under `Editor/Analyzers/`, governed by the
EDITOR-ONLY asmdef `WallstopStudios.DxMessaging.Editor` (an asmdef whose
`includePlatforms` is restricted to `Editor`). Applying the folder rule: the
generator was scoped to the editor assembly and everything that references it.
But **no consumer RUNTIME assembly can reference an editor-only assembly**, so:

- Projects WITHOUT assembly definitions keep their scripts in the predefined
  `Assembly-CSharp` (a runtime assembly). It never referenced the editor
  assembly, so it never received the generator.
- Consumer `[Dx*Message]` types then failed with cryptic `CS0315` / `CS0452`
  errors, because the partial type the generator was supposed to emit never
  existed.

The folder location, not the analyzer code, was the bug.

## The fix: ship under the RUNTIME asmdef

Ship the two `RoslynAnalyzer`-labeled DLLs under `Runtime/Analyzers/`, the folder
governed by the **all-platforms runtime asmdef**
(`WallstopStudios.DxMessaging`). Applying the folder rule, the generator now
reaches:

- the DxMessaging runtime assembly, AND
- every assembly that references it -- which includes the predefined
  `Assembly-CSharp` (predefined assemblies auto-reference package runtime
  assemblies) AND any consumer runtime asmdef that references DxMessaging.

No consumer asmdef is required, so projects that never adopted assembly
definitions are covered.

Crucially this is targeted, not blanket: the runtime asmdef does NOT pull in
unrelated assemblies that have no DxMessaging reference, so the analyzer does not
pollute compilations it has no business inspecting. (Dropping the DLLs in a
no-asmdef folder would have hit every predefined assembly indiscriminately --
correct for `Assembly-CSharp`, but noise everywhere else.)

```text
Runtime/Analyzers/WallstopStudios.DxMessaging.SourceGenerators.dll(.meta)
Runtime/Analyzers/WallstopStudios.DxMessaging.Analyzer.dll(.meta)
```

## Compile-time only: never in player builds

`RoslynAnalyzer`-labeled DLLs are **compiler inputs, not runtime code**. Unity
excludes them from player builds regardless of which folder they sit in -- the
all-platforms runtime asmdef governs the folder for _scoping_ purposes, but the
label keeps the bytes out of the shipped game. So `Runtime/Analyzers/` adds zero
bytes to a consumer's build output; it only changes which compilations see the
generator.

## No consumer-project copy

The package no longer copies analyzer DLLs into the consumer's
`Assets/Plugins/Editor/`; that mechanism is retired. Shipping the labeled DLLs
inside the package is sufficient, and a vendored copy under the consumer's
`Assets/` would double-apply the generator. Projects upgrading from the old
scheme have that stale copy removed automatically.

The automatic upgrade cleanup must stay conservative. Delete the retired
`Assets/Plugins/Editor/WallstopStudios.DxMessaging` folder only when it contains
the first-party source-generator DLL plus exact known legacy analyzer/dependency
DLL names, with optional matching `.dll.meta` sidecars. The analyzer companion
DLL is optional because released 2.x payloads predate it. Preserve the folder for
any foreign DLL, foreign `.meta`, subfolder, duplicate name, or other file so
consumer-owned editor plugins cannot be removed during upgrade.

When a folder contains a DxMessaging legacy payload fragment but is not safe to
auto-delete, log one warning with manual cleanup guidance; do not warn for
folders that contain only unrelated consumer content.

## Drift-guard

`scripts/__tests__/analyzer-runtime-placement.test.js` enforces the placement so
nobody silently regresses issue #229. It asserts:

- both first-party DLLs (`...SourceGenerators.dll`, `...Analyzer.dll`) exist
  under `Runtime/Analyzers/` with their `.meta` sidecars, and
- no `RoslynAnalyzer`-labeled DLL anywhere under `Runtime/` or `Editor/` resolves
  to a nearest enclosing asmdef that is editor-only.

It also carries a red-green sentinel proving the editor-only detection fires if
the DLLs are moved back. Run it with the script suite (`npm test`).

## Building and verifying the payload

The analyzer payload is built and verified by `npm run check:analyzers` (refresh
with `npm run refresh:analyzers`), which builds the source generators in
`Release`, asserts reproducible bytes, and compares the result against the
committed DLLs. The committed analyzer DLLs live in `Runtime/Analyzers/`.

```bash
npm run check:analyzers     # build + verify committed DLLs match
npm run refresh:analyzers   # rebuild and refresh the committed DLLs
```

## Checklist for shipping a folder-resident analyzer

1. Compile the analyzer/generator to a DLL.
1. Label the DLL `RoslynAnalyzer` in its `.meta` (`labels:` sequence).
1. Place it under the folder of the asmdef whose assembly + referrers you want to
   reach -- the **runtime** asmdef to reach `Assembly-CSharp` and runtime
   referrers; never an editor-only asmdef if consumer runtime code must compile.
1. Ship the DLL and its `.meta` in the package (`package.json` "files").
1. Do NOT copy the DLL into the consumer's `Assets/`.
1. Add or keep a drift-guard pinning the placement.

## See Also

- [npm Package Configuration](./npm-package-configuration.md)
- [UPM Test Harness](../unity/upm-test-harness.md)
- [Unity analyzer scope docs](https://docs.unity3d.com/Manual/roslyn-analyzers.html)
