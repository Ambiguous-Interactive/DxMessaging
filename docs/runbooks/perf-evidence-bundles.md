# Performance evidence bundles

CI artifacts expire after 14 days and the checked-in baseline CSV is replaced on every run, so a
published performance number outlives the evidence behind it. A content-addressed bundle keeps the
raw bytes, the identity of the run that produced them, and the normalized result, so a reviewer can
prove a reported effect came from the retained evidence rather than from a screenshot or a
hand-copied winner.

This runbook covers issue #508. It documents the tool, not the campaign protocol. For the
measurement rules themselves see
[the performance benchmark methodology runbook](./perf-benchmark-methodology.md).

## What a bundle is

A bundle is a directory of evidence plus one `evidence-manifest.json` at its root. The manifest
declares:

| Field           | Meaning                                                                         |
| --------------- | ------------------------------------------------------------------------------- |
| `experimentId`  | Stable lowercase identifier for the experiment, for example a matrix and editor |
| `revision`      | Starts at 1; a correction publishes a new revision, never a replacement         |
| `artifactClass` | Which kind of evidence this is                                                  |
| `reducer`       | The deterministic function that produced `normalized`                           |
| `sourceCommit`  | The lowercase 40- or 64-character commit ID the run was built from              |
| `files`         | Every file, POSIX-relative, with its byte length and lowercase SHA-256          |
| `normalized`    | The machine-readable result a decision cites                                    |
| `bundleDigest`  | SHA-256 over the identity, the file inventory, and `normalized`                 |

Each reducer accepts only its registered artifact class:

| Artifact class                     | Reducer                               |
| ---------------------------------- | ------------------------------------- |
| `shipping-fidelity-matrix`         | `shipping-fidelity-matrix-v1`         |
| `paired-throughput-screen`         | `paired-throughput-screen-v1`         |
| `allocation-subunsub-observations` | `allocation-subunsub-observations-v1` |
| `differential-replay-failure`      | `differential-replay-failure-v1`      |
| `open-loop-editor-capture`         | `open-loop-editor-capture-v1`         |
| `editor-latency-clock-capture`     | `editor-latency-clock-capture-v1`     |

Seal, verify, replay, and manifest writes reject a different class, even when its digest was
recomputed. The paired screen retains the existing exploratory bracket decision. It does not
supply confirmatory intervals, native binaries, or the full paired-throughput campaign contract.
The SubUnsub class retains individual benchmark observations, including unmeasured allocation
probes. Allocation-heavy SubUnsub campaigns, cold latency, frame/queue latency, WPR/PMU native
mapping, and ARM64 energy still need their complete raw-input contracts and reducers under #508.
The Editor open-loop class is a descriptive protocol screen; it does not establish an independent
player session, interval precision, or a latency promotion verdict.

The open-loop class requires exactly eight text files: `capture-environment.json`,
`capture-replay.json`, `open-loop-editor-plan.json`, one maintained-runner result JSON, and that
result's `.run.json`, `.cleanup.json`, `.status`, and `.cleanup.status` sidecars. The `.status`
extension receives the same reviewed-text privacy scan as other admitted extensions. The reducer
binds the manifest source commit to the environment descriptor, hashes the plan, checks terminal
runner ownership and clean-scene state, then recomputes every offered arrival, completion, exact
quantile, and tail effect from the raw result. It rejects a retained replay summary that differs
from the raw-derived result. The Node bundle tool passes only the supplied file bytes and source
commit to the Python exact-integer auditor over process stdin; the auditor reads no data files,
clock, or environment variables. Restorers need Python 3 and Node; the three-OS script matrix
pins Python 3.12 and uses `python` on Windows. Source-tree and
Unity-assembly identity still require separate remote/source checks.

The Editor clock class requires exactly seven text files: `clock-environment.json`,
`clock-replay.json`, one raw maintained-runner result JSON, and that result's four run/cleanup
sidecars. The strict environment descriptor selects either the single-pair or batched clock
control. The reducer binds source commit and run GUID, requires terminal clean-scene capture,
rederives all ticks and descriptive summaries from the raw result, and rejects a retained replay
that differs. These controls measure Editor Mono timer behavior only. Batched elapsed time includes
loop and checksum work; neither control establishes individual player p99 resolution or IL2CPP cost.

Paths are POSIX-relative. The sealer rejects absolute paths, drive letters, backslashes, traversal,
Windows-forbidden or reserved names, trailing spaces or dots, and names that collide after Unicode
normalization and case folding. A bundle sealed on the Windows perf runner therefore verifies
unchanged on a Linux or macOS reviewer machine.

## Commands

```bash
# Seal a directory of evidence. Writes evidence-manifest.json into the directory.
node scripts/unity/perf-evidence-bundle.js seal .artifacts/unity/6000.5.2f1-shipping \
  --experiment-id shipping-fidelity-matrix-6000.5.2f1 \
  --artifact-class shipping-fidelity-matrix \
  --reducer shipping-fidelity-matrix-v1 \
  --source-commit "$(git rev-parse HEAD)"

# Prove every declared file still hashes to its declared value, and that nothing was added.
node scripts/unity/perf-evidence-bundle.js verify \
  .artifacts/unity/6000.5.2f1-shipping/evidence-manifest.json

# Re-derive the normalized result from the sealed bytes and require it to match the manifest.
node scripts/unity/perf-evidence-bundle.js replay \
  .artifacts/unity/6000.5.2f1-shipping/evidence-manifest.json
```

`verify` checks the manifest digest, every file length and hash, the absence of undeclared files,
and the current sensitive-data policy over the raw manifest and every declared file. Inspecting the
manifest bytes before trusting parsed keys prevents duplicate JSON keys from hiding private data. A
bundle sealed under an older policy can therefore fail current verification; scrub it and publish a
new revision. `replay` first verifies, then proves the published conclusion follows from the sealed
bytes. Cite a result only after `replay` succeeds.

## What fails, and what it means

| Failure                                                       | Cause                                                           |
| ------------------------------------------------------------- | --------------------------------------------------------------- |
| `hashes to ... but the manifest declares ...`                 | A raw file changed after sealing                                |
| `is declared by the manifest but could not be read`           | A required artifact is missing or unreachable                   |
| `Undeclared files are present in the bundle`                  | Something was added after sealing                               |
| `does not match its own contents`                             | The manifest itself was edited, including its normalized result |
| `is already sealed as ... but these bytes seal as ...`        | An overwrite of sealed evidence; publish a new revision instead |
| `looks like it contains ...; scrub it before sealing`         | Sensitive data is still present; see below                      |
| `does not use a reviewed text evidence extension ...`         | The artifact class has no approved inspection rule              |
| `is not valid UTF-8 or byte-order-marked UTF-16 text ...`     | The file has malformed or opaque bytes                          |
| `contains non-text control bytes` or `too many NUL bytes`     | The file does not meet the reviewed-text contract               |
| `reports fields for cell ... that disagree with its raw cell` | The matrix summary disagrees with the per-cell evidence         |

## Sensitive-data refusal

Sealing accepts only reviewed text evidence extensions declared in
`scripts/unity/credential-patterns.js`. Each file must decode completely as UTF-8 or
byte-order-marked UTF-16 and may contain only normal text controls. A scanned Unity log may carry
up to eight sparse stray NULs from native subprocess output. The sealer still scans the whole decoded file
for credentials and private identifiers. It rejects unreviewed extensions, malformed text, opaque
data, a binary tail, and excess control bytes. Add a format-specific inspection rule before a new
file class enters durable evidence.

The `./.github/actions/redact-unity-artifacts` step runs before every Unity artifact upload, not
only before sealing. The same text diagnostic logs are available during their workflow artifact
retention period, so seal-only redaction would still expose the identifiers. The redactor rewrites only
reviewed text extensions. It reads byte-order-marked UTF-16, prefers strict UTF-8, and uses a
byte-preserving Latin-1 fallback for malformed UTF-8. It also scans a lossy UTF-8 view before
allowing that fallback. It removes stray NUL separators before matching. For an unreviewed
extension, it scans strict text and fails the upload if sensitive data is present, but never rewrites
the file. It also scans decoded JSON and XML escape forms. It blocks encoded sensitive values that
cannot be mapped back safely. Opaque files remain byte-identical and are reported as unscanned; ordinary workflow
artifacts are not certified as privacy-safe evidence. The sealer is the fail-closed boundary that
prevents an unreviewed format from entering a bundle intended for durable publication.

If sealing refuses a file, scrub the file. Do not weaken the pattern.

The private-identifier policy removes values that identify the runner account, host, network, or
licensed machine:

| Removed                                       | Retained                                           |
| --------------------------------------------- | -------------------------------------------------- |
| Recognized home roots and CI shell aliases    | Relative paths and other absolute paths            |
| UNC and private HTTP(S) hosts                 | Dotted public URLs and unlabelled `//` authorities |
| Canonical IP, MAC/EUI, and Windows volume IDs | CPU model, memory size, and normal Unity versions  |
| Unity machine IDs and explicit host fields    | Timing, process IDs, ports, and player-session IDs |
| Accelerator and Cache Server endpoints        | Non-network benchmark inputs and outputs           |

An unlabelled public address can identify a runner network, so canonical IP literals are removed.
This intentionally also removes a four-component numeric value such as `1.2.3.4`; without context,
it is indistinguishable from IPv4. HTTP(S) hosts are classified after WHATWG normalization. This
catches encoded or Unicode private hosts, legacy IPv4 URL spellings, backslash separators, and user
information. Repeated trailing dots are removed before classification. Literal or serialized format
and non-spacing control characters in HTTP(S), file, and UNC authorities fail closed. Sealed reviewed
text rejects any remaining format control. A bare legacy IPv4 spelling remains ordinary numeric text
because it is ambiguous outside a URL. Domain-based public service URLs and `localhost` remain
intact; single-label HTTP(S) hosts and names under `.local` or `.internal` are treated as private.
Forward-slash authorities are treated as UNC only when a `path`, `share`, or `UNC` label supplies
that context; an unlabelled `//host/path` is retained because it is indistinguishable from a
scheme-relative URL. Backslash UNC paths, extended UNC and Windows volume paths, backslash WSL
homes, macOS and Fedora home aliases, and MSYS/Cygwin Windows-home aliases are recognized directly.
A remote share name alone is not inferred to be an account name because ordinary project shares
use the same syntax; explicit `home`, `Users`, and `Documents and Settings` segments remain
account-home evidence.

An unlabelled backslash pair followed by a valid host and share shape is a UNC path. That syntax is
also legal in TeX-like prose, so the privacy-first policy can redact such text. Use ordinary `/`
paths in durable evidence when the source is not a Windows path.

Redaction preserves the label and structurally bounded delimiters while replacing the private value
with a square-bracketed named placeholder. The placeholder is safe inside JSON strings and XML
attributes. An unquoted, line-oriented value may consume an ambiguous remainder of its line; do
not use redacted diagnostic text as a metric source. Every placeholder is outside its source
pattern, so a second pass is byte-identical.

The privacy policy added for #522 was checked against a real 441-file shipping-fidelity bundle from
Unity Tests run 33739376165. The scrubber removed 400 IP address occurrences, 400 Unity host-name
occurrences, 200 single-label web-host occurrences, 40 Unity IPC host suffixes, and 40 Unity machine
IDs across 40 files. An independent inventory found ten distinct raw address, host, and machine
values and found none in the corrected tree. A second pass changed no files. The corrected bundle
sealed all 441 files, verified, and replayed the same 20-cell normalized result as the original.

The shipping reducer checks the matrix and cell schema, Unity version, numeric types, unique cell
identities, declared counts, and every rendered summary column, including timings. Each completed
row requires its raw cell file. Failed and unreadable outcomes must be explicit, unique, and
separate from completed cells. A partial run can retain those outcomes without presenting missing
cells as completed evidence.

## Retaining a paired screen

Copy the original bracket declaration to `bracket-manifest.json` without rewriting its bytes.
The bundle's `sourceCommit` must match the first run; `normalized.provenance` retains all three
measured commits and source trees. Record the analysis source revision separately.
Copy each run's `paired-comparison-summary.json` to `first.json`, `center.json`, and `last.json`
in declared run order. Each summary must retain its raw cycle ratios, source identities, protocol,
execution profile, and matching declaration digest. Keep other reviewed text evidence alongside
these inputs; the bundle inventory hashes every retained file.

```bash
node scripts/unity/perf-evidence-bundle.js seal .artifacts/paired-screen \
  --experiment-id paired-screen-example \
  --artifact-class paired-throughput-screen \
  --reducer paired-throughput-screen-v1 \
  --source-commit "$FIRST_RUN_COMMIT"
node scripts/unity/perf-evidence-bundle.js replay \
  .artifacts/paired-screen/evidence-manifest.json
```

The adapter calls `reduce-paired-bracket.js` directly. It validates all three positions, raw cycle
consistency, source relationships, declaration identity, and the shared execution profile before
reproducing effects and the `accepted`, `rejected`, or `uninterpretable` screen decision. Replaying
a rejected or uninterpretable screen succeeds when its original decision is reproduced. Missing
inputs, changed bytes, or a different decision fail. These files do not prove native payload
retention, independent build replication, or immutable remote restoration.

## Retaining SubUnsub allocation observations

Retain the original benchmark output in `comparison-output.log`. For an MCP result, copy the
selected test leaf's `output` string without editing its metric lines. Keep the source result and
its SHA-256 in the local experiment record; inspect any full result before publication because
suite names can contain private paths. Extract the CSV with the production extractor:

```bash
node scripts/unity/extract-perf-baseline.js \
  --input .artifacts/subunsub/comparison-output.log \
  --output .artifacts/subunsub/comparison-baseline.csv
node scripts/unity/perf-evidence-bundle.js seal .artifacts/subunsub \
  --experiment-id subunsub-observations-example \
  --artifact-class allocation-subunsub-observations \
  --reducer allocation-subunsub-observations-v1 \
  --source-commit "$MEASURED_SOURCE_COMMIT"
node scripts/unity/perf-evidence-bundle.js replay .artifacts/subunsub/evidence-manifest.json
```

The reducer requires the current canonical eight-column CSV and an exact match with rows
extracted from the retained output. Unknown or malformed CSV rows, duplicate run identities,
legacy missing byte columns, unsafe integer measurements, and invalid negative values fail.
Every retained row must share the declared source commit and exact platform string, with one
recognized execution scope. At least one `Comparison_DxMessaging_SubUnsub` row is required.
The normalized result copies all SubUnsub rows in source order, including their run identities,
throughput, window duration, allocation call counts, and informational allocated bytes.

`gcAllocations` and `gcAllocatedBytes` retain their producer's `-1` unmeasured sentinel
independently. A measured zero remains zero; neither field is inferred from the other.
These are aggregate benchmark observations. The CSV does not carry probe sample distributions,
operation denominators, independent build identities, workload schedules, or confirmatory
intervals. Do not infer per-operation allocation costs or campaign acceptance from this class.
Local Mono observations retain their scope and cannot establish a Standalone IL2CPP headline.

## Retaining a differential replay failure

Set `DXM_DIFFERENTIAL_REPLAY_EVIDENCE_DIRECTORY` to the bundle's `replays` directory before
running
`DifferentialHostTraceTests.GeneratedLifecycleReplayMatchesAndShrinksDestroyedHostMutation`.
The variable is opt-in: ordinary CI runs do no evidence I/O and execute the same test cases. Use an
empty directory because the writer refuses to replace a prior capture. The fixture writes one raw
JSON record for each of `Untargeted`, `Targeted`, and `Broadcast`, retaining the seed,
generator and observation schema versions, complete original and minimized operations, both
control/candidate traces, and the first mismatch classification.

Add these reviewed text inputs at the bundle root:

- `differential-replay-environment.json`: schema version, source commit and tree, Unity version,
  test mode, scripting backend, assembly, and full test name.
- `differential-replay-profile.json`: the exact `profile` object from
  `scripts/unity/differential-replay-contract.json`.
- `candidate-adapter.txt`: the source path, commit, line range, and exact excerpt that implements
  the injected candidate fault.
- `replay-command.txt`: the exact command or MCP invocation needed to rerun the focused fixture.

```bash
node scripts/unity/perf-evidence-bundle.js seal .artifacts/differential-replay-failure \\
  --experiment-id native-lifecycle-replay-failure \\
  --artifact-class differential-replay-failure \\
  --reducer differential-replay-failure-v1 \\
  --source-commit "$(git rev-parse HEAD)"
node scripts/unity/perf-evidence-bundle.js replay \\
  .artifacts/differential-replay-failure/evidence-manifest.json
```

The v1 reducer is deliberately specific to the reviewed seed-509, six-operation native lifecycle
profile. It independently locates the first observable mismatch with the same ordered comparison
categories as the C# oracle. It requires the original operation-kind sequence, a one-operation
`DestroyHost` deletion-minimal subsequence, matching `state` failures for all three message
kinds, exact profile/source agreement, and non-empty candidate and replay inputs. A future
generator profile needs a new reducer version; do not weaken this contract to admit it.

## Adding a reducer

A reducer must be a pure function of the bundle's bytes. Read only from the supplied content map,
never from disk, the clock, or the environment. Copy measured values verbatim. Order arrays by an
ordinal key or a declared experimental position rather than by directory-walk order. The shipping
reducer derives only integer comparisons. The paired screen reuses the existing floating-point
analysis without rounding or tolerances in replay: a different normalized result fails replay.
Retain the analysis source revision and Node.js version with a paired experiment to diagnose any
engine-dependent arithmetic difference. Such a mismatch is failed restoration, not permission to
replace the sealed result.

Register it in `REDUCERS` in `scripts/unity/perf-evidence-bundle.js` and add cases to
`scripts/__tests__/perf-evidence-bundle.test.js` covering a missing input, a corrupted input, and a
summary that disagrees with the raw rows it claims to describe. Compare every copied
field, including profile identity and nested fields, while allowing object-key order
and the producer's `cellId` override.

## Where bundles are produced

The `unity-tests` job seals the shipping-fidelity matrix after redaction and before upload, then
replays it, so a bundle that cannot reproduce its own result fails the run. The manifest travels
inside the 14-day workflow artifact.

The [evidence index](#evidence-index) records immutable publications and their restore checks.
A workflow artifact alone is not durable campaign evidence. New bundles must pass the publication
and restore gates below before entering the index.

## Durable publication contract

Repository maintainers own the evidence releases and the tracked evidence index below, linked from
issue #500 and reviewed with its code change. `PLAN.md` is a transient routing page, not the durable index. Retain each published
revision without an expiry date, including superseded revisions. A correction
adds a revision and an index entry; it does not replace an asset, move a tag, or erase the previous
entry. If privacy or access loss requires withdrawal, mark the entry unavailable and every dependent
conclusion incomplete. Never substitute a local copy for a failed durable retrieval.

Use `perf-evidence-<experiment-id>-r<revision>-<manifest-sha8>` for the tag. Here `manifest-sha8`
means the first eight characters of SHA-256 over the exact manifest file bytes, not `bundleDigest`.
The explicit revision prevents ambiguity between corrections; the short hash is a label, not an
integrity check. Record the full manifest SHA-256, full archive SHA-256, `bundleDigest`, revision,
source commit, verifier commit, release URL, and exact asset name in the reviewed evidence index.
The source commit identifies the measured build; the verifier commit identifies the checked-out
scripts used to inspect and replay it. They need not match.

Before enabling publication:

1. Confirm the package release workflow uploads and verifies assets while the release is a draft.
   Its published-rerun path must verify downloaded bytes without replacing assets. Keep these
   checks when updating the workflow; repository immutability also affects package releases.
1. Have a repository administrator enable immutable releases. Check the authenticated repository
   setting and require `enabled: true`; a successful HTTP response alone is not proof. Missing
   permissions, unavailable settings, and a false value all block publication.
1. Seal, verify, and replay the scrubbed bundle. Package only its manifest and declared files in one
   archive, with relative paths, no links, and normalized archive ownership. Keep large evidence out
   of Git history; `progress/` is local and ignored. If it cannot fit the chosen release asset, leave the result incomplete
   until a reviewed storage decision supplies an equally verifiable durable location.
1. Reject an existing experiment/revision with different manifest bytes, even if its tag has a
   different hash suffix. An identical existing publication is reusable only after downloading and
   verifying its assets. Never use an asset-replacement option. Local `seal` checks only the manifest
   in its current directory; it does not enforce uniqueness across releases or preserve old folders.
1. Create a new draft prerelease at the verifier commit, upload every asset, and download and verify
   the draft's complete asset inventory before publishing. A failed or interrupted draft is not
   durable evidence. Do not automatically delete, overwrite, or publish it on a retry.
1. Publish the completed draft, then require the release response to report `draft: false`,
   `prerelease: true`, and `immutable: true`. Check the tag commit and every asset name and digest.
   Perform the independent restore below before recording the result as durable.

GitHub locks the tag and assets only after publication. Release titles, notes, and the prerelease
flag remain editable, so they cannot replace the reviewed digest index. See GitHub's
[immutable release guarantees](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)
and [repository immutability settings API](https://docs.github.com/en/rest/repos/repos#check-if-immutable-releases-are-enabled-for-a-repository).

## Independent restore acceptance

Use a reviewer environment with no runner artifact cache. Clone the repository into a new directory,
check out the full verifier commit from the reviewed index, and require an empty
`git status --porcelain`. Run `npm ci --ignore-scripts` to install the pinned verifier dependencies.
The credential scanner uses XML and YAML parsers from the lockfile. Do not copy scripts or
dependencies from the producing checkout. The bundle commands do not require Unity.

1. Retrieve the indexed release and exact asset from GitHub, using the supported authentication
   path when needed. Require the immutable release metadata above. A 401, 403, 404, timeout,
   missing asset, or failed download is an incomplete experiment, never an empty passing result.
   Do not fall back to a workflow artifact, another revision, or a cached local archive.
1. Download into a new temporary directory. Require the full archive SHA-256 to equal the reviewed
   index before extraction. Inspect the archive inventory for absolute paths, traversal, links, or
   special files, then extract into a separate empty directory without restoring ownership.
1. Require the extracted manifest's full SHA-256 to equal the index. Check its experiment ID,
   revision, source commit, and `bundleDigest` against that same index, not against release notes.
1. Run the existing commands from the clean verifier checkout, using the restored manifest path:

   ```bash
   node scripts/unity/perf-evidence-bundle.js verify /tmp/restored-evidence/evidence-manifest.json
   node scripts/unity/perf-evidence-bundle.js replay /tmp/restored-evidence/evidence-manifest.json
   ```

1. Require both commands to exit zero. `replay` checks exact normalized-result equality, not just
   file availability. Record the release and asset identities, digests, verifier commit, command
   exit codes, and clean checkout status in the evidence index.
1. Exercise denied access to an actual required remote asset in an isolated reviewer context,
   without deleting or changing the publication. Require retrieval to fail and the experiment to
   remain incomplete. A fabricated URL, missing local file, or HTTP mock does not prove this gate.

A local archive round trip can test packaging, byte integrity, and reducer replay. It cannot prove
GitHub retention, immutable publication, independent remote retrieval, or denied remote access.

## Evidence index

Repository-wide immutable releases were enabled with maintainer approval on 2026-09-10. The
authenticated setting read back `enabled: true`. Publication remains an operator procedure, not
an automatic upload of every workflow artifact. Retain all indexed revisions without expiry.

### native-lifecycle-replay-failure, revision 1

Status: published and restored from GitHub in a fresh verifier clone on 2026-09-13. This is a
deterministic Unity 6000.4.6f1 PlayMode Mono differential-oracle observation. It retains the seed,
original and minimized operations, and control and faulty-adapter traces for a skipped host
destruction across all three message kinds. It is behavioral oracle evidence, not a performance
claim.

| Identity                        | Value                                                                                                                                                                                                         |
| ------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Release                         | [Immutable native lifecycle replay prerelease](https://github.com/Ambiguous-Interactive/DxMessaging/releases/tag/perf-evidence-native-lifecycle-replay-failure-r1-86b25374)                                   |
| Release ID                      | `388023458`                                                                                                                                                                                                   |
| Asset                           | [Native lifecycle replay archive](https://github.com/Ambiguous-Interactive/DxMessaging/releases/download/perf-evidence-native-lifecycle-replay-failure-r1-86b25374/native-lifecycle-replay-failure-r1.tar.gz) |
| Exact asset name                | `native-lifecycle-replay-failure-r1.tar.gz`                                                                                                                                                                   |
| Asset ID and size               | `561869392`, 2,850 bytes                                                                                                                                                                                      |
| Archive SHA-256                 | `36a23d0772a5cdad44e611ddf1af00f0576ae469ff1af67bcec08667f8dccbc1`                                                                                                                                            |
| Manifest SHA-256                | `86b25374954df963f18c7c615a24a35224753e3be8425fdcb310d12a3851f414`                                                                                                                                            |
| Bundle digest                   | `f48c7e80cd3a9f33d056f9eb8e48b7f7de177ea6729ce577f7c451e880652025`                                                                                                                                            |
| Measured source                 | `39ac059a89bb3aaccdf105a0bb3a15ab764e1032`                                                                                                                                                                    |
| Verifier and release tag commit | `bef2f36bdf79a92959c09390a5084c724cf4cf43`                                                                                                                                                                    |
| Reducer                         | `differential-replay-failure-v1`                                                                                                                                                                              |

The draft contained exactly one asset. Its downloaded bytes matched the archive hash before
publication. Published metadata reported `draft: false`, `prerelease: true`, and
`immutable: true`; the tag resolves to the verifier commit.

The restore used a new remote clone at the verifier commit and a fresh pinned dependency install.
The archive was downloaded anonymously. Its eight entries were relative regular files with
normalized ownership and no links. Archive and manifest hashes matched this index. `verify`
accepted all seven declared files, and `replay` reproduced the three normalized message-kind
cases. The verifier worktree stayed clean. A denied-access request for actual asset `561869392`
with invalid credentials returned HTTP 401, so failed required retrieval remains incomplete.

### shipping-fidelity-matrix-6000.5.2f1, revision 1

Status: published and restored from GitHub in a fresh verifier clone on 2026-09-11. This is a
20-cell Unity 6000.5.2f1 shipping-fidelity characterization across Minimal, Low, Medium, and High
stripping. It retains text evidence for five message-shape and cardinality topologies per level.
It does not retain player binaries or establish confirmatory performance.

| Identity                        | Value                                                                                                                                                                                                                  |
| ------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Release                         | [Immutable shipping-fidelity prerelease](https://github.com/Ambiguous-Interactive/DxMessaging/releases/tag/perf-evidence-shipping-fidelity-matrix-6000.5.2f1-r1-136ce04a)                                              |
| Release ID                      | `386771634`                                                                                                                                                                                                            |
| Asset                           | [Shipping-fidelity matrix archive](https://github.com/Ambiguous-Interactive/DxMessaging/releases/download/perf-evidence-shipping-fidelity-matrix-6000.5.2f1-r1-136ce04a/shipping-fidelity-matrix-6000.5.2f1-r1.tar.gz) |
| Exact asset name                | `shipping-fidelity-matrix-6000.5.2f1-r1.tar.gz`                                                                                                                                                                        |
| Asset ID and size               | `556403738`, 10,014 bytes                                                                                                                                                                                              |
| Archive SHA-256                 | `2fe634af708db99d291824a88717306f457599e2992c3ef7fb682252d96eb01e`                                                                                                                                                     |
| Manifest SHA-256                | `136ce04a8cb2f2f60db29d4526b05ed7a59d0396477ae30ff92de651b01a142d`                                                                                                                                                     |
| Bundle digest                   | `b3cbf02cc1a982c790a354df3d2b355dafa4b41f996742d7f9e06f9858820960`                                                                                                                                                     |
| Measured source                 | `5bd7ea33dd7182761117a3b3329d841b4d7f9523`                                                                                                                                                                             |
| Verifier and release tag commit | `1590dd334982cd832e65401138b5bd11904d520b`                                                                                                                                                                             |
| Reducer                         | `shipping-fidelity-matrix-v1`                                                                                                                                                                                          |

The draft contained exactly one asset. Its downloaded bytes matched the archive hash before
publication. Published metadata reported `draft: false`, `prerelease: true`, and `immutable: true`;
the tag resolves to the verifier commit.

The restore used a new remote clone at the verifier commit and a fresh pinned dependency install.
The archive was downloaded anonymously. Its 43 entries contained only relative regular files and
directories with normalized ownership and no links. Archive and manifest hashes matched this
index. `verify` accepted all 21 declared files, and `replay` reproduced all 20 normalized cells.
The verifier worktree stayed clean.

### shipping-fidelity-matrix-2021.3.45f1, revision 1

Status: published and restored from GitHub in a fresh verifier clone on 2026-09-14. This is the
20-cell Unity 2021.3.45f1 endpoint characterization at the PR #591 merge revision. It covers
Minimal, Low, Medium, and High stripping with five message-shape and cardinality topologies per
level. It retains the reviewed text build and result evidence, not player binaries or confirmatory
performance.

| Identity                        | Value                                                                                                                                                                                                                                 |
| ------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Release                         | [Immutable Unity 2021.3 shipping-fidelity prerelease](https://github.com/Ambiguous-Interactive/DxMessaging/releases/tag/perf-evidence-shipping-fidelity-matrix-2021.3.45f1-r1-cae3ea64)                                               |
| Release ID                      | `388173149`                                                                                                                                                                                                                           |
| Asset                           | [Unity 2021.3 shipping-fidelity matrix archive](https://github.com/Ambiguous-Interactive/DxMessaging/releases/download/perf-evidence-shipping-fidelity-matrix-2021.3.45f1-r1-cae3ea64/shipping-fidelity-matrix-2021.3.45f1-r1.tar.gz) |
| Exact asset name                | `shipping-fidelity-matrix-2021.3.45f1-r1.tar.gz`                                                                                                                                                                                      |
| Asset ID and size               | `562702894`, 4,546,853 bytes                                                                                                                                                                                                          |
| Archive SHA-256                 | `1da12030d6d4475a83242883f17044084a521224a30429cfd16e4b7a3f308478`                                                                                                                                                                    |
| Manifest SHA-256                | `cae3ea6433ab4d4d408dfa41372ee33e0f29e051dd1bb7e91752ef9200b90843`                                                                                                                                                                    |
| Bundle digest                   | `9f6c8923bcc161b38e82aad035180dc2f5aab25afddf780954c6a61dfdedb56c`                                                                                                                                                                    |
| Measured source                 | `eadc9b8a427a2a44922c4fa49653bb0ae3abb497`                                                                                                                                                                                            |
| Verifier and release tag commit | `eadc9b8a427a2a44922c4fa49653bb0ae3abb497`                                                                                                                                                                                            |
| Reducer                         | `shipping-fidelity-matrix-v1`                                                                                                                                                                                                         |

The draft contained exactly one asset. Its downloaded bytes matched the archive hash before
publication. Published metadata reported `draft: false`, `prerelease: true`, and `immutable: true`;
the tag resolves to the verifier commit.

The restore used a new remote clone at the verifier commit and a fresh pinned dependency install.
The archive was downloaded anonymously. Its 542 files were relative regular entries with
normalized ownership and no links or special files. Archive and manifest hashes matched this
index. `verify` accepted all 541 declared files, and `replay` reproduced all 20 normalized cells.
The verifier worktree stayed clean. A denied-access request for actual asset `562702894` with
invalid credentials returned HTTP 401, produced no file, and did not use a cached fallback.

### shipping-fidelity-matrix-2021.3.45f1, revision 2

Status: published and restored from GitHub in a fresh verifier clone on 2026-09-14. This revision
retains the exact PR #593 merge endpoint after the clean-predecessor snapshot correction. All 20
clean shipping cells and the separately bound High-semantic incremental build completed. Revision
1 remains immutable and available above.

| Identity                        | Value                                                                                                                                                                                                                                                |
| ------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Release                         | [Immutable Unity 2021.3 clean and incremental shipping-fidelity prerelease](https://github.com/Ambiguous-Interactive/DxMessaging/releases/tag/perf-evidence-shipping-fidelity-matrix-2021.3.45f1-r2-474a82d0)                                        |
| Release ID                      | `388755807`                                                                                                                                                                                                                                          |
| Asset                           | [Unity 2021.3 clean and incremental shipping-fidelity archive](https://github.com/Ambiguous-Interactive/DxMessaging/releases/download/perf-evidence-shipping-fidelity-matrix-2021.3.45f1-r2-474a82d0/shipping-fidelity-matrix-2021.3.45f1-r2.tar.gz) |
| Exact asset name                | `shipping-fidelity-matrix-2021.3.45f1-r2.tar.gz`                                                                                                                                                                                                     |
| Asset ID and size               | `564376681`, 3,652,817 bytes                                                                                                                                                                                                                         |
| Archive SHA-256                 | `f5eaff22861354e60139664e1db18c55a0bd67fa72950cd1cfa3d24ff1f46a5a`                                                                                                                                                                                   |
| Manifest SHA-256                | `474a82d0bb0229feabd037b31745a8de3be0e08fddc8c43a11c17d273a1d934b`                                                                                                                                                                                   |
| Bundle digest                   | `cb8a7f1606388ac835ec60f8f58b77a6ad047b6240c2a4e9e3e8776d16418078`                                                                                                                                                                                   |
| Measured source                 | `b6ce2f59f9c3e2115708226d51d7586c3e8846d1`                                                                                                                                                                                                           |
| Verifier and release tag commit | `b6ce2f59f9c3e2115708226d51d7586c3e8846d1`                                                                                                                                                                                                           |
| Reducer                         | `shipping-fidelity-matrix-v1`                                                                                                                                                                                                                        |

The draft contained exactly one asset. Its downloaded bytes matched the archive hash before
publication. Published metadata reported `draft: false`, `prerelease: true`, and `immutable: true`;
the tag resolves to the verifier commit.

The restore used a new remote clone at the verifier commit and a fresh pinned dependency install.
The archive was downloaded anonymously. Its 555 files were relative regular entries with
normalized ownership and no links or special files. Archive and manifest hashes matched this
index. `verify` accepted all 554 declared files, and `replay` reproduced all 20 normalized cells.
The verifier worktree stayed clean. A denied-access request for actual asset `564376681` with
invalid credentials returned HTTP 401, produced no file, and did not use a cached fallback.

### shipping-fidelity-matrix-6000.5.2f1, revision 2

Status: published and restored from GitHub in a fresh verifier clone on 2026-09-14. This revision
retains the exact PR #591 merge endpoint after the repeated-scalar redaction fix. All 20 shipping
cells completed, and artifact redaction finished inside its unchanged two-minute gate. Revision 1
remains immutable and available above.

| Identity                        | Value                                                                                                                                                                                                                               |
| ------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Release                         | [Immutable Unity 6000.5 shipping-fidelity prerelease](https://github.com/Ambiguous-Interactive/DxMessaging/releases/tag/perf-evidence-shipping-fidelity-matrix-6000.5.2f1-r2-cb123041)                                              |
| Release ID                      | `388173324`                                                                                                                                                                                                                         |
| Asset                           | [Unity 6000.5 shipping-fidelity matrix archive](https://github.com/Ambiguous-Interactive/DxMessaging/releases/download/perf-evidence-shipping-fidelity-matrix-6000.5.2f1-r2-cb123041/shipping-fidelity-matrix-6000.5.2f1-r2.tar.gz) |
| Exact asset name                | `shipping-fidelity-matrix-6000.5.2f1-r2.tar.gz`                                                                                                                                                                                     |
| Asset ID and size               | `562703727`, 4,090,916 bytes                                                                                                                                                                                                        |
| Archive SHA-256                 | `e7dce95484844bd025dfe2c51dd187832ab804f945d5dc32a93dd47a4ced9aa9`                                                                                                                                                                  |
| Manifest SHA-256                | `cb1230414f98a0db88c07139bd83c9b8ad5e29060188580c1eb822c665940ea2`                                                                                                                                                                  |
| Bundle digest                   | `f950f2438cce3eee62986c729bc65acfeb8370e73a3cf0ab40d4457d6c188240`                                                                                                                                                                  |
| Measured source                 | `eadc9b8a427a2a44922c4fa49653bb0ae3abb497`                                                                                                                                                                                          |
| Verifier and release tag commit | `eadc9b8a427a2a44922c4fa49653bb0ae3abb497`                                                                                                                                                                                          |
| Reducer                         | `shipping-fidelity-matrix-v1`                                                                                                                                                                                                       |

The draft contained exactly one asset. Its downloaded bytes matched the archive hash before
publication. Published metadata reported `draft: false`, `prerelease: true`, and `immutable: true`;
the tag resolves to the verifier commit.

The same new verifier clone downloaded this archive anonymously. Its 582 files were relative
regular entries with normalized ownership and no links or special files. Archive and manifest
hashes matched this index. `verify` accepted all 581 declared files, and `replay` reproduced all 20
normalized cells. The verifier worktree stayed clean. A denied-access request for actual asset
`562703727` with invalid credentials returned HTTP 401, produced no file, and did not use a cached
fallback.

### shipping-fidelity-matrix-6000.5.2f1, revision 3

Status: published and restored from GitHub in a fresh verifier clone on 2026-09-14. This revision
retains the exact PR #593 merge endpoint after the clean-predecessor snapshot correction. All 20
clean shipping cells and the separately bound High-semantic incremental build completed. Earlier
revisions remain immutable and available above.

| Identity                        | Value                                                                                                                                                                                                                                              |
| ------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Release                         | [Immutable Unity 6000.5 clean and incremental shipping-fidelity prerelease](https://github.com/Ambiguous-Interactive/DxMessaging/releases/tag/perf-evidence-shipping-fidelity-matrix-6000.5.2f1-r3-eb7d15ae)                                       |
| Release ID                      | `388774759`                                                                                                                                                                                                                                        |
| Asset                           | [Unity 6000.5 clean and incremental shipping-fidelity archive](https://github.com/Ambiguous-Interactive/DxMessaging/releases/download/perf-evidence-shipping-fidelity-matrix-6000.5.2f1-r3-eb7d15ae/shipping-fidelity-matrix-6000.5.2f1-r3.tar.gz) |
| Exact asset name                | `shipping-fidelity-matrix-6000.5.2f1-r3.tar.gz`                                                                                                                                                                                                    |
| Asset ID and size               | `564455344`, 4,405,971 bytes                                                                                                                                                                                                                       |
| Archive SHA-256                 | `c914c8b63382be958f31c67a21dd0b1792110fb98dcf813698646318fed35cb1`                                                                                                                                                                                 |
| Manifest SHA-256                | `eb7d15ae1bfe19831ce8a895b85fc8d87c320d56f2015372637214b9cab11646`                                                                                                                                                                                 |
| Bundle digest                   | `b02f9a5ff52cd3999f04f401f9c783660ecf5a01772194012f7c83b28ed8a128`                                                                                                                                                                                 |
| Measured source                 | `b6ce2f59f9c3e2115708226d51d7586c3e8846d1`                                                                                                                                                                                                         |
| Verifier and release tag commit | `b6ce2f59f9c3e2115708226d51d7586c3e8846d1`                                                                                                                                                                                                         |
| Reducer                         | `shipping-fidelity-matrix-v1`                                                                                                                                                                                                                      |

The draft contained exactly one asset. Its downloaded bytes matched the archive hash before
publication. Published metadata reported `draft: false`, `prerelease: true`, and `immutable: true`;
the tag resolves to the verifier commit.

The restore used a new remote clone at the verifier commit and a fresh pinned dependency install.
The archive was downloaded anonymously. Its 596 files were relative regular entries with
normalized ownership and no links or special files. Archive and manifest hashes matched this
index. `verify` accepted all 595 declared files, and `replay` reproduced all 20 normalized cells.
The verifier worktree stayed clean. A denied-access request for actual asset `564455344` with
invalid credentials returned HTTP 401, produced no file, and did not use a cached fallback.

The endpoint jobs also retained native player payloads as expiring workflow artifacts. A
values-suppressed scan found credential or network-identifier pattern classes in every binary.
Those native bytes remain excluded from immutable publication unless the format-aware
investigation in issue #508 can prove every finding safe. The text bundles do not establish native
payload retention.

### session247-paired-screen-replay, revision 1

Status: published and restored from GitHub in a fresh verifier clone on 2026-09-11. This historical
Windows x64 Standalone IL2CPP Release screen retains three raw paired summaries and reproduces an
`uninterpretable` decision because two sentinels exceed the 3% band. It is not a parity or
performance-improvement claim.

| Identity                        | Value                                                                                                                                                                                                           |
| ------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Release                         | [Immutable paired-screen prerelease](https://github.com/Ambiguous-Interactive/DxMessaging/releases/tag/perf-evidence-session247-paired-screen-replay-r1-75a42003)                                               |
| Release ID                      | `386771306`                                                                                                                                                                                                     |
| Asset                           | [Session 247 paired-screen archive](https://github.com/Ambiguous-Interactive/DxMessaging/releases/download/perf-evidence-session247-paired-screen-replay-r1-75a42003/session247-paired-screen-replay-r1.tar.gz) |
| Exact asset name                | `session247-paired-screen-replay-r1.tar.gz`                                                                                                                                                                     |
| Asset ID and size               | `556402567`, 6,287 bytes                                                                                                                                                                                        |
| Archive SHA-256                 | `1cf1acfef029c3d29a178e71f719fdb76b754283e5911ca034b8bb47592dd478`                                                                                                                                              |
| Manifest SHA-256                | `75a42003e501936051ee7e80e4be25d1d1b9449a8a21bdade522c3451ae747d2`                                                                                                                                              |
| Bundle digest                   | `c835487bcde331429aa7f6f429124b3d6f4dc8d5296db835f218d33fc47d76ef`                                                                                                                                              |
| Measured source                 | `acf0fa4fb5d5a9ba1122a39a0d4a93cc0f38e47b`                                                                                                                                                                      |
| Verifier and release tag commit | `1590dd334982cd832e65401138b5bd11904d520b`                                                                                                                                                                      |
| Reducer                         | `paired-throughput-screen-v1`                                                                                                                                                                                   |

The draft contained exactly one asset. Its downloaded bytes matched the archive hash before
publication. Published metadata reported `draft: false`, `prerelease: true`, and `immutable: true`;
the tag resolves to the verifier commit.

The restore used the same new remote verifier clone as the shipping-fidelity entry and downloaded
this archive anonymously. Its six entries contained only relative regular files and one directory,
with normalized ownership and no links. Archive and manifest hashes matched this index. `verify`
accepted all four declared files, and `replay` reproduced the sealed `uninterpretable` result. The
verifier worktree stayed clean.

### session271-subunsub-measured, revision 1

Status: published and restored from GitHub in a fresh verifier clone on 2026-09-10. This is a
historical Editor PlayMode Mono x64 Debug observation on Unity 6000.4.6f1. It does not establish
per-operation allocation cost, campaign acceptance, MessagePipe parity, or an IL2CPP headline.

| Identity                        | Value                                                                                                                                                                                                  |
| ------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Release                         | [Immutable observation prerelease](https://github.com/Ambiguous-Interactive/DxMessaging/releases/tag/perf-evidence-session271-subunsub-measured-r1-8eb717b0)                                           |
| Release ID                      | `386611585`                                                                                                                                                                                            |
| Asset                           | [session271 observation archive](https://github.com/Ambiguous-Interactive/DxMessaging/releases/download/perf-evidence-session271-subunsub-measured-r1-8eb717b0/session271-subunsub-measured-r1.tar.gz) |
| Exact asset name                | `session271-subunsub-measured-r1.tar.gz`                                                                                                                                                               |
| Asset ID and size               | `555789190`, 907 bytes                                                                                                                                                                                 |
| Archive SHA-256                 | `50ecad878a106beb1f692be41da4158e5e54323e9be560e56e7c1a4c2b6075a5`                                                                                                                                     |
| Manifest SHA-256                | `8eb717b0a6c366de8d3d2d90b98cf1d7a71b8ed5fbd43a3429308f6732fe82b8`                                                                                                                                     |
| Bundle digest                   | `a1638bafeddbcba88869424cf9fcfd8a3124e1d14e2cd539c05f6ba4d5bee7ac`                                                                                                                                     |
| Measured source                 | `857c293c2e03945383652b5c269e00bbf698a11a`                                                                                                                                                             |
| Verifier and release tag commit | `c2065e63e1d4605874dff0f7971df9f3371c6e99`                                                                                                                                                             |
| Reducer                         | `allocation-subunsub-observations-v1`                                                                                                                                                                  |

The draft contained exactly one asset. Its downloaded bytes matched the archive hash before
publication. Published metadata reported `draft: false`, `prerelease: true`, and `immutable: true`;
the tag resolved to the verifier commit. No asset was replaced.

The restore used a new remote clone at the verifier commit, `npm ci --ignore-scripts`, and
Node.js 24.20.0. Git status was empty before and after verification. The archive was downloaded
anonymously from the indexed GitHub URL, not copied from the producing checkout or an artifact
cache. Its three regular files had relative paths and normalized ownership, with no links.
Archive and manifest hashes matched the index. Both `verify` and `replay` exited zero; replay
reproduced the exact sealed normalized result.

The denied-access drill requested actual asset `555789190` through GitHub's release-asset API in a
separate temporary process with deliberately invalid credentials. GitHub returned HTTP 401 and
`curl --fail` exited 22. No asset file existed, verification and replay were not attempted, and
the experiment remained incomplete. The drill did not change the release, fabricate a missing
asset URL, or use cached evidence as a fallback. This proves the operator procedure's refusal;
there is no automated remote restore service implied by this result.

### s336-open-loop-editor, revision 1

Status: published and restored from GitHub in a fresh verifier clone on 2026-09-17. This is an
Editor PlayMode Mono protocol screen on Unity 6000.4.6f1. Its 12 traces and one tail-control
effect are descriptive; they do not establish an independent player session, IL2CPP confirmation,
an interval estimate, or a performance promotion verdict.

| Identity                        | Value                                                                                                                                                                                  |
| ------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Release                         | [Immutable s336 Editor evidence prerelease](https://github.com/Ambiguous-Interactive/DxMessaging/releases/tag/perf-evidence-s336-open-loop-editor-r1-7da590ac)                         |
| Release ID                      | `390857962`                                                                                                                                                                            |
| Asset                           | [s336 Editor evidence archive](https://github.com/Ambiguous-Interactive/DxMessaging/releases/download/perf-evidence-s336-open-loop-editor-r1-7da590ac/s336-open-loop-editor-r1.tar.gz) |
| Exact asset name                | `s336-open-loop-editor-r1.tar.gz`                                                                                                                                                      |
| Asset ID and size               | `570580334`, 21,824 bytes                                                                                                                                                              |
| Archive SHA-256                 | `a7bb9e40e8afe83e691a9c4a5c7154986fc94ba2aff8e6facca75ef1f2c13259`                                                                                                                     |
| Manifest SHA-256                | `7da590ac55e36a8a8a92c6e9e5c2fe3c325189e6f71eea0c9d28b1352b10fb8e`                                                                                                                     |
| Bundle digest                   | `8ef62f844f1c3e4533679df2df7ec19cc50955fcbe1ad8748425c3c6b4a2871f`                                                                                                                     |
| Measured source                 | `fe8fe99ec0382eaa2b85ad054b51209776f1d53c`                                                                                                                                             |
| Verifier and release tag commit | `79279da3d50e52a7e452e86f5ef515a873b69be3`                                                                                                                                             |
| Reducer                         | `open-loop-editor-capture-v1`                                                                                                                                                          |

The draft held exactly one asset. Its fresh download matched the archive hash, and the extracted
manifest matched the index before publication. Published metadata reported `draft: false`,
`prerelease: true`, and `immutable: true`; the tag resolves to the verifier commit.

An anonymous postpublication download into a new directory matched the indexed archive hash.
The archive contained eight relative regular files plus the manifest and one directory entry,
with normalized ownership and no links or special files. A new remote verifier clone checked out
the exact commit with clean status, installed pinned dependencies with `npm ci --ignore-scripts`,
and reproduced the indexed manifest hash. `verify` accepted all eight declared files and `replay`
reproduced the sealed result; both exited zero, and the worktree stayed clean.

The denied-access drill requested actual asset `570580334` through GitHub's release-asset API with
deliberately invalid credentials. GitHub returned HTTP 401 and `curl --fail` exited 22. No asset
file was produced, and verification and replay were not attempted. The inaccessible experiment
remained incomplete without using a cached fallback.
