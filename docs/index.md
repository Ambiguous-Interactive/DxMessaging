---
title: Home
description: Decoupled, simple systems for Unity
template: home.html
hide:
  - navigation
  - toc
---

## Start Here

- [Quick Start](getting-started/quick-start.md) - Define, register, and emit
  your first message.
- [Mental Model](concepts/mental-model.md) - Choose between untargeted,
  targeted, and broadcast messages.
- [Inspector Tools](guides/inspector-overlay.md) - Use diagnostics and
  base-call warnings inside Unity.
- [Message Monitor](guides/diagnostics.md) - Inspect emissions, trace paths, and
  registration topology.
- [Performance](architecture/performance.md) - Read the current published
  benchmark tables.

## Install

### OpenUPM

```bash
openupm add com.wallstop-studios.dxmessaging
```

### Git URL

```text
https://github.com/Ambiguous-Interactive/DxMessaging.git
```

See the [Install Guide](getting-started/install.md) for scoped registry, Git URL, and local tarball options.

## First Message

```csharp
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Extensions;
using DxMessaging.Unity;
using UnityEngine;

[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct DamageRequested
{
    public readonly int Amount;
}

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
GameObject. The hazard targets that exact object without knowing whether it is a player, enemy,
crate, or future gameplay type. Neither component holds a reference to the other.

## Why Teams Use It

<div class="dx-home-feature-grid">
  <section>
    <h3>Simple primitives</h3>
    <p>Three message shapes - untargeted, targeted, broadcast - and nothing else to learn. Each contract is an explicit typed struct, and no system holds a reference to any other.</p>
  </section>
  <section>
    <h3>Easy to use</h3>
    <p>Define a struct, register a handler, emit. Registration tokens follow their owner's lifecycle, so handlers remove themselves - no manual unsubscribe, no leaked listeners.</p>
  </section>
  <section>
    <h3>Small edits, big impact</h3>
    <p>The same simple primitives decouple entire systems. Wiring a feature in is one registration; removing it is deleting that line. Interceptors, handler priorities, and global observers layer on without touching existing code.</p>
  </section>
  <section>
    <h3>High performance</h3>
    <p>Struct messages and by-ref handlers keep steady-state dispatch at zero allocation. Type-indexed routing stays O(1), with published results around 10 ns per handler.</p>
  </section>
</div>

## Next

- New to the package: [Getting Started](getting-started/index.md)
- Choosing message types: [Message Types](concepts/message-types.md)
- Unity integration patterns: [Unity Integration](guides/unity-integration.md)
- Debugging message flow: [Diagnostics](guides/diagnostics.md)
- API details: [Reference](reference/reference.md)
