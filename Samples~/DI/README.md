# DxMessaging DI Samples

These snippets illustrate how to consume `IMessageRegistrationBuilder` inside common Unity dependency injection containers. The scripts compile only when the corresponding scripting define is enabled and the container package is present.

## Setup

1. Install the relevant container package (Zenject/Extenject, VContainer, or Reflex) into your Unity project.
1. Enable the matching scripting define symbol in **Project Settings > Player > Scripting Define Symbols**:
   - `ZENJECT_PRESENT`
   - `VCONTAINER_PRESENT`
   - `REFLEX_PRESENT`
1. Import the sample folder you need into your Unity project (`Assets/Samples/DxMessaging/*`).

Each sample shows:

- Registering `IMessageRegistrationBuilder` via the provided shim under [Runtime/Unity/Integrations](../../Runtime/Unity/Integrations/).
- Constructing a `MessageRegistrationLease` in a container-managed service.
- Activating and disposing the lease through the container lifecycle.

### Container examples

- Zenject sample installer: [SampleInstaller.cs](./Zenject/SampleInstaller.cs)
- VContainer sample lifetime scope: [SampleLifetimeScope.cs](./VContainer/SampleLifetimeScope.cs)
- Reflex sample installer: [SampleInstaller.cs](./Reflex/SampleInstaller.cs)

## Container walkthrough

1. **Hook up one container**
   - **Zenject**:
     - Add `DxMessagingRegistrationInstaller` (from [Runtime/Unity/Integrations](../../Runtime/Unity/Integrations/)) to your ProjectContext or scene installer list.
     - Drop [SampleInstaller.cs](./Zenject/SampleInstaller.cs) into your project and register it alongside other installers. When the scene runs, the installer resolves `IMessageRegistrationBuilder`, stages a `PlayerSpawned` listener, and activates via the Zenject lifecycle.
   - **VContainer**:
     - Define `VCONTAINER_PRESENT` and reference the optional extension under [VContainerRegistrationExtensions.cs](../../Runtime/Unity/Integrations/VContainer/VContainerRegistrationExtensions.cs).
     - Add [SampleLifetimeScope.cs](./VContainer/SampleLifetimeScope.cs) to the scene (or derive from it). The scope registers one `IMessageBus`, the builder resolves that scoped bus, and the entry point injects the same bus for one `ScoreUpdated` emission per second.
   - **Reflex**:
     - Install Reflex, then enable `REFLEX_PRESENT` for the imported sample assembly.
     - Attach [SampleInstaller.cs](./Reflex/SampleInstaller.cs) to a Reflex SceneScope or RootScope hierarchy. It registers the bus and `IMessageRegistrationBuilder`, then constructs the sample service when Reflex builds the container. The service subscribes to `PlayerAlert`; call the installer's `EmitAlertFor` method to emit one.

1. **Emit a message**  
   For Reflex, call `SampleInstaller.EmitAlertFor`. The VContainer entry point emits and consumes `ScoreUpdated` on its container-scoped bus at a bounded one-second interval. The Zenject sample focuses on registration lifetime; emit `PlayerSpawned` from a service that injects the same bus when adapting it.

These container examples do not require the sample prefab. Each registration builder and emitter must
resolve the same `IMessageBus` from its container scope.

## Separate provider and prefab walkthrough

The sample also includes a provider-driven Unity hierarchy that is independent of Zenject,
VContainer, and Reflex:

- [GlobalMessageBusProvider.asset](./Providers/GlobalMessageBusProvider.asset) resolves whichever
  bus is currently configured as global.
- [InitialGlobalMessageBusProvider.asset](./Providers/InitialGlobalMessageBusProvider.asset) always
  resolves the original startup global bus, ignoring later overrides.
- [MessagingInstallerSample.prefab](./Prefabs/MessagingInstallerSample.prefab) has a
  `MessagingComponentInstaller` configured with the current-global provider. At `Awake`, it applies
  that provider to its descendant `MessagingComponent`.

Drop the prefab into a scene to inspect provider-driven `MessagingComponent` configuration. The
prefab does not register `IMessageBus` or `IMessageRegistrationBuilder` in a DI container, and its
bare child `MessagingComponent` does not install gameplay listeners. To create service leases from
the prefab configuration, explicitly call `MessagingComponentInstaller.CreateRegistrationBuilder()`
and give the resulting builder to a service whose lifecycle owns and disposes its lease.

## Lifetime guarantees

Each container owns the registration lease it creates:

- Zenject and VContainer call `Dispose` on their managed services, which disposes the lease.
- Reflex removes its one-shot container-built callback as soon as it fires and again during
  `OnDestroy` as a defensive fallback. Rebuilding replaces and disposes the previous service.
- The VContainer tick performs bounded work. It does not emit or log every rendered frame.

When adapting these samples, keep the same ownership rule: the object that subscribes, schedules,
or creates a service must release it through the matching container or Unity lifecycle callback.
