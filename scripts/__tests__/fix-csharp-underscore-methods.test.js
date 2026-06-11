"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const {
  isCsharpSourceFile,
  convertMethodNameToPascalCase,
  collectMethodRenames,
  applyMethodRenames,
  processFile,
  isPathInsideRoot,
  isExcludedRepoLocalPath
} = require("../fix-csharp-underscore-methods.js");

function fakeRealpathSync(mapping) {
  const realpathSync = (fullPath) => {
    const key = fullPath.toLowerCase();
    if (mapping.has(key)) {
      return mapping.get(key);
    }

    const error = new Error(`ENOENT: no such file or directory, realpath '${fullPath}'`);
    error.code = "ENOENT";
    throw error;
  };

  realpathSync.native = realpathSync;
  return realpathSync;
}

test("convertMethodNameToPascalCase joins segments in PascalCase", () => {
  assert.equal(convertMethodNameToPascalCase("Parse_Line_Bare"), "ParseLineBare");
  assert.equal(convertMethodNameToPascalCase("handles_empty_input"), "HandlesEmptyInput");
  assert.equal(convertMethodNameToPascalCase("a_b"), "AB");
  assert.equal(convertMethodNameToPascalCase("Already__Doubled"), "AlreadyDoubled");
});

test("isCsharpSourceFile accepts .cs and rejects .meta and other files", () => {
  assert.equal(isCsharpSourceFile("Runtime/Foo.cs"), true);
  assert.equal(isCsharpSourceFile("Runtime/Foo.CS"), true);
  assert.equal(isCsharpSourceFile("Runtime/Foo.cs.meta"), false);
  assert.equal(isCsharpSourceFile("Runtime/Foo.js"), false);
  assert.equal(isCsharpSourceFile(""), false);
});

test("collectMethodRenames finds underscore methods and skips op_ names", () => {
  const source = [
    "public class Sample {",
    "    public void Handles_Empty_Input() {}",
    "    private static int Count_Items(int x) { return x; }",
    "    public static bool op_Equality(Sample a, Sample b) { return true; }",
    "    public void AlreadyClean() {}",
    "}"
  ].join("\n");

  const renames = collectMethodRenames(source);
  assert.deepEqual(
    [...renames.entries()].sort(),
    [
      ["Count_Items", "CountItems"],
      ["Handles_Empty_Input", "HandlesEmptyInput"]
    ].sort()
  );
});

test("applyMethodRenames renames declarations and call sites only on word boundaries", () => {
  const source = [
    "public void Do_Work() {}",
    "void Caller() { Do_Work(); NotDo_Work(); other.Do_Work(); }"
  ].join("\n");
  const renames = collectMethodRenames(source);
  const { updatedContent, renameCount } = applyMethodRenames(source, renames);

  assert.equal(renameCount, 1);
  assert.ok(updatedContent.includes("public void DoWork() {}"));
  assert.ok(updatedContent.includes("DoWork();"));
  assert.ok(updatedContent.includes("other.DoWork();"));
  // Larger identifiers containing the old name are untouched.
  assert.ok(updatedContent.includes("NotDo_Work();"));
});

test("collectMethodRenames ignores clean sources", () => {
  const source = "public class Clean {\n    public void DoWork() {}\n}";
  assert.equal(collectMethodRenames(source).size, 0);
});

test("processFile rewrites files unless checkOnly is set", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "cs-fixer-"));
  try {
    const file = path.join(dir, "Sample.cs");
    const original = "public class S {\n    public void Run_Once() {}\n}\n";
    fs.writeFileSync(file, original, "utf8");

    const checkResult = processFile(file, true);
    assert.equal(checkResult.changed, true);
    assert.equal(fs.readFileSync(file, "utf8"), original);

    const fixResult = processFile(file, false);
    assert.equal(fixResult.changed, true);
    assert.equal(fixResult.renameCount, 1);
    assert.ok(fs.readFileSync(file, "utf8").includes("public void RunOnce()"));

    const repeat = processFile(file, false);
    assert.equal(repeat.changed, false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("explicit-file path filters use robust Windows containment", () => {
  const realpathSync = fakeRealpathSync(
    new Map([
      ["c:\\repo", "\\\\?\\C:\\Repo"],
      ["c:\\repo\\.artifacts", "\\\\?\\C:\\Repo\\.artifacts"]
    ])
  );
  const options = { realpathSync };

  const insideCases = [
    ["missing repo-local file", "C:\\Repo\\Runtime\\Missing.cs", true],
    ["repo root itself", "C:\\Repo", true],
    ["outside sibling", "C:\\Other\\Runtime\\File.cs", false],
    ["cross-drive file", "D:\\Repo\\Runtime\\File.cs", false]
  ];

  for (const [name, fullPath, expected] of insideCases) {
    assert.equal(isPathInsideRoot("C:\\Repo", fullPath, options), expected, name);
  }

  const excludedCases = [
    ["missing file under excluded directory", "C:\\Repo\\.artifacts\\Generated.cs", true],
    ["normal source file", "C:\\Repo\\Runtime\\Generated.cs", false],
    ["outside file is not repo-local excluded", "C:\\Other\\.artifacts\\Generated.cs", false]
  ];

  for (const [name, fullPath, expected] of excludedCases) {
    assert.equal(isExcludedRepoLocalPath("C:\\Repo", fullPath, options), expected, name);
  }
});
