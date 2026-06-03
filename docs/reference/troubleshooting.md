# Troubleshooting -- Common Issues & Solutions

[Back to Index](../getting-started/index.md) | [FAQ](faq.md) | [Getting Started](../getting-started/getting-started.md) | [Glossary](glossary.md)

---

## Not receiving messages

- Ensure your `MessageRegistrationToken` is enabled (Enable in `OnEnable`, Disable in `OnDisable`).
- Verify the category matches the emission (Untargeted vs Targeted vs Broadcast).
- Targeted/Broadcast require a valid `InstanceId`; ensure the target/source object exists when you emit.
- In Unity, confirm your `MessagingComponent` exists on sender/receiver GameObjects.
- **CRITICAL**: If inheriting from `MessageAwareComponent`, ensure your overrides call base methods:
  - **`base.RegisterMessageHandlers()`** - Call this FIRST in your override to preserve default setup (including string message demos) and parent class registrations.
  - **`base.Awake()`** - Call this if you override `Awake()`, or your token won't be created (this is the #1 cause of handlers not firing).
  - **`base.OnEnable()` / `base.OnDisable()`** - Call these so the token actually enables/disables.
  - **`base.OnDestroy()`** - Call this if you override `OnDestroy()`, or registrations leak past the component's lifetime and held references prevent GC.
  - **Never use `new` to hide Unity methods** (e.g., `new void OnEnable()`); always use `override` and call `base.*`.
  - For the complete table of guarded methods and the exact failure mode for each, see [Inheritance and base calls](../getting-started/quick-start.md#important-inheritance-and-base-calls) in the quickstart.

Registration timing

- **ALWAYS register message handlers in `Awake()`**, not `Start()`.
- `MessageAwareComponent` automatically calls `RegisterMessageHandlers()` in `Awake()`.
- Registering in `Awake()` ensures handlers are ready before other components' `Start()` methods run.
- If you register in `Start()`, you may miss messages emitted by other components in their `Start()` methods.

Unexpected ordering

- Check `priority` values on registrations; lower runs earlier. Same priority is registration order.
- Interceptors always precede handlers and can cancel; confirm interceptors return `true`.

Double registration or over-deregistration warnings

- Avoid calling stage/enable multiple times; pair registrations and lifecycles consistently.
- Review logs with `bus.Log.Enabled = true` to see the registration history.

Allocations/boxing

- Prefer struct messages implementing the generic interfaces: `I*Message<T>`.
- Use by-ref handler overloads to avoid copies.

Emitting while disabled

- If you need to emit when a component is disabled, use a bus not tied to enable state or set `emitMessagesWhenDisabled` on `MessagingComponent`.

Diagnostics overhead

- Disable diagnostics in release builds (`IMessageBus.GlobalDiagnosticsMode = false`).

## Memory grows in long sessions

- Read `bus.OccupiedTypeSlots` and `bus.OccupiedTargetSlots` (or the global
  `MessageHandler.MessageBus.OccupiedTypeSlots` / `OccupiedTargetSlots`) at
  region boundaries to see whether per-type or per-target slots are the
  culprit.
- Call `MessageHandler.TrimAll(force: true)` (or `bus.Trim(force: true)`) at
  scene unload or other natural transitions. Slots that survive a forced
  trim correspond to active registrations.
- Tune the reclamation policy through `DxMessagingRuntimeSettings`. See the
  [Memory Reclamation guide](../guides/memory-reclamation.md) for tuning
  recommendations and a leak-watching pattern.

---

## Related Documentation

- **Get Unstuck**
  - to [FAQ](faq.md) -- Common questions answered
  - to [Getting Started](../getting-started/getting-started.md) -- Learn the basics
  - to [Glossary](glossary.md) -- Understand the terminology
- **Debug & Inspect**
  - to [Diagnostics](../guides/diagnostics.md) -- Inspector tools and debugging
  - to [Listening Patterns](../concepts/listening-patterns.md) -- Verify you're listening correctly
  - to [Message Types](../concepts/message-types.md) -- Ensure you're using the right type
- **Examples**
  - to [Mini Combat sample](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Samples~/Mini%20Combat/README.md) -- See working code
  - to [Common Patterns](../guides/patterns.md) -- Real-world solutions
