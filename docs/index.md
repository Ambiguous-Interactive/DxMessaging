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
using UnityEngine.UI;

[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct HealPlayerRequested
{
    public readonly int Amount;
}

public sealed class PlayerHealth : MessageAwareComponent
{
    private const int MaximumHealth = 100;

    public int CurrentHealth { get; private set; } = 50;

    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterGameObjectTargeted<HealPlayerRequested>(gameObject, OnHealRequested);
    }

    private void OnHealRequested(ref HealPlayerRequested message)
    {
        CurrentHealth = Mathf.Min(MaximumHealth, CurrentHealth + Mathf.Max(0, message.Amount));
    }
}

[RequireComponent(typeof(Button))]
public sealed class HealButton : MonoBehaviour
{
    private Button _button;
    private PlayerHealth _playerHealth;

    private void Awake()
    {
        _button = GetComponent<Button>();
        _playerHealth = GetComponentInParent<PlayerHealth>();
        if (_playerHealth == null)
        {
            Debug.LogError("HealButton must be placed under a PlayerHealth component.", this);
            return;
        }
        _button.onClick.AddListener(Click);
    }

    private void OnDestroy()
    {
        _button.onClick.RemoveListener(Click);
    }

    private void Click()
    {
        HealPlayerRequested request = new HealPlayerRequested(25);
        request.EmitGameObjectTargeted(_playerHealth.gameObject);
    }
}
```

Put `HealButton` on a Unity `Button` under the player's `PlayerHealth` object. The component
wires the click and finds that player automatically; there is no `On Click` event or player
reference to assign in the Inspector. The heal request is targeted because it changes one
player's health.

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
