# Unity CI analyzers

The four DLLs here are the analyzer and its complete Roslyn 3.8 dependency
closure from `Roslynator.Analyzers` 4.16.0. The Unity EditMode CI host copies
them into its disposable `Assets` tree and registers all four through the
project-wide `csc.rsp`. None of these files are part of the published UPM
package.

- Source: <https://www.nuget.org/packages/Roslynator.Analyzers/4.16.0>
- License: Apache-2.0
- NuGet package SHA-256: `8fdf744e36778e6d3c00c5ea89e94fd36810588d3cd8a572301d9d9934c28a9b`
- Vendored DLL SHA-256: `3f104ae829826e063b36ea4c11df2fd595ae482ddf76c58c09530486e1ebf853`
- Common dependency SHA-256: `4b3133ce1d4f52e17e6b488a1b7e7eb3d768e4d705c50d3482f8ca65e91cc834`
- Core dependency SHA-256: `bab462206bdb9653cc61f39b13b47042d82b8fcc189ab73eaf76452f2f369424`
- CSharp dependency SHA-256: `c69267920234e720e5c93f0eec218d522547edd1e67ec2e295f42c5a2b89de70`

The code-fix and Workspaces assemblies are deliberately excluded because
Unity's compiler host does not need them. See `LICENSE.txt` for the bundled
Apache-2.0 license.
