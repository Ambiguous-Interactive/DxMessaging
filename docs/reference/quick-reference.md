# Quick Reference (Cheat Sheet)

Use this as a rapid guide to define/emit/listen and manage lifecycles.

Do's

- Use attributes + `DxAutoConstructor` for clarity (or interfaces on structs for perf).
- Bind struct messages to a variable before emitting.
- Use GameObject/Component emit helpers (no manual `InstanceId`).
- Register once; enable/disable with component state.
- Prefer named handler methods over inline lambdas for reuse and clarity.
- When using DI, inject `IMessageRegistrationBuilder` instead of newing `MessageHandler`s manually.

## Don'ts

- Don't emit from temporaries; use a local variable (e.g., `var msg = new M(...); msg.Emit();`).
- Don't mix Component vs GameObject targeting if you expect matches (see targeting notes below).
- Don't register in Update; use `Awake` for staging + `OnEnable`/`OnDisable` for lifecycle.
- Don't forget base calls when inheriting from `MessageAwareComponent` -- call `base.RegisterMessageHandlers()` and `base.OnEnable()`/`base.OnDisable()`.
- Don't hide Unity methods with `new` (e.g., `new void OnEnable()`); prefer `override` and call `base.*`.

## Define messages

```csharp
using DxMessaging.Core.Attributes;

[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct SceneLoaded { public readonly int buildIndex; }

[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct Heal { public readonly int amount; }

[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct TookDamage { public readonly int amount; }
```

## Emit (Unity helpers)

```csharp
using DxMessaging.Core.Extensions;

var scene = new SceneLoaded(1); scene.Emit();
var heal  = new Heal(10);       heal.EmitGameObjectTargeted(gameObject);
var hit   = new TookDamage(5);  hit.EmitComponentBroadcast(this);

// String shorthands
"Saved".Emit();                   // GlobalStringMessage
"Hello".EmitAt(gameObject);       // StringMessage to GO (or .Emit(instanceId))
"Hit".EmitFrom(gameObject);       // SourcedStringMessage from GO
```

## Register (Unity, via token)

```csharp
using DxMessaging.Core; // InstanceId
// Untargeted
_ = token.RegisterUntargeted<SceneLoaded>(OnSceneLoaded);
void OnSceneLoaded(ref SceneLoaded m) { /* ... */ }

// Targeted: to this component or gameObject
_ = token.RegisterComponentTargeted<Heal>(this, OnHeal);
_ = token.RegisterGameObjectTargeted<Heal>(gameObject, OnHeal);
void OnHeal(ref Heal m) { /* ... */ }

// Broadcast: from this component or gameObject
_ = token.RegisterComponentBroadcast<TookDamage>(this, OnDamageFromMe);
_ = token.RegisterGameObjectBroadcast<TookDamage>(gameObject, OnDamageFromMe);
void OnDamageFromMe(ref TookDamage m) { /* ... */ }

// Listen to all targets/sources
_ = token.RegisterTargetedWithoutTargeting<Heal>(OnAnyHeal);
void OnAnyHeal(ref InstanceId target, ref Heal m) { /* ... */ }

_ = token.RegisterBroadcastWithoutSource<TookDamage>(OnAnyDamage);
void OnAnyDamage(ref InstanceId src, ref TookDamage m) { /* ... */ }
```

## Register (DI / services)

```csharp
using DxMessaging.Core.MessageBus;

public sealed class DamageSystem : IStartable, IDisposable
{
    private readonly MessageRegistrationLease lease;

    public DamageSystem(IMessageRegistrationBuilder registrationBuilder)
    {
        lease = registrationBuilder.Build(new MessageRegistrationBuildOptions
        {
            Configure = token =>
            {
                _ = token.RegisterUntargeted<TookDamage>(OnDamage);
            }
        });
    }

    public void Start() => lease.Activate();

    public void Dispose() => lease.Dispose();

    private static void OnDamage(ref TookDamage message) { /* respond */ }
}
```

Tip: Define `ZENJECT_PRESENT`, `VCONTAINER_PRESENT`, or `REFLEX_PRESENT` to enable the optional shims under [Runtime/Unity/Integrations](https://github.com/Ambiguous-Interactive/DxMessaging/tree/master/Runtime/Unity/Integrations) that bind the builder automatically for those containers.

## Interceptors and post-processors

```csharp
using DxMessaging.Core;            // MessageHandler
using DxMessaging.Core.MessageBus; // IMessageBus

var bus = MessageHandler.MessageBus;
_ = bus.RegisterBroadcastInterceptor<TookDamage>((ref InstanceId src, ref TookDamage m) =>
{
    if (m.amount <= 0) return false; // cancel
    m = new TookDamage(Math.Min(m.amount, 999));
    return true;
});

_ = token.RegisterUntargetedPostProcessor<SceneLoaded>((ref SceneLoaded m) => LogScene(m.buildIndex));
```

## Lifecycle

```csharp
void Awake()     { /* stage registrations */ }
void OnEnable()  { token.Enable(); }
void OnDisable() { token.Disable(); }
```

## Inheritance tip (MessageAwareComponent)

- If you override `RegisterMessageHandlers`, start with `base.RegisterMessageHandlers()`.
- If you override Unity lifecycle methods, call `base.OnEnable()` / `base.OnDisable()` (and `base.Awake()`/`base.OnDestroy()` if overridden).

## Targeting notes (Component vs GameObject)

- A targeted message matches if the emitted `InstanceId` equals the registered `InstanceId`.
- Registering for a Component target listens for messages targeted at that specific Component.
- Registering for a GameObject target listens for messages targeted at that GameObject.
- Emitting to a GameObject will not reach Component-targeted listeners (and vice-versa). Use the matching helper.
- Shorthands exist for strings too; be explicit about using a GameObject vs Component with `EmitAt`/`EmitFrom`.

## Memory Reclamation

| API                             | Purpose                                                                           |
| ------------------------------- | --------------------------------------------------------------------------------- |
| `bus.Trim(bool force = false)`  | Reclaim empty slots and pooled collections on a single bus; returns `TrimResult`. |
| `MessageHandler.TrimAll(force)` | Convenience wrapper that calls `Trim` on the global bus.                          |
| `bus.OccupiedTypeSlots`         | Count of distinct per-message-type slots currently occupied on the bus.           |
| `bus.OccupiedTargetSlots`       | Count of distinct (type, target) context tuples currently occupied on the bus.    |

For tuning, scenario tables, and a leak-watching pattern see the
[Memory Reclamation guide](../guides/memory-reclamation.md). For the asset
parameters and defaults see the
[Runtime Settings reference](runtime-settings.md).

## See also

- [Emit Shorthands](../advanced/emit-shorthands.md)
- [Advanced](../guides/advanced.md)
- [Targeting & Context](../concepts/targeting-and-context.md)
- [Interceptors & Ordering](../concepts/interceptors-and-ordering.md)
- [Memory Reclamation](../guides/memory-reclamation.md)
- [Runtime Settings](runtime-settings.md)

## Execution Order

### Untargeted

```text
Interceptors -> Global Accept-All -> Handlers<T> -> Post-Processors<T>
```

### Targeted

```text
Interceptors -> Global Accept-All -> Handlers<T> @ target
    -> Handlers<T> (All Targets) -> Post-Processors<T> @ target
    -> Post-Processors<T> (All Targets)
```

### Broadcast

```text
Interceptors -> Global Accept-All -> Handlers<T> @ source
    -> Handlers<T> (All Sources) -> Post-Processors<T> @ source
    -> Post-Processors<T> (All Sources)
```

> 📝 **Note: Priority Rules**
>
> - Lower priority values run earlier
> - Same priority preserves registration order
> - Within a priority, fast (by-ref) handlers run before action handlers

## API Quick Reference

### Token: Untargeted

```csharp
// Choose either the Action or by-ref overload.
_ = token.RegisterUntargeted<SceneLoaded>(OnSceneLoaded, priority: 0);
_ = token.RegisterUntargeted<SceneLoaded>(OnSceneLoadedFast, priority: 0);

// Post-processor
_ = token.RegisterUntargetedPostProcessor<SceneLoaded>(AfterSceneLoaded, priority: 0);

void OnSceneLoaded(SceneLoaded message) => Debug.Log(message.buildIndex);
void OnSceneLoadedFast(ref SceneLoaded message) => Debug.Log(message.buildIndex);
void AfterSceneLoaded(ref SceneLoaded message) => Debug.Log(message.buildIndex);
```

### Token: Targeted (Specific)

```csharp
_ = token.RegisterGameObjectTargeted<Heal>(gameObject, OnHeal, priority: 0);
_ = token.RegisterComponentTargeted<Heal>(this, OnHeal, priority: 0);
_ = token.RegisterTargeted<Heal>(targetInstanceId, OnHeal, priority: 0);

// Post-processor
_ = token.RegisterTargetedPostProcessor<Heal>(targetInstanceId, AfterHeal, priority: 0);

void OnHeal(ref Heal message) => Debug.Log(message.amount);
void AfterHeal(ref Heal message) => Debug.Log(message.amount);
```

### Token: Targeted (All Targets)

```csharp
// Listen to messages for any target
_ = token.RegisterTargetedWithoutTargeting<Heal>(OnAnyHeal, priority: 0);

// Post-processor
_ = token.RegisterTargetedWithoutTargetingPostProcessor<Heal>(AfterAnyHeal, priority: 0);

void OnAnyHeal(ref InstanceId target, ref Heal message) =>
    Debug.Log($"Healed {target} for {message.amount}");
void AfterAnyHeal(ref InstanceId target, ref Heal message) =>
    Debug.Log($"Finished healing {target} for {message.amount}");
```

### Token: Broadcast (Specific)

```csharp
_ = token.RegisterGameObjectBroadcast<TookDamage>(gameObject, OnDamage, priority: 0);
_ = token.RegisterComponentBroadcast<TookDamage>(this, OnDamage, priority: 0);
_ = token.RegisterBroadcast<TookDamage>(sourceInstanceId, OnDamage, priority: 0);

// Post-processor
_ = token.RegisterBroadcastPostProcessor<TookDamage>(sourceInstanceId, AfterDamage, priority: 0);

void OnDamage(ref TookDamage message) => Debug.Log(message.amount);
void AfterDamage(ref TookDamage message) => Debug.Log(message.amount);
```

### Token: Broadcast (All Sources)

```csharp
// Listen to broadcasts from any source
_ = token.RegisterBroadcastWithoutSource<TookDamage>(OnAnyDamage, priority: 0);

// Post-processor
_ = token.RegisterBroadcastWithoutSourcePostProcessor<TookDamage>(AfterAnyDamage, priority: 0);

void OnAnyDamage(ref InstanceId source, ref TookDamage message) =>
    Debug.Log($"{source} dealt {message.amount} damage");
void AfterAnyDamage(ref InstanceId source, ref TookDamage message) =>
    Debug.Log($"Finished damage from {source}: {message.amount}");
```

### Token: Global Observer

```csharp
_ = token.RegisterGlobalAcceptAll(
    message => Debug.Log(message.MessageType),
    (target, message) => Debug.Log($"{message.MessageType} to {target}"),
    (source, message) => Debug.Log($"{message.MessageType} from {source}")
);

// Fast handler-based
_ = token.RegisterGlobalAcceptAll(
    (ref IUntargetedMessage message) => Debug.Log(message.MessageType),
    (ref InstanceId target, ref ITargetedMessage message) =>
        Debug.Log($"{message.MessageType} to {target}"),
    (ref InstanceId source, ref IBroadcastMessage message) =>
        Debug.Log($"{message.MessageType} from {source}")
);
```

### Bus: Interceptors

```csharp
_ = bus.RegisterUntargetedInterceptor<SceneLoaded>(
    (ref SceneLoaded message) => message.buildIndex >= 0,
    priority: 0
);
_ = bus.RegisterTargetedInterceptor<Heal>(
    (ref InstanceId target, ref Heal message) => message.amount > 0,
    priority: 0
);
_ = bus.RegisterBroadcastInterceptor<TookDamage>(
    (ref InstanceId source, ref TookDamage message) => message.amount > 0,
    priority: 0
);

// Bus-level global observer
_ = bus.RegisterGlobalAcceptAll(messageHandler);
```
