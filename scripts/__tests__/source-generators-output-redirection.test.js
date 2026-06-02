/**
 * @fileoverview Regression guard for SourceGenerators build-output redirection.
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
 * The fix lives in SourceGenerators/Directory.Build.props: it redirects obj/
 * AND bin/ (plus NuGet restore's BaseIntermediateOutputPath) for EVERY project
 * under SourceGenerators/ into the Unity-ignored, git-ignored .artifacts/ tree.
 * Earlier the redirect was gated on a Tests-only condition
 * ('$(MSBuildProjectName)' == '...SourceGenerators.Tests'), which left the two
 * main projects building in-tree -> the bug.
 *
 * This is a fast STATIC assertion on Directory.Build.props (a full
 * 'dotnet build' regression check is far slower and is covered manually in the
 * fix's verification). It asserts:
 *   1. The obj/bin/restore redirect is NOT gated on a Tests-only condition.
 *   2. BaseIntermediateOutputPath, IntermediateOutputPath, and OutputPath all
 *      point under .artifacts (keyed off $(MSBuildProjectName) so every project
 *      gets its own subtree).
 *   3. .artifacts/ is git-ignored, so the redirected output never pollutes the
 *      working tree.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { getPropertyValue, resolveProperty, getElements } = require("../lib/msbuild-xml");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const PROPS_PATH = path.join(REPO_ROOT, "SourceGenerators", "Directory.Build.props");
const GITIGNORE_PATH = path.join(REPO_ROOT, ".gitignore");

function readProps() {
  return fs.readFileSync(PROPS_PATH, "utf8");
}

/**
 * Element types that can carry a `Condition` attribute gating the build-output
 * redirect. Scanned structurally so the Tests-only guard is invariant to
 * line-wrapping / attribute order / CRLF.
 */
const CONDITION_BEARING_ELEMENTS = [
  "PropertyGroup",
  "ItemGroup",
  "Target",
  "When",
  "Otherwise",
  "BaseIntermediateOutputPath",
  "IntermediateOutputPath",
  "OutputPath",
  "ArtifactsRoot",
  "AnalyzerPayloadOutputDir"
];

/**
 * The Condition attribute (or null when unconditional) of every <PropertyGroup>,
 * read STRUCTURALLY via the shared helper's getElements so it is invariant to
 * line-wrapping / attribute order / CRLF (no hand-rolled `/<PropertyGroup\b...>/`
 * open-tag regex, which is exactly the fragile shape this change forbids).
 *
 * @param {string} content
 * @returns {Array<string|null>}
 */
function propertyGroupConditions(content) {
  return getElements(content, "PropertyGroup").map((element) =>
    Object.prototype.hasOwnProperty.call(element.attributes, "Condition")
      ? element.attributes.Condition
      : null
  );
}

/**
 * Every Condition attribute value across all condition-bearing element types,
 * read STRUCTURALLY via getElements (no `Condition[^>]*...` single-line-tag
 * regex, which is exactly the fragile shape this change exists to forbid).
 *
 * @param {string} content
 * @returns {string[]}
 */
function allConditions(content) {
  const conditions = [];
  for (const elementName of CONDITION_BEARING_ELEMENTS) {
    for (const element of getElements(content, elementName)) {
      if (typeof element.attributes.Condition === "string") {
        conditions.push(element.attributes.Condition);
      }
    }
  }
  return conditions;
}

describe("SourceGenerators build-output redirection", () => {
  test("Directory.Build.props exists", () => {
    expect(fs.existsSync(PROPS_PATH)).toBe(true);
  });

  test("redirect is NOT gated on a Tests-only condition", () => {
    const content = readProps();
    const conditions = propertyGroupConditions(content);

    // The old (buggy) shape gated the whole redirect on the Tests project name.
    // Assert no PropertyGroup is conditioned on the Tests project specifically.
    const testsOnly = conditions.filter(
      (condition) =>
        condition !== null && /MSBuildProjectName.*SourceGenerators\.Tests/.test(condition)
    );
    expect(testsOnly).toEqual([]);

    // And, defensively, NO condition-bearing element anywhere in the file may
    // carry a Condition that mentions the Tests project. Read structurally over
    // every Condition attribute value (not a `Condition[^>]*...` regex).
    const testsGated = allConditions(content).filter((condition) =>
      condition.includes("SourceGenerators.Tests")
    );
    expect(testsGated).toEqual([]);
  });

  test("obj/bin/restore output all redirect under .artifacts keyed on the project name", () => {
    const content = readProps();

    const baseIntermediate = getPropertyValue(content, "BaseIntermediateOutputPath");
    const intermediate = getPropertyValue(content, "IntermediateOutputPath");
    const outputPath = getPropertyValue(content, "OutputPath");

    // All three must be present so NuGet restore (BaseIntermediateOutputPath),
    // the obj/ build intermediates (IntermediateOutputPath), and the bin/ build
    // output (OutputPath) are redirected.
    expect(baseIntermediate).not.toBeNull();
    expect(intermediate).not.toBeNull();
    expect(outputPath).not.toBeNull();

    // The raw values must NOT be a bare in-tree "obj"/"bin".
    expect(baseIntermediate).not.toMatch(/^obj[\\/]?$/);
    expect(outputPath).not.toMatch(/^bin[\\/]?$/);

    // After transitively expanding $(...) references, each must resolve under
    // the Unity-ignored .artifacts tree (an unresolved reference would leave a
    // literal "$(...)" and fail this match).
    const resolvedBase = resolveProperty(content, "BaseIntermediateOutputPath");
    const resolvedIntermediate = resolveProperty(content, "IntermediateOutputPath");
    const resolvedOutput = resolveProperty(content, "OutputPath");
    for (const value of [resolvedBase, resolvedIntermediate, resolvedOutput]) {
      expect(value).toMatch(/\.artifacts/);
      expect(value).not.toMatch(/\$\(/);
    }

    // The redirect must be per-project (so each project gets its own subtree
    // and there is never a shared/in-tree obj or bin). The ArtifactsRoot is
    // keyed on $(MSBuildProjectName); assert that key is wired in and that two
    // distinct project names resolve to distinct .artifacts subtrees.
    const artifactsRoot = getPropertyValue(content, "ArtifactsRoot");
    expect(artifactsRoot).not.toBeNull();
    expect(artifactsRoot).toMatch(/\.artifacts/);
    expect(artifactsRoot).toMatch(/\$\(MSBuildProjectName\)/);

    const outputForA = resolveProperty(content, "OutputPath", { MSBuildProjectName: "ProjA" });
    const outputForB = resolveProperty(content, "OutputPath", { MSBuildProjectName: "ProjB" });
    expect(outputForA).toMatch(/ProjA/);
    expect(outputForB).toMatch(/ProjB/);
    expect(outputForA).not.toEqual(outputForB);
  });

  test(".artifacts is git-ignored so redirected output never pollutes the tree", () => {
    const gitignore = fs.readFileSync(GITIGNORE_PATH, "utf8");
    const lines = gitignore.split(/\r\n|\r|\n/).map((line) => line.trim());
    expect(lines).toContain(".artifacts/");
  });
});
