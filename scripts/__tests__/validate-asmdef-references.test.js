/**
 * @fileoverview Tests for validate-asmdef-references.js.
 *
 * Covers:
 *   - The PURE check (findAsmdefReferenceViolations) on synthetic asmdef
 *     objects: the violation, and the three OK shapes.
 *   - Defensive handling of missing keys and malformed input.
 *   - asmdef ownership classification (package subtrees vs. cache/third-party).
 *   - Hard-failure (not silent pass) on read/parse errors via string paths.
 *   - An end-to-end pass over the REAL repo asmdefs, which must currently PASS
 *     (the Runtime asmdef was fixed to precompiledReferences: []).
 */

"use strict";

const {
  PACKAGE_SOURCE_PREFIXES,
  SECONDARY_SCOPE_NOTE,
  isOwnPackageAsmdef,
  getOwnPackageAsmdefPaths,
  resolveOverrideReferences,
  resolvePrecompiledReferences,
  findAsmdefReferenceViolations,
  validateAsmdefReferences
} = require("../validate-asmdef-references.js");

// The exact fingerprint of the bug that caused the IL2CPP/standalone failure.
const VIOLATION_ASMDEF = {
  name: "WallstopStudios.DxMessaging",
  overrideReferences: false,
  precompiledReferences: ["System.Runtime.CompilerServices.Unsafe.dll"]
};

// The Runtime asmdef as it now stands (fixed): empty precompiledReferences.
const FIXED_RUNTIME_ASMDEF = {
  name: "WallstopStudios.DxMessaging",
  overrideReferences: false,
  precompiledReferences: []
};

// A Tests-style asmdef: actively overriding, so the reference is honored. OK.
const OVERRIDING_ASMDEF = {
  name: "WallstopStudios.DxMessaging.Tests.Editor",
  overrideReferences: true,
  precompiledReferences: ["nunit.framework.dll"]
};

describe("validate-asmdef-references", () => {
  afterEach(() => {
    jest.restoreAllMocks();
  });

  describe("findAsmdefReferenceViolations (PURE)", () => {
    test("flags non-empty precompiledReferences with overrideReferences:false", () => {
      const violations = findAsmdefReferenceViolations([
        { path: "Runtime/WallstopStudios.DxMessaging.asmdef", asmdef: VIOLATION_ASMDEF }
      ]);

      expect(violations).toHaveLength(1);
      expect(violations[0]).toEqual(
        expect.objectContaining({
          type: "dead-precompiled-references",
          path: "Runtime/WallstopStudios.DxMessaging.asmdef",
          ignoredReferences: ["System.Runtime.CompilerServices.Unsafe.dll"]
        })
      );
      // Message must name the path, the ignored reference, and explain the fix.
      expect(violations[0].message).toContain("Runtime/WallstopStudios.DxMessaging.asmdef");
      expect(violations[0].message).toContain("System.Runtime.CompilerServices.Unsafe.dll");
      expect(violations[0].message).toContain("overrideReferences");
      expect(violations[0].message.toLowerCase()).toContain("il2cpp");
      expect(violations[0].message).toMatch(/runtime Plugins folder/i);
    });

    test("passes empty precompiledReferences with overrideReferences:false (the fixed asmdef)", () => {
      const violations = findAsmdefReferenceViolations([
        { path: "Runtime/WallstopStudios.DxMessaging.asmdef", asmdef: FIXED_RUNTIME_ASMDEF }
      ]);
      expect(violations).toEqual([]);
    });

    test("passes non-empty precompiledReferences when overrideReferences:true", () => {
      const violations = findAsmdefReferenceViolations([
        { path: "Tests/Editor/Foo.asmdef", asmdef: OVERRIDING_ASMDEF }
      ]);
      expect(violations).toEqual([]);
    });

    test("accepts bare asmdef objects and synthesizes a display path", () => {
      const violations = findAsmdefReferenceViolations([VIOLATION_ASMDEF]);
      expect(violations).toHaveLength(1);
      expect(violations[0].path).toBe("<asmdef[0]>");
    });

    test("treats missing overrideReferences as false (still a violation)", () => {
      const violations = findAsmdefReferenceViolations([
        { path: "a.asmdef", asmdef: { precompiledReferences: ["X.dll"] } }
      ]);
      expect(violations).toHaveLength(1);
      expect(violations[0].ignoredReferences).toEqual(["X.dll"]);
    });

    test("treats missing precompiledReferences as empty (no violation)", () => {
      const violations = findAsmdefReferenceViolations([
        { path: "a.asmdef", asmdef: { overrideReferences: false } },
        { path: "b.asmdef", asmdef: {} }
      ]);
      expect(violations).toEqual([]);
    });

    test("does not treat overrideReferences:true + empty refs as a violation", () => {
      const violations = findAsmdefReferenceViolations([
        { path: "a.asmdef", asmdef: { overrideReferences: true, precompiledReferences: [] } }
      ]);
      expect(violations).toEqual([]);
    });

    test("reports each violating asmdef independently", () => {
      const violations = findAsmdefReferenceViolations([
        { path: "a.asmdef", asmdef: VIOLATION_ASMDEF },
        { path: "b.asmdef", asmdef: FIXED_RUNTIME_ASMDEF },
        { path: "c.asmdef", asmdef: { precompiledReferences: ["Y.dll", "Z.dll"] } }
      ]);
      expect(violations.map((violation) => violation.path)).toEqual(["a.asmdef", "c.asmdef"]);
      expect(violations[1].ignoredReferences).toEqual(["Y.dll", "Z.dll"]);
    });

    test("throws on non-array input rather than silently passing", () => {
      expect(() => findAsmdefReferenceViolations(null)).toThrow(/expects an array/);
    });

    test("hard-fails (throws) on unreadable string-path entries", () => {
      const readFileSync = jest.fn(() => {
        throw new Error("ENOENT: no such file");
      });
      expect(() =>
        findAsmdefReferenceViolations(["Runtime/Missing.asmdef"], { readFileSync })
      ).toThrow(/Unable to read asmdef 'Runtime\/Missing.asmdef'/);
    });

    test("hard-fails (throws) on malformed JSON rather than silently passing", () => {
      const readFileSync = jest.fn(() => "{ not valid json ");
      expect(() => findAsmdefReferenceViolations(["Runtime/Bad.asmdef"], { readFileSync })).toThrow(
        /Unable to parse asmdef 'Runtime\/Bad.asmdef' as JSON/
      );
    });

    test("reads and flags a violation through a string path + injected fs", () => {
      const readFileSync = jest.fn(() => JSON.stringify(VIOLATION_ASMDEF));
      const violations = findAsmdefReferenceViolations(
        ["Runtime/WallstopStudios.DxMessaging.asmdef"],
        {
          readFileSync
        }
      );
      expect(readFileSync).toHaveBeenCalledTimes(1);
      expect(violations).toHaveLength(1);
      expect(violations[0].path).toBe("Runtime/WallstopStudios.DxMessaging.asmdef");
    });
  });

  describe("field coercion helpers", () => {
    test("resolveOverrideReferences only true for strict boolean true", () => {
      expect(resolveOverrideReferences(true)).toBe(true);
      expect(resolveOverrideReferences(false)).toBe(false);
      expect(resolveOverrideReferences(undefined)).toBe(false);
      expect(resolveOverrideReferences("true")).toBe(false);
      expect(resolveOverrideReferences(1)).toBe(false);
    });

    test("resolvePrecompiledReferences coerces to a string array", () => {
      expect(resolvePrecompiledReferences(undefined)).toEqual([]);
      expect(resolvePrecompiledReferences("X.dll")).toEqual([]);
      expect(resolvePrecompiledReferences(["X.dll"])).toEqual(["X.dll"]);
      expect(resolvePrecompiledReferences([1, "Y.dll"])).toEqual(["1", "Y.dll"]);
    });
  });

  describe("isOwnPackageAsmdef", () => {
    test("accepts package subtree asmdefs", () => {
      expect(isOwnPackageAsmdef("Runtime/WallstopStudios.DxMessaging.asmdef")).toBe(true);
      expect(isOwnPackageAsmdef("Editor/WallstopStudios.DxMessaging.Editor.asmdef")).toBe(true);
      expect(
        isOwnPackageAsmdef("Tests/Editor/WallstopStudios.DxMessaging.Tests.Editor.asmdef")
      ).toBe(true);
      expect(isOwnPackageAsmdef("Samples~/Mini Combat/Foo.Sample.asmdef")).toBe(true);
    });

    test("rejects cache/third-party trees and non-asmdef files", () => {
      expect(isOwnPackageAsmdef("node_modules/pkg/Foo.asmdef")).toBe(false);
      expect(isOwnPackageAsmdef(".unity-test-project/Library/PackageCache/x/Foo.asmdef")).toBe(
        false
      );
      expect(isOwnPackageAsmdef("Runtime/Foo.cs")).toBe(false);
      expect(isOwnPackageAsmdef("Runtime/Foo.asmref")).toBe(false);
    });

    test("normalizes Windows-style separators before classifying", () => {
      expect(isOwnPackageAsmdef("Runtime\\Sub\\Foo.asmdef")).toBe(true);
      expect(isOwnPackageAsmdef("node_modules\\pkg\\Foo.asmdef")).toBe(false);
    });

    test("PACKAGE_SOURCE_PREFIXES covers exactly the package's own roots", () => {
      expect(PACKAGE_SOURCE_PREFIXES).toEqual(["Runtime/", "Editor/", "Tests/", "Samples~/"]);
    });
  });

  describe("getOwnPackageAsmdefPaths", () => {
    test("merges tracked + untracked, filters to package asmdefs, sorts & dedupes", () => {
      const execFileSync = jest
        .fn()
        // First call: tracked `*.asmdef`.
        .mockReturnValueOnce(
          [
            "Runtime/WallstopStudios.DxMessaging.asmdef",
            "Tests/Editor/Foo.asmdef",
            "node_modules/pkg/Vendor.asmdef",
            ".unity-test-project/Library/PackageCache/x/Cached.asmdef"
          ].join("\n")
        )
        // Second call: untracked `*.asmdef`.
        .mockReturnValueOnce(
          ["Editor/New.asmdef", "Runtime/WallstopStudios.DxMessaging.asmdef"].join("\n")
        );

      const paths = getOwnPackageAsmdefPaths(execFileSync);

      expect(paths).toEqual([
        "Editor/New.asmdef",
        "Runtime/WallstopStudios.DxMessaging.asmdef",
        "Tests/Editor/Foo.asmdef"
      ]);
      expect(execFileSync).toHaveBeenCalledTimes(2);
      expect(execFileSync).toHaveBeenNthCalledWith(
        1,
        "git",
        ["ls-files", "*.asmdef"],
        expect.any(Object)
      );
      expect(execFileSync).toHaveBeenNthCalledWith(
        2,
        "git",
        ["ls-files", "--others", "--exclude-standard", "*.asmdef"],
        expect.any(Object)
      );
    });

    test("propagates git failures as hard errors (never silently empty)", () => {
      const execFileSync = jest.fn(() => {
        throw new Error("git not found");
      });
      expect(() => getOwnPackageAsmdefPaths(execFileSync)).toThrow(/git not found/);
    });
  });

  describe("validateAsmdefReferences (orchestration)", () => {
    test("returns invalid and lists violations without exiting when not in --check", () => {
      jest.spyOn(console, "error").mockImplementation(() => {});
      const execFileSync = jest
        .fn()
        .mockReturnValueOnce("Runtime/WallstopStudios.DxMessaging.asmdef\n")
        .mockReturnValueOnce("");
      const readFileSync = jest.fn(() => JSON.stringify(VIOLATION_ASMDEF));

      const result = validateAsmdefReferences({ execFileSync, readFileSync, check: false });

      expect(result.valid).toBe(false);
      expect(result.violations).toHaveLength(1);
      expect(result.scanned).toEqual(["Runtime/WallstopStudios.DxMessaging.asmdef"]);
    });

    test("returns valid when every scanned asmdef is clean", () => {
      jest.spyOn(console, "log").mockImplementation(() => {});
      const execFileSync = jest
        .fn()
        .mockReturnValueOnce("Runtime/WallstopStudios.DxMessaging.asmdef\n")
        .mockReturnValueOnce("");
      const readFileSync = jest.fn(() => JSON.stringify(FIXED_RUNTIME_ASMDEF));

      const result = validateAsmdefReferences({ execFileSync, readFileSync, check: false });

      expect(result.valid).toBe(true);
      expect(result.violations).toEqual([]);
    });
  });

  describe("SECONDARY scope", () => {
    test("explicitly documents that SECONDARY is out of scope", () => {
      expect(SECONDARY_SCOPE_NOTE).toMatch(/out of scope/i);
      expect(SECONDARY_SCOPE_NOTE).toMatch(/nunit/i);
    });
  });

  describe("end-to-end against the real repository", () => {
    test("the package's own asmdefs currently PASS the guard", () => {
      // Real discovery + real fs read. The Runtime asmdef was fixed to
      // precompiledReferences: [], so this must pass today and will fail loudly
      // if the dead-config pattern ever regresses into a package asmdef.
      const asmdefPaths = getOwnPackageAsmdefPaths();
      expect(asmdefPaths.length).toBeGreaterThan(0);
      expect(asmdefPaths).toContain("Runtime/WallstopStudios.DxMessaging.asmdef");

      const violations = findAsmdefReferenceViolations(asmdefPaths);
      if (violations.length > 0) {
        // Surface the offending messages if this ever regresses.
        throw new Error(
          `Expected zero asmdef violations but found:\n${violations
            .map((violation) => `  - ${violation.message}`)
            .join("\n")}`
        );
      }
      expect(violations).toEqual([]);
    });
  });
});
