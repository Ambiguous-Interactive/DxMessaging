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

| Failure                                                   | Cause                                                           |
| --------------------------------------------------------- | --------------------------------------------------------------- |
| `hashes to ... but the manifest declares ...`             | A raw file changed after sealing                                |
| `is declared by the manifest but could not be read`       | A required artifact is missing or unreachable                   |
| `Undeclared files are present in the bundle`              | Something was added after sealing                               |
| `does not match its own contents`                         | The manifest itself was edited, including its normalized result |
| `is already sealed as ... but these bytes seal as ...`    | An overwrite of sealed evidence; publish a new revision instead |
| `looks like it contains ...; scrub it before sealing`     | Sensitive data is still present; see below                      |
| `does not use a reviewed text evidence extension ...`     | The artifact class has no approved inspection rule              |
| `is not valid UTF-8 or byte-order-marked UTF-16 text ...` | The file has malformed or opaque bytes                          |
| `contains non-text control bytes` or `too many NUL bytes` | The file does not meet the reviewed-text contract               |
| `reports ... for cell ... but its own evidence says ...`  | The matrix summary disagrees with the per-cell evidence         |

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
inside the 14-day workflow artifact.

Durable GitHub prerelease publication and independent restore remain tracked by #521. Do not cite a
workflow artifact as durable campaign evidence until that publication and restore gate exists.
