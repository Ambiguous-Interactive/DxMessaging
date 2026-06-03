"use strict";

const fs = require("fs");
const path = require("path");

/**
 * Host-default FOLDER environment variables that scripts probe to discover
 * machine-installed software (e.g. ensure-editor.ps1's Find-UnityEditor checks
 * `${env:ProgramFiles}\Unity\Hub\Editor\<ver>\Editor\Unity.exe`). A test that
 * spawns such a script MUST neutralize these so a real install on the host
 * cannot leak into the resolution path and make the test pass/fail by accident.
 *
 * The canonical-cased names we deliberately SET to empty sandbox dirs. The
 * lowercased denylist (HOST_FOLDER_DENYLIST below) is what we REMOVE -- it is
 * derived from these plus their `(x86)` variants so every casing is covered.
 */
const HOST_FOLDER_CANONICAL_VARS = [
  "ProgramFiles",
  "ProgramFiles(x86)",
  "ProgramW6432",
  "CommonProgramFiles",
  "CommonProgramFiles(x86)",
  "CommonProgramW6432",
  "LOCALAPPDATA"
];

/**
 * Lowercased set of EVERY host-default folder variable name to strip, regardless
 * of source casing. This is the crux of the cross-platform fix: Windows
 * environment-variable NAMES are CASE-INSENSITIVE (so `$env:ProgramFiles` reads
 * a key spelled `PROGRAMFILES`, `ProgramFiles`, or any other casing), but a
 * JavaScript object's keys are CASE-SENSITIVE. A naive `delete env.ProgramFiles`
 * therefore leaves any surviving case-variant key (e.g. `PROGRAMFILES`) in
 * place, and the child pwsh still sees the real folder -- the real install
 * leaks in. We must compare names case-insensitively to remove them all.
 */
const HOST_FOLDER_DENYLIST = new Set(HOST_FOLDER_CANONICAL_VARS.map((name) => name.toLowerCase()));

/**
 * Turn a canonical variable name into a filesystem-safe subdirectory name.
 * `ProgramFiles(x86)` -> `ProgramFiles_x86_` so each var maps to a DISTINCT,
 * portable directory under the sandbox root on every OS.
 *
 * @param {string} varName - Canonical environment-variable name.
 * @returns {string} A filesystem-safe directory leaf name.
 */
function sandboxDirNameFor(varName) {
  return varName.replace(/[^A-Za-z0-9]+/g, "_");
}

function findPathEnvKey(env) {
  if (!env || typeof env !== "object") {
    return null;
  }

  for (const key of Object.keys(env)) {
    if (key.toLowerCase() === "path") {
      return key;
    }
  }

  return null;
}

function getPathEnvValue(env) {
  const key = findPathEnvKey(env);
  return key ? env[key] || "" : "";
}

function getPathDelimiterForPlatform(platform) {
  return platform === "win32" ? ";" : ":";
}

function prependPathEnv(baseEnv = process.env, pathSegment, options = {}) {
  if (typeof pathSegment !== "string" || pathSegment.length === 0) {
    throw new Error("prependPathEnv: pathSegment must be a non-empty string.");
  }

  const platform = typeof options.platform === "string" ? options.platform : process.platform;
  const delimiter =
    typeof options.delimiter === "string"
      ? options.delimiter
      : getPathDelimiterForPlatform(platform);
  const existingPathKey = findPathEnvKey(baseEnv);
  const targetPathKey = existingPathKey || (platform === "win32" ? "Path" : "PATH");
  const existingPathValue = getPathEnvValue(baseEnv);

  const result = {};
  for (const [key, value] of Object.entries(baseEnv || {})) {
    if (key.toLowerCase() === "path") {
      continue;
    }
    result[key] = value;
  }

  result[targetPathKey] =
    existingPathValue && existingPathValue.length > 0
      ? `${pathSegment}${delimiter}${existingPathValue}`
      : pathSegment;

  return result;
}

/**
 * Build a hermetic spawn environment in which every host-default FOLDER variable
 * points at a DISTINCT EMPTY sandbox directory instead of a real machine path.
 *
 * Why SET-to-empty-sandbox rather than DELETE:
 *   1. Case-insensitivity (Windows): see HOST_FOLDER_DENYLIST. A case-sensitive
 *      `delete env.ProgramFiles` misses surviving variants (`PROGRAMFILES`,
 *      ...), so the real folder leaks into the child process. We remove EVERY
 *      key whose lowercased name is on the denylist.
 *   2. Determinism: an ABSENT variable is best modeled by an empty directory,
 *      not by deletion. ensure-editor.ps1 guards its probes with
 *      `${env:ProgramFiles} -and .Trim().Length -gt 0`, so a populated-but-empty
 *      sandbox dir keeps that guard TRUE (exercising the probe path the script
 *      really runs on a Windows host) while guaranteeing
 *      `${env:ProgramFiles}\Unity\Hub\Editor\...` never matches a real install.
 *
 * The function is PURE: it returns a brand-new env object and never mutates the
 * input `baseEnv` or `process.env`. The sandbox subdirectories are created on
 * disk (mkdir -p) so the child process sees real, empty directories.
 *
 * @param {NodeJS.ProcessEnv} [baseEnv=process.env] - Source environment to copy.
 * @param {string} sandboxRootDir - Directory under which empty per-var subdirs
 *   are created. Must be provided (typically the test's temp workspace).
 * @param {object} [options]
 * @param {string[]} [options.extraVars] - Additional host-default folder var
 *   names to neutralize (removed case-insensitively and set to empty sandbox
 *   dirs alongside the built-in canonical set).
 * @returns {NodeJS.ProcessEnv} A new env object safe to pass to spawn().
 */
function sandboxHostFolderEnv(baseEnv = process.env, sandboxRootDir, options = {}) {
  if (typeof sandboxRootDir !== "string" || sandboxRootDir.length === 0) {
    throw new Error("sandboxHostFolderEnv: sandboxRootDir must be a non-empty string.");
  }

  // Tolerate an explicit `null` (the default param only applies to `undefined`,
  // so `sandboxHostFolderEnv(env, root, null)` would otherwise throw on
  // `null.extraVars`).
  const opts = options || {};
  const extraVars = Array.isArray(opts.extraVars) ? opts.extraVars : [];

  // The full set of canonical names we will SET (built-ins plus any caller
  // extras), de-duplicated while preserving order.
  const canonicalVars = [];
  const seenCanonical = new Set();
  for (const name of [...HOST_FOLDER_CANONICAL_VARS, ...extraVars]) {
    if (typeof name !== "string" || name.length === 0) {
      continue;
    }
    if (seenCanonical.has(name)) {
      continue;
    }
    seenCanonical.add(name);
    canonicalVars.push(name);
  }

  // The lowercased denylist of names to REMOVE (built-ins plus extras), so every
  // existing case-variant of each name is dropped.
  const removalDenylist = new Set(HOST_FOLDER_DENYLIST);
  for (const name of extraVars) {
    if (typeof name === "string" && name.length > 0) {
      removalDenylist.add(name.toLowerCase());
    }
  }

  // 1. Copy the base env, dropping EVERY key whose lowercased name is on the
  //    denylist (case-insensitive removal -- the crux fix).
  const result = {};
  for (const key of Object.keys(baseEnv)) {
    if (removalDenylist.has(key.toLowerCase())) {
      continue;
    }
    result[key] = baseEnv[key];
  }

  // 2. SET each canonical var to a DISTINCT empty sandbox subdirectory, creating
  //    it on disk so the child process sees a real, empty directory.
  for (const varName of canonicalVars) {
    const dir = path.join(sandboxRootDir, sandboxDirNameFor(varName));
    fs.mkdirSync(dir, { recursive: true });
    result[varName] = dir;
  }

  return result;
}

module.exports = {
  sandboxHostFolderEnv,
  HOST_FOLDER_CANONICAL_VARS,
  HOST_FOLDER_DENYLIST,
  sandboxDirNameFor,
  findPathEnvKey,
  getPathDelimiterForPlatform,
  getPathEnvValue,
  prependPathEnv
};
