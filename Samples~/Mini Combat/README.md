# Mini Combat Sample

Run one scene to compare untargeted, targeted, and broadcast messages in a small combat flow.

## Run the sample

1. Open **Window > Package Manager**.
1. Select **DxMessaging**, then import **Mini Combat**.
1. Open `MiniCombat.unity` from the imported sample folder.
1. Enter Play Mode. Watch the combat feed in the Game view and the gameplay output in the Console.

The scene is already wired. `Boot` emits three messages after every listener has registered:

1. `VideoSettingsChanged` is untargeted, so `UIOverlay` observes a global settings change.
1. `Heal` targets one `Player` component, so another player would not receive it.
1. `TookDamage` broadcasts from the `Enemy` GameObject, so observers retain source context.

## Files

| File | Responsibility |
| --- | --- |
| [Messages.cs](./Messages.cs) | Declares the three message contracts. |
| [Player.cs](./Player.cs) | Applies a targeted heal to one player. |
| [Enemy.cs](./Enemy.cs) | Broadcasts damage with its GameObject as the source. |
| [UIOverlay.cs](./UIOverlay.cs) | Presents global settings and damage from any source in the Game view. |
| [Boot.cs](./Boot.cs) | Starts the deterministic flow and owns any fallback objects it creates. |
| [Walkthrough.md](./Walkthrough.md) | Explains route choice, lifecycle, and extension points. |

## Core pattern

`MessageAwareComponent` creates and owns a registration token. Register handlers in
`RegisterMessageHandlers` and call the base implementation first:

```csharp
protected override void RegisterMessageHandlers()
{
    base.RegisterMessageHandlers();
    _ = Token.RegisterComponentTargeted<Heal>(this, OnHeal);
}
```

Emit struct messages from variables because the generated extension methods take the
message by reference:

```csharp
var heal = new Heal(10);
heal.EmitComponentTargeted(player);
```

## Safe fallback behavior

The included scene serializes every reference. If you place `Boot` in an empty scene,
it creates the missing `Player`, `Enemy`, and `UIOverlay` components before `Start`.
`Boot` records exactly which GameObjects it created and destroys only those objects in
`OnDestroy`; it never destroys scene-owned or user-assigned objects.

## Extend the scene

- Add another `Player` and target it explicitly to verify component isolation.
- Add an audio listener for `TookDamage` without changing `Enemy`.
- Add an analytics post-processor when you need a measurement after gameplay handlers
  have completed. Keep gameplay state changes in handlers.

If you override `Awake`, `OnEnable`, `OnDisable`, `OnDestroy`, or
`RegisterMessageHandlers` in a `MessageAwareComponent` subclass, call the corresponding
base method first. The package analyzers report missing base calls.
