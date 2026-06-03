/**
 * @fileoverview Tests for scripts/lib/node-modules-integrity.js.
 *
 * The integrity library is the single source of truth for the
 * "are critical node_modules files present + non-zero?" check that the
 * managed Jest/Prettier/cspell wrappers gate on before running anything.
 * These tests pin INTEGRITY_TARGETS shape, the probe semantics, the
 * subprocess re-probe contract, and the Windows-only zero-byte native
 * scan.
 *
 * @cross-platform-regression -- this marker requires the file to be gated on
 * ubuntu/windows/macos via the targeted step in
 * .github/workflows/cross-platform-preflight.yml; enforced by
 * scripts/__tests__/cross-platform-preflight-coverage.test.js.
 */

"use strict";

const path = require("path");

const {
  INTEGRITY_TARGETS,
  DEFAULT_LOADABLE_REL_PATHS,
  probeIntegrity,
  probeIntegrityInSubprocess,
  findZeroByteNativeBinaries,
  formatIntegrityFailure,
  probeResolverHealth
} = require("../lib/node-modules-integrity");
const { toPosixPath } = require("../lib/path-classifier");

describe("INTEGRITY_TARGETS", () => {
  test("is frozen and immutable end-to-end", () => {
    expect(Object.isFrozen(INTEGRITY_TARGETS)).toBe(true);
    for (const target of INTEGRITY_TARGETS) {
      expect(Object.isFrozen(target)).toBe(true);
      expect(Object.isFrozen(target.files)).toBe(true);
      for (const file of target.files) {
        expect(Object.isFrozen(file)).toBe(true);
      }
    }
  });

  test("contains entries for prettier, markdownlint-cli2, cspell, jest, jest-circus", () => {
    const tools = INTEGRITY_TARGETS.map((target) => target.tool);
    expect(tools).toEqual(
      expect.arrayContaining(["prettier", "markdownlint-cli2", "cspell", "jest", "jest-circus"])
    );
    expect(tools.length).toBe(5);
  });

  test("cspell entry covers both the CLI and the cspell-lib API used by edit-time guards", () => {
    const cspell = INTEGRITY_TARGETS.find((target) => target.tool === "cspell");
    expect(cspell).toBeTruthy();
    expect(cspell.files.map((file) => file.relPath)).toEqual(
      expect.arrayContaining([
        "node_modules/cspell/bin.mjs",
        "node_modules/cspell/dist/esm/app.js",
        "node_modules/cspell-lib/dist/index.js"
      ])
    );
  });

  test("every file entry has a relPath and a numeric minBytes", () => {
    for (const target of INTEGRITY_TARGETS) {
      expect(target.files.length).toBeGreaterThan(0);
      for (const file of target.files) {
        expect(typeof file.relPath).toBe("string");
        expect(file.relPath.length).toBeGreaterThan(0);
        expect(file.relPath.startsWith("node_modules/")).toBe(true);
        expect(typeof file.minBytes).toBe("number");
        expect(file.minBytes).toBeGreaterThan(0);
      }
    }
  });

  test("jest-circus entry includes the runner.js + index.js + jestAdapterInit.js triad (verified on-disk)", () => {
    const jestCircus = INTEGRITY_TARGETS.find((t) => t.tool === "jest-circus");
    expect(jestCircus).toBeTruthy();
    const relPaths = jestCircus.files.map((f) => f.relPath);
    expect(relPaths).toEqual(
      expect.arrayContaining([
        "node_modules/jest-circus/build/runner.js",
        "node_modules/jest-circus/build/index.js",
        "node_modules/jest-circus/build/jestAdapterInit.js"
      ])
    );
  });

  test("cannot be mutated by consumers", () => {
    expect(() => {
      INTEGRITY_TARGETS.push({ tool: "malicious" });
    }).toThrow();
    expect(INTEGRITY_TARGETS.length).toBe(5);
  });
});

describe("probeIntegrity", () => {
  const fakeTargets = [
    {
      tool: "alpha",
      files: [
        { relPath: "node_modules/alpha/index.js", minBytes: 1 },
        { relPath: "node_modules/alpha/bin/alpha.js", minBytes: 1 }
      ]
    },
    {
      tool: "beta",
      files: [{ relPath: "node_modules/beta/bin.mjs", minBytes: 1 }]
    }
  ];

  test("returns ok=true with empty missing[] when every file is present and non-empty", () => {
    const result = probeIntegrity({
      repoRoot: "/repo",
      existsSyncFn: () => true,
      statSyncFn: () => ({ size: 100 }),
      targets: fakeTargets
    });

    expect(result.ok).toBe(true);
    expect(result.missing).toEqual([]);
  });

  test("flags a missing file via existsSync=false", () => {
    const result = probeIntegrity({
      repoRoot: "/repo",
      // POSIX-normalize before substring check so the fixture is
      // platform-agnostic (on Windows, path.join uses "\").
      existsSyncFn: (abs) => !toPosixPath(abs).endsWith("alpha.js"),
      statSyncFn: () => ({ size: 100 }),
      targets: fakeTargets
    });

    expect(result.ok).toBe(false);
    expect(result.missing).toEqual([
      { tool: "alpha", relPath: "node_modules/alpha/bin/alpha.js", reason: "missing" }
    ]);
  });

  test("flags a missing file via statSync ENOENT", () => {
    const result = probeIntegrity({
      repoRoot: "/repo",
      existsSyncFn: () => true,
      statSyncFn: (abs) => {
        if (toPosixPath(abs).endsWith("bin.mjs")) {
          const err = new Error("ENOENT: file gone");
          err.code = "ENOENT";
          throw err;
        }
        return { size: 100 };
      },
      targets: fakeTargets
    });

    expect(result.ok).toBe(false);
    expect(result.missing.find((m) => m.tool === "beta")).toEqual({
      tool: "beta",
      relPath: "node_modules/beta/bin.mjs",
      reason: "missing"
    });
  });

  test("flags zero-byte file as empty (existsSync true, size 0)", () => {
    const result = probeIntegrity({
      repoRoot: "/repo",
      existsSyncFn: () => true,
      statSyncFn: (abs) => (toPosixPath(abs).endsWith("index.js") ? { size: 0 } : { size: 100 }),
      targets: fakeTargets
    });

    expect(result.ok).toBe(false);
    expect(result.missing).toEqual([
      { tool: "alpha", relPath: "node_modules/alpha/index.js", reason: "empty" }
    ]);
  });

  test("throws when repoRoot is missing", () => {
    expect(() => probeIntegrity({ targets: fakeTargets })).toThrow(/repoRoot/);
    expect(() => probeIntegrity({ repoRoot: "", targets: fakeTargets })).toThrow();
  });

  test("default targets reference INTEGRITY_TARGETS when none provided", () => {
    // Spy: statSyncFn is invoked for each file in INTEGRITY_TARGETS.
    const seenPaths = new Set();
    probeIntegrity({
      repoRoot: "/repo",
      existsSyncFn: () => true,
      statSyncFn: (abs) => {
        seenPaths.add(abs);
        return { size: 100 };
      }
    });
    const totalFiles = INTEGRITY_TARGETS.reduce((sum, target) => sum + target.files.length, 0);
    expect(seenPaths.size).toBe(totalFiles);
  });

  test("non-ENOENT stat errors map to 'empty' (defense-in-depth)", () => {
    const result = probeIntegrity({
      repoRoot: "/repo",
      existsSyncFn: () => true,
      statSyncFn: () => {
        const err = new Error("EACCES");
        err.code = "EACCES";
        throw err;
      },
      targets: [{ tool: "alpha", files: [{ relPath: "node_modules/alpha/index.js", minBytes: 1 }] }]
    });

    expect(result.ok).toBe(false);
    expect(result.missing[0].reason).toBe("empty");
  });
});

describe("probeIntegrityInSubprocess", () => {
  test("invokes spawnSyncFn with node -e <inline-script> and parses stdout JSON", () => {
    let capturedCommand;
    let capturedArgs;
    const spawnSyncFn = (command, args) => {
      capturedCommand = command;
      capturedArgs = args;
      return {
        status: 0,
        error: null,
        stdout: JSON.stringify({ ok: true, missing: [] }),
        stderr: ""
      };
    };

    const result = probeIntegrityInSubprocess({
      repoRoot: "/repo",
      execPath: "/usr/bin/node",
      spawnSyncFn
    });

    expect(capturedCommand).toBe("/usr/bin/node");
    expect(Array.isArray(capturedArgs)).toBe(true);
    expect(capturedArgs[0]).toBe("-e");
    expect(typeof capturedArgs[1]).toBe("string");
    expect(capturedArgs[1]).toContain("probeIntegrity");
    expect(capturedArgs[1]).toContain('"/repo"');
    expect(result).toEqual({ ok: true, missing: [] });
  });

  test("spawns the child with cwd set to repoRoot", () => {
    // Defense in depth: an inline script that prints JSON to stdout
    // does not strictly require a cwd, but pinning it to repoRoot
    // prevents any future addition that relies on relative paths
    // (or a Node policy file lookup) from drifting.
    let capturedOptions;
    const spawnSyncFn = (command, args, options) => {
      capturedOptions = options;
      return {
        status: 0,
        error: null,
        stdout: JSON.stringify({ ok: true, missing: [] }),
        stderr: ""
      };
    };
    probeIntegrityInSubprocess({
      repoRoot: "/repo",
      execPath: "/usr/bin/node",
      spawnSyncFn
    });
    expect(capturedOptions).toEqual(expect.objectContaining({ cwd: "/repo" }));
  });

  test("inline subprocess script opts into strict mode", () => {
    // Cosmetic consistency with the parent file's "use strict"; pin
    // it so a future inline-script rewrite cannot silently drop it.
    let capturedArgs;
    probeIntegrityInSubprocess({
      repoRoot: "/repo",
      execPath: "/usr/bin/node",
      spawnSyncFn: (_command, args) => {
        capturedArgs = args;
        return {
          status: 0,
          error: null,
          stdout: JSON.stringify({ ok: true, missing: [] }),
          stderr: ""
        };
      }
    });
    expect(capturedArgs[1]).toMatch(/^"use strict";/);
  });

  test("returns a synthetic failure when subprocess prints malformed JSON", () => {
    const result = probeIntegrityInSubprocess({
      repoRoot: "/repo",
      spawnSyncFn: () => ({ status: 0, error: null, stdout: "not json", stderr: "" })
    });

    expect(result.ok).toBe(false);
    expect(result.missing).toEqual([
      expect.objectContaining({
        tool: "(subprocess)",
        reason: expect.stringContaining("JSON parse failed")
      })
    ]);
  });

  test("returns a synthetic failure when subprocess exits non-zero", () => {
    const result = probeIntegrityInSubprocess({
      repoRoot: "/repo",
      spawnSyncFn: () => ({
        status: 1,
        error: null,
        stdout: "",
        stderr: "Error: blah"
      })
    });

    expect(result.ok).toBe(false);
    expect(result.missing[0].reason).toContain("exit=1");
    expect(result.missing[0].reason).toContain("Error: blah");
  });

  test("returns a synthetic failure when spawn itself errors", () => {
    const result = probeIntegrityInSubprocess({
      repoRoot: "/repo",
      spawnSyncFn: () => ({
        status: null,
        error: { message: "ENOENT" },
        stdout: "",
        stderr: ""
      })
    });

    expect(result.ok).toBe(false);
    expect(result.missing[0].reason).toContain("spawn failed");
  });

  test("throws when repoRoot is missing", () => {
    expect(() => probeIntegrityInSubprocess({})).toThrow(/repoRoot/);
  });

  test("merges zeroByteNativeBinaries into missing[] when subprocess reports them", () => {
    // The first-pass in-process gate scans for AV-truncated *.node
    // binaries on Windows; the subprocess re-probe (after npm ci) must
    // do the same, otherwise a truncated native binding mid-rewrite
    // would falsely report ok. We fake a subprocess JSON shape that
    // includes the zeroByteNativeBinaries field and assert the parent
    // surfaces it under missing[] using the same <native-binding>
    // shape as the in-process gate.
    const subprocessJson = JSON.stringify({
      ok: true,
      missing: [],
      zeroByteNativeBinaries: ["node_modules/fake-native-binding/build/Release/binding.node"]
    });
    const result = probeIntegrityInSubprocess({
      repoRoot: "/repo",
      spawnSyncFn: () => ({
        status: 0,
        error: null,
        stdout: subprocessJson,
        stderr: ""
      })
    });
    expect(result.ok).toBe(false);
    expect(result.missing).toEqual([
      expect.objectContaining({
        tool: "<native-binding>",
        relPath: "node_modules/fake-native-binding/build/Release/binding.node",
        reason: "zero-byte"
      })
    ]);
  });

  test("preserves ok=true when subprocess emits empty zeroByteNativeBinaries", () => {
    // Backwards-compat: a subprocess JSON shape that includes the
    // zeroByteNativeBinaries field but with no offenders must not flip
    // ok=true to ok=false. This pins the desired behavior for the
    // common (healthy) case after npm ci recovery.
    const subprocessJson = JSON.stringify({
      ok: true,
      missing: [],
      zeroByteNativeBinaries: []
    });
    const result = probeIntegrityInSubprocess({
      repoRoot: "/repo",
      spawnSyncFn: () => ({
        status: 0,
        error: null,
        stdout: subprocessJson,
        stderr: ""
      })
    });
    expect(result).toEqual({ ok: true, missing: [] });
  });

  test("inline subprocess script calls findZeroByteNativeBinaries on win32 only", () => {
    // Pin the inline script wiring so a future rewrite cannot silently
    // drop the Windows zero-byte scan from the re-probe.
    let capturedArgs;
    probeIntegrityInSubprocess({
      repoRoot: "/repo",
      execPath: "/usr/bin/node",
      spawnSyncFn: (_command, args) => {
        capturedArgs = args;
        return {
          status: 0,
          error: null,
          stdout: JSON.stringify({
            ok: true,
            missing: [],
            zeroByteNativeBinaries: []
          }),
          stderr: ""
        };
      }
    });
    expect(capturedArgs[1]).toContain("findZeroByteNativeBinaries");
    expect(capturedArgs[1]).toContain('"win32"');
    expect(capturedArgs[1]).toContain("zeroByteNativeBinaries");
  });

  test("end-to-end: invokes the real child process and produces a parseable result", () => {
    // Integration smoke against the real repo. This exercises the full
    // wiring: parent serializes inline script, child requires the
    // integrity module + calls probeIntegrity, parent parses JSON.
    const realRepoRoot = path.resolve(__dirname, "..", "..");
    const result = probeIntegrityInSubprocess({ repoRoot: realRepoRoot });
    expect(typeof result.ok).toBe("boolean");
    expect(Array.isArray(result.missing)).toBe(true);
    // On a healthy repo the result should be ok; we assert the shape
    // rather than ok=true so the test is robust across mid-implementation
    // states.
    if (!result.ok) {
      for (const entry of result.missing) {
        expect(entry).toEqual(
          expect.objectContaining({
            tool: expect.any(String),
            relPath: expect.any(String),
            reason: expect.any(String)
          })
        );
      }
    }
  });
});

describe("findZeroByteNativeBinaries", () => {
  test("returns [] on Linux/macOS without walking", () => {
    let walked = false;
    const result = findZeroByteNativeBinaries({
      repoRoot: "/repo",
      readdirSyncFn: () => {
        walked = true;
        return [];
      },
      statSyncFn: () => ({ size: 0 }),
      platform: "linux"
    });
    expect(result).toEqual([]);
    expect(walked).toBe(false);
  });

  test("returns [] when skip=true even on Windows", () => {
    let walked = false;
    const result = findZeroByteNativeBinaries({
      repoRoot: "/repo",
      readdirSyncFn: () => {
        walked = true;
        return [];
      },
      statSyncFn: () => ({ size: 0 }),
      platform: "win32",
      skip: true
    });
    expect(result).toEqual([]);
    expect(walked).toBe(false);
  });

  test("returns offenders on Windows when *.node files have size 0", () => {
    // Fake one-level fs: node_modules has a child "native" with a 0-byte
    // index.node and a healthy 100-byte sibling.node.
    //
    // The fixture is keyed in POSIX form so the test runs identically on
    // Linux and Windows. The injected readdirSyncFn / statSyncFn
    // normalize the (platform-native) absolute path the production code
    // hands them before fixture lookup.
    const tree = {
      "/repo/node_modules": [{ name: "native", isDirectory: () => true, isFile: () => false }],
      "/repo/node_modules/native": [
        { name: "index.node", isDirectory: () => false, isFile: () => true },
        { name: "sibling.node", isDirectory: () => false, isFile: () => true }
      ]
    };
    const sizes = {
      "/repo/node_modules/native/index.node": 0,
      "/repo/node_modules/native/sibling.node": 100
    };
    const result = findZeroByteNativeBinaries({
      repoRoot: "/repo",
      platform: "win32",
      readdirSyncFn: (dir) => tree[toPosixPath(dir)] || [],
      statSyncFn: (abs) => {
        const key = toPosixPath(abs);
        return { size: sizes[key] !== undefined ? sizes[key] : 100 };
      }
    });

    expect(result).toEqual(["node_modules/native/index.node"]);
  });

  test("respects maxDepth to keep the scan bounded", () => {
    const visited = new Set();
    const tree = (dir) => {
      visited.add(dir);
      return [{ name: "deeper", isDirectory: () => true, isFile: () => false }];
    };
    findZeroByteNativeBinaries({
      repoRoot: "/repo",
      platform: "win32",
      readdirSyncFn: tree,
      statSyncFn: () => ({ size: 0 }),
      maxDepth: 2
    });
    // Depth budget 2 means at most 3 readdir calls (root, level1, level2).
    expect(visited.size).toBeLessThanOrEqual(3);
  });
});

describe("formatIntegrityFailure", () => {
  test("produces a stable single-line format", () => {
    const formatted = formatIntegrityFailure({
      ok: false,
      missing: [
        {
          tool: "jest-circus",
          relPath: "node_modules/jest-circus/build/runner.js",
          reason: "missing"
        }
      ]
    });
    expect(formatted).toBe(
      "Integrity probe failed: missing node_modules/jest-circus/build/runner.js (missing) for jest-circus"
    );
    // Single line - no embedded newlines.
    expect(formatted.includes("\n")).toBe(false);
  });

  test("reports the count of remaining offenders when more than one is missing", () => {
    const formatted = formatIntegrityFailure({
      ok: false,
      missing: [
        { tool: "jest-circus", relPath: "a", reason: "missing" },
        { tool: "jest-circus", relPath: "b", reason: "missing" },
        { tool: "prettier", relPath: "c", reason: "empty" }
      ]
    });
    expect(formatted).toContain("2 more");
  });

  test("handles empty / malformed input defensively", () => {
    expect(formatIntegrityFailure(null)).toContain("no detail available");
    expect(formatIntegrityFailure({})).toContain("no detail available");
    expect(formatIntegrityFailure({ ok: true, missing: [] })).toContain("no detail available");
  });

  test("POSIX-normalizes Windows-flavored relPath in the output", () => {
    const formatted = formatIntegrityFailure({
      ok: false,
      missing: [
        {
          tool: "jest-circus",
          relPath: "node_modules\\jest-circus\\build\\runner.js",
          reason: "missing"
        }
      ]
    });
    expect(formatted).toContain("node_modules/jest-circus/build/runner.js");
    expect(formatted).not.toContain("\\");
  });
});

describe("probeResolverHealth", () => {
  test("returns ok when all specifiers resolve (subprocess emits ok:true)", () => {
    const spawnSyncFn = jest.fn(() => ({
      status: 0,
      stdout: JSON.stringify({ ok: true, failures: [] }),
      stderr: ""
    }));
    const result = probeResolverHealth({
      repoRoot: "/repo",
      spawnSyncFn,
      specifiers: ["jest-circus/runner"]
    });
    expect(result).toEqual({ ok: true, failures: [] });
    expect(spawnSyncFn).toHaveBeenCalledTimes(1);
    const [, args, opts] = spawnSyncFn.mock.calls[0];
    expect(args[0]).toBe("-e");
    // Verify the inline script encodes both repoRoot and specifiers
    // via JSON.stringify (defense against injection).
    expect(args[1]).toContain("JSON.stringify");
    expect(args[1]).toContain('"/repo"');
    expect(args[1]).toContain('"jest-circus/runner"');
    for (const relPath of DEFAULT_LOADABLE_REL_PATHS) {
      expect(args[1]).toContain(relPath);
    }
    expect(opts.cwd).toBe("/repo");
  });

  test("surfaces failures emitted by the subprocess JSON payload", () => {
    const spawnSyncFn = jest.fn(() => ({
      status: 0,
      stdout: JSON.stringify({
        ok: false,
        failures: [
          {
            specifier: "jest-circus/runner",
            error: "Failed to load native binding: @unrs/resolver-binding-win32-x64-msvc"
          }
        ]
      }),
      stderr: ""
    }));
    const result = probeResolverHealth({
      repoRoot: "/repo",
      spawnSyncFn
    });
    expect(result.ok).toBe(false);
    expect(result.failures).toHaveLength(1);
    expect(result.failures[0].specifier).toBe("jest-circus/runner");
    expect(result.failures[0].error).toContain("native binding");
  });

  test("returns synthetic failure when subprocess exits non-zero", () => {
    const spawnSyncFn = jest.fn(() => ({
      status: 1,
      stdout: "",
      stderr: "node: boom"
    }));
    const result = probeResolverHealth({
      repoRoot: "/repo",
      spawnSyncFn
    });
    expect(result.ok).toBe(false);
    expect(result.failures).toHaveLength(1);
    expect(result.failures[0].specifier).toBe("<subprocess>");
    expect(result.failures[0].error).toContain("exit=1");
    expect(result.failures[0].error).toContain("boom");
  });

  test("returns synthetic failure when subprocess emits malformed JSON", () => {
    const spawnSyncFn = jest.fn(() => ({
      status: 0,
      stdout: "not-json",
      stderr: ""
    }));
    const result = probeResolverHealth({
      repoRoot: "/repo",
      spawnSyncFn
    });
    expect(result.ok).toBe(false);
    expect(result.failures).toHaveLength(1);
    expect(result.failures[0].specifier).toBe("<subprocess>");
    expect(result.failures[0].error).toContain("malformed probe output");
  });

  test("returns synthetic failure when subprocess returns null result", () => {
    const spawnSyncFn = jest.fn(() => null);
    const result = probeResolverHealth({
      repoRoot: "/repo",
      spawnSyncFn
    });
    expect(result.ok).toBe(false);
    expect(result.failures[0].error).toContain("spawn returned null");
  });

  test("returns synthetic failure when spawn itself throws", () => {
    const spawnSyncFn = jest.fn(() => {
      throw new Error("ENOENT: node not found");
    });
    const result = probeResolverHealth({
      repoRoot: "/repo",
      spawnSyncFn
    });
    expect(result.ok).toBe(false);
    expect(result.failures[0].error).toContain("spawn threw");
    expect(result.failures[0].error).toContain("ENOENT");
  });

  test("returns synthetic failure when stdout is empty (ok exit)", () => {
    const spawnSyncFn = jest.fn(() => ({ status: 0, stdout: "", stderr: "" }));
    const result = probeResolverHealth({
      repoRoot: "/repo",
      spawnSyncFn
    });
    expect(result.ok).toBe(false);
    expect(result.failures[0].error).toContain("empty stdout");
  });

  test("throws when repoRoot is missing", () => {
    expect(() => probeResolverHealth({})).toThrow(/repoRoot/);
    expect(() => probeResolverHealth({ repoRoot: "" })).toThrow();
  });

  test("end-to-end: probes the real repo successfully", () => {
    // Spawns a real Node subprocess against this repo's actual root.
    // If the real jest-circus install is healthy (it must be, since the
    // test runner is jest-circus), this returns ok:true.
    const result = probeResolverHealth({
      repoRoot: path.resolve(__dirname, "../..")
    });
    expect(result.ok).toBe(true);
    expect(result.failures).toEqual([]);
  });

  test("surfaces unrs-resolver load failures emitted by the subprocess (C1 regression)", () => {
    // The Windows failure mode: the subprocess reports that
    // require("unrs-resolver") threw because the native binding is
    // missing or corrupt. The parent must propagate that failure with
    // ok=false and the unrs-resolver specifier preserved.
    const spawnSyncFn = jest.fn(() => ({
      status: 0,
      stdout: JSON.stringify({
        ok: false,
        failures: [
          {
            specifier: "unrs-resolver",
            error: "Cannot find native binding. (@unrs/resolver-binding-win32-x64-msvc)"
          }
        ]
      }),
      stderr: ""
    }));
    const result = probeResolverHealth({
      repoRoot: "/repo",
      spawnSyncFn
    });
    expect(result.ok).toBe(false);
    expect(result.failures).toHaveLength(1);
    expect(result.failures[0].specifier).toBe("unrs-resolver");
    expect(result.failures[0].error).toContain("native binding");
  });

  test("contract: inline script source MUST mention unrs-resolver (load-bearing invariant)", () => {
    // The whole point of the resolver probe is to exercise unrs-resolver
    // (and via fallback, jest-resolve) — Node's own require.resolve will
    // happily succeed on a Windows install with a broken native binding
    // because the JS files are present on disk. This test pins the
    // contract by inspecting the inline script source the parent hands
    // to spawnSync, so a future refactor that accidentally strips the
    // unrs-resolver load chain will fail loudly here instead of silently
    // shipping a no-op probe to the Windows developer.
    let capturedArgs;
    probeResolverHealth({
      repoRoot: "/repo",
      spawnSyncFn: (_command, args) => {
        capturedArgs = args;
        return {
          status: 0,
          stdout: JSON.stringify({ ok: true, failures: [] }),
          stderr: ""
        };
      }
    });
    expect(capturedArgs).toBeTruthy();
    expect(capturedArgs[0]).toBe("-e");
    const script = capturedArgs[1];
    expect(typeof script).toBe("string");
    // The literal "unrs-resolver" token MUST appear; this is the
    // load-bearing invariant the user reviewer flagged.
    expect(script).toContain("unrs-resolver");
    // ResolverFactory + sync must also appear: the probe must
    // INSTANTIATE the factory and CALL sync(), not just require the
    // module (a half-loaded binding can survive require but throw at
    // sync time).
    expect(script).toContain("ResolverFactory");
    expect(script).toContain("sync");
    // jest-resolve fallback is wired in so a repo whose tree does not
    // directly list unrs-resolver still exercises the failure surface.
    expect(script).toContain("jest-resolve");
    // The cspell edit-time and native wrapper paths also need module-load
    // coverage, not just non-empty file checks.
    expect(script).toContain("loadableRelPaths");
    expect(script).toContain("module-load-throw");
    expect(script).toContain("node_modules/cspell-lib/dist/index.js");
    expect(script).toContain("node_modules/cspell/dist/esm/app.js");
  });

  test("surfaces cspell module-load failures emitted by the subprocess", () => {
    const spawnSyncFn = jest.fn(() => ({
      status: 0,
      stdout: JSON.stringify({
        ok: false,
        failures: [
          {
            specifier: "node_modules/cspell-lib/dist/index.js",
            error: "module-load-throw: SyntaxError: Unexpected token"
          }
        ]
      }),
      stderr: ""
    }));
    const result = probeResolverHealth({
      repoRoot: "/repo",
      spawnSyncFn
    });
    expect(result.ok).toBe(false);
    expect(result.failures[0].specifier).toBe("node_modules/cspell-lib/dist/index.js");
    expect(result.failures[0].error).toContain("module-load-throw");
  });

  test("end-to-end: simulated broken native binding via SKIP_UNRS_RESOLVER_FALLBACK is surfaced", () => {
    // The real Windows failure happens BEFORE napi-postinstall's
    // auto-fallback (which itself is a partial mitigation on the
    // Windows machines that hit this class of bug). We can simulate
    // the post-fallback failure deterministically on Linux by setting
    // SKIP_UNRS_RESOLVER_FALLBACK=1 and removing the native binding
    // from node_modules/@unrs/. The simpler integration approach:
    // construct a synthetic repoRoot where unrs-resolver MUST throw on
    // load. We approximate by pointing repoRoot at a directory whose
    // package.json does not resolve unrs-resolver -- the probe should
    // then push a 'unrs-resolver' failure with MODULE_NOT_FOUND.
    const os = require("os");
    const fs = require("fs");
    const tmpRepo = fs.mkdtempSync(path.join(os.tmpdir(), "resolver-probe-fixture-"));
    try {
      // Bare repo: package.json + empty node_modules. No unrs-resolver
      // installed -> the probe's repoRequire('unrs-resolver') throws
      // MODULE_NOT_FOUND, which the probe surfaces as a failure with
      // specifier 'unrs-resolver'.
      fs.writeFileSync(
        path.join(tmpRepo, "package.json"),
        JSON.stringify({ name: "fixture", version: "0.0.0" })
      );
      fs.mkdirSync(path.join(tmpRepo, "node_modules"));
      const result = probeResolverHealth({ repoRoot: tmpRepo });
      expect(result.ok).toBe(false);
      const unrsFailure = result.failures.find((f) => f.specifier === "unrs-resolver");
      expect(unrsFailure).toBeTruthy();
      expect(unrsFailure.error).toMatch(/MODULE_NOT_FOUND|Cannot find module/);
    } finally {
      fs.rmSync(tmpRepo, { recursive: true, force: true });
    }
  });
});
