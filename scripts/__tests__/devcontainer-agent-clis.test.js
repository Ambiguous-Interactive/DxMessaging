"use strict";
// cspell:ignore mcps
const assert = require("node:assert/strict");
const childProcess = require("node:child_process");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { test } = require("node:test");
// The devcontainer shell requires POSIX; structural tests run on Windows too.
const CAN_RUN_SHELL = process.platform !== "win32";
const ROOT = path.resolve(__dirname, "..", "..");

// Expose only these system tools so image-provided CLIs cannot satisfy the fixture.
const SYSTEM_TOOLS = [
  "bash",
  "sh",
  "mkdir",
  "grep",
  "head",
  "tr",
  "cat",
  "rm",
  "chmod",
  "flock",
  "node",
  "readlink"
];
const LEGACY_OPENCODE_LINK = "../lib/node_modules/opencode-ai/bin/opencode.exe";

function resolveTool(name) {
  const found = childProcess.spawnSync("sh", ["-c", `command -v ${name}`], { encoding: "utf8" });
  return found.status === 0 ? found.stdout.trim() : undefined;
}
const dev = (name) => path.join(ROOT, ".devcontainer", name);
const read = (name) => fs.readFileSync(dev(name), "utf8");

const fixture = dev("test-agent-clis-fixture.sh");

function writeExecutable(file, contents) {
  fs.writeFileSync(file, contents);
  fs.chmodSync(file, 0o755);
}

// Builds a hermetic sandbox (own prefix, own TMPDIR so the flock path cannot
// collide with a sibling case) and runs the installer inside it.
function runInstaller(t, setup) {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-agent-clis-"));
  t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
  const prefixBin = path.join(temp, "prefix", "bin");
  const stubBin = path.join(temp, "stub-bin");
  fs.mkdirSync(prefixBin, { recursive: true });
  fs.mkdirSync(stubBin, { recursive: true });
  writeExecutable(path.join(stubBin, "npm"), fs.readFileSync(fixture, "utf8"));
  writeExecutable(path.join(stubBin, "sleep"), "#!/usr/bin/env bash\nexit 0\n");
  // macOS lacks timeout; emulate it without adding latency to fixture retries.
  writeExecutable(path.join(stubBin, "timeout"), '#!/usr/bin/env bash\nshift\nexec "$@"\n');
  if (setup.installed) {
    for (const [, command] of PACKAGES) {
      writeExecutable(
        path.join(prefixBin, command),
        `#!/usr/bin/env bash\necho ${setup.installed}\n`
      );
    }
  }
  const opencode = path.join(prefixBin, "opencode");
  if (setup.legacySystem) {
    fs.unlinkSync(opencode);
    writeExecutable(path.join(stubBin, "opencode"), "#!/usr/bin/env bash\necho 1.19.6\n");
  }
  if (setup.legacyUser) {
    const legacy = path.join(prefixBin, LEGACY_OPENCODE_LINK);
    fs.mkdirSync(path.dirname(legacy), { recursive: true });
    writeExecutable(legacy, "#!/usr/bin/env bash\necho 1.19.6\n");
    fs.unlinkSync(opencode);
    fs.symlinkSync(LEGACY_OPENCODE_LINK, opencode);
  }
  const systemBin = path.join(temp, "system-bin");
  fs.mkdirSync(systemBin, { recursive: true });
  for (const tool of SYSTEM_TOOLS) {
    const resolved = resolveTool(tool);
    if (resolved) {
      fs.symlinkSync(resolved, path.join(systemBin, tool));
    }
  }
  const sandboxPath = `${prefixBin}:${stubBin}:${systemBin}`;
  if (!setup.installed) {
    for (const [, command] of PACKAGES) {
      // Resolve the shell first; assert non-success (bash returns 1, dash 127).
      assert.notEqual(
        childProcess.spawnSync("sh", ["-c", `PATH="${sandboxPath}" command -v ${command}`], {
          encoding: "utf8"
        }).status,
        0,
        `sandbox leak: ${command} is visible on the sandbox PATH, so this case proves nothing`
      );
    }
  }
  const callLog = path.join(temp, "npm-calls.log");
  fs.writeFileSync(callLog, "");
  const result = childProcess.spawnSync(resolveTool("bash"), [dev("install-agent-clis.sh")], {
    encoding: "utf8",
    env: {
      PATH: sandboxPath,
      HOME: temp,
      TMPDIR: temp,
      NPM_CONFIG_PREFIX: path.join(temp, "prefix"),
      NPM_CALL_LOG: callLog,
      NPM_LATEST: setup.latest || "",
      NPM_OPENCODE_VERSIONS: JSON.stringify(setup.opencodeVersions || setup.latest || ""),
      NPM_VIEW_FAILS: setup.viewFails ? "1" : "",
      NPM_INSTALL_FAILS: setup.installFails ? "1" : "",
      NPM_INSTALL_NOOP: setup.installNoop ? "1" : ""
    }
  });
  const calls = fs.readFileSync(callLog, "utf8").split("\n").filter(Boolean);
  return { result, prefixBin, calls };
}

const {
  installerCases: CASES,
  wiring: WIRING,
  packages: PACKAGES
} = require("./devcontainer-agent-cli-vectors.json");

for (const testCase of CASES) {
  test(`install-agent-clis.sh handles ${testCase.name}`, { skip: !CAN_RUN_SHELL }, (t) => {
    const { result, prefixBin, calls } = runInstaller(t, testCase.setup);
    assert.equal(result.status, testCase.status, result.stdout + result.stderr);
    for (const [stream, patterns] of [
      ["stdout", testCase.stdout],
      ["stderr", testCase.stderr]
    ]) {
      for (const pattern of patterns) {
        for (const [packageName] of pattern.includes("{package}") ? PACKAGES : [[""]]) {
          const expected = new RegExp(pattern.replace("{package}", packageName));
          assert.match(result[stream], expected, packageName || testCase.name);
        }
      }
    }
    for (const [packageName, command] of PACKAGES) {
      const installs = calls.filter((call) => call.startsWith(`install -g ${packageName}@`)).length;
      assert.equal(installs, testCase.installsPerPackage, `${packageName}: ${calls}`);
      const present = fs.existsSync(path.join(prefixBin, command));
      assert.equal(present, testCase.commandsPresent, `${command} presence`);
    }
  });
}

for (const legacy of ["legacySystem", "legacyUser"]) {
  test(`migrates ${legacy} OpenCode to the newest stable v2`, { skip: !CAN_RUN_SHELL }, (t) => {
    const { result, prefixBin, calls } = runInstaller(t, {
      installed: "2.4.1",
      latest: "2.4.1",
      opencodeVersions: ["2.4.0", "2.4.1"],
      [legacy]: true
    });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.ok(calls.includes("view @opencode/cli@2 version --json"));
    const install = calls.findIndex((call) => call.startsWith("install -g @opencode/cli@2.4.1 "));
    assert.notEqual(install, -1, calls.join("\n"));
    const uninstall = calls.indexOf("uninstall -g opencode-ai");
    if (legacy === "legacyUser") {
      assert.ok(uninstall >= 0 && uninstall < install, calls.join("\n"));
    } else {
      assert.equal(uninstall, -1, calls.join("\n"));
    }
    assert.match(fs.readFileSync(path.join(prefixBin, "opencode"), "utf8"), /echo 2\.4\.1/);
  });
}

test("OpenCode-only v1 fallback is reported when offline", { skip: !CAN_RUN_SHELL }, (t) => {
  const { result, calls } = runInstaller(t, {
    installed: "2.4.1",
    legacySystem: true,
    viewFails: true
  });
  assert.equal(result.status, 1, result.stdout + result.stderr);
  assert.match(result.stderr, /registry unavailable.*OpenCode v2/);
  assert.ok(
    calls.every((call) => call.startsWith("view ")),
    calls.join("\n")
  );
});

for (const name of ["post-create.sh", "post-start.sh"]) {
  test(`${name} serializes MCP configuration`, () => {
    const source = read(name);
    assert.match(source, /\$\{TMPDIR:-\/tmp\}\/dxm-mcp-configure\.lock/);
    assert.match(source, /flock -w \d+ "\$\{(MCP_CONFIGURE_LOCK|mcp_lock)\}"/);
    assert.match(source, /command -v flock/);
  });
}

test("the root tooling lock is available to clean checkouts", () => {
  const ignored = childProcess.spawnSync("git", ["check-ignore", "package-lock.json"], {
    cwd: ROOT,
    encoding: "utf8"
  }).status;
  assert.equal(ignored, 1, "the root lock must remain eligible for source control");
  assert.ok(fs.existsSync(path.join(ROOT, "package-lock.json")));
  assert.ok(fs.existsSync(path.join(ROOT, "package-lock.json.meta")));
});

test("devcontainer agent scripts have valid bash syntax", { skip: !CAN_RUN_SHELL }, () => {
  for (const name of ["install-agent-clis.sh", "post-create.sh", "post-start.sh"]) {
    childProcess.execFileSync("bash", ["-n", dev(name)], { cwd: ROOT });
  }
});

for (const [file, text] of WIRING) {
  test(`${file} includes ${text}`, () => assert.ok(read(file).includes(text)));
}
test("launch does not block on Git LFS downloads", () => {
  assert.doesNotMatch(read("post-start.sh"), /^\s*git lfs pull/m);
});

test("post-create owns its CLI refresh", { skip: !CAN_RUN_SHELL }, (t) => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-bootstrap-order-"));
  t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
  const result = childProcess.spawnSync("bash", [fixture, "post-create", dev("post-create.sh")], {
    encoding: "utf8",
    env: { ...process.env, HOME: temp, TMPDIR: temp }
  });
  assert.equal(result.status, 0, result.stdout + result.stderr);
});
for (const [label, mode, foreign, repair, expected] of [
  ["root-owned child beneath writable cache", 0o644, true, "directory", 0],
  ["writable foreign file with unsupported chown", 0o666, true, "reject", 0],
  ["unwritable foreign file repaired by chown", 0o644, true, "real", 0],
  ["unwritable owned file", 0o444, false, "real", 1],
  ["unwritable foreign file with no-op chown", 0o644, true, "noop", 1],
  ["missing npm manifests", null, false, "reject", 0]
]) {
  test(`cache permissions handle ${label}`, { skip: !CAN_RUN_SHELL }, (t) => {
    if (childProcess.spawnSync("sudo", ["-n", "true"]).status !== 0 || process.getuid() === 0)
      return t.skip("requires an unprivileged user with passwordless sudo");
    const temp = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-file-owner-"));
    t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
    const file = path.join(temp, repair === "directory" ? "nested/cache" : "package.json");
    fs.mkdirSync(path.dirname(file), { recursive: true });
    if (mode !== null) {
      fs.writeFileSync(file, "{}");
      fs.chmodSync(file, mode);
      const target = repair === "directory" ? path.dirname(file) : file;
      if (foreign) childProcess.execFileSync("sudo", ["-n", "chown", "-R", "0:0", target]);
    }
    const result = childProcess.spawnSync(
      "bash",
      [fixture, "cache-permissions", dev("cache-contract.sh")],
      {
        encoding: "utf8",
        env: { ...process.env, HOME: temp, WORKSPACE_FOLDER: temp, REPAIR: repair }
      }
    );
    assert.equal(result.status, expected, `${label}: ${result.stderr}`);
    if (expected) assert.match(result.stderr, /not writable/);
    if (mode !== null && expected === 0) fs.writeFileSync(file, "writable without sudo");
    if (repair === "directory") assert.equal(fs.statSync(file).uid, process.getuid());
  });
}

for (const [mode, expected] of [
  ["ready", 0],
  ["install", 0],
  ["download-fails", 1],
  ["install-fails", 1],
  ["install-noop", 1]
]) {
  test(`pinned SDK bootstrap handles ${mode}`, { skip: !CAN_RUN_SHELL }, (t) => {
    const temp = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-pinned-sdk-"));
    t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
    fs.writeFileSync(
      path.join(temp, "global.json"),
      JSON.stringify({ sdk: { version: "10.0.404" } })
    );
    const result = childProcess.spawnSync(
      "bash",
      [fixture, "pinned-sdk", dev("lib/pinned-sdk.sh")],
      {
        encoding: "utf8",
        env: { ...process.env, TMPDIR: temp, MODE: mode }
      }
    );
    assert.equal(result.status, expected, result.stdout + result.stderr);
  });
}

test("both container lifecycle hooks resolve the pinned SDK synchronously", () => {
  for (const name of ["post-create.sh", "post-start.sh"]) {
    assert.match(read(name), /source "\$\{SCRIPT_DIR\}\/lib\/pinned-sdk\.sh"/);
    assert.match(read(name), /^\s*ensure_pinned_sdk(?: "[^"\n]+")?(?: \|\| return 1)?\s*$/m);
  }
});
