#!/usr/bin/env node
"use strict";

const crypto = require("crypto");
const fs = require("fs");
const os = require("os");
const path = require("path");

const { spawnPlatformCommandSync } = require("../lib/shell-command");
const { toRepoPosixRelative } = require("../lib/path-classifier");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SOURCE_GENERATORS_DIR = path.join(REPO_ROOT, "SourceGenerators");
const EDITOR_ANALYZERS_DIR = path.join(REPO_ROOT, "Editor", "Analyzers");
const ARTIFACT_ROOT = path.join(REPO_ROOT, ".artifacts", "analyzer-payload");
const CONFIGURATION = "Release";
const REMEDIATION = "npm run refresh:analyzers";

const FIRST_PARTY_ANALYZER_DLLS = Object.freeze([
  "WallstopStudios.DxMessaging.SourceGenerators.dll",
  "WallstopStudios.DxMessaging.Analyzer.dll"
]);

function usage() {
  return [
    "Usage: node scripts/analyzers/verify-analyzer-payload.js (--check|--write)",
    "",
    "  --write  Build the canonical analyzer payload and refresh the two first-party DLLs.",
    "  --check  Build the payload twice, assert reproducible bytes, then compare committed DLLs."
  ].join("\n");
}

function parseArgs(argv) {
  const modes = argv.filter((arg) => arg === "--check" || arg === "--write");
  if (modes.length !== 1 || modes.length !== argv.length) {
    throw new Error(usage());
  }
  return { mode: modes[0].slice(2) };
}

function rmDir(dir) {
  fs.rmSync(dir, { recursive: true, force: true });
}

function ensureDir(dir) {
  fs.mkdirSync(dir, { recursive: true });
}

function repoRelative(absPath) {
  return toRepoPosixRelative(absPath, REPO_ROOT);
}

function run(command, args, options = {}) {
  const result = spawnPlatformCommandSync(command, args, {
    cwd: options.cwd || REPO_ROOT,
    encoding: "utf8",
    env: options.env || process.env,
    maxBuffer: 32 * 1024 * 1024,
    stdio: options.stdio || "pipe"
  });

  if (result.status !== 0) {
    const rendered = [command, ...args].join(" ");
    const details = [result.stdout, result.stderr].filter(Boolean).join("\n").trim();
    throw new Error(`${rendered} failed with exit code ${result.status}\n${details}`);
  }

  return result.stdout || "";
}

function getDotnetVersion() {
  try {
    return run("dotnet", ["--version"], { cwd: SOURCE_GENERATORS_DIR }).trim();
  } catch (error) {
    return `unavailable (${error.message.split(/\r?\n/)[0]})`;
  }
}

function projectPath(projectName) {
  return path.join(SOURCE_GENERATORS_DIR, projectName, `${projectName}.csproj`);
}

function buildAnalyzerPayload(label, payloadDir) {
  const buildRoot = path.join(ARTIFACT_ROOT, label, "build");
  rmDir(buildRoot);
  rmDir(payloadDir);
  ensureDir(payloadDir);

  const msbuildArtifactsRoot = `${buildRoot.split(path.sep).join("/")}/$(MSBuildProjectName)/`;
  const commonArgs = [
    "--configuration",
    CONFIGURATION,
    `/p:ArtifactsRoot=${msbuildArtifactsRoot}`,
    `/p:AnalyzerPayloadOutputDir=${payloadDir}`,
    "/p:CopyAnalyzerPayload=true"
  ];

  for (const projectName of [
    "WallstopStudios.DxMessaging.SourceGenerators",
    "WallstopStudios.DxMessaging.Analyzer"
  ]) {
    run("dotnet", ["build", projectPath(projectName), ...commonArgs], {
      cwd: SOURCE_GENERATORS_DIR
    });
  }

  const unexpected = fs
    .readdirSync(payloadDir, { withFileTypes: true })
    .filter((entry) => entry.isFile() && entry.name.toLowerCase().endsWith(".dll"))
    .map((entry) => entry.name)
    .filter((name) => !FIRST_PARTY_ANALYZER_DLLS.includes(name));

  if (unexpected.length > 0) {
    throw new Error(
      `Analyzer payload build emitted unexpected DLL(s): ${unexpected.join(", ")}. ` +
        "Only first-party analyzer DLLs may be refreshed or checked."
    );
  }

  for (const dllName of FIRST_PARTY_ANALYZER_DLLS) {
    const generated = path.join(payloadDir, dllName);
    if (!fs.existsSync(generated)) {
      throw new Error(`Analyzer payload build did not produce ${repoRelative(generated)}.`);
    }
  }
}

function sha256(filePath) {
  return crypto.createHash("sha256").update(fs.readFileSync(filePath)).digest("hex");
}

function payloadHashes(payloadDir) {
  const hashes = new Map();
  for (const dllName of FIRST_PARTY_ANALYZER_DLLS) {
    hashes.set(dllName, sha256(path.join(payloadDir, dllName)));
  }
  return hashes;
}

function compareHashMaps(left, right) {
  return FIRST_PARTY_ANALYZER_DLLS.filter((dllName) => left.get(dllName) !== right.get(dllName));
}

function copyGeneratedPayload(payloadDir) {
  ensureDir(EDITOR_ANALYZERS_DIR);
  for (const dllName of FIRST_PARTY_ANALYZER_DLLS) {
    fs.copyFileSync(path.join(payloadDir, dllName), path.join(EDITOR_ANALYZERS_DIR, dllName));
  }
}

function createMetadataReaderProject(projectDir) {
  ensureDir(projectDir);
  fs.writeFileSync(
    path.join(projectDir, "AnalyzerMetadataReader.csproj"),
    [
      '<Project Sdk="Microsoft.NET.Sdk">',
      "  <PropertyGroup>",
      "    <OutputType>Exe</OutputType>",
      "    <TargetFramework>net9.0</TargetFramework>",
      "    <ImplicitUsings>enable</ImplicitUsings>",
      "    <Nullable>enable</Nullable>",
      "  </PropertyGroup>",
      "</Project>",
      ""
    ].join(os.EOL),
    "utf8"
  );
  fs.writeFileSync(
    path.join(projectDir, "Program.cs"),
    [
      "using System.Reflection.Metadata;",
      "using System.Reflection.PortableExecutable;",
      "",
      "foreach (string file in args)",
      "{",
      "    using FileStream stream = File.OpenRead(file);",
      "    using PEReader peReader = new(stream);",
      "    MetadataReader metadata = peReader.GetMetadataReader();",
      "    ModuleDefinition module = metadata.GetModuleDefinition();",
      "    string mvid = metadata.GetGuid(module.Mvid).ToString();",
      '    string informationalVersion = "(missing)";',
      "    foreach (CustomAttributeHandle handle in metadata.GetAssemblyDefinition().GetCustomAttributes())",
      "    {",
      "        CustomAttribute attribute = metadata.GetCustomAttribute(handle);",
      "        EntityHandle constructor = attribute.Constructor;",
      "        EntityHandle container = constructor.Kind switch",
      "        {",
      "            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent,",
      "            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),",
      "            _ => default",
      "        };",
      "        if (container.Kind != HandleKind.TypeReference && container.Kind != HandleKind.TypeDefinition)",
      "        {",
      "            continue;",
      "        }",
      "        string name = container.Kind == HandleKind.TypeReference",
      "            ? metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)container).Name)",
      "            : metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)container).Name);",
      '        if (name != "AssemblyInformationalVersionAttribute")',
      "        {",
      "            continue;",
      "        }",
      "        BlobReader blob = metadata.GetBlobReader(attribute.Value);",
      "        if (blob.ReadUInt16() == 1)",
      "        {",
      '            informationalVersion = blob.ReadSerializedString() ?? "(null)";',
      "        }",
      "    }",
      '    string debugTypes = string.Join(",", peReader.ReadDebugDirectory().Select(entry => entry.Type.ToString()));',
      '    Console.WriteLine($"{Path.GetFileName(file)} informationalVersion={informationalVersion} mvid={mvid} debugDirectory=[{debugTypes}]");',
      "}",
      ""
    ].join(os.EOL),
    "utf8"
  );
}

function metadataDiagnostics(paths) {
  const projectDir = path.join(ARTIFACT_ROOT, "metadata-reader");
  createMetadataReaderProject(projectDir);
  try {
    return run("dotnet", ["run", "--project", projectDir, "--", ...paths], {
      cwd: REPO_ROOT
    }).trim();
  } catch (error) {
    return `metadata unavailable: ${error.message}`;
  }
}

function printPayloadDiagnostics(title, rows, metadataPaths) {
  console.error("");
  console.error(title);
  console.error(`SDK version: ${getDotnetVersion()}`);
  for (const row of rows) {
    console.error(
      `${row.dllName}: generated=${row.generatedHash || "(missing)"} ` +
        `committed=${row.committedHash || "(missing)"} ` +
        `firstBuild=${row.firstHash || "(missing)"} secondBuild=${row.secondHash || "(missing)"}`
    );
  }
  console.error("");
  console.error("PE metadata:");
  console.error(metadataDiagnostics(metadataPaths));
  console.error("");
  console.error(`Remediation: ${REMEDIATION}`);
}

function checkPayload() {
  const firstPayload = path.join(ARTIFACT_ROOT, "check-a", "payload");
  const secondPayload = path.join(ARTIFACT_ROOT, "check-b", "payload");
  rmDir(path.join(ARTIFACT_ROOT, "check-a"));
  rmDir(path.join(ARTIFACT_ROOT, "check-b"));

  buildAnalyzerPayload("check-a", firstPayload);
  buildAnalyzerPayload("check-b", secondPayload);

  const first = payloadHashes(firstPayload);
  const second = payloadHashes(secondPayload);
  const reproducibilityFailures = compareHashMaps(first, second);
  if (reproducibilityFailures.length > 0) {
    printPayloadDiagnostics(
      "Analyzer payload is not reproducible across two clean builds.",
      reproducibilityFailures.map((dllName) => ({
        dllName,
        firstHash: first.get(dllName),
        secondHash: second.get(dllName)
      })),
      reproducibilityFailures.flatMap((dllName) => [
        path.join(firstPayload, dllName),
        path.join(secondPayload, dllName)
      ])
    );
    return 1;
  }

  const committed = new Map();
  for (const dllName of FIRST_PARTY_ANALYZER_DLLS) {
    const committedPath = path.join(EDITOR_ANALYZERS_DIR, dllName);
    if (!fs.existsSync(committedPath)) {
      committed.set(dllName, null);
    } else {
      committed.set(dllName, sha256(committedPath));
    }
  }

  const freshnessFailures = FIRST_PARTY_ANALYZER_DLLS.filter(
    (dllName) => first.get(dllName) !== committed.get(dllName)
  );
  if (freshnessFailures.length > 0) {
    printPayloadDiagnostics(
      "Committed Editor/Analyzers payload is stale.",
      freshnessFailures.map((dllName) => ({
        dllName,
        generatedHash: first.get(dllName),
        committedHash: committed.get(dllName)
      })),
      freshnessFailures.flatMap((dllName) => [
        path.join(firstPayload, dllName),
        path.join(EDITOR_ANALYZERS_DIR, dllName)
      ])
    );
    return 1;
  }

  console.log("Analyzer payload is reproducible and matches committed Editor/Analyzers DLLs.");
  return 0;
}

function writePayload() {
  const payloadDir = path.join(ARTIFACT_ROOT, "write", "payload");
  rmDir(path.join(ARTIFACT_ROOT, "write"));
  buildAnalyzerPayload("write", payloadDir);
  copyGeneratedPayload(payloadDir);
  for (const dllName of FIRST_PARTY_ANALYZER_DLLS) {
    console.log(`Updated ${repoRelative(path.join(EDITOR_ANALYZERS_DIR, dllName))}`);
  }
  return 0;
}

function main(argv = process.argv.slice(2)) {
  try {
    const { mode } = parseArgs(argv);
    if (mode === "check") {
      return checkPayload();
    }
    return writePayload();
  } catch (error) {
    console.error(error.message);
    return 1;
  }
}

if (require.main === module) {
  process.exit(main());
}
