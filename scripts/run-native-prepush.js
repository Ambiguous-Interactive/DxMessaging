#!/usr/bin/env node
"use strict";

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");
const { mergeSanitizedEnv, spawnPlatformCommandSync } = require("./lib/shell-command");
const { isMissingGit } = require("./lib/changed-files");
const { hasValidHookValidationStamp } = require("./lib/hook-validation-stamp");

const REPO_ROOT = path.resolve(__dirname, "..");
const ZERO_OID_RE = /^0+$/;
const REMOTE_NAME_FORBIDDEN_RE = /[\x00-\x20\x7f~^:?\*\[\\]/;

function isZeroOid(oid) {
  return typeof oid === "string" && oid.length > 0 && ZERO_OID_RE.test(oid);
}

function parsePrePushInput(input) {
  const updates = [];
  const lines = String(input || "").split(/\r?\n/);

  for (let index = 0; index < lines.length; index++) {
    const line = lines[index].trim();
    if (line.length === 0) {
      continue;
    }

    const parts = line.split(/\s+/);
    if (parts.length !== 4) {
      throw new Error(`pre-push stdin line ${index + 1} is malformed: expected 4 fields.`);
    }

    const [localRef, localOid, remoteRef, remoteOid] = parts;
    updates.push({ localRef, localOid, remoteRef, remoteOid });
  }

  return updates;
}

function isDeletedUpdate(update) {
  return update.localRef === "(delete)" || isZeroOid(update.localOid);
}

function isNoopUpdate(update) {
  return update.localOid === update.remoteOid;
}

function isNewRemoteUpdate(update) {
  return isZeroOid(update.remoteOid);
}

function runGit(args, options = {}) {
  return spawnPlatformCommandSync(
    "git",
    args,
    {
      cwd: REPO_ROOT,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
      ...options
    },
    childProcess.spawnSync
  );
}

function throwIfMissingGit(result) {
  if (isMissingGit(result)) {
    const detail = result.error && result.error.message ? result.error.message : "ENOENT";
    throw new Error(`native pre-push: unable to spawn git (${detail}); git must be on PATH.`);
  }
}

function sanitizeRemoteName(remoteName) {
  const value = String(remoteName || "");
  if (
    value.length === 0 ||
    value.trim() !== value ||
    value.startsWith("/") ||
    value.endsWith("/") ||
    value.endsWith(".") ||
    value.includes("//") ||
    value.includes("..") ||
    value.includes("@{") ||
    REMOTE_NAME_FORBIDDEN_RE.test(value)
  ) {
    return null;
  }

  const components = value.split("/");
  for (const component of components) {
    if (
      component.length === 0 ||
      component === "@" ||
      component.startsWith(".") ||
      component.endsWith(".") ||
      component.endsWith(".lock")
    ) {
      return null;
    }
  }

  return value;
}

function remoteBaseCandidates(remoteName) {
  const candidates = [];
  const seen = new Set();
  const addRemote = (name) => {
    const safe = sanitizeRemoteName(name);
    if (!safe || seen.has(safe)) {
      return;
    }
    seen.add(safe);
    candidates.push(
      { probe: `refs/remotes/${safe}/HEAD`, ref: `refs/remotes/${safe}/HEAD` },
      { probe: `refs/remotes/${safe}/master`, ref: `refs/remotes/${safe}/master` },
      { probe: `refs/remotes/${safe}/main`, ref: `refs/remotes/${safe}/main` }
    );
  };

  addRemote(remoteName);
  addRemote("origin");
  candidates.push(
    { probe: "refs/heads/master", ref: "refs/heads/master" },
    { probe: "refs/heads/main", ref: "refs/heads/main" }
  );
  return candidates;
}

function refExists(runGitFn, ref) {
  const result = runGitFn(["rev-parse", "--verify", "--quiet", ref]);
  throwIfMissingGit(result);
  return !!(result && !result.error && result.status === 0);
}

function resolveBaseRefForRemote(runGitFn, remoteName) {
  for (const candidate of remoteBaseCandidates(remoteName)) {
    if (refExists(runGitFn, candidate.probe)) {
      return candidate.ref;
    }
  }
  return null;
}

function resolveMergeBaseForTarget(runGitFn, baseRef, targetRef) {
  const result = runGitFn(["merge-base", baseRef, targetRef]);
  throwIfMissingGit(result);
  if (!result || result.error || result.status !== 0) {
    return null;
  }

  const sha = String(result.stdout || "").trim();
  return sha.length > 0 ? sha : null;
}

function commitExistsLocally(runGitFn, oid) {
  const result = runGitFn(["cat-file", "-e", `${oid}^{commit}`]);
  throwIfMissingGit(result);
  return !!(result && !result.error && result.status === 0);
}

function buildValidationJobs(updates, deps = {}) {
  const runGitFn = deps.runGitFn || runGit;
  const remoteName = deps.remoteName || null;
  const jobs = [];
  const seenRanges = new Set();
  let needsFullFallback = false;
  let fullFallbackLabel = null;

  for (const update of updates) {
    if (isDeletedUpdate(update) || isNoopUpdate(update)) {
      continue;
    }

    if (!isNewRemoteUpdate(update)) {
      if (
        !commitExistsLocally(runGitFn, update.remoteOid) ||
        !commitExistsLocally(runGitFn, update.localOid)
      ) {
        needsFullFallback = true;
        fullFallbackLabel = "ref update endpoint is not available in the local object database";
        continue;
      }

      const key = `${update.remoteOid}..${update.localOid}`;
      if (!seenRanges.has(key)) {
        seenRanges.add(key);
        jobs.push({
          type: "range",
          rangeFrom: update.remoteOid,
          rangeTo: update.localOid,
          label: `${update.remoteRef}: ${update.remoteOid}..${update.localOid}`
        });
      }
      continue;
    }

    const baseRef = resolveBaseRefForRemote(runGitFn, remoteName);
    if (!baseRef) {
      needsFullFallback = true;
      fullFallbackLabel =
        fullFallbackLabel || "new ref without a resolvable default-branch merge base";
      continue;
    }

    const mergeBase = resolveMergeBaseForTarget(runGitFn, baseRef, update.localOid);
    if (!mergeBase) {
      needsFullFallback = true;
      fullFallbackLabel =
        fullFallbackLabel || "new ref without a resolvable default-branch merge base";
      continue;
    }

    const key = `${mergeBase}..${update.localOid}`;
    if (!seenRanges.has(key)) {
      seenRanges.add(key);
      jobs.push({
        type: "range",
        rangeFrom: mergeBase,
        rangeTo: update.localOid,
        label: `${update.remoteRef}: ${mergeBase}..${update.localOid} (new ref via ${baseRef})`
      });
    }
  }

  if (needsFullFallback) {
    return [
      {
        type: "full",
        label: fullFallbackLabel || "ref update cannot be represented as a local range"
      }
    ];
  }

  return jobs;
}

function runCommand(command, args, deps = {}) {
  const env = mergeSanitizedEnv(deps.env || process.env, {}, { removeKeys: ["SKIP"] });
  const options = {
    cwd: REPO_ROOT,
    env,
    stdio: "inherit"
  };
  const result = deps.spawnFn
    ? deps.spawnFn(command, args, options)
    : spawnPlatformCommandSync(
        command,
        args,
        options,
        deps.spawnSyncImpl || childProcess.spawnSync,
        deps.platform || process.platform
      );

  if (result.error && result.error.code === "ENOENT") {
    process.stderr.write(`Unable to find required command '${command}'.\n`);
    return 127;
  }

  return typeof result.status === "number" ? result.status : 1;
}

function runRangePreflight(job, deps = {}) {
  return runCommand(
    process.execPath,
    [
      "scripts/preflight.js",
      "--stage=pre-push",
      "--profile=guard",
      "--range-from",
      job.rangeFrom,
      "--range-to",
      job.rangeTo,
      "--no-worktree"
    ],
    deps
  );
}

function runFullFallback(deps = {}) {
  return runCommand("npm", ["run", "preflight:pre-push"], deps);
}

function hasCallerSkip(env = process.env) {
  return String((env && env.SKIP) || "")
    .split(",")
    .some((entry) => entry.trim().length > 0);
}

function runJobs(jobs, deps = {}) {
  const logFn = deps.logFn || ((message) => process.stdout.write(`${message}\n`));
  const hasValidStampFn = deps.hasValidHookValidationStampFn || hasValidHookValidationStamp;
  const allowStampSkip = !hasCallerSkip(deps.env || process.env);

  if (jobs.length === 0) {
    logFn("native pre-push: no changed ref updates to validate.");
    return 0;
  }

  const fullJob = jobs.find((job) => job.type === "full");
  if (fullJob) {
    logFn(`native pre-push: ${fullJob.label}; running exhaustive pre-push preflight.`);
    return runFullFallback(deps);
  }

  for (const job of jobs) {
    if (allowStampSkip) {
      const stamp = hasValidStampFn(REPO_ROOT, "pre-push", {
        rangeFrom: job.rangeFrom,
        rangeTo: job.rangeTo
      });
      if (stamp.valid) {
        logFn(`native pre-push: validation stamp is current for ${job.label}; skipping.`);
        continue;
      }
    }

    logFn(`native pre-push: validating ${job.label}.`);
    const status = runRangePreflight(job, deps);
    if (status !== 0) {
      return status;
    }
  }

  return 0;
}

function main(argv = process.argv.slice(2), deps = {}) {
  let input;
  try {
    input = deps.stdin !== undefined ? deps.stdin : fs.readFileSync(0, "utf8");
    const updates = parsePrePushInput(input);
    const remoteName = deps.remoteName !== undefined ? deps.remoteName : argv[0];
    const jobs = buildValidationJobs(updates, { ...deps, remoteName });
    return runJobs(jobs, deps);
  } catch (error) {
    const detail = error && error.message ? error.message : String(error);
    process.stderr.write(`native pre-push: ${detail}\n`);
    return 1;
  }
}

module.exports = {
  REPO_ROOT,
  isZeroOid,
  parsePrePushInput,
  isDeletedUpdate,
  isNoopUpdate,
  isNewRemoteUpdate,
  sanitizeRemoteName,
  remoteBaseCandidates,
  refExists,
  resolveBaseRefForRemote,
  resolveMergeBaseForTarget,
  commitExistsLocally,
  buildValidationJobs,
  runCommand,
  runRangePreflight,
  runFullFallback,
  hasCallerSkip,
  runJobs,
  main
};

if (require.main === module) {
  process.exit(main());
}
