# Inspector Overlay & Base-Call Warnings

DxMessaging ships a Roslyn analyzer and a companion Unity Inspector overlay
that catch the most common authoring mistake when subclassing
`MessageAwareComponent`: forgetting to call `base.OnEnable()` (and friends)
in your override. Without those base calls the messaging system does
nothing -- every handler you registered silently fails to fire. This page
is the user-facing tour of how the package surfaces the problem and how
you fix it.

This guide covers when warnings appear, what the Inspector warning surfaces
look like, the three actions they offer, the Project Settings panel, and the
manual rescan menu. For the full reference -- every diagnostic id,
exact detection policy, suppression precedence, and Unity 2021 setup
notes -- see [Roslyn Analyzers & Diagnostics](../reference/analyzers.md).

## When a Warning Appears

Whenever your code triggers one of the base-call diagnostics
([DXMSG006](../reference/analyzers.md#dxmsg006-missing-base-call),
[DXMSG007](../reference/analyzers.md#dxmsg007-new-hides-unity-method),
[DXMSG009](../reference/analyzers.md#dxmsg009-implicit-hide-and-missing-modifier),
or [DXMSG010](../reference/analyzers.md#dxmsg010-broken-transitive-base-call-chain)),
two things happen in parallel:

1. **At compile time**, the Roslyn analyzer (`WallstopStudios.DxMessaging.Analyzer.dll`,
   shipped under `Runtime/Analyzers/`) emits a warning into Unity's
   Console with the corresponding `DXMSG###` id and a message that
   names the offending type and method.
1. **At Inspector time**, the overlay reads the cached scan from
   `Library/DxMessaging/baseCallReport.json` and renders a warning
   panel at the very top of every `MessageAwareComponent` subclass's
   Inspector that has at least one missing base call.

You see both surfaces by default. The Console warning is authoritative
for CI builds (the analyzer is activated for Unity's C# compiler through
the `RoslynAnalyzer` label, so it runs on every Unity-driven compile);
the Inspector overlay is the in-Editor reminder you cannot ignore while
wiring a prefab.

!!! tip
Severity is per-project tunable. Add lines like `dotnet_diagnostic.DXMSG006.severity = error` to your `.editorconfig` to upgrade missing base calls into a build break, or `severity = none` to silence one project-wide. See [Suppression precedence](../reference/analyzers.md#suppression-precedence) for the full ordering.

## The Warning Surfaces

When the overlay decides to render for the normal DxMessaging-owned
component inspector, it draws a compact UI Toolkit panel above your
component's Inspector body, followed by a horizontal row of action
buttons. User-defined custom editors for specific `MessageAwareComponent`
subclasses keep their normal inspector body; the package injects the
same warning data through Unity's component-header hook as an IMGUI
HelpBox so the user's editor can stay in charge of its body.

![Inspector overlay warning panel (DXMSG009 implicit-hide example) at the top of a MessageAwareComponent subclass Inspector](../images/inspector-overlay/dxmsg009-overlay.png)

The panel follows this shape:

- **Title:** `Missing MessageAwareComponent base calls`
- **Body:** `<FullyQualifiedTypeName> has lifecycle methods that do not chain to MessageAwareComponent. DxMessaging will not function on this component.`
- **Method list:** one row per missing method, such as `OnEnable:
'<type>' overrides MessageAwareComponent.OnEnable but does not call
base.OnEnable(); handlers will not be re-enabled when this component
is enabled.`

When the cache is stale (immediately after a domain reload, before the
first post-reload scan completes), the panel includes a
`Report is cached from previous session; refreshing...` line -- see
[Cached-from-previous-session annotation](#cached-from-previous-session-annotation)
below.

The custom-editor IMGUI path uses Unity's native `HelpBox` instead of
the retained UI Toolkit panel. Its text names the fully-qualified type,
lists the missing methods, includes the same per-method consequence
lines, points to `docs/reference/analyzers.md`, and appends
`(cached from previous session; refreshing...)` while the cache is stale.
It exposes the same **Open Script** and **Ignore this type** actions.

The method list is taken straight from the analyzer's per-type report
-- typically one of `Awake`, `OnEnable`, `OnDisable`, `OnDestroy`, or
`RegisterMessageHandlers`. A single component can list multiple methods
if more than one override is broken.

### Cached-from-previous-session annotation

After a domain reload -- when you enter Play Mode, recompile, or open the
Editor -- the overlay needs a moment to rebuild its scan. Rather than
flashing an empty Inspector and then suddenly showing a warning, the
package eagerly loads the previous session's cache from
`Library/DxMessaging/baseCallReport.json` so the warning is visible
immediately.

While that cached data is being refreshed, the warning panel includes
the stale-cache note `Report is cached from previous session;
refreshing...`. The IMGUI header-hook path appends
`(cached from previous session; refreshing...)` to the HelpBox body
instead.

Once the first post-reload scan completes (typically within a single
editor tick after assembly reload completes), the harvester flips its
`IsFreshThisSession` flag and the note disappears. You do not need to
do anything -- the Inspector repaints automatically. The annotation
exists so you understand the data is from the previous session in the
unlikely event you have just edited the offending source code and the
Inspector is showing a warning that the latest compile would have
fixed.

## Three Inspector Actions

Below the warning surface the overlay draws a horizontal action row.
The buttons that appear depend on whether the component's
fully-qualified type name is currently in the project ignore list.

### Default (warning) state

When the component is **not** ignored, you see two buttons:

![Open Script and Ignore this type buttons](../images/inspector-overlay/inspector-actions.png)

- **Open Script** -- opens the offending component's source file at the
  top; when the legacy console bridge is enabled and a line number is
  available, the file opens at that line.
- **Ignore this type** -- appends the component's fully-qualified type
  name to the ignore list in `Assets/Editor/DxMessagingSettings.asset`;
  the generated sidecar is refreshed for the analyzer. The next
  Inspector repaint flips the overlay into its info shape (below).
  The mutation is deferred to the next editor frame so the current GUI
  cycle completes cleanly -- there is no perceptible delay.

### Ignored state

When the component **is** in the ignore list, the overlay shows the info
shape -- the type is explicitly excluded from the base-call check -- and
the action row collapses to a single button:

![Stop ignoring action for an excluded type](../images/inspector-overlay/inspector-ignored.png)

- **Stop ignoring** -- removes the component's fully-qualified type
  name from the ignore list. The ignored info state clears on the next
  repaint; if the type still violates the rule, the warning returns
  after the next fresh scan, compile, or manual rescan.

!!! warning
Adding a type to the ignore list silences the **overlay** and the compile-time
base-call analyzer for that type, but it does not change the runtime behaviour.
If the override genuinely never reaches `base.OnEnable()`, the messaging system
on that component is still dead. For finer-grained control, the source-level
`[DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]` attribute suppresses the
analyzer at the class or method level and is checked **before** the project
ignore list -- see the [Suppression precedence ordering](../reference/analyzers.md#suppression-precedence)
for the full priority. Use either suppression path only when the silencing is
genuinely intentional (for example, a deliberate adapter that should not
participate in messaging) and document the reason somewhere your team can find
it.

## Project Settings Panel

The package registers a UI Toolkit Project Settings page under
**Project Settings > Wallstop Studios > DxMessaging**. The page is
split into three sections:

![DxMessaging Project Settings panel](../images/inspector-overlay/project-settings-panel.png)

### Diagnostics

- **Diagnostics Targets** -- flags-enum field (`Off`, `Editor`,
  `Runtime`, `All`) controlling where global diagnostics are enabled.
  See [Diagnostics](diagnostics.md) for what this toggle activates.
- **Message Buffer Size** -- integer; the default ring-buffer size
  used by every newly-created bus and token when diagnostics are
  active. Defaults to `IMessageBus.DefaultMessageBufferSize`.

### Editor Safety

- **Suppress Domain Reload Warning** -- checkbox; disables the warning
  Unity shows when "Enter Play Mode Options" skips a domain reload.
  DxMessaging still resets its statics, so the warning is noise on
  most projects.

### Inspector Checks

- **Base-Call Check Enabled** -- master toggle for the Inspector
  overlay. When `false`, the overlay is silenced; the underlying
  analyzer still emits the Console warning unless `.editorconfig`
  says otherwise.
- **Use Console Bridge** -- opt-in legacy bridge that unions Unity
  Console / compiler-message warnings into the IL-reflection scan.
  Default off.

The settings asset itself lives at
`Assets/Editor/DxMessagingSettings.asset` and stores additional
fields the overlay relies on:

- The project ignore list
  (`DxMessagingSettings.BaseCallIgnoredTypes`), edited from the asset
  Inspector at `Assets/Editor/DxMessagingSettings.asset` (the Project
  Settings panel does not currently expose the ignore list). Mirrored
  to the sidecar `Assets/Editor/DxMessaging.BaseCallIgnore.txt` that
  the analyzer reads via `csc.rsp`'s `-additionalfile:` switch.

!!! note
The Inspector overlay's **Ignore this type** / **Stop ignoring** buttons read and write the same ignore-list field that the settings asset exposes. You can also bulk-edit the list directly from the asset Inspector.

For the field-by-field semantics -- including the ScriptableObject
behaviour around `OnValidate` regenerating the sidecar -- see
[Inspector integration](../reference/analyzers.md#inspector-integration)
in the analyzer reference.

## Tools > Wallstop Studios > DxMessaging > Rescan Base-Call Warnings

The package adds a manual rescan menu entry:

![Tools menu showing DxMessaging Rescan Base-Call Warnings entry](../images/inspector-overlay/tools-menu-rescan.png)

Click **Tools > Wallstop Studios > DxMessaging > Rescan Base-Call Warnings** to
re-run the harvester on demand. You normally do not need to invoke
this -- the package re-scans automatically on every assembly reload
and after every per-assembly compilation event -- but it is useful
when you have just toggled the master setting, edited the ignore
list outside of Unity, or want to confirm that a fix has cleared a
warning before the next domain reload.

The menu action is a no-op while Unity is mid-compile or mid-import.
Automatic scheduled scans requeue for the next safe tick; if you click
the menu during the blocked window, click it again after Unity finishes
compiling or importing.

## Worked Example

Let's walk through the most common case end-to-end. Suppose you have
a `HealthComponent` that derives from `MessageAwareComponent`:

```csharp
using DxMessaging.Unity;
using DxMessaging.Core.Messages;
using UnityEngine;

public sealed class HealthComponent : MessageAwareComponent
{
    protected override void OnEnable()
    {
        // Forgot base.OnEnable() -- Token.Enable() never runs,
        // every handler this component registered is dead.
        Debug.Log("HealthComponent enabled");
    }

    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterComponentTargeted<TookDamage>(this, OnHit);
    }

    private void OnHit(ref TookDamage m) => Debug.Log($"hit for {m.amount}");
}
```

### What you see

After the next compile, the Console shows a `DXMSG006` warning
naming `Game.HealthComponent.OnEnable`. When you click into a
GameObject that has `HealthComponent` attached, the Inspector
renders the overlay warning panel at the top of the component:

![HealthComponent Inspector with the missing-base-call warning panel visible](../images/inspector-overlay/worked-example-before.png)

The panel names `OnEnable` in its missing-method list and gives the
runtime consequence for the missing base call. **Open Script** opens
the component source file; when line data is available, it opens at
that line.

### Fixing it

Add the base call:

```csharp
protected override void OnEnable()
{
    base.OnEnable();   // <-- the fix
    Debug.Log("HealthComponent enabled");
}
```

After the next compile, the `DXMSG006` Console entry is gone. The
Inspector warning disappears after the compile-triggered scan refreshes
the report; if the Console is already clean but the Inspector still
shows cached data, run **Tools > Wallstop Studios > DxMessaging > Rescan Base-Call
Warnings** to refresh the overlay snapshot:

![Same HealthComponent Inspector with the warning panel cleared after the fix](../images/inspector-overlay/worked-example-after.png)

That is the entire loop: warning > fix > silence.

### When the fix is intentional

If your override genuinely needs to skip the base implementation, or your fix delegates the base call into a helper method (a known false positive of the textual matcher -- see [Detection policy (good-faith textual match)](../reference/analyzers.md#detection-policy-good-faith-textual-match)), suppress the analyzer at the class or method level with `[DxIgnoreMissingBaseCall]`:

```csharp
using DxMessaging.Core.Attributes;

public sealed class FlashyComponent : MessageAwareComponent
{
    [DxIgnoreMissingBaseCall]
    protected override void Awake() => CallHelperThatChainsToBase();

    private void CallHelperThatChainsToBase() => base.Awake();
}
```

Each suppression emits an audit-only [`DXMSG008`](../reference/analyzers.md#dxmsg008-opt-out-marker) so the opt-out shows up in your build report.

## Related

- [Roslyn Analyzers & Diagnostics](../reference/analyzers.md) -- every
  diagnostic id, the suppression-precedence ordering, and the Unity 2021
  setup notes.
- [Unity Integration](unity-integration.md) -- the inheritance contract
  the analyzer enforces and the recommended `MessageAwareComponent`
  patterns.
- [Diagnostics](diagnostics.md) -- diagnostics targets, registration
  logging, and emission history.
- [Troubleshooting](../reference/troubleshooting.md) -- runtime symptoms
  ("my handler never fires") and how they map back to base-call
  mistakes.
