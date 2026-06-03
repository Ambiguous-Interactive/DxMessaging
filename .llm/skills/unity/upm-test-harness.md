---
title: "UPM Test Harness"
id: "upm-test-harness"
category: "unity"
version: "1.0.0"
created: "2026-05-05"
updated: "2026-05-05"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/unity/run-ci-tests.ps1"
    - path: "scripts/unity/ensure-editor.ps1"
    - path: "scripts/unity/lib/asmdef-discovery.js"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "unity"
  - "upm"
  - "test-harness"
  - "manifest"
  - "testables"

complexity:
  level: "basic"
  reasoning: "Generated project; standard UPM testables semantics."

impact:
  performance:
    rating: "none"
    details: "Test infrastructure only"
  maintainability:
    rating: "high"
    details: "Generated manifest and exact Library cache keys keep CI reproducible without committing a Unity project"
  testability:
    rating: "high"
    details: "Without this harness, the package has no Unity surface to execute against"

prerequisites:
  - "Familiarity with Unity Package Manager (UPM)"
  - "Awareness of asmdef structure"

dependencies:
  packages: []
  skills:
    - "headless-test-runner"

applies_to:
  languages:
    - "JSON"
    - "C#"
  frameworks:
    - "Unity"
  versions:
    unity: ">=2021.3"

aliases:
  - "Unity test project"
  - "ephemeral Unity test project"
  - "Test harness"

related:
  - "headless-test-runner"
  - "unity-ci-matrix"
  - "unity-perf-test-isolation"

status: "stable"
---

<!-- trigger: unity, upm, manifest, testables, harness, test-project | UPM test harness for executing the package's Tests asmdefs | Core -->

# UPM Test Harness

> **One-line summary**: CI generates a thin Unity host project under `.artifacts/unity/projects/<version>-<mode>/`; its only job is to import the package via a local `file:` dependency and expose its `Tests/` asmdefs through the UPM `testables` field.

## When to Use

- Adding a new `.asmdef` under `Tests/` and verifying it shows up in Test Runner.
- Reproducing a CI failure that needs a working Unity project on disk.
- Changing the generated CI manifest in `scripts/unity/run-ci-tests.ps1`.
- Diagnosing "Test framework not found" or empty test-run failures.

## When NOT to Use

- Editing source files for the package itself. Those live at the repo root (`Runtime/`, `Editor/`, `Tests/`); this harness only references them.
- Adding regular `Assets/` content to the repo. The generated CI project may create temporary `Assets/Editor/` configuration code under `.artifacts/`, but no Unity project assets belong in the package root.

## Architecture

```text
repo-root/
+-- package.json                  # the DxMessaging UPM package manifest
+-- Runtime/                      # package source (asmdefs)
+-- Editor/                       # package source (asmdefs)
+-- Tests/
|   +-- Editor/                   # NUnit + UTF tests (asmdefs)
|   +-- Runtime/                  # PlayMode tests (asmdefs)
+-- .artifacts/unity/projects/    # generated thin hosts that import the package
    +-- Packages/
    |   +-- manifest.json         # "com.wallstop-studios.dxmessaging": "file:<repo-root>",
    |   |                         # plus "testables" exposing the package's Tests
    +-- ProjectSettings/
    |   +-- ProjectVersion.txt    # generated for the selected matrix version
    +-- Assets/Editor/            # generated CI configurator for standalone IL2CPP
    +-- Library/                  # gitignored, populated on first run
    +-- Temp/                     # gitignored
    +-- Logs/                     # gitignored
    +-- Builds/                   # gitignored, native standalone player output lands here
```

The shape is deliberate. UPM resolves the local `file:` dependency to the repo root, the package surfaces its asmdefs, and the `testables` array tells Unity Test Framework to scan that package's Tests assemblies. The checked-in repo remains a package, not a Unity project.

## Key Files

| File                                               | Role                                                                                |
| -------------------------------------------------- | ----------------------------------------------------------------------------------- |
| Generated `Packages/manifest.json`                 | Declares the package via local `file:` and lists it under `testables`.              |
| Generated `ProjectSettings/ProjectVersion.txt`     | Pins the selected matrix Editor version for that ephemeral project.                 |
| Generated `Assets/Editor/DxmCiTestConfigurator.cs` | Sets standalone tests to `StandaloneWindows64` IL2CPP before player test execution. |

The generated manifest shape:

```json
{
  "dependencies": {
    "com.unity.test-framework": "1.4.5",
    "com.unity.test-framework.performance": "3.4.2",
    "com.wallstop-studios.dxmessaging": "file:<repo-root>"
  },
  "scopedRegistries": [],
  "testables": ["com.wallstop-studios.dxmessaging"]
}
```

## What to Commit vs Gitignore

Committed source of truth:

- `scripts/unity/run-ci-tests.ps1`
- package source at `Runtime/`, `Editor/`, `Tests/`

Generated and gitignored:

- `.artifacts/unity/projects/**/Library/`
- `.artifacts/unity/projects/**/Temp/`
- `.artifacts/unity/projects/**/Logs/`
- `.artifacts/unity/projects/**/UserSettings/`
- `.artifacts/unity/cache/**`

CI caches the generated project's `Library/` with an exact key that includes OS, architecture, Unity version, mode, package/test inputs, and `scripts/unity/run-ci-tests.ps1`. Do not add broad restore keys.

## Adding a New Test Asmdef

The `testables` field exposes every asmdef under the package's `Tests/` directory automatically; no harness change is needed for typical additions:

1. Create the asmdef under `Tests/Editor/<NewSuite>/` (or `Tests/Runtime/<NewSuite>/`).
1. Set its `name` to a stable, descriptive identifier (e.g., `WallstopStudios.DxMessaging.Tests.Editor.NewSuite`).
1. Verify it shows up in the runner's discovery:

   ```bash
   node scripts/unity/lib/asmdef-discovery.js
   ```

1. Re-run the headless runner to confirm the new tests execute:

   ```bash
   bash scripts/unity/run-tests.sh --platform editmode
   ```

If the new asmdef is a perf or DI-integration suite, name it accordingly (`*Benchmarks*`, `*Allocations*`, `*Comparisons*`, `*VContainer*`, `*Zenject*`, `*Reflex*`). The classification regex in `scripts/unity/lib/asmdef-discovery.js` will excluded it from the default include list. See [unity-perf-test-isolation](./unity-perf-test-isolation.md).

## Adding a Test Dependency

When a new test suite needs an additional UPM package (a DI container, a third-party assertion library, etc.):

1. Add the generated dependency to `New-ManifestJson` in `scripts/unity/run-ci-tests.ps1`.
1. Re-run the CI runner in `-GenerateOnly` mode to inspect the generated manifest.
1. Re-run the Unity suite to confirm the new dependency loads cleanly.

Avoid adding heavyweight runtime dependencies unless the corresponding tests can opt in via the `--include-integrations` flag. The default suite stays lean for local runs and for the active Unity gate (direct Unity on self-hosted Windows).

## See Also

- [Headless Test Runner](./headless-test-runner.md)
- [Unity CI Matrix](./unity-ci-matrix.md)
- [Unity Perf Test Isolation](./unity-perf-test-isolation.md)

## References

- Unity Package Manager testables: https://docs.unity3d.com/Manual/cus-tests.html
- Unity Test Framework: https://docs.unity3d.com/Packages/com.unity.test-framework@1.4/manual/index.html
- Source: `scripts/unity/run-ci-tests.ps1`
