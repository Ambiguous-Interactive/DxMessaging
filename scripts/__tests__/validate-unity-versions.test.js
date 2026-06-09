/**
 * @fileoverview Tests for scripts/validate-unity-versions.js.
 *
 * Covers the canonical-schema validator, the comment-stripping literal
 * extractor, the per-consumer policy checker, and an end-to-end run of main()
 * (plus a spawned process) against the REAL repository to prove the refactored
 * workflows and the canonical file are mutually consistent.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");

const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");

const {
  loadCanonical,
  validateCanonicalSchema,
  extractVersionLiterals,
  checkConsumer,
  resolveWorkflowPolicy,
  main,
  CONSUMER_POLICIES
} = require("../validate-unity-versions.js");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const VALIDATOR_PATH = path.resolve(__dirname, "../validate-unity-versions.js");

const VALID_CANONICAL = Object.freeze({
  all: ["2021.3.45f1", "2022.3.45f1", "6000.3.16f1"],
  release: "2022.3.45f1"
});

describe("validateCanonicalSchema", () => {
  test("accepts a valid canonical object", () => {
    expect(validateCanonicalSchema(VALID_CANONICAL)).toEqual([]);
  });

  test.each([
    ["null root", null],
    ["array root", ["2021.3.45f1"]],
    ["string root", "2021.3.45f1"]
  ])("rejects %s", (_name, data) => {
    const errors = validateCanonicalSchema(data);
    expect(errors.length).toBeGreaterThan(0);
  });

  test("rejects an empty all array", () => {
    const errors = validateCanonicalSchema({ all: [], release: "2022.3.45f1" });
    expect(errors.length).toBeGreaterThan(0);
    expect(errors.join("\n")).toMatch(/non-empty array/);
  });

  test("rejects a non-array all", () => {
    const errors = validateCanonicalSchema({ all: "2021.3.45f1", release: "2021.3.45f1" });
    expect(errors.length).toBeGreaterThan(0);
  });

  test("rejects an all entry that is not a string", () => {
    const errors = validateCanonicalSchema({ all: [20213, "2022.3.45f1"], release: "2022.3.45f1" });
    expect(errors.length).toBeGreaterThan(0);
    expect(errors.join("\n")).toMatch(/must be a string/);
  });

  test("rejects a badly formatted version literal", () => {
    const errors = validateCanonicalSchema({
      all: ["2021.3", "2022.3.45f1"],
      release: "2022.3.45f1"
    });
    expect(errors.length).toBeGreaterThan(0);
    expect(errors.join("\n")).toMatch(/not a valid Unity version/);
  });

  test("rejects a non-ascending sequence", () => {
    const errors = validateCanonicalSchema({
      all: ["2022.3.45f1", "2021.3.45f1"],
      release: "2021.3.45f1"
    });
    expect(errors.length).toBeGreaterThan(0);
    expect(errors.join("\n")).toMatch(/strictly ascending/);
  });

  test("rejects equal-triple (non-strict) sequence", () => {
    const errors = validateCanonicalSchema({
      all: ["2022.3.45f1", "2022.3.45p2"],
      release: "2022.3.45f1"
    });
    expect(errors.length).toBeGreaterThan(0);
    expect(errors.join("\n")).toMatch(/strictly ascending/);
  });

  test("rejects duplicate entries", () => {
    const errors = validateCanonicalSchema({
      all: ["2021.3.45f1", "2021.3.45f1"],
      release: "2021.3.45f1"
    });
    expect(errors.length).toBeGreaterThan(0);
    expect(errors.join("\n")).toMatch(/duplicate/);
  });

  test("rejects release not in all", () => {
    const errors = validateCanonicalSchema({
      all: ["2021.3.45f1", "2022.3.45f1"],
      release: "6000.3.16f1"
    });
    expect(errors.length).toBeGreaterThan(0);
    expect(errors.join("\n")).toMatch(/must be a member of `all`/);
  });

  test("rejects missing release key", () => {
    const errors = validateCanonicalSchema({ all: ["2021.3.45f1"] });
    expect(errors.length).toBeGreaterThan(0);
    expect(errors.join("\n")).toMatch(/`release` must be a string/);
  });

  test("rejects missing all key", () => {
    const errors = validateCanonicalSchema({ release: "2022.3.45f1" });
    expect(errors.length).toBeGreaterThan(0);
  });
});

describe("extractVersionLiterals", () => {
  test("strips a YAML inline comment so a commented version yields no literal", () => {
    const content = "      # uses 6000.3.16f1 in the comment\n      key: value";
    expect(extractVersionLiterals(content, ".yml")).toEqual([]);
  });

  test("extracts a quoted literal from a YAML default", () => {
    const content = '        default: "2022.3.45f1"';
    const literals = extractVersionLiterals(content, ".yml");
    expect(literals).toEqual([{ version: "2022.3.45f1", line: 1 }]);
  });

  test("extracts multiple literals from a single line", () => {
    const content = "versions=['2021.3.45f1','2022.3.45f1','6000.3.16f1']";
    const literals = extractVersionLiterals(content, ".yml");
    expect(literals.map((l) => l.version)).toEqual([
      "2021.3.45f1",
      "2022.3.45f1",
      "6000.3.16f1"
    ]);
  });

  test("strips a PowerShell inline comment", () => {
    const content = "$x = 1  # default 2022.3.45f1";
    expect(extractVersionLiterals(content, ".ps1")).toEqual([]);
  });

  test("extracts PowerShell array literals with correct line numbers", () => {
    const content = ["param(", "  $UnityVersions = @('2021.3.45f1', '6000.3.16f1')", ")"].join(
      "\n"
    );
    const literals = extractVersionLiterals(content, ".ps1");
    expect(literals).toEqual([
      { version: "2021.3.45f1", line: 2 },
      { version: "6000.3.16f1", line: 2 }
    ]);
  });

  test("strips a shell (.sh) inline comment so a commented version yields no literal", () => {
    const content = "UNITY_VERSION_DEFAULT=fallback  # was 2022.3.45f1";
    expect(extractVersionLiterals(content, ".sh")).toEqual([]);
  });

  test("extracts a shell (.sh) code default before any inline comment", () => {
    const content = 'UNITY_VERSION_DEFAULT="${UNITY_VERSION:-2022.3.45f1}"  # default';
    const literals = extractVersionLiterals(content, ".sh");
    expect(literals).toEqual([{ version: "2022.3.45f1", line: 1 }]);
  });
});

describe("checkConsumer", () => {
  const all = VALID_CANONICAL.all;
  const release = VALID_CANONICAL.release;

  describe("no-literals", () => {
    test("passes when no literal is present", () => {
      const violations = checkConsumer({
        relativePath: ".github/workflows/perf-numbers.yml",
        policy: "no-literals",
        literals: [],
        all,
        release
      });
      expect(violations).toEqual([]);
    });

    test("flags an injected literal", () => {
      const violations = checkConsumer({
        relativePath: ".github/workflows/perf-numbers.yml",
        policy: "no-literals",
        literals: [{ version: "6000.3.16f1", line: 42 }],
        all,
        release
      });
      expect(violations).toHaveLength(1);
      expect(violations[0]).toContain(".github/workflows/perf-numbers.yml:42");
      expect(violations[0]).toContain("expected NONE");
    });
  });

  describe("mirror-all", () => {
    test("passes when the literal set equals all exactly", () => {
      const literals = all.map((version, index) => ({ version, line: index + 1 }));
      const violations = checkConsumer({
        relativePath: ".github/workflows/runner-bootstrap.yml",
        policy: "mirror-all",
        literals,
        all,
        release
      });
      expect(violations).toEqual([]);
    });

    test("flags a missing version", () => {
      const literals = [
        { version: "2021.3.45f1", line: 1 },
        { version: "2022.3.45f1", line: 1 }
      ];
      const violations = checkConsumer({
        relativePath: ".github/workflows/runner-bootstrap.yml",
        policy: "mirror-all",
        literals,
        all,
        release
      });
      expect(violations).toHaveLength(1);
      expect(violations[0]).toContain("6000.3.16f1");
      expect(violations[0]).toContain("missing");
    });

    test("flags an out-of-set (extra) version", () => {
      const literals = [
        { version: "2021.3.45f1", line: 1 },
        { version: "2022.3.45f1", line: 2 },
        { version: "6000.3.16f1", line: 3 },
        { version: "2020.3.1f1", line: 4 }
      ];
      const violations = checkConsumer({
        relativePath: "scripts/unity/maintain-windows-runner.ps1",
        policy: "mirror-all",
        literals,
        all,
        release
      });
      expect(violations).toHaveLength(1);
      expect(violations[0]).toContain("2020.3.1f1");
      expect(violations[0]).toContain("not in canonical");
    });
  });

  describe("mirror-release", () => {
    test("passes when every literal equals release", () => {
      const violations = checkConsumer({
        relativePath: ".github/workflows/release.yml",
        policy: "mirror-release",
        literals: [
          { version: "2022.3.45f1", line: 10 },
          { version: "2022.3.45f1", line: 20 }
        ],
        all,
        release
      });
      expect(violations).toEqual([]);
    });

    test("flags a non-release literal", () => {
      const violations = checkConsumer({
        relativePath: ".github/workflows/release.yml",
        policy: "mirror-release",
        literals: [{ version: "6000.3.16f1", line: 12 }],
        all,
        release
      });
      expect(violations).toHaveLength(1);
      expect(violations[0]).toContain("does not match canonical release");
      expect(violations[0]).toContain("2022.3.45f1");
    });

    test("flags absence of any literal", () => {
      const violations = checkConsumer({
        relativePath: ".github/workflows/unity-gameci-experiment.yml",
        policy: "mirror-release",
        literals: [],
        all,
        release
      });
      expect(violations).toHaveLength(1);
      expect(violations[0]).toContain("no Unity version literal found");
    });

    test("flags a .sh code literal != release while the #-commented version is ignored", () => {
      // The comment carries the correct release (would pass if scanned), but the
      // code default is a non-release version. Comment stripping must IGNORE the
      // comment and the code literal must be the ONLY thing flagged.
      const content = [
        'UNITY_VERSION_DEFAULT="${UNITY_VERSION:-6000.3.16f1}"  # release is 2022.3.45f1'
      ].join("\n");
      const literals = extractVersionLiterals(content, ".sh");
      expect(literals).toEqual([{ version: "6000.3.16f1", line: 1 }]);
      const violations = checkConsumer({
        relativePath: "scripts/unity/run-tests.sh",
        policy: "mirror-release",
        literals,
        all,
        release
      });
      expect(violations).toHaveLength(1);
      expect(violations[0]).toContain("does not match canonical release");
      expect(violations[0]).toContain(release);
    });

    test("a .ps1 fixture whose only code literal is release passes", () => {
      const content = "    $UnityVersion = '2022.3.45f1'  # default";
      const literals = extractVersionLiterals(content, ".ps1");
      const violations = checkConsumer({
        relativePath: "scripts/unity/run-tests.ps1",
        policy: "mirror-release",
        literals,
        all,
        release
      });
      expect(violations).toEqual([]);
    });

    test("a .sh fixture whose only code literal is release passes", () => {
      const content = 'UNITY_VERSION_DEFAULT="${UNITY_VERSION:-2022.3.45f1}"';
      const literals = extractVersionLiterals(content, ".sh");
      const violations = checkConsumer({
        relativePath: "scripts/unity/run-tests.sh",
        policy: "mirror-release",
        literals,
        all,
        release
      });
      expect(violations).toEqual([]);
    });
  });
});

describe("resolveWorkflowPolicy", () => {
  test("returns the explicit policy for a listed file", () => {
    expect(resolveWorkflowPolicy(".github/workflows/release.yml")).toBe("mirror-release");
    expect(resolveWorkflowPolicy(".github/workflows/runner-bootstrap.yml")).toBe("mirror-all");
  });

  test("defaults an unlisted active workflow to no-literals", () => {
    expect(resolveWorkflowPolicy(".github/workflows/some-new-workflow.yml")).toBe("no-literals");
  });

  test("returns the explicit policy for a listed non-workflow consumer", () => {
    // CONSUMER_POLICIES is consulted first, so a listed .ps1 keeps its policy.
    expect(resolveWorkflowPolicy("scripts/unity/maintain-windows-runner.ps1")).toBe("mirror-all");
  });

  test("returns null for an unlisted non-workflow path", () => {
    expect(resolveWorkflowPolicy(".github/unity-versions.json")).toBeNull();
    expect(resolveWorkflowPolicy("scripts/unity/some-other-script.ps1")).toBeNull();
  });
});

describe("CONSUMER_POLICIES", () => {
  test("declares the documented consumer set", () => {
    expect(CONSUMER_POLICIES).toEqual(
      expect.objectContaining({
        ".github/workflows/perf-numbers.yml": "no-literals",
        ".github/workflows/unity-tests.yml": "no-literals",
        ".github/workflows/unity-benchmarks.yml": "no-literals",
        ".github/workflows/runner-bootstrap.yml": "mirror-all",
        "scripts/unity/maintain-windows-runner.ps1": "mirror-all",
        "scripts/unity/install-runner-maintenance-task.ps1": "mirror-all",
        ".github/workflows/release.yml": "mirror-release",
        ".github/workflows/unity-gameci-experiment.yml": "mirror-release"
      })
    );
  });
});

describe("loadCanonical (real repo)", () => {
  test("loads and parses the real canonical file", () => {
    const { data } = loadCanonical(REPO_ROOT);
    expect(Array.isArray(data.all)).toBe(true);
    expect(data.all.length).toBeGreaterThan(0);
    expect(data.all).toContain(data.release);
  });
});

describe("main (real repo integration)", () => {
  test("main() returns 0 against the real repository", () => {
    const logs = [];
    const errors = [];
    const code = main({
      repoRoot: REPO_ROOT,
      log: (message) => logs.push(String(message)),
      errorLog: (message) => errors.push(String(message))
    });
    expect(errors).toEqual([]);
    expect(code).toBe(0);
    expect(logs.join("\n")).toMatch(/single-source check passed/);
  });

  test("spawning the validator exits 0 against the real repository", () => {
    const result = childProcess.spawnSync(process.execPath, [VALIDATOR_PATH], {
      cwd: REPO_ROOT,
      encoding: "utf8"
    });
    expect(result.status).toBe(0);
    expect(result.stdout).toMatch(/single-source check passed/);
  });
});

describe("main (end-to-end negative against a fixture repo)", () => {
  // Scratch dirs are created INSIDE the repo (repo policy forbids os.tmpdir()
  // fixtures), under a gitignored `dxm-*` prefix, and removed in a finally per
  // test plus a defensive afterAll so validate:untracked-policy is never tripped.
  const createdDirs = [];

  function makeFixtureRepo(canonicalJsonText) {
    const dir = makeTempDir("uv-test", { root: REPO_ROOT, prefix: "dxm-" });
    createdDirs.push(dir);
    fs.mkdirSync(path.join(dir, ".github"), { recursive: true });
    fs.writeFileSync(path.join(dir, ".github", "unity-versions.json"), canonicalJsonText, "utf8");
    return dir;
  }

  afterAll(() => {
    for (const dir of createdDirs) {
      cleanupDir(dir);
    }
  });

  test("returns 1 when the canonical file has release not in all (schema violation)", () => {
    const dir = makeFixtureRepo(
      JSON.stringify({ all: ["2021.3.45f1", "2022.3.45f1"], release: "6000.3.16f1" })
    );
    try {
      const code = main({ repoRoot: dir, log: () => {}, errorLog: () => {} });
      expect(code).toBe(1);
    } finally {
      cleanupDir(dir);
    }
  });

  test("returns 1 when the canonical file is invalid JSON", () => {
    const dir = makeFixtureRepo("{ this is not valid json ");
    try {
      const code = main({ repoRoot: dir, log: () => {}, errorLog: () => {} });
      expect(code).toBe(1);
    } finally {
      cleanupDir(dir);
    }
  });
});
