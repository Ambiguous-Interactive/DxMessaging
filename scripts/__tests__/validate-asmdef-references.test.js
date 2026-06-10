"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");

const {
  isOwnPackageAsmdef,
  resolveOverrideReferences,
  resolvePrecompiledReferences,
  findAsmdefReferenceViolations
} = require("../validate-asmdef-references.js");

test("isOwnPackageAsmdef accepts only package-owned asmdef paths", () => {
  assert.equal(isOwnPackageAsmdef("Runtime/Core/My.asmdef"), true);
  assert.equal(isOwnPackageAsmdef("Editor/Tools.asmdef"), true);
  assert.equal(isOwnPackageAsmdef("Tests/Runtime/Tests.asmdef"), true);
  assert.equal(isOwnPackageAsmdef("Samples~/Demo/Demo.asmdef"), true);
  assert.equal(isOwnPackageAsmdef("Runtime\\Core\\My.asmdef"), true);

  assert.equal(isOwnPackageAsmdef("node_modules/pkg/Some.asmdef"), false);
  assert.equal(isOwnPackageAsmdef(".unity-test-project/Library/PackageCache/X.asmdef"), false);
  assert.equal(isOwnPackageAsmdef("Runtime/Core/My.asmdef.meta"), false);
  assert.equal(isOwnPackageAsmdef("Runtime/Core/My.json"), false);
});

test("resolveOverrideReferences defaults to false for anything but true", () => {
  assert.equal(resolveOverrideReferences(true), true);
  assert.equal(resolveOverrideReferences(false), false);
  assert.equal(resolveOverrideReferences(undefined), false);
  assert.equal(resolveOverrideReferences("true"), false);
  assert.equal(resolveOverrideReferences(1), false);
});

test("resolvePrecompiledReferences coerces to a string array", () => {
  assert.deepEqual(resolvePrecompiledReferences(["a.dll", "b.dll"]), ["a.dll", "b.dll"]);
  assert.deepEqual(resolvePrecompiledReferences(undefined), []);
  assert.deepEqual(resolvePrecompiledReferences("a.dll"), []);
  assert.deepEqual(resolvePrecompiledReferences([42]), ["42"]);
});

test("flags non-empty precompiledReferences with overrideReferences false", () => {
  const violations = findAsmdefReferenceViolations([
    {
      path: "Runtime/Bad.asmdef",
      asmdef: {
        name: "Bad",
        overrideReferences: false,
        precompiledReferences: ["System.Runtime.CompilerServices.Unsafe.dll"]
      }
    }
  ]);

  assert.equal(violations.length, 1);
  assert.equal(violations[0].type, "dead-precompiled-references");
  assert.equal(violations[0].path, "Runtime/Bad.asmdef");
  assert.deepEqual(violations[0].ignoredReferences, ["System.Runtime.CompilerServices.Unsafe.dll"]);
  assert.ok(violations[0].message.includes("overrideReferences"));
});

test("flags missing overrideReferences the same as false", () => {
  const violations = findAsmdefReferenceViolations([
    { name: "Implicit", precompiledReferences: ["nunit.framework.dll"] }
  ]);
  assert.equal(violations.length, 1);
  assert.equal(violations[0].path, "<asmdef[0]>");
});

test("accepts overrideReferences true and empty precompiledReferences", () => {
  const violations = findAsmdefReferenceViolations([
    {
      path: "Tests/Good.asmdef",
      asmdef: {
        name: "Good",
        overrideReferences: true,
        precompiledReferences: ["nunit.framework.dll"]
      }
    },
    { path: "Runtime/None.asmdef", asmdef: { name: "None" } },
    { path: "Runtime/Empty.asmdef", asmdef: { name: "Empty", precompiledReferences: [] } }
  ]);
  assert.deepEqual(violations, []);
});

test("reports each violating entry across a mixed batch", () => {
  const violations = findAsmdefReferenceViolations([
    { path: "Runtime/A.asmdef", asmdef: { precompiledReferences: ["a.dll"] } },
    {
      path: "Runtime/B.asmdef",
      asmdef: { overrideReferences: true, precompiledReferences: ["b.dll"] }
    },
    {
      path: "Runtime/C.asmdef",
      asmdef: { overrideReferences: false, precompiledReferences: ["c.dll"] }
    }
  ]);
  assert.deepEqual(
    violations.map((violation) => violation.path),
    ["Runtime/A.asmdef", "Runtime/C.asmdef"]
  );
});

test("rejects non-array input and invalid entries", () => {
  assert.throws(() => findAsmdefReferenceViolations("not-an-array"), /expects an array/);
  assert.throws(() => findAsmdefReferenceViolations([42]), /Invalid asmdef input at index 0/);
});
