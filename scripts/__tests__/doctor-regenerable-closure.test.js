/**
 * @fileoverview CLOSURE CONTRACT for the regenerable isolated managed-Jest
 * cache: the exact DIRTY shape that doctor.checkIsolatedJestCache flags MUST be
 * cleared by one real automated heal pass (and the heal is idempotent). This is
 * the generalized form of the fix-eol -> check-eol closure contract
 * (check-eol.test.js): "what the checker can flag == what the healer clears",
 * so the corrupt-regenerable-cache class can never become a dead-end state.
 *
 * Isolation: a per-test fs.mkdtempSync sandbox is used as the cacheRoot --
 * NEVER the real os.tmpdir()/dxmessaging-managed-jest (parallel Jest workers
 * mutate the shared root; fail->ok flapping was observed). The heal DI accepts
 * a cacheRoot override so the spawned real-script repair targets the sandbox.
 *
 * The heal is exercised by SPAWNING the real production module in a child
 * process (process.execPath), so the contract holds natively on Linux, macOS,
 * and Windows. A process.platform override parameterizes the in-process probe +
 * heal call boundary per repo convention.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const childProcess = require("child_process");

const doctor = require("../doctor");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

// Override process.platform for the duration of fn, then restore.
function withPlatform(platform, fn) {
  const original = Object.getOwnPropertyDescriptor(process, "platform");
  Object.defineProperty(process, "platform", { value: platform, configurable: true });
  try {
    return fn();
  } finally {
    if (original) {
      Object.defineProperty(process, "platform", original);
    } else {
      delete process.platform;
    }
  }
}

/**
 * Plant the EXACT partial install that pre-push.txt reproduced: a jest_30.3.0
 * dir with package.json + jest/bin/jest.js + jest-circus/package.json (exports
 * './runner') but with node_modules/jest-circus/build/runner.js OMITTED. From
 * inside that dir, `require.resolve('jest-circus/runner')` points at the
 * missing build/runner.js, which is precisely the corruption the doctor flags.
 */
function plantPartialInstall(sandboxRoot, dirName) {
  const installDir = path.join(sandboxRoot, dirName);
  fs.mkdirSync(path.join(installDir, "node_modules", "jest-circus", "build"), { recursive: true });
  fs.mkdirSync(path.join(installDir, "node_modules", "jest", "bin"), { recursive: true });
  fs.writeFileSync(path.join(installDir, "package.json"), "{}\n");
  fs.writeFileSync(path.join(installDir, "node_modules", "jest", "bin", "jest.js"), "//\n");
  fs.writeFileSync(
    path.join(installDir, "node_modules", "jest-circus", "package.json"),
    JSON.stringify({
      name: "jest-circus",
      version: "30.3.0",
      exports: { "./runner": "./build/runner.js" }
    })
  );
  // Intentionally DO NOT create node_modules/jest-circus/build/runner.js.
  return installDir;
}

/**
 * Spawn the real heal in a child process, scoped to the given cacheRoot.
 *
 * Test isolation: the spawned heal acquires runWithRepairLock under the REAL
 * repo .git. Two Jest workers running this suite concurrently would otherwise
 * serialize on the single shared dxmsg-regenerable-cache-heal.lock; with the
 * (correct) sub-second heal-lock timeout, a contended worker could even fail to
 * acquire and skip its sandbox purge, breaking the `installDir absent`
 * assertion. We thread a PER-CALL UNIQUE lockName so each spawned closure heal
 * takes a distinct lock dir and can never contend cross-worker. The lockName is
 * a first-class healIsolatedJestCache DI input, so this exercises the same
 * production code path with only the lock identity sandboxed.
 */
function runRealHeal(cacheRoot) {
  const uniqueLockName = `dxmsg-regen-closure-test-${process.pid}-${Math.random()
    .toString(36)
    .slice(2)}.lock`;
  const driver = `
    const { healIsolatedJestCache } = require(${JSON.stringify(
      path.join(REPO_ROOT, "scripts", "lib", "regenerable-cache-registry.js")
    )});
    const result = healIsolatedJestCache({
      cacheRoot: ${JSON.stringify(cacheRoot)},
      lockName: ${JSON.stringify(uniqueLockName)}
    });
    if (!result.ok) {
      console.error("heal reported ok:false", JSON.stringify(result));
      process.exit(2);
    }
    process.exit(0);
  `;
  return childProcess.spawnSync(process.execPath, ["-e", driver], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });
}

describe("regenerable isolated-Jest cache closure contract", () => {
  let sandboxRoot;

  beforeEach(() => {
    sandboxRoot = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-regen-closure-"));
  });

  afterEach(() => {
    fs.rmSync(sandboxRoot, { recursive: true, force: true });
  });

  for (const platform of ["linux", "win32", "darwin"]) {
    test(`DIRTY -> flagged, REPAIR -> closure + idempotence (platform=${platform})`, () => {
      const installDir = plantPartialInstall(sandboxRoot, "jest_30.3.0");

      // SANITY (non-vacuous, mirrors check-eol.test.js): the doctor section
      // MUST flag the planted partial cache BEFORE the heal. Drive the
      // in-process function against the sandbox cacheRoot with the relevance
      // gate pinned off so the corruption surfaces as a runner failure (warn).
      const before = withPlatform(platform, () =>
        doctor.checkIsolatedJestCache({
          cacheRoot: sandboxRoot,
          hasHealthyLocalJestInstallFn: () => false
        })
      );
      const beforeText = before.lines.join("\n");
      expect(before.status).toBe("warn"); // regenerable corruption -> WARN (not fail)
      expect(beforeText).toMatch(/jest_30\.3\.0/);
      expect(beforeText).toMatch(/jest-circus\/runner/);
      // Defensive: it must NOT be a vacuous "ok".
      expect(before.status).not.toBe("ok");
      expect(fs.existsSync(installDir)).toBe(true);

      // REPAIR: run the REAL heal in a child process, scoped to the sandbox.
      const repair = runRealHeal(sandboxRoot);
      expect(repair.status).toBe(0);

      // CLOSURE: the corrupt dir is gone and the section no longer flags it.
      expect(fs.existsSync(installDir)).toBe(false);
      const after = withPlatform(platform, () =>
        doctor.checkIsolatedJestCache({
          cacheRoot: sandboxRoot,
          hasHealthyLocalJestInstallFn: () => false
        })
      );
      // STRICT closure for the MOTIVATING shape (pre-push.txt): the sole install
      // dir was the only entry, so purging it leaves the cache root EMPTY and the
      // doctor PROVABLY returns 'ok' (doctor.js: "contains no install dirs").
      // Assert the strictly-correct 'ok' (not the permissive ['ok','warn']) so a
      // regression that leaves a residual downgrading this EXACT shape to a
      // non-blocking WARN is caught. The broader ['ok','warn'] acceptance is
      // retained ONLY for the divergent legacy-runner / doctor-stricter shapes
      // below, where a residual WARN is the documented, intended contract.
      expect(after.status).toBe("ok");

      // IDEMPOTENCE (mirrors check-eol.test.js): a second heal pass is a clean
      // no-op (the cache root still exists but holds no corrupt dir), exit 0.
      const repairAgain = runRealHeal(sandboxRoot);
      expect(repairAgain.status).toBe(0);
      expect(fs.existsSync(installDir)).toBe(false);
    });
  }

  test("a HEALTHY install dir is left untouched by the heal (minimal deletion)", () => {
    // Plant a dir that resolves correctly (runner.js present). The heal must
    // NOT delete it. Proves the closure contract does not over-reach.
    const installDir = path.join(sandboxRoot, "jest_30.3.0");
    fs.mkdirSync(path.join(installDir, "node_modules", "jest-circus", "build"), {
      recursive: true
    });
    fs.mkdirSync(path.join(installDir, "node_modules", "jest", "bin"), { recursive: true });
    fs.writeFileSync(path.join(installDir, "package.json"), "{}\n");
    fs.writeFileSync(path.join(installDir, "node_modules", "jest", "bin", "jest.js"), "//\n");
    fs.writeFileSync(
      path.join(installDir, "node_modules", "jest-circus", "package.json"),
      JSON.stringify({
        name: "jest-circus",
        version: "30.3.0",
        exports: { "./runner": "./build/runner.js" }
      })
    );
    fs.writeFileSync(
      path.join(installDir, "node_modules", "jest-circus", "build", "runner.js"),
      "module.exports = {};\n"
    );

    const repair = runRealHeal(sandboxRoot);
    expect(repair.status).toBe(0);
    expect(fs.existsSync(installDir)).toBe(true);
  });

  for (const platform of ["linux", "win32", "darwin"]) {
    test(`STRAY-FILE cache root (ENOTDIR): DIRTY -> WARN, REPAIR -> closure (platform=${platform})`, () => {
      // REGENERABLE corruption shape the lens explicitly names: the cache ROOT
      // is a stray FILE, not a dir (botched extract, a `>` redirect, another
      // tool). readdir/mkdir both throw ENOTDIR, so the reactive fallback is a
      // dead-end too. Deleting the file fully restores correctness (the next run
      // rebuilds the dir). Lock the fix<->check closure for this shape: the
      // doctor flags it WARN (not fail) AND the real heal clears it.
      const fileRoot = path.join(sandboxRoot, "stray-cache-root");
      fs.writeFileSync(fileRoot, "botched extract / stray redirect\n");

      const before = withPlatform(platform, () =>
        doctor.checkIsolatedJestCache({
          cacheRoot: fileRoot,
          hasHealthyLocalJestInstallFn: () => false
        })
      );
      // Regenerable stray-file root -> WARN, never a push-blocking FAIL.
      expect(before.status).toBe("warn");
      expect(before.lines.join("\n")).toMatch(/stray FILE/i);
      expect(fs.existsSync(fileRoot)).toBe(true);

      // REPAIR: the real heal removes the stray file (strict-equality guarded).
      const repair = runRealHeal(fileRoot);
      expect(repair.status).toBe(0);
      expect(fs.existsSync(fileRoot)).toBe(false);

      // CLOSURE: the section no longer flags it (root now absent -> ok).
      const after = withPlatform(platform, () =>
        doctor.checkIsolatedJestCache({
          cacheRoot: fileRoot,
          hasHealthyLocalJestInstallFn: () => false
        })
      );
      expect(after.status).toBe("ok");

      // IDEMPOTENCE: a second heal pass on the now-absent root is a clean no-op.
      const repairAgain = runRealHeal(fileRoot);
      expect(repairAgain.status).toBe(0);
    });
  }

  test("doctor-STRICTER-than-healer boundary: no-exports + legacy stray runner -> doctor WARN, heal NO-OP (non-blocking, runner-usable)", () => {
    // The closure contract is "the healer clears every cache the RUNNER deems
    // unusable", NOT bit-identical convergence with the doctor's stricter
    // exports-based resolve probe. Construct the divergent shape: a
    // jest-circus/package.json with NO 'exports' map PLUS a present legacy
    // node_modules/jest-circus/build/runner.js. The doctor flags WARN (its
    // require.resolve('jest-circus/runner') throws without 'exports'), but the
    // healer does NOT purge it, because resolveIsolatedJestRunnerPath falls back
    // to the legacy build/runner.js and returns non-null -> run-managed-jest
    // would still run fine. This WARN is non-blocking and the dir is genuinely
    // usable, so leaving it intact does not reintroduce a dead-end. Pin the
    // boundary so the (correct, softened) contract is locked.
    const installDir = path.join(sandboxRoot, "jest_30.3.0");
    fs.mkdirSync(path.join(installDir, "node_modules", "jest-circus", "build"), {
      recursive: true
    });
    fs.mkdirSync(path.join(installDir, "node_modules", "jest", "bin"), { recursive: true });
    fs.writeFileSync(path.join(installDir, "package.json"), "{}\n");
    fs.writeFileSync(path.join(installDir, "node_modules", "jest", "bin", "jest.js"), "//\n");
    // NO 'exports' map -> require.resolve('jest-circus/runner') throws.
    fs.writeFileSync(
      path.join(installDir, "node_modules", "jest-circus", "package.json"),
      JSON.stringify({ name: "jest-circus", version: "30.3.0" })
    );
    // But the legacy default path IS present and non-empty.
    fs.writeFileSync(
      path.join(installDir, "node_modules", "jest-circus", "build", "runner.js"),
      "module.exports = {};\n"
    );

    const before = doctor.checkIsolatedJestCache({
      cacheRoot: sandboxRoot,
      hasHealthyLocalJestInstallFn: () => false
    });
    // Doctor is stricter: it WARNs (resolve threw), but never a push-blocking FAIL.
    expect(before.status).toBe("warn");
    expect(before.lines.join("\n")).toMatch(/resolve threw|Cannot find module/i);

    // The real heal is a NO-OP here: the runner is usable via the legacy path,
    // so the healer leaves the dir intact (exit 0, dir still present).
    const repair = runRealHeal(sandboxRoot);
    expect(repair.status).toBe(0);
    expect(fs.existsSync(installDir)).toBe(true);
  });

  for (const platform of ["linux", "win32", "darwin"]) {
    test(`ZERO-BYTE runner.js: DIRTY -> WARN, REPAIR -> closure (platform=${platform})`, () => {
      // The antivirus/Disk-Cleanup mid-write class: jest-circus/build/runner.js
      // EXISTS but is size 0, so existsSync alone would call it healthy while a
      // real require() loads an empty module and Jest crashes un-decoded. The
      // doctor + run-managed-jest now both treat size 0 as a miss (mirroring
      // node-modules-integrity's empty-file rule), so the doctor flags it WARN
      // and the heal purges it. Lock that closure.
      const installDir = plantPartialInstall(sandboxRoot, "jest_30.3.0");
      // Now CREATE the runner at size 0 (plantPartialInstall omits it).
      fs.writeFileSync(
        path.join(installDir, "node_modules", "jest-circus", "build", "runner.js"),
        ""
      );
      expect(
        fs.statSync(path.join(installDir, "node_modules", "jest-circus", "build", "runner.js")).size
      ).toBe(0);

      const before = withPlatform(platform, () =>
        doctor.checkIsolatedJestCache({
          cacheRoot: sandboxRoot,
          hasHealthyLocalJestInstallFn: () => false
        })
      );
      expect(before.status).toBe("warn");
      expect(before.lines.join("\n")).toMatch(/empty \(size 0\)/i);
      expect(fs.existsSync(installDir)).toBe(true);

      // REPAIR: the real heal purges the zero-byte-runner dir.
      const repair = runRealHeal(sandboxRoot);
      expect(repair.status).toBe(0);
      expect(fs.existsSync(installDir)).toBe(false);

      // CLOSURE: the section no longer flags a corruption.
      const after = withPlatform(platform, () =>
        doctor.checkIsolatedJestCache({
          cacheRoot: sandboxRoot,
          hasHealthyLocalJestInstallFn: () => false
        })
      );
      expect(after.status).not.toBe("fail");
      expect(["ok", "warn"]).toContain(after.status);
    });
  }
});
