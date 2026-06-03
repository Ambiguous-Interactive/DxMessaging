---
title: "Unity License Bootstrap"
id: "unity-license-bootstrap"
category: "unity"
version: "4.0.0"
created: "2026-05-05"
updated: "2026-05-22"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/unity/run-ci-tests.ps1"
    - path: "scripts/unity/activate-license.sh"
    - path: "scripts/unity/run-tests.sh"
    - path: ".github/actions/validate-unity-license/action.yml"
    - path: ".github/actions/return-unity-license/action.yml"
    - path: ".devcontainer/devcontainer.json"
    - path: ".github/workflows-disabled/unity-tests.yml"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "unity"
  - "license"
  - "serial"
  - "ulf"
  - "personal"
  - "ci"
  - "secrets"

complexity:
  level: "basic"
  reasoning: "One primary CI path (classic serial activation) plus a local .ulf/serial fallback; no algorithmic content."

impact:
  performance:
    rating: "none"
    details: "Tooling only"
  maintainability:
    rating: "high"
    details: "CI activates with a classic serial and guarantees a return on every exit path; ULF remains only for local fallback"
  testability:
    rating: "low"
    details: "Validated implicitly: the runner refuses to launch without a working license path"

prerequisites:
  - "A Unity ID (sign-up at id.unity.com)"
  - "For CI: a paid Unity serial plus the account email and password"
  - "Docker socket reachable inside the devcontainer (for live --check)"

dependencies:
  packages: []
  skills:
    - "headless-test-runner"
    - "unity-license-return-guarantee"

applies_to:
  languages:
    - "Bash"
    - "PowerShell"
  frameworks:
    - "Unity"
  versions:
    unity: ">=2021.3"

aliases:
  - "Unity license"
  - "Serial activation"
  - "ULF activation"
  - "activate-license.sh"

related:
  - "headless-test-runner"
  - "unity-license-return-guarantee"
  - "unity-ci-matrix"
  - "cicd-devcontainer-workflows"

status: "stable"
---

<!-- trigger: unity, license, serial, ulf, returnlicense, activation, secret | Classic serial activation (CI) plus ULF/serial local fallback for headless Unity | Core -->

# Unity License Bootstrap

> **One-line summary**: CI activates Unity with a classic serial (`UNITY_SERIAL` + `UNITY_EMAIL` + `UNITY_PASSWORD`) and guarantees a `-returnlicense` on every exit path; a raw `.ulf` (`UNITY_LICENSE` / `UNITY_LICENSE_B64` / `UNITY_LICENSE_FILE`) or a local serial remains the LOCAL fallback for `run-tests.sh` / `run-tests.ps1`.

## When to Use

- First-time setup on a new dev machine, codespace, or CI runner.
- After the Unity serial or credentials rotate, or a local `.ulf` expires.
- When the runner reports `Error: No Unity license configured.`
- When a CI run fails with `Failed to activate` or `No valid Unity Editor license found`.

## License Types

Classic serial activation is the primary, only-supported CI activation path. A
`.ulf` (Personal) or a local serial are LOCAL fallback paths only.

| Type           | Scope               | Activation Method                                              | Notes                                                                       |
| -------------- | ------------------- | -------------------------------------------------------------- | --------------------------------------------------------------------------- |
| Serial (paid)  | CI (primary)        | `UNITY_SERIAL` + `UNITY_EMAIL` + `UNITY_PASSWORD` -> `-serial` | The single CI path. Activated before the editor and returned on every exit. |
| Personal / ULF | Local fallback only | Raw `.ulf` in `UNITY_LICENSE` or base64 `UNITY_LICENSE_B64`    | Used by `run-tests.sh` / `run-tests.ps1` when no serial is configured.      |
| Serial (local) | Local fallback only | Same three vars as CI                                          | Local convenience; the local runner returns the seat on exit too.           |

The floating licensing server has been RETIRED. The `UNITY_LICENSING_SERVER`
secret is removed and must not be reintroduced; the `validate-unity-license`
action rejects it.

## Classic Serial Path (CI, Primary)

CI activates Unity with the classic serial command line. Three GitHub secrets are
required: `UNITY_SERIAL`, `UNITY_EMAIL`, and `UNITY_PASSWORD`.

How `scripts/unity/run-ci-tests.ps1` activates and returns Unity:

```text
# Activate (throws on failure)
Unity.exe -quit -batchmode -nographics -serial <UNITY_SERIAL> \
  -username <UNITY_EMAIL> -password <UNITY_PASSWORD> -logFile -

# Return (best-effort, never throws)
Unity.exe -quit -batchmode -nographics -returnlicense \
  -username <UNITY_EMAIL> -password <UNITY_PASSWORD> -logFile -
```

`run-ci-tests.ps1` wraps these as `Invoke-UnityLicenseActivate` (throws on
failure) and `Invoke-UnityLicenseReturn` (best-effort, never throws). The license
is returned on EVERY exit path through four redundant layers: a defensive
return-at-start, a PowerShell `try`/`finally` return, a workflow `if: always()`
step (`./.github/actions/return-unity-license`), and the next run's
return-at-start on the same persistent runner. Serial licenses have no
server-side reclaim and only a small seat pool, so those return layers are the
only thing that frees a seat -- the full guarantee, the seat-limit tradeoff, and
its enforcement live in [[unity-license-return-guarantee]].

SECURITY: never echo or log the serial or password; license logs go to
`RUNNER_TEMP`, never to uploaded artifacts.

### CI Secrets

| Secret           | Value                  | Required |
| ---------------- | ---------------------- | -------- |
| `UNITY_SERIAL`   | Paid Unity serial      | Yes      |
| `UNITY_EMAIL`    | Unity account email    | Yes      |
| `UNITY_PASSWORD` | Unity account password | Yes      |

The retired `UNITY_LICENSING_SERVER` secret is removed from CI and must not be
re-wired. The active workflows under `.github/workflows/unity-*.yml` forward the
three serial secrets to `scripts/unity/run-ci-tests.ps1` on the self-hosted
Windows runners. Each workflow runs `./.github/actions/validate-unity-license`
(it checks that the three serial secrets are present and errors if the retired
`UNITY_LICENSING_SERVER` is still set) BEFORE acquiring the central organization
Unity lock, so a misconfigured license fails with a clear diagnostic before Unity
starts or blocks the shared seat. Inside the org-lock window, every Unity
workflow also has an `if: always()` step (`./.github/actions/return-unity-license`)
that returns the license if the process is killed, placed before the lock
release. The `.github/workflows-disabled/*` files are ubuntu game-ci reference
mirrors that pass the serial via `unitySerial: ${{ secrets.UNITY_SERIAL }}`,
`unityEmail`, and `unityPassword`.

## Local Fallback Path (Serial / ULF)

Local development prefers a serial when configured and falls back to a `.ulf`
otherwise. `run-tests.sh`, `run-tests.ps1`, and `activate-license.sh` use a paid
serial (`UNITY_SERIAL` + `UNITY_EMAIL` + `UNITY_PASSWORD`) when all three are set,
and otherwise fall back to a raw `.ulf` (`UNITY_LICENSE` / `UNITY_LICENSE_B64` /
`UNITY_LICENSE_FILE`). The local runner returns the seat on exit (the EXIT trap
runs `-returnlicense`) so a serial-based local run does not leak a seat.

1. For a serial, export the three vars:

   ```bash
   export UNITY_SERIAL='...' UNITY_EMAIL='you@example.com' UNITY_PASSWORD='...'
   ```

   The local runner uses `-serial` activation and returns the seat on exit.

1. For the ULF fallback, obtain a `.ulf` through Unity Hub or Unity's manual
   activation flow, then use the base64 convenience variable:

   ```bash
   bash scripts/unity/activate-license.sh --apply path/to/Unity_lic.ulf
   ```

   Add the printed `UNITY_LICENSE_B64` export to your shell profile.

1. Verify: `bash scripts/unity/activate-license.sh --check` reports which path
   is active (serial, raw `.ulf`, or base64 `.ulf`).

1. Run: `bash scripts/unity/run-tests.sh --platform editmode`.

### Email/Password-Only Caveat

Do not configure only `UNITY_EMAIL` + `UNITY_PASSWORD`. Email/password alone is
not a supported headless container path; pair them with a `UNITY_SERIAL` for
serial activation, or use a `.ulf` instead.

## Common Failures

| Signature                                                           | Cause                                                      | Remediation                                                                                 |
| ------------------------------------------------------------------- | ---------------------------------------------------------- | ------------------------------------------------------------------------------------------- |
| `UNITY_SERIAL is required` (CI)                                     | One of the three serial secrets is unset in CI.            | Set `UNITY_SERIAL`, `UNITY_EMAIL`, and `UNITY_PASSWORD` repository secrets.                 |
| `Retired Unity activation secret UNITY_LICENSING_SERVER is set`     | The retired licensing-server secret remains in CI.         | Remove `UNITY_LICENSING_SERVER` from the repository/workflows.                              |
| `Failed to activate` / `No Unity license configured` (local)        | Serial unset/invalid and ULF path unset.                   | Set the three serial vars, or fall back to `UNITY_LICENSE` / `UNITY_LICENSE_B64`.           |
| `com.unity.editor.headless` / `No valid Unity Editor license found` | Email/password-only path or invalid entitlement.           | Pair credentials with a serial, or use a `.ulf`; email/password alone is unsupported.       |
| `License client failed to start`                                    | Activation hiccup, expired `.ulf`, or wrong credentials.   | Retry; then verify the serial/credentials or the local ULF.                                 |
| `LICENSE SYSTEM ... License is not valid for this build target`     | The `.ulf` was issued for a different Unity major version. | Refresh the Hub on the issuing dev machine and re-run `--apply`.                            |
| `Warn: <path> does not look like a Unity license file.`             | `--apply` was pointed at the wrong file.                   | Re-run with the actual `.ulf` from the Unity Hub install path.                              |
| `All serial seats consumed` / activation blocked                    | A prior run leaked a seat, or both seats are held.         | The next run's return-at-start reclaims a leaked seat; if persistent, raise the seat count. |
| Network unreachable from container                                  | Corporate proxy or container egress blocked.               | Set `HTTP_PROXY` / `HTTPS_PROXY` in the devcontainer env, or run the bootstrap on the host. |

## Renewal

- **Serial** (CI primary): if activation fails after a renewal, verify the serial
  and credentials in the Unity dashboard and update the `UNITY_SERIAL` /
  `UNITY_EMAIL` / `UNITY_PASSWORD` secrets.
- **ULF** (local fallback): when the file expires, refresh it and update
  `UNITY_LICENSE` or `UNITY_LICENSE_B64`.

## See Also

- [Unity License Return Guarantee](./unity-license-return-guarantee.md) [[unity-license-return-guarantee]]
- [Headless Test Runner](./headless-test-runner.md)
- [Unity CI Matrix](./unity-ci-matrix.md)
- [CI/CD Devcontainer Workflows](../github-actions/cicd-devcontainer-workflows.md)

## References

- Unity command-line arguments (`-serial`, `-returnlicense`): <https://docs.unity3d.com/Manual/CommandLineArguments.html>
- Unity license activation methods: <https://docs.unity3d.com/Manual/LicenseActivationMethods.html>
- GameCI activation guide: <https://game.ci/docs/github/activation/>
- Source: `scripts/unity/run-ci-tests.ps1`, `scripts/unity/activate-license.sh`, `scripts/unity/run-tests.sh`

## Changelog

| Version | Date       | Changes                                                                                                                                                                                     |
| ------- | ---------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 4.0.0   | 2026-05-22 | Floating licensing server RETIRED; classic serial (`UNITY_SERIAL` + `UNITY_EMAIL` + `UNITY_PASSWORD`) is now the primary, only CI path with a guaranteed return; ULF is the local fallback. |
| 3.0.0   | 2026-05-21 | Floating licensing server (`UNITY_LICENSING_SERVER`) was the primary CI path; legacy secrets removed from CI; ULF/serial local fallback (superseded by the serial cutover).                 |
| 2.0.0   | 2026-05-05 | ULF and serial activation paths; email/password-only caveat.                                                                                                                                |
