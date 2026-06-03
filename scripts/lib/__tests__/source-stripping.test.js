/**
 * @fileoverview Unit tests for scripts/lib/source-stripping.js.
 *
 * Pins behavior used by the `--testRunner` injection policy guards. If these
 * tests fail, the static-analysis tests that depend on this helper will
 * produce false positives or false negatives.
 *
 * Covers in particular the bypass-class fixed in the state-machine tokenizer
 * rewrite: a block-comment regex that matched across string literals could
 * be defeated by source like `const a = "/" + "*"; ...; const b = "*" + "/";`
 * (with the tokens spelled as actual `/*` and `*\/` inside strings). The
 * tokenizer recognizes such tokens as STRING CONTENT and preserves the code
 * between the strings.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const {
  stripJsCommentsAndStrings,
  extractCommentsOnly,
  maskCommentsAndStrings
} = require("../source-stripping");

const REPO_ROOT = path.resolve(__dirname, "..", "..", "..");
const RUN_MANAGED_JEST_PATH = path.join(REPO_ROOT, "scripts", "run-managed-jest.js");

describe("stripJsCommentsAndStrings", () => {
  // Sentinel inputs: empty/non-string return "", all-whitespace is returned
  // unchanged. One row per former per-assertion case.
  const STRIP_SENTINEL_CASES = [
    { name: "empty input returns empty output", input: "", expected: "" },
    { name: "undefined input returns empty string", input: undefined, expected: "" },
    { name: "null input returns empty string", input: null, expected: "" },
    { name: "numeric input returns empty string", input: 42, expected: "" },
    { name: "Buffer input returns empty string", input: Buffer.from("x"), expected: "" },
    {
      name: "all-whitespace input returns the same whitespace",
      input: "   \n\t\n",
      expected: "   \n\t\n"
    }
  ];

  test.each(STRIP_SENTINEL_CASES)("$name", ({ input, expected }) => {
    expect(stripJsCommentsAndStrings(input)).toBe(expected);
  });

  test("block comments are removed", () => {
    const source = "var a = 1; /* keep --testRunner out */ var b = 2;";
    const stripped = stripJsCommentsAndStrings(source);
    expect(stripped).not.toContain("--testRunner");
    expect(stripped).toContain("var a = 1;");
    expect(stripped).toContain("var b = 2;");
  });

  test("multi-line block comments are removed but preserve newlines", () => {
    const source = [
      "var a = 1;",
      "/*",
      " * --testRunner banner explaining the regression",
      " */",
      "var b = 2;"
    ].join("\n");
    const stripped = stripJsCommentsAndStrings(source);
    expect(stripped).not.toContain("--testRunner");
    // Line count is preserved so error messages keep their line numbers.
    expect(stripped.split("\n").length).toBe(source.split("\n").length);
  });

  test("line comments are removed", () => {
    const source = "var a = 1; // forbid --testRunner here\nvar b = 2;";
    const stripped = stripJsCommentsAndStrings(source);
    expect(stripped).not.toContain("--testRunner");
    expect(stripped).toContain("var a = 1;");
    expect(stripped).toContain("var b = 2;");
  });

  test("single-quoted string literals are reduced to empty quotes", () => {
    const source = "var a = 'hello --testRunner world';";
    const stripped = stripJsCommentsAndStrings(source);
    expect(stripped).not.toContain("--testRunner");
    expect(stripped).toContain("''");
  });

  test("double-quoted string literals are reduced to empty quotes", () => {
    const source = 'var a = "hello --testRunner world";';
    const stripped = stripJsCommentsAndStrings(source);
    expect(stripped).not.toContain("--testRunner");
    expect(stripped).toContain('""');
  });

  test("backtick template strings preserve backticks and erase literal portions", () => {
    const source = "var a = `hello --testRunner world`;";
    const stripped = stripJsCommentsAndStrings(source);
    expect(stripped).not.toContain("--testRunner");
    expect(stripped).toContain("``");
  });

  // Tricky sources whose `--testRunner` token lives in a string/comment construct
  // that the stripper must erase. One row per former per-assertion case (the three
  // escape forms, plus the all-three-constructs-on-one-line case). Every row shares
  // the single assertion `strip(src)` does NOT contain `--testRunner`.
  const STRIP_REMOVES_TESTRUNNER_CASES = [
    { name: "escaped single-quote inside a string", src: "var a = 'it\\'s --testRunner';" },
    { name: "escaped double-quote inside a string", src: 'var a = "he said \\"--testRunner\\"";' },
    { name: "escaped backtick inside a template", src: "var a = `he said \\`--testRunner\\``;" },
    {
      name: "multiple constructs (string + block + line comment) on a single line",
      src: 'var a = "--testRunner"; /* --testRunner */ // --testRunner'
    }
  ];

  test.each(STRIP_REMOVES_TESTRUNNER_CASES)("strips `--testRunner` from: $name", ({ src }) => {
    expect(stripJsCommentsAndStrings(src)).not.toContain("--testRunner");
  });

  test("URL-like substrings (http://) inside strings survive stripping pipeline", () => {
    const source = 'var a = "https://example.com/path";\nvar b = 2;';
    const stripped = stripJsCommentsAndStrings(source);
    expect(stripped).toContain("var a =");
    expect(stripped).toContain("var b = 2;");
    // String payload is removed; no URL leakage either way.
    expect(stripped).not.toContain("example.com");
  });

  test('identifier ending in `https` followed by `+ "://..."` is not truncated', () => {
    // The regex pipeline used a `[^:\\]` lookbehind hack to avoid eating
    // `://`. The tokenizer does not need that hack because it tokenizes
    // each construct in order; `//` only triggers `lineComment` when the
    // characters appear in `code` state.
    const source = 'var a = https + "://example.com";';
    const stripped = stripJsCommentsAndStrings(source);
    expect(stripped).toContain("https +");
  });

  test("mixed real-world source from run-managed-jest.js is processed without throwing", () => {
    const source = fs.readFileSync(RUN_MANAGED_JEST_PATH, "utf8");
    const stripped = stripJsCommentsAndStrings(source);

    expect(typeof stripped).toBe("string");
    expect(stripped.length).toBeGreaterThan(0);

    // The wrapper documents `--testRunner` in comments and forwards it
    // via caller args — both are in comments or string literals. After
    // stripping, the literal must NOT appear in remaining code.
    expect(stripped).not.toContain("--testRunner");
  });

  // ---- Bypass-class regression tests ----------------------------------

  test("PoC: strings containing /* and */ do not cause code between them to be erased", () => {
    // This is the C1 bypass that the regex-pipeline form was vulnerable to.
    // The old `/\/\*[\s\S]*?\*\//g` regex would match from the `/*` inside
    // the first string to the `*/` inside the last string, eating the
    // entire function body.
    const source = [
      'const _opener = "/*";',
      "function inject(args) {",
      '    args.push("--testRunner", "/tmp/x.js");',
      "}",
      'const _closer = "*/";'
    ].join("\n");
    const stripped = stripJsCommentsAndStrings(source);

    // Real code between the strings MUST be preserved.
    expect(stripped).toContain("function inject(args)");
    expect(stripped).toContain("args.push(");

    // String contents are erased; the literal `--testRunner` (which was
    // inside a string) does not survive.
    expect(stripped).not.toContain("--testRunner");
    expect(stripped).not.toContain("/tmp/x.js");
  });

  test("strings containing // do not cause subsequent code to be treated as line comment", () => {
    const source = ['const url = "http://example.com";', "var a = 1;"].join("\n");
    const stripped = stripJsCommentsAndStrings(source);
    expect(stripped).toContain("var a = 1;");
  });

  test('block comments containing "foo" do not cause subsequent strings to be mis-parsed', () => {
    const source = [
      '/* a block comment with "foo" inside */',
      'var a = "--testRunner";',
      "var b = 2;"
    ].join("\n");
    const stripped = stripJsCommentsAndStrings(source);
    expect(stripped).not.toContain("--testRunner");
    expect(stripped).not.toContain("foo");
    expect(stripped).toContain("var a =");
    expect(stripped).toContain("var b = 2;");
  });

  test("template literal ${} expressions are processed as code; inner strings are blanked", () => {
    // `foo ${"bar"} baz` — the literal portions and `"bar"` payload are
    // erased, but the `${` and `}` delimiters survive so static analysis
    // can recognize the expression boundary.
    const source = 'var a = `foo ${"bar"} baz ${"--testRunner"}`;';
    const stripped = stripJsCommentsAndStrings(source);
    expect(stripped).not.toContain("--testRunner");
    expect(stripped).not.toContain("bar");
    expect(stripped).not.toContain("foo");
    // Expression delimiters preserved.
    expect(stripped).toContain("${");
    expect(stripped).toContain("}");
  });

  test("template literal with nested braces in ${} expression tracks brace depth", () => {
    // `${ { a: "bar" } }` — the inner object braces should NOT prematurely
    // pop the templateExpr state.
    const source = 'var a = `pre ${ { a: "--testRunner" } } post`;';
    const stripped = stripJsCommentsAndStrings(source);
    expect(stripped).not.toContain("--testRunner");
    expect(stripped).toContain("${");
    // The object braces survive (they are code, not string).
    expect(stripped).toMatch(/\{\s*a\s*:\s*""\s*\}/);
  });

  test("nested template literal inside ${} expression is handled", () => {
    // `outer ${ `inner ${"x"}` } end` — every layer must be tokenized.
    const source = 'var a = `outer ${ `inner ${"--testRunner"}` } end`;';
    const stripped = stripJsCommentsAndStrings(source);
    expect(stripped).not.toContain("--testRunner");
    expect(stripped).not.toContain("outer");
    expect(stripped).not.toContain("inner");
    expect(stripped).not.toContain("end");
  });

  test("comment inside ${} expression is stripped", () => {
    const source = "var a = `pre ${ /* --testRunner */ x } post`;";
    const stripped = stripJsCommentsAndStrings(source);
    expect(stripped).not.toContain("--testRunner");
    expect(stripped).toContain("x");
  });

  test("unterminated block comment does not throw and consumes rest of input", () => {
    const source = "var a = 1; /* unterminated\nvar b = 2;";
    // Behavior: enter blockComment at `/*` and never exit. All subsequent
    // characters are erased (line breaks are preserved). The function
    // must return cleanly.
    const stripped = stripJsCommentsAndStrings(source);
    expect(typeof stripped).toBe("string");
    expect(stripped).toContain("var a = 1;");
    expect(stripped).not.toContain("--testRunner");
  });

  test("unterminated string does not throw and consumes rest of input", () => {
    const source = 'var a = "unterminated\nvar b = 2;';
    const stripped = stripJsCommentsAndStrings(source);
    expect(typeof stripped).toBe("string");
    expect(stripped).toContain("var a =");
  });

  test("line count is preserved across stripping", () => {
    const source = [
      "var a = 1;",
      "/* multi",
      "   line",
      "   block */",
      'var b = "multi\\n',
      '   line";',
      "// trailing",
      "var c = 3;"
    ].join("\n");
    const stripped = stripJsCommentsAndStrings(source);
    expect(stripped.split("\n").length).toBe(source.split("\n").length);
  });
});

describe("extractCommentsOnly (inverse projection)", () => {
  const TOKEN = "@needle";
  const inComment = (src) => extractCommentsOnly(src).includes(TOKEN);

  // Sentinel inputs (non-string / empty) all project to "". One row per former
  // per-assertion case.
  const EXTRACT_SENTINEL_CASES = [
    { name: "undefined input returns empty string", input: undefined },
    { name: "null input returns empty string", input: null },
    { name: "numeric input returns empty string", input: 42 },
    { name: "Buffer input returns empty string", input: Buffer.from("x") },
    { name: "empty input returns empty string", input: "" }
  ];

  test.each(EXTRACT_SENTINEL_CASES)("$name", ({ input }) => {
    expect(extractCommentsOnly(input)).toBe("");
  });

  test("keeps line-comment payloads, blanks code", () => {
    const out = extractCommentsOnly("const x = 1; // " + TOKEN + "\n");
    expect(out).toContain(TOKEN);
    expect(out).not.toContain("const");
  });

  // Does the TOKEN survive in the comments-only projection? `true` == the token is
  // in a comment region (kept); `false` == it is in code or a string (blanked). One
  // row per former per-assertion case; comments carry the original test rationale.
  const INCOMMENT_CASES = [
    {
      name: "keeps block-comment payload on a plain continuation line (no leading star)",
      src: "/*\n   " + TOKEN + "\n*/\n",
      expected: true
    },
    {
      name: "keeps indented block-comment text",
      src: "/*\n      text " + TOKEN + " here\n*/\n",
      expected: true
    },
    {
      name: "keeps JSDoc star-prefixed body payload",
      src: "/**\n * " + TOKEN + "\n */\n",
      expected: true
    },
    // blanks single/double/template string payloads (token does NOT survive)
    {
      name: "blanks a double-quoted string payload",
      src: 'const a = "' + TOKEN + '";\n',
      expected: false
    },
    {
      name: "blanks a single-quoted string payload",
      src: "const a = '" + TOKEN + "';\n",
      expected: false
    },
    {
      name: "blanks a template string payload",
      src: "const a = `" + TOKEN + "`;\n",
      expected: false
    },
    // a `//` or `/*` INSIDE a string does not open a comment
    {
      name: "a `//` inside a string does not open a comment",
      src: 'const a = "x // y ' + TOKEN + '";\n',
      expected: false
    },
    {
      name: "a `/*` inside a string does not open a comment",
      src: 'const a = "x /* y ' + TOKEN + '";\n',
      expected: false
    },
    {
      name: "bare code identifier does not survive",
      src: "const " + TOKEN.slice(1) + " = 1;\n",
      expected: false
    }
  ];

  test.each(INCOMMENT_CASES)("inComment: $name -> $expected", ({ src, expected }) => {
    expect(inComment(src)).toBe(expected);
  });

  test("line breaks are preserved so line numbers stay aligned", () => {
    const source = ["var a = 1;", "/* block", "   " + TOKEN, "   */", "var b = 2;"].join("\n");
    const out = extractCommentsOnly(source);
    expect(out.split("\n").length).toBe(source.split("\n").length);
    expect(out).toContain(TOKEN);
  });

  test("comment inside a template ${} expression is preserved; surrounding template is blanked", () => {
    const out = extractCommentsOnly("var a = `pre ${ /* " + TOKEN + " */ x } post`;\n");
    expect(out).toContain(TOKEN);
    expect(out).not.toContain("pre");
    expect(out).not.toContain("post");
  });

  test("is the exact complement of strip mode for which-region-won a token", () => {
    // A token in a comment survives extractCommentsOnly and is gone from strip.
    const commented = "// " + TOKEN + "\n";
    expect(extractCommentsOnly(commented)).toContain(TOKEN);
    expect(stripJsCommentsAndStrings(commented)).not.toContain(TOKEN);
    // A token in code survives strip and is gone from extractCommentsOnly.
    const coded = "const a = " + TOKEN.slice(1) + ";\n";
    expect(stripJsCommentsAndStrings(coded)).toContain(TOKEN.slice(1));
    expect(extractCommentsOnly(coded)).not.toContain(TOKEN.slice(1));
  });
});

describe("maskCommentsAndStrings (offset-preserving code projection)", () => {
  // Sentinel inputs (non-string / empty) all project to "". One row per former
  // per-assertion case.
  const MASK_SENTINEL_CASES = [
    { name: "undefined input returns empty string", input: undefined },
    { name: "null input returns empty string", input: null },
    { name: "numeric input returns empty string", input: 42 },
    { name: "Buffer input returns empty string", input: Buffer.from("x") },
    { name: "empty input returns empty string", input: "" }
  ];

  test.each(MASK_SENTINEL_CASES)("$name", ({ input }) => {
    expect(maskCommentsAndStrings(input)).toBe("");
  });

  test("output is the same total length as the input", () => {
    const source = [
      'const _opener = "/*";',
      "function inject(args) {",
      '    args.push("--testRunner", "/tmp/x.js"); // forbid --testRunner',
      "}",
      "/* multi",
      " * line block --testRunner",
      " */",
      'const _closer = "*/";'
    ].join("\n");
    const masked = maskCommentsAndStrings(source);
    expect(masked.length).toBe(source.length);
  });

  test("line breaks are preserved (line count unchanged)", () => {
    const source = [
      "var a = 1;",
      "/* block",
      "   spanning",
      "   lines */",
      'var b = "two\\nlines";',
      "// trailing comment",
      "var c = 3;"
    ].join("\n");
    const masked = maskCommentsAndStrings(source);
    expect(masked.split("\n").length).toBe(source.split("\n").length);
  });

  test("a `//`-comment expect( is blanked", () => {
    const source = '// expect(out.stdout).toContain("multi word")\nvar a = 1;';
    const masked = maskCommentsAndStrings(source);
    expect(masked).not.toContain("expect(");
    // The blanked comment region is all spaces (plus the preserved newline).
    expect(masked.split("\n")[0].trim()).toBe("");
  });

  test("a string 'expect(...)' is blanked", () => {
    const source = "const s = 'expect(result.stdout).toContain(\\'multi word\\')';\nvar a = 1;";
    const masked = maskCommentsAndStrings(source);
    expect(masked).not.toContain("expect(");
    // Surrounding code structure (the binding) survives.
    expect(masked).toContain("const s =");
  });

  test("a real-code expect( survives at the SAME offset as the raw source", () => {
    const source = [
      '// expect(out.stdout).toContain("decoy in comment")',
      'expect(result.stdout).toContain("real code phrase");',
      "const s = 'expect(decoy.stdout)';"
    ].join("\n");
    const masked = maskCommentsAndStrings(source);
    // Exactly ONE real-code expect( survives (the comment + string ones blanked).
    expect((masked.match(/expect\(/g) || []).length).toBe(1);
    const rawOffset = source.indexOf("expect(result.stdout)");
    expect(masked.indexOf("expect(")).toBe(rawOffset);
    // The receiver text at that offset reads identically in mask and raw.
    expect(masked.slice(rawOffset, rawOffset + "expect(result.stdout)".length)).toBe(
      "expect(result.stdout)"
    );
  });

  test('the "/*"-in-string edge from strip tests is safe (code between strings survives)', () => {
    // The C1 bypass shape: a `/*` and `*/` spelled inside string literals must
    // NOT eat the code between them in mask mode either.
    const source = [
      'const _opener = "/*";',
      "function inject(args) {",
      '    args.push("--testRunner", "/tmp/x.js");',
      "}",
      'const _closer = "*/";'
    ].join("\n");
    const masked = maskCommentsAndStrings(source);
    // Real code between the strings is preserved verbatim.
    expect(masked).toContain("function inject(args)");
    expect(masked).toContain("args.push(");
    // String payloads are blanked (the forbidden literal does not survive).
    expect(masked).not.toContain("--testRunner");
    expect(masked).not.toContain("/tmp/x.js");
    // Length still preserved through the edge.
    expect(masked.length).toBe(source.length);
  });

  test("code inside a template ${} expression survives (it is real code)", () => {
    const source = "const a = `pre ${ expect(x) } post`;";
    const masked = maskCommentsAndStrings(source);
    // The interpolation expression is code -> kept verbatim; the literal
    // portions (`pre `, ` post`) are blanked.
    expect(masked).toContain("${ expect(x) }");
    expect(masked).not.toContain("pre");
    expect(masked).not.toContain("post");
    expect(masked.length).toBe(source.length);
  });

  test("shares region classification with strip/comments for a token", () => {
    const TOKEN = "@needle";
    // In code: survives mask, gone from comments.
    const coded = "const a = " + TOKEN.slice(1) + ";\n";
    expect(maskCommentsAndStrings(coded)).toContain(TOKEN.slice(1));
    expect(extractCommentsOnly(coded)).not.toContain(TOKEN.slice(1));
    // In a comment: gone from mask (blanked), survives in comments.
    const commented = "// " + TOKEN + "\n";
    expect(maskCommentsAndStrings(commented)).not.toContain(TOKEN);
    expect(extractCommentsOnly(commented)).toContain(TOKEN);
    // In a string: gone from BOTH mask and comments.
    const stringed = 'const a = "' + TOKEN + '";\n';
    expect(maskCommentsAndStrings(stringed)).not.toContain(TOKEN);
    expect(extractCommentsOnly(stringed)).not.toContain(TOKEN);
  });
});

describe("regex literal tokenization (regex state)", () => {
  // M-1: the tokenizer now has a `regex` state so a quote INSIDE a regex literal
  // cannot toggle string state and bleed through a following `expect(...)`. Each
  // row is fed through ALL THREE projections; the assertions below pin (1) the
  // regex body never leaks string/code state into what follows, (2) genuine
  // DIVISION is left as code (not eaten as a phantom regex), and (3) every
  // projection preserves total length (mask/comments) and newlines.

  // Does a `/` open a regex here? Each row crafts a `<prefix><slash...>; const after = "KEEP";`
  // shape: when the slash is a REGEX, its body (incl. any quote) is blanked and the
  // trailing `"KEEP"` string is parsed cleanly (mask keeps `"    "`); when the slash
  // is DIVISION, the slash survives verbatim in code. `tail` is a fragment that MUST
  // still be present (verbatim) in the mask after the construct, proving no leak.
  const REGEX_VS_DIVISION_CASES = [
    {
      name: "regex containing a double-quote does not leak string state past it",
      src: 'const r = /a"b/;\nconst after = "KEEP";',
      // The regex (incl. its `"`) is blanked; the real string after is intact.
      maskContains: ["const r =", 'const after = "    ";'],
      maskExcludes: ['/a"b/']
    },
    {
      name: "regex containing a single-quote does not leak string state past it",
      src: 'const r = /a\'b/;\nconst after = "KEEP";',
      maskContains: ["const r =", 'const after = "    ";'],
      maskExcludes: ["/a'b/"]
    },
    {
      name: "division is NOT treated as a regex (a / b / c stays code)",
      src: "const x = a / b / c;",
      // Pure division: every char is code and survives verbatim in the mask.
      maskContains: ["const x = a / b / c;"],
      maskExcludes: []
    },
    {
      name: "division after a closing paren stays code",
      src: "const x = (a + b) / c;",
      maskContains: ["const x = (a + b) / c;"],
      maskExcludes: []
    },
    {
      name: "a char class containing `/` terminates correctly (the inner / is literal)",
      src: 'const r = /[a/b]/;\nconst after = "KEEP";',
      // The `/` inside `[...]` does NOT close the regex; the WHOLE literal is blanked
      // and the following string is parsed cleanly.
      maskContains: ["const r =", 'const after = "    ";'],
      maskExcludes: ["[a/b]"]
    },
    {
      name: "an escaped `/` does not terminate the regex early",
      src: 'const r = /a\\/b/;\nconst after = "KEEP";',
      maskContains: ["const r =", 'const after = "    ";'],
      maskExcludes: ["a\\/b"]
    },
    {
      name: "regex flags are consumed as part of the literal",
      src: 'const r = /foo/gimsuy;\nconst after = "KEEP";',
      maskContains: ["const r =", 'const after = "    ";'],
      maskExcludes: ["foo", "gimsuy"]
    },
    {
      name: "a regex after `return` is recognized (return is an expression context)",
      src: 'function f() { return /x"y/.test(s); }\nconst after = "KEEP";',
      maskContains: ["function f() {", "return", ".test(s);", 'const after = "    ";'],
      maskExcludes: ['x"y']
    },
    {
      name: 'the M-1 witness: split(/["\\n]/) above a real expect( keeps the expect',
      src: 'out.stdout.split(/["\\n]/);\nexpect(out.stdout).toContain("phrase here");',
      // The regex (with its quote) is neutralized; the REAL-code `expect(` survives.
      maskContains: ["out.stdout.split(", "expect(out.stdout).toContain("],
      maskExcludes: ['/["\\n]/']
    },
    {
      // A raw newline aborts regex state defensively: an UNTERMINATED regex (no
      // closing `/`) recovers at the newline, so the real `expect(` two lines down
      // is still seen on the mask (not swallowed by a runaway regex).
      name: "a raw newline aborts an unterminated regex and recovers to code",
      src: 'const r = /abc;\nconst keep = 1;\nexpect(out).toContain("a b");',
      maskContains: ["const keep = 1;", "expect(out).toContain("],
      maskExcludes: []
    }
  ];

  test.each(REGEX_VS_DIVISION_CASES)("$name", ({ src, maskContains, maskExcludes }) => {
    const masked = maskCommentsAndStrings(src);
    // Offset-preserving invariants (length + newline count) in EVERY row.
    expect(masked.length).toBe(src.length);
    expect(masked.split("\n").length).toBe(src.split("\n").length);
    for (const frag of maskContains) {
      expect(masked).toContain(frag);
    }
    for (const frag of maskExcludes) {
      expect(masked).not.toContain(frag);
    }
  });

  // The same neutralization must hold in the two LEGACY projections. A regex body
  // is erased in strip mode and blanked in comments mode; the surrounding
  // code/string state must be correct in every mode. `lengthPreserving` rows
  // additionally pin total length + newline count (comments is offset-preserving;
  // strip is not). One row per projection/shape.
  const REGEX_ALL_MODES_CASES = [
    {
      name: "strip mode: regex with a quote does not corrupt the following string",
      fn: stripJsCommentsAndStrings,
      lengthPreserving: false,
      src: 'const r = /a"b/; const keep = "SECRET"; var z = 1;',
      // Strip removes the regex body and the string payload; the binding code and
      // the trailing statement survive, and the leaked-quote content does not.
      contains: ["const r =", "const keep =", "var z = 1;"],
      excludes: ['a"b', "SECRET"]
    },
    {
      name: "comments mode: a regex body is blanked, code stays blanked, length preserved",
      fn: extractCommentsOnly,
      lengthPreserving: true,
      src: 'const r = /a"b/; // tail @needle\nvar z = 1;',
      // Only the real trailing line-comment payload survives; the regex (and the
      // code) are blanked. Length-preservation is asserted by the runner.
      contains: ["@needle"],
      excludes: ['a"b', "const r", "var z"]
    },
    {
      // A complex regex (quote + escaped `/` + char class with `/` + flags) is
      // fully neutralized while the real-code expect( on the next line survives,
      // and the offset-preserving comments projection keeps length + newlines.
      name: "comments mode: a complex multi-feature regex is blanked, length + newlines preserved",
      fn: extractCommentsOnly,
      lengthPreserving: true,
      src: 'const r = /a"b\\/c[x/y]/gi;\nfoo(out);\n// c @k\n',
      contains: ["@k"],
      excludes: ['a"b', "[x/y]", "foo", "const r"]
    },
    {
      name: "mask mode: a complex multi-feature regex is blanked but the real expect( survives",
      fn: maskCommentsAndStrings,
      lengthPreserving: true,
      src: 'const r = /a"b\\/c[x/y]/gi;\nexpect(out).toContain("p q");\n',
      // The regex (incl. quote / escaped slash / char-class slash / flags) is gone;
      // the real-code expect( on the next line is preserved verbatim.
      contains: ['expect(out).toContain("   ");'],
      excludes: ['a"b', "[x/y]", "gi;"]
    }
  ];

  test.each(REGEX_ALL_MODES_CASES)("$name", ({ fn, src, contains, excludes, lengthPreserving }) => {
    const out = fn(src);
    if (lengthPreserving) {
      expect(out.length).toBe(src.length);
      expect(out.split("\n").length).toBe(src.split("\n").length);
    }
    for (const frag of contains) {
      expect(out).toContain(frag);
    }
    for (const frag of excludes) {
      expect(out).not.toContain(frag);
    }
  });
});
