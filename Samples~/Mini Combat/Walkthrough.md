# Mini Combat Walkthrough

The sample uses one message shape for each routing decision. The choice follows who owns
the action and which context a receiver needs.

## 1. Global settings use an untargeted message

`VideoSettingsChanged` has no recipient. Any independent system can observe the new
resolution:

```csharp
var settings = new VideoSettingsChanged(1920, 1080);
settings.Emit();
```

`UIOverlay` registers with `RegisterUntargeted<VideoSettingsChanged>`. This route fits
settings, pause state, and scene-level announcements. Do not use it for a command that
must reach one entity.

## 2. Healing uses a targeted message

`Boot` already knows which player should heal, so it targets that component:

```csharp
var heal = new Heal(10);
heal.EmitComponentTargeted(player);
```

`Player` registers the same component instance as its target. Multiple players can use
the same message type without receiving each other's commands.

## 3. Damage uses a broadcast message

`Enemy` owns the event but does not know which systems observe it:

```csharp
var tookDamage = new TookDamage(amount);
tookDamage.EmitGameObjectBroadcast(gameObject);
```

`UIOverlay` uses `RegisterBroadcastWithoutSource<TookDamage>` because it displays damage
from every enemy. A source-specific listener could instead register for one enemy.

## Lifecycle and ownership

`Player` and `UIOverlay` derive from `MessageAwareComponent`. Their tokens enable and
disable with the components and release registrations when destroyed.

The scene assigns `Boot.player`, `Boot.enemy`, and `Boot.uiOverlay`. The fallback path is
also safe for an empty scene:

- `Awake` creates only missing participants, before any `Start` method runs.
- Each created GameObject is recorded separately.
- `OnDestroy` destroys only recorded objects.
- Assigned scene objects remain untouched.

This ownership rule matters whenever a bootstrapper, pool, or service creates a resource:
the creator must retain enough information to release exactly what it owns.

## Handlers and post-processors have different jobs

Use a handler when the message should change gameplay or presentation state. For example,
`Player.OnHeal` updates hit points and `UIOverlay.OnAnyDamage` updates the on-screen combat feed.

Use a post-processor for work that must observe the completed dispatch, such as recording
a processed-heal-request metric or adding a replay entry. Completion means that dispatch reached
the post-processing stage; it does not prove that a handler applied the requested amount. A
post-processor should not duplicate the handler merely to print the same payload.

## Debug the flow

If a message does not arrive:

1. Confirm the listener GameObject and component are enabled.
1. Confirm component targeting and GameObject targeting are not mixed.
1. Check that every overridden lifecycle method calls its base implementation.
1. Enable diagnostics and inspect Message Monitor or Flow Graph.

See [Listening Patterns](../../docs/concepts/listening-patterns.md) for across-all listeners
and post-processors.
