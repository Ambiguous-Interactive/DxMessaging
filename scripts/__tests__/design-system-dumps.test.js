"use strict";

const { execFileSync } = require("node:child_process");
const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const COMPLETE_BORDER_HELPER_PATH = "Editor/DxMessagingEditorTheme.cs";
const THIS_TEST_PATH = "scripts/__tests__/design-system-dumps.test.js";

test("design-system source dumps are not tracked", () => {
  const output = execFileSync("git", ["ls-files", "design-system*"], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });
  const trackedDumps = output.split("\n").filter(Boolean);

  assert.deepEqual(
    trackedDumps,
    [],
    "Keep design-system source dumps ignored and untracked; migrate canonical assets into Editor/Theme, Editor/Icons, or docs/stylesheets."
  );
});

test("editor design system avoids left-only borders", () => {
  const output = execFileSync(
    "git",
    ["ls-files", "--cached", "--others", "--exclude-standard", "--", "Editor", "docs/stylesheets"],
    {
      cwd: REPO_ROOT,
      encoding: "utf8"
    }
  );
  const violations = [];

  for (const relativePath of output.split("\n").filter(Boolean)) {
    if (!/\.(cs|uss|css)$/.test(relativePath)) {
      continue;
    }

    const content = fs.readFileSync(path.join(REPO_ROOT, relativePath), "utf8");
    if (/\bborder-left\b/i.test(content)) {
      violations.push(`${relativePath}: use complete borders instead of border-left`);
    }
    if (
      relativePath !== COMPLETE_BORDER_HELPER_PATH &&
      /\bborderLeft(?:Width|Color)\b/.test(content)
    ) {
      violations.push(
        `${relativePath}: use DxMessagingEditorTheme.ApplyCompleteBorder for semantic borders`
      );
    }
  }

  assert.deepEqual(violations, []);
});

test("editor-window screenshot automation does not use blocked capture primitives", () => {
  const output = execFileSync(
    "git",
    ["ls-files", "--cached", "--others", "--exclude-standard", "--", "Editor", "Tests", "scripts", ".github"],
    {
      cwd: REPO_ROOT,
      encoding: "utf8"
    }
  );
  const violations = [];
  const blockedPatterns = [
    ["ReadScreenPixel", /\bReadScreenPixel\b/],
    ["SwitchSkinAndRepaintAllViews", /\bSwitchSkinAndRepaintAllViews\b/]
  ];

  for (const relativePath of output.split("\n").filter(Boolean)) {
    if (relativePath === THIS_TEST_PATH) {
      continue;
    }

    const absolutePath = path.join(REPO_ROOT, relativePath);
    if (!fs.statSync(absolutePath).isFile()) {
      continue;
    }

    const content = fs.readFileSync(absolutePath, "utf8");
    for (const [name, pattern] of blockedPatterns) {
      if (pattern.test(content)) {
        violations.push(
          `${relativePath}: ${name} is blocked for editor-window screenshot automation`
        );
      }
    }
  }

  assert.deepEqual(violations, []);
});
