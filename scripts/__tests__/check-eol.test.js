/**
 * @fileoverview Tests for check-eol.js helper behavior.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");
const {
  splitNormalizedLines,
  isIndexEolViolation,
  hasBom,
  hasNonCrlfEol,
  hasNonLfEol,
  isPathExcluded
} = require("../check-eol.js");
const { crlfExts, lfExts } = require("../lib/eol-policy.js");

function getLeadingBlockComment(filePath) {
  const content = fs.readFileSync(filePath, "utf8");
  const match = content.match(/\/\*[\s\S]*?\*\//);
  return match ? match[0] : "";
}

describe("check-eol helpers", () => {
  describe("splitNormalizedLines", () => {
    test("normalizes lone CR and CRLF before splitting", () => {
      const lines = splitNormalizedLines("a\r\nb\rc\n");
      expect(lines).toEqual(["a", "b", "c", ""]);
    });

    test("returns single line for content without line endings", () => {
      const lines = splitNormalizedLines("abc");
      expect(lines).toEqual(["abc"]);
    });
  });

  describe("hasBom", () => {
    test("detects UTF-8 BOM", () => {
      const withBom = Buffer.from([0xef, 0xbb, 0xbf, 0x61]);
      expect(hasBom(withBom)).toBe(true);
    });

    test("returns false when BOM is absent", () => {
      const withoutBom = Buffer.from("abc", "utf8");
      expect(hasBom(withoutBom)).toBe(false);
    });
  });

  describe("hasNonCrlfEol", () => {
    test("returns false for CRLF-only content", () => {
      const buf = Buffer.from("a\r\nb\r\n", "utf8");
      expect(hasNonCrlfEol(buf)).toBe(false);
    });

    test("returns true for lone CR", () => {
      const buf = Buffer.from("a\rb\r", "utf8");
      expect(hasNonCrlfEol(buf)).toBe(true);
    });

    test("returns true for LF-only content", () => {
      const buf = Buffer.from("a\nb\n", "utf8");
      expect(hasNonCrlfEol(buf)).toBe(true);
    });
  });

  describe("hasNonLfEol", () => {
    test("returns false for LF-only content", () => {
      const buf = Buffer.from("a\nb\n", "utf8");
      expect(hasNonLfEol(buf)).toBe(false);
    });

    test("returns true for CRLF content", () => {
      const buf = Buffer.from("a\r\nb\r\n", "utf8");
      expect(hasNonLfEol(buf)).toBe(true);
    });

    test("returns true for lone CR content", () => {
      const buf = Buffer.from("a\rb\r", "utf8");
      expect(hasNonLfEol(buf)).toBe(true);
    });
  });

  describe("EOL policy sync", () => {
    test("includes .props in CRLF extension set", () => {
      expect(crlfExts.has(".props")).toBe(true);
    });

    test("matches .gitattributes CRLF extension policy", () => {
      const gitattributesPath = path.resolve(__dirname, "../../.gitattributes");
      const content = fs.readFileSync(gitattributesPath, "utf8");

      const crlfFromGitattributes = new Set(
        content
          .split(/\r\n|\r|\n/)
          .map((line) => line.trim())
          .filter((line) => line && !line.startsWith("#"))
          .map((line) => line.split(/\s+/))
          .filter((parts) => parts[0].startsWith("*.") && parts.includes("eol=crlf"))
          .map((parts) => `.${parts[0].slice(2).toLowerCase()}`)
      );

      expect(new Set(crlfExts)).toEqual(crlfFromGitattributes);
    });

    test("LF extension policy is compatible with .gitattributes", () => {
      const gitattributesPath = path.resolve(__dirname, "../../.gitattributes");
      const content = fs.readFileSync(gitattributesPath, "utf8");
      const lines = content
        .split(/\r\n|\r|\n/)
        .map((line) => line.trim())
        .filter((line) => line && !line.startsWith("#"));

      const hasDefaultLfRule = lines.some(
        (line) => line.startsWith("*") && line.includes("text=auto") && line.includes("eol=lf")
      );
      const explicitLfExts = new Set(
        lines
          .map((line) => line.split(/\s+/))
          .filter((parts) => parts[0].startsWith("*.") && parts.includes("eol=lf"))
          .map((parts) => `.${parts[0].slice(2).toLowerCase()}`)
      );

      for (const ext of lfExts) {
        // LF compatibility is satisfied by either an explicit per-extension rule or the default rule.
        expect(explicitLfExts.has(ext) || hasDefaultLfRule).toBe(true);
      }
    });

    test("LF extension policy includes all explicit .gitattributes LF entries", () => {
      const gitattributesPath = path.resolve(__dirname, "../../.gitattributes");
      const content = fs.readFileSync(gitattributesPath, "utf8");
      const explicitLfExts = new Set(
        content
          .split(/\r\n|\r|\n/)
          .map((line) => line.trim())
          .filter((line) => line && !line.startsWith("#"))
          .map((line) => line.split(/\s+/))
          .filter((parts) => parts[0].startsWith("*.") && parts.includes("eol=lf"))
          .map((parts) => `.${parts[0].slice(2).toLowerCase()}`)
      );

      for (const ext of explicitLfExts) {
        expect(lfExts.has(ext)).toBe(true);
      }
    });

    test("matches check-eol.ps1 CRLF extension policy", () => {
      const ps1Path = path.resolve(__dirname, "../check-eol.ps1");
      const content = fs.readFileSync(ps1Path, "utf8");

      const match = content.match(/\$crlfExtensions\s*=\s*@\(([\s\S]*?)\)/);
      expect(match).not.toBeNull();

      const fromPs1 = new Set(
        match[1]
          .split(",")
          .map((item) => item.trim())
          .filter(Boolean)
          .map((item) => item.replace(/['"\s]/g, "").toLowerCase())
      );

      expect(new Set(crlfExts)).toEqual(fromPs1);
    });

    test("matches check-eol.ps1 LF extension policy", () => {
      const ps1Path = path.resolve(__dirname, "../check-eol.ps1");
      const content = fs.readFileSync(ps1Path, "utf8");

      const match = content.match(/\$lfExtensions\s*=\s*@\(([\s\S]*?)\)/);
      expect(match).not.toBeNull();

      const fromPs1 = new Set(
        match[1]
          .split(",")
          .map((item) => item.trim())
          .filter(Boolean)
          .map((item) => item.replace(/['"\s]/g, "").toLowerCase())
      );

      expect(new Set(lfExts)).toEqual(fromPs1);
    });

    test("check-eol.ps1 enforces LF policy for extensionless git hooks", () => {
      const ps1Path = path.resolve(__dirname, "../check-eol.ps1");
      const content = fs.readFileSync(ps1Path, "utf8");

      expect(content).toMatch(/scripts\/hooks\//);
      expect(content).toMatch(/Test-IsGitHookPath/);
      expect(content).toMatch(/\$lfExtensions -contains \$ext -or \$isGitHook/);
    });

    test("does not overlap CRLF and LF extension sets", () => {
      const overlap = [...crlfExts].filter((ext) => lfExts.has(ext));
      expect(overlap).toEqual([]);
    });

    test("documents bidirectional sync references in shared policy files", () => {
      const eolPolicyPath = path.resolve(__dirname, "../lib/eol-policy.js");
      const checkEolPath = path.resolve(__dirname, "../check-eol.js");
      const fixEolPath = path.resolve(__dirname, "../fix-eol.js");
      const checkEolPs1Path = path.resolve(__dirname, "../check-eol.ps1");

      const eolPolicyContent = fs.readFileSync(eolPolicyPath, "utf8");
      expect(eolPolicyContent).toMatch(/scripts\/check-eol\.ps1/);
      expect(eolPolicyContent).toMatch(/scripts\/check-eol\.js/);
      expect(eolPolicyContent).toMatch(/scripts\/fix-eol\.js/);

      const checkEolContent = fs.readFileSync(checkEolPath, "utf8");
      expect(checkEolContent).toMatch(/scripts\/lib\/eol-policy\.js/);
      expect(checkEolContent).toMatch(/scripts\/check-eol\.ps1/);

      const fixEolContent = fs.readFileSync(fixEolPath, "utf8");
      expect(fixEolContent).toMatch(/scripts\/lib\/eol-policy\.js/);
      expect(fixEolContent).toMatch(/scripts\/check-eol\.ps1/);

      const checkEolPs1Content = fs.readFileSync(checkEolPs1Path, "utf8");
      expect(checkEolPs1Content).toMatch(/scripts\/lib\/eol-policy\.js/);
      expect(checkEolPs1Content).toMatch(/scripts\/check-eol\.js/);
      expect(checkEolPs1Content).toMatch(/scripts\/fix-eol\.js/);
    });
  });

  describe("EOL policy documentation", () => {
    test("describes mixed EOL policy in check and fix script headers", () => {
      const checkEolPath = path.resolve(__dirname, "../check-eol.js");
      const fixEolPath = path.resolve(__dirname, "../fix-eol.js");

      const checkEolHeader = getLeadingBlockComment(checkEolPath);
      const fixEolHeader = getLeadingBlockComment(fixEolPath);

      expect(checkEolHeader).toMatch(/mixed line-ending policy/i);
      expect(checkEolHeader).toMatch(/\.cs.*\.csproj.*\.sln.*\.props/i);
      expect(checkEolHeader).toMatch(/all other tracked text files.*LF/i);
      expect(checkEolHeader).not.toMatch(/Enforce CRLF line endings/i);

      expect(fixEolHeader).toMatch(/mixed line-ending policy/i);
      expect(fixEolHeader).toMatch(/converted to CRLF/i);
      expect(fixEolHeader).toMatch(/normalized to LF/i);
      expect(fixEolHeader).not.toMatch(/Fix CRLF line endings/i);
    });

    test("contributing guide states LF default and CRLF .NET exceptions", () => {
      const contributingPath = path.resolve(__dirname, "../../CONTRIBUTING.md");
      const content = fs.readFileSync(contributingPath, "utf8");

      expect(content).toMatch(/most text files to\s+\*\*LF\*\*/i);
      expect(content).toMatch(/C#\/\.NET files/i);
      expect(content).toMatch(/\.cs.*\.csproj.*\.sln.*\.props/i);
      expect(content).not.toMatch(/most text files to CRLF/i);
    });
  });

  describe("isIndexEolViolation (index-token classifier)", () => {
    // Regression guard for the git-hook failure where a staged file that git
    // classifies as binary (i/-text, e.g. it contains NUL bytes) was reported
    // as a non-normalized-EOL violation that NO line-ending fixer could clear.
    test("i/-text is NOT a violation (binary blob -- never EOL-converted)", () => {
      expect(isIndexEolViolation("i/-text")).toBe(false);
    });

    test("i/lf and i/none are NOT violations (already normalized)", () => {
      expect(isIndexEolViolation("i/lf")).toBe(false);
      expect(isIndexEolViolation("i/none")).toBe(false);
    });

    test("i/crlf and i/mixed ARE violations (genuine non-normalized endings)", () => {
      expect(isIndexEolViolation("i/crlf")).toBe(true);
      expect(isIndexEolViolation("i/mixed")).toBe(true);
    });

    test("unknown / future tokens default to non-violation (whitelist semantics)", () => {
      // Whitelist (only crlf/mixed fail) guarantees the checker can never
      // out-grow what fix-eol is able to normalize.
      expect(isIndexEolViolation("i/something-new")).toBe(false);
      expect(isIndexEolViolation("")).toBe(false);
      expect(isIndexEolViolation(undefined)).toBe(false);
    });
  });

  describe("script walker safety", () => {
    test("fix-eol walk warns instead of silently swallowing readdirSync errors", () => {
      const fixEolPath = path.resolve(__dirname, "../fix-eol.js");
      const content = fs.readFileSync(fixEolPath, "utf8");

      expect(content).toMatch(/Warning: Unable to read directory/);
      expect(content).not.toMatch(/catch\s*\{\s*return files;\s*\}/);
    });

    test("recursive script scanners guard readdirSync with error handling", () => {
      const scriptPaths = [
        path.resolve(__dirname, "../generate-skills-index.js"),
        path.resolve(__dirname, "../validate-skills.js"),
        path.resolve(__dirname, "../update-llms-txt.js"),
        path.resolve(__dirname, "../validate-workflows.js")
      ];

      for (const scriptPath of scriptPaths) {
        const content = fs.readFileSync(scriptPath, "utf8");
        expect(content).toMatch(/readdirSync\([\s\S]*?\}\s*catch\s*\(error\)\s*\{/);
        expect(content).toMatch(/Unable to read (directory|workflows directory)/);
      }
    });
  });
});

/**
 * Fixer/checker closure contract.
 *
 * THE failure class these tests lock out: a checker (check-eol.js) that can
 * FAIL on a state its paired auto-fixer (fix-eol.js) cannot reach a passing
 * state for. When the two diverge, the pre-commit auto-fix "Passes", the check
 * still "Fails", and the commit loops forever with no automated recovery.
 *
 * Two complementary proofs, both spawning the REAL scripts cross-platform via
 * `process.execPath` so the contract holds on Linux / macOS / Windows:
 *   1. CONTENT closure (no git): every dirty working-tree shape that check-eol
 *      can flag is cleared by one fix-eol pass.
 *   2. INDEX regression (real git): a staged file git classifies as binary
 *      (NUL bytes -> i/-text) is NOT flagged, and stays clean across a
 *      fix-eol + re-stage cycle. This is the exact case that broke the hook.
 */
describe("fix-eol -> check-eol closure contract", () => {
  const REPO_ROOT = path.resolve(__dirname, "..", "..");
  const FIX_EOL = path.join(REPO_ROOT, "scripts", "fix-eol.js");
  const CHECK_EOL = path.join(REPO_ROOT, "scripts", "check-eol.js");

  const tempDirs = [];

  // Create the fixture scratch dir INSIDE the repo under a benign, non-excluded
  // name -- NOT under os.tmpdir().
  //
  // WHY NOT os.tmpdir(): check-eol.js drops any target path that matches its
  // directory-exclusion list (.git, node_modules, Library, obj, Temp,
  // Samples~, .vs, .venv, .artifacts, site) in resolveTargets() BEFORE any
  // text-file collection. The `Temp` rule (/(^|[\/\\])Temp([\/\\]|$)/) is
  // case-SENSITIVE and matches the capitalized `Temp` segment that Windows
  // os.tmpdir() always carries ('C:\\Users\\<u>\\AppData\\Local\\Temp\\...').
  // So on the reporting Windows host EVERY fixture under os.tmpdir() is
  // excluded -> the checker prints "EOL check skipped" and exits 0, defeating
  // BOTH bare-name and absolute-path inputs (path-resolution and git toplevel
  // are irrelevant once the path is excluded). /tmp on Linux has no `Temp`
  // segment, which is why the bug only ever surfaced on Windows -- the same
  // platform asymmetry as the original failure.
  //
  // The repo's own working tree is by construction NOT under any excluded
  // segment, so a scratch dir directly beneath REPO_ROOT survives collection on
  // every platform. We PROVE the location is admissible below via the checker's
  // own isPathExcluded(), so the fixture placement can never silently regress
  // if the exclude list grows.
  //
  // ZERO-POLLUTION: the `dxm-eol-closure-*` prefix is in .gitignore, so an
  // interrupted run (SIGKILL / CI timeout / Ctrl-C between fixture creation and
  // afterAll) cannot leave an untracked dir at REPO_ROOT that would trip
  // validate-untracked-policy (a pre-commit + preflight gate) or a repo-wide
  // check-eol -- both the happy path and the crash window stay zero-manual-touch.
  // The gitignore prefix is NOT in check-eol's excludeRegexes (separate lists),
  // so the isPathExcluded() self-guard below still passes and the fixtures are
  // still collected.
  function makeTempDir() {
    const dir = fs.mkdtempSync(path.join(REPO_ROOT, "dxm-eol-closure-"));
    tempDirs.push(dir);
    // Self-guard: assert the scratch location is admissible to check-eol's
    // exclusion list (the SAME source of truth the script uses). If this ever
    // fires, the fixtures would be silently dropped and downstream
    // "must-fail"/"must-pass" assertions would go vacuous -- exactly the
    // Windows `Temp` failure mode. Fail loudly here instead.
    expect(isPathExcluded(dir)).toBe(false);
    return dir;
  }

  function runNode(scriptPath, args, cwd) {
    return spawnSync(process.execPath, [scriptPath, ...args], {
      cwd,
      encoding: "utf8"
    });
  }

  function gitAvailable() {
    const probe = spawnSync("git", ["--version"], { encoding: "utf8" });
    return !probe.error && probe.status === 0;
  }

  function git(cwd, args) {
    return spawnSync("git", args, { cwd, encoding: "utf8", env: { ...process.env } });
  }

  afterAll(() => {
    for (const dir of tempDirs) {
      try {
        fs.rmSync(dir, { recursive: true, force: true });
      } catch (_error) {
        // Best-effort cleanup; a leaked temp dir must never fail the suite.
      }
    }
  });

  test("one fix-eol pass clears every content shape check-eol can flag", () => {
    // No git needed: the fixtures live in an in-repo, non-excluded scratch dir
    // (see makeTempDir), so check-eol/fix-eol resolve and collect them on every
    // platform without any git-init recipe.
    const dir = makeTempDir();
    // Dirty fixtures spanning every content-level violation check-eol reports.
    // Written as raw bytes so the on-disk EOLs are exactly what we intend
    // (writeFileSync with a string would not introduce CRLF on its own, but
    // being explicit keeps the fixtures unambiguous across platforms).
    const fixtures = {
      // LF file requiring LF but stored CRLF -> violation.
      "crlf.js": Buffer.from("const a = 1;\r\nconst b = 2;\r\n", "utf8"),
      // BOM + CRLF -> BOM violation + EOL violation.
      "bom.json": Buffer.concat([
        Buffer.from([0xef, 0xbb, 0xbf]),
        Buffer.from('{\r\n  "a": 1\r\n}\r\n', "utf8")
      ]),
      // Bare CR (classic-Mac) -> violation for an LF file.
      "bare-cr.yaml": Buffer.from("a: 1\rb: 2\r", "utf8"),
      // Mixed CRLF + LF -> violation.
      "mixed.md": Buffer.from("# Title\r\n\nBody\r\n", "utf8"),
      // C#/.NET file requiring CRLF but stored LF -> violation.
      "Sample.cs": Buffer.from("class A\n{\n}\n", "utf8")
    };
    const names = Object.keys(fixtures);
    for (const name of names) {
      fs.writeFileSync(path.join(dir, name), fixtures[name]);
    }
    // Pass ABSOLUTE fixture paths: both scripts resolve targets via
    // path.resolve(repoRoot, rawTarget), and path.resolve(anyRoot, abs) === abs
    // on every platform, so absolute inputs are independent of whatever git
    // toplevel the host assigns to cwd -- matching the established absolute-path
    // convention in fix-csharp-underscore-methods.test.js and
    // run-staged-validators.test.js. (Combined with the in-repo, non-excluded
    // scratch dir, the fixtures are guaranteed to reach text-file collection.)
    const absPaths = names.map((name) => path.join(dir, name));

    // Sanity: the checker must actually flag the dirty corpus first, otherwise
    // the closure assertion below would be vacuously true.
    const before = runNode(CHECK_EOL, absPaths, dir);
    // Precondition guard against the Windows `Temp`-exclusion regression: prove
    // the fixtures were genuinely collected and inspected, not silently dropped.
    // A dropped/empty corpus prints "EOL check skipped" and exits 0; assert the
    // checker neither skipped nor passed, and that it named a real violation.
    expect(before.stdout || "").not.toMatch(/EOL check skipped/);
    expect(before.status).toBe(1);

    // One fixer pass.
    const fixed = runNode(FIX_EOL, absPaths, dir);
    expect(fixed.status).toBe(0);

    // Closure: the checker now passes on the fixer's output.
    const after = runNode(CHECK_EOL, absPaths, dir);
    expect(after.status).toBe(0);

    // Idempotence: a second fixer pass changes nothing.
    const refixed = runNode(FIX_EOL, absPaths, dir);
    expect(refixed.stdout).toMatch(/Updated 0\./);
  });

  test("binary-content (NUL) staged file is not flagged and survives fix + re-stage", () => {
    if (!gitAvailable()) {
      // git is a hard dependency of the hook itself; if it is somehow absent
      // the index-level contract cannot be exercised. Never fail on infra.
      return;
    }
    const dir = makeTempDir();
    const gitHere = (args) => git(dir, args);

    expect(gitHere(["init"]).status).toBe(0);
    gitHere(["config", "user.email", "test@example.com"]);
    gitHere(["config", "user.name", "Test"]);
    // Mirror the repo's default text policy so attributes resolve to text/eol=lf
    // exactly as in the real tree; git's content scan still classifies a
    // NUL-bearing blob as binary (i/-text) regardless.
    fs.writeFileSync(path.join(dir, ".gitattributes"), "* text=auto eol=lf\n");

    // A NUL-bearing .js fixture mirroring scripts/__tests__ files that assert on
    // NUL-delimited `git ... -z` output. LF-only endings: the ONLY thing wrong
    // (pre-fix) was git's binary classification, which is not an EOL fault.
    const nulFile = "nul-fixture.test.js";
    fs.writeFileSync(
      path.join(dir, nulFile),
      Buffer.from('const z = "A\u0000src/a.cs\u0000M\u0000b.cs\u0000";\n', "utf8")
    );
    gitHere(["add", ".gitattributes", nulFile]);

    // Precondition: git really does classify the staged blob as binary.
    const ls = gitHere(["ls-files", "--eol", "--", nulFile]);
    expect(ls.status).toBe(0);
    expect(ls.stdout).toMatch(/^i\/-text\b/);

    // The checker must PASS (the regression: it used to fail here).
    const checked = runNode(CHECK_EOL, [nulFile], dir);
    // Precondition: the fixture must have been collected (not dropped by an
    // exclusion segment), else status 0 would pass vacuously -- the same
    // Windows `Temp`-exclusion failure mode the closure test guards against.
    // The in-repo, non-excluded scratch dir (makeTempDir) ensures this on every
    // platform; assert it explicitly so a future scratch-location regression is
    // caught here too.
    expect(checked.stdout || "").not.toMatch(/EOL check skipped/);
    expect(checked.status).toBe(0);

    // Closure across the auto-fix + re-stage cycle the pre-commit hook performs.
    expect(runNode(FIX_EOL, [nulFile], dir).status).toBe(0);
    gitHere(["add", nulFile]);
    const recheck = runNode(CHECK_EOL, [nulFile], dir);
    expect(recheck.status).toBe(0);
  });
});
