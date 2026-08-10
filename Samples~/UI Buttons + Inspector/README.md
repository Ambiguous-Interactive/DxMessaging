# UI Buttons + Inspector Sample

> **Perfect for:** Seeing DxMessaging work with Unity UI in 60 seconds

## What You'll Learn (No Code Required!)

**Stop writing button click handlers!** This sample shows you how to:

1. **Wire UI Buttons to messages** - directly from the Inspector (drag & drop)
1. **See messages flow in real-time** - watch the Console as you click
1. **Enable diagnostics** - see every message with timestamps and payloads

**Why this matters:** You can add new systems (analytics, audio, achievements) that react to button clicks WITHOUT touching existing code.

---

## Comparison

### Before DxMessaging:

```csharp
public class PlayButton : MonoBehaviour {
    [SerializeField] private GameManager gameManager;   // Coupling
    [SerializeField] private AudioManager audio;        // Coupling
    [SerializeField] private Analytics analytics;       // Coupling

    public void OnClick() {
        gameManager.StartGame();     // Manual call
        audio.PlayClickSound();       // Manual call
        analytics.LogButtonClick();   // Manual call
    }
}
```

#### Every new system = update PlayButton script. Exhausting.

##### With DxMessaging:

```csharp
public class PlayButton : MonoBehaviour {
    public void OnClick() {
        var clicked = new ButtonClicked("Play");
        clicked.Emit();
        // Done! Everything reacts automatically.
    }
}
```

###### GameManager, Audio, and Analytics listen independently without direct references.

## Import & Run (30 Seconds)

### Want to see it immediately?

1. **Window  ->  Package Manager**
1. **Find DxMessaging**  to  Scroll to **Samples**
1. **"UI Buttons + Inspector"**  to  Click **Import**
1. **Navigate to** Assets/Samples/.../UI Buttons + Inspector/
1. **Open the scene**  to  **Press Play**
1. **Click the buttons**  to  Watch Console logs

You are now seeing DxMessaging in action.

## Click-To-Message: The Quick Path

This sample includes `UIButtonEmitter` and `MessagingObserver` components:

- `UIButtonEmitter` lives on a GameObject and exposes a `Click()` method.
- Hook `Click()` to a Unity UI Button's `OnClick` from the Inspector.
- `MessagingObserver` logs incoming messages to the Console during Play Mode.

Steps if you're wiring your own Button:

1. Add a `UIButtonEmitter` component to any GameObject.
1. Select your UI Button in the scene.
1. In the Button's `On Click ()` list, click `+`.
1. Drag the GameObject with `UIButtonEmitter` into the new slot.
1. Choose `UIButtonEmitter -> Click` in the function dropdown.
1. (Optional) Set a friendly `buttonId` in the `UIButtonEmitter` Inspector.
1. Press Play and click the Button  --  watch the Console logs.

## What's Happening Under The Hood

When you click the Button, `UIButtonEmitter.Click()` constructs and emits messages using the simple two-line pattern:

```csharp
var evt = new ButtonClicked(buttonId);
evt.Emit();

var text = new StringMessage($"Clicked {buttonId}");
text.EmitGameObjectTargeted(gameObject);
```

Why two lines? It keeps struct messages explicit and readable, works well with source generators, and mirrors the pattern used throughout the package.

The listener in this sample is `MessagingObserver`, which subclasses `MessageAwareComponent` and registers handlers in one place:

```csharp
protected override void RegisterMessageHandlers()
{
    _ = Token.RegisterUntargeted<ButtonClicked>(OnButtonClicked);
    _ = Token.RegisterGlobalAcceptAll(OnAnyUntargeted, OnAnyTargeted, OnAnyBroadcast);
}
```

Handlers simply `Debug.Log(...)` the messages they receive so you can see traffic as you click.

## Diagnostics (Optional but Handy)

Want to see extra internal info while you interact with the scene?

- Drop the included `DiagnosticsEnabler` component on any GameObject and press Play. It toggles global diagnostics on Start:

```csharp
MessageHandler.MessageBus.DiagnosticsMode = true;
```

- Prefer code? You can enable/disable diagnostics yourself at runtime in any script using the same line.

## Try These Variations

- Multiple buttons: Add more UI Buttons, add more `UIButtonEmitter`s, and give each a unique `buttonId`.
- Targeted vs. untargeted: Notice the sample also sends a targeted `StringMessage` to the emitter's `gameObject`.
- Your own message type: Open [Messages.cs](./Messages.cs) to see how `ButtonClicked` is declared using attributes:

```csharp
[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct ButtonClicked { public readonly string id; }
```

Add your own partial struct next to it, then emit it from `UIButtonEmitter` using the same two-line pattern.

## Troubleshooting

- Button click does nothing: Confirm the Button's `On Click ()` has the `UIButtonEmitter.Click` function assigned, and you're not editing while in Play Mode (changes revert when you exit Play Mode).
- No logs in Console: Make sure a `MessagingObserver` exists in the scene and the Console isn't filtered. Diagnostics are optional  --  basic logs should still appear without them.
- Button doesn't respond: Ensure there's an `EventSystem` in the scene (Unity auto-adds one with UI); make sure the Button is interactable and not occluded by other UI.

## CRITICAL: Inheriting from MessageAwareComponent

### If you extend `MessagingObserver` or create your own scripts deriving from `MessageAwareComponent`:

### Important Guidelines

1. **ALWAYS call `base.RegisterMessageHandlers()` FIRST** in your override:

   ```csharp
   protected override void RegisterMessageHandlers() {
       base.RegisterMessageHandlers();  // <- MUST be first!
       _ = Token.RegisterUntargeted<MyMessage>(OnMyMessage);
   }
   ```

   **Why?** The base class registers essential handlers. Skipping this breaks functionality.

1. **If you override Unity lifecycle methods, call base:**
   - `base.Awake()` - Skip this = token never created = handlers never fire
   - `base.OnEnable()` / `base.OnDisable()` - Skip these = handlers never activate
   - `base.OnDestroy()` - Skip this = potential memory leaks

1. **Use `override`, never `new`:**

   ```csharp
   //  WRONG - This hides the method, doesn't override it
   new void OnEnable() { }

   //  CORRECT - This properly overrides
   protected override void OnEnable() {
       base.OnEnable();
   }
   ```

### Registration Timing: Use Awake, Not Start

- `MessageAwareComponent` calls `RegisterMessageHandlers()` in `Awake()` automatically
- **This is the correct pattern** - handlers are ready before other components' `Start()` methods
- **Don't register in `Start()`** unless you have a specific order-of-execution reason
- Early registration = you won't miss messages from other components

### Common Pitfall Example

```csharp
//  WRONG - Forgot base.RegisterMessageHandlers()
public class MyObserver : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        // Missing base call!
        _ = Token.RegisterUntargeted<ButtonClicked>(OnClick);
    }
}
// Result: String messages won't work, base class handlers missing
```

```csharp
//  CORRECT
public class MyObserver : MessageAwareComponent {
    protected override void RegisterMessageHandlers() {
        base.RegisterMessageHandlers();  // Essential!
        _ = Token.RegisterUntargeted<ButtonClicked>(OnClick);
    }
}
```

**Remember:** Forgetting `base.RegisterMessageHandlers()` or `base.Awake()` is the #1 cause of "handlers not firing" issues.

## Next Steps

- Quick tour: [Getting Started guide](../../docs/getting-started/getting-started.md)
- Patterns and recipes: [Common Patterns](../../docs/guides/patterns.md)
- Explore another sample: [Mini Combat sample](../Mini%20Combat/README.md)

You now have an inspector-first way to publish and observe messages. Build on this by using your own message types and listeners.
