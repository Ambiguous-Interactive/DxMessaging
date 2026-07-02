# Targeting and Context (Component vs GameObject)

**Critical Unity concept:** Understanding GameObject vs Component targeting is essential for using DxMessaging effectively.

## Overview

Targeted and broadcast messages carry context: an `InstanceId` for the target (targeted) or source (broadcast). In Unity, `InstanceId` can represent either a `GameObject` or a specific `Component`. **These are completely separate channels** -- mixing them is the #1 cause of "why isn't my handler firing?" bugs.

## Key Concepts

### InstanceId

- **Implicit conversion:** Automatically converts from both `GameObject` and `Component`
- **Equality:** Based on Unity's instance ID (internally stored as an `int`)
- **Matching:** The bus compares `InstanceId` values for exact matches
- **Separate channels:** GameObject and Component are distinct addresses

### Matching Rules

- A handler registered for a `GameObject` **only** receives messages emitted to that `GameObject`
- A handler registered for a `Component` **only** receives messages emitted to that `Component`
- Emitting to a `GameObject` does **not** reach Component-registered handlers (and vice versa)
- This applies to **both** targeted and broadcast messages

## Understanding GameObject vs Component Targeting

### What GameObject Targeting Means

When you emit to a **GameObject**, you're saying: "This message is for whatever is attached to this GameObject."

```csharp
[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct Heal { public readonly int amount; }

// Emit to GameObject
var heal = new Heal(10);
heal.EmitGameObjectTargeted(playerGameObject);
```

#### Who receives it?

- Any Component on that GameObject that registered with `RegisterGameObjectTargeted`
- Multiple components on the same GameObject can all receive it
- Components on child/parent GameObjects will NOT receive it

##### Use when

- You don't care which specific component handles it
- You want flexibility (multiple components can respond)
- You're commanding "the player" or "the enemy" as a conceptual unit

### What Component Targeting Means

When you emit to a **Component**, you're saying: "This message is specifically for this one Component instance."

```csharp
// Emit to specific Component
var heal = new Heal(10);
heal.EmitComponentTargeted(playerHealthComponent);
```

#### Who receives it?

- ONLY that specific Component instance
- Other components on the same GameObject will NOT receive it
- This is a direct, pinpoint message

##### Use when

- You have a reference to the exact component you want to message
- You need precise control (only this component, not its siblings)
- You're working with component-specific logic

## Visual Comparison: Targeted Messages

```csharp
// Setup: Player GameObject with multiple components
GameObject player = /* ... */;
HealthComponent health = player.GetComponent<HealthComponent>();
UIComponent ui = player.GetComponent<UIComponent>();

// Both components register for Heal on the GameObject
_ = healthToken.RegisterGameObjectTargeted<Heal>(player, health.OnHeal);
_ = uiToken.RegisterGameObjectTargeted<Heal>(player, ui.OnHeal);

// Scenario 1: Target the GameObject
heal.EmitGameObjectTargeted(player);
// Yes health.OnHeal() fires
// Yes ui.OnHeal() fires
// Both components receive it!

// Scenario 2: Target a Component (but registered for GameObject)
heal.EmitComponentTargeted(health);
// No health.OnHeal() does NOT fire (registered for GameObject, not Component)
// No ui.OnHeal() does NOT fire
// Nothing happens! Wrong channel!

// Scenario 3: Register for Component, emit to Component
_ = healthToken.RegisterComponentTargeted<Heal>(health, health.OnHeal);
heal.EmitComponentTargeted(health);
// Yes health.OnHeal() fires
// No ui.OnHeal() does NOT fire (different component)
```

## Broadcast Messages: Same Rules Apply

Broadcast messages follow the exact same GameObject vs Component rules:

```csharp
[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct TookDamage { public readonly int amount; }

// Broadcast from GameObject
var damage = new TookDamage(5);
damage.EmitGameObjectBroadcast(gameObject);

// Register for broadcasts from this GameObject
_ = token.RegisterGameObjectBroadcast<TookDamage>(gameObject, OnDamage);
// Yes OnDamage fires when damage.EmitGameObjectBroadcast(gameObject) is called

// Register for broadcasts from this Component
_ = token.RegisterComponentBroadcast<TookDamage>(enemyComponent, OnComponentDamage);
// No OnComponentDamage does NOT fire (registered for Component, but emitted from GameObject)
```

## The `this` Trap

**Most common mistake:** Using `this` in a MonoBehaviour and forgetting it's a Component, not a GameObject.

```csharp
public class Enemy : MonoBehaviour
{
    void Start()
    {
        // No WRONG: Registered for GameObject
        _ = token.RegisterGameObjectTargeted<TakeDamage>(gameObject, OnDamage);
    }

    void TakeDamageFrom(GameObject attacker)
    {
        var damage = new TakeDamage(10);
        // No WRONG: Emitting to Component (this)
        damage.EmitAt(this);  // WON'T BE RECEIVED!
    }

    void OnDamage(ref TakeDamage msg) { }
}

// FIX 1: Both use GameObject
_ = token.RegisterGameObjectTargeted<TakeDamage>(gameObject, OnDamage);
damage.EmitAt(gameObject);  // Yes Works!

// FIX 2: Both use Component
_ = token.RegisterComponentTargeted<TakeDamage>(this, OnDamage);
damage.EmitAt(this);  // Yes Works!
```

**Remember:** In Unity, `this` inside a `MonoBehaviour` is **always** a Component, never a GameObject!

## Listening to ALL Events (Global Observers)

**Feature:** DxMessaging allows listening to all targeted or broadcast messages **without knowing the specific target or source**.

This is useful for:

- **Analytics** -- Track every action in your game without coupling to individual objects
- **Debugging** -- See all events of a type in one place
- **Cross-cutting concerns** -- Achievements, logging, VFX that respond to any entity's events

### Why this is different from classic event buses

| Approach                 | Classic Event Bus             | DxMessaging Global Observers       |
| ------------------------ | ----------------------------- | ---------------------------------- |
| **Subscriptions needed** | One per entity type           | One for ALL entities               |
| **Coupling**             | Tight (know all entity types) | Zero (no knowledge needed)         |
| **New entity types**     | Update all subscribers        | Zero changes needed                |
| **Context**              | Lost (who was damaged?)       | Provided (source/target parameter) |

### Classic Event Bus Anti-Pattern

```csharp
// No Traditional approach: Tight coupling, multiple subscriptions
EventBus.PlayerDamaged += OnPlayerDamaged;
EventBus.EnemyDamaged += OnEnemyDamaged;
EventBus.NPCDamaged += OnNPCDamaged;
EventBus.BossDamaged += OnBossDamaged;
// Add new entity? Update EVERY analytics/logging system!

void OnPlayerDamaged(int amount) { RecordDamage("Player", amount); }
void OnEnemyDamaged(int amount) { RecordDamage("Enemy", amount); }
void OnNPCDamaged(int amount) { RecordDamage("NPC", amount); }
void OnBossDamaged(int amount) { RecordDamage("Boss", amount); }
```

### DxMessaging Global Observer Pattern

```csharp
// Yes DxMessaging: One subscription, zero coupling
_ = token.RegisterBroadcastWithoutSource<TookDamage>(OnAnyDamage);

void OnAnyDamage(ref InstanceId source, ref TookDamage msg)
{
    // Works for Player, Enemy, NPC, Boss, and any future entity type!
    RecordDamage(source, msg.amount);
}
// Add new entity? Zero changes needed.
```

### Listening to All Targeted Messages

Use `RegisterTargetedWithoutTargeting` to receive ALL targeted messages of a type, regardless of target:

```csharp
[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct Heal { public readonly int amount; }

// Listen to ALL heal messages, no matter who they're targeted at
_ = token.RegisterTargetedWithoutTargeting<Heal>(OnAnyHeal);

void OnAnyHeal(ref InstanceId target, ref Heal msg)
{
    Debug.Log($"Someone healed {target} for {msg.amount}");
    // You get the target as a parameter so you know WHO was healed
}

// Now when ANYONE emits a heal...
heal.EmitAt(player);    // OnAnyHeal fires with target = player
heal.EmitAt(enemy);     // OnAnyHeal fires with target = enemy
heal.EmitAt(npc);       // OnAnyHeal fires with target = npc
```

#### Use cases

- Analytics ("track all damage dealt")
- Debugging ("log every heal in the game")
- Achievements ("count total kills")
- Global UI updates ("show floating damage numbers for any entity")

### Listening to All Broadcast Messages

Use `RegisterBroadcastWithoutSource` to receive ALL broadcast messages of a type, regardless of source:

```csharp
[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct TookDamage { public readonly int amount; }

// Listen to ALL damage events from any source
_ = token.RegisterBroadcastWithoutSource<TookDamage>(OnAnyDamage);

void OnAnyDamage(ref InstanceId source, ref TookDamage msg)
{
    Debug.Log($"{source} took {msg.amount} damage");
    // You get the source as a parameter so you know WHO took damage
}

// When this component broadcasts damage...
damage.EmitFrom(gameObject); // OnAnyDamage fires with source = gameObject
```

Each other component that emits the same message uses its own `gameObject` as the source, so the
global listener still receives damage from every object.

#### Use cases

- Combat logs ("record all damage in the scene")
- Particle effects ("spawn blood VFX for any damage")
- Achievement tracking ("count enemy deaths")
- Analytics dashboards ("total damage per second")

### Specific vs Global Listeners: Both Can Coexist

You can have BOTH specific listeners AND global listeners for the same message:

```csharp
// Specific listener: only cares about this source
_ = playerToken.RegisterGameObjectBroadcast<TookDamage>(gameObject, OnDamage);

// Global listener: cares about ALL damage
_ = analyticsToken.RegisterBroadcastWithoutSource<TookDamage>(OnAnyDamage);

// When this object takes damage
damage.EmitFrom(gameObject);
// Yes OnDamage fires (specific listener)
// Yes OnAnyDamage fires (global listener)
// Both fire!
```

If a different object emits the same message from its own `gameObject`, the global listener still
fires, while the source-specific listener above does not.

### Real-World Example: Combat System

Here's how to combine specific and global listeners in a combat system:

```csharp
public class Player : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        // Listen only to damage targeting THIS player
        _ = Token.RegisterGameObjectBroadcast<TookDamage>(gameObject, OnPlayerTookDamage);
    }

    void OnPlayerTookDamage(ref TookDamage msg)
    {
        // Player-specific logic: update health bar, play hurt sound
        health -= msg.amount;
        PlayHurtSound();
    }
}

public class CombatLog : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        // Listen to ALL damage from ANY source (global observer)
        _ = Token.RegisterBroadcastWithoutSource<TookDamage>(OnAnyDamage);
    }

    void OnAnyDamage(ref InstanceId source, ref TookDamage msg)
    {
        // Global combat logging
        LogToFile($"{Time.time}: {source} took {msg.amount} damage");
    }
}

public class AchievementSystem : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        // Listen to ALL damage targeting ANY entity (global observer)
        _ = Token.RegisterTargetedWithoutTargeting<DealDamage>(OnAnyDamageDealt);
    }

    void OnAnyDamageDealt(ref InstanceId target, ref DealDamage msg)
    {
        // Track total damage dealt for achievements
        totalDamageDealt += msg.amount;
        CheckDamageAchievements();
    }
}
```

### Performance Note

Global listeners (`RegisterTargetedWithoutTargeting` / `RegisterBroadcastWithoutSource`) are **slightly slower** than specific listeners because they receive every message of that type. This is usually negligible, but avoid them in hot paths if you're emitting thousands of messages per frame.

#### Rule of thumb

- For gameplay (< 100 messages/frame): Use freely
- For analytics/debugging: Perfect use case
- For tight loops (> 1000 messages/frame): Prefer specific listeners

## When to Use Which

### GameObject Context

#### Use when

- The whole object is the logical target/source (e.g., "Player was healed", "Enemy took damage")
- Multiple components should coordinate under the same address
- You don't care which specific component handles it
- You want flexibility for future refactoring (adding/removing components)

##### Examples

- Combat: "Deal damage to this enemy"
- UI: "Update all panels related to this character"
- State: "Notify all systems that this entity died"

### Component Context

#### Use when

- The message is explicitly for one component's responsibility (e.g., "open JUST this DoorController")
- Multiple components on the same GameObject must be independently addressable
- You need pinpoint precision
- You have a direct reference to the target component

##### Examples

- UI: "Update this specific health bar, not the mana bar"
- Animation: "Tell this animator to play a clip"
- Physics: "Toggle this specific collider"

## Best Practices

### Do's

Yes **Pick a context and be consistent** across emitters and listeners for that message type
Yes **Prefer GameObject when in doubt** -- easier coordination among components on the same object
Yes **Use explicit helpers** (`EmitGameObjectTargeted`, `RegisterGameObjectTargeted`) to make intent clear
Yes **Document** which context your message types use (add it to your message comments)
Yes **Use global listeners** for analytics, debugging, and cross-cutting concerns

### Don'ts

No **Don't emit to GameObject** and expect Component-registered handlers to receive it (and vice versa)
No **Don't assume `this` means GameObject** -- it's always a Component in Unity
No **Don't mix GameObject/Component** for the same message type across your codebase
No **Don't register the same handler under both** unless you intend to handle both contexts
No **Don't use global listeners** in performance-critical tight loops (thousands of messages/frame)

## Troubleshooting

### "My handler isn't firing!"

**Most likely cause:** GameObject/Component mismatch

```csharp
// Check 1: Are you emitting and registering on the same type?
// No WRONG
_ = token.RegisterGameObjectTargeted<Heal>(gameObject, OnHeal);
heal.EmitAt(this);  // Component, not GameObject!

// Yes CORRECT
_ = token.RegisterGameObjectTargeted<Heal>(gameObject, OnHeal);
heal.EmitAt(gameObject);  // Both GameObject
```

#### Other causes

1. Handler not enabled (call `token.Enable()` in `OnEnable`)
1. Registration happened after emission
1. Using wrong bus (local vs global)

See [Troubleshooting](../reference/troubleshooting.md) for more debugging tips.

## Quick Reference

| Operation              | GameObject Variant                             | Component Variant                              |
| ---------------------- | ---------------------------------------------- | ---------------------------------------------- |
| **Emit Targeted**      | `msg.EmitGameObjectTargeted(go)`               | `msg.EmitComponentTargeted(comp)`              |
| **Emit Broadcast**     | `msg.EmitGameObjectBroadcast(gameObject)`      | `msg.EmitComponentBroadcast(this)`             |
| **Register Targeted**  | `RegisterGameObjectTargeted<T>(go, handler)`   | `RegisterComponentTargeted<T>(comp, handler)`  |
| **Register Broadcast** | `RegisterGameObjectBroadcast<T>(go, handler)`  | `RegisterComponentBroadcast<T>(comp, handler)` |
| **Global Targeted**    | `RegisterTargetedWithoutTargeting<T>(handler)` | (Same -- no distinction)                       |
| **Global Broadcast**   | `RegisterBroadcastWithoutSource<T>(handler)`   | (Same -- no distinction)                       |

## See Also

- **[Emit Shorthands](../advanced/emit-shorthands.md)** -- Concise ways to emit messages
- **[Message Types](message-types.md)** -- Understanding Untargeted, Targeted, and Broadcast
- **[Unity Integration](../guides/unity-integration.md)** -- MessageAwareComponent and lifecycle
- **[Quick Reference](../reference/quick-reference.md)** -- API cheat sheet
- **[Troubleshooting](../reference/troubleshooting.md)** -- Solving common issues
