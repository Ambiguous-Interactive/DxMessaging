# Contributing

Thanks for helping improve DxMessaging!

Developer setup (run these steps in order):

1. Install Node dependencies: `npm install` (no `package-lock.json` is tracked, so `npm ci` will not work)
1. Restore the .NET local tools (CSharpier): `dotnet tool restore` - without this, the first commit touching a `.cs` file fails because the csharpier hook cannot find the tool
1. Install the pre-commit framework: `pipx install pre-commit` (or `pip install pre-commit`)
1. Install the git hooks: `pre-commit install`
1. Optionally run everything once: `pre-commit run --all-files && pre-commit run --all-files --hook-stage pre-push` (the second command is needed because `pre-commit run` defaults to commit-stage hooks; spelling, asmdef validation, and the script tests are staged at pre-push)

Git hooks are managed solely by the standard [pre-commit framework](https://pre-commit.com); the hook set lives in `.pre-commit-config.yaml`. If you contributed before the tooling simplification and your local clone still points `core.hooksPath` at the removed `scripts/hooks` directory, clear it once with `git config --local --unset core.hooksPath` and re-run `pre-commit install`, or commits will fail looking for deleted hook scripts.

## Before you push

- Script tests: `npm test` (the built-in `node --test` runner; no jest)
- Repo validators: `npm run validate:all` (note: the `check:analyzers` step builds `SourceGenerators/` and requires the exact .NET SDK pinned in `SourceGenerators/global.json` — `rollForward` is `disable`, so a nearby 9.0.3xx SDK does not satisfy it. The devcontainer installs the pinned SDK during post-create; if `dotnet build` still reports a missing SDK, install it into the existing dotnet host location with: `wget https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh && sudo bash /tmp/dotnet-install.sh --jsonfile SourceGenerators/global.json --install-dir /usr/share/dotnet`. The `--install-dir /usr/share/dotnet` part matters: the default `~/.dotnet` location is not consulted by the system `dotnet` host on `PATH`.)
- Formatting: `npm run format:check` (fix with `npm run format`)
- Markdown lint: `npm run lint:markdown`
- Spelling: `npm run check:spelling`
- All hooks at once: `pre-commit run --all-files && pre-commit run --all-files --hook-stage pre-push` (the pre-push stage covers cspell, asmdef validation, and the script tests)

Windows note: if you use `nvm` or `fnm`, run commits from a shell where Node is initialized (PowerShell or Git Bash) and verify `npm --version` before running hooks.

Line endings: Git normalizes most text files to **LF** through `.gitattributes`. **Exception:** C#/.NET files (`.cs`, `.csproj`, `.sln`, `.props`) use CRLF per .NET conventions. Run this once after cloning (especially on Windows) to fix your working tree:

```bash
git config core.autocrlf false
git add --renormalize .
git checkout -- .
```

The pre-commit `mixed-line-ending` hook keeps line endings honest at commit time.

## VS Code Security Policy

- Do not commit terminal auto-approval settings (for example `chat.tools.terminal.autoApprove`) to `.vscode/settings.json`.
- Repository settings must not bypass command review prompts for chat-invoked terminal commands.
- If you need personal auto-approval rules, keep them in local user settings, not repository-tracked files.

What runs locally (via `pre-commit`, see `.pre-commit-config.yaml`):

- Markdown style and formatting: markdownlint-cli2 + Prettier
- JSON/.asmdef formatting: Prettier (2-space indent)
- YAML formatting: Prettier (2-space indent) + yamllint
- C# formatting and naming: CSharpier + the underscore-method auto-fixer
- Banner SVG sync, llms.txt freshness, spelling (cspell), asmdef reference
  validation, and the `node --test` script suite

On pull requests, CI checks markdown links with lychee in two passes. An offline pass validates relative/local links and in-repo `#anchor` fragments against the working tree and blocks the PR on any broken one. A lenient external-liveness pass fails only on genuinely-dead links (404/410 or a DNS/connection failure); bot-detection and throttling responses (401/403/405/406/408/415/429/5xx) are accepted, so a `w3.org` 403 never reds a PR. The fix for a flaky external link is to widen `accept` in `.lychee.toml`, never to add a per-domain `exclude` or swap to a "more stable" URL. Deep external rot is caught by a scheduled advisory scan that opens a tracking issue instead of failing CI.

Handy commands:

- Lint markdown: `npm run lint:markdown` (auto-fix: `npx markdownlint-cli2 --fix "**/*.md"`)
- Format markdown/JSON/.asmdef/YAML: `npm run format` (check-only: `npm run format:check`)
- Run the yamllint hook directly: `pre-commit run yamllint --all-files`
- Format C#: `dotnet tool restore && dotnet tool run csharpier format .` (the trailing `.` is required; without a path, `csharpier format` reads stdin and formats nothing)

Prettier keeps YAML formatting consistent but does not automatically wrap long YAML lines. `yamllint` is the authoritative check for the YAML line-length rule; for workflow `run:` commands, use folded scalars (`run: >-`) or multiline blocks (`run: |`) to split long commands across readable lines.

## Documentation Style and Code Samples

Two strict rules apply to all documentation (Markdown files and `///` XML doc comments) and to every C# code sample:

1. **ASCII-only.** Pure ASCII is required. Real Unicode emojis are allowed only on callout lines (lines starting with `>`), capped at five per file. See the [ASCII-only documentation guideline](./.llm/skills/documentation/ascii-only-docs.md).
1. **Code samples must compile.** Every C# snippet - inline backticks, fenced blocks, table cells, and XML `<code>` blocks - must compile against the snippet harness. See the [Code samples must compile guideline](./.llm/skills/documentation/code-samples-must-compile.md) and run `dotnet test .docs-tests/WallstopStudios.DxMessaging.Docs.Tests.csproj`.

## NPM Package Validation

Unity requires `.meta` files for every asset to maintain consistent GUIDs across installations. When changing packaging metadata, verify against the real tarball with `npm pack --dry-run` that:

1. Every `.meta` file in the package corresponds to an actual file or directory
1. Every Unity-tracked file/directory has its `.meta` file included

If a check fails, it means either:

- **Orphaned .meta files**: A `.meta` file exists without its corresponding file/directory (often from deleted files)
- **Missing .meta files**: A file/directory exists without its `.meta` file (Unity will generate a new GUID, breaking references)

To fix issues:

- For orphaned .meta files: Delete the orphaned `.meta` file
- For missing .meta files: Ensure Unity generates the `.meta` file, or copy it from the repository

If you need to repair line endings manually (for example, after copying files from an external tool), run `git add --renormalize . && git checkout -- .` and then re-stage the affected files.

## SourceGenerators Analyzer Troubleshooting

If you open the package in Unity and see project-wide `CS0315` / `CS0452` errors (`type ... cannot be used as type parameter ...; there is no boxing conversion to ...IMessage`), or `CS0006` errors that name a metadata file under `SourceGenerators/.../obj/...dll` or `SourceGenerators/.../bin/...dll`, the cause is stale build output rather than a code change.

Cause: Unity imported the `SourceGenerators/` projects' in-tree `obj/` and `bin/` build DLLs and cached the auto-referenced-plugin registrations in its Library. Those stray DLLs shadow the two real analyzers shipped in `Runtime/Analyzers/`, so the wrong assemblies feed the compiler.

To fix it:

1. Close Unity.
1. Delete the Unity **project's** `Library/` folder. A partial **Assets > Reimport** does not clear the cached auto-referenced-plugin registrations, so the full Library delete is required.
1. Confirm no `obj/` or `bin/` folders remain under `SourceGenerators/`. The build is configured to emit output to `.artifacts/`, which Unity ignores.
1. Reopen the project.

Contributor invariant: never let the `SourceGenerators/` projects build their `obj/` or `bin/` in-tree. `SourceGenerators/Directory.Build.props` redirects all output (obj, bin, and restore) to the git-ignored `.artifacts/` tree precisely so Unity never imports a build DLL.
