# Migration Guide: Adopting DxMessaging Incrementally

This guide helps you introduce DxMessaging into an existing Unity project **gradually and pragmatically**. You don't need to rewrite everything at once.

## Philosophy: Start Small, Prove Value

### Don't do this

- Rip out all C# events and rewrite everything
- Force the whole team to learn it before trying it
- Commit to full adoption before seeing benefits

#### Do this instead

- Pick ONE system to migrate (low risk, high visibility)
- Let old and new approaches coexist
- Expand usage as team comfort grows
- Evaluate after each migration step

## Phase 0: Install and Experiment (1-2 hours)

### Goal: Get comfortable without touching production code

1. **Install DxMessaging** via Package Manager
1. **Read the [Visual Guide](../getting-started/visual-guide.md)** (5 minutes)
1. **Import the Mini Combat sample** from Package Manager
1. **Create a throwaway test scene** and try:

   ```csharp
   [DxUntargetedMessage]
   [DxAutoConstructor]
   public readonly partial struct TestMessage { public readonly int value; }

   public class TestListener : MessageAwareComponent {
       protected override void RegisterMessageHandlers() {
           base.RegisterMessageHandlers();
           _ = Token.RegisterUntargeted<TestMessage>(OnTest);
       }
       void OnTest(ref TestMessage m) => Debug.Log($"Got {m.value}");
   }

   // In another script:
   var msg = new TestMessage(42);
   msg.Emit();
   ```

**Success criteria:** You understand the basic flow and have no build errors.

## Phase 1: Add to a New Feature (1 week)

### Goal: Prove value without refactoring existing code

#### Best candidates for first adoption

- **New UI system** - Add a new settings menu that reacts to game state
- **Achievement/analytics system** - Listen to existing events without coupling
- **New game mode** - Implement it with DxMessaging from scratch

### Example: Adding an Achievement System

```csharp
// 1. Define messages for interesting events (don't touch existing code yet)
[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct EnemyKilled {
    public readonly string enemyType;
    public readonly int playerLevel;
}

// 2. Make your NEW achievement system listen
public class AchievementSystem : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        _ = Token.RegisterBroadcastWithoutSource<EnemyKilled>(OnEnemyKilled);
    }

    void OnEnemyKilled(InstanceId source, EnemyKilled msg) {
        // Track kills, unlock achievements
        if (msg.enemyType == "Boss") UnlockAchievement("BossSlayer");
    }
}

// 3. Bridge from existing code (minimal change)
public class Enemy : MonoBehaviour {
    public event Action OnDied; // OLD - keep for now

    void Die() {
        OnDied?.Invoke(); // OLD code still works

        // NEW: Emit DxMessage too
        var msg = new EnemyKilled(enemyType, PlayerStats.Level);
        msg.EmitGameObjectBroadcast(gameObject);
    }
}
```

#### Why this works

- Old code still works (zero risk)
- New system is decoupled
- Team sees immediate value (achievements without wiring)
- Easy to roll back if needed

## Phase 2: Migrate High-Pain Areas (2-4 weeks)

### Goal: Replace the systems causing the most problems

#### High-value migration targets

1. **UI that references too many systems** - Replace with message listeners
1. **Global static event buses** - Convert to DxMessaging
1. **Memory-leak prone event chains** - Eliminate manual unsubscribe

### Strategy: Parallel Paths During Transition

#### Step 1: Add DxMessages alongside existing events

```csharp
// Old event (keep for now)
public event Action<int> OnHealthChanged;

// New message
[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct HealthChanged { public readonly int newHealth; }

void TakeDamage(int amount) {
    health -= amount;

    // Fire both during migration
    OnHealthChanged?.Invoke(health);  // OLD
    var msg = new HealthChanged(health);
    msg.EmitGameObjectBroadcast(gameObject);  // NEW
}
```

#### Step 2: Migrate listeners one-by-one

```csharp
// OLD listener (comment out when ready)
// void Awake() { player.OnHealthChanged += UpdateBar; }
// void OnDestroy() { player.OnHealthChanged -= UpdateBar; }

// NEW listener
public class HealthBar : MessageAwareComponent {
    [SerializeField] private GameObject playerObject;

    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        _ = Token.RegisterGameObjectBroadcast<HealthChanged>(playerObject, OnHealthChanged);
    }

    void OnHealthChanged(ref HealthChanged msg) => UpdateBar(msg.newHealth);
}
```

#### Step 3: Remove old events once all listeners migrated

```csharp
// Delete after confirming no one uses it:
// public event Action<int> OnHealthChanged; L

void TakeDamage(int amount) {
    health -= amount;
    var msg = new HealthChanged(health);
    msg.EmitGameObjectBroadcast(gameObject);  // Only this now
}
```

### Migration Checklist Template

Use this for each system you migrate:

```text
System: _________________

[ ] Identified all listeners to migrate
[ ] Defined DxMessages for all events
[ ] Added DxMessage emissions (parallel with old events)
[ ] Migrated listeners one-by-one
[ ] Tested thoroughly
[ ] Removed old event declarations
[ ] Updated documentation/comments
```

## Phase 3: Adopt for All New Code (Ongoing)

### Goal: Make DxMessaging the default for new features

#### Team guidelines

- All new cross-system communication uses DxMessaging
- Old code migrates opportunistically (when touched)
- Code reviews check for messaging best practices

##### Example team policy

```text
When to use DxMessaging (for new code):
- Any UI listening to game state -> DxMessaging
- Any analytics/logging -> DxMessaging
- Any cross-scene communication -> DxMessaging
- Any event with 2+ listeners -> DxMessaging

When to use direct references/events:
- Simple UI button -> method call (use UnityEvents)
- Single listener, same GameObject -> direct reference
- Private implementation details -> keep internal
```

## Memory reclamation in 3.0

DxMessaging 3.0 adds a memory-reclamation subsystem that resets empty
per-message-type and per-context slots so long-running sessions do not
retain a slot for every message type or `InstanceId` ever touched. The
2.x dispatch surface is unchanged; the new pieces are opt-in tuning and
diagnostics.

### What is new

- **Optional `DxMessagingRuntimeSettings` asset.** A ScriptableObject loaded
  via `Resources.Load<DxMessagingRuntimeSettings>("DxMessagingRuntimeSettings")`.
  Use `Assets > Create > Wallstop Studios > DxMessaging > Runtime Settings (in Resources)`
  to drop the asset under `Assets/Resources/`. Without an asset the runtime
  hands out a defaulted instance, so the 2.x out-of-the-box behavior is
  preserved.
- **New `Trim` and `TrimAll` API.** `IMessageBus.Trim(bool force = false)`
  and the convenience wrapper `MessageHandler.TrimAll(bool force = false)`
  reclaim empty slots and pooled collections synchronously. Both return a
  `TrimResult` reporting how much was reclaimed.
- **New `OccupiedTypeSlots` and `OccupiedTargetSlots` counters on
  `IMessageBus`.** Aggregated read-only counters for use in diagnostics and
  leak-watching tests.

### Backward compatibility

- The defaults match 2.x behavior with no asset present: idle eviction is
  on with a 30 second threshold, the explicit Trim API is on, pool retention
  is LRU at 512 entries.
- No existing dispatch, registration, or interceptor code changes. Active
  registrations are never reclaimed; the sweep only resets empty slots.
- The settings asset hot-reloads through `DxMessagingRuntimeSettings.SettingsChanged`.
  Existing buses re-apply caps without recreation, so editing the asset
  during Play mode does not invalidate registrations.

### When to adopt

- **Shipped titles or dedicated servers running for hours.** Drop the asset
  in to bound retained slot memory; tune `IdleEvictionSeconds` and
  `BufferMaxDistinctEntries` for the workload.
- **Editor sessions across many scene loads.** Call
  `MessageHandler.TrimAll(force: true)` at scene unload to keep the
  occupancy counters honest.
- **Leak diagnosis.** Snapshot `OccupiedTypeSlots` / `OccupiedTargetSlots`,
  run the operation, force a trim, then compare. Surviving slots correspond
  to active registrations.

For tuning, scenario tables, and worked examples, see the
[Memory Reclamation guide](memory-reclamation.md). For asset parameters,
defaults, and the full diagnostic API, see the
[Runtime Settings reference](../reference/runtime-settings.md).

## Coexistence Patterns

### Pattern 1: Event-to-Message Bridge

```csharp
public class LegacyBridge : MonoBehaviour {
    [SerializeField] private LegacySystem legacySystem;

    void Awake() {
        // Old system fires event, we convert to message
        legacySystem.OnSomethingHappened += (args) => {
            var msg = new SomethingHappened(args);
            msg.Emit();
        };
    }
}
```

### Pattern 2: Message-to-Event Bridge

```csharp
public class ModernBridge : MessageAwareComponent {
    public event Action<int> LegacyEvent; // For old code that needs events

    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        _ = Token.RegisterUntargeted<NewMessage>(OnMessage);
    }

    void OnMessage(ref NewMessage msg) {
        LegacyEvent?.Invoke(msg.value); // Fire old event
    }
}
```

### Pattern 3: Gradual GameObject Migration

```csharp
// Phase 1: Keep old inspector references, emit messages
public class Player : MonoBehaviour {
    [SerializeField] private HealthBar healthBar; // OLD - will remove later

    void TakeDamage(int amount) {
        health -= amount;
        healthBar.UpdateHealth(health); // OLD direct call
        var msg = new HealthChanged(health);
        msg.EmitGameObjectBroadcast(gameObject); // NEW message
    }
}

// Phase 2: Remove direct references
public class Player : MonoBehaviour {
    // [SerializeField] private HealthBar healthBar; -> DELETED

    void TakeDamage(int amount) {
        health -= amount;
        var msg = new HealthChanged(health);
        msg.EmitGameObjectBroadcast(gameObject); // Only this
    }
}
```

## What to Migrate First vs. Last

### Migrate FIRST (High Value, Low Risk)

1. **New systems** - No refactor needed, immediate win
1. **Analytics/logging** - Decoupled observers, zero disruption
1. **UI that needs to listen to many systems** - Eliminate reference spaghetti
1. **Global event buses** - Direct replacement, clear improvement

### Migrate LATER (Lower Priority)

1. **Stable, working code** - If it ain't broke, don't rush
1. **Performance-critical paths** - Validate overhead first
1. **Code that rarely changes** - Low ROI for migration
1. **Third-party integrations** - Keep adapters simple

### DON'T Migrate (Keep As-Is)

1. **Simple button onClick -> method** - UnityEvents are fine
1. **Private implementation details** - Internal events are okay
1. **Single-listener, same-GameObject** - Direct references are clearer
1. **Legacy systems about to be deleted** - Why bother?

## Common Migration Pitfalls

### L Pitfall 1: Boiling the Ocean

**Problem:** "Let's rewrite the entire codebase!"

**Solution:** Migrate incrementally. Set a rule: "One system per sprint" or "New features only."

### L Pitfall 2: No Rollback Plan

**Problem:** Full commit before proving value.

**Solution:** Keep old code commented for 1-2 sprints:

```csharp
// OLD (keep until 2024-02-01)
// player.OnHealthChanged += UpdateBar;

// NEW
_ = Token.RegisterBroadcast<HealthChanged>(...);
```

### L Pitfall 3: Mixing Message Types Incorrectly

**Problem:** Using Untargeted for everything because it's "simpler."

**Solution:** Follow message type guidelines:

- Global state? Untargeted
- Command to one? Targeted
- Event from one? Broadcast

### L Pitfall 4: Over-Messaging

**Problem:** Converting every method call to a message.

**Solution:** Keep simple things simple:

```csharp
// L OVERKILL - Just call the method!
var msg = new CloseDoorMessage(doorId);
msg.Emit();

// BETTER - Direct reference is fine
door.Close();
```

### L Pitfall 5: Not Training the Team

**Problem:** Team doesn't understand when/how to use it.

#### Solution

- Schedule a 30-minute walkthrough
- Share the [Visual Guide](../getting-started/visual-guide.md)
- Pair program on first migrations
- Document team conventions in your wiki

## Success Metrics

Track these to validate migration is worthwhile:

### Quantitative

- Lines of event subscribe/unsubscribe code removed
- Number of SerializedField references eliminated
- Memory leaks fixed (profiler)

#### Qualitative

- Time to add new observers (before/after)
- Ease of debugging message flow
- Team satisfaction (survey)

##### Example

to "Before: Adding achievement tracking required touching 12 files.
to After: Added achievement system with zero changes to existing code."

## Timeline Examples

### Small Project (10k lines)

- **Week 1:** Experiment + add to one new feature
- **Week 2-3:** Migrate high-pain UI systems
- **Week 4+:** New code uses DxMessaging

### Medium Project (50k lines)

- **Month 1:** Pilot with 2-3 systems
- **Month 2-4:** Gradual migration of problem areas
- **Month 5+:** Standard practice for new code

### Large Project (100k+ lines)

- **Quarter 1:** Pilot + evangelize
- **Quarter 2-3:** Migrate critical systems
- **Quarter 4+:** Opportunistic refactors

## Getting Team Buy-In

### For Managers

- "Reduces memory leaks and hard-to-debug issues"
- "Faster feature development (decoupled systems)"
- "Easier onboarding (clear message contracts)"

### For Developers

- "No more manual unsubscribe hell"
- "Built-in debugging (Inspector shows message history)"
- "Add features without touching existing code"

### For QA

- "Easier to reproduce bugs (message logs)"
- "Fewer null reference errors"
- "Clear system boundaries"

## FAQ: Migration Edition

### "Do we need to migrate everything?"

**No!** DxMessaging coexists happily with C# events, UnityEvents, and direct references. Migrate what benefits, leave what works.

### "What if we decide it's not for us?"

Keep old events during migration. If you hate it, delete the DxMessaging parts and uncomment the old code.

### "How do we handle prefabs with Inspector references?"

Phase them out gradually:

1. Keep references during transition
1. Emit messages alongside old calls
1. Migrate listeners
1. Remove references in next refactor pass

### Adopt the DI Registration Builder

Once the bus/provider abstractions are in place, wire listeners through the registration builder instead of hand-rolling handler lifecycles. Benefits:

- Container-managed lifetimes (`IDisposable`, `IInitializable`, `IStartable`, etc.) automatically enable/disable registrations.
- Centralises diagnostics toggles and message bus selection.
- Keeps MonoBehaviours and pure C# services on the same path.

```csharp
public sealed class InventoryService : IStartable, IDisposable
{
    private readonly MessageRegistrationLease lease;

    public InventoryService(IMessageRegistrationBuilder registrationBuilder)
    {
        lease = registrationBuilder.Build(new MessageRegistrationBuildOptions
        {
            Configure = token =>
            {
                _ = token.RegisterUntargeted<InventoryChanged>(OnInventoryChanged);
            }
        });
    }

    public void Start()
    {
        lease.Activate();
    }

    public void Dispose()
    {
        lease.Dispose();
    }

    private static void OnInventoryChanged(ref InventoryChanged message)
    {
        // respond to updates
    }
}
```

- **Unity scene code:** Call `MessagingComponent.CreateRegistrationBuilder()` during dependency injection and share the lease across helper services or pooled objects.
- **Container shims:** Define `ZENJECT_PRESENT`, `VCONTAINER_PRESENT`, or `REFLEX_PRESENT` to enable the built-in installers/extensions that register the builder automatically when those frameworks are present.
- **Tests:** Prefer the builder to create isolated tokens tied to the test fixture lifecycle.

### "Should we migrate tests?"

Yes! Tests benefit from isolated message buses:

```csharp
var testBus = new MessageBus();
var token = MessageRegistrationToken.Create(handler, testBus);
// Test in isolation
```

### "What about mobile performance?"

Enable diagnostics only in Editor:

```csharp
// DiagnosticsTarget.Editor scopes diagnostics to the Unity Editor automatically;
// player builds leave them off, so no #if UNITY_EDITOR guard is needed.
IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Editor;
```

Profile early, measure impact.

## Next Steps

1. **Try Phase 0** - Install and experiment (today)
1. **Pick one system** - Choose a low-risk, high-value target (this week)
1. **Timebox it** - Give yourself 2 weeks to evaluate
1. **Measure results** - Did it make life better?
1. **Expand or abort** - Based on evidence, not hope

**Remember:** Migration is a journey, not a destination. Go at your own pace.

---

**Questions?** See [FAQ](../reference/faq.md) | **Need patterns?** See [Common Patterns](patterns.md)
