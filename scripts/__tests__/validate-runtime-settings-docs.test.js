/**
 * @fileoverview Tests for validate-runtime-settings-docs.js
 *
 * Covers:
 * - Happy path: matching source + doc → exits 0.
 * - Drift: missing doc row, extra doc row, renamed property.
 * - BOM tolerance.
 * - Parse-error cases for missing files.
 * - Modifier coverage: static, virtual, override, new, readonly.
 * - Auto-properties: { get; }, { get; private set; }, { get; init; }.
 * - Qualified types (global::), nested generics, multi-line expression body.
 * - String-literal brace pitfall (M5): `=> "}";` does not break brace counter.
 * - Attribute between doc-comment and `public` (m5).
 * - Extra-whitespace tolerance (m6).
 * - --list-properties debug flag (m8).
 * - Real-repo state: gated on the doc file existing so this test does not
 *   block while the docs agent's deliverable is in flight.
 * - Source-order vs. doc-order parity (m11): source list must match the
 *   doc-table order so a future reorder is loud.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");

const {
  DEFAULT_SOURCE_PATH,
  DEFAULT_DOC_PATH,
  stripBom,
  normalizeLineEndings,
  stripStringsAndComments,
  consumeTypeToken,
  extractPropertyNameFromCandidate,
  extractPublicReadOnlyProperties,
  extractDocPropertyNames,
  parseMarkdownTableRow,
  stripBackticks,
  diffPropertySets,
  validate,
  reportResult,
  parseArgs
} = require("../validate-runtime-settings-docs.js");

// Snapshot of the seven public properties that exist in
// DxMessagingRuntimeSettings.cs at the time these validators were authored.
// The validator extracts them dynamically; this snapshot exists only so a
// future drift in either direction is loud.
const KNOWN_PUBLIC_PROPERTIES = [
  "IdleEvictionSeconds",
  "BufferMaxDistinctEntries",
  "BufferUseLruEviction",
  "EnableTrimApi",
  "EvictionTickIntervalSeconds",
  "EvictionEnabled",
  "MessageBufferSize"
];

let tempRoot = null;

function makeTempRoot() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "dx-runtime-settings-docs-"));
}

function writeTempFile(name, contents) {
  if (!tempRoot) {
    tempRoot = makeTempRoot();
  }
  const filePath = path.join(tempRoot, name);
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, contents, "utf8");
  return filePath;
}

function buildSource(propertyNames) {
  const propertyLines = propertyNames
    .map(
      (name) =>
        `        /// <summary>${name} property.</summary>\n` +
        `        public int ${name} => _${name.charAt(0).toLowerCase() + name.slice(1)};`
    )
    .join("\n\n");

  return [
    "namespace DxMessaging.Core.Configuration",
    "{",
    "    public sealed class DxMessagingRuntimeSettings",
    "    {",
    propertyLines,
    "    }",
    "}",
    ""
  ].join("\n");
}

function buildDoc(propertyNames, { useBackticks = true } = {}) {
  const headerLines = [
    "# Runtime Settings",
    "",
    "## Parameter Reference",
    "",
    "| Name | C# property | Type |",
    "| --- | --- | --- |"
  ];
  const dataLines = propertyNames.map((name) => {
    const cell = useBackticks ? `\`${name}\`` : name;
    return `| Friendly ${name} | ${cell} | int |`;
  });
  return [...headerLines, ...dataLines, ""].join("\n");
}

afterEach(() => {
  if (tempRoot && fs.existsSync(tempRoot)) {
    fs.rmSync(tempRoot, { recursive: true, force: true });
  }
  tempRoot = null;
});

describe("validate-runtime-settings-docs", () => {
  describe("stripBom", () => {
    test("removes a leading UTF-8 BOM", () => {
      const bom = "﻿";
      expect(stripBom(`${bom}hello`)).toBe("hello");
    });

    test("returns content unchanged when no BOM is present", () => {
      expect(stripBom("hello")).toBe("hello");
    });

    test("returns empty string for non-string input", () => {
      expect(stripBom(undefined)).toBe("");
      expect(stripBom(null)).toBe("");
    });
  });

  describe("normalizeLineEndings", () => {
    test("converts CRLF to LF and strips BOM", () => {
      const bom = "﻿";
      expect(normalizeLineEndings(`${bom}a\r\nb\r\nc`)).toBe("a\nb\nc");
    });

    test("converts lone CR to LF", () => {
      expect(normalizeLineEndings("a\rb\rc")).toBe("a\nb\nc");
    });
  });

  describe("parseMarkdownTableRow", () => {
    test("splits a simple row and trims cells", () => {
      expect(parseMarkdownTableRow("| a | b | c |")).toEqual(["a", "b", "c"]);
    });

    test("handles missing trailing pipe", () => {
      expect(parseMarkdownTableRow("| a | b | c")).toEqual(["a", "b", "c"]);
    });
  });

  describe("stripBackticks", () => {
    test("strips a single matching pair", () => {
      expect(stripBackticks("`Foo`")).toBe("Foo");
    });

    test("returns input unchanged when no backticks", () => {
      expect(stripBackticks("Foo")).toBe("Foo");
    });
  });

  describe("stripStringsAndComments", () => {
    test("blanks regular string body but preserves length", () => {
      const input = 'public int X => "}".Length;';
      const output = stripStringsAndComments(input);
      expect(output.length).toBe(input.length);
      // The closing brace inside the string must be replaced with a space.
      expect(output).not.toContain("}");
    });

    test("blanks verbatim string body including doubled quotes", () => {
      const input = 'public string X => @"foo""}bar";';
      const output = stripStringsAndComments(input);
      expect(output.length).toBe(input.length);
      expect(output).not.toContain("}");
    });

    test("blanks interpolated string but preserves outer braces", () => {
      const input = 'var s = $"{value}"; { /* opens block */ }';
      const output = stripStringsAndComments(input);
      // The braces inside the interpolation are blanked; the standalone
      // braces outside remain in place.
      const openCount = (output.match(/\{/g) || []).length;
      const closeCount = (output.match(/\}/g) || []).length;
      expect(openCount).toBe(1);
      expect(closeCount).toBe(1);
    });

    test("strips line comments", () => {
      const output = stripStringsAndComments("public int X => 1; // public int Y => }");
      expect(output).not.toContain("Y");
      expect(output).not.toContain("}");
    });

    test("strips block comments while preserving newlines", () => {
      const input = "public int X => 1; /* multi\nline\n} */ public int Y => 2;";
      const output = stripStringsAndComments(input);
      const newlineCount = (output.match(/\n/g) || []).length;
      expect(newlineCount).toBe(2);
      // Y must still appear because it follows the block comment.
      expect(output).toContain("Y");
    });
  });

  describe("consumeTypeToken", () => {
    test("consumes a simple type", () => {
      const text = "int Foo";
      expect(consumeTypeToken(text)).toBe("int".length);
    });

    test("consumes a generic type", () => {
      const text = "List<int> Foo";
      expect(consumeTypeToken(text)).toBe("List<int>".length);
    });

    test("consumes a nested-generic type", () => {
      const text = "List<KeyValuePair<int, string>> Foo";
      expect(consumeTypeToken(text)).toBe("List<KeyValuePair<int, string>>".length);
    });

    test("consumes a qualified type with global::", () => {
      const text = "global::System.Int32 Foo";
      expect(consumeTypeToken(text)).toBe("global::System.Int32".length);
    });

    test("consumes a nullable type", () => {
      const text = "int? Foo";
      expect(consumeTypeToken(text)).toBe("int?".length);
    });

    test("consumes an array type", () => {
      const text = "int[] Foo";
      expect(consumeTypeToken(text)).toBe("int[]".length);
    });

    test("returns 0 for non-type input", () => {
      expect(consumeTypeToken("=> 1;")).toBe(0);
    });
  });

  describe("extractPropertyNameFromCandidate", () => {
    test("extracts the simple form", () => {
      expect(extractPropertyNameFromCandidate("public int Foo => _x;")).toBe("Foo");
    });

    test("rejects methods", () => {
      expect(extractPropertyNameFromCandidate("public int Foo() => 1;")).toBeNull();
    });

    test("rejects non-public lines", () => {
      expect(extractPropertyNameFromCandidate("internal int Foo => _x;")).toBeNull();
    });

    test("rejects type declarations", () => {
      expect(extractPropertyNameFromCandidate("public class Foo { }")).toBeNull();
      expect(extractPropertyNameFromCandidate("public sealed class Foo { }")).toBeNull();
    });
  });

  describe("extractPublicReadOnlyProperties", () => {
    test("extracts public expression-bodied properties only", () => {
      const source = [
        "namespace X {",
        "    public sealed class DxMessagingRuntimeSettings {",
        "        internal int _idle = 0;",
        "        public int IdleEvictionSeconds => _idle;",
        "        public bool EnableTrimApi => _enable;",
        "        internal bool IsFallbackInstance => _isFallback;",
        "        private int Internal => _internal;",
        "    }",
        "}",
        ""
      ].join("\n");

      const { names } = extractPublicReadOnlyProperties(source);
      expect(names).toEqual(["IdleEvictionSeconds", "EnableTrimApi"]);
    });

    test("ignores nested-type properties", () => {
      const source = [
        "public class DxMessagingRuntimeSettings {",
        "    public int Outer => _outer;",
        "    public class Nested {",
        "        public int Inner => _inner;",
        "    }",
        "}"
      ].join("\n");

      const { names } = extractPublicReadOnlyProperties(source);
      expect(names).toEqual(["Outer"]);
    });

    test("returns empty when class is missing", () => {
      const { names } = extractPublicReadOnlyProperties(
        "public class Other { public int X => 1; }"
      );
      expect(names).toEqual([]);
    });

    test("tolerates UTF-8 BOM and CRLF", () => {
      const source =
        "﻿namespace X {\r\n" +
        "    public class DxMessagingRuntimeSettings {\r\n" +
        "        public int IdleEvictionSeconds => _x;\r\n" +
        "    }\r\n" +
        "}\r\n";
      const { names } = extractPublicReadOnlyProperties(source);
      expect(names).toEqual(["IdleEvictionSeconds"]);
    });

    test("extracts public static properties (M2)", () => {
      const source = [
        "public class DxMessagingRuntimeSettings {",
        "    public static int Foo => _foo;",
        "}"
      ].join("\n");
      const { names } = extractPublicReadOnlyProperties(source);
      expect(names).toEqual(["Foo"]);
    });

    test("extracts public virtual properties (M2)", () => {
      const source = [
        "public class DxMessagingRuntimeSettings {",
        "    public virtual int Foo => _foo;",
        "}"
      ].join("\n");
      const { names } = extractPublicReadOnlyProperties(source);
      expect(names).toEqual(["Foo"]);
    });

    test("extracts public override properties (M2)", () => {
      const source = [
        "public class DxMessagingRuntimeSettings {",
        "    public override int Foo => _foo;",
        "}"
      ].join("\n");
      const { names } = extractPublicReadOnlyProperties(source);
      expect(names).toEqual(["Foo"]);
    });

    test("extracts public new properties (M2)", () => {
      const source = [
        "public class DxMessagingRuntimeSettings {",
        "    public new int Foo => _foo;",
        "}"
      ].join("\n");
      const { names } = extractPublicReadOnlyProperties(source);
      expect(names).toEqual(["Foo"]);
    });

    test("extracts public readonly properties (M2)", () => {
      const source = [
        "public class DxMessagingRuntimeSettings {",
        "    public readonly int Foo => _foo;",
        "}"
      ].join("\n");
      const { names } = extractPublicReadOnlyProperties(source);
      expect(names).toEqual(["Foo"]);
    });

    test("extracts auto-properties: { get; } (M2)", () => {
      const source = [
        "public class DxMessagingRuntimeSettings {",
        "    public int Foo { get; }",
        "    public int Bar { get; private set; }",
        "    public int Baz { get; init; }",
        "}"
      ].join("\n");
      const { names } = extractPublicReadOnlyProperties(source);
      expect(names).toEqual(["Foo", "Bar", "Baz"]);
    });

    test("extracts qualified type names (global::) (M2)", () => {
      const source = [
        "public class DxMessagingRuntimeSettings {",
        "    public global::System.Int32 Foo => _x;",
        "}"
      ].join("\n");
      const { names } = extractPublicReadOnlyProperties(source);
      expect(names).toEqual(["Foo"]);
    });

    test("extracts nested-generic types (M2)", () => {
      const source = [
        "public class DxMessagingRuntimeSettings {",
        "    public List<KeyValuePair<int, string>> Foo => _x;",
        "}"
      ].join("\n");
      const { names } = extractPublicReadOnlyProperties(source);
      expect(names).toEqual(["Foo"]);
    });

    test("extracts multi-line expression-bodied property (M2)", () => {
      const source = [
        "public class DxMessagingRuntimeSettings {",
        "    public int Foo",
        "        => _foo;",
        "}"
      ].join("\n");
      const { names } = extractPublicReadOnlyProperties(source);
      expect(names).toEqual(["Foo"]);
    });

    test("string-literal closing brace does not fool brace counter (M5)", () => {
      const source = [
        "public class DxMessagingRuntimeSettings {",
        '    public string Stringy => "}";',
        "    public int Foo => _foo;",
        "}"
      ].join("\n");
      const { names } = extractPublicReadOnlyProperties(source);
      // Both Stringy and Foo must be extracted; the brace inside the string
      // literal must not close the class body early.
      expect(names).toEqual(["Stringy", "Foo"]);
    });

    test("interpolated string brace does not fool brace counter (M5)", () => {
      const source = [
        "public class DxMessagingRuntimeSettings {",
        '    public string Stringy => $"value: {{0}}";',
        "    public int Foo => _foo;",
        "}"
      ].join("\n");
      const { names } = extractPublicReadOnlyProperties(source);
      expect(names).toEqual(["Stringy", "Foo"]);
    });

    test("attribute between doc-comment and public is tolerated (m5)", () => {
      const source = [
        "public class DxMessagingRuntimeSettings {",
        "    /// <summary>Old.</summary>",
        "    [Obsolete]",
        "    public int Foo => _foo;",
        "}"
      ].join("\n");
      const { names } = extractPublicReadOnlyProperties(source);
      expect(names).toEqual(["Foo"]);
    });

    test("extra-whitespace tolerance (m6)", () => {
      const source = [
        "public class DxMessagingRuntimeSettings {",
        "    public  int   Foo  =>  _x ;",
        "}"
      ].join("\n");
      const { names } = extractPublicReadOnlyProperties(source);
      expect(names).toEqual(["Foo"]);
    });

    test("ignores methods that look like properties", () => {
      const source = [
        "public class DxMessagingRuntimeSettings {",
        "    public int Foo => _foo;",
        "    public int GetFoo() => _foo;",
        "    public int Compute(int x) => x + 1;",
        "}"
      ].join("\n");
      const { names } = extractPublicReadOnlyProperties(source);
      expect(names).toEqual(["Foo"]);
    });
  });

  describe("extractDocPropertyNames", () => {
    test("extracts property names from the C# property column with backticks", () => {
      const doc = buildDoc(["IdleEvictionSeconds", "EnableTrimApi"]);
      const { names } = extractDocPropertyNames(doc);
      expect(names).toEqual(["IdleEvictionSeconds", "EnableTrimApi"]);
    });

    test("extracts property names without backticks", () => {
      const doc = buildDoc(["IdleEvictionSeconds"], { useBackticks: false });
      const { names } = extractDocPropertyNames(doc);
      expect(names).toEqual(["IdleEvictionSeconds"]);
    });

    test("ignores section headings that look like property names", () => {
      const doc = [
        "# Runtime Settings",
        "",
        "### `IdleEvictionSeconds`",
        "",
        "Some prose.",
        "",
        "## Parameter Reference",
        "",
        "| Name | C# property | Type |",
        "| --- | --- | --- |",
        "| Foo | `EnableTrimApi` | bool |",
        ""
      ].join("\n");

      const { names } = extractDocPropertyNames(doc);
      expect(names).toEqual(["EnableTrimApi"]);
    });

    test("returns empty when no parameter table is present", () => {
      const { names } = extractDocPropertyNames("# Runtime Settings\n\nNo table.\n");
      expect(names).toEqual([]);
    });
  });

  describe("diffPropertySets", () => {
    test("reports missing and extra independently", () => {
      const result = diffPropertySets(["A", "B", "C"], ["B", "C", "D"]);
      expect(result.missingInDoc).toEqual(["A"]);
      expect(result.extraInDoc).toEqual(["D"]);
    });
  });

  describe("validate", () => {
    test("happy path: source and doc agree", () => {
      const sourcePath = writeTempFile(
        "Runtime/Core/Configuration/DxMessagingRuntimeSettings.cs",
        buildSource(["A", "B", "C"])
      );
      const docPath = writeTempFile(
        "docs/reference/runtime-settings.md",
        buildDoc(["A", "B", "C"])
      );

      const result = validate({ sourcePath, docPath });
      expect(result.valid).toBe(true);
      expect(result.errors).toEqual([]);
      expect(result.sourceNames).toEqual(["A", "B", "C"]);
      expect(result.docNames).toEqual(["A", "B", "C"]);
    });

    test("missing doc row fires when source has more properties", () => {
      const sourcePath = writeTempFile(
        "Runtime/Core/Configuration/DxMessagingRuntimeSettings.cs",
        buildSource(["A", "B", "C"])
      );
      const docPath = writeTempFile("docs/reference/runtime-settings.md", buildDoc(["A", "B"]));

      const result = validate({ sourcePath, docPath });
      expect(result.valid).toBe(false);
      const missing = result.errors.filter((error) => error.type === "missing-doc-row");
      expect(missing).toHaveLength(1);
      expect(missing[0].name).toBe("C");
      expect(missing[0].message).toContain("'C'");
    });

    test("extra doc row fires when doc references unknown property", () => {
      const sourcePath = writeTempFile(
        "Runtime/Core/Configuration/DxMessagingRuntimeSettings.cs",
        buildSource(["A", "B"])
      );
      const docPath = writeTempFile(
        "docs/reference/runtime-settings.md",
        buildDoc(["A", "B", "C"])
      );

      const result = validate({ sourcePath, docPath });
      expect(result.valid).toBe(false);
      const extras = result.errors.filter((error) => error.type === "extra-doc-row");
      expect(extras).toHaveLength(1);
      expect(extras[0].name).toBe("C");
    });

    test("renamed property: missing-doc-row + extra-doc-row both fire", () => {
      const sourcePath = writeTempFile(
        "Runtime/Core/Configuration/DxMessagingRuntimeSettings.cs",
        buildSource(["A", "B", "RenamedNew"])
      );
      const docPath = writeTempFile(
        "docs/reference/runtime-settings.md",
        buildDoc(["A", "B", "RenamedOld"])
      );

      const result = validate({ sourcePath, docPath });
      expect(result.valid).toBe(false);
      const types = result.errors.map((error) => error.type);
      expect(types).toContain("missing-doc-row");
      expect(types).toContain("extra-doc-row");
    });

    test("internal property is treated as removed (extra-doc-row fires)", () => {
      const source = [
        "public class DxMessagingRuntimeSettings {",
        "    public int Kept => _kept;",
        "    internal int Hidden => _hidden;",
        "}",
        ""
      ].join("\n");
      const sourcePath = writeTempFile(
        "Runtime/Core/Configuration/DxMessagingRuntimeSettings.cs",
        source
      );
      const docPath = writeTempFile(
        "docs/reference/runtime-settings.md",
        buildDoc(["Kept", "Hidden"])
      );

      const result = validate({ sourcePath, docPath });
      expect(result.valid).toBe(false);
      expect(
        result.errors.some((error) => error.type === "extra-doc-row" && error.name === "Hidden")
      ).toBe(true);
      expect(result.errors.some((error) => error.type === "missing-doc-row")).toBe(false);
    });

    test("BOM tolerance: source and doc with BOM still parse cleanly", () => {
      const bom = "﻿";
      const sourcePath = writeTempFile(
        "Runtime/Core/Configuration/DxMessagingRuntimeSettings.cs",
        bom + buildSource(["A"])
      );
      const docPath = writeTempFile("docs/reference/runtime-settings.md", bom + buildDoc(["A"]));

      const result = validate({ sourcePath, docPath });
      expect(result.valid).toBe(true);
    });

    test("parse-error: source file missing", () => {
      const docPath = writeTempFile("docs/reference/runtime-settings.md", buildDoc(["A"]));
      const result = validate({
        sourcePath: path.join(os.tmpdir(), "definitely-does-not-exist-12345.cs"),
        docPath
      });
      expect(result.valid).toBe(false);
      expect(result.errors).toHaveLength(1);
      expect(result.errors[0].type).toBe("parse-error");
    });

    test("parse-error: doc file missing", () => {
      const sourcePath = writeTempFile(
        "Runtime/Core/Configuration/DxMessagingRuntimeSettings.cs",
        buildSource(["A"])
      );
      const result = validate({
        sourcePath,
        docPath: path.join(os.tmpdir(), "definitely-does-not-exist-67890.md")
      });
      expect(result.valid).toBe(false);
      expect(result.errors).toHaveLength(1);
      expect(result.errors[0].type).toBe("parse-error");
    });

    test("parse-error: source file present but no public properties found", () => {
      const sourcePath = writeTempFile(
        "Runtime/Core/Configuration/DxMessagingRuntimeSettings.cs",
        "public class DxMessagingRuntimeSettings { /* empty */ }\n"
      );
      const docPath = writeTempFile("docs/reference/runtime-settings.md", buildDoc(["A"]));

      const result = validate({ sourcePath, docPath });
      expect(result.valid).toBe(false);
      expect(result.errors[0].type).toBe("parse-error");
    });
  });

  describe("reportResult", () => {
    test("returns 0 and logs OK on success", () => {
      const messages = [];
      const fakeLogger = { log: (message) => messages.push(message) };
      const exitCode = reportResult(
        { valid: true, errors: [], sourceNames: ["A"], docNames: ["A"] },
        { logger: fakeLogger }
      );
      expect(exitCode).toBe(0);
      expect(messages.some((message) => message.includes("OK"))).toBe(true);
    });

    test("returns 1 and lists errors on failure", () => {
      const messages = [];
      const fakeLogger = { log: (message) => messages.push(message) };
      const exitCode = reportResult(
        {
          valid: false,
          errors: [{ type: "missing-doc-row", name: "X", message: "X is missing" }],
          sourceNames: [],
          docNames: []
        },
        { logger: fakeLogger }
      );
      expect(exitCode).toBe(1);
      expect(messages.join("\n")).toContain("missing-doc-row");
    });
  });

  describe("parseArgs", () => {
    test("parses --check flag", () => {
      expect(parseArgs(["--check"]).check).toBe(true);
    });

    test("parses --list-properties flag (m8)", () => {
      expect(parseArgs(["--list-properties"]).listProperties).toBe(true);
    });

    test("collects unknown flags as errors", () => {
      const result = parseArgs(["--bogus"]);
      expect(result.errors).toEqual(["Unknown argument: --bogus"]);
    });

    test("parses --help flag", () => {
      const result = parseArgs(["--help"]);
      expect(result.help).toBe(true);
    });
  });

  describe("real repository state", () => {
    const sourceExists = fs.existsSync(DEFAULT_SOURCE_PATH);
    const docExists = fs.existsSync(DEFAULT_DOC_PATH);

    if (!sourceExists) {
      // The C# file is a fixture in this repo; if it disappears that itself
      // is a problem worth surfacing.
      test.skip("source file exists at the expected path (skipped: source file missing)", () => {});
    } else {
      test("source file exposes the snapshot of seven public properties", () => {
        const content = fs.readFileSync(DEFAULT_SOURCE_PATH, "utf8");
        const { names } = extractPublicReadOnlyProperties(content);
        // Order-insensitive comparison so a future reorder does not flake.
        expect(names.slice().sort()).toEqual(KNOWN_PUBLIC_PROPERTIES.slice().sort());
      });
    }

    if (!docExists) {
      // Skip until the docs agent's deliverable lands. Mark the skip clearly
      // so a CI run reports the gating reason.
      test.skip("real repo: source and doc agree (skipped: docs/reference/runtime-settings.md not present yet)", () => {});
      test.skip("real repo: source-order matches doc-table order (skipped: doc not present yet) (m11)", () => {});
    } else {
      test("real repo: source and doc agree", () => {
        const result = validate();
        if (!result.valid) {
          // Surface the names so a maintainer reading the test failure does
          // not have to re-run the validator manually to see the drift.
          const messages = result.errors
            .map((error) => `[${error.type}] ${error.message}`)
            .join("\n");
          throw new Error(`validate-runtime-settings-docs failed:\n${messages}`);
        }
        expect(result.valid).toBe(true);
      });

      test("real repo: source-order matches doc-table order (m11)", () => {
        // A stricter parity check than the order-insensitive snapshot above.
        // Source order and doc-table order should match so a future reorder
        // is loud. If the two orderings ever diverge intentionally, this
        // test can be relaxed to the order-insensitive form.
        const result = validate();
        expect(result.sourceNames).toEqual(result.docNames);
      });
    }
  });
});
