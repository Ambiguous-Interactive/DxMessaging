# Interceptors, Ordering, and Post-Processing

## Snapshot Semantics: Frozen Listener Lists

**IMPORTANT:** DxMessaging uses snapshot semantics for message emissions. When a message is emitted, the system creates a snapshot of all current listeners (interceptors, handlers, and post-processors). This snapshot is "frozen" for the duration of that emission.

### What this means

- Listeners added during emission will **not** be invoked for the current message
- Newly registered listeners will only become active starting with the **next** emission
- Listeners removed during emission will still complete their execution for the current message
- This behavior applies to all registration types: handlers, interceptors, and post-processors
- This behavior applies to all message categories: Untargeted, Targeted, and Broadcast

One phase boundary is deliberately not frozen. Interceptors run **before** the
handler and post-processor snapshots are taken, so a handler that an interceptor
deregisters is gone before those snapshots exist and does **not** run for the
current message. Deregistering from a handler or a post-processor follows the
frozen rule above; deregistering a handler from an interceptor takes effect
immediately.

#### Example

```csharp
// Handler adds a new listener during emission
_ = token.RegisterUntargeted<GameEvent>(msg => {
    DoWork();
    // This new listener will NOT run for this emission
    _ = token.RegisterUntargeted<GameEvent>(newMsg => ProcessLater());
});

// First emission: only the original handler runs
var firstEvent = new GameEvent();
firstEvent.Emit();  // DoWork() executes, ProcessLater() does NOT

// Second emission: both handlers run
var secondEvent = new GameEvent();
secondEvent.Emit();  // Both DoWork() and ProcessLater() execute
```

##### Why this matters

- Prevents infinite loops (a handler that registers itself won't recurse)
- Guarantees predictable execution order
- Ensures all listeners see a consistent view of the registration state
- Makes debugging and reasoning about message flow easier

This snapshot behavior is extensively tested in the [Mutation During Emission tests](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Tests/Runtime/Core/MutationDuringEmissionTests.cs).

---

Execution order (precise)

DxMessaging runs emissions through a fixed pipeline. This section documents the exact order used at runtime for every category. Unless otherwise noted:

- Priority: lower numbers run earlier.
- Same priority: registration order is preserved.
- Within a priority group, fast handlers (by-ref) run before action handlers.
- Each category (Untargeted, Targeted, Broadcast) has its own pipeline.

Untargeted pipeline

1. Interceptors for `T` (ascending priority; within priority by registration order)
1. Global Accept-All Untargeted handlers (in the MessageHandler that registered them)
1. Untargeted handlers for `T` (ascending priority; within priority by registration order)
1. Untargeted Post-Processors for `T` (ascending priority; within priority by registration order)

Targeted pipeline

1. Interceptors for `T` (ascending priority)
1. Global Accept-All Targeted handlers (receive `(target, ITargetedMessage)`)
1. Targeted handlers for `T` registered for the specific `target`
1. Targeted-Without-Targeting handlers for `T` (listen for all targets)
1. Targeted Post-Processors for `T` registered for the specific `target`
1. Targeted-Without-Targeting Post-Processors for `T` (listen for all targets)

Broadcast pipeline

1. Interceptors for `T` (ascending priority)
1. Global Accept-All Broadcast handlers (receive `(source, IBroadcastMessage)`)
1. Broadcast handlers for `T` registered for the specific `source`
1. Broadcast-Without-Source handlers for `T` (listen for all sources)
1. Broadcast Post-Processors for `T` registered for the specific `source`
1. Broadcast-Without-Source Post-Processors for `T` (listen for all sources)

Notes on handler groups

- Fast vs Action: At a given priority, fast handlers (by-ref delegates) are invoked before action handlers, and within each group the registration order is preserved.
- "Without Targeting/Source" registrations run in their own groups and do not replace the specific target/source groups.

Visual overview

```mermaid
flowchart LR
  subgraph Untargeted["Untargeted Messages"]
    direction TB
    U1["Interceptors(T)"] --> U2[Global Accept-All Untargeted]
    U2 --> U3["Handlers(T)"]
    U3 --> U4["Post-Processors(T)"]
  end

  subgraph Targeted["Targeted Messages"]
    direction TB
    T1["Interceptors(T)"] --> T2[Global Accept-All Targeted]
    T2 --> T3["Handlers(T) @ target"]
    T3 --> T4["Handlers(T) (All Targets)"]
    T4 --> T5["Post-Processors(T) @ target"]
    T5 --> T6["Post-Processors(T) (All Targets)"]
  end

  subgraph Broadcast["Broadcast Messages"]
    direction TB
    B1["Interceptors(T)"] --> B2[Global Accept-All Broadcast]
    B2 --> B3["Handlers(T) @ source"]
    B3 --> B4["Handlers(T) (All Sources)"]
    B4 --> B5["Post-Processors(T) @ source"]
    B5 --> B6["Post-Processors(T) (All Sources)"]
  end

  classDef neutral stroke-width:2px
  class Untargeted,Targeted,Broadcast neutral
  classDef warning stroke-width:2px
  class U1,T1,B1 warning
  classDef accent stroke-width:2px
  class U2,T2,B2 accent
  classDef primary stroke-width:2px
  class U3,T3,T4,B3,B4 primary
  classDef success stroke-width:2px
  class U4,T5,T6,B5,B6 success
```

Example sequence

```mermaid
sequenceDiagram
    participant P as Producer
    participant I as Interceptor(s)
    participant G as Global Accept-All
    participant H as Handler(s)
    participant PP as Post-Processor(s)
    P->>I: emit(ref message)
    I-->>P: false? cancel : continue
    I->>G: message (category-specific)
    G->>H: message
    H->>PP: after all handlers complete
```

Interceptors

- Mutate or cancel messages before any handler runs. Return `false` to cancel.
- Define per category: `RegisterUntargetedInterceptor<T>`, `RegisterTargetedInterceptor<T>`, `RegisterBroadcastInterceptor<T>`.
- Useful for validation, normalization, enrichment, and short-circuiting.

```csharp
using DxMessaging.Core;               // MessageHandler, InstanceId
using DxMessaging.Core.MessageBus;    // IMessageBus

// Cancel <=0 damage and clamp high values
IMessageBus bus = MessageHandler.MessageBus;
MessageBusRegistration registration = bus.RegisterTargetedInterceptor<ApplyDamage>(
    (ref InstanceId target, ref ApplyDamage m) =>
    {
        if (m.amount <= 0)
        {
            return false;
        }
        m = new ApplyDamage(Math.Min(m.amount, 999));
        return true;
    },
    priority: 0
);

// The owning system calls this during teardown.
bus.Deregister<ApplyDamage>(in registration);
```

Calls through `MessageRegistrationToken` are owned by that token. Calls made directly on
`IMessageBus` return raw handles; retain them and call `Deregister<T>(in registration)` with
the exact registered message type when the owner shuts down.

Real-World Use Cases

#### State-Based Message Cancellation

Prevent UI messages from being processed based on current UI state:

```csharp
using System;
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;

[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct OpenMenu
{
    public readonly string menuName;
}

[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct ShowDialog
{
    public readonly string dialogText;
}

public sealed class UIStateManager : IDisposable
{
    private readonly IMessageBus _bus;
    private MessageBusRegistration _openMenuRegistration;
    private MessageBusRegistration _showDialogRegistration;
    private bool _isInCutscene;
    private bool _isPaused = false;
    private bool _isLoading = false;

    public UIStateManager(IMessageBus bus)
    {
        _bus = bus;

        // Block all UI interactions during cutscenes, loading, or when paused
        _openMenuRegistration = bus.RegisterUntargetedInterceptor<OpenMenu>(
            (ref OpenMenu m) => !_isInCutscene && !_isLoading,
            priority: -100  // Run early
        );

        _showDialogRegistration = bus.RegisterUntargetedInterceptor<ShowDialog>(
            (ref ShowDialog m) => !_isInCutscene && !_isPaused,
            priority: -100
        );
    }

    public void Dispose()
    {
        if (_openMenuRegistration.IsValid)
        {
            _bus.Deregister<OpenMenu>(in _openMenuRegistration);
            _openMenuRegistration = MessageBusRegistration.None;
        }
        if (_showDialogRegistration.IsValid)
        {
            _bus.Deregister<ShowDialog>(in _showDialogRegistration);
            _showDialogRegistration = MessageBusRegistration.None;
        }
    }

    public void EnterCutscene() => _isInCutscene = true;
    public void ExitCutscene() => _isInCutscene = false;
}

// Usage: UI messages automatically blocked during cutscenes
using UIStateManager uiManager = new(MessageHandler.MessageBus);
uiManager.EnterCutscene();

var openMenu = new OpenMenu("inventory");
openMenu.Emit();  // Cancelled by interceptor
var showDialog = new ShowDialog("Hello!");
showDialog.Emit();  // Cancelled by interceptor
```

#### Value Clamping and Normalization

Ensure message data stays within valid ranges:

```csharp
using System;
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;
using UnityEngine;

[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct MovementInput
{
    public readonly Vector2 direction;
    public readonly float speed;
}

public sealed class InputNormalizer : IDisposable
{
    private readonly IMessageBus _bus;
    private MessageBusRegistration _registration;

    public InputNormalizer(IMessageBus bus)
    {
        _bus = bus;

        // Normalize and clamp movement input
        _registration = bus.RegisterUntargetedInterceptor<MovementInput>(
            (ref MovementInput m) =>
            {
                // Normalize direction vector
                var normalized = m.direction.normalized;

                // Clamp speed to valid range
                var clampedSpeed = Mathf.Clamp(m.speed, 0f, 10f);

                // Mutate the message with cleaned values
                m = new MovementInput(normalized, clampedSpeed);
                return true;
            },
            priority: 0
        );
    }

    public void Dispose()
    {
        if (!_registration.IsValid)
        {
            return;
        }

        _bus.Deregister<MovementInput>(in _registration);
        _registration = MessageBusRegistration.None;
    }
}

// Usage: all movement input is automatically normalized
using InputNormalizer normalizer = new(MessageHandler.MessageBus);

// Even invalid input gets cleaned
var input = new MovementInput(new Vector2(100, 200), 9999f);
input.Emit();
// Handlers receive: direction=(0.45, 0.89), speed=10.0
```

#### Permission and Authorization Checks

Block messages that violate game rules or permissions:

```csharp
using System;
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;

[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct SpendCurrency
{
    public readonly int amount;
}

[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct UnlockAchievement
{
    public readonly string achievementId;
}

public sealed class PermissionSystem : IDisposable
{
    private readonly IPlayerDataService _playerData;
    private readonly IMessageBus _bus;
    private MessageBusRegistration _spendRegistration;
    private MessageBusRegistration _achievementRegistration;

    public PermissionSystem(IPlayerDataService playerData, IMessageBus bus)
    {
        _playerData = playerData;
        _bus = bus;

        // Block currency spending if player doesn't have enough
        _spendRegistration = bus.RegisterTargetedInterceptor<SpendCurrency>(
            (ref InstanceId playerId, ref SpendCurrency m) =>
            {
                var balance = _playerData.GetCurrencyBalance(playerId);
                if (balance < m.amount)
                {
                    Debug.LogWarning($"Player {playerId} attempted to spend {m.amount} but only has {balance}");
                    return false;  // Cancel the message
                }
                return true;
            },
            priority: -50
        );

        // Block achievement unlocks if already unlocked (idempotency)
        _achievementRegistration = bus.RegisterTargetedInterceptor<UnlockAchievement>(
            (ref InstanceId playerId, ref UnlockAchievement m) =>
            {
                if (_playerData.HasAchievement(playerId, m.achievementId))
                {
                    return false;  // Already unlocked, skip
                }
                return true;
            },
            priority: -50
        );
    }

    public void Dispose()
    {
        if (_spendRegistration.IsValid)
        {
            _bus.Deregister<SpendCurrency>(in _spendRegistration);
            _spendRegistration = MessageBusRegistration.None;
        }
        if (_achievementRegistration.IsValid)
        {
            _bus.Deregister<UnlockAchievement>(in _achievementRegistration);
            _achievementRegistration = MessageBusRegistration.None;
        }
    }
}

// Usage: invalid operations automatically blocked
using PermissionSystem permissions = new(playerDataService, MessageHandler.MessageBus);

// This will be blocked if player doesn't have 100 currency
var spendCurrency = new SpendCurrency(100);
spendCurrency.EmitTargeted(playerId);

// This will be blocked if achievement is already unlocked
var unlockAchievement = new UnlockAchievement("first_boss");
unlockAchievement.EmitTargeted(playerId);
```

#### Message Enrichment and Context Addition

Add contextual data to messages as they flow through the system:

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;
using System;

[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct PlayerAction
{
    public readonly string actionType;
    [DxOptionalParameter]
    public readonly long timestamp;  // Added by interceptor
    [DxOptionalParameter]
    public readonly string sessionId; // Added by interceptor
}

public sealed class TelemetryEnricher : IDisposable
{
    private readonly string _currentSessionId;
    private readonly IMessageBus _bus;
    private MessageBusRegistration _registration;

    public TelemetryEnricher(string sessionId, IMessageBus bus)
    {
        _currentSessionId = sessionId;
        _bus = bus;

        // Enrich player actions with timestamp and session context
        _registration = bus.RegisterTargetedInterceptor<PlayerAction>(
            (ref InstanceId playerId, ref PlayerAction m) =>
            {
                // Add timestamp and session ID to every action
                m = new PlayerAction(
                    m.actionType,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    _currentSessionId
                );
                return true;
            },
            priority: -100  // Run very early to enrich before other interceptors
        );
    }

    public void Dispose()
    {
        if (!_registration.IsValid)
        {
            return;
        }

        _bus.Deregister<PlayerAction>(in _registration);
        _registration = MessageBusRegistration.None;
    }
}

// Usage: messages automatically enriched with context
using TelemetryEnricher enricher = new(Guid.NewGuid().ToString(), MessageHandler.MessageBus);

// Emit without timestamp/session - interceptor adds them
var action = new PlayerAction("jump");
action.EmitTargeted(playerId);
// Handlers receive fully enriched message with timestamp and sessionId
```

#### Cooldown and Rate Limiting

Prevent message spam by enforcing cooldowns:

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;
using System;
using System.Collections.Generic;

[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct CastSpell
{
    public readonly string spellName;
}

public sealed class CooldownManager : IDisposable
{
    private readonly Dictionary<(InstanceId, string), DateTime> _lastCastTimes = new();
    private readonly TimeSpan _globalCooldown = TimeSpan.FromSeconds(1.5);
    private readonly IMessageBus _bus;
    private MessageBusRegistration _registration;

    public CooldownManager(IMessageBus bus)
    {
        _bus = bus;

        _registration = bus.RegisterTargetedInterceptor<CastSpell>(
            (ref InstanceId casterId, ref CastSpell m) =>
            {
                var key = (casterId, m.spellName);
                var now = DateTime.UtcNow;

                if (_lastCastTimes.TryGetValue(key, out var lastCast))
                {
                    if (now - lastCast < _globalCooldown)
                    {
                        // Still on cooldown
                        return false;
                    }
                }

                _lastCastTimes[key] = now;
                return true;
            },
            priority: -10
        );
    }

    public void Dispose()
    {
        if (!_registration.IsValid)
        {
            return;
        }

        _bus.Deregister<CastSpell>(in _registration);
        _registration = MessageBusRegistration.None;
    }
}

// Usage: rapid spell casts automatically throttled
using CooldownManager cooldowns = new(MessageHandler.MessageBus);

var spell1 = new CastSpell("fireball");
spell1.EmitTargeted(playerId);  // Yes Allowed
var spell2 = new CastSpell("fireball");
spell2.EmitTargeted(playerId);  // No Blocked (too soon)
// ... wait 1.5s ...
var spell3 = new CastSpell("fireball");
spell3.EmitTargeted(playerId);  // Yes Allowed
```

When to Use Interceptors

Yes **Good use cases:**

- Input validation and sanitization
- Value clamping and normalization
- Permission and authorization checks
- State-based message filtering
- Rate limiting and cooldown enforcement
- Message enrichment (adding timestamps, session IDs, etc.)
- Early exit for duplicate or redundant messages
- Logging suspicious or invalid message attempts

Warning: **Key principles:**

- **Run before handlers**: Interceptors execute before any type-specific handlers, making them perfect for preprocessing
- **Can mutate**: Unlike post-processors, interceptors can modify message data
- **Can cancel**: Return `false` to prevent the message from reaching handlers
- **Priority matters**: Lower priority values run first (use negative priorities for early interceptors)

No **Avoid for:**

- Read-only observation (use handlers or post-processors instead)
- Actions that should run after message processing (use post-processors)
- Heavy computation that doesn't need to block the message

Post-processors

- Observe after handlers. Use them for logging, metrics, replay records, or follow-up emission.
- Keep gameplay and presentation state changes in handlers. A completed dispatch does not
  reveal whether a handler applied the payload to health, inventory, UI, or another state.
- Post-processors cannot cancel delivery. Use an interceptor when a message must be rejected
  or normalized before handlers run.
- Per category and scope (per target/source or all):
  - Untargeted: `RegisterUntargetedPostProcessor<T>`
  - Targeted (specific): `RegisterTargetedPostProcessor<T>(target, ...)`
  - Targeted (all): `RegisterTargetedWithoutTargetingPostProcessor<T>(...)`
  - Broadcast (specific): `RegisterBroadcastPostProcessor<T>(source, ...)`
  - Broadcast (all): `RegisterBroadcastWithoutSourcePostProcessor<T>(...)`

Global Accept-All

- Register once and observe all messages on a handler.
- Overloads exist for action and fast handlers.
- Runs between interceptors and type-specific handlers.

Related

- [Message Types](message-types.md)
- [Listening Patterns](listening-patterns.md)
