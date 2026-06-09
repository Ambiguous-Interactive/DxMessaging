/**
 * @fileoverview Unit tests for scripts/lib/regenerable-cache-registry.js -- the
 * failure-class -> automated-repair REGISTRY and its first concrete healer
 * (the isolated managed-Jest fallback cache).
 *
 * Every external touched by the healer is dependency-injected so these tests
 * drive deterministic fakes and NEVER touch the real os.tmpdir() cache root
 * (parallel Jest workers mutate that shared dir). Path-handling cases are
 * platform-parameterized via a process.platform override per repo convention.
 */

"use strict";

const path = require("path");

const {
  REGENERABLE_CACHE_HEAL_LOCK_NAME,
  REGENERABLE_CACHE_HEAL_LOCK_TIMEOUT_MS,
  REGENERABLE_CACHE_HEAL_PURGE_BUDGET_MS,
  DEFAULT_HEAL_RETRY_DELAYS_MS,
  REGENERABLE_CACHE_REPAIRS,
  healRegenerableCaches,
  healIsolatedJestCache,
  makeBudgetedSleepFn,
  removeFileIfIsKnownCacheRoot
} = require("../lib/regenerable-cache-registry");
const { removeDirIfStrictDescendant, ISOLATED_JEST_CACHE_ROOT } = require("../run-managed-jest");
const { withPlatform } = require("../lib/jest-fixtures");

// A runWithRepairLock fake that records the lockName and executes the callback
// (lock acquired). Mirrors the real return contract closely enough for the
// healer's post-processing.
function makeLockFake(record) {
  return (repoRoot, cb, options) => {
    record.lockName = options && options.lockName;
    record.options = options;
    record.repoRoot = repoRoot;
    record.acquired = true;
    return cb();
  };
}

const SANDBOX_ROOT = "/sandbox/dxmessaging-managed-jest";

describe("regenerable-cache-registry: registry shape", () => {
  test("REGENERABLE_CACHE_REPAIRS is frozen and every entry is frozen", () => {
    expect(Object.isFrozen(REGENERABLE_CACHE_REPAIRS)).toBe(true);
    expect(REGENERABLE_CACHE_REPAIRS.length).toBeGreaterThan(0);
    for (const entry of REGENERABLE_CACHE_REPAIRS) {
      expect(Object.isFrozen(entry)).toBe(true);
      expect(typeof entry.id).toBe("string");
      expect(typeof entry.doctorSectionName).toBe("string");
      expect(typeof entry.probeAndHeal).toBe("function");
    }
  });

  test("the first entry is the isolated managed-Jest cache keyed on the doctor section name", () => {
    const first = REGENERABLE_CACHE_REPAIRS[0];
    expect(first.id).toBe("isolated-managed-jest-cache");
    expect(first.doctorSectionName).toBe("isolated managed-Jest cache");
  });
});

describe("healIsolatedJestCache", () => {
  test("cache root ABSENT -> no-op: rmSync NOT called, NO lock taken", () => {
    const rmSyncFn = jest.fn();
    const runWithRepairLockFn = jest.fn();
    const readdirSyncFn = jest.fn(() => {
      throw new Error("readdir must not run when root is absent");
    });

    const result = healIsolatedJestCache({
      cacheRoot: SANDBOX_ROOT,
      existsSyncFn: () => false,
      readdirSyncFn,
      rmSyncFn,
      runWithRepairLockFn,
      warnFn: () => {}
    });

    expect(result.healed).toBe(false);
    expect(result.ok).toBe(true);
    expect(rmSyncFn).not.toHaveBeenCalled();
    expect(runWithRepairLockFn).not.toHaveBeenCalled();
    expect(readdirSyncFn).not.toHaveBeenCalled();
  });

  for (const platform of ["linux", "win32", "darwin"]) {
    test(`purges ONLY the corrupt dir among several (scope convergence + minimal deletion) (platform=${platform})`, () => {
      withPlatform(platform, () => {
        // Three install dirs; resolveRunnerFn returns a real path for two
        // (healthy) and null for one (corrupt). Only the corrupt one is purged.
        const dirs = ["jest_30.3.0", "jest_29.0.0", "jest_28.1.0"];
        const corruptName = "jest_29.0.0";
        const purged = [];

        const result = healIsolatedJestCache({
          cacheRoot: SANDBOX_ROOT,
          existsSyncFn: () => true,
          readdirSyncFn: () => dirs.map((name) => ({ name, isDirectory: () => true })),
          resolveRunnerFn: (installDir) =>
            installDir.includes(corruptName) ? null : path.join(installDir, "runner.js"),
          // Fake guarded-rm: record which install dir was targeted; succeed.
          // It receives (cacheRoot, installDir) scoped to the walked root.
          removeDirIfStrictDescendantFn: (root, installDir) => {
            expect(root).toBe(SANDBOX_ROOT);
            purged.push(path.basename(installDir));
            return true;
          },
          runWithRepairLockFn: makeLockFake({}),
          warnFn: () => {}
        });

        expect(purged).toEqual([corruptName]);
        expect(result.healed).toBe(true);
        expect(result.ok).toBe(true);
        expect(result.purgedDirs).toEqual([corruptName]);
      });
    });
  }

  for (const platform of ["linux", "win32", "darwin"]) {
    test(`refuses a path-traversal '..' dir name via the strict-descendant guard (platform=${platform})`, () => {
      withPlatform(platform, () => {
        // A poisoned dir name ".." joins to the cache root's PARENT, which the
        // REAL strict-descendant guard refuses (resolves outside the walked
        // root). rm is never called for it and a "Refusing ... not a
        // descendant" warn is emitted (mirrors run-managed-jest.test.js).
        const rmSyncFn = jest.fn();
        const warnings = [];

        const result = healIsolatedJestCache({
          cacheRoot: SANDBOX_ROOT,
          existsSyncFn: () => true,
          readdirSyncFn: () => [{ name: "..", isDirectory: () => true }],
          // Force "corrupt" so the healer attempts to purge the poisoned dir.
          resolveRunnerFn: () => null,
          // Use the REAL guarded reset so the traversal refusal is exercised,
          // scoped to the SANDBOX root.
          removeDirIfStrictDescendantFn: removeDirIfStrictDescendant,
          rmSyncFn,
          runWithRepairLockFn: makeLockFake({}),
          warnFn: (m) => warnings.push(String(m))
        });

        expect(rmSyncFn).not.toHaveBeenCalled();
        expect(warnings.some((m) => m.includes("not a descendant"))).toBe(true);
        // A refused dir is "not healed" but does not throw.
        expect(result.purgedDirs).toEqual([]);
      });
    });
  }

  test("cache root PRESENT but every dir HEALTHY -> near-zero happy path: NO lock taken, nothing purged", () => {
    // The common case on a machine that already has a fallback cache. The lock
    // guards MUTATION only, so a present-but-healthy cache must NOT pay the
    // cross-process lock acquire (nor risk the sub-second acquire-timeout stall
    // under contention) -- it only readdirs + resolves, lock-free.
    const runWithRepairLockFn = jest.fn();
    const rmSyncFn = jest.fn();

    const result = healIsolatedJestCache({
      cacheRoot: SANDBOX_ROOT,
      existsSyncFn: () => true,
      readdirSyncFn: () => [
        { name: "jest_30.3.0", isDirectory: () => true },
        { name: "jest_29.0.0", isDirectory: () => true }
      ],
      // Both resolve fine -> healthy -> nothing to mutate -> no lock.
      resolveRunnerFn: (installDir) => path.join(installDir, "runner.js"),
      runWithRepairLockFn,
      rmSyncFn,
      warnFn: () => {}
    });

    expect(result.healed).toBe(false);
    expect(result.ok).toBe(true);
    expect(result.purgedDirs).toEqual([]);
    expect(runWithRepairLockFn).not.toHaveBeenCalled();
    expect(rmSyncFn).not.toHaveBeenCalled();
  });

  test("takes runWithRepairLock with a SEPARATE lock name when there IS corruption to purge (not the npm-ci REPAIR_LOCK_NAME)", () => {
    const record = {};
    healIsolatedJestCache({
      cacheRoot: SANDBOX_ROOT,
      existsSyncFn: () => true,
      // One corrupt dir so the heal must mutate -> it takes the lock.
      readdirSyncFn: () => [{ name: "jest_30.3.0", isDirectory: () => true }],
      resolveRunnerFn: () => null,
      removeDirIfStrictDescendantFn: () => true,
      runWithRepairLockFn: makeLockFake(record),
      warnFn: () => {}
    });

    expect(record.lockName).toBe(REGENERABLE_CACHE_HEAL_LOCK_NAME);
    expect(record.lockName).not.toBe("dxmsg-node-modules-repair.lock");
    // Sanity: the heal lock is genuinely distinct from the production isolated
    // root's parent (defense-in-depth that the constant is the heal lock).
    expect(REGENERABLE_CACHE_HEAL_LOCK_NAME).toContain("regenerable");
  });

  test("forwards a SUB-SECOND lock timeoutMs on the mutating path (pins the <1s hook budget on a contended heal-lock)", () => {
    // BUDGET CONTRACT: the heal must NOT inherit acquireRepairLock's 120s
    // default. On a contended heal-lock it must give up fast (best-effort) so
    // the git-hook stays inside its <1s budget; the other concurrent heal (or
    // the reactive run-managed-jest tier) clears the corrupt cache. Assert the
    // call forwards a sub-second timeoutMs + a short retryDelayMs (driven on the
    // corruption path, the only path that takes the lock).
    const record = {};
    healIsolatedJestCache({
      cacheRoot: SANDBOX_ROOT,
      existsSyncFn: () => true,
      readdirSyncFn: () => [{ name: "jest_30.3.0", isDirectory: () => true }],
      resolveRunnerFn: () => null,
      removeDirIfStrictDescendantFn: () => true,
      runWithRepairLockFn: makeLockFake(record),
      warnFn: () => {}
    });

    expect(record.options.timeoutMs).toBe(REGENERABLE_CACHE_HEAL_LOCK_TIMEOUT_MS);
    expect(REGENERABLE_CACHE_HEAL_LOCK_TIMEOUT_MS).toBeLessThan(1000);
    expect(record.options.timeoutMs).toBeGreaterThan(0);
    expect(typeof record.options.retryDelayMs).toBe("number");
    expect(record.options.retryDelayMs).toBeLessThan(record.options.timeoutMs);
  });

  test("lock acquisition FAILURE on the mutating path is best-effort (not healed, ok:true, no throw)", () => {
    const result = healIsolatedJestCache({
      cacheRoot: SANDBOX_ROOT,
      existsSyncFn: () => true,
      // Corruption present -> the heal wants the lock; the lock cannot be
      // acquired -> best-effort give-up (another process likely holds it and is
      // doing the same work). The rm guard proves no mutation runs when the lock
      // fails.
      readdirSyncFn: () => [{ name: "jest_30.3.0", isDirectory: () => true }],
      resolveRunnerFn: () => null,
      rmSyncFn: () => {
        throw new Error("must not rm when lock fails");
      },
      runWithRepairLockFn: () => ({ status: 1, lockFailed: true, reason: "held" }),
      warnFn: () => {}
    });

    expect(result.healed).toBe(false);
    expect(result.ok).toBe(true);
    expect(result.purgedDirs).toEqual([]);
  });

  test("readdir read-error is surfaced as ok:false (host fault, detected lock-free), no throw", () => {
    const warnings = [];
    const runWithRepairLockFn = jest.fn();
    const result = healIsolatedJestCache({
      cacheRoot: SANDBOX_ROOT,
      existsSyncFn: () => true,
      readdirSyncFn: () => {
        const err = new Error("EACCES: permission denied");
        err.code = "EACCES";
        throw err;
      },
      runWithRepairLockFn,
      warnFn: (m) => warnings.push(String(m))
    });

    expect(result.healed).toBe(false);
    expect(result.ok).toBe(false);
    expect(
      warnings.some((m) => m.includes("Could not read isolated managed-Jest cache root"))
    ).toBe(true);
    // A host read-error is not auto-deletable and a lock would not help: it is
    // detected lock-free, so the cross-process lock is never acquired.
    expect(runWithRepairLockFn).not.toHaveBeenCalled();
  });

  for (const platform of ["linux", "win32", "darwin"]) {
    test(`ENOTDIR cache root (a stray FILE) is auto-purged via the strict-equality file guard (platform=${platform})`, () => {
      withPlatform(platform, () => {
        // The cache ROOT is a stray FILE, not a dir: readdir throws ENOTDIR. The
        // strict-descendant guard cannot clear it (relative(root,root)===""),
        // so the healer routes through removeFileIfIsKnownCacheRoot. Use the
        // REAL file-guard with a fake rmSync so we exercise the production
        // strict-equality + rm path, scoped to the sandbox root.
        const rmTargets = [];
        const rmSyncFn = jest.fn((target) => {
          rmTargets.push(target);
        });
        const warnings = [];

        const result = healIsolatedJestCache({
          cacheRoot: SANDBOX_ROOT,
          existsSyncFn: () => true,
          readdirSyncFn: () => {
            const err = new Error("ENOTDIR: not a directory, scandir");
            err.code = "ENOTDIR";
            throw err;
          },
          // The real file guard; rmSync is faked.
          removeFileIfIsKnownCacheRootFn: removeFileIfIsKnownCacheRoot,
          rmSyncFn,
          runWithRepairLockFn: makeLockFake({}),
          warnFn: (m) => warnings.push(String(m))
        });

        expect(result.ok).toBe(true);
        expect(result.healed).toBe(true);
        expect(result.purgedDirs).toEqual([SANDBOX_ROOT]);
        // The rm targeted exactly the resolved cache root, nothing else.
        expect(rmTargets).toEqual([path.resolve(SANDBOX_ROOT)]);
        expect(warnings.some((m) => m.includes("Auto-cleared stray file"))).toBe(true);
      });
    });
  }

  test("ENOTDIR stray-file purge FAILURE (persistent EPERM) -> ok:false, not healed, no throw", () => {
    // The stray file cannot be removed (EPERM on every attempt). The healer must
    // surface ok:false (the doctor downgrade keeps this WARN) and never throw.
    const rmSyncFn = jest.fn(() => {
      const err = new Error("EPERM: operation not permitted, unlink");
      err.code = "EPERM";
      throw err;
    });
    const warnings = [];

    const result = healIsolatedJestCache({
      cacheRoot: SANDBOX_ROOT,
      existsSyncFn: () => true,
      readdirSyncFn: () => {
        const err = new Error("ENOTDIR: not a directory, scandir");
        err.code = "ENOTDIR";
        throw err;
      },
      removeFileIfIsKnownCacheRootFn: removeFileIfIsKnownCacheRoot,
      rmSyncFn,
      sleepFn: () => {},
      // No retries so the test is fast and deterministic: 1 rm attempt, fail.
      retryDelaysMs: [],
      runWithRepairLockFn: makeLockFake({}),
      warnFn: (m) => warnings.push(String(m))
    });

    expect(result.ok).toBe(false);
    expect(result.healed).toBe(false);
    expect(result.purgedDirs).toEqual([]);
    expect(warnings.some((m) => m.includes("Failed to remove stray"))).toBe(true);
  });

  for (const platform of ["linux", "win32", "darwin"]) {
    test(`per-dir purge retries EPERM-then-succeeds at the HEALER boundary (composition) (platform=${platform})`, () => {
      withPlatform(platform, () => {
        // HEALER-LEVEL retry composition: drive healIsolatedJestCache with the
        // REAL removeDirIfStrictDescendant plus a fake rmSync that throws EPERM
        // once then succeeds. This pins the corrupt-dir-is-momentarily-locked-
        // on-Windows-then-purged path end-to-end through the healer (not just
        // the shared primitive), so a future refactor that drops
        // retryDelaysMs/sleepFn threading from purgeCorruptInstallDir is caught.
        let rmCalls = 0;
        const rmSyncFn = jest.fn(() => {
          rmCalls += 1;
          if (rmCalls === 1) {
            const err = new Error("EPERM: operation not permitted, rmdir");
            err.code = "EPERM";
            throw err;
          }
          // Second attempt succeeds.
        });
        const sleepFn = jest.fn();

        const result = healIsolatedJestCache({
          cacheRoot: SANDBOX_ROOT,
          existsSyncFn: () => true,
          readdirSyncFn: () => [{ name: "jest_30.3.0", isDirectory: () => true }],
          resolveRunnerFn: () => null, // corrupt -> attempt purge
          removeDirIfStrictDescendantFn: removeDirIfStrictDescendant,
          rmSyncFn,
          sleepFn,
          retryDelaysMs: [5],
          runWithRepairLockFn: makeLockFake({}),
          warnFn: () => {}
        });

        expect(result.purgedDirs).toEqual(["jest_30.3.0"]);
        expect(result.healed).toBe(true);
        expect(result.ok).toBe(true);
        expect(rmSyncFn).toHaveBeenCalledTimes(2);
        expect(sleepFn).toHaveBeenCalledTimes(1);
      });
    });
  }

  test("per-dir purge could-not-purge (non-refusal failure) aggregates into healer ok:false", () => {
    // A corrupt dir whose guarded purge FAILS for a non-traversal reason (e.g.
    // persistent EPERM after retries): purgeCorruptInstallDir returns
    // {purged:false, refused:false}. The healer must report healed:false,
    // ok:false, purgedDirs:[] (no warn about "not a descendant" since this is a
    // could-not-purge, not a refusal).
    const result = healIsolatedJestCache({
      cacheRoot: SANDBOX_ROOT,
      existsSyncFn: () => true,
      readdirSyncFn: () => [{ name: "jest_30.3.0", isDirectory: () => true }],
      resolveRunnerFn: () => null, // corrupt
      // Could-not-purge: returns false WITHOUT emitting a "not a descendant" warn.
      removeDirIfStrictDescendantFn: () => false,
      runWithRepairLockFn: makeLockFake({}),
      warnFn: () => {}
    });

    expect(result.healed).toBe(false);
    expect(result.ok).toBe(false);
    expect(result.purgedDirs).toEqual([]);
  });

  test("removeFileIfIsKnownCacheRoot refuses a path that is NOT the known cache root (strict-equality guard)", () => {
    // Defense-in-depth: the file guard must never rm anything other than the
    // exact cache root it was handed. A mismatched stray path is refused.
    const rmSyncFn = jest.fn();
    const warnings = [];
    const out = removeFileIfIsKnownCacheRoot(SANDBOX_ROOT, `${SANDBOX_ROOT}/child-file`, {
      rmSyncFn,
      warnFn: (m) => warnings.push(String(m))
    });
    expect(out.purged).toBe(false);
    expect(rmSyncFn).not.toHaveBeenCalled();
    expect(warnings.some((m) => m.includes("not the known cache root"))).toBe(true);
  });

  test("removeFileIfIsKnownCacheRoot still purges a DIFFERENTLY-CASED spelling of the root on win32 (case-fold parity)", () => {
    // CASE-INSENSITIVE-FS PARITY: on Windows C:\\Temp\\... and C:\\temp\\... are
    // the SAME file. A raw `path.resolve(a) === path.resolve(b)` strict compare
    // would case-fold-MISS and leave the genuinely-regenerable stray file
    // un-healed (a soft dead-end). The guard now uses the shared
    // normalizeForPathComparison comparator, which lower-cases on win32, so a
    // differently-cased spelling of the same root is recognized and purged.
    // (The identical-reference call the production healer makes is already
    // covered above; this pins the case-folded spelling specifically.)
    withPlatform("win32", () => {
      const rmTargets = [];
      const rmSyncFn = jest.fn((target) => rmTargets.push(target));
      const cacheRoot = "C:\\Temp\\dxmessaging-managed-jest";
      const strayLowerCase = "c:\\temp\\dxmessaging-managed-jest";

      const out = removeFileIfIsKnownCacheRoot(cacheRoot, strayLowerCase, {
        rmSyncFn,
        warnFn: () => {}
      });

      expect(out.purged).toBe(true);
      expect(rmSyncFn).toHaveBeenCalledTimes(1);
      // It targeted the resolved stray spelling (not an arbitrary path).
      expect(rmTargets).toEqual([path.resolve(strayLowerCase)]);
    });
  });

  test("removeFileIfIsKnownCacheRoot is CASE-SENSITIVE on POSIX (a differently-cased spelling is a different file -> refused)", () => {
    // POSIX parity: on Linux/macOS case matters, so a differently-cased spelling
    // is a genuinely different path and must be refused (the comparator does NOT
    // case-fold off win32). Guards against an over-eager case-fold leaking onto
    // case-sensitive hosts.
    withPlatform("linux", () => {
      const rmSyncFn = jest.fn();
      const warnings = [];
      const out = removeFileIfIsKnownCacheRoot(
        "/tmp/dxmessaging-managed-jest",
        "/tmp/DXMESSAGING-managed-jest",
        { rmSyncFn, warnFn: (m) => warnings.push(String(m)) }
      );
      expect(out.purged).toBe(false);
      expect(rmSyncFn).not.toHaveBeenCalled();
      expect(warnings.some((m) => m.includes("not the known cache root"))).toBe(true);
    });
  });

  test("uses ISOLATED_JEST_CACHE_ROOT as the default cacheRoot", () => {
    // Default-arg sanity: with no cacheRoot override and existsSyncFn->false the
    // healer probes the real isolated root and no-ops. Proves the production
    // default is wired (without touching disk).
    const existsSyncFn = jest.fn(() => false);
    const result = healIsolatedJestCache({ existsSyncFn, warnFn: () => {} });
    expect(result.ok).toBe(true);
    expect(existsSyncFn).toHaveBeenCalledWith(ISOLATED_JEST_CACHE_ROOT);
  });
});

describe("makeBudgetedSleepFn (cumulative in-lock retry-sleep cap)", () => {
  test("sleeps requested ms until the budget is reached, then no-ops (clamps the final sleep)", () => {
    let total = 0;
    const realSleep = (ms) => {
      total += ms;
    };
    const budgeted = makeBudgetedSleepFn(realSleep, 600);
    budgeted(200); // 200
    budgeted(400); // 600 (at budget)
    budgeted(400); // no-op (already at budget)
    budgeted(1000); // no-op
    expect(total).toBe(600);
  });

  test("clamps a single over-budget sleep so it never overshoots", () => {
    let total = 0;
    const budgeted = makeBudgetedSleepFn((ms) => {
      total += ms;
    }, 250);
    budgeted(1000); // clamped to the remaining 250
    expect(total).toBe(250);
  });

  test("a zero/negative/non-finite budget makes every sleep a no-op", () => {
    for (const budget of [0, -10, NaN, undefined]) {
      let total = 0;
      const budgeted = makeBudgetedSleepFn((ms) => {
        total += ms;
      }, budget);
      budgeted(500);
      expect(total).toBe(0);
    }
  });

  test("ignores zero/negative/non-finite requested sleeps without consuming budget", () => {
    let total = 0;
    let calls = 0;
    const budgeted = makeBudgetedSleepFn((ms) => {
      total += ms;
      calls += 1;
    }, 600);
    budgeted(0);
    budgeted(-5);
    budgeted(NaN);
    budgeted(300);
    expect(total).toBe(300);
    expect(calls).toBe(1); // only the one positive sleep ran
  });
});

describe("healIsolatedJestCache in-lock purge wall-time budget (Windows persistent-EPERM)", () => {
  for (const platform of ["linux", "win32", "darwin"]) {
    test(`cumulative retry-sleep across MANY persistently-locked corrupt dirs stays within the purge budget (platform=${platform})`, () => {
      withPlatform(platform, () => {
        // Cross-platform timing guarantee (the precise Windows %TEMP%/antivirus
        // class): plant MANY corrupt install dirs that EVERY rm attempt fails on
        // with a persistent EPERM. Without the shared budgeted sleep, each dir
        // would pay sum(retryDelaysMs) and N dirs would stack into N*backoff of
        // in-lock Atomics.wait. Assert the REAL purgeCorruptInstallDir +
        // removeDirIfStrictDescendant retry path, driven through the healer with
        // a recording sleepFn, never sleeps more than the documented budget in
        // aggregate -- regardless of how many dirs are locked.
        const dirCount = 12;
        const dirs = Array.from({ length: dirCount }, (_, i) => `jest_${i}.0.0`);
        let totalSlept = 0;
        const sleepFn = (ms) => {
          totalSlept += Number(ms) || 0;
        };
        // Every rm attempt throws a persistent EPERM (the OS lock never clears).
        const rmSyncFn = jest.fn(() => {
          const err = new Error("EPERM: operation not permitted, rmdir");
          err.code = "EPERM";
          throw err;
        });

        const result = healIsolatedJestCache({
          cacheRoot: SANDBOX_ROOT,
          existsSyncFn: () => true,
          readdirSyncFn: () => dirs.map((name) => ({ name, isDirectory: () => true })),
          resolveRunnerFn: () => null, // every dir is corrupt -> attempt purge
          // REAL guarded reset so the production retry loop (and its backoff)
          // actually runs, scoped to the sandbox root.
          removeDirIfStrictDescendantFn: removeDirIfStrictDescendant,
          rmSyncFn,
          sleepFn,
          // Use the PRODUCTION default backoff + budget (the contract under test).
          retryDelaysMs: DEFAULT_HEAL_RETRY_DELAYS_MS,
          purgeBudgetMs: REGENERABLE_CACHE_HEAL_PURGE_BUDGET_MS,
          runWithRepairLockFn: makeLockFake({}),
          warnFn: () => {}
        });

        // CONTRACT: total in-lock retry sleep is bounded by the budget, NOT by
        // N * sum(retryDelaysMs). With the old unbounded scheme this would have
        // been dirCount * 600 = 7200ms; the cap holds it at <= 600ms.
        expect(totalSlept).toBeLessThanOrEqual(REGENERABLE_CACHE_HEAL_PURGE_BUDGET_MS);
        // Non-vacuity: the budget was actually exercised (we DID sleep, i.e. the
        // retry path ran), so the cap is meaningful and not trivially zero.
        expect(totalSlept).toBeGreaterThan(0);
        // Every dir failed to purge (persistent EPERM) -> not healed, ok:false,
        // but NO throw: the reactive run-managed-jest tier remains the backstop.
        expect(result.healed).toBe(false);
        expect(result.ok).toBe(false);
        expect(result.purgedDirs).toEqual([]);
        // Worst-case total heal wall time = acquire timeout + purge budget < 1s.
        expect(
          REGENERABLE_CACHE_HEAL_LOCK_TIMEOUT_MS + REGENERABLE_CACHE_HEAL_PURGE_BUDGET_MS
        ).toBeLessThan(1000);
      });
    });
  }

  test("the ENOTDIR stray-file branch shares the SAME budget (no separate N+1 stall)", () => {
    // The stray-file purge (removeFileIfIsKnownCacheRoot) is on the budgeted
    // sleep too, so a persistent EPERM on the stray file cannot add an
    // independent sum(retryDelaysMs) on top of the per-dir budget. Drive the
    // ENOTDIR branch with a persistently-failing rm and assert the sleep stays
    // within the budget.
    let totalSlept = 0;
    const sleepFn = (ms) => {
      totalSlept += Number(ms) || 0;
    };
    const rmSyncFn = jest.fn(() => {
      const err = new Error("EPERM: operation not permitted, unlink");
      err.code = "EPERM";
      throw err;
    });

    const result = healIsolatedJestCache({
      cacheRoot: SANDBOX_ROOT,
      existsSyncFn: () => true,
      readdirSyncFn: () => {
        const err = new Error("ENOTDIR: not a directory, scandir");
        err.code = "ENOTDIR";
        throw err;
      },
      removeFileIfIsKnownCacheRootFn: removeFileIfIsKnownCacheRoot,
      rmSyncFn,
      sleepFn,
      retryDelaysMs: DEFAULT_HEAL_RETRY_DELAYS_MS,
      purgeBudgetMs: REGENERABLE_CACHE_HEAL_PURGE_BUDGET_MS,
      runWithRepairLockFn: makeLockFake({}),
      warnFn: () => {}
    });

    expect(totalSlept).toBeLessThanOrEqual(REGENERABLE_CACHE_HEAL_PURGE_BUDGET_MS);
    expect(result.ok).toBe(false);
  });

  test("default purge budget is sub-second and the heal-specific backoff is DISTINCT from the npm-ci [750, 2000]", () => {
    // Pin the budget contract numerically: the purge budget is well under 1s,
    // and the per-dir backoff is the tighter heal-specific [200, 400] (NOT the
    // heavyweight npm-ci [750, 2000]) so a single locked dir's full retry also
    // stays small.
    expect(REGENERABLE_CACHE_HEAL_PURGE_BUDGET_MS).toBeGreaterThan(0);
    expect(REGENERABLE_CACHE_HEAL_PURGE_BUDGET_MS).toBeLessThan(1000);
    expect(DEFAULT_HEAL_RETRY_DELAYS_MS).not.toEqual([750, 2000]);
    // The full backoff for one dir must fit inside the budget so a transient
    // single-dir lock still gets its complete retry sequence.
    const oneDirBackoff = DEFAULT_HEAL_RETRY_DELAYS_MS.reduce((a, b) => a + b, 0);
    expect(oneDirBackoff).toBeLessThanOrEqual(REGENERABLE_CACHE_HEAL_PURGE_BUDGET_MS);
  });
});

describe("healRegenerableCaches orchestrator", () => {
  test("iterates the registry and aggregates perEntry results", () => {
    const registry = [
      {
        id: "a",
        doctorSectionName: "A",
        probeAndHeal: () => ({ id: "a", healed: true, ok: true })
      },
      {
        id: "b",
        doctorSectionName: "B",
        probeAndHeal: () => ({ id: "b", healed: false, ok: true })
      }
    ];
    const out = healRegenerableCaches({ registry, env: {}, warnFn: () => {} });
    expect(out.healed).toBe(true);
    expect(out.perEntry).toEqual([
      { id: "a", healed: true, ok: true },
      { id: "b", healed: false, ok: true }
    ]);
  });

  test("a throwing healer is caught (best-effort) and reported without throwing", () => {
    const registry = [
      {
        id: "boom",
        doctorSectionName: "Boom",
        probeAndHeal: () => {
          throw new Error("kaboom");
        }
      }
    ];
    const warnings = [];
    let out;
    expect(() => {
      out = healRegenerableCaches({ registry, env: {}, warnFn: (m) => warnings.push(String(m)) });
    }).not.toThrow();
    expect(out.healed).toBe(false);
    expect(out.perEntry).toEqual([{ id: "boom", healed: false, ok: false }]);
    expect(warnings.some((m) => m.includes("threw"))).toBe(true);
  });

  test("DXMSG_HOOK_NO_REGENERABLE_HEAL=1 short-circuits to a no-op", () => {
    const probeAndHeal = jest.fn();
    const registry = [{ id: "x", doctorSectionName: "X", probeAndHeal }];
    const out = healRegenerableCaches({
      registry,
      env: { DXMSG_HOOK_NO_REGENERABLE_HEAL: "1" },
      warnFn: () => {}
    });
    expect(out.healed).toBe(false);
    expect(out.perEntry).toEqual([]);
    expect(probeAndHeal).not.toHaveBeenCalled();
  });

  test("forwards healDeps (e.g. a sandbox cacheRoot) into each probeAndHeal", () => {
    let received = null;
    const registry = [
      {
        id: "x",
        doctorSectionName: "X",
        probeAndHeal: (deps) => {
          received = deps;
          return { id: "x", healed: false, ok: true };
        }
      }
    ];
    healRegenerableCaches({
      registry,
      env: {},
      warnFn: () => {},
      healDeps: { cacheRoot: SANDBOX_ROOT }
    });
    expect(received.cacheRoot).toBe(SANDBOX_ROOT);
  });

  test("the real registry (default) is a no-op when the cache root is absent", () => {
    // Integration-ish: drive the real registry with a healDeps existsSyncFn
    // that reports the root absent so nothing is touched.
    const out = healRegenerableCaches({
      env: {},
      warnFn: () => {},
      healDeps: { existsSyncFn: () => false }
    });
    expect(out.healed).toBe(false);
    expect(out.perEntry.map((e) => e.id)).toContain("isolated-managed-jest-cache");
  });
});
