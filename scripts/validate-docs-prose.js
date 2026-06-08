#!/usr/bin/env node

/**
 * validate-docs-prose.js
 *
 * Enforces the human-prose policy for documentation. Detects LLM-style
 * marketing prose, hedge transitions, filler idioms, vague quantifiers,
 * and soft conversational fluff in human-readable documentation.
 *
 * Targets:
 *   - All .md files in the repository.
 *   - All .cs files under Runtime/, Editor/, Tests/, SourceGenerators/ -- but
 *     only the contents of /// XML doc comment lines are scanned.
 *   - The generated llms.txt at the repo root.
 *
 * Companion to scripts/validate-docs-ascii.js. The two validators share
 * structure, CLI shape, and exit-code contract on purpose.
 *
 * Skipped content within an otherwise-scanned file:
 *   - Lines inside fenced code blocks (``` ... ```).
 *   - Inline code spans (between ` ` on the same line).
 *   - URLs (http://... or https://...).
 *   - HTML tags and attributes (anything inside <...>, including those
 *     spanning multiple lines).
 *   - Markdown YAML frontmatter (--- ... --- at the top of .md files).
 *   - Indented code blocks (4+ spaces or 1 tab after a blank line).
 *
 * Inline allow markers (HTML comment forms, single-line only):
 *   <!-- prose-allow: term1, term2 -->            same line
 *   <!-- prose-allow-next-line: term1, term2 -->  next non-blank line
 *   <!-- prose-allow-file: term1, term2 -->       file-wide (anywhere)
 *
 * The marker comments themselves are stripped from the scanned text so
 * they cannot accidentally trigger their own banned terms. Multi-line
 * markers (closing --> on a different line than the opening <!--) are
 * detected and reported on stderr but do not fail the run.
 *
 * Per-file exemptions:
 *   - .llm/skills/documentation/ files are exempt entirely (they document
 *     the policy and must be free to discuss the banned terms).
 *   - CHANGELOG.md (case-insensitive) is exempt from the marketing rule
 *     for the term "comprehensive".
 *   - .llm/skills/index.md and llms.txt are exempt from "comprehensive"
 *     because they are auto-generated.
 *
 * Usage:
 *   node scripts/validate-docs-prose.js [--check] [--paths <comma-list>]
 *                                       [--rule <rule-id>]
 *                                       [--list-rules] [--summary]
 *                                       [--baseline <file>]
 *                                       [--write-baseline <file>]
 *                                       [files...]
 *
 * Exit codes:
 *   0  No violations.
 *   1  Violations found (or unrecoverable error).
 *
 * Public surface (module.exports):
 *   - scanContent(filePath, content, options) -> { violations, fileExempt }
 *   - scanFile(filePath, options)              -> { violations, fileExempt }
 *   - RULES                                   -> Array<RuleSpec>
 *   - RULE_INDEX                              -> Map<id, RuleSpec>
 *   - MARKETING_TERMS, LLM_FILLER_TERMS, HEDGE_TRANSITIONS,
 *     VAGUE_QUANTIFIER_TERMS                  -> term arrays
 *   - parseBaseline(text)                     -> Set<string>
 *   - baselineKey(violation)                  -> string
 *   - formatBaselineEntry(violation)          -> string
 *   - EXCLUDE_DIRS                            -> Set<string>
 *
 * Each RuleSpec has shape:
 *   { id, category, severity, description, matchLine(line, ctx) }
 * where matchLine returns Array<{ column, term, suggestion }>.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { walkFiles } = require("./lib/repo-files");

const ROOT_DIR = path.resolve(__dirname, "..");

const EXCLUDE_DIRS = new Set([
  ".git",
  "node_modules",
  "Library",
  "Temp",
  "obj",
  "bin",
  "Logs",
  "site",
  "coverage",
  "__pycache__",
  ".venv",
  "venv",
  ".artifacts",
  "progress",
  ".vs",
  ".claude",
  ".devcontainer",
  ".config"
]);

const CS_SCAN_ROOTS = ["Runtime", "Editor", "Tests", "SourceGenerators"];

const EXTRA_FILES = [path.join(ROOT_DIR, "llms.txt")];

const SKILL_DOC_POLICY_DIR = path.join(ROOT_DIR, ".llm", "skills", "documentation");

// Auto-generated files that are exempt from the validator. The contents
// of these files are produced by tooling that may legitimately use
// banned terms while regenerating across versions.
const GENERATED_EXEMPT_PATHS = new Set([
  path.join(ROOT_DIR, ".llm", "skills", "index.md"),
  path.join(ROOT_DIR, "llms.txt")
]);

// --- Rule catalog -----------------------------------------------------------

/**
 * A rule is a self-contained matcher with a category, severity, and
 * suggestion. Each rule's matchLine(line) returns an array of
 * { column, term, suggestion } findings. Column is 0-based; the reporter
 * adds 1 to produce a 1-based column number.
 */

function escapeRegExp(text) {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function buildWholeWordPattern(terms) {
  // Word boundary that also accepts hyphens as part of a phrase. We anchor
  // on lookbehind/lookahead of non-word-non-hyphen characters so that
  // multi-word phrases like "blazing fast" still match across whitespace.
  const escaped = terms
    .slice()
    .sort((a, b) => b.length - a.length)
    .map(escapeRegExp);
  return new RegExp(`(?<![A-Za-z0-9_-])(?:${escaped.join("|")})(?![A-Za-z0-9_-])`, "gi");
}

function buildPhrasePattern(phrases) {
  // Phrase match: any internal whitespace becomes \s+ so soft-wrapped lines
  // still match. No word boundary on the right (some phrases end in "of").
  const alternatives = phrases
    .slice()
    .sort((a, b) => b.length - a.length)
    .map((phrase) => phrase.split(/\s+/).map(escapeRegExp).join("\\s+"));
  return new RegExp(`(?<![A-Za-z0-9_-])(?:${alternatives.join("|")})(?![A-Za-z0-9_-])`, "gi");
}

const MARKETING_TERMS = [
  "cutting-edge",
  "cutting edge",
  "blazing fast",
  "seamless",
  "seamlessly",
  "seamlessness",
  "powerful",
  "powerfully",
  "robust",
  "robustly",
  "elegant",
  "elegantly",
  "world-class",
  "next-generation",
  "industry-leading",
  "state-of-the-art",
  "comprehensive",
  "comprehensively",
  "unparalleled",
  "revolutionary",
  "game-changing",
  "best-in-class",
  "production-ready",
  "enterprise-grade",
  "lightning-fast",
  "frictionless",
  "battle-tested",
  "bulletproof",
  "rock-solid"
];

const LLM_FILLER_TERMS = [
  "delve into",
  "delving into",
  "delved into",
  "delves into",
  "harness the power",
  "navigate the complexities",
  "unlock the potential",
  "tapestry",
  "realm of",
  "dive deep into",
  "dive into",
  "at the heart of",
  "lies the",
  "treasure trove",
  "it goes without saying",
  "needless to say"
];

const HEDGE_TRANSITIONS = [
  "Furthermore",
  "Moreover",
  "In conclusion",
  "In essence",
  "In summary",
  "It's important to note",
  "It's worth noting",
  "That said",
  "Overall",
  "Ultimately"
];

const VAGUE_QUANTIFIER_TERMS = [
  "a wide variety of",
  "a wide array of",
  "a plethora of",
  "myriad",
  "numerous"
];

const SOFT_FLUFF_PATTERNS = [
  {
    id: "gives-you-best",
    pattern: /\bgives you (?:the )?best\b/gi,
    suggestion: "Replace with a concrete claim or remove."
  },
  {
    id: "provides-you-with",
    pattern: /\bprovides you with\b/gi,
    suggestion: "Replace with 'provides' or restructure around the noun."
  },
  {
    id: "helps-you-to",
    pattern: /\bhelps you to\b/gi,
    suggestion: "Drop 'helps you to' and lead with the action verb."
  },
  {
    id: "allows-you-to-easily",
    pattern: /\ballows you to easily\b/gi,
    suggestion: "Drop 'allows you to easily' and state the action."
  },
  {
    id: "enables-you-to",
    pattern: /\benables you to\b/gi,
    suggestion: "Replace with 'lets you' or rewrite the subject as the actor."
  }
];

const MARKETING_REPLACEMENTS = {
  "cutting-edge": "modern, current, or describe the specific feature",
  "cutting edge": "modern, current, or describe the specific feature",
  "blazing fast": "give a measurement or remove",
  seamless: "describe what is integrated and how",
  seamlessly: "describe the integration concretely",
  seamlessness: "describe what is integrated and how",
  powerful: "describe the capability concretely",
  powerfully: "describe the capability concretely",
  robust: "describe the failure modes it handles",
  robustly: "describe the failure modes it handles",
  elegant: "describe the design choice",
  elegantly: "describe the design choice",
  "world-class": "describe the actual quality",
  "next-generation": "describe the version or capability",
  "industry-leading": "remove or cite a benchmark",
  "state-of-the-art": "describe the current technique",
  comprehensive: "list what is covered",
  comprehensively: "list what is covered",
  unparalleled: "remove or cite a comparison",
  revolutionary: "describe the change",
  "game-changing": "describe the impact",
  "best-in-class": "remove or cite a benchmark",
  "production-ready": "describe the operational guarantees",
  "enterprise-grade": "describe the actual capability",
  "lightning-fast": "give a measurement or remove",
  frictionless: "describe what was removed from the workflow",
  "battle-tested": "cite the deployments or workloads",
  bulletproof: "describe the failure modes it handles",
  "rock-solid": "describe the stability guarantees"
};

const FILLER_REPLACEMENTS = {
  "delve into": "discuss or describe",
  "delving into": "discussing or describing",
  "delved into": "discussed or described",
  "delves into": "discusses or describes",
  "harness the power": "use",
  "navigate the complexities": "handle the details of",
  "unlock the potential": "use the feature to",
  tapestry: "remove",
  "realm of": "remove or replace with the topic name",
  "dive deep into": "explain in detail",
  "dive into": "explain or cover",
  "at the heart of": "the core of",
  "lies the": "is the",
  "treasure trove": "set or collection",
  "it goes without saying": "remove",
  "needless to say": "remove"
};

const HEDGE_REPLACEMENTS = {
  Furthermore: "remove the transition; lead with the new fact",
  Moreover: "remove the transition; lead with the new fact",
  "In conclusion": "remove or replace with a concrete summary",
  "In essence": "remove and state the point directly",
  "In summary": "remove or replace with a concrete summary",
  "It's important to note": "state the fact directly",
  "It's worth noting": "state the fact directly",
  "That said": "remove or replace with the contrast directly",
  Overall: "remove or replace with a concrete summary",
  Ultimately: "remove or replace with the actual conclusion"
};

const QUANTIFIER_REPLACEMENTS = {
  "a wide variety of": "list the kinds or give a count",
  "a wide array of": "list the kinds or give a count",
  "a plethora of": "give a count or list the items",
  myriad: "give a count or describe the set",
  numerous: "give a count or describe the set"
};

const RULES = [];

function suggestionForTerm(map, term) {
  const lower = term.toLowerCase();
  for (const key of Object.keys(map)) {
    if (key.toLowerCase() === lower) {
      return map[key];
    }
  }
  return null;
}

RULES.push({
  id: "marketing",
  category: "marketing",
  severity: "error",
  description: "Marketing adjective; replace with a concrete claim.",
  pattern: buildWholeWordPattern(MARKETING_TERMS),
  terms: MARKETING_TERMS,
  matchLine(line, ctx) {
    const findings = [];
    let match;
    this.pattern.lastIndex = 0;
    while ((match = this.pattern.exec(line)) !== null) {
      const term = match[0];
      const lower = term.toLowerCase();
      // CHANGELOG.md exemption for "comprehensive" (case-insensitive).
      if (
        lower === "comprehensive" &&
        ctx &&
        ctx.fileBasename &&
        /^changelog/i.test(ctx.fileBasename)
      ) {
        continue;
      }
      findings.push({
        column: match.index,
        term,
        suggestion:
          suggestionForTerm(MARKETING_REPLACEMENTS, term) || "Replace with a concrete claim."
      });
    }
    return findings;
  }
});

RULES.push({
  id: "llm-filler",
  category: "llm-filler",
  severity: "error",
  description: "LLM-signature filler idiom; rewrite with concrete language.",
  pattern: buildPhrasePattern(LLM_FILLER_TERMS),
  terms: LLM_FILLER_TERMS,
  matchLine(line) {
    const findings = [];
    let match;
    this.pattern.lastIndex = 0;
    while ((match = this.pattern.exec(line)) !== null) {
      const term = match[0];
      findings.push({
        column: match.index,
        term,
        suggestion:
          suggestionForTerm(FILLER_REPLACEMENTS, term) || "Rewrite without the filler idiom."
      });
    }
    return findings;
  }
});

RULES.push({
  id: "hedge",
  category: "hedge",
  severity: "error",
  description: "Hedge or weak transition at sentence start; lead with the fact.",
  terms: HEDGE_TRANSITIONS,
  // Custom matcher: only fire when the term appears at the start of a
  // line (after optional leading whitespace, optional list / blockquote
  // markers like "- ", "* ", "> ", and optional C# /// XML doc prefix).
  // Matches with or without trailing comma.
  matchLine(line) {
    const findings = [];
    const stripped = line.replace(/^[\s>*\-+#/]*(?:\d+[.)]\s+)?/, (m) => " ".repeat(m.length));
    for (const term of HEDGE_TRANSITIONS) {
      const idx = leadingMatch(stripped, term);
      if (idx === -1) continue;
      findings.push({
        column: idx,
        term,
        suggestion:
          suggestionForTerm(HEDGE_REPLACEMENTS, term) ||
          "Drop the transition and state the fact directly."
      });
    }
    return findings;
  }
});

function leadingMatch(line, term) {
  // Returns the column (0-based) where `term` starts at the beginning of
  // `line` ignoring case, or -1 if the term is not the first non-space
  // token. We treat the term as a literal substring; a trailing comma is
  // optional. The match must be followed by a non-word boundary so that
  // "Overall" does not match "Overalls".
  const lineLower = line.toLowerCase();
  const termLower = term.toLowerCase();
  const start = line.search(/\S/);
  if (start < 0) return -1;
  if (!lineLower.startsWith(termLower, start)) {
    return -1;
  }
  let end = start + termLower.length;
  // Optional trailing comma -- still considered part of the hedge.
  if (lineLower.charAt(end) === ",") {
    end += 1;
  }
  // Boundary check: the next character must not continue the word.
  const next = lineLower.charAt(end);
  if (next === "" || /[^a-z0-9_-]/.test(next)) {
    return start;
  }
  return -1;
}

RULES.push({
  id: "vague-quantifier",
  category: "vague-quantifier",
  severity: "error",
  description: "Vague quantifier; give a count or list the items.",
  pattern: buildPhrasePattern(VAGUE_QUANTIFIER_TERMS),
  terms: VAGUE_QUANTIFIER_TERMS,
  matchLine(line) {
    const findings = [];
    let match;
    this.pattern.lastIndex = 0;
    while ((match = this.pattern.exec(line)) !== null) {
      const term = match[0];
      findings.push({
        column: match.index,
        term,
        suggestion:
          suggestionForTerm(QUANTIFIER_REPLACEMENTS, term) ||
          "Replace with a count or concrete list."
      });
    }
    return findings;
  }
});

RULES.push({
  id: "soft-fluff",
  category: "soft-fluff",
  severity: "error",
  description: "Soft conversational fluff; rewrite around the action.",
  terms: SOFT_FLUFF_PATTERNS.map((p) => p.id),
  matchLine(line) {
    const findings = [];
    for (const sub of SOFT_FLUFF_PATTERNS) {
      sub.pattern.lastIndex = 0;
      let match;
      while ((match = sub.pattern.exec(line)) !== null) {
        findings.push({
          column: match.index,
          term: match[0],
          suggestion: sub.suggestion
        });
      }
    }
    return findings;
  }
});

const RULE_INDEX = new Map(RULES.map((r) => [r.id, r]));

// --- File enumeration -------------------------------------------------------

function defaultFileSet() {
  const out = [];
  out.push(
    ...walkFiles(ROOT_DIR, {
      match: (p) => p.endsWith(".md"),
      excludeDir: (full, dirent) => EXCLUDE_DIRS.has(dirent.name)
    })
  );
  for (const root of CS_SCAN_ROOTS) {
    const abs = path.join(ROOT_DIR, root);
    if (!fs.existsSync(abs)) continue;
    out.push(
      ...walkFiles(abs, {
        match: (p) => p.endsWith(".cs"),
        excludeDir: (full, dirent) => EXCLUDE_DIRS.has(dirent.name)
      })
    );
  }
  for (const extra of EXTRA_FILES) {
    if (fs.existsSync(extra)) out.push(extra);
  }
  return out;
}

function isExempt(filePath) {
  // Skill files inside .llm/skills/documentation/ describe the policy.
  if (filePath.startsWith(SKILL_DOC_POLICY_DIR + path.sep)) return true;
  if (filePath === SKILL_DOC_POLICY_DIR) return true;
  // Auto-generated index/llms files would otherwise regenerate the same
  // violations on every run.
  if (GENERATED_EXEMPT_PATHS.has(filePath)) return true;
  return false;
}

// --- Scrubbing helpers ------------------------------------------------------

// Allow markers: tightened to accept only letters, digits, commas, spaces,
// tabs, hyphens, apostrophes inside the term list. Anything outside that
// character class causes the marker to not match (so e.g. a stray '>' or
// '<' inside the list will fail closed instead of swallowing more text).
const ALLOW_LINE_RE = /<!--\s*prose-allow:\s*([A-Za-z0-9,'\- \t]*?)\s*-->/i;
const ALLOW_NEXT_RE = /<!--\s*prose-allow-next-line:\s*([A-Za-z0-9,'\- \t]*?)\s*-->/i;
const ALLOW_FILE_RE = /<!--\s*prose-allow-file:\s*([A-Za-z0-9,'\- \t]*?)\s*-->/gi;

// Detect a malformed (multi-line) marker: line opens "<!-- prose-allow..."
// but does NOT close with "-->" on the same line.
const MALFORMED_MARKER_RE = /<!--\s*prose-allow(?:-next-line|-file)?:[^>]*$/i;

function parseAllowList(spec) {
  return spec
    .split(",")
    .map((t) => t.trim().toLowerCase())
    .filter(Boolean);
}

function extractFileAllowList(content) {
  const allowed = new Set();
  let match;
  ALLOW_FILE_RE.lastIndex = 0;
  while ((match = ALLOW_FILE_RE.exec(content)) !== null) {
    for (const term of parseAllowList(match[1])) {
      allowed.add(term);
    }
  }
  return allowed;
}

function stripAllowMarkerComments(line) {
  return line.replace(ALLOW_LINE_RE, "").replace(ALLOW_NEXT_RE, "").replace(ALLOW_FILE_RE, "");
}

function shouldScanLineCs(line) {
  return /^\s*\/\/\//.test(line);
}

function isFenceOpenClose(line) {
  return /^\s*```/.test(line);
}

// Detect an indented (4+ space or 1 tab) Markdown code block start. The
// caller maintains state for "previous line was blank" because indented
// code blocks must be preceded by a blank line.
function isIndentedCodeStart(line) {
  return /^(?:    | *\t)/.test(line) && line.trim() !== "";
}

// --- Scanning ---------------------------------------------------------------

function scanContent(filePath, content, options) {
  const opts = options || {};
  const ruleFilter = opts.rule || null;
  const fileBasename = path.basename(filePath);
  const isCsharp = filePath.endsWith(".cs");
  const isMarkdown = filePath.endsWith(".md") || filePath.endsWith(".markdown");
  if (isExempt(filePath)) {
    return { violations: [], fileExempt: true };
  }

  // Strip a leading BOM so that line/column counts and frontmatter
  // detection do not include it.
  let normalized = content;
  if (normalized.charCodeAt(0) === 0xfeff) {
    normalized = normalized.slice(1);
  }
  normalized = normalized.replace(/\r\n/g, "\n").replace(/\r/g, "\n");

  const fileAllow = extractFileAllowList(normalized);
  const lines = normalized.split("\n");

  // Markdown YAML frontmatter: if the first line is exactly "---", skip
  // every line up to and including the next "---" line.
  let frontmatterEnd = -1; // exclusive index into `lines`
  if (isMarkdown && lines.length > 0 && lines[0] === "---") {
    for (let j = 1; j < lines.length; j++) {
      if (lines[j] === "---") {
        frontmatterEnd = j + 1;
        break;
      }
    }
  }

  const violations = [];
  let inFence = false;
  let inHtmlTag = false;
  let prevBlank = true; // before file start counts as "blank" for indented-block detection
  let inIndentedBlock = false;
  let pendingNextLineAllow = null;
  let pendingNextLineUsed = false;

  const malformedMarkers = [];

  for (let lineIndex = 0; lineIndex < lines.length; lineIndex++) {
    // Skip frontmatter wholly.
    if (frontmatterEnd > 0 && lineIndex < frontmatterEnd) {
      // Frontmatter resets indented-block detection.
      prevBlank = true;
      inIndentedBlock = false;
      continue;
    }

    const rawLine = lines[lineIndex];
    const lineIsBlank = rawLine.trim() === "";

    // Allow markers do not gate fence detection; check fence on raw line.
    if (isFenceOpenClose(rawLine)) {
      inFence = !inFence;
      // After the fence transition we may still want to scan any
      // residue that follows the closing ``` on the same line.
      if (!inFence) {
        // Closing fence: scan whatever follows after the ``` on this line.
        const fenceMatch = rawLine.match(/^\s*```\S*\s*/);
        const residue = fenceMatch ? rawLine.slice(fenceMatch[0].length) : "";
        if (residue.trim() !== "") {
          // Synthesize a virtual line whose column offsets match
          // the original by left-padding with spaces.
          const padded = " ".repeat(rawLine.length - residue.length) + residue;
          scanLine(
            padded,
            lineIndex,
            violations,
            ruleFilter,
            fileAllow,
            pendingNextLineAllow,
            pendingNextLineUsed,
            fileBasename,
            isCsharp,
            filePath
            // pendingState mutator (callbacks unused here -- residue
            // does not consume the pending allow either way).
          );
        }
      }
      prevBlank = false;
      continue;
    }
    if (inFence) {
      prevBlank = false;
      continue;
    }

    // Indented code-block detection (Markdown only). The block starts
    // when a line is indented by 4+ spaces / 1 tab AND the previous
    // line was blank.
    if (isMarkdown) {
      if (!inIndentedBlock && prevBlank && isIndentedCodeStart(rawLine)) {
        inIndentedBlock = true;
      }
      if (inIndentedBlock) {
        // Block continues while lines remain indented (or blank inside).
        if (lineIsBlank || isIndentedCodeStart(rawLine)) {
          prevBlank = lineIsBlank;
          continue;
        }
        inIndentedBlock = false;
        // fall through to scan this non-indented line
      }
    }

    // Detect prose-allow-next-line markers BEFORE we decide whether to
    // scan this line: a marker on its own line transfers its allow set
    // to the next non-blank scanned line.
    const nextLineMatch = rawLine.match(ALLOW_NEXT_RE);
    if (nextLineMatch) {
      pendingNextLineAllow = new Set(parseAllowList(nextLineMatch[1]));
      pendingNextLineUsed = false;
      // Strip the marker so the rest of the line still scans correctly,
      // but if the line is otherwise blank it is a pure marker line.
      const stripped = stripAllowMarkerComments(rawLine).trim();
      if (stripped === "") {
        prevBlank = true;
        continue;
      }
    }

    // Multi-line allow-marker detection: warn but do not fail.
    if (
      !nextLineMatch &&
      !rawLine.match(ALLOW_LINE_RE) &&
      !rawLine.match(/<!--\s*prose-allow-file:.*-->/i) &&
      MALFORMED_MARKER_RE.test(rawLine)
    ) {
      malformedMarkers.push({ file: filePath, line: lineIndex + 1 });
    }

    const sameLineMatch = rawLine.match(ALLOW_LINE_RE);
    const sameLineAllow = sameLineMatch ? new Set(parseAllowList(sameLineMatch[1])) : null;

    if (isCsharp && !shouldScanLineCs(rawLine)) {
      prevBlank = lineIsBlank;
      continue;
    }

    let workLine = stripAllowMarkerComments(rawLine);

    // Empty lines never produce findings, but we should not consume the
    // pending-next-line allow on a blank line.
    if (workLine.trim() === "") {
      prevBlank = true;
      continue;
    }

    // Apply the pending next-line allow if we have one and have not yet
    // applied it.
    let activeNextLineAllow = null;
    if (pendingNextLineAllow && !pendingNextLineUsed) {
      activeNextLineAllow = pendingNextLineAllow;
      pendingNextLineUsed = true;
    }

    const maskState = { inHtmlTag };
    const masked = maskScanLine(workLine, maskState);
    inHtmlTag = maskState.inHtmlTag;

    const ctx = { fileBasename };

    for (const rule of RULES) {
      if (ruleFilter && rule.id !== ruleFilter) continue;
      const findings = rule.matchLine(masked, ctx);
      for (const f of findings) {
        const termLower = f.term.toLowerCase();
        if (fileAllow.has(termLower)) continue;
        if (sameLineAllow && sameLineAllow.has(termLower)) continue;
        if (activeNextLineAllow && activeNextLineAllow.has(termLower)) continue;
        violations.push({
          file: filePath,
          line: lineIndex + 1,
          column: f.column + 1,
          rule: rule.id,
          category: rule.category,
          severity: rule.severity,
          term: f.term,
          message: rule.description,
          suggestion: f.suggestion
        });
      }
    }

    // Pending-next-line allow expires after one scanned content line.
    if (activeNextLineAllow) {
      pendingNextLineAllow = null;
    }

    prevBlank = false;
  }

  return { violations, fileExempt: false, malformedMarkers };
}

/**
 * Inner helper used by the closing-fence-residue path.
 *
 * We re-implement only the rule-matching slice of the main loop because
 * the residue scan does not need to update fence/allow state. It does
 * need to obey the same filtering rules so that residue findings are
 * reported with the original line number.
 */
function scanLine(
  paddedLine,
  lineIndex,
  violations,
  ruleFilter,
  fileAllow,
  pendingNextLineAllow,
  pendingNextLineUsed,
  fileBasename,
  isCsharp,
  filePath
) {
  if (isCsharp && !shouldScanLineCs(paddedLine)) return;
  const stripped = stripAllowMarkerComments(paddedLine);
  if (stripped.trim() === "") return;
  const sameLineMatch = paddedLine.match(ALLOW_LINE_RE);
  const sameLineAllow = sameLineMatch ? new Set(parseAllowList(sameLineMatch[1])) : null;
  const maskState = { inHtmlTag: false };
  const masked = maskScanLine(stripped, maskState);
  const ctx = { fileBasename };
  for (const rule of RULES) {
    if (ruleFilter && rule.id !== ruleFilter) continue;
    const findings = rule.matchLine(masked, ctx);
    for (const f of findings) {
      const termLower = f.term.toLowerCase();
      if (fileAllow.has(termLower)) continue;
      if (sameLineAllow && sameLineAllow.has(termLower)) continue;
      if (pendingNextLineAllow && pendingNextLineAllow.has(termLower)) {
        continue;
      }
      violations.push({
        file: filePath,
        line: lineIndex + 1,
        column: f.column + 1,
        rule: rule.id,
        category: rule.category,
        severity: rule.severity,
        term: f.term,
        message: rule.description,
        suggestion: f.suggestion
      });
    }
  }
}

/**
 * Replace every span we should not scan with spaces of equal length, so
 * column numbers stay aligned with the original line. State allows us to
 * keep track of HTML tags that span multiple lines.
 *
 * Fast path: when the previous line did not leave us inside a tag and this
 * line contains no '<' character, no HTML masking is needed.
 */
function maskScanLine(line, state) {
  let out = line;
  // URLs
  out = out.replace(/https?:\/\/\S+/gi, (m) => " ".repeat(m.length));
  // Inline code: backtick-delimited spans on a single line.
  if (out.indexOf("`") !== -1) {
    out = out.replace(/`[^`]*`/g, (m) => " ".repeat(m.length));
  }

  const inTagInitial = state && state.inHtmlTag;
  if (!inTagInitial && out.indexOf("<") === -1) {
    // No tag carryover, no '<' in line -- nothing to mask.
    return out;
  }

  // HTML tags. We must handle multi-line tags via state. Walk char-by-char
  // and replace any character inside a tag with a space.
  let inTag = inTagInitial;
  let result = "";
  for (let i = 0; i < out.length; i++) {
    const ch = out.charCodeAt(i);
    if (!inTag) {
      if (ch === 60) {
        // '<'
        inTag = true;
        result += " ";
      } else {
        result += out[i];
      }
    } else {
      result += " ";
      if (ch === 62) {
        // '>'
        inTag = false;
      }
    }
  }
  if (state) state.inHtmlTag = inTag;
  return result;
}

function scanFile(filePath, options) {
  let content;
  try {
    content = fs.readFileSync(filePath, "utf8");
  } catch (error) {
    return { violations: [], fileExempt: false };
  }
  return scanContent(filePath, content, options);
}

// --- Baseline support -------------------------------------------------------

/**
 * Format a single violation as a baseline entry. The format is stable:
 *   "<relative-path>:<line>:<col> [<rule>] <term>"
 * Each part is significant -- changing any field breaks the match.
 */
function formatBaselineEntry(violation) {
  const rel = path.relative(ROOT_DIR, violation.file) || violation.file;
  return `${rel}:${violation.line}:${violation.column} [${violation.rule}] ${violation.term}`;
}

/**
 * Produce a stable string key for matching a violation against a baseline.
 * Uses the same components as formatBaselineEntry but stripped to the
 * minimum: path, line, col, rule, term.
 */
function baselineKey(violation) {
  const rel = path.relative(ROOT_DIR, violation.file) || violation.file;
  return `${rel}|${violation.line}|${violation.column}|${violation.rule}|${violation.term}`;
}

/**
 * Parse a baseline file's text into a Set of keys. Entries follow the
 * format produced by formatBaselineEntry. Blank lines and lines starting
 * with "#" are ignored.
 */
function parseBaseline(text) {
  const keys = new Set();
  if (!text) return keys;
  const lines = text.replace(/\r\n/g, "\n").split("\n");
  for (const line of lines) {
    if (!line || line.startsWith("#")) continue;
    // path:line:col [rule] term
    const m = line.match(/^(.+?):(\d+):(\d+)\s+\[([^\]]+)\]\s+(.+)$/);
    if (!m) continue;
    const [, p, ln, col, rule, term] = m;
    keys.add(`${p}|${ln}|${col}|${rule}|${term}`);
  }
  return keys;
}

// --- CLI --------------------------------------------------------------------

function parseArgs(argv) {
  const args = {
    check: true,
    paths: null,
    files: [],
    listRules: false,
    rule: null,
    summary: false,
    baseline: null,
    writeBaseline: null
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--check") {
      args.check = true;
    } else if (a === "--paths") {
      args.paths = argv[++i];
    } else if (a === "--rule") {
      args.rule = argv[++i];
    } else if (a === "--list-rules") {
      args.listRules = true;
    } else if (a === "--summary") {
      args.summary = true;
    } else if (a === "--baseline") {
      args.baseline = argv[++i];
    } else if (a === "--write-baseline") {
      args.writeBaseline = argv[++i];
    } else if (a === "--help" || a === "-h") {
      printHelp();
      process.exit(0);
    } else if (a.startsWith("--")) {
      console.error(`Unknown option: ${a}`);
      process.exit(1);
    } else {
      args.files.push(a);
    }
  }
  return args;
}

function printHelp() {
  process.stdout.write(
    [
      "Usage: node scripts/validate-docs-prose.js [options] [files...]",
      "",
      "Options:",
      "  --check               Default. Exit 1 on any banned phrase.",
      "  --paths <list>        Comma-separated paths or directory roots.",
      "  --rule <id>           Run only the named rule.",
      "  --list-rules          Print rules and term lists, then exit.",
      "  --summary             Print per-category counts instead of per-line.",
      "  --baseline <file>     Skip violations whose path/line/col/rule/term",
      "                        match a line in the baseline file (transitional).",
      "  --write-baseline <file>  Write the current violation set to <file>",
      "                           in baseline format and exit.",
      "  -h, --help            Show this message.",
      ""
    ].join("\n")
  );
}

function printRuleList() {
  process.stdout.write("Configured prose rules:\n\n");
  for (const rule of RULES) {
    process.stdout.write(
      `  ${rule.id.padEnd(20)} category=${rule.category.padEnd(20)} severity=${rule.severity}\n`
    );
    process.stdout.write(`    ${rule.description}\n`);
    if (Array.isArray(rule.terms) && rule.terms.length > 0) {
      process.stdout.write(`    terms: ${rule.terms.join(", ")}\n`);
    }
  }
}

function resolveFileList(args) {
  if (args.files.length > 0) {
    return args.files.map((f) => path.resolve(process.cwd(), f));
  }
  if (args.paths) {
    const out = [];
    for (const entry of args.paths.split(",")) {
      const abs = path.resolve(process.cwd(), entry);
      if (!fs.existsSync(abs)) continue;
      const stat = fs.statSync(abs);
      if (stat.isDirectory()) {
        // When --paths targets a directory, walk both .md and .cs
        // files unconditionally -- the user explicitly opted in.
        out.push(
          ...walkFiles(abs, {
            match: (p) => p.endsWith(".md") || p.endsWith(".cs"),
            excludeDir: (full, dirent) => EXCLUDE_DIRS.has(dirent.name)
          })
        );
      } else if (stat.isFile()) {
        out.push(abs);
      }
    }
    return out;
  }
  return defaultFileSet();
}

function relativeOrAbsolute(filePath) {
  // When the relative path would escape the repo root (starts with "..")
  // fall back to the absolute path so the violation address is unambiguous.
  const rel = path.relative(ROOT_DIR, filePath);
  if (!rel || rel.startsWith("..") || path.isAbsolute(rel)) {
    return filePath;
  }
  return rel;
}

function main() {
  const args = parseArgs(process.argv.slice(2));

  if (args.listRules) {
    printRuleList();
    return 0;
  }

  if (args.rule && !RULE_INDEX.has(args.rule)) {
    process.stderr.write(`Unknown rule id: ${args.rule}\n`);
    return 1;
  }

  const files = resolveFileList(args);

  let baselineKeys = new Set();
  if (args.baseline) {
    if (fs.existsSync(args.baseline)) {
      const text = fs.readFileSync(args.baseline, "utf8");
      baselineKeys = parseBaseline(text);
    } else {
      process.stderr.write(`validate-docs-prose: baseline file not found: ${args.baseline}\n`);
      return 1;
    }
  }

  const allViolations = [];
  const allMalformed = [];
  for (const file of files) {
    const result = scanFile(file, { rule: args.rule });
    if (result.violations && result.violations.length > 0) {
      allViolations.push(...result.violations);
    }
    if (result.malformedMarkers && result.malformedMarkers.length > 0) {
      allMalformed.push(...result.malformedMarkers);
    }
  }

  // Always emit malformed-marker warnings to stderr (non-fatal).
  for (const m of allMalformed) {
    const rel = relativeOrAbsolute(m.file);
    process.stderr.write(
      `WARN malformed prose-allow marker spans multiple lines at ${rel}:${m.line}\n`
    );
  }

  // --write-baseline mode: dump current violations and exit 0.
  if (args.writeBaseline) {
    const lines = [
      "# validate-docs-prose baseline",
      "# Generated by: node scripts/validate-docs-prose.js --write-baseline",
      "# Format: path:line:col [rule] term",
      "# Each entry grandfathers ONE specific violation. New violations are",
      "# still flagged. Phase B1 will rewrite these and delete the file.",
      ""
    ];
    const sorted = allViolations.map(formatBaselineEntry).sort();
    for (const entry of sorted) lines.push(entry);
    const text = lines.join("\n") + "\n";
    fs.writeFileSync(args.writeBaseline, text, "utf8");
    process.stdout.write(
      `validate-docs-prose: wrote ${allViolations.length} entries to ${args.writeBaseline}\n`
    );
    return 0;
  }

  // Filter out violations matched by the baseline.
  const filtered =
    baselineKeys.size > 0
      ? allViolations.filter((v) => !baselineKeys.has(baselineKey(v)))
      : allViolations;

  const baselineGrandfathered = allViolations.length - filtered.length;

  if (args.summary) {
    // In summary mode, summary trailer goes to stdout (it's the success
    // signal). The error trailer remains on stderr.
    if (filtered.length === 0) {
      process.stdout.write(`validate-docs-prose: 0 violations across ${files.length} file(s).\n`);
      if (baselineGrandfathered > 0) {
        process.stdout.write(
          `validate-docs-prose: ${baselineGrandfathered} baseline-grandfathered violation(s).\n`
        );
      }
      return 0;
    }
    const counts = new Map();
    for (const v of filtered) {
      counts.set(v.category, (counts.get(v.category) || 0) + 1);
    }
    for (const [cat, count] of [...counts.entries()].sort()) {
      process.stdout.write(`${cat}: ${count}\n`);
    }
    process.stdout.write(
      `\nvalidate-docs-prose: ${filtered.length} violation(s) across ${counts.size} categories.\n`
    );
    if (baselineGrandfathered > 0) {
      process.stdout.write(
        `validate-docs-prose: ${baselineGrandfathered} baseline-grandfathered violation(s).\n`
      );
    }
    return 1;
  }

  if (filtered.length === 0) {
    process.stdout.write(`validate-docs-prose: 0 violations across ${files.length} file(s).\n`);
    if (baselineGrandfathered > 0) {
      process.stdout.write(
        `validate-docs-prose: ${baselineGrandfathered} baseline-grandfathered violation(s).\n`
      );
    }
    return 0;
  }

  for (const v of filtered) {
    const rel = relativeOrAbsolute(v.file);
    process.stderr.write(
      `${rel}:${v.line}:${v.column} [${v.category}/${v.rule}] '${v.term}' -- ${v.message} ${v.suggestion}\n`
    );
  }
  process.stderr.write(`\nvalidate-docs-prose: ${filtered.length} violation(s) found.\n`);
  if (baselineGrandfathered > 0) {
    process.stderr.write(
      `validate-docs-prose: ${baselineGrandfathered} baseline-grandfathered violation(s) skipped.\n`
    );
  }
  return 1;
}

if (require.main === module) {
  process.exit(main());
}

/**
 * Public module surface. Tests and downstream tooling consume these
 * exports; treat any change here as a breaking-change for callers.
 *
 * @typedef {Object} Violation
 * @property {string} file       Absolute path to the offending file.
 * @property {number} line       1-based line number.
 * @property {number} column     1-based column number.
 * @property {string} rule       Rule id (see RULES[].id).
 * @property {string} category   Category id; for current rules == id.
 * @property {string} severity   Always "error" today.
 * @property {string} term       The exact text that matched.
 * @property {string} message    Short human-readable rule description.
 * @property {string} suggestion Suggested fix strategy.
 *
 * @typedef {Object} ScanResult
 * @property {Array<Violation>} violations
 * @property {boolean} fileExempt
 * @property {Array<{file:string,line:number}>=} malformedMarkers
 *
 * @typedef {Object} ScanOptions
 * @property {string=} rule  Restrict scanning to a single rule id.
 */
module.exports = {
  scanContent,
  scanFile,
  RULES,
  RULE_INDEX,
  MARKETING_TERMS,
  LLM_FILLER_TERMS,
  HEDGE_TRANSITIONS,
  VAGUE_QUANTIFIER_TERMS,
  parseBaseline,
  baselineKey,
  formatBaselineEntry,
  EXCLUDE_DIRS
};
