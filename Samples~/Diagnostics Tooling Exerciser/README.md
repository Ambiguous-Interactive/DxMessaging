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
1. Press Play. The runner emits a burst of one untargeted pulse, one targeted
   command for each receiver, and one broadcast signal for each source.
1. Open **Tools > Wallstop Studios > DxMessaging > Message Monitor**.
1. Open **Tools > Wallstop Studios > DxMessaging > Flow Graph**.
1. Click any route line, including near a message or receiver endpoint, then
   expand its evidence or open a captured message or call-site source link.
1. Select `Player Ship`, `Enemy Drone`, and `HUD Console` to inspect local
   diagnostics counters.

The active runner starts its burst after the initial scene load and again from
`OnEnable` instead of relying on a one-shot `Start` callback. Consecutive Play
entries therefore reset and repopulate the tooling data when **Enter Play Mode
Options** disables domain and scene reload. A per-activation guard deduplicates
the runner's `OnEnable` and post-scene-load callbacks. Active receivers
deliberately replace their token after the initial scene load so a stale enabled
flag cannot hide reset bus state.

The runner also exposes context-menu commands:

- **Emit One Of Each** sends one untargeted, targeted, and broadcast pass.
- **Emit Burst** repeats that pass using `burstCount`.
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
  rows. Diagnostics split route health from trace coverage; component and
  message selections show distinct busiest paths as directed relationship
  records, while route selections omit those redundant aggregates. The full
  text report remains available through **Copy diagnostics**. The textual
  route and trace reports remain inside the collapsed **Analysis and Raw Data**
  section.
- The component diagnostics panel shows enabled listener diagnostics and local
  emissions for each receiver.

This sample is intentionally small and deterministic. If a tool surface changes,
update this README, the scene, and
`DiagnosticsToolingSampleContractTests` together.
