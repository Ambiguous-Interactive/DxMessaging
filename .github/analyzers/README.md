# Developer-local Unity CI analyzers

No third-party analyzer binary is checked into this repository or included in
the published UPM package. The Unity EditMode harness downloads the exact NuGet
packages in `manifest.json` into its ignored, developer-local cache, verifies
both package and payload SHA-256 values, and copies only the selected analyzer
closure into the disposable test project's `Assets` tree. `csc.rsp` registers
the files for the existing full-source compile, where `-warnaserror` makes every
enabled diagnostic blocking.

The pinned set is:

- [Roslynator.Analyzers 4.16.0](https://www.nuget.org/packages/Roslynator.Analyzers/4.16.0)
  (`Apache-2.0`), using its Roslyn 3.8 payload.
- [Microsoft.Unity.Analyzers 1.22.0](https://www.nuget.org/packages/Microsoft.Unity.Analyzers/1.22.0)
  (`MIT`), the newest tested release that loads in Unity 2021's Roslyn 3.8 host.
- [SonarAnalyzer.CSharp 10.34.0.3385](https://www.nuget.org/packages/SonarAnalyzer.CSharp/10.34.0.3385)
  (`Sonar Source-Available License v1.0`).
- [Microsoft.CodeAnalysis.NetAnalyzers 7.0.4](https://www.nuget.org/packages/Microsoft.CodeAnalysis.NetAnalyzers/7.0.4)
  (`MIT`), the newest tested line that loads in Roslyn 3.8.
- [ErrorProne.NET.CoreAnalyzers 0.4.0-beta.1](https://www.nuget.org/packages/ErrorProne.NET.CoreAnalyzers/0.4.0-beta.1)
  (`MIT`), the newest tested line that loads in Roslyn 3.8; 0.6 and later
  require newer compiler assemblies.

Code-fix and Workspaces assemblies are excluded because Unity's compiler host
does not need them. `manifest.json` is the text-only lock file for package URLs,
versions, licenses, exact archive entries, hashes, and Unity metadata GUIDs.
