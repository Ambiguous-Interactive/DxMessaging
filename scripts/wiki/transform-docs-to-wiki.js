#!/usr/bin/env node

const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const childProcess = require("child_process");
const { normalizeToLf } = require("../lib/line-endings");
const { walkFiles } = require("../lib/repo-files");

const DOCS_DIR = path.join(__dirname, "..", "..", "docs");
const README_PATH = path.join(__dirname, "..", "..", "README.md");
const OWNERSHIP_FILE = ".dxmessaging-generated-files.json";
// Adopt only byte-identical outputs from the last Wiki state before ownership manifests existed.
const LEGACY_WIKI_COMMIT = "725bf58fca45dd1f823a962f2f8d772cd0b2bd46";
const IMAGE_EXTENSIONS = new Set([".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp"]);

class CodeBlockTracker {
  constructor() {
    this.inCodeBlock = false;
    this.codeBlockDelimiter = null;
  }

  processLine(line) {
    const trimmed = line.trimStart();
    const fenceMatch = trimmed.match(/^(`{3,}|~{3,})(.*)$/);

    if (fenceMatch) {
      const delimiter = fenceMatch[1][0];
      const count = fenceMatch[1].length;
      const info = fenceMatch[2];

      if (!this.inCodeBlock) {
        if (delimiter === "`" && info.includes("`")) {
          return this.inCodeBlock;
        }
        this.inCodeBlock = true;
        this.codeBlockDelimiter = { char: delimiter, count };
      } else if (
        delimiter === this.codeBlockDelimiter.char &&
        count >= this.codeBlockDelimiter.count &&
        info.trim() === ""
      ) {
        this.inCodeBlock = false;
        this.codeBlockDelimiter = null;
      }
    }

    return this.inCodeBlock;
  }
}

function isExternalLink(href) {
  return /^(?:[a-z][a-z0-9+.-]*:|\/\/)/i.test(href);
}

function docsPathToWikiPage(docsPath) {
  let pageName = docsPath.replace(/\\/g, "/").replace(/\.md$/i, "");

  if (pageName.endsWith("/index") || pageName === "index") {
    pageName = pageName.replace(/\/?index$/, "");
    if (!pageName) {
      return "Home";
    }
  }

  if (pageName === "README" || pageName === "../README") {
    return "Home";
  }

  pageName = pageName.replace(/\//g, "-");

  return pageName
    .split("-")
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
    .join("-");
}

function resolveDocsLink(href, currentFilePath) {
  const currentDir = path.posix.dirname(currentFilePath.replace(/\\/g, "/"));
  const normalizedHref = href.replace(/\\/g, "/");
  let target = normalizedHref.startsWith("/")
    ? path.posix.normalize(normalizedHref.replace(/^\/+/, ""))
    : path.posix.normalize(path.posix.join(currentDir, normalizedHref));
  if (target === "docs" || target === "../docs") return "index.md";
  if (target.startsWith("../docs/")) target = target.slice(8);
  if (target.startsWith("docs/")) target = target.slice(5);
  return target || "index.md";
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
      let backtickCount = 0;
      let j = i;
      while (j < line.length && line[j] === "`") {
        backtickCount++;
        j++;
      }
      const delimiter = "`".repeat(backtickCount);
      const endTick = line.indexOf(delimiter, j);
      if (endTick !== -1) {
        i = endTick + backtickCount;
        continue;
      }
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

function transformImagePath(imagePath) {
  if (isExternalLink(imagePath)) {
    return imagePath;
  }
  const localPath = imagePath.split(/[?#]/, 1)[0].replace(/\\/g, "/");
  return `wiki-images/${path.posix.basename(localPath)}`;
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

    if (link.href.startsWith("#")) {
      continue;
    }

    if (!link.href || link.href.startsWith("?")) continue;

    if (link.isImage) {
      const newPath = transformImagePath(link.href);
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

    const queryIndex = href.indexOf("?");
    if (queryIndex !== -1) href = href.substring(0, queryIndex);

    const wikiPage = docsPathToWikiPage(resolveDocsLink(href, currentFilePath));
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
    onError: (error, failedDir) => {
      throw new Error(`Unable to enumerate ${failedDir}: ${error.message}`);
    }
  }).sort();
}

function collectImages(sourceDir, images = new Map()) {
  for (const entry of fs.readdirSync(sourceDir, { withFileTypes: true })) {
    const fullPath = path.join(sourceDir, entry.name);
    if (entry.isDirectory()) collectImages(fullPath, images);
    if (!entry.isFile() || !IMAGE_EXTENSIONS.has(path.extname(entry.name).toLowerCase())) continue;
    const collides = [...images.keys()].some(
      (name) => name.toLowerCase() === entry.name.toLowerCase()
    );
    if (collides) throw new Error(`Image output collision: ${entry.name}`);
    images.set(entry.name, fs.readFileSync(fullPath));
  }
  return images;
}

function createGenerationPlan(docsDir, readmePath) {
  const pages = new Map();
  for (const file of getAllMarkdownFiles(docsDir)) {
    const relativePath = path.relative(docsDir, file).replace(/\\/g, "/");
    const name = `${docsPathToWikiPage(relativePath)}.md`;
    const collides = [...pages.keys()].some((page) => page.toLowerCase() === name.toLowerCase());
    if (collides || name.startsWith("_")) throw new Error(`Page output collision: ${name}`);
    pages.set(name, transformFile(fs.readFileSync(file, "utf8"), relativePath));
  }
  if (!pages.has("Home.md")) {
    if (!fs.existsSync(readmePath)) throw new Error(`README missing: ${readmePath}`);
    pages.set("Home.md", transformFile(fs.readFileSync(readmePath, "utf8"), "../README.md"));
  }
  return { pages, images: collectImages(docsDir) };
}

function gitBlobId(content) {
  const header = Buffer.from(`blob ${content.length}\0`);
  return crypto.createHash("sha1").update(header).update(content).digest("hex");
}

function isGeneratedName(name, kind) {
  if (/[\\/]/.test(name)) return false;
  return kind === "pages"
    ? name.endsWith(".md") && !name.startsWith("_")
    : IMAGE_EXTENSIONS.has(path.extname(name).toLowerCase());
}

function validateOwnership(manifest) {
  if (manifest.version !== 1 || !Array.isArray(manifest.pages) || !Array.isArray(manifest.images)) {
    throw new Error("Invalid generated-file ownership manifest");
  }
  const pages = new Set(manifest.pages);
  const images = new Set(manifest.images);
  if (
    pages.size !== manifest.pages.length ||
    images.size !== manifest.images.length ||
    [...pages].some((name) => !isGeneratedName(name, "pages")) ||
    [...images].some((name) => !isGeneratedName(name, "images"))
  ) {
    throw new Error("Unsafe generated-file ownership manifest");
  }
  return { pages, images };
}

function readOwnership(wikiDir) {
  const manifestPath = path.join(wikiDir, OWNERSHIP_FILE);
  if (fs.existsSync(manifestPath)) {
    return validateOwnership(JSON.parse(fs.readFileSync(manifestPath, "utf8")));
  }
  const ownership = { pages: new Set(), images: new Set() };
  if (!fs.existsSync(path.join(wikiDir, ".git"))) return ownership;
  const tree = childProcess.execFileSync("git", ["ls-tree", "-r", "-z", LEGACY_WIKI_COMMIT], {
    cwd: wikiDir,
    encoding: "utf8"
  });
  for (const entry of tree.split("\0").filter(Boolean)) {
    const match = entry.match(/^[^ ]+ blob ([a-f0-9]{40})\t(.+)$/);
    if (!match) continue;
    const [, expectedHash, relativePath] = match;
    const isImage = relativePath.startsWith("wiki-images/");
    const kind = isImage ? "images" : "pages";
    const name = path.basename(relativePath);
    const expectedPath = isImage ? `wiki-images/${name}` : name;
    if (relativePath !== expectedPath || !isGeneratedName(name, kind)) continue;
    const filePath = path.join(wikiDir, ...relativePath.split("/"));
    if (!fs.existsSync(filePath) || gitBlobId(fs.readFileSync(filePath)) !== expectedHash) continue;
    ownership[kind].add(name);
  }
  return ownership;
}

function outputPath(wikiDir, kind, name) {
  return kind === "pages" ? path.join(wikiDir, name) : path.join(wikiDir, "wiki-images", name);
}

function rejectSymlink(target) {
  const stats = fs.lstatSync(target, { throwIfNoEntry: false });
  if (stats?.isSymbolicLink()) throw new Error(`Managed wiki path is a symbolic link: ${target}`);
}

function validateOutputs(wikiDir, plan, ownership) {
  for (const kind of ["pages", "images"]) {
    const names = new Set([...plan[kind].keys(), ...ownership[kind]]);
    for (const name of names) {
      const target = outputPath(wikiDir, kind, name);
      rejectSymlink(target);
      if (plan[kind].has(name) && fs.existsSync(target) && !ownership[kind].has(name)) {
        throw new Error(`Refusing to overwrite wiki-owned file: ${target}`);
      }
      if (ownership[kind].has(name) && fs.existsSync(target) && !fs.statSync(target).isFile())
        throw new Error(`Owned output is not a file: ${target}`);
    }
  }
}

function processAllFiles(wikiDir, docsDir = DOCS_DIR, readmePath = README_PATH) {
  const plan = createGenerationPlan(docsDir, readmePath);
  for (const target of [
    wikiDir,
    path.join(wikiDir, "wiki-images"),
    path.join(wikiDir, OWNERSHIP_FILE)
  ])
    rejectSymlink(target);
  const ownership = readOwnership(wikiDir);
  validateOutputs(wikiDir, plan, ownership);
  fs.mkdirSync(path.join(wikiDir, "wiki-images"), { recursive: true });
  for (const kind of ["pages", "images"]) {
    for (const [name, content] of plan[kind]) {
      fs.writeFileSync(outputPath(wikiDir, kind, name), content);
    }
    for (const name of ownership[kind]) {
      const target = outputPath(wikiDir, kind, name);
      if (!plan[kind].has(name) && fs.existsSync(target)) {
        fs.unlinkSync(target);
      }
    }
  }
  const manifest = {
    version: 1,
    pages: [...plan.pages.keys()].sort(),
    images: [...plan.images.keys()].sort()
  };
  fs.writeFileSync(path.join(wikiDir, OWNERSHIP_FILE), `${JSON.stringify(manifest, null, 2)}\n`);
  return { generatedPages: new Set(manifest.pages), generatedImages: new Set(manifest.images) };
}

if (require.main === module) {
  const outputDirectory = process.argv[2];
  if (!outputDirectory) {
    console.error("Usage: node transform-docs-to-wiki.js <output-wiki-dir>");
    process.exit(1);
  } else {
    try {
      processAllFiles(path.resolve(outputDirectory));
    } catch (error) {
      console.error("Error transforming docs to wiki:", error.message);
      process.exit(1);
    }
  }
}

module.exports = {
  isExternalLink,
  docsPathToWikiPage,
  findMarkdownLinks,
  CodeBlockTracker,
  transformLine,
  transformFile,
  processAllFiles,
  gitBlobId,
  LEGACY_WIKI_COMMIT
};
