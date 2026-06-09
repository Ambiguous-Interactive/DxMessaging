/**
 * @fileoverview Unit tests for scripts/lib/workflow-fixtures.js.
 *
 * Pins the exact output contracts of the shared workflow-fixture builders
 * (`asLines`, `singleJobLines`, `singleJobWorkflow`, `writeWorkflowFile`).
 * The oracle-suite fixture fold replaces hand-written literal fixtures with
 * these builders on the strength of byte-identical output, so every expected
 * value here is a fully-written literal -- a regression in scaffold shape,
 * newline handling, or verbatim emission would silently rewrite hundreds of
 * workflow-validator fixtures. The temp-dir tests dogfood jest-fixtures'
 * `makeTempDir`/`cleanupDir` with try/finally so no scratch directories leak.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const { makeTempDir, cleanupDir } = require("../jest-fixtures");
const {
  asLines,
  singleJobLines,
  singleJobWorkflow,
  writeWorkflowFile
} = require("../workflow-fixtures");

describe("workflow-fixtures", () => {
  describe("asLines", () => {
    test("strips exactly one leading newline before splitting", () => {
      expect(asLines("\njobs:\n  build:")).toEqual(["jobs:", "  build:"]);
    });

    test("two leading newlines leave one empty leading element", () => {
      expect(asLines("\n\njobs:")).toEqual(["", "jobs:"]);
    });

    test("a fixture without a leading newline is split unchanged", () => {
      expect(asLines("jobs:\n  build:")).toEqual(["jobs:", "  build:"]);
    });

    test("a fixture ending in a newline yields a trailing empty element", () => {
      expect(asLines("\njobs:\n  build:\n")).toEqual(["jobs:", "  build:", ""]);
    });

    test("does not normalize carriage returns", () => {
      expect(asLines("a\r\nb")).toEqual(["a\r", "b"]);
    });

    test("matches the byte-for-byte behavior of the hoisted suite-local helper", () => {
      // Exact body of the local helper this module hoisted from
      // scripts/__tests__/validate-workflows-concurrency-and-labels.test.js.
      const hoistedReference = (text) => {
        const trimmed = text.replace(/^\n/, "");
        return trimmed.split("\n");
      };
      const samples = [
        "",
        "\n",
        "\n\n",
        "jobs:",
        "\njobs:\n  build:\n    steps:\n",
        "\n\n  indented\n\n",
        "a\r\nb\r\n",
        "no newlines at all"
      ];
      for (const sample of samples) {
        expect(asLines(sample)).toEqual(hoistedReference(sample));
      }
    });
  });

  describe("singleJobLines", () => {
    test("defaults to ubuntu-latest with no header or job keys", () => {
      expect(
        singleJobLines("build", ["      - uses: actions/checkout@v4", "      - run: npm test"])
      ).toEqual([
        "jobs:",
        "  build:",
        "    runs-on: ubuntu-latest",
        "    steps:",
        "      - uses: actions/checkout@v4",
        "      - run: npm test"
      ]);
    });

    test("emits an explicit runs-on value verbatim, including inline label arrays", () => {
      expect(
        singleJobLines("unity-tests", ["      - run: echo hi"], {
          runsOn: "[self-hosted, Windows, RAM-64GB]"
        })
      ).toEqual([
        "jobs:",
        "  unity-tests:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - run: echo hi"
      ]);
    });

    test("emits header lines verbatim before jobs:", () => {
      expect(
        singleJobLines("build", ["      - run: echo hi"], {
          header: ["name: CI", "on: [push]", ""]
        })
      ).toEqual([
        "name: CI",
        "on: [push]",
        "",
        "jobs:",
        "  build:",
        "    runs-on: ubuntu-latest",
        "    steps:",
        "      - run: echo hi"
      ]);
    });

    test("emits multi-line jobKeys blocks verbatim between runs-on and steps", () => {
      expect(
        singleJobLines("matrix-job", ["      - run: echo hi"], {
          jobKeys: [
            "    concurrency:",
            "      group: matrix-${{ github.ref }}-${{ matrix.os }}",
            "      cancel-in-progress: false",
            "    strategy:",
            "      matrix:",
            "        os: [ubuntu-latest, windows-latest]"
          ]
        })
      ).toEqual([
        "jobs:",
        "  matrix-job:",
        "    runs-on: ubuntu-latest",
        "    concurrency:",
        "      group: matrix-${{ github.ref }}-${{ matrix.os }}",
        "      cancel-in-progress: false",
        "    strategy:",
        "      matrix:",
        "        os: [ubuntu-latest, windows-latest]",
        "    steps:",
        "      - run: echo hi"
      ]);
    });

    test("combines header, runsOn, and jobKeys around the scaffold in order", () => {
      expect(
        singleJobLines("unity-tests", ["      - run: echo combined"], {
          runsOn: "[self-hosted, Windows, RAM-64GB]",
          header: ["name: Combined", "on:", "  push: {}", ""],
          jobKeys: ["    timeout-minutes: 90"]
        })
      ).toEqual([
        "name: Combined",
        "on:",
        "  push: {}",
        "",
        "jobs:",
        "  unity-tests:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    timeout-minutes: 90",
        "    steps:",
        "      - run: echo combined"
      ]);
    });

    test("an empty steps array ends the scaffold at the bare steps: line", () => {
      expect(singleJobLines("empty", [])).toEqual([
        "jobs:",
        "  empty:",
        "    runs-on: ubuntu-latest",
        "    steps:"
      ]);
    });

    test("does not mutate its inputs (frozen arrays pass through untouched)", () => {
      const header = Object.freeze(["name: Frozen", ""]);
      const jobKeys = Object.freeze(["    timeout-minutes: 10"]);
      const steps = Object.freeze(["      - run: echo frozen"]);
      expect(
        singleJobLines("frozen-job", steps, { runsOn: "windows-latest", header, jobKeys })
      ).toEqual([
        "name: Frozen",
        "",
        "jobs:",
        "  frozen-job:",
        "    runs-on: windows-latest",
        "    timeout-minutes: 10",
        "    steps:",
        "      - run: echo frozen"
      ]);
      expect(header).toEqual(["name: Frozen", ""]);
      expect(jobKeys).toEqual(["    timeout-minutes: 10"]);
      expect(steps).toEqual(["      - run: echo frozen"]);
    });

    test("returns a fresh array on every call", () => {
      const steps = ["      - run: echo hi"];
      const first = singleJobLines("repeat", steps);
      const second = singleJobLines("repeat", steps);
      expect(first).not.toBe(second);
      expect(first).toEqual(second);
      // Mutating one result must not leak into a later call's result.
      first.push("      - run: extra");
      expect(singleJobLines("repeat", steps)).toEqual(second);
    });

    test("throws a TypeError naming the function when steps is not an array", () => {
      let thrown;
      try {
        singleJobLines("bad", "      - run: echo hi");
      } catch (error) {
        thrown = error;
      }
      expect(thrown).toBeInstanceOf(TypeError);
      expect(thrown.message).toBe(
        "singleJobLines expected steps to be an array of step lines, got string"
      );

      let thrownForUndefined;
      try {
        singleJobLines("bad", undefined);
      } catch (error) {
        thrownForUndefined = error;
      }
      expect(thrownForUndefined).toBeInstanceOf(TypeError);
      expect(thrownForUndefined.message).toBe(
        "singleJobLines expected steps to be an array of step lines, got undefined"
      );
    });
  });

  describe("singleJobWorkflow", () => {
    test("joins the scaffold with embedded newlines and no trailing newline", () => {
      expect(singleJobWorkflow("build", ["      - run: echo hi"])).toBe(
        "jobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hi"
      );
    });

    test('strictly equals singleJobLines(...).join("\\n") for the same inputs', () => {
      const steps = ["      - uses: actions/checkout@v4", "      - run: npm test"];
      const options = {
        runsOn: "[self-hosted, Windows, RAM-64GB]",
        header: ["name: Parity", "on: push", ""],
        jobKeys: ["    timeout-minutes: 45"]
      };
      expect(singleJobWorkflow("parity", steps, options)).toBe(
        singleJobLines("parity", steps, options).join("\n")
      );
    });
  });

  describe("writeWorkflowFile", () => {
    test("joins array content with newlines and appends no trailing newline", () => {
      const root = makeTempDir("workflow-fixtures-array");
      try {
        const file = writeWorkflowFile(root, "test.yml", ["jobs:", "  build:", "    steps: []"]);
        expect(fs.readFileSync(file, "utf8")).toBe("jobs:\n  build:\n    steps: []");
      } finally {
        cleanupDir(root);
      }
    });

    test("writes string content verbatim with no carriage-return normalization", () => {
      const root = makeTempDir("workflow-fixtures-string");
      try {
        const content = "jobs:\r\n  build:\r    steps: []\r\n";
        const file = writeWorkflowFile(root, "crlf.yml", content);
        expect(fs.readFileSync(file, "utf8")).toBe("jobs:\r\n  build:\r    steps: []\r\n");
      } finally {
        cleanupDir(root);
      }
    });

    test("creates nested parent directories for the relative path", () => {
      const root = makeTempDir("workflow-fixtures-nested");
      try {
        const file = writeWorkflowFile(root, ".github/workflows/test.yml", ["jobs: {}"]);
        expect(fs.existsSync(path.join(root, ".github", "workflows"))).toBe(true);
        expect(fs.readFileSync(file, "utf8")).toBe("jobs: {}");
      } finally {
        cleanupDir(root);
      }
    });

    test("returns the absolute path of the written file", () => {
      const root = makeTempDir("workflow-fixtures-abs");
      try {
        const file = writeWorkflowFile(root, ".github/workflows/deep/test.yml", "jobs: {}");
        expect(path.isAbsolute(file)).toBe(true);
        expect(file).toBe(path.join(root, ".github", "workflows", "deep", "test.yml"));
        expect(fs.existsSync(file)).toBe(true);
      } finally {
        cleanupDir(root);
      }
    });
  });
});
