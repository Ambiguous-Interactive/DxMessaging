/**
 * @fileoverview Categorical guard against fragile, formatting-SENSITIVE
 * assertions on the CONTENT of MSBuild / structured XML config
 * (.props / .csproj / .targets).
 *
 * BACKGROUND: the repo's prettier does NOT format XML (there is no
 * @prettier/plugin-xml and no .props/.csproj override), so any editor,
 * hand-edit, or future formatter can legitimately re-wrap an element so its
 * closing `>` sits on its own line, or split a multi-attribute open tag one
 * attribute per line. A regression here once shipped because two suites
 * hand-rolled `new RegExp("<NAME ...>([^<]*)</NAME>")` extractors and
 * `content.toContain('<Target Name="..." AfterTargets="..." Condition="...">')`
 * substrings that assumed single-line tags. The files were semantically correct;
 * the assertions were fragile.
 *
 * THE RULE: tests that assert on the CONTENT of an XML config file must parse it
 * structurally via scripts/lib/msbuild-xml.js (getPropertyValue / resolveProperty
 * / hasElement), never via local regex/substring patterns that assume single-line
 * tags or a fixed attribute order.
 *
 * This guard scans scripts/**\/__tests__/*.test.js and fails when a test
 * reintroduces either fragile shape:
 *   (a) a LOCAL hand-rolled XML extractor -- a `function getPropertyValue` /
 *       `function resolveProperty` declared in the test, a regex of the shape
 *       `</NAME>` requiring a contiguous closing tag (whether NAME is an
 *       interpolated `${name}` / concatenated `" + name` OR a FIXED literal
 *       MSBuild element name like `</Target>`), an XML element-open regex
 *       LITERAL of the fragile shape `/<Tag\b ... >/` (e.g.
 *       `/<PropertyGroup\b([^>]*)>/g`), OR the same open-tag pattern hand-BUILT
 *       from a string passed to `new RegExp(...)` (e.g.
 *       `new RegExp("<Target\\s+([^>]*)>")`, including a `const pat = "<Tag...>";
 *       new RegExp(pat)` indirection) -- instead of importing the shared helper; OR
 *   (b) a single-line MSBuild element-open substring carrying at least one
 *       `attr="..."` pair, OR a bare contiguous `attr="value"` pair, in a test
 *       that actually READS a .props/.csproj/.targets/.sln file (or routes through
 *       the shared helper / contract module). The element-open form is flagged
 *       wherever it appears as a string literal -- a matcher argument
 *       (toContain / toMatch / expect.stringContaining / String.prototype
 *       includes / indexOf), a hoisted `const`, or one arm of a string
 *       concatenation -- since such a literal has no benign purpose in an
 *       XML-config-reading test. The bare-attribute and regex-literal forms are
 *       flagged when passed to a content check. The only residual (accepted floor)
 *       is a value assembled purely at runtime from non-literal pieces, which is
 *       inherently un-scannable statically.
 *
 * The heuristics are intentionally narrow. Shape (a) fires only inside a local
 * `function` body (so prose / fixtures elsewhere do not trip it) and only when
 * the shared helper is NOT imported. Shape (b) fires only in a suite that
 * demonstrably reads such a file. The shared helper's own fixture set
 * legitimately contains XML literals and local naming, so it (and this policy
 * file, whose docstring quotes the forbidden shapes) are the allowlisted
 * exceptions.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SCRIPTS_DIR = path.join(REPO_ROOT, "scripts");

const HELPER_REQUIRE = /require\(["'][^"']*\/lib\/msbuild-xml["']\)/;

/**
 * The single allowlisted file: the shared helper's own test, which by design
 * contains XML literals and exercises the parser directly.
 */
const ALLOWLIST = new Set([
  // The shared helper's own fixture set: it legitimately contains XML literals
  // and exercises the parser directly.
  "scripts/lib/__tests__/msbuild-xml.test.js",
  // This policy file itself: its docstring quotes the forbidden shapes as
  // examples (e.g. `<Target Name="..." ...>`), which would otherwise self-trip.
  "scripts/__tests__/xml-contract-assertion-policy.test.js"
]);

/**
 * Recursively collect every *.test.js under a scripts/**\/__tests__ directory.
 *
 * @param {string} dir Absolute directory to walk.
 * @param {string[]} out Accumulator of absolute paths.
 * @returns {string[]} `out`.
 */
function collectTestFiles(dir, out) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch (error) {
    if (error.code === "ENOENT") {
      return out;
    }
    throw error;
  }
  for (const entry of entries) {
    const abs = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      collectTestFiles(abs, out);
    } else if (entry.isFile() && entry.name.endsWith(".test.js")) {
      out.push(abs);
    }
  }
  return out;
}

/**
 * True when the test demonstrably READS an MSBuild/structured-XML config file --
 * not merely mentions one. We require real evidence of reading file CONTENT:
 *   - a `require(...)` of the shared helper (msbuild-xml) or the analyzer build
 *     contract module (which reads those files); OR
 *   - a path literal ending in `.props`/`.csproj`/`.targets`/`.sln` (typically a
 *     `path.join(..., "Directory.Build.props")`) AND a file-content read call
 *     (`readFileSync` / `readFile` / `existsSync`) somewhere in the suite.
 *
 * This is deliberately stricter than "the string `.csproj` appears anywhere":
 * a suite that only mentions `.props` in a comment, or asserts an unrelated 2+
 * attribute open tag substring on HTML/SVG/AndroidManifest markup, is NOT
 * flagged. Keeps the shape-(b) false-positive surface near zero.
 *
 * @param {string} source Test file source.
 * @returns {boolean}
 */
function readsXmlConfig(source) {
  if (/require\(["'][^"']*\/(?:msbuild-xml|analyzer-build-contract)["']\)/.test(source)) {
    return true;
  }
  const hasConfigPathLiteral = /["'`][^"'`]*\.(?:props|csproj|targets|sln)["'`]/.test(source);
  const readsAFile = /\b(?:readFileSync|readFile|existsSync)\s*\(/.test(source);
  return hasConfigPathLiteral && readsAFile;
}

/**
 * Element-open regex literal of the fragile single-line-assuming shape, e.g.
 * `/<PropertyGroup\b([^>]*)>/g`, `/<Target\s+([^>]*)>/`, `/<Target /`,
 * `/<Target[ \t]/`, or `/<Target(\s...)>/`. Matches a JS regex literal that opens
 * with `<Tag` followed by ANY boundary that proves the name has ended -- an
 * escape (`\b`/`\s`/`\t`...), a character class (`[`), a literal space, or a
 * grouping/alternation paren -- and later a `>`. This is the open-tag analogue of
 * the `</NAME>` close-tag shape, deliberately broad so a re-spelling of the same
 * fragile pattern cannot slip past.
 *
 * The boundary alternatives, after the `<Tag` name:
 *   \\\\ ... a backslash-escape   (`\b`, `\s`, `\t`, `\n`, `\W`, ...)
 *   \[   ... a character class    (`[ \t]`, `[^>]`)
 *   \(   ... a group/alternation  (`(\s|>)`)
 *   ' '  ... a literal space      (`/<Target /`)
 * and the literal must still contain a later `>` (the close of the open tag).
 */
const OPEN_TAG_REGEX_LITERAL = /\/<[A-Za-z_][\w:.-]*(?:\\.|\[|\(| )[^/\n]*>/;

/**
 * The slash-less body form of OPEN_TAG_REGEX_LITERAL, applied to the reconstructed
 * STRING argument of a `new RegExp("<Tag\\s+...>")` call. Hand-building an open-tag
 * pattern from a string is a plausible re-spelling of the fragile element-open
 * regex: `new RegExp("<Target\\s+([^>]*)>")` breaks on a wrapped `<Target\n  ...>`
 * exactly like the regex-literal form, yet a literal-only scan never sees it. The
 * decoded string drops one backslash level (`"\\s"` -> `\s`), so the same `<Tag`
 * + boundary (`\`-escape / `[` class / `(` group / literal space) + later `>`
 * shape matches the decoded argument.
 */
const OPEN_TAG_REGEX_BODY = /<[A-Za-z_][\w:.-]*(?:\\.|\[|\(| )[^\n]*>/;

/**
 * True when `name` looks like an MSBuild / structured-XML element name rather
 * than an HTML / SVG / shell tag. MSBuild element names are PascalCase XML
 * identifiers (`PropertyGroup`, `Target`, `PackageReference`, `NoWarn`, ...),
 * always starting with an uppercase ASCII letter; HTML and SVG tags
 * (`a`, `div`, `span`, `body`, `script`, `svg`, `rect`, `path`, `g`, ...) and
 * shell here-doc / redirection markers are conventionally lowercase, so the
 * uppercase-first requirement alone separates the two vocabularies and keeps the
 * fixed-name close-tag scan from firing on an HTML/SVG/shell `</tag>`. The body
 * is restricted to the XML name character set so an arbitrary captured fragment
 * (e.g. `</${x}` decoded to junk) is not mistaken for a tag.
 *
 * @param {string} name Candidate element name (no angle brackets / slash).
 * @returns {boolean}
 */
function isMsbuildElementName(name) {
  return /^[A-Z][\w.:-]*$/.test(name);
}

/**
 * The contiguous close-tag fragility expressed with a FIXED, literal element
 * name -- `</Target>`, `</PropertyGroup>`, `</MicrosoftCodeAnalysisVersion>` --
 * captured from a regex literal body or a `new RegExp("...")` string argument.
 * This is the original failure class re-spelled with a hard-coded name instead
 * of an interpolated `${name}` / concatenated `" + name`, so the
 * `</${name}`-only branch in detectLocalExtractor never sees it. The match
 * requires the `>` to sit IMMEDIATELY after the name (no `\s*` / `\n` between),
 * which is exactly the assumption that breaks on a wrapped `</Name\n>`; a
 * helper-style `</${escapeRegExp(name)}\s*>` is therefore not this shape. The
 * captured name is filtered through isMsbuildElementName so an HTML/SVG/shell
 * `</a>` / `</div>` / `</svg>` is not flagged (finding: limit to MSBuild
 * vocabulary). Backslash before the `/` (`<\/Target>` in a regex literal) is
 * tolerated.
 */
const FIXED_NAME_CLOSE_TAG = /<\\?\/([A-Za-z_][\w.:-]*)>/g;

/**
 * Extract the source text of every JS regex LITERAL in `source` (the body between
 * the delimiting slashes, with the leading `/`), skipping `/` that appears inside
 * strings, template literals, comments, or a division context. Heuristic but
 * conservative: a regex literal is recognized only where a `/` legitimately begins
 * one (start of expression / after an operator, `(`, `,`, `=`, `:`, `!`, `&`,
 * `|`, `?`, `{`, `[`, `;`, `return`, or `.test(` / `.toMatch(`). Used so the
 * open-tag-regex check fires anywhere a fragile pattern lives -- arrow test
 * callbacks included -- not only inside named function bodies.
 *
 * @param {string} source Source text.
 * @returns {string[]} Each regex literal as `/.../` (flags omitted).
 */
function regexLiterals(source) {
  const out = [];
  let i = 0;
  let prevSignificant = "";
  let mode = null; // null | '"' | "'" | '`' | 'line' | 'block'
  while (i < source.length) {
    const ch = source[i];
    const next = source[i + 1];
    if (mode === "line") {
      if (ch === "\n") {
        mode = null;
      }
      i += 1;
      continue;
    }
    if (mode === "block") {
      if (ch === "*" && next === "/") {
        mode = null;
        i += 2;
        continue;
      }
      i += 1;
      continue;
    }
    if (mode === '"' || mode === "'" || mode === "`") {
      if (ch === "\\") {
        i += 2;
        continue;
      }
      if (ch === mode) {
        mode = null;
      }
      i += 1;
      continue;
    }
    if (ch === "/" && next === "/") {
      mode = "line";
      i += 2;
      continue;
    }
    if (ch === "/" && next === "*") {
      mode = "block";
      i += 2;
      continue;
    }
    if (ch === '"' || ch === "'" || ch === "`") {
      mode = ch;
      prevSignificant = ch;
      i += 1;
      continue;
    }
    if (ch === "/" && isRegexContext(prevSignificant)) {
      // Scan to the closing unescaped `/`, honoring character classes (which may
      // contain an unescaped `/`).
      let j = i + 1;
      let inClass = false;
      for (; j < source.length; j += 1) {
        const c = source[j];
        if (c === "\\") {
          j += 1;
          continue;
        }
        if (c === "[") {
          inClass = true;
        } else if (c === "]") {
          inClass = false;
        } else if (c === "/" && !inClass) {
          break;
        } else if (c === "\n") {
          break; // unterminated; not a regex literal
        }
      }
      if (j < source.length && source[j] === "/") {
        out.push(source.slice(i, j)); // `/<body` (closing slash omitted, fine for our test)
        i = j + 1;
        prevSignificant = "/";
        continue;
      }
    }
    if (!/\s/.test(ch)) {
      prevSignificant = ch;
    }
    i += 1;
  }
  return out;
}

/**
 * True when the previous significant character means a `/` begins a regex literal
 * rather than a division operator.
 *
 * @param {string} prev Previous non-whitespace character (or "").
 * @returns {boolean}
 */
function isRegexContext(prev) {
  return prev === "" || "([{,;=:!&|?+-*%~^<>".includes(prev);
}

/**
 * Extract the decoded body of every JS STRING / template LITERAL in `source`,
 * skipping `/`-delimited regex literals and `//` / `/* *​/` comments. Backslash
 * escapes inside the literal are resolved (`\"` -> `"`, `\\` -> `\`, `\n` -> a
 * newline) so the returned text is what the assertion actually compares against.
 * Template-literal `${...}` interpolations are blanked to a single space (their
 * runtime value is unknowable statically). Used so a fragile XML open-tag literal
 * is detected wherever it lives -- a matcher argument, a hoisted `const`, or one
 * arm of a `'<Tag a="x"' + ' b="y">'` concatenation (the parts are reassembled by
 * the caller) -- not only when it is the direct argument of a matcher call.
 *
 * @param {string} source Source text.
 * @returns {string[]} Each string/template literal's decoded body.
 */
function stringLiterals(source) {
  const out = [];
  let i = 0;
  let prevSignificant = "";
  while (i < source.length) {
    const ch = source[i];
    const next = source[i + 1];
    if (ch === "/" && next === "/") {
      i += 2;
      while (i < source.length && source[i] !== "\n") {
        i += 1;
      }
      continue;
    }
    if (ch === "/" && next === "*") {
      i += 2;
      while (i < source.length && !(source[i] === "*" && source[i + 1] === "/")) {
        i += 1;
      }
      i += 2;
      continue;
    }
    if (ch === "/" && isRegexContext(prevSignificant)) {
      // Skip a regex literal body (string-literal walker does not parse regexes).
      let j = i + 1;
      let inClass = false;
      for (; j < source.length; j += 1) {
        const c = source[j];
        if (c === "\\") {
          j += 1;
          continue;
        }
        if (c === "[") {
          inClass = true;
        } else if (c === "]") {
          inClass = false;
        } else if (c === "/" && !inClass) {
          break;
        } else if (c === "\n") {
          break;
        }
      }
      if (j < source.length && source[j] === "/") {
        i = j + 1;
        prevSignificant = "/";
        continue;
      }
    }
    if (ch === '"' || ch === "'" || ch === "`") {
      const quote = ch;
      let j = i + 1;
      let body = "";
      for (; j < source.length; j += 1) {
        const c = source[j];
        if (c === "\\") {
          const escaped = source[j + 1];
          if (escaped === "n") {
            body += "\n";
          } else if (escaped === "t") {
            body += "\t";
          } else {
            body += escaped === undefined ? "" : escaped;
          }
          j += 1;
          continue;
        }
        if (c === quote) {
          break;
        }
        if (quote === "`" && c === "$" && source[j + 1] === "{") {
          // Blank the interpolation to a space; skip to the matching `}`.
          body += " ";
          j += 2;
          let depth = 1;
          for (; j < source.length && depth > 0; j += 1) {
            if (source[j] === "{") {
              depth += 1;
            } else if (source[j] === "}") {
              depth -= 1;
            }
          }
          j -= 1;
          continue;
        }
        body += c;
      }
      out.push(body);
      i = j + 1;
      prevSignificant = quote;
      continue;
    }
    if (!/\s/.test(ch)) {
      prevSignificant = ch;
    }
    i += 1;
  }
  return out;
}

/**
 * Detect a LOCAL MSBuild/XML property extractor (shape (a)). Fires on a local
 * `function getPropertyValue` / `function resolveProperty` declaration, or a
 * regex literal of the contiguous-closing-tag shape `</NAME>` (with NAME being a
 * fixed identifier or an interpolated `${name}`), but NOT when the file imports
 * the shared helper (importing it and re-using the names is the correct path,
 * though the helper is a module export so a local `function` of that name would
 * shadow it -- which is exactly what we forbid).
 *
 * @param {string} source Test file source.
 * @returns {string|null} A diagnostic when fragile, else null.
 */
function detectLocalExtractor(source) {
  const localFn = /\bfunction\s+(getPropertyValue|resolveProperty)\s*\(/.exec(source);
  if (localFn !== null) {
    return `declares a local function ${localFn[1]}() instead of importing scripts/lib/msbuild-xml.js`;
  }
  // Contiguous-closing-tag pattern built from a name, e.g.
  //   new RegExp(`<${name} ...>([^<]*)</${name}>`)
  //   new RegExp("<" + name + ">([^<]*)</" + name + ">")
  // This is the precise shape that returns null on a wrapped `</Name\n>` close
  // tag. Gate on a `new RegExp(` being present (so plain prose mentioning `</`
  // never trips), then look for a name-keyed contiguous closing tag: `</`
  // directly followed by a `${...}` interpolation or a `" + ` / `' + `
  // concatenation.
  if (/new RegExp\(/.test(source)) {
    const namedClose =
      /<\\?\/\$\{[^}]+\}/.test(source) || // `</${name}`
      /<\\?\/"\s*\+/.test(source) || // `</" + name`
      /<\\?\/'\s*\+/.test(source); // `</' + name`
    if (namedClose) {
      return "builds a contiguous `</NAME>` closing-tag regex (breaks on a wrapped `</NAME\\n>`) instead of using scripts/lib/msbuild-xml.js getPropertyValue";
    }
  }
  // NOTE: the element-OPEN regex-literal shape (`/<Tag ...>/`) is detected by
  // detectOpenTagRegexLiteral, which the main test gates on readsXmlConfig so an
  // open-tag regex over unrelated SVG/HTML markup is not wrongly flagged. It is
  // intentionally NOT folded in here (this function runs unconditionally).
  return null;
}

/**
 * Detect a single-line-assuming element-OPEN regex literal anywhere in the suite
 * (e.g. `/<PropertyGroup\b([^>]*)>/g`, `/<Target /`), whether inside a named
 * function body OR an arrow test callback. This is the same fragile category as
 * the two named extractors; route it through getElements/hasElement from the
 * shared helper instead. Scanned over EVERY regex literal so a re-spelling or a
 * callback-level pattern cannot slip past.
 *
 * Gated by the caller on readsXmlConfig: an open-tag regex over UNRELATED markup
 * (e.g. an `/<rect[^>]*\/>/` over an SVG banner) is not an MSBuild-config
 * assertion and is correctly not flagged.
 *
 * @param {string} source Test file source.
 * @returns {string|null} A diagnostic when fragile, else null.
 */
function detectOpenTagRegexLiteral(source) {
  for (const literal of regexLiterals(source)) {
    if (OPEN_TAG_REGEX_LITERAL.test(literal)) {
      return `hand-rolls an element-open regex literal (\`/<Tag ...>/\`: ${literal.slice(
        0,
        60
      )}) instead of using scripts/lib/msbuild-xml.js (getElements/hasElement)`;
    }
  }
  return null;
}

/**
 * Reconstruct the decoded STRING argument of each `new RegExp(...)` call -- a
 * single string literal, a `+`-joined concatenation of literals, or a const that
 * was assigned such a literal -- and flag it when it carries an element-OPEN shape
 * (`<Tag\s+...>` / `<Tag />` / `<Tag[ \t]...>`), the slash-less analogue of the
 * regex-literal form detectOpenTagRegexLiteral catches. This closes the
 * string-built re-spelling (`const pat = "<Target\\s+([^>]*)>"; new RegExp(pat)`),
 * which neither detectLocalExtractor's close-tag-only `new RegExp(` branch nor the
 * regex-literal scan sees.
 *
 * We resolve simple `const NAME = "<...>";` bindings so the
 * `new RegExp(pat)` indirection is followed, then reconstruct the literal text
 * inside each `new RegExp(` argument list (concatenated runs included). Gated by
 * the caller on readsXmlConfig, like the regex-literal form.
 *
 * @param {string} source Test file source.
 * @returns {string|null} A diagnostic when fragile, else null.
 */
function detectOpenTagRegexStringBuilt(source) {
  // 1) Inline literal/concatenation argument: `new RegExp("<Target\\s+...>")` or
  //    `new RegExp("<Target" + "\\s+([^>]*)>")`. Capture the argument-list slice up
  //    to the matching close paren, decode its string literals, and join them.
  const callRe = /\bnew\s+RegExp\s*\(/g;
  let m;
  while ((m = callRe.exec(source)) !== null) {
    const argStart = callRe.lastIndex;
    const argSlice = sliceBalancedParens(source, argStart);
    if (argSlice === null) {
      continue;
    }
    const joined = stringLiterals(argSlice).join("");
    if (OPEN_TAG_REGEX_BODY.test(joined)) {
      return `builds an element-open regex from a string passed to new RegExp(...) (\`<Tag ...>\`: ${joined.slice(
        0,
        60
      )}) instead of using scripts/lib/msbuild-xml.js (getElements/hasElement)`;
    }
  }

  // 2) Indirected via a `const NAME = "<...>";` binding then `new RegExp(NAME)`:
  //    only fire when the source actually constructs a RegExp from that name, so a
  //    bare prose constant is not flagged.
  if (callRe.lastIndex !== 0) {
    callRe.lastIndex = 0; // (callRe is /g; reset before reuse not needed below, kept explicit)
  }
  const constRe = /\bconst\s+([A-Za-z_$][\w$]*)\s*=\s*((?:["'`][^"'`]*["'`]\s*\+?\s*)+);/g;
  let c;
  while ((c = constRe.exec(source)) !== null) {
    const name = c[1];
    const joined = stringLiterals(c[2]).join("");
    if (!OPEN_TAG_REGEX_BODY.test(joined)) {
      continue;
    }
    const usedInRegExp = new RegExp(`\\bnew\\s+RegExp\\s*\\(\\s*${name}\\b`).test(source);
    if (usedInRegExp) {
      return `builds an element-open regex from a string passed to new RegExp(...) (\`<Tag ...>\`: ${joined.slice(
        0,
        60
      )}) instead of using scripts/lib/msbuild-xml.js (getElements/hasElement)`;
    }
  }
  return null;
}

/**
 * Detect the contiguous close-tag fragility expressed with a FIXED, literal
 * element name -- `/<\/Target>/`, `new RegExp("<MicrosoftCodeAnalysisVersion>([^<]*)</MicrosoftCodeAnalysisVersion>")`,
 * etc. detectLocalExtractor only catches the interpolated `</${name}` /
 * concatenated `</" + name` form, so a hard-coded name re-spelling of the SAME
 * original failure class (a contiguous `</Name>` that breaks on a wrapped
 * `</Name\n>`) slipped past every detector. We scan both regex LITERAL bodies
 * and `new RegExp("...")` string arguments, requiring the `>` to sit immediately
 * after an MSBuild-vocabulary name (isMsbuildElementName) -- the
 * shared-helper-style `</${escapeRegExp(name)}\s*>` is NOT this shape (it has a
 * `\s*` before `>`), and an HTML/SVG/shell `</a>` / `</div>` is excluded by the
 * MSBuild-name filter.
 *
 * Gated by the caller on readsXmlConfig (like the open-tag forms) so a fixed-name
 * close tag in a suite that does not read an XML config file is not flagged.
 *
 * @param {string} source Test file source.
 * @returns {string|null} A diagnostic when fragile, else null.
 */
function detectFixedNameCloseTagRegex(source) {
  const candidates = [];
  for (const literal of regexLiterals(source)) {
    candidates.push(literal);
  }
  const callRe = /\bnew\s+RegExp\s*\(/g;
  let m;
  while ((m = callRe.exec(source)) !== null) {
    const argSlice = sliceBalancedParens(source, callRe.lastIndex);
    if (argSlice !== null) {
      candidates.push(stringLiterals(argSlice).join(""));
    }
  }
  for (const text of candidates) {
    FIXED_NAME_CLOSE_TAG.lastIndex = 0;
    let match;
    while ((match = FIXED_NAME_CLOSE_TAG.exec(text)) !== null) {
      if (isMsbuildElementName(match[1])) {
        return `hand-rolls a contiguous fixed-name close-tag pattern (\`</${match[1]}>\`, breaks on a wrapped \`</${match[1]}\\n>\`) instead of using scripts/lib/msbuild-xml.js getPropertyValue/getElements`;
      }
    }
  }
  return null;
}

/**
 * Return the source slice from `open` (the index just AFTER an opening `(`) up to,
 * but excluding, its matching `)`, honoring nested parens and skipping `)` that
 * appears inside string/template/regex literals or comments. Returns null if no
 * balanced close is found.
 *
 * @param {string} source Source text.
 * @param {number} open Index just past the opening paren.
 * @returns {string|null} The argument-list text, or null.
 */
function sliceBalancedParens(source, open) {
  let depth = 1;
  let i = open;
  let mode = null; // null | '"' | "'" | '`'
  while (i < source.length) {
    const ch = source[i];
    const next = source[i + 1];
    if (mode !== null) {
      if (ch === "\\") {
        i += 2;
        continue;
      }
      if (ch === mode) {
        mode = null;
      }
      i += 1;
      continue;
    }
    if (ch === "/" && next === "/") {
      i += 2;
      while (i < source.length && source[i] !== "\n") {
        i += 1;
      }
      continue;
    }
    if (ch === "/" && next === "*") {
      i += 2;
      while (i < source.length && !(source[i] === "*" && source[i + 1] === "/")) {
        i += 1;
      }
      i += 2;
      continue;
    }
    if (ch === '"' || ch === "'" || ch === "`") {
      mode = ch;
      i += 1;
      continue;
    }
    if (ch === "(") {
      depth += 1;
    } else if (ch === ")") {
      depth -= 1;
      if (depth === 0) {
        return source.slice(open, i);
      }
    }
    i += 1;
  }
  return null;
}

/**
 * A single-line MSBuild element open tag `<Name ` followed by 1+ `attr="value"`
 * pairs. Even ONE attribute makes the open tag fragile: a formatter can wrap its
 * closing `>` (`<Target Name="x"\n>`) or the attribute value itself, so any
 * contiguous-substring/regex check breaks exactly like the original failure did.
 * A bare `<Tag>` with NO attributes is adjacent to its `>` and cannot wrap, so it
 * is intentionally NOT matched.
 */
const ATTRIBUTED_OPEN_TAG =
  /<[A-Za-z_][\w:.-]*\s+(?:[A-Za-z_][\w:.-]*\s*=\s*(?:"[^"\n]*"|'[^'\n]*')\s*)+/;

/**
 * A BARE MSBuild attribute pair `attr="value"` with NO leading `<Element`,
 * matched wherever it appears CONTAINED inside a literal/run. Three properties
 * keep this from over-firing on ordinary code while still catching the policed
 * fragility:
 *   - The name is anchored on a non-identifier boundary (start, whitespace, `,`,
 *     `(`, `[`, `{`) so a longer identifier is not split into a bogus attribute.
 *   - The `=` is CONTIGUOUS (no surrounding whitespace), matching how an MSBuild
 *     attribute is actually written (`Include="..."`); a C# / JS assignment
 *     (`string Value = "..."`, `const x = "..."`) uses spaces around `=` and is
 *     therefore NOT mistaken for an attribute pair.
 *   - The value is DOUBLE-quoted (MSBuild attribute syntax), so a single-quoted
 *     string-of-prose value is not flagged.
 *
 * This is the attribute-only fragility: a contiguous `DestinationFolder="$(X)"`
 * substring assumes the name, `=`, and quoted value sit on ONE line, but a
 * formatter may wrap the value onto the next line (`DestinationFolder=\n  "$(X)"`),
 * breaking the substring just like a wrapped open tag.
 *
 * The element-open form already carries this pair, so the caller only tests a
 * literal/run for a "bare" pair when it has NO `<` at all: a true element-open
 * literal routes through ATTRIBUTED_OPEN_TAG (and is reported as such) while a
 * tagless `attr="value"` -- whether a matcher argument, a hoisted `const`, an
 * array element, or a `+`-assembled run -- routes here. Scanning every literal/run
 * (not just the matcher argument) closes the original Form-2 asymmetry whereby a
 * hoisted/array-stored bare pair slipped past the policy.
 */
const BARE_ATTRIBUTE_PAIR = /(?:^|[\s,([{])[A-Za-z_][\w:.-]*="[^"\n]*"/;

/**
 * Detect a single-line MSBuild element-open (or bare attribute) substring carrying
 * an `attr="..."` pair asserted against XML config content (shape (b)).
 *
 * Three fragile forms are caught, all in a suite that demonstrably reads an XML
 * config file (the caller gates on readsXmlConfig):
 *
 *   1. An attributed element-open STRING literal anywhere in the file -- a matcher
 *      argument (`toContain('<Target Name="x">')`), a hoisted `const targetOpen =
 *      '<Target Name="x">'`, or one arm of a `'<Tag a="x"' + ' b="y">'`
 *      concatenation (reassembled below). A `<Tag attr="x">` literal in an
 *      XML-config-reading test has no benign purpose, so we flag it wherever it
 *      sits rather than only as a direct matcher argument.
 *   2. A bare contiguous `attr="value"` substring (no `<Element`) -- whether the
 *      direct argument of a content check (`toContain('DestinationFolder="$(X)"')`),
 *      a hoisted `const want = 'Include="..."'`, an array element, or one arm of a
 *      concatenated run. The value can wrap onto its own line, so this is as fragile
 *      as the element-open case; route it through hasElement/getElements instead.
 *      Scanned over EVERY string literal and concatenated run (symmetric with the
 *      element-open Form 1), not only the matcher-argument literal, so the same
 *      fragility lifted one line up out of the matcher call is still caught.
 *   3. The REGEX-literal form of the element-open fragility (`/<Target Name="x">/`)
 *      passed to `toMatch(...)` / used as `regex.test(content)`. A string scan
 *      never sees a regex arg, so the regex literals are scanned directly.
 *
 * @param {string} source Test file source.
 * @returns {string|null} A diagnostic when fragile, else null.
 */
function detectMultiAttrSubstring(source) {
  // Form 1: any string/template literal that, on its own, contains an attributed
  // element open tag. Catches the direct-matcher-argument case AND the hoisted
  // `const` / variable case the prior string-only matcher scan missed. Form 2 (a
  // BARE `attr="value"` pair carrying NO `<Element`, e.g. a hoisted/array-stored
  // `Include="..."`) is scanned over the SAME literals so it is symmetric with
  // Form 1 -- the original Form-2 asymmetry only inspected the matcher argument,
  // letting a hoisted/array bare pair evade the policy.
  const literals = stringLiterals(source);
  for (const literal of literals) {
    if (ATTRIBUTED_OPEN_TAG.test(literal)) {
      return `embeds a single-line attributed element open tag in a string literal (assert it structurally via scripts/lib/msbuild-xml.js hasElement/getElements): ${literal
        .replace(/\s+/g, " ")
        .slice(0, 80)}`;
    }
    // A literal carrying a `<` routes through ATTRIBUTED_OPEN_TAG above (or is a
    // benign tagless string); only a literal with NO `<` can be a "bare" pair.
    if (!literal.includes("<") && BARE_ATTRIBUTE_PAIR.test(literal)) {
      return `embeds a bare contiguous attribute pair (\`attr="value"\`) in a string literal (its value can wrap; use hasElement/getElements): ${literal
        .replace(/\s+/g, " ")
        .slice(0, 80)}`;
    }
  }

  // Form 1 (split): a `'<Tag a="x"' + ' b="y">'` concatenation -- no single arm is
  // a full attributed open tag, but the assembled run is. Reconstruct runs of
  // string literals joined only by `+` (and whitespace/newlines) and re-scan.
  // Form 2 split (a bare pair assembled across `+`) is checked on the same runs.
  for (const joined of concatenatedStringRuns(source)) {
    if (ATTRIBUTED_OPEN_TAG.test(joined)) {
      return `assembles a single-line attributed element open tag by string concatenation (assert it structurally via scripts/lib/msbuild-xml.js): ${joined
        .replace(/\s+/g, " ")
        .slice(0, 80)}`;
    }
    if (!joined.includes("<") && BARE_ATTRIBUTE_PAIR.test(joined)) {
      return `assembles a bare contiguous attribute pair (\`attr="value"\`) by string concatenation (its value can wrap; use hasElement/getElements): ${joined
        .replace(/\s+/g, " ")
        .slice(0, 80)}`;
    }
  }

  // Form 3: a regex literal carrying an attributed open tag (`/<Target Name="x">/`),
  // passed to toMatch / used in `.test(...)`. An attribute value in a regex literal
  // may be unquoted-equivalent, so accept `attr=\s*("..."|'...'|\S+)`.
  const openTagInRegex =
    /<[A-Za-z_][\w:.-]*(?:\\?[\s])+[A-Za-z_][\w:.-]*\s*=\s*(?:\\?["'][^"'\n]*\\?["']|[^\s>]+)[\s\S]*?>/;
  for (const literal of regexLiterals(source)) {
    if (openTagInRegex.test(literal)) {
      return `passes a single-line attributed element-open regex literal to a content check (toMatch/.test): ${literal.slice(
        0,
        80
      )}`;
    }
  }
  return null;
}

/**
 * Reconstruct each maximal run of string/template literals joined only by the `+`
 * operator (with arbitrary whitespace/newlines/comments between), returning the
 * concatenated decoded text of each run. This lets shape (b) catch a fragile open
 * tag split across `'<Tag a="x"' + ' b="y">'`, where no single literal carries the
 * full attributed open tag. A run interrupted by any non-string, non-`+` token
 * ends; a non-literal `+` operand (a variable) breaks the run there.
 *
 * Implementation: tokenize into string-literal placeholders and `+`/other, then
 * stitch adjacent `string (+ string)*` sequences.
 *
 * @param {string} source Source text.
 * @returns {string[]} Concatenated text of each multi-literal run.
 */
function concatenatedStringRuns(source) {
  const runs = [];
  let i = 0;
  let prevSignificant = "";
  let current = null; // accumulating decoded run, or null between runs
  let expectingOperand = true; // true when a `+` was just seen (next string continues run)
  while (i < source.length) {
    const ch = source[i];
    const next = source[i + 1];
    if (ch === "/" && next === "/") {
      i += 2;
      while (i < source.length && source[i] !== "\n") {
        i += 1;
      }
      continue;
    }
    if (ch === "/" && next === "*") {
      i += 2;
      while (i < source.length && !(source[i] === "*" && source[i + 1] === "/")) {
        i += 1;
      }
      i += 2;
      continue;
    }
    if (ch === "/" && isRegexContext(prevSignificant)) {
      let j = i + 1;
      let inClass = false;
      for (; j < source.length; j += 1) {
        const c = source[j];
        if (c === "\\") {
          j += 1;
          continue;
        }
        if (c === "[") {
          inClass = true;
        } else if (c === "]") {
          inClass = false;
        } else if (c === "/" && !inClass) {
          break;
        } else if (c === "\n") {
          break;
        }
      }
      if (j < source.length && source[j] === "/") {
        i = j + 1;
        prevSignificant = "/";
        if (current !== null) {
          if (current.length > 0) {
            runs.push(current);
          }
          current = null;
        }
        expectingOperand = false;
        continue;
      }
    }
    if (ch === '"' || ch === "'" || ch === "`") {
      const quote = ch;
      let j = i + 1;
      let body = "";
      for (; j < source.length; j += 1) {
        const c = source[j];
        if (c === "\\") {
          const escaped = source[j + 1];
          body +=
            escaped === "n" ? "\n" : escaped === "t" ? "\t" : escaped === undefined ? "" : escaped;
          j += 1;
          continue;
        }
        if (c === quote) {
          break;
        }
        if (quote === "`" && c === "$" && source[j + 1] === "{") {
          body += " ";
          j += 2;
          let depth = 1;
          for (; j < source.length && depth > 0; j += 1) {
            if (source[j] === "{") {
              depth += 1;
            } else if (source[j] === "}") {
              depth -= 1;
            }
          }
          j -= 1;
          continue;
        }
        body += c;
      }
      // A string literal: start or continue a run.
      if (current === null || expectingOperand) {
        current = (current || "") + body;
      } else {
        // Two adjacent strings without a `+` between -- end the prior run.
        if (current.length > 0) {
          runs.push(current);
        }
        current = body;
      }
      expectingOperand = false;
      i = j + 1;
      prevSignificant = quote;
      continue;
    }
    if (/\s/.test(ch)) {
      i += 1;
      continue;
    }
    if (ch === "+") {
      // Continue the run iff we are mid-run; a `++` is not concatenation.
      if (current !== null && next !== "+") {
        expectingOperand = true;
      } else if (current !== null) {
        if (current.length > 0) {
          runs.push(current);
        }
        current = null;
        expectingOperand = false;
      }
      prevSignificant = ch;
      i += 1;
      continue;
    }
    // Any other significant token ends the current run.
    if (current !== null) {
      if (current.length > 0) {
        runs.push(current);
      }
      current = null;
    }
    expectingOperand = false;
    prevSignificant = ch;
    i += 1;
  }
  if (current !== null && current.length > 0) {
    runs.push(current);
  }
  return runs;
}

describe("XML contract assertions are formatting-invariant", () => {
  const files = collectTestFiles(SCRIPTS_DIR, []).map((abs) => ({
    abs,
    rel: path.relative(REPO_ROOT, abs)
  }));

  test("the policy scans a non-trivial number of test files", () => {
    expect(files.length).toBeGreaterThan(10);
  });

  test("scripts/lib/msbuild-xml.js exists and exports the structural readers", () => {
    const helper = require("../lib/msbuild-xml");
    expect(typeof helper.getPropertyValue).toBe("function");
    expect(typeof helper.resolveProperty).toBe("function");
    expect(typeof helper.hasElement).toBe("function");
  });

  test("no test hand-rolls a fragile XML-content assertion", () => {
    const violations = [];
    for (const { abs, rel } of files) {
      // Compare on POSIX-normalized paths so the allowlist literals are
      // portable and the match is not subject to native-separator drift.
      const normalizedRel = rel.split(path.sep).join("/");
      if (ALLOWLIST.has(normalizedRel)) {
        continue;
      }
      const source = fs.readFileSync(abs, "utf8");

      // Shape (a) -- named extractors / contiguous `</NAME>` close-tag regex: a
      // local extractor by that name/shape is fragile regardless of whether the
      // suite reads an XML file (it is, by name, an MSBuild reader).
      const localExtractor = detectLocalExtractor(source);
      if (localExtractor !== null && !HELPER_REQUIRE.test(source)) {
        violations.push(`${normalizedRel}: ${localExtractor}`);
      }

      // Shape (a)/(b) -- patterns that depend on the file actually being XML
      // config: a single-line attributed element SUBSTRING (shape b) and an
      // element-OPEN regex literal (`/<Tag ...>/`) are only fragile MSBuild-config
      // assertions in a suite that demonstrably reads .props/.csproj/.targets/.sln
      // (an open-tag regex over unrelated SVG/HTML markup is not flagged).
      if (readsXmlConfig(source)) {
        const multiAttr = detectMultiAttrSubstring(source);
        if (multiAttr !== null) {
          violations.push(`${normalizedRel}: ${multiAttr}`);
        }
        const openTagRegex = detectOpenTagRegexLiteral(source);
        if (openTagRegex !== null && !HELPER_REQUIRE.test(source)) {
          violations.push(`${normalizedRel}: ${openTagRegex}`);
        }
        // The string-built `new RegExp("<Tag ...>")` re-spelling of the same fragile
        // open-tag pattern, which the literal scan above never sees.
        const openTagBuilt = detectOpenTagRegexStringBuilt(source);
        if (openTagBuilt !== null && !HELPER_REQUIRE.test(source)) {
          violations.push(`${normalizedRel}: ${openTagBuilt}`);
        }
        // A contiguous FIXED-name close-tag regex (`/<\/Target>/`,
        // `new RegExp("</PropertyGroup>")`) -- the literal-name re-spelling of the
        // original failure class that detectLocalExtractor's `</${name}`-only
        // branch never sees. MSBuild-vocabulary names only, so an HTML/SVG `</a>`
        // in a config-reading suite is not flagged.
        const fixedClose = detectFixedNameCloseTagRegex(source);
        if (fixedClose !== null && !HELPER_REQUIRE.test(source)) {
          violations.push(`${normalizedRel}: ${fixedClose}`);
        }
      }
    }

    if (violations.length > 0) {
      throw new Error(
        "Fragile, formatting-sensitive XML-content assertions found. Route XML config " +
          "content assertions through scripts/lib/msbuild-xml.js (getPropertyValue / " +
          "resolveProperty / hasElement), which tolerate line-wrapping, attribute order, " +
          "and CRLF (the repo's prettier does not format XML):\n  " +
          violations.join("\n  ")
      );
    }
    expect(violations).toEqual([]);
  });

  // Self-coverage: prove the heuristics catch every fragile shape they claim to,
  // and do NOT fire on the safe/equivalent structural forms. These guard against
  // the detectors silently regressing into no-ops.
  describe("detector self-coverage", () => {
    test("shape (a): the two named extractors are caught", () => {
      expect(detectLocalExtractor("function getPropertyValue(x) { return x; }")).toMatch(
        /getPropertyValue/
      );
      expect(detectLocalExtractor("function resolveProperty(x) { return x; }")).toMatch(
        /resolveProperty/
      );
    });

    test("shape (a): a contiguous `</NAME>` close-tag regex is caught", () => {
      const src = "const re = new RegExp(`<${name}>([^<]*)</${name}>`);";
      expect(detectLocalExtractor(src)).toMatch(/contiguous/);
    });

    test("shape (a): an arbitrary-named local open-tag regex extractor is caught", () => {
      const src = [
        "function targetConditions(content) {",
        "  const re = /<Target\\b([^>]*)>/g;",
        "  return re.exec(content);",
        "}"
      ].join("\n");
      expect(detectOpenTagRegexLiteral(src)).toMatch(/element-open regex literal/);
    });

    test("shape (a): an open-tag regex inside an ARROW test callback is caught", () => {
      // The original detector only scanned named function bodies; an inline regex
      // in a `test(() => {...})` callback (where most assertions live) must trip too.
      const src = [
        "test('x', () => {",
        "  const m = content.match(/<PropertyGroup\\b([^>]*)>/g);",
        "  expect(m).toBeTruthy();",
        "});"
      ].join("\n");
      expect(detectOpenTagRegexLiteral(src)).toMatch(/element-open regex literal/);
    });

    test("shape (a): re-spelled open-tag regex boundaries are caught", () => {
      // Character class, literal space, and paren boundaries after the tag name --
      // all the same fragile pattern, none escaping the detector.
      expect(detectOpenTagRegexLiteral("const r = /<Target[ \\t]([^>]*)>/;")).toMatch(
        /element-open regex literal/
      );
      expect(detectOpenTagRegexLiteral("const r = /<Target >/;")).toMatch(
        /element-open regex literal/
      );
      expect(detectOpenTagRegexLiteral("const r = /<Target(\\s+[^>]*)?>/;")).toMatch(
        /element-open regex literal/
      );
    });

    test('shape (a): a STRING-built open-tag regex (`new RegExp("<Tag ...>")`) is caught', () => {
      // The string-built re-spelling: neither the close-tag-only `new RegExp(`
      // branch of detectLocalExtractor nor the regex-LITERAL scan sees it, so a
      // dedicated string-argument scan must. Inline literal, `+`-concatenation, and
      // a `const pat = "..."; new RegExp(pat)` indirection are all caught.
      expect(
        detectOpenTagRegexStringBuilt('const re = new RegExp("<Target\\\\s+([^>]*)>");')
      ).toMatch(/new RegExp/);
      expect(
        detectOpenTagRegexStringBuilt('const re = new RegExp("<Target" + "\\\\s+([^>]*)>");')
      ).toMatch(/new RegExp/);
      expect(
        detectOpenTagRegexStringBuilt(
          'const pat = "<Target\\\\s+([^>]*)>"; const re = new RegExp(pat);'
        )
      ).toMatch(/new RegExp/);
      // The detectLocalExtractor close-tag branch is unaffected and still fires on
      // its own shape; the open-tag string-built form is genuinely additional.
      expect(detectLocalExtractor('const re = new RegExp("<Target\\\\s+([^>]*)>");')).toBeNull();
    });

    test("shape (a): a string-built regex with NO open-tag shape is NOT caught", () => {
      // A bare `<Tag>` (no boundary), a close tag, or a non-XML pattern must not trip.
      expect(detectOpenTagRegexStringBuilt('new RegExp("<PrivateAssets>");')).toBeNull();
      expect(detectOpenTagRegexStringBuilt('new RegExp("\\\\d+\\\\.\\\\d+");')).toBeNull();
      // A const that is never fed to new RegExp() is not flagged.
      expect(detectOpenTagRegexStringBuilt('const pat = "<Target\\\\s+>"; foo(pat);')).toBeNull();
    });

    test("shape (a): a contiguous FIXED-name close-tag regex is caught (literal + new RegExp)", () => {
      // The original failure class re-spelled with a hard-coded element name, which
      // detectLocalExtractor's `</${name}`-only close-tag branch never sees.
      expect(detectFixedNameCloseTagRegex("const r = /<Target>([^<]*)<\\/Target>/;")).toMatch(
        /fixed-name close-tag/
      );
      expect(
        detectFixedNameCloseTagRegex(
          'const r = new RegExp("<MicrosoftCodeAnalysisVersion>([^<]*)</MicrosoftCodeAnalysisVersion>");'
        )
      ).toMatch(/fixed-name close-tag/);
      // detectLocalExtractor (the prior detector) genuinely misses this shape --
      // proving the new detector is additive, not redundant.
      expect(
        detectLocalExtractor('const r = new RegExp("<Target>([^<]*)</Target>");')
      ).toBeNull();
    });

    test("shape (a): the helper-style `</NAME\\s*>` close tag is NOT caught (it is the safe form)", () => {
      // The shared helper builds `</${escapeRegExp(name)}\s*>` -- a `\s*` before `>`
      // tolerates a wrapped close tag, so it is the CORRECT structural form and must
      // not be mistaken for the contiguous fragility.
      expect(detectFixedNameCloseTagRegex("const r = /<\\/Target\\s*>/;")).toBeNull();
    });

    test("shape (a)/finding-5: an HTML/SVG/shell `</tag>` close tag is NOT caught (MSBuild vocabulary only)", () => {
      // Lowercase HTML/SVG element names are excluded by isMsbuildElementName, so the
      // fixed-name close-tag scan does not false-positive on unrelated markup even in
      // a suite that reads a config file.
      expect(detectFixedNameCloseTagRegex("const r = /<\\/div>/;")).toBeNull();
      expect(detectFixedNameCloseTagRegex('const r = new RegExp("<a>([^<]*)</a>");')).toBeNull();
      expect(detectFixedNameCloseTagRegex("const r = /<\\/svg>/;")).toBeNull();
      expect(detectFixedNameCloseTagRegex("const r = /<\\/rect>/;")).toBeNull();
      // The MSBuild-name predicate itself: PascalCase in, lowercase out.
      expect(isMsbuildElementName("PropertyGroup")).toBe(true);
      expect(isMsbuildElementName("MicrosoftCodeAnalysisVersion")).toBe(true);
      expect(isMsbuildElementName("div")).toBe(false);
      expect(isMsbuildElementName("svg")).toBe(false);
    });

    test("shape (a): a structural helper-based function is NOT caught", () => {
      const src = [
        "function propertyGroupConditions(content) {",
        "  return getElements(content, 'PropertyGroup').map((e) => e.attributes.Condition);",
        "}"
      ].join("\n");
      expect(detectLocalExtractor(src)).toBeNull();
      expect(detectOpenTagRegexLiteral(src)).toBeNull();
    });

    test("shape (a): a benign non-XML regex (e.g. /^4\\./, division) is NOT caught", () => {
      expect(detectOpenTagRegexLiteral("expect(v).not.toMatch(/^4\\./);")).toBeNull();
      expect(detectOpenTagRegexLiteral("const half = total / 2; const r = /\\d+/;")).toBeNull();
      // A bare `<Tag>` regex with no attributes / boundary cannot wrap; not flagged.
      expect(detectOpenTagRegexLiteral("const r = /<PrivateAssets>/;")).toBeNull();
    });

    test("shape (a): an open-tag regex over UNRELATED markup (SVG) is only flagged in an XML-config suite", () => {
      // The open-tag-regex scan is gated by the main test on readsXmlConfig, so a
      // `/<rect[^>]*\/>/` in a suite that does NOT read .props/.csproj is not an
      // MSBuild-config assertion. Verify the gate's inputs directly.
      const svgSuite = "expect(svg).toMatch(/<rect[^>]*\\/>/);";
      expect(readsXmlConfig(svgSuite)).toBe(false);
      // The detector itself fires on the shape; it is the readsXmlConfig gate that
      // (correctly) prevents flagging it for non-config markup.
      expect(detectOpenTagRegexLiteral(svgSuite)).toMatch(/element-open regex literal/);
    });

    test("shape (b): single-attribute open-tag substring is caught for each idiom", () => {
      expect(detectMultiAttrSubstring(`expect(c).toContain('<Target Name="x">');`)).not.toBeNull();
      expect(
        detectMultiAttrSubstring(`expect(c.includes('<Target Name="x">')).toBe(true);`)
      ).not.toBeNull();
      expect(
        detectMultiAttrSubstring(`expect(c.indexOf('<Target Name="x">')).toBeGreaterThan(-1);`)
      ).not.toBeNull();
      expect(
        detectMultiAttrSubstring(`expect(c).toEqual(expect.stringContaining('<Target Name="x">'));`)
      ).not.toBeNull();
    });

    test("shape (b): multi-attribute open-tag substring is caught", () => {
      const src = 'expect(c).toContain(\'<PackageReference Include="X" Version="3.8.0">\');';
      expect(detectMultiAttrSubstring(src)).not.toBeNull();
    });

    test("shape (b): a REGEX-literal attributed open tag passed to toMatch is caught", () => {
      // A string matcher never sees a regex arg, so this would have slipped past
      // the string-only scan; the regex-literal pass must catch it.
      expect(
        detectMultiAttrSubstring('expect(c).toMatch(/<Target Name="PostBuildCopyAnalyzers">/);')
      ).not.toBeNull();
    });

    test("shape (b): a bare regex `.test()` attributed open tag is caught", () => {
      expect(
        detectMultiAttrSubstring('expect(/<PackageReference Include="X">/.test(c)).toBe(true);')
      ).not.toBeNull();
    });

    test("shape (b): a bare `<Tag>` with no attributes is NOT caught (cannot wrap)", () => {
      expect(detectMultiAttrSubstring(`expect(c).toContain('<PrivateAssets>');`)).toBeNull();
      expect(detectMultiAttrSubstring(`expect(c).toContain('</PackageReference>');`)).toBeNull();
      // A no-attribute regex open tag is likewise not flagged.
      expect(detectMultiAttrSubstring("expect(c).toMatch(/<PrivateAssets>/);")).toBeNull();
    });

    test("shape (b): an attributed open tag hoisted into a `const` is caught", () => {
      // The original failure shape, lifted one line up out of the matcher call.
      const src = [
        'const targetOpen = \'<Target Name="PostBuildCopyAnalyzers" AfterTargets="Build">\';',
        "expect(content).toContain(targetOpen);"
      ].join("\n");
      expect(detectMultiAttrSubstring(src)).toMatch(/string literal/);
    });

    test("shape (b): an attributed open tag split across a string concatenation is caught", () => {
      // Split mid-attribute so NEITHER arm alone is a complete attributed open tag
      // (`<Target Name=` has no value yet; `"x">` has no name) -- only the assembled
      // run is fragile, exercising concatenatedStringRuns specifically.
      const src = `expect(c).toContain('<Target Name=' + '"x">');`;
      expect(detectMultiAttrSubstring(src)).toMatch(/concatenation/);
    });

    test('shape (b): a bare contiguous `attr="value"` substring is caught', () => {
      // No leading `<Element`, but the attribute value can still wrap onto its own
      // line; the contiguous substring is therefore fragile.
      expect(
        detectMultiAttrSubstring(
          `expect(c).toContain('DestinationFolder="$(AnalyzerPayloadOutputDir)"');`
        )
      ).toMatch(/bare contiguous attribute pair/);
      expect(
        detectMultiAttrSubstring(
          `expect(c.includes('Include="Microsoft.CodeAnalysis.CSharp"')).toBe(true);`
        )
      ).toMatch(/bare contiguous attribute pair/);
    });

    test('shape (b): a bare `attr="value"` HOISTED into a const / stored in an array is caught', () => {
      // The Form-2 analogue of the line-~904 element-open hoist: the original
      // detector only scanned the matcher ARGUMENT, so lifting the bare pair into a
      // `const` (or an array of pairs) evaded the policy entirely. It is now scanned
      // over every string literal, symmetric with the element-open Form 1.
      const hoisted = [
        `const want = 'Include="Microsoft.CodeAnalysis.CSharp"';`,
        "expect(content.includes(want)).toBe(true);"
      ].join("\n");
      expect(detectMultiAttrSubstring(hoisted)).toMatch(/bare contiguous attribute pair/);

      const arrayStored = [
        "const pairs = ['Include=\"Microsoft.CodeAnalysis.CSharp\"', 'Version=\"3.8.0\"'];",
        "for (const p of pairs) { expect(content.includes(p)).toBe(true); }"
      ].join("\n");
      expect(detectMultiAttrSubstring(arrayStored)).toMatch(/bare contiguous attribute pair/);
    });

    test('shape (b): a bare `attr="value"` assembled by concatenation is caught', () => {
      // Split mid-pair so no single arm is a complete `attr="value"`; only the
      // assembled run is, exercising concatenatedStringRuns for the bare-pair form.
      const src = `expect(c).toContain('Version=' + '"3.8.0"');`;
      expect(detectMultiAttrSubstring(src)).toMatch(/by string concatenation/);
    });

    test("shape (b): an unrelated bare string (not an attribute pair) is NOT caught", () => {
      // A plain prose/value substring with no `attr="value"` shape is safe.
      expect(detectMultiAttrSubstring(`expect(c).toContain('Editor/Analyzers');`)).toBeNull();
      expect(detectMultiAttrSubstring(`expect(c).toContain('3.8.0');`)).toBeNull();
      // A value-only literal that merely contains `=` is not an attribute pair.
      expect(
        detectMultiAttrSubstring(`expect(c).toContain('/p:CopyAnalyzerPayload=false');`)
      ).toBeNull();
      // A C# / JS assignment uses SPACES around `=`, so it is not an MSBuild
      // attribute pair and must NOT trip the bare-pair scan (guards the broadened
      // every-literal scan against false-positives on ordinary code fixtures).
      expect(detectMultiAttrSubstring(`const fixture = 'string Value = "zzz";';`)).toBeNull();
      expect(detectMultiAttrSubstring(`const js = 'const value = "zzz";';`)).toBeNull();
      // A single-quoted value is not MSBuild attribute syntax (double-quoted).
      expect(detectMultiAttrSubstring(`const s = "name='zzz'";`)).toBeNull();
    });

    test("string/concatenation walkers decode literals and reassemble runs", () => {
      // stringLiterals resolves escapes and blanks template interpolations.
      expect(stringLiterals(`const a = 'x\\"y'; const b = \`p\${q}r\`;`)).toEqual(['x"y', "p r"]);
      // concatenatedStringRuns stitches `+`-joined literals; a variable breaks the run.
      expect(concatenatedStringRuns(`'a' + 'b' + 'c'`)).toEqual(["abc"]);
      expect(concatenatedStringRuns(`'a' + x + 'b'`)).toEqual(["a", "b"]);
    });

    test("readsXmlConfig requires real evidence of reading a config file", () => {
      // Mention-only (a comment) is NOT enough.
      expect(readsXmlConfig("// edits Directory.Build.props sometimes")).toBe(false);
      // A path literal plus a read call IS enough.
      expect(
        readsXmlConfig("const p = path.join(r, 'Directory.Build.props'); fs.readFileSync(p);")
      ).toBe(true);
      // Importing the shared helper / contract module counts.
      expect(readsXmlConfig('require("../lib/msbuild-xml");')).toBe(true);
      expect(readsXmlConfig('require("../lib/analyzer-build-contract");')).toBe(true);
      // Unrelated markup with no config-file evidence is NOT flagged.
      expect(readsXmlConfig('expect(html).toContain(\'<a href="x" target="y">\');')).toBe(false);
    });
  });
});
