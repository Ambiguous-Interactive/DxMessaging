"use strict";

// Executes .devcontainer/install-agent-clis.sh against a stub npm on PATH so the
// refresh logic (skip-when-current, offline fallback, retry budget) is verified
// by running it, not by grepping the shell source.

const assert = require("node:assert/strict");
const childProcess = require("node:child_process");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { test } = require("node:test");

// The installer is the Linux devcontainer bootstrap script. Windows runners have no
// bash and no POSIX PATH, so the executing cases are skipped there; the static wiring
// assertions below still run on every platform.
const CAN_RUN_SHELL = process.platform !== "win32";

const ROOT = path.resolve(__dirname, "..", "..");

/**
 * The only system tools the installer calls. The sandbox links exactly these and nothing else, so
 * a real agent CLI installed on the image can never satisfy the script under test. Putting
 * `/usr/bin:/bin` on the sandbox PATH looked hermetic and was not: the devcontainer image installs
 * all three CLIs globally, so the "fresh container" and "CLI absent" cases silently found real
 * binaries and asserted against the wrong world.
 */
const SYSTEM_TOOLS = ["bash", "sh", "mkdir", "grep", "head", "tr", "cat", "rm", "chmod", "flock"];

function resolveTool(name) {
  const found = childProcess.spawnSync("sh", ["-c", `command -v ${name}`], { encoding: "utf8" });
  return found.status === 0 ? found.stdout.trim() : undefined;
}
const dev = (name) => path.join(ROOT, ".devcontainer", name);
const read = (name) => fs.readFileSync(dev(name), "utf8");
const PACKAGES = [
  ["@openai/codex", "codex"],
  ["opencode-ai", "opencode"],
  ["@nanocollective/nanocoder", "nanocoder"]
];

const NPM_STUB = `#!/usr/bin/env bash
printf '%s\\n' "$*" >>"\${NPM_CALL_LOG}"
case "$1" in
    view)
        if [[ -n "\${NPM_VIEW_FAILS}" ]]; then exit 1; fi
        printf '%s\\n' "\${NPM_LATEST}"
        ;;
    install)
        if [[ -n "\${NPM_INSTALL_FAILS}" ]]; then exit 1; fi
        spec="$3"
        case "\${spec%@*}" in
            @openai/codex) shim="codex" ;;
            opencode-ai) shim="opencode" ;;
            @nanocollective/nanocoder) shim="nanocoder" ;;
            *) exit 1 ;;
        esac
        target="\${NPM_CONFIG_PREFIX}/bin/\${shim}"
        printf '#!/usr/bin/env bash\\necho %s\\n' "\${spec##*@}" >"\${target}"
        chmod +x "\${target}"
        ;;
esac
`;

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
  writeExecutable(path.join(stubBin, "npm"), NPM_STUB);
  writeExecutable(path.join(stubBin, "sleep"), "#!/usr/bin/env bash\nexit 0\n");
  // macOS ships no `timeout`, so without this stub every bounded call in the
  // installer would fail as "command not found" and look like an offline registry.
  writeExecutable(path.join(stubBin, "timeout"), '#!/usr/bin/env bash\nshift\nexec "$@"\n');
  if (setup.installed) {
    for (const [, command] of PACKAGES) {
      writeExecutable(
        path.join(prefixBin, command),
        `#!/usr/bin/env bash\necho ${setup.installed}\n`
      );
    }
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
      // Probe with the real environment so the shell itself resolves, overriding PATH only for
      // the lookup under test.
      // `command -v` reports "not found" as 1 in bash and 127 in dash, so assert on success only.
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
      NPM_VIEW_FAILS: setup.viewFails ? "1" : "",
      NPM_INSTALL_FAILS: setup.installFails ? "1" : ""
    }
  });
  const calls = fs.readFileSync(callLog, "utf8").split("\n").filter(Boolean);
  return { result, prefixBin, calls };
}

const CASES = [
  {
    name: "fresh container with a reachable registry",
    setup: { latest: "1.2.3" },
    status: 0,
    stdout: [
      "installing {package}@1\\.2\\.3 \\(current: missing\\)",
      "{package}@1\\.2\\.3 is ready\\."
    ],
    stderr: [],
    installsPerPackage: 1,
    commandsPresent: true
  },
  {
    name: "every CLI already at the latest version",
    setup: { latest: "4.5.6", installed: "4.5.6" },
    status: 0,
    stdout: ["{package}@4\\.5\\.6 is current\\."],
    stderr: [],
    installsPerPackage: 0,
    commandsPresent: true
  },
  {
    name: "unreachable registry with the CLI already installed",
    setup: { installed: "7.8.9", viewFails: true },
    status: 0,
    stdout: ["registry unavailable; keeping {package}@7\\.8\\.9\\."],
    stderr: [],
    installsPerPackage: 0,
    commandsPresent: true
  },
  {
    name: "unreachable registry with the CLI absent",
    setup: { viewFails: true },
    status: 1,
    stdout: [],
    stderr: [
      "registry unavailable and {package} is not installed\\.",
      "3 agent CLI installation\\(s\\) remain unavailable\\."
    ],
    installsPerPackage: 0,
    commandsPresent: false
  },
  {
    name: "npm install that never succeeds",
    setup: { latest: "2.0.0", installFails: true },
    status: 1,
    stdout: [],
    stderr: [
      "{package} install attempt 3/3 failed\\.",
      "3 agent CLI installation\\(s\\) remain unavailable\\."
    ],
    installsPerPackage: 3,
    commandsPresent: false
  }
];

for (const testCase of CASES) {
  test(`install-agent-clis.sh handles ${testCase.name}`, { skip: !CAN_RUN_SHELL }, (t) => {
    const { result, prefixBin, calls } = runInstaller(t, testCase.setup);
    assert.equal(
      result.status,
      testCase.status,
      `${testCase.name}: unexpected exit status (stdout: ${result.stdout}, stderr: ${result.stderr})`
    );
    for (const [stream, patterns] of [
      ["stdout", testCase.stdout],
      ["stderr", testCase.stderr]
    ]) {
      for (const pattern of patterns) {
        for (const [packageName] of pattern.includes("{package}") ? PACKAGES : [[""]]) {
          assert.match(
            result[stream],
            new RegExp(pattern.replace("{package}", packageName)),
            `${testCase.name}: ${stream} must report "${pattern}" for ${packageName || "the run"}`
          );
        }
      }
    }
    for (const [packageName, command] of PACKAGES) {
      assert.equal(
        calls.filter((call) => call.startsWith(`install -g ${packageName}@`)).length,
        testCase.installsPerPackage,
        `${testCase.name}: unexpected npm install attempt count for ${packageName} (calls: ${calls})`
      );
      assert.equal(
        fs.existsSync(path.join(prefixBin, command)),
        testCase.commandsPresent,
        `${testCase.name}: ${command} presence in the npm prefix does not match expectations`
      );
    }
  });
}

// `npm ci` refuses to run without a lockfile, and this repository gitignores package-lock.json,
// so a fresh clone has none. A devcontainer lifecycle script that reaches for `npm ci` installs
// nothing and takes every later step that needs node_modules down with it.
// `waitFor: updateContentCommand` lets post-create and post-start overlap, and both configure the
// MCP clients. Two unlocked runs starting with no bearer token would each mint one and leave the
// six generated client configs disagreeing about which token is real.
test("both lifecycle scripts serialize MCP configuration on one lock", () => {
  const lock = "dxm-mcp-configure.lock";
  for (const name of ["post-create.sh", "post-start.sh"]) {
    const source = read(name);
    assert.match(
      source,
      new RegExp(`\\$\\{TMPDIR:-/tmp\\}/${lock.replace(/\./g, "\\.")}`),
      `${name} must use the shared MCP configure lock path`
    );
    assert.match(
      source,
      /flock -w \d+ "\$\{(MCP_CONFIGURE_LOCK|mcp_lock)\}"/,
      `${name} must take the lock with a bounded wait before configuring`
    );
    assert.match(
      source,
      /command -v flock/,
      `${name} must degrade rather than fail when flock is unavailable`
    );
  }
});

test("devcontainer bootstrap never uses npm ci while the lockfile is gitignored", () => {
  const ignored = childProcess.spawnSync("git", ["check-ignore", "package-lock.json"], {
    cwd: ROOT,
    encoding: "utf8"
  }).status;
  assert.equal(ignored, 0, "this guard assumes package-lock.json is gitignored; it no longer is");
  for (const name of ["post-create.sh", "post-start.sh"]) {
    // Comment lines may name `npm ci` to explain why it is not used; only a real call counts.
    const code = read(name)
      .split("\n")
      .filter((line) => !line.trimStart().startsWith("#"))
      .join("\n");
    assert.doesNotMatch(
      code,
      /\bnpm\s+ci\b/,
      `${name} must use "npm install"; "npm ci" fails with EUSAGE when no lockfile is tracked`
    );
  }
});

test("devcontainer agent scripts have valid bash syntax", { skip: !CAN_RUN_SHELL }, () => {
  for (const name of ["install-agent-clis.sh", "post-create.sh", "post-start.sh"]) {
    childProcess.execFileSync("bash", ["-n", dev(name)], { cwd: ROOT });
  }
});

test("devcontainer wiring keeps the offline fallback and a non-blocking attach", () => {
  const dockerfile = read("Dockerfile");
  assert.match(
    dockerfile,
    /npm install --global/,
    "Dockerfile must install the agent CLIs at image build time so an offline launch still has them"
  );
  for (const [packageName] of PACKAGES) {
    assert.match(
      dockerfile,
      new RegExp(`\\s${packageName}@latest`),
      `Dockerfile must bake ${packageName}@latest into the image`
    );
  }
  const start = read("post-start.sh");
  assert.match(
    start,
    /nohup bash "\$\{installer\}"/,
    "post-start.sh must background the agent CLI refresh so it cannot delay VS Code attach"
  );
  assert.doesNotMatch(
    start,
    /^\s*git lfs pull/m,
    "post-start.sh must not run git lfs pull synchronously on every attach"
  );
  const create = read("post-create.sh");
  assert.match(
    create,
    /bash "\$\{installer\}"/,
    "post-create.sh must run install-agent-clis.sh during first-time setup"
  );
  assert.match(
    create,
    /unity-mcp\.mjs" configure --no-discover --timeout 750/,
    "post-create.sh must run the Unity MCP configurator"
  );
  const config = read("devcontainer.json");
  assert.match(
    config,
    /"waitFor": "updateContentCommand"/,
    "devcontainer.json must wait only for updateContentCommand"
  );
  assert.match(
    config,
    /"NANOCODER_MCPSERVERS_FILE": "\$\{containerWorkspaceFolder\}\/\.nanocoder\/mcp\.json"/,
    "devcontainer.json must point NANOCODER_MCPSERVERS_FILE at .nanocoder/mcp.json"
  );
});
