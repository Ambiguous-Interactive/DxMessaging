/**
 * @fileoverview Tests for sync-banner-version.ps1 logic.
 *
 * These tests validate the core logic that the PowerShell script implements:
 * - Version extraction from package.json
 * - Pattern matching and replacement in the SVG
 * - Edge cases for error handling
 *
 * Since the actual script is PowerShell, we test the equivalent JavaScript
 * implementations of the core logic to ensure correctness.
 */

"use strict";

const fs = require("fs");
const path = require("path");

// SYNC: Keep pattern in sync with sync-banner-version.ps1 $versionPattern
// The pattern anchors to the Version badge comment to avoid matching other version-like text
//
// ESCAPING DIFFERENCES between JavaScript and PowerShell:
// - JavaScript regex literals require escaping forward slashes as \/ (e.g., <\/text>, <\/g>)
// - PowerShell regex strings do NOT need forward slash escaping (uses </text>, </g> directly)
// - Both use \s* for whitespace, \d+ for digits, and [^>]* for negated character classes
// - The logical pattern is identical; only the escape syntax differs
//
// PowerShell pattern:
//   '<!-- Version badge \(top right\).*?-->\s*<g[^>]*>\s*<rect[^>]*/>\s*<text[^>]*>v\d+\.\d+\.\d+[^<]*</text>\s*</g>'
//
// JavaScript pattern (below) - note the \/ escapes for forward slashes:
const VERSION_PATTERN =
  /<!-- Version badge \(top right\).*?-->\s*<g[^>]*>\s*<rect[^>]*\/>\s*<text[^>]*>v\d+\.\d+\.\d+[^<]*<\/text>\s*<\/g>/s;

// SYNC: Keep semver pattern in sync with sync-banner-version.ps1 version validation
const SEMVER_PATTERN = /^\d+\.\d+\.\d+/;

// SYNC: Keep test-count label pattern in sync with sync-banner-version.ps1 $testCountPattern
const TEST_COUNT_PATTERN =
  /(<text(?=[^>]*\bx="20")(?=[^>]*\by="13")(?=[^>]*\bfill="#00d9ff")[^>]*>)(\d+\+ Tests)(<\/text>)/;

const TEST_FILE_NAME_PATTERN = /(?:Test|Tests)\.cs$|\.(?:test|spec)\.js$/;

/**
 * Extracts version from a package.json content string.
 * @param {string} content - The package.json file content
 * @returns {string|null} The version string or null if not found/invalid
 */
function extractVersion(content) {
  try {
    const packageJson = JSON.parse(content);
    const version = packageJson.version;
    if (!version || typeof version !== "string") {
      return null;
    }
    if (!SEMVER_PATTERN.test(version)) {
      return null;
    }
    return version;
  } catch {
    return null;
  }
}

/**
 * Generates the replacement SVG content for the version badge.
 * @param {string} version - The version string (e.g., "2.1.4")
 * @returns {string} The SVG group element for the version badge
 */
function generateVersionBadge(version) {
  return `<!-- Version badge (top right) - text must contain vX.Y.Z for version sync -->
  <g transform="translate(720, 18)">
    <rect x="0" y="0" width="62" height="22" rx="11" ry="11" fill="#e94560" opacity="0.95" filter="url(#softShadow)"/>
    <text x="31" y="15" text-anchor="middle" font-family="'SF Mono', 'Fira Code', monospace" font-size="11" font-weight="700" fill="#ffffff" letter-spacing="0.5">v${version}</text>
  </g>`;
}

/**
 * Extracts the current version from SVG content.
 * @param {string} svgContent - The SVG file content
 * @returns {string|null} The current version or null if not found
 */
function extractCurrentVersion(svgContent) {
  const match = svgContent.match(VERSION_PATTERN);
  if (!match) {
    return null;
  }
  const versionMatch = match[0].match(/>v(\d+\.\d+\.\d+[^<]*)<\/text>/);
  if (!versionMatch) {
    return null;
  }
  return versionMatch[1];
}

/**
 * Updates the version in SVG content.
 * @param {string} svgContent - The SVG file content
 * @param {string} newVersion - The new version string
 * @returns {string|null} Updated SVG content or null if pattern not found
 */
function updateSvgVersion(svgContent, newVersion) {
  if (!VERSION_PATTERN.test(svgContent)) {
    return null;
  }
  return svgContent.replace(VERSION_PATTERN, generateVersionBadge(newVersion));
}

function stripSourceComments(content) {
  return content.replace(/\/\*[\s\S]*?\*\//g, "").replace(/(^|[^:])\/\/.*$/gm, "$1");
}

function maskJavaScriptNonCode(content) {
  let result = "";
  let state = "code";
  let quote = "";
  let escaped = false;

  const mask = (char) => (char === "\n" || char === "\r" ? char : " ");

  for (let i = 0; i < content.length; ) {
    const char = content[i];
    const next = content[i + 1] ?? "";

    if (state === "code") {
      if (char === "/" && next === "/") {
        result += mask(char) + mask(next);
        i += 2;
        state = "line-comment";
        continue;
      }
      if (char === "/" && next === "*") {
        result += mask(char) + mask(next);
        i += 2;
        state = "block-comment";
        continue;
      }
      if (char === "'" || char === '"' || char === "`") {
        result += mask(char);
        quote = char;
        escaped = false;
        i++;
        state = "string";
        continue;
      }

      result += char;
      i++;
      continue;
    }

    if (state === "line-comment") {
      result += mask(char);
      i++;
      if (char === "\n" || char === "\r") {
        state = "code";
      }
      continue;
    }

    if (state === "block-comment") {
      result += mask(char);
      if (char === "*" && next === "/") {
        result += mask(next);
        i += 2;
        state = "code";
        continue;
      }
      i++;
      continue;
    }

    result += mask(char);
    i++;
    if (escaped) {
      escaped = false;
      continue;
    }
    if (char === "\\") {
      escaped = true;
      continue;
    }
    if (char === quote) {
      state = "code";
    }
  }

  return result;
}

function countTestMarkers(filePath, content) {
  if (filePath.endsWith(".cs")) {
    const source = stripSourceComments(content);
    return (source.match(/\[(?:UnityTest|Test|TestCase|TestCaseSource|Theory|Fact)\b/g) ?? [])
      .length;
  }
  if (/\.(?:test|spec)\.js$/.test(filePath)) {
    const source = maskJavaScriptNonCode(content);
    return (source.match(/(?<![\w.])(?:test|it)\s*\(/g) ?? []).length;
  }
  return 0;
}

function roundTestCount(testCount) {
  const rounded = Math.floor(testCount / 100) * 100;
  return rounded < 1 ? testCount : rounded;
}

function updateSvgTestCount(svgContent, testCount) {
  if (!TEST_COUNT_PATTERN.test(svgContent)) {
    return null;
  }
  return svgContent.replace(TEST_COUNT_PATTERN, `$1${roundTestCount(testCount)}+ Tests$3`);
}

function getRepositoryTestFiles(root) {
  const results = [];
  const roots = ["Tests", "SourceGenerators", "scripts"];
  for (const relativeRoot of roots) {
    const absoluteRoot = path.join(root, relativeRoot);
    if (!fs.existsSync(absoluteRoot)) {
      continue;
    }
    collectTestFiles(absoluteRoot, results);
  }
  return results;
}

function collectTestFiles(dir, results) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      collectTestFiles(fullPath, results);
      continue;
    }
    if (entry.isFile() && TEST_FILE_NAME_PATTERN.test(entry.name)) {
      results.push(fullPath);
    }
  }
}

function calculateRepositoryTestCount(root) {
  return getRepositoryTestFiles(root).reduce(
    (sum, filePath) => sum + countTestMarkers(filePath, fs.readFileSync(filePath, "utf-8")),
    0
  );
}

describe("sync-banner-version", () => {
  describe("extractVersion", () => {
    test("should extract a valid semver version from package.json", () => {
      const content = JSON.stringify({ name: "test", version: "1.2.3" });
      expect(extractVersion(content)).toBe("1.2.3");
    });

    test("should extract version with pre-release suffix", () => {
      const content = JSON.stringify({ name: "test", version: "1.2.3-beta.1" });
      expect(extractVersion(content)).toBe("1.2.3-beta.1");
    });

    test("should extract version with build metadata", () => {
      const content = JSON.stringify({ name: "test", version: "1.2.3+build.456" });
      expect(extractVersion(content)).toBe("1.2.3+build.456");
    });

    test("should return null for missing version field", () => {
      const content = JSON.stringify({ name: "test" });
      expect(extractVersion(content)).toBeNull();
    });

    test("should return null for empty version", () => {
      const content = JSON.stringify({ name: "test", version: "" });
      expect(extractVersion(content)).toBeNull();
    });

    test("should return null for non-string version", () => {
      const content = JSON.stringify({ name: "test", version: 123 });
      expect(extractVersion(content)).toBeNull();
    });

    test("should return null for invalid semver format", () => {
      const content = JSON.stringify({ name: "test", version: "1.2" });
      expect(extractVersion(content)).toBeNull();
    });

    test("should return null for malformed JSON", () => {
      const content = "{ invalid json }";
      expect(extractVersion(content)).toBeNull();
    });

    test("should return null for empty content", () => {
      const content = "";
      expect(extractVersion(content)).toBeNull();
    });

    test("should handle version with leading zeros in segments", () => {
      const content = JSON.stringify({ name: "test", version: "01.02.03" });
      // Leading zeros are technically valid in the regex pattern
      expect(extractVersion(content)).toBe("01.02.03");
    });

    test("should handle very large version numbers", () => {
      const content = JSON.stringify({ name: "test", version: "999.999.999" });
      expect(extractVersion(content)).toBe("999.999.999");
    });
  });

  describe("generateVersionBadge", () => {
    test("should generate correct SVG badge for a version", () => {
      const badge = generateVersionBadge("2.1.4");
      expect(badge).toContain("v2.1.4");
      expect(badge).toContain("<!-- Version badge (top right)");
      expect(badge).toContain('<g transform="translate(720, 18)">');
      expect(badge).toContain("</text>");
      expect(badge).toContain("</g>");
    });

    test("should handle pre-release versions", () => {
      const badge = generateVersionBadge("3.0.0-alpha.1");
      expect(badge).toContain("v3.0.0-alpha.1");
    });

    test("should include all required SVG elements", () => {
      const badge = generateVersionBadge("1.0.0");
      expect(badge).toMatch(/<rect[^>]*\/>/);
      expect(badge).toMatch(/<text[^>]*>v1\.0\.0<\/text>/);
    });
  });

  describe("extractCurrentVersion", () => {
    const validSvgContent = `<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 200">
  <!-- Some other content -->
  <!-- Version badge (top right) - text must contain vX.Y.Z for version sync -->
  <g transform="translate(720, 18)">
    <rect x="0" y="0" width="62" height="22" rx="11" ry="11" fill="#e94560" opacity="0.95" filter="url(#softShadow)"/>
    <text x="31" y="15" text-anchor="middle" font-family="'SF Mono', 'Fira Code', monospace" font-size="11" font-weight="700" fill="#ffffff" letter-spacing="0.5">v2.1.4</text>
  </g>
  <!-- More content -->
</svg>`;

    test("should extract version from valid SVG", () => {
      expect(extractCurrentVersion(validSvgContent)).toBe("2.1.4");
    });

    test("should return null for SVG without version badge", () => {
      const svgWithoutBadge = `<?xml version="1.0"?>
<svg xmlns="http://www.w3.org/2000/svg">
  <rect width="100" height="100"/>
</svg>`;
      expect(extractCurrentVersion(svgWithoutBadge)).toBeNull();
    });

    test("should return null for empty content", () => {
      expect(extractCurrentVersion("")).toBeNull();
    });

    test("should return null for malformed version badge", () => {
      const malformedSvg = `<!-- Version badge (top right) -->
  <g transform="translate(720, 18)">
    <rect x="0" y="0" width="62" height="22"/>
    <text>no version here</text>
  </g>`;
      expect(extractCurrentVersion(malformedSvg)).toBeNull();
    });

    test("should handle version with pre-release suffix", () => {
      const svgWithPrerelease = validSvgContent.replace("v2.1.4", "v3.0.0-beta.2");
      expect(extractCurrentVersion(svgWithPrerelease)).toBe("3.0.0-beta.2");
    });
  });

  describe("updateSvgVersion", () => {
    const baseSvgContent = `<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 200">
  <defs>
    <filter id="softShadow">
      <feDropShadow dx="0" dy="1" stdDeviation="2" flood-opacity="0.3"/>
    </filter>
  </defs>
  <!-- Some header content -->
  <!-- Version badge (top right) - text must contain vX.Y.Z for version sync -->
  <g transform="translate(720, 18)">
    <rect x="0" y="0" width="62" height="22" rx="11" ry="11" fill="#e94560" opacity="0.95" filter="url(#softShadow)"/>
    <text x="31" y="15" text-anchor="middle" font-family="'SF Mono', 'Fira Code', monospace" font-size="11" font-weight="700" fill="#ffffff" letter-spacing="0.5">v1.0.0</text>
  </g>
  <!-- Footer content -->
</svg>`;

    test("should update version in SVG content", () => {
      const updated = updateSvgVersion(baseSvgContent, "2.0.0");
      expect(updated).not.toBeNull();
      expect(updated).toContain("v2.0.0");
      expect(updated).not.toContain("v1.0.0");
    });

    test("should preserve other SVG content", () => {
      const updated = updateSvgVersion(baseSvgContent, "2.0.0");
      expect(updated).toContain('viewBox="0 0 800 200"');
      expect(updated).toContain('<filter id="softShadow">');
      expect(updated).toContain("<!-- Some header content -->");
      expect(updated).toContain("<!-- Footer content -->");
    });

    test("should return null for SVG without version pattern", () => {
      const svgWithoutPattern = `<?xml version="1.0"?>
<svg><rect width="100" height="100"/></svg>`;
      expect(updateSvgVersion(svgWithoutPattern, "2.0.0")).toBeNull();
    });

    test("should handle upgrade from major version", () => {
      const updated = updateSvgVersion(baseSvgContent, "10.0.0");
      expect(updated).toContain("v10.0.0");
    });

    test("should handle pre-release version update", () => {
      const updated = updateSvgVersion(baseSvgContent, "2.0.0-rc.1");
      expect(updated).toContain("v2.0.0-rc.1");
    });
  });

  describe("test count calculation", () => {
    test("should count NUnit and Unity test attributes in C# test files", () => {
      const content = `
public class ExampleTests
{
    [Test]
    public void One() {}

    [UnityTest]
    public IEnumerator Two() { yield break; }

    [TestCase(1)]
    [TestCaseSource(nameof(Cases))]
    public void Parameterized(int value) {}
}`;
      expect(countTestMarkers("Tests/ExampleTests.cs", content)).toBe(4);
    });

    test("should count Jest test and it calls in JavaScript test files", () => {
      const content = `
describe("suite", () => {
    test("one", () => {});
    it("two", () => {});
    helper.test("not a test declaration");
});`;
      expect(countTestMarkers("scripts/__tests__/example.test.js", content)).toBe(2);
    });

    test("should ignore Jest markers inside JavaScript fixture strings", () => {
      const content = `
const source = "test('fixture', () => {}); it('also fixture', () => {});";
const quoted = 'literal test( and it( text';
const template = \`template test("not real", () => {})\`;
describe("suite", () => {
    test("real", () => {});
    it("also real", () => {});
});`;
      expect(countTestMarkers("scripts/__tests__/example.test.js", content)).toBe(2);
    });

    test("should ignore test markers inside comments", () => {
      const content = `
// [Test]
/* test("commented", () => {}); */
[Test]
public void RealTest() {}`;
      expect(countTestMarkers("Tests/ExampleTests.cs", content)).toBe(1);
    });

    test("should round test counts down to stable hundred-count labels", () => {
      expect(roundTestCount(2305)).toBe(2300);
      expect(roundTestCount(99)).toBe(99);
    });

    test("should update the feature-row test count label", () => {
      const svg = `<svg>
  <g transform="translate(120, 150)">
    <text x="20" y="13" fill="#00d9ff">300+ Tests</text>
  </g>
</svg>`;
      const updated = updateSvgTestCount(svg, 2305);
      expect(updated).toContain("2300+ Tests");
      expect(updated).toContain('fill="#00d9ff">2300+ Tests</text>');
    });

    test("should return null when the feature-row test count label is missing", () => {
      expect(updateSvgTestCount("<svg></svg>", 2305)).toBeNull();
    });
  });

  describe("VERSION_PATTERN regex", () => {
    test("should match standard version badge format", () => {
      const badge = `<!-- Version badge (top right) - some comment -->
  <g transform="translate(720, 18)">
    <rect x="0" y="0" width="62" height="22" rx="11" fill="#e94560"/>
    <text x="31" y="15">v1.2.3</text>
  </g>`;
      expect(VERSION_PATTERN.test(badge)).toBe(true);
    });

    test("should match with various whitespace", () => {
      const badge = `<!-- Version badge (top right) -->   
<g>
<rect/>
<text>v1.0.0</text>
</g>`;
      expect(VERSION_PATTERN.test(badge)).toBe(true);
    });

    test("should not match without version comment", () => {
      const noComment = `<g transform="translate(720, 18)">
    <rect x="0" y="-12"/>
    <text>v1.2.3</text>
  </g>`;
      expect(VERSION_PATTERN.test(noComment)).toBe(false);
    });

    test("should not match version text outside the badge structure", () => {
      const standalone = `<text>v1.2.3</text>`;
      expect(VERSION_PATTERN.test(standalone)).toBe(false);
    });

    test("should match version with pre-release suffix", () => {
      const badge = `<!-- Version badge (top right) -->
  <g>
    <rect/>
    <text>v2.0.0-alpha.5</text>
  </g>`;
      expect(VERSION_PATTERN.test(badge)).toBe(true);
    });
  });

  describe("SEMVER_PATTERN regex", () => {
    test("should match basic semver", () => {
      expect(SEMVER_PATTERN.test("1.2.3")).toBe(true);
    });

    test("should match semver with pre-release", () => {
      expect(SEMVER_PATTERN.test("1.2.3-beta.1")).toBe(true);
    });

    test("should match semver with build metadata", () => {
      expect(SEMVER_PATTERN.test("1.2.3+build.123")).toBe(true);
    });

    test("should not match incomplete version", () => {
      expect(SEMVER_PATTERN.test("1.2")).toBe(false);
    });

    test("should not match non-numeric version", () => {
      expect(SEMVER_PATTERN.test("a.b.c")).toBe(false);
    });

    test("should not match empty string", () => {
      expect(SEMVER_PATTERN.test("")).toBe(false);
    });
  });

  describe("integration with actual files", () => {
    const repoRoot = path.resolve(__dirname, "../..");
    const packageJsonPath = path.join(repoRoot, "package.json");
    const svgPath = path.join(repoRoot, "docs", "images", "DxMessaging-banner.svg");

    test("should be able to read version from actual package.json", () => {
      const exists = fs.existsSync(packageJsonPath);
      expect(exists).toBe(true);

      if (exists) {
        const content = fs.readFileSync(packageJsonPath, "utf-8");
        const version = extractVersion(content);
        expect(version).not.toBeNull();
        expect(version).toMatch(SEMVER_PATTERN);
      }
    });

    test("should be able to find version pattern in actual SVG", () => {
      const exists = fs.existsSync(svgPath);
      expect(exists).toBe(true);

      if (exists) {
        const content = fs.readFileSync(svgPath, "utf-8");
        const version = extractCurrentVersion(content);
        expect(version).not.toBeNull();
      }
    });

    test("should have matching versions between package.json and SVG", () => {
      const packageExists = fs.existsSync(packageJsonPath);
      const svgExists = fs.existsSync(svgPath);

      if (packageExists && svgExists) {
        const packageContent = fs.readFileSync(packageJsonPath, "utf-8");
        const svgContent = fs.readFileSync(svgPath, "utf-8");

        const packageVersion = extractVersion(packageContent);
        const svgVersion = extractCurrentVersion(svgContent);

        // Explicit null assertions before comparison
        expect(packageVersion).not.toBeNull();
        expect(svgVersion).not.toBeNull();

        // This test documents the expected synchronized state
        // If this fails, the banner needs to be synced
        expect(svgVersion).toBe(packageVersion);
      }
    });

    test("should have matching rounded test count between repository tests and SVG", () => {
      const svgExists = fs.existsSync(svgPath);
      expect(svgExists).toBe(true);

      if (svgExists) {
        const svgContent = fs.readFileSync(svgPath, "utf-8");
        const labelMatch = svgContent.match(TEST_COUNT_PATTERN);
        const repositoryTestCount = calculateRepositoryTestCount(repoRoot);
        const expectedLabel = `${roundTestCount(repositoryTestCount)}+ Tests`;

        expect(repositoryTestCount).toBeGreaterThan(0);
        expect(labelMatch).not.toBeNull();
        expect(labelMatch[2]).toBe(expectedLabel);
      }
    });
  });

  describe("edge cases", () => {
    test("should handle SVG with multiple g elements", () => {
      const multipleGs = `<?xml version="1.0"?>
<svg>
  <g id="first"><rect/></g>
  <!-- Version badge (top right) -->
  <g transform="translate(720, 18)">
    <rect x="0"/>
    <text>v1.0.0</text>
  </g>
  <g id="last"><rect/></g>
</svg>`;
      const version = extractCurrentVersion(multipleGs);
      expect(version).toBe("1.0.0");
    });

    test("should handle SVG with nested groups", () => {
      const nested = `<?xml version="1.0"?>
<svg>
  <g id="outer">
    <!-- Version badge (top right) -->
    <g transform="translate(720, 18)">
      <rect/>
      <text>v2.5.0</text>
    </g>
  </g>
</svg>`;
      const version = extractCurrentVersion(nested);
      expect(version).toBe("2.5.0");
    });

    test("should handle version at end of text with trailing content", () => {
      const badge = `<!-- Version badge (top right) -->
  <g>
    <rect/>
    <text>v1.0.0-rc.1+build.123</text>
  </g>`;
      expect(VERSION_PATTERN.test(badge)).toBe(true);
    });

    test("should handle package.json with extra fields", () => {
      const content = JSON.stringify({
        name: "test",
        version: "1.0.0",
        description: "A test package",
        dependencies: { lodash: "^4.0.0" },
        devDependencies: {},
        scripts: { test: "jest" }
      });
      expect(extractVersion(content)).toBe("1.0.0");
    });

    test("should handle unicode in package.json", () => {
      const content = JSON.stringify({
        name: "test-émoji-📦",
        version: "1.0.0",
        description: "A test with unicode 🎉"
      });
      expect(extractVersion(content)).toBe("1.0.0");
    });

    test("should handle very long version numbers", () => {
      const content = JSON.stringify({ name: "test", version: "1000.2000.3000" });
      expect(extractVersion(content)).toBe("1000.2000.3000");
    });

    test("should handle version with only pre-release (no build metadata)", () => {
      const content = JSON.stringify({ name: "test", version: "1.0.0-alpha.1" });
      expect(extractVersion(content)).toBe("1.0.0-alpha.1");
    });

    test("should handle version with only build metadata (no pre-release)", () => {
      const content = JSON.stringify({ name: "test", version: "1.0.0+20260128" });
      expect(extractVersion(content)).toBe("1.0.0+20260128");
    });
  });

  describe("line ending handling", () => {
    test("should handle SVG content with LF line endings", () => {
      const svgWithLf = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 200">',
        "  <!-- Version badge (top right) - text must contain vX.Y.Z for version sync -->",
        '  <g transform="translate(720, 18)">',
        '    <rect x="0" y="0" width="62" height="22" rx="11" fill="#e94560"/>',
        '    <text x="31" y="15">v1.2.3</text>',
        "  </g>",
        "</svg>"
      ].join("\n");
      expect(VERSION_PATTERN.test(svgWithLf)).toBe(true);
      expect(extractCurrentVersion(svgWithLf)).toBe("1.2.3");
    });

    test("should handle SVG content with CRLF line endings", () => {
      const svgWithCrlf = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 200">',
        "  <!-- Version badge (top right) - text must contain vX.Y.Z for version sync -->",
        '  <g transform="translate(720, 18)">',
        '    <rect x="0" y="0" width="62" height="22" rx="11" fill="#e94560"/>',
        '    <text x="31" y="15">v2.0.0</text>',
        "  </g>",
        "</svg>"
      ].join("\r\n");
      expect(VERSION_PATTERN.test(svgWithCrlf)).toBe(true);
      expect(extractCurrentVersion(svgWithCrlf)).toBe("2.0.0");
    });

    test("should update SVG and preserve LF line endings in replacement", () => {
      const svgWithLf = [
        '<?xml version="1.0"?>',
        "<svg>",
        "  <!-- Version badge (top right) -->",
        "  <g><rect/><text>v1.0.0</text></g>",
        "</svg>"
      ].join("\n");
      const updated = updateSvgVersion(svgWithLf, "3.0.0");
      expect(updated).not.toBeNull();
      expect(updated).toContain("v3.0.0");
      // The replacement badge uses \n internally
      expect(updated).toContain("\n");
    });

    test("should update SVG with CRLF line endings", () => {
      const svgWithCrlf = [
        '<?xml version="1.0"?>',
        "<svg>",
        "  <!-- Version badge (top right) -->",
        "  <g><rect/><text>v1.0.0</text></g>",
        "</svg>"
      ].join("\r\n");
      const updated = updateSvgVersion(svgWithCrlf, "4.0.0");
      expect(updated).not.toBeNull();
      expect(updated).toContain("v4.0.0");
    });

    test("should handle SVG with mixed line endings", () => {
      // Edge case: file with inconsistent line endings
      const mixedSvg =
        '<?xml version="1.0"?>\r\n' +
        "<svg>\n" +
        "  <!-- Version badge (top right) -->\r\n" +
        "  <g><rect/><text>v1.5.0</text></g>\n" +
        "</svg>";
      expect(VERSION_PATTERN.test(mixedSvg)).toBe(true);
      expect(extractCurrentVersion(mixedSvg)).toBe("1.5.0");
    });
  });

  describe("additional edge cases", () => {
    test("should handle very long version numbers in SVG", () => {
      const badge = `<!-- Version badge (top right) -->
  <g>
    <rect/>
    <text>v1000.2000.3000</text>
  </g>`;
      expect(VERSION_PATTERN.test(badge)).toBe(true);
    });

    test("should update to very long version numbers", () => {
      const baseSvg = `<?xml version="1.0"?>
<svg>
  <!-- Version badge (top right) -->
  <g><rect/><text>v1.0.0</text></g>
</svg>`;
      const updated = updateSvgVersion(baseSvg, "1000.2000.3000");
      expect(updated).not.toBeNull();
      expect(updated).toContain("v1000.2000.3000");
    });

    test("should handle version with only pre-release in SVG", () => {
      const badge = `<!-- Version badge (top right) -->
  <g>
    <rect/>
    <text>v2.0.0-beta.5</text>
  </g>`;
      expect(VERSION_PATTERN.test(badge)).toBe(true);
      const svgContent = `<svg>${badge}</svg>`;
      // extractCurrentVersion expects the full badge structure
      expect(extractCurrentVersion(`<svg>${badge}</svg>`)).toBe("2.0.0-beta.5");
    });

    test("should handle version with only build metadata in SVG", () => {
      const badge = `<!-- Version badge (top right) -->
  <g>
    <rect/>
    <text>v3.0.0+build.789</text>
  </g>`;
      expect(VERSION_PATTERN.test(badge)).toBe(true);
      expect(extractCurrentVersion(`<svg>${badge}</svg>`)).toBe("3.0.0+build.789");
    });

    test("should update to version with only pre-release", () => {
      const baseSvg = `<?xml version="1.0"?>
<svg>
  <!-- Version badge (top right) -->
  <g><rect/><text>v1.0.0</text></g>
</svg>`;
      const updated = updateSvgVersion(baseSvg, "2.0.0-rc.1");
      expect(updated).not.toBeNull();
      expect(updated).toContain("v2.0.0-rc.1");
    });

    test("should update to version with only build metadata", () => {
      const baseSvg = `<?xml version="1.0"?>
<svg>
  <!-- Version badge (top right) -->
  <g><rect/><text>v1.0.0</text></g>
</svg>`;
      const updated = updateSvgVersion(baseSvg, "2.0.0+sha.abc123");
      expect(updated).not.toBeNull();
      expect(updated).toContain("v2.0.0+sha.abc123");
    });
  });
});
