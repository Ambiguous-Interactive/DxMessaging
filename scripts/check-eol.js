#!/usr/bin/env node
/*
  Enforce mixed line-ending policy and no UTF-8 BOM on text files.
  C#/.NET files (.cs, .csproj, .sln, .props) must use CRLF.
  All other tracked text files must use LF.
  Mirrors scripts/check-eol.ps1 behavior used in CI.
  Source of truth for extension lists: scripts/lib/eol-policy.js and .gitattributes.
*/
const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");
const { normalizeToLf } = require("./lib/quote-parser");
const { crlfExts, lfExts } = require("./lib/eol-policy");

function splitNormalizedLines(text) {
  return normalizeToLf(text).split("\n");
}

function getGitRepoRoot() {
  const result = spawnSync("git", ["rev-parse", "--show-toplevel"], {
    cwd: process.cwd(),
    encoding: "utf8"
  });
  if (result.status === 0 && result.stdout) {
    return result.stdout.trim();
  }
  // Fallback to cwd if not in a git repo
  return process.cwd();
}

const repoRoot = getGitRepoRoot();
const argv = process.argv.slice(2);
let checkEntireRepo = false;
const targetArgs = [];

// Parse arguments: --all checks entire repo, otherwise paths are checked
for (const arg of argv) {
  if (arg === "--all") {
    checkEntireRepo = true;
    continue;
  }
  targetArgs.push(arg);
}

// Exclude directory patterns (match anywhere in path)
const excludeRegexes = [
  /(^|[\/\\])\.git([\/\\]|$)/i,
  /(^|[\/\\])node_modules([\/\\]|$)/i,
  /(^|[\/\\])Library([\/\\]|$)/,
  /(^|[\/\\])(Obj|obj)([\/\\]|$)/,
  /(^|[\/\\])Temp([\/\\]|$)/,
  /(^|[\/\\])Samples~([\/\\]|$)/,
  /(^|[\/\\])\.vs([\/\\]|$)/,
  /(^|[\/\\])\.venv([\/\\]|$)/,
  /(^|[\/\\])\.artifacts([\/\\]|$)/,
  /(^|[\/\\])site([\/\\]|$)/
];

/**
 * Report whether an absolute path would be dropped by the directory-exclusion
 * list (.git, node_modules, Library, obj, Temp, Samples~, .vs, .venv,
 * .artifacts, site) before any text-file collection happens.
 *
 * Exposed so callers/tests can verify, against the SAME source of truth the
 * checker uses, that a chosen fixture/scratch location is admissible -- rather
 * than assuming a host-provided directory (e.g. os.tmpdir(), which on Windows
 * is '...\\AppData\\Local\\Temp\\...' and so carries an excluded `Temp`
 * segment) survives collection. The exclusion is case-sensitive for the
 * Unity/.NET segments, so this is the only reliable way to know.
 *
 * @param {string} candidatePath - Absolute path to test.
 * @returns {boolean} True if some exclusion regex matches the path.
 */
function isPathExcluded(candidatePath) {
  return excludeRegexes.some((re) => re.test(candidatePath));
}

// SYNC (bidirectional): Keep extension policy in sync with scripts/lib/eol-policy.js
// (source of truth), scripts/check-eol.ps1 extension lists, and .gitattributes.

// Git hooks directory - files here need LF regardless of extension
const hooksDir = path.join("scripts", "hooks");

// All text file extensions we validate
const exts = new Set([...crlfExts, ...lfExts]);

/**
 * Check if a file is a git hook (no extension, in hooks directory)
 * @param {string} filePath - Absolute path to the file.
 * @returns {boolean} True if the file is a git hook.
 */
function isGitHook(filePath) {
  const rel = path.relative(repoRoot, filePath);
  return rel.startsWith(hooksDir + path.sep) && path.extname(filePath) === "";
}

/**
 * Recursively collect file paths under dir, excluding paths matching excludeRegexes.
 * @param {string} dir - Absolute path to the directory to walk.
 * @param {string[]} files - Accumulator array for collected file paths.
 * @returns {string[]} Array of absolute file paths.
 */
function walk(dir, files = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch (err) {
    console.warn(`Warning: cannot read directory, skipping: ${dir} (${err.code || err.message})`);
    return files;
  }
  for (const ent of entries) {
    const full = path.join(dir, ent.name);
    if (isPathExcluded(full)) {
      continue;
    }
    if (ent.isDirectory()) {
      walk(full, files);
    } else if (ent.isFile()) {
      files.push(full);
    }
  }
  return files;
}

/**
 * Resolve target paths (files or directories) to an array of absolute file paths.
 * Paths are resolved relative to the git repository root.
 * @param {string[]} targets - Array of relative or absolute paths (files or directories).
 * @returns {string[]} Array of absolute file paths, deduplicated and filtered by exclusions.
 */
function resolveTargets(targets) {
  if (!targets || targets.length === 0) {
    return [];
  }

  const seen = new Set();
  const resolved = [];

  for (const rawTarget of targets) {
    // Paths are resolved relative to git repo root (not cwd), matching git's behavior
    const targetPath = path.resolve(repoRoot, rawTarget);
    if (!fs.existsSync(targetPath)) {
      console.warn(
        `Warning: path does not exist, skipping: ${rawTarget} (resolved to: ${targetPath})`
      );
      continue;
    }

    if (isPathExcluded(targetPath)) {
      continue;
    }

    const stats = fs.statSync(targetPath);
    if (stats.isDirectory()) {
      for (const file of walk(targetPath)) {
        if (!seen.has(file)) {
          resolved.push(file);
          seen.add(file);
        }
      }
    } else if (stats.isFile()) {
      if (!seen.has(targetPath)) {
        resolved.push(targetPath);
        seen.add(targetPath);
      }
    }
  }

  return resolved;
}

/**
 * Get list of staged files from git (Added, Copied, Modified, Renamed).
 * Returns paths relative to repoRoot, suitable for passing to resolveTargets().
 * @returns {string[]} Array of staged file paths relative to repo root.
 */
function getStagedFiles() {
  const result = spawnSync("git", ["diff", "--cached", "--name-only", "--diff-filter=ACMR"], {
    cwd: repoRoot,
    encoding: "utf8"
  });

  if (result.status !== 0) {
    return [];
  }

  return splitNormalizedLines(result.stdout)
    .map((line) => line.trim())
    .filter(Boolean);
}

/**
 * Check for non-normalized line endings in the git index for tracked text files.
 * The repository should store LF even when working tree uses CRLF.
 * @param {string[]} files - Absolute file paths to check.
 * @returns {Array<{path: string, indexEol: string}>} Array of index EOL issues.
 */
function getIndexEolIssues(files) {
  if (!files || files.length === 0) {
    return [];
  }

  const relPaths = files.map((file) => path.relative(repoRoot, file));
  const result = spawnSync("git", ["ls-files", "--eol", "--", ...relPaths], {
    cwd: repoRoot,
    encoding: "utf8"
  });

  if (result.status !== 0 || !result.stdout) {
    return [];
  }

  const issues = [];
  const lines = splitNormalizedLines(result.stdout);
  for (const line of lines) {
    if (!line) {
      continue;
    }
    const tabIndex = line.indexOf("\t");
    if (tabIndex < 0) {
      continue;
    }
    const meta = line.slice(0, tabIndex).trim();
    const filePath = line.slice(tabIndex + 1).trim();
    const ext = path.extname(filePath).toLowerCase();
    const fullPath = path.resolve(repoRoot, filePath);
    const isHook = isGitHook(fullPath);

    // Skip if not a known text file extension and not a git hook
    if (!exts.has(ext) && !isHook) {
      continue;
    }

    // git ls-files --eol output format: i/[eol] w/[eol] attr/[attrs] [path]
    const parts = meta.split(/\s+/);
    const indexToken = parts.find((part) => part.startsWith("i/"));
    const attrToken = parts.find((part) => part.startsWith("attr/"));
    if (!indexToken) {
      continue;
    }
    if (attrToken === "attr/-text") {
      continue;
    }

    if (isIndexEolViolation(indexToken)) {
      issues.push({ path: filePath, indexEol: indexToken });
    }
  }

  return issues;
}

/**
 * Classify a git `ls-files --eol` index token as an EOL violation or not.
 *
 * Only CRLF and mixed endings stored in the index are genuine
 * non-normalized-EOL violations. Two tokens are deliberately NOT violations:
 *   - `i/lf` / `i/none`  -- already normalized (none == text with no EOLs).
 *   - `i/-text`          -- git classified the indexed blob as BINARY (for
 *                           example it contains NUL bytes), regardless of any
 *                           explicit `text` gitattribute. A binary blob is
 *                           stored byte-for-byte and is never line-ending
 *                           converted, so it carries no LF/CRLF normalization
 *                           concern. Flagging it produces a false positive
 *                           that NO line-ending fixer can ever clear (fix-eol
 *                           leaves the bytes untouched), which is exactly the
 *                           fixer/checker divergence this classifier prevents.
 *
 * Keep this whitelist-style (only crlf/mixed fail) rather than a blacklist:
 * any future or unknown token git invents stays a non-violation by default,
 * so the checker can never out-grow what fix-eol is able to normalize.
 *
 * @param {string} indexToken - The `i/<eol>` token, e.g. "i/lf", "i/crlf",
 *   "i/mixed", "i/none", "i/-text".
 * @returns {boolean} True only for genuine non-normalized index endings.
 */
function isIndexEolViolation(indexToken) {
  return indexToken === "i/crlf" || indexToken === "i/mixed";
}

/**
 * Check if a buffer starts with UTF-8 BOM (byte order mark).
 * @param {Buffer} buf - File content buffer.
 * @returns {boolean} True if BOM is present.
 */
function hasBom(buf) {
  return buf.length >= 3 && buf[0] === 0xef && buf[1] === 0xbb && buf[2] === 0xbf;
}

/**
 * Check if buffer contains non-CRLF line endings (bare LF or CR).
 * @param {Buffer} buf - File content buffer.
 * @returns {boolean} True if non-CRLF line endings are found.
 */
function hasNonCrlfEol(buf) {
  const txt = buf.toString("utf8");
  // Strip all valid CRLF pairs; any remaining bare \n or \r is invalid
  const stripped = txt.replace(/\r\n/g, "");
  return stripped.includes("\n") || stripped.includes("\r");
}

/**
 * Check if buffer contains non-LF line endings (CRLF or bare CR).
 * Used for shell scripts that must use LF only.
 * @param {Buffer} buf - File content buffer.
 * @returns {boolean} True if non-LF line endings are found.
 */
function hasNonLfEol(buf) {
  const txt = buf.toString("utf8");
  // CRLF or bare CR are invalid for LF-only files
  return txt.includes("\r");
}

function main() {
  const candidateFiles = checkEntireRepo
    ? walk(repoRoot)
    : resolveTargets(targetArgs.length > 0 ? targetArgs : getStagedFiles());

  const textFiles = [];
  const seenFiles = new Set();

  for (const file of candidateFiles) {
    const ext = path.extname(file).toLowerCase();
    const isHook = isGitHook(file);

    // Process if it has a known extension OR if it's a git hook
    if (!exts.has(ext) && !isHook) {
      continue;
    }

    // candidateFiles already contains absolute paths from walk() or resolveTargets()
    if (seenFiles.has(file)) {
      continue;
    }

    seenFiles.add(file);
    textFiles.push(file);
  }

  if (textFiles.length === 0) {
    console.log("EOL check skipped: no matching files to verify.");
    return 0;
  }

  const bomFiles = [];
  const badEolFiles = [];
  const indexEolIssues = getIndexEolIssues(textFiles);

  for (const file of textFiles) {
    let buf;
    try {
      buf = fs.readFileSync(file);
    } catch (err) {
      console.warn(`Warning: cannot read file, skipping: ${file} (${err.code || err.message})`);
      continue;
    }
    if (hasBom(buf)) {
      bomFiles.push(path.relative(repoRoot, file));
    }

    const ext = path.extname(file).toLowerCase();
    const isHook = isGitHook(file);

    // C# and .NET files must use CRLF; all other text files (including shell scripts and git hooks) use LF
    const needsCrlf = crlfExts.has(ext) && !isHook;
    if (needsCrlf) {
      if (hasNonCrlfEol(buf)) {
        badEolFiles.push(path.relative(repoRoot, file));
      }
    } else if (hasNonLfEol(buf)) {
      // All other text files must use LF
      badEolFiles.push(path.relative(repoRoot, file));
    }
  }

  if (bomFiles.length === 0 && badEolFiles.length === 0 && indexEolIssues.length === 0) {
    console.log(
      `EOL check passed: ${textFiles.length} file(s) verified (CRLF for C#/.NET files, LF for all other text files), no BOMs detected.`
    );
    return 0;
  }

  if (bomFiles.length) {
    console.error("Files contain a UTF-8 BOM (should be no BOM):");
    for (const f of bomFiles) {
      console.error(`  ${f}`);
    }
  }
  if (badEolFiles.length) {
    // Separate CRLF-required files (C#/.NET) from LF-required files (everything else)
    const crlfViolations = badEolFiles.filter((f) => {
      const fullPath = path.resolve(repoRoot, f);
      return crlfExts.has(path.extname(f).toLowerCase()) && !isGitHook(fullPath);
    });
    const lfViolations = badEolFiles.filter((f) => {
      const fullPath = path.resolve(repoRoot, f);
      return !crlfExts.has(path.extname(f).toLowerCase()) || isGitHook(fullPath);
    });

    if (crlfViolations.length) {
      console.error("C#/.NET files require CRLF line endings but contain LF or bare CR:");
      for (const f of crlfViolations) {
        console.error(`  ${f}`);
      }
    }
    if (lfViolations.length) {
      console.error("Text files require LF line endings but contain CRLF or bare CR:");
      for (const f of lfViolations) {
        console.error(`  ${f}`);
      }
    }
  }
  if (indexEolIssues.length) {
    console.error(
      "Git index contains non-normalized line endings (expected LF in repo for text files):"
    );
    for (const issue of indexEolIssues) {
      console.error(`  ${issue.path} (${issue.indexEol})`);
    }
    console.error('Fix: Run "node scripts/fix-eol.js" then re-stage affected files.');
  }
  console.error("EOL/BOM policy violations detected.");
  return 1;
}

if (require.main === module) {
  process.exit(main());
}

module.exports = {
  splitNormalizedLines,
  isIndexEolViolation,
  hasBom,
  hasNonCrlfEol,
  hasNonLfEol,
  getStagedFiles,
  getIndexEolIssues,
  isPathExcluded,
  main
};
