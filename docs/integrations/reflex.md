# DxMessaging + Reflex

[Back to Integrations Overview](index.md)

---

## Overview

**Reflex** is a minimal, lightweight dependency injection framework for Unity. DxMessaging integrates with Reflex, allowing you to:

- **Inject `IMessageBus`** in any class with minimal overhead
- **Use DI for construction** + DxMessaging for events (best of both worlds)
- **Minimal API surface** -- small number of concepts to understand
- **Compatible** -- Reflex and DxMessaging can be used together

**Why combine DI + Messaging?** Use constructor injection for service dependencies (repositories, managers) and messaging for reactive events (damage taken, item collected), combining both approaches.

---

## Quick Start

### Prerequisites

- DxMessaging installed via UPM
- Reflex installed (`gustavopsantos/Reflex`) via source or UPM

### 1. Create a Reflex Installer

```csharp
using DxMessaging.Core.MessageBus;
using DxMessaging.Unity.Integrations.Reflex;
using Reflex.Core;

// Option A: Use IInstaller interface (recommended for explicit registration)
public sealed class DxMessagingInstaller : IInstaller
{
    public void InstallBindings(ContainerBuilder containerBuilder)
    {
        // Bind MessageBus as singleton implementing IMessageBus
        containerBuilder.AddSingleton(
            typeof(MessageBus),
            typeof(MessageBus),
            typeof(IMessageBus)
        );

        // Optional: Enable automatic IMessageRegistrationBuilder binding
        // Install the DxMessagingRegistrationInstaller to get IMessageRegistrationBuilder
        #if REFLEX_PRESENT
        new DxMessagingRegistrationInstaller().InstallBindings(containerBuilder);
        #endif
    }
}

// Option B: Extend Installer base class (common pattern)
public sealed class DxMessagingInstallerAlt : Installer
{
    protected override void InstallBindings()
    {
        Container.Bind<MessageBus>().AsSingleton();
        Container.Bind<IMessageBus>().FromContainer<MessageBus>();
    }
}
```

**Note:** You must import the `DxMessaging.Unity.Integrations.Reflex` namespace to access `DxMessagingRegistrationInstaller`.

#### Add to your scene

1. Create a `SceneContext` or `ProjectContext` in your scene
1. Add `DxMessagingInstaller` to the installers list
1. Reflex will now inject `IMessageBus` automatically

---

## Usage Patterns

### Pattern 1: Inject into Plain Classes (Recommended for Services)

Use `IMessageRegistrationBuilder` to create message handlers in non-MonoBehaviour classes:

```csharp
using DxMessaging.Core.MessageBus;
using DxMessaging.Core.Attributes;

// Define a message
[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct PlayerDamaged
{
    public readonly int damage;
}

// Service that listens to messages
public sealed class DamageService
{
    private readonly MessageRegistrationLease _lease;

    // Builder is injected automatically when using the installer
    public DamageService(IMessageRegistrationBuilder registrationBuilder)
    {
        var options = new MessageRegistrationBuildOptions
        {
            Configure = token =>
            {
                _ = token.RegisterUntargeted<PlayerDamaged>(OnPlayerDamaged);
            }
        };

        _lease = registrationBuilder.Build(options);
    }

    public void Initialize()
    {
        _lease.Activate();  // Start listening
    }

    public void Dispose()
    {
        _lease.Dispose();   // Clean up
    }

    private static void OnPlayerDamaged(ref PlayerDamaged message)
    {
        UnityEngine.Debug.Log($"Player took {message.damage} damage!");
    }
}
```

#### Register the service in your installer

```csharp
// Using IInstaller interface
public void InstallBindings(ContainerBuilder containerBuilder)
{
    containerBuilder.AddSingleton(typeof(DamageService), typeof(DamageService));
    // Call Initialize() from a bootstrap MonoBehaviour
}

// Or using Installer base class
protected override void InstallBindings()
{
    Container.Bind<DamageService>().AsSingleton();
    // Call Initialize() from a bootstrap MonoBehaviour
}
```

**Note:** Reflex doesn't have lifecycle interfaces like `IInitializable`. Call `Initialize()` and `Dispose()` manually from a controlling MonoBehaviour or bootstrap script.

---

### Pattern 2: Configure MessagingComponents (For Existing MonoBehaviours)

```csharp
using DxMessaging.Core.MessageBus;
using DxMessaging.Unity;
using Reflex.Attributes;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(MessagingComponent))]
public sealed class MessagingComponentConfigurator : MonoBehaviour
{
    [Inject]
    private IMessageBus _messageBus;

    private void Awake()
    {
        GetComponent<MessagingComponent>().Configure(
            _messageBus,
            MessageBusRebindMode.RebindActive
        );
    }
}
```

#### Usage

1. Add `MessagingComponentConfigurator` alongside any `MessagingComponent` in your prefabs
1. Reflex will inject the bus in `Awake()` before handlers are registered
1. Your message handlers now use the container-managed bus

---

### Pattern 3: Inject IMessageBus Directly

For simple emission without listening, inject `IMessageBus` directly:

```csharp
public sealed class GameBootstrap : MonoBehaviour
{
    [Inject]
    private IMessageBus _messageBus;

    private void Start()
    {
        var message = new GameStarted();
        _messageBus.EmitUntargeted(ref message);
    }
}
```

---

## Advanced: Object Pooling

When using object pooling with Reflex:

```csharp
public sealed class EnemyPool
{
    private readonly Container _container;
    private readonly Queue<Enemy> _pool = new();

    public EnemyPool(Container container)
    {
        _container = container;
    }

    public Enemy Spawn()
    {
        Enemy enemy;
        if (_pool.Count > 0)
        {
            enemy = _pool.Dequeue();
        }
        else
        {
            enemy = UnityEngine.Object.Instantiate(enemyPrefab);
            _container.Inject(enemy);  // Inject dependencies
        }
        return enemy;
    }

    public void Return(Enemy enemy)
    {
        _pool.Enqueue(enemy);
    }
}
```

---

## Testing with Reflex

### Unit Tests

```csharp
using DxMessaging.Core.MessageBus;
using Reflex.Core;
using NUnit.Framework;

[TestFixture]
public class DamageServiceTests
{
    [Test]
    public void Initialize_ListensToMessages()
    {
        // Arrange
        var builder = new ContainerBuilder();
        var bus = new MessageBus();
        builder.AddSingleton<IMessageBus>(bus);
        builder.AddSingleton<DamageService>();
        var container = builder.Build();

        bool messageReceived = false;
        var handler = new MessageHandler(new InstanceId(1), bus) { active = true };
        var token = MessageRegistrationToken.Create(handler, bus);
        _ = token.RegisterUntargeted<PlayerDamaged>(
            (ref PlayerDamaged msg) => messageReceived = true
        );
        token.Enable();

        // Act
        var service = container.Resolve<DamageService>();
        service.Initialize();
        var message = new PlayerDamaged(25);
        bus.EmitUntargeted(ref message);

        // Assert
        Assert.IsTrue(messageReceived);
    }
}
```

---

## Checklist

### Initial Setup

- [ ] Install DxMessaging and Reflex
- [ ] Create `DxMessagingInstaller` with bus bindings
- [ ] Add installer to your `SceneContext` or `ProjectContext`
- [ ] Import `DxMessaging.Unity.Integrations.Reflex` and install `DxMessagingRegistrationInstaller` for `IMessageRegistrationBuilder` support

### Integration

- [ ] Use `IMessageRegistrationBuilder` in plain classes
- [ ] Call `Initialize()` and `Dispose()` manually from a bootstrap script
- [ ] Add `MessagingComponentConfigurator` to prefabs with `MessagingComponent`
- [ ] Replace `MessageHandler.MessageBus` references with injected `IMessageBus`

### Pooling

- [ ] Use `container.Inject(instance)` for pooled objects
- [ ] Ensure injection happens before message handlers are registered

### Testing

- [ ] Create isolated `ContainerBuilder` instances in tests
- [ ] Use `builder.AddSingleton<IMessageBus>(new MessageBus())` for test buses

---

## Next Steps

- **[Zenject Integration](zenject.md)** -- Full-featured DI with extensive Unity support
- **[VContainer Integration](vcontainer.md)** -- Lightweight alternative with scoped lifetimes
- **[Back to Documentation Hub](../getting-started/index.md)** -- Browse all docs
