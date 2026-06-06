"use strict";

const childProcess = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const { prependPathEnv } = require("../lib/spawn-env-sandbox");
const { assertSpawnStatus, stdoutText } = require("../lib/pwsh-output");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SCRIPT_PATH = path.join(REPO_ROOT, "scripts", "unity", "capture-perf-baseline.ps1");

const BENCHMARK_PATH = path.join(
  REPO_ROOT,
  "Tests",
  "Runtime",
  "Benchmarks",
  "DispatchThroughputBenchmarks.cs"
);
const REAL_PWSH = (() => {
  const result = childProcess.spawnSync(
    "pwsh",
    ["-NoProfile", "-Command", "(Get-Command pwsh).Source"],
    {
      cwd: REPO_ROOT,
      encoding: "utf8"
    }
  );

  if (result.status !== 0) {
    return null;
  }

  return result.stdout.trim();
})();

function makeTempToolDir() {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "dxmsg-perf-baseline-wrapper-"));
  const binDir = path.join(tempRoot, "bin");
  fs.mkdirSync(binDir);

  const pwshMarker = path.join(tempRoot, "fake-pwsh.json");
  const nodeMarker = path.join(tempRoot, "fake-node.json");

  const fakePwshJs = [
    '"use strict";',
    'const fs = require("fs");',
    'const path = require("path");',
    "const marker = {",
    '  commit: process.env.DX_PERF_COMMIT || "",',
    '  baseline: process.env.DX_PERF_BASELINE || "",',
    '  mode: process.env.DX_PERF_BASELINE_MODE || ""',
    "};",
    "fs.writeFileSync(process.env.FAKE_PWSH_MARKER, `${JSON.stringify(marker)}\\n`);",
    'if (process.env.FAKE_SKIP_BASELINE !== "1" && marker.baseline) {',
    "  const baselinePath = path.resolve(process.cwd(), marker.baseline);",
    "  fs.mkdirSync(path.dirname(baselinePath), { recursive: true });",
    "  fs.writeFileSync(",
    "    baselinePath,",
    '    "scenario,platform,commit,runIndex,emitsPerSecond,allocatedBytesDelta,wallClockMs\\n"',
    "  );",
    "}",
    "process.stdout.write(`fake unity stdout for ${marker.commit}\\n`);",
    "process.stderr.write(`fake unity stderr for ${marker.commit}\\n`);",
    'process.exit(Number.parseInt(process.env.FAKE_PWSH_EXIT || "0", 10));',
    ""
  ].join("\n");

  const fakePwshSh = [
    "#!/usr/bin/env sh",
    "set -eu",
    'exec "$FAKE_REAL_NODE" "$(dirname "$0")/fake-pwsh.js"',
    ""
  ].join("\n");

  const fakePwshCmd = [
    "@echo off",
    '"%FAKE_REAL_NODE%" "%~dp0fake-pwsh.js"',
    "exit /b %ERRORLEVEL%",
    ""
  ].join("\r\n");

  const fakeNodeJs = [
    '"use strict";',
    "require('fs').writeFileSync(process.env.FAKE_NODE_MARKER, 'invoked\\n');",
    "process.exit(99);",
    ""
  ].join("\n");
  const fakeNodeSh = [
    "#!/usr/bin/env sh",
    'exec "$FAKE_REAL_NODE" "$(dirname "$0")/fake-node.js"',
    ""
  ].join("\n");
  const fakeNodeCmd = [
    "@echo off",
    '"%FAKE_REAL_NODE%" "%~dp0fake-node.js"',
    "exit /b %ERRORLEVEL%",
    ""
  ].join("\r\n");

  fs.writeFileSync(path.join(binDir, "fake-pwsh.js"), fakePwshJs);
  fs.writeFileSync(path.join(binDir, "fake-node.js"), fakeNodeJs);
  fs.writeFileSync(path.join(binDir, "pwsh"), fakePwshSh, { mode: 0o755 });
  fs.writeFileSync(path.join(binDir, "pwsh.cmd"), fakePwshCmd);
  fs.writeFileSync(path.join(binDir, "node"), fakeNodeSh, { mode: 0o755 });
  fs.writeFileSync(path.join(binDir, "node.cmd"), fakeNodeCmd);

  return { tempRoot, binDir, pwshMarker, nodeMarker };
}

function runCapture(args, tools, extraEnv = {}) {
  const env = prependPathEnv(
    {
      ...process.env,
      ...extraEnv,
      FAKE_NODE_MARKER: tools.nodeMarker,
      FAKE_PWSH_MARKER: tools.pwshMarker,
      FAKE_REAL_NODE: process.execPath,
      PATHEXT: process.env.PATHEXT || ".COM;.EXE;.BAT;.CMD"
    },
    tools.binDir
  );

  return childProcess.spawnSync(REAL_PWSH, ["-NoProfile", "-File", SCRIPT_PATH, ...args], {
    cwd: REPO_ROOT,
    encoding: "utf8",
    env
  });
}

function cleanupPerfArtifacts(commit) {
  const artifactToken = commit.replace(/[^A-Za-z0-9_.-]/g, "-");
  for (const suffix of ["results.xml", "unity-log.txt"]) {
    fs.rmSync(path.join(REPO_ROOT, ".artifacts", `perf-${artifactToken}-${suffix}`), {
      force: true
    });
  }
}

function expectDockerRelativePath(actual, expected) {
  expect(actual).toBe(expected);
  expect(actual).not.toContain("\\");
}

describe("scripts/unity/capture-perf-baseline.ps1 contract", () => {
  let content;

  beforeAll(() => {
    expect(fs.existsSync(SCRIPT_PATH)).toBe(true);
    content = fs.readFileSync(SCRIPT_PATH, "utf8");
  });

  test("accepts or prompts for a commit and exports DX_PERF_COMMIT", () => {
    expect(content).toContain("[Parameter(Position = 0)]");
    expect(content).toContain("Read-Host 'Commit/ref for DX_PERF_COMMIT'");
    expect(content).toContain("$env:DX_PERF_COMMIT = $Commit");
    expect(content).toContain("$env:DX_PERF_BASELINE = $BaselinePathForUnity");
    expect(content).toContain("$env:DX_PERF_BASELINE_MODE = 'replace'");
    expect(content).toContain("$previousDxPerfCommit = $env:DX_PERF_COMMIT");
  });

  test("runs playmode performance benchmarks through the PowerShell Unity runner", () => {
    expect(content).toContain("run-tests.ps1");
    expect(content).toContain("-Platform playmode");
    expect(content).toContain("-IncludePerf");
    expect(content).toContain(
      "DxMessaging.Tests.Runtime.Benchmarks.DispatchThroughputBenchmarks.UpdateDispatchThroughputBaseline"
    );
    expect(content).not.toContain("run-tests.sh");
  });

  test("tees Unity stdout and stderr to a commit-specific artifacts log", () => {
    expect(content).toContain("2>&1 |");
    expect(content).toContain("Tee-Object -FilePath $logPath");
    expect(content).toContain('Join-Path $ArtifactsDir "perf-$artifactToken-unity-log.txt"');
  });

  test("uses commit-specific results and writes the shared baseline CSV", () => {
    expect(content).toContain('Join-Path $ArtifactsDir "perf-$artifactToken-results.xml"');
    expect(content).toContain("[string]$Output = '.artifacts/perf-baseline.csv'");
    expect(content).toContain("$artifactToken = $Commit -replace '[^A-Za-z0-9_.-]', '-'");
    expect(content).toContain("function ConvertTo-RepoRelativePath");
    expect(content).toContain("$BaselinePathForUnity = ConvertTo-RepoRelativePath $Output");
    expect(content).toContain("[System.IO.Path]::IsPathRooted($relativePath)");
    expect(content).toContain("[System.IO.Path]::GetFullPath($Path, $RepoRoot)");
    expect(content).toContain("$dockerRelativePath = $relativePath.Replace('\\', '/')");
    expect(content).toContain("$dockerRelativePath.StartsWith('../')");
    expect(content).toContain(
      "$BaselineDisplayPath = [System.IO.Path]::GetFullPath($Output, $RepoRoot)"
    );
    expect(content).toContain("$baselineTimestampBeforeRun");
    expect(content).toContain("$baselineTimestampAfterRun -le $baselineTimestampBeforeRun");
    expect(content).toContain("did not write baseline CSV");
    expect(content).toContain("did not update baseline CSV");
  });

  test("does not shell out to the extractor because the Unity test writes the baseline", () => {
    expect(content).not.toContain("extract-perf-baseline.js");
    expect(content).not.toContain("& node");
  });

  test("exports perf baseline variables to pwsh and tees its output to the Unity log", () => {
    if (!REAL_PWSH) {
      return;
    }

    const commit = "behavior-tee-test";
    const tools = makeTempToolDir();
    const outputPath = ".artifacts/behavior-baseline.csv";

    try {
      const result = runCapture(["-Commit", commit, "-Output", outputPath, "-Replace"], tools);
      const logPath = path.join(REPO_ROOT, ".artifacts", `perf-${commit}-unity-log.txt`);
      const marker = JSON.parse(fs.readFileSync(tools.pwshMarker, "utf8"));

      assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
      expect(marker.commit).toBe(commit);
      expectDockerRelativePath(marker.baseline, outputPath);
      expect(marker.mode).toBe("replace");
      expect(stdoutText(result)).toContain(`fake unity stdout for ${commit}`);
      expect(stdoutText(result)).toContain(`fake unity stderr for ${commit}`);
      expect(fs.readFileSync(logPath, "utf8")).toContain(`fake unity stdout for ${commit}`);
      expect(fs.readFileSync(logPath, "utf8")).toContain(`fake unity stderr for ${commit}`);
      expect(fs.existsSync(tools.nodeMarker)).toBe(false);
    } finally {
      cleanupPerfArtifacts(commit);
      fs.rmSync(path.join(REPO_ROOT, outputPath), { force: true });
    }
  });

  test("translates repo-root absolute output to repo-relative for docker-safe resolution", () => {
    if (!REAL_PWSH) {
      return;
    }

    const commit = "absolute-output-test";
    const tools = makeTempToolDir();
    const outputPath = path.join(REPO_ROOT, ".artifacts", "absolute-baseline.csv");

    try {
      const result = runCapture(["-Commit", commit, "-Output", outputPath, "-Replace"], tools);
      const marker = JSON.parse(fs.readFileSync(tools.pwshMarker, "utf8"));

      assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
      expectDockerRelativePath(marker.baseline, ".artifacts/absolute-baseline.csv");
      expect(stdoutText(result)).toContain(outputPath);
    } finally {
      cleanupPerfArtifacts(commit);
      fs.rmSync(outputPath, { force: true });
    }
  });

  test("rejects absolute output outside the repo because Docker cannot see it", () => {
    if (!REAL_PWSH) {
      return;
    }

    const tools = makeTempToolDir();
    const outputPath = path.join(tools.tempRoot, "outside-baseline.csv");
    const result = runCapture(["-Commit", "outside-output-test", "-Output", outputPath], tools);

    assertSpawnStatus(result, 2, expect.getState().currentTestName || "pwsh harness");
    expect(stdoutText(result)).toContain(
      "-Output must be relative to the repo or under the repo root"
    );
    expect(fs.existsSync(tools.pwshMarker)).toBe(false);
  });

  test("rejects relative output outside the repo because Docker cannot see it", () => {
    if (!REAL_PWSH) {
      return;
    }

    const tools = makeTempToolDir();
    const result = runCapture(
      ["-Commit", "relative-outside-output-test", "-Output", "../outside-baseline.csv"],
      tools
    );

    assertSpawnStatus(result, 2, expect.getState().currentTestName || "pwsh harness");
    expect(stdoutText(result)).toContain(
      "-Output must be relative to the repo or under the repo root"
    );
    expect(fs.existsSync(tools.pwshMarker)).toBe(false);
  });

  test("rejects backslash parent traversal after Docker path normalization", () => {
    if (!REAL_PWSH) {
      return;
    }

    const tools = makeTempToolDir();
    const result = runCapture(
      ["-Commit", "backslash-outside-output-test", "-Output", "..\\outside-baseline.csv"],
      tools
    );

    assertSpawnStatus(result, 2, expect.getState().currentTestName || "pwsh harness");
    expect(stdoutText(result)).toContain(
      "-Output must be relative to the repo or under the repo root"
    );
    expect(fs.existsSync(tools.pwshMarker)).toBe(false);
  });

  test("normalizes Windows-style relative output to forward slashes for Docker-safe resolution", () => {
    if (!REAL_PWSH) {
      return;
    }

    const commit = "windows-relative-output-test";
    const tools = makeTempToolDir();
    const outputPath = ".artifacts\\windows-relative-baseline.csv";
    const cleanupPath = path.join(REPO_ROOT, ".artifacts", "windows-relative-baseline.csv");

    try {
      const result = runCapture(["-Commit", commit, "-Output", outputPath, "-Replace"], tools);
      const marker = JSON.parse(fs.readFileSync(tools.pwshMarker, "utf8"));

      assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
      expectDockerRelativePath(marker.baseline, ".artifacts/windows-relative-baseline.csv");
    } finally {
      cleanupPerfArtifacts(commit);
      fs.rmSync(cleanupPath, { force: true });
    }
  });

  test("forwards the default baseline output as repo-relative for docker-safe resolution", () => {
    if (!REAL_PWSH) {
      return;
    }

    const commit = "default-output-test";
    const tools = makeTempToolDir();
    const outputPath = path.join(REPO_ROOT, ".artifacts", "perf-baseline.csv");
    const baselineExisted = fs.existsSync(outputPath);

    try {
      const result = runCapture(["-Commit", commit, "-Replace"], tools);
      const marker = JSON.parse(fs.readFileSync(tools.pwshMarker, "utf8"));

      assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
      expectDockerRelativePath(marker.baseline, ".artifacts/perf-baseline.csv");
      expect(stdoutText(result)).toContain(path.join(REPO_ROOT, ".artifacts", "perf-baseline.csv"));
    } finally {
      cleanupPerfArtifacts(commit);
      if (!baselineExisted) {
        fs.rmSync(outputPath, { force: true });
      }
    }
  });

  test("propagates a nonzero pwsh exit and does not invoke node", () => {
    if (!REAL_PWSH) {
      return;
    }

    const commit = "failing-unity-test";
    const tools = makeTempToolDir();
    const outputPath = ".artifacts/failing-baseline.csv";

    try {
      const result = runCapture(["-Commit", commit, "-Output", outputPath, "-Replace"], tools, {
        FAKE_PWSH_EXIT: "37"
      });

      assertSpawnStatus(result, 37, expect.getState().currentTestName || "pwsh harness");
      expect(stdoutText(result)).toContain("Unity perf run failed with exit code 37");
      expect(fs.existsSync(tools.pwshMarker)).toBe(true);
      expect(fs.existsSync(tools.nodeMarker)).toBe(false);
    } finally {
      cleanupPerfArtifacts(commit);
      fs.rmSync(path.join(REPO_ROOT, outputPath), { force: true });
    }
  });

  test("fails if the Unity runner exits successfully without writing the baseline", () => {
    if (!REAL_PWSH) {
      return;
    }

    const commit = "missing-baseline-test";
    const tools = makeTempToolDir();
    const outputPath = ".artifacts/missing-baseline.csv";

    try {
      const result = runCapture(["-Commit", commit, "-Output", outputPath], tools, {
        FAKE_SKIP_BASELINE: "1"
      });

      assertSpawnStatus(result, 1, expect.getState().currentTestName || "pwsh harness");
      expect(stdoutText(result)).toContain("did not write baseline CSV");
      expect(fs.existsSync(tools.pwshMarker)).toBe(true);
      expect(fs.existsSync(tools.nodeMarker)).toBe(false);
    } finally {
      cleanupPerfArtifacts(commit);
      fs.rmSync(path.join(REPO_ROOT, outputPath), { force: true });
    }
  });

  test("fails if the Unity runner exits successfully without updating an existing baseline", () => {
    if (!REAL_PWSH) {
      return;
    }

    const commit = "stale-baseline-test";
    const tools = makeTempToolDir();
    const outputPath = ".artifacts/stale-baseline.csv";
    const fullOutputPath = path.join(REPO_ROOT, outputPath);

    try {
      fs.mkdirSync(path.dirname(fullOutputPath), { recursive: true });
      fs.writeFileSync(
        fullOutputPath,
        "scenario,platform,commit,runIndex,emitsPerSecond,allocatedBytesDelta,wallClockMs\n"
      );
      const staleTimestamp = new Date("2020-01-01T00:00:00Z");
      fs.utimesSync(fullOutputPath, staleTimestamp, staleTimestamp);

      const result = runCapture(["-Commit", commit, "-Output", outputPath], tools, {
        FAKE_SKIP_BASELINE: "1"
      });

      assertSpawnStatus(result, 1, expect.getState().currentTestName || "pwsh harness");
      expect(stdoutText(result)).toContain("did not update baseline CSV");
      expect(fs.existsSync(tools.pwshMarker)).toBe(true);
      expect(fs.existsSync(tools.nodeMarker)).toBe(false);
    } finally {
      cleanupPerfArtifacts(commit);
      fs.rmSync(fullOutputPath, { force: true });
    }
  });
});

describe("DispatchThroughputBenchmarks baseline update test", () => {
  let content;

  beforeAll(() => {
    expect(fs.existsSync(BENCHMARK_PATH)).toBe(true);
    content = fs.readFileSync(BENCHMARK_PATH, "utf8");
  });

  test("has an explicit Unity test that updates the perf baseline CSV", () => {
    expect(content).toContain('private const string BaselineOutputEnvVar = "DX_PERF_BASELINE"');
    expect(content).toContain('private const string BaselineModeEnvVar = "DX_PERF_BASELINE_MODE"');
    expect(content).toContain(
      'private const string PackageName = "com.wallstop-studios.dxmessaging"'
    );
    expect(content).toContain('[Test, Explicit, Performance, Category("PerfBaseline")]');
    expect(content).toContain("public void UpdateDispatchThroughputBaseline()");
    expect(content).toContain("WriteBaselineRows(outputPath, results, replaceAllRows)");
  });

  test("resolves default output under the package root instead of an arbitrary current directory", () => {
    expect(content).toContain("UnityEditor.PackageManager.PackageInfo.FindForAssembly");
    expect(content).toContain("packageInfo.resolvedPath");
    expect(content).toContain("ResolvePackageInfoRoot(");
    expect(content).toContain("typeof(DispatchThroughputBenchmarks).Assembly");
    expect(content).toContain("typeof(MessageBus).Assembly");
    expect(content).toContain("return FindPackageRoot(packageInfo.resolvedPath)");
    expect(content).toContain("IsPackageRoot(current.FullName)");
    expect(content).toContain("Regex.IsMatch(");
    expect(content).toContain("Regex.Escape(PackageName)");
    expect(content).toContain("ResolveUnityProjectRoot()");
    expect(content).not.toContain('File.Exists(Path.Combine(current.FullName, "package.json"))');
  });

  test("resolves direct Unity baseline commits from the package git metadata when env is absent", () => {
    expect(content).toContain('Environment.GetEnvironmentVariable("DX_PERF_COMMIT")');
    expect(content).toContain('Environment.GetEnvironmentVariable("GITHUB_SHA")');
    expect(content).toContain(
      "ResolveGitHeadCommit(DispatchThroughputBenchmarks.ResolvePackageRoot())"
    );
    expect(content).toContain('Path.Combine(packageRoot, ".git")');
    expect(content).toContain('Path.Combine(gitPath, "HEAD")');
    expect(content).toContain('Path.Combine(gitPath, "commondir")');
    expect(content).toContain("ReadGitRefCommit(gitPath, refName)");
    expect(content).toContain("ReadGitRefCommit(commonGitPath, refName)");
    expect(content).toContain('Path.Combine(gitPath, "packed-refs")');
  });

  test("updates matching rows and preserves unrelated baseline rows by default", () => {
    expect(content).toContain("ReadExistingBaselineRows(outputPath)");
    expect(content).toContain("RemoveMatchingBaselineRow(rows, result)");
    expect(content).toContain("rows.Add(result.ToCsvRow())");
    expect(content).toContain("rows.Sort(CompareBaselineRows)");
    expect(content).toContain(
      "File.WriteAllText(outputPath, builder.ToString(), new UTF8Encoding(false))"
    );
  });
});
