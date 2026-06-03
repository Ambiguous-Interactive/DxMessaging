# Windows partial node_modules extract

This runbook covers the Windows-specific failure mode where `npm install`
extracts only some of the files in a package, leaving the package directory
present but unusable. The pre-push hooks surface this as opaque "module not
found" or "testRunner option was not found" errors. The integrity gate in
`scripts/lib/node-modules-integrity.js` auto-repairs the most common case,
but operator action is required when auto-repair is refused or fails.

## Symptoms

The canonical symptom is a pre-push log that looks like (verbatim from
`pre-push.txt`):

```text
Validation Error:

  testRunner option was not found.
  Make sure jest-circus is installed: https://www.npmjs.com/package/jest-circus
```

The error appears even though `node_modules/jest-circus` is present on
disk. Other symptoms include:

- `cspell` failing with `Cannot find module 'cspell/dist/...'` when
  `node_modules/cspell/bin.mjs` is present but other internal files are
  missing.
- `prettier` failing with `Cannot find package` for an internal dependency
  even though `node_modules/prettier/index.cjs` and
  `node_modules/prettier/bin/prettier.cjs` are present.
- `npm install` reporting `up to date` repeatedly while the broken file
  remains broken.

## Root cause

`npm` writes packages by extracting their tarballs into `node_modules`
incrementally. If the install is interrupted - antivirus quarantine, sleep
transition, network blip, long-path limit (260 chars on default Windows
NTFS), or the user closing the shell - some files are written and the rest
are not. The lockfile hash, however, is computed from the manifest, not
the extracted contents, so a subsequent `npm install` sees a matching
lockfile entry and skips re-extraction (the "up to date" message).

The repository's pre-push hooks invoke tools that need files deeper inside
the package than `npm install` looks at. The integrity gate enumerates the
load-bearing critical files (see `INTEGRITY_TARGETS` in
`scripts/lib/node-modules-integrity.js`) and probes each one for both
presence and non-zero size before any tool is invoked.

## Auto-repair behavior

When `scripts/run-managed-jest.js`, `scripts/run-managed-prettier.js`, or
`scripts/run-managed-cspell.js` is invoked:

1. The integrity gate runs first. It probes every file in
   `INTEGRITY_TARGETS` for existence and non-zero size.
1. If the probe is OK, the wrapper continues to its normal tier dispatch
   (local devDependency, isolated managed cache, npm exec, npx).
1. If the probe fails, the gate consults `isAutoRepairAllowed`. Refusal
   cases are listed below.
1. If auto-repair is allowed, the gate runs `npm ci --no-audit --no-fund`
   against the repo root.
1. After `npm ci` succeeds, the gate spawns a fresh `node -e` subprocess
   that re-runs `probeIntegrity`. The subprocess is needed because the
   parent process still has the previous (broken) module + stat cache
   entries; a same-process re-probe would falsely report failure.
1. If the subprocess probe is OK, the wrapper proceeds. If it still
   fails, the wrapper prints the actionable repair banner and exits with
   status 1.

Separately, the isolated managed-Jest fallback cache under the OS temp
dir is auto-healed: `node scripts/repair-node-tooling.js` (the first
native pre-push step) and `scripts/preflight.js` both call
`healRegenerableCaches`, which purges any corrupt/partial
`<tmpdir>/dxmessaging-managed-jest/jest_<version>` install (or a stray file
at the cache root) before the read-only doctor inspects it. See
[Isolated managed-Jest cache (regenerable; auto-healed)](#isolated-managed-jest-cache-regenerable-auto-healed).

The gate emits no `--testRunner` injection at any point; the contract
documented in
`.llm/skills/scripting/jest-hook-robustness.md` remains load-bearing.

## When auto-repair is refused

The gate refuses to run `npm ci` in any of these states:

| Refusal reason                           | How to fix                                                             |
| ---------------------------------------- | ---------------------------------------------------------------------- |
| `npm` is not on PATH                     | Open a shell with Node + npm initialized; re-run the hook.             |
| `package-lock.json` has unstaged changes | Stage or stash the lockfile edits, then re-run.                        |
| Mid-rebase (`.git/rebase-merge` exists)  | Finish the rebase (`git rebase --continue` or `--abort`), then re-run. |
| Mid-rebase (`.git/rebase-apply` exists)  | Finish the rebase, then re-run.                                        |
| `DXMSG_HOOK_NO_AUTOREPAIR=1` is set      | Either unset the variable or run `npm ci` manually.                    |

## Isolated managed-Jest cache (regenerable; auto-healed)

The isolated managed-Jest fallback cache under the OS temp dir
(`<tmpdir>/dxmessaging-managed-jest`) is a REGENERABLE artifact: the tooling
rebuilds it on demand. When it is corrupt or partially installed (a Windows
`%TEMP%` cleaner, antivirus mid-write, Disk Cleanup, or a reboot left a
half-written `jest_<version>` dir, or a stray file sits where the cache dir
belongs), the correct response is to PURGE it and let the next managed-Jest run
rebuild it -- never a manual `rm` gate.

This is fully automated and needs ZERO manual touch. The native pre-push hook
runs `node scripts/repair-node-tooling.js` first; that calls
`healRegenerableCaches` (in `scripts/lib/regenerable-cache-registry.js`), which
auto-clears the corrupt cache BEFORE `npm run doctor` ever inspects it. The
agentic preflight (`scripts/preflight.js`) fires the same heal. So a corrupt
isolated cache is purged automatically on the next push or preflight, and the
read-only doctor reports it as a non-blocking WARN (never a hard FAIL).

To trigger the automated heal explicitly (e.g. before a push):

```bash
npm run repair:node-tooling
```

## Manual node_modules recovery (cross-platform)

If the `node_modules` integrity auto-repair is refused or fails, run these
commands in order. They work on Linux, macOS, Windows CMD, and Windows
PowerShell:

```bash
npm ci
node scripts/validate-node-tooling.js
npm run preflight:pre-push
```

The first reinstalls the repo's `node_modules` from the lockfile. The second
verifies the install is complete. The third runs the full pre-push gauntlet.

If, after the above, a stale isolated managed-Jest cache somehow persists
(it should already be auto-healed by `npm run repair:node-tooling` above), you
may clear it directly as a last resort. They work on Linux, macOS, Windows CMD,
and Windows PowerShell:

```bash
# last resort only -- npm run repair:node-tooling auto-clears this for you
node -e "require('fs').rmSync(require('path').join(require('os').tmpdir(), 'dxmessaging-managed-jest'), { recursive: true, force: true })"
```

## Aggressive recovery

When the partial extract has persisted across multiple `npm ci`
invocations (rare, usually antivirus-driven), use the aggressive flag:

```bash
DXMSG_HOOK_AGGRESSIVE_RECOVERY=1 node scripts/run-managed-jest.js --version
```

This deletes `node_modules` outright before invoking `npm ci`. Use it
only when the normal path has failed; the rm-rf step adds 30-60s to the
hook on a healthy install.

## Long-path mitigation (Windows)

The default Windows NTFS path limit is 260 characters. Nested
`node_modules` trees can blow past this. Two mitigations:

1. Enable long-path support globally:

   ```powershell
   New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem" -Name "LongPathsEnabled" -Value 1 -PropertyType DWORD -Force
   ```

   Reboot required.

1. Move the repo closer to the drive root. A repo at
   `D:\Code\dxmessaging` rarely hits the limit; one at
   `C:\Users\<name>\Documents\GitHub\organization\sub\dxmessaging` may.

## Missing or broken `unrs-resolver` native binding

Newer versions of the dev-tool resolver chain (`unrs-resolver`) ship a
platform-specific native binding -- on Windows it is
`@unrs/resolver-binding-win32-x64-msvc`. When the binding is missing or
truncated (commonly during a partial install or after an AV scan), the
JS file `node_modules/jest-circus/build/runner.js` is present on disk
but `require.resolve('jest-circus/runner')` throws at runtime with
`Failed to load native binding`. The file-based integrity probe is
blind to this; the resolver probe added in
`scripts/lib/node-modules-integrity.js` catches it.

Symptoms:

- `Failed to load native binding: @unrs/resolver-binding-win32-x64-msvc`
  in the pre-push log even though `node_modules/jest-circus/build/runner.js`
  exists and is non-zero.
- `Cannot find module 'unrs-resolver/resolver.win32-x64-msvc.node'`.

Manual recovery:

```powershell
# PowerShell
npm rebuild @unrs/resolver-binding-win32-x64-msvc
# If rebuild doesn't fix it, blow away node_modules entirely
Remove-Item -Recurse -Force node_modules
npm install
```

```bash
# Git Bash / WSL
npm rebuild @unrs/resolver-binding-win32-x64-msvc
# Or, aggressive:
rm -rf node_modules && npm install
```

## `DXMSG_HOOK_NO_AUTOREPAIR=1` shell carry-over

If you set `DXMSG_HOOK_NO_AUTOREPAIR=1` in a previous session and forgot
to unset it, the integrity gate will detect the partial extract but
SKIP `npm ci` even when the repair is safe. The gate banner now
explicitly tells you when this is the cause; to re-enable auto-repair:

```bash
# POSIX (bash, zsh)
unset DXMSG_HOOK_NO_AUTOREPAIR
```

```powershell
# PowerShell
Remove-Item Env:\DXMSG_HOOK_NO_AUTOREPAIR
```

To verify it is unset:

```bash
echo "${DXMSG_HOOK_NO_AUTOREPAIR:-<unset>}"   # POSIX
```

```powershell
"$env:DXMSG_HOOK_NO_AUTOREPAIR"               # PowerShell
```

If the variable is set globally (Windows -> Environment Variables panel,
or `~/.bashrc`, `~/.zshrc`, PowerShell `$PROFILE`), edit the source and
re-open your shell.

## Doubled paths and junctions on Windows

Windows junctions or accidental path doubling (for example
`C:\foo\Packages\Packages\com.wallstop-studios.dxmessaging`) can confuse
Jest's resolver and the `unrs-resolver` cache. If the integrity probe
keeps reporting the same file as missing after a successful `npm ci`,
re-clone into a flat path:

```powershell
git clone https://github.com/Ambiguous-Interactive/DxMessaging C:\dev\dxmessaging
cd C:\dev\dxmessaging
npm ci
```

## Antivirus exclusions

Windows Defender, Symantec, and Sophos have all been observed
quarantining `*.node` native binaries during package install. Symptoms:

- `findZeroByteNativeBinaries` (in
  `scripts/lib/node-modules-integrity.js`) reports a non-empty list.
- `npm ci` completes but the next probe still fails.

Mitigation: add an exclusion for the repo's `node_modules` directory in
the AV control panel. The repo path is local development only; an
exclusion does not weaken production security.

## See also

- `.llm/skills/scripting/jest-hook-robustness.md` for the hook-side
  contract and the `--testRunner` injection ban.
- `scripts/lib/node-modules-integrity.js` for the canonical
  `INTEGRITY_TARGETS` list.
- `scripts/lib/integrity-gate-with-recovery.js` for the gate's
  probe-repair-reprobe flow.
- `scripts/doctor.js` for the read-only diagnostic that surfaces the
  same probe results without attempting any repair.
