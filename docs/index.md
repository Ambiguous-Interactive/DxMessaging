---
title: Home
description: Decoupled, simple systems for Unity
template: home.html
hide:
  - navigation
  - toc
---

## AI Assistance Disclosure

DxMessaging began as an original project around 2014 and remains actively developed and used today.
Over the past two years, I (wallstop) have been using LLMs to help improve the library's quality and
performance, expand its tests, and create user-facing documentation. There is always a human in
the loop: I actively review all changes and read through all user-facing copy. I share this so you
can make an informed decision about using the project.

DxMessaging is free and will remain free forever. No paid editions, paid upgrades, or feature charges.

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
    <p>Choose untargeted, targeted, or broadcast messages. Senders and receivers share a typed message contract.</p>
  </section>
  <section>
    <h3>Easy to use</h3>
    <p>Define a message, register a handler, and emit. MessageAwareComponent owns registration cleanup through its Unity lifecycle. Plain C# owners enable and dispose their registration tokens.</p>
  </section>
  <section>
    <h3>Small edits, big impact</h3>
    <p>The same simple primitives decouple entire systems. Wiring a feature in is one registration; removing it is deleting that line. Interceptors, handler priorities, and global observers layer on without touching existing code.</p>
  </section>
  <section>
    <h3>High performance</h3>
    <p>Ordinary typed steady-state struct dispatch is allocation-free. Global accept-all handlers box structs, and emission-site stack-trace capture allocates. See the performance page for measured scenario costs.</p>
  </section>
</div>

See the [allocation contract and exceptions](reference/faq.md#does-dxmessaging-allocate-memory-is-dispatch-zero-gc)
before choosing a dispatch path or enabling diagnostics.

## Next

- New to the package: [Getting Started](getting-started/index.md)
- Choosing message types: [Message Types](concepts/message-types.md)
- Unity integration patterns: [Unity Integration](guides/unity-integration.md)
- Debugging message flow: [Diagnostics](guides/diagnostics.md)
- API details: [Reference](reference/reference.md)
