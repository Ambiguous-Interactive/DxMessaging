---
title: "XML Documentation Standards"
id: "documentation-xml-docs"
category: "documentation"
version: "1.0.0"
created: "2026-01-22"
updated: "2026-01-22"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Runtime/"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "documentation"
  - "xml-docs"
  - "api-reference"
  - "versioning"
  - "code-comments"

complexity:
  level: "basic"
  reasoning: "Applies consistent XML documentation structure across public APIs"

impact:
  performance:
    rating: "none"
    details: "Documentation only"
  maintainability:
    rating: "high"
    details: "Clear XML docs reduce API misuse and support burden"
  testability:
    rating: "none"
    details: "Documentation does not affect testability"

prerequisites:
  - "Familiarity with C# XML doc comments"

dependencies:
  packages: []
  skills:
    - "documentation-updates"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
    - ".NET"
  versions:
    unity: ">=2021.3"
    dotnet: ">=netstandard2.0"

aliases:
  - "API XML docs"
  - "C# XML comments"

related:
  - "documentation-updates"
  - "documentation-style-guide"
  - "documentation-update-workflow"

status: "stable"
---

# XML Documentation Standards

> **One-line summary**: Public APIs require complete XML docs with summaries, parameters, returns, and version annotations when behavior changes.

## Overview

XML documentation is the primary API reference in IntelliSense and generated docs. Every public member needs clear XML docs that explain intent, constraints, and version history.

## Problem Statement

Missing or vague XML docs lead to:

- Users misusing APIs
- Confusion about expected behavior
- Unclear upgrade impact when behavior changes

## Solution

### Core Concept

Document every public API with:

- **summary**: What the API does
- **typeparam/param**: What inputs mean
- **returns**: What the result indicates
- **remarks**: Version notes, behavioral nuances, and gotchas
- **example**: Minimal working usage

### Implementation

```csharp
/// <summary>
/// Emits a targeted message to a specific recipient.
/// </summary>
/// <typeparam name="TMessage">The message type. Must implement ITargetedMessage.</typeparam>
/// <param name="target">The InstanceId of the recipient.</param>
/// <param name="message">The message to emit.</param>
/// <returns>True if any handler received the message; false otherwise.</returns>
/// <remarks>
/// <para><b>Added in v2.0.0.</b></para>
/// <para>For broadcast messages, use <see cref="Broadcast{TMessage}"/> instead.</para>
/// </remarks>
/// <example>
/// <code>
/// DamageMessage damageMessage = new DamageMessage(25);
/// messageBus.TargetedEmit(targetId, ref damageMessage);
/// </code>
/// </example>
public bool TargetedEmit<TMessage>(InstanceId target, ref TMessage message)
    where TMessage : struct, ITargetedMessage
{
    // Implementation
}
```

### Version Annotations

Use version notes when behavior changes or new APIs appear:

```csharp
/// <remarks>
/// <para><b>Added in v2.1.0.</b></para>
/// <para>Interceptors run before handlers and can modify or cancel messages.</para>
/// </remarks>
public void RegisterInterceptor<TMessage>(IMessageInterceptor<TMessage> interceptor)
```

### Variations

#### Variation A: Minimal Private/Internal API Docs

```csharp
/// <summary>
/// Validates the message before processing.
/// </summary>
private bool ValidateMessage<TMessage>(ref TMessage message)
    where TMessage : struct, IMessage
{
    // Implementation
}
```

#### Variation B: Full Public API Docs

```csharp
/// <summary>
/// Registers a handler for untargeted messages of the specified type.
/// </summary>
/// <typeparam name="TMessage">The message type to handle.</typeparam>
/// <param name="handler">The handler delegate invoked when a matching message is emitted.</param>
/// <returns>A handle that can be used to unregister this specific handler.</returns>
/// <remarks>
/// <para>Untargeted messages are received by all registered handlers regardless of targeting.</para>
/// <para>The handler receives the message by reference to avoid allocations.</para>
/// </remarks>
/// <example>
/// <code>
/// MessageRegistrationHandle handle = token.RegisterUntargeted<DifficultyChanged>(HandleDifficulty);
///
/// private void HandleDifficulty(ref DifficultyChanged message)
/// {
///     currentDifficulty = message.newLevel;
/// }
/// </code>
/// </example>
public MessageRegistrationHandle RegisterUntargeted<TMessage>(FastHandler<TMessage> handler)
    where TMessage : struct, IUntargetedMessage
```

## See Also

- [Documentation Updates](documentation-updates.md)
- [Documentation Style Guide](documentation-style-guide.md)
- [Documentation Update Workflow](documentation-update-workflow.md)

## References

- [Microsoft XML Documentation Comments](https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/xmldoc/)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-01-22 | Initial version |
