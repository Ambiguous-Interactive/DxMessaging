# Contributing

Thanks for helping improve DxMessaging!

Before committing, install the versioned hook bootstrap and local linters so you catch issues early.
Run these steps in order:

1. Install Node dependencies: `npm install`
1. Install versioned native hooks and repair tooling: `npm run repair:hooks`
1. Run Node tooling preflight: `npm run preflight:pre-commit` (includes YAML formatting + yamllint checks)
1. Run on all files: `node scripts/ensure-pre-commit.js run --all-files`

`jest` does not need to be installed globally. Hooks and scripts route through `scripts/run-managed-jest.js` so they can use local devDependencies first, then a managed fallback when needed.

Prettier hooks and npm scripts route through `scripts/run-managed-prettier.js` so format checks and writes use the same resolved Prettier version across local shells and CI.

## Before you push

You do not need to remember which validator to run for each file you touched:

- `npm run preflight` -- change-aware (the default). It inspects exactly what you changed (committed range vs the integration base, plus staged/unstaged/untracked work), delegates file-to-hook selection to pre-commit, self-heals tooling first, and runs the relevant checks in-loop. A push-guard runs it automatically before `git push`. See [Change-Aware Preflight](./.llm/skills/scripting/change-aware-preflight.md).
- `npm run preflight:pre-push` -- full parity with the native pre-push hook (the exhaustive sweep CI runs); use it when you want the complete check set on demand.
- `npm run doctor` -- diagnose and repair the local Node/hook environment (run it after a `testRunner option was not found` / `jest-circus` error or a partial `node_modules` extract).

Windows note: if you use `nvm` or `fnm`, run commits from a shell where Node is initialized (PowerShell or Git Bash) and verify `npm --version` before running hooks.
If you edit `.github/workflows/*.yml`, run `npm run preflight:pre-commit` in that same shell before `git commit`.

Line endings: Git normalizes most text files to **LF** through `.gitattributes`. **Exception:** C#/.NET files (`.cs`, `.csproj`, `.sln`, `.props`) use CRLF per .NET conventions. Run this once after cloning (especially on Windows) to fix your working tree:

```bash
git config core.autocrlf false
node scripts/fix-eol.js
```

This directly converts files in your working directory to the correct line endings. Add `-v` for verbose output showing each file fixed.

> **Note:** You may see references to `git add --renormalize`, but that command only updates the git index (staging area) -- it does **not** modify your working tree files. Use `fix-eol.js` to actually fix files on disk.

## VS Code Security Policy

- Do not commit terminal auto-approval settings (for example `chat.tools.terminal.autoApprove`) to `.vscode/settings.json`.
- Repository settings must not bypass command review prompts for chat-invoked terminal commands.
- If you need personal auto-approval rules, keep them in local user settings, not repository-tracked files.

What runs locally:

- Markdown link text check: enforces human-readable link text (no raw file names/paths)
- Internal markdown link validity: verifies relative links and anchors point to real files/sections
- Markdown style and formatting: markdownlint (auto-fix common issues)
- JSON/.asmdef formatting: Prettier (2-space indent)
- YAML formatting: Prettier (2-space indent) + yamllint
- NPM package validation: ensures all Unity .meta files are properly included in npm package

On pull requests, CI also checks all markdown links (including external URLs) with lychee.

Handy commands:

- Internal links (local): `python .github/scripts/validate_markdown_links.py .`
- Lint markdown (all files): `node scripts/ensure-pre-commit.js run markdownlint --all-files`
- Lint markdown (manual): `npx markdownlint-cli2@0.20.0 "**/*.md" --fix`
- Format JSON/.asmdef (all files): `node scripts/ensure-pre-commit.js run prettier --all-files`
- Format JSON/.asmdef (manual): `node scripts/run-managed-prettier.js --write "**/*.{json,asmdef}"`
- Format YAML (all files): `node scripts/ensure-pre-commit.js run prettier-yaml --all-files`
- Check YAML formatting + lint: `npm run check:yaml`
- Run yamllint hook directly: `node scripts/ensure-pre-commit.js run yamllint --all-files`

Prettier keeps YAML formatting consistent but does not automatically wrap long YAML lines. `yamllint` is the authoritative check for the 200-character YAML line-length rule.

If `npm run check:yaml` reports a YAML line-length failure:

1. For workflow `run:` commands, use folded scalars (`run: >-`) to split long commands across readable lines.
1. For non-command YAML values, break long strings into multiline YAML values where valid, or refactor the content so each line stays within 200 characters.

- Format C#: `dotnet tool restore && dotnet tool run csharpier format`
- Validate pre-commit Node tooling policy: `npm run validate:pre-commit-tooling`
- Run pre-commit Node preflight: `npm run preflight:pre-commit`
- Validate NPM package: `npm run validate:npm-meta`

## Documentation Style and Code Samples

Two strict rules apply to all documentation (Markdown files and `///` XML doc comments) and to every C# code sample:

1. **ASCII-only.** Pure ASCII is required. Real Unicode emojis are allowed only on callout lines (lines starting with `>`), capped at five per file. See the [ASCII-only documentation guideline](./.llm/skills/documentation/ascii-only-docs.md). Run `node scripts/validate-docs-ascii.js` (or `node scripts/normalize-docs-ascii.js` to auto-fix).
1. **Code samples must compile.** Every C# snippet - inline backticks, fenced blocks, table cells, and XML `<code>` blocks - must compile against the snippet harness. See the [Code samples must compile guideline](./.llm/skills/documentation/code-samples-must-compile.md). Run `node scripts/validate-doc-code-patterns.js` and the `DocsSnippetCompilationTests` suite under `SourceGenerators/`.

Both rules are enforced by the consolidated pre-commit hooks (`run-staged-md-pipeline` for `.md` / `.markdown` and `run-staged-validators` for `.cs`) and the `.github/workflows/docs-lint.yml` CI job. The standalone CLI entries (`node scripts/validate-docs-ascii.js`, `node scripts/validate-doc-code-patterns.js`, `node scripts/validate-docs-prose.js`) remain available for ad-hoc invocations.

## NPM Package Validation

Unity requires `.meta` files for every asset to maintain consistent GUIDs across installations. The `validate:npm-meta` script ensures:

1. Every `.meta` file in the package corresponds to an actual file or directory
1. Every Unity-tracked file/directory has its `.meta` file included

This validation runs automatically:

- Before every push via pre-commit hooks
- In CI/CD on every pull request

If validation fails, it means either:

- **Orphaned .meta files**: A `.meta` file exists without its corresponding file/directory (often from deleted files)
- **Missing .meta files**: A file/directory exists without its `.meta` file (Unity will generate a new GUID, breaking references)

To fix issues:

- For orphaned .meta files: Delete the orphaned `.meta` file
- For missing .meta files: Ensure Unity generates the `.meta` file, or copy it from the repository

If you do still need to repair line endings manually (for example, after copying files from an external tool), run `node scripts/fix-eol.js -v` and then re-stage the affected files.

## SourceGenerators Analyzer Troubleshooting

If you open the package in Unity and see project-wide `CS0315` / `CS0452` errors (`type ... cannot be used as type parameter ...; there is no boxing conversion to ...IMessage`), or `CS0006` errors that name a metadata file under `SourceGenerators/.../obj/...dll` or `SourceGenerators/.../bin/...dll`, the cause is stale build output rather than a code change.

Cause: Unity imported the `SourceGenerators/` projects' in-tree `obj/` and `bin/` build DLLs and cached the auto-referenced-plugin registrations in its Library. Those stray DLLs shadow the two real analyzers shipped in `Editor/Analyzers/`, so the wrong assemblies feed the compiler.

To fix it:

1. Close Unity.
1. Delete the Unity **project's** `Library/` folder. A partial **Assets > Reimport** does not clear the cached auto-referenced-plugin registrations, so the full Library delete is required.
1. Confirm no `obj/` or `bin/` folders remain under `SourceGenerators/`. The build is configured to emit output to `.artifacts/`, which Unity ignores.
1. Reopen the project.

Contributor invariant: never let the `SourceGenerators/` projects build their `obj/` or `bin/` in-tree. `SourceGenerators/Directory.Build.props` redirects all output (obj, bin, and restore) to the git-ignored `.artifacts/` tree precisely so Unity never imports a build DLL.
