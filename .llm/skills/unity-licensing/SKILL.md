---
name: unity-licensing
description: "Unity Editor licensing for CI: classic serial activation with the UNITY_SERIAL, UNITY_EMAIL, and UNITY_PASSWORD secrets, the four-layer always-return guarantee (return-at-start, PowerShell try/finally, an if: always() return-unity-license workflow step inside the org-lock window, and the next run's return-at-start), the seven-step per-job flow, the roughly two-seat no-reclaim tradeoff, and the retired UNITY_LICENSING_SERVER secret that must not come back. Use when a Unity job reports Failed to activate, No valid Unity Editor license found, or consumed serial seats; when wiring Unity secrets on a runner; or when editing license handling in run-ci-tests.ps1 or a Unity workflow. Local Unity needs no license."
metadata:
  category: "unity"
  tags: "unity, serial, license, return, leak, seat, ci"
---

# Unity Licensing

CI activates Unity with a classic serial and returns it on every exit path. A serial has no server-side reclaim and only about two concurrent seats, so the return layers are the only thing that frees one.

## When to use

- First-time CI runner setup, or wiring the Unity secrets on a new repository.
- After the Unity serial or account credentials rotate.
- A CI run fails with `Failed to activate`, `No valid Unity Editor license found`, `License client failed to start`, or reports all serial seats consumed.
- Reviewing or changing license activation and return handling in `scripts/unity/run-ci-tests.ps1`.
- Adding, removing, or reordering the `if: always()` `return-unity-license` step in a Unity workflow.

Local Unity verification needs NO license. The devcontainer ships no Unity build; local runs drive the host editor through the MCP loop, and that editor holds its own license. There is no local `.ulf`, no `UNITY_LICENSE` / `UNITY_LICENSE_B64`, and no local serial.

## Rules

### Secrets and the activation path

- Classic serial activation is the only supported CI path. Three repository secrets are required: `UNITY_SERIAL`, `UNITY_EMAIL`, `UNITY_PASSWORD`.
- The floating licensing server is RETIRED. `UNITY_LICENSING_SERVER` must not be reintroduced; the `./.github/actions/validate-unity-license` action rejects it, and `findForbiddenUnityLicenseSecretViolations` plus the static guard fail any workflow that re-wires it.
- `scripts/unity/run-ci-tests.ps1` wraps the CLI in two functions with deliberately different failure semantics. `Invoke-UnityLicenseActivate` runs `-serial <UNITY_SERIAL> -username <UNITY_EMAIL> -password <UNITY_PASSWORD>` and THROWS on failure, so a job that cannot activate fails loudly instead of running unlicensed. `Invoke-UnityLicenseReturn` runs `-returnlicense` best-effort and NEVER throws, so a return attempt cannot mask the real job result.

### The four-layer always-return guarantee

1. **Return-at-start.** Each job calls `Invoke-UnityLicenseReturn` defensively before activating, reclaiming any seat a prior force-killed run leaked on that persistent runner.
1. **PowerShell `try`/`finally`.** `run-ci-tests.ps1` activates inside a `try` and returns in the `finally`, covering both a clean exit and an editor throw or non-zero exit.
1. **Workflow terminal return step.** Every Unity workflow invokes the
   centrally pinned `return-unity-license` action after diagnostics and before
   classify/release/gate, scoped to an acquired lock, so a failed Unity step
   still returns the license before the next job acquires the lock.
1. **The next run's return-at-start.** If the whole runner process is killed and the three layers above never run, layer 1 of the next run on that machine reclaims the seat.

### Per-job flow

Validate the secrets with `./.github/actions/validate-unity-license` BEFORE acquiring the lock, so a misconfiguration fails with a clear diagnostic before Unity starts or blocks the shared seat. Then acquire the org build lock (`wallstop-organization-builds`, `max-parallel: 1`), return-at-start, activate, run Unity (editmode / playmode / standalone IL2CPP) against the generated project, return in the `finally`, and run the `if: always()` return step before releasing the lock.

### Contract invariants to check on every change

- The return lives in a `finally`, and the defensive return-at-start is still present.
- Every Unity workflow keeps the acquired-scoped central return followed by
  classifier, release, and final cleanup gate inside the org-lock window.
- No workflow references `secrets.UNITY_LICENSING_SERVER`, and every Unity job wires all three serial secrets.

Anti-patterns: returning the license only on the success path; dropping the `if: always()` step and relying on the next run's return-at-start; echoing or logging the serial or password; re-adding the retired licensing-server secret.

### Security

Never echo or log the serial or password. They are passed as Unity CLI arguments only - not printed, not written to an artifact, not added to a shell trace. License activation and return logs go to `RUNNER_TEMP`, never into uploaded artifacts, so a credential cannot leak through a downloadable log.

### The seat-limit tradeoff, stated honestly

A serial has no server-side reclaim and typically about two concurrent activations, and the schema-5 organization lock admits at most two distinct runners. Because the runners are persistent, a leaked seat is normally freed by the next job landing on the same machine. But the reaper can only quarantine a stale lock holder; it cannot return an activation in Unity's portal. If both machines leak, operators must reconcile the portal manually. The four layers make a permanent leak very unlikely; the small seat pool remains a real constraint.

### Common failures

| Signature                                                       | Cause                                         | Remediation                                                                     |
| --------------------------------------------------------------- | --------------------------------------------- | ------------------------------------------------------------------------------- |
| `UNITY_SERIAL is required`                                      | One of the three secrets is unset             | Set all three repository secrets                                                |
| `Retired Unity activation secret UNITY_LICENSING_SERVER is set` | Retired secret still present                  | Remove it from the repository and workflows                                     |
| `Failed to activate` / `No valid Unity Editor license found`    | Serial unset or invalid, or wrong credentials | Verify the three secrets against the Unity dashboard                            |
| `License client failed to start`                                | Activation hiccup or wrong credentials        | Retry, then verify the serial and credentials                                   |
| All serial seats consumed                                       | A prior run leaked a seat, or both are held   | The next run's return-at-start reclaims it; if persistent, raise the seat count |

## References

| Document                                                                            | Purpose                                                                                                                                            |
| ----------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| [unity-license-bootstrap.md](./references/unity-license-bootstrap.md)               | CI secret setup, the serial activation and return command lines, why local Unity needs no license, and the common-failure remediation table.       |
| [unity-license-return-guarantee.md](./references/unity-license-return-guarantee.md) | The four-layer return guarantee, the seven-step per-job flow, leak failure modes, contract invariants, anti-patterns, and the seat-limit tradeoff. |
