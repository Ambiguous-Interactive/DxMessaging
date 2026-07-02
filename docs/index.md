---
title: Home
description: Synchronous, type-safe messaging for Unity
---

<section class="dx-home-hero">
  <div class="dx-home-hero__copy">
    <div class="dx-home-brand">
      <img src="images/dx-mark.svg" alt="" />
      <span>Unity / C# / MIT</span>
    </div>
    <h1>DxMessaging</h1>
    <p>
      A synchronous message bus for Unity projects that need typed messages,
      lifecycle-managed registrations, and Unity editor visibility without direct
      component references.
    </p>
    <div class="dx-home-actions">
      <a class="md-button md-button--primary" href="getting-started/">Get started</a>
      <a class="md-button" href="https://github.com/Ambiguous-Interactive/DxMessaging">View source</a>
    </div>
  </div>
  <div class="dx-home-signal" aria-label="DxMessaging signal path">
    <div class="dx-home-signal__title">One bus, three routes</div>
    <div class="dx-home-signal__path">
      <span class="dx-home-signal__node">U</span>
      <span class="dx-home-signal__line"></span>
      <span class="dx-home-signal__hub"></span>
      <span class="dx-home-signal__line"></span>
      <span class="dx-home-signal__node">T</span>
      <span class="dx-home-signal__line"></span>
      <span class="dx-home-signal__node">B</span>
    </div>
    <div class="dx-home-signal__rows">
      <span>Untargeted announcements</span>
      <span>Targeted commands</span>
      <span>Broadcast facts</span>
    </div>
  </div>
</section>

<section class="dx-home-telemetry-band" aria-label="DxMessaging diagnostics coverage">
  <a class="dx-home-telemetry-item" href="guides/diagnostics/">
    <span class="dx-home-telemetry-item__label">Message routes</span>
    <span class="dx-home-telemetry-route" aria-hidden="true">
      <span>U</span>
      <i></i>
      <span>T</span>
      <i></i>
      <span>B</span>
    </span>
    <small>Untargeted, targeted, and broadcast paths stay visible in Message Monitor and Flow Graph.</small>
  </a>
  <a class="dx-home-telemetry-item" href="guides/diagnostics/#flow-graph">
    <span class="dx-home-telemetry-item__label">Trace paths</span>
    <strong>Context / target / delivery</strong>
    <small>Flow Graph views expose recent trace evidence without changing the runtime dispatch model.</small>
  </a>
  <a class="dx-home-telemetry-item" href="guides/inspector-overlay/">
    <span class="dx-home-telemetry-item__label">Editor tools</span>
    <strong>Monitor / Graph / Inspector</strong>
    <small>Use package-owned tools for message history, route topology, and component-local warnings.</small>
  </a>
</section>

## Start Here

<div class="dx-home-link-grid">
  <a class="dx-home-link" href="getting-started/quick-start/">
    <span>Quick Start</span>
    <small>Define, register, and emit your first message.</small>
  </a>
  <a class="dx-home-link" href="concepts/mental-model/">
    <span>Mental Model</span>
    <small>Choose between untargeted, targeted, and broadcast messages.</small>
  </a>
  <a class="dx-home-link" href="guides/inspector-overlay/">
    <span>Inspector Tools</span>
    <small>Use diagnostics and base-call warnings inside Unity.</small>
  </a>
  <a class="dx-home-link" href="guides/diagnostics/">
    <span>Message Monitor</span>
    <small>Inspect emissions, trace paths, and registration topology.</small>
  </a>
  <a class="dx-home-link" href="architecture/performance/">
    <span>Performance</span>
    <small>Read the current published benchmark tables.</small>
  </a>
</div>

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
public readonly partial struct Heal
{
    public readonly int Amount;
}

public sealed class PlayerHealth : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        Token.RegisterGameObjectTargeted<Heal>(gameObject, OnHeal);
    }

    private void OnHeal(ref Heal message)
    {
        // Apply the heal to this player.
    }
}

public sealed class HealButton : MonoBehaviour
{
    [SerializeField]
    private GameObject _player;

    public void Click()
    {
        Heal heal = new Heal(25);
        heal.EmitGameObjectTargeted(_player);
    }
}
```

## Why Teams Use It

<div class="dx-home-feature-grid">
  <section>
    <h3>Lifecycle-managed listeners</h3>
    <p>Registrations follow their token or component, so teardown lives with ownership.</p>
  </section>
  <section>
    <h3>Typed message contracts</h3>
    <p>Message shapes are C# structs, with source generators for IDs and constructors.</p>
  </section>
  <section>
    <h3>Editor visibility</h3>
    <p>Diagnostics expose recent messages, listener state, trace paths, and base-call warnings.</p>
  </section>
  <section>
    <h3>Allocation-aware dispatch</h3>
    <p>Hot-path dispatch is measured in CI and documented from the latest published baseline.</p>
  </section>
</div>

## Next

- New to the package: [Getting Started](getting-started/index.md)
- Choosing message types: [Message Types](concepts/message-types.md)
- Unity integration patterns: [Unity Integration](guides/unity-integration.md)
- Debugging message flow: [Diagnostics](guides/diagnostics.md)
- API details: [Reference](reference/reference.md)
