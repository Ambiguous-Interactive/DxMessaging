/**
 * @fileoverview Tests for scripts/validate-comparison-packages.js.
 *
 * Covers the single-source schema validator, the gated-asmdef collector, both
 * cross-check directions (asmdef <-> single source, committed manifest <->
 * single source), the generator wiring guard, and an end-to-end run of main()
 * (plus a spawned process) against the REAL repository to prove the single
 * source, the comparison asmdefs, and the committed local-parity manifest are
 * mutually consistent.
 *
 * Negative cases build an ISOLATED fixture repo by deep-copying the five real
 * inputs (single source, the gated asmdefs at their real relative paths, the
 * local manifest, the package lock, the generator) into a gitignored
 * `dxm-cmp-test-*` scratch dir INSIDE the repo (repo policy forbids os.tmpdir()
 * fixtures), mutate exactly one thing, and assert the matching failure. The
 * real tree is never mutated.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");

const {
  validateSourceSchema,
  scopeCoversId,
  collectGatedAsmdefs,
  checkAsmdefCrossReference,
  checkLocalManifest,
  checkLocalPackageLock,
  checkGeneratorWired,
  main,
  SOURCE_RELATIVE_PATH,
  LOCAL_MANIFEST_RELATIVE_PATH,
  LOCAL_PACKAGE_LOCK_RELATIVE_PATH,
  COMPARISONS_RELATIVE_DIR,
  GENERATOR_RELATIVE_PATH
} = require("../validate-comparison-packages.js");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const VALIDATOR_PATH = path.resolve(__dirname, "../validate-comparison-packages.js");

const VALID_SOURCE = Object.freeze({
  registry: {
    name: "package.openupm.com",
    url: "https://package.openupm.com",
    scopes: ["com.cysharp", "com.svermeulen", "com.neuecc", "com.unity-atoms"]
  },
  packages: {
    "com.cysharp.messagepipe": "1.8.1",
    "com.cysharp.unitask": "2.5.11",
    "com.svermeulen.extenject": "9.2.0-stcf3",
    "com.neuecc.unirx": "7.1.0",
    "com.unity-atoms.unity-atoms-core": "4.6.1",
    "com.unity-atoms.unity-atoms-base-atoms": "4.6.1"
  },
  unityBuiltInPackages: {
    "com.unity.modules.animation": "1.0.0",
    "com.unity.ugui": "1.0.0",
    "com.unity.modules.unitywebrequest": "1.0.0",
    "com.unity.modules.unitywebrequestwww": "1.0.0",
    "com.unity.modules.assetbundle": "1.0.0"
  },
  defines: {
    "com.cysharp.messagepipe": "MESSAGEPIPE_PRESENT",
    "com.cysharp.unitask": "UNITASK_PRESENT",
    "com.svermeulen.extenject": "ZENJECT_PRESENT",
    "com.neuecc.unirx": "UNIRX_PRESENT",
    "com.unity-atoms.unity-atoms-core": "UNITY_ATOMS_CORE_PRESENT",
    "com.unity-atoms.unity-atoms-base-atoms": "UNITY_ATOMS_BASE_ATOMS_PRESENT"
  },
  minUnityForComparisons: "2021.3.0f1"
});

/** Deep clone via JSON (all fixtures are plain JSON-serializable objects). */
function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

describe("validateSourceSchema", () => {
  test("accepts the valid single-source object", () => {
    expect(validateSourceSchema(VALID_SOURCE)).toEqual([]);
  });

  test.each([
    ["null root", null],
    ["array root", []],
    ["string root", "x"]
  ])("rejects %s", (_name, data) => {
    expect(validateSourceSchema(data).length).toBeGreaterThan(0);
  });

  test("rejects a non-https registry url", () => {
    const data = clone(VALID_SOURCE);
    data.registry.url = "http://package.openupm.com";
    const errors = validateSourceSchema(data);
    expect(errors.join("\n")).toMatch(/must start with 'https:\/\/'/);
  });

  test("rejects an empty registry url", () => {
    const data = clone(VALID_SOURCE);
    data.registry.url = "";
    expect(validateSourceSchema(data).join("\n")).toMatch(/`registry.url` must be a non-empty/);
  });

  test("rejects empty registry scopes", () => {
    const data = clone(VALID_SOURCE);
    data.registry.scopes = [];
    expect(validateSourceSchema(data).join("\n")).toMatch(/`registry.scopes` must be a non-empty/);
  });

  test("rejects an empty packages object", () => {
    const data = clone(VALID_SOURCE);
    data.packages = {};
    data.defines = {};
    expect(validateSourceSchema(data).join("\n")).toMatch(/at least one entry/);
  });

  test("rejects an empty version string", () => {
    const data = clone(VALID_SOURCE);
    data.packages["com.cysharp.unitask"] = "";
    expect(validateSourceSchema(data).join("\n")).toMatch(/must be a non-empty version string/);
  });

  test.each([
    [
      "missing unityBuiltInPackages",
      (data) => {
        delete data.unityBuiltInPackages;
      },
      /`unityBuiltInPackages` must be an object/
    ],
    [
      "empty unityBuiltInPackages",
      (data) => {
        data.unityBuiltInPackages = {};
      },
      /`unityBuiltInPackages` must have at least one entry/
    ],
    [
      "non-Unity built-in package id",
      (data) => {
        data.unityBuiltInPackages["com.example.notbuiltin"] = "1.0.0";
      },
      /must start with 'com\.unity\.'/
    ],
    [
      "non-1.0.0 built-in package version",
      (data) => {
        data.unityBuiltInPackages["com.unity.ugui"] = "2.0.0";
      },
      /must be the Unity built-in package version '1\.0\.0'/
    ],
    [
      "built-in duplicated in OpenUPM packages",
      (data) => {
        data.packages["com.unity.ugui"] = "1.0.0";
      },
      /must not also appear in `packages`/
    ]
  ])("rejects %s", (_name, mutate, expected) => {
    const data = clone(VALID_SOURCE);
    mutate(data);
    expect(validateSourceSchema(data).join("\n")).toMatch(expected);
  });

  test("rejects a package with no defines entry", () => {
    const data = clone(VALID_SOURCE);
    delete data.defines["com.cysharp.unitask"];
    const errors = validateSourceSchema(data);
    expect(errors.join("\n")).toMatch(
      /`defines` is missing an entry for package 'com.cysharp.unitask'/
    );
  });

  test("rejects a defines entry with no matching package (extra)", () => {
    const data = clone(VALID_SOURCE);
    data.defines["com.extra.package"] = "EXTRA_PRESENT";
    const errors = validateSourceSchema(data);
    expect(errors.join("\n")).toMatch(/not a package in `packages` \(no extras allowed\)/);
  });

  test("rejects duplicate package define mappings", () => {
    const data = clone(VALID_SOURCE);
    data.defines["com.unity-atoms.unity-atoms-base-atoms"] =
      data.defines["com.unity-atoms.unity-atoms-core"];
    const errors = validateSourceSchema(data);
    expect(errors.join("\n")).toMatch(
      /define 'UNITY_ATOMS_CORE_PRESENT' is assigned to multiple packages/
    );
  });

  test("rejects a package id not covered by any scope", () => {
    const data = clone(VALID_SOURCE);
    data.packages["com.uncovered.lib"] = "1.0.0";
    data.defines["com.uncovered.lib"] = "UNCOVERED_PRESENT";
    const errors = validateSourceSchema(data);
    expect(errors.join("\n")).toMatch(/is not covered by any `registry.scopes` prefix/);
  });
});

describe("scopeCoversId", () => {
  test("exact id == scope is covered", () => {
    expect(scopeCoversId("com.cysharp", "com.cysharp")).toBe(true);
  });

  test("dotted descendant is covered", () => {
    expect(scopeCoversId("com.cysharp.unitask", "com.cysharp")).toBe(true);
  });

  test("a non-dot-boundary prefix is NOT covered", () => {
    expect(scopeCoversId("com.cysharpX.lib", "com.cysharp")).toBe(false);
  });

  test("an unrelated id is NOT covered", () => {
    expect(scopeCoversId("com.neuecc.unirx", "com.cysharp")).toBe(false);
  });
});

describe("checkAsmdefCrossReference", () => {
  const packages = VALID_SOURCE.packages;
  const defines = VALID_SOURCE.defines;

  const goodGated = [
    {
      relativePath: `${COMPARISONS_RELATIVE_DIR}/External/External.asmdef`,
      defineConstraints: [
        "UNITY_INCLUDE_TESTS",
        "MESSAGEPIPE_PRESENT",
        "UNIRX_PRESENT",
        "ZENJECT_PRESENT",
        "UNITASK_PRESENT"
      ],
      versionDefines: [
        { name: "com.cysharp.messagepipe", define: "MESSAGEPIPE_PRESENT" },
        { name: "com.neuecc.unirx", define: "UNIRX_PRESENT" },
        { name: "com.svermeulen.extenject", define: "ZENJECT_PRESENT" },
        { name: "com.cysharp.unitask", define: "UNITASK_PRESENT" }
      ]
    },
    {
      relativePath: `${COMPARISONS_RELATIVE_DIR}/UnityAtoms/UnityAtoms.asmdef`,
      defineConstraints: [
        "UNITY_INCLUDE_TESTS",
        "UNITY_ATOMS_CORE_PRESENT",
        "UNITY_ATOMS_BASE_ATOMS_PRESENT"
      ],
      versionDefines: [
        { name: "com.unity-atoms.unity-atoms-core", define: "UNITY_ATOMS_CORE_PRESENT" },
        {
          name: "com.unity-atoms.unity-atoms-base-atoms",
          define: "UNITY_ATOMS_BASE_ATOMS_PRESENT"
        }
      ]
    }
  ];

  test("passes when asmdefs agree with the single source", () => {
    expect(
      checkAsmdefCrossReference({ packages, defines, gatedAsmdefs: clone(goodGated) })
    ).toEqual([]);
  });

  test("fails when there are no gated asmdefs", () => {
    const violations = checkAsmdefCrossReference({ packages, defines, gatedAsmdefs: [] });
    expect(violations.length).toBeGreaterThan(0);
    expect(violations.join("\n")).toMatch(/no gated comparison asmdef/);
  });

  test("fails when a single-source define is not constrained anywhere (direction a)", () => {
    const gated = clone(goodGated);
    // Drop UNITASK_PRESENT from BOTH the constraints and the versionDefines so
    // the define is no longer constrained nor produced.
    gated[0].defineConstraints = gated[0].defineConstraints.filter((c) => c !== "UNITASK_PRESENT");
    gated[0].versionDefines = gated[0].versionDefines.filter((v) => v.define !== "UNITASK_PRESENT");
    const violations = checkAsmdefCrossReference({ packages, defines, gatedAsmdefs: gated });
    expect(violations.join("\n")).toMatch(/UNITASK_PRESENT.*defineConstraints/s);
  });

  test("fails when a versionDefines name is not a known package (direction b)", () => {
    const gated = clone(goodGated);
    gated[0].versionDefines.push({ name: "com.unknown.pkg", define: "MESSAGEPIPE_PRESENT" });
    const violations = checkAsmdefCrossReference({ packages, defines, gatedAsmdefs: gated });
    expect(violations.join("\n")).toMatch(/'com.unknown.pkg' is not a\s+package id/);
  });

  test("fails when an asmdef maps a package to the wrong define (direction b)", () => {
    const gated = clone(goodGated);
    gated[0].versionDefines[0].define = "WRONG_DEFINE";
    const violations = checkAsmdefCrossReference({ packages, defines, gatedAsmdefs: gated });
    expect(violations.join("\n")).toMatch(
      /maps package 'com.cysharp.messagepipe' to define 'WRONG_DEFINE'/
    );
  });

  test("fails when an asmdef produces a package define without constraining it locally", () => {
    const gated = clone(goodGated);
    gated[1].defineConstraints = gated[1].defineConstraints.filter(
      (constraint) => constraint !== "UNITY_ATOMS_BASE_ATOMS_PRESENT"
    );
    const violations = checkAsmdefCrossReference({ packages, defines, gatedAsmdefs: gated });
    expect(violations.join("\n")).toMatch(
      /produces package define 'UNITY_ATOMS_BASE_ATOMS_PRESENT'.*does not require it/s
    );
  });

  test("fails when an asmdef constrains a single-source define it does not produce", () => {
    const gated = clone(goodGated);
    gated[1].defineConstraints.push("UNITASK_PRESENT");
    const violations = checkAsmdefCrossReference({ packages, defines, gatedAsmdefs: gated });
    expect(violations.join("\n")).toMatch(
      /requires single-source define 'UNITASK_PRESENT'.*does not produce it/s
    );
  });

  test("fails when one asmdef declares the same versionDefines package twice", () => {
    const gated = clone(goodGated);
    gated[0].versionDefines.push({ ...gated[0].versionDefines[0] });
    const violations = checkAsmdefCrossReference({ packages, defines, gatedAsmdefs: gated });
    expect(violations.join("\n")).toMatch(
      /declares package 'com\.cysharp\.messagepipe' multiple times/
    );
  });
});

describe("checkLocalManifest", () => {
  const registry = VALID_SOURCE.registry;
  const packages = VALID_SOURCE.packages;
  const unityBuiltInPackages = VALID_SOURCE.unityBuiltInPackages;

  function goodManifest() {
    const dependencies = {};
    for (const pins of [packages, unityBuiltInPackages]) {
      for (const [id, version] of Object.entries(pins)) {
        dependencies[id] = version;
      }
    }
    return {
      dependencies,
      scopedRegistries: [
        {
          name: "package.openupm.com",
          url: registry.url,
          // Superset is allowed: an extra scope must not fail.
          scopes: [...registry.scopes, "com.extra.allowed"]
        }
      ],
      testables: ["com.wallstop-studios.dxmessaging"]
    };
  }

  test("passes when the manifest carries the registry superset + exact pins", () => {
    expect(
      checkLocalManifest({ manifest: goodManifest(), registry, packages, unityBuiltInPackages })
    ).toEqual([]);
  });

  test.each([
    [
      "pinned comparison package",
      "com.neuecc.unirx",
      /missing pinned comparison package 'com\.neuecc\.unirx'/
    ],
    [
      "required Unity built-in package",
      "com.unity.ugui",
      /missing required Unity built-in package 'com\.unity\.ugui'/
    ]
  ])("fails on a missing %s", (_label, dependencyId, expected) => {
    const manifest = goodManifest();
    delete manifest.dependencies[dependencyId];
    const violations = checkLocalManifest({ manifest, registry, packages, unityBuiltInPackages });
    expect(violations.join("\n")).toMatch(expected);
  });

  test.each([
    ["pinned comparison package", "com.cysharp.unitask", "2.0.0"],
    ["required Unity built-in package", "com.unity.modules.animation", "2.0.0"]
  ])("fails on a %s version mismatch", (_label, dependencyId, driftedVersion) => {
    const manifest = goodManifest();
    manifest.dependencies[dependencyId] = driftedVersion;
    const violations = checkLocalManifest({ manifest, registry, packages, unityBuiltInPackages });
    expect(violations.join("\n")).toContain(
      `dependencies.${dependencyId}\` is '${driftedVersion}'`
    );
  });

  test("fails on a scoped-registry url mismatch", () => {
    const manifest = goodManifest();
    manifest.scopedRegistries[0].url = "https://example.com";
    const violations = checkLocalManifest({ manifest, registry, packages, unityBuiltInPackages });
    expect(violations.join("\n")).toMatch(/no `scopedRegistries` entry has url/);
  });

  test("fails on a missing scope (not a superset)", () => {
    const manifest = goodManifest();
    manifest.scopedRegistries[0].scopes = ["com.cysharp"];
    const violations = checkLocalManifest({ manifest, registry, packages, unityBuiltInPackages });
    expect(violations.join("\n")).toMatch(/is missing scope 'com.svermeulen'/);
  });
});

describe("checkLocalPackageLock", () => {
  const registry = VALID_SOURCE.registry;
  const packages = VALID_SOURCE.packages;
  const unityBuiltInPackages = VALID_SOURCE.unityBuiltInPackages;

  function goodPackageLock() {
    const dependencies = {};
    for (const [id, version] of Object.entries(packages)) {
      dependencies[id] = {
        version,
        depth: 0,
        source: "registry",
        dependencies: {},
        url: registry.url
      };
    }
    for (const [id, version] of Object.entries(unityBuiltInPackages)) {
      dependencies[id] = {
        version,
        depth: 0,
        source: "builtin",
        dependencies: {}
      };
    }
    return { dependencies };
  }

  test("passes when the package lock carries exact direct pins", () => {
    expect(
      checkLocalPackageLock({
        packageLock: goodPackageLock(),
        registry,
        packages,
        unityBuiltInPackages
      })
    ).toEqual([]);
  });

  test.each([
    [
      "pinned comparison package",
      "com.cysharp.messagepipe",
      /missing pinned comparison package 'com\.cysharp\.messagepipe'/
    ],
    [
      "required Unity built-in package",
      "com.unity.ugui",
      /missing required Unity built-in package 'com\.unity\.ugui'/
    ]
  ])("fails on a missing %s", (_label, dependencyId, expected) => {
    const packageLock = goodPackageLock();
    delete packageLock.dependencies[dependencyId];
    const violations = checkLocalPackageLock({
      packageLock,
      registry,
      packages,
      unityBuiltInPackages
    });
    expect(violations.join("\n")).toMatch(expected);
  });

  test.each([
    ["version", "com.cysharp.unitask", (entry) => (entry.version = "0.0.0")],
    ["depth", "com.neuecc.unirx", (entry) => (entry.depth = 1)],
    ["source", "com.unity.ugui", (entry) => (entry.source = "registry")],
    ["registry url", "com.svermeulen.extenject", (entry) => (entry.url = "https://example.com")]
  ])("fails on %s drift", (_name, dependencyId, mutate) => {
    const packageLock = goodPackageLock();
    mutate(packageLock.dependencies[dependencyId]);
    const violations = checkLocalPackageLock({
      packageLock,
      registry,
      packages,
      unityBuiltInPackages
    });
    expect(violations.length).toBeGreaterThan(0);
  });
});

describe("checkGeneratorWired (real repo)", () => {
  test("the real generator references the single source", () => {
    expect(checkGeneratorWired(REPO_ROOT)).toEqual([]);
  });

  test("comments alone do not satisfy the generator wiring guard", () => {
    const dir = fs.mkdtempSync(path.join(REPO_ROOT, "dxm-cmp-test-"));
    try {
      const generatorAbs = path.join(dir, GENERATOR_RELATIVE_PATH);
      fs.mkdirSync(path.dirname(generatorAbs), { recursive: true });
      fs.writeFileSync(
        generatorAbs,
        "# comparison-packages.json\n# unityBuiltInPackages\nfunction New-ManifestJson {}\n",
        "utf8"
      );
      const violations = checkGeneratorWired(dir);
      expect(violations.join("\n")).toMatch(/does not reference 'comparison-packages.json'/);
      expect(violations.join("\n")).toMatch(/does not reference 'unityBuiltInPackages'/);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });
});

describe("collectGatedAsmdefs (real repo)", () => {
  test("collects exactly the two gated comparison asmdefs (excludes the base)", () => {
    const gated = collectGatedAsmdefs(REPO_ROOT);
    expect(gated.length).toBe(2);
    const paths = gated.map((g) => g.relativePath).sort();
    expect(paths.some((p) => p.includes("/External/"))).toBe(true);
    expect(paths.some((p) => p.includes("/UnityAtoms/"))).toBe(true);
    // The shared base asmdef (empty versionDefines) must NOT be collected.
    expect(paths.every((p) => g_isGated(gated, p))).toBe(true);
    for (const asmdef of gated) {
      expect(asmdef.versionDefines.length).toBeGreaterThan(0);
    }
  });
});

function g_isGated(gated, relativePath) {
  const found = gated.find((g) => g.relativePath === relativePath);
  return Boolean(found) && found.versionDefines.length > 0;
}

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
  // fixtures), under a gitignored `dxm-cmp-test-*` prefix, and removed in a
  // finally per test plus a defensive afterAll so validate:untracked-policy is
  // never tripped.
  const createdDirs = [];

  /**
   * Deep-copy the five real inputs into a fresh fixture repoRoot, then apply
   * `mutate(paths)` to drift exactly one thing before running main().
   */
  function makeFixtureRepo(mutate) {
    const dir = fs.mkdtempSync(path.join(REPO_ROOT, "dxm-cmp-test-"));
    createdDirs.push(dir);

    const sourceAbs = path.join(dir, SOURCE_RELATIVE_PATH);
    const manifestAbs = path.join(dir, LOCAL_MANIFEST_RELATIVE_PATH);
    const packageLockAbs = path.join(dir, LOCAL_PACKAGE_LOCK_RELATIVE_PATH);
    const generatorAbs = path.join(dir, GENERATOR_RELATIVE_PATH);

    // Real gated asmdefs, copied at their real relative paths so the collector
    // walks Tests/Runtime/Comparisons/** exactly as in production.
    const gatedRelatives = collectGatedAsmdefs(REPO_ROOT).map((g) => g.relativePath);

    const copyReal = (relativePath) => {
      const from = path.join(REPO_ROOT, relativePath);
      const to = path.join(dir, relativePath);
      fs.mkdirSync(path.dirname(to), { recursive: true });
      fs.copyFileSync(from, to);
      return to;
    };

    copyReal(SOURCE_RELATIVE_PATH);
    copyReal(LOCAL_MANIFEST_RELATIVE_PATH);
    copyReal(LOCAL_PACKAGE_LOCK_RELATIVE_PATH);
    copyReal(GENERATOR_RELATIVE_PATH);
    for (const relativePath of gatedRelatives) {
      copyReal(relativePath);
    }

    const readJson = (absPath) => JSON.parse(fs.readFileSync(absPath, "utf8"));
    const writeJson = (absPath, value) =>
      fs.writeFileSync(absPath, `${JSON.stringify(value, null, 2)}\n`, "utf8");

    mutate({
      dir,
      sourceAbs,
      manifestAbs,
      packageLockAbs,
      generatorAbs,
      gatedAbs: gatedRelatives.map((relativePath) => path.join(dir, relativePath)),
      readJson,
      writeJson
    });

    return dir;
  }

  function runFixture(dir) {
    const errors = [];
    const code = main({
      repoRoot: dir,
      log: () => {},
      errorLog: (message) => errors.push(String(message))
    });
    return { code, errorText: errors.join("\n") };
  }

  afterAll(() => {
    for (const dir of createdDirs) {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  test("an unmodified fixture copy passes (proves the harness is faithful)", () => {
    const dir = makeFixtureRepo(() => {});
    try {
      const { code, errorText } = runFixture(dir);
      expect(errorText).toBe("");
      expect(code).toBe(0);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  test("(b) returns 1 when the local manifest is MISSING a pinned dependency", () => {
    const dir = makeFixtureRepo(({ manifestAbs, readJson, writeJson }) => {
      const manifest = readJson(manifestAbs);
      delete manifest.dependencies["com.neuecc.unirx"];
      writeJson(manifestAbs, manifest);
    });
    try {
      const { code, errorText } = runFixture(dir);
      expect(code).toBe(1);
      expect(errorText).toMatch(/missing pinned comparison package 'com.neuecc.unirx'/);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  test("(b2) returns 1 when the local manifest is MISSING a Unity built-in dependency", () => {
    const dir = makeFixtureRepo(({ manifestAbs, readJson, writeJson }) => {
      const manifest = readJson(manifestAbs);
      delete manifest.dependencies["com.unity.ugui"];
      writeJson(manifestAbs, manifest);
    });
    try {
      const { code, errorText } = runFixture(dir);
      expect(code).toBe(1);
      expect(errorText).toMatch(/missing required Unity built-in package 'com.unity.ugui'/);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  test("(b3) returns 1 when the package lock is MISSING a Unity built-in dependency", () => {
    const dir = makeFixtureRepo(({ packageLockAbs, readJson, writeJson }) => {
      const packageLock = readJson(packageLockAbs);
      delete packageLock.dependencies["com.unity.ugui"];
      writeJson(packageLockAbs, packageLock);
    });
    try {
      const { code, errorText } = runFixture(dir);
      expect(code).toBe(1);
      expect(errorText).toMatch(/missing required Unity built-in package 'com.unity.ugui'/);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  test("(b4) returns 1 when the package lock has a comparison package version mismatch", () => {
    const dir = makeFixtureRepo(({ packageLockAbs, readJson, writeJson }) => {
      const packageLock = readJson(packageLockAbs);
      packageLock.dependencies["com.cysharp.messagepipe"].version = "0.0.0-drift";
      writeJson(packageLockAbs, packageLock);
    });
    try {
      const { code, errorText } = runFixture(dir);
      expect(code).toBe(1);
      expect(errorText).toMatch(/dependencies\.com\.cysharp\.messagepipe\.version/);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  test("(c) returns 1 on a single-source vs local-manifest VERSION mismatch", () => {
    const dir = makeFixtureRepo(({ manifestAbs, readJson, writeJson }) => {
      const manifest = readJson(manifestAbs);
      manifest.dependencies["com.cysharp.unitask"] = "0.0.0-drift";
      writeJson(manifestAbs, manifest);
    });
    try {
      const { code, errorText } = runFixture(dir);
      expect(code).toBe(1);
      expect(errorText).toMatch(/`dependencies.com.cysharp.unitask` is '0.0.0-drift'/);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  test("(d) returns 1 on scoped-registry URL drift", () => {
    const dir = makeFixtureRepo(({ manifestAbs, readJson, writeJson }) => {
      const manifest = readJson(manifestAbs);
      manifest.scopedRegistries[0].url = "https://drifted.example.com";
      writeJson(manifestAbs, manifest);
    });
    try {
      const { code, errorText } = runFixture(dir);
      expect(code).toBe(1);
      expect(errorText).toMatch(/no `scopedRegistries` entry has url/);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  test("(d2) returns 1 on scoped-registry SCOPES drift (not a superset)", () => {
    const dir = makeFixtureRepo(({ manifestAbs, readJson, writeJson }) => {
      const manifest = readJson(manifestAbs);
      manifest.scopedRegistries[0].scopes = ["com.cysharp"];
      writeJson(manifestAbs, manifest);
    });
    try {
      const { code, errorText } = runFixture(dir);
      expect(code).toBe(1);
      expect(errorText).toMatch(/is missing scope/);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  test("(e) returns 1 on an asmdef define/package mismatch", () => {
    const dir = makeFixtureRepo(({ gatedAbs, readJson, writeJson }) => {
      // Pick the asmdef that maps com.cysharp.messagepipe and corrupt its define.
      let mutated = false;
      for (const asmdefAbs of gatedAbs) {
        const asmdef = readJson(asmdefAbs);
        const entry = (asmdef.versionDefines || []).find(
          (v) => v.name === "com.cysharp.messagepipe"
        );
        if (entry) {
          entry.define = "DRIFTED_DEFINE";
          writeJson(asmdefAbs, asmdef);
          mutated = true;
          break;
        }
      }
      expect(mutated).toBe(true);
    });
    try {
      const { code, errorText } = runFixture(dir);
      expect(code).toBe(1);
      expect(errorText).toMatch(
        /maps package 'com.cysharp.messagepipe' to define 'DRIFTED_DEFINE'/
      );
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  test("(f) returns 1 when a package is MISSING its defines entry", () => {
    const dir = makeFixtureRepo(({ sourceAbs, readJson, writeJson }) => {
      const source = readJson(sourceAbs);
      delete source.defines["com.cysharp.unitask"];
      writeJson(sourceAbs, source);
    });
    try {
      const { code, errorText } = runFixture(dir);
      expect(code).toBe(1);
      expect(errorText).toMatch(/`defines` is missing an entry for package 'com.cysharp.unitask'/);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  test("(f2) returns 1 when two packages map to the same define", () => {
    const dir = makeFixtureRepo(({ sourceAbs, readJson, writeJson }) => {
      const source = readJson(sourceAbs);
      source.defines["com.unity-atoms.unity-atoms-base-atoms"] =
        source.defines["com.unity-atoms.unity-atoms-core"];
      writeJson(sourceAbs, source);
    });
    try {
      const { code, errorText } = runFixture(dir);
      expect(code).toBe(1);
      expect(errorText).toMatch(/define 'UNITY_ATOMS_CORE_PRESENT' is assigned to multiple/);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  test("(g) returns 1 when a package id is not covered by any scope", () => {
    const dir = makeFixtureRepo(({ sourceAbs, readJson, writeJson }) => {
      const source = readJson(sourceAbs);
      // Add a package outside every scope, with both a version and a define so
      // ONLY the scope-coverage rule fires (isolating case g).
      source.packages["com.uncovered.lib"] = "1.0.0";
      source.defines["com.uncovered.lib"] = "UNCOVERED_PRESENT";
      writeJson(sourceAbs, source);
    });
    try {
      const { code, errorText } = runFixture(dir);
      expect(code).toBe(1);
      expect(errorText).toMatch(/is not covered by any `registry.scopes` prefix/);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  test("returns 1 when the single source is missing entirely", () => {
    const dir = makeFixtureRepo(({ sourceAbs }) => {
      fs.rmSync(sourceAbs, { force: true });
    });
    try {
      const { code, errorText } = runFixture(dir);
      expect(code).toBe(1);
      expect(errorText).toMatch(/Cannot read '.github\/comparison-packages.json'/);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  test("returns 1 when the generator no longer references the single source", () => {
    const dir = makeFixtureRepo(({ generatorAbs }) => {
      fs.writeFileSync(generatorAbs, "# generator with no single-source reference\n", "utf8");
    });
    try {
      const { code, errorText } = runFixture(dir);
      expect(code).toBe(1);
      expect(errorText).toMatch(/does not reference 'comparison-packages.json'/);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  test("returns 1 when the generator no longer wires Unity built-in packages", () => {
    const dir = makeFixtureRepo(({ generatorAbs }) => {
      const content = fs.readFileSync(generatorAbs, "utf8");
      fs.writeFileSync(generatorAbs, content.replace(/unityBuiltInPackages/g, "unityBuiltIns"), "utf8");
    });
    try {
      const { code, errorText } = runFixture(dir);
      expect(code).toBe(1);
      expect(errorText).toMatch(/does not reference 'unityBuiltInPackages'/);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });
});

describe("exported relative paths", () => {
  test("expose the documented consumer paths", () => {
    expect(SOURCE_RELATIVE_PATH).toBe(".github/comparison-packages.json");
    expect(LOCAL_MANIFEST_RELATIVE_PATH).toBe(".unity-test-project/Packages/manifest.json");
    expect(LOCAL_PACKAGE_LOCK_RELATIVE_PATH).toBe(
      ".unity-test-project/Packages/packages-lock.json"
    );
    expect(COMPARISONS_RELATIVE_DIR).toBe("Tests/Runtime/Comparisons");
    expect(GENERATOR_RELATIVE_PATH).toBe("scripts/unity/run-ci-tests.ps1");
  });
});
