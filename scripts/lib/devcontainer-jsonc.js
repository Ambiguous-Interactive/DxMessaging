#!/usr/bin/env node
/**
 * devcontainer-jsonc.js
 *
 * Robust JSONC (JSON with comments) parsing for devcontainer.json and similar
 * VS Code/VSC-style files. The shell counterpart lives at
 * .devcontainer/lib/parse-devcontainer-mounts.sh; the two implementations MUST
 * agree on the comment-stripping algorithm. The parity test in
 * scripts/__tests__/devcontainer-jsonc.test.js loads both and asserts they
 * produce identical mount tuples for a shared fixture set.
 *
 * Public surface:
 *   stripJsoncComments(text)               -> string
 *   parseDevcontainerMounts(text, context) -> Array<{source, target, type, raw}>
 *
 * Failure mode: parseDevcontainerMounts THROWS on invalid JSON after comment
 * stripping. Callers must let the throw propagate (no silent grep fallback).
 */

"use strict";

/**
 * Strip JSONC comments while preserving comment-like sequences inside JSON
 * string literals. The state machine tracks four states:
 *   - default: outside any string/comment
 *   - inString: inside a double-quoted JSON string
 *   - inLineComment: from `//` to end of line
 *   - inBlockComment: from `/*` to nearest `* /`
 *
 * Escapes inside strings (\\, \") are honored so a literal `\"` does not end
 * the string. Single-quoted strings are NOT a thing in JSON; if a file uses
 * them, we treat them as ordinary characters (the downstream JSON.parse will
 * raise the actual syntax error).
 *
 * Comments are replaced with spaces (not deleted) so character offsets in any
 * downstream JSON.parse error map back to recognizable columns. Newlines
 * inside block comments are preserved so line numbers stay aligned.
 *
 * @param {string} text Raw JSONC source.
 * @returns {string} JSON-with-spaces-where-comments-used-to-be.
 */
function stripJsoncComments(text) {
  if (typeof text !== "string") {
    throw new TypeError("stripJsoncComments: expected string input");
  }

  // Strip a single leading UTF-8 BOM. JSON.parse rejects a BOM with
  // "Unexpected token" while jq tolerates it; the bash counterpart in
  // .devcontainer/lib/parse-devcontainer-mounts.sh also strips here so
  // the parity test enforces byte-equivalent output. We strip ONLY at
  // index 0 -- BOM-like bytes inside string literals are preserved.
  if (text.length > 0 && text.charCodeAt(0) === 0xfeff) {
    text = text.slice(1);
  }

  // Normalize CRLF -> LF before the state machine. The awk counterpart
  // does the same per-line via `sub(/\r$/, "", line)`. Doing this
  // unconditionally keeps mount targets parsed from a CRLF-saved
  // devcontainer.json from carrying spurious \r characters that would
  // break subsequent string comparisons against the cache contract.
  if (text.indexOf("\r") !== -1) {
    text = text.replace(/\r\n/g, "\n").replace(/\r/g, "\n");
  }

  const length = text.length;
  let output = "";
  let i = 0;
  let inString = false;
  let inLineComment = false;
  let inBlockComment = false;

  while (i < length) {
    const ch = text[i];
    const next = i + 1 < length ? text[i + 1] : "";

    if (inLineComment) {
      if (ch === "\n") {
        output += ch;
        inLineComment = false;
      } else {
        output += " ";
      }
      i++;
      continue;
    }

    if (inBlockComment) {
      if (ch === "*" && next === "/") {
        output += "  ";
        i += 2;
        inBlockComment = false;
        continue;
      }
      output += ch === "\n" ? "\n" : " ";
      i++;
      continue;
    }

    if (inString) {
      output += ch;
      if (ch === "\\" && i + 1 < length) {
        output += text[i + 1];
        i += 2;
        continue;
      }
      if (ch === '"') {
        inString = false;
      }
      i++;
      continue;
    }

    if (ch === '"') {
      inString = true;
      output += ch;
      i++;
      continue;
    }

    if (ch === "/" && next === "/") {
      inLineComment = true;
      output += "  ";
      i += 2;
      continue;
    }

    if (ch === "/" && next === "*") {
      inBlockComment = true;
      output += "  ";
      i += 2;
      continue;
    }

    output += ch;
    i++;
  }

  return output;
}

function excerptParseError(jsonText, error) {
  const match = error && error.message ? /position\s+(\d+)/i.exec(error.message) : null;
  if (!match) {
    return null;
  }
  const position = Number.parseInt(match[1], 10);
  if (!Number.isFinite(position)) {
    return null;
  }
  const start = Math.max(0, position - 60);
  const end = Math.min(jsonText.length, position + 60);
  return jsonText.slice(start, end).replace(/\n/g, "\\n");
}

/**
 * Parse a devcontainer.json (or compatible JSONC) buffer and return the
 * resolved mounts as an array of {source, target, type, raw} objects.
 *
 * Each mount string is split on commas, then each `key=value` pair is parsed.
 * Both string-form mounts ("source=...,target=...,type=...") and object-form
 * mounts ({ "source": "...", "target": "...", "type": "..." }) are supported.
 *
 * Template substitutions:
 *   - `${containerWorkspaceFolder}` -> options.containerWorkspaceFolder
 *   - `${localWorkspaceFolder}`     -> options.localWorkspaceFolder
 *
 * Substitution happens after JSON parsing so a template variable inside a
 * JSON string is not misread as a comment. Missing context values leave the
 * template variable in place (so callers can compare against the original
 * unresolved form when debugging).
 *
 * @param {string} text Raw devcontainer.json source.
 * @param {object} options
 * @param {string} options.containerWorkspaceFolder Resolved in-container path
 *   (typically CACHE_WORKSPACE_ROOT or /workspaces/<repo>).
 * @param {string} [options.localWorkspaceFolder] Resolved host-side path.
 * @returns {Array<{source: string, target: string, type: string, raw: string}>}
 * @throws {Error} on JSON parse failure (comment-stripped text included).
 */
function parseDevcontainerMounts(text, options = {}) {
  const stripped = stripJsoncComments(text);

  let parsed;
  try {
    parsed = JSON.parse(stripped);
  } catch (error) {
    const excerpt = excerptParseError(stripped, error);
    const detail = excerpt ? ` near "${excerpt}"` : "";
    const wrapped = new Error(
      `parseDevcontainerMounts: JSON.parse failed after comment strip${detail}: ${error.message}`
    );
    wrapped.cause = error;
    throw wrapped;
  }

  if (!parsed || !Array.isArray(parsed.mounts)) {
    return [];
  }

  const containerFolder =
    typeof options.containerWorkspaceFolder === "string" ? options.containerWorkspaceFolder : "";
  const localFolder =
    typeof options.localWorkspaceFolder === "string" ? options.localWorkspaceFolder : "";

  const result = [];
  for (const entry of parsed.mounts) {
    const tuple = normalizeMountEntry(entry, { containerFolder, localFolder });
    if (tuple) {
      result.push(tuple);
    }
  }
  return result;
}

function substituteTemplateVars(value, { containerFolder, localFolder }) {
  if (typeof value !== "string") {
    return value;
  }
  let result = value;
  if (containerFolder) {
    result = result.split("${containerWorkspaceFolder}").join(containerFolder);
  }
  if (localFolder) {
    result = result.split("${localWorkspaceFolder}").join(localFolder);
  }
  return result;
}

function normalizeMountEntry(entry, context) {
  if (typeof entry === "string") {
    const parts = entry.split(",");
    const fields = { source: "", target: "", type: "" };
    for (const part of parts) {
      const eq = part.indexOf("=");
      if (eq < 0) {
        continue;
      }
      const key = part.slice(0, eq).trim();
      const value = part.slice(eq + 1).trim();
      if (key === "source" || key === "target" || key === "type") {
        fields[key] = substituteTemplateVars(value, context);
      }
    }
    return {
      source: fields.source,
      target: fields.target,
      type: fields.type,
      raw: entry
    };
  }
  if (entry && typeof entry === "object") {
    return {
      source: substituteTemplateVars(entry.source || "", context),
      target: substituteTemplateVars(entry.target || "", context),
      type: substituteTemplateVars(entry.type || "", context),
      raw: JSON.stringify(entry)
    };
  }
  return null;
}

/**
 * Extract a top-level scalar property from JSONC text. Mirrors the shell
 * helper `get_devcontainer_property` in
 * `.devcontainer/lib/parse-devcontainer-mounts.sh`. Returns the string form
 * of the value (numbers and booleans are coerced via String(value)), or
 * `undefined` when the key is absent OR explicitly set to `null`.
 *
 * Non-scalar policy (round-3 MINOR-C, locked in by the parity test in
 * scripts/__tests__/devcontainer-jsonc.test.js): when the requested
 * property resolves to an array or object, BOTH implementations FAIL
 * LOUDLY. The Node side throws a TypeError; the bash side exits with
 * status 2 and writes a descriptive diagnostic to stderr. The previous
 * "stringify scalars but silently `String(value)` composites" behavior
 * silently returned `"[object Object]"` on objects and a CSV-ish
 * `"1,2,3"` on arrays in Node while the bash side returned a valid JSON
 * string -- the drift was undetectable through string compare and
 * created a real risk of incorrect downstream extraction. Throwing
 * forces callers to use a JSON-aware path (e.g. parse jq output
 * directly) when reading composite properties.
 *
 * This is the JSONC-aware replacement for any `grep ... \.json"` /
 * `awk ... \.json"` pattern in pre-existing tooling -- grep cannot
 * distinguish a commented-out key (`// "remoteUser": "root"`) from the
 * live key on the next line.
 *
 * @param {string} text Raw JSONC source.
 * @param {string} property Top-level property name.
 * @returns {string | undefined} Property value (stringified) or undefined.
 * @throws {Error} on JSON parse failure (comment-stripped text included).
 * @throws {TypeError} when the property resolves to an array or object.
 */
function getDevcontainerProperty(text, property) {
  if (typeof property !== "string" || property.length === 0) {
    throw new TypeError("getDevcontainerProperty: property must be a non-empty string");
  }
  const stripped = stripJsoncComments(text);
  let parsed;
  try {
    parsed = JSON.parse(stripped);
  } catch (error) {
    const excerpt = excerptParseError(stripped, error);
    const detail = excerpt ? ` near "${excerpt}"` : "";
    const wrapped = new Error(
      `getDevcontainerProperty: JSON.parse failed after comment strip${detail}: ${error.message}`
    );
    wrapped.cause = error;
    throw wrapped;
  }
  if (!parsed || typeof parsed !== "object") {
    return undefined;
  }
  if (!Object.prototype.hasOwnProperty.call(parsed, property)) {
    return undefined;
  }
  const value = parsed[property];
  if (value === null || value === undefined) {
    return undefined;
  }
  if (Array.isArray(value) || (typeof value === "object" && value !== null)) {
    // Round-3 MINOR-C: composite values FAIL LOUDLY (parity with bash).
    // Callers wanting an array/object must parse the JSONC directly.
    const valueType = Array.isArray(value) ? "array" : "object";
    throw new TypeError(
      `getDevcontainerProperty: property '${property}' resolves to a non-scalar ${valueType}; use a JSON-aware reader (parseDevcontainerMounts or JSON.parse(stripJsoncComments(text))) for composite values.`
    );
  }
  return typeof value === "string" ? value : String(value);
}

module.exports = {
  stripJsoncComments,
  parseDevcontainerMounts,
  getDevcontainerProperty
};
