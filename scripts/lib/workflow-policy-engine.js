"use strict";

/**
 * workflow-policy-engine
 *
 * Pure, side-effect-free parsing primitives for GitHub Actions workflow YAML:
 * structural job/step/run-block extraction, indentation helpers, path-filter
 * blocks, concurrency / matrix / needs / runs-on parsing, scalar + label
 * normalization, and the shared `Violation` value type and lazy `yaml` loader.
 *
 * These functions operate on an already-split array of LF lines (or raw text)
 * and never read the filesystem, instantiate policy, or depend on validator
 * configuration. validate-workflows.js composes its policy rules on top.
 */

// The `yaml` package parses workflows structurally (formatting-invariant) for
// callers that need a real AST rather than line scanning. It is not a declared
// dependency of this package; it arrives transitively through devDependencies
// (cspell, markdownlint-cli2). Resolve it lazily and return the outcome so a
// caller can surface a missing module as an actionable policy result instead of
// crashing with an uncaught MODULE_NOT_FOUND. The result is cached (module on
// success, Error on failure) so the require runs at most once.
let cachedYamlModule;

/**
 * Loads the `yaml` package, caching the result across calls.
 *
 * @returns {{ module: object } | { error: Error }} The loaded module, or the
 *   load error when `yaml` cannot be resolved.
 */
function loadYamlModule() {
  if (cachedYamlModule === undefined) {
    try {
      cachedYamlModule = { module: require("yaml") };
    } catch (error) {
      cachedYamlModule = { error: error instanceof Error ? error : new Error(String(error)) };
    }
  }
  return cachedYamlModule;
}

/**
 * Represents a validation violation.
 */
class Violation {
  constructor(file, line, pattern, message, severity = "error") {
    this.file = file;
    this.line = line;
    this.pattern = pattern;
    this.message = message;
    this.severity = severity;
  }

  toString() {
    const prefix = this.severity === "error" ? "ERROR" : "WARN";
    return `[${prefix}] ${this.file}:${this.line}: ${this.message}\n  Pattern: ${this.pattern}`;
  }
}

function parseYamlBoolean(rawValue) {
  if (typeof rawValue !== "string") {
    return null;
  }

  const normalized = rawValue.trim().toLowerCase();
  if (normalized === "true") {
    return true;
  }
  if (normalized === "false") {
    return false;
  }

  return null;
}

function getIndent(line) {
  return line.length - line.trimStart().length;
}

function extractWorkflowPathEntries(lines) {
  return extractWorkflowPathBlocks(lines).flatMap((block) => block.entries);
}

function extractWorkflowPathBlocks(lines) {
  return extractWorkflowPathFilterBlocks(lines, "paths");
}

function extractWorkflowPathIgnoreBlocks(lines) {
  return extractWorkflowPathFilterBlocks(lines, "paths-ignore");
}

function extractWorkflowPathFilterBlocks(lines, filterKey) {
  const blocks = [];
  let currentBlock = null;
  const filterKeyPattern = new RegExp(`^\\s*${filterKey}:\\s*$`);

  const startBlock = (lineNumber, indent) => {
    currentBlock = {
      line: lineNumber,
      indent,
      entries: []
    };
    blocks.push(currentBlock);
  };

  const stopBlock = () => {
    currentBlock = null;
  };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (!currentBlock && filterKeyPattern.test(line)) {
      startBlock(i + 1, indent);
      continue;
    }

    if (!currentBlock) {
      continue;
    }

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= currentBlock.indent && !/^\s*-\s+/.test(line)) {
      stopBlock();

      if (filterKeyPattern.test(line)) {
        startBlock(i + 1, indent);
      }
      continue;
    }

    const pathEntry = /^\s*-\s*["']?([^"'#]+)["']?\s*(?:#.*)?$/.exec(line);
    if (pathEntry) {
      currentBlock.entries.push({
        line: i + 1,
        path: pathEntry[1].trim()
      });
    }
  }

  return blocks;
}

function normalizeWorkflowPathPattern(pathValue) {
  return pathValue.replace(/\\/g, "/").replace(/^\.\//, "");
}

function escapeRegexChar(char) {
  return /[\\^$+?.()|[\]{}]/.test(char) ? `\\${char}` : char;
}

function workflowPathGlobToRegex(pattern) {
  let source = "";
  const normalized = normalizeWorkflowPathPattern(pattern);

  for (let index = 0; index < normalized.length; index++) {
    const char = normalized[index];
    if (char === "*") {
      if (normalized[index + 1] === "*") {
        source += ".*";
        index++;
      } else {
        source += "[^/]*";
      }
      continue;
    }
    if (char === "?") {
      source += "[^/]";
      continue;
    }
    source += escapeRegexChar(char);
  }

  return new RegExp(`^${source}$`);
}

function extractRunBlocks(lines) {
  const blocks = [];

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const blockRunMatch = /^(\s*)(?:-\s+)?run:\s*([>|])[+-]?\s*$/.exec(line);

    if (blockRunMatch) {
      const baseIndent = blockRunMatch[1].length;
      const blockLines = [];
      let j = i + 1;

      while (j < lines.length) {
        const nextLine = lines[j];
        const trimmed = nextLine.trim();
        const nextIndent = getIndent(nextLine);

        if (trimmed.length > 0 && nextIndent <= baseIndent) {
          break;
        }

        blockLines.push(nextLine.trim());
        j++;
      }

      blocks.push({
        startLine: i + 1,
        text: blockLines.join("\n").trim()
      });

      i = j - 1;
      continue;
    }

    const inlineRunMatch = /^\s*(?:-\s+)?run:\s*(.+?)\s*$/.exec(line);
    if (inlineRunMatch) {
      blocks.push({
        startLine: i + 1,
        text: inlineRunMatch[1].trim()
      });
    }
  }

  return blocks;
}

function extractJobs(lines) {
  const jobs = [];
  let inJobsBlock = false;
  let jobsIndent = -1;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (!inJobsBlock && /^\s*jobs:\s*$/.test(line)) {
      inJobsBlock = true;
      jobsIndent = indent;
      continue;
    }

    if (!inJobsBlock) {
      continue;
    }

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= jobsIndent) {
      break;
    }

    const jobHeader = /^\s*([A-Za-z0-9_-]+):\s*$/.exec(line);
    if (!jobHeader || indent !== jobsIndent + 2) {
      continue;
    }

    let endLine = lines.length - 1;
    for (let j = i + 1; j < lines.length; j++) {
      const nextLine = lines[j];
      const nextTrimmed = nextLine.trim();
      const nextIndent = getIndent(nextLine);

      if (nextTrimmed.length === 0 || nextTrimmed.startsWith("#")) {
        continue;
      }

      if (nextIndent <= jobsIndent) {
        endLine = j - 1;
        break;
      }

      if (nextIndent === jobsIndent + 2 && /^\s*[A-Za-z0-9_-]+:\s*$/.test(nextLine)) {
        endLine = j - 1;
        break;
      }
    }

    jobs.push({
      id: jobHeader[1],
      startLine: i + 1,
      endLine: endLine + 1,
      indent
    });

    i = endLine;
  }

  return jobs;
}

function extractDefaultRunShellFromBlock(lines, startIndex, endIndex, defaultsIndent) {
  let runIndent = -1;

  for (let i = startIndex + 1; i <= endIndex && i < lines.length; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= defaultsIndent) {
      break;
    }

    if (runIndent === -1 && /^\s*run:\s*$/.test(line) && indent === defaultsIndent + 2) {
      runIndent = indent;
      continue;
    }

    if (runIndent !== -1) {
      if (indent <= runIndent) {
        break;
      }

      const shellMatch = /^\s*shell:\s*["']?([^"'\s#]+)["']?\s*(?:#.*)?$/.exec(line);
      if (shellMatch && indent === runIndent + 2) {
        return shellMatch[1].toLowerCase();
      }
    }
  }

  return null;
}

function extractWorkflowDefaultsShell(lines) {
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (!/^\s*defaults:\s*$/.test(line) || getIndent(line) !== 0) {
      continue;
    }

    return extractDefaultRunShellFromBlock(lines, i, lines.length - 1, 0);
  }

  return null;
}

function extractJobDefaultsShell(lines, job) {
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= job.indent) {
      break;
    }

    if (!/^\s*defaults:\s*$/.test(line) || indent !== job.indent + 2) {
      continue;
    }

    return extractDefaultRunShellFromBlock(lines, i, endIndex, indent);
  }

  return null;
}

function jobTargetsWindows(lines, job) {
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;
  let runsOnValue = null;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= job.indent) {
      break;
    }

    if (indent !== job.indent + 2) {
      continue;
    }

    const runsOnMatch = /^\s*runs-on:\s*(.+?)\s*$/.exec(line);
    if (!runsOnMatch) {
      continue;
    }

    runsOnValue = runsOnMatch[1].trim();
    break;
  }

  if (!runsOnValue) {
    return false;
  }

  if (/\bwindows(?:-[a-z0-9]+)?\b/i.test(runsOnValue)) {
    return true;
  }

  if (!/matrix\./i.test(runsOnValue)) {
    return false;
  }

  let inMatrixBlock = false;
  let matrixIndent = -1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (!inMatrixBlock && /^\s*matrix:\s*$/.test(line)) {
      inMatrixBlock = true;
      matrixIndent = indent;
      continue;
    }

    if (!inMatrixBlock) {
      continue;
    }

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= matrixIndent) {
      inMatrixBlock = false;
      matrixIndent = -1;
      continue;
    }

    if (/\bwindows(?:-[a-z0-9]+)?\b/i.test(line)) {
      return true;
    }
  }

  return false;
}

function extractStepRun(lines, stepStartIndex, stepEndIndex) {
  for (let i = stepStartIndex; i <= stepEndIndex; i++) {
    const line = lines[i];
    const blockRunMatch = /^(\s*)(?:-\s+)?run:\s*([>|])[+-]?\s*$/.exec(line);

    if (blockRunMatch) {
      const baseIndent = blockRunMatch[1].length;
      const blockLines = [];
      let j = i + 1;

      while (j <= stepEndIndex) {
        const nextLine = lines[j];
        const trimmed = nextLine.trim();
        const nextIndent = getIndent(nextLine);

        if (trimmed.length > 0 && nextIndent <= baseIndent) {
          break;
        }

        blockLines.push(nextLine.trim());
        j++;
      }

      return {
        line: i + 1,
        style: blockRunMatch[2],
        text: blockLines.join("\n").trim()
      };
    }

    const inlineRunMatch = /^\s*(?:-\s+)?run:\s*(.+?)\s*$/.exec(line);
    if (inlineRunMatch) {
      return {
        line: i + 1,
        style: null,
        text: inlineRunMatch[1].trim()
      };
    }
  }

  return null;
}

function extractStepShell(lines, stepStartIndex, stepEndIndex) {
  for (let i = stepStartIndex; i <= stepEndIndex; i++) {
    const line = lines[i];

    // Quoted shell string -- captures everything between matching quotes,
    // including values with spaces (e.g. the Git Bash absolute-path escape
    // hatch). Matching the full quoted span avoids truncating at the first
    // space.
    const quotedMatch = /^\s*shell:\s*(['"])(.*?)\1\s*(?:#.*)?$/.exec(line);
    if (quotedMatch) {
      return quotedMatch[2].toLowerCase();
    }

    // Unquoted (single token) shell value: `shell: pwsh`, `shell: bash`.
    const bareMatch = /^\s*shell:\s*([^\s'"#]+)\s*(?:#.*)?$/.exec(line);
    if (bareMatch) {
      return bareMatch[1].toLowerCase();
    }
  }

  return null;
}

function extractStepUses(lines, stepStartIndex, stepEndIndex) {
  for (let i = stepStartIndex; i <= stepEndIndex; i++) {
    const line = lines[i];
    const usesMatch = /^\s*(?:-\s+)?uses:\s*["']?([^"'\s#]+)["']?\s*(?:#.*)?$/i.exec(line);

    if (usesMatch) {
      return usesMatch[1].toLowerCase();
    }
  }

  return null;
}

function extractStepName(lines, stepStartIndex, stepEndIndex) {
  for (let i = stepStartIndex; i <= stepEndIndex; i++) {
    const line = lines[i];
    const nameMatch = /^\s*(?:-\s+)?name:\s*["']?(.+?)["']?\s*(?:#.*)?$/i.exec(line);

    if (nameMatch) {
      return nameMatch[1].trim();
    }
  }

  return null;
}

function extractStepIf(lines, stepStartIndex, stepEndIndex) {
  for (let i = stepStartIndex; i <= stepEndIndex; i++) {
    const line = lines[i];
    const ifMatch = /^\s*if:\s*(.+?)\s*(?:#.*)?$/i.exec(line);

    if (ifMatch) {
      return ifMatch[1].trim();
    }
  }

  return null;
}

function extractStepWithMap(lines, stepStartIndex, stepEndIndex) {
  const values = new Map();
  let withIndent = -1;

  for (let i = stepStartIndex; i <= stepEndIndex; i++) {
    const line = lines[i];
    if (/^\s*with:\s*(?:#.*)?$/i.test(line)) {
      withIndent = getIndent(line);
      continue;
    }

    if (withIndent === -1) {
      continue;
    }

    const trimmed = line.trim();
    const indent = getIndent(line);
    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }
    if (indent <= withIndent) {
      break;
    }

    const keyMatch = /^\s*([A-Za-z0-9_-]+)\s*:\s*(.*?)\s*(?:#.*)?$/.exec(line);
    if (keyMatch && indent === withIndent + 2) {
      values.set(keyMatch[1], keyMatch[2].replace(/^["']|["']$/g, ""));
    }
  }

  return values;
}

function extractStepEnvMap(lines, stepStartIndex, stepEndIndex) {
  const values = new Map();
  let envIndent = -1;
  const stepPropertyIndent = getIndent(lines[stepStartIndex]) + 2;

  for (let i = stepStartIndex; i <= stepEndIndex; i++) {
    const line = lines[i];
    if (/^\s*env:\s*(?:#.*)?$/i.test(line) && getIndent(line) === stepPropertyIndent) {
      envIndent = getIndent(line);
      continue;
    }

    if (envIndent === -1) {
      continue;
    }

    const trimmed = line.trim();
    const indent = getIndent(line);
    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }
    if (indent <= envIndent) {
      break;
    }

    const keyMatch = /^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(.*?)\s*(?:#.*)?$/.exec(line);
    if (keyMatch && indent === envIndent + 2) {
      values.set(keyMatch[1], keyMatch[2].replace(/^["']|["']$/g, ""));
    }
  }

  return values;
}

function extractJobSteps(lines, job) {
  const steps = [];
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;
  let stepsStartIndex = -1;
  let stepsIndent = -1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    if (/^\s*steps:\s*$/.test(line) && getIndent(line) === job.indent + 2) {
      stepsStartIndex = i;
      stepsIndent = getIndent(line);
      break;
    }
  }

  if (stepsStartIndex === -1) {
    return steps;
  }

  let i = stepsStartIndex + 1;
  while (i <= endIndex) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      i++;
      continue;
    }

    if (indent <= stepsIndent) {
      break;
    }

    if (!(indent === stepsIndent + 2 && /^\s*-\s+/.test(line))) {
      i++;
      continue;
    }

    const stepStartIndex = i;
    let stepEndIndex = endIndex;

    for (let j = i + 1; j <= endIndex; j++) {
      const nextLine = lines[j];
      const nextTrimmed = nextLine.trim();
      const nextIndent = getIndent(nextLine);

      if (nextTrimmed.length === 0 || nextTrimmed.startsWith("#")) {
        continue;
      }

      if (nextIndent <= stepsIndent) {
        stepEndIndex = j - 1;
        break;
      }

      if (nextIndent === stepsIndent + 2 && /^\s*-\s+/.test(nextLine)) {
        stepEndIndex = j - 1;
        break;
      }
    }

    const run = extractStepRun(lines, stepStartIndex, stepEndIndex);
    steps.push({
      startIndex: stepStartIndex,
      endIndex: stepEndIndex,
      name: extractStepName(lines, stepStartIndex, stepEndIndex),
      if: extractStepIf(lines, stepStartIndex, stepEndIndex),
      shell: extractStepShell(lines, stepStartIndex, stepEndIndex),
      uses: extractStepUses(lines, stepStartIndex, stepEndIndex),
      with: extractStepWithMap(lines, stepStartIndex, stepEndIndex),
      env: extractStepEnvMap(lines, stepStartIndex, stepEndIndex),
      run
    });

    i = stepEndIndex + 1;
  }

  return steps;
}

/**
 * Invokes `callback(job, steps)` for each top-level job, in declaration order,
 * with `steps` parsed via {@link extractJobSteps}. Use this when a policy needs
 * the whole step array per job (cross-step ordering, find/filter over the
 * array, per-job state); use {@link forEachJobStep} when each step is checked
 * independently. Callback return values are ignored; there is no early-exit
 * protocol.
 *
 * @param {string[]} lines - Workflow file content split into lines
 * @param {(job: object, steps: object[]) => void} callback - Invoked once per job
 * @returns {void}
 */
function forEachJob(lines, callback) {
  for (const job of extractJobs(lines)) {
    callback(job, extractJobSteps(lines, job));
  }
}

/**
 * Invokes `callback(step, job)` for every step of every top-level job,
 * flattening {@link forEachJob} in declaration order. Use this when a policy
 * inspects each step independently of its siblings. Callback return values are
 * ignored; there is no early-exit protocol.
 *
 * @param {string[]} lines - Workflow file content split into lines
 * @param {(step: object, job: object) => void} callback - Invoked once per step
 * @returns {void}
 */
function forEachJobStep(lines, callback) {
  forEachJob(lines, (job, steps) => {
    for (const step of steps) {
      callback(step, job);
    }
  });
}

/**
 * Extracts a concurrency.group value (string) and line number from a YAML
 * block whose key starts at the given containing indent (so any directly-
 * nested key sits at containingIndent + 2). Returns null when no
 * concurrency block / group key is found.
 *
 * Supports three forms:
 *
 *   1. Multi-line mapping form:
 *
 *        concurrency:
 *          group: foo
 *          cancel-in-progress: false
 *
 *   2. Inline mapping form:
 *
 *        concurrency: { group: foo, cancel-in-progress: false }
 *
 *   3. Scalar shorthand form (GitHub Actions treats the entire value as the
 *      group name; cancel-in-progress is implicitly false / the default):
 *
 *        concurrency: foo
 *        concurrency: "foo"
 *        concurrency: 'foo'
 *
 *   The shorthand form is detected too, so a single line `concurrency:
 *   wallstop-organization-builds` resolves to that group. For shorthand the
 *   returned cancelInProgress is undefined (GitHub Actions defaults the field).
 *
 * Returns { group, line, cancelInProgress, queue } where line is 1-indexed.
 */
function extractConcurrencyGroupFromBlock(lines, startIndex, endIndex, containingIndent) {
  const targetIndent = containingIndent + 2;

  for (let i = startIndex; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    // Workflow-level scanning passes containingIndent = -2 (so target
    // indent is 0). We still need to stop when we leave the block.
    if (indent < targetIndent && i !== startIndex) {
      break;
    }

    if (indent !== targetIndent) {
      continue;
    }

    // Inline mapping form: `concurrency: { group: foo, ... }`
    const inlineMapMatch = /^\s*concurrency:\s*\{([^}]*)\}\s*(?:#.*)?$/.exec(line);
    if (inlineMapMatch) {
      const inner = inlineMapMatch[1];
      const groupMatch = /\bgroup\s*:\s*["']?([^,"'}]+?)["']?\s*(?:,|$)/.exec(inner);
      const cancelMatch = /\bcancel-in-progress\s*:\s*(true|false)/.exec(inner);
      const queueMatch = /\bqueue\s*:\s*["']?([^,"'}]+?)["']?\s*(?:,|$)/.exec(inner);
      if (groupMatch) {
        return {
          group: groupMatch[1].trim(),
          line: i + 1,
          cancelInProgress: cancelMatch ? cancelMatch[1] === "true" : undefined,
          queue: queueMatch ? queueMatch[1].trim() : undefined
        };
      }
      return null;
    }

    // Multi-line block form: bare `concurrency:` followed by indented mapping.
    if (/^\s*concurrency:\s*(?:#.*)?$/.test(line)) {
      const concurrencyIndent = indent;
      let group = null;
      let groupLine = -1;
      let cancelInProgress;
      let queue;
      for (let j = i + 1; j <= endIndex; j++) {
        const childLine = lines[j];
        const childTrimmed = childLine.trim();
        const childIndent = getIndent(childLine);

        if (childTrimmed.length === 0 || childTrimmed.startsWith("#")) {
          continue;
        }

        if (childIndent <= concurrencyIndent) {
          break;
        }

        const childGroupMatch = /^\s*group\s*:\s*["']?(.+?)["']?\s*(?:#.*)?$/.exec(childLine);
        if (childGroupMatch && group === null) {
          group = childGroupMatch[1].trim();
          groupLine = j + 1;
          continue;
        }
        const childCancelMatch = /^\s*cancel-in-progress\s*:\s*(true|false)\s*(?:#.*)?$/.exec(
          childLine
        );
        if (childCancelMatch) {
          cancelInProgress = childCancelMatch[1] === "true";
        }
        const childQueueMatch = /^\s*queue\s*:\s*["']?(.+?)["']?\s*(?:#.*)?$/.exec(childLine);
        if (childQueueMatch) {
          queue = childQueueMatch[1].trim();
        }
      }

      if (group !== null) {
        return { group, line: groupLine, cancelInProgress, queue };
      }
      return null;
    }

    // Scalar shorthand form: `concurrency: <name>` where the value is a
    // bare identifier or quoted string (NOT an inline mapping, NOT a
    // YAML block/folded scalar, NOT empty, NOT the YAML null marker).
    const shorthandMatch = /^\s*concurrency:\s*(.+?)\s*(?:#.*)?$/.exec(line);
    if (shorthandMatch) {
      const rawValue = shorthandMatch[1].trim();
      // Skip non-scalar leaders: inline mapping `{`, block/folded
      // scalars `|`/`>`, YAML null `~`, or empty (the multi-line case
      // we already returned for above).
      if (
        rawValue.length === 0 ||
        rawValue === "~" ||
        rawValue.startsWith("{") ||
        rawValue.startsWith("|") ||
        rawValue.startsWith(">")
      ) {
        continue;
      }
      const stripped = rawValue.replace(/^(["'])(.*)\1$/, "$2");
      if (stripped.length === 0) {
        continue;
      }
      return {
        group: stripped,
        line: i + 1,
        cancelInProgress: undefined,
        queue: undefined
      };
    }
  }

  return null;
}

/**
 * Extracts a job's concurrency.group value (string) and line number, or
 * null when no concurrency block / group key is found. A thin wrapper over
 * extractConcurrencyGroupFromBlock for clarity at call sites.
 */
function extractJobConcurrencyGroup(lines, job) {
  return extractConcurrencyGroupFromBlock(
    lines,
    job.startLine, // 1-indexed job header; first child sits at startLine + 0 (0-indexed = job.startLine)
    job.endLine - 1,
    job.indent
  );
}

/**
 * Extracts a workflow-level (top-level) concurrency.group value and line
 * number, or null when no top-level concurrency block / group key is
 * found. Workflow-level concurrency applies to the whole workflow run,
 * not per-matrix-entry; callers use it to inspect the single workflow-level
 * concurrency group.
 */
function extractWorkflowConcurrencyGroup(lines) {
  return extractConcurrencyGroupFromBlock(lines, 0, lines.length - 1, -2);
}

/**
 * Returns true when the job declares a `strategy.matrix:` block. Detects the
 * standard multi-line form; this is the only form actually used in this repo.
 *
 * Limitation: the flow-style mapping form `strategy: { matrix: {...},
 * max-parallel: 1 }` is silently unanalyzable by this helper and would
 * return `false`. No active workflow uses flow style; if a future author
 * introduces it, expand this helper before relying on it.
 */
function jobHasMatrix(lines, job) {
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;

  let inStrategy = false;
  let strategyIndent = -1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= job.indent) {
      break;
    }

    if (!inStrategy) {
      if (indent === job.indent + 2 && /^\s*strategy:\s*(?:#.*)?$/.test(line)) {
        inStrategy = true;
        strategyIndent = indent;
      }
      continue;
    }

    if (indent <= strategyIndent) {
      inStrategy = false;
      strategyIndent = -1;
      continue;
    }

    if (/^\s*matrix:\s*(?:#.*)?$/.test(line) && indent === strategyIndent + 2) {
      return true;
    }
  }

  return false;
}

/**
 * Returns the integer value of `strategy.max-parallel:` for the given job, or
 * `null` when the key is absent or not parseable as a positive integer.
 *
 * The check accepts the standard form `max-parallel: <int>` (with optional
 * single or double quoting around the value) nested directly under
 * `strategy:` at the job's `indent + 4` column. This matches every form used
 * in this repository; expressions such as `${{ ... }}` are deliberately not
 * resolved (a non-literal value cannot be statically guaranteed to be 1).
 *
 * Limitations:
 *   - The flow-style mapping form `strategy: { matrix: {...},
 *     max-parallel: 1 }` is silently unanalyzable and returns `null`. No
 *     active workflow uses flow style; if a future author introduces it,
 *     expand this helper before relying on it.
 *   - Float-looking values like `max-parallel: 1.0` are intentionally
 *     rejected (return `null`). GitHub Actions documents `max-parallel`
 *     as an integer, and YAML tooling that round-trips floats can change
 *     the value's representation in surprising ways.
 */
function extractJobMatrixMaxParallel(lines, job) {
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;

  let inStrategy = false;
  let strategyIndent = -1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= job.indent) {
      break;
    }

    if (!inStrategy) {
      if (indent === job.indent + 2 && /^\s*strategy:\s*(?:#.*)?$/.test(line)) {
        inStrategy = true;
        strategyIndent = indent;
      }
      continue;
    }

    if (indent <= strategyIndent) {
      inStrategy = false;
      strategyIndent = -1;
      continue;
    }

    const maxParallelMatch = /^\s*max-parallel:\s*["']?([^"'\s#]+)["']?\s*(?:#.*)?$/.exec(line);
    if (maxParallelMatch && indent === strategyIndent + 2) {
      const raw = maxParallelMatch[1];
      if (/^[0-9]+$/.test(raw)) {
        const parsed = Number.parseInt(raw, 10);
        if (Number.isFinite(parsed) && parsed > 0) {
          return parsed;
        }
      }
      return null;
    }
  }

  return null;
}

/**
 * Returns the job-level `timeout-minutes` as an integer, or null when absent.
 * Only a line at EXACTLY `job.indent + 2` is considered so deeper step-level
 * `timeout-minutes:` declarations (and strategy/matrix keys) are ignored.
 */
function extractJobTimeoutMinutes(lines, job) {
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= job.indent) {
      break;
    }

    if (indent !== job.indent + 2) {
      continue;
    }

    const timeoutMatch = /^\s*timeout-minutes:\s*["']?([0-9]+)["']?\s*(?:#.*)?$/.exec(line);
    if (timeoutMatch) {
      return Number.parseInt(timeoutMatch[1], 10);
    }
  }

  return null;
}

/**
 * Returns a step's OWN `timeout-minutes` as an integer, or null when absent.
 * Only a line at EXACTLY the step key indent (`getIndent(step header) + 2`) is
 * considered, so a deeper `with.timeout-minutes:` (an action input, e.g. the
 * acquire step's `with: { timeout-minutes: "300" }`) is NOT mistaken for the
 * step's own GitHub-Actions `timeout-minutes` clock.
 */
function extractStepTimeoutMinutes(lines, step) {
  const stepKeyIndent = getIndent(lines[step.startIndex]) + 2;

  for (let i = step.startIndex; i <= step.endIndex; i++) {
    const line = lines[i];
    if (getIndent(line) !== stepKeyIndent) {
      continue;
    }

    const timeoutMatch = /^\s*timeout-minutes:\s*["']?([0-9]+)["']?\s*(?:#.*)?$/.exec(line);
    if (timeoutMatch) {
      return Number.parseInt(timeoutMatch[1], 10);
    }
  }

  return null;
}

/**
 * Returns the set of job ids referenced by the job's `needs:` declaration,
 * as a string[] (or empty array when no needs are declared). Supports the
 * scalar form (`needs: foo`), the inline-array form (`needs: [foo, bar]`),
 * and the multi-line block-list form (`needs:` then `  - foo`).
 */
function extractJobNeeds(lines, job) {
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= job.indent) {
      break;
    }

    if (indent !== job.indent + 2) {
      continue;
    }

    const needsMatch = /^\s*needs:\s*(.*?)\s*(?:#.*)?$/.exec(line);
    if (!needsMatch) {
      continue;
    }

    const raw = needsMatch[1].trim();

    if (raw.length === 0) {
      // Multi-line block list form.
      const items = [];
      for (let j = i + 1; j <= endIndex; j++) {
        const childLine = lines[j];
        const childTrimmed = childLine.trim();
        const childIndent = getIndent(childLine);

        if (childTrimmed.length === 0 || childTrimmed.startsWith("#")) {
          continue;
        }

        if (childIndent <= indent) {
          break;
        }

        const itemMatch = /^\s*-\s*["']?([A-Za-z0-9_-]+)["']?\s*(?:#.*)?$/.exec(childLine);
        if (itemMatch) {
          items.push(itemMatch[1]);
        }
      }
      return items;
    }

    if (raw.startsWith("[")) {
      const inner = raw.slice(1, -1);
      return inner
        .split(",")
        .map((part) => part.trim().replace(/^["']|["']$/g, ""))
        .filter((part) => part.length > 0);
    }

    // Scalar form.
    const stripped = raw.replace(/^(["'])(.*)\1$/, "$2").trim();
    if (stripped.length === 0) {
      return [];
    }
    return [stripped];
  }

  return [];
}

/**
 * Returns the job's `runs-on:` value text and the 1-indexed line. The value
 * is the raw text following the colon (without the `runs-on:` prefix); for
 * multi-line block list form the value will be empty and `blockList` will
 * hold the gathered child entries.
 *
 * Result: { line, raw, blockList?: string[] } or null when no `runs-on:` is
 * declared at the job-key indent.
 */
function extractJobRunsOn(lines, job) {
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    if (indent <= job.indent) {
      break;
    }

    if (indent !== job.indent + 2) {
      continue;
    }

    const runsOnMatch = /^\s*runs-on:\s*(.*?)\s*(?:#.*)?$/.exec(line);
    if (!runsOnMatch) {
      continue;
    }

    const raw = runsOnMatch[1].trim();
    const lineNumber = i + 1;

    if (raw.length === 0) {
      // Multi-line: collect block list children or object children.
      const blockList = [];
      for (let j = i + 1; j <= endIndex; j++) {
        const childLine = lines[j];
        const childTrimmed = childLine.trim();
        const childIndent = getIndent(childLine);

        if (childTrimmed.length === 0 || childTrimmed.startsWith("#")) {
          continue;
        }

        if (childIndent <= indent) {
          break;
        }

        const itemMatch = /^\s*-\s*["']?([^"'#\s]+)["']?\s*(?:#.*)?$/.exec(childLine);
        if (itemMatch) {
          blockList.push(itemMatch[1].trim());
        }
      }

      return { line: lineNumber, raw: "", blockList };
    }

    return { line: lineNumber, raw };
  }

  return null;
}

/**
 * Parses an inline array form like `[self-hosted, Windows, RAM-64GB]` or
 * `["self-hosted", "Windows", "RAM-64GB"]` into a string[] of labels.
 * Returns null if the value is not a recognizable inline array.
 *
 * Throws on a trailing-comma form (`[a, b, c,]`); the empty element it
 * produces would otherwise show up as a phantom blank label in downstream
 * error messages and confuse the operator.
 */
function parseInlineLabelArray(raw) {
  const match = /^\[\s*(.*?)\s*\]$/.exec(raw);
  if (!match) {
    return null;
  }

  const inner = match[1].trim();
  if (inner.length === 0) {
    return [];
  }

  const parts = inner.split(",").map((part) => part.trim().replace(/^["']|["']$/g, ""));
  if (parts.some((part) => part.length === 0)) {
    throw new Error(
      `Trailing or duplicate comma in label list '${raw}'. Remove the empty element.`
    );
  }
  return parts;
}

/**
 * Extracts the bash run-text and outputs map of all jobs in the workflow,
 * indexed by jobId. Used to validate that a dynamic runs-on backed by
 * `${{ fromJSON(needs.<jobId>.outputs.<output>) }}` ultimately produces an
 * allowlisted label set.
 */
function extractJobOutputsSourceMap(lines) {
  const jobs = extractJobs(lines);
  const result = {};

  for (const job of jobs) {
    const startIndex = job.startLine - 1;
    const endIndex = job.endLine - 1;
    const outputs = {};

    // Map outputs key -> { stepId, outputKey }
    let inOutputs = false;
    let outputsIndent = -1;
    for (let i = startIndex + 1; i <= endIndex; i++) {
      const line = lines[i];
      const trimmed = line.trim();
      const indent = getIndent(line);

      if (trimmed.length === 0 || trimmed.startsWith("#")) {
        continue;
      }

      if (indent <= job.indent) {
        break;
      }

      if (!inOutputs) {
        if (indent === job.indent + 2 && /^\s*outputs:\s*(?:#.*)?$/.test(line)) {
          inOutputs = true;
          outputsIndent = indent;
        }
        continue;
      }

      if (indent <= outputsIndent) {
        inOutputs = false;
        outputsIndent = -1;
        continue;
      }

      const outputMatch =
        /^\s*([A-Za-z0-9_-]+):\s*\$\{\{\s*steps\.([A-Za-z0-9_-]+)\.outputs\.([A-Za-z0-9_-]+)\s*\}\}\s*(?:#.*)?$/.exec(
          line
        );
      if (outputMatch) {
        outputs[outputMatch[1]] = {
          stepId: outputMatch[2],
          outputKey: outputMatch[3]
        };
      }
    }

    // Collect each step's id + run text so we can resolve outputs->bash.
    const steps = extractJobSteps(lines, job);
    const stepsById = {};
    for (const step of steps) {
      let stepId = null;
      for (let i = step.startIndex; i <= step.endIndex; i++) {
        // Allow the optional `- ` step-list marker before `id:`.
        const stepIdMatch = /^\s*(?:-\s+)?id:\s*["']?([A-Za-z0-9_-]+)["']?\s*(?:#.*)?$/.exec(
          lines[i]
        );
        if (stepIdMatch) {
          stepId = stepIdMatch[1];
          break;
        }
      }
      if (stepId) {
        stepsById[stepId] = step;
      }
    }

    result[job.id] = { outputs, stepsById };
  }

  return result;
}

function stepSourceText(lines, step) {
  if (!step) {
    return "";
  }
  return lines.slice(step.startIndex, step.endIndex + 1).join("\n");
}

function stripYamlScalarQuotes(value) {
  return value.trim().replace(/^(["'])(.*)\1$/, "$2");
}

function parseInlineYamlList(value) {
  const trimmed = value.trim().replace(/\s+#.*$/, "");
  if (!trimmed.startsWith("[") || !trimmed.endsWith("]")) {
    return null;
  }

  return trimmed
    .slice(1, -1)
    .split(",")
    .map((entry) => stripYamlScalarQuotes(entry))
    .filter((entry) => entry.length > 0);
}

function parseLiteralMatrixScalar(value) {
  const trimmed = value.trim().replace(/\s+#.*$/, "");
  if (trimmed.length === 0) {
    return null;
  }
  if (/\$\{\{/.test(trimmed) || trimmed.startsWith("{") || trimmed.startsWith("|")) {
    return null;
  }
  if (trimmed.startsWith("[")) {
    return parseInlineYamlList(trimmed);
  }
  return [stripYamlScalarQuotes(trimmed)];
}

function extractJobMatrixValues(lines, job, matrixKey) {
  const startIndex = job.startLine - 1;
  const endIndex = job.endLine - 1;
  const escapedKey = matrixKey.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const keyPattern = new RegExp(`^\\s*${escapedKey}\\s*:\\s*(.*?)\\s*(?:#.*)?$`);

  let inStrategy = false;
  let strategyIndent = -1;
  let inMatrix = false;
  let matrixIndent = -1;

  for (let i = startIndex + 1; i <= endIndex; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    const indent = getIndent(line);

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }
    if (indent <= job.indent) {
      break;
    }

    if (!inStrategy) {
      if (indent === job.indent + 2 && /^\s*strategy:\s*(?:#.*)?$/.test(line)) {
        inStrategy = true;
        strategyIndent = indent;
      }
      continue;
    }

    if (indent <= strategyIndent) {
      break;
    }

    if (!inMatrix) {
      if (indent === strategyIndent + 2 && /^\s*matrix:\s*(?:#.*)?$/.test(line)) {
        inMatrix = true;
        matrixIndent = indent;
      }
      continue;
    }

    if (indent <= matrixIndent) {
      break;
    }

    const keyMatch = keyPattern.exec(line);
    if (!keyMatch || indent !== matrixIndent + 2) {
      continue;
    }

    const inlineValues = parseLiteralMatrixScalar(keyMatch[1]);
    if (inlineValues !== null) {
      return inlineValues;
    }

    const values = [];
    for (let j = i + 1; j <= endIndex; j++) {
      const childLine = lines[j];
      const childTrimmed = childLine.trim();
      const childIndent = getIndent(childLine);
      if (childTrimmed.length === 0 || childTrimmed.startsWith("#")) {
        continue;
      }
      if (childIndent <= indent) {
        break;
      }
      const listMatch = /^\s*-\s+(.+?)\s*(?:#.*)?$/.exec(childLine);
      if (listMatch) {
        const parsed = parseLiteralMatrixScalar(listMatch[1]);
        if (parsed === null || parsed.length !== 1) {
          return null;
        }
        values.push(parsed[0]);
      }
    }
    return values.length > 0 ? values : null;
  }

  return null;
}

function stringifyWorkflowScalar(value) {
  if (value === null || value === undefined) {
    return "";
  }
  return String(value).trim();
}

function workflowScalarIsTrue(value) {
  return value === true || stringifyWorkflowScalar(value).toLowerCase() === "true";
}

function workflowScalarIsFalse(value) {
  return value === false || stringifyWorkflowScalar(value).toLowerCase() === "false";
}

function extractedWorkflowStepRunText(step) {
  if (!step || !step.run) {
    return "";
  }
  return stringifyWorkflowScalar(step.run.text);
}

/**
 * Returns the deduplicated list of labels referenced by a job's `runs-on:`
 * value, including inline-array, block-list, and scalar forms. Returns
 * `null` when the form is dynamic (`${{ fromJSON(...) }}` etc.) and the
 * caller cannot statically resolve the labels.
 */
function extractStaticJobLabels(lines, job) {
  const runsOn = extractJobRunsOn(lines, job);
  if (!runsOn) {
    return null;
  }
  const raw = runsOn.raw;

  if (raw.startsWith("[")) {
    try {
      return parseInlineLabelArray(raw);
    } catch (_error) {
      return null;
    }
  }

  if (raw === "" && Array.isArray(runsOn.blockList) && runsOn.blockList.length > 0) {
    return runsOn.blockList.slice();
  }

  if (raw.length === 0) {
    return null;
  }

  if (raw.startsWith("${{")) {
    return null;
  }

  return [raw.replace(/^(["'])(.*)\1$/, "$2").trim()];
}

function normalizeUsesRef(rawUses) {
  if (typeof rawUses !== "string") {
    return "";
  }

  return rawUses.trim().toLowerCase().replace(/^\.\//, "");
}

function stepRunText(step) {
  if (!step || typeof step !== "object") {
    return "";
  }

  const run = step.run;
  if (typeof run === "string") {
    return run;
  }

  // A list-form `run:` is invalid GitHub Actions YAML, but tolerate it
  // defensively by joining rather than throwing.
  if (Array.isArray(run)) {
    return run.filter((entry) => typeof entry === "string").join("\n");
  }

  return "";
}

module.exports = {
  Violation,
  escapeRegexChar,
  extractConcurrencyGroupFromBlock,
  extractDefaultRunShellFromBlock,
  extractJobConcurrencyGroup,
  extractJobDefaultsShell,
  extractJobMatrixMaxParallel,
  extractJobMatrixValues,
  extractJobNeeds,
  extractJobOutputsSourceMap,
  extractJobRunsOn,
  extractJobSteps,
  extractJobTimeoutMinutes,
  extractJobs,
  extractRunBlocks,
  extractStaticJobLabels,
  extractStepEnvMap,
  extractStepIf,
  extractStepName,
  extractStepRun,
  extractStepShell,
  extractStepTimeoutMinutes,
  extractStepUses,
  extractStepWithMap,
  extractWorkflowConcurrencyGroup,
  extractWorkflowDefaultsShell,
  extractWorkflowPathBlocks,
  extractWorkflowPathEntries,
  extractWorkflowPathFilterBlocks,
  extractWorkflowPathIgnoreBlocks,
  extractedWorkflowStepRunText,
  forEachJob,
  forEachJobStep,
  getIndent,
  jobHasMatrix,
  jobTargetsWindows,
  loadYamlModule,
  normalizeUsesRef,
  normalizeWorkflowPathPattern,
  parseInlineLabelArray,
  parseInlineYamlList,
  parseLiteralMatrixScalar,
  parseYamlBoolean,
  stepRunText,
  stepSourceText,
  stringifyWorkflowScalar,
  stripYamlScalarQuotes,
  workflowPathGlobToRegex,
  workflowScalarIsFalse,
  workflowScalarIsTrue
};
