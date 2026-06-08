/**
 * @fileoverview Tests for scripts/hooks/post-edit-validate-guard.js -- the
 * generalized, advisory PostToolUse guard that runs the fast, read-only
 * validators relevant to a just-edited file (closing the edit-time gap that let
 * an npm-packaging regression slip through to the native git hook).
 *
 * The guard's child-process spawns are injectable, so these tests never spawn a
 * real validator and are OS-independent.
 */

"use strict";

// cspell:ignore typoo wurd zzzxqword

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");

const {
  ACTIVE_ENV,
  CSPELL_EXTENSIONS,
  isPackagingRelevant,
  isDocQualityRelevant,
  isDocsOutOfTreeLinkRelevant,
  isChangelogCoverageRelevant,
  isSpellcheckRelevant,
  isXmlBuildContractRelevant,
  formatCspellIssues,
  cspellIssueLocation,
  hasCspellInfrastructureError,
  runCspellApiValidator,
  runSpellcheckValidator,
  buildDispatchTable,
  resolveRepoRoot,
  toRepoRelativePosix,
  runValidator,
  evaluate,
  buildMessage,
  run
} = require("../hooks/post-edit-validate-guard.js");
const {
  getPackageCspellAllExtensions,
  getCspellHookExtensions
} = require("../lib/cspell-extension-parity");
const { normalizeNodeColorEnv } = require("../lib/shell-command");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const PRE_COMMIT_CONFIG_PATH = path.join(REPO_ROOT, ".pre-commit-config.yaml");
const PACKAGE_JSON_PATH = path.join(REPO_ROOT, "package.json");

/** Build a fake spawnSync-shaped result. */
function spawnResult({ status = 0, stdout = "", stderr = "", error = null }) {
  return { status, stdout, stderr, error };
}

/** A spawn impl that returns a fixed result and records its calls. */
function fakeSpawn(result) {
  const calls = [];
  const impl = (command, args, options) => {
    calls.push({ command, args, options });
    return typeof result === "function" ? result(command, args, options) : result;
  };
  impl.calls = calls;
  return impl;
}

describe("path classification", () => {
  test.each([
    ["package.json", true],
    [".npmignore", true],
    ["Editor/Analyzers/WallstopStudios.DxMessaging.Analyzer.dll", true],
    ["Editor/Analyzers/WallstopStudios.DxMessaging.Analyzer.dll.meta", true],
    ["Runtime/Foo.cs.meta", true],
    ["Runtime/Foo.cs", false],
    ["README.md", false],
    [".github/workflows/ci.yml", false]
  ])("isPackagingRelevant(%s) === %s", (rel, expected) => {
    expect(isPackagingRelevant(rel)).toBe(expected);
  });

  test.each([
    ["Runtime/Foo.cs", true],
    ["docs/guide.md", true],
    ["docs/guide.markdown", true],
    ["package.json", false],
    ["Editor/Analyzers/x.dll", false],
    ["a.yml", false]
  ])("isDocQualityRelevant(%s) === %s", (rel, expected) => {
    expect(isDocQualityRelevant(rel)).toBe(expected);
  });

  test.each([
    ["docs/guide.md", true],
    ["docs/reference/runtime-settings.md", true],
    ["README.md", false],
    ["docs/guide.markdown", true],
    ["scripts/guide.md", false]
  ])("isDocsOutOfTreeLinkRelevant(%s) === %s", (rel, expected) => {
    expect(isDocsOutOfTreeLinkRelevant(rel)).toBe(expected);
  });

  test.each([
    ["Runtime/Foo.cs", true],
    ["Editor/SetupCscRsp.cs", true],
    ["SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators/MessageBusGenerator.cs", true],
    ["Samples~/BasicUsage/Example.cs", true],
    ["CHANGELOG.md", true],
    ["Editor/Analyzers/Analyzer.dll", false],
    ["Tests/Runtime/FooTests.cs", false],
    ["scripts/validate-changelog.js", false],
    ["docs/reference/runtime-settings.md", false]
  ])("isChangelogCoverageRelevant(%s) === %s", (rel, expected) => {
    expect(isChangelogCoverageRelevant(rel)).toBe(expected);
  });

  test.each([
    ["Runtime/Foo.cs", true],
    ["docs/guide.md", true],
    ["docs/guide.markdown", true],
    ["package.json", true],
    [".github/workflows/ci.yml", true],
    [".yamllint.yaml", true],
    ["scripts/hook.ps1", true],
    ["scripts/hook.js", true],
    ["Editor/Analyzers/x.dll", false],
    ["README.txt", false]
  ])("isSpellcheckRelevant(%s) === %s", (rel, expected) => {
    expect(isSpellcheckRelevant(rel)).toBe(expected);
  });

  test.each([
    ["SourceGenerators/Directory.Build.props", true],
    ["SourceGenerators/WallstopStudios.DxMessaging.Analyzer/Analyzer.csproj", true],
    ["SourceGenerators/Some.targets", true],
    ["SourceGenerators/Foo.cs", false],
    ["Directory.Build.props", false],
    ["Editor/Analyzers/x.dll", false],
    ["package.json", false]
  ])("isXmlBuildContractRelevant(%s) === %s", (rel, expected) => {
    expect(isXmlBuildContractRelevant(rel)).toBe(expected);
  });

  test("spellcheck extension set stays in parity with native pre-push and package scripts", () => {
    const config = fs.readFileSync(PRE_COMMIT_CONFIG_PATH, "utf8");
    const configLines = config.split(/\r\n|\r|\n/);
    const cspellStart = configLines.findIndex((line) => /^\s*-\s+id:\s*cspell\s*$/.test(line));
    expect(cspellStart).toBeGreaterThanOrEqual(0);
    const cspellHookBlock = configLines.slice(cspellStart).join("\n");
    const pkg = JSON.parse(fs.readFileSync(PACKAGE_JSON_PATH, "utf8"));
    const expected = getCspellHookExtensions(cspellHookBlock);

    expect(CSPELL_EXTENSIONS.slice().sort()).toEqual(expected);
    expect(getPackageCspellAllExtensions(pkg.scripts?.["check:cspell:all"])).toEqual(expected);
    for (const extension of expected) {
      expect(isSpellcheckRelevant(`example.${extension}`)).toBe(true);
    }
    expect(isSpellcheckRelevant("example.txt")).toBe(false);
  });
});

describe("dispatch table", () => {
  test("has npm-packaging, doc-quality, docs-out-of-tree-links, changelog, and spelling entries with required shape", () => {
    const table = buildDispatchTable();
    const ids = table.map((e) => e.id);
    expect(ids).toEqual(
      expect.arrayContaining([
        "npm-packaging",
        "doc-quality",
        "docs-out-of-tree-links",
        "changelog-coverage",
        "spelling",
        "xml-build-contract"
      ])
    );

    for (const entry of table) {
      expect(typeof entry.matches).toBe("function");
      expect(typeof entry.remediation).toBe("string");
      expect(entry.remediation.length).toBeGreaterThan(0);
      expect(Array.isArray(entry.validators)).toBe(true);
      expect(entry.validators.length).toBeGreaterThan(0);
      for (const v of entry.validators) {
        expect(typeof v.label).toBe("string");
        expect(typeof v.args === "function" || typeof v.run === "function").toBe(true);
        if (typeof v.args === "function") {
          expect(Array.isArray(v.args("/abs/path"))).toBe(true);
        }
      }
    }
  });

  test("npm-packaging validator invokes validate-npm-meta --check (not a single-file arg)", () => {
    const table = buildDispatchTable();
    const packaging = table.find((e) => e.id === "npm-packaging");
    const args = packaging.validators[0].args("/abs/Editor/Analyzers/x.dll");
    expect(args).toEqual(["scripts/validate-npm-meta.js", "--check"]);
  });

  test("doc-quality validators are file-scoped read-only doc checks", () => {
    const table = buildDispatchTable();
    const doc = table.find((e) => e.id === "doc-quality");
    const labels = doc.validators.map((v) => v.label);
    expect(labels).toEqual(
      expect.arrayContaining(["docs-ascii", "docs-code-patterns", "docs-prose"])
    );
    expect(doc.validators[0].args("/abs/x.md")[1]).toBe("/abs/x.md");
  });

  test("docs-out-of-tree-links validator is docs/*.md scoped and file-targeted", () => {
    const table = buildDispatchTable();
    const links = table.find((e) => e.id === "docs-out-of-tree-links");
    expect(links.matches("docs/guides/mcp-local-setup.md")).toBe(true);
    expect(links.matches("docs/guides/mcp-local-setup.markdown")).toBe(true);
    expect(links.matches("README.md")).toBe(false);
    expect(links.validators).toHaveLength(1);
    expect(links.validators[0].label).toBe("docs-out-of-tree-links");
    expect(links.validators[0].args("/abs/docs/guides/mcp-local-setup.md")).toEqual([
      "scripts/validate-docs-out-of-tree-links.js",
      "/abs/docs/guides/mcp-local-setup.md"
    ]);
  });

  test("changelog coverage validator invokes the global coverage check", () => {
    const table = buildDispatchTable();
    const changelog = table.find((e) => e.id === "changelog-coverage");
    expect(changelog.matches("Runtime/Core/MessageBus.cs")).toBe(true);
    expect(changelog.matches("CHANGELOG.md")).toBe(true);
    expect(changelog.matches("scripts/validate-changelog.js")).toBe(false);
    expect(changelog.validators[0].args("/abs/Runtime/Core/MessageBus.cs")).toEqual([
      "scripts/validate-changelog.js",
      "--check-coverage"
    ]);
  });

  test("spelling validator uses the in-process cspell runner", () => {
    const table = buildDispatchTable();
    const spelling = table.find((e) => e.id === "spelling");
    expect(spelling.matches("scripts/__tests__/x.test.js")).toBe(true);
    expect(spelling.matches(".github/workflows/ci.yml")).toBe(true);
    expect(spelling.validators).toHaveLength(1);
    expect(spelling.validators[0].label).toBe("cspell");
    expect(spelling.validators[0].run).toBe(runSpellcheckValidator);
  });

  test("xml-build-contract fires on SourceGenerators build files and runs the contract validator", () => {
    const table = buildDispatchTable();
    const entry = table.find((e) => e.id === "xml-build-contract");
    expect(entry.matches("SourceGenerators/Directory.Build.props")).toBe(true);
    expect(
      entry.matches(
        "SourceGenerators/WallstopStudios.DxMessaging.Analyzer/WallstopStudios.DxMessaging.Analyzer.csproj"
      )
    ).toBe(true);
    expect(entry.matches("SourceGenerators/Foo.cs")).toBe(false);
    expect(entry.matches("package.json")).toBe(false);
    expect(entry.validators).toHaveLength(1);
    expect(entry.validators[0].label).toBe("analyzer-build-contract");
    expect(entry.validators[0].args("/abs/SourceGenerators/Directory.Build.props")).toEqual([
      "scripts/validate-analyzer-build-contract.js"
    ]);
  });
});

describe("cspell helpers", () => {
  test("cspellIssueLocation derives cspell-lib one-based coordinates", () => {
    expect(
      cspellIssueLocation({
        offset: 15,
        line: { offset: 0, position: { line: 7, character: 0 } }
      })
    ).toEqual({ row: 8, col: 16 });
  });

  test("formatCspellIssues emits compact diagnostics and caps output", () => {
    const detail = formatCspellIssues("scripts/x.js", [
      { text: "wurd", row: 3, col: 9 },
      { text: "typoo", row: 4, col: 2 },
      { text: "third", row: 5, col: 1 },
      { text: "fourth", row: 6, col: 1 }
    ]);
    expect(detail).toContain("scripts/x.js:3:9 Unknown word (wurd)");
    expect(detail).toContain("scripts/x.js:4:2 Unknown word (typoo)");
    expect(detail).toContain("+1 more");
  });

  test("runCspellApiValidator reports cspell issues without spawning the managed CLI", async () => {
    const loadConfig = jest.fn(async (configPath) => {
      expect(configPath).toBe(path.join(REPO_ROOT, ".cspell.json"));
      return { settings: true };
    });
    const spellCheckFile = jest.fn(async (absPath, options, config) => {
      expect(absPath).toBe(path.join(REPO_ROOT, "scripts", "x.js"));
      expect(options.root).toBe(REPO_ROOT);
      expect(config).toEqual({ settings: true });
      return {
        checked: true,
        issues: [{ text: "wurd", offset: 10, line: { offset: 0, position: { line: 7 } } }]
      };
    });

    const result = await runCspellApiValidator(path.join(REPO_ROOT, "scripts", "x.js"), REPO_ROOT, {
      importFn: () => ({ loadConfig, spellCheckFile })
    });

    expect(loadConfig).toHaveBeenCalledTimes(1);
    expect(spellCheckFile).toHaveBeenCalledTimes(1);
    expect(result.ok).toBe(false);
    expect(result.detail).toContain("scripts/x.js:8:11 Unknown word (wurd)");
  });

  test("runCspellApiValidator treats cspell API errors as infrastructure failures", async () => {
    await expect(
      runCspellApiValidator(path.join(REPO_ROOT, "scripts", "x.js"), REPO_ROOT, {
        importFn: () => ({
          loadConfig: async () => ({}),
          spellCheckFile: async () => ({
            checked: false,
            issues: [],
            errors: [new Error("dictionary load failed")]
          })
        })
      })
    ).rejects.toThrow(/dictionary load failed/);
    expect(hasCspellInfrastructureError({ errors: 1 }, [])).toBe(true);
  });

  test("runCspellApiValidator treats ignored files as clean no-ops", async () => {
    const result = await runCspellApiValidator(
      path.join(REPO_ROOT, "Samples~", "Example", "readme.md"),
      REPO_ROOT,
      {
        importFn: () => ({
          loadConfig: async () => ({}),
          spellCheckFile: async () => ({
            checked: false,
            issues: [],
            errors: []
          })
        })
      }
    );

    expect(result).toEqual({ label: "cspell", ok: true, detail: "" });
  });

  test("runSpellcheckValidator falls back to the managed runner when cspell import fails", async () => {
    const spawn = fakeSpawn(spawnResult({ status: 1, stdout: "x.js:1:2 - Unknown word (wurd)" }));
    const result = await runSpellcheckValidator(
      path.join(REPO_ROOT, "scripts", "x.js"),
      REPO_ROOT,
      {
        importFn: async () => {
          throw new Error("missing cspell");
        },
        spawnImpl: spawn
      }
    );

    expect(result.ok).toBe(false);
    expect(result.label).toBe("cspell");
    expect(spawn.calls[0].args).toEqual([
      "scripts/run-managed-cspell.js",
      "--no-progress",
      "--no-summary",
      "--no-must-find-files",
      "scripts/x.js"
    ]);
  });

  test("runSpellcheckValidator falls back to the managed runner when cspell API reports errors", async () => {
    const spawn = fakeSpawn(spawnResult({ status: 0, stdout: "" }));
    const result = await runSpellcheckValidator(
      path.join(REPO_ROOT, ".github", "workflows", "ci.yml"),
      REPO_ROOT,
      {
        importFn: () => ({
          loadConfig: async () => ({}),
          spellCheckFile: async () => ({
            checked: false,
            issues: [],
            errors: [new Error("config read failed")]
          })
        }),
        spawnImpl: spawn
      }
    );

    expect(result.ok).toBe(true);
    expect(spawn.calls[0].args).toEqual([
      "scripts/run-managed-cspell.js",
      "--no-progress",
      "--no-summary",
      "--no-must-find-files",
      ".github/workflows/ci.yml"
    ]);
  });
});

describe("real cspell integration", () => {
  let tempDir;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(REPO_ROOT, "dxm-cspell-fixture-"));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  function writeFixture(relativePath, content) {
    const abs = path.join(tempDir, relativePath);
    fs.mkdirSync(path.dirname(abs), { recursive: true });
    fs.writeFileSync(abs, content, "utf8");
    return abs;
  }

  function runManagedCspell(relativePath) {
    return childProcess.spawnSync(
      process.execPath,
      [
        "scripts/run-managed-cspell.js",
        "--no-progress",
        "--no-summary",
        "--no-gitignore",
        path.join(tempDir, relativePath)
      ],
      { cwd: REPO_ROOT, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }
    );
  }

  function runApiSubprocess(relativePath) {
    const script = [
      "const guard = require('./scripts/hooks/post-edit-validate-guard.js');",
      "guard.runCspellApiValidator(process.argv[1], process.cwd()).then((result) => {",
      "  console.log(JSON.stringify(result));",
      "  process.exit(result.ok ? 0 : 1);",
      "}).catch((error) => {",
      "  console.error(error && error.stack ? error.stack : String(error));",
      "  process.exit(2);",
      "});"
    ].join("\n");
    return childProcess.spawnSync(
      process.execPath,
      ["-e", script, path.join(tempDir, relativePath)],
      {
        cwd: REPO_ROOT,
        encoding: "utf8",
        env: normalizeNodeColorEnv(process.env),
        stdio: ["ignore", "pipe", "pipe"]
      }
    );
  }

  function runWarmApiSubprocess(relativePath) {
    const script = [
      "const { performance } = require('perf_hooks');",
      "const guard = require('./scripts/hooks/post-edit-validate-guard.js');",
      "(async () => {",
      "  const file = process.argv[1];",
      "  await guard.runCspellApiValidator(file, process.cwd());",
      "  const start = performance.now();",
      "  const result = await guard.runCspellApiValidator(file, process.cwd());",
      "  console.log(JSON.stringify({ ok: result.ok, ms: performance.now() - start }));",
      "  process.exit(result.ok ? 0 : 1);",
      "})().catch((error) => {",
      "  console.error(error && error.stack ? error.stack : String(error));",
      "  process.exit(2);",
      "});"
    ].join("\n");
    return childProcess.spawnSync(
      process.execPath,
      ["-e", script, path.join(tempDir, relativePath)],
      {
        cwd: REPO_ROOT,
        encoding: "utf8",
        env: normalizeNodeColorEnv(process.env),
        stdio: ["ignore", "pipe", "pipe"]
      }
    );
  }

  test.each([
    ["typo.md", "zzzxqword\n"],
    ["typo.markdown", "zzzxqword\n"],
    ["typo.cs", 'public sealed class Demo { string Value = "zzzxqword"; }\n'],
    ["typo.json", '{ "name": "zzzxqword" }\n'],
    ["typo.js", "const value = 'zzzxqword';\n"],
    ["typo.ps1", '$value = "zzzxqword"\n'],
    ["typo.yaml", "name: zzzxqword\n"],
    ["typo.yml", "name: zzzxqword\n"]
  ])("real cspell API catches a %s typo before native pre-push", async (relativePath, content) => {
    writeFixture(relativePath, content);
    const apiRun = runApiSubprocess(relativePath);
    const cli = runManagedCspell(relativePath);

    expect(apiRun.status).toBe(1);
    expect(apiRun.stderr).toBe("");
    expect(apiRun.stdout).not.toBe("");
    const api = JSON.parse(apiRun.stdout);
    expect(api.ok).toBe(false);
    expect(api.detail).toContain("Unknown word (zzzxqword)");
    expect(cli.status).toBe(1);
    expect(cli.stdout).toContain("Unknown word (zzzxqword)");
  });

  test("real cspell API warm file check stays inside the edit-time hot-path budget", () => {
    writeFixture("clean.js", "const knownValue = 1;\n");
    const run = runWarmApiSubprocess("clean.js");

    expect(run.status).toBe(0);
    expect(run.stderr).toBe("");
    const result = JSON.parse(run.stdout);
    expect(result.ok).toBe(true);
    expect(result.ms).toBeLessThan(750);
  });
});

describe("runValidator", () => {
  const validator = { label: "demo", args: (abs) => ["scripts/x.js", abs] };

  test("status 0 => ok, no detail", async () => {
    const spawn = fakeSpawn(spawnResult({ status: 0, stdout: "ok" }));
    await expect(runValidator(validator, "/abs/x", REPO_ROOT, spawn)).resolves.toEqual({
      label: "demo",
      ok: true,
      detail: ""
    });
  });

  test("non-zero status => not ok, detail is the output tail", async () => {
    const spawn = fakeSpawn(spawnResult({ status: 1, stdout: "line1\nline2\nBOOM" }));
    const out = await runValidator(validator, "/abs/x", REPO_ROOT, spawn);
    expect(out.ok).toBe(false);
    expect(out.detail).toContain("BOOM");
  });

  test("ENOENT (node missing) => ok (advisory guards never manufacture infra noise)", async () => {
    const spawn = fakeSpawn(spawnResult({ status: null, error: { code: "ENOENT" } }));
    expect((await runValidator(validator, "/abs/x", REPO_ROOT, spawn)).ok).toBe(true);
  });

  test("passes the file path through args and sets the re-entrancy env", async () => {
    const spawn = fakeSpawn(spawnResult({ status: 0 }));
    await runValidator(validator, "/abs/here", REPO_ROOT, spawn);
    expect(spawn.calls[0].args).toEqual(["scripts/x.js", "/abs/here"]);
    expect(spawn.calls[0].options.env[ACTIVE_ENV]).toBe("1");
  });
});

describe("evaluate", () => {
  const table = [
    {
      id: "pkg",
      matches: (r) => r === "package.json",
      remediation: "fix pkg",
      validators: [{ label: "npm-meta", args: () => ["scripts/validate-npm-meta.js", "--check"] }]
    },
    {
      id: "doc",
      matches: (r) => /\.md$/.test(r),
      remediation: "fix docs",
      validators: [{ label: "ascii", args: (a) => ["scripts/validate-docs-ascii.js", a] }]
    }
  ];

  test("returns a report only for matching entries whose validator failed", async () => {
    const spawn = fakeSpawn(spawnResult({ status: 1, stdout: "violation" }));
    const reports = await evaluate("package.json", "/abs/package.json", REPO_ROOT, {
      spawnImpl: spawn,
      table
    });
    expect(reports).toHaveLength(1);
    expect(reports[0].id).toBe("pkg");
    expect(reports[0].failures[0].label).toBe("npm-meta");
  });

  test("passing validators => no reports", async () => {
    const spawn = fakeSpawn(spawnResult({ status: 0 }));
    await expect(
      evaluate("package.json", "/abs/package.json", REPO_ROOT, { spawnImpl: spawn, table })
    ).resolves.toEqual([]);
  });

  test("non-matching path => no validators run, no reports", async () => {
    const spawn = fakeSpawn(spawnResult({ status: 1 }));
    await expect(
      evaluate("src/Foo.cs", "/abs/src/Foo.cs", REPO_ROOT, { spawnImpl: spawn, table })
    ).resolves.toEqual([]);
    expect(spawn.calls).toHaveLength(0);
  });
});

describe("buildMessage", () => {
  test("no reports => null", () => {
    expect(buildMessage("package.json", [])).toBeNull();
  });

  test("reports => message naming the file, the entry id, and remediation", () => {
    const msg = buildMessage("package.json", [
      {
        id: "npm-packaging",
        remediation: "Run validate-npm-meta.",
        failures: [{ label: "validate-npm-meta", detail: "missing .meta" }]
      }
    ]);
    expect(msg).toContain("package.json");
    expect(msg).toContain("[npm-packaging]");
    expect(msg).toContain("validate-npm-meta");
    expect(msg).toContain("missing .meta");
    expect(msg).toContain("Run validate-npm-meta.");
  });
});

describe("run (PostToolUse entry)", () => {
  let writeSpy;
  let written;

  beforeEach(() => {
    written = [];
    writeSpy = jest.spyOn(process.stdout, "write").mockImplementation((chunk) => {
      written.push(String(chunk));
      return true;
    });
  });

  afterEach(() => {
    writeSpy.mockRestore();
  });

  const payloadFor = (absPath) =>
    JSON.stringify({ hook_event_name: "PostToolUse", tool_input: { file_path: absPath } });

  test("re-entrancy: ACTIVE_ENV=1 => exit 0, silent", async () => {
    const spawn = fakeSpawn(spawnResult({ status: 1 }));
    const code = await run(
      payloadFor(path.join(REPO_ROOT, "package.json")),
      { [ACTIVE_ENV]: "1" },
      { spawnImpl: spawn }
    );
    expect(code).toBe(0);
    expect(written).toHaveLength(0);
    expect(spawn.calls).toHaveLength(0);
  });

  test("invalid JSON => exit 0, silent", async () => {
    await expect(run("{not json", {}, {})).resolves.toBe(0);
    expect(written).toHaveLength(0);
  });

  test("missing file_path => exit 0, silent", async () => {
    await expect(run(JSON.stringify({ tool_input: {} }), {}, {})).resolves.toBe(0);
    expect(written).toHaveLength(0);
  });

  test("YAML file is not globally skipped; it can receive read-only spelling validation", async () => {
    const spawn = fakeSpawn(spawnResult({ status: 1 }));
    const code = await run(
      payloadFor(path.join(REPO_ROOT, ".yamllint.yaml")),
      {},
      {
        spawnImpl: spawn,
        table: [
          {
            id: "spelling",
            matches: (r) => /\.ya?ml$/.test(r),
            remediation: "fix spelling",
            validators: [{ label: "cspell", args: () => ["scripts/fake-cspell.js"] }]
          }
        ]
      }
    );
    expect(code).toBe(0);
    expect(spawn.calls).toHaveLength(1);
    const out = JSON.parse(written[0]);
    expect(out.systemMessage).toContain("spelling");
  });

  test("non-matching existing file => exit 0, silent, no spawn", async () => {
    const spawn = fakeSpawn(spawnResult({ status: 1 }));
    const code = await run(
      payloadFor(path.join(REPO_ROOT, "LICENSE.md")),
      {},
      {
        spawnImpl: spawn,
        table: [
          {
            id: "pkg",
            matches: (r) => r === "package.json",
            remediation: "x",
            validators: [{ label: "a", args: () => ["s.js"] }]
          }
        ]
      }
    );
    expect(code).toBe(0);
    expect(spawn.calls).toHaveLength(0);
  });

  test("path outside the repo => exit 0, silent", async () => {
    const spawn = fakeSpawn(spawnResult({ status: 1 }));
    const code = await run(payloadFor("/etc/hosts"), {}, { spawnImpl: spawn });
    expect(code).toBe(0);
    expect(spawn.calls).toHaveLength(0);
  });

  test("packaging edit with a FAILING validator => exit 0 and an advisory systemMessage", async () => {
    const spawn = fakeSpawn(spawnResult({ status: 1, stdout: "Missing .meta in tarball" }));
    const code = await run(
      payloadFor(path.join(REPO_ROOT, "package.json")),
      {},
      {
        spawnImpl: spawn,
        table: [
          {
            id: "npm-packaging",
            matches: (r) => r === "package.json",
            remediation: "fix package",
            validators: [
              {
                label: "validate-npm-meta",
                args: () => ["scripts/validate-npm-meta.js", "--check"]
              }
            ]
          }
        ]
      }
    );
    expect(code).toBe(0);
    expect(written).toHaveLength(1);
    const out = JSON.parse(written[0]);
    expect(out.hookSpecificOutput.hookEventName).toBe("PostToolUse");
    expect(out.systemMessage).toContain("package.json");
    expect(out.systemMessage).toContain("npm-packaging");
    expect(out.hookSpecificOutput.additionalContext).toBe(out.systemMessage);
  });

  test("packaging edit with a PASSING validator => exit 0, silent", async () => {
    const spawn = fakeSpawn(spawnResult({ status: 0 }));
    const code = await run(
      payloadFor(path.join(REPO_ROOT, "package.json")),
      {},
      {
        spawnImpl: spawn,
        table: [
          {
            id: "npm-packaging",
            matches: (r) => r === "package.json",
            remediation: "fix package",
            validators: [
              {
                label: "validate-npm-meta",
                args: () => ["scripts/validate-npm-meta.js", "--check"]
              }
            ]
          }
        ]
      }
    );
    expect(code).toBe(0);
    expect(written).toHaveLength(0);
  });

  test("default table runs file-scoped spelling for an edited JS file", async () => {
    const importFn = () => ({
      loadConfig: async () => ({}),
      spellCheckFile: async () => ({
        checked: true,
        issues: [{ text: "wurd", row: 1, col: 5 }]
      })
    });

    const code = await run(
      payloadFor(path.join(REPO_ROOT, "scripts", "hooks", "post-edit-validate-guard.js")),
      {},
      { importFn }
    );

    expect(code).toBe(0);
    const out = JSON.parse(written[0]);
    expect(out.systemMessage).toContain("[spelling]");
    expect(out.systemMessage).toContain("Unknown word (wurd)");
  });
});

describe("helpers", () => {
  test("resolveRepoRoot prefers CLAUDE_PROJECT_DIR when set", () => {
    const prev = process.env.CLAUDE_PROJECT_DIR;
    process.env.CLAUDE_PROJECT_DIR = "/tmp/some-root";
    try {
      expect(resolveRepoRoot()).toBe("/tmp/some-root");
    } finally {
      if (prev === undefined) {
        delete process.env.CLAUDE_PROJECT_DIR;
      } else {
        process.env.CLAUDE_PROJECT_DIR = prev;
      }
    }
  });

  test("toRepoRelativePosix returns null for outside-repo paths and POSIX for inside", () => {
    expect(toRepoRelativePosix(REPO_ROOT, "/etc/hosts")).toBeNull();
    expect(toRepoRelativePosix(REPO_ROOT, path.join(REPO_ROOT, "package.json"))).toBe(
      "package.json"
    );
  });
});
