#!/usr/bin/env node
/**
 * generate-skills-index.js
 *
 * Auto-generates index.md from all skill files in .llm/skills/.
 * Scans for markdown files with YAML frontmatter and produces a categorized,
 * searchable index with complexity badges, status indicators, and tag clouds.
 *
 * @usage
 *   node scripts/generate-skills-index.js
 *   node scripts/generate-skills-index.js --check
 *
 * @exitcodes
 *   0 - Success, index.md generated successfully
 *   1 - Error occurred during generation (e.g., file access issues)
 *
 * @example
 *   # Run from repository root
 *   node scripts/generate-skills-index.js
 *
 *   # Run from any directory
 *   node /path/to/repo/scripts/generate-skills-index.js
 */

const fs = require("fs");
const path = require("path");
const { stripMatchingBoundaryQuotes, normalizeToLf } = require("./lib/quote-parser");
const { getPinnedPrettierSpec } = require("./lib/prettier-version");
const { loadLocalPrettier, runBundledNpxCommand } = require("./lib/managed-prettier");

const SKILLS_DIR = path.join(__dirname, "..", ".llm", "skills");
const INDEX_PATH = path.join(SKILLS_DIR, "index.md");
const EXCLUDED_FILES = ["index.md", "specification.md"];
const EXCLUDED_DIRS = ["templates"];
const REPO_ROOT = path.join(__dirname, "..");
const PRETTIER_LOCAL_BIN = path.join(REPO_ROOT, "node_modules", "prettier", "bin", "prettier.cjs");
const PRETTIER_LOCAL_MODULE = path.join(REPO_ROOT, "node_modules", "prettier", "index.cjs");

/**
 * Brand name capitalization mapping.
 * Maps lowercase words to their properly capitalized forms.
 */
const BRAND_NAMES = {
  github: "GitHub",
  javascript: "JavaScript",
  typescript: "TypeScript",
  nodejs: "Node.js",
  csharp: "C#",
  dotnet: ".NET",
  nuget: "NuGet",
  npm: "npm",
  api: "API",
  apis: "APIs",
  cli: "CLI",
  json: "JSON",
  yaml: "YAML",
  xml: "XML",
  html: "HTML",
  css: "CSS",
  sql: "SQL",
  url: "URL",
  urls: "URLs",
  uri: "URI",
  uris: "URIs",
  http: "HTTP",
  https: "HTTPS",
  rest: "REST",
  graphql: "GraphQL",
  oauth: "OAuth",
  jwt: "JWT",
  sdk: "SDK",
  ide: "IDE",
  vscode: "VS Code",
  visualstudio: "Visual Studio",
  macos: "macOS",
  ios: "iOS",
  webgl: "WebGL",
  opengl: "OpenGL",
  directx: "DirectX",
  llm: "LLM",
  ai: "AI",
  ml: "ML",
  ci: "CI",
  cd: "CD"
};

/**
 * Apply proper brand name capitalization to a word.
 * Returns the properly capitalized form if it's a known brand,
 * otherwise returns the word with standard title case.
 */
function applyBrandCapitalization(word) {
  const lowerWord = word.toLowerCase();
  if (BRAND_NAMES[lowerWord]) {
    return BRAND_NAMES[lowerWord];
  }
  // Standard title case: capitalize first letter
  return word.charAt(0).toUpperCase() + word.slice(1).toLowerCase();
}

/**
 * Convert a category slug (e.g., "github-actions") to a properly
 * capitalized title (e.g., "GitHub Actions").
 */
function categoryToTitle(category) {
  return category
    .split("-")
    .map((word) => applyBrandCapitalization(word))
    .join(" ");
}

function getLatestSkillDate(skills) {
  const dates = skills
    .map((skill) => skill.updated || skill.created)
    .filter((date) => typeof date === "string" && /^\d{4}-\d{2}-\d{2}$/.test(date))
    .sort();

  if (dates.length > 0) {
    return dates[dates.length - 1];
  }

  return new Date().toISOString().split("T")[0];
}

async function formatWithPrettier(content, options = {}) {
  const {
    loadLocalPrettierFn = loadLocalPrettier,
    runBundledNpxCommandFn = runBundledNpxCommand,
    getPinnedPrettierSpecFn = getPinnedPrettierSpec,
    prettierModulePath = PRETTIER_LOCAL_MODULE,
    prettierBinPath = PRETTIER_LOCAL_BIN,
    indexPath = INDEX_PATH,
    repoRoot = REPO_ROOT
  } = options;

  const prettier = loadLocalPrettierFn({
    localPrettierModulePath: prettierModulePath,
    localPrettierBinPath: prettierBinPath
  });

  if (prettier) {
    const optionsFromConfig = await prettier.resolveConfig(indexPath, { editorconfig: true });
    const fileInfo = await prettier.getFileInfo(indexPath, {
      resolveConfig: false
    });

    if (fileInfo.ignored) {
      return content;
    }

    return prettier.format(content, {
      ...(optionsFromConfig || {}),
      filepath: indexPath
    });
  }

  const prettierSpec = getPinnedPrettierSpecFn();
  const result = runBundledNpxCommandFn(
    ["--yes", `--package=${prettierSpec}`, "prettier", "--stdin-filepath", indexPath],
    {
      cwd: repoRoot,
      input: content,
      encoding: "utf8"
    }
  );

  if (result.error) {
    throw result.error;
  }

  if (result.status !== 0) {
    const details = result.stderr
      ? result.stderr.trim()
      : result.stdout
        ? result.stdout.trim()
        : "Unknown error";
    throw new Error(`Prettier fallback via bundled npm npx-cli.js failed: ${details}`);
  }

  return typeof result.stdout === "string" ? result.stdout : content;
}

/**
 * Parse YAML frontmatter from markdown file content.
 * Uses a stack-based approach to handle arbitrary nesting depth.
 * Returns null if no valid frontmatter found.
 */
function parseFrontmatter(content) {
  const normalizedContent = normalizeToLf(content);
  const match = normalizedContent.match(/^---\n([\s\S]*?)\n---/);
  if (!match) {
    return null;
  }

  const yaml = match[1];
  const result = {};
  const lines = yaml.split("\n");

  // Stack-based parser for arbitrary nesting depth
  // Each stack entry: { obj, key, indent, isArray }
  const contextStack = [{ obj: result, key: null, indent: -1, isArray: false }];

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];

    // Skip empty lines and comments
    if (!line.trim() || line.trim().startsWith("#")) {
      continue;
    }

    const trimmed = line.trim();
    const indent = line.search(/\S/);

    // Pop stack until we find the right context level
    while (contextStack.length > 1 && contextStack[contextStack.length - 1].indent >= indent) {
      contextStack.pop();
    }

    const currentContext = contextStack[contextStack.length - 1];

    // Handle array items
    if (trimmed.startsWith("- ")) {
      const arrayValue = trimmed.slice(2).trim();

      // Check if this is an object item (has nested content or is "key: value")
      const colonIndex = arrayValue.indexOf(":");
      if (colonIndex > 0 && !arrayValue.startsWith('"') && !arrayValue.startsWith("'")) {
        // Array of objects - parse as key: value
        const itemKey = arrayValue.slice(0, colonIndex).trim();
        let itemValue = stripMatchingBoundaryQuotes(arrayValue.slice(colonIndex + 1).trim());

        // Check if we need to create a new object in the array
        if (currentContext.isArray) {
          // Create new object and add to array
          const newObj = {};
          newObj[itemKey] = itemValue;
          currentContext.obj.push(newObj);
          // Push this object onto the stack for potential nested properties
          contextStack.push({ obj: newObj, key: itemKey, indent: indent + 2, isArray: false });
        }
      } else {
        // Simple array value
        const value = stripMatchingBoundaryQuotes(arrayValue);
        if (currentContext.isArray) {
          currentContext.obj.push(value);
        }
      }
      continue;
    }

    // Handle key: value pairs
    const colonIndex = trimmed.indexOf(":");
    if (colonIndex === -1) {
      continue;
    }

    const key = trimmed.slice(0, colonIndex).trim();
    let value = trimmed.slice(colonIndex + 1).trim();

    if (value === "" || value === "[]") {
      // Check if next line starts an array or is nested
      const nextLine = i < lines.length - 1 ? lines[i + 1] : "";
      const nextTrimmed = nextLine.trim();

      if (nextTrimmed.startsWith("- ")) {
        // This key starts an array
        currentContext.obj[key] = [];
        contextStack.push({
          obj: currentContext.obj[key],
          key: key,
          indent: indent,
          isArray: true
        });
      } else if (nextLine && nextLine.search(/\S/) > indent) {
        // This key starts a nested object
        currentContext.obj[key] = {};
        contextStack.push({
          obj: currentContext.obj[key],
          key: key,
          indent: indent,
          isArray: false
        });
      } else {
        // Empty value
        currentContext.obj[key] = value === "[]" ? [] : "";
      }
    } else {
      // Simple key: value
      value = stripMatchingBoundaryQuotes(value);
      currentContext.obj[key] = value;
    }
  }

  return result;
}

/**
 * Recursively find all skill files.
 */
function findSkillFiles(dir, baseDir = dir) {
  const skills = [];

  if (!fs.existsSync(dir)) {
    return skills;
  }

  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch (error) {
    console.warn(`Warning: Unable to read directory ${dir}: ${error.message}`);
    return skills;
  }

  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);

    if (entry.isDirectory()) {
      if (!EXCLUDED_DIRS.includes(entry.name)) {
        skills.push(...findSkillFiles(fullPath, baseDir));
      }
    } else if (entry.isFile() && entry.name.endsWith(".md")) {
      if (!EXCLUDED_FILES.includes(entry.name)) {
        const relativePath = path.relative(baseDir, fullPath);
        const category = path.dirname(relativePath);

        if (category !== ".") {
          skills.push({
            path: fullPath,
            relativePath: relativePath,
            category: category,
            filename: entry.name
          });
        }
      }
    }
  }

  return skills;
}

/**
 * Load skill metadata from file.
 * Returns null if file cannot be read or has no valid frontmatter.
 */
function loadSkill(skillFile) {
  let content;
  try {
    content = fs.readFileSync(skillFile.path, "utf8");
  } catch (error) {
    console.error(`Error reading ${skillFile.relativePath}: ${error.message}`);
    return null;
  }

  const lineCount = normalizeToLf(content).split("\n").length;
  const frontmatter = parseFrontmatter(content);

  if (!frontmatter) {
    console.warn(`Warning: No frontmatter in ${skillFile.relativePath}`);
    return null;
  }

  return {
    ...skillFile,
    ...frontmatter,
    lineCount: lineCount
  };
}

/**
 * Get complexity badge.
 */
function getComplexityBadge(level) {
  const badges = {
    basic: "[basic]",
    intermediate: "[intermediate]",
    advanced: "[advanced]",
    expert: "[expert]"
  };
  return badges[level] || level;
}

/**
 * Get status badge.
 */
function getStatusBadge(status) {
  const badges = {
    draft: "[draft]",
    review: "[review]",
    stable: "[stable]",
    deprecated: "[deprecated]"
  };
  return badges[status] || status;
}

/**
 * Get impact indicator.
 */
function getImpactIndicator(rating) {
  const indicators = {
    none: "[risk: none]",
    low: "[risk: low]",
    medium: "[risk: medium]",
    high: "[risk: high]",
    critical: "[risk: critical]"
  };
  return indicators[rating] || "?";
}

/**
 * Get line size indicator based on repository .llm limits.
 * < 120: [draft]
 * 120-260: [ok]
 * 261-300: [warn]
 * > 300: [over]
 */
function getLineSizeIndicator(lineCount) {
  if (typeof lineCount !== "number") {
    return "?";
  }
  if (lineCount > 300) {
    return "[over]";
  }
  if (lineCount > 260) {
    return "[warn]";
  }
  if (lineCount >= 120) {
    return "[ok]";
  }
  return "[draft]";
}

/**
 * Generate the index content.
 */
function generateIndex(skills) {
  const indexDate = getLatestSkillDate(skills);

  // Group by category
  const byCategory = {};
  for (const skill of skills) {
    const cat = skill.category || "uncategorized";
    if (!byCategory[cat]) {
      byCategory[cat] = [];
    }
    byCategory[cat].push(skill);
  }

  // Sort categories and skills within
  const sortedCategories = Object.keys(byCategory).sort();
  for (const cat of sortedCategories) {
    byCategory[cat].sort((a, b) => (a.title || "").localeCompare(b.title || ""));
  }

  // Build compact index
  let content = `# Skills Index

> **Auto-generated** on ${indexDate}. Do not edit manually.
> Run \`node scripts/generate-skills-index.js\` to regenerate.

---

## Summary

| Metric | Value |
|--------|-------|
| Total Skills | ${skills.length} |
| Categories | ${sortedCategories.length} |

---

## Table of Contents

`;

  for (const cat of sortedCategories) {
    const catTitle = categoryToTitle(cat);
    content += `- [${catTitle}](#${cat}) (${byCategory[cat].length})\n`;
  }

  content += `\n---\n\n`;

  // Category sections
  for (const cat of sortedCategories) {
    const catTitle = categoryToTitle(cat);
    content += `## ${catTitle}\n\n`;

    content += `| Skill | Lines | Complexity | Status | Performance | Tags |\n`;
    content += `|-------|-------|------------|--------|-------------|------|\n`;

    for (const skill of byCategory[cat]) {
      const title = skill.title || skill.filename;
      const link = `./${skill.relativePath.replace(/\\/g, "/")}`;
      const lineCount = skill.lineCount || "?";
      const lineIndicator = getLineSizeIndicator(skill.lineCount);
      const complexity = skill.complexity?.level ? getComplexityBadge(skill.complexity.level) : "?";
      const status = skill.status ? getStatusBadge(skill.status) : "?";
      const perfImpact = skill.impact?.performance?.rating
        ? getImpactIndicator(skill.impact.performance.rating)
        : "?";
      const tags = Array.isArray(skill.tags) ? skill.tags.slice(0, 2).join(", ") : "";

      content += `| [${title}](${link}) | ${lineIndicator} ${lineCount} | ${complexity} | ${status} | ${perfImpact} | ${tags} |\n`;
    }

    content += `\n`;
  }

  content += `---\n\n_Generated by \`scripts/generate-skills-index.js\`_\n`;

  return content;
}

/**
 * Main entry point.
 */
async function main() {
  const args = process.argv.slice(2);
  const checkOnly = args.includes("--check");

  console.log("Scanning for skills in", SKILLS_DIR);

  const skillFiles = findSkillFiles(SKILLS_DIR);
  console.log(`Found ${skillFiles.length} skill files`);

  const skills = skillFiles.map(loadSkill).filter((s) => s !== null);
  console.log(`Loaded ${skills.length} skills with valid frontmatter`);

  const indexContent = generateIndex(skills);
  let formattedContent;

  try {
    formattedContent = await formatWithPrettier(indexContent);
  } catch (error) {
    console.error(`Failed to format index with Prettier: ${error.message}`);
    return 1;
  }

  const normalizedContent = normalizeToLf(formattedContent);

  if (checkOnly) {
    let existingContent = null;
    try {
      existingContent = fs.readFileSync(INDEX_PATH, "utf8");
    } catch (error) {
      console.error(`Unable to read ${INDEX_PATH}: ${error.message}`);
      return 1;
    }

    if (existingContent !== normalizedContent) {
      console.error("index.md is out of date. Run node scripts/generate-skills-index.js");
      return 1;
    }

    console.log("Skills index is up to date.");
    return 0;
  }

  fs.writeFileSync(INDEX_PATH, normalizedContent, "utf8");
  console.log(`Generated ${INDEX_PATH}`);

  return 0;
}

// Export for testing when required as a module
if (typeof module !== "undefined" && module.exports) {
  module.exports = {
    applyBrandCapitalization,
    categoryToTitle,
    formatWithPrettier,
    getLineSizeIndicator,
    parseFrontmatter,
    BRAND_NAMES
  };
}

// Only run main when executed directly (not when required as a module)
if (require.main === module) {
  main().then(
    (code) => process.exit(code),
    (error) => {
      console.error(`generate-skills-index: fatal error: ${error.stack || error.message}`);
      process.exit(1);
    }
  );
}
