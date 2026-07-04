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
1. Select `Player Ship`, `Enemy Drone`, and `HUD Console` to inspect local
   diagnostics counters.

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
- Flow Graph shows three receiver components, three message types, targeted
  routes to each receiver, exact-source broadcast routes for `Player Ship` and
  `Enemy Drone`, and broadcast-without-source routes for all receivers.
- The component diagnostics panel shows enabled listener diagnostics and local
  emissions for each receiver.

This sample is intentionally small and deterministic. If a tool surface changes,
update this README, the scene, and
`DiagnosticsToolingSampleContractTests` together.
