"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const {
  LIMITS,
  NAME_PATTERN,
  buildManifest,
  countLines,
  mirrorContent,
  parseFrontmatter,
  renderIndexJson,
  renderIndexMarkdown,
  replaceRegistryBlock,
  validate,
} = require("../llm/harness");

const ROOT = path.resolve(__dirname, "..", "..");

// ---------------------------------------------------------------------------
// Pure helpers
// ---------------------------------------------------------------------------

for (const [label, input, expected] of [
  ["an empty file", "", 0],
  ["a single unterminated line", "one", 1],
  ["a trailing newline", "one\n", 1],
  ["multiple lines", "one\ntwo\nthree\n", 3],
  ["CRLF endings", "one\r\ntwo\r\n", 2],
  ["a blank final line", "one\n\n", 2],
]) {
  test(`countLines handles ${label}`, () => assert.equal(countLines(input), expected));
}

for (const name of ["object-pooling", "a", "unity-mcp-test-loop", "il2cpp-build-configuration", "a1-b2"]) {
  test(`NAME_PATTERN accepts ${name}`, () => assert.ok(NAME_PATTERN.test(name)));
}

for (const name of ["Object-Pooling", "-leading", "trailing-", "double--hyphen", "has_underscore", "has space", ""]) {
  test(`NAME_PATTERN rejects ${JSON.stringify(name)}`, () => assert.ok(!NAME_PATTERN.test(name)));
}

test("parseFrontmatter separates mapping from body and reports malformed input", () => {
  const parsed = parseFrontmatter("---\nname: a\ndescription: b\n---\n# Title\n");
  assert.deepEqual(parsed.data, { name: "a", description: "b" });
  assert.equal(parsed.body, "# Title\n");
  assert.equal(parsed.error, undefined);

  assert.match(parseFrontmatter("# no frontmatter\n").error, /missing YAML frontmatter/);
  assert.match(parseFrontmatter("---\n: :\n---\n").error, /invalid YAML frontmatter/);
  assert.match(parseFrontmatter("---\n- a\n- b\n---\n").error, /must be a mapping/);
});

test("replaceRegistryBlock swaps the block and refuses ambiguous markers", () => {
  const start = "<!-- BEGIN GENERATED SKILL REGISTRY -->";
  const end = "<!-- END GENERATED SKILL REGISTRY -->";
  const text = `intro\n${start}\nold\n${end}\noutro\n`;
  const replaced = replaceRegistryBlock(text, `${start}\nnew\n${end}`);
  assert.match(replaced, /intro/);
  assert.match(replaced, /outro/);
  assert.match(replaced, /new/);
  assert.doesNotMatch(replaced, /old/);

  assert.throws(() => replaceRegistryBlock("no markers", "x"), /exactly one/);
  assert.throws(() => replaceRegistryBlock(`${start}${start}${end}`, "x"), /exactly one/);
});

test("mirrorContent is a pointer carrying only the discovery fields", () => {
  const content = mirrorContent({ name: "object-pooling", description: "Reuse pooled objects." });
  const { data, body } = parseFrontmatter(content);
  assert.deepEqual(Object.keys(data).sort(), ["description", "name"]);
  assert.equal(data.name, "object-pooling");
  assert.match(body, /\.llm\/skills\/object-pooling\/SKILL\.md/);
  // Three levels up from .claude/skills/<name>/ or .agents/skills/<name>/ reaches the repo root.
  assert.match(body, /\.\.\/\.\.\/\.\.\/\.llm/);
});

test("mirrorContent escapes a description that would break YAML", () => {
  const content = mirrorContent({ name: "x", description: 'Uses "quotes": and: colons' });
  const { data, error } = parseFrontmatter(content);
  assert.equal(error, undefined);
  assert.equal(data.description, 'Uses "quotes": and: colons');
});

// ---------------------------------------------------------------------------
// The repository's own skills
// ---------------------------------------------------------------------------

test("every skill in this repository satisfies the Agent Skills spec", () => {
  const { issues } = validate();
  assert.deepEqual(
    issues.map((issue) => `${issue.path}: ${issue.message}`),
    []
  );
});

test("every skill directory has a SKILL.md and at least one reference", () => {
  const { skills } = buildManifest();
  assert.ok(skills.length > 0);
  for (const skill of skills) {
    assert.equal(skill.exists, true, `${skill.name} is missing SKILL.md`);
    assert.ok(skill.description.length > 0, `${skill.name} has no description`);
    assert.ok(skill.lineCount <= LIMITS.skillFail, `${skill.name} is ${skill.lineCount} lines`);
  }
});

test("skill descriptions are distinct, since agents match on description alone", () => {
  const seen = new Map();
  for (const skill of buildManifest().skills) {
    const key = skill.description.toLowerCase();
    assert.equal(seen.has(key), false, `${skill.name} duplicates the description of ${seen.get(key)}`);
    seen.set(key, skill.name);
  }
});

test("every reference file is linked from its SKILL.md", () => {
  for (const skill of buildManifest().skills) {
    const body = fs.readFileSync(path.join(ROOT, skill.path), "utf8");
    for (const reference of skill.references) {
      const base = path.basename(reference.path);
      assert.ok(
        body.includes(`./references/${base}`),
        `${skill.name}/SKILL.md does not link references/${base}`
      );
    }
  }
});

/**
 * Strip fenced blocks and inline code spans. The documentation skills quote markdown syntax as
 * examples (`[text](url)`, `[README.md](../README.md)`), which a renderer never resolves and a
 * link check must not either.
 */
function prose(markdown) {
  return markdown
    .replace(/^ {0,3}(`{3,}|~{3,})[\s\S]*?^ {0,3}\1[^\n]*$/gm, "")
    .replace(/`[^`\n]*`/g, "");
}

test("every relative link in a SKILL.md resolves on disk", () => {
  // The 159-file consolidation re-homed every document, so a stale relative link is the most
  // likely regression when a skill is later split, merged, or renamed.
  const link = /\[[^\]]*\]\(([^)\s]+)\)/g;
  const unresolved = [];
  for (const skill of buildManifest().skills) {
    const directory = path.dirname(path.join(ROOT, skill.path));
    const body = prose(fs.readFileSync(path.join(ROOT, skill.path), "utf8"));
    for (const [, target] of body.matchAll(link)) {
      if (/^(https?:|#|mailto:)/.test(target)) {
        continue;
      }
      const [withoutAnchor] = target.split("#");
      if (withoutAnchor && !fs.existsSync(path.resolve(directory, withoutAnchor))) {
        unresolved.push(`${skill.path} -> ${target}`);
      }
    }
  }
  assert.deepEqual(unresolved, []);
});

test("generated index artifacts are deterministic and cover every skill", () => {
  const manifest = buildManifest();
  assert.equal(renderIndexJson(manifest), renderIndexJson(manifest));
  assert.equal(renderIndexMarkdown(manifest), renderIndexMarkdown(manifest));

  const parsed = JSON.parse(renderIndexJson(manifest));
  assert.equal(parsed.skillCount, manifest.skills.length);
  const markdown = renderIndexMarkdown(manifest);
  for (const skill of manifest.skills) {
    assert.ok(markdown.includes(`./skills/${skill.name}/SKILL.md`), `index omits ${skill.name}`);
  }
});

test("both agent mirrors exist for every skill and stay in sync with the source", () => {
  const manifest = buildManifest();
  for (const root of [".claude/skills", ".agents/skills"]) {
    for (const skill of manifest.skills) {
      const mirror = path.join(ROOT, root, skill.name, "SKILL.md");
      assert.ok(fs.existsSync(mirror), `${root}/${skill.name}/SKILL.md is missing`);
      assert.equal(fs.readFileSync(mirror, "utf8"), mirrorContent(skill), `${root}/${skill.name} is stale`);
    }
    const mirrored = fs.readdirSync(path.join(ROOT, root)).sort();
    assert.deepEqual(
      mirrored,
      manifest.skills.map((skill) => skill.name).sort(),
      `${root} has entries with no matching skill`
    );
  }
});
