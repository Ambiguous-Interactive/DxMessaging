#!/usr/bin/env node
/*
  Fix mixed line-ending policy and remove UTF-8 BOM on text files.
  C#/.NET files (.cs, .csproj, .sln, .props) are converted to CRLF.
  All other tracked text files are normalized to LF.
  Mirrors scope and rules from scripts/check-eol.js.
  Source of truth for extension lists: scripts/lib/eol-policy.js and .gitattributes.
*/
const fs = require("fs");
const path = require("path");
const { crlfExts, lfExts } = require("./lib/eol-policy");

const repoRoot = process.cwd();
const argv = process.argv.slice(2);
const verbose = argv.includes("-v") || argv.includes("--verbose");
const flagArgs = new Set(["-v", "--verbose"]);
const targetArgs = argv.filter((arg) => !flagArgs.has(arg));

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

// SYNC (bidirectional): Keep extension policy in sync with scripts/lib/eol-policy.js
// (source of truth), scripts/check-eol.ps1 extension lists, and .gitattributes.

// Git hooks directory - files here need LF regardless of extension
const hooksDir = path.join("scripts", "hooks");

// All text file extensions we fix
const exts = new Set([...crlfExts, ...lfExts]);

// Check if a file is a git hook (no extension, in hooks directory)
function isGitHook(filePath) {
  const rel = path.relative(repoRoot, filePath);
  return rel.startsWith(hooksDir + path.sep) && path.extname(filePath) === "";
}

function walk(dir, files = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch (err) {
    console.warn(`Warning: Unable to read directory ${dir}: ${err.code || err.message}`);
    return files;
  }
  for (const ent of entries) {
    const full = path.join(dir, ent.name);
    if (excludeRegexes.some((re) => re.test(full))) {
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

function stripBom(buf) {
  if (buf.length >= 3 && buf[0] === 0xef && buf[1] === 0xbb && buf[2] === 0xbf) {
    return buf.slice(3);
  }
  return buf;
}

function toCrlf(txt) {
  // Normalize all newlines to \n then convert to CRLF
  // Replace CRLF first to avoid doubling
  let normalized = txt.replace(/\r\n/g, "\n");
  normalized = normalized.replace(/\r/g, "\n");
  return normalized.replace(/\n/g, "\r\n");
}

function toLf(txt) {
  // Normalize all newlines to LF (for shell scripts)
  let normalized = txt.replace(/\r\n/g, "\n");
  normalized = normalized.replace(/\r/g, "\n");
  return normalized;
}

function resolveTargets(targets) {
  if (targets.length === 0) {
    return walk(repoRoot);
  }

  const seen = new Set();
  const resolvedFiles = [];

  for (const rawTarget of targets) {
    const targetPath = path.resolve(repoRoot, rawTarget);
    if (!fs.existsSync(targetPath)) {
      continue;
    }

    const stats = fs.statSync(targetPath);
    if (stats.isDirectory()) {
      for (const file of walk(targetPath)) {
        if (!seen.has(file)) {
          resolvedFiles.push(file);
          seen.add(file);
        }
      }
    } else if (stats.isFile()) {
      if (!seen.has(targetPath)) {
        resolvedFiles.push(targetPath);
        seen.add(targetPath);
      }
    }
  }

  return resolvedFiles;
}

const allFiles = resolveTargets(targetArgs);
let changed = 0;
let scanned = 0;

for (const file of allFiles) {
  const ext = path.extname(file).toLowerCase();
  const isHook = isGitHook(file);

  // Process if it has a known extension OR if it's a git hook
  if (!exts.has(ext) && !isHook) continue;
  let buf;
  try {
    buf = fs.readFileSync(file);
  } catch (err) {
    if (verbose) {
      console.warn(
        `Warning: cannot read file, skipping: ${path.relative(repoRoot, file)} (${err.code || err.message})`
      );
    }
    continue;
  }
  scanned++;
  const origBuf = buf;
  buf = stripBom(buf);
  const origTxt = origBuf.toString("utf8");
  const noBomTxt = buf.toString("utf8");

  // C# and .NET files use CRLF; all other text files (including shell scripts and git hooks) use LF
  const needsCrlf = crlfExts.has(ext) && !isHook;
  const fixedTxt = needsCrlf ? toCrlf(noBomTxt) : toLf(noBomTxt);

  if (fixedTxt !== origTxt) {
    fs.writeFileSync(file, fixedTxt, { encoding: "utf8" });
    changed++;
    if (verbose) console.log(`Fixed: ${path.relative(repoRoot, file)}`);
  }
}

console.log(`Scanned ${scanned} text files. Updated ${changed}.`);
if (changed > 0) {
  console.log(
    "All changes written with correct line endings (CRLF for C#/.NET files, LF for all other text files) and no BOM."
  );
}
