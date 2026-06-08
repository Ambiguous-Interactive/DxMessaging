#!/usr/bin/env node
"use strict";

const fs = require("fs");
const path = require("path");

const { maskCommentsAndStrings } = require("./lib/source-stripping");
const { lineNumberAt } = require("./lib/repo-files");

const REPO_ROOT = path.resolve(__dirname, "..");
const TEST_ROOTS = [
  path.join(REPO_ROOT, "scripts", "__tests__"),
  path.join(REPO_ROOT, "scripts", "lib", "__tests__")
];

const IDENT = "[A-Za-z_$][\\w$]*";
const SPAWNER_NAME = "(?:spawnSync|spawn|execFileSync|execFile|spawnPlatformCommandSync)";
const SPAWNER_EXPRESSION = `(?:${IDENT}\\s*\\.\\s*)?${SPAWNER_NAME}`;
const SPAWNER_CALL_RE =
  /\b(?:spawnSync|spawn|execFileSync|execFile|spawnPlatformCommandSync)\s*\(/g;
const PWSH_COMMANDS = new Set(["pwsh", "pwsh.exe", "powershell", "powershell.exe"]);
const PWSH_COMMAND_VARIABLES = new Set(["REAL_PWSH", "PWSH", "pwshPath", "PWSH_PATH"]);
const NORMALIZING_HELPERS = new Set(["normalizePwshText", "combinedText", "stdoutText"]);
const SPAWN_STATUS_LABEL = 'expect.getState().currentTestName || "pwsh harness"';
const NON_METHOD_BLOCK_KEYWORDS = new Set([
  "catch",
  "for",
  "function",
  "if",
  "switch",
  "while",
  "with"
]);

function toPosixPath(value) {
  return value.split(path.sep).join("/");
}

function toRepoRelative(absolutePath) {
  return toPosixPath(path.relative(REPO_ROOT, absolutePath));
}

function usage() {
  return [
    "Usage: node scripts/fix-pwsh-output-assertions.js [--check] [--] [files...]",
    "",
    "Auto-rewrites common Jest assertions that check multi-word phrases against",
    "raw PowerShell stdout/stderr so they use scripts/lib/pwsh-output.js helpers.",
    "Without files, scans scripts/__tests__ and scripts/lib/__tests__."
  ].join("\n");
}

function readQuotedString(source, quoteIndex) {
  const quote = source[quoteIndex];
  if (quote !== '"' && quote !== "'") {
    return null;
  }

  let value = "";
  for (let i = quoteIndex + 1; i < source.length; i += 1) {
    const ch = source[i];
    if (ch === "\\") {
      if (i + 1 < source.length) {
        value += source[i + 1];
        i += 1;
      }
      continue;
    }
    if (ch === quote) {
      return { value, end: i + 1 };
    }
    value += ch;
  }

  return null;
}

function readTemplateFixedText(source, backtickIndex) {
  if (source[backtickIndex] !== "`") {
    return null;
  }

  let text = "";
  let expressionDepth = 0;
  for (let i = backtickIndex + 1; i < source.length; i += 1) {
    const ch = source[i];
    const next = source[i + 1];
    if (ch === "\\") {
      if (expressionDepth === 0 && i + 1 < source.length) {
        text += source[i + 1];
      }
      i += 1;
      continue;
    }
    if (expressionDepth === 0 && ch === "`") {
      return { value: text, end: i + 1 };
    }
    if (expressionDepth === 0 && ch === "$" && next === "{") {
      expressionDepth = 1;
      i += 1;
      continue;
    }
    if (expressionDepth > 0) {
      if (ch === "{") {
        expressionDepth += 1;
      } else if (ch === "}") {
        expressionDepth -= 1;
      }
      continue;
    }
    text += ch;
  }

  return null;
}

function hasWrappableWordBoundary(value) {
  return /\S\s+\S/.test(value);
}

function argumentIsPhraseAt(source, startIndex, phraseVars = new Set()) {
  let index = startIndex;
  while (index < source.length && /\s/.test(source[index])) {
    index += 1;
  }

  const ch = source[index];
  if (ch === '"' || ch === "'") {
    const literal = readQuotedString(source, index);
    return Boolean(literal && hasWrappableWordBoundary(literal.value));
  }
  if (ch === "`") {
    const literal = readTemplateFixedText(source, index);
    return Boolean(literal && hasWrappableWordBoundary(literal.value));
  }

  const identifier = new RegExp(String.raw`^(${IDENT})\b`).exec(source.slice(index));
  if (identifier && phraseVars.has(identifier[1])) {
    let next = index + identifier[0].length;
    while (next < source.length && /\s/.test(source[next])) {
      next += 1;
    }
    return source[next] === ")" || source[next] === ",";
  }

  return false;
}

function hasPhraseMatcherAfterExpect(source, mask, afterExpectCloseIndex, phraseVars = new Set()) {
  const matcherRe = /\s*(?:\.not)?\.(toContain|toMatch)\s*\(/y;
  matcherRe.lastIndex = afterExpectCloseIndex;
  const match = matcherRe.exec(mask);
  if (!match) {
    return false;
  }
  if (match[1] === "toMatch") {
    return true;
  }
  return argumentIsPhraseAt(source, matcherRe.lastIndex, phraseVars);
}

function spawnCommandIsPwshAt(source, mask, openParenIndex) {
  let index = openParenIndex + 1;
  while (index < mask.length && /\s/.test(mask[index])) {
    index += 1;
  }

  const ch = mask[index];
  if (ch === '"' || ch === "'") {
    const literal = readQuotedString(source, index);
    return Boolean(literal && PWSH_COMMANDS.has(literal.value.toLowerCase()));
  }
  if (ch === "`") {
    const literal = readTemplateFixedText(source, index);
    return Boolean(literal && PWSH_COMMANDS.has(literal.value.toLowerCase()));
  }

  const identifier = /^([A-Za-z_$][\w$]*)/.exec(mask.slice(index));
  return Boolean(identifier && PWSH_COMMAND_VARIABLES.has(identifier[1]));
}

function hasPwshSpawn(source) {
  const mask = maskCommentsAndStrings(source);
  SPAWNER_CALL_RE.lastIndex = 0;
  let match;
  while ((match = SPAWNER_CALL_RE.exec(mask)) !== null) {
    const openParenIndex = match.index + match[0].lastIndexOf("(");
    if (spawnCommandIsPwshAt(source, mask, openParenIndex)) {
      return true;
    }
  }
  return false;
}

function collectTestFilesUnder(root) {
  const files = [];
  if (!fs.existsSync(root)) {
    return files;
  }

  for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
    const absolutePath = path.join(root, entry.name);
    if (entry.isDirectory()) {
      files.push(...collectTestFilesUnder(absolutePath));
    } else if (entry.isFile() && entry.name.endsWith(".test.js")) {
      files.push(absolutePath);
    }
  }

  return files;
}

function listDefaultFiles() {
  const files = [];
  for (const root of TEST_ROOTS) {
    files.push(...collectTestFilesUnder(root));
  }
  return files;
}

function resolveImportPath(filePath) {
  const target = path.join(REPO_ROOT, "scripts", "lib", "pwsh-output.js");
  let relative = toPosixPath(path.relative(path.dirname(filePath), target));
  relative = relative.replace(/\.js$/, "");
  return relative.startsWith(".") ? relative : `./${relative}`;
}

function parseImportNames(rawNames) {
  return rawNames
    .split(",")
    .map((name) => name.trim())
    .filter(Boolean);
}

function renderImport(names, importPath) {
  return `const { ${names.join(", ")} } = require("${importPath}");`;
}

function ensurePwshOutputImport(source, filePath, helpers) {
  if (helpers.size === 0) {
    return source;
  }

  const importPath = resolveImportPath(filePath);
  const escapedImportPath = importPath.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const existingImportRe = new RegExp(
    `^const\\s+\\{([^}]+)\\}\\s*=\\s*require\\(["']${escapedImportPath}["']\\);$`,
    "m"
  );
  const existing = source.match(existingImportRe);
  if (existing) {
    const names = parseImportNames(existing[1]);
    for (const helper of helpers) {
      if (!names.includes(helper)) {
        names.push(helper);
      }
    }
    names.sort();
    return source.replace(existingImportRe, renderImport(names, importPath));
  }

  const lines = source.split(/\n/);
  let insertAt = -1;
  for (let i = 0; i < Math.min(lines.length, 80); i += 1) {
    if (/^(?:const|let|var)\s+.+\s*=\s*require\(.+\);$/.test(lines[i])) {
      insertAt = i + 1;
    }
  }

  const names = Array.from(helpers).sort();
  const importLine = renderImport(names, importPath);
  if (insertAt >= 0) {
    lines.splice(insertAt, 0, importLine);
    return lines.join("\n");
  }

  if (lines[0] === '"use strict";') {
    lines.splice(1, 0, "", importLine);
    return lines.join("\n");
  }

  return `${importLine}\n${source}`;
}

function rawMergePattern(prefix, suffix) {
  const stdout = String.raw`\$\{\s*(${IDENT})\.stdout\s*(?:\|\|\s*["']{2})?\s*\}`;
  const stderr = String.raw`\$\{\s*(${IDENT})\.stderr\s*(?:\|\|\s*["']{2})?\s*\}`;
  return new RegExp(`${prefix}${stdout}\\s*${stderr}${suffix}`, "g");
}

function applyMaskedReplacements(source, regex, buildReplacement) {
  const mask = maskCommentsAndStrings(source);
  regex.lastIndex = 0;

  const replacements = [];
  let match;
  while ((match = regex.exec(mask)) !== null) {
    const text = buildReplacement(match, source, mask);
    if (text === null || text === undefined) {
      continue;
    }
    replacements.push({
      start: match.index,
      end: match.index + match[0].length,
      text
    });
  }

  if (replacements.length === 0) {
    return source;
  }

  let out = "";
  let cursor = 0;
  for (const replacement of replacements) {
    if (replacement.start < cursor) {
      continue;
    }
    out += source.slice(cursor, replacement.start);
    out += replacement.text;
    cursor = replacement.end;
  }
  out += source.slice(cursor);
  return out;
}

function applySpanReplacements(source, replacements) {
  if (replacements.length === 0) {
    return source;
  }
  replacements.sort((left, right) => left.start - right.start || left.end - right.end);
  let out = "";
  let cursor = 0;
  for (const replacement of replacements) {
    if (replacement.start < cursor) {
      continue;
    }
    out += source.slice(cursor, replacement.start);
    out += replacement.text;
    cursor = replacement.end;
  }
  out += source.slice(cursor);
  return out;
}

function variableHasPhraseAssertion(source, mask, variableName) {
  const escaped = variableName.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const re = new RegExp(String.raw`\bexpect\(\s*${escaped}\s*\)`, "g");
  let match;
  while ((match = re.exec(mask)) !== null) {
    if (hasPhraseMatcherAfterExpect(source, mask, match.index + match[0].length)) {
      return true;
    }
  }
  return false;
}

function matchingCloseIndex(source, openIndex, openChar, closeChar) {
  let depth = 0;
  for (let i = openIndex; i < source.length; i += 1) {
    const ch = source[i];
    if (ch === openChar) {
      depth += 1;
    } else if (ch === closeChar) {
      depth -= 1;
      if (depth === 0) {
        return i;
      }
    }
  }
  return -1;
}

function matchingOpenIndex(source, closeIndex, openChar, closeChar) {
  let depth = 0;
  for (let i = closeIndex; i >= 0; i -= 1) {
    const ch = source[i];
    if (ch === closeChar) {
      depth += 1;
    } else if (ch === openChar) {
      depth -= 1;
      if (depth === 0) {
        return i;
      }
    }
  }
  return -1;
}

function declaratorInitializerEnd(source, start) {
  let depth = 0;
  for (let i = start; i < source.length; i += 1) {
    const ch = source[i];
    if (ch === "(" || ch === "[" || ch === "{") {
      depth += 1;
    } else if (ch === ")" || ch === "]" || ch === "}") {
      if (depth === 0) {
        return { end: i, terminator: "close" };
      }
      depth -= 1;
    } else if (ch === "," && depth === 0) {
      return { end: i, terminator: "," };
    } else if (ch === ";" && depth === 0) {
      return { end: i, terminator: ";" };
    }
  }
  return { end: source.length, terminator: "eof" };
}

function findEnclosingBlockOpen(mask, index) {
  const stack = [];
  for (let i = 0; i < index; i += 1) {
    const ch = mask[i];
    if (ch === "{") {
      stack.push(i);
    } else if (ch === "}" && stack.length > 0) {
      stack.pop();
    }
  }
  return stack.length > 0 ? stack[stack.length - 1] : -1;
}

function findBindingScopeEnd(mask, declarationIndex) {
  const blockOpen = findEnclosingBlockOpen(mask, declarationIndex);
  if (blockOpen < 0) {
    return mask.length;
  }

  const blockClose = matchingCloseIndex(mask, blockOpen, "{", "}");
  return blockClose >= 0 ? blockClose : mask.length;
}

function findBindingScopeStart(mask, declarationIndex) {
  const blockOpen = findEnclosingBlockOpen(mask, declarationIndex);
  return blockOpen >= 0 ? blockOpen + 1 : 0;
}

function previousNonWhitespaceIndex(source, index) {
  for (let i = index - 1; i >= 0; i -= 1) {
    if (!/\s/.test(source[i])) {
      return i;
    }
  }
  return -1;
}

function methodNameBeforeParen(mask, argsOpen) {
  const nameEnd = previousNonWhitespaceIndex(mask, argsOpen);
  if (nameEnd < 0) {
    return null;
  }

  const endChar = mask[nameEnd];
  if (
    endChar === "]" ||
    endChar === '"' ||
    endChar === "'" ||
    endChar === "`" ||
    /\d/.test(endChar)
  ) {
    return "__computed_or_literal_method__";
  }

  let nameStart = nameEnd;
  while (nameStart >= 0 && /[\w$]/.test(mask[nameStart])) {
    nameStart -= 1;
  }
  const name = mask.slice(nameStart + 1, nameEnd + 1);
  if (!name || NON_METHOD_BLOCK_KEYWORDS.has(name)) {
    return null;
  }
  return name;
}

function splitParameterNames(mask, argsOpen, argsClose) {
  return splitTopLevelArgs(mask.slice(argsOpen + 1, argsClose))
    .map((param) => /^([A-Za-z_$][\w$]*)\b/.exec(param.trim())?.[1] ?? "")
    .filter(Boolean);
}

function collectFunctionBodyRanges(mask, start = 0, end = mask.length) {
  const ranges = [];

  const functionRe = /\bfunction\b/g;
  let match;
  while ((match = functionRe.exec(mask)) !== null) {
    if (match.index < start || match.index >= end) {
      continue;
    }
    const argsOpen = mask.indexOf("(", match.index);
    if (argsOpen < 0 || argsOpen >= end) {
      continue;
    }
    const argsClose = matchingCloseIndex(mask, argsOpen, "(", ")");
    if (argsClose < 0 || argsClose >= end) {
      continue;
    }
    let bodyOpen = argsClose + 1;
    while (bodyOpen < end && /\s/.test(mask[bodyOpen])) {
      bodyOpen += 1;
    }
    if (mask[bodyOpen] !== "{") {
      continue;
    }
    const bodyClose = matchingCloseIndex(mask, bodyOpen, "{", "}");
    if (bodyClose >= 0 && bodyClose <= end) {
      ranges.push({ start: bodyOpen, end: bodyClose });
    }
  }

  const arrowRe = /=>/g;
  while ((match = arrowRe.exec(mask)) !== null) {
    if (match.index < start || match.index >= end) {
      continue;
    }
    let bodyOpen = match.index + 2;
    while (bodyOpen < end && /\s/.test(mask[bodyOpen])) {
      bodyOpen += 1;
    }
    if (mask[bodyOpen] !== "{") {
      const bodyEnd = declaratorInitializerEnd(mask, bodyOpen).end;
      if (bodyEnd > bodyOpen && bodyEnd <= end) {
        ranges.push({ start: bodyOpen, end: bodyEnd });
      }
      continue;
    }
    const bodyClose = matchingCloseIndex(mask, bodyOpen, "{", "}");
    if (bodyClose >= 0 && bodyClose <= end) {
      ranges.push({ start: bodyOpen, end: bodyClose });
    }
  }

  for (
    let argsOpen = mask.indexOf("(", start);
    argsOpen >= 0 && argsOpen < end;
    argsOpen = mask.indexOf("(", argsOpen + 1)
  ) {
    const methodName = methodNameBeforeParen(mask, argsOpen);
    if (methodName === null) {
      continue;
    }
    const argsClose = matchingCloseIndex(mask, argsOpen, "(", ")");
    if (argsClose < 0 || argsClose >= end) {
      continue;
    }
    let bodyOpen = argsClose + 1;
    while (bodyOpen < end && /\s/.test(mask[bodyOpen])) {
      bodyOpen += 1;
    }
    if (mask[bodyOpen] !== "{") {
      continue;
    }
    const bodyClose = matchingCloseIndex(mask, bodyOpen, "{", "}");
    if (bodyClose >= 0 && bodyClose <= end) {
      ranges.push({ start: bodyOpen, end: bodyClose });
    }
  }

  ranges.sort((left, right) => left.start - right.start || left.end - right.end);
  return ranges;
}

function findVarScopeEnd(mask, declarationIndex) {
  let nearest = null;
  for (const range of collectFunctionBodyRanges(mask)) {
    if (range.start < declarationIndex && declarationIndex < range.end) {
      if (nearest === null || range.start > nearest.start) {
        nearest = range;
      }
    }
  }
  return nearest === null ? mask.length : nearest.end;
}

function expressionEnd(mask, expressionStart) {
  return declaratorInitializerEnd(mask, expressionStart).end;
}

function readExpressionInfo(mask, start) {
  let expressionStart = start;
  while (expressionStart < mask.length && /\s/.test(mask[expressionStart])) {
    expressionStart += 1;
  }
  const expressionEndIndex = expressionEnd(mask, expressionStart);
  let trimmedEnd = expressionEndIndex;
  while (trimmedEnd > expressionStart && /\s/.test(mask[trimmedEnd - 1])) {
    trimmedEnd -= 1;
  }
  return {
    expr: mask.slice(expressionStart, trimmedEnd),
    start: expressionStart,
    end: trimmedEnd
  };
}

function expressionNestedFunctionBodies(
  mask,
  expressionStart,
  end = expressionEnd(mask, expressionStart)
) {
  return collectFunctionBodyRanges(mask, expressionStart, end).filter(
    (range) => range.start > expressionStart
  );
}

function topLevelTernary(mask, start, end) {
  let depth = 0;
  let question = -1;
  for (let index = start; index < end; index += 1) {
    const ch = mask[index];
    if (ch === "(" || ch === "[" || ch === "{") {
      depth += 1;
      continue;
    }
    if (ch === ")" || ch === "]" || ch === "}") {
      depth = Math.max(0, depth - 1);
      continue;
    }
    if (depth === 0 && ch === "?" && question < 0) {
      question = index;
      continue;
    }
    if (depth === 0 && ch === ":" && question >= 0) {
      return { question, colon: index };
    }
  }
  return null;
}

function directPwshSpawnCallSpansExpression(source, mask, start, end) {
  const expression = mask.slice(start, end);
  const call = new RegExp(String.raw`^(?:${IDENT}\s*\.\s*)?${SPAWNER_NAME}\s*\(`).exec(expression);
  if (!call) {
    return false;
  }
  const openParenIndex = start + call[0].lastIndexOf("(");
  if (!spawnCommandIsPwshAt(source, mask, openParenIndex)) {
    return false;
  }
  const closeParenIndex = matchingCloseIndex(mask, openParenIndex, "(", ")");
  if (closeParenIndex < 0 || closeParenIndex >= end) {
    return false;
  }
  return mask.slice(closeParenIndex + 1, end).trim() === "";
}

function expressionRangeIsPwshSpawnResult(source, mask, start, end) {
  let expressionStart = start;
  let expressionEndIndex = end;
  while (expressionStart < expressionEndIndex && /\s/.test(mask[expressionStart])) {
    expressionStart += 1;
  }
  while (expressionEndIndex > expressionStart && /\s/.test(mask[expressionEndIndex - 1])) {
    expressionEndIndex -= 1;
  }
  if (expressionStart >= expressionEndIndex) {
    return false;
  }

  if (mask.startsWith("await", expressionStart) && !/[\w$]/.test(mask[expressionStart + 5] || "")) {
    return expressionRangeIsPwshSpawnResult(source, mask, expressionStart + 5, expressionEndIndex);
  }

  if (mask[expressionStart] === "(") {
    const close = matchingCloseIndex(mask, expressionStart, "(", ")");
    if (close === expressionEndIndex - 1) {
      return expressionRangeIsPwshSpawnResult(source, mask, expressionStart + 1, close);
    }
  }

  const ternary = topLevelTernary(mask, expressionStart, expressionEndIndex);
  if (ternary !== null) {
    return (
      expressionRangeIsPwshSpawnResult(source, mask, ternary.question + 1, ternary.colon) ||
      expressionRangeIsPwshSpawnResult(source, mask, ternary.colon + 1, expressionEndIndex)
    );
  }

  return directPwshSpawnCallSpansExpression(source, mask, expressionStart, expressionEndIndex);
}

function expressionIsPwshSpawn(source, mask, expressionStart) {
  const end = expressionEnd(mask, expressionStart);
  if (expressionRangeIsPwshSpawnResult(source, mask, expressionStart, end)) {
    return true;
  }
  return false;
}

function calleeNameAt(mask, expressionStart) {
  return new RegExp(String.raw`^(${IDENT})\s*\(`).exec(mask.slice(expressionStart))?.[1] ?? null;
}

function expressionCallsPwshHelper(helpers, mask, expressionStart) {
  const end = expressionEnd(mask, expressionStart);
  const nestedFunctionBodies = expressionNestedFunctionBodies(mask, expressionStart, end);
  const memberCallRe = new RegExp(String.raw`\b(${IDENT}(?:\s*\.\s*${IDENT})+)\s*\(`, "g");
  memberCallRe.lastIndex = expressionStart;
  let memberMatch;
  while ((memberMatch = memberCallRe.exec(mask)) !== null) {
    if (memberMatch.index >= end) {
      break;
    }
    const nested = containsIndex(nestedFunctionBodies, memberMatch.index);
    if (nested !== null) {
      memberCallRe.lastIndex = nested.end + 1;
      continue;
    }
    const helperPath = memberMatch[1].replace(/\s+/g, "");
    if (isPwshSpawnHelperAt(helpers, helperPath, memberMatch.index)) {
      return true;
    }
  }

  const callRe = new RegExp(String.raw`\b(${IDENT})\s*\(`, "g");
  callRe.lastIndex = expressionStart;

  let match;
  while ((match = callRe.exec(mask)) !== null) {
    if (match.index >= end) {
      break;
    }
    const nested = containsIndex(nestedFunctionBodies, match.index);
    if (nested !== null) {
      callRe.lastIndex = nested.end + 1;
      continue;
    }
    const prev = previousNonWhitespaceIndex(mask, match.index);
    if (prev >= 0 && mask[prev] === ".") {
      continue;
    }
    if (isPwshSpawnHelperAt(helpers, match[1], match.index)) {
      return true;
    }
  }

  return false;
}

function outermostCalleeName(expression) {
  const match = new RegExp(String.raw`^(${IDENT})\s*\(`).exec(expression);
  if (!match) {
    return null;
  }
  const close = matchingCloseIndex(expression, match[0].length - 1, "(", ")");
  return close === expression.length - 1 ? match[1] : null;
}

function stripEnclosingParens(expression) {
  const trimmed = expression.trim();
  if (!trimmed.startsWith("(")) {
    return trimmed;
  }
  const close = matchingCloseIndex(trimmed, 0, "(", ")");
  return close === trimmed.length - 1 ? trimmed.slice(1, -1).trim() : trimmed;
}

function splitTopLevelPlus(expression) {
  const operands = [];
  let depth = 0;
  let quote = null;
  let current = "";

  for (let index = 0; index < expression.length; index += 1) {
    const ch = expression[index];
    const prev = expression[index - 1] || "";
    if (quote !== null) {
      current += ch;
      if (ch === quote && prev !== "\\") {
        quote = null;
      }
      continue;
    }
    if (ch === '"' || ch === "'" || ch === "`") {
      quote = ch;
      current += ch;
      continue;
    }
    if (ch === "(" || ch === "[" || ch === "{") {
      depth += 1;
      current += ch;
      continue;
    }
    if (ch === ")" || ch === "]" || ch === "}") {
      depth = Math.max(0, depth - 1);
      current += ch;
      continue;
    }
    if (ch === "+" && depth === 0 && expression[index + 1] !== "+" && prev !== "+") {
      operands.push(current.trim());
      current = "";
      continue;
    }
    current += ch;
  }

  operands.push(current.trim());
  return operands;
}

function splitTopLevelArgs(argsText) {
  const args = [];
  let depth = 0;
  let quote = null;
  let current = "";

  for (let index = 0; index < argsText.length; index += 1) {
    const ch = argsText[index];
    const prev = argsText[index - 1] || "";
    if (quote !== null) {
      current += ch;
      if (ch === quote && prev !== "\\") {
        quote = null;
      }
      continue;
    }
    if (ch === '"' || ch === "'" || ch === "`") {
      quote = ch;
      current += ch;
      continue;
    }
    if (ch === "(" || ch === "[" || ch === "{") {
      depth += 1;
      current += ch;
      continue;
    }
    if (ch === ")" || ch === "]" || ch === "}") {
      depth = Math.max(0, depth - 1);
      current += ch;
      continue;
    }
    if (ch === "," && depth === 0) {
      args.push(current.trim());
      current = "";
      continue;
    }
    current += ch;
  }

  if (current.trim().length > 0) {
    args.push(current.trim());
  }
  return args;
}

function isBareRawOutputMember(operand) {
  let core = stripEnclosingParens(operand);
  core = core.replace(/\|\|\s*(["'`])[\s\S]*?\1\s*$/, "").trim();
  core = core.replace(/\?\?\s*(["'`])[\s\S]*?\1\s*$/, "").trim();
  core = stripEnclosingParens(core);
  return /\.\s*(?:stdout|stderr)\s*$/.test(core);
}

function isStringOrTemplateOperand(operand) {
  const trimmed = operand.trim();
  return (
    trimmed.length >= 2 && /^["'`]/.test(trimmed) && trimmed[trimmed.length - 1] === trimmed[0]
  );
}

function isTemplateInterpolatingRawOutput(operand) {
  const trimmed = operand.trim();
  return (
    trimmed.length >= 2 &&
    trimmed[0] === "`" &&
    trimmed[trimmed.length - 1] === "`" &&
    /\.\s*(?:stdout|stderr)\b/.test(trimmed)
  );
}

function isRawMergeExpression(expression) {
  const core = stripEnclosingParens(expression);
  const callee = outermostCalleeName(core);
  if (callee !== null && NORMALIZING_HELPERS.has(callee)) {
    return false;
  }
  if (!/\.\s*(?:stdout|stderr)\b/.test(core)) {
    return false;
  }

  const operands = splitTopLevelPlus(core);
  if (operands.length === 1) {
    return core.startsWith("`");
  }

  return (
    operands.some(
      (operand) => isBareRawOutputMember(operand) || isTemplateInterpolatingRawOutput(operand)
    ) && operands.some(isStringOrTemplateOperand)
  );
}

function expressionHasPwshResultMember(expression, expressionStart, bindings) {
  const expressionMask = maskCommentsAndStrings(expression);
  const memberRe = new RegExp(String.raw`\b(${IDENT})\s*\.\s*(?:stdout|stderr)\b`, "g");
  let match;
  while ((match = memberRe.exec(expressionMask)) !== null) {
    if (isPwshResultAt(bindings, match[1], expressionStart + match.index)) {
      return true;
    }
  }
  return false;
}

function rawMergeReceiverNames(expression) {
  const names = new Set();
  const expressionMask = maskCommentsAndStrings(expression);
  const memberRe = new RegExp(String.raw`\b(${IDENT})\s*\.\s*(?:stdout|stderr)\b`, "g");
  let match;
  while ((match = memberRe.exec(expressionMask)) !== null) {
    names.add(match[1]);
  }
  return names;
}

function containsIndex(ranges, index) {
  return ranges.find((range) => range.start <= index && index <= range.end) ?? null;
}

function returnExpressionStarts(mask, start, end) {
  const starts = [];
  const nestedFunctionBodies = collectFunctionBodyRanges(mask, start, end).filter(
    (range) => range.start > start
  );

  for (let index = start; index < end; index += 1) {
    const nested = containsIndex(nestedFunctionBodies, index);
    if (nested !== null) {
      index = nested.end;
      continue;
    }

    if (
      mask.startsWith("return", index) &&
      !/[\w$]/.test(mask[index - 1] || "") &&
      !/[\w$]/.test(mask[index + "return".length] || "")
    ) {
      let expressionStart = index + "return".length;
      while (expressionStart < end && /\s/.test(mask[expressionStart])) {
        expressionStart += 1;
      }
      if (expressionStart < end) {
        starts.push(expressionStart);
      }
      index = expressionStart;
    }
  }

  return starts;
}

function bodyHasReturnedPwshBinding(source, mask, bodyOpen, returnStart, name) {
  const escapedName = name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const declarationRe = new RegExp(String.raw`\b(?:const|let|var)\s+${escapedName}\s*=`, "g");
  declarationRe.lastIndex = bodyOpen + 1;

  let match;
  while ((match = declarationRe.exec(mask)) !== null) {
    if (match.index >= returnStart) {
      break;
    }

    let expressionStart = declarationRe.lastIndex;
    while (expressionStart < mask.length && /\s/.test(mask[expressionStart])) {
      expressionStart += 1;
    }
    const expressionEndIndex = declaratorInitializerEnd(mask, expressionStart).end;
    if (expressionRangeIsPwshSpawnResult(source, mask, expressionStart, expressionEndIndex)) {
      return true;
    }
  }

  return false;
}

function bodyReturnsPwshSpawn(source, mask, bodyOpen, bodyClose) {
  return returnExpressionStarts(mask, bodyOpen + 1, bodyClose).some((expressionStart) => {
    if (expressionIsPwshSpawn(source, mask, expressionStart)) {
      return true;
    }

    const { expr } = readExpressionInfo(mask, expressionStart);
    const returnedName = new RegExp(String.raw`^(${IDENT})$`).exec(expr)?.[1] ?? null;
    return (
      returnedName !== null &&
      bodyHasReturnedPwshBinding(source, mask, bodyOpen, expressionStart, returnedName)
    );
  });
}

function collectHelperBindings(source, mask = maskCommentsAndStrings(source)) {
  const helpers = [];

  const functionRe = new RegExp(String.raw`\bfunction\s+(${IDENT})\s*\(`, "g");
  let match;
  while ((match = functionRe.exec(mask)) !== null) {
    const argsOpen = mask.indexOf("(", match.index);
    const argsClose = matchingCloseIndex(mask, argsOpen, "(", ")");
    if (argsClose < 0) {
      continue;
    }
    let bodyOpen = argsClose + 1;
    while (bodyOpen < mask.length && /\s/.test(mask[bodyOpen])) {
      bodyOpen += 1;
    }
    if (mask[bodyOpen] !== "{") {
      continue;
    }
    const bodyClose = matchingCloseIndex(mask, bodyOpen, "{", "}");
    if (bodyClose < 0) {
      continue;
    }
    helpers.push({
      name: match[1],
      start: findBindingScopeStart(mask, match.index),
      end: findBindingScopeEnd(mask, match.index),
      isPwshHelper: bodyReturnsPwshSpawn(source, mask, bodyOpen, bodyClose)
    });
  }

  const arrowRe = new RegExp(
    String.raw`\b(const|let|var)\s+(${IDENT})\s*=\s*(?:async\s+)?(?:\([^)]*\)|${IDENT})\s*=>`,
    "g"
  );
  while ((match = arrowRe.exec(mask)) !== null) {
    let bodyStart = arrowRe.lastIndex;
    while (bodyStart < mask.length && /\s/.test(mask[bodyStart])) {
      bodyStart += 1;
    }
    if (mask[bodyStart] === "{") {
      const bodyClose = matchingCloseIndex(mask, bodyStart, "{", "}");
      if (bodyClose < 0) {
        continue;
      }
      const nameStart = match.index + match[0].indexOf(match[2]);
      helpers.push({
        name: match[2],
        start: nameStart,
        end:
          match[1] === "var"
            ? findVarScopeEnd(mask, nameStart)
            : findBindingScopeEnd(mask, nameStart),
        isPwshHelper: bodyReturnsPwshSpawn(source, mask, bodyStart, bodyClose)
      });
      continue;
    }
    const nameStart = match.index + match[0].indexOf(match[2]);
    helpers.push({
      name: match[2],
      start: nameStart,
      end:
        match[1] === "var"
          ? findVarScopeEnd(mask, nameStart)
          : findBindingScopeEnd(mask, nameStart),
      isPwshHelper: expressionIsPwshSpawn(source, mask, bodyStart)
    });
  }

  return helpers;
}

function skipWhitespace(mask, index, end = mask.length) {
  let next = index;
  while (next < end && /\s/.test(mask[next])) {
    next += 1;
  }
  return next;
}

function readStaticPropertyName(source, mask, index) {
  const identifier = new RegExp(String.raw`^(${IDENT})\b`).exec(mask.slice(index));
  if (identifier) {
    return { name: identifier[1], end: index + identifier[0].length };
  }

  const ch = mask[index];
  if (ch === '"' || ch === "'") {
    const literal = readQuotedString(source, index);
    return {
      name:
        literal && new RegExp(String.raw`^${IDENT}$`).test(literal.value) ? literal.value : null,
      end: literal ? literal.end : index + 1
    };
  }

  if (ch === "[") {
    const close = matchingCloseIndex(mask, index, "[", "]");
    if (close < 0) {
      return { name: null, end: index + 1 };
    }
    const innerStart = skipWhitespace(mask, index + 1, close);
    const innerEnd = previousNonWhitespaceIndex(mask, close);
    let name = null;
    if (innerStart <= innerEnd && (mask[innerStart] === '"' || mask[innerStart] === "'")) {
      const literal = readQuotedString(source, innerStart);
      if (
        literal &&
        literal.end === innerEnd + 1 &&
        new RegExp(String.raw`^${IDENT}$`).test(literal.value)
      ) {
        name = literal.value;
      }
    }
    return { name, end: close + 1 };
  }

  const number = /^\d+\b/.exec(mask.slice(index));
  if (number) {
    return { name: null, end: index + number[0].length };
  }

  return { name: null, end: index + 1 };
}

function collectObjectMethodHelpersFromLiteral(
  source,
  mask,
  objectOpen,
  objectClose,
  path,
  scope,
  helpers
) {
  let index = objectOpen + 1;
  while (index < objectClose) {
    index = skipWhitespace(mask, index, objectClose);
    if (mask[index] === ",") {
      index += 1;
      continue;
    }

    const property = readStaticPropertyName(source, mask, index);
    let cursor = skipWhitespace(mask, property.end, objectClose);

    if (property.name && mask[cursor] === "(") {
      const argsClose = matchingCloseIndex(mask, cursor, "(", ")");
      if (argsClose < 0 || argsClose >= objectClose) {
        index = property.end;
        continue;
      }
      const bodyOpen = skipWhitespace(mask, argsClose + 1, objectClose);
      if (mask[bodyOpen] !== "{") {
        index = argsClose + 1;
        continue;
      }
      const bodyClose = matchingCloseIndex(mask, bodyOpen, "{", "}");
      if (bodyClose < 0 || bodyClose > objectClose) {
        index = bodyOpen + 1;
        continue;
      }
      helpers.push({
        name: `${path}.${property.name}`,
        start: scope.start,
        end: scope.end,
        isPwshHelper: bodyReturnsPwshSpawn(source, mask, bodyOpen, bodyClose)
      });
      index = bodyClose + 1;
      continue;
    }

    if (mask[cursor] !== ":") {
      index = Math.max(property.end, index + 1);
      continue;
    }

    const valueStart = skipWhitespace(mask, cursor + 1, objectClose);
    if (property.name && mask[valueStart] === "{") {
      const valueClose = matchingCloseIndex(mask, valueStart, "{", "}");
      if (valueClose > valueStart && valueClose <= objectClose) {
        collectObjectMethodHelpersFromLiteral(
          source,
          mask,
          valueStart,
          valueClose,
          `${path}.${property.name}`,
          scope,
          helpers
        );
        index = valueClose + 1;
        continue;
      }
    }

    const valueEnd = declaratorInitializerEnd(mask, valueStart).end;
    index = Math.max(valueEnd, valueStart + 1);
  }
}

function collectObjectMethodHelpers(source, mask, bindings) {
  const helpers = [];
  for (const binding of bindings) {
    if (binding.expressionStart < 0 || !stripEnclosingParens(binding.initializer).startsWith("{")) {
      continue;
    }
    const objectOpen = binding.expressionStart;
    const objectClose = matchingCloseIndex(mask, objectOpen, "{", "}");
    if (objectClose > objectOpen) {
      collectObjectMethodHelpersFromLiteral(
        source,
        mask,
        objectOpen,
        objectClose,
        binding.name,
        binding,
        helpers
      );
    }
  }
  return helpers;
}

function pushRawMergeHelperDefinitions(definitions, base, expressionInfos) {
  const rawExpressions = expressionInfos.filter((info) => isRawMergeExpression(info.expr));
  if (rawExpressions.length === 0) {
    definitions.push({
      ...base,
      isRawMerge: false,
      expr: "",
      exprStart: -1,
      exprEnd: -1,
      line: lineNumberAt(base.source, base.bindingStart)
    });
    return;
  }

  for (const info of rawExpressions) {
    definitions.push({
      ...base,
      isRawMerge: true,
      expr: info.expr,
      exprStart: info.start,
      exprEnd: info.end,
      line: lineNumberAt(base.source, info.start)
    });
  }
}

function returnExpressionInfos(mask, bodyOpen, bodyClose) {
  return returnExpressionStarts(mask, bodyOpen + 1, bodyClose).map((start) =>
    readExpressionInfo(mask, start)
  );
}

function collectFunctionRawMergeHelperDefinitions(source, mask) {
  const definitions = [];
  const functionRe = new RegExp(String.raw`\bfunction\s+(${IDENT})\s*\(`, "g");
  let match;
  while ((match = functionRe.exec(mask)) !== null) {
    const argsOpen = mask.indexOf("(", match.index);
    const argsClose = argsOpen >= 0 ? matchingCloseIndex(mask, argsOpen, "(", ")") : -1;
    const bodyOpen = argsClose >= 0 ? skipWhitespace(mask, argsClose + 1) : -1;
    const bodyClose =
      bodyOpen >= 0 && mask[bodyOpen] === "{" ? matchingCloseIndex(mask, bodyOpen, "{", "}") : -1;
    if (argsClose < 0 || bodyClose < 0) {
      continue;
    }
    pushRawMergeHelperDefinitions(
      definitions,
      {
        source,
        name: match[1],
        params: splitParameterNames(mask, argsOpen, argsClose),
        kind: "return",
        activeStart: findBindingScopeStart(mask, match.index),
        scopeStart: findBindingScopeStart(mask, match.index),
        scopeEnd: findBindingScopeEnd(mask, match.index),
        bindingStart: match.index
      },
      returnExpressionInfos(mask, bodyOpen, bodyClose)
    );
  }
  return definitions;
}

function collectArrowRawMergeHelperDefinitions(source, mask) {
  const definitions = [];
  const arrowRe = new RegExp(
    String.raw`\b(const|let|var)\s+(${IDENT})\s*=\s*(?:async\s+)?(\([^)]*\)|${IDENT})\s*=>`,
    "g"
  );
  let match;
  while ((match = arrowRe.exec(mask)) !== null) {
    const nameStart = match.index + match[0].indexOf(match[2]);
    const bodyStart = skipWhitespace(mask, arrowRe.lastIndex);
    const paramsText = match[3].trim();
    const params =
      paramsText.startsWith("(") && paramsText.endsWith(")")
        ? splitTopLevelArgs(paramsText.slice(1, -1)).map((param) => param.trim())
        : [paramsText];
    const scopeEnd =
      match[1] === "var" ? findVarScopeEnd(mask, nameStart) : findBindingScopeEnd(mask, nameStart);
    const base = {
      source,
      name: match[2],
      params,
      kind: "arrow",
      activeStart: nameStart,
      scopeStart: findBindingScopeStart(mask, nameStart),
      scopeEnd,
      bindingStart: nameStart
    };
    if (mask[bodyStart] === "{") {
      const bodyClose = matchingCloseIndex(mask, bodyStart, "{", "}");
      if (bodyClose >= 0) {
        pushRawMergeHelperDefinitions(
          definitions,
          base,
          returnExpressionInfos(mask, bodyStart, bodyClose)
        );
      }
      continue;
    }
    pushRawMergeHelperDefinitions(definitions, base, [readExpressionInfo(mask, bodyStart)]);
  }
  return definitions;
}

function collectObjectMethodRawMergeDefinitionsFromLiteral(
  source,
  mask,
  objectOpen,
  objectClose,
  pathName,
  scope,
  definitions
) {
  let index = objectOpen + 1;
  while (index < objectClose) {
    index = skipWhitespace(mask, index, objectClose);
    if (mask[index] === ",") {
      index += 1;
      continue;
    }

    const property = readStaticPropertyName(source, mask, index);
    let cursor = skipWhitespace(mask, property.end, objectClose);

    if (property.name && mask[cursor] === "(") {
      const argsClose = matchingCloseIndex(mask, cursor, "(", ")");
      const bodyOpen = argsClose >= 0 ? skipWhitespace(mask, argsClose + 1, objectClose) : -1;
      const bodyClose =
        bodyOpen >= 0 && mask[bodyOpen] === "{" ? matchingCloseIndex(mask, bodyOpen, "{", "}") : -1;
      if (argsClose >= 0 && bodyClose >= 0 && bodyClose <= objectClose) {
        pushRawMergeHelperDefinitions(
          definitions,
          {
            source,
            name: `${pathName}.${property.name}`,
            params: splitParameterNames(mask, cursor, argsClose),
            kind: "method return",
            activeStart: scope.start,
            scopeStart: scope.start,
            scopeEnd: scope.end,
            bindingStart: property.end
          },
          returnExpressionInfos(mask, bodyOpen, bodyClose)
        );
        index = bodyClose + 1;
        continue;
      }
    }

    if (mask[cursor] !== ":") {
      index = Math.max(property.end, index + 1);
      continue;
    }

    const valueStart = skipWhitespace(mask, cursor + 1, objectClose);
    if (property.name && mask[valueStart] === "{") {
      const valueClose = matchingCloseIndex(mask, valueStart, "{", "}");
      if (valueClose > valueStart && valueClose <= objectClose) {
        collectObjectMethodRawMergeDefinitionsFromLiteral(
          source,
          mask,
          valueStart,
          valueClose,
          `${pathName}.${property.name}`,
          scope,
          definitions
        );
        index = valueClose + 1;
        continue;
      }
    }

    index = Math.max(declaratorInitializerEnd(mask, valueStart).end, valueStart + 1);
  }
}

function collectObjectMethodRawMergeDefinitions(source, mask, bindings) {
  const definitions = [];
  for (const binding of bindings) {
    if (binding.expressionStart < 0 || !stripEnclosingParens(binding.initializer).startsWith("{")) {
      continue;
    }
    const objectOpen = binding.expressionStart;
    const objectClose = matchingCloseIndex(mask, objectOpen, "{", "}");
    if (objectClose > objectOpen) {
      collectObjectMethodRawMergeDefinitionsFromLiteral(
        source,
        mask,
        objectOpen,
        objectClose,
        binding.name,
        binding,
        definitions
      );
    }
  }
  return definitions;
}

function collectRawMergeHelperDefinitions(source, mask, bindings) {
  return [
    ...collectFunctionRawMergeHelperDefinitions(source, mask),
    ...collectArrowRawMergeHelperDefinitions(source, mask),
    ...collectObjectMethodRawMergeDefinitions(source, mask, bindings)
  ];
}

function helperCallRegex(name) {
  const parts = name.split(".").map((part) => part.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"));
  if (parts.length === 1) {
    return new RegExp(String.raw`\b${parts[0]}\s*\(`, "g");
  }
  return new RegExp(String.raw`\b${parts.join(String.raw`\s*\.\s*`)}\s*\(`, "g");
}

function activeHelperDefinitionAt(definitions, name, index) {
  let active = null;
  for (const definition of definitions) {
    if (
      definition.name !== name ||
      definition.activeStart > index ||
      definition.scopeEnd <= index ||
      definition.scopeStart > index
    ) {
      continue;
    }
    if (
      active === null ||
      definition.activeStart > active.activeStart ||
      (definition.activeStart === active.activeStart &&
        definition.bindingStart > active.bindingStart)
    ) {
      active = definition;
    }
  }
  return active;
}

function helperCalledWithPwshResult(hit, source, mask, context, definitions) {
  const receiverNames = rawMergeReceiverNames(hit.expr);
  const parameterIndexes = [];
  hit.params.forEach((param, index) => {
    if (param && receiverNames.has(param)) {
      parameterIndexes.push(index);
    }
  });
  if (parameterIndexes.length === 0) {
    return false;
  }

  const callRe = helperCallRegex(hit.name);
  let match;
  while ((match = callRe.exec(mask)) !== null) {
    if (match.index < hit.scopeStart || match.index >= hit.scopeEnd) {
      continue;
    }
    const active = activeHelperDefinitionAt(definitions, hit.name, match.index);
    if (active === null || active.bindingStart !== hit.bindingStart) {
      continue;
    }
    const prev = previousNonWhitespaceIndex(mask, match.index);
    if (hit.name.indexOf(".") < 0 && prev >= 0 && mask[prev] === ".") {
      continue;
    }
    if (
      hit.name.indexOf(".") < 0 &&
      /\bfunction\s*$/.test(mask.slice(Math.max(0, match.index - 32), match.index))
    ) {
      continue;
    }
    const openIndex = match.index + match[0].lastIndexOf("(");
    const closeIndex = matchingCloseIndex(mask, openIndex, "(", ")");
    if (closeIndex < 0) {
      continue;
    }
    const args = splitTopLevelArgs(source.slice(openIndex + 1, closeIndex));
    for (const parameterIndex of parameterIndexes) {
      const arg = args[parameterIndex]?.trim() ?? "";
      if (
        new RegExp(String.raw`^${IDENT}$`).test(arg) &&
        isPwshResultAt(context.variableBindings, arg, match.index)
      ) {
        return true;
      }
    }
  }
  return false;
}

function rawMergeHelperTouchesPwshOutput(hit, source, mask, context, definitions) {
  if (expressionHasPwshResultMember(hit.expr, hit.exprStart, context.variableBindings)) {
    return true;
  }
  return helperCalledWithPwshResult(hit, source, mask, context, definitions);
}

function rawMergeHelperHits(source, mask, context) {
  const definitions = collectRawMergeHelperDefinitions(source, mask, context.variableBindings);
  return definitions.filter(
    (definition) =>
      definition.isRawMerge &&
      rawMergeHelperTouchesPwshOutput(definition, source, mask, context, definitions)
  );
}

function findRenamedMergeHelpers(rawSource) {
  const source = rawSource.replace(/\r\n?/g, "\n");
  if (!hasPwshSpawn(source)) {
    return [];
  }
  const mask = maskCommentsAndStrings(source);
  const context = collectPwshOutputContext(source, mask);
  return rawMergeHelperHits(source, mask, context).map((hit) => ({
    line: hit.line,
    kind: hit.kind,
    expr: hit.expr,
    exprStart: hit.exprStart,
    exprEnd: hit.exprEnd,
    name: hit.name,
    params: hit.params,
    scopeStart: hit.scopeStart,
    scopeEnd: hit.scopeEnd,
    bindingStart: hit.bindingStart
  }));
}

function collectParameterBindings(mask) {
  const bindings = [];
  const addParams = (params, start, end) => {
    for (const name of params) {
      bindings.push({
        name,
        start,
        end,
        expressionStart: -1,
        initializer: "",
        isPwshResult: false
      });
    }
  };

  const functionRe = /\bfunction\b/g;
  let match;
  while ((match = functionRe.exec(mask)) !== null) {
    const argsOpen = mask.indexOf("(", match.index);
    const argsClose = argsOpen >= 0 ? matchingCloseIndex(mask, argsOpen, "(", ")") : -1;
    const bodyOpen = argsClose >= 0 ? skipWhitespace(mask, argsClose + 1) : -1;
    const bodyClose =
      bodyOpen >= 0 && mask[bodyOpen] === "{" ? matchingCloseIndex(mask, bodyOpen, "{", "}") : -1;
    if (argsClose >= 0 && bodyClose >= 0) {
      addParams(splitParameterNames(mask, argsOpen, argsClose), bodyOpen + 1, bodyClose);
    }
  }

  const arrowRe = /=>/g;
  while ((match = arrowRe.exec(mask)) !== null) {
    const before = previousNonWhitespaceIndex(mask, match.index);
    let params = [];
    if (before >= 0 && mask[before] === ")") {
      const argsOpen = matchingOpenIndex(mask, before, "(", ")");
      if (argsOpen >= 0) {
        params = splitParameterNames(mask, argsOpen, before);
      }
    } else if (before >= 0) {
      let nameStart = before;
      while (nameStart >= 0 && /[\w$]/.test(mask[nameStart])) {
        nameStart -= 1;
      }
      const name = mask.slice(nameStart + 1, before + 1);
      if (new RegExp(String.raw`^${IDENT}$`).test(name)) {
        params = [name];
      }
    }
    const bodyStart = skipWhitespace(mask, match.index + 2);
    const bodyEnd =
      mask[bodyStart] === "{"
        ? matchingCloseIndex(mask, bodyStart, "{", "}")
        : declaratorInitializerEnd(mask, bodyStart).end;
    if (bodyEnd >= bodyStart) {
      addParams(params, mask[bodyStart] === "{" ? bodyStart + 1 : bodyStart, bodyEnd);
    }
  }

  for (
    let argsOpen = mask.indexOf("(");
    argsOpen >= 0;
    argsOpen = mask.indexOf("(", argsOpen + 1)
  ) {
    if (methodNameBeforeParen(mask, argsOpen) === null) {
      continue;
    }
    const argsClose = matchingCloseIndex(mask, argsOpen, "(", ")");
    const bodyOpen = argsClose >= 0 ? skipWhitespace(mask, argsClose + 1) : -1;
    const bodyClose =
      bodyOpen >= 0 && mask[bodyOpen] === "{" ? matchingCloseIndex(mask, bodyOpen, "{", "}") : -1;
    if (argsClose >= 0 && bodyClose >= 0) {
      addParams(splitParameterNames(mask, argsOpen, argsClose), bodyOpen + 1, bodyClose);
    }
  }

  return bindings;
}

function collectVariableBindings(source, mask = maskCommentsAndStrings(source)) {
  const helpers = collectHelperBindings(source, mask);
  const bindings = collectParameterBindings(mask);
  const declarationRe = /\b(const|let|var)\s+/g;

  let match;
  while ((match = declarationRe.exec(mask)) !== null) {
    const declarationKind = match[1];
    let index = declarationRe.lastIndex;

    while (index < mask.length) {
      while (index < mask.length && /\s/.test(mask[index])) {
        index += 1;
      }

      const nameMatch = new RegExp(String.raw`^(${IDENT})\b`).exec(mask.slice(index));
      if (!nameMatch) {
        break;
      }

      const name = nameMatch[1];
      const declarationIndex = index;
      index += nameMatch[0].length;
      while (index < mask.length && /\s/.test(mask[index])) {
        index += 1;
      }

      let expressionStart = -1;
      let initializer = "";
      let end = { end: index, terminator: "eof" };
      if (mask[index] === "=" && mask[index + 1] !== "=" && mask[index + 1] !== ">") {
        index += 1;
        expressionStart = index;
        while (expressionStart < mask.length && /\s/.test(mask[expressionStart])) {
          expressionStart += 1;
        }
        end = declaratorInitializerEnd(mask, expressionStart);
        initializer = mask.slice(expressionStart, end.end).trim();
      } else {
        end = declaratorInitializerEnd(mask, index);
      }

      bindings.push({
        name,
        start: declarationIndex,
        end:
          declarationKind === "var"
            ? findVarScopeEnd(mask, declarationIndex)
            : findBindingScopeEnd(mask, declarationIndex),
        expressionStart,
        initializer,
        isPwshResult: false
      });

      if (end.terminator !== ",") {
        index = end.end;
        break;
      }
      index = end.end + 1;
    }

    declarationRe.lastIndex = Math.max(declarationRe.lastIndex, match.index + match[0].length);
  }

  helpers.push(...collectObjectMethodHelpers(source, mask, bindings));
  for (const binding of bindings) {
    binding.isPwshResult =
      binding.expressionStart >= 0 &&
      (expressionIsPwshSpawn(source, mask, binding.expressionStart) ||
        expressionCallsPwshHelper(helpers, mask, binding.expressionStart));
  }

  return bindings;
}

function activeBindingAt(bindings, name, index) {
  let active = null;
  for (const binding of bindings) {
    if (binding.name !== name || binding.start > index || binding.end <= index) {
      continue;
    }
    if (active === null || binding.start > active.start) {
      active = binding;
    }
  }
  return active;
}

function bindingSet(bindings, predicate) {
  const names = new Set();
  for (const binding of bindings) {
    if (predicate(binding)) {
      names.add(binding.name);
    }
  }
  return names;
}

function isPwshResultAt(bindings, name, index) {
  const active = activeBindingAt(bindings, name, index);
  return Boolean(active && active.isPwshResult);
}

function isPwshSpawnHelperAt(bindings, name, index) {
  if (name === null) {
    return false;
  }
  const active = activeBindingAt(bindings, name, index);
  return Boolean(active && active.isPwshHelper);
}

function collectPwshResultBindings(source, mask = maskCommentsAndStrings(source)) {
  return collectVariableBindings(source, mask).filter((binding) => binding.isPwshResult);
}

function collectPwshResultVariables(source, mask = maskCommentsAndStrings(source)) {
  return bindingSet(collectPwshResultBindings(source, mask), (binding) => binding.isPwshResult);
}

function collectRawMergeVariables(mask, pwshResultVars) {
  const variables = new Set();
  const rawVariableRe = rawMergePattern(
    String.raw`\b(?:const|let|var)\s+(${IDENT})\s*=\s*` + "`",
    "`" + String.raw`\s*;`
  );

  let match;
  while ((match = rawVariableRe.exec(mask)) !== null) {
    const [, name, stdoutRun, stderrRun] = match;
    if (stdoutRun === stderrRun && pwshResultVars.has(stdoutRun)) {
      variables.add(name);
    }
  }

  return variables;
}

function rawMergeResultName(initializer) {
  const rawMergeRe = rawMergePattern("^`", "`$");
  const match = rawMergeRe.exec(initializer);
  if (!match) {
    return null;
  }
  const [, stdoutRun, stderrRun] = match;
  return stdoutRun === stderrRun ? stdoutRun : null;
}

function collectRawMergeBindings(mask, bindings) {
  return bindings.filter((binding) => {
    if (binding.expressionStart < 0) {
      return false;
    }
    if (
      isBareRawOutputMember(binding.initializer) &&
      expressionHasPwshResultMember(binding.initializer, binding.expressionStart, bindings)
    ) {
      return true;
    }
    return (
      isRawMergeExpression(binding.initializer) &&
      expressionHasPwshResultMember(binding.initializer, binding.expressionStart, bindings)
    );
  });
}

function collectPhraseVariables(source, mask = maskCommentsAndStrings(source)) {
  const phraseVars = new Set();
  for (const binding of collectVariableBindings(source, mask)) {
    if (binding.expressionStart < 0) {
      continue;
    }
    const initializer = binding.initializer;
    const quote = initializer[0];
    if (quote !== '"' && quote !== "'" && quote !== "`") {
      continue;
    }
    if (initializer.length < 2 || initializer[initializer.length - 1] !== quote) {
      continue;
    }
    if (quote === "`" && initializer.includes("${")) {
      continue;
    }
    const close = binding.expressionStart + initializer.length - 1;
    const payload = source.slice(binding.expressionStart + 1, close);
    if (hasWrappableWordBoundary(payload)) {
      phraseVars.add(binding.name);
    }
  }
  return phraseVars;
}

function isRawMergeVariableAt(bindings, rawMergeBindings, name, index) {
  const active = activeBindingAt(bindings, name, index);
  return Boolean(
    active &&
    rawMergeBindings.some((binding) => binding.name === name && binding.start === active.start)
  );
}

function collectPwshOutputContext(source, mask = maskCommentsAndStrings(source)) {
  const variableBindings = collectVariableBindings(source, mask);
  return {
    variableBindings,
    pwshResultBindings: variableBindings.filter((binding) => binding.isPwshResult),
    rawMergeBindings: collectRawMergeBindings(mask, variableBindings)
  };
}

function fixRawMergeHelperDefinitions(source, context, helpers) {
  const mask = maskCommentsAndStrings(source);
  const hits = rawMergeHelperHits(source, mask, context);
  if (hits.length === 0) {
    return source;
  }
  helpers.add("normalizePwshText");
  return applySpanReplacements(
    source,
    hits.map((hit) => ({
      start: hit.exprStart,
      end: hit.exprEnd,
      text: `normalizePwshText(${source.slice(hit.exprStart, hit.exprEnd)})`
    }))
  );
}

function directRawReceiverReplacement(receiver, receiverStart, context, helpers) {
  const trimmed = receiver.trim();
  const leadingWhitespace = receiver.length - receiver.trimStart().length;
  const expressionStart = receiverStart + leadingWhitespace;

  const callee = outermostCalleeName(trimmed);
  if (callee !== null && NORMALIZING_HELPERS.has(callee)) {
    return null;
  }

  const stdout = new RegExp(String.raw`^(${IDENT})\.stdout$`).exec(trimmed);
  if (stdout && isPwshResultAt(context.variableBindings, stdout[1], expressionStart)) {
    helpers.add("stdoutText");
    return `stdoutText(${stdout[1]})`;
  }

  const stderr = new RegExp(String.raw`^(${IDENT})\.stderr$`).exec(trimmed);
  if (stderr && isPwshResultAt(context.variableBindings, stderr[1], expressionStart)) {
    helpers.add("normalizePwshText");
    return `normalizePwshText(${stderr[1]}.stderr || "")`;
  }

  if (
    new RegExp(String.raw`^${IDENT}$`).test(trimmed) &&
    isRawMergeVariableAt(
      context.variableBindings,
      context.rawMergeBindings,
      trimmed,
      expressionStart
    )
  ) {
    helpers.add("normalizePwshText");
    return `normalizePwshText(${trimmed})`;
  }

  if (expressionHasPwshResultMember(trimmed, expressionStart, context.variableBindings)) {
    helpers.add("normalizePwshText");
    return `normalizePwshText(${trimmed})`;
  }

  return null;
}

function fixDirectRawExpectReceivers(source, context, phraseVars, helpers) {
  const mask = maskCommentsAndStrings(source);
  const replacements = [];
  const expectRe = /\bexpect\s*\(/g;
  let match;
  while ((match = expectRe.exec(mask)) !== null) {
    const openIndex = match.index + match[0].lastIndexOf("(");
    const closeIndex = matchingCloseIndex(mask, openIndex, "(", ")");
    if (closeIndex < 0) {
      continue;
    }
    if (!hasPhraseMatcherAfterExpect(source, mask, closeIndex + 1, phraseVars)) {
      continue;
    }
    const receiver = source.slice(openIndex + 1, closeIndex);
    const replacement = directRawReceiverReplacement(receiver, openIndex + 1, context, helpers);
    if (replacement !== null) {
      replacements.push({
        start: openIndex + 1,
        end: closeIndex,
        text: replacement
      });
    }
  }
  return applySpanReplacements(source, replacements);
}

function fixPwshStatusAssertions(source, context, helpers) {
  const mask = maskCommentsAndStrings(source);
  const replacements = [];
  const statusExpectRe = new RegExp(
    String.raw`\bexpect\s*\(\s*(${IDENT})\s*\.\s*status\s*\)\s*(\.not)?\s*\.\s*toBe\s*\(`,
    "g"
  );

  let match;
  while ((match = statusExpectRe.exec(mask)) !== null) {
    const [, run, notClause] = match;
    if (!isPwshResultAt(context.variableBindings, run, match.index)) {
      continue;
    }

    const argsOpen = match.index + match[0].lastIndexOf("(");
    const argsClose = matchingCloseIndex(mask, argsOpen, "(", ")");
    if (argsClose < 0) {
      continue;
    }

    const expectedStatus = source.slice(argsOpen + 1, argsClose).trim();
    if (notClause) {
      if (expectedStatus !== "0") {
        continue;
      }
      helpers.add("assertSpawnNonZeroStatus");
      replacements.push({
        start: match.index,
        end: argsClose + 1,
        text: `assertSpawnNonZeroStatus(${run}, ${SPAWN_STATUS_LABEL})`
      });
      continue;
    }

    helpers.add("assertSpawnStatus");
    replacements.push({
      start: match.index,
      end: argsClose + 1,
      text: `assertSpawnStatus(${run}, ${expectedStatus}, ${SPAWN_STATUS_LABEL})`
    });
  }

  return applySpanReplacements(source, replacements);
}

function fixPwshStatusFailureBranches(source, context, helpers) {
  const mask = maskCommentsAndStrings(source);
  const replacements = [];
  const statusIfRe = new RegExp(
    String.raw`\bif\s*\(\s*(${IDENT})\s*\.\s*status\s*!==?\s*0\s*\)\s*\{`,
    "g"
  );

  let match;
  while ((match = statusIfRe.exec(mask)) !== null) {
    const [, run] = match;
    if (!isPwshResultAt(context.variableBindings, run, match.index)) {
      continue;
    }

    const blockOpen = match.index + match[0].lastIndexOf("{");
    const blockClose = matchingCloseIndex(mask, blockOpen, "{", "}");
    if (blockClose < 0) {
      continue;
    }

    const body = mask.slice(blockOpen + 1, blockClose).trim();
    if (!/^throw\s+new\s+Error\s*\([\s\S]*\)\s*;?$/.test(body)) {
      continue;
    }

    helpers.add("assertSpawnStatus");
    replacements.push({
      start: match.index,
      end: blockClose + 1,
      text: `assertSpawnStatus(${run}, 0, ${SPAWN_STATUS_LABEL});`
    });
  }

  return applySpanReplacements(source, replacements);
}

function removeConsecutiveDuplicateSpawnStatusAssertions(source) {
  const lines = source.split("\n");
  const out = [];
  let previousTrimmed = "";

  for (const line of lines) {
    const trimmed = line.trim();
    const isSpawnStatusAssertion = /^assertSpawn(?:NonZero)?Status\(.+\);$/.test(trimmed);
    if (isSpawnStatusAssertion && trimmed === previousTrimmed) {
      continue;
    }
    out.push(line);
    previousTrimmed = trimmed;
  }

  return out.join("\n");
}

function fixSource(source, filePath) {
  if (!hasPwshSpawn(source)) {
    return { source, changed: false };
  }

  let next = source;
  const helpers = new Set();
  const buildContext = (currentSource) => {
    const mask = maskCommentsAndStrings(currentSource);
    const outputContext = collectPwshOutputContext(currentSource, mask);
    return {
      mask,
      outputContext,
      variableBindings: outputContext.variableBindings,
      rawMergeBindings: outputContext.rawMergeBindings,
      phraseVars: collectPhraseVariables(currentSource, mask),
      pwshResultVars: bindingSet(
        outputContext.pwshResultBindings,
        (binding) => binding.isPwshResult
      )
    };
  };

  let context = buildContext(next);
  if (context.pwshResultVars.size === 0) {
    return { source, changed: false };
  }

  next = fixRawMergeHelperDefinitions(next, context.outputContext, helpers);
  context = buildContext(next);

  const rawVariableExpectRe = new RegExp(
    String.raw`expect\(\s*(${IDENT})\s*\)(\s*(?:\.not)?\.(toContain|toMatch)\s*\()`,
    "g"
  );
  next = applyMaskedReplacements(next, rawVariableExpectRe, (match, currentSource) => {
    const [, variableName, matcherPrefix, matcher] = match;
    if (
      !isRawMergeVariableAt(
        context.variableBindings,
        context.rawMergeBindings,
        variableName,
        match.index
      ) ||
      (matcher !== "toMatch" &&
        !argumentIsPhraseAt(currentSource, match.index + match[0].length, context.phraseVars))
    ) {
      return null;
    }
    helpers.add("normalizePwshText");
    return `expect(normalizePwshText(${variableName}))${matcherPrefix}`;
  });

  context = buildContext(next);
  const rawExpectRe = rawMergePattern(String.raw`expect\(\s*` + "`", "`" + String.raw`\s*\)`);
  next = applyMaskedReplacements(next, rawExpectRe, (match, currentSource, mask) => {
    const [, stdoutRun, stderrRun] = match;
    if (
      stdoutRun !== stderrRun ||
      !isPwshResultAt(context.variableBindings, stdoutRun, match.index) ||
      !hasPhraseMatcherAfterExpect(
        currentSource,
        mask,
        match.index + match[0].length,
        context.phraseVars
      )
    ) {
      return null;
    }
    helpers.add("combinedText");
    return `expect(combinedText(${stdoutRun}))`;
  });

  context = buildContext(next);
  const stdoutExpectRe = new RegExp(
    String.raw`expect\(\s*(${IDENT})\.stdout\s*\)(\s*(?:\.not)?\.(toContain|toMatch)\s*\()`,
    "g"
  );
  next = applyMaskedReplacements(next, stdoutExpectRe, (match, currentSource) => {
    const [, run, matcherPrefix, matcher] = match;
    if (
      !isPwshResultAt(context.variableBindings, run, match.index) ||
      (matcher !== "toMatch" &&
        !argumentIsPhraseAt(currentSource, match.index + match[0].length, context.phraseVars))
    ) {
      return null;
    }
    helpers.add("stdoutText");
    return `expect(stdoutText(${run}))${matcherPrefix}`;
  });

  context = buildContext(next);
  const stderrExpectRe = new RegExp(
    String.raw`expect\(\s*(${IDENT})\.stderr\s*\)(\s*(?:\.not)?\.(toContain|toMatch)\s*\()`,
    "g"
  );
  next = applyMaskedReplacements(next, stderrExpectRe, (match, currentSource) => {
    const [, run, matcherPrefix, matcher] = match;
    if (
      !isPwshResultAt(context.variableBindings, run, match.index) ||
      (matcher !== "toMatch" &&
        !argumentIsPhraseAt(currentSource, match.index + match[0].length, context.phraseVars))
    ) {
      return null;
    }
    helpers.add("normalizePwshText");
    return `expect(normalizePwshText(${run}.stderr || ""))${matcherPrefix}`;
  });

  context = buildContext(next);
  next = fixDirectRawExpectReceivers(next, context.outputContext, context.phraseVars, helpers);

  context = buildContext(next);
  next = fixPwshStatusAssertions(next, context, helpers);

  context = buildContext(next);
  next = fixPwshStatusFailureBranches(next, context, helpers);

  next = removeConsecutiveDuplicateSpawnStatusAssertions(next);

  next = ensurePwshOutputImport(next, filePath, helpers);
  return { source: next, changed: next !== source };
}

function parseArgs(argv) {
  const files = [];
  let check = false;
  let passthrough = false;

  for (const arg of argv) {
    if (!passthrough && arg === "--") {
      passthrough = true;
      continue;
    }
    if (!passthrough && arg === "--check") {
      check = true;
      continue;
    }
    if (!passthrough && (arg === "-h" || arg === "--help")) {
      return { help: true, check, files };
    }
    files.push(path.resolve(REPO_ROOT, arg));
  }

  return { help: false, check, files };
}

function runCli(argv = process.argv.slice(2), io = process) {
  const parsed = parseArgs(argv);
  if (parsed.help) {
    io.stdout.write(`${usage()}\n`);
    return 0;
  }

  const files = parsed.files.length > 0 ? parsed.files : listDefaultFiles();
  const changed = [];

  for (const file of files) {
    if (!fs.existsSync(file) || !fs.statSync(file).isFile() || !file.endsWith(".js")) {
      continue;
    }
    const source = fs.readFileSync(file, "utf8");
    const result = fixSource(source, file);
    if (!result.changed) {
      continue;
    }
    changed.push(file);
    if (!parsed.check) {
      fs.writeFileSync(file, result.source, "utf8");
    }
  }

  if (changed.length > 0) {
    const relative = changed.map(toRepoRelative).join("\n  ");
    if (parsed.check) {
      io.stderr.write(
        `PowerShell output assertion normalization is needed in:\n  ${relative}\n` +
          "Run `npm run fix:pwsh-output-assertions -- <files...>`.\n"
      );
      return 1;
    }
    io.stdout.write(`Normalized PowerShell output assertions in:\n  ${relative}\n`);
  }

  return 0;
}

if (require.main === module) {
  process.exitCode = runCli();
}

module.exports = {
  argumentIsPhraseAt,
  collectPhraseVariables,
  collectPwshOutputContext,
  collectPwshResultBindings,
  collectRawMergeVariables,
  collectPwshResultVariables,
  findRenamedMergeHelpers,
  fixSource,
  hasPwshSpawn,
  isPwshResultAt,
  isRawMergeVariableAt,
  resolveImportPath,
  runCli
};
