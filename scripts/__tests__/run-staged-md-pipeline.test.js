/**
 * @fileoverview Tests for run-staged-md-pipeline.js. The pipeline composes
 * existing fixers and validators in-process; this suite covers the
 * orchestration layer (filtering, sequencing, modification tracking,
 * validator surfacing) without mocking the underlying fixer / validator
 * APIs.
 */

"use strict";

const childProcess = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const {
  isApplicable,
  isExcluded,
  ALLOWED_EXTS,
  toImportFileUrl,
  getMarkdownlintRelativePaths,
  isRecoverableToolLoadError,
  runNpxCommand,
  runStagedMdPipeline,
  runMarkdownlintInProcess,
  main
} = require("../run-staged-md-pipeline");

const fixMd036 = require("../fix-md036-headings");
const fixMd029Md051 = require("../fix-md029-md051");

const REPO_ROOT = path.resolve(__dirname, "../..");
const PIPELINE_SCRIPT = path.join(REPO_ROOT, "scripts", "run-staged-md-pipeline.js");

function runPipelineSubprocess(argv) {
  // Spawn a fresh Node process so prettier's ESM-via-dynamic-import path
  // is exercised in a real environment rather than the Jest VM (Jest
  // requires --experimental-vm-modules to allow dynamic import inside
  // an in-process test).
  return childProcess.spawnSync(process.execPath, [PIPELINE_SCRIPT, ...argv], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });
}

function makeTempFile(name, body) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "staged-md-pipeline-"));
  const target = path.join(dir, name);
  fs.writeFileSync(target, body, "utf8");
  return { dir, target };
}

describe("run-staged-md-pipeline", () => {
  describe("filtering", () => {
    test("ALLOWED_EXTS gates only the markdown extensions the YAML hook targets", () => {
      expect(ALLOWED_EXTS.has(".md")).toBe(true);
      expect(ALLOWED_EXTS.has(".markdown")).toBe(true);
      expect(ALLOWED_EXTS.has(".cs")).toBe(false);
      expect(ALLOWED_EXTS.has(".json")).toBe(false);
    });

    test("isExcluded matches the YAML exclude regex", () => {
      expect(isExcluded("Library/PackageCache/foo.md")).toBe(true);
      expect(isExcluded("Temp/scratch.md")).toBe(true);
      expect(isExcluded("node_modules/x/README.md")).toBe(true);
      expect(isExcluded("Runtime/Foo/bin/Bar.md")).toBe(true);
      expect(isExcluded("Editor/Bar/obj/Baz.md")).toBe(true);
    });

    test("isExcluded does not match legitimate markdown paths", () => {
      expect(isExcluded("README.md")).toBe(false);
      expect(isExcluded(".llm/skills/performance/git-hook-performance.md")).toBe(false);
    });

    test("isApplicable rejects non-markdown extensions even when path is otherwise valid", () => {
      const repoRoot = path.resolve(__dirname, "../..");
      expect(isApplicable(path.join(repoRoot, "README.md"))).toBe(true);
      expect(isApplicable(path.join(repoRoot, "Runtime/Core/MessageBus/MessageBus.cs"))).toBe(
        false
      );
      expect(isApplicable(path.join(repoRoot, "package.json"))).toBe(false);
    });
  });

  describe("end-to-end orchestration", () => {
    afterEach(() => {
      // No global state to reset; the helpers below clean up their
      // own temp dirs.
    });

    test("pipeline reports zero violations and zero modifications for a clean known-good file", async () => {
      const repoRoot = path.resolve(__dirname, "../..");
      const target = path.join(repoRoot, "README.md");
      // README.md is part of the published surface and must always
      // pass the documentation gates without modification.
      const result = await runStagedMdPipeline([target], {
        skipMarkdownlint: true,
        skipPrettier: true
      });
      expect(result.applicable).toEqual([target]);
      expect(result.modified).toEqual([]);
      expect(result.violations.ascii.violations).toEqual([]);
      expect(result.violations.codePatterns.violations).toEqual([]);
      expect(result.violations.prose.violations).toEqual([]);
    });

    test("pipeline runs the in-process fixers AND records the file as modified when they rewrite content", async () => {
      // MD036 input: bold-only paragraph isolated by blank lines must
      // become a heading. MD029 input: an ordered list with `2.` after
      // `1.` must be normalized to `1.` `1.`.
      const fixture = ["# Top", "", "**Subheading-as-bold**", "", "1. first", "2. second", ""].join(
        "\n"
      );
      const { dir, target } = makeTempFile("fixers.md", fixture);
      try {
        // skipPrettier: prettier 3.x routes its CJS entry through a
        // dynamic import("./index.mjs") that Jest cannot run without
        // --experimental-vm-modules. The subprocess test below
        // exercises the prettier path in a real Node process.
        const result = await runStagedMdPipeline([target], {
          skipMarkdownlint: true,
          skipPrettier: true
        });
        expect(result.applicable).toEqual([target]);
        expect(result.modified).toContain(target);

        const written = fs.readFileSync(target, "utf8");
        // MD036 should have rewritten the bold-only line to a heading.
        expect(written).toMatch(/^##? Subheading-as-bold$/m);
        expect(written).not.toMatch(/^\*\*Subheading-as-bold\*\*$/m);
        // MD029 normalization should have produced two `1.` prefixes.
        expect((written.match(/^1\. /gm) || []).length).toBeGreaterThanOrEqual(2);
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    });

    test("pipeline surfaces validator violations on the prose stage", async () => {
      // The marketing word "seamless" is on the prose-policy ban list.
      const fixture = "# Title\n\nOur framework offers a seamless experience.\n";
      const { dir, target } = makeTempFile("violation.md", fixture);
      try {
        const writeSpy = jest.spyOn(process.stderr, "write").mockImplementation(() => true);
        const stdoutSpy = jest.spyOn(process.stdout, "write").mockImplementation(() => true);
        try {
          const result = await runStagedMdPipeline([target], {
            skipMarkdownlint: true,
            skipPrettier: true
          });
          expect(result.violations.prose.violations.length).toBeGreaterThan(0);
        } finally {
          writeSpy.mockRestore();
          stdoutSpy.mockRestore();
        }
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    });

    test("pipeline auto-fixes out-of-tree links for docs/ markdown files", async () => {
      // Mirror the failure mode that originally took down
      // `Validate Documentation Build / Build documentation (strict mode)`:
      // a docs/runbooks/<file>.md that links UP into .github/workflows/...
      // via a relative path. The pipeline must surface this via the
      // outOfTreeLinks key.
      const repoRoot = path.resolve(__dirname, "../..");
      const docsRunbooks = path.join(repoRoot, "docs", "runbooks");
      // Use mkdtempSync DIRECTLY inside docs/runbooks/ so the validator's
      // isDocsMarkdown gate recognizes the file as docs/-rooted. The temp
      // dir is removed in the finally block.
      const dir = fs.mkdtempSync(path.join(docsRunbooks, "out-of-tree-pipeline-"));
      const target = path.join(dir, "fixture.md");
      // Inline backticks and fenced blocks must NOT be rewritten (covered by
      // unit tests). The bare inline link is auto-rewritten.
      const fixture = [
        "# Out-of-tree fixture",
        "",
        "See [bad](../../../.github/workflows/stuck-job-watchdog.yml) for context.",
        "",
        "```text",
        "[ignored](../../../.github/workflows/stuck-job-watchdog.yml)",
        "```",
        ""
      ].join("\n");
      fs.writeFileSync(target, fixture, "utf8");
      const writeSpy = jest.spyOn(process.stderr, "write").mockImplementation(() => true);
      const stdoutSpy = jest.spyOn(process.stdout, "write").mockImplementation(() => true);
      try {
        const result = await runStagedMdPipeline([target], {
          skipMarkdownlint: true,
          skipPrettier: true
        });
        expect(result.applicable).toEqual([target]);
        expect(result.modified).toContain(target);
        expect(result.violations.outOfTreeLinks.violations).toHaveLength(0);

        const written = fs.readFileSync(target, "utf8");
        expect(written).toContain(
          "https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/stuck-job-watchdog.yml"
        );
        expect(written).toContain("[ignored](../../../.github/workflows/stuck-job-watchdog.yml)");
      } finally {
        writeSpy.mockRestore();
        stdoutSpy.mockRestore();
        fs.rmSync(dir, { recursive: true, force: true });
      }
    });

    test("pipeline auto-normalizes right-arrow menu paths before ASCII validation", async () => {
      const rightArrow = String.fromCodePoint(0x2192);
      const fixture = [
        "# Title",
        "",
        `Menu path: Tools ${rightArrow} Wallstop Studios ${rightArrow}`,
        `DxMessaging ${rightArrow} Settings.`,
        ""
      ].join("\n");
      const { dir, target } = makeTempFile("ascii-normalized.md", fixture);
      try {
        const writeSpy = jest.spyOn(process.stderr, "write").mockImplementation(() => true);
        const stdoutSpy = jest.spyOn(process.stdout, "write").mockImplementation(() => true);
        try {
          const result = await runStagedMdPipeline([target], {
            skipMarkdownlint: true,
            skipPrettier: true
          });
          expect(result.modified).toContain(target);
          expect(result.violations.ascii.violations).toEqual([]);

          const written = fs.readFileSync(target, "utf8");
          expect(written).not.toContain(rightArrow);
          expect(written).toContain("Tools -> Wallstop Studios ->");
          expect(written).toContain("DxMessaging -> Settings.");
          for (const ch of written) {
            expect(ch.codePointAt(0)).toBeLessThan(0x80);
          }
        } finally {
          writeSpy.mockRestore();
          stdoutSpy.mockRestore();
        }
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    });

    test("pipeline surfaces ASCII validator violations for non-normalizable emoji", async () => {
      const rocket = String.fromCodePoint(0x1f680);
      const fixture = `# Title\n\nBad emoji ${rocket} in prose.\n`;
      const { dir, target } = makeTempFile("ascii-violation.md", fixture);
      try {
        const writeSpy = jest.spyOn(process.stderr, "write").mockImplementation(() => true);
        const stdoutSpy = jest.spyOn(process.stdout, "write").mockImplementation(() => true);
        try {
          const result = await runStagedMdPipeline([target], {
            skipMarkdownlint: true,
            skipPrettier: true
          });
          expect(result.violations.ascii.violations).toHaveLength(1);
          expect(result.violations.ascii.violations[0].codepoint).toBe(0x1f680);
        } finally {
          writeSpy.mockRestore();
          stdoutSpy.mockRestore();
        }
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    });

    test("subprocess invocation surfaces violations and exits 1 (covers prettier in-process path)", () => {
      // The full main() path in-process would exercise the prettier
      // ESM dynamic-import surface, which Jest cannot do without
      // --experimental-vm-modules. Spawning a fresh Node lets us cover
      // that real codepath end-to-end without changing the test
      // runner config.
      const fixture = "# Title\n\nOur framework offers a seamless experience.\n";
      const { dir, target } = makeTempFile("subprocess.md", fixture);
      try {
        const result = runPipelineSubprocess([target]);
        expect(result.status).toBe(1);
        // The prose validator emits a `[marketing]` rule id for
        // banned terms; surfacing this string proves the pipeline
        // reached the prose stage.
        expect(result.stderr).toMatch(/\[marketing\]\s+seamless/);
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    });

    test("subprocess invocation passes for a clean known-good file (covers prettier success path)", () => {
      const repoRoot = path.resolve(__dirname, "../..");
      const target = path.join(repoRoot, "README.md");
      const result = runPipelineSubprocess([target]);
      expect(result.status).toBe(0);
      expect(result.stdout).toMatch(/run-staged-md-pipeline:\s+0 violations/);
    });

    test("pipeline silently drops files outside the (?i)\\.(md|markdown)$ filter", async () => {
      const { dir, target } = makeTempFile("stowaway.cs", "// nothing markdown here\n");
      try {
        const result = await runStagedMdPipeline([target], {
          skipMarkdownlint: true
        });
        expect(result.applicable).toEqual([]);
        expect(result.modified).toEqual([]);
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    });

    test("pipeline tolerates missing input file (skips silently through every stage including markdownlint)", async () => {
      const dir = fs.mkdtempSync(path.join(os.tmpdir(), "staged-md-pipeline-"));
      const ghost = path.join(dir, "does-not-exist.md");
      try {
        // Do NOT pass skipMarkdownlint: true here. The bug was that
        // markdownlint's batched fs.statSync(path) crashes with
        // ENOENT on a missing file because the per-file loop's
        // silent skip didn't propagate to the batched stage. This
        // test exercises the full pipeline including markdownlint
        // to lock that behaviour in.
        const result = await runStagedMdPipeline([ghost], {
          skipPrettier: true
        });
        // Filter accepts the .md extension, but the file read fails
        // and the per-file loop skips it. Markdownlint must also
        // skip it (no ENOENT crash).
        expect(result.applicable).toEqual([ghost]);
        expect(result.modified).toEqual([]);
        expect(result.markdownlintErrors).toBe(0);
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    });

    test("main exits 0 when given an empty argv", async () => {
      const writeSpy = jest.spyOn(process.stdout, "write").mockImplementation(() => true);
      try {
        const code = await main([]);
        expect(code).toBe(0);
      } finally {
        writeSpy.mockRestore();
      }
    });

    test("toImportFileUrl emits a file:// specifier for dynamic import", () => {
      const modulePath = path.join(
        REPO_ROOT,
        "node_modules",
        "markdownlint-cli2",
        "markdownlint-cli2.mjs"
      );

      const specifier = toImportFileUrl(modulePath);
      expect(specifier.startsWith("file://")).toBe(true);
      expect(specifier).toContain("markdownlint-cli2.mjs");
    });

    test("toImportFileUrl normalizes Windows absolute paths to file URLs", () => {
      const windowsPath = String.raw`D:\repo\markdownlint-cli2.mjs`;
      expect(toImportFileUrl(windowsPath)).toBe("file:///D:/repo/markdownlint-cli2.mjs");
    });

    test("toImportFileUrl normalizes Windows UNC absolute paths to file URLs", () => {
      const uncPath = String.raw`\\fileserver\engineering\markdownlint-cli2.mjs`;
      expect(toImportFileUrl(uncPath)).toBe("file://fileserver/engineering/markdownlint-cli2.mjs");
    });

    test("toImportFileUrl preserves forward-slash UNC absolute paths", () => {
      const uncPath = "//fileserver/engineering/markdownlint-cli2.mjs";
      expect(toImportFileUrl(uncPath)).toBe("file://fileserver/engineering/markdownlint-cli2.mjs");
    });

    test("markdownlint argv uses POSIX repo-relative paths", () => {
      const repoRoot = path.resolve(__dirname, "../..");
      const absPaths = [
        path.join(repoRoot, "docs", "guides", "testing.md"),
        path.join(repoRoot, "Samples~", "Mini Combat", "README.md")
      ];

      expect(getMarkdownlintRelativePaths(absPaths)).toEqual([
        "docs/guides/testing.md",
        "Samples~/Mini Combat/README.md"
      ]);
    });

    test("recoverable tool load detection covers missing transitive packages", () => {
      const error = new Error(
        "Cannot find package 'D:\\repo\\node_modules\\fast-glob\\index.js' imported from D:\\repo\\node_modules\\globby\\index.js"
      );
      error.code = "ERR_MODULE_NOT_FOUND";

      expect(isRecoverableToolLoadError(error)).toBe(true);
    });

    test("markdownlint falls back to pinned npx when local dependency graph is incomplete", async () => {
      const fixture = "# Title\n\nBody text.\n";
      const { dir, target } = makeTempFile("fallback.md", fixture);
      const modifiedSet = new Set();
      const importError = new Error(
        "Cannot find package 'fast-glob' imported from globby/index.js"
      );
      importError.code = "ERR_MODULE_NOT_FOUND";
      const fallback = jest.fn(() => ({ errors: 0 }));
      const stderrSpy = jest.spyOn(process.stderr, "write").mockImplementation(() => true);

      try {
        const result = await runMarkdownlintInProcess([target], modifiedSet, {
          importModuleFn: async () => {
            throw importError;
          },
          runNpxMarkdownlintFn: fallback
        });

        expect(result).toEqual({ errors: 0 });
        expect(fallback).toHaveBeenCalledWith([target], modifiedSet);
        expect(stderrSpy).toHaveBeenCalledWith(
          expect.stringContaining("using pinned npx fallback")
        );
      } finally {
        stderrSpy.mockRestore();
        fs.rmSync(dir, { recursive: true, force: true });
      }
    });

    test("npx fallback prefers bundled npm CLI through node so filenames are not shell-expanded", () => {
      const calls = [];
      const result = runNpxCommand(["--yes", "tool", "docs/has & ampersand.md"], {
        execPath: String.raw`C:\node\node.exe`,
        resolveBundledNpxCliPathFn: () => String.raw`C:\node\node_modules\npm\bin\npx-cli.js`,
        runToolCommandFn: (command, args) => {
          calls.push({ command, args });
          return { status: 0 };
        }
      });

      expect(result).toEqual({ status: 0 });
      expect(calls).toEqual([
        {
          command: String.raw`C:\node\node.exe`,
          args: [
            String.raw`C:\node\node_modules\npm\bin\npx-cli.js`,
            "--yes",
            "tool",
            "docs/has & ampersand.md"
          ]
        }
      ]);
    });

    test("npx fallback fails closed when a safe bundled npm CLI is unavailable", () => {
      expect(() =>
        runNpxCommand(["--yes", "tool", "docs/has & ampersand.md"], {
          execPath: String.raw`C:\node\node.exe`,
          resolveBundledNpxCliPathFn: () => null,
          runToolCommandFn: jest.fn()
        })
      ).toThrow("Unable to locate npm's npx-cli.js");
    });
  });

  describe("standalone CLI surface", () => {
    // The pipeline composes existing scripts; their CLI entry points
    // must keep working so ad-hoc invocations are unchanged.

    test("fix-md036-headings.js still exposes processMarkdownContent", () => {
      const before = "# T\n\n**b**\n";
      const result = fixMd036.processMarkdownContent(before);
      expect(typeof result.changed).toBe("boolean");
      expect(typeof result.content).toBe("string");
    });

    test("fix-md029-md051.js still exposes processMarkdownContent", () => {
      const before = "1. one\n2. two\n";
      const result = fixMd029Md051.processMarkdownContent(before);
      expect(typeof result.changed).toBe("boolean");
      expect(typeof result.content).toBe("string");
    });

    test("fix-md036-headings.js --help exits 0 when called via main()", () => {
      const writeSpy = jest.spyOn(process.stdout, "write").mockImplementation(() => true);
      try {
        const code = fixMd036.main(["--help"]);
        expect(code).toBe(0);
        expect(writeSpy).toHaveBeenCalled();
      } finally {
        writeSpy.mockRestore();
      }
    });

    test("fix-md029-md051.js --help exits 0 when called via main()", () => {
      const writeSpy = jest.spyOn(process.stdout, "write").mockImplementation(() => true);
      try {
        const code = fixMd029Md051.main(["--help"]);
        expect(code).toBe(0);
        expect(writeSpy).toHaveBeenCalled();
      } finally {
        writeSpy.mockRestore();
      }
    });
  });
});
