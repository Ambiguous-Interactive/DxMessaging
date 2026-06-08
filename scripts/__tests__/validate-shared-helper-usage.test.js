"use strict";

/**
 * Tests for the shared-helper duplication gate
 * (`scripts/validate-shared-helper-usage.js`).
 *
 * The most important test is the LIVE CONTRACT: `evaluate()` run against the
 * real repository must report zero violations and zero stale allowlist entries.
 * That single assertion keeps the ALLOWLIST honest forever -- a new duplicated
 * helper fails it, and an allowlist entry left behind after a migration also
 * fails it. The remaining tests pin the detection and classification logic with
 * synthetic inputs so a regression is attributable.
 */

const {
  BANNED_HELPERS,
  SHARED_HOMES,
  ALLOWLIST,
  buildDefinitionRegex,
  findLocalHelperDefinitions,
  classifyFindings,
  evaluate
} = require("../validate-shared-helper-usage");
const { listTrackedFiles } = require("../lib/repo-files");

describe("validate-shared-helper-usage: live repository contract", () => {
  test("the real repository has no undocumented duplicates and no stale allowlist entries", () => {
    const { violations, staleAllowlist } = evaluate();
    expect({ violations, staleAllowlist }).toEqual({ violations: [], staleAllowlist: [] });
  });

  test("every local definition the scan finds is an allowlisted (file, helper) pair", () => {
    for (const { file, helper } of findLocalHelperDefinitions()) {
      const entry = ALLOWLIST[file];
      expect(entry).toBeDefined();
      expect(entry.helpers).toContain(helper);
    }
  });
});

describe("validate-shared-helper-usage: definition detection", () => {
  const detect = (source) => {
    const regex = buildDefinitionRegex(BANNED_HELPERS);
    const names = [];
    let match;
    while ((match = regex.exec(source)) !== null) {
      names.push(match[1] || match[2]);
    }
    return names;
  };

  test("matches function declarations, const/let/var, and async functions", () => {
    expect(detect("function readUtf8(p) {}")).toEqual(["readUtf8"]);
    expect(detect("const walk = (d) => {};")).toEqual(["walk"]);
    expect(detect("let parseArgs = function () {};")).toEqual(["parseArgs"]);
    expect(detect("async function lineNumberAt(t, i) {}")).toEqual(["lineNumberAt"]);
    expect(detect("function toRepoRelative(a) {}")).toEqual(["toRepoRelative"]);
    expect(detect("function* walk(dir) {}")).toEqual(["walk"]);
    expect(detect("function*walk(dir) {}")).toEqual(["walk"]);
    expect(detect("async function* readUtf8(p) {}")).toEqual(["readUtf8"]);
    // A real separator is required: the glued identifier `functionwalk(` is a
    // call to an identifier, not a `function walk` definition.
    expect(detect("functionwalk(dir) {}")).toEqual([]);
  });

  test("does NOT match destructured imports or call sites (only definitions)", () => {
    expect(detect('const { readUtf8 } = require("./lib/repo-files");')).toEqual([]);
    expect(detect('const { parseArgs: parseCliArgs } = require("./lib/cli-options");')).toEqual([]);
    expect(detect("readUtf8(absolutePath);")).toEqual([]);
    expect(detect("const x = walk(dir);")).toEqual([]);
    // A member access such as obj.readUtf8 = ... is not a bare local definition.
    expect(detect("obj.parseArgs = function () {};")).toEqual([]);
    // A longer identifier sharing a prefix must not match.
    expect(detect("function toRepoRelativeKey(a) {}")).toEqual([]);
    expect(detect("const readUtf8Raw = (p) => p;")).toEqual([]);
  });
});

describe("validate-shared-helper-usage: classification logic", () => {
  test("an allowlisted (file, helper) pair is not a violation and is not stale", () => {
    const found = [{ file: "scripts/a.js", helper: "parseArgs", line: 10 }];
    const allowlist = { "scripts/a.js": { helpers: ["parseArgs"], reason: "bespoke" } };
    expect(classifyFindings(found, allowlist)).toEqual({ violations: [], staleAllowlist: [] });
  });

  test("a found definition with no allowlist entry is a violation", () => {
    const found = [{ file: "scripts/new.js", helper: "walk", line: 7 }];
    const { violations, staleAllowlist } = classifyFindings(found, {});
    expect(staleAllowlist).toEqual([]);
    expect(violations).toHaveLength(1);
    expect(violations[0]).toContain("scripts/new.js:7");
    expect(violations[0]).toContain('local "walk"');
  });

  test("an allowlist entry with no matching definition is stale", () => {
    const allowlist = { "scripts/gone.js": { helpers: ["readUtf8"], reason: "removed" } };
    const { violations, staleAllowlist } = classifyFindings([], allowlist);
    expect(violations).toEqual([]);
    expect(staleAllowlist).toHaveLength(1);
    expect(staleAllowlist[0]).toContain("scripts/gone.js");
    expect(staleAllowlist[0]).toContain("readUtf8");
  });

  test("allowlisting helper A does not excuse a different helper B in the same file", () => {
    const found = [{ file: "scripts/a.js", helper: "walk", line: 3 }];
    const allowlist = { "scripts/a.js": { helpers: ["parseArgs"], reason: "bespoke" } };
    const { violations, staleAllowlist } = classifyFindings(found, allowlist);
    expect(violations).toHaveLength(1);
    expect(violations[0]).toContain('local "walk"');
    // parseArgs was allowlisted but never found -> stale.
    expect(staleAllowlist).toHaveLength(1);
    expect(staleAllowlist[0]).toContain("parseArgs");
  });
});

describe("validate-shared-helper-usage: ALLOWLIST well-formedness", () => {
  const trackedScripts = new Set(listTrackedFiles(["scripts"]));

  test("every allowlisted file is a tracked file that is not a shared home", () => {
    for (const file of Object.keys(ALLOWLIST)) {
      expect(trackedScripts.has(file)).toBe(true);
      expect(SHARED_HOMES.has(file)).toBe(false);
    }
  });

  test("every allowlisted helper is one of the banned helpers, with a non-empty reason", () => {
    for (const [file, entry] of Object.entries(ALLOWLIST)) {
      expect(Array.isArray(entry.helpers)).toBe(true);
      expect(entry.helpers.length).toBeGreaterThan(0);
      expect(new Set(entry.helpers).size).toBe(entry.helpers.length);
      for (const helper of entry.helpers) {
        expect(BANNED_HELPERS).toContain(helper);
      }
      expect(typeof entry.reason).toBe("string");
      expect(entry.reason.trim().length).toBeGreaterThan(0);
      expect(file).toBe(file.replace(/\\/g, "/"));
    }
  });
});
