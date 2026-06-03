"use strict";

/**
 * precommit-stage-model.js
 *
 * Thin, parser-derived model of the stage <-> hook-id structure declared in
 * `.pre-commit-config.yaml`. Built ONLY on the line-walking helpers in
 * `precommit-yaml.js` (`findAllHookBlocks` / `extractStagesFromHookBlock`)
 * plus the top-level `default_install_hook_types`.
 *
 * It deliberately contains NO file -> hook matching logic. `scripts/preflight.js`
 * delegates all `types`/`files`/`exclude`/`always_run`/`pass_filenames`
 * selection to pre-commit itself; this module only answers two structural
 * questions:
 *
 *   - `stagesInConfig()`     -> the set of stages any hook declares, merged
 *                               with `default_install_hook_types`.
 *   - `hookIdsForStage(stg)` -> the hook ids declared (or defaulted) for one
 *                               stage.
 *
 * Stage defaulting rule (matches pre-commit's own semantics): a hook with NO
 * explicit `stages:` block participates in EVERY stage in
 * `default_install_hook_types`. A hook WITH an explicit `stages:` block
 * participates only in the listed stages.
 */

const fs = require("fs");
const path = require("path");
const { normalizeToLf } = require("./quote-parser");
const { findAllHookBlocks, extractStagesFromHookBlock } = require("./precommit-yaml");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const CONFIG_PATH = path.join(REPO_ROOT, ".pre-commit-config.yaml");

/**
 * Read and LF-normalize the config file, returning its lines. Injectable via
 * `options.configText` for tests that want to drive a synthetic config without
 * touching disk.
 *
 * @param {object} [options]
 * @param {string} [options.configText] Raw config text override.
 * @param {string} [options.configPath] Config path override.
 * @returns {string[]} LF-split config lines.
 */
function loadConfigLines(options = {}) {
  const { configText, configPath = CONFIG_PATH } = options;
  const raw = typeof configText === "string" ? configText : fs.readFileSync(configPath, "utf8");
  return normalizeToLf(raw).split("\n");
}

/**
 * Parse the top-level `default_install_hook_types:` list. These are the stages
 * a hook participates in when it declares no explicit `stages:` block.
 *
 * The list is a top-level (zero-indent) key whose items are `  - <stage>`
 * entries until the next zero-indent key. We stop at the first zero-indent,
 * non-blank, non-list line after the key.
 *
 * @param {string[]} lines LF-split config lines.
 * @returns {string[]} ordered default stages (may be empty).
 */
function parseDefaultInstallHookTypes(lines) {
  const stages = [];
  let inBlock = false;

  for (const line of lines) {
    if (!inBlock) {
      if (/^default_install_hook_types:\s*$/.test(line)) {
        inBlock = true;
      }
      continue;
    }

    if (line.trim().length === 0) {
      continue;
    }

    const itemMatch = /^\s+-\s*([^\s#]+)\s*$/.exec(line);
    if (itemMatch) {
      stages.push(itemMatch[1].trim());
      continue;
    }

    // A non-blank, non-list line. If it is indented it is unexpected nesting;
    // a zero-indent line is the next top-level key -> the block has ended.
    if (/^\S/.test(line)) {
      break;
    }
  }

  return stages;
}

/**
 * Build the structural model once from a set of config lines.
 *
 * @param {string[]} lines LF-split config lines.
 * @returns {{ defaultStages: string[], hooks: Array<{ id: string, stages: string[] }> }}
 *   `hooks[].stages` is the EFFECTIVE stage list (explicit when declared, else
 *   `defaultStages`).
 */
function buildModel(lines) {
  const defaultStages = parseDefaultInstallHookTypes(lines);
  const blocks = findAllHookBlocks(lines);

  const hooks = blocks.map((block) => {
    const explicit = extractStagesFromHookBlock(block);
    const stages = explicit.length > 0 ? explicit : defaultStages.slice();
    return { id: block.id, stages };
  });

  return { defaultStages, hooks };
}

/**
 * The set of stages any hook declares, merged with
 * `default_install_hook_types`.
 *
 * @param {object} [options] See {@link loadConfigLines}.
 * @returns {Set<string>} stage names.
 */
function stagesInConfig(options = {}) {
  const lines = loadConfigLines(options);
  const { defaultStages, hooks } = buildModel(lines);

  const stages = new Set(defaultStages);
  for (const hook of hooks) {
    for (const stage of hook.stages) {
      stages.add(stage);
    }
  }
  return stages;
}

/**
 * The hook ids that participate in a given stage (explicit or defaulted),
 * de-duplicated and in config order.
 *
 * @param {string} stage stage name (e.g. "pre-commit", "pre-push").
 * @param {object} [options] See {@link loadConfigLines}.
 * @returns {string[]} hook ids for the stage.
 */
function hookIdsForStage(stage, options = {}) {
  const lines = loadConfigLines(options);
  const { hooks } = buildModel(lines);

  const ids = [];
  const seen = new Set();
  for (const hook of hooks) {
    if (hook.stages.includes(stage) && !seen.has(hook.id)) {
      seen.add(hook.id);
      ids.push(hook.id);
    }
  }
  return ids;
}

module.exports = {
  REPO_ROOT,
  CONFIG_PATH,
  loadConfigLines,
  parseDefaultInstallHookTypes,
  buildModel,
  stagesInConfig,
  hookIdsForStage
};
