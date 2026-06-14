---
title: "Unity License Bootstrap"
id: "unity-license-bootstrap"
category: "unity"
version: "5.0.0"
created: "2026-05-05"
updated: "2026-06-14"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/unity/run-ci-tests.ps1"
    - path: ".github/actions/validate-unity-license/action.yml"
    - path: ".github/actions/return-unity-license/action.yml"
    - path: ".github/workflows-disabled/unity-tests.yml"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "unity"
  - "license"
  - "serial"
  - "ci"
  - "secrets"

complexity:
  level: "basic"
  reasoning: "One CI activation path (classic serial); no algorithmic content."

impact:
  performance:
    rating: "none"
    details: "Tooling only"
  maintainability:
    rating: "high"
    details: "CI activates with a classic serial and guarantees a return on every exit path"
  testability:
    rating: "low"
    details: "Validated implicitly: CI refuses to launch Unity without a working license path"

prerequisites:
  - "A Unity ID (sign-up at id.unity.com)"
  - "For CI: a paid Unity serial plus the account email and password"

dependencies:
  packages: []
  skills:
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

related:
  - "unity-license-return-guarantee"
  - "unity-ci-matrix"
  - "mcp-test-loop"
  - "cicd-devcontainer-workflows"

status: "stable"
---

<!-- trigger: unity, license, serial, returnlicense, activation, secret | Classic serial activation for CI Unity (local Unity needs no license) | Core -->

# Unity License Bootstrap

> **One-line summary**: CI activates Unity with a classic serial (`UNITY_SERIAL` + `UNITY_EMAIL` + `UNITY_PASSWORD`) and guarantees a `-returnlicense` on every exit path. LOCAL Unity needs NO license: the host editor (driven via the [Unity MCP Test Loop](./mcp-test-loop.md)) supplies its own.

## When to Use

- First-time CI runner setup, or wiring the three Unity secrets on a new repo.
- After the Unity serial or credentials rotate.
- When a CI run fails with `Failed to activate` or `No valid Unity Editor license found`.

## License Types

Classic serial activation is the only supported CI activation path. LOCAL Unity
verification needs no license at all -- it runs against the host editor through
the MCP loop, and that editor already holds its own license.

| Type          | Scope     | Activation Method                                              | Notes                                                                       |
| ------------- | --------- | -------------------------------------------------------------- | --------------------------------------------------------------------------- |
| Serial (paid) | CI (only) | `UNITY_SERIAL` + `UNITY_EMAIL` + `UNITY_PASSWORD` -> `-serial` | The single CI path. Activated before the editor and returned on every exit. |

The floating licensing server has been RETIRED. The `UNITY_LICENSING_SERVER`
secret is removed and must not be reintroduced; the `validate-unity-license`
action rejects it.

## Local Unity Needs No License

The devcontainer ships no local Unity build. Local verification runs on the host
editor through the [Unity MCP Test Loop](./mcp-test-loop.md) (the
`unity-mcp-remote` MCP server), and the host editor supplies its own license.
There is no local `.ulf`, no `UNITY_LICENSE` / `UNITY_LICENSE_B64`, and no local
serial to configure. Everything below is CI-only.

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

## Common Failures (CI)

| Signature                                                       | Cause                                              | Remediation                                                                                 |
| --------------------------------------------------------------- | -------------------------------------------------- | ------------------------------------------------------------------------------------------- |
| `UNITY_SERIAL is required`                                      | One of the three serial secrets is unset in CI.    | Set `UNITY_SERIAL`, `UNITY_EMAIL`, and `UNITY_PASSWORD` repository secrets.                 |
| `Retired Unity activation secret UNITY_LICENSING_SERVER is set` | The retired licensing-server secret remains in CI. | Remove `UNITY_LICENSING_SERVER` from the repository/workflows.                              |
| `Failed to activate` / `No valid Unity Editor license found`    | Serial unset or invalid, or wrong credentials.     | Verify the three serial secrets against the Unity dashboard.                                |
| `License client failed to start`                                | Activation hiccup or wrong credentials.            | Retry; then verify the serial and credentials.                                              |
| `All serial seats consumed` / activation blocked                | A prior run leaked a seat, or both seats are held. | The next run's return-at-start reclaims a leaked seat; if persistent, raise the seat count. |

## Renewal

If activation fails after a serial renewal, verify the serial and credentials in
the Unity dashboard and update the `UNITY_SERIAL` / `UNITY_EMAIL` /
`UNITY_PASSWORD` secrets.

## See Also

- [Unity License Return Guarantee](./unity-license-return-guarantee.md) [[unity-license-return-guarantee]]
- [Unity MCP Test Loop](./mcp-test-loop.md)
- [Unity CI Matrix](./unity-ci-matrix.md)
- [CI/CD Devcontainer Workflows](../github-actions/cicd-devcontainer-workflows.md)

## References

- Unity command-line arguments (`-serial`, `-returnlicense`): <https://docs.unity3d.com/Manual/CommandLineArguments.html>
- Unity license activation methods: <https://docs.unity3d.com/Manual/LicenseActivationMethods.html>
- GameCI activation guide: <https://game.ci/docs/github/activation/>
- Source: `scripts/unity/run-ci-tests.ps1`

## Changelog

| Version | Date       | Changes                                                                                                                                                                                     |
| ------- | ---------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 5.0.0   | 2026-06-14 | Rewritten CI-only: the local Unity runners and the ULF/serial local fallback were removed; local Unity now uses the host editor via the MCP loop and needs no license.                      |
| 4.0.0   | 2026-05-22 | Floating licensing server RETIRED; classic serial (`UNITY_SERIAL` + `UNITY_EMAIL` + `UNITY_PASSWORD`) is now the primary, only CI path with a guaranteed return; ULF is the local fallback. |
| 3.0.0   | 2026-05-21 | Floating licensing server (`UNITY_LICENSING_SERVER`) was the primary CI path; legacy secrets removed from CI; ULF/serial local fallback (superseded by the serial cutover).                 |
| 2.0.0   | 2026-05-05 | ULF and serial activation paths; email/password-only caveat.                                                                                                                                |
