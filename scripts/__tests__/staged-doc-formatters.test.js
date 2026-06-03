"use strict";

const {
  formatCodepointGlyph,
  formatAsciiViolation,
  formatAsciiWarning,
  formatCodePatternViolation,
  formatProseViolation
} = require("../lib/staged-doc-formatters");

describe("staged-doc-formatters", () => {
  const CHECK_MARK = String.fromCodePoint(0x2705);

  const toRepoRelative = (absPath) => {
    const segments = absPath.split("/");
    return `repo/${segments[segments.length - 1]}`;
  };

  test("formatCodepointGlyph keeps diagnostics ASCII-only for non-ASCII characters", () => {
    expect(formatCodepointGlyph(CHECK_MARK, 0x2705)).toBe("\\u{2705}");
    expect(formatCodepointGlyph("'", 0x27)).toBe("'\\''");
  });

  test("formatCodePatternViolation emits the intentional three-line block", () => {
    const formatted = formatCodePatternViolation(
      {
        file: "/tmp/sample.md",
        line: 14,
        column: 9,
        id: "struct-emit-temporary",
        sample: "new DamageMessage(5).Emit();",
        why: "temporary struct emit obscures ownership of the message value",
        fix: "assign to a local, then emit that local"
      },
      toRepoRelative
    );

    const lines = formatted.split("\n");
    expect(lines).toHaveLength(3);
    expect(lines[0]).toBe(
      "repo/sample.md:14:9: [struct-emit-temporary] new DamageMessage(5).Emit();"
    );
    expect(lines[1]).toBe("    why: temporary struct emit obscures ownership of the message value");
    expect(lines[2]).toBe("    fix: assign to a local, then emit that local");
  });

  test("single-line formatters remain single-line", () => {
    const asciiLine = formatAsciiViolation(
      {
        file: "/tmp/ascii.md",
        line: 2,
        column: 5,
        codepoint: 0x2705,
        char: CHECK_MARK,
        reason: "non-ASCII character"
      },
      toRepoRelative
    );
    const proseLine = formatProseViolation(
      {
        file: "/tmp/prose.md",
        line: 7,
        column: 1,
        rule: "marketing-language",
        term: "seamless"
      },
      toRepoRelative
    );

    expect(asciiLine).toBe("repo/ascii.md:2:5: U+2705 \\u{2705} -- non-ASCII character");
    expect(proseLine).toBe("repo/prose.md:7:1: [marketing-language] seamless");
    expect(asciiLine.includes("\n")).toBe(false);
    expect(proseLine.includes("\n")).toBe(false);
    for (const ch of asciiLine) {
      expect(ch.codePointAt(0)).toBeLessThan(0x80);
    }
  });

  test("formatAsciiWarning uses WARN prefix and single-line shape", () => {
    const warningLine = formatAsciiWarning(
      {
        file: "/tmp/emoji.md",
        count: 3,
        cap: 5
      },
      toRepoRelative
    );

    expect(warningLine).toBe("WARN repo/emoji.md: 3 emoji(s) found, soft cap is 5.");
    expect(warningLine.includes("\n")).toBe(false);
  });
});
