#!/usr/bin/env node
// cspell:words xlink labelledby describedby onbegin onrepeat bbox

/**
 * validate-banner.js
 *
 * Enforces long-term quality of the DxMessaging banner SVG. Run from the
 * pre-commit hook and from CI to prevent regression of issues that took
 * eleven iterations to stabilise.
 *
 * The canonical banner lives at docs/images/DxMessaging-banner.svg. The
 * site/ copy is MkDocs build output (gitignored), so it is regenerated and
 * not validated here.
 *
 * Validations (each implemented as an independent helper, all errors
 * collected before exit):
 *
 *   Sync / drift
 *     1. The version-badge block matches scripts/sync-banner-version.ps1
 *     3. The feature-row test-count label matches the repository test count.
 *
 *   Hard requirements
 *     4. viewBox="0 0 800 200", width="800", height="200" on root <svg>.
 *     5. No external resources (no <image href=...>, no xlink:href to a
 *        URL, no @import in <style>, no remote font URLs).
 *     6. No JavaScript (no <script>, no on* event handlers).
 *     7. File size <= 12 KiB.
 *     8. ASCII-only source bytes.
 *
 *   Accessibility
 *     8. <title> and <desc> as direct children of root <svg>, both
 *        non-empty.
 *     9. role="img" on root <svg>.
 *    10. aria-labelledby/aria-describedby (if present) reference IDs that
 *        actually exist on <title>/<desc>.
 *
 *   Layout / encapsulation
 *    11. Stat badge rect widths fit their text plus 10px padding per side
 *        using monospace and emoji width heuristics.
 *    12. Feature row contains exactly Simple, Automatic, Dev-Friendly,
 *        and "<digits>+ Tests".
 *    13. All text/rect/line/polygon bounding boxes lie inside the viewBox
 *        (text widths approximated; text-anchor middle/end warns rather
 *        than errors).
 *
 *   Code quality
 *    14. XML well-formed (open/close balance, no stray angle brackets in
 *        attributes).
 *    15. No duplicate id="..." attributes.
 *    16. (warn) Every <defs> id is referenced via url(#...) somewhere
 *        outside the <defs> block.
 *
 *   Drift prevention
 *    17. Semver-shaped strings (vX.Y.Z) appear only inside the version
 *        badge text element.
 *    18. "<digits>+ Tests" appears only inside the feature row.
 *
 * Usage:
 *   node scripts/validate-banner.js
 *
 * Exit codes:
 *   0  All checks passed.
 *   1  One or more errors. Warnings alone do not fail the build.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..");
const PACKAGE_JSON_PATH = path.join(REPO_ROOT, "package.json");
const SYNC_SCRIPT_PATH = path.join(REPO_ROOT, "scripts", "sync-banner-version.ps1");
// SYNC: keep in lockstep with $svgPath in scripts/sync-banner-version.ps1.
const BANNER_PATH = path.join(REPO_ROOT, "docs", "images", "DxMessaging-banner.svg");
const BANNER_PATHS = [BANNER_PATH];

const MAX_BANNER_BYTES = 12 * 1024;
const VIEWBOX_W = 800;
const VIEWBOX_H = 200;
const BADGE_PADDING_PX = 10;
// Width-per-em heuristics (advance width / font-size).
const MONO_GLYPH_FRACTION = 0.6;
const EMOJI_GLYPH_FRACTION = 1.5;
const TEST_FILE_NAME_PATTERN = /(?:Test|Tests)\.cs$|\.(?:test|spec)\.js$/;
const TEST_ROOTS = ["Tests", "SourceGenerators", "scripts"];

// --- Tiny helpers ----------------------------------------------------------

function rel(p) {
  return path.relative(REPO_ROOT, p) || p;
}

function makeError(category, file, message, fix) {
  return { kind: "error", category, file, message, fix };
}

function makeWarn(category, file, message) {
  return { kind: "warn", category, file, message };
}

function readFileBytesOrNull(p) {
  try {
    return fs.readFileSync(p);
  } catch (err) {
    return null;
  }
}

function readFileTextOrNull(p) {
  const buf = readFileBytesOrNull(p);
  if (buf === null) return null;
  // SVGs and the PS script are LF UTF-8 in this repo. We do NOT normalise
  // here: the heredoc/SVG byte-equality check is sensitive to CRLF.
  return buf.toString("utf8");
}

// --- 1. Banner copies byte-identical ---------------------------------------

function checkBannersIdentical() {
  const errs = [];
  if (BANNER_PATHS.length < 2) return errs;
  const first = readFileBytesOrNull(BANNER_PATHS[0]);
  if (first === null) {
    errs.push(
      makeError(
        "missing-file",
        BANNER_PATHS[0],
        "banner file not found",
        "restore the banner from git history or regenerate it"
      )
    );
    return errs;
  }
  for (let i = 1; i < BANNER_PATHS.length; i++) {
    const other = readFileBytesOrNull(BANNER_PATHS[i]);
    if (other === null) {
      errs.push(
        makeError(
          "missing-file",
          BANNER_PATHS[i],
          "banner file not found",
          "restore the banner from git history or regenerate it"
        )
      );
      continue;
    }
    if (!first.equals(other)) {
      errs.push(
        makeError(
          "banner-drift",
          BANNER_PATHS[i],
          `differs from ${rel(BANNER_PATHS[0])} (${first.length} vs ${other.length} bytes)`,
          `cp ${rel(BANNER_PATHS[0])} ${rel(BANNER_PATHS[i])}`
        )
      );
    }
  }
  return errs;
}

// --- 2. Version-badge block matches the PS heredoc -------------------------

function extractHeredoc(psSource) {
  // Find: $newVersionText = @"\n...\n"@
  const startMarker = '$newVersionText = @"\n';
  const startIdx = psSource.indexOf(startMarker);
  if (startIdx === -1) return null;
  const contentStart = startIdx + startMarker.length;
  // The terminator must be a line whose first characters are "@.
  // PowerShell requires "@ to start at column 0; we emulate the same rule.
  const endMarker = '\n"@';
  const endIdx = psSource.indexOf(endMarker, contentStart);
  if (endIdx === -1) return null;
  return psSource.slice(contentStart, endIdx);
}

function checkVersionBadgeBlock(version, psSource) {
  const errs = [];
  if (psSource === null) {
    errs.push(
      makeError(
        "missing-file",
        SYNC_SCRIPT_PATH,
        "PowerShell sync script not found",
        "restore scripts/sync-banner-version.ps1 from git history"
      )
    );
    return errs;
  }
  const heredoc = extractHeredoc(psSource);
  if (heredoc === null) {
    errs.push(
      makeError(
        "ps-heredoc-malformed",
        SYNC_SCRIPT_PATH,
        'could not locate $newVersionText = @" ... "@ heredoc',
        "ensure the heredoc literal in sync-banner-version.ps1 is intact"
      )
    );
    return errs;
  }
  if (version === null) {
    // checkPackageVersion will already have errored.
    return errs;
  }
  const expected = heredoc.replace(/\$version/g, version);
  // Anchor on the comment marker that uniquely identifies the badge block.
  const startTag = "<!-- Version badge (top right)";
  for (const svgPath of BANNER_PATHS) {
    const svg = readFileTextOrNull(svgPath);
    if (svg === null) continue;
    const startIdx = svg.indexOf(startTag);
    if (startIdx === -1) {
      errs.push(
        makeError(
          "version-badge-missing",
          svgPath,
          `could not find "${startTag}" anchor`,
          "restore the version badge block from sync-banner-version.ps1"
        )
      );
      continue;
    }
    // The badge is comment + opening <g> + rect + text + closing </g>.
    // Find the FIRST </g> after the opening <g>.
    const gOpen = svg.indexOf("<g", startIdx);
    const gClose = svg.indexOf("</g>", gOpen);
    if (gOpen === -1 || gClose === -1) {
      errs.push(
        makeError(
          "version-badge-malformed",
          svgPath,
          "found version-badge comment but no <g>...</g> wrapper",
          "restore the version badge block from sync-banner-version.ps1"
        )
      );
      continue;
    }
    const actual = svg.slice(startIdx, gClose + "</g>".length);
    if (actual !== expected) {
      errs.push(
        makeError(
          "version-badge-drift",
          svgPath,
          "version-badge block does not match sync-banner-version.ps1 heredoc:\n" +
            buildSimpleDiff(expected, actual),
          "edit the SVG to match the heredoc, or run scripts/sync-banner-version.ps1"
        )
      );
    }
  }
  return errs;
}

function buildSimpleDiff(expected, actual) {
  const a = expected.split("\n");
  const b = actual.split("\n");
  const out = [];
  const max = Math.max(a.length, b.length);
  for (let i = 0; i < max; i++) {
    const ea = a[i];
    const eb = b[i];
    if (ea === eb) {
      out.push("    " + (ea !== undefined ? ea : ""));
    } else {
      if (ea !== undefined) out.push("  - " + ea);
      if (eb !== undefined) out.push("  + " + eb);
    }
  }
  return out.join("\n");
}

// --- 3. Banner version matches package.json --------------------------------

function checkPackageVersion(version, svgFile, svgText) {
  const errs = [];
  if (version === null) return errs;
  // Match the version-badge text element only.
  const badgeText = svgText.match(/<text[^>]*>v(\d+\.\d+\.\d+[^<]*)<\/text>\s*<\/g>/);
  if (badgeText === null) {
    errs.push(
      makeError(
        "version-not-found",
        svgFile,
        "could not locate version badge text element (expected vX.Y.Z)",
        "restore the version badge block"
      )
    );
    return errs;
  }
  if (badgeText[1] !== version) {
    errs.push(
      makeError(
        "version-mismatch",
        svgFile,
        `banner shows v${badgeText[1]} but package.json is ${version}`,
        "run scripts/sync-banner-version.ps1 (or scripts/sync-banner-version.sh)"
      )
    );
  }
  return errs;
}

// --- 4. Banner test-count label matches repository tests ------------------

function maskJavaScriptNonCode(content) {
  let result = "";
  let state = "code";
  let quote = "";
  let escaped = false;

  const mask = (char) => (char === "\n" || char === "\r" ? char : " ");

  for (let i = 0; i < content.length; ) {
    const char = content[i];
    const next = content[i + 1] ?? "";

    if (state === "code") {
      if (char === "/" && next === "/") {
        result += mask(char) + mask(next);
        i += 2;
        state = "line-comment";
        continue;
      }
      if (char === "/" && next === "*") {
        result += mask(char) + mask(next);
        i += 2;
        state = "block-comment";
        continue;
      }
      if (char === "'" || char === '"' || char === "`") {
        result += mask(char);
        quote = char;
        escaped = false;
        i++;
        state = "string";
        continue;
      }

      result += char;
      i++;
      continue;
    }

    if (state === "line-comment") {
      result += mask(char);
      i++;
      if (char === "\n" || char === "\r") {
        state = "code";
      }
      continue;
    }

    if (state === "block-comment") {
      result += mask(char);
      if (char === "*" && next === "/") {
        result += mask(next);
        i += 2;
        state = "code";
        continue;
      }
      i++;
      continue;
    }

    result += mask(char);
    i++;
    if (escaped) {
      escaped = false;
      continue;
    }
    if (char === "\\") {
      escaped = true;
      continue;
    }
    if (char === quote) {
      state = "code";
    }
  }

  return result;
}

function stripSourceComments(content) {
  return content.replace(/\/\*[\s\S]*?\*\//g, "").replace(/(^|[^:])\/\/.*$/gm, "$1");
}

function countTestMarkers(filePath, content) {
  if (filePath.endsWith(".cs")) {
    const source = stripSourceComments(content);
    return (source.match(/\[(?:UnityTest|Test|TestCase|TestCaseSource|Theory|Fact)\b/g) ?? [])
      .length;
  }
  if (/\.(?:test|spec)\.js$/.test(filePath)) {
    const source = maskJavaScriptNonCode(content);
    return (source.match(/(?<![\w.])(?:test|it)\s*\(/g) ?? []).length;
  }
  return 0;
}

function collectTestFiles(dir, results) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      collectTestFiles(fullPath, results);
      continue;
    }
    if (entry.isFile() && TEST_FILE_NAME_PATTERN.test(entry.name)) {
      results.push(fullPath);
    }
  }
}

function getRepositoryTestFiles(root) {
  const results = [];
  for (const relativeRoot of TEST_ROOTS) {
    const absoluteRoot = path.join(root, relativeRoot);
    if (fs.existsSync(absoluteRoot)) {
      collectTestFiles(absoluteRoot, results);
    }
  }
  return results;
}

function calculateRepositoryTestCount(root) {
  return getRepositoryTestFiles(root).reduce(
    (sum, filePath) => sum + countTestMarkers(filePath, fs.readFileSync(filePath, "utf8")),
    0
  );
}

function roundTestCount(testCount) {
  const rounded = Math.floor(testCount / 100) * 100;
  return rounded < 1 ? testCount : rounded;
}

function checkRepositoryTestCount(svgFile, svgText) {
  const errs = [];
  const labelMatch = svgText.match(
    /<text(?=[^>]*\bx="20")(?=[^>]*\by="13")(?=[^>]*\bfill="#00d9ff")[^>]*>(\d+\+ Tests)<\/text>/g
  );
  if (labelMatch === null) {
    // checkFeatureRow reports the missing label with more context.
    return errs;
  }
  const testLabel = labelMatch
    .map((label) => label.match(/>(\d+\+ Tests)<\/text>/))
    .filter(Boolean)
    .map((match) => match[1])
    .find((label) => /^\d+\+ Tests$/.test(label));
  if (testLabel === undefined) {
    return errs;
  }

  const repositoryTestCount = calculateRepositoryTestCount(REPO_ROOT);
  const expectedLabel = `${roundTestCount(repositoryTestCount)}+ Tests`;
  if (testLabel !== expectedLabel) {
    errs.push(
      makeError(
        "test-count-drift",
        svgFile,
        `banner shows "${testLabel}" but repository count is ${repositoryTestCount} (${expectedLabel})`,
        "run scripts/sync-banner-version.ps1 to refresh the feature-row test label"
      )
    );
  }
  return errs;
}

// --- 4. ViewBox is 0 0 800 200 with matching width/height ------------------

function checkViewBox(svgFile, svgText) {
  const errs = [];
  const rootMatch = svgText.match(/<svg\b([^>]*)>/);
  if (rootMatch === null) {
    errs.push(
      makeError(
        "no-root-svg",
        svgFile,
        "no <svg> root element found",
        "ensure the file begins with a well-formed <svg ...> tag"
      )
    );
    return errs;
  }
  const attrs = rootMatch[1];
  const viewBox = readAttr(attrs, "viewBox");
  const width = readAttr(attrs, "width");
  const height = readAttr(attrs, "height");
  const expectedViewBox = `0 0 ${VIEWBOX_W} ${VIEWBOX_H}`;
  if (viewBox !== expectedViewBox) {
    errs.push(
      makeError(
        "viewbox",
        svgFile,
        `viewBox is "${viewBox}", expected "${expectedViewBox}"`,
        `set viewBox="${expectedViewBox}" on the root <svg>`
      )
    );
  }
  if (width !== String(VIEWBOX_W)) {
    errs.push(
      makeError(
        "viewbox",
        svgFile,
        `width is "${width}", expected "${VIEWBOX_W}"`,
        `set width="${VIEWBOX_W}" on the root <svg>`
      )
    );
  }
  if (height !== String(VIEWBOX_H)) {
    errs.push(
      makeError(
        "viewbox",
        svgFile,
        `height is "${height}", expected "${VIEWBOX_H}"`,
        `set height="${VIEWBOX_H}" on the root <svg>`
      )
    );
  }
  return errs;
}

function readAttr(attrString, name) {
  const m = attrString.match(new RegExp(`\\b${name}\\s*=\\s*"([^"]*)"`));
  return m ? m[1] : null;
}

function readNumberAttr(attrString, name) {
  const value = readAttr(attrString, name);
  if (value === null) return NaN;
  return Number(value);
}

function parseTranslate(attrString) {
  const transform = readAttr(attrString, "transform");
  if (transform === null) return null;
  const m = transform.match(/^translate\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)$/);
  if (m === null) return null;
  return { x: Number(m[1]), y: Number(m[2]) };
}

// --- 5. No external resources ---------------------------------------------

function checkNoExternalResources(svgFile, svgText) {
  const errs = [];
  // <image href="..."> and <image xlink:href="..."> with a remote URL.
  const imageHref = svgText.match(/<image\b[^>]*\s(?:xlink:)?href\s*=\s*"([^"]+)"/i);
  if (imageHref !== null) {
    errs.push(
      makeError(
        "external-resource",
        svgFile,
        `<image> element references external resource "${imageHref[1]}"`,
        "remove <image> or inline the asset as data:"
      )
    );
  }
  // xlink:href / href on any element pointing to http(s):// or //.
  const externalRef = svgText.match(/\b(?:xlink:)?href\s*=\s*"((?:https?:)?\/\/[^"]+)"/i);
  if (externalRef !== null) {
    errs.push(
      makeError(
        "external-resource",
        svgFile,
        `external href "${externalRef[1]}" found`,
        "drop the remote reference; the banner must be self-contained"
      )
    );
  }
  // <style> with @import.
  const styleImport = svgText.match(/<style\b[^>]*>[\s\S]*?@import[\s\S]*?<\/style>/i);
  if (styleImport !== null) {
    errs.push(
      makeError(
        "external-resource",
        svgFile,
        "<style> contains @import",
        "remove @import; inline any required CSS"
      )
    );
  }
  // url() pointing at http(s) inside a style attribute or stylesheet.
  const remoteUrl = svgText.match(/url\(\s*["']?(https?:[^"')]+)/i);
  if (remoteUrl !== null) {
    errs.push(
      makeError(
        "external-resource",
        svgFile,
        `remote url() reference "${remoteUrl[1]}"`,
        "replace with an internal #id reference or inline the asset"
      )
    );
  }
  return errs;
}

// --- 6. No JavaScript ------------------------------------------------------

const ON_EVENT_ATTRS = [
  "onclick",
  "onload",
  "onmouseover",
  "onmouseout",
  "onmousedown",
  "onmouseup",
  "onmousemove",
  "onkeydown",
  "onkeyup",
  "onkeypress",
  "onfocus",
  "onblur",
  "onerror",
  "onactivate",
  "onbegin",
  "onend",
  "onrepeat",
  "onunload",
  "onresize",
  "onscroll",
  "onzoom"
];

function checkNoJavaScript(svgFile, svgText) {
  const errs = [];
  if (/<script\b/i.test(svgText)) {
    errs.push(
      makeError("javascript", svgFile, "<script> element present", "remove the <script> element")
    );
  }
  for (const evt of ON_EVENT_ATTRS) {
    const re = new RegExp(`\\b${evt}\\s*=\\s*"`, "i");
    if (re.test(svgText)) {
      errs.push(
        makeError(
          "javascript",
          svgFile,
          `event handler attribute ${evt} present`,
          `remove the ${evt} attribute`
        )
      );
    }
  }
  // javascript: URL scheme anywhere.
  if (/\bjavascript\s*:/i.test(svgText)) {
    errs.push(
      makeError("javascript", svgFile, 'literal "javascript:" found', "remove the javascript: URL")
    );
  }
  return errs;
}

// --- 7. File size ----------------------------------------------------------

function checkFileSize(svgFile, svgBytes) {
  const errs = [];
  if (svgBytes.length > MAX_BANNER_BYTES) {
    errs.push(
      makeError(
        "size",
        svgFile,
        `${svgBytes.length} bytes exceeds limit of ${MAX_BANNER_BYTES}`,
        "trim whitespace or simplify gradient/filter definitions"
      )
    );
  }
  return errs;
}

// --- 8. ASCII-only source --------------------------------------------------

function checkAsciiOnly(svgFile, svgBytes) {
  const errs = [];
  let line = 1;
  let column = 0;
  for (let i = 0; i < svgBytes.length; i++) {
    const b = svgBytes[i];
    if (b === 0x0a) {
      line++;
      column = 0;
      continue;
    }
    column++;
    if (b > 0x7f) {
      errs.push(
        makeError(
          "non-ascii",
          svgFile,
          `non-ASCII byte 0x${b.toString(16).padStart(2, "0")} at line ${line} column ${column}`,
          "replace with a numeric character reference (&#xNNNN;) or ASCII equivalent"
        )
      );
      return errs; // first occurrence is enough; the user fixes one at a time.
    }
  }
  return errs;
}

// --- 9. <title> and <desc> -------------------------------------------------

function checkTitleDesc(svgFile, svgText) {
  const errs = [];
  const titleMatch = svgText.match(/<title\b[^>]*>([\s\S]*?)<\/title>/);
  const descMatch = svgText.match(/<desc\b[^>]*>([\s\S]*?)<\/desc>/);
  if (titleMatch === null || titleMatch[1].trim() === "") {
    errs.push(
      makeError(
        "a11y",
        svgFile,
        "missing or empty <title> element",
        'add <title id="...">DxMessaging: ...</title> as a direct child of <svg>'
      )
    );
  }
  if (descMatch === null || descMatch[1].trim() === "") {
    errs.push(
      makeError(
        "a11y",
        svgFile,
        "missing or empty <desc> element",
        'add <desc id="...">...</desc> as a direct child of <svg>'
      )
    );
  }
  return errs;
}

// --- 10. role="img" --------------------------------------------------------

function checkRoleImg(svgFile, svgText) {
  const errs = [];
  const rootMatch = svgText.match(/<svg\b([^>]*)>/);
  if (rootMatch === null) return errs;
  const role = readAttr(rootMatch[1], "role");
  if (role !== "img") {
    errs.push(
      makeError(
        "a11y",
        svgFile,
        `root <svg> role is "${role ?? "(unset)"}", expected "img"`,
        'add role="img" to the root <svg> element'
      )
    );
  }
  return errs;
}

// --- 11. aria-labelledby/aria-describedby refs ----------------------------

function checkAriaRefs(svgFile, svgText) {
  const errs = [];
  const rootMatch = svgText.match(/<svg\b([^>]*)>/);
  if (rootMatch === null) return errs;
  const attrs = rootMatch[1];
  const labelledBy = readAttr(attrs, "aria-labelledby");
  const describedBy = readAttr(attrs, "aria-describedby");
  if (labelledBy !== null) {
    const titleId = matchAttr(svgText, /<title\b([^>]*)>/, "id");
    if (titleId !== labelledBy) {
      errs.push(
        makeError(
          "a11y",
          svgFile,
          `aria-labelledby="${labelledBy}" does not match <title id="${titleId ?? "(unset)"}">`,
          `set <title id="${labelledBy}"> or remove aria-labelledby`
        )
      );
    }
  }
  if (describedBy !== null) {
    const descId = matchAttr(svgText, /<desc\b([^>]*)>/, "id");
    if (descId !== describedBy) {
      errs.push(
        makeError(
          "a11y",
          svgFile,
          `aria-describedby="${describedBy}" does not match <desc id="${descId ?? "(unset)"}">`,
          `set <desc id="${describedBy}"> or remove aria-describedby`
        )
      );
    }
  }
  return errs;
}

function matchAttr(text, openTagRegex, attr) {
  const m = text.match(openTagRegex);
  if (m === null) return null;
  return readAttr(m[1], attr);
}

// --- 12. Stat badge encapsulation -----------------------------------------

// Returns the visible character count for a tspan body, treating numeric
// character references (&#x...; / &#NNN;) as ONE glyph and excluding the
// VS-16 selector U+FE0F (zero-width).
function visibleGlyphCount(literalText) {
  const stripped = literalText
    .replace(/&#[xX][0-9a-fA-F]+;/g, (match) => {
      // U+FE0F is &#xFE0F; - count zero.
      const cp = parseInt(match.slice(3, -1), 16);
      return cp === 0xfe0f ? "" : "X";
    })
    .replace(/&#\d+;/g, (match) => {
      const cp = parseInt(match.slice(2, -1), 10);
      return cp === 0xfe0f ? "" : "X";
    })
    .replace(/&[a-zA-Z]+;/g, "X");
  return stripped.length;
}

function badgeTextWidth(textElement, defaultFontSize) {
  // Sum the contributions of each <tspan>. Outside-tspan text (rare) is
  // treated under the default font-family (monospace).
  const tspanRe = /<tspan\b([^>]*)>([\s\S]*?)<\/tspan>/g;
  let total = 0;
  let consumed = "";
  let m;
  while ((m = tspanRe.exec(textElement)) !== null) {
    const attrs = m[1];
    const body = m[2];
    consumed += m[0];
    const fontFamily = readAttr(attrs, "font-family") ?? "";
    const isEmoji = /Color Emoji|Emoji/i.test(fontFamily);
    const glyphs = visibleGlyphCount(body);
    const fraction = isEmoji ? EMOJI_GLYPH_FRACTION : MONO_GLYPH_FRACTION;
    total += glyphs * defaultFontSize * fraction;
  }
  // Account for any text outside of tspans (defensive).
  const inner = textElement.replace(/<[^>]+>/g, "");
  const consumedInner = consumed.replace(/<[^>]+>/g, "");
  if (inner.length > consumedInner.length) {
    const extra = inner.slice(consumedInner.length);
    total += visibleGlyphCount(extra) * defaultFontSize * MONO_GLYPH_FRACTION;
  }
  return total;
}

function checkStatBadges(svgFile, svgText) {
  const errs = [];
  // Locate the badge column group: <g transform="translate(556, 85)" ... font-size="11">
  const columnRe =
    /<g\s+transform="translate\(\s*\d+\s*,\s*\d+\s*\)"\s+font-family="'SF Mono'[^"]*"\s+font-size="(\d+)"\s*>([\s\S]*?)<\/g>\s*<!--\s*Version badge/;
  const colMatch = svgText.match(columnRe);
  if (colMatch === null) {
    errs.push(
      makeError(
        "stat-badges",
        svgFile,
        "could not locate stat-badge column (expected SF Mono <g> followed by version badge comment)",
        "restore the stat-badge column"
      )
    );
    return errs;
  }
  const fontSize = Number(colMatch[1]);
  const columnInner = colMatch[2];
  const badgeRe =
    /<g\s+transform="translate\(\s*\d+\s*,\s*\d+\s*\)"\s*>\s*<rect\b([^>]*)\/>\s*(<text\b[\s\S]*?<\/text>)\s*<\/g>/g;
  let count = 0;
  let m;
  while ((m = badgeRe.exec(columnInner)) !== null) {
    count++;
    const rectAttrs = m[1];
    const textElement = m[2];
    const widthAttr = readAttr(rectAttrs, "width");
    const rectWidth = widthAttr !== null ? Number(widthAttr) : NaN;
    const textWidth = badgeTextWidth(textElement, fontSize);
    const minWidth = textWidth + 2 * BADGE_PADDING_PX;
    if (!Number.isFinite(rectWidth) || rectWidth < minWidth) {
      const labelMatch = textElement.match(/<tspan[^>]*>([^<]*)<\/tspan>\s*<\/text>/);
      const label = labelMatch ? labelMatch[1] : `badge #${count}`;
      errs.push(
        makeError(
          "stat-badges",
          svgFile,
          `badge "${label}" rect width=${rectWidth} < estimated text width ${textWidth.toFixed(1)} + ${2 * BADGE_PADDING_PX}px padding (= ${minWidth.toFixed(1)})`,
          `widen the rect width to at least ${Math.ceil(minWidth)}`
        )
      );
    }
  }
  if (count !== 3) {
    errs.push(
      makeError(
        "stat-badges",
        svgFile,
        `expected 3 stat badges in the column, found ${count}`,
        "ensure Unity / Zero Alloc / High Perf badges are all present"
      )
    );
  }
  return errs;
}

// --- 13. Feature row labels ------------------------------------------------

function checkFeatureRow(svgFile, svgText) {
  const errs = [];
  const rowRe =
    /<g\s+transform="translate\(120,\s*150\)"[\s\S]*?>([\s\S]*?)<\/g>\s*\n\s*<!--\s*Right-side capability pill badges/;
  const rowMatch = svgText.match(rowRe);
  if (rowMatch === null) {
    errs.push(
      makeError(
        "feature-row",
        svgFile,
        "could not locate feature row group (translate(120, 150))",
        "restore the feature row group"
      )
    );
    return errs;
  }
  const inner = rowMatch[1];
  // The label (not the emoji) is in a <text> with fill="#00d9ff".
  const labelRe =
    /<text(?=[^>]*\bx="20")(?=[^>]*\by="13")(?=[^>]*\bfill="#00d9ff")[^>]*>([^<]+)<\/text>/g;
  const labels = [];
  let m;
  while ((m = labelRe.exec(inner)) !== null) {
    labels.push(m[1]);
  }
  const expected = ["Simple", "Automatic", "Dev-Friendly"];
  if (labels.length !== 4) {
    errs.push(
      makeError(
        "feature-row",
        svgFile,
        `feature row has ${labels.length} labels, expected 4 (Simple, Automatic, Dev-Friendly, "<n>+ Tests")`,
        "restore the four feature labels"
      )
    );
    return errs;
  }
  for (let i = 0; i < expected.length; i++) {
    if (labels[i] !== expected[i]) {
      errs.push(
        makeError(
          "feature-row",
          svgFile,
          `feature row label ${i + 1} is "${labels[i]}", expected "${expected[i]}"`,
          `change the label to "${expected[i]}"`
        )
      );
    }
  }
  if (!/^\d+\+ Tests$/.test(labels[3])) {
    errs.push(
      makeError(
        "feature-row",
        svgFile,
        `feature row label 4 is "${labels[3]}", expected something matching /^\\d+\\+ Tests$/`,
        'change the label to e.g. "300+ Tests"'
      )
    );
  }
  return errs;
}

// --- 14. All elements within viewBox --------------------------------------

function checkBoundingBoxes(svgFile, svgText) {
  const errs = [];
  // We deliberately keep this lightweight: we walk top-level <rect> and
  // <line> elements (the geometric primitives we actually use) and confirm
  // their coordinates lie in the viewBox. Text is approximated.
  // We do NOT traverse nested <g transform=...> coordinate systems; rects
  // inside transforms are validated indirectly by the badge checks (12)
  // which already enforce widths.
  // NOTE: the background rect <rect x="0" y="0" width="800" height="200">
  // touches the right/bottom edges; "within" means <= bounds.
  const rectRe = /<rect\b([^>]*)\/>/g;
  let m;
  while ((m = rectRe.exec(svgText)) !== null) {
    const a = m[1];
    // Skip rects nested inside <g transform> wrappers - those have local
    // coords. We detect this by checking if x and y look like absolute
    // coords (i.e., x>=0 and clearly absolute background). A reliable
    // proxy: only complain if x="0" y="0" and width or height exceeds
    // viewBox.
    const x = Number(readAttr(a, "x"));
    const y = Number(readAttr(a, "y"));
    const w = Number(readAttr(a, "width"));
    const h = Number(readAttr(a, "height"));
    if (x === 0 && y === 0 && Number.isFinite(w) && Number.isFinite(h)) {
      if (w > VIEWBOX_W || h > VIEWBOX_H) {
        errs.push(
          makeError(
            "bbox",
            svgFile,
            `background rect ${w}x${h} exceeds viewBox ${VIEWBOX_W}x${VIEWBOX_H}`,
            `clamp width/height to <= ${VIEWBOX_W}x${VIEWBOX_H}`
          )
        );
      }
    }
  }
  // Top-level <line> coords are in viewBox space.
  const lineRe = /<line\b([^>]*)\/>/g;
  while ((m = lineRe.exec(svgText)) !== null) {
    const a = m[1];
    const coords = ["x1", "y1", "x2", "y2"].map((k) => Number(readAttr(a, k)));
    const [x1, y1, x2, y2] = coords;
    for (const [name, val, max] of [
      ["x1", x1, VIEWBOX_W],
      ["x2", x2, VIEWBOX_W],
      ["y1", y1, VIEWBOX_H],
      ["y2", y2, VIEWBOX_H]
    ]) {
      if (Number.isFinite(val) && (val < 0 || val > max)) {
        errs.push(
          makeError(
            "bbox",
            svgFile,
            `<line> ${name}=${val} outside [0, ${max}]`,
            `move the line endpoint inside the viewBox`
          )
        );
      }
    }
  }
  return errs;
}

function checkRectInsideViewBox(svgFile, label, x, y, width, height) {
  const errs = [];
  if (
    !Number.isFinite(x) ||
    !Number.isFinite(y) ||
    !Number.isFinite(width) ||
    !Number.isFinite(height)
  ) {
    errs.push(
      makeError(
        "bbox",
        svgFile,
        `${label} has non-numeric rect bounds`,
        "use numeric x/y/width/height values for transformed badge rects"
      )
    );
    return errs;
  }
  const right = x + width;
  const bottom = y + height;
  if (x < 0 || y < 0 || right > VIEWBOX_W || bottom > VIEWBOX_H) {
    errs.push(
      makeError(
        "bbox",
        svgFile,
        `${label} rect bounds (${x}, ${y}, ${right}, ${bottom}) exceed viewBox 0 0 ${VIEWBOX_W} ${VIEWBOX_H}`,
        "move the transformed badge/version rect inside the viewBox"
      )
    );
  }
  return errs;
}

function checkTransformedBadgeLayout(svgFile, svgText) {
  const errs = [];
  const rightColumnRe =
    /<!--\s*Right-side capability pill badges[\s\S]*?-->\s*<g\b([^>]*)>([\s\S]*?)<\/g>\s*<!--\s*Version badge/;
  const rightColumnMatch = svgText.match(rightColumnRe);
  if (rightColumnMatch !== null) {
    const outerTranslate = parseTranslate(rightColumnMatch[1]);
    if (outerTranslate === null) {
      errs.push(
        makeError(
          "bbox",
          svgFile,
          "right badge column is missing a numeric translate(x, y) transform",
          "restore the right badge column transform"
        )
      );
    } else {
      const badgeRe = /<g\b([^>]*)>\s*<rect\b([^>]*)\/>\s*<text\b[\s\S]*?<\/text>\s*<\/g>/g;
      let badgeIndex = 0;
      let m;
      while ((m = badgeRe.exec(rightColumnMatch[2])) !== null) {
        badgeIndex++;
        const innerTranslate = parseTranslate(m[1]);
        if (innerTranslate === null) {
          errs.push(
            makeError(
              "bbox",
              svgFile,
              `right badge #${badgeIndex} is missing a numeric translate(x, y) transform`,
              "restore the nested badge transform"
            )
          );
          continue;
        }
        const rectAttrs = m[2];
        errs.push(
          ...checkRectInsideViewBox(
            svgFile,
            `right badge #${badgeIndex}`,
            outerTranslate.x + innerTranslate.x + readNumberAttr(rectAttrs, "x"),
            outerTranslate.y + innerTranslate.y + readNumberAttr(rectAttrs, "y"),
            readNumberAttr(rectAttrs, "width"),
            readNumberAttr(rectAttrs, "height")
          )
        );
      }
    }
  }

  const versionRe =
    /<!--\s*Version badge \(top right\)[\s\S]*?-->\s*<g\b([^>]*)>\s*<rect\b([^>]*)\/>/;
  const versionMatch = svgText.match(versionRe);
  if (versionMatch !== null) {
    const translate = parseTranslate(versionMatch[1]);
    if (translate === null) {
      errs.push(
        makeError(
          "bbox",
          svgFile,
          "version badge is missing a numeric translate(x, y) transform",
          "restore the version badge transform"
        )
      );
    } else {
      const rectAttrs = versionMatch[2];
      errs.push(
        ...checkRectInsideViewBox(
          svgFile,
          "version badge",
          translate.x + readNumberAttr(rectAttrs, "x"),
          translate.y + readNumberAttr(rectAttrs, "y"),
          readNumberAttr(rectAttrs, "width"),
          readNumberAttr(rectAttrs, "height")
        )
      );
    }
  }

  return errs;
}

// --- 15. XML well-formed --------------------------------------------------

function checkXmlWellFormed(svgFile, svgText) {
  const errs = [];
  // Strip comments (which legally contain unescaped < > as long as no -->).
  const stripped = svgText.replace(/<!--[\s\S]*?-->/g, "");
  // Walk tags. We accept self-closing (/>), CDATA-free content.
  const tagRe = /<(\/?)([A-Za-z][A-Za-z0-9-]*)\b([^>]*)>/g;
  const stack = [];
  let m;
  while ((m = tagRe.exec(stripped)) !== null) {
    const isClose = m[1] === "/";
    const name = m[2];
    const attrs = m[3];
    const selfClose = /\/\s*$/.test(attrs);
    if (isClose) {
      if (stack.length === 0 || stack[stack.length - 1] !== name) {
        errs.push(
          makeError(
            "xml",
            svgFile,
            `unbalanced close tag </${name}> (stack top: ${stack[stack.length - 1] ?? "(empty)"})`,
            "fix the mismatched tag"
          )
        );
        return errs;
      }
      stack.pop();
    } else if (!selfClose) {
      stack.push(name);
    }
  }
  if (stack.length !== 0) {
    errs.push(
      makeError(
        "xml",
        svgFile,
        `unclosed tags at EOF: ${stack.join(", ")}`,
        "add the missing closing tags"
      )
    );
  }
  return errs;
}

// --- 16. No duplicate IDs --------------------------------------------------

function checkUniqueIds(svgFile, svgText) {
  const errs = [];
  const ids = new Map();
  const re = /\bid\s*=\s*"([^"]+)"/g;
  let m;
  while ((m = re.exec(svgText)) !== null) {
    const id = m[1];
    const count = (ids.get(id) ?? 0) + 1;
    ids.set(id, count);
  }
  for (const [id, count] of ids) {
    if (count > 1) {
      errs.push(
        makeError(
          "duplicate-id",
          svgFile,
          `id="${id}" used ${count} times`,
          `rename the duplicate id="${id}" attributes to be unique`
        )
      );
    }
  }
  return errs;
}

// --- 17. Unused defs (warn only) ------------------------------------------

function checkUnusedDefs(svgFile, svgText) {
  const warns = [];
  const defsMatch = svgText.match(/<defs\b[^>]*>([\s\S]*?)<\/defs>/);
  if (defsMatch === null) return warns;
  const defsBody = defsMatch[1];
  const outsideDefs =
    svgText.slice(0, defsMatch.index) + svgText.slice(defsMatch.index + defsMatch[0].length);
  const idRe = /\bid\s*=\s*"([^"]+)"/g;
  let m;
  while ((m = idRe.exec(defsBody)) !== null) {
    const id = m[1];
    const ref = new RegExp(`url\\(#${escapeRegex(id)}\\)`);
    if (!ref.test(outsideDefs)) {
      warns.push(
        makeWarn("unused-defs", svgFile, `<defs> id="${id}" is never referenced via url(#${id})`)
      );
    }
  }
  return warns;
}

function escapeRegex(s) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

// --- 18. Hardcoded version outside the version badge ----------------------

function checkVersionUniqueness(svgFile, svgText) {
  const errs = [];
  const semverRe = /v\d+\.\d+\.\d+/g;
  const matches = [];
  let m;
  while ((m = semverRe.exec(svgText)) !== null) {
    matches.push({ index: m.index, text: m[0] });
  }
  // Locate the version-badge text element bounds.
  const badgeStart = svgText.indexOf("<!-- Version badge (top right)");
  let badgeEnd = -1;
  if (badgeStart !== -1) {
    const closing = svgText.indexOf("</g>", badgeStart);
    if (closing !== -1) badgeEnd = closing + "</g>".length;
  }
  for (const occ of matches) {
    if (badgeStart !== -1 && badgeEnd !== -1 && occ.index >= badgeStart && occ.index < badgeEnd) {
      continue;
    }
    // The "vX.Y.Z" comment in the version badge IS allowed but appears
    // inside the badge region; anything outside is a violation.
    errs.push(
      makeError(
        "drift",
        svgFile,
        `semver-shaped string "${occ.text}" appears outside the version badge (offset ${occ.index})`,
        "move the version reference into the version-badge text or remove it"
      )
    );
  }
  return errs;
}

// --- 19. Hardcoded test count outside the feature row ---------------------

function checkTestCountUniqueness(svgFile, svgText) {
  const errs = [];
  const re = /\d+\+\s*(?:Tests|tests)/g;
  const matches = [];
  let m;
  while ((m = re.exec(svgText)) !== null) {
    matches.push({ index: m.index, text: m[0] });
  }
  const rowStart = svgText.indexOf("translate(120, 150)");
  let rowEnd = -1;
  if (rowStart !== -1) {
    // Walk forward to the matching </g> at the same nesting level. We
    // already established the row exists; count matching <g>...</g>.
    let depth = 1;
    let i = svgText.indexOf(">", rowStart) + 1;
    while (i < svgText.length && depth > 0) {
      const open = svgText.indexOf("<g", i);
      const close = svgText.indexOf("</g>", i);
      if (close === -1) break;
      if (open !== -1 && open < close) {
        depth++;
        i = open + 2;
      } else {
        depth--;
        i = close + 4;
        if (depth === 0) {
          rowEnd = i;
          break;
        }
      }
    }
  }
  for (const occ of matches) {
    if (rowStart !== -1 && rowEnd !== -1 && occ.index >= rowStart && occ.index < rowEnd) {
      continue;
    }
    errs.push(
      makeError(
        "drift",
        svgFile,
        `test-count phrase "${occ.text}" appears outside the feature row (offset ${occ.index})`,
        "move the test count into the feature-row label or remove it"
      )
    );
  }
  return errs;
}

// --- Driver ----------------------------------------------------------------

function loadPackageVersion() {
  const text = readFileTextOrNull(PACKAGE_JSON_PATH);
  if (text === null) {
    return {
      version: null,
      errors: [
        makeError(
          "missing-file",
          PACKAGE_JSON_PATH,
          "package.json not found",
          "ensure the script is run from the repository root"
        )
      ]
    };
  }
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch (err) {
    return {
      version: null,
      errors: [
        makeError(
          "package-json",
          PACKAGE_JSON_PATH,
          `failed to parse: ${err.message}`,
          "repair the JSON syntax"
        )
      ]
    };
  }
  if (typeof parsed.version !== "string" || parsed.version === "") {
    return {
      version: null,
      errors: [
        makeError(
          "package-json",
          PACKAGE_JSON_PATH,
          'missing "version" field',
          'add a "version" field in semver form'
        )
      ]
    };
  }
  return { version: parsed.version, errors: [] };
}

function formatFinding(f) {
  const tag = f.kind === "error" ? "[ERROR]" : "[WARN ]";
  const lines = [`${tag} ${f.category}: ${f.message}`, `    file: ${rel(f.file)}`];
  if (f.kind === "error" && f.fix) lines.push(`    fix:  ${f.fix}`);
  return lines.join("\n");
}

function main() {
  const errors = [];
  const warnings = [];

  // Banner-copy parity is the highest-value check; run first so the user
  // sees it before any per-file noise.
  errors.push(...checkBannersIdentical());

  const { version, errors: pkgErrors } = loadPackageVersion();
  errors.push(...pkgErrors);

  const psSource = readFileTextOrNull(SYNC_SCRIPT_PATH);
  errors.push(...checkVersionBadgeBlock(version, psSource));

  for (const svgPath of BANNER_PATHS) {
    const bytes = readFileBytesOrNull(svgPath);
    if (bytes === null) {
      // Already reported by checkBannersIdentical.
      continue;
    }
    const text = bytes.toString("utf8");
    errors.push(...checkPackageVersion(version, svgPath, text));
    errors.push(...checkViewBox(svgPath, text));
    errors.push(...checkNoExternalResources(svgPath, text));
    errors.push(...checkNoJavaScript(svgPath, text));
    errors.push(...checkFileSize(svgPath, bytes));
    errors.push(...checkAsciiOnly(svgPath, bytes));
    errors.push(...checkTitleDesc(svgPath, text));
    errors.push(...checkRoleImg(svgPath, text));
    errors.push(...checkAriaRefs(svgPath, text));
    errors.push(...checkStatBadges(svgPath, text));
    errors.push(...checkFeatureRow(svgPath, text));
    errors.push(...checkRepositoryTestCount(svgPath, text));
    errors.push(...checkBoundingBoxes(svgPath, text));
    errors.push(...checkTransformedBadgeLayout(svgPath, text));
    errors.push(...checkXmlWellFormed(svgPath, text));
    errors.push(...checkUniqueIds(svgPath, text));
    warnings.push(...checkUnusedDefs(svgPath, text));
    errors.push(...checkVersionUniqueness(svgPath, text));
    errors.push(...checkTestCountUniqueness(svgPath, text));
  }

  for (const w of warnings) {
    process.stderr.write(formatFinding(w) + "\n");
  }
  for (const e of errors) {
    process.stderr.write(formatFinding(e) + "\n");
  }

  const summary = `Banner validation: ${errors.length} error(s), ${warnings.length} warning(s)`;
  if (errors.length === 0) {
    process.stdout.write(`OK  ${summary}\n`);
    return 0;
  }
  process.stderr.write(`\n${summary}\n`);
  return 1;
}

if (require.main === module) {
  process.exit(main());
}

module.exports = {
  extractHeredoc,
  visibleGlyphCount,
  badgeTextWidth
};
