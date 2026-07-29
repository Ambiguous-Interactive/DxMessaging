# Documentation Updates and Maintenance

> **One-line summary**: Keep documentation accurate and useful by updating it whenever user-facing behavior, APIs, or examples change.

## Overview

Documentation is part of the product. When code changes, documentation must change in the same PR. This skill defines when to update docs and where to look for impacted content.

## Problem Statement

Documentation rot happens when:

- New features ship without docs
- Bug fixes change behavior but docs describe the old behavior
- Code samples become stale and stop compiling
- Version-specific behavior is not clearly marked

Outdated documentation misleads users and increases support load.

## Solution

### Core Concept

**Document as you code.** Documentation updates are part of the feature or fix, not a follow-up task.

### When to Update Documentation

| Change Type                             | Documentation Required                                |
| --------------------------------------- | ----------------------------------------------------- |
| New public API                          | XML docs + docs/ article + README if significant      |
| Modified public API signature           | XML docs + all affected examples                      |
| Behavior change (even if API unchanged) | docs/ article + version annotation                    |
| Bug fix with observable behavior change | CHANGELOG + possibly docs/ if behavior was documented |
| New configuration option                | advanced/runtime-configuration.md + relevant guides   |
| Deprecation                             | XML docs with `[Obsolete]` + migration guide          |
| Performance improvement                 | architecture/performance.md + CHANGELOG               |

### Check These Files

1. **docs/** - user-facing guides and tutorials
1. **README.md** - quick start and feature list
1. **CHANGELOG.md** - version history and migration notes
1. **XML comments** - public APIs
1. **Code samples** - must compile and match current APIs

### What to Document

- **Public APIs** with XML docs and examples
- **Behavior changes** with version annotations
- **User-facing features** in docs/ guides
- **Breaking changes** with migration notes in CHANGELOG

## See Also

- [XML Documentation Standards](../../documentation-code-samples/references/documentation-xml-docs.md)
- [Code Sample Requirements](../../documentation-code-samples/references/documentation-code-samples.md)
- [Documentation Style Guide](./documentation-style-guide.md)
- [Documentation Update Workflow](./documentation-update-workflow.md)
- [MkDocs Navigation](../../markdown-authoring/references/mkdocs-navigation.md)
- [Changelog Management](../../changelog-management/references/changelog-management.md)

## References

- [Microsoft XML Documentation Comments](https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/xmldoc/)
- [Unity Best Practice Guides](https://docs.unity3d.com/Manual/best-practice-guides.html)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-22 | Initial version |
