# Message Bus Providers

DxMessaging provides a flexible provider system that lets you configure which message bus drives your components. This works both at design time (with ScriptableObject providers) and at runtime (with provider handles).

This guide covers:

- The `IMessageBusProvider` interface
- Built-in ScriptableObject providers
- The `MessageBusProviderHandle` system
- How to create custom providers
- Practical usage patterns

---

## Table of Contents

- [Overview](#overview)
- [IMessageBusProvider Interface](#imessagebusprovider-interface)
- [Built-in Providers](#built-in-providers)
  - [Current Global Message Bus Provider](#current-global-message-bus-provider)
  - [Initial Global Message Bus Provider](#initial-global-message-bus-provider)
- [MessageBusProviderHandle](#messagebusproviderhandle)
- [Using Providers with Components](#using-providers-with-components)
- [Creating Custom Providers](#creating-custom-providers)
- [Common Patterns](#common-patterns)

---

## Overview

Providers abstract away the details of how a message bus is resolved. This lets you:

- **Swap buses at design time** -- Change a ScriptableObject reference without modifying code
- **Integrate with DI containers** -- Resolve buses from your container
- **Support runtime reconfiguration** -- Change which bus a component uses dynamically
- **Isolate scenes or features** -- Use different buses for different parts of your game

If you're using the default global bus everywhere, you probably don't need providers. They're most useful when you need flexibility or are integrating with DI frameworks.

---

## IMessageBusProvider Interface

All providers implement this simple interface:

```csharp
public interface IMessageBusProvider
{
    IMessageBus Resolve();
}
```

When a component needs a bus, it calls `Resolve()` on its provider. The provider returns the appropriate bus instance.

**Key insight:** By abstracting bus resolution behind this interface, components don't need to know whether they're using the global bus, a container-managed bus, or something completely custom.

---

## Built-in Providers

DxMessaging ships with two ScriptableObject-based providers. These are assets you can create in the editor and reference in your scenes.

### Current Global Message Bus Provider

`CurrentGlobalMessageBusProvider` returns whatever bus is currently set as the global bus via `MessageHandler.MessageBus`. If you override the global bus at runtime, this provider reflects that change.

#### Creating the asset

Right-click in the Project window:

```text
Create > Wallstop Studios > DxMessaging > Message Bus Providers > Current Global Message Bus
```

#### When to use this

- When you want components to follow the global bus, even if it changes
- In DI scenarios where the global bus is replaced during bootstrap
- For components that should always use the "active" bus

#### Example

```csharp
using DxMessaging.Core.MessageBus;
using DxMessaging.Unity;
using UnityEngine;

[RequireComponent(typeof(MessagingComponent))]
public sealed class MessagingComponentConfigurator : MonoBehaviour
{
    [SerializeField]
    private CurrentGlobalMessageBusProvider provider;

    private void Awake()
    {
        MessagingComponent component = GetComponent<MessagingComponent>();
        component.Configure(provider, MessageBusRebindMode.RebindActive);
    }
}
```

### Initial Global Message Bus Provider

`InitialGlobalMessageBusProvider` always returns the original startup bus that was created during static initialization. It ignores any calls to `SetGlobalMessageBus()` or `OverrideGlobalMessageBus()`.

#### Creating the asset

Right-click in the Project window:

```text
Create > Wallstop Studios > DxMessaging > Message Bus Providers > Initial Global Message Bus
```

#### When to use this

- In diagnostic tools that need a stable reference point
- When testing with temporary bus overrides but need access to the original
- For debugging scenarios where you want to compare against the startup bus

#### Example

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;
using UnityEngine;

public class DiagnosticLogger : MonoBehaviour
{
    [SerializeField]
    private InitialGlobalMessageBusProvider initialProvider;

    private void Start()
    {
        // Always logs to the original bus, even if global bus is overridden
        IMessageBus startupBus = initialProvider.Resolve();

        // Can compare with current global bus
        bool busWasReplaced = startupBus != MessageHandler.MessageBus;
        Debug.Log($"Bus replaced: {busWasReplaced}");
    }
}
```

---

## MessageBusProviderHandle

`MessageBusProviderHandle` is a serializable struct that can reference either:

- A ScriptableObject provider (design-time configuration)
- A runtime provider instance (runtime configuration)

The handle resolves the runtime provider when one is set; otherwise it resolves the asset. That way the same field accepts an editor-assigned ScriptableObject or a runtime override without branching at every call site.

### Key Methods

```csharp
// Create from a runtime provider
MessageBusProviderHandle handle = MessageBusProviderHandle.FromProvider(myProvider);

// Associate a runtime provider with an existing handle
handle = handle.WithRuntimeProvider(myRuntimeProvider);

// Resolve the provider (runtime takes precedence over asset)
if (handle.TryGetProvider(out IMessageBusProvider provider))
{
    IMessageBus resolvedBus = provider.Resolve();
}

// Or resolve the bus directly
IMessageBus bus = handle.ResolveBus();
```

### How It Works

The handle has two fields:

1. `Provider` -- A serialized reference to a ScriptableObject provider (visible in Inspector)
1. A runtime provider instance (not serialized)

When you call `TryGetProvider()` or `ResolveBus()`, it checks:

1. Is there a runtime provider? Use that first.
1. Otherwise, use the serialized ScriptableObject provider.
1. If neither exists, return null or the global default.

**Why this matters:** You can design your prefabs with ScriptableObject references, then override them at runtime with DI-provided buses. No code changes needed.

### Example: Design Time Configuration

```csharp
using DxMessaging.Unity;
using UnityEngine;

public class PlayerController : MessageAwareComponent
{
    [SerializeField]
    private MessageBusProviderHandle providerHandle;

    protected override void Awake()
    {
        // Configure with the handle before base.Awake()
        if (providerHandle.TryGetProvider(out var provider))
        {
            ConfigureMessageBus(provider, MessageBusRebindMode.RebindActive);
        }

        base.Awake();
    }
}
```

In the Inspector, you can drag a `CurrentGlobalMessageBusProvider` or `InitialGlobalMessageBusProvider` asset onto the `providerHandle` field.

### Example: Runtime Override

```csharp
using DxMessaging.Core.MessageBus;
using DxMessaging.Unity;

public class DynamicConfigurator
{
    public void ConfigureWithRuntimeBus(MessageAwareComponent component, IMessageBus runtimeBus)
    {
        // Create a runtime provider
        var provider = new RuntimeMessageBusProvider(runtimeBus);

        // Wrap it in a handle
        var handle = MessageBusProviderHandle.FromProvider(provider);

        // Configure the component
        component.ConfigureMessageBus(handle, MessageBusRebindMode.RebindActive);
    }
}

// Simple runtime provider implementation
public class RuntimeMessageBusProvider : IMessageBusProvider
{
    private readonly IMessageBus _bus;

    public RuntimeMessageBusProvider(IMessageBus bus)
    {
        _bus = bus;
    }

    public IMessageBus Resolve() => _bus;
}
```

---

## Using Providers with Components

### MessagingComponent

`MessagingComponent` has three `Configure()` overloads:

```csharp
// Direct bus reference
component.Configure(messageBus, MessageBusRebindMode.RebindActive);

// Provider interface
component.Configure(provider, MessageBusRebindMode.RebindActive);

// Provider handle (design-time or runtime)
component.Configure(providerHandle, MessageBusRebindMode.RebindActive);
```

### MessageAwareComponent

`MessageAwareComponent` exposes `ConfigureMessageBus()` with the same three overloads:

```csharp
// Direct bus
ConfigureMessageBus(messageBus, MessageBusRebindMode.RebindActive);

// Provider
ConfigureMessageBus(provider, MessageBusRebindMode.RebindActive);

// Handle
ConfigureMessageBus(providerHandle, MessageBusRebindMode.RebindActive);
```

**Best practice:** Call `ConfigureMessageBus()` in `Awake()` before calling `base.Awake()` to ensure the bus is set before message handlers are registered.

### MessagingComponentInstaller

`MessagingComponentInstaller` configures all `MessagingComponent` descendants in a hierarchy:

```csharp
using DxMessaging.Core.MessageBus;
using DxMessaging.Unity;
using UnityEngine;

public class SceneSetup : MonoBehaviour
{
    [SerializeField]
    private MessagingComponentInstaller installer;

    [SerializeField]
    private CurrentGlobalMessageBusProvider provider;

    private void Awake()
    {
        // Configure installer with provider
        installer.SetProvider(MessageBusProviderHandle.FromProvider(provider));

        // Apply to all child MessagingComponents
        installer.ApplyConfiguration();
    }
}
```

This is useful when you want to configure an entire prefab hierarchy or scene section with a single provider.

---

## Creating Custom Providers

You can create your own providers for advanced scenarios:

### Example: Container-Managed Provider

```csharp
using DxMessaging.Core.MessageBus;

public class ContainerMessageBusProvider : IMessageBusProvider
{
    private readonly IDependencyContainer _container;

    public ContainerMessageBusProvider(IDependencyContainer container)
    {
        _container = container;
    }

    public IMessageBus Resolve()
    {
        // Resolve from container each time
        return _container.Resolve<IMessageBus>();
    }
}
```

### Example: ScriptableObject Provider for Specific Bus

```csharp
using DxMessaging.Core.MessageBus;
using UnityEngine;

[CreateAssetMenu(menuName = "Game/Messaging/Custom Bus Provider")]
public class CustomBusProvider : ScriptableObject, IMessageBusProvider
{
    [SerializeField]
    private bool useTestBus;

    private static IMessageBus _testBus;
    private static IMessageBus _productionBus;

    public IMessageBus Resolve()
    {
        if (useTestBus)
        {
            return _testBus ??= new MessageBus();
        }
        return _productionBus ??= new MessageBus();
    }
}
```

### Example: Lazy-Initialized Provider

```csharp
using DxMessaging.Core.MessageBus;

public class LazyMessageBusProvider : IMessageBusProvider
{
    private IMessageBus _cachedBus;

    public IMessageBus Resolve()
    {
        // Only create the bus when first requested
        return _cachedBus ??= new MessageBus();
    }
}
```

---

## Common Patterns

### Pattern 1: Design-Time Configuration

Set up providers in the editor, no code needed:

1. Create a `CurrentGlobalMessageBusProvider` asset in your project
1. Add a `MessagingComponent` to your prefab
1. Add a `MessagingComponentConfigurator` script that references the provider
1. In Awake, call `component.Configure(provider, ...)`

This lets designers swap providers without touching code.

### Pattern 2: DI Container Integration

Use a provider to resolve buses from your container:

```csharp
// In your DI installer
container.RegisterInstance<IMessageBus>(new MessageBus());

// In a bootstrap script
var provider = new ContainerMessageBusProvider(container);
MessageHandler.SetGlobalMessageBus(provider.Resolve());

// Now all components use the container bus
```

### Pattern 3: Scene-Scoped Buses

Create a provider that returns a scene-specific bus:

```csharp
public class SceneMessageBusManager : MonoBehaviour
{
    private IMessageBus _sceneBus;

    private void Awake()
    {
        // Create scene-local bus
        _sceneBus = new MessageBus();

        // Configure all components in this scene
        var components = GetComponentsInChildren<MessagingComponent>();
        var provider = new RuntimeMessageBusProvider(_sceneBus);

        foreach (var component in components)
        {
            component.Configure(provider, MessageBusRebindMode.RebindActive);
        }
    }
}
```

Messages sent in this scene stay in this scene.

### Pattern 4: Runtime Provider Override

Start with a design-time provider, override at runtime:

```csharp
public class RuntimeReconfiguration : MonoBehaviour
{
    [SerializeField]
    private MessageAwareComponent component;

    [SerializeField]
    private MessageBusProviderHandle designTimeHandle;  // Set in Inspector

    private void Start()
    {
        // Use design-time provider initially
        component.ConfigureMessageBus(designTimeHandle, MessageBusRebindMode.RebindActive);
    }

    public void SwitchToRuntimeBus(IMessageBus runtimeBus)
    {
        // Override with runtime provider
        var runtimeProvider = new RuntimeMessageBusProvider(runtimeBus);
        var handle = MessageBusProviderHandle.FromProvider(runtimeProvider);
        component.ConfigureMessageBus(handle, MessageBusRebindMode.RebindActive);
    }
}
```

---

## Quick Reference

| Type                                   | Purpose                                       | Use Case                          |
| -------------------------------------- | --------------------------------------------- | --------------------------------- |
| `IMessageBusProvider`                  | Interface for bus resolution                  | Create custom providers           |
| `CurrentGlobalMessageBusProvider`      | ScriptableObject returning current global bus | Follow the active global bus      |
| `InitialGlobalMessageBusProvider`      | ScriptableObject returning startup bus        | Diagnostics, stable reference     |
| `MessageBusProviderHandle`             | Serializable wrapper for providers            | Design-time + runtime flexibility |
| `handle.FromProvider(provider)`        | Create handle from runtime provider           | Runtime configuration             |
| `handle.WithRuntimeProvider(provider)` | Add runtime provider to handle                | Override design-time config       |
| `handle.TryGetProvider(out provider)`  | Resolve provider from handle                  | Get the actual provider           |
| `handle.ResolveBus()`                  | Resolve bus directly                          | Shortcut to get bus               |

---

## See Also

- **[Runtime Configuration](runtime-configuration.md)** -- Setting and overriding global buses, re-binding registrations
- **[Registration Builders](registration-builders.md)** -- Fluent API for building message registrations with priority and lifecycle control
- **[DI Integration Guides](../integrations/index.md)** -- Zenject, VContainer, and Reflex integration patterns
- **[Unity Integration](../guides/unity-integration.md)** -- MessagingComponent and MessageAwareComponent deep dive
- **[Back to Documentation Hub](../getting-started/index.md)** -- Browse all docs
