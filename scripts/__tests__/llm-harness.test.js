"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");

const {
  GENERATED_MARKER,
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
} = require("../llm/harness");
const { CodeBlockTracker } = require("../wiki/transform-docs-to-wiki.js");

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

test("mirrorContent is a pointer carrying the discovery fields", () => {
  const content = mirrorContent({ name: "object-pooling", description: "Reuse pooled objects." });
  const { data, body } = parseFrontmatter(content);
  assert.deepEqual(Object.keys(data).sort(), ["description", "name"]);
  assert.equal(data.name, "object-pooling");
  assert.match(body, /\.llm\/skills\/object-pooling\/SKILL\.md/);
  assert.ok(content.includes(GENERATED_MARKER), "a mirror must carry the generated marker");
  // Three levels up from .claude/skills/<name>/ or .agents/skills/<name>/ reaches the repo root.
  assert.match(body, /\.\.\/\.\.\/\.\.\/\.llm/);
});

test("mirrorContent propagates license, compatibility, and allowed-tools when declared", () => {
  const content = mirrorContent({
    name: "x",
    description: "Does x.",
    license: "MIT",
    compatibility: "Requires Unity 2022.3 or newer.",
    allowedTools: "Read Grep Bash",
  });
  const { data, error } = parseFrontmatter(content);
  assert.equal(error, undefined);
  assert.equal(data.license, "MIT");
  assert.equal(data.compatibility, "Requires Unity 2022.3 or newer.");
  assert.equal(data["allowed-tools"], "Read Grep Bash");
});

test("mirrorContent omits optional fields the source does not declare", () => {
  const { data } = parseFrontmatter(mirrorContent({ name: "x", description: "Does x." }));
  assert.deepEqual(Object.keys(data).sort(), ["description", "name"]);
});

test("mirrorContent escapes a description that would break YAML", () => {
  const content = mirrorContent({ name: "x", description: 'Uses "quotes": and: colons' });
  const { data, error } = parseFrontmatter(content);
  assert.equal(error, undefined);
  assert.equal(data.description, 'Uses "quotes": and: colons');
});

// ---------------------------------------------------------------------------
// Fixture harness: drive the real validator over temporary skill trees
// ---------------------------------------------------------------------------

const CONTEXT_FIXTURE = [
  "# Fixture context",
  "",
  "<!-- BEGIN GENERATED SKILL REGISTRY -->",
  "",
  "<!-- END GENERATED SKILL REGISTRY -->",
  "",
].join("\n");

const NAME_RULE =
  "must be 1-64 lowercase alphanumeric characters and single hyphens, " +
  "with no leading, trailing, or consecutive hyphens";

function frontmatter(lines) {
  return `---\n${lines.join("\n")}\n---\n`;
}

/** A SKILL.md that passes every rule, so a fixture varies exactly one thing. */
function validSkill(name, description = `Covers ${name}. Use when working on ${name}.`) {
  return `${frontmatter([`name: ${name}`, `description: ${description}`])}\n# ${name}\n`;
}

function repeatLines(count, line = "text") {
  return `${Array.from({ length: count }, () => line).join("\n")}\n`;
}

/**
 * Build a throwaway `.llm` tree. `skills` maps a directory name to either a SKILL.md string, or
 * `{ skill, files }` where `skill: null` omits SKILL.md and `files` are extra paths inside the
 * skill directory (string or Buffer content).
 */
function createFixture(t, skills) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "llm-harness-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  fs.mkdirSync(path.join(root, ".llm"), { recursive: true });
  fs.writeFileSync(path.join(root, ".llm", "context.md"), CONTEXT_FIXTURE, "utf8");
  for (const [name, spec] of Object.entries(skills)) {
    const directory = path.join(root, ".llm", "skills", name);
    fs.mkdirSync(directory, { recursive: true });
    const { skill, files = {} } = typeof spec === "string" ? { skill: spec } : spec;
    if (skill !== null && skill !== undefined) {
      fs.writeFileSync(path.join(directory, "SKILL.md"), skill, "utf8");
    }
    for (const [relative, content] of Object.entries(files)) {
      const target = path.join(directory, relative);
      fs.mkdirSync(path.dirname(target), { recursive: true });
      fs.writeFileSync(target, content);
    }
  }
  return root;
}

/** Run `action` with the harness pointed at `root` instead of this repository. */
function atRoot(root, action) {
  const previous = process.env.DX_LLM_ROOT;
  process.env.DX_LLM_ROOT = root;
  try {
    return action();
  } finally {
    if (previous === undefined) {
      delete process.env.DX_LLM_ROOT;
    } else {
      process.env.DX_LLM_ROOT = previous;
    }
  }
}

function validateFixture(t, skills) {
  const root = createFixture(t, skills);
  const { issues, warnings } = atRoot(root, () => validate());
  return {
    root,
    warnings: warnings.map((warning) => `${warning.path}: ${warning.message}`),
    messages: issues.map((issue) => `${issue.path}: ${issue.message}`),
  };
}

/** Invoke the CLI entry point with console output captured and the exit code restored. */
function runMain(root, command) {
  const restore = { log: console.log, warn: console.warn, error: console.error };
  const previousExitCode = process.exitCode;
  const output = [];
  const capture = (...args) => output.push(args.join(" "));
  console.log = capture;
  console.warn = capture;
  console.error = capture;
  try {
    process.exitCode = 0;
    atRoot(root, () => main([command]));
    return { exitCode: process.exitCode, output: output.join("\n") };
  } finally {
    Object.assign(console, restore);
    process.exitCode = previousExitCode;
  }
}

// ---------------------------------------------------------------------------
// One fixture per rule the harness advertises
// ---------------------------------------------------------------------------

const LONG_NAME = "a".repeat(65);

const RULE_FIXTURES = [
  {
    label: "rejects an uppercase name",
    skills: { Alpha: validSkill("Alpha") },
    expected: [`.llm/skills/Alpha/SKILL.md: name "Alpha" ${NAME_RULE}`],
  },
  {
    label: "rejects a leading hyphen in name",
    skills: { "-alpha": validSkill("-alpha") },
    expected: [`.llm/skills/-alpha/SKILL.md: name "-alpha" ${NAME_RULE}`],
  },
  {
    label: "rejects a trailing hyphen in name",
    skills: { "alpha-": validSkill("alpha-") },
    expected: [`.llm/skills/alpha-/SKILL.md: name "alpha-" ${NAME_RULE}`],
  },
  {
    label: "rejects consecutive hyphens in name",
    skills: { "alpha--beta": validSkill("alpha--beta") },
    expected: [`.llm/skills/alpha--beta/SKILL.md: name "alpha--beta" ${NAME_RULE}`],
  },
  {
    label: "rejects a name longer than 64 characters",
    skills: { [LONG_NAME]: validSkill(LONG_NAME) },
    expected: [`.llm/skills/${LONG_NAME}/SKILL.md: name "${LONG_NAME}" ${NAME_RULE}`],
  },
  {
    label: "rejects a name that does not match its directory",
    skills: { alpha: validSkill("beta") },
    expected: ['.llm/skills/alpha/SKILL.md: name "beta" must match the directory name "alpha"'],
  },
  {
    label: "rejects a missing name",
    skills: { alpha: frontmatter(["description: Covers alpha. Use when working on alpha."]) },
    expected: [".llm/skills/alpha/SKILL.md: missing required frontmatter field: name"],
  },
  {
    label: "rejects a blank name",
    skills: { alpha: frontmatter(['name: ""', "description: Covers alpha."]) },
    expected: [
      ".llm/skills/alpha/SKILL.md: missing required frontmatter field: name",
      `.llm/skills/alpha/SKILL.md: name "" ${NAME_RULE}`,
      '.llm/skills/alpha/SKILL.md: name "" must match the directory name "alpha"',
    ],
  },
  {
    label: "rejects a missing description",
    skills: { alpha: frontmatter(["name: alpha"]) },
    expected: [".llm/skills/alpha/SKILL.md: missing required frontmatter field: description"],
  },
  {
    label: "rejects a description over 1024 characters",
    skills: { alpha: frontmatter(["name: alpha", `description: ${"d".repeat(1025)}`]) },
    expected: [".llm/skills/alpha/SKILL.md: description is 1025 characters (max 1024)"],
  },
  {
    label: "rejects a newline inside description",
    skills: { alpha: frontmatter(["name: alpha", 'description: "one\\ntwo"']) },
    expected: [".llm/skills/alpha/SKILL.md: description must be a single line; it contains a newline"],
  },
  {
    label: "rejects compatibility over 500 characters",
    skills: {
      alpha: frontmatter(["name: alpha", "description: Covers alpha.", `compatibility: ${"c".repeat(501)}`]),
    },
    expected: [".llm/skills/alpha/SKILL.md: compatibility is 501 characters (must be 1-500)"],
  },
  {
    label: "rejects an empty compatibility",
    skills: { alpha: frontmatter(["name: alpha", "description: Covers alpha.", 'compatibility: ""']) },
    expected: [".llm/skills/alpha/SKILL.md: compatibility is 0 characters (must be 1-500)"],
  },
  {
    label: "rejects a non-scalar compatibility",
    skills: {
      alpha: frontmatter(["name: alpha", "description: Covers alpha.", "compatibility:", "  unity: 2022"]),
    },
    expected: [".llm/skills/alpha/SKILL.md: compatibility must be a string"],
  },
  {
    label: "rejects a non-string license",
    skills: { alpha: frontmatter(["name: alpha", "description: Covers alpha.", "license: 2"]) },
    expected: [".llm/skills/alpha/SKILL.md: license must be a string"],
  },
  {
    label: "rejects allowed-tools declared as a YAML list",
    skills: {
      alpha: frontmatter(["name: alpha", "description: Covers alpha.", "allowed-tools:", "  - Read", "  - Bash"]),
    },
    expected: [".llm/skills/alpha/SKILL.md: allowed-tools must be a space-separated string"],
  },
  {
    label: "rejects metadata that is not a mapping",
    skills: { alpha: frontmatter(["name: alpha", "description: Covers alpha.", "metadata:", "  - one", "  - two"]) },
    expected: [".llm/skills/alpha/SKILL.md: metadata must be a mapping"],
  },
  {
    label: "rejects non-string metadata values",
    skills: {
      alpha: frontmatter([
        "name: alpha",
        "description: Covers alpha.",
        "metadata:",
        "  version: 1.5",
        "  tags: [a, b]",
        "  nested: { deep: true }",
      ]),
    },
    expected: [
      ".llm/skills/alpha/SKILL.md: metadata.version must be a string, not number",
      ".llm/skills/alpha/SKILL.md: metadata.tags must be a string, not array",
      ".llm/skills/alpha/SKILL.md: metadata.nested must be a string, not object",
    ],
  },
  {
    label: "rejects a skill directory with no SKILL.md",
    skills: { alpha: { skill: null } },
    expected: [".llm/skills/alpha/SKILL.md: missing SKILL.md"],
  },
  {
    label: "rejects a SKILL.md with no frontmatter",
    skills: { alpha: "# Alpha\n" },
    expected: [".llm/skills/alpha/SKILL.md: missing YAML frontmatter"],
  },
  {
    label: "rejects malformed YAML frontmatter",
    skills: { alpha: "---\n: :\n---\n" },
    pattern: /^\.llm\/skills\/alpha\/SKILL\.md: invalid YAML frontmatter: /,
  },
  {
    label: "rejects a SKILL.md over the 200-line cap",
    skills: { alpha: validSkill("alpha") + repeatLines(195) },
    expected: [".llm/skills/alpha/SKILL.md: 201 lines (max 200); move detail into references/"],
  },
  {
    label: "rejects a reference over the 500-line cap",
    skills: { alpha: { skill: validSkill("alpha"), files: { "references/big.md": repeatLines(501) } } },
    expected: [".llm/skills/alpha/references/big.md: 501 lines (max 500)"],
  },
  {
    label: "rejects duplicate descriptions across skills",
    skills: {
      "alpha-one": validSkill("alpha-one", "Shared summary."),
      "alpha-two": validSkill("alpha-two", "Shared summary."),
    },
    expected: [
      ".llm/skills/alpha-two/SKILL.md: description is identical to alpha-one; agents match on description alone",
    ],
  },
  {
    label: "rejects an empty skills directory",
    skills: {},
    expected: [".llm/skills: no skills found"],
  },
  {
    label: "accepts a conformant skill",
    skills: { alpha: { skill: validSkill("alpha"), files: { "references/detail.md": "# Detail\n" } } },
    expected: [],
  },
];

for (const fixture of RULE_FIXTURES) {
  test(`validate ${fixture.label}`, (t) => {
    const { messages } = validateFixture(t, fixture.skills);
    if (fixture.pattern) {
      assert.equal(messages.length, 1, `expected one issue, got ${JSON.stringify(messages)}`);
      assert.match(messages[0], fixture.pattern);
      return;
    }
    assert.deepEqual(messages.sort(), [...fixture.expected].sort());
  });
}

test("validate reports an unknown frontmatter key as a warning, not an issue", (t) => {
  const skill = frontmatter(["name: alpha", "description: Covers alpha.", "future-client-field: yes"]);
  const { messages, warnings } = validateFixture(t, { alpha: skill });
  assert.deepEqual(messages, []);
  assert.ok(
    warnings.includes(
      '.llm/skills/alpha/SKILL.md: unknown frontmatter key "future-client-field"; ' +
        "repository-specific fields belong under metadata"
    ),
    `warnings were ${JSON.stringify(warnings)}`
  );
});

test("validate exempts bundled scripts/ and assets/ from the reference line cap", (t) => {
  const skills = {
    alpha: {
      skill: validSkill("alpha"),
      files: {
        "scripts/extract.py": repeatLines(600, "print('x')"),
        "assets/blob.bin": Buffer.from([0x00, 0xff, 0xfe, 0x80, 0x0a]),
      },
    },
  };
  const { root, messages } = validateFixture(t, skills);
  assert.deepEqual(messages, []);
  const [skill] = atRoot(root, () => buildManifest().skills);
  assert.deepEqual(skill.references, []);
  assert.deepEqual(skill.resources, [
    ".llm/skills/alpha/assets/blob.bin",
    ".llm/skills/alpha/scripts/extract.py",
  ]);
});

test("validate accepts spec-shaped optional frontmatter fields", (t) => {
  const skill = frontmatter([
    "name: alpha",
    "description: Covers alpha.",
    "license: MIT",
    "compatibility: Requires Unity 2022.3 or newer.",
    "allowed-tools: Read Grep Bash",
    "metadata:",
    "  owner: tooling",
  ]);
  const { messages } = validateFixture(t, { alpha: skill });
  assert.deepEqual(messages, []);
});

// ---------------------------------------------------------------------------
// Round trip: writeArtifacts -> artifactDrifts -> staleMirrors
// ---------------------------------------------------------------------------

const MIRROR_ROOTS = [".claude/skills", ".agents/skills"];

test("writeArtifacts generates every artifact and artifactDrifts then reports none", (t) => {
  const root = createFixture(t, { alpha: validSkill("alpha"), beta: validSkill("beta") });
  const changed = atRoot(root, () => writeArtifacts(buildManifest()));
  assert.deepEqual(
    changed.sort(),
    [
      ".agents/skills/alpha/SKILL.md",
      ".agents/skills/beta/SKILL.md",
      ".claude/skills/alpha/SKILL.md",
      ".claude/skills/beta/SKILL.md",
      ".llm/context.md",
      ".llm/index.json",
      ".llm/index.md",
    ].sort()
  );

  const index = JSON.parse(fs.readFileSync(path.join(root, ".llm", "index.json"), "utf8"));
  assert.equal(index.skillCount, 2);
  assert.deepEqual(
    index.skills.map((skill) => skill.name),
    ["alpha", "beta"]
  );
  const indexMarkdown = fs.readFileSync(path.join(root, ".llm", "index.md"), "utf8");
  assert.match(indexMarkdown, /\.\/skills\/alpha\/SKILL\.md/);
  assert.match(fs.readFileSync(path.join(root, ".llm", "context.md"), "utf8"), /`alpha`, `beta`/);
  for (const mirror of MIRROR_ROOTS) {
    assert.ok(fs.existsSync(path.join(root, mirror, "alpha", "SKILL.md")), `${mirror}/alpha is missing`);
  }

  assert.deepEqual(atRoot(root, () => artifactDrifts(buildManifest())), []);
  assert.deepEqual(atRoot(root, () => writeArtifacts(buildManifest())), []);
});

test("artifactDrifts reports an edited artifact and a missing one", (t) => {
  const root = createFixture(t, { alpha: validSkill("alpha") });
  atRoot(root, () => writeArtifacts(buildManifest()));

  fs.writeFileSync(path.join(root, ".llm", "index.md"), "# tampered\n", "utf8");
  fs.rmSync(path.join(root, ".claude", "skills", "alpha", "SKILL.md"));
  const drifts = atRoot(root, () => artifactDrifts(buildManifest()));
  assert.deepEqual(
    drifts.map((drift) => `${drift.path}: ${drift.message}`).sort(),
    [".claude/skills/alpha/SKILL.md: missing generated file", ".llm/index.md: generated file is stale"].sort()
  );

  atRoot(root, () => writeArtifacts(buildManifest()));
  assert.deepEqual(atRoot(root, () => artifactDrifts(buildManifest())), []);
});

test("a renamed skill leaves no mirror file and no empty mirror directory", (t) => {
  const root = createFixture(t, { alpha: validSkill("alpha") });
  atRoot(root, () => writeArtifacts(buildManifest()));

  fs.rmSync(path.join(root, ".llm", "skills", "alpha"), { recursive: true });
  fs.mkdirSync(path.join(root, ".llm", "skills", "gamma"), { recursive: true });
  fs.writeFileSync(path.join(root, ".llm", "skills", "gamma", "SKILL.md"), validSkill("gamma"), "utf8");

  const drifts = atRoot(root, () => artifactDrifts(buildManifest()));
  assert.ok(
    drifts.some((drift) => drift.path === ".claude/skills/alpha/SKILL.md" && drift.message === "mirror has no matching skill"),
    `drifts were ${JSON.stringify(drifts)}`
  );

  const changed = atRoot(root, () => writeArtifacts(buildManifest()));
  assert.ok(changed.includes("removed .claude/skills/alpha/SKILL.md"), `changed was ${JSON.stringify(changed)}`);
  for (const mirror of MIRROR_ROOTS) {
    assert.equal(fs.existsSync(path.join(root, mirror, "alpha")), false, `${mirror}/alpha directory survived`);
    assert.ok(fs.existsSync(path.join(root, mirror, "gamma", "SKILL.md")));
  }
  assert.deepEqual(atRoot(root, () => artifactDrifts(buildManifest())), []);
});

test("a hand-authored .claude/skills entry survives index and check", (t) => {
  const root = createFixture(t, { alpha: validSkill("alpha") });
  const handAuthored = path.join(root, ".claude", "skills", "my-own-skill");
  fs.mkdirSync(path.join(handAuthored, "references"), { recursive: true });
  const content = `${validSkill("my-own-skill")}Hand written, not generated.\n`;
  fs.writeFileSync(path.join(handAuthored, "SKILL.md"), content, "utf8");
  fs.writeFileSync(path.join(handAuthored, "references", "notes.md"), "# Notes\n", "utf8");

  const indexRun = runMain(root, "index");
  assert.equal(indexRun.exitCode, 0, indexRun.output);
  assert.equal(fs.readFileSync(path.join(handAuthored, "SKILL.md"), "utf8"), content);
  assert.ok(fs.existsSync(path.join(handAuthored, "references", "notes.md")));
  assert.doesNotMatch(indexRun.output, /my-own-skill/);

  const checkRun = runMain(root, "check");
  assert.equal(checkRun.exitCode, 0, checkRun.output);
  assert.doesNotMatch(checkRun.output, /my-own-skill/);
  assert.equal(fs.readFileSync(path.join(handAuthored, "SKILL.md"), "utf8"), content);
});

test("a generated mirror with no matching skill is still reaped", (t) => {
  const root = createFixture(t, { alpha: validSkill("alpha") });
  atRoot(root, () => writeArtifacts(buildManifest()));
  const orphan = path.join(root, ".claude", "skills", "removed-skill", "SKILL.md");
  fs.mkdirSync(path.dirname(orphan), { recursive: true });
  fs.writeFileSync(orphan, mirrorContent({ name: "removed-skill", description: "Gone." }), "utf8");

  const checkRun = runMain(root, "check");
  assert.equal(checkRun.exitCode, 1, checkRun.output);
  assert.match(checkRun.output, /removed-skill\/SKILL\.md: mirror has no matching skill/);

  assert.equal(runMain(root, "index").exitCode, 0);
  assert.equal(fs.existsSync(orphan), false);
  assert.equal(runMain(root, "check").exitCode, 0);
});

test("index refuses to write while validation fails, and check reports the same issue", (t) => {
  const root = createFixture(t, { alpha: validSkill("beta") });
  const indexRun = runMain(root, "index");
  assert.equal(indexRun.exitCode, 1);
  assert.match(indexRun.output, /must match the directory name/);
  assert.equal(fs.existsSync(path.join(root, ".llm", "index.json")), false);
  assert.equal(runMain(root, "check").exitCode, 1);
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
  // Only `references/` is covered. Bundled `scripts/` and `assets/` are spec-sanctioned resources
  // that a SKILL.md invokes by whatever path it likes, so they carry no linking requirement.
  for (const skill of buildManifest().skills) {
    const body = fs.readFileSync(path.join(ROOT, skill.path), "utf8");
    for (const reference of skill.references) {
      const base = path.basename(reference.path);
      assert.ok(body.includes(`./references/${base}`), `${skill.name}/SKILL.md does not link references/${base}`);
    }
  }
});

/**
 * Strip fenced blocks and inline code spans. The documentation skills quote markdown syntax as
 * examples (`[text](url)`, `[README.md](../README.md)`), which a renderer never resolves and a
 * link check must not either. CodeBlockTracker is the repository's single fence parser; a
 * hand-rolled regex flips parity on a variable-length fence that encloses a shorter one.
 */
function prose(markdown) {
  const tracker = new CodeBlockTracker();
  return markdown
    .split(/\r\n|\n|\r/)
    .map((line) => {
      const wasFenced = tracker.inCodeBlock;
      const fenced = tracker.processLine(line) || wasFenced;
      return fenced ? "" : line.replace(/`[^`\n]*`/g, "");
    })
    .join("\n");
}

test("prose strips a variable-length fence that encloses a shorter one", () => {
  const markdown = ["````markdown", "```", "[example](./nowhere.md)", "```", "````", "[real](./somewhere.md)"].join(
    "\n"
  );
  const stripped = prose(markdown);
  assert.doesNotMatch(stripped, /nowhere/);
  assert.match(stripped, /somewhere/);
});

test("every relative link in a SKILL.md or reference resolves on disk", () => {
  // The 159-file consolidation re-homed every document, so a stale relative link is the most
  // likely regression when a skill is later split, merged, or renamed. References are 84% of the
  // corpus, so checking SKILL.md alone would miss almost every broken link.
  const link = /\[[^\]]*\]\(([^)\s]+)\)/g;
  const unresolved = [];
  let checked = 0;
  for (const skill of buildManifest().skills) {
    for (const relative of [skill.path, ...skill.references.map((reference) => reference.path)]) {
      if (!relative.endsWith(".md")) {
        continue;
      }
      checked += 1;
      const absolute = path.join(ROOT, relative);
      const directory = path.dirname(absolute);
      const body = prose(fs.readFileSync(absolute, "utf8"));
      for (const [, target] of body.matchAll(link)) {
        if (/^(https?:|#|mailto:)/.test(target)) {
          continue;
        }
        const [withoutAnchor] = target.split("#");
        if (withoutAnchor && !fs.existsSync(path.resolve(directory, withoutAnchor))) {
          unresolved.push(`${relative} -> ${target}`);
        }
      }
    }
  }
  assert.deepEqual(unresolved, []);
  assert.ok(checked > 100, `expected the reference corpus to be covered, checked only ${checked} files`);
});

test("generated index artifacts are deterministic and cover every skill", () => {
  // Two independent reads of the same tree, so a nondeterministic walk or sort would diverge.
  assert.equal(renderIndexJson(buildManifest()), renderIndexJson(buildManifest()));
  assert.equal(renderIndexMarkdown(buildManifest()), renderIndexMarkdown(buildManifest()));

  const manifest = buildManifest();
  const parsed = JSON.parse(renderIndexJson(manifest));
  assert.equal(parsed.skillCount, manifest.skills.length);
  const markdown = renderIndexMarkdown(manifest);
  for (const skill of manifest.skills) {
    assert.ok(markdown.includes(`./skills/${skill.name}/SKILL.md`), `index omits ${skill.name}`);
  }
});

test("both agent mirrors exist for every skill and stay in sync with the source", () => {
  const manifest = buildManifest();
  const names = new Set(manifest.skills.map((skill) => skill.name));
  for (const root of MIRROR_ROOTS) {
    for (const skill of manifest.skills) {
      const mirror = path.join(ROOT, root, skill.name, "SKILL.md");
      assert.ok(fs.existsSync(mirror), `${root}/${skill.name}/SKILL.md is missing`);
      assert.equal(fs.readFileSync(mirror, "utf8"), mirrorContent(skill), `${root}/${skill.name} is stale`);
    }
    // A hand-authored skill may also live here, so only generated entries must match a source skill.
    for (const entry of fs.readdirSync(path.join(ROOT, root))) {
      const mirror = path.join(ROOT, root, entry, "SKILL.md");
      if (fs.existsSync(mirror) && fs.readFileSync(mirror, "utf8").includes(GENERATED_MARKER)) {
        assert.ok(names.has(entry), `${root}/${entry} is generated but has no matching skill`);
      }
    }
  }
});
