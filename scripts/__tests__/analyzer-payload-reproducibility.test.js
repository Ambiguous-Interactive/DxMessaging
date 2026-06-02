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

const PRODUCTION_PROJECTS = [
  {
    name: "source generator",
    path: path.join(
      SOURCE_GENERATORS_DIR,
      "WallstopStudios.DxMessaging.SourceGenerators",
      "WallstopStudios.DxMessaging.SourceGenerators.csproj"
    )
  },
  {
    name: "analyzer",
    path: path.join(
      SOURCE_GENERATORS_DIR,
      "WallstopStudios.DxMessaging.Analyzer",
      "WallstopStudios.DxMessaging.Analyzer.csproj"
    )
  }
];

function readUtf8(absPath) {
  return fs.readFileSync(absPath, "utf8");
}

function readBinaryText(absPath) {
  return fs.readFileSync(absPath).toString("latin1");
}

function getPropertyValue(content, name) {
  const match = new RegExp(`<${name}(?:\\s[^>]*)?>([^<]*)</${name}>`).exec(content);
  return match ? match[1].trim() : null;
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
  test.each([
    ["LangVersion", "10.0"],
    ["Deterministic", "true"],
    ["ContinuousIntegrationBuild", "true"],
    ["IncludeSourceRevisionInInformationalVersion", "false"],
    ["DebugType", "none"],
    ["DebugSymbols", "false"],
    ["CopyAnalyzerPayload", "true"]
  ])("Directory.Build.props sets %s=%s", (propertyName, expectedValue) => {
    const props = readUtf8(PROPS_PATH);

    expect(getPropertyValue(props, propertyName)).toBe(expectedValue);
  });

  test("Directory.Build.props exposes a redirectable AnalyzerPayloadOutputDir", () => {
    const props = readUtf8(PROPS_PATH);
    const outputDir = getPropertyValue(props, "AnalyzerPayloadOutputDir");

    expect(outputDir).not.toBeNull();
    expect(outputDir).toContain("Editor");
    expect(outputDir).toContain("Analyzers");
    expect(props).toContain("Condition=\"'$(AnalyzerPayloadOutputDir)' == ''\"");
  });

  test.each(PRODUCTION_PROJECTS)("$name project does not use LangVersion=latest", (project) => {
    const content = readUtf8(project.path);

    expect(content).not.toMatch(/<LangVersion>\s*latest\s*<\/LangVersion>/i);
  });

  test.each(PRODUCTION_PROJECTS)(
    "$name project pins Roslyn 3.8 and stable informational version",
    (project) => {
      const content = readUtf8(project.path);

      expect(content).toContain("<MicrosoftCodeAnalysisVersion>3.8.0</MicrosoftCodeAnalysisVersion>");
      expect(content).toContain('<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="3.8.0">');
      expect(content).toContain("<AssemblyInformationalVersion>$(Version)</AssemblyInformationalVersion>");
      expect(content).not.toMatch(/SourceRevision/i);
    }
  );

  test.each(PRODUCTION_PROJECTS)(
    "$name project copies only to AnalyzerPayloadOutputDir when enabled",
    (project) => {
      const content = readUtf8(project.path);

      expect(content).toContain(
        '<Target Name="PostBuildCopyAnalyzers" AfterTargets="Build" Condition="\'$(CopyAnalyzerPayload)\' == \'true\'">'
      );
      expect(content).toContain('DestinationFolder="$(AnalyzerPayloadOutputDir)"');
      expect(content).not.toContain("DestinationFolder=\"$(EditorAnalyzersDir)\"");
      expect(content).not.toContain("<EditorAnalyzersDir>");
    }
  );
});

describe("committed analyzer payload metadata", () => {
  test.each(FIRST_PARTY_ANALYZER_DLLS)("%s does not embed source revision or PDB metadata", (dllName) => {
    const dllText = readBinaryText(path.join(EDITOR_ANALYZERS_DIR, dllName));

    expect(dllText).not.toMatch(/\b\d+\.\d+\.\d+(?:\.\d+)?\+[0-9a-f]{7,40}\b/i);
    expect(dllText).not.toMatch(/\.pdb\b/i);
  });
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
