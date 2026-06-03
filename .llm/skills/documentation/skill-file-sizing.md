---
title: "Skill File Sizing Guidelines"
id: "skill-file-sizing"
category: "documentation"
version: "1.0.0"
created: "2026-01-22"
updated: "2026-01-22"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: ".llm/skills/specification.md"
    - path: "scripts/validate-skills.js"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "documentation"
  - "skills"
  - "file-organization"
  - "maintainability"
  - "llm-context"

complexity:
  level: "basic"
  reasoning: "Straightforward guidelines with clear metrics"

impact:
  performance:
    rating: "none"
    details: "No runtime impact, documentation only"
  maintainability:
    rating: "high"
    details: "Properly sized files are easier to maintain and update"
  testability:
    rating: "low"
    details: "Smaller, focused files are easier to validate"

prerequisites:
  - "Understanding of skill file structure"
  - "Familiarity with specification.md"

dependencies:
  packages: []
  skills:
    - "documentation-updates"

applies_to:
  languages:
    - "Markdown"
  frameworks: []
  versions: {}

aliases:
  - "skill-sizing"
  - "file-length-limits"

related:
  - "documentation-updates"
  - "changelog-management"

status: "stable"
---

# Skill File Sizing Guidelines

> **One-line summary**: Keep skill files between 120-260 lines, split at the warning range, and never exceed 300 lines.

## Overview

Skill files are designed to provide focused, actionable guidance that LLMs can effectively use during code generation and problem-solving. File size directly impacts how well an LLM can understand and apply the skill content. Files that are too long overwhelm context windows and dilute focus; files that are too short lack sufficient detail for proper application.

This skill documents the sizing requirements for skill files and context documents in the `.llm/` directory.

## Problem Statement

Large skill files create several problems:

- **Context window saturation**: LLMs have limited context windows; overly large files consume disproportionate space
- **Reduced focus**: Long files often cover too many concepts, making it harder for LLMs to identify relevant guidance
- **Maintenance burden**: Large files are harder to review, update, and keep accurate
- **Cognitive overload**: Both humans and LLMs struggle with monolithic documentation

Conversely, files that are too short may lack:

- Sufficient examples to demonstrate patterns
- Edge case coverage
- Anti-pattern documentation

## Solution

### Core Concept

Apply strict line count limits with graduated enforcement:

| Range         | Status  | Action                                     |
| ------------- | ------- | ------------------------------------------ |
| < 120 lines   | Short   | Consider adding more examples or detail    |
| 120-260 lines | Ideal   | Target range for skill files               |
| 261-300 lines | Warning | Consider splitting into focused sub-skills |
| > 300 lines   | Error   | Must split; blocks CI/pre-commit           |

### Implementation

Line count validation is automated via `scripts/validate-skills.js`:

```javascript
const LINE_LIMIT_IDEAL_MIN = 120;
const LINE_LIMIT_IDEAL_MAX = 260;
const LINE_LIMIT_HARD_MAX = 300;
```

Run validation manually:

```bash
npm run repair:llm-policy
npm run validate:llm-policy
```

### Enforcement Points

1. **Pre-commit hook**: Warns on files exceeding 260 lines, blocks on 300+
1. **CI/CD pipeline**: Fails PRs that introduce files exceeding 300 lines
1. **Index generation**: Reports line counts in `index.md`

## Variations

### Variation A: Context Files

The main `context.md` file follows the same limits. If it grows too large, extract detailed guidance into category-specific skill files.

> **Note**: `context.md` is excluded from "short file" informational messages since it serves as a high-level summary document that intentionally references other skill files rather than containing exhaustive detail itself.

### Variation B: Specification Files

`specification.md` and `index.md` are exempt from validation but should still aim for reasonable lengths.

## Usage Examples

### Example 1: Splitting a Large Skill

If `object-pooling.md` exceeds the 260-line warning range:

**Before** (one large file):

```text
.llm/skills/performance/
+-- object-pooling.md (450 lines)
```

**After** (split by variation):

```text
.llm/skills/performance/
+-- object-pooling.md (180 lines - core concept)
+-- array-pooling.md (200 lines - array-specific)
+-- collection-pooling.md (220 lines - collections)
+-- stringbuilder-pooling.md (190 lines - StringBuilder)
```

### Example 2: Organizing Related Skills

Group related skills under a common category with cross-references:

```markdown
## See Also

- [skill file sizing part 1](./skill-file-sizing-part-1.md)
- [Array Pooling](./array-pooling.md) - Specialized pooling for arrays
- [Collection Pooling](./collection-pooling.md) - List and Dictionary pooling
```

## Anti-Patterns

### Kitchen Sink Skills

```markdown
# Everything About Performance

This skill covers pooling, caching, inlining, struct optimization,
memory alignment, SIMD, async patterns, threading...
```

**Why it's wrong**: Covers too many unrelated concepts. Split into focused skills.

### Overly Terse Skills

```markdown
# Object Pooling

Use pools. Here's how:

`ObjectPool<T>.Get()` / `ObjectPool<T>.Return()`

Done.
```

**Why it's wrong**: Lacks examples, context, anti-patterns, and edge cases.

### Excessive Code Duplication

Including the same example code in multiple variations within one file. Extract to a referenced utility or create separate skill files.

## Testing Considerations

Validation is automated:

```bash
# Run validation
npm run repair:llm-policy
npm run validate:llm-policy

# Check output for size warnings/errors
#   size: File has 278 lines (ideal: 120-260)
#  size: File has 317 lines (max: 300)
```

## When to Split a Skill

Consider splitting when:

1. **Line count exceeds 260**: Split before the hard 300-line ceiling
1. **Multiple distinct variations**: Each variation could stand alone
1. **Different complexity levels**: Basic vs. advanced usage
1. **Different use cases**: Unity-specific vs. general .NET
1. **Tangential content**: Testing guidance, migration guides

## File Organization Strategy

```text
.llm/skills/{category}/
+-- {main-concept}.md           # Core pattern (120-260 lines)
+-- {concept}-{variation1}.md   # Specific variation
+-- {concept}-{variation2}.md   # Another variation
+-- {concept}-advanced.md       # Advanced usage
```

Cross-reference using the `related` frontmatter field and `## See Also` sections.

## Performance Notes

File size limits improve:

- **LLM response quality**: Focused context leads to better suggestions
- **Index generation speed**: Smaller files parse faster
- **PR review efficiency**: Reviewers can assess changes quickly

## Related Links

- [Documentation Updates](./documentation-updates.md)
- [Changelog Management](./changelog-management.md)
- [Skill Specification](../specification.md)

## References

- [Skill Specification](../specification.md)
- Validation script: [scripts/validate-skills.js](../../../scripts/validate-skills.js)
- Pre-commit config: [.pre-commit-config.yaml](../../../.pre-commit-config.yaml)
