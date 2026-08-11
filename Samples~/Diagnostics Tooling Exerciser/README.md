# Diagnostics Tooling Exerciser Sample

This sample imports a scene that generates deterministic diagnostics data for
the package editor tools.

## What It Exercises

- **Message Monitor**: global history, component diagnostics, typed filters,
  context filters, visible message-type lanes, and visible context lanes.
- **Flow Graph**: untargeted, targeted, broadcast, exact-source broadcast,
  broadcast-without-source, `RegisterGlobalAcceptAll`, trace IDs, route-kind
  lanes, component lanes, target lanes, context lanes, and route maps.
- **Inspector overlay**: each receiver is a `MessageAwareComponent` with a live
  `MessagingComponent`, enabled token diagnostics, public counters, and recent
  payload fields.
- **Project Settings**: the runner enables global diagnostics at play start, so
  Project Settings changes to diagnostics targets and message buffer size are
  visible immediately when the scene is rerun.

## Run It

1. Import **Diagnostics Tooling Exerciser** from the Package Manager Samples tab.
1. Open `DiagnosticsToolingExerciser.unity`.
1. Follow the **DxMessaging Guided Tour** window that opens with the scene. It
   starts Play Mode, emits deterministic traffic, opens Message Monitor and Flow
   Graph, selects all receivers, and links to the relevant Project Settings.

Reopen the guide at any time from **Tools > Wallstop Studios > DxMessaging >
Diagnostics Tooling Guided Tour**. Each step remains safe when the scene is not
running: emit actions stay disabled until the runner is active, and the status
panel explains whether the runner and receivers are available.

The active runner starts its burst after the initial scene load and again from
`OnEnable` instead of relying on a one-shot `Start` callback. Consecutive Play
entries therefore reset and repopulate the tooling data when **Enter Play Mode
Options** disables domain and scene reload. A per-activation guard deduplicates
the runner's `OnEnable` and post-scene-load callbacks. Active receivers
deliberately replace their token after the initial scene load so a stale enabled
flag cannot hide reset bus state.

Disabling the runner cancels its scheduled invokes and coroutines, releases its
reference-counted diagnostics lease, and restores the bus's original diagnostics state when the
last runner exits. Closing the guide pauses its UI Toolkit schedule. The sample does not leave
background work, registrations, or global configuration behind.

The runner also exposes context-menu commands:

- **Emit One Of Each** sends one untargeted, targeted, and broadcast pass.
- **Emit Burst** repeats that pass using `burstCount`.
- **Reset Counters And Emit Burst** clears all receiver counters and emits the
  configured burst. Message Monitor history stays visible, and sequence-based
  trace IDs continue forward without duplicates.
- Receiver **Reset Counts** clears the inspector counters without changing
  registrations.

## Expected Tool Data

After the default play-start burst:

- `DiagnosticsToolingExerciser.Sequence` is `3`.
- Message Monitor global history includes `ToolingPulse`, `ToolingCommand`, and
  `ToolingSignal` entries with trace IDs like `sample-pulse-001`.
- Flow Graph shows three receiver components, four message nodes (the three
  concrete messages plus `ANY MESSAGE`), 15 routes, and 33 recent trace paths.
  Its primary canvas places the four messages on the left, the three receivers
  on the right, and draws all 15 live connections. It starts with no default
  selection; clicking any part of a connection opens focused route and activity
  details while evidence and diagnostics remain collapsed. Expanded evidence
  uses breadcrumb context trails plus compact source-linked message and call-site
  rows. Breadcrumbs backed by exact captured Unity-object identities and
  relationship endpoints select the matching graph item; **Route roster** reveals
  each exact receiver ID, registration subtype, context, and context ID on demand.
  Diagnostics split route health from trace coverage; component and message
  selections show distinct busiest paths as directed relationship records,
  while route selections omit those redundant aggregates. The full text report
  remains available through **Copy diagnostics**. The textual
  route and trace reports remain inside the collapsed **Analysis and Raw Data**
  section.
- The component diagnostics panel shows enabled listener diagnostics and local
  emissions for each receiver.

This sample is intentionally small and deterministic. If a tool surface changes,
update this README, the scene, and
`DiagnosticsToolingSampleContractTests` together.
