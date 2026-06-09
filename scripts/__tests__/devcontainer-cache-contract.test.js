/**
 * @fileoverview Contract tests for the .devcontainer/ cache mount surface.
 *
 * cache-contract.sh defines the bash arrays CACHE_MOUNT_SOURCES and
 * CACHE_MOUNT_TARGETS that post-create.sh, post-start.sh, validate-caching.sh,
 * and devcontainer.json all rely on. We line-scan rather than `source`-ing the
 * file because Jest runs in pure Node.js — and even when a bash were available,
 * `set -e` + the file's re-source guard would make repeat runs of the test
 * suite spuriously fail. The line-scan also keeps the test fast (<10ms).
 *
 * @cross-platform-regression -- this file spawns `bash` and compares the
 * resolved workspace root against the repo root. A bash `pwd` string
 * (`/d/Code/...`, `/cygdrive/d/...`, `/mnt/d/...`) is NOT string-equal to a
 * Node `path.resolve()` string (`D:\Code\...`) on Windows even when both name
 * the SAME directory, so this suite is platform-divergent and must run on the
 * cross-OS targeted gate (enforced by cross-platform-preflight-coverage.test.js
 * and cross-toolchain-path-comparison-policy.test.js). All path-identity
 * assertions below route BOTH sides through one toolchain via `bashResolveDir`;
 * see .llm/skills/scripting/cross-toolchain-path-comparison.md.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");

const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const DEVCONTAINER_DIR = path.join(REPO_ROOT, ".devcontainer");

/**
 * Parse a `readonly NAME=( "a" "b" )` style array out of a bash file. Tolerant
 * of leading whitespace, single- or double-quoted entries, and inline
 * comments. Throws when the array isn't found.
 *
 * @param {string} content - Raw bash source
 * @param {string} arrayName - The array variable name (without the `$`)
 * @returns {string[]} Array entries (quotes stripped)
 */
function parseBashArray(content, arrayName) {
  const re = new RegExp(
    `^\\s*(?:readonly\\s+|declare\\s+-[a-z]+\\s+)?${arrayName}\\s*=\\s*\\(([\\s\\S]*?)\\)`,
    "m"
  );
  const match = content.match(re);
  if (!match) {
    throw new Error(`bash array ${arrayName} not found`);
  }
  return match[1]
    .split("\n")
    .map((line) => line.replace(/#.*$/, "").trim())
    .filter((line) => line.length > 0)
    .map((line) => line.replace(/^["']|["']$/g, ""));
}

function devcontainerTargetForContractTarget(target) {
  return target.replace("${CACHE_WORKSPACE_ROOT}", "${containerWorkspaceFolder}");
}

describe(".devcontainer cache mount contract", () => {
  const cacheContractPath = path.join(DEVCONTAINER_DIR, "cache-contract.sh");
  const devcontainerJsonPath = path.join(DEVCONTAINER_DIR, "devcontainer.json");
  const dockerfilePath = path.join(DEVCONTAINER_DIR, "Dockerfile");
  const postCreatePath = path.join(DEVCONTAINER_DIR, "post-create.sh");
  const postStartPath = path.join(DEVCONTAINER_DIR, "post-start.sh");
  const validateCachingPath = path.join(DEVCONTAINER_DIR, "validate-caching.sh");

  let cacheContract;
  let devcontainerJson;
  let dockerfile;
  let sources;
  let targets;

  beforeAll(() => {
    cacheContract = fs.readFileSync(cacheContractPath, "utf8");
    devcontainerJson = fs.readFileSync(devcontainerJsonPath, "utf8");
    dockerfile = fs.readFileSync(dockerfilePath, "utf8");
    sources = parseBashArray(cacheContract, "CACHE_MOUNT_SOURCES");
    targets = parseBashArray(cacheContract, "CACHE_MOUNT_TARGETS");
  });

  test("cache-contract.sh exists", () => {
    expect(fs.existsSync(cacheContractPath)).toBe(true);
  });

  test("CACHE_MOUNT_SOURCES has at least 4 entries", () => {
    expect(sources.length).toBeGreaterThanOrEqual(4);
  });

  test("CACHE_MOUNT_TARGETS has the same length as CACHE_MOUNT_SOURCES", () => {
    // Aligned-by-index is the documented contract; a length mismatch would
    // silently shift mounts under the rug.
    expect(targets.length).toBe(sources.length);
  });

  test("each source name appears verbatim in devcontainer.json `mounts`", () => {
    for (const source of sources) {
      expect(devcontainerJson).toContain(`source=${source}`);
    }
  });

  test("each target path appears verbatim in devcontainer.json `mounts`", () => {
    for (const target of targets) {
      expect(devcontainerJson).toContain(`target=${devcontainerTargetForContractTarget(target)}`);
    }
  });

  test("devcontainer keeps Linux node_modules in a named volume", () => {
    const nodeModulesSource = "dxm-node-modules";
    const contractNodeModulesTarget = "${CACHE_WORKSPACE_ROOT}/node_modules";
    const devcontainerNodeModulesTarget = "${containerWorkspaceFolder}/node_modules";

    expect(sources).toContain(nodeModulesSource);
    expect(targets).toContain(contractNodeModulesTarget);
    expect(devcontainerJson).toContain(
      `source=${nodeModulesSource},target=${devcontainerNodeModulesTarget},type=volume`
    );
  });

  test("devcontainer cache contract does not include Unity Library", () => {
    expect(sources).not.toContain("dxm-unity-library-cache");
    expect(targets).not.toContain(
      "/workspaces/com.wallstop-studios.dxmessaging/.unity-test-project/Library"
    );
    expect(devcontainerJson).not.toContain("dxm-unity-library-cache");
  });

  test("Dockerfile pre-creates every cache target that lives under the workspace", () => {
    const workspaceTargets = targets.filter(
      (target) => target.startsWith("/workspaces/") || target.startsWith("${CACHE_WORKSPACE_ROOT}/")
    );

    for (const target of workspaceTargets) {
      if (target.startsWith("${CACHE_WORKSPACE_ROOT}/")) {
        expect(dockerfile).toContain("/workspaces");
      } else {
        expect(dockerfile).toContain(target);
      }
    }
  });

  test("Dockerfile does not pre-create static Unity Library cache target", () => {
    expect(dockerfile).not.toContain(
      "/workspaces/com.wallstop-studios.dxmessaging/.unity-test-project/Library"
    );
  });

  test("devcontainer.json includes the docker-outside-of-docker feature", () => {
    // The feature reference key on the registry; any version is fine.
    expect(devcontainerJson).toMatch(/devcontainers\/features\/docker-outside-of-docker:1/);
  });

  test("devcontainer forwards Unity license and host workspace env vars", () => {
    // Classic serial activation (UNITY_SERIAL + UNITY_EMAIL + UNITY_PASSWORD) is
    // the primary local path, with the ULF fallback (UNITY_LICENSE /
    // UNITY_LICENSE_B64) retained. The floating-license server was retired, so
    // UNITY_LICENSING_SERVER must NOT be forwarded any longer.
    expect(devcontainerJson).not.toContain('"UNITY_LICENSING_SERVER"');
    expect(devcontainerJson).toContain('"UNITY_LICENSE"');
    expect(devcontainerJson).toContain('"UNITY_LICENSE_B64"');
    expect(devcontainerJson).toContain('"UNITY_SERIAL"');
    expect(devcontainerJson).toContain('"UNITY_EMAIL"');
    expect(devcontainerJson).toContain('"UNITY_PASSWORD"');
    expect(devcontainerJson).toContain('"LOCAL_WORKSPACE_FOLDER": "${localWorkspaceFolder}"');
  });

  test("Dockerfile declares the BuildKit syntax directive on the first line", () => {
    // First non-empty line must be a `# syntax=docker/dockerfile:<v>`
    // directive — Docker only honors it when it is the very first line.
    const firstLine = dockerfile.split(/\r?\n/)[0];
    expect(firstLine).toMatch(/^#\s*syntax=docker\/dockerfile:1\.\d+/);
  });

  test("post-create.sh sources cache-contract.sh", () => {
    const postCreate = fs.readFileSync(postCreatePath, "utf8");
    expect(postCreate).toMatch(/source\s+["']?[^"'\s]*cache-contract\.sh/);
  });

  test("post-start.sh sources cache-contract.sh", () => {
    const postStart = fs.readFileSync(postStartPath, "utf8");
    expect(postStart).toMatch(/source\s+["']?[^"'\s]*cache-contract\.sh/);
  });

  test("validate-caching.sh checks both devcontainer workflows", () => {
    const validateCaching = fs.readFileSync(validateCachingPath, "utf8");

    expect(validateCaching).toContain(".github/workflows/devcontainer-test.yml");
    expect(validateCaching).toContain(".github/workflows/devcontainer-prebuild.yml");
    expect(validateCaching).toContain('docker push "${IMAGE}"');
    expect(validateCaching).toContain('docker pull "${IMAGE}"');
  });
});

// =============================================================================
// CACHE_WORKSPACE_ROOT resolution (anti-drift).
//
// Guards against the silent workspace-path drift bug: cache-contract.sh used to
// hardcode an absolute fallback (`/workspaces/com.wallstop-studios.dxmessaging`)
// that could diverge from the real devcontainer.json mount target if the repo
// was moved/renamed. The fallback is now DERIVED from the script's own location
// (parent of .devcontainer/ == workspace root == ${containerWorkspaceFolder}).
// These tests assert (a) no brittle literal is reintroduced, (b) the derived
// fallback resolves to the repo root, (c) explicit WORKSPACE_FOLDER wins, (d)
// the node_modules contract target lines up with the devcontainer.json mount,
// and (e) the diagnostic helper reports both resolution branches. We spawn a
// fresh `bash -c` per case so the re-source guard / readonly do not interfere.
// =============================================================================
(canRunBash() ? describe : describe.skip)("CACHE_WORKSPACE_ROOT resolution (anti-drift)", () => {
  const cacheContractPath = path.join(DEVCONTAINER_DIR, "cache-contract.sh");
  const devcontainerJsonPath = path.join(DEVCONTAINER_DIR, "devcontainer.json");

  let cacheContract;
  let devcontainerJson;

  beforeAll(() => {
    cacheContract = fs.readFileSync(cacheContractPath, "utf8");
    devcontainerJson = fs.readFileSync(devcontainerJsonPath, "utf8");
  });

  // Resolve an absolute directory through the SAME `bash` that produces
  // CACHE_WORKSPACE_ROOT (a `cd ... && pwd`), so BOTH sides of every
  // path-identity assertion live in one path-convention space. This is the
  // load-bearing anti-drift detail: on Windows the bash flavor git invokes
  // (Git-Bash / MSYS / Cygwin / WSL) prints a POSIX mount form
  // (`/d/...`, `/cygdrive/d/...`, `/mnt/d/...`) that is NOT string-equal to
  // Node's `path.resolve()` output (`D:\Code\...`) even when both name the
  // same directory. Comparing a bash `pwd` to a Node `path` string therefore
  // PASSES on Linux/macOS and FAILS on Windows -- the exact drift that reached
  // a contributor's pre-push. Routing the Node-side reference through bash too
  // neutralizes the pwd-SPELLING axis.
  //
  // SCOPE: bashResolveDir normalizes separators to `/` (a no-op on Linux/macOS)
  // and does a `cd -- "$1" && pwd`, so it only neutralizes the pwd-spelling
  // axis. resolveWorkspaceRoot(undefined) below instead exercises the
  // production fallback, which runs `dirname` on a Node-interpolated
  // BASH_SOURCE that is backslash-separated on Windows. GNU `dirname` splits
  // only on `/`, so that is a SECOND, independent divergence axis. It is closed
  // in production (cache-contract.sh normalizes `\` -> `/` before `dirname`),
  // not by this helper. The @cross-platform-regression marker + windows-latest
  // gate (cross-platform-preflight.yml) backstop any residual axis as an
  // attributed CI failure rather than silent drift.
  // See .llm/skills/scripting/cross-toolchain-path-comparison.md.
  function bashResolveDir(absoluteDir) {
    // `cd` accepts the forward-slash form on every bash flavor; a raw-backslash
    // Windows path (`D:\Code\...`) is flavor-dependent. Normalize in JS, then
    // pass as an argv element (never interpolated) so spaces and shell
    // metacharacters cannot inject.
    const posixDir = absoluteDir.replace(/\\/g, "/");
    const result = childProcess.spawnSync("bash", ["-c", 'cd -- "$1" && pwd', "bash", posixDir], {
      encoding: "utf8"
    });
    expect(result.status).toBe(0);
    return result.stdout.trim();
  }

  // Source cache-contract.sh in a fresh shell and echo the resolved root.
  function resolveWorkspaceRoot(workspaceFolderValue) {
    const command =
      workspaceFolderValue === undefined
        ? `unset WORKSPACE_FOLDER; source "${cacheContractPath}"; printf '%s' "$CACHE_WORKSPACE_ROOT"`
        : `source "${cacheContractPath}"; printf '%s' "$CACHE_WORKSPACE_ROOT"`;
    const env = { ...process.env };
    if (workspaceFolderValue === undefined) {
      env.WORKSPACE_FOLDER = "";
    } else {
      env.WORKSPACE_FOLDER = workspaceFolderValue;
    }
    const result = childProcess.spawnSync("bash", ["-c", command], {
      cwd: REPO_ROOT,
      encoding: "utf8",
      env
    });
    expect(result.status).toBe(0);
    return result.stdout.trim();
  }

  test("no hardcoded absolute workspace literal in cache-contract.sh", () => {
    // A stale absolute literal silently diverges from the real mount target.
    expect(cacheContract).not.toContain("/workspaces/com.wallstop-studios.dxmessaging");
  });

  test("derived fallback (WORKSPACE_FOLDER unset) resolves to the repo root", () => {
    // Both sides land in one path-convention space: bashResolveDir(REPO_ROOT)
    // routes the Node reference through bash `pwd` (neutralizing the pwd-
    // spelling axis), and resolveWorkspaceRoot's production fallback normalizes
    // a backslash BASH_SOURCE to `/` before `dirname` (neutralizing the
    // dirname-on-backslash axis). Comparing the bash result to the raw Node
    // `REPO_ROOT` string would pass on Linux/macOS and FAIL on Windows for the
    // same directory.
    expect(resolveWorkspaceRoot(undefined)).toBe(bashResolveDir(REPO_ROOT));
  });

  test("explicit WORKSPACE_FOLDER wins (precedence over derived fallback)", () => {
    expect(resolveWorkspaceRoot("/tmp/some-other-workspace")).toBe("/tmp/some-other-workspace");
  });

  test("node_modules contract target aligns with devcontainer.json mount", () => {
    const resolvedRoot = resolveWorkspaceRoot(undefined);
    // The fallback maps onto the real mount target under the
    // ${containerWorkspaceFolder} <-> repoRoot identity. Both sides routed
    // through bash `pwd` (see bashResolveDir) so the compare is
    // convention-agnostic across Linux/macOS/Windows bash flavors.
    expect(`${resolvedRoot}/node_modules`).toBe(`${bashResolveDir(REPO_ROOT)}/node_modules`);
    expect(devcontainerJson).toContain(
      "source=dxm-node-modules,target=${containerWorkspaceFolder}/node_modules,type=volume"
    );
  });

  test("diagnostic helper reports both resolution branches", () => {
    function describe(workspaceFolderValue) {
      const command =
        workspaceFolderValue === undefined
          ? `unset WORKSPACE_FOLDER; source "${cacheContractPath}"; cache_contract_describe_workspace_root`
          : `source "${cacheContractPath}"; cache_contract_describe_workspace_root`;
      const env = { ...process.env };
      if (workspaceFolderValue === undefined) {
        env.WORKSPACE_FOLDER = "";
      } else {
        env.WORKSPACE_FOLDER = workspaceFolderValue;
      }
      const result = childProcess.spawnSync("bash", ["-c", command], {
        cwd: REPO_ROOT,
        encoding: "utf8",
        env
      });
      expect(result.status).toBe(0);
      return result.stdout;
    }

    expect(describe("/tmp/some-other-workspace")).toContain("(from WORKSPACE_FOLDER env)");
    expect(describe(undefined)).toContain("(derived from script location; WORKSPACE_FOLDER unset)");
  });
});

/**
 * Inline behavioural tests for matches_expected_mount in validate-caching.sh.
 * The function previously used a brittle `*",type=volume"*` glob that happened
 * to tolerate extra mount fields by accident. The hardened version parses the
 * comma-separated `key=value` pairs explicitly so:
 *   - additional spec-allowed fields (bind-propagation, consistency, ...) are
 *     accepted, with an INFO diagnostic emitted to stderr,
 *   - permuted ordering still matches (key=value pair order is irrelevant),
 *   - type other than `volume` (e.g. bind) is rejected.
 */
function canRunBash() {
  try {
    const result = childProcess.spawnSync("bash", ["--version"], {
      stdio: "ignore"
    });
    return result.status === 0;
  } catch {
    return false;
  }
}

const HAS_BASH_FOR_MOUNT_FN = canRunBash();

(HAS_BASH_FOR_MOUNT_FN ? describe : describe.skip)(
  "matches_expected_mount (validate-caching.sh)",
  () => {
    const VALIDATE_CACHING_PATH = path.join(DEVCONTAINER_DIR, "validate-caching.sh");

    // The function uses ${BLUE}/${NC} for the INFO diagnostic. We declare
    // them empty so the script under test prints clean text we can assert on.
    // Extract the function body once and stage it to a temp file. This is
    // more robust than inline `$(awk ...)` substitution -- variable
    // interpolation and parentheses inside the extracted function survive
    // the cleaner code path. The test does NOT use `set -e` so the false
    // branch of the function does not abort the shell before NOMATCH.
    const extractFunction = () => {
      const content = fs.readFileSync(VALIDATE_CACHING_PATH, "utf8");
      const startIdx = content.indexOf("matches_expected_mount() {");
      if (startIdx < 0) {
        throw new Error("matches_expected_mount() not found in validate-caching.sh");
      }
      // Find the first standalone `}` line after startIdx.
      const after = content.slice(startIdx);
      const closeIdx = after.search(/\n\}\n/);
      if (closeIdx < 0) {
        throw new Error("matches_expected_mount() close brace not found");
      }
      return after.slice(0, closeIdx + 2); // include the closing }\n
    };
    const MATCHES_FN_BODY = extractFunction();

    let tempDir;
    let scriptPath;
    beforeAll(() => {
      tempDir = makeTempDir("matches-expected-mount");
      scriptPath = path.join(tempDir, "harness.sh");
      const wrapper = [
        "#!/usr/bin/env bash",
        'BLUE=""',
        'NC=""',
        MATCHES_FN_BODY,
        'if matches_expected_mount "$1" "$2" "$3"; then',
        "  echo MATCH",
        "else",
        "  echo NOMATCH",
        "fi",
        ""
      ].join("\n");
      fs.writeFileSync(scriptPath, wrapper, "utf8");
      fs.chmodSync(scriptPath, 0o755);
    });
    afterAll(() => {
      cleanupDir(tempDir);
    });

    function runMatches(mountEntry, sourceName, targetDir) {
      return childProcess.spawnSync("bash", [scriptPath, mountEntry, sourceName, targetDir], {
        encoding: "utf8"
      });
    }

    // Table-driven cases. Each row carries the runMatches args, the expected
    // exit status (0 where the original asserted it, undefined where it did
    // not), the expected stdout sentinel, and optional stderr regex
    // expectations. `stderrEmpty` distinguishes the canonical case's
    // `stderr === ""` assertion from cases that made no stderr assertion.
    const cases = [
      {
        description: "matches canonical source,target,type-volume entry",
        mountEntry: "source=dxm-cache,target=/cache,type=volume",
        sourceName: "dxm-cache",
        targetDir: "/cache",
        expectedStatus: 0,
        expectedStdout: "MATCH",
        stderrEmpty: true
      },
      {
        description: "permuted field order still matches",
        mountEntry: "type=volume,target=/cache,source=dxm-cache",
        sourceName: "dxm-cache",
        targetDir: "/cache",
        expectedStatus: 0,
        expectedStdout: "MATCH"
      },
      {
        description: "accepts spec-allowed extra fields and surfaces INFO diagnostic",
        mountEntry: "source=dxm-cache,target=/cache,type=volume,bind-propagation=rprivate",
        sourceName: "dxm-cache",
        targetDir: "/cache",
        expectedStatus: 0,
        expectedStdout: "MATCH",
        expectedStderr: [/INFO/, /bind-propagation=rprivate/]
      },
      {
        description: "rejects entry with wrong type (bind instead of volume)",
        mountEntry: "source=dxm-cache,target=/cache,type=bind",
        sourceName: "dxm-cache",
        targetDir: "/cache",
        expectedStdout: "NOMATCH"
      },
      {
        description: "rejects entry with wrong source name",
        mountEntry: "source=other,target=/cache,type=volume",
        sourceName: "dxm-cache",
        targetDir: "/cache",
        expectedStdout: "NOMATCH"
      },
      {
        description: "rejects entry with wrong target dir",
        mountEntry: "source=dxm-cache,target=/elsewhere,type=volume",
        sourceName: "dxm-cache",
        targetDir: "/cache",
        expectedStdout: "NOMATCH"
      }
    ];

    test.each(cases)(
      "$description",
      ({
        mountEntry,
        sourceName,
        targetDir,
        expectedStatus,
        expectedStdout,
        expectedStderr,
        stderrEmpty
      }) => {
        const result = runMatches(mountEntry, sourceName, targetDir);
        if (expectedStatus !== undefined) {
          expect(result.status).toBe(expectedStatus);
        }
        expect(result.stdout.trim()).toBe(expectedStdout);
        if (stderrEmpty) {
          expect(result.stderr).toBe("");
        }
        if (expectedStderr) {
          for (const re of expectedStderr) {
            expect(result.stderr).toMatch(re);
          }
        }
      }
    );
  }
);

// =============================================================================
// Round-3 NIT-E: sourcing guard. validate-caching.sh must NOT run the full
// validation flow when sourced; only the helper library imports above the
// guard should fire. Confirmed by spawning a child bash that sources the
// file and inspecting the resulting environment + stdout.
// =============================================================================
(HAS_BASH_FOR_MOUNT_FN ? describe : describe.skip)("validate-caching.sh sourcing guard", () => {
  const VALIDATE_CACHING_PATH = path.join(DEVCONTAINER_DIR, "validate-caching.sh");

  test("sourcing the script does NOT run the validation flow", () => {
    // When sourced, the BASH_SOURCE[0] != $0 guard returns 0 BEFORE the
    // validation flow's counters (CHECKS_PASSED, CHECKS_TOTAL, ...) are
    // initialized. We assert both that the script does not exit non-zero
    // AND that the validation summary block was never emitted to stdout.
    const child = childProcess.spawnSync(
      "bash",
      [
        "-c",
        // Use `source` so $0 of the parent shell (bash -c) differs from
        // BASH_SOURCE[0] of the sourced file. Print a sentinel afterwards
        // so we can tell sourcing returned rather than process-exiting.
        `source "${VALIDATE_CACHING_PATH}"; echo "DONE_SOURCING"; echo "CHECKS_TOTAL=\${CHECKS_TOTAL:-<unset>}"`
      ],
      { encoding: "utf8" }
    );

    expect(child.status).toBe(0);
    expect(child.stdout).toContain("DONE_SOURCING");
    // The validation flow header ("Checking Contract and Static Files")
    // would print BEFORE the sentinel if the guard were broken. Its
    // absence is the load-bearing assertion.
    expect(child.stdout).not.toContain("Checking Contract and Static Files");
    expect(child.stdout).not.toContain("Validation Summary");
    // Counter variables are defined AFTER the guard returns, so they
    // must be unset when sourcing completes.
    expect(child.stdout).toContain("CHECKS_TOTAL=<unset>");
  });

  test("executing the script directly DOES run the validation flow", () => {
    // Sanity check the other direction: the guard must not over-fire.
    // We do NOT assert the final exit status (the runtime mount-point
    // check is expected to fail outside a properly-mounted container);
    // we only assert that the validation flow's stdout headers appear,
    // which proves the guard let execution through.
    const child = childProcess.spawnSync("bash", [VALIDATE_CACHING_PATH], { encoding: "utf8" });

    expect(child.stdout).toContain("Checking Contract and Static Files");
    expect(child.stdout).toContain("Validation Summary");
  });
});
