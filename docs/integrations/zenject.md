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

### 1. Create an Installer

Create a `DxMessagingInstaller` to bind the message bus to your Zenject container:

```csharp
using DxMessaging.Core.MessageBus;
using Zenject;

public sealed class DxMessagingInstaller : MonoInstaller
{
    public override void InstallBindings()
    {
        // Bind MessageBus as a singleton and expose IMessageBus interface
        Container.BindInterfacesAndSelfTo<MessageBus>().AsSingle();

        // Optional: Enable automatic IMessageRegistrationBuilder binding
        // Requires ZENJECT_PRESENT define (auto-added by DxMessaging when Zenject detected)
        #if ZENJECT_PRESENT
        Container.RegisterMessageRegistrationBuilder();
        #endif
    }
}
```

#### Add to your ProjectContext

1. Select (or create) your `ProjectContext` prefab
1. Add `DxMessagingInstaller` as a MonoInstaller
1. Save the prefab

---

## Usage Patterns

### Pattern 1: Inject into Plain Classes (Recommended for Services)

Use `IMessageRegistrationBuilder` to create message handlers in non-MonoBehaviour classes:

```csharp
using DxMessaging.Core.MessageBus;
using DxMessaging.Core.Attributes;
using Zenject;

// Define a message
[DxUntargetedMessage]
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
                _ = token.RegisterUntargeted<PlayerSpawned>(OnPlayerSpawned);
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

    private static void OnPlayerSpawned(ref PlayerSpawned message)
    {
        UnityEngine.Debug.Log($"Player {message.playerId} spawned!");
    }
}
```

#### Register the service in your installer

```csharp
public sealed class GameInstaller : MonoInstaller
{
    public override void InstallBindings()
    {
        Container.BindInterfacesAndSelfTo<PlayerController>().AsSingle();
    }
}
```

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
    private IMessageBus _messageBus;

    private void Awake()
    {
        MessagingComponent component = GetComponent<MessagingComponent>();
        component.Configure(_messageBus, MessageBusRebindMode.RebindActive);
    }
}
```

#### Usage

1. Add `MessagingComponentConfigurator` alongside any `MessagingComponent` in your prefabs
1. Zenject will inject the bus before `RegisterMessageHandlers` is called
1. Your message handlers now use the container-managed bus

**Alternative approach:** Extend `MessageAwareComponent` and override `Awake`:

```csharp
public class ZenjectAwareComponent : MessageAwareComponent
{
    [Inject]
    private IMessageBus _messageBus;

    protected override void Awake()
    {
        Configure(_messageBus, MessageBusRebindMode.RebindActive);
        base.Awake();
    }
}
```

---

### Pattern 3: Inject IMessageBus Directly

For simple cases, inject `IMessageBus` and emit messages directly:

```csharp
public sealed class GameInitializer : IInitializable
{
    private readonly IMessageBus _messageBus;

    public GameInitializer(IMessageBus messageBus)
    {
        _messageBus = messageBus;
    }

    public void Initialize()
    {
        var message = new GameStarted();
        _messageBus.EmitUntargeted(ref message);
    }
}
```

---

## Advanced: Bridging to Zenject Signals

If you're gradually migrating from Zenject Signals to DxMessaging, you can create a bridge:

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;
using System;
using Zenject;

[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct SceneTransition
{
    public readonly string sceneName;
}

public sealed class DxToSignalBridge : IInitializable, IDisposable
{
    private readonly IMessageBus _messageBus;
    private readonly SignalBus _signalBus;
    private MessageRegistrationToken _token;

    public DxToSignalBridge(IMessageBus messageBus, SignalBus signalBus)
    {
        _messageBus = messageBus;
        _signalBus = signalBus;
    }

    public void Initialize()
    {
        // Create a handler to listen to DxMessaging events
        var handler = new MessageHandler(new InstanceId(0), _messageBus)
        {
            active = true
        };
        _token = MessageRegistrationToken.Create(handler, _messageBus);

        // Bridge DxMessaging -> Zenject Signals
        _ = _token.RegisterUntargeted<SceneTransition>(OnSceneTransition);
        _token.Enable();
    }

    public void Dispose()
    {
        _token?.Disable();
    }

    private void OnSceneTransition(ref SceneTransition message)
    {
        // Forward to SignalBus for legacy consumers
        _signalBus.Fire(message);
    }
}
```

### Register the bridge in your installer

```csharp
public override void InstallBindings()
{
    Container.BindInterfacesAndSelfTo<DxToSignalBridge>().AsSingle();
    Container.DeclareSignal<SceneTransition>();
}
```

---

## Testing with Zenject

### Unit Tests

```csharp
using DxMessaging.Core.MessageBus;
using Zenject;
using NUnit.Framework;

[TestFixture]
public class GameInitializerTests : ZenjectUnitTestFixture
{
    [Test]
    public void Initialize_EmitsGameStarted()
    {
        // Arrange
        var bus = new MessageBus();
        Container.Bind<IMessageBus>().FromInstance(bus).AsSingle();
        Container.BindInterfacesAndSelfTo<GameInitializer>().AsSingle();

        bool messageReceived = false;
        var handler = new MessageHandler(new InstanceId(1), bus) { active = true };
        var token = MessageRegistrationToken.Create(handler, bus);
        _ = token.RegisterUntargeted<GameStarted>(
            (ref GameStarted msg) => messageReceived = true
        );
        token.Enable();

        // Act
        var initializer = Container.Resolve<GameInitializer>();
        initializer.Initialize();

        // Assert
        Assert.IsTrue(messageReceived);
    }
}
```

---

## Checklist

### Initial Setup

- [ ] Install DxMessaging and Zenject/Extenject
- [ ] Create `DxMessagingInstaller` with `Container.BindInterfacesAndSelfTo<MessageBus>()`
- [ ] Add installer to your `ProjectContext`
- [ ] Add `#if ZENJECT_PRESENT` check and call `Container.RegisterMessageRegistrationBuilder()`

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
