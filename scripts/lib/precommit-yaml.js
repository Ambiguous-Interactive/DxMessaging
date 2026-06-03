"use strict";

/**
 * Shared helpers for walking .pre-commit-config.yaml hook blocks line-by-line.
 *
 * These helpers intentionally do not parse YAML semantically. Hook stage and
 * filter policy tests need stable line numbers and need to detect comment
 * markers (for example "# perf-allow:") that a structural YAML parser would
 * drop, so the parser walks raw lines and returns slices.
 *
 * Public API:
 *   - getIndent(line) -> number
 *   - findHookBlock(lines, hookId) -> { startLine, lines } | null
 *   - extractStagesFromHookBlock(hookBlock) -> string[]
 *   - findAllHookBlocks(lines) -> Array<{ id, startLine, lines }>
 */

function getIndent(line) {
  return line.length - line.trimStart().length;
}

function findHookBlock(lines, hookId) {
  let startIndex = -1;
  let hookIndent = -1;

  for (let i = 0; i < lines.length; i++) {
    const idMatch = /^(\s*)-\s+id:\s*([^\s#]+)\s*$/.exec(lines[i]);
    if (!idMatch || idMatch[2].trim() !== hookId) {
      continue;
    }

    startIndex = i;
    hookIndent = idMatch[1].length;
    break;
  }

  if (startIndex === -1) {
    return null;
  }

  let endIndex = lines.length;
  for (let i = startIndex + 1; i < lines.length; i++) {
    const idMatch = /^(\s*)-\s+id:\s*([^\s#]+)\s*$/.exec(lines[i]);
    if (!idMatch) {
      continue;
    }

    if (idMatch[1].length === hookIndent) {
      endIndex = i;
      break;
    }
  }

  return {
    startLine: startIndex + 1,
    lines: lines.slice(startIndex, endIndex)
  };
}

function extractStagesFromHookBlock(hookBlock) {
  if (!hookBlock) {
    return [];
  }

  const stages = [];

  for (let i = 0; i < hookBlock.lines.length; i++) {
    const stagesMatch = /^(\s*)stages:\s*$/.exec(hookBlock.lines[i]);
    if (!stagesMatch) {
      continue;
    }

    const stagesIndent = stagesMatch[1].length;

    for (let j = i + 1; j < hookBlock.lines.length; j++) {
      const line = hookBlock.lines[j];
      if (!line.trim()) {
        continue;
      }

      const indent = getIndent(line);
      if (indent <= stagesIndent) {
        break;
      }

      const stageMatch = /^\s*-\s*([^\s#]+)\s*$/.exec(line);
      if (stageMatch) {
        stages.push(stageMatch[1].trim());
      }
    }

    break;
  }

  return stages;
}

function findAllHookBlocks(lines) {
  const blocks = [];
  const ids = [];

  for (let i = 0; i < lines.length; i++) {
    const idMatch = /^(\s*)-\s+id:\s*([^\s#]+)\s*$/.exec(lines[i]);
    if (idMatch) {
      ids.push({ id: idMatch[2].trim(), startIndex: i, indent: idMatch[1].length });
    }
  }

  for (let n = 0; n < ids.length; n++) {
    const start = ids[n];
    let endIndex = lines.length;
    for (let m = n + 1; m < ids.length; m++) {
      if (ids[m].indent === start.indent) {
        endIndex = ids[m].startIndex;
        break;
      }
    }
    blocks.push({
      id: start.id,
      startLine: start.startIndex + 1,
      lines: lines.slice(start.startIndex, endIndex)
    });
  }

  return blocks;
}

module.exports = {
  getIndent,
  findHookBlock,
  extractStagesFromHookBlock,
  findAllHookBlocks
};
