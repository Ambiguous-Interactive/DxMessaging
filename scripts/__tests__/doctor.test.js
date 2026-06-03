/**
 * @fileoverview Tests for scripts/doctor.js -- the agentic preflight gate.
 *
 * Each section function in doctor.js accepts a dependency-injection options
 * bag so these tests can drive every probe with deterministic fakes
 * (synthetic fs, synthetic spawnSync, synthetic YAML). Real fs/child_process
 * are not touched.
 */

"use strict";

const path = require("path");

const {
  STALE_CACHE_DAYS,
  STALE_CACHE_MS,
  KNOWN_STATUSES,
  aggregateStatus,
  statusRank,
  statusToExitCode,
  checkNodeModulesFreshness,
  checkIsolatedJestCache,
  checkEolPolicy,
  checkPreCommitConfig,
  checkHookPerfBudget,
  checkCrossPlatformSanity,
  checkWorkingTreeState,
  checkChangedDocumentation,
  probeToolVersions,
  runDoctor,
  shouldUseColor,
  decorateStatusLabel,
  formatHeaderBanner,
  formatSection,
  formatFooter,
  main
} = require("../doctor");

const precommitYaml = require("../lib/precommit-yaml");
const precommitPerfScore = require("../lib/precommit-perf-score");

function noopRequire() {
  return {};
}

describe("doctor.js status helpers", () => {
  test("statusRank orders fail > warn > ok", () => {
    expect(statusRank("fail")).toBeGreaterThan(statusRank("warn"));
    expect(statusRank("warn")).toBeGreaterThan(statusRank("ok"));
  });

  test("aggregateStatus returns the worst section status", () => {
    const swallow = () => {};
    expect(aggregateStatus([{ status: "ok" }, { status: "ok" }], { warn: swallow })).toBe("ok");
    expect(aggregateStatus([{ status: "ok" }, { status: "warn" }], { warn: swallow })).toBe("warn");
    expect(aggregateStatus([{ status: "warn" }, { status: "fail" }], { warn: swallow })).toBe(
      "fail"
    );
    expect(aggregateStatus([], { warn: swallow })).toBe("ok");
  });

  test("aggregateStatus treats unrecognized statuses as fail and logs a warning (M4)", () => {
    const warnings = [];
    const result = aggregateStatus(
      [
        { name: "weird", status: "maybe" },
        { name: "fine", status: "ok" }
      ],
      { warn: (msg) => warnings.push(msg) }
    );
    expect(result).toBe("fail");
    expect(warnings.length).toBe(1);
    expect(warnings[0]).toContain("'weird'");
    expect(warnings[0]).toContain("'maybe'");
    expect(warnings[0]).toContain("treating as 'fail'");
  });

  test("aggregateStatus accepts a default warn sink (smoke test, swallow stderr)", () => {
    // No explicit warn override -> the function reaches for the default
    // sink (process.stderr.write). We do not assert on stderr; we only
    // care that the call still returns the fail-safe status.
    const originalWrite = process.stderr.write;
    process.stderr.write = () => true;
    try {
      const result = aggregateStatus([{ name: "x", status: "unknown" }]);
      expect(result).toBe("fail");
    } finally {
      process.stderr.write = originalWrite;
    }
  });

  test("statusToExitCode returns 1 only for fail", () => {
    expect(statusToExitCode("ok")).toBe(0);
    expect(statusToExitCode("warn")).toBe(0);
    expect(statusToExitCode("fail")).toBe(1);
  });

  test("STALE_CACHE_MS matches STALE_CACHE_DAYS", () => {
    expect(STALE_CACHE_MS).toBe(STALE_CACHE_DAYS * 24 * 60 * 60 * 1000);
  });
});

describe("checkNodeModulesFreshness", () => {
  // Step 10: checkNodeModulesFreshness now consumes probeIntegrity from
  // scripts/lib/node-modules-integrity.js as the file-existence + size
  // layer. Tests inject a no-op `probeIntegrityFn` so the existing fake-fs
  // fixtures remain authoritative for the legacy `requiredFiles` path.
  const noopProbeIntegrity = () => ({ ok: true, missing: [] });

  test("returns ok when every tool resolves, exists, and loads", () => {
    const toolSpecs = [
      {
        name: "prettier",
        requiredFiles: ["node_modules/prettier/index.cjs"],
        load: "require",
        entry: "node_modules/prettier/index.cjs"
      },
      {
        name: "markdownlint-cli2",
        requiredFiles: ["node_modules/markdownlint-cli2/markdownlint-cli2.mjs"],
        load: "import",
        entry: "node_modules/markdownlint-cli2/markdownlint-cli2.mjs"
      },
      {
        name: "cspell",
        requiredFiles: ["node_modules/cspell/bin.mjs"]
      },
      {
        name: "jest-circus",
        requiredFiles: [],
        load: "resolve",
        entry: "jest-circus/runner"
      }
    ];

    const section = checkNodeModulesFreshness({
      probeIntegrityFn: noopProbeIntegrity,
      toolSpecs,
      existsSyncFn: () => true,
      requireResolveFn: () => "/repo/node_modules/jest-circus/build/runner.js",
      requireFn: noopRequire,
      repoRoot: "/repo"
    });

    expect(section.name).toBe("node_modules freshness");
    expect(section.status).toBe("ok");
    expect(section.lines.some((line) => line.includes("ok    prettier"))).toBe(true);
    expect(section.lines.some((line) => line.includes("ok    jest-circus"))).toBe(true);
  });

  test("returns fail when one tool's required file is missing", () => {
    const toolSpecs = [
      {
        name: "prettier",
        requiredFiles: ["node_modules/prettier/index.cjs"],
        load: "require",
        entry: "node_modules/prettier/index.cjs"
      },
      {
        name: "missing-tool",
        requiredFiles: ["node_modules/missing-tool/bin.js"]
      }
    ];

    const section = checkNodeModulesFreshness({
      probeIntegrityFn: noopProbeIntegrity,
      toolSpecs,
      existsSyncFn: (absPath) => !absPath.includes("missing-tool"),
      requireFn: noopRequire,
      requireResolveFn: () => "/repo/node_modules/jest-circus/build/runner.js",
      repoRoot: "/repo"
    });

    expect(section.status).toBe("fail");
    const text = section.lines.join("\n");
    expect(text).toContain("FAIL  missing-tool");
    expect(text).toContain("missing required file");
    expect(text).toContain("npm ci");
  });

  test("returns fail when require throws", () => {
    const toolSpecs = [
      {
        name: "prettier",
        requiredFiles: ["node_modules/prettier/index.cjs"],
        load: "require",
        entry: "node_modules/prettier/index.cjs"
      }
    ];

    const section = checkNodeModulesFreshness({
      probeIntegrityFn: noopProbeIntegrity,
      toolSpecs,
      existsSyncFn: () => true,
      requireFn: () => {
        throw new Error("transitive dep broken");
      },
      requireResolveFn: () => "/path",
      repoRoot: "/repo"
    });

    expect(section.status).toBe("fail");
    expect(section.lines.join("\n")).toContain("transitive dep broken");
  });

  test("handles tool-specs without load/entry (m5: cspell, jest bare)", () => {
    // A spec that only has requiredFiles (no load mode, no entry) should
    // still succeed when those files exist. This is the cspell/jest-bare
    // shape -- the spec contributes a presence check only.
    const toolSpecs = [
      {
        name: "cspell",
        requiredFiles: ["node_modules/cspell/bin.mjs"]
      },
      {
        name: "jest",
        requiredFiles: ["node_modules/jest/package.json", "node_modules/jest/bin/jest.js"]
      }
    ];

    const section = checkNodeModulesFreshness({
      probeIntegrityFn: noopProbeIntegrity,
      toolSpecs,
      existsSyncFn: () => true,
      requireFn: noopRequire,
      requireResolveFn: () => "/should/not/be/called",
      repoRoot: "/repo"
    });

    expect(section.status).toBe("ok");
    const text = section.lines.join("\n");
    expect(text).toContain("ok    cspell");
    expect(text).toContain("ok    jest");
    // No resolve/required/loaded lines because there is no entry.
    expect(text).not.toContain("resolved:");
    expect(text).not.toContain("required:");
    expect(text).not.toContain("loaded:");
  });

  test("returns fail when resolve-mode lookup throws", () => {
    const toolSpecs = [
      {
        name: "jest-circus",
        requiredFiles: [],
        load: "resolve",
        entry: "jest-circus/runner"
      }
    ];

    const section = checkNodeModulesFreshness({
      probeIntegrityFn: noopProbeIntegrity,
      toolSpecs,
      existsSyncFn: () => true,
      requireFn: noopRequire,
      requireResolveFn: () => {
        throw new Error("Cannot find module 'jest-circus/runner'");
      },
      repoRoot: "/repo"
    });

    expect(section.status).toBe("fail");
    expect(section.lines.join("\n")).toContain("Cannot find module");
  });
});

describe("checkIsolatedJestCache", () => {
  // size defaults to non-zero so a healthy resolved runner passes the new
  // zero-byte (empty-file) check; tests that exercise the empty-runner path
  // override statSyncFn to return { size: 0 } for the runner path explicitly.
  function makeStat(mtimeMs, size = 1024) {
    return { mtimeMs, size };
  }

  test("returns ok with no-cache message when cache root does not exist", () => {
    const section = checkIsolatedJestCache({
      existsSyncFn: () => false,
      readdirSyncFn: () => {
        throw new Error("should not read when root missing");
      },
      statSyncFn: () => {
        throw new Error("should not stat");
      },
      cacheRoot: "/tmp/dxmessaging-managed-jest",
      nowMs: 1_700_000_000_000
    });

    expect(section.status).toBe("ok");
    const text = section.lines.join("\n");
    expect(text).toContain("No isolated managed-Jest cache yet");
    // Zero manual touch: the doctor no longer prints a manual rm one-liner for
    // a regenerable artifact. The bootstrap auto-clears it.
    expect(text).not.toContain("Manual reset");
  });

  test("returns ok when cache root exists but is empty", () => {
    const section = checkIsolatedJestCache({
      existsSyncFn: () => true,
      readdirSyncFn: () => [],
      statSyncFn: () => makeStat(0),
      createRequireFn: () => ({ resolve: () => "/never" }),
      cacheRoot: "/tmp/dxmessaging-managed-jest",
      nowMs: 1_700_000_000_000
    });

    expect(section.status).toBe("ok");
    expect(section.lines.join("\n")).toContain("contains no install dirs");
  });

  test("returns warn for stale entries (>30 days mtime)", () => {
    const now = 1_700_000_000_000;
    const fortyDaysAgo = now - 40 * 24 * 60 * 60 * 1000;

    // existsSyncFn must return true for the cache root, the package.json
    // inside the install dir, and the resolved runner path.
    const existsSyncFn = jest.fn(() => true);
    const readdirSyncFn = () => [{ name: "jest_30.3.0", isDirectory: () => true }];
    const statSyncFn = () => makeStat(fortyDaysAgo);
    const createRequireFn = () => ({
      resolve: () =>
        "/tmp/dxmessaging-managed-jest/jest_30.3.0/node_modules/jest-circus/build/runner.js"
    });

    const section = checkIsolatedJestCache({
      existsSyncFn,
      readdirSyncFn,
      statSyncFn,
      createRequireFn,
      cacheRoot: "/tmp/dxmessaging-managed-jest",
      nowMs: now
    });

    expect(section.status).toBe("warn");
    const text = section.lines.join("\n");
    expect(text).toContain("STALE");
    expect(text).toContain("age=40d");
  });

  test("returns WARN (not fail) when jest-circus/runner cannot be resolved (regenerable corruption)", () => {
    // SEVERITY CONTRACT: a corrupt/partial REGENERABLE isolated cache is never
    // a push-blocking FAIL. The bootstrap (repair-node-tooling) auto-clears it
    // before the doctor runs; the doctor reports WARN with NO manual command.
    const now = 1_700_000_000_000;
    const recentMs = now - 1000;

    const section = checkIsolatedJestCache({
      existsSyncFn: () => true,
      readdirSyncFn: () => [{ name: "jest_30.3.0", isDirectory: () => true }],
      statSyncFn: () => makeStat(recentMs),
      createRequireFn: () => ({
        resolve: () => {
          throw new Error("Cannot find module 'jest-circus/runner'");
        }
      }),
      // Pin the relevance gate so the status is host-independent (warn either
      // way; gate only toggles the info line).
      hasHealthyLocalJestInstallFn: () => false,
      cacheRoot: "/tmp/dxmessaging-managed-jest",
      nowMs: now
    });

    expect(section.status).toBe("warn");
    const text = section.lines.join("\n");
    expect(text).toContain("Cannot find module 'jest-circus/runner'");
    // The diagnostic still prints, but with zero manual recourse.
    expect(text).not.toContain("Manual reset");
  });

  test("caps at WARN (never fail) when local Jest is healthy even if the isolated runner is missing", () => {
    // Relevance gate (pre-push.txt proof): when local node_modules Jest is
    // healthy the fallback is PROVABLY never consulted, so a corrupt fallback
    // cache is purely informational -- it must not block the push.
    const now = 1_700_000_000_000;
    const section = checkIsolatedJestCache({
      existsSyncFn: () => true,
      readdirSyncFn: () => [{ name: "jest_30.3.0", isDirectory: () => true }],
      statSyncFn: () => makeStat(now - 1000),
      createRequireFn: () => ({
        resolve: () => {
          throw new Error("Cannot find module 'jest-circus/runner'");
        }
      }),
      hasHealthyLocalJestInstallFn: () => true,
      cacheRoot: "/tmp/dxmessaging-managed-jest",
      nowMs: now
    });

    expect(section.status).toBe("warn");
    expect(section.lines.join("\n")).toContain(
      "Local node_modules Jest is healthy; this fallback cache is not consulted"
    );
  });

  test("returns fail when readdirSync throws EACCES AND local Jest is unhealthy (m4)", () => {
    // Simulates a read failure mid-flight (EACCES, EIO, etc.) on the cache ROOT.
    // This is a genuine HOST read-error, not regenerable corruption, and not
    // auto-deletable. It stays FAIL ONLY when local Jest is ALSO unhealthy --
    // then the fallback could actually be consulted, so an unreadable root is a
    // real blocker. The section must surface the underlying error message and
    // must not silently treat the cache as empty.
    const section = checkIsolatedJestCache({
      existsSyncFn: () => true,
      readdirSyncFn: () => {
        const err = new Error("EACCES: permission denied");
        err.code = "EACCES";
        throw err;
      },
      statSyncFn: () => makeStat(0),
      createRequireFn: () => ({ resolve: () => "/never" }),
      hasHealthyLocalJestInstallFn: () => false,
      cacheRoot: "/tmp/dxmessaging-managed-jest",
      nowMs: 1_700_000_000_000
    });

    expect(section.status).toBe("fail");
    const text = section.lines.join("\n");
    expect(text).toContain("Could not read cache root");
    expect(text).toContain("EACCES: permission denied");
    // Even on the host-fault FAIL path there is no manual rm one-liner.
    expect(text).not.toContain("Manual reset");
  });

  test("readdir EACCES caps at WARN (not fail) when local Jest is HEALTHY (relevance gate)", () => {
    // WRONG-SEVERITY guard: a host permissions/IO glitch on the REGENERABLE,
    // irrelevant tmp cache root must NOT hard-block a push that would provably
    // never consult the fallback. When local Jest is healthy the readdir-error
    // branch is purely informational -> WARN (mirrors the runner-failure
    // branch's relevance gate).
    const section = checkIsolatedJestCache({
      existsSyncFn: () => true,
      readdirSyncFn: () => {
        const err = new Error("EIO: i/o error, scandir");
        err.code = "EIO";
        throw err;
      },
      statSyncFn: () => makeStat(0),
      createRequireFn: () => ({ resolve: () => "/never" }),
      hasHealthyLocalJestInstallFn: () => true,
      cacheRoot: "/tmp/dxmessaging-managed-jest",
      nowMs: 1_700_000_000_000
    });

    expect(section.status).toBe("warn");
    const text = section.lines.join("\n");
    expect(text).toContain("Could not read cache root");
    expect(text).toContain(
      "Local node_modules Jest is healthy; this fallback cache is not consulted"
    );
    expect(text).not.toContain("Manual reset");
  });

  for (const localJestHealthy of [true, false]) {
    test(`readdir ENOTDIR (stray FILE at cache root) -> WARN regenerable, never FAIL (localJestHealthy=${localJestHealthy})`, () => {
      // The cache ROOT is a stray FILE, not a dir: readdir throws ENOTDIR. This
      // IS regenerable corruption (the bootstrap purges the stray file and the
      // next run rebuilds the dir), so it is WARN regardless of the relevance
      // gate -- never a push-blocking FAIL. Contrast with EACCES/EIO above.
      const section = checkIsolatedJestCache({
        existsSyncFn: () => true,
        readdirSyncFn: () => {
          const err = new Error("ENOTDIR: not a directory, scandir");
          err.code = "ENOTDIR";
          throw err;
        },
        statSyncFn: () => makeStat(0),
        createRequireFn: () => ({ resolve: () => "/never" }),
        hasHealthyLocalJestInstallFn: () => localJestHealthy,
        cacheRoot: "/tmp/dxmessaging-managed-jest",
        nowMs: 1_700_000_000_000
      });

      expect(section.status).toBe("warn");
      const text = section.lines.join("\n");
      expect(text).toContain("is a stray FILE, not a directory");
      // References the automated heal, never a manual rm.
      expect(text).toContain("auto-cleared");
      expect(text).not.toContain("Manual reset");
    });
  }

  test("returns WARN when the resolved runner is ZERO-BYTE (empty file, antivirus mid-write)", () => {
    // A jest-circus/build/runner.js that EXISTS but is size 0 is the
    // antivirus/Disk-Cleanup mid-write class: existsSync passes but require()
    // would load an empty module and Jest crashes. The doctor treats size 0 as
    // a miss (mirroring run-managed-jest's isUsableRunnerFile + node-modules-
    // integrity's empty-file rule) -> regenerable corruption -> WARN.
    const now = 1_700_000_000_000;
    const resolvedRunner =
      "/tmp/dxmessaging-managed-jest/jest_30.3.0/node_modules/jest-circus/build/runner.js";
    const section = checkIsolatedJestCache({
      existsSyncFn: () => true, // package.json + runner path both "exist"
      readdirSyncFn: () => [{ name: "jest_30.3.0", isDirectory: () => true }],
      statSyncFn: (p) => (p === resolvedRunner ? { size: 0 } : makeStat(now - 1000)),
      createRequireFn: () => ({ resolve: () => resolvedRunner }),
      hasHealthyLocalJestInstallFn: () => false,
      cacheRoot: "/tmp/dxmessaging-managed-jest",
      nowMs: now
    });

    expect(section.status).toBe("warn");
    const text = section.lines.join("\n");
    expect(text).toContain("empty (size 0)");
    expect(text).not.toContain("Manual reset");
  });

  test("ignores non-directory entries under cache root", () => {
    const section = checkIsolatedJestCache({
      existsSyncFn: () => true,
      readdirSyncFn: () => [{ name: "stray-file.txt", isDirectory: () => false }],
      statSyncFn: () => makeStat(Date.now()),
      createRequireFn: () => ({ resolve: () => "/never" }),
      cacheRoot: "/tmp/dxmessaging-managed-jest",
      nowMs: Date.now()
    });

    expect(section.status).toBe("ok");
    expect(section.lines.join("\n")).toContain("contains no install dirs");
  });
});

describe("checkEolPolicy", () => {
  test("returns ok and reports policy counts consistent with the shared policy module", () => {
    const realPolicy = require("../lib/eol-policy");
    const section = checkEolPolicy({ policy: realPolicy });

    expect(section.status).toBe("ok");
    const text = section.lines.join("\n");
    expect(text).toContain(`CRLF extensions (${realPolicy.crlfExts.size})`);
    expect(text).toContain(`LF   extensions (${realPolicy.lfExts.size})`);
    const expectedTotal = realPolicy.crlfExts.size + realPolicy.lfExts.size;
    expect(text).toContain(`total tracked extensions: ${expectedTotal}`);
  });

  test("returns fail when an extension appears in both crlfExts and lfExts", () => {
    const conflictingPolicy = {
      crlfExts: new Set([".cs", ".js"]),
      lfExts: new Set([".js", ".md"])
    };
    const section = checkEolPolicy({ policy: conflictingPolicy });
    expect(section.status).toBe("fail");
    expect(section.lines.join("\n")).toContain(".js");
  });
});

describe("checkPreCommitConfig", () => {
  const sampleConfig = [
    "repos:",
    "  - repo: local",
    "    hooks:",
    "      - id: alpha",
    "        entry: echo alpha",
    "        language: system",
    "        stages:",
    "          - pre-commit",
    "      - id: beta",
    "        entry: echo beta",
    "        language: system",
    "        stages:",
    "          - pre-push",
    "      - id: gamma",
    "        entry: echo gamma",
    "        language: system",
    "        stages:",
    "          - pre-commit",
    "          - pre-push",
    ""
  ].join("\n");

  function makeReadFile(map) {
    return (filePath) => {
      if (Object.prototype.hasOwnProperty.call(map, filePath)) {
        return map[filePath];
      }
      throw new Error(`unexpected read: ${filePath}`);
    };
  }

  test("counts total hooks and pre-push subset, verifies preflight coverage script", () => {
    const packageJson = JSON.stringify({
      scripts: {
        "preflight:pre-push": "node scripts/run-prepush-preflight.js"
      }
    });

    const section = checkPreCommitConfig({
      readFileSyncFn: makeReadFile({
        "/repo/.pre-commit-config.yaml": sampleConfig,
        "/repo/package.json": packageJson
      }),
      parsePrecommitYaml: precommitYaml,
      runCommandFn: () => ({
        status: 0,
        error: null,
        stdout: "pre-commit 4.0.0",
        stderr: ""
      }),
      configPath: "/repo/.pre-commit-config.yaml",
      packageJsonPath: "/repo/package.json"
    });

    expect(section.status).toBe("ok");
    const text = section.lines.join("\n");
    expect(text).toContain("Parsed 3 hook block(s)");
    expect(text).toContain("pre-push hooks (2): beta, gamma");
    expect(text).toContain("pre-commit --version: pre-commit 4.0.0");
    expect(text).toContain("preflight:pre-push script present");
    expect(text).toContain("invokes the full pre-push hook set");
  });

  test("returns fail when preflight:pre-push is missing", () => {
    const packageJson = JSON.stringify({ scripts: {} });

    const section = checkPreCommitConfig({
      readFileSyncFn: makeReadFile({
        "/repo/.pre-commit-config.yaml": sampleConfig,
        "/repo/package.json": packageJson
      }),
      parsePrecommitYaml: precommitYaml,
      runCommandFn: () => ({ status: 1, error: null, stdout: "", stderr: "" }),
      configPath: "/repo/.pre-commit-config.yaml",
      packageJsonPath: "/repo/package.json"
    });

    expect(section.status).toBe("fail");
    expect(section.lines.join("\n")).toContain("preflight:pre-push is missing");
  });

  test("returns fail when preflight:pre-push lacks the all-files invocation", () => {
    const packageJson = JSON.stringify({
      scripts: {
        "preflight:pre-push": "echo not-really"
      }
    });

    const section = checkPreCommitConfig({
      readFileSyncFn: makeReadFile({
        "/repo/.pre-commit-config.yaml": sampleConfig,
        "/repo/package.json": packageJson
      }),
      parsePrecommitYaml: precommitYaml,
      runCommandFn: () => ({ status: 1, error: null, stdout: "", stderr: "" }),
      configPath: "/repo/.pre-commit-config.yaml",
      packageJsonPath: "/repo/package.json"
    });

    expect(section.status).toBe("fail");
    expect(section.lines.join("\n")).toContain("does not invoke the full pre-push hook set");
  });

  test("fails when pre-commit is not on PATH (ENOENT)", () => {
    // Without `pre-commit` on PATH, `npm run preflight:pre-push` cannot
    // run its `pre-commit run --hook-stage pre-push --all-files` step.
    // The doctor must surface this as a hard failure -- it directly
    // contradicts the doctor's premise that "this branch is safe to push".
    const packageJson = JSON.stringify({
      scripts: {
        "preflight:pre-push":
          "npm run preflight:pre-commit && pre-commit run --hook-stage pre-push --all-files"
      }
    });

    const section = checkPreCommitConfig({
      readFileSyncFn: makeReadFile({
        "/repo/.pre-commit-config.yaml": sampleConfig,
        "/repo/package.json": packageJson
      }),
      parsePrecommitYaml: precommitYaml,
      runCommandFn: () => ({
        status: null,
        error: { code: "ENOENT", message: "ENOENT" },
        stdout: "",
        stderr: ""
      }),
      configPath: "/repo/.pre-commit-config.yaml",
      packageJsonPath: "/repo/package.json"
    });

    expect(section.status).toBe("fail");
    const text = section.lines.join("\n");
    expect(text).toContain("pre-commit --version: not on PATH (ENOENT)");
    expect(text).toContain("preflight:pre-push cannot run");
    expect(text).toContain("npm run repair:pre-commit");
  });

  test("fails when pre-commit --version errors with a non-ENOENT reason", () => {
    const packageJson = JSON.stringify({
      scripts: {
        "preflight:pre-push":
          "npm run preflight:pre-commit && pre-commit run --hook-stage pre-push --all-files"
      }
    });

    const section = checkPreCommitConfig({
      readFileSyncFn: makeReadFile({
        "/repo/.pre-commit-config.yaml": sampleConfig,
        "/repo/package.json": packageJson
      }),
      parsePrecommitYaml: precommitYaml,
      runCommandFn: () => ({
        status: 2,
        error: null,
        stdout: "",
        stderr: "some other failure"
      }),
      configPath: "/repo/.pre-commit-config.yaml",
      packageJsonPath: "/repo/package.json"
    });

    expect(section.status).toBe("fail");
    expect(section.lines.join("\n")).toContain("pre-commit --version: not available");
  });

  test("accepts a pre-resolved preCommitVersionResult and does NOT spawn", () => {
    const packageJson = JSON.stringify({
      scripts: {
        "preflight:pre-push":
          "npm run preflight:pre-commit && pre-commit run --hook-stage pre-push --all-files"
      }
    });

    let spawned = 0;
    const section = checkPreCommitConfig({
      readFileSyncFn: makeReadFile({
        "/repo/.pre-commit-config.yaml": sampleConfig,
        "/repo/package.json": packageJson
      }),
      parsePrecommitYaml: precommitYaml,
      runCommandFn: () => {
        spawned += 1;
        return { status: 1, error: null, stdout: "", stderr: "" };
      },
      preCommitVersionResult: {
        status: 0,
        error: null,
        stdout: "pre-commit 3.6.0",
        stderr: ""
      },
      configPath: "/repo/.pre-commit-config.yaml",
      packageJsonPath: "/repo/package.json"
    });

    expect(spawned).toBe(0);
    expect(section.status).toBe("ok");
    expect(section.lines.join("\n")).toContain("pre-commit --version: pre-commit 3.6.0");
  });
});

describe("checkHookPerfBudget", () => {
  test("returns ok when scoreConfig reports a score within budget", () => {
    const section = checkHookPerfBudget({
      readFileSyncFn: () => "irrelevant -- scoreConfigFn is mocked",
      scoreConfigFn: () => ({
        totalScore: 5,
        perHookScores: [],
        allowList: [],
        rejections: [],
        perHookViolations: []
      }),
      budget: 10,
      perHookCeiling: 3,
      configPath: "/repo/.pre-commit-config.yaml"
    });

    expect(section.status).toBe("ok");
    expect(section.lines.join("\n")).toContain("score: 5 (budget=10");
  });

  test("returns fail when score exceeds the budget", () => {
    const section = checkHookPerfBudget({
      readFileSyncFn: () => "irrelevant",
      scoreConfigFn: () => ({
        totalScore: 99,
        perHookScores: [],
        allowList: [],
        rejections: [],
        perHookViolations: []
      }),
      budget: 10,
      perHookCeiling: 3,
      configPath: "/repo/.pre-commit-config.yaml"
    });

    expect(section.status).toBe("fail");
    expect(section.lines.join("\n")).toContain("exceeds whole-pipeline budget");
  });

  test("returns fail when per-hook ceiling is violated", () => {
    const section = checkHookPerfBudget({
      readFileSyncFn: () => "irrelevant",
      scoreConfigFn: () => ({
        totalScore: 5,
        perHookScores: [],
        allowList: [],
        rejections: [],
        perHookViolations: [{ id: "bad-hook", score: 5, ceiling: 3 }]
      }),
      budget: 10,
      perHookCeiling: 3,
      configPath: "/repo/.pre-commit-config.yaml"
    });

    expect(section.status).toBe("fail");
    expect(section.lines.join("\n")).toContain("bad-hook");
  });

  test("returns fail when scoreConfig reports rejected perf-allow directives", () => {
    const section = checkHookPerfBudget({
      readFileSyncFn: () => "irrelevant",
      scoreConfigFn: () => ({
        totalScore: 5,
        perHookScores: [],
        allowList: [],
        rejections: [{ id: "noisy", startLine: 12, error: "reason too short" }],
        perHookViolations: []
      }),
      budget: 10,
      perHookCeiling: 3,
      configPath: "/repo/.pre-commit-config.yaml"
    });

    expect(section.status).toBe("fail");
    expect(section.lines.join("\n")).toContain("reason too short");
  });

  test("integration: live precommit-perf-score.scoreConfig consumed correctly", () => {
    // The doctor reuses the EXISTING scoreConfig (per the deliverable
    // constraints). This integration smoke-test runs the real scorer
    // against a tiny synthetic config to prove the wiring.
    const tinyConfig = [
      "repos:",
      "  - repo: local",
      "    hooks:",
      "      - id: cheap",
      "        entry: echo hi",
      "        language: system",
      "        stages:",
      "          - pre-commit"
    ].join("\n");

    const section = checkHookPerfBudget({
      readFileSyncFn: () => tinyConfig,
      scoreConfigFn: precommitPerfScore.scoreConfig,
      budget: precommitPerfScore.PERF_BUDGET,
      perHookCeiling: precommitPerfScore.PER_HOOK_CEILING,
      configPath: "/synthetic/.pre-commit-config.yaml"
    });

    expect(section.status).toBe("ok");
  });
});

describe("checkCrossPlatformSanity", () => {
  test("reports the injected platform and LF package.json", () => {
    const calls = [];
    const runCommandFn = (cmd) => {
      calls.push(cmd);
      if (cmd === "pwsh") {
        return { status: 0, stdout: "PowerShell 7.4.0\n", error: null };
      }
      if (cmd === "bash") {
        return {
          status: 0,
          stdout: "GNU bash, version 5.2.15(1)-release\n",
          error: null
        };
      }
      return { status: 1, error: null, stdout: "", stderr: "" };
    };

    const section = checkCrossPlatformSanity({
      platformFn: () => "linux",
      runCommandFn,
      readFileSyncFn: () => '{\n  "name": "clean"\n}\n',
      packageJsonPath: "/repo/package.json",
      shellEnv: "/bin/bash"
    });

    expect(section.status).toBe("ok");
    const text = section.lines.join("\n");
    expect(text).toContain("process.platform: linux");
    expect(text).toContain("PowerShell 7.4.0");
    expect(text).toContain("bash, version 5.2.15");
    expect(text).toContain("package.json line endings: LF");
    expect(calls).toEqual(["pwsh", "bash"]);
  });

  test("gracefully handles pwsh missing on Linux (does not fail)", () => {
    const section = checkCrossPlatformSanity({
      platformFn: () => "linux",
      runCommandFn: (cmd) => {
        if (cmd === "pwsh") {
          return { status: null, error: { code: "ENOENT" }, stdout: "", stderr: "" };
        }
        if (cmd === "bash") {
          return { status: 0, stdout: "GNU bash, version 5.2\n", error: null };
        }
        return { status: 1, error: null, stdout: "", stderr: "" };
      },
      readFileSyncFn: () => "{\n}\n",
      packageJsonPath: "/repo/package.json",
      shellEnv: "/bin/bash"
    });

    expect(section.status).toBe("ok");
    expect(section.lines.join("\n")).toContain("pwsh:    not installed");
  });

  test("flags pwsh ENOENT as warn on win32", () => {
    const section = checkCrossPlatformSanity({
      platformFn: () => "win32",
      runCommandFn: (cmd) => {
        if (cmd === "pwsh") {
          return { status: null, error: { code: "ENOENT" }, stdout: "", stderr: "" };
        }
        if (cmd === "bash") {
          return { status: 0, stdout: "GNU bash, version 5.2\n", error: null };
        }
        return { status: 1, error: null, stdout: "", stderr: "" };
      },
      readFileSyncFn: () => "{}\n",
      packageJsonPath: "/repo/package.json",
      shellEnv: "C:\\Windows\\System32\\cmd.exe"
    });

    expect(section.status).toBe("warn");
  });

  test("fails when package.json has CRLF line endings", () => {
    const section = checkCrossPlatformSanity({
      platformFn: () => "linux",
      runCommandFn: () => ({ status: 0, stdout: "x\n", error: null }),
      readFileSyncFn: () => '{\r\n  "name": "crlf"\r\n}\r\n',
      packageJsonPath: "/repo/package.json",
      shellEnv: "/bin/bash"
    });

    expect(section.status).toBe("fail");
    expect(section.lines.join("\n")).toContain("CRLF line endings");
  });

  test("fails when package.json cannot be read", () => {
    const section = checkCrossPlatformSanity({
      platformFn: () => "linux",
      runCommandFn: () => ({ status: 0, stdout: "x\n", error: null }),
      readFileSyncFn: () => {
        throw new Error("ENOENT: package.json");
      },
      packageJsonPath: "/repo/package.json",
      shellEnv: "/bin/bash"
    });

    expect(section.status).toBe("fail");
    expect(section.lines.join("\n")).toContain("Could not read");
  });
});

describe("checkWorkingTreeState", () => {
  function makeRun(stdout, overrides = {}) {
    return () => ({
      status: 0,
      error: null,
      stdout,
      stderr: "",
      ...overrides
    });
  }

  test("returns ok on a clean tree (empty porcelain)", () => {
    const section = checkWorkingTreeState({ runCommandFn: makeRun("") });
    expect(section.status).toBe("ok");
    expect(section.lines.join("\n")).toContain("Working tree is clean");
  });

  test("returns fail listing untracked paths (validate-untracked-policy gate)", () => {
    // Mirrors the exact case the adversarial reviewer caught: an untracked
    // path under scripts/ that validate-untracked-policy would reject.
    const section = checkWorkingTreeState({
      runCommandFn: makeRun("?? scripts/foo.js\n")
    });
    expect(section.status).toBe("fail");
    const text = section.lines.join("\n");
    expect(text).toContain("1 untracked-and-unignored path(s)");
    expect(text).toContain("?? scripts/foo.js");
    expect(text).toContain("Remediation:");
    expect(text).toContain(".gitignore");
    expect(text).toContain("git add -N");
  });

  test("returns warn for modified-but-not-staged paths", () => {
    // " M" -> worktree change, no staged change. preflight does NOT fail
    // on these, but the doctor warns so the operator notices the drift.
    const section = checkWorkingTreeState({
      runCommandFn: makeRun(" M scripts/foo.js\n")
    });
    expect(section.status).toBe("warn");
    const text = section.lines.join("\n");
    expect(text).toContain("1 modified-but-unstaged path(s)");
    expect(text).toContain(" M scripts/foo.js");
  });

  test("returns ok when only staged changes are present", () => {
    // "M " -> staged modification, worktree matches index. The push will
    // carry this; nothing to flag.
    const section = checkWorkingTreeState({
      runCommandFn: makeRun("M  scripts/foo.js\n")
    });
    expect(section.status).toBe("ok");
    const text = section.lines.join("\n");
    expect(text).toContain("1 staged path(s)");
    expect(text).toContain("M  scripts/foo.js");
  });

  test("includes staged + unstaged info under a FAIL untracked diagnostic", () => {
    const porcelain = ["?? new.js", " M modified.js", "M  staged.js"].join("\n") + "\n";
    const section = checkWorkingTreeState({
      runCommandFn: makeRun(porcelain)
    });
    expect(section.status).toBe("fail");
    const text = section.lines.join("\n");
    expect(text).toContain("?? new.js");
    expect(text).toContain(" M modified.js");
    expect(text).toContain("M  staged.js");
  });

  test("fails when git is not on PATH (ENOENT)", () => {
    const section = checkWorkingTreeState({
      runCommandFn: () => ({
        status: null,
        error: { code: "ENOENT", message: "ENOENT" },
        stdout: "",
        stderr: ""
      })
    });
    expect(section.status).toBe("fail");
    expect(section.lines.join("\n")).toContain("git not found on PATH");
  });

  test("fails when git status exits non-zero", () => {
    const section = checkWorkingTreeState({
      runCommandFn: () => ({
        status: 128,
        error: null,
        stdout: "",
        stderr: "fatal: not a git repository"
      })
    });
    expect(section.status).toBe("fail");
    const text = section.lines.join("\n");
    expect(text).toContain("git status exited with status 128");
    expect(text).toContain("not a git repository");
  });

  test("fails when spawn returns a non-ENOENT error", () => {
    const section = checkWorkingTreeState({
      runCommandFn: () => ({
        status: null,
        error: new Error("ETXTBSY: text file busy"),
        stdout: "",
        stderr: ""
      })
    });
    expect(section.status).toBe("fail");
    expect(section.lines.join("\n")).toContain("ETXTBSY");
  });

  test("treats intent-to-add (` A` after git add -N) as warn, NOT fail", () => {
    // `git add -N path` produces " A path" in porcelain output. The
    // important distinction is that validate-untracked-policy's underlying
    // command (`git ls-files --others --exclude-standard`) does NOT list
    // intent-to-add files, so they are NOT a hard failure. Treating them
    // as modified-but-unstaged matches that semantic.
    const section = checkWorkingTreeState({
      runCommandFn: makeRun(" A scripts/new.js\n")
    });
    expect(section.status).toBe("warn");
  });
});

describe("checkChangedDocumentation", () => {
  test("returns ok when there are no changed documentation files", () => {
    const section = checkChangedDocumentation({
      runChangedDocValidatorsFn: () => ({ files: [], totalViolations: 0 })
    });

    expect(section.status).toBe("ok");
    expect(section.lines.join("\n")).toContain("No changed markdown or C# files");
  });

  test("returns fail when changed documentation validators report violations", () => {
    const section = checkChangedDocumentation({
      runChangedDocValidatorsFn: () => ({
        files: ["docs/failing.md"],
        totalViolations: 2
      })
    });

    expect(section.status).toBe("fail");
    const text = section.lines.join("\n");
    expect(text).toContain("2 documentation validator violation(s)");
    expect(text).toContain("npm run validate:changed-docs");
  });
});

describe("probeToolVersions", () => {
  test("spawns one --version probe per tool and returns them keyed by name", () => {
    const calls = [];
    const runCommandFn = (cmd, args) => {
      calls.push({ cmd, args });
      return { status: 0, error: null, stdout: `${cmd}-version-output`, stderr: "" };
    };
    const probes = probeToolVersions({ runCommandFn });
    expect(probes.npm.stdout).toBe("npm-version-output");
    expect(probes.preCommit.stdout).toBe("pre-commit-version-output");
    expect(probes.pwsh.stdout).toBe("pwsh-version-output");
    expect(probes.bash.stdout).toBe("bash-version-output");
    // Exactly one spawn per tool.
    expect(calls.map((c) => c.cmd)).toEqual(["npm", "pre-commit", "pwsh", "bash"]);
  });
});

describe("KNOWN_STATUSES", () => {
  test("exposes the canonical status vocabulary", () => {
    expect(KNOWN_STATUSES).toEqual(["ok", "warn", "fail"]);
  });
});

describe("runDoctor aggregator", () => {
  test("aggregates section statuses; any fail yields exit code 1", () => {
    // We cannot trivially inject all six section deps via runDoctor()
    // without effectively duplicating the section logic in tests, so we
    // exercise the aggregator pathway with overrides that force one
    // section to fail by triggering a failure condition that depends only
    // on the injected deps.
    const overrides = {
      checkNodeModulesFreshness: {
        toolSpecs: [
          {
            name: "always-missing",
            requiredFiles: ["node_modules/always-missing/index.js"]
          }
        ],
        existsSyncFn: () => false,
        requireFn: noopRequire,
        requireResolveFn: () => null,
        repoRoot: "/repo"
      },
      checkIsolatedJestCache: {
        existsSyncFn: () => false,
        readdirSyncFn: () => [],
        statSyncFn: () => ({ mtimeMs: 0 }),
        cacheRoot: "/never",
        nowMs: 0
      },
      checkEolPolicy: {
        policy: { crlfExts: new Set(), lfExts: new Set() }
      },
      checkPreCommitConfig: {
        readFileSyncFn: (filePath) => {
          if (filePath.endsWith(".pre-commit-config.yaml")) {
            return "repos: []\n";
          }
          return JSON.stringify({
            scripts: {
              "preflight:pre-push":
                "npm run preflight:pre-commit && pre-commit run --hook-stage pre-push --all-files"
            }
          });
        },
        parsePrecommitYaml: precommitYaml,
        runCommandFn: () => ({ status: 0, stdout: "pre-commit 4.0.0", error: null })
      },
      checkHookPerfBudget: {
        readFileSyncFn: () => "anything",
        scoreConfigFn: () => ({
          totalScore: 0,
          perHookScores: [],
          allowList: [],
          rejections: [],
          perHookViolations: []
        }),
        budget: 10,
        perHookCeiling: 3
      },
      checkCrossPlatformSanity: {
        platformFn: () => "linux",
        runCommandFn: () => ({ status: 0, stdout: "x\n", error: null }),
        readFileSyncFn: () => "{}\n",
        shellEnv: "/bin/bash"
      },
      checkWorkingTreeState: {
        runCommandFn: () => ({ status: 0, stdout: "", error: null, stderr: "" })
      },
      checkChangedDocumentation: {
        runChangedDocValidatorsFn: () => ({ files: [], totalViolations: 0 })
      }
    };

    const report = runDoctor({ overrides });
    expect(report.overall).toBe("fail");
    expect(report.status).toBe(1);
    const failingSection = report.sections.find((s) => s.status === "fail");
    expect(failingSection).toBeTruthy();
    expect(failingSection.name).toBe("node_modules freshness");
  });

  test("aggregates to ok when every section is ok", () => {
    const overrides = {
      checkNodeModulesFreshness: {
        toolSpecs: [],
        existsSyncFn: () => true,
        requireFn: noopRequire,
        requireResolveFn: () => "/x",
        repoRoot: "/repo"
      },
      checkIsolatedJestCache: {
        existsSyncFn: () => false,
        readdirSyncFn: () => [],
        statSyncFn: () => ({ mtimeMs: 0 }),
        cacheRoot: "/never",
        nowMs: 0
      },
      checkEolPolicy: {
        policy: { crlfExts: new Set([".cs"]), lfExts: new Set([".js"]) }
      },
      checkPreCommitConfig: {
        readFileSyncFn: (filePath) => {
          if (filePath.endsWith(".pre-commit-config.yaml")) {
            return "repos: []\n";
          }
          return JSON.stringify({
            scripts: {
              "preflight:pre-push":
                "npm run preflight:pre-commit && pre-commit run --hook-stage pre-push --all-files"
            }
          });
        },
        parsePrecommitYaml: precommitYaml,
        runCommandFn: () => ({ status: 0, stdout: "pre-commit 4.0.0", error: null })
      },
      checkHookPerfBudget: {
        readFileSyncFn: () => "anything",
        scoreConfigFn: () => ({
          totalScore: 0,
          perHookScores: [],
          allowList: [],
          rejections: [],
          perHookViolations: []
        }),
        budget: 10,
        perHookCeiling: 3
      },
      checkCrossPlatformSanity: {
        platformFn: () => "linux",
        runCommandFn: () => ({ status: 0, stdout: "x\n", error: null }),
        readFileSyncFn: () => "{}\n",
        shellEnv: "/bin/bash"
      },
      checkWorkingTreeState: {
        runCommandFn: () => ({ status: 0, stdout: "", error: null, stderr: "" })
      },
      checkChangedDocumentation: {
        runChangedDocValidatorsFn: () => ({ files: [], totalViolations: 0 })
      }
    };

    const report = runDoctor({ overrides });
    expect(report.overall).toBe("ok");
    expect(report.status).toBe(0);
  });

  test("threads versionProbes into pre-commit + cross-platform sections without re-spawning (M1)", () => {
    // Sentinel results that the sections SHOULD consume from
    // versionProbes. If they ignored versionProbes and spawned their own
    // probes, runCommandFn (which we set up to throw) would be invoked
    // for those tools.
    const versionProbes = {
      npm: { status: 0, error: null, stdout: "11.99.0", stderr: "" },
      preCommit: { status: 0, error: null, stdout: "pre-commit SENTINEL-PC", stderr: "" },
      pwsh: { status: 0, error: null, stdout: "PowerShell SENTINEL-PWSH\n", stderr: "" },
      bash: { status: 0, error: null, stdout: "GNU bash, version SENTINEL-BASH\n", stderr: "" }
    };

    const throwOnSpawn = (cmd) => {
      // The aggregator passes the versionProbes-derived results into
      // sections; sections must not re-spawn. The only spawn allowed
      // through this fixture is git status inside checkWorkingTreeState
      // (its own runCommandFn is independently injected below).
      throw new Error(`Unexpected spawn for ${cmd} -- versionProbes should have been used`);
    };

    const overrides = {
      checkNodeModulesFreshness: {
        toolSpecs: [],
        existsSyncFn: () => true,
        requireFn: noopRequire,
        requireResolveFn: () => "/x",
        repoRoot: "/repo"
      },
      checkIsolatedJestCache: {
        existsSyncFn: () => false,
        readdirSyncFn: () => [],
        statSyncFn: () => ({ mtimeMs: 0 }),
        cacheRoot: "/never",
        nowMs: 0
      },
      checkEolPolicy: {
        policy: { crlfExts: new Set([".cs"]), lfExts: new Set([".js"]) }
      },
      checkPreCommitConfig: {
        readFileSyncFn: (filePath) => {
          if (filePath.endsWith(".pre-commit-config.yaml")) {
            return "repos: []\n";
          }
          return JSON.stringify({
            scripts: {
              "preflight:pre-push":
                "npm run preflight:pre-commit && pre-commit run --hook-stage pre-push --all-files"
            }
          });
        },
        parsePrecommitYaml: precommitYaml,
        runCommandFn: throwOnSpawn
      },
      checkHookPerfBudget: {
        readFileSyncFn: () => "anything",
        scoreConfigFn: () => ({
          totalScore: 0,
          perHookScores: [],
          allowList: [],
          rejections: [],
          perHookViolations: []
        }),
        budget: 10,
        perHookCeiling: 3
      },
      checkCrossPlatformSanity: {
        platformFn: () => "linux",
        runCommandFn: throwOnSpawn,
        readFileSyncFn: () => "{}\n",
        shellEnv: "/bin/bash"
      },
      checkWorkingTreeState: {
        runCommandFn: () => ({ status: 0, stdout: "", error: null, stderr: "" })
      },
      checkChangedDocumentation: {
        runChangedDocValidatorsFn: () => ({ files: [], totalViolations: 0 })
      }
    };

    const report = runDoctor({ overrides, versionProbes });
    expect(report.overall).toBe("ok");

    const preCommitSection = report.sections.find((s) => s.name === "pre-commit config");
    expect(preCommitSection.lines.join("\n")).toContain("SENTINEL-PC");

    const sanitySection = report.sections.find((s) => s.name === "cross-platform sanity");
    const sanityText = sanitySection.lines.join("\n");
    expect(sanityText).toContain("SENTINEL-PWSH");
    expect(sanityText).toContain("SENTINEL-BASH");
  });

  test("fast mode skips the two redundant git-walk sections WITHOUT invoking their git runners", () => {
    // Hot-path trim: with fast:true (DXMSG_DOCTOR_FAST), checkWorkingTreeState
    // and checkChangedDocumentation are replaced by stable "skipped" sections
    // and their injected runners are NEVER called (preflight:pre-push re-runs
    // those validators authoritatively). Inject runners that THROW to prove
    // they are not invoked.
    const overrides = {
      checkNodeModulesFreshness: {
        toolSpecs: [],
        existsSyncFn: () => true,
        requireFn: noopRequire,
        requireResolveFn: () => "/x",
        repoRoot: "/repo"
      },
      checkIsolatedJestCache: {
        existsSyncFn: () => false,
        readdirSyncFn: () => [],
        statSyncFn: () => ({ mtimeMs: 0 }),
        cacheRoot: "/never",
        nowMs: 0
      },
      checkEolPolicy: {
        policy: { crlfExts: new Set([".cs"]), lfExts: new Set([".js"]) }
      },
      checkPreCommitConfig: {
        readFileSyncFn: (filePath) => {
          if (filePath.endsWith(".pre-commit-config.yaml")) {
            return "repos: []\n";
          }
          return JSON.stringify({
            scripts: {
              "preflight:pre-push":
                "npm run preflight:pre-commit && pre-commit run --hook-stage pre-push --all-files"
            }
          });
        },
        parsePrecommitYaml: precommitYaml,
        runCommandFn: () => ({ status: 0, stdout: "pre-commit 4.0.0", error: null })
      },
      checkHookPerfBudget: {
        readFileSyncFn: () => "anything",
        scoreConfigFn: () => ({
          totalScore: 0,
          perHookScores: [],
          allowList: [],
          rejections: [],
          perHookViolations: []
        }),
        budget: 10,
        perHookCeiling: 3
      },
      checkCrossPlatformSanity: {
        platformFn: () => "linux",
        runCommandFn: () => ({ status: 0, stdout: "x\n", error: null }),
        readFileSyncFn: () => "{}\n",
        shellEnv: "/bin/bash"
      },
      checkWorkingTreeState: {
        runCommandFn: () => {
          throw new Error("checkWorkingTreeState git runner must NOT be called in fast mode");
        }
      },
      checkChangedDocumentation: {
        runChangedDocValidatorsFn: () => {
          throw new Error("checkChangedDocumentation runner must NOT be called in fast mode");
        }
      }
    };

    const report = runDoctor({ overrides, fast: true });
    // Aggregation neutral: skipped sections are OK, so overall stays OK.
    expect(report.overall).toBe("ok");
    expect(report.status).toBe(0);

    const wt = report.sections.find((s) => s.name === "working-tree state");
    const cd = report.sections.find((s) => s.name === "changed documentation validators");
    // Report shape is stable: both sections still present, both "skipped".
    expect(wt.status).toBe("ok");
    expect(cd.status).toBe("ok");
    expect(wt.lines.join("\n")).toContain("skipped (covered by preflight:pre-push validators)");
    expect(cd.lines.join("\n")).toContain("skipped (covered by preflight:pre-push validators)");
  });

  test("non-fast mode (default) still runs the two git-walk sections", () => {
    // Guard against the fast path silently becoming the default: with fast
    // unset, the injected runners ARE consulted (here returning clean states).
    let wtCalled = false;
    let cdCalled = false;
    const overrides = {
      checkNodeModulesFreshness: {
        toolSpecs: [],
        existsSyncFn: () => true,
        requireFn: noopRequire,
        requireResolveFn: () => "/x",
        repoRoot: "/repo"
      },
      checkIsolatedJestCache: {
        existsSyncFn: () => false,
        readdirSyncFn: () => [],
        statSyncFn: () => ({ mtimeMs: 0 }),
        cacheRoot: "/never",
        nowMs: 0
      },
      checkEolPolicy: { policy: { crlfExts: new Set([".cs"]), lfExts: new Set([".js"]) } },
      checkPreCommitConfig: {
        readFileSyncFn: (filePath) => {
          if (filePath.endsWith(".pre-commit-config.yaml")) {
            return "repos: []\n";
          }
          return JSON.stringify({
            scripts: {
              "preflight:pre-push":
                "npm run preflight:pre-commit && pre-commit run --hook-stage pre-push --all-files"
            }
          });
        },
        parsePrecommitYaml: precommitYaml,
        runCommandFn: () => ({ status: 0, stdout: "pre-commit 4.0.0", error: null })
      },
      checkHookPerfBudget: {
        readFileSyncFn: () => "anything",
        scoreConfigFn: () => ({
          totalScore: 0,
          perHookScores: [],
          allowList: [],
          rejections: [],
          perHookViolations: []
        }),
        budget: 10,
        perHookCeiling: 3
      },
      checkCrossPlatformSanity: {
        platformFn: () => "linux",
        runCommandFn: () => ({ status: 0, stdout: "x\n", error: null }),
        readFileSyncFn: () => "{}\n",
        shellEnv: "/bin/bash"
      },
      checkWorkingTreeState: {
        runCommandFn: () => {
          wtCalled = true;
          return { status: 0, stdout: "", error: null, stderr: "" };
        }
      },
      checkChangedDocumentation: {
        runChangedDocValidatorsFn: () => {
          cdCalled = true;
          return { files: [], totalViolations: 0 };
        }
      }
    };

    const report = runDoctor({ overrides, fast: false });
    expect(report.overall).toBe("ok");
    expect(wtCalled).toBe(true);
    expect(cdCalled).toBe(true);
  });
});

describe("doctor presentation helpers", () => {
  // Data-driven truth table for shouldUseColor. Every row supplies ALL four
  // parameters explicitly so a leaked process.env value (CI=true in this
  // very CI run, for example) cannot influence the assertion. The rationale
  // column documents WHY the expected value follows from the precedence
  // rules in doctor.js#shouldUseColor.
  //
  // Precedence (highest -> lowest), per the production spec:
  //   1. NO_COLOR present and non-empty -> false (no-color.org spec).
  //   2. FORCE_COLOR non-empty and not "0" -> true (Node convention).
  //   3. CI truthy (per isTruthyEnv: "0"/"false"/"no"/"off"/"" are falsy).
  //   4. isTTY -> true; else false.
  test.each([
    // [name, envCi, envNoColor, envForceColor, isTTY, expected]
    // --- CI semantics (no NO_COLOR/FORCE_COLOR interference) ---
    ["CI=1 + TTY suppresses color", "1", "", "", true, false],
    ["CI=true + TTY suppresses color", "true", "", "", true, false],
    // CI="" (explicit empty string) is distinct from "no env present"
    // at the row level even though the function treats both as "not CI".
    // Kept as a distinct row so a future regression to the empty-CI
    // path is captured separately from the no-env-present path.
    ['CI="" (explicit empty) + TTY allows color (falsy CI)', "", "", "", true, true],
    ["CI=0 + TTY allows color (falsy CI)", "0", "", "", true, true],
    ["CI=false + TTY allows color (falsy CI)", "false", "", "", true, true],
    ["CI=no + TTY allows color (falsy CI)", "no", "", "", true, true],
    ["CI=off + TTY allows color (falsy CI)", "off", "", "", true, true],
    ["CI=null + non-TTY -> no color", null, "", "", false, false],
    // --- NO_COLOR semantics (per https://no-color.org/) ---
    // "When present and not an empty string, regardless of its value..."
    ["NO_COLOR=1 + TTY -> no color", "", "1", "", true, false],
    ["NO_COLOR=true + TTY -> no color", "", "true", "", true, false],
    // The previous code treated NO_COLOR=\"0\" as falsy. That violates the
    // NO_COLOR spec, which says ANY non-empty value disables color.
    ["NO_COLOR=0 + TTY -> no color (spec)", "", "0", "", true, false],
    ["NO_COLOR=false + TTY -> no color (spec)", "", "false", "", true, false],
    ["NO_COLOR=off + TTY -> no color (spec)", "", "off", "", true, false],
    ["NO_COLOR=no + TTY -> no color (spec)", "", "no", "", true, false],
    ['NO_COLOR=" " + TTY -> no color (spec, whitespace is non-empty)', "", " ", "", true, false],
    // Empty NO_COLOR is treated as absent.
    ['NO_COLOR="" + TTY falls through to color=true', "", "", "", true, true],
    // --- FORCE_COLOR semantics (Node convention) ---
    // FORCE_COLOR=0 explicitly disables; any other non-empty value enables.
    ["FORCE_COLOR=1 + non-TTY -> color", "", "", "1", false, true],
    ["FORCE_COLOR=true + non-TTY -> color", "", "", "true", false, true],
    ["FORCE_COLOR=2 + non-TTY -> color", "", "", "2", false, true],
    ["FORCE_COLOR=0 + non-TTY -> no color (Node)", "", "", "0", false, false],
    ['FORCE_COLOR="" + non-TTY -> no color (absent)', "", "", "", false, false],
    // --- FORCE_COLOR whitespace handling (M2 lock-in) ---
    // isForceColorOn trims the input before comparing against "0".
    // Pure-whitespace strings are non-empty but do NOT trim to "0",
    // so they ENABLE color. The padded zero DOES trim to "0", so it
    // DISABLES. A leading tab on a non-zero digit ENABLES.
    ['FORCE_COLOR=" " (single space) -> color (trim != "0")', "", "", " ", false, true],
    ['FORCE_COLOR="\\t1" -> color (trim = "1")', "", "", "\t1", false, true],
    ['FORCE_COLOR=" 0 " -> no color (trim = "0")', "", "", " 0 ", false, false],
    // --- Precedence: NO_COLOR beats FORCE_COLOR ---
    ["NO_COLOR=1 + FORCE_COLOR=1 -> no color (NO_COLOR wins)", "", "1", "1", true, false],
    // --- Precedence: FORCE_COLOR beats CI ---
    ["CI=1 + FORCE_COLOR=1 + non-TTY -> color", "1", "", "1", false, true],
    // --- Precedence: CI beats isTTY ---
    ["CI=1 + non-TTY -> no color (consistent)", "1", "", "", false, false],
    // --- isTTY is the last resort ---
    ["No env + TTY -> color", "", "", "", true, true],
    ["No env + non-TTY -> no color", "", "", "", false, false]
  ])(
    "shouldUseColor truth table: %s",
    (_name, envCi, envNoColor, envForceColor, isTTY, expected) => {
      const result = shouldUseColor({
        envCi,
        envNoColor,
        envForceColor,
        isTTY
      });
      expect(result).toBe(expected);
    }
  );

  describe("shouldUseColor env-default branches (H5)", () => {
    // These tests exercise the destructuring defaults that read from
    // process.env when the caller omits a specific parameter. The
    // production CLI entry point relies on these defaults; we need
    // direct coverage so a regression in the default expressions
    // (e.g. accidentally reading the wrong env var name) is caught.

    // Snapshot the env keys we touch and restore them after each test so
    // the surrounding suite -- which often runs under CI=true -- is not
    // perturbed by these per-key mutations.
    const ENV_KEYS_TO_GUARD = ["CI", "NO_COLOR", "FORCE_COLOR"];
    const savedEnv = {};
    beforeEach(() => {
      for (const k of ENV_KEYS_TO_GUARD) {
        savedEnv[k] = process.env[k];
        delete process.env[k];
      }
    });
    afterEach(() => {
      for (const k of ENV_KEYS_TO_GUARD) {
        if (savedEnv[k] === undefined) {
          delete process.env[k];
        } else {
          process.env[k] = savedEnv[k];
        }
      }
    });

    test("CI default: omitted envCi reads process.env.CI (sets to '1' -> suppress color)", () => {
      process.env.CI = "1";
      const result = shouldUseColor({
        // envCi intentionally omitted -- default reads process.env.CI
        envNoColor: "",
        envForceColor: "",
        isTTY: true
      });
      expect(result).toBe(false);
    });

    test("CI default: process.env.CI absent + TTY -> color", () => {
      // CI was deleted in beforeEach.
      const result = shouldUseColor({
        envNoColor: "",
        envForceColor: "",
        isTTY: true
      });
      expect(result).toBe(true);
    });

    test("NO_COLOR default: omitted envNoColor reads process.env.NO_COLOR (sets to '1' -> suppress color)", () => {
      process.env.NO_COLOR = "1";
      const result = shouldUseColor({
        envCi: "",
        // envNoColor intentionally omitted.
        envForceColor: "",
        isTTY: true
      });
      expect(result).toBe(false);
    });

    test("FORCE_COLOR default: omitted envForceColor reads process.env.FORCE_COLOR (sets to '1' + non-TTY -> color)", () => {
      process.env.FORCE_COLOR = "1";
      const result = shouldUseColor({
        envCi: "",
        envNoColor: "",
        // envForceColor intentionally omitted.
        isTTY: false
      });
      expect(result).toBe(true);
    });

    test("isTTY default: omitted isTTY reads process.stdout.isTTY", () => {
      // The default expression is `Boolean(process.stdout && process.stdout.isTTY)`.
      // We can't reliably mutate process.stdout.isTTY in a worker, so
      // simply assert the function does not throw when isTTY is omitted
      // and returns a strict boolean.
      const result = shouldUseColor({
        envCi: "",
        envNoColor: "",
        envForceColor: ""
        // isTTY intentionally omitted.
      });
      expect(typeof result).toBe("boolean");
    });

    test("all defaults: shouldUseColor() called with no args returns a strict boolean", () => {
      // Regression guard: calling with NO arguments must hit all four
      // env-default branches and still return a boolean.
      const result = shouldUseColor();
      expect(typeof result).toBe("boolean");
    });
  });

  test("shouldUseColor: regression guard - NO_COLOR=0 on a TTY in CI must suppress color (m7)", () => {
    // Specific regression: in CI, process.env.CI is set, but the test
    // historically passed only envNoColor and isTTY. If the function
    // leaks CI from process.env it returns false; the test then asserts
    // true and explodes. With the spec-correct NO_COLOR handling the
    // expected value is now false (NO_COLOR present + non-empty wins
    // over everything), AND all parameters are passed so no leak is
    // possible.
    expect(
      shouldUseColor({
        envCi: "1",
        envNoColor: "0",
        envForceColor: "",
        isTTY: true
      })
    ).toBe(false);
  });

  test("decorateStatusLabel includes ANSI only when useColor=true", () => {
    const colorized = decorateStatusLabel("fail", true);
    expect(colorized).toMatch(/\x1b\[/);
    expect(colorized).toContain("[FAIL]");
    const plain = decorateStatusLabel("fail", false);
    expect(plain).toBe("[FAIL]");
  });

  test("formatHeaderBanner contains repo/node/os fields and uses ASCII box drawing", () => {
    const banner = formatHeaderBanner({
      repoRoot: "/repo",
      nodeVersion: "v24.0.0",
      platform: "linux",
      arch: "x64",
      shellEnv: "/bin/bash",
      npmVersion: "11.0.0"
    });
    expect(banner).toContain("Repo:    /repo");
    expect(banner).toContain("Node:    v24.0.0");
    expect(banner).toContain("npm:     11.0.0");
    expect(banner).toContain("OS:      linux/x64");
    // ASCII-only.
    for (let i = 0; i < banner.length; i += 1) {
      const code = banner.charCodeAt(i);
      const ok = code === 0x0a || (code >= 0x20 && code <= 0x7e);
      if (!ok) {
        throw new Error(
          `Header banner contains non-ASCII codepoint 0x${code.toString(16)} at index ${i}`
        );
      }
    }
  });

  test("formatSection and formatFooter produce printable text", () => {
    const section = { name: "x", status: "ok", lines: ["  ok    body"] };
    const text = formatSection(section, false);
    expect(text).toContain("[ OK ] x");
    expect(text).toContain("ok    body");

    const footer = formatFooter([section], "ok", false);
    expect(footer).toContain("Summary:");
    expect(footer).toContain("Overall: [ OK ]");
  });
});

describe("doctor main()", () => {
  test("main returns the runDoctor status code and writes output via writeFn", () => {
    const writes = [];
    const writeFn = (text) => writes.push(text);
    const runDoctorFn = () => ({
      status: 0,
      overall: "ok",
      sections: [{ name: "fake", status: "ok", lines: ["  ok    placeholder"] }]
    });
    const runCommandFn = () => ({ status: 0, stdout: "11.0.0", error: null });
    const probeToolVersionsFn = () => ({
      npm: { status: 0, error: null, stdout: "11.0.0", stderr: "" },
      preCommit: { status: 0, error: null, stdout: "pre-commit 4.0.0", stderr: "" },
      pwsh: { status: 0, error: null, stdout: "PowerShell 7.4.0\n", stderr: "" },
      bash: { status: 0, error: null, stdout: "GNU bash, version 5.2\n", stderr: "" }
    });

    const exitCode = main({
      writeFn,
      envCi: "1",
      envNoColor: undefined,
      envForceColor: undefined,
      isTTY: false,
      runDoctorFn,
      runCommandFn,
      probeToolVersionsFn
    });

    expect(exitCode).toBe(0);
    const joined = writes.join("");
    expect(joined).toContain("DxMessaging doctor");
    expect(joined).toContain("[ OK ] fake");
    expect(joined).toContain("Overall: [ OK ]");
  });

  test("main returns 1 when runDoctor fails", () => {
    const writes = [];
    const writeFn = (text) => writes.push(text);
    const runDoctorFn = () => ({
      status: 1,
      overall: "fail",
      sections: [{ name: "fake", status: "fail", lines: ["  FAIL  bad"] }]
    });
    const runCommandFn = () => ({ status: 0, stdout: "11.0.0", error: null });
    const probeToolVersionsFn = () => ({
      npm: { status: 0, error: null, stdout: "11.0.0", stderr: "" },
      preCommit: { status: 0, error: null, stdout: "pre-commit 4.0.0", stderr: "" },
      pwsh: { status: 0, error: null, stdout: "PowerShell 7.4.0\n", stderr: "" },
      bash: { status: 0, error: null, stdout: "GNU bash, version 5.2\n", stderr: "" }
    });

    const exitCode = main({
      writeFn,
      envCi: "1",
      envNoColor: undefined,
      envForceColor: undefined,
      isTTY: false,
      runDoctorFn,
      runCommandFn,
      probeToolVersionsFn
    });

    expect(exitCode).toBe(1);
    expect(writes.join("")).toContain("Overall: [FAIL]");
  });

  test("main calls probeToolVersionsFn exactly once (M1)", () => {
    const writes = [];
    let probeCalls = 0;
    const probeToolVersionsFn = () => {
      probeCalls += 1;
      return {
        npm: { status: 0, error: null, stdout: "11.0.0", stderr: "" },
        preCommit: { status: 0, error: null, stdout: "pre-commit 4.0.0", stderr: "" },
        pwsh: { status: 0, error: null, stdout: "PowerShell 7.4.0\n", stderr: "" },
        bash: { status: 0, error: null, stdout: "GNU bash, version 5.2\n", stderr: "" }
      };
    };
    const runDoctorFn = () => ({
      status: 0,
      overall: "ok",
      sections: [{ name: "fake", status: "ok", lines: [] }]
    });

    main({
      writeFn: (text) => writes.push(text),
      envCi: "1",
      envNoColor: undefined,
      envForceColor: undefined,
      isTTY: false,
      runDoctorFn,
      runCommandFn: () => ({ status: 0, stdout: "", error: null }),
      probeToolVersionsFn
    });

    expect(probeCalls).toBe(1);
  });
});

describe("doctor file lives next to other scripts", () => {
  test("doctor.js is located at scripts/doctor.js relative to repo root", () => {
    // Defensive: the hook regex and the preflight script entry both
    // assume the doctor lives at scripts/doctor.js. If a refactor moves
    // it, the failure should surface here, not at push time.
    const expected = path.resolve(__dirname, "../doctor.js");
    const required = require.resolve("../doctor");
    expect(required).toBe(expected);
  });
});
