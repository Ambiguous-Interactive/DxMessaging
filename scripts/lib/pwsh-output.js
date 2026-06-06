/**
 * @fileoverview Normalize PowerShell-rendered console output so that phrase /
 * substring assertions are STABLE across host console widths and color modes.
 *
 * THE ROOT-CAUSE THIS SOLVES (a real, width-dependent CI flake):
 *   When `pwsh -File <script>` lets an unhandled terminating error (`throw`)
 *   reach the top of the script, PowerShell's default "ConciseView" error
 *   formatter renders it as a multi-line block: a source-context excerpt, a
 *   `Line |` header, a caret (`~~~`) underline, and the error MESSAGE itself --
 *   and it WORD-WRAPS that message at the host console width, inserting a
 *   continuation gutter (`\n     | `) between words, plus ANSI color escapes
 *   around every fragment. On a wide terminal the message stays on one line; on
 *   a narrower one (the Windows CI runner's effective width, ~105-120 cols) a
 *   phrase like "outside the managed root" is split as
 *   "...outside the\n     | managed root.", so a literal substring assertion for
 *   "outside the managed root" finds nothing -- on Windows only. The PRODUCTION
 *   message is correct; the assertion is what is fragile.
 *
 *   `Write-Host` output, by contrast, is NOT subject to this wrap, which is why
 *   `::error::` / `::warning::` annotations are a stable assertion target.
 *
 * THE FIX:
 *   Run pwsh stdout/stderr through `normalizePwshText` BEFORE a phrase / error
 *   substring assertion. It strips ANSI/OSC escapes, normalizes line endings,
 *   rejoins the ConciseView continuation gutter, and collapses whitespace runs,
 *   recovering the logical message text regardless of the width it was rendered
 *   at. It is PURE and IDEMPOTENT: on already-clean text (plain `Write-Host`
 *   stdout, or text that was never wrapped) it only collapses surrounding
 *   whitespace, so it is safe to apply uniformly to any value used for a phrase
 *   assertion. Do NOT use it where line STRUCTURE matters (e.g. taking the last
 *   line of stdout to read a resolved path) -- it intentionally flattens
 *   newlines; keep the raw stream for those reads.
 *
 * SCOPE / WHAT IT DOES NOT DO:
 *   This recovers WORD-WRAPPED text (the documented ConciseView failure). It does
 *   not attempt to reconstruct a phrase that PowerShell TRUNCATED with a trailing
 *   ellipsis on the source-context excerpt line (the `4 | throw "..." ...` line);
 *   that excerpt is a rendering of the SOURCE, not the message, and the full
 *   message is always emitted (wrapped) on its own gutter lines below, which IS
 *   recovered. Assert against the message, not the source excerpt.
 *
 *   Two consequences of the flatten-everything-to-one-line approach, both of which
 *   mean a `.toContain` here proves only that words appear in the stream, NOT that
 *   they were emitted as one contiguous phrase:
 *     (a) Collapsing newlines GLUES the end of one logical line to the start of the
 *         next, so a substring can span text that was never rendered contiguously
 *         (e.g. the tail of an error line + a `Write-Host` line that followed it can
 *         read as a single phrase). Because of this, the MOST robust assertion
 *         target is the wrap-immune, single-line `::error::` / `::warning::`
 *         annotation (Write-Host is not word-wrapped) -- assert the substring is in
 *         THAT line, not merely somewhere in the whitespace-collapsed blob.
 *     (b) The gutter rejoin merges ANY line whose first non-space character is `|`
 *         into the previous line -- it cannot tell the ConciseView continuation
 *         gutter apart from a LEGITIMATE leading-`|` value. A Markdown table row, a
 *         `Format-Table` rule, or any emitted value that starts with `|` would be
 *         folded into the line above. The normalizer is scoped to ERROR-MESSAGE
 *         recovery; do not run it over output where a leading `|` is real data.
 */

"use strict";

// Control Sequence Introducer / SGR color escapes: ESC [ <params> <intermediate>
// <final>. Covers the color runs PowerShell wraps every fragment in, cursor
// moves, etc. Parameter bytes 0x30-0x3F, intermediate 0x20-0x2F, final 0x40-0x7E.
const CSI_ESCAPE = /\x1B\[[0-9;?]*[ -/]*[@-~]/g;

// Operating System Command sequences: ESC ] ... terminated by BEL (0x07) or the
// String Terminator ESC \. Some terminals/title writers emit these; strip them so
// they never leak into an assertion. The body excludes BEL and ESC so the match
// cannot run past its terminator.
const OSC_ESCAPE = /\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)/g;

// Lone two-character (Fe) escapes that are neither CSI nor OSC (e.g. ESC c reset).
// Stripped after CSI/OSC so those longer, more specific forms match first.
const LONE_FE_ESCAPE = /\x1B[@-Z\\-_]/g;

// The ConciseView continuation gutter that PowerShell inserts when it word-wraps
// the error message: a newline, run of spaces/tabs, a single `|`, then an
// optional single separating space. Rejoining with a single space reconstitutes
// the wrapped words ("outside the\n     | managed" -> "outside the managed").
const CONTINUATION_GUTTER = /\n[ \t]*\|[ \t]?/g;

// Any run of whitespace (including the newlines that survive gutter rejoin)
// collapses to a single space so a multi-line rendering compares equal to its
// single-line logical form.
const WHITESPACE_RUN = /\s+/g;

/**
 * Normalize PowerShell-rendered console text for stable phrase/substring
 * assertions. Strips ANSI/OSC escape sequences, normalizes CRLF/CR to LF,
 * rejoins the ConciseView word-wrap continuation gutter, collapses whitespace
 * runs to single spaces, and trims. Pure and idempotent.
 *
 * @param {unknown} value - Raw pwsh stdout/stderr (or any value; coerced to string).
 * @returns {string} The normalized, wrap-immune text.
 */
function normalizePwshText(value) {
  return String(value)
    .replace(OSC_ESCAPE, "")
    .replace(CSI_ESCAPE, "")
    .replace(LONE_FE_ESCAPE, "")
    .replace(/\r\n?/g, "\n")
    .replace(CONTINUATION_GUTTER, " ")
    .replace(WHITESPACE_RUN, " ")
    .trim();
}

/**
 * Merge a pwsh run's stdout+stderr into one normalized string for phrase /
 * substring assertions. The shared wrapper that test files use so a phrase
 * assertion against pwsh output is width-immune (rejoins the ConciseView gutter,
 * strips ANSI). Use ONLY for phrase assertions; reads that depend on line
 * structure (e.g. taking the last stdout line to get a resolved path) MUST use
 * the raw stream.
 *
 * @param {{stdout?: unknown, stderr?: unknown}} run - A child_process spawn result.
 * @returns {string} The normalized, wrap-immune stdout+stderr text.
 */
function combinedText(run) {
  return normalizePwshText(`${run.stdout || ""}\n${run.stderr || ""}`);
}

/**
 * Normalize a pwsh run's stdout alone for phrase / substring assertions. Same
 * width-immunity as combinedText but for the stdout-only case (e.g. a script that
 * routes its diagnostics through stdout). A no-op on already-clean single-line
 * stdout.
 *
 * @param {{stdout?: unknown}} result - A child_process spawn result.
 * @returns {string} The normalized, wrap-immune stdout text.
 */
function stdoutText(result) {
  return normalizePwshText(result.stdout || "");
}

/**
 * Produce a structured diagnostic that explains WHY a phrase assertion against
 * raw pwsh output might pass on a wide host yet fail on the narrower Windows
 * runner. Pure: it does not mutate global state, print, or change normalize's
 * output -- it merely reports which width-dependent artifacts are present and
 * whether normalization recovered the text. It is the data source for
 * assertPwshContains's failure message (the real consumer).
 *
 * The detection reuses the SAME regexes normalizePwshText applies, so what it
 * reports is exactly what normalization acts on:
 *   - `hadAnsiEscapes`: a CSI/SGR, OSC, or lone Fe escape sequence is present.
 *   - `hadContinuationGutter`: the ConciseView word-wrap gutter (`\n  | `) is
 *     present, i.e. the message was split across console-width lines.
 *   - `textChanged`: normalization changed the text (escapes stripped, gutter
 *     rejoined, and/or whitespace collapsed) -- a raw `.toContain` would
 *     therefore see different bytes than a normalized one.
 *   - `normalized`: the recovered, wrap-immune text.
 *
 * @param {unknown} raw - Raw pwsh stdout/stderr (or any value; coerced to string).
 * @returns {{hadAnsiEscapes: boolean, hadContinuationGutter: boolean,
 *   textChanged: boolean, normalized: string}}
 */
function describePwshNormalization(raw) {
  const rawText = String(raw);
  const normalized = normalizePwshText(rawText);
  // Use fresh, non-global copies so the global-flag `lastIndex` state on the shared
  // module-level regexes is never observed or mutated across calls.
  const hadAnsiEscapes =
    new RegExp(CSI_ESCAPE.source).test(rawText) ||
    new RegExp(OSC_ESCAPE.source).test(rawText) ||
    new RegExp(LONE_FE_ESCAPE.source).test(rawText);
  // Detect the gutter on the text AS normalizePwshText sees it just before the
  // gutter rejoin: ANSI/OSC/Fe escapes stripped and CRLF/CR -> LF. The real
  // captured output is CRLF-terminated and wraps each fragment in color escapes, so
  // the `\n  | ` gutter only matches once both of those are removed first.
  const preGutter = rawText
    .replace(new RegExp(OSC_ESCAPE.source, "g"), "")
    .replace(new RegExp(CSI_ESCAPE.source, "g"), "")
    .replace(new RegExp(LONE_FE_ESCAPE.source, "g"), "")
    .replace(/\r\n?/g, "\n");
  const hadContinuationGutter = new RegExp(CONTINUATION_GUTTER.source).test(preGutter);
  const textChanged = normalized !== rawText;
  return {
    hadAnsiEscapes,
    hadContinuationGutter,
    textChanged,
    normalized
  };
}

// Cap the normalized-text preview in a failure message so a huge stream cannot
// swamp the diagnostic; the recovered phrase is the part a developer needs.
const PREVIEW_LIMIT = 600;
const SPAWN_OUTPUT_PREVIEW_LIMIT = 1200;

function preview(value, limit = SPAWN_OUTPUT_PREVIEW_LIMIT) {
  const text = normalizePwshText(value || "");
  return text.length > limit ? `${text.slice(0, limit)}...` : text;
}

function formatSpawnError(error) {
  if (!error) {
    return "(none)";
  }

  const fields = [
    error.name ? `name=${error.name}` : null,
    error.code ? `code=${error.code}` : null,
    error.errno ? `errno=${error.errno}` : null,
    error.syscall ? `syscall=${error.syscall}` : null,
    error.message ? `message=${error.message}` : null
  ].filter(Boolean);
  return fields.length > 0 ? fields.join(" ") : String(error);
}

/**
 * Format process-level fields from a child_process.spawnSync result. Use this
 * when a test expects a pwsh helper to exit 0 or non-zero: `status: null` means
 * the child did not produce a normal exit code, and the useful evidence lives in
 * `signal`, `error`, stdout, and stderr.
 *
 * @param {{status?: number|null, signal?: string|null, error?: Error|null,
 *   stdout?: unknown, stderr?: unknown}} run - A spawnSync result.
 * @param {string} label - Human-readable command/test label.
 * @returns {string} Multi-line diagnostic text.
 */
function describeSpawnResult(run, label = "spawned process") {
  const result = run || {};
  return [
    `${label} spawn result:`,
    `  status: ${result.status === null || result.status === undefined ? "(null)" : result.status}`,
    `  signal: ${result.signal || "(none)"}`,
    `  error:  ${formatSpawnError(result.error)}`,
    `  stdout: ${JSON.stringify(preview(result.stdout))}`,
    `  stderr: ${JSON.stringify(preview(result.stderr))}`
  ].join("\n");
}

function assertSpawnStatus(run, expectedStatus, label = "spawned process") {
  if (run && run.status === expectedStatus) {
    return;
  }

  const actual =
    run && Object.prototype.hasOwnProperty.call(run, "status") ? run.status : undefined;
  throw new Error(
    `${label} exited with status ${actual}; expected ${expectedStatus}.\n` +
      describeSpawnResult(run, label)
  );
}

function assertSpawnNonZeroStatus(run, label = "spawned process") {
  if (run && typeof run.status === "number" && run.status !== 0) {
    return;
  }

  const actual =
    run && Object.prototype.hasOwnProperty.call(run, "status") ? run.status : undefined;
  throw new Error(
    `${label} did not exit with a normal non-zero status; actual status was ${actual}.\n` +
      describeSpawnResult(run, label)
  );
}

/**
 * Select the RAW (un-normalized) stream text from a spawn result or string,
 * matching how the combinedText/stdoutText wrappers pick their source. The
 * "combined" stream merges stdout+stderr exactly as combinedText does before
 * normalizing, so the diagnostic describes the same bytes the assertion checks.
 *
 * @param {{stdout?: unknown, stderr?: unknown}|string} run - A spawn result or a string.
 * @param {"combined"|"stdout"|"stderr"} stream - Which stream to read.
 * @returns {string} The raw stream text.
 */
function rawStreamText(run, stream) {
  if (typeof run === "string") {
    return run;
  }
  if (stream === "stdout") {
    return String(run.stdout || "");
  }
  if (stream === "stderr") {
    return String(run.stderr || "");
  }
  return `${run.stdout || ""}\n${run.stderr || ""}`;
}

/**
 * Assert that the NORMALIZED form of a pwsh run contains `phrase`. This is the
 * width-immune replacement for `expect(run.stdout).toContain(phrase)` on
 * ConciseView-wrapped output: it normalizes first (rejoining the word-wrap gutter
 * and stripping ANSI) so the assertion holds regardless of the host console
 * width. On a miss it throws a multi-line diagnostic -- built from
 * describePwshNormalization of the SAME raw stream -- that tells the developer
 * whether a wrap gutter / ANSI escapes were present and shows the recovered text,
 * which is exactly what is needed to understand a Windows-only wrap failure.
 *
 * @param {{stdout?: unknown, stderr?: unknown}|string} run - A spawn result or a string.
 * @param {string} phrase - The expected substring.
 * @param {{stream?: ("combined"|"stdout"|"stderr")}} [options] - Which stream to
 *   read; defaults to "combined" (normalized stdout+stderr).
 * @returns {void} Returns on a match; throws otherwise.
 */
function assertPwshContains(run, phrase, options = {}) {
  // Derive `stream` defensively: the `options = {}` default only fires on
  // `undefined`, so an explicit `null` would otherwise throw on `options.stream`.
  const stream = (options && options.stream) || "combined";
  const raw = rawStreamText(run, stream);
  // describePwshNormalization is the single source of truth here: it both
  // normalizes and reports the width-dependent artifacts of the RAW stream.
  const report = describePwshNormalization(raw);
  if (report.normalized.includes(phrase)) {
    return;
  }
  const preview =
    report.normalized.length > PREVIEW_LIMIT
      ? `${report.normalized.slice(0, PREVIEW_LIMIT)}...`
      : report.normalized;
  throw new Error(
    `assertPwshContains: phrase not found in normalized pwsh ${stream} output.\n` +
      `  phrase:                ${JSON.stringify(phrase)}\n` +
      `  hadAnsiEscapes:        ${report.hadAnsiEscapes}\n` +
      `  hadContinuationGutter: ${report.hadContinuationGutter}\n` +
      `  textChanged:           ${report.textChanged}\n` +
      `  normalized (preview):  ${JSON.stringify(preview)}`
  );
}

module.exports = {
  normalizePwshText,
  combinedText,
  stdoutText,
  describePwshNormalization,
  assertPwshContains,
  describeSpawnResult,
  assertSpawnStatus,
  assertSpawnNonZeroStatus
};
