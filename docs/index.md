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
- [Message Monitor](guides/diagnostics.md#message-monitor) - Inspect emissions,
  filters, contexts, and captured call sites.
- [Flow Graph](guides/diagnostics.md#flow-graph) - Inspect loaded-scene
  `MessagingComponent` routes and delivery evidence.
- [Inspector Tools](guides/inspector-overlay.md) - Catch base-call mistakes and
  inspect component-local diagnostics.
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
    <p>Struct messages and readonly by-reference handlers keep steady-state dispatch at zero allocation. Type-indexed routing stays O(1); see the current CI table for measured scenario costs.</p>
  </section>
</div>

## Next

- New to the package: [Getting Started](getting-started/index.md)
- Choosing message types: [Message Types](concepts/message-types.md)
- Unity integration patterns: [Unity Integration](guides/unity-integration.md)
- Debugging message flow: [Diagnostics](guides/diagnostics.md)
- API details: [Reference](reference/reference.md)
