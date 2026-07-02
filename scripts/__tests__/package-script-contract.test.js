const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { walkFiles } = require("../lib/repo-files.js");
const REPO_ROOT = path.resolve(__dirname, "..", "..");
const PACKAGE = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, "package.json"), "utf8"));
const LOCAL_IMAGE_RE =
  /!\[[^\]]*]\((?!<?[a-z][\w+.-]*:|#)([^)\s]+)[^)]*\)|<img\b[^>]*\bsrc=["']?(?![a-z][\w+.-]*:|#)([^"'\s>]+)/gi;
const NPM_RUN_RE = /\bnpm\s+run(?:-script)?\s+([A-Za-z0-9:_-]+)\b/g;
const SCAN_ROOTS =
  ".github/workflows .github/actions .github/ISSUE_TEMPLATE docs scripts CHANGELOG.md README.md CONTRIBUTING.md .llm".split(
    " "
  );
const TEXT_FILE_RE = /\.(md|markdown|ya?ml|js|json)$/i;
function walkTextFiles(file) {
  const abs = path.join(REPO_ROOT, file);
  if (!fs.existsSync(abs)) return [];
  if (fs.statSync(abs).isFile()) return TEXT_FILE_RE.test(file) ? [file] : [];
  return walkFiles(abs, { match: (file) => TEXT_FILE_RE.test(file) }).map((file) =>
    path.relative(REPO_ROOT, file)
  );
}
function localMarkdownImages(file) {
  return [...fs.readFileSync(path.join(REPO_ROOT, file), "utf8").matchAll(LOCAL_IMAGE_RE)].map(
    (match) =>
      path.posix.normalize(
        path.posix.join(
          path.posix.dirname(file),
          decodeURIComponent((match[1] ?? match[2]).replace(/^<|>$/g, ""))
        )
      )
  );
}
function isPackaged(set, p) {
  return set.has(p) || [...set].some((f) => f.endsWith("/**") && p.startsWith(f.slice(0, -2)));
}
function npmRunReferences() {
  return SCAN_ROOTS.flatMap(walkTextFiles).flatMap((file) =>
    [...fs.readFileSync(path.join(REPO_ROOT, file), "utf8").matchAll(NPM_RUN_RE)].map((match) => ({
      file,
      script: match[1]
    }))
  );
}
test("documented package commands are defined and validate:all keeps required gates", () => {
  const scripts = PACKAGE.scripts || {};
  const missing = npmRunReferences()
    .filter(({ script }) => !Object.hasOwn(scripts, script))
    .map(({ file, script }) => `${file}: npm run ${script}`);
  assert.deepEqual(missing, []);
  assert.match(scripts["validate:all"] || "", /\bnpm run check:issue-template-versions\b/);
});
test("package-shipped markdown local images are included in package files", () => {
  const packageFiles = new Set(PACKAGE.files || []);
  assert.ok(localMarkdownImages("README.md").includes("docs/images/DxMessaging-banner.svg"));
  const missing = [...packageFiles]
    .flatMap((f) => (f.endsWith("/**") ? walkTextFiles(f.slice(0, -3)) : walkTextFiles(f)))
    .filter((file) => /\.(md|markdown)$/i.test(file))
    .flatMap((file) =>
      localMarkdownImages(file)
        .filter((image) => !isPackaged(packageFiles, image))
        .map((image) => `${file}: ${image}`)
    );
  assert.deepEqual(missing, []);
});
