"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const childProcess = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const INSTALLER = path.join(REPO_ROOT, ".devcontainer", "install-codex-cli.sh");
const POST_CREATE = path.join(REPO_ROOT, ".devcontainer", "post-create.sh");
const POST_START = path.join(REPO_ROOT, ".devcontainer", "post-start.sh");

function read(filePath) {
  return fs.readFileSync(filePath, "utf8");
}

test("devcontainer codex scripts have valid bash syntax", () => {
  for (const scriptPath of [INSTALLER, POST_CREATE, POST_START]) {
    childProcess.execFileSync("bash", ["-n", scriptPath], {
      cwd: REPO_ROOT,
      stdio: "pipe"
    });
  }
});

test("installer parses package version key robustly and bounds codex --version", () => {
  const source = read(INSTALLER);

  assert.match(
    source,
    /grep -m1 '\^\[\[:space:\]\]\*"version"\[\[:space:\]\]\*:'/,
    "installer fallback must grep the version key, not arbitrary string values"
  );
  assert.match(
    source,
    /timeout 10 codex --version/,
    "installer should bound codex --version to avoid hangs on corrupted installs"
  );
});

test("post-start invokes install-codex-cli.sh without requiring executable bit", () => {
  const source = read(POST_START);

  assert.match(
    source,
    /\[\[\s*!\s*-f\s+"\$\{SCRIPT_DIR\}\/install-codex-cli\.sh"\s*\]\]/,
    "post-start must gate on file existence (-f), not executability (-x)"
  );
  assert.match(
    source,
    /bash\s+"\$\{SCRIPT_DIR\}\/install-codex-cli\.sh"/,
    "post-start must execute install-codex-cli.sh via bash"
  );
  assert.doesNotMatch(
    source,
    /\[\[\s+-x\s+"\$\{SCRIPT_DIR\}\/install-codex-cli\.sh"\s*\]\]/,
    "post-start should not rely on executable bit for install-codex-cli.sh"
  );
  assert.match(
    source,
    /WARN:\s+Codex CLI install\/update failed \(continuing\)/,
    "post-start should log a warning when codex install/update fails"
  );
});

test("post-create installs codex so first attached terminal has it", () => {
  const source = read(POST_CREATE);

  assert.match(
    source,
    /install-codex-cli\.sh/,
    "post-create must reference install-codex-cli.sh"
  );
  assert.match(
    source,
    /run_optional\s+"Installing Codex CLI \(@openai\/codex\)"\s+install_codex_cli/,
    "post-create must run codex install during initial container creation"
  );
  assert.match(
    source,
    /bash\s+"\$\{installer\}"/,
    "post-create must install codex during initial container creation"
  );
  assert.match(
    source,
    /ensure_path_line\s+"\$HOME\/\.profile"/,
    "post-create must ensure PATH includes ~/.local/bin for login shells"
  );
});
