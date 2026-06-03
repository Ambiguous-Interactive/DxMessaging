---
title: "Let Tools Resolve Modules"
id: "let-tools-resolve-modules"
category: "scripting"
version: "1.0.0"
created: "2026-05-18"
updated: "2026-05-18"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/run-managed-jest.js"
    - path: "scripts/__tests__/no-testrunner-injection-policy.test.js"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "cross-platform"
  - "tooling"
  - "module-resolution"
  - "windows"
  - "node"
  - "jest"

complexity:
  level: "basic"
  reasoning: "Simple meta-rule with a single decision point"

impact:
  performance:
    rating: "none"
    details: "Style and reliability rule; no runtime cost"
  maintainability:
    rating: "high"
    details: "Prevents an entire class of Windows-only and pnpm-only tool failures"
  testability:
    rating: "medium"
    details: "Heuristic is easy to grep for during review"

prerequisites:
  - "Understanding of Node module resolution"

dependencies:
  packages: []
  skills:
    - "cross-platform-compatibility"

applies_to:
  languages:
    - "JavaScript"
  frameworks:
    - "Jest"
    - "ESLint"
    - "Prettier"
    - "TypeScript"
  versions:
    node: ">=18.0"

aliases:
  - "Bare specifier rule"
  - "Do not pre-resolve"

related:
  - "jest-hook-robustness"
  - "cross-platform-compatibility"

status: "stable"
---

# Let Tools Resolve Modules

> **One-line summary**: When a tool resolves modules internally (jest-config
> `testRunner`, ESLint resolver, Prettier plugin loader, TypeScript `--lib`),
> pass the bare specifier; do NOT pre-resolve with `require.resolve` or
> `path.join(REPO_ROOT, 'node_modules', ...)` and pass an absolute path.

## Overview

Most JavaScript tools ship their own resolver tuned for their internal layout
assumptions. Pre-resolving from outside bypasses those rules and breaks
unpredictably across platforms.

## Solution

1. Default to passing the bare specifier (`jest-circus`, `eslint-config-foo`,
   `prettier-plugin-bar`).
1. Let the tool's resolver walk its own search paths.
1. Reserve absolute paths for the documented escape hatches where the caller
   explicitly opts in.

## Why

Tool-specific resolution edge cases are tool-private and version-private:

- Windows drive letters and backslash normalization.
- Symlinked `node_modules` (Yarn PnP, npm workspaces).
- pnpm's content-addressable store and virtual store shapes.
- npm workspace hoisting decisions per project layout.
- Jest's runner validation rejecting paths that fail its own `path.isAbsolute`
  plus normalization sequence.

Pre-resolving from outside a tool bypasses all of these. Windows is the most
common manifestation because path normalization differs the most there, but
pnpm-on-Linux trips the same class of failures.

## Canonical example: the jest-circus footgun

`scripts/run-managed-jest.js` used to inject
`--testRunner <abs-path-to-jest-circus>`. That absolute path failed
jest-config's runner validator on Windows even though the file existed at the
exact path supplied. See
[Jest Hook Robustness](./jest-hook-robustness.md) for the full failure story
and the two regression tests that pin the contract.

## Other tools where the pattern applies

- ESLint plugin and config paths: pass the package name; let ESLint resolve.
- Prettier `--plugin <pkg>`: pass the package name, not a built file path.
- TypeScript `--lib` and `--types`: pass library identifiers, not file paths.
- Dynamic `import()` of filesystem paths: use
  `pathToFileURL(absPath).href` rather than passing a raw Windows
  drive-letter path string to `import()`.

## Boundary: when is an absolute path OK?

When the caller explicitly opts in through a documented escape hatch. The
public `args` array forwarded to `run-managed-jest.js` is a supported escape
hatch: if a test author deliberately passes `--testRunner /abs/path/to/runner`
in argv, the wrapper forwards it unchanged. The rule applies to
INTERNAL injection by the wrapper, not to argv pass-through from a deliberate
caller.

## Detection heuristic

Any `scripts/*.js` line that computes a `--<flag>` value with `require.resolve`
or with `path.join(..., 'node_modules', ...)` and then passes the result to a
child tool is suspect. Default to the bare specifier. Reviewers should grep:

```bash
grep -rnE "(--[A-Za-z]+\s*[\"'].*node_modules)|(require\\.resolve\\([^)]*\\).*--)" scripts/
```

## See Also

- [Jest Hook Robustness](./jest-hook-robustness.md)
- [Cross-Platform Script Compatibility](./cross-platform-compatibility.md)

## Changelog

| Version | Date       | Changes                                                                |
| ------- | ---------- | ---------------------------------------------------------------------- |
| 1.0.0   | 2026-05-18 | Initial version generalizing the jest-circus testRunner injection bug. |
