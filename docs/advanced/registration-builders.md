# Registration Builders

The `MessageRegistrationBuilder` provides a structured way to create message registrations with fine-grained control over lifecycle, configuration, and resource cleanup.

This guide covers:

- When to use the builder pattern
- Configuring registrations with options
- Managing lifecycle with leases
- Practical usage examples

---

## Table of Contents

- [Overview](#overview)
- [MessageRegistrationBuilder](#messageregistrationbuilder)
- [MessageRegistrationBuildOptions](#messageregistrationbuildoptions)
- [MessageRegistrationLease](#messageregistrationlease)
- [MessageRegistrationLifecycle](#messageregistrationlifecycle)
- [Code Examples](#code-examples)
- [See Also](#see-also)

---

## Overview

DxMessaging offers two approaches to message registration:

| Approach              | Use Case                                                                                 |
| --------------------- | ---------------------------------------------------------------------------------------- |
| Direct token creation | Simple scenarios with manual lifecycle management                                        |
| Builder pattern       | Complex scenarios requiring DI integration, lifecycle hooks, or structured configuration |

### When to use the builder pattern

- You need lifecycle callbacks (build, activate, deactivate, dispose)
- You want to integrate with dependency injection containers
- You prefer a more declarative configuration style
- You need to manage multiple registrations as a single unit

#### When direct token creation is sufficient

- Simple MonoBehaviour-based messaging
- No need for lifecycle hooks
- Using `MessagingComponent` or similar built-in components

---

## MessageRegistrationBuilder

The `MessageRegistrationBuilder` class creates `MessageRegistrationLease` instances based on configuration options.

### Creating a Builder

```csharp
using DxMessaging.Core.MessageBus;

// Builder that uses global bus resolution
MessageRegistrationBuilder builder = new MessageRegistrationBuilder();

// Builder with a custom provider
IMessageBusProvider provider = new FixedMessageBusProvider(myMessageBus);
MessageRegistrationBuilder builderWithProvider = new MessageRegistrationBuilder(provider);
```

### Building Leases

Call `Build()` with options to create a lease:

```csharp
MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions
{
    ActivateOnBuild = true,
    Configure = token =>
    {
        _ = token.RegisterUntargeted<PlayerDamaged>(OnPlayerDamaged);
    }
};

using MessageRegistrationLease lease = builder.Build(options);
```

---

## MessageRegistrationBuildOptions

Configure how the builder creates registrations using `MessageRegistrationBuildOptions`.

### Owner Configuration

Specify who owns the registration:

```csharp
// Explicit InstanceId owner
options.Owner = new InstanceId(42);

// Unity Object owner (Unity 2021.3+)
options.UnityOwner = gameObject;  // or any Component
```

When omitted, a synthetic owner ID is generated automatically. `UnityOwner` takes precedence over `Owner` when both are set.

### Message Bus Selection

Control which bus handles registrations:

```csharp
// Use a specific bus directly
options.PreferredMessageBus = myMessageBus;

// Use a provider (falls back if PreferredMessageBus is null)
options.MessageBusProvider = new FixedMessageBusProvider(otherBus);
```

Resolution order:

1. `PreferredMessageBus` if set
1. `MessageBusProvider.Resolve()` if provider is set
1. Builder's provider (from constructor) if set
1. `null` (uses global bus)

---

### Handler State

Control the initial state of handlers:

```csharp
// Whether the underlying MessageHandler starts active (default: true)
options.HandlerStartsActive = true;

// Whether to call Enable() immediately after building (default: false)
options.ActivateOnBuild = true;
```

### Diagnostics

Enable diagnostic mode for debugging:

```csharp
options.EnableDiagnostics = true;
```

This sets `MessageRegistrationToken.DiagnosticMode` to `true`, enabling detailed logging.

### Configure Callback

Register handlers immediately after token creation:

```csharp
options.Configure = token =>
{
    _ = token.RegisterUntargeted<GameStarted>(OnGameStarted);
    // For targeted messages, provide the target InstanceId
    _ = token.RegisterTargeted<DamageMessage>(targetInstanceId, OnDamage);
};
```

This callback runs after the token is created but before lifecycle hooks and activation.

**Note:** `RegisterTargeted<T>` requires an `InstanceId` target parameter specifying which entity should receive the message. For Unity objects, use `RegisterGameObjectTargeted<T>` or `RegisterComponentTargeted<T>` instead.

### Lifecycle Hooks

Add callbacks for lifecycle events:

```csharp
options.Lifecycle = new MessageRegistrationLifecycle(
    onBuild: token => Debug.Log("Token created"),
    onActivate: token => Debug.Log("Token enabled"),
    onDeactivate: token => Debug.Log("Token disabled"),
    onDispose: token => Debug.Log("Token disposed")
);
```

---

## MessageRegistrationLease

A `MessageRegistrationLease` wraps the created token and provides lifecycle management.

### Properties

| Property     | Type                       | Description                           |
| ------------ | -------------------------- | ------------------------------------- |
| `Token`      | `MessageRegistrationToken` | The underlying registration token     |
| `Handler`    | `MessageHandler`           | The handler hosting registrations     |
| `MessageBus` | `IMessageBus`              | The bus used for registrations        |
| `Owner`      | `InstanceId`               | The owner identifier                  |
| `IsActive`   | `bool`                     | Whether the lease is currently active |

### Activation Methods

```csharp
// Enable the token and invoke OnActivate callback
lease.Activate();

// Disable the token and invoke OnDeactivate callback
lease.Deactivate();
```

- `Activate()` throws `ObjectDisposedException` if called after disposal
- `Deactivate()` is safe to call multiple times or after disposal
- If `ActivateOnBuild` activation throws, the builder disposes the partially constructed lease before throwing again. If cleanup itself cannot finish because deregistration remains retryable, `Build()` throws `MessageRegistrationBuildException`; dispose `exception.Lease` after resolving the cleanup failure.

### Dispose Pattern

Leases implement `IDisposable`:

```csharp
using MessageRegistrationLease lease = builder.Build(options);
// ... use the lease
// Automatically deactivated and disposed at end of scope
```

Disposal sequence:

1. Calls `Deactivate()` if active (triggers `OnDeactivate`)
1. Invokes `OnDispose` callback
1. Clears the owned token's staged registrations and token-local diagnostics
1. Marks the lease as disposed after token cleanup succeeds

Token cleanup still runs when a lifecycle callback throws; `Dispose()` rethrows the first lifecycle exception after cleanup. If the bus deregistration action itself throws before cleanup, the failed token cleanup remains retryable by calling `Dispose()` again.

---

## MessageRegistrationLifecycle

The `MessageRegistrationLifecycle` struct holds callbacks for each lifecycle stage:

```csharp
public readonly struct MessageRegistrationLifecycle
{
    public MessageRegistrationLifecycle(
        Action<MessageRegistrationToken> onBuild,
        Action<MessageRegistrationToken> onActivate,
        Action<MessageRegistrationToken> onDeactivate,
        Action<MessageRegistrationToken> onDispose);

    public Action<MessageRegistrationToken> OnBuild { get; }
    public Action<MessageRegistrationToken> OnActivate { get; }
    public Action<MessageRegistrationToken> OnDeactivate { get; }
    public Action<MessageRegistrationToken> OnDispose { get; }
}
```

### Callback Order

1. `OnBuild` -- Immediately after lease creation, before activation
1. `OnActivate` -- When `Activate()` is called (or automatically if `ActivateOnBuild = true`)
1. `OnDeactivate` -- When `Deactivate()` is called or during disposal while active
1. `OnDispose` -- During `Dispose()`, after deactivation

All callbacks receive the `MessageRegistrationToken` as a parameter.

---

## Code Examples

### Basic Builder Usage

```csharp
using DxMessaging.Core.MessageBus;
using UnityEngine;

public sealed class BasicBuilderExample : MonoBehaviour
{
    private MessageRegistrationBuilder builder;
    private MessageRegistrationLease lease;

    private void Awake()
    {
        builder = new MessageRegistrationBuilder();

        MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions
        {
            UnityOwner = this,
            ActivateOnBuild = true,
            Configure = token =>
            {
                _ = token.RegisterUntargeted<GameEvent>(OnGameEvent);
            }
        };

        lease = builder.Build(options);
    }

    private void OnDestroy()
    {
        lease?.Dispose();
    }

    // Action<T> handler signature (used with RegisterUntargeted<T>(..., Action<T>))
    private void OnGameEvent(GameEvent message)
    {
        Debug.Log($"Received: {message}");
    }

    // Alternative: FastHandler<T> signature for better performance (avoids boxing)
    // private void OnGameEvent(ref GameEvent message)
    // {
    //     Debug.Log($"Received: {message}");
    // }
}
```

### DI Integration Pattern

```csharp
using DxMessaging.Core.MessageBus;
using UnityEngine;

public sealed class DIIntegrationExample : MonoBehaviour
{
    // Injected by your DI container
    private IMessageBusProvider messageBusProvider;
    private MessageRegistrationLease lease;

    public void Initialize(IMessageBusProvider provider)
    {
        messageBusProvider = provider;

        MessageRegistrationBuilder builder = new MessageRegistrationBuilder(provider);

        MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions
        {
            UnityOwner = this,
            ActivateOnBuild = true,
            Configure = ConfigureRegistrations
        };

        lease = builder.Build(options);
    }

    private void ConfigureRegistrations(MessageRegistrationToken token)
    {
        _ = token.RegisterUntargeted<PlayerSpawned>(OnPlayerSpawned);
        _ = token.RegisterUntargeted<PlayerDied>(OnPlayerDied);
    }

    private void OnDestroy()
    {
        lease?.Dispose();
    }

    // Action<T> handler signatures
    private void OnPlayerSpawned(PlayerSpawned message)
    {
        Debug.Log($"Player spawned: {message.PlayerId}");
    }

    private void OnPlayerDied(PlayerDied message)
    {
        Debug.Log($"Player died: {message.PlayerId}");
    }
}
```

### Custom Lifecycle Hooks

```csharp
using DxMessaging.Core.MessageBus;
using UnityEngine;

public sealed class LifecycleHooksExample : MonoBehaviour
{
    private MessageRegistrationBuilder builder;
    private MessageRegistrationLease lease;

    private void Awake()
    {
        builder = new MessageRegistrationBuilder();

        MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions
        {
            UnityOwner = this,
            HandlerStartsActive = true,
            ActivateOnBuild = false,  // We'll activate manually
            EnableDiagnostics = true,
            Configure = token =>
            {
                _ = token.RegisterUntargeted<LevelLoaded>(OnLevelLoaded);
            },
            Lifecycle = new MessageRegistrationLifecycle(
                onBuild: token => Debug.Log("[Lifecycle] Token built"),
                onActivate: token => Debug.Log("[Lifecycle] Activated - now receiving messages"),
                onDeactivate: token => Debug.Log("[Lifecycle] Deactivated - paused"),
                onDispose: token => Debug.Log("[Lifecycle] Disposed - cleanup complete")
            )
        };

        lease = builder.Build(options);
    }

    private void OnEnable()
    {
        // Activate when the component is enabled
        lease?.Activate();
    }

    private void OnDisable()
    {
        // Deactivate but don't dispose - we might re-enable
        lease?.Deactivate();
    }

    private void OnDestroy()
    {
        lease?.Dispose();
    }

    // Action<T> handler signature
    private void OnLevelLoaded(LevelLoaded message)
    {
        Debug.Log($"Level loaded: {message.LevelName}");
    }
}
```

### FixedMessageBusProvider

For scenarios where you always want to use a specific bus instance:

```csharp
using DxMessaging.Core.MessageBus;

// Create a provider that always returns a specific bus
IMessageBus myBus = new MessageBus();
IMessageBusProvider provider = new FixedMessageBusProvider(myBus);

// Use with builder
MessageRegistrationBuilder builder = new MessageRegistrationBuilder(provider);

// Or override per-build
MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions
{
    MessageBusProvider = provider,
    Configure = token => { /* ... */ }
};
```

---

## See Also

- [Message Bus Providers](message-bus-providers.md) -- More on the provider system
- [Runtime Configuration](runtime-configuration.md) -- Dynamic reconfiguration options
