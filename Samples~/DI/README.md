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
- Activating/deactivating the lease using the container lifecycle.

### Structure

- Zenject sample installer: [SampleInstaller.cs](./Zenject/SampleInstaller.cs)
- VContainer sample lifetime scope: [SampleLifetimeScope.cs](./VContainer/SampleLifetimeScope.cs)
- Reflex sample installer: [SampleInstaller.cs](./Reflex/SampleInstaller.cs)
- Current global message bus provider asset: [GlobalMessageBusProvider.asset](./Providers/GlobalMessageBusProvider.asset) -- ScriptableObject that resolves whichever bus is currently configured as global.
- Initial global message bus provider asset: [InitialGlobalMessageBusProvider.asset](./Providers/InitialGlobalMessageBusProvider.asset) -- ScriptableObject that always returns the original startup global bus, ignoring later overrides.
- Prefab setup: [MessagingInstallerSample.prefab](./Prefabs/MessagingInstallerSample.prefab) -- ready-to-use hierarchy with `MessagingComponentInstaller` configuring a child `MessagingComponent` using the provider asset. Drop it into a scene to see provider-driven wiring without writing setup code.

## Walkthrough

1. **Place the prefab**  
   Drag `Prefabs/MessagingInstallerSample.prefab` into your test scene. The root object carries `MessagingComponentInstaller` with its provider handle already pointing at the global provider ScriptableObject.

1. **Hook up the container**
   - **Zenject**:
     - Add `DxMessagingRegistrationInstaller` (from [Runtime/Unity/Integrations](../../Runtime/Unity/Integrations/)) to your ProjectContext or scene installer list.
     - Drop [SampleInstaller.cs](./Zenject/SampleInstaller.cs) into your project and register it alongside other installers. When the scene runs, the installer resolves `IMessageRegistrationBuilder`, stages a `PlayerSpawned` listener, and activates via the Zenject lifecycle.
   - **VContainer**:
     - Define `VCONTAINER_PRESENT` and reference the optional extension under [VContainerRegistrationExtensions.cs](../../Runtime/Unity/Integrations/VContainer/VContainerRegistrationExtensions.cs).
     - Add [SampleLifetimeScope.cs](./VContainer/SampleLifetimeScope.cs) to the scene (or derive from it); the sample scope registers the builder and an entry point that emits/consumes `ScoreUpdated` messages each tick.
   - **Reflex**:
     - Enable `REFLEX_PRESENT` and install `DxMessagingRegistrationInstaller` into your container bootstrap.
     - Include [SampleInstaller.cs](./Reflex/SampleInstaller.cs) in your installer chain. The sample service resolves `IMessageRegistrationBuilder`, subscribes to `PlayerAlert`, and can emit alerts via `EmitAlertFor`.

1. **Emit a message**  
   Use the service exposed by the container (e.g., call into `ScoreboardService` or `PlayerAlertService`) to emit a message. Because the prefab already configured `MessagingComponent` instances via the installer, the listeners run immediately.

1. **Swap providers** (optional)  
   Duplicate [GlobalMessageBusProvider.asset](./Providers/GlobalMessageBusProvider.asset), modify it to return a custom bus, assign it on the prefab root, and observe how builder-created leases now resolve that bus instead.

Feel free to duplicate these scripts into your own project and adjust lifecycles or message types as needed.
