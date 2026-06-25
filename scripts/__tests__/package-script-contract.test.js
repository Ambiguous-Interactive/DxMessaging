"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { walkFiles } = require("../lib/repo-files.js");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const PACKAGE_JSON = path.join(REPO_ROOT, "package.json");
const NPM_RUN_RE = /\bnpm\s+run(?:-script)?\s+([A-Za-z0-9:_-]+)\b/g;

const SCAN_ROOTS = [
  ".github/workflows",
  ".github/actions",
  ".github/ISSUE_TEMPLATE",
  "docs",
  "scripts",
  "CHANGELOG.md",
  "README.md",
  "CONTRIBUTING.md",
  ".llm"
];

function readPackageScripts() {
  return JSON.parse(fs.readFileSync(PACKAGE_JSON, "utf8")).scripts || {};
}

function walkTextFiles(relativePath) {
  const absolutePath = path.join(REPO_ROOT, relativePath);
  if (!fs.existsSync(absolutePath)) {
    return [];
  }
  if (fs.statSync(absolutePath).isFile()) {
    return /\.(md|markdown|ya?ml|js|json)$/i.test(relativePath) ? [relativePath] : [];
  }
  return walkFiles(absolutePath, {
    match: (file) => /\.(md|markdown|ya?ml|js|json)$/i.test(file)
  }).map((file) => path.relative(REPO_ROOT, file));
}

function npmRunReferences() {
  const references = [];
  for (const file of SCAN_ROOTS.flatMap(walkTextFiles)) {
    const text = fs.readFileSync(path.join(REPO_ROOT, file), "utf8");
    for (const match of text.matchAll(NPM_RUN_RE)) {
      references.push({ file, script: match[1] });
    }
  }
  return references;
}

test("documented and workflow package commands are defined in package.json", () => {
  const scripts = readPackageScripts();
  const missing = npmRunReferences()
    .filter(({ script }) => !Object.hasOwn(scripts, script))
    .map(({ file, script }) => `${file}: npm run ${script}`);

  assert.deepEqual(missing, []);
});

test("validate:all includes the documented issue-template version gate", () => {
  const validateAll = readPackageScripts()["validate:all"] || "";

  assert.match(validateAll, /\bnpm run check:issue-template-versions\b/);
});
