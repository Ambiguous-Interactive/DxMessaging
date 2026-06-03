/**
 * @fileoverview Tests for scripts/lib/jest-error-decoder.js.
 */

"use strict";

const {
  PATTERNS,
  decodeJestStderr,
  formatRepairBanner,
  isTruthyEnv
} = require("../lib/jest-error-decoder");

describe("jest-error-decoder", () => {
  test("PATTERNS is frozen and contains the four expected kinds in most-specific-first order", () => {
    expect(Object.isFrozen(PATTERNS)).toBe(true);
    const kinds = PATTERNS.map((entry) => entry.kind);
    // Order is precedence: MISSING_TEST_RUNNER (most specific) wins over
    // the broader "Cannot find module" patterns when both would match.
    // PARTIAL_NODE_MODULES_INSTALL is last because its sentinel regex
    // never matches naturally — it is emitted synthetically by the
    // integrity gate when auto-repair has been exhausted.
    expect(kinds).toEqual([
      "MISSING_TEST_RUNNER",
      "CORRUPT_ISOLATED_CACHE",
      "MISSING_LOCAL_JEST",
      "PARTIAL_NODE_MODULES_INSTALL"
    ]);
    expect(PATTERNS.length).toBe(4);
    expect(PATTERNS[PATTERNS.length - 1].kind).toBe("PARTIAL_NODE_MODULES_INSTALL");
    for (const entry of PATTERNS) {
      expect(Object.isFrozen(entry)).toBe(true);
      expect(Object.isFrozen(entry.rootCauses)).toBe(true);
      expect(Object.isFrozen(entry.repairCommands)).toBe(true);
      expect(Object.isFrozen(entry.selfHeal)).toBe(true);
    }
  });

  test("MISSING_TEST_RUNNER advertises BOTH isolatedCacheReset and npmCi selfHeal flags", () => {
    // The Windows failure that motivated this rewrite was caused by a
    // partial extract of the repo's node_modules — npm ci is the right
    // recovery, NOT isolated-cache reset. The original entry only set
    // isolatedCacheReset; we now expose both so the runtime tier
    // dispatcher can pick the correct channel based on the resolved
    // runner path's containing tree.
    const missingTestRunner = PATTERNS.find((p) => p.kind === "MISSING_TEST_RUNNER");
    expect(missingTestRunner).toBeTruthy();
    expect(missingTestRunner.selfHeal.isolatedCacheReset).toBe(true);
    expect(missingTestRunner.selfHeal.npmCi).toBe(true);
    expect(missingTestRunner.selfHeal.retryOnce).toBe(true);
  });

  test("PARTIAL_NODE_MODULES_INSTALL is the lowest-priority entry and only matches the integrity-gate sentinel", () => {
    const sentinel = PATTERNS[PATTERNS.length - 1];
    expect(sentinel.kind).toBe("PARTIAL_NODE_MODULES_INSTALL");
    // The regex must NOT match ambient Jest stderr; only the explicit
    // synthetic sentinel emitted by the gate's banner-print path.
    expect(sentinel.regex.test("Cannot find module 'jest-circus/runner'")).toBe(false);
    expect(
      sentinel.regex.test("Module /tmp/x/runner.js in the testRunner option was not found.")
    ).toBe(false);
    expect(sentinel.regex.test("__INTEGRITY_GATE_FAILURE__")).toBe(true);
    // Decoding the sentinel string returns this entry.
    const decoded = decodeJestStderr("__INTEGRITY_GATE_FAILURE__");
    expect(decoded).not.toBeNull();
    expect(decoded.kind).toBe("PARTIAL_NODE_MODULES_INSTALL");
    expect(decoded.selfHeal.npmCi).toBe(false);
    expect(decoded.selfHeal.retryOnce).toBe(false);
  });

  test("PATTERNS cannot be mutated by consumers", () => {
    // Strict mode (this file uses "use strict") makes mutation of a frozen
    // array throw. The throw is incidental to the invariant we care about
    // (mutation must not stick), so we test the invariant directly using
    // Jest's `toThrow` matcher and then re-read PATTERNS.
    expect(() => {
      PATTERNS[0].rootCauses.push("malicious");
    }).toThrow();
    expect(PATTERNS[0].rootCauses.includes("malicious")).toBe(false);
  });

  test("decoded result does NOT expose internal regex (dead data)", () => {
    const decoded = decodeJestStderr("Cannot find module 'jest/bin/jest.js'");
    expect(decoded).not.toBeNull();
    // The internal regex is an implementation detail; consumers should
    // not depend on it, and we now omit it from the returned object.
    expect(Object.prototype.hasOwnProperty.call(decoded, "regex")).toBe(false);
  });

  test("decodeJestStderr accepts Buffer input and converts to UTF-8 internally", () => {
    const decoded = decodeJestStderr(Buffer.from("Cannot find module 'jest/bin/jest.js'", "utf8"));
    expect(decoded).not.toBeNull();
    expect(decoded.kind).toBe("MISSING_LOCAL_JEST");
  });

  test("decodes the Windows MISSING_TEST_RUNNER stderr verbatim", () => {
    const stderr =
      "Module D:\\Code\\Packages\\Packages\\com.wallstop-studios.dxmessaging\\node_modules\\jest-circus\\build\\runner.js in the testRunner option was not found.";
    const decoded = decodeJestStderr(stderr);
    expect(decoded).not.toBeNull();
    expect(decoded.kind).toBe("MISSING_TEST_RUNNER");
    expect(decoded.capturedMatch).toBeTruthy();
    expect(decoded.capturedMatch[1]).toContain(
      "D:\\Code\\Packages\\Packages\\com.wallstop-studios.dxmessaging"
    );
    expect(decoded.capturedMatch[1]).toContain("jest-circus");
    expect(decoded.capturedMatch[1]).toContain("runner.js");
  });

  test("decodes 'Cannot find module jest-circus/runner' as CORRUPT_ISOLATED_CACHE", () => {
    const decoded = decodeJestStderr("Error: Cannot find module 'jest-circus/runner'");
    expect(decoded).not.toBeNull();
    expect(decoded.kind).toBe("CORRUPT_ISOLATED_CACHE");
  });

  test("decodes 'Cannot find module jest/bin/jest.js' as MISSING_LOCAL_JEST", () => {
    const decoded = decodeJestStderr("Cannot find module 'jest/bin/jest.js'");
    expect(decoded).not.toBeNull();
    expect(decoded.kind).toBe("MISSING_LOCAL_JEST");
  });

  test("decodes 'Error: Cannot find module jest' as MISSING_LOCAL_JEST", () => {
    const decoded = decodeJestStderr("Error: Cannot find module 'jest'");
    expect(decoded).not.toBeNull();
    expect(decoded.kind).toBe("MISSING_LOCAL_JEST");
  });

  test("MISSING_LOCAL_JEST regex anchors to line start and is not triggered by mid-line module identifiers", () => {
    // The previous regex matched "Cannot find module 'jest'" anywhere in
    // the stream, so a short module identifier whose own error text ended
    // with that substring would false-positive. Anchoring to line start
    // (with optional "Error:" prefix) prevents this.
    const stderr = "babylonia: Cannot find module 'jest'";
    expect(decodeJestStderr(stderr)).toBeNull();
  });

  test("MISSING_LOCAL_JEST does NOT match jest-circus / jest-cli false positives", () => {
    // The (?![\w-]) suffix prevents matching neighbors of "jest" that are
    // themselves valid module names. jest-circus is covered separately
    // by the CORRUPT_ISOLATED_CACHE pattern.
    const stderrCircus = "Cannot find module 'jest-circus'";
    const decodedCircus = decodeJestStderr(stderrCircus);
    // jest-circus matches CORRUPT_ISOLATED_CACHE (the more-specific pattern).
    expect(decodedCircus).not.toBeNull();
    expect(decodedCircus.kind).toBe("CORRUPT_ISOLATED_CACHE");

    // jest-cli does not match any pattern (not jest-circus, not bare jest).
    expect(decodeJestStderr("Cannot find module 'jest-cli'")).toBeNull();
  });

  test("MISSING_LOCAL_JEST matches 'Error: Cannot find module jest/bin/jest.js' (canonical Node error)", () => {
    const decoded = decodeJestStderr("Error: Cannot find module 'jest/bin/jest.js'");
    expect(decoded).not.toBeNull();
    expect(decoded.kind).toBe("MISSING_LOCAL_JEST");
  });

  test("pattern order is precedence (most specific first): MISSING_TEST_RUNNER beats MISSING_LOCAL_JEST when both could match", () => {
    // A stderr containing both signals — the precise "testRunner option
    // was not found" sentence AND a "Cannot find module 'jest'" line —
    // must decode to MISSING_TEST_RUNNER because that pattern is more
    // specific and is listed earlier in PATTERNS.
    const stderr =
      "Module /tmp/foo/runner.js in the testRunner option was not found.\n" +
      "Cannot find module 'jest'";
    const decoded = decodeJestStderr(stderr);
    expect(decoded).not.toBeNull();
    expect(decoded.kind).toBe("MISSING_TEST_RUNNER");
  });

  test("returns null for unrelated stderr", () => {
    expect(decodeJestStderr("some random unrelated error")).toBeNull();
  });

  test("decodeJestStderr handles null, undefined, and empty input defensively", () => {
    expect(decodeJestStderr(null)).toBeNull();
    expect(decodeJestStderr(undefined)).toBeNull();
    expect(decodeJestStderr("")).toBeNull();
    expect(decodeJestStderr(123)).toBeNull();
  });

  test("formatRepairBanner(null) returns empty string", () => {
    expect(formatRepairBanner(null)).toBe("");
    expect(formatRepairBanner(undefined)).toBe("");
  });

  test("formatRepairBanner contains every repair command and the skill reference", () => {
    const decoded = decodeJestStderr(
      "Module /tmp/whatever/runner.js in the testRunner option was not found."
    );
    const banner = formatRepairBanner(decoded);
    expect(typeof banner).toBe("string");
    expect(banner.length).toBeGreaterThan(0);
    for (const command of decoded.repairCommands) {
      expect(banner).toContain(command);
    }
    expect(banner).toContain(decoded.skillRef);
    expect(banner).toContain(decoded.summary);
    expect(banner).toContain(decoded.kind);
  });

  test("formatRepairBanner uses only ASCII (no Unicode box-drawing characters)", () => {
    for (const pattern of PATTERNS) {
      const decoded = {
        kind: pattern.kind,
        regex: pattern.regex,
        summary: pattern.summary,
        rootCauses: pattern.rootCauses,
        repairCommands: pattern.repairCommands,
        skillRef: pattern.skillRef,
        selfHeal: pattern.selfHeal,
        capturedMatch: null
      };
      const banner = formatRepairBanner(decoded);
      // Every codepoint must be in basic ASCII (printable + newline).
      // No Unicode box-drawing (U+2500..U+257F), no smart quotes, no
      // BMP glyphs above 0x7F.
      for (let index = 0; index < banner.length; index += 1) {
        const codePoint = banner.charCodeAt(index);
        const isAllowedAscii =
          codePoint === 0x0a || // \n
          (codePoint >= 0x20 && codePoint <= 0x7e);
        if (!isAllowedAscii) {
          throw new Error(
            `Banner for ${pattern.kind} contains non-ASCII codepoint 0x${codePoint.toString(16)} at index ${index}.`
          );
        }
      }
      // Spot-check: the box characters we DO use are present.
      expect(banner).toContain("=".repeat(64));
      expect(banner).toContain("-".repeat(64));
    }
  });

  test("formatRepairBanner color option respects injected env and isTTY", () => {
    const decoded = decodeJestStderr(
      "Module /tmp/whatever/runner.js in the testRunner option was not found."
    );

    // Case 1: CI=truthy forces uncolored output even when caller asks for color.
    const ciBanner = formatRepairBanner(decoded, {
      color: true,
      env: { CI: "true" },
      isTTY: true
    });
    expect(ciBanner.includes("\x1b[31m")).toBe(false);
    expect(ciBanner.includes("\x1b[0m")).toBe(false);

    // Case 2: not a TTY -> uncolored regardless of color flag.
    const noTtyBanner = formatRepairBanner(decoded, {
      color: true,
      env: {},
      isTTY: false
    });
    expect(noTtyBanner.includes("\x1b[31m")).toBe(false);

    // Case 3: TTY + no CI + color:true -> colored.
    const colorBanner = formatRepairBanner(decoded, {
      color: true,
      env: {},
      isTTY: true
    });
    expect(colorBanner.includes("\x1b[31m")).toBe(true);
    expect(colorBanner.includes("\x1b[0m")).toBe(true);

    // Case 4: TTY + no CI + color:false (default) -> uncolored.
    const noColorBanner = formatRepairBanner(decoded, {
      color: false,
      env: {},
      isTTY: true
    });
    expect(noColorBanner.includes("\x1b[31m")).toBe(false);
  });

  test("formatRepairBanner treats CI=0/false/no/off as falsy (does not suppress color)", () => {
    const decoded = decodeJestStderr(
      "Module /tmp/whatever/runner.js in the testRunner option was not found."
    );

    // The previous Boolean(env.CI) check treated CI="0" as truthy because
    // "0" is a non-empty string. isTruthyEnv treats it as falsy, which
    // matches typical shell-script truthiness intuition.
    for (const falsyValue of ["0", "false", "no", "off", "", "  "]) {
      const banner = formatRepairBanner(decoded, {
        color: true,
        env: { CI: falsyValue },
        isTTY: true
      });
      expect(banner.includes("\x1b[31m")).toBe(true);
    }
  });

  test("formatRepairBanner falls back to process.stderr.isTTY (not stdout) by default", () => {
    // The banner is written to stderr, so the TTY gate must reflect stderr.
    // We verify by spying on process.stderr.isTTY and asserting the
    // formatter consults it when `isTTY` is not passed explicitly.
    const decoded = decodeJestStderr(
      "Module /tmp/whatever/runner.js in the testRunner option was not found."
    );
    const originalStderrIsTty = process.stderr.isTTY;
    const originalStdoutIsTty = process.stdout.isTTY;
    try {
      // Force stderr=TTY, stdout=non-TTY. Colored output proves the
      // formatter looked at stderr (not stdout).
      Object.defineProperty(process.stderr, "isTTY", {
        configurable: true,
        value: true
      });
      Object.defineProperty(process.stdout, "isTTY", {
        configurable: true,
        value: false
      });
      const banner = formatRepairBanner(decoded, {
        color: true,
        env: {}
      });
      expect(banner.includes("\x1b[31m")).toBe(true);
    } finally {
      Object.defineProperty(process.stderr, "isTTY", {
        configurable: true,
        value: originalStderrIsTty
      });
      Object.defineProperty(process.stdout, "isTTY", {
        configurable: true,
        value: originalStdoutIsTty
      });
    }
  });
});

describe("isTruthyEnv", () => {
  test.each([
    [null, false],
    [undefined, false],
    ["", false],
    ["   ", false],
    ["0", false],
    ["false", false],
    ["FALSE", false],
    ["No", false],
    ["off", false],
    ["1", true],
    ["true", true],
    ["yes", true],
    ["on", true],
    ["arbitrary", true]
  ])("isTruthyEnv(%j) -> %s", (input, expected) => {
    expect(isTruthyEnv(input)).toBe(expected);
  });
});
