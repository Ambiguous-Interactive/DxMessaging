---
title: "Markdown Compatibility Guidelines"
id: "markdown-compatibility"
category: "documentation"
version: "1.3.0"
created: "2026-01-29"
updated: "2026-03-17"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "docs/"
    - path: "README.md"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "documentation"
  - "markdown"
  - "compatibility"
  - "mkdocs"
  - "portability"

complexity:
  level: "basic"
  reasoning: "Simple syntax substitutions for cross-platform compatibility"

impact:
  performance:
    rating: "none"
    details: "Documentation only"
  maintainability:
    rating: "high"
    details: "Ensures docs render correctly in all viewers without maintenance burden"
  testability:
    rating: "none"
    details: "Documentation does not affect testability"

prerequisites:
  - "Understanding of Markdown"

dependencies:
  packages: []
  skills:
    - "documentation-style-guide"
    - "documentation-updates"

applies_to:
  languages:
    - "Markdown"
  frameworks:
    - "MkDocs"
    - "GitHub"
    - "VS Code"

aliases:
  - "Portable markdown"
  - "Cross-platform docs"
  - "MkDocs alternatives"

related:
  - "documentation-style-guide"
  - "documentation-updates"
  - "mkdocs-navigation"
  - "mermaid-theming"

status: "stable"
---

# Markdown Compatibility Guidelines

> **One-line summary**: Use only standard markdown syntax that renders correctly in GitHub, VS Code, and MkDocs simultaneously.

## Overview

This project's documentation must render correctly across multiple platforms:

- **GitHub's markdown viewer** (README, docs in repo)
- **VS Code markdown preview** (local development)
- **Standard markdown renderers** (third-party tools, npm packages)
- **MkDocs** (docs site build)

Using MkDocs-specific syntax breaks rendering in standard viewers, creating a poor experience for users who browse documentation on GitHub or in their editor.

## Problem Statement

MkDocs and its Material theme provide powerful extensions like admonitions, tabs, and buttons. However, these extensions use non-standard syntax that renders as broken text in standard markdown viewers:

```markdown
!!! warning "Caution"
This will not render correctly on GitHub.
```

On GitHub, this displays as literal text: `!!! warning "Caution"` followed by an indented paragraph -- not as a styled warning box.

## Solution

Use only standard markdown syntax with Unicode emoji for visual emphasis. This ensures consistent rendering across all platforms.

---

## Nested Fenced Code Blocks

When documenting markdown that contains code blocks, the **outer fence must have MORE backticks than any inner fence**. CommonMark requires opening and closing sequences to match exactly, so use 4+ backticks for outer fences when inner uses 3:

`````markdown
````markdown
This outer fence uses 4 backticks.

```python
print("Hello")
```
````
`````

For deeper nesting, keep increasing: outer uses 5, middle uses 4, inner uses 3.

### Common Mistakes

| Mistake             | Problem                              | Fix                          |
| ------------------- | ------------------------------------ | ---------------------------- |
| Same backtick count | Inner fence closes outer prematurely | More backticks on outer      |
| Spaces in fence     | ` ``` ` may not parse                | spaces in backtick sequence  |
| Mismatched closing  | Opening with 4, closing with 3       | Count must match exactly     |
| Missing closing     | Following headings render as code    | Always close the outer fence |

### Guardrail: Keep Real Sections Outside Fenced Examples

If a heading like `## See Also` belongs to the actual document structure, do not place it inside a fenced markdown example. A useful check is: if removing the fence would change document structure, that heading likely belongs outside the code sample.

---

## See Also

- [markdown compatibility part 1](./markdown-compatibility-part-1.md)
- [markdown compatibility part 2](./markdown-compatibility-part-2.md)
