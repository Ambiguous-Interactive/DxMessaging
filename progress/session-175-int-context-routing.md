# Session 175 -- context-key investigation and pooled ownership

Date: 2026-07-30
Branch: `dev/wallstop/session-175-int-context-keys`
PR: **#312**
Issue: **#289**

## Outcome

Implemented and fully validated primitive `int` context-routing keys, then
reverted them after two Standalone IL2CPP Release runs failed #289's acceptance
gate. The candidate consistently improved only targeted misses. Source-bound
broadcast did not improve, targeted fan-out was mixed, and unaffected
untargeted rows regressed. The public and internal context-key representation
therefore remains unchanged.

Human PR review found an independent exception-safety defect worth retaining:
a pooled collection could be stranded if attaching a fresh rental to its
lifecycle owner threw. PR #312 now contains only that fix.

## Correctness coverage

- Added `CollectionPool<T>.RentAndAdd`, which returns the exact rental before
  propagating a failed dictionary insertion.
- Converted every dictionary-owned `CollectionPool<T>` rental to the guarded
  ownership transfer. The remaining collection rentals assign directly to an
  owner field before any throwing work.
- Added explicit rollback across the bus context map's high-water tracker and
  `MessageCache` owner.
- Added a throwing-comparer test that proves the original exception is
  preserved, the owner remains empty, and the same rental is returned.
- Cursor Bugbot found no issue on either pushed candidate revision. The only
  Copilot response was the account's review-quota limit. The human review thread
  is resolved.

## Local verification

- Script tests: **404 passed / 0 failed**.
- Static validation: `npm run validate:all`, Prettier, CSharpier, markdownlint,
  ASCII scan, and `git diff --check` passed. Vale was unavailable in the
  devcontainer.
- EditMode contract fixture: **31 passed / 0 failed**.
- PlayMode diagnostics + memory reclamation: **67 passed / 0 failed**.
- EditMode allocation matrix: **61 passed / 0 failed**.
- EditMode pooled-ownership contract fixture: **19 passed / 0 failed**,
  including a throwing-comparer proof that failed owner attachment returns the
  exact rental.
- Candidate complete EditMode passes: **783 passed / 0 failed** twice, then
  **784 passed / 0 failed** after the ownership-transfer test was added.
- Candidate complete PlayMode passes: **979 passed / 0 failed** twice.
- Post-review PlayMode diagnostics + memory reclamation: **67 passed / 0
  failed**.
- Reduced-patch complete EditMode pass after reverting the key experiment:
  **783 passed / 0 failed** in 341.2 seconds.
- Reduced-patch PlayMode diagnostics + memory reclamation: **65 passed / 0
  failed** in 5.3 seconds.
- Superseded primitive-key candidate CI: all static checks and all nine Unity
  legs passed across 2021.3, 2022.3, and 6000.3.

## Performance science

The local Mono A/B/A TargetMap hit comparison used current `master` as both
controls and four representative key counts:

| Keys | Control mean (ops/s) | Experiment (ops/s) | Delta |
| ---: | ---: | ---: | ---: |
| 1 | 17,338,149 | 18,720,193 | +8.0% |
| 16 | 16,900,217 | 18,107,983 | +7.1% |
| 256 | 14,278,897 | 14,887,693 | +4.3% |
| 4096 | 9,769,984 | 9,107,063 | -6.8% |

Three additional 4096-key experiment trials ranged from 8.61M to 9.60M ops/s.
The outlier was recorded rather than averaged away.

The final Standalone IL2CPP run produced:

| Scenario | Delta |
| --- | ---: |
| Untargeted Flood (One Handler) | -6.02% |
| Untargeted Flood (Four Handlers, Four Priorities) | -11.03% |
| Targeted Flood (No Matching Target) | +7.03% |
| Targeted Flood (One Listener) | +5.55% |
| Targeted Flood (Sixteen Listeners) | -1.75% |
| Broadcast Flood (One Handler) | -0.05% |

The earlier independent run also failed directionally: targeted sixteen
listeners was -10.86%, broadcast one handler was -8.90%, and untargeted one
handler was -3.25%. Both allocation legs reported exactly zero allocations and
zero bytes for the affected dispatch rows. The primitive-key candidate failed
the stated gate and was reverted rather than shipping an unproven optimization.
