/**
 * @fileoverview Tests for validate-npm-meta.js
 *
 * These tests validate the NPM meta file validation logic:
 * - Directory meta files are properly validated
 * - File meta files are properly validated
 * - Orphaned meta files are detected
 * - Missing meta files are detected
 */

"use strict";

const childProcess = require("child_process");
const { buildSpawnInvocation } = require("../lib/shell-command");
const { withPlatform } = require("../lib/jest-fixtures");

// Canonical npm pack invocation that production (getPackageFiles) hands to
// spawnPlatformCommandSync. Derive expectations from buildSpawnInvocation so
// the assertion tracks production on every platform.
const NPM_PACK_ARGS = ["pack", "--json", "--dry-run", "--ignore-scripts"];
// The two analyzer assemblies the package MUST ship. SetupCscRsp copies them
// into Assets/Plugins and activates them with Unity's RoslynAnalyzer label.
// Editor/Analyzers/ also ships the Roslyn runtime deps alongside them; the validator only enforces that these two
// REQUIRED files are present and does not forbid the dep DLLs from shipping.
const REQUIRED_ANALYZER_FILES = [
  "Editor/Analyzers/WallstopStudios.DxMessaging.SourceGenerators.dll",
  "Editor/Analyzers/WallstopStudios.DxMessaging.SourceGenerators.dll.meta",
  "Editor/Analyzers/WallstopStudios.DxMessaging.Analyzer.dll",
  "Editor/Analyzers/WallstopStudios.DxMessaging.Analyzer.dll.meta"
];

const {
  getPackageFiles,
  parseNpmPackJsonOutput,
  parseTarListingOutput,
  validateDevelopmentFilesExcluded,
  validateMetaFilesHaveTargets,
  validateFilesHaveMetaFiles,
  validateNoBuildArtifactsInTarball,
  validatePublishedFilesArePairedWithMetas,
  validateRequiredAnalyzerFilesInTarball,
  validateNpmMeta
} = require("../validate-npm-meta.js");

describe("validate-npm-meta", () => {
  afterEach(() => {
    jest.restoreAllMocks();
  });

  describe("parseTarListingOutput", () => {
    test("parses package paths with LF line endings", () => {
      const tarOutput = ["package/Runtime/File.cs", "package/Runtime/File.cs.meta", ""].join("\n");

      const files = parseTarListingOutput(tarOutput);
      expect(files).toEqual(["Runtime/File.cs", "Runtime/File.cs.meta"]);
    });

    test("parses package paths with lone CR line endings", () => {
      const tarOutput = ["package/Runtime/File.cs", "package/Runtime/File.cs.meta", ""].join("\r");

      const files = parseTarListingOutput(tarOutput);
      expect(files).toEqual(["Runtime/File.cs", "Runtime/File.cs.meta"]);
    });
  });

  describe("parseNpmPackJsonOutput", () => {
    test("parses npm pack --json files with object entries", () => {
      const packOutput = JSON.stringify([
        {
          files: [{ path: "Runtime/File.cs" }, { path: "Runtime/File.cs.meta" }]
        }
      ]);

      const files = parseNpmPackJsonOutput(packOutput);
      expect(files).toEqual(["Runtime/File.cs", "Runtime/File.cs.meta"]);
    });

    test("parses npm pack --json files with string entries", () => {
      const packOutput = JSON.stringify([
        {
          files: ["Runtime/File.cs", "Runtime/File.cs.meta"]
        }
      ]);

      const files = parseNpmPackJsonOutput(packOutput);
      expect(files).toEqual(["Runtime/File.cs", "Runtime/File.cs.meta"]);
    });

    test("parses npm pack JSON output with CRLF and surrounding whitespace", () => {
      const packOutput =
        "\r\n" +
        JSON.stringify([
          {
            files: [{ path: "Runtime/File.cs" }, { path: "Runtime/File.cs.meta" }]
          }
        ]) +
        "\r\n";

      const files = parseNpmPackJsonOutput(packOutput);
      expect(files).toEqual(["Runtime/File.cs", "Runtime/File.cs.meta"]);
    });

    test("throws when npm pack output is not valid JSON", () => {
      expect(() => parseNpmPackJsonOutput("not-json")).toThrow(
        "Unable to parse npm pack --json output"
      );
    });

    test("throws when npm pack output does not include files", () => {
      const packOutput = JSON.stringify([
        {
          name: "com.wallstop-studios.dxmessaging"
        }
      ]);

      expect(() => parseNpmPackJsonOutput(packOutput)).toThrow("did not include a files list");
    });
  });

  describe("getPackageFiles", () => {
    test("uses cross-platform npm pack invocation and returns file list", () => {
      const spawnSyncSpy = jest.spyOn(childProcess, "spawnSync").mockReturnValue({
        status: 0,
        stdout: JSON.stringify([
          {
            files: [{ path: "Runtime/File.cs" }, { path: "Runtime/File.cs.meta" }]
          }
        ]),
        stderr: ""
      });

      const files = getPackageFiles();

      // Expectation tracks production on this host: buildSpawnInvocation uses
      // the same code path spawnPlatformCommandSync does. Only command/args are
      // host-divergent; the options shape is asserted separately below via
      // expect.objectContaining (cwd is resolved by production to a real path,
      // so it is matched with expect.any(String) rather than an exact value).
      const inv = buildSpawnInvocation("npm", NPM_PACK_ARGS);

      expect(files).toEqual(["Runtime/File.cs", "Runtime/File.cs.meta"]);
      expect(spawnSyncSpy).toHaveBeenCalledWith(
        inv.command,
        inv.args,
        expect.objectContaining({
          cwd: expect.any(String),
          encoding: "utf8",
          stdio: ["ignore", "pipe", "pipe"]
        })
      );
    });

    test("uses the cmd.exe wrapper for npm pack on win32 (forced platform)", () => {
      // Exercise the Windows branch even on a Linux/macOS host so the divergence
      // that broke the pre-push hook on Windows is caught everywhere.
      withPlatform("win32", () => {
        const spawnSyncSpy = jest.spyOn(childProcess, "spawnSync").mockReturnValue({
          status: 0,
          stdout: JSON.stringify([{ files: [{ path: "Runtime/File.cs" }] }]),
          stderr: ""
        });

        getPackageFiles();

        const inv = buildSpawnInvocation("npm", NPM_PACK_ARGS, {}, "win32");
        expect(inv.command).toBe(buildSpawnInvocation("npm", [], {}, "win32").command);
        expect(inv.args).toEqual(["/d", "/s", "/c", "npm.cmd", ...NPM_PACK_ARGS]);
        expect(spawnSyncSpy).toHaveBeenCalledWith(
          inv.command,
          inv.args,
          expect.objectContaining({
            encoding: "utf8",
            stdio: ["ignore", "pipe", "pipe"],
            shell: false,
            windowsHide: true
          })
        );
      });
    });

    test("uses plain npm passthrough on linux (forced platform)", () => {
      withPlatform("linux", () => {
        const spawnSyncSpy = jest.spyOn(childProcess, "spawnSync").mockReturnValue({
          status: 0,
          stdout: JSON.stringify([{ files: [{ path: "Runtime/File.cs" }] }]),
          stderr: ""
        });

        getPackageFiles();

        expect(spawnSyncSpy).toHaveBeenCalledWith(
          "npm",
          NPM_PACK_ARGS,
          expect.objectContaining({ encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] })
        );
      });
    });

    test("throws when npm pack exits with non-zero status", () => {
      jest.spyOn(childProcess, "spawnSync").mockReturnValue({
        status: 1,
        stdout: "",
        stderr: "simulated failure"
      });

      expect(() => getPackageFiles()).toThrow("npm pack --json --dry-run failed with exit code 1");
    });

    test("throws when npm process spawn fails", () => {
      jest.spyOn(childProcess, "spawnSync").mockReturnValue({
        error: new Error("spawn failed"),
        status: null,
        stdout: "",
        stderr: ""
      });

      expect(() => getPackageFiles()).toThrow("spawn failed");
    });
  });

  describe("validateMetaFilesHaveTargets", () => {
    test("should pass when all .meta files have corresponding files", () => {
      const files = [
        "Runtime/Core/MessageHandler.cs",
        "Runtime/Core/MessageHandler.cs.meta",
        "Editor/Settings.meta",
        "Editor/Settings/DxMessagingSettings.cs",
        "Editor/Settings/DxMessagingSettings.cs.meta"
      ];

      const result = validateMetaFilesHaveTargets(files);

      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    test("should pass when directory .meta files have files in that directory", () => {
      const files = [
        "Runtime.meta",
        "Runtime/Core.meta",
        "Runtime/Core/MessageHandler.cs",
        "Runtime/Core/MessageHandler.cs.meta"
      ];

      const result = validateMetaFilesHaveTargets(files);

      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    test("should fail when .meta file has no corresponding file", () => {
      const files = [
        "Runtime/Core/MessageHandler.cs.meta",
        "Runtime/Core/OtherFile.cs",
        "Runtime/Core/OtherFile.cs.meta"
      ];

      const result = validateMetaFilesHaveTargets(files);

      expect(result.valid).toBe(false);
      expect(result.errors).toHaveLength(1);
      expect(result.errors[0].type).toBe("orphaned-meta");
      expect(result.errors[0].file).toBe("Runtime/Core/MessageHandler.cs.meta");
    });

    test("should fail when directory .meta has no files in directory", () => {
      const files = [
        "Runtime.meta",
        "Runtime/Core.meta",
        "Editor/Settings.meta",
        "Editor/OtherDir.meta",
        "Editor/OtherDir/File.cs",
        "Editor/OtherDir/File.cs.meta"
      ];

      const result = validateMetaFilesHaveTargets(files);

      expect(result.valid).toBe(false);
      expect(result.errors).toHaveLength(2);
      // Runtime.meta and Runtime/Core.meta don't have files, but Editor/Settings.meta and Editor/OtherDir.meta do
      expect(result.errors.map((e) => e.file)).toContain("Runtime/Core.meta");
      expect(result.errors.map((e) => e.file)).toContain("Editor/Settings.meta");
    });

    test("should handle nested directory structures", () => {
      const files = [
        "Runtime.meta",
        "Runtime/Core.meta",
        "Runtime/Core/Messages.meta",
        "Runtime/Core/Messages/StringMessage.cs",
        "Runtime/Core/Messages/StringMessage.cs.meta"
      ];

      const result = validateMetaFilesHaveTargets(files);

      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });
  });

  describe("validateFilesHaveMetaFiles", () => {
    test("should pass when all files have .meta files", () => {
      const files = [
        "Runtime.meta",
        "Runtime/Core.meta",
        "Runtime/Core/MessageHandler.cs",
        "Runtime/Core/MessageHandler.cs.meta",
        "Editor.meta",
        "Editor/Settings.meta",
        "Editor/Settings/DxMessagingSettings.cs",
        "Editor/Settings/DxMessagingSettings.cs.meta"
      ];

      const result = validateFilesHaveMetaFiles(files);

      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    test("should fail when files are missing .meta files", () => {
      const files = [
        "Runtime.meta",
        "Runtime/Core.meta",
        "Runtime/Core/MessageHandler.cs",
        "Runtime/Core/OtherFile.cs",
        "Runtime/Core/OtherFile.cs.meta"
      ];

      const result = validateFilesHaveMetaFiles(files);

      expect(result.valid).toBe(false);
      expect(result.errors).toHaveLength(1);
      expect(result.errors[0].type).toBe("missing-meta");
      expect(result.errors[0].file).toBe("Runtime/Core/MessageHandler.cs");
    });

    test("should require directory .meta files for included Unity assets", () => {
      const files = [
        "Runtime.meta",
        "Runtime/Core/MessageHandler.cs",
        "Runtime/Core/MessageHandler.cs.meta"
      ];

      const result = validateFilesHaveMetaFiles(files);

      expect(result.valid).toBe(false);
      expect(result.errors).toEqual([
        {
          type: "missing-meta",
          file: "Runtime/Core",
          message: "Directory 'Runtime/Core' is missing its .meta file in the package"
        }
      ]);
    });

    test("should require nested directory .meta files for included Unity assets", () => {
      const files = [
        "Runtime.meta",
        "Runtime/Core.meta",
        "Runtime/Core/Messages/StringMessage.cs",
        "Runtime/Core/Messages/StringMessage.cs.meta"
      ];

      const result = validateFilesHaveMetaFiles(files);

      expect(result.valid).toBe(false);
      expect(result.errors).toEqual([
        {
          type: "missing-meta",
          file: "Runtime/Core/Messages",
          message: "Directory 'Runtime/Core/Messages' is missing its .meta file in the package"
        }
      ]);
    });

    test("should not require directory .meta files for non-Unity and development-only paths", () => {
      const files = [
        "package.json",
        "package-lock.json",
        ".github/workflows/build.yml",
        ".git/HEAD",
        "node_modules/some-package/index.js",
        ".unity-test-project/Packages/manifest.json",
        "scripts/validate-npm-meta.js",
        "Samples~/Mini Combat.meta",
        "Samples~/Mini Combat/Boot.cs",
        "Samples~/Mini Combat/Boot.cs.meta"
      ];

      const result = validateFilesHaveMetaFiles(files);

      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    test("should allow package.json and package-lock.json without .meta", () => {
      const files = [
        "package.json",
        "package-lock.json",
        "Runtime.meta",
        "Runtime/Core.meta",
        "Runtime/Core/MessageHandler.cs",
        "Runtime/Core/MessageHandler.cs.meta"
      ];

      const result = validateFilesHaveMetaFiles(files);

      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    test("should allow .github, .git, and node_modules paths without .meta", () => {
      const files = [
        ".github/workflows/build.yml",
        ".git/HEAD",
        "node_modules/some-package/index.js",
        "Runtime.meta",
        "Runtime/Core.meta",
        "Runtime/Core/MessageHandler.cs",
        "Runtime/Core/MessageHandler.cs.meta"
      ];

      const result = validateFilesHaveMetaFiles(files);

      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    test("should detect multiple missing .meta files", () => {
      const files = [
        "Runtime.meta",
        "Runtime/Core.meta",
        "Runtime/Core/File1.cs",
        "Runtime/Core/File2.cs",
        "Runtime/Core/File3.cs",
        "Runtime/Core/File3.cs.meta"
      ];

      const result = validateFilesHaveMetaFiles(files);

      expect(result.valid).toBe(false);
      expect(result.errors).toHaveLength(2);
      expect(result.errors.map((e) => e.file)).toContain("Runtime/Core/File1.cs");
      expect(result.errors.map((e) => e.file)).toContain("Runtime/Core/File2.cs");
    });

    test("should handle various file extensions", () => {
      const files = [
        "README.md",
        "README.md.meta",
        "LICENSE.md",
        "LICENSE.md.meta",
        "Runtime.meta",
        "Runtime/WallstopStudios.DxMessaging.asmdef",
        "Runtime/WallstopStudios.DxMessaging.asmdef.meta"
      ];

      const result = validateFilesHaveMetaFiles(files);

      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });
  });

  describe("validateDevelopmentFilesExcluded", () => {
    test("should pass for package runtime contents", () => {
      const files = [
        "package.json",
        "package.json.meta",
        "Runtime/Core/MessageHandler.cs",
        "Runtime/Core/MessageHandler.cs.meta",
        "Editor/Settings/DxMessagingSettings.cs",
        "Editor/Settings/DxMessagingSettings.cs.meta"
      ];

      const result = validateDevelopmentFilesExcluded(files);

      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    test("should reject development-only repository roots", () => {
      const files = [
        ".ambiguous-organization-build-lock/README.md",
        ".config/tool.json",
        ".unity-test-project/Packages/manifest.json",
        ".unity-test-project.meta",
        ".unity-test-project/ProjectSettings/ProjectVersion.txt",
        ".llm/context.md",
        ".github/workflows/unity-tests.yml",
        ".husky/pre-commit",
        ".devcontainer/devcontainer.json",
        ".venv/bin/python",
        "Tests/Runtime/SomeTest.cs",
        "Tests.meta",
        "SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.Tests/GeneratorTests.cs",
        "SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.Tests.meta",
        "scripts/validate-npm-meta.js",
        "scripts.meta",
        "node_modules/some-package/index.js",
        "node_modules.meta",
        "coverage/lcov.info",
        "coverage.meta",
        "site/index.html",
        "site.meta",
        "progress/notes.md",
        "progress.meta",
        "package-lock.json",
        "package-lock.json.meta",
        "Runtime/Core/MessageHandler.cs",
        "Runtime/Core/MessageHandler.cs.meta"
      ];

      const result = validateDevelopmentFilesExcluded(files);

      expect(result.valid).toBe(false);
      expect(result.errors.map((error) => error.file)).toEqual([
        ".ambiguous-organization-build-lock/README.md",
        ".config/tool.json",
        ".unity-test-project/Packages/manifest.json",
        ".unity-test-project.meta",
        ".unity-test-project/ProjectSettings/ProjectVersion.txt",
        ".llm/context.md",
        ".github/workflows/unity-tests.yml",
        ".husky/pre-commit",
        ".devcontainer/devcontainer.json",
        ".venv/bin/python",
        "Tests/Runtime/SomeTest.cs",
        "Tests.meta",
        "SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.Tests/GeneratorTests.cs",
        "SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.Tests.meta",
        "scripts/validate-npm-meta.js",
        "scripts.meta",
        "node_modules/some-package/index.js",
        "node_modules.meta",
        "coverage/lcov.info",
        "coverage.meta",
        "site/index.html",
        "site.meta",
        "progress/notes.md",
        "progress.meta",
        "package-lock.json",
        "package-lock.json.meta"
      ]);
      expect(new Set(result.errors.map((error) => error.type))).toEqual(
        new Set(["development-file-in-package"])
      );
    });

    test("should reject development-only root files and tool configuration", () => {
      const files = [
        "AGENTS.md",
        "AGENTS.md.meta",
        "CLAUDE.md",
        "CLAUDE.md.meta",
        "CONTRIBUTING.md",
        "CONTRIBUTING.md.meta",
        "GH-PAGES-PLAN.md",
        "GH-PAGES-PLAN.md.meta",
        "PLAN.md",
        "PLAN.md.meta",
        ".gitattributes",
        ".editorconfig",
        ".prettierrc",
        ".prettierrc.json",
        ".prettierignore",
        ".markdownlint.json",
        ".markdownlint.jsonc",
        ".markdownlint-cli2.jsonc",
        ".markdownlintignore",
        ".yamllint.yaml",
        ".cspell.json",
        ".lychee.toml",
        ".csharpierignore",
        ".csharpierrc.json",
        ".cursorrules",
        ".pre-commit-config.yaml",
        "Runtime/Core/MessageHandler.cs",
        "Runtime/Core/MessageHandler.cs.meta"
      ];

      const result = validateDevelopmentFilesExcluded(files);

      expect(result.valid).toBe(false);
      expect(result.errors.map((error) => error.file)).toEqual(files.slice(0, -2));
    });

    test("should reject documentation build tooling entries", () => {
      const files = [
        "requirements-docs.txt",
        "requirements-docs.txt.meta",
        "mkdocs.yml",
        "mkdocs.yml.meta",
        "docs/hooks.py",
        "docs/hooks.py.meta",
        "docs/__pycache__/hooks.pyc",
        "__pycache__.meta",
        "Runtime/Core/MessageHandler.cs",
        "Runtime/Core/MessageHandler.cs.meta"
      ];

      const result = validateDevelopmentFilesExcluded(files);

      expect(result.valid).toBe(false);
      expect(result.errors.map((error) => error.file)).toEqual(files.slice(0, -2));
    });
  });

  describe("integration scenarios", () => {
    test("should validate a typical Unity package structure", () => {
      const files = [
        "package.json",
        "package.json.meta",
        "README.md",
        "README.md.meta",
        "Runtime.meta",
        "Runtime/Core.meta",
        "Runtime/Core/MessageHandler.cs",
        "Runtime/Core/MessageHandler.cs.meta",
        "Editor.meta",
        "Editor/Settings.meta",
        "Editor/Settings/DxMessagingSettings.cs",
        "Editor/Settings/DxMessagingSettings.cs.meta"
      ];

      const metaResult = validateMetaFilesHaveTargets(files);
      const fileResult = validateFilesHaveMetaFiles(files);

      expect(metaResult.valid).toBe(true);
      expect(fileResult.valid).toBe(true);
    });

    test("should detect both orphaned and missing meta files", () => {
      const files = [
        "Runtime.meta",
        "Runtime/Core.meta",
        "Runtime/Core/MessageHandler.cs",
        "Runtime/Core/MissingFile.cs.meta",
        "Editor.meta",
        "Editor/Settings.meta",
        "Editor/Settings/DxMessagingSettings.cs"
      ];

      const metaResult = validateMetaFilesHaveTargets(files);
      const fileResult = validateFilesHaveMetaFiles(files);

      expect(metaResult.valid).toBe(false);
      expect(metaResult.errors).toHaveLength(1);
      expect(metaResult.errors[0].file).toBe("Runtime/Core/MissingFile.cs.meta");

      expect(fileResult.valid).toBe(false);
      expect(fileResult.errors).toHaveLength(2);
      expect(fileResult.errors.map((e) => e.file)).toContain("Runtime/Core/MessageHandler.cs");
      expect(fileResult.errors.map((e) => e.file)).toContain(
        "Editor/Settings/DxMessagingSettings.cs"
      );
    });

    test("validateNpmMeta should pass against the real npm pack --dry-run output on the current branch", () => {
      // Integration check: shells out to the real npm pack flow via the script's own
      // getPackageFiles() and asserts the current branch is clean. This is the live
      // guardrail that issue #204 (https://github.com/Ambiguous-Interactive/DxMessaging/issues/204)
      // cannot regress without the test failing.
      jest.spyOn(console, "log").mockImplementation(() => {});

      const result = validateNpmMeta();

      expect(result.valid).toBe(true);
      expect(result.errors).toEqual([]);
    });

    test("validateNpmMeta should fail when npm pack includes development-only files", () => {
      jest.spyOn(console, "log").mockImplementation(() => {});
      jest.spyOn(childProcess, "spawnSync").mockReturnValue({
        status: 0,
        stdout: JSON.stringify([
          {
            files: [
              { path: "package.json" },
              { path: "package.json.meta" },
              { path: "Runtime.meta" },
              { path: "Runtime/Core.meta" },
              { path: "Runtime/Core/MessageHandler.cs" },
              { path: "Runtime/Core/MessageHandler.cs.meta" },
              { path: "Editor.meta" },
              { path: "Editor/Analyzers.meta" },
              { path: ".unity-test-project.meta" },
              { path: ".unity-test-project/Packages/manifest.json" },
              { path: ".unity-test-project/Packages/manifest.json.meta" },
              ...REQUIRED_ANALYZER_FILES.map((file) => ({ path: file }))
            ]
          }
        ]),
        stderr: ""
      });

      const result = validateNpmMeta();

      expect(result.valid).toBe(false);
      expect(result.errors).toEqual([
        {
          type: "development-file-in-package",
          file: ".unity-test-project.meta",
          message:
            "Development-only file '.unity-test-project.meta' must not be included in the npm package"
        },
        {
          type: "development-file-in-package",
          file: ".unity-test-project/Packages/manifest.json",
          message:
            "Development-only file '.unity-test-project/Packages/manifest.json' must not be included in the npm package"
        },
        {
          type: "development-file-in-package",
          file: ".unity-test-project/Packages/manifest.json.meta",
          message:
            "Development-only file '.unity-test-project/Packages/manifest.json.meta' must not be included in the npm package"
        }
      ]);
    });
  });

  describe("required analyzer package files", () => {
    test("accepts the required analyzer DLLs and meta files from the tarball list", () => {
      const result = validateRequiredAnalyzerFilesInTarball(REQUIRED_ANALYZER_FILES);

      expect(result).toEqual({ valid: true, errors: [] });
    });

    test("flags missing analyzer DLLs from the actual tarball file list", () => {
      const files = REQUIRED_ANALYZER_FILES.filter(
        (file) => file !== "Editor/Analyzers/WallstopStudios.DxMessaging.Analyzer.dll"
      );

      const result = validateRequiredAnalyzerFilesInTarball(files);

      expect(result.valid).toBe(false);
      expect(result.errors).toEqual([
        {
          type: "missing-required-analyzer-file",
          file: "Editor/Analyzers/WallstopStudios.DxMessaging.Analyzer.dll",
          message: expect.stringContaining("Tarball is missing required analyzer file")
        }
      ]);
    });
  });

  // -------------------------------------------------------------------------
  // Issue #204 regression coverage
  //
  // GitHub issue #204 (https://github.com/Ambiguous-Interactive/DxMessaging/issues/204)
  // reported `GuidDB::CreateMetaFileMappings` warnings on every Unity asset-database
  // refresh after installing the npm package. Pre-2.1.8 tarballs shipped
  // SourceGenerator `bin/Debug/netstandard2.0/...` build outputs and `obj/...` files
  // whose paths had no `.meta` partner. 2.1.8 patched `.npmignore`. These tests are
  // the permanent guardrail that #204 cannot regress -- both the bin/obj artifacts
  // (validateNoBuildArtifactsInTarball) and the missing-meta-pairings symptom
  // (validatePublishedFilesArePairedWithMetas) are caught.
  // -------------------------------------------------------------------------
  describe("issue #204 regression coverage", () => {
    describe("validateNoBuildArtifactsInTarball", () => {
      test("rejects Runtime/bin/Foo.dll", () => {
        const result = validateNoBuildArtifactsInTarball(["Runtime/bin/Foo.dll"]);

        expect(result.valid).toBe(false);
        expect(result.errors).toHaveLength(1);
        expect(result.errors[0].type).toBe("build-artifact-in-tarball");
        expect(result.errors[0].file).toBe("Runtime/bin/Foo.dll");
        expect(result.errors[0].message).toContain("Issue #204");
      });

      test("rejects SourceGenerators obj/Debug build outputs", () => {
        const offending =
          "SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators/obj/Debug/Foo.cs";
        const result = validateNoBuildArtifactsInTarball([offending]);

        expect(result.valid).toBe(false);
        expect(result.errors).toHaveLength(1);
        expect(result.errors[0].type).toBe("build-artifact-in-tarball");
        expect(result.errors[0].file).toBe(offending);
        expect(result.errors[0].message).toContain("Issue #204");
      });

      test("rejects Editor/Foo.pdb", () => {
        const result = validateNoBuildArtifactsInTarball(["Editor/Foo.pdb"]);

        expect(result.valid).toBe(false);
        expect(result.errors).toHaveLength(1);
        expect(result.errors[0].type).toBe("build-artifact-in-tarball");
        expect(result.errors[0].file).toBe("Editor/Foo.pdb");
        expect(result.errors[0].message).toContain("Issue #204");
      });

      test("rejects Runtime/Foo.tmp", () => {
        const result = validateNoBuildArtifactsInTarball(["Runtime/Foo.tmp"]);

        expect(result.valid).toBe(false);
        expect(result.errors).toHaveLength(1);
        expect(result.errors[0].type).toBe("build-artifact-in-tarball");
        expect(result.errors[0].file).toBe("Runtime/Foo.tmp");
      });

      test("rejects Editor/Foo.csproj.user", () => {
        const result = validateNoBuildArtifactsInTarball(["Editor/Foo.csproj.user"]);

        expect(result.valid).toBe(false);
        expect(result.errors).toHaveLength(1);
        expect(result.errors[0].type).toBe("build-artifact-in-tarball");
        expect(result.errors[0].file).toBe("Editor/Foo.csproj.user");
      });

      test("rejects Samples~/.vs/foo.txt", () => {
        const result = validateNoBuildArtifactsInTarball(["Samples~/.vs/foo.txt"]);

        expect(result.valid).toBe(false);
        expect(result.errors).toHaveLength(1);
        expect(result.errors[0].type).toBe("build-artifact-in-tarball");
        expect(result.errors[0].file).toBe("Samples~/.vs/foo.txt");
      });

      test("rejects Editor/Foo.suo", () => {
        const result = validateNoBuildArtifactsInTarball(["Editor/Foo.suo"]);

        expect(result.valid).toBe(false);
        expect(result.errors).toHaveLength(1);
        expect(result.errors[0].type).toBe("build-artifact-in-tarball");
        expect(result.errors[0].file).toBe("Editor/Foo.suo");
      });

      test("rejects com.wallstop-studios.dxmessaging.sln.DotSettings.user", () => {
        const result = validateNoBuildArtifactsInTarball([
          "com.wallstop-studios.dxmessaging.sln.DotSettings.user"
        ]);

        expect(result.valid).toBe(false);
        expect(result.errors).toHaveLength(1);
        expect(result.errors[0].type).toBe("build-artifact-in-tarball");
        expect(result.errors[0].file).toBe("com.wallstop-studios.dxmessaging.sln.DotSettings.user");
      });

      test("accepts a clean Runtime/Foo.cs tarball with no errors", () => {
        const files = ["Runtime/Foo.cs", "Runtime/Foo.cs.meta", "Runtime.meta"];

        const result = validateNoBuildArtifactsInTarball(files);

        expect(result.valid).toBe(true);
        expect(result.errors).toHaveLength(0);
      });
    });

    describe("validatePublishedFilesArePairedWithMetas", () => {
      test("reproduces the exact #204 leak: a SourceGenerator bin/Debug AssemblyInfo.cs without its .meta", () => {
        const offending =
          "SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators/bin/Debug/netstandard2.0/AssemblyInfo.cs";
        const files = [
          "SourceGenerators.meta",
          "SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.meta",
          offending
        ];

        const metaPairingResult = validatePublishedFilesArePairedWithMetas(files);

        // The validator considers SourceGenerators/.../*.cs Unity-relevant, so the missing
        // .meta partner is reported. This is the exact symptom that issue #204 surfaced.
        expect(metaPairingResult.valid).toBe(false);
        const missingMetaErrors = metaPairingResult.errors.filter(
          (e) => e.type === "missing-meta-in-tarball"
        );
        expect(missingMetaErrors.length).toBeGreaterThan(0);
        const errorForFile = missingMetaErrors.find((e) => e.file === offending + ".meta");
        expect(errorForFile).toBeDefined();
        expect(errorForFile.message).toContain(offending + ".meta");
        expect(errorForFile.message).toContain("issue #204");

        // Defense in depth: the bin/ artifact validator must also fire on the same path.
        const buildArtifactResult = validateNoBuildArtifactsInTarball(files);
        expect(buildArtifactResult.valid).toBe(false);
        expect(buildArtifactResult.errors[0].type).toBe("build-artifact-in-tarball");
        expect(buildArtifactResult.errors[0].file).toBe(offending);
      });

      test("rejects Runtime/Core/Foo.cs and its .meta when the directory meta Runtime/Core.meta is missing", () => {
        const files = [
          "Runtime.meta",
          "Runtime/Core/Foo.cs",
          "Runtime/Core/Foo.cs.meta"
          // Missing Runtime/Core.meta
        ];

        const result = validatePublishedFilesArePairedWithMetas(files);

        expect(result.valid).toBe(false);
        const missingDirectoryMeta = result.errors.find((e) => e.file === "Runtime/Core.meta");
        expect(missingDirectoryMeta).toBeDefined();
        expect(missingDirectoryMeta.type).toBe("missing-meta-in-tarball");
        expect(missingDirectoryMeta.message).toContain("Runtime/Core.meta");
        expect(missingDirectoryMeta.message).toContain("issue #204");
      });

      test("accepts a canonical clean Runtime/Core/Foo.cs shape with all .meta partners", () => {
        const files = [
          "Runtime.meta",
          "Runtime/Core.meta",
          "Runtime/Core/Foo.cs",
          "Runtime/Core/Foo.cs.meta"
        ];

        const result = validatePublishedFilesArePairedWithMetas(files);

        expect(result.valid).toBe(true);
        expect(result.errors).toHaveLength(0);
      });

      test("accepts a Samples~/Demo/Foo.cs shape with all .meta neighbours", () => {
        // Samples~/ paths ARE Unity-relevant; the Samples~ root itself is hidden by UPM
        // but its subdirectories still need .meta partners.
        const files = ["Samples~/Demo.meta", "Samples~/Demo/Foo.cs", "Samples~/Demo/Foo.cs.meta"];

        const result = validatePublishedFilesArePairedWithMetas(files);

        expect(result.valid).toBe(true);
        expect(result.errors).toHaveLength(0);
      });

      test("rejects Runtime/Foo.cs without its Runtime/Foo.cs.meta partner", () => {
        const files = [
          "Runtime.meta",
          "Runtime/Foo.cs"
          // Missing Runtime/Foo.cs.meta
        ];

        const result = validatePublishedFilesArePairedWithMetas(files);

        expect(result.valid).toBe(false);
        const missing = result.errors.find((e) => e.file === "Runtime/Foo.cs.meta");
        expect(missing).toBeDefined();
        expect(missing.type).toBe("missing-meta-in-tarball");
        expect(missing.message).toContain("Runtime/Foo.cs.meta");
        expect(missing.message).toContain("issue #204");
      });

      test("rejects ./Runtime/Foo.cs (./-prefixed paths) so leading-dot prefixes do not mask leaks", () => {
        // npm pack and tar listings occasionally emit `./Runtime/Foo.cs` style entries.
        // Without normalization, the `startsWith("Runtime/")` check would silently skip
        // these and miss the regression.
        const files = ["./Runtime/Foo.cs", "./Runtime.meta"];

        const result = validatePublishedFilesArePairedWithMetas(files);

        expect(result.valid).toBe(false);
        const missing = result.errors.find((e) => e.file === "Runtime/Foo.cs.meta");
        expect(missing).toBeDefined();
        expect(missing.type).toBe("missing-meta-in-tarball");
      });

      test("flags SourceGenerators/Directory.Build.props missing its .meta partner", () => {
        // package.json explicitly ships SourceGenerators/Directory.Build.props.meta as a
        // Unity-tracked asset. The validator must therefore require the .props file's
        // .meta partner; otherwise a future drop of the .meta line in package.json would
        // sail past the validator unnoticed.
        const files = ["SourceGenerators.meta", "SourceGenerators/Directory.Build.props"];

        const result = validatePublishedFilesArePairedWithMetas(files);

        expect(result.valid).toBe(false);
        const missing = result.errors.find(
          (e) => e.file === "SourceGenerators/Directory.Build.props.meta"
        );
        expect(missing).toBeDefined();
        expect(missing.type).toBe("missing-meta-in-tarball");
        expect(missing.message).toContain("SourceGenerators/Directory.Build.props.meta");
        expect(missing.message).toContain("issue #204");
      });

      test("emits exactly one missing-meta error when the directory walk catches the root meta", () => {
        // Mi1 regression guard: the explicit rootShippedDirectoryMetas loop was removed
        // because the per-directory walk already records every ancestor of every shipped
        // file. Asserting exactly one error here proves there is no double-report.
        const files = ["Runtime/Core/Foo.cs", "Runtime/Core/Foo.cs.meta"];

        const result = validatePublishedFilesArePairedWithMetas(files);

        expect(result.valid).toBe(false);
        const runtimeCoreMetaErrors = result.errors.filter((e) => e.file === "Runtime/Core.meta");
        const runtimeMetaErrors = result.errors.filter((e) => e.file === "Runtime.meta");
        expect(runtimeCoreMetaErrors).toHaveLength(1);
        expect(runtimeMetaErrors).toHaveLength(1);
      });
    });

    describe("validateNoBuildArtifactsInTarball edge cases", () => {
      test("rejects JetBrains .idea/ state nested under Runtime/", () => {
        const result = validateNoBuildArtifactsInTarball(["Runtime/.idea/workspace.xml"]);

        expect(result.valid).toBe(false);
        expect(result.errors).toHaveLength(1);
        expect(result.errors[0].type).toBe("build-artifact-in-tarball");
        expect(result.errors[0].file).toBe("Runtime/.idea/workspace.xml");
        expect(result.errors[0].message).toContain("JetBrains IDE state");
      });

      test("rejects a generic .user file via the bare \\.user$ pattern", () => {
        // The bare `\.user$` pattern is the catch-all for IDE-flavoured per-user files
        // that do not match a more specific pattern (.csproj.user / .DotSettings.user).
        const result = validateNoBuildArtifactsInTarball(["Runtime/Foo.user"]);

        expect(result.valid).toBe(false);
        expect(result.errors).toHaveLength(1);
        expect(result.errors[0].type).toBe("build-artifact-in-tarball");
        expect(result.errors[0].file).toBe("Runtime/Foo.user");
        expect(result.errors[0].message).toContain("per-user IDE settings file");
      });

      test("rejects a deeply nested bin/ path", () => {
        const result = validateNoBuildArtifactsInTarball(["Editor/CodeGen/bin/cache.json"]);

        expect(result.valid).toBe(false);
        expect(result.errors).toHaveLength(1);
        expect(result.errors[0].type).toBe("build-artifact-in-tarball");
        expect(result.errors[0].file).toBe("Editor/CodeGen/bin/cache.json");
      });

      test("rejects a root-level bin/ path via the ^bin/ branch of the alternation", () => {
        const result = validateNoBuildArtifactsInTarball(["bin/Foo.dll"]);

        expect(result.valid).toBe(false);
        expect(result.errors).toHaveLength(1);
        expect(result.errors[0].type).toBe("build-artifact-in-tarball");
        expect(result.errors[0].file).toBe("bin/Foo.dll");
      });

      test("does not produce false positives on names that merely contain the substrings", () => {
        // The regexes are word/path-anchored on purpose. A filename containing `bin`,
        // `obj`, or `user` as part of a longer identifier must NOT match.
        const files = [
          "Runtime/AwesomeBin.cs",
          "Runtime/objective-c-bridge.cs",
          "Runtime/foo.user.cs"
        ];

        const result = validateNoBuildArtifactsInTarball(files);

        expect(result.valid).toBe(true);
        expect(result.errors).toHaveLength(0);
      });

      test("scenario D: bin/ artifact with its .meta still flags the build artifact but not the missing meta", () => {
        // Defense-in-depth: even if some future package.json change accidentally ships
        // a .meta partner alongside a bin/ artifact, validateNoBuildArtifactsInTarball
        // must still reject the artifact while validatePublishedFilesArePairedWithMetas
        // does not synthesize a meta-pairing complaint (because the meta is present).
        const files = [
          "Runtime.meta",
          "Runtime/bin.meta",
          "Runtime/bin/Foo.dll",
          "Runtime/bin/Foo.dll.meta"
        ];

        const buildArtifactResult = validateNoBuildArtifactsInTarball(files);
        expect(buildArtifactResult.valid).toBe(false);
        expect(buildArtifactResult.errors).toHaveLength(2);
        expect(buildArtifactResult.errors.map((e) => e.file).sort()).toEqual([
          "Runtime/bin/Foo.dll",
          "Runtime/bin/Foo.dll.meta"
        ]);

        const metaPairingResult = validatePublishedFilesArePairedWithMetas(files);
        // Runtime/bin/Foo.dll IS Unity-relevant (under Runtime/) and HAS its meta sibling,
        // so the pairing validator stays silent on this scenario.
        const missingForDll = metaPairingResult.errors.find(
          (e) => e.file === "Runtime/bin/Foo.dll.meta"
        );
        expect(missingForDll).toBeUndefined();
      });
    });
  });
});
