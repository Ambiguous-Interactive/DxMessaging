/**
 * @fileoverview Unit tests for scripts/lib/repo-files.js.
 *
 * Pins the canonical behavior of the shared file-discovery and text-reading
 * helpers (`readUtf8`, `lineNumberAt`, `walkFiles`, `toRepoRelative`,
 * `listTrackedFiles`). Many scripts and policy tests depend on these, so a
 * regression here would silently change line-number reporting, file walks, or
 * path normalization across the repo.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");

const {
  REPO_ROOT,
  readUtf8,
  lineNumberAt,
  walkFiles,
  toRepoRelative,
  listTrackedFiles
} = require("../repo-files");

/**
 * Create a throwaway directory under the OS temp root and remove it after the
 * callback runs. `walkFiles` has no path-exclusion logic (unlike check-eol),
 * so the OS temp root is a safe home for these fixtures.
 *
 * @param {(dir: string) => void} run
 */
function withTempDir(run) {
  const dir = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), "dxm-repo-files-")));
  try {
    run(dir);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

describe("REPO_ROOT", () => {
  test("points at the repository root containing package.json", () => {
    expect(fs.existsSync(path.join(REPO_ROOT, "package.json"))).toBe(true);
    expect(fs.existsSync(path.join(REPO_ROOT, "scripts", "lib", "repo-files.js"))).toBe(true);
  });
});

describe("readUtf8", () => {
  test("normalizes CRLF and lone CR to LF by default", () => {
    withTempDir((dir) => {
      const file = path.join(dir, "mixed.txt");
      fs.writeFileSync(file, "a\r\nb\rc\n");
      expect(readUtf8(file)).toBe("a\nb\nc\n");
    });
  });

  test("strips a single leading UTF-8 BOM by default", () => {
    withTempDir((dir) => {
      const file = path.join(dir, "bom.txt");
      fs.writeFileSync(file, "\uFEFFhello");
      expect(readUtf8(file)).toBe("hello");
    });
  });

  test("strips at most one BOM (a second U+FEFF is preserved)", () => {
    withTempDir((dir) => {
      const file = path.join(dir, "doublebom.txt");
      fs.writeFileSync(file, "\uFEFF\uFEFFx");
      expect(readUtf8(file)).toBe("\uFEFFx");
    });
  });

  test("normalizeEol:false preserves CRLF", () => {
    withTempDir((dir) => {
      const file = path.join(dir, "crlf.txt");
      fs.writeFileSync(file, "a\r\nb");
      expect(readUtf8(file, { normalizeEol: false })).toBe("a\r\nb");
    });
  });

  test("stripBom:false preserves the leading BOM", () => {
    withTempDir((dir) => {
      const file = path.join(dir, "rawbom.txt");
      fs.writeFileSync(file, "\uFEFFx");
      expect(readUtf8(file, { stripBom: false })).toBe("\uFEFFx");
    });
  });

  test("throws when the file does not exist", () => {
    withTempDir((dir) => {
      expect(() => readUtf8(path.join(dir, "missing.txt"))).toThrow();
    });
  });
});

describe("lineNumberAt", () => {
  test("offset 0 is line 1", () => {
    expect(lineNumberAt("abc\ndef", 0)).toBe(1);
  });

  test("offset before the first newline is line 1", () => {
    expect(lineNumberAt("abc\ndef", 2)).toBe(1);
  });

  test("offset after one newline is line 2", () => {
    const text = "abc\ndef";
    expect(lineNumberAt(text, text.indexOf("def"))).toBe(2);
  });

  test("counts only \\n, so the index after a newline starts the next line", () => {
    const text = "a\nb\nc";
    expect(lineNumberAt(text, text.length)).toBe(3);
  });

  test("empty prefix (index 0 on empty text) is line 1", () => {
    expect(lineNumberAt("", 0)).toBe(1);
  });

  test("a negative index is clamped to line 1 (never counts from the end)", () => {
    expect(lineNumberAt("a\nb\nc\nd", -2)).toBe(1);
    expect(lineNumberAt("a\nb\nc", -1)).toBe(1);
  });
});

describe("walkFiles", () => {
  test("recursively collects every file when no filter is given", () => {
    withTempDir((dir) => {
      fs.writeFileSync(path.join(dir, "a.txt"), "");
      fs.mkdirSync(path.join(dir, "sub"));
      fs.writeFileSync(path.join(dir, "sub", "b.txt"), "");
      fs.writeFileSync(path.join(dir, "sub", "c.md"), "");
      const found = walkFiles(dir).sort();
      expect(found).toEqual(
        [
          path.join(dir, "a.txt"),
          path.join(dir, "sub", "b.txt"),
          path.join(dir, "sub", "c.md")
        ].sort()
      );
    });
  });

  test("match filters files by full path", () => {
    withTempDir((dir) => {
      fs.writeFileSync(path.join(dir, "keep.md"), "");
      fs.writeFileSync(path.join(dir, "drop.txt"), "");
      const found = walkFiles(dir, { match: (full) => full.endsWith(".md") });
      expect(found).toEqual([path.join(dir, "keep.md")]);
    });
  });

  test("match receives the Dirent as a second argument", () => {
    withTempDir((dir) => {
      fs.writeFileSync(path.join(dir, "x.txt"), "");
      const names = [];
      walkFiles(dir, {
        match: (full, dirent) => {
          names.push(dirent.name);
          return true;
        }
      });
      expect(names).toEqual(["x.txt"]);
    });
  });

  test("excludeDir skips a directory subtree entirely", () => {
    withTempDir((dir) => {
      fs.writeFileSync(path.join(dir, "root.txt"), "");
      fs.mkdirSync(path.join(dir, "node_modules"));
      fs.writeFileSync(path.join(dir, "node_modules", "dep.txt"), "");
      const found = walkFiles(dir, {
        excludeDir: (full, dirent) => dirent.name === "node_modules"
      });
      expect(found).toEqual([path.join(dir, "root.txt")]);
    });
  });

  test("missing root directory returns [] and invokes onError once", () => {
    withTempDir((dir) => {
      const missing = path.join(dir, "does-not-exist");
      const errors = [];
      const found = walkFiles(missing, {
        onError: (error, failedDir) => errors.push([error.code, failedDir])
      });
      expect(found).toEqual([]);
      expect(errors).toHaveLength(1);
      expect(errors[0][0]).toBe("ENOENT");
      expect(errors[0][1]).toBe(missing);
    });
  });

  test("missing root directory is silent when onError is omitted", () => {
    withTempDir((dir) => {
      expect(walkFiles(path.join(dir, "nope"))).toEqual([]);
    });
  });
});

describe("toRepoRelative", () => {
  test("absolute path inside the repo becomes a POSIX relative path", () => {
    const abs = path.join(REPO_ROOT, "scripts", "lib", "repo-files.js");
    expect(toRepoRelative(abs)).toBe("scripts/lib/repo-files.js");
  });

  test("relative input is resolved against cwd", () => {
    expect(toRepoRelative("repo-files.js", { cwd: path.join(REPO_ROOT, "scripts", "lib") })).toBe(
      "scripts/lib/repo-files.js"
    );
  });

  test("the repo root itself maps to its POSIX-absolute form", () => {
    expect(toRepoRelative(REPO_ROOT)).toBe(REPO_ROOT.split(path.sep).join("/"));
  });

  test("a path outside the repo falls back to the POSIX-absolute form", () => {
    withTempDir((dir) => {
      const outside = path.join(dir, "outside.txt");
      expect(toRepoRelative(outside)).toBe(outside.split(path.sep).join("/"));
    });
  });

  test("honors a custom repoRoot", () => {
    const abs = path.join(REPO_ROOT, "scripts", "lib", "repo-files.js");
    expect(toRepoRelative(abs, { repoRoot: path.join(REPO_ROOT, "scripts") })).toBe(
      "lib/repo-files.js"
    );
  });

  test("non-string input is returned unchanged", () => {
    expect(toRepoRelative(null)).toBe(null);
    expect(toRepoRelative(undefined)).toBe(undefined);
    expect(toRepoRelative(42)).toBe(42);
  });
});

describe("listTrackedFiles", () => {
  test("returns tracked files matching a pathspec", () => {
    // Reference an already-committed file so the assertion does not depend on
    // whether this test run's new files have been staged yet.
    const files = listTrackedFiles(["scripts/lib/path-classifier.js"]);
    expect(files).toContain("scripts/lib/path-classifier.js");
  });

  test("emits POSIX forward-slash paths", () => {
    const files = listTrackedFiles(["scripts/lib/*.js"]);
    expect(files.length).toBeGreaterThan(0);
    for (const file of files) {
      expect(file).not.toContain("\\");
    }
  });

  test("an empty pathspec lists the whole tree", () => {
    const all = listTrackedFiles();
    expect(all).toContain("package.json");
    expect(all.length).toBeGreaterThan(100);
  });

  test("throws when cwd is not a git repository", () => {
    withTempDir((dir) => {
      // Stop git from discovering an ancestor repository above the temp dir so
      // the not-a-repo failure is deterministic wherever the OS temp root lives.
      const savedCeiling = process.env.GIT_CEILING_DIRECTORIES;
      process.env.GIT_CEILING_DIRECTORIES = path.dirname(dir);
      try {
        expect(() => listTrackedFiles([], { cwd: dir })).toThrow(/git ls-files/);
      } finally {
        if (savedCeiling === undefined) {
          delete process.env.GIT_CEILING_DIRECTORIES;
        } else {
          process.env.GIT_CEILING_DIRECTORIES = savedCeiling;
        }
      }
    });
  });
});
