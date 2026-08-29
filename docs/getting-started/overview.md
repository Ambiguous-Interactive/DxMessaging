# Overview

[Back to Index](index.md) | [Getting Started](getting-started.md) | [Quick Start](quick-start.md) | [Visual Guide](visual-guide.md)

---

DxMessaging is a type-safe messaging system for Unity that **addresses three common pain points** of traditional event systems:

1. **Memory leaks** from forgotten unsubscribes -> Automatic lifecycle management
1. **Tight coupling** creating refactoring nightmares -> Full decoupling with no direct references
1. **Debugging black holes** ("what fired when?") -> Message Monitor, Flow Graph, and Inspector diagnostics

## What Problems Does It Solve?

### For Beginners: "I'm calling methods manually everywhere"

#### Traditional approaches often look like this

```csharp
public class Player : MonoBehaviour {
    public HealthBar healthBar;
    public AudioManager audio;
    public AchievementSystem achievements;

    void TakeDamage(int amount) {
        health -= amount;
        healthBar.UpdateHealth(health);  // Manual call
        audio.PlayDamageSound();         // Manual call
        achievements.CheckDamage(amount); // Manual call
        // Add analytics? More manual calls...
    }
}
```

**Every new system = another SerializeField + another manual call.** This approach requires frequent updates as systems are added.

##### DxMessaging fixes this

```csharp
void TakeDamage(int amount) {
    health -= amount;
    var damage = new TookDamage(amount);
    damage.EmitComponentBroadcast(this);
    // Subscribed systems react to this message.
}
```

### For Intermediate Devs: "I use C# events but they leak"

#### You know this pain

```csharp
void OnEnable() { GameManager.OnScoreChanged += UpdateUI; }
void OnDisable() { /* Missing this results in a memory leak: */ }
```

DxMessaging manages subscription lifecycle automatically, eliminating the need for manual unsubscribe - cleanup occurs when components are destroyed.

### For Advanced Devs: "I need observability and control"

- Inspect emission history and call sites in Message Monitor
- Inspect loaded-scene `MessagingComponent` topology and delivery evidence in
  Flow Graph
- Catch lifecycle mistakes and provider warnings in the Inspector
- Priority-based execution (deterministic ordering)
- Interceptors (validate/normalize before handlers)
- Global observers (track ALL instances of a message type)
- Local bus islands (isolated testing, zero global state)

## What It Solves (Technical)

- **Decoupling without references** - producers/consumers never know about each other
- **Predictable lifecycle** - explicit tokens tied to Unity component lifecycles
- **Performance** - struct messages passed by-ref, designed to minimize allocations and boxing
- **Observability** - interceptors, post-processors, diagnostics, registration logs
- **Scalable taxonomy** - three message types (Untargeted/Targeted/Broadcast) cover most common messaging patterns

## When to Consider DxMessaging

### Use it when

- You have 3+ systems that need to communicate
- You've debugged memory leaks from forgotten unsubscribes
- You're tired of UI depending on 15 different gameplay systems
- You want to see "what fired when" without setting 50 breakpoints
- You're building for the long term (months/years, not days)

#### Skip it when

- Game jam prototype (<1 week)
- Tiny project (<1000 lines)
- Single-system communication (call the method directly)
- You need synchronous return values (DxMessaging is fire-and-forget)

Core ideas

- Message categories
  - Untargeted: Global notifications (e.g., settings changed).
  - Targeted: Sent to one specific target (e.g., Heal(10) to Player).
  - Broadcast: Emitted from a source; anyone can observe (e.g., TookDamage from Enemy).
- Ordering: Lower priority runs earlier; same priority uses registration order.
- Pipeline: Interceptors -> Handlers -> Post-Processors, with diagnostics optionally enabled.
- Unity integration: `MessagingComponent` and `MessageAwareComponent` manage subscription lifecycles automatically.

## Key Features

### Global Observers: Observing All Instances

#### Traditional event systems force you to do this

```csharp
// Subscribe to each entity type separately
PlayerEvents.OnDamaged += TrackPlayerDamage;
EnemyEvents.OnDamaged += TrackEnemyDamage;
NPCEvents.OnDamaged += TrackNPCDamage;
// Add a new entity type? More subscriptions...
```

##### DxMessaging lets you do this

```csharp
// Subscribe ONCE to ALL damage, regardless of source
_ = Token.RegisterBroadcastWithoutSource<TookDamage>(
    (InstanceId source, TookDamage msg) => {
        Analytics.Log($"{source} took {msg.amount} damage");
    }
);
```

**Example use cases:** Achievement systems, combat logs, and analytics that work with any entity - including ones added later.

### Other Features

- **Priority-based ordering** - control execution flow explicitly
- **Interceptor pipeline** - validate/normalize messages BEFORE handlers run (one validation, all handlers protected)
- **Local bus islands** - isolated testing with zero global state contamination
- **Low-allocation design** - struct messages passed by-ref, minimizes boxing and GC pressure
- **Auto-constructor generation** - `[DxAutoConstructor]` eliminates boilerplate while keeping type safety
- **Unity-first helpers** - `EmitGameObjectTargeted()`, `EmitComponentBroadcast()` feel natural
- **Editor diagnostics** - inspect emissions in Message Monitor, routes in Flow
  Graph, and component warnings in the Inspector

---

## Related Documentation

### Start Here

- -> [Mental Model](../concepts/mental-model.md) (10 min) - Philosophy and first principles
- -> [Visual Guide](visual-guide.md) (5 min) - Beginner-friendly introduction
- -> [Getting Started](getting-started.md) (10 min) - Complete guide
- -> [Quick Start](quick-start.md) (5 min) - Working example

#### Go Deeper

- -> [Message Types](../concepts/message-types.md) - When to use Untargeted/Targeted/Broadcast
- -> [Comparisons](../architecture/comparisons.md) - DxMessaging vs alternatives
- -> [Design & Architecture](../architecture/design-and-architecture.md) - How it works

##### Install & Setup

- -> [Install](install.md) - Installation guide
- -> [Compatibility](../reference/compatibility.md) - Unity versions
