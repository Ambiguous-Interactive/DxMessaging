"use strict";

/**
 * @fileoverview SINGLE SOURCE OF TRUTH for "what the SourceGenerators build
 * config MUST contain". Both the jest contract test
 * (scripts/__tests__/analyzer-payload-reproducibility.test.js) and the fast
 * edit-time validator (scripts/validate-analyzer-build-contract.js consumed by
 * the post-edit-validate-guard) call into the SAME checks here, so the contract
 * is asserted in exactly one place and the two can never drift. The concrete
 * required VALUES that the jest suite also re-asserts in layered checks -- the
 * Roslyn pin (ROSLYN_VERSION), the Microsoft.CodeAnalysis.CSharp PackageReference
 * spec (ROSLYN_PACKAGE_REFERENCE), the PostBuildCopyAnalyzers Target spec
 * (POST_BUILD_COPY_TARGET), and the scalar REQUIRED_PROPS map -- are EXPORTED from
 * here and consumed by the suite, so no value is hand-duplicated across the two
 * files; changing a pin or the target's gating Condition in this one place updates
 * both the validator and the suite.
 *
 * Every check is expressed STRUCTURALLY via scripts/lib/msbuild-xml.js so it is
 * invariant to XML line-wrapping, attribute order, and CRLF (the repo's prettier
 * does not format XML, so those vary freely). A check returns
 * { ok:boolean, message:string }; `message` is a human-readable diagnostic used
 * when ok is false.
 *
 * Pure Node, zero runtime dependencies, cross-platform.
 */

const fs = require("fs");
const path = require("path");
const { getPropertyValue, resolveProperty, hasElement, getElements } = require("./msbuild-xml");

/**
 * MSBuild element types that can carry a `Condition` attribute gating the
 * build-output redirect. Scanned structurally (via getElements) so the Tests-only
 * guard is invariant to line-wrapping / attribute order / CRLF, the same as every
 * other check here. Individual property elements (e.g. <BaseIntermediateOutputPath
 * Condition="...">) are also condition-bearing, but the redirect properties this
 * contract governs are enumerated explicitly below so they are covered directly.
 */
const CONDITION_BEARING_ELEMENTS = Object.freeze([
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
]);

/**
 * True iff any condition-bearing element in `props` carries a `Condition`
 * attribute whose value mentions the SourceGenerators.Tests project -- the old
 * (buggy) shape that gated the whole redirect on the Tests project name and left
 * the two production projects building in-tree. Read STRUCTURALLY so it never
 * depends on the `Condition="..."` attribute living on the same line as the `>`.
 *
 * @param {string} props Directory.Build.props content.
 * @returns {boolean}
 */
function hasTestsOnlyConditionGate(props) {
  for (const elementName of CONDITION_BEARING_ELEMENTS) {
    for (const element of getElements(props, elementName)) {
      const condition = element.attributes.Condition;
      if (typeof condition === "string" && condition.includes("SourceGenerators.Tests")) {
        return true;
      }
    }
  }
  return false;
}

/**
 * Repo-relative locations of the build files this contract governs. Resolved
 * against an injectable repo root so the module works from any cwd and is
 * testable.
 */
const SOURCE_GENERATORS_DIR = "SourceGenerators";
const PROPS_REL = path.posix.join(SOURCE_GENERATORS_DIR, "Directory.Build.props");
const PRODUCTION_PROJECTS = Object.freeze([
  {
    name: "source generator",
    rel: "SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.csproj"
  },
  {
    name: "analyzer",
    rel: "SourceGenerators/WallstopStudios.DxMessaging.Analyzer/WallstopStudios.DxMessaging.Analyzer.csproj"
  }
]);

/** Required scalar properties in Directory.Build.props (name -> expected value). */
const REQUIRED_PROPS = Object.freeze({
  LangVersion: "10.0",
  Deterministic: "true",
  ContinuousIntegrationBuild: "true",
  IncludeSourceRevisionInInformationalVersion: "false",
  DebugType: "none",
  DebugSymbols: "false",
  CopyAnalyzerPayload: "true"
});

/**
 * Per-production-project required specs, exported as the SINGLE SOURCE OF TRUTH so
 * both the contract checks below AND the jest suite drive off the same constants
 * (no verbatim duplication that could silently drift). Each is consumed by
 * hasElement / getPropertyValue, so they are line-wrap- and attribute-order-
 * invariant.
 *
 *   ROSLYN_VERSION                 the Unity-2021 Microsoft.CodeAnalysis pin
 *   ROSLYN_PACKAGE_REFERENCE       the <PackageReference> hasElement spec
 *   POST_BUILD_COPY_TARGET         the <Target> hasElement spec
 */
const ROSLYN_VERSION = "3.8.0";
const ROSLYN_PACKAGE_REFERENCE = Object.freeze({
  name: "PackageReference",
  attributes: Object.freeze({
    Include: "Microsoft.CodeAnalysis.CSharp",
    Version: ROSLYN_VERSION
  })
});
const POST_BUILD_COPY_TARGET = Object.freeze({
  name: "Target",
  attributes: Object.freeze({
    Name: "PostBuildCopyAnalyzers",
    AfterTargets: "Build",
    Condition: "'$(CopyAnalyzerPayload)' == 'true'"
  })
});

/**
 * Resolve a contract file to an absolute path under the repo root.
 *
 * @param {string} repoRoot Absolute repo root.
 * @param {string} rel Repo-relative POSIX path.
 * @returns {string} Absolute path.
 */
function abs(repoRoot, rel) {
  return path.join(repoRoot, rel.split("/").join(path.sep));
}

/**
 * Read a file as UTF-8, returning null when it does not exist.
 *
 * @param {string} absPath Absolute path.
 * @returns {string|null} Content or null.
 */
function readOrNull(absPath) {
  try {
    return fs.readFileSync(absPath, "utf8");
  } catch (error) {
    if (error && error.code === "ENOENT") {
      return null;
    }
    throw error;
  }
}

/**
 * Build the full list of contract checks against the on-disk SourceGenerators
 * build files. Each check is { ok, message }.
 *
 * @param {string} repoRoot Absolute repo root.
 * @returns {Array<{ok:boolean, message:string}>} Ordered checks.
 */
function evaluateContract(repoRoot) {
  const checks = [];
  const propsPath = abs(repoRoot, PROPS_REL);
  const props = readOrNull(propsPath);

  if (props === null) {
    checks.push({ ok: false, message: `${PROPS_REL} is missing` });
    return checks;
  }

  // Required reproducible-build scalar properties.
  for (const [name, expected] of Object.entries(REQUIRED_PROPS)) {
    const actual = getPropertyValue(props, name);
    checks.push({
      ok: actual === expected,
      message: `${PROPS_REL}: <${name}> must be "${expected}" (found ${
        actual === null ? "absent" : `"${actual}"`
      })`
    });
  }

  // Redirectable analyzer payload output dir.
  const outputDir = getPropertyValue(props, "AnalyzerPayloadOutputDir");
  checks.push({
    ok: outputDir !== null && outputDir.includes("Editor") && outputDir.includes("Analyzers"),
    message: `${PROPS_REL}: <AnalyzerPayloadOutputDir> must resolve under Editor/Analyzers (found ${
      outputDir === null ? "absent" : `"${outputDir}"`
    })`
  });

  // obj/bin/restore output all redirect under .artifacts, keyed per project.
  for (const propName of ["BaseIntermediateOutputPath", "IntermediateOutputPath", "OutputPath"]) {
    const resolved = resolveProperty(props, propName);
    const ok = resolved !== null && /\.artifacts/.test(resolved) && !/\$\(/.test(resolved);
    checks.push({
      ok,
      message: `${PROPS_REL}: <${propName}> must resolve under .artifacts/ with no unresolved $(...) (resolved to ${
        resolved === null ? "absent" : `"${resolved}"`
      })`
    });
  }

  const artifactsRoot = getPropertyValue(props, "ArtifactsRoot");
  checks.push({
    ok:
      artifactsRoot !== null &&
      /\.artifacts/.test(artifactsRoot) &&
      /\$\(MSBuildProjectName\)/.test(artifactsRoot),
    message: `${PROPS_REL}: <ArtifactsRoot> must be under .artifacts/ and keyed on $(MSBuildProjectName) (found ${
      artifactsRoot === null ? "absent" : `"${artifactsRoot}"`
    })`
  });

  // The redirect must not be gated on a Tests-only condition anywhere. Checked
  // structurally (no `Condition[^>]*...` single-line-tag regex) so this guard is
  // as formatting-invariant as the rest of the contract.
  checks.push({
    ok: !hasTestsOnlyConditionGate(props),
    message: `${PROPS_REL}: build-output redirect must not be gated on a SourceGenerators.Tests-only Condition`
  });

  // Per-project csproj invariants (Roslyn pin + post-build copy target).
  for (const project of PRODUCTION_PROJECTS) {
    const projPath = abs(repoRoot, project.rel);
    const content = readOrNull(projPath);
    if (content === null) {
      checks.push({ ok: false, message: `${project.rel} is missing` });
      continue;
    }

    checks.push({
      ok: getPropertyValue(content, "MicrosoftCodeAnalysisVersion") === ROSLYN_VERSION,
      message: `${project.rel}: <MicrosoftCodeAnalysisVersion> must be "${ROSLYN_VERSION}" (Unity 2021 Roslyn pin)`
    });

    checks.push({
      ok: hasElement(content, ROSLYN_PACKAGE_REFERENCE),
      message: `${project.rel}: must reference Microsoft.CodeAnalysis.CSharp ${ROSLYN_VERSION} (PackageReference)`
    });

    checks.push({
      ok: hasElement(content, POST_BUILD_COPY_TARGET),
      message: `${project.rel}: must define the PostBuildCopyAnalyzers target (AfterTargets=Build, gated on CopyAnalyzerPayload)`
    });

    // Expressed structurally via getPropertyValue (case-insensitive on the
    // value) rather than a single-line `<LangVersion>latest</LangVersion>`
    // regex, so the contract module is itself uniformly formatting-invariant and
    // does not embed the fragile pattern this whole change exists to police. A
    // wrapped `<LangVersion\n>latest</LangVersion\n>` is still caught.
    const csprojLangVersion = getPropertyValue(content, "LangVersion");
    checks.push({
      ok: csprojLangVersion === null || csprojLangVersion.trim().toLowerCase() !== "latest",
      message: `${project.rel}: must not set <LangVersion>latest</LangVersion>`
    });
  }

  return checks;
}

module.exports = {
  PROPS_REL,
  PRODUCTION_PROJECTS,
  REQUIRED_PROPS,
  ROSLYN_VERSION,
  ROSLYN_PACKAGE_REFERENCE,
  POST_BUILD_COPY_TARGET,
  evaluateContract
};
