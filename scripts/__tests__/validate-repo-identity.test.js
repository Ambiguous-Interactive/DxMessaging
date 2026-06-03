/**
 * @fileoverview Tests for validate-repo-identity.js.
 */

"use strict";

const {
  EXPECTED_REPOSITORY,
  findStaleIdentityReferencesInContent,
  fixStaleIdentityReferences,
  fixStaleIdentityReferencesInContent,
  getRepositoryCandidateFiles,
  parseGitFileList,
  validateRepoIdentity
} = require("../validate-repo-identity.js");

const ALLOWED_PACKAGE_ID = "com.wallstop-studios.dxmessaging";
const STALE_REPOSITORY = ["wallstop", "DxMessaging"].join("/");
const STALE_REPOSITORY_URL = `https://github.com/${STALE_REPOSITORY}`;
const STALE_DOCS_URL = ["https://wallstop.github.io", "DxMessaging"].join("/");
const STALE_PACKAGE_REPOSITORY = ["wallstop-studios", "com.wallstop-studios.dxmessaging"].join("/");

const CANONICAL_REPOSITORY_URL = `https://github.com/${EXPECTED_REPOSITORY}`;
const CANONICAL_DOCS_URL = "https://ambiguous-interactive.github.io/DxMessaging/";
const CANONICAL_GUARD = `github.repository == '${EXPECTED_REPOSITORY}'`;

describe("validate-repo-identity", () => {
  afterEach(() => {
    jest.restoreAllMocks();
  });

  test("detects stale wallstop GitHub URLs", () => {
    const errors = findStaleIdentityReferencesInContent(
      [
        `repository: ${STALE_REPOSITORY_URL}`,
        `changelog: ${STALE_REPOSITORY_URL}/blob/master/CHANGELOG.md`
      ].join("\n"),
      "README.md"
    );

    expect(errors).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          file: "README.md",
          line: 1,
          value: STALE_REPOSITORY_URL
        }),
        expect.objectContaining({
          file: "README.md",
          line: 2,
          value: `${STALE_REPOSITORY_URL}/blob/master/CHANGELOG.md`
        })
      ])
    );
  });

  test("detects stale GitHub Pages URLs", () => {
    const errors = findStaleIdentityReferencesInContent(
      `docs: ${STALE_DOCS_URL}/getting-started/install/`,
      "docs/index.md"
    );

    expect(errors).toEqual([
      expect.objectContaining({
        file: "docs/index.md",
        line: 1,
        value: `${STALE_DOCS_URL}/getting-started/install/`
      })
    ]);
  });

  test("detects stale repository slugs and old release-drafter guards", () => {
    const errors = findStaleIdentityReferencesInContent(
      [
        `repo: ${STALE_REPOSITORY}`,
        `mirror: ${STALE_PACKAGE_REPOSITORY}`,
        `if: github.repository == '${STALE_REPOSITORY}'`
      ].join("\n"),
      ".github/workflows/release-drafter.yml"
    );

    expect(errors).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          line: 1,
          value: STALE_REPOSITORY
        }),
        expect.objectContaining({
          line: 2,
          value: STALE_PACKAGE_REPOSITORY
        }),
        expect.objectContaining({
          line: 3,
          value: `github.repository == '${STALE_REPOSITORY}'`
        })
      ])
    );
  });

  test("detects stale Dependabot owner routing", () => {
    const errors = findStaleIdentityReferencesInContent(
      ["assignees:", "  - wallstop", "reviewers:", "  - wallstop"].join("\n"),
      ".github/dependabot.yml"
    );

    expect(errors).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          type: "stale-dependabot-routing",
          line: 2,
          value: "- wallstop"
        }),
        expect.objectContaining({
          type: "stale-dependabot-routing",
          line: 4,
          value: "- wallstop"
        })
      ])
    );
  });

  test("allows current repository identity and Unity package id", () => {
    const errors = findStaleIdentityReferencesInContent(
      [
        `repo: ${EXPECTED_REPOSITORY}`,
        `package: ${ALLOWED_PACKAGE_ID}`,
        `openupm add ${ALLOWED_PACKAGE_ID}`,
        `https://openupm.com/packages/${ALLOWED_PACKAGE_ID}/`,
        `if: github.repository == '${EXPECTED_REPOSITORY}'`
      ].join("\n"),
      "package.json"
    );

    expect(errors).toHaveLength(0);
  });

  test("does not flag the bare Unity package id as repository identity", () => {
    // Pattern 4 only matches the 'wallstop-studios/<id>' slug, never the bare id,
    // so the bare package id must produce zero findings on its own. This pins the
    // precondition for removing the former ALLOWED_PACKAGE_ID guard branch.
    const lines = [
      `"name": "${ALLOWED_PACKAGE_ID}"`,
      ALLOWED_PACKAGE_ID,
      `  ${ALLOWED_PACKAGE_ID}  `,
      `displayName: ${ALLOWED_PACKAGE_ID}`
    ];

    for (const line of lines) {
      expect(findStaleIdentityReferencesInContent(line, "package.json")).toHaveLength(0);
    }
  });

  test("validateRepoIdentity returns invalid with stale references", () => {
    jest.spyOn(console, "error").mockImplementation(() => {});

    const result = validateRepoIdentity({
      files: ["README.md"],
      readFileSync: () => STALE_REPOSITORY_URL
    });

    expect(result.valid).toBe(false);
    expect(result.errors).toHaveLength(1);
  });

  test("parseGitFileList normalizes git output", () => {
    expect(parseGitFileList("a\r\nb\n\n")).toEqual(["a", "b"]);
  });

  test("candidate files include tracked, staged, and untracked files", () => {
    const execFileSync = jest
      .fn()
      .mockReturnValueOnce("tracked.md\nshared.md\n")
      .mockReturnValueOnce("staged.yml\nshared.md\n")
      .mockReturnValueOnce("untracked.js\n");

    const files = getRepositoryCandidateFiles(execFileSync);

    expect(files).toEqual(["shared.md", "staged.yml", "tracked.md", "untracked.js"]);
    expect(execFileSync).toHaveBeenCalledWith(
      "git",
      ["ls-files", "--others", "--exclude-standard"],
      expect.any(Object)
    );
  });

  test("getRepositoryCandidateFiles delegates tracked enumeration to getTrackedFiles", () => {
    // Delegation contract: the tracked portion is served by exactly one bare
    // 'git ls-files' call (getTrackedFiles), so the candidate enumerator issues
    // exactly three git invocations and never re-invokes git for tracked files.
    const execFileSync = jest
      .fn()
      .mockReturnValueOnce("tracked.md\n")
      .mockReturnValueOnce("staged.yml\n")
      .mockReturnValueOnce("untracked.js\n");

    getRepositoryCandidateFiles(execFileSync);

    expect(execFileSync).toHaveBeenCalledTimes(3);
    // First call is the bare ls-files from getTrackedFiles, not a re-invocation.
    expect(execFileSync.mock.calls[0][0]).toBe("git");
    expect(execFileSync.mock.calls[0][1]).toEqual(["ls-files"]);
    // Exactly one bare ls-files invocation total (no duplicate tracked query).
    const bareLsFilesCalls = execFileSync.mock.calls.filter(
      (call) => call[1].length === 1 && call[1][0] === "ls-files"
    );
    expect(bareLsFilesCalls).toHaveLength(1);
  });

  describe("fix mode", () => {
    const fixCases = [
      {
        name: "stale GitHub URL",
        stale: STALE_REPOSITORY_URL,
        canonical: CANONICAL_REPOSITORY_URL
      },
      {
        name: "stale GitHub URL with path",
        stale: `${STALE_REPOSITORY_URL}/blob/master/CHANGELOG.md`,
        canonical: CANONICAL_REPOSITORY_URL
      },
      {
        name: "stale documentation URL",
        stale: `${STALE_DOCS_URL}/getting-started/install/`,
        canonical: CANONICAL_DOCS_URL
      },
      {
        name: "stale github.repository guard (==)",
        stale: `if: github.repository == '${STALE_REPOSITORY}'`,
        canonical: `if: ${CANONICAL_GUARD}`
      },
      {
        name: "stale github.repository guard (!=, package slug)",
        stale: `if: github.repository != '${STALE_PACKAGE_REPOSITORY}'`,
        canonical: `if: ${CANONICAL_GUARD}`
      },
      {
        name: "stale package repository slug",
        stale: `mirror: ${STALE_PACKAGE_REPOSITORY}`,
        canonical: `mirror: ${EXPECTED_REPOSITORY}`
      },
      {
        name: "stale repository slug",
        stale: `repo: ${STALE_REPOSITORY}`,
        canonical: `repo: ${EXPECTED_REPOSITORY}`
      }
    ];

    test.each(fixCases)(
      "rewrites $name to canonical form, is idempotent, and leaves no residual stale identity",
      ({ stale, canonical }) => {
        const first = fixStaleIdentityReferencesInContent(stale);
        expect(first.changed).toBe(true);
        expect(first.content).toBe(canonical);

        const second = fixStaleIdentityReferencesInContent(first.content);
        expect(second.changed).toBe(false);
        expect(second.content).toBe(first.content);

        const residual = findStaleIdentityReferencesInContent(first.content, "x.yml").filter(
          (error) => error.type === "stale-repository-identity"
        );
        expect(residual).toHaveLength(0);
      }
    );

    test("does not report a change when content is already canonical", () => {
      const canonical = [
        `repo: ${EXPECTED_REPOSITORY}`,
        `url: ${CANONICAL_REPOSITORY_URL}`,
        `docs: ${CANONICAL_DOCS_URL}`,
        `package: ${ALLOWED_PACKAGE_ID}`
      ].join("\n");

      const result = fixStaleIdentityReferencesInContent(canonical);

      expect(result.changed).toBe(false);
      expect(result.content).toBe(canonical);
    });

    test("preserves CRLF line endings when rewriting", () => {
      const crlf = `a\r\n${STALE_REPOSITORY_URL}\r\nb\r\n`;
      const result = fixStaleIdentityReferencesInContent(crlf);

      expect(result.changed).toBe(true);
      expect(result.content).toBe(`a\r\n${CANONICAL_REPOSITORY_URL}\r\nb\r\n`);
      // Idempotent under CRLF: a second pass is byte-identical.
      expect(fixStaleIdentityReferencesInContent(result.content).content).toBe(result.content);
    });

    test("preserves LF line endings when rewriting", () => {
      const lf = `a\n${STALE_REPOSITORY_URL}\nb\n`;
      const result = fixStaleIdentityReferencesInContent(lf);

      expect(result.changed).toBe(true);
      expect(result.content).toBe(`a\n${CANONICAL_REPOSITORY_URL}\nb\n`);
    });

    test("fixStaleIdentityReferences writes only files that actually change", () => {
      const files = {
        "stale.md": `link: ${STALE_REPOSITORY_URL}`,
        "clean.md": `link: ${CANONICAL_REPOSITORY_URL}`
      };
      const writes = {};
      const writeFileSync = jest.fn((absolutePath, content) => {
        writes[absolutePath] = content;
      });
      const readFileSync = jest.fn((absolutePath) => {
        const key = Object.keys(files).find((name) => absolutePath.endsWith(name));
        return files[key];
      });

      const result = fixStaleIdentityReferences(["stale.md", "clean.md"], {
        readFileSync,
        writeFileSync
      });

      expect(result.changedFiles).toEqual(["stale.md"]);
      expect(result.errors).toEqual([]);
      // clean.md must not be written (guarded on real change).
      expect(writeFileSync).toHaveBeenCalledTimes(1);
      const writtenForStale = Object.entries(writes).find(([key]) => key.endsWith("stale.md"));
      expect(writtenForStale[1]).toBe(`link: ${CANONICAL_REPOSITORY_URL}`);
    });

    test("fixStaleIdentityReferences is idempotent across files (second run writes nothing)", () => {
      let content = `link: ${STALE_REPOSITORY_URL}`;
      const readFileSync = jest.fn(() => content);
      const writeFileSync = jest.fn((absolutePath, next) => {
        content = next;
      });

      const firstRun = fixStaleIdentityReferences(["docs.md"], { readFileSync, writeFileSync });
      expect(firstRun.changedFiles).toEqual(["docs.md"]);
      expect(writeFileSync).toHaveBeenCalledTimes(1);

      const secondRun = fixStaleIdentityReferences(["docs.md"], { readFileSync, writeFileSync });
      expect(secondRun.changedFiles).toEqual([]);
      // No additional write on the second pass.
      expect(writeFileSync).toHaveBeenCalledTimes(1);
      expect(content).toBe(`link: ${CANONICAL_REPOSITORY_URL}`);
    });

    test("fix mode leaves dependabot routing as a reported, non-fixable error", () => {
      const dependabot = ["reviewers:", "  - wallstop"].join("\n");
      const readFileSync = jest.fn(() => dependabot);
      const writeFileSync = jest.fn();

      const result = fixStaleIdentityReferences([".github/dependabot.yml"], {
        readFileSync,
        writeFileSync
      });

      // No canonical replacement exists, so nothing is rewritten...
      expect(result.changedFiles).toEqual([]);
      expect(writeFileSync).not.toHaveBeenCalled();
      // ...but the routing remains a reported error.
      expect(result.errors).toEqual([
        expect.objectContaining({
          type: "stale-dependabot-routing",
          line: 2,
          value: "- wallstop"
        })
      ]);
    });

    test("validateRepoIdentity({ fix: true }) rewrites and reports changed files, exits valid", () => {
      jest.spyOn(console, "log").mockImplementation(() => {});
      let content = `link: ${STALE_REPOSITORY_URL}`;
      const writeFileSync = jest.fn((absolutePath, next) => {
        content = next;
      });

      const result = validateRepoIdentity({
        files: ["README.md"],
        fix: true,
        readFileSync: () => content,
        writeFileSync
      });

      expect(result.valid).toBe(true);
      expect(result.changedFiles).toEqual(["README.md"]);
      expect(content).toBe(`link: ${CANONICAL_REPOSITORY_URL}`);
    });

    test("validateRepoIdentity({ fix: true }) never mutates files that validation flags but fix leaves stale", () => {
      jest.spyOn(console, "error").mockImplementation(() => {});
      jest.spyOn(console, "log").mockImplementation(() => {});
      const writeFileSync = jest.fn();

      const result = validateRepoIdentity({
        files: [".github/dependabot.yml"],
        fix: true,
        readFileSync: () => ["reviewers:", "  - wallstop"].join("\n"),
        writeFileSync
      });

      expect(result.valid).toBe(false);
      expect(result.changedFiles).toEqual([]);
      expect(writeFileSync).not.toHaveBeenCalled();
      expect(result.errors).toEqual([
        expect.objectContaining({ type: "stale-dependabot-routing" })
      ]);
    });

    test("check (validate-only) mode never invokes writeFileSync", () => {
      jest.spyOn(console, "error").mockImplementation(() => {});
      const writeFileSync = jest.fn();

      const result = validateRepoIdentity({
        files: ["README.md"],
        readFileSync: () => STALE_REPOSITORY_URL,
        writeFileSync
      });

      expect(result.valid).toBe(false);
      expect(writeFileSync).not.toHaveBeenCalled();
    });
  });
});
