#!/usr/bin/env node
/**
 * validate-changelog.js
 *
 * Enforces changelog policy with deterministic errors and heuristic warnings:
 * - ERROR: missing Unreleased section
 * - ERROR: package.json version missing from CHANGELOG.md
 * - ERROR: invalid changelog category header
 * - ERROR: user-visible file changes without CHANGELOG.md update (when coverage checks are enabled)
 * - WARNING: Unreleased section has no entries
 * - WARNING: likely internal-only changelog entry in Unreleased
 * - WARNING: likely category mismatch in Unreleased entry
 * - WARNING: likely duplicate Added+Fixed entry pair for the same unreleased item
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { execFileSync } = require("child_process");
const { normalizeToLf } = require("./lib/quote-parser");

const REPO_ROOT = path.join(__dirname, "..");
const CHANGELOG_PATH = path.join(REPO_ROOT, "CHANGELOG.md");
const PACKAGE_JSON_PATH = path.join(REPO_ROOT, "package.json");

const VALID_CATEGORIES = new Set([
  "Added",
  "Changed",
  "Deprecated",
  "Removed",
  "Fixed",
  "Security"
]);

const INTERNAL_ONLY_PATTERNS = [
  /\bmeta files?\b/i,
  /\.meta\b/i,
  /\bnpmignore\b/i,
  /\.npmignore\b/i,
  /\bpre-commit\b/i,
  /\bworkflow\b/i,
  /\bci\b/i,
  /\bcspell\b/i,
  /\bprettier\b/i,
  /\blinter\b/i,
  /\bautomation\b/i,
  /\btooling\b/i,
  /\brefactor(?:ed|ing)?\b/i,
  /\binternal\b/i,
  /\bagent(?:ic)?\b/i,
  /\binstruction(?:s)?\b/i,
  /\bprompt(?:s)?\b/i,
  /\blarge language model(?:s)?\b/i,
  /\bbuild harness\b/i,
  /\bskill(?:s)?\b/i,
  /\btest(?:s|ing)?(?:\s+only)?\b/i
];

const USER_IMPACT_HINTS = [
  /\buser(?:s)?\b/i,
  /\bapi\b/i,
  /\bruntime\b/i,
  /\binspector\b/i,
  /\bmessage(?:s|ing)?\b/i,
  /\bunity editor\b/i,
  /\bplayer build(?:s)?\b/i,
  /\bperformance\b/i,
  /\bstability\b/i,
  /\bcrash\b/i,
  /\bnow\b/i,
  /\bsupport(?:s|ed)?\b/i,
  /\bfix(?:ed|es)?\b/i
];

const CATEGORY_PREFIX_MISMATCH_RULES = {
  Added: [/^fixed\b/i, /^resolved\b/i, /^corrected\b/i, /^removed\b/i],
  Fixed: [/^added\b/i, /^new\b/i, /^introduced\b/i, /^deprecated\b/i],
  Removed: [/^added\b/i, /^fixed\b/i],
  Deprecated: [/^added\b/i, /^fixed\b/i],
  Security: [/^added\b/i, /^removed\b/i]
};

const STOP_WORDS = new Set([
  "a",
  "an",
  "and",
  "are",
  "as",
  "at",
  "be",
  "by",
  "for",
  "from",
  "in",
  "into",
  "is",
  "it",
  "its",
  "now",
  "of",
  "on",
  "or",
  "that",
  "the",
  "their",
  "this",
  "to",
  "using",
  "when",
  "with"
]);

class Violation {
  constructor(code, severity, message, line = null, suggestion = "") {
    this.code = code;
    this.severity = severity;
    this.message = message;
    this.line = line;
    this.suggestion = suggestion;
  }

  toString() {
    const lineSuffix = this.line == null ? "" : ` (line ${this.line})`;
    const suggestionSuffix = this.suggestion ? `\n  Suggestion: ${this.suggestion}` : "";
    return `${this.severity} ${this.code}${lineSuffix}: ${this.message}${suggestionSuffix}`;
  }
}

function normalizeRepoPath(filePath) {
  return String(filePath || "")
    .trim()
    .replace(/\\/g, "/")
    .replace(/^\.\//, "");
}

function parseArgs(argv) {
  const options = {
    checkCoverage: false,
    strictWarnings: false,
    help: false,
    changelogPath: CHANGELOG_PATH,
    packageJsonPath: PACKAGE_JSON_PATH,
    changedFiles: []
  };

  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index];

    if (argument === "--help" || argument === "-h") {
      options.help = true;
      continue;
    }

    if (argument === "--check-coverage") {
      options.checkCoverage = true;
      continue;
    }

    if (argument === "--strict-warnings") {
      options.strictWarnings = true;
      continue;
    }

    if (argument === "--changed-file") {
      const value = argv[index + 1];
      if (!value) {
        throw new Error("--changed-file requires a path value");
      }
      options.changedFiles.push(normalizeRepoPath(value));
      index++;
      continue;
    }

    if (argument.startsWith("--changed-file=")) {
      options.changedFiles.push(normalizeRepoPath(argument.slice("--changed-file=".length)));
      continue;
    }

    if (argument === "--changelog") {
      const value = argv[index + 1];
      if (!value) {
        throw new Error("--changelog requires a path value");
      }
      options.changelogPath = path.resolve(value);
      index++;
      continue;
    }

    if (argument.startsWith("--changelog=")) {
      options.changelogPath = path.resolve(argument.slice("--changelog=".length));
      continue;
    }

    if (argument === "--package-json") {
      const value = argv[index + 1];
      if (!value) {
        throw new Error("--package-json requires a path value");
      }
      options.packageJsonPath = path.resolve(value);
      index++;
      continue;
    }

    if (argument.startsWith("--package-json=")) {
      options.packageJsonPath = path.resolve(argument.slice("--package-json=".length));
      continue;
    }

    if (argument.startsWith("-")) {
      throw new Error(`Unknown argument: ${argument}`);
    }

    options.changedFiles.push(normalizeRepoPath(argument));
  }

  options.changedFiles = options.changedFiles.filter(Boolean);
  return options;
}

function parsePackageVersion(packageJsonContent) {
  let parsedPackage;

  try {
    parsedPackage = JSON.parse(packageJsonContent);
  } catch (error) {
    throw new Error(`Unable to parse package.json: ${error.message}`);
  }

  if (
    !parsedPackage ||
    typeof parsedPackage.version !== "string" ||
    parsedPackage.version.trim() === ""
  ) {
    throw new Error("package.json is missing a non-empty version field");
  }

  return parsedPackage.version.trim();
}

function parseChangelog(changelogContent) {
  const lines = normalizeToLf(changelogContent).split("\n");
  const sections = [];
  const entries = [];

  let currentSection = null;
  let currentCategory = null;
  let currentEntry = null;

  const finalizeEntry = () => {
    if (!currentEntry || !currentSection || !currentCategory) {
      currentEntry = null;
      return;
    }

    const normalizedText = currentEntry.text.replace(/\s+/g, " ").trim();
    if (normalizedText.length === 0) {
      currentEntry = null;
      return;
    }

    const entry = {
      version: currentSection.version,
      category: currentCategory.name,
      line: currentEntry.line,
      text: normalizedText
    };

    currentCategory.entries.push(entry);
    entries.push(entry);
    currentEntry = null;
  };

  for (let lineIndex = 0; lineIndex < lines.length; lineIndex++) {
    const rawLine = lines[lineIndex];
    const trimmedLine = rawLine.trim();

    const sectionMatch = /^## \[([^\]]+)\](?:\s*-\s*(\d{4}-\d{2}-\d{2}))?\s*$/.exec(trimmedLine);
    if (sectionMatch) {
      finalizeEntry();
      currentCategory = null;
      currentSection = {
        version: sectionMatch[1].trim(),
        date: sectionMatch[2] || null,
        line: lineIndex + 1,
        categories: []
      };
      sections.push(currentSection);
      continue;
    }

    const categoryMatch = /^###\s+(.+?)\s*$/.exec(trimmedLine);
    if (categoryMatch && currentSection) {
      finalizeEntry();
      currentCategory = {
        name: categoryMatch[1].trim(),
        line: lineIndex + 1,
        entries: []
      };
      currentSection.categories.push(currentCategory);
      continue;
    }

    const entryMatch = /^-\s+(.+?)\s*$/.exec(trimmedLine);
    if (entryMatch && currentSection && currentCategory) {
      finalizeEntry();
      currentEntry = {
        line: lineIndex + 1,
        text: entryMatch[1]
      };
      continue;
    }

    if (
      currentEntry &&
      trimmedLine.length > 0 &&
      !/^##\s+\[/.test(trimmedLine) &&
      !/^###\s+/.test(trimmedLine) &&
      !/^-\s+/.test(trimmedLine) &&
      !/^\[[^\]]+\]:/.test(trimmedLine)
    ) {
      currentEntry.text += ` ${trimmedLine}`;
      continue;
    }

    if (trimmedLine.length === 0) {
      continue;
    }
  }

  finalizeEntry();

  return {
    sections,
    entries
  };
}

function getSectionByVersion(parsedChangelog, version) {
  return parsedChangelog.sections.find((section) => section.version === version) || null;
}

function validateStructuralRules(parsedChangelog, packageVersion) {
  const errors = [];

  const unreleasedSection = getSectionByVersion(parsedChangelog, "Unreleased");
  if (!unreleasedSection) {
    errors.push(
      new Violation(
        "E001",
        "ERROR",
        "Missing required '## [Unreleased]' section.",
        null,
        "Add an Unreleased section at the top of CHANGELOG.md."
      )
    );
  }

  const packageVersionSection = getSectionByVersion(parsedChangelog, packageVersion);
  if (!packageVersionSection) {
    errors.push(
      new Violation(
        "E002",
        "ERROR",
        `Missing changelog section for package.json version '${packageVersion}'.`,
        null,
        `Add '## [${packageVersion}]' to CHANGELOG.md before release.`
      )
    );
  }

  for (const section of parsedChangelog.sections) {
    for (const category of section.categories) {
      if (!VALID_CATEGORIES.has(category.name)) {
        errors.push(
          new Violation(
            "E003",
            "ERROR",
            `Invalid changelog category '${category.name}' in section [${section.version}].`,
            category.line,
            `Use one of: ${Array.from(VALID_CATEGORIES).join(", ")}.`
          )
        );
      }
    }
  }

  return errors;
}

function hasUserImpactHint(entryText) {
  return USER_IMPACT_HINTS.some((pattern) => pattern.test(entryText));
}

function isLikelyInternalOnlyEntry(entryText) {
  const hasInternalPattern = INTERNAL_ONLY_PATTERNS.some((pattern) => pattern.test(entryText));
  return hasInternalPattern && !hasUserImpactHint(entryText);
}

function detectCategoryMismatch(entry) {
  const mismatchRules = CATEGORY_PREFIX_MISMATCH_RULES[entry.category] || [];
  return mismatchRules.some((pattern) => pattern.test(entry.text));
}

function extractBacktickSymbols(entryText) {
  const symbols = new Set();
  const pattern = /`([^`]+)`/g;
  let match = pattern.exec(entryText);

  while (match) {
    const symbol = match[1].trim().toLowerCase();
    if (symbol.length > 0) {
      symbols.add(symbol);
    }
    match = pattern.exec(entryText);
  }

  return symbols;
}

function tokenizeForSimilarity(entryText) {
  return new Set(
    entryText
      .toLowerCase()
      .replace(/https?:\/\/\S+/g, " ")
      .replace(/[^a-z0-9\s]/g, " ")
      .split(/\s+/)
      .map((token) => token.trim())
      .filter((token) => token.length >= 4 && !STOP_WORDS.has(token))
  );
}

function hasTokenOverlap(leftTokens, rightTokens, minimumShared = 2, minimumRatio = 0.6) {
  if (leftTokens.size === 0 || rightTokens.size === 0) {
    return false;
  }

  const shared = [];
  for (const token of leftTokens) {
    if (rightTokens.has(token)) {
      shared.push(token);
    }
  }

  if (shared.length < minimumShared) {
    return false;
  }

  const minSize = Math.min(leftTokens.size, rightTokens.size);
  return shared.length / minSize >= minimumRatio;
}

function areLikelyMutationPair(addedEntry, fixedEntry) {
  const addedSymbols = extractBacktickSymbols(addedEntry.text);
  const fixedSymbols = extractBacktickSymbols(fixedEntry.text);

  for (const symbol of addedSymbols) {
    if (fixedSymbols.has(symbol)) {
      return true;
    }
  }

  const addedTokens = tokenizeForSimilarity(addedEntry.text);
  const fixedTokens = tokenizeForSimilarity(fixedEntry.text);
  return hasTokenOverlap(addedTokens, fixedTokens);
}

function validateHeuristicRules(parsedChangelog) {
  const violations = [];
  const unreleasedSection = getSectionByVersion(parsedChangelog, "Unreleased");

  if (!unreleasedSection) {
    return violations;
  }

  const unreleasedEntries = parsedChangelog.entries.filter(
    (entry) => entry.version === "Unreleased"
  );

  if (unreleasedEntries.length === 0) {
    violations.push(
      new Violation(
        "W001",
        "WARNING",
        "Unreleased section has no entries.",
        unreleasedSection.line,
        "Add at least one user-facing entry when user-visible changes are introduced."
      )
    );
  }

  for (const entry of unreleasedEntries) {
    if (isLikelyInternalOnlyEntry(entry.text)) {
      violations.push(
        new Violation(
          "W002",
          "WARNING",
          "Unreleased entry appears internal-only and may not be user-facing.",
          entry.line,
          "Rewrite the entry around user impact or move internal details to developer docs."
        )
      );
    }

    if (detectCategoryMismatch(entry)) {
      violations.push(
        new Violation(
          "W003",
          "WARNING",
          `Unreleased entry in '${entry.category}' may belong to a different category.`,
          entry.line,
          "Use Added for new capabilities and Fixed for bug corrections."
        )
      );
    }
  }

  const addedEntries = unreleasedEntries.filter((entry) => entry.category === "Added");
  const fixedEntries = unreleasedEntries.filter((entry) => entry.category === "Fixed");

  for (const addedEntry of addedEntries) {
    for (const fixedEntry of fixedEntries) {
      if (!areLikelyMutationPair(addedEntry, fixedEntry)) {
        continue;
      }

      violations.push(
        new Violation(
          "E005",
          "ERROR",
          "Unreleased Added/Fixed entries look like the same change. Mutate the existing unreleased entry instead of stacking bullets.",
          fixedEntry.line,
          `Consider merging line ${fixedEntry.line} into the Added entry at line ${addedEntry.line}.`
        )
      );
    }
  }

  return violations;
}

function isLikelyUserVisiblePath(filePath) {
  const normalizedPath = normalizeRepoPath(filePath);

  if (normalizedPath.length === 0 || normalizedPath === "CHANGELOG.md") {
    return false;
  }

  if (normalizedPath.endsWith(".meta")) {
    return false;
  }

  if (
    normalizedPath.startsWith("Tests/") ||
    normalizedPath.startsWith("scripts/") ||
    normalizedPath.startsWith("docs/") ||
    normalizedPath.startsWith(".github/") ||
    normalizedPath.startsWith(".llm/")
  ) {
    return false;
  }

  if (normalizedPath.startsWith("Runtime/")) {
    return true;
  }

  if (normalizedPath.startsWith("SourceGenerators/")) {
    // Only shipped source-generator/analyzer code should require changelog coverage.
    if (/\/(?:bin|obj)\//.test(normalizedPath)) {
      return false;
    }

    if (/\.Tests(?:\/|$)/.test(normalizedPath)) {
      return false;
    }

    if (normalizedPath === "SourceGenerators/Directory.Build.props") {
      return false;
    }

    if (
      normalizedPath.startsWith("SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators/")
    ) {
      return true;
    }

    if (
      normalizedPath.startsWith("SourceGenerators/WallstopStudios.DxMessaging.Analyzer/Analyzers/")
    ) {
      return true;
    }

    return false;
  }

  if (normalizedPath.startsWith("Samples~/")) {
    return true;
  }

  if (
    normalizedPath.startsWith("Editor/Analyzers/") ||
    normalizedPath.startsWith("Editor/Testing/")
  ) {
    return false;
  }

  if (normalizedPath.startsWith("Editor/")) {
    return true;
  }

  return false;
}

function parseChangedFilesOutput(commandOutput) {
  return normalizeToLf(commandOutput)
    .split("\n")
    .map((filePath) => normalizeRepoPath(filePath))
    .filter(Boolean);
}

function parseChangedFilesStatusOutput(commandOutput) {
  const text = String(commandOutput || "");
  if (text.includes("\0")) {
    const fields = text.split("\0").filter((field) => field.length > 0);
    const files = [];

    for (let index = 0; index < fields.length; index++) {
      const status = fields[index];
      const statusCode = status[0];

      if (statusCode === "R" || statusCode === "C") {
        files.push(normalizeRepoPath(fields[index + 1]));
        files.push(normalizeRepoPath(fields[index + 2]));
        index += 2;
        continue;
      }

      files.push(normalizeRepoPath(fields[index + 1]));
      index++;
    }

    return files.filter(Boolean);
  }

  return normalizeToLf(text)
    .split("\n")
    .flatMap((line) => {
      const fields = line.split("\t").filter((field) => field.length > 0);
      if (fields.length < 2) {
        return [];
      }

      const statusCode = fields[0][0];
      if (statusCode === "R" || statusCode === "C") {
        return [normalizeRepoPath(fields[1]), normalizeRepoPath(fields[2])];
      }

      return [normalizeRepoPath(fields[1])];
    })
    .filter(Boolean);
}

function formatGitFailure(error) {
  if (!error) {
    return "unknown failure";
  }

  const stderr = typeof error.stderr === "string" ? error.stderr.trim() : "";
  if (stderr.length > 0) {
    return stderr.split("\n")[0];
  }

  return error.message || String(error);
}

function getChangedFilesFromGitDetails(execFileSyncImpl = execFileSync, env = process.env) {
  const attemptedSources = [];
  const failures = [];

  const runGit = (source, args, parseOutput = parseChangedFilesOutput) => {
    attemptedSources.push(source);

    try {
      const output = execFileSyncImpl("git", args, {
        cwd: REPO_ROOT,
        encoding: "utf8",
        stdio: ["ignore", "pipe", "pipe"]
      });

      return {
        ok: true,
        files: parseOutput(output)
      };
    } catch (error) {
      failures.push({
        source,
        command: `git ${args.join(" ")}`,
        message: formatGitFailure(error)
      });

      return {
        ok: false,
        files: []
      };
    }
  };

  const mergeUniquePaths = (...pathLists) => {
    const merged = [];
    const seen = new Set();

    for (const pathList of pathLists) {
      for (const filePath of pathList) {
        if (seen.has(filePath)) {
          continue;
        }

        seen.add(filePath);
        merged.push(filePath);
      }
    }

    return merged;
  };

  const runGitStatus = (source, args) =>
    runGit(source, ["diff", "-z", "--name-status", "-M", ...args], parseChangedFilesStatusOutput);

  const staged = runGitStatus("staged", ["--cached"]);
  if (!staged.ok) {
    return {
      files: [],
      source: "unavailable",
      attemptedSources,
      failures
    };
  }

  const isCiEnvironment =
    String(env.CI || "").toLowerCase() === "true" || String(env.GITHUB_ACTIONS || "") === "true";

  if (!isCiEnvironment) {
    const unstaged = runGitStatus("unstaged", []);
    const untracked = runGit("untracked", ["ls-files", "--others", "--exclude-standard"]);
    const files = mergeUniquePaths(
      staged.files,
      unstaged.ok ? unstaged.files : [],
      untracked.ok ? untracked.files : []
    );
    if (!unstaged.ok || !untracked.ok) {
      return {
        files,
        source: "unavailable",
        attemptedSources,
        failures
      };
    }

    return {
      files,
      source: files.length > 0 ? "local" : "local-empty",
      attemptedSources,
      failures
    };
  }

  if (staged.files.length > 0) {
    return {
      files: staged.files,
      source: "staged",
      attemptedSources,
      failures
    };
  }

  if (
    env.GITHUB_EVENT_NAME === "pull_request" &&
    typeof env.GITHUB_BASE_REF === "string" &&
    env.GITHUB_BASE_REF.length > 0
  ) {
    const baseRef = `origin/${env.GITHUB_BASE_REF}`;
    const pr = runGitStatus("pull-request", [`${baseRef}...HEAD`]);
    if (pr.ok) {
      return {
        files: pr.files,
        source: pr.files.length > 0 ? "pull-request" : "pull-request-empty",
        attemptedSources,
        failures
      };
    }

    return {
      files: [],
      source: "unavailable",
      attemptedSources,
      failures
    };
  }

  if (
    env.GITHUB_EVENT_NAME === "push" &&
    typeof env.GITHUB_EVENT_BEFORE === "string" &&
    env.GITHUB_EVENT_BEFORE &&
    !/^0+$/.test(env.GITHUB_EVENT_BEFORE)
  ) {
    const push = runGitStatus("push", [`${env.GITHUB_EVENT_BEFORE}...HEAD`]);
    if (push.ok) {
      return {
        files: push.files,
        source: push.files.length > 0 ? "push" : "push-empty",
        attemptedSources,
        failures
      };
    }

    return {
      files: [],
      source: "unavailable",
      attemptedSources,
      failures
    };
  }

  const fallback = runGitStatus("head-fallback", ["HEAD~1...HEAD"]);
  return {
    files: fallback.ok ? fallback.files : [],
    source: fallback.ok
      ? fallback.files.length > 0
        ? "head-fallback"
        : "head-fallback-empty"
      : "unavailable",
    attemptedSources,
    failures
  };
}

function getChangedFilesFromGit(execFileSyncImpl = execFileSync, env = process.env) {
  return getChangedFilesFromGitDetails(execFileSyncImpl, env).files;
}

function describeChangedFilesSource(diagnostics) {
  if (!diagnostics || !diagnostics.source) {
    return "";
  }

  const attempted =
    Array.isArray(diagnostics.attemptedSources) && diagnostics.attemptedSources.length > 0
      ? `; attempted sources: ${diagnostics.attemptedSources.join(", ")}`
      : "";

  return ` Changed-file source: ${diagnostics.source}${attempted}.`;
}

function probeShallowCloneState(execFileSyncImpl = execFileSync) {
  const probe = (args) => {
    try {
      const output = execFileSyncImpl("git", args, {
        cwd: REPO_ROOT,
        encoding: "utf8",
        stdio: ["ignore", "pipe", "pipe"]
      });
      return { ok: true, output: String(output || "").trim() };
    } catch (error) {
      return {
        ok: false,
        output: "",
        error: error && error.message ? error.message : String(error)
      };
    }
  };

  const isShallow = probe(["rev-parse", "--is-shallow-repository"]);
  const originRefs = probe(["for-each-ref", "--format=%(refname)", "refs/remotes/origin"]);

  return {
    isShallow: isShallow.ok ? isShallow.output === "true" : null,
    originRefs:
      originRefs.ok && originRefs.output.length > 0
        ? originRefs.output
            .split("\n")
            .map((line) => line.trim())
            .filter(Boolean)
        : [],
    originRefsProbeError: originRefs.ok ? null : originRefs.error
  };
}

function formatShallowCloneDiagnostic(shallowState) {
  if (!shallowState) {
    return "";
  }

  const parts = [];
  if (shallowState.isShallow === true) {
    parts.push("Repository is a SHALLOW clone (git rev-parse --is-shallow-repository = true)");
  } else if (shallowState.isShallow === false) {
    parts.push("Repository is a full clone (not shallow)");
  } else {
    parts.push("Shallow-state probe could not run");
  }

  if (shallowState.originRefs && shallowState.originRefs.length > 0) {
    parts.push(`origin refs present: ${shallowState.originRefs.join(", ")}`);
  } else {
    parts.push("origin refs present: <none>");
  }

  return ` ${parts.join("; ")}.`;
}

function validateChangedFilesDiscovery(diagnostics, shallowProbeImpl = probeShallowCloneState) {
  if (!diagnostics || diagnostics.source !== "unavailable") {
    return [];
  }

  const failureSummary =
    diagnostics.failures && diagnostics.failures.length > 0
      ? diagnostics.failures
          .map((failure) => `${failure.source}: ${failure.command} (${failure.message})`)
          .join("; ")
      : "no Git sources were available";

  const shallowState = typeof shallowProbeImpl === "function" ? shallowProbeImpl() : null;
  const shallowDiagnostic = formatShallowCloneDiagnostic(shallowState);

  // Build a suggestion that NAMES the fix when the shallow-clone signature
  // is present. Operators saw "Fix the checkout/fetch configuration" and had
  // no idea this meant fetch-depth: 0 in their workflow's checkout step.
  const suggestionLines = [];
  if (shallowState && shallowState.isShallow === true) {
    suggestionLines.push(
      "Set fetch-depth: 0 on actions/checkout (shallow clones do not have origin/master / origin/<base> refs needed for changelog coverage)."
    );
  } else if (shallowState && shallowState.originRefs && shallowState.originRefs.length === 0) {
    suggestionLines.push(
      "No remote-tracking refs were found. If running in CI, set fetch-depth: 0 on actions/checkout so origin/<base> is fetched."
    );
  } else {
    suggestionLines.push(
      "Fix the checkout/fetch configuration (set fetch-depth: 0 on actions/checkout) or pass --changed-file explicitly."
    );
  }

  suggestionLines.push(`Git failures: ${failureSummary}.${shallowDiagnostic}`);

  return [
    new Violation(
      "E006",
      "ERROR",
      "Unable to determine changed files for changelog coverage.",
      null,
      suggestionLines.join(" ")
    )
  ];
}

function validateCoverageRule(changedFiles, diagnostics = null) {
  const discoveryErrors = validateChangedFilesDiscovery(diagnostics);
  if (discoveryErrors.length > 0) {
    return discoveryErrors;
  }

  if (!Array.isArray(changedFiles) || changedFiles.length === 0) {
    return [];
  }

  const normalizedChangedFiles = changedFiles
    .map((filePath) => normalizeRepoPath(filePath))
    .filter(Boolean);
  const changelogChanged = normalizedChangedFiles.includes("CHANGELOG.md");
  const userVisibleFiles = normalizedChangedFiles.filter((filePath) =>
    isLikelyUserVisiblePath(filePath)
  );

  if (userVisibleFiles.length === 0 || changelogChanged) {
    return [];
  }

  return [
    new Violation(
      "E004",
      "ERROR",
      "Likely user-visible files changed without a CHANGELOG.md update.",
      null,
      `Add or mutate an Unreleased changelog entry. Trigger files: ${userVisibleFiles.join(", ")}.${describeChangedFilesSource(diagnostics)}`
    )
  ];
}

function validateChangelogPolicy({
  changelogContent,
  packageJsonContent,
  checkCoverage = false,
  changedFiles = [],
  changedFilesDiagnostics = null
}) {
  const parsedChangelog = parseChangelog(changelogContent);
  const packageVersion = parsePackageVersion(packageJsonContent);

  const errors = [...validateStructuralRules(parsedChangelog, packageVersion)];

  if (checkCoverage) {
    errors.push(...validateCoverageRule(changedFiles, changedFilesDiagnostics));
  }

  const heuristicViolations = validateHeuristicRules(parsedChangelog);

  const heuristicErrors = heuristicViolations.filter((violation) => violation.severity === "ERROR");
  const warnings = heuristicViolations.filter((violation) => violation.severity !== "ERROR");

  errors.push(...heuristicErrors);

  return {
    errors,
    warnings,
    parsedChangelog,
    packageVersion
  };
}

function printUsage() {
  console.log("Usage: node scripts/validate-changelog.js [options] [changed-file ...]");
  console.log("");
  console.log("Options:");
  console.log(
    "  --check-coverage           Fail when likely user-visible changes do not update CHANGELOG.md."
  );
  console.log(
    "  --changed-file <path>      Add a changed file path for coverage checks (repeatable)."
  );
  console.log("  --changelog <path>         Use a custom CHANGELOG path.");
  console.log("  --package-json <path>      Use a custom package.json path.");
  console.log("  --strict-warnings          Treat warnings as failures (exit code 1).");
  console.log("  --help                     Show this help output.");
}

function main() {
  let options;

  try {
    options = parseArgs(process.argv.slice(2));
  } catch (error) {
    console.error(`ERROR: ${error.message}`);
    printUsage();
    process.exit(1);
  }

  if (options.help) {
    printUsage();
    process.exit(0);
  }

  let changelogContent;
  let packageJsonContent;

  try {
    changelogContent = fs.readFileSync(options.changelogPath, "utf8");
  } catch (error) {
    console.error(
      `ERROR: Unable to read changelog file '${options.changelogPath}': ${error.message}`
    );
    process.exit(1);
  }

  try {
    packageJsonContent = fs.readFileSync(options.packageJsonPath, "utf8");
  } catch (error) {
    console.error(
      `ERROR: Unable to read package.json file '${options.packageJsonPath}': ${error.message}`
    );
    process.exit(1);
  }

  const changedFilesDiagnostics =
    options.checkCoverage && options.changedFiles.length === 0
      ? getChangedFilesFromGitDetails()
      : null;
  const resolvedChangedFiles = options.checkCoverage
    ? options.changedFiles.length > 0
      ? options.changedFiles
      : changedFilesDiagnostics.files
    : [];

  let result;
  try {
    result = validateChangelogPolicy({
      changelogContent,
      packageJsonContent,
      checkCoverage: options.checkCoverage,
      changedFiles: resolvedChangedFiles,
      changedFilesDiagnostics
    });
  } catch (error) {
    console.error(`ERROR: ${error.message}`);
    process.exit(1);
  }

  const hasErrors = result.errors.length > 0;
  const hasWarnings = result.warnings.length > 0;

  if (!hasErrors && !hasWarnings) {
    console.log("PASS: changelog policy validation passed.");
    process.exit(0);
  }

  if (hasErrors) {
    console.error(`ERRORS (${result.errors.length}):`);
    for (const error of result.errors) {
      console.error(`- ${error.toString()}`);
    }
  }

  if (hasWarnings) {
    console.log(`WARNINGS (${result.warnings.length}):`);
    for (const warning of result.warnings) {
      console.log(`- ${warning.toString()}`);
    }
  }

  if (hasErrors || (options.strictWarnings && hasWarnings)) {
    process.exit(1);
  }

  process.exit(0);
}

module.exports = {
  CHANGELOG_PATH,
  PACKAGE_JSON_PATH,
  VALID_CATEGORIES,
  Violation,
  normalizeRepoPath,
  parseArgs,
  parsePackageVersion,
  parseChangelog,
  getSectionByVersion,
  validateStructuralRules,
  isLikelyInternalOnlyEntry,
  detectCategoryMismatch,
  extractBacktickSymbols,
  tokenizeForSimilarity,
  hasTokenOverlap,
  areLikelyMutationPair,
  validateHeuristicRules,
  isLikelyUserVisiblePath,
  parseChangedFilesOutput,
  parseChangedFilesStatusOutput,
  getChangedFilesFromGitDetails,
  getChangedFilesFromGit,
  probeShallowCloneState,
  formatShallowCloneDiagnostic,
  validateChangedFilesDiscovery,
  validateCoverageRule,
  validateChangelogPolicy,
  main
};

if (require.main === module) {
  main();
}
