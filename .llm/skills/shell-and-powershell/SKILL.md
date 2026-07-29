---
name: shell-and-powershell
description: "Writing bash and PowerShell that survives CI: set -e error handling for grep/diff/rm, quoting, filename case sensitivity on Linux runners, the PowerShell StrictMode 0/1/many gotcha that requires @()-wrapping every captured result before reading .Count or indexing, here-string quoting, WriteAllText encoding, regex non-greedy versus character-class exclusion, accurate regex comments for the g/i/m/s/u flags, Windows PE-binary stub requirements, cross-drive path containment, and tar archive operands. Use when adding or editing a .sh, .ps1, or Node script, or when a script passes locally and fails on a Linux or Windows runner."
metadata:
  category: "scripting"
  tags: "cross-platform, case-sensitivity, testing, powershell, javascript, ci-cd, linux, windows, macos"
---

# Shell and PowerShell

Repository scripts run on Linux CI, self-hosted Windows runners, and developer macOS machines.
These rules cover the failure classes that reproduce on exactly one of those three.

## When to use

- Adding or editing a script under `scripts/`, `.husky/`, or `.devcontainer/`.
- A script works locally but fails in GitHub Actions, or vice versa.
- Writing or reviewing a regex, in any language, that ships with a comment.
- Debugging `pathspec did not match`, `property 'Count' cannot be found`, `not a valid
application for this OS platform`, or a case-mismatch file-not-found.

## Rules

### Filename case sensitivity

- Linux (ext4/XFS), WSL, and Docker are case-sensitive; Windows NTFS and macOS APFS are not,
  so a wrong-case path passes locally and fails in CI (the `DxMessaging-banner.svg` versus
  `dxmessaging-banner.svg` failure in PR #144).
- Verify the canonical name with `git ls-files | grep -i <name>` or `ls -la` before hardcoding.
  Prefer deriving the path from a source of truth (`Get-ChildItem -Filter`, a config JSON)
  over a literal.
- Exercise path-sensitive scripts on Linux, WSL, or in Docker before merging.

### bash

- Under `set -e`, every command that can fail must be either intentionally fatal or explicitly
  handled. A comment saying "optional" without `|| true` is a contradiction that kills the run.
- Commands that fail in non-obvious ways: `grep` (exit 1 on no match), `diff` (exit 1 on
  difference), `git diff`, `rm` on a missing file (use `rm -f`), `cd` on a missing directory,
  `read` at EOF. Use `cmd || true`, `cmd || echo "..."`, or `if cmd; then ... fi`.
- Always quote variables that hold paths or patterns: `rm "$file"`, `cd "$dir"`,
  `git diff HEAD -- "$FILE_PATH"`. Unquoted expansion for intentional word splitting needs a
  `# shellcheck disable=SC2086` comment.
- `grep -q` already stops at the first match, so a preceding `head -N` is a pointless fork.
  Use `git ls-files "*.md" | grep -q .`, not `... | head -1 | grep -q .`.
- Run `shellcheck scripts/*.sh .husky/*` before merging.

### PowerShell

- The 0/1/many gotcha: PowerShell collapses pipeline output, so a function returning `@()`
  emits ZERO objects and a bare capture stores AutomationNull. Reading `.Count` or `.Length`
  then throws under `Set-StrictMode -Version` 2.0 and above (including `Latest`), and INDEXING
  throws at every StrictMode level, including Off. Always wrap the capture at the source:
  `$argv = @(Get-Args)`. Use `@(...)`, never `,(...)` (which yields Count 1) or a bare comma
  (a parse error). Two regression guards lock this: an end-to-end pwsh smoke test through the
  empty path and a static guard that flags a bare capture whose `.Count`/index is later read.
- Regex: use non-greedy `.*?` when the content may legitimately contain the excluded character.
  `'<!--[^>]*-->'` is broken for comments (`>` is legal inside them); use `'<!--.*?-->'`.
  `[^>]*` IS safe for well-formed XML tag attributes, where the closing `>` is always outside
  quoted values.
- Match complete structural units in XML/SVG replacements (`'<g id="x">.*?</g>'`, not
  `'...</text>'`), and make the replacement a valid standalone fragment. Consider `(?s)` for
  multi-line content.
- Do not put a literal example of the matched content in a comment above the pattern; a
  self-modifying script is the result. Describe it or use placeholder notation such as
  `{SEMVER}`.
- Here-strings: inside `@"..."@`, double quotes are NOT escaped; writing `""` emits two
  characters. `@'...'@` is fully literal with no expansion and no escaping.
- `[System.IO.File]::WriteAllText()` writes UTF-8 WITHOUT BOM by default. Do not "fix" it by
  passing `UTF8Encoding($true)`. `Set-Content -Encoding UTF8` on PS 5.1 does add a BOM, and
  `Out-File` on PS 5.1 writes UTF-16 LE.
- Say "runs before each commit is created" or "runs as a pre-commit hook". Never "runs on every
  commit", which is ambiguous.
- Scripts must stay valid under `Set-StrictMode -Version Latest`: initialize module-scope state
  before any helper reads it.

### Regex comments (any language)

- The comment describes what the pattern ACTUALLY matches, not what the author hoped.
- `\s` matches newlines. Use `[ \t]` when you only mean horizontal whitespace; a `\s*` suffix
  in a strip-and-replace silently joins adjacent lines. `[^\S\r\n]` is the alternative form.
- Document every flag: `g` requires plural phrasing ("all X"), `m` requires "start/end of any
  line", `s` requires "including newlines", `i` requires "case-insensitively", `u` requires
  noting `\p{}` support.
- Also document greedy versus lazy behavior, what `^`/`$`/`\b` anchor to given the flags, and
  known limitations. Never copy a comment from a similar pattern without re-verifying it.

### Windows-specific traps

- Windows `CreateProcess()` ignores shebangs: an `.exe` must be a real PE binary, a `.bat`/
  `.cmd` must be batch text. A shebang-bodied fake `Unity.exe` runs on Linux and fails on
  Windows with "not a valid application for this OS platform". Tests that need a fake Unity
  binary set `DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE=1` (preferred) or write a real `.cmd`
  companion, and must reference that variable or carry `// @allow-unity-native-probe`.
- `DXM_UNITY_FAKE_IMPORTS` alone does not force the missing-DLL branch: those names still
  resolve against the host through KnownDLLs and System32. Use
  `DXM_UNITY_FAKE_MISSING_IMPORTS` or synthetic unresolvable names.
- `path.relative(dir, file)` returns an ABSOLUTE path across Windows drives (repo on `D:`,
  `os.tmpdir()` on `C:`), so `rel.startsWith("..")` mislabels an outside path as inside. Use
  `isPathInsideDirectory` / `isPathOutsideDirectory` / `isOutsideRelative` from
  `scripts/lib/path-classifier.js`; if you truly cannot, pair with `path.isAbsolute(rel)`.
- GNU tar reads an archive operand containing an unqualified colon as a remote spec, so
  `tar -f C:\Temp\package.tgz` fails. Set the subprocess `cwd` to the archive directory and
  pass `./<basename>`; use `buildLocalTarArchiveSpec()` from `scripts/validate-npm-meta.js`
  and add `path.win32` coverage when touching it.

### Coverage

- All scripts, including `.ps1` files, are covered by the node `--test` suite (`npm test`),
  which spawns `pwsh` where needed.

## References

| Document                                                                                | Purpose                                                                                                                        |
| --------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------ |
| [cross-platform-compatibility.md](./references/cross-platform-compatibility.md)         | Case-sensitivity prevention, Windows PE-stub requirements, host DLL test seams, cross-drive path containment, and tar operands |
| [powershell-best-practices-part-1.md](./references/powershell-best-practices-part-1.md) | Regex character-class versus non-greedy pitfalls, structural completeness in XML replacements, and here-string quoting         |
| [powershell-best-practices-part-2.md](./references/powershell-best-practices-part-2.md) | Hook terminology, .NET encoding defaults, and the full StrictMode 0/1/many analysis with its enforcement guards                |
| [powershell-best-practices.md](./references/powershell-best-practices.md)               | The six-rule PowerShell summary and pointer into the case-sensitivity guidance                                                 |
| [regex-documentation-part-1.md](./references/regex-documentation-part-1.md)             | Wrong versus correct comments for each of the g, m, s, i, and u flags                                                          |
| [regex-documentation-part-2.md](./references/regex-documentation-part-2.md)             | Combined-flag comments, five comment anti-patterns, the verification checklist, and per-language notes                         |
| [regex-documentation.md](./references/regex-documentation.md)                           | The describe-actual-behavior principle, whitespace class comparison table, and flag reference                                  |
| [shell-best-practices-part-1.md](./references/shell-best-practices-part-1.md)           | Exit-code checking, variable quoting, ShellCheck usage, and the redundant head-before-grep-q pattern                           |
| [shell-best-practices.md](./references/shell-best-practices.md)                         | The set -e contract, error-handling patterns, commands that fail unexpectedly, and Linux case semantics                        |
