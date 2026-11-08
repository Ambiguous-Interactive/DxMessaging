# DI Framework Integrations

DxMessaging integrates with three Unity DI frameworks: VContainer, Zenject, and Reflex. This section shows how to bind `IMessageBus` in each container, use scoped lifetimes for per-scene buses, and inject `IMessageRegistrationBuilder` so handlers stay testable.

## Why Combine DI + Messaging?

DI and messaging cover different concerns:

- **Constructor Injection** -- for service dependencies (repositories, managers, configuration)
- **Messaging** -- for reactive events (damage taken, item collected, game state changes)

This combination provides:

- Explicit dependencies with testable constructors
- Loose coupling between systems via events
- Scoped message buses for scene isolation
- Clean separation of concerns

## Supported Frameworks

These are setup fragments for the callbacks named below. Each helper registers a new
bus for its container. Follow the linked guides to register builders and services.

### [VContainer](vcontainer.md)

Call the helper inside `LifetimeScope.Configure(IContainerBuilder builder)`:

```csharp
using DxMessaging.Unity.Integrations.VContainer;

builder.RegisterDxMessagingBus();
```

### [Zenject](zenject.md)

Call the helper inside `MonoInstaller.InstallBindings()`, using its `Container` property:

```csharp
using DxMessaging.Unity.Integrations.Zenject;

Container.BindDxMessagingBus();
```

### [Reflex](reflex.md)

Call the helper inside `IInstaller.InstallBindings(ContainerBuilder builder)`:

```csharp
using DxMessaging.Unity.Integrations.Reflex;

builder.AddDxMessagingBus();
```

## Configuration at a Glance

| Framework      | Configuration pattern                                              |
| -------------- | ------------------------------------------------------------------ |
| **VContainer** | `LifetimeScope.Configure` and service entry-point registrations    |
| **Zenject**    | `MonoInstaller.InstallBindings` in the context's installer list    |
| **Reflex**     | `IInstaller.InstallBindings` discovered under its `ContainerScope` |

## Common Patterns

All frameworks support the same core patterns:

1. **Global Bus** -- Explicitly bind `MessageHandler.MessageBus` when the container should share the global bus
1. **Scoped Bus** -- Register a new bus in each scene or feature container and emit on the bus resolved from that container
1. **Builder Injection** -- Bind `IMessageRegistrationBuilder` through the framework setup; each independent service owns and disposes its lease
1. **Testable Design** -- Use an isolated `MessageBus` per test and verify delivery and cleanup

See each framework's guide for detailed examples and best practices.
