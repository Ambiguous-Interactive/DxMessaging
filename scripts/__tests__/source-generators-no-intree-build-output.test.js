/**
 * @fileoverview Real-filesystem guard for the no-in-tree-obj/bin invariant in
 * SourceGenerators/.
 *
 * This repo IS a Unity UPM package and the repo root is the package root, so
 * Unity imports everything under it that is not in a Unity-ignored location.
 * If any project under SourceGenerators/ builds its obj/ or bin/ IN-TREE, Unity
 * imports those build outputs. The analyzer/source-generator DLLs there share
 * the assembly names of the correctly-configured shipped analyzers in
 * Editor/Analyzers/, but their Unity-auto-generated .meta lack the
 * RoslynAnalyzer / validateReferences:0 / all-platforms-disabled config. Unity
 * then fails to resolve their Microsoft.CodeAnalysis reference and the duplicate
 * shadows the real analyzers, so the source generator never runs and every
 * [Dx*Message] type fails to implement its generated interface
 * (CS0315/CS0452) project-wide.
 *
 * The fix lives in SourceGenerators/Directory.Build.props, which redirects obj/
 * AND bin/ (plus NuGet restore's BaseIntermediateOutputPath) for EVERY project
 * under SourceGenerators/ into the Unity-ignored, git-ignored .artifacts/ tree.
 * The companion test source-generators-output-redirection.test.js STATICALLY
 * asserts that props file is shaped correctly.
 *
 * This test is the real-filesystem defense-in-depth for that static check. CI
 * runs the dotnet build BEFORE the jest suite, so if a regressed redirect ever
 * lets a build pollute in-tree obj/bin, this test sees the directories on disk
 * and fails. It asserts:
 *   1. No 'obj' or 'bin' directory exists anywhere under any
 *      SourceGenerators/<project>/ folder (a fast recursive fs walk).
 *   2. 'git ls-files' reports NO tracked path under any
 *      SourceGenerators/**\/{obj,bin}/ folder (so build output never gets
 *      committed even if a local walk happens to be clean).
 */

"use strict";

const { execFileSync } = require("child_process");
const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SOURCE_GENERATORS_DIR = path.join(REPO_ROOT, "SourceGenerators");

const BUILD_OUTPUT_DIR_NAMES = new Set(["obj", "bin"]);

/**
 * Recursively walk a directory and collect the repo-relative POSIX paths of
 * every directory named 'obj' or 'bin'. The walk does not descend into a
 * matched directory (its entire subtree is already a violation) and skips
 * symlinked directories so a stray link can never send the walk out of tree
 * or into a cycle.
 *
 * @param {string} dir - Absolute directory to walk.
 * @param {string[]} found - Accumulator of repo-relative POSIX paths.
 * @returns {string[]} The `found` accumulator.
 */
function collectBuildOutputDirs(dir, found) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch (error) {
    if (error.code === "ENOENT") {
      return found;
    }
    throw error;
  }

  for (const entry of entries) {
    if (!entry.isDirectory()) {
      continue;
    }
    const absolute = path.join(dir, entry.name);
    if (BUILD_OUTPUT_DIR_NAMES.has(entry.name)) {
      const relative = path.relative(REPO_ROOT, absolute).split(path.sep).join("/");
      found.push(relative);
      // Do not descend: the whole subtree is already flagged.
      continue;
    }
    collectBuildOutputDirs(absolute, found);
  }
  return found;
}

/**
 * Return the repo-relative POSIX paths that `git ls-files` reports under any
 * SourceGenerators/**\/{obj,bin}/ folder. Uses pathspecs so git does the
 * filtering and only matching tracked paths come back.
 *
 * @param {Function} [execFileSyncImpl] - Injectable for tests.
 * @returns {string[]}
 */
function listTrackedBuildOutput(execFileSyncImpl = execFileSync) {
  const output = execFileSyncImpl(
    "git",
    [
      "ls-files",
      "--",
      "SourceGenerators/**/obj/**",
      "SourceGenerators/**/bin/**",
      "SourceGenerators/**/obj/*",
      "SourceGenerators/**/bin/*"
    ],
    { cwd: REPO_ROOT, encoding: "utf8" }
  );
  return output
    .split(/\r\n|\r|\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

describe("SourceGenerators no in-tree build output", () => {
  test("SourceGenerators directory exists", () => {
    expect(fs.existsSync(SOURCE_GENERATORS_DIR)).toBe(true);
  });

  test("no 'obj' or 'bin' directory exists anywhere under SourceGenerators/", () => {
    const found = collectBuildOutputDirs(SOURCE_GENERATORS_DIR, []);
    if (found.length > 0) {
      throw new Error(
        "In-tree build output found under SourceGenerators/. The Directory.Build.props " +
          "redirect to .artifacts/ has regressed; Unity will import these obj/bin DLLs " +
          "and shadow the shipped analyzers in Editor/Analyzers/.\nOffending directories:\n  " +
          found.join("\n  ")
      );
    }
    expect(found).toEqual([]);
  });

  test("git tracks no path under SourceGenerators/**/{obj,bin}/", () => {
    const tracked = listTrackedBuildOutput();
    if (tracked.length > 0) {
      throw new Error(
        "Tracked build-output paths found under SourceGenerators/. These must never be " +
          "committed; the redirect to .artifacts/ keeps obj/bin out of the tree.\n" +
          "Offending tracked paths:\n  " +
          tracked.join("\n  ")
      );
    }
    expect(tracked).toEqual([]);
  });
});
