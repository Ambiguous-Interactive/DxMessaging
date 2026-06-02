/**
 * @fileoverview Static contract tests for the shipped analyzer payload build.
 *
 * The two first-party DLLs under Editor/Analyzers must be byte-reproducible:
 * CI builds them twice into temp payload directories, compares those outputs
 * to each other, and only then compares them to the committed payload. These
 * tests keep that contract wired without running a full dotnet build.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { getPropertyValue, hasElement, getElements } = require("../lib/msbuild-xml");
// SINGLE SOURCE OF TRUTH: the structural contract (required props, the Roslyn
// 3.8.0 pin, the PostBuildCopyAnalyzers target, the .artifacts redirect) lives in
// scripts/lib/analyzer-build-contract.js and is consumed BOTH here and by the
// edit-time validator (scripts/validate-analyzer-build-contract.js). This suite
// drives its property/element assertions off the exported data + evaluateContract
// so the contract is defined in exactly one place and the two cannot drift.
const {
  evaluateContract,
  REQUIRED_PROPS,
  ROSLYN_VERSION,
  ROSLYN_PACKAGE_REFERENCE,
  POST_BUILD_COPY_TARGET,
  PRODUCTION_PROJECTS: CONTRACT_PROJECTS
} = require("../lib/analyzer-build-contract");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SOURCE_GENERATORS_DIR = path.join(REPO_ROOT, "SourceGenerators");
const PROPS_PATH = path.join(SOURCE_GENERATORS_DIR, "Directory.Build.props");
const GLOBAL_JSON_PATH = path.join(SOURCE_GENERATORS_DIR, "global.json");
const DOTNET_WORKFLOW_PATH = path.join(REPO_ROOT, ".github", "workflows", "dotnet-tests.yml");
const PACKAGE_JSON_PATH = path.join(REPO_ROOT, "package.json");
const EDITOR_ANALYZERS_DIR = path.join(REPO_ROOT, "Editor", "Analyzers");
const VERIFY_SCRIPT_PATH = path.join(
  REPO_ROOT,
  "scripts",
  "analyzers",
  "verify-analyzer-payload.js"
);

const FIRST_PARTY_ANALYZER_DLLS = [
  "WallstopStudios.DxMessaging.SourceGenerators.dll",
  "WallstopStudios.DxMessaging.Analyzer.dll"
];

// Derived from the shared contract's PRODUCTION_PROJECTS (repo-relative POSIX
// `rel`) so the project set is defined once. The suite-only assertions below
// (AssemblyInformationalVersion, DestinationFolder, no LangVersion=latest) layer
// on top of the structural contract checks.
const PRODUCTION_PROJECTS = CONTRACT_PROJECTS.map((project) => ({
  name: project.name,
  path: path.join(REPO_ROOT, project.rel.split("/").join(path.sep))
}));

function readUtf8(absPath) {
  return fs.readFileSync(absPath, "utf8");
}

function readBinaryText(absPath) {
  return fs.readFileSync(absPath).toString("latin1");
}

describe("analyzer payload SDK pin", () => {
  test("SourceGenerators/global.json pins the canonical artifact SDK", () => {
    const parsed = JSON.parse(readUtf8(GLOBAL_JSON_PATH));

    expect(parsed).toEqual({
      sdk: {
        version: "9.0.314",
        rollForward: "disable",
        allowPrerelease: false
      }
    });
  });

  test("dotnet-tests workflow installs .NET from SourceGenerators/global.json", () => {
    const workflow = readUtf8(DOTNET_WORKFLOW_PATH);

    expect(workflow).toContain("uses: actions/setup-dotnet@v5");
    expect(workflow).toContain("global-json-file: SourceGenerators/global.json");
    expect(workflow).not.toContain('dotnet-version: "9.0.x"');
  });
});

describe("analyzer payload reproducible build properties", () => {
  test("the full structural build contract passes (single source of truth)", () => {
    // Drives every check in scripts/lib/analyzer-build-contract.js -- the same
    // checks the edit-time validator runs -- so this suite and the validator can
    // never disagree about what the build config must contain.
    const failures = evaluateContract(REPO_ROOT).filter((check) => !check.ok);
    expect(failures.map((check) => check.message)).toEqual([]);
  });

  // Required values come from the shared contract's REQUIRED_PROPS map, not a
  // duplicated inline list, so a change to a pinned value updates both this
  // suite and the validator in one edit.
  test.each(Object.entries(REQUIRED_PROPS))(
    "Directory.Build.props sets %s=%s",
    (propertyName, expectedValue) => {
      const props = readUtf8(PROPS_PATH);

      expect(getPropertyValue(props, propertyName)).toBe(expectedValue);
    }
  );

  test("Directory.Build.props exposes a redirectable AnalyzerPayloadOutputDir", () => {
    const props = readUtf8(PROPS_PATH);
    const outputDir = getPropertyValue(props, "AnalyzerPayloadOutputDir");

    expect(outputDir).not.toBeNull();
    expect(outputDir).toContain("Editor");
    expect(outputDir).toContain("Analyzers");
    expect(
      hasElement(props, {
        name: "AnalyzerPayloadOutputDir",
        attributes: { Condition: "'$(AnalyzerPayloadOutputDir)' == ''" }
      })
    ).toBe(true);
  });

  test.each(PRODUCTION_PROJECTS)("$name project does not use LangVersion=latest", (project) => {
    const content = readUtf8(project.path);

    // Structural (not a single-line `<LangVersion>latest</LangVersion>` regex):
    // either the csproj inherits LangVersion from Directory.Build.props (absent
    // here) or, if present, it must not be "latest". Invariant to line-wrapping.
    const langVersion = getPropertyValue(content, "LangVersion");
    expect(langVersion === null || langVersion.trim().toLowerCase() !== "latest").toBe(true);
  });

  test.each(PRODUCTION_PROJECTS)(
    "$name project pins Roslyn 3.8 and stable informational version",
    (project) => {
      const content = readUtf8(project.path);

      // Roslyn pin + PackageReference spec come from the shared contract's exports
      // (not hard-coded "3.8.0" here), so the pin is defined in exactly one place
      // and this suite cannot drift from the validator.
      expect(getPropertyValue(content, "MicrosoftCodeAnalysisVersion")).toBe(ROSLYN_VERSION);
      expect(hasElement(content, ROSLYN_PACKAGE_REFERENCE)).toBe(true);
      // AssemblyInformationalVersion / no-SourceRevision are genuinely suite-only
      // layered checks (not part of the structural contract module).
      expect(getPropertyValue(content, "AssemblyInformationalVersion")).toBe("$(Version)");
      expect(content).not.toMatch(/SourceRevision/i);
    }
  );

  test.each(PRODUCTION_PROJECTS)(
    "$name project copies only to AnalyzerPayloadOutputDir when enabled",
    (project) => {
      const content = readUtf8(project.path);

      // The PostBuildCopyAnalyzers Target spec (name + AfterTargets + gating
      // Condition) is the shared contract's export, not a verbatim duplicate, so
      // changing the gating Condition in the contract updates this suite too.
      expect(hasElement(content, POST_BUILD_COPY_TARGET)).toBe(true);
      // Structural: the <Copy> element's DestinationFolder must be the redirectable
      // payload dir. A contiguous `DestinationFolder="..."` substring would break if
      // a formatter wrapped that attribute's value onto its own line; hasElement is
      // line-wrap- and attribute-order-invariant.
      expect(
        hasElement(content, {
          name: "Copy",
          attributes: { DestinationFolder: "$(AnalyzerPayloadOutputDir)" }
        })
      ).toBe(true);
      // And NO <Copy> targets the (nonexistent) EditorAnalyzersDir, checked over
      // every <Copy> occurrence rather than via a fragile contiguous substring.
      expect(
        getElements(content, "Copy").every(
          (copy) => copy.attributes.DestinationFolder !== "$(EditorAnalyzersDir)"
        )
      ).toBe(true);
      expect(getElements(content, "EditorAnalyzersDir")).toEqual([]);
    }
  );
});

describe("committed analyzer payload metadata", () => {
  test.each(FIRST_PARTY_ANALYZER_DLLS)(
    "%s does not embed source revision or PDB metadata",
    (dllName) => {
      const dllText = readBinaryText(path.join(EDITOR_ANALYZERS_DIR, dllName));

      expect(dllText).not.toMatch(/\b\d+\.\d+\.\d+(?:\.\d+)?\+[0-9a-f]{7,40}\b/i);
      expect(dllText).not.toMatch(/\.pdb\b/i);
    }
  );
});

describe("analyzer payload verifier contract", () => {
  test("package.json exposes refresh/check analyzer commands", () => {
    const parsed = JSON.parse(readUtf8(PACKAGE_JSON_PATH));

    expect(parsed.scripts["refresh:analyzers"]).toBe(
      "node scripts/analyzers/verify-analyzer-payload.js --write"
    );
    expect(parsed.scripts["check:analyzers"]).toBe(
      "node scripts/analyzers/verify-analyzer-payload.js --check"
    );
  });

  test("verifier allowlist contains only the two first-party DLLs", () => {
    const { FIRST_PARTY_ANALYZER_DLLS: exported } = require("../analyzers/verify-analyzer-payload");

    expect(exported).toEqual(FIRST_PARTY_ANALYZER_DLLS);
    expect(exported).not.toContain("Microsoft.CodeAnalysis.dll");
    expect(exported).not.toContain("System.Collections.Immutable.dll");
  });

  test("check mode double-builds into separate temp payload directories", () => {
    const script = readUtf8(VERIFY_SCRIPT_PATH);

    expect(script).toContain('buildAnalyzerPayload("check-a", firstPayload)');
    expect(script).toContain('buildAnalyzerPayload("check-b", secondPayload)');
    expect(script).toContain("compareHashMaps(first, second)");
    expect(script).toContain("Analyzer payload is not reproducible across two clean builds.");
  });

  test("write mode stages output before updating only the committed first-party DLLs", () => {
    const script = readUtf8(VERIFY_SCRIPT_PATH);

    expect(script).toContain('buildAnalyzerPayload("write", payloadDir)');
    expect(script).toContain("copyGeneratedPayload(payloadDir)");
    expect(script).toContain("for (const dllName of FIRST_PARTY_ANALYZER_DLLS)");
    expect(script).toContain("Editor/Analyzers");
  });

  test("stale-payload diagnostics include exact remediation and PE metadata", () => {
    const script = readUtf8(VERIFY_SCRIPT_PATH);

    expect(script).toContain("npm run refresh:analyzers");
    expect(script).toContain("informationalVersion=");
    expect(script).toContain("mvid=");
    expect(script).toContain("debugDirectory=");
    expect(script).toContain("sha256");
  });
});

describe("dotnet-tests workflow analyzer payload guard", () => {
  test("source-generator restore/test disables analyzer payload copying", () => {
    const workflow = readUtf8(DOTNET_WORKFLOW_PATH);

    expect(workflow).toContain("working-directory: SourceGenerators");
    expect(workflow).toContain("/p:CopyAnalyzerPayload=false");
  });

  test("freshness guard delegates to npm run check:analyzers", () => {
    const workflow = readUtf8(DOTNET_WORKFLOW_PATH);

    expect(workflow).toContain("run: npm run check:analyzers");
    expect(workflow).not.toContain("git diff --exit-code --");
  });
});
