---
title: "Documentation Code Samples"
id: "documentation-code-samples"
category: "documentation"
version: "1.1.0"
created: "2026-01-22"
updated: "2026-01-22"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "docs/"
    - path: "README.md"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "documentation"
  - "code-samples"
  - "examples"
  - "testing"
  - "accuracy"
  - "linting"
  - "anti-patterns"

complexity:
  level: "basic"
  reasoning: "Requires maintaining runnable examples that match current APIs"

impact:
  performance:
    rating: "none"
    details: "Documentation only"
  maintainability:
    rating: "high"
    details: "Accurate samples reduce user confusion and support load"
  testability:
    rating: "medium"
    details: "Samples should be verified with tests or compilation checks"

prerequisites:
  - "Understanding of the DxMessaging API surface"

dependencies:
  packages: []
  skills:
    - "documentation-updates"

applies_to:
  languages:
    - "C#"
    - "Markdown"
  frameworks:
    - "Unity"
    - ".NET"

aliases:
  - "Doc examples"
  - "Sample code standards"

related:
  - "documentation-updates"
  - "documentation-style-guide"
  - "documentation-update-workflow"
  - "link-quality-guidelines"

status: "stable"
---

# Documentation Code Samples

> **One-line summary**: Code samples must compile, include context, and use the latest APIs.

## Overview

Docs are only as good as their samples. Every snippet should be copy-paste ready and reflect the current API surface.

## Problem Statement

Broken or incomplete samples cause:

- User frustration and support tickets
- Incorrect usage patterns spreading through copy/paste
- Reduced trust in the documentation

## Solution

### Code Sample Requirements

All code samples must be:

1. **Correct** - Compiles without errors
1. **Complete** - Includes required `using` statements and context
1. **Current** - Uses the latest API, not deprecated patterns
1. **Tested** - Verified to actually work

### Good Code Sample

```csharp
using DxMessaging.Core;
using DxMessaging.Core.Attributes;
using DxMessaging.Unity;
using UnityEngine;

[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct DamageMessage
{
    public readonly int amount;
    public readonly InstanceId source;
}

public sealed class HealthComponent : MessageAwareComponent
{
    [SerializeField]
    private int currentHealth = 100;

    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = _messageRegistrationToken.RegisterGameObjectTargeted<DamageMessage>(
            gameObject,
            HandleDamage
        );
    }

    private void HandleDamage(ref DamageMessage message)
    {
        currentHealth -= message.amount;
        Debug.Log($"Took {message.amount} damage from {message.source}. Health: {currentHealth}");
    }
}
```

### Bad Code Sample (Anti-Pattern)

```csharp
// Missing using statements
// Missing class context
// Uses outdated API

public void HandleDamage(DamageMessage msg)  // Wrong signature! Should be (ref DamageMessage message)
{
    health -= msg.damage;  // Field doesn't exist in example - should be msg.amount
}
```

### Code Fence Language for Anti-Pattern Examples

When showing examples of **bad patterns** (anti-patterns) in documentation, especially for Markdown syntax, use `text` or `none` as the code fence language instead of `markdown`:

````markdown
<!-- GOOD: Use 'text' for anti-pattern examples that might trigger linters -->

```text
<!-- BAD: Raw file names as link text -->
See [README.md](../README.md) for installation.
```
````

#### Why this matters

- Documentation linters may scan for patterns like raw file names in link text
- Using `text` or `none` signals that the content is illustrative, not actual documentation
- Some linting tools only check content in `markdown` code blocks
- Prevents false positives in CI/CD pipelines

For non-Markdown anti-patterns (like C# code), using the actual language (`csharp`) is still appropriate since code linters typically don't run on documentation files.

### Anti-Patterns to Avoid

#### Bad: Outdated Examples

````markdown
## Emitting Messages

```csharp
// Old API - no longer works!
MessageBus.Instance.Send(new DamageEvent());
```
````

#### Bad: Missing Version Context

```markdown
## New Feature

Use the new interceptor API to preprocess messages...
```

Fix with a version annotation:

```markdown
## Message Interceptors

> **Added in v2.1.0**

Use interceptors to preprocess messages...
```

#### Bad: Incomplete Code Samples

```csharp
// Register the handler
_messageRegistrationToken.RegisterGameObjectTargeted<DamageMessage>(gameObject, OnDamage);

// Handle the message
private void OnDamage(DamageMessage msg) { ... }  // Wrong signature and missing context
```

## See Also

- [documentation code samples part 1](./documentation-code-samples-part-1.md)
