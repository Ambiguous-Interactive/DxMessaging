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
  parseFrontmatter,
  renderIndexMarkdown,
  replaceRegistryBlock,
  validate,
  writeArtifacts
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
  ["a blank final line", "one\n\n", 2]
]) {
  test(`countLines handles ${label}`, () => assert.equal(countLines(input), expected));
}

for (const name of [
  "object-pooling",
  "a",
  "unity-mcp-test-loop",
  "il2cpp-build-configuration",
  "a1-b2"
]) {
  test(`NAME_PATTERN accepts ${name}`, () => assert.ok(NAME_PATTERN.test(name)));
}

for (const name of [
  "Object-Pooling",
  "-leading",
  "trailing-",
  "double--hyphen",
  "has_underscore",
  "has space",
  ""
]) {
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

// ---------------------------------------------------------------------------
// Fixture harness: drive the real validator over temporary skill trees
// ---------------------------------------------------------------------------

const CONTEXT_FIXTURE = [
  "# Fixture context",
  "",
  "<!-- BEGIN GENERATED SKILL REGISTRY -->",
  "",
  "<!-- END GENERATED SKILL REGISTRY -->",
  ""
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
    messages: issues.map((issue) => `${issue.path}: ${issue.message}`)
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

const skillRule = (label, lines, ...errors) => ({
  label,
  skills: { alpha: frontmatter(lines) },
  expected: errors.map((error) => `.llm/skills/alpha/SKILL.md: ${error}`)
});
const RULE_FIXTURES = [
  ...[
    ["rejects an uppercase name", "Alpha"],
    ["rejects a leading hyphen in name", "-alpha"],
    ["rejects a trailing hyphen in name", "alpha-"],
    ["rejects consecutive hyphens in name", "alpha--beta"],
    ["rejects a name longer than 64 characters", LONG_NAME]
  ].map(([label, name]) => ({
    label,
    skills: { [name]: validSkill(name) },
    expected: [`.llm/skills/${name}/SKILL.md: name "${name}" ${NAME_RULE}`]
  })),
  ...[
    [
      "rejects a name that does not match its directory",
      validSkill("beta"),
      'name "beta" must match the directory name "alpha"'
    ],
    ["rejects a SKILL.md with no frontmatter", "# Alpha\n", "missing YAML frontmatter"],
    [
      "rejects a SKILL.md over the 200-line cap",
      validSkill("alpha") + repeatLines(195),
      "201 lines (max 200); move detail into references/"
    ]
  ].map(([label, skill, error]) => ({
    label,
    skills: { alpha: skill },
    expected: [`.llm/skills/alpha/SKILL.md: ${error}`]
  })),
  skillRule(
    "rejects a missing name",
    ["description: Covers alpha. Use when working on alpha."],
    "missing required frontmatter field: name"
  ),
  skillRule(
    "rejects a blank name",
    ['name: ""', "description: Covers alpha."],
    "missing required frontmatter field: name",
    `name "" ${NAME_RULE}`,
    'name "" must match the directory name "alpha"'
  ),
  skillRule(
    "rejects a missing description",
    ["name: alpha"],
    "missing required frontmatter field: description"
  ),
  skillRule(
    "rejects a description over 1024 characters",
    ["name: alpha", `description: ${"d".repeat(1025)}`],
    "description is 1025 characters (max 1024)"
  ),
  skillRule(
    "rejects a newline inside description",
    ["name: alpha", 'description: "one\\ntwo"'],
    "description must be a single line; it contains a newline"
  ),
  ...[
    [
      "rejects compatibility over 500 characters",
      [`compatibility: ${"c".repeat(501)}`],
      "compatibility is 501 characters (must be 1-500)"
    ],
    [
      "rejects an empty compatibility",
      ['compatibility: ""'],
      "compatibility is 0 characters (must be 1-500)"
    ],
    [
      "rejects a non-scalar compatibility",
      ["compatibility:", "  unity: 2022"],
      "compatibility must be a string"
    ],
    ["rejects a non-string license", ["license: 2"], "license must be a string"],
    [
      "rejects allowed-tools declared as a YAML list",
      ["allowed-tools:", "  - Read", "  - Bash"],
      "allowed-tools must be a space-separated string"
    ],
    [
      "rejects metadata that is not a mapping",
      ["metadata:", "  - one", "  - two"],
      "metadata must be a mapping"
    ],
    [
      "rejects non-string metadata values",
      ["metadata:", "  version: 1.5", "  tags: [a, b]", "  nested: { deep: true }"],
      "metadata.version must be a string, not number",
      "metadata.tags must be a string, not array",
      "metadata.nested must be a string, not object"
    ]
  ].map(([label, fields, ...errors]) =>
    skillRule(label, ["name: alpha", "description: Covers alpha.", ...fields], ...errors)
  ),
  {
    label: "rejects a skill directory with no SKILL.md",
    skills: { alpha: { skill: null } },
    expected: [".llm/skills/alpha/SKILL.md: missing SKILL.md"]
  },
  {
    label: "rejects malformed YAML frontmatter",
    skills: { alpha: "---\n: :\n---\n" },
    pattern: /^\.llm\/skills\/alpha\/SKILL\.md: invalid YAML frontmatter: /
  },
  {
    label: "rejects a reference over the 500-line cap",
    skills: {
      alpha: { skill: validSkill("alpha"), files: { "references/big.md": repeatLines(501) } }
    },
    expected: [".llm/skills/alpha/references/big.md: 501 lines (max 500)"]
  },
  {
    label: "rejects duplicate descriptions across skills",
    skills: {
      "alpha-one": validSkill("alpha-one", "Shared summary."),
      "alpha-two": validSkill("alpha-two", "Shared summary.")
    },
    expected: [
      ".llm/skills/alpha-two/SKILL.md: description is identical to alpha-one; agents match on description alone"
    ]
  },
  {
    label: "rejects an empty skills directory",
    skills: {},
    expected: [".llm/skills: no skills found"]
  },
  {
    label: "accepts a conformant skill",
    skills: {
      alpha: { skill: validSkill("alpha"), files: { "references/detail.md": "# Detail\n" } }
    },
    expected: []
  }
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
  const skill = frontmatter([
    "name: alpha",
    "description: Covers alpha.",
    "future-client-field: yes"
  ]);
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
        "assets/blob.bin": Buffer.from([0x00, 0xff, 0xfe, 0x80, 0x0a])
      }
    }
  };
  const { root, messages } = validateFixture(t, skills);
  assert.deepEqual(messages, []);
  const [skill] = atRoot(root, () => buildManifest().skills);
  assert.deepEqual(skill.references, []);
  assert.deepEqual(skill.resources, [
    ".llm/skills/alpha/assets/blob.bin",
    ".llm/skills/alpha/scripts/extract.py"
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
    "  owner: tooling"
  ]);
  const { messages } = validateFixture(t, { alpha: skill });
  assert.deepEqual(messages, []);
});

// ---------------------------------------------------------------------------
// Round trip: the index and registry are the only generated artifacts
// ---------------------------------------------------------------------------

const OTHER_SKILL_ROOTS = [".claude/skills", ".agents/skills", ".github/skills", ".codex/skills"];

test("index generates only canonical artifacts and remains idempotent", (t) => {
  const root = createFixture(t, { alpha: validSkill("alpha"), beta: validSkill("beta") });
  const changed = atRoot(root, () => writeArtifacts(buildManifest()));
  assert.deepEqual(changed.sort(), [".llm/context.md", ".llm/index.md"]);
  assert.equal(fs.existsSync(path.join(root, ".llm", "index.json")), false);
  assert.match(
    fs.readFileSync(path.join(root, ".llm", "index.md"), "utf8"),
    /skills\/alpha\/SKILL\.md/
  );
  assert.match(fs.readFileSync(path.join(root, ".llm", "context.md"), "utf8"), /`alpha`, `beta`/);
  assert.deepEqual(
    atRoot(root, () => artifactDrifts(buildManifest())),
    []
  );
  assert.deepEqual(
    atRoot(root, () => writeArtifacts(buildManifest())),
    []
  );
  assert.equal(runMain(root, "index").exitCode, 0);
  assert.equal(runMain(root, "check").exitCode, 0);
  for (const other of OTHER_SKILL_ROOTS) {
    assert.equal(fs.existsSync(path.join(root, other)), false, other);
  }
});

test("artifactDrifts reports an edited index and repairs it", (t) => {
  const root = createFixture(t, { alpha: validSkill("alpha") });
  atRoot(root, () => writeArtifacts(buildManifest()));
  fs.writeFileSync(path.join(root, ".llm", "index.md"), "# tampered\n", "utf8");
  assert.deepEqual(
    atRoot(root, () => artifactDrifts(buildManifest())),
    [{ path: ".llm/index.md", message: "generated file is stale" }]
  );
  atRoot(root, () => writeArtifacts(buildManifest()));
  assert.deepEqual(
    atRoot(root, () => artifactDrifts(buildManifest())),
    []
  );
  fs.rmSync(path.join(root, ".llm", "index.md"));
  assert.deepEqual(
    atRoot(root, () => artifactDrifts(buildManifest())),
    [{ path: ".llm/index.md", message: "missing generated file" }]
  );
  assert.equal(runMain(root, "index").exitCode, 0);
  assert.equal(runMain(root, "check").exitCode, 0);
});

test("renaming a skill updates canonical discovery without generating mirrors", (t) => {
  const root = createFixture(t, { alpha: validSkill("alpha") });
  atRoot(root, () => writeArtifacts(buildManifest()));
  fs.renameSync(
    path.join(root, ".llm", "skills", "alpha"),
    path.join(root, ".llm", "skills", "gamma")
  );
  fs.writeFileSync(path.join(root, ".llm", "skills", "gamma", "SKILL.md"), validSkill("gamma"));
  assert.equal(runMain(root, "check").exitCode, 1);
  assert.equal(runMain(root, "index").exitCode, 0);
  assert.equal(runMain(root, "check").exitCode, 0);
  const index = fs.readFileSync(path.join(root, ".llm", "index.md"), "utf8");
  assert.match(index, /skills\/gamma\/SKILL\.md/);
  assert.doesNotMatch(index, /alpha/);
  for (const other of OTHER_SKILL_ROOTS) {
    assert.equal(fs.existsSync(path.join(root, other)), false, other);
  }
});

for (const other of OTHER_SKILL_ROOTS) {
  test(`a linked skill root at ${other} is rejected without following it`, (t) => {
    const root = createFixture(t, { alpha: validSkill("alpha") });
    const link = path.join(root, other);
    fs.mkdirSync(path.dirname(link), { recursive: true });
    fs.symlinkSync(root, link, "junction");
    const run = runMain(root, "index");
    assert.equal(run.exitCode, 1, run.output);
    assert.equal(fs.lstatSync(link).isSymbolicLink(), true);
  });

  for (const entry of ["", "removed-skill/references/note.md"]) {
    test(`non-skill content at ${other}/${entry} is rejected and preserved`, (t) => {
      const root = createFixture(t, { alpha: validSkill("alpha") });
      const target = path.join(root, other, entry);
      fs.mkdirSync(path.dirname(target), { recursive: true });
      fs.writeFileSync(target, "Keep this work.\n");
      const run = runMain(root, "index");
      assert.equal(run.exitCode, 1, run.output);
      assert.equal(fs.readFileSync(target, "utf8"), "Keep this work.\n");
    });
  }

  test(`empty leftover directories under ${other} do not block canonical indexing`, (t) => {
    const root = createFixture(t, { alpha: validSkill("alpha") });
    const empty = path.join(root, other, "removed-skill", "references");
    fs.mkdirSync(empty, { recursive: true });
    for (const command of ["validate", "index", "check", "index", "check"]) {
      const run = runMain(root, command);
      assert.equal(run.exitCode, 0, run.output);
    }
    assert.deepEqual(fs.readdirSync(empty), []);
    assert.equal(fs.existsSync(path.join(root, other, "alpha", "SKILL.md")), false);
  });

  for (const generated of [false, true]) {
    test(`commands reject but preserve a ${generated ? "generated" : "hand-authored"} skill under ${other}`, (t) => {
      const root = createFixture(t, { alpha: validSkill("alpha") });
      const misplaced = path.join(root, other, "alpha", "SKILL.md");
      fs.mkdirSync(path.dirname(misplaced), { recursive: true });
      const content = validSkill("alpha") + (generated ? GENERATED_MARKER : "Hand written.");
      fs.writeFileSync(misplaced, content);
      for (const command of ["validate", "index", "check"]) {
        const run = runMain(root, command);
        assert.equal(run.exitCode, 1, run.output);
        assert.ok(run.output.includes(other), run.output);
        assert.match(run.output, /must live only under \.llm\/skills/);
        assert.equal(fs.readFileSync(misplaced, "utf8"), content);
      }
      assert.equal(fs.existsSync(path.join(root, ".llm", "index.md")), false);
    });
  }
}

test("index preserves unrelated agent configuration", (t) => {
  const root = createFixture(t, { alpha: validSkill("alpha") });
  const config = path.join(root, ".claude", "settings.json");
  fs.mkdirSync(path.dirname(config), { recursive: true });
  fs.writeFileSync(config, "{}\n");
  assert.equal(runMain(root, "index").exitCode, 0);
  assert.equal(runMain(root, "check").exitCode, 0);
  assert.equal(fs.readFileSync(config, "utf8"), "{}\n");
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
    assert.equal(
      seen.has(key),
      false,
      `${skill.name} duplicates the description of ${seen.get(key)}`
    );
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
  const markdown = [
    "````markdown",
    "```",
    "[example](./nowhere.md)",
    "```",
    "````",
    "[real](./somewhere.md)"
  ].join("\n");
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
  assert.ok(
    checked > 100,
    `expected the reference corpus to be covered, checked only ${checked} files`
  );
});

test("generated skill index is deterministic and covers every skill", () => {
  // Two independent reads of the same tree, so a nondeterministic walk or sort would diverge.
  assert.equal(renderIndexMarkdown(buildManifest()), renderIndexMarkdown(buildManifest()));

  const manifest = buildManifest();
  const markdown = renderIndexMarkdown(manifest);
  for (const skill of manifest.skills) {
    assert.ok(markdown.includes(`./skills/${skill.name}/SKILL.md`), `index omits ${skill.name}`);
  }
});

test("repository skills have only one home", () => {
  const { issues } = validate();
  for (const other of OTHER_SKILL_ROOTS) {
    assert.equal(
      issues.some((issue) => issue.path.startsWith(other)),
      false,
      other
    );
  }
});
