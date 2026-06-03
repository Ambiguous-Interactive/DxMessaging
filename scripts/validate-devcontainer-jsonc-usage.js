#!/usr/bin/env node
/**
 * validate-devcontainer-jsonc-usage.js
 *
 * Static guard against re-introducing brittle text-based parsing of any
 * .devcontainer/*.json (which is, by VS Code convention, JSONC -- JSON with
 * comments). A `grep -E '"remoteUser"...' devcontainer.json` line will
 * silently match a commented-out value next to a live divergent value;
 * `awk` / `sed` against the raw JSONC has the same failure mode.
 *
 * Policy:
 *   Any shell script under .devcontainer/ that pipes/reads a .json file
 *   through grep/awk/sed/cut MUST instead use one of the JSONC-aware
 *   helpers in .devcontainer/lib/parse-devcontainer-mounts.sh:
 *     - parse_devcontainer_mounts (for the mounts array)
 *     - get_devcontainer_property (for top-level scalar properties)
 *     - strip_jsonc_comments       (when downstream is jq or a real JSON parser)
 *   ... OR the line must carry an inline `# devcontainer-jsonc-ok: <reason>`
 *   marker documenting why text-based extraction is safe.
 *
 * Detection patterns (one violation per offending line):
 *   - grep ... <something>.json
 *   - awk ... <something>.json
 *   - sed ... <something>.json
 *   - cut -d ... <something>.json
 *
 * Lines piped INTO a downstream consumer that is the helper itself (e.g.,
 *   `bash lib/parse-devcontainer-mounts.sh strip foo.json | jq ...`) are
 *   allowed -- the JSON is going through the JSONC pipeline.
 *
 * Exit codes:
 *   0 - No violations.
 *   1 - At least one violation; details printed to stderr.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..");
const DEVCONTAINER_ROOT = path.join(REPO_ROOT, ".devcontainer");

// Tools that crack open a raw text file. Any of these used against a .json
// path is presumed unsafe unless the line carries the override marker.
const TEXT_TOOLS = ["grep", "awk", "sed", "cut"];

// `# devcontainer-jsonc-ok:` allows a controlled escape hatch. The
// helper itself contains the literal strings of these patterns in its
// usage docs; that's why this regex requires the tool token AT WORD
// BOUNDARY followed by some args before the .json filename.
const TOOL_USE_RE = new RegExp(
  `(?:^|[\\s|;&\`(])(${TEXT_TOOLS.join("|")})\\b(?=[^#\\n]*\\.json\\b)`,
  ""
);

const OVERRIDE_MARKER = "devcontainer-jsonc-ok:";

function listShellFiles(dir) {
  const out = [];
  walk(dir, out);
  return out;
}

function walk(dir, out) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const entry of entries) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      walk(full, out);
    } else if (entry.isFile() && entry.name.toLowerCase().endsWith(".sh")) {
      out.push(full);
    }
  }
}

function scanContent(filePath, content) {
  const violations = [];
  const lines = content.split(/\r?\n/);
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    // Skip lines that are entirely shell comments.
    const trimmed = line.replace(/^\s+/, "");
    if (trimmed.startsWith("#")) {
      continue;
    }
    // Strip trailing comment so we don't match `.json` mentioned in a
    // trailing comment; preserve the override-marker check on the
    // original line below.
    const codePart = stripTrailingComment(line);
    if (!codePart.includes(".json")) {
      continue;
    }
    const match = TOOL_USE_RE.exec(codePart);
    if (!match) {
      continue;
    }
    if (line.includes(OVERRIDE_MARKER)) {
      // Operator has documented the override.
      continue;
    }
    violations.push({
      file: filePath,
      line: i + 1,
      tool: match[1],
      excerpt: codePart.trim(),
      reason:
        "shell text-tool against a .json file in .devcontainer/. Use parse_devcontainer_mounts, get_devcontainer_property, or strip_jsonc_comments from .devcontainer/lib/parse-devcontainer-mounts.sh, or add an inline `# devcontainer-jsonc-ok: <reason>` marker explaining why text-based extraction is safe."
    });
  }
  return violations;
}

function stripTrailingComment(line) {
  // Conservative: split at the first unquoted `#`. We are not trying to
  // be a real shell parser -- this only needs to suppress trailing
  // comments that the operator might use to annotate a safe line.
  let inSingle = false;
  let inDouble = false;
  let escape = false;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (escape) {
      escape = false;
      continue;
    }
    if (ch === "\\" && !inSingle) {
      escape = true;
      continue;
    }
    if (ch === "'" && !inDouble) {
      inSingle = !inSingle;
      continue;
    }
    if (ch === '"' && !inSingle) {
      inDouble = !inDouble;
      continue;
    }
    if (ch === "#" && !inSingle && !inDouble) {
      // Comment must be preceded by whitespace or start-of-line for
      // this to count as a trailing comment.
      const prev = i === 0 ? "" : line[i - 1];
      if (i === 0 || /\s/.test(prev)) {
        return line.slice(0, i);
      }
    }
  }
  return line;
}

function scanFile(filePath) {
  let content;
  try {
    content = fs.readFileSync(filePath, "utf8");
  } catch (error) {
    return [
      {
        file: filePath,
        line: 0,
        tool: "",
        excerpt: "",
        reason: `failed to read: ${error.message}`
      }
    ];
  }
  return scanContent(filePath, content);
}

function listDevcontainerShellFiles() {
  return listShellFiles(DEVCONTAINER_ROOT);
}

function toRepoRelative(absPath) {
  const rel = path.relative(REPO_ROOT, absPath);
  return rel.split(path.sep).join("/");
}

function main() {
  const files = listDevcontainerShellFiles();
  if (files.length === 0) {
    process.stdout.write(
      "validate-devcontainer-jsonc-usage: no .devcontainer/*.sh files to inspect.\n"
    );
    return 0;
  }

  const allViolations = [];
  for (const file of files) {
    allViolations.push(...scanFile(file));
  }

  if (allViolations.length === 0) {
    process.stdout.write(
      `validate-devcontainer-jsonc-usage: 0 violations across ${files.length} script(s).\n`
    );
    return 0;
  }

  for (const v of allViolations) {
    process.stderr.write(
      `${toRepoRelative(v.file)}:${v.line}: ${v.tool} on .json -- ${v.reason}\n` +
        `  excerpt: ${v.excerpt}\n`
    );
  }
  process.stderr.write(
    `validate-devcontainer-jsonc-usage: ${allViolations.length} violation(s) found.\n`
  );
  return 1;
}

module.exports = {
  DEVCONTAINER_ROOT,
  REPO_ROOT,
  TEXT_TOOLS,
  OVERRIDE_MARKER,
  scanContent,
  scanFile,
  listDevcontainerShellFiles,
  main
};

if (require.main === module) {
  process.exit(main());
}
