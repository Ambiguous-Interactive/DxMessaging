#!/usr/bin/env node
"use strict";

/**
 * Regenerate the derivable parts of `.llm/skills/index.md`: the `Lines` column
 * (canonical `wc -l`), the Summary "Total Skills"/"Categories" values, and the
 * Table-of-Contents `(N)` counts. All other columns are hand-authored and kept
 * verbatim. Markers: `[draft]` < 120, `[ok]` 120-260, `[warn]` > 260.
 *
 *   node scripts/generate-skills-index.js [--check]   (--check gates validate:all)
 *
 * Run via `npm run update:skills-index` (chains prettier to align). A row<->file
 * mismatch is fatal in both modes (write cannot author a new row's metadata).
 */

const fs = require("fs");
const path = require("path");
const { normalizeToLf } = require("./lib/line-endings");
const { isPathOutsideDirectory } = require("./lib/path-classifier");
const { walkFiles } = require("./lib/repo-files");

const ROOT_DIR = path.resolve(__dirname, "..");
const SKILLS_DIR = path.join(ROOT_DIR, ".llm", "skills");
const INDEX_PATH = path.join(SKILLS_DIR, "index.md");

// Non-skill entries under .llm/skills (kept in sync with update-llms-txt.js).
const NON_SKILL_FILES = new Set(["index.md", "specification.md"]);
const NON_SKILL_DIRECTORIES = new Set(["templates"]);

const LINK_PATTERN = /\]\((\.\/[^)]+\.md)\)/; // the (./category/file.md) target
const MARKER_PATTERN = /\[(ok|warn|draft)\]\s+(\d+)/; // the Lines cell body

/** `wc -l`-equivalent line count: the number of newline bytes. */
function countLines(content) {
  return normalizeToLf(content).split("\n").length - 1;
}

/** Page-size status marker derived from a line count. */
function statusMarker(lineCount) {
  return lineCount < 120 ? "draft" : lineCount > 260 ? "warn" : "ok";
}

/** Slugify a heading the way GitHub anchors do ("GitHub Actions" -> "github-actions"). */
function slugify(heading) {
  return heading
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9 -]/g, "")
    .replace(/\s+/g, "-");
}

function isCountedSkillPath(fullPath, baseDir) {
  const rel = path.relative(baseDir, fullPath).split(path.sep).join("/");
  if (!rel || isPathOutsideDirectory(fullPath, baseDir)) {
    return false;
  }
  const segments = rel.split("/");
  if (!segments[segments.length - 1].endsWith(".md")) {
    return false;
  }
  if (NON_SKILL_FILES.has(segments[segments.length - 1])) {
    return false;
  }
  return !segments.some((segment) => NON_SKILL_DIRECTORIES.has(segment));
}

/** Enumerate catalogued skill files as { link, category, lineCount }, link-sorted. */
function enumerateSkillFiles(skillsDir = SKILLS_DIR) {
  if (!fs.existsSync(skillsDir)) {
    return [];
  }
  return walkFiles(skillsDir, {
    excludeDir: (fullPath, entry) => NON_SKILL_DIRECTORIES.has(entry.name),
    match: (fullPath) => isCountedSkillPath(fullPath, skillsDir)
  })
    .map((fullPath) => {
      const rel = path.relative(skillsDir, fullPath).split(path.sep).join("/");
      return { link: `./${rel}`, category: rel.split("/")[0], lineCount: countLines(fs.readFileSync(fullPath, "utf8")) };
    })
    .sort((a, b) => a.link.localeCompare(b.link));
}

/** Parse the index into { rows, sectionCounts, summary }. Rows are link-anchored. */
function parseIndex(indexText) {
  const rows = [];
  const tocCounts = new Map(); // the literal "(N)" beside each Table-of-Contents entry
  const summary = { totalSkills: null, categoryCount: null };
  let section = null;

  for (const line of normalizeToLf(indexText).split("\n")) {
    const heading = /^##\s+(.+?)\s*$/.exec(line);
    if (heading) {
      const slug = slugify(heading[1]);
      section = slug === "summary" || slug === "table-of-contents" ? null : slug;
      continue;
    }
    const tocEntry = /^-\s*\[[^\]]*\]\(#([a-z0-9-]+)\)\s*\((\d+)\)\s*$/.exec(line);
    if (tocEntry) {
      tocCounts.set(tocEntry[1], parseInt(tocEntry[2], 10));
      continue;
    }
    const summaryRow = /^\|\s*(Total Skills|Categories)\s*\|\s*(\d+)\s*\|\s*$/.exec(line);
    if (summaryRow) {
      summary[summaryRow[1] === "Total Skills" ? "totalSkills" : "categoryCount"] = parseInt(summaryRow[2], 10);
      continue;
    }
    if (!section || !/^\s*\|/.test(line)) {
      continue;
    }
    // Anchor the marker search AFTER the link so a "[ok] 5" inside a Skill title
    // is never mistaken for the Lines cell.
    const link = LINK_PATTERN.exec(line);
    if (link) {
      const marker = MARKER_PATTERN.exec(line.slice(link.index + link[0].length));
      if (marker) {
        rows.push({ link: link[1], marker: marker[1], count: parseInt(marker[2], 10), section });
      }
    }
  }
  return { rows, tocCounts, summary };
}

/** Disk-derived expectation: per-file counts, per-category counts, totals. */
function buildExpectedModel(skillFiles) {
  const lineCounts = new Map();
  const sectionCounts = new Map();
  for (const file of skillFiles) {
    lineCounts.set(file.link, file.lineCount);
    sectionCounts.set(file.category, (sectionCounts.get(file.category) ?? 0) + 1);
  }
  return { lineCounts, sectionCounts, totalSkills: skillFiles.length, categoryCount: sectionCounts.size };
}

/** Fatal structural problems: missing/orphan/duplicate/mis-sectioned rows. */
function findBijectionErrors(skillFiles, parsed) {
  const fileLinks = new Set(skillFiles.map((file) => file.link));
  const categoryOf = (link) => link.replace("./", "").split("/")[0];
  const errors = [];
  const seen = new Set();

  for (const row of parsed.rows) {
    if (!fileLinks.has(row.link)) {
      errors.push(`index row points at a missing skill file: ${row.link}`);
    } else if (row.section !== categoryOf(row.link)) {
      errors.push(`index row filed under section "${row.section}" but belongs to "${categoryOf(row.link)}": ${row.link}`);
    }
    if (seen.has(row.link)) {
      errors.push(`duplicate index row for: ${row.link}`);
    }
    seen.add(row.link);
  }
  for (const file of skillFiles) {
    if (!seen.has(file.link)) {
      errors.push(`skill file has no index row: ${file.link}`);
    }
  }
  return errors.sort();
}

/** Count/marker/summary/TOC drifts between the index and disk. Pure. */
function findDrift(parsed, expected) {
  const drifts = [];
  for (const row of parsed.rows) {
    const count = expected.lineCounts.get(row.link);
    if (count === undefined) {
      continue;
    }
    const marker = statusMarker(count);
    if (row.count !== count || row.marker !== marker) {
      drifts.push(`${row.link}: index [${row.marker}] ${row.count} -> [${marker}] ${count}`);
    }
  }
  if (parsed.summary.totalSkills !== expected.totalSkills) {
    drifts.push(`Summary "Total Skills": ${parsed.summary.totalSkills} -> ${expected.totalSkills}`);
  }
  if (parsed.summary.categoryCount !== expected.categoryCount) {
    drifts.push(`Summary "Categories": ${parsed.summary.categoryCount} -> ${expected.categoryCount}`);
  }
  // Compare the literal Table-of-Contents "(N)" against disk in both directions:
  // a stale number, a missing entry, and an orphan entry all drift.
  for (const slug of new Set([...expected.sectionCounts.keys(), ...parsed.tocCounts.keys()])) {
    const want = expected.sectionCounts.get(slug) ?? 0;
    const have = parsed.tocCounts.get(slug) ?? 0;
    if (have !== want) {
      drifts.push(`Table of Contents "${slug}": (${have}) -> (${want})`);
    }
  }
  return drifts;
}

/**
 * Rewrite the derivable cells in place. Does NOT re-align tables -- the chained
 * `prettier --write` owns alignment, so this is a byte-for-byte no-op on an
 * already-correct file (idempotent).
 */
function renderIndex(indexText, expected) {
  const eol = indexText.includes("\r\n") ? "\r\n" : "\n";
  let section = null;

  const lines = normalizeToLf(indexText)
    .split("\n")
    .map((line) => {
      const heading = /^##\s+(.+?)\s*$/.exec(line);
      if (heading) {
        const slug = slugify(heading[1]);
        section = slug === "summary" || slug === "table-of-contents" ? null : slug;
        return line;
      }
      const summaryRow = /^(\|\s*)(Total Skills|Categories)(\s*\|\s*)\d+(\s*\|\s*)$/.exec(line);
      if (summaryRow) {
        const value = summaryRow[2] === "Total Skills" ? expected.totalSkills : expected.categoryCount;
        return `${summaryRow[1]}${summaryRow[2]}${summaryRow[3]}${value}${summaryRow[4]}`;
      }
      const toc = /^(-\s*\[[^\]]*\]\(#([a-z0-9-]+)\)\s*\()\d+(\)\s*)$/.exec(line);
      if (toc) {
        const count = expected.sectionCounts.get(toc[2]);
        return count === undefined ? line : `${toc[1]}${count}${toc[3]}`;
      }
      if (section) {
        const link = LINK_PATTERN.exec(line);
        if (link && expected.lineCounts.has(link[1])) {
          const count = expected.lineCounts.get(link[1]);
          // Replace the marker only in the slice AFTER the link, so a "[ok] 5"
          // inside the Skill title is never the substitution target.
          const at = link.index + link[0].length;
          return line.slice(0, at) + line.slice(at).replace(MARKER_PATTERN, `[${statusMarker(count)}] ${count}`);
        }
      }
      return line;
    });

  return lines.join("\n").replace(/\n/g, eol);
}

/** Read disk + index, validate, and return everything write/check need. */
function analyze(skillsDir = SKILLS_DIR, indexPath = INDEX_PATH) {
  const skillFiles = enumerateSkillFiles(skillsDir);
  const indexText = fs.readFileSync(indexPath, "utf8");
  const parsed = parseIndex(indexText);
  const expected = buildExpectedModel(skillFiles);
  const errors = findBijectionErrors(skillFiles, parsed);
  const drift = errors.length === 0 ? findDrift(parsed, expected) : [];
  const newText = errors.length === 0 ? renderIndex(indexText, expected) : indexText;
  return { indexText, errors, drift, newText };
}

function main() {
  const checkMode = process.argv.slice(2).includes("--check");
  // Env overrides exist only so the CLI exit-code paths are testable against a
  // temp fixture; production runs use the repo defaults.
  const skillsDir = process.env.DX_SKILLS_DIR || SKILLS_DIR;
  const indexPath = process.env.DX_SKILLS_INDEX || INDEX_PATH;
  try {
    const { indexText, errors, drift, newText } = analyze(skillsDir, indexPath);

    if (errors.length > 0) {
      console.error("ERROR: .llm/skills/index.md rows and skill files are out of sync:");
      errors.forEach((error) => console.error(`  - ${error}`));
      console.error("Fix the row(s) by hand (metadata columns are hand-authored), then run: npm run update:skills-index");
      process.exit(1);
    }

    if (checkMode) {
      if (drift.length > 0) {
        console.error("ERROR: .llm/skills/index.md is out of date:");
        drift.forEach((entry) => console.error(`  - ${entry}`));
        console.error("Run: npm run update:skills-index");
        process.exit(1);
      }
      console.log("[ok] .llm/skills/index.md is up to date");
      return;
    }

    if (drift.length > 0) {
      if (newText === indexText) {
        // Drift the line-based renderer cannot fix (e.g. a missing Summary or
        // Table-of-Contents row). Stop instead of writing identical text and
        // claiming success, which would never converge.
        console.error("ERROR: .llm/skills/index.md has drift that cannot be auto-fixed");
        console.error("(a Summary or Table-of-Contents row may be missing or orphaned). Fix it by hand:");
        drift.forEach((entry) => console.error(`  - ${entry}`));
        process.exit(1);
      }
      fs.writeFileSync(indexPath, newText, "utf8");
      console.log(`[ok] Updated .llm/skills/index.md (${drift.length} cell(s) refreshed); run prettier to align.`);
    } else {
      console.log("[ok] .llm/skills/index.md already up to date");
    }
  } catch (error) {
    console.error("ERROR:", error.message);
    process.exit(1);
  }
}

if (require.main === module) {
  main();
}

module.exports = {
  analyze,
  buildExpectedModel,
  countLines,
  enumerateSkillFiles,
  findBijectionErrors,
  findDrift,
  parseIndex,
  renderIndex,
  slugify,
  statusMarker
};
