## Overview

Continuation extracted from `npm-package-configuration.md` to keep files within the repository line-budget policy.

## Solution

## Complete Example (This Project)

### package.json "files"

```json
{
  "files": [
    "Editor/**",
    "Runtime/**",
    "Samples~/**",
    "SourceGenerators/Directory.Build.props",
    "SourceGenerators/Directory.Build.props.meta",
    "SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators/*.cs",
    "SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators/*.cs.meta",
    "SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators/*.csproj",
    "SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators/*.csproj.meta",
    "SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.meta",
    "CHANGELOG.md",
    "CHANGELOG.md.meta",
    "LICENSE.md",
    "LICENSE.md.meta",
    "README.md",
    "README.md.meta",
    "Third Party Notices.md",
    "Third Party Notices.md.meta",
    "package.json.meta",
    "Editor.meta",
    "Runtime.meta",
    "SourceGenerators.meta"
  ]
}
```

### .npmignore

```text
# npm package exclusions for com.wallstop-studios.dxmessaging
#
# This file works with package.json "files" to control packaging.
# To verify: npm pack --dry-run

# Build artifacts
**/.vs/
**/bin/
**/obj/
**/*.pdb

# SourceGenerator test project
SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.Tests/
SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.Tests.meta

# Tests (defense-in-depth)
Tests/
Tests.meta

# Development files
scripts/
scripts.meta
.github/
.llm/
```

## Summary

| Mechanism    | Purpose    | Use For                                    |
| ------------ | ---------- | ------------------------------------------ |
| `"files"`    | Allowlist  | Directories/files TO include               |
| `.npmignore` | Exclusions | Subdirs, build artifacts, defense-in-depth |

### Key principles

1. Use "files" as a pure allowlist -- no negated patterns
1. Use specific patterns for complex structures (e.g., `SourceGenerators/Foo/*.cs`)
1. Use `.npmignore` for subdirectory exclusions and defense-in-depth
1. For Unity: include `.meta` files for included items, exclude for excluded items
1. Always verify with `npm pack --dry-run`

## See Also

- [npm documentation: files](https://docs.npmjs.com/cli/v10/configuring-npm/package-json#files)
- [npm documentation: .npmignore](https://docs.npmjs.com/cli/v10/using-npm/developers#keeping-files-out-of-your-package)

## Related Links

- [npm Package Configuration](./npm-package-configuration.md)
