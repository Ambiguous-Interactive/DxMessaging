#!/usr/bin/env node
/**
 * @fileoverview Validates repository identity references.
 *
 * Default and --check mode: report stale references and exit non-zero, never
 * mutating files. --fix mode: rewrite stale references in place using each
 * pattern's replacement. --fix is idempotent (a second run is a no-op and
 * leaves files byte-identical), writes only when a file's content actually
 * changes, and never alters line endings (replacements never contain newline
 * characters, so the original CR/LF bytes are preserved verbatim).
 */

"use strict";

const { execFileSync } = require("child_process");
const fs = require("fs");
const path = require("path");
const { normalizeToLf } = require("./lib/quote-parser");

const EXPECTED_REPOSITORY = "Ambiguous-Interactive/DxMessaging";

const repoRoot = path.resolve(__dirname, "..");

const staleIdentityPatterns = [
  {
    pattern: /https?:\/\/github\.com\/wallstop\/DxMessaging(?:[/?#][^\s"'<>)]*)?/g,
    label: "stale GitHub URL",
    replacement: `https://github.com/${EXPECTED_REPOSITORY}`
  },
  {
    pattern: /https?:\/\/wallstop\.github\.io\/DxMessaging(?:\/[^\s"'<>)]*)?/g,
    label: "stale documentation URL",
    replacement: "https://ambiguous-interactive.github.io/DxMessaging/"
  },
  {
    pattern:
      /github\.repository\s*(?:==|!=)\s*['"](?:wallstop\/DxMessaging|wallstop-studios\/com\.wallstop-studios\.dxmessaging)['"]/g,
    label: "stale github.repository guard",
    replacement: `github.repository == '${EXPECTED_REPOSITORY}'`
  },
  {
    pattern: /\bwallstop-studios\/com\.wallstop-studios\.dxmessaging\b/g,
    label: "stale repository slug",
    replacement: EXPECTED_REPOSITORY
  },
  {
    pattern: /\bwallstop\/DxMessaging\b/g,
    label: "stale repository slug",
    replacement: EXPECTED_REPOSITORY
  }
];

function parseGitFileList(output) {
  return normalizeToLf(output)
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

function getTrackedFiles(execFileSyncImpl = execFileSync) {
  return parseGitFileList(
    execFileSyncImpl("git", ["ls-files"], {
      cwd: repoRoot,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"]
    })
  );
}

function getRepositoryCandidateFiles(execFileSyncImpl = execFileSync) {
  const trackedFiles = getTrackedFiles(execFileSyncImpl);
  const stagedFiles = parseGitFileList(
    execFileSyncImpl("git", ["diff", "--cached", "--name-only", "--diff-filter=ACMR"], {
      cwd: repoRoot,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"]
    })
  );
  const untrackedFiles = parseGitFileList(
    execFileSyncImpl("git", ["ls-files", "--others", "--exclude-standard"], {
      cwd: repoRoot,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"]
    })
  );

  return [...new Set([...trackedFiles, ...stagedFiles, ...untrackedFiles])].sort();
}

function isTextContent(content) {
  return !content.includes("\u0000");
}

function findStaleIdentityReferencesInContent(content, filePath) {
  const errors = [];
  const normalizedContent = normalizeToLf(content);
  const lines = normalizedContent.split("\n");

  for (let lineIndex = 0; lineIndex < lines.length; lineIndex++) {
    const line = lines[lineIndex];
    const reportedRanges = [];

    for (const stalePattern of staleIdentityPatterns) {
      stalePattern.pattern.lastIndex = 0;

      for (const match of line.matchAll(stalePattern.pattern)) {
        const value = match[0];
        const start = match.index;
        const end = start + value.length;
        const overlapsReportedRange = reportedRanges.some(
          (range) => start < range.end && end > range.start
        );

        if (overlapsReportedRange) {
          continue;
        }

        reportedRanges.push({ start, end });

        errors.push({
          type: "stale-repository-identity",
          file: filePath,
          line: lineIndex + 1,
          value,
          message:
            `${filePath}:${lineIndex + 1} contains ${stalePattern.label} '${value}'. ` +
            `Use '${stalePattern.replacement}' for repository identity.`
        });
      }
    }

    if (filePath === ".github/dependabot.yml" && /^\s*-\s*wallstop\s*$/.test(line)) {
      errors.push({
        type: "stale-dependabot-routing",
        file: filePath,
        line: lineIndex + 1,
        value: line.trim(),
        message:
          `${filePath}:${lineIndex + 1} routes Dependabot ownership to '${line.trim()}'. ` +
          "Remove the stale owner or replace it with Ambiguous-owned routing."
      });
    }
  }

  return errors;
}

/**
 * Rewrite stale repository identity references in raw file content.
 *
 * Each stale pattern is replaced by its canonical replacement. Replacements
 * never contain CR or LF, so applying them to the raw (un-normalized) content
 * leaves every line-ending byte untouched. The dependabot owner-routing finding
 * has no canonical replacement and is intentionally left unchanged; callers that
 * also validate will continue to surface it as a non-fixable error.
 *
 * The transform is idempotent: the canonical replacements never re-match any
 * stale pattern, so a second pass produces byte-identical output.
 *
 * @param {string} content - Raw file content (line endings preserved verbatim).
 * @returns {{ content: string, changed: boolean }} Rewritten content and a flag
 *   indicating whether any replacement altered the content.
 */
function fixStaleIdentityReferencesInContent(content) {
  let updated = content;

  for (const stalePattern of staleIdentityPatterns) {
    stalePattern.pattern.lastIndex = 0;
    updated = updated.replace(stalePattern.pattern, stalePattern.replacement);
  }

  return { content: updated, changed: updated !== content };
}

function findStaleIdentityReferences(filePaths, options = {}) {
  const errors = [];
  const readFileSyncImpl = options.readFileSync || fs.readFileSync;

  for (const filePath of filePaths) {
    const absolutePath = path.resolve(repoRoot, filePath);
    let content;

    try {
      content = readFileSyncImpl(absolutePath, "utf8");
    } catch (error) {
      errors.push({
        type: "unreadable-file",
        file: filePath,
        line: 0,
        value: "",
        message: `${filePath}: unable to read file: ${error.message}`
      });
      continue;
    }

    if (!isTextContent(content)) {
      continue;
    }

    errors.push(...findStaleIdentityReferencesInContent(content, filePath));
  }

  return errors;
}

/**
 * Rewrite stale repository identity references across the given files in place.
 *
 * Reads each file, applies fixStaleIdentityReferencesInContent, and writes back
 * only when the content actually changes. Unreadable files are reported rather
 * than skipped silently. Returns the list of files whose content changed and the
 * remaining (post-fix) findings -- only references that have no canonical
 * replacement (currently dependabot owner-routing) or unreadable files survive.
 *
 * @param {string[]} filePaths - Candidate files to rewrite.
 * @param {object} [options] - Injection points: readFileSync, writeFileSync.
 * @returns {{ changedFiles: string[], errors: object[] }}
 */
function fixStaleIdentityReferences(filePaths, options = {}) {
  const readFileSyncImpl = options.readFileSync || fs.readFileSync;
  const writeFileSyncImpl = options.writeFileSync || fs.writeFileSync;
  const changedFiles = [];
  const errors = [];

  for (const filePath of filePaths) {
    const absolutePath = path.resolve(repoRoot, filePath);
    let content;

    try {
      content = readFileSyncImpl(absolutePath, "utf8");
    } catch (error) {
      errors.push({
        type: "unreadable-file",
        file: filePath,
        line: 0,
        value: "",
        message: `${filePath}: unable to read file: ${error.message}`
      });
      continue;
    }

    if (!isTextContent(content)) {
      continue;
    }

    const { content: fixedContent, changed } = fixStaleIdentityReferencesInContent(content);

    if (changed) {
      writeFileSyncImpl(absolutePath, fixedContent, "utf8");
      changedFiles.push(filePath);
    }

    // Surface any references that survive the rewrite (no canonical replacement).
    errors.push(...findStaleIdentityReferencesInContent(fixedContent, filePath));
  }

  return { changedFiles, errors };
}

function validateRepoIdentity(options = {}) {
  const files = options.files || getRepositoryCandidateFiles(options.execFileSync);

  if (options.fix) {
    const { changedFiles, errors } = fixStaleIdentityReferences(files, options);

    if (changedFiles.length > 0) {
      console.log(`Repository identity fix rewrote ${changedFiles.length} file(s):`);
      for (const changedFile of changedFiles) {
        console.log(`  - ${changedFile}`);
      }
    } else {
      console.log("Repository identity fix found nothing to rewrite.");
    }

    if (errors.length === 0) {
      console.log(`Repository identity references are canonical for ${EXPECTED_REPOSITORY}.`);
      return { valid: true, changedFiles, errors: [] };
    }

    console.error(`Repository identity fix left ${errors.length} unfixable reference(s).`);
    for (const error of errors) {
      console.error(`  - ${error.message}`);
    }

    return { valid: false, changedFiles, errors };
  }

  const errors = findStaleIdentityReferences(files, options);

  if (errors.length === 0) {
    console.log(`Repository identity validation passed for ${EXPECTED_REPOSITORY}.`);
    return { valid: true, errors: [] };
  }

  console.error(
    `Repository identity validation failed: found ${errors.length} stale reference(s).`
  );
  for (const error of errors) {
    console.error(`  - ${error.message}`);
  }

  return { valid: false, errors };
}

if (require.main === module) {
  const args = process.argv.slice(2);
  const fix = args.includes("--fix");
  const flags = new Set(["--check", "--fix"]);
  const fileArgs = args.filter((arg) => !flags.has(arg) && !arg.startsWith("-"));

  try {
    const result = validateRepoIdentity({
      fix,
      files: fileArgs.length > 0 ? fileArgs : undefined
    });
    if (!result.valid) {
      process.exitCode = 1;
    }
  } catch (error) {
    console.error("Repository identity validation failed:", error.message);
    process.exit(1);
  }
}

module.exports = {
  EXPECTED_REPOSITORY,
  findStaleIdentityReferences,
  findStaleIdentityReferencesInContent,
  fixStaleIdentityReferences,
  fixStaleIdentityReferencesInContent,
  getRepositoryCandidateFiles,
  getTrackedFiles,
  parseGitFileList,
  validateRepoIdentity
};
