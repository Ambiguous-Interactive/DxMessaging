/**
 * @fileoverview Tests for scripts/lib/devcontainer-jsonc.js and its bash
 * counterpart at .devcontainer/lib/parse-devcontainer-mounts.sh. These are
 * the primary parser for .devcontainer/devcontainer.json -- the previous
 * grep-based fallback in validate-caching.sh kept unresolved template
 * variables in the mount strings, which broke the CI mount-contract check.
 *
 * The parity block exercises both implementations against a shared fixture
 * set so the shell and Node parsers cannot drift.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const childProcess = require("child_process");

const {
  stripJsoncComments,
  parseDevcontainerMounts,
  getDevcontainerProperty
} = require("../lib/devcontainer-jsonc");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const REAL_DEVCONTAINER_JSON = path.join(REPO_ROOT, ".devcontainer", "devcontainer.json");
const REAL_CACHE_CONTRACT = path.join(REPO_ROOT, ".devcontainer", "cache-contract.sh");
const SHELL_PARSER = path.join(REPO_ROOT, ".devcontainer", "lib", "parse-devcontainer-mounts.sh");

const CONTAINER_WORKSPACE_FOLDER = "/workspaces/com.wallstop-studios.dxmessaging";
const LOCAL_WORKSPACE_FOLDER = "/local/host/workspace/dxmessaging";

const STRIP_FIXTURES = [
  {
    name: "line comment immediately before mount entry",
    input:
      '{\n  // outer header comment\n  "mounts": [\n    // pinned cache\n    "source=a,target=/x,type=volume"\n  ]\n}',
    expectedStrings: ["source=a,target=/x,type=volume"]
  },
  {
    name: "block comment between mounts",
    input:
      '{\n  "mounts": [\n    "source=a,target=/x,type=volume",\n    /* historical bind\n       across rebuilds */\n    "source=b,target=/y,type=volume"\n  ]\n}',
    expectedStrings: ["source=a,target=/x,type=volume", "source=b,target=/y,type=volume"]
  },
  {
    name: "// inside JSON string must be preserved verbatim",
    input:
      '{\n  "homepage": "https://example.com/docs",\n  "mounts": [\n    "source=a,target=/x,type=volume"\n  ]\n}',
    expectedStrings: ["https://example.com/docs", "source=a,target=/x,type=volume"]
  },
  {
    name: "/* inside JSON string must be preserved verbatim",
    input: '{\n  "pattern": "comment /* literal */ stay",\n  "mounts": []\n}',
    expectedStrings: ["comment /* literal */ stay"]
  },
  {
    name: "multiple template variables in a single mount target",
    input:
      '{\n  "mounts": [\n    "source=a,target=${containerWorkspaceFolder}/node_modules,type=volume",\n    "source=b,target=${localWorkspaceFolder}/host,type=bind"\n  ]\n}',
    expectedStrings: [
      "source=a,target=${containerWorkspaceFolder}/node_modules,type=volume",
      "source=b,target=${localWorkspaceFolder}/host,type=bind"
    ]
  },
  {
    name: "trailing comma after mount entry (JSONC tolerance probe)",
    input: '{\n  "mounts": [\n    "source=a,target=/x,type=volume",\n  ]\n}',
    // Strict JSON.parse will reject the trailing comma; the strip pass MUST
    // preserve the comma so the downstream JSON.parse error is faithful.
    expectedStrings: ['"source=a,target=/x,type=volume",']
  },
  {
    name: "escaped quote inside string does not end the string",
    input: '{\n  "label": "he said \\"hi\\" // not a comment",\n  "mounts": []\n}',
    expectedStrings: ['he said \\"hi\\" // not a comment']
  },
  {
    name: "leading UTF-8 BOM is stripped",
    input: '﻿{\n  // header\n  "mounts": [\n    "source=a,target=/x,type=volume"\n  ]\n}',
    expectedStrings: ["source=a,target=/x,type=volume"]
  },
  {
    name: "CRLF line endings are normalized to LF",
    input:
      '{\r\n  // header\r\n  "mounts": [\r\n    "source=a,target=/x,type=volume"\r\n  ]\r\n}\r\n',
    expectedStrings: ["source=a,target=/x,type=volume"]
  }
];

describe("stripJsoncComments", () => {
  test.each(STRIP_FIXTURES)("$name", ({ input, expectedStrings }) => {
    const stripped = stripJsoncComments(input);
    for (const expected of expectedStrings) {
      expect(stripped).toContain(expected);
    }
    // Comments must NOT survive (excluding contents inside strings).
    // A simple structural assertion: the stripped output, with strings
    // replaced by placeholders, contains no `//` and no `/*` outside of
    // strings.
    const withoutStrings = stripped.replace(/"(?:\\.|[^"\\])*"/g, '""');
    expect(withoutStrings).not.toMatch(/\/\//);
    expect(withoutStrings).not.toMatch(/\/\*/);
  });

  test("rejects non-string input", () => {
    expect(() => stripJsoncComments(null)).toThrow(/expected string/i);
    expect(() => stripJsoncComments(123)).toThrow(/expected string/i);
  });

  test("preserves line count for downstream error reporting", () => {
    const input = "{\n// drop\n/* keep newline\nlines */\n}\n";
    const stripped = stripJsoncComments(input);
    expect(stripped.split("\n").length).toBe(input.split("\n").length);
  });

  test("leading BOM is stripped only once, BOM-like char inside string is preserved", () => {
    const bomChar = String.fromCharCode(0xfeff);
    const input = `${bomChar}{ "label": "before${bomChar}after" }`;
    const stripped = stripJsoncComments(input);
    // Index 0 BOM gone.
    expect(stripped.charCodeAt(0)).toBe("{".charCodeAt(0));
    // BOM-like char inside the string MUST survive.
    expect(stripped).toContain(`before${bomChar}after`);
  });

  test("CRLF is normalized to LF before parsing", () => {
    const stripped = stripJsoncComments("{\r\n  // x\r\n}\r\n");
    expect(stripped.includes("\r")).toBe(false);
    expect(stripped.split("\n").length).toBeGreaterThanOrEqual(3);
  });
});

const PARSE_FIXTURES = [
  {
    name: "string-form mounts with container workspace folder",
    input:
      '{\n  "mounts": [\n    "source=dxm-node-modules,target=${containerWorkspaceFolder}/node_modules,type=volume"\n  ]\n}',
    expected: [
      {
        source: "dxm-node-modules",
        target: `${CONTAINER_WORKSPACE_FOLDER}/node_modules`,
        type: "volume"
      }
    ]
  },
  {
    name: "mix of static and templated targets",
    input:
      '{\n  "mounts": [\n    "source=dxm-nuget-cache,target=/home/vscode/.nuget,type=volume",\n    "source=dxm-node-modules,target=${containerWorkspaceFolder}/node_modules,type=volume"\n  ]\n}',
    expected: [
      { source: "dxm-nuget-cache", target: "/home/vscode/.nuget", type: "volume" },
      {
        source: "dxm-node-modules",
        target: `${CONTAINER_WORKSPACE_FOLDER}/node_modules`,
        type: "volume"
      }
    ]
  },
  {
    name: "comments around mounts do not perturb parse",
    input:
      '{\n  // header\n  "mounts": [\n    /* pinned */\n    "source=a,target=/x,type=volume"\n  ]\n}',
    expected: [{ source: "a", target: "/x", type: "volume" }]
  },
  {
    name: "object-form mount entries",
    input: '{\n  "mounts": [\n    { "source": "a", "target": "/x", "type": "volume" }\n  ]\n}',
    expected: [{ source: "a", target: "/x", type: "volume" }]
  },
  {
    name: "local workspace folder substitution",
    input:
      '{\n  "mounts": [\n    "source=host,target=${localWorkspaceFolder}/projects,type=bind"\n  ]\n}',
    expected: [
      {
        source: "host",
        target: `${LOCAL_WORKSPACE_FOLDER}/projects`,
        type: "bind"
      }
    ]
  },
  {
    name: "missing mounts array returns empty list",
    input: '{ "remoteUser": "vscode" }',
    expected: []
  },
  {
    name: "leading UTF-8 BOM is parsed cleanly (parity probe)",
    input: '﻿{\n  "mounts": [\n    "source=bom-cache,target=/cache,type=volume"\n  ]\n}',
    expected: [{ source: "bom-cache", target: "/cache", type: "volume" }]
  },
  {
    name: "CRLF line endings parse to the same mount tuples (parity probe)",
    input:
      '{\r\n  "mounts": [\r\n    "source=crlf-cache,target=/cache,type=volume"\r\n  ]\r\n}\r\n',
    expected: [{ source: "crlf-cache", target: "/cache", type: "volume" }]
  },
  {
    // The devcontainer mount spec allows additional fields beyond
    // source/target/type (e.g., readonly, consistency, bind-propagation).
    // The parser MUST extract the contract triple cleanly while ignoring
    // (but not failing on) extra fields. This locks the behavior in so a
    // regression that starts rejecting valid extra fields surfaces here.
    name: "extra mount fields are accepted (extracted triple matches contract)",
    input:
      '{\n  "mounts": [\n    "source=extra-fields,target=/x,type=volume,readonly=true,bind-propagation=rprivate"\n  ]\n}',
    expected: [{ source: "extra-fields", target: "/x", type: "volume" }]
  }
];

describe("parseDevcontainerMounts", () => {
  test.each(PARSE_FIXTURES)("$name", ({ input, expected }) => {
    const mounts = parseDevcontainerMounts(input, {
      containerWorkspaceFolder: CONTAINER_WORKSPACE_FOLDER,
      localWorkspaceFolder: LOCAL_WORKSPACE_FOLDER
    });
    expect(mounts).toHaveLength(expected.length);
    for (let i = 0; i < expected.length; i++) {
      expect(mounts[i]).toMatchObject(expected[i]);
    }
  });

  test("throws on invalid JSON after comment strip", () => {
    expect(() =>
      parseDevcontainerMounts('{ "mounts": [ "bad" ', {
        containerWorkspaceFolder: CONTAINER_WORKSPACE_FOLDER
      })
    ).toThrow(/parseDevcontainerMounts.*JSON\.parse failed/);
  });

  test("real .devcontainer/devcontainer.json parses with every contract target resolved", () => {
    const text = fs.readFileSync(REAL_DEVCONTAINER_JSON, "utf8");
    const mounts = parseDevcontainerMounts(text, {
      containerWorkspaceFolder: CONTAINER_WORKSPACE_FOLDER,
      localWorkspaceFolder: LOCAL_WORKSPACE_FOLDER
    });

    expect(mounts.length).toBeGreaterThan(0);

    const contract = fs.readFileSync(REAL_CACHE_CONTRACT, "utf8");
    const sources = extractBashArray(contract, "CACHE_MOUNT_SOURCES");
    const rawTargets = extractBashArray(contract, "CACHE_MOUNT_TARGETS");
    const resolvedTargets = rawTargets.map((value) =>
      value.replace(/\$\{CACHE_WORKSPACE_ROOT\}/g, CONTAINER_WORKSPACE_FOLDER)
    );

    for (let i = 0; i < sources.length; i++) {
      const expectedSource = sources[i];
      const expectedTarget = resolvedTargets[i];
      const match = mounts.find(
        (mount) =>
          mount.source === expectedSource &&
          mount.target === expectedTarget &&
          mount.type === "volume"
      );
      expect(match).toBeDefined();
    }
  });
});

function extractBashArray(content, arrayName) {
  const re = new RegExp(
    `^\\s*(?:readonly\\s+|declare\\s+-[a-z]+\\s+)?${arrayName}\\s*=\\s*\\(([\\s\\S]*?)\\)`,
    "m"
  );
  const match = content.match(re);
  if (!match) {
    throw new Error(`bash array ${arrayName} not found`);
  }
  return match[1]
    .split("\n")
    .map((line) => line.replace(/#.*$/, "").trim())
    .filter((line) => line.length > 0)
    .map((line) => line.replace(/^["']|["']$/g, ""));
}

// =============================================================================
// getDevcontainerProperty: Node-side mirror of the shell helper. The Jest
// parity block further down exercises the bash counterpart against the same
// fixtures.
// =============================================================================
describe("getDevcontainerProperty (Node)", () => {
  const NODE_PROPERTY_FIXTURES = [
    {
      name: "live value with no comments",
      input: '{\n  "remoteUser": "vscode"\n}',
      property: "remoteUser",
      expected: "vscode"
    },
    {
      name: "commented-out alternative next to live value -- returns LIVE",
      input: '{\n  // "remoteUser": "root",\n  "remoteUser": "vscode"\n}',
      property: "remoteUser",
      expected: "vscode"
    },
    {
      name: "value inside a string literal that LOOKS like the key -- ignored",
      input: '{\n  "description": "set remoteUser to vscode if needed",\n  "remoteUser": "root"\n}',
      property: "remoteUser",
      expected: "root"
    },
    {
      name: "missing property returns undefined",
      input: '{\n  "image": "ghcr.io/example/foo"\n}',
      property: "remoteUser",
      expected: undefined
    },
    {
      name: "explicit null value returns undefined",
      input: '{\n  "remoteUser": null\n}',
      property: "remoteUser",
      expected: undefined
    },
    {
      name: "block comment preceding live value",
      input: '{\n  /* legacy:\n     "remoteUser": "root"\n  */\n  "remoteUser": "vscode"\n}',
      property: "remoteUser",
      expected: "vscode"
    },
    {
      name: "BOM-prefixed JSON parses cleanly",
      input: '﻿{\n  "remoteUser": "vscode"\n}',
      property: "remoteUser",
      expected: "vscode"
    },
    {
      name: "CRLF line endings parse cleanly",
      input: '{\r\n  "remoteUser": "vscode"\r\n}\r\n',
      property: "remoteUser",
      expected: "vscode"
    },
    {
      name: "numeric value is stringified",
      input: '{ "containerPort": 8080 }',
      property: "containerPort",
      expected: "8080"
    },
    {
      name: "boolean value is stringified",
      input: '{ "privileged": true }',
      property: "privileged",
      expected: "true"
    }
  ];

  test.each(NODE_PROPERTY_FIXTURES)("$name", ({ input, property, expected }) => {
    expect(getDevcontainerProperty(input, property)).toBe(expected);
  });

  test("rejects empty property name", () => {
    expect(() => getDevcontainerProperty('{"a":1}', "")).toThrow(/non-empty string/);
  });

  test("throws with diagnostic excerpt on invalid JSON", () => {
    expect(() => getDevcontainerProperty('{ "remoteUser":', "remoteUser")).toThrow(
      /getDevcontainerProperty:.*JSON\.parse failed/
    );
  });

  // Round-3 MINOR-C: composite (array/object) values FAIL LOUDLY. The
  // previous behavior returned `String(value)` which gives the readable-
  // garbage "[object Object]" (objects) and "1,2,3" (arrays) -- both of
  // which DISAGREE with the bash counterpart, whose `tostring` emits
  // valid JSON. Forcing both sides to throw eliminates the silent
  // divergence; callers that need composite values must use
  // `parseDevcontainerMounts` (for mounts) or
  // `JSON.parse(stripJsoncComments(text))` directly.
  test("throws for array-valued property", () => {
    expect(() => getDevcontainerProperty('{ "mounts": ["a", "b"] }', "mounts")).toThrow(
      /non-scalar array/
    );
  });

  test("throws for object-valued property", () => {
    expect(() =>
      getDevcontainerProperty(
        '{ "build": { "context": "..", "dockerfile": "Dockerfile" } }',
        "build"
      )
    ).toThrow(/non-scalar object/);
  });

  test("throws for nested-array property", () => {
    expect(() => getDevcontainerProperty('{ "matrix": [[1, 2], [3, 4]] }', "matrix")).toThrow(
      /non-scalar array/
    );
  });
});

// =============================================================================
// Parity tests: shell implementation must agree with the Node implementation
// for every shared fixture. The shell parser is only tested when bash and jq
// are available (devcontainer + Linux/macOS CI runners). On runners that lack
// either tool the suite reports the gap as a single skipped test (CI fails
// loudly via the dedicated devcontainer-test job; this skip is observability).
// =============================================================================
const HAS_BASH = canRun("bash", ["--version"]);
const HAS_JQ = canRun("jq", ["--version"]);
const SHELL_AVAILABLE = HAS_BASH && HAS_JQ && fs.existsSync(SHELL_PARSER);

(SHELL_AVAILABLE ? describe : describe.skip)("shell parser parity with Node parser", () => {
  test.each(PARSE_FIXTURES)("$name", ({ input, expected }) => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "devcontainer-jsonc-"));
    try {
      const tempFile = path.join(tempDir, "devcontainer.json");
      fs.writeFileSync(tempFile, input, "utf8");
      const result = childProcess.spawnSync(
        "bash",
        [SHELL_PARSER, "mounts", tempFile, CONTAINER_WORKSPACE_FOLDER, LOCAL_WORKSPACE_FOLDER],
        { encoding: "utf8" }
      );
      expect(result.status).toBe(0);
      const shellLines = result.stdout
        .split("\n")
        .map((line) => line.trim())
        .filter((line) => line.length > 0);
      expect(shellLines).toHaveLength(expected.length);
      for (let i = 0; i < expected.length; i++) {
        const { source, target, type } = expected[i];
        expect(shellLines[i]).toContain(`source=${source}`);
        expect(shellLines[i]).toContain(`target=${target}`);
        expect(shellLines[i]).toContain(`type=${type}`);
      }
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("parses the real .devcontainer/devcontainer.json", () => {
    const result = childProcess.spawnSync(
      "bash",
      [
        SHELL_PARSER,
        "mounts",
        REAL_DEVCONTAINER_JSON,
        CONTAINER_WORKSPACE_FOLDER,
        LOCAL_WORKSPACE_FOLDER
      ],
      { encoding: "utf8" }
    );
    expect(result.status).toBe(0);
    const text = fs.readFileSync(REAL_DEVCONTAINER_JSON, "utf8");
    const nodeMounts = parseDevcontainerMounts(text, {
      containerWorkspaceFolder: CONTAINER_WORKSPACE_FOLDER,
      localWorkspaceFolder: LOCAL_WORKSPACE_FOLDER
    });
    const shellLines = result.stdout
      .split("\n")
      .map((line) => line.trim())
      .filter((line) => line.length > 0);
    expect(shellLines).toHaveLength(nodeMounts.length);
    for (let i = 0; i < nodeMounts.length; i++) {
      const expected = nodeMounts[i];
      expect(shellLines[i]).toContain(`source=${expected.source}`);
      expect(shellLines[i]).toContain(`target=${expected.target}`);
      expect(shellLines[i]).toContain(`type=${expected.type}`);
    }
  });
});

function canRun(command, args) {
  try {
    const result = childProcess.spawnSync(command, args, { stdio: "ignore" });
    return result.status === 0;
  } catch {
    return false;
  }
}

// =============================================================================
// get_devcontainer_property: shell-only helper added in the same round as the
// parse-devcontainer-mounts hardening. Used by .devcontainer/validate-caching.sh
// to read a top-level scalar property (e.g. remoteUser) without falling for the
// `// "remoteUser": "vscode"` next-to-`"remoteUser": "root"` JSONC trap.
// =============================================================================
const PROPERTY_FIXTURES = [
  {
    name: "live value with no comments",
    input: '{\n  "remoteUser": "vscode"\n}',
    property: "remoteUser",
    expected: "vscode"
  },
  {
    name: "commented-out alternative next to live value -- must return LIVE",
    input: '{\n  // "remoteUser": "root",\n  "remoteUser": "vscode"\n}',
    property: "remoteUser",
    expected: "vscode"
  },
  {
    name: "value inside a string literal that LOOKS like the key -- must NOT match",
    input: '{\n  "description": "set remoteUser to vscode if needed",\n  "remoteUser": "root"\n}',
    property: "remoteUser",
    expected: "root"
  },
  {
    name: "missing property returns empty string (and exit 0)",
    input: '{\n  "image": "ghcr.io/example/foo"\n}',
    property: "remoteUser",
    expected: ""
  },
  {
    name: "block comment preceding live value",
    input: '{\n  /* legacy:\n     "remoteUser": "root"\n  */\n  "remoteUser": "vscode"\n}',
    property: "remoteUser",
    expected: "vscode"
  },
  {
    name: "BOM-prefixed JSON parses cleanly",
    input: '﻿{\n  "remoteUser": "vscode"\n}',
    property: "remoteUser",
    expected: "vscode"
  },
  {
    name: "CRLF line endings parse cleanly",
    input: '{\r\n  "remoteUser": "vscode"\r\n}\r\n',
    property: "remoteUser",
    expected: "vscode"
  }
];

(SHELL_AVAILABLE ? describe : describe.skip)("get_devcontainer_property", () => {
  test.each(PROPERTY_FIXTURES)("$name", ({ input, property, expected }) => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "devcontainer-prop-"));
    try {
      const tempFile = path.join(tempDir, "devcontainer.json");
      fs.writeFileSync(tempFile, input, "utf8");
      const result = childProcess.spawnSync(
        "bash",
        [SHELL_PARSER, "property", tempFile, property],
        { encoding: "utf8" }
      );
      expect(result.status).toBe(0);
      expect(result.stdout).toBe(expected);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("invalid JSON returns non-zero exit", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "devcontainer-prop-"));
    try {
      const tempFile = path.join(tempDir, "devcontainer.json");
      fs.writeFileSync(tempFile, '{ "remoteUser":', "utf8");
      const result = childProcess.spawnSync(
        "bash",
        [SHELL_PARSER, "property", tempFile, "remoteUser"],
        { encoding: "utf8" }
      );
      expect(result.status).not.toBe(0);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("missing property argument returns non-zero exit", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "devcontainer-prop-"));
    try {
      const tempFile = path.join(tempDir, "devcontainer.json");
      fs.writeFileSync(tempFile, '{ "remoteUser": "vscode" }', "utf8");
      const result = childProcess.spawnSync("bash", [SHELL_PARSER, "property", tempFile], {
        encoding: "utf8"
      });
      expect(result.status).not.toBe(0);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  // Round-3 MINOR-C: parity probe. The bash and Node implementations
  // MUST agree on the "non-scalar property" failure mode. Both throw /
  // exit non-zero with a diagnostic. The fixtures below exercise the
  // bash side; the corresponding Node-side assertions live in the
  // `getDevcontainerProperty (Node)` describe block.
  const COMPOSITE_PROPERTY_FIXTURES = [
    {
      name: "array-valued property fails loudly",
      input: '{ "mounts": ["a", "b"] }',
      property: "mounts",
      expectedStderrMatch: /non-scalar array/
    },
    {
      name: "object-valued property fails loudly",
      input: '{ "build": { "context": "..", "dockerfile": "Dockerfile" } }',
      property: "build",
      expectedStderrMatch: /non-scalar object/
    },
    {
      name: "nested-array property fails loudly",
      input: '{ "matrix": [[1, 2], [3, 4]] }',
      property: "matrix",
      expectedStderrMatch: /non-scalar array/
    }
  ];

  test.each(COMPOSITE_PROPERTY_FIXTURES)("$name", ({ input, property, expectedStderrMatch }) => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "devcontainer-prop-"));
    try {
      const tempFile = path.join(tempDir, "devcontainer.json");
      fs.writeFileSync(tempFile, input, "utf8");
      const result = childProcess.spawnSync(
        "bash",
        [SHELL_PARSER, "property", tempFile, property],
        { encoding: "utf8" }
      );
      // Non-zero exit AND a diagnostic on stderr -- exactly matching
      // the Node side's TypeError.
      expect(result.status).not.toBe(0);
      expect(result.stderr).toMatch(expectedStderrMatch);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  // Round-3 MINOR-C: ANY scalar fixture in this block must agree
  // BYTE-FOR-BYTE with the Node side, AND every composite fixture
  // above must fail on BOTH sides with the same surface-level shape
  // (TypeError-ish message vs non-zero exit + stderr). This cross-
  // check asserts the agreement explicitly in case the Node fixture
  // table drifts away from the bash one.
  test("composite-failure semantics match Node-side throws", () => {
    for (const fixture of COMPOSITE_PROPERTY_FIXTURES) {
      expect(() => getDevcontainerProperty(fixture.input, fixture.property)).toThrow(
        /non-scalar (array|object)/
      );
    }
  });
});
