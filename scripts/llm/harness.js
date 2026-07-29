#!/usr/bin/env node
"use strict";

/**
 * The `.llm` harness: validation, index generation, and agent-visible skill mirrors.
 *
 *   node scripts/llm/harness.js index      Regenerate .llm/index.json, .llm/index.md, mirrors
 *   node scripts/llm/harness.js validate   Check Agent Skills conformance and line limits
 *   node scripts/llm/harness.js check      validate + fail on any stale generated artifact
 *
 * `.llm/skills/<name>/SKILL.md` is the single source of truth. Claude Code reads `.claude/skills/`,
 * Codex reads `.agents/skills/`, and Copilot reads both, so each skill is mirrored into those two
 * locations as a thin pointer rather than duplicated.
 */

const fs = require("fs");
const path = require("path");
const YAML = require("yaml");

const ROOT = path.resolve(__dirname, "..", "..");
const LLM_DIR = path.join(ROOT, ".llm");
const SKILLS_DIR = path.join(LLM_DIR, "skills");
const CONTEXT_FILE = path.join(LLM_DIR, "context.md");
const INDEX_MD = path.join(LLM_DIR, "index.md");
const INDEX_JSON = path.join(LLM_DIR, "index.json");
const MIRROR_ROOTS = [path.join(ROOT, ".claude", "skills"), path.join(ROOT, ".agents", "skills")];

const BLOCK_START = "<!-- BEGIN GENERATED SKILL REGISTRY -->";
const BLOCK_END = "<!-- END GENERATED SKILL REGISTRY -->";

// Lines-per-file limits. `SKILL.md` is what an agent loads whole on activation, so it is capped
// hard; overflow belongs in `references/`, which are loaded only on demand and so are allowed the
// Agent Skills spec's own 500-line ceiling.
const LIMITS = {
  skillWarn: 150,
  skillFail: 200,
  referenceFail: 500,
  contextFail: 300,
  lineLength: 2000,
};

const NAME_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

// Required and optional keys from https://agentskills.io/specification, plus the client extensions
// Claude Code and VS Code document. Anything else belongs under `metadata`.
const REQUIRED_KEYS = ["name", "description"];
const SPEC_KEYS = ["name", "description", "license", "compatibility", "metadata", "allowed-tools"];
const CLIENT_EXTENSION_KEYS = [
  "argument-hint",
  "user-invocable",
  "disable-model-invocation",
  "disallowed-tools",
  "context",
  "agent",
  "model",
];
const ALLOWED_KEYS = new Set([...SPEC_KEYS, ...CLIENT_EXTENSION_KEYS]);

function readUtf8(filePath) {
  return fs.readFileSync(filePath, "utf8");
}

function countLines(text) {
  if (text.length === 0) {
    return 0;
  }
  const lines = text.split(/\r\n|\n|\r/);
  return lines[lines.length - 1] === "" ? lines.length - 1 : lines.length;
}

function longestLine(text) {
  return text.split(/\r\n|\n|\r/).reduce((longest, line) => Math.max(longest, line.length), 0);
}

function toPosix(filePath) {
  return filePath.split(path.sep).join("/");
}

function parseFrontmatter(text) {
  const match = /^---\r?\n([\s\S]*?)\r?\n---\r?\n?/.exec(text);
  if (!match) {
    return { data: undefined, body: text, error: "missing YAML frontmatter" };
  }
  try {
    const data = YAML.parse(match[1]) ?? {};
    if (!data || typeof data !== "object" || Array.isArray(data)) {
      return { data: undefined, body: "", error: "frontmatter must be a mapping" };
    }
    return { data, body: text.slice(match[0].length), error: undefined };
  } catch (error) {
    return { data: undefined, body: "", error: `invalid YAML frontmatter: ${error.message}` };
  }
}

function listSkillDirectories() {
  if (!fs.existsSync(SKILLS_DIR)) {
    return [];
  }
  return fs
    .readdirSync(SKILLS_DIR, { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => entry.name)
    .sort((left, right) => left.localeCompare(right, "en"));
}

function walk(directory) {
  const results = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      results.push(...walk(full));
    } else if (entry.isFile()) {
      results.push(full);
    }
  }
  return results;
}

function buildManifest() {
  const skills = [];
  for (const name of listSkillDirectories()) {
    const directory = path.join(SKILLS_DIR, name);
    const skillFile = path.join(directory, "SKILL.md");
    const record = {
      name,
      directory: `.llm/skills/${name}`,
      path: `.llm/skills/${name}/SKILL.md`,
      exists: fs.existsSync(skillFile),
      description: "",
      metadata: {},
      allowedTools: undefined,
      lineCount: 0,
      references: [],
      errors: [],
    };
    if (!record.exists) {
      record.errors.push("missing SKILL.md");
      skills.push(record);
      continue;
    }
    const text = readUtf8(skillFile);
    record.lineCount = countLines(text);
    const { data, error } = parseFrontmatter(text);
    if (error) {
      record.errors.push(error);
    } else {
      record.frontmatter = data;
      record.description = typeof data.description === "string" ? data.description.trim() : "";
      record.metadata = data.metadata && typeof data.metadata === "object" ? data.metadata : {};
      record.allowedTools = data["allowed-tools"];
    }
    record.references = walk(directory)
      .filter((file) => path.basename(file) !== "SKILL.md")
      .map((file) => ({
        path: toPosix(path.relative(ROOT, file)),
        lineCount: countLines(readUtf8(file)),
      }))
      .sort((left, right) => left.path.localeCompare(right.path, "en"));
    skills.push(record);
  }
  return { skills };
}

// ---------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------

function validateFrontmatter(skill, issues) {
  const add = (message) => issues.push({ path: skill.path, message });
  const data = skill.frontmatter;
  if (!data) {
    return;
  }
  for (const key of REQUIRED_KEYS) {
    if (typeof data[key] !== "string" || data[key].trim() === "") {
      add(`missing required frontmatter field: ${key}`);
    }
  }
  if (typeof data.name === "string") {
    if (data.name.length > 64 || !NAME_PATTERN.test(data.name)) {
      add(
        `name "${data.name}" must be 1-64 lowercase alphanumeric characters and single hyphens, ` +
          "with no leading, trailing, or consecutive hyphens"
      );
    }
    if (data.name !== skill.name) {
      add(`name "${data.name}" must match the directory name "${skill.name}"`);
    }
  }
  if (typeof data.description === "string" && data.description.length > 1024) {
    add(`description is ${data.description.length} characters (max 1024)`);
  }
  if (data.compatibility !== undefined && String(data.compatibility).length > 500) {
    add("compatibility exceeds 500 characters");
  }
  if (data.metadata !== undefined && (typeof data.metadata !== "object" || Array.isArray(data.metadata))) {
    add("metadata must be a mapping");
  }
  for (const key of Object.keys(data)) {
    if (!ALLOWED_KEYS.has(key)) {
      add(`unknown frontmatter key "${key}"; repository-specific fields belong under metadata`);
    }
  }
}

function validateLineLimits(issues, warnings) {
  for (const skill of buildManifest().skills) {
    if (skill.lineCount > LIMITS.skillFail) {
      issues.push({
        path: skill.path,
        message: `${skill.lineCount} lines (max ${LIMITS.skillFail}); move detail into references/`,
      });
    } else if (skill.lineCount > LIMITS.skillWarn) {
      warnings.push({
        path: skill.path,
        message: `${skill.lineCount} lines (target ${LIMITS.skillWarn})`,
      });
    }
    for (const reference of skill.references) {
      if (reference.lineCount > LIMITS.referenceFail) {
        issues.push({
          path: reference.path,
          message: `${reference.lineCount} lines (max ${LIMITS.referenceFail})`,
        });
      }
    }
  }

  for (const file of walk(LLM_DIR).filter((candidate) => candidate.endsWith(".md"))) {
    const text = readUtf8(file);
    const relative = toPosix(path.relative(ROOT, file));
    const longest = longestLine(text);
    if (longest > LIMITS.lineLength) {
      issues.push({ path: relative, message: `longest line is ${longest} characters (max ${LIMITS.lineLength})` });
    }
  }

  const contextLines = countLines(readUtf8(CONTEXT_FILE));
  if (contextLines > LIMITS.contextFail) {
    issues.push({ path: ".llm/context.md", message: `${contextLines} lines (max ${LIMITS.contextFail})` });
  }
}

function validate() {
  const manifest = buildManifest();
  const issues = [];
  const warnings = [];

  if (manifest.skills.length === 0) {
    issues.push({ path: ".llm/skills", message: "no skills found" });
  }
  for (const skill of manifest.skills) {
    for (const error of skill.errors) {
      issues.push({ path: skill.path, message: error });
    }
    validateFrontmatter(skill, issues);
  }

  const descriptions = new Map();
  for (const skill of manifest.skills) {
    if (!skill.description) {
      continue;
    }
    const key = skill.description.toLowerCase();
    if (descriptions.has(key)) {
      issues.push({
        path: skill.path,
        message: `description is identical to ${descriptions.get(key)}; agents match on description alone`,
      });
    }
    descriptions.set(key, skill.name);
  }

  validateLineLimits(issues, warnings);
  return { manifest, issues, warnings };
}

// ---------------------------------------------------------------------------
// Generated artifacts
// ---------------------------------------------------------------------------

function renderIndexJson(manifest) {
  const skills = manifest.skills.map((skill) => ({
    name: skill.name,
    path: skill.path,
    description: skill.description,
    metadata: skill.metadata,
    lineCount: skill.lineCount,
    references: skill.references.map((reference) => reference.path),
  }));
  return `${JSON.stringify({ skillCount: skills.length, skills }, null, 2)}\n`;
}

function renderIndexMarkdown(manifest) {
  const lines = [
    "# Skill Index",
    "",
    "<!-- Generated by `npm run llm:index`. Do not edit by hand. -->",
    "",
    `${manifest.skills.length} skills. Each is an [Agent Skills](https://agentskills.io/specification)`,
    "directory: `SKILL.md` holds the instructions, `references/` holds supporting detail loaded on demand.",
    "",
    "| Skill | References | Description |",
    "| --- | --- | --- |",
  ];
  for (const skill of manifest.skills) {
    const description = skill.description.replace(/\|/g, "\\|");
    lines.push(`| [${skill.name}](./skills/${skill.name}/SKILL.md) | ${skill.references.length} | ${description} |`);
  }
  // No trailing blank entry: the single newline below is the file's only line terminator, which is
  // what markdownlint MD012 expects at end of file.
  return `${lines.join("\n")}\n`;
}

function renderRegistryBlock(manifest) {
  return [
    BLOCK_START,
    "",
    `<!-- Generated by \`npm run llm:index\`. Do not edit by hand. -->`,
    "",
    `${manifest.skills.length} skills are registered. See [the skill index](./index.md) for the full table.`,
    "",
    manifest.skills.map((skill) => `\`${skill.name}\``).join(", "),
    "",
    BLOCK_END,
  ].join("\n");
}

function replaceRegistryBlock(text, block) {
  const starts = text.split(BLOCK_START).length - 1;
  const ends = text.split(BLOCK_END).length - 1;
  if (starts !== 1 || ends !== 1) {
    throw new Error(
      `.llm/context.md must contain exactly one ${BLOCK_START} / ${BLOCK_END} pair (found ${starts} and ${ends})`
    );
  }
  const pattern = new RegExp(`${BLOCK_START}[\\s\\S]*?${BLOCK_END}`, "m");
  return text.replace(pattern, block);
}

function mirrorContent(skill) {
  const frontmatter = YAML.stringify({ name: skill.name, description: skill.description }).trimEnd();
  return [
    "---",
    frontmatter,
    "---",
    "",
    "<!-- Generated by `npm run llm:index`. Do not edit by hand. -->",
    "",
    `Canonical instructions: [\`.llm/skills/${skill.name}/SKILL.md\`](../../../.llm/skills/${skill.name}/SKILL.md)`,
    "",
    "Read that file and follow it. Supporting detail is in the sibling `references/` directory.",
    "",
  ].join("\n");
}

function expectedArtifacts(manifest) {
  const artifacts = new Map();
  artifacts.set(INDEX_JSON, renderIndexJson(manifest));
  artifacts.set(INDEX_MD, renderIndexMarkdown(manifest));
  artifacts.set(CONTEXT_FILE, replaceRegistryBlock(readUtf8(CONTEXT_FILE), renderRegistryBlock(manifest)));
  for (const root of MIRROR_ROOTS) {
    for (const skill of manifest.skills) {
      artifacts.set(path.join(root, skill.name, "SKILL.md"), mirrorContent(skill));
    }
  }
  return artifacts;
}

/** Mirror files that exist on disk but are no longer generated, so a renamed skill cannot linger. */
function staleMirrors(expected) {
  const stale = [];
  for (const root of MIRROR_ROOTS) {
    if (!fs.existsSync(root)) {
      continue;
    }
    for (const file of walk(root)) {
      if (!expected.has(file)) {
        stale.push(file);
      }
    }
  }
  return stale;
}

function writeArtifacts(manifest) {
  const expected = expectedArtifacts(manifest);
  const changed = [];
  for (const [filePath, content] of expected) {
    const current = fs.existsSync(filePath) ? readUtf8(filePath) : undefined;
    if (current === content) {
      continue;
    }
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, content, "utf8");
    changed.push(toPosix(path.relative(ROOT, filePath)));
  }
  for (const filePath of staleMirrors(expected)) {
    fs.rmSync(filePath, { force: true });
    changed.push(`removed ${toPosix(path.relative(ROOT, filePath))}`);
  }
  for (const root of MIRROR_ROOTS) {
    if (!fs.existsSync(root)) {
      continue;
    }
    for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
      const directory = path.join(root, entry.name);
      if (entry.isDirectory() && fs.readdirSync(directory).length === 0) {
        fs.rmdirSync(directory);
      }
    }
  }
  return changed;
}

function artifactDrifts(manifest) {
  const expected = expectedArtifacts(manifest);
  const drifts = [];
  for (const [filePath, content] of expected) {
    const relative = toPosix(path.relative(ROOT, filePath));
    if (!fs.existsSync(filePath)) {
      drifts.push({ path: relative, message: "missing generated file" });
    } else if (readUtf8(filePath).replace(/\r\n/g, "\n") !== content.replace(/\r\n/g, "\n")) {
      drifts.push({ path: relative, message: "generated file is stale" });
    }
  }
  for (const filePath of staleMirrors(expected)) {
    drifts.push({ path: toPosix(path.relative(ROOT, filePath)), message: "mirror has no matching skill" });
  }
  return drifts;
}

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

function report(issues, warnings) {
  for (const warning of warnings) {
    console.warn(`warning: ${warning.path}: ${warning.message}`);
  }
  if (issues.length === 0) {
    return true;
  }
  console.error(`.llm harness validation failed with ${issues.length} issue(s):`);
  for (const issue of issues) {
    console.error(`- ${issue.path}: ${issue.message}`);
  }
  return false;
}

function main(argv) {
  const command = argv[0] ?? "check";
  if (!["index", "validate", "check"].includes(command)) {
    throw new Error(`Unknown command: ${command}. Expected index, validate, or check.`);
  }

  const { manifest, issues, warnings } = validate();
  if (issues.length > 0) {
    report(issues, warnings);
    process.exitCode = 1;
    return;
  }

  if (command === "index") {
    const changed = writeArtifacts(manifest);
    report([], warnings);
    console.log(
      changed.length > 0
        ? `.llm harness: regenerated ${changed.length} artifact(s):\n${changed.map((c) => `  ${c}`).join("\n")}`
        : ".llm harness: generated artifacts already current."
    );
    return;
  }

  const drifts = command === "check" ? artifactDrifts(manifest) : [];
  if (drifts.length > 0) {
    report(
      drifts.map((drift) => ({ ...drift, message: `${drift.message}; run \`npm run llm:index\`` })),
      warnings
    );
    process.exitCode = 1;
    return;
  }

  report([], warnings);
  console.log(
    `.llm harness: ${manifest.skills.length} skills valid` +
      (command === "check" ? ", generated artifacts current." : ".")
  );
}

if (require.main === module) {
  try {
    main(process.argv.slice(2));
  } catch (error) {
    console.error(`.llm harness failed: ${error.message}`);
    process.exitCode = 1;
  }
}

module.exports = {
  LIMITS,
  NAME_PATTERN,
  artifactDrifts,
  buildManifest,
  countLines,
  main,
  mirrorContent,
  parseFrontmatter,
  renderIndexJson,
  renderIndexMarkdown,
  replaceRegistryBlock,
  validate,
  writeArtifacts,
};
