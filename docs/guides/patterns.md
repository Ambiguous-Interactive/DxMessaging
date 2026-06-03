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
| Build a battle royale / large multiplayer    | [Pattern: Battle Royale Example](#real-world-production-example-battle-royale-game)                          |
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
- [Production Example: Battle Royale Game](#real-world-production-example-battle-royale-game)

---

### Important: Inheritance with MessageAwareComponent

- Many examples derive from `MessageAwareComponent`. **When overriding hooks, you MUST call the base method.**
- **Always call `base.RegisterMessageHandlers()` FIRST** in your override to preserve default string-message registrations and parent class registrations.
- **CRITICAL**: Call `base.OnEnable()` / `base.OnDisable()` if you override lifecycle methods; otherwise your token may never enable/disable.
- **CRITICAL**: Call `base.Awake()` if you override `Awake()`; otherwise your token won't be created.
- To opt out of string demos, override `RegisterForStringMessages => false` instead of skipping the base call.
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
void OnSceneLoaded(ref SceneLoaded m) => RefreshUI();
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
void OnHeal(ref Heal m) => ApplyHeal(m.amount);
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
hit.EmitGameObjectBroadcast(enemyGO);

// Listener: observe every source
_ = token.RegisterBroadcastWithoutSource<TookDamage>(OnAnyTookDamage);
void OnAnyTookDamage(InstanceId src, TookDamage m) => Log(src, m);
```

## 4) Validation and Normalization (Interceptors)

Use interceptors to enforce rules before handlers run.

```csharp
using DxMessaging.Core; // MessageHandler

var bus = MessageHandler.MessageBus;
_ = token.RegisterBroadcastInterceptor<TookDamage>((ref InstanceId source, ref TookDamage m) =>
{
    if (m.amount <= 0) return false; // cancel
    m = new TookDamage(Math.Min(m.amount, 999)); // clamp
    return true;
}, priority: 0);
```

## 5) Analytics/Logging (Post-Processors)

Post-processors run after handlers -- ideal for metrics.

```csharp
_ = token.RegisterUntargetedPostProcessor<SceneLoaded>((ref SceneLoaded m) => Metrics.TrackScene(m.buildIndex));
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
    (ref IUntargetedMessage m) => Debug.Log($"Untargeted {m.MessageType}"),
    (ref InstanceId target, ref ITargetedMessage m) => Debug.Log($"Targeted {m.MessageType} to {target}"),
    (ref InstanceId source, ref IBroadcastMessage m) => Debug.Log($"Broadcast {m.MessageType} from {source}")
);

Do's

- Use global accept-all in tooling and debug inspectors.
- Prefer specific registrations for gameplay code to avoid surprises.
```

## 11) Diagnostics and Tuning

- Enable `IMessageBus.GlobalDiagnosticsMode` in Editor or per-token.
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
        // Only listens to ONE enemy, not all 100
        _ = Token.RegisterGameObjectBroadcast<EntityDamaged>(trackedEnemy, OnDamaged);
    }

    void OnDamaged(ref EntityDamaged msg) => UpdateBar();
}

// Analytics: Observes ALL entities (global observation)
public class CombatAnalytics : MessageAwareComponent {
    private readonly Dictionary<InstanceId, int> totalDamageByEntity = new();

    protected override void RegisterMessageHandlers() {
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

- Disable diagnostics in production (`IMessageBus.GlobalDiagnosticsMode = false`)
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

    void Awake() {
        if (instance != null) {
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    protected override void RegisterMessageHandlers() {
        // Listen to ALL broadcasts from ANY scene
        _ = Token.RegisterBroadcastWithoutSource<EntityDamaged>(OnAnyDamage);
        _ = Token.RegisterUntargeted<LevelCompleted>(OnLevelComplete);
    }

    void OnAnyDamage(InstanceId src, EntityDamaged msg) {
        totalDamage += msg.amount;
        CheckAchievements();
    }

    void OnLevelComplete(ref LevelCompleted msg) {
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
// Game state messages (global)
[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct PlayerStatsChanged {
    public readonly int health;
    public readonly int mana;
    public readonly int gold;
}

[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct InventoryChanged { public readonly int itemCount; }

// Each UI panel listens independently
public class HealthPanel : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        _ = Token.RegisterUntargeted<PlayerStatsChanged>(OnStats);
    }
    void OnStats(ref PlayerStatsChanged msg) => UpdateHealth(msg.health);
}

public class ManaPanel : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        _ = Token.RegisterUntargeted<PlayerStatsChanged>(OnStats);
    }
    void OnStats(ref PlayerStatsChanged msg) => UpdateMana(msg.mana);
}

public class GoldPanel : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        _ = Token.RegisterUntargeted<PlayerStatsChanged>(OnStats);
    }
    void OnStats(ref PlayerStatsChanged msg) => UpdateGold(msg.gold);
}

public class InventoryPanel : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        _ = Token.RegisterUntargeted<InventoryChanged>(OnInventory);
    }
    void OnInventory(ref InventoryChanged msg) => Refresh();
}

// Game systems emit once, all panels update
public class PlayerStats : MonoBehaviour {
    void UpdateStats() {
        var msg = new PlayerStatsChanged(health, mana, gold);
        msg.Emit(); // All 3 panels update automatically
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
[DxUntargetedMessage] public struct HealthChanged { public int health; }
[DxUntargetedMessage] public struct ManaChanged { public int mana; }
[DxUntargetedMessage] public struct GoldChanged { public int gold; }
// This creates 3x message traffic and registration overhead

//  DO: Batch related updates into one message
[DxUntargetedMessage] public struct PlayerStatsChanged {
    public int health;
    public int mana;
    public int gold;
}
```

### Pattern: Priority-Ordered Execution for Complex Systems

**Challenge:** Ensure SaveSystem runs before SceneLoader, AudioSystem fades before UI transitions.

```csharp
[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct GameExit { }

public class SaveSystem : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        // Priority 0 = runs FIRST
        _ = Token.RegisterUntargeted<GameExit>(OnExit, priority: 0);
    }

    void OnExit(ref GameExit msg) {
        SaveGame(); // Must complete before audio fades
    }
}

public class AudioSystem : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        // Priority 5 = runs AFTER SaveSystem
        _ = Token.RegisterUntargeted<GameExit>(OnExit, priority: 5);
    }

    void OnExit(ref GameExit msg) {
        FadeOutMusic(); // Runs after save
    }
}

public class UISystem : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        // Priority 10 = runs LAST
        _ = Token.RegisterUntargeted<GameExit>(OnExit, priority: 10);
    }

    void OnExit(ref GameExit msg) {
        ShowExitAnimation(); // Runs after audio fade starts
    }
}

// Emit once, all systems execute in priority order
var msg = new GameExit();
msg.Emit();
```

**Key insight:** Lower priority numbers run first. Use priority to eliminate race conditions and ensure deterministic ordering.

#### Recommended priority ranges

- **-100 to -50:** Critical systems (save, validation)
- **-10 to 0:** Core gameplay logic
- **0 to 10:** UI updates
- **10 to 20:** Visual effects
- **20+:** Analytics, logging, non-critical

### Pattern: Efficient Interception at Scale

**Challenge:** Validate 1000s of messages without duplicating logic.

```csharp
// Single interceptor validates ALL damage
public class DamageValidator : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        // Interceptors run BEFORE handlers
        _ = Token.RegisterBroadcastInterceptor<EntityDamaged>(ValidateDamage, priority: -100);
    }

    bool ValidateDamage(ref InstanceId source, ref EntityDamaged msg) {
        // Validation logic runs once per message, not per handler
        if (msg.amount <= 0) return false; // Cancel invalid
        if (msg.amount > 9999) {
            msg = new EntityDamaged(9999, msg.damageType); // Clamp
        }
        return true; // Allow to proceed to handlers
    }
}

// Handlers can trust data is valid (no duplicate checks needed)
public class CombatEntity : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        _ = Token.RegisterComponentBroadcast<EntityDamaged>(this, OnDamaged);
    }

    void OnDamaged(ref EntityDamaged msg) {
        // No need to validate - interceptor already did it
        health -= msg.amount;
    }
}
```

**Scale benefit:** Validation runs O(1) per message, not O(N) per handler. For 100 handlers, this saves 99 validation calls per message.

### Pattern: Post-Processing for Analytics at Scale

**Challenge:** Track metrics without polluting gameplay code.

```csharp
public class GameAnalytics : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        // Post-processors run AFTER all handlers
        _ = Token.RegisterBroadcastWithoutSourcePostProcessor<EntityDamaged>(LogDamage, priority: 100);
        _ = Token.RegisterUntargetedPostProcessor<LevelCompleted>(LogLevelComplete, priority: 100);
    }

    void LogDamage(InstanceId source, EntityDamaged msg) {
        // Analytics code is completely isolated from gameplay
        SendAnalyticsEvent("damage_dealt", new {
            source = source.ToString(),
            amount = msg.amount,
            type = msg.damageType
        });
    }

    void LogLevelComplete(ref LevelCompleted msg) {
        SendAnalyticsEvent("level_complete", new { level = msg.levelIndex });
    }
}
```

**Key insight:** Post-processors let you add analytics, logging, and telemetry WITHOUT touching existing handlers. Add/remove analytics system without modifying game logic.

### Performance Optimization Patterns at Scale

#### Optimization 1: Disable Diagnostics in Builds

```csharp
#if UNITY_EDITOR
IMessageBus.GlobalDiagnosticsMode = true;
IMessageBus.GlobalMessageBufferSize = 100; // Keep history for debugging
#else
IMessageBus.GlobalDiagnosticsMode = false; // Production builds
#endif
```

**Impact:** Diagnostics adds ~5-10% overhead per message. Disable in production.

#### Optimization 2: Use Specific Registrations Over GlobalAcceptAll

```csharp
//  SLOW: Receives ALL messages, even irrelevant ones
_ = Token.RegisterGlobalAcceptAll(
    (ref IUntargetedMessage m) => { /* called for every untargeted */ },
    (ref InstanceId t, ref ITargetedMessage m) => { /* called for every targeted */ },
    (ref InstanceId s, ref IBroadcastMessage m) => { /* called for every broadcast */ }
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
    msg.Emit(); // Emits every frame
}

//  EFFICIENT: Batch updates, emit once per frame
private bool healthDirty = false;

void TakeDamage(int amount) {
    health -= amount;
    healthDirty = true;
}

void LateUpdate() {
    if (healthDirty) {
        var msg = new HealthChanged(health);
        msg.Emit(); // Emits once per frame max
        healthDirty = false;
    }
}
```

**Impact:** Reduces message traffic by 10-100x in high-frequency scenarios.

### Scaling Guidelines

| Entity Count | Message Rate | Recommendations                                   |
| ------------ | ------------ | ------------------------------------------------- |
| 1-10         | Any          | Default settings fine                             |
| 10-50        | <1000/sec    | Disable diagnostics in builds                     |
| 50-100       | <5000/sec    | Batch emissions, avoid GlobalAcceptAll            |
| 100-500      | <10k/sec     | Use local buses for subsystems, profile carefully |
| 500+         | <20k/sec     | Consider ECS or native code for hot paths         |

**Rule of thumb:** If you're emitting >20,000 messages/second, you're likely using DxMessaging for something better suited to ECS or direct method calls.

### Real-World Production Example: Battle Royale Game

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
        _ = Token.RegisterGameObjectBroadcast<PlayerDamaged>(localPlayerObject, OnDamage);
    }

    void OnDamage(ref PlayerDamaged msg) => UpdateHealthBar(msg.newHealth, msg.newArmor);
}

// TEAMMATE health bars (selective observation)
public class TeamHealthUI : MessageAwareComponent {
    [SerializeField] private List<GameObject> teammateObjects;

    protected override void RegisterMessageHandlers() {
        foreach (var teammate in teammateObjects) {
            _ = Token.RegisterGameObjectBroadcast<PlayerDamaged>(teammate, OnTeammateDamage);
        }
    }

    void OnTeammateDamage(ref PlayerDamaged msg) => UpdateTeammateBar(msg.playerId, msg.newHealth);
}

// KILL FEED (global observation)
public class KillFeedUI : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        _ = Token.RegisterBroadcastWithoutSource<PlayerKilled>(OnAnyKill);
    }

    void OnAnyKill(InstanceId source, PlayerKilled msg) {
        ShowKillFeedEntry(msg.killerId, msg.victimId);
    }
}

// MATCH STATS (analytics)
public class MatchStats : MessageAwareComponent {
    private Dictionary<int, int> damageByPlayer = new();

    protected override void RegisterMessageHandlers() {
        _ = Token.RegisterBroadcastWithoutSourcePostProcessor<PlayerDamaged>(TrackDamage);
    }

    void TrackDamage(InstanceId source, PlayerDamaged msg) {
        // Track for post-game stats
    }
}
```

#### Key insights

- Self health UI: 1 registration, receives ~10 messages/sec
- Team health UI: 4 registrations, receives ~40 messages/sec
- Kill feed UI: 1 registration, receives ALL kills (~5 messages/sec)
- Match stats: Post-processor, doesn't affect gameplay latency

**Total:** ~100 players x 10 damage/sec = 1000 messages/sec. DxMessaging handles this with negligible overhead (~0.06ms/frame).

---

**Summary:** DxMessaging scales from small prototypes to large production games. Use targeted observation for specific entities, global observation for analytics, and post-processors for metrics. Disable diagnostics in production and batch emissions for optimal performance.

---

## 14) Compatibility with Scriptable Object Architecture (SOA)

**Note:** Scriptable Object Architecture (SOA) is a debated pattern in the Unity community. It has both proponents who value its designer-friendly workflow and critics who raise concerns about scalability and maintainability. See [Anti-ScriptableObject Architecture](https://github.com/cathei/AntiScriptableObjectArchitecture) for one perspective on the criticisms. Teams should evaluate SOA based on their specific needs. Alternatives include dependency injection (Zenject, VContainer), reactive systems (UniRx), or messaging systems (DxMessaging, MessagePipe).

If your project uses or requires SOA, DxMessaging can work alongside it.

### What is Scriptable Object Architecture?

**SOA Background:** Popularized by Ryan Hipple's [Unite Austin 2017 talk](https://www.youtube.com/watch?v=raQ3iHhE_Kk), SOA uses ScriptableObject assets as:

1. **Shared Variables** - `FloatVariable`, `IntVariable`, etc. (ScriptableObject assets that hold runtime state)
1. **Event Channels** - `GameEvent` + `GameEventListener` pattern (designer-created events)
1. **Runtime Sets** - Collections of active game objects (e.g., all enemies)

**Core idea:** Systems communicate through serialized SO assets instead of direct references.

#### Key resources

- [Unite 2017 Talk by Ryan Hipple](https://www.youtube.com/watch?v=raQ3iHhE_Kk)
- [Official Unity Guide](https://unity.com/how-to/architect-game-code-scriptable-objects)
- [Reference Implementation](https://github.com/roboryantron/Unite2017)
- [Community Package](https://github.com/DanielEverland/ScriptableObject-Architecture)

### Why SOA is Controversial

From [Anti-ScriptableObject Architecture](https://github.com/cathei/AntiScriptableObjectArchitecture), key criticisms include:

1. **Wrong Purpose** - ScriptableObjects are designed for immutable design data, not runtime mutable state
1. **Redundant Complexity** - Standard C# objects achieve the same goals without SO restrictions
1. **Inspector Dependency** - Binds architecture to Unity's GUI, complicating debugging and maintenance
1. **Limited Scalability** - Runtime-created variables undermine the pattern; managing hundreds of `*Variable` and `GameEvent` assets across scenes becomes unwieldy
1. **Domain Reload Issues** - Disabled domain reloading causes ScriptableObjects to retain values unpredictably
1. **Testability Concerns** - SO assets persist between tests, requiring manual cleanup

#### Recommended alternatives

- **Dependency Injection** - Zenject, VContainer, Reflex (see [DxMessaging DI Integrations](../integrations/index.md))
- **Reactive Systems** - UniRx, UniTask
- **Messaging** - DxMessaging (this framework), MessagePipe
- **Configuration Data** - Spreadsheet-based solutions (e.g., BakingSheet)

Use ScriptableObjects for their intended purpose: **immutable design-time data** (configs, balance tables, prefab references).

### Can DxMessaging Work with SOA?

**Yes, but with caveats.** DxMessaging and SOA solve similar problems (decoupling, communication) with different philosophies:

| Aspect               | SOA                                                                              | DxMessaging                               |
| -------------------- | -------------------------------------------------------------------------------- | ----------------------------------------- |
| **Paradigm**         | Asset-based, persistent state                                                    | Runtime message passing, transient        |
| **Designer-Centric** | High (create events in Inspector)                                                | Low (code-driven)                         |
| **Type Safety**      | Mixed (SO refs typed, but UnityEvent inspector wiring loses compile-time safety) | Strong (compile-time validation)          |
| **Lifecycle**        | Manual (SO assets persist)                                                       | Automatic (tokens clean up)               |
| **Debugging**        | Inspector-dependent                                                              | Built-in diagnostics                      |
| **Performance**      | List iteration, UnityAction overhead                                             | Zero-allocation structs                   |
| **Use Case**         | Shared state, designer-driven configs                                            | Event-driven communication, runtime logic |
| **Testability**      | Requires SO asset cleanup                                                        | Isolated buses per test                   |

**Summary:** For new projects, evaluate DxMessaging, DI frameworks, or other messaging approaches based on your needs. If you have existing SOA code, the patterns below show coexistence strategies.

### Pattern Overview

| Pattern | What it shows                                          | When to use                                           | SOA involvement           |
| ------- | ------------------------------------------------------ | ----------------------------------------------------- | ------------------------- |
| **A**   | SOA Events (GameEvent) forwarding to DxMessaging       | Designer-created event assets, modern code downstream | Yes - SOA Event pattern   |
| **B**   | ScriptableObjects for configs + DxMessaging for events | New projects / best practice                          | No - proper SO usage only |

### Pattern A: SOA -> DxMessaging (Event Forwarding)

**Use case:** Designer-created SOA events, but you want DxMessaging benefits downstream.

**Strategy:** SOA GameEventListener forwards to DxMessaging.

> **This uses the SOA GameEvent pattern:** If you're NOT using designer-created GameEvent assets
> with `Raise()`/listener management, you don't need this pattern. For immutable config data,
> see Pattern B instead.

#### Example: Scene Transitions

```csharp
using DxMessaging.Core.Messages;
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
public class SOAEventBridge : MonoBehaviour
{
    [SerializeField] private GameEvent onSceneTransitionSO; // Designer-created asset

    void OnEnable()
    {
        onSceneTransitionSO.RegisterListener(OnSOAEvent);
    }

    void OnDisable()
    {
        onSceneTransitionSO.UnregisterListener(OnSOAEvent);
    }

    void OnSOAEvent()
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

    void OnTransition(ref SceneTransitionRequested msg)
    {
        FadeOutMusic(); // Type-safe, debuggable via DxMessaging Inspector
    }
}
```

##### Benefits

- [x] Designers create events in Inspector (SOA workflow preserved)
- [x] Code uses DxMessaging (type-safe, lifecycle-safe)

###### Drawbacks

- Bridge boilerplate for each SOA event
- Double registration (SOA listener + DxMessaging handler)

### Pattern B: Proper ScriptableObject Usage (Recommended)

**Use case:** ScriptableObjects for immutable config data, DxMessaging for runtime events.

**Strategy:** Use each tool for its intended purpose; avoid bridging.

> **Important:** This pattern uses ScriptableObjects CORRECTLY (immutable design data),
> NOT as SOA (mutable runtime state). This is standard Unity best practice, not SOA.
> If you're only using SOs for config data like this, you don't need SOA patterns at all.

#### Example: Combat with Designer-Tunable Data

```csharp
using DxMessaging.Core.Messages;
using DxMessaging.Core.Attributes;
using DxMessaging.Unity;
using UnityEngine;

// ScriptableObject: Designer-tunable balance data (immutable at runtime)
// This is CORRECT SO usage, NOT SOA
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
    [SerializeField] private WeaponStats stats; // Immutable config (correct SO usage)

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

##### Benefits

- [x] **Best of both worlds** - ScriptableObjects for static configs, DxMessaging for runtime events
- [x] No bridging overhead
- [x] Uses each system correctly: SOs for their intended purpose (immutable design data), messaging for runtime communication
- [x] This is NOT SOA - it's proper Unity architecture

###### This pattern separates concerns clearly

### When to Use Each Pattern

| Pattern                     | Use When                                                | Complexity | Performance |
| --------------------------- | ------------------------------------------------------- | ---------- | ----------- |
| **A: SOA -> DxMessaging**   | Designers create SOA events, modern code uses messaging | Medium     | Medium      |
| **B: Proper SO Usage**      | Immutable configs only, messaging for events            | Low        | Good        |
| **None (Pure DxMessaging)** | Greenfield project or full SOA migration                | Lowest     | Best        |

### Migration Path: SOA -> DxMessaging

If you're moving away from SOA:

1. **Phase 1:** Identify SOA event usage (GameEvent/GameEventListener patterns)
1. **Phase 2:** Create equivalent DxMessaging messages (Untargeted/Broadcast)
1. **Phase 3:** Add bridges (Pattern A or B) to maintain compatibility
1. **Phase 4:** Migrate listeners to DxMessaging incrementally
1. **Phase 5:** Remove bridges and SOA assets once all references gone

For SOA variables:

1. Convert read-only SO configs -> Keep as-is (correct SO usage) or move to JSON/ScriptableObjects for data
1. Convert mutable SO variables -> DxMessaging messages or DI-injected services
1. Convert RuntimeSets -> DxMessaging global observers (`RegisterBroadcastWithoutSource`)

### Final Recommendations

#### If you're using SOA

- [x] **Do:** Use Pattern B (Proper SO Usage) - SOs for immutable configs ONLY, DxMessaging for runtime events
- [x] **Do:** Use Pattern A to bridge existing SOA GameEvent assets to DxMessaging during migration
- [x] **Do:** Read [Anti-ScriptableObject Architecture](https://github.com/cathei/AntiScriptableObjectArchitecture) to understand risks
- [x] **Do:** Consider gradual migration to DxMessaging or DI frameworks
- [ ] **Don't:** Use SOs for mutable runtime state (health, scores, etc.)
- [ ] **Don't:** Create new SOA event assets -- use DxMessaging messages instead

##### If you're starting fresh

- [x] **Do:** Use DxMessaging for all messaging/events
- [x] **Do:** Use ScriptableObjects ONLY for immutable design data (weapon stats, level configs)
- [x] **Do:** Consider DI frameworks (Zenject/VContainer) for service dependencies
- [ ] **Don't:** Adopt SOA's GameEvent/Variable patterns -- they're superseded by better tools

###### Resources

- [Anti-ScriptableObject Architecture](https://github.com/cathei/AntiScriptableObjectArchitecture) - Detailed critique
- [Ryan Hipple Unite 2017 Talk](https://www.youtube.com/watch?v=raQ3iHhE_Kk) - Original SOA presentation
- [Unity Official Guide](https://unity.com/how-to/architect-game-code-scriptable-objects) - Unity's perspective
- [DxMessaging DI Integrations](../integrations/index.md) - Better alternatives for dependency management
- [Zenject](https://github.com/modesttree/Zenject) - Recommended DI framework
- [VContainer](https://github.com/hadashiA/VContainer) - Lightweight DI alternative

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
