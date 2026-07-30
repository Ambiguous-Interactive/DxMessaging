# Session 175 -- primitive context routing keys

Date: 2026-07-30
Branch: `dev/wallstop/session-175-int-context-keys`
Issue: **#289**

## Outcome

Converted the internal context-routing layer from `InstanceId` dictionary keys
to primitive `int` keys without changing the public `InstanceId` API. The change
covers the live bus maps, typed-handler maps, dirty sweep candidates, pooled
collections, future slot scaffolding, and benchmark construction seams.

The object-bearing wrapper still flows through interceptors, callbacks,
reflexive dispatch, registration records, and emission diagnostics. Contract
tests now pin the raw-key storage shapes, and integration tests assert that bus
and token histories retain the original Unity object reference.

## Correctness coverage

- Added targeted and broadcast cases for `int.MinValue`, `0`, and
  `int.MaxValue`, including selective trim, replacement registration, and stale
  deregistration.
- Preserved generation, slot-version, and leaf-reference guards around pooled
  map reuse.
- Converted dirty target lists and sets atomically with the live maps, retaining
  their independent pool caps and diagnostics.
- Updated memory-reclamation and performance documentation plus the
  `[Unreleased]` changelog entry.
- Ran two adversarial source-review passes. The first found four test/doc
  precision gaps; all four were fixed. The second found no actionable issue.

## Local verification

- Script tests: **404 passed / 0 failed**.
- Static validation: `npm run validate:all`, Prettier, CSharpier, markdownlint,
  ASCII scan, and `git diff --check` passed. Vale was unavailable in the
  devcontainer.
- EditMode contract fixture: **31 passed / 0 failed**.
- PlayMode diagnostics + memory reclamation: **67 passed / 0 failed**.
- EditMode allocation matrix: **61 passed / 0 failed**.
- PlayMode TargetMap benchmarks: **20 passed / 0 failed**; every hit and miss
  row reported zero managed allocations.
- Complete EditMode passes: **783 passed / 0 failed** twice, in 341.8 and
  383.4 seconds.
- Complete PlayMode passes: **979 passed / 0 failed** twice, in 40.9 and
  28.0 seconds.

## Performance science

The local Mono A/B/A TargetMap hit comparison used current `master` as both
controls and four representative key counts:

| Keys | Control mean (ops/s) | Experiment (ops/s) | Delta |
| ---: | ---: | ---: | ---: |
| 1 | 17,338,149 | 18,720,193 | +8.0% |
| 16 | 16,900,217 | 18,107,983 | +7.1% |
| 256 | 14,278,897 | 14,887,693 | +4.3% |
| 4096 | 9,769,984 | 9,107,063 | -6.8% |

Three additional 4096-key experiment trials ranged from 8.61M to 9.60M
ops/s. The outlier is recorded rather than averaged away. The acceptance gate
from #289 is the published Standalone IL2CPP end-to-end result: targeted and
sourced-broadcast rows must improve, untargeted must not regress, allocations
must remain unchanged, and the full Unity matrix must stay green.
