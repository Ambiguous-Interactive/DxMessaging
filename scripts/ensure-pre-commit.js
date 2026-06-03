#!/usr/bin/env node
"use strict";

const childProcess = require("child_process");
const path = require("path");
const { spawnPlatformCommandSync } = require("./lib/shell-command");

const PRE_COMMIT_VERSION = "4.6.0";
const PACKAGE_SPEC = `pre-commit==${PRE_COMMIT_VERSION}`;
const REPO_ROOT = path.resolve(__dirname, "..");

const PYTHON_CANDIDATES = [
  { command: "python", args: [] },
  { command: "python3", args: [] },
  { command: "py", args: ["-3"] }
];

function runCommand(command, args, options = {}, spawnSyncImpl = childProcess.spawnSync) {
  return spawnPlatformCommandSync(
    command,
    args,
    {
      cwd: REPO_ROOT,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
      ...options
    },
    spawnSyncImpl
  );
}

function isSuccess(result) {
  return result && !result.error && result.status === 0;
}

function isMissingCommand(result) {
  return result && result.error && result.error.code === "ENOENT";
}

function parsePreCommitVersion(output) {
  const match = /\bpre-commit\s+(\d+\.\d+\.\d+)\b/.exec(String(output || ""));
  return match ? match[1] : null;
}

function versionMatchesPin(output) {
  return parsePreCommitVersion(output) === PRE_COMMIT_VERSION;
}

function probePreCommitExecutable(options = {}) {
  const { runCommandFn = runCommand } = options;
  const result = runCommandFn("pre-commit", ["--version"]);
  if (!isSuccess(result) || !versionMatchesPin(result.stdout)) {
    return null;
  }

  return {
    command: "pre-commit",
    argsPrefix: [],
    version: String(result.stdout || "").trim()
  };
}

function probePythonCandidate(candidate, options = {}) {
  const { runCommandFn = runCommand } = options;
  const version = runCommandFn(candidate.command, [...candidate.args, "--version"]);
  if (!isSuccess(version)) {
    return null;
  }

  return candidate;
}

function findPython(options = {}) {
  const { candidates = PYTHON_CANDIDATES } = options;
  for (const candidate of candidates) {
    const found = probePythonCandidate(candidate, options);
    if (found) {
      return found;
    }
  }

  return null;
}

function probePreCommitModule(python, options = {}) {
  if (!python) {
    return null;
  }

  const { runCommandFn = runCommand } = options;
  const argsPrefix = [...python.args, "-m", "pre_commit"];
  const result = runCommandFn(python.command, [...argsPrefix, "--version"]);
  if (!isSuccess(result) || !versionMatchesPin(result.stdout)) {
    return null;
  }

  return {
    command: python.command,
    argsPrefix,
    version: String(result.stdout || "").trim()
  };
}

function shouldRetryWithBreakSystemPackages(result) {
  const output = `${result && result.stderr ? result.stderr : ""}\n${
    result && result.stdout ? result.stdout : ""
  }`;
  return /externally-managed-environment|--break-system-packages/i.test(output);
}

function installPreCommitWithPython(python, options = {}) {
  const { runCommandFn = runCommand, useBreakSystemPackages = false } = options;
  const args = [
    ...python.args,
    "-m",
    "pip",
    "install",
    "--disable-pip-version-check",
    "--user"
  ];
  if (useBreakSystemPackages) {
    args.push("--break-system-packages");
  }
  args.push(PACKAGE_SPEC);

  return runCommandFn(python.command, args, { stdio: "inherit", encoding: undefined });
}

function ensurePreCommit(options = {}) {
  const {
    logFn = console.log,
    warnFn = console.warn,
    runCommandFn = runCommand,
    candidates = PYTHON_CANDIDATES
  } = options;

  const executable = probePreCommitExecutable({ runCommandFn });
  if (executable) {
    return { ok: true, invocation: executable, installed: false };
  }

  const python = findPython({ runCommandFn, candidates });
  if (!python) {
    warnFn(
      "ensure-pre-commit: pre-commit is not on PATH and no Python launcher was found; cannot auto-install pre-commit."
    );
    return { ok: false, invocation: null, installed: false, reason: "missing-python" };
  }

  const module = probePreCommitModule(python, { runCommandFn });
  if (module) {
    return { ok: true, invocation: module, installed: false };
  }

  logFn(`ensure-pre-commit: installing ${PACKAGE_SPEC} with ${python.command}.`);
  let install = installPreCommitWithPython(python, { runCommandFn });
  if (!isSuccess(install) && shouldRetryWithBreakSystemPackages(install)) {
    install = installPreCommitWithPython(python, {
      runCommandFn,
      useBreakSystemPackages: true
    });
  }

  if (!isSuccess(install)) {
    const detail = isMissingCommand(install)
      ? `${python.command} not found while installing`
      : `pip exited with status ${install && typeof install.status === "number" ? install.status : "unknown"}`;
    warnFn(`ensure-pre-commit: failed to install ${PACKAGE_SPEC}: ${detail}.`);
    return { ok: false, invocation: null, installed: false, reason: "install-failed" };
  }

  const installedModule = probePreCommitModule(python, { runCommandFn });
  if (!installedModule) {
    warnFn(`ensure-pre-commit: ${PACKAGE_SPEC} installed but python -m pre_commit is unavailable.`);
    return { ok: false, invocation: null, installed: true, reason: "module-unavailable" };
  }

  return { ok: true, invocation: installedModule, installed: true };
}

function runPreCommit(argv, options = {}) {
  const {
    ensurePreCommitFn = ensurePreCommit,
    runCommandFn = runCommand,
    stdio = "inherit"
  } = options;

  const result = ensurePreCommitFn(options);
  if (!result.ok) {
    return 1;
  }

  const invocation = result.invocation;
  const run = runCommandFn(
    invocation.command,
    [...invocation.argsPrefix, ...argv],
    { stdio, encoding: undefined }
  );
  if (run.error && run.error.code === "ENOENT") {
    process.stderr.write(`ensure-pre-commit: unable to run ${invocation.command}.\n`);
    return 127;
  }

  return typeof run.status === "number" ? run.status : 1;
}

function main(argv = process.argv.slice(2)) {
  if (argv.length > 0) {
    return runPreCommit(argv);
  }

  const result = ensurePreCommit();
  if (!result.ok) {
    return 1;
  }

  if (result.invocation && result.invocation.version) {
    process.stdout.write(`ensure-pre-commit: ${result.invocation.version}\n`);
  }
  return 0;
}

module.exports = {
  PRE_COMMIT_VERSION,
  PACKAGE_SPEC,
  PYTHON_CANDIDATES,
  runCommand,
  isSuccess,
  isMissingCommand,
  parsePreCommitVersion,
  versionMatchesPin,
  probePreCommitExecutable,
  findPython,
  probePreCommitModule,
  shouldRetryWithBreakSystemPackages,
  installPreCommitWithPython,
  ensurePreCommit,
  runPreCommit,
  main
};

if (require.main === module) {
  process.exit(main());
}
