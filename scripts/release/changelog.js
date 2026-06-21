"use strict";

/**
 * Shared CHANGELOG.md parsing for the release tooling.
 *
 * The changelog follows Keep a Changelog: level-2 headings are versioned
 * sections -- `## [X.Y.Z]` (and `## [Unreleased]`), newest first, oldest last.
 * This module is the SINGLE source of truth for reading those sections, shared
 * by prepare-release.js and the release-notes.js CLI (which release.yml,
 * release-prepare.yml, and release-drafter.yml all call), so the three
 * workflows can never drift on extraction semantics. See
 * .llm/skills/github-actions/release-asset-and-notes-invariants.md.
 */

const { normalizeToLf } = require("../lib/line-endings.js");
const { CodeBlockTracker } = require("../wiki/transform-docs-to-wiki.js");

const UNRELEASED_HEADING = "## [Unreleased]";

/**
 * True for each line that belongs to a fenced code block (``` or ~~~),
 * including the fence delimiter lines themselves. Reuses the shared
 * CodeBlockTracker from the wiki transform so a `## ` line inside a fenced
 * example is never mistaken for a real changelog heading.
 *
 * @param {string[]} lines
 * @returns {boolean[]}
 */
function computeFencedLineMask(lines) {
  const tracker = new CodeBlockTracker();
  return lines.map((line) => {
    const wasInFence = tracker.inCodeBlock;
    return tracker.processLine(line) || wasInFence;
  });
}

/**
 * @param {string} content - Raw CHANGELOG.md text.
 * @param {string} version - Version name, e.g. "3.1.0" or "Unreleased".
 * @returns {boolean} True when a non-fenced `## [version]` heading exists.
 */
function changelogHasVersionHeading(content, version) {
  const lines = normalizeToLf(content).split("\n");
  const fenced = computeFencedLineMask(lines);
  return lines.some((line, index) => line === `## [${version}]` && !fenced[index]);
}

/**
 * Return the trimmed body beneath the `## [version]` heading. Accepts the
 * unbracketed `## version` form as well, mirroring the verify-tag gate in
 * release.yml so any changelog that passes the gate can render its notes.
 * Stops at the next non-fenced `## ` heading; the final section reads to EOF.
 * A `## [x]` line inside a fenced code block is never a boundary. Throws when
 * no matching heading exists or the section has no content.
 *
 * @param {string} content - Raw CHANGELOG.md text.
 * @param {string} version - Version name, e.g. "3.1.0" or "Unreleased".
 * @returns {string} The section body, LF-joined, with surrounding blanks trimmed.
 */
function extractSection(content, version) {
  const lines = normalizeToLf(content).split("\n");
  const fenced = computeFencedLineMask(lines);
  const bracketed = `## [${version}]`;
  const bare = `## ${version}`;
  const headingIndex = lines.findIndex(
    (line, index) => !fenced[index] && (line === bracketed || line === bare)
  );
  if (headingIndex === -1) {
    throw new Error(`CHANGELOG.md has no '## [${version}]' section.`);
  }

  let end = lines.length;
  for (let index = headingIndex + 1; index < lines.length; index += 1) {
    if (lines[index].startsWith("## ") && !fenced[index]) {
      end = index;
      break;
    }
  }

  const body = lines.slice(headingIndex + 1, end);
  while (body.length > 0 && body[0].trim() === "") {
    body.shift();
  }
  while (body.length > 0 && body[body.length - 1].trim() === "") {
    body.pop();
  }
  // Mirror rotateChangelog's hasContent check: a section that is only blank
  // lines or `### ` subsection headers (no entries) is not publishable notes.
  if (!body.some((line) => line.trim() !== "" && !line.startsWith("### "))) {
    throw new Error(`CHANGELOG.md section '## [${version}]' has no content.`);
  }
  return body.join("\n");
}

module.exports = {
  UNRELEASED_HEADING,
  computeFencedLineMask,
  changelogHasVersionHeading,
  extractSection
};
