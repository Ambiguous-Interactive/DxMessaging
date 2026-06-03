---
title: "Cross-Toolchain Path Comparison"
id: "cross-toolchain-path-comparison"
category: "scripting"
version: "1.0.0"
created: "2026-06-03"
updated: "2026-06-03"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/__tests__/devcontainer-cache-contract.test.js"
    - path: "scripts/__tests__/cross-toolchain-path-comparison-policy.test.js"
    - path: "scripts/__tests__/cross-platform-preflight-coverage.test.js"
    - path: ".github/workflows/cross-platform-preflight.yml"
    - path: "scripts/lib/path-classifier.js"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "cross-platform"
  - "testing"
  - "windows"
  - "bash"
  - "paths"
  - "jest"
  - "ci-cd"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding that the same directory has different string spellings across Node and the various Windows bash flavors"

impact:
  performance:
    rating: "none"
    details: "Test-authoring pattern; no runtime performance impact"
  maintainability:
    rating: "high"
    details: "Eliminates a class of Windows-only test failures that pass on Linux/macOS and only surface at a contributor's pre-push"
  testability:
    rating: "high"
    details: "A static policy plus the cross-OS CI gate make the class self-preventing"

prerequisites:
  - "Familiarity with Jest equality matchers"
  - "Awareness that git invokes bash on Windows (Git-Bash / MSYS / Cygwin / WSL)"

dependencies:
  packages: []
  skills:
    - "cross-platform-compatibility"
    - "shell-best-practices"

applies_to:
  languages:
    - "JavaScript"
    - "Bash"
  frameworks:
    - "Jest"
    - "GitHub Actions"
  versions:
    node: ">=18.0"

aliases:
  - "Node path vs bash pwd"
  - "Path spelling drift"

related:
  - "cross-platform-compatibility"
  - "integrity-gate-robustness"
  - "shell-best-practices"

status: "stable"
---

# Cross-Toolchain Path Comparison

> **One-line summary**: Never assert that a path printed by a spawned shell is
> string-equal to a path produced by Node's `path` API -- the same directory has
> different spellings across toolchains on Windows. Route both sides through ONE
> toolchain instead.

## When to Use

- Writing or reviewing a Jest test that spawns `bash` / `sh` / `pwsh` /
  `powershell` and compares the path it prints against a Node-derived path.
- Diagnosing a test that is green on Linux/macOS and CI but red on a Windows
  contributor's machine -- often only caught at the pre-push hook.
- Adding any `.toBe` / `.toEqual` / `.toStrictEqual` assertion over a path value.

## When NOT to Use

- Pure substring/regex checks (`toContain`, `toMatch`) -- those do not exhibit
  whole-string drift.
- Comparisons where BOTH sides come from the same toolchain (two Node paths, or
  two shell `pwd` strings). Those are already convention-consistent.

## The Bug Class

A path string is only comparable to another path string when BOTH were spelled
by the SAME toolchain. The trap is resolving one directory two ways and
asserting equality:

```js
// BROKEN: cross-toolchain string equality.
// resolveWorkspaceRoot() spawns `bash` and returns its `pwd`.
expect(resolveWorkspaceRoot(undefined)).toBe(REPO_ROOT); // REPO_ROOT = path.resolve(...)
```

On Linux and macOS this is GREEN: Node's `path.resolve()` and the shell's `pwd`
print the same POSIX string byte-for-byte. On Windows it is RED for the SAME
directory, because the bash git invokes prints a POSIX mount form while Node
prints the native form:

| Toolchain                     | Spelling of the same directory                      |
| ----------------------------- | --------------------------------------------------- |
| Node `path.resolve()` (win32) | `D:\Code\com.wallstop-studios.dxmessaging`          |
| Git-Bash / MSYS `pwd`         | `/d/Code/com.wallstop-studios.dxmessaging`          |
| Cygwin `pwd`                  | `/cygdrive/d/Code/com.wallstop-studios.dxmessaging` |
| WSL `pwd`                     | `/mnt/d/Code/com.wallstop-studios.dxmessaging`      |

This is the same shape as the case-sensitivity and path-separator traps: the
divergence is invisible on the Linux/macOS dev box and CI lanes and surfaces
only on a Windows contributor's machine -- usually at the pre-push hook, the
last resort, rather than in an agentic or CI loop.

## The Fix: Route Both Sides Through One Toolchain

Resolve the Node-side reference through the SAME shell that produced the other
side, so the spelling cannot diverge:

```js
// Resolve a directory through the SAME bash that produced the other value.
// Pass the dir as an argv element (NOT interpolated) so backslashes and spaces
// survive intact. `cd` accepts the native form on every bash flavor; `pwd`
// normalizes to that flavor's mount spelling -- so both sides match.
function bashResolveDir(absoluteDir) {
  const result = childProcess.spawnSync("bash", ["-c", 'cd -- "$1" && pwd', "bash", absoluteDir], {
    encoding: "utf8"
  });
  expect(result.status).toBe(0);
  return result.stdout.trim();
}

// FIXED for the pwd-spelling axis: both sides are bash `pwd` strings.
expect(resolveWorkspaceRoot(undefined)).toBe(bashResolveDir(REPO_ROOT));
```

> **Scope caveat -- `bashResolveDir` only neutralizes the pwd-SPELLING axis.**
> It does a direct `cd -- "$1" && pwd`. If the value under test is itself
> derived from a Node-interpolated path that is then fed to `dirname`, a SECOND,
> independent axis appears: GNU `dirname` splits only on `/`, so a backslash
> path (`D:\repo\.devcontainer\cache-contract.sh`) returns `.` and resolves the
> WRONG directory relative to the shell CWD. Routing the Node reference through
> `pwd` does NOT fix that -- it is a separator bug, not a spelling bug. Close it
> at the SOURCE that runs `dirname`: normalize `\` to `/` first
> (`src="${src//\\//}"`), which is a no-op on Linux/macOS and correct on every
> Windows bash flavor. See `.devcontainer/cache-contract.sh`. Where a residual
> axis cannot be closed in-test, the `@cross-platform-regression` marker + the
> windows-latest gate (below) backstop it as an attributed CI failure rather
> than silent drift.

Three acceptable strategies, in preference order:

1. **Route the Node side through the shell** (`bashResolveDir`) when the test
   genuinely needs a shell-produced value. Reference implementation:
   `scripts/__tests__/devcontainer-cache-contract.test.js`.
1. **Normalize both sides** with `toPosixPath` / `toRepoPosixRelative` from
   `scripts/lib/path-classifier.js` when neither side must remain native.
1. **Assert directory IDENTITY** instead of string equality (e.g. write a
   sentinel file via one toolchain and read it via the other) when even the
   normalized spelling is awkward.

Do NOT "fix" it by string-munging the Windows spelling (stripping `/cygdrive`,
swapping separators) -- the flavor set is open (Git-Bash, MSYS, Cygwin, WSL) and
a hand-rolled converter rots. Let one toolchain own the spelling.

## Automated Enforcement

Two guards make this class self-preventing -- the first so an agent catches it
on the Linux dev box BEFORE the hook, the second so CI catches any escape FAST:

1. `scripts/__tests__/cross-toolchain-path-comparison-policy.test.js` -- a fast,
   pure-static scan (no shell spawn). It flags any test that BOTH spawns a
   Windows-divergent shell AND asserts a bare Node-path token (`REPO_ROOT` /
   `path.resolve` / `path.join` / `__dirname` / `process.cwd()`) with an
   equality matcher. The remedy is cheap: normalize the compare (the flag then
   clears) OR add the `@cross-platform-regression` marker.
1. The `@cross-platform-regression` marker promotes a platform-divergent test
   onto the cross-OS targeted gate in
   `.github/workflows/cross-platform-preflight.yml`, so it runs on
   windows-latest and the drift fails fast with clear attribution. The
   marker-to-gate sync is kept honest by
   `scripts/__tests__/cross-platform-preflight-coverage.test.js`.

### Choosing a remedy (the two are NOT equally cheap)

- **Normalize (preferred, truly zero-cost):** route both sides through one
  toolchain (`bashResolveDir`) or normalize with `toPosixPath` /
  `toRepoPosixRelative`. The bare Node-path token disappears, the static guard
  clears, and nothing else changes. This is the only remedy that is harmless in
  isolation, so it is the right answer for a false positive.
- **Mark (a COORDINATED two-file edit, NOT a free promotion):** adding only the
  `@cross-platform-regression` marker clears the static guard but turns
  `cross-platform-preflight-coverage.test.js` RED in the SAME run -- it asserts
  every marked file is also present in the `--runTestsByPath` list of the
  targeted step. To use the marker remedy you MUST do both edits together: add
  the marker comment AND wire the file into that step in
  `.github/workflows/cross-platform-preflight.yml`. Both guards recognize the
  marker ONLY inside a real comment span (they share the `extractCommentsOnly`
  projection), so a marker buried in a string literal neither silences the
  static guard nor satisfies the coverage guard -- there is no free silencing.

The static guard never produces an incorrect rewrite. The worst case for a
false positive is "this test also gains win+mac CI execution" -- and if you take
the marker route you must also wire it into the targeted step, or the coverage
guard fails fast in the same run.

## See Also

- [Cross-Platform Script Compatibility](./cross-platform-compatibility.md)
- [Integrity Gate Robustness](./integrity-gate-robustness.md)
- [Shell Best Practices](./shell-best-practices.md)

## Changelog

| Version | Date       | Changes                                                  |
| ------- | ---------- | -------------------------------------------------------- |
| 1.0.0   | 2026-06-03 | Initial version from the devcontainer-cache-contract fix |
