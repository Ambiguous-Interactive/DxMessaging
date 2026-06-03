/**
 * @fileoverview Unit tests for scripts/lib/node-require-scan.js -- the static
 * module-specifier scanner behind the dependency-hygiene guards. This file is a
 * legitimate exception to the "no module specifiers in strings" intuition: it
 * deliberately embeds `require(...)` calls inside string literals, comments, and
 * regex to prove the scanner does NOT mistake them for real imports (the exact
 * shape node-modules-integrity.js uses when it builds probe code as a string).
 */

"use strict";

const {
  extractModuleSpecifiers,
  packageNameOf,
  classifySpecifier
} = require("../node-require-scan");

describe("extractModuleSpecifiers", () => {
  test("captures require / import / dynamic-import / from specifiers", () => {
    const src = [
      'const a = require("yaml");',
      "const b = require('node:fs');",
      'import c from "prettier";',
      'export { x } from "./local";',
      'const d = await import("cspell");',
      'import "side-effect-pkg";'
    ].join("\n");
    expect(new Set(extractModuleSpecifiers(src))).toEqual(
      new Set(["yaml", "node:fs", "prettier", "./local", "cspell", "side-effect-pkg"])
    );
  });

  test("ignores require specifiers that appear inside string literals", () => {
    const src = [
      'const probe = "require(\\"unrs-resolver\\")";',
      "const t = `require('jest-resolve')`;",
      'const real = require("yaml");'
    ].join("\n");
    expect(extractModuleSpecifiers(src)).toEqual(["yaml"]);
  });

  test("ignores require specifiers inside line and block comments", () => {
    const src = [
      '// require("commented-out")',
      "/* require('block-commented') */",
      'const real = require("yaml");'
    ].join("\n");
    expect(extractModuleSpecifiers(src)).toEqual(["yaml"]);
  });

  test("ignores quotes inside regex literals without breaking string tracking", () => {
    const src = ["const re = /require\\([\"']x[\"']\\)/g;", 'const real = require("yaml");'].join(
      "\n"
    );
    expect(extractModuleSpecifiers(src)).toEqual(["yaml"]);
  });

  test("treats division as division, not a regex (no false scrub)", () => {
    const src = ['const x = a / b; const real = require("yaml"); const y = c / d;'].join("\n");
    expect(extractModuleSpecifiers(src)).toEqual(["yaml"]);
  });

  test("ignores a regex after an expression keyword (return/typeof) and keeps the next real import", () => {
    const src = 'function f(){ return /require("ghost")/.test(x); }\nrequire("yaml");';
    expect(extractModuleSpecifiers(src)).toEqual(["yaml"]);
  });

  test("does NOT treat property/method calls as imports", () => {
    const src = [
      'loader.require("ghost-method");',
      'obj.import("ghost-import");',
      "db.from`ghost-table`;",
      'qb.from "ghost-table";',
      'const real = require("yaml");'
    ].join("\n");
    expect(extractModuleSpecifiers(src)).toEqual(["yaml"]);
  });

  test("matches `import/export ... from` but not a `.from` member access", () => {
    expect(extractModuleSpecifiers('import x from "pkg";')).toEqual(["pkg"]);
    expect(extractModuleSpecifiers('export { a } from "pkg";')).toEqual(["pkg"]);
    expect(extractModuleSpecifiers('export * from "pkg";')).toEqual(["pkg"]);
    expect(extractModuleSpecifiers("import x from`pkg`;")).toEqual(["pkg"]);
    expect(extractModuleSpecifiers("db.from`ghost`;")).toEqual([]);
    expect(extractModuleSpecifiers('Array.from("ghost");')).toEqual([]);
  });

  test("captures a real require inside a template interpolation", () => {
    expect(extractModuleSpecifiers('const t = `${require("yaml")}`;')).toEqual(["yaml"]);
  });

  test("captures a constant backtick specifier but not an interpolated one", () => {
    expect(extractModuleSpecifiers("require(`yaml`);")).toEqual(["yaml"]);
    expect(extractModuleSpecifiers("require(`${pkgName}`);")).toEqual([]);
  });

  test("returns empty for a self-contained module", () => {
    const src = 'const path = require("node:path");\nmodule.exports = {};';
    expect(extractModuleSpecifiers(src)).toEqual(["node:path"]);
  });
});

describe("packageNameOf", () => {
  test.each([
    ["yaml", "yaml"],
    ["yaml/util", "yaml"],
    ["@scope/pkg", "@scope/pkg"],
    ["@scope/pkg/sub/path", "@scope/pkg"]
  ])("%s -> %s", (input, expected) => {
    expect(packageNameOf(input)).toBe(expected);
  });
});

describe("classifySpecifier", () => {
  test.each([
    ["fs", "builtin"],
    ["node:fs", "builtin"],
    ["fs/promises", "builtin"],
    ["./local", "relative"],
    ["../up", "relative"],
    ["/abs/path", "relative"],
    ["yaml", "thirdparty"],
    ["@scope/pkg", "thirdparty"]
  ])("%s -> %s", (spec, kind) => {
    expect(classifySpecifier(spec).kind).toBe(kind);
  });

  test("third-party classification reports the installable package name", () => {
    expect(classifySpecifier("@scope/pkg/sub")).toEqual({
      kind: "thirdparty",
      packageName: "@scope/pkg"
    });
  });
});
