---
name: package-publishing
description: "Controlling what ships in the com.wallstop-studios.dxmessaging npm/UPM package: the package.json files allowlist versus .npmignore exclusions, Unity .meta pairing rules for every shipped file and directory, the issue #204 tarball invariants verified against npm pack --json --dry-run, and where RoslynAnalyzer-labeled DLLs must live (Runtime/Analyzers/, never an editor-only asmdef) so the source generator reaches Assembly-CSharp. Use when adding or excluding files from the package, adding a new output directory, seeing CS0315/CS0452 on consumer [Dx*Message] types, or building and verifying the analyzer payload."
metadata:
  category: "packaging"
  tags: "npm, packaging, configuration, files, npmignore, unity"
---

# Package Publishing

The package ships to npm and is consumed as a Unity UPM package. Two things decide whether a
consumer's project works: which files land in the tarball (with their `.meta` companions), and
where the `RoslynAnalyzer`-labeled DLLs sit inside it.

## When to use

- Adding, moving, or excluding files from the published package.
- A script starts writing to a new top-level output directory.
- Build artifacts or orphaned `.meta` files show up in the tarball.
- Consumer `[DxTargetedMessage]` / `[DxUntargetedMessage]` / `[DxBroadcastMessage]` /
  `[DxAutoConstructor]` types fail with `CS0315` or `CS0452`.
- Building, refreshing, or verifying the committed analyzer DLLs.

## Rules

### files versus .npmignore

- `package.json` `"files"` is a pure ALLOWLIST of what to include. `.npmignore` is an
  EXCLUSION list applied to what the allowlist already admitted. The pipeline is
  repository files, then the allowlist, then the exclusions.
- Never use negated (`!`) patterns in `"files"`. Put the exclusion in `.npmignore` instead.
- Prefer specific glob patterns over broad wildcards for complex trees. `SourceGenerators/`
  ships individual patterns
  (`SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators/*.cs`, `*.csproj`,
  `Directory.Build.props`) rather than `SourceGenerators/**`, so the test project needs no
  exclusion.
- npm always includes `package.json`, `README`, `LICENSE`, and `CHANGELOG` regardless of
  configuration.
- `.npmignore` still earns its place for subdirectories inside broadly included paths, for
  build artifacts (`**/bin/`, `**/obj/`, `**/.vs/`, `**/*.pdb`), and as defense in depth if the
  allowlist changes. It is NOT a copy of `.gitignore`. Give it a header explaining the
  allowlist/exclusion split and organize it into commented sections.

### Unity .meta pairing

- Every included file and directory needs its `.meta` in the allowlist: `Editor.meta`,
  `Runtime.meta`, `SourceGenerators.meta`, `package.json.meta`, `README.md.meta`, and so on.
- Every excluded directory must have its `.meta` excluded too, or the tarball ships orphaned
  metadata. Always pair them: `Tests/` with `Tests.meta`, `scripts/` with `scripts.meta`,
  `SourceGenerators/....Tests/` with `SourceGenerators/....Tests.meta`.

### Tarball invariants (issue #204)

Verify these against real `npm pack --json --dry-run` output, never a config-string proxy,
whenever packaging metadata changes:

1. No `bin/`, `obj/`, `*.pdb`, `*.tmp`, `*.csproj.user`, `.vs/`, `.idea/`, `*.suo`, or
   `*.DotSettings.user` paths appear in the tarball.
1. Every shipped Unity-relevant path has its `.meta` neighbour (`Foo.cs` with `Foo.cs.meta`,
   `Foo.asmdef` with `Foo.asmdef.meta`).
1. Every shipped directory has its directory `.meta`. If `Runtime/Core/Foo.cs` ships, so must
   `Runtime/Core.meta` and `Runtime.meta`.

When a script writes to a new top-level directory (`.artifacts/`, `.profiler-output/`,
`.unity-test-project/`), add it to BOTH `.gitignore` and `.npmignore` in the same change.

### Shipping the Roslyn analyzer

- Unity scopes a folder-resident analyzer DLL (one labeled `RoslynAnalyzer` in its `.meta`
  `labels:` sequence) by the nearest enclosing asmdef: the analyzer applies to that assembly
  AND every assembly that references it. With no enclosing asmdef it applies to all predefined
  assemblies.
- The DLLs must ship under `Runtime/Analyzers/`, governed by the all-platforms runtime asmdef
  `WallstopStudios.DxMessaging`. That reaches the runtime assembly plus every referrer,
  including the predefined `Assembly-CSharp`, so consumers who never adopted asmdefs are
  covered, without polluting unrelated compilations.
- Never ship them under an editor-only asmdef. That was issue #229: `Editor/Analyzers/` under
  `WallstopStudios.DxMessaging.Editor` meant no consumer RUNTIME assembly could reference it,
  so `Assembly-CSharp` never got the generator and `[Dx*Message]` types failed with `CS0315` /
  `CS0452`.
- Without the `RoslynAnalyzer` label Unity loads the DLL as a plain managed plugin. With it,
  Unity treats the DLL as a compiler input and excludes it from player builds, so
  `Runtime/Analyzers/` adds zero bytes to a consumer's build output.
- Do not copy analyzer DLLs into the consumer's `Assets/Plugins/Editor/`; that mechanism is
  retired and a vendored copy double-applies the generator. The automatic upgrade cleanup
  deletes the retired folder only when it holds the first-party source-generator DLL plus exact
  known legacy analyzer/dependency DLL names with optional `.dll.meta` sidecars; any foreign
  file, subfolder, or duplicate name preserves the folder and logs one warning.
- `scripts/__tests__/analyzer-runtime-placement.test.js` pins the placement: both first-party
  DLLs plus `.meta` sidecars exist under `Runtime/Analyzers/`, and no `RoslynAnalyzer`-labeled
  DLL under `Runtime/` or `Editor/` resolves to an editor-only asmdef.

### Verification commands

```bash
npm pack --dry-run          # inspect the tarball contents
npm run check:analyzers     # build the generators in Release and verify committed DLLs match
npm run refresh:analyzers   # rebuild and refresh the committed DLLs
npm test                    # runs the packaging and analyzer-placement drift guards
```

## References

| Document                                                                                | Purpose                                                                                                                      |
| --------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| [npm-package-configuration-part-1.md](./references/npm-package-configuration-part-1.md) | The live package.json files allowlist and .npmignore for this package, with the mechanism summary table                      |
| [npm-package-configuration.md](./references/npm-package-configuration.md)               | Allowlist versus exclusion semantics, Unity .meta pairing rules, common mistakes, and the issue #204 tarball invariants      |
| [unity-analyzer-shipping.md](./references/unity-analyzer-shipping.md)                   | Unity's analyzer folder-scoping rule, the issue #229 editor-only-asmdef trap, the Runtime/Analyzers fix, and the drift guard |
