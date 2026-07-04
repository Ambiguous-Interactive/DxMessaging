# DxMessaging Visual Guide for Beginners

If you're brand new to messaging systems, this visual guide will help you understand DxMessaging in minutes.

## What Problem Does It Solve

### The Old Way (Spaghetti Code)

```mermaid
flowchart LR
    Player[Player]
    Enemy[Enemy]
    Inventory[Inventory]
    UI[UI]
    Audio[Audio]

    Player -->|direct ref| UI
    Player -->|direct ref| Audio
    Enemy -->|direct ref| UI
    Enemy -->|direct ref| Audio
    Inventory -->|direct ref| Audio

    classDef danger stroke-width:2px
    class Player,Enemy,Inventory danger
    classDef success stroke-width:2px
    class UI,Audio success
```

#### Problems

- Everyone needs to know everyone else
- Hard to add/remove systems
- Memory leaks from forgotten unsubscribes

### The DxMessaging Way (Clean Separation)

```mermaid
flowchart TB
    Player[Player]
    Enemy[Enemy]
    Inventory[Inventory]
    Bus((Message<br/>Bus))
    UI[UI]
    Audio[Audio]
    Analytics[Analytics]

    Player -->|message| Bus
    Enemy -->|message| Bus
    Inventory -->|message| Bus

    Bus -->|notify| UI
    Bus -->|notify| Audio
    Bus -->|notify| Analytics

    classDef primary stroke-width:2px
    class Player,Enemy,Inventory primary
    classDef warning stroke-width:3px
    class Bus warning
    classDef success stroke-width:2px
    class UI,Audio,Analytics success
```

#### Benefits

- Nobody knows about anyone else
- Easy to add/remove systems
- Automatic cleanup (prevents common leaks)

## The Three Message Types (Simple!)

Think of messages like different kinds of mail:

### 1. Untargeted (Announcement to Everyone)

Like a megaphone announcement in a stadium - everyone hears it.

```csharp
// Define the announcement
[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct GamePaused { }

// Anyone can announce
var msg = new GamePaused();
msg.Emit();

// Anyone can listen
_ = token.RegisterUntargeted<GamePaused>(OnPause);
```

#### Real-world uses

- "Game paused!"
- "Settings changed!"
- "Level loaded!"

### 2. Targeted (Letter to One Person)

Like mailing a letter to a specific address - only that recipient gets it.

```csharp
// Define the letter
[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct Heal { public readonly int amount; }

// Send to specific person
var heal = new Heal(50);
heal.EmitGameObjectTargeted(playerObject);

// Only the player listens
_ = token.RegisterComponentTargeted<Heal>(this, OnHeal);
```

#### Real-world uses

- "Player, heal yourself!"
- "Enemy #3, take damage!"
- "Button, update your text!"

### 3. Broadcast (News from One Source)

Like a news broadcast - comes from one source, anyone can tune in.

```csharp
// Define the news
[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct TookDamage { public readonly int amount; }

// Broadcast from this object
var dmg = new TookDamage(25);
dmg.EmitGameObjectBroadcast(gameObject);

// UI can listen to this source
_ = token.RegisterGameObjectBroadcast<TookDamage>(gameObject, OnThisDamage);

// OR achievement system can listen to ALL enemies
_ = token.RegisterBroadcastWithoutSource<TookDamage>(OnAnyEnemy);
```

#### Real-world uses

- "I (player) took damage!"
- "I (enemy) died!"
- "I (chest) was opened!"

## The Message Journey (Step by Step)

When you send a message, here's what happens:

```mermaid
sequenceDiagram
    participant You as Your Code
    participant Msg as Message
    participant Int as Interceptors<br/>(Optional)
    participant H0 as Handler<br/>priority: 0
    participant H5 as Handler<br/>priority: 5
    participant H10 as Handler<br/>priority: 10
    participant PP as Post-Processors<br/>(Optional)

    Note over You: 1. Create message
    You->>Msg: var heal = new Heal(10);

    Note over You,Msg: 2. Emit message
    You->>Msg: heal.EmitGameObjectTargeted(player);

    Note over Int: 3. Validate & Normalize
    Msg->>Int: Check message
    Int->>Int: Is valid? (>0)<br/>Clamp if needed (<999)
    alt Invalid message
        Int--xMsg: Cancel
    else Valid message
        Int->>H0: Continue
    end

    Note over H0,H10: 4. Execute handlers (by priority)
    H0->>H0: SaveSystem runs first
    H0->>H5: Next priority
    H5->>H5: AudioSystem runs
    H5->>H10: Next priority
    H10->>H10: UISystem runs last

    Note over PP: 5. Analytics & Logging
    H10->>PP: All handlers complete
    PP->>PP: Analytics.Track(...)<br/>Debug.Log(...)
```

### Key points

- **Step 1-2:** You create and emit the message
- **Step 3 (Optional):** Interceptors can validate, modify, or cancel
- **Step 4:** Handlers run in priority order (lower number = earlier)
- **Step 5 (Optional):** Post-processors run after everything (suitable for analytics)

## Your First Message (3 Easy Steps)

### Step 1: Define It

```csharp
using DxMessaging.Core.Attributes;

[DxTargetedMessage]     // <- What kind of message?
[DxAutoConstructor]     // <- Auto-make a constructor
public readonly partial struct Heal {
    public readonly int amount;
}
```

#### What are those `[DxSomething]` tags?

These are **attributes** that work with C# source generators to produce boilerplate code at compile time:

- **`[DxTargetedMessage]`** - Marks this struct as a targeted message and generates the required emit methods
- **`[DxAutoConstructor]`** - Generates a constructor that initializes all fields

For example, `[DxAutoConstructor]` generates this constructor automatically:

```csharp
public Heal(int amount) { this.amount = amount; }
```

**Why `partial`?** The `partial` keyword allows the source generator to add the generated code to your type in a separate file during compilation.

**Want to learn more?** See [Helpers & Source Generation](../reference/helpers.md) for the full explanation!

### Step 2: Listen for It

```csharp
using DxMessaging.Unity;

public class Player : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        // "When someone sends Heal to ME, call OnHeal"
        _ = Token.RegisterComponentTargeted<Heal>(this, OnHeal);
    }

    void OnHeal(ref Heal msg) {
        health += msg.amount;
        Debug.Log($"Healed {msg.amount}!");
    }
}
```

**Automatic:** `MessageAwareComponent` handles all the lifecycle automatically.

- Creates registration in `Awake()`
- Activates in `OnEnable()`
- Deactivates in `OnDisable()`
- Cleans up in `OnDestroy()`

> **Important**: If you override `Awake`, `OnEnable`, `OnDisable`, `OnDestroy`, or `RegisterMessageHandlers` on a `MessageAwareComponent`, call `base.X()` first or your handlers stop working silently. See the [analyzer reference](../reference/analyzers.md#dxmsg006-missing-base-call).

### Step 3: Send It

```csharp
// From anywhere in your code:
var healMsg = new Heal(50);
healMsg.EmitComponentTargeted(playerComponent);

// Player will receive this message.
```

## Common Patterns Visualized

### Pattern: Scene Transition

```mermaid
sequenceDiagram
    participant SM as SceneManager
    participant Bus as Message Bus
    participant Audio as AudioSystem
    participant Save as SaveSystem

    Note over SM: Scene is changing
    SM->>Bus: SceneChanged(sceneIndex: 2)

    Note over Bus: Message broadcast to all listeners

    Bus->>Audio: SceneChanged received
    Audio->>Audio: FadeOutMusic()

    Bus->>Save: SceneChanged received
    Save->>Save: SaveGame()

    Note over Audio,Save: All independent,<br/>no coupling
```

**Why this works:** AudioSystem and SaveSystem don't know about SceneManager or each other. They just listen for `SceneChanged` messages and react independently.

Code:

```csharp
// Define
[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct SceneChanged { public readonly int sceneIndex; }

// Anyone can send
var msg = new SceneChanged(2);
msg.Emit();

// Many can listen independently
_ = audioToken.RegisterUntargeted<SceneChanged>(OnScene);
_ = saveToken.RegisterUntargeted<SceneChanged>(OnScene);
```

### Pattern: Player Input -> Action

```mermaid
sequenceDiagram
    participant Input as InputSystem
    participant Bus as Message Bus
    participant Player as Player

    Note over Input: User presses Space
    Input->>Bus: Jump(force: 10f)<br/>[Targeted to Player]

    Bus->>Player: Jump message received
    Player->>Player: ApplyForce()<br/>rb.AddForce(...)

    Note over Input,Player: Decoupled!<br/>Input doesn't reference Player
```

**Why this works:** InputSystem doesn't need a reference to Player. It just sends a `Jump` message targeted at the player, and the player responds.

Code:

```csharp
// Input system (doesn't know about Player!)
void Update() {
    if (Input.GetKeyDown(KeyCode.Space)) {
        var jump = new Jump(10f);
        jump.EmitComponentTargeted(playerController);
    }
}

// Player (doesn't know about Input system!)
_ = token.RegisterComponentTargeted<Jump>(this, OnJump);
void OnJump(ref Jump msg) {
    rb.AddForce(Vector3.up * msg.force, ForceMode.Impulse);
}
```

### Pattern: Achievement Tracking

```mermaid
sequenceDiagram
    participant E as Enemy
    participant P as Player
    participant C as Chest
    participant Bus as Message Bus
    participant Ach as Achievement System

    Note over Ach: Listens to ALL messages

    E->>Bus: EnemyKilled
    Bus->>Ach: CheckProgress()<br/>UnlockIfReady()

    P->>Bus: LevelCompleted
    Bus->>Ach: CheckProgress()<br/>UnlockIfReady()

    C->>Bus: ChestOpened
    Bus->>Ach: CheckProgress()<br/>UnlockIfReady()

    Note over Ach: Sees EVERYTHING<br/>without coupling!
```

**Why this works:** Achievement System uses `RegisterGlobalAcceptAll()` to observe every message type, tracking progress across the entire game without any system knowing about it.

Code:

```csharp
public class AchievementSystem : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();
        // Listen to EVERYTHING
        _ = Token.RegisterGlobalAcceptAll(
            (ref IUntargetedMessage m) => Check(m),
            (ref InstanceId t, ref ITargetedMessage m) => Check(m),
            (ref InstanceId s, ref IBroadcastMessage m) => Check(m)
        );
    }
}
```

## When to Use Which Message Type

### Use Untargeted When

- Global game state changes (pause, settings, scene load)
- System-wide announcements
- Configuration updates

### Use Targeted When

- Commanding a specific object ("You, do this!")
- UI updates for specific elements
- Direct communication (A -> B)

### Use Broadcast When

- Events others should know about ("I did this!")
- Analytics tracking
- Achievement triggers
- Notifications from specific sources

## Mental Model: Restaurant Analogy

Think of DxMessaging like a restaurant:

### Untargeted = Restaurant Announcement

to "Attention all customers: We're closing in 10 minutes!"

> to -> Everyone hears it

### Targeted = Waiter Delivering Food

to "Order for table 5: Here's your burger"

> to -> Only table 5 gets it

### Broadcast = Customer Calling Waiter

to "Excuse me, I need a refill!" (from table 3)

> to -> Comes from table 3
>
> to -> Any available waiter can respond
>
> to -> Manager might track it for statistics

## Debugging Visualized

DxMessaging has built-in Inspector support!

### MessagingComponent Inspector

#### Message History (last 10)

- `12:34:05 - Heal -> Player (50)`
- `12:34:03 - Jump -> Player`
- `12:34:01 - GamePaused (global)`

##### Registrations

- Heal (priority: 0, 5 calls)
- Jump (priority: 0, 2 calls)
- TookDamage (priority: 10)

## Performance at a Glance

| Metric       | Traditional C# Events | DxMessaging                         |
| ------------ | --------------------- | ----------------------------------- |
| **Speed**    | (baseline)            | (~10ns slower, negligible)          |
| **Memory**   | Can leak!             | Automatic cleanup (struct messages) |
| **Coupling** | Tight coupling        | Zero coupling                       |

**Bottom line:** Slightly slower than raw events, but:

- Prevents common memory leaks
- Zero coupling
- Full observability
- Predictable ordering

## Learning Path

```mermaid
flowchart TD
    Start[START HERE<br/>Read this Visual Guide<br/>5 min]
    Start --> Step2[Try Quick Start example<br/>5 min<br/>Define -> Listen -> Send]
    Step2 --> Step3[Import Mini Combat sample<br/>10 min<br/>See it in action!]
    Step3 --> Step4[Read Common Patterns<br/>15 min<br/>Real-world solutions]
    Step4 --> Step5[Build your first feature!<br/>30 min<br/>You're ready!]

    classDef primary stroke-width:3px
    class Start primary
    classDef success stroke-width:2px
    class Step5 success
    classDef secondary stroke-width:2px
    class Step2,Step3,Step4 secondary
```

## Common Beginner Questions

### "Do I always need MessageAwareComponent?"

**For Unity:** Yes! It's the easiest way. Think of it like `MonoBehaviour` - you inherit from it and it handles all the messy lifecycle stuff automatically.

**For pure C#:** No, you can use `MessageRegistrationToken` directly if you're not in Unity.

**Bottom line:** If you're in Unity, use `MessageAwareComponent`. It handles subscription lifecycle automatically, which can reduce debugging related to memory leaks.

### "Can I send a message to multiple targets?"

**No** - Targeted messages go to ONE specific entity (like mailing a letter to one address).

#### Instead, use

- **Untargeted** if literally everyone should hear it (like a megaphone announcement)
- **Broadcast** if it's from one source and many can observe (like a news broadcast)

##### Example

```csharp
// DON'T: Try to target multiple entities
msg.EmitComponentTargeted(player1);
msg.EmitComponentTargeted(player2);  // Feels wrong, right?

// DO: Use broadcast so everyone can listen
msg.EmitGameObjectBroadcast(gameObject);  // Now anyone can observe this source
```

### "What if I forget to unsubscribe?"

#### The system handles cleanup automatically

When your component is destroyed, DxMessaging cleans up registrations for you. No `OnDestroy()` needed. This reduces the likelihood of common memory leak patterns.

#### Old way (easy to forget)

```csharp
void OnEnable() { GameManager.OnScoreChanged += Update; }
void OnDisable() { GameManager.OnScoreChanged -= Update; }  // Forgot this? LEAK!
```

##### DxMessaging way (automatic management)

```csharp
protected override void RegisterMessageHandlers() {
    _ = Token.RegisterUntargeted<ScoreChanged>(Update);
}
// Automatic cleanup when component is destroyed.
```

### "Is it slower than regular events?"

**Barely** (~10ns per handler = 0.00001 milliseconds).

#### Benchmark comparison

- Regular C# event: ~50ns
- DxMessaging: ~60ns
- Difference: ~10ns per invocation

You get automatic lifecycle, automatic cleanup, full observability, and predictable ordering for approximately 20% overhead compared to direct method calls.

### "Can I cancel a message?"

#### Yes! That's what interceptors are for

```csharp
// Cancel invalid damage
_ = token.RegisterBroadcastInterceptor<TookDamage>(
    (ref InstanceId source, ref TookDamage msg) => {
        if (msg.amount <= 0) return false;  // Cancel invalid damage
        if (IsInvincible(source)) return false;  // Cancel during invincibility
        return true;  // Allow
    }
);
```

##### Real-world uses

- Block input during cutscenes
- Cancel damage when invincible
- Prevent cheating (clamp values)
- Enforce game rules globally

### "Can I see what messages are firing?"

#### Yes! Open any component in the Inspector and scroll down

You'll see:

- Message history (last 50 messages with timestamps)
- Active registrations (what you're listening to)
- Call counts (how many times each handler ran)

**No more guessing.** You can literally see your event flow in real-time.

## Quick Checklist: Am I Doing It Right

- [ ] Using `MessageAwareComponent` for Unity components?
- [ ] Defining messages as `readonly struct`?
- [ ] Using `[DxAutoConstructor]` to avoid boilerplate?
- [ ] Storing struct in variable before emitting?
- [ ] Choosing the right message type (Untargeted/Targeted/Broadcast)?
- [ ] Using GameObject/Component emit helpers?

If you checked all these, you are following best practices.

## Next Steps

Ready for more?

1. **[Mental Model](../concepts/mental-model.md)** - Understand the philosophy
1. **[Getting Started Guide](getting-started.md)** - Full guide with more details
1. **[Common Patterns](../guides/patterns.md)** - Real-world examples
1. **[Message Types](../concepts/message-types.md)** - When to pick Untargeted, Targeted, or Broadcast
1. **[Diagnostics](../guides/diagnostics.md)** - Master the Inspector tools

---

**Summary:** DxMessaging provides a structured approach to inter-component communication. You define the message, specify recipients, and the system handles delivery.
