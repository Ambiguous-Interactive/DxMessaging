# Skills Repository Specification

This document defines the structure, schema, and tooling for storing code patterns (skills) extracted from repositories.

---

## 1. Directory Structure

```text
.llm/
+-- skills/
|   +-- specification.md          # This file - the spec
|   +-- index.md                  # Auto-generated index of all skills
|   +-- templates/                # Skill templates
|   |   +-- skill-template.md
|   |
|   +-- performance/              # Performance optimization patterns
|   |   +-- object-pooling.md
|   |   +-- cache-strategies.md
|   |   +-- allocation-reduction.md
|   |
|   +-- testing/                  # Testing patterns and practices
|   |   +-- unity-test-patterns.md
|   |   +-- mock-strategies.md
|   |   +-- assertion-patterns.md
|   |
|   +-- solid/                    # SOLID principles implementations
|   |   +-- dependency-injection.md
|   |   +-- interface-segregation.md
|   |   +-- single-responsibility.md
|   |
|   +-- messaging/                # Messaging and event patterns
|   |   +-- pub-sub-patterns.md
|   |   +-- message-routing.md
|   |   +-- broadcast-strategies.md
|   |
|   +-- unity/                    # Unity-specific patterns
|   |   +-- lifecycle-management.md
|   |   +-- component-patterns.md
|   |   +-- editor-extensions.md
|   |
|   +-- concurrency/              # Threading and async patterns
|   |   +-- thread-safety.md
|   |   +-- lock-free-patterns.md
|   |   +-- async-patterns.md
|   |
|   +-- architecture/             # Architectural patterns
|   |   +-- service-locator.md
|   |   +-- factory-patterns.md
|   |   +-- repository-pattern.md
|   |
|   +-- error-handling/           # Error handling strategies
|   |   +-- exception-patterns.md
|   |   +-- result-types.md
|   |   +-- defensive-coding.md
|   |
|   +-- code-generation/          # Source generation patterns
|   |   +-- roslyn-analyzers.md
|   |   +-- source-generators.md
|   |   +-- emit-patterns.md
|   |
|   +-- scripting/                # Shell and script patterns
|   |   +-- powershell-best-practices.md
|   |   +-- shell-patterns.md
|   |
|   +-- github-actions/           # GitHub Actions workflow patterns
|   |   +-- workflow-consistency.md
|   |
|   +-- documentation/            # Documentation and code comments
|       +-- documentation-updates.md
```

---

## 2. YAML Frontmatter Schema

Every skill file MUST include the following YAML frontmatter:

```yaml
---
# Required Fields
title: "Human-readable skill title"
id: "unique-kebab-case-identifier"
category: "performance|testing|solid|messaging|unity|concurrency|architecture|error-handling|code-generation|documentation|scripting|github-actions"
version: "1.0.0"
created: "2026-01-21"
updated: "2026-01-21"

# Source Information
source:
  repository: "owner/repo-name"
  files:
    - path: "relative/path/to/file.cs"
      lines: "10-50" # Optional: specific line range
      commit: "abc123" # Optional: specific commit hash
    - path: "another/file.cs"
  url: "https://github.com/owner/repo" # Optional: direct URL

# Classification
tags:
  - "primary-tag"
  - "secondary-tag"
  - "technology-tag"

complexity:
  level: "basic|intermediate|advanced|expert"
  reasoning: "Brief explanation of complexity rating"

# Impact Assessment
impact:
  performance:
    rating: "none|low|medium|high|critical"
    details: "Brief description of performance implications"
  maintainability:
    rating: "none|low|medium|high|critical"
    details: "Brief description of maintainability implications"
  testability:
    rating: "none|low|medium|high|critical"
    details: "Brief description of testability implications"

# Dependencies and Prerequisites
prerequisites:
  - "Required knowledge or skill"
  - "Another prerequisite"

dependencies:
  packages:
    - "Package.Name >= 1.0.0"
  skills:
    - "related-skill-id"

# Applicability
applies_to:
  languages:
    - "C#"
    - "JavaScript"
  frameworks:
    - "Unity"
    - ".NET"
  versions:
    unity: ">=2021.3"
    dotnet: ">=netstandard2.0"

# Optional metadata
aliases:
  - "Alternative name 1"
  - "Alternative name 2"

related:
  - "related-skill-id-1"
  - "related-skill-id-2"

status: "draft|review|stable|deprecated"
---
```

### Field Definitions

| Field                           | Type   | Required | Description                                                          |
| ------------------------------- | ------ | -------- | -------------------------------------------------------------------- |
| `title`                         | string | Yes      | Human-readable title for the skill                                   |
| `id`                            | string | Yes      | Unique kebab-case identifier (must match filename without extension) |
| `category`                      | enum   | Yes      | Primary category (must match parent folder name)                     |
| `version`                       | semver | Yes      | Semantic version of this skill document                              |
| `created`                       | date   | Yes      | ISO 8601 date when skill was first documented                        |
| `updated`                       | date   | Yes      | ISO 8601 date when skill was last modified                           |
| `source.repository`             | string | Yes      | Source repository in `owner/repo` format                             |
| `source.files`                  | array  | Yes      | Array of source file references                                      |
| `source.files[].path`           | string | Yes      | Relative path within source repository                               |
| `source.files[].lines`          | string | No       | Line range in format `start-end`                                     |
| `source.files[].commit`         | string | No       | Git commit SHA for version pinning                                   |
| `source.url`                    | string | No       | Direct URL to repository or file                                     |
| `tags`                          | array  | Yes      | Array of descriptive tags for discovery                              |
| `complexity.level`              | enum   | Yes      | One of: basic, intermediate, advanced, expert                        |
| `complexity.reasoning`          | string | No       | Explanation of complexity rating                                     |
| `impact.performance.rating`     | enum   | Yes      | Performance impact rating                                            |
| `impact.performance.details`    | string | No       | Performance impact explanation                                       |
| `impact.maintainability.rating` | enum   | Yes      | Maintainability impact rating                                        |
| `impact.testability.rating`     | enum   | Yes      | Testability impact rating                                            |
| `prerequisites`                 | array  | No       | Required knowledge or skills                                         |
| `dependencies.packages`         | array  | No       | Required NuGet/npm packages                                          |
| `dependencies.skills`           | array  | No       | Related skill IDs that should be learned first                       |
| `applies_to.languages`          | array  | Yes      | Programming languages this applies to                                |
| `applies_to.frameworks`         | array  | No       | Frameworks this applies to                                           |
| `applies_to.versions`           | object | No       | Version constraints                                                  |
| `aliases`                       | array  | No       | Alternative names for search                                         |
| `related`                       | array  | No       | IDs of related skills                                                |
| `status`                        | enum   | Yes      | Document status: draft, review, stable, deprecated                   |

---

## 3. Markdown Body Format

After the frontmatter, skills follow this structure:

Each skill file must stay within the repository line-budget policy: target
120-260 lines, treat 261-300 as a split warning, and never exceed 300 lines.
Run `npm run repair:llm-policy` and `npm run validate:llm-policy` before
handoff.

```markdown
---
[YAML Frontmatter]
---

# {title}

> **One-line summary**: Brief description of what this pattern accomplishes.

## Overview

2-4 paragraphs explaining:

- What problem this pattern solves
- When to use it
- Key benefits and trade-offs

## Problem Statement

Describe the specific problem this pattern addresses. Include:

- Common symptoms of the problem
- Why naive solutions don't work
- Real-world scenarios where this matters

## See Also

- [skill-template](./templates/skill-template.md)
- [Skill File Sizing Guidelines](./documentation/skill-file-sizing.md)
```
