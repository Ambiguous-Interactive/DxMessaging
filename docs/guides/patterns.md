# DxMessaging Patterns: Real-World Solutions

[Back to Index](../getting-started/index.md) | [Getting Started](../getting-started/getting-started.md) | [Message Types](../concepts/message-types.md) | [Samples](https://github.com/Ambiguous-Interactive/DxMessaging/tree/master/Samples~)

---

**You're here because:** You understand DxMessaging basics, now you want to see "How do I actually build X?"

## What you'll find

- **Basic Patterns** - Fundamental building blocks (scene transitions, commands, observability)
- **Advanced Patterns** - Power user techniques (diagnostics, testing, legacy integration)
- **Scale Patterns** - Examples for larger systems (100+ entities, cross-scene systems, large UI)

### Reading guide

- **New to DxMessaging?** Start with Basic Patterns 1-8
- **Intermediate user?** Jump to Advanced Patterns 9-12
- **Building at scale?** Go straight to Real-World Scale Patterns
- **Specific problem?** Use Ctrl+F / Cmd+F to search

**Philosophy:** These patterns address common challenges teams face when building messaging systems.

---

## Quick Links: "I Want To..."

### Find your use case, jump to the pattern

| I want to...                                 | Go to                                                                                                        |
| -------------------------------------------- | ------------------------------------------------------------------------------------------------------------ |
| Make UI react to gameplay                    | [Pattern 2: Directed Commands](#2-directed-commands-targeted)                                                |
| Coordinate scene transitions                 | [Pattern 1: Scene-wide Events](#1-scene-wide-events-untargeted)                                              |
| Build an achievement system                  | [Pattern 3: Observability](#3-observability-broadcast) + [Global Accept-All](#10-global-accept-all-handlers) |
| Validate input/damage before it happens      | [Pattern 4: Interceptors](#4-validation-and-normalization-interceptors)                                      |
| Add analytics without touching gameplay      | [Pattern 5: Post-Processors](#5-analyticslogging-post-processors)                                            |
| Track ALL damage from ANY entity             | [Pattern: Managing 100+ Entities](#pattern-managing-100-combat-entities)                                     |
| Build a large UI system (20+ panels)         | [Pattern: Large-Scale UI](#pattern-large-scale-ui-system-20-panels)                                          |
| Make systems run in a specific order         | [Pattern: Priority Ordering](#pattern-priority-ordered-execution-for-complex-systems)                        |
| Test in isolation                            | [Pattern 6: Local Bus Islands](#6-local-bus-islands)                                                         |
| Migrate from C# events                       | [Pattern 9: Bridging Legacy](#9-bridging-legacy-unity-messaging)                                             |
| Handle persistent systems across scene loads | [Pattern: Cross-Scene Persistent](#pattern-cross-scene-persistent-systems)                                   |
| See what's happening (debug message flow)    | [Pattern 11: Diagnostics](#11-diagnostics-and-tuning)                                                        |
| Build a battle royale / large multiplayer    | [Pattern: Battle Royale Example](#example-battle-royale-ui)                                                  |
| Use with Scriptable Object Architecture      | [Pattern 14: SOA Compatibility](#14-compatibility-with-scriptable-object-architecture-soa)                   |

---

## Table of Contents

### Basic Patterns

- [Scene-wide Events (Untargeted)](#1-scene-wide-events-untargeted)
- [Directed Commands (Targeted)](#2-directed-commands-targeted)
- [Observability (Broadcast)](#3-observability-broadcast)
- [Validation and Normalization (Interceptors)](#4-validation-and-normalization-interceptors)
- [Analytics and Logging (Post-Processors)](#5-analyticslogging-post-processors)
- [Local Bus Islands](#6-local-bus-islands)
- [Lifecycle Pattern in Unity](#7-lifecycle-pattern-in-unity)
- [Cross-Scene Messaging](#8-cross-scene-messaging)

### Advanced Patterns

- [Bridging Legacy Unity Messaging](#9-bridging-legacy-unity-messaging)
- [Global Accept-All Handlers](#10-global-accept-all-handlers)
- [Diagnostics and Tuning](#11-diagnostics-and-tuning)
- [Testing](#12-testing)
- [Compatibility with Scriptable Object Architecture (SOA)](#14-compatibility-with-scriptable-object-architecture-soa)

### Real-World Scale Patterns

- [Managing 100+ Combat Entities](#pattern-managing-100-combat-entities)
- [Cross-Scene Persistent Systems](#pattern-cross-scene-persistent-systems)
- [Large-Scale UI System (20+ Panels)](#pattern-large-scale-ui-system-20-panels)
- [Priority-Ordered Execution](#pattern-priority-ordered-execution-for-complex-systems)
- [Efficient Interception at Scale](#pattern-efficient-interception-at-scale)
- [Post-Processing for Analytics at Scale](#pattern-post-processing-for-analytics-at-scale)
- [Performance Optimization Patterns](#performance-optimization-patterns-at-scale)
- [Production Example: Battle Royale Game](#example-battle-royale-ui)

---

### Important: Inheritance with MessageAwareComponent

- Many examples derive from `MessageAwareComponent`. **When overriding hooks, you MUST call the base method.**
- **Always call `base.RegisterMessageHandlers()` FIRST** in your override to preserve parent class registrations.
- **CRITICAL**: Call `base.OnEnable()` / `base.OnDisable()` if you override lifecycle methods; otherwise your token may never enable/disable.
- **CRITICAL**: Call `base.Awake()` if you override `Awake()`; otherwise your token won't be created.
- To opt in to the built-in string demos, override `RegisterForStringMessages => true`.
- **Don't use `new` to hide methods** (e.g., `new void OnEnable()`); always use `override` and call `base.*`.

Registration timing (pit of success)

- **Prefer `Awake()` for all message handler registration** -- this is when `MessageAwareComponent` calls `RegisterMessageHandlers()`.
- Avoid registering in `Start()` unless you have a specific order-of-execution reason.
- Early registration in `Awake()` ensures your handlers are ready before other components' `Start()` methods run.

## 1) Scene-wide Events (Untargeted)

Use untargeted messages for global state changes that any system might care about. Keep messages small and immutable.

```csharp
using DxMessaging.Core.Messages;
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Extensions;

[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct SceneLoaded
{
    public readonly int buildIndex;
}

// Sender
var evt = new SceneLoaded(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
evt.Emit();

// Listener
_ = token.RegisterUntargeted<SceneLoaded>(OnSceneLoaded);
void OnSceneLoaded(in SceneLoaded m) => RefreshUI();
```

## 2) Directed Commands (Targeted)

Target a specific `GameObject`/`Component` when you want direct control.

```csharp
using DxMessaging.Core.Messages;
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Extensions;

[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct Heal { public readonly int amount; }

// Sender
var heal = new Heal(10);
heal.EmitComponentTargeted(this);

// Listener (on the hero)
_ = token.RegisterComponentTargeted<Heal>(this, OnHeal);
void OnHeal(in Heal m) => ApplyHeal(m.amount);
```

## 3) Observability (Broadcast)

Broadcast from a source; any listener can observe.

```csharp
using DxMessaging.Core.Messages;
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Extensions;
using DxMessaging.Core; // InstanceId

[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct TookDamage { public readonly int amount; }

// Sender
var hit = new TookDamage(5);
hit.EmitGameObjectBroadcast(gameObject);

// Listener: observe every source
_ = token.RegisterBroadcastWithoutSource<TookDamage>(OnAnyTookDamage);
void OnAnyTookDamage(InstanceId src, TookDamage m) => Log(src, m);
```

## 4) Validation and Normalization (Interceptors)

Use interceptors to enforce rules before handlers run.

```csharp
using DxMessaging.Core; // MessageHandler

var bus = MessageHandler.MessageBus;
_ = token.RegisterTargetedInterceptor<ApplyDamage>((ref InstanceId target, ref ApplyDamage m) =>
{
    if (m.amount <= 0) return false; // cancel
    m = new ApplyDamage(Math.Min(m.amount, 999)); // clamp
    return true;
}, priority: 0);
```

## 5) Analytics/Logging (Post-Processors)

Post-processors run after handlers -- ideal for metrics.

```csharp
_ = token.RegisterUntargetedPostProcessor<SceneLoaded>(
    (in SceneLoaded m) => Metrics.RecordProcessedSceneLoad(m.buildIndex));
```

## 6) Local Bus Islands

`MessageHandler.MessageBus` is the global bus. For isolation, use your own `MessageBus` instance and pass it to the token factory.

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;

var localBus = new MessageBus();
var token = MessageRegistrationToken.Create(handler, localBus);
```

## 7) Lifecycle Pattern in Unity

- **Stage registrations in `Awake()`** (preferred) or `Start()` (only if order-dependent).
- Call `token.Enable()` in `OnEnable` and `token.Disable()` in `OnDisable`.
- Use `MessageAwareComponent` to avoid boilerplate -- it handles all of this automatically.

Why Awake over Start?

- `Awake()` runs before any `Start()` methods, ensuring your handlers are ready early.
- Other components may emit messages in their `Start()` methods -- registering in `Awake()` ensures you don't miss them.
- `MessageAwareComponent` automatically calls `RegisterMessageHandlers()` in `Awake()`, following this best practice.

Side-by-side: lifecycle

Before

```csharp
void Awake() { /* register */ }
void OnDestroy() { /* maybe unregister (often forgotten) */ }
```

After (token)

```csharp
void Awake()     { /* stage registrations - PREFERRED */ }
void OnEnable()  { token.Enable(); }
void OnDisable() { token.Disable(); }
```

## 8) Cross-Scene Messaging

Untargeted messages flow anywhere; targeted/broadcast require a valid `InstanceId`. For cross-scene, ensure the target/source object exists when you emit.

## 9) Bridging Legacy Unity Messaging

`ReflexiveMessage` mirrors `SendMessage*` patterns but keeps you in the bus pipeline.

> **Fixed in v4.0.0:** Combine `Flat`, `Upwards`, and `Downwards` to traverse more than one
> direction. Sends include disabled components and inactive GameObjects unless you also set
> `OnlyIncludeActive`. A missing receiver is silent for every argument count. Nested reflexive
> sends preserve the outer traversal, and receivers destroyed during traversal are skipped. Active
> single-direction sends with zero or one argument keep Unity's native per-GameObject behavior, so a
> reset from one receiver completes that GameObject before traversal stops.

```csharp
using DxMessaging.Core;
using DxMessaging.Core.Messages;

var msg = new ReflexiveMessage("OnHit", ReflexiveSendMode.Upwards, 10);
msg.EmitGameObjectTargeted(someGameObject);
```

## 10) Global Accept-All Handlers

Use when building tools or inspectors that want to observe everything.

```csharp
_ = token.RegisterGlobalAcceptAll(
    (in IUntargetedMessage m) => Debug.Log($"Untargeted {m.MessageType}"),
    (in InstanceId target, in ITargetedMessage m) => Debug.Log($"Targeted {m.MessageType} to {target}"),
    (in InstanceId source, in IBroadcastMessage m) => Debug.Log($"Broadcast {m.MessageType} from {source}")
);
```

Do's

- Use global accept-all in tooling and debug inspectors.
- Prefer specific registrations for gameplay code to avoid surprises.

## 11) Diagnostics and Tuning

- Enable `IMessageBus.GlobalDiagnosticsTargets` (e.g. `DiagnosticsTarget.Editor`) or per-token.
- Adjust `IMessageBus.GlobalMessageBufferSize` for deeper history (Editor settings UI provided).
- Wire `MessagingDebug.LogFunction` to Unity's console to see warnings/errors.

## 12) Testing

- Construct messages directly and emit via extension helpers.
- Use a local `MessageBus` per test to avoid global state.
- Wrap handlers to increment counters or assert payloads.

## 13) Real-World Scale: Beyond Toy Examples

Most documentation shows simple, small-scale examples. Here's how DxMessaging behaves at production scale.

### Pattern: Managing 100+ Combat Entities

**Challenge:** Track damage, healing, status effects for a large battlefield without performance collapse.

```csharp
using DxMessaging.Core.Messages;
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Extensions;
using DxMessaging.Core;

// Messages
[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct EntityDamaged {
    public readonly int amount;
    public readonly string damageType;
}

[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct EntityHealed { public readonly int amount; }

// Entity: Each of 100+ entities emits events
public class CombatEntity : MessageAwareComponent {
    public void TakeDamage(int amount, string type) {
        health -= amount;
        var msg = new EntityDamaged(amount, type);
        msg.EmitGameObjectBroadcast(gameObject);
    }
}

// Combat UI: Tracks specific enemy (targeted observation)
public class EnemyHealthBar : MessageAwareComponent {
    [SerializeField] private GameObject trackedEnemy;

    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        // Only listens to ONE enemy, not all 100
        _ = Token.RegisterGameObjectBroadcast<EntityDamaged>(trackedEnemy, OnDamaged);
    }

    void OnDamaged(in EntityDamaged msg) => UpdateBar();
}

// Analytics: Observes ALL entities (global observation)
public class CombatAnalytics : MessageAwareComponent {
    private readonly Dictionary<InstanceId, int> totalDamageByEntity = new();

    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        // Listens to all 100+ entities with ONE registration
        _ = Token.RegisterBroadcastWithoutSource<EntityDamaged>(OnAnyDamage);
    }

    void OnAnyDamage(InstanceId source, EntityDamaged msg) {
        if (!totalDamageByEntity.ContainsKey(source))
            totalDamageByEntity[source] = 0;
        totalDamageByEntity[source] += msg.amount;
    }
}
```

#### Scale characteristics

- [x] Each entity broadcasts ~10-50 messages/second -> No GC allocations (struct messages)
- [x] Targeted listeners (health bars) only receive relevant messages -> O(1) lookup
- [x] Global listeners (analytics) receive all messages -> Single handler, not N handlers
- [x] Adding/removing entities doesn't break registrations (no manual wiring)

##### Performance notes

- Disable diagnostics in production (`IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Off`)
- Use `RegisterBroadcastWithoutSource` sparingly (it's called for every emit)
- Profile with Unity Profiler to find hotspots

### Pattern: Cross-Scene Persistent Systems

**Challenge:** Maintain event flow across scene loads (achievements, save system, analytics).

```csharp
using UnityEngine;
using DxMessaging.Unity;
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Extensions;

// Persistent singleton that survives scene loads
public class PersistentAchievementSystem : MessageAwareComponent {
    private static PersistentAchievementSystem instance;

    protected override void Awake() {
        base.Awake();
        if (instance != null) {
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        // Listen to ALL broadcasts from ANY scene
        _ = Token.RegisterBroadcastWithoutSource<EntityDamaged>(OnAnyDamage);
        _ = Token.RegisterUntargeted<LevelCompleted>(OnLevelComplete);
    }

    void OnAnyDamage(InstanceId src, EntityDamaged msg) {
        totalDamage += msg.amount;
        CheckAchievements();
    }

    void OnLevelComplete(in LevelCompleted msg) {
        UnlockAchievement($"Complete_Level_{msg.levelIndex}");
    }
}

// Scene-specific objects emit messages normally
public class Boss : MessageAwareComponent {
    void Die() {
        var msg = new BossDefeated(bossName);
        msg.EmitGameObjectBroadcast(gameObject);
        // PersistentAchievementSystem hears this even though it's in DontDestroyOnLoad
    }
}
```

**Key insight:** Persistent listeners (`DontDestroyOnLoad`) stay registered across scenes. Scene-specific emitters come/go, but the observer remains.

**Gotcha:** If you emit `Targeted` or `Broadcast` messages, ensure the target/source `InstanceId` is still valid after scene loads. For cross-scene communication, prefer `Untargeted` messages.

### Pattern: Large-Scale UI System (20+ Panels)

**Challenge:** Coordinate 20+ UI panels reacting to game state without tight coupling.

```csharp
// Player-owned state snapshots are broadcasts from that player.
[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct PlayerStatsChanged {
    public readonly int health;
    public readonly int mana;
    public readonly int gold;
}

[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct InventoryChanged { public readonly int itemCount; }

// Each UI panel listens independently
public class HealthPanel : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        _ = Token.RegisterBroadcastWithoutSource<PlayerStatsChanged>(OnStats);
    }
    void OnStats(InstanceId player, PlayerStatsChanged msg) => UpdateHealth(player, msg.health);
}

public class ManaPanel : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        _ = Token.RegisterBroadcastWithoutSource<PlayerStatsChanged>(OnStats);
    }
    void OnStats(InstanceId player, PlayerStatsChanged msg) => UpdateMana(player, msg.mana);
}

public class GoldPanel : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        _ = Token.RegisterBroadcastWithoutSource<PlayerStatsChanged>(OnStats);
    }
    void OnStats(InstanceId player, PlayerStatsChanged msg) => UpdateGold(player, msg.gold);
}

public class InventoryPanel : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        _ = Token.RegisterBroadcastWithoutSource<InventoryChanged>(OnInventory);
    }
    void OnInventory(InstanceId player, InventoryChanged msg) => Refresh(player);
}

// Game systems emit once, all panels update
public class PlayerStats : MonoBehaviour {
    void UpdateStats() {
        var msg = new PlayerStatsChanged(health, mana, gold);
        msg.EmitGameObjectBroadcast(gameObject); // All 3 panels update automatically
    }
}
```

#### Benefits at scale

- [x] Add/remove panels without touching game logic
- [x] Panels can be enabled/disabled freely (tokens handle lifecycle)
- [x] Easy to add "observer panels" (e.g., debug overlays) without modifying existing code

##### Anti-pattern to avoid

```csharp
//  DON'T: Separate message per UI element (too granular)
[DxBroadcastMessage] public partial struct HealthChanged { public int health; }
[DxBroadcastMessage] public partial struct ManaChanged { public int mana; }
[DxBroadcastMessage] public partial struct GoldChanged { public int gold; }
// This creates 3x message traffic and registration overhead

//  DO: Batch related updates into one message
[DxBroadcastMessage] public partial struct PlayerStatsChanged {
    public int health;
    public int mana;
    public int gold;
}
```

### Pattern: Priority-Ordered Execution for Complex Systems

**Challenge:** Invoke save, audio and UI handlers in a chosen order.

Save each component class in a matching `.cs` file with the shown imports. Attach and enable the
three listeners before calling `ExitController.RequestExit`. This example logs handler invocation;
replace each log with the corresponding application work. All components use the global bus here.

```csharp
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Extensions;
using DxMessaging.Unity;
using UnityEngine;

[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct GameExit { }

public sealed class SaveSystem : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterUntargeted<GameExit>(OnExit, priority: 0);
    }

    private void OnExit(in GameExit message)
    {
        Debug.Log("Save handler: priority 0");
    }
}

public sealed class AudioSystem : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterUntargeted<GameExit>(OnExit, priority: 5);
    }

    private void OnExit(in GameExit message)
    {
        Debug.Log("Audio handler: priority 5");
    }
}

public sealed class UISystem : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterUntargeted<GameExit>(OnExit, priority: 10);
    }

    private void OnExit(in GameExit message)
    {
        Debug.Log("UI handler: priority 10");
    }
}

public sealed class ExitController : MonoBehaviour
{
    public void RequestExit()
    {
        GameExit message = new();
        message.Emit();
    }
}
```

**Key insight:** Lower priority numbers run first. Priority orders these synchronous callback
invocations; asynchronous or deferred work needs its own completion coordination.

#### Recommended priority ranges

- **-100 to -50:** Critical systems (save, validation)
- **-10 to 0:** Core gameplay logic
- **0 to 10:** UI updates
- **10 to 20:** Visual effects
- **20+:** Analytics, logging, non-critical

### Pattern: Efficient Interception at Scale

**Challenge:** Validate 1000s of messages without duplicating logic.

```csharp
[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct ApplyDamage {
    public readonly int amount;
    public readonly DamageType damageType;
}

// Single interceptor validates ALL targeted damage commands
public class DamageValidator : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        // Interceptors run BEFORE handlers
        _ = Token.RegisterTargetedInterceptor<ApplyDamage>(ValidateDamage, priority: -100);
    }

    bool ValidateDamage(ref InstanceId target, ref ApplyDamage msg) {
        // Validation logic runs once per message, not per handler
        if (msg.amount <= 0) return false; // Cancel invalid
        if (msg.amount > 9999) {
            msg = new ApplyDamage(9999, msg.damageType); // Clamp
        }
        return true; // Allow to proceed to handlers
    }
}

// Handlers can trust data is valid (no duplicate checks needed)
public class CombatEntity : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        _ = Token.RegisterComponentTargeted<ApplyDamage>(this, OnApplyDamage);
    }

    void OnApplyDamage(in ApplyDamage msg) {
        // No need to validate - interceptor already did it
        health -= msg.amount;
        var fact = new EntityDamaged(msg.amount, msg.damageType);
        fact.EmitComponentBroadcast(this);
    }
}
```

**Scale benefit:** Validation runs O(1) per message, not O(N) per handler. For 100 handlers, this saves 99 validation calls per message.

### Pattern: Post-Processing for Analytics at Scale

**Challenge:** Track metrics without polluting gameplay code.

```csharp
public class GameAnalytics : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        // Record completed dispatch after gameplay handlers have run.
        _ = Token.RegisterBroadcastWithoutSourcePostProcessor<EntityDamaged>(
            RecordProcessedDamageMessage,
            priority: 100);
        _ = Token.RegisterUntargetedPostProcessor<LevelCompleted>(
            RecordProcessedLevelCompletion,
            priority: 100);
    }

    void RecordProcessedDamageMessage(InstanceId source, EntityDamaged msg) {
        SendAnalyticsEvent("damage_message_processed", new {
            source = source.ToString(),
            amount = msg.amount,
            type = msg.damageType
        });
    }

    void RecordProcessedLevelCompletion(in LevelCompleted msg) {
        SendAnalyticsEvent("level_completion_message_processed", new { level = msg.levelIndex });
    }
}
```

**Key insight:** Handlers own gameplay and presentation state. Post-processors let you add
analytics, logging, and telemetry without modifying those handlers. A post-processor sees the
processed message, not proof that a handler changed health or any other state.

### Performance Optimization Patterns at Scale

#### Optimization 1: Disable Diagnostics in Builds

```csharp
#if UNITY_EDITOR
IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.All;
IMessageBus.GlobalMessageBufferSize = 100; // Keep history for debugging
#else
IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Off; // Production builds
#endif
```

**Impact:** Diagnostics adds ~5-10% overhead per message. Disable in production.

#### Optimization 2: Use Specific Registrations Over GlobalAcceptAll

```csharp
//  SLOW: Receives ALL messages, even irrelevant ones
_ = Token.RegisterGlobalAcceptAll(
    (in IUntargetedMessage m) => { /* called for every untargeted */ },
    (in InstanceId t, in ITargetedMessage m) => { /* called for every targeted */ },
    (in InstanceId s, in IBroadcastMessage m) => { /* called for every broadcast */ }
);

//  FAST: Only receives relevant messages
_ = Token.RegisterBroadcastWithoutSource<EntityDamaged>(OnDamage);
_ = Token.RegisterUntargeted<LevelCompleted>(OnLevelComplete);
```

**Impact:** Specific registrations use type-indexed lookups (O(1)). GlobalAcceptAll checks every message (O(N)).

#### Optimization 3: Batch Message Emissions

```csharp
//  WASTEFUL: Emit after every tiny change
void TakeDamage(int amount) {
    health -= amount;
    var msg = new HealthChanged(health);
    msg.EmitGameObjectBroadcast(gameObject); // Emits every frame
}
```

```csharp
//  EFFICIENT: Batch updates, emit once per frame
private bool healthDirty = false;

void TakeDamage(int amount) {
    health -= amount;
    healthDirty = true;
}

void LateUpdate() {
    if (healthDirty) {
        healthDirty = false;
        var msg = new HealthChanged(health);
        msg.EmitGameObjectBroadcast(gameObject);
    }
}
```

This example coalesces changes made before `LateUpdate` into one emission from that callback.
Clear the dirty flag before emitting: dispatch is synchronous, so a listener can call `TakeDamage`
and mark the next batch dirty. Those changes are emitted on the next `LateUpdate` instead of being
lost when the current emission returns. Intermediate health values are omitted. The reduction
depends on how many changes occur between flushes; this example does not measure throughput.

### Scaling Guidelines

Measure representative workloads on the target hardware, Unity backend and build configuration.
Record subscriber fan-out and callback work alongside the message rate:

| Workload                  | Measurements to collect                                          |
| ------------------------- | ---------------------------------------------------------------- |
| Many active entities      | Registrations per entity, subscriber fan-out and retained memory |
| Frequent emissions        | Delivery rate, callback time, latency and frame budget           |
| Spawn, despawn or pooling | Registration work, cleanup work and managed allocations          |
| Diagnostics enabled       | Capture mode, recording cost and retained history                |

Compare results within the scope recorded by the
[performance benchmarks](../architecture/performance.md). Use the
[measurement methodology](../runbooks/perf-benchmark-methodology.md) when collecting new results.

<a id="real-world-production-example-battle-royale-game"></a>

### Example: Battle Royale UI

**Scenario:** 100 players, each with health/armor/weapons. UI needs to show:

- Your health/armor
- Teammate health (4 players)
- Kill feed (all 100 players)
- Match stats (damage dealt, kills, etc.)

```csharp
// Messages
[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct PlayerDamaged {
    public readonly int playerId;
    public readonly int newHealth;
    public readonly int newArmor;
}

[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct PlayerKilled {
    public readonly int victimId;
    public readonly int killerId;
}

// YOUR health bar (targeted observation)
public class SelfHealthUI : MessageAwareComponent {
    [SerializeField] private GameObject localPlayerObject;

    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        _ = Token.RegisterGameObjectBroadcast<PlayerDamaged>(localPlayerObject, OnDamage);
    }

    void OnDamage(in PlayerDamaged msg) => UpdateHealthBar(msg.newHealth, msg.newArmor);
}

// TEAMMATE health bars (selective observation)
public class TeamHealthUI : MessageAwareComponent {
    [SerializeField] private List<GameObject> teammateObjects;

    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        foreach (var teammate in teammateObjects) {
            _ = Token.RegisterGameObjectBroadcast<PlayerDamaged>(teammate, OnTeammateDamage);
        }
    }

    void OnTeammateDamage(in PlayerDamaged msg) => UpdateTeammateBar(msg.playerId, msg.newHealth);
}

// KILL FEED (global observation)
public class KillFeedUI : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        _ = Token.RegisterBroadcastWithoutSource<PlayerKilled>(OnAnyKill);
    }

    void OnAnyKill(InstanceId source, PlayerKilled msg) {
        ShowKillFeedEntry(msg.killerId, msg.victimId);
    }
}

// MATCH STATS (analytics)
public class MatchStats : MessageAwareComponent {
    private readonly Dictionary<int, int> processedDamageMessagesByPlayer = new();

    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        _ = Token.RegisterBroadcastWithoutSourcePostProcessor<PlayerDamaged>(
            CountProcessedDamageMessage);
    }

    void CountProcessedDamageMessage(InstanceId source, PlayerDamaged msg) {
        processedDamageMessagesByPlayer.TryGetValue(msg.playerId, out int count);
        processedDamageMessagesByPlayer[msg.playerId] = count + 1;
    }
}
```

#### Example traffic assumptions

Assume each of the 100 players emits 10 damage messages per second and the match emits 5 kill
messages per second:

- Self health UI: 1 source-filtered registration observes 10 damage messages/sec.
- Team health UI: 4 source-filtered registrations observe 40 damage messages/sec in total.
- Kill feed UI: 1 registration observes all 5 kill messages/sec.
- Match stats: 1 post-processor observes all 1000 damage messages/sec and counts processed payloads.

These assumed rates describe message volume and filtering. They do not measure dispatch cost, UI
updates, dictionary growth, allocations or frame time. Measure the complete workload on the target
hardware and backend; the [published benchmarks](../architecture/performance.md) cover their named
dispatch scenarios with minimal handler work.

---

Use source-filtered observation for specific entities, global observation for analytics, and
post-processors for metrics. Batch emissions only when omitting intermediate values is acceptable,
and measure diagnostics overhead in the configuration you plan to ship.

---

## 14) Compatibility with Scriptable Object Architecture (SOA)

Scriptable Object Architecture uses shared assets for data, variables, event channels or runtime
sets. DxMessaging can carry notifications between systems that use those assets.

[Unity's architecture guide](https://unity.com/how-to/architect-game-code-scriptable-objects)
demonstrates both mutable runtime variables and asset-based events. ScriptableObjects are not
restricted to immutable configuration. The
[Unity 2021.3 manual](https://docs.unity3d.com/2021.3/Documentation/Manual/class-ScriptableObject.html)
distinguishes shared data loaded from assets from saving asset changes: a deployed player needs its
own save system for persistent runtime changes.

### What is Scriptable Object Architecture?

Ryan Hipple's [Unite Austin 2017 talk](https://www.youtube.com/watch?v=raQ3iHhE_Kk) describes assets
used as shared variables, event channels and runtime collections. Serialized references let multiple
components access the same asset. The asset's implementation defines its payload, mutation and
listener behavior.

<a id="why-soa-is-controversial"></a>

### Choose Ownership and Reset Rules

Decide these responsibilities before connecting an asset to the bus:

- **State owner:** Identify which asset or service owns health, scores or collection membership.
  A notification reports a change; it does not replace that state or answer a current-state query.
- **Initial state:** Give new listeners a current snapshot when they need one. A listener that
  subscribes after a change cannot reconstruct it from future notifications alone.
- **Reset:** Define what resets at a new game, scene boundary and play session. Test the domain
  reload configuration used by the project.
- **Subscription lifetime:** Pair asset listeners and bus registrations with their actual owners.
  A bridge owns subscriptions on both sides.
- **Persistence:** Separate authored defaults, runtime values and saved-game data. Decide which
  changes should survive a session and which storage writes them.
- **Testing:** Restore shared asset state and dispose subscriptions between tests. Local buses
  isolate message routing; they do not isolate a shared asset's data.

[Anti-ScriptableObject Architecture](https://github.com/cathei/AntiScriptableObjectArchitecture)
is a critique of these patterns. Evaluate its arguments alongside Unity's examples and the needs of
your project; it does not establish that every runtime asset use is invalid.

### Can DxMessaging Work with SOA?

Use an asset or service to own state and DxMessaging to report changes. Choose event assets when
serialized asset references suit the authoring workflow. Choose typed bus registrations when code
needs DxMessaging routing, interception, priorities or diagnostics. A bridge can connect the two.

| Responsibility   | ScriptableObject-based design                                      | DxMessaging                                                                  |
| ---------------- | ------------------------------------------------------------------ | ---------------------------------------------------------------------------- |
| State            | An asset can hold shared configuration or mutable runtime values   | Messages report changes; the application owns state                          |
| Authoring        | Serialized asset references and project-specific Inspector tooling | Message types, registrations and optional component setup                    |
| Payload contract | Defined by the event or variable implementation                    | Typed message interfaces and handler signatures                              |
| Lifetime         | The application manages asset state and listeners                  | Tokens, leases and components manage registrations when disposed or released |
| Diagnostics      | Asset Inspector and project tooling                                | Bus and token diagnostics                                                    |
| Cost             | Depends on the asset implementation and listener work              | Depends on routes, handlers and diagnostics; measure the complete workload   |

### Pattern Overview

| Pattern | What it shows                                          | When to use                                           | SOA involvement         |
| ------- | ------------------------------------------------------ | ----------------------------------------------------- | ----------------------- |
| **A**   | SOA Events (GameEvent) forwarding to DxMessaging       | Designer-created event assets, modern code downstream | Yes - SOA Event pattern |
| **B**   | ScriptableObjects for configs + DxMessaging for events | Shared authored config, typed runtime notifications   | No event-asset bridge   |

### Pattern A: SOA -> DxMessaging (Event Forwarding)

**Use case:** Designer-created SOA events, but you want DxMessaging benefits downstream.

**Strategy:** SOA GameEventListener forwards to DxMessaging.

> **This uses the SOA GameEvent pattern:** If you're NOT using designer-created GameEvent assets
> with `Raise()`/listener management, you don't need this pattern. For immutable config data,
> see Pattern B instead.

#### Example: Scene Transitions

```csharp
using System.Collections.Generic;
using DxMessaging.Core.Messages;
using DxMessaging.Unity;
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Extensions;
using UnityEngine;
using UnityEngine.Events;

// Traditional SOA event
[CreateAssetMenu(menuName = "Events/Game Event")]
public class GameEvent : ScriptableObject
{
    private readonly List<UnityAction> listeners = new();

    public void Raise()
    {
        for (int i = listeners.Count - 1; i >= 0; i--)
            listeners[i]?.Invoke();
    }

    public void RegisterListener(UnityAction listener) => listeners.Add(listener);
    public void UnregisterListener(UnityAction listener) => listeners.Remove(listener);
}

// DxMessaging message (modern, type-safe)
[DxUntargetedMessage]
public readonly partial struct SceneTransitionRequested { }

// Bridge: SOA Event -> DxMessaging
public class SoaEventBridge : MonoBehaviour
{
    [SerializeField] private GameEvent onSceneTransitionSO; // Designer-created asset
    private GameEvent subscribedEvent;

    void OnEnable()
    {
        if (onSceneTransitionSO == null)
        {
            return;
        }

        subscribedEvent = onSceneTransitionSO;
        subscribedEvent.RegisterListener(OnSoaEvent);
    }

    void OnDisable()
    {
        GameEvent publisher = subscribedEvent;
        subscribedEvent = null;
        if (!ReferenceEquals(publisher, null))
        {
            publisher.UnregisterListener(OnSoaEvent);
        }
    }

    void OnSoaEvent()
    {
        // Forward to DxMessaging
        var message = new SceneTransitionRequested();
        message.Emit();
    }
}

// Modern DxMessaging listeners (no SO dependency)
public class AudioSystem : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterUntargeted<SceneTransitionRequested>(OnTransition);
    }

    void OnTransition(in SceneTransitionRequested msg)
    {
        Debug.Log("Scene transition requested; start the application audio fade here.");
    }
}
```

Assign the event asset before enabling the bridge. The bridge keeps the subscribed asset for
teardown, so editing or clearing the Inspector field while active does not leave its old listener
registered. A later enable uses the current field value. The audio listener logs the notification;
replace that log with the application's fade operation.

##### Benefits

- [x] Designers create events in Inspector (SOA workflow preserved)
- [x] Code uses DxMessaging (type-safe, lifecycle-safe)

###### Drawbacks

- Bridge boilerplate for each SOA event
- Double registration (SOA listener + DxMessaging handler)

<a id="pattern-b-proper-scriptableobject-usage-recommended"></a>

### Pattern B: Configuration Assets and Runtime Messages

**Use case:** ScriptableObjects for immutable config data, DxMessaging for runtime events.

**Strategy:** Read shared configuration from assets and emit runtime notifications through the bus.
No event-asset bridge is needed for this arrangement. This example leaves the configuration unchanged
during play; that is an application choice, not a ScriptableObject restriction.

#### Example: Combat with Designer-Tunable Data

```csharp
using DxMessaging.Core;
using DxMessaging.Core.Extensions;
using DxMessaging.Core.Messages;
using DxMessaging.Core.Attributes;
using DxMessaging.Unity;
using UnityEngine;

// ScriptableObject: Designer-tunable balance data (immutable at runtime)
// This example reads configuration without changing it during play.
[CreateAssetMenu(menuName = "Config/Weapon Stats")]
public class WeaponStats : ScriptableObject
{
    public int baseDamage = 10;      // Designer tweaks in Inspector
    public float critMultiplier = 2f;
}

// DxMessaging: Runtime event (code-driven)
[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct DamageDealt
{
    public readonly int amount;
    public readonly bool wasCrit;
}

// Combat system: Reads immutable SO data, emits DxMessaging events
public class Weapon : MessageAwareComponent
{
    [SerializeField] private WeaponStats stats; // Shared configuration read by this component

    public void Fire()
    {
        bool crit = Random.value < 0.1f;
        int damage = Mathf.RoundToInt(stats.baseDamage * (crit ? stats.critMultiplier : 1f));

        var msg = new DamageDealt(damage, crit);
        msg.EmitComponentBroadcast(this); // Runtime event via DxMessaging
    }
}

// Analytics: Pure DxMessaging (no SOA dependency)
public class CombatAnalytics : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterBroadcastWithoutSource<DamageDealt>(OnDamage);
    }

    void OnDamage(InstanceId source, DamageDealt msg)
    {
        Debug.Log($"{source} dealt {msg.amount} damage (crit: {msg.wasCrit})");
    }
}
```

##### Responsibilities

- Keep authored weapon settings separate from per-player mutable state.
- Register analytics with the bus used by the sender and release registrations with their owner.
- Measure any configuration lookup, message dispatch and listener work in the target application.

### When to Use Each Pattern

| Pattern                   | Use when                                               | Ownership to define                                     |
| ------------------------- | ------------------------------------------------------ | ------------------------------------------------------- |
| **A: SOA -> DxMessaging** | Existing event assets must notify bus listeners        | Asset listeners, bridge lifetime and bus registrations  |
| **B: Configs + messages** | Shared authored settings feed code that emits messages | Configuration, runtime state and bus registrations      |
| **No bridge**             | The application already uses one event system          | State, listener lifetime and reset rules in that system |

These examples do not measure or rank the alternatives' performance.

### Migration Path: SOA -> DxMessaging

Migrate a notification channel separately from the state it describes:

1. Identify event producers, listeners, payloads and lifetime boundaries.
1. Define equivalent typed messages and choose the bus each participant uses.
1. Add a bridge for the event channels that must coexist during migration.
1. Move listeners incrementally, checking that a transition does not produce duplicate delivery.
1. Remove a bridge or asset only after every remaining reference and owner has been accounted for.

For shared variables, keep an explicit state owner. An asset or service can hold the current value
while messages report changes. Preserve initialization, reset and save behavior if you move that
state to another owner.

For runtime sets, retain a membership collection or service with queries for current members.
Messages can announce additions and removals, but a new observer still needs an initial snapshot
and a defined ordering between that snapshot and subsequent changes. A
`RegisterBroadcastWithoutSource` subscription alone is not a replacement for a runtime set.

### Final Recommendations

- Keep existing asset workflows that meet the project's authoring and ownership needs.
- Use Pattern A when existing event assets need to reach DxMessaging listeners.
- Use Pattern B when shared configuration and runtime messages are sufficient for the application.
- Test reset, subscription cleanup, initial-state delivery and persistence for the chosen design.
- Compare measured workload costs before choosing an architecture for performance reasons.

#### Resources

- [Anti-ScriptableObject Architecture](https://github.com/cathei/AntiScriptableObjectArchitecture) - Detailed critique
- [Ryan Hipple Unite 2017 Talk](https://www.youtube.com/watch?v=raQ3iHhE_Kk) - Original SOA presentation
- [Unity Official Guide](https://unity.com/how-to/architect-game-code-scriptable-objects) - Unity's perspective
- [DxMessaging DI Integrations](../integrations/index.md) - Options for service ownership
- [Zenject](https://github.com/modesttree/Zenject) - DI framework
- [VContainer](https://github.com/hadashiA/VContainer) - DI framework

---

## Related Documentation

### Learn the Basics First?

- to [Getting Started](../getting-started/getting-started.md) (10 min) -- Complete introduction
- to [Message Types](../concepts/message-types.md) (10 min) -- When to use what
- to [Visual Guide](../getting-started/visual-guide.md) (5 min) -- Beginner-friendly pictures

### Try Real Examples

- to [Mini Combat sample](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Samples~/Mini%20Combat/README.md) -- Working combat example
- to [UI Buttons + Inspector sample](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Samples~/UI%20Buttons%20%2B%20Inspector/README.md) -- Interactive diagnostics
- to [End-to-End Example](../examples/end-to-end.md) -- Complete feature walkthrough

### Deep Dives

- to [Interceptors & Ordering](../concepts/interceptors-and-ordering.md) -- Control execution flow
- to [Design & Architecture](../architecture/design-and-architecture.md) -- Internals and optimizations
- to [Performance](../architecture/performance.md) -- Benchmarks and tuning

### Reference

- to [Quick Reference](../reference/quick-reference.md) -- Cheat sheet
- to [API Reference](../reference/reference.md) -- Complete API
