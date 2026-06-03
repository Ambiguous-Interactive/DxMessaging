/**
 * @fileoverview Tests for scripts/validate-docs-ascii.js.
 *
 * Drives the validator as a child process against fixture files and asserts
 * exit code + stderr match the documented contract. Coverage focuses on:
 *   - The ASCII-only baseline (printable ASCII passes).
 *   - Banned-codepoint classes: em-dash, control characters, curly quotes,
 *     dingbat-range geometric symbols.
 *   - The callout-position emoji exception ("> ..." prefix).
 *
 * NOTE: All non-ASCII content is constructed via String.fromCodePoint so this
 * source file stays pure ASCII (matching the project's documentation policy).
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const childProcess = require("child_process");

const VALIDATOR_SCRIPT_PATH = path.resolve(__dirname, "../validate-docs-ascii.js");
const REPO_ROOT = path.resolve(__dirname, "../..");

// Non-ASCII codepoints constructed at runtime so this source stays ASCII.
const EM_DASH = String.fromCodePoint(0x2014);
const CONTROL_U0005 = String.fromCodePoint(0x0005);
const CURLY_DOUBLE_LEFT = String.fromCodePoint(0x201c);
const CURLY_DOUBLE_RIGHT = String.fromCodePoint(0x201d);
const CURLY_SINGLE_RIGHT = String.fromCodePoint(0x2019);
const WARNING_SIGN = String.fromCodePoint(0x26a0); // dingbat-range warning
const ROCKET_EMOJI = String.fromCodePoint(0x1f680); // U+1F680 rocket
const BOM = String.fromCodePoint(0xfeff);
const RIGHT_ARROW = String.fromCodePoint(0x2192);

function runValidator(filePath) {
  return childProcess.spawnSync(process.execPath, [VALIDATOR_SCRIPT_PATH, "--paths", filePath], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });
}

function withFixture(suffix, contents, callback) {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dxmsg-docs-ascii-"));
  const filePath = path.join(tempDir, "fixture" + suffix);
  try {
    fs.writeFileSync(filePath, contents, "utf8");
    callback(filePath);
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
}

describe("validate-docs-ascii", () => {
  describe("ASCII baseline", () => {
    test("file containing only printable ASCII exits 0", () => {
      const fixture = [
        "# Heading",
        "",
        "Regular ASCII prose with punctuation: a, b, c.",
        "",
        "```csharp",
        "var x = 1;",
        "```",
        ""
      ].join("\n");
      withFixture(".md", fixture, (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(0);
        expect(result.stdout).toContain("0 violations");
      });
    });

    test("empty file exits 0", () => {
      withFixture(".md", "", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(0);
      });
    });
  });

  describe("banned codepoints", () => {
    test("em-dash (U+2014) is rejected", () => {
      withFixture(".md", "Hello " + EM_DASH + " world\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(1);
        expect(result.stderr).toContain("U+2014");
      });
    });

    test("control character U+0005 is rejected", () => {
      withFixture(".md", "Hello " + CONTROL_U0005 + " world\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(1);
        expect(result.stderr).toContain("U+0005");
      });
    });

    test("curly double quotes (U+201C / U+201D) are rejected", () => {
      const content = "Hello " + CURLY_DOUBLE_LEFT + "world" + CURLY_DOUBLE_RIGHT + "\n";
      withFixture(".md", content, (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(1);
        expect(result.stderr).toMatch(/U\+201[CD]/);
      });
    });

    test("curly single quote (U+2019) is rejected", () => {
      withFixture(".md", "Hello" + CURLY_SINGLE_RIGHT + "s world\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(1);
        expect(result.stderr).toContain("U+2019");
      });
    });

    test("dingbat warning sign (U+26A0) is rejected outright", () => {
      withFixture(".md", "Hello " + WARNING_SIGN + " world\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(1);
        expect(result.stderr).toContain("dingbat");
      });
    });

    test("right arrow (U+2192) is rejected with ASCII-only diagnostics", () => {
      withFixture(".md", "Tools " + RIGHT_ARROW + " Settings\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(1);
        expect(result.stderr).toContain("U+2192");
        expect(result.stderr).toContain("\\u{2192}");
        expect(result.stderr).not.toContain(RIGHT_ARROW);
        for (const ch of result.stderr) {
          expect(ch.codePointAt(0)).toBeLessThan(0x80);
        }
      });
    });
  });

  describe("emoji policy", () => {
    test("real emoji (rocket) in a callout line is allowed", () => {
      withFixture(".md", "> " + ROCKET_EMOJI + " Note: deploy in progress.\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(0);
      });
    });

    test("real emoji (rocket) in plain prose (not callout) is rejected", () => {
      withFixture(".md", "We launched " + ROCKET_EMOJI + " yesterday.\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(1);
        expect(result.stderr).toContain("emoji outside a callout");
      });
    });

    test("indented callout '  > rocket' is treated as callout", () => {
      withFixture(
        ".md",
        "  > " + ROCKET_EMOJI + " Tip: indented callout still counts.\n",
        (filePath) => {
          const result = runValidator(filePath);
          expect(result.status).toBe(0);
        }
      );
    });
  });

  describe("BOM handling", () => {
    test("BOM at start of file is tolerated", () => {
      withFixture(".md", BOM + "# Heading\n\nProse.\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(0);
      });
    });

    test("BOM mid-file is rejected", () => {
      withFixture(".md", "Heading\n" + BOM + " prose\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(1);
        expect(result.stderr).toContain("U+FEFF");
      });
    });
  });
});

describe("validate-docs-ascii module exports", () => {
  const { classifyChar, isCalloutLine, EMOJI_SOFT_CAP } = require("../validate-docs-ascii.js");

  test("classifyChar returns 'ok' for ASCII printable", () => {
    const result = classifyChar(0x41, "A", false);
    expect(result.kind).toBe("ok");
  });

  test("classifyChar returns 'banned' for em-dash", () => {
    const result = classifyChar(0x2014, "x " + EM_DASH + " y", false);
    expect(result.kind).toBe("banned");
  });

  test("classifyChar returns 'emoji' for emoji on callout line", () => {
    const result = classifyChar(0x1f680, "> " + ROCKET_EMOJI + " hello", false);
    expect(result.kind).toBe("emoji");
  });

  test("classifyChar returns 'banned' for emoji on non-callout line", () => {
    const result = classifyChar(0x1f680, "hello " + ROCKET_EMOJI, false);
    expect(result.kind).toBe("banned");
  });

  test("isCalloutLine recognizes '>' and indented '>' lines", () => {
    expect(isCalloutLine("> foo")).toBe(true);
    expect(isCalloutLine("  > foo")).toBe(true);
    expect(isCalloutLine("foo > bar")).toBe(false);
  });

  test("EMOJI_SOFT_CAP is exported as a positive integer", () => {
    expect(typeof EMOJI_SOFT_CAP).toBe("number");
    expect(EMOJI_SOFT_CAP).toBeGreaterThan(0);
  });
});
