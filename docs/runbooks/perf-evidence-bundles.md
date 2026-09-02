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
| `sourceCommit`  | The commit the run was built from                                               |
| `files`         | Every file, POSIX-relative, with its byte length and lowercase SHA-256          |
| `normalized`    | The machine-readable result a decision cites                                    |
| `bundleDigest`  | SHA-256 over the identity, the file inventory, and `normalized`                 |

Paths are POSIX-relative and rejected outright if they are absolute, carry a drive letter, use
backslashes, or contain a `..` segment. A bundle sealed on the Windows perf runner therefore
verifies unchanged on a Linux or macOS reviewer machine.

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

`verify` proves the bytes are unchanged. `replay` proves the published conclusion follows from
them. Cite a result only after `replay` succeeds.

## What fails, and what it means

| Failure                                                  | Cause                                                           |
| -------------------------------------------------------- | --------------------------------------------------------------- |
| `hashes to ... but the manifest declares ...`            | A raw file changed after sealing                                |
| `is declared by the manifest but could not be read`      | A required artifact is missing or unreachable                   |
| `Undeclared files are present in the bundle`             | Something was added after sealing                               |
| `does not match its own contents`                        | The manifest itself was edited, including its normalized result |
| `is already sealed as ... but these bytes seal as ...`   | An overwrite of sealed evidence; publish a new revision instead |
| `looks like it contains ...; scrub it before sealing`    | Credential material is still present; see below                 |
| `reports ... for cell ... but its own evidence says ...` | The matrix summary disagrees with the per-cell evidence         |

## Credential refusal

Sealing refuses any file carrying credential material, using the shared list in
`scripts/unity/credential-patterns.js`. This is a backstop, not the primary control. The primary
control is the `./.github/actions/redact-unity-artifacts` step that runs before every Unity artifact
upload; sealing exists to fail closed if redaction is ever skipped or a new log format appears.

If sealing refuses a file, scrub the file. Do not weaken the pattern.

## Adding a reducer

A reducer must be a pure function of the bundle's bytes. Read only from the supplied content map,
never from disk, the clock, or the environment. Copy measured values verbatim and derive only exact
integer comparisons, so no floating-point rounding can differ between the sealing runner and a
reviewer's machine. Order every array by an ordinal key rather than by directory-walk order.

Register it in `REDUCERS` in `scripts/unity/perf-evidence-bundle.js` and add cases to
`scripts/__tests__/perf-evidence-bundle.test.js` covering a missing input, a corrupted input, and a
summary that disagrees with the raw rows it claims to describe.

## Where bundles are produced

The `unity-tests` job seals the shipping-fidelity matrix after redaction and before upload, then
replays it, so a bundle that cannot reproduce its own result fails the run. The manifest travels
inside the uploaded artifact.

Publishing sealed bundles as immutable GitHub prereleases named
`perf-evidence-<experiment-id>-<manifest-sha8>` is the remaining part of #508 and is not implemented
yet.
