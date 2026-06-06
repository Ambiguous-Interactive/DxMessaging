#!/usr/bin/env node
/**
 * validate-pre-commit-tooling.js
 *
 * Enforces non-interactive Node tooling rules for local hooks:
 * - npx calls must explicitly set install policy via --yes/-y or --no.
 * - Jest-related hooks must use scripts/run-managed-jest.js for deterministic execution.
 * - yamllint must be configured as a non-optional hook (no conditional skip wrappers).
 * - Preflight must auto-repair the pre-commit executable before hook parity checks.
 * - Mutating pre-commit hooks must use run-and-restage / run-and-stage.
 * - YAML comment auto-wrap hook must use run-and-restage + require_serial.
 * - Markdown auto-fix pipeline must use run-and-restage + require_serial.
 * - preflight:pre-commit must run tracked markdown link-text validation.
 * - preflight:pre-commit must repair and validate .llm policy before hook parity.
 * - preflight:pre-commit must include YAML policy checks, including comment drift checks.
 * - preflight:pre-push must run all-file managed cspell before full hook parity.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { normalizeToLf } = require("./lib/quote-parser");
const { getConfiguredPrettierSpec, getPinnedPrettierSpec } = require("./lib/prettier-version");
const {
  scoreConfig,
  PERF_BUDGET,
  PER_HOOK_CEILING,
  formatReport
} = require("./lib/precommit-perf-score");
const { STEPS: PREPUSH_PREFLIGHT_STEPS } = require("./run-prepush-preflight");

const PRE_COMMIT_CONFIG_PATH = path.join(__dirname, "..", ".pre-commit-config.yaml");
const PACKAGE_JSON_PATH = path.join(__dirname, "..", "package.json");
const REQUIRED_PRECHECK_PARSER_COMMAND =
  "node scripts/ensure-pre-commit.js run --hook-stage pre-push script-parser-tests --all-files";
const REQUIRED_NODE_REPAIR_COMMAND = "npm run repair:node-tooling";
const REQUIRED_PRE_COMMIT_REPAIR_COMMAND = "npm run repair:pre-commit";
const REQUIRED_NODE_TOOLING_COMMAND = "npm run validate:node-tooling";
const REQUIRED_HOOK_MARKDOWN_COMMAND = "npm run validate:hook-markdown";
const REQUIRED_MARKDOWN_LINK_TEXT_COMMAND = "npm run validate:markdown-link-text";
const REQUIRED_CHANGED_DOCS_COMMAND = "npm run validate:changed-docs";
const REQUIRED_LLM_MARKDOWN_COMMAND = "npm run validate:llm-markdown";
const REQUIRED_SKILLS_VALIDATION_COMMAND = "npm run validate:skills";
const REQUIRED_LLM_POLICY_REPAIR_COMMAND = "npm run repair:llm-policy";
const REQUIRED_LLM_POLICY_COMMAND = "npm run validate:llm-policy";
const REQUIRED_SKILLS_INDEX_REPAIR_COMMAND =
  "node scripts/run-and-stage.js node scripts/generate-skills-index.js -- .llm/skills/index.md";
const REQUIRED_SKILLS_INDEX_CHECK_COMMAND = "node scripts/generate-skills-index.js --check";
const REQUIRED_PACKAGE_JSON_FORMAT_COMMAND = "npm run check:package-json-format";
const REQUIRED_YAML_VALIDATION_COMMAND = "npm run check:yaml";
const REQUIRED_YAML_COMMENTS_CHECK_COMMAND = "npm run check:yaml:comments";
const REQUIRED_ALL_CSPELL_COMMAND = "npm run check:cspell:all";
const REQUIRED_PREPUSH_PREFLIGHT_RUNNER_COMMAND = "node scripts/run-prepush-preflight.js";
const REQUIRED_SCRIPTS_CSPELL_COMMAND = "npm run check:cspell:scripts";
const REQUIRED_WORKFLOW_CSPELL_COMMAND = "npm run check:workflow-cspell";
const REQUIRED_WORKFLOW_VALIDATION_COMMAND = "npm run validate:workflows";
const REQUIRED_BANNER_SYNC_COMMAND = "npm run check:banner-sync";
const REQUIRED_CHANGELOG_VALIDATION_COMMAND = "npm run validate:changelog:coverage";
const REQUIRED_PARSER_SUITE_HOOK_ID = "script-parser-tests";
const REQUIRED_PARSER_SUITE_TEST_PATHS = [
  "scripts/__tests__/fix-csharp-underscore-methods.test.js",
  "scripts/__tests__/check-conflict-markers.test.js",
  "scripts/__tests__/validate-changed-docs.test.js",
  "scripts/__tests__/validate-changelog.test.js",
  "scripts/__tests__/pre-commit-hook-stage-policy.test.js"
];

class Violation {
  constructor(hookId, line, message, entry) {
    this.hookId = hookId;
    this.line = line;
    this.message = message;
    this.entry = entry;
  }

  toString() {
    return `${this.hookId} (line ${this.line}): ${this.message}\n  entry: ${this.entry}`;
  }
}

function getIndent(line) {
  return line.length - line.trimStart().length;
}

function parseHookEntries(content) {
  const normalized = normalizeToLf(content);
  const lines = normalized.split("\n");
  const entries = [];

  let currentHookId = null;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const idMatch = /^(\s*)-\s+id:\s*([^\s#]+)\s*$/.exec(line);
    if (idMatch) {
      currentHookId = idMatch[2].trim();
      continue;
    }

    if (!currentHookId) {
      continue;
    }

    const entryMatch = /^(\s*)entry:\s*(.*)$/.exec(line);
    if (!entryMatch) {
      continue;
    }

    const entryIndent = entryMatch[1].length;
    const entryValue = entryMatch[2].trim();
    let command;

    if ([">", ">-", "|", "|-"].includes(entryValue)) {
      const blockLines = [];
      let j = i + 1;
      while (j < lines.length) {
        const nextLine = lines[j];
        const nextLineIndent = getIndent(nextLine);

        if (nextLine.trim().length > 0 && nextLineIndent <= entryIndent) {
          break;
        }

        if (nextLine.trim().length > 0) {
          blockLines.push(nextLine.trim());
        }

        j++;
      }

      // Skip block lines that were consumed by this folded/literal entry.
      i = j - 1;
      command = blockLines.join(" ").replace(/\s+/g, " ").trim();
    } else {
      command = entryValue;
    }

    entries.push({ id: currentHookId, line: i + 1, entry: command });
  }

  return entries;
}

function parseHookIds(content) {
  const lines = normalizeToLf(content).split("\n");
  const ids = [];

  for (let i = 0; i < lines.length; i++) {
    const idMatch = /^\s*-\s+id:\s*([^\s#]+)\s*$/.exec(lines[i]);
    if (idMatch) {
      ids.push({ id: idMatch[1].trim(), line: i + 1 });
    }
  }

  return ids;
}

function parseHookConfigs(content) {
  const lines = normalizeToLf(content).split("\n");
  const hooks = [];
  let current = null;

  for (let i = 0; i < lines.length; i++) {
    const idMatch = /^(\s*)-\s+id:\s*([^\s#]+)\s*$/.exec(lines[i]);
    if (idMatch) {
      current = {
        id: idMatch[2].trim(),
        line: i + 1,
        indent: idMatch[1].length,
        properties: {}
      };
      hooks.push(current);
      continue;
    }

    if (!current) {
      continue;
    }

    const lineIndent = getIndent(lines[i]);
    if (lines[i].trim().length > 0 && lineIndent <= current.indent) {
      current = null;
      continue;
    }

    const propertyMatch = /^\s*([A-Za-z_][A-Za-z0-9_-]*):\s*(.*?)\s*(?:#.*)?$/.exec(lines[i]);
    if (propertyMatch) {
      current.properties[propertyMatch[1]] = propertyMatch[2].trim();
    }
  }

  return hooks;
}

function tokenizeCommand(entry) {
  const tokens = entry.match(/"(?:\\.|[^"])*"|'(?:\\.|[^'])*'|\S+/g) || [];
  return tokens.map((token) => token.replace(/^['"]|['"]$/g, ""));
}

function escapeRegexLiteral(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function hasRequiredPreflightCommand(preflightScript, requiredCommand) {
  if (typeof preflightScript !== "string" || preflightScript.trim().length === 0) {
    return false;
  }

  const normalizedScript = preflightScript.replace(/\s+/g, " ").trim();
  const normalizedRequired = requiredCommand.replace(/\s+/g, " ").trim();
  const commandRegex = new RegExp(
    `(?:^|&&\\s*)${escapeRegexLiteral(normalizedRequired)}(?:\\s*&&|$)`
  );

  return commandRegex.test(normalizedScript);
}

function hasRequiredParserPrecheckCommand(preflightScript) {
  return hasRequiredPreflightCommand(preflightScript, REQUIRED_PRECHECK_PARSER_COMMAND);
}

function hasRequiredNodeRepairCommand(preflightScript) {
  return hasRequiredPreflightCommand(preflightScript, REQUIRED_NODE_REPAIR_COMMAND);
}

function hasNodeRepairBeforeNodeValidation(preflightScript) {
  if (
    typeof preflightScript !== "string" ||
    !hasRequiredNodeRepairCommand(preflightScript) ||
    !hasRequiredNodeToolingCommand(preflightScript)
  ) {
    return false;
  }

  return (
    preflightScript.indexOf(REQUIRED_NODE_REPAIR_COMMAND) <
    preflightScript.indexOf(REQUIRED_NODE_TOOLING_COMMAND)
  );
}

function hasRequiredPreCommitRepairCommand(preflightScript) {
  return hasRequiredPreflightCommand(preflightScript, REQUIRED_PRE_COMMIT_REPAIR_COMMAND);
}

function hasPreCommitRepairBeforeHookMarkdown(preflightScript) {
  const repairIndex = preflightScript.indexOf(REQUIRED_PRE_COMMIT_REPAIR_COMMAND);
  const hookIndex = preflightScript.indexOf(REQUIRED_HOOK_MARKDOWN_COMMAND);
  return repairIndex >= 0 && hookIndex >= 0 && repairIndex < hookIndex;
}

function hasRequiredPackageJsonFormatCommand(preflightScript) {
  return hasRequiredPreflightCommand(preflightScript, REQUIRED_PACKAGE_JSON_FORMAT_COMMAND);
}

function hasRequiredYamlValidationCommand(preflightScript) {
  return hasRequiredPreflightCommand(preflightScript, REQUIRED_YAML_VALIDATION_COMMAND);
}

function hasRequiredNodeToolingCommand(preflightScript) {
  return hasRequiredPreflightCommand(preflightScript, REQUIRED_NODE_TOOLING_COMMAND);
}

function hasRequiredHookMarkdownCommand(preflightScript) {
  return hasRequiredPreflightCommand(preflightScript, REQUIRED_HOOK_MARKDOWN_COMMAND);
}

function hasRequiredMarkdownLinkTextCommand(preflightScript) {
  return hasRequiredPreflightCommand(preflightScript, REQUIRED_MARKDOWN_LINK_TEXT_COMMAND);
}

function hasRequiredChangedDocsCommand(preflightScript) {
  return hasRequiredPreflightCommand(preflightScript, REQUIRED_CHANGED_DOCS_COMMAND);
}

function hasRequiredLlmMarkdownCommand(preflightScript) {
  return hasRequiredPreflightCommand(preflightScript, REQUIRED_LLM_MARKDOWN_COMMAND);
}

function hasRequiredSkillsValidationCommand(script) {
  return hasRequiredPreflightCommand(script, REQUIRED_SKILLS_VALIDATION_COMMAND);
}

function hasRequiredLlmPolicyRepairCommand(script) {
  return hasRequiredPreflightCommand(script, REQUIRED_LLM_POLICY_REPAIR_COMMAND);
}

function hasRequiredLlmPolicyCommand(script) {
  return hasRequiredPreflightCommand(script, REQUIRED_LLM_POLICY_COMMAND);
}

function hasLlmPolicyRepairBeforeValidation(preflightScript) {
  const repairIndex = preflightScript.indexOf(REQUIRED_LLM_POLICY_REPAIR_COMMAND);
  const validateIndex = preflightScript.indexOf(REQUIRED_LLM_POLICY_COMMAND);
  return repairIndex >= 0 && validateIndex >= 0 && repairIndex < validateIndex;
}

function hasRequiredSkillsIndexRepairCommand(script) {
  return hasRequiredPreflightCommand(script, REQUIRED_SKILLS_INDEX_REPAIR_COMMAND);
}

function hasRequiredSkillsIndexCheckCommand(script) {
  return hasRequiredPreflightCommand(script, REQUIRED_SKILLS_INDEX_CHECK_COMMAND);
}

function hasRequiredScriptsCspellCommand(preflightScript) {
  return hasRequiredPreflightCommand(preflightScript, REQUIRED_SCRIPTS_CSPELL_COMMAND);
}

function stepText(step) {
  const command = step.command === process.execPath ? "node" : step.command;
  return [command, ...step.args].join(" ");
}

function resolvePrePushScriptForPolicy(prePushScript) {
  if (typeof prePushScript !== "string") {
    return "";
  }
  if (prePushScript.trim() === REQUIRED_PREPUSH_PREFLIGHT_RUNNER_COMMAND) {
    return PREPUSH_PREFLIGHT_STEPS.map(stepText).join(" && ");
  }
  return prePushScript;
}

function hasRequiredAllCspellCommand(prePushScript) {
  return hasRequiredPreflightCommand(
    resolvePrePushScriptForPolicy(prePushScript),
    REQUIRED_ALL_CSPELL_COMMAND
  );
}

function hasAllCspellBeforePrePushHookParity(prePushScript) {
  if (typeof prePushScript !== "string" || !hasRequiredAllCspellCommand(prePushScript)) {
    return false;
  }

  const resolved = resolvePrePushScriptForPolicy(prePushScript);
  const cspellIndex = resolved.indexOf(REQUIRED_ALL_CSPELL_COMMAND);
  const prePushHookIndex = resolved.indexOf("run --hook-stage pre-push");
  return cspellIndex >= 0 && prePushHookIndex >= 0 && cspellIndex < prePushHookIndex;
}

function hasRequiredWorkflowCspellCommand(preflightScript) {
  return hasRequiredPreflightCommand(preflightScript, REQUIRED_WORKFLOW_CSPELL_COMMAND);
}

function hasRequiredWorkflowValidationCommand(preflightScript) {
  return hasRequiredPreflightCommand(preflightScript, REQUIRED_WORKFLOW_VALIDATION_COMMAND);
}

function hasRequiredBannerSyncCommand(preflightScript) {
  return hasRequiredPreflightCommand(preflightScript, REQUIRED_BANNER_SYNC_COMMAND);
}

function hasRequiredChangelogValidationCommand(preflightScript) {
  return hasRequiredPreflightCommand(preflightScript, REQUIRED_CHANGELOG_VALIDATION_COMMAND);
}

function hasRequiredYamlCommentsCheckCommand(yamlCheckScript) {
  if (typeof yamlCheckScript !== "string" || yamlCheckScript.trim().length === 0) {
    return false;
  }

  return hasRequiredPreflightCommand(yamlCheckScript, REQUIRED_YAML_COMMENTS_CHECK_COMMAND);
}

function hasRequiredParserSuiteTestPaths(
  preCommitConfigContent,
  requiredTestPaths = REQUIRED_PARSER_SUITE_TEST_PATHS
) {
  const parserSuiteHook = parseHookEntries(preCommitConfigContent).find(
    (hook) => hook.id === REQUIRED_PARSER_SUITE_HOOK_ID
  );

  if (!parserSuiteHook) {
    return false;
  }

  const parserSuiteTokens = new Set(tokenizeCommand(parserSuiteHook.entry));
  return requiredTestPaths.every((requiredTestPath) => parserSuiteTokens.has(requiredTestPath));
}

function hasNpxInstallPolicy(entry) {
  const tokens = tokenizeCommand(entry);
  let foundNpx = false;

  for (let i = 0; i < tokens.length; i++) {
    if (tokens[i] !== "npx") {
      continue;
    }

    foundNpx = true;
    let hasPolicy = false;

    for (let j = i + 1; j < tokens.length; j++) {
      const token = tokens[j];

      if (token === "--yes" || token === "-y" || token === "--no") {
        hasPolicy = true;
        break;
      }

      if (token === "--") {
        break;
      }

      if (!token.startsWith("-")) {
        break;
      }
    }

    if (!hasPolicy) {
      return false;
    }
  }

  if (foundNpx) {
    return true;
  }

  // Fallback for quoted shell fragments that contain npx but were tokenized as a single token.
  // This check is intentionally lexical and does not attempt to evaluate shell expansion.
  // The flag boundary uses (?:^|\s) on the left and (?=\s|$) on the right because
  // \b does not match between space and a leading hyphen.
  if (/\bnpx\b/.test(entry)) {
    return /(?:^|\s)(--yes|-y|--no)(?=\s|$)/.test(entry);
  }

  return true;
}

function usesManagedJestWrapper(entry) {
  return /\bnode\b\s+scripts\/run-managed-jest\.js\b/.test(entry);
}

function isJestRelatedHook(hookId, entry) {
  return (
    usesManagedJestWrapper(entry) ||
    /\bjest\b/.test(entry) ||
    /script-(?:parser-)?tests/.test(hookId)
  );
}

function hasManagedJestInvocation(hookIdOrEntry, maybeEntry) {
  const hookId = maybeEntry === undefined ? "" : hookIdOrEntry;
  const entry = maybeEntry === undefined ? hookIdOrEntry : maybeEntry;

  if (!isJestRelatedHook(hookId, entry)) {
    return true;
  }

  return usesManagedJestWrapper(entry);
}

// Round-4: the prettier hook inlined its bin via `bash -c` (cspell /
// markdownlint pattern). Phase X (integrity gate): the hook now routes
// through `node scripts/run-managed-prettier.js`, which performs the same
// local-bin-vs-npx-fallback dispatch internally AND adds an integrity
// probe ahead of either branch. The pinned `prettier@<v>` literal lives in
// scripts/lib/prettier-version.js#FALLBACK_PRETTIER_SPEC and is validated
// against package.json devDependencies by
// `scripts/__tests__/prettier-version-parity.test.js`.
//
// `hasInlinedPrettierEntry` accepts EITHER:
//   (a) the legacy inlined shape (local bin + pinned npx --package=...), OR
//   (b) the managed-runner shape (`node scripts/run-managed-prettier.js`).
function hasInlinedPrettierEntry(entry) {
  if (!/\bprettier\b/.test(entry)) {
    return false;
  }

  const usesManagedRunner = /\bnode\b\s+scripts\/run-managed-prettier\.js\b/.test(entry);
  if (usesManagedRunner) {
    return true;
  }

  const usesLocalBin = /\bnode_modules\/prettier\/bin\/prettier\.cjs\b/.test(entry);
  const usesPinnedNpxFallback = /\bnpx\b[^&]*--package=prettier@\d+\.\d+\.\d+/.test(entry);
  return usesLocalBin && usesPinnedNpxFallback;
}

function hasInlinedPrettierInvocation(hookId, entry) {
  if (hookId !== "prettier") {
    return true;
  }

  return hasInlinedPrettierEntry(entry);
}

function hasGuardedFixerRestagePattern(hookId, entry) {
  if (hookId !== "fix-csharp-underscore-methods") {
    return true;
  }

  return (
    /\bnode\b\s+scripts\/run-and-restage\.js\b/.test(entry) &&
    /\bnode\b\s+scripts\/fix-csharp-underscore-methods\.js\b/.test(entry) &&
    /(?:^|\s)--(?:\s|$)/.test(entry)
  );
}

function hasGuardedCsharpierRestagePattern(hookId, entry) {
  if (hookId !== "csharpier") {
    return true;
  }

  return (
    /\bnode\b\s+scripts\/run-and-restage\.js\b/.test(entry) &&
    /\bdotnet\b\s+tool\s+run\s+csharpier\s+format\b/.test(entry) &&
    /(?:^|\s)--(?:\s|$)/.test(entry)
  );
}

function hasGuardedPrettierRestagePattern(hookId, entry) {
  if (hookId !== "prettier") {
    return true;
  }

  return (
    /\bnode\b\s+scripts\/run-and-restage\.js\b/.test(entry) &&
    /\bnode\b\s+scripts\/run-managed-prettier\.js\b/.test(entry) &&
    /(?:^|\s)--(?:\s|$)/.test(entry)
  );
}

function hasGuardedEolRestagePattern(hookId, entry) {
  if (hookId !== "fix-eol") {
    return true;
  }

  return (
    /\bnode\b\s+scripts\/run-and-restage\.js\b/.test(entry) &&
    /\bnode\b\s+scripts\/fix-eol\.js\b/.test(entry) &&
    /(?:^|\s)--(?:\s|$)/.test(entry)
  );
}

function hasGuardedBannerStagePattern(hookId, entry) {
  if (hookId !== "sync-banner-version") {
    return true;
  }

  return (
    /\bnode\b\s+scripts\/run-and-stage\.js\b/.test(entry) &&
    /\bnode\b\s+scripts\/sync-banner-version-hook\.js\b/.test(entry) &&
    /\bdocs\/images\/DxMessaging-banner\.svg\b/.test(entry) &&
    /(?:^|\s)--(?:\s|$)/.test(entry)
  );
}

function hasGuardedYamlCommentRestagePattern(hookId, entry) {
  if (hookId !== "fix-yaml-comments-line-length") {
    return true;
  }

  return (
    /\bnode\b\s+scripts\/run-and-restage\.js\b/.test(entry) &&
    /\bnode\b\s+scripts\/fix-yaml-comments-line-length\.js\b/.test(entry) &&
    /(?:^|\s)--(?:\s|$)/.test(entry)
  );
}

function hasGuardedMarkdownPipelineRestagePattern(hookId, entry) {
  if (hookId !== "run-staged-md-pipeline") {
    return true;
  }

  return (
    /\bnode\b\s+scripts\/run-and-restage\.js\b/.test(entry) &&
    /\bnode\b\s+scripts\/run-staged-md-pipeline\.js\b/.test(entry) &&
    /(?:^|\s)--(?:\s|$)/.test(entry)
  );
}

function hasPortableHookInvocation(entry) {
  return !/^\s*(?:bash|sh|pwsh|powershell)(?:\s|$)/.test(entry);
}

function usesManagedNodeRepair(entry) {
  return /\bnode\b\s+scripts\/run-managed-(?:prettier|cspell)\.js\b/.test(entry);
}

function hasRequiredSerialManagedRepairHook(hook) {
  if (!usesManagedNodeRepair(hook.entry)) {
    return true;
  }

  return hook.requireSerial === "true";
}

function hasRequiredSerialYamlCommentFixerHook(hook) {
  if (hook.id !== "fix-yaml-comments-line-length") {
    return true;
  }

  return hook.requireSerial === "true";
}

function hasRequiredSerialEolFixerHook(hook) {
  if (hook.id !== "fix-eol") {
    return true;
  }

  return hook.requireSerial === "true";
}

function hasRequiredSerialMarkdownPipelineHook(hook) {
  if (hook.id !== "run-staged-md-pipeline") {
    return true;
  }

  return hook.requireSerial === "true";
}

function validateHookEntries(entries, hookConfigs = []) {
  const violations = [];
  const configById = new Map(hookConfigs.map((hook) => [hook.id, hook]));

  for (const hook of entries) {
    const hookConfig = configById.get(hook.id);
    const enrichedHook = {
      ...hook,
      requireSerial: hookConfig && hookConfig.properties.require_serial
    };

    if (/\bnpx\b/.test(hook.entry) && !hasNpxInstallPolicy(hook.entry)) {
      violations.push(
        new Violation(
          hook.id,
          hook.line,
          "npx entry must explicitly set install policy with --yes/-y or --no.",
          hook.entry
        )
      );
    }

    if (!hasManagedJestInvocation(hook.id, hook.entry)) {
      violations.push(
        new Violation(
          hook.id,
          hook.line,
          "Jest-related hooks must invoke node scripts/run-managed-jest.js.",
          hook.entry
        )
      );
    }

    if (!hasInlinedPrettierInvocation(hook.id, hook.entry)) {
      violations.push(
        new Violation(
          hook.id,
          hook.line,
          "Prettier hook must inline node_modules/prettier/bin/prettier.cjs with a pinned `npx --yes --package=prettier@<version>` fallback (see scripts/__tests__/prettier-version-parity.test.js).",
          hook.entry
        )
      );
    }

    if (!hasGuardedFixerRestagePattern(hook.id, hook.entry)) {
      violations.push(
        new Violation(
          hook.id,
          hook.line,
          "fix-csharp-underscore-methods must use node scripts/run-and-restage.js so restaging is shell-neutral and diff-guarded.",
          hook.entry
        )
      );
    }

    if (!hasGuardedCsharpierRestagePattern(hook.id, hook.entry)) {
      violations.push(
        new Violation(
          hook.id,
          hook.line,
          "csharpier must use node scripts/run-and-restage.js with '--' so staged C# formatting is shell-neutral and diff-guarded.",
          hook.entry
        )
      );
    }

    if (!hasGuardedPrettierRestagePattern(hook.id, hook.entry)) {
      violations.push(
        new Violation(
          hook.id,
          hook.line,
          "prettier must use node scripts/run-and-restage.js with '--' so staged formatting is shell-neutral and diff-guarded.",
          hook.entry
        )
      );
    }

    if (!hasGuardedEolRestagePattern(hook.id, hook.entry)) {
      violations.push(
        new Violation(
          hook.id,
          hook.line,
          "fix-eol must use node scripts/run-and-restage.js with '--' so staged EOL fixes are shell-neutral and diff-guarded.",
          hook.entry
        )
      );
    }

    if (!hasGuardedBannerStagePattern(hook.id, hook.entry)) {
      violations.push(
        new Violation(
          hook.id,
          hook.line,
          "sync-banner-version must use node scripts/run-and-stage.js and stage docs/images/DxMessaging-banner.svg when the generated banner changes.",
          hook.entry
        )
      );
    }

    if (!hasGuardedYamlCommentRestagePattern(hook.id, hook.entry)) {
      violations.push(
        new Violation(
          hook.id,
          hook.line,
          "fix-yaml-comments-line-length must use node scripts/run-and-restage.js with '--' so staged YAML comment auto-wrap is shell-neutral and diff-guarded.",
          hook.entry
        )
      );
    }

    if (!hasGuardedMarkdownPipelineRestagePattern(hook.id, hook.entry)) {
      violations.push(
        new Violation(
          hook.id,
          hook.line,
          "run-staged-md-pipeline must use node scripts/run-and-restage.js with '--' so staged Markdown auto-fixes are shell-neutral and diff-guarded.",
          hook.entry
        )
      );
    }

    if (!hasPortableHookInvocation(hook.entry)) {
      violations.push(
        new Violation(
          hook.id,
          hook.line,
          "Hook entries must use shell-neutral Node or tool executables; do not require bash, sh, pwsh, or powershell directly.",
          hook.entry
        )
      );
    }

    if (!hasRequiredSerialManagedRepairHook(enrichedHook)) {
      violations.push(
        new Violation(
          hook.id,
          hook.line,
          "Managed Prettier/cspell hooks must set require_serial: true so npm ci self-heal is single-writer across pre-commit filename partitions.",
          hook.entry
        )
      );
    }

    if (!hasRequiredSerialYamlCommentFixerHook(enrichedHook)) {
      violations.push(
        new Violation(
          hook.id,
          hook.line,
          "fix-yaml-comments-line-length must set require_serial: true so restage writes stay single-writer across filename partitions.",
          hook.entry
        )
      );
    }

    if (!hasRequiredSerialEolFixerHook(enrichedHook)) {
      violations.push(
        new Violation(
          hook.id,
          hook.line,
          "fix-eol must set require_serial: true because it can restage files.",
          hook.entry
        )
      );
    }

    if (!hasRequiredSerialMarkdownPipelineHook(enrichedHook)) {
      violations.push(
        new Violation(
          hook.id,
          hook.line,
          "run-staged-md-pipeline must set require_serial: true because it can restage files.",
          hook.entry
        )
      );
    }
  }

  return violations;
}

function validateYamllintPolicy(content) {
  const violations = [];
  const normalized = normalizeToLf(content);
  const lines = normalized.split("\n");
  const hookIds = parseHookIds(content);
  const yamllintHook = hookIds.find((hook) => hook.id === "yamllint");

  if (!yamllintHook) {
    violations.push(
      new Violation(
        "yamllint",
        1,
        "Missing required yamllint hook. Configure a non-optional yamllint hook in .pre-commit-config.yaml.",
        "(missing hook)"
      )
    );
  }

  const forbiddenPatterns = [/yamllint not installed; skipping/i, /command\s+-v\s+yamllint/i];

  for (const pattern of forbiddenPatterns) {
    const lineIndex = lines.findIndex((line) => pattern.test(line));
    if (lineIndex !== -1) {
      violations.push(
        new Violation(
          "yamllint",
          lineIndex + 1,
          "yamllint hook must not be conditionally skipped; use a deterministic managed hook.",
          lines[lineIndex].trim()
        )
      );
    }
  }

  return violations;
}

function validatePrettierVersionResolution(
  getConfiguredPrettierSpecFn = getConfiguredPrettierSpec,
  getPinnedPrettierSpecFn = getPinnedPrettierSpec
) {
  const violations = [];

  const configuredSpec = getConfiguredPrettierSpecFn();
  if (!configuredSpec) {
    violations.push(
      new Violation(
        "prettier-version",
        1,
        "Missing pinned prettier version in package.json devDependencies.",
        "(missing package.json devDependencies.prettier)"
      )
    );
    return violations;
  }

  const resolvedSpec = getPinnedPrettierSpecFn();
  if (resolvedSpec !== configuredSpec) {
    violations.push(
      new Violation(
        "prettier-version",
        1,
        `Resolved managed Prettier spec (${resolvedSpec}) must match package.json (${configuredSpec}).`,
        "scripts/lib/prettier-version.js"
      )
    );
  }

  return violations;
}

function validatePreflightScriptPolicy(
  readFileSyncImpl = fs.readFileSync,
  packageJsonPath = PACKAGE_JSON_PATH,
  preCommitConfigPath = PRE_COMMIT_CONFIG_PATH
) {
  const violations = [];
  let packageJson;
  let preCommitConfig;

  try {
    packageJson = JSON.parse(readFileSyncImpl(packageJsonPath, "utf8"));
  } catch (error) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        "Unable to parse package.json while validating preflight script policy.",
        error.message
      )
    );
    return violations;
  }

  const preflightScript = packageJson?.scripts?.["preflight:pre-commit"];
  if (typeof preflightScript !== "string" || preflightScript.trim().length === 0) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        "Missing package.json scripts.preflight:pre-commit command.",
        "package.json"
      )
    );
    return violations;
  }

  if (!hasRequiredPackageJsonFormatCommand(preflightScript)) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must include '${REQUIRED_PACKAGE_JSON_FORMAT_COMMAND}' so package.json formatting drift is caught before hooks.`,
        preflightScript
      )
    );
  }

  if (!hasRequiredYamlValidationCommand(preflightScript)) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must include '${REQUIRED_YAML_VALIDATION_COMMAND}' so YAML lint/format policy drift is caught before hooks.`,
        preflightScript
      )
    );
  }

  if (!hasNodeRepairBeforeNodeValidation(preflightScript)) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must run '${REQUIRED_NODE_REPAIR_COMMAND}' before '${REQUIRED_NODE_TOOLING_COMMAND}' so partial node_modules installs are auto-repaired before read-only validation.`,
        preflightScript
      )
    );
  }

  if (
    hasRequiredPreCommitRepairCommand(preflightScript) &&
    hasRequiredHookMarkdownCommand(preflightScript) &&
    !hasPreCommitRepairBeforeHookMarkdown(preflightScript)
  ) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must run '${REQUIRED_PRE_COMMIT_REPAIR_COMMAND}' before '${REQUIRED_HOOK_MARKDOWN_COMMAND}' so a missing pre-commit executable is auto-installed before hook parity checks.`,
        preflightScript
      )
    );
  }

  if (!hasRequiredNodeToolingCommand(preflightScript)) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must include '${REQUIRED_NODE_TOOLING_COMMAND}' so incomplete local node_modules installs are caught before hooks.`,
        preflightScript
      )
    );
  }

  if (!hasRequiredPreCommitRepairCommand(preflightScript)) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must include '${REQUIRED_PRE_COMMIT_REPAIR_COMMAND}' so missing pre-commit executables are auto-repaired before hooks.`,
        preflightScript
      )
    );
  }

  if (!hasRequiredHookMarkdownCommand(preflightScript)) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must include '${REQUIRED_HOOK_MARKDOWN_COMMAND}' so the pre-commit markdown hook path is smoke-tested before commit.`,
        preflightScript
      )
    );
  }

  if (!hasRequiredMarkdownLinkTextCommand(preflightScript)) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must include '${REQUIRED_MARKDOWN_LINK_TEXT_COMMAND}' so tracked markdown link-text regressions are caught locally before CI.`,
        preflightScript
      )
    );
  }

  if (!hasRequiredChangedDocsCommand(preflightScript)) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must include '${REQUIRED_CHANGED_DOCS_COMMAND}' so changed documentation validator failures are caught before hook-time.`,
        preflightScript
      )
    );
  }

  if (!hasRequiredLlmPolicyRepairCommand(preflightScript)) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must include '${REQUIRED_LLM_POLICY_REPAIR_COMMAND}' so generated .llm index drift is auto-repaired before validation.`,
        preflightScript
      )
    );
  }

  if (!hasRequiredLlmPolicyCommand(preflightScript)) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must include '${REQUIRED_LLM_POLICY_COMMAND}' so .llm skill size/schema, index, and markdown policy failures are caught before hook-time.`,
        preflightScript
      )
    );
  }

  if (
    hasRequiredLlmPolicyRepairCommand(preflightScript) &&
    hasRequiredLlmPolicyCommand(preflightScript) &&
    !hasLlmPolicyRepairBeforeValidation(preflightScript)
  ) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must run '${REQUIRED_LLM_POLICY_REPAIR_COMMAND}' before '${REQUIRED_LLM_POLICY_COMMAND}' so auto-generated .llm index drift is repaired before the check.`,
        preflightScript
      )
    );
  }

  const llmPolicyRepairScript = packageJson?.scripts?.["repair:llm-policy"];
  if (
    typeof llmPolicyRepairScript !== "string" ||
    !hasRequiredSkillsIndexRepairCommand(llmPolicyRepairScript)
  ) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `package.json scripts.repair:llm-policy must include '${REQUIRED_SKILLS_INDEX_REPAIR_COMMAND}' so generated .llm index drift has a zero-touch repair path.`,
        String(llmPolicyRepairScript || "")
      )
    );
  }

  const llmPolicyScript = packageJson?.scripts?.["validate:llm-policy"];
  if (typeof llmPolicyScript !== "string" || llmPolicyScript.trim().length === 0) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        "Missing package.json scripts.validate:llm-policy command.",
        "package.json"
      )
    );
  } else {
    if (!hasRequiredSkillsValidationCommand(llmPolicyScript)) {
      violations.push(
        new Violation(
          "preflight-script",
          1,
          `validate:llm-policy must include '${REQUIRED_SKILLS_VALIDATION_COMMAND}' so skill size/schema failures are caught outside hooks.`,
          llmPolicyScript
        )
      );
    }

    if (!hasRequiredSkillsIndexCheckCommand(llmPolicyScript)) {
      violations.push(
        new Violation(
          "preflight-script",
          1,
          `validate:llm-policy must include '${REQUIRED_SKILLS_INDEX_CHECK_COMMAND}' so generated index drift is still enforced in CI/read-only validation.`,
          llmPolicyScript
        )
      );
    }

    if (!hasRequiredLlmMarkdownCommand(llmPolicyScript)) {
      violations.push(
        new Violation(
          "preflight-script",
          1,
          `validate:llm-policy must include '${REQUIRED_LLM_MARKDOWN_COMMAND}' so .llm ASCII/code/prose checks stay bundled with skill policy.`,
          llmPolicyScript
        )
      );
    }
  }

  if (!hasRequiredScriptsCspellCommand(preflightScript)) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must include '${REQUIRED_SCRIPTS_CSPELL_COMMAND}' so script spelling regressions are caught before hooks.`,
        preflightScript
      )
    );
  }

  if (!hasRequiredWorkflowCspellCommand(preflightScript)) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must include '${REQUIRED_WORKFLOW_CSPELL_COMMAND}' so workflow spelling regressions are caught before hooks.`,
        preflightScript
      )
    );
  }

  if (!hasRequiredWorkflowValidationCommand(preflightScript)) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must include '${REQUIRED_WORKFLOW_VALIDATION_COMMAND}' so workflow policy regressions are caught before hooks.`,
        preflightScript
      )
    );
  }

  if (!hasRequiredBannerSyncCommand(preflightScript)) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must include '${REQUIRED_BANNER_SYNC_COMMAND}' so banner drift is caught before push-time checks.`,
        preflightScript
      )
    );
  }

  if (!hasRequiredChangelogValidationCommand(preflightScript)) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must include '${REQUIRED_CHANGELOG_VALIDATION_COMMAND}' so changelog policy drift is caught before hooks.`,
        preflightScript
      )
    );
  }

  const prePushScript = packageJson?.scripts?.["preflight:pre-push"];
  if (typeof prePushScript === "string" && prePushScript.trim().length > 0) {
    if (!hasRequiredAllCspellCommand(prePushScript)) {
      violations.push(
        new Violation(
          "preflight-script",
          1,
          `preflight:pre-push must include '${REQUIRED_ALL_CSPELL_COMMAND}' so full-repo spelling failures are caught before hook parity.`,
          prePushScript
        )
      );
    } else if (!hasAllCspellBeforePrePushHookParity(prePushScript)) {
      violations.push(
        new Violation(
          "preflight-script",
          1,
          `preflight:pre-push must run '${REQUIRED_ALL_CSPELL_COMMAND}' before the full pre-push hook parity invocation.`,
          prePushScript
        )
      );
    }
  }

  const yamlCheckScript = packageJson?.scripts?.["check:yaml"];
  if (
    typeof yamlCheckScript === "string" &&
    yamlCheckScript.trim().length > 0 &&
    !hasRequiredYamlCommentsCheckCommand(yamlCheckScript)
  ) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `package.json scripts.check:yaml must include '${REQUIRED_YAML_COMMENTS_CHECK_COMMAND}' so breakable long YAML comment lines are auto-detected before yamllint hook-time.`,
        String(yamlCheckScript || "")
      )
    );
  }

  if (!hasRequiredParserPrecheckCommand(preflightScript)) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `preflight:pre-commit must include '${REQUIRED_PRECHECK_PARSER_COMMAND}' to match hook parser coverage.`,
        preflightScript
      )
    );
  }

  try {
    preCommitConfig = readFileSyncImpl(preCommitConfigPath, "utf8");
  } catch (error) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        "Unable to read .pre-commit-config.yaml while validating preflight parser coverage.",
        error.message
      )
    );
    return violations;
  }

  const hasParserSuiteHook = parseHookIds(preCommitConfig).some(
    (hook) => hook.id === REQUIRED_PARSER_SUITE_HOOK_ID
  );
  if (!hasParserSuiteHook) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `Missing required '${REQUIRED_PARSER_SUITE_HOOK_ID}' hook in .pre-commit-config.yaml.`,
        ".pre-commit-config.yaml"
      )
    );
  }

  if (
    hasParserSuiteHook &&
    !hasRequiredParserSuiteTestPaths(preCommitConfig, REQUIRED_PARSER_SUITE_TEST_PATHS)
  ) {
    violations.push(
      new Violation(
        "preflight-script",
        1,
        `The '${REQUIRED_PARSER_SUITE_HOOK_ID}' hook entry must include required regression test path(s): ${REQUIRED_PARSER_SUITE_TEST_PATHS.join(
          ", "
        )}.`,
        REQUIRED_PARSER_SUITE_HOOK_ID
      )
    );
  }

  return violations;
}

function validatePerfBudget(content) {
  const violations = [];
  const result = scoreConfig(content);
  if (result.totalScore > PERF_BUDGET) {
    violations.push(
      new Violation(
        "perf-budget",
        1,
        `Pre-commit perf score ${result.totalScore} exceeds total budget ${PERF_BUDGET}. ${formatReport(result)} See .llm/skills/performance/git-hook-performance.md.`,
        ".pre-commit-config.yaml"
      )
    );
  }

  // Per-hook ceiling: each hook scoring strictly above PER_HOOK_CEILING is
  // its own violation. The total budget catches accumulated drift; this
  // catches single-rule regressions (a `bash -lc` (5 points) added to one
  // entry would otherwise sit comfortably under the total budget).
  const perHookViolations = result.perHookViolations || [];
  for (const violation of perHookViolations) {
    const ruleSummary = violation.contributingRules
      .map((r) => `${r.ruleId}(+${r.score})`)
      .join(", ");
    violations.push(
      new Violation(
        "perf-budget",
        violation.startLine,
        `Hook '${violation.id}' score ${violation.score} exceeds per-hook ceiling ${PER_HOOK_CEILING}. Contributing rules: ${ruleSummary || "(none unwaived)"}. ${formatReport(result)} See .llm/skills/performance/git-hook-performance.md.`,
        ".pre-commit-config.yaml"
      )
    );
  }

  return violations;
}

function validateConfigContent(
  content,
  {
    readFileSyncImpl = fs.readFileSync,
    packageJsonPath = PACKAGE_JSON_PATH,
    preCommitConfigPath = PRE_COMMIT_CONFIG_PATH,
    getConfiguredPrettierSpecFn = getConfiguredPrettierSpec,
    getPinnedPrettierSpecFn = getPinnedPrettierSpec
  } = {}
) {
  const hooks = parseHookEntries(content);
  const hookConfigs = parseHookConfigs(content);
  return [
    ...validatePreflightScriptPolicy(readFileSyncImpl, packageJsonPath, preCommitConfigPath),
    ...validateHookEntries(hooks, hookConfigs),
    ...validateYamllintPolicy(content),
    ...validatePrettierVersionResolution(getConfiguredPrettierSpecFn, getPinnedPrettierSpecFn),
    ...validatePerfBudget(content)
  ];
}

function validateConfigFile(filePath = PRE_COMMIT_CONFIG_PATH, readFileSyncImpl = fs.readFileSync) {
  const content = readFileSyncImpl(filePath, "utf8");
  const resolvedFilePath = path.resolve(filePath);

  const readFileSyncWithCachedConfig = (targetPath, encoding) => {
    if (path.resolve(targetPath) === resolvedFilePath) {
      return content;
    }

    return readFileSyncImpl(targetPath, encoding);
  };

  return validateConfigContent(content, {
    readFileSyncImpl: readFileSyncWithCachedConfig,
    preCommitConfigPath: filePath
  });
}

function main() {
  const violations = validateConfigFile(PRE_COMMIT_CONFIG_PATH);

  if (violations.length === 0) {
    console.log("✅ Pre-commit Node tooling validation passed.");
    process.exit(0);
  }

  console.error(`❌ Found ${violations.length} pre-commit tooling violation(s):`);
  for (const violation of violations) {
    console.error(`\n- ${violation.toString()}`);
  }

  process.exit(1);
}

module.exports = {
  PRE_COMMIT_CONFIG_PATH,
  Violation,
  getIndent,
  parseHookEntries,
  parseHookIds,
  parseHookConfigs,
  tokenizeCommand,
  escapeRegexLiteral,
  hasRequiredPreflightCommand,
  hasRequiredParserPrecheckCommand,
  hasRequiredNodeRepairCommand,
  hasNodeRepairBeforeNodeValidation,
  hasRequiredPreCommitRepairCommand,
  hasPreCommitRepairBeforeHookMarkdown,
  hasRequiredPackageJsonFormatCommand,
  hasRequiredYamlValidationCommand,
  hasRequiredYamlCommentsCheckCommand,
  hasRequiredNodeToolingCommand,
  hasRequiredHookMarkdownCommand,
  hasRequiredChangedDocsCommand,
  hasRequiredLlmMarkdownCommand,
  hasRequiredSkillsValidationCommand,
  hasRequiredLlmPolicyRepairCommand,
  hasRequiredLlmPolicyCommand,
  hasLlmPolicyRepairBeforeValidation,
  hasRequiredSkillsIndexRepairCommand,
  hasRequiredSkillsIndexCheckCommand,
  resolvePrePushScriptForPolicy,
  hasRequiredAllCspellCommand,
  hasAllCspellBeforePrePushHookParity,
  hasRequiredScriptsCspellCommand,
  hasRequiredWorkflowCspellCommand,
  hasRequiredWorkflowValidationCommand,
  hasRequiredBannerSyncCommand,
  hasRequiredChangelogValidationCommand,
  hasNpxInstallPolicy,
  usesManagedJestWrapper,
  isJestRelatedHook,
  hasManagedJestInvocation,
  hasInlinedPrettierEntry,
  hasInlinedPrettierInvocation,
  hasGuardedFixerRestagePattern,
  hasGuardedCsharpierRestagePattern,
  hasGuardedPrettierRestagePattern,
  hasGuardedEolRestagePattern,
  hasGuardedBannerStagePattern,
  hasGuardedYamlCommentRestagePattern,
  hasGuardedMarkdownPipelineRestagePattern,
  hasPortableHookInvocation,
  usesManagedNodeRepair,
  hasRequiredSerialManagedRepairHook,
  hasRequiredSerialYamlCommentFixerHook,
  hasRequiredSerialEolFixerHook,
  hasRequiredSerialMarkdownPipelineHook,
  validateHookEntries,
  validateYamllintPolicy,
  validatePrettierVersionResolution,
  validatePreflightScriptPolicy,
  validatePerfBudget,
  PACKAGE_JSON_PATH,
  REQUIRED_PRECHECK_PARSER_COMMAND,
  REQUIRED_NODE_REPAIR_COMMAND,
  REQUIRED_PRE_COMMIT_REPAIR_COMMAND,
  REQUIRED_NODE_TOOLING_COMMAND,
  REQUIRED_HOOK_MARKDOWN_COMMAND,
  REQUIRED_MARKDOWN_LINK_TEXT_COMMAND,
  REQUIRED_CHANGED_DOCS_COMMAND,
  REQUIRED_LLM_MARKDOWN_COMMAND,
  REQUIRED_SKILLS_VALIDATION_COMMAND,
  REQUIRED_LLM_POLICY_REPAIR_COMMAND,
  REQUIRED_LLM_POLICY_COMMAND,
  REQUIRED_SKILLS_INDEX_REPAIR_COMMAND,
  REQUIRED_SKILLS_INDEX_CHECK_COMMAND,
  REQUIRED_PACKAGE_JSON_FORMAT_COMMAND,
  REQUIRED_YAML_VALIDATION_COMMAND,
  REQUIRED_YAML_COMMENTS_CHECK_COMMAND,
  REQUIRED_ALL_CSPELL_COMMAND,
  REQUIRED_PREPUSH_PREFLIGHT_RUNNER_COMMAND,
  REQUIRED_SCRIPTS_CSPELL_COMMAND,
  REQUIRED_WORKFLOW_CSPELL_COMMAND,
  REQUIRED_WORKFLOW_VALIDATION_COMMAND,
  REQUIRED_BANNER_SYNC_COMMAND,
  REQUIRED_CHANGELOG_VALIDATION_COMMAND,
  REQUIRED_PARSER_SUITE_HOOK_ID,
  REQUIRED_PARSER_SUITE_TEST_PATHS,
  hasRequiredParserSuiteTestPaths,
  validateConfigContent,
  validateConfigFile
};

if (require.main === module) {
  main();
}
