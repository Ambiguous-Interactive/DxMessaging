/**
 * @fileoverview Tests for scripts/validate-docs-prose.js.
 *
 * Drives the validator both through its module exports and as a child
 * process against fixture files. Coverage focuses on:
 *   - Each rule's positive match and the suggestion text.
 *   - Code-fence and inline-code skip behavior.
 *   - URL and HTML attribute skip behavior.
 *   - Allow markers (same-line, next-line, file-wide).
 *   - Per-file exemptions (CHANGELOG, .llm/skills/documentation/).
 *   - CLI flags: --paths, --rule, --list-rules, --summary, exit codes.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const childProcess = require("child_process");

const VALIDATOR_SCRIPT_PATH = path.resolve(__dirname, "../validate-docs-prose.js");
const REPO_ROOT = path.resolve(__dirname, "../..");

const {
  scanContent,
  RULES,
  RULE_INDEX,
  parseBaseline,
  baselineKey,
  formatBaselineEntry,
  EXCLUDE_DIRS,
  MARKETING_TERMS,
  LLM_FILLER_TERMS
} = require("../validate-docs-prose.js");

function runValidator(args) {
  return childProcess.spawnSync(process.execPath, [VALIDATOR_SCRIPT_PATH, ...args], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });
}

function withFixture(suffix, contents, callback) {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dxmsg-prose-"));
  const filePath = path.join(tempDir, `fixture${suffix}`);
  try {
    fs.writeFileSync(filePath, contents, "utf8");
    callback(filePath, tempDir);
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
}

function scan(content, filename) {
  const filePath = filename || "/tmp/fake.md";
  return scanContent(filePath, content, {});
}

describe("validate-docs-prose rule matching", () => {
  test("marketing: 'powerful' is flagged whole-word", () => {
    const r = scan("This is a powerful library.\n");
    expect(r.violations).toHaveLength(1);
    expect(r.violations[0].rule).toBe("marketing");
    expect(r.violations[0].term.toLowerCase()).toBe("powerful");
  });

  test("marketing: substring inside a longer word does not match", () => {
    const r = scan("The powerfulness of names should not match.\n");
    expect(r.violations).toHaveLength(0);
  });

  test("marketing: case-insensitive", () => {
    const r = scan("Build a Robust API.\n");
    expect(r.violations).toHaveLength(1);
    expect(r.violations[0].rule).toBe("marketing");
  });

  test("marketing: hyphenated terms match", () => {
    const r = scan("Our cutting-edge runtime.\n");
    expect(r.violations).toHaveLength(1);
    expect(r.violations[0].term.toLowerCase()).toBe("cutting-edge");
  });

  test("llm-filler: phrase match across spaces", () => {
    const r = scan("We will delve into the topic.\n");
    expect(r.violations.some((v) => v.rule === "llm-filler")).toBe(true);
  });

  test("llm-filler: 'realm of' matches", () => {
    const r = scan("In the realm of messaging, there is X.\n");
    expect(r.violations.some((v) => v.rule === "llm-filler")).toBe(true);
  });

  test("hedge: 'Furthermore,' at line start is flagged", () => {
    const r = scan("Furthermore, the bus is sync.\n");
    expect(r.violations.some((v) => v.rule === "hedge")).toBe(true);
  });

  test("hedge: 'Furthermore,' mid-line is NOT flagged", () => {
    const r = scan("This is fine and Furthermore, ignore.\n");
    expect(r.violations.some((v) => v.rule === "hedge")).toBe(false);
  });

  test("hedge: list item 'Furthermore,' is flagged", () => {
    const r = scan("- Furthermore, the bus is sync.\n");
    expect(r.violations.some((v) => v.rule === "hedge")).toBe(true);
  });

  test("hedge: 'It's worth noting' triggers", () => {
    const r = scan("It's worth noting that X is Y.\n");
    expect(r.violations.some((v) => v.rule === "hedge")).toBe(true);
  });

  test("vague-quantifier: 'a wide variety of' is flagged", () => {
    const r = scan("We support a wide variety of senders.\n");
    expect(r.violations.some((v) => v.rule === "vague-quantifier")).toBe(true);
  });

  test("vague-quantifier: 'numerous' alone matches", () => {
    const r = scan("There are numerous reasons for this.\n");
    expect(r.violations.some((v) => v.rule === "vague-quantifier")).toBe(true);
  });

  test("soft-fluff: 'allows you to easily' is flagged", () => {
    const r = scan("This allows you to easily emit messages.\n");
    expect(r.violations.some((v) => v.rule === "soft-fluff")).toBe(true);
  });

  test("soft-fluff: 'enables you to' is flagged", () => {
    const r = scan("This enables you to register a handler.\n");
    expect(r.violations.some((v) => v.rule === "soft-fluff")).toBe(true);
  });
});

describe("validate-docs-prose skip rules", () => {
  test("words inside fenced code blocks are NOT flagged", () => {
    const md =
      'Intro paragraph.\n\n```csharp\n// powerful is fine inside code\nvar x = "powerful";\n```\n\nMore prose.\n';
    const r = scan(md);
    expect(r.violations).toHaveLength(0);
  });

  test("words inside inline backticks are NOT flagged", () => {
    const r = scan("The flag `powerful` toggles things.\n");
    expect(r.violations).toHaveLength(0);
  });

  test("words inside URLs are NOT flagged", () => {
    const r = scan("See https://example.com/powerful/blazing-fast for more.\n");
    expect(r.violations).toHaveLength(0);
  });

  test("words inside HTML attributes are NOT flagged", () => {
    const r = scan('<img src="powerful.png" alt="comprehensive guide" />\n');
    expect(r.violations).toHaveLength(0);
  });

  test("multiple inline-code spans on one line are all stripped", () => {
    const r = scan("`powerful` and `seamless` both stay; outside it's fine.\n");
    expect(r.violations).toHaveLength(0);
  });
});

describe("validate-docs-prose allow markers", () => {
  test("same-line allow disables a single term", () => {
    const r = scan("We are powerful here. <!-- prose-allow: powerful -->\n");
    expect(r.violations).toHaveLength(0);
  });

  test("same-line allow only affects that line", () => {
    const md = "We are powerful here. <!-- prose-allow: powerful -->\nWe are powerful again.\n";
    const r = scan(md);
    expect(r.violations).toHaveLength(1);
    expect(r.violations[0].line).toBe(2);
  });

  test("file-wide allow applies anywhere in the file", () => {
    const md =
      "<!-- prose-allow-file: powerful -->\n\nFirst powerful line.\nSecond powerful line.\n";
    const r = scan(md);
    expect(r.violations).toHaveLength(0);
  });

  test("file-wide allow comment line itself does not trigger", () => {
    const md = "<!-- prose-allow-file: powerful -->\n";
    const r = scan(md);
    expect(r.violations).toHaveLength(0);
  });

  test("next-line allow applies to the next non-blank line", () => {
    const md =
      "<!-- prose-allow-next-line: powerful -->\n\nThis is powerful.\nAnother powerful line.\n";
    const r = scan(md);
    // First "powerful" is allowed, second is not.
    expect(r.violations).toHaveLength(1);
    expect(r.violations[0].line).toBe(4);
  });

  test("allow markers are case-insensitive in their term list", () => {
    const r = scan("We are POWERFUL here. <!-- prose-allow: Powerful -->\n");
    expect(r.violations).toHaveLength(0);
  });
});

describe("validate-docs-prose per-file exemptions", () => {
  test("CHANGELOG.md is exempt from 'comprehensive'", () => {
    const r = scanContent("/x/CHANGELOG.md", "Comprehensive overhaul of the bus.\n", {});
    expect(r.violations).toHaveLength(0);
  });

  test("CHANGELOG.md still flags 'powerful'", () => {
    const r = scanContent("/x/CHANGELOG.md", "Powerful overhaul of the bus.\n", {});
    expect(r.violations.length).toBeGreaterThan(0);
  });

  test(".llm/skills/documentation/ files are wholly exempt", () => {
    const skillsRoot = path.join(REPO_ROOT, ".llm", "skills", "documentation", "fake-policy.md");
    const r = scanContent(skillsRoot, "We discuss powerful, seamless, and delve into.\n", {});
    expect(r.fileExempt).toBe(true);
    expect(r.violations).toHaveLength(0);
  });
});

describe("validate-docs-prose CLI", () => {
  test("--list-rules prints all rules and exits 0", () => {
    const result = runValidator(["--list-rules"]);
    expect(result.status).toBe(0);
    for (const rule of RULES) {
      expect(result.stdout).toContain(rule.id);
    }
  });

  test("--paths with a clean file exits 0", () => {
    withFixture(".md", "# Title\n\nA short clean paragraph.\n", (fp) => {
      const result = runValidator(["--paths", fp]);
      expect(result.status).toBe(0);
      expect(result.stdout).toContain("0 violations");
    });
  });

  test("--paths with a dirty file exits 1 and reports rule", () => {
    withFixture(".md", "This is a powerful library.\n", (fp) => {
      const result = runValidator(["--paths", fp]);
      expect(result.status).toBe(1);
      expect(result.stderr).toContain("[marketing/marketing]");
      expect(result.stderr).toContain("powerful");
    });
  });

  test("--rule narrows scanning to one rule", () => {
    withFixture(".md", "Furthermore, this is powerful.\n", (fp) => {
      const onlyMarketing = runValidator(["--paths", fp, "--rule", "marketing"]);
      expect(onlyMarketing.status).toBe(1);
      expect(onlyMarketing.stderr).not.toContain("hedge");
      expect(onlyMarketing.stderr).toContain("marketing");

      const onlyHedge = runValidator(["--paths", fp, "--rule", "hedge"]);
      expect(onlyHedge.status).toBe(1);
      expect(onlyHedge.stderr).toContain("hedge");
      expect(onlyHedge.stderr).not.toContain("marketing");
    });
  });

  test("--rule with unknown id exits 1 with a clear error", () => {
    const result = runValidator(["--rule", "no-such-rule"]);
    expect(result.status).toBe(1);
    expect(result.stderr).toContain("Unknown rule id");
  });

  test("--summary prints per-category counts and exits 1 on dirty file", () => {
    withFixture(".md", "This is a powerful library that is also robust.\n", (fp) => {
      const result = runValidator(["--paths", fp, "--summary"]);
      expect(result.status).toBe(1);
      expect(result.stdout).toMatch(/marketing:\s*2/);
      expect(result.stdout).toMatch(/2 violation\(s\)/);
    });
  });

  test("--summary on a clean file exits 0", () => {
    withFixture(".md", "# Clean\n\nNothing to see.\n", (fp) => {
      const result = runValidator(["--paths", fp, "--summary"]);
      expect(result.status).toBe(0);
    });
  });

  test("--help exits 0 and prints usage", () => {
    const result = runValidator(["--help"]);
    expect(result.status).toBe(0);
    expect(result.stdout).toContain("Usage:");
  });

  test("unknown option exits 1", () => {
    const result = runValidator(["--bogus-flag"]);
    expect(result.status).toBe(1);
    expect(result.stderr).toContain("Unknown option");
  });

  test("explicit file argument is scanned", () => {
    withFixture(".md", "Powerful claim.\n", (fp) => {
      const result = runValidator([fp]);
      expect(result.status).toBe(1);
      expect(result.stderr).toContain("[marketing/marketing]");
    });
  });
});

describe("validate-docs-prose C# XML doc handling", () => {
  test("only /// lines are scanned in .cs files", () => {
    const cs =
      'namespace Foo\n{\n    /// <summary>This is powerful.</summary>\n    public class Bar { string s = "powerful"; }\n}\n';
    const r = scanContent("/x/Bar.cs", cs, {});
    expect(r.violations).toHaveLength(1);
    expect(r.violations[0].line).toBe(3);
  });

  test("non-doc lines in .cs files are ignored", () => {
    const cs = 'namespace Foo\n{\n    public class Bar { string s = "powerful"; }\n}\n';
    const r = scanContent("/x/Bar.cs", cs, {});
    expect(r.violations).toHaveLength(0);
  });
});

describe("validate-docs-prose module exports", () => {
  test("RULES is non-empty and each rule has the expected shape", () => {
    expect(RULES.length).toBeGreaterThan(0);
    for (const rule of RULES) {
      expect(typeof rule.id).toBe("string");
      expect(typeof rule.category).toBe("string");
      expect(typeof rule.severity).toBe("string");
      expect(typeof rule.matchLine).toBe("function");
    }
  });

  test("RULE_INDEX exposes lookups by id", () => {
    for (const rule of RULES) {
      expect(RULE_INDEX.get(rule.id)).toBe(rule);
    }
  });

  test("EXCLUDE_DIRS contains the gitignored directories the reviewer flagged", () => {
    expect(EXCLUDE_DIRS.has(".venv")).toBe(true);
    expect(EXCLUDE_DIRS.has("venv")).toBe(true);
    expect(EXCLUDE_DIRS.has(".artifacts")).toBe(true);
    expect(EXCLUDE_DIRS.has("progress")).toBe(true);
    expect(EXCLUDE_DIRS.has(".vs")).toBe(true);
    expect(EXCLUDE_DIRS.has(".claude")).toBe(true);
    expect(EXCLUDE_DIRS.has(".devcontainer")).toBe(true);
    expect(EXCLUDE_DIRS.has(".config")).toBe(true);
  });
});

// --- Reviewer-driven hardening tests ---------------------------------------

describe("validate-docs-prose C# /// hedge prefix", () => {
  test("hedge fires when /// prefix precedes the term", () => {
    const cs = "/// Furthermore, this is a fact.\n";
    const r = scanContent("/x/Bar.cs", cs, {});
    expect(r.violations.some((v) => v.rule === "hedge")).toBe(true);
  });

  test("hedge handles indented /// in C#", () => {
    const cs = "    /// Moreover, more facts.\n";
    const r = scanContent("/x/Bar.cs", cs, {});
    expect(r.violations.some((v) => v.rule === "hedge")).toBe(true);
  });
});

describe("validate-docs-prose BOM handling", () => {
  test("leading BOM is stripped before scanning", () => {
    const md = "﻿This is a powerful library.\n";
    const r = scanContent("/x/file.md", md, {});
    expect(r.violations).toHaveLength(1);
    // Column counts must NOT include the BOM.
    expect(r.violations[0].column).toBe(11); // "This is a " = 10 chars
    expect(r.violations[0].line).toBe(1);
  });

  test("BOM does not break frontmatter detection", () => {
    const md = "﻿---\ntitle: foo\n---\n\nThis is powerful.\n";
    const r = scanContent("/x/file.md", md, {});
    expect(r.violations).toHaveLength(1);
    expect(r.violations[0].line).toBe(5);
  });
});

describe("validate-docs-prose CRLF line endings", () => {
  test("CRLF is normalized; line numbers point at logical lines", () => {
    const md = "Line 1\r\nThis is powerful.\r\nLine 3\r\n";
    const r = scanContent("/x/file.md", md, {});
    expect(r.violations).toHaveLength(1);
    expect(r.violations[0].line).toBe(2);
  });

  test("lone CR also normalizes", () => {
    const md = "Line 1\rThis is powerful.\rLine 3\r";
    const r = scanContent("/x/file.md", md, {});
    expect(r.violations).toHaveLength(1);
    expect(r.violations[0].line).toBe(2);
  });
});

describe("validate-docs-prose tab handling", () => {
  test("tabs inside a paragraph preserve column positions byte-faithfully", () => {
    // The validator counts byte-by-byte; a hard tab inside a sentence
    // remains a single column. (Indented code blocks are skipped, so
    // we use a tab in the middle of a paragraph here.)
    const md = "Intro line with no leading whitespace.\nA\tpowerful claim here.\n";
    const r = scanContent("/x/file.md", md, {});
    expect(r.violations).toHaveLength(1);
    // 'A\t' = 2 bytes, then 'powerful' starts at byte index 2 (0-based)
    // -> 1-based column 3.
    expect(r.violations[0].column).toBe(3);
    expect(r.violations[0].line).toBe(2);
  });
});

describe("validate-docs-prose multi-line allow markers", () => {
  test("multi-line marker is reported as malformed (warning, not failure)", () => {
    const md = "<!-- prose-allow: powerful,\nrobust -->\n\nThis is powerful.\n";
    const r = scanContent("/x/file.md", md, {});
    // Marker did NOT take effect, so 'powerful' is still flagged.
    expect(r.violations.some((v) => v.rule === "marketing")).toBe(true);
    expect(Array.isArray(r.malformedMarkers)).toBe(true);
    expect(r.malformedMarkers.length).toBeGreaterThan(0);
    expect(r.malformedMarkers[0].line).toBe(1);
  });

  test("single-line allow marker still works after multi-line warning was added", () => {
    const md = "Powerful claim here. <!-- prose-allow: powerful -->\n";
    const r = scanContent("/x/file.md", md, {});
    expect(r.violations).toHaveLength(0);
  });
});

describe("validate-docs-prose indented code blocks", () => {
  test("indented (4-space) code blocks after a blank line are skipped", () => {
    const md =
      "Intro paragraph.\n\n    powerful = true; // not flagged\n    seamless = false;\n\nMore prose.\n";
    const r = scanContent("/x/file.md", md, {});
    expect(r.violations).toHaveLength(0);
  });

  test("tab-indented code blocks are skipped", () => {
    const md = "Intro.\n\n\tpowerful = 1;\n\trobust = 2;\n\nOutside is scanned.\n";
    const r = scanContent("/x/file.md", md, {});
    expect(r.violations).toHaveLength(0);
  });

  test("indented text NOT preceded by blank is not a code block", () => {
    const md = "Intro line\n    This is a powerful line.\n";
    const r = scanContent("/x/file.md", md, {});
    // Continuation paragraph -- still scanned.
    expect(r.violations.some((v) => v.rule === "marketing")).toBe(true);
  });
});

describe("validate-docs-prose closing fence with trailing content", () => {
  test("text after the closing ``` on the same line is scanned as prose", () => {
    const md = "```\nfoo\n``` and this is powerful prose.\n";
    const r = scanContent("/x/file.md", md, {});
    expect(r.violations.some((v) => v.rule === "marketing")).toBe(true);
  });
});

describe("validate-docs-prose YAML frontmatter", () => {
  test("frontmatter is skipped wholly", () => {
    const md =
      "---\ntitle: powerful library\ndescription: comprehensive guide\n---\n\nClean prose.\n";
    const r = scanContent("/x/file.md", md, {});
    expect(r.violations).toHaveLength(0);
  });

  test("frontmatter does not skip body violations", () => {
    const md = "---\ntitle: ok\n---\n\nThis is powerful prose.\n";
    const r = scanContent("/x/file.md", md, {});
    expect(r.violations).toHaveLength(1);
    expect(r.violations[0].line).toBe(5);
  });

  test("missing closing --- means frontmatter is not detected", () => {
    const md = "---\ntitle: powerful here\n\nbody powerful here.\n";
    const r = scanContent("/x/file.md", md, {});
    // No frontmatter detected; both 'powerful' get flagged.
    expect(r.violations.length).toBeGreaterThanOrEqual(2);
  });
});

describe("validate-docs-prose multi-line HTML tag", () => {
  test("HTML tag spanning multiple lines is masked", () => {
    const md = '<a\n  href="https://example.com"\n  alt="powerful seamless guide"\n>link</a>\n';
    const r = scanContent("/x/file.md", md, {});
    expect(r.violations).toHaveLength(0);
  });
});

describe("validate-docs-prose inflected forms (M2)", () => {
  test.each([
    ["robustly", "marketing"],
    ["powerfully", "marketing"],
    ["comprehensively", "marketing"],
    ["elegantly", "marketing"],
    ["seamlessness", "marketing"]
  ])("marketing inflected form '%s' fires the %s rule", (term, rule) => {
    const r = scanContent("/x/f.md", `This is ${term}.\n`, {});
    expect(r.violations.some((v) => v.rule === rule)).toBe(true);
  });

  test("LLM filler 'delved into' is matched", () => {
    const r = scanContent("/x/f.md", "We delved into the topic.\n", {});
    expect(r.violations.some((v) => v.rule === "llm-filler")).toBe(true);
  });

  test("LLM filler 'delves into' is matched", () => {
    const r = scanContent("/x/f.md", "She delves into the topic.\n", {});
    expect(r.violations.some((v) => v.rule === "llm-filler")).toBe(true);
  });

  test("'cutting edge' (no hyphen) is flagged as marketing", () => {
    const r = scanContent("/x/f.md", "Our cutting edge runtime.\n", {});
    expect(r.violations.some((v) => v.rule === "marketing")).toBe(true);
  });
});

describe("validate-docs-prose hedge without trailing comma (M3)", () => {
  test("'Furthermore' without comma is flagged", () => {
    const r = scanContent("/x/f.md", "Furthermore the bus is sync.\n", {});
    expect(r.violations.some((v) => v.rule === "hedge")).toBe(true);
  });

  test("'Overall' without comma is flagged", () => {
    const r = scanContent("/x/f.md", "Overall the design works.\n", {});
    expect(r.violations.some((v) => v.rule === "hedge")).toBe(true);
  });

  test("'Furthermore,' with comma still flagged", () => {
    const r = scanContent("/x/f.md", "Furthermore, the bus is sync.\n", {});
    expect(r.violations.some((v) => v.rule === "hedge")).toBe(true);
  });

  test("hedge does not fire on a partial token like 'Overalls'", () => {
    const r = scanContent("/x/f.md", "Overalls are a kind of clothing.\n", {});
    expect(r.violations.some((v) => v.rule === "hedge")).toBe(false);
  });
});

describe("validate-docs-prose CHANGELOG case-insensitive (m3)", () => {
  test("changelog.md (lowercase) is exempt from 'comprehensive'", () => {
    const r = scanContent("/x/changelog.md", "Comprehensive overhaul.\n", {});
    expect(r.violations).toHaveLength(0);
  });

  test("CHANGELOG.markdown is exempt from 'comprehensive'", () => {
    const r = scanContent("/x/CHANGELOG.markdown", "Comprehensive overhaul.\n", {});
    expect(r.violations).toHaveLength(0);
  });
});

describe("validate-docs-prose generated file exemptions (C8)", () => {
  test("llms.txt at the repo root is exempt", () => {
    const target = path.join(REPO_ROOT, "llms.txt");
    const r = scanContent(target, "This is a comprehensive overview.\n", {});
    expect(r.fileExempt).toBe(true);
    expect(r.violations).toHaveLength(0);
  });

  test(".llm/skills/index.md is exempt", () => {
    const target = path.join(REPO_ROOT, ".llm", "skills", "index.md");
    const r = scanContent(target, "Comprehensive index of all skills.\n", {});
    expect(r.fileExempt).toBe(true);
    expect(r.violations).toHaveLength(0);
  });
});

describe("validate-docs-prose --paths walks .cs anywhere (C9)", () => {
  test("--paths <dir> walks .cs files outside CS_SCAN_ROOTS", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dxmsg-paths-cs-"));
    try {
      const subDir = path.join(tempDir, "Random");
      fs.mkdirSync(subDir);
      const csFile = path.join(subDir, "Foo.cs");
      fs.writeFileSync(csFile, "/// This is a powerful summary.\nclass Foo {}\n", "utf8");
      const result = childProcess.spawnSync(
        process.execPath,
        [VALIDATOR_SCRIPT_PATH, "--paths", tempDir],
        { cwd: REPO_ROOT, encoding: "utf8" }
      );
      expect(result.status).toBe(1);
      expect(result.stderr).toContain("powerful");
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});

describe("validate-docs-prose baseline (C5)", () => {
  test("baseline file skips matching violations", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dxmsg-baseline-"));
    try {
      const dirty = path.join(tempDir, "dirty.md");
      fs.writeFileSync(dirty, "This is a powerful library.\n", "utf8");
      // First pass: write the baseline.
      const baselinePath = path.join(tempDir, "baseline.txt");
      const writeResult = childProcess.spawnSync(
        process.execPath,
        [VALIDATOR_SCRIPT_PATH, "--paths", tempDir, "--write-baseline", baselinePath],
        { cwd: REPO_ROOT, encoding: "utf8" }
      );
      expect(writeResult.status).toBe(0);
      const baselineText = fs.readFileSync(baselinePath, "utf8");
      expect(baselineText).toContain("dirty.md");
      expect(baselineText).toContain("[marketing]");
      expect(baselineText).toContain("powerful");

      // Second pass: with the baseline, exit 0.
      const cleanResult = childProcess.spawnSync(
        process.execPath,
        [VALIDATOR_SCRIPT_PATH, "--paths", tempDir, "--baseline", baselinePath],
        { cwd: REPO_ROOT, encoding: "utf8" }
      );
      expect(cleanResult.status).toBe(0);
      expect(cleanResult.stdout).toContain("0 violations");
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("baseline does NOT match if line/column moves", () => {
    const v1 = {
      file: path.join(REPO_ROOT, "doc.md"),
      line: 10,
      column: 5,
      rule: "marketing",
      term: "powerful"
    };
    const v2 = { ...v1, line: 11 };
    const text = formatBaselineEntry(v1);
    const keys = parseBaseline(text);
    expect(keys.has(baselineKey(v1))).toBe(true);
    expect(keys.has(baselineKey(v2))).toBe(false);
  });

  test("baseline file with comment lines parses correctly", () => {
    const text = "# header\n# more comments\n\ndoc.md:1:1 [marketing] powerful\n";
    const keys = parseBaseline(text);
    expect(keys.size).toBe(1);
    expect(keys.has("doc.md|1|1|marketing|powerful")).toBe(true);
  });

  test("--baseline points to a missing file -> exit 1", () => {
    const result = childProcess.spawnSync(
      process.execPath,
      [
        VALIDATOR_SCRIPT_PATH,
        "--paths",
        "/tmp/nope-not-exist",
        "--baseline",
        "/tmp/no-such-baseline-file.txt"
      ],
      { cwd: REPO_ROOT, encoding: "utf8" }
    );
    expect(result.status).toBe(1);
    expect(result.stderr).toContain("baseline file not found");
  });
});

describe("validate-docs-prose --list-rules dumps term lists (m4)", () => {
  test("--list-rules output contains the marketing term list", () => {
    const result = childProcess.spawnSync(
      process.execPath,
      [VALIDATOR_SCRIPT_PATH, "--list-rules"],
      { cwd: REPO_ROOT, encoding: "utf8" }
    );
    expect(result.status).toBe(0);
    expect(result.stdout).toContain("powerful");
    expect(result.stdout).toContain("comprehensive");
    expect(result.stdout).toContain("delve into");
  });
});

describe("validate-docs-prose --summary trailer goes to stdout (m10)", () => {
  test("--summary trailer is on stdout", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dxmsg-summary-"));
    try {
      const f = path.join(tempDir, "f.md");
      fs.writeFileSync(f, "Powerful and robust prose.\n", "utf8");
      const result = childProcess.spawnSync(
        process.execPath,
        [VALIDATOR_SCRIPT_PATH, "--paths", f, "--summary"],
        { cwd: REPO_ROOT, encoding: "utf8" }
      );
      expect(result.status).toBe(1);
      // Trailer text is on stdout (m10), not stderr.
      expect(result.stdout).toMatch(/violation\(s\) across/);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});

describe("validate-docs-prose extended marketing terms", () => {
  test.each([
    ["production-ready"],
    ["enterprise-grade"],
    ["lightning-fast"],
    ["frictionless"],
    ["battle-tested"],
    ["bulletproof"],
    ["rock-solid"]
  ])("marketing term '%s' is flagged whole-word", (term) => {
    const r = scanContent("/x/f.md", `This is ${term} software.\n`, {});
    expect(r.violations.some((v) => v.rule === "marketing")).toBe(true);
    expect(r.violations.some((v) => v.term.toLowerCase() === term.toLowerCase())).toBe(true);
  });

  test("marketing extended terms are case-insensitive", () => {
    const r = scanContent("/x/f.md", "Production-Ready and BULLETPROOF claims.\n", {});
    expect(r.violations.filter((v) => v.rule === "marketing")).toHaveLength(2);
  });

  test("marketing extended terms appear in MARKETING_TERMS export", () => {
    for (const term of [
      "production-ready",
      "enterprise-grade",
      "lightning-fast",
      "frictionless",
      "battle-tested",
      "bulletproof",
      "rock-solid"
    ]) {
      expect(MARKETING_TERMS).toContain(term);
    }
  });
});

describe("validate-docs-prose absolute path fallback (m2)", () => {
  test("violation outside the repo root falls back to absolute path", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dxmsg-outside-"));
    try {
      const f = path.join(tempDir, "outside.md");
      fs.writeFileSync(f, "Powerful claim.\n", "utf8");
      const result = childProcess.spawnSync(
        process.execPath,
        [VALIDATOR_SCRIPT_PATH, "--paths", f],
        { cwd: REPO_ROOT, encoding: "utf8" }
      );
      expect(result.status).toBe(1);
      // The reported path is the absolute path (it begins with "/" or
      // the temp dir path), not "../...".
      expect(result.stderr).not.toMatch(/^\.\./m);
      expect(result.stderr).toContain(f);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});
