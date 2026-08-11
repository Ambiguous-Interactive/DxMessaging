# Unity Integration

Unity-centric helpers make registration lifecycles explicit and safe.

## MessagingComponent

- Attach to any GameObject that will send or receive messages.
- Creates a per-owner `MessageHandler` and offers `Create(this)` to get a `MessageRegistrationToken`.
- Call `Configure(IMessageBus, MessageBusRebindMode)` before `Create` if you want the component to use a custom bus (e.g., one resolved from a DI container). Passing `MessageBusRebindMode.RebindActive` migrates current registrations; `PreserveRegistrations` defers the swap until the next enable.
- Optional: set `emitMessagesWhenDisabled` if you need to emit while disabled.

## MessageAwareComponent

- Derive for a batteries-included pattern; it manages a token for you.
- Override `RegisterMessageHandlers()` to stage registrations.
- The token is enabled/disabled with the component's enable state.
- Call `ConfigureMessageBus(IMessageBus, MessageBusRebindMode)` before `base.Awake()` (or shortly after via a DI bootstrapper) to ensure the token is created against your container-provided bus.

```csharp
using DxMessaging.Unity;
using DxMessaging.Core.Messages;

public sealed class HealthComponent : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterComponentTargeted<ApplyDamage>(this, OnApplyDamage);
        _ = Token.RegisterUntargeted<WorldRegenerated>(OnWorldRegenerated);
    }

    private void OnApplyDamage(ref ApplyDamage m) => Apply(m.amount);
    private void OnWorldRegenerated(ref WorldRegenerated m) => Reset();
}
```

## Do's

- Use `MessageAwareComponent` when possible to avoid boilerplate.
- Override `RegisterMessageHandlers()` and bind to named methods.
- Keep handlers small and fast; offload heavy work.

## Don'ts

- Don't register in Update; register once and enable/disable with component state.
- Don't forget to call `base.RegisterMessageHandlers()` if your subclass relies on base registrations.

## Important: Inheritance and base calls

- `MessageAwareComponent` uses many virtual methods (e.g., `Awake`, `OnEnable`, `OnDisable`, `RegisterMessageHandlers`).
- **CRITICAL**: If you override any of these, you MUST call the base method: `base.Awake()`, `base.OnEnable()`, `base.OnDisable()`, `base.RegisterMessageHandlers()`.
- **Always call `base.RegisterMessageHandlers()` first** in your override -- this ensures parent class registrations happen before yours.
- Skipping base calls can break core setup (token creation/enable) and default string-message registrations.
- If you need to opt out of string demos, prefer overriding `RegisterForStringMessages => false` rather than removing the base call.
- **Don't hide Unity methods** with `new` (e.g., `new void OnEnable()`); always `override` and call `base.*`.

!!! tip "Diagnostics & Analyzer"
DxMessaging ships a Roslyn analyzer + Inspector overlay that catches missing base calls at compile time and surfaces them as a HelpBox at the top of the offending component's Inspector. See the [Inspector Overlay & Base-Call Warnings](inspector-overlay.md) guide for the day-to-day workflow, or the [Roslyn Analyzers & Diagnostics](../reference/analyzers.md) reference for every diagnostic id and the suppression-precedence ordering.

## Registration timing

- **Prefer `Awake()` for registration** rather than `Start()`.
- `MessageAwareComponent` automatically calls `RegisterMessageHandlers()` in `Awake()`.
- Early registration in `Awake()` ensures handlers are ready before other components' `Start()` methods run.
- If you need custom setup before registration, override `Awake()`, do your setup, then call `base.Awake()`.

## Manual token management

```csharp
using DxMessaging.Unity;
using DxMessaging.Core;
using DxMessaging.Core.Messages;

[RequireComponent(typeof(MessagingComponent))]
public sealed class InventoryUI : UnityEngine.MonoBehaviour
{
    private MessagingComponent _messaging;
    private MessageRegistrationToken _token;

    private void Awake()
    {
        _messaging = GetComponent<MessagingComponent>();
        _token = _messaging.Create(this);
        _ = _token.RegisterUntargeted<WorldRegenerated>(OnWorld);
        _ = _token.RegisterComponentTargeted<ApplyDamage>(this, OnDamage);
    }

    private void OnEnable() => _token.Enable();
    private void OnDisable() => _token.Disable();

    private void OnWorld(ref WorldRegenerated m) { /* update UI */ }
    private void OnDamage(ref ApplyDamage m) { /* apply damage */ }
}
```

## Manual enable/disable (advanced)

```csharp
public sealed class AlwaysListening : MessageAwareComponent
{
    protected override bool MessageRegistrationTiedToEnableStatus => false; // keep token enabled

    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterUntargeted<MyEvent>(OnEvent);
        Token.Enable(); // explicitly enable once
    }

    private void OnEvent(ref MyEvent m) { /* ... */ }
}
```

## String message demos (opt-out)

```csharp
public sealed class NoStringDemos : MessageAwareComponent
{
    protected override bool RegisterForStringMessages => false;

    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        // only your registrations
    }
}
```

## ReflexiveMessage (bridging legacy SendMessage)

```csharp
using DxMessaging.Core;
using DxMessaging.Core.Messages;

var msg = new ReflexiveMessage("OnHit", ReflexiveSendMode.Upwards, 10);
msg.EmitGameObjectTargeted(gameObject);
```

Related

- [Quick Start](../getting-started/quick-start.md)
- [Patterns](patterns.md)
- [Diagnostics (Inspector)](diagnostics.md)
