/**
 * @fileoverview Regression guard: no EMPTY GitHub Actions expression (`${{ }}`)
 * anywhere under .github/.
 *
 * THE CLASS THIS GUARDS. GitHub Actions evaluates `${{ ... }}` expressions inside
 * `run:` block scalars too -- including lines that are shell/PowerShell COMMENTS,
 * because a block scalar is opaque string content, not YAML the parser can strip.
 * An empty `${{ }}` (whitespace-only between the braces) is never a valid
 * expression, so GitHub fails to LOAD the workflow/action with
 * "(Line: N, Col: M): An expression was expected" / TemplateValidationException
 * -- which takes down EVERY job/step that uses it. This exact bug shipped in
 * verify-unity-results/action.yml: a PowerShell comment that wrote `${{ }}` to
 * *refer to* the concept of an inline expansion broke every Unity job's verify
 * step. YAML-parsing-based tests do not catch it because a YAML parser never
 * evaluates `${{ }}`; only GitHub's template parser does.
 *
 * Fast static scan. An empty `${{ }}` is unambiguously a bug, so this guard is
 * robust and non-fragile: to reference the expansion concept in prose/comments,
 * write it without the literal empty braces (e.g. "an inline workflow
 * expression").
 */

"use strict";

const fs = require("fs");
const path = require("path");

const GITHUB_DIR = path.resolve(__dirname, "..", "..", ".github");
// Empty / whitespace-only expression: `${{}}`, `${{ }}`, `${{\t}}`, etc.
const EMPTY_EXPRESSION_REGEX = /\$\{\{\s*\}\}/;

/**
 * Recursively collect every *.yml / *.yaml under dir (workflows AND composite
 * actions). workflows-disabled/ is intentionally included -- a disabled file
 * with an empty expression would still fail to load if re-enabled.
 * @param {string} dir
 * @returns {string[]} absolute file paths
 */
function collectYaml(dir) {
  const out = [];
  if (!fs.existsSync(dir)) {
    return out;
  }
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      out.push(...collectYaml(full));
    } else if (entry.isFile() && /\.ya?ml$/.test(entry.name)) {
      out.push(full);
    }
  }
  return out;
}

describe("GitHub Actions expression policy (no empty ${{ }})", () => {
  const files = collectYaml(GITHUB_DIR).sort();

  test("at least the workflow/action YAML files are discovered (guard is not vacuous)", () => {
    expect(files.length).toBeGreaterThan(0);
  });

  test.each(files.map((file) => [path.relative(GITHUB_DIR, file), file]))(
    "%s contains no empty ${{ }} expression",
    (_rel, file) => {
      const content = fs.readFileSync(file, "utf8");
      const offenders = [];
      content.split(/\r\n|\r|\n/).forEach((line, index) => {
        if (EMPTY_EXPRESSION_REGEX.test(line)) {
          offenders.push(`${index + 1}: ${line.trim()}`);
        }
      });
      expect(offenders).toEqual([]);
      // Also catch an empty expression split across lines (`${{\n}}`): GitHub's
      // parser ignores newlines inside the braces, but the per-line scan above
      // cannot see it. `\s` already spans newlines, so a whole-file test closes
      // the gap.
      expect(EMPTY_EXPRESSION_REGEX.test(content)).toBe(false);
    }
  );
});
