"use strict";
// prettier-ignore
const { test } = require("node:test"), assert = require("node:assert/strict"), path = require("node:path"), { spawnSync } = require("node:child_process");
// prettier-ignore
test("PowerShell Unity shipping and WebGL regression suites", () => {
  for (const file of ["shipping-fidelity.test.ps1", "webgl-smoke.test.ps1"]) { const result = spawnSync("pwsh", ["-NoLogo", "-NoProfile", "-File", path.join(__dirname, file)], { cwd: path.resolve(__dirname, "../../.."), encoding: "utf8", maxBuffer: 16 * 1024 * 1024 }); assert.ifError(result.error); assert.equal(result.status, 0, result.stdout + result.stderr); }
});
