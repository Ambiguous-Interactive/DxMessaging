/**
 * @fileoverview Static category guard for the StrictMode "0/1/many" collection
 * gotcha in PowerShell scripts.
 *
 * THE CATEGORY (verified on pwsh 7.6.1, the CI runtime): a function that
 * `return @()` on an empty path emits ZERO objects. A bare capture
 * (`$x = Get-Foo`) therefore assigns AutomationNull (it compares equal to $null)
 * -- the empty array unwraps to nothing. Reading `.Count` / `.Length` of that
 * value THROWS ("property '...' cannot be found on this object") under
 * `Set-StrictMode -Version` 2.0 and every higher level; only `-Version 1.0` (or
 * Off) avoids that throw (and under 1.0 it returns the integer 0, not $null).
 * Indexing it (`$x[0]`) THROWS ("Cannot index into a null array") at EVERY
 * StrictMode level (indexing-null is not StrictMode-gated). The fix is to
 * `@(...)`-wrap the captured result so it is ALWAYS a real array (Count 0 when
 * empty). (Note: concatenating `... + $x` does NOT inject a spurious element
 * when $x is the empty/AutomationNull capture -- `+` DROPS it; a LITERAL $null
 * operand would instead ADD an element. So the real bugs are the property/index
 * reads on the unwrapped capture, not the `+`.)
 *
 * This guard scans every .ps1 that enables StrictMode at a version where the
 * throw occurs and flags: a variable assigned DIRECTLY from a call to a function
 * DEFINED IN THE SAME FILE, WITHOUT `@(...)` / leading `,` wrapping, whose
 * `.Count`, `.Length`, or indexing read (`$x[...]`) appears later in the file.
 * It is deliberately PRECISE (low false-positive): it only considers
 * locally-defined functions, so guaranteed-collection cmdlets (e.g.
 * `[regex]::Matches(...).Count`, `Get-ChildItem`) are never flagged; and it
 * ignores `.Count`/`.Length`/index text that lives inside quoted strings or
 * here-strings (not real reads). An inline suppression comment
 * (`# strictmode-collection-safety: ignore`) on the assignment line opts a
 * deliberate exception out.
 *
 * This is the static companion to the end-to-end run in
 * unity-runner-strictmode-smoke.test.js: the smoke test proves the live behavior
 * on one script; this guard prevents the *shape* from reappearing in ANY .ps1.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

// Suppression marker for a deliberate, reviewed exception on an assignment line.
const SUPPRESS_MARKER = "strictmode-collection-safety: ignore";

// True when the script enables StrictMode at a version where reading `.Count` /
// `.Length` of an empty capture throws: `Latest`, or `2.0` and above. On
// PowerShell 7 (the CI runtime, verified on pwsh 7.6.1) reading `.Count` /
// `.Length` throws under StrictMode 2.0 and every higher level; only `-Version
// 1.0` (or Off) avoids that throw (and under 1.0 the read returns the integer 0,
// not $null). (Indexing the empty capture throws "Cannot index into a null
// array" at EVERY level, but the version scope here is keyed to the throwing
// .Count/.Length read.) Windows PowerShell 5.1 does not start throwing on
// .Count/.Length until 3.0, but CI runs pwsh 7, so 2.0 and up are in scope.
function enablesThrowingStrictMode(text) {
  const re = /Set-StrictMode\s+-Version\s+(Latest|(\d+)(?:\.\d+)?)/gi;
  let match;
  while ((match = re.exec(text)) !== null) {
    if (/^latest$/i.test(match[1])) {
      return true;
    }
    const major = Number(match[2]);
    if (Number.isFinite(major) && major >= 2) {
      return true;
    }
  }
  return false;
}

// Names of functions DEFINED in this file: `function <Name> {` / `function <Name>(`.
// Only these are considered "local" so cmdlets are never flagged.
function localFunctionNames(text) {
  const names = new Set();
  const re = /^\s*function\s+([A-Za-z_][\w-]*)\b/gim;
  let match;
  while ((match = re.exec(text)) !== null) {
    names.add(match[1]);
  }
  return names;
}

// Walk a single line tracking single/double-quoted-string state. Returns a copy
// with the INTERIOR of every quoted string blanked to spaces and any trailing
// `# comment` (one that is NOT inside a string) removed. Quote and paren/name
// structure outside strings is preserved, so this stays precise for both
// assignment-shape classification and `.Count`/`.Length`/index read matching:
// a `.Count` that lives inside `"...$x.Count..."` becomes spaces and never
// matches. (Single-line scope only; here-strings are handled in stripCodeLines.)
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

// Produce a per-line "code" view of the whole file: lines that fall INSIDE a
// here-string body (`@'...'@` / `@"..."@`) are blanked entirely (their content
// is data, never executable reads), and every other line is run through codeOf
// to drop comments and quoted-string interiors. Returns an array aligned 1:1
// with the input lines. A here-string opens on a line whose last non-space token
// is `@'` or `@"` and closes on a line that begins (ignoring leading space) with
// `'@` or `"@`.
function stripCodeLines(lines) {
  const codes = new Array(lines.length);
  let hereTerminator = null; // "'@" or '"@' while inside a here-string body
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (hereTerminator !== null) {
      // Inside a here-string body: blank the line. The terminator line itself
      // is also data (and may carry a trailing close), so blank it too.
      const trimmed = line.replace(/^\s+/, "");
      codes[i] = "";
      if (trimmed.startsWith(hereTerminator)) {
        hereTerminator = null;
      }
      continue;
    }
    const opener = line.match(/@("|')\s*$/);
    if (opener) {
      // Line opens a here-string. Keep the part BEFORE `@"`/`@'` as code (so an
      // assignment target on the same line is still classified) and enter the
      // body. The interior never contributes reads.
      const head = line.slice(0, opener.index);
      codes[i] = codeOf(head);
      hereTerminator = opener[1] + "@";
      continue;
    }
    codes[i] = codeOf(line);
  }
  return codes;
}

/**
 * Scan a single .ps1 body. Returns an array of finding strings (empty when
 * clean). Only meaningful when enablesThrowingStrictMode(text) is true.
 */
function scanScript(relPath, text) {
  const findings = [];
  const locals = localFunctionNames(text);
  if (locals.size === 0) {
    return findings;
  }

  const lines = text.split(/\r?\n/);
  // Per-line code view: comments dropped, quoted-string interiors blanked, and
  // here-string bodies blanked. All matching below runs on this view so a
  // `.Count`/`.Length`/index that lives in a string is never a false positive.
  const codes = stripCodeLines(lines);

  // Pass 1: collect `$var = <LocalFunc> ...` assignments that are NOT wrapped in
  // @(...) and do NOT start with the unary array operator `,`.
  // varName -> { line (1-based), func }
  const bareCaptures = new Map();
  const assignRe = /^\s*\$([A-Za-z_]\w*)\s*=\s*(.+)$/;

  codes.forEach((code, index) => {
    const assign = code.match(assignRe);
    if (!assign) {
      return;
    }
    const varName = assign[1];
    const rhs = assign[2].trim();

    // Throw-safe at the read: wrapped in @(...) or a leading `,` both yield a
    // real array, so .Count/.Length/index never hit a bare $null/AutomationNull.
    // (@(...) is still the recommended fix -- a leading `,` yields Count 1 when
    // the call returned nothing, which @(...) correctly reports as 0.)
    if (rhs.startsWith("@(") || rhs.startsWith(",")) {
      return;
    }

    // The RHS must START with a call to a locally-defined function (optionally
    // via the call operator `& Func`). A pipeline or method call elsewhere on
    // the line does not qualify -- we only care about a DIRECT capture.
    const callMatch = rhs.match(/^&?\s*([A-Za-z_][\w-]*)\b/);
    if (!callMatch) {
      return;
    }
    const calledName = callMatch[1];
    if (!locals.has(calledName)) {
      return;
    }

    // Honor an inline suppression on the assignment line (check the raw line so
    // the marker text inside the comment is still seen).
    if (lines[index].includes(SUPPRESS_MARKER)) {
      return;
    }

    bareCaptures.set(varName, { line: index + 1, func: calledName });
  });

  if (bareCaptures.size === 0) {
    return findings;
  }

  // Pass 2: flag a bare capture whose `.Count` / `.Length` or indexing read
  // (`$x[0]`, `$x[-1]`, `$x[...]`) appears later in the file. Reading the
  // `.Count` / `.Length` property throws "property '...' cannot be found" under
  // StrictMode 2.0+; indexing throws "Cannot index into a null array" at every
  // StrictMode level. (This guard only runs on scripts at 2.0+, so either read
  // is a live crash there.)
  for (const [varName, info] of bareCaptures) {
    // `$var.Count` / `$var.Length` (property read), or `$var[` (index read).
    // The LHS of the assignment is `$var =` (space then `=`, never `.`/`[`), so
    // matching this on the assignment line cannot hit the target -- it only
    // catches the same-line `$x = Func; $x.Count` form, which is also a bug.
    const readRe = new RegExp(`\\$${varName}(?:\\.(Count|Length)\\b|(\\[))`);
    for (let i = 0; i < codes.length; i++) {
      const readMatch = codes[i].match(readRe);
      if (readMatch) {
        const access = readMatch[1] ? `$${varName}.${readMatch[1]}` : `$${varName}[...]`;
        const kind = readMatch[1] ? `${readMatch[1]} is read` : "indexed";
        findings.push(
          `${relPath}:${info.line}: $${varName} = ${info.func} (...) is captured WITHOUT @() ` +
            `wrapping, then ${access} (${kind}) at line ${i + 1} under StrictMode. ` +
            `Wrap the capture: $${varName} = @(${info.func} ...). ` +
            `(Suppress with "# ${SUPPRESS_MARKER}" on the assignment if intentional.)`
        );
        break;
      }
    }
  }

  return findings;
}

function listTrackedPs1() {
  const result = spawnSync("git", ["ls-files", "*.ps1"], {
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

const ALL_PS1 = listTrackedPs1();
const STRICT_PS1 = ALL_PS1.filter((rel) =>
  enablesThrowingStrictMode(fs.readFileSync(path.join(REPO_ROOT, rel), "utf8"))
);

describe("PowerShell StrictMode collection-safety static guard", () => {
  test("discovers at least one StrictMode-2.0+/Latest .ps1 to scan", () => {
    // A zero-script scan would silently validate nothing -- the false-green class
    // this whole change set exists to prevent.
    expect(STRICT_PS1.length).toBeGreaterThanOrEqual(1);
    // run-ci-tests.ps1 (the script the original bug lived in) must be in scope.
    expect(STRICT_PS1).toContain("scripts/unity/run-ci-tests.ps1");
  });

  test.each(STRICT_PS1)("%s is free of unwrapped local-function .Count/.Length/index captures", (rel) => {
    const text = fs.readFileSync(path.join(REPO_ROOT, rel), "utf8");
    const findings = scanScript(rel, text);
    expect(findings).toEqual([]);
  });

  // Detector self-tests: prove the guard FIRES on the dangerous shape and stays
  // SILENT on the safe @()-wrapped shape, so a future refactor cannot quietly
  // declaw it. These use tiny in-memory fixtures (no repo files touched).
  describe("detector behavior on fixtures", () => {
    const UNWRAPPED_FIXTURE = [
      "Set-StrictMode -Version Latest",
      "function Get-Args {",
      "    if (-not $x) { return @() }",
      "    return @('-a', '-b')",
      "}",
      "$myArgs = Get-Args",
      "if ($myArgs.Count -gt 0) { Write-Host 'has args' }",
      ""
    ].join("\n");

    const WRAPPED_FIXTURE = [
      "Set-StrictMode -Version Latest",
      "function Get-Args {",
      "    if (-not $x) { return @() }",
      "    return @('-a', '-b')",
      "}",
      "$myArgs = @(Get-Args)",
      "if ($myArgs.Count -gt 0) { Write-Host 'has args' }",
      ""
    ].join("\n");

    const CALL_OPERATOR_FIXTURE = [
      "Set-StrictMode -Version Latest",
      "function Get-Args { return @() }",
      "$myArgs = & Get-Args",
      "Write-Host $myArgs.Length",
      ""
    ].join("\n");

    const SUPPRESSED_FIXTURE = [
      "Set-StrictMode -Version Latest",
      "function Get-Args { return @() }",
      "$myArgs = Get-Args # strictmode-collection-safety: ignore",
      "if ($myArgs.Count -gt 0) { Write-Host 'x' }",
      ""
    ].join("\n");

    const CMDLET_FIXTURE = [
      "Set-StrictMode -Version Latest",
      "$matches = [regex]::Matches($s, $p)",
      "Write-Host $matches.Count",
      "$files = Get-ChildItem",
      "Write-Host $files.Length",
      ""
    ].join("\n");

    const STRICTMODE_V2_FIXTURE = [
      "Set-StrictMode -Version 2.0",
      "function Get-Args { return @() }",
      "$myArgs = Get-Args",
      "if ($myArgs.Count -gt 0) { Write-Host 'x' }",
      ""
    ].join("\n");

    const STRICTMODE_OFF_FIXTURE = [
      "Set-StrictMode -Version 1.0",
      "function Get-Args { return @() }",
      "$myArgs = Get-Args",
      "if ($myArgs.Count -gt 0) { Write-Host 'x' }",
      ""
    ].join("\n");

    // Indexing a bare capture also crashes ($null[0] -> "Cannot index into a
    // null array"), so it is in the same 0/1/many category.
    const INDEX_UNWRAPPED_FIXTURE = [
      "Set-StrictMode -Version Latest",
      "function Get-Args { return @() }",
      "$myArgs = Get-Args",
      "Write-Host $myArgs[0]",
      ""
    ].join("\n");

    const INDEX_WRAPPED_FIXTURE = [
      "Set-StrictMode -Version Latest",
      "function Get-Args { return @() }",
      "$myArgs = @(Get-Args)",
      "Write-Host $myArgs[-1]",
      ""
    ].join("\n");

    // Same-line `$x = Func; $x.Count` form -- still a bug.
    const SAME_LINE_FIXTURE = [
      "Set-StrictMode -Version Latest",
      "function Get-Args { return @() }",
      "$myArgs = Get-Args; if ($myArgs.Count -gt 0) { Write-Host 'x' }",
      ""
    ].join("\n");

    // The `.Count` here lives ONLY inside a double-quoted string -- not a real
    // property read, so the guard must stay silent (no false positive).
    const QUOTED_STRING_FIXTURE = [
      "Set-StrictMode -Version Latest",
      "function Get-Args { return @() }",
      "$myArgs = Get-Args",
      'Write-Host "the $myArgs.Count value is informational"',
      ""
    ].join("\n");

    // The `.Length` / index text here lives ONLY inside a here-string body --
    // data, not a read, so the guard must stay silent (no false positive).
    const HERE_STRING_FIXTURE = [
      "Set-StrictMode -Version Latest",
      "function Get-Args { return @() }",
      "$myArgs = Get-Args",
      "$doc = @'",
      "this mentions $myArgs.Length and $myArgs[0] as documentation only",
      "'@",
      "Write-Host $doc",
      ""
    ].join("\n");

    test("FIRES on a bare local-function capture read via .Count", () => {
      expect(enablesThrowingStrictMode(UNWRAPPED_FIXTURE)).toBe(true);
      const findings = scanScript("fixture.ps1", UNWRAPPED_FIXTURE);
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("$myArgs = Get-Args");
      expect(findings[0]).toContain("WITHOUT @()");
    });

    test("does NOT fire on the @()-wrapped form", () => {
      expect(scanScript("fixture.ps1", WRAPPED_FIXTURE)).toEqual([]);
    });

    test("FIRES on `& Func` capture read via .Length", () => {
      const findings = scanScript("fixture.ps1", CALL_OPERATOR_FIXTURE);
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain(".Length");
    });

    test("respects the inline suppression marker", () => {
      expect(scanScript("fixture.ps1", SUPPRESSED_FIXTURE)).toEqual([]);
    });

    test("does NOT fire on guaranteed-collection cmdlet results", () => {
      // [regex]::Matches and Get-ChildItem are not locally-defined functions, so
      // their .Count/.Length must never be flagged.
      expect(scanScript("fixture.ps1", CMDLET_FIXTURE)).toEqual([]);
    });

    test("FIRES on a bare capture indexed via $x[0]", () => {
      const findings = scanScript("fixture.ps1", INDEX_UNWRAPPED_FIXTURE);
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("$myArgs = Get-Args");
      expect(findings[0]).toContain("indexed");
    });

    test("does NOT fire when the indexed capture is @()-wrapped", () => {
      expect(scanScript("fixture.ps1", INDEX_WRAPPED_FIXTURE)).toEqual([]);
    });

    test("FIRES on the same-line `$x = Func; $x.Count` form", () => {
      const findings = scanScript("fixture.ps1", SAME_LINE_FIXTURE);
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("$myArgs = Get-Args");
    });

    test("does NOT false-positive on .Count inside a double-quoted string", () => {
      expect(scanScript("fixture.ps1", QUOTED_STRING_FIXTURE)).toEqual([]);
    });

    test("does NOT false-positive on .Length/index inside a here-string", () => {
      expect(scanScript("fixture.ps1", HERE_STRING_FIXTURE)).toEqual([]);
    });

    test("treats Set-StrictMode -Version 2.0 as IN scope and flags the bug", () => {
      // On pwsh 7 (CI runtime) $null.Count throws under StrictMode 2.0, so a
      // bare 2.0 capture is in scope and must be flagged.
      expect(enablesThrowingStrictMode(STRICTMODE_V2_FIXTURE)).toBe(true);
      const findings = scanScript("fixture.ps1", STRICTMODE_V2_FIXTURE);
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("$myArgs = Get-Args");
    });

    test("treats Set-StrictMode -Version 1.0 as out of scope", () => {
      // Under -Version 1.0 reading .Count/.Length of an empty capture returns the
      // integer 0 (the synthetic count), not $null, and does not throw -- so the
      // .Count/.Length bug shape is not in scope at 1.0. (Indexing still throws at
      // every level, but this guard's scope key is the throwing .Count/.Length
      // read.)
      expect(enablesThrowingStrictMode(STRICTMODE_OFF_FIXTURE)).toBe(false);
    });
  });
});
