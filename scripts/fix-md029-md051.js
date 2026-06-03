#!/usr/bin/env node
// Safe auto-fix for markdownlint MD029 (ordered list prefix) and MD051 (link fragments).
// - Normalizes ordered list prefixes to 1. outside fenced code blocks.
// - Fixes local fragment links (#...) to match GitHub/markdownlint heading fragments.

"use strict";

const fs = require("fs");
const path = require("path");

const headingAnchorRe = /\{(#[a-z\d]+(?:[-_][a-z\d]+)*)\}/gu;
const lineFragmentRe = /^#(?:L\d+(?:C\d+)?-L\d+(?:C\d+)?|L\d+)$/u;
const fencedCodeRe = /^(?:>\s*)*(```|~~~)/u;
const headingRe = /^#{1,6}\s+(.+)$/u;
const orderedListRe = /^(\s*(?:>\s*)*)\d+\.(\s+)/u;
const inlineLinkFragmentRe = /\]\((#[^) \t]+)([^)]*)\)/gu;
const definitionFragmentRe = /^(\s*\[[^\]]+\]:\s*)(#[^\s]+)(.*)$/u;

function safeDecodeURIComponent(value) {
  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
}

function convertHeadingToHtmlFragment(headingText) {
  const withoutTrailingHashes = headingText.replace(/\s+#+\s*$/u, "").trim();
  headingAnchorRe.lastIndex = 0;
  const withoutCustomAnchors = withoutTrailingHashes.replace(headingAnchorRe, "").trim();
  headingAnchorRe.lastIndex = 0;

  if (withoutCustomAnchors.length === 0) {
    return "#";
  }

  const slug = withoutCustomAnchors
    .toLowerCase()
    .replace(/[^\p{Letter}\p{Mark}\p{Number}\p{Connector_Punctuation}\- ]/gu, "")
    .replace(/ /gu, "-");

  return `#${encodeURIComponent(slug)}`;
}

function simplifyFragment(fragment) {
  const withoutHash = fragment.startsWith("#") ? fragment.slice(1) : fragment;
  const decoded = safeDecodeURIComponent(withoutHash);

  const simplified = decoded
    .toLowerCase()
    .replace(/[^\p{Letter}\p{Mark}\p{Number}\p{Connector_Punctuation}\- ]/gu, "")
    .replace(/ /gu, "-")
    .replace(/-+/gu, "-");

  return `#${simplified}`;
}

function collectDocumentFragments(lines) {
  const fragments = new Set(["#top"]);
  const counts = new Map();

  let inFence = false;
  for (const rawLine of lines) {
    const trimmed = rawLine.trim();

    if (fencedCodeRe.test(trimmed)) {
      inFence = !inFence;
      continue;
    }

    if (inFence) {
      continue;
    }

    const headingMatch = trimmed.match(headingRe);
    if (!headingMatch) {
      continue;
    }

    const headingText = headingMatch[1];
    const fragment = convertHeadingToHtmlFragment(headingText);

    if (fragment !== "#") {
      const count = counts.get(fragment) || 0;
      if (count > 0) {
        fragments.add(`${fragment}-${count}`);
      }
      fragments.add(fragment);
      counts.set(fragment, count + 1);
    }

    headingAnchorRe.lastIndex = 0;
    let anchorMatch = null;
    while ((anchorMatch = headingAnchorRe.exec(headingText)) !== null) {
      fragments.add(anchorMatch[1]);
    }
    headingAnchorRe.lastIndex = 0;
  }

  return fragments;
}

function buildAliasMap(fragments) {
  const aliasMap = new Map();
  for (const fragment of fragments) {
    const key = simplifyFragment(fragment);
    if (!aliasMap.has(key)) {
      aliasMap.set(key, new Set());
    }
    aliasMap.get(key).add(fragment);
  }
  return aliasMap;
}

function resolveFragment(fragment, fragments, aliasMap) {
  if (fragments.has(fragment) || lineFragmentRe.test(fragment)) {
    return fragment;
  }

  const key = simplifyFragment(fragment);
  const candidates = aliasMap.get(key);
  if (!candidates || candidates.size !== 1) {
    return fragment;
  }

  return Array.from(candidates)[0];
}

function processMarkdownContent(content) {
  const lines = content.replace(/\r\n/g, "\n").replace(/\r/g, "\n").split("\n");
  const fragments = collectDocumentFragments(lines);
  const aliasMap = buildAliasMap(fragments);

  let inFence = false;
  let changed = false;

  for (let i = 0; i < lines.length; i++) {
    const originalLine = lines[i];
    const trimmed = originalLine.trim();

    if (fencedCodeRe.test(trimmed)) {
      inFence = !inFence;
      continue;
    }

    if (inFence) {
      continue;
    }

    let nextLine = originalLine.replace(
      orderedListRe,
      (match, leadingSpace, trailingSpace) => `${leadingSpace}1.${trailingSpace}`
    );
    if (nextLine !== originalLine) {
      changed = true;
    }

    nextLine = nextLine.replace(inlineLinkFragmentRe, (match, fragment, suffix) => {
      const resolved = resolveFragment(fragment, fragments, aliasMap);
      if (resolved === fragment) {
        return match;
      }
      changed = true;
      return `](${resolved}${suffix})`;
    });

    const definitionMatch = nextLine.match(definitionFragmentRe);
    if (definitionMatch) {
      const resolved = resolveFragment(definitionMatch[2], fragments, aliasMap);
      if (resolved !== definitionMatch[2]) {
        nextLine = `${definitionMatch[1]}${resolved}${definitionMatch[3]}`;
        changed = true;
      }
    }

    lines[i] = nextLine;
  }

  return {
    content: lines.join("\n"),
    changed
  };
}

function printHelp() {
  process.stdout.write(
    [
      "Usage: node scripts/fix-md029-md051.js <file1.md> [file2.md] ...",
      "",
      "Normalizes ordered-list prefixes to '1.' (MD029) and rewrites local",
      "fragment links (#section) to match the slugged heading anchors that",
      "GitHub and markdownlint produce (MD051).",
      "",
      "Options:",
      "  -h, --help    Show this message.",
      ""
    ].join("\n")
  );
}

function main(argv) {
  const args = argv || process.argv.slice(2);
  const wantsHelp = args.some((arg) => arg === "-h" || arg === "--help");
  const files = args.filter((arg) => arg !== "-h" && arg !== "--help");

  if (wantsHelp) {
    printHelp();
    return 0;
  }

  if (files.length === 0) {
    return 0;
  }

  for (const relPath of files) {
    const absolutePath = path.resolve(process.cwd(), relPath);

    if (!fs.existsSync(absolutePath) || !fs.statSync(absolutePath).isFile()) {
      continue;
    }

    let source;
    try {
      source = fs.readFileSync(absolutePath, "utf8");
    } catch (error) {
      console.error(`Skipping ${relPath}: ${error.message}`);
      continue;
    }

    const result = processMarkdownContent(source);
    if (!result.changed) {
      continue;
    }

    fs.writeFileSync(absolutePath, result.content);
    console.log(`MD029/MD051 fixed: ${path.relative(process.cwd(), absolutePath)}`);
  }
  return 0;
}

if (typeof module !== "undefined" && module.exports) {
  module.exports = {
    collectDocumentFragments,
    convertHeadingToHtmlFragment,
    processMarkdownContent,
    resolveFragment,
    simplifyFragment,
    main
  };
}

if (require.main === module) {
  process.exit(main(process.argv.slice(2)));
}
