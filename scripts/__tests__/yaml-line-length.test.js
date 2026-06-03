"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const childProcess = require("child_process");

// `yaml` is a transitive dependency only; we use it INSIDE TESTS to assert that
// a rewrite (or a left-untouched line) still parses as YAML. Shipping code must
// never depend on it (the lib hand-rolls its parsing).
const YAML = require("yaml");

const {
  DEFAULT_MAX_LINE_LENGTH,
  resolveYamlLineLengthPolicy,
  checkInlineMapping,
  findLineLengthViolations,
  findPowerShellSplitSpaces,
  parsePowerShellStringLine,
  rewritePowerShellStringLine,
  isBlockScalarOpenerFor,
  findPwshRunBlockScalarLines,
  rewriteYamlBlockScalarLines,
  BLOCK_SCALAR_REMEDIATION
} = require("../lib/yaml-line-length");

const {
  processFiles,
  parseArgs,
  uniqueExistingYamlFiles
} = require("../fix-yaml-block-scalar-line-length");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const YAMLLINT_CONFIG = path.join(REPO_ROOT, ".yamllint.yaml");
const ACTION_PATH = path.join(
  REPO_ROOT,
  ".github",
  "actions",
  "print-self-hosted-runner-diagnostics",
  "action.yml"
);

// The exact original (pre-fix) line 41 content of the action. The fix splits
// this into a multi-line concatenation; the regression test below proves that
// split reconstructs this byte-for-byte.
const ACTION_LINE_41_ORIGINAL =
  '            Write-Output "::error title=pwsh missing on self-hosted runner::' +
  "PowerShell 7 (pwsh) is not installed on runner '$env:RUNNER_NAME'. Install it " +
  "before queueing Unity jobs -- see docs/runbooks/unity-runners-after-transfer.md " +
  '(PowerShell 7 prerequisite)."';

function listTrackedYamlFiles() {
  const output = childProcess.execFileSync("git", ["ls-files", "--", "*.yml", "*.yaml"], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });
  return output
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

function yamllintAvailable() {
  try {
    const result = childProcess.spawnSync("yamllint", ["--version"], { stdio: "ignore" });
    return result.status === 0;
  } catch {
    return false;
  }
}

// The corpus parity gate enumerates tracked YAML via `git ls-files`, which only
// works inside a real git checkout. Detached materializations (e.g. a
// `git archive` extract, or a sandbox without `.git`) have no repository, so we
// gate on git availability too -- skip gracefully rather than crash the suite.
function gitTrackingAvailable() {
  try {
    const result = childProcess.spawnSync("git", ["rev-parse", "--is-inside-work-tree"], {
      cwd: REPO_ROOT,
      stdio: "ignore"
    });
    return result.status === 0;
  } catch {
    return false;
  }
}

const HAS_YAMLLINT = yamllintAvailable();
const HAS_GIT_CORPUS = gitTrackingAvailable();

/**
 * Strip a rendered concatenation line back to its raw string-literal piece so a
 * test can reconstruct the original literal and assert exact equality.
 */
function pieceFromRenderedLine(line, isFirst, isLast) {
  let inner = line.replace(/^\s+/, "");
  if (isFirst) {
    // `<prefix>("piece" +`  -> drop prefix up to `("` and the trailing `" +`.
    inner = inner.replace(/^[^"]*\("/, "");
  } else {
    inner = inner.replace(/^"/, "");
  }
  if (isLast) {
    inner = inner.replace(/"\)$/, "");
  } else {
    inner = inner.replace(/" \+$/, "");
  }
  return inner;
}

function reconstructBody(renderedLines) {
  return renderedLines
    .map((line, index) =>
      pieceFromRenderedLine(line, index === 0, index === renderedLines.length - 1)
    )
    .join("");
}

describe("yaml-line-length policy resolution", () => {
  test("resolves the repo policy from .yamllint.yaml (single source of truth)", () => {
    const policy = resolveYamlLineLengthPolicy(YAMLLINT_CONFIG);
    expect(policy.max).toBe(200);
    expect(policy.allowNonBreakableWords).toBe(true);
    expect(policy.allowNonBreakableInlineMappings).toBe(true);
  });

  test("falls back to defaults when the config is missing", () => {
    const policy = resolveYamlLineLengthPolicy(path.join(os.tmpdir(), "does-not-exist.yaml"));
    expect(policy.max).toBe(DEFAULT_MAX_LINE_LENGTH);
    expect(policy.allowNonBreakableWords).toBe(true);
    expect(policy.allowNonBreakableInlineMappings).toBe(false);
  });
});

describe("findLineLengthViolations (faithful yamllint port)", () => {
  const policy = { max: 200, allowNonBreakableWords: true, allowNonBreakableInlineMappings: true };

  test("reports column max+1 and the code-point length", () => {
    const long = `key: ${"a ".repeat(150)}`; // breakable, well over 200
    const violations = findLineLengthViolations(long, policy);
    expect(violations).toHaveLength(1);
    expect(violations[0].line).toBe(1);
    expect(violations[0].column).toBe(201);
    expect(violations[0].length).toBe(Array.from(long).length);
  });

  test("exempts a long non-breakable inline mapping (files: '<regex>')", () => {
    const line = `        files: '^(${"x".repeat(250)})$'`;
    expect(findLineLengthViolations(line, policy)).toHaveLength(0);
  });

  test("exempts a long single non-breakable word", () => {
    const line = `          ^(${"a".repeat(250)})$`;
    expect(findLineLengthViolations(line, policy)).toHaveLength(0);
  });

  test("exempts a long non-breakable sequence-item value (yamllint `- ` dash-skip)", () => {
    // A sequence item whose value is a single non-breakable token > max. yamllint
    // skips the leading `- ` before scanning for an interior space, so the token
    // counts as non-breakable and the line is exempt. This PINS the dash-skip
    // branch in findLineLengthViolations: deleting it would make this a finding.
    const content = `items:\n  - ${"a".repeat(251)}\n`;
    expect(findLineLengthViolations(content, policy)).toHaveLength(0);
    if (HAS_YAMLLINT) {
      const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "yaml-seq-dash-"));
      try {
        const target = path.join(tempDir, "seq.yaml");
        fs.writeFileSync(target, content, "utf8");
        const lint = childProcess.spawnSync("yamllint", ["-c", YAMLLINT_CONFIG, target], {
          cwd: REPO_ROOT,
          encoding: "utf8"
        });
        // Real yamllint agrees: the exempt sequence-item line is not a finding.
        expect(lint.status).toBe(0);
      } finally {
        fs.rmSync(tempDir, { recursive: true, force: true });
      }
    }
  });

  test("flags a long breakable line", () => {
    const line = `        note: ${"word ".repeat(60)}end`;
    expect(findLineLengthViolations(line, policy)).toHaveLength(1);
  });

  test("counts astral characters as single code points like yamllint", () => {
    // A breakable line (the value has a space) whose length is measured in code
    // points: 1 astral char counts as one, like yamllint. Build a line that is
    // exactly 200 code points (not a violation) and one that is 201.
    const head = "note: a "; // 8 code points, includes a space -> breakable
    const fitLine = head + "\u{1F600}".repeat(192); // 8 + 192 = 200 code points
    expect(Array.from(fitLine).length).toBe(200);
    expect(findLineLengthViolations(fitLine, policy)).toHaveLength(0);
    const overLine = head + "\u{1F600}".repeat(193); // 201 code points
    expect(Array.from(overLine).length).toBe(201);
    expect(findLineLengthViolations(overLine, policy)).toHaveLength(1);
  });
});

describe("checkInlineMapping (port of yamllint check_inline_mapping)", () => {
  const cases = [
    ["        files: '^(a|b)$'", true],
    ["        files: '^(a b)$'", false],
    ["  key: value", true],
    ["  key: nospace", true],
    ["  key:value", false],
    ["  key:  value", true],
    ["  key:\tvalue", false],
    ["key: value", true],
    ["  - key: nospace", true],
    ["  - key: has space", false],
    ["  # key: value", false],
    ["  - item", false],
    ["  http://x: y", true],
    ["  'quoted: key': value", true],
    ["  key: value: more", false],
    ["  key: a#b", true],
    ["  outer:", false],
    ["  key: 'a''b'", true],
    ["  key: &a value", false],
    ["  key: &a nospace", false],
    ["  key: !tag value", false],
    ["  key: *ref", false],
    ["  key: {x: y}", true],
    ["  key: {x: y z}", false],
    ["  key: [a, b]", false]
  ];

  test.each(cases)("checkInlineMapping(%j) === %s", (line, expected) => {
    expect(checkInlineMapping(line)).toBe(expected);
  });
});

describe("findPowerShellSplitSpaces", () => {
  test("returns only depth-0 literal spaces", () => {
    expect(findPowerShellSplitSpaces("a b c")).toEqual([1, 3]);
  });

  test("never splits inside $(...) subexpressions", () => {
    const body = "x $((Get-Command pwsh).Source) y";
    const idx = findPowerShellSplitSpaces(body);
    // Only the two plain spaces around the subexpression are split points.
    expect(idx).toEqual([1, body.length - 2]);
  });

  test("never splits inside ${...} spans", () => {
    const body = "x ${env:My Var} y";
    const idx = findPowerShellSplitSpaces(body);
    expect(idx).toEqual([1, body.length - 2]);
  });

  test("does not treat a backtick-escaped space as a split point", () => {
    const body = "a` b c";
    // index 1 is the backtick (escapes the space at 2); only the space at 4 is a
    // safe split point.
    expect(findPowerShellSplitSpaces(body)).toEqual([4]);
  });

  test("keeps interpolation tokens like $env:RUNNER_NAME intact", () => {
    const body = "runner $env:RUNNER_NAME here";
    const idx = findPowerShellSplitSpaces(body);
    // $env:RUNNER_NAME has no internal spaces, so it is never split.
    for (const i of idx) {
      expect(body[i]).toBe(" ");
    }
    expect(body.slice(idx[0] + 1, idx[1])).toBe("$env:RUNNER_NAME");
  });
});

describe("parsePowerShellStringLine", () => {
  test("parses the safe Write-Output shape", () => {
    const parsed = parsePowerShellStringLine('    Write-Output "hello world"');
    expect(parsed).toEqual({ indent: "    ", prefix: "Write-Output ", body: "hello world" });
  });

  test("rejects lines with no prefix before the quote", () => {
    expect(parsePowerShellStringLine('    "no prefix"')).toBeNull();
  });

  test("rejects lines with content after the closing quote", () => {
    expect(parsePowerShellStringLine('    Write-Output "x" + $y')).toBeNull();
  });

  test("rejects single-quoted content (ambiguous quoting)", () => {
    expect(parsePowerShellStringLine("    Write-Output 'x'")).toBeNull();
  });
});

describe("rewritePowerShellStringLine (provably semantics-preserving)", () => {
  const max = 80;

  test("rewrites a long Write-Output literal preserving the exact string", () => {
    const body =
      "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi";
    const line = `        Write-Output "${body}"`;
    const result = rewritePowerShellStringLine(line, max);
    expect(result.status).toBe("rewritten");
    expect(result.lines.length).toBeGreaterThan(1);
    for (const renderedLine of result.lines) {
      expect(Array.from(renderedLine).length).toBeLessThanOrEqual(max);
    }
    // Semantics preserved: reconstructed body equals original exactly.
    expect(reconstructBody(result.lines)).toBe(body);
    // Indentation preserved on every produced line.
    for (const renderedLine of result.lines) {
      expect(renderedLine.startsWith("        ")).toBe(true);
    }
    // First line keeps the prefix and opening paren; last closes the paren.
    expect(result.lines[0]).toContain('Write-Output ("');
    expect(result.lines[result.lines.length - 1].endsWith('")')).toBe(true);
  });

  test("preserves interpolation tokens across the split", () => {
    const body =
      "this is a very long diagnostic line about runner '$env:RUNNER_NAME' and more text here too ok";
    const line = `      Write-Output "${body}"`;
    const result = rewritePowerShellStringLine(line, max);
    expect(result.status).toBe("rewritten");
    expect(reconstructBody(result.lines)).toBe(body);
    // The interpolation token survives intact in exactly one piece.
    const joined = result.lines.join("\n");
    expect(joined).toContain("$env:RUNNER_NAME");
  });

  test("is idempotent: an already-fitting line is left unchanged", () => {
    const line = '    Write-Output "short enough"';
    expect(rewritePowerShellStringLine(line, max).status).toBe("unchanged");
  });

  test("refuses to rewrite a non-string-literal shape", () => {
    const line = `        if ($x -eq ${"y".repeat(200)}) { Do-Thing }`;
    const result = rewritePowerShellStringLine(line, max);
    expect(result.status).toBe("unsafe");
    expect(result.reason).toMatch(/not a single PowerShell double-quoted string/);
  });

  test("refuses when a single token is wider than the budget", () => {
    const body = `prefix ${"x".repeat(300)} suffix`;
    const line = `        Write-Output "${body}"`;
    const result = rewritePowerShellStringLine(line, max);
    expect(result.status).toBe("unsafe");
    expect(result.reason).toMatch(/non-breakable token/);
  });

  test("running the rewrite again on its output is a no-op (idempotent)", () => {
    const body =
      "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho";
    const line = `        Write-Output "${body}"`;
    const first = rewritePowerShellStringLine(line, max);
    expect(first.status).toBe("rewritten");
    // Each produced line individually fits, so re-running yields unchanged.
    for (const renderedLine of first.lines) {
      expect(rewritePowerShellStringLine(renderedLine, max).status).toBe("unchanged");
    }
  });
});

describe("rewriteYamlBlockScalarLines", () => {
  const policy = { max: 200, allowNonBreakableWords: true, allowNonBreakableInlineMappings: true };

  test("rewrites only over-length safe lines and leaves the rest untouched", () => {
    const body = `::error ::${"word ".repeat(60)}done`;
    // B1: a `run:` block scalar is only rewritable when the step has an explicit
    // pwsh/powershell shell. Provide it so the safe line is eligible.
    const content = [
      "runs:",
      "  steps:",
      "    - shell: pwsh",
      "      run: |",
      `        Write-Output "${body}"`,
      '        Write-Output "short"'
    ].join("\n");
    const result = rewriteYamlBlockScalarLines(content, policy);
    expect(result.changedLines).toEqual([5]);
    expect(result.unsafe).toHaveLength(0);
    const outLines = result.content.split("\n");
    for (const line of outLines) {
      expect(Array.from(line).length).toBeLessThanOrEqual(policy.max);
    }
  });

  test("reports unsafe over-length lines in a pwsh run block without modifying them", () => {
    const body = `prefix${"x".repeat(260)}`; // single non-breakable token, but breakable region exists
    const content = [
      "runs:",
      "  steps:",
      "    - shell: pwsh",
      "      run: |",
      `        $x = "value with a space ${body}"`
    ].join("\n");
    const result = rewriteYamlBlockScalarLines(content, policy);
    // The line is over-length and has a space (breakable region) so it is a real
    // violation, AND it sits in an eligible pwsh run block, but the giant token
    // cannot be split safely -> unsafe, untouched.
    expect(result.changedLines).toHaveLength(0);
    expect(result.unsafe).toHaveLength(1);
    expect(result.unsafe[0].line).toBe(5);
    expect(result.content).toBe(content);
  });
});

describe("regression: action.yml line 41 fix", () => {
  test("the original line 41 splits into <=200 lines that reconstruct the literal", () => {
    const result = rewritePowerShellStringLine(ACTION_LINE_41_ORIGINAL, 200);
    expect(result.status).toBe("rewritten");
    for (const renderedLine of result.lines) {
      expect(Array.from(renderedLine).length).toBeLessThanOrEqual(200);
    }
    // Reconstructed double-quoted body equals the original literal body exactly.
    const original = parsePowerShellStringLine(ACTION_LINE_41_ORIGINAL);
    expect(original).not.toBeNull();
    expect(reconstructBody(result.lines)).toBe(original.body);
    // The runtime-critical interpolation + annotation prefix survive.
    const joined = result.lines.join("\n");
    expect(joined).toContain("$env:RUNNER_NAME");
    expect(joined).toContain("::error title=pwsh missing on self-hosted runner::");
  });

  test("the committed action.yml passes the Node violation checker", () => {
    const policy = resolveYamlLineLengthPolicy(YAMLLINT_CONFIG);
    const content = fs.readFileSync(ACTION_PATH, "utf8");
    expect(findLineLengthViolations(content, policy)).toHaveLength(0);
  });

  test("BLOCK_SCALAR_REMEDIATION points authors at the externalization skill", () => {
    expect(BLOCK_SCALAR_REMEDIATION).toMatch(/\.llm\/skills\/github-actions\//);
  });
});

describe("fix-yaml-block-scalar-line-length CLI helpers", () => {
  test("parseArgs parses flags and files", () => {
    expect(parseArgs(["--check", "--all-files", "a.yml", "b.yaml", "x.md"])).toEqual({
      check: true,
      allFiles: true,
      files: ["a.yml", "b.yaml", "x.md"]
    });
  });

  test("uniqueExistingYamlFiles filters non-yaml and missing files", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "yaml-block-filter-"));
    try {
      const yamlFile = path.join(tempDir, "a.yaml");
      fs.writeFileSync(yamlFile, "key: value\n", "utf8");
      fs.writeFileSync(path.join(tempDir, "README.md"), "# t\n", "utf8");
      expect(
        uniqueExistingYamlFiles([
          yamlFile,
          path.join(tempDir, "README.md"),
          path.join(tempDir, "missing.yml")
        ])
      ).toEqual([yamlFile]);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("processFiles check mode reports without writing; write mode rewrites", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "yaml-block-proc-"));
    try {
      fs.writeFileSync(
        path.join(tempDir, ".yamllint.yaml"),
        [
          "rules:",
          "  line-length:",
          "    max: 80",
          "    allow-non-breakable-words: true",
          "    allow-non-breakable-inline-mappings: true"
        ].join("\n"),
        "utf8"
      );
      const target = path.join(tempDir, "action.yml");
      const body =
        "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi";
      const original =
        [
          "runs:",
          "  using: composite",
          "  steps:",
          "    - shell: pwsh",
          "      run: |",
          `        Write-Output "${body}"`
        ].join("\n") + "\n";
      fs.writeFileSync(target, original, "utf8");

      const checkResult = processFiles([target], { check: true, repoRoot: tempDir });
      expect(checkResult.checkViolations).toHaveLength(1);
      expect(fs.readFileSync(target, "utf8")).toBe(original);

      const writeResult = processFiles([target], { check: false, repoRoot: tempDir });
      expect(writeResult.changedFiles).toHaveLength(1);
      const written = fs.readFileSync(target, "utf8");
      for (const line of written.split("\n")) {
        expect(Array.from(line).length).toBeLessThanOrEqual(80);
      }
      // Idempotence: a second write pass changes nothing.
      const secondPass = processFiles([target], { check: false, repoRoot: tempDir });
      expect(secondPass.changedFiles).toHaveLength(0);
      expect(fs.readFileSync(target, "utf8")).toBe(written);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});

// ===========================================================================
// BLOCKER B1 regression suite: the rewriter must be CONTEXT AWARE. Only a
// content line inside a `run:` block scalar whose step has an explicit
// `shell: pwsh`/`shell: powershell` AND that is the provably-safe string-literal
// shape may be rewritten. Everything else stays BYTE-IDENTICAL (status unsafe),
// and the result must still parse as YAML.
// ===========================================================================

const B1_POLICY = {
  max: 200,
  allowNonBreakableWords: true,
  allowNonBreakableInlineMappings: true
};

// A long, fully-BREAKABLE body (spaces every few chars, no oversized token) so
// the ONLY reason a line is left untouched is the structural context gate, not
// an unbreakable-token refusal. ~340 chars.
const LONG_BREAKABLE = `start ${"word ".repeat(64)}end`;

function assertParses(yamlText) {
  // Throws on a YAML syntax error; the test fails loudly if a left-untouched or
  // rewritten document no longer parses.
  expect(() => YAML.parse(yamlText)).not.toThrow();
}

describe("B1 negative: over-length lines that must stay byte-identical (status unsafe)", () => {
  const negativeCases = [
    {
      name: "long bash `run: |` content line (step shell: bash)",
      content: [
        "runs:",
        "  steps:",
        "    - shell: bash",
        "      run: |",
        `        echo "${LONG_BREAKABLE}"`
      ].join("\n"),
      offendingLine: 5
    },
    {
      name: "long `run: |` content line in a step with NO explicit shell",
      content: ["runs:", "  steps:", "    - run: |", `        echo "${LONG_BREAKABLE}"`].join("\n"),
      offendingLine: 4
    },
    {
      name: "long folded `>-` scalar value (description:), NOT a run block",
      content: ["description: >-", `  See "${LONG_BREAKABLE}" for details`].join("\n"),
      offendingLine: 2
    },
    {
      name: "long plain mapping value line (name: <long quoted text>)",
      content: ["x:", `    name: "${LONG_BREAKABLE}"`].join("\n"),
      offendingLine: 2
    },
    {
      name: "long line inside a `with:` multi-line value (not a run block)",
      content: [
        "runs:",
        "  steps:",
        "    - shell: pwsh",
        "      with:",
        "        script: |",
        `          Write-Output "${LONG_BREAKABLE}"`
      ].join("\n"),
      offendingLine: 6
    }
  ];

  test.each(negativeCases)("$name -> unsafe, byte-identical, still parses", (testCase) => {
    // Sanity: the offending line really is an over-length violation.
    const violations = findLineLengthViolations(testCase.content, B1_POLICY);
    expect(violations.map((v) => v.line)).toContain(testCase.offendingLine);

    const result = rewriteYamlBlockScalarLines(testCase.content, B1_POLICY);
    expect(result.changedLines).toHaveLength(0);
    expect(result.content).toBe(testCase.content); // BYTE-IDENTICAL
    expect(result.unsafe.map((u) => u.line)).toContain(testCase.offendingLine);
    assertParses(result.content);
  });

  test("pwsh step but UNSAFE shape (embedded \"\") -> unsafe, untouched", () => {
    // A doubled double-quote inside the literal is not the provably-safe shape
    // (the parser would see the first `""` as a close+reopen ambiguity), so even
    // in an eligible pwsh run block it must be left alone.
    const body = `${"word ".repeat(40)}value ""quoted"" ${"word ".repeat(20)}done`;
    const content = [
      "runs:",
      "  steps:",
      "    - shell: pwsh",
      "      run: |",
      `        Write-Output "${body}"`
    ].join("\n");
    const result = rewriteYamlBlockScalarLines(content, B1_POLICY);
    expect(result.changedLines).toHaveLength(0);
    expect(result.content).toBe(content);
    expect(result.unsafe).toHaveLength(1);
    assertParses(result.content);
  });

  test("pwsh step but UNSAFE shape (oversized non-breakable token) -> unsafe, untouched", () => {
    const body = `prefix ${"x".repeat(300)} suffix`;
    const content = [
      "runs:",
      "  steps:",
      "    - shell: pwsh",
      "      run: |",
      `        Write-Output "${body}"`
    ].join("\n");
    const result = rewriteYamlBlockScalarLines(content, B1_POLICY);
    expect(result.changedLines).toHaveLength(0);
    expect(result.content).toBe(content);
    expect(result.unsafe).toHaveLength(1);
    expect(result.unsafe[0].reason).toMatch(/non-breakable token/);
    assertParses(result.content);
  });

  test("pwsh step but UNSAFE shape (unbalanced `$(`) -> unsafe, untouched", () => {
    // An unterminated subexpression makes the whole literal one non-breakable
    // span (no depth-0 split spaces), so no safe split fits.
    const body = `lead ${"word ".repeat(40)}$(Get-Command pwsh ${"more ".repeat(20)}tail`;
    const content = [
      "runs:",
      "  steps:",
      "    - shell: pwsh",
      "      run: |",
      `        Write-Output "${body}"`
    ].join("\n");
    const result = rewriteYamlBlockScalarLines(content, B1_POLICY);
    expect(result.changedLines).toHaveLength(0);
    expect(result.content).toBe(content);
    expect(result.unsafe).toHaveLength(1);
    assertParses(result.content);
  });
});

describe("B1 positive: pwsh/powershell run blocks that MUST rewrite", () => {
  const positiveCases = [
    {
      name: "shell: pwsh, Write-Output, shell BEFORE run",
      content: [
        "runs:",
        "  steps:",
        "    - shell: pwsh",
        "      run: |",
        `        Write-Output "${LONG_BREAKABLE}"`
      ].join("\n"),
      offendingLine: 5
    },
    {
      name: "shell: powershell (real action.yml case), shell BEFORE run",
      content: [
        "runs:",
        "  using: composite",
        "  steps:",
        "    - name: Verify",
        "      shell: powershell",
        "      run: |",
        `        Write-Output "${LONG_BREAKABLE}"`
      ].join("\n"),
      offendingLine: 7
    },
    {
      name: "shell: powershell, shell AFTER run (association within the step)",
      content: [
        "runs:",
        "  steps:",
        "    - run: |",
        `        Write-Output "${LONG_BREAKABLE}"`,
        "      shell: powershell",
        "      name: Verify"
      ].join("\n"),
      offendingLine: 4
    },
    {
      name: "composite-action shape (using: composite, steps: [...])",
      content: [
        "runs:",
        "  using: composite",
        "  steps:",
        "    - shell: pwsh",
        "      run: |",
        `        Write-Output "${LONG_BREAKABLE}"`
      ].join("\n"),
      offendingLine: 6
    }
  ];

  test.each(positiveCases)("$name -> rewritten, <=max, still valid YAML", (testCase) => {
    const result = rewriteYamlBlockScalarLines(testCase.content, B1_POLICY);
    expect(result.changedLines).toEqual([testCase.offendingLine]);
    expect(result.unsafe).toHaveLength(0);
    for (const line of result.content.split("\n")) {
      expect(Array.from(line).length).toBeLessThanOrEqual(B1_POLICY.max);
    }
    // Still valid YAML.
    assertParses(result.content);
    // The rewritten content must form a single-expression PowerShell
    // concatenation: the opening piece carries `Write-Output ("`.
    expect(result.content).toContain('Write-Output ("');
    // Reconstruct the original literal body from the rendered pieces and assert
    // byte-identity (no character added or removed).
    const originalLine = testCase.content.split("\n")[testCase.offendingLine - 1];
    const originalParsed = parsePowerShellStringLine(originalLine);
    expect(originalParsed).not.toBeNull();
    const renderedRun = result.content
      .split("\n")
      .filter((l) => l.includes("Write-Output") || /^\s*"/.test(l));
    expect(reconstructBody(renderedRun)).toBe(originalParsed.body);
  });

  test("interpolation tokens ($env:X and $(...)) are preserved across the split", () => {
    const body = `runner '$env:RUNNER_NAME' at $((Get-Command pwsh).Source) ${"detail ".repeat(30)}end`;
    const content = [
      "runs:",
      "  steps:",
      "    - shell: pwsh",
      "      run: |",
      `        Write-Output "${body}"`
    ].join("\n");
    const result = rewriteYamlBlockScalarLines(content, B1_POLICY);
    expect(result.changedLines).toEqual([5]);
    const joined = result.content;
    expect(joined).toContain("$env:RUNNER_NAME");
    expect(joined).toContain("$((Get-Command pwsh).Source)");
    assertParses(result.content);
  });

  test("indentation is preserved on every produced line", () => {
    const content = [
      "runs:",
      "  steps:",
      "    - shell: pwsh",
      "      run: |",
      `            Write-Output "${LONG_BREAKABLE}"`
    ].join("\n");
    const result = rewriteYamlBlockScalarLines(content, B1_POLICY);
    expect(result.changedLines).toEqual([5]);
    const produced = result.content.split("\n").slice(4);
    for (const line of produced) {
      expect(line.startsWith("            ")).toBe(true);
    }
    assertParses(result.content);
  });

  test("idempotence: a second rewrite pass is a no-op", () => {
    const content =
      [
        "runs:",
        "  steps:",
        "    - shell: pwsh",
        "      run: |",
        `        Write-Output "${LONG_BREAKABLE}"`
      ].join("\n") + "\n";
    const first = rewriteYamlBlockScalarLines(content, B1_POLICY);
    expect(first.changedLines).toHaveLength(1);
    const second = rewriteYamlBlockScalarLines(first.content, B1_POLICY);
    expect(second.changedLines).toHaveLength(0);
    expect(second.content).toBe(first.content);
    assertParses(first.content);
  });
});

describe("findPwshRunBlockScalarLines (structural context detection)", () => {
  test("body lines of a pwsh run block are eligible; nothing else is", () => {
    const content = [
      "runs:", // 1
      "  steps:", // 2
      "    - shell: pwsh", // 3
      "      run: |", // 4
      "        line one", // 5  eligible
      "        line two", // 6  eligible
      "    - shell: bash", // 7
      "      run: |", // 8
      "        not eligible" // 9
    ].join("\n");
    const eligible = findPwshRunBlockScalarLines(content);
    expect([...eligible].sort((a, b) => a - b)).toEqual([5, 6]);
  });

  test("shell AFTER run is still associated within the same step", () => {
    const content = [
      "steps:", // 1
      "  - run: |", // 2
      "      hello world", // 3 eligible (shell resolved later)
      "    shell: pwsh" // 4
    ].join("\n");
    const eligible = findPwshRunBlockScalarLines(content);
    expect([...eligible]).toEqual([3]);
  });

  test("a run block in a NON-pwsh step yields no eligible lines", () => {
    const content = ["steps:", "  - shell: bash", "    run: |", "      echo hi"].join("\n");
    expect(findPwshRunBlockScalarLines(content).size).toBe(0);
  });

  test("a top-level run (no step, no shell) is never eligible", () => {
    const content = ["run: |", "  echo hi", "  echo bye"].join("\n");
    expect(findPwshRunBlockScalarLines(content).size).toBe(0);
  });

  test("body lines never collide with sibling `- ` markers or keys", () => {
    // A body line that itself starts with `- ` (deeper than the run key) is body,
    // not a new step marker.
    const content = [
      "steps:", // 1
      "  - shell: pwsh", // 2
      "    run: |", // 3
      "      - item-like body line", // 4 eligible body, not a step
      "      more body", // 5 eligible
      "  - shell: bash", // 6 next step
      "    run: |", // 7
      "      other" // 8 not eligible
    ].join("\n");
    const eligible = findPwshRunBlockScalarLines(content);
    expect([...eligible].sort((a, b) => a - b)).toEqual([4, 5]);
  });

  test("various block-scalar indicators open a run body (>, |-, |+, |2, >- # c)", () => {
    // Valid YAML block-scalar headers: `|`/`>` with an OPTIONAL single-digit
    // indentation indicator and an OPTIONAL chomping indicator, in EITHER order,
    // at most one of each.
    const validIndicators = [
      "|",
      ">",
      "|-",
      "|+",
      ">-",
      ">+",
      "|2",
      "|2-", // digit then chomp
      "|-2", // chomp then digit
      "| # keep"
    ];
    for (const indicator of validIndicators) {
      expect(isBlockScalarOpenerFor(`run: ${indicator}`, "run")).toBe(true);
      expect(isBlockScalarOpenerFor(`- run: ${indicator}`, "run")).toBe(true);
    }
    // An INVALID header (two indentation digits) is NOT a clean opener.
    expect(isBlockScalarOpenerFor("run: |2-3", "run")).toBe(false);
    // A non-block-scalar `run:` value is NOT an opener.
    expect(isBlockScalarOpenerFor('run: "echo hi"', "run")).toBe(false);
    expect(isBlockScalarOpenerFor("run: echo hi", "run")).toBe(false);
    expect(isBlockScalarOpenerFor("run:", "run")).toBe(false);
  });

  test("only `run:` block scalars are rewrite targets; other keys' openers are not", () => {
    // `isBlockScalarOpenerFor` is key-parameterized: a block-scalar opener on a
    // NON-`run` key (`script:`, `value:`) is an opener for THAT key but never for
    // `run`. This pins that the structural detector keys off `run:` alone.
    for (const key of ["script", "value"]) {
      expect(isBlockScalarOpenerFor(`${key}: |`, key)).toBe(true);
      expect(isBlockScalarOpenerFor(`${key}: |`, "run")).toBe(false);
    }

    // End-to-end: a long pwsh-shaped string under a non-`run` block scalar
    // (`with:` -> `value: |`) inside a pwsh step yields NO eligible lines and is
    // left BYTE-IDENTICAL (status unsafe) -- only `run:` bodies are rewritable.
    const content = [
      "runs:",
      "  steps:",
      "    - shell: pwsh",
      "      with:",
      "        value: |",
      `          Write-Output "${LONG_BREAKABLE}"`
    ].join("\n");
    const offendingLine = 6;
    // Sanity: the pwsh-shaped line really is an over-length violation.
    expect(findLineLengthViolations(content, B1_POLICY).map((v) => v.line)).toContain(offendingLine);
    expect(findPwshRunBlockScalarLines(content).size).toBe(0);
    const result = rewriteYamlBlockScalarLines(content, B1_POLICY);
    expect(result.changedLines).toHaveLength(0);
    expect(result.content).toBe(content); // BYTE-IDENTICAL
    expect(result.unsafe.map((u) => u.line)).toContain(offendingLine);
    assertParses(result.content);
  });

  test("shell value matching is case-insensitive and tolerates quotes", () => {
    for (const shell of ["pwsh", "PWSH", "PowerShell", '"pwsh"', "'powershell'"]) {
      const content = ["steps:", `  - shell: ${shell}`, "    run: |", "      body line"].join("\n");
      expect(findPwshRunBlockScalarLines(content).has(4)).toBe(true);
    }
    // GitHub default shells must NOT be inferred.
    for (const shell of ["sh", "bash", "cmd", "python"]) {
      const content = ["steps:", `  - shell: ${shell}`, "    run: |", "      body line"].join("\n");
      expect(findPwshRunBlockScalarLines(content).size).toBe(0);
    }
  });
});

// ===========================================================================
// M1/M2: parity CONTRACT boundary on syntactically-INVALID YAML. These fixtures
// PIN the documented expected-divergence on invalid YAML (the Node port is exact
// only for VALID YAML; real yamllint suppresses line-length on a syntax error
// and remains the authoritative final gate).
// ===========================================================================
describe("findLineLengthViolations: invalid-YAML divergence boundary (documented)", () => {
  const policy = { max: 200, allowNonBreakableWords: true, allowNonBreakableInlineMappings: true };

  test("known FALSE-NEGATIVE: unbalanced double quote, >200 chars, breakable", () => {
    // Real yamllint raises a ScannerError on the unterminated quoted scalar and
    // suppresses line-length. The hand-rolled inline-mapping scanner treats the
    // value as a non-breakable inline mapping (no space after the value start)
    // and ALSO exempts it -> here both agree on "no finding", but for DIFFERENT
    // reasons. We pin that this stays a non-finding so the boundary is explicit.
    const line = `key: "${"a".repeat(250)}`; // unterminated quote, no internal space
    expect(YAML.parse.bind(null, line)).toThrow(); // genuinely invalid YAML
    expect(findLineLengthViolations(line, policy)).toHaveLength(0);
  });

  test("known FALSE-POSITIVE: `key: |vvv...` (bad block-scalar header), >200 chars", () => {
    // `|vvv...` is an invalid block-scalar indicator; real yamllint raises a
    // ScannerError and suppresses line-length (no finding). The Node port has no
    // full scanner: the value `|vvv...` has no space, so the line is NOT exempt
    // and the port REPORTS it -> a divergence that only occurs on invalid YAML.
    const line = `key: |${"v".repeat(250)}`;
    expect(YAML.parse.bind(null, line)).toThrow(); // genuinely invalid YAML
    const violations = findLineLengthViolations(line, policy);
    expect(violations).toHaveLength(1); // port reports; real yamllint would not
  });

  test("CONTRACT: valid YAML with the same shape behaves identically (control)", () => {
    // The SAME line made valid (balanced quote, breakable) IS a real violation
    // both for the port and for yamllint -- confirming the divergence is bound
    // to invalidity, not to the line shape.
    const valid = `key: "${"a ".repeat(150)}"`;
    expect(() => YAML.parse(valid)).not.toThrow();
    expect(findLineLengthViolations(valid, policy)).toHaveLength(1);
  });
});

// ===========================================================================
// L1: LF-normalize-on-write, but NEVER touch a file with zero rewritten lines.
// ===========================================================================
describe("L1: no spurious EOL flips / no-op when nothing is rewritten", () => {
  test("a file with zero rewritten lines is left byte-identical (CRLF preserved on disk)", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "yaml-block-eol-"));
    try {
      fs.writeFileSync(
        path.join(tempDir, ".yamllint.yaml"),
        ["rules:", "  line-length:", "    max: 200"].join("\n"),
        "utf8"
      );
      const target = path.join(tempDir, "action.yml");
      // CRLF content with NO over-length line. Write mode must not rewrite it,
      // so the on-disk CRLF bytes must survive untouched.
      const crlf = ["runs:", "  steps:", "    - shell: pwsh", "      run: |", "        echo hi"].join(
        "\r\n"
      );
      fs.writeFileSync(target, crlf, "utf8");
      const before = fs.readFileSync(target);

      const result = processFiles([target], { check: false, repoRoot: tempDir });
      expect(result.changedFiles).toHaveLength(0);
      const after = fs.readFileSync(target);
      expect(after.equals(before)).toBe(true); // byte-identical, CRLF preserved
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});

// Parity gate: the Node port MUST produce identical line-length findings to the
// real yamllint binary across every tracked YAML file. Skipped gracefully when
// the yamllint binary is not installed (so the suite still runs on machines
// without it), but it runs and must pass in CI / this environment.
const RUN_PARITY = HAS_YAMLLINT && HAS_GIT_CORPUS;
(RUN_PARITY ? describe : describe.skip)("yamllint parity (real binary vs Node port)", () => {
  test("Node findings equal yamllint line-length findings over the corpus", () => {
    const policy = resolveYamlLineLengthPolicy(YAMLLINT_CONFIG);
    const files = listTrackedYamlFiles();
    expect(files.length).toBeGreaterThan(0);

    const result = childProcess.spawnSync(
      "yamllint",
      ["-c", YAMLLINT_CONFIG, "-f", "parsable", ...files],
      { cwd: REPO_ROOT, encoding: "utf8" }
    );
    // yamllint exits non-zero when findings exist; we parse stdout regardless.
    const yamllintFindings = result.stdout
      .split(/\r?\n/)
      .map((line) => line.trim())
      .filter((line) => line.includes("(line-length)"))
      .map((line) => {
        // Format: <path>:<line>:<col>: [error] line too long (...) (line-length)
        const match = /^(.*?):(\d+):(\d+):/.exec(line);
        return `${match[1]}:${match[2]}:${match[3]}`;
      })
      .sort();

    const nodeFindings = [];
    for (const rel of files) {
      const content = fs.readFileSync(path.join(REPO_ROOT, rel), "utf8");
      for (const violation of findLineLengthViolations(content, policy)) {
        nodeFindings.push(`${rel}:${violation.line}:${violation.column}`);
      }
    }
    nodeFindings.sort();

    expect(nodeFindings).toEqual(yamllintFindings);
  });
});
