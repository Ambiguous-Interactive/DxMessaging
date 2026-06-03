"use strict";

const path = require("path");

const {
  collectRawMergeVariables,
  collectPwshResultVariables,
  fixSource,
  hasPwshSpawn,
  resolveImportPath
} = require("../fix-pwsh-output-assertions");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const TEST_FILE = path.join(REPO_ROOT, "scripts", "__tests__", "example.test.js");
const LIB_TEST_FILE = path.join(REPO_ROOT, "scripts", "lib", "__tests__", "example.test.js");

const PWSH_SPAWN = 'const result = spawnSync("pwsh", ["-File", script], { encoding: "utf8" });';

describe("fix-pwsh-output-assertions", () => {
  test("detects real PowerShell spawns but ignores fixture strings", () => {
    expect(hasPwshSpawn(PWSH_SPAWN)).toBe(true);
    expect(hasPwshSpawn("const result = spawnSync(`pwsh`, ['-File', script]);")).toBe(true);
    expect(hasPwshSpawn('const result = spawnSync(PWSH, ["-File", script]);')).toBe(true);
    expect(hasPwshSpawn('const result = spawnSync(REAL_PWSH, ["-File", script]);')).toBe(true);
    expect(hasPwshSpawn('const result = spawnSync(pwshPath, ["-File", script]);')).toBe(true);
    expect(hasPwshSpawn('const result = spawnSync(PWSH_PATH, ["-File", script]);')).toBe(true);
    expect(hasPwshSpawn('const fixture = "spawnSync(\\"pwsh\\", [])";')).toBe(false);
    expect(hasPwshSpawn('const result = spawnSync("node", ["x"]);')).toBe(false);
  });

  test("resolves helper import path from both script test roots", () => {
    expect(resolveImportPath(TEST_FILE)).toBe("../lib/pwsh-output");
    expect(resolveImportPath(LIB_TEST_FILE)).toBe("../pwsh-output");
  });

  test("normalizes raw stdout/stderr merge variable assertions without changing the binding", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      PWSH_SPAWN,
      "const text = `${result.stdout}\\n${result.stderr}`;",
      'expect(text).toContain("outside the managed root");',
      'expect(text.split("\\n")[0]).toBe("header");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain('const { normalizePwshText } = require("../lib/pwsh-output");');
    expect(result.source).toContain("const text = `${result.stdout}\\n${result.stderr}`;");
    expect(result.source).toContain(
      'expect(normalizePwshText(text)).toContain("outside the managed root");'
    );
    expect(result.source).toContain('expect(text.split("\\n")[0]).toBe("header");');
  });

  test("normalizes multi-line raw merge variable assertions without changing the binding", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      PWSH_SPAWN,
      "const text =",
      "  `${result.stdout}\\n` +",
      "  `${result.stderr}`;",
      'expect(text).toContain("outside the managed root");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain('const { normalizePwshText } = require("../lib/pwsh-output");');
    expect(result.source).toContain(
      "const text =\n  `${result.stdout}\\n` +\n  `${result.stderr}`;"
    );
    expect(result.source).toContain(
      'expect(normalizePwshText(text)).toContain("outside the managed root");'
    );
  });

  test("normalizes raw stdout alias assertions without changing the binding", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      PWSH_SPAWN,
      'const stdout = result.stdout || "";',
      'expect(stdout).toContain("JSON=Installing Android NDK...");',
      'expect(stdout).toContain("PLAIN=plain two");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain('const { normalizePwshText } = require("../lib/pwsh-output");');
    expect(result.source).toContain('const stdout = result.stdout || "";');
    expect(result.source).toContain(
      'expect(normalizePwshText(stdout)).toContain("JSON=Installing Android NDK...");'
    );
    expect(result.source).toContain(
      'expect(normalizePwshText(stdout)).toContain("PLAIN=plain two");'
    );
  });

  test("does not normalize raw stdout aliases from a non-PowerShell process", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      'const available = spawnSync("pwsh", ["-Command", "exit 0"], { encoding: "utf8" }).status === 0;',
      'const git = spawnSync("git", ["status"], { encoding: "utf8" });',
      'const stdout = git.stdout || "";',
      'expect(stdout).toContain("working tree clean");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(false);
    expect(result.source).toBe(source);
  });

  test("does not normalize raw merge variables from a non-PowerShell process", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      'const available = spawnSync("pwsh", ["-Command", "exit 0"], { encoding: "utf8" }).status === 0;',
      'const git = spawnSync("git", ["status"], { encoding: "utf8" });',
      "const text = `${git.stdout}\\n${git.stderr}`;",
      'expect(text).toContain("working tree clean");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(false);
    expect(result.source).toBe(source);
  });

  test("rewrites results from local helpers that return a PowerShell spawn", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "function runActionScript(script) {",
      '  return spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      "}",
      "",
      "const result = runActionScript(script);",
      'expect(result.stderr).toContain("outside the managed root");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain('const { normalizePwshText } = require("../lib/pwsh-output");');
    expect(result.source).toContain(
      'expect(normalizePwshText(result.stderr || "")).toContain("outside the managed root");'
    );
  });

  test("rewrites parenthesized PowerShell spawn result bindings", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      'const result = (spawnSync("pwsh", ["-File", script], { encoding: "utf8" }));',
      'expect(result.stdout).toContain("outside the managed root");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'expect(stdoutText(result)).toContain("outside the managed root");'
    );
  });

  test("rewrites ternary PowerShell spawn result bindings", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      'const result = usePwsh ? spawnSync("pwsh", ["-File", script], { encoding: "utf8" }) : spawnSync("git", ["status"], { encoding: "utf8" });',
      'expect(result.stderr).toContain("outside the managed root");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'expect(normalizePwshText(result.stderr || "")).toContain("outside the managed root");'
    );
  });

  test("rewrites results from helpers with parenthesized PowerShell returns", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "function runActionScript(script) {",
      '  return (spawnSync("pwsh", ["-File", script], { encoding: "utf8" }));',
      "}",
      "",
      "const result = runActionScript(script);",
      'expect(result.stderr).toContain("outside the managed root");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'expect(normalizePwshText(result.stderr || "")).toContain("outside the managed root");'
    );
  });

  test("rewrites results from local helpers that return a PowerShell spawn inside control flow", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "function runActionScript(script) {",
      "  if (process.env.USE_PWSH) {",
      '    return spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      "  }",
      '  return spawnSync("git", ["status"], { encoding: "utf8" });',
      "}",
      "",
      "const result = runActionScript(script);",
      'expect(result.stderr).toContain("outside the managed root");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'expect(normalizePwshText(result.stderr || "")).toContain("outside the managed root");'
    );
  });

  test("does not trust helpers that probe PowerShell but return another process", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "function runGit() {",
      '  const available = spawnSync("pwsh", ["-Command", "exit 0"], { encoding: "utf8" });',
      '  return spawnSync("git", ["status"], { encoding: "utf8" });',
      "}",
      "",
      "const result = runGit();",
      'expect(result.stdout).toContain("working tree clean");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(false);
    expect(result.source).toBe(source);
  });

  test("does not trust nested helper returns inside a non-PowerShell helper", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "function runGit() {",
      "  function probe() {",
      '    return spawnSync("pwsh", ["-Command", "exit 0"], { encoding: "utf8" });',
      "  }",
      "  probe();",
      '  return spawnSync("git", ["status"], { encoding: "utf8" });',
      "}",
      "",
      "const result = runGit();",
      'expect(result.stdout).toContain("working tree clean");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(false);
    expect(result.source).toBe(source);
  });

  test("does not trust object method returns inside a non-PowerShell helper", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "function runGit() {",
      "  const probe = {",
      "    run() {",
      '      return spawnSync("pwsh", ["-Command", "exit 0"], { encoding: "utf8" });',
      "    }",
      "  };",
      "  probe.run();",
      '  return spawnSync("git", ["status"], { encoding: "utf8" });',
      "}",
      "",
      "const result = runGit();",
      'expect(result.stdout).toContain("working tree clean");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(false);
    expect(result.source).toBe(source);
  });

  test("does not trust quoted computed or numeric method returns inside a non-PowerShell helper", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "function runGit() {",
      "  const probe = {",
      '    "quoted"() {',
      '      return spawnSync("pwsh", ["-Command", "exit 0"], { encoding: "utf8" });',
      "    },",
      '    ["computed"]() {',
      '      return spawnSync("pwsh", ["-Command", "exit 0"], { encoding: "utf8" });',
      "    },",
      "    7() {",
      '      return spawnSync("pwsh", ["-Command", "exit 0"], { encoding: "utf8" });',
      "    }",
      "  };",
      "  probe.quoted();",
      '  return spawnSync("git", ["status"], { encoding: "utf8" });',
      "}",
      "",
      "const result = runGit();",
      'expect(result.stdout).toContain("working tree clean");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(false);
    expect(result.source).toBe(source);
  });

  test("rewrites results from object method helpers that return a PowerShell spawn", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "const runner = {",
      "  run(script) {",
      '    return spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      "  }",
      "};",
      "const result = runner.run(script);",
      'expect(result.stderr).toContain("outside the managed root");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'expect(normalizePwshText(result.stderr || "")).toContain("outside the managed root");'
    );
  });

  test("rewrites nested object method helpers without flattening sibling method names", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "const runner = {",
      "  nested: {",
      "    run(script) {",
      '      return spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      "    }",
      "  },",
      "  run() {",
      '    return spawnSync("git", ["status"], { encoding: "utf8" });',
      "  }",
      "};",
      "const result = runner.nested.run(script);",
      'expect(result.stderr).toContain("outside the managed root");',
      "const git = runner.run();",
      'expect(git.stdout).toContain("working tree clean");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'expect(normalizePwshText(result.stderr || "")).toContain("outside the managed root");'
    );
    expect(result.source).toContain('expect(git.stdout).toContain("working tree clean");');
  });

  test("does not trust expression-bodied nested arrow returns inside a non-PowerShell helper", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "function runGit() {",
      '  return useCallback ? (() => spawnSync("pwsh", ["-Command", "exit 0"], { encoding: "utf8" })) : spawnSync("git", ["status"], { encoding: "utf8" });',
      "}",
      "",
      "const result = runGit();",
      'expect(result.stdout).toContain("working tree clean");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(false);
    expect(result.source).toBe(source);
  });

  test("does not let function parameters inherit an outer PowerShell result binding", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      'const result = spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      "function assertGit(result) {",
      '  expect(result.stdout).toContain("working tree clean");',
      "}",
      'expect(result.stderr).toContain("outside the managed root");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain('expect(result.stdout).toContain("working tree clean");');
    expect(result.source).toContain(
      'expect(normalizePwshText(result.stderr || "")).toContain("outside the managed root");'
    );
  });

  test("does not let a PowerShell helper taint a shadowing non-PowerShell helper", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "function run() {",
      '  return spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      "}",
      "{",
      "  function run() {",
      '    return spawnSync("git", ["status"], { encoding: "utf8" });',
      "  }",
      "  const result = run();",
      '  expect(result.stdout).toContain("working tree clean");',
      "}",
      "const result = run();",
      'expect(result.stderr).toContain("outside the managed root");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain('expect(result.stdout).toContain("working tree clean");');
    expect(result.source).toContain(
      'expect(normalizePwshText(result.stderr || "")).toContain("outside the managed root");'
    );
  });

  test("does not let an object method PowerShell helper taint a shadowing object method", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "const runner = {",
      "  nested: {",
      "    run(script) {",
      '      return spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      "    }",
      "  }",
      "};",
      "{",
      "  const runner = {",
      "    nested: {",
      "      run() {",
      '        return spawnSync("git", ["status"], { encoding: "utf8" });',
      "      }",
      "    }",
      "  };",
      "  const result = runner.nested.run();",
      '  expect(result.stdout).toContain("working tree clean");',
      "}",
      "const result = runner.nested.run(script);",
      'expect(result.stderr).toContain("outside the managed root");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain('expect(result.stdout).toContain("working tree clean");');
    expect(result.source).toContain(
      'expect(normalizePwshText(result.stderr || "")).toContain("outside the managed root");'
    );
  });

  test("does not rewrite unrelated process output in a file that also spawns PowerShell", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      'const available = spawnSync("pwsh", ["-Command", "exit 0"], { encoding: "utf8" }).status === 0;',
      'const git = spawnSync("git", ["status"], { encoding: "utf8" });',
      'expect(git.stdout).toContain("working tree clean");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(false);
    expect(result.source).toBe(source);
  });

  test("does not treat a PowerShell probe in a condition as the expression result", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      'const result = spawnSync("pwsh", ["-Command", "exit 0"], { encoding: "utf8" }).status === 0 ?',
      '  spawnSync("git", ["status"], { encoding: "utf8" }) :',
      '  spawnSync("git", ["rev-parse", "HEAD"], { encoding: "utf8" });',
      'expect(result.stdout).toContain("working tree clean");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(false);
    expect(result.source).toBe(source);
  });

  test("does not let a PowerShell result name taint another scoped binding with the same name", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "test('pwsh case', () => {",
      '  const result = spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      '  expect(result.stderr).toContain("outside the managed root");',
      "});",
      "",
      "test('git case', () => {",
      '  const result = spawnSync("git", ["status"], { encoding: "utf8" });',
      '  expect(result.stdout).toContain("working tree clean");',
      "});",
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'expect(normalizePwshText(result.stderr || "")).toContain("outside the managed root");'
    );
    expect(result.source).toContain('expect(result.stdout).toContain("working tree clean");');
  });

  test("keeps var PowerShell results active after nested block scope", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "if (process.env.USE_PWSH) {",
      '  var result = spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      "}",
      'expect(result.stderr).toContain("outside the managed root");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'expect(normalizePwshText(result.stderr || "")).toContain("outside the managed root");'
    );
  });

  test("keeps var PowerShell results scoped to object method bodies", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "const suite = {",
      "  run() {",
      "    if (process.env.USE_PWSH) {",
      '      var result = spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      "    }",
      '    expect(result.stderr).toContain("outside the managed root");',
      "  }",
      "};",
      'expect(result.stderr).toContain("not in scope here");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'expect(normalizePwshText(result.stderr || "")).toContain("outside the managed root");'
    );
    expect(result.source).toContain('expect(result.stderr).toContain("not in scope here");');
  });

  test("keeps var PowerShell results scoped to quoted computed and numeric method bodies", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "const suite = {",
      '  "quoted"() {',
      '    var quotedResult = spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      '    expect(quotedResult.stderr).toContain("outside the managed root");',
      "  },",
      '  ["computed"]() {',
      '    var computedResult = spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      '    expect(computedResult.stderr).toContain("outside the managed root");',
      "  },",
      "  7() {",
      '    var numericResult = spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      '    expect(numericResult.stderr).toContain("outside the managed root");',
      "  }",
      "};",
      'expect(quotedResult.stderr).toContain("not in scope here");',
      'expect(computedResult.stderr).toContain("not in scope here");',
      'expect(numericResult.stderr).toContain("not in scope here");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'expect(normalizePwshText(quotedResult.stderr || "")).toContain("outside the managed root");'
    );
    expect(result.source).toContain(
      'expect(normalizePwshText(computedResult.stderr || "")).toContain("outside the managed root");'
    );
    expect(result.source).toContain(
      'expect(normalizePwshText(numericResult.stderr || "")).toContain("outside the managed root");'
    );
    expect(result.source).toContain('expect(quotedResult.stderr).toContain("not in scope here");');
    expect(result.source).toContain(
      'expect(computedResult.stderr).toContain("not in scope here");'
    );
    expect(result.source).toContain('expect(numericResult.stderr).toContain("not in scope here");');
  });

  test("fixes later raw stdout assertions in the same run after earlier rewrites shift offsets", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "test('many rewrites', () => {",
      '  const result = spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      "  const first = `${result.stdout}\\n${result.stderr}`;",
      "  const second = `${result.stdout}\\n${result.stderr}`;",
      "  const third = `${result.stdout}\\n${result.stderr}`;",
      "  const fourth = `${result.stdout}\\n${result.stderr}`;",
      '  expect(first).toContain("outside the managed root");',
      '  expect(second).toContain("outside the managed root");',
      '  expect(third).toContain("outside the managed root");',
      '  expect(fourth).toContain("outside the managed root");',
      '  expect(result.stdout).toContain("outside the managed root");',
      "});",
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'expect(normalizePwshText(first)).toContain("outside the managed root");'
    );
    expect(result.source).toContain(
      'expect(normalizePwshText(fourth)).toContain("outside the managed root");'
    );
    expect(result.source).toContain(
      'expect(stdoutText(result)).toContain("outside the managed root");'
    );
  });

  test("lets a block-scoped binding shadow a var PowerShell result", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "if (process.env.USE_PWSH) {",
      '  var result = spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      "}",
      "{",
      '  const result = spawnSync("git", ["status"], { encoding: "utf8" });',
      '  expect(result.stdout).toContain("working tree clean");',
      "}",
      'expect(result.stderr).toContain("outside the managed root");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain('expect(result.stdout).toContain("working tree clean");');
    expect(result.source).toContain(
      'expect(normalizePwshText(result.stderr || "")).toContain("outside the managed root");'
    );
  });

  test("does not let a raw merge variable taint another scoped binding with the same name", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "test('pwsh case', () => {",
      '  const result = spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      "  const text = `${result.stdout}\\n${result.stderr}`;",
      '  expect(text).toContain("outside the managed root");',
      "});",
      "",
      "test('git case', () => {",
      '  const git = spawnSync("git", ["status"], { encoding: "utf8" });',
      "  const text = git.stdout;",
      '  expect(text).toContain("working tree clean");',
      "});",
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'expect(normalizePwshText(text)).toContain("outside the managed root");'
    );
    expect(result.source).toContain('expect(text).toContain("working tree clean");');
  });

  test("does not let a PowerShell helper name taint another scoped function with the same name", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "{",
      "  function run() {",
      '    return spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      "  }",
      "  const result = run();",
      '  expect(result.stderr).toContain("outside the managed root");',
      "}",
      "",
      "{",
      "  function run() {",
      '    return spawnSync("git", ["status"], { encoding: "utf8" });',
      "  }",
      "  const result = run();",
      '  expect(result.stdout).toContain("working tree clean");',
      "}",
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'expect(normalizePwshText(result.stderr || "")).toContain("outside the managed root");'
    );
    expect(result.source).toContain('expect(result.stdout).toContain("working tree clean");');
  });

  test("does not let a PowerShell helper name taint another scoped arrow helper with the same name", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      "{",
      '  const run = () => spawnSync("pwsh", ["-File", script], { encoding: "utf8" });',
      "  const result = run();",
      '  expect(result.stdout).toContain("outside the managed root");',
      "}",
      "",
      "{",
      '  const run = () => spawnSync("git", ["status"], { encoding: "utf8" });',
      "  const result = run();",
      '  expect(result.stdout).toContain("working tree clean");',
      "}",
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'expect(stdoutText(result)).toContain("outside the managed root");'
    );
    expect(result.source).toContain('expect(result.stdout).toContain("working tree clean");');
  });

  test("does not rewrite a raw local merge used only for a single-token assertion", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      PWSH_SPAWN,
      "const text = `${result.stdout}\\n${result.stderr}`;",
      'expect(text).toContain("EXIT=0");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(false);
    expect(result.source).toBe(source);
  });

  test("rewrites direct combined-output expect receivers", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      PWSH_SPAWN,
      'expect(`${result.stdout}\\n${result.stderr}`).toContain("0 tests ran for Zero Tests");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain("expect(combinedText(result)).toContain");
  });

  test("rewrites phrase-variable arguments on raw receivers", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      PWSH_SPAWN,
      'const phrase = "outside the managed root";',
      "expect(result.stdout).toContain(phrase);",
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain("expect(stdoutText(result)).toContain(phrase);");
  });

  test("rewrites direct single-stream template receivers", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      PWSH_SPAWN,
      'expect(`${result.stdout}`).toContain("multi word phrase");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'expect(normalizePwshText(`${result.stdout}`)).toContain("multi word phrase");'
    );
  });

  test("normalizes raw merge helper function definitions", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      PWSH_SPAWN,
      "function merged(out) {",
      "  return `${out.stdout}\\n${out.stderr}`;",
      "}",
      "expect(merged(result)).toContain(\"outside the managed root\");",
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      "return normalizePwshText(`${out.stdout}\\n${out.stderr}`);"
    );
    expect(result.source).toContain('const { normalizePwshText } = require("../lib/pwsh-output");');
  });

  test("normalizes raw merge block-bodied arrow helper definitions", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      PWSH_SPAWN,
      "const merged = (out) => {",
      "  return `${out.stdout}\\n${out.stderr}`;",
      "};",
      "expect(merged(result)).toContain(\"outside the managed root\");",
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      "return normalizePwshText(`${out.stdout}\\n${out.stderr}`);"
    );
  });

  test("normalizes raw merge object method helper definitions", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      PWSH_SPAWN,
      "const helper = {",
      "  merged(out) {",
      "    return `${out.stdout}\\n${out.stderr}`;",
      "  }",
      "};",
      "expect(helper.merged(result)).toContain(\"outside the managed root\");",
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      "return normalizePwshText(`${out.stdout}\\n${out.stderr}`);"
    );
  });

  test("rewrites raw assertions when PowerShell is spawned through a known path variable", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      'const result = spawnSync(PWSH, ["-File", script], { encoding: "utf8" });',
      'expect(result.stderr).toContain("outside the managed root");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain('const { normalizePwshText } = require("../lib/pwsh-output");');
    expect(result.source).toContain(
      'expect(normalizePwshText(result.stderr || "")).toContain("outside the managed root");'
    );
  });

  test("rewrites raw assertions for namespaced child-process PowerShell spawns", () => {
    const source = [
      '"use strict";',
      "",
      'const childProcess = require("child_process");',
      "",
      'const result = childProcess.spawnSync(PWSH, ["-File", script], { encoding: "utf8" });',
      'expect(result.stderr).toContain("outside the managed root");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain('const { normalizePwshText } = require("../lib/pwsh-output");');
    expect(result.source).toContain(
      'expect(normalizePwshText(result.stderr || "")).toContain("outside the managed root");'
    );
  });

  test("collects only variables proven to contain PowerShell spawn results", () => {
    const source = [
      'const direct = spawnSync("pwsh", ["-File", script]);',
      'const indirect = spawnSync(PWSH, ["-File", script]);',
      'const namespaced = childProcess.spawnSync(REAL_PWSH, ["-File", script]);',
      'const git = spawnSync("git", ["status"]);',
      "function runPwsh() { return spawnSync(`pwsh`, ['-File', script]); }",
      "const helper = runPwsh();",
      ""
    ].join("\n");

    const variables = collectPwshResultVariables(source);

    expect(Array.from(variables).sort()).toEqual(["direct", "helper", "indirect", "namespaced"]);
  });

  test("collects raw merge variables separately from their other uses", () => {
    const source = [
      'const result = spawnSync("pwsh", ["-File", script]);',
      "const text = `${result.stdout}\\n${result.stderr}`;",
      'expect(text).toContain("outside the managed root");',
      'expect(text.split("\\n")).toHaveLength(2);',
      ""
    ].join("\n");

    const resultVariables = collectPwshResultVariables(source);
    const rawMergeVariables = collectRawMergeVariables(
      require("../lib/source-stripping").maskCommentsAndStrings(source),
      resultVariables
    );

    expect(Array.from(rawMergeVariables)).toEqual(["text"]);
  });

  test("rewrites raw stdout and stderr phrase receivers to stream-specific normalization", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      PWSH_SPAWN,
      'expect(result.stdout).toContain("provisioning diagnostics files");',
      "expect(result.stderr).toMatch(/outside the managed root/);",
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'const { normalizePwshText, stdoutText } = require("../lib/pwsh-output");'
    );
    expect(result.source).toContain(
      'expect(stdoutText(result)).toContain("provisioning diagnostics files");'
    );
    expect(result.source).toContain('expect(normalizePwshText(result.stderr || "")).toMatch');
  });

  test("does not rewrite raw stdout single-token assertions", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      PWSH_SPAWN,
      'expect(result.stdout).toContain("EXIT=0");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(false);
    expect(result.source).toBe(source);
  });

  test("merges helper names into an existing pwsh-output import", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      'const { assertPwshContains } = require("../lib/pwsh-output");',
      "",
      PWSH_SPAWN,
      'const text = `${result.stdout || ""}\\n${result.stderr || ""}`;',
      'expect(text).toContain("outside the managed root");',
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(true);
    expect(result.source).toContain(
      'const { assertPwshContains, normalizePwshText } = require("../lib/pwsh-output");'
    );
  });

  test("leaves crafted source strings untouched", () => {
    const source = [
      '"use strict";',
      "",
      'const { spawnSync } = require("child_process");',
      "",
      PWSH_SPAWN,
      "const fixture = " +
        JSON.stringify(
          'const text = `${result.stdout}\\n${result.stderr}`; expect(text).toContain("outside the managed root");'
        ) +
        ";",
      "expect(result.status).toBe(1);",
      ""
    ].join("\n");

    const result = fixSource(source, TEST_FILE);

    expect(result.changed).toBe(false);
    expect(result.source).toBe(source);
  });
});
