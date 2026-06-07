/**
 * @fileoverview Tests for validate-workflows.js logic.
 *
 * These tests validate the core detection logic for problematic
 * git add --renormalize patterns in GitHub Actions workflows.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");

const {
  isForbiddenRenormalizePattern,
  hasExistenceCheck,
  isGitIgnoredPath,
  extractWorkflowPathEntries,
  findIgnoredPathViolations,
  extractRunBlocks,
  findLockfileInstallViolations,
  findPreCommitInstallHookWriterViolations,
  detectBashSyntaxPattern,
  findWindowsBashPortabilityViolations,
  findForbiddenRunsOnGroupViolations,
  findChangelogCoverageCheckoutViolations,
  findCheckoutCredentialPersistenceViolations,
  findTokenizedGitRemoteCredentialViolations,
  findPersistentGitExtraheaderCredentialViolations,
  findAutoCommitAppCredentialWarningViolations,
  findGitHubAppAutoCommitRobustnessViolations,
  runTextInvokesChangelogCoverage,
  NPM_SCRIPTS_REQUIRING_GIT_HISTORY,
  extractStaticJobLabels,
  jobIsRunnerAccessPreflight,
  findSelfHostedRunnerPreflightViolations,
  isSelfHostedWindowsLabelSet,
  isAllowedSelfHostedWindowsShell,
  findUnityNativeProvisioningViolations,
  findForbidPlainShellBashOnSelfHostedWindowsViolations,
  findSelfHostedWindowsCheckoutLongPathViolations,
  resolveWorkflowLineLengthPolicy,
  resolveWorkflowLineLengthMax,
  findWorkflowLineLengthViolations,
  validatePreCommitConfigLineLengths,
  findCrossPlatformPreflightTargetedGateViolations,
  findLycheeActionPolicyViolations,
  findRequiredWorkflowFileViolations,
  REQUIRED_LYCHEE_VERSION,
  ADVISORY_LYCHEE_REPORT_PATH,
  validateWorkflow
} = require("../validate-workflows.js");

describe("isForbiddenRenormalizePattern", () => {
  describe("should detect FORBIDDEN patterns", () => {
    test("single line with multiple distinct extensions", () => {
      const line = "git add --renormalize -- '*.md' '**/*.md' '*.json' '**/*.json'";
      expect(isForbiddenRenormalizePattern(line)).toBe(true);
    });

    test("single line with three extensions", () => {
      const line = "git add --renormalize -- '*.md' '*.json' '*.yml'";
      expect(isForbiddenRenormalizePattern(line)).toBe(true);
    });

    test("double-quoted patterns", () => {
      const line = 'git add --renormalize -- "*.md" "**/*.md" "*.json" "**/*.json"';
      expect(isForbiddenRenormalizePattern(line)).toBe(true);
    });

    test("mixed quote styles", () => {
      const line = "git add --renormalize -- '*.cs' \"*.md\"";
      expect(isForbiddenRenormalizePattern(line)).toBe(true);
    });

    test("indented in workflow file", () => {
      const line = "          git add --renormalize -- '*.md' '*.json' '*.yml'";
      expect(isForbiddenRenormalizePattern(line)).toBe(true);
    });
  });

  describe("should ALLOW safe patterns", () => {
    test("single extension with recursive pattern", () => {
      const line = "git add --renormalize -- '*.md' '**/*.md'";
      expect(isForbiddenRenormalizePattern(line)).toBe(false);
    });

    test("variable-based pattern in loop", () => {
      const line = 'git add --renormalize -- "*.$ext" "**/*.$ext"';
      expect(isForbiddenRenormalizePattern(line)).toBe(false);
    });

    test("variable-based pattern with braces", () => {
      const line = 'git add --renormalize -- "*.${ext}" "**/*.${ext}"';
      expect(isForbiddenRenormalizePattern(line)).toBe(false);
    });

    test("variable-based pattern with uppercase variable name", () => {
      const line = 'git add --renormalize -- "*.${FILE_EXT}" "**/*.${FILE_EXT}"';
      expect(isForbiddenRenormalizePattern(line)).toBe(false);
    });

    test("ignores non-command extension text on same line", () => {
      const line = 'echo "extensions: *.json" && git add --renormalize -- "*.md" "**/*.md"';
      expect(isForbiddenRenormalizePattern(line)).toBe(false);
    });

    test("single specific file", () => {
      const line = "git add --renormalize -- '.config/dotnet-tools.json'";
      expect(isForbiddenRenormalizePattern(line)).toBe(false);
    });

    test("line without git add", () => {
      const line = "echo 'renormalize'";
      expect(isForbiddenRenormalizePattern(line)).toBe(false);
    });

    test("line without renormalize", () => {
      const line = "git add -- '*.md' '*.json'";
      expect(isForbiddenRenormalizePattern(line)).toBe(false);
    });

    test("add_options: --renormalize (YAML key)", () => {
      const line = "add_options: --renormalize";
      expect(isForbiddenRenormalizePattern(line)).toBe(false);
    });

    test("comment describing renormalize", () => {
      const line = "# Use --renormalize to ensure line endings";
      expect(isForbiddenRenormalizePattern(line)).toBe(false);
    });
  });
});

describe("findUnityNativeProvisioningViolations", () => {
  function unityProvisioningFixture({ requireHealthyExisting }) {
    const healthyFlag = requireHealthyExisting ? "            -RequireHealthyExisting `" : null;
    return [
      "jobs:",
      "  unity:",
      "    runs-on: [self-hosted, Windows, RAM-64GB]",
      "    steps:",
      "      - name: Provision Unity Editor",
      "        shell: pwsh",
      "        run: |",
      "          $editor = ./scripts/unity/ensure-editor.ps1 `",
      "            -UnityVersion '6000.3.16f1' `",
      "            -CiManagedOnly `",
      ...(healthyFlag ? [healthyFlag] : []),
      "            -ProvisioningProfile StandaloneWindowsIl2Cpp",
      '          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append',
      "      - name: Acquire organization Unity lock",
      "        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1",
      "      - name: Run Unity Test Runner",
      "        shell: pwsh",
      "        run: |",
      "          ./scripts/unity/run-ci-tests.ps1 `",
      "            -UnityVersion '6000.3.16f1' `",
      "            -TestMode standalone"
    ];
  }

  test("accepts CI-managed Unity provisioning with RequireHealthyExisting", () => {
    const violations = findUnityNativeProvisioningViolations(
      "unity-tests.yml",
      unityProvisioningFixture({ requireHealthyExisting: true })
    );

    expect(violations).toEqual([]);
  });

  test("flags CI-managed Unity provisioning that can repair in a test job", () => {
    const violations = findUnityNativeProvisioningViolations(
      "unity-tests.yml",
      unityProvisioningFixture({ requireHealthyExisting: false })
    );

    expect(violations).toHaveLength(1);
    expect(violations[0].pattern).toBe("ensure-editor.ps1 -RequireHealthyExisting");
    expect(violations[0].message).toContain("maintain-windows-runner.ps1");
  });
});

describe("hasExistenceCheck", () => {
  describe("should detect proper guards", () => {
    test("if statement with git ls-files and grep", () => {
      const lines = [
        "for ext in cs md json; do",
        '  if git ls-files "*.$ext" "**/*.$ext" | grep -q .; then',
        '    git add --renormalize -- "*.$ext" "**/*.$ext"',
        "  fi",
        "done"
      ];
      expect(hasExistenceCheck(lines, 2)).toBe(true);
    });

    test("for loop with existence check", () => {
      const lines = [
        "for ext in cs md json asmdef yml yaml; do",
        '  if git ls-files "*.$ext" "**/*.$ext" | grep -q .; then',
        '    git add --renormalize -- "*.$ext" "**/*.$ext"',
        "  fi",
        "done"
      ];
      expect(hasExistenceCheck(lines, 2)).toBe(true);
    });
  });

  describe("should detect missing guards", () => {
    test("direct command without check", () => {
      const lines = [
        "- name: Renormalize line endings",
        "  run: |",
        "    git add --renormalize -- '*.md' '**/*.md'"
      ];
      expect(hasExistenceCheck(lines, 2)).toBe(false);
    });

    test("guard too far away", () => {
      const lines = [
        'if git ls-files "*.md" | grep -q .; then',
        "  echo 'files exist'",
        "fi",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "git add --renormalize -- '*.md' '**/*.md'"
      ];
      // Index 13, lookback 10 won't reach index 0
      expect(hasExistenceCheck(lines, 13)).toBe(false);
    });
  });
});

describe("extractWorkflowPathEntries", () => {
  test("collects entries under paths blocks", () => {
    const lines = [
      "on:",
      "  pull_request:",
      "    paths:",
      '      - "package.json"',
      '      - "package-lock.json"',
      "  workflow_dispatch:",
      "    inputs:",
      "      target:",
      "        description: Target"
    ];

    const entries = extractWorkflowPathEntries(lines);
    expect(entries).toEqual([
      { line: 4, path: "package.json" },
      { line: 5, path: "package-lock.json" }
    ]);
  });
});

describe("findIgnoredPathViolations", () => {
  const isIgnoredPathMock = (_repoRoot, candidatePath) => candidatePath === "package-lock.json";

  test("reports ignored literal path entries", () => {
    const lines = [
      "on:",
      "  push:",
      "    paths:",
      "      - package-lock.json",
      "      - scripts/**/*.js"
    ];

    const violations = findIgnoredPathViolations("test.yml", lines, "/tmp", isIgnoredPathMock);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("ignored by git");
    expect(violations[0].line).toBe(4);
  });

  test.each([
    "scripts/**/*.js",
    "**/*.yml",
    "${{ github.event.pull_request.head.ref }}",
    "!docs/**"
  ])("ignores non-literal path pattern '%s'", (pathPattern) => {
    const lines = ["on:", "  push:", "    paths:", `      - ${pathPattern}`];

    const violations = findIgnoredPathViolations("test.yml", lines, "/tmp", isIgnoredPathMock);

    expect(violations).toHaveLength(0);
  });
});

describe("isGitIgnoredPath", () => {
  test("uses git check-ignore with --no-index and -- separator", () => {
    const execFileSyncMock = jest.fn();

    const ignored = isGitIgnoredPath("/repo", "package-lock.json", execFileSyncMock);

    expect(ignored).toBe(true);
    expect(execFileSyncMock).toHaveBeenCalledWith(
      "git",
      ["check-ignore", "--quiet", "--no-index", "--", "package-lock.json"],
      expect.objectContaining({ cwd: "/repo" })
    );
  });

  test("falls back when git does not support --no-index", () => {
    const unsupportedNoIndexError = new Error("unknown option");
    unsupportedNoIndexError.status = 129;
    unsupportedNoIndexError.stderr = "error: unknown option `no-index`";

    const execFileSyncMock = jest
      .fn()
      .mockImplementationOnce(() => {
        throw unsupportedNoIndexError;
      })
      .mockImplementationOnce(() => {});

    const ignored = isGitIgnoredPath("/repo", "package-lock.json", execFileSyncMock);

    expect(ignored).toBe(true);
    expect(execFileSyncMock).toHaveBeenNthCalledWith(
      2,
      "git",
      ["check-ignore", "--quiet", "--", "package-lock.json"],
      expect.objectContaining({ cwd: "/repo" })
    );
  });

  test("returns false when fallback check-ignore reports not ignored", () => {
    const unsupportedNoIndexError = new Error("unknown option");
    unsupportedNoIndexError.status = 129;
    unsupportedNoIndexError.stderr = "error: unknown option `no-index`";

    const notIgnoredError = new Error("not ignored");
    notIgnoredError.status = 1;

    const execFileSyncMock = jest
      .fn()
      .mockImplementationOnce(() => {
        throw unsupportedNoIndexError;
      })
      .mockImplementationOnce(() => {
        throw notIgnoredError;
      });

    const ignored = isGitIgnoredPath("/repo", "package-lock.json", execFileSyncMock);

    expect(ignored).toBe(false);
  });

  test("throws when git binary is unavailable for --no-index check", () => {
    const missingGitError = new Error("git is not installed");
    missingGitError.code = "ENOENT";

    const execFileSyncMock = jest.fn().mockImplementationOnce(() => {
      throw missingGitError;
    });

    expect(() => isGitIgnoredPath("/repo", "package-lock.json", execFileSyncMock)).toThrow(
      /git executable was not found on PATH/i
    );
  });

  test("throws when fallback check-ignore cannot execute due to missing git", () => {
    const unsupportedNoIndexError = new Error("unknown option");
    unsupportedNoIndexError.status = 129;
    unsupportedNoIndexError.stderr = "error: unknown option `no-index`";

    const missingGitError = new Error("git is not installed");
    missingGitError.code = "ENOENT";

    const execFileSyncMock = jest
      .fn()
      .mockImplementationOnce(() => {
        throw unsupportedNoIndexError;
      })
      .mockImplementationOnce(() => {
        throw missingGitError;
      });

    expect(() => isGitIgnoredPath("/repo", "package-lock.json", execFileSyncMock)).toThrow(
      /check-ignore fallback/i
    );
  });
});

describe("run block lockfile policy", () => {
  test("extractRunBlocks handles folded and inline run definitions", () => {
    const lines = [
      "steps:",
      "  - name: Install dependencies",
      "    run: |",
      "      npm ci",
      "  - name: Inline",
      "    run: npm run test:scripts"
    ];

    const blocks = extractRunBlocks(lines);
    expect(blocks).toHaveLength(2);
    expect(blocks[0]).toEqual(expect.objectContaining({ startLine: 3, text: "npm ci" }));
    expect(blocks[1]).toEqual(
      expect.objectContaining({ startLine: 6, text: "npm run test:scripts" })
    );
  });

  test.each([
    {
      name: "flags hard failure when lockfile is missing",
      lines: [
        "steps:",
        "  - run: |",
        "      if [ ! -f package-lock.json ]; then",
        "        exit 1",
        "      fi",
        "      npm ci"
      ],
      expectedViolations: 1
    },
    {
      name: "flags unguarded npm ci when lockfile is ignored",
      lines: ["steps:", "  - run: npm ci"],
      expectedViolations: 1
    },
    {
      name: "allows npm ci with fallback install",
      lines: [
        "steps:",
        "  - run: |",
        "      if [ -f package-lock.json ]; then",
        "        npm ci",
        "      else",
        "        npm i --no-audit --no-fund",
        "      fi"
      ],
      expectedViolations: 0
    },
    {
      name: "allows npm ci with fallback full install command",
      lines: [
        "steps:",
        "  - run: |",
        "      if [ -f package-lock.json ]; then",
        "        npm ci",
        "      else",
        "        npm install --no-audit --no-fund",
        "      fi"
      ],
      expectedViolations: 0
    },
    {
      name: "allows PowerShell Test-Path npm ci with fallback install",
      lines: [
        "steps:",
        "  - shell: pwsh",
        "    run: |",
        "      if (Test-Path package-lock.json) {",
        "        npm ci",
        "      } else {",
        "        npm i --no-audit --no-fund",
        "      }"
      ],
      expectedViolations: 0
    },
    {
      name: "allows npm ci with shell-or fallback",
      lines: ["steps:", "  - run: npm ci || npm i --no-audit --no-fund"],
      expectedViolations: 0
    },
    {
      name: "flags npm install-only blocks",
      lines: ["steps:", "  - run: npm i --no-audit --no-fund"],
      expectedViolations: 1
    },
    {
      name: "flags fallback with wrong lockfile guard",
      lines: [
        "steps:",
        "  - run: |",
        "      if [ -f npm-shrinkwrap.json ]; then",
        "        npm ci",
        "      else",
        "        npm i --no-audit --no-fund",
        "      fi"
      ],
      expectedViolations: 1
    }
  ])("findLockfileInstallViolations: $name", ({ lines, expectedViolations }) => {
    const violations = findLockfileInstallViolations("test.yml", lines, true);

    expect(violations).toHaveLength(expectedViolations);
  });

  test("does not enforce lockfile fallback policy when package-lock is not ignored", () => {
    const lines = ["steps:", "  - run: npm ci"];

    const violations = findLockfileInstallViolations("test.yml", lines, false);
    expect(violations).toHaveLength(0);
  });

  test("source avoids optional-suffix shorthand that trips cspell", () => {
    const source = fs.readFileSync(path.resolve(__dirname, "../validate-workflows.js"), "utf8");
    const optionalSuffixShorthand = `i(?:${"n" + "stall"})?`;

    expect(source).not.toContain(optionalSuffixShorthand);
  });
});

describe("pre-commit hook environment policy", () => {
  test.each([
    {
      name: "flags pre-commit install --install-hooks",
      lines: ["steps:", "  - run: pre-commit install --install-hooks"],
      expectedViolations: 1
    },
    {
      name: "flags direct pre-commit install without install-hooks",
      lines: ["steps:", "  - run: pre-commit install"],
      expectedViolations: 1
    },
    {
      name: "flags multiline pre-commit install --install-hooks",
      lines: [
        "steps:",
        "  - run: |",
        "      python -m pip install pre-commit",
        "      pre-commit install --install-hooks"
      ],
      expectedViolations: 1
    },
    {
      name: "flags direct unpinned pre-commit pip install",
      lines: ["steps:", "  - run: python -m pip install pre-commit"],
      expectedViolations: 1
    },
    {
      name: "flags direct pinned pre-commit pip install",
      lines: ["steps:", "  - run: pip install pre-commit==4.6.0"],
      expectedViolations: 1
    },
    {
      name: "flags direct pre-commit pipx install",
      lines: ["steps:", "  - run: pipx install pre-commit"],
      expectedViolations: 1
    },
    {
      name: "flags direct pre-commit uv tool install",
      lines: ["steps:", "  - run: uv tool install pre-commit"],
      expectedViolations: 1
    },
    {
      name: "flags direct pre-commit brew install",
      lines: ["steps:", "  - run: brew install pre-commit"],
      expectedViolations: 1
    },
    {
      name: "flags direct pre-commit choco install",
      lines: ["steps:", "  - run: choco install pre-commit -y"],
      expectedViolations: 1
    },
    {
      name: "flags direct pre-commit apt install",
      lines: ["steps:", "  - run: sudo apt-get install -y pre-commit"],
      expectedViolations: 1
    },
    {
      name: "flags direct pre-commit pacman install",
      lines: ["steps:", "  - run: sudo pacman -S --noconfirm pre-commit"],
      expectedViolations: 1
    },
    {
      name: "flags direct pre-commit install-hooks",
      lines: ["steps:", "  - run: pre-commit install-hooks"],
      expectedViolations: 1
    },
    {
      name: "flags direct pre-commit hook execution",
      lines: ["steps:", "  - run: pre-commit run --hook-stage pre-push --all-files"],
      expectedViolations: 1
    },
    {
      name: "flags python module pre-commit install --install-hooks",
      lines: ["steps:", "  - run: python -m pre_commit install --install-hooks"],
      expectedViolations: 1
    },
    {
      name: "flags python module pre-commit install without install-hooks",
      lines: ["steps:", "  - run: python -m pre_commit install"],
      expectedViolations: 1
    },
    {
      name: "flags python3 module pre-commit install --install-hooks",
      lines: ["steps:", "  - run: python3 -m pre_commit install --install-hooks"],
      expectedViolations: 1
    },
    {
      name: "allows pinned pre-commit install-hooks wrapper",
      lines: ["steps:", "  - run: node scripts/ensure-pre-commit.js install-hooks"],
      expectedViolations: 0
    },
    {
      name: "allows pinned pre-commit hook execution wrapper",
      lines: [
        "steps:",
        "  - run: node scripts/ensure-pre-commit.js run --hook-stage pre-push --all-files"
      ],
      expectedViolations: 0
    }
  ])("$name", ({ lines, expectedViolations }) => {
    const violations = findPreCommitInstallHookWriterViolations("test.yml", lines);

    expect(violations).toHaveLength(expectedViolations);
  });
});

describe("windows matrix bash shell portability policy", () => {
  test.each([
    {
      name: "detects if bracket conditionals",
      runText: "if [ -f package-lock.json ]; then\n  npm ci\nfi",
      expected: "if/elif [ ... ] conditional"
    },
    {
      name: "detects elif bracket conditionals",
      runText:
        "if [ -f package-lock.json ]; then\n  npm ci\nelif [ -f npm-shrinkwrap.json ]; then\n  npm ci\nfi",
      expected: "if/elif [ ... ] conditional"
    },
    {
      name: "detects for-in loops",
      runText: 'for ext in md json; do\n  echo "$ext"\ndone',
      expected: "for ... in loop"
    },
    {
      name: "detects while loops",
      runText: "while [ -f package-lock.json ]; do\n  break\ndone",
      expected: "while [ ... ] loop"
    },
    {
      name: "detects until loops",
      runText: "until [ -f package-lock.json ]; do\n  break\ndone",
      expected: "until [ ... ] loop"
    },
    {
      name: "detects set shell options",
      runText: "set -euo pipefail\nnpm ci",
      expected: "set -e/-o shell option"
    },
    {
      name: "detects test builtins",
      runText: "test -f package-lock.json && npm ci",
      expected: "test -f/-d shell check"
    },
    {
      name: "detects logical chaining operators",
      runText: "npm ci && npm run validate:npm-meta",
      expected: "logical chaining operator (&&/||)"
    },
    {
      name: "ignores commented bash snippets",
      runText: "# if [ -f package-lock.json ]; then\nnpm ci",
      expected: null
    },
    {
      name: "ignores plain npm command without chaining",
      runText: "npm ci",
      expected: null
    }
  ])("detectBashSyntaxPattern: $name", ({ runText, expected }) => {
    expect(detectBashSyntaxPattern(runText)).toBe(expected);
  });

  test.each([
    {
      name: "flags bash syntax in windows matrix job without shell override",
      lines: [
        "name: test",
        "jobs:",
        "  validate:",
        "    runs-on: ${{ matrix.os }}",
        "    strategy:",
        "      matrix:",
        "        os:",
        "          - ubuntu-latest",
        "          - windows-latest",
        "    steps:",
        "      - name: Install",
        "        run: |",
        "          if [ -f package-lock.json ]; then",
        "            npm ci",
        "          else",
        "            npm i --no-audit --no-fund",
        "          fi"
      ],
      expectedViolationCount: 1
    },
    {
      name: "flags shell chaining operators in windows matrix job without shell override",
      lines: [
        "name: test",
        "jobs:",
        "  validate:",
        "    runs-on: ${{ matrix.os }}",
        "    strategy:",
        "      matrix:",
        "        os:",
        "          - ubuntu-latest",
        "          - windows-latest",
        "    steps:",
        "      - name: Install",
        "        run: npm ci && npm run validate:npm-meta"
      ],
      expectedViolationCount: 1
    },
    {
      name: "allows step-level shell override",
      lines: [
        "name: test",
        "jobs:",
        "  validate:",
        "    runs-on: ${{ matrix.os }}",
        "    strategy:",
        "      matrix:",
        "        os:",
        "          - ubuntu-latest",
        "          - windows-latest",
        "    steps:",
        "      - name: Install",
        "        shell: bash",
        "        run: |",
        "          if [ -f package-lock.json ]; then",
        "            npm ci",
        "          else",
        "            npm i --no-audit --no-fund",
        "          fi"
      ],
      expectedViolationCount: 0
    },
    {
      name: "allows job defaults.run.shell override",
      lines: [
        "name: test",
        "jobs:",
        "  validate:",
        "    runs-on: ${{ matrix.os }}",
        "    strategy:",
        "      matrix:",
        "        os:",
        "          - ubuntu-latest",
        "          - windows-latest",
        "    defaults:",
        "      run:",
        "        shell: bash",
        "    steps:",
        "      - name: Install",
        "        run: |",
        "          if [ -f package-lock.json ]; then",
        "            npm ci",
        "          else",
        "            npm i --no-audit --no-fund",
        "          fi"
      ],
      expectedViolationCount: 0
    },
    {
      name: "allows workflow defaults.run.shell override",
      lines: [
        "name: test",
        "defaults:",
        "  run:",
        "    shell: bash",
        "jobs:",
        "  validate:",
        "    runs-on: ${{ matrix.os }}",
        "    strategy:",
        "      matrix:",
        "        os:",
        "          - ubuntu-latest",
        "          - windows-latest",
        "    steps:",
        "      - name: Install",
        "        run: |",
        "          if [ -f package-lock.json ]; then",
        "            npm ci",
        "          else",
        "            npm i --no-audit --no-fund",
        "          fi"
      ],
      expectedViolationCount: 0
    },
    {
      name: "does not enforce bash-shell policy for ubuntu-only jobs",
      lines: [
        "name: test",
        "jobs:",
        "  validate:",
        "    runs-on: ubuntu-latest",
        "    steps:",
        "      - name: Install",
        "        run: |",
        "          if [ -f package-lock.json ]; then",
        "            npm ci",
        "          else",
        "            npm i --no-audit --no-fund",
        "          fi"
      ],
      expectedViolationCount: 0
    },
    {
      name: "does not flag non-bash run blocks in windows jobs",
      lines: [
        "name: test",
        "jobs:",
        "  validate:",
        "    runs-on: ${{ matrix.os }}",
        "    strategy:",
        "      matrix:",
        "        os:",
        "          - ubuntu-latest",
        "          - windows-latest",
        "    steps:",
        "      - name: Install",
        "        run: npm ci"
      ],
      expectedViolationCount: 0
    }
  ])("findWindowsBashPortabilityViolations: $name", ({ lines, expectedViolationCount }) => {
    const violations = findWindowsBashPortabilityViolations("test.yml", lines);

    expect(violations).toHaveLength(expectedViolationCount);
  });
});

describe("findForbiddenRunsOnGroupViolations", () => {
  test.each([
    {
      name: "flags multi-line runs-on with a group: key",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on:",
        "      group: some-runner-group",
        "      labels:",
        "        - self-hosted",
        "        - Windows",
        "    steps:",
        "      - run: echo hello"
      ],
      expectedViolations: 1
    },
    {
      name: "flags inline mapping runs-on with a group: key",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: { group: some-runner-group, labels: [self-hosted, Windows] }",
        "    steps:",
        "      - run: echo hello"
      ],
      expectedViolations: 1
    },
    {
      name: "allows labels-only inline array runs-on",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - run: echo hello"
      ],
      expectedViolations: 0
    },
    {
      name: "allows labels-only block list runs-on",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on:",
        "      - self-hosted",
        "      - Windows",
        "      - RAM-64GB",
        "    steps:",
        "      - run: echo hello"
      ],
      expectedViolations: 0
    },
    {
      name: "allows plain scalar runs-on",
      lines: [
        "jobs:",
        "  ubuntu:",
        "    runs-on: ubuntu-latest",
        "    steps:",
        "      - run: echo hello"
      ],
      expectedViolations: 0
    },
    {
      name: "does not confuse concurrency.group with runs-on.group (multi-line)",
      lines: [
        "jobs:",
        "  unity:",
        "    concurrency:",
        "      group: wallstop-organization-builds",
        "      cancel-in-progress: false",
        "    runs-on:",
        "      labels:",
        "        - self-hosted",
        "        - Windows",
        "        - RAM-64GB",
        "    steps:",
        "      - run: echo hello"
      ],
      expectedViolations: 0
    },
    {
      name: "still flags runs-on.group when a sibling concurrency.group is present",
      lines: [
        "jobs:",
        "  unity:",
        "    concurrency:",
        "      group: wallstop-organization-builds",
        "      cancel-in-progress: false",
        "    runs-on:",
        "      group: some-runner-group",
        "      labels:",
        "        - self-hosted",
        "        - Windows",
        "        - RAM-64GB",
        "    steps:",
        "      - run: echo hello"
      ],
      expectedViolations: 1
    }
  ])("$name", ({ lines, expectedViolations }) => {
    const violations = findForbiddenRunsOnGroupViolations("test.yml", lines);

    expect(violations).toHaveLength(expectedViolations);
    if (expectedViolations > 0) {
      for (const violation of violations) {
        expect(violation.severity).toBe("error");
        expect(violation.message).toContain("runs-on.group is forbidden");
        expect(violation.message).toContain("per-runner serialization");
      }
    }
  });
});

describe("Real workflow patterns", () => {
  describe("should correctly handle actual workflow content", () => {
    test("correct per-extension loop pattern", () => {
      const workflowContent = `
      - name: Renormalize line endings
        shell: bash
        run: |
          # Renormalize each extension separately to avoid "pathspec did not match" failures
          for ext in cs md json asmdef yml yaml; do
            if git ls-files "*.$ext" "**/*.$ext" | grep -q .; then
              git add --renormalize -- "*.$ext" "**/*.$ext"
            fi
          done
`;
      const lines = workflowContent.split("\n");
      const renormalizeLine = lines.findIndex((l) => l.includes("git add --renormalize"));

      // Should NOT be forbidden (uses variable)
      expect(isForbiddenRenormalizePattern(lines[renormalizeLine])).toBe(false);
      // Should have existence check
      expect(hasExistenceCheck(lines, renormalizeLine)).toBe(true);
    });

    test("problematic single-line pattern", () => {
      const workflowContent = `
      - name: Renormalize line endings
        run: git add --renormalize -- '*.md' '*.markdown' '*.json'
`;
      const lines = workflowContent.split("\n");
      const renormalizeLine = lines.findIndex((l) => l.includes("git add --renormalize"));

      // Should BE forbidden (multiple extensions on single line)
      expect(isForbiddenRenormalizePattern(lines[renormalizeLine])).toBe(true);
    });
  });
});

describe("validateWorkflow newline handling", () => {
  test("detects forbidden pattern when file uses lone CR line endings", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "validate-workflows-"));
    try {
      const workflowPath = path.join(tempDir, "test.yml");

      const workflowContent = [
        "name: Test",
        "jobs:",
        "  lint:",
        "    steps:",
        "      - run: git add --renormalize -- '*.md' '*.json'"
      ].join("\r");

      fs.writeFileSync(workflowPath, workflowContent, "utf8");
      const violations = validateWorkflow(workflowPath);

      expect(violations.some((v) => v.severity === "error")).toBe(true);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});

describe("workflow line-length guard", () => {
  test("flags workflow lines above configured maximum", () => {
    const lines = ["name: Test", `${"x".repeat(201)}`];

    const violations = findWorkflowLineLengthViolations("test.yml", lines, 200);

    expect(violations).toHaveLength(1);
    expect(violations[0]).toEqual(
      expect.objectContaining({
        file: "test.yml",
        line: 2,
        severity: "error"
      })
    );
    expect(violations[0].message).toContain("Workflow line exceeds 200 characters (201)");
  });

  test("allows lines at or below configured maximum", () => {
    const lines = ["name: Test", `${"x".repeat(200)}`];

    const violations = findWorkflowLineLengthViolations("test.yml", lines, 200);

    expect(violations).toHaveLength(0);
  });

  test("allows overlong non-breakable words when enabled", () => {
    const lines = ["name: Test", `key: ${"x".repeat(205)}`];

    const violations = findWorkflowLineLengthViolations("test.yml", lines, 200, {
      allowNonBreakableWords: true
    });

    expect(violations).toHaveLength(0);
  });

  test("still fails overlong non-breakable words when disabled", () => {
    const lines = ["name: Test", `key: ${"x".repeat(205)}`];

    const violations = findWorkflowLineLengthViolations("test.yml", lines, 200, {
      allowNonBreakableWords: false
    });

    expect(violations).toHaveLength(1);
  });

  test("can emit non-workflow context labels", () => {
    const lines = ["# comment", `# ${"word ".repeat(60)}`.trimEnd()];

    const violations = findWorkflowLineLengthViolations(".pre-commit-config.yaml", lines, 60, {
      allowNonBreakableWords: true,
      contextLabel: "YAML"
    });

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("YAML line exceeds 60 characters");
  });
});

describe("validatePreCommitConfigLineLengths", () => {
  test("reports line-length violations from .pre-commit-config.yaml using yamllint policy", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "validate-precommit-line-length-"));
    try {
      const configPath = path.join(tempDir, ".pre-commit-config.yaml");
      fs.writeFileSync(
        path.join(tempDir, ".yamllint.yaml"),
        ["rules:", "  line-length:", "    max: 40"].join("\n"),
        "utf8"
      );
      fs.writeFileSync(
        configPath,
        [
          "repos:",
          "  - repo: local",
          "    hooks:",
          "      # this comment is intentionally longer than forty characters for test coverage"
        ].join("\n"),
        "utf8"
      );

      const violations = validatePreCommitConfigLineLengths({
        repoRoot: tempDir,
        preCommitConfigPath: configPath
      });

      expect(violations).toHaveLength(1);
      expect(violations[0].file).toBe(".pre-commit-config.yaml");
      expect(violations[0].line).toBe(4);
      expect(violations[0].message).toContain("YAML line exceeds 40 characters");
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("returns no violations when pre-commit config is missing", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "validate-precommit-missing-"));
    try {
      const violations = validatePreCommitConfigLineLengths({
        repoRoot: tempDir,
        preCommitConfigPath: path.join(tempDir, ".pre-commit-config.yaml")
      });

      expect(violations).toEqual([]);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});

describe("resolveWorkflowLineLengthPolicy", () => {
  test("loads max and non-breakable options from .yamllint.yaml", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "validate-workflows-policy-yamllint-"));
    try {
      fs.writeFileSync(
        path.join(tempDir, ".yamllint.yaml"),
        [
          "rules:",
          "  line-length:",
          "    max: 180",
          "    allow-non-breakable-words: false",
          "    allow-non-breakable-inline-mappings: false"
        ].join("\n"),
        "utf8"
      );

      const policy = resolveWorkflowLineLengthPolicy(tempDir);

      expect(policy).toEqual({
        max: 180,
        allowNonBreakableWords: false,
        allowNonBreakableInlineMappings: false
      });
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("treats inline-mappings option as implying non-breakable words", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "validate-workflows-policy-inline-map-"));
    try {
      fs.writeFileSync(
        path.join(tempDir, ".yamllint.yaml"),
        [
          "rules:",
          "  line-length:",
          "    max: 200",
          "    allow-non-breakable-words: false",
          "    allow-non-breakable-inline-mappings: true"
        ].join("\n"),
        "utf8"
      );

      const policy = resolveWorkflowLineLengthPolicy(tempDir);
      expect(policy.allowNonBreakableInlineMappings).toBe(true);
      expect(policy.allowNonBreakableWords).toBe(true);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});

describe("resolveWorkflowLineLengthMax", () => {
  test("loads max from .yamllint.yaml when present", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "validate-workflows-yamllint-"));
    try {
      fs.writeFileSync(
        path.join(tempDir, ".yamllint.yaml"),
        ["rules:", "  line-length:", "    max: 187", "    allow-non-breakable-words: true"].join(
          "\n"
        ),
        "utf8"
      );

      expect(resolveWorkflowLineLengthMax(tempDir)).toBe(187);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("falls back to default when .yamllint.yaml is absent", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "validate-workflows-yamllint-missing-"));
    try {
      expect(resolveWorkflowLineLengthMax(tempDir)).toBe(200);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});

describe("findCrossPlatformPreflightTargetedGateViolations", () => {
  const workflowPath = ".github/workflows/cross-platform-preflight.yml";

  test("accepts the PowerShell array+splat targeted test list", () => {
    const lines = [
      "jobs:",
      "  preflight:",
      "    steps:",
      "      - name: Run cross-platform spawn + host-env hermeticity regression suite",
      "        shell: pwsh",
      "        run: |",
      "          $tests = @(",
      '            "scripts/__tests__/path-classifier.test.js"',
      '            "scripts/lib/__tests__/spawn-env-sandbox.test.js"',
      "          )",
      "          node scripts/run-managed-jest.js --runTestsByPath @tests"
    ];

    expect(findCrossPlatformPreflightTargetedGateViolations(workflowPath, lines)).toEqual([]);
  });

  test("flags an unresolved PowerShell splat before native pre-push", () => {
    const lines = [
      "jobs:",
      "  preflight:",
      "    steps:",
      "      - name: Run cross-platform spawn + host-env hermeticity regression suite",
      "        shell: pwsh",
      "        run: |",
      "          node scripts/run-managed-jest.js --runTestsByPath @tests"
    ];

    const violations = findCrossPlatformPreflightTargetedGateViolations(workflowPath, lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("not parseable");
    expect(violations[0].message).toContain("Unresolved PowerShell array splat");
  });

  test("flags a PowerShell splat declared after the targeted command", () => {
    const lines = [
      "jobs:",
      "  preflight:",
      "    steps:",
      "      - name: Run cross-platform spawn + host-env hermeticity regression suite",
      "        shell: pwsh",
      "        run: |",
      "          node scripts/run-managed-jest.js --runTestsByPath @tests",
      "          $tests = @(",
      '            "scripts/__tests__/path-classifier.test.js"',
      "          )"
    ];

    const violations = findCrossPlatformPreflightTargetedGateViolations(workflowPath, lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("Unresolved PowerShell array splat");
  });

  test("does not inspect unrelated workflows", () => {
    const lines = ["jobs:", "  test:", "    steps:", "      - run: echo ok"];
    expect(findCrossPlatformPreflightTargetedGateViolations("workflow.yml", lines)).toEqual([]);
  });
});

describe("findLycheeActionPolicyViolations", () => {
  const blockingWorkflowPath = ".github/workflows/lint-doc-links.yml";
  const advisoryWorkflowPath = ".github/workflows/markdown-link-validity.yml";

  function makeWorkflow(stepLines) {
    return [
      "name: Test",
      "jobs:",
      "  lint:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      ...stepLines
    ].join("\n");
  }

  const installPinnedLycheeStep = [
    "      - name: Install pinned lychee",
    "        uses: ./.github/actions/install-pinned-lychee",
    "        with:",
    `          version: ${REQUIRED_LYCHEE_VERSION}`
  ];
  const validAdvisoryIssueBodyLine =
    '          gh issue edit "${number}" --body-file ' + ADVISORY_LYCHEE_REPORT_PATH;

  const validBlockingLycheeSteps = [
    ...installPinnedLycheeStep,
    "      - name: Check internal links and anchors (offline)",
    "        run: >-",
    "          lychee",
    "          -c .lychee.toml",
    "          --offline",
    "          --include-fragments",
    '          --files-from "${RUNNER_TEMP}/doc-files.txt"',
    "      - name: Probe external link liveness",
    "        run: >-",
    "          lychee",
    "          -c .lychee.toml",
    "          --scheme https",
    "          --scheme http",
    "          --accept-timeouts=true",
    '          --files-from "${RUNNER_TEMP}/doc-files.txt"'
  ];

  const validAdvisoryLycheeSteps = [
    ...installPinnedLycheeStep,
    "      - name: Scan external links (advisory)",
    "        id: lychee",
    "        run: |",
    "          set +e",
    "          mkdir -p ./lychee",
    "          lychee \\",
    "            -c .lychee.toml \\",
    "            --scheme https \\",
    "            --scheme http \\",
    "            --format markdown \\",
    `            --output ${ADVISORY_LYCHEE_REPORT_PATH} \\`,
    '            --files-from "${RUNNER_TEMP}/doc-files.txt"',
    '          exit_code="${?}"',
    '          echo "exit_code=${exit_code}" >> "${GITHUB_OUTPUT}"',
    "          exit 0",
    "      - name: Sync the dead-link tracking issue",
    "        run: |",
    validAdvisoryIssueBodyLine
  ];

  test("accepts the blocking workflow's pinned native-timeout lychee shape", () => {
    const violations = findLycheeActionPolicyViolations(
      blockingWorkflowPath,
      makeWorkflow(validBlockingLycheeSteps)
    );

    expect(violations).toEqual([]);
  });

  test.each([
    {
      label: "missing pinned installer",
      steps: validBlockingLycheeSteps.slice(4),
      expectedPattern: "missing install-pinned-lychee",
      expectedMessage: "installing"
    },
    {
      label: "wrong pinned installer version",
      steps: [
        "      - name: Install pinned lychee",
        "        uses: ./.github/actions/install-pinned-lychee",
        "        with:",
        "          version: v0.24.1",
        ...validBlockingLycheeSteps.slice(4)
      ],
      expectedPattern: "version: v0.24.1",
      expectedMessage: REQUIRED_LYCHEE_VERSION
    },
    {
      label: "offline step missing fragment checks",
      steps: [
        ...installPinnedLycheeStep,
        "      - name: Check internal links and anchors (offline)",
        "        run: >-",
        "          lychee",
        "          -c .lychee.toml",
        "          --offline",
        '          --files-from "${RUNNER_TEMP}/doc-files.txt"',
        ...validBlockingLycheeSteps.slice(11)
      ],
      expectedPattern: "--include-fragments",
      expectedMessage: "heading anchors"
    },
    {
      label: "CLI step missing tracked-docs file list",
      steps: [
        ...installPinnedLycheeStep,
        "      - name: Check internal links and anchors (offline)",
        "        run: >-",
        "          lychee",
        "          -c .lychee.toml",
        "          --offline",
        "          --include-fragments",
        ...validBlockingLycheeSteps.slice(11)
      ],
      expectedPattern: "--files-from",
      expectedMessage: "tracked docs"
    }
  ])("rejects blocking lychee CLI shape: $label", ({ steps, expectedPattern, expectedMessage }) => {
    const violations = findLycheeActionPolicyViolations(blockingWorkflowPath, makeWorkflow(steps));

    expect(violations.some((violation) => violation.pattern === expectedPattern)).toBe(true);
    expect(violations.some((violation) => violation.message.includes(expectedMessage))).toBe(true);
  });

  test("rejects active lycheeverse action usage before it can hide installer breakage", () => {
    const content = makeWorkflow([
      "      - name: Scan external links",
      "        uses: lycheeverse/lychee-action@v2",
      "        with:",
      "          args: ./docs",
      "          fail: false"
    ]);

    const violations = findLycheeActionPolicyViolations(".github/workflows/some.yml", content);

    expect(violations).toHaveLength(1);
    expect(violations[0].pattern).toBe("lycheeverse/lychee-action");
    expect(violations[0].message).toContain("install-pinned-lychee");
  });

  test("reports a blocking workflow missing the external lychee liveness step", () => {
    const content = makeWorkflow([
      ...installPinnedLycheeStep,
      "      - name: Check internal links and anchors (offline)",
      "        run: >-",
      "          lychee",
      "          -c .lychee.toml",
      "          --offline",
      "          --include-fragments",
      '          --files-from "${RUNNER_TEMP}/doc-files.txt"'
    ]);

    const violations = findLycheeActionPolicyViolations(blockingWorkflowPath, content);

    expect(violations).toHaveLength(1);
    expect(violations[0].pattern).toBe("lychee CLI external liveness step");
    expect(violations[0].message).toContain("external liveness step");
  });

  test("reports a blocking workflow missing the offline lychee step", () => {
    const content = makeWorkflow([
      ...installPinnedLycheeStep,
      "      - name: Probe external link liveness",
      "        run: >-",
      "          lychee",
      "          -c .lychee.toml",
      "          --scheme https",
      "          --scheme http",
      "          --accept-timeouts=true",
      '          --files-from "${RUNNER_TEMP}/doc-files.txt"'
    ]);

    const violations = findLycheeActionPolicyViolations(blockingWorkflowPath, content);

    expect(violations).toHaveLength(1);
    expect(violations[0].pattern).toBe("lychee CLI offline/internal step");
    expect(violations[0].message).toContain("offline/internal step");
  });

  test("reports a blocking external lychee step missing required schemes", () => {
    const content = makeWorkflow([
      ...installPinnedLycheeStep,
      "      - name: Check internal links and anchors (offline)",
      "        run: >-",
      "          lychee",
      "          -c .lychee.toml",
      "          --offline",
      "          --include-fragments",
      '          --files-from "${RUNNER_TEMP}/doc-files.txt"',
      "      - name: Probe external link liveness",
      "        run: >-",
      "          lychee",
      "          -c .lychee.toml",
      "          --scheme https",
      "          --accept-timeouts=true",
      '          --files-from "${RUNNER_TEMP}/doc-files.txt"'
    ]);

    const violations = findLycheeActionPolicyViolations(blockingWorkflowPath, content);

    expect(violations).toHaveLength(1);
    expect(violations[0].pattern).toBe("--scheme http");
    expect(violations[0].message).toContain("both --scheme https and --scheme http");
  });

  test("allows the scheduled advisory scan to report through the tracking issue", () => {
    const content = makeWorkflow(validAdvisoryLycheeSteps);

    const violations = findLycheeActionPolicyViolations(advisoryWorkflowPath, content);

    expect(violations).toEqual([]);
  });

  test("reports a scheduled advisory workflow with no lychee scan step", () => {
    const content = makeWorkflow(["      - name: Do nothing", "        run: echo ok"]);

    const violations = findLycheeActionPolicyViolations(advisoryWorkflowPath, content);

    expect(violations).toHaveLength(1);
    expect(violations[0].pattern).toBe("lychee CLI advisory scan step");
    expect(violations[0].message).toContain("advisory scan step");
  });

  test("allows the current scheduled advisory workflow lychee shape", () => {
    const content = fs.readFileSync(
      path.resolve(__dirname, "../../.github/workflows/markdown-link-validity.yml"),
      "utf8"
    );

    const violations = findLycheeActionPolicyViolations(advisoryWorkflowPath, content);

    expect(violations).toEqual([]);
  });

  test("install-pinned-lychee action keeps archive-layout diagnostics", () => {
    const actionText = fs.readFileSync(
      path.resolve(__dirname, "../../.github/actions/install-pinned-lychee/action.yml"),
      "utf8"
    );

    expect(actionText).toContain("unknown-linux-musl");
    expect(actionText).toContain("tar -tzf");
    expect(actionText).toContain('find "${temp_dir}" -maxdepth 3 -type f -name lychee');
    expect(actionText).toContain("Expected exactly one lychee binary");
    expect(actionText).toContain('lychee" --version');
  });

  test("rejects advisory workflow lychee steps that would fail the workflow directly", () => {
    const content = makeWorkflow([
      ...installPinnedLycheeStep,
      "      - name: Scan external links (advisory)",
      "        id: lychee",
      "        run: |",
      "          mkdir -p ./lychee",
      "          lychee \\",
      "            -c .lychee.toml \\",
      "            --scheme https \\",
      "            --scheme http \\",
      "            --format markdown \\",
      `            --output ${ADVISORY_LYCHEE_REPORT_PATH} \\`,
      '            --files-from "${RUNNER_TEMP}/doc-files.txt"',
      "      - name: Sync the dead-link tracking issue",
      "        run: |",
      validAdvisoryIssueBodyLine
    ]);

    const violations = findLycheeActionPolicyViolations(advisoryWorkflowPath, content);

    expect(violations).toHaveLength(1);
    expect(violations[0].pattern).toBe("advisory exit_code output");
    expect(violations[0].message).toContain("exit 0");
  });

  test.each([
    {
      label: "missing report directory creation",
      omittedLine: "          mkdir -p ./lychee",
      expectedMessage: "Markdown lychee report"
    },
    {
      label: "missing Markdown format",
      omittedLine: "            --format markdown \\",
      expectedMessage: "Markdown lychee report"
    },
    {
      label: "missing report output path",
      omittedLine: `            --output ${ADVISORY_LYCHEE_REPORT_PATH} \\`,
      expectedMessage: ADVISORY_LYCHEE_REPORT_PATH
    }
  ])("rejects advisory report generation shape: $label", ({ omittedLine, expectedMessage }) => {
    const content = makeWorkflow(validAdvisoryLycheeSteps.filter((line) => line !== omittedLine));

    const violations = findLycheeActionPolicyViolations(advisoryWorkflowPath, content);

    expect(violations.some((violation) => violation.pattern === "advisory markdown report")).toBe(
      true
    );
    expect(violations.some((violation) => violation.message.includes(expectedMessage))).toBe(true);
  });

  test("rejects advisory issue sync steps that drop the lychee report body file", () => {
    const content = makeWorkflow(
      validAdvisoryLycheeSteps.map((line) =>
        line.includes("--body-file") ? '          gh issue edit "${number}"' : line
      )
    );

    const violations = findLycheeActionPolicyViolations(advisoryWorkflowPath, content);

    expect(violations).toHaveLength(1);
    expect(violations[0].pattern).toBe("advisory issue body file");
    expect(violations[0].message).toContain(ADVISORY_LYCHEE_REPORT_PATH);
  });

  test.each(["--accept-timeouts", "--accept-timeouts=true"])(
    "rejects advisory workflow timeout acceptance via %s",
    (acceptTimeoutsArg) => {
      const content = makeWorkflow([
        ...installPinnedLycheeStep,
        "      - name: Scan external links (advisory)",
        "        id: lychee",
        "        run: |",
        "          set +e",
        "          mkdir -p ./lychee",
        "          lychee \\",
        "            -c .lychee.toml \\",
        "            --scheme https \\",
        "            --scheme http \\",
        `            ${acceptTimeoutsArg} \\`,
        "            --format markdown \\",
        `            --output ${ADVISORY_LYCHEE_REPORT_PATH} \\`,
        '            --files-from "${RUNNER_TEMP}/doc-files.txt"',
        '          exit_code="${?}"',
        '          echo "exit_code=${exit_code}" >> "${GITHUB_OUTPUT}"',
        "          exit 0",
        "      - name: Sync the dead-link tracking issue",
        "        run: |",
        validAdvisoryIssueBodyLine
      ]);

      const violations = findLycheeActionPolicyViolations(advisoryWorkflowPath, content);

      expect(violations).toHaveLength(1);
      expect(violations[0].message).toContain("timeout reporting must stay enabled");
    }
  );

  test("ignores archived disabled workflows when checking retired lychee-action usage", () => {
    const content = makeWorkflow([
      "      - name: Old scan",
      "        uses: lycheeverse/lychee-action@v2",
      "        with:",
      "          args: ./docs",
      "          fail: false"
    ]);

    const violations = findLycheeActionPolicyViolations(
      ".github/workflows-disabled/old-lychee.yml",
      content
    );

    expect(violations).toEqual([]);
  });

  test("rejects the old blocking fail:false JSON post-classification pattern", () => {
    const content = makeWorkflow([
      ...installPinnedLycheeStep,
      "      - name: Probe external link liveness",
      "        id: external",
      "        run: |",
      "          set +e",
      "          lychee \\",
      "            -c .lychee.toml \\",
      "            --scheme https \\",
      "            --scheme http \\",
      '            --files-from "${RUNNER_TEMP}/doc-files.txt" \\',
      '            > "${RUNNER_TEMP}/external.json"',
      '          exit_code="${?}"',
      '          echo "exit_code=${exit_code}" >> "${GITHUB_OUTPUT}"',
      "          exit 0",
      "      - name: Fail only on genuinely-dead external links",
      "        if: steps.external.outputs.exit_code != '0'",
      "        run: |",
      '          dead="$(jq \'.error_map[]?[]? | select(.status.text != \"Timeout\")\' external.json)"',
      '          echo "${dead}"'
    ]);

    const messages = findLycheeActionPolicyViolations(blockingWorkflowPath, content).map(
      (violation) => violation.message
    );

    expect(messages.some((message) => message.includes("--accept-timeouts=true"))).toBe(true);
    expect(messages.some((message) => message.includes("fail:false plus format/output"))).toBe(
      true
    );
    expect(messages.some((message) => message.includes("retired JSON post-classifier"))).toBe(true);
  });

  test("does not let --offline hide malformed blocking external liveness steps", () => {
    const content = makeWorkflow([
      ...installPinnedLycheeStep,
      "      - name: Probe external link liveness",
      "        run: >-",
      "          lychee",
      "          -c .lychee.toml",
      "          --offline",
      "          --scheme https",
      "          --scheme http",
      '          --files-from "${RUNNER_TEMP}/doc-files.txt"'
    ]);

    const messages = findLycheeActionPolicyViolations(blockingWorkflowPath, content).map(
      (violation) => violation.message
    );

    expect(messages.some((message) => message.includes("must not combine --offline"))).toBe(true);
    expect(messages.some((message) => message.includes("--accept-timeouts=true"))).toBe(true);
  });

  test("rejects --offline on a blocking external step even when native timeout policy is present", () => {
    const content = makeWorkflow([
      ...installPinnedLycheeStep,
      "      - name: Probe external link liveness",
      "        run: >-",
      "          lychee",
      "          -c .lychee.toml",
      "          --offline",
      "          --scheme=https",
      "          --scheme http",
      "          --accept-timeouts=true",
      '          --files-from "${RUNNER_TEMP}/doc-files.txt"'
    ]);

    const violations = findLycheeActionPolicyViolations(blockingWorkflowPath, content);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("must not combine --offline");
  });

  test("validateWorkflow reports blocking external lychee steps missing timeout acceptance", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "validate-workflows-lychee-"));
    try {
      const workflowPath = path.join(tempDir, ".github", "workflows", "lint-doc-links.yml");
      fs.mkdirSync(path.dirname(workflowPath), { recursive: true });
      fs.writeFileSync(
        workflowPath,
        makeWorkflow([
          ...installPinnedLycheeStep,
          "      - name: Probe external link liveness",
          "        run: >-",
          "          lychee",
          "          -c .lychee.toml",
          "          --scheme https",
          "          --scheme http",
          '          --files-from "${RUNNER_TEMP}/doc-files.txt"'
        ]),
        "utf8"
      );

      const violations = validateWorkflow(workflowPath, {
        repoRoot: tempDir,
        isIgnoredPathFn: () => false
      });

      expect(
        violations.some((violation) => violation.message.includes("--accept-timeouts=true"))
      ).toBe(true);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("validateWorkflow reports advisory lychee timeout acceptance", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "validate-workflows-lychee-"));
    try {
      const workflowPath = path.join(tempDir, ".github", "workflows", "markdown-link-validity.yml");
      fs.mkdirSync(path.dirname(workflowPath), { recursive: true });
      fs.writeFileSync(
        workflowPath,
        makeWorkflow([
          ...installPinnedLycheeStep,
          "      - name: Scan external links (advisory)",
          "        id: lychee",
          "        run: |",
          "          set +e",
          "          lychee \\",
          "            -c .lychee.toml \\",
          "            --scheme https \\",
          "            --scheme http \\",
          "            --accept-timeouts=true \\",
          '            --files-from "${RUNNER_TEMP}/doc-files.txt"',
          '          exit_code="${?}"',
          '          echo "exit_code=${exit_code}" >> "${GITHUB_OUTPUT}"',
          "          exit 0"
        ]),
        "utf8"
      );

      const violations = validateWorkflow(workflowPath, {
        repoRoot: tempDir,
        isIgnoredPathFn: () => false
      });

      expect(
        violations.some((violation) =>
          violation.message.includes("timeout reporting must stay enabled")
        )
      ).toBe(true);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});

describe("findRequiredWorkflowFileViolations", () => {
  test("accepts the required active docs link workflows when both are present", () => {
    const violations = findRequiredWorkflowFileViolations([
      "lint-doc-links.yml",
      "markdown-link-validity.yml"
    ]);

    expect(violations).toEqual([]);
  });

  test("fails closed when required docs link workflows are missing", () => {
    const violations = findRequiredWorkflowFileViolations(["unity-tests.yml"]);

    expect(violations).toHaveLength(2);
    expect(violations.map((violation) => violation.file)).toEqual([
      ".github/workflows/lint-doc-links.yml",
      ".github/workflows/markdown-link-validity.yml"
    ]);
    expect(violations.every((violation) => violation.pattern === "required workflow file")).toBe(
      true
    );
  });
});

describe("findChangelogCoverageCheckoutViolations", () => {
  test.each([
    ["direct changelog validator", "node scripts/validate-changelog.js --check-coverage"],
    ["pre-commit changelog policy hook", "pre-commit run validate-changelog-policy --all-files"],
    ["npm coverage script", "npm run validate:changelog:coverage"],
    ["npm preflight pre-commit", "npm run preflight:pre-commit"],
    // Bug 2 regression guard: cross-platform-preflight.yml invoked
    // `npm run preflight:pre-push` (which chains preflight:pre-commit ->
    // validate:changelog:coverage) without fetch-depth: 0. The previous
    // pattern only matched preflight:pre-commit and validate:all, so this
    // workflow slipped through the validator.
    ["npm preflight pre-push (Bug 2 regression)", "npm run preflight:pre-push"],
    ["npm full validation script", "npm run validate:all"],
    ["bare git diff origin/master", "git diff origin/master...HEAD"],
    ["bare git merge-base origin/main", "git merge-base origin/main HEAD"]
  ])("requires full-history checkout for %s", (_name, coverageCommand) => {
    const lines = [
      "jobs:",
      "  changelog:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Checkout repository",
      "        uses: actions/checkout@v6",
      "        with:",
      "          persist-credentials: false",
      "      - name: Validate changelog coverage",
      `        run: ${coverageCommand}`
    ];

    const violations = findChangelogCoverageCheckoutViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0]).toEqual(
      expect.objectContaining({
        file: "workflow.yml",
        line: 10,
        pattern: coverageCommand,
        severity: "error"
      })
    );
    expect(violations[0].message).toContain("fetch-depth: 0");
  });

  test.each([
    ["unquoted zero", "          fetch-depth: 0"],
    ["quoted zero", '          fetch-depth: "0"']
  ])("allows full-history checkout with %s", (_name, fetchDepthLine) => {
    const lines = [
      "jobs:",
      "  changelog:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Checkout repository",
      "        uses: actions/checkout@v6",
      "        with:",
      fetchDepthLine,
      "          persist-credentials: false",
      "      - name: Validate changelog coverage",
      "        run: pre-commit run validate-changelog-policy --all-files"
    ];

    const violations = findChangelogCoverageCheckoutViolations("workflow.yml", lines);

    expect(violations).toHaveLength(0);
  });

  test("allows shorthand full-history checkout before changelog coverage", () => {
    const lines = [
      "jobs:",
      "  changelog:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - uses: actions/checkout@v6",
      "        with:",
      "          fetch-depth: 0",
      "      - name: Validate changelog coverage",
      "        run: node scripts/validate-changelog.js --check-coverage"
    ];

    const violations = findChangelogCoverageCheckoutViolations("workflow.yml", lines);

    expect(violations).toHaveLength(0);
  });

  test.each([
    [
      "folded run block",
      [
        "      - name: Validate changelog coverage",
        "        run: >-",
        "          node scripts/validate-changelog.js",
        "          --check-coverage"
      ]
    ],
    [
      "literal run block",
      [
        "      - name: Validate changelog coverage",
        "        run: |",
        "          pre-commit run",
        "          validate-changelog-policy --all-files"
      ]
    ]
  ])("detects changelog coverage in %s", (_name, runStepLines) => {
    const lines = [
      "jobs:",
      "  changelog:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Checkout repository",
      "        uses: actions/checkout@v6",
      "      - name: Other step",
      "        run: echo before",
      ...runStepLines
    ];

    const violations = findChangelogCoverageCheckoutViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("preceding full-history checkout");
  });

  test("requires the full-history checkout to precede changelog coverage", () => {
    const lines = [
      "jobs:",
      "  changelog:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Validate changelog coverage",
      "        run: node scripts/validate-changelog.js --check-coverage",
      "      - name: Checkout repository",
      "        uses: actions/checkout@v6",
      "        with:",
      "          fetch-depth: 0"
    ];

    const violations = findChangelogCoverageCheckoutViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
  });

  test("uses the most recent checkout before changelog coverage", () => {
    const lines = [
      "jobs:",
      "  changelog:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Full checkout",
      "        uses: actions/checkout@v6",
      "        with:",
      "          fetch-depth: 0",
      "      - name: Later shallow checkout",
      "        uses: actions/checkout@v6",
      "      - name: Validate changelog coverage",
      "        run: node scripts/validate-changelog.js --check-coverage"
    ];

    const violations = findChangelogCoverageCheckoutViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
  });

  test("shorthand shallow checkout after full checkout invalidates changelog coverage", () => {
    const lines = [
      "jobs:",
      "  changelog:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Full checkout",
      "        uses: actions/checkout@v6",
      "        with:",
      "          fetch-depth: 0",
      "      - uses: actions/checkout@v6",
      "      - name: Validate changelog coverage",
      "        run: node scripts/validate-changelog.js --check-coverage"
    ];

    const violations = findChangelogCoverageCheckoutViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
  });
});

describe("runTextInvokesChangelogCoverage + NPM_SCRIPTS_REQUIRING_GIT_HISTORY (Bug 2)", () => {
  test("NPM_SCRIPTS_REQUIRING_GIT_HISTORY allowlist contains the scripts that transitively need origin/<base>", () => {
    // Contract: any npm script that itself, or via chain, runs
    // validate:changelog:coverage or `git diff origin/*` MUST appear here.
    // This list is what runTextInvokesChangelogCoverage matches against.
    // `validate:changelog` (without `--check-coverage`) is deliberately
    // NOT in this list -- the bare validator does not touch origin/<base>.
    expect(NPM_SCRIPTS_REQUIRING_GIT_HISTORY).toEqual(
      expect.arrayContaining([
        "validate:changelog:coverage",
        "preflight:pre-commit",
        // The script that the failing run (cross-platform-preflight.yml)
        // actually invoked. Adding it here is the documented fix for Bug 2.
        "preflight:pre-push",
        "validate:all"
      ])
    );
    expect(NPM_SCRIPTS_REQUIRING_GIT_HISTORY).not.toContain("validate:changelog");
  });

  test.each([
    // [name, runText, expected]
    ["preflight:pre-push (Bug 2 root cause)", "npm run preflight:pre-push", true],
    ["preflight:pre-commit", "npm run preflight:pre-commit", true],
    ["validate:changelog:coverage", "npm run validate:changelog:coverage", true],
    ["validate:all", "npm run validate:all", true],
    [
      "validate:changelog (no coverage flag) -- does NOT need git history",
      "npm run validate:changelog",
      false
    ],
    [
      "direct validator with --check-coverage",
      "node scripts/validate-changelog.js --check-coverage",
      true
    ],
    ["pre-commit hook", "pre-commit run validate-changelog-policy --all-files", true],
    ["git diff against origin", "git diff origin/master...HEAD", true],
    ["git merge-base against origin", "git merge-base origin/main HEAD", true],
    // Negative cases.
    ["unrelated npm script", "npm run test", false],
    ["git diff against HEAD (no origin ref needed)", "git diff HEAD~1 HEAD", false],
    ["empty run text", "", false],
    ["git log (not in allowlist of git-history-aware probes)", "git log --oneline -5", false]
  ])("matches %s -> %s", (_name, runText, expected) => {
    expect(runTextInvokesChangelogCoverage(runText)).toBe(expected);
  });

  test.each([
    // [name, runStep, hasFetchDepth0, expectedViolations]
    ["preflight:pre-push without fetch-depth: 0 fails", "npm run preflight:pre-push", false, 1],
    ["preflight:pre-push with fetch-depth: 0 passes", "npm run preflight:pre-push", true, 0],
    ["preflight:pre-commit without fetch-depth: 0 fails", "npm run preflight:pre-commit", false, 1],
    ["preflight:pre-commit with fetch-depth: 0 passes", "npm run preflight:pre-commit", true, 0],
    ["unrelated npm script without fetch-depth: 0 passes", "npm run test", false, 0],
    ["unrelated npm script with fetch-depth: 0 passes", "npm run test", true, 0],
    [
      "bare git diff origin without fetch-depth: 0 fails",
      "git diff origin/master...HEAD",
      false,
      1
    ],
    ["bare git diff origin with fetch-depth: 0 passes", "git diff origin/master...HEAD", true, 0]
  ])("data-driven workflow check: %s", (_name, runCommand, hasFetchDepth0, expectedViolations) => {
    const checkoutBlock = hasFetchDepth0
      ? [
          "      - name: Checkout",
          "        uses: actions/checkout@v6",
          "        with:",
          "          fetch-depth: 0"
        ]
      : ["      - name: Checkout", "        uses: actions/checkout@v6"];

    const lines = [
      "jobs:",
      "  preflight:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      ...checkoutBlock,
      "      - name: Run preflight",
      `        run: ${runCommand}`
    ];

    const violations = findChangelogCoverageCheckoutViolations("workflow.yml", lines);
    expect(violations).toHaveLength(expectedViolations);
    if (expectedViolations > 0) {
      // Confirm the diagnostic names fetch-depth: 0 explicitly.
      expect(violations[0].message).toContain("fetch-depth: 0");
    }
  });

  test("regression guard: cross-platform-preflight.yml shape is now caught", () => {
    // Reproduce the exact YAML that failed in logs_69627069942.
    const lines = [
      "jobs:",
      "  preflight:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Checkout repository",
      "        uses: actions/checkout@v6",
      "        with:",
      "          ref: ${{ github.event.pull_request.head.sha || github.sha }}",
      "          persist-credentials: false",
      "      - name: Run pre-push preflight",
      "        shell: bash",
      "        run: npm run preflight:pre-push"
    ];

    const violations = findChangelogCoverageCheckoutViolations("workflow.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("fetch-depth: 0");
  });
});

describe("findCheckoutCredentialPersistenceViolations", () => {
  test("requires every checkout to declare persist-credentials explicitly", () => {
    const lines = [
      "jobs:",
      "  check:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Checkout",
      "        uses: actions/checkout@v6",
      "      - run: npm test"
    ];

    const violations = findCheckoutCredentialPersistenceViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].line).toBe(5);
    expect(violations[0].message).toContain("without an explicit persist-credentials value");
  });

  test("accepts checkout credential opt-out for read-only jobs", () => {
    const lines = [
      "jobs:",
      "  check:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Checkout",
      "        uses: actions/checkout@v6",
      "        with:",
      "          persist-credentials: false",
      "      - run: npm test"
    ];

    const violations = findCheckoutCredentialPersistenceViolations("workflow.yml", lines);

    expect(violations).toEqual([]);
  });

  test("rejects persisted checkout credentials even before a local auto-commit action", () => {
    const lines = [
      "jobs:",
      "  autofix:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Checkout",
      "        uses: actions/checkout@v6",
      "        with:",
      "          persist-credentials: true",
      "      - name: Commit fixes",
      "        if: ${{ steps.changes.outputs.has_changes == 'true' }}",
      "        uses: stefanzweifel/git-auto-commit-action@v7"
    ];

    const violations = findCheckoutCredentialPersistenceViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("persists checkout credentials");
  });

  test.each(["true", "${{ inputs.persist_credentials }}"])(
    "rejects persist-credentials: %s without an allowed consumer",
    (persistValue) => {
      const lines = [
        "jobs:",
        "  check:",
        "    runs-on: ubuntu-latest",
        "    steps:",
        "      - name: Checkout",
        "        uses: actions/checkout@v6",
        "        with:",
        `          persist-credentials: ${persistValue}`,
        "      - run: npm test"
      ];

      const violations = findCheckoutCredentialPersistenceViolations("workflow.yml", lines);

      expect(violations).toHaveLength(1);
      expect(violations[0].message).toContain("persists checkout credentials");
    }
  );
});

describe("findTokenizedGitRemoteCredentialViolations", () => {
  test("rejects tokenized remote setup that is not adjacent to a guarded auto-commit", () => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Configure push credentials too early",
      "        env:",
      "          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}",
      '        run: git remote set-url origin "https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.git"',
      "      - run: npm test",
      "      - run: git push origin HEAD"
    ];

    const violations = findTokenizedGitRemoteCredentialViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("immediate origin cleanup");
  });

  test("rejects tokenized remote setup split across shell continuations", () => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Configure push credentials too early",
      "        env:",
      "          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}",
      "        run: |",
      "          git remote set-url origin \\",
      '            "https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.git"'
    ];

    const violations = findTokenizedGitRemoteCredentialViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("manual clone/fetch/push steps");
  });

  test("accepts tokenized remote setup immediately before matching auto-commit action", () => {
    const lines = [
      "jobs:",
      "  autofix:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Configure push credentials",
      "        if: ${{ steps.changes.outputs.has_changes == 'true' }}",
      "        env:",
      "          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}",
      '        run: git remote set-url origin "https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.git"',
      "      - name: Commit fixes",
      "        if: ${{ steps.changes.outputs.has_changes == 'true' }}",
      "        uses: stefanzweifel/git-auto-commit-action@v7",
      "      - name: Clear push credentials",
      "        if: ${{ always() && steps.changes.outputs.has_changes == 'true' }}",
      '        run: git remote set-url origin "https://github.com/${GITHUB_REPOSITORY}.git"'
    ];

    const violations = findTokenizedGitRemoteCredentialViolations("workflow.yml", lines);

    expect(violations).toEqual([]);
  });

  test("accepts tokenized auto-commit handoff split across shell continuations", () => {
    const lines = [
      "jobs:",
      "  autofix:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Configure push credentials",
      "        if: ${{ steps.changes.outputs.has_changes == 'true' }}",
      "        env:",
      "          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}",
      "        run: |",
      "          git remote set-url origin \\",
      '            "https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.git"',
      "      - name: Commit fixes",
      "        if: ${{ steps.changes.outputs.has_changes == 'true' }}",
      "        uses: stefanzweifel/git-auto-commit-action@v7",
      "      - name: Clear push credentials",
      "        if: ${{ always() && steps.changes.outputs.has_changes == 'true' }}",
      "        run: |",
      "          git remote set-url origin \\",
      '            "https://github.com/${GITHUB_REPOSITORY}.git"'
    ];

    const violations = findTokenizedGitRemoteCredentialViolations("workflow.yml", lines);

    expect(violations).toEqual([]);
  });

  test("rejects tokenized auto-commit handoff without immediate cleanup", () => {
    const lines = [
      "jobs:",
      "  autofix:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Configure push credentials",
      "        if: ${{ steps.changes.outputs.has_changes == 'true' }}",
      "        env:",
      "          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}",
      '        run: git remote set-url origin "https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.git"',
      "      - name: Commit fixes",
      "        if: ${{ steps.changes.outputs.has_changes == 'true' }}",
      "        uses: stefanzweifel/git-auto-commit-action@v7",
      "      - run: git fetch origin HEAD"
    ];

    const violations = findTokenizedGitRemoteCredentialViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("immediate origin cleanup");
  });

  test("rejects tokenized remote setup before auto-commit when guards differ", () => {
    const lines = [
      "jobs:",
      "  autofix:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Configure push credentials",
      "        if: ${{ github.event_name == 'pull_request' }}",
      "        env:",
      "          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}",
      '        run: git remote set-url origin "https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.git"',
      "      - name: Commit fixes",
      "        if: ${{ steps.changes.outputs.has_changes == 'true' }}",
      "        uses: stefanzweifel/git-auto-commit-action@v7"
    ];

    const violations = findTokenizedGitRemoteCredentialViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("git-auto-commit-action handoff");
  });

  test("rejects tokenized remote setup before auto-commit when the handoff is unguarded", () => {
    const lines = [
      "jobs:",
      "  autofix:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Configure push credentials",
      "        env:",
      "          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}",
      '        run: git remote set-url origin "https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.git"',
      "      - name: Commit fixes",
      "        uses: stefanzweifel/git-auto-commit-action@v7"
    ];

    const violations = findTokenizedGitRemoteCredentialViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("single-command git-auto-commit-action handoff");
  });

  test("rejects auto-commit handoff blocks that also push manually", () => {
    const lines = [
      "jobs:",
      "  autofix:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Configure push credentials",
      "        if: ${{ steps.changes.outputs.has_changes == 'true' }}",
      "        env:",
      "          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}",
      "        run: |",
      '          git remote set-url origin "https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.git"',
      "          git push origin HEAD",
      "      - name: Commit fixes",
      "        if: ${{ steps.changes.outputs.has_changes == 'true' }}",
      "        uses: stefanzweifel/git-auto-commit-action@v7"
    ];

    const violations = findTokenizedGitRemoteCredentialViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("single-command git-auto-commit-action handoff");
  });

  test("rejects manual tokenized remote setup even when the same run block pushes", () => {
    const lines = [
      "jobs:",
      "  fork_fix:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Create bot branch and push",
      "        if: steps.changes.outputs.has_changes == 'true'",
      "        run: |",
      '          git remote add upstream "https://x-access-token:${{ secrets.GITHUB_TOKEN }}@github.com/${{ github.repository }}.git"',
      "          git push upstream bot/fix --force"
    ];

    const violations = findTokenizedGitRemoteCredentialViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("manual clone/fetch/push steps");
  });

  test("rejects tokenized remote setup in folded run scalars", () => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Configure push credentials too early",
      "        env:",
      "          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}",
      "        run: >",
      "          git remote set-url origin",
      '          "https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.git"'
    ];

    const violations = findTokenizedGitRemoteCredentialViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
  });

  test.each([
    'git -C . remote set-url origin "https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.git"',
    'git -c core.askPass=true clone "https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.git" repo',
    'git fetch "https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.git" HEAD',
    'git push "https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.git" HEAD'
  ])("rejects tokenized git command form: %s", (command) => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Tokenized git command",
      `        run: ${command}`
    ];

    const violations = findTokenizedGitRemoteCredentialViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
  });

  test("accepts command-scoped extraheader credentials for manual pushes", () => {
    const lines = [
      "jobs:",
      "  fork_fix:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Create bot branch and push",
      "        if: steps.changes.outputs.has_changes == 'true'",
      "        env:",
      "          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}",
      "        run: |",
      "          auth_header=\"$(printf 'x-access-token:%s' \"${GH_TOKEN}\" | base64 | tr -d '\\n')\"",
      '          git remote add upstream "https://github.com/${GITHUB_REPOSITORY}.git"',
      '          git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" push upstream bot/fix --force'
    ];

    const violations = findTokenizedGitRemoteCredentialViolations("workflow.yml", lines);

    expect(violations).toEqual([]);
  });

  test("ignores documentation text that mentions tokenized remotes", () => {
    const lines = [
      "jobs:",
      "  docs:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Explain remote policy",
      "        run: |",
      '          echo "Do not run git remote set-url origin https://x-access-token:TOKEN@example.invalid/repo.git"'
    ];

    const violations = findTokenizedGitRemoteCredentialViolations("workflow.yml", lines);

    expect(violations).toEqual([]);
  });

  test("ignores quoted documentation text with shell separators", () => {
    const lines = [
      "jobs:",
      "  docs:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Explain remote policy",
      "        run: |",
      '          echo "example; git remote set-url origin https://x-access-token:TOKEN@example.invalid/repo.git"'
    ];

    const violations = findTokenizedGitRemoteCredentialViolations("workflow.yml", lines);

    expect(violations).toEqual([]);
  });

  test("ignores heredoc content that mentions tokenized remotes", () => {
    const lines = [
      "jobs:",
      "  docs:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Write docs",
      "        run: |",
      "          cat <<'EOF' > notes.txt",
      "          git remote set-url origin https://x-access-token:TOKEN@example.invalid/repo.git",
      "          EOF"
    ];

    const violations = findTokenizedGitRemoteCredentialViolations("workflow.yml", lines);

    expect(violations).toEqual([]);
  });
});

describe("findPersistentGitExtraheaderCredentialViolations", () => {
  test("rejects persisted Git extraheader credentials", () => {
    const lines = [
      "jobs:",
      "  push:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Persist auth header",
      "        run: |",
      '          git config http.https://github.com/.extraheader "AUTHORIZATION: basic ${auth_header}"',
      "          git push origin HEAD"
    ];

    const violations = findPersistentGitExtraheaderCredentialViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("persists Git extraheader credentials");
  });

  test("rejects persisted Git extraheader credentials split across shell continuations", () => {
    const lines = [
      "jobs:",
      "  push:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Persist auth header",
      "        run: |",
      "          git config \\",
      '            http.https://github.com/.extraheader "AUTHORIZATION: basic ${auth_header}"',
      "          git push origin HEAD"
    ];

    const violations = findPersistentGitExtraheaderCredentialViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("persists Git extraheader credentials");
  });

  test("rejects persisted Git extraheader credentials in folded run scalars", () => {
    const lines = [
      "jobs:",
      "  push:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Persist auth header",
      "        run: >",
      "          git config",
      '          http.https://github.com/.extraheader "AUTHORIZATION: basic ${auth_header}"',
      "          && git push origin HEAD"
    ];

    const violations = findPersistentGitExtraheaderCredentialViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
  });

  test("rejects persisted Git extraheader credentials split across PowerShell continuations", () => {
    const lines = [
      "jobs:",
      "  push:",
      "    runs-on: windows-latest",
      "    steps:",
      "      - name: Persist auth header",
      "        shell: pwsh",
      "        run: |",
      "          git config `",
      '            http.https://github.com/.extraheader "AUTHORIZATION: basic ${auth_header}"',
      "          git push origin HEAD"
    ];

    const violations = findPersistentGitExtraheaderCredentialViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
  });

  test("accepts command-scoped Git extraheader credentials", () => {
    const lines = [
      "jobs:",
      "  push:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Push with command-scoped auth",
      "        run: |",
      '          git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" push origin HEAD'
    ];

    const violations = findPersistentGitExtraheaderCredentialViolations("workflow.yml", lines);

    expect(violations).toEqual([]);
  });

  test("ignores documentation text that mentions persistent extraheaders", () => {
    const lines = [
      "jobs:",
      "  docs:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Explain header policy",
      "        run: |",
      '          echo "Do not run git config http.https://github.com/.extraheader AUTHORIZATION"'
    ];

    const violations = findPersistentGitExtraheaderCredentialViolations("workflow.yml", lines);

    expect(violations).toEqual([]);
  });

  test("ignores quoted documentation text with separators that mentions extraheaders", () => {
    const lines = [
      "jobs:",
      "  docs:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Explain header policy",
      "        run: |",
      '          echo "example; git config http.https://github.com/.extraheader AUTHORIZATION"'
    ];

    const violations = findPersistentGitExtraheaderCredentialViolations("workflow.yml", lines);

    expect(violations).toEqual([]);
  });
});

describe("GitHub App auto-commit workflow policy", () => {
  test("rejects AUTO_COMMIT_APP_* checks that silently set has-app=false", () => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Check for the auto-commit GitHub App credentials",
      "        id: check-autocommit-app",
      "        env:",
      "          AUTO_COMMIT_APP_ID: ${{ secrets.AUTO_COMMIT_APP_ID }}",
      "          AUTO_COMMIT_APP_PRIVATE_KEY: ${{ secrets.AUTO_COMMIT_APP_PRIVATE_KEY }}",
      "        run: |",
      '          if [ -n "${AUTO_COMMIT_APP_ID:-}" ] && [ -n "${AUTO_COMMIT_APP_PRIVATE_KEY:-}" ]; then',
      '            echo "has-app=true" >> "${GITHUB_OUTPUT}"',
      "          else",
      '            echo "has-app=false" >> "${GITHUB_OUTPUT}"',
      "          fi"
    ];

    const violations = findAutoCommitAppCredentialWarningViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("without emitting a ::warning::");
  });

  test("accepts AUTO_COMMIT_APP_* checks that warn before setting has-app=false", () => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Check for the auto-commit GitHub App credentials",
      "        id: check-autocommit-app",
      "        env:",
      "          AUTO_COMMIT_APP_ID: ${{ secrets.AUTO_COMMIT_APP_ID }}",
      "          AUTO_COMMIT_APP_PRIVATE_KEY: ${{ secrets.AUTO_COMMIT_APP_PRIVATE_KEY }}",
      "        run: |",
      '          if [ -n "${AUTO_COMMIT_APP_ID:-}" ] && [ -n "${AUTO_COMMIT_APP_PRIVATE_KEY:-}" ]; then',
      '            echo "has-app=true" >> "${GITHUB_OUTPUT}"',
      "          else",
      '            echo "::warning::AUTO_COMMIT_APP_* secrets are not set; skipping auto-commit."',
      '            echo "has-app=false" >> "${GITHUB_OUTPUT}"',
      "          fi"
    ];

    const violations = findAutoCommitAppCredentialWarningViolations("workflow.yml", lines);

    expect(violations).toEqual([]);
  });

  test("rejects GitHub App token pushes through git-auto-commit-action", () => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Generate auto-commit GitHub App token",
      "        id: app-token",
      "        uses: actions/create-github-app-token@v2",
      "      - name: Commit generated file",
      "        uses: stefanzweifel/git-auto-commit-action@v7"
    ];

    const violations = findGitHubAppAutoCommitRobustnessViolations("workflow.yml", lines);

    expect(violations.some((v) => v.message.includes("git-auto-commit-action"))).toBe(true);
    expect(violations.some((v) => v.message.includes("freshness diagnostic"))).toBe(true);
  });

  test("rejects GitHub App token pushes without branch freshness diagnostics", () => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Generate auto-commit GitHub App token",
      "        id: app-token",
      "        uses: actions/create-github-app-token@v2",
      "      - name: Commit and push",
      "        run: |",
      "          git commit -m generated",
      '          git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" push origin "HEAD:${GITHUB_REF_NAME}"'
    ];

    const violations = findGitHubAppAutoCommitRobustnessViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("freshness diagnostic");
  });

  test("rejects GitHub App token pushes that do not pass command-scoped credentials", () => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Generate auto-commit GitHub App token",
      "        id: app-token",
      "        uses: actions/create-github-app-token@v2",
      "      - name: Check default branch is still current",
      "        run: |",
      '          git fetch origin "refs/heads/${GITHUB_REF_NAME}:refs/remotes/origin/${GITHUB_REF_NAME}"',
      '          latest_sha="$(git rev-parse "refs/remotes/origin/${GITHUB_REF_NAME}")"',
      '          echo "::warning::Default branch advanced to ${latest_sha}; skipping stale output."',
      "      - name: Commit and push",
      "        run: |",
      "          git commit -m generated",
      '          git push origin "HEAD:${GITHUB_REF_NAME}"'
    ];

    const violations = findGitHubAppAutoCommitRobustnessViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("command-scoped GitHub extraheader");
  });

  test("rejects unauthenticated pushes even when the same step has an authenticated fetch", () => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Generate auto-commit GitHub App token",
      "        id: app-token",
      "        uses: actions/create-github-app-token@v2",
      "      - name: Commit and push",
      "        run: |",
      '          git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" \\',
      "            fetch --no-tags --depth=1 origin \\",
      '            "refs/heads/${GITHUB_REF_NAME}:refs/remotes/origin/${GITHUB_REF_NAME}"',
      '          latest_sha="$(git rev-parse "refs/remotes/origin/${GITHUB_REF_NAME}")"',
      '          echo "::warning::Default branch advanced to ${latest_sha}; regenerating generated output."',
      "          git commit -m generated",
      '          git push origin "HEAD:${GITHUB_REF_NAME}"'
    ];

    const violations = findGitHubAppAutoCommitRobustnessViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("on the push command");
  });

  test("rejects extraheader -c arguments placed after the push subcommand", () => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Generate auto-commit GitHub App token",
      "        id: app-token",
      "        uses: actions/create-github-app-token@v2",
      "      - name: Commit and push",
      "        run: |",
      '          git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" \\',
      "            fetch --no-tags --depth=1 origin \\",
      '            "refs/heads/${GITHUB_REF_NAME}:refs/remotes/origin/${GITHUB_REF_NAME}"',
      '          latest_sha="$(git rev-parse "refs/remotes/origin/${GITHUB_REF_NAME}")"',
      '          echo "::warning::Default branch advanced to ${latest_sha}; regenerating generated output."',
      "          git commit -m generated",
      '          git push -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" origin "HEAD:${GITHUB_REF_NAME}"'
    ];

    const violations = findGitHubAppAutoCommitRobustnessViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("on the push command");
  });

  test.each([
    ['if git push origin "HEAD:${GITHUB_REF_NAME}"; then'],
    ['if { git push origin "HEAD:${default_branch}"; }; then'],
    ['if (git push origin "HEAD:${default_branch}"); then']
  ])("rejects shell-control-prefixed pushes without command-scoped credentials: %s", (pushLine) => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Generate auto-commit GitHub App token",
      "        id: app-token",
      "        uses: actions/create-github-app-token@v2",
      "      - name: Commit and push",
      "        run: |",
      '          git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" \\',
      "            fetch --no-tags --depth=1 origin \\",
      '            "refs/heads/${GITHUB_REF_NAME}:refs/remotes/origin/${GITHUB_REF_NAME}"',
      '          latest_sha="$(git rev-parse "refs/remotes/origin/${GITHUB_REF_NAME}")"',
      '          echo "::warning::Default branch advanced to ${latest_sha}; regenerating generated output."',
      "          git commit -m generated",
      `          ${pushLine}`,
      "            exit 0",
      "          fi"
    ];

    const violations = findGitHubAppAutoCommitRobustnessViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("on the push command");
  });

  test.each([
    ['git push origin "HEAD:refs/heads/${GITHUB_REF_NAME}"'],
    ['git push origin "+HEAD:${GITHUB_REF_NAME}"'],
    ['git push origin "HEAD:${{ github.event.repository.default_branch }}"'],
    ['git push origin "HEAD:refs/heads/${{ github.event.repository.default_branch }}"'],
    ['git push origin "HEAD:${default_branch}"'],
    ['git push origin "HEAD:$default_branch"'],
    ['git push origin "${GITHUB_REF_NAME}:${GITHUB_REF_NAME}"'],
    ["git push origin main"],
    ["git push origin refs/heads/master:refs/heads/master"],
    ["git push origin HEAD"]
  ])(
    "rejects equivalent current-head App-token pushes without command-scoped credentials: %s",
    (pushCommand) => {
      const lines = [
        "jobs:",
        "  update:",
        "    runs-on: ubuntu-latest",
        "    steps:",
        "      - name: Generate auto-commit GitHub App token",
        "        id: app-token",
        "        uses: actions/create-github-app-token@v2",
        "      - name: Commit and push",
        "        run: |",
        '          git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" \\',
        "            fetch --no-tags --depth=1 origin \\",
        '            "refs/heads/${GITHUB_REF_NAME}:refs/remotes/origin/${GITHUB_REF_NAME}"',
        '          latest_sha="$(git rev-parse "refs/remotes/origin/${GITHUB_REF_NAME}")"',
        '          echo "::warning::Default branch advanced to ${latest_sha}; regenerating generated output."',
        "          git commit -m generated",
        `          ${pushCommand}`
      ];

      const violations = findGitHubAppAutoCommitRobustnessViolations("workflow.yml", lines);

      expect(violations).toHaveLength(1);
      expect(violations[0].message).toContain("on the push command");
    }
  );

  test("rejects any GitHub App token git push without command-scoped credentials", () => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Generate auto-commit GitHub App token",
      "        id: app-token",
      "        uses: actions/create-github-app-token@v2",
      "      - name: Push release tag",
      "        run: |",
      "          git push origin refs/tags/v1.2.3"
    ];

    const violations = findGitHubAppAutoCommitRobustnessViolations("workflow.yml", lines);

    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("on the push command");
  });

  test("accepts command-scoped non-branch App-token pushes without freshness diagnostics", () => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Generate auto-commit GitHub App token",
      "        id: app-token",
      "        uses: actions/create-github-app-token@v2",
      "      - name: Push release tag",
      "        run: |",
      '          git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" \\',
      "            push origin refs/tags/v1.2.3"
    ];

    const violations = findGitHubAppAutoCommitRobustnessViolations("workflow.yml", lines);

    expect(violations).toEqual([]);
  });

  test("accepts race-aware command-scoped GitHub App token pushes", () => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Generate auto-commit GitHub App token",
      "        id: app-token",
      "        uses: actions/create-github-app-token@v2",
      "      - name: Commit and push",
      "        run: |",
      '          git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" \\',
      "            fetch --no-tags --depth=1 origin \\",
      '            "refs/heads/${GITHUB_REF_NAME}:refs/remotes/origin/${GITHUB_REF_NAME}"',
      '          latest_sha="$(git rev-parse "refs/remotes/origin/${GITHUB_REF_NAME}")"',
      '          echo "::warning::Default branch advanced to ${latest_sha}; regenerating generated output."',
      "          git commit -m generated",
      '          git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" \\',
      '            push origin "HEAD:${GITHUB_REF_NAME}"'
    ];

    const violations = findGitHubAppAutoCommitRobustnessViolations("workflow.yml", lines);

    expect(violations).toEqual([]);
  });

  test("accepts shell-control-prefixed pushes with command-scoped credentials", () => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Generate auto-commit GitHub App token",
      "        id: app-token",
      "        uses: actions/create-github-app-token@v2",
      "      - name: Commit and push",
      "        run: |",
      '          git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" \\',
      "            fetch --no-tags --depth=1 origin \\",
      '            "refs/heads/${GITHUB_REF_NAME}:refs/remotes/origin/${GITHUB_REF_NAME}"',
      '          latest_sha="$(git rev-parse "refs/remotes/origin/${GITHUB_REF_NAME}")"',
      '          echo "::warning::Default branch advanced to ${latest_sha}; regenerating generated output."',
      "          git commit -m generated",
      '          if git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" \\',
      '            push origin "HEAD:${GITHUB_REF_NAME}"; then',
      "            exit 0",
      "          fi"
    ];

    const violations = findGitHubAppAutoCommitRobustnessViolations("workflow.yml", lines);

    expect(violations).toEqual([]);
  });

  test.each([
    [
      'git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" push origin "HEAD:refs/heads/${GITHUB_REF_NAME}"'
    ],
    [
      'git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" push origin "HEAD:${{ github.event.repository.default_branch }}"'
    ],
    [
      'git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" push origin "HEAD:${default_branch}"'
    ],
    [
      'git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" push origin "${GITHUB_REF_NAME}:${GITHUB_REF_NAME}"'
    ],
    [
      'git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" push origin main'
    ],
    [
      'git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" push origin refs/heads/master:refs/heads/master'
    ],
    [
      'git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" push origin HEAD'
    ]
  ])("accepts equivalent command-scoped current-head App-token pushes: %s", (pushCommand) => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Generate auto-commit GitHub App token",
      "        id: app-token",
      "        uses: actions/create-github-app-token@v2",
      "      - name: Commit and push",
      "        run: |",
      '          git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" \\',
      "            fetch --no-tags --depth=1 origin \\",
      '            "refs/heads/${GITHUB_REF_NAME}:refs/remotes/origin/${GITHUB_REF_NAME}"',
      '          latest_sha="$(git rev-parse "refs/remotes/origin/${GITHUB_REF_NAME}")"',
      '          echo "::warning::Default branch advanced to ${latest_sha}; regenerating generated output."',
      "          git commit -m generated",
      `          ${pushCommand}`
    ];

    const violations = findGitHubAppAutoCommitRobustnessViolations("workflow.yml", lines);

    expect(violations).toEqual([]);
  });

  test("accepts default-branch variable freshness diagnostics", () => {
    const lines = [
      "jobs:",
      "  update:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - name: Generate auto-commit GitHub App token",
      "        id: app-token",
      "        uses: actions/create-github-app-token@v2",
      "      - name: Commit and push",
      "        run: |",
      '          default_branch="${{ github.event.repository.default_branch }}"',
      '          git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" \\',
      "            fetch --no-tags --depth=1 origin \\",
      '            "refs/heads/${default_branch}:refs/remotes/origin/${default_branch}"',
      '          latest_sha="$(git rev-parse "refs/remotes/origin/${default_branch}")"',
      '          echo "::warning::Default branch advanced to ${latest_sha}; regenerating generated output."',
      "          git commit -m generated",
      '          git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic ${auth_header}" \\',
      '            push origin "HEAD:${default_branch}"'
    ];

    const violations = findGitHubAppAutoCommitRobustnessViolations("workflow.yml", lines);

    expect(violations).toEqual([]);
  });
});

describe("findSelfHostedRunnerPreflightViolations (Bug 3)", () => {
  const runnerPreflightStep = [
    "      - name: Probe self-hosted runner availability",
    "        env:",
    "          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}",
    '          REQUIRED_LABELS: "self-hosted,Windows,RAM-64GB"',
    "        run: |",
    "          gh api repos/${GITHUB_REPOSITORY}/actions/runners"
  ];

  test("flags self-hosted job that has no preflight dependency", () => {
    const lines = [
      "jobs:",
      "  unity-tests:",
      "    runs-on: [self-hosted, Windows, RAM-64GB]",
      "    steps:",
      "      - name: Test",
      "        run: echo test"
    ];

    const violations = findSelfHostedRunnerPreflightViolations("workflow.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("preflight");
    expect(violations[0].message).toContain("unity-runners-after-transfer.md");
  });

  test("accepts self-hosted job that needs a preflight job in the same workflow", () => {
    const lines = [
      "jobs:",
      "  runner-preflight:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      ...runnerPreflightStep,
      "  unity-tests:",
      "    needs: runner-preflight",
      "    runs-on: [self-hosted, Windows, RAM-64GB]",
      "    steps:",
      "      - name: Test",
      "        run: echo test"
    ];

    const violations = findSelfHostedRunnerPreflightViolations("workflow.yml", lines);
    expect(violations).toHaveLength(0);
  });

  test("accepts self-hosted job that needs a preflight via block-list needs", () => {
    const lines = [
      "jobs:",
      "  matrix-config:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - run: echo matrix",
      "  runner-preflight:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      ...runnerPreflightStep,
      "  unity-tests:",
      "    needs:",
      "      - matrix-config",
      "      - runner-preflight",
      "    runs-on: [self-hosted, Windows, RAM-64GB]",
      "    steps:",
      "      - name: Test",
      "        run: echo test"
    ];

    const violations = findSelfHostedRunnerPreflightViolations("workflow.yml", lines);
    expect(violations).toHaveLength(0);
  });

  test("ignores jobs that target ubuntu-latest (not self-hosted)", () => {
    const lines = [
      "jobs:",
      "  ubuntu-job:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - run: echo hi"
    ];

    const violations = findSelfHostedRunnerPreflightViolations("workflow.yml", lines);
    expect(violations).toHaveLength(0);
  });

  test("does not require the preflight job to gate itself", () => {
    const lines = [
      "jobs:",
      "  runner-preflight:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      ...runnerPreflightStep
    ];

    const violations = findSelfHostedRunnerPreflightViolations("workflow.yml", lines);
    expect(violations).toHaveLength(0);
  });

  test("flags self-hosted job whose needs target lacks the runner-probe marker", () => {
    // The preflight job MUST contain the actions/runners marker for
    // jobIsRunnerAccessPreflight to accept it. After the H1 rewrite the
    // job name is no longer consulted, but the marker is still required.
    const lines = [
      "jobs:",
      "  preflight-thing:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - run: echo not the right probe",
      "  unity-tests:",
      "    needs: preflight-thing",
      "    runs-on: [self-hosted, Windows, RAM-64GB]",
      "    steps:",
      "      - run: echo test"
    ];

    const violations = findSelfHostedRunnerPreflightViolations("workflow.yml", lines);
    expect(violations).toHaveLength(1);
  });

  test("flags self-hosted job whose needs target is missing from the workflow", () => {
    const lines = [
      "jobs:",
      "  unity-tests:",
      "    needs: phantom-job",
      "    runs-on: [self-hosted, Windows, RAM-64GB]",
      "    steps:",
      "      - run: echo test"
    ];

    const violations = findSelfHostedRunnerPreflightViolations("workflow.yml", lines);
    expect(violations).toHaveLength(1);
  });

  test.each([
    // [name, runsOn, expectedHasSelfHosted]
    ["inline array with self-hosted", "[self-hosted, Windows, RAM-64GB]", true],
    ["inline array without self-hosted", "[ubuntu-latest]", false],
    ["scalar ubuntu-latest", "ubuntu-latest", false],
    ["scalar self-hosted", "self-hosted", true]
  ])("extractStaticJobLabels: %s", (_name, runsOnText, expectedHasSelfHosted) => {
    const lines = [
      "jobs:",
      "  job1:",
      `    runs-on: ${runsOnText}`,
      "    steps:",
      "      - run: echo hi"
    ];
    const { extractJobs } = require("../validate-workflows.js");
    const jobs = extractJobs(lines);
    expect(jobs).toHaveLength(1);
    const labels = extractStaticJobLabels(lines, jobs[0]);
    if (expectedHasSelfHosted) {
      expect(labels).toContain("self-hosted");
    } else {
      expect(labels || []).not.toContain("self-hosted");
    }
  });

  test("jobIsRunnerAccessPreflight uses the structural signature only (H1)", () => {
    const { extractJobs } = require("../validate-workflows.js");

    // (1) Canonical positive: hosted runner + actions/runners marker.
    const matching = [
      "jobs:",
      "  runner-preflight:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - run: gh api repos/${GITHUB_REPOSITORY}/actions/runners"
    ];
    let jobs = extractJobs(matching);
    expect(jobIsRunnerAccessPreflight(matching, jobs[0])).toBe(true);

    // (2) No marker -> not a preflight (name alone is insufficient).
    const nameOnly = [
      "jobs:",
      "  runner-preflight:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - run: echo unrelated"
    ];
    jobs = extractJobs(nameOnly);
    expect(jobIsRunnerAccessPreflight(nameOnly, jobs[0])).toBe(false);

    // (3) Marker on a hosted-runner job with a DIFFERENT name still
    // qualifies under the structural-only rule (H1). The previous
    // implementation gated on /preflight/i which overfit the name; this
    // assertion locks in that the name is no longer consulted.
    const markerNoPreflightName = [
      "jobs:",
      "  setup:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - run: gh api repos/${GITHUB_REPOSITORY}/actions/runners"
    ];
    jobs = extractJobs(markerNoPreflightName);
    expect(jobIsRunnerAccessPreflight(markerNoPreflightName, jobs[0])).toBe(true);

    // (4) Marker on a SELF-HOSTED job does NOT qualify -- a self-hosted
    // job cannot gate self-hosted access. The whole point of the
    // preflight is to surface a failure BEFORE the self-hosted dispatch.
    const markerOnSelfHosted = [
      "jobs:",
      "  bad:",
      "    runs-on: [self-hosted, Windows, RAM-64GB]",
      "    steps:",
      "      - run: gh api repos/${GITHUB_REPOSITORY}/actions/runners"
    ];
    jobs = extractJobs(markerOnSelfHosted);
    expect(jobIsRunnerAccessPreflight(markerOnSelfHosted, jobs[0])).toBe(false);

    // (5) Marker on a hosted runner with a non-canonical name +
    // org-scoped endpoint also qualifies. This is the actual shape
    // after the runner-preflight rewrite (org-first, repo-fallback).
    const orgScopedEndpoint = [
      "jobs:",
      "  runner-access-probe:",
      "    runs-on: ubuntu-22.04",
      "    steps:",
      "      - run: gh api orgs/${GITHUB_REPOSITORY_OWNER}/actions/runners"
    ];
    jobs = extractJobs(orgScopedEndpoint);
    expect(jobIsRunnerAccessPreflight(orgScopedEndpoint, jobs[0])).toBe(true);

    // (6) Other hosted-runner label families also qualify (windows-latest, macos-latest).
    const onWindows = [
      "jobs:",
      "  probe:",
      "    runs-on: windows-latest",
      "    steps:",
      "      - run: gh api orgs/foo/actions/runners"
    ];
    jobs = extractJobs(onWindows);
    expect(jobIsRunnerAccessPreflight(onWindows, jobs[0])).toBe(true);
  });

  test("integration: real unity-tests.yml shape passes", () => {
    // Mirror the actual unity-tests.yml structure: matrix-config +
    // runner-preflight + unity-tests with needs on both.
    const lines = [
      "jobs:",
      "  matrix-config:",
      "    runs-on: ubuntu-latest",
      "    steps:",
      "      - run: echo matrix",
      "  runner-preflight:",
      "    runs-on: ubuntu-latest",
      "    timeout-minutes: 3",
      "    steps:",
      ...runnerPreflightStep,
      "  unity-tests:",
      "    needs:",
      "      - matrix-config",
      "      - runner-preflight",
      "    runs-on: [self-hosted, Windows, RAM-64GB]",
      "    steps:",
      "      - run: echo test"
    ];

    const violations = findSelfHostedRunnerPreflightViolations("unity-tests.yml", lines);
    expect(violations).toHaveLength(0);
  });
});

describe("validateWorkflow policy integration", () => {
  test("reports ignored path filters and unsafe lockfile install policy", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "validate-workflows-policy-"));
    try {
      const workflowPath = path.join(tempDir, "policy-test.yml");
      const workflowContent = [
        "name: Policy Test",
        "on:",
        "  pull_request:",
        "    paths:",
        "      - package-lock.json",
        "jobs:",
        "  test:",
        "    runs-on: ubuntu-latest",
        "    steps:",
        "      - name: Install dependencies",
        "        run: |",
        "          if [ ! -f package-lock.json ]; then",
        "            exit 1",
        "          fi",
        "          npm ci"
      ].join("\n");

      fs.writeFileSync(workflowPath, workflowContent, "utf8");
      const isIgnoredPathMock = (_repoRoot, candidatePath) => candidatePath === "package-lock.json";
      const violations = validateWorkflow(workflowPath, {
        repoRoot: tempDir,
        isIgnoredPathFn: isIgnoredPathMock
      });

      const errorMessages = violations
        .filter((violation) => violation.severity === "error")
        .map((violation) => violation.message);

      expect(errorMessages.some((message) => message.includes("ignored by git"))).toBe(true);
      expect(
        errorMessages.some((message) =>
          message.includes("must not fail when the lockfile is absent")
        )
      ).toBe(true);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("reports workflow line-length violations using repo yamllint ceiling", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "validate-workflows-line-length-"));
    try {
      const workflowDir = path.join(tempDir, ".github", "workflows");
      fs.mkdirSync(workflowDir, { recursive: true });
      fs.writeFileSync(
        path.join(tempDir, ".yamllint.yaml"),
        ["rules:", "  line-length:", "    max: 40"].join("\n"),
        "utf8"
      );

      const workflowPath = path.join(workflowDir, "line-length.yml");
      fs.writeFileSync(
        workflowPath,
        [
          "name: Line Length",
          "jobs:",
          "  lint:",
          "    runs-on: ubuntu-latest",
          "    steps:",
          "      - run: echo this workflow line is intentionally longer than forty chars"
        ].join("\n"),
        "utf8"
      );

      const violations = validateWorkflow(workflowPath, {
        repoRoot: tempDir,
        isIgnoredPathFn: () => false
      });

      expect(
        violations.some(
          (violation) =>
            violation.severity === "error" &&
            violation.message.includes("Workflow line exceeds 40 characters")
        )
      ).toBe(true);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("surfaces ignore policy evaluation failures as validation errors", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "validate-workflows-policy-error-"));
    try {
      const workflowPath = path.join(tempDir, "policy-error-test.yml");
      fs.writeFileSync(
        workflowPath,
        [
          "name: Policy Error Test",
          "on:",
          "  pull_request:",
          "    paths:",
          "      - package-lock.json"
        ].join("\n"),
        "utf8"
      );

      const violations = validateWorkflow(workflowPath, {
        repoRoot: tempDir,
        isIgnoredPathFn: () => {
          throw new Error("mock git failure");
        }
      });

      expect(
        violations.some((violation) =>
          violation.message.includes("Workflow validation failed while evaluating ignore policy")
        )
      ).toBe(true);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("uses provided repoRoot when reporting violation file paths", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "validate-workflows-repo-root-"));
    try {
      const workflowDir = path.join(tempDir, ".github", "workflows");
      fs.mkdirSync(workflowDir, { recursive: true });

      const workflowPath = path.join(workflowDir, "relative-path.yml");
      fs.writeFileSync(
        workflowPath,
        [
          "name: Relative Path",
          "jobs:",
          "  lint:",
          "    runs-on: ubuntu-latest",
          "    steps:",
          "      - run: git add --renormalize -- '*.md' '*.json'"
        ].join("\n"),
        "utf8"
      );

      const violations = validateWorkflow(workflowPath, {
        repoRoot: tempDir,
        isIgnoredPathFn: () => false
      });

      expect(violations.length).toBeGreaterThan(0);
      expect(
        violations.every((violation) => violation.file === ".github/workflows/relative-path.yml")
      ).toBe(true);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("current repository workflows pass with no validation errors", () => {
    const workflowsDir = path.resolve(__dirname, "../../.github/workflows");
    const workflowFiles = fs
      .readdirSync(workflowsDir)
      .filter((fileName) => fileName.endsWith(".yml") || fileName.endsWith(".yaml"));

    for (const workflowFile of workflowFiles) {
      const workflowPath = path.join(workflowsDir, workflowFile);
      const violations = validateWorkflow(workflowPath);
      const errors = violations.filter((violation) => violation.severity === "error");
      expect(errors).toHaveLength(0);
    }
  });

  test("pre-commit tooling workflow invokes pre-push-only parser hook at pre-push stage", () => {
    const workflowPath = path.resolve(
      __dirname,
      "../../.github/workflows/pre-commit-tooling-check.yml"
    );
    const workflowContent = fs.readFileSync(workflowPath, "utf8");

    expect(workflowContent).toContain(
      "node scripts/ensure-pre-commit.js run --hook-stage pre-push script-parser-tests --all-files"
    );
    expect(workflowContent).not.toContain(
      "node scripts/ensure-pre-commit.js run script-parser-tests --all-files"
    );
  });

  test("sync-wiki push retries refresh the wiki remote before retrying", () => {
    const workflowPath = path.resolve(__dirname, "../../.github/workflows/sync-wiki.yml");
    const workflowContent = fs.readFileSync(workflowPath, "utf8");

    expect(workflowContent).toContain("Wiki push attempt $i failed");
    expect(workflowContent).toContain('fetch --no-tags origin "${wiki_branch}"');
    expect(workflowContent).toContain('git rebase "origin/${wiki_branch}"');
  });

  test("update-llms-txt regenerates before checking a retried branch tip", () => {
    const workflowPath = path.resolve(__dirname, "../../.github/workflows/update-llms-txt.yml");
    const workflowContent = fs.readFileSync(workflowPath, "utf8");
    const retryIndex = workflowContent.indexOf("retrying from ${latest_after_push}");
    const checkoutIndex = workflowContent.indexOf(
      'git checkout -f -B "${GITHUB_REF_NAME}" "refs/remotes/origin/${GITHUB_REF_NAME}"',
      retryIndex
    );
    const updateIndex = workflowContent.indexOf("npm run update:llms-txt", checkoutIndex);
    const validateIndex = workflowContent.indexOf("npm run validate:llms-txt", updateIndex);

    expect(retryIndex).toBeGreaterThanOrEqual(0);
    expect(checkoutIndex).toBeGreaterThan(retryIndex);
    expect(updateIndex).toBeGreaterThan(checkoutIndex);
    expect(validateIndex).toBeGreaterThan(updateIndex);
  });

  test("hook performance workflow provisions dotnet tools before measuring C# hooks", () => {
    const workflowPath = path.resolve(
      __dirname,
      "../../.github/workflows/hook-perf-measurement.yml"
    );
    const workflowContent = fs.readFileSync(workflowPath, "utf8");
    const setupDotnetIndex = workflowContent.indexOf("actions/setup-dotnet@v5");
    const restoreIndex = workflowContent.indexOf("dotnet tool restore");
    const diagnosticsIndex = workflowContent.indexOf("dotnet tool list");
    const measureIndex = workflowContent.indexOf("node scripts/measure-hook-wallclock.js");

    expect(setupDotnetIndex).toBeGreaterThanOrEqual(0);
    expect(restoreIndex).toBeGreaterThanOrEqual(0);
    expect(diagnosticsIndex).toBeGreaterThanOrEqual(0);
    expect(measureIndex).toBeGreaterThanOrEqual(0);
    expect(workflowContent).toContain('dotnet-version: "8.0.x"');
    expect(setupDotnetIndex).toBeLessThan(restoreIndex);
    expect(restoreIndex).toBeLessThan(measureIndex);
    expect(diagnosticsIndex).toBeLessThan(measureIndex);
  });

  test("release drafter lets version resolver choose the draft version from labels", () => {
    const workflowPath = path.resolve(__dirname, "../../.github/workflows/release-drafter.yml");
    const workflowContent = fs.readFileSync(workflowPath, "utf8");

    expect(workflowContent).toContain("uses: release-drafter/release-drafter@v7");
    expect(workflowContent).not.toMatch(
      /\bversion:\s*\$\{\{\s*steps\.version\.outputs\.version\s*\}\}/
    );
    expect(workflowContent).not.toContain("name: version");
    expect(workflowContent).not.toContain("id: version");
  });

  test("release drafter body uses Unreleased changelog without changing the draft tag", () => {
    const workflowPath = path.resolve(__dirname, "../../.github/workflows/release-drafter.yml");
    const workflowContent = fs.readFileSync(workflowPath, "utf8");

    expect(workflowContent).toContain('awk -v ver="Unreleased"');
    expect(workflowContent).toContain("CHANGELOG_SECTION");
    expect(workflowContent).not.toMatch(/\btag_name:\s*version\b/);
    expect(workflowContent).not.toMatch(/\bname:\s*version\b/);
  });

  test("workflow Node versions satisfy current JavaScript toolchain engines", () => {
    const workflowDirs = [
      path.resolve(__dirname, "../../.github/workflows"),
      path.resolve(__dirname, "../../.github/workflows-disabled")
    ];

    const nodeVersionEntries = [];
    for (const workflowsDir of workflowDirs) {
      const workflowFiles = fs
        .readdirSync(workflowsDir)
        .filter((fileName) => fileName.endsWith(".yml") || fileName.endsWith(".yaml"));

      for (const workflowFile of workflowFiles) {
        const workflowPath = path.join(workflowsDir, workflowFile);
        const workflowContent = fs.readFileSync(workflowPath, "utf8");
        const matches = workflowContent.matchAll(/node-version:\s*["']?([^"'\s#]+)["']?/g);
        for (const match of matches) {
          nodeVersionEntries.push({
            workflowFile: path.relative(path.resolve(__dirname, "../.."), workflowPath),
            version: match[1]
          });
        }
      }
    }

    expect(nodeVersionEntries.length).toBeGreaterThan(0);
    for (const entry of nodeVersionEntries) {
      expect(entry.version).toBe("22.18.0");
    }
  });
});

describe("forbidPlainShellBashOnSelfHostedWindows", () => {
  test.each([
    {
      name: "self-hosted Windows label set",
      labels: ["self-hosted", "Windows", "RAM-64GB"],
      expected: true
    },
    {
      name: "self-hosted Linux label set",
      labels: ["self-hosted", "Linux", "X64", "RAM-64GB"],
      expected: false
    },
    {
      name: "self-hosted macOS label set",
      labels: ["self-hosted", "macOS", "ARM64"],
      expected: false
    },
    {
      name: "bare self-hosted",
      labels: ["self-hosted"],
      expected: false
    },
    {
      name: "hosted ubuntu",
      labels: ["ubuntu-latest"],
      expected: false
    },
    {
      name: "hosted windows-latest is not self-hosted",
      labels: ["windows-latest"],
      expected: false
    },
    {
      name: "empty",
      labels: [],
      expected: false
    }
  ])("isSelfHostedWindowsLabelSet: $name", ({ labels, expected }) => {
    expect(isSelfHostedWindowsLabelSet(labels)).toBe(expected);
  });

  test.each([
    { name: "pwsh allowed", shell: "pwsh", expected: true },
    { name: "powershell allowed", shell: "powershell", expected: true },
    { name: "cmd allowed", shell: "cmd", expected: true },
    { name: "bash forbidden", shell: "bash", expected: false },
    { name: "sh forbidden", shell: "sh", expected: false },
    {
      name: "explicit Git Bash absolute path allowed",
      shell: "'C:\\Program Files\\Git\\bin\\bash.EXE' --noprofile --norc -eo pipefail {0}",
      expected: true
    },
    {
      name: "WSL stub absolute path explicitly rejected",
      shell: "'C:\\Windows\\System32\\bash.exe' -c {0}",
      expected: false
    },
    { name: "empty string forbidden", shell: "", expected: false },
    { name: "non-string forbidden", shell: null, expected: false }
  ])("isAllowedSelfHostedWindowsShell: $name", ({ shell, expected }) => {
    expect(isAllowedSelfHostedWindowsShell(shell)).toBe(expected);
  });

  test.each([
    {
      name: "flags shell: bash on self-hosted Windows step",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: diagnostics",
        "        shell: bash",
        "        run: echo hello"
      ],
      expectedViolations: 1
    },
    {
      name: "flags unspecified shell on self-hosted Windows step",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: diagnostics",
        "        run: echo hello"
      ],
      expectedViolations: 1
    },
    {
      name: "allows shell: pwsh on self-hosted Windows step",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: diagnostics",
        "        shell: pwsh",
        "        run: Write-Output 'hello'"
      ],
      expectedViolations: 0
    },
    {
      name: "allows uses: composite action on self-hosted Windows step",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: diagnostics",
        "        uses: ./.github/actions/print-self-hosted-runner-diagnostics"
      ],
      expectedViolations: 0
    },
    {
      name: "allows job defaults.run.shell pwsh on self-hosted Windows job",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    defaults:",
        "      run:",
        "        shell: pwsh",
        "    steps:",
        "      - name: diagnostics",
        "        run: Write-Output 'hello'"
      ],
      expectedViolations: 0
    },
    {
      name: "does not enforce policy on Linux self-hosted job",
      lines: [
        "jobs:",
        "  linux-job:",
        "    runs-on: [self-hosted, Linux, X64, RAM-64GB]",
        "    steps:",
        "      - name: diagnostics",
        "        shell: bash",
        "        run: echo hello"
      ],
      expectedViolations: 0
    },
    {
      name: "does not enforce policy on hosted ubuntu job",
      lines: [
        "jobs:",
        "  ubuntu-job:",
        "    runs-on: ubuntu-latest",
        "    steps:",
        "      - name: diagnostics",
        "        shell: bash",
        "        run: echo hello"
      ],
      expectedViolations: 0
    },
    {
      name: "flags multiple offending steps in one job",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: a",
        "        shell: bash",
        "        run: echo a",
        "      - name: b",
        "        shell: bash",
        "        run: echo b"
      ],
      expectedViolations: 2
    },
    {
      name: "allows explicit Git Bash absolute path shell",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: needs-posix",
        "        shell: \"'C:\\\\Program Files\\\\Git\\\\bin\\\\bash.EXE' --noprofile --norc -eo pipefail {0}\"",
        "        run: echo hello"
      ],
      expectedViolations: 0
    },
    {
      // Finding #10: workflow-scope defaults.run.shell coverage. Without
      // this fixture, removing the workflow-defaults branch from
      // findForbidPlainShellBashOnSelfHostedWindowsViolations would not
      // be observed by the test matrix.
      //
      // Round-3 hardening (MINOR-B): the previous shape of this fixture
      // (step has no inline `shell:` AND no workflow defaults) collapses
      // to the same "no shell" violation as the empty-defaults case, so
      // null-ing the `workflowDefaultsShell` lookup inside the rule body
      // does not flip this fixture from pass to fail. The rule now
      // annotates the source of the resolved shell in the violation
      // message ("resolved via workflow defaults" / "resolved via job
      // defaults" / "resolved via step"), and this fixture asserts the
      // workflow-defaults annotation is present. Combined with the
      // belt-and-suspenders fixture immediately below (workflow defaults
      // BASH overridden by step-level pwsh, expected 0 violations), the
      // workflow-defaults consultation is observable from the test
      // matrix.
      name: "flags workflow-scope defaults.run.shell: bash on self-hosted Windows step",
      lines: [
        "defaults:",
        "  run:",
        "    shell: bash",
        "",
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: diagnostics",
        "        run: echo hello"
      ],
      expectedViolations: 1,
      expectedMessageMatch: /resolved via workflow defaults/
    },
    {
      // Belt-and-suspenders: workflow defaults sets shell: bash, but the
      // step explicitly overrides with shell: pwsh. The override MUST win
      // (step-level beats workflow-defaults), so this scenario produces
      // zero violations. If a future refactor accidentally swaps the
      // precedence (e.g. workflowDefaultsShell preferred over stepShell),
      // this fixture will start emitting a violation.
      name: "step-level shell: pwsh overrides workflow-defaults shell: bash (no violation)",
      lines: [
        "defaults:",
        "  run:",
        "    shell: bash",
        "",
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: diagnostics",
        "        shell: pwsh",
        "        run: Write-Output 'hello'"
      ],
      expectedViolations: 0
    },
    {
      // Round-3 (MINOR-B) coverage probe: step-level shell: bash on a
      // self-hosted Windows job MUST be annotated as "resolved via step"
      // (not workflow defaults). Locks in the source-attribution shape so
      // a refactor that always reports "workflow defaults" surfaces here.
      name: "step-level shell: bash annotation matches 'resolved via step'",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: diagnostics",
        "        shell: bash",
        "        run: echo hello"
      ],
      expectedViolations: 1,
      expectedMessageMatch: /resolved via step/
    },
    {
      // Round-3 (MINOR-B) coverage probe: job-defaults shell: bash
      // annotation must read "resolved via job defaults" (NOT workflow
      // defaults, even when workflow defaults are absent).
      name: "job-defaults shell: bash annotation matches 'resolved via job defaults'",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    defaults:",
        "      run:",
        "        shell: bash",
        "    steps:",
        "      - name: diagnostics",
        "        run: echo hello"
      ],
      expectedViolations: 1,
      expectedMessageMatch: /resolved via job defaults/
    },
    {
      // Finding #4: dynamic runs-on resolved via fromJSON(needs....outputs....)
      // -- emitting branch produces a self-hosted Windows label set. The
      // shell:bash policy MUST be applied even though the runs-on is dynamic.
      name: "flags shell: bash on dynamic runs-on resolving to self-hosted Windows",
      lines: [
        "jobs:",
        "  matrix-config:",
        "    runs-on: ubuntu-latest",
        "    outputs:",
        "      labels: ${{ steps.pick.outputs.labels }}",
        "    steps:",
        "      - name: pick",
        "        id: pick",
        "        run: |",
        '          if [ "$GITHUB_EVENT_NAME" = "pull_request" ]; then',
        '            echo \'labels=["self-hosted","Windows","RAM-64GB"]\' >> "$GITHUB_OUTPUT"',
        "          else",
        '            echo \'labels=["self-hosted","Windows","RAM-64GB"]\' >> "$GITHUB_OUTPUT"',
        "          fi",
        "  unity:",
        "    needs: matrix-config",
        "    runs-on: ${{ fromJSON(needs.matrix-config.outputs.labels) }}",
        "    steps:",
        "      - name: diagnostics",
        "        shell: bash",
        "        run: echo hello"
      ],
      expectedViolations: 1
    },
    {
      // Finding #4: dynamic runs-on resolving to ubuntu-latest -> policy
      // does NOT apply, so shell:bash is fine.
      name: "passes dynamic runs-on resolving to ubuntu-latest with shell: bash",
      lines: [
        "jobs:",
        "  matrix-config:",
        "    runs-on: ubuntu-latest",
        "    outputs:",
        "      labels: ${{ steps.pick.outputs.labels }}",
        "    steps:",
        "      - name: pick",
        "        id: pick",
        "        run: |",
        '          echo \'labels=["ubuntu-latest"]\' >> "$GITHUB_OUTPUT"',
        "  linux:",
        "    needs: matrix-config",
        "    runs-on: ${{ fromJSON(needs.matrix-config.outputs.labels) }}",
        "    steps:",
        "      - name: diagnostics",
        "        shell: bash",
        "        run: echo hello"
      ],
      expectedViolations: 0
    },
    {
      // Finding #4: dynamic runs-on that can't be resolved (no labels=
      // emissions, or missing source job). Validator must emit a WARNING
      // so the maintainer is aware the rule cannot statically verify.
      name: "emits warning when dynamic runs-on cannot be statically resolved",
      lines: [
        "jobs:",
        "  unknown:",
        "    needs: missing-source",
        "    runs-on: ${{ fromJSON(needs.missing-source.outputs.labels) }}",
        "    steps:",
        "      - name: diagnostics",
        "        shell: bash",
        "        run: echo hello"
      ],
      expectedViolations: 1,
      expectedSeverity: "warning"
    }
  ])(
    "findForbidPlainShellBashOnSelfHostedWindowsViolations: $name",
    ({ lines, expectedViolations, expectedSeverity, expectedMessageMatch }) => {
      const violations = findForbidPlainShellBashOnSelfHostedWindowsViolations("test.yml", lines);
      expect(violations).toHaveLength(expectedViolations);
      const expectSeverity = expectedSeverity || "error";
      for (const v of violations) {
        expect(v.severity).toBe(expectSeverity);
        if (expectSeverity === "error") {
          expect(v.message).toMatch(/WSL stub|System32/);
        } else {
          expect(v.message).toMatch(/dynamic runs-on cannot be statically resolved/);
        }
        if (expectedMessageMatch) {
          // Round-3 (MINOR-B): observable source-attribution. Mutation-
          // testing the rule by removing the `workflowDefaultsShell`
          // resolution branch causes the workflow-defaults fixture to
          // either lose this annotation OR fall through to the "no
          // shell" branch (which carries a different sentence). Either
          // way, this assertion flips from pass to fail.
          expect(v.message).toMatch(expectedMessageMatch);
        }
      }
    }
  );

  test("real Unity workflow files pass the rule (regression guard)", () => {
    const workflowDir = path.resolve(__dirname, "..", "..", ".github", "workflows");
    const targets = ["unity-tests.yml", "unity-benchmarks.yml", "release.yml"];
    for (const file of targets) {
      const filePath = path.join(workflowDir, file);
      if (!fs.existsSync(filePath)) {
        continue;
      }
      const content = fs.readFileSync(filePath, "utf8").split(/\r?\n/);
      const violations = findForbidPlainShellBashOnSelfHostedWindowsViolations(file, content);
      expect(violations).toEqual([]);
    }
  });
});

describe("findSelfHostedWindowsCheckoutLongPathViolations", () => {
  test.each([
    {
      name: "flags checkout before Git long paths on self-hosted Windows",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: Checkout",
        "        uses: actions/checkout@v6",
        "        with:",
        "          persist-credentials: false"
      ],
      expectedViolations: 1
    },
    {
      name: "allows Git long paths before checkout",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: Enable Git long paths",
        "        shell: pwsh",
        "        env:",
        "          GIT_CONFIG_GLOBAL: ${{ runner.temp }}/dxm-gitconfig",
        "        run: git config --global core.longpaths true",
        "      - name: Checkout",
        "        uses: actions/checkout@v6",
        "        env:",
        "          GIT_CONFIG_GLOBAL: ${{ runner.temp }}/dxm-gitconfig",
        "        with:",
        "          persist-credentials: false"
      ],
      expectedViolations: 0
    },
    {
      name: "flags Git long paths after checkout because cleanup has already run",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: Checkout",
        "        uses: actions/checkout@v6",
        "        with:",
        "          persist-credentials: false",
        "      - name: Enable Git long paths",
        "        shell: pwsh",
        "        env:",
        "          GIT_CONFIG_GLOBAL: ${{ runner.temp }}/dxm-gitconfig",
        "        run: git config --global core.longpaths true"
      ],
      expectedViolations: 1
    },
    {
      name: "flags echoing the Git long paths command because it does not configure Git",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: Print intended command",
        "        shell: pwsh",
        "        env:",
        "          GIT_CONFIG_GLOBAL: ${{ runner.temp }}/dxm-gitconfig",
        "        run: Write-Host 'git config --global core.longpaths true'",
        "      - name: Checkout",
        "        uses: actions/checkout@v6",
        "        env:",
        "          GIT_CONFIG_GLOBAL: ${{ runner.temp }}/dxm-gitconfig",
        "        with:",
        "          persist-credentials: false"
      ],
      expectedViolations: 1
    },
    {
      name: "flags conditional Git long paths setup because checkout can still run",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: Enable Git long paths",
        "        if: false",
        "        shell: pwsh",
        "        env:",
        "          GIT_CONFIG_GLOBAL: ${{ runner.temp }}/dxm-gitconfig",
        "        run: git config --global core.longpaths true",
        "      - name: Checkout",
        "        uses: actions/checkout@v6",
        "        env:",
        "          GIT_CONFIG_GLOBAL: ${{ runner.temp }}/dxm-gitconfig",
        "        with:",
        "          persist-credentials: false"
      ],
      expectedViolations: 1
    },
    {
      name: "flags command hidden inside a conditional run block",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: Enable Git long paths",
        "        shell: pwsh",
        "        env:",
        "          GIT_CONFIG_GLOBAL: ${{ runner.temp }}/dxm-gitconfig",
        "        run: |",
        "          if ($false) {",
        "            git config --global core.longpaths true",
        "          }",
        "      - name: Checkout",
        "        uses: actions/checkout@v6",
        "        env:",
        "          GIT_CONFIG_GLOBAL: ${{ runner.temp }}/dxm-gitconfig",
        "        with:",
        "          persist-credentials: false"
      ],
      expectedViolations: 1
    },
    {
      name: "flags env text inside the script body because it is not top-level step env",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: Enable Git long paths",
        "        shell: pwsh",
        "        run: |",
        "          env:",
        "            GIT_CONFIG_GLOBAL: ${{ runner.temp }}/dxm-gitconfig",
        "          git config --global core.longpaths true",
        "      - name: Checkout",
        "        uses: actions/checkout@v6",
        "        env:",
        "          GIT_CONFIG_GLOBAL: ${{ runner.temp }}/dxm-gitconfig",
        "        with:",
        "          persist-credentials: false"
      ],
      expectedViolations: 1
    },
    {
      name: "flags checkout missing matching GIT_CONFIG_GLOBAL env",
      lines: [
        "jobs:",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: Enable Git long paths",
        "        shell: pwsh",
        "        env:",
        "          GIT_CONFIG_GLOBAL: ${{ runner.temp }}/dxm-gitconfig",
        "        run: git config --global core.longpaths true",
        "      - name: Checkout",
        "        uses: actions/checkout@v6",
        "        with:",
        "          persist-credentials: false"
      ],
      expectedViolations: 1
    },
    {
      name: "allows hosted Windows without the self-hosted checkout guard",
      lines: [
        "jobs:",
        "  hosted:",
        "    runs-on: windows-latest",
        "    steps:",
        "      - name: Checkout",
        "        uses: actions/checkout@v6",
        "        with:",
        "          persist-credentials: false"
      ],
      expectedViolations: 0
    },
    {
      name: "applies to dynamic runs-on resolving to self-hosted Windows",
      lines: [
        "jobs:",
        "  matrix-config:",
        "    runs-on: ubuntu-latest",
        "    outputs:",
        "      labels: ${{ steps.pick.outputs.labels }}",
        "    steps:",
        "      - name: Pick",
        "        id: pick",
        "        run: |",
        '          echo \'labels=["self-hosted","Windows","RAM-64GB"]\' >> "$GITHUB_OUTPUT"',
        "  unity:",
        "    needs: matrix-config",
        "    runs-on: ${{ fromJSON(needs.matrix-config.outputs.labels) }}",
        "    steps:",
        "      - name: Checkout",
        "        uses: actions/checkout@v6",
        "        with:",
        "          persist-credentials: false"
      ],
      expectedViolations: 1
    },
    {
      name: "warns when dynamic runs-on cannot be resolved",
      lines: [
        "jobs:",
        "  unity:",
        "    needs: matrix-config",
        "    runs-on: ${{ fromJSON(needs.matrix-config.outputs.labels) }}",
        "    steps:",
        "      - name: Checkout",
        "        uses: actions/checkout@v6",
        "        with:",
        "          persist-credentials: false"
      ],
      expectedViolations: 1,
      expectedSeverity: "warning"
    },
    {
      name: "does not warn for unresolved dynamic runs-on jobs without checkout",
      lines: [
        "jobs:",
        "  dynamic:",
        "    needs: matrix-config",
        "    runs-on: ${{ fromJSON(needs.matrix-config.outputs.labels) }}",
        "    steps:",
        "      - name: Diagnostics",
        "        shell: pwsh",
        "        run: Write-Output 'no checkout here'"
      ],
      expectedViolations: 0
    }
  ])("$name", ({ lines, expectedViolations, expectedSeverity }) => {
    const violations = findSelfHostedWindowsCheckoutLongPathViolations("test.yml", lines);
    expect(violations).toHaveLength(expectedViolations);
    for (const violation of violations) {
      expect(violation.severity).toBe(expectedSeverity || "error");
      if (violation.severity === "error") {
        expect(violation.message).toContain("core.longpaths true");
        expect(violation.message).toContain("Unity PackageCache");
      } else {
        expect(violation.message).toContain("dynamic runs-on cannot be statically resolved");
      }
    }
  });

  test("real self-hosted Windows checkout workflows enable Git long paths first", () => {
    const workflowDir = path.resolve(__dirname, "..", "..", ".github", "workflows");
    const targets = [
      "unity-tests.yml",
      "unity-benchmarks.yml",
      "perf-numbers.yml",
      "unity-gameci-experiment.yml",
      "runner-bootstrap.yml",
      "release.yml"
    ];
    for (const file of targets) {
      const filePath = path.join(workflowDir, file);
      const content = fs.readFileSync(filePath, "utf8").split(/\r?\n/);
      const violations = findSelfHostedWindowsCheckoutLongPathViolations(file, content);
      expect(violations).toEqual([]);
    }
  });
});
