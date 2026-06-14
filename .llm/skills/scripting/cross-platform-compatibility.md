---
title: "Cross-Platform Script Compatibility"
id: "cross-platform-compatibility"
category: "scripting"
version: "1.2.0"
created: "2026-01-28"
updated: "2026-06-11"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/"
    - path: ".github/workflows/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "cross-platform"
  - "case-sensitivity"
  - "testing"
  - "powershell"
  - "javascript"
  - "ci-cd"
  - "linux"
  - "windows"
  - "macos"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of filesystem differences across platforms"

impact:
  performance:
    rating: "none"
    details: "Script patterns only; no runtime performance impact"
  maintainability:
    rating: "critical"
    details: "Cross-platform issues cause CI failures that work locally"
  testability:
    rating: "high"
    details: "Proper testing catches platform-specific issues before CI"

prerequisites:
  - "Understanding of filesystem case sensitivity"
  - "Familiarity with the Node.js built-in node:test runner"
  - "Knowledge of CI/CD pipeline concepts"

dependencies:
  packages: []
  skills:
    - "shell-best-practices"
    - "powershell-best-practices"

applies_to:
  languages:
    - "PowerShell"
    - "JavaScript"
    - "Bash"
  frameworks:
    - "node:test"
    - "GitHub Actions"
  versions:
    node: ">=18.0"
    powershell: ">=5.1"

aliases:
  - "Case sensitivity"
  - "Platform compatibility"
  - "Script testing"

related:
  - "shell-best-practices"
  - "powershell-best-practices"
  - "comprehensive-test-coverage"

status: "stable"
---

# Cross-Platform Script Compatibility

> **One-line summary**: Ensure scripts work correctly across Windows, macOS, and Linux by handling
> case-sensitive paths and covering each script with platform-specific tests.

## Overview

Scripts that work locally on Windows or macOS often fail in CI/CD environments running Linux.
The most common cause is **filename case sensitivity** -- Linux filesystems are case-sensitive while
Windows and macOS are case-insensitive by default. This skill documents patterns to prevent these
issues and ensure all scripts have proper test coverage.

## Solution

1. **Verify exact file path case** before committing using `git ls-files` or `ls -la`
1. **Test in case-sensitive environments** (Docker, WSL, or Linux) before pushing
1. **Derive paths from source of truth** instead of hardcoding filenames
1. **Maintain test coverage** for all scripts (via `npm test`, the node --test suite)
1. **Add case validation in CI** to catch issues automatically

## Filename Case Sensitivity

### The Problem

Linux filesystems (ext4, XFS, etc.) distinguish between `File.txt` and `file.txt` as completely
different files. Windows (NTFS) and macOS (APFS) treat them as the same file.

#### Real-World Example from PR #144

```powershell
# BROKEN: Wrong case for the actual file
$bannerPath = Join-Path $repoRoot "docs/images/dxmessaging-banner.svg"
# Actual filename: DxMessaging-banner.svg

# This works on Windows/macOS but fails on Linux CI!
```

The script used `dxmessaging-banner.svg` but the actual file was `DxMessaging-banner.svg`.
This passed all local tests but failed in the GitHub Actions CI environment.

### Prevention Strategies

#### 1. Verify Exact Case Before Committing

Always verify the exact case of files referenced in scripts:

```bash
# Use git to find the canonical name
git ls-files | grep -i banner

# Use ls to verify exact case
ls -la docs/images/

# Use tab-completion to get exact case
```

#### 2. Use Variables from Source of Truth

Instead of hardcoding filenames, derive them from existing known-good sources:

```powershell
# BETTER: Derive from a source of truth
$files = Get-ChildItem -Path $imagesDir -Filter "*banner*"
if ($files.Count -eq 1) {
    $bannerPath = $files[0].FullName
}

# OR: Store the canonical name in configuration
$config = Get-Content "scripts/config.json" | ConvertFrom-Json
$bannerPath = Join-Path $repoRoot $config.bannerPath
```

#### 3. Add Case Validation in CI

Create a CI check that validates case consistency:

```bash
#!/bin/bash
# Validate that all script file references match actual case

# Find all file path strings in PowerShell scripts
grep -roh '"[^"]*\.[a-z]*"' scripts/*.ps1 | sort -u | while read -r path; do
    # Strip quotes and check if file exists with exact case
    clean_path="${path//\"/}"
    if [[ -e "$clean_path" ]]; then
        actual=$(ls -d "$clean_path" 2>/dev/null)
        if [[ "$clean_path" != "$actual" ]]; then
            echo "Case mismatch: script uses '$clean_path' but actual is '$actual'"
            exit 1
        fi
    fi
done
```

### Cross-Platform Path Comparison Table

| Platform       | Filesystem | Case-Sensitive | CI Environment |
| -------------- | ---------- | -------------- | -------------- |
| Linux          | ext4, XFS  | Yes            | GitHub Actions |
| macOS          | APFS       | (default)      | Local dev      |
| Windows        | NTFS       | No             | Local dev      |
| WSL            | ext4       | Yes            | Local dev      |
| Docker (Linux) | ext4       | Yes            | Local CI       |

### Testing for Case Sensitivity Issues

Run your scripts in a case-sensitive environment before committing:

```bash
# Option 1: Use Docker with Linux
docker run --rm -v "$PWD:/workspace" -w /workspace mcr.microsoft.com/powershell:latest \
    pwsh -File scripts/unity/export-unitypackage.ps1

# Option 2: Use WSL on Windows
wsl pwsh -File scripts/unity/export-unitypackage.ps1

# Option 3: Create a case-sensitive volume on macOS
# (requires disk utility to create APFS case-sensitive volume)
```

## Stub executables on Windows: PE-binary requirement

Linux and macOS execute scripts with a shebang via kernel `execve` dispatch
regardless of file extension: a file named `Unity.exe` whose contents are
`#!/usr/bin/env sh\n...` runs as a shell script when chmod-executable. Windows
`CreateProcess()` does NOT honor shebangs and instead requires:

- `.exe` / `.com` files to be valid PE binaries
- `.bat` / `.cmd` files to be batch text
- Anything else to be routed through an interpreter explicitly

Spawning a shebang-bodied `Unity.exe` on Windows fails with
_"The specified executable is not a valid application for this OS platform"_,
and the surrounding script aborts. The class of failure is invisible on
Linux/macOS CI and only surfaces on Windows runners.

Two acceptable patterns in tests that need a fake Unity binary:

1. **Bypass the native startup probe** by setting
   `DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE=1` in the spawn env. This is the
   preferred path when only the surrounding `ensure-editor.ps1` logic is under
   test (module install/repair/quarantine/host-env hermeticity). The early-
   return gate lives in `Ensure-UnityNativeStartupHealthy` in
   `scripts/unity/ensure-editor.ps1`.
1. **Use a real PE-shaped stub**: on Windows write a `.cmd` companion file and
   reroute through it instead of writing a shebang `.exe`.

Any test that drives `ensure-editor.ps1` with a fake `Unity.exe` must reference
`DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE` or carry the comment escape hatch
`// @allow-unity-native-probe`.

Production CI never sets `DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE`; the probe and
auto-repair behavior in `Ensure-UnityNativeStartupHealthy` is unchanged for real
Unity editors.

## Host DLL state in PowerShell tests

Tests for Windows loader diagnostics must not infer missing DLLs from
`DXM_UNITY_FAKE_IMPORTS` alone. That override supplies a fake Unity import list,
but `Test-UnityImportResolution` still resolves those names against the native
host: on Windows, `KERNEL32.dll` can resolve through KnownDLLs and
`VCRUNTIME140.dll` / `MSVCP140.dll` can resolve from System32 when the Visual C++
Redistributable is installed.

When a test needs the `MISSING DLL(s):` annotation branch, force the missing
bucket with `DXM_UNITY_FAKE_MISSING_IMPORTS` or use synthetic DLL names that
cannot resolve on any supported host. Reserve `DXM_UNITY_FAKE_IMPORTS` alone for
tests that are explicitly about the import-list or all-imports-resolve branches.

## Cross-drive path containment (Windows)

`path.relative(dir, file)` does NOT always return a `..`-prefixed path. On
DIFFERENT Windows drives -- the common hook case (repo on `D:\`, `os.tmpdir()` on
`C:\`) -- it returns the ABSOLUTE target (`C:\Users\...`), so `rel.startsWith("..")`
is `false` for a genuinely-outside path (mislabeled INSIDE), and its negation is
wrong for "inside" too (UNC and 8.3-short-name paths also break a raw compare).
Never hand-roll it -- use `isPathInsideDirectory` / `isPathOutsideDirectory` /
`isOutsideRelative` from `scripts/lib/path-classifier.js`, which add the
`path.isAbsolute` branch plus symlink-resolve + case-fold on Windows
(`isOutsideRelative(rel)` is `""`->false, else `..` / `..`+sep / absolute). If you
truly cannot use the helper, pair with `path.isAbsolute(rel)` (idiom
`!rel.startsWith("..") && !path.isAbsolute(rel)`).

## Local tar archive operands

GNU tar treats an archive operand containing an unqualified colon as a remote
archive spec. On Windows this makes `tar -f C:\Temp\package.tgz` fail before it
opens the local file. Do not pass raw absolute paths to `tar -f`.

For local archive paths, resolve the file once, set the subprocess `cwd` to the
archive directory, and pass `./<basename>` as the archive operand. In this repo,
use `buildLocalTarArchiveSpec()` from `scripts/validate-npm-meta.js` for npm
tarball validation, and add `path.win32` coverage when touching this behavior.

## Validation Checklist

Before merging scripts:

### Case Sensitivity

- [ ] All file path references verified against actual filesystem
- [ ] Tested in case-sensitive environment (Linux, Docker, or WSL)
- [ ] File extensions use correct case (`.svg` not `.SVG`)
- [ ] Directory names use correct case (`docs/images/` not `Docs/Images/`)

### Test Coverage

- [ ] Test coverage exists for all scripts (`npm test`)

## See Also

- [Shell Best Practices](./shell-best-practices.md) - Shell-specific case sensitivity patterns
- [PowerShell Best Practices](./powershell-best-practices.md) - PowerShell scripting patterns
- [Test Coverage Requirements](../testing/comprehensive-test-coverage.md) - General test coverage requirements

## Changelog

| Version | Date       | Changes                                                          |
| ------- | ---------- | ---------------------------------------------------------------- |
| 1.2.0   | 2026-06-11 | Add local tar archive operand guidance for Windows drive paths   |
| 1.1.0   | 2026-05-30 | Add cross-drive path containment section (Windows `os.tmpdir()`) |
| 1.0.0   | 2026-01-28 | Initial version from PR #144 feedback                            |
