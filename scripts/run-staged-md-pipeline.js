#!/usr/bin/env node
/**
 * run-staged-md-pipeline.js
 *
 * Single Node process that runs the full markdown pre-commit pipeline against
 * the staged markdown files passed via argv. Round-4 collapses the markdown
 * pre-commit path into one in-process pipeline:
 *
 *   1. normalize-docs-ascii (in-process)
 *   2. fix-md036-headings (in-process via processMarkdownContent)
 *   3. fix-md029-md051    (in-process)
 *   4. validate-docs-out-of-tree-links --fix (in-process)
 *   5. prettier --write   (in-process via the prettier programmatic API)
 *   6. markdownlint-cli2 --fix (in-process via dynamic import of the
 *      markdownlint-cli2 ESM module)
 *   7. The three doc validators (validate-docs-ascii,
 *      validate-doc-code-patterns, validate-docs-prose) -- each already
 *      exposes a programmatic scanContent API.
 *
 * Each underlying script remains usable as a standalone CLI; this pipeline
 * only `require()`s their public APIs. The pre-commit "files were modified
 * by this hook" check is preserved (steps 1-4 may rewrite the file on
 * disk), so the user-facing UX matches the previous five-hook setup.
 *
 * Exit codes:
 *   0  No validator violations (pre-commit may still fail the commit if the
 *      pipeline modified any files; that is the expected fixer UX).
 *   1  At least one validator reported a violation, no files were given
 *      that match the pipeline filter, or an unrecoverable error occurred.
 *
 * The helper file at `node_modules/prettier/bin/prettier.cjs` and the ESM
 * entry at `node_modules/markdownlint-cli2/markdownlint-cli2.mjs` are
 * resolved relative to the repo root, matching the inlined hook entries
 * for cspell / markdownlint / prettier.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");
const { pathToFileURL } = require("url");
const { spawnPlatformCommandSync } = require("./lib/shell-command");
const { toRepoRelative } = require("./lib/repo-files");
const {
  TOOL_LOAD_ERROR_PATTERNS,
  isRecoverableToolLoadError,
  resolveBundledNpxCliPath,
  runBundledNpxCommand,
  loadLocalPrettier
} = require("./lib/managed-prettier");

const ROOT_DIR = path.resolve(__dirname, "..");

const fixMd036 = require("./fix-md036-headings");
const fixMd029Md051 = require("./fix-md029-md051");
const asciiNormalizer = require("./normalize-docs-ascii");
const ascii = require("./validate-docs-ascii");
const codePatterns = require("./validate-doc-code-patterns");
const prose = require("./validate-docs-prose");
const outOfTreeLinks = require("./validate-docs-out-of-tree-links");
const sharedFormatters = require("./lib/staged-doc-formatters");

// Mirror the per-hook YAML filter that this pipeline replaces:
//   files: '(?i)\.(md|markdown)$'
const ALLOWED_EXTS = new Set([".md", ".markdown"]);
const PACKAGE_JSON_PATH = path.join(ROOT_DIR, "package.json");

// Mirror the per-validator exclude regex used by the staged-validators hook
// the pipeline subsumes for `.md` paths.
const EXCLUDE_PREFIXES = ["Library/", "Temp/", "node_modules/", "obj/", "bin/"];
const EXCLUDE_SEGMENT_RE = /(?:^|\/)(?:bin|obj)\//;

const PRETTIER_LOCAL_BIN = path.join(ROOT_DIR, "node_modules", "prettier", "bin", "prettier.cjs");
const PRETTIER_LOCAL_MODULE = path.join(ROOT_DIR, "node_modules", "prettier", "index.cjs");
const MARKDOWNLINT_CLI2_MODULE = path.join(
  ROOT_DIR,
  "node_modules",
  "markdownlint-cli2",
  "markdownlint-cli2.mjs"
);
const WINDOWS_ABSOLUTE_PATH_RE = /^[A-Za-z]:[\\/]/;
const WINDOWS_UNC_ABSOLUTE_PATH_RE = /^(?:\\\\|\/\/)[^\\/]+[\\/][^\\/]+/;

function readPackageJson() {
  return JSON.parse(fs.readFileSync(PACKAGE_JSON_PATH, "utf8"));
}

function getPackageSpec(packageName) {
  const packageJson = readPackageJson();
  const version =
    packageJson.devDependencies?.[packageName] || packageJson.dependencies?.[packageName];
  if (!version) {
    throw new Error(`Missing ${packageName} dependency in package.json.`);
  }
  return `${packageName}@${version.replace(/^[~^]/, "")}`;
}

function runToolCommand(command, args, options = {}) {
  const runner =
    command === "npm" || command === "npx" ? spawnPlatformCommandSync : childProcess.spawnSync;
  return runner(command, args, {
    cwd: ROOT_DIR,
    encoding: "utf8",
    ...options
  });
}

function runNpxCommand(args, options = {}) {
  const {
    execPath = process.execPath,
    resolveBundledNpxCliPathFn = resolveBundledNpxCliPath,
    runToolCommandFn = runToolCommand
  } = options;
  return runBundledNpxCommand(args, {
    execPath,
    resolveBundledNpxCliPathFn,
    runCommandFn: runToolCommandFn
  });
}

function toImportFileUrl(modulePath) {
  if (WINDOWS_ABSOLUTE_PATH_RE.test(modulePath)) {
    const windowsUrl = pathToFileURL(modulePath, { windows: true }).href;
    if (/^file:\/\/\/[A-Za-z]:\//.test(windowsUrl)) {
      return windowsUrl;
    }

    // Fallback for runtimes that ignore the `windows` option.
    const normalized = modulePath.replace(/\\/g, "/");
    return pathToFileURL(`/${normalized}`).href;
  }

  if (WINDOWS_UNC_ABSOLUTE_PATH_RE.test(modulePath)) {
    const windowsUrl = pathToFileURL(modulePath, { windows: true }).href;
    if (/^file:\/\/[^/]/.test(windowsUrl)) {
      return windowsUrl;
    }

    const normalized = modulePath.replace(/\\/g, "/").replace(/^\/+/, "//");
    return new URL(`file:${normalized}`).href;
  }

  return pathToFileURL(modulePath).href;
}

function isExcluded(repoRelPath) {
  for (const prefix of EXCLUDE_PREFIXES) {
    if (repoRelPath.startsWith(prefix)) return true;
  }
  if (EXCLUDE_SEGMENT_RE.test(repoRelPath)) return true;
  return false;
}

function isApplicable(filePath) {
  const ext = path.extname(filePath).toLowerCase();
  if (!ALLOWED_EXTS.has(ext)) return false;
  const rel = toRepoRelative(filePath);
  if (isExcluded(rel)) return false;
  return true;
}

function readFileSafe(filePath) {
  try {
    return fs.readFileSync(filePath, "utf8");
  } catch {
    return null;
  }
}

function writeIfChanged(filePath, originalContent, nextContent, modifiedSet) {
  if (nextContent === originalContent) return originalContent;
  fs.writeFileSync(filePath, nextContent);
  modifiedSet.add(filePath);
  return nextContent;
}

function applyInProcessFixers(absPath, content, modifiedSet) {
  let next = content;
  const normalized = asciiNormalizer.processMarkdown(next);
  if (normalized.changed) {
    next = writeIfChanged(absPath, next, normalized.content, modifiedSet);
  }
  const md036 = fixMd036.processMarkdownContent(next);
  if (md036.changed) {
    next = writeIfChanged(absPath, next, md036.content, modifiedSet);
  }
  const md029Md051 = fixMd029Md051.processMarkdownContent(next);
  if (md029Md051.changed) {
    const before = next;
    next = writeIfChanged(absPath, before, md029Md051.content, modifiedSet);
  }
  const outOfTreeFixed = outOfTreeLinks.fixOutOfTreeRelativeLinks(absPath, next);
  if (outOfTreeFixed.changed) {
    const before = next;
    next = writeIfChanged(absPath, before, outOfTreeFixed.content, modifiedSet);
  }
  return next;
}

async function loadPrettier() {
  return loadLocalPrettier({
    localPrettierModulePath: PRETTIER_LOCAL_MODULE,
    localPrettierBinPath: PRETTIER_LOCAL_BIN,
    isRecoverableToolLoadErrorFn: isRecoverableToolLoadError
  });
}

function runNpxPrettier(absPath, modifiedSet) {
  const before = readFileSafe(absPath);
  const result = runNpxCommand([
    "--yes",
    `--package=${getPackageSpec("prettier")}`,
    "prettier",
    "--write",
    path.relative(ROOT_DIR, absPath).split(path.sep).join("/")
  ]);

  if (result.stdout) process.stdout.write(result.stdout);
  if (result.stderr) process.stderr.write(result.stderr);
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(`npx prettier exited with status ${result.status}.`);
  }

  const after = readFileSafe(absPath);
  if (before !== after) {
    modifiedSet.add(absPath);
  }
  return after ?? before ?? "";
}

async function runPrettierInProcess(absPath, content, modifiedSet) {
  const prettier = await loadPrettier();
  if (!prettier) {
    process.stderr.write(
      "run-staged-md-pipeline: local Prettier install is missing or incomplete; using pinned npx fallback.\n"
    );
    return runNpxPrettier(absPath, modifiedSet);
  }
  const options = await prettier.resolveConfig(absPath, { editorconfig: true });
  const fileInfo = await prettier.getFileInfo(absPath, {
    resolveConfig: false
  });
  if (fileInfo.ignored) {
    return content;
  }
  const formatOptions = {
    ...(options || {}),
    filepath: absPath
  };
  const formatted = await prettier.format(content, formatOptions);
  if (formatted === content) {
    return content;
  }
  return writeIfChanged(absPath, content, formatted, modifiedSet);
}

function snapshotFileStats(absPaths) {
  const sizeByPath = new Map();
  const mtimeByPath = new Map();
  for (const p of absPaths) {
    const stat = fs.statSync(p);
    sizeByPath.set(p, stat.size);
    mtimeByPath.set(p, stat.mtimeMs);
  }

  return { sizeByPath, mtimeByPath };
}

function recordModifiedByStats(absPaths, beforeStats, modifiedSet) {
  for (const p of absPaths) {
    let stat;
    try {
      stat = fs.statSync(p);
    } catch {
      continue;
    }
    if (
      stat.size !== beforeStats.sizeByPath.get(p) ||
      stat.mtimeMs !== beforeStats.mtimeByPath.get(p)
    ) {
      modifiedSet.add(p);
    }
  }
}

function getMarkdownlintRelativePaths(absPaths) {
  return absPaths.map((p) => path.relative(ROOT_DIR, p).split(path.sep).join("/"));
}

async function importMarkdownlintCli2Module() {
  return import(toImportFileUrl(MARKDOWNLINT_CLI2_MODULE));
}

function runNpxMarkdownlint(absPaths, modifiedSet) {
  if (absPaths.length === 0) return { errors: 0 };

  const beforeStats = snapshotFileStats(absPaths);
  const result = runNpxCommand([
    "--yes",
    `--package=${getPackageSpec("markdownlint-cli2")}`,
    "markdownlint-cli2",
    "--fix",
    ...getMarkdownlintRelativePaths(absPaths)
  ]);

  if (result.stdout) process.stdout.write(result.stdout);
  if (result.stderr) process.stderr.write(result.stderr);
  if (result.error) throw result.error;

  recordModifiedByStats(absPaths, beforeStats, modifiedSet);
  return { errors: result.status === 0 ? 0 : result.status || 1 };
}

async function runMarkdownlintInProcess(absPaths, modifiedSet, options = {}) {
  if (absPaths.length === 0) return { errors: 0 };
  const {
    existsSyncFn = fs.existsSync,
    importModuleFn = importMarkdownlintCli2Module,
    runNpxMarkdownlintFn = runNpxMarkdownlint
  } = options;
  if (!existsSyncFn(MARKDOWNLINT_CLI2_MODULE)) {
    process.stderr.write(
      "run-staged-md-pipeline: local markdownlint-cli2 install is missing; using pinned npx fallback.\n"
    );
    return runNpxMarkdownlintFn(absPaths, modifiedSet);
  }
  // markdownlint-cli2 ships as ESM only; CommonJS callers must use a
  // dynamic import. The module object exposes `main(params)` per the
  // bin entry at node_modules/markdownlint-cli2/markdownlint-cli2-bin.mjs.
  let mod;
  try {
    mod = await importModuleFn();
  } catch (error) {
    if (!isRecoverableToolLoadError(error)) {
      throw error;
    }

    process.stderr.write(
      `run-staged-md-pipeline: local markdownlint-cli2 install is incomplete (${error.message}); using pinned npx fallback.\n`
    );
    return runNpxMarkdownlintFn(absPaths, modifiedSet);
  }
  const main = mod.main;

  // Capture output for two reasons: (1) the cli2 banner is noise on hook
  // runs, (2) the modified-set bookkeeping below needs to detect rewrites
  // by re-reading from disk after `--fix` runs in place.
  const beforeStats = snapshotFileStats(absPaths);

  // Pass POSIX-style relative paths to keep markdownlint-cli2's globby
  // resolution stable on Windows.
  const relativePaths = getMarkdownlintRelativePaths(absPaths);

  const collected = [];
  const result = await main({
    directory: ROOT_DIR,
    argv: ["--fix", ...relativePaths],
    logMessage: (msg) => {
      // Drop the banner / progress lines; keep nothing on stdout in
      // success cases. Errors come through logError.
      collected.push(msg);
    },
    logError: (msg) => {
      process.stderr.write(`${msg}\n`);
    },
    allowStdin: false
  });

  // Detect file rewrites caused by `--fix` so the modified-set is
  // accurate. mtime is the cheap signal; size is the tiebreaker.
  recordModifiedByStats(absPaths, beforeStats, modifiedSet);

  return { errors: result, log: collected };
}

function runValidators(absPath, content) {
  const violations = { ascii: [], codePatterns: [], prose: [], outOfTreeLinks: [] };
  const warnings = { ascii: [] };

  const asciiResult = ascii.scanContent(absPath, content, false);
  violations.ascii.push(...asciiResult.violations);
  if (asciiResult.warning) warnings.ascii.push(asciiResult.warning);

  codePatterns.scanMarkdown(absPath, content, violations.codePatterns);

  const proseResult = prose.scanContent(absPath, content);
  violations.prose.push(...proseResult.violations);

  // Out-of-tree link guard only applies to Markdown files under docs/;
  // mkdocs only renders that subtree, so the policy is scoped there.
  if (outOfTreeLinks.isDocsMarkdown(absPath)) {
    violations.outOfTreeLinks.push(...outOfTreeLinks.scanContent(absPath, content));
  }

  return { violations, warnings };
}

// Output formatters live in scripts/lib/staged-doc-formatters.js to keep
// the per-line shape identical between this pipeline and
// run-staged-validators.js. Local thin wrappers bind the pipeline-local
// toRepoRelative.
function formatAsciiViolation(v) {
  return sharedFormatters.formatAsciiViolation(v, toRepoRelative);
}

function formatAsciiWarning(w) {
  return sharedFormatters.formatAsciiWarning(w, toRepoRelative);
}

function formatCodePatternViolation(v) {
  return sharedFormatters.formatCodePatternViolation(v, toRepoRelative);
}

function formatProseViolation(v) {
  return sharedFormatters.formatProseViolation(v, toRepoRelative);
}

function emit(line, useStderr) {
  const stream = useStderr ? process.stderr : process.stdout;
  stream.write(`${line}\n`);
}

async function runStagedMdPipeline(filePaths, options = {}) {
  const skipMarkdownlint = !!options.skipMarkdownlint;
  const skipPrettier = !!options.skipPrettier;
  const applicable = [];
  for (const raw of filePaths) {
    if (isApplicable(raw)) {
      applicable.push(path.isAbsolute(raw) ? raw : path.resolve(process.cwd(), raw));
    }
  }

  const modifiedSet = new Set();
  const aggregated = {
    ascii: { violations: [], warnings: [] },
    codePatterns: { violations: [] },
    prose: { violations: [] },
    outOfTreeLinks: { violations: [] }
  };
  let markdownlintErrors = 0;
  // Tracks files the per-file loop actually processed (i.e. files that
  // existed and were readable). Markdownlint runs in a single batched
  // call after the loop, and would crash with ENOENT on missing files
  // because it stats every input. Including only the present files
  // mirrors the per-file loop's silent-skip behaviour.
  const present = [];

  for (const absPath of applicable) {
    const original = readFileSafe(absPath);
    if (original === null) continue;
    present.push(absPath);

    // Stage 1-4: in-process ASCII/markdown structure and docs out-of-tree
    // link fixers.
    let working = applyInProcessFixers(absPath, original, modifiedSet);

    // Stage 5: prettier in-process.
    if (!skipPrettier) {
      try {
        working = await runPrettierInProcess(absPath, working, modifiedSet);
      } catch (error) {
        emit(
          `run-staged-md-pipeline: prettier failed on ${toRepoRelative(absPath)}: ${error.message}`,
          true
        );
        throw error;
      }
    }
  }

  // Stage 6: markdownlint --fix across all present files at once.
  // Skipping any file the per-file loop couldn't read keeps markdownlint
  // from crashing on a missing path (ENOENT in fs.statSync).
  if (!skipMarkdownlint && present.length > 0) {
    const lintResult = await runMarkdownlintInProcess(present, modifiedSet);
    markdownlintErrors = lintResult.errors || 0;
  }

  // Stage 7: validators read the final post-fixer, post-prettier,
  // post-markdownlint content. This keeps hook results aligned with the
  // actual file snapshot pre-commit will ask the user to re-stage.
  for (const absPath of present) {
    const finalContent = readFileSafe(absPath);
    if (finalContent === null) continue;
    const v = runValidators(absPath, finalContent);
    aggregated.ascii.violations.push(...v.violations.ascii);
    aggregated.ascii.warnings.push(...v.warnings.ascii);
    aggregated.codePatterns.violations.push(...v.violations.codePatterns);
    aggregated.prose.violations.push(...v.violations.prose);
    aggregated.outOfTreeLinks.violations.push(...v.violations.outOfTreeLinks);
  }

  return {
    applicable,
    modified: [...modifiedSet],
    violations: aggregated,
    markdownlintErrors
  };
}

function reportPipelineResult(result) {
  let totalViolations = 0;

  for (const w of result.violations.ascii.warnings) {
    emit(formatAsciiWarning(w), true);
  }

  if (result.violations.ascii.violations.length > 0) {
    emit("-- validate-docs-ascii --", true);
    for (const v of result.violations.ascii.violations) {
      emit(formatAsciiViolation(v), true);
    }
    totalViolations += result.violations.ascii.violations.length;
  }

  if (result.violations.codePatterns.violations.length > 0) {
    emit("-- validate-doc-code-patterns --", true);
    for (const v of result.violations.codePatterns.violations) {
      emit(formatCodePatternViolation(v), true);
    }
    totalViolations += result.violations.codePatterns.violations.length;
  }

  if (result.violations.prose.violations.length > 0) {
    emit("-- validate-docs-prose --", true);
    for (const v of result.violations.prose.violations) {
      emit(formatProseViolation(v), true);
    }
    totalViolations += result.violations.prose.violations.length;
  }

  if (result.violations.outOfTreeLinks && result.violations.outOfTreeLinks.violations.length > 0) {
    emit("-- validate-docs-out-of-tree-links --", true);
    for (const v of result.violations.outOfTreeLinks.violations) {
      const rel = toRepoRelative(v.file);
      emit(`${rel}:${v.line}: out-of-tree link "${v.url}" -- ${v.reason}`, true);
    }
    totalViolations += result.violations.outOfTreeLinks.violations.length;
  }

  if (result.markdownlintErrors > 0) {
    emit(
      `run-staged-md-pipeline: markdownlint reported ${result.markdownlintErrors} error(s); see stderr above.`,
      true
    );
  }

  if (totalViolations === 0 && result.markdownlintErrors === 0 && result.modified.length === 0) {
    emit(
      `run-staged-md-pipeline: 0 violations, no files modified (${result.applicable.length} file(s) inspected).`,
      false
    );
  } else if (totalViolations === 0 && result.markdownlintErrors === 0) {
    emit(
      `run-staged-md-pipeline: 0 violations, ${result.modified.length} file(s) auto-fixed (${result.applicable.length} inspected). Re-stage to commit.`,
      false
    );
  } else {
    emit(
      `run-staged-md-pipeline: ${totalViolations} validator violation(s) and ${result.markdownlintErrors} markdownlint error(s) across ${result.applicable.length} file(s).`,
      true
    );
  }
  return totalViolations + result.markdownlintErrors;
}

async function main(argv) {
  if (!argv || argv.length === 0) {
    process.stdout.write("run-staged-md-pipeline: no file paths given (nothing to do).\n");
    return 0;
  }
  const result = await runStagedMdPipeline(argv);
  const failures = reportPipelineResult(result);
  return failures === 0 ? 0 : 1;
}

module.exports = {
  ROOT_DIR,
  ALLOWED_EXTS,
  EXCLUDE_PREFIXES,
  EXCLUDE_SEGMENT_RE,
  PRETTIER_LOCAL_BIN,
  PRETTIER_LOCAL_MODULE,
  MARKDOWNLINT_CLI2_MODULE,
  WINDOWS_ABSOLUTE_PATH_RE,
  WINDOWS_UNC_ABSOLUTE_PATH_RE,
  TOOL_LOAD_ERROR_PATTERNS,
  readPackageJson,
  getPackageSpec,
  isRecoverableToolLoadError,
  runToolCommand,
  resolveBundledNpxCliPath,
  runNpxCommand,
  toImportFileUrl,
  toRepoRelative,
  isExcluded,
  isApplicable,
  applyInProcessFixers,
  runNpxPrettier,
  runPrettierInProcess,
  snapshotFileStats,
  recordModifiedByStats,
  getMarkdownlintRelativePaths,
  importMarkdownlintCli2Module,
  runNpxMarkdownlint,
  runMarkdownlintInProcess,
  runValidators,
  runStagedMdPipeline,
  reportPipelineResult,
  main
};

if (require.main === module) {
  main(process.argv.slice(2)).then(
    (code) => process.exit(code),
    (error) => {
      process.stderr.write(
        `run-staged-md-pipeline: fatal error: ${error.stack || error.message}\n`
      );
      process.exit(1);
    }
  );
}
