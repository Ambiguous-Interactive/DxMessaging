"use strict";
const { test } = require("node:test"),
  assert = require("node:assert/strict");
const path = require("node:path"),
  { spawnSync } = require("node:child_process");
// prettier-ignore
test("PowerShell capture-dispatch-codegen regression suite", () => {
  const result = spawnSync("pwsh", ["-NoLogo", "-NoProfile", "-File", path.resolve(__dirname, "../capture-dispatch-codegen.ps1"), "-SelfTestOnly"], { cwd: path.resolve(__dirname, "../../.."), encoding: "utf8", maxBuffer: 16 * 1024 * 1024 });
  assert.ifError(result.error); assert.equal(result.status, 0, result.stdout + result.stderr);
});
