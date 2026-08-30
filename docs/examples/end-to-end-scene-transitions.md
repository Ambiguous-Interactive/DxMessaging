# End-to-End Example: Scene Transitions + Overlay Pause

Short intro

An end-to-end pattern showing scene-scoped buses, a global overlay that persists across scenes, and pausing emissions/listeners safely.

Scenario

- Each scene has its own local MessageBus for scene-scoped flows.
- A global overlay persists across scenes and listens to global notifications.
- During pause, some systems should stop processing (Disable tokens), but others still emit (emit while disabled).

Setup

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;
using UnityEngine;

public static class SceneBus
{
    public static MessageBus Current { get; private set; } = new MessageBus();
    public static void NewScene() => Current = new MessageBus();
}
```

Messages

```csharp
using DxMessaging.Core.Attributes;

[DxUntargetedMessage][DxAutoConstructor]
public readonly partial struct SceneLoaded { public readonly int buildIndex; }

[DxUntargetedMessage][DxAutoConstructor]
public readonly partial struct Paused { }

[DxUntargetedMessage][DxAutoConstructor]
public readonly partial struct Resumed { }
```

Global overlay (persists across scenes)

```csharp
using DxMessaging.Unity;
using DxMessaging.Core.Messages;
using UnityEngine;

public sealed class GlobalOverlay : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        DontDestroyOnLoad(gameObject);
        _ = Token.RegisterUntargeted<SceneLoaded>(OnSceneLoaded);
        _ = Token.RegisterUntargeted<Paused>(m => ShowPause());
        _ = Token.RegisterUntargeted<Resumed>(m => HidePause());
    }

    private void OnSceneLoaded(in SceneLoaded m) => RebuildUIForScene(m.buildIndex);
}
```

Scene systems (use local bus)

```csharp
using DxMessaging.Core;
using DxMessaging.Core.MessageBus;
using DxMessaging.Core.Messages;

public sealed class SceneDriver
{
    public void OnSceneStart(int buildIndex)
    {
        // Switch to a fresh bus per scene
        SceneBus.NewScene();

        // Broadcast globally (untargeted) so overlay updates
        var info = new SceneLoaded(buildIndex);
        info.Emit();

        // For scene-local flows, pass the local bus to tokens
        // var token = MessageRegistrationToken.Create(handler, SceneBus.Current);
    }
}
```

Pausing

```csharp
using DxMessaging.Unity;
using UnityEngine;

public sealed class PauseController : MonoBehaviour
{
    public MessagingComponent[] systemsToPause; // listeners to disable during pause

    public void Pause()
    {
        foreach (var mc in systemsToPause)
        {
            mc.ToggleMessageHandler(false); // disable listeners
        }

        var p = new Paused(); p.Emit(); // overlay still listening globally
    }

    public void Resume()
    {
        var r = new Resumed(); r.Emit();
        foreach (var mc in systemsToPause)
        {
            mc.ToggleMessageHandler(true);
        }
    }
}
```

Emitting while disabled

```csharp
// For specific emitters that must keep sending even if disabled:
messaging.emitMessagesWhenDisabled = true;
```

Notes

- Use global untargeted messages to communicate scene transitions to global overlays.
- Keep scene-specific flows isolated on a per-scene bus (pass to tokens for registrations and emit to it).
- Toggle listeners off to pause, and opt into emission while disabled for emitters that must remain active.
