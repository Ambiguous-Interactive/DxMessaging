# DxMessaging + VContainer

[Back to Integrations Overview](index.md)

---

## Overview

**VContainer** is a fast, lightweight dependency injection framework for Unity with minimal overhead. DxMessaging integrates with VContainer, allowing you to:

- **Inject `IMessageBus`** in any class with deterministic lifetimes
- **Create per-scope buses** for scene isolation (perfect for additive scenes)
- **Use DI for construction** + DxMessaging for events (best of both worlds)
- **Compatible** -- VContainer and DxMessaging can be used together

**Why combine DI + Messaging?** Use constructor injection for service dependencies (repositories, managers) and messaging for reactive events (damage taken, item collected), combining both approaches. VContainer's scoped lifetimes support per-scene message buses.

---

## Quick Start

### Prerequisites

- DxMessaging installed via UPM
- VContainer installed (Git URL or OpenUPM)

These examples require VContainer. DxMessaging's integration assembly uses
`VCONTAINER_PRESENT` as an assembly-local Version Define. Call the helpers directly
from these framework-specific examples. If your application needs conditional code,
configure [Version Defines on its own assembly](https://docs.unity3d.com/2021.3/Documentation/Manual/ScriptCompilationAssemblyDefinitionFiles.html#define-symbols).

For a custom application asmdef, reference `WallstopStudios.DxMessaging`,
`WallstopStudios.DxMessaging.Attributes`, `WallstopStudios.DxMessaging.VContainer`, `VContainer`, and `VContainer.Unity` for
these examples.

### 1. Create a LifetimeScope with DxMessaging

```csharp
using DxMessaging.Core.MessageBus;
using DxMessaging.Unity.Integrations.VContainer; // Required for RegisterDxMessagingBus()
using UnityEngine;
using VContainer;
using VContainer.Unity;

public sealed class MessagingLifetimeScope : LifetimeScope
{
    protected override void Configure(IContainerBuilder builder)
    {
        // Register MessageBus as both concrete and interface through the safe helper.
        builder.RegisterDxMessagingBus();

        // Register the builder for services that request it.
        builder.RegisterMessageRegistrationBuilder();
    }
}
```

**Note:** You must import the `DxMessaging.Unity.Integrations.VContainer` namespace to access the registration extension methods.

#### Add to your scene

1. Create an empty GameObject in your scene
1. Add the `MessagingLifetimeScope` component
1. This creates a singleton bus for the entire scene

**Tip:** Use `Lifetime.Singleton` for project-wide buses, or `Lifetime.Scoped` for isolated scene/feature buses.

---

## Usage Patterns

### Pattern 1: Inject into Plain Classes (Recommended for Services)

Use `IMessageRegistrationBuilder` to create message handlers in non-MonoBehaviour classes:

```csharp
using System;
using DxMessaging.Core.MessageBus;
using DxMessaging.Core.Attributes;
using DxMessaging.Core;
using VContainer.Unity;

// Define a message
[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct PlayerSpawned
{
    public readonly int playerId;
}

// Service that listens to messages
public sealed class PlayerService : IStartable, IDisposable
{
    private readonly MessageRegistrationLease _lease;

    // Builder is injected automatically when using the scope
    public PlayerService(IMessageRegistrationBuilder registrationBuilder)
    {
        var options = new MessageRegistrationBuildOptions
        {
            Configure = token =>
            {
                _ = token.RegisterBroadcastWithoutSource<PlayerSpawned>(OnPlayerSpawned);
            }
        };

        _lease = registrationBuilder.Build(options);
    }

    public void Start()
    {
        _lease.Activate();  // Start listening when container starts
    }

    public void Dispose()
    {
        _lease.Dispose();   // Clean up when container disposes
    }

    private static void OnPlayerSpawned(InstanceId player, PlayerSpawned message)
    {
        UnityEngine.Debug.Log($"Player {message.playerId} spawned!");
    }
}
```

#### Register the service in your scope

```csharp
using DxMessaging.Unity.Integrations.VContainer; // Required for extension method

// ...

protected override void Configure(IContainerBuilder builder)
{
    builder.RegisterDxMessagingBus();
    builder.RegisterMessageRegistrationBuilder();

    // Register your service
    builder.RegisterEntryPoint<PlayerService>();
}
```

---

### Pattern 2: Configure MessagingComponents (For Existing MonoBehaviours)

Configure the messaging component from an injected method. The container must register
and inject the configurator; the scope below registers one from its scene hierarchy.
Save each component class in a `.cs` file with the same name.

```csharp
using DxMessaging.Core.MessageBus;
using DxMessaging.Unity;
using UnityEngine;
using VContainer;

[RequireComponent(typeof(MessagingComponent))]
public sealed class MessagingComponentConfigurator : MonoBehaviour
{
    [Inject]
    public void Construct(IMessageBus messageBus)
    {
        GetComponent<MessagingComponent>().Configure(
            messageBus,
            MessageBusRebindMode.RebindActive
        );
    }
}
```

Use this scope for the example, or add its component registration to your existing
scope after registering the bus:

```csharp
using DxMessaging.Unity.Integrations.VContainer;
using VContainer;
using VContainer.Unity;

public sealed class ConfiguredMessagingScope : LifetimeScope
{
    protected override void Configure(IContainerBuilder builder)
    {
        builder.RegisterDxMessagingBus();
        builder.RegisterComponentInHierarchy<MessagingComponentConfigurator>();
    }
}
```

#### Usage

1. Add `ConfiguredMessagingScope` to the scene and place one `MessagingComponentConfigurator`
   alongside the `MessagingComponent` to configure.
1. Building the scope injects the bus and calls `Construct`. For multiple components,
   register each explicit component reference with `builder.RegisterComponent(configurator)`.
1. Emit through the same injected `IMessageBus` to reach listeners on that bus.

`MessageAwareComponent` stages handlers in `Awake()` and normally enables them in
`OnEnable()`. Injection can happen after those callbacks. `RebindActive` moves existing
active registrations to the injected bus and selects it for future registrations.
Messages emitted before configuration are not replayed. Configuring a receiving
component does not change the global bus used by parameterless emission helpers.

See VContainer's [method injection](https://vcontainer.hadashikick.jp/resolving/method-injection)
and [scene component registration](https://vcontainer.hadashikick.jp/registering/register-monobehaviour).

---

### Pattern 3: Inject IMessageBus Directly

For simple emission without listening, inject `IMessageBus` directly:

```csharp
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Extensions;
using DxMessaging.Core.MessageBus;
using VContainer.Unity;

[DxUntargetedMessage]
public readonly partial struct GameStarted { }

public sealed class GameInitializer : IStartable
{
    private readonly IMessageBus _messageBus;

    public GameInitializer(IMessageBus messageBus)
    {
        _messageBus = messageBus;
    }

    public void Start()
    {
        GameStarted message = new();
        _messageBus.EmitUntargeted(ref message);
    }
}
```

---

## Advanced: Scene Scopes and Isolation

VContainer's scoped lifetimes support per-scene message buses. This is useful for additive scenes or isolated gameplay features:

Pass the prefab's `LifetimeScope` component to `LoadLevel`. Retain the returned child
scope and dispose it when the level unloads. See VContainer's
[child scope creation](https://vcontainer.hadashikick.jp/scoping/generate-child-with-code-first).

```csharp
using DxMessaging.Core.MessageBus;
using DxMessaging.Unity.Integrations.VContainer; // Required for extension method
using VContainer;
using VContainer.Unity;

public sealed class LevelLoader
{
    private readonly LifetimeScope _parentScope;

    public LevelLoader(LifetimeScope parentScope)
    {
        _parentScope = parentScope;
    }

    public LifetimeScope LoadLevel(LifetimeScope lifetimeScopePrefab)
    {
        // Create a child scope with its own MessageBus
        return _parentScope.CreateChildFromPrefab(lifetimeScopePrefab, builder =>
        {
            builder.RegisterDxMessagingBus();
            builder.RegisterMessageRegistrationBuilder();
        });
    }
}
```

### Benefits

- Each scene gets its own isolated message bus
- Messages don't leak between scenes
- Suitable for multiplayer lobbies, mini-games, or feature-scoped events

---

## Testing with VContainer

### Unit Tests

With `GameStarted` and `GameInitializer` from Pattern 3 in scope, register the concrete class to
check constructor injection and a direct `Start()` call. This test does not install VContainer's
player-loop dispatcher or verify automatic entry-point startup. Use `RegisterEntryPoint` in the
application's lifetime scope when you want that startup lifecycle.

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;
using NUnit.Framework;
using VContainer;

[TestFixture]
public sealed class GameInitializerTests
{
    [Test]
    public void StartEmitsGameStarted()
    {
        ContainerBuilder builder = new();
        MessageBus bus = new();
        builder.RegisterInstance<IMessageBus>(bus);
        builder.Register<GameInitializer>(Lifetime.Singleton);
        using IObjectResolver container = builder.Build();

        bool messageReceived = false;
        MessageHandler handler = new(new InstanceId(1), bus) { active = true };
        using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
        _ = token.RegisterUntargeted<GameStarted>(
            (in GameStarted message) => messageReceived = true
        );
        token.Enable();

        container.Resolve<GameInitializer>().Start();

        Assert.That(messageReceived, Is.True);
    }
}
```

### Play-Mode Tests

Put the following fixture in a PlayMode test assembly with the Unity Test Framework, VContainer,
and DxMessaging integration references. It uses `GameStarted` from Pattern 3. Each scope owns its
container and native GameObject; disposing the listener token before the scopes removes its
subscription. The test checks bus separation directly and needs no frame delay:

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;
using DxMessaging.Unity.Integrations.VContainer;
using NUnit.Framework;
using VContainer;
using VContainer.Unity;

[TestFixture]
public sealed class MessageBusPlayModeTests
{
    [Test]
    public void ScopesKeepMessageBusesSeparate()
    {
        using LifetimeScope first = LifetimeScope.Create(
            builder => builder.RegisterDxMessagingBus()
        );
        using LifetimeScope second = LifetimeScope.Create(
            builder => builder.RegisterDxMessagingBus()
        );
        IMessageBus firstBus = first.Container.Resolve<IMessageBus>();
        IMessageBus secondBus = second.Container.Resolve<IMessageBus>();
        Assert.That(firstBus, Is.Not.SameAs(secondBus));

        int received = 0;
        MessageHandler handler = new(new InstanceId(1), firstBus) { active = true };
        using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, firstBus);
        _ = token.RegisterUntargeted<GameStarted>(
            (in GameStarted message) => ++received
        );
        token.Enable();

        GameStarted message = new();
        secondBus.UntargetedBroadcast(ref message);
        Assert.That(received, Is.Zero);
        firstBus.UntargetedBroadcast(ref message);
        Assert.That(received, Is.EqualTo(1));

        token.Dispose();
        firstBus.UntargetedBroadcast(ref message);
        Assert.That(received, Is.EqualTo(1));
    }
}
```

---

## Checklist

### Initial Setup

- [ ] Install DxMessaging and VContainer
- [ ] Create `MessagingLifetimeScope` with `builder.RegisterDxMessagingBus()`
- [ ] Add scope to your scene as a GameObject component
- [ ] Import `DxMessaging.Unity.Integrations.VContainer` namespace for extension methods
- [ ] Call `builder.RegisterMessageRegistrationBuilder()` inside the scope's `Configure` method

### Integration

- [ ] Use `IMessageRegistrationBuilder` in plain classes with `IStartable`/`IDisposable`
- [ ] Register each `MessagingComponentConfigurator` with its scope to inject and configure its `MessagingComponent`
- [ ] Replace `MessageHandler.MessageBus` references with injected `IMessageBus`
- [ ] Consider using scoped buses for scene isolation

### Testing

- [ ] Create isolated `ContainerBuilder` instances in tests
- [ ] Use `builder.RegisterInstance<IMessageBus>(new MessageBus())` for test buses
- [ ] Dispose scopes after tests to ensure clean teardown

---

## Next Steps

- **[Zenject Integration](zenject.md)** -- Full-featured DI with extensive Unity support
- **[Reflex Integration](reflex.md)** -- Minimal DI framework
- **[Back to Documentation Hub](../getting-started/index.md)** -- Browse all docs
