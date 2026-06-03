/**
 * @fileoverview Regression guard: a .cs file in an ALL-PLATFORMS test assembly
 * that references `UnityEditor` must keep that reference behind a `#if
 * UNITY_EDITOR` guard.
 *
 * THE CLASS THIS GUARDS. A test asmdef with `includePlatforms: []` (the default,
 * "any platform") is compiled into the IL2CPP standalone test PLAYER as well as
 * the Editor. `UnityEditor` (and types like `SerializedObject`/`SerializedProperty`)
 * does NOT exist in a player, so any unguarded `using UnityEditor;` / `UnityEditor.X`
 * fails the standalone build with `CS0246: The type or namespace name '...' could
 * not be found`. This shipped in MessagingComponentInstallerSceneTests.cs (in the
 * all-platforms Tests.Runtime assembly), which used `SerializedObject` under only
 * a `#if UNITY_2021_3_OR_NEWER` guard and broke every standalone job's compile.
 *
 * The fix is to wrap such editor-only fixtures in `#if UNITY_EDITOR` (they cannot
 * run in a player anyway) -- or move them to an Editor-only asmdef. This guard
 * enforces it: for every all-platforms test asmdef, any `using UnityEditor` /
 * `UnityEditor.` reference must sit inside a `#if` region whose condition
 * includes UNITY_EDITOR. Editor-only asmdefs (`includePlatforms: ["Editor"]`,
 * e.g. Tests.Editor and the DI integration suites) are exempt -- they never
 * compile into a player.
 *
 * Fast static scan with a small #if-region tracker. Comment lines are ignored so
 * prose mentioning "UnityEditor" does not false-positive.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const TESTS_DIR = path.join(REPO_ROOT, "Tests");

function readJsonLoose(file) {
  return JSON.parse(fs.readFileSync(file, "utf8").replace(/^﻿/, ""));
}

function walk(dir, filter) {
  const out = [];
  if (!fs.existsSync(dir)) {
    return out;
  }
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      out.push(...walk(full, filter));
    } else if (entry.isFile() && filter(entry.name)) {
      out.push(full);
    }
  }
  return out;
}

/** Nearest ancestor .asmdef for a .cs file (Unity assigns a script to it). */
function nearestAsmdef(csFile) {
  let dir = path.dirname(csFile);
  while (dir.startsWith(TESTS_DIR)) {
    const asmdef = fs.readdirSync(dir).find((n) => n.endsWith(".asmdef"));
    if (asmdef) {
      return path.join(dir, asmdef);
    }
    if (dir === TESTS_DIR) {
      break;
    }
    dir = path.dirname(dir);
  }
  return null;
}

/** True when the asmdef compiles into a player (includePlatforms empty/absent). */
function isAllPlatforms(asmdefPath) {
  try {
    const platforms = readJsonLoose(asmdefPath).includePlatforms;
    return !Array.isArray(platforms) || platforms.length === 0;
  } catch {
    return false;
  }
}

// A real code reference to UnityEditor: the `using UnityEditor;` directive or a
// qualified `UnityEditor.` member access. Apostrophes (UnityEditor's ...) and
// other prose do not match.
const EDITOR_USING_REGEX = /\busing\s+UnityEditor\b/;
const EDITOR_QUALIFIED_REGEX = /\bUnityEditor\s*\./;
const COMMENT_LINE_REGEX = /^\s*(\/\/|\/\*|\*)/;
// A #if/#elif condition compiles ONLY in the Editor (so a UnityEditor reference
// inside it is player-safe) iff EVERY ||-disjunct requires UNITY_EDITOR. A lone
// `UNITY_EDITOR`, or `<anything> && UNITY_EDITOR`, qualifies. But
// `UNITY_EDITOR || X` ALSO compiles in a player when X is a runtime symbol --
// e.g. `#if UNITY_EDITOR || UNITY_STANDALONE` is true in a standalone build, so
// an unguarded UnityEditor reference inside it still breaks that build with
// CS0246. Such an OR is therefore NOT editor-only. A negated `!UNITY_EDITOR`
// never qualifies. We err toward flagging (zero false negatives): a redundant
// editor-only OR like `UNITY_EDITOR || UNITY_EDITOR_WIN` is conservatively
// flagged -- rewrite it as plain `#if UNITY_EDITOR`.
function conditionIsEditor(condition) {
  return condition
    .split("||")
    .every(
      (disjunct) =>
        /(?:^|[^!\w])UNITY_EDITOR\b/.test(disjunct) && !/!\s*UNITY_EDITOR\b/.test(disjunct)
    );
}

/**
 * Return the line entries (1-based "n: text") that reference UnityEditor outside
 * any UNITY_EDITOR-guarded #if region. Tracks #if/#elif/#else/#endif nesting.
 */
function unguardedEditorReferences(content) {
  const lines = content.split(/\r\n|\r|\n/);
  /** @type {boolean[]} editor-guarded flag per open #if region */
  const stack = [];
  const guardedNow = () => stack.some((editor) => editor);
  const offenders = [];

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const ifMatch = /^\s*#\s*if\s+(.+)$/.exec(line);
    if (ifMatch) {
      stack.push(conditionIsEditor(ifMatch[1]));
      continue;
    }
    const elifMatch = /^\s*#\s*elif\s+(.+)$/.exec(line);
    if (elifMatch) {
      if (stack.length) {
        stack[stack.length - 1] = conditionIsEditor(elifMatch[1]);
      }
      continue;
    }
    if (/^\s*#\s*else\b/.test(line)) {
      if (stack.length) {
        // The #else branch of a UNITY_EDITOR guard is the NON-editor branch.
        stack[stack.length - 1] = false;
      }
      continue;
    }
    if (/^\s*#\s*endif\b/.test(line)) {
      stack.pop();
      continue;
    }
    if (COMMENT_LINE_REGEX.test(line)) {
      continue;
    }
    if ((EDITOR_USING_REGEX.test(line) || EDITOR_QUALIFIED_REGEX.test(line)) && !guardedNow()) {
      offenders.push(`${i + 1}: ${line.trim()}`);
    }
  }
  return offenders;
}

describe("runtime test editor-guard policy (UnityEditor usage in all-platforms test asmdefs)", () => {
  const csFiles = walk(TESTS_DIR, (n) => n.endsWith(".cs"));
  // Only .cs whose owning asmdef compiles into a player AND that reference
  // UnityEditor in real code (not comments) are in scope.
  const inScope = csFiles
    .map((file) => {
      const asmdef = nearestAsmdef(file);
      return { file, asmdef };
    })
    .filter(({ asmdef }) => asmdef && isAllPlatforms(asmdef))
    .filter(({ file }) => {
      const content = fs.readFileSync(file, "utf8");
      return EDITOR_USING_REGEX.test(content) || EDITOR_QUALIFIED_REGEX.test(content);
    })
    .map(({ file, asmdef }) => [
      path.relative(REPO_ROOT, file),
      file,
      path.relative(REPO_ROOT, asmdef)
    ]);

  test("there is at least one all-platforms test asmdef (guard is wired to the right tree)", () => {
    const allPlatforms = walk(TESTS_DIR, (n) => n.endsWith(".asmdef")).filter(isAllPlatforms);
    expect(allPlatforms.length).toBeGreaterThan(0);
  });

  if (inScope.length === 0) {
    test("no all-platforms test file references UnityEditor (nothing to guard)", () => {
      expect(inScope).toEqual([]);
    });
  } else {
    test.each(inScope)(
      "%s (asmdef %s) guards every UnityEditor reference with #if UNITY_EDITOR",
      (_rel, file) => {
        const offenders = unguardedEditorReferences(fs.readFileSync(file, "utf8"));
        expect(offenders).toEqual([]);
      }
    );
  }
});

// Unit coverage for the #if-region tracker itself, on synthetic sources, so the
// classifier's own logic is verified independently of whatever the live tree
// happens to contain (a data-driven scan over real files can pass vacuously).
describe("runtime test editor-guard policy: #if-region classifier (synthetic)", () => {
  const ref = "    using UnityEditor;\n";
  const guard = (cond, body) => `#if ${cond}\n${body}#endif\n`;

  test("plain #if UNITY_EDITOR guards the reference", () => {
    expect(unguardedEditorReferences(guard("UNITY_EDITOR", ref))).toEqual([]);
  });

  test("#if <version> && UNITY_EDITOR (subset of editor) guards the reference", () => {
    expect(
      unguardedEditorReferences(guard("UNITY_2021_3_OR_NEWER && UNITY_EDITOR", ref))
    ).toEqual([]);
  });

  test("#if UNITY_EDITOR || <runtime symbol> does NOT guard -- compiles in player, must flag", () => {
    // The latent false-negative class: this region is also true in a standalone
    // IL2CPP player, where UnityEditor does not exist.
    expect(unguardedEditorReferences(guard("UNITY_EDITOR || UNITY_STANDALONE", ref))).toHaveLength(
      1
    );
  });

  test("#if !UNITY_EDITOR is the non-editor branch -- reference flagged", () => {
    expect(unguardedEditorReferences(guard("!UNITY_EDITOR", ref))).toHaveLength(1);
  });

  test("#elif UNITY_EDITOR guards a reference in the elif branch", () => {
    const src = `#if SOMETHING_ELSE\n    int x = 0;\n#elif UNITY_EDITOR\n${ref}#endif\n`;
    expect(unguardedEditorReferences(src)).toEqual([]);
  });

  test("#else after a UNITY_EDITOR #if is the non-editor branch -- reference flagged", () => {
    const src = `#if UNITY_EDITOR\n    int x = 0;\n#else\n${ref}#endif\n`;
    expect(unguardedEditorReferences(src)).toHaveLength(1);
  });

  test("nested non-editor #if inside an editor #if stays guarded", () => {
    const src = `#if UNITY_EDITOR\n#if UNITY_2021_3_OR_NEWER\n${ref}#endif\n#endif\n`;
    expect(unguardedEditorReferences(src)).toEqual([]);
  });

  test("a bare (unguarded) reference is flagged", () => {
    expect(unguardedEditorReferences(ref)).toHaveLength(1);
  });

  test("conditionIsEditor classifies OR/AND/negation correctly", () => {
    expect(conditionIsEditor("UNITY_EDITOR")).toBe(true);
    expect(conditionIsEditor("FOO && UNITY_EDITOR")).toBe(true);
    expect(conditionIsEditor("UNITY_EDITOR || UNITY_STANDALONE")).toBe(false);
    expect(conditionIsEditor("UNITY_EDITOR || UNITY_ANDROID")).toBe(false);
    expect(conditionIsEditor("!UNITY_EDITOR")).toBe(false);
    expect(conditionIsEditor("SOME_OTHER_SYMBOL")).toBe(false);
  });
});
