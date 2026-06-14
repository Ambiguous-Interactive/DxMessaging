#!/usr/bin/env node

/**
 * Transform documentation from docs/ format to GitHub Wiki format.
 *
 * Handles:
 * - Markdown link conversion to [[WikiLinks]]
 * - Code block preservation (no transforms inside ```)
 * - Anchor/section link handling
 * - External link preservation
 * - Image path transformation
 * - README → Home special case
 * - Nested brackets and escaped characters
 *
 * Usage: node scripts/wiki/transform-docs-to-wiki.js <output-wiki-dir>
 */

const fs = require("fs");
const path = require("path");
const { normalizeToLf } = require("../lib/line-endings");
const { walkFiles } = require("../lib/repo-files");

const DOCS_DIR = path.join(__dirname, "..", "..", "docs");

/**
 * State machine for code block detection
 */
class CodeBlockTracker {
  constructor() {
    this.inCodeBlock = false;
    this.codeBlockDelimiter = null;
  }

  processLine(line) {
    const trimmed = line.trimStart();
    const fenceMatch = trimmed.match(/^(`{3,}|~{3,})(\w*)?$/);

    if (fenceMatch) {
      const delimiter = fenceMatch[1][0];
      const count = fenceMatch[1].length;

      if (!this.inCodeBlock) {
        this.inCodeBlock = true;
        this.codeBlockDelimiter = { char: delimiter, count };
      } else if (
        delimiter === this.codeBlockDelimiter.char &&
        count >= this.codeBlockDelimiter.count
      ) {
        this.inCodeBlock = false;
        this.codeBlockDelimiter = null;
      }
    }

    return this.inCodeBlock;
  }

  reset() {
    this.inCodeBlock = false;
    this.codeBlockDelimiter = null;
  }
}

function isExternalLink(href) {
  return /^(https?:\/\/|mailto:|tel:|ftp:)/i.test(href);
}

function isAnchorOnlyLink(href) {
  return href.startsWith("#");
}

function docsPathToWikiPage(docsPath) {
  let pageName = docsPath.replace(/\.md$/i, "");

  if (pageName.endsWith("/index") || pageName === "index") {
    pageName = pageName.replace(/\/?index$/, "");
    if (!pageName) {
      return "Home";
    }
  }

  // Handle README BEFORE slash replacement
  if (pageName === "README" || pageName === "../README") {
    return "Home";
  }

  // NOW replace slashes
  pageName = pageName.replace(/\//g, "-");

  return pageName
    .split("-")
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
    .join("-");
}

function findMarkdownLinks(line) {
  const links = [];
  let i = 0;

  while (i < line.length) {
    if (line[i] === "\\" && i + 1 < line.length) {
      i += 2;
      continue;
    }

    if (line[i] === "`") {
      // Per CommonMark spec:
      // - Code spans open with N backticks and close with exactly N backticks
      // - If no matching closing delimiter is found, the backticks are literal text
      // - Empty spans like `` `` (two backticks, space, two backticks) are valid
      // Count consecutive backticks to determine delimiter length
      let backtickCount = 0;
      let j = i;
      while (j < line.length && line[j] === "`") {
        backtickCount++;
        j++;
      }
      // Find closing delimiter with same backtick count
      const delimiter = "`".repeat(backtickCount);
      const endTick = line.indexOf(delimiter, j);
      if (endTick !== -1) {
        i = endTick + backtickCount;
        continue;
      }
      // No matching closing delimiter, skip the opening backticks
      i = j;
      continue;
    }

    const isImage = line[i] === "!" && line[i + 1] === "[";
    const linkStart = isImage ? i : line[i] === "[" ? i : -1;

    if (linkStart === -1) {
      i++;
      continue;
    }

    const bracketStart = isImage ? i + 1 : i;

    let depth = 0;
    let bracketEnd = -1;
    for (let j = bracketStart; j < line.length; j++) {
      if (line[j] === "\\" && j + 1 < line.length) {
        j++;
        continue;
      }
      if (line[j] === "[") depth++;
      if (line[j] === "]") {
        depth--;
        if (depth === 0) {
          bracketEnd = j;
          break;
        }
      }
    }

    if (bracketEnd === -1 || bracketEnd + 1 >= line.length || line[bracketEnd + 1] !== "(") {
      i++;
      continue;
    }

    const parenStart = bracketEnd + 1;
    depth = 0;
    let parenEnd = -1;
    for (let j = parenStart; j < line.length; j++) {
      if (line[j] === "\\" && j + 1 < line.length) {
        j++;
        continue;
      }
      if (line[j] === "(") depth++;
      if (line[j] === ")") {
        depth--;
        if (depth === 0) {
          parenEnd = j;
          break;
        }
      }
    }

    if (parenEnd === -1) {
      i++;
      continue;
    }

    const fullMatch = line.substring(linkStart, parenEnd + 1);
    const text = line.substring(bracketStart + 1, bracketEnd);
    const href = line.substring(parenStart + 1, parenEnd);

    links.push({
      match: fullMatch,
      index: linkStart,
      text,
      href,
      isImage
    });

    i = parenEnd + 1;
  }

  return links;
}

function transformImagePath(imagePath, currentFilePath) {
  if (isExternalLink(imagePath)) {
    return imagePath;
  }

  const currentDir = path.dirname(currentFilePath);
  const resolvedPath = path.resolve(DOCS_DIR, currentDir, imagePath);
  const baseName = path.basename(resolvedPath);
  return `wiki-images/${baseName}`;
}

function transformLine(line, currentFilePath) {
  const links = findMarkdownLinks(line);

  if (links.length === 0) {
    return line;
  }

  let result = line;
  for (let i = links.length - 1; i >= 0; i--) {
    const link = links[i];

    if (isExternalLink(link.href)) {
      continue;
    }

    if (isAnchorOnlyLink(link.href)) {
      continue;
    }

    if (link.isImage) {
      const newPath = transformImagePath(link.href, currentFilePath);
      const replacement = `![${link.text}](${newPath})`;
      result =
        result.substring(0, link.index) +
        replacement +
        result.substring(link.index + link.match.length);
      continue;
    }

    let href = link.href;
    let anchor = "";
    const anchorIndex = href.indexOf("#");
    if (anchorIndex !== -1) {
      anchor = href.substring(anchorIndex + 1);
      href = href.substring(0, anchorIndex);
    }

    const wikiPage = docsPathToWikiPage(href);
    let wikiLink;

    if (anchor) {
      wikiLink = `[[${wikiPage}#${anchor}|${link.text}]]`;
    } else if (link.text !== wikiPage && link.text !== "") {
      wikiLink = `[[${wikiPage}|${link.text}]]`;
    } else {
      wikiLink = `[[${wikiPage}]]`;
    }

    result =
      result.substring(0, link.index) + wikiLink + result.substring(link.index + link.match.length);
  }

  return result;
}

function transformFile(content, filePath) {
  const lines = normalizeToLf(content).split("\n");
  const tracker = new CodeBlockTracker();
  const result = [];

  for (const line of lines) {
    const wasInCodeBlock = tracker.inCodeBlock;
    const inCodeBlock = tracker.processLine(line);

    if (inCodeBlock || wasInCodeBlock) {
      result.push(line);
    } else {
      result.push(transformLine(line, filePath));
    }
  }

  return result.join("\n");
}

function getAllMarkdownFiles(dir) {
  return walkFiles(dir, {
    match: (fullPath) => fullPath.endsWith(".md"),
    excludeDir: (fullPath, entry) => entry.name.startsWith(".") || entry.name === "includes",
    onError: (error, failedDir) =>
      console.warn(`Warning: Unable to read directory ${failedDir}: ${error.message}`)
  });
}

function copyImages(sourceDir, targetDir, copiedImages = new Map()) {
  const imageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp"];
  let entries;
  try {
    entries = fs.readdirSync(sourceDir, { withFileTypes: true });
  } catch (error) {
    console.warn(`Warning: Unable to read directory ${sourceDir}: ${error.message}`);
    return copiedImages;
  }

  for (const entry of entries) {
    const fullPath = path.join(sourceDir, entry.name);
    if (entry.isDirectory()) {
      copyImages(fullPath, targetDir, copiedImages);
    } else {
      const ext = path.extname(entry.name).toLowerCase();
      if (imageExtensions.includes(ext)) {
        const targetPath = path.join(targetDir, entry.name);
        if (copiedImages.has(entry.name)) {
          const previousSource = copiedImages.get(entry.name);
          console.warn(`WARNING: Image filename collision: "${entry.name}"`);
          console.warn(`  Previous: ${previousSource}`);
          console.warn(`  Current:  ${fullPath}`);
          console.warn(`  The current file will overwrite the previous one.`);
        }
        fs.copyFileSync(fullPath, targetPath);
        copiedImages.set(entry.name, fullPath);
        console.log(`Copied image: ${entry.name}`);
      }
    }
  }
  return copiedImages;
}

function processAllFiles(wikiDir) {
  if (!fs.existsSync(wikiDir)) {
    fs.mkdirSync(wikiDir, { recursive: true });
  }

  const imagesDir = path.join(wikiDir, "wiki-images");
  if (!fs.existsSync(imagesDir)) {
    fs.mkdirSync(imagesDir, { recursive: true });
  }

  const files = getAllMarkdownFiles(DOCS_DIR);
  const processedPages = new Set();

  for (const file of files) {
    const relativePath = path.relative(DOCS_DIR, file);
    const content = fs.readFileSync(file, "utf-8");
    const transformed = transformFile(content, relativePath);
    const wikiPageName = docsPathToWikiPage(relativePath);

    if (processedPages.has(wikiPageName)) {
      console.warn(`WARNING: Duplicate wiki page name: ${wikiPageName} (from ${relativePath})`);
      continue;
    }

    const outputPath = path.join(wikiDir, `${wikiPageName}.md`);
    fs.writeFileSync(outputPath, transformed);
    processedPages.add(wikiPageName);
    console.log(`Transformed: ${relativePath} → ${wikiPageName}.md`);
  }

  copyImages(DOCS_DIR, imagesDir);

  if (!processedPages.has("Home")) {
    const readmePath = path.join(__dirname, "..", "..", "README.md");
    if (fs.existsSync(readmePath)) {
      const readmeContent = fs.readFileSync(readmePath, "utf-8");
      const transformed = transformFile(readmeContent, "../README.md");
      fs.writeFileSync(path.join(wikiDir, "Home.md"), transformed);
      console.log("Created Home.md from README.md");
    }
  }

  console.log(`\nProcessed ${processedPages.size} wiki pages`);
}

// Main execution
function main() {
  const args = process.argv.slice(2);
  if (args.length < 1) {
    console.error("Usage: node transform-docs-to-wiki.js <output-wiki-dir>");
    process.exit(1);
  }

  const wikiDir = path.resolve(args[0]);
  console.log(`Transforming docs to wiki format...`);
  console.log(`Source: ${DOCS_DIR}`);
  console.log(`Target: ${wikiDir}\n`);

  processAllFiles(wikiDir);
}

// Only run main when executed directly (not when required as a module)
if (require.main === module) {
  try {
    main();
  } catch (error) {
    console.error("Error transforming docs to wiki:", error.message);
    process.exit(1);
  }
}

// Export functions for testing
module.exports = {
  isExternalLink,
  docsPathToWikiPage,
  findMarkdownLinks,
  CodeBlockTracker,
  transformLine,
  transformFile
};
