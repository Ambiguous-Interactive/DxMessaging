"use strict";

/**
 * @fileoverview Formatting-invariant readers for MSBuild / structured XML config
 * (`.props` / `.csproj` / `.targets`). These exist because contract tests used to
 * assert on the CONTENT of those files with hand-rolled regex/substring patterns
 * that assumed single-line tags, a fixed attribute order, and LF line endings.
 *
 * Those assumptions are wrong: the repo's prettier does NOT format XML (there is
 * no `@prettier/plugin-xml` and no `.props`/`.csproj` override), so any editor,
 * hand-edit, or future formatter can legitimately re-wrap an element so its
 * closing `>` sits on its own line (`</Name\n  >`) or split a multi-attribute
 * open tag one attribute per line. The files stay SEMANTICALLY identical, but a
 * `content.includes('<Tag A="x" B="y">')` or `/<Name>([^<]*)<\/Name>/` assertion
 * breaks. These helpers parse STRUCTURALLY instead, tolerating line-wrapping,
 * attribute reordering, surrounding whitespace, CRLF, and XML comments (an
 * element that exists only inside `<!-- ... -->` is never matched).
 *
 * Pure Node, zero runtime dependencies, cross-platform (Linux/macOS/Windows).
 */

/**
 * Normalize all newline forms (CRLF, lone CR) to LF so the scanners never have
 * to special-case `\r`. The returned string is only used internally for parsing;
 * callers keep their original content.
 *
 * @param {string} content Raw file content.
 * @returns {string} Content with LF newlines.
 */
function normalizeNewlines(content) {
  return String(content).replace(/\r\n/g, "\n").replace(/\r/g, "\n");
}

/**
 * Blank out XML comments (`<!-- ... -->`) so the structural scanners never match
 * an element that exists only inside a comment. Every non-newline character of a
 * comment is replaced with a space, which preserves the byte offsets and line
 * structure of the surrounding content (so any index-based slicing stays aligned)
 * while making the commented region invisible to the open/close-tag scanners.
 * Unterminated comments (`<!--` with no closing `-->`) blank to end-of-input,
 * matching XML's own treatment.
 *
 * @param {string} content Newline-normalized content.
 * @returns {string} Content of identical length with comment bodies blanked.
 */
function stripComments(content) {
  return content.replace(/<!--[\s\S]*?(?:-->|$)/g, (match) => match.replace(/[^\n]/g, " "));
}

/**
 * Newline-normalize AND comment-blank a document in one step. All structural
 * readers operate on the result so they are uniformly CRLF- and comment-safe.
 *
 * @param {string} content Raw file content.
 * @returns {string} Normalized, comment-blanked content.
 */
function prepare(content) {
  return stripComments(normalizeNewlines(content));
}

/**
 * Escape a string for safe use as a literal inside a RegExp.
 *
 * @param {string} value Literal string.
 * @returns {string} Regex-escaped string.
 */
function escapeRegExp(value) {
  return String(value).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * Find the index of the `>` that closes the open tag beginning at `startIndex`
 * (the index of the `<` of `<Name`). The scan skips any `>` that appears inside
 * a quoted attribute value, so MSBuild conditions such as
 * `Condition="'$(X)' == 'true'"` (which contain no `>` but could) and any
 * value that legitimately contains `>` are handled. Returns the index of the
 * closing `>` or -1 when the tag is never closed.
 *
 * @param {string} content Newline-normalized content.
 * @param {number} startIndex Index of the `<` that opens the tag.
 * @returns {number} Index of the closing `>`, or -1.
 */
function findTagEnd(content, startIndex) {
  let quote = null;
  for (let i = startIndex; i < content.length; i += 1) {
    const ch = content[i];
    if (quote !== null) {
      if (ch === quote) {
        quote = null;
      }
      continue;
    }
    if (ch === '"' || ch === "'") {
      quote = ch;
      continue;
    }
    if (ch === ">") {
      return i;
    }
  }
  return -1;
}

/**
 * Locate the open tag of the first element named `name` at or after `fromIndex`,
 * returning its open-tag boundaries and whether it is self-closing. The match
 * requires a word boundary after the name (so `<Foo` does not match `<FooBar`).
 * Returns null when absent.
 *
 * The scan is quote-aware: a `<Name` candidate that begins INSIDE a quoted
 * attribute value of an earlier open tag is skipped, so a stray `<` inside an
 * attribute value (e.g. `<Other Note="<Foo>"/>`) never produces a spurious match
 * for `Foo`. This mirrors findTagEnd's symmetric handling of `>` inside quotes.
 * (Strictly, a literal `<` inside an XML attribute value is invalid XML and must
 * be `&lt;`, but tolerating it costs nothing and removes a sharp edge.)
 *
 * @param {string} content Newline-normalized content.
 * @param {string} name Element name.
 * @param {number} [fromIndex] Index to start searching from.
 * @returns {{openStart:number, openEnd:number, selfClosing:boolean, tag:string}|null}
 */
function findOpenTag(content, name, fromIndex = 0) {
  // `<Name` followed by whitespace, `>`, or `/` (self-closing). The negative
  // case `<NameOther` is excluded by requiring a non-name char next.
  const re = new RegExp(`<${escapeRegExp(name)}(?=[\\s/>])`, "g");
  re.lastIndex = fromIndex;
  for (let match = re.exec(content); match !== null; match = re.exec(content)) {
    const openStart = match.index;
    if (isInsideQuotedValue(content, openStart, fromIndex)) {
      // This `<Name` sits inside a quoted attribute value of an enclosing open
      // tag; it is not a real element start. Keep scanning after it.
      continue;
    }
    const openEnd = findTagEnd(content, openStart);
    if (openEnd === -1) {
      return null;
    }
    const tag = content.slice(openStart, openEnd + 1);
    const selfClosing = /\/\s*>$/.test(tag);
    return { openStart, openEnd, selfClosing, tag };
  }
  return null;
}

/**
 * True when the `<` at `index` falls inside a quoted attribute value of an open
 * tag that began earlier. Determined by walking forward from the last preceding
 * `<` (or `scanStart`) and tracking quote state up to `index`: if we are inside a
 * quote when we reach `index`, the `<` is part of a value, not an element start.
 * A `>` outside quotes before `index` means we left any open tag, so the `<`
 * cannot be inside one.
 *
 * @param {string} content Newline-normalized content.
 * @param {number} index Index of the `<` candidate.
 * @param {number} scanStart Lower bound for the backward search.
 * @returns {boolean}
 */
function isInsideQuotedValue(content, index, scanStart) {
  // Find the nearest preceding `<` (the start of the enclosing tag candidate).
  let tagStart = -1;
  for (let i = index - 1; i >= scanStart; i -= 1) {
    if (content[i] === "<") {
      tagStart = i;
      break;
    }
  }
  if (tagStart === -1) {
    return false;
  }
  let quote = null;
  for (let i = tagStart; i < index; i += 1) {
    const ch = content[i];
    if (quote !== null) {
      if (ch === quote) {
        quote = null;
      }
      continue;
    }
    if (ch === '"' || ch === "'") {
      quote = ch;
    } else if (ch === ">") {
      // The enclosing tag already closed before `index`; not inside it.
      return false;
    }
  }
  return quote !== null;
}

/**
 * Extract the trimmed inner text of `<name ...>VALUE</name>`. Tolerant of
 * (a) attributes on the open tag, (b) the open tag's `>` sitting on its own line
 * or preceded by whitespace, and (c) a closing tag written `</name\s*>` with a
 * newline / whitespace before the `>`. Returns null when the element is absent
 * or has no matching close tag. CRLF-safe.
 *
 * Only elements with simple text content (no nested child elements) are
 * supported, which matches every MSBuild property this is used for. In
 * particular, nested SAME-NAME elements are not depth-tracked: the open tag is
 * paired with the FIRST `</name>` after it, so `<A><A>inner</A>outer</A>` reads
 * as `<A>inner` rather than the full body. This does not occur among MSBuild
 * scalar property elements (a PropertyGroup property never self-nests) but is a
 * sharp edge for any future same-name nesting.
 *
 * The CLOSE-tag scan is a plain `</name\s*>` regex and -- unlike findOpenTag /
 * findTagEnd, which are quote-aware -- is NOT quote-aware: a literal `</Name>`
 * appearing inside a quoted attribute value of a LATER sibling element would be
 * matched as the close. That requires a raw `</Name>` inside an attribute value,
 * which is invalid XML (it must be `&lt;/Name&gt;`) and does not occur in the
 * in-scope SourceGenerators build files; a caller parsing less-trusted XML must
 * not assume symmetric quote-safety on the close side.
 *
 * @param {string} content Raw file content (CRLF tolerated).
 * @param {string} name Property/element name.
 * @returns {string|null} Trimmed inner text, or null.
 */
function getPropertyValue(content, name) {
  const normalized = prepare(content);
  // Closing tag tolerant of whitespace/newline before the `>`:  </Name   >
  const closeRe = new RegExp(`</${escapeRegExp(name)}\\s*>`, "g");
  let fromIndex = 0;
  for (;;) {
    const open = findOpenTag(normalized, name, fromIndex);
    if (open === null) {
      return null;
    }
    // A self-closing `<Name ... />` carries no inner text; skip it and keep
    // looking for a later `<Name>...</Name>` rather than bailing to null (so the
    // reader is consistent with hasElement, which also iterates past
    // non-matching same-named elements).
    if (open.selfClosing) {
      fromIndex = open.openEnd + 1;
      continue;
    }
    closeRe.lastIndex = open.openEnd + 1;
    const close = closeRe.exec(normalized);
    if (close === null) {
      // No matching close for this open tag; advance past it and keep looking.
      fromIndex = open.openEnd + 1;
      continue;
    }
    return normalized.slice(open.openEnd + 1, close.index).trim();
  }
}

/**
 * Resolve an MSBuild property value by transitively expanding `$(Name)`
 * references against the other properties declared in the file. Mirrors the
 * semantics the contract tests relied on: `$(SolutionDir)`, `$(Configuration)`,
 * and `$(MSBuildProjectName)` are seeded with stand-in values so the resolved
 * string is concrete; an unresolved reference is left behind as a literal
 * `$(name)` (so a missing redirect surfaces as a literal `$(...)` and fails the
 * caller's assertion); a cycle is broken by a seen-set.
 *
 * LIMITATION: this expands plain `$(Name)` property references only. MSBuild
 * property FUNCTIONS and item/metadata syntax -- `$([System.IO.Path]::Combine(...))`,
 * `$(Name.Replace('a','b'))`, `@(Item)`, `%(Metadata)` -- are NOT evaluated; the
 * inner text of such a reference is treated as a property name and, finding no
 * declared property by that (function-call) name, is left literal. None of the
 * SourceGenerators build files in scope use property functions in a redirect path,
 * but a caller asserting on a function-derived value must not rely on resolution.
 *
 * @param {string} content Raw file content (CRLF tolerated).
 * @param {string} name Property name to resolve.
 * @param {Record<string,string>} [seed] Overrides/additions to the default seeds.
 * @returns {string|null} Resolved value, or null when the property is absent.
 */
function resolveProperty(content, name, seed) {
  const seeds = Object.assign(
    { SolutionDir: "REPO/", Configuration: "Release", MSBuildProjectName: "ProjName" },
    seed || {}
  );
  // `stack` is a true cycle guard: it holds only the property names currently on
  // the active recursion stack, and a name is removed once its expansion
  // completes. This breaks a real cycle (A -> B -> A) without conflating it with
  // a repeated/diamond reference (`$(X)-$(X)`, or A and B both referencing C),
  // which must expand at EVERY occurrence. Using a persistent visited-set here
  // would silently drop the 2nd and later occurrences of a referenced property.
  const stack = new Set();
  function expand(propName) {
    if (seeds[propName] !== undefined) {
      return seeds[propName];
    }
    if (stack.has(propName)) {
      return "";
    }
    const raw = getPropertyValue(content, propName);
    if (raw === null) {
      return null;
    }
    stack.add(propName);
    try {
      return raw.replace(/\$\(([^)]+)\)/g, (_, inner) => {
        const expanded = expand(inner);
        return expanded === null ? "$(" + inner + ")" : expanded;
      });
    } finally {
      stack.delete(propName);
    }
  }
  return expand(name);
}

/**
 * Parse the attribute name/value pairs out of an open tag. Attribute values are
 * double- or single-quoted; the parser tolerates the other quote character
 * appearing inside a value (e.g. single quotes inside a double-quoted MSBuild
 * condition). Returns a map of attribute name to its raw (unescaped) value.
 *
 * @param {string} tag The full open tag text, e.g. `<Target Name="x" ... >`.
 * @returns {Record<string,string>} Attribute name to value.
 */
function parseAttributes(tag) {
  const attrs = {};
  // name = "value"  |  name = 'value'  — name allows XML name chars; value is
  // everything up to the matching closing quote.
  const re = /([A-Za-z_:][\w:.-]*)\s*=\s*("([^"]*)"|'([^']*)')/g;
  let match;
  while ((match = re.exec(tag)) !== null) {
    const value = match[3] !== undefined ? match[3] : match[4];
    attrs[match[1]] = value;
  }
  return attrs;
}

/**
 * True iff an element `<name ...>` (or self-closing `<name ... />`) exists whose
 * open tag carries EVERY requested attribute as `attr="value"`, regardless of
 * attribute ORDER and regardless of line-WRAPPING within the open tag. CRLF
 * safe. Robust to single quotes inside double-quoted values (and vice versa).
 *
 * @param {string} content Raw file content (CRLF tolerated).
 * @param {{name:string, attributes?:Record<string,string>}} spec Element spec.
 * @returns {boolean} True when a matching element exists.
 */
function hasElement(content, spec) {
  const { name, attributes = {} } = spec || {};
  if (typeof name !== "string" || name.length === 0) {
    return false;
  }
  const normalized = prepare(content);
  let fromIndex = 0;
  for (;;) {
    const open = findOpenTag(normalized, name, fromIndex);
    if (open === null) {
      return false;
    }
    const attrs = parseAttributes(open.tag);
    const matches = Object.keys(attributes).every((key) => attrs[key] === attributes[key]);
    if (matches) {
      return true;
    }
    fromIndex = open.openEnd + 1;
  }
}

/**
 * Enumerate every element named `name` (comment-blanked, CRLF-safe, line-wrap
 * invariant), returning each one's parsed attribute map, whether it is
 * self-closing, and its trimmed inner text `value` (null for a self-closing
 * element or one with no matching close tag). Order is document order. Useful
 * when a test needs to assert a property OVER ALL occurrences -- e.g. "no
 * <PropertyGroup> carries a Tests-only Condition", or "EVERY
 * <MicrosoftCodeAnalysisVersion> is 3.8.0, not just the first" -- rather than
 * just the first/any match, doing so structurally instead of with a hand-rolled
 * `/<Name\b([^>]*)>/g` open-tag regex that assumes single-line tags.
 *
 * Shares getPropertyValue's two boundary caveats: (1) each open tag is paired
 * with the FIRST `</name>` after it with no depth tracking, so nested same-name
 * elements (`<A><A>inner</A>outer</A>`) produce overlapping entries rather than a
 * single nested one -- only safe for non-self-nesting elements (every in-scope
 * MSBuild element qualifies); and (2) the close-tag scan is a plain `</name\s*>`
 * regex that is NOT quote-aware, so a literal `</Name>` inside a later sibling's
 * quoted attribute value (invalid XML, absent from the in-scope files) would be
 * mismatched. Both are non-issues for the current contract but matter for a
 * future caller on arbitrary XML.
 *
 * @param {string} content Raw file content (CRLF tolerated).
 * @param {string} name Element name.
 * @returns {Array<{attributes:Record<string,string>, selfClosing:boolean, value:(string|null)}>}
 */
function getElements(content, name) {
  if (typeof name !== "string" || name.length === 0) {
    return [];
  }
  const normalized = prepare(content);
  const closeRe = new RegExp(`</${escapeRegExp(name)}\\s*>`, "g");
  const out = [];
  let fromIndex = 0;
  for (;;) {
    const open = findOpenTag(normalized, name, fromIndex);
    if (open === null) {
      return out;
    }
    let value = null;
    if (!open.selfClosing) {
      closeRe.lastIndex = open.openEnd + 1;
      const close = closeRe.exec(normalized);
      if (close !== null) {
        value = normalized.slice(open.openEnd + 1, close.index).trim();
      }
    }
    out.push({ attributes: parseAttributes(open.tag), selfClosing: open.selfClosing, value });
    fromIndex = open.openEnd + 1;
  }
}

module.exports = {
  normalizeNewlines,
  stripComments,
  getPropertyValue,
  resolveProperty,
  hasElement,
  getElements,
  parseAttributes
};
