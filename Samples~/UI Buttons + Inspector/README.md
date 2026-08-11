# UI Buttons and Inspector Sample

Run a zero-setup button-to-message flow, then inspect the same traffic with DxMessaging
diagnostics.

## Run the sample

1. Open **Window > Package Manager**.
1. Select **DxMessaging**, then import **UI Buttons + Inspector**.
1. Open `UIButtonsInspector.unity` from the imported sample folder.
1. Enter Play Mode.
1. Click **Emit Play** in the Game view and inspect the Console or the live fields on the
   `Message Observer` component.

The demo button uses Unity's built-in immediate-mode GUI, so the imported scene needs no
UGUI package, Canvas, EventSystem, or Inspector event wiring. The same `Click()` method is
available for a project's normal `UnityEngine.UI.Button.onClick` binding.

## Message flow

`UIButtonEmitter.Click()` emits an untargeted domain event and a GameObject-targeted text
message:

```csharp
string effectiveButtonId = string.IsNullOrWhiteSpace(buttonId) ? "UnnamedButton" : buttonId;
var clicked = new ButtonClicked(effectiveButtonId);
clicked.Emit();

var text = new StringMessage($"Clicked {effectiveButtonId}");
text.EmitGameObjectTargeted(gameObject);
```

An empty Inspector value therefore becomes a stable fallback ID instead of emitting ambiguous
data.

`MessagingObserver` demonstrates two different observation levels:

- `RegisterUntargeted<ButtonClicked>` handles the typed button event.
- `RegisterGlobalAcceptAll` observes route metadata for diagnostics and tooling.

Use the typed registration for gameplay. Reserve accept-all listeners for diagnostics,
telemetry, replay capture, or other cross-cutting systems because they see every message.

## Diagnostics lifecycle

`MessagingObserver` enables diagnostics only on its own registration token. The
`MessageAwareComponent` lifecycle disables that token with the component and disposes it on
destroy, so this sample neither changes nor needs to restore global bus state. Use the dedicated
Diagnostics Tooling Exerciser when you need global Message Monitor and Flow Graph capture.

## Bind a project button

To use `UIButtonEmitter` with an existing UGUI button:

1. Add `UIButtonEmitter` to a GameObject.
1. Disable **Show Demo Button** on that component.
1. Add the component's `Click()` method to the button's **On Click ()** event.
1. Set a stable button ID for analytics or routing.

The emitter does not search for or create a Canvas or EventSystem. Those objects remain
owned by the UI system that created them.

If you extend `MessagingObserver`, call the base implementation first from every overridden
`MessageAwareComponent` lifecycle method, including `RegisterMessageHandlers`.
