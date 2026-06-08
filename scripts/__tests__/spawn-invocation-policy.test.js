/**
 * @fileoverview Repository-wide policy guard that makes the ENTIRE category of
 * "host-platform-dependent spawn assertion" / "direct Windows-batch-shim spawn"
 * bug impossible to reintroduce (in its direct, literal, and simple-variable
 * forms; see "Residual limits").
 *
 * Background:
 *   npm/npx are exposed as `.cmd` batch shims on Windows. To avoid Node
 *   CVE-2024-27980, production code wraps them as
 *   `<ComSpec> /d /s /c npm.cmd ...args` via `spawnPlatformCommandSync()` in
 *   scripts/lib/shell-command.js. Two failure modes have bitten this repo:
 *
 *     (a) Tests asserted the spawn COMMAND with `toShellCommand("npm")` (which
 *         returns `"npm"` on Linux and `"npm.cmd"` on Windows). On Linux the
 *         assertion matched the passthrough and PASSED; on Windows it asserted
 *         the bare `"npm.cmd"` while production actually called `cmd.exe`, so the
 *         pre-push hook FAILED only on Windows. The divergence was invisible in
 *         the Linux devcontainer/CI.
 *
 *     (b) Production code spawned `"npm"` / `"npx"` (or any `.cmd` / `.bat`
 *         Windows batch shim) directly via child_process instead of routing
 *         through spawnPlatformCommandSync, bypassing the Windows shell-shim
 *         execution rules entirely.
 *
 * This guard scans the repository's own source so the regression is loud at
 * pre-push time on EVERY host, not just Windows.
 *
 *   Policy A — No test asserts `toShellCommand(...)` (under its own name OR a
 *              destructured/aliased import, including the member-access form
 *              `x.toShellCommand(`) as the expected COMMAND argument of a
 *              `toHaveBeenCalledWith(` / `toHaveBeenLastCalledWith(` /
 *              `toHaveBeenNthCalledWith(` call, and no test uses a literal
 *              `"npm.cmd"` / `"npx.cmd"` / `` `npm.cmd` `` (single, double, or
 *              backtick quoted) as an expected spawn command in any of those
 *              three matchers. Tests must derive expectations from
 *              `buildSpawnInvocation(...)` so they track production on every OS.
 *
 *   Policy B — No production script (`scripts/**\/*.js`, excluding `__tests__`)
 *              spawns npm/npx -- OR ANY Windows batch shim (a command literal
 *              ending in `.cmd` / `.bat`, case-insensitive, regardless of base
 *              name: `yarn.cmd`, `pnpm.cmd`, `setup.bat`, ...) -- directly via
 *              child_process. Every Windows batch-file shim spawned directly hits
 *              Node CVE-2024-27980 / EINVAL, so the whole category is blocked,
 *              not just npm/npx. This covers BOTH child_process entry-point
 *              families plus an indirect form:
 *                - ARG-VECTOR entry points (`spawnSync` / `spawn` /
 *                  `execFileSync` / `execFile`): the command is arg 0. Flagged
 *                  when it is the npm/npx literal, e.g. `spawnSync("npm",
 *                  ["pack"])`, or when its literal payload ends in `.cmd`/`.bat`,
 *                  e.g. `spawnSync("yarn.cmd", ["install"])`. A `.cmd`/`.bat` in
 *                  the args VECTOR (arg 1+) is NOT the command and is not flagged.
 *                - SHELL-STRING entry points (`exec` / `execSync`): the first
 *                  argument is a whole command LINE, so the program token may
 *                  continue after the name: `execSync("npm install --no-save")`
 *                  and `execSync("setup.bat --init")`. Only the LEADING program
 *                  token counts, so `execSync("echo build.bat")` is NOT flagged.
 *                - SIMPLE VARIABLE indirection: a top-level string-literal
 *                  binding (`const c = "npm"`) passed as the command argument of
 *                  any spawn-family call in the SAME file, e.g.
 *                  `const c = "npm"; childProcess.spawnSync(c, ...)`.
 *              The cmd.exe wrapper itself (`process.env.ComSpec` / `"cmd.exe"`)
 *              is the prescribed fix, not a violation, and is excluded by
 *              construction (`.exe` is neither `.cmd` nor `.bat`, and ComSpec is
 *              not a string literal). These MUST go through
 *              spawnPlatformCommandSync.
 *
 * Residual limits (documented honestly rather than chased into false positives):
 *   - Policy A's alias detection tracks the destructured/aliased local name of
 *     `toShellCommand` within each test file. A re-exported alias defined in a
 *     different module, or a fully computed callee name, is out of scope.
 *   - Policy B flags npm/npx and any literal `.cmd`/`.bat` command in the command
 *     slot, in its direct, literal, and simple-variable forms. The variable
 *     tracking is deliberately WITHIN-FILE and conservative (simple top-level
 *     `const|let|var <id> = "npm"|...` bindings only, npm/npx-valued), and the
 *     `.cmd`/`.bat` generalization only inspects STRING-LITERAL commands. Cross-
 *     module aliased re-exports of the spawn functions, runtime-computed command
 *     names (e.g. from arrays/concatenation), reassigned bindings, and a `.cmd`/
 *     `.bat` name held in a variable are not modeled -- catching those would
 *     require a real parser and would risk flagging legitimate code, which devs
 *     would then disable. Direct, literal, and simple-variable forms ARE made
 *     impossible to reintroduce.
 *
 * How to mutation-test this guard (do this when you touch it):
 *   1. In any consumer test, change a derived assertion back to
 *      `expect(spawnSyncSpy).toHaveBeenCalledWith(toShellCommand("npm"), ...)`.
 *      Run this suite -> Policy A must FAIL. Revert.
 *   2. In any production script (e.g. scripts/validate-npm-meta.js), add
 *      `require("child_process").spawnSync("npm", ["--version"]);` (or any
 *      `.cmd`/`.bat` shim, e.g. `spawnSync("yarn.cmd", ["install"]);`).
 *      Run this suite -> Policy B must FAIL. Revert.
 *   3. Add `expect(x).toHaveBeenCalledWith("npm.cmd", ...)` (or
 *      `toHaveBeenLastCalledWith(...)`) to any test.
 *      Run this suite -> Policy A must FAIL. Revert.
 *   If any mutation does NOT fail, the guard is inadequate and must be hardened.
 *   Self-tests below feed crafted source strings through the same detectors to
 *   prove they fire (and do not false-positive) without touching real files.
 *
 * @cross-platform-regression -- this marker requires the file to be gated on
 * ubuntu/windows/macos via the targeted step in
 * .github/workflows/cross-platform-preflight.yml; enforced by
 * scripts/__tests__/cross-platform-preflight-coverage.test.js.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const { stripJsCommentsAndStrings } = require("../lib/source-stripping");
const { readUtf8, lineNumberAt, toRepoRelative } = require("../lib/repo-files");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SCRIPTS_ROOT = path.join(REPO_ROOT, "scripts");
const TESTS_ROOT = path.join(SCRIPTS_ROOT, "__tests__");

const WALK_SKIP_DIRS = new Set(["node_modules", ".git", ".venv", "__pycache__", "Temp"]);

// child_process spawn-family entry points whose first positional argument is a
// bare executable name (an argument VECTOR follows separately). For these the
// command literal stands alone, so the closing-quote anchor is exact.
const ARG_VECTOR_SPAWN_FAMILY = ["spawnSync", "spawn", "execFileSync", "execFile"];

// child_process entry points whose first positional argument is a whole shell
// command LINE (program name + arguments in one string). For these the command
// token may CONTINUE after the program name, so `exec("npm install ...")` must
// match too -- not just `exec("npm")`.
const SHELL_STRING_SPAWN_FAMILY = ["exec", "execSync"];

// Union used for the within-file simple-variable-command tracking (Finding 3):
// any of these passed a string-literal-bound identifier as its command argument
// is the anti-pattern regardless of which family the entry point belongs to.
const SPAWN_FAMILY = [...ARG_VECTOR_SPAWN_FAMILY, ...SHELL_STRING_SPAWN_FAMILY];

// Forbidden literal command names for direct spawning.
const FORBIDDEN_SPAWN_COMMANDS = ["npm", "npx", "npm.cmd", "npx.cmd"];

// Production files that are PERMITTED to reference the npm/npx shims directly:
// shell-command.js owns the wrapping itself. (run-managed-jest.js routes through
// spawnPlatformCommandSync, so it never spawns "npm" as a child_process literal
// and does not need an allow-list entry.)
const PRODUCTION_ALLOW_LIST = new Set([path.join("scripts", "lib", "shell-command.js")]);

// Test files PERMITTED to contain the anti-pattern as fixture/documentation
// text: this guard itself embeds crafted source strings (the detector
// self-tests) and prose describing exactly what is forbidden. Those literals
// are data, not real spawn assertions.
const TEST_ALLOW_LIST = new Set([
  path.join("scripts", "__tests__", "spawn-invocation-policy.test.js")
]);

function listFilesRecursive(absoluteDir, predicate) {
  const out = [];
  if (!fs.existsSync(absoluteDir)) {
    return out;
  }

  const stack = [absoluteDir];
  while (stack.length > 0) {
    const dir = stack.pop();
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch (error) {
      continue;
    }

    for (const entry of entries) {
      if (entry.isDirectory()) {
        if (WALK_SKIP_DIRS.has(entry.name)) {
          continue;
        }
        stack.push(path.join(dir, entry.name));
        continue;
      }
      if (!entry.isFile()) {
        continue;
      }
      const abs = path.join(dir, entry.name);
      if (predicate(abs)) {
        out.push(abs);
      }
    }
  }

  return out;
}

function listTestFiles() {
  return listFilesRecursive(TESTS_ROOT, (abs) => abs.endsWith(".test.js"));
}

function listProductionScriptFiles() {
  return listFilesRecursive(SCRIPTS_ROOT, (abs) => {
    if (!abs.endsWith(".js")) {
      return false;
    }
    // Exclude any __tests__ directory anywhere in the tree.
    const relParts = path.relative(SCRIPTS_ROOT, abs).split(path.sep);
    return !relParts.includes("__tests__");
  });
}

/**
 * Extract the raw argument-list source of every `name(` call in `source`,
 * by balancing parentheses. Comments/strings in `source` are assumed already
 * stripped via stripJsCommentsAndStrings so that parentheses inside string
 * literals do not confuse the balancer. Returns an array of
 * `{ argsText, startIndex }` where `startIndex` is the index of the call name.
 */
function extractCallArgumentLists(source, callName) {
  const results = [];
  const namePattern = new RegExp(
    `\\b${callName.replace(/[.*+?^${}()|[\\]\\\\]/g, "\\$&")}\\s*\\(`,
    "g"
  );

  let match = namePattern.exec(source);
  while (match !== null) {
    const openParenIndex = match.index + match[0].length - 1;
    let depth = 0;
    let i = openParenIndex;
    for (; i < source.length; i++) {
      const ch = source[i];
      if (ch === "(") {
        depth++;
      } else if (ch === ")") {
        depth--;
        if (depth === 0) {
          break;
        }
      }
    }

    if (depth === 0) {
      results.push({
        argsText: source.slice(openParenIndex + 1, i),
        startIndex: match.index
      });
    }

    match = namePattern.exec(source);
  }

  return results;
}

/**
 * Split an argument-list source into its TOP-LEVEL comma-separated arguments,
 * ignoring commas nested inside parentheses, brackets, braces, strings, or
 * template literals. Returns the trimmed text of each argument.
 *
 * @param {string} argsText - The text between a call's outermost parens.
 * @returns {string[]} Top-level argument source fragments.
 */
function splitTopLevelArguments(argsText) {
  const args = [];
  let depth = 0;
  let current = "";
  let quote = null; // active string/template delimiter, or null

  for (let i = 0; i < argsText.length; i++) {
    const ch = argsText[i];
    const prev = i > 0 ? argsText[i - 1] : "";

    if (quote) {
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
      depth++;
      current += ch;
      continue;
    }
    if (ch === ")" || ch === "]" || ch === "}") {
      depth--;
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

// Jest call-args matchers that assert the spawn COMMAND in their argument list.
// `toHaveBeenCalledWith` and `toHaveBeenLastCalledWith` both put the command at
// index 0; `toHaveBeenNthCalledWith(n, command, ...)` shifts it to index 1.
// `commandArgIndexFor` encodes that offset, so any matcher with index-0 command
// semantics can be added here freely without further bookkeeping.
const CALLED_WITH_MATCHERS = [
  "toHaveBeenCalledWith",
  "toHaveBeenLastCalledWith",
  "toHaveBeenNthCalledWith"
];

/**
 * Return the index of the COMMAND-position argument for a given matcher.
 * `toHaveBeenNthCalledWith(n, command, args, options)` puts the command at
 * index 1; `toHaveBeenCalledWith(command, args, options)` and
 * `toHaveBeenLastCalledWith(command, args, options)` at index 0.
 *
 * @param {string} callName - Matcher name.
 * @returns {number} Index of the command argument.
 */
function commandArgIndexFor(callName) {
  return callName === "toHaveBeenNthCalledWith" ? 1 : 0;
}

// ---------------------------------------------------------------------------
// Policy A detectors (test-assertion anti-patterns).
// ---------------------------------------------------------------------------

/**
 * Parse the local aliases of `toShellCommand` from RAW source (the require/
 * import module path is a string literal, erased by stripping, so this must run
 * on raw text). Matches both CommonJS destructure and ESM named import, with or
 * without an alias, only when the source module path mentions `shell-command`.
 *
 * @param {string} rawSource - Original source (LF-normalized).
 * @returns {Set<string>} Local callee names that resolve to toShellCommand.
 */
function collectToShellCommandAliasesFromRaw(rawSource) {
  const names = new Set(["toShellCommand"]);

  // CommonJS: const { ..., toShellCommand[: alias], ... } = require("...shell-command...")
  const cjsRe =
    /\b(?:const|let|var)\s*\{([^}]*)\}\s*=\s*require\(\s*["'`]([^"'`]*shell-command[^"'`]*)["'`]\s*\)/g;
  // ESM: import { ..., toShellCommand[ as alias], ... } from "...shell-command..."
  const esmRe = /\bimport\s*\{([^}]*)\}\s*from\s*["'`]([^"'`]*shell-command[^"'`]*)["'`]/g;

  const harvest = (bindingsText, separator) => {
    for (const rawBinding of bindingsText.split(",")) {
      const binding = rawBinding.trim();
      if (!binding) {
        continue;
      }
      const parts = binding.split(separator).map((p) => p.trim());
      const imported = parts[0];
      const local = parts.length > 1 && parts[1] ? parts[1] : imported;
      if (imported === "toShellCommand") {
        names.add(local);
      }
    }
  };

  let match = cjsRe.exec(rawSource);
  while (match !== null) {
    harvest(match[1], ":");
    match = cjsRe.exec(rawSource);
  }

  match = esmRe.exec(rawSource);
  while (match !== null) {
    harvest(match[1], /\s+as\s+/);
    match = esmRe.exec(rawSource);
  }

  return names;
}

/**
 * Detect `toShellCommand(...)` (under its own name OR any local alias, and via
 * the member-access form `x.toShellCommand(`) used as the COMMAND-position
 * argument inside a `toHaveBeenCalledWith(` / `toHaveBeenNthCalledWith(` call.
 * Works on the STRIPPED source so identifiers/structure survive while string
 * payloads do not. Position-awareness (only the command argument is inspected)
 * keeps the legitimate `toShellCommand` UNIT assertions and any incidental use
 * elsewhere from producing a false positive.
 *
 * @param {string} strippedSource - Source with comments/string payloads erased.
 * @param {Set<string>} aliases - Local callee names that resolve to toShellCommand.
 * @returns {Array<{line: number}>}
 */
function findToShellCommandInCalledWith(strippedSource, aliases = new Set(["toShellCommand"])) {
  const violations = [];
  // Each alias may appear bare (`tsc(`) or as a member (`x.toShellCommand(`).
  // The member form always uses the canonical property name `toShellCommand`,
  // which is always in the alias set, so a single alternation over all names
  // covers both. The `(?:^|[^\w$])` prefix permits a leading `.` (member access)
  // while preventing the alias from matching as the suffix of a longer
  // identifier (e.g. `myToShellCommand`).
  const calleePattern = new RegExp(
    `(?:^|[^\\w$])(?:${[...aliases].map(escapeRegex).join("|")})\\s*\\(`
  );

  for (const callName of CALLED_WITH_MATCHERS) {
    const cmdIndex = commandArgIndexFor(callName);
    for (const call of extractCallArgumentLists(strippedSource, callName)) {
      const args = splitTopLevelArguments(call.argsText);
      const commandArg = args[cmdIndex];
      if (commandArg && calleePattern.test(commandArg)) {
        violations.push({ line: lineNumberAt(strippedSource, call.startIndex) });
      }
    }
  }

  return violations;
}

/**
 * Detect a literal `"npm.cmd"` / `"npx.cmd"` (single or double quoted) used as
 * the COMMAND-position argument inside a `toHaveBeen*CalledWith(` call. Needs
 * the string payloads intact, so it runs on RAW source. Position-awareness is
 * essential here: the canonical win32 ARGS array legitimately contains
 * `["/d","/s","/c","npm.cmd", ...]`, which must NOT be flagged -- only a
 * `.cmd` literal in the command slot is the anti-pattern.
 *
 * @param {string} rawSource - Original source (LF-normalized).
 * @returns {Array<{line: number, snippet: string}>}
 */
function findShimLiteralCommandInCalledWith(rawSource) {
  const violations = [];
  const commandLiteralPattern = /^[`"'](?:npm|npx)\.cmd[`"']$/;

  for (const callName of CALLED_WITH_MATCHERS) {
    const cmdIndex = commandArgIndexFor(callName);
    for (const call of extractCallArgumentLists(rawSource, callName)) {
      const args = splitTopLevelArguments(call.argsText);
      const commandArg = args[cmdIndex];
      if (commandArg && commandLiteralPattern.test(commandArg)) {
        violations.push({
          line: lineNumberAt(rawSource, call.startIndex),
          snippet: commandArg
        });
      }
    }
  }

  return violations;
}

// ---------------------------------------------------------------------------
// Policy B detector (direct npm/npx spawn in production).
// ---------------------------------------------------------------------------

function escapeRegex(text) {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * Record a violation if (and only if) the raw-source hit at `matchIndex`
 * corresponds to real code -- i.e. the spawn-call name also exists on the same
 * line of the STRIPPED source. A raw hit with no stripped counterpart lived
 * inside a comment or string literal (e.g. documentation) and is skipped.
 *
 * @param {object} ctx - { rawSource, strippedSource, callName, violations }
 * @param {number} matchIndex - Index of the match start in rawSource.
 * @param {string} snippet - The matched text (for the failure message).
 */
function pushIfRealCode(ctx, matchIndex, snippet) {
  const line = lineNumberAt(ctx.rawSource, matchIndex);
  const strippedLine = ctx.strippedSource.split("\n")[line - 1] || "";
  if (new RegExp(`\\b${escapeRegex(ctx.callName)}\\s*\\(`).test(strippedLine)) {
    ctx.violations.push({ line, snippet });
  }
}

/**
 * Detect direct spawning of npm/npx (or any Windows batch shim) in production
 * source. Runs on RAW source so the literal command string is visible; a
 * stripped-source cross-check (see pushIfRealCode) discards hits that live inside
 * comments/strings.
 *
 * Four shapes are detected:
 *   (a) ARG-VECTOR families (`spawnSync` / `spawn` / `execFileSync` /
 *       `execFile`): command is arg 0 as a bare quoted literal, so a closing
 *       quote immediately follows the command -- `spawnSync("npm", [...])`.
 *   (b) SHELL-STRING families (`exec` / `execSync`): the first argument is a
 *       command LINE, so the program name may continue --
 *       `execSync("npm install --no-save")` as well as `execSync("npm")`. We
 *       therefore require npm/npx to be followed by whitespace OR the closing
 *       quote rather than only the closing quote.
 *   (c) SIMPLE VARIABLE indirection: a top-level string-literal binding
 *       (`const c = "npm"`) passed as the command argument of any spawn-family
 *       call in the SAME file (`childProcess.spawnSync(c, ...)`). Conservative,
 *       within-file, mirrors scripts/validate-node-tooling.js.
 *   (d) GENERALIZED batch shim: any string-literal command (arg-vector arg 0, or
 *       the leading program token of a shell-string) ending in `.cmd`/`.bat`
 *       (case-insensitive), regardless of base name (`yarn.cmd`, `setup.bat`,
 *       ...). See findBatchShimSpawn. This subsumes npm.cmd/npx.cmd, so the
 *       result is de-duplicated by line.
 *
 * Shapes (a)-(c) support single, double, and backtick quoting and match `.cmd`
 * shims (`npm.cmd` / `npx.cmd`); shape (d) matches `.cmd`/`.bat` of ANY base name.
 *
 * @param {string} rawSource - Original source (LF-normalized).
 * @param {string} strippedSource - Source with comments/strings erased.
 * @returns {Array<{line: number, snippet: string}>}
 */
function findDirectNpmSpawn(rawSource, strippedSource) {
  const violations = [];
  const escapedCommands = FORBIDDEN_SPAWN_COMMANDS.map((c) => c.replace(/\./g, "\\.")).join("|");
  // npm/npx with the OPTIONAL `.cmd` suffix, for the shell-string form where the
  // program name may be followed by further arguments.
  const escapedProgramNames = "(?:npm|npx)(?:\\.cmd)?";

  const runMatcher = (callName, pattern) => {
    const ctx = { rawSource, strippedSource, callName, violations };
    let match = pattern.exec(rawSource);
    while (match !== null) {
      pushIfRealCode(ctx, match.index, match[0]);
      match = pattern.exec(rawSource);
    }
  };

  // (a) ARG-VECTOR families: command literal stands alone (closing quote anchor).
  for (const callName of ARG_VECTOR_SPAWN_FAMILY) {
    runMatcher(
      callName,
      new RegExp(`\\b${callName}\\s*\\(\\s*["'\`](?:${escapedCommands})["'\`]`, "g")
    );
  }

  // (b) SHELL-STRING families: program name may continue after npm/npx, so the
  // closing quote OR a whitespace must follow (covers `exec("npm")` and
  // `exec("npm install")`).
  for (const callName of SHELL_STRING_SPAWN_FAMILY) {
    runMatcher(
      callName,
      new RegExp(`\\b${callName}\\s*\\(\\s*["'\`]${escapedProgramNames}(?=[\\s"'\`])`, "g")
    );
  }

  // (c) SIMPLE VARIABLE indirection: collect within-file string-literal command
  // bindings, then flag any spawn-family call whose command argument is one of
  // those bound identifiers.
  const commandVariableNames = collectCommandVariableNames(rawSource);
  if (commandVariableNames.size > 0) {
    const varPattern = [...commandVariableNames].map(escapeRegex).join("|");
    for (const callName of SPAWN_FAMILY) {
      runMatcher(callName, new RegExp(`\\b${callName}\\s*\\(\\s*(?:${varPattern})\\s*[,)]`, "g"));
    }
  }

  // (d) GENERALIZED Windows batch-shim spawn: ANY string-literal command ending
  // in `.cmd` / `.bat` (case-insensitive), regardless of base name. This
  // subsumes the npm.cmd/npx.cmd shapes above and additionally catches
  // yarn.cmd, pnpm.cmd, foo.bat, etc. -- every Windows batch shim spawned
  // directly hits Node CVE-2024-27980 / EINVAL. Same exclusions apply: the
  // command slot only (arg 0 for arg-vector families; the leading program token
  // for shell-string families), real-code cross-check via the stripped source,
  // and the cmd.exe wrapper (`process.env.ComSpec` / `"cmd.exe"`) is naturally
  // excluded because `.exe` is neither `.cmd` nor `.bat`.
  findBatchShimSpawn(rawSource, strippedSource, violations);

  // De-duplicate by line: shape (d) intentionally overlaps shapes (a)/(b) for
  // npm.cmd/npx.cmd, so a single offending line must report exactly once.
  return dedupeViolationsByLine(violations);
}

/**
 * Flag direct spawning of ANY Windows batch shim -- a string-literal command
 * ending in `.cmd` or `.bat` (case-insensitive) in the COMMAND slot:
 *   - ARG-VECTOR families (`spawnSync` / `spawn` / `execFileSync` / `execFile`):
 *     arg 0 is the whole command literal, so the entire literal payload (after
 *     stripping its quotes) must end in `.cmd`/`.bat`. A `.cmd`/`.bat` appearing
 *     inside the args VECTOR (arg 1+) or as any later argument is NOT the
 *     command and is not flagged.
 *   - SHELL-STRING families (`exec` / `execSync`): arg 0 is a command LINE, so
 *     only the LEADING program token (first whitespace-delimited token) is the
 *     command. `execSync("foo.bat install")` is flagged; `execSync("echo
 *     build.bat")` is not (the shim is an argument to `echo`, not the program).
 *
 * Arguments are read from the RAW source (string payloads must be visible), with
 * a stripped-source cross-check (pushIfRealCode) to discard commented/stringified
 * occurrences. The cmd.exe wrapper itself (`process.env.ComSpec`, `"cmd.exe"`)
 * is never a `.cmd`/`.bat` literal, so it is excluded by construction.
 *
 * @param {string} rawSource - Original source (LF-normalized).
 * @param {string} strippedSource - Source with comments/strings erased.
 * @param {Array<{line: number, snippet: string}>} violations - Accumulator.
 */
function findBatchShimSpawn(rawSource, strippedSource, violations) {
  // Quote char, then the literal payload (no inner quote of the same kind), with
  // an optional leading args group for the shell-string program-token form. The
  // payload is captured so its tail / leading token can be inspected without
  // backtracking-heavy patterns. `[^"'`]*` is linear (no nested quantifier).
  const argVectorLiteral = /^(["'`])([^"'`]*)\1$/;
  // A leading program token inside a shell-string literal: everything up to the
  // first ASCII whitespace OR the closing quote, captured for suffix inspection.
  // Excluding the quote chars keeps a bare single-token line (`"foo.bat"`) from
  // capturing its own closing quote into the token.
  const leadingTokenRe = /^(["'`])([^\s"'`]*)/;
  const batchSuffix = /\.(?:cmd|bat)$/i;

  for (const callName of ARG_VECTOR_SPAWN_FAMILY) {
    const cmdIndex = 0;
    for (const call of extractCallArgumentLists(rawSource, callName)) {
      const args = splitTopLevelArguments(call.argsText);
      const commandArg = args[cmdIndex];
      if (!commandArg) {
        continue;
      }
      const literal = argVectorLiteral.exec(commandArg);
      if (literal && batchSuffix.test(literal[2])) {
        pushIfRealCode(
          { rawSource, strippedSource, callName, violations },
          call.startIndex,
          commandArg
        );
      }
    }
  }

  for (const callName of SHELL_STRING_SPAWN_FAMILY) {
    const cmdIndex = 0;
    for (const call of extractCallArgumentLists(rawSource, callName)) {
      const args = splitTopLevelArguments(call.argsText);
      const commandArg = args[cmdIndex];
      if (!commandArg) {
        continue;
      }
      const token = leadingTokenRe.exec(commandArg);
      if (token && batchSuffix.test(token[2])) {
        pushIfRealCode(
          { rawSource, strippedSource, callName, violations },
          call.startIndex,
          commandArg
        );
      }
    }
  }
}

/**
 * Collapse violations that share a line number to a single entry, preferring the
 * first-seen snippet. The generalized `.cmd`/`.bat` check (shape d) overlaps the
 * npm/npx checks (shapes a/b) on the same call site, so one offending line must
 * not be reported twice.
 *
 * @param {Array<{line: number, snippet: string}>} violations - Raw violations.
 * @returns {Array<{line: number, snippet: string}>} One entry per line.
 */
function dedupeViolationsByLine(violations) {
  const byLine = new Map();
  for (const violation of violations) {
    if (!byLine.has(violation.line)) {
      byLine.set(violation.line, violation);
    }
  }
  return [...byLine.values()];
}

// Top-level simple string-literal command bindings of a forbidden command name.
// Conservative and within-file by design (see file header residual limits);
// mirrors the variable-tracking style in scripts/validate-node-tooling.js.
const COMMAND_LITERAL_BINDING_RE =
  /\b(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*(["'`])(?:npm|npx)(?:\.cmd)?\2/g;

/**
 * Collect the names of top-level simple bindings whose initializer is a forbidden
 * command string literal (`const c = "npm"`, `let x = 'npx.cmd'`, etc). Runs on
 * RAW source so the string payload is visible.
 *
 * @param {string} rawSource - Original source (LF-normalized).
 * @returns {Set<string>} Bound identifier names.
 */
function collectCommandVariableNames(rawSource) {
  const names = new Set();
  COMMAND_LITERAL_BINDING_RE.lastIndex = 0;
  let match = COMMAND_LITERAL_BINDING_RE.exec(rawSource);
  while (match !== null) {
    names.add(match[1]);
    match = COMMAND_LITERAL_BINDING_RE.exec(rawSource);
  }
  return names;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe("spawn-invocation-policy (repo-wide)", () => {
  test("Policy A: no test asserts toShellCommand(...) or a .cmd literal as an expected spawn command", () => {
    const offenders = [];

    for (const abs of listTestFiles()) {
      const repoRelative = toRepoRelative(abs);
      if (TEST_ALLOW_LIST.has(path.relative(REPO_ROOT, abs))) {
        continue;
      }
      const raw = readUtf8(abs, { stripBom: false });
      const stripped = stripJsCommentsAndStrings(raw);
      const aliases = collectToShellCommandAliasesFromRaw(raw);

      const toShellHits = findToShellCommandInCalledWith(stripped, aliases).map((v) => ({
        ...v,
        reason: "toShellCommand-as-expected-command"
      }));
      const literalHits = findShimLiteralCommandInCalledWith(raw).map((v) => ({
        ...v,
        reason: "npm.cmd/npx.cmd-literal-as-expected-command"
      }));

      for (const hit of [...toShellHits, ...literalHits]) {
        offenders.push({ file: repoRelative, ...hit });
      }
    }

    if (offenders.length > 0) {
      const details = offenders
        .map((o) => `  ${o.file}:${o.line} [${o.reason}]${o.snippet ? `: ${o.snippet}` : ""}`)
        .join("\n");
      throw new Error(
        "Policy A violation: spawn-shape assertion(s) use a host-platform-dependent " +
          "expected command.\n" +
          '`toShellCommand("npm")` returns "npm" on Linux and "npm.cmd" on Windows, and a ' +
          'raw "npm.cmd" literal ignores the cmd.exe wrapper entirely -- both diverge from ' +
          "production (spawnPlatformCommandSync) on Windows and rot silently on Linux.\n" +
          "FIX: derive the expected command/args from buildSpawnInvocation(...) " +
          "(scripts/lib/shell-command.js), e.g.\n" +
          '  const inv = buildSpawnInvocation("npm", ["pack", ...]);\n' +
          "  expect(spy).toHaveBeenCalledWith(inv.command, inv.args, expect.objectContaining({...}));\n\n" +
          "Offending assertions:\n" +
          details
      );
    }
  });

  test("Policy B: no production script spawns npm/npx or any .cmd/.bat shim directly via child_process", () => {
    const offenders = [];

    for (const abs of listProductionScriptFiles()) {
      const repoRelative = path
        .join("scripts", path.relative(SCRIPTS_ROOT, abs))
        .split(path.sep)
        .join("/");
      if (PRODUCTION_ALLOW_LIST.has(path.relative(REPO_ROOT, abs))) {
        continue;
      }
      const raw = readUtf8(abs, { stripBom: false });
      const stripped = stripJsCommentsAndStrings(raw);
      const hits = findDirectNpmSpawn(raw, stripped);
      for (const hit of hits) {
        offenders.push({ file: repoRelative, ...hit });
      }
    }

    if (offenders.length > 0) {
      const details = offenders.map((o) => `  ${o.file}:${o.line}: ${o.snippet}`).join("\n");
      throw new Error(
        "Policy B violation: production script(s) spawn npm/npx -- or another Windows " +
          "batch shim (a .cmd/.bat command) -- directly via child_process.\n" +
          "Direct execution of Windows batch-file shims (npm.cmd/npx.cmd/yarn.cmd/foo.bat/...) " +
          "is unsafe (Node CVE-2024-27980) and breaks on Windows; it also bypasses the shell-shim " +
          "execution rules (cmd.exe /d /s /c wrapping, shell:false, windowsHide).\n" +
          "FIX: route the call through spawnPlatformCommandSync() from scripts/lib/shell-command.js " +
          "(extend isShellShimCommand() there if this is a new shim), or wrap it explicitly as " +
          "`<ComSpec> /d /s /c <shim> ...args` with shell:false.\n\n" +
          "Offending call sites:\n" +
          details
      );
    }
  });

  // -------------------------------------------------------------------------
  // Self-tests: prove the detectors fire on the exact anti-patterns and do NOT
  // false-positive on the legitimate shapes. These exercise the detector
  // functions directly with crafted source strings so the guard is verified
  // without mutating real repository files.
  // -------------------------------------------------------------------------
  describe("detector self-tests", () => {
    test("findToShellCommandInCalledWith flags toShellCommand inside toHaveBeenCalledWith", () => {
      const source = stripJsCommentsAndStrings(
        'expect(spy).toHaveBeenCalledWith(toShellCommand("npm"), ["pack"], {});'
      );
      expect(findToShellCommandInCalledWith(source)).toHaveLength(1);
    });

    test("findToShellCommandInCalledWith flags toShellCommand inside toHaveBeenNthCalledWith", () => {
      const source = stripJsCommentsAndStrings(
        'expect(spy).toHaveBeenNthCalledWith(2, toShellCommand("npx"), [], {});'
      );
      expect(findToShellCommandInCalledWith(source)).toHaveLength(1);
    });

    test("findToShellCommandInCalledWith flags toShellCommand inside toHaveBeenLastCalledWith", () => {
      const source = stripJsCommentsAndStrings(
        'expect(spy).toHaveBeenLastCalledWith(toShellCommand("npm"), ["pack"], {});'
      );
      expect(findToShellCommandInCalledWith(source)).toHaveLength(1);
    });

    test("findToShellCommandInCalledWith does NOT flag a bare toShellCommand unit assertion", () => {
      // This is the legitimate shape in shell-command.test.js / run-managed-jest.test.js.
      const source = stripJsCommentsAndStrings(
        'expect(toShellCommand("npm", "win32")).toBe("npm.cmd");'
      );
      expect(findToShellCommandInCalledWith(source)).toHaveLength(0);
    });

    test("findToShellCommandInCalledWith does NOT flag a buildSpawnInvocation-derived assertion", () => {
      const source = stripJsCommentsAndStrings(
        "expect(spy).toHaveBeenCalledWith(inv.command, inv.args, expect.objectContaining({}));"
      );
      expect(findToShellCommandInCalledWith(source)).toHaveLength(0);
    });

    test("findToShellCommandInCalledWith flags the member-access form x.toShellCommand(", () => {
      const source = stripJsCommentsAndStrings(
        'expect(spy).toHaveBeenCalledWith(shell.toShellCommand("npm"), [], {});'
      );
      expect(findToShellCommandInCalledWith(source)).toHaveLength(1);
    });

    test("collectToShellCommandAliasesFromRaw parses a destructured alias from shell-command require", () => {
      const raw = 'const { toShellCommand: tsc } = require("../lib/shell-command");';
      const aliases = collectToShellCommandAliasesFromRaw(raw);
      expect(aliases.has("toShellCommand")).toBe(true);
      expect(aliases.has("tsc")).toBe(true);
    });

    test("findToShellCommandInCalledWith flags an aliased toShellCommand callee", () => {
      const raw =
        'const { toShellCommand: tsc } = require("../lib/shell-command");\n' +
        'expect(spy).toHaveBeenCalledWith(tsc("npm"), ["pack"], {});';
      const aliases = collectToShellCommandAliasesFromRaw(raw);
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findToShellCommandInCalledWith(stripped, aliases)).toHaveLength(1);
    });

    test("findToShellCommandInCalledWith does NOT flag an unrelated identifier that ends in the alias", () => {
      // `myTsc(...)` must not be conflated with the alias `tsc`.
      const raw =
        'const { toShellCommand: tsc } = require("../lib/shell-command");\n' +
        'expect(spy).toHaveBeenCalledWith(myTsc("npm"), [], {});';
      const aliases = collectToShellCommandAliasesFromRaw(raw);
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findToShellCommandInCalledWith(stripped, aliases)).toHaveLength(0);
    });

    test("findShimLiteralCommandInCalledWith flags a raw npm.cmd literal as expected command", () => {
      const source = 'expect(spy).toHaveBeenCalledWith("npm.cmd", ["pack"], {});';
      expect(findShimLiteralCommandInCalledWith(source)).toHaveLength(1);
    });

    test("findShimLiteralCommandInCalledWith flags a backtick-quoted npm.cmd literal", () => {
      const source = "expect(spy).toHaveBeenCalledWith(`npm.cmd`, ['pack'], {});";
      expect(findShimLiteralCommandInCalledWith(source)).toHaveLength(1);
    });

    test("findShimLiteralCommandInCalledWith flags a backtick npm.cmd in toHaveBeenLastCalledWith", () => {
      const source = "expect(spy).toHaveBeenLastCalledWith(`npm.cmd`, ['pack'], {});";
      expect(findShimLiteralCommandInCalledWith(source)).toHaveLength(1);
    });

    test("findShimLiteralCommandInCalledWith does NOT flag npm.cmd outside a CalledWith matcher", () => {
      const source = 'expect(toShellCommand("npm", "win32")).toBe("npm.cmd");';
      expect(findShimLiteralCommandInCalledWith(source)).toHaveLength(0);
    });

    test("findShimLiteralCommandInCalledWith does NOT flag npm.cmd in the win32 ARGS array slot", () => {
      const source =
        'expect(spy).toHaveBeenCalledWith("cmd.exe", ["/d", "/s", "/c", "npm.cmd", "pack"], {});';
      expect(findShimLiteralCommandInCalledWith(source)).toHaveLength(0);
    });

    test("findDirectNpmSpawn flags spawnSync('npm', ...) in production code", () => {
      const raw = 'const r = childProcess.spawnSync("npm", ["--version"], {});';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(1);
    });

    test("findDirectNpmSpawn flags execFileSync('npx', ...) and the .cmd shims", () => {
      const raw = [
        'execFileSync("npx", ["jest"]);',
        'spawn("npm.cmd", []);',
        'spawnSync("npx.cmd", []);'
      ].join("\n");
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(3);
    });

    test("findDirectNpmSpawn flags execSync('npm') (bare program)", () => {
      const raw = 'const out = childProcess.execSync("npm");';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(1);
    });

    test("findDirectNpmSpawn flags the shell-STRING form execSync('npm install --no-save')", () => {
      const raw = 'childProcess.execSync("npm install --no-save", { stdio: "inherit" });';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(1);
    });

    test("findDirectNpmSpawn flags exec('npm install', cb) (the natural exec form)", () => {
      const raw = 'exec("npm install", (err) => {});';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(1);
    });

    test("findDirectNpmSpawn flags the backtick shell-string form exec(`npx jest`)", () => {
      const raw = "exec(`npx jest --runInBand`);";
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(1);
    });

    test("findDirectNpmSpawn flags execSync('npm.cmd run build') (.cmd shim shell-string)", () => {
      const raw = 'execSync("npm.cmd run build");';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(1);
    });

    test("findDirectNpmSpawn flags a simple within-file variable command binding", () => {
      const raw = 'const c = "npm";\nchildProcess.spawnSync(c, ["--version"], {});';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(1);
    });

    test("findDirectNpmSpawn flags a let-bound .cmd command variable passed to execSync", () => {
      const raw = 'let cmd = "npx.cmd";\nexecSync(cmd);';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(1);
    });

    test("findDirectNpmSpawn does NOT flag exec('node --version') (unrelated program)", () => {
      const raw = 'execSync("node --version");';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(0);
    });

    test("findDirectNpmSpawn does NOT flag exec('npmlogger ...') (npm as a name prefix)", () => {
      // `npmlogger` is a different program; npm must be a whole token (followed
      // by whitespace or the closing quote), not a substring prefix.
      const raw = 'execSync("npmlogger --report");';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(0);
    });

    test("findDirectNpmSpawn does NOT flag a variable bound to an unrelated command", () => {
      const raw = 'const c = "node";\nchildProcess.spawnSync(c, ["--version"], {});';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(0);
    });

    test("findDirectNpmSpawn does NOT flag spawnPlatformCommandSync('npm', ...)", () => {
      const raw = 'spawnPlatformCommandSync("npm", ["--version"], {});';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(0);
    });

    test("findDirectNpmSpawn does NOT flag spawnSync(process.execPath, ...) (node + cli.js)", () => {
      const raw = 'spawnSync(process.execPath, [LOCAL_JEST_BIN, "--version"]);';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(0);
    });

    test("findDirectNpmSpawn does NOT flag a commented-out or documented spawnSync('npm')", () => {
      const raw = '// historical: childProcess.spawnSync("npm", ["--version"]);';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(0);
    });

    test("commandArgIndexFor returns 0 for toHaveBeenLastCalledWith (index-0 command slot)", () => {
      expect(commandArgIndexFor("toHaveBeenLastCalledWith")).toBe(0);
      expect(commandArgIndexFor("toHaveBeenCalledWith")).toBe(0);
      expect(commandArgIndexFor("toHaveBeenNthCalledWith")).toBe(1);
    });

    // ---- Generalized .cmd/.bat batch-shim detection (shape d) ----

    test("findDirectNpmSpawn flags an arg-vector spawn of a non-npm .cmd shim (yarn.cmd)", () => {
      const raw = 'const r = childProcess.spawnSync("yarn.cmd", ["install"], {});';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(1);
    });

    test("findDirectNpmSpawn flags a shell-string exec of a .bat program (foo.bat install)", () => {
      const raw = 'execSync("foo.bat install");';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(1);
    });

    test("findDirectNpmSpawn flags a bare .bat program with no args (execSync('foo.bat'))", () => {
      const raw = 'execSync("foo.bat");';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(1);
    });

    test("findDirectNpmSpawn flags a .CMD shim case-insensitively (BUILD.CMD)", () => {
      const raw = 'spawn("BUILD.CMD", []);';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(1);
    });

    test("findDirectNpmSpawn flags a relative-path .cmd command in the command slot", () => {
      const raw = 'spawnSync("./scripts/install.cmd", ["--all"], {});';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(1);
    });

    test("findDirectNpmSpawn reports npm.cmd exactly once despite shape-(a)/(d) overlap", () => {
      const raw = 'spawnSync("npm.cmd", ["pack"], {});';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(1);
    });

    test("findDirectNpmSpawn does NOT flag a .cmd in a NON-command arg-vector slot (git arg)", () => {
      // `.cmd` appears in the args VECTOR, not the command slot.
      const raw = 'spawnSync("git", ["x.cmd"], {});';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(0);
    });

    test("findDirectNpmSpawn does NOT flag the cmd.exe wrapper itself (ComSpec + .cmd in args)", () => {
      const raw = 'spawnSync(process.env.ComSpec, ["/d", "/s", "/c", "npm.cmd"]);';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(0);
    });

    test("findDirectNpmSpawn does NOT flag a literal 'cmd.exe' command (ends in .exe)", () => {
      const raw = 'spawnSync("cmd.exe", ["/d", "/s", "/c", "build.bat"]);';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(0);
    });

    test("findDirectNpmSpawn does NOT flag a .bat that is an argument to a shell-string program (echo)", () => {
      const raw = 'execSync("echo build.bat");';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(0);
    });

    test("findDirectNpmSpawn does NOT flag a .cmd path passed as a later argument to node", () => {
      const raw = 'spawnSync(process.execPath, ["runner.js", "task.cmd"]);';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(0);
    });

    test("findDirectNpmSpawn does NOT flag a commented-out .bat spawn", () => {
      const raw = '// legacy: spawnSync("setup.bat", ["--init"]);';
      const stripped = stripJsCommentsAndStrings(raw);
      expect(findDirectNpmSpawn(raw, stripped)).toHaveLength(0);
    });
  });
});
