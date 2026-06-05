/**
 * @fileoverview Tests for the dependency-version-drift category killer:
 *   - scripts/lib/dependency-version-parity.js   (offline detector)
 *   - scripts/lib/dependency-drift-recovery.js    (npm install reconcile)
 *   - wiring into validate-node-tooling, repair-node-tooling, the doctor,
 *     and the post-edit guard dispatch table.
 *   - the LIVE invariant on the real repo (the test that fails if anyone
 *     pushes a manifest/installed version drift -- e.g. the cspell-lib
 *     10.0.0-vs-10.0.1 failure that motivated this).
 *
 * These checks are intentionally data-driven with injected fakes so they
 * exercise behavior, not the host machine's node_modules.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const parity = require("../lib/dependency-version-parity");
const {
  classifyPin,
  readInstalledVersion,
  readLockfileVersion,
  probeDependencyVersionParity,
  formatDriftLines
} = parity;

const REPO_ROOT = path.resolve(__dirname, "..", "..");

/**
 * Build an in-memory fs facade for the detector. `installed` maps package
 * name -> version string (or the literal "MALFORMED" to simulate an
 * unparseable manifest, or absent to simulate not-installed).
 */
function makeFakeFs(installed) {
  const files = new Map();
  for (const [name, version] of Object.entries(installed)) {
    const manifestPath = path.join(REPO_ROOT, "node_modules", ...name.split("/"), "package.json");
    files.set(manifestPath, version === "MALFORMED" ? "{not json" : JSON.stringify({ version }));
  }
  return {
    existsSyncFn: (p) => files.has(p),
    readFileSyncFn: (p) => {
      if (!files.has(p)) {
        const err = new Error(`ENOENT: ${p}`);
        err.code = "ENOENT";
        throw err;
      }
      return files.get(p);
    }
  };
}

function probe({ declared, installed = {}, lockfile = null, semverSatisfiesFn }) {
  const fake = makeFakeFs(installed);
  return probeDependencyVersionParity({
    repoRoot: REPO_ROOT,
    packageJson: { devDependencies: declared },
    lockfile,
    readFileSyncFn: fake.readFileSyncFn,
    existsSyncFn: fake.existsSyncFn,
    semverSatisfiesFn
  });
}

describe("classifyPin", () => {
  test.each([
    ["10.0.1", "exact"],
    ["3.8.3", "exact"],
    ["1.2.3-beta.1", "exact"],
    ["1.2.3+build.5", "exact"],
    ["1.2.3-rc.1+build.9", "exact"],
    ["1.0.0-alpha+exp.sha.5114f85", "exact"],
    ["^30.4.2", "range"],
    ["~1.2.3", "range"],
    [">=1.0.0", "range"],
    ["1.x", "range"],
    ["1.2.x", "range"],
    ["*", "range"],
    ["x", "range"],
    ["git+https://example.com/x.git", "unversioned"],
    ["file:../local", "unversioned"],
    ["workspace:*", "unversioned"],
    ["npm:other@1.2.3", "unversioned"],
    ["link:../x", "unversioned"]
  ])("classifies %s as %s", (spec, expected) => {
    expect(classifyPin(spec)).toBe(expected);
  });

  test("non-string is unversioned", () => {
    expect(classifyPin(undefined)).toBe("unversioned");
    expect(classifyPin(null)).toBe("unversioned");
  });
});

describe("readInstalledVersion", () => {
  test("reads scoped + unscoped names off disk, bypassing exports maps", () => {
    const fake = makeFakeFs({ cspell: "10.0.1", "@scope/pkg": "2.0.0" });
    expect(readInstalledVersion({ repoRoot: REPO_ROOT, name: "cspell", ...fake })).toBe("10.0.1");
    expect(readInstalledVersion({ repoRoot: REPO_ROOT, name: "@scope/pkg", ...fake })).toBe(
      "2.0.0"
    );
  });

  test("absent package -> null", () => {
    const fake = makeFakeFs({});
    expect(readInstalledVersion({ repoRoot: REPO_ROOT, name: "ghost", ...fake })).toBeNull();
  });

  test("malformed manifest -> null (never throws)", () => {
    const fake = makeFakeFs({ broken: "MALFORMED" });
    expect(readInstalledVersion({ repoRoot: REPO_ROOT, name: "broken", ...fake })).toBeNull();
  });
});

describe("readLockfileVersion", () => {
  test("undefined when no lockfile / no packages map", () => {
    expect(readLockfileVersion(null, "cspell")).toBeUndefined();
    expect(readLockfileVersion({}, "cspell")).toBeUndefined();
  });
  test("version from v7+ packages map", () => {
    const lock = { packages: { "node_modules/cspell": { version: "10.0.1" } } };
    expect(readLockfileVersion(lock, "cspell")).toBe("10.0.1");
  });
  test("null when lockfile present but no entry", () => {
    expect(readLockfileVersion({ packages: {} }, "cspell")).toBeNull();
  });
});

describe("probeDependencyVersionParity (detector behavior)", () => {
  test("exact pin matches installed -> ok", () => {
    const r = probe({ declared: { cspell: "10.0.1" }, installed: { cspell: "10.0.1" } });
    expect(r.ok).toBe(true);
    expect(r.checked).toBe(1);
  });

  test("exact pin installed mismatch -> drift installed-mismatch (the cspell-lib case)", () => {
    const r = probe({
      declared: { "cspell-lib": "10.0.1" },
      installed: { "cspell-lib": "10.0.0" }
    });
    expect(r.ok).toBe(false);
    expect(r.drifted).toHaveLength(1);
    expect(r.drifted[0]).toMatchObject({
      name: "cspell-lib",
      declared: "10.0.1",
      installed: "10.0.0",
      reason: "installed-mismatch"
    });
  });

  test("exact pin not installed -> drift not-installed", () => {
    const r = probe({ declared: { cspell: "10.0.1" }, installed: {} });
    expect(r.ok).toBe(false);
    expect(r.drifted[0]).toMatchObject({ reason: "not-installed", installed: null });
  });

  test("installed correct but lockfile stale -> drift lockfile-stale (npm ci would re-cement)", () => {
    const r = probe({
      declared: { cspell: "10.0.1" },
      installed: { cspell: "10.0.1" },
      lockfile: { packages: { "node_modules/cspell": { version: "10.0.0" } } }
    });
    expect(r.ok).toBe(false);
    expect(r.drifted[0]).toMatchObject({
      reason: "lockfile-stale",
      installed: "10.0.1",
      lockfile: "10.0.0"
    });
  });

  test("installed correct, lockfile matches -> ok", () => {
    const r = probe({
      declared: { cspell: "10.0.1" },
      installed: { cspell: "10.0.1" },
      lockfile: { packages: { "node_modules/cspell": { version: "10.0.1" } } }
    });
    expect(r.ok).toBe(true);
  });

  test("installed correct, lockfile present-but-no-entry -> ok (cannot judge)", () => {
    const r = probe({
      declared: { cspell: "10.0.1" },
      installed: { cspell: "10.0.1" },
      lockfile: { packages: {} }
    });
    expect(r.ok).toBe(true);
  });

  test("range pin satisfied -> ok (injected semver)", () => {
    const r = probe({
      declared: { jest: "^30.4.2" },
      installed: { jest: "30.5.0" },
      semverSatisfiesFn: () => true
    });
    expect(r.ok).toBe(true);
  });

  test("range pin unsatisfied -> drift range-unsatisfied", () => {
    const r = probe({
      declared: { jest: "^30.4.2" },
      installed: { jest: "29.0.0" },
      semverSatisfiesFn: () => false
    });
    expect(r.ok).toBe(false);
    expect(r.drifted[0]).toMatchObject({ reason: "range-unsatisfied", kind: "range" });
  });

  test("range pin not installed -> drift not-installed", () => {
    const r = probe({
      declared: { jest: "^30.4.2" },
      installed: {},
      semverSatisfiesFn: () => true
    });
    expect(r.ok).toBe(false);
    expect(r.drifted[0]).toMatchObject({ reason: "not-installed" });
  });

  test("range pin with semver unavailable degrades to present-is-ok (no false drift)", () => {
    const r = probe({
      declared: { jest: "^30.4.2" },
      installed: { jest: "1.0.0" },
      semverSatisfiesFn: null
    });
    expect(r.ok).toBe(true);
  });

  test("unversioned specs are skipped (not counted, never drift)", () => {
    const r = probe({
      declared: { a: "workspace:*", b: "file:../x", c: "git+https://e/x.git" },
      installed: {}
    });
    expect(r.checked).toBe(0);
    expect(r.ok).toBe(true);
  });

  test("malformed installed manifest -> not-installed drift", () => {
    const r = probe({ declared: { cspell: "10.0.1" }, installed: { cspell: "MALFORMED" } });
    expect(r.ok).toBe(false);
    expect(r.drifted[0]).toMatchObject({ reason: "not-installed" });
  });

  test("merges dependencies + devDependencies", () => {
    const fake = makeFakeFs({ a: "1.0.0", b: "2.0.0" });
    const r = probeDependencyVersionParity({
      repoRoot: REPO_ROOT,
      packageJson: { dependencies: { a: "1.0.0" }, devDependencies: { b: "2.0.0" } },
      lockfile: null,
      ...fake
    });
    expect(r.checked).toBe(2);
    expect(r.ok).toBe(true);
  });
});

describe("formatDriftLines", () => {
  test("renders one platform-agnostic line per drift", () => {
    const lines = formatDriftLines({
      drifted: [
        {
          name: "cspell-lib",
          kind: "exact",
          declared: "10.0.1",
          installed: "10.0.0",
          lockfile: "10.0.0",
          reason: "installed-mismatch"
        },
        {
          name: "x",
          kind: "exact",
          declared: "1.0.0",
          installed: null,
          lockfile: undefined,
          reason: "not-installed"
        }
      ]
    });
    expect(lines[0]).toContain("cspell-lib: declared 10.0.1");
    expect(lines[0]).toContain("installed 10.0.0");
    expect(lines[0]).toContain("[installed-mismatch]");
    expect(lines[1]).toContain("<not installed>");
  });
  test("empty when no drift", () => {
    expect(formatDriftLines({ drifted: [] })).toEqual([]);
    expect(formatDriftLines(null)).toEqual([]);
  });
});

describe("dependency-drift-recovery (repairDependencyDrift)", () => {
  const recovery = require("../lib/dependency-drift-recovery");
  const { repairDependencyDrift, NODE_MODULES_REPAIR_LOCK_NAME } = recovery;

  function lockPassthrough(repoRoot, cb) {
    return cb();
  }

  test("no drift -> no spawn, no recovery", () => {
    const spawnFn = jest.fn();
    const r = repairDependencyDrift({
      env: {},
      probeFn: () => ({ ok: true, drifted: [], checked: 3 }),
      spawnFn,
      runWithRepairLockFn: lockPassthrough,
      warnFn: () => {}
    });
    expect(spawnFn).not.toHaveBeenCalled();
    expect(r).toMatchObject({ ok: true, recovered: false, skipped: false });
  });

  test("drift -> npm install with exact args + cwd, then re-probe ok -> recovered", () => {
    const spawnFn = jest.fn(() => ({ status: 0 }));
    const probeFn = jest
      .fn()
      .mockReturnValueOnce({
        ok: false,
        drifted: [
          {
            name: "cspell",
            reason: "installed-mismatch",
            declared: "10.0.1",
            installed: "10.0.0",
            kind: "exact"
          }
        ]
      })
      .mockReturnValueOnce({ ok: true, drifted: [] });
    const r = repairDependencyDrift({
      env: {},
      repoRoot: "/repo",
      probeFn,
      spawnFn,
      runWithRepairLockFn: lockPassthrough,
      warnFn: () => {}
    });
    expect(spawnFn).toHaveBeenCalledTimes(1);
    const [cmd, args, opts] = spawnFn.mock.calls[0];
    expect(cmd).toBe("npm");
    expect(args).toEqual(["install", "--no-audit", "--no-fund"]);
    expect(opts).toMatchObject({ cwd: "/repo", stdio: "inherit" });
    expect(r).toMatchObject({ ok: true, recovered: true });
  });

  test("DXMSG_HOOK_NO_AUTOREPAIR=1 -> skipped, no spawn", () => {
    const spawnFn = jest.fn();
    const r = repairDependencyDrift({
      env: { DXMSG_HOOK_NO_AUTOREPAIR: "1" },
      probeFn: () => ({
        ok: false,
        drifted: [
          { name: "x", reason: "installed-mismatch", declared: "1", installed: "0", kind: "exact" }
        ]
      }),
      spawnFn,
      runWithRepairLockFn: lockPassthrough,
      warnFn: () => {}
    });
    expect(spawnFn).not.toHaveBeenCalled();
    expect(r).toMatchObject({ ok: false, skipped: true });
  });

  test("npm install failure -> ok:false reason npm install failed", () => {
    const r = repairDependencyDrift({
      env: {},
      probeFn: () => ({
        ok: false,
        drifted: [
          { name: "x", reason: "installed-mismatch", declared: "1", installed: "0", kind: "exact" }
        ]
      }),
      spawnFn: () => ({ status: 1 }),
      runWithRepairLockFn: lockPassthrough,
      warnFn: () => {}
    });
    expect(r).toMatchObject({ ok: false, reason: "npm install failed" });
  });

  test("lock unavailable -> ok:false", () => {
    const r = repairDependencyDrift({
      env: {},
      probeFn: () => ({
        ok: false,
        drifted: [
          { name: "x", reason: "installed-mismatch", declared: "1", installed: "0", kind: "exact" }
        ]
      }),
      spawnFn: () => ({ status: 0 }),
      runWithRepairLockFn: () => ({ status: 1, lockFailed: true }),
      warnFn: () => {}
    });
    expect(r).toMatchObject({ ok: false, reason: "repair lock unavailable" });
  });

  test("install#1 leaves drift -> escalates: removes drifted dirs + reinstalls -> recovered", () => {
    const drift = {
      ok: false,
      drifted: [
        {
          name: "@scope/pkg",
          reason: "installed-mismatch",
          declared: "1",
          installed: "0",
          kind: "exact"
        }
      ]
    };
    const spawnFn = jest.fn(() => ({ status: 0 }));
    const removeDirSyncFn = jest.fn();
    // initial drift, after#1 still drift (triggers escalation), after#2 ok.
    const probeFn = jest
      .fn()
      .mockReturnValueOnce(drift)
      .mockReturnValueOnce(drift)
      .mockReturnValueOnce({ ok: true, drifted: [] });
    const r = repairDependencyDrift({
      env: {},
      repoRoot: "/repo",
      probeFn,
      spawnFn,
      removeDirSyncFn,
      runWithRepairLockFn: lockPassthrough,
      warnFn: () => {}
    });
    expect(spawnFn).toHaveBeenCalledTimes(2); // install + escalated reinstall
    expect(removeDirSyncFn).toHaveBeenCalledTimes(1);
    // scoped name is split into node_modules/@scope/pkg
    expect(removeDirSyncFn.mock.calls[0][0]).toContain(path.join("node_modules", "@scope", "pkg"));
    expect(r).toMatchObject({ ok: true, recovered: true });
  });

  test("drift persists even after escalation -> ok:false", () => {
    const drift = {
      ok: false,
      drifted: [
        { name: "x", reason: "installed-mismatch", declared: "1", installed: "0", kind: "exact" }
      ]
    };
    const spawnFn = jest.fn(() => ({ status: 0 }));
    const removeDirSyncFn = jest.fn();
    const r = repairDependencyDrift({
      env: {},
      repoRoot: "/repo",
      probeFn: () => drift,
      spawnFn,
      removeDirSyncFn,
      runWithRepairLockFn: lockPassthrough,
      warnFn: () => {}
    });
    expect(spawnFn).toHaveBeenCalledTimes(2);
    expect(removeDirSyncFn).toHaveBeenCalledTimes(1);
    expect(r).toMatchObject({ ok: false, reason: "drift persisted after npm install" });
  });

  test("probe throw is best-effort (never aborts bootstrap)", () => {
    const r = repairDependencyDrift({
      env: {},
      probeFn: () => {
        throw new Error("boom");
      },
      spawnFn: jest.fn(),
      runWithRepairLockFn: lockPassthrough,
      warnFn: () => {}
    });
    expect(r).toMatchObject({ ok: true, skipped: true });
  });

  test("repair lock name is imported from (identical to) the integrity gate's lock", () => {
    const { REPAIR_LOCK_NAME } = require("../lib/integrity-gate-with-recovery");
    // Imported, not mirrored: the recovery's constant IS the gate's export, so
    // the two npm mutators provably contend on the same lock.
    expect(NODE_MODULES_REPAIR_LOCK_NAME).toBe(REPAIR_LOCK_NAME);
    expect(NODE_MODULES_REPAIR_LOCK_NAME).toBe("dxmsg-node-modules-repair.lock");
  });

  test("escalation does NOT remove dirs for lockfile-stale-only drift (install already correct)", () => {
    // installed correct, lockfile lagged. pass-1 install rewrites the lockfile;
    // simulate a persistent re-probe that still reports lockfile-stale to force
    // the escalation branch, then prove no node_modules dir was removed.
    const lockStale = {
      ok: false,
      drifted: [
        {
          name: "cspell",
          reason: "lockfile-stale",
          declared: "10.0.1",
          installed: "10.0.1",
          lockfile: "10.0.0",
          kind: "exact"
        }
      ]
    };
    const spawnFn = jest.fn(() => ({ status: 0 }));
    const removeDirSyncFn = jest.fn();
    const probeFn = jest
      .fn()
      .mockReturnValueOnce(lockStale) // initial
      .mockReturnValueOnce(lockStale) // after#1 -> escalate (but nothing to remove)
      .mockReturnValueOnce({ ok: true, drifted: [] }); // after#2 -> ok
    const r = repairDependencyDrift({
      env: {},
      repoRoot: "/repo",
      probeFn,
      spawnFn,
      removeDirSyncFn,
      runWithRepairLockFn: lockPassthrough,
      warnFn: () => {}
    });
    expect(removeDirSyncFn).not.toHaveBeenCalled();
    expect(spawnFn).toHaveBeenCalledTimes(2);
    expect(r).toMatchObject({ ok: true, recovered: true });
  });
});

describe("wiring", () => {
  test("repair-node-tooling reconciles drift BEFORE the npm-ci integrity gate", () => {
    const { repairNodeTooling } = require("../repair-node-tooling");
    const order = [];
    const result = repairNodeTooling({
      env: {},
      healRegenerableCachesFn: () => {},
      repairDependencyDriftFn: () => {
        order.push("drift");
        return { ok: true, recovered: false, skipped: false };
      },
      runIntegrityGateWithRecoveryFn: () => {
        order.push("gate");
        return { ok: true };
      },
      warnFn: () => {}
    });
    expect(order).toEqual(["drift", "gate"]);
    expect(result.status).toBe(0);
  });

  test("validate-node-tooling surfaces parity drift as a violation", async () => {
    const { validateTooling } = require("../validate-node-tooling");
    const violations = await validateTooling({
      enforceIntegrityProbe: false,
      enforceManagedNpxCliAvailability: false,
      toolSpecs: [],
      scriptSources: [],
      probeDependencyVersionParityFn: () => ({
        ok: false,
        drifted: [
          {
            name: "cspell-lib",
            kind: "exact",
            declared: "10.0.1",
            installed: "10.0.0",
            lockfile: "10.0.0",
            reason: "installed-mismatch"
          }
        ],
        checked: 1
      })
    });
    expect(violations.some((v) => v.startsWith("dependency-version-parity:"))).toBe(true);
  });

  test("post-edit guard dispatch matches package.json + package-lock.json", () => {
    const guard = require("../hooks/post-edit-validate-guard");
    expect(guard.isDependencyManifestRelevant("package.json")).toBe(true);
    expect(guard.isDependencyManifestRelevant("package-lock.json")).toBe(true);
    expect(guard.isDependencyManifestRelevant("Runtime/Foo.cs")).toBe(false);
    const entry = guard.buildDispatchTable().find((e) => e.id === "dependency-version-parity");
    expect(entry).toBeTruthy();
    expect(entry.validators[0].args()).toEqual(["scripts/validate-dependency-version-parity.js"]);
  });
});

describe("LIVE repo invariant (the category killer)", () => {
  test("every exact-pinned direct dependency is installed at exactly the declared version", () => {
    const result = probeDependencyVersionParity({ repoRoot: REPO_ROOT });
    if (!result.ok) {
      // Surface an actionable message identical to the CLI/validator.
      throw new Error(
        "Dependency version drift detected (run `npm install` to reconcile):\n" +
          formatDriftLines(result)
            .map((l) => "  - " + l)
            .join("\n")
      );
    }
    expect(result.ok).toBe(true);
    expect(result.checked).toBeGreaterThan(0);
  });
});
