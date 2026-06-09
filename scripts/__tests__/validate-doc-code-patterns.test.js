/**
 * @fileoverview Tests for scripts/validate-doc-code-patterns.js.
 *
 * Drives the validator as a child process against fixture files and asserts
 * exit code + stderr match the documented contract. Coverage focuses on the
 * struct-emit-temporary rule because the textual lint is the canonical defense
 * for the "new X().Emit()" bug class -- the Roslyn compilation harness cannot
 * reliably catch it (stub setup produces CS1510 which must stay ignored).
 */

"use strict";

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");

const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");

const VALIDATOR_SCRIPT_PATH = path.resolve(__dirname, "../validate-doc-code-patterns.js");
const REPO_ROOT = path.resolve(__dirname, "../..");

function runValidator(filePath) {
  return childProcess.spawnSync(process.execPath, [VALIDATOR_SCRIPT_PATH, "--paths", filePath], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });
}

function withFixture(suffix, contents, callback) {
  const tempDir = makeTempDir("doc-code-patterns");
  const filePath = path.join(tempDir, `fixture${suffix}`);
  try {
    fs.writeFileSync(filePath, contents, "utf8");
    callback(filePath);
  } finally {
    cleanupDir(tempDir);
  }
}

describe("validate-doc-code-patterns", () => {
  describe("struct-emit-temporary rule", () => {
    test("flags bare 'new X().Emit()' form", () => {
      withFixture(".md", "- `new Foo().Emit()`\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(1);
        expect(result.stderr).toContain("struct-emit-temporary");
        expect(result.stderr).toContain("new Foo().Emit(");
      });
    });

    test("flags parenthesized '(new X()).Emit()' form (the previously-missed case)", () => {
      withFixture(".md", "- `(new Foo()).Emit()`\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(1);
        expect(result.stderr).toContain("struct-emit-temporary");
        expect(result.stderr).toContain("(new Foo()).Emit(");
      });
    });

    test("flags 'new X().EmitTargeted(target)' shorthand", () => {
      withFixture(".md", "- `new Foo().EmitTargeted(target)`\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(1);
        expect(result.stderr).toContain("struct-emit-temporary");
        expect(result.stderr).toContain("EmitTargeted(");
      });
    });

    test("flags namespaced 'new Ns.X().Emit()' form", () => {
      withFixture(".md", "- `new MyNs.Foo().Emit()`\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(1);
        expect(result.stderr).toContain("struct-emit-temporary");
        expect(result.stderr).toContain("new MyNs.Foo().Emit(");
      });
    });

    test("flags whitespace-variant 'new X () . Emit ( )'", () => {
      withFixture(".md", "- `new Foo () . Emit ( )`\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(1);
        expect(result.stderr).toContain("struct-emit-temporary");
      });
    });

    test("flags multi-arg constructor 'new X(a, b).Emit()'", () => {
      withFixture(".md", "- `new Foo(arg1, arg2).Emit()`\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(1);
        expect(result.stderr).toContain("struct-emit-temporary");
      });
    });

    test("does NOT flag the correct 'var msg = new X(); msg.Emit();' pattern", () => {
      const fixture = ["```csharp", "var msg = new Foo();", "msg.Emit();", "```", ""].join("\n");
      withFixture(".md", fixture, (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(0);
        expect(result.stdout).toContain("0 violations");
      });
    });

    test("does NOT flag 'someMethod(new X()).Emit()' (no false positive)", () => {
      withFixture(".md", "- `someMethod(new Foo()).Emit()`\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(0);
        expect(result.stdout).toContain("0 violations");
      });
    });

    test("counter-example marker 'won't compile' suppresses match", () => {
      withFixture(".md", "- `new Foo().Emit()` won't compile.\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(0);
        expect(result.stdout).toContain("0 violations");
      });
    });

    test("counter-example marker 'will not compile' suppresses match", () => {
      withFixture(".md", "- `new Foo().Emit()` -- will not compile.\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(0);
      });
    });

    test("counter-example marker 'does not compile' suppresses match", () => {
      withFixture(".md", "- `new Foo().Emit()` does not compile.\n", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(0);
      });
    });
  });

  describe("baseline behavior", () => {
    test("empty file exits 0", () => {
      withFixture(".md", "", (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(0);
        expect(result.stdout).toContain("0 violations");
      });
    });

    test("file with only prose and no violations exits 0", () => {
      const fixture = [
        "# Heading",
        "",
        "Plain prose with no offending patterns.",
        "",
        "```csharp",
        "var x = 1;",
        "Console.WriteLine(x);",
        "```",
        ""
      ].join("\n");
      withFixture(".md", fixture, (filePath) => {
        const result = runValidator(filePath);
        expect(result.status).toBe(0);
      });
    });
  });

  describe("CLI surface", () => {
    test("--list-rules prints the configured catalog and exits 0", () => {
      const result = childProcess.spawnSync(
        process.execPath,
        [VALIDATOR_SCRIPT_PATH, "--list-rules"],
        { cwd: REPO_ROOT, encoding: "utf8" }
      );
      expect(result.status).toBe(0);
      expect(result.stdout).toContain("struct-emit-temporary");
      expect(result.stdout).toContain("Configured rules:");
    });
  });
});

describe("validate-doc-code-patterns module exports", () => {
  const { BANNED_PATTERNS, isCounterExampleLine } = require("../validate-doc-code-patterns.js");

  test("BANNED_PATTERNS contains struct-emit-temporary", () => {
    const ids = BANNED_PATTERNS.map((rule) => rule.id);
    expect(ids).toContain("struct-emit-temporary");
  });

  test("isCounterExampleLine detects all documented marker phrases", () => {
    expect(isCounterExampleLine("// won't compile")).toBe(true);
    expect(isCounterExampleLine("does not compile")).toBe(true);
    expect(isCounterExampleLine("will not compile")).toBe(true);
    expect(isCounterExampleLine("do not compile")).toBe(true);
    expect(isCounterExampleLine("fails to compile")).toBe(true);
    expect(isCounterExampleLine("regular prose")).toBe(false);
  });
});
