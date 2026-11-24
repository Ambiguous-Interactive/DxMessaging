# Quick Start - Your First Message

<a id="quick-start-your-first-message-in-5-minutes"></a>

[Back to Index](index.md) | [Getting Started](getting-started.md) | [Visual Guide](visual-guide.md) | [Samples](https://github.com/Ambiguous-Interactive/DxMessaging/tree/master/Samples~)

---

Send a damage command from a 3D trigger and see the receiver's health in the Unity Console.

**Stuck?** -> [Troubleshooting](../reference/troubleshooting.md) | [FAQ](../reference/faq.md)

---

## Step 0: Install

<a id="step-0-install-30-seconds"></a>

Unity Package Manager -> Add package from git URL:

```text
https://github.com/Ambiguous-Interactive/DxMessaging.git
```

**Requirements:** Unity 2021.3+ | .NET Standard 2.1 | Built-In Render Pipeline, URP, or HDRP

---

## Your First Message (3 Steps)

> **Handler version:** These examples use the 4.0 `in` signature. With a 3.x package, use
> `ref DamageRequested message` for the receiver callback parameter instead. Keep the registration
> and emission calls unchanged. See the [4.0 migration guide](../guides/migration-guide.md#migrating-to-400).

### Step 1: Define a damage command

Create `DamageRequested.cs`.

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

Create `DamageReceiver.cs`.

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

    private void OnDamageRequested(in DamageRequested message)
    {
        Health = Mathf.Max(0, Health - Mathf.Max(0, message.Amount));
        Debug.Log($"Health: {Health}", this);
    }
}
```

#### Important: Inheritance and base calls

> **Important**
>
> If you override any of the lifecycle methods that DxMessaging hooks, your override **must** call the matching base method first. Forgetting this is silent: no errors, no compile failure, just dead handlers. The Roslyn analyzer (DXMSG006) and the [Inspector overlay](../guides/inspector-overlay.md) will flag the mistake, but they fire after the broken code is already written.

The five guarded methods, with what breaks if you forget the base call:

| Method                           | What breaks                                                                                             |
| -------------------------------- | ------------------------------------------------------------------------------------------------------- |
| `base.Awake()`                   | The registration token is never created; no handler on this component runs.                             |
| `base.OnEnable()`                | When `MessageRegistrationTiedToEnableStatus` is true, your handlers never re-enable with the component. |
| `base.OnDisable()`               | Handlers stay live while the component is disabled, processing messages they should not see.            |
| `base.OnDestroy()`               | Registrations leak past the component's lifetime; held references prevent GC.                           |
| `base.RegisterMessageHandlers()` | Registrations declared by a parent component do not register.                                           |

Declare `Start`, `Update`, `FixedUpdate`, and `LateUpdate` as ordinary Unity message methods;
`MessageAwareComponent` does not declare them as virtual methods to override. Its virtual
`OnApplicationQuit` implementation does nothing. An override needs no base call for messaging
cleanup. Preserve any quit behavior implemented by an intermediate parent class.

See [DXMSG006 in the analyzer reference](../reference/analyzers.md#dxmsg006-missing-base-call) and the symptom-first [troubleshooting guide](../reference/troubleshooting.md).

### Step 3: Add a hazard

Create `Hazard.cs`.

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

#### Try it in Play Mode

1. Add two 3D cubes to an empty scene. Name them **Hazard** and **Target**. Set their positions
   to `(0, 0, 0)` and `(3, 0, 0)`, respectively.
1. Attach `Hazard` to **Hazard** and leave **Damage** at 25. On its Box Collider, enable
   **Is Trigger**. Add a Rigidbody, enable **Is Kinematic**, and disable **Use Gravity**.
1. Attach `DamageReceiver` to **Target**. Keep its Box Collider with **Is Trigger** disabled.
1. Enter Play Mode. In the Inspector for **Hazard**, set **Transform > Position > X** to 3.
   The hazard now overlaps the target.
1. In the Unity Console, select `Health: 75`. Unity highlights **Target** in the Hierarchy.

The trigger supplies the target GameObject. The hazard sends the command without a reference to
the receiver component.

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
  - -> [Mental Model](../concepts/mental-model.md) - Philosophy and first principles
  - -> [Getting Started Guide](getting-started.md) - Full explanation with examples
  - -> [Visual Guide](visual-guide.md) - Pictures and analogies
- **Try Real Examples**
  - -> [Mini Combat sample](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Samples~/Mini%20Combat/README.md) - Working combat example
  - -> [UI Buttons + Inspector sample](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Samples~/UI%20Buttons%20%2B%20Inspector/README.md) - See diagnostics in action
- **Go Deeper**
  - -> [Message Types](../concepts/message-types.md) - When to use which type
  - -> [Common Patterns](../guides/patterns.md) - Real-world solutions
  - -> [Interceptors & Ordering](../concepts/interceptors-and-ordering.md) - Advanced control
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
