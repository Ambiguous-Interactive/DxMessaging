<!-- trigger: serial, license, leak, seat, return, returnlicense, finally | Serial-activation always-return guarantee for zero leaked seats | Core -->

# Unity License Return Guarantee

> **One-line summary**: CI activates Unity with a classic serial (`UNITY_SERIAL` + `UNITY_EMAIL` + `UNITY_PASSWORD`) and attempts return through four redundant layers (return-at-start, PowerShell `try`/`finally`, an `if: always()` workflow step, and the next run's return-at-start). Cleanup evidence or the Unity portal must confirm the seat was actually freed.

## When to Use

- Reviewing or changing `scripts/unity/run-ci-tests.ps1` license activation or
  return handling.
- Adding, removing, or reordering the centrally pinned license-return,
  classification, release, and gate steps in a Unity workflow.
- Diagnosing a Unity job that fails to activate because all serial seats are
  consumed.
- Reviewing any change to the Unity workflows' license activation/return steps.

## Why It Matters

A serial Unity license has NO server-side auto-reclaim and typically only about
two concurrent activation seats. There is no floating licensing server to expire
a stale lease: once a run activates a seat and fails to return it, that seat
stays consumed until something explicitly runs `-returnlicense`. The runners are
persistent self-hosted Windows machines, so a force-killed run leaves its
activation behind on that machine across runs. The schema-5 organization lock
admits at most two distinct runners and reduces effective capacity for
quarantines or an account incident. A single un-returned activation plus one
clean concurrent activation can still exhaust the portal seats. The
return layers below give the next job on that runner another return attempt.
They cannot prove a portal return from a command exit code alone.

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

## The Four-Layer Return Attempt Contract

Four independent layers attempt to return the license on normal and failed
exit paths. There is no floating server or server-side reclaim. A successful
return frees a seat; the attempt alone does not prove that it did:

1. **Return-at-START of each job.** Before activating, the job runs a defensive
   `Invoke-UnityLicenseReturn`. On a persistent runner this can reclaim a seat
   a prior force-killed run leaked on that machine.
1. **PowerShell `try`/`finally` return.** `run-ci-tests.ps1` activates inside a
   `try` and calls `Invoke-UnityLicenseReturn` in the `finally`, so a clean exit
   AND an editor throw / non-zero both attempt the return.
1. **Workflow terminal return step.** Every Unity workflow invokes the
   centrally pinned `return-unity-license` action after diagnostics and before
   classify/release/gate, scoped to an acquired lock. A failed Unity step still
   reaches this terminal cleanup chain before another job can acquire the lock.
1. **The next run's return-at-start.** On a persistent self-hosted runner, if all
   three layers above are somehow skipped (for example the whole runner process
   is killed), layer 1 of the NEXT run retries the return on that machine.

## The Per-Job Flow (7 steps)

Each Unity job follows this order:

1. Validate the Unity secrets via `./.github/actions/validate-unity-license`
   (checks `UNITY_SERIAL` / `UNITY_EMAIL` / `UNITY_PASSWORD` presence and rejects
   the retired `UNITY_LICENSING_SERVER`) BEFORE acquiring the org lock.
1. Acquire the org build lock (`wallstop-organization-builds`, `max-parallel: 1`).
1. Return-at-start: `run-ci-tests.ps1` calls `Invoke-UnityLicenseReturn` to
   attempt to reclaim any seat a prior killed run leaked on this persistent runner.
1. Activate: `Invoke-UnityLicenseActivate` runs the serial activation (throws on
   failure).
1. Run Unity (editmode / playmode / standalone IL2CPP) against the generated
   project.
1. Return: the PowerShell `finally` calls `Invoke-UnityLicenseReturn` on every
   exit path.
1. The acquired-scoped central return action runs inside the org-lock window,
   followed by cleanup classification, exact lock release, and the final
   fail-closed cleanup gate.

## The Seat-Limit Tradeoff (documented honestly)

This is the accepted cost of leaving the floating licensing server behind:

- **No server-side reclaim.** A floating server reclaims a stale lease on expiry.
  A serial has nothing equivalent; only an explicit `-returnlicense` frees a seat.
- **Very few seats.** A serial typically allows only about two concurrent
  activations. With two persistent Windows runners, both can hold a seat at once.
- **How return-at-start compensates.** Because the runners are persistent, the
  next job on the same machine attempts `-returnlicense` before activation. A
  completed command is not proof of a returned seat when cleanup classification
  reports `return-ulf-skipped` or another unknown result.
- **Accepted residual risk.** The scheduled lock reaper can quarantine a stale
  holder and later auto-recover a reservation after its lease and owning run
  end. This restores lock capacity but cannot return an activation in Unity's
  portal. Reconcile inconclusive cleanup in the portal before further licensed
  work. If both machines leak, operators must reconcile the portal and perform
  exact-incident recovery before capacity is restored.

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

Each failure mode has at least one return attempt. With no server-side reclaim,
the return-at-start of the next run is the final automatic retry on a
persistent runner. An unknown cleanup classification still needs portal review.

| Failure mode                 | What happens                                     | Covered by                                                                            |
| ---------------------------- | ------------------------------------------------ | ------------------------------------------------------------------------------------- |
| Clean exit                   | Editor exits 0; script reaches `finally`.        | `try`/`finally` `Invoke-UnityLicenseReturn`.                                          |
| Editor throws / non-zero     | Editor exits non-zero; script reaches `finally`. | `try`/`finally` `Invoke-UnityLicenseReturn`.                                          |
| Step timeout / killed script | Script process is killed; no `finally` runs.     | `if: always()` `return-unity-license` step in the org-lock window.                    |
| Whole runner process killed  | No `finally` and no `if: always()` step run.     | Return-at-start of the NEXT run on the same persistent runner.                        |
| Prior run leaked a seat      | A seat is still activated from a previous run.   | Return-at-start attempts `-returnlicense`; require confirmed cleanup evidence.        |
| Both machines leak at once   | Zero seats free; a new run cannot activate.      | Schema 5 blocks admission; operators clean the portal and recover the exact incident. |

## Contract Invariants (review these on every change)

Automated workflow contract tests cover these invariants; keep them honest in review too:

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
its `finally`. On a persistent runner the next run's return-at-start then gets
another chance to reclaim the seat; do not rely on that alone -- keep the `if: always()` step
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
- [Unity CI Matrix](../../unity-editor-ci/references/unity-ci-matrix.md) [[unity-ci-matrix]]
- [CI/CD Devcontainer Workflows](../../github-workflow-consistency/references/cicd-devcontainer-workflows.md)

## References

- Unity command-line arguments (`-serial`, `-returnlicense`): <https://docs.unity3d.com/Manual/CommandLineArguments.html>
- Unity license activation methods: <https://docs.unity3d.com/Manual/LicenseActivationMethods.html>
- Source: `scripts/unity/run-ci-tests.ps1` and the centrally pinned
  `return-unity-license` action in each licensed workflow

## Changelog

| Version | Date       | Changes                                                                                                                                                                            |
| ------- | ---------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 2.0.1   | 2026-09-17 | Clarified that a return attempt or recovered lock reservation does not prove portal cleanup.                                                                                       |
| 2.0.0   | 2026-05-22 | Rewritten for classic serial activation: four-layer always-return guarantee, 7-step per-job flow, the ~2-seat / no-reclaim tradeoff, security, and the renamed enforcement layers. |
| 1.0.0   | 2026-05-21 | Initial version: floating-license acquire/return bracket, services-config placement, leak failure modes, and four enforcement layers (superseded by the serial cutover).           |
