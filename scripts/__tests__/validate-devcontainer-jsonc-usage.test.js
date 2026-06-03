/**
 * @fileoverview Tests for validate-devcontainer-jsonc-usage.js. The validator
 * blocks re-introduction of the brittle `grep ... \.json"` pattern in any
 * .devcontainer/*.sh script (which is, by VS Code convention, JSONC -- JSON
 * with comments). The earlier round of fixes replaced the live violation in
 * .devcontainer/validate-caching.sh; this validator prevents another
 * maintainer from re-introducing it.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");

const validator = require("../validate-devcontainer-jsonc-usage");

const FIXTURES = [
  {
    name: "grep pattern against .json is FORBIDDEN",
    content: 'grep -Eq \'"remoteUser":[[:space:]]*"vscode"\' devcontainer.json\n',
    expectedViolations: 1,
    expectedTool: "grep"
  },
  {
    name: "awk against .json is FORBIDDEN",
    content: "awk '/remoteUser/ {print}' devcontainer.json\n",
    expectedViolations: 1,
    expectedTool: "awk"
  },
  {
    name: "sed against .json is FORBIDDEN",
    content: 'sed -n \'s/.*remoteUser":\\s*"\\([^"]*\\)".*/\\1/p\' devcontainer.json\n',
    expectedViolations: 1,
    expectedTool: "sed"
  },
  {
    name: "cut directly against .json is FORBIDDEN",
    content: "cut -d'\"' -f4 devcontainer.json\n",
    expectedViolations: 1,
    expectedTool: "cut"
  },
  {
    name: "line piped through parse_devcontainer_mounts is OK",
    content: "parse_devcontainer_mounts devcontainer.json /workspaces/foo | head -n1\n",
    expectedViolations: 0
  },
  {
    name: "line piped through get_devcontainer_property is OK",
    content: "get_devcontainer_property devcontainer.json remoteUser\n",
    expectedViolations: 0
  },
  {
    name: "line piped through strip_jsonc_comments is OK",
    content: "strip_jsonc_comments devcontainer.json | jq -r .remoteUser\n",
    expectedViolations: 0
  },
  {
    name: "grep against non-JSON is OK",
    content: "grep -q 'KEY=value' .env\n",
    expectedViolations: 0
  },
  {
    name: "grep with explicit override marker is OK",
    content:
      "grep -q 'this is fine' devcontainer.json  # devcontainer-jsonc-ok: lines containing 'fine' are scanned as raw text\n",
    expectedViolations: 0
  },
  {
    name: "shell comment line mentioning grep ... .json is OK",
    content: "# Replaces the legacy grep against devcontainer.json.\n",
    expectedViolations: 0
  },
  {
    name: "multiple violations in one file each surface",
    content: "grep \"remoteUser\" devcontainer.json\nawk '/foo/' devcontainer.json\n",
    expectedViolations: 2
  }
];

describe("validate-devcontainer-jsonc-usage scanContent", () => {
  test.each(FIXTURES)("$name", ({ content, expectedViolations, expectedTool }) => {
    const fakePath = path.join(validator.DEVCONTAINER_ROOT, "synthetic-fixture.sh");
    const violations = validator.scanContent(fakePath, content);
    expect(violations).toHaveLength(expectedViolations);
    if (expectedTool && violations.length > 0) {
      expect(violations[0].tool).toBe(expectedTool);
    }
    for (const v of violations) {
      expect(v.reason).toMatch(
        /parse_devcontainer_mounts|get_devcontainer_property|strip_jsonc_comments/
      );
    }
  });
});

describe("validate-devcontainer-jsonc-usage real .devcontainer tree", () => {
  test("the live .devcontainer/*.sh tree has zero violations", () => {
    const files = validator.listDevcontainerShellFiles();
    expect(files.length).toBeGreaterThan(0);
    const violations = [];
    for (const file of files) {
      violations.push(...validator.scanFile(file));
    }
    if (violations.length > 0) {
      const lines = violations.map(
        (v) => `${path.relative(validator.REPO_ROOT, v.file)}:${v.line} -> ${v.tool}`
      );
      throw new Error(
        `Found ${violations.length} grep/awk/sed-against-.json violation(s):\n${lines.join("\n")}`
      );
    }
    expect(violations).toHaveLength(0);
  });
});

describe("validate-devcontainer-jsonc-usage main()", () => {
  test("main returns 0 when no scripts have violations (current tree)", () => {
    const writeOutSpy = jest.spyOn(process.stdout, "write").mockImplementation(() => true);
    const writeErrSpy = jest.spyOn(process.stderr, "write").mockImplementation(() => true);
    try {
      const code = validator.main();
      expect(code).toBe(0);
    } finally {
      writeOutSpy.mockRestore();
      writeErrSpy.mockRestore();
    }
  });
});
