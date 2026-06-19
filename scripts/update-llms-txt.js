#!/usr/bin/env node

/**
 * Update llms.txt with current project information
 *
 * This script generates/updates llms.txt by combining:
 * - package.json metadata
 * - .llm/skills/ directory statistics
 * - the curated llms.txt template in this script
 *
 * Usage:
 *   node scripts/update-llms-txt.js [--check]
 *
 * Options:
 *   --check  Verify llms.txt is up-to-date without modifying it
 *
 * Exit codes:
 *   0 - Success (file updated or already current)
 *   1 - Error or --check found differences
 */

const fs = require("fs");
const path = require("path");
const { normalizeToLf } = require("./lib/line-endings");
const { isPathOutsideDirectory } = require("./lib/path-classifier");
const { walkFiles } = require("./lib/repo-files");

const ROOT_DIR = path.resolve(__dirname, "..");
const LLMS_TXT_PATH = path.join(ROOT_DIR, "llms.txt");
const README_PATH = path.join(ROOT_DIR, "README.md");
const PACKAGE_JSON_PATH = path.join(ROOT_DIR, "package.json");
// DX_SKILLS_DIR, like DX_LLMS_TXT / DX_README (guardedClaimFiles), exists only so
// the CLI exit-code paths are testable against a temp fixture -- set them in the
// child process's env for CLI tests; production runs always use the repo defaults.
const LLM_SKILLS_DIR = process.env.DX_SKILLS_DIR || path.join(ROOT_DIR, ".llm", "skills");
const NON_SKILL_FILES = new Set(["index.md", "specification.md"]);
const NON_SKILL_DIRECTORIES = new Set(["templates"]);

// The "N+ specialized skill documents" claim is duplicated across human-facing
// docs (llms.txt, README.md). It is a floored "at least N" promise, so the only
// way it can be wrong is to overstate; understating is conservatively true. We
// therefore normalize the number out of structural comparison (like the date)
// and separately assert no claim overstates the real count. That kills the
// entire "forgot to bump the skill count" failure class while still catching
// genuinely false claims. See validateSkillCountClaim.
const SKILL_CLAIM_REGEX = /(\d+)\+ specialized skill documents/g;

// Docs whose skill-count claim is guarded (missing files are skipped). Resolved
// per run so the CLI exit-code paths stay testable against a temp fixture via the
// DX_LLMS_TXT / DX_README env overrides; production runs use the repo defaults.
function guardedClaimFiles() {
  return [
    { label: "llms.txt", filePath: process.env.DX_LLMS_TXT || LLMS_TXT_PATH },
    { label: "README.md", filePath: process.env.DX_README || README_PATH }
  ];
}

function isCountedSkillPath(fullPath) {
  const relativePath = path.relative(LLM_SKILLS_DIR, fullPath).split(path.sep).join("/");

  // Cross-drive-safe containment (scripts/lib/path-classifier.js): a bare
  // `relativePath.startsWith("../")` misses the absolute target that
  // path.relative returns across Windows drives. `!relativePath` also rules out
  // fullPath === LLM_SKILLS_DIR (the dir itself is never a counted skill file).
  if (!relativePath || isPathOutsideDirectory(fullPath, LLM_SKILLS_DIR)) {
    return false;
  }

  const pathSegments = relativePath.split("/");
  const fileName = pathSegments[pathSegments.length - 1];

  if (!fileName.endsWith(".md")) {
    return false;
  }

  if (NON_SKILL_FILES.has(fileName)) {
    return false;
  }

  return !pathSegments.some((segment) => NON_SKILL_DIRECTORIES.has(segment));
}

function countSkillFiles() {
  if (!fs.existsSync(LLM_SKILLS_DIR)) {
    return 0;
  }

  return walkFiles(LLM_SKILLS_DIR, {
    excludeDir: (fullPath, entry) => NON_SKILL_DIRECTORIES.has(entry.name),
    match: (fullPath) => isCountedSkillPath(fullPath),
    onError: (error, dir) =>
      console.warn(`Warning: Unable to read directory ${dir}: ${error.message}`)
  }).length;
}

/**
 * Extract every "N+ specialized skill documents" claim from content as integers,
 * in document order.
 */
function parseSkillCountClaims(content) {
  return [...normalizeToLf(content).matchAll(SKILL_CLAIM_REGEX)].map((match) =>
    Number.parseInt(match[1], 10)
  );
}

/**
 * Validate a document's skill-count claim against the real number of skill
 * files. The claim is a floored "at least N" promise, so understating is
 * allowed (conservative) and only overstating is an error. Requires exactly one
 * claim so a doc cannot silently drop or duplicate it.
 *
 * @returns {{ ok: boolean, claimed: number | null, reason?: string }}
 */
function validateSkillCountClaim(content, actualCount, label) {
  const claims = parseSkillCountClaims(content);

  if (claims.length !== 1) {
    return {
      ok: false,
      claimed: null,
      reason: `${label}: expected exactly one "N+ specialized skill documents" claim, found ${claims.length}`
    };
  }

  const claimed = claims[0];
  if (!Number.isInteger(claimed) || claimed < 1) {
    return { ok: false, claimed, reason: `${label}: skill-count claim must be a positive integer` };
  }

  if (claimed > actualCount) {
    return {
      ok: false,
      claimed,
      reason: `${label}: claims ${claimed}+ specialized skill documents but only ${actualCount} exist (overstated). Run: node scripts/update-llms-txt.js`
    };
  }

  return { ok: true, claimed };
}

/**
 * Rewrite a file's single "N+ specialized skill documents" claim to the exact
 * current count. No-op (returns false) when the file is absent, has no claim, or
 * already matches. Refuses to touch a file with multiple claims so an ambiguous
 * edit can never mangle prose.
 *
 * @returns {boolean} true when the file was rewritten.
 */
function syncSkillCountClaim(filePath, actualCount) {
  if (!fs.existsSync(filePath)) {
    return false;
  }

  const original = fs.readFileSync(filePath, "utf8");
  if (parseSkillCountClaims(original).length !== 1) {
    return false;
  }

  const updated = original.replace(
    SKILL_CLAIM_REGEX,
    `${actualCount}+ specialized skill documents`
  );
  if (updated === original) {
    return false;
  }

  fs.writeFileSync(filePath, updated, "utf8");
  return true;
}

/**
 * Produce a short human-readable summary of the first few line-level
 * differences between two already-normalized strings. Used to make --check
 * failures actionable instead of a bare "out of date".
 */
function summarizeDrift(expectedNormalized, actualNormalized, maxLines = 5) {
  const expected = expectedNormalized.split("\n");
  const actual = actualNormalized.split("\n");
  const diffs = [];

  for (let i = 0; i < Math.max(expected.length, actual.length); i++) {
    if (expected[i] !== actual[i]) {
      diffs.push(`  line ${i + 1}:`);
      diffs.push(`    committed:  ${JSON.stringify(expected[i] ?? "<missing>")}`);
      diffs.push(`    generated:  ${JSON.stringify(actual[i] ?? "<missing>")}`);
      if (diffs.length >= maxLines * 3) {
        diffs.push(`  ... (showing first ${maxLines} differing lines)`);
        break;
      }
    }
  }

  return diffs.join("\n");
}

/**
 * Validate that content has exactly one "**Last Updated:**" line
 * and that it contains a non-empty ISO date (YYYY-MM-DD).
 */
function hasValidLastUpdatedLine(content) {
  const lines = normalizeToLf(content).split("\n");
  const lastUpdatedLines = lines.filter((line) => line.startsWith("**Last Updated:**"));

  if (lastUpdatedLines.length !== 1) {
    return false;
  }

  const line = lastUpdatedLines[0];
  // Require an ISO date after the label, e.g. "**Last Updated:** 2024-01-31"
  const isoDatePattern = /^\*\*Last Updated:\*\*\s+\d{4}-\d{2}-\d{2}\s*$/;
  return isoDatePattern.test(line);
}

function getSkillCategories() {
  const categories = [];

  if (fs.existsSync(LLM_SKILLS_DIR)) {
    let entries;
    try {
      entries = fs.readdirSync(LLM_SKILLS_DIR, { withFileTypes: true });
    } catch (error) {
      console.warn(`Warning: Unable to read directory ${LLM_SKILLS_DIR}: ${error.message}`);
      return categories;
    }
    for (const entry of entries) {
      if (entry.isDirectory() && !NON_SKILL_DIRECTORIES.has(entry.name)) {
        categories.push(entry.name);
      }
    }
  }

  return categories.sort();
}

function getPackageInfo() {
  const pkg = JSON.parse(fs.readFileSync(PACKAGE_JSON_PATH, "utf8"));
  return {
    version: pkg.version,
    description: pkg.description,
    name: pkg.name,
    displayName: pkg.displayName
  };
}

function generateLlmsTxt() {
  const pkg = getPackageInfo();
  const skillCount = countSkillFiles();
  const skillCategories = getSkillCategories();
  const currentDate = new Date().toISOString().split("T")[0];

  // Format skill categories for display
  const skillCategoriesText = skillCategories.map((cat) => `  - **${cat}/**`).join("\n");

  return `# DxMessaging

> Type-safe, synchronous event bus and messaging system for Unity

## Overview

DxMessaging is a high-performance messaging library for Unity (v2021.3+) that replaces traditional C# events and UnityEvents with a type-safe, lifecycle-managed communication pattern. It enables decoupled communication between game systems without direct references.

**Version:** ${pkg.version}
**License:** MIT
**Repository:** https://github.com/Ambiguous-Interactive/DxMessaging
**Documentation:** https://ambiguous-interactive.github.io/DxMessaging/

## Quick Facts

- **Language:** C# (.NET Standard 2.0)
- **Platform:** Unity 2021.3+
- **Package Manager:** OpenUPM, npm, Unity Package Manager
- **Tests:** NUnit + Unity Test Framework (PascalCase method names, no underscores)
- **Documentation:** MkDocs Material

## Key Features

- **Three Message Types:** Untargeted (PA System), Targeted (Commands), Broadcast (Observable Facts)
- **Automatic Lifecycle Management:** No manual unsubscribe needed, prevents memory leaks
- **Zero Coupling:** Systems communicate without direct references
- **Inspector Diagnostics:** Built-in Unity Editor tools showing message flow, timestamps, and payloads
- **Priority-based Execution:** Control message handler ordering
- **Interceptor Pipeline:** Validate and normalize messages before handlers execute
- **DI Framework Support:** Integrations for Zenject, VContainer, and Reflex
- **Source Generators:** Auto-constructor generation for messages
- **Low Allocation Design:** Struct-based with minimal GC pressure
- **Local Bus Islands:** Isolated testing with zero global state

## Core Concepts

### Message Types

1. **Untargeted Messages** - Global announcements (like a PA system)
   - No specific target
   - Anyone can listen
   - Example: Game settings changed

1. **Targeted Messages** - Commands to specific entities
   - Has a specific GameObject/Component target
   - Only target and its children listen
   - Example: Heal this specific character

1. **Broadcast Messages** - Observable facts from a source
   - Has a source GameObject/Component
   - Anyone can observe what happened
   - Example: This enemy took damage

### Message Flow

\`\`\`text
Emitter > MessageBus > Interceptors > Handlers (by priority)
\`\`\`

## Project Structure

\`\`\`text
/Runtime/Core/          Core messaging engine (MessageBus, Messages, Attributes)
/Runtime/Unity/         Unity integration (Components, DI support)
/Editor/                Inspector tools, analyzers, custom editors
/SourceGenerators/      C# Source Generators for auto-constructors
/Tests/Runtime/         NUnit tests
/Samples~/              Example projects (Mini Combat, DI, Inspector)
/docs/                  MkDocs documentation site
\`\`\`

## Getting Started

### Installation (OpenUPM - Recommended)

\`\`\`bash
openupm add ${pkg.name}
\`\`\`

### Basic Usage

\`\`\`csharp
// Define a message
public readonly struct PlayerHealthChanged
{
    public readonly float newHealth;
    
    [DxAutoConstructor] // Auto-generates constructor
    public PlayerHealthChanged() { }
}

// Send a message
MessageBus.Emit(new PlayerHealthChanged(75f));

// Listen for messages
MessageBus.Register<PlayerHealthChanged>(msg => {
    Debug.Log($"Health changed to {msg.newHealth}");
});
\`\`\`

### Unity Component Integration

\`\`\`csharp
public class HealthDisplay : MessageAwareComponent
{
    void OnEnable()
    {
        Register<PlayerHealthChanged>(OnHealthChanged);
    }
    
    void OnHealthChanged(PlayerHealthChanged msg)
    {
        // Automatically unregistered when component is disabled/destroyed
    }
}
\`\`\`

## Documentation Structure

### Getting Started

- [Overview](https://ambiguous-interactive.github.io/DxMessaging/getting-started/overview/)
- [Installation](https://ambiguous-interactive.github.io/DxMessaging/getting-started/install/)
- [Quick Start](https://ambiguous-interactive.github.io/DxMessaging/getting-started/quick-start/)
- [Visual Guide](https://ambiguous-interactive.github.io/DxMessaging/getting-started/visual-guide/)

### Concepts

- [Mental Model](https://ambiguous-interactive.github.io/DxMessaging/concepts/mental-model/) - Core philosophy and design principles
- [Message Types](https://ambiguous-interactive.github.io/DxMessaging/concepts/message-types/) - Untargeted, Targeted, Broadcast
- [Listening Patterns](https://ambiguous-interactive.github.io/DxMessaging/concepts/listening-patterns/)
- [Targeting & Context](https://ambiguous-interactive.github.io/DxMessaging/concepts/targeting-and-context/)
- [Interceptors & Ordering](https://ambiguous-interactive.github.io/DxMessaging/concepts/interceptors-and-ordering/)

### Guides

- [Patterns](https://ambiguous-interactive.github.io/DxMessaging/guides/patterns/) - Best practices and common patterns
- [Unity Integration](https://ambiguous-interactive.github.io/DxMessaging/guides/unity-integration/)
- [Testing](https://ambiguous-interactive.github.io/DxMessaging/guides/testing/) - Testing strategies for message-based systems
- [Diagnostics](https://ambiguous-interactive.github.io/DxMessaging/guides/diagnostics/) - Inspector tools and debugging
- [Memory Reclamation](https://ambiguous-interactive.github.io/DxMessaging/guides/memory-reclamation/) - Idle eviction, Trim API, occupancy counters
- [Migration Guide](https://ambiguous-interactive.github.io/DxMessaging/guides/migration-guide/)

### Architecture

- [Design & Architecture](https://ambiguous-interactive.github.io/DxMessaging/architecture/design-and-architecture/)
- [Performance](https://ambiguous-interactive.github.io/DxMessaging/architecture/performance/) - Benchmarks (10-17M ops/sec)
- [Comparisons](https://ambiguous-interactive.github.io/DxMessaging/architecture/comparisons/) - vs Events, UnityEvents, other buses

### Advanced Topics

- [Emit Shorthands](https://ambiguous-interactive.github.io/DxMessaging/advanced/emit-shorthands/)
- [Message Bus Providers](https://ambiguous-interactive.github.io/DxMessaging/advanced/message-bus-providers/)
- [Registration Builders](https://ambiguous-interactive.github.io/DxMessaging/advanced/registration-builders/)
- [Runtime Configuration](https://ambiguous-interactive.github.io/DxMessaging/advanced/runtime-configuration/)

### Integrations

- [Zenject](https://ambiguous-interactive.github.io/DxMessaging/integrations/zenject/) - Extenject/Zenject DI integration
- [VContainer](https://ambiguous-interactive.github.io/DxMessaging/integrations/vcontainer/) - VContainer DI integration
- [Reflex](https://ambiguous-interactive.github.io/DxMessaging/integrations/reflex/) - Reflex DI integration

### Reference

- [Quick Reference](https://ambiguous-interactive.github.io/DxMessaging/reference/quick-reference/)
- [Runtime Settings](https://ambiguous-interactive.github.io/DxMessaging/reference/runtime-settings/) - DxMessagingRuntimeSettings asset and diagnostic API
- [FAQ](https://ambiguous-interactive.github.io/DxMessaging/reference/faq/)
- [Glossary](https://ambiguous-interactive.github.io/DxMessaging/reference/glossary/)
- [Troubleshooting](https://ambiguous-interactive.github.io/DxMessaging/reference/troubleshooting/)

## Key Files

- [README.md](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/README.md) - 30-second pitch, mental models, quick start
- [CHANGELOG.md](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/CHANGELOG.md) - Version history
- [CONTRIBUTING.md](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/CONTRIBUTING.md) - Contribution guidelines
- [package.json](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/package.json) - Package manifest
- [.llm/context.md](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.llm/context.md) - Repository guidelines for AI agents

## Development

### Build & Test Commands

\`\`\`bash
# Format code
dotnet tool restore
dotnet tool run csharpier .

# Build source generators
dotnet build SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.csproj

# Run tests (Unity Test Runner)
# Open Unity 2021.3+ project > Window > Test Runner > PlayMode

# Format markdown / JSON / YAML
npm run format

# Lint markdown
npm run lint:markdown

# Spell check
npm run check:spelling
\`\`\`

### Project Standards

- **Code Style:** 4-space indent, explicit types (no \`var\`), PascalCase for public APIs
- **Line Endings:** LF by default, CRLF for C#/.NET project files
- **Tests:** NUnit + Unity Test Framework, no underscores in method names (enforced by pre-commit fixer)
- **Documentation:** MkDocs Material, lazy numbering for ordered lists
- **Commits:** Imperative mood, reference issues/PRs

## AI Agent Context

This repository includes comprehensive AI agent guidance in the \`.llm/\` directory:

- **[.llm/context.md](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.llm/context.md)** - Repository guidelines, coding standards, testing policies
- **[.llm/skills/](https://github.com/Ambiguous-Interactive/DxMessaging/tree/master/.llm/skills)** - ${skillCount}+ specialized skill documents covering:
${skillCategoriesText}

## Common Pitfalls & Solutions

### Memory Leaks

**Problem:** Forgot to unsubscribe from events
**Solution:** Use \`MessageAwareComponent\` or \`MessageHandler\` for automatic lifecycle management

### Message Not Received

**Problem:** Handler registered after message was emitted
**Solution:** Messages are synchronous; ensure registration happens during \`Awake\`/\`OnEnable\`

### Wrong Message Type

**Problem:** Used Broadcast when Targeted was needed
**Solution:** See [Mental Model](https://ambiguous-interactive.github.io/DxMessaging/concepts/mental-model/) for type selection guidance

### Performance Issues

**Problem:** Too many handlers or heavy interceptors
**Solution:** Use priority ordering, profile with Inspector diagnostics

## Performance Characteristics

- **Message Emit:** 10-17M operations/second (OS-specific)
- **Memory:** Low allocation, struct-based design
- **Handler Invocation:** Direct calls, no reflection
- **Registration:** O(1) add/remove with backing dictionary
- **Priority Ordering:** Stable sort on registration

See [Performance Documentation](https://ambiguous-interactive.github.io/DxMessaging/architecture/performance/) for detailed benchmarks.

## Examples

### Mini Combat Sample

Demonstrates all three message types in a simple combat scenario:

- **Untargeted:** Game settings changes
- **Targeted:** Heal specific character
- **Broadcast:** Enemy takes damage

**Location:** \`Samples~/Mini Combat\`

### DI Integration Sample

Shows integration with Zenject, VContainer, and Reflex:

- Scoped message buses
- Container lifecycle integration
- IMessageRegistrationBuilder usage

**Location:** \`Samples~/DI\`

### Inspector Diagnostics Sample

Demonstrates debugging tools:

- Global observer pattern
- Message flow visualization
- Timestamp and payload inspection

**Location:** \`Samples~/UI Buttons + Inspector\`

## Support & Community

- **Issues:** https://github.com/Ambiguous-Interactive/DxMessaging/issues
- **Discussions:** https://github.com/Ambiguous-Interactive/DxMessaging/discussions
- **Email:** wallstop@wallstopstudios.com
- **OpenUPM:** https://openupm.com/packages/com.wallstop-studios.dxmessaging/

## License

MIT License - see [LICENSE.md](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/LICENSE.md)

Copyright (c) 2017-2026 Wallstop Studios

---

**Last Updated:** ${currentDate}
**Generated by:** scripts/update-llms-txt.js using package.json v${pkg.version} and .llm/skills metadata
`;
}

/**
 * Normalize content for comparison by replacing the auto-generated date line
 * with a stable placeholder, normalizing line endings, and trimming
 * whitespace. This allows the --check mode (and tests) to verify content
 * correctness without failing due to the date changing each day.
 */
function normalizeForComparison(str) {
  // Normalize all line endings (CRLF, LF, lone CR) to LF for stable comparison.
  const normalized = normalizeToLf(str);

  return (
    normalized
      // Normalize the Last Updated line by replacing the date with a fixed
      // placeholder, while keeping the marker text so structural differences are
      // still detected.
      .replace(/^\*\*Last Updated:\*\*.*$/m, "**Last Updated:** <DATE>")
      // Normalize the floored skill-count claim. The exact number is validated
      // separately (no-overstatement, see validateSkillCountClaim); keeping it out
      // of the structural diff means adding/removing a skill never trips --check.
      .replace(SKILL_CLAIM_REGEX, "<COUNT>+ specialized skill documents")
      .trim()
  );
}

/**
 * The single definition of "is the committed llms.txt / README state valid?",
 * shared by BOTH --check and update mode's post-write verification. Returns an
 * array of actionable error strings (empty == valid). One shared validator is
 * what guarantees update mode can never exit 0 while leaving a state --check
 * (and the pre-commit hook / CI / the auto-commit bot that run it) would reject.
 *
 * @param {{
 *   newContent: string,
 *   llmsTxtPath: string,
 *   claimFiles: { label: string, filePath: string }[],
 *   actualCount: number
 * }} params
 * @returns {string[]} validation errors in reporting order
 */
function collectValidationErrors({ newContent, llmsTxtPath, claimFiles, actualCount }) {
  const errors = [];

  if (!fs.existsSync(llmsTxtPath)) {
    errors.push("llms.txt does not exist. Run: node scripts/update-llms-txt.js");
    return errors;
  }

  const currentContent = fs.readFileSync(llmsTxtPath, "utf8");

  // 1. Structural freshness: everything except the date and the floored
  //    skill-count (both normalized away) must match a fresh generation.
  if (!hasValidLastUpdatedLine(currentContent) || !hasValidLastUpdatedLine(newContent)) {
    errors.push(
      "llms.txt is missing or has an invalid '**Last Updated:**' line (expected ISO date YYYY-MM-DD)"
    );
  }

  const expected = normalizeForComparison(currentContent);
  const generated = normalizeForComparison(newContent);
  if (expected !== generated) {
    errors.push(
      "llms.txt is out of date (structure differs from a fresh generation):\n" +
        summarizeDrift(expected, generated) +
        "\n  Run: node scripts/update-llms-txt.js"
    );
  }

  // 2. No-overstatement: every doc that advertises a skill count must claim no
  //    more than the real number AND carry exactly one claim. Understating is
  //    allowed (conservative), so adding skills never fails CI; a false
  //    (overstated) or a missing/duplicated claim does.
  for (const { label, filePath } of claimFiles) {
    if (!fs.existsSync(filePath)) {
      continue;
    }
    const result = validateSkillCountClaim(fs.readFileSync(filePath, "utf8"), actualCount, label);
    if (!result.ok) {
      errors.push(result.reason);
    }
  }

  return errors;
}

function main() {
  const checkMode = process.argv.slice(2).includes("--check");
  const claimFiles = guardedClaimFiles();
  const llmsTxtPath = claimFiles.find((doc) => doc.label === "llms.txt").filePath;

  try {
    const newContent = generateLlmsTxt();
    const actualCount = countSkillFiles();

    if (checkMode) {
      const errors = collectValidationErrors({ newContent, llmsTxtPath, claimFiles, actualCount });
      if (errors.length > 0) {
        errors.forEach((error) => console.error(`ERROR: ${error}`));
        process.exit(1);
      }
      console.log(`[ok] llms.txt is up to date (${actualCount} skill documents)`);
      return;
    }

    // Update mode: write llms.txt (LF endings, matching .gitattributes for
    // *.txt), then keep every other doc that advertises the skill count in sync
    // so the single documented remediation fixes the whole class of drift.
    fs.writeFileSync(llmsTxtPath, normalizeToLf(newContent), "utf8");
    console.log("[ok] Updated llms.txt");

    for (const { label, filePath } of claimFiles) {
      if (filePath === llmsTxtPath) {
        continue;
      }
      if (syncSkillCountClaim(filePath, actualCount)) {
        console.log(`[ok] Synced skill count in ${label} to ${actualCount}+`);
      }
    }

    // Verify-after-write: update must leave a state --check accepts, or fail
    // loudly. syncSkillCountClaim deliberately refuses to touch a doc whose claim
    // is missing or duplicated, so such a doc needs a human. Re-running the SAME
    // validator --check uses -- against the post-write state, not the fixer's
    // boolean self-report -- is what distinguishes "no-op because already correct"
    // (pass) from "no-op because unfixable" (fail). Without it the fixer could
    // report success while pre-commit / CI / the auto-commit bot reject what it
    // left behind. newContent is what we just wrote, so the structural checks pass
    // by construction; this only catches a sibling doc's malformed claim.
    const errors = collectValidationErrors({ newContent, llmsTxtPath, claimFiles, actualCount });
    if (errors.length > 0) {
      console.error("ERROR: a skill-count claim is invalid and update cannot auto-fix it.");
      console.error("Fix the file(s) below to carry exactly one claim, then re-run update:");
      errors.forEach((error) => console.error(`  - ${error}`));
      process.exit(1);
    }

    return;
  } catch (error) {
    console.error("ERROR:", error.message);
    process.exit(1);
  }
}

// Only run if executed directly (not required as module)
if (require.main === module) {
  main();
}

module.exports = {
  generateLlmsTxt,
  countSkillFiles,
  getSkillCategories,
  hasValidLastUpdatedLine,
  normalizeForComparison,
  parseSkillCountClaims,
  validateSkillCountClaim,
  syncSkillCountClaim,
  summarizeDrift,
  collectValidationErrors
};
