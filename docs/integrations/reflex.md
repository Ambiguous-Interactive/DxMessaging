# DxMessaging + Reflex

[Back to Integrations Overview](index.md)

## Overview

Use Reflex for object construction and DxMessaging for message delivery. The optional
DxMessaging Reflex assembly registers both `MessageBus` and `IMessageBus`, and it can provide
`IMessageRegistrationBuilder` to container-created services.

> **Changed in v3.2.3:** The examples target Reflex 14.0 or newer. DxMessaging's registration
> helpers also adapt to the pre-14 `AddSingleton` API.

## Quick start

### Prerequisites

- Install DxMessaging through UPM.
- Install Reflex 14.0 or newer (`com.gustavopsantos.reflex`).
- Create a Reflex settings asset inside a `Resources` folder with
  **Assets > Create > Reflex > Settings**.

### Create an installer

Reflex discovers `IInstaller` components below a `ContainerScope`. Derive the installer from
`MonoBehaviour` so it can be attached to that hierarchy.

```csharp
using DxMessaging.Unity.Integrations.Reflex;
using Reflex.Core;
using UnityEngine;

public sealed class DxMessagingInstaller : MonoBehaviour, IInstaller
{
    public void InstallBindings(ContainerBuilder builder)
    {
        builder.AddDxMessagingBus();
        new DxMessagingRegistrationInstaller().InstallBindings(builder);
    }
}
```

`AddDxMessagingBus()` uses an explicit factory and exposes the same singleton as both
`MessageBus` and `IMessageBus`. `DxMessagingRegistrationInstaller` adds
`IMessageRegistrationBuilder`.

### Add it to a scope

1. Create a scene scope with **GameObject > Reflex > SceneScope**.
1. Add `DxMessagingInstaller` to the `SceneScope` GameObject or one of its children.
1. Enter Play Mode. Reflex builds the scene container and injects scene objects from it.

For one bus shared across scenes, put the installer on a Reflex RootScope prefab and add that
prefab to the `RootScopes` list in the Reflex settings asset.

## Register a service

The following service owns a `MessageRegistrationLease`. Its constructor stages a real
handler, `Initialize()` activates it, and `Dispose()` releases it.

```csharp
using System;
using DxMessaging.Core.Attributes;
using DxMessaging.Core.MessageBus;

[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct PlayerDamaged
{
    public readonly int damage;
}

public sealed class DamageService : IDisposable
{
    private readonly MessageRegistrationLease _lease;

    public DamageService(IMessageRegistrationBuilder registrationBuilder)
    {
        MessageRegistrationBuildOptions options = new()
        {
            Configure = token =>
            {
                _ = token.RegisterUntargeted<PlayerDamaged>(OnPlayerDamaged);
            },
        };

        _lease = registrationBuilder.Build(options);
    }

    public int LastDamage { get; private set; }

    public void Initialize()
    {
        _lease.Activate();
    }

    public void Dispose()
    {
        _lease.Dispose();
    }

    private void OnPlayerDamaged(ref PlayerDamaged message)
    {
        LastDamage = message.damage;
    }
}
```

Register the service with Reflex 14's singleton and lazy-resolution settings:

```csharp
using Reflex.Enums;

builder.RegisterType(
    typeof(DamageService),
    Lifetime.Singleton,
    Resolution.Lazy
);
```

Reflex disposes singleton services with their owning container. Call `Initialize()` from a
bootstrap component after resolving the service. Reflex does not provide an `IInitializable`
lifecycle contract.

## Configure an existing MessagingComponent

Inject the container bus before `MessagingComponent` registers its handlers:

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

Add this configurator beside each `MessagingComponent` that should use the container-owned bus.
Reflex's scene scope runs before ordinary `Awake()` methods and injects the field first.

## Inject IMessageBus directly

Inject `IMessageBus` into a component that only emits messages:

```csharp
using DxMessaging.Core.Extensions;
using DxMessaging.Core.MessageBus;
using Reflex.Attributes;
using UnityEngine;

public sealed class GameBootstrap : MonoBehaviour
{
    [Inject]
    private IMessageBus _messageBus;

    private void Start()
    {
        GameStarted message = new();
        _messageBus.EmitUntargeted(ref message);
    }
}
```

## Inject pooled objects

Reflex injects scene objects when it creates the scene container. Inject objects instantiated
later through `GameObjectInjector` before returning them to callers:

```csharp
using System.Collections.Generic;
using Reflex.Core;
using Reflex.Injectors;
using UnityEngine;

public sealed class EnemyPool
{
    private readonly Container _container;
    private readonly Enemy _enemyPrefab;
    private readonly Queue<Enemy> _pool = new();

    public EnemyPool(Container container, Enemy enemyPrefab)
    {
        _container = container;
        _enemyPrefab = enemyPrefab;
    }

    public Enemy Spawn()
    {
        if (_pool.Count > 0)
        {
            return _pool.Dequeue();
        }

        Enemy enemy = UnityEngine.Object.Instantiate(_enemyPrefab);
        GameObjectInjector.InjectObject(enemy.gameObject, _container);
        return enemy;
    }

    public void Return(Enemy enemy)
    {
        _pool.Enqueue(enemy);
    }
}
```

## Test with Reflex

Build an isolated container with a real bus and the same DxMessaging installer used at runtime:

```csharp
using DxMessaging.Core.MessageBus;
using DxMessaging.Unity.Integrations.Reflex;
using NUnit.Framework;
using Reflex.Core;
using Reflex.Enums;

[TestFixture]
public sealed class DamageServiceTests
{
    [Test]
    public void InitializeListensToMessages()
    {
        ContainerBuilder builder = new();
        MessageBus bus = new();
        builder.RegisterValue(
            bus,
            new[] { typeof(MessageBus), typeof(IMessageBus) }
        );
        new DxMessagingRegistrationInstaller().InstallBindings(builder);
        builder.RegisterType(
            typeof(DamageService),
            Lifetime.Singleton,
            Resolution.Lazy
        );

        using Container container = builder.Build();
        DamageService service = container.Resolve<DamageService>();
        service.Initialize();

        PlayerDamaged message = new(25);
        bus.EmitUntargeted(ref message);

        Assert.That(service.LastDamage, Is.EqualTo(25));
    }
}
```

## Checklist

### Initial setup

- [ ] Install DxMessaging and Reflex.
- [ ] Create a Reflex settings asset under `Resources`.
- [ ] Create a `ContainerScope` and attach an `IInstaller` component.
- [ ] Call `AddDxMessagingBus()` and install `DxMessagingRegistrationInstaller`.

### Integration

- [ ] Register container services with a concrete Reflex lifetime and resolution mode.
- [ ] Activate builder-created leases from a bootstrap component.
- [ ] Dispose leases directly or let Reflex dispose their singleton owner.
- [ ] Configure each existing `MessagingComponent` with the injected bus.

### Pooling and tests

- [ ] Inject runtime-created GameObjects with `GameObjectInjector` before use.
- [ ] Build a fresh `ContainerBuilder` and `MessageBus` for each test.
- [ ] Register real handlers and assert their observable result.

## Next steps

- [Zenject Integration](zenject.md) -- Zenject container wiring
- [VContainer Integration](vcontainer.md) -- VContainer lifetime scopes
- [Back to Documentation Hub](../getting-started/index.md) -- all DxMessaging guides
