---
title: "Unity License Return Guarantee"
id: "unity-license-return-guarantee"
category: "unity"
version: "2.0.0"
created: "2026-05-21"
updated: "2026-05-22"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/unity/run-ci-tests.ps1"
    - path: ".github/actions/return-unity-license/action.yml"
    - path: ".github/actions/validate-unity-license/action.yml"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "unity"
  - "serial"
  - "license"
  - "return"
  - "leak"
  - "seat"
  - "ci"

complexity:
  level: "intermediate"
  reasoning: "A return-on-every-exit-path guarantee built from four redundant layers across PowerShell try/finally, a workflow if:always() step, and the next run's return-at-start, plus four enforcement layers; no algorithmic content."

impact:
  performance:
    rating: "none"
    details: "Tooling only"
  maintainability:
    rating: "high"
    details: "Serial licenses have only ~2 activation seats and no server-side reclaim; a leaked activation is reclaimed only by an explicit return, so the four return layers must stay intact"
  testability:
    rating: "high"
    details: "A static guard, a behavioral leak-regression smoke test, two validator rules, and a workflow-shape assertion all pin the contract"

prerequisites:
  - "Three CI secrets: UNITY_SERIAL, UNITY_EMAIL, UNITY_PASSWORD"
  - "Familiarity with the Unity license bootstrap (see unity-license-bootstrap)"

dependencies:
  packages: []
  skills:
    - "unity-license-bootstrap"
    - "headless-test-runner"
    - "unity-ci-matrix"

applies_to:
  languages:
    - "PowerShell"
    - "YAML"
    - "JavaScript"
  frameworks:
    - "Unity"
    - "GitHub Actions"
  versions:
    unity: ">=2021.3"

aliases:
  - "Unity serial license return"
  - "Always-return guarantee"
  - "Unity seat return"

related:
  - "unity-license-bootstrap"
  - "headless-test-runner"
  - "unity-ci-matrix"
  - "cicd-devcontainer-workflows"

status: "stable"
---

<!-- trigger: serial, license, leak, seat, return, returnlicense, finally | Serial-activation always-return guarantee for zero leaked seats | Core -->

# Unity License Return Guarantee

> **One-line summary**: CI activates Unity with a classic serial (`UNITY_SERIAL` + `UNITY_EMAIL` + `UNITY_PASSWORD`) and returns the license on EVERY exit path through four redundant layers (return-at-start, PowerShell `try`/`finally`, an `if: always()` workflow step, and the next run's return-at-start) so a crashed or force-killed run cannot permanently squat one of the very few serial activation seats.

## When to Use

- Reviewing or changing `scripts/unity/run-ci-tests.ps1` license activation or
  return handling.
- Adding, removing, or reordering the `if: always()` license-return step
  (`./.github/actions/return-unity-license`) in a Unity workflow.
- Diagnosing a Unity job that fails to activate because all serial seats are
  consumed.
- Reviewing any change to the Unity workflows' license activation/return steps.

## Why It Matters

A serial Unity license has NO server-side auto-reclaim and typically only about
two concurrent activation seats. There is no floating licensing server to expire
a stale lease: once a run activates a seat and fails to return it, that seat
stays consumed until something explicitly runs `-returnlicense`. The runners are
persistent self-hosted Windows machines, so a force-killed run leaves its
activation behind on that machine across runs. The org build lock
(`wallstop-organization-builds`, `max-parallel: 1`) serializes every Unity job
org-wide, so only one seat is needed at a time -- but the small seat pool means a
single un-returned activation, plus one held by a concurrent machine, can
exhaust the pool. The always-return guarantee below exists so no failure mode
leaves an activation behind for longer than the next job's return-at-start.

## The Activation and Return Contract

CI activates and returns with the classic serial CLI (the serial and password
are passed as arguments, never echoed -- see Security):

```text
# Activate (throws on failure)
Unity.exe -quit -batchmode -nographics -serial <UNITY_SERIAL> \
  -username <UNITY_EMAIL> -password <UNITY_PASSWORD> -logFile -

# Return (best-effort, never throws)
Unity.exe -quit -batchmode -nographics -returnlicense \
  -username <UNITY_EMAIL> -password <UNITY_PASSWORD> -logFile -
```

`scripts/unity/run-ci-tests.ps1` wraps these in two functions:

- `Invoke-UnityLicenseActivate` -- runs the serial activation and THROWS if it
  fails, so a job that cannot activate fails loudly instead of running unlicensed.
- `Invoke-UnityLicenseReturn` -- runs `-returnlicense` best-effort and NEVER
  throws, so a return attempt can never mask the real job result or fail the
  cleanup path.

## The Four-Layer Always-Return Guarantee

The license is returned on every exit path by four independent, redundant
layers. There is no floating server and no server-side reclaim, so these four
layers are the ONLY things that free a seat:

1. **Return-at-START of each job.** Before activating, the job runs a defensive
   `Invoke-UnityLicenseReturn`. On a persistent runner this reclaims any seat a
   prior force-killed run leaked on that machine, so a leak survives at most
   until the next run starts on the same runner.
1. **PowerShell `try`/`finally` return.** `run-ci-tests.ps1` activates inside a
   `try` and calls `Invoke-UnityLicenseReturn` in the `finally`, so a clean exit
   AND an editor throw / non-zero both return the license.
1. **Workflow `if: always()` return step.** Every Unity workflow runs
   `./.github/actions/return-unity-license` as an `if: always()` step inside the
   org-lock window (before the lock release), so even a step timeout or killed
   script process returns the license before the next job can acquire the lock.
1. **The next run's return-at-start.** On a persistent self-hosted runner, if all
   three layers above are somehow skipped (for example the whole runner process
   is killed), layer 1 of the NEXT run reclaims the leaked seat on that machine.

## The Per-Job Flow (7 steps)

Each Unity job follows this order:

1. Validate the Unity secrets via `./.github/actions/validate-unity-license`
   (checks `UNITY_SERIAL` / `UNITY_EMAIL` / `UNITY_PASSWORD` presence and rejects
   the retired `UNITY_LICENSING_SERVER`) BEFORE acquiring the org lock.
1. Acquire the org build lock (`wallstop-organization-builds`, `max-parallel: 1`).
1. Return-at-start: `run-ci-tests.ps1` calls `Invoke-UnityLicenseReturn` to
   reclaim any seat a prior killed run leaked on this persistent runner.
1. Activate: `Invoke-UnityLicenseActivate` runs the serial activation (throws on
   failure).
1. Run Unity (editmode / playmode / standalone IL2CPP) against the generated
   project.
1. Return: the PowerShell `finally` calls `Invoke-UnityLicenseReturn` on every
   exit path.
1. Workflow `if: always()` step runs `./.github/actions/return-unity-license`
   inside the org-lock window, then the lock is released.

## The Seat-Limit Tradeoff (documented honestly)

This is the accepted cost of leaving the floating licensing server behind:

- **No server-side reclaim.** A floating server reclaims a stale lease on expiry.
  A serial has nothing equivalent; only an explicit `-returnlicense` frees a seat.
- **Very few seats.** A serial typically allows only about two concurrent
  activations. With two persistent Windows runners, both can hold a seat at once.
- **How return-at-start compensates.** Because the runners are persistent, the
  return-at-start (layer 1) reclaims any seat the previous run on that machine
  leaked. So in normal operation a leaked seat is freed by the next job that
  lands on the same runner -- the seat is not lost forever.
- **Accepted residual risk.** The one scenario the four layers do NOT fully cover
  is BOTH machines leaking a seat simultaneously with zero seats free and no next
  run able to activate to reach its own return-at-start. The maintainer
  considered and DECLINED a scheduled reaper for this. The sanctioned mitigation
  is operational: ask Unity to raise the activation seat count so a transient
  double-leak cannot exhaust the pool.

Do not oversell the guarantee: the four layers make a permanent leak very
unlikely on persistent runners, but the small seat pool is a real constraint, not
a solved problem.

## Security

- NEVER echo or log the serial or password. They are passed as Unity CLI
  arguments only; do not print them, do not write them to an artifact, and do not
  add them to a shell trace.
- License activation/return logs go to `RUNNER_TEMP`, NOT to uploaded artifacts.
  Keeping the license logs out of the artifact bundle prevents a serial or
  credential from leaking through a downloadable log.
- The retired `UNITY_LICENSING_SERVER` secret must not be reintroduced; the
  validator and the static guard reject it.

## Leak Failure Modes

Each failure mode is covered by at least one of the four return layers. With no
server-side reclaim, the return-at-start of the next run is the final backstop on
a persistent runner.

| Failure mode                 | What happens                                     | Covered by                                                          |
| ---------------------------- | ------------------------------------------------ | ------------------------------------------------------------------- |
| Clean exit                   | Editor exits 0; script reaches `finally`.        | `try`/`finally` `Invoke-UnityLicenseReturn`.                        |
| Editor throws / non-zero     | Editor exits non-zero; script reaches `finally`. | `try`/`finally` `Invoke-UnityLicenseReturn`.                        |
| Step timeout / killed script | Script process is killed; no `finally` runs.     | `if: always()` `return-unity-license` step in the org-lock window.  |
| Whole runner process killed  | No `finally` and no `if: always()` step run.     | Return-at-start of the NEXT run on the same persistent runner.      |
| Prior run leaked a seat      | A seat is still activated from a previous run.   | Return-at-start (defensive `-returnlicense` before activating).     |
| Both machines leak at once   | Zero seats free; a new run cannot activate.      | NOT fully covered -- raise the seat count (accepted residual risk). |

## Contract Invariants (review these on every change)

There is no automated validator for these anymore; keep them honest in review:

1. `run-ci-tests.ps1` brackets activation with `Invoke-UnityLicenseActivate` /
   `Invoke-UnityLicenseReturn` (the return lives in a `finally`), and runs a
   defensive return-at-start.
1. Every Unity workflow keeps the `if: always()` `return-unity-license` step
   inside the org-lock window.
1. No workflow reintroduces a `secrets.UNITY_LICENSING_SERVER` reference, and
   every Unity job wires the three serial secrets
   (`UNITY_SERIAL` + `UNITY_EMAIL` + `UNITY_PASSWORD`).

## Anti-Patterns

### Returning the license outside finally

Returning only on the success path leaks the seat whenever the editor throws. The
return MUST live in a `finally` (`Invoke-UnityLicenseReturn`) so it runs on every
exit.

### Dropping the if:always() step

Without the `if: always()` workflow step, a killed or timed-out script never runs
its `finally`. On a persistent runner the seat is then reclaimed only by the next
run's return-at-start; do not rely on that alone -- keep the `if: always()` step
inside the org-lock window.

### Echoing or logging the serial / password

Printing the serial or password, or routing license logs into an uploaded
artifact, leaks a credential. License logs go to `RUNNER_TEMP` and are never
uploaded.

### Re-adding the retired licensing-server secret

The cutover removed `UNITY_LICENSING_SERVER`. Re-wiring it is rejected by
`findForbiddenUnityLicenseSecretViolations` and by the static guard.

## See Also

- [Unity License Bootstrap](./unity-license-bootstrap.md) [[unity-license-bootstrap]]
- [Headless Test Runner](./headless-test-runner.md) [[headless-test-runner]]
- [Unity CI Matrix](./unity-ci-matrix.md) [[unity-ci-matrix]]
- [CI/CD Devcontainer Workflows](../github-actions/cicd-devcontainer-workflows.md)

## References

- Unity command-line arguments (`-serial`, `-returnlicense`): <https://docs.unity3d.com/Manual/CommandLineArguments.html>
- Unity license activation methods: <https://docs.unity3d.com/Manual/LicenseActivationMethods.html>
- Source: `scripts/unity/run-ci-tests.ps1`, `.github/actions/return-unity-license/action.yml`

## Changelog

| Version | Date       | Changes                                                                                                                                                                            |
| ------- | ---------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 2.0.0   | 2026-05-22 | Rewritten for classic serial activation: four-layer always-return guarantee, 7-step per-job flow, the ~2-seat / no-reclaim tradeoff, security, and the renamed enforcement layers. |
| 1.0.0   | 2026-05-21 | Initial version: floating-license acquire/return bracket, services-config placement, leak failure modes, and four enforcement layers (superseded by the serial cutover).           |
