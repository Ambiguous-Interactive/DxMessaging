# End-to-End Example: Combat + UI + Settings

Short intro

A small scenario tying together Untargeted, Targeted, and Broadcast messages with Unity integration, ordering, interceptors, and diagnostics.

Scenario

- Global: settings change (Untargeted)
- Targeted: heal a specific player (Targeted)
- Broadcast: enemies report damage taken (Broadcast)
- UI listens globally to update overlays
- Analytics listens across all sources/targets

Messages

```csharp
using DxMessaging.Core.Attributes;

[DxUntargetedMessage][DxAutoConstructor]
public readonly partial struct VideoSettingsChanged { public readonly int width; public readonly int height; }

[DxTargetedMessage][DxAutoConstructor]
public readonly partial struct Heal { public readonly int amount; }

[DxBroadcastMessage][DxAutoConstructor]
public readonly partial struct TookDamage { public readonly int amount; }
```

Unity: Player component (targeted)

```csharp
using DxMessaging.Unity;
using DxMessaging.Core.Messages;

public sealed class Player : MessageAwareComponent
{
    private int _hp;

    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterComponentTargeted<Heal>(this, OnHeal);
    }

    private void OnHeal(in Heal m) => _hp += m.amount;
}
```

Unity: Enemy component (broadcast)

```csharp
using DxMessaging.Unity;
using DxMessaging.Core.Extensions;
using DxMessaging.Core.Messages;

[RequireComponent(typeof(MessagingComponent))]
public sealed class Enemy : UnityEngine.MonoBehaviour
{
    public void ApplyDamage(int amount)
    {
        var took = new TookDamage(amount);
        took.EmitGameObjectBroadcast(gameObject);
    }
}
```

UI overlay (global + all-sources)

```csharp
using DxMessaging.Unity;
using DxMessaging.Core;
using DxMessaging.Core.Messages;
using UnityEngine;

public sealed class UIOverlay : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterUntargeted<VideoSettingsChanged>(OnSettings);
        _ = Token.RegisterBroadcastWithoutSource<TookDamage>(OnAnyDamage);
    }

    private void OnSettings(in VideoSettingsChanged m) => RebuildUI(m.width, m.height);
    private void OnAnyDamage(in InstanceId src, in TookDamage m) => ShowFloatingText(src, $"-{m.amount}");
}
```

Interceptors and post-processing (ordering)

```csharp
using System;
using DxMessaging.Core;            // MessageHandler
using DxMessaging.Core.MessageBus; // IMessageBus

public sealed class HealRules : IDisposable
{
    private readonly IMessageBus _bus;
    private MessageBusRegistration _registration;

    public HealRules(IMessageBus bus)
    {
        _bus = bus;
        _registration = bus.RegisterTargetedInterceptor<Heal>(NormalizeHeal, priority: 0);
    }

    public void Dispose()
    {
        if (!_registration.IsValid)
        {
            return;
        }

        _bus.Deregister<Heal>(in _registration);
        _registration = MessageBusRegistration.None;
    }

    private static bool NormalizeHeal(ref InstanceId target, ref Heal message)
    {
        int amount = UnityEngine.Mathf.Clamp(message.amount, 0, 999);
        if (amount == 0)
        {
            return false;
        }

        message = new Heal(amount);
        return true;
    }
}
```

Keep the interceptor owner alive for the rules scope. Token-owned post-processors remain on
their token and need no separate bus handle:

```csharp
using HealRules healRules = new(MessageHandler.MessageBus);
_ = token.RegisterBroadcastWithoutSourcePostProcessor<TookDamage>((InstanceId src, TookDamage m) =>
{
    Analytics.Log("DamageMessageProcessed", new { src, m.amount });
});
```

The `UIOverlay` handler above owns the floating damage presentation. This post-processor
records the completed dispatch for telemetry; it does not claim that a health change occurred.

Settings menu (untargeted)

```csharp
using DxMessaging.Core.Extensions;

public sealed class SettingsMenu
{
    public void Apply(int width, int height)
    {
        var changed = new VideoSettingsChanged(width, height);
        changed.Emit();
    }
}
```

Diagnostics (Editor)

- Enable Editor diagnostics in **Project Settings > Wallstop Studios > DxMessaging**.
- Open **Tools > Wallstop Studios > DxMessaging > Message Monitor** to verify
  the three emissions, their route kinds, and their source/target contexts.
- Open **Tools > Wallstop Studios > DxMessaging > Flow Graph** to inspect the
  message-to-receiver routes and select a route for delivery evidence.
- Select a `MessagingComponent` to inspect its local buffer, registrations, and
  lifecycle warnings.
- See the [Diagnostics guide](../guides/diagnostics.md) for filters, call-site
  capture, and screenshots of both tools.

Notes

- Use component vs gameObject context consistently for targeted/broadcast messages (see [Targeting & Context](../concepts/targeting-and-context.md)).
- For tests/subsystems, use a local MessageBus and pass it to the token factory.
