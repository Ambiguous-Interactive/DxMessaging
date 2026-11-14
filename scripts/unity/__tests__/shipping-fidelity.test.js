"use strict";
// prettier-ignore
const { test } = require("node:test"), assert = require("node:assert/strict"), path = require("node:path"), { spawnSync } = require("node:child_process");
// prettier-ignore
test("PowerShell shipping-fidelity regression suite", () => {
  const result = spawnSync("pwsh", ["-NoLogo", "-NoProfile", "-File", path.join(__dirname, "shipping-fidelity.test.ps1")], { cwd: path.resolve(__dirname, "../../.."), encoding: "utf8", maxBuffer: 16 * 1024 * 1024 });
  assert.ifError(result.error); assert.equal(result.status, 0, result.stdout + result.stderr);
});
