# Runtime Message Bus Configuration

This guide covers how to configure message buses at runtime and retarget existing registrations. These features enable advanced scenarios like dependency injection, testing with isolated buses, and dynamic reconfiguration of messaging hierarchies.

## Table of Contents

- [Runtime Message Bus Configuration](#runtime-message-bus-configuration)
  - [When You Need This](#when-you-need-this)
  - [Global Message Bus Management](#global-message-bus-management)
  - [Re-binding Registrations](#re-binding-registrations)
  - [Common Patterns](#common-patterns)
  - [Memory Reclamation Policy](#memory-reclamation-policy)

---

## When You Need This

You'll use runtime configuration when you need to:

- **Integrate with DI containers** -- Replace the global bus with a container-managed instance
- **Isolate tests** -- Ensure each test uses its own bus to prevent interference
- **Support multiple game modes** -- Use different buses for different gameplay contexts (e.g., main game vs. mini-games)
- **Dynamically reconfigure components** -- Change which bus a component listens to after it's been created

If you're just getting started with DxMessaging, you probably don't need these features yet. The default global bus works great for most scenarios.

---

## Global Message Bus Management

By default, DxMessaging creates a single process-wide message bus during static initialization. This bus is available through `MessageHandler.MessageBus` and works great for most scenarios.

### Replacing the Global Bus

Use `SetGlobalMessageBus()` when you want to permanently replace the default bus:

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;

// Create a custom bus (often from a DI container)
IMessageBus customBus = new MessageBus();

// Replace the global bus
MessageHandler.SetGlobalMessageBus(customBus);

// All new registrations will now use customBus
var handler = new MessageHandler(new InstanceId(1)) { active = true };
// handler.MessageBus is now customBus
```

#### When to use this

- During application startup when integrating with DI containers
- When you want all message traffic to flow through a specific bus instance
- In integration tests that need complete control over message routing

**Important:** This permanently replaces the global bus for the lifetime of the application. All subsequent registrations will use the new bus unless explicitly configured otherwise.

### Temporarily Overriding the Global Bus

> **Fixed in v3.2.3:** Override scopes are safe to copy and allocate no managed objects per scope
> after `MessageHandler` static initialization. Keep the result as the concrete
> `GlobalMessageBusScope` type because converting a value type to `IDisposable` boxes it. Disposing
> any copy ends the override once. Nested scopes restore the nearest active parent even when
> disposed out of order. A later `SetGlobalMessageBus`, `ResetGlobalMessageBus`, or
> `DxMessagingStaticState.Reset` invalidates older scopes so they cannot restore stale state.

Use `OverrideGlobalMessageBus()` when you want to temporarily replace the bus and automatically restore it later:

```csharp
IMessageBus originalBus = MessageHandler.MessageBus;
IMessageBus temporaryBus = new MessageBus();

// Temporarily override
using (MessageHandler.OverrideGlobalMessageBus(temporaryBus))
{
    // Inside this scope, MessageHandler.MessageBus returns temporaryBus
    Assert.AreSame(temporaryBus, MessageHandler.MessageBus);

    // Any message handlers created here use temporaryBus
    var msg = new MyMessage();
    msg.EmitUntargeted();  // Goes to temporaryBus
}

// Outside the scope, the original bus is restored
Assert.AreSame(originalBus, MessageHandler.MessageBus);
```

#### When to use this

- In unit tests to isolate message traffic
- When temporarily redirecting messages for debugging or logging
- For scoped gameplay features that need their own message channel

**Pattern:** This returns a `GlobalMessageBusScope` struct. Use the concrete return value directly
with a `using` statement for allocation-free cleanup after static initialization. When the scope
exits, the nearest active previous bus is restored. The process-wide table supports 1,024 occupied
override slots. An out-of-order-disposed parent keeps its slot until all newer scopes have ended.
Create and dispose scopes, and set or reset the global bus, on Unity's main thread. These
configuration APIs follow DxMessaging's single-threaded runtime contract.

### Resetting to the Default Bus

Use `ResetGlobalMessageBus()` to restore the original startup bus:

```csharp
// Replace the global bus
MessageHandler.SetGlobalMessageBus(containerBus);

// Later, restore the original default bus
MessageHandler.ResetGlobalMessageBus();

// MessageHandler.MessageBus is now back to the original startup bus
```

#### When to use this

- After tests that called `SetGlobalMessageBus()`
- When tearing down a DI container and reverting to default behavior
- In development/debug scenarios where you want to reset state

### Accessing the Original Startup Bus

`MessageHandler.InitialGlobalMessageBus` always returns the original bus that was created during static initialization, regardless of any calls to `SetGlobalMessageBus()`:

```csharp
IMessageBus startupBus = MessageHandler.InitialGlobalMessageBus;

// Replace the global bus
MessageHandler.SetGlobalMessageBus(new MessageBus());

// InitialGlobalMessageBus is unchanged
Assert.AreSame(startupBus, MessageHandler.InitialGlobalMessageBus);

// But MessageBus returns the new bus
Assert.AreNotSame(startupBus, MessageHandler.MessageBus);
```

#### When to use this

- In diagnostic tools that need a stable reference point
- When you want to compare against the original bus for debugging
- In tests that need to verify bus isolation while still accessing the original

### Example: Bootstrapping a DI Container

Here's a complete example of integrating DxMessaging with a DI container:

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;

public class GameBootstrapper
{
    public void Bootstrap()
    {
        // Create container-managed bus
        IMessageBus containerBus = new MessageBus();

        // Register with container
        container.RegisterInstance<IMessageBus>(containerBus);

        // Replace global bus so all subsequent registrations use it
        MessageHandler.SetGlobalMessageBus(containerBus);

        // Now when MessagingComponents are created, they automatically
        // use the container-managed bus
    }
}
```

---

## Re-binding Registrations

Sometimes you need to change which bus a component listens to after it's already been set up. This is called "re-binding."

### The MessageBusRebindMode Enum

When re-binding, you have two options:

#### PreserveRegistrations

Keeps existing active registrations on their current bus, but sets the specified bus for future registrations:

```csharp
// Component is currently using busA
component.Configure(busA, MessageBusRebindMode.RebindActive);

// Later, switch to busB but preserve existing registrations
component.Configure(busB, MessageBusRebindMode.PreserveRegistrations);

// Existing handlers still listen on busA
// New registrations will use busB
```

##### When to use this

- When you want to gradually migrate a component to a new bus
- If you need to maintain backwards compatibility during refactoring
- When existing registrations must continue processing messages from their original source

#### RebindActive

Immediately moves all active registrations to the new bus:

```csharp
// Component is listening on busA
component.Configure(busA, MessageBusRebindMode.RebindActive);

// Switch everything to busB
component.Configure(busB, MessageBusRebindMode.RebindActive);

// All handlers now listen on busB
// busA handlers are deregistered and re-registered on busB
```

##### When to use this

- When you want complete, immediate migration to a new bus
- In DI scenarios where the container provides a new bus instance
- When you need atomic switching without mixed-bus behavior

### Retarget a Token Directly

You can also retarget individual `MessageRegistrationToken` instances:

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;

IMessageBus busA = new MessageBus();
IMessageBus busB = new MessageBus();

var handler = new MessageHandler(new InstanceId(1), busA) { active = true };
var token = MessageRegistrationToken.Create(handler, busA);
token.RegisterUntargeted<MyMessage>(OnMessage);
token.Enable();

// Later, retarget to busB
token.RetargetMessageBus(busB, MessageBusRebindMode.RebindActive);

// All registrations are now on busB
```

#### When to use this

- When you have direct access to a token and want fine-grained control
- In advanced scenarios like dynamic message routing or pooling
- When building custom messaging abstractions

### Example: Scene-Specific Buses

Here's a practical example of using re-binding for scene isolation:

```csharp
using DxMessaging.Core.MessageBus;
using DxMessaging.Unity;
using UnityEngine;

public class SceneMessagingManager : MonoBehaviour
{
    private IMessageBus _sceneLocalBus;

    private void Awake()
    {
        // Create a bus specific to this scene
        _sceneLocalBus = new MessageBus();

        // Find all messaging components in this scene
        MessagingComponent[] components = FindObjectsOfType<MessagingComponent>();

        // Retarget them to the scene-local bus
        foreach (var component in components)
        {
            component.Configure(_sceneLocalBus, MessageBusRebindMode.RebindActive);
        }
    }

    private void OnDestroy()
    {
        // When scene unloads, components are destroyed automatically
        // No need to manually clean up registrations
    }
}
```

#### Why this is useful

- Messages sent in this scene won't leak to other scenes
- When the scene unloads, all message traffic stops cleanly
- Perfect for additive scene workflows or mini-games

---

## Common Patterns

### Pattern 1: Test Isolation

Ensure each test has its own isolated bus:

```csharp
using NUnit.Framework;
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;

[TestFixture]
public class MySystemTests
{
    private IMessageBus _testBus;

    [SetUp]
    public void SetUp()
    {
        // Create a fresh bus for each test
        _testBus = new MessageBus();
        MessageHandler.SetGlobalMessageBus(_testBus);
    }

    [TearDown]
    public void TearDown()
    {
        // Restore the default bus
        MessageHandler.ResetGlobalMessageBus();
    }

    [Test]
    public void MyTest()
    {
        // This test uses _testBus exclusively
        // No interference from other tests
    }
}
```

### Pattern 2: Temporary Override with Automatic Cleanup

Use `using` statements for scoped overrides:

```csharp
[Test]
public void TestWithIsolation()
{
    IMessageBus isolatedBus = new MessageBus();

    using (MessageHandler.OverrideGlobalMessageBus(isolatedBus))
    {
        // Test code here uses isolatedBus
        var msg = new TestMessage();
        msg.EmitUntargeted();

        // Verify only this test's bus received the message
    }

    // Original bus automatically restored
}
```

### Pattern 3: DI Container Integration

Bootstrap with container-managed bus:

```csharp
public class DependencyInjectionBootstrap
{
    public void Initialize(IContainer container)
    {
        // Container creates the bus
        IMessageBus containerBus = container.Resolve<IMessageBus>();

        // Make it the global default
        MessageHandler.SetGlobalMessageBus(containerBus);

        // Now all MessagingComponents use the container bus by default
    }
}
```

### Pattern 4: Dynamic Component Reconfiguration

Change a component's bus at runtime:

```csharp
public class DynamicComponentManager
{
    public void SwitchComponentToBus(MessagingComponent component, IMessageBus newBus)
    {
        // Immediately move all handlers to the new bus
        component.Configure(newBus, MessageBusRebindMode.RebindActive);

        // Component now listens on newBus
    }
}
```

---

## Quick Reference

| API                                            | Purpose                                            | Use Case                        |
| ---------------------------------------------- | -------------------------------------------------- | ------------------------------- |
| `MessageHandler.MessageBus`                    | Access current global bus                          | Normal message emission         |
| `MessageHandler.InitialGlobalMessageBus`       | Access original startup bus                        | Diagnostics, debugging          |
| `MessageHandler.SetGlobalMessageBus(bus)`      | Permanently replace global bus                     | DI integration, test setup      |
| `MessageHandler.OverrideGlobalMessageBus(bus)` | Temporarily override with a concrete zero-GC scope | Test isolation, scoped features |
| `MessageHandler.ResetGlobalMessageBus()`       | Restore original startup bus                       | Test teardown, reset state      |
| `component.Configure(bus, mode)`               | Set component's bus                                | Component configuration         |
| `token.RetargetMessageBus(bus, mode)`          | Retarget a token                                   | Fine-grained control            |
| `MessageBusRebindMode.PreserveRegistrations`   | Keep existing registrations on old bus             | Gradual migration               |
| `MessageBusRebindMode.RebindActive`            | Move all registrations to new bus                  | Atomic switching                |

---

## Memory Reclamation Policy

Long-running buses retain per-message-type and per-context slots and a set of
shared collection pools. The memory reclamation system bounds that growth
through idle sweeps and an explicit `Trim` API. Both knobs live on the
`DxMessagingRuntimeSettings` ScriptableObject loaded from `Resources/`.

The asset is hot-reloadable. Live buses subscribe to
`DxMessagingRuntimeSettings.SettingsChanged` and re-apply caps without
recreation, which means you can tune the policy from the editor while Play
mode is running.

For scenario-driven tuning recommendations, the public `Trim` and
diagnostic-counter API surface, and worked examples for scene transitions
and leak diagnosis, see the [Memory Reclamation guide](../guides/memory-reclamation.md).
For the full parameter reference (including defaults, mins, and tooltip
text), see the [Runtime Settings reference](../reference/runtime-settings.md).

---

## See Also

- **[Memory Reclamation Guide](../guides/memory-reclamation.md)** -- when reclamation runs, how to tune it, and how to verify it
- **[Runtime Settings Reference](../reference/runtime-settings.md)** -- complete parameter reference for `DxMessagingRuntimeSettings`
- **[Message Bus Providers](message-bus-providers.md)** -- ScriptableObject-based provider system for design-time configuration
- **[Registration Builders](registration-builders.md)** -- Fluent API for building message registrations with priority and lifecycle control
- **[DI Integration Guides](../integrations/index.md)** -- Zenject, VContainer, and Reflex integration patterns
- **[Testing Guide](../guides/testing.md)** -- Patterns for unit-testing handlers, isolating per-test buses, and asserting on emission history
- **[Back to Documentation Hub](../getting-started/index.md)** -- Browse all docs
