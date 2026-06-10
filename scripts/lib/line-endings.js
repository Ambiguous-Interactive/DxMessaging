"use strict";

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
  normalizeToLf
};
