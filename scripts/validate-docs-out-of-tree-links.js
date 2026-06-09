#!/usr/bin/env node
/**
 * validate-docs-out-of-tree-links.js
 *
 * This validator enforces two complementary docs-to-repo linking concerns:
 *
 * 1. OUT-OF-TREE RELATIVE LINKS. Scans Markdown files inside docs/ for relative
 *    links that climb above the docs/ tree. mkdocs strict mode escalates
 *    "warnings" for such links into build failures (it cannot resolve repo
 *    files outside docs/ as valid navigation targets). Docs-to-repo references
 *    must instead use the absolute
 *    `https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/...` URL
 *    so the rendered site links work and the strict build stays green.
 *
 * 2. SELF-REPO BLOB/TREE-LINK TARGETS (validated OFFLINE). The absolute
 *    self-repo blob (file) and tree (directory) URLs that concern (1)
 *    recommends are checked against the WORKING TREE rather than over the
 *    network. The lychee link checker excludes these URLs (see .lychee.toml)
 *    precisely because a network check 404s for files added in the same PR as
 *    the doc that references them (the file is not on master yet) and is
 *    otherwise fragile (rate limits, private-repo 404s). Here we confirm each
 *    `.../blob/<ref>/<path>` or `.../tree/<ref>/<path>` resolves to a real file
 *    or directory on disk; the `<ref>` (branch/sha) segment is ignored because
 *    the working tree is the source of truth.
 *
 * @usage
 *   node scripts/validate-docs-out-of-tree-links.js [--fix] [<file>...]
 *
 * With no arguments, scans every Markdown file under docs/. With one or
 * more arguments, only scans those (used by the pre-commit hook entry).
 *
 * @exitcodes
 *   0 - All checked files are clean (or were auto-fixed when --fix is set).
 *   1 - At least one out-of-tree relative link or missing self-repo blob
 *       target was found.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const { walkFiles, toRepoRelative } = require("./lib/repo-files");
const { parseArgs: parseCliArgs } = require("./lib/cli-options");

const REPO_ROOT = path.resolve(__dirname, "..");
const DOCS_ROOT = path.join(REPO_ROOT, "docs");
const SELF_REPO_BLOB_URL_PREFIX =
  "https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/";

// CommonMark link syntaxes the validator must recognize:
//   - Inline:                `[text](url "title")`
//   - Full reference:        `[text][ref]` + `[ref]: url "title"`
//   - Collapsed reference:   `[ref][]` + `[ref]: url "title"`
//   - Shortcut reference:    `[ref]` + `[ref]: url "title"`
// mkdocs strict mode treats all four forms identically when it follows the
// link to a destination outside the docs/ tree, so the validator MUST cover
// every form. The reviewer verified empirically that
// `[text][ref]\n[ref]: ../../foo.yml` does trigger the mkdocs failure mode
// that inline-only matching would have missed.
const MARKDOWN_LINK_RE = /\[([^\]]*)\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g;
// Reference definition: optional 1-3 space indent, `[label]:`, then a URL
// (a non-whitespace token, optionally wrapped in `<...>`), then an optional
// title in `"..."`, `'...'`, or `(...)`.
const REFERENCE_DEFINITION_RE =
  /^[ ]{0,3}\[([^\]\n]+)\]:\s*<?([^\s>]+)>?(?:\s+(?:"[^"]*"|'[^']*'|\([^)]*\)))?\s*$/gm;
const FENCED_BLOCK_RE = /```[\s\S]*?```|~~~[\s\S]*?~~~/g;
// Inline backtick code spans on a single line. Conservative: only opens with
// a single backtick, closes at the next backtick on the same line. Markdown
// allows multi-backtick spans (``code with `tick` ``) but those are rare in
// docs/ and detection there is a hard problem; this validator deliberately
// scopes to the common case.
const INLINE_CODE_SPAN_RE = /`[^`\n]+`/g;
// 4-space-indented code block detector. Pragmatic approximation: a line that
// starts with 4+ spaces (or a tab) AND is not a continuation of a list item
// in the previous line. We blank-out the link content but preserve newlines
// so violation line numbers stay accurate. CommonMark's full rules around
// list-item continuations are intricate; this approximation covers the docs/
// authoring patterns the project uses (validator behaviour documented in
// scripts/__tests__/docs-out-of-tree-link-guard.test.js).
const INDENTED_CODE_LINE_RE = /^(?: {4}|\t)/;

/**
 * True when the path/name ends in one of the markdown extensions this repo
 * accepts for docs content (`.md` or `.markdown`).
 */
function isMarkdownFile(filePath) {
  return /\.(md|markdown)$/i.test(filePath);
}

function listAllDocsFiles() {
  return walkFiles(DOCS_ROOT, {
    match: (full, dirent) => isMarkdownFile(dirent.name)
  });
}

function stripFencedBlocks(text) {
  return text.replace(FENCED_BLOCK_RE, (block) => block.replace(/[^\n]/g, " "));
}

/**
 * Replace inline backtick code spans with same-length runs of spaces.
 * Newlines are preserved so subsequent line-number reporting stays accurate.
 * Single-line backticks only; multi-backtick spans (e.g., ``foo `bar` baz``)
 * are out of scope per the comment on INLINE_CODE_SPAN_RE.
 */
function stripInlineCodeSpans(text) {
  return text.replace(INLINE_CODE_SPAN_RE, (span) => span.replace(/[^\n]/g, " "));
}

/**
 * Blank-out 4-space-indented code blocks. A line is considered part of an
 * indented code block when it starts with 4+ spaces (or a tab) AND the
 * preceding non-blank line was either blank or itself an indented code line
 * -- this avoids blanking continuation lines of list items. Heuristic;
 * adequate for docs/ patterns the project uses.
 */
function stripIndentedCodeBlocks(text) {
  const lines = text.split("\n");
  let inBlock = false;
  let prevWasBlank = true;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const isIndented = INDENTED_CODE_LINE_RE.test(line);
    const isBlank = line.trim().length === 0;
    if (inBlock) {
      if (isIndented || isBlank) {
        if (isIndented) {
          lines[i] = line.replace(/[^\n]/g, " ");
        }
        // blank lines pass through; we stay in-block.
      } else {
        inBlock = false;
      }
    } else if (isIndented && prevWasBlank) {
      // Enter an indented-code block.
      inBlock = true;
      lines[i] = line.replace(/[^\n]/g, " ");
    }
    prevWasBlank = isBlank;
  }
  return lines.join("\n");
}

function isExternalUrl(url) {
  return /^[a-z][a-z0-9+.-]*:/i.test(url) || url.startsWith("//") || url.startsWith("#");
}

function isMailtoOrAnchor(url) {
  return url.startsWith("mailto:") || url.startsWith("#");
}

function escapesDocsTree(fromFile, linkTarget) {
  // Resolve link target relative to the source file's directory; check
  // whether the resolved absolute path leaves the docs/ tree.
  const fileDir = path.dirname(fromFile);
  const targetPath = linkTarget.split("#")[0].split("?")[0];
  if (!targetPath) {
    return false;
  }
  const resolved = path.resolve(fileDir, targetPath);
  const relative = path.relative(DOCS_ROOT, resolved);
  if (relative.startsWith("..") || path.isAbsolute(relative)) {
    return true;
  }
  return false;
}

function splitLinkTargetAndSuffix(linkTarget) {
  const match = /^([^?#]*)(.*)$/.exec(linkTarget);
  if (!match) {
    return { targetPath: linkTarget, suffix: "" };
  }
  return { targetPath: match[1], suffix: match[2] || "" };
}

function isRepoRelativePath(relPath) {
  return relPath.length > 0 && !relPath.startsWith("..") && !path.isAbsolute(relPath);
}

function encodeRepoPathForBlobUrl(repoRelPath) {
  return repoRelPath
    .split(/[\\/]/)
    .filter((segment) => segment.length > 0)
    .map((segment) => encodeURIComponent(segment))
    .join("/");
}

function outOfTreeRelativeLinkToBlobUrl(fromFile, linkTarget) {
  if (isExternalUrl(linkTarget) || isMailtoOrAnchor(linkTarget)) {
    return null;
  }
  if (!escapesDocsTree(fromFile, linkTarget)) {
    return null;
  }

  const { targetPath, suffix } = splitLinkTargetAndSuffix(linkTarget);
  if (!targetPath) {
    return null;
  }

  const resolved = path.resolve(path.dirname(fromFile), targetPath);
  const repoRel = path.relative(REPO_ROOT, resolved);
  if (!isRepoRelativePath(repoRel)) {
    return null;
  }

  const encodedPath = encodeRepoPathForBlobUrl(repoRel);
  if (!encodedPath) {
    return null;
  }
  return `${SELF_REPO_BLOB_URL_PREFIX}${encodedPath}${suffix}`;
}

// Self-repo blob/tree URL shape: `.../blob/<ref>/<path>` (file links) and
// `.../tree/<ref>/<path>` (directory links). The `<ref>` is a single branch/sha
// segment ([^/]+); everything after it is the repo-relative path we validate
// against the working tree (fs.existsSync resolves files AND directories, so the
// same check covers both forms). Anchored so it only matches OUR repo.
const SELF_REPO_BLOB_RE =
  /^https:\/\/github\.com\/Ambiguous-Interactive\/DxMessaging\/(?:blob|tree)\/[^/]+\/(.+)$/;
// Ephemeral CI-run URL shape: `https://github.com/<org>/<repo>/actions/runs/<runId>`.
// These URLs are PER-RUN audit-trail decoration; the run is purgeable, the link
// goes 404, and lychee then reports a hard failure. This is a CLASS BUG, not a
// point fix (no repo can rely on a specific actions/runs URL surviving), so the
// validator REJECTS it ANYWHERE in any docs/ markdown link. The right shape for
// citing a run id in prose is plain backticked text (e.g. `production run 12345`)
// -- no hyperlink. Anchored so a substring inside another URL cannot match.
const EPHEMERAL_CI_RUN_RE = /^https:\/\/github\.com\/[^/]+\/[^/]+\/actions\/runs\/\d+/;
// A single trailing prose character that can leak into a captured token in
// edge cases (e.g. a stray trailing backtick, period, closing paren, or
// comma). We only ever trim ONE of these, and only when doing so makes a
// previously-missing path resolve -- never when it would mask a real broken
// link. See selfRepoBlobTarget for the conservative application rule.
const TRAILING_PROSE_CHARS = new Set(["`", ".", ")", ","]);

/**
 * Decode a self-repo blob/tree URL into the repo-relative path it points at, or
 * return null when the URL is not a self-repo blob or tree link.
 *
 * The returned path is the destination AS A WORKING-TREE PATH:
 *   - the `<ref>` (branch/sha) segment is dropped (working tree is truth);
 *   - a trailing `#fragment` and `?query` are removed;
 *   - the result is `decodeURIComponent`-decoded (`%20`->space, `%2B`->`+`).
 * Tilde is NOT expanded: a literal `~` (as in `Samples~/`) is a real dir char.
 *
 * @param {string} url - candidate link URL.
 * @returns {string|null} repo-relative path, or null if not a self-repo blob/tree.
 */
function selfRepoBlobTarget(url) {
  const match = SELF_REPO_BLOB_RE.exec(url);
  if (!match) {
    return null;
  }
  // Drop fragment/query before decoding so encoded `#`/`?` inside the path are
  // not mistaken for delimiters (they would already be percent-encoded here).
  let captured = match[1].split("#")[0].split("?")[0];
  let decoded;
  try {
    decoded = decodeURIComponent(captured);
  } catch {
    // Malformed percent-encoding (e.g. `foo%bar.md` or a truncated `foo%2`)
    // makes decodeURIComponent throw a URIError. Keep the RAW captured path so
    // the existence check still runs and reports it as a (correctly) missing
    // target rather than letting the URIError crash the whole validator run.
    decoded = captured;
  }
  return decoded;
}

/**
 * Resolve a self-repo blob/tree target path against the working tree, applying
 * the conservative trailing-prose-punctuation trim. Returns true when the
 * target (file or directory) exists on disk. The trim removes a SINGLE trailing
 * `` ` ``/`.`/`)`/`,` ONLY when the untrimmed path is missing AND the trimmed
 * path exists -- so a genuine broken link is never masked.
 *
 * @param {string} repoRelPath - decoded repo-relative path from selfRepoBlobTarget.
 * @returns {boolean} whether the (optionally trimmed) path exists in the tree.
 */
function selfRepoBlobTargetExists(repoRelPath) {
  if (fs.existsSync(path.resolve(REPO_ROOT, repoRelPath))) {
    return true;
  }
  const last = repoRelPath.slice(-1);
  if (TRAILING_PROSE_CHARS.has(last)) {
    const trimmed = repoRelPath.slice(0, -1);
    if (trimmed && fs.existsSync(path.resolve(REPO_ROOT, trimmed))) {
      return true;
    }
  }
  return false;
}

function lineNumberOf(text, charIndex) {
  let n = 1;
  for (let i = 0; i < charIndex && i < text.length; i++) {
    if (text[i] === "\n") {
      n++;
    }
  }
  return n;
}

function strippedMarkdownForLinkPasses(content) {
  // Strip code regions in this order: fenced blocks first (so their content
  // is fully blanked before inline-tick detection), then inline backtick
  // spans, then 4-space-indented blocks. Each pass replaces forbidden
  // regions with spaces (preserving newlines) so line-number reporting in
  // the link-detection passes below stays accurate.
  let stripped = stripFencedBlocks(content);
  stripped = stripInlineCodeSpans(stripped);
  stripped = stripIndentedCodeBlocks(stripped);
  return stripped;
}

function collectOutOfTreeRelativeLinkFixes(filePath, content) {
  if (!isDocsMarkdown(filePath)) {
    return [];
  }

  const stripped = strippedMarkdownForLinkPasses(content);
  const fixes = [];

  const collect = (match, baseIndex) => {
    const url = match[2];
    if (!url) {
      return;
    }
    const replacement = outOfTreeRelativeLinkToBlobUrl(filePath, url);
    if (!replacement || replacement === url) {
      return;
    }
    const urlOffset = match[0].indexOf(url);
    if (urlOffset < 0) {
      return;
    }
    const start = baseIndex + urlOffset;
    fixes.push({
      start,
      end: start + url.length,
      before: url,
      after: replacement
    });
  };

  MARKDOWN_LINK_RE.lastIndex = 0;
  let match;
  while ((match = MARKDOWN_LINK_RE.exec(stripped)) !== null) {
    collect(match, match.index);
  }

  REFERENCE_DEFINITION_RE.lastIndex = 0;
  while ((match = REFERENCE_DEFINITION_RE.exec(stripped)) !== null) {
    collect(match, match.index);
  }

  fixes.sort((a, b) => a.start - b.start || a.end - b.end);
  const deduped = [];
  let previous = null;
  for (const fix of fixes) {
    if (previous && fix.start === previous.start && fix.end === previous.end) {
      if (fix.after === previous.after) {
        continue;
      }
      // Conflicting replacement at the same region: keep the first one.
      continue;
    }
    if (previous && fix.start < previous.end) {
      // Overlapping fix regions are unexpected; skip the later one.
      continue;
    }
    deduped.push(fix);
    previous = fix;
  }
  return deduped;
}

function applyTextFixes(content, fixes) {
  if (fixes.length === 0) {
    return content;
  }
  let out = content;
  for (let i = fixes.length - 1; i >= 0; i--) {
    const fix = fixes[i];
    out = `${out.slice(0, fix.start)}${fix.after}${out.slice(fix.end)}`;
  }
  return out;
}

function fixOutOfTreeRelativeLinks(filePath, content) {
  const fixes = collectOutOfTreeRelativeLinkFixes(filePath, content);
  if (fixes.length === 0) {
    return { content, changed: false, fixes: [] };
  }
  return {
    content: applyTextFixes(content, fixes),
    changed: true,
    fixes
  };
}

function fixFile(filePath) {
  let content;
  try {
    content = fs.readFileSync(filePath, "utf8");
  } catch (error) {
    return { changed: false, fixes: [], error };
  }

  const result = fixOutOfTreeRelativeLinks(filePath, content);
  if (!result.changed) {
    return { changed: false, fixes: [] };
  }

  fs.writeFileSync(filePath, result.content);
  return { changed: true, fixes: result.fixes };
}

function scanContent(filePath, content) {
  const violations = [];
  const stripped = strippedMarkdownForLinkPasses(content);

  const checkUrl = (url, charIndex) => {
    // CONCERN 3 (ephemeral CI-run URLs) runs FIRST: any markdown link whose
    // URL points at `https://github.com/<org>/<repo>/actions/runs/<runId>` is
    // rejected outright. These URLs are per-run audit-trail decoration -- the
    // run is purgeable, the link goes 404 within months, and lychee then
    // reports a hard failure during docs lint (this is a CLASS bug, not a
    // point fix). Cite the run id as backticked text instead of a hyperlink.
    if (EPHEMERAL_CI_RUN_RE.test(url)) {
      violations.push({
        file: filePath,
        line: lineNumberOf(stripped, charIndex),
        url,
        reason:
          "ephemeral CI run URLs go stale; cite the run id as backticked text instead of a hyperlink"
      });
      return;
    }

    // CONCERN 2 (offline self-repo blob/tree existence) runs FIRST and
    // independently of the out-of-tree check below. A self-repo blob/tree URL is
    // an EXTERNAL url (starts with `https:`), so the `isExternalUrl`
    // short-circuit further down would otherwise skip it entirely. We
    // deliberately check it here, before that early-return, and then fall
    // through (a self-repo blob/tree URL never also trips the out-of-tree check,
    // because `isExternalUrl` will return for it).
    const blobTarget = selfRepoBlobTarget(url);
    if (blobTarget !== null) {
      if (!selfRepoBlobTargetExists(blobTarget)) {
        violations.push({
          file: filePath,
          line: lineNumberOf(stripped, charIndex),
          url,
          reason: `self-repo blob/tree link target does not exist in the working tree: ${blobTarget}`
        });
      }
      return;
    }

    // CONCERN 1 (out-of-tree relative links). External/mailto/anchor links are
    // never out-of-tree relative links, so short-circuit them here.
    if (isExternalUrl(url) || isMailtoOrAnchor(url)) {
      return;
    }
    if (!escapesDocsTree(filePath, url)) {
      return;
    }
    violations.push({
      file: filePath,
      line: lineNumberOf(stripped, charIndex),
      url,
      reason:
        "links from docs/ to repo files outside docs/ must use the full https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/... URL"
    });
  };

  // Inline links: `[text](url)`.
  MARKDOWN_LINK_RE.lastIndex = 0;
  let match;
  while ((match = MARKDOWN_LINK_RE.exec(stripped)) !== null) {
    checkUrl(match[2], match.index);
  }

  // Reference-style link DEFINITIONS: `[ref]: url "title"`. These are the
  // line that actually carries the URL; the in-body usage (`[text][ref]`,
  // `[ref][]`, or bare `[ref]`) does not need separate scanning because
  // mkdocs strict mode trips on the resolved URL, which lives in the
  // definition. CommonMark requires definitions to start at column 0-3 and
  // we anchor with `^` via the `m` flag.
  REFERENCE_DEFINITION_RE.lastIndex = 0;
  while ((match = REFERENCE_DEFINITION_RE.exec(stripped)) !== null) {
    checkUrl(match[2], match.index);
  }
  return violations;
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
        url: "",
        reason: `failed to read: ${error.message}`
      }
    ];
  }
  return scanContent(filePath, content);
}

function isDocsMarkdown(filePath) {
  const abs = path.isAbsolute(filePath) ? filePath : path.resolve(process.cwd(), filePath);
  if (!isMarkdownFile(abs)) {
    return false;
  }
  const rel = path.relative(DOCS_ROOT, abs);
  return !rel.startsWith("..") && !path.isAbsolute(rel);
}

function printHelp() {
  process.stdout.write(
    [
      "Usage: node scripts/validate-docs-out-of-tree-links.js [--fix] [<file>...]",
      "",
      "Checks docs/*.md and docs/*.markdown links for out-of-tree relative",
      "paths and stale self-repo",
      "blob/tree targets. With --fix, rewrites out-of-tree relative links to",
      "absolute GitHub blob URLs before validating.",
      ""
    ].join("\n")
  );
}

function parseArgs(argv) {
  const { values, positionals } = parseCliArgs(argv, {
    options: {
      fix: { type: "boolean", aliases: ["--fix"] },
      help: { type: "boolean", aliases: ["--help", "-h"] }
    },
    unknownOption: "collect",
    allowEquals: false,
    endOfOptions: false
  });
  // `--help`/`-h` short-circuits the original loop, discarding any `--fix` or
  // files seen before it; reproduce that here rather than threading them out.
  if (values.help) {
    return { help: true, fix: false, files: [] };
  }
  return { help: false, fix: Boolean(values.fix), files: positionals };
}

function main(argv) {
  const parsed = parseArgs(argv);
  if (parsed.help) {
    printHelp();
    return 0;
  }

  let files;
  if (parsed.files.length === 0) {
    files = listAllDocsFiles();
  } else {
    files = parsed.files
      .map((file) => (path.isAbsolute(file) ? file : path.resolve(process.cwd(), file)))
      .filter((file) => isDocsMarkdown(file));
  }

  if (files.length === 0) {
    process.stdout.write(
      "validate-docs-out-of-tree-links: no docs/*.md or docs/*.markdown files to inspect.\n"
    );
    return 0;
  }

  let fixedFiles = 0;
  let fixedLinks = 0;
  if (parsed.fix) {
    for (const file of files) {
      try {
        const result = fixFile(file);
        if (result.changed) {
          fixedFiles++;
          fixedLinks += result.fixes.length;
        }
      } catch (error) {
        process.stderr.write(
          `${toRepoRelative(file)}:0: out-of-tree link "" -- failed to auto-fix: ${error.message}\n`
        );
      }
    }

    if (fixedLinks > 0) {
      process.stdout.write(
        `validate-docs-out-of-tree-links: auto-fixed ${fixedLinks} link(s) across ${fixedFiles} file(s).\n`
      );
    }
  }

  const allViolations = [];
  for (const file of files) {
    allViolations.push(...scanFile(file));
  }

  if (allViolations.length === 0) {
    process.stdout.write(
      `validate-docs-out-of-tree-links: 0 violations across ${files.length} file(s).\n`
    );
    return 0;
  }

  for (const v of allViolations) {
    process.stderr.write(
      `${toRepoRelative(v.file)}:${v.line}: out-of-tree link "${v.url}" -- ${v.reason}\n`
    );
  }
  process.stderr.write(
    `validate-docs-out-of-tree-links: ${allViolations.length} violation(s) found.\n`
  );
  return 1;
}

module.exports = {
  DOCS_ROOT,
  REPO_ROOT,
  SELF_REPO_BLOB_URL_PREFIX,
  scanContent,
  scanFile,
  escapesDocsTree,
  splitLinkTargetAndSuffix,
  encodeRepoPathForBlobUrl,
  outOfTreeRelativeLinkToBlobUrl,
  collectOutOfTreeRelativeLinkFixes,
  fixOutOfTreeRelativeLinks,
  fixFile,
  // Names kept as `*BlobTarget*` for stability (tests import them), but they
  // cover BOTH self-repo `blob/` (file) and `tree/` (directory) links.
  selfRepoBlobTarget,
  selfRepoBlobTargetExists,
  EPHEMERAL_CI_RUN_RE,
  isDocsMarkdown,
  listAllDocsFiles,
  stripFencedBlocks,
  stripInlineCodeSpans,
  stripIndentedCodeBlocks,
  parseArgs,
  main
};

if (require.main === module) {
  process.exit(main(process.argv.slice(2)));
}
