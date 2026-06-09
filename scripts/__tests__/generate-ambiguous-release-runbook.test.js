"use strict";

const fs = require("fs");
const path = require("path");
const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");

const {
  OUTPUT_RELATIVE_PATH,
  TRACKED_RUNBOOK_SOURCES,
  generateRunbook,
  generateRunbookContent
} = require("../generate-ambiguous-release-runbook");

const ROOT_DIR = path.resolve(__dirname, "../..");

function readRepoFile(relativePath) {
  return fs.readFileSync(path.join(ROOT_DIR, relativePath), "utf8");
}

describe("generate-ambiguous-release-runbook", () => {
  test("generates the local runbook at the expected ignored path", () => {
    const tempRoot = makeTempDir("dx-runbook");
    try {
      const outputPath = generateRunbook({ rootDir: tempRoot });

      expect(outputPath).toBe(path.join(tempRoot, OUTPUT_RELATIVE_PATH));
      expect(fs.readFileSync(outputPath, "utf8")).toBe(generateRunbookContent());
    } finally {
      cleanupDir(tempRoot);
    }
  });

  test("does not clobber an existing local runbook by default", () => {
    const tempRoot = makeTempDir("dx-runbook");
    const outputPath = path.join(tempRoot, OUTPUT_RELATIVE_PATH);
    const existingContent = "local operator notes\n";

    try {
      fs.mkdirSync(path.dirname(outputPath), { recursive: true });
      fs.writeFileSync(outputPath, existingContent, "utf8");

      expect(() => generateRunbook({ rootDir: tempRoot })).toThrow(/--force/);
      expect(fs.readFileSync(outputPath, "utf8")).toBe(existingContent);
    } finally {
      cleanupDir(tempRoot);
    }
  });

  test("does not clobber if the runbook appears during a non-force write", () => {
    const tempRoot = makeTempDir("dx-runbook");
    const outputPath = path.join(tempRoot, OUTPUT_RELATIVE_PATH);
    const existingContent = "created by another process\n";
    const writeFileSyncSpy = jest.spyOn(fs, "writeFileSync");

    try {
      writeFileSyncSpy.mockImplementationOnce((targetPath, content, options) => {
        fs.mkdirSync(path.dirname(targetPath), { recursive: true });
        fs.writeFileSync(targetPath, existingContent, "utf8");

        const error = new Error("file already exists");
        error.code = "EEXIST";
        throw error;
      });

      expect(() => generateRunbook({ rootDir: tempRoot })).toThrow(/--force/);
      expect(fs.readFileSync(outputPath, "utf8")).toBe(existingContent);
      expect(writeFileSyncSpy).toHaveBeenCalledWith(
        outputPath,
        generateRunbookContent(),
        expect.objectContaining({ flag: "wx" })
      );
    } finally {
      writeFileSyncSpy.mockRestore();
      cleanupDir(tempRoot);
    }
  });

  test("overwrites an existing local runbook when force is explicit", () => {
    const tempRoot = makeTempDir("dx-runbook");
    const outputPath = path.join(tempRoot, OUTPUT_RELATIVE_PATH);

    try {
      fs.mkdirSync(path.dirname(outputPath), { recursive: true });
      fs.writeFileSync(outputPath, "local operator notes\n", "utf8");

      expect(generateRunbook({ rootDir: tempRoot, force: true })).toBe(outputPath);
      expect(fs.readFileSync(outputPath, "utf8")).toBe(generateRunbookContent());
    } finally {
      cleanupDir(tempRoot);
    }
  });

  test("local runbook points to and includes tracked operator docs", () => {
    const content = generateRunbookContent();

    expect(TRACKED_RUNBOOK_SOURCES).toEqual([
      {
        title: "Ambiguous release migration operator guide",
        relativePath: "docs/ops/ambiguous-release-migration.md"
      },
      {
        title: "Release operations",
        relativePath: "docs/ops/release-operations.md"
      },
      {
        title: "GitHub transfer",
        relativePath: "docs/ops/github-transfer.md"
      },
      {
        title: "CI and GitHub settings",
        relativePath: "docs/ops/ci-and-github-settings.md"
      },
      {
        title: "npm release publishing",
        relativePath: "docs/ops/npm-release-publishing.md"
      },
      {
        title: "OpenUPM metadata",
        relativePath: "docs/ops/openupm-metadata.md"
      },
      {
        title: "Unity Asset Store UPM",
        relativePath: "docs/ops/unity-asset-store-upm.md"
      },
      {
        title: "Post-transfer verification",
        relativePath: "docs/ops/post-transfer-verification.md"
      }
    ]);
    expect(content).toContain("Tracked source documents to follow:");
    expect(content).toContain("`docs/ops/ambiguous-release-migration.md`");
    expect(content).toContain("`docs/ops/release-operations.md`");
    expect(content).toContain("`docs/ops/unity-asset-store-upm.md`");
    expect(content).toContain("## Public Verification Notes");
    expect(content).toContain("## Public Follow-Up Links");
    expect(content).toContain("## Non-Sensitive Next Actions");
    expect(content).not.toContain("## Included:");
    expect(content).not.toContain("## Ambiguous Release Migration Operator Guide");
  });

  test("tracked operator docs cover every required migration area", () => {
    const content = [
      readRepoFile("docs/ops/ambiguous-release-migration.md"),
      readRepoFile("docs/ops/release-operations.md"),
      readRepoFile("docs/ops/github-transfer.md"),
      readRepoFile("docs/ops/ci-and-github-settings.md"),
      readRepoFile("docs/ops/npm-release-publishing.md"),
      readRepoFile("docs/ops/openupm-metadata.md"),
      readRepoFile("docs/ops/unity-asset-store-upm.md"),
      readRepoFile("docs/ops/post-transfer-verification.md")
    ].join("\n");
    const requiredPhrases = [
      "## GitHub Repository Transfer",
      "## Self-Hosted Unity Runner Setup",
      "## GitHub Environments, Secrets, and Protections",
      "### Branches and Tags",
      "## npm Ownership, Trusted Publishing, and Provenance",
      "## Semver Tag Release Flow",
      "## OpenUPM Metadata Update",
      "## Unity Asset Store UPM Onboarding",
      "## Post-Transfer Verification",
      "tag protection",
      "Dependabot"
    ];

    for (const phrase of requiredPhrases) {
      expect(content).toContain(phrase);
    }
  });

  test("tracked operator docs preserve exact public release identifiers", () => {
    const content = [
      readRepoFile("docs/ops/ambiguous-release-migration.md"),
      readRepoFile("docs/ops/release-operations.md"),
      readRepoFile("docs/ops/ci-and-github-settings.md"),
      readRepoFile("docs/ops/npm-release-publishing.md")
    ].join("\n");
    const requiredIdentifiers = [
      "Ambiguous-Interactive/DxMessaging",
      "https://github.com/Ambiguous-Interactive/DxMessaging",
      "https://ambiguous-interactive.github.io/DxMessaging/",
      "com.wallstop-studios.dxmessaging",
      ".github/workflows/release.yml",
      ".github/workflows/deploy-docs.yml",
      ".github/workflows/validate-npm-meta.yml",
      ".github/workflows/unity-tests.yml",
      ".github/workflows/unity-benchmarks.yml",
      "RAM-64GB",
      "github-pages",
      "vX.Y.Z"
    ];

    for (const identifier of requiredIdentifiers) {
      expect(content).toContain(identifier);
    }
  });

  test("tracked and generated docs forbid sensitive local operator material", () => {
    const content = [
      generateRunbookContent(),
      readRepoFile("docs/ops/ambiguous-release-migration.md"),
      readRepoFile("docs/ops/ci-and-github-settings.md"),
      readRepoFile("docs/ops/github-transfer.md"),
      readRepoFile("docs/ops/npm-release-publishing.md"),
      readRepoFile("docs/ops/openupm-metadata.md"),
      readRepoFile("docs/ops/post-transfer-verification.md"),
      readRepoFile("docs/ops/release-operations.md"),
      readRepoFile("docs/ops/unity-asset-store-upm.md")
    ].join("\n");

    expect(content).toContain("Do not paste secrets, account screenshots");
    expect(content).toContain("tracked files or this local runbook");
    expect(content).toContain("Keep only non-sensitive verification notes");
    expect(content).toContain("Do not commit secrets");
    expect(content).not.toMatch(/keep maintainer account details/i);
    expect(content).not.toMatch(/private notes/i);
    expect(content).not.toMatch(/private npm account notes in the local ignored runbook/i);
    expect(content).not.toMatch(/account notes.*local ignored runbook/i);
    expect(content).not.toMatch(/private status.*local ignored runbook/i);
    expect(content).not.toMatch(/record secret existence.*local ignored runbook/i);
    expect(content).not.toMatch(/last rotated.*local ignored runbook/i);
    expect(content).not.toMatch(/approval status.*local ignored runbook/i);
    expect(content).not.toMatch(/approval state.*local ignored runbook/i);
    expect(content).not.toMatch(/review state.*tracked follow-up/i);
    expect(content).not.toMatch(/release draft url/i);
    expect(content).not.toMatch(/open release draft/i);
    expect(content).not.toMatch(/secret availability/i);
    expect(content).not.toMatch(/pass\/fail state/i);
    expect(content).not.toMatch(/ghp_[A-Za-z0-9_]+/);
    expect(content).not.toMatch(/npm_[A-Za-z0-9_]+/);
    expect(content).not.toMatch(/publisher id:\s*\S+/i);
    expect(content).not.toMatch(/local status:\s*\S+/i);
  });

  test("generated runbook directory is gitignored with one-line rationale", () => {
    const gitignore = readRepoFile(".gitignore");

    expect(gitignore).toContain(
      "# Local operator runbooks may contain environment-specific execution notes.\n.operator-runbooks/"
    );
  });

  test("generated runbook directory is excluded from npm packages", () => {
    const npmignore = readRepoFile(".npmignore");
    const packageJson = JSON.parse(readRepoFile("package.json"));

    expect(npmignore).toContain(".operator-runbooks/");
    expect(packageJson.files).not.toContain(".operator-runbooks/**");
  });
});
