# Glossary -- DxMessaging Terms Explained

[Back to Index](../getting-started/index.md) | [Getting Started](../getting-started/getting-started.md) | [Visual Guide](../getting-started/visual-guide.md)

---

**New to messaging systems?** This glossary explains key terms in plain English.

## Core Concepts

### Message

A **data structure** that represents something that happened or should happen. Think of it like a letter or announcement containing information.

Example: `Heal { amount: 10 }` is a message saying "heal for 10 points."

### Message Type

The **category** of message that determines who receives it:

- **Untargeted**: Everyone hears it (like a megaphone announcement)
- **Targeted**: One specific recipient gets it (like a letter to an address)
- **Broadcast**: Comes from one source, anyone can listen (like a news broadcast)

### Handler

A **function** that runs when a message is received. Your game logic goes here.

Example:

```csharp
void OnHeal(ref Heal msg) {
    health += msg.amount; // This is the handler
}
```

### Token

A **registration handle** that manages the lifecycle of your message handlers. It automatically enables/disables handlers when your component is active/inactive.

Think of it like a subscription card -- when you destroy it, all your subscriptions end automatically.

### MessageAwareComponent

A **Unity MonoBehaviour base class** that handles all the lifecycle management for you. Inherit from this and you get automatic setup/cleanup.

```csharp
public class MyComponent : MessageAwareComponent {
    // Token is created automatically!
}
```

## Message Pipeline

### Interceptor

Code that runs **before** handlers. Can **validate, modify, or cancel** messages before anyone else sees them.

Example: "Only allow an apply-damage request between 1 and 999"

```csharp
// Use the type-specific interceptor variant based on your message type:
// - RegisterUntargetedInterceptor<T> for IUntargetedMessage
// - RegisterTargetedInterceptor<T> for ITargetedMessage
// - RegisterBroadcastInterceptor<T> for IBroadcastMessage
_ = token.RegisterTargetedInterceptor<ApplyDamage>((ref InstanceId target, ref ApplyDamage msg) => {
    if (msg.amount <= 0) return false; // Cancel
    if (msg.amount > 999) msg = new ApplyDamage(999); // Clamp
    return true; // Allow
});
```

### Post-Processor

Code that runs **after** all handlers. Use it to record completed dispatch for logging,
analytics, metrics, or replay without changing gameplay state.

Example: "Count every processed damage message for telemetry"

```csharp
// Use the type-specific post-processor variant based on your message type:
// - RegisterUntargetedPostProcessor<T> for IUntargetedMessage
// - RegisterTargetedWithoutTargetingPostProcessor<T> for ITargetedMessage to any target
_ = token.RegisterTargetedWithoutTargetingPostProcessor<ApplyDamage>((ref InstanceId target, ref ApplyDamage msg) => {
    Analytics.RecordProcessedDamageRequest(target, msg.amount);
});
```

The message payload describes the request that completed dispatch. It does not prove how a
handler changed health or another gameplay value.

### Priority

A **number** that controls execution order. **Lower numbers run first**.

Example:

```csharp
_ = token.RegisterUntargeted<GameExit>(SaveGame, priority: 0);  // Runs first
_ = token.RegisterUntargeted<GameExit>(FadeAudio, priority: 5); // Runs second
_ = token.RegisterUntargeted<GameExit>(ShowUI, priority: 10);   // Runs third
```

## Unity Integration

### InstanceId

A **unique identifier** for a GameObject or Component. Used internally to route messages to the right place.

You rarely use this directly -- use the GameObject/Component helpers instead:

```csharp
msg.EmitGameObjectTargeted(gameObject); // Helper (use this)
// vs
msg.EmitTargeted(gameObject.GetInstanceID()); // Manual InstanceId (avoid)
```

### MessagingComponent

The **base Unity component** that provides messaging infrastructure. `MessageAwareComponent` inherits from this.

### Emit

To **send** a message. Like hitting "send" on an email.

```csharp
var heal = new Heal(10);
heal.EmitGameObjectTargeted(player); // Send it to one player
```

### Register

To **sign up** to receive messages. Like subscribing to a newsletter.

```csharp
_ = Token.RegisterGameObjectTargeted<Heal>(player, OnHeal); // Subscribe to this player
```

## Advanced Terms

### Message Bus

The **central routing system** that delivers messages. Think of it like a post office.

There's a global bus (`MessageHandler.MessageBus`) that most code uses, but you can create local buses for testing or isolation.

### Local Bus / Bus Island

A **separate message bus** used to isolate subsystems or tests. Messages sent to a local bus don't affect the global bus.

```csharp
var testBus = new MessageBus(); // Create island
var token = MessageRegistrationToken.Create(handler, testBus); // Use it
```

### Global Accept-All

A special handler that receives **every single message** regardless of type. Used for tools, debuggers, and analytics.

```csharp
_ = Token.RegisterGlobalAcceptAll(
    (ref IUntargetedMessage m) => Debug.Log("Untargeted: " + m),
    (ref InstanceId t, ref ITargetedMessage m) => Debug.Log("Targeted: " + m),
    (ref InstanceId s, ref IBroadcastMessage m) => Debug.Log("Broadcast: " + m)
);
```

### Diagnostics Mode

A **debug feature** that tracks emission history, registrations, routes, and
handler statistics. Enable it in the Editor, inspect emissions in
[Message Monitor](../guides/diagnostics.md#message-monitor), and inspect
loaded-scene `MessagingComponent` topology in
[Flow Graph](../guides/diagnostics.md#flow-graph). Direct bus/token registrations
outside those components require logs or counters. Disable diagnostics in
release builds when runtime diagnostics are not required.

```csharp
// DiagnosticsTarget.Editor turns diagnostics on in the Editor only, off in builds.
IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Editor;
```

## Memory Reclamation Terms

### Idle Eviction

Background reclamation that resets empty per-message-type and per-context
slots after they have stayed empty for `IdleEvictionSeconds` worth of bus
activity ticks. Active registrations are never touched. Gated by
`EvictionEnabled` and `EvictionTickIntervalSeconds`.

### Sweep

A single pass of the reclamation algorithm. A sweep walks dirty-tracked slots
and pool entries, resets the eligible empty ones, and updates the live
occupancy counters. Triggered by emit-time sampling, the Unity PlayerLoop
hook, or an explicit `Trim` call.

### Trim

The public API (`IMessageBus.Trim` / `MessageHandler.TrimAll`) that runs a
sweep synchronously. With `force: true` it ignores the idle threshold and
drains shared pools to zero; with `force: false` it uses the same idle
threshold as scheduled sweeps. Gated by `EnableTrimApi`.

### TrimResult

The struct returned by `Trim`. Its fields (`TypeSlotsEvicted`,
`TargetSlotsEvicted`, `PooledCollectionsEvicted`, `LiveTypeSlotsRemaining`)
report how much state the sweep reclaimed and how much remains live.

### Empty Slot

A typed-handler, interceptor, or context slot whose registrations have all
been deregistered. Empty slots stay around until reclamation runs because a
freshly empty slot is often about to be reused on the next dispatch.

## Attributes (Source Generation)

### [DxUntargetedMessage]

Marks a struct as an **Untargeted message** (global announcement).

### [DxTargetedMessage]

Marks a struct as a **Targeted message** (command to one recipient).

### [DxBroadcastMessage]

Marks a struct as a **Broadcast message** (event from a source).

### [DxAutoConstructor]

Auto-generates a **constructor** for your message struct so you don't have to write it manually.

```csharp
[DxTargetedMessage]
[DxAutoConstructor] // Generates: public Heal(int amount) { this.amount = amount; }
public readonly partial struct Heal {
    public readonly int amount;
}
```

## Common Patterns

### Lifecycle

The **creation and destruction** of components and their message registrations.

DxMessaging handles this automatically via `MessageAwareComponent`:

- `Awake()`: Token created, handlers registered
- `OnEnable()`: Token enabled, handlers active
- `OnDisable()`: Token disabled, handlers inactive
- `OnDestroy()`: Token destroyed, everything cleaned up

### Decoupling

Making systems **independent** so they don't need references to each other.

#### Before DxMessaging

```csharp
public class UI : MonoBehaviour {
    [SerializeField] Player player; // Tight coupling!
    [SerializeField] EnemySpawner spawner;
    [SerializeField] AudioManager audio;

    private void Refresh() {
        player.Refresh();
        spawner.Refresh();
        audio.Refresh();
    }
}
```

##### With DxMessaging

```csharp
public class UI : MessageAwareComponent {
    // No references needed! Just listen for messages.
    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        _ = Token.RegisterBroadcastWithoutSource<PlayerDamaged>(OnDamage);
    }
}
```

## Quick Reference Table

| Term               | One-Line Explanation                                 |
| ------------------ | ---------------------------------------------------- |
| **Message**        | Data saying "something happened" or "do something"   |
| **Handler**        | Function that runs when you receive a message        |
| **Token**          | Subscription manager (auto-cleanup)                  |
| **Interceptor**    | Guard that checks/modifies messages before delivery  |
| **Post-Processor** | Code that runs after all handlers (logging/metrics)  |
| **Priority**       | Number controlling execution order (lower = earlier) |
| **Emit**           | Send a message                                       |
| **Register**       | Subscribe to receive messages                        |
| **InstanceId**     | Unique ID for a GameObject or Component              |
| **Bus**            | Central message router (like a post office)          |

---

## Related Documentation

### Learn More

- to [Visual Guide](../getting-started/visual-guide.md) -- See these concepts visualized
- to [Getting Started](../getting-started/getting-started.md) -- Full introduction with examples
- to [Message Types](../concepts/message-types.md) -- When to use each type

#### Reference

- to [Quick Reference](quick-reference.md) -- API cheat sheet
- to [API Reference](reference.md) -- Complete API documentation

- to [Mini Combat sample](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Samples~/Mini%20Combat/README.md) -- See concepts in action
- to [Patterns](../guides/patterns.md) -- Real-world usage patterns
