/**
 * @fileoverview Static category guard for the "GUI-subsystem process is launched
 * but never waited on" PowerShell CI bug.
 *
 * THE CATEGORY (verified on pwsh 7.6.1, the CI runtime): Unity.exe is a Windows
 * GUI-subsystem binary. PowerShell's call operator `&` (and `.`) launches a
 * GUI-subsystem executable ASYNCHRONOUSLY -- it does NOT block until the process
 * exits and does NOT set `$LASTEXITCODE`. A site that does `& $editor @args` and
 * then reads `$LASTEXITCODE` therefore races the editor: the check runs against a
 * stale/zero exit code while Unity is still booting, so a real test failure (or a
 * crash) sails through as a green run. The proven fixes are EITHER:
 *   (a) consume the process's stdout in the SAME statement -- e.g.
 *       `& $editor @args 2>&1 | Tee-Object ...` -- because Unity is launched with
 *       `-logFile -` (logs to stdout); piping its output forces PowerShell to
 *       block until exit AND reliably populates `$LASTEXITCODE`; OR
 *   (b) `Start-Process $editor -ArgumentList $a -Wait`.
 * A pipe is NOT sufficient on its own: if the statement also carries
 * `-logFile <file>` (a real file path, not the dash `-`), Unity writes its log to
 * that file and NOTHING reaches stdout, so the pipe consumer drains immediately
 * and PowerShell does not wait. That is exactly the original finally-block bug
 * (`& $UnityEditorPath ... -logFile $returnLog | Out-Host`).
 *
 * This guard scans every pwsh surface we ship (standalone *.ps1 PLUS inline
 * `shell: pwsh` `run:` blocks in workflows and composite actions, globbing both
 * `*.yml` and `*.yaml`) and flags any UNSAFE Unity-EDITOR invocation. It is
 * deliberately PRECISE about what counts as an editor invocation so console tools
 * (`$script:UnityCliPath` -- the standalone `unity` CLI -- `docker`, `node`,
 * `git`, `pwsh`, `python3`, `id`, `hostname`) are NEVER flagged.
 *
 * WHAT COUNTS AS AN EDITOR INVOCATION. The guard matches the call operator `&`/`.`
 * (as a standalone token, so a member-access dot `$x.Foo` is never mistaken for
 * it) or `Start-Process` (positional target OR the `-FilePath <value>` named
 * parameter), whose target is EITHER:
 *   - a literal path ending in `Unity.exe` (case-insensitive) -- detected on the
 *     PRE-STRIP raw statement view, so a quoted literal such as
 *     `& "C:\Program Files\Unity\Editor\Unity.exe" @args` IS caught; OR
 *   - an editor-path VARIABLE. The variable matcher is broad on purpose so the
 *     bug shape is hard to reintroduce under a new name: it matches a lowercased
 *     bare name that CONTAINS `unity` OR STARTS WITH `editor` (covering `$unity`,
 *     `$unityExe`, `$unityBin`, `$unityApp`, `$unityPath`, `$UnityEditorPath`,
 *     `$editor`, `$editorExe`, `$editorBinary`, `$EditorPath`, ...), but it
 *     EXCLUDES any name containing `cli` (so `$script:UnityCliPath` / the console
 *     `unity` CLI is never matched) and is anchored so an unrelated
 *     verb-then-Editor name like `$ensureEditor` (a path to ensure-editor.ps1, a
 *     pwsh SCRIPT -- not Unity.exe) does NOT match.
 *
 * WHAT IS NOT DETECTED (by design): a bareword editor path with no `unity.exe` in
 * it (e.g. `& C:\custom\game-editor.bin`) and a target reached through indirection
 * the static view can't see. The literal/variable matchers above are the contract.
 *
 * STATEMENT ASSEMBLY. A logical pipeline can span physical lines. The scanner
 * joins the next line into the current statement when the current (stripped) line
 * ends with `|` or a trailing backtick line-continuation, OR when the next
 * (stripped, trimmed) line STARTS with `|` (leading-pipe continuation is valid in
 * pwsh 7). The pipeline/`-logFile` SAFETY classification then runs on the joined
 * STRIPPED view so `unity.exe` / `-logFile $x` text inside a string or here-string
 * never causes a false positive; only the literal-path TARGET detection peeks at
 * the raw (pre-strip) view. Pipeline-chain operators `&&` and `||` are statement
 * boundaries, so a chained editor call (`Test-Path x && & $editor @args`) is still
 * found. An inline suppression comment (`# unity-process-wait-safety: ignore`) on
 * any line of the statement opts a deliberate exception out.
 *
 * This is the static companion to unity-runner-strictmode-smoke.test.js: the
 * smoke test proves the live run of one script reaches a correct loud exit; this
 * guard prevents the async-no-wait *shape* from reappearing in ANY pwsh surface.
 *
 * pwsh is NOT required to run this guard (it is pure static analysis in JS), but
 * an always-on discovery assertion guarantees the scan is never silently empty.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");
const yaml = require("yaml");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

// Suppression marker for a deliberate, reviewed exception on an invocation line.
const SUPPRESS_MARKER = "unity-process-wait-safety: ignore";

// Pipeline consumers that drain a launched process's stdout. Presence of one of
// these downstream of an `&`/`.` editor call (with no file logFile) forces the
// wait + sets $LASTEXITCODE. `%` is the alias for ForEach-Object.
const STDOUT_CONSUMERS = [
  "Tee-Object",
  "Out-File",
  "Out-Host",
  "Out-Null",
  "Out-Default",
  "ForEach-Object",
  "%"
];

// A variable whose name denotes a Unity EDITOR path. Matched case-insensitively
// on the bare (no `$`) name. Two intentionally-broad alternatives so the bug
// shape is hard to reintroduce under a new variable name:
//   - CONTAINS `unity` ($unity, $unityExe, $unityBin, $unityApp, $unityPath,
//     $UnityEditorPath, ...); or
//   - STARTS WITH `editor` ($editor, $editorExe, $editorBinary, $EditorPath, ...).
// `editor` is ANCHORED to the start so an unrelated verb-then-Editor name like
// `$ensureEditor` (a path to ensure-editor.ps1, a pwsh SCRIPT, not Unity.exe) is
// NOT matched. Console CLIs are excluded separately by the `cli` check so
// `$script:UnityCliPath` is never treated as an editor.
const EDITOR_VAR_RE = /(unity|^editor)/i;

// ---------------------------------------------------------------------------
// Comment/string-stripped code view (adapted from
// powershell-strictmode-collection-safety.test.js). Blanks the INTERIOR of every
// quoted string and drops trailing `# comments`, so `unity.exe` / `-logFile $x`
// text living inside a string can never be mistaken for a real invocation.
// ---------------------------------------------------------------------------
function codeOf(line) {
  let out = "";
  let inSingle = false;
  let inDouble = false;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (ch === "'" && !inDouble) {
      out += ch;
      inSingle = !inSingle;
    } else if (ch === '"' && !inSingle) {
      out += ch;
      inDouble = !inDouble;
    } else if (ch === "#" && !inSingle && !inDouble) {
      break; // trailing comment: drop the remainder of the line
    } else if (inSingle || inDouble) {
      out += " "; // blank the string interior
    } else {
      out += ch;
    }
  }
  return out;
}

// Comment-stripped, STRING-PRESERVING view of a single line. Identical to codeOf
// except it copies string interiors through verbatim instead of blanking them.
// Crucially it is LENGTH- and OFFSET-IDENTICAL to codeOf up to the trailing
// comment (both emit exactly one output char per input char and both `break` at
// the same `#`), so a regex match index on the stripped view maps 1:1 onto this
// raw view. Used ONLY to recover the literal target text (e.g. a quoted
// `...\Unity.exe` path) after the operator/target position was found on the
// safe stripped view.
function rawCodeOf(line) {
  let out = "";
  let inSingle = false;
  let inDouble = false;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (ch === "'" && !inDouble) {
      out += ch;
      inSingle = !inSingle;
    } else if (ch === '"' && !inSingle) {
      out += ch;
      inDouble = !inDouble;
    } else if (ch === "#" && !inSingle && !inDouble) {
      break; // trailing comment: drop the remainder of the line (same as codeOf)
    } else {
      out += ch; // keep everything else, INCLUDING string interiors
    }
  }
  return out;
}

// Per-line "code" view of the whole file: here-string bodies (`@'...'@` /
// `@"..."@`) are blanked entirely (their content is data), and every other line
// is run through codeOf. Returns an array aligned 1:1 with the input lines. When
// `keepStrings` is true the per-line transform is rawCodeOf (string interiors are
// preserved) instead of codeOf -- used to build the literal-target raw view.
function stripCodeLines(lines, keepStrings) {
  const transform = keepStrings ? rawCodeOf : codeOf;
  const codes = new Array(lines.length);
  let hereTerminator = null; // "'@" or '"@' while inside a here-string body
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (hereTerminator !== null) {
      const trimmed = line.replace(/^\s+/, "");
      codes[i] = "";
      if (trimmed.startsWith(hereTerminator)) {
        hereTerminator = null;
      }
      continue;
    }
    const opener = line.match(/@("|')\s*$/);
    if (opener) {
      const head = line.slice(0, opener.index);
      codes[i] = transform(head);
      hereTerminator = opener[1] + "@";
      continue;
    }
    codes[i] = transform(line);
  }
  return codes;
}

// ---------------------------------------------------------------------------
// Classification helpers (operate on the stripped code view).
// ---------------------------------------------------------------------------

// Extract the raw (string-preserving) substring corresponding to a target token
// that was located on the stripped statement at offset `idx`. Because the
// stripped and raw statements are offset-identical (see rawCodeOf), the token
// occupies the same character span; we re-read it from the raw view so a quoted
// literal path keeps its interior (e.g. `"...\Unity.exe"`) for literal detection.
function rawTokenAt(rawStmt, idx, len) {
  if (typeof rawStmt !== "string") {
    return "";
  }
  return rawStmt.slice(idx, idx + len);
}

// True when the joined statement targets a Unity EDITOR binary via the call
// operator `&`, the dot operator `.`, or `Start-Process`. Returns the kind of
// invocation so the caller can apply the right safety rule.
//   { kind: 'call' | 'startprocess', target: '<string>' }  or  null
// `stmt` is the SAFE stripped view (used for positions + variable detection);
// `rawStmt` is the offset-aligned string-preserving view used ONLY to recover a
// quoted literal `Unity.exe` path.
function classifyEditorInvocation(stmt, rawStmt) {
  // Start-Process [<leading params>] <target> ...  The target is the first
  // non-flag token EXCEPT that an explicit `-FilePath <value>` names it. We
  // capture the full argument tail after `Start-Process` and locate the value.
  const sp = stmt.match(/(?:^|[;{(&|]|\bthen\b)\s*Start-Process\b([^|]*)/i);
  if (sp) {
    // Offset where the captured tail begins, so token positions map to rawStmt.
    const tailIdx = sp.index + sp[0].length - sp[1].length;
    const target = startProcessTarget(sp[1], tailIdx, rawStmt);
    if (target && isEditorTarget(target.stripped, target.raw)) {
      return { kind: "startprocess", target: target.stripped };
    }
    return null;
  }

  // Call/dot operator: `& <target>` or `. <target>`. The operator must be a
  // standalone token (start of statement, after `;`, `(`, `{`, `=`, or `|`) so a
  // `.` that is a member-access dot (`$x.Foo`) is never mistaken for the dot
  // operator. (`&&`/`||` chains are normalized to `;;` boundaries before this, so
  // a chained `... && & $editor` reaches here as `... ;; & $editor`.) The target
  // is either a quoted span (a string-literal path may contain spaces, e.g.
  // "C:\Program Files\...") or a bareword token. We scan every `&`/`.` operator
  // occurrence on the statement.
  const opRe = /(?:^|[;({=|]|\bthen\b)\s*([&.])\s+("[^"]*"|'[^']*'|\S+)/g;
  let m;
  while ((m = opRe.exec(stmt)) !== null) {
    const tokenIdx = m.index + m[0].length - m[2].length;
    const raw = rawTokenAt(rawStmt, tokenIdx, m[2].length);
    if (isEditorTarget(m[2], raw)) {
      // Prefer the raw token for the displayed target (identical for variables;
      // a readable literal path for quoted/bareword Unity.exe targets).
      const display = (raw || m[2]).replace(/^['"]|['"]$/g, "");
      return { kind: "call", target: display };
    }
  }
  return null;
}

// Resolve the file target of a `Start-Process` from its (stripped) argument tail.
// Honors the `-FilePath <value>` named parameter (skipping any leading flags /
// other named params) and otherwise takes the first positional (non-flag) token.
// Returns { stripped, raw } for the chosen value token, or null.
function startProcessTarget(tail, tailIdx, rawStmt) {
  // Tokenize on whitespace while tracking each token's offset within `tail`.
  const tokenRe = /\S+/g;
  let tok;
  const tokens = [];
  while ((tok = tokenRe.exec(tail)) !== null) {
    tokens.push({ text: tok[0], idx: tok.index });
  }
  // Pass 1: explicit -FilePath <value>.
  for (let i = 0; i < tokens.length - 1; i++) {
    if (/^-FilePath$/i.test(tokens[i].text)) {
      const v = tokens[i + 1];
      const absIdx = tailIdx + v.idx;
      return { stripped: v.text, raw: rawTokenAt(rawStmt, absIdx, v.text.length) };
    }
  }
  // Pass 2: first positional (non-flag) token.
  for (const t of tokens) {
    if (!t.text.startsWith("-")) {
      const absIdx = tailIdx + t.idx;
      return { stripped: t.text, raw: rawTokenAt(rawStmt, absIdx, t.text.length) };
    }
  }
  return null;
}

// True when a call/start-process target denotes a Unity EDITOR binary:
// a `Unity.exe` literal path (detected on the RAW string-preserving token so a
// QUOTED literal path is caught), or an editor-path variable that does NOT
// contain `cli` (so the console `unity` CLI is never matched). `strippedToken`
// is used for the variable-name match; `rawToken` is used for the literal match.
function isEditorTarget(strippedToken, rawToken) {
  if (!strippedToken) {
    return false;
  }
  // Literal-path target on the PRE-STRIP view: e.g. & "C:\...\Unity.exe" or
  // & 'C:\Unity\Editor\Unity.exe'. The raw token keeps the string interior, so a
  // quoted literal path is caught here (M1). A bareword `...\Unity.exe` (no
  // quotes) is identical in both views and is also caught.
  if (rawToken && /unity\.exe/i.test(rawToken)) {
    return true;
  }
  // Variable target: $name / ${name} / $script:name / $env:name. Read the bare
  // name from the stripped token (variable syntax has no string interior).
  const varMatch = strippedToken.match(/^\$\{?(?:[A-Za-z_]\w*:)?([A-Za-z_][\w]*)\}?/);
  if (!varMatch) {
    return false;
  }
  const name = varMatch[1].toLowerCase();
  if (name.includes("cli")) {
    return false; // $script:UnityCliPath etc. -- the console CLI, not the editor
  }
  return EDITOR_VAR_RE.test(name);
}

// A statement carries a FILE logFile (anything other than the dash `-`) when it
// has a literal `-logFile <X>` token where `<X>` is not `-`. A file logFile means
// Unity writes its log to that file and nothing reaches stdout, so even a pipe
// does NOT force the wait. A `-logFile -` (dash) keeps logs on stdout (safe), and
// the ABSENCE of any literal `-logFile` token means the value is supplied by a
// splatted `@args` array (which the callers populate with `-logFile -`) -- also
// treated as not-a-file.
//
// CONSERVATIVE BY DESIGN: this runs on the comment/string-STRIPPED view, so a
// QUOTED value's interior is already blanked (`-logFile "..."` -> `-logFile "`).
// We therefore cannot read a quoted value's contents here and treat any quoted
// (non-bare-dash) value as a file path. That fails SAFE: a quoted FILE path
// (`-logFile "C:\u.log"`) is correctly flagged, and the only over-flag is the
// never-written `-logFile "-"` (a quoted bare dash) -- which errs toward flagging,
// never toward missing a real un-waited launch. Real code uses the bare
// `-logFile -` / splatted form, both handled exactly.
function hasFileLogFile(stmt) {
  const re = /-logFile\s+(\S+)/i;
  const m = stmt.match(re);
  if (!m) {
    return false; // no literal token -> splatted @args supply -logFile -
  }
  return m[1] !== "-";
}

// True when the statement pipes into one of the stdout consumers that forces the
// wait. A consumer only counts when it is the COMMAND AT THE HEAD of a pipeline
// segment -- i.e. immediately after a `|` (modulo whitespace) -- not merely a
// substring somewhere downstream. So `& $editor @args | Tee-Object ...` is
// consumed, but `& $editor @args | Start-Sleep Out-Null` is NOT (Out-Null is an
// argument to Start-Sleep, not the segment's command). We split on `|` and test
// only the first token of each segment after the first.
function pipesIntoConsumer(stmt) {
  if (!stmt.includes("|")) {
    return false;
  }
  const segments = stmt.split("|").slice(1); // every pipeline segment after the head
  return segments.some((seg) => {
    const head = seg.trim().split(/\s+/)[0] || "";
    return STDOUT_CONSUMERS.some((c) => {
      if (c === "%") {
        return head === "%";
      }
      return new RegExp(`^${c}$`).test(head);
    });
  });
}

/**
 * Scan a single pwsh body. Returns an array of finding strings (empty when
 * clean). `relPath` is the surface label used in finding messages.
 */
function scanScript(relPath, text) {
  const findings = [];
  const lines = text.split(/\r?\n/);
  const codes = stripCodeLines(lines); // safe view (string interiors blanked)
  const rawCodes = stripCodeLines(lines, true); // string-preserving view (M1)

  // Strip a trailing backtick line-continuation from a stripped line, for the
  // "should I join the next line?" test (the backtick itself is not code).
  const dropBacktick = (s) => s.replace(/`\s*$/, "");

  for (let i = 0; i < codes.length; i++) {
    // Assemble a logical statement that may span physical lines. We join the
    // NEXT line into the current statement when ANY of these continuation forms
    // hold (all valid in pwsh 7):
    //   - the current (stripped) line ends with a trailing `|`         (M2)
    //   - the current (stripped) line ends with a trailing backtick    (M2)
    //   - the NEXT (stripped, trimmed) line STARTS with `|`            (M2)
    // The raw (string-preserving) statement is assembled in lockstep with the
    // SAME line set and the SAME `" "` join separators, so character offsets in
    // `stmt` map 1:1 onto `rawStmt` for literal-target recovery.
    let stmt = codes[i];
    let rawStmt = rawCodes[i];
    let last = i;
    while (last < codes.length - 1) {
      const cur = codes[last].replace(/\s+$/, "");
      const next = codes[last + 1].replace(/^\s+/, "");
      const endsWithPipe = /\|$/.test(cur);
      const endsWithBacktick = /`$/.test(cur);
      const nextStartsWithPipe = /^\|/.test(next);
      if (!endsWithPipe && !endsWithBacktick && !nextStartsWithPipe) {
        break;
      }
      // For a backtick continuation, drop the backtick before joining so it does
      // not survive into the classified statement.
      stmt = (endsWithBacktick ? dropBacktick(stmt) : stmt) + " " + codes[last + 1];
      rawStmt = (endsWithBacktick ? dropBacktick(rawStmt) : rawStmt) + " " + rawCodes[last + 1];
      last += 1;
    }

    // Normalize pipeline-chain operators `&&` / `||` to a pair of `;` boundary
    // tokens so a chained editor call (`Test-Path x && & $editor @args`) is seen
    // as a fresh statement and detected. The substitution is LENGTH-PRESERVING
    // (2 chars -> 2 chars) and applied to BOTH views, so the stripped/raw offset
    // alignment used for literal-target recovery is unaffected. (M5)
    const normalize = (s) => s.replace(/&&|\|\|/g, ";;");
    const invocation = classifyEditorInvocation(normalize(stmt), normalize(rawStmt));
    if (!invocation) {
      continue;
    }

    // Honor an inline suppression marker on ANY of the raw lines that make up
    // this statement (check the raw lines so the marker text in the comment is
    // still visible after stripping).
    let suppressed = false;
    for (let r = i; r <= last; r++) {
      if (lines[r].includes(SUPPRESS_MARKER)) {
        suppressed = true;
        break;
      }
    }
    if (suppressed) {
      i = last;
      continue;
    }

    const lineNo = i + 1;
    if (invocation.kind === "startprocess") {
      // Safe only with -Wait.
      if (/-Wait\b/i.test(stmt)) {
        i = last;
        continue;
      }
      findings.push(
        `${relPath}:${lineNo}: Start-Process ${invocation.target} launches the Unity editor ` +
          `WITHOUT -Wait, so PowerShell does not block until it exits and the exit code is ` +
          `lost. Add -Wait (Start-Process ${invocation.target} -ArgumentList <args> -Wait). ` +
          `(Suppress with "# ${SUPPRESS_MARKER}" if intentional.)`
      );
      i = last;
      continue;
    }

    // kind === 'call': safe ONLY when it pipes into a stdout consumer AND does
    // not carry a file logFile. A bare `& $editor @args` (no pipe) is unsafe; a
    // piped call with `-logFile <file>` is unsafe (nothing reaches stdout).
    const piped = pipesIntoConsumer(stmt);
    const fileLog = hasFileLogFile(stmt);
    if (piped && !fileLog) {
      i = last;
      continue;
    }

    let why;
    if (!piped) {
      why =
        "is launched via the call operator with NO pipeline consuming its stdout, so " +
        "PowerShell launches this GUI-subsystem binary asynchronously, does not wait for " +
        "it, and does not set $LASTEXITCODE";
    } else {
      why =
        "pipes its output to a consumer BUT also carries a file -logFile (not the dash '-'), " +
        "so Unity logs to that file and nothing reaches stdout -- the pipe drains immediately " +
        "and PowerShell does not wait for the editor to exit";
    }
    findings.push(
      `${relPath}:${lineNo}: ${invocation.target} (Unity editor) ${why}. ` +
        `Pipe its stdout with a dash logFile (& ${invocation.target} @args -logFile - 2>&1 | ` +
        `Tee-Object ...) or use Start-Process -Wait. ` +
        `(Suppress with "# ${SUPPRESS_MARKER}" if intentional.)`
    );
    i = last;
  }

  return findings;
}

// ---------------------------------------------------------------------------
// Discovery: standalone *.ps1 PLUS inline pwsh in workflows + composite actions
// (mirrors powershell-syntax.test.js's buildSnippets()/collectPwshSteps()).
// ---------------------------------------------------------------------------
function listTrackedFiles(globArg) {
  const result = spawnSync("git", ["ls-files", globArg], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });
  if (result.status !== 0) {
    return [];
  }
  return result.stdout
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean);
}

// GitHub Actions templating is resolved before PowerShell runs; replace each
// ${{ ... }} with a bareword so the residual is plain PowerShell text. (We do not
// parse, but this keeps the stripped view free of `${{` noise.)
function stripGithubExpressions(text) {
  return text.replace(/\$\{\{[\s\S]*?\}\}/g, "GHAEXPR");
}

function collectPwshSteps(steps, sourceLabel, sink) {
  if (!Array.isArray(steps)) {
    return;
  }
  steps.forEach((step, index) => {
    if (
      step &&
      typeof step.run === "string" &&
      typeof step.shell === "string" &&
      step.shell.toLowerCase() === "pwsh"
    ) {
      const name = step.name
        ? `${sourceLabel} :: ${step.name}`
        : `${sourceLabel} :: step[${index}]`;
      // `yaml: true` tags a snippet that came from a YAML pwsh block (action or
      // workflow), so the discovery test can assert the YAML surface is non-empty.
      sink.push({ name, src: stripGithubExpressions(step.run), yaml: true });
    }
  });
}

// Deduplicating union of two tracked-file globs (e.g. *.yml + *.yaml), preserving
// order. Both extensions are scanned so a workflow/action written with the
// `.yaml` extension cannot silently escape the guard.
function listTrackedFilesAny(...globArgs) {
  const seen = new Set();
  const out = [];
  for (const g of globArgs) {
    for (const rel of listTrackedFiles(g)) {
      if (!seen.has(rel)) {
        seen.add(rel);
        out.push(rel);
      }
    }
  }
  return out;
}

function buildSnippets() {
  const snippets = [];

  // 1) Standalone *.ps1 scripts (no GitHub templating).
  for (const rel of listTrackedFiles("*.ps1")) {
    const abs = path.join(REPO_ROOT, rel);
    snippets.push({ name: rel, src: fs.readFileSync(abs, "utf8") });
  }

  // 2) Composite-action pwsh steps (.yml AND .yaml).
  for (const rel of listTrackedFilesAny(
    ".github/actions/**/action.yml",
    ".github/actions/**/action.yaml"
  )) {
    const abs = path.join(REPO_ROOT, rel);
    const doc = yaml.parse(fs.readFileSync(abs, "utf8"));
    if (doc && doc.runs && Array.isArray(doc.runs.steps)) {
      collectPwshSteps(doc.runs.steps, rel, snippets);
    }
  }

  // 3) Workflow pwsh steps across every job (.yml AND .yaml).
  for (const rel of listTrackedFilesAny(
    ".github/workflows/*.yml",
    ".github/workflows/*.yaml"
  )) {
    const abs = path.join(REPO_ROOT, rel);
    const doc = yaml.parse(fs.readFileSync(abs, "utf8"));
    const jobs = (doc && doc.jobs) || {};
    for (const [jobId, job] of Object.entries(jobs)) {
      if (job && Array.isArray(job.steps)) {
        collectPwshSteps(job.steps, `${rel} :: ${jobId}`, snippets);
      }
    }
  }

  return snippets;
}

const SNIPPETS = buildSnippets();
const PS1_NAMES = SNIPPETS.filter((s) => s.name.endsWith(".ps1")).map((s) => s.name);
const YAML_BLOCK_COUNT = SNIPPETS.filter((s) => s.yaml).length;

describe("PowerShell Unity-editor process-wait static guard", () => {
  test("discovers the pwsh surface (scripts + action/workflow blocks)", () => {
    // A zero-snippet scan would silently validate nothing -- the false-green
    // class this whole change set exists to prevent.
    expect(SNIPPETS.length).toBeGreaterThanOrEqual(1);
    // The two scripts that actually launch the editor must be in scope.
    expect(PS1_NAMES).toContain("scripts/unity/run-ci-tests.ps1");
    expect(PS1_NAMES).toContain("scripts/unity/run-tests.ps1");
    // The .ps1 surface must be non-trivial (anti-no-op): there are many tracked
    // scripts, and dropping to ~0 would mean discovery silently broke.
    expect(PS1_NAMES.length).toBeGreaterThanOrEqual(5);
    // The YAML pwsh surface (action + workflow blocks) must not silently drop to
    // zero, so the .yml/.yaml inline-pwsh path stays guarded. (M7)
    expect(YAML_BLOCK_COUNT).toBeGreaterThanOrEqual(1);
  });

  test.each(SNIPPETS.map((s) => s.name))(
    "%s has no unsafe (no-wait) Unity-editor invocations",
    (name) => {
      const snippet = SNIPPETS.find((s) => s.name === name);
      const findings = scanScript(name, snippet.src);
      expect(findings).toEqual([]);
    }
  );

  // Detector self-tests: prove the guard FIRES on every unsafe shape and stays
  // SILENT on every safe shape and every console tool, so a future refactor
  // cannot quietly declaw it. Tiny in-memory fixtures; no repo files touched.
  describe("detector behavior on fixtures", () => {
    const wrap = (...body) => ["Set-StrictMode -Version Latest", ...body, ""].join("\n");

    test("FIRES on a bare `& $EditorPath @args` (no pipe)", () => {
      const findings = scanScript("fixture.ps1", wrap("& $EditorPath @Arguments"));
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("NO pipeline");
      expect(findings[0]).toContain("$EditorPath");
    });

    test("FIRES on a piped call carrying a file -logFile (the finally-block bug)", () => {
      const findings = scanScript(
        "fixture.ps1",
        wrap("& $UnityEditorPath -quit -batchmode -returnlicense -logFile $returnLog | Out-Host")
      );
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("file -logFile");
    });

    test("FIRES on Start-Process WITHOUT -Wait", () => {
      const findings = scanScript(
        "fixture.ps1",
        wrap("Start-Process $unityPath -ArgumentList $a")
      );
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("WITHOUT -Wait");
    });

    test("does NOT fire on `& $unityPath @unityArgs 2>&1 | Tee-Object` (splatted, piped)", () => {
      expect(
        scanScript(
          "fixture.ps1",
          wrap("& $unityPath @unityArgs 2>&1 | Tee-Object -FilePath $log")
        )
      ).toEqual([]);
    });

    test("does NOT fire on `& $EditorPath @args -logFile - 2>&1 | Tee-Object`", () => {
      expect(
        scanScript(
          "fixture.ps1",
          wrap("& $EditorPath @Arguments -logFile - 2>&1 | Tee-Object -FilePath $LogPath")
        )
      ).toEqual([]);
    });

    test("does NOT fire on Start-Process WITH -Wait", () => {
      expect(
        scanScript("fixture.ps1", wrap("Start-Process $unityPath -ArgumentList $a -Wait"))
      ).toEqual([]);
    });

    test("does NOT fire on console-tool calls (CLI / docker / node)", () => {
      expect(scanScript("fixture.ps1", wrap("& $script:UnityCliPath @Arguments"))).toEqual([]);
      expect(scanScript("fixture.ps1", wrap("& $DockerCommand info"))).toEqual([]);
      expect(scanScript("fixture.ps1", wrap("& node -e $s"))).toEqual([]);
      // The capturing-CLI form (CLI piped into ForEach-Object) must also be
      // ignored -- it is the console CLI, not the editor.
      expect(
        scanScript("fixture.ps1", wrap("& $script:UnityCliPath @Arguments 2>&1 | ForEach-Object { $_ }"))
      ).toEqual([]);
    });

    test("does NOT fire on a non-editor CONSOLE tool whose var has no unity/editor token", () => {
      // A console (non-GUI) helper tool's var name ($ConsoleToolPath) contains
      // no `unity`/`editor` token, so the editor-target matcher must NOT flag it.
      // The Tee-Object idiom here is incidental (wait + $LASTEXITCODE), not
      // because it is the editor. (Historically the retired Unity Licensing
      // Client was such a tool; the discrimination it exercised still matters.)
      expect(
        scanScript(
          "fixture.ps1",
          wrap(
            "$output = & $ConsoleToolPath --status 2>&1 | Tee-Object -FilePath $LogPath"
          )
        )
      ).toEqual([]);
      // The bare form too (no pipe needed -- it is not the editor, so even a
      // bare console call must not fire).
      expect(
        scanScript("fixture.ps1", wrap("& $ConsoleToolPath --whoami $token"))
      ).toEqual([]);
    });

    test("does NOT false-positive on unity.exe / -logFile $x text inside strings", () => {
      // Double-quoted string mentioning unity.exe and a file logFile.
      expect(
        scanScript(
          "fixture.ps1",
          wrap('Write-Host "would run unity.exe -logFile $returnLog without a pipe"')
        )
      ).toEqual([]);
      // Here-string body mentioning the same tokens -- pure data.
      expect(
        scanScript(
          "fixture.ps1",
          wrap("$doc = @'", "& $EditorPath @Arguments -logFile $f and unity.exe documentation", "'@", "Write-Host $doc")
        )
      ).toEqual([]);
    });

    test("does NOT mistake member-access dot (`$x.Foo`) for the dot operator", () => {
      expect(scanScript("fixture.ps1", wrap("$UnityEditorPath.Trim()"))).toEqual([]);
    });

    test("respects the inline suppression marker", () => {
      expect(
        scanScript(
          "fixture.ps1",
          wrap("& $EditorPath @Arguments # unity-process-wait-safety: ignore")
        )
      ).toEqual([]);
    });

    test("handles a multi-line pipeline whose `|` continues to the next line", () => {
      // Safe: a dash logFile piped to Tee-Object split across two physical lines.
      expect(
        scanScript(
          "fixture.ps1",
          wrap("& $EditorPath @Arguments -logFile - 2>&1 |", "    Tee-Object -FilePath $LogPath")
        )
      ).toEqual([]);
      // Unsafe: a file logFile piped to Out-Host split across two physical lines.
      const bad = scanScript(
        "fixture.ps1",
        wrap("& $UnityEditorPath -returnlicense -logFile $returnLog |", "    Out-Host")
      );
      expect(bad).toHaveLength(1);
      expect(bad[0]).toContain("file -logFile");
    });

    // -----------------------------------------------------------------------
    // M1 -- literal `Unity.exe` PATH targets (quoted, the file's own doc claim).
    // -----------------------------------------------------------------------
    test("M1: FIRES on a bare `& \"...\\Unity.exe\" @args` double-quoted literal", () => {
      const findings = scanScript(
        "fixture.ps1",
        wrap('& "C:\\Program Files\\Unity\\Editor\\Unity.exe" @Arguments')
      );
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("NO pipeline");
    });

    test("M1: FIRES on a bare `& '...\\Unity.exe' @args` single-quoted literal", () => {
      const findings = scanScript(
        "fixture.ps1",
        wrap("& 'C:\\Unity\\Editor\\Unity.exe' @Arguments")
      );
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("NO pipeline");
    });

    test("M1: does NOT fire on a SAFE `& \"...\\Unity.exe\" @args -logFile - 2>&1 | Tee-Object`", () => {
      expect(
        scanScript(
          "fixture.ps1",
          wrap('& "C:\\Program Files\\Unity\\Editor\\Unity.exe" @Arguments -logFile - 2>&1 | Tee-Object -FilePath $log')
        )
      ).toEqual([]);
    });

    test("M1: does NOT fire when unity.exe appears only in a comment", () => {
      expect(
        scanScript("fixture.ps1", wrap("# we used to call unity.exe here without a wait"))
      ).toEqual([]);
    });

    test("M1: does NOT fire when unity.exe appears only in a string ASSIGNMENT (not an invocation)", () => {
      expect(
        scanScript("fixture.ps1", wrap('$exe = "C:\\Unity\\Editor\\Unity.exe"'))
      ).toEqual([]);
    });

    // -----------------------------------------------------------------------
    // M2 -- leading-pipe and backtick line-continuation must NOT false-positive.
    // -----------------------------------------------------------------------
    test("M2: does NOT fire on the leading-pipe safe form (| starts the next line)", () => {
      expect(
        scanScript(
          "fixture.ps1",
          wrap("& $EditorPath @Arguments -logFile - 2>&1", "  | Tee-Object -FilePath $log")
        )
      ).toEqual([]);
    });

    test("M2: does NOT fire on the trailing-backtick safe form", () => {
      expect(
        scanScript(
          "fixture.ps1",
          wrap("& $EditorPath @Arguments -logFile - 2>&1 `", "  | Tee-Object -FilePath $log")
        )
      ).toEqual([]);
    });

    test("M2: still fires (trailing-pipe) on a file-logFile pipeline split with a trailing |", () => {
      // The trailing-pipe safe form is covered above; here keep a trailing-pipe
      // UNSAFE case (file logFile) so trailing-pipe join is exercised both ways.
      const bad = scanScript(
        "fixture.ps1",
        wrap("& $EditorPath @Arguments -logFile $f 2>&1 |", "  Out-Host")
      );
      expect(bad).toHaveLength(1);
      expect(bad[0]).toContain("file -logFile");
    });

    // -----------------------------------------------------------------------
    // M3 -- Start-Process -FilePath named-parameter target.
    // -----------------------------------------------------------------------
    test("M3: FIRES on `Start-Process -FilePath $EditorPath -ArgumentList $a` (no -Wait)", () => {
      const findings = scanScript(
        "fixture.ps1",
        wrap("Start-Process -FilePath $EditorPath -ArgumentList $a")
      );
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("WITHOUT -Wait");
    });

    test("M3: does NOT fire on `Start-Process -FilePath $EditorPath -ArgumentList $a -Wait`", () => {
      expect(
        scanScript(
          "fixture.ps1",
          wrap("Start-Process -FilePath $EditorPath -ArgumentList $a -Wait")
        )
      ).toEqual([]);
    });

    test("M3: FIRES on the positional `Start-Process $EditorPath` form (no -Wait)", () => {
      const findings = scanScript("fixture.ps1", wrap("Start-Process $EditorPath -ArgumentList $a"));
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("WITHOUT -Wait");
    });

    test("M3: does NOT fire on Start-Process of a non-editor (`-FilePath $DockerCommand`)", () => {
      expect(
        scanScript("fixture.ps1", wrap("Start-Process -FilePath $DockerCommand -ArgumentList $a"))
      ).toEqual([]);
    });

    // -----------------------------------------------------------------------
    // M4 -- broadened editor-variable-name coverage (names cannot be reintroduced undetected).
    // -----------------------------------------------------------------------
    test("M4: FIRES on `& $unity @args` (bare 'unity' name, no pipe)", () => {
      const findings = scanScript("fixture.ps1", wrap("& $unity @Arguments"));
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("NO pipeline");
    });

    test("M4: FIRES on `& $editorExe @args` (starts-with-editor name, no pipe)", () => {
      const findings = scanScript("fixture.ps1", wrap("& $editorExe @Arguments"));
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("NO pipeline");
    });

    test("M4: does NOT fire on `& $unityCli @args` (name contains 'cli')", () => {
      expect(scanScript("fixture.ps1", wrap("& $unityCli @Arguments"))).toEqual([]);
    });

    test("M4: does NOT fire on `& $DockerCommand info` (non-editor name)", () => {
      expect(scanScript("fixture.ps1", wrap("& $DockerCommand info"))).toEqual([]);
    });

    test("M4: does NOT fire on `& $ensureEditor @args` (verb-then-Editor script ref, not the editor)", () => {
      // $ensureEditor is the path to ensure-editor.ps1 (a pwsh script). The
      // anchored `^editor` rule means a verb-then-Editor name is NOT an editor.
      expect(scanScript("fixture.ps1", wrap("& $ensureEditor @ensureArgs | Select-Object -Last 1"))).toEqual([]);
    });

    // -----------------------------------------------------------------------
    // M5 -- &&/|| pipeline-chain operators as statement boundaries.
    // -----------------------------------------------------------------------
    test("M5: FIRES on a chained `Test-Path x && & $EditorPath @args`", () => {
      const findings = scanScript("fixture.ps1", wrap("Test-Path $p && & $EditorPath @Arguments"));
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("NO pipeline");
    });

    test("M5: FIRES on a chained `Test-Path x || & $EditorPath @args`", () => {
      const findings = scanScript("fixture.ps1", wrap("Test-Path $p || & $EditorPath @Arguments"));
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("NO pipeline");
    });

    // -----------------------------------------------------------------------
    // M6 -- consumer must be at the HEAD of a pipeline segment.
    // -----------------------------------------------------------------------
    test("M6: FIRES when a consumer keyword is only a downstream ARGUMENT, not a segment head", () => {
      // `Out-Null` here is an argument to Start-Sleep, NOT the segment command,
      // so it does not consume the editor's stdout -> still unsafe.
      const findings = scanScript(
        "fixture.ps1",
        wrap("& $EditorPath @Arguments | Start-Sleep Out-Null")
      );
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("NO pipeline");
    });

    test("M6: does NOT fire when the consumer IS the segment head", () => {
      expect(
        scanScript("fixture.ps1", wrap("& $EditorPath @Arguments -logFile - 2>&1 | Out-Null"))
      ).toEqual([]);
    });
  });
});

module.exports = { scanScript, classifyEditorInvocation, isEditorTarget };
