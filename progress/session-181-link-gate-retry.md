# Session 181 - External link gate turns master red on third-party outages

Date: 2026-07-31
Branch: `dev/wallstop/link-gate-retry`

## Outcome

`Lint docs links` is a required check that asks third-party websites whether
they are up. It ran once and failed the build on the first bad answer, so a blip
anywhere on the public internet turned a PR or master red on a commit that
changed no links.

The external probe now runs twice, 30 seconds apart, and fails only if both
attempts fail.

## Evidence

Two failures in under an hour on 2026-07-31, from two different hosts and two
different causes, neither related to the commit under test:

| Run | Where | Cause |
| --- | --- | --- |
| [30610933921](https://github.com/Ambiguous-Interactive/DxMessaging/actions/runs/30610933921) | PR #320 | `openupm.com` timed out; a cached result for the same URL was reported as a fatal error despite `--accept-timeouts=true` |
| [30613636402](https://github.com/Ambiguous-Interactive/DxMessaging/actions/runs/30613636402) | master | `shellcheck.net`: `Connection failed. Check network connectivity and firewall settings` |

PR #320 changed a PowerShell test and a progress note. Master's commit added
editor test files. Neither touched a link, a doc page, or `.lychee.toml`.

Both runs' offline pass reported `0 Errors`, so nothing internal was wrong in
either case. Both were cleared by a manual re-run, which is the tell: the check
was measuring the weather, not the repository.

## Why a retry rather than a per-host exclude

`.lychee.toml` bans per-domain excludes explicitly, and rightly: an exclude
would hide the link when it really does die. `accept` takes status codes, so it
cannot express "the connection failed". The step's own summary already commits
to accepting bot detection, throttling, transient responses, and timeouts, so
accepting a transient connection failure is the same policy, not a new one.

A single attempt cannot separate "this link is dead" from "this host was
unreachable for a moment". Two attempts can: a dead link is still dead 30
seconds later. The retry costs nothing on a green run, because the second
attempt only happens after a failure.

This is not a flaky test retried into green. The flakiness being smoothed over
is in third-party web servers, outside this repository, and the alternative is
training reviewers to re-run a red required check without reading it, which is
worse than the flake.

## Scope

`--accept-timeouts=true`, `.lychee.toml`, the pinned lychee version, and the
offline internal-link pass are all unchanged. The scheduled advisory scan still
shares `.lychee.toml` without `accept_timeouts`, so it keeps reporting timeouts.

Issue #324 stays open for the narrower upstream defect it documents: lychee
reclassifies a cached timeout as a generic error, which defeats
`--accept-timeouts=true` for any URL that appears more than once. The retry
makes that survivable; it does not make it correct.

## Verification

- `actionlint`, `yamllint`, `prettier --check`: passed;
- full Node/script suite: 406 passed, 0 failed;
- `npm run validate:all`: passed;
- master restored to green by re-running the failed job before this change.
