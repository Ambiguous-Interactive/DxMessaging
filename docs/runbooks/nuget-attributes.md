# Publish the engine-free attributes package

`WallstopStudios.DxMessaging.Attributes` packages the seven public attributes and the
two shipped compiler DLLs for plain .NET consumers. It is free and MIT licensed.
The runtime assembly targets .NET Standard 2.0. The C# 9 and Roslyn 3.8 floors remain
unchanged. The Unity UPM package includes the same attribute sources and compiler inputs.

## Build and test locally

Run from the repository root with the SDK in `global.json`:

```bash
dotnet pack .net-attributes/DxMessaging.Attributes.csproj --configuration Release --output .artifacts/nuget
dotnet test .nuget-tests/DxMessaging.Attributes.Package.Tests.csproj --configuration Release
```

The local version is `0.0.0-local`. The consumer uses `PackageReference`, rather than
a project reference or linked attribute sources. It compiles the same rules fixture as
the native engine-free Unity test assembly. The constructor/default-value tests and
assembly-reference checks must pass. CI uses a fresh package cache for each run so an
older package with the same local version cannot satisfy the test.

The absence control removes exactly the two packaged compiler inputs before compilation:

```bash
dotnet build .nuget-tests/DxMessaging.Attributes.Package.Tests.csproj --configuration Release --no-restore --target Rebuild -p:DisableDxGenerators=true
```

Expect `CS1729` for the generated constructor calls. This command must fail. Re-run the
normal consumer test without `DisableDxGenerators` to prove recovery. Keep the failed
build log with the positive results. CI runs this sequence automatically.

## Publish a version

Merge the reviewed source only after `CI Success`, `Unity CI Success`, and all other
applicable checks pass. Keep the attributes issue open until live-feed verification passes.

Use the [Attributes NuGet package workflow](../../.github/workflows/attributes-package.yml).
Choose the `master` branch, an unused version such as `4.0.0-preview.1`, and mode `build`
to inspect the package and consumer result artifact first. Use lowercase prerelease labels.
Record the exact workflow commit; use that same source for publication and verification.

Publication needs a NuGet.org account that owns or can create this package and a repository
secret named `NUGET_API_KEY` with permission to push it. Configure that account and key
before selecting mode `publish`. The workflow refuses publication from another branch or
with `0.0.0-local`. It uploads the package and its SHA-256 before attempting the push.

NuGet.org indexes uploads asynchronously. After the version is available through the
NuGet.org v3 feed, run mode `verify` with the same version and source commit. This mode
does not push another package. It restores from NuGet.org into a fresh cache, runs the
consumer, and compares every library, analyzer, and README payload with the source-bound
local package. Retain the live consumer results and workflow URL in the issue.

Versions on NuGet.org are immutable. If the push succeeded but verification is pending,
use mode `verify`; do not repeat `publish` or change a released version's payload.
If master advanced, select a tag that identifies the original workflow commit and includes
this workflow. A payload mismatch must be investigated, not skipped.

Update the package README and installation instructions with the actual verified version
after the first publication. A local nupkg or a successful upload does not prove a consumer
can restore the public package.

## References

- [NuGet MSBuild packaging](https://learn.microsoft.com/en-us/nuget/create-packages/creating-a-package-msbuild)
- [NuGet analyzer conventions](https://learn.microsoft.com/en-us/nuget/reference/analyzers-conventions)
- [Unity assembly migration](../guides/migration-guide.md#reference-the-dedicated-attributes-assembly)
