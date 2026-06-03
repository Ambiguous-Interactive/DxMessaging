# Quick Start - Your First Message in 5 Minutes

[Back to Index](index.md) | [Getting Started](getting-started.md) | [Visual Guide](visual-guide.md) | [Samples](https://github.com/Ambiguous-Interactive/DxMessaging/tree/master/Samples~)

---

**Goal:** Get a working message system in 5 minutes. Copy, paste, run.

**Stuck?** -> [Troubleshooting](../reference/troubleshooting.md) | [FAQ](../reference/faq.md)

---

## Step 0: Install (30 seconds)

Unity Package Manager -> Add package from git URL:

```text
https://github.com/Ambiguous-Interactive/DxMessaging.git
```

**Requirements:** Unity 2021.3+ | .NET Standard 2.1 | All render pipelines supported

---

## Your First Message (3 Steps)

### Step 1: Define messages

```csharp
using DxMessaging.Core.Messages;
using DxMessaging.Core.Attributes;

// Recommended: attributes + auto constructor (beginner-friendly)
[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct WorldRegenerated
{
    public readonly int seed;
}

[DxUntargetedMessage]
[DxAutoConstructor]
public readonly partial struct VideoSettingsChanged
{
    public readonly int width;
    public readonly int height;
}

[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct Heal
{
    public readonly int amount;
}

[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct TookDamage
{
    public readonly int amount;
}

// Performance option: keep [DxTargetedMessage] on a readonly struct to stay zero-boxing friendly, and drop [DxAutoConstructor] only if you need custom constructor logic.

// Optional parameters with custom defaults
[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct HealAdvanced
{
    public readonly int amount;
    [DxOptionalParameter(true)]  // Custom default value
    public readonly bool showEffect;
    [DxOptionalParameter(Expression = "Color.green")]  // Expression for any type
    public readonly Color effectColor;
}
```

### Step 2: Add a messaging component

```csharp
using DxMessaging.Unity;
using DxMessaging.Core.Messages;
using UnityEngine;

public sealed class HealthUI : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterUntargeted<WorldRegenerated>(OnWorld);
        _ = Token.RegisterComponentBroadcast<TookDamage>(this, OnDamage);
    }

    private void OnWorld(ref WorldRegenerated m) { /* update UI */ }
    private void OnDamage(ref TookDamage m) { /* flash damage */ }
}
```

#### Important: Inheritance and base calls

> **Important**
>
> If you override any of the lifecycle methods that DxMessaging hooks, your override **must** call the matching base method first. Forgetting this is silent: no errors, no compile failure, just dead handlers. The Roslyn analyzer (DXMSG006) and the [Inspector overlay](../guides/inspector-overlay.md) will flag the mistake, but they fire after the broken code is already written.

The five guarded methods, with what breaks if you forget the base call:

| Method                           | What breaks                                                                                                                 |
| -------------------------------- | --------------------------------------------------------------------------------------------------------------------------- |
| `base.Awake()`                   | The registration token is never created; no handler on this component runs.                                                 |
| `base.OnEnable()`                | When `MessageRegistrationTiedToEnableStatus` is true, your handlers never re-enable with the component.                     |
| `base.OnDisable()`               | Handlers stay live while the component is disabled, processing messages they should not see.                                |
| `base.OnDestroy()`               | Registrations leak past the component's lifetime; held references prevent GC.                                               |
| `base.RegisterMessageHandlers()` | The default `StringMessage` handlers never register. Override `RegisterForStringMessages => false` if you do not want them. |

`Start`, `Update`, `FixedUpdate`, `LateUpdate`, and `OnApplicationQuit` are not hooked. You can override them without calling base.

See [DXMSG006 in the analyzer reference](../reference/analyzers.md#dxmsg006-missing-base-call) and the symptom-first [troubleshooting guide](../reference/troubleshooting.md).

### Step 3: Send messages

```csharp
using DxMessaging.Core.Extensions;   // Emit helpers
using UnityEngine;

// Untargeted (global)
var world = new WorldRegenerated(42);
world.Emit();

// Targeted (to a specific component or GameObject)
var heal = new Heal(10);
heal.EmitGameObjectTargeted(gameObject);     // no InstanceId cast needed

// Broadcast (from a specific source)
var hit = new TookDamage(5);
hit.EmitGameObjectBroadcast(gameObject);     // no InstanceId cast needed

// String convenience (global)
"Saved".Emit();
```

---

## Summary

You have:

1. Defined 4 message types
1. Created a component that listens
1. Sent messages from anywhere

Registration cleanup is automatic. Messages are type-safe.

---

## What You Built

- **Untargeted messages** (`WorldRegenerated`, `VideoSettingsChanged`) -> Global announcements anyone can hear
- **Targeted messages** (`Heal`) -> Commands to a specific object
- **Broadcast messages** (`TookDamage`) -> Events from a source that others observe

---

## Next Steps

- **Understand What You Did**
  - -> [Mental Model](../concepts/mental-model.md) (10 min) - Philosophy and first principles
  - -> [Getting Started Guide](getting-started.md) (10 min) - Full explanation with examples
  - -> [Visual Guide](visual-guide.md) (5 min) - Pictures and analogies
- **Try Real Examples**
  - -> [Mini Combat sample](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Samples~/Mini%20Combat/README.md) - Working combat example
  - -> [UI Buttons + Inspector sample](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Samples~/UI%20Buttons%20%2B%20Inspector/README.md) - See diagnostics in action
- **Go Deeper**
  - -> [Message Types](../concepts/message-types.md) (10 min) - When to use which type
  - -> [Common Patterns](../guides/patterns.md) (15 min) - Real-world solutions
  - -> [Interceptors & Ordering](../concepts/interceptors-and-ordering.md) (10 min) - Advanced control
- **Reference**
  - -> [Quick Reference](../reference/quick-reference.md) - Cheat sheet
  - -> [API Reference](../reference/reference.md) - Complete API
  - -> [Troubleshooting](../reference/troubleshooting.md) - Fix common issues

---

## Quick Tips

### Do's

- Use `MessageAwareComponent` for Unity components (automatic lifecycle)
- Store struct in variable before emitting: `var msg = new Heal(10); msg.Emit();`
- Call `base.RegisterMessageHandlers()` when overriding

### Don'ts

- Don't emit from temporaries: `new Heal(10).Emit()` won't compile (struct emit methods require `ref this`)
- Don't use Untargeted for commands to one object (use Targeted instead)
- Don't forget `using DxMessaging.Core.Extensions;` for `Emit*` methods
