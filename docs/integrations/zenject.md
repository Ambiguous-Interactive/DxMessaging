# DxMessaging + Zenject

[Back to Integrations Overview](index.md)

---

## Overview

**Zenject** (also known as Extenject) is a dependency injection framework for Unity with extensive container, sub-container, and factory support. DxMessaging integrates with Zenject, allowing you to:

- **Inject `IMessageBus`** as a singleton dependency in any class
- **Use DI for construction** + DxMessaging for events (best of both worlds)
- **Create per-scope message buses** for scene or gameplay isolation
- **Bridge to SignalBus** for gradual migration from Zenject Signals

**Why combine DI + Messaging?** Use constructor injection for service dependencies (repositories, managers) and messaging for reactive events (damage taken, item collected), combining both approaches.

---

## Quick Start

### Prerequisites

- DxMessaging installed via UPM
- Zenject/Extenject installed (source or UPM)

These examples require Zenject/Extenject. DxMessaging's `ZENJECT_PRESENT` Version
Define applies to its integration assembly. Add your own assembly's Version Define
if your application conditionally compiles framework-specific code; see
[Unity's assembly definition rules](https://docs.unity3d.com/2021.3/Documentation/Manual/ScriptCompilationAssemblyDefinitionFiles.html#define-symbols).

### 1. Create an Installer

Create a `DxMessagingInstaller` to bind the message bus to your Zenject container:

```csharp
using DxMessaging.Core.MessageBus;
using DxMessaging.Unity.Integrations.Zenject;
using Zenject;
using ZenjectBusProvider = DxMessaging.Unity.Integrations.Zenject.DxMessagingRegistrationInstaller.ContainerMessageBusProvider;

public sealed class DxMessagingInstaller : MonoInstaller
{
    public override void InstallBindings()
    {
        Container.BindDxMessagingBus();
        Container.Bind<IMessageRegistrationBuilder>()
            .FromMethod(context => new MessageRegistrationBuilder(
                new ZenjectBusProvider(
                    context.Container,
                    context.Container.Resolve<IMessageBus>()
                )
            ))
            .AsTransient();
    }
}
```

The factory creates a builder backed by this container's resolved `IMessageBus`.
`ZenjectBusProvider` is an alias for the existing provider type supplied by DxMessaging.
Save the installer as `DxMessagingInstaller.cs`.

#### Add to your ProjectContext

1. Select (or create) your `ProjectContext` prefab
1. Add the `DxMessagingInstaller` component and add its reference to the context's installer list
1. Save the prefab

---

## Usage Patterns

### Pattern 1: Inject into Plain Classes (Recommended for Services)

Use `IMessageRegistrationBuilder` to create message handlers in non-MonoBehaviour classes:

```csharp
using System;
using DxMessaging.Core.MessageBus;
using DxMessaging.Core.Attributes;
using DxMessaging.Core;
using Zenject;

// Define a message
[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct PlayerSpawned
{
    public readonly int playerId;
}

// Service that listens to messages
public sealed class PlayerController : IInitializable, IDisposable
{
    private readonly MessageRegistrationLease _lease;

    // Builder is injected automatically when using the installer
    public PlayerController(IMessageRegistrationBuilder registrationBuilder)
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

    public void Initialize()
    {
        _lease.Activate();  // Start listening when container initializes
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

#### Register the service in your installer

```csharp
using Zenject;

public sealed class GameInstaller : MonoInstaller
{
    public override void InstallBindings()
    {
        Container.BindInterfacesAndSelfTo<PlayerController>().AsSingle();
    }
}
```

Add `GameInstaller` to the same context's installer list. Its `IInitializable` and
`IDisposable` bindings connect the service's lease activation and cleanup to that
context's lifetime. See [Extenject's installer setup](https://github.com/Mathijs-Bakker/Extenject#hello-world-example).

---

### Pattern 2: Configure MessagingComponents (For Existing MonoBehaviours)

If you have existing `MessageAwareComponent` scripts, you can inject the container-managed bus into them:

```csharp
using DxMessaging.Core.MessageBus;
using DxMessaging.Unity;
using UnityEngine;
using Zenject;

[RequireComponent(typeof(MessagingComponent))]
public sealed class MessagingComponentConfigurator : MonoBehaviour
{
    [Inject]
    public void Construct(IMessageBus messageBus)
    {
        MessagingComponent component = GetComponent<MessagingComponent>();
        component.Configure(messageBus, MessageBusRebindMode.RebindActive);
    }
}
```

#### Usage

1. Add `MessagingComponentConfigurator` beside each `MessagingComponent` that should use the bus.
1. Add a configured `SceneContext` to the scene; its injection pass calls `Construct` before
   ordinary scene `Awake` methods. Create runtime prefabs through Zenject's prefab factories so they
   receive injection too.
1. Keep component-specific bus/provider settings consistent with the container bus.

`MessageAwareComponent.Awake` stages registrations and `OnEnable` activates them. Configuring in a
separate component's `Awake` would depend on their relative execution order. `RebindActive` can move
existing registrations, but it does not replay messages emitted before configuration. Parameterless
emission shorthands still select the global bus; pass the container bus explicitly when emitting.
See [Extenject's context order](https://github.com/Mathijs-Bakker/Extenject#zenject-order-of-operations).

Alternatively, derive from `MessageAwareComponent` and configure its bus during injection:

```csharp
using DxMessaging.Core.MessageBus;
using DxMessaging.Unity;
using Zenject;

public sealed class ZenjectAwareComponent : MessageAwareComponent
{
    [Inject]
    public void Construct(IMessageBus messageBus)
    {
        ConfigureMessageBus(messageBus, MessageBusRebindMode.RebindActive);
    }
}
```

---

### Pattern 3: Inject IMessageBus Directly

For simple cases, inject `IMessageBus` and emit messages directly:

```csharp
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Extensions;
using DxMessaging.Core.MessageBus;
using Zenject;

[DxUntargetedMessage]
public readonly partial struct GameStarted { }

public sealed class GameInitializer : IInitializable
{
    private readonly IMessageBus _messageBus;

    public GameInitializer(IMessageBus messageBus)
    {
        _messageBus = messageBus;
    }

    public void Initialize()
    {
        GameStarted message = new();
        _messageBus.EmitUntargeted(ref message);
    }
}
```

To run it through the context's startup lifecycle, add this binding to an installer on that context:

```csharp
Container.BindInterfacesAndSelfTo<GameInitializer>().AsSingle();
```

---

## Advanced: Bridging to Zenject Signals

If you're gradually migrating from Zenject Signals to DxMessaging, you can create a bridge:

```csharp
using System;
using DxMessaging.Core.Attributes;
using DxMessaging.Core.MessageBus;
using Zenject;

[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct SceneTransition
{
    public readonly string sceneName;
}

public sealed class DxToSignalBridge : IInitializable, IDisposable
{
    private readonly SignalBus _signalBus;
    private readonly MessageRegistrationLease _lease;

    public DxToSignalBridge(
        IMessageRegistrationBuilder registrationBuilder,
        SignalBus signalBus
    )
    {
        _signalBus = signalBus;
        _lease = registrationBuilder.Build(new MessageRegistrationBuildOptions
        {
            Configure = token =>
            {
                _ = token.RegisterUntargeted<SceneTransition>(OnSceneTransition);
            },
        });
    }

    public void Initialize()
    {
        _lease.Activate();
    }

    public void Dispose()
    {
        _lease.Dispose();
    }

    private void OnSceneTransition(in SceneTransition message)
    {
        _signalBus.Fire(message);
    }
}
```

The builder stages one subscription in the constructor. `Initialize()` activates the lease;
repeated initialization keeps that same registration. `Dispose()` removes it, and initialization
after disposal throws `ObjectDisposedException`. The bridge uses the builder's configured bus.

### Register the bridge in your installer

Add these bindings to an installer on the same context as `DxMessagingInstaller`.
Install Zenject's signal support once per context before declaring the signal. If another
installer already installs `SignalBusInstaller`, keep that installation and add only the
bridge binding and signal declaration below:

```csharp
public override void InstallBindings()
{
    SignalBusInstaller.Install(Container);
    Container.BindInterfacesAndSelfTo<DxToSignalBridge>().AsSingle();
    Container.DeclareSignal<SceneTransition>();
}
```

---

## Testing with Zenject

### Unit Tests

With `GameStarted` and `GameInitializer` from Pattern 3 in scope, test their emission with a fresh
bus. This plain NUnit test calls `Initialize()` directly; it does not test Zenject context startup
or Unity injection order.

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;
using NUnit.Framework;

[TestFixture]
public sealed class GameInitializerTests
{
    [Test]
    public void InitializeEmitsGameStarted()
    {
        MessageBus bus = new();
        bool messageReceived = false;
        MessageHandler handler = new(new InstanceId(1), bus) { active = true };
        using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
        _ = token.RegisterUntargeted<GameStarted>(
            (in GameStarted message) => messageReceived = true
        );
        token.Enable();

        GameInitializer initializer = new(bus);
        initializer.Initialize();

        Assert.That(messageReceived, Is.True);
    }
}
```

---

## Checklist

### Initial Setup

- [ ] Install DxMessaging and Zenject/Extenject
- [ ] Create `DxMessagingInstaller` with `Container.BindDxMessagingBus()` and the builder factory above
- [ ] Add the installer component to your `ProjectContext` and its installer list
- [ ] Import `DxMessaging.Unity.Integrations.Zenject` and bind `IMessageRegistrationBuilder`

### Integration

- [ ] Use `IMessageRegistrationBuilder` in plain classes with `IInitializable`/`IDisposable`
- [ ] Add `MessagingComponentConfigurator` to prefabs with `MessagingComponent`
- [ ] Replace `MessageHandler.MessageBus` references with injected `IMessageBus`
- [ ] Consider bridging to SignalBus if migrating from Zenject Signals

### Testing

- [ ] Inject `IMessageBus` in tests using `FromInstance(new MessageBus())`
- [ ] Verify messages flow through the container-provided bus

---

## Next Steps

- **[VContainer Integration](vcontainer.md)** -- Lightweight alternative to Zenject
- **[Reflex Integration](reflex.md)** -- Minimal DI framework
- **[Back to Documentation Hub](../getting-started/index.md)** -- Browse all docs
