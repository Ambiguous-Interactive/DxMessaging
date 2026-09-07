"use strict";

// Execute the installer against a hermetic npm/CLI fixture.

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
        if [[ -n "\${NPM_INSTALL_NOOP}" ]]; then exit 0; fi
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
      NPM_VIEW_FAILS: setup.viewFails ? "1" : "",
      NPM_INSTALL_FAILS: setup.installFails ? "1" : "",
      NPM_INSTALL_NOOP: setup.installNoop ? "1" : ""
    }
  });
  const calls = fs.readFileSync(callLog, "utf8").split("\n").filter(Boolean);
  return { result, prefixBin, calls };
}

const CASES = require("./devcontainer-agent-cli-vectors.json");

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

// Lifecycle hooks share a lock because each can create the shared bearer token.
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

// Wiring checks complement the executing installer and permission tests.
const WIRING = [
  ["Dockerfile", "npm install --global"],
  ...PACKAGES.map(([name]) => ["Dockerfile", `${name}@latest`]),
  ["Dockerfile", 'NPM_CONFIG_PREFIX="/home/vscode/.local"'],
  ["post-start.sh", 'nohup bash "${installer}"'],
  ["post-start.sh", "cache_contract_repair_permissions"],
  ["post-create.sh", 'bash "${installer}"'],
  ["post-create.sh", 'unity-mcp.mjs" configure --offline'],
  ["devcontainer.json", '"waitFor": "updateContentCommand"'],
  ["devcontainer.json", '"updateContentCommand": "bash .devcontainer/post-start.sh --prepare"'],
  ["devcontainer.json", '"postAttachCommand": "bash .devcontainer/post-start.sh"'],
  ["devcontainer.json", '"userEnvProbe": "none"'],
  ["devcontainer.json", '"COPILOT_HOME": "${containerWorkspaceFolder}/.copilot"'],
  ["devcontainer.json", "host.docker.internal:host-gateway"],
  [
    "devcontainer.json",
    '"NANOCODER_MCPSERVERS_FILE": "${containerWorkspaceFolder}/.nanocoder/mcp.json"'
  ]
];
for (const [file, text] of WIRING) {
  test(`${file} includes ${text}`, () => assert.ok(read(file).includes(text)));
}
test("launch does not block on Git LFS downloads", () => {
  assert.doesNotMatch(read("post-start.sh"), /^\s*git lfs pull/m);
});

test(
  "repair fixes a root-owned child beneath a writable npm directory",
  { skip: !CAN_RUN_SHELL },
  (t) => {
    if (childProcess.spawnSync("sudo", ["-n", "true"]).status !== 0)
      return t.skip("requires passwordless container sudo");
    const temp = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-npm-owner-"));
    t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
    const child = path.join(temp, "nested");
    fs.mkdirSync(child);
    fs.writeFileSync(path.join(child, "cache"), "old");
    childProcess.execFileSync("sudo", ["-n", "chown", "-R", "0:0", child]);
    const repaired = childProcess.spawnSync(
      "bash",
      [
        "-c",
        'source "$1"; cache_contract_repair_directory "$2" "$(id -u)" "$(id -g)"',
        "bash",
        dev("cache-contract.sh"),
        temp
      ],
      { encoding: "utf8" }
    );
    assert.equal(repaired.status, 0, repaired.stderr);
    assert.equal(fs.statSync(path.join(child, "cache")).uid, process.getuid());
    fs.writeFileSync(path.join(child, "cache"), "writable without sudo");
  }
);
