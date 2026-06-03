"use strict";

/**
 * Returns true when a value has matching quote boundaries using the same quote character.
 *
 * @param {string} value - Raw TOML/YAML scalar value
 * @returns {boolean} True when value starts and ends with matching single or double quotes
 */
function hasMatchingBoundaryQuotes(value) {
  const trimmed = String(value).trim();
  return hasMatchingBoundaryQuotesInTrimmed(trimmed);
}

/**
 * Checks matching quote boundaries on an already trimmed scalar.
 *
 * @param {string} trimmed - Pre-trimmed TOML/YAML scalar value
 * @returns {boolean} True when boundaries are matching quotes
 */
function hasMatchingBoundaryQuotesInTrimmed(trimmed) {
  if (trimmed.length < 2) {
    return false;
  }

  const firstChar = trimmed[0];
  const lastChar = trimmed[trimmed.length - 1];
  const isQuote = firstChar === '"' || firstChar === "'";

  return isQuote && firstChar === lastChar;
}

/**
 * Strip wrapping quotes only when both boundary quotes match.
 *
 * @param {string} value - Raw TOML/YAML scalar value
 * @returns {string} Trimmed value with matching boundary quotes removed
 */
function stripMatchingBoundaryQuotes(value) {
  const trimmed = String(value).trim();
  if (!hasMatchingBoundaryQuotesInTrimmed(trimmed)) {
    return trimmed;
  }

  return trimmed.slice(1, -1);
}

/**
 * Normalize all supported newline forms to LF.
 *
 * Converts CRLF (\r\n) and lone CR (\r) to LF (\n).
 *
 * @param {string} value - Raw text content
 * @returns {string} Text normalized to LF line endings
 */
function normalizeToLf(value) {
  return String(value).replace(/\r\n/g, "\n").replace(/\r/g, "\n");
}

module.exports = {
  hasMatchingBoundaryQuotes,
  stripMatchingBoundaryQuotes,
  normalizeToLf
};
