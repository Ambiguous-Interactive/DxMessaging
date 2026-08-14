# Quick Start - Your First Message in 5 Minutes

[Back to Index](index.md) | [Getting Started](getting-started.md) | [Visual Guide](visual-guide.md) | [Samples](https://github.com/Ambiguous-Interactive/DxMessaging/tree/master/Samples~)

---

**Goal:** Get a working message system in 5 minutes. Copy the scripts, add two components, and run.

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

### Step 1: Define a damage command

```csharp
using DxMessaging.Core.Attributes;

[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct DamageRequested
{
    public readonly int Amount;
}
```

The source generator adds the constructor and targeted message identity. The emit helper is a
DxMessaging extension method.

### Step 2: Receive damage

```csharp
using DxMessaging.Unity;
using UnityEngine;

public sealed class DamageReceiver : MessageAwareComponent
{
    public int Health { get; private set; } = 100;

    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterGameObjectTargeted<DamageRequested>(gameObject, OnDamageRequested);
    }

    private void OnDamageRequested(ref DamageRequested message)
    {
        Health = Mathf.Max(0, Health - Mathf.Max(0, message.Amount));
    }
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

### Step 3: Add a hazard

```csharp
using DxMessaging.Core.Extensions;
using UnityEngine;

public sealed class Hazard : MonoBehaviour
{
    public int Damage = 25;

    private void OnTriggerEnter(Collider other)
    {
        DamageRequested request = new DamageRequested(Damage);
        request.EmitGameObjectTargeted(other.gameObject);
    }
}
```

On the hazard GameObject, enable **Is Trigger** on its collider and add a kinematic `Rigidbody` with
**Use Gravity** disabled. Put `DamageReceiver` and the entering collider on the same target
GameObject. The hazard sends the same command to whatever enters the trigger without a player
field, receiver lookup, interface, or UnityEvent.

---

## Summary

You have:

1. Defined a targeted gameplay command
1. Registered a receiver against its own GameObject
1. Sent the command to the object discovered by a physics event

Registration cleanup is automatic. Messages are type-safe.

---

## What You Built

The hazard and damage receiver share only the `DamageRequested` contract. You can add shields,
breakable props, enemies, or test doubles without changing `Hazard`. Use the message-type guide next
to learn when a global announcement or source-bound event fits better than this targeted command.

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
- Store the struct in a variable before emitting: `var message = new DamageRequested(10); message.EmitGameObjectTargeted(target);`
- Call `base.RegisterMessageHandlers()` when overriding

### Don'ts

- Don't emit from temporaries: `new DamageRequested(10).EmitGameObjectTargeted(target)` won't compile (struct emit methods require `ref this`)
- Don't use Untargeted for commands to one object (use Targeted instead)
- Don't forget `using DxMessaging.Core.Extensions;` for `Emit*` methods
