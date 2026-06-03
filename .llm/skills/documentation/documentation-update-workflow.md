---
title: "Documentation Update Workflow"
id: "documentation-update-workflow"
category: "documentation"
version: "1.0.0"
created: "2026-01-22"
updated: "2026-01-22"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "docs/"
    - path: "README.md"
    - path: "CHANGELOG.md"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "documentation"
  - "workflow"
  - "checklist"
  - "maintenance"

complexity:
  level: "basic"
  reasoning: "Requires consistent process discipline"

impact:
  performance:
    rating: "none"
    details: "Documentation only"
  maintainability:
    rating: "high"
    details: "Clear workflow prevents documentation drift"
  testability:
    rating: "low"
    details: "Docs are not tested, but examples should be verified"

prerequisites:
  - "Understanding of the DxMessaging API surface"

dependencies:
  packages: []
  skills:
    - "documentation-updates"

applies_to:
  languages:
    - "Markdown"
    - "C#"
  frameworks:
    - "Unity"
    - ".NET"

aliases:
  - "Docs update checklist"
  - "Docs maintenance workflow"

related:
  - "documentation-updates"
  - "documentation-xml-docs"
  - "documentation-code-samples"
  - "changelog-management"
  - "mkdocs-navigation"

status: "stable"
---

# Documentation Update Workflow

> **One-line summary**: Use a consistent step-by-step workflow and checklist for every user-facing change.

## Overview

Documentation updates should happen in the same PR as the code change. This workflow ensures nothing is missed.

## Problem Statement

Without a workflow, documentation updates are inconsistent, and important references go stale.

## Solution

### Step-by-Step Process

1. **Identify scope**: What user-facing behavior changed?
1. **Find affected docs**: Search docs/ for mentions of the changed API/feature
1. **Update XML comments**: Modify or add comments on changed code
1. **Update code samples**: Verify all examples still work
1. **Add version notes**: Mark new/changed behavior with version
1. **Update CHANGELOG**: Add entry under appropriate section
1. **Cross-reference**: Ensure links and "See Also" sections are current
1. **Auto-fix Markdown structure**: Run `node scripts/fix-md029-md051.js <changed-docs.md ...>`
1. **Lint Markdown before commit**: Run `npx markdownlint-cli2 <changed-docs.md ...>`

### Example: Adding a New Emit Overload

```csharp
/// <summary>
/// Emits a message with a custom priority.
/// </summary>
/// <remarks>
/// <para><b>Added in v2.2.0.</b></para>
/// <para>Higher priority messages are delivered before lower priority ones.</para>
/// </remarks>
public void Emit<TMessage>(TMessage message, int priority)
    where TMessage : struct, IMessage
```

Documentation updates needed:

1. **docs/concepts/message-types.md** - Add section on prioritized emission
1. **docs/architecture/performance.md** - Note any performance implications
1. **docs/Quickreference/reference.md** - Add to API quick reference table
1. **CHANGELOG.md** - Add under "Added" section

### Documentation Checklist

- [ ] All new public APIs have XML documentation
- [ ] XML docs include `<summary>`, `<param>`, `<returns>` as appropriate
- [ ] Code samples compile and run correctly
- [ ] Version annotations added for new features
- [ ] CHANGELOG.md updated with user-facing changes
- [ ] Related docs/ articles updated
- [ ] README.md updated if feature is significant
- [ ] No TODOs or placeholders in documentation
- [ ] Links between related docs are bidirectional
- [ ] Examples use current API, not deprecated patterns
- [ ] Ordered lists use MD029 `one` style (`1.` prefixes)
- [ ] Internal fragment links resolve correctly (MD051)
- [ ] `node scripts/fix-md029-md051.js` run on changed docs
- [ ] `npx markdownlint-cli2` passes for changed docs

## Performance Notes

- **XML doc size**: Keep docs concise to avoid excessive metadata bloat
- **Build time**: Large docs do not significantly affect build time
- **Sample complexity**: Prefer minimal working examples over complex demos

## See Also

- [Documentation Updates](documentation-updates.md)
- [XML Documentation Standards](documentation-xml-docs.md)
- [Documentation Code Samples](documentation-code-samples.md)
- [MkDocs Navigation](mkdocs-navigation.md)
- [Changelog Management](changelog-management.md)

## References

- [Unity Best Practice Guides](https://docs.unity3d.com/Manual/best-practice-guides.html)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-22 | Initial version |
