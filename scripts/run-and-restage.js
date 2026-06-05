#!/usr/bin/env node
"use strict";

const childProcess = require("child_process");
const fs = require("fs");
const path = require("path");
const { spawnPlatformCommandSync } = require("./lib/shell-command");

function splitArgs(argv) {
  const separator = argv.indexOf("--");
  if (separator === -1) {
    return { command: argv, files: [] };
  }
  return {
    command: argv.slice(0, separator),
    files: argv.slice(separator + 1)
  };
}

function run(command, args, options = {}) {
  return spawnPlatformCommandSync(
    command,
    args,
    {
      stdio: "inherit",
      ...options
    },
    childProcess.spawnSync
  );
}

function runCaptured(command, args, options = {}) {
  const { stdio, ...rest } = options;
  return spawnPlatformCommandSync(
    command,
    args,
    {
      encoding: "utf8",
      stdio:
        stdio ||
        (options.input !== undefined ? ["pipe", "pipe", "pipe"] : ["ignore", "pipe", "pipe"]),
      ...rest
    },
    childProcess.spawnSync
  );
}

function uniqueFiles(files) {
  return [...new Set(files)];
}

function toGitPath(file, cwd = process.cwd()) {
  const rel = path.isAbsolute(file) ? path.relative(cwd, file) : file;
  return rel.replace(/\\/g, "/");
}

function splitNulFields(value) {
  return String(value || "")
    .split("\0")
    .filter((field) => field.length > 0);
}

function readSnapshot(file, fsImpl = fs) {
  try {
    return { exists: true, content: fsImpl.readFileSync(file) };
  } catch (error) {
    if (error && error.code === "ENOENT") {
      return { exists: false, content: null };
    }
    throw error;
  }
}

function snapshotPath(file, cwd) {
  return cwd && !path.isAbsolute(file) ? path.join(cwd, file) : file;
}

function snapshotFiles(files, fsImpl = fs, cwd = undefined) {
  return uniqueFiles(files).map((file) => ({
    file,
    absPath: snapshotPath(file, cwd),
    before: readSnapshot(snapshotPath(file, cwd), fsImpl)
  }));
}

function snapshotChanged(snapshot, fsImpl = fs) {
  const after = readSnapshot(snapshot.absPath || snapshot.file, fsImpl);
  if (snapshot.before.exists !== after.exists) {
    return true;
  }
  if (!after.exists) {
    return false;
  }
  return Buffer.compare(Buffer.from(snapshot.before.content), Buffer.from(after.content)) !== 0;
}

function changedFilesSinceSnapshot(snapshots, fsImpl = fs) {
  return snapshots
    .filter((snapshot) => snapshotChanged(snapshot, fsImpl))
    .map((snapshot) => snapshot.file);
}

function patchIsEmpty(patch) {
  return String(patch || "").length === 0;
}

function gitFailureDetail(result) {
  if (result && result.error) {
    return result.error.message;
  }
  return `${(result && result.stderr) || ""}\n${(result && result.stdout) || ""}`.trim();
}

function captureGitPatch(files, options = {}) {
  if (files.length === 0) {
    return { ok: true, patch: "" };
  }
  const runCapturedFn = options.runCapturedFn || runCaptured;
  const args = ["diff", "--binary", "--no-ext-diff", "--unified=0"];
  if (options.cached) {
    args.push("--cached");
  }
  args.push("--", ...files);
  const result = runCapturedFn("git", args, { cwd: options.cwd });
  if (result.error || result.status !== 0) {
    return { ok: false, patch: "", detail: gitFailureDetail(result) };
  }
  return { ok: true, patch: String(result.stdout || "") };
}

function captureGitPatchMap(files, options = {}) {
  const patches = new Map();
  for (const file of files) {
    const result = captureGitPatch([file], options);
    if (!result.ok) {
      return result;
    }
    patches.set(file, result.patch);
  }
  return { ok: true, patches };
}

function captureUntrackedFiles(files, options = {}) {
  if (files.length === 0) {
    return { ok: true, files: new Set() };
  }
  const runCapturedFn = options.runCapturedFn || runCaptured;
  const result = runCapturedFn(
    "git",
    ["ls-files", "--others", "--exclude-standard", "-z", "--", ...files],
    { cwd: options.cwd }
  );
  if (result.error || result.status !== 0) {
    return { ok: false, files: new Set(), detail: gitFailureDetail(result) };
  }
  return { ok: true, files: new Set(splitNulFields(result.stdout)) };
}

function patchesForFiles(patches, files) {
  if (!patches) {
    return "";
  }
  if (patches instanceof Map) {
    return files.map((file) => patches.get(file) || "").join("");
  }
  return String(patches || "");
}

function applyCachedPatch(patch, args, options = {}) {
  if (patchIsEmpty(patch)) {
    return { ok: true };
  }
  const runCapturedFn = options.runCapturedFn || runCaptured;
  const result = runCapturedFn(
    "git",
    ["apply", "--cached", "--whitespace=nowarn", "--unidiff-zero", ...args, "-"],
    {
      cwd: options.cwd,
      input: patch
    }
  );
  if (result.error || result.status !== 0) {
    return { ok: false, detail: gitFailureDetail(result) };
  }
  return { ok: true };
}

function restoreIndex(files, indexPatch, options = {}) {
  const runCapturedFn = options.runCapturedFn || runCaptured;
  const reset = runCapturedFn("git", ["reset", "-q", "--", ...files], { cwd: options.cwd });
  if (reset.error || reset.status !== 0) {
    return { ok: false, detail: gitFailureDetail(reset) };
  }
  return applyCachedPatch(indexPatch, [], options);
}

function rejectChangedPreexistingUntracked(files, preexistingUntracked, label, cwd) {
  const blocked = files.filter((file) => preexistingUntracked.has(toGitPath(file, cwd)));
  if (blocked.length === 0) {
    return false;
  }
  process.stderr.write(
    `${label}: refusing to stage pre-existing untracked file(s) changed by the wrapped command: ${blocked.join(", ")}. Git cannot stage only the generated delta for an untracked file.\n`
  );
  return true;
}

function stageFiles(files, options = {}) {
  const normalized = typeof options === "function" ? { runFn: options } : options;
  const runFn = normalized.runFn || run;
  if (files.length === 0) {
    return 0;
  }

  const addResult = runFn("git", ["add", "--", ...files], { cwd: normalized.cwd });
  if (addResult.error) {
    process.stderr.write(`run-and-restage: failed to restage files: ${addResult.error.message}\n`);
    return 1;
  }
  if (addResult.status !== 0) {
    return 1;
  }

  const unstagedPatch = patchesForFiles(
    normalized.unstagedPatches || normalized.unstagedPatch,
    files
  );
  const reverse = applyCachedPatch(unstagedPatch, ["--reverse"], normalized);
  if (reverse.ok) {
    return 0;
  }

  const indexPatch = patchesForFiles(normalized.indexPatches || normalized.indexPatch, files);
  const restored = restoreIndex(files, indexPatch, normalized);
  const rollback = restored.ok ? "" : ` Rollback failed: ${restored.detail}`;
  process.stderr.write(
    `run-and-restage: refusing to stage stale pre-existing hunks in fixed file(s). ${reverse.detail}${rollback}\n`
  );
  return 1;
}

function main(argv = process.argv.slice(2), deps = {}) {
  const runFn = deps.runFn || run;
  const runCapturedFn = deps.runCapturedFn || runCaptured;
  const fsImpl = deps.fsImpl || fs;
  const cwd = deps.cwd;
  const { command, files } = splitArgs(argv);
  if (command.length === 0) {
    process.stderr.write("run-and-restage: missing command before --.\n");
    return 1;
  }

  const targetFiles = uniqueFiles(files);
  let indexPatch = captureGitPatch(targetFiles, { cached: true, cwd, runCapturedFn });
  if (!indexPatch.ok) {
    process.stderr.write(
      `run-and-restage: failed to capture staged baseline: ${indexPatch.detail}\n`
    );
    return 1;
  }
  let unstagedPatch = captureGitPatch(targetFiles, { cached: false, cwd, runCapturedFn });
  if (!unstagedPatch.ok) {
    process.stderr.write(
      `run-and-restage: failed to capture unstaged baseline: ${unstagedPatch.detail}\n`
    );
    return 1;
  }
  const preexistingUntracked = captureUntrackedFiles(targetFiles, { cwd, runCapturedFn });
  if (!preexistingUntracked.ok) {
    process.stderr.write(
      `run-and-restage: failed to capture untracked baseline: ${preexistingUntracked.detail}\n`
    );
    return 1;
  }
  if (!patchIsEmpty(unstagedPatch.patch)) {
    indexPatch = captureGitPatchMap(targetFiles, { cached: true, cwd, runCapturedFn });
    if (!indexPatch.ok) {
      process.stderr.write(
        `run-and-restage: failed to capture per-file staged baseline: ${indexPatch.detail}\n`
      );
      return 1;
    }
    unstagedPatch = captureGitPatchMap(targetFiles, { cached: false, cwd, runCapturedFn });
    if (!unstagedPatch.ok) {
      process.stderr.write(
        `run-and-restage: failed to capture per-file unstaged baseline: ${unstagedPatch.detail}\n`
      );
      return 1;
    }
  }

  const snapshots = snapshotFiles(targetFiles, fsImpl, cwd);
  const result = runFn(command[0], [...command.slice(1), ...files], { cwd });
  if (result.error) {
    process.stderr.write(`run-and-restage: failed to run ${command[0]}: ${result.error.message}\n`);
    return 1;
  }
  if (result.status !== 0) {
    return typeof result.status === "number" ? result.status : 1;
  }

  if (files.length === 0) {
    return 0;
  }

  const changedFiles = changedFilesSinceSnapshot(snapshots, fsImpl);
  if (
    rejectChangedPreexistingUntracked(
      changedFiles,
      preexistingUntracked.files,
      "run-and-restage",
      cwd
    )
  ) {
    return 1;
  }

  return stageFiles(changedFiles, {
    cwd,
    runFn,
    runCapturedFn,
    indexPatches: indexPatch.patches || indexPatch.patch,
    unstagedPatches: unstagedPatch.patches || unstagedPatch.patch
  });
}

module.exports = {
  splitArgs,
  runCaptured,
  uniqueFiles,
  toGitPath,
  splitNulFields,
  readSnapshot,
  snapshotFiles,
  snapshotChanged,
  changedFilesSinceSnapshot,
  patchIsEmpty,
  captureGitPatch,
  captureGitPatchMap,
  captureUntrackedFiles,
  patchesForFiles,
  applyCachedPatch,
  restoreIndex,
  rejectChangedPreexistingUntracked,
  stageFiles,
  main
};

if (require.main === module) {
  process.exit(main());
}
