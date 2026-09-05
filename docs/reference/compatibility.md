# Compatibility

DxMessaging is render-pipeline agnostic (pure C#) and targets Unity 2021.3+. The matrix below summarizes support by Unity version and Render Pipeline.

Unity Version vs Render Pipeline

| Unity      | Built-In RP | URP        | HDRP       |
| ---------- | ----------- | ---------- | ---------- |
| 2021.3 LTS | Compatible  | Compatible | Compatible |
| 2022.3 LTS | Compatible  | Compatible | Compatible |
| 2023.x     | Compatible  | Compatible | Compatible |
| 6.x        | Compatible  | Compatible | Compatible |

Notes

- RP-agnostic: DxMessaging does not depend on rendering APIs; it works equally across Built-In, URP, and HDRP.
- Minimum version is governed by the package manifest (`unity`: 2021.3). Newer LTS versions are expected to work.
- Unity 6 migrates object identity to `EntityId` and deprecates `Object.GetInstanceID()` (it becomes a compile error in Unity 6.5). DxMessaging handles this internally: on Unity 6.4+ the dispatch key reads the non-deprecated `EntityId.ToULong(...)` accessor (keeping the same 32-bit value), and older Unity keeps `GetInstanceID()`. The package keeps building across the supported range, including Unity 6.5+ where the old API is removed.

## Architecture Pattern Compatibility

### Scriptable Object Architecture (SOA)

DxMessaging can work alongside Scriptable Object Architecture patterns, though SOA has documented limitations. See [Pattern 14: SOA Compatibility](../guides/patterns.md#14-compatibility-with-scriptable-object-architecture-soa) for detailed integration strategies, code examples, and migration paths.

#### Quick summary

- [x] **Compatible** - DxMessaging can bridge with SOA systems
- **Not recommended** - SOA has scalability and maintainability concerns ([detailed critique](https://github.com/cathei/AntiScriptableObjectArchitecture))
- [x] **Best practice** - Use ScriptableObjects for immutable design data, DxMessaging for runtime events
- to See [SOA Integration Patterns](../guides/patterns.md#14-compatibility-with-scriptable-object-architecture-soa) for three coexistence strategies with code examples

### Dependency Injection (DI) Frameworks

DxMessaging integrates with popular DI frameworks:

- **Zenject** - See [Zenject Integration Guide](../integrations/zenject.md)
- **VContainer** - See [VContainer Integration Guide](../integrations/vcontainer.md)
- **Reflex** - See [Reflex Integration Guide](../integrations/reflex.md)

DI and DxMessaging complement each other: DI manages dependencies/services, DxMessaging handles event communication.

### Other Unity Frameworks

For comparisons with other messaging/event frameworks (UniRx, MessagePipe, Zenject Signals, etc.), see [Framework Comparisons](../architecture/comparisons.md).

## Untyped dispatch

Untyped dispatch accepts a message interface. A boxed struct is copied into a typed local;
interceptors can change that emission's local value, but the original box stays unchanged.
Class messages preserve object identity. Reuse a preboxed message when repeated untyped
emissions must avoid boxing allocations; normal typed struct dispatch needs no box.

On IL2CPP, a source-generated message includes its dispatch bridge. For a manually implemented
message, register a handler or interceptor for the concrete message type, or emit that type
through the typed API, before its first untyped emission. Registration on `MessageBus` is
sufficient; it does not require a separate typed warm-up. An untyped emission of a manual
message with no prepared bridge throws an exception that explains how to prepare it.

Custom `IMessageBus` implementations receive bridge calls through their own typed methods.
Their implementations remain responsible for making those generic methods available to
IL2CPP.

## Plain .NET runtime

The source checkout also builds the core runtime for .NET Standard 2.1, with C# 9 and
an explicit `System.Runtime.CompilerServices.Unsafe` 6.0.0 dependency. Build it with:

```bash
dotnet build .net/DxMessaging.csproj --configuration Release
dotnet test .net-tests/DxMessaging.Net.Tests.csproj --configuration Release
```

Reference `.net/DxMessaging.csproj` from a .NET application. This source-build target
is separate from UPM installation; the repository does not publish a NuGet package.
The runtime project compiles `Runtime/` without Unity symbols or Unity assemblies.
The tests run on .NET 9 and share the message-kind scenarios and leak watcher used by
Unity tests. The checked-in analyzer payload supplies runtime message generation. A project reference
does not forward analyzers: consumer projects using `[Dx*Message]` attributes must also
reference `Runtime/Analyzers/*.dll` as MSBuild `Analyzer` items. Implement the message
interfaces directly when consumer source generation is unnecessary.

Core bus, handler, token, typed/untyped dispatch, numeric `InstanceId`, diagnostic history,
and explicit trim APIs are available. `MonoBehaviour` components, GameObject routing,
reflexive GameObject messages, PlayerLoop integration, and `ScriptableObject` settings are
Unity-only. The settings provider type is absent outside Unity; buses use built-in defaults.
Call `DxMessagingStaticState.Reset()` when a host needs to reset shared static state.

Keep all bus activity, including trims and registration changes, on one owning thread.
There is no background sweep outside Unity. Host activity advances idle counters; force
trim at a maintenance boundary when an inactive bus needs immediate reclamation.
